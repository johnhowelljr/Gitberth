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
}
