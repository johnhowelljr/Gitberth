using System.Collections.Immutable;
using Gitberth.Docker;

namespace Gitberth.Health;

/// <summary>
/// Follows Docker's event stream for containers starting, stopping, crashing, restarting and changing health, and keeps
/// the last hours of it in memory and on disk (StatsStore) for the Health page's charts. After a restart of Gitberth it
/// reads the saved events back, then asks the engine for the ones since, which it remembers for a while. Each start it
/// hears of is passed on (<see cref="Started"/>): Gitberth joins a recreated container to its kept networks again, and
/// checks a recreated stopped container's first start.
/// </summary>
public sealed class ContainerEventWatcher(DockerApi docker, StatsStore store, ILogger<ContainerEventWatcher> logger) : BackgroundService
{
    /// <summary>An hour more than the charts show, to know how each container stood when they start.</summary>
    public static readonly TimeSpan Kept = ContainerStatsCollector.Kept + TimeSpan.FromHours(1);

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    private ImmutableList<ContainerEvent> _events = ImmutableList<ContainerEvent>.Empty;
    // The engine repeats events when asked for the ones since a time. Only the watching loop touches this.
    private readonly HashSet<(DateTime Time, string Id, string Action)> _seen = [];

    /// <summary>The events kept, oldest first. Replaced as a whole, so it's safe to read from any thread.</summary>
    public IReadOnlyList<ContainerEvent> Events => _events;

    /// <summary>
    /// A container started, by its name then: raised on the watching loop for each start it hears of, as it happens or,
    /// after Gitberth was away, from the ones the engine remembers. A handler hands its work off and returns at once, so
    /// the next event isn't held up; one that throws is logged and the loop goes on.
    /// </summary>
    public event Action<string>? Started;

    /// <summary>
    /// Every event it hears of for the first time, as it happens or replayed after Gitberth was away (so a handler looks at
    /// <see cref="ContainerEvent.Time"/>). Same rules as <see cref="Started"/>: hand the work off and return at once.
    /// </summary>
    public event Action<ContainerEvent>? Happened;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var e in await store.LoadEventsAsync(DateTime.UtcNow - Kept, stoppingToken)) Remember(e, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the saved container events");
        }

        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // From the newest event on record, so nothing that happened while Gitberth (or the stream) was away is missed.
            var since = _events.Count > 0 ? _events[^1].Time : DateTime.UtcNow - Kept;
            try
            {
                await foreach (var e in docker.StreamEventsAsync(since, stoppingToken))
                {
                    if (failing) logger.LogInformation("Following Docker's events again");
                    failing = false;
                    if (!Heard(e, DateTime.UtcNow)) continue;
                    try
                    {
                        await store.AddEventsAsync([e], stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Couldn't save a container event");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The engine restarting, say: said once, then retried quietly.
                if (!failing) logger.LogWarning("Following Docker's events stopped ({Reason}); trying again every {Seconds} seconds", ex.Message, Retry.TotalSeconds);
                failing = true;
            }
            try
            {
                await Task.Delay(Retry, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>An event from the stream: remembered, and a start that's new passed on to <see cref="Started"/>. True when it was new.</summary>
    internal bool Heard(ContainerEvent e, DateTime now)
    {
        if (!Remember(e, now)) return false;
        if (Happened is { } happened)
            foreach (var handler in happened.GetInvocationList().Cast<Action<ContainerEvent>>())
            {
                try
                {
                    handler(e);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Handling an event of {Container} failed", e.Name);
                }
            }
        if (e.Action == "start" && Started is { } started)
            // Each on its own: one that throws doesn't keep the start from the others.
            foreach (var handler in started.GetInvocationList().Cast<Action<string>>())
            {
                try
                {
                    handler(e.Name);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Handling the start of {Container} failed", e.Name);
                }
            }
        return true;
    }

    /// <summary>Adds an event unless it's known already, and forgets the ones too old to show. True when it was new.</summary>
    internal bool Remember(ContainerEvent e, DateTime now)
    {
        var cutoff = now - Kept;
        if (e.Time < cutoff || !_seen.Add((e.Time, e.Id, e.Action))) return false;
        var list = _events;
        if (list.Count > 0 && list[0].Time < cutoff)
        {
            list = list.RemoveAll(x => x.Time < cutoff);
            _seen.RemoveWhere(k => k.Time < cutoff);
        }
        // Usually the newest; one the engine replays after a reconnect can be older.
        var at = list.Count;
        while (at > 0 && list[at - 1].Time > e.Time) at--;
        _events = list.Insert(at, e);
        return true;
    }
}
