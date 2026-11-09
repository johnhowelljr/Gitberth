using System.Net;
using System.Net.Sockets;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>How an address reaches a container: a domain, the host's LAN address, the host only, a name inside Docker, or its own LAN address.</summary>
public enum AddressKind { Domain, Lan, HostOnly, Internal, OwnLan }

/// <summary>
/// One way to reach a container or an app: a domain (https://app.example.com), its port on the host's LAN address
/// (10.1.1.80:5432, or http://10.1.1.80:3001 for a web port), on the host only (127.0.0.1:3002), by name inside Docker
/// (postgres:5432), or on its own LAN address (10.1.1.230:5432), where it answers on every port it listens on.
/// </summary>
/// <param name="Href">What opening it opens: web addresses only, and never a host-only one, which the browser's machine can't reach.</param>
/// <param name="Copy">What a copy button copies.</param>
/// <param name="Live">It answers there now; false while the container is stopped, or for a saved port that isn't live.</param>
/// <param name="Note">"Stopped", "Saved: 3005, not live yet", "Random: changes at every start", "Main" (a domain).</param>
/// <param name="HostPort">The host port of a LAN or host-only address.</param>
public sealed record Address(AddressKind Kind, string Text, string? Href, string Copy, int? ContainerPort, string Protocol, bool Live, string? Note,
    int? HostPort = null);

/// <summary>How a list shows a port chip: opens a web address, copies an address, copies a host-only one, or is unpublished.</summary>
public enum AddressChipKind { Open, Copy, HostOnly, Unpublished }

/// <summary>A port in a list of containers: "9101:80" (Docker's -p order) for a published one, "5432/tcp" for one that isn't.</summary>
/// <param name="Tooltip">"10.1.1.80:5432 -> 5432/tcp (all addresses)", or where containers reach an unpublished one.</param>
/// <param name="Value">What clicking opens (Open) or copies (Copy, HostOnly); null for an unpublished port.</param>
public sealed record AddressChip(string Label, string Tooltip, AddressChipKind Kind, string? Value, int? HostPort, int ContainerPort, string Protocol)
{
    /// <summary>
    /// What screen readers and voice control call it: the label it shows first, so "click 9101:80" finds it, then what
    /// it does ("9101:80, open http://10.1.1.80:9101").
    /// </summary>
    public string AccessibleName => Kind switch
    {
        AddressChipKind.Open => $"{Label}, open {Value}",
        AddressChipKind.HostOnly => $"{Label}, host only, copy {Value}",
        AddressChipKind.Copy => $"{Label}, copy {Value}",
        _ => $"{Label}, {Tooltip}",
    };
}

/// <summary>A container's addresses and the chips a list shows for its ports.</summary>
/// <param name="Network">The named network others reach it on (the shared one first); null when it's only on Docker's default bridge.</param>
/// <param name="HostNetwork">It uses the host's network: it listens on the host's own addresses, on whatever ports its program picks.</param>
/// <param name="SharesNetworkOf">network mode container:&lt;x&gt;: its ports are x's. x's name, or its short ID when it's gone.</param>
public sealed record ContainerAddresses(string Container, IReadOnlyList<Address> Addresses, IReadOnlyList<AddressChip> Chips, string? Network,
    bool HostNetwork, string? SharesNetworkOf)
{
    /// <summary>Whether a search for a port (8080, 5432/tcp, 9101:80) finds it.</summary>
    public bool Matches(string text)
    {
        text = text.Trim();
        return int.TryParse(text, out var port)
            ? Chips.Any(c => c.HostPort == port || c.ContainerPort == port)
            : Chips.Any(c => c.Label.Equals(text, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>An app's addresses: its domains (the main one first), then LAN, host-only and internal ones, the web port's first.</summary>
/// <param name="WebPort">The container port its domains, Open and health check use (the row marked as the web port); null for an app without one.</param>
public sealed record AppAddresses(int AppId, IReadOnlyList<Address> Addresses, int? WebPort, bool Running)
{
    public static AppAddresses None(int appId) => new(appId, [], null, false);

    public IEnumerable<Address> Domains => Addresses.Where(a => a.Kind == AddressKind.Domain);

    public IEnumerable<Address> Lan => Addresses.Where(a => a.Kind == AddressKind.Lan);

    public IEnumerable<Address> HostOnly => Addresses.Where(a => a.Kind == AddressKind.HostOnly);
}

/// <summary>Every container's and app's addresses from one look at Docker, for pages that list many.</summary>
public sealed record AddressList(IReadOnlyDictionary<string, ContainerAddresses> Containers, IReadOnlyDictionary<int, AppAddresses> Apps)
{
    public static readonly AddressList Empty = new(new Dictionary<string, ContainerAddresses>(), new Dictionary<int, AppAddresses>());

    public ContainerAddresses? For(string container) => Containers.GetValueOrDefault(container);

    /// <summary>
    /// An app's addresses; null when they weren't read (Docker couldn't be, or the app is newer than the list), which a
    /// page must not show as "Internal only".
    /// </summary>
    public AppAddresses? For(int appId) => Apps.GetValueOrDefault(appId);
}

/// <summary>
/// Where every container and app is reached, built one way for every page: the Containers list, the dashboard, the deploy
/// page and the app's Open button all agree. Live bindings always win over saved settings, which show as "Saved: …, not
/// live yet". A binding on every address (empty, 0.0.0.0 or ::) is reached at the LAN address of this host; one on the
/// loopback is the host's only and never a link; any other address is shown as it is, bracketed when it's IPv6.
/// </summary>
public sealed class AddressBook(NetworkService network, AppService apps, DomainService domains, IDbContextFactory<GitberthDb> dbFactory)
{
    /// <summary>
    /// Ports that never speak HTTP, so their chips copy rather than open (SSH, mail, DNS, databases, brokers, Erlang's port
    /// mapper and distribution, Docker's API).
    /// </summary>
    public static readonly IReadOnlySet<int> NotWeb = new HashSet<int>
    {
        22, 25, 53, 110, 143, 389, 465, 587, 993, 995, 1433, 1521, 1883, 2375, 2376, 3306, 4222, 4369, 5432, 5671, 5672, 6379, 7687,
        8883, 9042, 9092, 11211, 25672, 27017, 61613, 61614,
    };

    /// <summary>The web consoles of engines that are otherwise databases: RabbitMQ, MinIO, CouchDB, Elasticsearch, InfluxDB, Neo4j, ClickHouse, NATS.</summary>
    public static readonly IReadOnlySet<int> DatabaseWeb = new HashSet<int> { 15672, 9001, 5984, 9200, 8086, 7474, 8123, 8222 };

    /// <summary>Every container's and app's addresses, by container name and by app.</summary>
    public async Task<AddressList> LoadAsync(string lanHost, CancellationToken ct = default)
    {
        var snapshot = await network.SnapshotAsync(ct);
        var all = await AppsAsync(ct);
        var domainsOn = await domains.EnabledAsync(ct);
        // gb-<slug> apps go by their containers' label; an Existing app by its container's name.
        var labelled = all.Where(a => a.Container is null).ToList();
        var nav = labelled.ToDictionary(a => a.Slug, a => new NavApp(a.Id, a.Name, a.Slug), StringComparer.Ordinal);
        var bySlug = labelled.ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var byContainer = all.Where(a => a.Container is not null).ToDictionary(a => a.Container!, StringComparer.Ordinal);
        var byName = snapshot.Containers.ToDictionary(c => c.Name, StringComparer.Ordinal);

        var containers = snapshot.Containers.ToDictionary(c => c.Name, c =>
        {
            var role = ContainerService.RoleOf(new ContainerSummary(c.Name, c.State, c.Status, c.Image, "", c.Labels, c.Command, c.Id), nav, network.Self);
            // Only the app's own container answers to the app's name; a deploy's temporary one doesn't.
            var app = c.Labels.TryGetValue("gitberth.app", out var slug) && bySlug.GetValueOrDefault(slug) is { } owner && c.Name == owner.ContainerName ? owner
                : byContainer.GetValueOrDefault(c.Name);
            return ForContainer(c, snapshot.ConfigOf(c), role, lanHost, network.Shared, app, snapshot.Containers);
        }, StringComparer.Ordinal);
        var withContainers = all.Select(a => (App: a, Container: byName.GetValueOrDefault(a.ContainerName)))
            .Select(x => (x.App, x.Container, Config: x.Container is null ? NetworkPortConfig.None : snapshot.ConfigOf(x.Container))).ToList();
        var tunnelByName = await TunnelByNameAsync(withContainers, domainsOn, ct);
        var appAddresses = withContainers.ToDictionary(x => x.App.Id, x => ForApp(x.App, x.Container, x.Config, lanHost, domainsOn, tunnelByName));
        return new AddressList(containers, appAddresses);
    }

    /// <summary>One app's addresses. <paramref name="app"/> needs its Ports and Domains loaded.</summary>
    public async Task<AppAddresses> ForAppAsync(AppDefinition app, string lanHost, CancellationToken ct = default)
    {
        var snapshot = await network.SnapshotAsync(ct);
        var container = snapshot.Containers.FirstOrDefault(c => c.Name == app.ContainerName);
        var config = container is null ? NetworkPortConfig.None : snapshot.ConfigOf(container);
        var domainsOn = await domains.EnabledAsync(ct);
        return ForApp(app, container, config, lanHost, domainsOn, await TunnelByNameAsync([(app, container, config)], domainsOn, ct));
    }

    /// <summary>The LAN address of this host, as every page uses it (see <see cref="LanHostFromAsync"/>).</summary>
    public Task<string> LanHostAsync(string baseUri) => apps.GetPublicHostAsync(baseUri);

    /// <summary>
    /// The LAN address of this host: the one saved in Settings, else GITBERTH_PUBLIC_HOST, else the address detected on the
    /// host's default route, else the host name the browser used. Detecting is only asked for when it's needed.
    /// </summary>
    public static async Task<string> LanHostFromAsync(string? saved, string? variable, Func<Task<string?>> detect, string browserHost) =>
        NullIfBlank(saved) ?? NullIfBlank(variable) ?? NullIfBlank(await detect()) ?? browserHost;

    /// <summary>
    /// The address that stands for the app: its main domain when domains are on and it's live, else its web port's LAN
    /// address, else nothing.
    /// </summary>
    public static string? Primary(AppAddresses a) =>
        a.Domains.FirstOrDefault() is { Live: true } main
            ? main.Href
            : a.Lan.FirstOrDefault(x => x.ContainerPort == a.WebPort && x.Protocol == "tcp" && x.Href is not null)?.Href;

    /// <summary>
    /// Whether a port opens in a browser: an app's web port always; otherwise a TCP port that isn't a known non-HTTP one,
    /// on a container that isn't a database, unless it's that database's web console (RabbitMQ's 15672).
    /// </summary>
    public static bool IsWeb(int port, string protocol, ContainerRole role, bool appWebPort) =>
        IsWeb(port, protocol, role == ContainerRole.Database, appWebPort);

    internal static bool IsWeb(int port, string protocol, bool database, bool appWebPort) =>
        protocol == "tcp" && (appWebPort || (database ? DatabaseWeb.Contains(port) : !NotWeb.Contains(port)));

    /// <summary>A container's addresses and chips: live bindings while it runs, the ones it was created with while it's stopped.</summary>
    /// <param name="app">The app whose container it is, if any: its alias is the app's name on the network, and its web port opens.</param>
    /// <param name="others">Every container, to name the one whose network it uses (network mode container:&lt;id&gt;).</param>
    public static ContainerAddresses ForContainer(NetworkContainer c, NetworkPortConfig config, ContainerRole role, string lanHost, string shared,
        AppDefinition? app = null, IEnumerable<NetworkContainer>? others = null)
    {
        var named = c.Networks.Where(e => e.Network is not ("bridge" or "host" or "none")).OrderBy(e => e.Network == shared ? 0 : 1)
            .ThenBy(e => e.Network, StringComparer.Ordinal).FirstOrDefault()?.Network;
        var sharesOf = c.NetworkMode?.StartsWith("container:", StringComparison.Ordinal) == true ? NetworkOwner(c.NetworkMode["container:".Length..], others ?? []) : null;
        if (c.NetworkMode == "host")
            return new ContainerAddresses(c.Name, [new Address(AddressKind.Lan, lanHost, null, lanHost, null, "tcp", c.Running, "On any port it listens on (host network)")],
                [], null, HostNetwork: true, null);

        var name = app?.NetworkName ?? c.Name;
        var webPort = app is null ? null : PortRules.WebOf(app.Ports)?.ContainerPort;
        var database = role == ContainerRole.Database;
        var addresses = new List<Address>();
        var chips = new List<AddressChip>();
        var bound = new HashSet<(int, string)>();
        foreach (var b in Bindings(c, config))
        {
            var web = IsWeb(b.ContainerPort, b.Protocol, database, webPort == b.ContainerPort);
            var address = Published(b, lanHost, web, c.Running ? b.Random ? "Random: changes at every start" : null : Down(c), c.Running);
            addresses.Add(address);
            bound.Add((b.ContainerPort, b.Protocol));
            var label = $"{b.HostPort}:{b.ContainerPort}{(b.Protocol == "tcp" ? "" : "/" + b.Protocol)}";
            var tooltip = $"{HostAndPort(address)} -> {b.ContainerPort}/{b.Protocol}{(Scope(b.Ips) is { } scope ? $" ({scope})" : "")}{(c.Running ? "" : $" · {Down(c).ToLowerInvariant()}")}";
            // Nothing answers while it's stopped, so its address is there to copy, not to open.
            chips.Add(address.Kind == AddressKind.HostOnly ? new AddressChip(label, tooltip, AddressChipKind.HostOnly, address.Copy, b.HostPort, b.ContainerPort, b.Protocol)
                : address.Href is { } href && c.Running ? new AddressChip(label, tooltip, AddressChipKind.Open, href, b.HostPort, b.ContainerPort, b.Protocol)
                : new AddressChip(label, tooltip, AddressChipKind.Copy, address.Copy, b.HostPort, b.ContainerPort, b.Protocol));
        }

        // Every port it listens on is reached by name on a named network, published or not, and on its own LAN address.
        var ports = (c.Live
                ? c.Ports.Select(p => (Port: p.PrivatePort, Protocol: p.Type))
                : config.Exposed.Select(p => (Port: p.Port, Protocol: p.Protocol)).Concat(config.Bindings.Select(b => (Port: b.ContainerPort, Protocol: b.Protocol))))
            .Distinct().OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal).ToList();
        var own = OwnAddress(c);
        foreach (var (port, protocol) in ports)
        {
            if (named is not null)
            {
                var text = app is not null && webPort == port && protocol == "tcp" ? $"http://{name}:{port}" : $"{name}:{port}";
                addresses.Add(new Address(AddressKind.Internal, text, null, text, port, protocol, c.Running, c.Running ? null : Down(c)));
            }
            if (own is not null) addresses.Add(Own(own, port, protocol, IsWeb(port, protocol, database, webPort == port), c.Running, c.Running ? null : Down(c)));
            if (bound.Contains((port, protocol))) continue;
            var tip = named is not null ? $"Not published. Containers on {named} reach it as {name}:{port}."
                : c.Networks.Any(e => e.Network == "bridge") ? "Not published. It's only on Docker's default bridge, where containers have no names."
                : "Not published, and on no network.";
            chips.Add(new AddressChip($"{port}/{protocol}", tip, AddressChipKind.Unpublished, null, null, port, protocol));
        }
        // With no port known, the address alone: it still answers there on whatever it listens on.
        if (own is not null && ports.Count == 0) addresses.Add(new Address(AddressKind.OwnLan, own, null, own, null, "tcp", c.Running, c.Running ? null : Down(c)));
        return new ContainerAddresses(c.Name, addresses, WebFirst(chips, webPort), named, HostNetwork: false, sharesOf);
    }

    /// <summary>
    /// The chips with the app's web port first, then a console a broker or database serves (RabbitMQ's 15672), then the rest
    /// as they were: the Apps list opens the first one, so RabbitMQ's row opens its console, not Erlang's 4369.
    /// </summary>
    internal static List<AddressChip> WebFirst(IEnumerable<AddressChip> chips, int? webPort) =>
        chips.OrderBy(ch => ch.Protocol != "tcp" ? 2 : ch.ContainerPort == webPort ? 0 : DatabaseWeb.Contains(ch.ContainerPort) ? 1 : 2).ToList();

    /// <summary>
    /// The container's own LAN address (gitberth-lan): the one it has while it runs, else the one it keeps for its next
    /// start; null for none.
    /// </summary>
    internal static string? OwnAddress(NetworkContainer? c) =>
        c?.Networks.FirstOrDefault(e => e.Network == LanAddresses.Network) is { } e ? e.IpAddress ?? e.FixedAddress : null;

    // A port on the container's own LAN address: 10.1.1.230:5432, or http://10.1.1.230:8080 for a web port.
    private static Address Own(string ip, int port, string protocol, bool web, bool live, string? note)
    {
        var hostAndPort = $"{ip}:{port}";
        if (!web) return new Address(AddressKind.OwnLan, hostAndPort, null, hostAndPort, port, protocol, live, note);
        var url = $"{(port is 443 or 8443 ? "https" : "http")}://{hostAndPort}";
        return new Address(AddressKind.OwnLan, url, url, url, port, protocol, live, note);
    }

    /// <summary>
    /// The container named in network mode container:&lt;x&gt;, by its name. Compose writes the full ID of the service's
    /// container there, so x is looked up by ID too; one that's gone is shortened to the 12 characters Docker shows.
    /// </summary>
    internal static string NetworkOwner(string target, IEnumerable<NetworkContainer> containers)
    {
        if (containers.FirstOrDefault(c => c.Name == target || c.Id == target || (target.Length >= 12 && c.Id.StartsWith(target, StringComparison.Ordinal))) is { } owner)
            return owner.Name;
        return target.Length > 12 && target.All(char.IsAsciiHexDigitLower) ? target[..12] : target;
    }

    /// <summary>An app's addresses: what its container publishes now when it runs, what it was created with when it's stopped, else its saved ports.</summary>
    /// <param name="container">The app's own container (gb-&lt;slug&gt;), if it has one.</param>
    /// <param name="tunnelByName">
    /// cloudflared is on the shared network and not the host's, so it reaches the web port by the app's name, published or
    /// not (<see cref="TunnelRoutes.ServiceFor"/>); otherwise a domain is live only through a host binding of the web port.
    /// </param>
    public static AppAddresses ForApp(AppDefinition app, NetworkContainer? container, NetworkPortConfig config, string lanHost, bool domainsOn,
        bool tunnelByName = false)
    {
        var web = PortRules.WebOf(app.Ports);
        var running = container?.Running == true;
        var down = container is null ? "Not running" : Down(container);
        List<Binding> bindings = container is null ? [] : Bindings(container, config).ToList();
        var addresses = new List<Address>();

        var main = DomainService.MainOf(app.Domains);
        var webLive = web is not null && running && (HostBound(web, bindings)
            || (tunnelByName && container!.Ports.Any(p => p.PrivatePort == web.ContainerPort && p.Type == "tcp")));
        foreach (var d in app.Domains.OrderBy(d => d.Hostname == main ? 0 : 1).ThenBy(d => d.Id))
            addresses.Add(new Address(AddressKind.Domain, $"https://{d.Hostname}", $"https://{d.Hostname}/", $"https://{d.Hostname}", web?.ContainerPort, "tcp",
                domainsOn && webLive, d.Hostname == main ? "Main" : null));

        var rows = app.Ports.OrderBy(p => p == web ? 0 : 1).ThenBy(p => p.Id).ToList();
        foreach (var p in rows)
        {
            var isWeb = IsWeb(p.ContainerPort, p.Protocol, database: false, appWebPort: p == web);
            var mine = bindings.Where(b => b.ContainerPort == p.ContainerPort && b.Protocol == p.Protocol).ToList();
            if (mine.Count > 0)
            {
                // Live bindings win; a saved port that differs shows next to them.
                foreach (var b in mine)
                    addresses.Add(Published(b, lanHost, isWeb,
                        !running ? down : b.HostPort != p.HostPort || !SameAccess(b.Ips, ContainerPorts.BindAddress(p.Access, p.HostIp)) ? NotLiveYet(p) : b.Random ? "Random: changes at every start" : null,
                        running));
            }
            else if (p.HostPort is int hostPort)
            {
                var saved = new Binding(hostPort, p.ContainerPort, p.Protocol, [ContainerPorts.BindAddress(p.Access, p.HostIp)], Random: false);
                addresses.Add(Published(saved, lanHost, isWeb, container is { Live: true } ? NotLiveYet(p) : down, live: false));
            }
        }
        // A binding the container has without a saved port: it's live all the same.
        foreach (var b in bindings.Where(b => !app.Ports.Any(p => p.ContainerPort == b.ContainerPort && p.Protocol == b.Protocol)))
            addresses.Add(Published(b, lanHost, IsWeb(b.ContainerPort, b.Protocol, database: false, appWebPort: false), running ? null : down, running));

        var own = OwnAddress(container);
        foreach (var (port, protocol) in rows.Select(p => (p.ContainerPort, p.Protocol)).Distinct())
        {
            var text = port == web?.ContainerPort && protocol == "tcp" ? $"http://{app.NetworkName}:{port}" : $"{app.NetworkName}:{port}";
            addresses.Add(new Address(AddressKind.Internal, text, null, text, port, protocol, running, running ? null : down));
            if (own is not null)
                addresses.Add(Own(own, port, protocol, IsWeb(port, protocol, database: false, appWebPort: port == web?.ContainerPort), running, running ? null : down));
        }
        return new AppAddresses(app.Id, addresses, web?.ContainerPort, running);
    }

    // A saved port the container doesn't have yet: its new host port, or, saved as Internal, no host port at all.
    private static string NotLiveYet(PortMapping p) => p.HostPort is int port ? $"Saved: {port}, not live yet" : "Saved: Internal, not live yet";

    // The web port has a host binding, which cloudflared on the host's network needs.
    private static bool HostBound(PortMapping web, IEnumerable<Binding> bindings) => bindings.Any(b => b.ContainerPort == web.ContainerPort && b.Protocol == "tcp");

    /// <summary>
    /// Whether cloudflared reaches the app's web port by name (see <see cref="ForApp"/>): asked only when that decides
    /// whether its domains are live, for a running app with domains whose web port has no host binding.
    /// </summary>
    private async Task<bool> TunnelByNameAsync(IEnumerable<(AppDefinition App, NetworkContainer? Container, NetworkPortConfig Config)> candidates, bool domainsOn,
        CancellationToken ct) =>
        domainsOn
        && candidates.Any(a => a.App.Domains.Count > 0 && PortRules.WebOf(a.App.Ports) is { } web && a.Container is { Running: true } c && !HostBound(web, Bindings(c, a.Config)))
        && (await domains.FindTunnelAsync(ct)).Tunnel is { HostNetwork: false, OnAppNetwork: true };

    // Why a container doesn't answer now.
    private static string Down(NetworkContainer c) => c.State switch
    {
        "paused" => "Paused",
        "restarting" => "Restarting",
        _ => "Stopped",
    };

    /// <summary>A host name or IP as it goes in an address: an IPv6 literal in brackets.</summary>
    public static string HostPart(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host.Trim('[', ']')}]" : host;

    /// <summary>A host port bound to a container port, on one or more addresses (IPv4 and IPv6 are one binding).</summary>
    /// <param name="Ips">Where it's bound; null or empty is every address.</param>
    /// <param name="Random">Docker picked the host port, and picks another at every start.</param>
    private sealed record Binding(int HostPort, int ContainerPort, string Protocol, IReadOnlyList<string?> Ips, bool Random);

    // Live while it runs (with the ports Docker actually bound, random ones included); else the ones it was created with.
    private static IEnumerable<Binding> Bindings(NetworkContainer c, NetworkPortConfig config)
    {
        if (c.Live)
            return c.Ports.Where(p => p.PublicPort is not null)
                .GroupBy(p => (Host: p.PublicPort!.Value, p.PrivatePort, p.Type))
                .Select(g => new Binding(g.Key.Host, g.Key.PrivatePort, g.Key.Type, g.Select(p => p.Ip).ToList(),
                    config.PublishAll || config.Bindings.Any(b => b.ContainerPort == g.Key.PrivatePort && b.Protocol == g.Key.Type && b.HostPort is null)))
                // Loopback and other bindings of one port are separate addresses.
                .SelectMany(Split)
                .OrderBy(b => b.HostPort);
        return config.Bindings.Where(b => b.HostPort is not null)
            .GroupBy(b => (Host: b.HostPort!.Value, b.ContainerPort, b.Protocol))
            .Select(g => new Binding(g.Key.Host, g.Key.ContainerPort, g.Key.Protocol, g.Select(b => b.HostIp).ToList(), Random: false))
            .SelectMany(Split)
            .OrderBy(b => b.HostPort);

        static IEnumerable<Binding> Split(Binding b) =>
            b.Ips.Any(ProcNet.IsAnyAddress) || b.Ips.All(ProcNet.IsLoopback) || b.Ips.Count == 1
                ? [b]
                : b.Ips.Select(ip => b with { Ips = [ip] });
    }

    private static Address Published(Binding b, string lanHost, bool web, string? note, bool live)
    {
        var loopback = !b.Ips.Any(ProcNet.IsAnyAddress) && b.Ips.All(ProcNet.IsLoopback);
        var host = b.Ips.Any(ProcNet.IsAnyAddress) ? HostPart(lanHost)
            : loopback ? HostPart(b.Ips.Any(ip => ip!.Contains('.')) ? "127.0.0.1" : "::1")
            : HostPart(b.Ips[0]!);
        var hostAndPort = $"{host}:{b.HostPort}";
        if (loopback) return new Address(AddressKind.HostOnly, hostAndPort, null, hostAndPort, b.ContainerPort, b.Protocol, live, note, b.HostPort);
        if (!web) return new Address(AddressKind.Lan, hostAndPort, null, hostAndPort, b.ContainerPort, b.Protocol, live, note, b.HostPort);
        var url = $"{(b.ContainerPort is 443 or 8443 ? "https" : "http")}://{hostAndPort}";
        return new Address(AddressKind.Lan, url, url, url, b.ContainerPort, b.Protocol, live, note, b.HostPort);
    }

    // 10.1.1.80:3001 from http://10.1.1.80:3001.
    private static string HostAndPort(Address a) => a.Text.Contains("://", StringComparison.Ordinal) ? a.Text[(a.Text.IndexOf("://", StringComparison.Ordinal) + 3)..] : a.Text;

    // Which addresses a binding is on, for its tooltip.
    private static string? Scope(IReadOnlyList<string?> ips)
    {
        if (ips.Any(string.IsNullOrEmpty) || (ips.Contains("0.0.0.0") && ips.Contains("::"))) return "all addresses";
        if (ips.Contains("0.0.0.0")) return "all IPv4 addresses";
        if (ips.Contains("::")) return "all IPv6 addresses";
        return ips.All(ProcNet.IsLoopback) ? "host only" : null;
    }

    private static bool SameAccess(IReadOnlyList<string?> ips, string? savedIp) =>
        ProcNet.IsAnyAddress(savedIp) ? ips.Any(ProcNet.IsAnyAddress) : ips.Any(ip => ProcNet.Overlap(ip, savedIp) && !ProcNet.IsAnyAddress(ip));

    private async Task<List<AppDefinition>> AppsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().ToListAsync(ct);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
