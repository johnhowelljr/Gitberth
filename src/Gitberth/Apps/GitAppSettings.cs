using System.Text.Json;
using System.Text.Json.Serialization;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;

namespace Gitberth.Apps;

/// <summary>
/// A Git app's settings beyond the classic ones, as <see cref="AppDefinition.RuntimeExtras"/> holds them: everything in
/// <see cref="ContainerSettings"/> that has no column or row of its own. Canonical JSON: the properties in this order,
/// maps sorted by key, nothing for a member that's unset (null, empty or false), and no JSON at all when nothing is set.
/// </summary>
public sealed record GitExtras
{
    /// <summary>Null: the image's own. Empty: none.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }
    /// <summary>Null: the image's own.</summary>
    public IReadOnlyList<string>? Command { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public IReadOnlyList<GitTmpfs>? Tmpfs { get; init; }
    public string? Hostname { get; init; }
    public string? Domainname { get; init; }
    public IReadOnlyList<string>? ExtraHosts { get; init; }
    public IReadOnlyList<string>? Dns { get; init; }
    public IReadOnlyList<string>? DnsSearch { get; init; }
    public IReadOnlyList<string>? DnsOptions { get; init; }
    /// <summary>on-failure's maximum retries.</summary>
    public int? RestartRetries { get; init; }
    public long? MemoryReservation { get; init; }
    /// <summary>-1: unlimited swap.</summary>
    public long? MemorySwap { get; init; }
    public long? CpuShares { get; init; }
    public string? CpusetCpus { get; init; }
    /// <summary>-1: no limit.</summary>
    public long? PidsLimit { get; init; }
    public long? ShmSize { get; init; }
    public IReadOnlyList<UlimitSetting>? Ulimits { get; init; }
    public GitHealth? Health { get; init; }
    /// <summary>The owner's own labels; Gitberth's are added by every deploy.</summary>
    public SortedDictionary<string, string>? Labels { get; init; }
    public IReadOnlyList<DeviceSetting>? Devices { get; init; }
    public string? Gpus { get; init; }
    public IReadOnlyList<string>? CapAdd { get; init; }
    public IReadOnlyList<string>? CapDrop { get; init; }
    public bool? Privileged { get; init; }
    public IReadOnlyList<string>? SecurityOpt { get; init; }
    public bool? ReadOnlyRootfs { get; init; }
    public bool? Init { get; init; }
    public SortedDictionary<string, string>? Sysctls { get; init; }
    /// <summary>Null: the daemon's driver.</summary>
    public string? LogDriver { get; init; }
    public SortedDictionary<string, string>? LogOptions { get; init; }
    public string? StopSignal { get; init; }
    public int? StopTimeout { get; init; }
    public bool? Tty { get; init; }
    public bool? OpenStdin { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The extras a column holds; null for none, or for JSON that can't be read (nothing is lost: it's never written back unread).</summary>
    public static GitExtras? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<GitExtras>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The canonical JSON of the extras, or null when nothing is set.</summary>
    public static string? Write(GitExtras? extras)
    {
        if (extras is null) return null;
        var json = JsonSerializer.Serialize(extras.Normalized(), Json);
        return json == "{}" ? null : json;
    }

    /// <summary>The same extras with every unset member null, so equal settings always write the same JSON.</summary>
    public GitExtras Normalized() => this with
    {
        WorkingDir = Blank(WorkingDir),
        User = Blank(User),
        Tmpfs = List(Tmpfs),
        Hostname = Blank(Hostname),
        Domainname = Blank(Domainname),
        ExtraHosts = List(ExtraHosts),
        Dns = List(Dns),
        DnsSearch = List(DnsSearch),
        DnsOptions = List(DnsOptions),
        RestartRetries = RestartRetries is > 0 ? RestartRetries : null,
        MemoryReservation = MemoryReservation is > 0 ? MemoryReservation : null,
        MemorySwap = MemorySwap is > 0 or -1 ? MemorySwap : null,
        CpuShares = CpuShares is > 0 ? CpuShares : null,
        CpusetCpus = Blank(CpusetCpus),
        PidsLimit = PidsLimit is > 0 ? PidsLimit : null,
        ShmSize = ShmSize is > 0 ? ShmSize : null,
        Ulimits = List(Ulimits),
        Health = Health is { Mode: not HealthMode.Image } ? Health : null,
        Labels = Map(Labels),
        Devices = List(Devices),
        Gpus = Blank(Gpus),
        CapAdd = List(CapAdd),
        CapDrop = List(CapDrop),
        Privileged = True(Privileged),
        SecurityOpt = List(SecurityOpt),
        ReadOnlyRootfs = True(ReadOnlyRootfs),
        Init = True(Init),
        Sysctls = Map(Sysctls),
        LogDriver = Blank(LogDriver),
        LogOptions = Map(LogOptions),
        StopSignal = Blank(StopSignal),
        StopTimeout = StopTimeout,
        Tty = True(Tty),
        OpenStdin = True(OpenStdin),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<T>? List<T>(IReadOnlyList<T>? list) => list is { Count: > 0 } ? [.. list] : null;

    private static SortedDictionary<string, string>? Map(IReadOnlyDictionary<string, string>? map) =>
        map is { Count: > 0 } ? new SortedDictionary<string, string>(map.ToDictionary(), StringComparer.Ordinal) : null;

    private static bool? True(bool? value) => value == true ? true : null;
}

/// <summary>A tmpfs mount of a Git app (its volumes and binds are Volumes rows).</summary>
public sealed record GitTmpfs(string Target, long? Bytes = null, bool ReadOnly = false);

/// <summary>A Git app's own health check (Docker's HEALTHCHECK), when it isn't the image's.</summary>
public sealed record GitHealth(HealthMode Mode, IReadOnlyList<string>? Test = null, TimeSpan? Interval = null, TimeSpan? Timeout = null,
    int? Retries = null, TimeSpan? StartPeriod = null, TimeSpan? StartInterval = null)
{
    public HealthSetting ToSetting() => new(Mode, Test, Interval, Timeout, Retries, StartPeriod, StartInterval);

    public static GitHealth? From(HealthSetting h) => h.Mode == HealthMode.Image ? null : new(h.Mode, h.Test, h.Interval, h.Timeout, h.Retries, h.StartPeriod, h.StartInterval);
}

/// <summary>
/// Git apps' DB settings as <see cref="ContainerSettings"/>, and back: the columns (restart policy, memory, CPUs), the rows
/// (environment, ports, volumes) and <see cref="AppDefinition.RuntimeExtras"/> for the rest. A Git app's settings live in
/// the database and a deploy applies them, so nothing here touches Docker.
/// </summary>
public static class GitAppSettings
{
    /// <summary>Fields a Git app never edits: Image (built from the repo) and NetworkMode (always the shared network).</summary>
    public static readonly IReadOnlySet<SettingField> ReadOnly = new HashSet<SettingField> { SettingField.Image, SettingField.NetworkMode };

    /// <summary>The resource limits as the forms' Resources fields set them: memory, CPUs (and their priority), processes, shared memory.</summary>
    public static readonly IReadOnlySet<SettingField> Resources = new HashSet<SettingField>
        { SettingField.Memory, SettingField.Cpu, SettingField.PidsLimit, SettingField.ShmSize };

    private const long MiB = 1024 * 1024;

    /// <summary>A memory limit as a Git app's column keeps it: whole MiB, at least 1; null for none.</summary>
    public static int? MemoryMb(long? bytes) => bytes is > 0 and var b ? (int)Math.Max(1, (b + MiB / 2) / MiB) : null;

    public static ContainerSettings ToContainer(AppDefinition app, SecretProtector protector)
    {
        var x = GitExtras.Parse(app.RuntimeExtras) ?? new GitExtras();
        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["gitberth.managed"] = "true", ["gitberth.app"] = app.Slug };
        foreach (var (key, value) in x.Labels ?? []) labels.TryAdd(key, value);
        var mounts = app.Volumes.OrderBy(v => v.Id)
            .Select(v => VolumeNames.IsBind(v.Source)
                ? new MountSetting(MountKind.Bind, v.Source, v.Target, v.ReadOnly)
                : new MountSetting(MountKind.Volume, VolumeNames.Resolve(app.Slug, v.Source), v.Target, v.ReadOnly))
            .Concat((x.Tmpfs ?? []).Select(t => new MountSetting(MountKind.Tmpfs, "", t.Target, t.ReadOnly, t.Bytes)))
            .ToList();
        return new ContainerSettings
        {
            Image = app.CurrentImage ?? "",
            Entrypoint = x.Entrypoint,
            Command = x.Command,
            WorkingDir = x.WorkingDir,
            User = x.User,
            Env = app.EnvVars.OrderBy(v => v.Id)
                .Select(v => new EnvSetting(v.Key, protector.TryUnprotect(v.ProtectedValue), Secret: v.IsSecret, BuildTime: v.BuildTime)).ToList(),
            Ports = app.Ports.OrderBy(p => p.Id).Select(RuleOf).ToList(),
            Mounts = mounts,
            NetworkMode = "",
            Hostname = x.Hostname,
            Domainname = x.Domainname,
            ExtraHosts = x.ExtraHosts ?? [],
            Dns = x.Dns ?? [],
            DnsSearch = x.DnsSearch ?? [],
            DnsOptions = x.DnsOptions ?? [],
            Restart = new RestartSetting(app.RestartPolicy, x.RestartRetries ?? 0),
            Resources = new ResourceSetting(app.MemoryLimitMb is int mb ? mb * MiB : null, x.MemoryReservation, x.MemorySwap, app.CpuLimit, x.CpuShares,
                x.CpusetCpus, x.PidsLimit),
            ShmSizeBytes = x.ShmSize,
            Ulimits = x.Ulimits ?? [],
            Health = x.Health?.ToSetting() ?? new HealthSetting(),
            Labels = labels,
            Devices = x.Devices ?? [],
            Gpus = x.Gpus,
            CapAdd = x.CapAdd ?? [],
            CapDrop = x.CapDrop ?? [],
            Privileged = x.Privileged == true,
            SecurityOpt = x.SecurityOpt ?? [],
            ReadOnlyRootfs = x.ReadOnlyRootfs == true,
            Init = x.Init == true,
            Sysctls = x.Sysctls ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
            Logging = new LogSetting(x.LogDriver ?? "", x.LogOptions),
            StopSignal = x.StopSignal,
            StopTimeoutSeconds = x.StopTimeout,
            Tty = x.Tty == true,
            OpenStdin = x.OpenStdin == true,
        };
    }

    public static GitOptions ToGit(AppDefinition app) =>
        new(app.Branch, app.Subdirectory, app.DockerfileMode, app.CustomDockerfile, app.ProjectHint, app.AutoDeploy, app.VercelVariables);

    public static AppOptions ToOptions(AppDefinition app, HealthSettings health, RestartSchedules restarts) => new()
    {
        Name = app.Name,
        WebPort = PortRules.WebOf(app.Ports)?.ContainerPort,
        HealthCheckPath = app.HealthCheckPath,
        GatherStats = health.IsGathered(app.ContainerName),
        ScheduledRestart = restarts.For(app.ContainerName),
        Updates = app.UpdatePolicy,
        TrackedImage = app.ImageRef,
        RegistryId = app.RegistryId,
        SecretKeys = SecretKeysOf(app),
        Pinned = app.Pinned,
    };

    /// <summary>The variable names an app's row marks secret: its SecretKeys, or for a Git app its secret variables.</summary>
    public static IReadOnlyList<string> SecretKeysOf(AppDefinition app)
    {
        if (app.SecretKeys is { Length: > 0 } json)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        return app.EnvVars.Where(v => v.IsSecret).OrderBy(v => v.Id).Select(v => v.Key).ToList();
    }

    /// <summary>Writes the fields onto the app: columns, EnvVars and Volumes rows (short volume names), RuntimeExtras. The caller saves.</summary>
    public static void Apply(AppDefinition app, ContainerSettings settings, IReadOnlySet<SettingField> fields, SecretProtector protector)
    {
        var x = GitExtras.Parse(app.RuntimeExtras) ?? new GitExtras();
        foreach (var field in fields)
        {
            switch (field)
            {
                case SettingField.Entrypoint: x = x with { Entrypoint = settings.Entrypoint }; break;
                case SettingField.Command: x = x with { Command = settings.Command }; break;
                case SettingField.WorkingDir: x = x with { WorkingDir = settings.WorkingDir }; break;
                case SettingField.User: x = x with { User = settings.User }; break;
                case SettingField.Env: ApplyEnv(app, settings.Env, protector); break;
                case SettingField.Ports: ApplyPorts(app, settings.Ports); break;
                case SettingField.Mounts:
                    ApplyVolumes(app, settings.Mounts);
                    x = x with { Tmpfs = settings.Mounts.Where(m => m.Kind == MountKind.Tmpfs).Select(m => new GitTmpfs(m.Target, m.TmpfsBytes, m.ReadOnly)).ToList() };
                    break;
                case SettingField.Hostname: x = x with { Hostname = settings.Hostname, Domainname = settings.Domainname }; break;
                case SettingField.ExtraHosts: x = x with { ExtraHosts = settings.ExtraHosts }; break;
                case SettingField.Dns: x = x with { Dns = settings.Dns, DnsSearch = settings.DnsSearch, DnsOptions = settings.DnsOptions }; break;
                case SettingField.RestartPolicy:
                    app.RestartPolicy = settings.Restart.Name;
                    x = x with { RestartRetries = settings.Restart.Name == "on-failure" ? settings.Restart.MaximumRetryCount : null };
                    break;
                case SettingField.Memory:
                    app.MemoryLimitMb = MemoryMb(settings.Resources.MemoryBytes);
                    x = x with { MemoryReservation = settings.Resources.MemoryReservationBytes, MemorySwap = settings.Resources.MemorySwapBytes };
                    break;
                case SettingField.Cpu:
                    app.CpuLimit = settings.Resources.Cpus is > 0 ? settings.Resources.Cpus : null;
                    x = x with { CpuShares = settings.Resources.CpuShares, CpusetCpus = settings.Resources.CpusetCpus };
                    break;
                case SettingField.PidsLimit: x = x with { PidsLimit = settings.Resources.PidsLimit }; break;
                case SettingField.ShmSize: x = x with { ShmSize = settings.ShmSizeBytes }; break;
                case SettingField.Ulimits: x = x with { Ulimits = settings.Ulimits }; break;
                case SettingField.Health: x = x with { Health = GitHealth.From(settings.Health) }; break;
                case SettingField.Labels:
                    x = x with { Labels = new SortedDictionary<string, string>(settings.Labels.Where(l => !l.Key.StartsWith("gitberth.", StringComparison.Ordinal))
                        .ToDictionary(l => l.Key, l => l.Value), StringComparer.Ordinal) };
                    break;
                case SettingField.Devices: x = x with { Devices = settings.Devices }; break;
                case SettingField.Gpus: x = x with { Gpus = settings.Gpus }; break;
                case SettingField.Capabilities: x = x with { CapAdd = settings.CapAdd, CapDrop = settings.CapDrop }; break;
                case SettingField.Privileged: x = x with { Privileged = settings.Privileged }; break;
                case SettingField.SecurityOpt: x = x with { SecurityOpt = settings.SecurityOpt }; break;
                case SettingField.ReadOnlyRootfs: x = x with { ReadOnlyRootfs = settings.ReadOnlyRootfs }; break;
                case SettingField.Init: x = x with { Init = settings.Init }; break;
                case SettingField.Sysctls:
                    x = x with { Sysctls = new SortedDictionary<string, string>(settings.Sysctls.ToDictionary(), StringComparer.Ordinal) };
                    break;
                case SettingField.Logging:
                    x = x with
                    {
                        LogDriver = settings.Logging.Driver,
                        LogOptions = settings.Logging.Options is { } o ? new SortedDictionary<string, string>(o.ToDictionary(), StringComparer.Ordinal) : null,
                    };
                    break;
                case SettingField.Stop: x = x with { StopSignal = settings.StopSignal, StopTimeout = settings.StopTimeoutSeconds }; break;
                case SettingField.Terminal: x = x with { Tty = settings.Tty, OpenStdin = settings.OpenStdin }; break;
                // Image (built from the repo) and NetworkMode (the shared network) are never a Git app's to set.
            }
        }
        app.RuntimeExtras = GitExtras.Write(x);
    }

    public static void Apply(AppDefinition app, GitOptions git)
    {
        app.Branch = git.Branch.Trim();
        app.Subdirectory = string.IsNullOrWhiteSpace(git.Subdirectory) ? null : git.Subdirectory.Trim().Trim('/');
        app.DockerfileMode = git.DockerfileMode;
        if (git.CustomDockerfile is not null) app.CustomDockerfile = git.CustomDockerfile;
        app.ProjectHint = string.IsNullOrWhiteSpace(git.ProjectHint) ? null : git.ProjectHint;
        app.AutoDeploy = git.AutoDeploy;
        app.VercelVariables = git.VercelVariables;
    }

    /// <summary>
    /// The fields whose values differ between two settings, compared by content. Only what a Git app stores counts: the
    /// image and the network mode never change through its settings, and labels compare without Gitberth's own.
    /// </summary>
    public static IReadOnlySet<SettingField> Changed(ContainerSettings before, ContainerSettings after) =>
        Enum.GetValues<SettingField>().Where(f => !ReadOnly.Contains(f) && Key(before, f) != Key(after, f)).ToHashSet();

    // A field's value as text, for comparing it.
    private static string Key(ContainerSettings s, SettingField field) => field switch
    {
        SettingField.Entrypoint => J(s.Entrypoint),
        SettingField.Command => J(s.Command),
        SettingField.WorkingDir => s.WorkingDir ?? "",
        SettingField.User => s.User ?? "",
        SettingField.Env => J(s.Env.Where(e => !e.FromImage).Select(e => new[] { e.Key, e.Value, e.Secret ? "s" : "", e.BuildTime ? "b" : "" })),
        SettingField.Ports => J(s.Ports.Select(p => new object?[] { p.ContainerPort, p.Protocol, p.Access.ToString(), p.Access == PortAccess.Internal ? null : p.HostPort,
            p.Access == PortAccess.Custom ? p.HostIp : null })),
        SettingField.Mounts => J(s.Mounts),
        SettingField.Hostname => J(new[] { s.Hostname, s.Domainname }),
        SettingField.ExtraHosts => J(s.ExtraHosts),
        SettingField.Dns => J(new[] { s.Dns, s.DnsSearch, s.DnsOptions }),
        SettingField.RestartPolicy => J(s.Restart),
        SettingField.Memory => J(new[] { s.Resources.MemoryBytes, s.Resources.MemoryReservationBytes, s.Resources.MemorySwapBytes }),
        SettingField.Cpu => J(new object?[] { s.Resources.Cpus, s.Resources.CpuShares, s.Resources.CpusetCpus }),
        SettingField.PidsLimit => J(s.Resources.PidsLimit),
        SettingField.ShmSize => J(s.ShmSizeBytes),
        SettingField.Ulimits => J(s.Ulimits),
        SettingField.Health => J(s.Health),
        SettingField.Labels => J(new SortedDictionary<string, string>(s.Labels.Where(l => !l.Key.StartsWith("gitberth.", StringComparison.Ordinal)).ToDictionary(), StringComparer.Ordinal)),
        SettingField.Devices => J(s.Devices),
        SettingField.Gpus => s.Gpus ?? "",
        SettingField.Capabilities => J(new[] { s.CapAdd, s.CapDrop }),
        SettingField.Privileged => J(s.Privileged),
        SettingField.SecurityOpt => J(s.SecurityOpt),
        SettingField.ReadOnlyRootfs => J(s.ReadOnlyRootfs),
        SettingField.Init => J(s.Init),
        SettingField.Sysctls => J(new SortedDictionary<string, string>(s.Sysctls.ToDictionary(), StringComparer.Ordinal)),
        SettingField.Logging => J(new object?[] { s.Logging.Driver, s.Logging.Options is { } o ? new SortedDictionary<string, string>(o.ToDictionary(), StringComparer.Ordinal) : null }),
        SettingField.Stop => J(new object?[] { s.StopSignal, s.StopTimeoutSeconds }),
        SettingField.Terminal => J(new[] { s.Tty, s.OpenStdin }),
        SettingField.Image => s.Image,
        SettingField.NetworkMode => s.NetworkMode,
        _ => "",
    };

    private static string J<T>(T value) => JsonSerializer.Serialize(value);

    private static PortRule RuleOf(PortMapping p) => new(p.ContainerPort, p.Protocol, p.Access, p.HostPort, p.HostIp);

    // Variables are updated in place (the (AppId, Key) index is unique); a value is encrypted again only when it changed.
    private static void ApplyEnv(AppDefinition app, IReadOnlyList<EnvSetting> env, SecretProtector protector)
    {
        var rows = env.Where(e => !e.FromImage && !string.IsNullOrWhiteSpace(e.Key)).ToList();
        var keep = rows.Select(e => e.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        app.EnvVars.RemoveAll(v => !keep.Contains(v.Key));
        foreach (var e in rows)
        {
            var key = e.Key.Trim();
            var existing = app.EnvVars.FirstOrDefault(v => v.Key == key);
            if (existing is null) app.EnvVars.Add(existing = new EnvVar { Key = key });
            if (existing.ProtectedValue.Length == 0 || protector.TryUnprotect(existing.ProtectedValue) != e.Value) existing.ProtectedValue = protector.Protect(e.Value);
            // Marked secret, or it looks it (a secret-sounding name, a URL with a password): the deploy keeps it off argv and out of its log.
            existing.IsSecret = e.Secret || Masking.IsSecret(key, e.Value);
            existing.BuildTime = e.BuildTime;
        }
    }

    // The web marker stays on its container port: it's Gitberth's (AppOptions.WebPort), not the container's.
    private static void ApplyPorts(AppDefinition app, IReadOnlyList<PortRule> rules)
    {
        var web = PortRules.WebOf(app.Ports);
        var rows = rules.Select(r => new PortRow
        {
            ContainerPort = r.ContainerPort,
            Protocol = r.Protocol,
            Access = r.Access,
            HostPort = r.HostPort,
            HostIp = r.Access == PortAccess.Custom ? r.HostIp : null,
            IsWeb = web is not null && r.ContainerPort == web.ContainerPort && PortRules.ProtocolOf(r.Protocol) == "tcp",
        }).ToList();
        // Only the first row of the web port carries it.
        var marked = false;
        foreach (var row in rows.Where(r => r.IsWeb))
        {
            row.IsWeb = !marked;
            marked = true;
        }
        app.Ports.Clear();
        app.Ports.AddRange(PortRules.ToMappings(rows));
    }

    // Volumes and binds; a volume named for the app (gb-<slug>-data) is stored by its short name, as every deploy names it.
    private static void ApplyVolumes(AppDefinition app, IReadOnlyList<MountSetting> mounts)
    {
        var prefix = $"gb-{app.Slug}-";
        app.Volumes.Clear();
        foreach (var m in mounts.Where(m => m.Kind != MountKind.Tmpfs && m.Target.Length > 0))
            app.Volumes.Add(new VolumeMount
            {
                Source = m.Kind == MountKind.Volume && m.Source.StartsWith(prefix, StringComparison.Ordinal) ? m.Source[prefix.Length..] : m.Source,
                Target = m.Target,
                ReadOnly = m.ReadOnly,
            });
    }
}
