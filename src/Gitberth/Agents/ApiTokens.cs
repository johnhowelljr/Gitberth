using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Agents;

/// <summary>A token as Settings › AI agents and API lists it: never the token itself, only its first characters.</summary>
public sealed record TokenView(int Id, string Name, ApiAccess Access, string Prefix, DateTime CreatedAt, DateTime? ExpiresAt,
    DateTime? LastUsedAt, string? LastUsedIp, DateTime? RevokedAt)
{
    public bool ExpiredAt(DateTime now) => ExpiresAt is { } at && at <= now;

    /// <summary>Not revoked and not expired: requests with it are let in.</summary>
    public bool ActiveAt(DateTime now) => RevokedAt is null && !ExpiredAt(now);

    /// <summary>"gbt_Ab3dE6gH…": how the list tells tokens apart.</summary>
    public string Shown => ApiTokens.Scheme + Prefix + "…";
}

/// <summary>A new token, shown once (<see cref="Secret"/>), or why none was made.</summary>
public sealed record TokenCreated(TokenView? Token, string? Secret, string? Problem);

/// <summary>Why a request's token wasn't let in, as the 401's challenge header says it (a fixed ASCII word, never the token's name).</summary>
public enum TokenRefusal { None, Missing, NotBearer, Malformed, Unknown, Revoked, Expired }

/// <summary>What a request's bearer token turned out to be: the token it names when it may be used, else why not.</summary>
/// <param name="Refusal">One sentence for the 401's body: unknown, revoked, expired.</param>
/// <param name="Kind">Which refusal it is, for the challenge header.</param>
public sealed record TokenCheck(ApiToken? Token, string? Refusal, TokenRefusal Kind = TokenRefusal.None);

/// <summary>
/// The tokens AI agents (over MCP, /mcp) and scripts (the HTTP API, /api/v1) use instead of signing in. A token is
/// <c>gbt_</c> and 32 random bytes as base64url; only its SHA-256 is kept (with its first 8 characters, to tell tokens
/// apart), so it's shown once, when it's made. Each has a name (what the audit log calls whoever used it), an access level
/// (<see cref="ApiAccess"/>) and maybe an expiry; revoking one ends it at once. Only a signed-in person manages tokens: no
/// token can make, list or revoke one. Made, revoked and expired tokens are recorded in the audit log, by name and access,
/// never the token.
/// </summary>
public sealed class ApiTokens(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, AuditLog? audit = null)
{
    /// <summary>What every token starts with, so it's recognised (and secret scanners can find one that leaked).</summary>
    public const string Scheme = "gbt_";

    /// <summary>The longest name a token has.</summary>
    public const int MaxName = 50;

    /// <summary>The most tokens that aren't revoked at once.</summary>
    public const int MaxActive = 50;

    /// <summary>How many characters after <see cref="Scheme"/> the list shows.</summary>
    public const int PrefixLength = 8;

    /// <summary>The expiries Settings offers, in days (0: never).</summary>
    public static readonly IReadOnlyList<int> ExpiryChoices = [0, 30, 90, 365];

    /// <summary>The ids of expired tokens whose expiry is in the audit log already, as a JSON list.</summary>
    public const string ExpiredNotedKey = "apiTokens.expiredNoted";

    /// <summary>LastUsedAt and LastUsedIp are written at most this often per token.</summary>
    public static readonly TimeSpan UseWriteEvery = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, (DateTime At, string? Ip)> _lastWrite = new();

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>A new token: <c>gbt_</c> and 32 random bytes, base64url without padding (47 characters in all).</summary>
    public static string NewToken() => Scheme + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What's stored to find a token: its SHA-256, as lowercase hex.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>The characters the list shows: the first <see cref="PrefixLength"/> after <see cref="Scheme"/>.</summary>
    public static string PrefixOf(string token) => token.Length >= Scheme.Length + PrefixLength ? token.Substring(Scheme.Length, PrefixLength) : "";

    /// <summary>Whether it can be one of Gitberth's tokens at all: gbt_ and 43 base64url characters.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: 47 } && token.StartsWith(Scheme, StringComparison.Ordinal)
        && token.AsSpan(Scheme.Length).ToString().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Why a name can't be a token's, or null.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give it a name, like Claude Code or Nightly report: the audit log shows it for everything it does."
        : name.Trim().Length > MaxName ? $"Keep it to {MaxName} characters."
        : name.Any(char.IsControl) || name.Contains('|') ? "Use letters, digits, spaces and punctuation (no |)."
        : null;

    /// <summary>"Read", "Operate", "Admin", as the list and the audit log say them.</summary>
    public static string AccessText(ApiAccess access) => access.ToString();

    /// <summary>What each access level lets a token do, in one line.</summary>
    public static string AccessHelp(ApiAccess access) => access switch
    {
        ApiAccess.Read => "Sees everything: apps, logs, deploys, domains, uptime, the audit log. Changes nothing.",
        ApiAccess.Operate => "Read, plus deploy, restart, start, stop, roll back, cancel, promote, updates, cron jobs and previews on or off.",
        _ => "Full control of this host, like signing in: it can create apps whose containers reach the whole host. Treat it like the admin password.",
    };

    public async Task<IReadOnlyList<TokenView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiTokens.AsNoTracking().OrderBy(t => t.RevokedAt != null).ThenByDescending(t => t.Id).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    /// <summary>A new token; <paramref name="expiresInDays"/> null or 0 for one that never expires.</summary>
    public async Task<TokenCreated> CreateAsync(string? name, ApiAccess access, int? expiresInDays, CancellationToken ct = default)
    {
        if (NameProblem(name) is { } problem) return new TokenCreated(null, null, problem);
        if (!Enum.IsDefined(access)) return new TokenCreated(null, null, "Choose Read, Operate or Admin.");
        if (expiresInDays is < 0 or > 3650) return new TokenCreated(null, null, "Choose when it expires: never, or in 1 to 3650 days.");
        name = name!.Trim();
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = Now();
            var active = await db.ApiTokens.AsNoTracking().Where(t => t.RevokedAt == null).ToListAsync(ct);
            active = active.Where(t => t.ExpiresAt is null || t.ExpiresAt > now).ToList();
            if (active.Count >= MaxActive) return new TokenCreated(null, null, $"There are {MaxActive} tokens in use, the most there can be. Revoke one first.");
            if (active.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                return new TokenCreated(null, null, $"A token in use is called {name} already. Give this one another name, so the audit log tells them apart.");
            var token = NewToken();
            var row = new ApiToken
            {
                Name = name, Access = access, TokenHash = Hash(token), Prefix = PrefixOf(token), CreatedAt = now,
                ExpiresAt = expiresInDays is > 0 and var days ? now.AddDays(days) : null,
            };
            db.ApiTokens.Add(row);
            await db.SaveChangesAsync(ct);
            var expiry = row.ExpiresAt is { } at ? $"it expires on {at:yyyy-MM-dd}" : "it never expires";
            audit?.Record(new AuditEntry("auth.token.created", $"Made the token {name} with {AccessText(access)} access: {expiry}.")
            {
                Target = name,
                Changes = [new AuditChange("Access", null, AccessText(access)), new AuditChange("Expires", null, row.ExpiresAt is { } e ? e.ToString("yyyy-MM-dd") : "never")],
            });
            return new TokenCreated(View(row), token, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Revokes a token: every request with it gets 401 from now on. False when there's none by that id, or it was revoked already.</summary>
    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || row.RevokedAt is not null) return false;
        row.RevokedAt = Now();
        await db.SaveChangesAsync(ct);
        _lastWrite.TryRemove(id, out _);
        audit?.Record(new AuditEntry("auth.token.revoked", $"Revoked the token {row.Name} ({AccessText(row.Access)} access): requests with it are refused from now on.")
        {
            Target = row.Name,
            Changes = [new AuditChange("Access", AccessText(row.Access), null)],
        });
        return true;
    }

    /// <summary>
    /// The token a request's bearer names, when it may be used: found by its SHA-256, compared in constant time, and
    /// neither revoked nor expired. An expired one is recorded as such the first time it's seen (if the sweep hasn't).
    /// </summary>
    public async Task<TokenCheck> CheckAsync(string? token, CancellationToken ct = default)
    {
        if (!LooksLikeToken(token)) return new TokenCheck(null, "That isn't a Gitberth token: they start with gbt_.", TokenRefusal.Malformed);
        var hash = Hash(token!);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        // The lookup is by an index; the comparison that lets it in doesn't stop at the first differing character.
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(hash)))
            return new TokenCheck(null, "The token isn't known: it may have been mistyped, or deleted.", TokenRefusal.Unknown);
        if (row.RevokedAt is not null) return new TokenCheck(null, $"The token {row.Name} was revoked.", TokenRefusal.Revoked);
        if (row.ExpiresAt is { } at && at <= Now())
        {
            await NoteExpiredAsync(ct);
            return new TokenCheck(null, $"The token {row.Name} expired on {at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", TokenRefusal.Expired);
        }
        return new TokenCheck(row, null);
    }

    /// <summary>Notes a request with the token, for Last used (when, and from where): written at most once a minute per token.</summary>
    public async Task UsedAsync(int id, string? ip, CancellationToken ct = default)
    {
        var now = Now();
        if (_lastWrite.TryGetValue(id, out var last) && now - last.At < UseWriteEvery) return;
        _lastWrite[id] = (now, ip);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ApiTokens.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now).SetProperty(t => t.LastUsedIp, ip), ct);
    }

    /// <summary>
    /// Records each token that expired since the last look, once: the ids already recorded are kept in a setting, so a
    /// restart doesn't record them again. Returns how many were recorded now.
    /// </summary>
    public async Task<int> NoteExpiredAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = Now();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var expired = await db.ApiTokens.AsNoTracking().Where(t => t.RevokedAt == null && t.ExpiresAt != null).ToListAsync(ct);
            expired = expired.Where(t => t.ExpiresAt <= now).ToList();
            var noted = ReadNoted(await settings.GetAsync(ExpiredNotedKey, ct));
            var fresh = expired.Where(t => !noted.Contains(t.Id)).ToList();
            if (fresh.Count == 0) return 0;
            foreach (var t in fresh)
            {
                audit?.Record(new AuditEntry("auth.token.expired", $"The token {t.Name} ({AccessText(t.Access)} access) expired on {t.ExpiresAt:yyyy-MM-dd HH:mm} UTC: requests with it are refused from now on.")
                {
                    Actor = AuditActor.Gitberth,
                    Target = t.Name,
                });
                noted.Add(t.Id);
            }
            // Only ids of tokens that still exist are kept.
            var existing = (await db.ApiTokens.AsNoTracking().Select(t => t.Id).ToListAsync(ct)).ToHashSet();
            noted.IntersectWith(existing);
            await settings.SetAsync(ExpiredNotedKey, JsonSerializer.Serialize(noted.Order().ToList()), ct);
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HashSet<int> ReadNoted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json)?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TokenView View(ApiToken t) =>
        new(t.Id, t.Name, t.Access, t.Prefix, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.LastUsedIp, t.RevokedAt);
}

/// <summary>Records tokens that expired, once a minute, so the audit log tells it even when nobody uses them again.</summary>
public sealed class ApiTokenExpiry(ApiTokens tokens, ILogger<ApiTokenExpiry> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = AuditScope.Begin(AuditActor.System(AuditVia.Gitberth));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await tokens.NoteExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Couldn't look for expired API tokens");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
