using System.Globalization;
using Gitberth.Apps;
using Gitberth.Detection;
using Gitberth.Security;
using Gitberth.Services;

namespace Gitberth.Network;

/// <summary>
/// What differs between two sets of a container's settings, field by field and by content (the records compare lists by
/// reference), what a change makes of the settings the container has now, and the CHANGE lines and summary that say so
/// with every secret masked. Pure.
/// </summary>
public static class SettingsDiff
{
    /// <summary>How many items of a list a line shows before "and 3 more".</summary>
    private const int ListMost = 5;

    public static IReadOnlySet<SettingField> Changed(ContainerSettings before, ContainerSettings after) =>
        Enum.GetValues<SettingField>().Where(f => !Same(f, before, after)).ToHashSet();

    /// <summary>
    /// Live with <paramref name="fields"/> taken from wanted: what a change makes it. The rest stays as the container has
    /// it now, so a docker update made outside Gitberth since the page loaded survives a change to the environment.
    /// </summary>
    public static ContainerSettings Merge(ContainerSettings live, ContainerSettings wanted, IReadOnlySet<SettingField> fields)
    {
        var s = live;
        foreach (var field in fields)
            s = field switch
            {
                SettingField.Image => s with { Image = wanted.Image },
                SettingField.Entrypoint => s with { Entrypoint = wanted.Entrypoint },
                SettingField.Command => s with { Command = wanted.Command },
                SettingField.WorkingDir => s with { WorkingDir = wanted.WorkingDir },
                SettingField.User => s with { User = wanted.User },
                SettingField.Env => s with { Env = wanted.Env },
                SettingField.Ports => s with { Ports = wanted.Ports },
                SettingField.Mounts => s with { Mounts = wanted.Mounts },
                SettingField.NetworkMode => s with { NetworkMode = wanted.NetworkMode },
                SettingField.Hostname => s with { Hostname = wanted.Hostname, Domainname = wanted.Domainname },
                SettingField.ExtraHosts => s with { ExtraHosts = wanted.ExtraHosts },
                SettingField.Dns => s with { Dns = wanted.Dns, DnsSearch = wanted.DnsSearch, DnsOptions = wanted.DnsOptions },
                SettingField.RestartPolicy => s with { Restart = wanted.Restart },
                SettingField.Memory => s with
                {
                    Resources = s.Resources with
                    {
                        MemoryBytes = wanted.Resources.MemoryBytes,
                        MemoryReservationBytes = wanted.Resources.MemoryReservationBytes,
                        MemorySwapBytes = wanted.Resources.MemorySwapBytes,
                    },
                },
                SettingField.Cpu => s with
                {
                    Resources = s.Resources with { Cpus = wanted.Resources.Cpus, CpuShares = wanted.Resources.CpuShares, CpusetCpus = wanted.Resources.CpusetCpus },
                },
                SettingField.PidsLimit => s with { Resources = s.Resources with { PidsLimit = wanted.Resources.PidsLimit } },
                SettingField.ShmSize => s with { ShmSizeBytes = wanted.ShmSizeBytes },
                SettingField.Ulimits => s with { Ulimits = wanted.Ulimits },
                SettingField.Health => s with { Health = wanted.Health },
                SettingField.Labels => s with { Labels = wanted.Labels },
                SettingField.Devices => s with { Devices = wanted.Devices },
                SettingField.Gpus => s with { Gpus = wanted.Gpus },
                SettingField.Capabilities => s with { CapAdd = wanted.CapAdd, CapDrop = wanted.CapDrop },
                SettingField.Privileged => s with { Privileged = wanted.Privileged },
                SettingField.SecurityOpt => s with { SecurityOpt = wanted.SecurityOpt },
                SettingField.ReadOnlyRootfs => s with { ReadOnlyRootfs = wanted.ReadOnlyRootfs },
                SettingField.Init => s with { Init = wanted.Init },
                SettingField.Sysctls => s with { Sysctls = wanted.Sysctls },
                SettingField.Logging => s with { Logging = wanted.Logging },
                SettingField.Stop => s with { StopSignal = wanted.StopSignal, StopTimeoutSeconds = wanted.StopTimeoutSeconds },
                SettingField.Terminal => s with { Tty = wanted.Tty, OpenStdin = wanted.OpenStdin },
                _ => s,
            };
        return s;
    }

    /// <summary>
    /// The CHANGE lines, one per field (one per variable for the environment), values masked: by key (the secret keys and
    /// Gitberth's guess), by a URL's password, and by option for the command and entrypoint (--token, TUNNEL_TOKEN=…).
    /// </summary>
    public static IReadOnlyList<SettingLine> Lines(ContainerSettings before, ContainerSettings after, IReadOnlySet<SettingField> fields,
        IReadOnlyCollection<string> secretKeys, Func<SettingField, ApplyKind> how)
    {
        var lines = new List<SettingLine>();
        foreach (var field in Enum.GetValues<SettingField>().Where(fields.Contains))
        {
            var kind = how(field);
            if (field == SettingField.Env)
            {
                lines.AddRange(EnvLines(before.Env, after.Env, secretKeys, kind));
                continue;
            }
            // The entrypoint and command are one line each, and only when that one differs.
            if (field is SettingField.Entrypoint or SettingField.Command && Same(field, before, after)) continue;
            lines.Add(new SettingLine(field, Label(field), Text(field, before, secretKeys), Text(field, after, secretKeys), kind));
        }
        return lines;
    }

    /// <summary>Names only: "changed DATABASE_URL and the memory limit", "4 setting changes".</summary>
    public static string Summary(ContainerSettings before, ContainerSettings after, IReadOnlySet<SettingField> fields)
    {
        var parts = new List<string>();
        foreach (var field in Enum.GetValues<SettingField>().Where(fields.Contains))
        {
            if (field == SettingField.Env)
            {
                var (added, removed, changed) = EnvChanges(before.Env, after.Env);
                parts.AddRange(changed.Concat(added).Concat(removed).Order(StringComparer.Ordinal));
            }
            else if (!(field is SettingField.Entrypoint or SettingField.Command && Same(field, before, after))) parts.Add(Noun(field));
        }
        return parts.Count == 0 ? "no setting changes"
            : parts.Count <= 3 ? "changed " + RecreateRules.Join(parts)
            : $"{parts.Count.ToString(CultureInfo.InvariantCulture)} setting changes";
    }

    // ---- Comparing ----

    private static bool Same(SettingField field, ContainerSettings a, ContainerSettings b) => field switch
    {
        SettingField.Image => a.Image == b.Image,
        SettingField.Entrypoint => SameOptional(a.Entrypoint, b.Entrypoint),
        SettingField.Command => SameOptional(a.Command, b.Command),
        SettingField.WorkingDir => (a.WorkingDir ?? "") == (b.WorkingDir ?? ""),
        SettingField.User => (a.User ?? "") == (b.User ?? ""),
        SettingField.Env => EnvChanges(a.Env, b.Env) is { Added.Count: 0, Removed.Count: 0, Changed.Count: 0 },
        SettingField.Ports => PortsKey(a.Ports) == PortsKey(b.Ports),
        SettingField.Mounts => MountsKey(a.Mounts) == MountsKey(b.Mounts),
        SettingField.NetworkMode => a.NetworkMode == b.NetworkMode,
        SettingField.Hostname => (a.Hostname ?? "") == (b.Hostname ?? "") && (a.Domainname ?? "") == (b.Domainname ?? ""),
        SettingField.ExtraHosts => a.ExtraHosts.SequenceEqual(b.ExtraHosts),
        SettingField.Dns => a.Dns.SequenceEqual(b.Dns) && a.DnsSearch.SequenceEqual(b.DnsSearch) && a.DnsOptions.SequenceEqual(b.DnsOptions),
        SettingField.RestartPolicy => a.Restart.Name == b.Restart.Name && (a.Restart.Name != "on-failure" || a.Restart.MaximumRetryCount == b.Restart.MaximumRetryCount),
        SettingField.Memory => a.Resources.MemoryBytes == b.Resources.MemoryBytes && a.Resources.MemoryReservationBytes == b.Resources.MemoryReservationBytes
            && a.Resources.MemorySwapBytes == b.Resources.MemorySwapBytes,
        SettingField.Cpu => SameCpus(a.Resources.Cpus, b.Resources.Cpus) && a.Resources.CpuShares == b.Resources.CpuShares
            && (a.Resources.CpusetCpus ?? "") == (b.Resources.CpusetCpus ?? ""),
        SettingField.PidsLimit => a.Resources.PidsLimit == b.Resources.PidsLimit,
        SettingField.ShmSize => a.ShmSizeBytes == b.ShmSizeBytes,
        SettingField.Ulimits => Key(a.Ulimits.Select(u => $"{u.Name}={u.Soft}:{u.Hard}"), sort: true) == Key(b.Ulimits.Select(u => $"{u.Name}={u.Soft}:{u.Hard}"), sort: true),
        SettingField.Health => HealthKey(a.Health) == HealthKey(b.Health),
        SettingField.Labels => Key(a.Labels) == Key(b.Labels),
        SettingField.Devices => Key(a.Devices.Select(d => $"{d.HostPath}:{d.ContainerPath}:{d.Permissions}"), sort: false)
            == Key(b.Devices.Select(d => $"{d.HostPath}:{d.ContainerPath}:{d.Permissions}"), sort: false),
        SettingField.Gpus => (a.Gpus ?? "") == (b.Gpus ?? ""),
        SettingField.Capabilities => Key(a.CapAdd, sort: true) == Key(b.CapAdd, sort: true) && Key(a.CapDrop, sort: true) == Key(b.CapDrop, sort: true),
        SettingField.Privileged => a.Privileged == b.Privileged,
        SettingField.SecurityOpt => a.SecurityOpt.SequenceEqual(b.SecurityOpt),
        SettingField.ReadOnlyRootfs => a.ReadOnlyRootfs == b.ReadOnlyRootfs,
        SettingField.Init => a.Init == b.Init,
        SettingField.Sysctls => Key(a.Sysctls) == Key(b.Sysctls),
        SettingField.Logging => a.Logging.Driver == b.Logging.Driver
            && Key(a.Logging.Options ?? new Dictionary<string, string>()) == Key(b.Logging.Options ?? new Dictionary<string, string>()),
        SettingField.Stop => (a.StopSignal ?? "") == (b.StopSignal ?? "") && a.StopTimeoutSeconds == b.StopTimeoutSeconds,
        SettingField.Terminal => a.Tty == b.Tty && a.OpenStdin == b.OpenStdin,
        _ => true,
    };

    private static bool SameOptional(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null) == (b is null) && (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);

    private static bool SameCpus(double? a, double? b) => (a, b) switch
    {
        (null, null) => true,
        ({ } x, { } y) => Math.Abs(x - y) < 1e-9,
        _ => false,
    };

    /// <summary>
    /// The ports as Docker publishes them: each published rule by its port, access, host port, and address when it's
    /// Custom (a LAN rule's 0.0.0.0 or "" is the same port), and each unpublished one by its port.
    /// </summary>
    internal static string PortsKey(IEnumerable<PortRule> rules) =>
        Key(rules.Select(r => r.Published
            ? $"{r.ContainerPort}/{r.Protocol.ToLowerInvariant()}:{r.Access}:{r.HostPort}:{(r.Access == PortAccess.Custom ? r.HostIp : "")}"
            : $"{r.ContainerPort}/{r.Protocol.ToLowerInvariant()}"), sort: true);

    private static string MountsKey(IEnumerable<MountSetting> mounts) =>
        Key(mounts.Select(m => $"{m.Kind}:{m.Source}:{CloneSpec.Clean(m.Target)}:{m.ReadOnly}:{m.TmpfsBytes}"), sort: true);

    private static string HealthKey(HealthSetting h) => h.Mode != HealthMode.Custom ? h.Mode.ToString()
        : $"{string.Join('\u0001', h.Test ?? [])}|{h.Interval}|{h.Timeout}|{h.Retries}|{h.StartPeriod}|{h.StartInterval}";

    private static string Key(IEnumerable<string> items, bool sort) =>
        string.Join('\u0000', sort ? items.Order(StringComparer.Ordinal) : items);

    private static string Key(IReadOnlyDictionary<string, string> map) =>
        Key(map.Select(kv => kv.Key + "=" + kv.Value), sort: true);

    // The variables by key: the last of a key counts, as Docker reads them.
    private static (List<string> Added, List<string> Removed, List<string> Changed) EnvChanges(IReadOnlyList<EnvSetting> before, IReadOnlyList<EnvSetting> after)
    {
        var a = Values(before);
        var b = Values(after);
        return (b.Keys.Where(k => !a.ContainsKey(k)).ToList(), a.Keys.Where(k => !b.ContainsKey(k)).ToList(),
            b.Keys.Where(k => a.TryGetValue(k, out var v) && v != b[k]).ToList());
    }

    private static Dictionary<string, string> Values(IReadOnlyList<EnvSetting> env)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in env) values[e.Key] = e.Value;
        return values;
    }

    // ---- Lines ----

    private static IEnumerable<SettingLine> EnvLines(IReadOnlyList<EnvSetting> before, IReadOnlyList<EnvSetting> after, IReadOnlyCollection<string> secretKeys, ApplyKind kind)
    {
        var a = Values(before);
        var b = Values(after);
        var (added, removed, changed) = EnvChanges(before, after);
        var lines = changed.Select(k => new SettingLine(SettingField.Env, k, Masking.Value(k, a[k], secretKeys), Masking.Value(k, b[k], secretKeys), kind))
            .Concat(added.Select(k => new SettingLine(SettingField.Env, "Added " + k, "", Masking.Value(k, b[k], secretKeys), kind)))
            .Concat(removed.Select(k => new SettingLine(SettingField.Env, "Removed " + k, Masking.Value(k, a[k], secretKeys), "", kind)))
            .ToList();
        if (lines.Count <= ListMost) return lines;
        var more = lines.Count - ListMost;
        return lines.Take(ListMost).Append(new SettingLine(SettingField.Env, $"and {more.ToString(CultureInfo.InvariantCulture)} more {(more == 1 ? "variable" : "variables")}", "", "", kind));
    }

    private static string Label(SettingField field) => field switch
    {
        SettingField.Image => "Image",
        SettingField.Entrypoint => "Entrypoint",
        SettingField.Command => "Command",
        SettingField.WorkingDir => "Working directory",
        SettingField.User => "User",
        SettingField.Ports => "Ports",
        SettingField.Mounts => "Storage",
        SettingField.NetworkMode => "Network mode",
        SettingField.Hostname => "Hostname",
        SettingField.ExtraHosts => "Extra hosts",
        SettingField.Dns => "DNS",
        SettingField.RestartPolicy => "Restart policy",
        SettingField.Memory => "Memory limit",
        SettingField.Cpu => "CPU limit",
        SettingField.PidsLimit => "Process limit",
        SettingField.ShmSize => "Shared memory",
        SettingField.Ulimits => "Ulimits",
        SettingField.Health => "Health check",
        SettingField.Labels => "Labels",
        SettingField.Devices => "Devices",
        SettingField.Gpus => "GPUs",
        SettingField.Capabilities => "Capabilities",
        SettingField.Privileged => "Privileged",
        SettingField.SecurityOpt => "Security options",
        SettingField.ReadOnlyRootfs => "Read-only root",
        SettingField.Init => "Init",
        SettingField.Sysctls => "Sysctls",
        SettingField.Logging => "Logging",
        SettingField.Stop => "Stop",
        SettingField.Terminal => "Terminal",
        _ => field.ToString(),
    };

    private static string Noun(SettingField field) => field switch
    {
        SettingField.Image => "the image",
        SettingField.Entrypoint => "the entrypoint",
        SettingField.Command => "the command",
        SettingField.WorkingDir => "the working directory",
        SettingField.User => "the user",
        SettingField.Ports => "the ports",
        SettingField.Mounts => "the storage",
        SettingField.NetworkMode => "the network mode",
        SettingField.Hostname => "the hostname",
        SettingField.ExtraHosts => "the extra hosts",
        SettingField.Dns => "the DNS settings",
        SettingField.RestartPolicy => "the restart policy",
        SettingField.Memory => "the memory limit",
        SettingField.Cpu => "the CPU limit",
        SettingField.PidsLimit => "the process limit",
        SettingField.ShmSize => "the shared memory",
        SettingField.Ulimits => "the ulimits",
        SettingField.Health => "the health check",
        SettingField.Labels => "the labels",
        SettingField.Devices => "the devices",
        SettingField.Gpus => "the GPUs",
        SettingField.Capabilities => "the capabilities",
        SettingField.Privileged => "privileged mode",
        SettingField.SecurityOpt => "the security options",
        SettingField.ReadOnlyRootfs => "the read-only root",
        SettingField.Init => "init",
        SettingField.Sysctls => "the sysctls",
        SettingField.Logging => "the logging",
        SettingField.Stop => "how it stops",
        SettingField.Terminal => "the terminal",
        _ => field.ToString(),
    };

    // A field's value in a line's words, secrets masked.
    private static string Text(SettingField field, ContainerSettings s, IReadOnlyCollection<string> secretKeys) => field switch
    {
        SettingField.Image => s.Image,
        SettingField.Entrypoint => s.Entrypoint is null ? "the image's" : s.Entrypoint.Count == 0 ? "none" : string.Join(' ', Masking.Args(s.Entrypoint, secretKeys)),
        SettingField.Command => s.Command is null ? "the image's" : s.Command.Count == 0 ? "none" : string.Join(' ', Masking.Args(s.Command, secretKeys)),
        SettingField.WorkingDir => s.WorkingDir ?? "the image's",
        SettingField.User => s.User ?? "the image's",
        SettingField.Ports => List(s.Ports.Select(r => r.Published
            ? $"{r.Key} {(r.Access switch { PortAccess.Lan => "LAN", PortAccess.HostOnly => "Host only", _ => r.HostIp })} {(r.HostPort is { } p ? p.ToString(CultureInfo.InvariantCulture) : "random")}"
            : $"{r.Key} Internal")),
        SettingField.Mounts => List(s.Mounts.Select(m => m.Kind switch
        {
            MountKind.Tmpfs => $"tmpfs {m.Target}{(m.TmpfsBytes is { } size ? $" ({Size(size)})" : "")}",
            _ => $"{(m.Anonymous ? "anonymous volume " + m.Source[..12] : m.Source)} → {m.Target}{(m.ReadOnly ? " (read-only)" : "")}",
        })),
        SettingField.NetworkMode => s.NetworkMode.Length == 0 ? "bridge" : s.NetworkMode,
        SettingField.Hostname => (s.Hostname ?? "Docker's default") + (s.Domainname is { } domain ? "." + domain : ""),
        SettingField.ExtraHosts => List(s.ExtraHosts),
        SettingField.Dns => List(s.Dns.Concat(s.DnsSearch.Select(d => "search " + d)).Concat(s.DnsOptions.Select(o => "option " + o))),
        // In the words the forms offer: Never, On failure, Unless stopped, Always.
        SettingField.RestartPolicy => s.Restart.Name == "on-failure" && s.Restart.MaximumRetryCount > 0
            ? $"On failure, {s.Restart.MaximumRetryCount.ToString(CultureInfo.InvariantCulture)} tries" : RestartWords(s.Restart.Name),
        SettingField.Memory => (s.Resources.MemoryBytes is { } memory ? Size(memory) : "none")
            + (s.Resources.MemoryReservationBytes is { } reservation ? $", reserves {Size(reservation)}" : "")
            + (s.Resources.MemorySwapBytes is { } swap ? swap < 0 ? ", swap unlimited" : $", swap {Size(swap)}" : ""),
        SettingField.Cpu => (s.Resources.Cpus is { } cpus ? cpus.ToString("0.###", CultureInfo.InvariantCulture) + (cpus == 1 ? " CPU" : " CPUs") : "none")
            + (s.Resources.CpuShares is { } shares ? $", shares {shares.ToString(CultureInfo.InvariantCulture)}" : "")
            + (s.Resources.CpusetCpus is { } set ? $", CPUs {set}" : ""),
        SettingField.PidsLimit => s.Resources.PidsLimit is { } pids ? pids.ToString(CultureInfo.InvariantCulture) : "none",
        SettingField.ShmSize => s.ShmSizeBytes is { } shm ? Size(shm) : "64 MiB (Docker's)",
        SettingField.Ulimits => List(s.Ulimits.Select(u => $"{u.Name} {u.Soft.ToString(CultureInfo.InvariantCulture)}:{u.Hard.ToString(CultureInfo.InvariantCulture)}")),
        // As the check's choices name them: Image default, a command of its own, None.
        SettingField.Health => s.Health.Mode switch
        {
            HealthMode.Disabled => "None",
            HealthMode.Custom => string.Join(' ', Masking.Args((s.Health.Test ?? []).Skip(1).ToList(), secretKeys))
                + (s.Health.Interval is { } every ? $" every {every.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s" : ""),
            _ => "Image default",
        },
        SettingField.Labels => List(s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={Masking.Value(l.Key, l.Value, secretKeys)}")),
        SettingField.Devices => List(s.Devices.Select(d => $"{d.HostPath} → {d.ContainerPath}")),
        SettingField.Gpus => s.Gpus ?? "none",
        SettingField.Capabilities => List(s.CapAdd.Select(c => "+" + c).Concat(s.CapDrop.Select(c => "-" + c))),
        SettingField.Privileged => s.Privileged ? "on" : "off",
        SettingField.SecurityOpt => List(s.SecurityOpt),
        SettingField.ReadOnlyRootfs => s.ReadOnlyRootfs ? "on" : "off",
        SettingField.Init => s.Init ? "on" : "off",
        SettingField.Sysctls => List(s.Sysctls.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")),
        SettingField.Logging => (s.Logging.Driver.Length == 0 ? "the daemon's" : s.Logging.Driver)
            + ((s.Logging.Options ?? new Dictionary<string, string>()).Count == 0 ? ""
                : " (" + string.Join(", ", s.Logging.Options!.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={Masking.Value(o.Key, o.Value, secretKeys)}")) + ")"),
        SettingField.Stop => (s.StopSignal ?? "the image's signal")
            + (s.StopTimeoutSeconds is { } t ? $", waits {t.ToString(CultureInfo.InvariantCulture)} s" : ""),
        SettingField.Terminal => s.Tty && s.OpenStdin ? "TTY, stdin open" : s.Tty ? "TTY" : s.OpenStdin ? "stdin open" : "none",
        _ => "",
    };

    /// <summary>Docker's restart policy as every form offers it: Never, On failure, Unless stopped, Always.</summary>
    internal static string RestartWords(string? policy) => policy switch
    {
        "always" => "Always",
        "unless-stopped" => "Unless stopped",
        "on-failure" => "On failure",
        _ => "Never",
    };

    // "a, b, c, d, e and 3 more"; "none" for an empty list.
    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        if (all.Count == 0) return "none";
        if (all.Count <= ListMost) return RecreateRules.Join(all);
        return string.Join(", ", all.Take(ListMost)) + $" and {(all.Count - ListMost).ToString(CultureInfo.InvariantCulture)} more";
    }

    /// <summary>512 MiB, 1 GiB, 1.5 GiB.</summary>
    internal static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB"
        : bytes >= 1L << 10 ? (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KiB"
        : bytes.ToString(CultureInfo.InvariantCulture) + " B";
}
