using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Network;

namespace Gitberth.Docker;

public sealed record EnvValue(string Key, string Value, bool IsSecret);

/// <summary>A network a container joins, and what it answers to there besides its container name.</summary>
/// <param name="Aliases">An app's network name (its slug), and a name its row adds, like db; none for a deploy's temporary container.</param>
/// <param name="IPv4Address">Its own address there (an own LAN address); null for one Docker picks.</param>
/// <param name="LowPriority">
/// Joined at gateway priority -1, so it never becomes the container's gateway: its published ports and its way out stay
/// on the shared network.
/// </param>
/// <param name="Id">
/// The network's ID, which docker run is given in place of a name with capitals: the CLI lower-cases what --network's
/// name= says (Docker 29's does), and Docker's names are exact, so MediaNet would be asked for as medianet. An ID has no
/// capitals. Null for a name docker run can be given as it is.
/// </param>
public sealed record NetworkJoin(string Name, IReadOnlyList<string> Aliases, string? IPv4Address = null, bool LowPriority = false, string? Id = null);

/// <summary>Everything needed to `docker run` an app's container.</summary>
public sealed class ContainerSpec
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    /// <summary>The networks it joins, the shared one first: that one carries its published ports and its way out.</summary>
    public required IReadOnlyList<NetworkJoin> Networks { get; init; }
    /// <summary>The engine takes gateway priorities (API 1.48, Docker 28). Before that a network gives none, and the CLI would refuse one.</summary>
    public bool GatewayPriority { get; init; } = true;
    /// <summary>
    /// The LAN's ports go on 0.0.0.0, IPv4 only, rather than on every address: the host has an IPv6 address the internet can
    /// reach, and the LAN is this network, not the internet.
    /// </summary>
    public bool LanIpv4Only { get; init; }
    public string RestartPolicy { get; init; } = "unless-stopped";
    public IReadOnlyList<PortMapping> Ports { get; init; } = [];
    public IReadOnlyList<EnvValue> Env { get; init; } = [];
    public IReadOnlyList<VolumeMount> Volumes { get; init; } = [];
    public int? MemoryMb { get; init; }
    public double? Cpus { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>--log-opt values; empty unless the daemon's log driver needs them (see DockerCli.GetLogOptionsAsync).</summary>
    public IReadOnlyList<string> LogOptions { get; init; } = [];
    /// <summary>The app's settings beyond the classic ones (<see cref="AppDefinition.RuntimeExtras"/>); null for none, which adds nothing.</summary>
    public GitExtras? Extras { get; init; }

    /// <summary>
    /// Args for `docker run -d ...`, plus env vars for the docker CLI process itself.
    /// Secret values travel through that environment (`-e KEY` with no value), never through argv.
    /// </summary>
    public (List<string> Args, Dictionary<string, string> ProcessEnv) BuildRunCommand()
    {
        var x = Extras;
        var args = new List<string>
        {
            "run", "-d", "--name", Name,
            "--restart", x?.RestartRetries is > 0 and var retries && RestartPolicy == "on-failure" ? $"{RestartPolicy}:{retries}" : RestartPolicy,
        };
        foreach (var network in Networks) args.AddRange(["--network", NetworkArg(network, GatewayPriority)]);
        // The rotation the daemon's json-file driver needs doesn't go with another driver the app chose.
        if (x?.LogDriver is null) foreach (var option in LogOptions) args.AddRange(["--log-opt", option]);
        var processEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Labels) args.AddRange(["--label", $"{key}={value}"]);
        foreach (var port in Ports) args.AddRange(PortArg(port, LanIpv4Only));
        foreach (var e in Env)
        {
            args.Add("-e");
            if (e.IsSecret && EnvPassing.CanPassViaEnvironment(e.Key))
            {
                args.Add(e.Key);
                processEnv[e.Key] = e.Value;
            }
            else
            {
                args.Add($"{e.Key}={e.Value}");
            }
        }
        foreach (var v in Volumes) args.AddRange(["-v", FormatVolume(v)]);
        if (MemoryMb is > 0) args.AddRange(["--memory", $"{MemoryMb}m"]);
        if (Cpus is > 0) args.AddRange(["--cpus", Cpus.Value.ToString("0.##", CultureInfo.InvariantCulture)]);
        if (x is not null) args.AddRange(ExtraArgs(x));
        args.Add(Image);
        // An entrypoint of several words: docker run takes its first, and the rest go before the command.
        if (x?.Entrypoint is { Count: > 1 } entrypoint) args.AddRange(entrypoint.Skip(1));
        if (x?.Command is { } command) args.AddRange(command);
        return (args, processEnv);
    }

    /// <summary>
    /// The flags for a Git app's settings beyond the classic ones, in one fixed order: resources, then names and DNS, then
    /// the process, health, labels, devices, security, mounts, logging, stopping and the terminal.
    /// </summary>
    internal static List<string> ExtraArgs(GitExtras x)
    {
        var args = new List<string>();
        void Add(string flag, string? value)
        {
            if (value is not null) args.AddRange([flag, value]);
        }
        void Flag(string flag, bool? on)
        {
            if (on == true) args.Add(flag);
        }
        void Each(string flag, IEnumerable<string>? values)
        {
            foreach (var v in values ?? []) args.AddRange([flag, v]);
        }

        Add("--memory-reservation", Bytes(x.MemoryReservation));
        Add("--memory-swap", x.MemorySwap == -1 ? "-1" : Bytes(x.MemorySwap));
        Add("--cpu-shares", x.CpuShares?.ToString(CultureInfo.InvariantCulture));
        Add("--cpuset-cpus", x.CpusetCpus);
        Add("--pids-limit", x.PidsLimit?.ToString(CultureInfo.InvariantCulture));
        Add("--shm-size", Bytes(x.ShmSize));
        Each("--ulimit", x.Ulimits?.Select(u => $"{u.Name}={u.Soft.ToString(CultureInfo.InvariantCulture)}:{u.Hard.ToString(CultureInfo.InvariantCulture)}"));
        Add("--hostname", x.Hostname);
        Add("--domainname", x.Domainname);
        Each("--add-host", x.ExtraHosts);
        Each("--dns", x.Dns);
        Each("--dns-search", x.DnsSearch);
        Each("--dns-option", x.DnsOptions);
        Add("--workdir", x.WorkingDir);
        Add("--user", x.User);
        if (x.Entrypoint is { } entrypoint) Add("--entrypoint", entrypoint.Count == 0 ? "" : entrypoint[0]);
        if (x.Health is { Mode: HealthMode.Disabled }) args.Add("--no-healthcheck");
        else if (x.Health is { Mode: HealthMode.Custom } h)
        {
            // docker run takes a shell command: an exec form's words are quoted into one.
            Add("--health-cmd", h.Test switch
            {
                ["CMD-SHELL", var shell] => shell,
                { Count: > 1 } words when words[0] == "CMD" => string.Join(' ', words.Skip(1).Select(Quote)),
                { Count: > 0 } words => string.Join(' ', words.Select(Quote)),
                _ => null,
            });
            Add("--health-interval", Duration(h.Interval));
            Add("--health-timeout", Duration(h.Timeout));
            Add("--health-retries", h.Retries?.ToString(CultureInfo.InvariantCulture));
            Add("--health-start-period", Duration(h.StartPeriod));
            Add("--health-start-interval", Duration(h.StartInterval));
        }
        Each("--label", x.Labels?.Select(l => $"{l.Key}={l.Value}"));
        Each("--device", x.Devices?.Select(d => $"{d.HostPath}:{d.ContainerPath}{(d.Permissions is { Length: > 0 } p && p != "rwm" ? ":" + p : "")}"));
        // The CLI reads --gpus as CSV: device=0,1 must be quoted to stay one field.
        Add("--gpus", x.Gpus is { } gpus && gpus.Contains(',') ? $"\"{gpus}\"" : x.Gpus);
        Each("--cap-add", x.CapAdd);
        Each("--cap-drop", x.CapDrop);
        Flag("--privileged", x.Privileged);
        Each("--security-opt", x.SecurityOpt);
        Flag("--read-only", x.ReadOnlyRootfs);
        Flag("--init", x.Init);
        Each("--sysctl", x.Sysctls?.Select(s => $"{s.Key}={s.Value}"));
        Each("--tmpfs", x.Tmpfs?.Select(t =>
        {
            var options = new List<string>();
            if (t.ReadOnly) options.Add("ro");
            if (Bytes(t.Bytes) is { } size) options.Add("size=" + size);
            return options.Count == 0 ? t.Target : $"{t.Target}:{string.Join(',', options)}";
        }));
        Add("--log-driver", x.LogDriver);
        Each("--log-opt", x.LogOptions?.Select(o => $"{o.Key}={o.Value}"));
        Add("--stop-signal", x.StopSignal);
        Add("--stop-timeout", x.StopTimeout?.ToString(CultureInfo.InvariantCulture));
        Flag("-t", x.Tty);
        Flag("-i", x.OpenStdin);
        return args;
    }

    // A size as docker run takes it: 512m, 2g, or bytes.
    private static string? Bytes(long? bytes) => bytes switch
    {
        null or <= 0 => null,
        var b when b % (1L << 30) == 0 => $"{b >> 30}g",
        var b when b % (1L << 20) == 0 => $"{b >> 20}m",
        var b when b % (1L << 10) == 0 => $"{b >> 10}k",
        var b => b.Value.ToString(CultureInfo.InvariantCulture),
    };

    // A duration as Go writes it: 30s, 1500ms.
    private static string? Duration(TimeSpan? span) => span is not { } t || t <= TimeSpan.Zero ? null
        : t.Ticks % TimeSpan.TicksPerSecond == 0 ? $"{(long)t.TotalSeconds}s"
        : $"{(long)t.TotalMilliseconds}ms";

    /// <summary>The equivalent `docker run` command with secret values masked.</summary>
    public string ToDisplayString()
    {
        var (args, _) = BuildRunCommand();
        var secrets = Env.Where(e => e.IsSecret).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        // What follows the image is the command, whose secret-looking options are hidden.
        var command = Math.Max(0, (Extras?.Entrypoint?.Count ?? 1) - 1) + (Extras?.Command?.Count ?? 0);
        var image = args.Count - 1 - command;
        if (command > 0) args = [.. args.Take(image + 1), .. Security.Masking.Args(args.Skip(image + 1).ToList(), secrets)];
        var sb = new StringBuilder("docker");
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            // A secret variable shows as KEY=••••, passed by the environment or not; the rest, labels and log options by the
            // one rule, and the entrypoint and the health command too.
            if (i > 0 && args[i - 1] == "-e" && secrets.Contains(arg.Split('=')[0])) arg = arg.Split('=')[0] + "=" + Security.Masking.Mask;
            else if (i > 0 && i < image && args[i - 1] is "-e" or "--label" or "--log-opt" && arg.IndexOf('=') is > 0 and var eq)
                arg = arg[..(eq + 1)] + Security.Masking.Value(arg[..eq], arg[(eq + 1)..], secrets);
            else if (i > 0 && i < image && args[i - 1] is "--entrypoint" or "--health-cmd") arg = Security.Masking.Text(arg, secrets);
            var breakLine = (arg.StartsWith('-') && arg.Length > 1 && i > 2 && i < image) || i == image;
            sb.Append(breakLine ? " \\\n  " : " ").Append(Quote(arg));
        }
        return sb.ToString();
    }

    /// <summary>
    /// What the container is created with that saved settings can change short of a build, in a fixed order: its -p and
    /// --expose arguments and its --network ones. The web marker isn't in it, and neither is what the engine and the host
    /// decide (the gateway priority, a network's ID, and whether the LAN's ports are on IPv4 only), so an engine upgrade or
    /// a moved marker leaves it as it was.
    /// </summary>
    public string RuntimeKey => RuntimeKeyOf(Ports, Networks);

    /// <summary>The runtime key (<see cref="RuntimeKey"/>) of these ports and networks.</summary>
    public static string RuntimeKeyOf(IEnumerable<PortMapping> ports, IEnumerable<NetworkJoin> networks) =>
        string.Join(' ', ports.Select(p => string.Join(' ', PortArg(p))).Order(StringComparer.Ordinal)
            .Concat(networks.Select(n => "--network " + NetworkArg(n with { Id = null }, gatewayPriority: false)).Order(StringComparer.Ordinal)));

    /// <summary>
    /// Whether a container, as its inspect shows it, already runs with what this spec sets for its ports and networks: the
    /// same host ports for the same container ports, each reached by the same people (a LAN port on every address or on
    /// IPv4's only is one port, and so is a host-only one on 127.0.0.1 or ::1), every port the spec exposes, and every
    /// network with at least its names and address. The web marker doesn't count. A container the old pipeline started
    /// (-p 3000:80 --network gitberth --network-alias orb) is up to date with the same port saved today.
    /// </summary>
    public bool UpToDate(JsonObject inspect)
    {
        var config = NetworkPortConfig.From(inspect);
        if (config.PublishAll) return false;
        var bound = config.Bindings
            .Select(b => b.HostPort is int port ? Binding(ContainerPorts.AccessOf(b.HostIp), b.HostIp, port, b.ContainerPort, b.Protocol) : $"random:{b.ContainerPort}/{b.Protocol}")
            .ToHashSet(StringComparer.Ordinal);
        var wanted = Ports.Where(Published).Select(p => Binding(p.Access, p.HostIp, p.HostPort!.Value, p.ContainerPort, p.Protocol)).ToHashSet(StringComparer.Ordinal);
        if (!bound.SetEquals(wanted)) return false;
        // An Internal port only needs exposing; a published port, or the image's own EXPOSE, is exposed too.
        var exposed = config.Exposed.Select(e => (e.Port, e.Protocol)).Concat(config.Bindings.Select(b => (b.ContainerPort, b.Protocol))).ToHashSet();
        if (Ports.Where(p => !Published(p)).Any(p => !exposed.Contains((p.ContainerPort, p.Protocol)))) return false;
        var endpoints = inspect["NetworkSettings"]?["Networks"] as JsonObject;
        foreach (var network in Networks)
        {
            if (endpoints?[network.Name] is not JsonObject endpoint) return false;
            var names = Strings(endpoint["Aliases"]).Concat(Strings(endpoint["DNSNames"])).ToHashSet(StringComparer.Ordinal);
            if (!network.Aliases.All(names.Contains)) return false;
            if (network.IPv4Address is { } ip && ContainerPorts.Str(endpoint["IPAMConfig"], "IPv4Address") != ip) return false;
        }
        return true;
    }

    /// <summary>
    /// A port's argument, by who reaches it: -p for a published one (see <see cref="FormatPort"/>), --expose C for an
    /// Internal one, which containers on its networks reach by name; /udp for UDP.
    /// </summary>
    public static string[] PortArg(PortMapping p, bool lanIpv4Only = false) =>
        Published(p) ? ["-p", FormatPort(p, lanIpv4Only)] : ["--expose", $"{p.ContainerPort}{Udp(p)}"];

    /// <summary>
    /// -p for a published port: H:C for the LAN (0.0.0.0:H:C when it's IPv4 only), 127.0.0.1:H:C for the host only, IP:H:C
    /// for Custom; /udp for UDP.
    /// </summary>
    public static string FormatPort(PortMapping p, bool lanIpv4Only = false)
    {
        var ip = p.Access == PortAccess.Lan && lanIpv4Only ? "0.0.0.0" : ContainerPorts.BindAddress(p.Access, p.HostIp);
        return $"{(ip is { Length: > 0 } ? ip + ":" : "")}{p.HostPort}:{p.ContainerPort}{Udp(p)}";
    }

    /// <summary>
    /// --network's value: name=gitberth,alias=web for the shared network, name=media,gw-priority=-1,alias=web for another
    /// (without the priority on an engine that has none), and ,ip= for a fixed address. A network with an ID to go by is
    /// named by it (see <see cref="NetworkJoin.Id"/>).
    /// </summary>
    public static string NetworkArg(NetworkJoin network, bool gatewayPriority = true)
    {
        var parts = new List<string> { "name=" + (network.Id ?? network.Name) };
        if (network.LowPriority && gatewayPriority) parts.Add("gw-priority=-1");
        parts.AddRange(network.Aliases.Select(a => "alias=" + a));
        if (network.IPv4Address is { } ip) parts.Add("ip=" + ip);
        return string.Join(',', parts);
    }

    // An Internal port, or one without a host port, isn't published.
    private static bool Published(PortMapping p) => p.Access != PortAccess.Internal && p.HostPort is not null;

    private static string Udp(PortMapping p) => p.Protocol == "udp" ? "/udp" : "";

    // A binding by who reaches it: only one on a single address of the host (Custom) keeps that address.
    private static string Binding(PortAccess access, string? hostIp, int hostPort, int containerPort, string protocol) =>
        $"{access}:{(access == PortAccess.Custom ? Canonical(hostIp) : "")}:{hostPort}:{containerPort}/{protocol}";

    private static string Canonical(string? ip) =>
        IPAddress.TryParse(ip?.Trim().Trim('[', ']'), out var address) ? address.ToString() : ip?.Trim() ?? "";

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray list ? list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>() : [];

    private static string FormatVolume(VolumeMount v) => $"{v.Source}:{v.Target}{(v.ReadOnly ? ":ro" : "")}";

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@,+%•".Contains(c)) ? arg : "'" + arg.Replace("'", "'\\''") + "'";
}

public static partial class EnvPassing
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "PWD", "SHELL", "USER", "HOSTNAME", "LANG",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
    };

    // What the docker CLI (a Go program) or the loader reads. DOTNET_ and ASPNETCORE_ values don't affect it, so secrets
    // with those names can travel through its environment too, instead of its command line.
    private static readonly string[] ReservedPrefixes = ["DOCKER_", "BUILDX_", "BUILDKIT_", "LD_", "GIT_", "LC_"];

    /// <summary>True when a value can be handed to the docker CLI via its own environment without affecting the CLI itself.</summary>
    public static bool CanPassViaEnvironment(string key) =>
        PosixName().IsMatch(key)
        && !Reserved.Contains(key)
        && !ReservedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PosixName();
}

public static class VolumeNames
{
    public static bool IsBind(string source) => source.StartsWith('/');

    /// <summary>Named volumes are namespaced per app so two apps never share "data" by accident.</summary>
    public static string Resolve(string slug, string source) => IsBind(source) ? source : $"gb-{slug}-{source}";
}

public static class ContainerSpecFactory
{
    /// <summary>Marks a deploy's temporary container, so tidying up never touches anything else.</summary>
    public const string BridgeLabel = "gitberth.bridge";

    /// <summary>The deploy that started a container, and what started that deploy: Docker's events carry both, so the Health page can tell a deploy from a restart.</summary>
    public const string DeploymentLabel = "gitberth.deployment";
    public const string TriggerLabel = "gitberth.trigger";

    /// <summary>How the app came to Gitberth: registry, local, manual, or git. Absent means git.</summary>
    public const string SourceLabel = "gitberth.source";

    /// <param name="ports">The ports to publish, when they differ from the app's saved ones (a deploy moving its container port).</param>
    /// <param name="name">Another name than the app's container, for a new version that starts next to the old one.</param>
    /// <param name="restartPolicy">Another restart policy than the app's.</param>
    /// <param name="bridge">
    /// It's a deploy's temporary container (see DeployPipeline.SwapWithBridgeAsync): labelled as one, without the app's
    /// names on its networks until it has passed its checks, and never with the app's own addresses.
    /// </param>
    /// <param name="deploy">The deploy starting it, recorded in its labels.</param>
    /// <param name="networks">The app's own networks, when they're read apart from the app (a deploy reads them under the app's lock).</param>
    /// <param name="gatewayPriority">The engine takes gateway priorities (API 1.48, Docker 28).</param>
    /// <param name="lanIpv4Only">The host has an IPv6 address the internet can reach, so the LAN's ports go on IPv4 only.</param>
    /// <param name="networkIds">The IDs docker run is given for networks whose names have capitals, by name (see <see cref="NetworkJoin.Id"/>).</param>
    public static ContainerSpec For(AppDefinition app, string image, IReadOnlyList<EnvValue> env, string network, IReadOnlyList<string>? logOptions = null,
        IReadOnlyList<PortMapping>? ports = null, string? name = null, string? restartPolicy = null, bool bridge = false,
        (int Id, DeployTrigger Trigger)? deploy = null, IReadOnlyList<AppNetwork>? networks = null, bool gatewayPriority = true, bool lanIpv4Only = false,
        IReadOnlyDictionary<string, string>? networkIds = null) => new()
    {
        LogOptions = logOptions ?? [],
        Name = name ?? app.ContainerName,
        Image = image,
        Networks = Joins(app, network, networks ?? app.Networks, bridge, networkIds),
        GatewayPriority = gatewayPriority,
        LanIpv4Only = lanIpv4Only,
        RestartPolicy = restartPolicy ?? app.RestartPolicy,
        Ports = (ports ?? app.Ports).OrderBy(p => p.Id).ToList(),
        Env = env,
        Volumes = app.Volumes.OrderBy(v => v.Id)
            .Select(v => new VolumeMount { Source = VolumeNames.Resolve(app.Slug, v.Source), Target = v.Target, ReadOnly = v.ReadOnly })
            .ToList(),
        MemoryMb = app.MemoryLimitMb,
        Cpus = app.CpuLimit,
        Labels = Labels(app, bridge, deploy),
        Extras = GitExtras.Parse(app.RuntimeExtras),
    };

    /// <summary>
    /// The networks an app's container joins: the shared one under the app's network name (its slug), then each network of
    /// its own at a lower gateway priority, under its network name and the name its row adds, at its own address if it has
    /// one. A row for the shared network only adds its name there. A deploy's temporary container joins them with no name,
    /// so other apps reach it only once it has passed its checks, and leaves out the one the app has its own LAN address
    /// on: two containers can't hold one address, and joined without it, Docker would give it another of the block's,
    /// which a stopped container may be keeping (checked on CT 101). The address moves to the new container when it starts,
    /// once the old one has stopped.
    /// </summary>
    /// <param name="ids">The IDs to give docker run for networks whose names have capitals, by name (see <see cref="NetworkJoin.Id"/>).</param>
    public static List<NetworkJoin> Joins(AppDefinition app, string shared, IEnumerable<AppNetwork> networks, bool bridge = false,
        IReadOnlyDictionary<string, string>? ids = null)
    {
        var rows = networks.OrderBy(n => n.Id).ToList();
        IReadOnlyList<string> Names(AppNetwork? row) =>
            bridge ? [] : row?.Alias is { Length: > 0 } alias && alias != app.Slug ? [app.Slug, alias] : [app.Slug];
        var joins = new List<NetworkJoin> { new(shared, Names(rows.FirstOrDefault(n => n.Network == shared)), Id: ids?.GetValueOrDefault(shared)) };
        joins.AddRange(rows.Where(n => n.Network != shared && !(bridge && !string.IsNullOrWhiteSpace(n.IPv4Address))).Select(n =>
            new NetworkJoin(n.Network, Names(n), bridge || string.IsNullOrWhiteSpace(n.IPv4Address) ? null : n.IPv4Address.Trim(), LowPriority: true,
                Id: ids?.GetValueOrDefault(n.Network))));
        return joins;
    }

    /// <summary>The app's runtime key for its ports and networks as they're saved (<see cref="ContainerSpec.RuntimeKey"/>).</summary>
    /// <param name="networks">The app's own networks, when they're read apart from the app (a deploy reads them under the app's lock).</param>
    public static string RuntimeKey(AppDefinition app, string shared, IEnumerable<AppNetwork>? networks = null) =>
        ContainerSpec.RuntimeKeyOf(app.Ports.OrderBy(p => p.Id), Joins(app, shared, networks ?? app.Networks));

    private static Dictionary<string, string> Labels(AppDefinition app, bool bridge, (int Id, DeployTrigger Trigger)? deploy)
    {
        var labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = app.Slug };
        // A Git app says so once it has settings of its own beyond the classic ones. Without the label it's a Git app all
        // the same (RecreateRules), so a container from before, and its docker run, stay exactly as they were.
        if (app.RuntimeExtras is not null) labels[SourceLabel] = "git";
        if (bridge) labels[BridgeLabel] = "true";
        if (deploy is { } d)
        {
            labels[DeploymentLabel] = d.Id.ToString(CultureInfo.InvariantCulture);
            labels[TriggerLabel] = d.Trigger.ToString();
        }
        return labels;
    }
}
