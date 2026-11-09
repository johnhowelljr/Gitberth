using System.Collections.Concurrent;
using Gitberth.Data;
using Gitberth.Health;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Apps;

/// <summary>
/// Keeps the row of an app that isn't a Git app in step with its container when something else recreates it (docker
/// run, compose up, an image update by hand): on each start the event watcher hears, a container under an app's name with
/// a new ID gets its ports mirror, ID and saved settings read again (<see cref="AppCatalog.SyncAsync"/>), and the app's
/// Activity says it was recreated outside Gitberth, unless Gitberth made it: a change (its journal knows both IDs) or
/// Gitberth's own create. NetworkKeeper joins it to its kept networks again on the same start. A Git app's row is the
/// truth for its settings and its deploys make its containers, so it's left alone, and a container without a row is
/// nobody's record. Nothing here writes to Docker. Whenever a change of the change engine ends, however it ends (done, put
/// back, rolled back, kept, on a stopped container too), the row is synced to whatever holds the name then.
/// </summary>
/// <param name="changer">The change engine, whose ends it follows; without it (in tests) only starts are followed.</param>
public sealed class AppSync(ContainerEventWatcher events, AppCatalog catalog, IDbContextFactory<GitberthDb> dbFactory, IChangeJournal journal,
    NetworkService network, ActivityLog activity, ILogger<AppSync> logger, PortChanger? changer = null) : BackgroundService
{
    // One look per container at a time: the starts of a restart loop don't pile up.
    private readonly ConcurrentDictionary<string, byte> _looking = new(StringComparer.Ordinal);
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        events.Started += OnStarted;
        if (changer is not null) changer.Ended += OnEnded;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            events.Started -= OnStarted;
            if (changer is not null) changer.Ended -= OnEnded;
        }
    }

    // Off the change's thread, which goes on at once.
    private void OnEnded(ContainerChange change) => _ = Task.Run(async () =>
    {
        try
        {
            await EndedAsync(change, _stopping);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bringing {Container}'s row in line after its change failed", change.ContainerName);
        }
    }, CancellationToken.None);

    /// <summary>
    /// A change ended: the app's row follows the container that holds the name now (its ID, ports mirror and saved
    /// settings), whatever the end. An update that was kept records the image it ran before, for Roll back, even when
    /// Gitberth restarted in the middle of it; a rollback leaves nothing to go back to. True when a row was synced.
    /// </summary>
    internal async Task<bool> EndedAsync(ContainerChange change, CancellationToken ct)
    {
        if (await RowOfAsync(change.ContainerName, ct) is not { } row) return false;
        if (change.Outcome == ChangeOutcome.Done && change.Kind is ChangeKind.Update or ChangeKind.Rollback)
        {
            var previous = change.Kind == ChangeKind.Update && change.ImageBefore is { } before && before.StartsWith("sha256:", StringComparison.Ordinal) ? before : null;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (change.Kind == ChangeKind.Rollback || previous is not null)
                await db.Apps.Where(a => a.Id == row.Id && a.PreviousImage != previous).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviousImage, previous), ct);
        }
        await catalog.SyncAsync(row.Id, ct);
        return true;
    }

    private async Task<AppDefinition?> RowOfAsync(string container, CancellationToken ct)
    {
        var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Source != AppSource.Git && (a.Container == container || (a.Container == null && slug != null && a.Slug == slug)), ct);
    }

    // Off the event loop, which goes on to the next event at once.
    private void OnStarted(string container)
    {
        if (!_looking.TryAdd(container, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await SeenAsync(container, _stopping);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading {Container} again after its start failed", container);
            }
            finally
            {
                _looking.TryRemove(container, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// A container started: when it's a non-Git app's with an ID its row doesn't know, the row is synced, and the Activity
    /// says so unless Gitberth made it. True when the row was synced.
    /// </summary>
    internal async Task<bool> SeenAsync(string container, CancellationToken ct)
    {
        var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
        AppDefinition? row;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            row = await db.Apps.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Source != AppSource.Git && (a.Container == container || (a.Container == null && slug != null && a.Slug == slug)), ct);
        if (row is null) return false;
        var id = ContainerPorts.Str(await network.InspectAsync(container, ct), "Id");
        if (id.Length == 0 || id == row.ContainerId) return false;

        // Gitberth's own: the first container of an app it created, one it created again, or a change's new container.
        var ours = row.ContainerId is null || catalog.Created(container) || await journal.WithContainerAsync(id, ct) is not null;
        await catalog.SyncAsync(row.Id, ct);
        if (!ours)
        {
            logger.LogInformation("{Container} was recreated outside Gitberth; its ports and settings were read again", container);
            await activity.AddAsync(container, row.Id, ActivityKind.RecreatedOutside, "Recreated outside Gitberth: its ports and settings were read again.",
                ActivityTone.Warning, ct);
        }
        return true;
    }
}
