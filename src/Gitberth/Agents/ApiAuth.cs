using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Gitberth.Agents;

/// <summary>Who's calling /mcp or /api/v1: a token, by its id, name and access, from an address.</summary>
public sealed record ApiCaller(int TokenId, string Name, ApiAccess Access, AuditVia Via, string? Ip)
{
    public const string AccessClaim = "gitberth.access";
    public const string ViaClaim = "gitberth.via";
    public const string IpClaim = "gitberth.ip";

    /// <summary>The audit log's actor for whatever the token changes: "the token &lt;name&gt;", over MCP or the API.</summary>
    public AuditActor Actor => new(AuditActorKind.ApiToken, Name, Via, Ip);

    public bool May(ApiAccess needed) => Access >= needed;

    /// <summary>The caller a request was let in as (<see cref="ApiTokenHandler"/>), or null when it wasn't one.</summary>
    public static ApiCaller? From(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true, AuthenticationType: ApiTokenHandler.SchemeName } identity) return null;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        if (!Enum.TryParse<ApiAccess>(user.FindFirstValue(AccessClaim), out var access) || !Enum.IsDefined(access)) return null;
        var via = Enum.TryParse<AuditVia>(user.FindFirstValue(ViaClaim), out var v) ? v : AuditVia.Api;
        return new ApiCaller(id, identity.Name ?? "", access, via, user.FindFirstValue(IpClaim));
    }

    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, TokenId.ToString(CultureInfo.InvariantCulture)),
        new Claim(ClaimTypes.Name, Name),
        new Claim(AccessClaim, Access.ToString()),
        new Claim(ViaClaim, Via.ToString()),
        .. Ip is null ? Array.Empty<Claim>() : [new Claim(IpClaim, Ip)],
    ], ApiTokenHandler.SchemeName, ClaimTypes.Name, ClaimTypes.Role));
}

/// <summary>The paths a token works on, and nothing else: the MCP server and the HTTP API.</summary>
public static class AgentPaths
{
    public const string Mcp = "/mcp";
    public const string Api = "/api";
    public const string ApiV1 = "/api/v1";

    public static bool IsMcp(PathString path) => path.StartsWithSegments(Mcp, StringComparison.OrdinalIgnoreCase);

    public static bool IsApi(PathString path) => path.StartsWithSegments(Api, StringComparison.OrdinalIgnoreCase);

    public static bool Contains(PathString path) => IsMcp(path) || IsApi(path);

    /// <summary>
    /// Before authentication, for /mcp and /api only: they answer their own status codes and JSON, never the "not found"
    /// page or the error page (anything that fails is a JSON 500, said in Gitberth's log); and a request with an Origin
    /// header (a browser's: a page on some site, or DNS rebinding of Gitberth's address) is refused with 403, as the MCP
    /// specification asks. MCP clients and scripts send none.
    /// </summary>
    public static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (!Contains(context.Request.Path))
        {
            await next(context);
            return;
        }
        if (context.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers.Origin.Count > 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Requests from a browser page (with an Origin header) aren't accepted here. Call it from an MCP client, a script or curl.");
            return;
        }
        try
        {
            await next(context);
        }
        // A caller that hung up isn't answered; anything else that fails before the answer started is said as JSON.
        catch (Exception ex) when (!(ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested) && !context.Response.HasStarted)
        {
            context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Gitberth.Agents").LogError(ex, "A request to {Path} failed", context.Request.Path);
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "Gitberth couldn't answer that: something went wrong on its side, and its log says what.");
        }
    }

    /// <summary>After authorization: whatever the request changes is the token's in the audit log.</summary>
    public static async Task ActorMiddleware(HttpContext context, RequestDelegate next)
    {
        if (Contains(context.Request.Path) && ApiCaller.From(context.User) is { } caller)
        {
            using (AuditScope.Begin(caller.Actor))
                await next(context);
            return;
        }
        await next(context);
    }

    /// <summary>{"error": "…"} with the status code.</summary>
    public static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(new JsonObject { ["error"] = message }.ToJsonString(), context.RequestAborted);
    }
}

/// <summary>
/// Failed token attempts by address: after <see cref="MaxFailures"/> in <see cref="Window"/>, further failures get 429
/// rather than 401 (a good token always gets in: 32 random bytes can't be guessed, and an agent on the same machine as
/// a misconfigured one keeps working). A refusal is recorded in the audit log at most once a minute per address.
/// </summary>
public sealed class ApiTokenThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AuditEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart)> _state = new();
    private readonly ConcurrentDictionary<string, DateTime> _audited = new();

    public bool Blocked(string client, DateTime now) =>
        _state.TryGetValue(client, out var s) && s.Failures >= MaxFailures && now - s.WindowStart < Window;

    public void Failed(string client, DateTime now)
    {
        _state.AddOrUpdate(client, _ => (1, now), (_, s) => now - s.WindowStart >= Window ? (1, now) : (s.Failures + 1, s.WindowStart));
        // Kept small: addresses whose window is over are forgotten.
        if (_state.Count > 10_000)
            foreach (var (key, value) in _state)
                if (now - value.WindowStart >= Window) _state.TryRemove(key, out _);
    }

    /// <summary>True when a refusal from <paramref name="client"/> should be recorded now (none in the last minute).</summary>
    public bool ShouldAudit(string client, DateTime now)
    {
        if (_audited.TryGetValue(client, out var at) && now - at < AuditEvery) return false;
        _audited[client] = now;
        if (_audited.Count > 10_000)
            foreach (var (key, value) in _audited)
                if (now - value >= AuditEvery) _audited.TryRemove(key, out _);
        return true;
    }
}

/// <summary>
/// Which refused tool calls go in the audit log: at most one a minute per token and tool, so an agent that keeps trying
/// one it may not call can't flood the log. The next entry says how many refusals in between weren't recorded one by one.
/// </summary>
public sealed class ToolDenials
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<(int Token, string Tool), (DateTime At, int Unrecorded)> _last = [];

    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// Null when this refusal isn't recorded now (it's counted for the next entry); otherwise how many refusals of the same
    /// tool to the same token since the last entry weren't recorded.
    /// </summary>
    public int? ShouldRecord(int tokenId, string tool)
    {
        var now = Now();
        lock (_gate)
        {
            var key = (tokenId, tool);
            var found = _last.TryGetValue(key, out var last);
            if (found && now - last.At < Every)
            {
                _last[key] = (last.At, last.Unrecorded + 1);
                return null;
            }
            _last[key] = (now, 0);
            // Kept small: what was recorded a while ago with nothing since is forgotten.
            if (_last.Count > 10_000)
                foreach (var stale in _last.Where(e => now - e.Value.At >= Every && e.Value.Unrecorded == 0).Select(e => e.Key).ToList())
                    _last.Remove(stale);
            return found ? last.Unrecorded : 0;
        }
    }
}

/// <summary>The "ApiToken" scheme's options (none beyond the defaults).</summary>
public sealed class ApiTokenOptions : AuthenticationSchemeOptions;

/// <summary>
/// The "ApiToken" authentication scheme: <c>Authorization: Bearer gbt_…</c>, on /mcp and /api only (its policy is the only
/// one that names it, and the cookie, the default scheme, isn't in that policy). A missing, unknown, revoked or expired token
/// gets 401 with <c>WWW-Authenticate: Bearer</c> and a JSON body that says why; refusals are throttled and audited per address.
/// </summary>
public sealed class ApiTokenHandler(IOptionsMonitor<ApiTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ApiTokens tokens, ApiTokenThrottle throttle, AuditLog audit)
    : AuthenticationHandler<ApiTokenOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string Policy = "ApiToken";

    private const string RefusalKey = "gitberth.tokenRefusal";
    private const string RefusalKindKey = "gitberth.tokenRefusalKind";
    private const string ThrottledKey = "gitberth.tokenThrottled";

    /// <summary>
    /// The challenge for a refusal, as RFC 6750 has it: plain ASCII and the same for everyone, never the token's name or the
    /// body's sentence (Kestrel refuses anything else in a header). A request without a bearer token (none, or another
    /// scheme such as Basic) gets no error code.
    /// </summary>
    public static string Challenge(TokenRefusal kind) => kind switch
    {
        TokenRefusal.Malformed => Invalid("The access token is malformed"),
        TokenRefusal.Unknown => Invalid("The access token is not known"),
        TokenRefusal.Revoked => Invalid("The access token was revoked"),
        TokenRefusal.Expired => Invalid("The access token expired"),
        _ => "Bearer realm=\"Gitberth\"",
    };

    private static string Invalid(string description) => $"Bearer realm=\"Gitberth\", error=\"invalid_token\", error_description=\"{description}\"";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            Context.Items[RefusalKey] = "Send a token: Authorization: Bearer gbt_… (make one in Settings › AI agents and API).";
            Context.Items[RefusalKindKey] = TokenRefusal.Missing;
            return AuthenticateResult.NoResult();
        }
        var ip = Audit.AuditCircuitHandler.ClientIp(Context);
        var client = ip ?? "unknown";
        var now = DateTime.UtcNow;
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var check = token is null
            ? new TokenCheck(null, "Send the token as Authorization: Bearer gbt_….", TokenRefusal.NotBearer)
            : await tokens.CheckAsync(token, Context.RequestAborted);
        if (check.Token is { } row)
        {
            try
            {
                await tokens.UsedAsync(row.Id, ip, Context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "Couldn't note the use of the token {Token}", row.Name);
            }
            var via = AgentPaths.IsMcp(Request.Path) ? AuditVia.Mcp : AuditVia.Api;
            var caller = new ApiCaller(row.Id, row.Name, row.Access, via, ip);
            return AuthenticateResult.Success(new AuthenticationTicket(caller.ToPrincipal(), SchemeName));
        }
        var refusal = check.Refusal ?? "The token isn't accepted.";
        Context.Items[RefusalKey] = refusal;
        Context.Items[RefusalKindKey] = check.Kind == TokenRefusal.None ? TokenRefusal.Unknown : check.Kind;
        if (throttle.Blocked(client, now)) Context.Items[ThrottledKey] = true;
        throttle.Failed(client, now);
        if (throttle.ShouldAudit(client, now))
            audit.Record(new AuditEntry("auth.token.refused", $"Refused a request to {Request.Path} from {client}: {refusal.TrimEnd('.')}.")
            {
                Actor = new AuditActor(AuditActorKind.ApiToken, "unknown token", AgentPaths.IsMcp(Request.Path) ? AuditVia.Mcp : AuditVia.Api, ip),
                Outcome = AuditOutcome.Denied,
                Target = Request.Path.Value,
            });
        return AuthenticateResult.Fail(refusal);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(ThrottledKey))
        {
            Response.Headers.RetryAfter = ((int)ApiTokenThrottle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            await AgentPaths.WriteErrorAsync(Context, StatusCodes.Status429TooManyRequests, "Too many requests with a bad token from this address. Try again in a few minutes.");
            return;
        }
        var refusal = Context.Items[RefusalKey] as string ?? "Send a token: Authorization: Bearer gbt_….";
        // The header says which kind of refusal in fixed ASCII words; the body says it all, in a sentence.
        Response.Headers.WWWAuthenticate = Challenge(Context.Items[RefusalKindKey] is TokenRefusal kind ? kind : TokenRefusal.Missing);
        await AgentPaths.WriteErrorAsync(Context, StatusCodes.Status401Unauthorized, refusal);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        AgentPaths.WriteErrorAsync(Context, StatusCodes.Status403Forbidden, "This token can't do that.");
}
