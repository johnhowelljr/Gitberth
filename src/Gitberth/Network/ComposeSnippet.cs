using System.Globalization;
using System.Text;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Security;

namespace Gitberth.Network;

/// <summary>
/// The Compose YAML for a change to a container Compose manages (it generalizes <see cref="ComposeService.Snippet"/>):
/// exactly the keys of the fields that change, under the service, as Compose's file writes them. A secret variable's
/// value is written as ${KEY}, with a line saying to put it in the project's .env, and any other secret (in the command, a
/// label, a log option, the health check) shows masked, with a line saying to keep the file's own, so the snippet never
/// holds one. Pure.
/// </summary>
public static class ComposeSnippet
{
    public static string For(ComposeService compose, ContainerSettings target, IReadOnlySet<SettingField> fields, IReadOnlyCollection<string> secretKeys)
    {
        var s = target;
        var body = new StringBuilder();
        var secrets = new List<string>();
        // A secret in the command, a label, a log option or the health check is hidden: the file keeps its own value there.
        var hidden = false;
        IReadOnlyList<string> Words(IReadOnlyList<string> words)
        {
            var masked = Masking.Args(words, secretKeys);
            hidden |= !masked.SequenceEqual(words, StringComparer.Ordinal);
            return masked;
        }
        string Hide(string key, string value)
        {
            var masked = Masking.Value(key, value, secretKeys);
            hidden |= masked != value;
            return masked;
        }
        void Line(string text) => body.Append("    ").Append(text).Append('\n');
        void Item(string text) => body.Append("      - ").Append(text).Append('\n');
        void Entry(string key, string value) => body.Append("      ").Append(Key(key)).Append(": ").Append(value).Append('\n');

        foreach (var field in Enum.GetValues<SettingField>().Where(fields.Contains))
            switch (field)
            {
                case SettingField.Image:
                    Line("image: " + Scalar(s.Image));
                    break;
                case SettingField.Entrypoint:
                    Line(s.Entrypoint is null ? "entrypoint: null" : "entrypoint: " + Flow(Words(s.Entrypoint)));
                    break;
                case SettingField.Command:
                    Line(s.Command is null ? "command: null" : "command: " + Flow(Words(s.Command)));
                    break;
                case SettingField.WorkingDir:
                    if (s.WorkingDir is { } dir) Line("working_dir: " + Scalar(dir));
                    break;
                case SettingField.User:
                    if (s.User is { } user) Line("user: " + Scalar(user));
                    break;
                case SettingField.Env:
                    var own = s.Env.Where(e => !e.FromImage).ToList();
                    if (own.Count == 0)
                    {
                        Line("environment: {}");
                        break;
                    }
                    Line("environment:");
                    foreach (var e in own)
                    {
                        var secret = e.Secret || Masking.IsSecret(e.Key, e.Value, secretKeys);
                        if (secret) secrets.Add(e.Key);
                        Entry(e.Key, secret ? "${" + e.Key + "}" : Scalar(e.Value.Replace("$", "$$", StringComparison.Ordinal)));
                    }
                    break;
                case SettingField.Ports:
                    var published = s.Ports.Where(p => p.Published).ToList();
                    if (published.Count == 0) Line("ports: []");
                    else
                    {
                        Line("ports:");
                        foreach (var p in published)
                        {
                            var ip = p.Access == PortAccess.Lan ? (p.HostIp is { Length: > 0 } lan && ContainerPorts.AccessOf(lan) == PortAccess.Lan ? lan : "")
                                : ContainerPorts.BindAddress(p.Access, p.HostIp) ?? "";
                            var address = ip.Length == 0 ? "" : (ip.Contains(':') ? $"[{ip}]" : ip) + ":";
                            Item($"\"{address}{(p.HostPort is { } hp ? hp.ToString(CultureInfo.InvariantCulture) + ":" : "")}{p.ContainerPort.ToString(CultureInfo.InvariantCulture)}{(p.Protocol == "tcp" ? "" : "/" + p.Protocol)}\"");
                        }
                    }
                    var exposed = s.Ports.Where(p => !p.Published).ToList();
                    if (exposed.Count > 0) Line("expose: " + Flow(exposed.Select(p => p.Key).ToList()));
                    break;
                case SettingField.Mounts:
                    var volumes = s.Mounts.Where(m => m.Kind != MountKind.Tmpfs).ToList();
                    Line(volumes.Count == 0 ? "volumes: []" : "volumes:");
                    foreach (var m in volumes) Item(Scalar($"{m.Source}:{m.Target}{(m.ReadOnly ? ":ro" : "")}"));
                    var tmpfs = s.Mounts.Where(m => m.Kind == MountKind.Tmpfs).ToList();
                    if (tmpfs.Count > 0)
                    {
                        Line("tmpfs:");
                        foreach (var m in tmpfs) Item(Scalar(m.Target + (m.TmpfsBytes is { } size ? ":size=" + size.ToString(CultureInfo.InvariantCulture) : "")));
                    }
                    break;
                case SettingField.NetworkMode:
                    if (s.NetworkMode.Length > 0) Line("network_mode: " + Scalar(s.NetworkMode));
                    else Line("network_mode: bridge");
                    break;
                case SettingField.Hostname:
                    if (s.Hostname is { } hostname) Line("hostname: " + Scalar(hostname));
                    if (s.Domainname is { } domain) Line("domainname: " + Scalar(domain));
                    break;
                case SettingField.ExtraHosts:
                    Line("extra_hosts: " + Flow(s.ExtraHosts));
                    break;
                case SettingField.Dns:
                    Line("dns: " + Flow(s.Dns));
                    Line("dns_search: " + Flow(s.DnsSearch));
                    Line("dns_opt: " + Flow(s.DnsOptions));
                    break;
                case SettingField.RestartPolicy:
                    Line("restart: " + (s.Restart.Name == "on-failure" && s.Restart.MaximumRetryCount > 0
                        ? $"\"on-failure:{s.Restart.MaximumRetryCount.ToString(CultureInfo.InvariantCulture)}\"" : Scalar(s.Restart.Name)));
                    break;
                case SettingField.Memory:
                    Line("mem_limit: " + (s.Resources.MemoryBytes is { } memory ? Bytes(memory) : "0"));
                    if (s.Resources.MemoryReservationBytes is { } reservation) Line("mem_reservation: " + Bytes(reservation));
                    if (s.Resources.MemorySwapBytes is { } swap) Line("memswap_limit: " + (swap < 0 ? "-1" : Bytes(swap)));
                    break;
                case SettingField.Cpu:
                    Line("cpus: " + (s.Resources.Cpus is { } cpus ? cpus.ToString("0.###", CultureInfo.InvariantCulture) : "0"));
                    if (s.Resources.CpuShares is { } shares) Line("cpu_shares: " + shares.ToString(CultureInfo.InvariantCulture));
                    if (s.Resources.CpusetCpus is { } cpuset) Line("cpuset: " + Scalar(cpuset));
                    break;
                case SettingField.PidsLimit:
                    Line("pids_limit: " + (s.Resources.PidsLimit ?? -1).ToString(CultureInfo.InvariantCulture));
                    break;
                case SettingField.ShmSize:
                    Line("shm_size: " + (s.ShmSizeBytes is { } shm ? Bytes(shm) : "64m"));
                    break;
                case SettingField.Ulimits:
                    if (s.Ulimits.Count == 0) Line("ulimits: {}");
                    else
                    {
                        Line("ulimits:");
                        foreach (var u in s.Ulimits)
                        {
                            body.Append("      ").Append(Key(u.Name)).Append(":\n");
                            body.Append("        soft: ").Append(u.Soft.ToString(CultureInfo.InvariantCulture)).Append('\n');
                            body.Append("        hard: ").Append(u.Hard.ToString(CultureInfo.InvariantCulture)).Append('\n');
                        }
                    }
                    break;
                case SettingField.Health:
                    switch (s.Health.Mode)
                    {
                        case HealthMode.Disabled:
                            Line("healthcheck:");
                            Line("  disable: true");
                            break;
                        case HealthMode.Custom:
                            Line("healthcheck:");
                            Line("  test: " + Flow(s.Health.Test is { Count: > 0 } test ? [test[0], .. Words(test.Skip(1).ToList())] : []));
                            if (s.Health.Interval is { } interval) Line("  interval: " + Duration(interval));
                            if (s.Health.Timeout is { } timeout) Line("  timeout: " + Duration(timeout));
                            if (s.Health.Retries is { } retries) Line("  retries: " + retries.ToString(CultureInfo.InvariantCulture));
                            if (s.Health.StartPeriod is { } startPeriod) Line("  start_period: " + Duration(startPeriod));
                            if (s.Health.StartInterval is { } startInterval) Line("  start_interval: " + Duration(startInterval));
                            break;
                        default:
                            body.Append("    # healthcheck: remove the service's healthcheck key, so the image's applies\n");
                            break;
                    }
                    break;
                case SettingField.Labels:
                    var labels = s.Labels.Where(l => !l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal)).OrderBy(l => l.Key, StringComparer.Ordinal).ToList();
                    if (labels.Count == 0) Line("labels: {}");
                    else
                    {
                        Line("labels:");
                        foreach (var (key, value) in labels) Entry(key, Scalar(Hide(key, value)));
                    }
                    break;
                case SettingField.Devices:
                    Line("devices: " + Flow(s.Devices.Select(d => $"{d.HostPath}:{d.ContainerPath}{(d.Permissions == "rwm" ? "" : ":" + d.Permissions)}").ToList()));
                    break;
                case SettingField.Gpus:
                    if (s.Gpus is { } gpus) Line("gpus: " + (gpus == "all" ? "all" : Scalar(gpus)));
                    else body.Append("    # gpus: remove the service's gpus key\n");
                    break;
                case SettingField.Capabilities:
                    Line("cap_add: " + Flow(s.CapAdd));
                    Line("cap_drop: " + Flow(s.CapDrop));
                    break;
                case SettingField.Privileged:
                    Line("privileged: " + Bool(s.Privileged));
                    break;
                case SettingField.SecurityOpt:
                    Line("security_opt: " + Flow(s.SecurityOpt));
                    break;
                case SettingField.ReadOnlyRootfs:
                    Line("read_only: " + Bool(s.ReadOnlyRootfs));
                    break;
                case SettingField.Init:
                    Line("init: " + Bool(s.Init));
                    break;
                case SettingField.Sysctls:
                    if (s.Sysctls.Count == 0) Line("sysctls: {}");
                    else
                    {
                        Line("sysctls:");
                        foreach (var (key, value) in s.Sysctls.OrderBy(l => l.Key, StringComparer.Ordinal)) Entry(key, Scalar(value));
                    }
                    break;
                case SettingField.Logging:
                    Line("logging:");
                    if (s.Logging.Driver.Length > 0) Line("  driver: " + Scalar(s.Logging.Driver));
                    var options = s.Logging.Options ?? new Dictionary<string, string>();
                    if (options.Count > 0)
                    {
                        Line("  options:");
                        foreach (var (key, value) in options.OrderBy(o => o.Key, StringComparer.Ordinal)) body.Append("        ").Append(Key(key)).Append(": ").Append(Scalar(Hide(key, value))).Append('\n');
                    }
                    break;
                case SettingField.Stop:
                    if (s.StopSignal is { } signal) Line("stop_signal: " + Scalar(signal));
                    if (s.StopTimeoutSeconds is { } seconds) Line("stop_grace_period: " + (seconds < 0 ? "1000000h" : seconds.ToString(CultureInfo.InvariantCulture) + "s"));
                    break;
                case SettingField.Terminal:
                    Line("tty: " + Bool(s.Tty));
                    Line("stdin_open: " + Bool(s.OpenStdin));
                    break;
            }

        var text = new StringBuilder();
        if (secrets.Count > 0)
            text.Append("# Put ").Append(RecreateRules.Join(secrets)).Append(" in the project's .env")
                .Append(compose.WorkingDir is { } dir ? $" ({dir.TrimEnd('/')}/.env)" : "").Append(": the file reads ").Append(secrets.Count == 1 ? "it" : "them")
                .Append(" from there.\n");
        if (hidden) text.Append("# Values shown as ").Append(Masking.Mask).Append(" are hidden: keep them as your file has them.\n");
        text.Append("services:\n  ").Append(Key(compose.Service ?? "<service>")).Append(":\n").Append(body);
        return text.ToString().TrimEnd('\n');
    }

    private static string Flow(IReadOnlyList<string> items) => "[" + string.Join(", ", items.Select(Scalar)) + "]";

    private static string Bool(bool value) => value ? "true" : "false";

    // A YAML scalar, quoted unless it's plainly safe.
    private static string Scalar(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./@+%,".Contains(c)) && !char.IsAsciiDigit(value[0])
            && value is not ("true" or "false" or "yes" or "no" or "null" or "on" or "off")
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string Key(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? key : "\"" + key.Replace("\"", "\\\"") + "\"";

    private static string Bytes(long bytes) =>
        bytes % (1L << 30) == 0 ? (bytes >> 30).ToString(CultureInfo.InvariantCulture) + "g"
        : bytes % (1L << 20) == 0 ? (bytes >> 20).ToString(CultureInfo.InvariantCulture) + "m"
        : bytes.ToString(CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) =>
        span.Ticks % TimeSpan.TicksPerSecond == 0 ? ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"
        : ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
}
