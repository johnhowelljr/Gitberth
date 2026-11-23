namespace Gitberth.Backup;

/// <summary>
/// The restore's boot step, before anything opens Gitberth's database or keys: when a restore's first phase staged
/// Gitberth's data (and marked it whole), the staged gitberth.db, keys, Health, uptime and audit history, Compose releases
/// and deploy logs replace the fresh install's, which are moved to /data/restore/before/. The migrations then run as usual,
/// so an older backup's database is brought up to date.
/// </summary>
/// <remarks>
/// It goes one item at a time, and only for an item still staged: what it replaces is moved aside, then the staged one in.
/// A crash in the middle leaves each item either still staged (swapped next time) or in place (left alone), so the next
/// start only finishes what's left, and never moves restored data aside.
/// </remarks>
public static class RestoreBoot
{
    // A database goes with its WAL, shared-memory and journal files.
    private static readonly string[] Companions = ["", "-wal", "-shm", "-journal"];

    /// <summary>Puts the staged data in place; returns what it did, or null when nothing was staged.</summary>
    public static string? SwapStaged(GitberthOptions options)
    {
        var paths = new BackupPaths(options);
        if (!File.Exists(paths.StagedReady)) return null;
        Directory.CreateDirectory(paths.Before);

        foreach (var file in BackupPaths.DataFiles)
        {
            var staged = Path.Combine(paths.Staged, file);
            if (!File.Exists(staged)) continue;
            foreach (var suffix in Companions)
            {
                var aside = Path.Combine(paths.Before, file + suffix);
                var live = Path.Combine(options.DataDir, file + suffix);
                if (File.Exists(aside)) File.Delete(aside);
                if (File.Exists(live)) File.Move(live, aside);
            }
            File.Move(staged, Path.Combine(options.DataDir, file));
        }
        foreach (var folder in BackupPaths.DataFolders)
        {
            var staged = Path.Combine(paths.Staged, folder);
            if (!Directory.Exists(staged)) continue;
            var aside = Path.Combine(paths.Before, folder);
            var live = Path.Combine(options.DataDir, folder);
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            if (Directory.Exists(live)) Directory.Move(live, aside);
            Directory.Move(staged, live);
        }
        foreach (var folder in BackupPaths.DataFolders) Directory.CreateDirectory(Path.Combine(options.DataDir, folder));
        Directory.Delete(paths.Staged, recursive: true);

        const string done = "Gitberth's data from the backup is in place (what it replaced is in /data/restore/before).";
        new RestoreLog(paths).Line(done);
        return done;
    }
}
