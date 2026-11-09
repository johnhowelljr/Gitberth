using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;

namespace Gitberth.Network;

/// <summary>Whether Gitberth may recreate a container to change its ports, and if not, why.</summary>
public enum RecreateKind
{
    Allowed,
    Missing,
    /// <summary>A short-lived helper Gitberth runs (label gitberth.helper).</summary>
    Helper,
    /// <summary>Gitberth's own container: stopping it would stop the change halfway.</summary>
    Self,
    /// <summary>A Gitberth app's container: its ports are the app's, applied by a deploy.</summary>
    App,
    /// <summary>A deploy's temporary container (_next) or the one it moved aside (_prev).</summary>
    DeployTemporary,
    /// <summary>Docker Compose would undo the change: Gitberth gives the YAML and the command instead.</summary>
    Compose,
    Swarm,
    Kubernetes,
    HostNetwork,
    /// <summary>It runs in another container's network (network mode container:x): its ports are that one's.</summary>
    SharesNetwork,
    NoNetwork,
    /// <summary>Other containers run in its network, process or IPC namespace, which a recreate would take away.</summary>
    SharedByOthers,
    /// <summary>Other containers --link to it: their next start would fail.</summary>
    LinkedTo,
    /// <summary>Started with --rm: stopping it deletes it.</summary>
    AutoRemove,
    EngineTooOld,
    Paused,
    Restarting,
    /// <summary>Its network is gone and it's on no other network, so there's nothing to create it on.</summary>
    NetworkGone,
    /// <summary>A change to it is running, or waits for a start or for the owner's decision.</summary>
    Busy,
}

/// <param name="Reason">The one line the Ports card shows, in the design's words; null when allowed.</param>
/// <param name="Compose">Where its Compose file is, for Compose-managed containers and Gitberth itself.</param>
/// <param name="Link">The container a link goes to: vpn, for "Shares the network of vpn".</param>
public sealed record RecreateVerdict(RecreateKind Kind, string? Reason = null, ComposeService? Compose = null, string? Link = null)
{
    public static readonly RecreateVerdict Yes = new(RecreateKind.Allowed);

    public bool Allowed => Kind == RecreateKind.Allowed;
}

/// <param name="Engine">The engine's version: changing ports needs API 1.44.</param>
/// <param name="Self">Gitberth's own container as it knows itself (its ID, or its hostname, which is the short ID); null outside one.</param>
/// <param name="Others">Every other container's inspect: who shares its namespaces or links to it.</param>
/// <param name="Networks">GET /networks.</param>
/// <param name="LanAddress">The LAN address of this host, for the host-network line.</param>
/// <param name="Busy">Why a change to it can't start now (one is running, or waits); null when none does.</param>
public sealed record RecreateContext(EngineVersion Engine, string? Self, IReadOnlyList<JsonObject> Others, JsonArray Networks, string LanAddress, string? Busy = null);

/// <summary>What a recreate changes: only the ports (the Network tab's Ports card), or other settings too (an edit, an update).</summary>
public enum ChangeScope { Ports, Settings }

/// <summary>
/// Which containers Gitberth recreates to change them (section 7.3, and 2.4 of the unified design): never itself, its
/// helpers, a Git app's container or a deploy's temporaries; never what Compose, Swarm or Kubernetes own; never a
/// container whose namespaces, --link name or --rm a recreate would break. A container on the host's network, another's
/// or none has no ports to change, but its other settings can be. Pure.
/// </summary>
public static class RecreateRules
{
    /// <summary>On a helper container Gitberth runs for a moment (host scans, port probes): the one label every list leaves out.</summary>
    public const string HelperLabel = HostScanHelper.Label;

    public const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>On a container Gitberth created: where it came from (registry, local, manual; git, or none, for a deployed app).</summary>
    public const string SourceLabel = "gitberth.source";

    /// <summary>
    /// An app Gitberth created from an image (gitberth.source registry, local or manual): under any name (one renamed outside
    /// Gitberth too) it's a container like any other, never a deploy's temporary. Only Git apps deploy; their temporaries
    /// carry the bridge label or are the app's container under another name, and the engine's copies are known by the journal.
    /// </summary>
    public static bool MadeFromImage(IReadOnlyDictionary<string, string> labels) => labels.GetValueOrDefault(SourceLabel) is not (null or "" or "git");

    /// <param name="scope">
    /// Ports (the default, as every caller before the unified design): host, none and container:x are refused. Settings:
    /// they're allowed. Either way, an app Gitberth created from an image (gitberth.source registry, local or manual) is
    /// recreated like any container, and a Git app's container never is: its settings are applied by a deploy.
    /// </param>
    public static RecreateVerdict Classify(string name, JsonObject? inspect, RecreateContext context, ChangeScope scope = ChangeScope.Ports)
    {
        if (inspect is null) return new(RecreateKind.Missing, $"There's no container called {name}.");
        var labels = Labels(inspect);
        var id = Str(inspect, "Id");
        var hostConfig = inspect["HostConfig"] as JsonObject;
        var state = inspect["State"] as JsonObject;
        var compose = ComposeService.Of(inspect);

        if (Gitberths(name, id, labels, context.Self, compose) is { } gitberths) return gitberths;
        if (compose is not null)
            return new(RecreateKind.Compose,
                $"Docker Compose manages this container (project {compose.Project}{(compose.Service is null ? "" : $", service {compose.Service}")}). Compose would undo a change made here, so this is the change to make in its file.",
                compose);
        if (Orchestrated(labels) is { } orchestrated) return orchestrated;

        var mode = Str(hostConfig, "NetworkMode");
        if (scope == ChangeScope.Ports)
        {
            if (mode == "host")
                return new(RecreateKind.HostNetwork, $"Uses the host's network, so Docker maps no ports: it listens on {context.LanAddress} directly.");
            if (mode.StartsWith("container:", StringComparison.Ordinal))
            {
                var other = NameOf(mode["container:".Length..], context.Others);
                return new(RecreateKind.SharesNetwork, $"Shares the network of {other}: its ports are {other}'s.", Link: other);
            }
            if (mode == "none") return new(RecreateKind.NoNetwork, "No network (network mode none).");
        }

        if (SharedBy(name, id, context.Others) is { } shared) return new(RecreateKind.SharedByOthers, shared);
        var linkers = context.Others.Where(o => LinksTo(o, name)).Select(o => Str(o, "Name").TrimStart('/')).Distinct().ToList();
        if (linkers.Count > 0)
        {
            var who = Join(linkers);
            return new(RecreateKind.LinkedTo,
                $"{who} {(linkers.Count == 1 ? "links" : "link")} to {name} (--link); recreating {name} would stop {(linkers.Count == 1 ? who : "them")} from starting.");
        }
        if (hostConfig?["AutoRemove"]?.GetValueKind() == JsonValueKind.True)
            return new(RecreateKind.AutoRemove, "Started with --rm: stopping it deletes it, so Gitberth can't recreate it.");
        if (!context.Engine.AtLeast(1, 44))
            return new(RecreateKind.EngineTooOld,
                $"Changing ports needs Docker 25 or later (API 1.44). This host has Docker {context.Engine.Version} (API {context.Engine.ApiVersion}).");
        if (state?["Paused"]?.GetValueKind() == JsonValueKind.True) return new(RecreateKind.Paused, "Paused. Unpause it to change its ports.");
        if (state?["Restarting"]?.GetValueKind() == JsonValueKind.True)
            return new(RecreateKind.Restarting, $"{name} keeps restarting. Stop it first: then it's recreated stopped.");
        if (Str(state, "Status") == "dead") return new(RecreateKind.Restarting, $"Docker reports {name} as dead, so Gitberth can't recreate it.");
        if (CloneSpec.NetworkModeFor(inspect, context.Networks) is { Mode: null, Gone: { } gone })
            return new(RecreateKind.NetworkGone, $"Its network {gone} no longer exists, and it's on no other network.");
        if (context.Busy is not null) return new(RecreateKind.Busy, context.Busy);
        return RecreateVerdict.Yes;
    }

    /// <summary>
    /// Why a live change (docker update: limits, restart policy) can't be made to the container; null when it can. Live
    /// changes need no recreate, so stopped, paused, restarting and Compose-managed containers (whose next recreate by
    /// Compose sets them again) all take them. Refused: Gitberth's helpers and itself, a deploy's temporaries, Swarm and
    /// Kubernetes, a Git app's container (its limits are the app's, saved first and then applied), and a container whose
    /// change isn't settled, or that a finished change left behind.
    /// </summary>
    public static string? LiveRefusal(string name, JsonObject? inspect, RecreateContext context) => LiveVerdict(name, inspect, context).Reason;

    /// <summary><see cref="LiveRefusal"/> as a verdict, for a plan.</summary>
    public static RecreateVerdict LiveVerdict(string name, JsonObject? inspect, RecreateContext context)
    {
        if (inspect is null) return new(RecreateKind.Missing, $"There's no container called {name}.");
        var labels = Labels(inspect);
        var compose = ComposeService.Of(inspect);
        if (Gitberths(name, Str(inspect, "Id"), labels, context.Self, compose) is { } gitberths)
            return gitberths.Kind == RecreateKind.App
                ? gitberths with { Reason = $"{name} is the app {labels.GetValueOrDefault("gitberth.app")}'s container: its limits are the app's settings." }
                : gitberths.Kind == RecreateKind.Self ? gitberths with { Reason = "Gitberth's own container: change it in its Compose file." }
                : gitberths;
        if (Orchestrated(labels) is { } orchestrated) return orchestrated;
        if (context.Busy is not null) return new(RecreateKind.Busy, context.Busy);
        return RecreateVerdict.Yes;
    }

    /// <summary>The note a live change to a Compose-managed container comes with.</summary>
    public const string ComposeLiveNote = "Compose sets these again when it recreates the container.";

    // Gitberth's own: its helpers, itself, a Git app's container and a deploy's temporaries. An app Gitberth created from an
    // image (gitberth.source registry, local or manual) is none of these, whatever its name (one renamed outside Gitberth
    // too): it's recreated like any other container. Only Git apps deploy, so only their containers have temporaries.
    private static RecreateVerdict? Gitberths(string name, string id, IReadOnlyDictionary<string, string> labels, string? self, ComposeService? compose)
    {
        if (labels.ContainsKey(HelperLabel)) return new(RecreateKind.Helper, "This is a Gitberth helper: it only runs for a moment.");
        if (IsSelf(name, id, labels, self))
            return new(RecreateKind.Self, compose is null
                ? "Gitberth's own container: it can't recreate itself."
                : $"Gitberth's own container. Its ports are set in {compose.File}.", compose);
        if (labels.TryGetValue("gitberth.app", out var slug))
        {
            if (labels.ContainsKey(ContainerSpecFactory.BridgeLabel))
                return new(RecreateKind.DeployTemporary, "This is a deploy's temporary or previous container, which Gitberth removes itself.");
            if (MadeFromImage(labels)) return null;
            if (name != AppDefinition.ContainerNameFor(slug))
                return new(RecreateKind.DeployTemporary, "This is a deploy's temporary or previous container, which Gitberth removes itself.");
            return new(RecreateKind.App, $"{name} is the app {slug}'s container: its ports are on the app's Network tab.");
        }
        if (labels.ContainsKey(ContainerSpecFactory.BridgeLabel))
            return new(RecreateKind.DeployTemporary, "This is a deploy's temporary or previous container, which Gitberth removes itself.");
        return null;
    }

    private static RecreateVerdict? Orchestrated(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.ContainsKey("com.docker.swarm.task.id") || labels.ContainsKey("com.docker.swarm.service.id"))
            return new(RecreateKind.Swarm, "Part of a Swarm service: change its ports with docker service update --publish-add.");
        if (labels.Keys.Any(k => k.StartsWith("io.kubernetes.", StringComparison.Ordinal) || k.StartsWith("io.x-k8s.", StringComparison.Ordinal)))
            return new(RecreateKind.Kubernetes, "Managed by Kubernetes: change its ports there.");
        return null;
    }

    /// <summary>Gitberth itself: the container it runs in, or the gitberth service of the gitberth Compose project.</summary>
    private static bool IsSelf(string name, string id, IReadOnlyDictionary<string, string> labels, string? self)
    {
        if (self is { Length: > 0 } && (name == self || id == self || (self.Length >= 12 && id.StartsWith(self, StringComparison.Ordinal)))) return true;
        return labels.GetValueOrDefault(ComposeProjectLabel) == "gitberth" && labels.GetValueOrDefault("com.docker.compose.service") == "gitberth";
    }

    // Who runs in its namespaces: their network, processes or IPC go when it's replaced.
    private static string? SharedBy(string name, string id, IReadOnlyList<JsonObject> others)
    {
        bool Points(string mode) => mode.StartsWith("container:", StringComparison.Ordinal) && Refers(mode["container:".Length..], name, id);
        foreach (var (field, what) in new[] { ("NetworkMode", "network"), ("PidMode", "processes"), ("IpcMode", "IPC namespace") })
        {
            var users = others.Where(o => Str(o, "Id") != id && Points(Str(o["HostConfig"], field)))
                .Select(o => Str(o, "Name").TrimStart('/')).Distinct().ToList();
            if (users.Count == 0) continue;
            var who = Join(users);
            return field == "NetworkMode"
                ? $"{who} {(users.Count == 1 ? "uses" : "use")} its network; recreating it would cut {(users.Count == 1 ? who : "them")} off."
                : $"{who} {(users.Count == 1 ? "shares" : "share")} its {what}; recreating it would cut {(users.Count == 1 ? who : "them")} off.";
        }
        return null;
    }

    // A legacy link reads "/postgres:/sample-web/db" in the linking container's HostConfig.Links.
    private static bool LinksTo(JsonObject other, string name) =>
        (other["HostConfig"]?["Links"] as JsonArray ?? []).OfType<JsonValue>()
            .Any(l => l.TryGetValue<string>(out var link) && link.Split(':')[0].TrimStart('/') == name);

    private static bool Refers(string reference, string name, string id) =>
        reference == name || reference == id || (reference.Length >= 12 && id.StartsWith(reference, StringComparison.Ordinal));

    // container:<id> names a container by ID; the line names it.
    private static string NameOf(string reference, IReadOnlyList<JsonObject> others) =>
        others.FirstOrDefault(o => Refers(reference, Str(o, "Name").TrimStart('/'), Str(o, "Id"))) is { } found
            ? Str(found, "Name").TrimStart('/')
            : reference.Length == 64 ? reference[..12] : reference;

    internal static IReadOnlyDictionary<string, string> Labels(JsonObject inspect)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in inspect["Config"]?["Labels"] as JsonObject ?? [])
            labels[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return labels;
    }

    internal static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);
}

/// <summary>Where Docker Compose keeps a container's settings, read from the labels Compose puts on every container.</summary>
/// <param name="ConfigFiles">com.docker.compose.project.config_files: the files, as paths on the host.</param>
public sealed record ComposeService(string Project, string? Service, IReadOnlyList<string> ConfigFiles, string? WorkingDir)
{
    public static ComposeService? Of(JsonObject inspect)
    {
        var labels = RecreateRules.Labels(inspect);
        if (!labels.TryGetValue(RecreateRules.ComposeProjectLabel, out var project) || project.Length == 0) return null;
        var files = labels.GetValueOrDefault("com.docker.compose.project.config_files") is { Length: > 0 } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new ComposeService(project, labels.GetValueOrDefault("com.docker.compose.service") is { Length: > 0 } service ? service : null,
            files, labels.GetValueOrDefault("com.docker.compose.project.working_dir") is { Length: > 0 } dir ? dir : null);
    }

    /// <summary>Where its ports are set, for "In /opt/db/docker-compose.yml:"; "its Compose file" when the labels don't say.</summary>
    public string File => ConfigFiles.Count == 0 ? "its Compose file" : string.Join(", ", ConfigFiles);

    /// <summary>
    /// The command that applies the file on the host: docker compose -p db -f /opt/db/docker-compose.yml up -d postgres.
    /// The project and files come from the labels, so it works from any folder and picks the same project the stack was
    /// started as. Null when the labels don't name the service.
    /// </summary>
    public string? Command => Service is null
        ? null
        : "docker compose -p " + Shell(Project) + string.Concat(ConfigFiles.Select(f => " -f " + Shell(f))) + " up -d " + Shell(Service);

    /// <summary>The service's ports in the file, for the bindings it should have: - "5432:5432", - "127.0.0.1:5432:5432", - "53:53/udp".</summary>
    public string Snippet(IEnumerable<NewBinding> bindings)
    {
        var ports = bindings.Select(b =>
        {
            var ip = b.HostIp.Length == 0 ? "" : (b.HostIp.Contains(':') ? $"[{b.HostIp}]" : b.HostIp) + ":";
            return $"      - \"{ip}{b.HostPort.ToString(CultureInfo.InvariantCulture)}:{b.ContainerPort.ToString(CultureInfo.InvariantCulture)}{(b.Protocol == "tcp" ? "" : "/" + b.Protocol)}\"";
        }).ToList();
        var text = new StringBuilder("services:\n").Append("  ").Append(YamlKey(Service ?? "<service>")).Append(":\n");
        if (ports.Count == 0) return text.Append("    ports: []").ToString();
        text.Append("    ports:\n");
        return text.Append(string.Join('\n', ports)).ToString();
    }

    private static string YamlKey(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? key : "\"" + key.Replace("\"", "\\\"") + "\"";

    private static string Shell(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,+%".Contains(c)) ? arg : "'" + arg.Replace("'", "'\\''") + "'";
}
