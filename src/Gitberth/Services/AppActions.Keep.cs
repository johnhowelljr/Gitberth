using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Domains;
using Gitberth.Keeper;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>What the keep-running check asks of an app (<see cref="AppActions.BringBackAsync"/>).</summary>
public enum BringBackKind
{
    /// <summary>Start its existing container as it is (a Compose app: the services named).</summary>
    Start,
    /// <summary>Make its missing container again: from its image or current release, or from its saved settings.</summary>
    Recreate,
    /// <summary>Build it again from the commit it ran: its container and its image are both gone (Docker said so of the image).</summary>
    Build,
}

/// <param name="Services">A Compose app's services to start; null for every stopped one.</param>
public sealed record BringBackRequest(int AppId, BringBackKind Kind, IReadOnlyList<string>? Services = null);

/// <param name="Done">Something was done: a container started, or a deploy or a create queued (whose outcome comes later).</param>
/// <param name="Busy">Something else changes the app right now: nothing was done, and it isn't a try.</param>
/// <param name="Error">Why nothing started; the try counts.</param>
/// <param name="How">What it did, for its words.</param>
/// <param name="Image">The image a deploy without a build makes the container again from.</param>
public sealed record BringBackResult(bool Done, bool Busy, string? Error, int? DeploymentId = null, KeepHow How = KeepHow.Started, string? Image = null)
{
    public static readonly BringBackResult Waits = new(false, true, null);
}

/// <summary>
/// The keep-running check's way in: start an app's container as it is, make a missing one again, or build it again, with
/// the same care as a person's Start (the app's lock, a container's lock, a change that isn't settled, an old copy of a
/// database). Saved settings are never applied to a container that exists: they stay pending, as a scheduled restart
/// leaves them. Each asks again, under the lock it acts under, whether the app is still meant to run: a Stop, a deletion or
/// a Forget that came meanwhile wins. A deploy it queues is a Recover one, under the deploy gate, only while the app has
/// none queued or running and isn't being deleted, so it never takes another deploy's place in the queue; it never makes the
/// app meant to run (the deploy's swap reads a Stop that came meanwhile, and starts nothing). Docker is asked before the
/// gate is taken: a slow Docker never holds up other deploys or deletions.
/// </summary>
public sealed partial class AppActions
{
    /// <summary>Whether the app is being deleted (or forgotten) right now: its container may be gone a moment before its row.</summary>
    public bool IsDeleting(int appId) => _deleting.ContainsKey(appId);

    internal async Task<BringBackResult> BringBackAsync(BringBackRequest request, CancellationToken ct = default)
    {
        var app = await RowAsync(request.AppId, ct);
        if (app is null || app.DesiredState != DesiredState.Running || IsDeleting(app.Id)) return BringBackResult.Waits;
        return request.Kind switch
        {
            BringBackKind.Start when !app.Deployed => await StartContainerAsync(app, ct),
            BringBackKind.Start when app.IsCompose => await StartServicesAsync(app, request.Services, ct),
            BringBackKind.Start => await StartGitContainerAsync(app, ct),
            BringBackKind.Recreate when !app.Deployed => await MakeAgainFromSettingsAsync(app, ct),
            _ when !app.Deployed => new BringBackResult(false, false, $"{app.Name} isn't built by Gitberth, so it can't be built again."),
            _ => await QueueRecoverAsync(app, build: request.Kind == BringBackKind.Build, ct),
        };
    }

    // Whether the app isn't meant to run any more: stopped, being deleted or forgotten, or gone, since the check looked.
    private async Task<bool> NotWantedAsync(int appId, CancellationToken ct) =>
        IsDeleting(appId) || await RowAsync(appId, ct) is not { DesiredState: DesiredState.Running };

    private static string NotWanted(string app) => $"{app} was stopped, deleted or forgotten meanwhile.";

    // A Git app's own container, under its lock: a deploy or a person's stop that holds it goes first.
    private async Task<BringBackResult> StartGitContainerAsync(AppDefinition app, CancellationToken ct)
    {
        using var held = await locks.TryAcquireAsync(app.Id);
        if (held is null) return BringBackResult.Waits;
        // Its owner may have stopped it a moment ago, before the lock was free: the row says.
        if (await NotWantedAsync(app.Id, ct)) return BringBackResult.Waits;
        var r = await docker.StartAsync(app.ContainerName, ct);
        return r.Ok ? new BringBackResult(true, false, null) : new BringBackResult(false, false, Last(r.ErrorText));
    }

    // A Compose app's services, alone: docker compose start of the ones that crashed, with its current release.
    private async Task<BringBackResult> StartServicesAsync(AppDefinition app, IReadOnlyList<string>? services, CancellationToken ct)
    {
        using var held = await locks.TryAcquireAsync(app.Id);
        if (held is null) return BringBackResult.Waits;
        if (await NotWantedAsync(app.Id, ct)) return BringBackResult.Waits;
        var current = await CurrentReleaseAsync(app, ct);
        var r = await Composer.RunAsync(app.ComposeProject, current?.File, null, ["start", .. services ?? []], ct: ct);
        return r.Ok ? new BringBackResult(true, false, null) : new BringBackResult(false, false, Last(r.ErrorText));
    }

    // Any other app's container, as a person's Start does it: its lock, a change that isn't settled, a database's old copy.
    // The row is read again under the container's lock, right before the start: a Stop that came meanwhile wins.
    private async Task<BringBackResult> StartContainerAsync(AppDefinition app, CancellationToken ct)
    {
        if (containers is null) return new BringBackResult(false, false, $"Gitberth can't start {app.Name} from here.");
        var details = (await docker.InspectContainersAsync([app.ContainerName], ct)).FirstOrDefault();
        // Gone since the check looked: its next look says so.
        if (details is null) return BringBackResult.Waits;
        if (await RefusedAsync(app.ContainerName, ContainerAction.Start, ct) is { } refused) return new BringBackResult(false, false, refused);
        var stale = false;
        var error = await containers.Value.RunContainerAsync(app.ContainerName, ContainerService.IsDatabase(details.Image, null, app.ImageRef), ContainerAction.Start, ct,
            unless: async c => (stale = await NotWantedAsync(app.Id, c)) ? NotWanted(app.Name) : null);
        return stale ? BringBackResult.Waits : new BringBackResult(error is null, false, error);
    }

    // A missing container of an app from a registry, a local image or by hand, from its saved settings (the change engine
    // starts and checks it). Never an adopted one's, never one whose first create didn't work, never next to a renamed one.
    private async Task<BringBackResult> MakeAgainFromSettingsAsync(AppDefinition app, CancellationToken ct)
    {
        if (app.Source == Apps.AppSource.Existing || app.ContainerId is null)
            return new BringBackResult(false, false, $"Gitberth doesn't make {app.ContainerName} by itself.", How: KeepHow.MadeFromSettings);
        var error = await CreateAgainAsync(app, ct);
        return new BringBackResult(error is null, false, error, How: KeepHow.MadeFromSettings);
    }

    /// <summary>
    /// A Git or Compose app's missing container: a container a deploy cut short moved aside goes back (as start-up does it);
    /// else a deploy without a build from its image or current release; else, with that gone too (<paramref name="build"/>:
    /// Docker said its image is), a build of the commit it ran, on its own branch, never the branch's newer tip (a preview
    /// builds its own ref, a Compose file pasted into Gitberth the file Gitberth keeps).
    /// </summary>
    private async Task<BringBackResult> QueueRecoverAsync(AppDefinition app, bool build, CancellationToken ct)
    {
        // Docker first, outside the deploy gate, which every deploy and deletion waits on.
        if (!app.IsCompose && await PutBackAsync(app, ct) is { } putBack) return putBack;
        await _deployGate.WaitAsync(ct);
        try
        {
            if (_deleting.ContainsKey(app.Id)) return BringBackResult.Waits;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Apps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
            // Stopped, deleted or forgotten since the check looked: theirs.
            if (row is not { DesiredState: DesiredState.Running }) return BringBackResult.Waits;
            // Never in another deploy's way: the queue keeps one request per app, and would drop the older one.
            if (await db.Deployments.AnyAsync(d => d.AppId == row.Id && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning
                    || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting), ct))
                return BringBackResult.Waits;
            // Its image, or its current release's files: run again without a build. Never a build of a newer commit in their
            // place, nor a wait on this gate again (RecreateAsync's own fallback).
            if (!build && await RecreateCoreAsync(db, row, DeployTrigger.Recover, null, ct) is int again)
                return new BringBackResult(true, false, null, again, KeepHow.MadeFromImage, row.IsCompose ? null : row.CurrentImage);
            var (gitRef, commit) = RecoverSource(row);
            var id = await QueueAsync(db, row, row.Id, DeployTrigger.Recover, expectedSha: null, gitRef, commit, null, ct);
            return new BringBackResult(true, false, null, id, KeepHow.Built);
        }
        finally
        {
            _deployGate.Release();
        }
    }

    /// <summary>
    /// What a Recover build fetches: the commit the app ran, on its own branch, a Compose app from a repo's too; null for a
    /// preview (it builds its own ref), a Compose file pasted into Gitberth (the file Gitberth keeps), or an app that never
    /// ran a commit.
    /// </summary>
    internal static (string? GitRef, string? Commit) RecoverSource(AppDefinition app) =>
        app.FromRepo && !app.IsPreview && app.CurrentCommit is { } commit ? (DeployPipeline.OwnBranchRef(app), commit) : (null, null);

    // A Git app's container a deploy cut short moved aside goes back under its name and starts, as start-up does it: under
    // the app's lock, which a deploy's swap and a Stop take too, and only while the app is still meant to run. Null when
    // there's none to put back.
    private async Task<BringBackResult?> PutBackAsync(AppDefinition app, CancellationToken ct)
    {
        using var held = await locks.TryAcquireAsync(app.Id);
        if (held is null) return BringBackResult.Waits;
        var previous = app.ContainerName + DeployPipeline.PreviousSuffix;
        if (await docker.ContainerExistsAsync(app.ContainerName, ct) || !await docker.ContainerExistsAsync(previous, ct)) return null;
        if (await NotWantedAsync(app.Id, ct)) return BringBackResult.Waits;
        var renamed = await docker.RenameAsync(previous, app.ContainerName, ct);
        if (!renamed.Ok) return new BringBackResult(false, false, Last(renamed.ErrorText), How: KeepHow.PutBack);
        var started = await docker.StartAsync(app.ContainerName, ct);
        return new BringBackResult(started.Ok, false, started.Ok ? null : Last(started.ErrorText), How: KeepHow.PutBack);
    }

    /// <summary>
    /// Forgets an app that isn't deployed from a repo or a file (<see cref="Apps.AppCatalog.ForgetAsync"/>): its record goes,
    /// its container stays as it is. Meanwhile it counts as being deleted, so the keep-running check never makes a missing
    /// container again for a record that's going; a create already under way asks again before it makes anything.
    /// </summary>
    public async Task<SyncReport> ForgetAsync(int appId, CancellationToken ct = default)
    {
        if (catalog is null) throw new InvalidOperationException("Gitberth can't forget an app from here.");
        return await WhileRemovingAsync(await RowAsync(appId, ct), () => catalog.ForgetAsync(appId, ct));
    }

    // While an app that isn't deployed from a repo or a file is removed or forgotten: its container goes a moment before its
    // row, and the keep-running check must not take that for a container gone missing.
    private async Task<T> WhileRemovingAsync<T>(AppDefinition? app, Func<Task<T>> remove)
    {
        if (app is null) return await remove();
        _deleting[app.Id] = 0;
        try
        {
            return await remove();
        }
        finally
        {
            _deleting.TryRemove(app.Id, out _);
        }
    }
}
