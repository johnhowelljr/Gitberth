using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Components.Shared.Network;

/// <summary>Where a row of an app's Ports table comes from.</summary>
public enum PortOrigin
{
    /// <summary>Exposed only: its image declares it (EXPOSE), or it was started with --expose.</summary>
    Exposed,
    /// <summary>Published: a binding the container was created with.</summary>
    Bound,
    /// <summary>A port Gitberth noted for it: its program listens there, though nothing declares it.</summary>
    Note,
    /// <summary>Added on the card, not saved yet.</summary>
    Added,
    /// <summary>The app's own and editable in every part: a Git app's saved port, or one of a new app's.</summary>
    Saved,
}

/// <summary>
/// A row of an app's Ports table (5.3, 5.9), whatever the app's source: one binding of a container port, or a port that
/// isn't published. A port published twice (on the LAN and on the host only, say) has a row for each binding.
/// </summary>
public sealed class ContainerPortRow
{
    public int? ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    /// <summary>Who reaches it. An added row starts on the LAN; Custom is only ever a binding the container has.</summary>
    public PortAccess Access { get; set; } = PortAccess.Lan;
    public int? HostPort { get; set; }
    /// <summary>
    /// The address Docker has it on ("", 0.0.0.0, 127.0.0.1 or one of the host's), so a row nobody touched is published
    /// exactly as before; null for a row that wasn't published.
    /// </summary>
    public string? HostIp { get; set; }
    public PortOrigin Origin { get; init; } = PortOrigin.Added;
    /// <summary>Docker picks its host port at every start (-P, or a port published without its host side), until it's kept or moved.</summary>
    public bool Random { get; set; }
    /// <summary>The host port Docker picked at this start, for [Keep 32768]; null while it's stopped.</summary>
    public int? Picked { get; init; }
    /// <summary>The web port: Open, domains, the health URL and scheduled jobs use it. At most one TCP row; Gitberth's own data.</summary>
    public bool IsWeb { get; set; }

    /// <summary>The container's own port, from its image or its bindings: its number and protocol show as text, and it stays.</summary>
    public bool Fixed => Origin is PortOrigin.Exposed or PortOrigin.Bound;

    /// <summary>A port Gitberth noted, or one added here: [x] takes it away.</summary>
    public bool Removable => !Fixed;

    /// <summary>Docker's key for it, 5432/tcp; null until it has a container port.</summary>
    public string? Key => ContainerPort is int port ? ContainerPorts.Key(port, PortRules.ProtocolOf(Protocol)) : null;

    /// <summary>Only noted: it isn't the container's own, and it isn't published. Nothing restarts for it.</summary>
    internal bool Noted => Origin is PortOrigin.Note or PortOrigin.Added && Access == PortAccess.Internal;

    /// <summary>The marker can go on it: a TCP port.</summary>
    public bool CanBeWeb => ContainerPort is not null && PortRules.ProtocolOf(Protocol) == "tcp";

    /// <summary>What the container is created with for the row, as the draft compares it: what its access doesn't use doesn't count.</summary>
    internal string Binding => Access == PortAccess.Internal
        ? $"{Access}:{Key}"
        : $"{Access}:{(Access == PortAccess.Custom ? HostIp?.Trim() : "")}:{HostPort}:{(Random ? "random" : "")}:{Key}";

    /// <summary>The row as the recreate takes it (<see cref="PortChanger"/>); null without a container port.</summary>
    public PortRule? Rule() => ContainerPort is int port
        ? new PortRule(port, PortRules.ProtocolOf(Protocol), Access, Access == PortAccess.Internal ? null : HostPort, HostIp)
        : null;

    /// <summary>The row as <see cref="Gitberth.Apps.AppSettingsService.PlanPortsAsync"/> and a Git app's settings take it.</summary>
    public PortRow ToPortRow() => new()
    {
        ContainerPort = ContainerPort, Protocol = PortRules.ProtocolOf(Protocol), Access = Access,
        HostPort = Access == PortAccess.Internal ? null : HostPort, HostIp = HostIp, IsWeb = IsWeb && CanBeWeb,
    };
}

/// <summary>
/// An app's ports as its Network tab edits them (5.3, 5.9), one draft for every source: the rows from the container's image,
/// its bindings and Gitberth's notes, or a Git app's saved ports, plus the web marker; and what changed since they were
/// loaded. A change to who reaches a port recreates the container (a Git app: a deploy without a build); a port only noted,
/// or the web marker moving, restarts nothing. The page's refresh never touches a draft; it loads a new one only while
/// nothing is being edited.
/// </summary>
public sealed class ContainerPortsDraft
{
    // Each loaded row, as the container had it (or as it was noted, or saved).
    private readonly Dictionary<ContainerPortRow, string> _loaded = new(ReferenceEqualityComparer.Instance);
    private readonly string _rules;
    private readonly string _notes;
    private readonly ContainerPortRow? _loadedWeb;
    private readonly string _loadedWebKey;

    private ContainerPortsDraft(List<ContainerPortRow> rows, IReadOnlyList<string> imageExposed, string loaded, bool owned)
    {
        Rows = rows;
        foreach (var row in rows) _loaded[row] = row.Binding;
        ImageExposed = imageExposed;
        Loaded = loaded;
        Owned = owned;
        _rules = RulesKey(rows);
        _notes = NotesKey(rows);
        _loadedWeb = rows.FirstOrDefault(r => r.IsWeb && r.CanBeWeb);
        _loadedWebKey = WebKey(_loadedWeb);
    }

    /// <summary>
    /// A draft of the container's ports: what it exposes and what it's published on (the ports Docker picked at this start
    /// for a running container, the ones it was created with for a stopped one), then the ports Gitberth noted for it that
    /// it doesn't have already.
    /// </summary>
    /// <param name="inspect">Its inspect (GET /containers/{id}/json).</param>
    /// <param name="image">The ports its image declares (EXPOSE), as 5432/tcp: only a hint on the card.</param>
    /// <param name="notes">The ports Gitberth noted for it.</param>
    /// <param name="webPort">The container port marked as the web port, if any (a TCP one).</param>
    public static ContainerPortsDraft From(JsonObject inspect, IEnumerable<string> image, IEnumerable<ContainerPortNote> notes, int? webPort = null)
    {
        var noted = notes.ToList();
        var current = ContainerPorts.Read(inspect);
        var rows = new List<ContainerPortRow>();
        foreach (var p in current)
        {
            if (p.Bindings.Count == 0)
                rows.Add(new ContainerPortRow { ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = PortAccess.Internal, Origin = PortOrigin.Exposed });
            foreach (var b in p.Bindings)
                rows.Add(new ContainerPortRow
                {
                    ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = b.Access, HostPort = b.HostPort, HostIp = b.HostIp,
                    Origin = PortOrigin.Bound, Random = b.Random, Picked = b.Random ? b.HostPort : null,
                });
        }
        var own = current.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var n in noted.OrderBy(n => n.Port).ThenBy(n => n.Protocol, StringComparer.Ordinal))
            if (own.Add(ContainerPorts.Key(n.Port, n.Protocol)))
                rows.Add(new ContainerPortRow { ContainerPort = n.Port, Protocol = n.Protocol, Access = PortAccess.Internal, Origin = PortOrigin.Note });
        Mark(rows, webPort);
        return new ContainerPortsDraft(rows, image.Distinct(StringComparer.Ordinal).ToList(), KeyOf(inspect, noted), owned: false);
    }

    /// <summary>A Git app's saved ports, in the order they were added: every part of every row can change.</summary>
    public static ContainerPortsDraft FromSaved(IEnumerable<PortMapping> saved, IEnumerable<string>? image = null)
    {
        var list = saved.OrderBy(p => p.Id).ToList();
        var rows = list.Select(p => new ContainerPortRow
        {
            ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = p.Access, HostPort = p.HostPort, HostIp = p.HostIp, IsWeb = p.IsWeb,
            Origin = PortOrigin.Saved,
        }).ToList();
        return new ContainerPortsDraft(rows, (image ?? []).Distinct(StringComparer.Ordinal).ToList(), AppSettingsModel.PortsKey(list), owned: true);
    }

    /// <summary>A new app's ports (New app's Configure step): its own, all of them editable.</summary>
    public static ContainerPortsDraft ForNew(IEnumerable<PortRule> rules, int? webPort, IEnumerable<string>? image = null)
    {
        var rows = rules.Select(r => new ContainerPortRow
        {
            ContainerPort = r.ContainerPort, Protocol = r.Protocol, Access = r.Access, HostPort = r.HostPort, HostIp = r.HostIp, Origin = PortOrigin.Saved,
        }).ToList();
        Mark(rows, webPort);
        return new ContainerPortsDraft(rows, (image ?? []).Distinct(StringComparer.Ordinal).ToList(), "", owned: true);
    }

    // The first TCP row of the web port carries the marker.
    private static void Mark(List<ContainerPortRow> rows, int? webPort)
    {
        if (webPort is not int web) return;
        var row = rows.Where(r => r.ContainerPort == web && r.CanBeWeb).OrderBy(r => r.Access == PortAccess.Internal ? 1 : 0).FirstOrDefault();
        if (row is not null) row.IsWeb = true;
    }

    /// <summary>
    /// The container and its ports as they are: its ID (a recreate gives it another), every binding (a restart picks new
    /// random ports) and the notes. A draft whose <see cref="Loaded"/> differs was started on something that's gone.
    /// </summary>
    public static string KeyOf(JsonObject inspect, IEnumerable<ContainerPortNote> notes) =>
        string.Join('|', ContainerPorts.Str(inspect, "Id"),
            string.Join(',', ContainerPorts.Rules(inspect).Select(r => $"{r.Key}:{r.Access}:{r.HostIp}:{r.HostPort}")),
            string.Join(',', notes.Select(n => ContainerPorts.Key(n.Port, n.Protocol)).Order(StringComparer.Ordinal)));

    /// <summary>The rows the Ports table edits.</summary>
    public List<ContainerPortRow> Rows { get; }

    /// <summary>What its image declares, for "Exposed by the image: 5432/tcp."</summary>
    public IReadOnlyList<string> ImageExposed { get; }

    /// <summary>
    /// The ports as they were loaded, for "changed since you started editing": <see cref="KeyOf"/> for a container, the
    /// saved ports' key (<see cref="AppSettingsModel.PortsKey(IEnumerable{PortMapping})"/>) for a Git app.
    /// </summary>
    public string Loaded { get; }

    /// <summary>Every row is the app's own (a Git app's, or a new app's): none is only a note.</summary>
    public bool Owned { get; }

    /// <summary>A row for [Add port]: on the LAN, the app's own when the rows are, else a note until it's published.</summary>
    public ContainerPortRow NewRow() => new() { Origin = Owned ? PortOrigin.Saved : PortOrigin.Added };

    /// <summary>The rows differ from the container, from what Gitberth noted for it, or in the web marker.</summary>
    public bool Dirty => NeedsRecreate || NotesChanged || WebChanged;

    /// <summary>The container has to be recreated for the rows to be live; ports only noted, and the web marker, don't need it.</summary>
    public bool NeedsRecreate => RulesKey(Rows) != _rules;

    /// <summary>A port was noted, or a note taken away: saving them restarts nothing.</summary>
    public bool NotesChanged => NotesKey(Rows) != _notes;

    /// <summary>The web row as it is now; null when no row is marked.</summary>
    public ContainerPortRow? Web => Rows.FirstOrDefault(r => r.IsWeb && r.CanBeWeb);

    /// <summary>The web marker is on another port than it was loaded on (or on none, or one now).</summary>
    public bool WebChanged => WebKey(Web) != _loadedWebKey;

    /// <summary>The row was loaded and nobody changed it: its host port is the container's own, so it isn't checked again.</summary>
    public bool Unchanged(ContainerPortRow row) => _loaded.TryGetValue(row, out var was) && was == row.Binding;

    /// <summary>The row was there when the draft was loaded (not added here).</summary>
    public bool WasLoaded(ContainerPortRow row) => _loaded.ContainsKey(row);

    /// <summary>
    /// The changes the bar counts: each row changed, added or taken away counts once, and so does the web marker moving
    /// between ports that were there before.
    /// </summary>
    public int Changes
    {
        get
        {
            var present = Rows.Where(r => r.ContainerPort is not null).ToHashSet(ReferenceEqualityComparer.Instance);
            var changes = Rows.Count(r => r.ContainerPort is not null && (!_loaded.TryGetValue(r, out var was) || was != r.Binding))
                + _loaded.Keys.Count(r => !present.Contains(r));
            var web = Web;
            // A port added as the web port, or the web port removed, is counted already.
            var added = web is not null && !_loaded.ContainsKey(web);
            var removed = _loadedWeb is not null && !present.Contains(_loadedWeb);
            if (!ReferenceEquals(web, _loadedWeb) && !added && !removed) changes++;
            return changes;
        }
    }

    /// <summary>
    /// Every port the container should have afterwards, for the recreate: its own ports as they're set now, and the noted
    /// or added ones that are published. Ones only noted aren't the container's business.
    /// </summary>
    public IReadOnlyList<PortRule> Rules() => Rows.Where(r => !r.Noted).Select(r => r.Rule()).OfType<PortRule>().ToList();

    /// <summary>The rows as the settings service plans them (<see cref="Gitberth.Apps.AppSettingsService.PlanPortsAsync"/>), web marker included.</summary>
    public IReadOnlyList<PortRow> ToPortRows() => Rows.Where(r => !r.Noted && r.ContainerPort is not null).Select(r => r.ToPortRow()).ToList();

    /// <summary>The container port marked as the web port; null when none is.</summary>
    public int? WebPort => Web?.ContainerPort;

    /// <summary>
    /// The ports to keep as notes: every noted or added one, published or not, so one whose recreate failed is still listed.
    /// Once the container has a port of its own, its note isn't shown. An app whose rows are all its own keeps none.
    /// </summary>
    public IReadOnlyList<(int Port, string Protocol)> Notes() => Owned ? []
        : Rows.Where(r => !r.Fixed && r.ContainerPort is not null).Select(r => (r.ContainerPort!.Value, PortRules.ProtocolOf(r.Protocol))).Distinct().ToList();

    /// <summary>Puts the web marker on the row, and off every other (or off all of them, for null).</summary>
    public void SetWeb(ContainerPortRow? row)
    {
        foreach (var r in Rows) r.IsWeb = ReferenceEquals(r, row) && r.CanBeWeb;
    }

    /// <summary>
    /// What's wrong with the rows, each under the field it concerns, in the words app ports use: a container port out of
    /// range or listed twice, a published row without a host port, two rows on one host port. Who else holds a host port
    /// is the field's own check.
    /// </summary>
    public List<PortProblem> Problems()
    {
        var problems = new List<PortProblem>();
        var bound = new List<(int Port, string Protocol, string Address)>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.ContainerPort is not int port || port is < 1 or > 65535)
            {
                problems.Add(new(i, PortField.ContainerPort, "Each port needs a container port between 1 and 65535."));
                continue;
            }
            var protocol = PortRules.ProtocolOf(row.Protocol);
            var at = i;
            // A container port can have several bindings of its own, but a noted or added one is a port it has already.
            if (!row.Fixed && Rows.Where((o, j) => j != at && o.Key == row.Key && (o.Fixed || j < at) && (o.Access == row.Access || !Owned)).Any())
                problems.Add(new(i, PortField.ContainerPort, $"{row.Key} is listed twice."));
            if (row.Access == PortAccess.Internal) continue;
            if (row.HostPort is not int hostPort || hostPort is < 1 or > 65535)
            {
                problems.Add(new(i, PortField.HostPort, "Pick a host port between 1 and 65535."));
                continue;
            }
            var address = row.Access switch
            {
                PortAccess.Custom => row.HostIp?.Trim() ?? "",
                PortAccess.HostOnly => "127.0.0.1",
                _ => row.HostIp is { } ip && ContainerPorts.AccessOf(ip) == PortAccess.Lan ? ip : "",
            };
            if (bound.Any(b => b.Port == hostPort && b.Protocol == protocol && ContainerPorts.Overlap(b.Address, address)))
                problems.Add(new(i, PortField.HostPort, $"Host port {hostPort} is listed twice."));
            bound.Add((hostPort, protocol, address));
        }
        return problems;
    }

    private static string WebKey(ContainerPortRow? row) => row?.Key ?? "";

    private static string RulesKey(IEnumerable<ContainerPortRow> rows) =>
        string.Join('|', rows.Where(r => !r.Noted && r.ContainerPort is not null).Select(r => r.Binding).Order(StringComparer.Ordinal));

    private static string NotesKey(IEnumerable<ContainerPortRow> rows) =>
        string.Join('|', rows.Where(r => !r.Fixed && r.Origin != PortOrigin.Saved && r.ContainerPort is not null).Select(r => r.Key).Distinct().Order(StringComparer.Ordinal));
}

/// <summary>The line on a standalone container's Ports card that says what applying the changes does (5.3).</summary>
public static class ContainerPortsBar
{
    /// <param name="recreate">The container has to be recreated for the change (<see cref="ContainerPortsDraft.NeedsRecreate"/>).</param>
    /// <param name="database">A database Gitberth recognizes, which takes longer to stop and start.</param>
    public static string Line(int changes, bool recreate, string name, bool running, bool database)
    {
        var count = changes == 1 ? "1 change." : $"{changes} changes.";
        if (!recreate) return $"{count} Nothing restarts.";
        if (!running) return $"{count} {name} is stopped: it's recreated stopped, so nothing goes offline.";
        return $"{count} Applying recreates {name}{(changes > 1 ? " once" : "")}: about {(database ? 5 : 3)} s offline.";
    }

    /// <summary>Docker's state changed under the draft: Apply is off until it's discarded.</summary>
    public static string Stale(string name) => $"{name} changed since you started editing.";
}

/// <summary>Where a row of a standalone container's Ports table is reached, as its Address column shows it.</summary>
public static class ContainerPortAddress
{
    /// <summary>
    /// The row's address: LAN at the LAN address of this host (a link for a web port, https for 443 and 8443), Host only at
    /// 127.0.0.1, a Custom row at its own address, and Internal by name while the container is on a named network. Null
    /// while the row has no container port, a published one no host port, or an Internal one no network to be named on.
    /// </summary>
    /// <param name="name">What containers on its network call it: its name.</param>
    /// <param name="named">It's on a named network (<see cref="ContainerAddresses.Network"/>): Docker's default bridge gives no names.</param>
    /// <param name="database">A database Gitberth recognizes: only its web console (RabbitMQ's 15672) is a link.</param>
    public static (string Text, string? Href)? Of(ContainerPortRow row, string lanHost, string name, bool named, bool database)
    {
        if (row.ContainerPort is not int port) return null;
        var web = row.IsWeb && row.CanBeWeb;
        if (row.Access == PortAccess.Internal)
            return named ? (web ? $"http://{ContainerPorts.Address(name, port)}" : ContainerPorts.Address(name, port), null) : null;
        if (row.HostPort is not int hostPort) return null;
        if (row.Access == PortAccess.HostOnly) return (ContainerPorts.Address("127.0.0.1", hostPort), null);
        var address = ContainerPorts.Address(row.Access == PortAccess.Custom ? row.HostIp?.Trim().Trim('[', ']') ?? "" : lanHost, hostPort);
        if (!AddressBook.IsWeb(port, PortRules.ProtocolOf(row.Protocol), database, appWebPort: web)) return (address, null);
        var url = $"{(port is 443 or 8443 ? "https" : "http")}://{address}";
        return (url, url);
    }
}

/// <summary>The services a container port is known for, shown faint next to it.</summary>
public static class PortServices
{
    /// <summary>"PostgreSQL" for 5432/tcp; null for a port it doesn't know.</summary>
    public static string? Name(int port, string protocol) => PortRules.ProtocolOf(protocol) != "tcp" ? null : port switch
    {
        5432 => "PostgreSQL",
        3306 => "MySQL",
        6379 => "Redis",
        27017 => "MongoDB",
        5672 => "AMQP",
        15672 => "RabbitMQ management",
        _ => null,
    };
}
