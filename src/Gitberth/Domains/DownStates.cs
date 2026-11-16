using System.Collections.Concurrent;

namespace Gitberth.Domains;

/// <summary>Why an app's domains show Gitberth's down page.</summary>
public enum DownReason
{
    /// <summary>A deploy is replacing its container and has to stop the old one first.</summary>
    Updating,
    /// <summary>It was stopped on purpose (its desired state is Stopped).</summary>
    Stopped,
    /// <summary>Its container hasn't been running for a while: it crashed, keeps restarting, or was stopped outside Gitberth.</summary>
    NotRunning,
    /// <summary>A protected domain whose protection can't be applied right now: it's held back, and only the down page is served.</summary>
    Held,
}

/// <summary>
/// Which apps' domains show the down page now, and why: written by <see cref="DownWatcher"/> (stopped, not running) and by
/// a deploy's swap (updating), read by every domain sync (<see cref="DomainService"/>) and the public down page. It holds
/// no data of its own beyond this process: after a restart the watcher works the states out again.
/// </summary>
public sealed class DownStates
{
    private readonly ConcurrentDictionary<int, DownReason> _down = new();
    // How many swaps of the app want its domains on the down page right now (normally one).
    private readonly ConcurrentDictionary<int, int> _updating = new();

    /// <summary>
    /// Something changed what an app should show right now (it was stopped or started through Gitberth, its down page was
    /// turned on or off): the watcher looks again at once instead of at its next sweep.
    /// </summary>
    public event Action<int>? Nudged;

    /// <summary>Raised after any app's state changed, for open pages.</summary>
    public event Action? Changed;

    /// <summary>Why the app's domains show the down page now; null when they go to the app.</summary>
    public DownReason? For(int appId) =>
        _updating.ContainsKey(appId) ? DownReason.Updating : _down.TryGetValue(appId, out var reason) ? reason : null;

    /// <summary>Whether a deploy is replacing the app's container right now (<see cref="BeginUpdate"/>).</summary>
    public bool IsUpdating(int appId) => _updating.ContainsKey(appId);

    /// <summary>
    /// The watcher has looked at every app since Gitberth started; its first look asks for a sync whatever it found. A sync
    /// never takes a domain off the down page just because nothing is decided for its app yet (Gitberth has just started, or
    /// the container stopped less than <see cref="DownWatcher.Grace"/> ago): only once its down page is off, or can't be shown.
    /// </summary>
    public bool Ready { get; private set; }

    /// <summary>Called by the watcher once it has looked at every app.</summary>
    public void MarkReady() => Ready = true;

    /// <summary>The apps the watcher put on the down page, with why (a deploy's update not included).</summary>
    public IReadOnlyDictionary<int, DownReason> Watched => new Dictionary<int, DownReason>(_down);

    /// <summary>Sets what the watcher decided for the app; true when that changed anything.</summary>
    public bool Set(int appId, DownReason? reason)
    {
        bool changed;
        if (reason is { } r)
        {
            var before = _down.TryGetValue(appId, out var was) ? was : (DownReason?)null;
            _down[appId] = r;
            changed = before != r;
        }
        else
        {
            changed = _down.TryRemove(appId, out _);
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Forgets every app the watcher put on the down page (domains were turned off, say). True when there were any.</summary>
    public bool Clear()
    {
        if (_down.IsEmpty) return false;
        _down.Clear();
        Changed?.Invoke();
        return true;
    }

    /// <summary>A deploy's swap is about to stop the app's container: its domains go to the down page until <see cref="EndUpdate"/>.</summary>
    public void BeginUpdate(int appId)
    {
        _updating.AddOrUpdate(appId, 1, (_, n) => n + 1);
        Changed?.Invoke();
    }

    /// <summary>The swap is over, whichever way it went: the app's domains go back to it once it runs.</summary>
    public void EndUpdate(int appId)
    {
        while (_updating.TryGetValue(appId, out var n))
        {
            if (n <= 1 ? _updating.TryRemove(KeyValuePair.Create(appId, n)) : _updating.TryUpdate(appId, n - 1, n)) break;
        }
        Changed?.Invoke();
    }

    /// <summary>Asks the watcher to look at the app again now.</summary>
    public void Nudge(int appId) => Nudged?.Invoke(appId);
}
