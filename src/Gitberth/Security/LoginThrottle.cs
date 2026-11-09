using System.Collections.Concurrent;

namespace Gitberth.Security;

/// <summary>Locks a client out for a while after repeated failed sign-ins.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart)> _state = new();

    public bool IsLocked(string client) =>
        _state.TryGetValue(client, out var s) && s.Failures >= MaxFailures && DateTime.UtcNow - s.WindowStart < Window;

    public void Fail(string client) =>
        _state.AddOrUpdate(client, _ => (1, DateTime.UtcNow), (_, s) =>
            DateTime.UtcNow - s.WindowStart >= Window ? (1, DateTime.UtcNow) : (s.Failures + 1, s.WindowStart));

    public void Succeed(string client) => _state.TryRemove(client, out _);
}
