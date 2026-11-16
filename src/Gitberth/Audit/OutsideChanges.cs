using System.Text.Json;
using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Audit;

/// <summary>One event from Docker's stream as the audit log reads it: any type, any action, with its attributes.</summary>
/// <param name="Name">What it's about, by name: a container's or network's name, a volume's, an image's reference.</param>
public sealed record DockerChange(DateTime Time, string Type, string Action, string Id, string Name, IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>One line of the event stream; null for one that can't be read.</summary>
    public static DockerChange? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Str(root, "Type");
            var action = Str(root, "Action") is { Length: > 0 } a ? a : Str(root, "status");
            // "exec_start: sh -c …", "health_status: healthy": the action is the part before the colon.
            var colon = action.IndexOf(':');
            if (colon > 0) action = action[..colon].Trim();
            var actor = root.TryGetProperty("Actor", out var act) && act.ValueKind == JsonValueKind.Object ? act : default;
            var id = actor.ValueKind == JsonValueKind.Object && Str(actor, "ID") is { Length: > 0 } i ? i : Str(root, "id");
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (actor.ValueKind == JsonValueKind.Object && actor.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                foreach (var p in attrs.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) attributes[p.Name] = p.Value.GetString() ?? "";
            if (type.Length == 0) type = attributes.ContainsKey("image") ? "container" : "";
            var nanos = root.TryGetProperty("timeNano", out var tn) && tn.ValueKind == JsonValueKind.Number && tn.TryGetInt64(out var ns) ? ns
                : root.TryGetProperty("time", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var s) ? s * 1_000_000_000 : 0;
            if (type.Length == 0 || action.Length == 0 || id.Length == 0 || nanos <= 0) return null;
            var name = type switch
            {
                "volume" => id,
                "image" => attributes.TryGetValue("name", out var image) && image.Length > 0 ? image : id,
                _ => attributes.TryGetValue("name", out var n) ? n : "",
            };
            return new DockerChange(DateTime.UnixEpoch.AddTicks(nanos / 100), type, action, id, name, attributes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>A Docker change made outside Gitberth, ready to be recorded.</summary>
public sealed record OutsideChange(DateTime At, string Action, string Summary, string? Container, string? Target, IReadOnlyList<AuditChange> Changes);

/// <summary>
/// Tells Gitberth's own Docker changes from everyone else's, and says each other one once. Pure, driven by the events and a
/// clock. Left out: Gitberth's own changes (<see cref="ExpectedChanges"/>), its own container and its helpers, a crash
/// (die, oom: the Health page's), Docker's own restart of a container after it died (a start within
/// <see cref="RestartPolicyWindow"/> of a death no kill caused, with no stop or kill since; docker stop's events can say
/// "stop" before or after the death), and a network join or leave that's only part of a container starting or
/// stopping (see the remarks). A container's events a few seconds apart are one change: docker stop's kill, die and
/// stop are "stopped", docker restart's are "restarted", docker run's create and start are "created and started".
/// </summary>
/// <remarks>
/// <para>
/// A container's own start or stop joins and leaves its networks, but Docker says so apart from the container's events,
/// and on a busy engine seconds apart from them: the leave comes when the program has exited, after the kill, with the
/// stop and the death only once the container's files are unmounted; the join comes before the start, after the
/// volumes are mounted. So a join or leave is never told by how close in time its container's events are. It's
/// Gitberth's own when Gitberth is starting, stopping, restarting or removing that container right then (by its name or
/// ID); otherwise it waits for the container's next event: a join followed by its start, or a leave followed by its stop
/// or death, is part of that, and anything else (a kill: the container was still running) or nothing at all within
/// <see cref="JoinWait"/> makes it a change of its own, made with docker network connect or disconnect.
/// </para>
/// </remarks>
public sealed class OutsideChangeBuffer(ExpectedChanges expected, Func<DockerChange, bool> isGitberth)
{
    /// <summary>
    /// How long a network join or leave waits for its container's next event before it's taken to be a change of its own:
    /// a busy engine can take several seconds from the leave to the death, and from the join to the start.
    /// </summary>
    public static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(30);

    // Engines before Docker 25 said a container died before it left its networks: a leave this soon after the death is it.
    private static readonly TimeSpan LeftAfterDeath = TimeSpan.FromSeconds(10);

    // What Gitberth's own lifecycle commands cause that a join or leave of the container is part of.
    private static readonly string[] Starting = ["start", "create", "restart"];
    private static readonly string[] Stopping = ["die", "stop", "kill", "destroy"];

    /// <summary>How long a container's events are gathered after the last one before they're said.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>After a kill: docker stop waits up to 10 seconds for the program before it kills it and says "stop".</summary>
    public static readonly TimeSpan KillSettle = TimeSpan.FromSeconds(12);

    /// <summary>
    /// After a create: docker run's start follows at once on an idle engine, but a busy one can take several seconds to set
    /// the container up, and "Created and started" is still one change.
    /// </summary>
    public static readonly TimeSpan CreateSettle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long after a container died a start with no stop or kill since is Docker's restart policy: its back-off doubles
    /// from 100 ms up to a minute in a crash loop.
    /// </summary>
    public static readonly TimeSpan RestartPolicyWindow = TimeSpan.FromSeconds(65);

    /// <summary>
    /// How long after a kill (docker stop's signal, or docker kill's) the container's death is that kill's and not a crash:
    /// docker stop waits for the program for as long as the container's stop timeout says.
    /// </summary>
    public static readonly TimeSpan StopWindow = TimeSpan.FromMinutes(2);

    internal static readonly HashSet<string> ContainerActions = new(StringComparer.Ordinal)
    {
        "create", "destroy", "rename", "update", "start", "stop", "kill", "restart", "pause", "unpause",
    };

    internal static readonly HashSet<string> ImageActions = new(StringComparer.Ordinal) { "pull", "delete", "tag", "untag", "load", "import" };

    private static readonly HashSet<string> LifecycleActions = new(StringComparer.Ordinal)
    {
        "create", "start", "die", "stop", "kill", "destroy", "restart", "pause", "unpause",
    };

    private sealed class Group
    {
        public List<DockerChange> Events { get; } = [];
        public DateTime Due { get; set; }
    }

    private readonly Dictionary<string, Group> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Group> _images = new(StringComparer.Ordinal);
    // Network joins and leaves waiting for their container's next event, with when they came in.
    private readonly List<(DockerChange Change, DateTime Fed)> _networkJoins = [];
    private readonly List<OutsideChange> _ready = [];
    // Per container ID: when it last died, and whether a stop or kill came since; its other lifecycle events; its name.
    private readonly Dictionary<string, (DateTime At, bool StoppedSince)> _died = new(StringComparer.Ordinal);
    // Per container ID: when it was last sent a signal (a kill event), until the death it causes.
    private readonly Dictionary<string, DateTime> _signalled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(DateTime At, string Action)>> _lifecycle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, DateTime Seen)> _names = new(StringComparer.Ordinal);

    /// <summary>Takes one event in. What it completes comes out of <see cref="Flush"/>.</summary>
    public void Feed(DockerChange change, DateTime now)
    {
        switch (change.Type)
        {
            case "container":
                Container(change, now);
                break;
            case "image":
                if (!ImageActions.Contains(change.Action) || expected.Matches(change)) return;
                Gather(_images, change.Id, change, now + Settle);
                break;
            case "network":
                Network(change, now);
                break;
            case "volume":
                if (change.Action is not ("create" or "destroy") || Anonymous(change.Id) || expected.Matches(change)) return;
                _ready.Add(new OutsideChange(change.Time, $"docker.volume.{Verb(change.Action)}",
                    $"{(change.Action == "create" ? "Created" : "Removed")} the volume {change.Id} outside Gitberth.", null, change.Id, []));
                break;
        }
    }

    /// <summary>The changes whose events have settled by <paramref name="now"/>, oldest first.</summary>
    public List<OutsideChange> Flush(DateTime now)
    {
        foreach (var (id, group) in _containers.Where(g => g.Value.Due <= now).ToList())
        {
            _containers.Remove(id);
            _ready.AddRange(CollapseContainer(group.Events));
        }
        foreach (var (id, group) in _images.Where(g => g.Value.Due <= now).ToList())
        {
            _images.Remove(id);
            _ready.AddRange(CollapseImage(group.Events));
        }
        for (var i = 0; i < _networkJoins.Count; i++)
        {
            var (change, fed) = _networkJoins[i];
            var container = change.Attributes.GetValueOrDefault("container") ?? "";
            var part = PartOfLifecycle(change, container, waited: now - fed >= JoinWait);
            if (part is null) continue;
            _networkJoins.RemoveAt(i--);
            if (part.Value) continue;
            var who = NameOf(container);
            var network = change.Name.Length > 0 ? change.Name : Short(change.Id);
            _ready.Add(new OutsideChange(change.Time, $"docker.network.{Verb(change.Action)}",
                change.Action == "connect" ? $"Connected {who} to the network {network} outside Gitberth." : $"Disconnected {who} from the network {network} outside Gitberth.",
                who, network, []));
        }
        Forget(now);
        var ready = _ready.OrderBy(c => c.At).ToList();
        _ready.Clear();
        return ready;
    }

    private void Container(DockerChange change, DateTime now)
    {
        if (change.Name.Length > 0) _names[change.Id] = (change.Name, now);
        if (LifecycleActions.Contains(change.Action))
        {
            if (!_lifecycle.TryGetValue(change.Id, out var events)) _lifecycle[change.Id] = events = [];
            events.Add((change.Time, change.Action));
        }
        if (isGitberth(change) || change.Attributes.ContainsKey(HostScanHelper.Label)) return;
        switch (change.Action)
        {
            case "die":
                // A death a kill caused (docker stop says "stop" before or after it) is no crash: what follows is no restart policy's.
                var signalled = _signalled.Remove(change.Id, out var sent) && change.Time >= sent && change.Time - sent <= StopWindow;
                _died[change.Id] = (change.Time, signalled);
                return;
            case "stop" or "kill":
                if (change.Action == "kill") _signalled[change.Id] = change.Time;
                if (_died.TryGetValue(change.Id, out var died)) _died[change.Id] = died with { StoppedSince = true };
                break;
            case "start" when _died.TryGetValue(change.Id, out var crashed) && !crashed.StoppedSince && change.Time - crashed.At <= RestartPolicyWindow:
                // Docker's own restart after it died: the crash is the Health page's, and the start nobody's change.
                _died.Remove(change.Id);
                return;
        }
        if (change.Action == "start")
        {
            _died.Remove(change.Id);
            _signalled.Remove(change.Id);
        }
        if (!ContainerActions.Contains(change.Action) || expected.Matches(change)) return;
        // A create waits longer (CreateSettle) only while nothing has followed it: its next event, a start or anything else
        // (a builder's intermediate container is created and removed), ends that wait, so the change is said once it settles.
        var settle = change.Action switch { "kill" => KillSettle, "create" => CreateSettle, _ => Settle };
        if (_containers.TryGetValue(change.Id, out var waiting) && waiting.Events.Count > 0 && waiting.Events[^1].Action == "create")
            waiting.Due = now + settle;
        Gather(_containers, change.Id, change, now + settle);
    }

    private void Network(DockerChange change, DateTime now)
    {
        if (change.Action is not ("create" or "destroy" or "connect" or "disconnect")) return;
        var container = change.Attributes.GetValueOrDefault("container") ?? "";
        if (container.Length > 0 && isGitberth(change with { Type = "container", Id = container, Name = NameOf(container) })) return;
        if (expected.Matches(change)) return;
        if (change.Action is "connect" or "disconnect")
        {
            // Part of a start, stop, restart or removal of the container that Gitberth is making right then: its own.
            if (container.Length > 0 && GitberthsLifecycle(change, container)) return;
            _networkJoins.Add((change, now));
            return;
        }
        var name = change.Name.Length > 0 ? change.Name : Short(change.Id);
        _ready.Add(new OutsideChange(change.Time, $"docker.network.{Verb(change.Action)}",
            $"{(change.Action == "create" ? "Created" : "Removed")} the network {name} outside Gitberth.", null, name, []));
    }

    // Whether Gitberth expects the container (by its name or ID, never "any container") to start or stop when it joined
    // or left the network.
    private bool GitberthsLifecycle(DockerChange join, string container)
    {
        var probe = join with { Type = "container", Id = container, Name = NameOf(container), Attributes = new Dictionary<string, string>(StringComparer.Ordinal) };
        return (join.Action == "connect" ? Starting : Stopping).Any(action => expected.MatchesByName(probe with { Action = action }));
    }

    /// <summary>
    /// Whether a join or leave is part of its container starting or stopping: true or false once its container's events
    /// say (a join followed by the start; a leave followed by the stop or the death, or one that comes right after the
    /// death, as engines before Docker 25 say it); null while there's nothing yet to tell by and it hasn't
    /// <paramref name="waited"/> <see cref="JoinWait"/>, after which it's a change of its own.
    /// </summary>
    private bool? PartOfLifecycle(DockerChange join, string container, bool waited)
    {
        if (container.Length == 0 || !_lifecycle.TryGetValue(container, out var events)) return waited ? false : null;
        if (join.Action == "disconnect" && events.Where(e => e.At <= join.Time).OrderByDescending(e => e.At).FirstOrDefault() is { Action: "die" } died
            && join.Time - died.At <= LeftAfterDeath)
            return true;
        var next = events.Where(e => e.At > join.Time).OrderBy(e => e.At).Select(e => e.Action).FirstOrDefault();
        if (next is not null)
            return join.Action == "connect" ? next is "start" or "restart" : next is "die" or "stop";
        return waited ? false : null;
    }

    private static void Gather(Dictionary<string, Group> groups, string key, DockerChange change, DateTime due)
    {
        if (!groups.TryGetValue(key, out var group)) groups[key] = group = new Group();
        group.Events.Add(change);
        if (due > group.Due) group.Due = due;
    }

    // A container's settled events as the changes they make: kills inside a stop, a removal or a restart go with it, a
    // restart's stop and start are the restart, and a create followed by its start is one change.
    internal static List<OutsideChange> CollapseContainer(List<DockerChange> events)
    {
        var list = events.OrderBy(e => e.Time).ToList();
        var actions = list.Select(e => e.Action).ToList();
        if (actions.Contains("restart")) list.RemoveAll(e => e.Action is "stop" or "start" or "kill");
        if (actions.Contains("destroy")) list.RemoveAll(e => e.Action is "stop" or "kill");
        for (var i = 0; i < list.Count; i++)
            if (list[i].Action == "kill" && list.Skip(i + 1).Any(e => e.Action == "stop")) list.RemoveAt(i--);
        var result = new List<OutsideChange>();
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var name = e.Name.Length > 0 ? e.Name : Short(e.Id);
            if (e.Action == "create" && i + 1 < list.Count && list[i + 1].Action == "start")
            {
                result.Add(new OutsideChange(e.Time, "docker.container.created", $"Created and started outside Gitberth: {name}.", name, null, Image(e)));
                i++;
                continue;
            }
            var (summary, changes) = e.Action switch
            {
                "create" => ($"Created outside Gitberth: {name}.", Image(e)),
                "destroy" => ($"Removed outside Gitberth: {name}.", []),
                "rename" => e.Attributes.TryGetValue("oldName", out var old) && old.Length > 0
                    ? ($"Renamed outside Gitberth: {old.TrimStart('/')} is {name} now.", (IReadOnlyList<AuditChange>)[new AuditChange("Name", old.TrimStart('/'), name)])
                    : ($"Renamed outside Gitberth: {name}.", []),
                "update" => ($"Changed outside Gitberth with docker update: {name}.", []),
                "start" => ($"Started outside Gitberth: {name}.", []),
                "stop" => ($"Stopped outside Gitberth: {name}.", []),
                "kill" => ($"Killed outside Gitberth: {name}{(e.Attributes.TryGetValue("signal", out var signal) && signal.Length > 0 ? $" (signal {signal})" : "")}.", []),
                "restart" => ($"Restarted outside Gitberth: {name}.", []),
                "pause" => ($"Paused outside Gitberth: {name}.", []),
                "unpause" => ($"Unpaused outside Gitberth: {name}.", []),
                _ => ($"{e.Action} outside Gitberth: {name}.", (IReadOnlyList<AuditChange>)[]),
            };
            result.Add(new OutsideChange(e.Time, $"docker.container.{Verb(e.Action)}", summary, name, null, changes));
        }
        return result;
    }

    // An image's settled events: untagging it and deleting it is one removal.
    internal static List<OutsideChange> CollapseImage(List<DockerChange> events)
    {
        var list = events.OrderBy(e => e.Time).ToList();
        string Ref(DockerChange e) => list.Select(x => x.Name).FirstOrDefault(n => n.Length > 0 && !n.StartsWith("sha256:", StringComparison.Ordinal)) ?? Short(e.Id);
        if (list.Any(e => e.Action == "delete"))
        {
            var deleted = list.First(e => e.Action == "delete");
            return [new OutsideChange(deleted.Time, "docker.image.removed", $"Removed the image {Ref(deleted)} outside Gitberth.", null, Ref(deleted), [])];
        }
        return list.Select(e => new OutsideChange(e.Time, $"docker.image.{Verb(e.Action)}", e.Action switch
        {
            "pull" => $"Pulled the image {Ref(e)} outside Gitberth.",
            "tag" => $"Tagged an image {Ref(e)} outside Gitberth.",
            "untag" => $"Took the tag {Ref(e)} off an image outside Gitberth.",
            "load" => $"Loaded the image {Ref(e)} outside Gitberth.",
            "import" => $"Imported the image {Ref(e)} outside Gitberth.",
            _ => $"Changed the image {Ref(e)} outside Gitberth.",
        }, null, Ref(e), [])).ToList();
    }

    /// <summary>The verb an action is recorded with: docker.container.stopped, docker.image.pulled.</summary>
    internal static string Verb(string action) => action switch
    {
        "create" => "created",
        "destroy" or "delete" => "removed",
        "rename" => "renamed",
        "update" => "updated",
        "start" => "started",
        "stop" => "stopped",
        "kill" => "killed",
        "restart" => "restarted",
        "pause" => "paused",
        "unpause" => "unpaused",
        "pull" => "pulled",
        "tag" => "tagged",
        "untag" => "untagged",
        "load" => "loaded",
        "import" => "imported",
        "connect" => "connected",
        "disconnect" => "disconnected",
        _ => action,
    };

    private static IReadOnlyList<AuditChange> Image(DockerChange e) =>
        e.Attributes.TryGetValue("image", out var image) && image.Length > 0 ? [new AuditChange("Image", null, image)] : [];

    private string NameOf(string containerId) =>
        containerId.Length == 0 ? "a container" : _names.TryGetValue(containerId, out var known) ? known.Name : Short(containerId);

    private static string Short(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..Math.Min(id.Length, 19)] : id[..Math.Min(id.Length, 12)];

    private static bool Anonymous(string volume) => volume.Length == 64 && volume.All(char.IsAsciiHexDigitLower);

    private void Forget(DateTime now)
    {
        var old = now - TimeSpan.FromMinutes(10);
        foreach (var key in _died.Where(d => d.Value.At < old).Select(d => d.Key).ToList()) _died.Remove(key);
        foreach (var key in _signalled.Where(d => d.Value < old).Select(d => d.Key).ToList()) _signalled.Remove(key);
        foreach (var key in _names.Where(d => d.Value.Seen < old).Select(d => d.Key).ToList()) _names.Remove(key);
        foreach (var (key, events) in _lifecycle.ToList())
        {
            events.RemoveAll(e => e.At < old);
            if (events.Count == 0) _lifecycle.Remove(key);
        }
    }
}

/// <summary>
/// Follows Docker's events for the audit log, apart from the Health page's watcher: containers, images, networks and volumes.
/// Each change Gitberth didn't make (<see cref="OutsideChangeBuffer"/>) is recorded with the actor "outside Gitberth", against
/// the app whose container it was. Settings › Audit log turns it off (<see cref="AuditLog.RecordOutside"/>). It starts from
/// the moment Gitberth starts: what happened while Gitberth was away isn't known to be anyone's.
/// </summary>
public sealed class OutsideChanges(DockerApi docker, ExpectedChanges expected, AuditLog audit, GitberthOptions options, IDbContextFactory<GitberthDb> dbFactory,
    ILogger<OutsideChanges> logger) : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    /// <summary>The events asked for: everything a change makes, and die to know Docker's own restarts.</summary>
    internal const string Filters = """{"type":["container","image","network","volume"],"event":["create","destroy","rename","update","start","stop","kill","restart","pause","unpause","die","pull","delete","tag","untag","load","import","connect","disconnect"]}""";

    private readonly Lock _lock = new();
    private OutsideChangeBuffer? _buffer;
    private readonly HashSet<(DateTime, string, string)> _seen = [];
    private Dictionary<string, (int Id, string Name)> _apps = new(StringComparer.Ordinal);
    private DateTime _appsRead = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _buffer = new OutsideChangeBuffer(expected, IsGitberth);
        var since = DateTime.UtcNow;
        using var flushing = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var flush = FlushLoopAsync(flushing, stoppingToken);
        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var line in docker.StreamEventLinesAsync(since, Filters, stoppingToken))
                {
                    if (failing) logger.LogInformation("The audit log follows Docker's events again");
                    failing = false;
                    if (DockerChange.Parse(line) is not { } change) continue;
                    lock (_lock)
                    {
                        // The engine repeats events from `since` on after a reconnect.
                        if (!_seen.Add((change.Time, change.Id, change.Action))) continue;
                        if (change.Time > since) since = change.Time;
                        _buffer.Feed(change, DateTime.UtcNow);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!failing) logger.LogWarning("The audit log stopped following Docker's events ({Reason}); trying again every {Seconds} seconds", ex.Message, Retry.TotalSeconds);
                failing = true;
            }
            try
            {
                await Task.Delay(Retry, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        await flush;
    }

    private async Task FlushLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<OutsideChange> ready;
                lock (_lock)
                {
                    ready = _buffer!.Flush(DateTime.UtcNow);
                    if (_seen.Count > 5000) _seen.RemoveWhere(k => k.Item1 < DateTime.UtcNow.AddMinutes(-30));
                }
                if (ready.Count == 0) continue;
                await RecordAsync(ready, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RecordAsync(List<OutsideChange> ready, CancellationToken ct)
    {
        if (!audit.RecordOutside) return;
        try
        {
            if (DateTime.UtcNow - _appsRead > TimeSpan.FromSeconds(30)) await ReadAppsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't read the apps for the audit log");
        }
        foreach (var change in ready)
        {
            var app = change.Container is { } c && _apps.TryGetValue(c, out var known) ? known : ((int Id, string Name)?)null;
            audit.Record(new AuditEntry(change.Action, change.Summary)
            {
                Actor = AuditActor.Outside,
                At = change.At,
                AppId = app?.Id,
                AppName = app?.Name,
                Container = change.Container,
                Target = change.Target,
                Changes = change.Changes,
            });
        }
    }

    private async Task ReadAppsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Apps.AsNoTracking().Select(a => new { a.Id, a.Name, a.Container, a.Slug }).ToListAsync(ct);
        _apps = rows.GroupBy(a => a.Container ?? AppDefinition.ContainerNameFor(a.Slug), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Name), StringComparer.Ordinal);
        _appsRead = DateTime.UtcNow;
    }

    // Gitberth's own container: by its ID (or the start of it, its hostname), or its Compose project and service.
    private bool IsGitberth(DockerChange change)
    {
        if (options.OwnContainer is { Length: > 0 } own && (change.Id.StartsWith(own, StringComparison.Ordinal) || change.Name == own)) return true;
        return change.Attributes.TryGetValue("com.docker.compose.project", out var project) && project == "gitberth"
            && change.Attributes.TryGetValue("com.docker.compose.service", out var service) && service == "gitberth";
    }
}
