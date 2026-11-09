using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>Who holds a host port.</summary>
public enum HostPortUser { App, Container, Gitberth, Route, HostProgram, Reserved }

/// <summary>How a host port is held: bound now, by a stopped container, saved for an app, by a tunnel route, or by a socket on the host.</summary>
public enum HostPortState { Published, Stopped, Saved, Route, Listening }

/// <summary>
/// One use of a host port: who holds it, on which address, and for which container port. IPv4 and IPv6 bindings of one
/// owner are one use. A port can have several uses, such as a stopped container created with a port a running one has.
/// </summary>
/// <param name="HostIp">Where it's bound: empty for every address, 127.0.0.1 for the host only; null when it isn't known (a tunnel route).</param>
/// <param name="Who">The app's or container's name, the domain a tunnel route serves, or what holds it on the host (SSH).</param>
/// <param name="Link">The app's or container's page.</param>
/// <param name="Container">The container holding it, which tells a container's own ports from everyone else's.</param>
/// <param name="AppId">The app it's for: its container's, its deploy's temporary containers', its saved ports and its domains' routes.</param>
public sealed record HostPortUse(int Port, string Protocol, string? HostIp, HostPortUser Kind, string Who, string? Link,
    int? ContainerPort, HostPortState State, string? Note = null, string? Container = null, int? AppId = null)
{
    /// <summary>The Note on a deploy's temporary container's port.</summary>
    public const string TemporaryNote = "Deploy's temporary container";

    /// <summary>The Note on a stopped container's port, which it binds again when it starts.</summary>
    public const string StoppedNote = "Stopped: takes it when started";

    /// <summary>The Note on a restarting container's port: Docker lets go of it until the container is up again.</summary>
    public const string RestartingNote = "Restarting: takes it when started";

    /// <summary>How a held-but-unbound port's owner is described: "stopped", or "restarting".</summary>
    internal string Down => Note == RestartingNote ? "restarting" : "stopped";

    /// <summary>Who holds it, as the Used by column says: "kanban", "pg-old", "SSH (host)", "The tunnel". Every app is named alike, whatever its source.</summary>
    public string Owner => Kind switch
    {
        HostPortUser.Reserved => $"{Who} (host)",
        HostPortUser.Route => "The tunnel",
        _ => Who,
    };
}

/// <summary>Whose ports a check leaves out: a container's or an app's own bindings (and its _prev) don't count against it.</summary>
public sealed record HostPortOwner(string? Container = null, int? AppId = null);

/// <summary>What a host port is: free, or who holds it.</summary>
public sealed record HostPortCheck(int Port, string Protocol, HostPortUse? Owner, (int From, int To) RandomRange)
{
    public bool Free => Owner is null;

    /// <summary>In the range Linux hands out for outgoing connections: free now, but any program may take it for a moment.</summary>
    public bool InRandomRange => Port >= RandomRange.From && Port <= RandomRange.To;

    /// <summary>The answer the Network page's Check a port box gives.</summary>
    public string Sentence
    {
        get
        {
            var what = $"{Port}/{Protocol}";
            if (Owner is not { } o)
                return InRandomRange ? $"{what} is free, but in the host's random range ({RandomRange.From}-{RandomRange.To})." : $"{what} is free.";
            return (o.Kind, o.State) switch
            {
                (HostPortUser.Gitberth, _) => $"{what} is Gitberth.",
                (HostPortUser.Reserved, _) when o.Who == HostPortMap.Ssh => $"{what} is SSH on the host.",
                (HostPortUser.Reserved, _) => $"{what} is Docker's API on the host.",
                (HostPortUser.HostProgram, _) => $"{what} is used by a program on the host, outside Docker.",
                (HostPortUser.Route, _) => $"{what} is where the tunnel sends {o.Who}.",
                (_, HostPortState.Saved) => $"{what} is saved for {o.Who}, not live yet.",
                (_, HostPortState.Stopped) => $"{what} is used by {o.Who} ({o.Down}): it takes it when it starts.",
                (HostPortUser.App, _) when o.Note == HostPortUse.TemporaryNote => $"{what} is used by a deploy of {o.Who}, port {o.ContainerPort}.",
                _ => $"{what} is used by {o.Who}, port {o.ContainerPort}.",
            };
        }
    }

    /// <summary>The line under a host-port field as it's typed in: "Free", or who holds it.</summary>
    public string FieldText => Owner is not { } o
        ? InRandomRange ? $"In the host's random range ({RandomRange.From}-{RandomRange.To}). A lower port is safer." : "Free"
        : (o.Kind, o.State) switch
        {
            (HostPortUser.Gitberth, _) => "Gitberth's own page.",
            (HostPortUser.Reserved, _) when o.Who == HostPortMap.Ssh => "SSH on the host.",
            (HostPortUser.Reserved, _) => "Docker's API on the host.",
            (HostPortUser.HostProgram, _) => "A program on the host listens there.",
            (HostPortUser.Route, _) => $"Kept for the domain {o.Who}.",
            (_, HostPortState.Saved) => $"Saved for {o.Who}, not live yet.",
            (_, HostPortState.Stopped) => $"Taken by {o.Who} ({o.Down}): it takes it when it starts.",
            _ => $"Used by {o.Who}.",
        };

    /// <summary>Who holds it, as a port change's refusal names it ("Port 5432 is taken by sample-pg."); null when it's free.</summary>
    public string? Holder => Owner is not { } o ? null : (o.Kind, o.State) switch
    {
        (HostPortUser.Gitberth, _) => "Gitberth's own page",
        (HostPortUser.Reserved, _) when o.Who == HostPortMap.Ssh => "SSH on the host",
        (HostPortUser.Reserved, _) => "Docker's API on the host",
        (HostPortUser.HostProgram, _) => "a program on the host",
        (HostPortUser.Route, _) => $"the tunnel's route to {o.Who}",
        (_, HostPortState.Saved) => $"{o.Who}, saved but not live yet",
        (_, HostPortState.Stopped) => $"{o.Who} ({o.Down})",
        (HostPortUser.App, _) when o.Note == HostPortUse.TemporaryNote => $"a deploy of {o.Who}",
        _ => o.Who,
    };

    /// <summary>Why a port row can't have it, as the check before saving says it: "Port 3000 is used by orb."; null when it's free.</summary>
    public string? Problem => Owner is not { } o ? null : (o.Kind, o.State) switch
    {
        (HostPortUser.Gitberth, _) => $"Port {Port} is Gitberth's own page.",
        (HostPortUser.Reserved, _) when o.Who == HostPortMap.Ssh => $"Port {Port} is SSH on the host.",
        (HostPortUser.Reserved, _) => $"Port {Port} is Docker's API on the host.",
        (HostPortUser.HostProgram, _) => $"Port {Port} is used by a program on the host.",
        (HostPortUser.Route, _) => $"Port {Port} is where the tunnel sends {o.Who}.",
        (_, HostPortState.Saved) => $"Port {Port} is saved for {o.Who}, not live yet.",
        (_, HostPortState.Stopped) => $"Port {Port} is used by {o.Who} ({o.Down}): it takes it when it starts.",
        (HostPortUser.App, _) when o.Note == HostPortUse.TemporaryNote => $"Port {Port} is used by a deploy of {o.Who}.",
        _ => $"Port {Port} is used by {o.Who}.",
    };
}

/// <summary>A line of the Host ports card: one use, or a run of similar containers' ports folded into one (9100-9178, sample-*).</summary>
/// <param name="Prefix">What the folded containers' names start with, like "sample-"; null for a line of its own.</param>
public sealed record HostPortRow(IReadOnlyList<HostPortUse> Uses, string? Prefix)
{
    public HostPortUse First => Uses[0];

    public bool Folded => Prefix is not null;

    public int Port => Uses.Min(u => u.Port);

    public int LastPort => Uses.Max(u => u.Port);

    public string Protocol => First.Protocol;
}

/// <summary>Everything the map is built from, read at one time.</summary>
/// <param name="Routes">Gitberth's tunnel routes to host ports: the domain each serves, and its app (null once the domain was removed).</param>
/// <param name="TunnelPorts">The host ports any rule in the tunnel goes to, Gitberth's or not.</param>
/// <param name="Self">Gitberth's own container, when it runs in one: its ID, or its hostname (<see cref="NetworkService.Self"/>).</param>
public sealed record HostPortSources(
    NetworkSnapshot Docker,
    IReadOnlyList<AppDefinition> Apps,
    IReadOnlyList<(int Port, int? AppId, string Hostname)> Routes,
    IReadOnlySet<int> TunnelPorts,
    HostScan Scan,
    string? Self);

/// <summary>The map, and what it was read from.</summary>
/// <param name="HostNetwork">Containers on the host's network: what they listen on shows up as programs on the host.</param>
public sealed record HostPortView(IReadOnlyList<HostPortUse> Uses, HostScan Scan, DateTime ReadAt, IReadOnlyList<string> HostNetwork);

/// <summary>
/// Who uses each host port: running and stopped containers' bindings, apps' saved ports (live or not), the tunnel's routes
/// (they keep a port taken while they point at it), and sockets on the host that no container's binding accounts for.
/// Every check, suggestion and the Network page use this one map. TCP and UDP are counted apart, and a binding on every
/// address clashes with any other binding of its port. Kept for 15 seconds, and read again after a container's event.
/// </summary>
public sealed class HostPortMap(
    NetworkService network,
    IDbContextFactory<GitberthDb> dbFactory,
    DomainService domains,
    HostScanner scanner,
    SettingsService settings,
    ContainerEventWatcher events,
    ILogger<HostPortMap> logger)
{
    public static readonly TimeSpan Kept = TimeSpan.FromSeconds(15);

    /// <summary>Ports never suggested, unless they're the container's own: SSH, Docker's API (plain and TLS) and Gitberth's page.</summary>
    public static readonly IReadOnlySet<int> Reserved = new HashSet<int> { 22, 2375, 2376, 8080 };

    /// <summary>A run of at least this many consecutive ports of similar containers folds into one line.</summary>
    public const int FoldAt = 5;

    internal const string Ssh = "SSH";
    internal const string DockerApi = "Docker API";
    internal const string HostProgram = "A program on the host";

    // A Cloudflare that's slow to answer mustn't hold the page up.
    private static readonly TimeSpan TunnelWait = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Reading? _last;

    /// <summary>A map, and the newest container event Gitberth had when it was read.</summary>
    private sealed record Reading(HostPortView View, ContainerEvent? Newest);

    /// <summary>The map, as read in the last 15 seconds unless a container started or stopped since.</summary>
    public async Task<HostPortView> GetViewAsync(CancellationToken ct = default)
    {
        if (Fresh(_last) is { } fresh) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh(_last) is { } again) return again;
            // Taken before reading: an event that comes in meanwhile, even one dated earlier, makes this reading out of date.
            var newest = Newest();
            var view = await ReadAsync(ct);
            _last = new Reading(view, newest);
            return view;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HostPortUse>> GetAsync(CancellationToken ct = default) => (await GetViewAsync(ct)).Uses;

    /// <summary>Who holds <paramref name="port"/> for a binding on <paramref name="hostIp"/> (empty for every address), leaving out <paramref name="self"/>'s own.</summary>
    public async Task<HostPortCheck> CheckAsync(int port, string protocol, string? hostIp = null, HostPortOwner? self = null, CancellationToken ct = default)
    {
        var view = await GetViewAsync(ct);
        return Check(view.Uses, view.Scan.Random, port, protocol, hostIp, self);
    }

    /// <summary>The host port to offer for a container port: its own when free, else the next free one above it (see <see cref="Suggest"/>).</summary>
    public async Task<int> SuggestAsync(int containerPort, string protocol, HostPortOwner? self, IEnumerable<int> alsoTaken, CancellationToken ct = default)
    {
        var view = await GetViewAsync(ct);
        return Suggest(view.Uses, view.Scan.Random, containerPort, protocol, self, alsoTaken);
    }

    /// <summary>A free port on 127.0.0.1 for a deploy's temporary container: the first from 8100 up, below the random range.</summary>
    public async Task<int?> SpareAsync(IEnumerable<int> alsoTaken, CancellationToken ct = default)
    {
        var view = await GetViewAsync(ct);
        return Spare(view.Uses, view.Scan.Random, await settings.GetIntAsync(SettingKeys.PortRangeStart, 8100, ct), alsoTaken);
    }

    /// <summary>Drops the kept map and the host's sockets, after Gitberth changed a port itself.</summary>
    public void Forget()
    {
        _last = null;
        scanner.Forget();
    }

    /// <summary>Drops the kept map after an app's saved ports changed. Nothing changed on the host, so its sockets are kept.</summary>
    public void ForgetSaved() => _last = null;

    /// <summary>
    /// When a container last started or stopped: docker-proxy opens and closes the host's sockets for its ports then, so
    /// a scan of the host from before it is out of date.
    /// </summary>
    internal static DateTime? LastStartOrStop(IReadOnlyList<ContainerEvent> events) =>
        events.LastOrDefault(e => e.Action is "start" or "die")?.Time;

    private ContainerEvent? Newest() => events.Events.Count > 0 ? events.Events[^1] : null;

    private HostPortView? Fresh(Reading? reading) =>
        reading is not null && DateTime.UtcNow - reading.View.ReadAt < Kept && ReferenceEquals(Newest(), reading.Newest) ? reading.View : null;

    private async Task<HostPortView> ReadAsync(CancellationToken ct)
    {
        var readAt = DateTime.UtcNow;
        var docker = network.SnapshotAsync(ct);
        // A stopped container's docker-proxy sockets would otherwise linger in the kept scan as a program on the host.
        var scan = scanner.GetAsync(LastStartOrStop(events.Events), ct);
        var tunnel = TunnelPortsAsync(ct);
        List<AppDefinition> apps;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            apps = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().ToListAsync(ct);
        var routes = await domains.RoutedHostPortsAsync(ct);
        var sources = new HostPortSources(await docker, apps, routes, await tunnel, await scan, network.Self);
        return new HostPortView(Build(sources), sources.Scan, readAt,
            sources.Docker.Containers.Where(c => c.NetworkMode == "host").Select(c => c.Name).Order(StringComparer.Ordinal).ToList());
    }

    // Read from Cloudflare when domains are on; nothing when they're off or Cloudflare can't be read.
    private async Task<IReadOnlySet<int>> TunnelPortsAsync(CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TunnelWait);
        try
        {
            return await domains.TunnelHostPortsAsync(timer.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Couldn't read the tunnel's routes for the host-port map");
            return new HashSet<int>();
        }
    }

    /// <summary>Every use of a host port, by port and protocol.</summary>
    public static List<HostPortUse> Build(HostPortSources s)
    {
        var uses = new List<HostPortUse>();
        // gb-<slug> apps go by their containers' label; an Existing app by its container's name.
        var bySlug = s.Apps.Where(a => a.Container is null).ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var byContainer = s.Apps.Where(a => a.Container is not null).ToDictionary(a => a.Container!, StringComparer.Ordinal);
        var pages = s.Apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => NetworkService.PageOf(g.First().ContainerName));

        foreach (var c in s.Docker.Containers)
        {
            var (kind, who, link, appId, note) = Holder(c, bySlug, byContainer, s.Self);
            var config = s.Docker.ConfigOf(c);
            if (c.Live)
            {
                foreach (var g in c.Ports.Where(p => p.PublicPort is not null).GroupBy(p => (Port: p.PublicPort!.Value, p.Type, p.PrivatePort)))
                {
                    // Docker picked it: -P, or a binding without a host port.
                    var random = config.PublishAll || config.Bindings.Any(b => b.ContainerPort == g.Key.PrivatePort && b.Protocol == g.Key.Type && b.HostPort is null);
                    uses.Add(new HostPortUse(g.Key.Port, g.Key.Type, MergeIps(g.Select(p => p.Ip)), kind, who, link, g.Key.PrivatePort,
                        HostPortState.Published, random ? "Random: changes at every start" : note, c.Name, appId));
                }
            }
            else
            {
                // Stopped or restarting: the ports it was created with, which it takes again when it starts.
                var down = c.State == "restarting" ? HostPortUse.RestartingNote : HostPortUse.StoppedNote;
                foreach (var g in config.Bindings.Where(b => b.HostPort is not null).GroupBy(b => (Port: b.HostPort!.Value, b.Protocol, b.ContainerPort)))
                    uses.Add(new HostPortUse(g.Key.Port, g.Key.Protocol, MergeIps(g.Select(b => b.HostIp)), kind, who, link, g.Key.ContainerPort,
                        HostPortState.Stopped, down, c.Name, appId));
            }
        }

        foreach (var app in s.Apps)
        {
            // Saved ports its container doesn't have yet: they're the app's as soon as it's applied. An Internal one takes no host port.
            foreach (var p in app.Ports)
            {
                if (p.HostPort is not int port || uses.Any(u => u.Container == app.ContainerName && u.Port == port && u.Protocol == p.Protocol)) continue;
                uses.Add(new HostPortUse(port, p.Protocol, ContainerPorts.BindAddress(p.Access, p.HostIp) ?? "", HostPortUser.App, app.Name, pages[app.Id], p.ContainerPort,
                    HostPortState.Saved, "Saved, not live", app.ContainerName, app.Id));
            }
            // The web port carries its domains.
            if (app.Domains.Count == 0 || PortRules.WebOf(app.Ports) is not { } web) continue;
            var at = uses.FindIndex(u => u.Container == app.ContainerName && u.ContainerPort == web.ContainerPort && u.Protocol == "tcp" && u.Note is null);
            if (at >= 0) uses[at] = uses[at] with { Note = app.Domains.Count == 1 ? "1 domain" : $"{app.Domains.Count} domains" };
        }

        foreach (var (port, appId, hostname) in s.Routes.Distinct())
        {
            // A route to its own app's binding is how the domain reaches the app; any other route keeps its port for that domain.
            if (appId is int id && uses.Any(u => u.AppId == id && u.Port == port && u.Protocol == "tcp" && u.State is HostPortState.Published or HostPortState.Stopped)) continue;
            uses.Add(new HostPortUse(port, "tcp", null, HostPortUser.Route, hostname, appId is int a ? pages.GetValueOrDefault(a) : null, null,
                HostPortState.Route, $"Tunnel route to {hostname}", null, appId));
        }
        foreach (var port in s.TunnelPorts.Where(p => !uses.Any(u => u.Port == p && u.Protocol == "tcp")).Order())
            uses.Add(new HostPortUse(port, "tcp", null, HostPortUser.Route, "a domain", null, null, HostPortState.Route, "Tunnel route"));

        // docker-proxy listens on the host for every binding Docker made: those sockets are the containers' ports, not programs.
        var published = uses.Where(u => u.State == HostPortState.Published).Select(u => (u.Port, u.Protocol)).ToHashSet();
        foreach (var g in s.Scan.Sockets.Where(x => !published.Contains((x.Port, x.Protocol))).GroupBy(x => (x.Port, x.Protocol)))
        {
            var (kind, who) = Fixed(g.Key.Port, g.Key.Protocol) is { } label ? (HostPortUser.Reserved, label) : (HostPortUser.HostProgram, HostProgram);
            uses.Add(new HostPortUse(g.Key.Port, g.Key.Protocol, MergeIps(g.Select(x => (string?)x.Address)), kind, who, null, null, HostPortState.Listening));
        }
        // Without the host's sockets, SSH and Docker's API are assumed to be where they always are.
        if (s.Scan.Error is not null)
            foreach (var port in new[] { 22, 2375, 2376 }.Where(p => !uses.Any(u => u.Port == p && u.Protocol == "tcp")))
                uses.Add(new HostPortUse(port, "tcp", null, HostPortUser.Reserved, Fixed(port, "tcp")!, null, null, HostPortState.Listening));

        return uses.OrderBy(u => u.Port).ThenBy(u => u.Protocol, StringComparer.Ordinal).ThenBy(u => Weight(u)).ThenBy(u => u.Who, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Who holds <paramref name="port"/> for a new binding on <paramref name="hostIp"/>: the strongest claim when several
    /// do (bound now, then a socket on the host, a stopped container, a saved port, a tunnel route), or nobody. Between
    /// Docker's bindings Docker's own rule decides (0.0.0.0 and :: don't clash); a socket on the host on [::] may be
    /// dual-stack, so it clashes with any binding of its port.
    /// </summary>
    public static HostPortCheck Check(IReadOnlyList<HostPortUse> uses, (int From, int To) random, int port, string protocol, string? hostIp, HostPortOwner? self)
    {
        var owner = uses
            .Where(u => u.Port == port && u.Protocol == protocol && !Mine(u, self) && Clashes(u, hostIp))
            .OrderBy(Weight)
            .FirstOrDefault();
        return new HostPortCheck(port, protocol, owner, random);
    }

    // Sockets read from the host's /proc are Listening uses; everything else is a binding Docker makes, or will.
    private static bool Clashes(HostPortUse u, string? hostIp) =>
        u.State == HostPortState.Listening ? ProcNet.SocketBlocks(u.HostIp, hostIp) : ProcNet.Overlap(u.HostIp, hostIp);

    /// <summary>
    /// The host port to offer: the container's own port when it's free; otherwise the next free one above it, never 22,
    /// 2375, 2376, 8080 or one in the random range.
    /// </summary>
    public static int Suggest(IReadOnlyList<HostPortUse> uses, (int From, int To) random, int containerPort, string protocol, HostPortOwner? self, IEnumerable<int> alsoTaken)
    {
        var skip = alsoTaken.ToHashSet();
        bool Free(int p) => !skip.Contains(p) && Check(uses, random, p, protocol, null, self).Free;
        if (containerPort is >= 1 and <= 65535 && Free(containerPort)) return containerPort;
        for (var p = Math.Max(containerPort + 1, 1); p <= 65535; p++)
            if (!Reserved.Contains(p) && (p < random.From || p > random.To) && Free(p)) return p;
        return containerPort;
    }

    /// <summary>The first port from <paramref name="from"/> up that's free on 127.0.0.1, skipping the reserved ports and the random range.</summary>
    public static int? Spare(IReadOnlyList<HostPortUse> uses, (int From, int To) random, int from, IEnumerable<int> alsoTaken)
    {
        var skip = alsoTaken.ToHashSet();
        for (var p = Math.Max(from, 1); p <= 65535; p++)
            if (!Reserved.Contains(p) && (p < random.From || p > random.To) && !skip.Contains(p) && Check(uses, random, p, "tcp", "127.0.0.1", null).Free)
                return p;
        return null;
    }

    /// <summary>
    /// The uses the Network page lists: all but UDP sockets of programs on the host in the random range, which are their
    /// outgoing traffic (cloudflared's QUIC, say) and change all the time. Checks still count them.
    /// </summary>
    public static List<HostPortUse> Listed(IEnumerable<HostPortUse> uses, (int From, int To) random) =>
        uses.Where(u => !(u.Kind == HostPortUser.HostProgram && u.Protocol == "udp" && u.Port >= random.From && u.Port <= random.To)).ToList();

    /// <summary>
    /// The lines of the Host ports card: runs of <see cref="FoldAt"/> or more consecutive ports held by containers whose
    /// names share a prefix (sample-*) fold into one line; everything else is a line of its own.
    /// </summary>
    public static List<HostPortRow> Fold(IEnumerable<HostPortUse> uses)
    {
        var rows = new List<HostPortRow>();
        foreach (var protocol in uses.GroupBy(u => u.Protocol))
        {
            var list = protocol.OrderBy(u => u.Port).ThenBy(Weight).ToList();
            for (var i = 0; i < list.Count;)
            {
                var prefix = list[i].Kind == HostPortUser.Container ? list[i].Container : null;
                var j = i + 1;
                while (prefix is not null && j < list.Count && list[j].Kind == HostPortUser.Container && list[j].Port - list[j - 1].Port <= 1
                       && CommonPrefix(prefix, list[j].Container!) is { Length: > 0 } common)
                {
                    prefix = common;
                    j++;
                }
                if (prefix is not null && j - i > 1 && list.GetRange(i, j - i).Select(u => u.Port).Distinct().Count() >= FoldAt)
                {
                    rows.Add(new HostPortRow(list.GetRange(i, j - i), prefix));
                    i = j;
                }
                else
                {
                    rows.Add(new HostPortRow([list[i]], null));
                    i++;
                }
            }
        }
        return rows.OrderBy(r => r.Port).ThenBy(r => r.Protocol, StringComparer.Ordinal).ToList();
    }

    /// <summary>Who reaches a binding on the address: LAN for every address, Host only for 127.0.0.1 and ::1, else the address.</summary>
    public static string Access(string? hostIp) =>
        hostIp is null ? "-" : ProcNet.IsAnyAddress(hostIp) ? "LAN" : ProcNet.IsLoopback(hostIp) ? "Host only" : hostIp;

    // What the fixed ports are on the host.
    private static string? Fixed(int port, string protocol) => (port, protocol) switch
    {
        (22, "tcp") => Ssh,
        (2375 or 2376, "tcp") => DockerApi,
        _ => null,
    };

    // The strongest claim first.
    private static int Weight(HostPortUse u) => u.State switch
    {
        HostPortState.Published => 0,
        HostPortState.Listening => 1,
        HostPortState.Stopped => 2,
        HostPortState.Saved => 3,
        _ => 4,
    };

    private static bool Mine(HostPortUse u, HostPortOwner? self) =>
        self is not null
        && ((self.AppId is int id && u.AppId == id)
            || (self.Container is { } name && u.Container is { } c && (c == name || c == name + DeployPipeline.PreviousSuffix)));

    private static (HostPortUser Kind, string Who, string Link, int? AppId, string? Note) Holder(NetworkContainer c, IReadOnlyDictionary<string, AppDefinition> bySlug,
        IReadOnlyDictionary<string, AppDefinition> byContainer, string? self)
    {
        // Every container is an app with a page of its own, by its name; a deploy's temporary one is its app's.
        var page = NetworkService.PageOf(c.Name);
        if (NetworkService.IsSelf(c, self)) return (HostPortUser.Gitberth, "Gitberth", page, null, null);
        if (c.Labels.TryGetValue("gitberth.app", out var slug) && bySlug.TryGetValue(slug, out var app))
            return c.Name == app.ContainerName
                ? (HostPortUser.App, app.Name, page, app.Id, null)
                : (HostPortUser.App, app.Name, NetworkService.PageOf(app.ContainerName), app.Id, HostPortUse.TemporaryNote);
        // An Existing app keeps its container's name, and its row's own ports never count against it.
        if (byContainer.TryGetValue(c.Name, out var existing)) return (HostPortUser.Container, c.Name, page, existing.Id, null);
        return (HostPortUser.Container, c.Name, page, null, null);
    }

    /// <summary>
    /// One address for a use bound on several: every address when a binding is, or when there are bindings on every
    /// address of both families (0.0.0.0 and ::, which is how Docker publishes every address); every address of one
    /// family when that's all (0.0.0.0 alone stays IPv4 only); else the first that isn't the host only, else the first.
    /// </summary>
    private static string MergeIps(IEnumerable<string?> ips)
    {
        var list = ips.ToList();
        if (list.Count == 0 || list.Any(string.IsNullOrEmpty)) return "";
        var v4 = list.Any(ip => ip == "0.0.0.0");
        var v6 = list.Any(ProcNet.IsIpv6Any);
        if (v4 && v6) return "";
        if (v4) return "0.0.0.0";
        if (v6) return "::";
        return list.FirstOrDefault(ip => !ProcNet.IsLoopback(ip)) ?? list[0]!;
    }

    /// <summary>What two names start with, up to its last - _ or . ("sample-node-a…" and "sample-php-…" give "sample-").</summary>
    private static string CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return a[..(a.AsSpan(0, n).LastIndexOfAny('-', '_', '.') + 1)];
    }
}
