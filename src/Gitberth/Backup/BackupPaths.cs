namespace Gitberth.Backup;

/// <summary>Where save and restore keep their few files, under Gitberth's data folder.</summary>
public sealed class BackupPaths(GitberthOptions options)
{
    /// <summary>/data/backup: the lists a save writes before it stops or pauses containers, and its SQLite snapshots for a moment.</summary>
    public string BackupDir => Path.Combine(options.DataDir, "backup");

    /// <summary>The containers a "stop everything" save stopped: started again when it ends, or at the next start.</summary>
    public string StoppedList => Path.Combine(BackupDir, "stopped.json");

    /// <summary>The containers paused for a volume's copy: unpaused when it ends, or at the next start.</summary>
    public string PausedList => Path.Combine(BackupDir, "paused.json");

    /// <summary>/data/restore: the upload, Gitberth's staged data, the plan and the log.</summary>
    public string RestoreDir => Path.Combine(options.DataDir, "restore");

    public string Upload => Path.Combine(RestoreDir, "upload.gbak");
    public string UploadPart => Path.Combine(RestoreDir, "upload.gbak.part");

    /// <summary>Gitberth's data from the backup, put in place at the next start, before the database is opened.</summary>
    public string Staged => Path.Combine(RestoreDir, "staged");

    /// <summary>Written last into <see cref="Staged"/>: only a complete staging is put in place.</summary>
    public string StagedReady => Path.Combine(Staged, ".ready");

    /// <summary>What the staged data replaced (the fresh install's), kept until the next restore.</summary>
    public string Before => Path.Combine(RestoreDir, "before");

    /// <summary>The restore's steps and how each ended: a restore that stopped runs again from it, skipping what's done.</summary>
    public string Plan => Path.Combine(RestoreDir, "plan.json");

    /// <summary>The restore's log (secrets masked), kept across the restart.</summary>
    public string Log => Path.Combine(RestoreDir, "restore.log");

    /// <summary>Gitberth's own files and folders a backup holds, as they're named under the data folder.</summary>
    public static readonly IReadOnlyList<string> DataFiles = ["gitberth.db", "stats.db", "uptime.db", "audit.db"];

    public static readonly IReadOnlyList<string> DataFolders = ["keys", "compose", "logs"];
}
