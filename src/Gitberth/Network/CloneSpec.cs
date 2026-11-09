using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gitberth.Network;

/// <summary>A volume the new container mounts by name: postgres-data, or an anonymous one's 64-hex name.</summary>
public sealed record CarriedVolume(string Name, string Destination, bool Anonymous);

/// <summary>
/// The body of POST /containers/create for a container like an existing one, with other published ports: built from its
/// inspect as raw JSON, so every setting Docker returns goes back, and only the keys listed here change. Pure: JSON in,
/// JSON out.
/// <list type="bullet">
/// <item>Config as it is, but Image is the tag only while the tag still names the image it runs (else a digest of that
/// image, so a port change never upgrades anything), no MacAddress (create rejects it), and every new container port
/// exposed.</item>
/// <item>HostConfig as it is, but PortBindings replaced (explicit host ports), PublishAllPorts off, anonymous volumes
/// mounted by name, MaskedPaths and ReadonlyPaths left to Docker unless they're empty, and a NetworkMode that exists.</item>
/// <item>One endpoint per network, with its aliases, static IPs, links, options, priority and (when it runs) its MAC,
/// but none of the old container's live state.</item>
/// </list>
/// </summary>
public static partial class CloneSpec
{
    // Docker's own lists (moby's oci defaults). They grow between versions and with the host's CPUs, so a container
    // whose lists hold only these gets null, and the daemon writes its current defaults again.
    private static readonly HashSet<string> DefaultMasked = new(StringComparer.Ordinal)
    {
        "/proc/acpi", "/proc/asound", "/proc/interrupts", "/proc/kcore", "/proc/keys", "/proc/latency_stats", "/proc/sched_debug",
        "/proc/scsi", "/proc/timer_list", "/proc/timer_stats", "/sys/devices/virtual/powercap", "/sys/firmware",
    };

    private static readonly HashSet<string> DefaultReadonly = new(StringComparer.Ordinal)
    {
        "/proc/bus", "/proc/fs", "/proc/irq", "/proc/sys", "/proc/sysrq-trigger",
    };

    // What an endpoint says about the old container's attachment; Docker works it out again for the new one.
    private static readonly string[] EndpointState =
        ["NetworkID", "EndpointID", "Gateway", "IPAddress", "IPPrefixLen", "IPv6Gateway", "GlobalIPv6Address", "GlobalIPv6PrefixLen", "DNSNames"];

    /// <summary>The create body for a copy of the container with <paramref name="ports"/> as its ports.</summary>
    /// <param name="inspect">GET /containers/{id}/json of the container.</param>
    /// <param name="image">GET /images/{Config.Image}/json, or null when the tag is gone.</param>
    /// <param name="ports">Every port it should have afterwards. Internal ones are only exposed.</param>
    /// <param name="networks">GET /networks: the networks that exist.</param>
    /// <param name="lanHostIp">The address a LAN rule without one is published on: "" for every address of the host.</param>
    /// <param name="running">GET /images/{Image}/json, the image it runs by its ID: its digest pins it once the tag has moved.</param>
    public static JsonObject Build(JsonObject inspect, JsonObject? image, IReadOnlyList<PortRule> ports, JsonArray networks, string lanHostIp = "",
        JsonObject? running = null)
    {
        var body = inspect["Config"] is JsonObject config ? (JsonObject)config.DeepClone() : new JsonObject();
        body["Image"] = ImageFor(inspect, image, running);
        body.Remove("MacAddress");

        var exposed = body["ExposedPorts"] as JsonObject ?? new JsonObject();
        foreach (var rule in ports)
            if (!exposed.ContainsKey(rule.Key)) exposed[rule.Key] = new JsonObject();
        if (exposed.Count > 0) body["ExposedPorts"] = exposed;

        var hostConfig = inspect["HostConfig"] is JsonObject hc ? (JsonObject)hc.DeepClone() : new JsonObject();
        var bindings = new JsonObject();
        foreach (var group in Bindings(inspect, ports, lanHostIp).GroupBy(b => b.Key))
            bindings[group.Key] = new JsonArray(group.Select(b => (JsonNode)new JsonObject
            {
                ["HostIp"] = b.HostIp,
                ["HostPort"] = b.HostPort.ToString(CultureInfo.InvariantCulture),
            }).ToArray());
        hostConfig["PortBindings"] = bindings;
        hostConfig["PublishAllPorts"] = false;
        CarryVolumes(inspect, hostConfig);
        hostConfig["MaskedPaths"] = SystemPaths(hostConfig["MaskedPaths"], p => DefaultMasked.Contains(p) || ThermalThrottle().IsMatch(p));
        hostConfig["ReadonlyPaths"] = SystemPaths(hostConfig["ReadonlyPaths"], DefaultReadonly.Contains);

        var endpoints = Endpoints(inspect, networks);
        var mode = NetworkModeFor(inspect, networks);
        if (mode.Mode is not null) hostConfig["NetworkMode"] = mode.Mode;
        // Its namespace's hostname, ports, DNS and hosts file are the other container's; create refuses its own (the image's
        // exposed ports included: moby's validateNetContainerMode).
        if (mode.Mode?.StartsWith("container:", StringComparison.Ordinal) == true)
        {
            body.Remove("Hostname");
            body.Remove("Domainname");
            body.Remove("ExposedPorts");
            hostConfig.Remove("Dns");
            hostConfig.Remove("ExtraHosts");
        }

        body["HostConfig"] = hostConfig;
        body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        return body;
    }

    /// <summary>
    /// The bindings the new container gets: an explicit host port on an exact address for each published rule. A rule's
    /// own address counts only while it belongs to the rule's access: a row read as LAN on "" and switched to Host only
    /// is published on 127.0.0.1, not on "" again. A LAN rule without a LAN address of its own takes the form the
    /// container has for the LAN (<see cref="LanFormOf"/>), so 0.0.0.0 stays 0.0.0.0 through a move or a round trip.
    /// </summary>
    public static List<NewBinding> Bindings(JsonObject inspect, IReadOnlyList<PortRule> ports, string lanHostIp)
    {
        var all = ContainerPorts.Read(inspect);
        var current = all.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var result = new List<NewBinding>();
        foreach (var rule in ports.Where(r => r.Published && r.HostPort is not null))
        {
            var own = rule.HostIp is { } given && (rule.Access == PortAccess.Custom || ContainerPorts.AccessOf(given) == rule.Access) ? given : null;
            var ip = own ?? rule.Access switch
            {
                // A LAN port nobody touched keeps the address Docker has (0.0.0.0 stays IPv4 only), so it doesn't count as a change.
                PortAccess.Lan => current.TryGetValue(rule.Key, out var now)
                    && now.Bindings.FirstOrDefault(b => b.Access == PortAccess.Lan && b.HostPort == rule.HostPort && !b.Random) is { } same
                    ? same.HostIp
                    : KeptForm(LanFormOf(all, rule.Key), lanHostIp),
                PortAccess.HostOnly => "127.0.0.1",
                _ => "",
            };
            var binding = new NewBinding(rule.ContainerPort, rule.Protocol, ip, rule.HostPort!.Value);
            if (!result.Contains(binding)) result.Add(binding);
        }
        return result;
    }

    /// <summary>
    /// The LAN-wide address ("", 0.0.0.0 or ::) the container's original configuration uses for the LAN, for a port that
    /// becomes LAN without one of its own (a Host only row switched back, a port published anew): the port's own LAN
    /// binding first, on any host port, else the one form every other LAN binding of the container shares. Null when it
    /// has none, or several: the host's choice (HostPortChecks.LanHostIpAsync) applies then.
    /// </summary>
    internal static string? LanFormOf(IReadOnlyList<CurrentPort> current, string key)
    {
        if (current.FirstOrDefault(p => p.Key == key)?.Bindings.FirstOrDefault(b => b.Access == PortAccess.Lan) is { } own) return own.HostIp;
        var forms = current.SelectMany(p => p.Bindings).Where(b => b.Access == PortAccess.Lan).Select(b => b.HostIp).Distinct(StringComparer.Ordinal).ToList();
        return forms.Count == 1 ? forms[0] : null;
    }

    // The container's own form, unless it would reach further than the host's choice: on a host with a global IPv6 address
    // (the LAN goes on 0.0.0.0 then), a new LAN port isn't put on IPv6 too because an old one was.
    private static string KeptForm(string? form, string lanHostIp) =>
        form is null || (lanHostIp == "0.0.0.0" && form != "0.0.0.0") ? lanHostIp : form;

    /// <summary>
    /// The image to create it from: the tag while it still names the image the container runs, so docker ps keeps showing
    /// postgres:18. Once a pull has moved the tag, a digest of the image it runs (postgres@sha256:…), so the change doesn't
    /// upgrade it and the image keeps its repository's name, which is how Gitberth knows a database. The bare ID only for
    /// an image with no digest: built on the host, or, on a containerd image store, one whose only tag moved on (Docker
    /// then keeps no name for it at all, and refuses repo@sha256:… for it).
    /// </summary>
    /// <param name="image">GET /images/{tag}/json, or null when the tag is gone.</param>
    /// <param name="running">GET /images/{Image}/json, the image it runs by its ID; null when it couldn't be read.</param>
    public static string ImageFor(JsonObject inspect, JsonObject? image, JsonObject? running = null)
    {
        var tag = Str(inspect["Config"], "Image");
        var id = Str(inspect, "Image");
        if (tag.Length == 0) return id;
        if (id.Length == 0 || (image is not null && Str(image, "Id") == id)) return tag;
        if (running is null || Str(running, "Id") != id) return id;
        var digests = (running["RepoDigests"] as JsonArray ?? []).OfType<JsonValue>()
            .Select(d => d.TryGetValue<string>(out var s) ? s : "")
            .Where(d => d.Contains("@sha256:", StringComparison.Ordinal))
            .ToList();
        return digests.FirstOrDefault(d => Repository(d) == Repository(tag)) ?? digests.FirstOrDefault() ?? id;
    }

    // "postgres" for postgres:18, docker.io/library/postgres or postgres@sha256:…; a registry's port stays.
    private static string Repository(string reference)
    {
        var name = reference.Split('@')[0];
        var colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/')) name = name[..colon];
        foreach (var prefix in new[] { "docker.io/", "index.docker.io/", "library/" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        return name;
    }

    /// <summary>
    /// The network mode the new container gets. A network the old one names but that's gone (postgres's "gitdock", removed
    /// in a rename while its only network is gitberth) would fail the create, so it becomes the network the container's
    /// published ports use: its highest-priority endpoint. Null when there's no network left to use.
    /// </summary>
    public static (string? Mode, string? Gone) NetworkModeFor(JsonObject inspect, JsonArray networks)
    {
        var mode = Str(inspect["HostConfig"], "NetworkMode");
        if (mode is "" or "default" or "host" or "none" || mode.StartsWith("container:", StringComparison.Ordinal)) return (mode.Length == 0 ? null : mode, null);
        var endpoints = inspect["NetworkSettings"]?["Networks"] as JsonObject ?? [];
        var named = NetworkName(networks, mode);
        if (named is not null && endpoints.ContainsKey(named)) return (mode, null);
        var best = endpoints
            .Where(e => NetworkName(networks, e.Key) is not null)
            .OrderByDescending(e => ContainerPorts.Number(e.Value, "GwPriority"))
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key)
            .FirstOrDefault();
        if (best is not null) return (best, named is null ? mode : null);
        return named is not null ? (mode, null) : (null, mode);
    }

    /// <summary>The volumes the new container mounts: named ones, and anonymous ones carried over by their names.</summary>
    public static List<CarriedVolume> Volumes(JsonObject inspect)
    {
        var named = NamedDestinations(inspect["HostConfig"] as JsonObject);
        return (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(m => Str(m, "Type") == "volume" && Str(m, "Name").Length > 0)
            .Select(m => new CarriedVolume(Str(m, "Name"), Clean(Str(m, "Destination")), !named.Contains(Clean(Str(m, "Destination")))))
            .ToList();
    }

    /// <summary>The network's name, for a name or an ID of one that exists; null when there's no such network.</summary>
    internal static string? NetworkName(JsonArray networks, string nameOrId)
    {
        foreach (var n in networks.OfType<JsonObject>())
        {
            var name = Str(n, "Name");
            var id = Str(n, "Id");
            if (name == nameOrId || (nameOrId.Length >= 12 && id.StartsWith(nameOrId, StringComparison.Ordinal))) return name;
        }
        return null;
    }

    // Where a bind or a mount with a source puts something: the new container gets the same there without help.
    private static HashSet<string> NamedDestinations(JsonObject? hostConfig)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bind in (hostConfig?["Binds"] as JsonArray ?? []).OfType<JsonValue>())
            if (bind.TryGetValue<string>(out var spec) && ParseBind(spec) is { Source: not null } b) named.Add(Clean(b.Destination));
        foreach (var mount in (hostConfig?["Mounts"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(mount, "Source").Length > 0 || Str(mount, "Type") != "volume") named.Add(Clean(Str(mount, "Target")));
        return named;
    }

    // Each anonymous volume (an image VOLUME, or -v /path) shows only in the top-level Mounts, under a 64-hex name.
    // Without a mount naming it, the new container would get a new, empty one, so each is mounted by name, as Compose
    // does. Tmpfs mounts start empty either way.
    private static void CarryVolumes(JsonObject inspect, JsonObject hostConfig)
    {
        var mountedAt = (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var covered = NamedDestinations(hostConfig);
        // "/data" alone in Binds would make a new anonymous volume: it goes, and the one it made is mounted by name below.
        if (hostConfig["Binds"] is JsonArray binds)
            foreach (var bind in binds.Where(b => b is JsonValue v && v.TryGetValue<string>(out var s) && ParseBind(s).Source is null).ToList())
                binds.Remove(bind);

        var mounts = hostConfig["Mounts"] as JsonArray;
        foreach (var mount in (mounts ?? []).OfType<JsonObject>())
        {
            var target = Clean(Str(mount, "Target"));
            if (Str(mount, "Type") == "volume" && Str(mount, "Source").Length == 0
                && mountedAt.FirstOrDefault(m => Str(m, "Type") == "volume" && Clean(Str(m, "Destination")) == target) is { } made
                && Str(made, "Name").Length > 0)
                mount["Source"] = Str(made, "Name");
            covered.Add(target);
        }

        foreach (var m in mountedAt.Where(m => Str(m, "Type") == "volume"))
        {
            var destination = Clean(Str(m, "Destination"));
            var name = Str(m, "Name");
            if (name.Length == 0 || destination.Length == 0 || !covered.Add(destination)) continue;
            mounts ??= [];
            mounts.Add(new JsonObject
            {
                ["Type"] = "volume",
                ["Source"] = name,
                ["Target"] = destination,
                ["ReadOnly"] = m["RW"]?.GetValueKind() == JsonValueKind.False,
            });
        }
        if (mounts is not null) hostConfig["Mounts"] = mounts;
    }

    // An empty list is what --security-opt systempaths=unconfined asked for, and the daemon fills in its defaults only
    // for null, so [] stays []. A list of Docker's defaults becomes null; one with anything else stays as it was set.
    private static JsonNode? SystemPaths(JsonNode? list, Func<string, bool> isDefault)
    {
        if (list is not JsonArray paths) return list;
        if (paths.Count == 0) return new JsonArray();
        return paths.All(p => p is JsonValue v && v.TryGetValue<string>(out var s) && isDefault(s)) ? null : list;
    }

    private static JsonObject Endpoints(JsonObject inspect, JsonArray networks)
    {
        var running = ContainerPorts.Running(inspect);
        var id = Str(inspect, "Id");
        var shortId = id.Length >= 12 ? id[..12] : id;
        var drivers = networks.OfType<JsonObject>().Where(n => Str(n, "Name").Length > 0)
            .GroupBy(n => Str(n, "Name")).ToDictionary(g => g.Key, g => Str(g.First(), "Driver"), StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var (name, node) in inspect["NetworkSettings"]?["Networks"] as JsonObject ?? [])
        {
            // A network removed since: the create would fail on it, and the old container can't start with it either.
            if (node is not JsonObject endpoint || !drivers.TryGetValue(name, out var driver)) continue;
            var copy = (JsonObject)endpoint.DeepClone();
            foreach (var field in EndpointState) copy.Remove(field);
            // Engines before 26 listed the short ID as an alias; the new container gets its own.
            if (copy["Aliases"] is JsonArray aliases)
                foreach (var alias in aliases.Where(a => a is JsonValue v && v.TryGetValue<string>(out var s) && s == shortId).ToList())
                    aliases.Remove(alias);
            // Only a running container shows its MAC (Docker clears it at stop). ipvlan shares its parent's and refuses one.
            if (!running || Str(copy, "MacAddress").Length == 0 || driver is "ipvlan" or "host" or "null") copy.Remove("MacAddress");
            result[name] = copy;
        }
        return result;
    }

    internal static HashSet<string> NetworkNames(JsonArray networks) =>
        networks.OfType<JsonObject>().Select(n => Str(n, "Name")).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>A bind as -v writes it, source:destination[:options]; the source is null for "/path" alone.</summary>
    internal static (string? Source, string Destination) ParseBind(string spec)
    {
        var parts = spec.Split(':');
        return parts.Length == 1 ? (null, parts[0]) : (parts[0], parts[1]);
    }

    internal static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    [GeneratedRegex(@"^/sys/devices/system/cpu/cpu\d+/thermal_throttle$")]
    private static partial Regex ThermalThrottle();
}

/// <summary>A binding the new container gets, with an explicit host port.</summary>
/// <param name="HostIp">"" for every address of the host, or one address.</param>
public sealed record NewBinding(int ContainerPort, string Protocol, string HostIp, int HostPort)
{
    public string Key => ContainerPorts.Key(ContainerPort, Protocol);

    public PortAccess Access => ContainerPorts.AccessOf(HostIp);
}
