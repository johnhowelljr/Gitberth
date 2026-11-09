using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Sources;

/// <summary>
/// Reads a pasted docker run command as an app's settings (3.5). Every option docker run has is either carried over, listed
/// as Dropped with the reason, or an Error: nothing is ignored without saying so. Both --flag value and --flag=value are
/// read, and short flags combine (-itd, -p8080:80). What the command doesn't set gets Gitberth's own defaults when the app
/// is created (restart unless-stopped, the shared network, LAN ports on suggested host ports).
/// </summary>
public static partial class DockerRunParser
{
    private enum Takes { Nothing, Value }

    private sealed record Option(Takes Takes, Action<Builder, string, string> Apply);

    private static readonly Dictionary<char, string> Shorts = new()
    {
        ['a'] = "attach", ['c'] = "cpu-shares", ['d'] = "detach", ['e'] = "env", ['h'] = "hostname", ['i'] = "interactive", ['l'] = "label",
        ['m'] = "memory", ['p'] = "publish", ['P'] = "publish-all", ['q'] = "quiet", ['t'] = "tty", ['u'] = "user", ['v'] = "volume", ['w'] = "workdir",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["net"] = "network", ["net-alias"] = "network-alias", ["dns-opt"] = "dns-option", ["link-local-ip"] = "link-local-ip",
    };

    private static readonly Dictionary<string, Option> Options = new(StringComparer.Ordinal)
    {
        // Carried over.
        ["detach"] = Flag((_, _, _) => { }),
        ["interactive"] = Flag((b, v, _) => b.OpenStdin = True(v)),
        ["tty"] = Flag((b, v, _) => b.Tty = True(v)),
        ["name"] = Value((b, v, f) => b.SetName(v, f)),
        ["env"] = Value((b, v, f) => b.AddEnv(v, f)),
        ["publish"] = Value((b, v, f) => b.Ports.AddRange(Publish(v, f, b.Issues))),
        ["publish-all"] = Flag((b, v, _) => b.PublishAll = True(v)),
        ["expose"] = Value((b, v, f) => b.Ports.AddRange(Expose(v, f, b.Issues))),
        ["volume"] = Value((b, v, f) => b.AddMount(Volume(v, f, b.Issues))),
        ["mount"] = Value((b, v, f) => b.AddMount(Mount(v, f, b.Issues))),
        ["tmpfs"] = Value((b, v, f) => b.AddMount(Tmpfs(v, f, b.Issues))),
        ["network"] = Value((b, v, f) => b.AddNetwork(v, f)),
        ["network-alias"] = Value((b, v, f) => b.AddAlias(v, f)),
        ["ip"] = Value((b, v, f) => b.SetIp(v, f)),
        ["hostname"] = Value((b, v, _) => b.Hostname = v),
        ["domainname"] = Value((b, v, _) => b.Domainname = v),
        ["add-host"] = Value((b, v, f) => b.AddHost(v, f)),
        ["dns"] = Value((b, v, _) => b.Dns.Add(v)),
        ["dns-search"] = Value((b, v, _) => b.DnsSearch.Add(v)),
        ["dns-option"] = Value((b, v, _) => b.DnsOptions.Add(v)),
        ["restart"] = Value((b, v, f) => b.Restart = Restart(v) ?? b.Fail($"{f}: the restart policy is no, always, unless-stopped or on-failure[:times].", f, b.Restart)),
        ["memory"] = Value((b, v, f) => b.Memory = b.Bytes(v, f)),
        ["memory-reservation"] = Value((b, v, f) => b.MemoryReservation = b.Bytes(v, f)),
        ["memory-swap"] = Value((b, v, f) => b.MemorySwap = v.Trim() == "-1" ? -1 : b.Bytes(v, f)),
        ["cpus"] = Value((b, v, f) => b.Cpus = Cpus(v) ?? b.Fail<double?>($"{f}: give a number of CPUs, such as 1.5.", f, null)),
        ["cpu-shares"] = Value((b, v, f) => b.CpuShares = b.Whole(v, f)),
        ["cpuset-cpus"] = Value((b, v, _) => b.CpusetCpus = v),
        ["cpu-quota"] = Value((b, v, f) => b.CpuQuota = b.Whole(v, f)),
        ["cpu-period"] = Value((b, v, f) => b.CpuPeriod = b.Whole(v, f)),
        ["pids-limit"] = Value((b, v, f) => b.PidsLimit = b.Whole(v, f)),
        ["shm-size"] = Value((b, v, f) => b.ShmSize = b.Bytes(v, f)),
        ["ulimit"] = Value((b, v, f) => b.AddUlimit(v, f)),
        ["workdir"] = Value((b, v, _) => b.WorkingDir = v),
        ["user"] = Value((b, v, _) => b.User = v),
        ["entrypoint"] = Value((b, v, _) => b.Entrypoint = v.Length == 0 ? [] : [v]),
        ["label"] = Value((b, v, f) => b.AddLabel(v, f)),
        ["health-cmd"] = Value((b, v, _) => { b.HealthCmd = v; b.AnyHealth = true; }),
        ["health-interval"] = Value((b, v, f) => { b.HealthInterval = b.Duration(v, f); b.AnyHealth = true; }),
        ["health-timeout"] = Value((b, v, f) => { b.HealthTimeout = b.Duration(v, f); b.AnyHealth = true; }),
        ["health-retries"] = Value((b, v, f) => { b.HealthRetries = (int?)b.Whole(v, f); b.AnyHealth = true; }),
        ["health-start-period"] = Value((b, v, f) => { b.HealthStartPeriod = b.Duration(v, f); b.AnyHealth = true; }),
        ["health-start-interval"] = Value((b, v, f) => { b.HealthStartInterval = b.Duration(v, f); b.AnyHealth = true; }),
        ["no-healthcheck"] = Flag((b, v, _) => b.NoHealthcheck = True(v)),
        ["device"] = Value((b, v, f) => b.AddDevice(v, f)),
        ["gpus"] = Value((b, v, f) => b.Gpus = Gpus(v, f, b.Issues)),
        ["cap-add"] = Value((b, v, _) => b.CapAdd.Add(v.Trim().ToUpperInvariant())),
        ["cap-drop"] = Value((b, v, _) => b.CapDrop.Add(v.Trim().ToUpperInvariant())),
        ["privileged"] = Flag((b, v, _) => b.Privileged = True(v)),
        ["security-opt"] = Value((b, v, _) => b.SecurityOpt.Add(v)),
        ["read-only"] = Flag((b, v, _) => b.ReadOnlyRootfs = True(v)),
        ["init"] = Flag((b, v, _) => b.Init = True(v)),
        ["sysctl"] = Value((b, v, f) => b.AddPair(b.Sysctls, v, f, "a kernel setting as name=value, such as net.core.somaxconn=1024")),
        ["log-driver"] = Value((b, v, _) => b.LogDriver = v),
        ["log-opt"] = Value((b, v, f) => b.AddPair(b.LogOptions, v, f, "a logging option as name=value, such as max-size=10m")),
        ["stop-signal"] = Value((b, v, _) => b.StopSignal = v),
        ["stop-timeout"] = Value((b, v, f) => b.StopTimeout = (int?)b.Whole(v, f)),
        ["platform"] = Value((b, v, _) => b.Platform = v),
        ["pull"] = Value((b, v, f) => b.SetPull(v, f)),

        // Not carried over, each with its reason.
        ["rm"] = Dropped(false, "Gitberth keeps its apps: --rm would delete it the first time it stops."),
        ["link"] = Dropped(true, "Links are Docker's old way to connect containers: join a network instead, where containers reach each other by name."),
        ["volumes-from"] = Dropped(true, "Mount the volumes it needs by name instead (Storage)."),
        ["env-file"] = Dropped(true, "The file is on another machine: paste its lines into Environment."),
        ["label-file"] = Dropped(true, "The file is on another machine: add its labels under Labels."),
        ["cidfile"] = Dropped(true, "Gitberth keeps the container's ID itself."),
        ["attach"] = Dropped(true, "Gitberth runs it in the background; its output is on the Logs tab."),
        ["sig-proxy"] = Dropped(false, "It only matters to a terminal attached to the container."),
        ["detach-keys"] = Dropped(true, "It only matters to a terminal attached to the container."),
        ["disable-content-trust"] = Dropped(false, "Gitberth pulls as Docker is configured to."),
        ["quiet"] = Dropped(false, "It only quiets docker run's own output."),
        ["cgroupns"] = Dropped(true, "It gets its own cgroup namespace, as Docker gives every container."),
        ["cgroup-parent"] = Dropped(true, "It runs in Docker's own cgroup."),
        ["runtime"] = Dropped(true, "It runs with Docker's default runtime."),
        ["isolation"] = Dropped(true, "Isolation is a Windows containers setting."),
        ["userns"] = Dropped(true, "It gets the user namespace Docker gives every container."),
        ["pid"] = Dropped(true, "It gets its own process namespace, as Docker gives every container."),
        ["ipc"] = Dropped(true, "It gets its own IPC namespace, as Docker gives every container."),
        ["uts"] = Dropped(true, "It gets its own UTS namespace, as Docker gives every container."),
        ["mac-address"] = Dropped(true, "Docker gives it a MAC address of its own."),
        ["oom-kill-disable"] = Dropped(false, "Gitberth doesn't keep it: the kernel may stop the container when memory runs out."),
        ["oom-score-adj"] = Dropped(true, "Gitberth doesn't keep it: the kernel weighs the container as it does any other."),
        ["blkio-weight"] = Dropped(true, "Gitberth doesn't keep disk I/O weights."),
        ["blkio-weight-device"] = Dropped(true, "Gitberth doesn't keep disk I/O weights."),
        ["device-read-bps"] = Dropped(true, "Gitberth doesn't keep disk I/O limits."),
        ["device-write-bps"] = Dropped(true, "Gitberth doesn't keep disk I/O limits."),
        ["device-read-iops"] = Dropped(true, "Gitberth doesn't keep disk I/O limits."),
        ["device-write-iops"] = Dropped(true, "Gitberth doesn't keep disk I/O limits."),
        ["kernel-memory"] = Dropped(true, "Docker no longer supports a kernel memory limit."),
        ["group-add"] = Dropped(true, "Gitberth doesn't keep extra groups: set the user it runs as (User) instead."),
        ["storage-opt"] = Dropped(true, "Gitberth doesn't keep storage driver options."),
        ["annotation"] = Dropped(true, "Gitberth doesn't keep annotations: use a label instead."),
        ["cpu-count"] = Dropped(true, "It's a Windows containers setting: use CPUs instead."),
        ["cpu-percent"] = Dropped(true, "It's a Windows containers setting: use CPUs instead."),
        ["cpu-rt-period"] = Dropped(true, "Gitberth doesn't keep real-time CPU settings."),
        ["cpu-rt-runtime"] = Dropped(true, "Gitberth doesn't keep real-time CPU settings."),
        ["cpuset-mems"] = Dropped(true, "Gitberth doesn't keep memory node pinning."),
        ["device-cgroup-rule"] = Dropped(true, "Gitberth doesn't keep device cgroup rules: add the device itself (Devices)."),
        ["io-maxbandwidth"] = Dropped(true, "It's a Windows containers setting."),
        ["io-maxiops"] = Dropped(true, "It's a Windows containers setting."),
        ["ip6"] = Dropped(true, "Docker gives it its IPv6 address."),
        ["link-local-ip"] = Dropped(true, "Docker gives it its addresses."),
        ["memory-swappiness"] = Dropped(true, "Gitberth doesn't keep it: the kernel's own setting applies."),
        ["volume-driver"] = Dropped(true, "Its volumes are made with Docker's local driver."),
        ["use-api-socket"] = Dropped(false, "Mount /var/run/docker.sock under Storage if it needs Docker's API."),
        ["help"] = new Option(Takes.Nothing, (b, _, f) => b.Issues.Add(Error("--help only prints docker run's help: remove it.", f))),
    };

    /// <summary>The settings a pasted docker run command asks for, with every issue (3.5).</summary>
    public static ParsedApp Parse(string text)
    {
        var (words, issues) = ShellWords.Split(text);
        var b = new Builder();
        b.Issues.AddRange(issues);
        var i = Lead(words, b.Issues);
        if (i < 0) return b.Build(null, []);

        for (; i < words.Count; i++)
        {
            var w = words[i];
            if (w == "--")
            {
                i++;
                break;
            }
            if (!w.StartsWith('-') || w == "-") break;

            if (w.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = w.IndexOf('=');
                var name = eq < 0 ? w[2..] : w[2..eq];
                name = Aliases.GetValueOrDefault(name, name);
                if (!Options.TryGetValue(name, out var option))
                {
                    b.Issues.Add(Error($"Unknown option --{name}: remove it or set it by hand.", w));
                    continue;
                }
                if (option.Takes == Takes.Nothing)
                {
                    option.Apply(b, eq < 0 ? "true" : w[(eq + 1)..], w);
                    continue;
                }
                if (eq >= 0)
                {
                    option.Apply(b, w[(eq + 1)..], w);
                }
                else if (i + 1 < words.Count)
                {
                    option.Apply(b, words[i + 1], $"{w} {words[i + 1]}");
                    i++;
                }
                else
                {
                    b.Issues.Add(Error($"{w} needs a value.", w));
                }
                continue;
            }

            // -itd, -p8080:80, -e=KEY=value: each letter is a flag; one that takes a value takes the rest, or the next word.
            for (var k = 1; k < w.Length; k++)
            {
                if (!Shorts.TryGetValue(w[k], out var name))
                {
                    b.Issues.Add(Error($"Unknown option -{w[k]}: remove it or set it by hand.", w));
                    break;
                }
                var option = Options[name];
                if (option.Takes == Takes.Nothing)
                {
                    if (k + 1 < w.Length && w[k + 1] == '=')
                    {
                        option.Apply(b, w[(k + 2)..], w);
                        break;
                    }
                    option.Apply(b, "true", w);
                    continue;
                }
                var rest = w[(k + 1)..];
                if (rest.StartsWith('=')) rest = rest[1..];
                if (rest.Length > 0) option.Apply(b, rest, w);
                else if (i + 1 < words.Count)
                {
                    option.Apply(b, words[i + 1], $"{w} {words[i + 1]}");
                    i++;
                }
                else b.Issues.Add(Error($"{w} needs a value.", w));
                break;
            }
        }

        if (i >= words.Count)
        {
            b.Issues.Add(Error("There's no image in the command: it goes after the options (docker run … IMAGE).", null));
            return b.Build(null, []);
        }
        var image = words[i];
        if (ImageReference.Parse(image) is null && !LocalId().IsMatch(image))
            b.Issues.Add(Error(ImageService.NotAReference(image), image));
        var command = words.Skip(i + 1).ToList();
        if (command.Count > 0 && command[0].StartsWith('-') && KnownOption(command[0]))
            b.Issues.Add(Note($"{command[0]} comes after the image, so it goes to the image's program, not to Docker. Put Docker's options before the image.", command[0]));
        return b.Build(image, command);
    }

    // The words before the options: docker run, docker container run, sudo docker run. The index of the first option, or -1.
    private static int Lead(IReadOnlyList<string> words, List<ParseIssue> issues)
    {
        var i = 0;
        if (words.Count > 0 && words[0] == "sudo") i++;
        if (i >= words.Count)
        {
            issues.Add(Error("Paste a docker run command, such as docker run -d -p 8080:80 nginx.", null));
            return -1;
        }
        if (words[i] is "docker-compose" || words[i] == "docker" && i + 1 < words.Count && words[i + 1] == "compose")
        {
            issues.Add(Error("That's a Compose command: paste the service's YAML under Paste a Compose service instead.", words[i]));
            return -1;
        }
        if (words[i] == "podman")
        {
            issues.Add(Error("That's a podman command: Gitberth reads docker run (most options are the same, so try it with docker instead of podman).", words[i]));
            return -1;
        }
        if (words[i] != "docker")
        {
            issues.Add(Error("Paste a docker run command: it starts with docker run.", words[i]));
            return -1;
        }
        i++;
        if (i < words.Count && words[i] == "run") return i + 1;
        if (i + 1 < words.Count && words[i] == "container" && words[i + 1] == "run") return i + 2;
        if (i < words.Count && (words[i] == "create" || i + 1 < words.Count && words[i] == "container" && words[i + 1] == "create"))
        {
            issues.Add(Note("docker create is read as docker run: Gitberth starts the app once it's created.", words[i]));
            return words[i] == "create" ? i + 1 : i + 2;
        }
        issues.Add(i < words.Count
            ? Error($"Gitberth reads docker run commands, not docker {words[i]}.", words[i])
            : Error("Paste a docker run command, such as docker run -d -p 8080:80 nginx.", "docker"));
        return -1;
    }

    private static bool KnownOption(string word)
    {
        if (word.StartsWith("--", StringComparison.Ordinal))
        {
            var eq = word.IndexOf('=');
            var name = eq < 0 ? word[2..] : word[2..eq];
            return Options.ContainsKey(Aliases.GetValueOrDefault(name, name));
        }
        return word.Length >= 2 && Shorts.ContainsKey(word[1]);
    }

    private static Option Flag(Action<Builder, string, string> apply) => new(Takes.Nothing, apply);

    private static Option Value(Action<Builder, string, string> apply) => new(Takes.Value, apply);

    private static Option Dropped(bool takesValue, string why) =>
        new(takesValue ? Takes.Value : Takes.Nothing, (b, v, f) =>
        {
            if (takesValue || True(v)) b.Issues.Add(new ParseIssue(ParseSeverity.Dropped, $"Not carried over: {why}", f));
        });

    private static bool True(string value) => value.Trim().ToLowerInvariant() is not ("false" or "0" or "no");

    internal static ParseIssue Note(string text, string? fragment) => new(ParseSeverity.Note, text, fragment);

    internal static ParseIssue Error(string text, string? fragment) => new(ParseSeverity.Error, text, fragment);

    internal static ParseIssue Drop(string text, string? fragment) => new(ParseSeverity.Dropped, $"Not carried over: {text}", fragment);

    // ---- Shared with ComposeParser: the forms docker run and Compose write alike. ----

    /// <summary>
    /// -p and Compose's short ports: [ip:][host:]container[/protocol], [::1]:8080:80, and ranges of up to 100 ports. No
    /// address, 0.0.0.0 or :: is the LAN; 127.0.0.1 or ::1 the host only; another address is Custom. No host port leaves it
    /// for Gitberth to suggest.
    /// </summary>
    internal static List<PortRule> Publish(string spec, string fragment, List<ParseIssue> issues)
    {
        var s = spec.Trim();
        var protocol = "tcp";
        var slash = s.LastIndexOf('/');
        if (slash >= 0)
        {
            protocol = s[(slash + 1)..].ToLowerInvariant();
            s = s[..slash];
            if (protocol is not ("tcp" or "udp" or "sctp"))
            {
                issues.Add(Error($"{spec}: the protocol is tcp, udp or sctp.", fragment));
                return [];
            }
        }
        string? ip = null;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != ':')
            {
                issues.Add(Error($"{spec} isn't a port mapping: [address]:host:container.", fragment));
                return [];
            }
            ip = s[1..close];
            s = s[(close + 2)..];
        }
        var parts = s.Split(':');
        string? host = null;
        string container;
        if (ip is not null)
        {
            if (parts.Length > 2)
            {
                issues.Add(Error($"{spec} isn't a port mapping: [address]:host:container.", fragment));
                return [];
            }
            host = parts.Length == 2 ? parts[0] : null;
            container = parts[^1];
        }
        else
        {
            switch (parts.Length)
            {
                case 1:
                    container = parts[0];
                    break;
                case 2:
                    host = parts[0];
                    container = parts[1];
                    break;
                case 3:
                    ip = parts[0];
                    host = parts[1];
                    container = parts[2];
                    break;
                default:
                    issues.Add(Error($"{spec}: put an IPv6 address in brackets, such as [::1]:8080:80.", fragment));
                    return [];
            }
        }
        if (string.IsNullOrEmpty(host)) host = null;
        if (string.IsNullOrEmpty(ip)) ip = null;
        if (ip is not null && !IPAddress.TryParse(ip, out _))
        {
            issues.Add(Error($"{spec}: {ip} isn't an address of the host.", fragment));
            return [];
        }
        if (Range(container) is not { } containers)
        {
            issues.Add(Error($"{spec}: the container port is a number from 1 to 65535, or a range such as 8000-8010.", fragment));
            return [];
        }
        (int From, int To)? hosts = null;
        if (host is not null && (hosts = Range(host)) is null)
        {
            issues.Add(Error($"{spec}: the host port is a number from 1 to 65535, or a range such as 8000-8010.", fragment));
            return [];
        }
        var count = containers.To - containers.From + 1;
        if (count > 100)
        {
            issues.Add(Error($"{spec} publishes {count} ports: give at most 100 at a time.", fragment));
            return [];
        }

        var access = ContainerPorts.AccessOf(ip);
        var hostIp = access == PortAccess.Custom ? ip : null;
        if (ip == "::1") issues.Add(Note($"{spec}: Host only is published on 127.0.0.1.", fragment));
        var rules = new List<PortRule>();
        if (hosts is { } h && h.To - h.From != containers.To - containers.From)
        {
            if (count == 1)
            {
                issues.Add(Note($"{spec}: Docker would pick a host port from {h.From}-{h.To}; Gitberth suggests one instead.", fragment));
                rules.Add(new PortRule(containers.From, protocol, access, null, hostIp));
                return rules;
            }
            issues.Add(Error($"{spec}: the host and container ranges must be the same length.", fragment));
            return [];
        }
        for (var n = 0; n < count; n++)
            rules.Add(new PortRule(containers.From + n, protocol, access, hosts is { } r ? r.From + n : null, hostIp));
        return rules;
    }

    /// <summary>--expose and Compose's expose: 8080, 8080/udp, 8000-8010; reached by containers on its networks only.</summary>
    internal static List<PortRule> Expose(string spec, string fragment, List<ParseIssue> issues)
    {
        var s = spec.Trim();
        var protocol = "tcp";
        var slash = s.LastIndexOf('/');
        if (slash >= 0)
        {
            protocol = s[(slash + 1)..].ToLowerInvariant();
            s = s[..slash];
        }
        if (protocol is not ("tcp" or "udp" or "sctp") || Range(s) is not { } range || range.To - range.From >= 100)
        {
            issues.Add(Error($"{spec} isn't a port to expose: a number, a range of up to 100, and /tcp or /udp.", fragment));
            return [];
        }
        return Enumerable.Range(range.From, range.To - range.From + 1).Select(p => new PortRule(p, protocol, PortAccess.Internal)).ToList();
    }

    private static (int From, int To)? Range(string text)
    {
        var dash = text.IndexOf('-');
        var from = Port(dash < 0 ? text : text[..dash]);
        var to = dash < 0 ? from : Port(text[(dash + 1)..]);
        return from is { } a && to is { } b && b >= a ? (a, b) : null;
    }

    private static int? Port(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    /// <summary>
    /// -v and Compose's short volumes: a named volume (name:/path), a bind mount (/host/path:/path), or an anonymous one
    /// (/path), with :ro or :rw. A relative or home path is an Error: there's no folder it's relative to. SELinux labels
    /// (:z, :Z), propagation, consistency and nocopy are dropped with a note. An anonymous one gets a name (Source "",
    /// named by the creator) so its data survives the app being recreated.
    /// </summary>
    internal static MountSetting? Volume(string spec, string fragment, List<ParseIssue> issues)
    {
        var parts = spec.Trim().Split(':');
        string? source = null, mode = null;
        string target;
        switch (parts.Length)
        {
            case 1:
                target = parts[0];
                break;
            case 2 when ValidModes(parts[1]):
                target = parts[0];
                mode = parts[1];
                break;
            case 2:
                source = parts[0];
                target = parts[1];
                break;
            case 3:
                source = parts[0];
                target = parts[1];
                mode = parts[2];
                break;
            default:
                if (parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0]))
                {
                    issues.Add(Error($"{spec} is a Windows path: give a folder on the Docker host (Linux), such as /srv/data.", fragment));
                    return null;
                }
                issues.Add(Error($"{spec} isn't a volume: name:/path, /host/path:/path or /path, then :ro if it's read-only.", fragment));
                return null;
        }
        if (parts.Length >= 2 && parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0]) && parts.Length > 2)
        {
            issues.Add(Error($"{spec} is a Windows path: give a folder on the Docker host (Linux), such as /srv/data.", fragment));
            return null;
        }
        if (!target.StartsWith('/'))
        {
            issues.Add(Error($"{spec}: the path in the container must be absolute, such as /data.", fragment));
            return null;
        }
        var readOnly = false;
        foreach (var option in (mode ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (option)
            {
                case "ro":
                case "readonly":
                    readOnly = true;
                    break;
                case "rw":
                    break;
                case "z":
                case "Z":
                    issues.Add(Drop($":{option} asks Docker to relabel the folder for SELinux; Gitberth mounts it as it is.", fragment));
                    break;
                case "nocopy":
                    issues.Add(Drop(":nocopy: Docker copies the image's files into a new volume, as usual.", fragment));
                    break;
                case "shared" or "rshared" or "slave" or "rslave" or "private" or "rprivate":
                    issues.Add(Drop($":{option} mount propagation: it's mounted with Docker's default (rprivate).", fragment));
                    break;
                case "consistent" or "cached" or "delegated":
                    issues.Add(Drop($":{option} is a Docker Desktop setting, with no effect on Linux.", fragment));
                    break;
                default:
                    issues.Add(Error($"{spec}: {option} isn't a volume option (ro, rw).", fragment));
                    return null;
            }
        }
        if (source is null)
        {
            issues.Add(Note($"{target} has no volume name: Gitberth gives it one (gb-<app>-{Segment(target)}) so its data survives the app being recreated.", fragment));
            return new MountSetting(MountKind.Volume, "", target, readOnly);
        }
        return HostSource(source, spec, fragment, issues) switch
        {
            null => null,
            true => new MountSetting(MountKind.Bind, source, target, readOnly),
            false => new MountSetting(MountKind.Volume, source, target, readOnly),
        };
    }

    // True for a bind (an absolute host path), false for a volume's name, null with an Error for a path Gitberth can't place.
    internal static bool? HostSource(string source, string spec, string fragment, List<ParseIssue> issues)
    {
        if (source.StartsWith('/')) return true;
        if (source.StartsWith('.') || source.StartsWith('~'))
        {
            issues.Add(Error($"{source} is relative to the folder the command ran in, which Gitberth doesn't know: give the full path on the host, such as /srv/{Segment(source)}.", fragment));
            return null;
        }
        if (source.Length >= 2 && char.IsAsciiLetter(source[0]) && source[1] == ':' || source.Contains('\\'))
        {
            issues.Add(Error($"{source} is a Windows path: give a folder on the Docker host (Linux), such as /srv/data.", fragment));
            return null;
        }
        if (!VolumeNamePattern().IsMatch(source))
        {
            issues.Add(Error($"{spec}: {source} isn't a volume name (letters, digits, _ . -).", fragment));
            return null;
        }
        return false;
    }

    private static bool ValidModes(string mode) =>
        mode.Split(',').All(m => m is "ro" or "rw" or "z" or "Z" or "nocopy" or "shared" or "rshared" or "slave" or "rslave" or "private" or "rprivate"
            or "consistent" or "cached" or "delegated");

    /// <summary>
    /// --mount's comma-separated form: type=volume|bind|tmpfs, source/src, target/dst/destination, readonly/ro, tmpfs-size.
    /// The options Gitberth doesn't keep are dropped one by one with a note.
    /// </summary>
    internal static MountSetting? Mount(string spec, string fragment, List<ParseIssue> issues)
    {
        string type = "volume", source = "", target = "";
        var readOnly = false;
        long? size = null;
        foreach (var field in Csv(spec))
        {
            var eq = field.IndexOf('=');
            var key = (eq < 0 ? field : field[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? "" : field[(eq + 1)..].Trim();
            switch (key)
            {
                case "type":
                    type = value.ToLowerInvariant();
                    break;
                case "source" or "src":
                    source = value;
                    break;
                case "target" or "dst" or "destination":
                    target = value;
                    break;
                case "readonly" or "ro":
                    readOnly = eq < 0 || True(value);
                    break;
                case "tmpfs-size":
                    size = ParseBytes(value);
                    if (size is null) issues.Add(Error($"{spec}: tmpfs-size is a size such as 64m.", fragment));
                    break;
                case "volume-nocopy":
                    issues.Add(Drop("volume-nocopy: Docker copies the image's files into a new volume, as usual.", fragment));
                    break;
                case "bind-propagation":
                    issues.Add(Drop($"bind-propagation={value}: it's mounted with Docker's default (rprivate).", fragment));
                    break;
                case "consistency":
                    issues.Add(Drop($"consistency={value} is a Docker Desktop setting, with no effect on Linux.", fragment));
                    break;
                case "volume-driver" or "volume-opt" or "volume-label":
                    issues.Add(Drop($"{key}: the volume is made with Docker's local driver, without options or labels.", fragment));
                    break;
                case "volume-subpath" or "image-subpath":
                    issues.Add(Drop($"{key}: the whole volume is mounted.", fragment));
                    break;
                case "bind-recursive" or "bind-nonrecursive" or "bind-create-src":
                    issues.Add(Drop($"{key}: it's mounted as -v mounts a folder.", fragment));
                    break;
                case "tmpfs-mode":
                    issues.Add(Drop($"tmpfs-mode={value}: the tmpfs gets Docker's default mode.", fragment));
                    break;
                default:
                    issues.Add(Error($"{spec}: {key} isn't a --mount option Gitberth knows.", fragment));
                    return null;
            }
        }
        if (target.Length == 0 || !target.StartsWith('/'))
        {
            issues.Add(Error($"{spec} needs target=, an absolute path in the container.", fragment));
            return null;
        }
        switch (type)
        {
            case "bind":
                if (source.Length == 0)
                {
                    issues.Add(Error($"{spec}: a bind mount needs source=, a folder on the host.", fragment));
                    return null;
                }
                return HostSource(source, spec, fragment, issues) is true ? new MountSetting(MountKind.Bind, source, target, readOnly)
                    : source.StartsWith('/') ? null : Fail(issues, $"{spec}: a bind mount's source is an absolute path on the host.", fragment);
            case "volume":
                if (source.Length == 0)
                {
                    issues.Add(Note($"{target} has no volume name: Gitberth gives it one (gb-<app>-{Segment(target)}) so its data survives the app being recreated.", fragment));
                    return new MountSetting(MountKind.Volume, "", target, readOnly);
                }
                return HostSource(source, spec, fragment, issues) is false ? new MountSetting(MountKind.Volume, source, target, readOnly) : null;
            case "tmpfs":
                return new MountSetting(MountKind.Tmpfs, "", target, readOnly, size);
            default:
                issues.Add(Drop($"type={type} mounts aren't kept: Gitberth keeps volumes, bind mounts and tmpfs.", fragment));
                return null;
        }
    }

    private static MountSetting? Fail(List<ParseIssue> issues, string text, string fragment)
    {
        if (!issues.Any(i => i.Severity == ParseSeverity.Error && i.Fragment == fragment)) issues.Add(Error(text, fragment));
        return null;
    }

    /// <summary>--tmpfs /path[:options]: size= and ro are kept; the other mount options are dropped with a note.</summary>
    internal static MountSetting? Tmpfs(string spec, string fragment, List<ParseIssue> issues)
    {
        var colon = spec.IndexOf(':');
        var target = (colon < 0 ? spec : spec[..colon]).Trim();
        if (!target.StartsWith('/'))
        {
            issues.Add(Error($"{spec}: a tmpfs needs an absolute path in the container, such as /run.", fragment));
            return null;
        }
        long? size = null;
        var readOnly = false;
        foreach (var option in colon < 0 ? [] : spec[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (option.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseBytes(option[5..]);
                if (size is null) issues.Add(Error($"{spec}: size= is a size such as 64m.", fragment));
            }
            else if (option == "ro") readOnly = true;
            else if (option != "rw") issues.Add(Drop($"the tmpfs option {option}: it's mounted with Docker's defaults.", fragment));
        }
        return new MountSetting(MountKind.Tmpfs, "", target, readOnly, size);
    }

    /// <summary>The fields of --mount and --network's long form: comma-separated, a field in double quotes may hold a comma.</summary>
    internal static List<string> Csv(string text)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields.Where(f => f.Trim().Length > 0).ToList();
    }

    /// <summary>A size as Docker reads it: 512m, 1g, 1.5GB, 64MiB, 1048576 (bytes); units are powers of 1024. Null for anything else.</summary>
    internal static long? ParseBytes(string text)
    {
        var m = SizePattern().Match(text.Trim());
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        var power = unit switch { "" or "b" => 0, "k" => 1, "m" => 2, "g" => 3, "t" => 4, "p" => 5, _ => -1 };
        if (power < 0) return null;
        var bytes = number * Math.Pow(1024, power);
        return bytes is >= 0 and < long.MaxValue ? (long)bytes : null;
    }

    /// <summary>A Go duration, as Docker and Compose write them: 30s, 1m30s, 500ms, 1h. Null for anything else.</summary>
    internal static TimeSpan? ParseDuration(string text)
    {
        var s = text.Trim();
        if (s == "0") return TimeSpan.Zero;
        var matches = DurationPart().Matches(s);
        if (matches.Count == 0 || matches.Sum(m => m.Length) != s.Length) return null;
        double ticks = 0;
        foreach (Match m in matches)
        {
            var number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            ticks += m.Groups[2].Value switch
            {
                "ns" => number / 100,
                "us" or "µs" => number * 10,
                "ms" => number * TimeSpan.TicksPerMillisecond,
                "s" => number * TimeSpan.TicksPerSecond,
                "m" => number * TimeSpan.TicksPerMinute,
                _ => number * TimeSpan.TicksPerHour,
            };
        }
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>no, always, unless-stopped, on-failure[:times]; null for anything else.</summary>
    internal static RestartSetting? Restart(string text)
    {
        var s = text.Trim().Trim('"', '\'');
        if (s is "no" or "always" or "unless-stopped") return new RestartSetting(s);
        if (s == "on-failure") return new RestartSetting("on-failure");
        if (s.StartsWith("on-failure:", StringComparison.Ordinal)
            && int.TryParse(s["on-failure:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var times)) return new RestartSetting("on-failure", times);
        return null;
    }

    internal static double? Cpus(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus) && cpus > 0 && cpus <= 1024 ? Math.Round(cpus, 3) : null;

    /// <summary>name=soft[:hard], as --ulimit takes it; a single number is both.</summary>
    internal static UlimitSetting? Ulimit(string text)
    {
        var eq = text.IndexOf('=');
        if (eq <= 0) return null;
        var values = text[(eq + 1)..].Split(':');
        if (values.Length > 2 || !long.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var soft)) return null;
        var hard = soft;
        if (values.Length == 2 && !long.TryParse(values[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out hard)) return null;
        return new UlimitSetting(text[..eq].Trim(), soft, hard);
    }

    /// <summary>/dev/x[:/dev/y[:rwm]]; null for anything else (CDI names among them).</summary>
    internal static DeviceSetting? Device(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length > 3 || !parts[0].StartsWith('/')) return null;
        var container = parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : parts[0];
        var permissions = parts.Length == 3 ? parts[2] : parts.Length == 2 && !parts[1].StartsWith('/') ? parts[1] : "rwm";
        return permissions.Length > 0 && permissions.All(c => c is 'r' or 'w' or 'm') ? new DeviceSetting(parts[0], container, permissions) : null;
    }

    /// <summary>--gpus: all, a count, device=0,1, or the long form's count= and device=.</summary>
    internal static string? Gpus(string text, string fragment, List<ParseIssue> issues)
    {
        var s = text.Trim();
        var bare = s.Trim('"', '\'');
        if (bare == "all") return "all";
        if (int.TryParse(bare, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0) return bare;
        string? value = null;
        // "device=0,1" is quoted so its comma stays inside the field, as Docker's own reader takes it.
        foreach (var field in Csv(s.Trim('\'')))
        {
            var eq = field.IndexOf('=');
            var key = eq < 0 ? field.Trim() : field[..eq].Trim();
            var v = eq < 0 ? "" : field[(eq + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "count":
                    value = v == "all" ? "all" : v;
                    break;
                case "device":
                    value = "device=" + v;
                    break;
                case "driver" when v == "nvidia":
                    break;
                case "capabilities":
                    issues.Add(Note($"{fragment}: the GPUs get Docker's gpu capability.", fragment));
                    break;
                default:
                    issues.Add(Error($"{fragment}: --gpus is all, a count (2) or device=0,1.", fragment));
                    return null;
            }
        }
        if (value is null) issues.Add(Error($"{fragment}: --gpus is all, a count (2) or device=0,1.", fragment));
        return value;
    }

    /// <summary>A volume's name from the path it's mounted on: data for /var/lib/data, postgresql for /var/lib/postgresql.</summary>
    internal static string Segment(string path)
    {
        var last = path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault(p => p.Length > 0 && p != "." && p != "~") ?? "";
        var clean = Regex.Replace(last.ToLowerInvariant(), "[^a-z0-9_.-]+", "-").Trim('-', '.');
        return clean.Length == 0 ? "data" : clean;
    }

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)?) ?([kKmMgGtTpPbB]?)(?:[iI]?[bB])?$")]
    private static partial Regex SizePattern();

    [GeneratedRegex("([0-9]+(?:\\.[0-9]+)?)(ns|us|µs|ms|s|m|h)")]
    private static partial Regex DurationPart();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex VolumeNamePattern();

    [GeneratedRegex("^(?:sha256:)?[a-f0-9]{12,64}$")]
    private static partial Regex LocalId();

    /// <summary>What the options set, as they're read; <see cref="Build"/> makes the settings.</summary>
    private sealed class Builder
    {
        public readonly List<ParseIssue> Issues = [];
        public string? Name;
        public readonly List<EnvSetting> Env = [];
        public readonly List<PortRule> Ports = [];
        public bool PublishAll;
        public readonly List<MountSetting> Mounts = [];
        public string NetworkMode = "";
        public readonly List<(string Name, List<string> Aliases, string? Ip, string Fragment)> Networks = [];
        public readonly List<string> PendingAliases = [];
        public (string Ip, string Fragment)? PendingIp;
        public string? LanAddress;
        public string? Hostname, Domainname, WorkingDir, User, StopSignal, LogDriver, Gpus, Platform, CpusetCpus, HealthCmd;
        public readonly List<string> ExtraHosts = [], Dns = [], DnsSearch = [], DnsOptions = [], CapAdd = [], CapDrop = [], SecurityOpt = [];
        public RestartSetting? Restart;
        public long? Memory, MemoryReservation, MemorySwap, CpuShares, CpuQuota, CpuPeriod, PidsLimit, ShmSize;
        public double? Cpus;
        public readonly List<UlimitSetting> Ulimits = [];
        public TimeSpan? HealthInterval, HealthTimeout, HealthStartPeriod, HealthStartInterval;
        public int? HealthRetries, StopTimeout;
        public bool AnyHealth, NoHealthcheck, Privileged, ReadOnlyRootfs, Init, Tty, OpenStdin, PullFirst;
        public readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal), Sysctls = new(StringComparer.Ordinal), LogOptions = new(StringComparer.Ordinal);
        public readonly List<DeviceSetting> Devices = [];
        public IReadOnlyList<string>? Entrypoint;

        public T Fail<T>(string text, string fragment, T keep)
        {
            Issues.Add(Error(text, fragment));
            return keep;
        }

        public long? Bytes(string value, string fragment) =>
            ParseBytes(value) ?? Fail<long?>($"{fragment}: give a size such as 512m or 2g.", fragment, null);

        public long? Whole(string value, string fragment) =>
            long.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : Fail<long?>($"{fragment}: give a whole number.", fragment, null);

        public TimeSpan? Duration(string value, string fragment) =>
            ParseDuration(value) ?? Fail<TimeSpan?>($"{fragment}: give a time such as 30s or 1m30s.", fragment, null);

        public void SetName(string value, string fragment)
        {
            var slug = AppService.Slugify(value);
            if (slug.Length == 0)
            {
                Issues.Add(Error($"{fragment}: the name needs a letter or a digit.", fragment));
                return;
            }
            Name = value.Trim().TrimStart('/');
            Issues.Add(Note($"Gitberth names the container gb-{slug}; other containers reach it as {slug}.", fragment));
        }

        public void AddEnv(string value, string fragment)
        {
            var eq = value.IndexOf('=');
            var key = eq < 0 ? value : value[..eq];
            if (key.Length == 0)
            {
                Issues.Add(Error($"{fragment}: a variable is KEY=value.", fragment));
                return;
            }
            var v = eq < 0 ? "" : value[(eq + 1)..];
            if (eq < 0) Issues.Add(Error($"Give {key} a value: docker run would take it from your shell, which Gitberth can't see.", fragment));
            Env.RemoveAll(e => e.Key == key);
            Env.Add(new EnvSetting(key, v, Secret: Security.Masking.IsSecret(key, v)));
        }

        public void AddMount(MountSetting? mount)
        {
            if (mount is null) return;
            Mounts.RemoveAll(m => m.Target == mount.Target);
            Mounts.Add(mount);
        }

        public void AddNetwork(string value, string fragment)
        {
            var name = value.Trim();
            string? alias = null, ip = null;
            // Docker 25's long form: name=x,alias=y,ip=z.
            if (name.Contains('='))
            {
                name = "";
                foreach (var field in Csv(value))
                {
                    var eq = field.IndexOf('=');
                    var key = eq < 0 ? field : field[..eq];
                    var v = eq < 0 ? "" : field[(eq + 1)..];
                    switch (key.Trim())
                    {
                        case "name": name = v.Trim(); break;
                        case "alias": alias = v.Trim(); break;
                        case "ip": ip = v.Trim(); break;
                        default: Issues.Add(Drop($"the network option {key.Trim()}: Docker's defaults apply.", fragment)); break;
                    }
                }
            }
            switch (name)
            {
                case "":
                    Issues.Add(Error($"{fragment}: name the network.", fragment));
                    return;
                case "host" or "none":
                    NetworkMode = name;
                    return;
                case "bridge" or "default":
                    Issues.Add(Note($"{fragment}: Gitberth puts it on its shared network, where other containers reach it by name, instead of Docker's default bridge.", fragment));
                    return;
            }
            if (name.StartsWith("container:", StringComparison.Ordinal))
            {
                NetworkMode = name;
                return;
            }
            if (ip is not null && !Ipv4(ip))
            {
                Issues.Add(Error($"{fragment}: ip= is an IPv4 address.", fragment));
                ip = null;
            }
            Networks.Add((name, alias is null ? [] : [alias], ip, fragment));
        }

        // docker run gives --network-alias and --ip to the first --network, wherever they're written.
        public void AddAlias(string value, string fragment) => PendingAliases.Add(value.Trim());

        public void SetIp(string value, string fragment)
        {
            if (!Ipv4(value))
            {
                Issues.Add(Error($"{fragment}: --ip is an IPv4 address.", fragment));
                return;
            }
            PendingIp = (value.Trim(), fragment);
        }

        private static bool Ipv4(string value) =>
            IPAddress.TryParse(value.Trim(), out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        public void AddHost(string value, string fragment)
        {
            // host:ip, or host=ip (Docker 25), the address possibly IPv6.
            var at = value.IndexOf('=');
            if (at < 0) at = value.IndexOf(':');
            var host = at < 0 ? "" : value[..at].Trim();
            var ip = at < 0 ? "" : value[(at + 1)..].Trim().Trim('[', ']');
            if (host.Length == 0 || (ip != "host-gateway" && !IPAddress.TryParse(ip, out _)))
            {
                Issues.Add(Error($"{fragment}: --add-host is name:address, such as db:10.1.1.20.", fragment));
                return;
            }
            ExtraHosts.Add($"{host}:{ip}");
        }

        public void AddUlimit(string value, string fragment)
        {
            if (Ulimit(value) is { } ulimit) Ulimits.Add(ulimit);
            else Issues.Add(Error($"{fragment}: --ulimit is name=soft[:hard], such as nofile=1024:2048.", fragment));
        }

        public void AddLabel(string value, string fragment)
        {
            var eq = value.IndexOf('=');
            var key = (eq < 0 ? value : value[..eq]).Trim();
            if (key.StartsWith("gitberth.", StringComparison.Ordinal))
            {
                Issues.Add(Error($"Labels starting with gitberth. are Gitberth's own: remove {key}.", fragment));
                return;
            }
            if (key.Length == 0)
            {
                Issues.Add(Error($"{fragment}: a label is key=value.", fragment));
                return;
            }
            Labels[key] = eq < 0 ? "" : value[(eq + 1)..];
        }

        public void AddDevice(string value, string fragment)
        {
            if (Device(value) is { } device) Devices.Add(device);
            else if (value.Contains('=')) Issues.Add(Drop($"{fragment}: a CDI device name isn't kept; give the device's path, such as /dev/dri.", fragment));
            else Issues.Add(Error($"{fragment}: --device is /dev/host[:/dev/container[:rwm]].", fragment));
        }

        public void AddPair(Dictionary<string, string> into, string value, string fragment, string what)
        {
            var eq = value.IndexOf('=');
            if (eq <= 0)
            {
                Issues.Add(Error($"{fragment}: give {what}.", fragment));
                return;
            }
            into[value[..eq].Trim()] = value[(eq + 1)..].Trim();
        }

        public void SetPull(string value, string fragment)
        {
            switch (value.Trim())
            {
                case "always":
                    PullFirst = true;
                    break;
                case "missing":
                    break;
                case "never":
                    Issues.Add(Note($"{fragment}: Gitberth pulls the image when it isn't on the host, so the app can be created.", fragment));
                    break;
                default:
                    Issues.Add(Error($"{fragment}: --pull is always, missing or never.", fragment));
                    break;
            }
        }

        public ParsedApp Build(string? image, IReadOnlyList<string> command)
        {
            var slug = AppService.Slugify(Name ?? (ImageReference.Parse(image) is { } reference ? reference.ShortName : "app"));
            if (slug.Length == 0) slug = "app";

            // The aliases and address go to the first network named; with none, an alias is one on Gitberth's shared network.
            if (Networks.Count > 0)
            {
                Networks[0].Aliases.AddRange(PendingAliases);
                if (PendingIp is { } ip) Networks[0] = Networks[0] with { Ip = ip.Ip };
            }
            else
            {
                if (PendingAliases.Count > 0) Networks.Add(("", PendingAliases.ToList(), null, "--network-alias"));
                if (PendingIp is { } ip)
                    Issues.Add(Drop($"{ip.Fragment}: an address of its own needs a network of your own; on Gitberth's shared network Docker picks it.", ip.Fragment));
            }
            // gitberth-lan is the own LAN address, set with the address pinned.
            foreach (var lan in Networks.Where(n => n.Name == LanAddresses.Network).ToList())
            {
                if (lan.Ip is { } address) LanAddress = address;
                else Issues.Add(Drop($"{LanAddresses.Network} without --ip: give it its own LAN address on its LAN address card once it's created.", lan.Fragment));
                Networks.Remove(lan);
            }

            // -p with the host's network: Docker discards them too.
            var ports = Ports.ToList();
            if (NetworkMode is "host" or "none" || NetworkMode.StartsWith("container:", StringComparison.Ordinal))
            {
                if (ports.Any(p => p.Published) || PublishAll)
                    Issues.Add(Drop(NetworkMode == "host"
                        ? "the published ports: on the host's network it listens on the host's own ports directly."
                        : $"the published ports: with --network {NetworkMode} it has no ports of its own.", "-p"));
                ports = [];
                PublishAll = false;
                if (Networks.Count > 0 || LanAddress is not null)
                    Issues.Add(Drop($"its other networks and addresses: with --network {NetworkMode} it joins no network.", "--network"));
                Networks.Clear();
                LanAddress = null;
            }
            // Two bindings of one port and host port on 0.0.0.0 and :: are one LAN binding.
            ports = ports.DistinctBy(p => (p.ContainerPort, p.Protocol, p.Access, p.HostPort, p.HostIp)).ToList();

            if (Restart is null)
                Issues.Add(Note("No --restart: Gitberth uses unless-stopped, so it comes back after a restart of the host.", null));

            if (CpuQuota is { } quota)
            {
                if (Cpus is not null) Issues.Add(Drop("--cpu-quota: --cpus says the same, and is the one kept.", "--cpu-quota"));
                else if (quota > 0 && (CpuPeriod ?? 100_000) > 0)
                {
                    var period = CpuPeriod ?? 100_000;
                    Cpus = Math.Round((double)quota / period, 3);
                    Issues.Add(Note($"--cpu-quota {quota} with a period of {period} is {Cpus.Value.ToString("0.###", CultureInfo.InvariantCulture)} CPUs: Gitberth sets --cpus.", "--cpu-quota"));
                }
            }
            else if (CpuPeriod is not null)
            {
                Issues.Add(Drop("--cpu-period without --cpu-quota: it limits nothing on its own.", "--cpu-period"));
            }

            var health = new HealthSetting();
            if (NoHealthcheck && AnyHealth)
                Issues.Add(Error("--no-healthcheck can't go with the --health-* options: keep one or the other.", "--no-healthcheck"));
            else if (NoHealthcheck)
                health = new HealthSetting(HealthMode.Disabled);
            else if (AnyHealth)
            {
                if (HealthCmd is null) Issues.Add(Note("--health-* without --health-cmd keeps the image's own check, with these timings.", "--health-interval"));
                health = new HealthSetting(HealthMode.Custom, HealthCmd is null ? null : ["CMD-SHELL", HealthCmd], HealthInterval, HealthTimeout, HealthRetries,
                    HealthStartPeriod, HealthStartInterval);
            }

            var mounts = Mounts.Select(m => m.Kind == MountKind.Volume && m.Source.Length == 0 ? m with { Source = $"gb-{slug}-{Segment(m.Target)}" } : m).ToList();
            var settings = new ContainerSettings
            {
                Image = image ?? "",
                Entrypoint = Entrypoint,
                Command = command.Count > 0 ? command : null,
                WorkingDir = WorkingDir,
                User = User,
                Env = Env,
                Ports = ports,
                Mounts = mounts,
                NetworkMode = NetworkMode,
                Hostname = Hostname,
                Domainname = Domainname,
                ExtraHosts = ExtraHosts,
                Dns = Dns,
                DnsSearch = DnsSearch,
                DnsOptions = DnsOptions,
                Restart = Restart ?? new RestartSetting(),
                Resources = new ResourceSetting(Memory, MemoryReservation, MemorySwap, Cpus, CpuShares, CpusetCpus, PidsLimit),
                ShmSizeBytes = ShmSize,
                Ulimits = Ulimits,
                Health = health,
                Labels = Labels,
                Devices = Devices,
                Gpus = Gpus,
                CapAdd = CapAdd,
                CapDrop = CapDrop,
                Privileged = Privileged,
                SecurityOpt = SecurityOpt,
                ReadOnlyRootfs = ReadOnlyRootfs,
                Init = Init,
                Sysctls = Sysctls,
                Logging = new LogSetting(LogDriver ?? "", LogOptions.Count > 0 ? LogOptions : null),
                StopSignal = StopSignal,
                StopTimeoutSeconds = StopTimeout,
                Tty = Tty,
                OpenStdin = OpenStdin,
            };
            var networks = Networks.Select(n => new NetworkJoin(n.Name, n.Aliases, n.Ip, LowPriority: true)).ToList();
            return new ParsedApp(Name, settings, networks, LanAddress, Issues, Platform, PullFirst) { PublishAll = PublishAll };
        }
    }
}
