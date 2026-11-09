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

    private async Task<JsonNode?> SendAsync(string token, HttpMethod method, string path, JsonNode? body, string doing, CancellationToken ct)
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
                return doc["result"];
            throw new CloudflareApiException(Explain(response.StatusCode, doc, doing));
        }
    }

    /// <summary>Cloudflare's error, said plainly: a missing permission names the one the token needs.</summary>
    internal static string Explain(HttpStatusCode status, JsonNode? doc, string doing)
    {
        var first = (doc?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var code = first?["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        var message = first is null ? null : Text(first, "message");
        // 10000 is "Authentication error" (missing permission); 6003, 6111 and 9109 are a malformed or unknown token.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || code is 10000 or 9109 or 6003 or 6111)
            return $"Cloudflare didn't let Gitberth's token {doing}. The token needs {Permissions}.";
        if (status == HttpStatusCode.TooManyRequests)
            return $"Cloudflare's API rate limit was reached while trying to {doing}. It clears within minutes: Gitberth tries again on the app's next deploy, and every 15 minutes for a domain it's removing.";
        return $"Cloudflare couldn't {doing}: {message ?? $"HTTP {(int)status}"}{(code > 0 ? $" (error {code})" : "")}.";
    }

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
