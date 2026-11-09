using System.Collections.Concurrent;

namespace Gitberth.Deploy;

/// <summary>In-memory copy of a running deploy's log that UI components can follow.</summary>
public sealed class LiveLog
{
    private const int MaxLines = 50_000;
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];
    private Action<string>? _lineAdded;
    private Action? _completed;

    public bool IsComplete { get; private set; }

    public void Append(string line)
    {
        Action<string>? handler;
        lock (_lock)
        {
            if (_lines.Count < MaxLines) _lines.Add(line);
            handler = _lineAdded;
        }
        handler?.Invoke(line);
    }

    public void Complete()
    {
        Action? handler;
        lock (_lock)
        {
            if (IsComplete) return;
            IsComplete = true;
            handler = _completed;
        }
        handler?.Invoke();
    }

    /// <summary>Delivers the lines so far, then each new line, without gaps or duplicates.</summary>
    public IDisposable Subscribe(Action<IReadOnlyList<string>> onSnapshot, Action<string> onLine, Action onComplete)
    {
        lock (_lock)
        {
            onSnapshot(_lines.ToList());
            if (IsComplete)
            {
                onComplete();
                return new Subscription(null);
            }
            _lineAdded += onLine;
            _completed += onComplete;
        }
        return new Subscription(() =>
        {
            lock (_lock)
            {
                _lineAdded -= onLine;
                _completed -= onComplete;
            }
        });
    }

    private sealed class Subscription(Action? unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe?.Invoke();
    }
}

public sealed class LiveLogHub
{
    private readonly ConcurrentDictionary<int, LiveLog> _logs = new();

    public LiveLog Open(int deploymentId) => _logs.GetOrAdd(deploymentId, _ => new LiveLog());

    public LiveLog? Get(int deploymentId) => _logs.TryGetValue(deploymentId, out var log) ? log : null;

    public void Close(int deploymentId)
    {
        if (!_logs.TryGetValue(deploymentId, out var log)) return;
        log.Complete();
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => _logs.TryRemove(deploymentId, out LiveLog? _), TaskScheduler.Default);
    }
}
