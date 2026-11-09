using Gitberth.Apps;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>What Docker would refuse in a container's settings, said under the field before anything changes.</summary>
public sealed class SettingsRulesTests
{
    private static readonly SettingsContext Host = new(8, 16L << 30, new HashSet<string> { "bridge", "gitberth", "media" }, GitApp: false);

    // Every field set, and set well.
    private static readonly ContainerSettings Good = new()
    {
        Image = "postgres:18",
        Entrypoint = ["docker-entrypoint.sh"],
        Command = ["postgres"],
        WorkingDir = "/var/lib/postgresql",
        User = "999:999",
        Env = [new EnvSetting("POSTGRES_PASSWORD", "s3cret"), new EnvSetting("PGDATA", "/var/lib/postgresql/18/docker")],
        Mounts = [new MountSetting(MountKind.Volume, "postgres-data", "/var/lib/postgresql"), new MountSetting(MountKind.Bind, "/srv/backups", "/backups", true),
            new MountSetting(MountKind.Tmpfs, "", "/scratch", TmpfsBytes: 64L << 20)],
        NetworkMode = "",
        Hostname = "db",
        Domainname = "lan",
        ExtraHosts = ["files.lan:10.1.1.5", "host.docker.internal:host-gateway"],
        Dns = ["10.1.1.1", "fd00::1"],
        DnsSearch = ["lan"],
        DnsOptions = ["ndots:2"],
        Restart = new RestartSetting("on-failure", 3),
        Resources = new ResourceSetting(512L << 20, 256L << 20, 1L << 30, 1.5, 512, "0-3", 200),
        ShmSizeBytes = 256L << 20,
        Ulimits = [new UlimitSetting("nofile", 1024, 4096)],
        Health = new HealthSetting(HealthMode.Custom, ["CMD-SHELL", "pg_isready"], TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 5, TimeSpan.FromSeconds(30)),
        Labels = new Dictionary<string, string> { ["team"] = "data" },
        Devices = [new DeviceSetting("/dev/dri", "/dev/dri", "rw")],
        Gpus = "device=0,1",
        CapAdd = ["NET_ADMIN", "CAP_SYS_TIME"],
        CapDrop = ["ALL"],
        SecurityOpt = ["no-new-privileges", "seccomp=unconfined", "apparmor:unconfined"],
        Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" },
        Logging = new LogSetting("json-file", new Dictionary<string, string> { ["max-size"] = "10m" }),
        StopSignal = "SIGINT",
        StopTimeoutSeconds = 60,
    };

    [Fact]
    public void Settings_set_well_have_no_problems()
    {
        Assert.Empty(SettingsRules.Check(Good, Host));
        Assert.Empty(SettingsRules.Check(new ContainerSettings { Image = "nginx:alpine" }, Host));
        // Unlimited swap, no PID limit, a named network, another's network, a stop that waits for ever.
        Assert.Empty(SettingsRules.Check(Good with { Resources = Good.Resources with { MemorySwapBytes = -1, PidsLimit = -1 }, NetworkMode = "media", StopTimeoutSeconds = -1 }, Host));
        Assert.Empty(SettingsRules.Check(Good with { NetworkMode = "container:vpn", Hostname = null, Domainname = null }, Host));
        Assert.Empty(SettingsRules.Check(Good with { Gpus = "all" }, Host));
        Assert.Empty(SettingsRules.Check(Good with { Gpus = "2" }, Host));
        // Without the host's facts, no upper bound is checked.
        Assert.Empty(SettingsRules.Check(Good with { Resources = Good.Resources with { Cpus = 64, MemoryBytes = 1L << 40, MemorySwapBytes = -1 } }, Host with { HostCpus = 0, HostMemoryBytes = 0 }));
    }

    public static readonly TheoryData<string, SettingField, string> Problems = new()
    {
        { "no image", SettingField.Image, "Choose the image it runs." },
        { "image with a space", SettingField.Image, "\"postgres 18\" isn't an image reference: it has a space in it." },
        { "empty entrypoint program", SettingField.Entrypoint, "The entrypoint's program can't be empty." },
        { "relative working folder", SettingField.WorkingDir, "The working directory must be a full path, starting with /." },
        { "user with a space", SettingField.User, "The user is a name or ID, optionally with a group: postgres, 1000 or 1000:1000." },
        { "bad variable name", SettingField.Env, "\"1BAD\" isn't a variable name: use letters, digits and _, not starting with a digit." },
        { "variable twice", SettingField.Env, "PGDATA is listed twice." },
        { "relative mount", SettingField.Mounts, "data needs a full path in the container, starting with /." },
        { "mount over root", SettingField.Mounts, "Nothing can be mounted over the container's root (/)." },
        { "mounted twice", SettingField.Mounts, "/var/lib/postgresql is mounted twice." },
        { "relative bind", SettingField.Mounts, "The folder for /backups must be a full path on the host, starting with /." },
        { "bad volume name", SettingField.Mounts, "\"my volume\" isn't a volume name: use letters, digits, _, . and -." },
        { "empty tmpfs", SettingField.Mounts, "The tmpfs at /scratch needs a size above 0." },
        { "unknown network", SettingField.NetworkMode, "There's no network called nope." },
        { "container mode without a name", SettingField.NetworkMode, "Name the container whose network it shares." },
        { "hostname with another's network", SettingField.Hostname, "A container that shares another's network has that one's hostname." },
        { "bad hostname", SettingField.Hostname, "\"db_1\" isn't a hostname: use letters, digits and -, up to 63 characters." },
        { "bad extra host", SettingField.ExtraHosts, "\"files.lan\" isn't host:address, such as db.lan:10.1.1.5." },
        { "bad dns server", SettingField.Dns, "\"dns.lan\" isn't a DNS server's address." },
        { "bad restart policy", SettingField.RestartPolicy, "The restart policy is no, always, unless-stopped or on-failure." },
        { "tries without on-failure", SettingField.RestartPolicy, "Only on-failure counts tries." },
        { "tiny memory", SettingField.Memory, "The memory limit must be at least 6 MiB." },
        { "memory above the host's", SettingField.Memory, "The memory limit is more than the host has (16 GiB)." },
        { "reservation above the limit", SettingField.Memory, "The memory reservation can't be more than the limit." },
        { "swap without memory", SettingField.Memory, "A swap limit needs a memory limit." },
        { "swap below memory", SettingField.Memory, "The swap limit counts memory and swap together: it can't be less than the memory limit." },
        { "no cpus", SettingField.Cpu, "The CPU limit must be at least 0.01." },
        { "cpus above the host's", SettingField.Cpu, "The CPU limit is more than the host has (8 CPUs)." },
        { "cpu shares", SettingField.Cpu, "CPU shares are between 2 and 262144 (1024 is the usual weight)." },
        { "bad cpuset", SettingField.Cpu, "\"0-3,x\" isn't a list of CPUs, such as 0-3 or 0,2." },
        { "cpuset beyond the host", SettingField.Cpu, "The host has CPUs 0 to 7." },
        { "pid limit", SettingField.PidsLimit, "The process limit must be at least 1." },
        { "shared memory", SettingField.ShmSize, "Shared memory must be more than 0." },
        { "unknown ulimit", SettingField.Ulimits, "\"files\" isn't a ulimit Docker knows, such as nofile or nproc." },
        { "soft above hard", SettingField.Ulimits, "nofile's soft limit can't be above its hard limit." },
        { "health check without a command", SettingField.Health, "Give the health check a command to run." },
        { "health check interval", SettingField.Health, "The health check's interval must be at least 1 ms." },
        { "bad device", SettingField.Devices, "The device \"dri\" must be a full path on the host, such as /dev/dri." },
        { "bad device permissions", SettingField.Devices, "A device's permissions are r, w and m: not \"rx\"." },
        { "bad gpus", SettingField.Gpus, "GPUs are all, a number, or device=0,1." },
        { "bad capability", SettingField.Capabilities, "\"net admin\" isn't a capability, such as NET_ADMIN or ALL." },
        { "bad security option", SettingField.SecurityOpt, "\"privileged\" isn't a security option Docker takes, such as no-new-privileges or seccomp=unconfined." },
        { "bad sysctl", SettingField.Sysctls, "\"somaxconn\" isn't a sysctl, such as net.core.somaxconn." },
        { "net sysctl on the host's network", SettingField.Sysctls, "net.core.somaxconn can't be set on a container that uses the host's network." },
        { "bad log driver", SettingField.Logging, "\"json file\" isn't a log driver's name." },
        { "bad stop signal", SettingField.Stop, "\"stop\" isn't a signal, such as SIGTERM or SIGINT." },
        { "bad stop timeout", SettingField.Stop, "The stop timeout is a number of seconds, or -1 to wait as long as it takes." },
    };

    private static ContainerSettings Broken(string what) => what switch
    {
        "no image" => Good with { Image = " " },
        "image with a space" => Good with { Image = "postgres 18" },
        "empty entrypoint program" => Good with { Entrypoint = [" "] },
        "relative working folder" => Good with { WorkingDir = "app" },
        "user with a space" => Good with { User = "post gres" },
        "bad variable name" => Good with { Env = [new EnvSetting("1BAD", "x")] },
        "variable twice" => Good with { Env = [.. Good.Env, new EnvSetting("PGDATA", "/data")] },
        "relative mount" => Good with { Mounts = [new MountSetting(MountKind.Volume, "x", "data")] },
        "mount over root" => Good with { Mounts = [new MountSetting(MountKind.Volume, "x", "/")] },
        "mounted twice" => Good with { Mounts = [.. Good.Mounts, new MountSetting(MountKind.Volume, "other", "/var/lib/postgresql/")] },
        "relative bind" => Good with { Mounts = [new MountSetting(MountKind.Bind, "backups", "/backups")] },
        "bad volume name" => Good with { Mounts = [new MountSetting(MountKind.Volume, "my volume", "/data")] },
        "empty tmpfs" => Good with { Mounts = [new MountSetting(MountKind.Tmpfs, "", "/scratch", TmpfsBytes: 0)] },
        "unknown network" => Good with { NetworkMode = "nope" },
        "container mode without a name" => Good with { NetworkMode = "container:", Hostname = null },
        "hostname with another's network" => Good with { NetworkMode = "container:vpn", Domainname = null },
        "bad hostname" => Good with { Hostname = "db_1" },
        "bad extra host" => Good with { ExtraHosts = ["files.lan"] },
        "bad dns server" => Good with { Dns = ["dns.lan"] },
        "bad restart policy" => Good with { Restart = new RestartSetting("sometimes") },
        "tries without on-failure" => Good with { Restart = new RestartSetting("always", 3) },
        "tiny memory" => Good with { Resources = Good.Resources with { MemoryBytes = 1L << 20, MemoryReservationBytes = null, MemorySwapBytes = null } },
        "memory above the host's" => Good with { Resources = Good.Resources with { MemoryBytes = 32L << 30, MemorySwapBytes = -1 } },
        "reservation above the limit" => Good with { Resources = Good.Resources with { MemoryReservationBytes = 768L << 20 } },
        "swap without memory" => Good with { Resources = Good.Resources with { MemoryBytes = null, MemoryReservationBytes = null } },
        "swap below memory" => Good with { Resources = Good.Resources with { MemorySwapBytes = 256L << 20 } },
        "no cpus" => Good with { Resources = Good.Resources with { Cpus = 0 } },
        "cpus above the host's" => Good with { Resources = Good.Resources with { Cpus = 12 } },
        "cpu shares" => Good with { Resources = Good.Resources with { CpuShares = 1 } },
        "bad cpuset" => Good with { Resources = Good.Resources with { CpusetCpus = "0-3,x" } },
        "cpuset beyond the host" => Good with { Resources = Good.Resources with { CpusetCpus = "0,9" } },
        "pid limit" => Good with { Resources = Good.Resources with { PidsLimit = 0 } },
        "shared memory" => Good with { ShmSizeBytes = 0 },
        "unknown ulimit" => Good with { Ulimits = [new UlimitSetting("files", 1, 2)] },
        "soft above hard" => Good with { Ulimits = [new UlimitSetting("nofile", 4096, 1024)] },
        "health check without a command" => Good with { Health = new HealthSetting(HealthMode.Custom, ["CMD-SHELL", " "]) },
        "health check interval" => Good with { Health = Good.Health with { Interval = TimeSpan.FromTicks(10) } },
        "bad device" => Good with { Devices = [new DeviceSetting("dri", "/dev/dri")] },
        "bad device permissions" => Good with { Devices = [new DeviceSetting("/dev/dri", "/dev/dri", "rx")] },
        "bad gpus" => Good with { Gpus = "many" },
        "bad capability" => Good with { CapAdd = ["net admin"] },
        "bad security option" => Good with { SecurityOpt = ["privileged"] },
        "bad sysctl" => Good with { Sysctls = new Dictionary<string, string> { ["somaxconn"] = "1024" } },
        "net sysctl on the host's network" => Good with { NetworkMode = "host" },
        "bad log driver" => Good with { Logging = new LogSetting("json file") },
        "bad stop signal" => Good with { StopSignal = "stop" },
        "bad stop timeout" => Good with { StopTimeoutSeconds = -5 },
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void Says_what_docker_would_refuse_under_its_field(string what, SettingField field, string message)
    {
        var problems = SettingsRules.Check(Broken(what), Host);

        Assert.Contains(new SettingProblem(field, message), problems);
        Assert.All(problems, p => Assert.Equal(field, p.Field));
    }

    [Fact]
    public void A_git_app_stays_on_the_shared_network() =>
        Assert.Equal([new SettingProblem(SettingField.NetworkMode, "A Git app always runs on the shared network.")],
            SettingsRules.Check(Good with { NetworkMode = "host" , Sysctls = new Dictionary<string, string>() }, Host with { GitApp = true }));
}
