namespace Gitberth.Data;

// What an app's row keeps for an uploaded folder (its source), its release command, and the tables of its scheduled
// commands, their runs and its config files. Every enum here is stored as a string: members are only ever appended.

public partial class AppDefinition
{
    /// <summary>
    /// An uploaded app's newest upload, which Deploy builds: the name it came with (site.zip, or a folder's name), its
    /// archive's SHA-256 (hex; the file is /data/uploads/&lt;id&gt;/&lt;sha&gt;.&lt;zip|tar|tgz&gt;), its size, when and who.
    /// Null for any other app.
    /// </summary>
    public string? UploadName { get; set; }
    public string? UploadSha { get; set; }
    public long? UploadSize { get; set; }
    public DateTime? UploadedAt { get; set; }
    public string? UploadedBy { get; set; }

    /// <summary>
    /// Run once per new version before it takes over (npm run migrate), in a one-off container of the new image with the
    /// app's variables, volumes and networks. A non-zero exit or a timeout fails the deploy, and the running version stays.
    /// Null: none.
    /// </summary>
    public string? ReleaseCommand { get; set; }
    /// <summary>How long the release command may take, in minutes (1–60); null: 10.</summary>
    public int? ReleaseTimeoutMinutes { get; set; }
    /// <summary>A Compose app's service the release command runs in; null: its web service.</summary>
    public string? ReleaseService { get; set; }

    /// <summary>It came as an uploaded folder or archive, deployed as a Git app is, without a repo.</summary>
    public bool FromUpload => Source == Apps.AppSource.Upload;

    /// <summary>Gitberth builds its image from source: a repo's, or an uploaded folder's (one container or a Compose project).</summary>
    public bool Built => Source is Apps.AppSource.Git or Apps.AppSource.Upload;
}

/// <summary>Where a scheduled command runs: in the app's running container (docker exec), or in a new one of its image.</summary>
public enum CommandPlace { Exec, NewContainer }

/// <summary>How a run of a scheduled command ended (Running while it runs).</summary>
public enum CommandRunStatus { Running, Succeeded, Failed, TimedOut, Skipped }

/// <summary>What started a run: its schedule, or someone's Run now.</summary>
public enum CommandTrigger { Schedule, RunNow }

/// <summary>A command an app runs on a schedule (its Jobs card), in Gitberth's time zone.</summary>
public class ScheduledCommand
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>A cron expression (minute hour day month weekday), as the card's choices write it.</summary>
    public string Schedule { get; set; } = "";
    public string Command { get; set; } = "";
    public CommandPlace Place { get; set; } = CommandPlace.Exec;
    /// <summary>A Compose app's service it runs in; null: its web service.</summary>
    public string? Service { get; set; }
    public int TimeoutMinutes { get; set; } = 30;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One run of a scheduled command: the last 20 of each are kept.</summary>
public class CommandRun
{
    public int Id { get; set; }
    public int CommandId { get; set; }
    public int AppId { get; set; }
    public CommandTrigger Trigger { get; set; }
    public CommandRunStatus Status { get; set; } = CommandRunStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>The last 64 KB of what it printed, masked.</summary>
    public string? Output { get; set; }
    /// <summary>Why it didn't run or didn't end as a command does (a timeout, no container, skipped): one sentence.</summary>
    public string? Error { get; set; }
    /// <summary>Who asked for a Run now, as the audit log names them; null for the schedule's.</summary>
    public string? RequestedBy { get; set; }
}

/// <summary>
/// A file Gitberth keeps for an app and mounts read-only at a path in its container (its Settings › Config files): written
/// under /data/configs/&lt;appId&gt;/, its contents encrypted here.
/// </summary>
public class ConfigFile
{
    public int Id { get; set; }
    public int AppId { get; set; }
    /// <summary>The absolute path in the container, like /etc/nginx/conf.d/site.conf.</summary>
    public string Path { get; set; } = "";
    /// <summary>The contents, encrypted with SecretProtector.</summary>
    public string ContentProtected { get; set; } = "";
    /// <summary>Masked in the UI, and never shown again once saved.</summary>
    public bool Secret { get; set; }
    /// <summary>Its mode, as a number (420 is 0644).</summary>
    public int Mode { get; set; } = 420;
    /// <summary>A Compose app's service it's mounted into; null: its web service.</summary>
    public string? Service { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
