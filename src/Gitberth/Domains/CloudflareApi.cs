using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Domains;

/// <param name="transient">Cloudflare failed for a moment rather than because of how things are set up (see <see cref="Transient"/>).</param>
/// <param name="tokenRefused">Cloudflare refused the token itself (see <see cref="TokenRefused"/>).</param>
public sealed class CloudflareApiException(string message, bool transient = false, bool tokenRefused = false) : Exception(message)
{
    /// <summary>
    /// Cloudflare was busy or out of reach (it answered 5xx or 429, or not at all, or not in time): the same call may work
    /// in a while. A refused token, a missing permission or something not set up is never transient.
    /// </summary>
    public bool Transient { get; } = transient;

    /// <summary>
    /// Cloudflare refused the token itself (a 401, or its codes for a token it doesn't take: expired, disabled, deleted or
    /// unknown), not a permission it lacks: no call with it works until a new one is saved.
    /// </summary>
    public bool TokenRefused { get; } = tokenRefused;
}

public sealed record CloudflareZone(string Id, string Name);

/// <summary>What Cloudflare's verify said of a token: taken, its status and expiry, or why not.</summary>
/// <param name="Refused">Cloudflare refused it: it answered as for a token it doesn't take, or said it isn't active.</param>
/// <param name="Status">Its status when Cloudflare said: active, disabled or expired.</param>
/// <param name="ExpiresOn">When it expires; null when it doesn't, or Cloudflare didn't say.</param>
/// <param name="Problem">Why it was refused, or couldn't be checked (Cloudflare out of reach or failing), in words; null when it's taken.</param>
public sealed record CloudflareTokenCheck(bool Refused, string? Status, DateTimeOffset? ExpiresOn, string? Problem);

/// <param name="Comment">Cloudflare's note on the record; Gitberth marks the ones it adds with <see cref="CloudflareApi.AddedByGitberth"/>.</param>
/// <param name="Ttl">1 is Cloudflare's "auto".</param>
/// <param name="Tags">Comma-separated, as Cloudflare lists them; kept so a record that's put back is the same record.</param>
public sealed record DnsRecord(string Id, string Type, string Name, string Content, bool Proxied, string? Comment = null, int Ttl = 1, string? Tags = null);

/// <summary>A Cloudflare Access application: the host name it guards, its audience tag (which cloudflared checks), and who it lets in.</summary>
/// <param name="Emails">The email addresses its allow policies name, lower case and sorted; null when the answer didn't list its policies.</param>
public sealed record AccessApp(string Id, string Name, string Domain, string Aud, IReadOnlyList<string>? Emails);

/// <summary>
/// Gitberth's Cloudflare API calls: the routes of the tunnel on this host, and the DNS records that send a domain to it.
/// Every change it makes to DNS and Cloudflare Access is recorded in the audit log, made or failed (never the token).
/// What each answer said of the token goes to the token watch (<paramref name="signals"/>).
/// </summary>
public sealed class CloudflareApi(IHttpClientFactory httpFactory, Audit.AuditLog? audit = null, Credentials.CredentialSignals? signals = null)
{
    private const string ApiRoot = "https://api.cloudflare.com/client/v4/";

    /// <summary>
    /// The permissions Gitberth's token needs, as Cloudflare's token page names them.
    /// </summary>
    public const string Permissions = "Account › Cloudflare Tunnel › Edit, Zone › DNS › Edit and Zone › Zone › Read";

    /// <summary>The comment on DNS records Gitberth adds: only those are ever removed with a domain.</summary>
    public const string AddedByGitberth = "Added by Gitberth";

    /// <summary>The comment Gitberth put on its records before it was renamed: still its own, and changed to <see cref="AddedByGitberth"/> on sight.</summary>
    public const string AddedByGitDock = "Added by GitDock";

    /// <summary>Whether Gitberth added the record. Ones added before it was renamed carry its old name, GitDock, and are its own too.</summary>
    public static bool AddedByUs(string? comment) => comment is AddedByGitberth or AddedByGitDock;

    /// <summary>Whether Cloudflare accepts the token at all (user tokens, then account-owned ones), and when it expires.</summary>
    /// <exception cref="CloudflareApiException">Cloudflare refused it, or couldn't be asked; its message says which.</exception>
    public async Task<DateTimeOffset?> VerifyTokenAsync(string token, string accountId, CancellationToken ct = default)
    {
        var check = await CheckTokenAsync(token, accountId, ct);
        if (check.Problem is { } problem) throw new CloudflareApiException(problem, transient: !check.Refused, tokenRefused: check.Refused);
        return check.ExpiresOn;
    }

    /// <summary>
    /// What Cloudflare says of the token now: verified as a user's token, else as one the tunnel's account owns (as saving it
    /// does), with its status and expiry. Refused only when Cloudflare refused it both ways, or said it isn't active; an
    /// answer that couldn't be had (Cloudflare out of reach or failing) is a problem, never a refusal.
    /// </summary>
    public async Task<CloudflareTokenCheck> CheckTokenAsync(string token, string accountId, CancellationToken ct = default)
    {
        var user = await VerifyAsync(token, "user/tokens/verify", ct);
        if (user.Status is not null) return user;
        var account = await VerifyAsync(token, $"accounts/{accountId}/tokens/verify", ct);
        if (account.Status is not null) return account;
        return user.Refused && account.Refused ? account : new CloudflareTokenCheck(false, null, null, (account.Refused ? user : account).Problem);
    }

    // One verify: {id, status, expires_on} when Cloudflare answered for the token, else why not.
    private async Task<CloudflareTokenCheck> VerifyAsync(string token, string path, CancellationToken ct)
    {
        try
        {
            var result = await SendAsync(token, HttpMethod.Get, path, null, "check the token", ct) as JsonObject;
            var status = (result is null ? null : Text(result, "status")) ?? "active";
            var expires = result is not null && Text(result, "expires_on") is { Length: > 0 } on
                && DateTimeOffset.TryParse(on, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at : (DateTimeOffset?)null;
            return status == "active"
                ? new CloudflareTokenCheck(false, status, expires, null)
                : new CloudflareTokenCheck(true, status, expires, $"Cloudflare says Gitberth's API token is {status}. Save a new one in Settings › Domains.");
        }
        catch (CloudflareApiException ex)
        {
            return new CloudflareTokenCheck(ex.TokenRefused, null, null, ex.Message);
        }
    }

    /// <summary>The tunnel's configuration as Cloudflare's dashboard edits it: ingress rules, warp-routing, origin settings.</summary>
    public async Task<JsonObject> GetTunnelConfigAsync(string token, string accountId, string tunnelId, CancellationToken ct = default)
    {
        var result = await SendAsync(token, HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", null, "read the tunnel's routes", ct);
        return result?["config"]?.DeepClone() as JsonObject ?? [];
    }

    /// <returns>The configuration's new version, which cloudflared logs once it runs it; null when Cloudflare doesn't say.</returns>
    public async Task<long?> PutTunnelConfigAsync(string token, string accountId, string tunnelId, JsonObject config, CancellationToken ct = default)
    {
        var result = await SendAsync(token, HttpMethod.Put, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            new JsonObject { ["config"] = config.DeepClone() }, "change the tunnel's routes", ct);
        return result is JsonObject o && o["version"] is JsonValue v && v.TryGetValue<long>(out var version) ? version : null;
    }

    /// <summary>
    /// The active zone in the tunnel's account that a host name lives in: app.example.co.uk is looked up as itself, then
    /// example.co.uk, then co.uk. A tunnel can only serve names in its own account.
    /// </summary>
    public async Task<CloudflareZone?> FindZoneAsync(string token, string accountId, string hostname, CancellationToken ct = default)
    {
        var labels = hostname.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var name = string.Join('.', labels[i..]);
            var result = await SendAsync(token, HttpMethod.Get,
                $"zones?name={Uri.EscapeDataString(name)}&account.id={Uri.EscapeDataString(accountId)}&status=active", null, $"look up {name} in Cloudflare", ct);
            if (result is JsonArray zones && zones.FirstOrDefault() is JsonObject zone && Text(zone, "id") is { } id)
                return new CloudflareZone(id, Text(zone, "name") ?? name);
        }
        return null;
    }

    /// <summary>The A, AAAA and CNAME records for exactly this name: the ones that decide where it goes.</summary>
    public async Task<List<DnsRecord>> GetDnsRecordsAsync(string token, string zoneId, string hostname, CancellationToken ct = default)
    {
        var result = await SendAsync(token, HttpMethod.Get, $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(hostname)}&per_page=100", null, $"read {hostname}'s DNS records", ct);
        return (result as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(r => new DnsRecord(Text(r, "id") ?? "", Text(r, "type") ?? "", Text(r, "name") ?? "", Text(r, "content") ?? "",
                r["proxied"] is JsonValue p && p.TryGetValue<bool>(out var on) && on, Text(r, "comment"),
                r["ttl"] is JsonValue t && t.TryGetValue<int>(out var ttl) ? ttl : 1,
                r["tags"] is JsonArray tags && tags.Count > 0
                    ? string.Join(',', tags.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var tag) ? tag : null).OfType<string>())
                    : null))
            .Where(r => r.Id.Length > 0 && r.Type is "A" or "AAAA" or "CNAME" && r.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public Task CreateCnameAsync(string token, string zoneId, string hostname, string target, CancellationToken ct = default) =>
        CreateRecordAsync(token, zoneId, new DnsRecord("", "CNAME", hostname, target, Proxied: true, AddedByGitberth), ct);

    /// <summary>Adds a record; also used to put back records that "Point it here" removed when the new one couldn't be added.</summary>
    public Task CreateRecordAsync(string token, string zoneId, DnsRecord record, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["type"] = record.Type,
            ["name"] = record.Name,
            ["content"] = record.Content,
            ["proxied"] = record.Proxied,
            ["ttl"] = record.Ttl,
        };
        if (record.Comment is { Length: > 0 } comment) body["comment"] = comment;
        if (record.Tags is { Length: > 0 } tags) body["tags"] = new JsonArray([.. tags.Split(',').Select(tag => (JsonNode?)tag)]);
        return AuditedAsync(() => SendAsync(token, HttpMethod.Post, $"zones/{zoneId}/dns_records", body, $"add a DNS record for {record.Name}", ct),
            "cloudflare.dns.added", record.Name, $"Added a DNS record for {record.Name}: {record.Type} to {record.Content}{(record.Proxied ? ", proxied" : "")}.",
            $"Couldn't add a DNS record for {record.Name}", [new("Record", null, $"{record.Type} {record.Content}")]);
    }

    public Task SetProxiedAsync(string token, string zoneId, string recordId, string hostname, CancellationToken ct = default) =>
        AuditedAsync(() => SendAsync(token, HttpMethod.Patch, $"zones/{zoneId}/dns_records/{recordId}", new JsonObject { ["proxied"] = true }, $"turn on Cloudflare's proxy for {hostname}", ct),
            "cloudflare.dns.changed", hostname, $"Turned on Cloudflare's proxy for {hostname}'s DNS record.", $"Couldn't turn on Cloudflare's proxy for {hostname}",
            [new("Proxied", "off", "on")]);

    public Task SetCommentAsync(string token, string zoneId, string recordId, string hostname, string comment, CancellationToken ct = default) =>
        SendAsync(token, HttpMethod.Patch, $"zones/{zoneId}/dns_records/{recordId}", new JsonObject { ["comment"] = comment }, $"update the note on {hostname}'s DNS record", ct);

    public Task DeleteDnsRecordAsync(string token, string zoneId, string recordId, string hostname, CancellationToken ct = default) =>
        AuditedAsync(() => SendAsync(token, HttpMethod.Delete, $"zones/{zoneId}/dns_records/{recordId}", null, $"remove a DNS record for {hostname}", ct),
            "cloudflare.dns.deleted", hostname, $"Removed a DNS record for {hostname}.", $"Couldn't remove a DNS record for {hostname}", []);

    // A change to Cloudflare in the audit log: made, or why it wasn't.
    private async Task<JsonNode?> AuditedAsync(Func<Task<JsonNode?>> call, string action, string target, string done, string failed, IReadOnlyList<Audit.AuditChange> changes)
    {
        try
        {
            var result = await call();
            audit?.Record(new Audit.AuditEntry(action, done) { Target = target, Changes = changes });
            return result;
        }
        catch (CloudflareApiException ex)
        {
            audit?.Record(new Audit.AuditEntry(action, $"{failed}: {Audit.AuditLog.Clean(ex.Message, 300)?.TrimEnd('.')}.")
            {
                Target = target, Changes = changes, Outcome = Audit.AuditOutcome.Failed,
            });
            throw;
        }
    }

    // ---- Cloudflare Access (Zero Trust): protected domains ----

    /// <summary>Makes, changes and removes the Access applications that protect domains.</summary>
    public const string AccessAppsPermission = "Account › Access: Apps and Policies › Edit";

    /// <summary>Reads the account's Zero Trust team domain, where visitors sign in: Cloudflare keeps it apart from the applications.</summary>
    public const string AccessOrganizationPermission = "Account › Access: Organizations, Identity Providers, and Groups › Read";

    /// <summary>What protecting domains needs on top of <see cref="Permissions"/>, as Cloudflare's token page names it.</summary>
    public const string AccessPermission = AccessAppsPermission + " and " + AccessOrganizationPermission;

    /// <summary>How Gitberth names the Access applications it makes: "Gitberth: app.example.com". Only those are ever changed or removed.</summary>
    public const string AccessAppPrefix = "Gitberth: ";

    private const string WithAccess = Permissions + ", plus " + AccessPermission + " to protect domains";

    /// <summary>
    /// The account's Zero Trust team domain (acme.cloudflareaccess.com), where visitors sign in; null when Zero Trust
    /// isn't set up for the account yet.
    /// </summary>
    public async Task<string?> GetAccessTeamDomainAsync(string token, string accountId, CancellationToken ct = default)
    {
        var result = await SendAsync(token, HttpMethod.Get, $"accounts/{accountId}/access/organizations", null, "read the account's Cloudflare Access settings", ct,
            WithAccess, notFoundOk: true);
        return result is JsonObject o && Text(o, "auth_domain") is { Length: > 0 } domain ? domain.Trim().TrimEnd('.').ToLowerInvariant() : null;
    }

    /// <summary>The account's Access applications (every page, up to 1,000).</summary>
    public async Task<List<AccessApp>> ListAccessAppsAsync(string token, string accountId, CancellationToken ct = default)
    {
        var all = new List<AccessApp>();
        for (var page = 1; page <= 10; page++)
        {
            var (result, info) = await SendWithInfoAsync(token, HttpMethod.Get, $"accounts/{accountId}/access/apps?per_page=100&page={page}", null,
                "list the account's Cloudflare Access applications", ct, WithAccess);
            var apps = (result as JsonArray ?? []).OfType<JsonObject>().Select(ParseAccessApp).OfType<AccessApp>().ToList();
            all.AddRange(apps);
            var pages = info?["total_pages"] is JsonValue t && t.TryGetValue<int>(out var total) ? total : 1;
            if (apps.Count == 0 || page >= pages) break;
        }
        return all;
    }

    public async Task<AccessApp?> GetAccessAppAsync(string token, string accountId, string appId, CancellationToken ct = default) =>
        await SendAsync(token, HttpMethod.Get, $"accounts/{accountId}/access/apps/{Uri.EscapeDataString(appId)}", null, "read a Cloudflare Access application", ct,
            WithAccess, notFoundOk: true) is JsonObject o ? ParseAccessApp(o) : null;

    /// <summary>
    /// Makes (or, with <paramref name="appId"/>, changes) the self-hosted Access application for a host name that lets in
    /// these email addresses, with Cloudflare's default sign-in: a one-time code sent by email.
    /// </summary>
    public async Task<AccessApp> SaveAccessAppAsync(string token, string accountId, string? appId, string hostname, IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        var body = AccessAppBody(hostname, emails);
        var who = emails.Count == 1 ? "1 email address may sign in" : $"{emails.Count} email addresses may sign in";
        var result = appId is null
            ? await AuditedAsync(() => SendAsync(token, HttpMethod.Post, $"accounts/{accountId}/access/apps", body, $"put {hostname} behind Cloudflare Access", ct, WithAccess),
                "cloudflare.access.created", hostname, $"Put {hostname} behind Cloudflare Access: {who}.", $"Couldn't put {hostname} behind Cloudflare Access", [new("Allowed", null, who)])
            : await AuditedAsync(() => SendAsync(token, HttpMethod.Put, $"accounts/{accountId}/access/apps/{Uri.EscapeDataString(appId)}", body, $"change who may sign in to {hostname}", ct, WithAccess),
                "cloudflare.access.changed", hostname, $"Changed who may sign in to {hostname} through Cloudflare Access: {who}.", $"Couldn't change who may sign in to {hostname}",
                [new("Allowed", null, who)]);
        return (result is JsonObject o ? ParseAccessApp(o) : null) ?? throw new CloudflareApiException($"Cloudflare didn't say which Access application it made for {hostname}.");
    }

    /// <summary>Removes an Access application; one that's gone already counts as removed.</summary>
    public Task DeleteAccessAppAsync(string token, string accountId, string appId, string hostname, CancellationToken ct = default) =>
        AuditedAsync(() => SendAsync(token, HttpMethod.Delete, $"accounts/{accountId}/access/apps/{Uri.EscapeDataString(appId)}", null, $"take {hostname} out of Cloudflare Access", ct,
            WithAccess, notFoundOk: true), "cloudflare.access.removed", hostname, $"Took {hostname} out of Cloudflare Access: the application Gitberth made for it is gone.",
            $"Couldn't take {hostname} out of Cloudflare Access", []);

    /// <summary>The application Gitberth makes for a protected host: self-hosted, one policy that allows the listed emails.</summary>
    internal static JsonObject AccessAppBody(string hostname, IReadOnlyList<string> emails) => new()
    {
        ["name"] = AccessAppPrefix + hostname,
        ["domain"] = hostname,
        ["type"] = "self_hosted",
        ["session_duration"] = "24h",
        ["app_launcher_visible"] = false,
        ["policies"] = new JsonArray(new JsonObject
        {
            ["name"] = "Gitberth: allowed email addresses",
            ["decision"] = "allow",
            ["precedence"] = 1,
            ["include"] = new JsonArray([.. emails.Select(e => (JsonNode?)new JsonObject { ["email"] = new JsonObject { ["email"] = e } })]),
        }),
    };

    /// <summary>An Access application as Cloudflare returns it; its emails are null when the answer doesn't list its policies.</summary>
    internal static AccessApp? ParseAccessApp(JsonObject o)
    {
        if (Text(o, "id") is not { Length: > 0 } id) return null;
        List<string>? emails = null;
        if (o["policies"] is JsonArray policies)
        {
            emails = policies.OfType<JsonObject>()
                .Where(p => Text(p, "decision") is null or "allow")
                .SelectMany(p => (p["include"] as JsonArray ?? []).OfType<JsonObject>())
                .Select(rule => rule["email"] is JsonObject email ? Text(email, "email") : null)
                .OfType<string>()
                .Select(e => e.Trim().ToLowerInvariant())
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        return new AccessApp(id, Text(o, "name") ?? "", (Text(o, "domain") ?? "").Trim().TrimEnd('/').ToLowerInvariant(), Text(o, "aud") ?? "", emails);
    }

    private async Task<JsonNode?> SendAsync(string token, HttpMethod method, string path, JsonNode? body, string doing, CancellationToken ct,
        string permissions = Permissions, bool notFoundOk = false) =>
        (await SendWithInfoAsync(token, method, path, body, doing, ct, permissions, notFoundOk)).Result;

    private async Task<(JsonNode? Result, JsonNode? Info)> SendWithInfoAsync(string token, HttpMethod method, string path, JsonNode? body, string doing, CancellationToken ct,
        string permissions = Permissions, bool notFoundOk = false)
    {
        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Gitberth");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient("cloudflare").SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudflareApiException($"api.cloudflare.com couldn't be reached to {doing} ({ex.Message}).", transient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudflareApiException($"api.cloudflare.com didn't answer in time to {doing}.", transient: true);
        }
        using (response)
        {
            JsonNode? doc = null;
            try
            {
                doc = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
            }
            if (response.IsSuccessStatusCode && doc?["success"] is JsonValue ok && ok.TryGetValue<bool>(out var success) && success)
            {
                signals?.Report(new Credentials.CredentialAnswer(Credentials.CredentialWatch.CloudflareKey, Credentials.CredentialSignals.Hash(token),
                    Credentials.CredentialVerdict.Accepted));
                return (doc["result"], doc["result_info"]);
            }
            if (notFoundOk && response.StatusCode == HttpStatusCode.NotFound) return (null, null);
            var refused = TokenRefused(response.StatusCode, CodeOf(doc), MessageOf(doc));
            if (refused)
                signals?.Report(new Credentials.CredentialAnswer(Credentials.CredentialWatch.CloudflareKey, Credentials.CredentialSignals.Hash(token),
                    Credentials.CredentialVerdict.Refused, Credentials.CredentialText.CloudflareDoing(doing)));
            throw new CloudflareApiException(Explain(response.StatusCode, doc, doing, permissions), IsTransient(response.StatusCode), refused);
        }
    }

    /// <summary>Whether an answer means Cloudflare is busy or failing for now (5xx, its rate limit, a timeout), so asking again later may work.</summary>
    internal static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;

    /// <summary>
    /// Whether Cloudflare refused the token itself: a 401, 1000 (an invalid token), 6003 or 6111 (malformed), or 9109 when it
    /// says "Invalid access token". Cloudflare also answers 9109 ("Unauthorized to access requested resource") to a token that
    /// works but lacks a permission there (an account's Access applications, say): that, a 403 without those, and 10000
    /// ("Authentication error") are a permission the token lacks.
    /// </summary>
    internal static bool TokenRefused(HttpStatusCode status, int code, string? message = null) =>
        status == HttpStatusCode.Unauthorized || code is 1000 or 6003 or 6111
        || code == 9109 && message is not null && message.Contains("Invalid access token", StringComparison.OrdinalIgnoreCase);

    // The first error's code, 0 when there's none.
    private static int CodeOf(JsonNode? doc) =>
        (doc?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;

    // The first error's message, null when there's none.
    private static string? MessageOf(JsonNode? doc) =>
        (doc?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } first ? Text(first, "message") : null;

    /// <summary>
    /// Cloudflare's error, said plainly: a token it refused outright says so (it expired, or was disabled or deleted), and a
    /// missing permission names the one the token needs.
    /// </summary>
    internal static string Explain(HttpStatusCode status, JsonNode? doc, string doing, string permissions = Permissions)
    {
        var code = CodeOf(doc);
        var message = MessageOf(doc);
        if (TokenRefused(status, code, message))
            return $"Cloudflare refused Gitberth's API token itself, so it couldn't {doing}: the token has expired, or was disabled or deleted. Save a new one in Settings › Domains.";
        if (status == HttpStatusCode.Forbidden || code is 10000 or 9109)
            return $"Cloudflare didn't let Gitberth's token {doing}. The token needs {permissions}.";
        if (status == HttpStatusCode.TooManyRequests)
            return $"Cloudflare's API rate limit was reached while trying to {doing}. It clears within minutes: Gitberth tries again on the app's next deploy, and every 15 minutes for a domain it's removing.";
        return $"Cloudflare couldn't {doing}: {message ?? $"HTTP {(int)status}"}{(code > 0 ? $" (error {code})" : "")}.";
    }

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
