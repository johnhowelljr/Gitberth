using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Security;
using Gitberth.Services;

namespace Gitberth.Network;

/// <summary>
/// <see cref="ContainerSettings"/> to and from Docker's JSON (section 2.1 of the design): read from an inspect or a create
/// body, written into a create body field by field. Pure: JSON in, JSON out.
/// <list type="bullet">
/// <item>Reading marks what the image supplies: its variables (FromImage), its labels (ImageLabels), and null for an
/// entrypoint, command, working folder, user, stop signal or health check that is the image's own.</item>
/// <item>Writing a field replaces exactly its paths in the body and nothing else, so whatever Gitberth has no field for
/// (VolumesFrom, Runtime, CgroupParent, IpcMode, Links and the rest) is carried over as the clone has it. An empty set of
/// fields changes nothing.</item>
/// </list>
/// </summary>
public static class ContainerSettingsJson
{
    /// <summary>Docker's shared memory when none is asked for: 64 MiB.</summary>
    public const long DefaultShmSize = 64L * 1024 * 1024;

    /// <summary>The paths each field writes, in the body: Config's keys at the top, HostConfig's under HostConfig.</summary>
    public static readonly IReadOnlyDictionary<SettingField, IReadOnlyList<string>> Paths = new Dictionary<SettingField, IReadOnlyList<string>>
    {
        [SettingField.Image] = ["Image"],
        // Docker gives a container the image's command only when it takes the image's entrypoint, so the two go together.
        [SettingField.Entrypoint] = ["Entrypoint", "Cmd"],
        [SettingField.Command] = ["Entrypoint", "Cmd"],
        [SettingField.WorkingDir] = ["WorkingDir"],
        [SettingField.User] = ["User"],
        [SettingField.Env] = ["Env"],
        [SettingField.Ports] = ["ExposedPorts", "HostConfig.PortBindings", "HostConfig.PublishAllPorts"],
        [SettingField.Mounts] = ["HostConfig.Binds", "HostConfig.Mounts", "HostConfig.Tmpfs"],
        [SettingField.NetworkMode] = ["HostConfig.NetworkMode", "NetworkingConfig", "HostConfig.PortBindings", "HostConfig.PublishAllPorts", "Hostname", "Domainname"],
        [SettingField.Hostname] = ["Hostname", "Domainname"],
        [SettingField.ExtraHosts] = ["HostConfig.ExtraHosts"],
        [SettingField.Dns] = ["HostConfig.Dns", "HostConfig.DnsSearch", "HostConfig.DnsOptions"],
        [SettingField.RestartPolicy] = ["HostConfig.RestartPolicy"],
        [SettingField.Memory] = ["HostConfig.Memory", "HostConfig.MemoryReservation", "HostConfig.MemorySwap"],
        [SettingField.Cpu] = ["HostConfig.NanoCpus", "HostConfig.CpuShares", "HostConfig.CpusetCpus", "HostConfig.CpuQuota", "HostConfig.CpuPeriod"],
        [SettingField.PidsLimit] = ["HostConfig.PidsLimit"],
        [SettingField.ShmSize] = ["HostConfig.ShmSize"],
        [SettingField.Ulimits] = ["HostConfig.Ulimits"],
        [SettingField.Health] = ["Healthcheck"],
        [SettingField.Labels] = ["Labels"],
        [SettingField.Devices] = ["HostConfig.Devices"],
        [SettingField.Gpus] = ["HostConfig.DeviceRequests"],
        [SettingField.Capabilities] = ["HostConfig.CapAdd", "HostConfig.CapDrop"],
        [SettingField.Privileged] = ["HostConfig.Privileged"],
        [SettingField.SecurityOpt] = ["HostConfig.SecurityOpt"],
        [SettingField.ReadOnlyRootfs] = ["HostConfig.ReadonlyRootfs"],
        [SettingField.Init] = ["HostConfig.Init"],
        [SettingField.Sysctls] = ["HostConfig.Sysctls"],
        [SettingField.Logging] = ["HostConfig.LogConfig"],
        [SettingField.Stop] = ["StopSignal", "StopTimeout"],
        [SettingField.Terminal] = ["Tty", "OpenStdin", "AttachStdin", "StdinOnce"],
    };

    /// <summary>The container's settings, from its inspect.</summary>
    /// <param name="image">GET /images/{inspect.Image}/json (by ID): what it supplies is marked FromImage, or left null.</param>
    public static ContainerSettings Read(JsonObject inspect, JsonObject? image)
    {
        var config = inspect["Config"] as JsonObject ?? new JsonObject();
        var host = inspect["HostConfig"] as JsonObject ?? new JsonObject();
        return ReadCore(config, host, ContainerPorts.Rules(inspect), MountsOf(host, inspect["Mounts"] as JsonArray), image, body: false);
    }

    /// <summary>A create body (a clone's, or NewBody's) read the same way.</summary>
    public static ContainerSettings ReadBody(JsonObject body, JsonObject? image)
    {
        var host = body["HostConfig"] as JsonObject ?? new JsonObject();
        var config = (JsonObject)body.DeepClone();
        config.Remove("HostConfig");
        config.Remove("NetworkingConfig");
        var asInspect = new JsonObject { ["Config"] = config.DeepClone(), ["HostConfig"] = host.DeepClone() };
        return ReadCore(config, host, ContainerPorts.Rules(asInspect), MountsOf(host, null), image, body: true);
    }

    /// <param name="body">A create body, where a missing entrypoint is the image's; in an inspect Docker has merged the image's in.</param>
    private static ContainerSettings ReadCore(JsonObject config, JsonObject host, IReadOnlyList<PortRule> ports, IReadOnlyList<MountSetting> mounts, JsonObject? image,
        bool body)
    {
        var fromImage = image?["Config"] as JsonObject;
        var ownEntrypoint = StrList(config["Entrypoint"]);
        var entrypoint = body && ownEntrypoint is null ? null : Own(ownEntrypoint, StrList(fromImage?["Entrypoint"]), image is not null);
        var cmd = StrList(config["Cmd"]);
        // The image's command, as the image's entrypoint runs it; with another entrypoint, the command is the container's own.
        IReadOnlyList<string>? command = cmd is null || cmd.Count == 0 ? null
            : entrypoint is null && image is not null && Same(cmd, StrList(fromImage?["Cmd"])) ? null
            : cmd;
        var imageEnv = (StrList(fromImage?["Env"]) ?? []).ToHashSet(StringComparer.Ordinal);
        var env = (StrList(config["Env"]) ?? [])
            .Select(e => e.Split('=', 2))
            .Select(kv => new EnvSetting(kv[0], kv.Length > 1 ? kv[1] : "", imageEnv.Contains(string.Join('=', kv)),
                Secret: Masking.IsSecret(kv[0], kv.Length > 1 ? kv[1] : "")))
            .ToList();
        var imageLabels = Map(fromImage?["Labels"]);
        var labels = Map(config["Labels"]).Where(l => !imageLabels.TryGetValue(l.Key, out var v) || v != l.Value)
            .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        var restart = host["RestartPolicy"] as JsonObject;
        var memory = Long(host, "Memory");
        var swap = Long(host, "MemorySwap");
        var nano = Long(host, "NanoCpus");
        var quota = Long(host, "CpuQuota");
        var period = Long(host, "CpuPeriod");
        var shm = Long(host, "ShmSize");
        var stopSignal = Str(config, "StopSignal");
        return new ContainerSettings
        {
            Image = Str(config, "Image"),
            Entrypoint = entrypoint,
            Command = command,
            WorkingDir = OwnText(Str(config, "WorkingDir"), Str(fromImage, "WorkingDir"), image is not null),
            User = OwnText(Str(config, "User"), Str(fromImage, "User"), image is not null),
            Env = env,
            Ports = ports,
            Mounts = mounts,
            NetworkMode = ModeOf(Str(host, "NetworkMode")),
            Hostname = NullIfEmpty(Str(config, "Hostname")),
            Domainname = NullIfEmpty(Str(config, "Domainname")),
            ExtraHosts = StrList(host["ExtraHosts"]) ?? [],
            Dns = StrList(host["Dns"]) ?? [],
            DnsSearch = StrList(host["DnsSearch"]) ?? [],
            DnsOptions = StrList(host["DnsOptions"]) ?? [],
            Restart = new RestartSetting(Str(restart, "Name") is { Length: > 0 } policy ? policy : "no", (int)Long(restart, "MaximumRetryCount")),
            Resources = new ResourceSetting(
                memory > 0 ? memory : null,
                Long(host, "MemoryReservation") is > 0 and var reservation ? reservation : null,
                // Docker gives a limit twice its size in swap unless told otherwise: that's no swap limit of its own.
                swap == -1 ? -1 : swap > 0 && swap != memory * 2 ? swap : null,
                nano > 0 ? nano / 1e9 : quota > 0 ? (double)quota / (period > 0 ? period : 100_000) : null,
                Long(host, "CpuShares") is > 0 and var shares ? shares : null,
                NullIfEmpty(Str(host, "CpusetCpus")),
                Long(host, "PidsLimit") is > 0 and var pids ? pids : null),
            ShmSizeBytes = shm > 0 && shm != DefaultShmSize ? shm : null,
            Ulimits = (host["Ulimits"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(u => new UlimitSetting(Str(u, "Name"), Long(u, "Soft"), Long(u, "Hard"))).ToList(),
            Health = HealthOf(config["Healthcheck"] as JsonObject, fromImage?["Healthcheck"] as JsonObject, image is not null),
            Labels = labels,
            ImageLabels = imageLabels,
            Devices = (host["Devices"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(d => new DeviceSetting(Str(d, "PathOnHost"), Str(d, "PathInContainer"), Str(d, "CgroupPermissions") is { Length: > 0 } p ? p : "rwm"))
                .ToList(),
            Gpus = GpusOf(host["DeviceRequests"] as JsonArray),
            CapAdd = StrList(host["CapAdd"]) ?? [],
            CapDrop = StrList(host["CapDrop"]) ?? [],
            Privileged = Bool(host, "Privileged"),
            SecurityOpt = StrList(host["SecurityOpt"]) ?? [],
            ReadOnlyRootfs = Bool(host, "ReadonlyRootfs"),
            Init = Bool(host, "Init"),
            Sysctls = Map(host["Sysctls"]),
            Logging = new LogSetting(Str(host["LogConfig"], "Type"), Map(host["LogConfig"]?["Config"])),
            StopSignal = stopSignal.Length == 0 || (image is not null && stopSignal == Str(fromImage, "StopSignal")) ? null : stopSignal,
            StopTimeoutSeconds = config["StopTimeout"] is JsonValue t && t.GetValueKind() == JsonValueKind.Number ? (int)Long(config, "StopTimeout") : null,
            Tty = Bool(config, "Tty"),
            OpenStdin = Bool(config, "OpenStdin"),
        };
    }

    /// <summary>
    /// Writes only <paramref name="fields"/> onto the body (section 2.5). An empty set changes nothing. A Gitberth-made
    /// container that switches back to a bridge network goes on the shared network, gitberth.
    /// </summary>
    public static void Write(JsonObject body, ContainerSettings settings, IReadOnlySet<SettingField> fields) => Write(body, settings, fields, "gitberth");

    /// <param name="sharedNetwork">The network a Gitberth-made container goes back to when it leaves host, none or container:x.</param>
    public static void Write(JsonObject body, ContainerSettings settings, IReadOnlySet<SettingField> fields, string sharedNetwork)
    {
        if (fields.Count == 0) return;
        var host = body["HostConfig"] as JsonObject ?? new JsonObject();
        body["HostConfig"] = host;
        foreach (var field in Enum.GetValues<SettingField>().Where(fields.Contains).OrderBy(f => f == SettingField.NetworkMode ? 1 : 0))
            WriteField(body, host, settings, field, sharedNetwork, lanHostIp: "");
    }

    private static void WriteField(JsonObject body, JsonObject host, ContainerSettings s, SettingField field, string sharedNetwork, string lanHostIp)
    {
        switch (field)
        {
            case SettingField.Image:
                body["Image"] = s.Image;
                break;
            case SettingField.Entrypoint or SettingField.Command:
                SetList(body, "Entrypoint", s.Entrypoint);
                SetList(body, "Cmd", s.Command);
                break;
            case SettingField.WorkingDir:
                body["WorkingDir"] = s.WorkingDir ?? "";
                break;
            case SettingField.User:
                body["User"] = s.User ?? "";
                break;
            case SettingField.Env:
                body["Env"] = new JsonArray(s.Env.Select(e => (JsonNode)JsonValue.Create(e.Key + "=" + e.Value)).ToArray());
                break;
            case SettingField.Ports:
                WritePorts(body, host, s.Ports, lanHostIp);
                break;
            case SettingField.Mounts:
                WriteMounts(host, s.Mounts);
                break;
            case SettingField.NetworkMode:
                WriteNetworkMode(body, host, s, sharedNetwork);
                break;
            case SettingField.Hostname:
                if (Str(host, "NetworkMode").StartsWith("container:", StringComparison.Ordinal)) break;
                SetText(body, "Hostname", s.Hostname);
                SetText(body, "Domainname", s.Domainname);
                break;
            case SettingField.ExtraHosts:
                host["ExtraHosts"] = Array(s.ExtraHosts);
                break;
            case SettingField.Dns:
                host["Dns"] = Array(s.Dns);
                host["DnsSearch"] = Array(s.DnsSearch);
                host["DnsOptions"] = Array(s.DnsOptions);
                break;
            case SettingField.RestartPolicy:
                host["RestartPolicy"] = RestartJson(s.Restart);
                break;
            case SettingField.Memory:
                host["Memory"] = s.Resources.MemoryBytes ?? 0;
                host["MemoryReservation"] = s.Resources.MemoryReservationBytes ?? 0;
                // 0 lets Docker give the limit twice its size in swap, as docker run --memory does.
                host["MemorySwap"] = s.Resources.MemorySwapBytes ?? 0;
                break;
            case SettingField.Cpu:
                host["NanoCpus"] = s.Resources.Cpus is { } cpus ? (long)Math.Round(cpus * 1e9) : 0;
                host["CpuShares"] = s.Resources.CpuShares ?? 0;
                host["CpusetCpus"] = s.Resources.CpusetCpus ?? "";
                // Docker refuses NanoCpus next to a quota: a limit in CPUs replaces the quota it was read from.
                host["CpuQuota"] = 0;
                host["CpuPeriod"] = 0;
                break;
            case SettingField.PidsLimit:
                host["PidsLimit"] = s.Resources.PidsLimit is { } pids ? JsonValue.Create(pids) : null;
                break;
            case SettingField.ShmSize:
                host["ShmSize"] = s.ShmSizeBytes ?? 0;
                break;
            case SettingField.Ulimits:
                host["Ulimits"] = s.Ulimits.Count == 0 ? null
                    : new JsonArray(s.Ulimits.Select(u => (JsonNode)new JsonObject { ["Name"] = u.Name, ["Soft"] = u.Soft, ["Hard"] = u.Hard }).ToArray());
                break;
            case SettingField.Health:
                if (HealthJson(s.Health) is { } check) body["Healthcheck"] = check;
                else body.Remove("Healthcheck");
                break;
            case SettingField.Labels:
                body["Labels"] = MapJson(s.Labels);
                break;
            case SettingField.Devices:
                host["Devices"] = new JsonArray(s.Devices.Select(d => (JsonNode)new JsonObject
                {
                    ["PathOnHost"] = d.HostPath, ["PathInContainer"] = d.ContainerPath, ["CgroupPermissions"] = d.Permissions,
                }).ToArray());
                break;
            case SettingField.Gpus:
                WriteGpus(host, s.Gpus);
                break;
            case SettingField.Capabilities:
                host["CapAdd"] = s.CapAdd.Count == 0 ? null : Array(s.CapAdd);
                host["CapDrop"] = s.CapDrop.Count == 0 ? null : Array(s.CapDrop);
                break;
            case SettingField.Privileged:
                host["Privileged"] = s.Privileged;
                break;
            case SettingField.SecurityOpt:
                host["SecurityOpt"] = s.SecurityOpt.Count == 0 ? null : Array(s.SecurityOpt);
                break;
            case SettingField.ReadOnlyRootfs:
                host["ReadonlyRootfs"] = s.ReadOnlyRootfs;
                break;
            case SettingField.Init:
                host["Init"] = s.Init ? true : null;
                break;
            case SettingField.Sysctls:
                host["Sysctls"] = s.Sysctls.Count == 0 ? null : MapJson(s.Sysctls);
                break;
            case SettingField.Logging:
                host["LogConfig"] = new JsonObject { ["Type"] = s.Logging.Driver, ["Config"] = MapJson(s.Logging.Options ?? new Dictionary<string, string>()) };
                break;
            case SettingField.Stop:
                SetText(body, "StopSignal", s.StopSignal);
                if (s.StopTimeoutSeconds is { } timeout) body["StopTimeout"] = timeout;
                else body.Remove("StopTimeout");
                break;
            case SettingField.Terminal:
                body["Tty"] = s.Tty;
                body["OpenStdin"] = s.OpenStdin;
                body["AttachStdin"] = false;
                body["StdinOnce"] = false;
                break;
        }
    }

    /// <summary>
    /// A create body from scratch, for creates and for recreating from a snapshot: every field written, on
    /// <paramref name="networks"/> (the shared one first, the others at gateway priority -1 when the engine has priorities).
    /// </summary>
    /// <param name="lanHostIp">The address a LAN port is published on: "" for every address of the host.</param>
    public static JsonObject NewBody(ContainerSettings settings, IReadOnlyList<NetworkJoin> networks, string lanHostIp, bool gatewayPriority)
    {
        var body = new JsonObject();
        var host = new JsonObject();
        body["HostConfig"] = host;
        foreach (var field in Enum.GetValues<SettingField>().Where(f => f != SettingField.NetworkMode))
            WriteField(body, host, settings, field, networks.FirstOrDefault()?.Name ?? "bridge", lanHostIp);
        var mode = settings.NetworkMode;
        if (IsOwnMode(mode))
        {
            host["NetworkMode"] = mode;
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject() };
            ClearPublished(body, host, mode);
            return body;
        }
        var endpoints = new JsonObject();
        foreach (var join in networks)
        {
            var endpoint = new JsonObject();
            if (join.Aliases.Count > 0) endpoint["Aliases"] = Array(join.Aliases);
            if (join.IPv4Address is { } ip) endpoint["IPAMConfig"] = new JsonObject { ["IPv4Address"] = ip };
            if (join.LowPriority && gatewayPriority) endpoint["GwPriority"] = -1;
            endpoints[join.Name] = endpoint;
        }
        host["NetworkMode"] = networks.Count > 0 ? networks[0].Name : "bridge";
        body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        return body;
    }

    /// <summary>The equivalent docker run, secrets masked, one option per line.</summary>
    public static string DockerRun(string name, ContainerSettings settings, IReadOnlyList<NetworkJoin> networks, IReadOnlyCollection<string> secretKeys)
    {
        var options = new List<string> { "-d", "--name " + Quote(name) };
        var s = settings;
        if (s.Restart.Name != "no")
            options.Add("--restart " + (s.Restart.Name == "on-failure" && s.Restart.MaximumRetryCount > 0
                ? $"on-failure:{s.Restart.MaximumRetryCount.ToString(CultureInfo.InvariantCulture)}" : s.Restart.Name));
        if (IsOwnMode(s.NetworkMode)) options.Add("--network " + Quote(s.NetworkMode));
        else
            foreach (var join in networks)
            {
                var parts = new List<string> { "name=" + join.Name };
                parts.AddRange(join.Aliases.Select(a => "alias=" + a));
                if (join.IPv4Address is { } ip) parts.Add("ip=" + ip);
                if (join.LowPriority) parts.Add("gw-priority=-1");
                options.Add("--network " + Quote(string.Join(',', parts)));
            }
        if (s.Hostname is { } hostname) options.Add("--hostname " + Quote(hostname));
        if (s.Domainname is { } domain) options.Add("--domainname " + Quote(domain));
        if (!IsOwnMode(s.NetworkMode))
            foreach (var rule in s.Ports)
                options.Add(rule.Published && rule.HostPort is { } hostPort
                    ? "-p " + Quote(PublishSpec(rule, hostPort))
                    : rule.Published ? "-p " + Quote(rule.Key) : "--expose " + Quote(rule.Key));
        foreach (var e in s.Env.Where(e => !e.FromImage))
            options.Add("-e " + Quote(e.Key + "=" + Masking.Value(e.Key, e.Value, secretKeys, e.Secret)));
        foreach (var m in s.Mounts)
            options.Add(m.Kind switch
            {
                MountKind.Tmpfs => "--tmpfs " + Quote(m.Target + (m.TmpfsBytes is { } size ? ":size=" + size.ToString(CultureInfo.InvariantCulture) : "")),
                _ => "-v " + Quote(m.Source + ":" + m.Target + (m.ReadOnly ? ":ro" : "")),
            });
        foreach (var extra in s.ExtraHosts) options.Add("--add-host " + Quote(extra));
        foreach (var dns in s.Dns) options.Add("--dns " + Quote(dns));
        foreach (var search in s.DnsSearch) options.Add("--dns-search " + Quote(search));
        foreach (var option in s.DnsOptions) options.Add("--dns-option " + Quote(option));
        var r = s.Resources;
        if (r.MemoryBytes is { } memory) options.Add("--memory " + Bytes(memory));
        if (r.MemoryReservationBytes is { } reservation) options.Add("--memory-reservation " + Bytes(reservation));
        if (r.MemorySwapBytes is { } swap) options.Add("--memory-swap " + (swap < 0 ? "-1" : Bytes(swap)));
        if (r.Cpus is { } cpus) options.Add("--cpus " + cpus.ToString("0.###", CultureInfo.InvariantCulture));
        if (r.CpuShares is { } cpuShares) options.Add("--cpu-shares " + cpuShares.ToString(CultureInfo.InvariantCulture));
        if (r.CpusetCpus is { } cpuset) options.Add("--cpuset-cpus " + Quote(cpuset));
        if (r.PidsLimit is { } pidsLimit) options.Add("--pids-limit " + pidsLimit.ToString(CultureInfo.InvariantCulture));
        if (s.ShmSizeBytes is { } shmSize) options.Add("--shm-size " + Bytes(shmSize));
        foreach (var u in s.Ulimits) options.Add($"--ulimit {u.Name}={u.Soft.ToString(CultureInfo.InvariantCulture)}:{u.Hard.ToString(CultureInfo.InvariantCulture)}");
        switch (s.Health.Mode)
        {
            case HealthMode.Disabled:
                options.Add("--no-healthcheck");
                break;
            case HealthMode.Custom:
                var test = s.Health.Test ?? [];
                if (test.Count > 1) options.Add("--health-cmd " + Quote(string.Join(' ', Masking.Args(test[0] == "CMD-SHELL" ? [test[1]] : test.Skip(1).ToArray(), secretKeys))));
                if (s.Health.Interval is { } interval) options.Add("--health-interval " + Duration(interval));
                if (s.Health.Timeout is { } healthTimeout) options.Add("--health-timeout " + Duration(healthTimeout));
                if (s.Health.Retries is { } retries) options.Add("--health-retries " + retries.ToString(CultureInfo.InvariantCulture));
                if (s.Health.StartPeriod is { } startPeriod) options.Add("--health-start-period " + Duration(startPeriod));
                if (s.Health.StartInterval is { } startInterval) options.Add("--health-start-interval " + Duration(startInterval));
                break;
        }
        foreach (var (key, value) in s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal)) options.Add("--label " + Quote(key + "=" + Masking.Value(key, value, secretKeys)));
        foreach (var d in s.Devices) options.Add("--device " + Quote($"{d.HostPath}:{d.ContainerPath}{(d.Permissions == "rwm" ? "" : ":" + d.Permissions)}"));
        if (s.Gpus is { } gpus) options.Add("--gpus " + Quote(gpus));
        foreach (var cap in s.CapAdd) options.Add("--cap-add " + Quote(cap));
        foreach (var cap in s.CapDrop) options.Add("--cap-drop " + Quote(cap));
        if (s.Privileged) options.Add("--privileged");
        foreach (var opt in s.SecurityOpt) options.Add("--security-opt " + Quote(opt));
        if (s.ReadOnlyRootfs) options.Add("--read-only");
        if (s.Init) options.Add("--init");
        foreach (var (key, value) in s.Sysctls.OrderBy(l => l.Key, StringComparer.Ordinal)) options.Add("--sysctl " + Quote(key + "=" + value));
        if (s.Logging.Driver.Length > 0) options.Add("--log-driver " + Quote(s.Logging.Driver));
        foreach (var (key, value) in s.Logging.Options ?? new Dictionary<string, string>()) options.Add("--log-opt " + Quote(key + "=" + Masking.Value(key, value, secretKeys)));
        if (s.StopSignal is { } signal) options.Add("--stop-signal " + Quote(signal));
        if (s.StopTimeoutSeconds is { } stopTimeout) options.Add("--stop-timeout " + stopTimeout.ToString(CultureInfo.InvariantCulture));
        if (s.Tty) options.Add("-t");
        if (s.OpenStdin) options.Add("-i");
        if (s.WorkingDir is { } workdir) options.Add("--workdir " + Quote(workdir));
        if (s.User is { } user) options.Add("--user " + Quote(user));
        if (s.Entrypoint is { } entrypoint)
            options.Add("--entrypoint " + Quote(entrypoint.Count == 0 ? "" : string.Join(' ', Masking.Args(entrypoint, secretKeys))));
        var tail = s.Image.Length == 0 ? "<image>" : Quote(s.Image);
        if (s.Command is { Count: > 0 } command) tail += " " + string.Join(' ', Masking.Args(command, secretKeys).Select(Quote));
        var text = new StringBuilder("docker run");
        foreach (var option in options) text.Append(" \\\n  ").Append(option);
        return text.Append(" \\\n  ").Append(tail).ToString();
    }

    /// <summary>GET /images/{id}/json as ImageFacts (C uses it too).</summary>
    public static ImageFacts Facts(JsonObject image)
    {
        var config = image["Config"] as JsonObject;
        var env = (StrList(config?["Env"]) ?? []).Select(e => e.Split('=', 2))
            .Select(kv => new EnvSetting(kv[0], kv.Length > 1 ? kv[1] : "", FromImage: true, Secret: Masking.IsSecret(kv[0], kv.Length > 1 ? kv[1] : "")))
            .ToList();
        var exposed = (config?["ExposedPorts"] as JsonObject ?? []).Select(p => ContainerPorts.ParseKey(p.Key))
            .OfType<(int Port, string Protocol)>().Select(p => new ExposedPort(p.Port, p.Protocol)).ToList();
        var check = config?["Healthcheck"] as JsonObject;
        return new ImageFacts(
            Str(image, "Id"),
            StrList(image["RepoTags"]) ?? [],
            StrList(image["RepoDigests"]) ?? [],
            DateTimeOffset.TryParse(Str(image, "Created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) && created.Year > 1
                ? created.UtcDateTime : null,
            Long(image, "Size"),
            Str(image, "Os"),
            Str(image, "Architecture"),
            env,
            StrList(config?["Entrypoint"]),
            StrList(config?["Cmd"]),
            NullIfEmpty(Str(config, "WorkingDir")),
            NullIfEmpty(Str(config, "User")),
            exposed,
            (config?["Volumes"] as JsonObject ?? []).Select(v => v.Key).ToList(),
            Map(config?["Labels"]),
            check is null ? new HealthSetting() : HealthOf(check, null, imageKnown: false),
            NullIfEmpty(Str(config, "StopSignal")));
    }

    // ---- Reading ----

    // The container's own value, or null when it's the image's. Without the image, whatever the container has is its own.
    private static IReadOnlyList<string>? Own(IReadOnlyList<string>? own, IReadOnlyList<string>? image, bool imageKnown)
    {
        var none = own is null || own.Count == 0 || (own.Count == 1 && own[0].Length == 0);
        if (!imageKnown)
        {
            if (own is null) return null;
            return none ? [] : own;
        }
        if (Same(own, image)) return null;
        return none ? [] : own;
    }

    private static string? OwnText(string own, string image, bool imageKnown) =>
        own.Length == 0 || (imageKnown && own == image) ? null : own;

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);

    private static HealthSetting HealthOf(JsonObject? check, JsonObject? image, bool imageKnown)
    {
        if (check is null || StrList(check["Test"]) is not { Count: > 0 } test) return new HealthSetting();
        if (test[0] == "NONE") return new HealthSetting(HealthMode.Disabled);
        if (imageKnown && image is not null && JsonNode.DeepEquals(Normal(check), Normal(image))) return new HealthSetting();
        static TimeSpan? Span(JsonObject o, string key) => Long(o, key) is > 0 and var ns ? TimeSpan.FromTicks(ns / 100) : null;
        return new HealthSetting(HealthMode.Custom, test, Span(check, "Interval"), Span(check, "Timeout"),
            check["Retries"] is JsonValue r && r.GetValueKind() == JsonValueKind.Number ? (int)Long(check, "Retries") : null,
            Span(check, "StartPeriod"), Span(check, "StartInterval"));
    }

    // A health check with Docker's zeros left out, so {"Test":[…],"Interval":0} and {"Test":[…]} compare equal.
    private static JsonObject Normal(JsonObject check)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in check)
            if (value is not null && !(value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Long(check, key) == 0)) copy[key] = value.DeepClone();
        return copy;
    }

    private static string? GpusOf(JsonArray? requests)
    {
        foreach (var request in (requests ?? []).OfType<JsonObject>())
        {
            if (!IsGpu(request)) continue;
            if (StrList(request["DeviceIDs"]) is { Count: > 0 } ids) return "device=" + string.Join(',', ids);
            var count = Long(request, "Count");
            return count < 0 ? "all" : count.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static bool IsGpu(JsonObject request) =>
        (request["Capabilities"] as JsonArray ?? []).OfType<JsonArray>().Any(set => set.Any(c => c is JsonValue v && v.TryGetValue<string>(out var s) && s == "gpu"));

    // "" for every bridge network (the default one, gitberth, any named one); host, none and container:x as they are.
    private static string ModeOf(string mode) => IsOwnMode(mode) ? mode : "";

    /// <summary>host, none or container:x: a network mode that isn't a bridge network, with no endpoints or published ports of its own.</summary>
    public static bool IsOwnMode(string mode) => mode is "host" or "none" || mode.StartsWith("container:", StringComparison.Ordinal);

    /// <summary>
    /// Every mount, as it's set: bind mounts and volumes of Binds, the entries of HostConfig.Mounts, the anonymous volumes
    /// Docker made (from the inspect's top-level Mounts, by their 64-hex names), then the tmpfs mounts of HostConfig.Tmpfs.
    /// </summary>
    internal static List<MountSetting> MountsOf(JsonObject host, JsonArray? inspectMounts)
    {
        var made = (inspectMounts ?? []).OfType<JsonObject>().Where(m => Str(m, "Type") == "volume")
            .GroupBy(m => CloneSpec.Clean(Str(m, "Destination"))).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var list = new List<MountSetting>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in StrList(host["Binds"]) ?? [])
        {
            var parts = spec.Split(':');
            if (parts.Length == 1)
            {
                var target = CloneSpec.Clean(parts[0]);
                if (made.TryGetValue(target, out var anonymous) && Str(anonymous, "Name").Length > 0 && covered.Add(target))
                    list.Add(new MountSetting(MountKind.Volume, Str(anonymous, "Name"), target, anonymous["RW"]?.GetValueKind() == JsonValueKind.False));
                continue;
            }
            var dst = CloneSpec.Clean(parts[1]);
            if (!covered.Add(dst)) continue;
            var readOnly = parts.Length > 2 && parts[2].Split(',').Contains("ro");
            list.Add(new MountSetting(parts[0].StartsWith('/') ? MountKind.Bind : MountKind.Volume, parts[0], dst, readOnly));
        }
        foreach (var m in (host["Mounts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var target = CloneSpec.Clean(Str(m, "Target"));
            if (!covered.Add(target)) continue;
            var readOnly = Bool(m, "ReadOnly");
            switch (Str(m, "Type"))
            {
                case "bind":
                    list.Add(new MountSetting(MountKind.Bind, Str(m, "Source"), target, readOnly));
                    break;
                case "tmpfs":
                    list.Add(new MountSetting(MountKind.Tmpfs, "", target, readOnly, Long(m["TmpfsOptions"], "SizeBytes") is > 0 and var size ? size : null));
                    break;
                case "volume":
                    var source = Str(m, "Source").Length > 0 ? Str(m, "Source") : made.TryGetValue(target, out var anonymous) ? Str(anonymous, "Name") : "";
                    list.Add(new MountSetting(MountKind.Volume, source, target, readOnly));
                    break;
            }
        }
        foreach (var (target, m) in made)
            if (Str(m, "Name").Length > 0 && covered.Add(target))
                list.Add(new MountSetting(MountKind.Volume, Str(m, "Name"), target, m["RW"]?.GetValueKind() == JsonValueKind.False));
        foreach (var (target, options) in host["Tmpfs"] as JsonObject ?? [])
        {
            var path = CloneSpec.Clean(target);
            if (!covered.Add(path)) continue;
            var text = options is JsonValue v && v.TryGetValue<string>(out var o) ? o : "";
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var size = parts.FirstOrDefault(p => p.StartsWith("size=", StringComparison.Ordinal)) is { } s ? ParseSize(s["size=".Length..]) : null;
            list.Add(new MountSetting(MountKind.Tmpfs, "", path, parts.Contains("ro"), size));
        }
        return list;
    }

    // ---- Writing ----

    private static void WritePorts(JsonObject body, JsonObject host, IReadOnlyList<PortRule> rules, string lanHostIp)
    {
        var exposed = body["ExposedPorts"] as JsonObject ?? new JsonObject();
        foreach (var rule in rules)
            if (!exposed.ContainsKey(rule.Key)) exposed[rule.Key] = new JsonObject();
        if (exposed.Count > 0) body["ExposedPorts"] = exposed;
        var bindings = new JsonObject();
        foreach (var group in rules.Where(r => r.Published).GroupBy(r => r.Key))
            bindings[group.Key] = new JsonArray(group.Select(r => (JsonNode)new JsonObject
            {
                ["HostIp"] = AddressOf(r, lanHostIp),
                ["HostPort"] = r.HostPort is { } port ? port.ToString(CultureInfo.InvariantCulture) : "",
            }).ToArray());
        host["PortBindings"] = bindings;
        host["PublishAllPorts"] = false;
    }

    // A rule's own address while it belongs to its access (0.0.0.0 read from Docker stays 0.0.0.0), else its access's.
    private static string AddressOf(PortRule rule, string lanHostIp) =>
        rule.HostIp is { } own && (rule.Access == PortAccess.Custom || ContainerPorts.AccessOf(own) == rule.Access) ? own
        : rule.Access == PortAccess.Lan ? lanHostIp
        : ContainerPorts.BindAddress(rule.Access, rule.HostIp) ?? "";

    /// <summary>
    /// Binds, HostConfig.Mounts and HostConfig.Tmpfs from the mounts. A mount at a target the body had keeps the form it
    /// had there, with the options Gitberth doesn't model (a bind's z or propagation, VolumeOptions, BindOptions, a tmpfs's
    /// mode); a new bind is a Binds string (so a missing folder is made, as -v does), a new volume a HostConfig.Mounts entry
    /// with its name, a new tmpfs an entry of HostConfig.Tmpfs. A HostConfig.Mounts entry of a type Gitberth doesn't model
    /// (--mount type=image, npipe or cluster) stays as it was, unless a mount now takes its target.
    /// </summary>
    private static void WriteMounts(JsonObject host, IReadOnlyList<MountSetting> mounts)
    {
        var oldBinds = (StrList(host["Binds"]) ?? []).Where(b => b.Split(':').Length > 1)
            .GroupBy(b => CloneSpec.Clean(b.Split(':')[1])).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var oldMounts = (host["Mounts"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(m => CloneSpec.Clean(Str(m, "Target"))).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var oldTmpfs = (host["Tmpfs"] as JsonObject ?? []).ToDictionary(t => CloneSpec.Clean(t.Key), t => t.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "",
            StringComparer.Ordinal);
        var binds = new JsonArray();
        var entries = new JsonArray();
        var tmpfs = new JsonObject();
        foreach (var m in mounts)
        {
            var target = CloneSpec.Clean(m.Target);
            if (oldMounts.TryGetValue(target, out var entry) && Str(entry, "Type") == TypeOf(m.Kind))
            {
                var copy = (JsonObject)entry.DeepClone();
                copy["Target"] = m.Target;
                if (m.Kind == MountKind.Tmpfs)
                {
                    copy.Remove("Source");
                    var options = copy["TmpfsOptions"] as JsonObject ?? new JsonObject();
                    if (m.TmpfsBytes is { } size) options["SizeBytes"] = size;
                    else options.Remove("SizeBytes");
                    if (options.Count > 0) copy["TmpfsOptions"] = options;
                    else copy.Remove("TmpfsOptions");
                }
                else copy["Source"] = m.Source;
                copy["ReadOnly"] = m.ReadOnly;
                entries.Add(copy);
                continue;
            }
            switch (m.Kind)
            {
                case MountKind.Bind:
                case MountKind.Volume when oldBinds.ContainsKey(target) && !m.Anonymous:
                    // Options other than ro/rw (z, Z, propagation, nocopy) stay as the bind at that target had them.
                    var kept = oldBinds.TryGetValue(target, out var old) && old.Split(':') is { Length: > 2 } parts
                        ? parts[2].Split(',').Where(o => o is not ("ro" or "rw") && o.Length > 0).ToList()
                        : [];
                    if (m.ReadOnly) kept.Insert(0, "ro");
                    binds.Add($"{m.Source}:{m.Target}{(kept.Count > 0 ? ":" + string.Join(',', kept) : "")}");
                    break;
                case MountKind.Volume:
                    entries.Add(new JsonObject { ["Type"] = "volume", ["Source"] = m.Source, ["Target"] = m.Target, ["ReadOnly"] = m.ReadOnly });
                    break;
                case MountKind.Tmpfs:
                    var rest = oldTmpfs.TryGetValue(target, out var was)
                        ? was.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(o => !o.StartsWith("size=", StringComparison.Ordinal) && o is not ("ro" or "rw")).ToList()
                        : [];
                    if (m.TmpfsBytes is { } bytes) rest.Insert(0, "size=" + bytes.ToString(CultureInfo.InvariantCulture));
                    if (m.ReadOnly) rest.Add("ro");
                    tmpfs[m.Target] = string.Join(',', rest);
                    break;
            }
        }
        var targets = mounts.Select(m => CloneSpec.Clean(m.Target)).ToHashSet(StringComparer.Ordinal);
        foreach (var (target, entry) in oldMounts)
            if (Str(entry, "Type") is not ("bind" or "volume" or "tmpfs") && !targets.Contains(target)) entries.Add(entry.DeepClone());
        host["Binds"] = binds.Count == 0 ? null : binds;
        host["Mounts"] = entries.Count == 0 ? null : entries;
        host["Tmpfs"] = tmpfs.Count == 0 ? null : tmpfs;
    }

    private static string TypeOf(MountKind kind) => kind switch { MountKind.Bind => "bind", MountKind.Tmpfs => "tmpfs", _ => "volume" };

    /// <summary>
    /// The network mode. Host, none and container:x have no endpoints and publish nothing (and container:x has the other's
    /// hostname). Switching back to a bridge network puts a Gitberth-made container (label gitberth.app) on the shared
    /// network with its slug as alias, and anything else on Docker's bridge. A bridge network it's on stays as it is.
    /// </summary>
    private static void WriteNetworkMode(JsonObject body, JsonObject host, ContainerSettings s, string sharedNetwork)
    {
        var current = Str(host, "NetworkMode");
        if (IsOwnMode(s.NetworkMode))
        {
            host["NetworkMode"] = s.NetworkMode;
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject() };
            ClearPublished(body, host, s.NetworkMode);
            return;
        }
        if (s.NetworkMode.Length > 0 && s.NetworkMode != current)
        {
            // A network named outright.
            host["NetworkMode"] = s.NetworkMode;
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject { [s.NetworkMode] = new JsonObject() } };
            return;
        }
        if (!IsOwnMode(current)) return;
        var slug = s.Labels.TryGetValue("gitberth.app", out var app) ? app : Str(body["Labels"], "gitberth.app");
        var network = slug.Length > 0 ? sharedNetwork : "bridge";
        var endpoint = new JsonObject();
        if (slug.Length > 0) endpoint["Aliases"] = new JsonArray(slug);
        host["NetworkMode"] = network;
        body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject { [network] = endpoint } };
    }

    private static void ClearPublished(JsonObject body, JsonObject host, string mode)
    {
        host["PortBindings"] = new JsonObject();
        host["PublishAllPorts"] = false;
        // The other container's namespace: Docker refuses a hostname, exposed ports, DNS or hosts entries of its own.
        if (mode.StartsWith("container:", StringComparison.Ordinal))
        {
            body.Remove("Hostname");
            body.Remove("Domainname");
            body.Remove("ExposedPorts");
            host.Remove("Dns");
            host.Remove("ExtraHosts");
        }
    }

    // The GPU request it had keeps what Gitberth doesn't model (its Driver, Options and Capabilities); only which GPUs changes.
    private static void WriteGpus(JsonObject host, string? gpus)
    {
        var requests = (host["DeviceRequests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var others = requests.Where(r => !IsGpu(r)).Select(r => (JsonNode)r.DeepClone()).ToList();
        if (gpus is { Length: > 0 })
        {
            var request = requests.FirstOrDefault(IsGpu)?.DeepClone() as JsonObject
                ?? new JsonObject { ["Driver"] = "", ["Count"] = 0, ["DeviceIDs"] = null, ["Capabilities"] = new JsonArray(new JsonArray("gpu")), ["Options"] = new JsonObject() };
            request["Count"] = 0;
            request["DeviceIDs"] = null;
            if (gpus == "all") request["Count"] = -1;
            else if (gpus.StartsWith("device=", StringComparison.Ordinal))
                request["DeviceIDs"] = Array(gpus["device=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else request["Count"] = long.TryParse(gpus, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
            others.Add(request);
        }
        host["DeviceRequests"] = others.Count == 0 ? null : new JsonArray(others.ToArray());
    }

    /// <summary>Docker's RestartPolicy for the setting: {"Name":"on-failure","MaximumRetryCount":3}.</summary>
    public static JsonObject RestartJson(RestartSetting restart) =>
        new() { ["Name"] = restart.Name, ["MaximumRetryCount"] = restart.Name == "on-failure" ? restart.MaximumRetryCount : 0 };

    /// <summary>Config.Healthcheck for the setting; null for the image's own.</summary>
    public static JsonObject? HealthJson(HealthSetting health)
    {
        switch (health.Mode)
        {
            case HealthMode.Disabled:
                return new JsonObject { ["Test"] = new JsonArray("NONE") };
            case HealthMode.Custom:
                var check = new JsonObject { ["Test"] = Array(health.Test ?? []) };
                if (health.Interval is { } interval) check["Interval"] = interval.Ticks * 100;
                if (health.Timeout is { } timeout) check["Timeout"] = timeout.Ticks * 100;
                if (health.StartPeriod is { } startPeriod) check["StartPeriod"] = startPeriod.Ticks * 100;
                if (health.StartInterval is { } startInterval) check["StartInterval"] = startInterval.Ticks * 100;
                if (health.Retries is { } retries) check["Retries"] = retries;
                return check;
            default:
                return null;
        }
    }

    private static void SetList(JsonObject body, string key, IReadOnlyList<string>? list)
    {
        if (list is null) body.Remove(key);
        else body[key] = Array(list);
    }

    private static void SetText(JsonObject body, string key, string? value)
    {
        if (value is null) body.Remove(key);
        else body[key] = value;
    }

    // ---- JSON helpers ----

    internal static List<string>? StrList(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() : null;

    internal static Dictionary<string, string> Map(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in node as JsonObject ?? []) map[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return map;
    }

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());

    private static JsonObject MapJson(IReadOnlyDictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (key, value) in map) o[key] = value;
        return o;
    }

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    private static long Long(JsonNode? node, string name) => ContainerPorts.Number(node, name);

    private static bool Bool(JsonNode? node, string name) => node is JsonObject o && o[name]?.GetValueKind() == JsonValueKind.True;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>"64m", "1g", "65536k" or a number of bytes; null when it isn't one.</summary>
    internal static long? ParseSize(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        var unit = text[^1] switch { 'k' => 1024L, 'm' => 1024L * 1024, 'g' => 1024L * 1024 * 1024, 'b' => 1L, _ => 0L };
        var digits = unit == 0 ? text : text[..^1];
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n * (unit == 0 ? 1 : unit) : null;
    }

    // As docker run takes it: 512m, 1g, or bytes.
    private static string Bytes(long bytes) =>
        bytes % (1024L * 1024 * 1024) == 0 ? (bytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture) + "g"
        : bytes % (1024L * 1024) == 0 ? (bytes / (1024L * 1024)).ToString(CultureInfo.InvariantCulture) + "m"
        : bytes.ToString(CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) =>
        span.TotalSeconds >= 1 && span.Ticks % TimeSpan.TicksPerSecond == 0
            ? ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"
            : ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    private static string PublishSpec(PortRule rule, int hostPort)
    {
        var ip = AddressOf(rule, "");
        var address = ip.Length == 0 ? "" : (ip.Contains(':') ? $"[{ip}]" : ip) + ":";
        return $"{address}{hostPort.ToString(CultureInfo.InvariantCulture)}:{rule.ContainerPort.ToString(CultureInfo.InvariantCulture)}{(rule.Protocol == "tcp" ? "" : "/" + rule.Protocol)}";
    }

    // A word as a POSIX shell reads it back.
    internal static string Quote(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,+%".Contains(c)) ? word : "'" + word.Replace("'", "'\\''") + "'";
}
