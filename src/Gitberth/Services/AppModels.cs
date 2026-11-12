using System.Globalization;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;

namespace Gitberth.Services;

public sealed class EnvRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public bool BuildTime { get; set; }
    /// <summary>Where a suggested variable came from; shown under the value.</summary>
    public string? Hint { get; set; }
}

/// <summary>A row of an app's Ports table, as the UI binds it.</summary>
public sealed class PortRow
{
    public int? HostPort { get; set; }
    public int? ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    /// <summary>Who reaches it. New rows start as LAN; Custom is only ever a row kept from before (one address of the host).</summary>
    public PortAccess Access { get; set; } = PortAccess.Lan;
    /// <summary>The address a Custom row is published on; nothing for the other kinds.</summary>
    public string? HostIp { get; set; }
    /// <summary>The web port: at most one row, and only a TCP one.</summary>
    public bool IsWeb { get; set; }

    public static PortRow From(PortMapping p) => new()
    {
        HostPort = p.HostPort, ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = p.Access, HostIp = p.HostIp, IsWeb = p.IsWeb,
    };

    /// <summary>What the container is created with for the row: what its access doesn't use (an Internal row's host port, a LAN row's address) doesn't count.</summary>
    internal string Binding =>
        $"{Access}:{(Access == PortAccess.Custom ? HostIp?.Trim() : "")}:{(Access == PortAccess.Internal ? null : HostPort)}:{ContainerPort}/{PortRules.ProtocolOf(Protocol)}";

    /// <summary>The row as <see cref="AppSettingsModel.PortsKey(IEnumerable{PortRow})"/> compares it: its binding and its web marker.</summary>
    internal string Key => IsWeb ? Binding + ":web" : Binding;
}

public sealed class VolumeRow
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
}

/// <summary>Everything the user can edit about an app, as the UI binds it.</summary>
public sealed class AppSettingsModel
{
    public string Name { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string? Subdirectory { get; set; }
    public DockerfileMode DockerfileMode { get; set; }
    public string? CustomDockerfile { get; set; }
    public string? ProjectHint { get; set; }
    public string RestartPolicy { get; set; } = "unless-stopped";
    public int? MemoryLimitMb { get; set; }
    public double? CpuLimit { get; set; }
    /// <summary>
    /// A new Git app's limits as New app's Resources fields give them (memory, CPUs and their priority, processes, shared
    /// memory); <see cref="AppService.CreateAsync"/> stores them as its Settings tab would. Null: just the two above.
    /// </summary>
    public Gitberth.Apps.ContainerSettings? Limits { get; set; }
    public string? HealthCheckPath { get; set; }
    public bool AutoDeploy { get; set; } = true;
    /// <summary>Whether Gitberth sets Vercel's variables for the app (see VercelEnv).</summary>
    public bool VercelVariables { get; set; } = true;
    /// <summary>Whether the Health page gathers this app's CPU, network and memory. Doesn't affect the container.</summary>
    public bool GatherStats { get; set; } = true;
    /// <summary>GatherStats as it was loaded, so a save only writes it when it was changed here, not over a newer choice made on the Health page.</summary>
    public bool GatherStatsLoaded { get; set; } = true;
    /// <summary>How often Gitberth restarts the container, if at all; see <see cref="RestartSchedules"/>. Doesn't affect the container's settings.</summary>
    public RestartChoice RestartEvery { get; set; } = RestartChoice.Never;
    public int? RestartAmount { get; set; }
    /// <summary>The schedule as it was loaded, in minutes, so a save only writes it when it was changed here.</summary>
    public int? RestartLoaded { get; set; }

    /// <summary>The scheduled restart chosen, or null for never.</summary>
    public RestartInterval? RestartInterval => RestartEvery switch
    {
        RestartChoice.Minutes => new(RestartAmount ?? 0, RestartUnit.Minutes),
        RestartChoice.Hours => new(RestartAmount ?? 0, RestartUnit.Hours),
        RestartChoice.Days => new(RestartAmount ?? 0, RestartUnit.Days),
        _ => null,
    };
    /// <summary>
    /// The app's ports. Only a new app's are saved with its settings (<see cref="AppService.CreateAsync"/>); an existing
    /// app's are saved on their own (<see cref="AppService.SavePortsAsync"/>), so the Settings tab's Save never touches them.
    /// </summary>
    public List<PortRow> Ports { get; set; } = [];
    /// <summary>The ports as they were loaded (<see cref="PortsKey(IEnumerable{PortMapping})"/>), for <see cref="AppService.SavePortsAsync"/>.</summary>
    public string? PortsLoaded { get; set; }
    public List<EnvRow> Env { get; set; } = [];
    public List<VolumeRow> Volumes { get; set; } = [];

    /// <summary>
    /// The rows as one string, to tell whether they changed: each row's access, address (Custom only), host port (not for an
    /// Internal row), container port, protocol and web marker. A row without a container port isn't a port yet.
    /// </summary>
    public static string PortsKey(IEnumerable<PortRow> rows) =>
        string.Join('|', rows.Where(p => p.ContainerPort is not null).Select(p => p.Key));

    public static string PortsKey(IEnumerable<PortMapping> ports) => PortsKey(ports.OrderBy(p => p.Id).Select(PortRow.From));
}

/// <summary>Vercel's variables as Gitberth adds them to an app.</summary>
/// <param name="Added">What Gitberth sets, as the app's next deploy would.</param>
/// <param name="Yours">Vercel names the app sets itself, so Gitberth leaves them alone.</param>
/// <param name="Off">They're turned off for the app.</param>
public sealed record VercelView(IReadOnlyList<EnvValue> Added, IReadOnlyList<string> Yours, bool Off);

/// <summary>What a scheduled-restart picker offers: never, or every so many minutes, hours or days.</summary>
public enum RestartChoice { Never, Minutes, Hours, Days }

public static class RestartChoices
{
    public static (RestartChoice Every, int? Amount) From(RestartInterval? interval) => interval switch
    {
        { Unit: RestartUnit.Minutes } i => (RestartChoice.Minutes, i.Amount),
        { Unit: RestartUnit.Hours } i => (RestartChoice.Hours, i.Amount),
        { Unit: RestartUnit.Days } i => (RestartChoice.Days, i.Amount),
        _ => (RestartChoice.Never, null),
    };

    public static string Label(RestartChoice choice) => choice switch
    {
        RestartChoice.Minutes => "Every N minutes",
        RestartChoice.Hours => "Every N hours",
        RestartChoice.Days => "Every N days",
        _ => "Never",
    };
}

/// <param name="NeedsRestart">Runtime settings changed; recreate the container to apply them.</param>
/// <param name="NeedsRebuild">Build inputs changed (branch, Dockerfile, build-time variables, volumes); deploy to apply them.</param>
public sealed record SaveOutcome(bool NeedsRestart, bool NeedsRebuild);

/// <summary>What saving an app's ports did (<see cref="AppService.SavePortsAsync"/>, <see cref="AppActions.ApplyPortsAsync"/>).</summary>
/// <param name="Problems">Why nothing was saved, each under the field it concerns; empty once they're saved.</param>
/// <param name="NeedsRestart">
/// The container takes the change only when it's recreated: a port's access, host port, address, container port or
/// protocol changed. False when only the web marker moved, which domains follow at once, or when nothing changed.
/// </param>
/// <param name="Domains">The domains' sync after the save; null when the app has none, or domains are off.</param>
/// <param name="DeploymentId">The deploy without a build that makes the change live now; null when none was started.</param>
public sealed record PortsSaved(IReadOnlyList<PortProblem> Problems, bool NeedsRestart, SyncReport? Domains = null, int? DeploymentId = null)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>The name a new app answers to on the shared network: its slug.</summary>
/// <param name="Taken">Why the name asked for wasn't free, for "Network name postgres-2 (postgres is taken on gitberth)"; null when it was.</param>
public sealed record NetworkName(string Slug, string? Taken);

public enum AppRunState { NotDeployed, Deploying, Running, Restarting, Stopped, Crashed, Missing, Unknown }

public sealed record AppStatusInfo(AppRunState State, string Label, string CssClass, string? Detail = null);

/// <param name="Deployed">A Git app, deployed from its repo: a scheduled restart recreates it through a deploy (RestartScheduler).</param>
public sealed record NavApp(int Id, string Name, string Slug, bool Deployed = false);

public sealed record AppDetail(AppDefinition App, AppStatusInfo Status, List<Deployment> Deployments, ContainerSummary? Container, string? DockerError);

public static class AppStatusResolver
{
    public static AppStatusInfo Resolve(AppDefinition app, ContainerSummary? container, Deployment? active, bool dockerReachable)
    {
        if (active is not null)
        {
            var label = active.Status switch
            {
                DeployStatus.Cloning => "Cloning…",
                DeployStatus.Building => "Building…",
                DeployStatus.Starting => "Starting…",
                _ => "Queued…",
            };
            return new AppStatusInfo(AppRunState.Deploying, label, "info", container?.Status);
        }
        if (!dockerReachable) return new AppStatusInfo(AppRunState.Unknown, "Unknown", "secondary", "Docker isn't reachable");
        if (container is null)
        {
            if (app.CurrentImage is null) return new AppStatusInfo(AppRunState.NotDeployed, "Not deployed", "secondary");
            return app.DesiredState == DesiredState.Stopped
                ? new AppStatusInfo(AppRunState.Stopped, "Stopped", "secondary")
                : new AppStatusInfo(AppRunState.Missing, "Container missing", "warning", "The container was removed outside Gitberth. Start the app to recreate it.");
        }
        return container.State switch
        {
            "running" => new AppStatusInfo(AppRunState.Running, "Running", "success", container.Status),
            "restarting" => new AppStatusInfo(AppRunState.Restarting, "Restarting", "warning", container.Status),
            _ when app.DesiredState == DesiredState.Stopped => new AppStatusInfo(AppRunState.Stopped, "Stopped", "secondary", container.Status),
            _ => new AppStatusInfo(AppRunState.Crashed, "Exited", "danger", container.Status),
        };
    }
}

public static class Format
{
    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public static string Duration(DateTime? start, DateTime? end)
    {
        if (start is null) return "";
        var d = (end ?? DateTime.UtcNow) - start.Value;
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{Math.Max(0, d.Seconds)}s";
    }

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>CPU on the `docker stats` scale, where 100% is one core.</summary>
    public static string Cpu(double percent) => percent switch
    {
        < 0.005 => "0%",
        < 1 => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
        < 10 => percent.ToString("0.#", CultureInfo.InvariantCulture) + "%",
        _ => percent.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>Network rates in decimal units and memory in binary units, like `docker stats`.</summary>
    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond, 1000, ["B", "kB", "MB", "GB"]) + "/s";

    public static string Memory(double bytes) => Bytes(bytes, 1024, ["B", "KiB", "MiB", "GiB", "TiB"]);

    private static string Bytes(double value, double step, string[] units)
    {
        var unit = 0;
        while (Math.Round(value) >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        // Round first, so 9.97 kB shows as "10 kB" rather than "10.0 kB".
        var text = Math.Round(value, 1) < 10 && unit > 0 ? value.ToString("0.#", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }

    public static string Trigger(DeployTrigger trigger) => trigger switch
    {
        DeployTrigger.Created => "First deploy",
        DeployTrigger.Manual => "Manual",
        DeployTrigger.Start => "Start",
        DeployTrigger.ConfigChange => "Settings change",
        DeployTrigger.Rollback => "Rollback",
        DeployTrigger.Poll => "New commit",
        DeployTrigger.Retry => "Automatic retry",
        DeployTrigger.Restart => "Scheduled restart",
        DeployTrigger.Webhook => "Push on GitHub",
        DeployTrigger.Promote => "Promoted",
        _ => trigger.ToString(),
    };

    /// <summary>Up to two initials for an app avatar.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)],
            _ => $"{parts[0][0]}{parts[1][0]}",
        };
        return initials.ToUpperInvariant();
    }
}
