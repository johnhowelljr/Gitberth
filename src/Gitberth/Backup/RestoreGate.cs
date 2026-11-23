namespace Gitberth.Backup;

/// <summary>
/// The global "restoring" gate: while a restore runs (its first phase, and after the restart its second, until every app
/// is deployed again), Gitberth's background jobs hold off: the keep-running check, start-up's rebuilds, the repo and
/// update watchers, the network keeper, app sync, disk cleanup, previews and the domains' sync. Otherwise they'd make
/// missing apps again at their branch's tip, or tidy away what the restore is putting back. Open unless a restore holds it.
/// </summary>
public sealed class RestoreGate
{
    private volatile bool _holding;

    /// <summary>Open: nothing is being restored.</summary>
    public RestoreGate()
    {
    }

    /// <summary>Closed from the start when a restore's second phase is still to run (Gitberth restarted in the middle of it).</summary>
    public RestoreGate(GitberthOptions options) => _holding = RestorePlanState.Load(new BackupPaths(options)) is { PhaseOneDone: true, Finished: false };

    /// <summary>A restore runs: background jobs hold off.</summary>
    public bool Holding => _holding;

    public void Close() => _holding = true;

    public void Open() => _holding = false;

    // The apps a restore of picked items is bringing back, by slug: the keep-running check and the repo watcher skip them.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _apps = new(StringComparer.Ordinal);

    /// <summary>A picked app (by its slug here) is being restored: Gitberth's jobs leave it alone until it's released.</summary>
    public void Hold(string slug) => _apps[slug] = 0;

    public void Release(string slug) => _apps.TryRemove(slug, out _);

    /// <summary>Whether Gitberth's jobs leave the app alone now: a whole restore runs, or this app is being restored.</summary>
    public bool Holds(Data.AppDefinition app) => _holding || _apps.ContainsKey(app.Slug);
}
