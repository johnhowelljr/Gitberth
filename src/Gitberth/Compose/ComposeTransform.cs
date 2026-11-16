using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Network;

namespace Gitberth.Compose;

/// <summary>What a path is on disk, for the transform's decisions about bind mounts.</summary>
public enum PathKind { None, File, Directory }

/// <summary>What a Compose app's deploy runs: the app, the deploy, and where its files are, in Gitberth's paths and the Docker host's.</summary>
public sealed record ComposeTarget
{
    public required string Slug { get; init; }
    /// <summary>The service that's the app's container: its domains, LAN port, health path, logs and uptime.</summary>
    public required string WebService { get; init; }
    /// <summary>The app's ports, which the web service publishes in place of the ones its file has.</summary>
    public IReadOnlyList<PortMapping> Ports { get; init; } = [];
    public int DeploymentId { get; init; }
    public DeployTrigger Trigger { get; init; } = DeployTrigger.Manual;
    /// <summary>The release's folder (the repo, or the kept Compose file), as Gitberth sees it.</summary>
    public required string ReleaseDir { get; init; }
    /// <summary>The same folder as the Docker host sees it: where its bind mounts come from.</summary>
    public required string ReleaseHostDir { get; init; }
    /// <summary>The app's own folder kept from one deploy to the next, for what its services write (as Gitberth sees it).</summary>
    public required string DataDir { get; init; }
    public required string DataHostDir { get; init; }
    /// <summary>A preview's deploy: its file is a pull request's, so it gets no more of the host than a preview of one container.</summary>
    public bool Preview { get; init; }
    /// <summary>What built services are tagged with: the commit's first 7 characters, or the Compose file's.</summary>
    public required string Tag { get; init; }
    /// <summary>A deploy without a build (a rollback, a settings change): the images the built services run, by service.</summary>
    public IReadOnlyDictionary<string, string>? Images { get; init; }
    /// <summary>The LAN's ports go on 0.0.0.0, IPv4 only, as for every other app on a host the internet reaches over IPv6.</summary>
    public bool LanIpv4Only { get; init; }
    /// <summary>The bind mounts the file writes with a relative source (./conf, ../data), by service and target.</summary>
    public IReadOnlySet<(string Service, string Target)> RelativeBinds { get; init; } = new HashSet<(string, string)>();
    /// <summary>What a path in the release is; the disk's own answer unless a test gives one.</summary>
    public Func<string, PathKind> Kind { get; init; } = path => File.Exists(path) ? PathKind.File : Directory.Exists(path) ? PathKind.Directory : PathKind.None;
    /// <summary>Whether a path in the release (the first) goes through a link out of it; the disk's own answer unless a test gives one.</summary>
    public Func<string, string, bool> LinksOut { get; init; } = ComposeTransform.LinksOut;
    /// <summary>The app's variables' names (what its .env has): the only ones a preview's secrets and configs may come from.</summary>
    public IReadOnlySet<string> Variables { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>
    /// A deploy without a build: the images its pulled services ran, pinned (gitberth/&lt;slug&gt;/&lt;service&gt;:pin-…), for
    /// the ones whose tag moved since; they run in place of the tag.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Pins { get; init; }
}

/// <summary>A member's place on Gitberth's shared network: joined (with the app's name as its alias, for the web service), or not.</summary>
public sealed record SharedJoin(string Service, string Member, string? Alias, bool Join);

/// <summary>A service Gitberth builds itself: its build section as Compose resolved it, and the tag its image gets.</summary>
public sealed record ComposeBuild(string Service, string Tag, JsonObject Build);

/// <summary>
/// A folder or file of the app's own that's kept from one deploy to the next, which a writable bind mount goes to: its place
/// in Gitberth's paths, and what the release has there to start it with (null: nothing, so it's an empty folder).
/// </summary>
public sealed record ComposeMount(string Path, string? SeedFrom, string Relative);

/// <summary>What a deploy runs, worked out from the resolved Compose file.</summary>
/// <param name="Model">The file to run, as JSON (Compose reads it as YAML).</param>
/// <param name="Images">Each service's image, as Deployment.Images keeps them.</param>
/// <param name="Notes">What the log says about it: what a preview leaves out, which folders are kept.</param>
/// <param name="Problems">Why it can't be deployed; empty when it can.</param>
public sealed record ComposePlan(JsonObject Model, IReadOnlyList<string> Services, IReadOnlyList<ComposeBuild> Builds, IReadOnlyDictionary<string, string> Images,
    IReadOnlyList<ComposeMount> Kept, IReadOnlyList<string> Notes, IReadOnlyList<string> Problems)
{
    /// <summary>Each service's container, by service.</summary>
    public IReadOnlyDictionary<string, string> Members { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Turns the file Compose resolved (docker compose config --format json) into the one Gitberth runs: every service labelled
/// as the app's (gitberth.managed, gitberth.app, gitberth.service, gitberth.deployment, gitberth.trigger), restarted unless
/// stopped where the file doesn't say, named gb-&lt;slug&gt;_&lt;service&gt;; built services tagged
/// gitberth/&lt;slug&gt;/&lt;service&gt;:&lt;tag&gt;; the web service publishing the app's ports; relative bind mounts going to the
/// release's folder on the Docker host (read-only ones) or to the app's kept folder (writable ones). A preview's file is a
/// pull request's, so it gets nothing more of the host than a preview of one container does: no privileges, devices,
/// host namespaces, host bind mounts, extra host names to the host, other ports than its web one, or production's named
/// volumes and networks. Pure: what's on disk comes through <see cref="ComposeTarget.Kind"/>.
/// </summary>
public static class ComposeTransform
{
    /// <summary>The key Gitberth keeps its own facts under in the file it runs (Compose ignores x- keys).</summary>
    public const string OwnKey = "x-gitberth";

    public static ComposePlan Apply(JsonObject resolved, ComposeTarget target)
    {
        var model = (JsonObject)resolved.DeepClone();
        var notes = new List<string>();
        var problems = new List<string>();
        var builds = new List<ComposeBuild>();
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<ComposeMount>();
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        model["name"] = ComposeNames.Project(target.Slug);
        if (model["services"] is not JsonObject services || services.Count == 0)
            return new ComposePlan(model, [], [], images, kept, notes, ["The Compose file has no services, so there's nothing to deploy."]);
        var names = services.Select(s => s.Key).ToList();
        if (!services.ContainsKey(target.WebService))
            problems.Add($"The web service {target.WebService} isn't in the Compose file (it has {Join(names)}). Pick another one in the app's settings.");

        if (target.Preview) PreviewTopLevel(model, target, notes);
        TopLevelFiles(model, target, notes, problems);

        foreach (var (name, node) in services.ToList())
        {
            if (node is not JsonObject service) continue;
            // A provider service is a program Compose runs itself, where it runs (Gitberth's container): never one of a file's.
            if (service["provider"] is not null)
            {
                problems.Add($"The service {name} is a provider service (provider: {Str(service["provider"]?["type"]) ?? "…"}), which docker compose runs as a program next to Gitberth. Gitberth doesn't run Compose providers: take it out of the Compose file.");
                continue;
            }
            var member = ComposeNames.Member(target.Slug, name);
            if (members.ContainsValue(member)) problems.Add($"Two services would both be called {member}. Rename one of them in the Compose file.");
            members[name] = member;
            service["container_name"] = member;
            Labels(service, target, name);
            if (service["restart"] is null) service["restart"] = "unless-stopped";
            if (Copies(service) is > 1 and var copies)
                problems.Add($"The service {name} asks for {copies} containers (deploy.replicas or scale). Gitberth runs one container a service: set it to 1 in the Compose file.");
            // Its values are in its environment now (Compose read them): the file Gitberth runs needs nothing else.
            service.Remove("env_file");
            if (target.Preview) PreviewService(name, service, name == target.WebService, target, notes, problems);
            Image(name, service, target, builds, images, problems);
            if (name == target.WebService) WebPorts(service, target);
            Binds(name, service, target, kept, notes, problems);
        }
        model[OwnKey] = new JsonObject
        {
            ["web"] = target.WebService,
            ["deployment"] = target.DeploymentId,
        };
        return new ComposePlan(model, names, builds, images, kept, notes, problems) { Members = members };
    }

    /// <summary>
    /// The services this file runs exactly as the release that runs now runs them (the same image, settings and variables):
    /// they keep that release's deploy labels, so docker compose leaves their containers running rather than recreating
    /// them, and a web service's new commit never restarts its database. Returns them.
    /// </summary>
    public static List<string> KeepUnchanged(JsonObject model, JsonObject? running)
    {
        var kept = new List<string>();
        if (model["services"] is not JsonObject services || running?["services"] is not JsonObject before) return kept;
        string[] deployLabels = [Docker.ContainerSpecFactory.DeploymentLabel, Docker.ContainerSpecFactory.TriggerLabel];
        foreach (var (name, node) in services)
        {
            if (node is not JsonObject service || before[name] is not JsonObject was) continue;
            if (was["labels"] is not JsonObject wasLabels || deployLabels.Any(l => Str(wasLabels[l]) is null)) continue;
            JsonObject Without(JsonObject s)
            {
                var copy = (JsonObject)s.DeepClone();
                if (copy["labels"] is JsonObject labels) foreach (var l in deployLabels) labels.Remove(l);
                return copy;
            }
            if (!JsonNode.DeepEquals(Without(service), Without(was))) continue;
            var own = (JsonObject)service["labels"]!;
            foreach (var l in deployLabels) own[l] = Str(wasLabels[l]);
            kept.Add(name);
        }
        return kept;
    }

    /// <summary>
    /// What each member of a file Gitberth runs gets on Gitberth's shared network after up: the web service joins under the
    /// app's name (its alias); with <paramref name="everyService"/>, every other one joins too, with no alias (it reaches
    /// the app's database and the other apps by name; nothing reaches it by a name of its own there); without, any other
    /// one that's on it leaves. A service in a network mode of its own (service:…, none, host) is left as it is.
    /// </summary>
    public static List<SharedJoin> SharedJoins(JsonObject model, string slug, bool everyService)
    {
        var joins = new List<SharedJoin>();
        if (model["services"] is not JsonObject services) return joins;
        var web = Str(model[OwnKey]?["web"]) ?? "";
        foreach (var (name, node) in services)
        {
            if (node is not JsonObject service || Str(service["network_mode"]) is { Length: > 0 }) continue;
            var member = Str(service["container_name"]) ?? ComposeNames.Member(slug, name);
            joins.Add(name == web ? new SharedJoin(name, member, slug, true) : new SharedJoin(name, member, null, everyService));
        }
        return joins.OrderBy(j => j.Alias is null ? 1 : 0).ToList();
    }

    // ---- Labels, copies, images ----

    // Gitberth's own labels, and none of the file's under its name: a file can't make a container look like a helper or another app's.
    private static void Labels(JsonObject service, ComposeTarget target, string name)
    {
        var labels = service["labels"] as JsonObject ?? new JsonObject();
        foreach (var key in labels.Select(l => l.Key).Where(k => k.StartsWith("gitberth.", StringComparison.Ordinal)).ToList()) labels.Remove(key);
        labels["gitberth.managed"] = "true";
        labels["gitberth.app"] = target.Slug;
        labels[ComposeNames.ServiceLabel] = name;
        labels[Docker.ContainerSpecFactory.DeploymentLabel] = target.DeploymentId.ToString(CultureInfo.InvariantCulture);
        labels[Docker.ContainerSpecFactory.TriggerLabel] = target.Trigger.ToString();
        service["labels"] = labels;
    }

    /// <summary>How many containers a service asks for: deploy.replicas or scale, the larger; 1 when neither says.</summary>
    internal static int Copies(JsonObject service)
    {
        var replicas = Int(service["deploy"]?["replicas"]) ?? 1;
        var scale = Int(service["scale"]) ?? 1;
        return Math.Max(replicas, scale);
    }

    private static void Image(string name, JsonObject service, ComposeTarget target, List<ComposeBuild> builds, Dictionary<string, string> images, List<string> problems)
    {
        if (service["build"] is JsonObject build)
        {
            if (target.Images?.GetValueOrDefault(name) is { Length: > 0 } image)
            {
                // A deploy without a build runs the image a build made before.
                service["image"] = image;
                service.Remove("build");
                service.Remove("pull_policy");
            }
            else
            {
                var tag = $"{ComposeNames.ImageRepository(target.Slug, name)}:{target.Tag}";
                if (BuildProblem(name, build, target) is { } problem) problems.Add(problem);
                service["image"] = tag;
                service.Remove("pull_policy");
                builds.Add(new ComposeBuild(name, tag, (JsonObject)build.DeepClone()));
            }
        }
        else if (Str(service["image"]) is not { Length: > 0 })
        {
            problems.Add($"The service {name} has neither an image nor a build section.");
        }
        else if (target.Preview && OthersImage(Str(service["image"])!, target) is { } why)
        {
            problems.Add($"The service {name} runs {Str(service["image"])}, {why}");
        }
        if (Str(service["image"]) is { Length: > 0 } final) images[name] = final;
        // A pulled image whose tag moved since the release ran: the image it ran, by the app's own tag for it.
        if (service["build"] is null && target.Pins?.GetValueOrDefault(name) is { Length: > 0 } pin) service["image"] = pin;
    }

    // A build reads its context on Gitberth's side: only the release's own files (never through a link out of it), or a remote one.
    private static string? BuildProblem(string name, JsonObject build, ComposeTarget target)
    {
        bool Outside(string path) => !Inside(target.ReleaseDir, path) || target.LinksOut(target.ReleaseDir, path);
        var context = Str(build["context"]) is { Length: > 0 } c ? c : null;
        if (context is not null && !Remote(context) && Outside(context))
            return $"The service {name} builds from {Shown(context, target)}, outside the repo. Gitberth builds from the repo's own files only.";
        // Gitberth reads the Dockerfile itself (for its build-time values): the one named, or the context's own.
        var dockerfile = Str(build["dockerfile"]) is { Length: > 0 } named ? named : build["dockerfile_inline"] is null ? "Dockerfile" : null;
        if (dockerfile is not null && (context is null || !Remote(context) || Path.IsPathRooted(dockerfile)))
        {
            var path = Path.IsPathRooted(dockerfile) ? dockerfile : Path.Combine(context ?? target.ReleaseDir, dockerfile).Replace('\\', '/');
            if (Outside(path)) return $"The service {name}'s Dockerfile {dockerfile} is outside the repo.";
        }
        if (build["additional_contexts"] is JsonObject more)
            foreach (var (key, value) in more)
                if (Str(value) is { Length: > 0 } path && !Remote(path) && !path.StartsWith("docker-image://", StringComparison.Ordinal)
                    && !path.StartsWith("service:", StringComparison.Ordinal) && Outside(path))
                    return $"The service {name}'s build context {key} ({path}) is outside the repo.";
        return null;
    }

    private static bool Remote(string context) =>
        context.Contains("://", StringComparison.Ordinal) || context.StartsWith("git@", StringComparison.Ordinal) || context.StartsWith("github.com/", StringComparison.Ordinal);

    // ---- The web service's ports ----

    /// <summary>
    /// The web service publishes the app's ports (the Network tab's), in place of its file's: the LAN's on every address (or
    /// 0.0.0.0), the host's on 127.0.0.1, a custom one on its address; an Internal one only exposed.
    /// </summary>
    private static void WebPorts(JsonObject service, ComposeTarget target)
    {
        if (Str(service["network_mode"]) is { Length: > 0 }) return;
        var ports = new JsonArray();
        var expose = service["expose"] as JsonArray ?? new JsonArray();
        foreach (var p in target.Ports.OrderBy(p => p.Id))
        {
            var protocol = p.Protocol == "udp" ? "udp" : "tcp";
            if (p.Access == PortAccess.Internal || p.HostPort is not int hostPort)
            {
                var exposed = protocol == "udp" ? $"{p.ContainerPort}/udp" : p.ContainerPort.ToString(CultureInfo.InvariantCulture);
                if (!expose.Any(e => Str(e) == exposed)) expose.Add(exposed);
                continue;
            }
            var port = new JsonObject
            {
                ["mode"] = "ingress",
                ["target"] = p.ContainerPort,
                ["published"] = hostPort.ToString(CultureInfo.InvariantCulture),
                ["protocol"] = protocol,
            };
            var ip = p.Access == PortAccess.Lan && target.LanIpv4Only ? "0.0.0.0" : ContainerPorts.BindAddress(p.Access, p.HostIp);
            if (ip is { Length: > 0 }) port["host_ip"] = ip;
            ports.Add(port);
        }
        if (ports.Count > 0) service["ports"] = ports;
        else service.Remove("ports");
        if (expose.Count > 0) service["expose"] = expose;
    }

    // ---- Bind mounts ----

    /// <summary>
    /// A relative bind mount (./conf) goes to the release's folder on the Docker host when it's read-only, and to the app's
    /// kept folder when its service can write there (or the repo doesn't have it), so what a service writes outlives the
    /// deploy; one that leaves the release (../../etc) is refused. An absolute path is the owner's choice, kept as written,
    /// except in a preview, which mounts no folder of the host at all.
    /// </summary>
    private static void Binds(string name, JsonObject service, ComposeTarget target, List<ComposeMount> kept, List<string> notes, List<string> problems)
    {
        if (service["volumes"] is not JsonArray volumes) return;
        foreach (var volume in volumes.OfType<JsonObject>().ToList())
        {
            if (Str(volume["type"]) != "bind" || Str(volume["source"]) is not { Length: > 0 } source) continue;
            var mountTarget = Str(volume["target"]) ?? "";
            if (target.Preview)
            {
                volumes.Remove(volume);
                notes.Add($"Not mounted in {name}: {Shown(source, target)} at {mountTarget}, because a preview never mounts a folder of the host.");
                continue;
            }
            var relative = target.RelativeBinds.Contains((name, mountTarget)) || Inside(target.ReleaseDir, source);
            if (!relative) continue;
            if (!Inside(target.ReleaseDir, source))
            {
                problems.Add($"The service {name} mounts {source} at {mountTarget}, outside the repo. A relative path in the Compose file stays inside it; write an absolute path for a folder of the host.");
                continue;
            }
            var rel = Path.GetRelativePath(target.ReleaseDir, source).Replace('\\', '/');
            if (rel == ".") rel = "";
            // Docker follows links in a bind's source on the host: a link in the repo would lead it out of the release.
            if (target.LinksOut(target.ReleaseDir, source))
            {
                problems.Add($"The service {name} mounts ./{rel} at {mountTarget}, which is a link out of the repo. A relative path in the Compose file stays inside it; write an absolute path for a folder of the host.");
                continue;
            }
            var kind = target.Kind(source);
            var readOnly = volume["read_only"]?.GetValueKind() == JsonValueKind.True;
            if (readOnly && kind != PathKind.None)
            {
                volume["source"] = Join(target.ReleaseHostDir, rel);
                continue;
            }
            volume["source"] = Join(target.DataHostDir, rel);
            if (kept.All(k => k.Relative != rel))
            {
                kept.Add(new ComposeMount(Join(target.DataDir, rel), kind == PathKind.None ? null : source, rel));
                notes.Add(kind == PathKind.None
                    ? $"./{rel} ({name}, at {mountTarget}) is kept in Gitberth's data folder from one deploy to the next."
                    : $"./{rel} ({name}, at {mountTarget}) is writable, so it's kept in Gitberth's data folder from one deploy to the next: files the repo changes are updated there, unless the app changed them.");
            }
        }
    }

    // ---- Top-level secrets and configs from files ----

    // A file secret or config is mounted from the Docker host: the release's file there, or, in a preview, none.
    private static void TopLevelFiles(JsonObject model, ComposeTarget target, List<string> notes, List<string> problems)
    {
        foreach (var section in new[] { "secrets", "configs" })
        {
            if (model[section] is not JsonObject entries) continue;
            foreach (var (key, node) in entries.ToList())
            {
                if (node is not JsonObject entry || Str(entry["file"]) is not { Length: > 0 } file) continue;
                if (target.Preview)
                {
                    entries.Remove(key);
                    foreach (var service in (model["services"] as JsonObject ?? []).Select(s => s.Value).OfType<JsonObject>())
                        foreach (var uses in new[] { service[section], service["build"]?[section] }.OfType<JsonArray>())
                            foreach (var use in uses.Where(u => u is JsonValue v && Str(v) == key || u is JsonObject o && Str(o["source"]) == key).ToList()) uses.Remove(use);
                    notes.Add($"No {(section == "secrets" ? "secret" : "config")} {key}: it's a file, and a preview never mounts a file of the host.");
                    continue;
                }
                if (!Inside(target.ReleaseDir, file)) continue;
                var rel = Path.GetRelativePath(target.ReleaseDir, file).Replace('\\', '/');
                if (target.LinksOut(target.ReleaseDir, file))
                    problems.Add($"The {(section == "secrets" ? "secret" : "config")} {key} is ./{rel}, which is a link out of the repo. Write an absolute path for a file of the host.");
                else
                    entry["file"] = Join(target.ReleaseHostDir, rel);
            }
        }
    }

    // ---- Previews ----

    // ---- Previews ----
    // A pull request's file is written by whoever opened it, so a preview keeps only what's known to stay inside its own
    // containers, by allowlists: any other key, at any level, is taken out and the log says so. The lists name what a preview
    // of one container may set too.

    /// <summary>The top-level keys a preview's file keeps (and x- ones, which Compose ignores).</summary>
    internal static readonly HashSet<string> PreviewTopKeys = new(StringComparer.Ordinal) { "name", "services", "networks", "volumes", "secrets", "configs" };

    /// <summary>
    /// The service keys a preview's file keeps (and x- ones). Not among them: privileged, cap_add, devices, gpus, sysctls,
    /// userns_mode, runtime, logging, cgroup_parent, external_links, use_api_socket (the Docker socket), post_start and
    /// pre_stop (hooks Compose runs in the container, privileged when asked), provider (a program Compose runs where it
    /// runs), models, storage_opt, oom_score_adj and anything Compose adds later.
    /// </summary>
    internal static readonly HashSet<string> PreviewServiceKeys = new(StringComparer.Ordinal)
    {
        "attach", "build", "cap_drop", "cgroup", "command", "configs", "container_name", "cpu_count", "cpu_percent", "cpu_period", "cpu_quota",
        "cpu_shares", "cpus", "cpuset", "depends_on", "deploy", "dns", "dns_opt", "dns_search", "domainname", "entrypoint", "environment", "expose",
        "extra_hosts", "group_add", "healthcheck", "hostname", "image", "init", "ipc", "labels", "links", "mac_address", "mem_limit", "mem_reservation",
        "mem_swappiness", "memswap_limit", "network_mode", "networks", "pid", "pids_limit", "platform", "ports", "profiles", "pull_policy", "read_only",
        "restart", "scale", "secrets", "security_opt", "shm_size", "stdin_open", "stop_grace_period", "stop_signal", "tmpfs", "tty", "ulimits", "user",
        "uts", "volumes", "volumes_from", "working_dir",
    };

    /// <summary>A preview's build keys (no ssh, entitlements, privileged, tags, cache_to, isolation…).</summary>
    internal static readonly HashSet<string> PreviewBuildKeys = new(StringComparer.Ordinal)
    {
        "context", "dockerfile", "dockerfile_inline", "args", "target", "labels", "shm_size", "extra_hosts", "network", "no_cache", "pull", "platforms",
        "cache_from", "secrets", "additional_contexts", "ulimits",
    };

    /// <summary>A preview's network keys: a plain bridge of its own (no driver_opts, ipam, another driver).</summary>
    internal static readonly HashSet<string> PreviewNetworkKeys = new(StringComparer.Ordinal) { "name", "external", "driver", "attachable", "enable_ipv4", "enable_ipv6", "internal", "labels" };

    /// <summary>A preview's volume keys: a plain local volume of its own (no driver_opts, which can mount the host's folders).</summary>
    internal static readonly HashSet<string> PreviewVolumeKeys = new(StringComparer.Ordinal) { "name", "external", "driver", "labels" };

    /// <summary>A preview's secret and config keys: from a file (taken out later), from one of the app's variables, or written in the file.</summary>
    internal static readonly HashSet<string> PreviewSecretKeys = new(StringComparer.Ordinal) { "name", "file", "environment", "content", "labels" };

    // Keys outside a list go, with what they were.
    private static List<string> KeepOnly(JsonObject node, IReadOnlySet<string> keys)
    {
        var gone = node.Select(p => p.Key).Where(k => !keys.Contains(k) && !k.StartsWith("x-", StringComparison.Ordinal)).ToList();
        foreach (var key in gone) node.Remove(key);
        return gone;
    }

    // A secret or config no longer there isn't asked for by any service, or its build.
    private static void RemoveUses(JsonObject model, string section, string key)
    {
        foreach (var service in (model["services"] as JsonObject ?? []).Select(s => s.Value).OfType<JsonObject>())
            foreach (var uses in new[] { service[section], service["build"]?[section] }.OfType<JsonArray>())
                foreach (var use in uses.Where(u => u is JsonValue v && Str(v) == key || u is JsonObject o && Str(o["source"]) == key).ToList()) uses.Remove(use);
    }

    // A preview's named volumes and networks are its own project's: a name the file fixes (even its default network's, or
    // one that is production's <project>_default), or an external one, would be production's. Plain ones too: a volume
    // driver's options can mount a folder of the host (o: bind), and a network driver other than a bridge can put its
    // services on the LAN itself (macvlan, ipvlan). Its secrets and configs come from the app's variables or the file.
    private static void PreviewTopLevel(JsonObject model, ComposeTarget target, List<string> notes)
    {
        var project = Str(model["name"]) ?? "";
        if (KeepOnly(model, PreviewTopKeys) is { Count: > 0 } top) notes.Add($"Taken out of the file, because a preview gets nothing it doesn't know: {Join(top)}.");
        if (model["volumes"] is JsonObject volumes)
            foreach (var (key, node) in volumes)
            {
                if (node is not JsonObject volume) continue;
                var external = volume["external"]?.GetValueKind() == JsonValueKind.True;
                var named = Str(volume["name"]) is { Length: > 0 } n && n != $"{project}_{key}";
                if (external || named)
                {
                    volume.Remove("external");
                    volume.Remove("name");
                    notes.Add($"The volume {key} is the preview's own, not {(external ? "the external one" : "the one its file names")}: a preview never shares production's data.");
                }
                var plain = KeepOnly(volume, PreviewVolumeKeys);
                if (Str(volume["driver"]) is { Length: > 0 } driver && driver != "local")
                {
                    volume.Remove("driver");
                    plain.Add("driver");
                }
                if (plain.Count > 0) notes.Add($"The volume {key} is a plain one of the preview's ({Join(plain)} taken out): a volume driver's options can reach the host's folders and devices.");
            }
        if (model["networks"] is JsonObject networks)
            foreach (var (key, node) in networks.ToList())
            {
                if (node is not JsonObject network) continue;
                if (network["external"]?.GetValueKind() == JsonValueKind.True)
                {
                    if (key == "default")
                    {
                        // Every service is on it: it becomes the preview's own default network.
                        var was = Str(network["name"]) ?? "the external one";
                        network.Remove("external");
                        network.Remove("name");
                        notes.Add($"The default network is the preview's own, not {was}: its services would answer to production's names there.");
                    }
                    else
                    {
                        networks.Remove(key);
                        foreach (var service in (model["services"] as JsonObject ?? []).Select(s => s.Value).OfType<JsonObject>())
                            (service["networks"] as JsonObject)?.Remove(key);
                        notes.Add($"Not on the network {Str(network["name"]) ?? key}: it's outside the preview's project, where its services would answer to production's names.");
                        continue;
                    }
                }
                // Only its own project's name: Compose would join any existing network the file names, production's too.
                if (Str(network["name"]) is { Length: > 0 } fixedName && fixedName != $"{project}_{key}")
                {
                    network.Remove("name");
                    notes.Add($"The network {key} is the preview's own, not {fixedName}: its services would answer to production's names there.");
                }
                var plain = KeepOnly(network, PreviewNetworkKeys);
                if (Str(network["driver"]) is { Length: > 0 } driver && driver != "bridge")
                {
                    network.Remove("driver");
                    plain.Add("driver");
                }
                if (plain.Count > 0) notes.Add($"The network {key} is a plain bridge of the preview's ({Join(plain)} taken out): another driver, or its addresses, could put its services on the LAN or reach the host's own network.");
            }
        foreach (var section in new[] { "secrets", "configs" })
        {
            if (model[section] is not JsonObject entries) continue;
            var what = section == "secrets" ? "secret" : "config";
            foreach (var (key, node) in entries.ToList())
            {
                if (node is not JsonObject entry)
                {
                    entries.Remove(key);
                    RemoveUses(model, section, key);
                    continue;
                }
                if (KeepOnly(entry, PreviewSecretKeys) is { Count: > 0 } gone)
                    notes.Add($"The {what} {key} is the preview's own ({Join(gone)} taken out).");
                // From a variable: only one of the app's own. Any other would be read from where Gitberth runs Compose.
                if (Str(entry["environment"]) is { Length: > 0 } variable && !target.Variables.Contains(variable))
                {
                    entries.Remove(key);
                    RemoveUses(model, section, key);
                    notes.Add($"No {what} {key}: it comes from {variable}, which isn't one of the app's variables.");
                    continue;
                }
                if (Str(entry["file"]) is null && Str(entry["environment"]) is null && entry["content"] is null)
                {
                    entries.Remove(key);
                    RemoveUses(model, section, key);
                    notes.Add($"No {what} {key}: a preview's comes from its file, one of the app's variables or the Compose file itself.");
                }
            }
        }
    }

    // What reaches past a container into the host, which a pull request's file never gets; and no host port but the web one's.
    private static void PreviewService(string name, JsonObject service, bool web, ComposeTarget target, List<string> notes, List<string> problems)
    {
        var removed = new List<string>();
        foreach (var key in KeepOnly(service, PreviewServiceKeys)) removed.Add(KeyWords(key));
        if (service["deploy"]?["resources"]?["reservations"] is JsonObject reservations && reservations["devices"] is not null)
        {
            reservations.Remove("devices");
            removed.Add("GPUs");
        }
        foreach (var key in new[] { "pid", "ipc", "uts", "cgroup" })
            if (Str(service[key]) is { Length: > 0 } mode && !mode.StartsWith("service:", StringComparison.Ordinal) && mode is not ("private" or "shareable"))
            {
                service.Remove(key);
                removed.Add($"{key} {mode}");
            }
        if (Str(service["network_mode"]) is { Length: > 0 } networkMode && !networkMode.StartsWith("service:", StringComparison.Ordinal) && networkMode != "none")
        {
            service.Remove("network_mode");
            removed.Add($"network mode {networkMode}");
        }
        if (service["security_opt"] is JsonArray security)
        {
            var others = security.Where(o => !NoNewPrivileges(Str(o) ?? "")).ToList();
            foreach (var o in others) security.Remove(o);
            if (others.Count > 0) removed.Add($"security options ({string.Join(", ", others.Select(o => Str(o)))})");
            if (security.Count == 0) service.Remove("security_opt");
        }
        if (service["extra_hosts"] is JsonArray hosts)
        {
            var toHost = hosts.Where(h => ToTheHost(Str(h) ?? "")).ToList();
            foreach (var h in toHost) hosts.Remove(h);
            if (toHost.Count > 0) removed.Add($"host names that lead to the host ({string.Join(", ", toHost.Select(h => Str(h)))})");
        }
        else if (service["extra_hosts"] is JsonObject hostMap)
        {
            var toHost = hostMap.Where(h => ToTheHost($"{h.Key}:{Str(h.Value)}")).Select(h => h.Key).ToList();
            foreach (var h in toHost) hostMap.Remove(h);
            if (toHost.Count > 0) removed.Add($"host names that lead to the host ({string.Join(", ", toHost)})");
        }
        if (service["volumes_from"] is JsonArray from)
        {
            var outside = from.Where(f => (Str(f) ?? "").StartsWith("container:", StringComparison.Ordinal)).ToList();
            foreach (var f in outside) from.Remove(f);
            if (outside.Count > 0) removed.Add("volumes of containers outside its project");
        }
        // Only volumes of its own and tmpfs (bind mounts go in Binds, with a note): no named pipe, no cluster volume.
        if (service["volumes"] is JsonArray mounts)
            foreach (var mount in mounts.ToList())
                if (mount is not JsonObject m || Str(m["type"]) is not ("volume" or "tmpfs" or "bind" or "image"))
                {
                    mounts.Remove(mount);
                    removed.Add(mount is JsonObject o && Str(o["type"]) is { Length: > 0 } type ? $"a {type} mount" : "a mount of no known type");
                }
        if (service["build"] is JsonObject build)
        {
            // Its build gets what a preview's build of one container does: no SSH, entitlements, privileged build, other
            // tags (which would put its image under other names on this host, redis:8-alpine or another app's), or cache
            // export (a folder where Gitberth runs).
            foreach (var key in KeepOnly(build, PreviewBuildKeys)) removed.Add(key switch
            {
                "ssh" => "SSH for its build",
                "entitlements" => "build entitlements",
                "privileged" => "a privileged build",
                "tags" => "its build's other tags",
                "cache_to" => "its build's cache export",
                _ => $"its build's {key}",
            });
            if (build["cache_from"] is JsonArray caches && caches.Where(c => !(Str(c) ?? "").Contains("type=registry", StringComparison.Ordinal) && (Str(c) ?? "").Contains('=')).ToList() is { Count: > 0 } local)
            {
                foreach (var c in local) caches.Remove(c);
                removed.Add("its build's local cache");
            }
            if (Str(build["network"]) is { Length: > 0 } buildNetwork && buildNetwork is not ("default" or "host" or "none"))
            {
                build.Remove("network");
                removed.Add($"its build's network {buildNetwork}");
            }
            // Another app's image isn't a preview's to build from either.
            if (build["additional_contexts"] is JsonObject contexts)
                foreach (var (key, value) in contexts.ToList())
                    if (Str(value) is { } source && source.StartsWith("docker-image://", StringComparison.Ordinal) && OthersImage(source["docker-image://".Length..], target) is { } why)
                        problems.Add($"The service {name}'s build context {key} is {source["docker-image://".Length..]}, {why}");
        }
        if (!web && service["ports"] is JsonArray { Count: > 0 })
        {
            service.Remove("ports");
            removed.Add("its host ports (only the web service's preview port is published)");
        }
        if (removed.Count > 0) notes.Add($"Taken out of {name}, because a preview gets no more of the host than a preview of one container: {Join(removed.Distinct().ToList())}.");
    }

    // How the log names a service key a preview took out.
    private static string KeyWords(string key) => key switch
    {
        "privileged" => "privileged mode",
        "cap_add" => "added capabilities",
        "devices" => "the host's devices",
        "device_cgroup_rules" => "device rules",
        "gpus" => "GPUs",
        "sysctls" => "sysctls",
        "cgroup_parent" => "its cgroup parent",
        "external_links" => "links outside its project",
        "post_start" => "post_start hooks",
        "pre_stop" => "pre_stop hooks",
        "use_api_socket" => "the Docker API socket",
        "userns_mode" => "the host's user namespace",
        "runtime" => "its runtime",
        "logging" => "its log driver",
        "provider" => "a provider",
        _ => key,
    };

    /// <summary>
    /// Why an image a preview's file names isn't its to run: another app's build (gitberth/…, which may hold that app's
    /// build-time values), or an image ID (a build of this host's, by its content). Null when it may run it.
    /// </summary>
    internal static string? OthersImage(string image, ComposeTarget target)
    {
        var name = image.Trim();
        if (name.StartsWith("docker.io/", StringComparison.Ordinal)) name = name["docker.io/".Length..];
        if (name.StartsWith("gitberth/", StringComparison.Ordinal) && !name.StartsWith($"gitberth/{target.Slug}/", StringComparison.Ordinal))
            return "an image Gitberth built for another app, which a preview never runs.";
        var bare = name.StartsWith("sha256:", StringComparison.Ordinal) ? name["sha256:".Length..] : name;
        if (bare.Length >= 12 && bare.All(char.IsAsciiHexDigitLower))
            return "an image ID, which a preview never runs: name an image by its repository.";
        return null;
    }

    private static bool NoNewPrivileges(string option) =>
        option.Trim().ToLowerInvariant() is "no-new-privileges" or "no-new-privileges:true" or "no-new-privileges=true";

    // host-gateway, or a loopback address: a name that leads to the Docker host itself.
    private static bool ToTheHost(string entry)
    {
        var sep = entry.IndexOfAny([':', '=']);
        var value = sep < 0 ? entry : entry[(sep + 1)..];
        value = value.Trim().Trim('[', ']');
        return value is "host-gateway" || value.StartsWith("127.", StringComparison.Ordinal) || value is "::1" or "0.0.0.0" or "::";
    }

    // ---- Paths and values ----

    /// <summary>Whether a path is the folder or inside it (both absolute, Gitberth's).</summary>
    internal static bool Inside(string folder, string path)
    {
        var root = Normalize(folder);
        var full = Normalize(path);
        return full == root || full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a path in the release goes through a link that leads out of it, or to an absolute path (which the Docker host
    /// reads as one of its own): Docker follows the links in a bind mount's source. A part that isn't there yet ends the
    /// walk (Docker makes the folder).
    /// </summary>
    internal static bool LinksOut(string release, string path)
    {
        var root = Normalize(release);
        var full = Normalize(path);
        if (!Inside(root, full)) return true;
        var current = root;
        foreach (var part in full[root.TrimEnd('/').Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.TrimEnd('/') + "/" + part;
            for (var hops = 0; ; hops++)
            {
                string? link;
                try
                {
                    link = new FileInfo(current).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
                if (link is null) break;
                if (hops >= 40 || Path.IsPathRooted(link)) return true;
                current = Normalize(current[..current.LastIndexOf('/')] + "/" + link);
                if (!Inside(root, current)) return true;
            }
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
        }
        return false;
    }

    // A path with forward slashes and no . or .. parts.
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        var drive = path.Length > 1 && path[1] == ':' ? "" : "/";
        return drive + string.Join('/', parts);
    }

    private static string Join(string folder, string rel) => rel.Length == 0 ? folder.TrimEnd('/') : folder.TrimEnd('/') + "/" + rel.TrimStart('/');

    // A source as the log shows it: relative to the release when it's in it.
    private static string Shown(string source, ComposeTarget target) =>
        Inside(target.ReleaseDir, source) ? "./" + Path.GetRelativePath(target.ReleaseDir, source).Replace('\\', '/') : source;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n.ToJsonString() : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : (int?)null) is int value
            ? value
            : null;

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
