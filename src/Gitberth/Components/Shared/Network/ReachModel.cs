using System.Globalization;
using Gitberth.Apps;
using Gitberth.Network;
using Gitberth.Services;
using MudBlazor;

namespace Gitberth.Components.Shared.Network;

/// <summary>Who a path in the diagram is for, which gives its Access hue (gb-ac-*).</summary>
public enum ReachTone { Public, Lan, Host, Internal }

/// <summary>What the one button on a path that's off does.</summary>
public enum ReachAction { None, PublishOnLan, TurnOnLanAddress, JoinShared }

/// <summary>Where a path starts: the internet, the LAN, the host, a network, the own LAN address.</summary>
/// <param name="Href">A link of its own: another container whose network it shares.</param>
public sealed record ReachSource(string Title, string? Sub, string Icon, ReachTone Tone, bool Ghost = false, bool MonoSub = false, string? Href = null);

/// <summary>A node a path goes through before the host port: the Cloudflare tunnel.</summary>
public sealed record ReachVia(string Title, string Sub, string Image);

/// <summary>A way into the container: through the host port, or straight in (a network by name, the own LAN address).</summary>
/// <param name="Label">The address on the path, in mono: 10.1.1.80:3001, kanban:3000. Words when it's off: "Not published".</param>
/// <param name="Ghost">It's off: dashed, with the button that turns it on.</param>
public sealed record ReachLane(ReachSource Source, string? Label, bool Ghost = false, ReachVia? Via = null, ReachAction Action = ReachAction.None,
    string? ActionText = null, string? ViaLabel = null);

/// <summary>The host port node: the big numeral, ringed in its Access hue.</summary>
/// <param name="Port">Null when nothing is published: "None yet".</param>
public sealed record ReachHostPort(int? Port, string Sub, ReachTone Tone, bool Ghost = false);

/// <summary>A container port on the container node.</summary>
public sealed record ReachSocket(int Port, string Protocol, bool Web, string? Service, bool Published);

/// <summary>A fact under the container node: Stack Next.js, On gitberth 172.21.0.2.</summary>
public sealed record ReachFact(string Key, string Value, bool Mono = false);

public sealed record ReachContainer(string Title, string? Image, string? Kind, bool Self, AppState State, string? StateText, DateTime? Since,
    IReadOnlyList<ReachSocket> Sockets, int MoreSockets, IReadOnlyList<ReachFact> Facts, string? Note = null);

/// <summary>A network the container is on, for a path by name.</summary>
/// <param name="Others">The titles of the other containers on it.</param>
public sealed record ReachNetwork(string Name, IReadOnlyList<string> Others, bool Shared);

/// <summary>The Cloudflare tunnel, when domains are on and Gitberth knows it.</summary>
/// <param name="HostNetwork">cloudflared uses the host's network: it reaches apps through a host port (localhost:3001).</param>
/// <param name="OnShared">cloudflared is on the shared network: it reaches apps by name.</param>
public sealed record ReachTunnel(string Container, bool HostNetwork, bool OnShared);

/// <summary>Everything the diagram is drawn from, as plain facts: the page reads them, <see cref="ReachModel.Build"/> decides.</summary>
public sealed record ReachInput
{
    /// <summary>What other containers call it: its container name, or an app's network name.</summary>
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Image { get; init; }
    public string? Kind { get; init; }
    public bool Self { get; init; }
    public AppState State { get; init; } = AppState.Running;
    public string? StateText { get; init; }
    public DateTime? Since { get; init; }
    public bool Running { get; init; } = true;
    /// <summary>The LAN address of this host: 10.1.1.80.</summary>
    public required string LanHost { get; init; }
    /// <summary>"" for a bridge network, host, none, container:&lt;x&gt;.</summary>
    public string NetworkMode { get; init; } = "";
    /// <summary>For container:&lt;x&gt;: x's name, and where its page is.</summary>
    public string? SharesWith { get; init; }
    public IReadOnlyList<PortRule> Ports { get; init; } = [];
    public int? WebPort { get; init; }
    public bool Database { get; init; }
    /// <summary>The app's domains, the main one first.</summary>
    public IReadOnlyList<string> Domains { get; init; } = [];
    public ReachTunnel? Tunnel { get; init; }
    /// <summary>The named networks it's on, the shared one first (no gitberth-lan, no default bridge).</summary>
    public IReadOnlyList<ReachNetwork> Networks { get; init; } = [];
    /// <summary>It's on Docker's default bridge only: no names.</summary>
    public bool OnlyDefaultBridge { get; init; }
    /// <summary>The shared network's name (gitberth).</summary>
    public string SharedNetwork { get; init; } = "gitberth";
    public string? OwnLanAddress { get; init; }
    public bool OwnLanAutomatic { get; init; }
    /// <summary>Own LAN addresses are on and this container may have one: the path that's off gets [Turn on].</summary>
    public bool OwnLanPossible { get; init; }
    /// <summary>Own LAN addresses are off for the whole host: the path's button says it leads to Settings, where they're set up first.</summary>
    public bool OwnLanOff { get; init; }
    /// <summary>Publishing can be asked from here (Gitberth may change its ports, or write the Compose edit).</summary>
    public bool CanPublish { get; init; } = true;
    public bool CanJoin { get; init; } = true;
    public IReadOnlyList<ReachFact> Facts { get; init; } = [];
}

/// <summary>
/// The "How it's reached" diagram as data (design-system.md §10): the paths through the host port (the internet through the
/// tunnel, the LAN, the host itself), the paths straight in (each network by name, the own LAN address), the host-port node
/// and the container node. Paths that are off are kept, dashed, with the one button that turns them on. Pure, so every
/// state on the host is a unit test.
/// </summary>
public sealed record ReachModel(IReadOnlyList<ReachLane> ThroughHost, ReachHostPort? HostPort, IReadOnlyList<ReachLane> Direct, ReachContainer Container,
    bool Stopped, string Caption)
{
    /// <summary>At most this many paths straight in; more networks fold into the last one.</summary>
    public const int MaxDirect = 4;

    public static string ToneClass(ReachTone tone) => tone switch
    {
        ReachTone.Public => "gb-ac-public",
        ReachTone.Lan => "gb-ac-lan",
        ReachTone.Host => "gb-ac-host",
        _ => "gb-ac-internal",
    };

    public static ReachModel Build(ReachInput i)
    {
        var through = new List<ReachLane>();
        var direct = new List<ReachLane>();
        ReachHostPort? hp = null;
        // Any device: the host's own subnet isn't "the LAN" (PCs reach it from other subnets too).
        const string lanSub = "any device on your network";
        var mode = i.NetworkMode;

        if (mode == "host")
        {
            direct.Add(new ReachLane(new ReachSource("Your LAN", lanSub, Icons.Material.Outlined.Lan, ReachTone.Lan),
                $"{i.LanHost} · any port it opens"));
        }
        else if (mode.StartsWith("container:", StringComparison.Ordinal))
        {
            var other = i.SharesWith ?? mode["container:".Length..];
            direct.Add(new ReachLane(new ReachSource($"{other}'s network", "Its ports and addresses are " + other + "'s", Icons.Material.Outlined.CallSplit,
                ReachTone.Internal, Href: "apps/" + Uri.EscapeDataString(other)), "shares it"));
        }
        else if (mode == "none")
        {
            direct.Add(new ReachLane(new ReachSource("No network", "Network mode none", Icons.Material.Outlined.Block, ReachTone.Internal, Ghost: true),
                "Nothing reaches it", Ghost: true));
        }
        else
        {
            (through, hp) = ThroughHostLanes(i, lanSub);
            direct.AddRange(DirectLanes(i));
        }

        var container = ContainerOf(i);
        var model = new ReachModel(through, hp, direct, container, !i.Running, "");
        return model with { Caption = CaptionOf(model, i) };
    }

    // The ports reached through the host: the one the host-port node shows, then the paths that lead to it.
    private static (List<ReachLane>, ReachHostPort?) ThroughHostLanes(ReachInput i, string lanSub)
    {
        var lanes = new List<ReachLane>();
        var published = i.Ports.Where(p => p.Published && p.HostPort is not null).ToList();
        var primary = Primary(i);
        var lan = published.Where(p => p.Access is PortAccess.Lan or PortAccess.Custom)
            .OrderByDescending(p => p.ContainerPort == primary?.ContainerPort).ThenBy(p => p.ContainerPort).FirstOrDefault();
        var host = published.Where(p => p.Access == PortAccess.HostOnly)
            .OrderByDescending(p => p.ContainerPort == primary?.ContainerPort).ThenBy(p => p.ContainerPort).FirstOrDefault();
        var shown = lan ?? host;
        var web = i.WebPort is int w ? published.Where(p => p.ContainerPort == w).OrderBy(p => p.Access == PortAccess.HostOnly ? 0 : 1).FirstOrDefault() : null;

        // The internet, through the Cloudflare tunnel: by the web port's host port when cloudflared is on the host's network.
        // When it's on the shared network instead, it reaches the app by name (a path straight in, see Direct).
        if (i.Domains.Count > 0 && i.Tunnel is { } tunnel && !TunnelByName(i))
        {
            var via = new ReachVia("Cloudflare tunnel", tunnel.Container, "cloudflare/cloudflared");
            if (web is not null)
            {
                lanes.Add(new ReachLane(InternetOf(i), $"localhost:{Num(web.HostPort!.Value)}", Via: via));
                shown = web.Access == PortAccess.HostOnly && lan?.ContainerPort != web.ContainerPort ? web : shown ?? web;
            }
            else
            {
                lanes.Add(new ReachLane(InternetOf(i), "Not routed: no host port", Ghost: true, Via: via));
            }
        }

        if (lan is not null)
            lanes.Add(new ReachLane(new ReachSource("Your LAN", lanSub, Icons.Material.Outlined.Lan, ReachTone.Lan),
                ContainerPorts.Address(lan.Access == PortAccess.Custom ? lan.HostIp?.Trim('[', ']') ?? i.LanHost : i.LanHost, lan.HostPort!.Value)));
        else
            lanes.Add(new ReachLane(new ReachSource("Your LAN", lanSub, Icons.Material.Outlined.Lan, ReachTone.Lan),
                published.Count > 0 ? "Not on the LAN" : "Not published", Ghost: true,
                Action: i.CanPublish && i.Ports.Count > 0 ? ReachAction.PublishOnLan : ReachAction.None, ActionText: "Publish on LAN"));

        // Programs on the host itself, when a port is published there only and the tunnel isn't already drawn using it.
        if (host is not null && lan is null && !lanes.Any(l => l.Via is not null && !l.Ghost))
            lanes.Insert(0, new ReachLane(new ReachSource("This host", "programs on the host", Icons.Material.Filled.Lock, ReachTone.Host),
                ContainerPorts.Address("127.0.0.1", host.HostPort!.Value)));

        ReachHostPort hp;
        if (shown is null)
            hp = new ReachHostPort(null, i.Ports.Count == 0 ? "No ports to publish" : "Publish to give it one", ReachTone.Lan, Ghost: true);
        else
        {
            var protocol = PortRules.ProtocolOf(shown.Protocol).ToUpperInvariant();
            var others = published.Select(p => p.ContainerPort).Distinct().Count(p => p != shown.ContainerPort);
            var where = shown.Access switch
            {
                PortAccess.HostOnly => "127.0.0.1 only",
                PortAccess.Custom => $"LAN · {shown.HostIp} only",
                _ => "LAN · all addresses",
            };
            var more = others > 0 ? $" · +{others} more" : "";
            hp = new ReachHostPort(shown.HostPort, $"{protocol} · {where}{more}", shown.Access == PortAccess.HostOnly ? ReachTone.Host : ReachTone.Lan);
        }
        return (lanes, hp);
    }

    private static IEnumerable<ReachLane> DirectLanes(ReachInput i)
    {
        var lanes = new List<ReachLane>();
        var port = Primary(i)?.ContainerPort ?? i.WebPort ?? i.Ports.Select(p => (int?)p.ContainerPort).FirstOrDefault();
        if (TunnelByName(i))
            lanes.Add(new ReachLane(InternetOf(i) with { Sub = "through the tunnel, by name" }, ContainerPorts.Address(i.Name, i.WebPort!.Value)));
        var named = i.Networks.Take(i.Networks.Count > 3 ? 2 : 3).ToList();
        foreach (var n in named)
        {
            var others = n.Others.Count == 0 ? "nothing else on it" : string.Join(" · ", n.Others.Take(3)) + (n.Others.Count > 3 ? $" +{n.Others.Count - 3}" : "");
            lanes.Add(new ReachLane(new ReachSource($"{n.Name} network", others, Icons.Material.Outlined.Hub, ReachTone.Internal),
                port is int p ? ContainerPorts.Address(i.Name, p) : $"{i.Name}, by name"));
        }
        if (i.Networks.Count > 3)
        {
            var rest = i.Networks.Skip(2).ToList();
            lanes.Add(new ReachLane(new ReachSource($"{rest.Count} more networks", string.Join(" · ", rest.Select(r => r.Name)), Icons.Material.Outlined.Hub,
                ReachTone.Internal), port is int p ? ContainerPorts.Address(i.Name, p) : $"{i.Name}, by name"));
        }
        if (i.Networks.Count == 0 && i.OnlyDefaultBridge)
            lanes.Add(new ReachLane(new ReachSource("Default bridge", "no names", Icons.Material.Outlined.Hub, ReachTone.Internal, Ghost: true),
                "Not on a shared network", Ghost: true, Action: i.CanJoin ? ReachAction.JoinShared : ReachAction.None, ActionText: $"Join {i.SharedNetwork}"));
        if (i.OwnLanAddress is { } own)
            lanes.Add(new ReachLane(new ReachSource("Own address", $"{own} · {(i.OwnLanAutomatic ? "automatic" : "static")}", Icons.Material.Outlined.Router, ReachTone.Lan),
                port is int p ? ContainerPorts.Address(own, p) : $"{own} · any port"));
        else if (i.OwnLanPossible)
            lanes.Add(new ReachLane(new ReachSource("Own LAN address", "Its own IP on your LAN", Icons.Material.Outlined.Router, ReachTone.Lan, Ghost: true),
                "Off", Ghost: true, Action: ReachAction.TurnOnLanAddress, ActionText: i.OwnLanOff ? "Set up in Settings" : "Turn on"));
        return lanes.Take(MaxDirect);
    }

    // cloudflared on the shared network (not the host's) reaches a web port that has no host binding by name.
    private static bool TunnelByName(ReachInput i) =>
        i.Domains.Count > 0 && i.Tunnel is { HostNetwork: false, OnShared: true } && i.WebPort is int w
        && !i.Ports.Any(p => p.ContainerPort == w && p.Published && p.HostPort is not null);

    private static ReachSource InternetOf(ReachInput i) =>
        new("Internet", i.Domains[0] + (i.Domains.Count > 1 ? $" +{i.Domains.Count - 1}" : ""), Icons.Material.Outlined.Public, ReachTone.Public);

    // The port the drawing is about: the web port, else a database's own port, else the lowest published one, else the lowest.
    internal static PortRule? Primary(ReachInput i)
    {
        var ordered = i.Ports.OrderBy(p => p.ContainerPort).ToList();
        if (i.WebPort is int w && ordered.FirstOrDefault(p => p.ContainerPort == w && p.Published) is { } web) return web;
        if (i.Database && ordered.FirstOrDefault(p => p.Published && PortServices.Name(p.ContainerPort, p.Protocol) is not null) is { } db) return db;
        return ordered.FirstOrDefault(p => p.Published && p.HostPort is not null)
            ?? (i.WebPort is int wi ? ordered.FirstOrDefault(p => p.ContainerPort == wi) : null)
            ?? ordered.FirstOrDefault();
    }

    private static ReachContainer ContainerOf(ReachInput i)
    {
        var sockets = i.Ports.GroupBy(p => (p.ContainerPort, Protocol: PortRules.ProtocolOf(p.Protocol)))
            .Select(g => new ReachSocket(g.Key.ContainerPort, g.Key.Protocol, g.Key.ContainerPort == i.WebPort && g.Key.Protocol == "tcp",
                PortServices.Name(g.Key.ContainerPort, g.Key.Protocol), g.Any(p => p.Published)))
            .OrderByDescending(s => s.Web).ThenByDescending(s => s.Published).ThenBy(s => s.Port)
            .ToList();
        var note = i.NetworkMode == "host" ? "Host network: no Docker ports"
            : i.NetworkMode == "none" ? "No network"
            : sockets.Count == 0 ? "No ports: it doesn't serve anything"
            : null;
        return new ReachContainer(i.Title, i.Image, i.Kind, i.Self, i.State, i.StateText, i.Since, sockets.Take(3).ToList(), Math.Max(0, sockets.Count - 3),
            i.Facts, note);
    }

    // The diagram in words, for screen readers: every path, in order.
    private static string CaptionOf(ReachModel m, ReachInput i)
    {
        var parts = new List<string>();
        var port = m.Container.Sockets.FirstOrDefault()?.Port;
        var to = port is int p ? $" to container port {Num(p)}" : "";
        foreach (var lane in m.ThroughHost)
        {
            var from = lane.Source.Title == "Internet" ? $"From the internet ({string.Join(", ", i.Domains)})" : $"From {Lower(lane.Source.Title)}";
            parts.Add(lane.Ghost
                ? $"{from}: {Lower(lane.Label ?? "off")}."
                : $"{from}: {lane.Label}{(lane.Via is { } v ? $", through the {Lower(v.Title)} ({v.Sub})" : "")}, to host port {m.HostPort?.Port}{to}.");
        }
        foreach (var lane in m.Direct)
            parts.Add(lane.Ghost ? $"{lane.Source.Title}: {Lower(lane.Label ?? "off")}." : $"From {Lower(lane.Source.Title)}: {lane.Label}.");
        if (m.Stopped) parts.Add($"{i.Title} is stopped: these answer again when it starts.");
        return string.Join(" ", parts);
    }

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One element of the diagram, placed on the grid twice: <see cref="Wide"/> left to right, <see cref="Narrow"/> top to bottom.</summary>
/// <param name="Lane">The lane it belongs to (index into ThroughHost or Direct), or -1.</param>
/// <param name="Part">src, flow, via, flow-via, hp, flow-hp, direct-src, direct-flow, ctr.</param>
public sealed record ReachCell(string Part, int Lane, string Wide, string Narrow);

/// <summary>
/// Where each element of the diagram goes: CSS grid areas ("row / column / row-end / column-end") for the wide layout
/// (seven columns: source, path, tunnel, path, host port, path, container) and the narrow one (twelve columns: sources on
/// top, host port, container, then the networks feeding up from underneath). The page switches between them with a
/// container query, so the same markup reads both ways.
/// </summary>
public static class ReachLayout
{
    public static IReadOnlyList<ReachCell> Place(ReachModel m)
    {
        var cells = new List<ReachCell>();
        var h = m.ThroughHost.Count;
        var n = m.Direct.Count;
        var hasHp = h > 0 && m.HostPort is not null;
        var rows = Math.Max(1, h + n);

        // Narrow: the sources through the host port share the top rows.
        var anyVia = m.ThroughHost.Any(l => l.Via is not null);
        var top = h == 0 ? 0 : anyVia ? 4 : 2;
        var ctrRow = h == 0 ? 1 : top + 3;

        for (var i = 0; i < h; i++)
        {
            var lane = m.ThroughHost[i];
            var r = i + 1;
            var (c0, c1) = Split(i, h);
            cells.Add(new("src", i, A(r, 1, r + 1, 2), A(1, c0, 2, c1)));
            if (lane.Via is not null)
            {
                cells.Add(new("flow", i, A(r, 2, r + 1, 3), A(2, c0, 3, c1)));
                cells.Add(new("via", i, A(r, 3, r + 1, 4), A(3, c0, 4, c1)));
                cells.Add(new("flow-via", i, A(r, 4, r + 1, 5), A(4, c0, 5, c1)));
            }
            else
            {
                cells.Add(new("flow", i, A(r, 2, r + 1, 5), A(2, c0, top + 1, c1)));
            }
        }
        if (hasHp)
        {
            cells.Add(new("hp", -1, A(1, 5, h + 1, 6), A(top + 1, 1, top + 2, 13)));
            cells.Add(new("flow-hp", -1, A(1, 6, h + 1, 7), A(top + 2, 1, top + 3, 13)));
        }
        for (var j = 0; j < n; j++)
        {
            var r = h + j + 1;
            var (c0, c1) = Split(j, n);
            cells.Add(new("direct-flow", j, A(r, 2, r + 1, 7), A(ctrRow + 1, c0, ctrRow + 2, c1)));
            cells.Add(new("direct-src", j, A(r, 1, r + 1, 2), A(ctrRow + 2, c0, ctrRow + 3, c1)));
        }
        cells.Add(new("ctr", -1, A(1, 7, rows + 1, 8), A(ctrRow, 1, ctrRow + 1, 13)));
        return cells;
    }

    // Twelve columns shared by k lanes side by side (four lanes take three each).
    internal static (int Start, int End) Split(int index, int count)
    {
        var width = 12 / Math.Clamp(count, 1, 4);
        return (1 + index * width, 1 + (index + 1) * width);
    }

    private static string A(int r0, int c0, int r1, int c1) =>
        string.Create(CultureInfo.InvariantCulture, $"{r0}/{c0}/{r1}/{c1}");
}
