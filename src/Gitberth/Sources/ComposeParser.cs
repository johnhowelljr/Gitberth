using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Gitberth.Sources;

/// <summary>
/// Reads a pasted Compose file, or one service of it, as an app's settings (3.6), through YamlDotNet. A whole file, its
/// services: block, one service under its name, or a service's keys on their own are all read. Anchors and merge keys
/// (&lt;&lt;: *common) are followed. Every key of a service is either carried over, listed as Dropped with the reason, or an
/// Error; x- keys are Compose's own extensions and are passed over. Variables are replaced as Compose would with nothing
/// set: a default is taken (with a Note), a variable without one is left empty (with an Error to fill it in), and $$ is $.
/// </summary>
public static partial class ComposeParser
{
    /// <summary>The services in the paste, in its order, for the UI to pick one; with what made it unreadable.</summary>
    public static ComposeServices Read(string yaml)
    {
        var issues = new List<ParseIssue>();
        if (Load(yaml, issues) is not { } root) return new ComposeServices([], issues);
        return new ComposeServices(ServicesOf(root, issues).Select(s => s.Name).ToList(), issues);
    }

    /// <summary>One service as an app: <paramref name="service"/> by name, or the only one when there's one.</summary>
    public static ParsedApp Parse(string yaml, string? service = null)
    {
        var issues = new List<ParseIssue>();
        if (Load(yaml, issues) is not { } root) return Empty(issues);
        var services = ServicesOf(root, issues);
        if (services.Count == 0)
        {
            if (!issues.Any(i => i.Severity == ParseSeverity.Error)) issues.Add(DockerRunParser.Error("There's no service in the paste.", null));
            return Empty(issues);
        }
        (string Name, YamlMappingNode Body) chosen;
        if (service is not null)
        {
            var match = services.FirstOrDefault(s => s.Name == service);
            if (match.Body is null)
            {
                issues.Add(DockerRunParser.Error($"There's no service {service}: it has {string.Join(", ", services.Select(s => s.Name))}.", service));
                return Empty(issues);
            }
            chosen = match;
        }
        else if (services.Count == 1) chosen = services[0];
        else
        {
            issues.Add(DockerRunParser.Error($"The paste has {services.Count} services ({string.Join(", ", services.Select(s => s.Name))}): pick one. Each becomes an app of its own.", null));
            return Empty(issues);
        }
        return new ServiceReader(root, chosen.Name, chosen.Body, issues).Read();
    }

    private static ParsedApp Empty(List<ParseIssue> issues) => new(null, new ContainerSettings(), [], null, issues, null, false);

    private static YamlMappingNode? Load(string yaml, List<ParseIssue> issues)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml ?? ""));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            {
                issues.Add(DockerRunParser.Error("The paste is empty: paste a Compose service (its YAML).", null));
                return null;
            }
            if (stream.Documents.Count > 1) issues.Add(DockerRunParser.Note("Only the first YAML document (before ---) is read.", "---"));
            if (stream.Documents[0].RootNode is YamlMappingNode root) return root;
            issues.Add(DockerRunParser.Error("That isn't a Compose file: it starts with services:, or with a service's name.", null));
            return null;
        }
        catch (YamlException ex)
        {
            var why = LocationPrefix().Replace((ex.InnerException as YamlException ?? ex).Message, "").Trim();
            issues.Add(DockerRunParser.Error($"The YAML can't be read at line {ex.Start.Line}, column {ex.Start.Column}: {why}", null));
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            issues.Add(DockerRunParser.Error($"The YAML can't be read: {ex.Message}", null));
            return null;
        }
    }

    // The services: a file's services: block; a paste that is that block's inside; or one service's keys.
    private static List<(string Name, YamlMappingNode Body)> ServicesOf(YamlMappingNode root, List<ParseIssue> issues)
    {
        var top = Map(root);
        if (top.FirstOrDefault(e => e.Key == "services").Value is { } block)
        {
            if (block is not YamlMappingNode services)
            {
                issues.Add(DockerRunParser.Error("services: should hold the services, each under its name.", "services"));
                return [];
            }
            return Map(services).Where(e => !e.Key.StartsWith("x-", StringComparison.Ordinal))
                .Select(e => (e.Key, e.Value as YamlMappingNode)).Where(e => e.Item2 is not null).Select(e => (e.Key, e.Item2!)).ToList();
        }
        if (top.Any(e => e.Key is "image" or "build"))
            return [("", root)];
        var named = top.Where(e => !e.Key.StartsWith("x-", StringComparison.Ordinal) && e.Key is not ("version" or "name" or "volumes" or "networks" or "secrets" or "configs"))
            .Where(e => e.Value is YamlMappingNode m && Map(m).Any(k => k.Key is "image" or "build"))
            .Select(e => (e.Key, (YamlMappingNode)e.Value)).ToList();
        if (named.Count > 0)
        {
            issues.Add(DockerRunParser.Note("Read as services: the paste has no services: line.", null));
            return named;
        }
        issues.Add(DockerRunParser.Error("That isn't a Compose service: a service has an image: (and ports:, environment: and the like).", null));
        return [];
    }

    /// <summary>A mapping's entries in order, with merge keys (&lt;&lt;: *anchor) followed; its own keys win over merged ones.</summary>
    private static List<(string Key, YamlNode Value)> Map(YamlMappingNode node)
    {
        var own = new List<(string Key, YamlNode Value)>();
        var merged = new List<(string Key, YamlNode Value)>();
        foreach (var (k, v) in node.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? "";
            if (key == "<<")
            {
                var sources = v is YamlSequenceNode list ? list.Children.OfType<YamlMappingNode>() : v is YamlMappingNode m ? [m] : [];
                foreach (var source in sources)
                    foreach (var entry in Map(source))
                        if (merged.All(e => e.Key != entry.Key)) merged.Add(entry);
            }
            else own.Add((key, v));
        }
        return [.. own, .. merged.Where(m => own.All(o => o.Key != m.Key))];
    }

    private static bool IsNull(YamlNode? node) =>
        node is null || node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } s && (s.Value is null or "" or "null" or "~" or "Null" or "NULL");

    /// <summary>
    /// Compose's variables with nothing set (the paste has no .env): ${VAR:-default} and ${VAR-default} take the default,
    /// ${VAR} and $VAR are left empty, ${VAR:?error} is empty with its message, ${VAR:+other} is empty, and $$ is $. Each
    /// variable is reported once.
    /// </summary>
    internal static string Interpolate(string text, List<ParseIssue> issues, HashSet<string> seen)
    {
        if (!text.Contains('$')) return text;
        var output = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '$' || i + 1 >= text.Length)
            {
                output.Append(c);
                i++;
                continue;
            }
            var next = text[i + 1];
            if (next == '$')
            {
                output.Append('$');
                i += 2;
                continue;
            }
            if (next == '{')
            {
                var depth = 0;
                var end = -1;
                for (var j = i + 1; j < text.Length; j++)
                {
                    if (text[j] == '{') depth++;
                    else if (text[j] == '}' && --depth == 0)
                    {
                        end = j;
                        break;
                    }
                }
                if (end < 0)
                {
                    issues.Add(DockerRunParser.Error($"{text[i..]} isn't closed: add the }} that ends it.", text[i..]));
                    return output.ToString();
                }
                var inner = text[(i + 2)..end];
                output.Append(Expand(inner, text[i..(end + 1)], issues, seen));
                i = end + 1;
                continue;
            }
            if (char.IsAsciiLetter(next) || next == '_')
            {
                var j = i + 1;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_')) j++;
                var name = text[(i + 1)..j];
                if (seen.Add(name))
                    issues.Add(DockerRunParser.Error($"{name} has no value here (it comes from the project's .env or the shell): fill it in.", text[i..j]));
                i = j;
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    private static string Expand(string inner, string fragment, List<ParseIssue> issues, HashSet<string> seen)
    {
        var m = VariablePattern().Match(inner);
        if (!m.Success)
        {
            issues.Add(DockerRunParser.Error($"{fragment} isn't a variable Compose takes.", fragment));
            return "";
        }
        var name = m.Groups[1].Value;
        var op = m.Groups[2].Value;
        var rest = m.Groups[3].Value;
        var first = seen.Add(name);
        switch (op)
        {
            case ":-" or "-":
                var value = Interpolate(rest, issues, seen);
                if (first)
                    issues.Add(DockerRunParser.Note(Security.Masking.IsSecret(name, value)
                        ? $"{name} isn't set here: it takes its default."
                        : $"{name} isn't set here: it takes its default, {(value.Length == 0 ? "empty" : value)}.", fragment));
                return value;
            case ":?" or "?":
                if (first) issues.Add(DockerRunParser.Error($"{name} has no value here: {(rest.Length > 0 ? rest : "the file requires it")}. Fill it in.", fragment));
                return "";
            case ":+" or "+":
                if (first) issues.Add(DockerRunParser.Note($"{name} isn't set here, so {fragment} is empty.", fragment));
                return "";
            default:
                if (first) issues.Add(DockerRunParser.Error($"{name} has no value here (it comes from the project's .env or the shell): fill it in.", fragment));
                return "";
        }
    }

    [GeneratedRegex(@"^\s*\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*")]
    private static partial Regex LocationPrefix();

    [GeneratedRegex("^([A-Za-z_][A-Za-z0-9_]*)(?:(:-|-|:\\?|\\?|:\\+|\\+)(.*))?$", RegexOptions.Singleline)]
    private static partial Regex VariablePattern();

    /// <summary>One service, read key by key into an app's settings.</summary>
    private sealed class ServiceReader(YamlMappingNode root, string service, YamlMappingNode body, List<ParseIssue> issues)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<(string Key, YamlNode Value)> _top = Map(root);
        private string? _image, _name, _workingDir, _user, _hostname, _domainname, _stopSignal, _platform, _cpuset, _logDriver, _gpus, _networkMode;
        private IReadOnlyList<string>? _command, _entrypoint;
        private readonly List<EnvSetting> _env = [];
        private readonly List<PortRule> _ports = [];
        private readonly List<(MountSetting Mount, string? Suffix)> _mounts = [];
        private readonly List<(string Name, List<string> Aliases, string? Ip)> _networks = [];
        private string? _lanAddress;
        private readonly List<string> _extraHosts = [], _dns = [], _dnsSearch = [], _dnsOptions = [], _capAdd = [], _capDrop = [], _securityOpt = [];
        private RestartSetting? _restart;
        private long? _memory, _memoryReservation, _memorySwap, _cpuShares, _pids, _shm, _cpuQuota, _cpuPeriod;
        private double? _cpus;
        private readonly List<UlimitSetting> _ulimits = [];
        private HealthSetting _health = new();
        private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal), _sysctls = new(StringComparer.Ordinal), _logOptions = new(StringComparer.Ordinal);
        private readonly List<DeviceSetting> _devices = [];
        private bool _privileged, _readOnly, _init, _tty, _stdin, _pullFirst, _build;
        private int? _stopTimeout;

        private string Project => _top.FirstOrDefault(e => e.Key == "name").Value is YamlScalarNode { Value: { Length: > 0 } project } ? project : "<project>";

        public ParsedApp Read()
        {
            foreach (var (key, value) in Map(body))
            {
                try
                {
                    ReadKey(key, value);
                }
                catch (FormatException ex)
                {
                    issues.Add(DockerRunParser.Error($"{key}: {ex.Message}", key));
                }
            }
            return Build();
        }

        private void ReadKey(string key, YamlNode value)
        {
            switch (key)
            {
                case "image": _image = Text(value); break;
                case "build": _build = true; break;
                case "container_name": _name = Text(value); break;
                case "command": _command = Words(value, key); break;
                case "entrypoint": _entrypoint = Words(value, key); break;
                case "environment": Environment(value); break;
                case "ports": foreach (var item in Items(value)) Port(item); break;
                case "expose": foreach (var item in Items(value)) _ports.AddRange(DockerRunParser.Expose(Text(item) ?? "", $"expose: {Text(item)}", issues)); break;
                case "volumes": foreach (var item in Items(value)) Volume(item); break;
                case "tmpfs": foreach (var item in Items(value)) AddMount(DockerRunParser.Tmpfs(Text(item) ?? "", $"tmpfs: {Text(item)}", issues), null); break;
                case "networks": Networks(value); break;
                case "network_mode": NetworkMode(Text(value) ?? ""); break;
                case "hostname": _hostname = Text(value); break;
                case "domainname": _domainname = Text(value); break;
                case "extra_hosts": ExtraHosts(value); break;
                case "dns": _dns.AddRange(Items(value).Select(Text).OfType<string>()); break;
                case "dns_search": _dnsSearch.AddRange(Items(value).Select(Text).OfType<string>()); break;
                case "dns_opt": _dnsOptions.AddRange(Items(value).Select(Text).OfType<string>()); break;
                case "restart":
                    _restart = DockerRunParser.Restart(Text(value) ?? "") ?? Fail<RestartSetting?>($"restart: {Text(value)} isn't a restart policy (no, always, unless-stopped, on-failure[:times]).", "restart", null);
                    break;
                case "mem_limit": _memory = Bytes(value, key); break;
                case "mem_reservation": _memoryReservation = Bytes(value, key); break;
                case "memswap_limit": _memorySwap = Text(value) == "-1" ? -1 : Bytes(value, key); break;
                case "cpus": _cpus = DockerRunParser.Cpus(Text(value) ?? "") ?? Fail<double?>($"cpus: {Text(value)} isn't a number of CPUs.", key, null); break;
                case "cpu_shares": _cpuShares = Whole(value, key); break;
                case "cpu_quota": _cpuQuota = Whole(value, key); break;
                case "cpu_period": _cpuPeriod = Whole(value, key); break;
                case "cpuset": _cpuset = Text(value); break;
                case "pids_limit": _pids = Whole(value, key); break;
                case "shm_size": _shm = Bytes(value, key); break;
                case "ulimits": Ulimits(value); break;
                case "deploy": Deploy(value); break;
                case "gpus": _gpus = GpuRequest(value, "gpus"); break;
                case "working_dir": _workingDir = Text(value); break;
                case "user": _user = Text(value); break;
                case "labels": foreach (var (k, v) in Pairs(value, key)) Label(k, v ?? ""); break;
                case "healthcheck": Health(value); break;
                case "devices": foreach (var item in Items(value)) Device(item); break;
                case "cap_add": _capAdd.AddRange(Items(value).Select(Text).OfType<string>().Select(c => c.ToUpperInvariant())); break;
                case "cap_drop": _capDrop.AddRange(Items(value).Select(Text).OfType<string>().Select(c => c.ToUpperInvariant())); break;
                case "privileged": _privileged = Bool(value); break;
                case "security_opt": _securityOpt.AddRange(Items(value).Select(Text).OfType<string>()); break;
                case "read_only": _readOnly = Bool(value); break;
                case "init": _init = Bool(value); break;
                case "sysctls": foreach (var (k, v) in Pairs(value, key)) _sysctls[k] = v ?? ""; break;
                case "logging": Logging(value); break;
                case "stop_signal": _stopSignal = Text(value); break;
                case "stop_grace_period":
                    _stopTimeout = DockerRunParser.ParseDuration(Text(value) ?? "") is { } grace
                        ? (int)Math.Ceiling(grace.TotalSeconds)
                        : Fail<int?>($"stop_grace_period: {Text(value)} isn't a time such as 30s or 1m30s.", key, null);
                    break;
                case "tty": _tty = Bool(value); break;
                case "stdin_open": _stdin = Bool(value); break;
                case "platform": _platform = Text(value); break;
                case "pull_policy": PullPolicy(Text(value) ?? ""); break;
                case "scale":
                    if (Whole(value, key) is > 1) issues.Add(DockerRunParser.Drop("scale: one container per app; it's created once.", key));
                    break;

                case "depends_on":
                    var needs = value is YamlMappingNode deps ? Map(deps).Select(d => d.Key).ToList() : Items(value).Select(Text).OfType<string>().ToList();
                    issues.Add(DockerRunParser.Drop($"depends_on: create {Join(needs)} first. Once {(needs.Count == 1 ? "it's an app" : "they're apps")}, this one reaches {(needs.Count == 1 ? "it" : "them")} by name on Gitberth's network.", key));
                    break;
                case "links" or "external_links":
                    issues.Add(DockerRunParser.Drop($"{key}: links are Docker's old way to connect containers. On Gitberth's network containers reach each other by name.", key));
                    break;
                case "volumes_from":
                    issues.Add(DockerRunParser.Drop("volumes_from: mount the volumes it needs by name instead.", key));
                    break;
                case "secrets" or "configs":
                    issues.Add(DockerRunParser.Drop($"{key}: they're Swarm's (or files next to the Compose file); put the value in Environment instead.", key));
                    break;
                case "profiles":
                    issues.Add(DockerRunParser.Drop("profiles: they choose which services Compose starts; Gitberth creates this one.", key));
                    break;
                case "extends":
                    issues.Add(DockerRunParser.Drop("extends: the service it extends isn't in the paste. Paste the settings it takes from there into this service.", key));
                    break;
                case "env_file":
                    issues.Add(DockerRunParser.Drop("env_file: the file is on another machine: paste its lines under environment.", key));
                    break;
                case "label_file":
                    issues.Add(DockerRunParser.Drop("label_file: the file is on another machine: paste its labels under labels.", key));
                    break;
                case "mac_address":
                    issues.Add(DockerRunParser.Drop("mac_address: Docker gives it a MAC address of its own.", key));
                    break;
                case "runtime":
                    issues.Add(DockerRunParser.Drop("runtime: it runs with Docker's default runtime.", key));
                    break;
                case "pid" or "ipc" or "uts" or "userns_mode" or "cgroup":
                    issues.Add(DockerRunParser.Drop($"{key}: it gets its own namespace, as Docker gives every container.", key));
                    break;
                case "cgroup_parent":
                    issues.Add(DockerRunParser.Drop("cgroup_parent: it runs in Docker's own cgroup.", key));
                    break;
                case "isolation" or "cpu_count" or "cpu_percent" or "credential_spec":
                    issues.Add(DockerRunParser.Drop($"{key} is a Windows containers setting.", key));
                    break;
                case "oom_kill_disable" or "oom_score_adj" or "mem_swappiness":
                    issues.Add(DockerRunParser.Drop($"{key}: the kernel's own handling of memory applies.", key));
                    break;
                case "blkio_config":
                    issues.Add(DockerRunParser.Drop("blkio_config: Gitberth doesn't keep disk I/O limits.", key));
                    break;
                case "cpu_rt_period" or "cpu_rt_runtime" or "cpuset_mems":
                    issues.Add(DockerRunParser.Drop($"{key}: Gitberth doesn't keep real-time CPU or memory node settings.", key));
                    break;
                case "device_cgroup_rules":
                    issues.Add(DockerRunParser.Drop("device_cgroup_rules: add the devices themselves under devices.", key));
                    break;
                case "group_add":
                    issues.Add(DockerRunParser.Drop("group_add: set the user it runs as (user) instead.", key));
                    break;
                case "storage_opt":
                    issues.Add(DockerRunParser.Drop("storage_opt: Gitberth doesn't keep storage driver options.", key));
                    break;
                case "annotations":
                    issues.Add(DockerRunParser.Drop("annotations: use labels instead.", key));
                    break;
                case "attach":
                    issues.Add(DockerRunParser.Drop("attach: its output is on the Logs tab.", key));
                    break;
                case "develop":
                    issues.Add(DockerRunParser.Drop("develop: Compose's file watching is for development on your own machine.", key));
                    break;
                case "post_start" or "pre_stop":
                    issues.Add(DockerRunParser.Drop($"{key}: Gitberth doesn't run commands in the container when it starts or stops.", key));
                    break;
                case "use_api_socket":
                    issues.Add(DockerRunParser.Drop("use_api_socket: mount /var/run/docker.sock under volumes if it needs Docker's API.", key));
                    break;
                case "provider" or "models":
                    issues.Add(DockerRunParser.Drop($"{key}: Compose's {key} aren't containers Gitberth makes.", key));
                    break;
                case "pull_refresh_after":
                    issues.Add(DockerRunParser.Drop("pull_refresh_after: Gitberth checks for updates itself (Updates).", key));
                    break;
                case "version":
                    break;
                default:
                    if (!key.StartsWith("x-", StringComparison.Ordinal))
                        issues.Add(DockerRunParser.Error($"Unknown key {key}: remove it or set it by hand.", key));
                    break;
            }
        }

        private ParsedApp Build()
        {
            if (_image is null or "")
            {
                issues.Add(_build
                    ? DockerRunParser.Error("Code to build: this service has build: and no image:. Use the GitHub repository source to build an app from its code.", "build")
                    : DockerRunParser.Error("The service has no image: add image:, such as image: postgres:18.", "image"));
            }
            else
            {
                if (_build) issues.Add(DockerRunParser.Note($"It has build: as well: Gitberth runs {_image} as the registry or this host has it, and builds nothing. To build it from its code, use the GitHub repository source.", "build"));
                if (ImageReference.Parse(_image) is null && !ImageReference.IsImageId(_image))
                    issues.Add(DockerRunParser.Error(ImageService.NotAReference(_image), "image"));
            }
            var name = _name ?? (service.Length > 0 ? service : null);
            var slug = AppService.Slugify(name ?? (ImageReference.Parse(_image) is { } reference ? reference.ShortName : "app"));
            if (slug.Length == 0) slug = "app";
            if (_name is not null && AppService.Slugify(_name) is { Length: > 0 } named)
                issues.Add(DockerRunParser.Note($"Gitberth names the container gb-{named}; other containers reach it as {named}.", "container_name"));

            foreach (var lan in _networks.Where(n => n.Name == LanAddresses.Network).ToList())
            {
                if (lan.Ip is { } address) _lanAddress = address;
                else issues.Add(DockerRunParser.Drop($"{LanAddresses.Network} without ipv4_address: give it its own LAN address on its LAN address card once it's created.", "networks"));
                _networks.Remove(lan);
            }
            var ports = _ports.DistinctBy(p => (p.ContainerPort, p.Protocol, p.Access, p.HostPort, p.HostIp)).ToList();
            if (_networkMode is { } mode && mode.Length > 0)
            {
                if (ports.Any(p => p.Published)) issues.Add(DockerRunParser.Drop(mode == "host"
                    ? "ports: on the host's network it listens on the host's own ports directly."
                    : $"ports: with network_mode: {mode} it has no ports of its own.", "ports"));
                if (_networks.Count > 0 || _lanAddress is not null) issues.Add(DockerRunParser.Drop($"networks: with network_mode: {mode} it joins no network.", "networks"));
                ports = [];
                _networks.Clear();
                _lanAddress = null;
            }
            if (_restart is null) issues.Add(DockerRunParser.Note("No restart: Gitberth uses unless-stopped, so it comes back after a restart of the host.", null));
            if (_cpuQuota is { } quota)
            {
                if (_cpus is not null) issues.Add(DockerRunParser.Drop("cpu_quota: cpus says the same, and is the one kept.", "cpu_quota"));
                else if (quota > 0 && (_cpuPeriod ?? 100_000) > 0)
                {
                    var period = _cpuPeriod ?? 100_000;
                    _cpus = Math.Round((double)quota / period, 3);
                    issues.Add(DockerRunParser.Note($"cpu_quota {quota} with a period of {period} is {_cpus.Value.ToString("0.###", CultureInfo.InvariantCulture)} CPUs: Gitberth sets cpus.", "cpu_quota"));
                }
            }
            else if (_cpuPeriod is not null) issues.Add(DockerRunParser.Drop("cpu_period without cpu_quota: it limits nothing on its own.", "cpu_period"));

            var settings = new ContainerSettings
            {
                Image = _image ?? "",
                Entrypoint = _entrypoint,
                Command = _command,
                WorkingDir = _workingDir,
                User = _user,
                Env = _env,
                Ports = ports,
                Mounts = _mounts.Select(m => m.Suffix is { } suffix ? m.Mount with { Source = $"gb-{slug}-{suffix}" } : m.Mount).ToList(),
                NetworkMode = _networkMode ?? "",
                Hostname = _hostname,
                Domainname = _domainname,
                ExtraHosts = _extraHosts,
                Dns = _dns,
                DnsSearch = _dnsSearch,
                DnsOptions = _dnsOptions,
                Restart = _restart ?? new RestartSetting(),
                Resources = new ResourceSetting(_memory, _memoryReservation, _memorySwap, _cpus, _cpuShares, _cpuset, _pids),
                ShmSizeBytes = _shm,
                Ulimits = _ulimits,
                Health = _health,
                Labels = _labels,
                Devices = _devices,
                Gpus = _gpus,
                CapAdd = _capAdd,
                CapDrop = _capDrop,
                Privileged = _privileged,
                SecurityOpt = _securityOpt,
                ReadOnlyRootfs = _readOnly,
                Init = _init,
                Sysctls = _sysctls,
                Logging = new LogSetting(_logDriver ?? "", _logOptions.Count > 0 ? _logOptions : null),
                StopSignal = _stopSignal,
                StopTimeoutSeconds = _stopTimeout,
                Tty = _tty,
                OpenStdin = _stdin,
            };
            var networks = _networks.Select(n => new NetworkJoin(n.Name, n.Aliases, n.Ip, LowPriority: true)).ToList();
            return new ParsedApp(name, settings, networks, _lanAddress, issues, _platform, _pullFirst);
        }

        // ---- Keys ----

        private void Environment(YamlNode value)
        {
            foreach (var (key, v) in Pairs(value, "environment"))
            {
                if (v is null) issues.Add(DockerRunParser.Error($"Give {key} a value: Compose would take it from the shell or the project's .env, which Gitberth can't see.", $"environment: {key}"));
                _env.RemoveAll(e => e.Key == key);
                _env.Add(new EnvSetting(key, v ?? "", Secret: Security.Masking.IsSecret(key, v ?? "")));
            }
        }

        private void Port(YamlNode item)
        {
            if (item is not YamlMappingNode longForm)
            {
                var spec = Text(item) ?? "";
                _ports.AddRange(DockerRunParser.Publish(spec, $"ports: {spec}", issues));
                return;
            }
            string? target = null, published = null, hostIp = null, protocol = null;
            foreach (var (key, v) in Map(longForm))
            {
                switch (key)
                {
                    case "target": target = Text(v); break;
                    case "published": published = Text(v); break;
                    case "host_ip": hostIp = Text(v); break;
                    case "protocol": protocol = Text(v); break;
                    case "mode": if (Text(v) == "ingress") issues.Add(DockerRunParser.Note("ports: mode: ingress is Swarm's; it's published on the host.", "mode")); break;
                    case "name" or "app_protocol": issues.Add(DockerRunParser.Drop($"ports: {key}: {Text(v)} is only a description, which Docker doesn't keep.", key)); break;
                    default: issues.Add(DockerRunParser.Error($"ports: {key} isn't a port setting (target, published, host_ip, protocol).", key)); break;
                }
            }
            if (target is null)
            {
                issues.Add(DockerRunParser.Error("ports: a port in the long form needs target:, the port in the container.", "ports"));
                return;
            }
            var address = hostIp is null ? "" : hostIp.Contains(':') ? $"[{hostIp}]:" : hostIp + ":";
            var text = (published is null ? (hostIp is null ? "" : address + ":") : address + published + ":") + target + (protocol is null ? "" : "/" + protocol);
            _ports.AddRange(DockerRunParser.Publish(text.StartsWith(':') ? text[1..] : text, $"ports: target {target}", issues));
        }

        private void Volume(YamlNode item)
        {
            if (item is not YamlMappingNode longForm)
            {
                var spec = Text(item) ?? "";
                var fragment = $"volumes: {spec}";
                var parts = spec.Split(':');
                // A name declared under the top-level volumes: gets its real name (name:, external:) or Gitberth's.
                if (parts.Length >= 2 && !parts[0].StartsWith('/') && !parts[0].StartsWith('.') && !parts[0].StartsWith('~') && !(parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0])))
                {
                    var mount = DockerRunParser.Volume(spec, fragment, issues);
                    if (mount is not null) Named(mount, parts[0], fragment);
                    return;
                }
                var made = DockerRunParser.Volume(spec, fragment, issues);
                AddMount(made, made is { Kind: MountKind.Volume, Source: "" } ? DockerRunParser.Segment(made.Target) : null);
                return;
            }

            string type = "volume", source = "", target = "";
            var readOnly = false;
            long? size = null;
            foreach (var (key, v) in Map(longForm))
            {
                switch (key)
                {
                    case "type": type = Text(v) ?? "volume"; break;
                    case "source": source = Text(v) ?? ""; break;
                    case "target": target = Text(v) ?? ""; break;
                    case "read_only": readOnly = Bool(v); break;
                    case "consistency": issues.Add(DockerRunParser.Drop($"volumes: consistency: {Text(v)} is a Docker Desktop setting, with no effect on Linux.", key)); break;
                    case "bind":
                        foreach (var (option, o) in v is YamlMappingNode bind ? Map(bind) : [])
                        {
                            if (option == "create_host_path") continue;
                            issues.Add(DockerRunParser.Drop(option == "propagation"
                                ? $"volumes: bind: propagation: {Text(o)}: it's mounted with Docker's default (rprivate)."
                                : $"volumes: bind: {option}: it's mounted as -v mounts a folder.", option));
                        }
                        break;
                    case "volume":
                        foreach (var (option, o) in v is YamlMappingNode vol ? Map(vol) : [])
                            issues.Add(DockerRunParser.Drop(option == "nocopy"
                                ? "volumes: volume: nocopy: Docker copies the image's files into a new volume, as usual."
                                : $"volumes: volume: {option}: the whole volume is mounted.", option));
                        break;
                    case "tmpfs":
                        foreach (var (option, o) in v is YamlMappingNode tmpfs ? Map(tmpfs) : [])
                        {
                            if (option == "size") size = Bytes(o, "tmpfs: size");
                            else issues.Add(DockerRunParser.Drop($"volumes: tmpfs: {option}: the tmpfs gets Docker's defaults.", option));
                        }
                        break;
                    case "image":
                        issues.Add(DockerRunParser.Drop("volumes: image: an image mount isn't kept.", key));
                        break;
                    default:
                        issues.Add(DockerRunParser.Error($"volumes: {key} isn't a volume setting (type, source, target, read_only).", key));
                        break;
                }
            }
            var where = $"volumes: target {target}";
            if (!target.StartsWith('/'))
            {
                issues.Add(DockerRunParser.Error("volumes: a volume in the long form needs target:, an absolute path in the container.", where));
                return;
            }
            switch (type)
            {
                case "bind":
                    if (DockerRunParser.HostSource(source, source, where, issues) is true) AddMount(new MountSetting(MountKind.Bind, source, target, readOnly), null);
                    else if (source.Length > 0 && !issues.Any(i => i.Fragment == where && i.Severity == ParseSeverity.Error))
                        issues.Add(DockerRunParser.Error($"volumes: a bind's source is an absolute path on the host, not {source}.", where));
                    break;
                case "volume":
                    if (source.Length == 0)
                    {
                        issues.Add(DockerRunParser.Note($"{target} has no volume name: Gitberth gives it one (gb-<app>-{DockerRunParser.Segment(target)}) so its data survives the app being recreated.", where));
                        AddMount(new MountSetting(MountKind.Volume, "", target, readOnly), DockerRunParser.Segment(target));
                    }
                    else Named(new MountSetting(MountKind.Volume, source, target, readOnly), source, where);
                    break;
                case "tmpfs":
                    AddMount(new MountSetting(MountKind.Tmpfs, "", target, readOnly, size), null);
                    break;
                default:
                    issues.Add(DockerRunParser.Drop($"volumes: type: {type} isn't kept: Gitberth keeps volumes, bind mounts and tmpfs.", where));
                    break;
            }
        }

        // A named volume as Compose would make it: its declared name, an external one's own, or (otherwise) project_name.
        private void Named(MountSetting mount, string key, string fragment)
        {
            var declared = _top.FirstOrDefault(e => e.Key == "volumes").Value is YamlMappingNode volumes
                ? Map(volumes).FirstOrDefault(e => e.Key == key)
                : default;
            var spec = declared.Value as YamlMappingNode;
            var settings = spec is null ? [] : Map(spec);
            string? Get(string name) => settings.FirstOrDefault(e => e.Key == name).Value is { } v ? Text(v) : null;

            if (Get("name") is { Length: > 0 } real)
            {
                AddMount(mount with { Source = real }, null);
                return;
            }
            var external = settings.FirstOrDefault(e => e.Key == "external").Value;
            if (external is YamlMappingNode old && Map(old).FirstOrDefault(e => e.Key == "name").Value is { } oldName)
            {
                AddMount(mount with { Source = Text(oldName) ?? key }, null);
                return;
            }
            if (external is not null && Bool(external))
            {
                AddMount(mount with { Source = key }, null);
                return;
            }
            // driver_opts type none, o bind, device /path: the volume is a folder of the host.
            var options = settings.FirstOrDefault(e => e.Key == "driver_opts").Value as YamlMappingNode;
            var opts = options is null ? [] : Map(options).ToDictionary(e => e.Key, e => Text(e.Value) ?? "", StringComparer.Ordinal);
            if (opts.GetValueOrDefault("o")?.Split(',').Contains("bind") == true && opts.GetValueOrDefault("device") is { } device && device.StartsWith('/'))
            {
                issues.Add(DockerRunParser.Note($"{key} is a bind of {device} (its driver_opts): Gitberth mounts that folder directly.", fragment));
                AddMount(new MountSetting(MountKind.Bind, device, mount.Target, mount.ReadOnly), null);
                return;
            }
            if (options is not null || settings.Any(e => e.Key is "driver" or "labels"))
                issues.Add(DockerRunParser.Drop($"{key}'s driver, options and labels: Gitberth makes it with Docker's local driver.", fragment));
            if (declared.Value is null && !(_top.FirstOrDefault(e => e.Key == "volumes").Value is YamlMappingNode))
                issues.Add(DockerRunParser.Note($"{key} isn't declared under a top-level volumes: (Compose would want it to be).", fragment));
            var suffix = AppService.Slugify(key) is { Length: > 0 } s ? s : "data";
            issues.Add(DockerRunParser.Note($"Compose would call the volume {Project}_{key}; Gitberth calls it gb-<app>-{suffix}. To use the one Compose already made, give its name instead (docker volume ls lists them).", fragment));
            AddMount(mount with { Source = "" }, suffix);
        }

        private void AddMount(MountSetting? mount, string? suffix)
        {
            if (mount is null) return;
            _mounts.RemoveAll(m => m.Mount.Target == mount.Target);
            _mounts.Add((mount, suffix));
        }

        private void Networks(YamlNode value)
        {
            var entries = value is YamlMappingNode map
                ? Map(map).Select(e => (e.Key, Settings: e.Value as YamlMappingNode)).ToList()
                : Items(value).Select(Text).OfType<string>().Select(n => (Key: n, Settings: (YamlMappingNode?)null)).ToList();
            foreach (var (key, settings) in entries)
            {
                var aliases = new List<string>();
                string? ip = null;
                foreach (var (option, v) in settings is null ? [] : Map(settings))
                {
                    switch (option)
                    {
                        case "aliases": aliases.AddRange(Items(v).Select(Text).OfType<string>()); break;
                        case "ipv4_address": ip = Text(v); break;
                        case "ipv6_address" or "link_local_ips": issues.Add(DockerRunParser.Drop($"networks: {key}: {option}: Docker gives it its addresses.", option)); break;
                        case "mac_address": issues.Add(DockerRunParser.Drop($"networks: {key}: mac_address: Docker gives it a MAC address of its own.", option)); break;
                        case "priority" or "gw_priority": issues.Add(DockerRunParser.Note($"networks: {key}: {option}: Gitberth's shared network carries its published ports and its way out.", option)); break;
                        case "driver_opts": issues.Add(DockerRunParser.Drop($"networks: {key}: driver_opts aren't kept.", option)); break;
                        default: issues.Add(DockerRunParser.Error($"networks: {key}: {option} isn't a network setting (aliases, ipv4_address).", option)); break;
                    }
                }
                if (ip is not null && (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    issues.Add(DockerRunParser.Error($"networks: {key}: ipv4_address {ip} isn't an IPv4 address.", "ipv4_address"));
                    ip = null;
                }
                if (key == "default")
                {
                    if (aliases.Count > 0) _networks.Add(("", aliases, null));
                    issues.Add(DockerRunParser.Note("networks: default: Gitberth puts it on its shared network, where other containers reach it by name, instead of the project's default network.", "default"));
                    continue;
                }
                _networks.Add((NetworkName(key), aliases, ip));
            }
        }

        // A network as Docker knows it: its declared name, an external one's own, or the key as it is (with a note).
        private string NetworkName(string key)
        {
            var declared = _top.FirstOrDefault(e => e.Key == "networks").Value is YamlMappingNode networks
                ? Map(networks).FirstOrDefault(e => e.Key == key).Value as YamlMappingNode
                : null;
            var settings = declared is null ? [] : Map(declared);
            if (settings.FirstOrDefault(e => e.Key == "name").Value is { } real && Text(real) is { Length: > 0 } name) return name;
            var external = settings.FirstOrDefault(e => e.Key == "external").Value;
            if (external is YamlMappingNode old && Map(old).FirstOrDefault(e => e.Key == "name").Value is { } oldName && Text(oldName) is { Length: > 0 } named) return named;
            if (external is not null && Bool(external)) return key;
            if (key != LanAddresses.Network)
                issues.Add(DockerRunParser.Note($"Compose would make the network as {Project}_{key}; Gitberth joins one called {key}, which must exist: Configure offers to create it.", $"networks: {key}"));
            return key;
        }

        private void NetworkMode(string mode)
        {
            switch (mode)
            {
                case "host" or "none":
                    _networkMode = mode;
                    break;
                case "bridge" or "default" or "":
                    issues.Add(DockerRunParser.Note($"network_mode: {mode}: Gitberth puts it on its shared network, where other containers reach it by name.", "network_mode"));
                    break;
                default:
                    if (mode.StartsWith("container:", StringComparison.Ordinal)) _networkMode = mode;
                    else if (mode.StartsWith("service:", StringComparison.Ordinal))
                        issues.Add(DockerRunParser.Error($"network_mode: {mode} shares another service's network: create {mode["service:".Length..]} first, then set the network mode to container:<its container>.", "network_mode"));
                    else issues.Add(DockerRunParser.Error($"network_mode: {mode} isn't one Gitberth knows (host, none, container:name).", "network_mode"));
                    break;
            }
        }

        private void ExtraHosts(YamlNode value)
        {
            var pairs = value is YamlMappingNode map
                ? Map(map).Select(e => $"{e.Key}:{Text(e.Value)}")
                : Items(value).Select(Text).OfType<string>();
            foreach (var pair in pairs)
            {
                var at = pair.IndexOf('=') is >= 0 and var eq ? eq : pair.IndexOf(':');
                var host = at < 0 ? "" : pair[..at].Trim();
                var ip = at < 0 ? "" : pair[(at + 1)..].Trim().Trim('[', ']');
                if (host.Length == 0 || ip != "host-gateway" && !IPAddress.TryParse(ip, out _))
                    issues.Add(DockerRunParser.Error($"extra_hosts: {pair} isn't name:address, such as db:10.1.1.20.", "extra_hosts"));
                else _extraHosts.Add($"{host}:{ip}");
            }
        }

        private void Ulimits(YamlNode value)
        {
            if (value is not YamlMappingNode map)
            {
                issues.Add(DockerRunParser.Error("ulimits: give each limit by name, such as nofile: {soft: 1024, hard: 2048}.", "ulimits"));
                return;
            }
            foreach (var (name, limit) in Map(map))
            {
                long? soft = null, hard = null;
                if (limit is YamlMappingNode both)
                    foreach (var (k, v) in Map(both))
                    {
                        if (k == "soft") soft = Whole(v, $"ulimits: {name}: soft");
                        else if (k == "hard") hard = Whole(v, $"ulimits: {name}: hard");
                    }
                else soft = hard = Whole(limit, $"ulimits: {name}");
                if (soft is { } s && hard is { } h) _ulimits.Add(new UlimitSetting(name, s, h));
                else if (soft is not null || hard is not null) issues.Add(DockerRunParser.Error($"ulimits: {name} needs both soft and hard.", "ulimits"));
            }
        }

        private void Deploy(YamlNode value)
        {
            foreach (var (key, v) in value is YamlMappingNode map ? Map(map) : [])
            {
                switch (key)
                {
                    case "resources":
                        foreach (var (kind, limits) in v is YamlMappingNode r ? Map(r) : [])
                            foreach (var (what, x) in limits is YamlMappingNode l ? Map(l) : [])
                                Resource(kind, what, x);
                        break;
                    case "replicas":
                        if (Whole(v, "deploy: replicas") is > 1) issues.Add(DockerRunParser.Drop("deploy: replicas: one container per app; it's created once.", "replicas"));
                        break;
                    case "restart_policy":
                        RestartPolicy(v);
                        break;
                    case "labels":
                        issues.Add(DockerRunParser.Drop("deploy: labels are Swarm's service labels: put them under labels to label the container.", "deploy: labels"));
                        break;
                    default:
                        issues.Add(DockerRunParser.Drop($"deploy: {key} is Swarm's.", $"deploy: {key}"));
                        break;
                }
            }
        }

        private void Resource(string kind, string what, YamlNode value)
        {
            var where = $"deploy: resources: {kind}: {what}";
            switch (kind, what)
            {
                case ("limits", "memory"): _memory = Bytes(value, where); break;
                case ("limits", "cpus"): _cpus = DockerRunParser.Cpus(Text(value) ?? "") ?? Fail<double?>($"{where}: {Text(value)} isn't a number of CPUs.", where, null); break;
                case ("limits", "pids"): _pids = Whole(value, where); break;
                case ("reservations", "memory"): _memoryReservation = Bytes(value, where); break;
                case ("reservations", "cpus"): issues.Add(DockerRunParser.Drop($"{where}: Docker has no CPU reservation for a container.", where)); break;
                case ("reservations", "devices"): _gpus = GpuRequest(value, where) ?? _gpus; break;
                case ("reservations", "generic_resources"): issues.Add(DockerRunParser.Drop($"{where} are Swarm's.", where)); break;
                default: issues.Add(DockerRunParser.Error($"{where} isn't a resource Gitberth knows.", where)); break;
            }
        }

        private void RestartPolicy(YamlNode value)
        {
            if (value is not YamlMappingNode map) return;
            var settings = Map(map).ToDictionary(e => e.Key, e => Text(e.Value) ?? "", StringComparer.Ordinal);
            var condition = settings.GetValueOrDefault("condition") ?? "any";
            var restart = condition switch
            {
                "none" => new RestartSetting("no"),
                "on-failure" => new RestartSetting("on-failure", int.TryParse(settings.GetValueOrDefault("max_attempts"), out var n) ? n : 0),
                _ => new RestartSetting("always"),
            };
            if (_restart is null)
            {
                _restart = restart;
                issues.Add(DockerRunParser.Note($"deploy: restart_policy is read as restart: {restart.Name}{(restart.MaximumRetryCount > 0 ? ":" + restart.MaximumRetryCount : "")}.", "restart_policy"));
            }
            foreach (var other in settings.Keys.Where(k => k is "delay" or "window"))
                issues.Add(DockerRunParser.Drop($"deploy: restart_policy: {other}: Docker restarts it at its own pace.", other));
        }

        // gpus: all, or [{driver, count, device_ids, capabilities}], as gpus: and deploy's devices write them.
        private string? GpuRequest(YamlNode value, string where)
        {
            if (Text(value) is { } all && value is YamlScalarNode) return all == "all" ? "all" : Fail<string?>($"{where}: {all} isn't all or a list.", where, null);
            string? found = null;
            foreach (var request in Items(value))
            {
                if (request is not YamlMappingNode r) continue;
                var settings = Map(r);
                var capabilities = settings.FirstOrDefault(e => e.Key == "capabilities").Value is { } caps ? Items(caps).Select(Text).OfType<string>().ToList() : ["gpu"];
                if (!capabilities.Contains("gpu"))
                {
                    issues.Add(DockerRunParser.Drop($"{where}: devices without the gpu capability ({Join(capabilities)}) aren't kept.", where));
                    continue;
                }
                var count = settings.FirstOrDefault(e => e.Key == "count").Value is { } c ? Text(c) : null;
                var ids = settings.FirstOrDefault(e => e.Key == "device_ids").Value is { } d ? Items(d).Select(Text).OfType<string>().ToList() : [];
                found = ids.Count > 0 ? "device=" + string.Join(',', ids) : count is null or "all" ? "all" : count;
                foreach (var other in settings.Select(e => e.Key).Where(k => k is "options" || k == "driver" && Text(settings.First(e => e.Key == k).Value) != "nvidia"))
                    issues.Add(DockerRunParser.Drop($"{where}: {other} isn't kept: GPUs go through Docker's own (nvidia) request.", where));
            }
            return found;
        }

        private void Label(string key, string value)
        {
            if (key.StartsWith("gitberth.", StringComparison.Ordinal))
                issues.Add(DockerRunParser.Error($"Labels starting with gitberth. are Gitberth's own: remove {key}.", $"labels: {key}"));
            else _labels[key] = value;
        }

        private void Health(YamlNode value)
        {
            if (value is not YamlMappingNode map) return;
            IReadOnlyList<string>? test = null;
            TimeSpan? interval = null, timeout = null, startPeriod = null, startInterval = null;
            int? retries = null;
            var disabled = false;
            foreach (var (key, v) in Map(map))
            {
                var where = $"healthcheck: {key}";
                switch (key)
                {
                    case "test":
                        if (v is YamlSequenceNode list)
                        {
                            var words = list.Children.Select(Text).OfType<string>().ToList();
                            if (words.FirstOrDefault() == "NONE") disabled = true;
                            else if (words.FirstOrDefault() is "CMD" or "CMD-SHELL") test = words;
                            else issues.Add(DockerRunParser.Error("healthcheck: test as a list starts with CMD, CMD-SHELL or NONE.", where));
                        }
                        else if (Text(v) is { Length: > 0 } shell) test = ["CMD-SHELL", shell];
                        break;
                    case "interval": interval = Time(v, where); break;
                    case "timeout": timeout = Time(v, where); break;
                    case "start_period": startPeriod = Time(v, where); break;
                    case "start_interval": startInterval = Time(v, where); break;
                    case "retries": retries = (int?)Whole(v, where); break;
                    case "disable": disabled = Bool(v); break;
                    default: issues.Add(DockerRunParser.Error($"healthcheck: {key} isn't a health check setting.", where)); break;
                }
            }
            _health = disabled ? new HealthSetting(HealthMode.Disabled)
                : new HealthSetting(HealthMode.Custom, test, interval, timeout, retries, startPeriod, startInterval);
            if (!disabled && test is null) issues.Add(DockerRunParser.Note("healthcheck: without test: it keeps the image's own check, with these timings.", "healthcheck"));
        }

        private void Device(YamlNode item)
        {
            string? spec;
            if (item is YamlMappingNode map)
            {
                var settings = Map(map).ToDictionary(e => e.Key, e => Text(e.Value) ?? "", StringComparer.Ordinal);
                spec = $"{settings.GetValueOrDefault("source")}:{settings.GetValueOrDefault("target") ?? settings.GetValueOrDefault("source")}:{settings.GetValueOrDefault("permissions") ?? "rwm"}";
            }
            else spec = Text(item);
            if (spec is not null && DockerRunParser.Device(spec) is { } device) _devices.Add(device);
            else if (spec?.Contains('=') == true) issues.Add(DockerRunParser.Drop($"devices: {spec}: a CDI device name isn't kept; give the device's path, such as /dev/dri.", "devices"));
            else issues.Add(DockerRunParser.Error($"devices: {spec} isn't /dev/host[:/dev/container[:rwm]].", "devices"));
        }

        private void Logging(YamlNode value)
        {
            foreach (var (key, v) in value is YamlMappingNode map ? Map(map) : [])
            {
                if (key == "driver") _logDriver = Text(v);
                else if (key == "options") foreach (var (k, o) in Pairs(v, "logging: options")) _logOptions[k] = o ?? "";
                else issues.Add(DockerRunParser.Error($"logging: {key} isn't a logging setting (driver, options).", "logging"));
            }
        }

        private void PullPolicy(string policy)
        {
            switch (policy)
            {
                case "always":
                    _pullFirst = true;
                    break;
                case "missing" or "if_not_present" or "":
                    break;
                case "never":
                    issues.Add(DockerRunParser.Note("pull_policy: never: Gitberth pulls the image when it isn't on the host, so the app can be created.", "pull_policy"));
                    break;
                case "build":
                    issues.Add(DockerRunParser.Note("pull_policy: build: Gitberth runs the image and builds nothing.", "pull_policy"));
                    break;
                default:
                    issues.Add(DockerRunParser.Note($"pull_policy: {policy}: Gitberth checks for newer images itself (Updates).", "pull_policy"));
                    break;
            }
        }

        // ---- Values ----

        // A scalar's text with Compose's variables replaced; null for a null or a list or mapping.
        private string? Text(YamlNode? node) =>
            node is YamlScalarNode scalar && !IsNull(node) ? Interpolate(scalar.Value ?? "", issues, _seen) : null;

        // A list's items; a single value is a list of one.
        private static IEnumerable<YamlNode> Items(YamlNode value) =>
            value is YamlSequenceNode list ? list.Children : IsNull(value) ? [] : [value];

        // A map (key: value) or a list of key=value, as environment, labels and sysctls are written; a value of null is "not given".
        private List<(string Key, string? Value)> Pairs(YamlNode value, string where)
        {
            if (value is YamlMappingNode map) return Map(map).Select(e => (e.Key, IsNull(e.Value) ? null : Text(e.Value) ?? "")).ToList();
            var pairs = new List<(string, string?)>();
            foreach (var item in Items(value).Select(Text).OfType<string>())
            {
                var eq = item.IndexOf('=');
                if (eq == 0) issues.Add(DockerRunParser.Error($"{where}: {item} has no name.", where));
                else pairs.Add(eq < 0 ? (item, null) : (item[..eq], item[(eq + 1)..]));
            }
            return pairs;
        }

        private IReadOnlyList<string>? Words(YamlNode value, string key)
        {
            if (value is YamlSequenceNode list) return list.Children.Select(c => Text(c) ?? "").ToList();
            if (Text(value) is not { } text) return null;
            if (text.Length == 0) return key == "entrypoint" ? [] : null;
            var (words, wordIssues) = ShellWords.Split(text, commandLine: false);
            issues.AddRange(wordIssues.Select(i => i with { Text = $"{key}: {i.Text}" }));
            return words;
        }

        private bool Bool(YamlNode value) => (Text(value) ?? "").ToLowerInvariant() is "true" or "yes" or "on" or "1";

        private long? Bytes(YamlNode value, string where) =>
            DockerRunParser.ParseBytes(Text(value) ?? "") ?? Fail<long?>($"{where}: {Text(value)} isn't a size such as 512m or 2g.", where, null);

        private long? Whole(YamlNode value, string where) =>
            long.TryParse(Text(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : Fail<long?>($"{where}: {Text(value)} isn't a whole number.", where, null);

        private TimeSpan? Time(YamlNode value, string where) =>
            DockerRunParser.ParseDuration(Text(value) ?? "") ?? Fail<TimeSpan?>($"{where}: {Text(value)} isn't a time such as 30s or 1m30s.", where, null);

        private T Fail<T>(string text, string fragment, T keep)
        {
            issues.Add(DockerRunParser.Error(text, fragment));
            return keep;
        }

        private static string Join(IReadOnlyList<string> names) =>
            names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }
}
