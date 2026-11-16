using System.Text.Json.Nodes;

namespace Gitberth.Audit;

/// <summary>
/// What Gitberth is about to change in Docker, so the events those changes cause are known as its own and never recorded
/// as changes made outside Gitberth. Every path that changes Docker registers here first, at the lowest layer: each mutating
/// docker CLI command (<see cref="ForCli"/>) and Engine API call (<see cref="ForApi"/>). An expectation names the event type,
/// the actions it causes, and what it's about (a container, network or volume by name or ID, a name prefix for bulk work,
/// or anything of the type for an image prune). It holds while the command runs and for <see cref="Grace"/> after, by the
/// events' own times, and is remembered for <see cref="Kept"/> so events Docker replays after a reconnect still match.
/// </summary>
public sealed class ExpectedChanges
{
    /// <summary>How long after a command ends its events still count as its own.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    /// <summary>How long a command that never says it ended is taken to run, at most.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(30);

    /// <summary>How long an ended expectation is remembered, for events replayed after the stream was away.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(20);

    // Events a little before the command started (clocks, rounding) still count.
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly List<Expectation> _items = [];

    /// <summary>Stands in for the clock in tests.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>How many expectations are remembered (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (_lock) return _items.Count;
        }
    }

    // ---- Registering ----

    /// <summary>Gitberth is about to cause <paramref name="actions"/> on these containers, networks or volumes (by name or ID).</summary>
    public IDisposable Expect(string type, IReadOnlyCollection<string> actions, params string[] subjects) =>
        Add(type, actions, subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Clean).ToArray(), prefix: null, any: false);

    /// <summary>Gitberth is about to cause <paramref name="actions"/> on whatever's named with this prefix (bulk work, a Compose project).</summary>
    public IDisposable ExpectPrefix(string type, IReadOnlyCollection<string> actions, string prefix) => Add(type, actions, [], Clean(prefix), any: false);

    /// <summary>Gitberth is about to cause <paramref name="actions"/> on things of this type it can't name beforehand (an image prune).</summary>
    public IDisposable ExpectAny(string type, IReadOnlyCollection<string> actions) => Add(type, actions, [], prefix: null, any: true);

    /// <summary>The expectations of a docker CLI command, held until the returned lease is disposed (when the command ended).</summary>
    public IDisposable ForCli(IReadOnlyList<string> args) => Combine(CliExpectations(args));

    /// <summary>The expectations of an Engine API call (<paramref name="path"/> without the version), held until the lease is disposed.</summary>
    public IDisposable ForApi(HttpMethod method, string path, JsonNode? body = null) => Combine(ApiExpectations(method, path, body));

    // ---- Matching ----

    /// <summary>Whether Gitberth expected this event: one of its own changes caused it.</summary>
    public bool Matches(DockerChange change) => Matches(change, byName: false);

    /// <summary>
    /// Whether Gitberth expected this event of what it names, by its name, ID or a name prefix: an expectation of anything
    /// of the type (an unnamed docker run, an image prune) doesn't count.
    /// </summary>
    public bool MatchesByName(DockerChange change) => Matches(change, byName: true);

    private bool Matches(DockerChange change, bool byName)
    {
        var now = Now();
        lock (_lock)
        {
            Forget(now);
            foreach (var e in _items)
                if (e.Covers(change, now, byName)) return true;
        }
        return false;
    }

    // ---- What each command causes ----

    internal static readonly string[] Created = ["create", "start"];
    internal static readonly string[] Stopped = ["kill", "die", "stop"];
    internal static readonly string[] Restarted = ["kill", "die", "stop", "start", "restart"];
    internal static readonly string[] Removed = ["kill", "die", "stop", "destroy"];
    internal static readonly string[] AnyContainer = ["create", "start", "kill", "die", "stop", "destroy", "restart", "rename", "update", "pause", "unpause"];
    internal static readonly string[] ImagePull = ["pull", "tag", "untag"];
    internal static readonly string[] ImageRemove = ["untag", "delete"];
    internal static readonly string[] AnyImage = ["pull", "tag", "untag", "delete", "load", "import"];
    internal static readonly string[] AnyNetwork = ["create", "destroy", "connect", "disconnect"];
    internal static readonly string[] AnyVolume = ["create", "destroy"];

    /// <param name="Project">A Compose project whose own containers, networks and volumes it covers (<see cref="OfProject"/>).</param>
    internal sealed record Spec(string Type, string[] Actions, string[] Subjects, string? Prefix, bool Any, string? Project = null);

    /// <summary>What a docker CLI command (its arguments, without "docker") changes; empty for one that only reads.</summary>
    internal static List<Spec> CliExpectations(IReadOnlyList<string> args)
    {
        var specs = new List<Spec>();
        if (args.Count == 0) return specs;
        // Global options before the command (--host, --context, --log-level) aren't Gitberth's; skipped all the same.
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-')) i += args[i].Contains('=') ? 1 : 2;
        if (i >= args.Count) return specs;
        var command = args[i];
        var rest = args.Skip(i + 1).ToList();
        // `docker container rm`, `docker image rm`, `docker network create`… and their short forms.
        switch (command)
        {
            case "container":
                return rest.Count > 0 ? ContainerCommand(rest[0], rest.Skip(1).ToList(), specs) : specs;
            case "image":
                return rest.Count > 0 ? ImageCommand(rest[0], rest.Skip(1).ToList(), specs) : specs;
            case "network":
                return rest.Count > 0 ? NetworkCommand(rest[0], rest.Skip(1).ToList(), specs) : specs;
            case "volume":
                return rest.Count > 0 ? VolumeCommand(rest[0], rest.Skip(1).ToList(), specs) : specs;
            case "buildx":
                return rest.Count > 0 && rest[0] is "build" or "b" ? ImageCommand("build", rest.Skip(1).ToList(), specs) : specs;
            case "builder":
                return specs;
            case "compose":
                return ComposeCommand(rest, specs);
            case "system":
                if (rest.Count > 0 && rest[0] == "prune")
                {
                    specs.Add(new Spec("container", ["destroy"], [], null, true));
                    specs.Add(new Spec("image", ImageRemove, [], null, true));
                    specs.Add(new Spec("network", ["destroy"], [], null, true));
                    specs.Add(new Spec("volume", ["destroy"], [], null, true));
                }
                return specs;
            case "rmi":
                return ImageCommand("rm", rest, specs);
            case "pull" or "build" or "tag" or "load" or "import" or "commit":
                return ImageCommand(command, rest, specs);
            default:
                return ContainerCommand(command, rest, specs);
        }
    }

    private static List<Spec> ContainerCommand(string verb, List<string> rest, List<Spec> specs)
    {
        var positional = Positional(rest, ValueOptions);
        switch (verb)
        {
            case "run" or "create":
            {
                var name = OptionValue(rest, "--name");
                // Without a name Docker picks one: anything created and started in the meantime counts.
                specs.Add(name is null
                    ? new Spec("container", verb == "run" ? AnyContainer : ["create"], [], null, true)
                    : new Spec("container", verb == "run" ? [.. Created, "die", "kill", "stop", "destroy"] : ["create"], [name], null, false));
                // A named volume that doesn't exist yet is made with it, and a missing image pulled.
                specs.Add(new Spec("volume", ["create"], [], null, true));
                specs.Add(new Spec("image", ImagePull, [], null, true));
                return specs;
            }
            case "start":
                specs.Add(new Spec("container", ["start"], positional.ToArray(), null, false));
                return specs;
            case "stop":
                specs.Add(new Spec("container", Stopped, positional.ToArray(), null, false));
                return specs;
            case "kill":
                specs.Add(new Spec("container", ["kill", "die"], positional.ToArray(), null, false));
                return specs;
            case "restart":
                specs.Add(new Spec("container", Restarted, positional.ToArray(), null, false));
                return specs;
            case "rm" or "remove":
                specs.Add(new Spec("container", Removed, positional.ToArray(), null, false));
                if (rest.Contains("-v") || rest.Contains("--volumes")) specs.Add(new Spec("volume", ["destroy"], [], null, true));
                return specs;
            case "rename":
                specs.Add(new Spec("container", ["rename"], positional.ToArray(), null, false));
                return specs;
            case "update":
                specs.Add(new Spec("container", ["update"], positional.ToArray(), null, false));
                return specs;
            case "pause":
                specs.Add(new Spec("container", ["pause"], positional.ToArray(), null, false));
                return specs;
            case "unpause":
                specs.Add(new Spec("container", ["unpause"], positional.ToArray(), null, false));
                return specs;
            case "prune":
                specs.Add(new Spec("container", ["destroy"], [], null, true));
                return specs;
            default:
                return specs;
        }
    }

    private static List<Spec> ImageCommand(string verb, List<string> rest, List<Spec> specs)
    {
        switch (verb)
        {
            case "pull":
                specs.Add(new Spec("image", ImagePull, [], null, true));
                return specs;
            case "rm" or "remove":
            case "prune":
                specs.Add(new Spec("image", ImageRemove, [], null, true));
                return specs;
            case "tag":
                specs.Add(new Spec("image", ["tag", "untag"], [], null, true));
                return specs;
            case "build":
                // BuildKit tags what it built (moving the tag off an older image), and pulls what it needs.
                specs.Add(new Spec("image", AnyImage, [], null, true));
                return specs;
            case "load" or "import" or "commit":
                specs.Add(new Spec("image", AnyImage, [], null, true));
                return specs;
            default:
                return specs;
        }
    }

    private static List<Spec> NetworkCommand(string verb, List<string> rest, List<Spec> specs)
    {
        var positional = Positional(rest, NetworkValueOptions);
        switch (verb)
        {
            case "create":
                if (positional.Count > 0) specs.Add(new Spec("network", ["create"], [positional[^1]], null, false));
                return specs;
            case "rm" or "remove":
                specs.Add(new Spec("network", ["destroy"], positional.ToArray(), null, false));
                return specs;
            case "connect":
                if (positional.Count > 0) specs.Add(new Spec("network", ["connect"], [positional[0]], null, false));
                return specs;
            case "disconnect":
                if (positional.Count > 0) specs.Add(new Spec("network", ["disconnect"], [positional[0]], null, false));
                return specs;
            case "prune":
                specs.Add(new Spec("network", ["destroy"], [], null, true));
                return specs;
            default:
                return specs;
        }
    }

    private static List<Spec> VolumeCommand(string verb, List<string> rest, List<Spec> specs)
    {
        var positional = Positional(rest, ["--driver", "-d", "--label", "--opt", "-o", "--name"]);
        switch (verb)
        {
            case "create":
                specs.Add(OptionValue(rest, "--name") is { } named ? new Spec("volume", ["create"], [named], null, false)
                    : positional.Count > 0 ? new Spec("volume", ["create"], [positional[0]], null, false)
                    : new Spec("volume", ["create"], [], null, true));
                return specs;
            case "rm" or "remove":
                specs.Add(new Spec("volume", ["destroy"], positional.ToArray(), null, false));
                return specs;
            case "prune":
                specs.Add(new Spec("volume", ["destroy"], [], null, true));
                return specs;
            default:
                return specs;
        }
    }

    // `docker compose -p <project> …`: its containers carry its label, and its networks and volumes (and every member, as
    // Gitberth names them) are <project>_…; another project that only starts with the same name isn't covered.
    private static List<Spec> ComposeCommand(List<string> rest, List<Spec> specs)
    {
        var project = OptionValue(rest, "-p") ?? OptionValue(rest, "--project-name");
        var verb = Positional(rest, ["-p", "--project-name", "-f", "--file", "--env-file", "--profile", "--project-directory", "--ansi", "--progress", "--parallel"])
            .FirstOrDefault();
        if (verb is null or "config" or "ps" or "ls" or "logs" or "images" or "top" or "version" or "port" or "events") return specs;
        if (project is null)
        {
            specs.Add(new Spec("container", AnyContainer, [], null, true));
            specs.Add(new Spec("network", AnyNetwork, [], null, true));
            specs.Add(new Spec("volume", AnyVolume, [], null, true));
        }
        else
        {
            specs.Add(new Spec("container", AnyContainer, [], null, false, project));
            specs.Add(new Spec("network", AnyNetwork, [], null, false, project));
            specs.Add(new Spec("volume", AnyVolume, [], null, false, project));
        }
        specs.Add(new Spec("image", AnyImage, [], null, true));
        return specs;
    }

    /// <summary>What an Engine API call changes; empty for a read.</summary>
    internal static List<Spec> ApiExpectations(HttpMethod method, string path, JsonNode? body)
    {
        var specs = new List<Spec>();
        if (method == HttpMethod.Get || method == HttpMethod.Head) return specs;
        var query = "";
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) (path, query) = (path[..q], path[(q + 1)..]);
        var parts = path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length == 0) return specs;
        switch (parts[0])
        {
            case "containers" when parts.Length == 2 && parts[1] == "create":
                specs.Add(Query(query, "name") is { Length: > 0 } name
                    ? new Spec("container", ["create"], [name], null, false)
                    : new Spec("container", ["create"], [], null, true));
                specs.Add(new Spec("volume", ["create"], [], null, true));
                return specs;
            case "containers" when parts.Length == 2 && parts[1] == "prune":
                specs.Add(new Spec("container", ["destroy"], [], null, true));
                return specs;
            case "containers" when parts.Length == 2 && method == HttpMethod.Delete:
                specs.Add(new Spec("container", Removed, [parts[1]], null, false));
                return specs;
            case "containers" when parts.Length >= 3:
            {
                var id = parts[1];
                string[]? actions = parts[2] switch
                {
                    "start" => ["start"],
                    "stop" => Stopped,
                    "kill" => ["kill", "die"],
                    "restart" => Restarted,
                    "update" => ["update"],
                    "pause" => ["pause"],
                    "unpause" => ["unpause"],
                    "rename" => ["rename"],
                    _ => null,
                };
                if (actions is null) return specs;
                var subjects = parts[2] == "rename" && Query(query, "name") is { Length: > 0 } to ? new[] { id, to } : [id];
                specs.Add(new Spec("container", actions, subjects, null, false));
                return specs;
            }
            case "networks" when parts.Length == 2 && parts[1] == "create":
                specs.Add(body?["Name"] is JsonValue n && n.TryGetValue<string>(out var network)
                    ? new Spec("network", ["create"], [network], null, false)
                    : new Spec("network", ["create"], [], null, true));
                return specs;
            case "networks" when parts.Length == 2 && parts[1] == "prune":
                specs.Add(new Spec("network", ["destroy"], [], null, true));
                return specs;
            case "networks" when parts.Length == 2 && method == HttpMethod.Delete:
                specs.Add(new Spec("network", ["destroy"], [parts[1]], null, false));
                return specs;
            case "networks" when parts.Length == 3 && parts[2] is "connect" or "disconnect":
                specs.Add(new Spec("network", [parts[2]], [parts[1]], null, false));
                return specs;
            case "images" when parts.Length == 2 && parts[1] == "create":
                specs.Add(new Spec("image", ImagePull, [], null, true));
                return specs;
            case "images" when parts.Length == 2 && parts[1] is "load":
                specs.Add(new Spec("image", AnyImage, [], null, true));
                return specs;
            case "images" when parts.Length == 2 && parts[1] == "prune":
            case "images" when parts.Length >= 2 && method == HttpMethod.Delete:
                specs.Add(new Spec("image", ImageRemove, [], null, true));
                return specs;
            case "images" when parts.Length >= 3 && parts[^1] == "tag":
                specs.Add(new Spec("image", ["tag", "untag"], [], null, true));
                return specs;
            case "build":
                if (parts.Length == 1) specs.Add(new Spec("image", AnyImage, [], null, true));
                return specs;
            case "commit":
                specs.Add(new Spec("image", AnyImage, [], null, true));
                return specs;
            case "volumes" when parts.Length == 2 && parts[1] == "create":
                specs.Add(body?["Name"] is JsonValue v && v.TryGetValue<string>(out var volume) && volume.Length > 0
                    ? new Spec("volume", ["create"], [volume], null, false)
                    : new Spec("volume", ["create"], [], null, true));
                return specs;
            case "volumes" when parts.Length == 2 && parts[1] == "prune":
                specs.Add(new Spec("volume", ["destroy"], [], null, true));
                return specs;
            case "volumes" when parts.Length == 2 && method == HttpMethod.Delete:
                specs.Add(new Spec("volume", ["destroy"], [parts[1]], null, false));
                return specs;
            default:
                return specs;
        }
    }

    // ---- The parts ----

    // Options of `docker run/create/update/…` that take a value, so their value isn't read as the container's name.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-t", "--time", "--timeout", "-s", "--signal", "--name", "-e", "--env", "--env-file", "-p", "--publish", "-v", "--volume", "--mount", "--network", "--net",
        "--network-alias", "-l", "--label", "--restart", "-m", "--memory", "--memory-reservation", "--memory-swap", "--cpus", "--cpu-shares", "-c", "--cpuset-cpus",
        "--pids-limit", "--user", "-u", "-w", "--workdir", "--entrypoint", "--hostname", "-h", "--add-host", "--dns", "--log-driver", "--log-opt", "--stop-signal",
        "--stop-timeout", "--health-cmd", "--health-interval", "--health-retries", "--health-timeout", "--health-start-period", "--shm-size", "--ulimit",
        "--cap-add", "--cap-drop", "--device", "--gpus", "--security-opt", "--sysctl", "--tmpfs", "--ip", "--ip6", "--mac-address", "--platform", "--expose",
        "--blkio-weight", "--kernel-memory", "--cpu-period", "--cpu-quota", "--detach-keys", "--format", "--filter",
    };

    private static readonly HashSet<string> NetworkValueOptions = new(StringComparer.Ordinal)
    {
        "--alias", "--driver", "-d", "--gateway", "--ip", "--ip6", "--ip-range", "--label", "--opt", "-o", "--subnet", "--aux-address", "--ipam-driver",
        "--ipam-opt", "--link", "--link-local-ip", "--driver-opt", "--gw-priority", "--scope", "--config-from", "--mac-address",
    };

    // The arguments that aren't options or option values.
    private static List<string> Positional(List<string> args, IReadOnlyCollection<string> valueOptions)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                result.AddRange(args.Skip(i + 1));
                break;
            }
            if (a.StartsWith('-') && a.Length > 1)
            {
                if (!a.Contains('=') && valueOptions.Contains(a)) i++;
                continue;
            }
            result.Add(a);
        }
        return result;
    }

    private static string? OptionValue(List<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == option && i + 1 < args.Count) return args[i + 1];
            if (args[i].StartsWith(option + "=", StringComparison.Ordinal)) return args[i][(option.Length + 1)..];
        }
        return null;
    }

    private static string? Query(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal)) return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string Clean(string subject) => subject.Trim().TrimStart('/');

    private IDisposable Combine(List<Spec> specs)
    {
        if (specs.Count == 0) return Nothing.Instance;
        var leases = specs.Select(s => Add(s.Type, s.Actions, s.Subjects.Where(x => x.Length > 0).Select(Clean).ToArray(), s.Prefix is null ? null : Clean(s.Prefix), s.Any,
            s.Project)).ToList();
        return leases.Count == 1 ? leases[0] : new Leases(leases);
    }

    private Expectation Add(string type, IReadOnlyCollection<string> actions, string[] subjects, string? prefix, bool any, string? project = null)
    {
        var now = Now();
        var e = new Expectation(this, type, actions.ToHashSet(StringComparer.Ordinal), subjects, prefix, any, now, project);
        lock (_lock)
        {
            Forget(now);
            _items.Add(e);
        }
        return e;
    }

    private void Ended(Expectation e)
    {
        lock (_lock) e.End = Now();
    }

    // Under the lock: what ended long enough ago goes.
    private void Forget(DateTime now) => _items.RemoveAll(e => (e.End ?? e.Start + Longest) + Grace + Kept < now);

    private sealed class Expectation(ExpectedChanges owner, string type, HashSet<string> actions, string[] subjects, string? prefix, bool any, DateTime start,
        string? project) : IDisposable
    {
        public DateTime Start { get; } = start;
        public DateTime? End { get; set; }

        public bool Covers(DockerChange change, DateTime now, bool byName)
        {
            if (change.Type != type || !actions.Contains(change.Action)) return false;
            var until = (End ?? Min(now, Start + Longest)) + Grace;
            if (change.Time < Start - Early || change.Time > until) return false;
            if (any) return !byName;
            if (project is not null) return OfProject(change, project);
            if (prefix is not null) return Named(change).Any(n => n.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var subject in subjects)
            {
                if (Named(change).Any(n => n == subject)) return true;
                // A short ID names the container or network it begins.
                if (subject.Length >= 12 && change.Id.StartsWith(subject, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        public void Dispose() => owner.Ended(this);
    }

    /// <summary>The label docker compose puts on its project's containers.</summary>
    internal const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>
    /// Whether an event is about a Compose project's own container, network or volume: a container whose project label is
    /// it (or, without the label, a name &lt;project&gt;_…, as Gitberth names every member), a network or volume named
    /// &lt;project&gt;_…. Never another project whose name only starts the same (gb-shop-pr-1, gb-shopify).
    /// </summary>
    internal static bool OfProject(DockerChange change, string project)
    {
        if (change.Type == "container" && change.Attributes.TryGetValue(ComposeProjectLabel, out var label)) return label == project;
        var own = project + "_";
        foreach (var name in Named(change))
            if (name.StartsWith(own, StringComparison.Ordinal) || ReplacementOf(name, own)) return true;
        return false;
    }

    // Everything an event calls its subject: its ID, its name, a rename's old name, a network's name.
    private static IEnumerable<string> Named(DockerChange change)
    {
        yield return change.Id;
        if (change.Name.Length > 0) yield return change.Name;
        if (change.Attributes.TryGetValue("oldName", out var old) && old.Length > 0) yield return old.TrimStart('/');
        if (change.Type == "network" && change.Attributes.TryGetValue("name", out var network)) yield return network;
    }

    /// <summary>
    /// Whether a container's name is docker compose's for the new container of a service it recreates: the old one's ID (12
    /// characters) and '_' before the service's own name (gb-shop_web), which it's renamed to once the old one is gone.
    /// </summary>
    /// <param name="prefix">The project's own names' start, with its '_': gb-shop_.</param>
    internal static bool ReplacementOf(string name, string prefix) =>
        name.Length > 13 && name[12] == '_' && name.AsSpan(0, 12).IndexOfAnyExcept("0123456789abcdef") < 0
        && name.AsSpan(13).StartsWith(prefix, StringComparison.Ordinal);

    private sealed class Leases(List<Expectation> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
