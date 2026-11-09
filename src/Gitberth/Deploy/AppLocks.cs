using System.Collections.Concurrent;

namespace Gitberth.Deploy;

/// <summary>Serializes container changes (swap, start, stop, delete) per app.</summary>
public sealed class AppLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(int appId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <summary>The app's lock when nothing holds it right now, else null.</summary>
    public async Task<IDisposable?> TryAcquireAsync(int appId)
    {
        var gate = _locks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(0) ? new Releaser(gate) : null;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
