using System.Collections.Concurrent;

namespace Gitberth.Services;

/// <summary>
/// Named locks for containers Gitberth didn't deploy, by container name. A port change (which recreates the container),
/// start, stop, restart, remove, joining and leaving a network each hold the container's lock, so none of them runs in the
/// middle of another: a scheduled restart during a recreate would start the old container next to the new one, on the
/// same volume. A scheduled restart that finds the lock taken waits for its next tick instead.
/// </summary>
public sealed class ContainerLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>The container's lock, once whatever holds it now is done.</summary>
    public async Task<IDisposable> AcquireAsync(string name, CancellationToken ct = default)
    {
        var gate = Gate(name);
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <summary>The container's lock when nothing holds it right now, else null.</summary>
    public async Task<IDisposable?> TryAcquireAsync(string name) =>
        await Gate(name).WaitAsync(0) ? new Releaser(Gate(name)) : null;

    /// <summary>Whether something is changing the container right now, for disabling buttons.</summary>
    public bool IsHeld(string name) => _locks.TryGetValue(name, out var gate) && gate.CurrentCount == 0;

    private SemaphoreSlim Gate(string name) => _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
