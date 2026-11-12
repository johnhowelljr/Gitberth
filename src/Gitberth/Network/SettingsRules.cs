using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Detection;

namespace Gitberth.Network;

/// <param name="HostCpus">The host's CPUs; 0 when unknown (no upper bound is checked then).</param>
/// <param name="HostMemoryBytes">The host's memory; 0 when unknown.</param>
/// <param name="Networks">The networks that exist.</param>
/// <param name="GitApp">A Git app's settings: its network mode is always the shared network.</param>
public sealed record SettingsContext(int HostCpus, long HostMemoryBytes, IReadOnlySet<string> Networks, bool GitApp);

/// <summary>
/// What's wrong with settings, each under its field, in the words the form shows next to it: what Docker would refuse at
/// create or update, said before anything changes. No warnings beyond that (a privileged container or a capability is the
/// owner's choice). Ports are PortRules.Check's. Pure.
/// </summary>
public static partial class SettingsRules
{
    /// <summary>Docker's smallest memory limit: 6 MiB.</summary>
    public const long MinimumMemory = 6L * 1024 * 1024;

    private static readonly HashSet<string> RestartNames = new(StringComparer.Ordinal) { "no", "always", "unless-stopped", "on-failure" };

    private static readonly HashSet<string> UlimitNames = new(StringComparer.Ordinal)
    {
        "core", "cpu", "data", "fsize", "locks", "memlock", "msgqueue", "nice", "nofile", "nproc", "rss", "rtprio", "rttime", "sigpending", "stack",
    };

    private static readonly HashSet<string> SecurityKeys = new(StringComparer.Ordinal)
        { "apparmor", "seccomp", "label", "no-new-privileges", "systempaths", "writable-cgroups", "mask", "unmask" };

    public static IReadOnlyList<SettingProblem> Check(ContainerSettings settings, SettingsContext context)
    {
        var problems = new List<SettingProblem>();
        void Add(SettingField field, string message) => problems.Add(new SettingProblem(field, message));
        var s = settings;

        if (s.Image.Trim().Length == 0) Add(SettingField.Image, "Choose the image it runs.");
        else if (s.Image.Any(char.IsWhiteSpace)) Add(SettingField.Image, $"\"{s.Image}\" isn't an image reference: it has a space in it.");

        if (s.Entrypoint is { Count: > 0 } ep && ep[0].Trim().Length == 0) Add(SettingField.Entrypoint, "The entrypoint's program can't be empty.");
        if (s.WorkingDir is { } dir && !dir.StartsWith('/')) Add(SettingField.WorkingDir, "The working directory must be a full path, starting with /.");
        if (s.User is { } user && (user.Length == 0 || user.Any(char.IsWhiteSpace) || user.Count(c => c == ':') > 1))
            Add(SettingField.User, "The user is a name or ID, optionally with a group: postgres, 1000 or 1000:1000.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in s.Env)
        {
            // A Git app's name may have a value for every deploy, one for production and one for previews.
            if (!EnvRules.IsValidKey(e.Key)) Add(SettingField.Env, $"\"{e.Key}\" isn't a variable name: use letters, digits and _, not starting with a digit.");
            else if (!keys.Add(Apps.EnvScopes.Named(e.Key, e.Scope))) Add(SettingField.Env, $"{Apps.EnvScopes.Named(e.Key, e.Scope)} is listed twice.");
            if (e.Value.Contains('\0')) Add(SettingField.Env, $"{e.Key}'s value has a null character in it.");
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in s.Mounts)
        {
            var target = CloneSpec.Clean(m.Target);
            if (!target.StartsWith('/')) Add(SettingField.Mounts, $"{(m.Target.Length == 0 ? "A mount" : m.Target)} needs a full path in the container, starting with /.");
            else if (target == "/") Add(SettingField.Mounts, "Nothing can be mounted over the container's root (/).");
            else if (!targets.Add(target)) Add(SettingField.Mounts, $"{target} is mounted twice.");
            switch (m.Kind)
            {
                case MountKind.Bind when !m.Source.StartsWith('/'):
                    Add(SettingField.Mounts, $"The folder for {target} must be a full path on the host, starting with /.");
                    break;
                case MountKind.Volume when !VolumeName().IsMatch(m.Source):
                    Add(SettingField.Mounts, m.Source.Length == 0 ? $"Name the volume for {target}." : $"\"{m.Source}\" isn't a volume name: use letters, digits, _, . and -.");
                    break;
                case MountKind.Tmpfs when m.TmpfsBytes is <= 0:
                    Add(SettingField.Mounts, $"The tmpfs at {target} needs a size above 0.");
                    break;
            }
        }

        var mode = s.NetworkMode;
        if (context.GitApp && mode.Length > 0) Add(SettingField.NetworkMode, "A Git app always runs on the shared network.");
        else if (mode.StartsWith("container:", StringComparison.Ordinal) && mode.Length == "container:".Length)
            Add(SettingField.NetworkMode, "Name the container whose network it shares.");
        else if (mode.Length > 0 && !ContainerSettingsJson.IsOwnMode(mode) && !context.Networks.Contains(mode))
            Add(SettingField.NetworkMode, $"There's no network called {mode}.");

        if (s.Hostname is { } hostname)
        {
            if (mode.StartsWith("container:", StringComparison.Ordinal)) Add(SettingField.Hostname, "A container that shares another's network has that one's hostname.");
            else if (!HostName().IsMatch(hostname)) Add(SettingField.Hostname, $"\"{hostname}\" isn't a hostname: use letters, digits and -, up to 63 characters.");
        }
        if (s.Domainname is { } domainName && !DomainName().IsMatch(domainName)) Add(SettingField.Hostname, $"\"{domainName}\" isn't a domain name.");

        foreach (var extra in s.ExtraHosts)
        {
            var colon = extra.IndexOf(':');
            var name = colon > 0 ? extra[..colon] : "";
            var ip = colon > 0 ? extra[(colon + 1)..].Trim('[', ']') : "";
            if (name.Length == 0 || !DomainName().IsMatch(name) || (ip != "host-gateway" && !IPAddress.TryParse(ip, out _)))
                Add(SettingField.ExtraHosts, $"\"{extra}\" isn't host:address, such as db.lan:10.1.1.5.");
        }
        foreach (var dns in s.Dns)
            if (!IPAddress.TryParse(dns, out _)) Add(SettingField.Dns, $"\"{dns}\" isn't a DNS server's address.");
        foreach (var search in s.DnsSearch)
            if (search != "." && !DomainName().IsMatch(search)) Add(SettingField.Dns, $"\"{search}\" isn't a search domain.");
        foreach (var option in s.DnsOptions)
            if (option.Trim().Length == 0 || option.Any(char.IsWhiteSpace)) Add(SettingField.Dns, $"\"{option}\" isn't a resolver option, such as ndots:2.");

        if (!RestartNames.Contains(s.Restart.Name)) Add(SettingField.RestartPolicy, "The restart policy is no, always, unless-stopped or on-failure.");
        else if (s.Restart.MaximumRetryCount < 0) Add(SettingField.RestartPolicy, "The number of tries can't be negative.");
        else if (s.Restart.MaximumRetryCount > 0 && s.Restart.Name != "on-failure") Add(SettingField.RestartPolicy, "Only on-failure counts tries.");

        var r = s.Resources;
        if (r.MemoryBytes is { } memory)
        {
            if (memory < MinimumMemory) Add(SettingField.Memory, "The memory limit must be at least 6 MiB.");
            else if (context.HostMemoryBytes > 0 && memory > context.HostMemoryBytes)
                Add(SettingField.Memory, $"The memory limit is more than the host has ({SettingsDiff.Size(context.HostMemoryBytes)}).");
        }
        if (r.MemoryReservationBytes is { } reservation)
        {
            if (reservation < MinimumMemory) Add(SettingField.Memory, "The memory reservation must be at least 6 MiB.");
            else if (r.MemoryBytes is { } limit && reservation > limit) Add(SettingField.Memory, "The memory reservation can't be more than the limit.");
        }
        if (r.MemorySwapBytes is { } swap && swap != -1)
        {
            if (r.MemoryBytes is not { } limit) Add(SettingField.Memory, "A swap limit needs a memory limit.");
            else if (swap < limit) Add(SettingField.Memory, "The swap limit counts memory and swap together: it can't be less than the memory limit.");
        }
        if (r.Cpus is { } cpus)
        {
            if (cpus <= 0 || cpus < 0.01) Add(SettingField.Cpu, "The CPU limit must be at least 0.01.");
            else if (context.HostCpus > 0 && cpus > context.HostCpus)
                Add(SettingField.Cpu, $"The CPU limit is more than the host has ({context.HostCpus.ToString(CultureInfo.InvariantCulture)} CPUs).");
        }
        if (r.CpuShares is { } shares && shares is < 2 or > 262144) Add(SettingField.Cpu, "CPU shares are between 2 and 262144 (1024 is the usual weight).");
        if (r.CpusetCpus is { } cpuset)
        {
            if (!CpuSet().IsMatch(cpuset)) Add(SettingField.Cpu, $"\"{cpuset}\" isn't a list of CPUs, such as 0-3 or 0,2.");
            else if (context.HostCpus > 0 && Numbers(cpuset).Any(n => n >= context.HostCpus))
                Add(SettingField.Cpu, $"The host has CPUs 0 to {(context.HostCpus - 1).ToString(CultureInfo.InvariantCulture)}.");
        }
        if (r.PidsLimit is { } pids && pids != -1 && pids < 1) Add(SettingField.PidsLimit, "The process limit must be at least 1.");

        if (s.ShmSizeBytes is <= 0) Add(SettingField.ShmSize, "Shared memory must be more than 0.");

        var ulimits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in s.Ulimits)
        {
            if (!UlimitNames.Contains(u.Name)) Add(SettingField.Ulimits, $"\"{u.Name}\" isn't a ulimit Docker knows, such as nofile or nproc.");
            else if (!ulimits.Add(u.Name)) Add(SettingField.Ulimits, $"{u.Name} is listed twice.");
            if (u.Soft > u.Hard && u.Hard != -1) Add(SettingField.Ulimits, $"{u.Name}'s soft limit can't be above its hard limit.");
        }

        if (s.Health.Mode == HealthMode.Custom)
        {
            var test = s.Health.Test ?? [];
            if (test.Count < 2 || test[0] is not ("CMD" or "CMD-SHELL") || test.Skip(1).All(t => t.Trim().Length == 0))
                Add(SettingField.Health, "Give the health check a command to run.");
            foreach (var (span, what) in new[] { (s.Health.Interval, "interval"), (s.Health.Timeout, "timeout"), (s.Health.StartPeriod, "start period"), (s.Health.StartInterval, "start interval") })
                if (span is { } value && value < TimeSpan.FromMilliseconds(1) && !(what == "start period" && value == TimeSpan.Zero))
                    Add(SettingField.Health, $"The health check's {what} must be at least 1 ms.");
            if (s.Health.Retries is < 0) Add(SettingField.Health, "The health check's retries can't be negative.");
        }

        foreach (var key in s.Labels.Keys)
            if (key.Trim().Length == 0) Add(SettingField.Labels, "A label needs a name.");

        foreach (var d in s.Devices)
        {
            if (!d.HostPath.StartsWith('/')) Add(SettingField.Devices, $"The device \"{d.HostPath}\" must be a full path on the host, such as /dev/dri.");
            if (d.ContainerPath.Length > 0 && !d.ContainerPath.StartsWith('/')) Add(SettingField.Devices, $"\"{d.ContainerPath}\" must be a full path in the container.");
            if (d.Permissions.Length == 0 || d.Permissions.Any(c => c is not ('r' or 'w' or 'm'))) Add(SettingField.Devices, $"A device's permissions are r, w and m: not \"{d.Permissions}\".");
        }

        if (s.Gpus is { } gpus && !(gpus == "all" || (long.TryParse(gpus, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0) || GpuIds().IsMatch(gpus)))
            Add(SettingField.Gpus, "GPUs are all, a number, or device=0,1.");

        foreach (var cap in s.CapAdd.Concat(s.CapDrop))
            if (!Capability().IsMatch(cap)) Add(SettingField.Capabilities, $"\"{cap}\" isn't a capability, such as NET_ADMIN or ALL.");

        foreach (var opt in s.SecurityOpt)
        {
            var key = opt.Split('=', ':')[0];
            if (!SecurityKeys.Contains(key)) Add(SettingField.SecurityOpt, $"\"{opt}\" isn't a security option Docker takes, such as no-new-privileges or seccomp=unconfined.");
        }

        foreach (var (key, value) in s.Sysctls)
        {
            if (!Sysctl().IsMatch(key)) Add(SettingField.Sysctls, $"\"{key}\" isn't a sysctl, such as net.core.somaxconn.");
            else if (value.Trim().Length == 0) Add(SettingField.Sysctls, $"{key} needs a value.");
            else if (mode == "host" && key.StartsWith("net.", StringComparison.Ordinal))
                Add(SettingField.Sysctls, $"{key} can't be set on a container that uses the host's network.");
        }

        if (s.Logging.Driver.Length > 0 && !LogDriver().IsMatch(s.Logging.Driver)) Add(SettingField.Logging, $"\"{s.Logging.Driver}\" isn't a log driver's name.");

        if (s.StopSignal is { } signal && !Signal().IsMatch(signal)) Add(SettingField.Stop, $"\"{signal}\" isn't a signal, such as SIGTERM or SIGINT.");
        if (s.StopTimeoutSeconds is < -1) Add(SettingField.Stop, "The stop timeout is a number of seconds, or -1 to wait as long as it takes.");

        return problems;
    }

    // 0-3,5,7-8
    private static IEnumerable<int> Numbers(string cpuset) =>
        cpuset.Split(',').SelectMany(part => part.Split('-')).Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex VolumeName();

    [GeneratedRegex(@"^(?=.{1,63}$)[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^(?=.{1,253}$)[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.?$")]
    private static partial Regex DomainName();

    [GeneratedRegex(@"^\d+(-\d+)?(,\d+(-\d+)?)*$")]
    private static partial Regex CpuSet();

    [GeneratedRegex(@"^device=[A-Za-z0-9-]+(,[A-Za-z0-9-]+)*$")]
    private static partial Regex GpuIds();

    [GeneratedRegex(@"^(ALL|(CAP_)?[A-Z][A-Z0-9_]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Capability();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-zA-Z0-9_*-]+)+$")]
    private static partial Regex Sysctl();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_./:-]*$")]
    private static partial Regex LogDriver();

    [GeneratedRegex(@"^(SIG[A-Z0-9+-]+|[A-Z][A-Z0-9]*|\d{1,2})$")]
    private static partial Regex Signal();
}
