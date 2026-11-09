using System.Text.Json.Nodes;
using Gitberth.Domains;
using Gitberth.Services;

namespace Gitberth.Network;

/// <summary>
/// What a port change asks about the host (IHostPortChecks): who holds a host port, from the one host-port map; the LAN
/// address of this host, as every page shows it; the address LAN ports are published on; and the domains the tunnel
/// sends to a host port, from Gitberth's own records and from the tunnel's rules, so a route made in Cloudflare's
/// dashboard counts too.
/// </summary>
public sealed class HostPortChecks(HostPortMap map, HostScanner scanner, SettingsService settings, DomainService domains, CloudflareApi cloudflare,
    GitberthOptions options, ILogger<HostPortChecks> logger) : IHostPortChecks
{
    // A Cloudflare that's slow to answer mustn't hold a change up: its routes only add a line to the confirmation.
    private static readonly TimeSpan TunnelWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Kept = TimeSpan.FromSeconds(15);

    private TunnelRules? _rules;

    /// <summary>The tunnel's rules to host ports, and when they were read.</summary>
    private sealed record TunnelRules(DateTime At, IReadOnlyList<(string Hostname, int Port)> Rules);

    /// <summary>
    /// The LAN address of this host: the one saved in Settings, else GITBERTH_PUBLIC_HOST, else the one detected on the
    /// host's default route. With none of those there's no browser to ask, and localhost stands in.
    /// </summary>
    public async Task<string> LanAddressAsync(CancellationToken ct) =>
        await AddressBook.LanHostFromAsync(await settings.GetAsync(SettingKeys.PublicHost, ct), options.PublicHost,
            () => scanner.DetectedAddressAsync(ct), "localhost");

    /// <summary>
    /// Every address of the host (""), unless the host has an IPv6 address the internet can route to, or its network
    /// couldn't be read so it may have one: then 0.0.0.0, so a LAN port is published on IPv4 only, which is what the LAN
    /// uses, and not where the internet may reach it (<see cref="Docker.HostScan.LanIpv4Only"/>).
    /// </summary>
    public async Task<string> LanHostIpAsync(CancellationToken ct) => (await scanner.LatestAsync(ct)).LanIpv4Only ? "0.0.0.0" : "";

    public async Task<string?> HolderAsync(string container, int hostPort, string protocol, string hostIp, CancellationToken ct) =>
        (await map.CheckAsync(hostPort, protocol, hostIp, new HostPortOwner(Container: container), ct)).Holder;

    public async Task<IReadOnlyList<string>> TunnelRoutesAsync(int hostPort, CancellationToken ct)
    {
        var mine = (await domains.RoutedHostPortsAsync(ct)).Where(r => r.Port == hostPort).Select(r => r.Hostname);
        var rules = (await RulesAsync(ct)).Where(r => r.Port == hostPort).Select(r => r.Hostname);
        return mine.Concat(rules).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
    }

    // The tunnel's rules to host ports, read from Cloudflare while domains are on, kept for 15 seconds; none when the
    // tunnel can't be read.
    private async Task<IReadOnlyList<(string Hostname, int Port)>> RulesAsync(CancellationToken ct)
    {
        if (_rules is { } kept && DateTime.UtcNow - kept.At < Kept) return kept.Rules;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TunnelWait);
        IReadOnlyList<(string Hostname, int Port)> rules = [];
        try
        {
            if (await domains.EnabledAsync(timer.Token) && await settings.GetSecretAsync(SettingKeys.CloudflareToken, timer.Token) is { } token
                && (await domains.FindTunnelAsync(timer.Token)).Tunnel is { } tunnel)
                rules = Rules((await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, timer.Token))["ingress"] as JsonArray ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Couldn't read the tunnel's routes for a port change");
        }
        _rules = new TunnelRules(DateTime.UtcNow, rules);
        return rules;
    }

    /// <summary>The tunnel's rules that go to a host port, with their domains: app.example.com to http://localhost:3002 gives (app.example.com, 3002).</summary>
    internal static List<(string Hostname, int Port)> Rules(JsonArray ingress) =>
        ingress.OfType<JsonObject>()
            .Select(r => (Hostname: ContainerPorts.Str(r, "hostname"), Port: TunnelRoutes.HostPortOf(ContainerPorts.Str(r, "service"))))
            .Where(r => r.Hostname.Length > 0 && r.Port is not null)
            .Select(r => (r.Hostname, r.Port!.Value))
            .ToList();
}
