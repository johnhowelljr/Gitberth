using System.Collections.Concurrent;
using System.Globalization;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>
/// Keeps containers on the networks Gitberth joined them to (7.1, 8.3). Docker forgets a network when something recreates
/// a container (docker run, compose up, an image update), so when a container starts, each network Gitberth keeps it on
/// that it's missing is joined again (<see cref="NetworkService.RejoinAsync"/>): with the same alias, at gateway priority
/// -1, and on the same own LAN address when one is pinned. One that Gitberth is changing right then is looked at once
/// that's done. At startup, after the port changes a restart cut short are settled, every container Gitberth keeps
/// networks for is looked at; every ten minutes after, each one that started since it was last looked at, in case that
/// start went by unheard. A network taken off one by hand stays off until its next start, as its Networks card says.
/// What was kept for a container that's gone at two looks in a row is forgotten.
/// </summary>
public sealed class NetworkKeeper(NetworkService network, DockerApi api, ContainerEventWatcher events, ContainerLocks locks,
    IDbContextFactory<GitberthDb> dbFactory, ILogger<NetworkKeeper> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    // One look per container at a time: the starts of a restart loop don't pile up.
    private readonly ConcurrentDictionary<string, byte> _looking = new(StringComparer.Ordinal);
    // Each failure is said once, then quietly: a kept network someone removed fails at every start.
    private readonly ConcurrentDictionary<string, byte> _said = new(StringComparer.Ordinal);
    // The start each container was last looked at for, heard or found: the timer looks again only after a later one.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _looked = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reconciling = new(1, 1);
    // The containers Gitberth kept something for that were gone at the last look.
    private HashSet<string> _gone = new(StringComparer.Ordinal);
    // When the last look began; null before the first.
    private DateTimeOffset? _lastLook;
    private CancellationToken _stopping;

    /// <summary>How long a start waits for Gitberth to finish changing the container before it's looked at anyway.</summary>
    internal TimeSpan BusyWait { get; init; } = TimeSpan.FromMinutes(2);

    internal TimeSpan BusyPoll { get; init; } = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        events.Started += OnStarted;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Every, stoppingToken);
                try
                {
                    await ReconcileAsync(startsOnly: true, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Checking the networks Gitberth keeps containers on failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            events.Started -= OnStarted;
        }
    }

    // Off the event loop, which goes on to the next event at once.
    private void OnStarted(string container)
    {
        if (!_looking.TryAdd(container, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await KeepAsync(container, _stopping);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Joining {Container} to its kept networks again failed", container);
            }
            finally
            {
                _looking.TryRemove(container, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Joins the container again to each network Gitberth keeps it on that it's missing. While Gitberth is changing it
    /// (a port change carries its networks over itself, a start is over in a moment) it's looked at once that's done.
    /// The start it was looked at for is remembered, so the timer doesn't look again until there's another.
    /// Throws when Docker can't be read.
    /// </summary>
    internal async Task<IReadOnlyList<NetworkChange>> KeepAsync(string container, CancellationToken ct)
    {
        for (var waited = TimeSpan.Zero; locks.IsHeld(container) && waited < BusyWait; waited += BusyPoll) await Task.Delay(BusyPoll, ct);
        // Still held: the rejoin below leaves it alone, so this start isn't looked at yet.
        var started = locks.IsHeld(container) ? null : await StartedAtAsync(container, ct);
        var results = await network.RejoinAsync(container, ct);
        if (started is { } at) _looked[container] = at;
        foreach (var r in results)
        {
            if (r.Ok) logger.LogInformation("{Done}", r.Done);
            else if (_said.TryAdd(r.Error!, 0)) logger.LogWarning("{Error}", r.Error);
            else logger.LogDebug("{Error}", r.Error);
        }
        return results;
    }

    /// <summary>
    /// At startup: looks at every container Gitberth keeps networks or port notes for, and every app's container with
    /// networks saved for it: each that exists is joined again to what it's missing, since starts went by while Gitberth
    /// was away. What was kept for a container that's gone now, and was gone at the last look too, is forgotten (its
    /// networks and port notes): removed outside Gitberth and not made again since, it isn't coming back as itself. An
    /// app's networks are the app's, and stay with it.
    /// </summary>
    public Task ReconcileAsync(CancellationToken ct = default) => ReconcileAsync(startsOnly: false, ct);

    /// <param name="startsOnly">
    /// On the timer: only a container that started after the start it was last looked at for, or (never looked at) after
    /// the last look began, is joined again: a start the event watcher missed. One up since then that isn't on a network
    /// Gitberth keeps it on was taken off it by hand, and stays off until its next start. The first look is a whole one.
    /// </param>
    internal async Task ReconcileAsync(bool startsOnly, CancellationToken ct)
    {
        await _reconciling.WaitAsync(ct);
        try
        {
            var at = DateTimeOffset.UtcNow;
            List<string> kept;
            List<string> apps;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                kept = (await db.KeptNetworks.AsNoTracking().Select(k => k.ContainerName).ToListAsync(ct))
                    .Concat(await db.ContainerPortNotes.AsNoTracking().Select(n => n.ContainerName).ToListAsync(ct))
                    .Distinct(StringComparer.Ordinal).ToList();
                // gb-<slug> apps, from any source; an Existing app's networks are kept by its container's name (above).
                apps = await db.Apps.AsNoTracking().Where(a => a.Container == null && a.Networks.Any()).Select(a => a.Slug).ToListAsync(ct);
            }
            var present = (await api.ListContainersAsync(ct)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var name in kept.Concat(apps.Select(AppDefinition.ContainerNameFor)).Distinct(StringComparer.Ordinal).Where(present.Contains))
            {
                try
                {
                    if (startsOnly && _lastLook is { } last && !await StartedSinceAsync(name, last, ct)) continue;
                    await KeepAsync(name, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Joining {Container} to its kept networks again failed", name);
                }
            }

            var gone = kept.Where(n => !present.Contains(n)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in gone.Where(_gone.Contains).ToList())
            {
                // Not while a change of the name isn't settled: what's kept is then its original's (it's looked at again next time).
                if (!await network.ForgetAsync(name, ct)) continue;
                gone.Remove(name);
                _looked.TryRemove(name, out _);
                logger.LogInformation("{Container} is gone, so Gitberth no longer keeps its networks or port notes", name);
            }
            _gone = gone;
            _lastLook = at;
        }
        finally
        {
            _reconciling.Release();
        }
    }

    // Whether the container started after the start it was last looked at for or, never looked at, after the last look
    // began. One that didn't is remembered as looked at for the start it has.
    private async Task<bool> StartedSinceAsync(string container, DateTimeOffset lastLook, CancellationToken ct)
    {
        if (await StartedAtAsync(container, ct) is not { } started) return false;
        if (started > (_looked.TryGetValue(container, out var seen) ? seen : lastLook)) return true;
        _looked[container] = started;
        return false;
    }

    // When the container last started, as Docker says; null when it never has, or isn't there.
    private async Task<DateTimeOffset?> StartedAtAsync(string container, CancellationToken ct) =>
        await api.InspectContainerAsync(container, ct) is { } inspect
        && DateTimeOffset.TryParse(ContainerPorts.Str(inspect["State"], "StartedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
        && at.Year > 1
            ? at
            : null;
}
