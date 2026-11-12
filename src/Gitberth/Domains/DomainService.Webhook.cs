using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

/// <summary>
/// GitHub's webhook: with domains on, a host name chosen in Settings (hooks.example.com) sends only its path /hooks/github
/// through the tunnel to Gitberth itself, never behind Access, and gets its DNS record like any domain. Every other path of
/// that host gets a 404 from the tunnel.
/// </summary>
public sealed partial class DomainService
{
    /// <summary>The host name GitHub's webhooks reach Gitberth on; null when there's none.</summary>
    public async Task<string?> WebhookHostAsync(CancellationToken ct = default) => await settings.GetAsync(SettingKeys.GitHubWebhookHost, ct);

    /// <summary>The payload URL GitHub sends deliveries to, for a webhook host name.</summary>
    public static string WebhookUrl(string host) => $"https://{host}/hooks/github";

    /// <summary>How many earlier webhook host names are remembered.</summary>
    private const int EarlierKept = 10;

    /// <summary>The earlier webhook host names, as saved (<see cref="SettingKeys.GitHubWebhookHostsBefore"/>), newest first.</summary>
    internal static List<string> EarlierHosts(string? saved) =>
        [.. (saved ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The earlier webhook host names, less these apps' domains, which are forgotten as earlier ones: from the moment a name
    /// is an app's, its rules in the tunnel are the app's (or whoever made them), never Gitberth's to remove. Saved with the
    /// context's next save. Earlier names play no part on GitHub: a webhook there is Gitberth's only by the ID it recorded,
    /// or by its payload URL (<see cref="WebhookKeeper"/>).
    /// </summary>
    internal static async Task<List<string>> ForgetEarlierHostsAsync(GitberthDb db, IEnumerable<string> domains, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GitHubWebhookHostsBefore, ct);
        var earlier = EarlierHosts(row?.Value);
        var taken = domains.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = earlier.Where(h => !taken.Contains(h)).ToList();
        if (row is not null && kept.Count < earlier.Count)
        {
            if (kept.Count == 0) db.Settings.Remove(row);
            else row.Value = string.Join('\n', kept);
        }
        return kept;
    }

    /// <summary>
    /// Sets (or, with an empty one, takes away) the host name GitHub's webhooks reach Gitberth on, and routes it: the old
    /// one's route and the DNS record Gitberth added go the way a removed domain's do. The old one is remembered, so its
    /// rules in the tunnel are known as Gitberth's; the webhooks on GitHub follow by the IDs Gitberth recorded
    /// (<see cref="WebhookKeeper"/>) in the keeper's next run, or stay where they are when it can't move them then, and
    /// they're forgotten once the name is taken away. Makes the webhook's secret the first time. A name one of the apps
    /// has, or one with routes of its own in the tunnel, isn't taken.
    /// </summary>
    public async Task<(string? Error, SyncReport Report)> SetWebhookHostAsync(string? input, CancellationToken ct = default)
    {
        string? hostname = null;
        if (!string.IsNullOrWhiteSpace(input))
        {
            hostname = DomainNames.Normalize(input);
            if (DomainNames.Problem(hostname) is { } problem) return (problem, SyncReport.Nothing);
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return ("Instant deploys need domains: turn them on in Settings › Domains first.", SyncReport.Nothing);
            var before = await WebhookHostAsync(ct);
            if (string.Equals(before, hostname, StringComparison.OrdinalIgnoreCase)) return (null, await SyncIfTokenAsync(ct));
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (hostname is not null)
                {
                    if (await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Hostname == hostname, ct) is { } owner)
                    {
                        var name = await db.Apps.AsNoTracking().Where(a => a.Id == owner.AppId).Select(a => a.Name).FirstOrDefaultAsync(ct);
                        return ($"{hostname} is a domain of {name ?? "an app"}. Pick a name of its own for GitHub's webhooks, like hooks.example.com.", SyncReport.Nothing);
                    }
                    if (await UsedInTunnelAsync(hostname, ct) is { } used) return (used, SyncReport.Nothing);
                    // Freed by an earlier change and still on its way out: it's this one's again.
                    if (await db.DomainRemovals.FindAsync([hostname], ct) is { } pending) db.DomainRemovals.Remove(pending);
                }
                if (before is not null && await db.DomainRemovals.FindAsync([before], ct) is null)
                    db.DomainRemovals.Add(new DomainRemoval { Hostname = before });
                await db.SaveChangesAsync(ct);
            }
            if (hostname is not null && await settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret, ct) is null)
                await settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, WebhookSignature.NewSecret(), ct);
            if (before is not null)
            {
                List<string> earlier = [before, .. EarlierHosts(await settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore, ct))];
                var kept = earlier.Where(h => !string.Equals(h, hostname, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(EarlierKept);
                await settings.SetAsync(SettingKeys.GitHubWebhookHostsBefore, string.Join('\n', kept), ct);
            }
            await settings.SetAsync(SettingKeys.GitHubWebhookHost, hostname, ct);
            return (null, await SyncIfTokenAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns once no change to the domains is under way (a sync, a host name being saved): what it did is recorded then.</summary>
    public async Task SettledAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        _gate.Release();
    }

    private async Task<SyncReport> SyncIfTokenAsync(CancellationToken ct) => await TokenSavedAsync(ct) ? await SyncCoreAsync(widen: null, ct) : SyncReport.Nothing;

    // Why the host can't be the webhook's: someone else's rules are on it already. Null when it's free (or Cloudflare can't be asked yet).
    private async Task<string?> UsedInTunnelAsync(string hostname, CancellationToken ct)
    {
        if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return null;
        var (tunnel, _) = await ResolveTunnelAsync(connect: false, ct);
        if (tunnel is null) return null;
        try
        {
            var ingress = (await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, ct))["ingress"] as JsonArray ?? [];
            return TunnelRoutes.UsedByOthers(ingress, hostname)
                ? $"{hostname} already has routes in your tunnel. Pick a name nothing uses yet, like hooks.example.com."
                : null;
        }
        catch (CloudflareApiException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Where the tunnel reaches Gitberth's own pages: on the host's network, the host port its container publishes them on
    /// (or its own port when it runs on the host itself); on the shared network, its container's name. Null when cloudflared
    /// can't reach it.
    /// </summary>
    private async Task<string?> OwnServiceAsync(TunnelInfo tunnel, CancellationToken ct)
    {
        var port = OwnPort(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        if (options is not { RunningInContainer: true, OwnContainer: { } self }) return OwnService(null, port, tunnel);
        var r = await docker.RunAsync(["container", "inspect", self], null, ct);
        return r.Ok ? OwnService(r.StdOut, port, tunnel) : null;
    }

    /// <summary>The port Gitberth listens on in its container: ASPNETCORE_HTTP_PORTS, else ASPNETCORE_URLS, else 8080 (its image's).</summary>
    internal static int OwnPort(string? httpPorts, string? urls)
    {
        foreach (var part in (httpPorts ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536) return p;
        foreach (var url in (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = url.LastIndexOf(':');
            if (at > 0 && int.TryParse(url[(at + 1)..].TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 && url.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return 8080;
    }

    /// <summary>
    /// Where cloudflared reaches Gitberth, from its own container's `docker container inspect` (null when it doesn't run in
    /// one): on the host's network, the host port publishing <paramref name="port"/>; on the shared network, the container's
    /// name and <paramref name="port"/>.
    /// </summary>
    internal static string? OwnService(string? inspectJson, int port, TunnelInfo tunnel)
    {
        if (inspectJson is null) return tunnel.HostNetwork ? $"http://localhost:{port}" : null;
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);
            var c = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            if (tunnel.HostNetwork)
            {
                if (c.TryGetProperty("HostConfig", out var host) && host.TryGetProperty("NetworkMode", out var mode) && mode.GetString() == "host")
                    return $"http://localhost:{port}";
                if (!c.TryGetProperty("NetworkSettings", out var ns) || !ns.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Object
                    || !ports.TryGetProperty($"{port}/tcp", out var bindings) || bindings.ValueKind != JsonValueKind.Array) return null;
                foreach (var b in bindings.EnumerateArray())
                {
                    var hostPort = b.TryGetProperty("HostPort", out var hp) ? hp.GetString() : null;
                    if (!int.TryParse(hostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var published)) continue;
                    var ip = b.TryGetProperty("HostIp", out var hi) ? hi.GetString() : null;
                    // Every address (or the IPv6 twin of it): cloudflared on the host reaches it on localhost.
                    if (string.IsNullOrEmpty(ip) || ip is "0.0.0.0" or "::" or "127.0.0.1") return $"http://localhost:{published}";
                    if (!ip.Contains(':')) return $"http://{ip}:{published}";
                }
                return null;
            }
            if (!tunnel.OnAppNetwork) return null;
            var name = c.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.TrimStart('/') : null;
            return name is { Length: > 0 } ? $"http://{name}:{port}" : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
