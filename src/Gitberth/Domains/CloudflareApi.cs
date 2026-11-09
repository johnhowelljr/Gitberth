using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Domains;

public sealed class CloudflareApiException(string message) : Exception(message);

public sealed record CloudflareZone(string Id, string Name);

/// <param name="Comment">Cloudflare's note on the record; Gitberth marks the ones it adds with <see cref="CloudflareApi.AddedByGitberth"/>.</param>
/// <param name="Ttl">1 is Cloudflare's "auto".</param>
/// <param name="Tags">Comma-separated, as Cloudflare lists them; kept so a record that's put back is the same record.</param>
public sealed record DnsRecord(string Id, string Type, string Name, string Content, bool Proxied, string? Comment = null, int Ttl = 1, string? Tags = null);

/// <summary>A Cloudflare Access application: the host name it guards, its audience tag (which cloudflared checks), and who it lets in.</summary>
/// <param name="Emails">The email addresses its allow policies name, lower case and sorted; null when the answer didn't list its policies.</param>
public sealed record AccessApp(string Id, string Name, string Domain, string Aud, IReadOnlyList<string>? Emails);

/// <summary>
/// Gitberth's Cloudflare API calls: the routes of the tunnel on this host, and the DNS records that send a domain to it.
/// </summary>
public sealed class CloudflareApi(IHttpClientFactory httpFactory)
{
    private const string ApiRoot = "https://api.cloudflare.com/client/v4/";

    /// <summary>
    /// The permissions Gitberth's token needs, as Cloudflare's token page names them.
    /// </summary>
    public const string Permissions = "Account › Cloudflare Tunnel › Edit, Zone › DNS › Edit and Zone › Zone › Read";

    /// <summary>The comment on DNS records Gitberth adds: only those are ever removed with a domain.</summary>
    public const string AddedByGitberth = "Added by Gitberth";

    /// <summary>Whether Gitberth added the record. Ones added before it was renamed carry its old name, GitDock, and are its own too.</summary>
    public static bool AddedByUs(string? comment) => comment is AddedByGitberth or "Added by GitDock";

    /// <summary>Whether Cloudflare accepts the token at all (user tokens, then account-owned ones).</summary>
    public async Task VerifyTokenAsync(string token, string accountId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(token, HttpMethod.Get, "user/tokens/verify", null, "check the token", ct);
        }
        catch (CloudflareApiException)
        {
            await SendAsync(token, HttpMethod.Get, $"accounts/{accountId}/tokens/verify", null, "check the token", ct);
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
        return SendAsync(token, HttpMethod.Post, $"zones/{zoneId}/dns_records", body, $"add a DNS record for {record.Name}", ct);
    }

    public Task SetProxiedAsync(string token, string zoneId, string recordId, string hostname, CancellationToken ct = default) =>
        SendAsync(token, HttpMethod.Patch, $"zones/{zoneId}/dns_records/{recordId}", new JsonObject { ["proxied"] = true }, $"turn on Cloudflare's proxy for {hostname}", ct);

    public Task DeleteDnsRecordAsync(string token, string zoneId, string recordId, string hostname, CancellationToken ct = default) =>
        SendAsync(token, HttpMethod.Delete, $"zones/{zoneId}/dns_records/{recordId}", null, $"remove a DNS record for {hostname}", ct);

    // ---- Cloudflare Access (Zero Trust): protected domains ----

    /// <summary>The permission protecting domains needs on top of <see cref="Permissions"/>, as Cloudflare's token page names it.</summary>
    public const string AccessPermission = "Account › Access: Apps and Policies › Edit";

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
        var result = appId is null
            ? await SendAsync(token, HttpMethod.Post, $"accounts/{accountId}/access/apps", body, $"put {hostname} behind Cloudflare Access", ct, WithAccess)
            : await SendAsync(token, HttpMethod.Put, $"accounts/{accountId}/access/apps/{Uri.EscapeDataString(appId)}", body, $"change who may sign in to {hostname}", ct, WithAccess);
        return (result is JsonObject o ? ParseAccessApp(o) : null) ?? throw new CloudflareApiException($"Cloudflare didn't say which Access application it made for {hostname}.");
    }

    /// <summary>Removes an Access application; one that's gone already counts as removed.</summary>
    public Task DeleteAccessAppAsync(string token, string accountId, string appId, string hostname, CancellationToken ct = default) =>
        SendAsync(token, HttpMethod.Delete, $"accounts/{accountId}/access/apps/{Uri.EscapeDataString(appId)}", null, $"take {hostname} out of Cloudflare Access", ct,
            WithAccess, notFoundOk: true);

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
            throw new CloudflareApiException($"api.cloudflare.com couldn't be reached to {doing} ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudflareApiException($"api.cloudflare.com didn't answer in time to {doing}.");
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
                return (doc["result"], doc["result_info"]);
            if (notFoundOk && response.StatusCode == HttpStatusCode.NotFound) return (null, null);
            throw new CloudflareApiException(Explain(response.StatusCode, doc, doing, permissions));
        }
    }

    /// <summary>Cloudflare's error, said plainly: a missing permission names the one the token needs.</summary>
    internal static string Explain(HttpStatusCode status, JsonNode? doc, string doing, string permissions = Permissions)
    {
        var first = (doc?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var code = first?["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        var message = first is null ? null : Text(first, "message");
        // 10000 is "Authentication error" (missing permission); 6003, 6111 and 9109 are a malformed or unknown token.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || code is 10000 or 9109 or 6003 or 6111)
            return $"Cloudflare didn't let Gitberth's token {doing}. The token needs {permissions}.";
        if (status == HttpStatusCode.TooManyRequests)
            return $"Cloudflare's API rate limit was reached while trying to {doing}. It clears within minutes: Gitberth tries again on the app's next deploy, and every 15 minutes for a domain it's removing.";
        return $"Cloudflare couldn't {doing}: {message ?? $"HTTP {(int)status}"}{(code > 0 ? $" (error {code})" : "")}.";
    }

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
