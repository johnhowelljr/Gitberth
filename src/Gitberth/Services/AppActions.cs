using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// Deploy, start, stop, restart, roll back and delete, for every app. A Git app's container changes go through its deploys
/// and the per-app lock; any other app's container goes through ContainerService's container path, under the container's
/// lock, and one that's missing is created again from its saved settings by the change engine. The int methods are a Git
/// app's (and every existing caller's); they dispatch on the row's source, so ContainerService's app branch works for every
/// source. The key methods take the container's name, with or without a row.
/// </summary>
/// <param name="catalog">The apps by key, and their saved settings; without it (in older tests) only Git apps are acted on.</param>
/// <param name="containers">The container path, which itself starts an app's container through this class, so it comes when it's first needed.</param>
/// <param name="changer">Creates a missing app's container again from its saved settings; it depends on ContainerService, so it comes when it's first needed.</param>
/// <param name="settings">An app's ports, for any app that isn't a Git app (AppSettingsService, which depends on this class).</param>
/// <param name="previewGate">The previews' gate (PreviewService's): deleting an app turns its previews off under it, so the watcher never makes one again meanwhile.</param>
/// <param name="downStates">Told when an app is stopped or started, so its domains go to the down page (or back) at once rather than at the next look.</param>
/// <param name="audit">Where deploys queued, starts, stops, restarts and deletions are recorded, with who asked; none in older tests.</param>
/// <param name="databases">
/// An app's databases, let go of when it's deleted (dropped with it when asked, and always a preview's); it deploys through
/// this class, so it comes when it's first needed. Without it (in older tests) they go with the row alone.
/// </param>
/// <param name="composer">Runs docker compose for a Compose app's whole project; without it (in older tests) one is made over <paramref name="docker"/>.</param>
public sealed partial class AppActions(IDbContextFactory<GitberthDb> dbFactory, DeployQueue queue, DockerCli docker, AppLocks locks, GitberthOptions options, RepoVariables repoVariables,
    DomainService domains, RestartSchedules restarts, AppService apps, ILogger<AppActions> logger,
    AppCatalog? catalog = null, Lazy<ContainerService>? containers = null, Lazy<PortChanger>? changer = null, Lazy<AppSettingsService>? settings = null,
    NetworkService? network = null, ContainerTitles? titles = null, HealthSettings? health = null, Previews.PreviewGate? previewGate = null,
    DownStates? downStates = null, Audit.AuditLog? audit = null, Lazy<Databases.DatabaseService>? databases = null,
    Compose.ComposeRunner? composer = null)
{
    /// <summary>Clone, build and run the branch tip.</summary>
    /// <param name="expectedSha">
    /// The commit the watcher saw. Recorded up front, so a deploy whose clone fails still counts as an attempt at it:
    /// otherwise the next poll would see an untried commit and start over, every 30 seconds.
    /// </param>
    /// <exception cref="InvalidOperationException">The app isn't a Git app: there's nothing to build.</exception>
    /// <param name="gitRef">Build this ref rather than the app's branch (a promoted preview's).</param>
    /// <param name="commit">Build exactly this commit of <paramref name="gitRef"/>.</param>
    /// <param name="promotedOver">
    /// A promote's: the branch's commit production stays off while the promote holds, saved with its deploy so the
    /// watcher never sees one without the other.
    /// </param>
    /// <param name="noCache">Build without the build cache: every step runs again, and the base images are pulled.</param>
    public async Task<int> DeployAsync(int appId, DeployTrigger trigger = DeployTrigger.Manual, CancellationToken ct = default, string? expectedSha = null,
        string? gitRef = null, string? commit = null, string? promotedOver = null, bool noCache = false) =>
        await QueueDeployAsync(appId, trigger, onlyIfRunning: false, ct, expectedSha, gitRef, commit, promotedOver, noCache)
        ?? throw new InvalidOperationException("The app is being deleted, so it isn't deployed.");

    /// <summary>
    /// A watcher's deploy of a commit it saw: none (null) when the app stopped being meant to run since the watcher read it
    /// (stopped by its owner, or being deleted), so a pass that read the list before never starts it again.
    /// </summary>
    public Task<int?> DeployWatchedAsync(int appId, DeployTrigger trigger, string sha, CancellationToken ct = default) =>
        QueueDeployAsync(appId, trigger, onlyIfRunning: true, ct, expectedSha: sha);

    /// <summary>
    /// A deploy hook's deploy: a full build of the branch's newest commit, even when that commit runs already, since what
    /// changed is usually outside the repo (a CMS's content). None (null) for an app that's stopped or being deleted: a hook
    /// never starts one. Calls in quick succession leave one build running and the newest queued (<see cref="DeployQueue"/>).
    /// </summary>
    public Task<int?> DeployFromHookAsync(int appId, string hookName, CancellationToken ct = default) =>
        QueueDeployAsync(appId, DeployTrigger.Hook, onlyIfRunning: true, ct, hookName: hookName);

    // Apps being deleted, which no deploy may start for. Set under _deployGate, which a deploy holds from its check to its
    // queueing, so once delete has set it every deploy is either refused or queued already (and delete cancels those).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _deleting = new();
    private readonly SemaphoreSlim _deployGate = new(1, 1);

    private async Task<int?> QueueDeployAsync(int appId, DeployTrigger trigger, bool onlyIfRunning, CancellationToken ct, string? expectedSha = null,
        string? gitRef = null, string? commit = null, string? promotedOver = null, bool noCache = false, string? hookName = null)
    {
        await _deployGate.WaitAsync(ct);
        try
        {
            if (_deleting.ContainsKey(appId)) return null;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var app = await db.Apps.FirstAsync(a => a.Id == appId, ct);
            if (!app.Deployed) throw NotGit(app);
            if (onlyIfRunning && app.DesiredState != DesiredState.Running) return null;
            return await QueueAsync(db, app, appId, trigger, expectedSha, gitRef, commit, promotedOver, ct, noCache, hookName);
        }
        finally
        {
            _deployGate.Release();
        }
    }

    private async Task<int> QueueAsync(GitberthDb db, AppDefinition app, int appId, DeployTrigger trigger, string? expectedSha, string? gitRef, string? commit,
        string? promotedOver, CancellationToken ct, bool noCache = false, string? hookName = null)
    {
        // The keep-running check's deploy never makes the app meant to run: a Stop that came meanwhile stays, and the
        // deploy's swap reads it and starts nothing.
        if (trigger != DeployTrigger.Recover) app.DesiredState = DesiredState.Running;
        // A promote's commit isn't its branch's: a promote that never runs, or fails, leaves what the watcher knows about
        // the branch as it was, so a rollback stays.
        if (expectedSha is not null && trigger != DeployTrigger.Promote) app.LastAttemptedCommit = expectedSha;
        // A manual deploy, or a hook's, builds the branch again: production stops staying on a promoted preview's commit.
        if (trigger is DeployTrigger.Manual or DeployTrigger.Hook) app.PromotedOverCommit = null;
        else if (trigger == DeployTrigger.Promote && promotedOver is not null) app.PromotedOverCommit = promotedOver;
        // Saved with the deploy, so its page and the app's history say it from the moment it's queued.
        var dep = new Deployment
        {
            AppId = appId, Trigger = trigger, CommitSha = expectedSha, NoCache = noCache,
            HookName = trigger == DeployTrigger.Hook ? hookName : null,
            // Who asked, so the deploy's outcome is told against them, whoever's code runs it.
            RequestedBy = Audit.AuditActors.Format(Audit.AuditScope.Current),
        };
        db.Deployments.Add(dep);
        await db.SaveChangesAsync(ct);
        Queued(app, dep);
        queue.Enqueue(new DeployRequest(appId, dep.Id, Build: true, GitRef: gitRef, Commit: commit, NoCache: noCache));
        return dep.Id;
    }

    // The deploy's entry in the audit log, the moment it's queued.
    private void Queued(AppDefinition app, Deployment dep, int? fromDeployment = null)
    {
        if (audit is null) return;
        var changes = new List<Audit.AuditChange> { new("Trigger", null, Audit.AuditText.Trigger(dep)) };
        if (dep.CommitSha is { Length: > 0 }) changes.Add(new("Commit", null, dep.ShortSha));
        if (dep.ImageTag is { Length: > 0 } image && !dep.NoCache) changes.Add(new("Image", null, image));
        if (dep.NoCache) changes.Add(new("Build cache", null, "not used"));
        if (dep.HookName is { } hook) changes.Add(new("Deploy hook", null, hook));
        audit.Record(new Audit.AuditEntry("app.deploy.queued", Audit.AuditText.Queued(app.Name, dep, fromDeployment))
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
            Target = $"deploy #{Audit.AuditText.N(dep.Id)}",
            Correlation = Audit.AuditCorrelation.ForDeployment(dep.Id),
            Changes = changes,
        });
    }

    // Started, stopped or restarted, by whoever asked, and how it went.
    private void Recorded(AppDefinition app, string verb, string? error, Audit.AuditOutcome failed = Audit.AuditOutcome.Failed) =>
        Recorded(app.ContainerName, app.Id, app.Name, verb, error, failed);

    private void Recorded(string container, int? appId, string name, string verb, string? error, Audit.AuditOutcome failed = Audit.AuditOutcome.Failed)
    {
        var action = verb switch { "started" => "app.started", "stopped" => "app.stopped", _ => "app.restarted" };
        audit?.Record(new Audit.AuditEntry(action, error is null ? $"{Capital(verb)} {name}." : $"Couldn't {Base(verb)} {name}: {Audit.AuditLog.Clean(error, 300)?.TrimEnd('.')}.")
        {
            AppId = appId, AppName = name, Container = container,
            Outcome = error is null ? Audit.AuditOutcome.Succeeded : failed,
        });
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Base(string verb) => verb switch { "started" => "start", "stopped" => "stop", _ => "restart" };

    /// <summary>Recreate the container without rebuilding: to apply settings, or to roll back to an earlier deploy's image.</summary>
    /// <exception cref="InvalidOperationException">The app isn't a Git app: the change engine recreates it.</exception>
    public async Task<int> RecreateAsync(int appId, DeployTrigger trigger, int? fromDeploymentId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.FirstAsync(a => a.Id == appId, ct);
        if (!app.Deployed) throw NotGit(app);
        if (await RecreateCoreAsync(db, app, trigger, fromDeploymentId, ct) is int id) return id;
        // Nothing to run again without a build (no image yet, or the release's files are gone): it's deployed again from its source.
        await db.DisposeAsync();
        return await DeployAsync(appId, trigger, ct);
    }

    /// <summary>
    /// A deploy without a build, queued for <paramref name="app"/> as <paramref name="db"/> tracks it; null when there's
    /// nothing to run again without one. It never waits on the deploy gate, so the keep-running check queues one under it.
    /// </summary>
    private async Task<int?> RecreateCoreAsync(GitberthDb db, AppDefinition app, DeployTrigger trigger, int? fromDeploymentId, CancellationToken ct)
    {
        var appId = app.Id;
        if (app.IsCompose) return await RecreateComposeAsync(db, app, trigger, fromDeploymentId, ct);
        string? image = app.CurrentImage, sha = app.CurrentCommit, message = null, author = null;
        if (fromDeploymentId is int fromId)
        {
            var from = await db.Deployments.FirstAsync(d => d.Id == fromId && d.AppId == appId, ct);
            image = from.ImageTag;
            sha = from.CommitSha;
            message = from.CommitMessage;
            author = from.CommitAuthor;
        }
        else if (sha is not null)
        {
            var current = await db.Deployments.Where(d => d.AppId == appId && d.CommitSha == sha && d.Status == DeployStatus.Succeeded)
                .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
            message = current?.CommitMessage;
            author = current?.CommitAuthor;
        }
        if (image is null) return null;

        // The keep-running check's deploy never makes the app meant to run: a Stop that came meanwhile stays.
        if (trigger != DeployTrigger.Recover) app.DesiredState = DesiredState.Running;
        var dep = new Deployment
        {
            AppId = appId, Trigger = trigger, ImageTag = image, CommitSha = sha, CommitMessage = message, CommitAuthor = author,
            RequestedBy = Audit.AuditActors.Format(Audit.AuditScope.Current),
        };
        db.Deployments.Add(dep);
        await db.SaveChangesAsync(ct);
        Queued(app, dep, fromDeploymentId);
        queue.Enqueue(new DeployRequest(appId, dep.Id, Build: false, Image: image));
        return dep.Id;
    }

    public Task<int> ApplySettingsAsync(int appId) => RecreateAsync(appId, DeployTrigger.ConfigChange);

    /// <summary>
    /// Makes saved settings live when the app runs: a deploy without a build (<see cref="ApplySettingsAsync"/>), on the
    /// zero-downtime path where it can. A stopped app takes them when it starts. Returns the deploy, or null when none started.
    /// While a deploy of the app is queued or running, that deploy is returned and nothing more is queued.
    /// </summary>
    public async Task<int?> ApplyIfRunningAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app?.CurrentImage is null) return null;
        // A deploy already queued or running is followed instead: a recreate queued behind it would be pinned to the image
        // from before it, and put that back once it's done.
        var active = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == appId && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting))
            .OrderByDescending(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        if (active is not null) return active;
        if (await docker.InspectStateAsync(app.ContainerName, ct) is not { Running: true }) return null;
        return await RecreateAsync(appId, DeployTrigger.ConfigChange, ct: ct);
    }

    /// <summary>
    /// Saves the app's ports (<see cref="AppService.SavePortsAsync"/>), then, with <paramref name="applyNow"/>, makes them
    /// live when the app runs and they need its container recreated. Without it they wait for the next deploy, with the
    /// pending banner up ("Save for next deploy"). Moving only the web marker never restarts anything. An app that isn't
    /// deployed from a repo has no next deploy: its ports apply now, and saving them for later is refused with why.
    /// </summary>
    /// <param name="loaded">The ports as the page loaded them (<see cref="AppSettingsModel.PortsKey(IEnumerable{PortMapping})"/>).</param>
    /// <returns>What the save did, with the deploy that applies it, if one started.</returns>
    public async Task<PortsSaved> ApplyPortsAsync(int appId, IReadOnlyList<PortRow> rows, string? loaded, bool applyNow, CancellationToken ct = default)
    {
        if (await RowAsync(appId, ct) is { Deployed: false } other) return await ApplyOtherPortsAsync(other, rows, loaded, applyNow, ct);
        var saved = await apps.SavePortsAsync(appId, rows, loaded, ct);
        if (!saved.Ok || !saved.NeedsRestart || !applyNow) return saved;
        return saved with { DeploymentId = await ApplyIfRunningAsync(appId, ct) };
    }

    // Any other app's ports are its container's: the change engine plans and applies them (AppSettingsService), and the
    // web marker is Gitberth's own data. There's no deploy to wait for.
    private async Task<PortsSaved> ApplyOtherPortsAsync(AppDefinition app, IReadOnlyList<PortRow> rows, string? loaded, bool applyNow, CancellationToken ct)
    {
        if (!applyNow)
            return new PortsSaved([new PortProblem(null, PortField.Table,
                $"{app.Name} isn't deployed from a repo, so there's no next deploy to wait for: its ports change when its container is recreated. Apply them now instead.")],
                NeedsRestart: false);
        if (settings is null) return new PortsSaved([new PortProblem(null, PortField.Table, $"Gitberth can't change {app.Name}'s ports from here.")], NeedsRestart: false);
        var containerId = network is null ? app.ContainerId : ContainerPorts.Str(await network.InspectAsync(app.ContainerName, ct), "Id") is { Length: > 0 } id ? id : null;
        var plan = await settings.Value.PlanPortsAsync(app.ContainerName, containerId, rows, loaded, ct);
        var restart = plan.Change is { LiveOnly: false };
        if (plan.Blocked is { } blocked) return new PortsSaved([new PortProblem(null, PortField.Table, blocked)], restart);
        if (plan.Problems.Count > 0) return new PortsSaved(plan.Problems.Select(p => new PortProblem(null, PortField.Table, p.Message)).ToList(), restart);
        if (plan.Lines.Count == 0) return new PortsSaved([], NeedsRestart: false);
        var outcome = await settings.Value.ApplyAsync(plan, ApplyTiming.Now, ct);
        // The ports are live once the change is over (a domain added with them is routed to them): one put back says why.
        var message = !outcome.Ok ? outcome.Message : outcome.Job is { } job && await job.Completion is { Ok: false } failed ? failed.Message : null;
        return message is null
            ? new PortsSaved([], restart, outcome.Domains)
            : new PortsSaved([new PortProblem(null, PortField.Table, message)], restart, outcome.Domains);
    }

    public Task<int> RollbackAsync(int appId, int toDeploymentId) => RecreateAsync(appId, DeployTrigger.Rollback, toDeploymentId);

    /// <summary>
    /// The images of a Git app's builds still on this host (gitberth/&lt;slug&gt;:&lt;commit&gt;): a deploy can be rolled
    /// back to only while its image is here. A deploy keeps the last few (DeployPipeline.PruneImagesAsync), and anyone can
    /// remove one. Empty when Docker can't be asked; a deploy can't build an older commit, so there's nothing else to offer.
    /// </summary>
    public async Task<IReadOnlySet<string>> RollbackImagesAsync(int appId, CancellationToken ct = default)
    {
        if (await RowAsync(appId, ct) is not { Deployed: true } app) return new HashSet<string>();
        if (app.IsCompose) return await ComposeRollbacksAsync(app, ct);
        try
        {
            return (await docker.ListImageTagsAsync(app.ImageRepository, ct)).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Couldn't list the kept images of {App}", app.Slug);
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Returns an error message, or null on success. While saved settings aren't live yet, the app is recreated from its
    /// current image rather than started as it was, so what was saved for the next start really applies. A container
    /// that's gone is made again from its last image when that's still on this host, else built again from its branch
    /// (the image goes when it's removed, or when Gitberth is restored onto another host). Any other app's container is
    /// started as a container is (<see cref="ContainerService"/>), or created again from its saved settings when it's missing.
    /// </summary>
    public async Task<string?> StartAsync(int appId)
    {
        var app = await SetDesiredStateAsync(appId, DesiredState.Running);
        // Its domains stay on the down page until its container runs, then go back to it.
        downStates?.Nudge(appId);
        var error = await StartCoreAsync(app);
        Recorded(app, "started", error);
        return error;
    }

    private async Task<string?> StartCoreAsync(AppDefinition app)
    {
        var appId = app.Id;
        if (!app.Deployed) return await StartOtherAsync(app, CancellationToken.None);
        if (app.IsCompose) return await StartComposeAsync(app);
        if (!app.ConfigChangePending || app.CurrentImage is null)
        {
            using (await locks.AcquireAsync(appId))
            {
                if (await docker.ContainerExistsAsync(app.ContainerName))
                {
                    var r = await docker.StartAsync(app.ContainerName);
                    return r.Ok ? null : r.ErrorText;
                }
            }
        }
        if (await HasLastImageAsync(app)) await RecreateAsync(appId, DeployTrigger.Start);
        else await DeployAsync(appId, DeployTrigger.Start);
        return null;
    }

    /// <summary>
    /// Whether a Git app's last image is still on this host, so Start makes its container again from it without a build.
    /// False when it has none yet or it's gone: then Start builds it again.
    /// </summary>
    public async Task<bool> HasLastImageAsync(AppDefinition app, CancellationToken ct = default) =>
        app.IsCompose ? await CurrentReleaseAsync(app, ct) is not null
        : app.CurrentImage is { } image && await docker.ImageExistsAsync(image, ct);

    public async Task<string?> StopAsync(int appId)
    {
        // Gitberth itself, once it has a row (pinned, say), isn't stopped from here: it would stay down.
        if (await RowAsync(appId, CancellationToken.None) is { Deployed: false } other && await RefusedAsync(other.ContainerName, ContainerAction.Stop, CancellationToken.None) is { } refused)
        {
            Recorded(other, "stopped", refused, Audit.AuditOutcome.Denied);
            return refused;
        }
        var app = await SetDesiredStateAsync(appId, DesiredState.Stopped);
        // Its domains go to the down page at once (while it stops), rather than to Cloudflare's error once it has.
        downStates?.Nudge(appId);
        var error = await StopCoreAsync(app);
        Recorded(app, "stopped", error);
        return error;
    }

    private async Task<string?> StopCoreAsync(AppDefinition app)
    {
        var appId = app.Id;
        if (!app.Deployed) return await RunOtherAsync(app, ContainerAction.Stop, missingIsFine: true, CancellationToken.None);
        // A deploy queued before the stop (a scheduled restart's, say) would start it again: it's cancelled, and one that's
        // under way finds the app stopped before it swaps (DeployPipeline).
        await CancelQueuedAsync(appId, "Cancelled: the app was stopped.", "the app was stopped");
        if (app.IsCompose) return await ProjectAsync(app, "stop");
        using (await locks.AcquireAsync(appId))
        {
            // A temporary container a deploy left behind would keep serving the app's domains.
            await docker.RemoveAsync(app.ContainerName + DeployPipeline.BridgeSuffix);
            if (!await docker.ContainerExistsAsync(app.ContainerName)) return null;
            var r = await docker.StopAsync(app.ContainerName);
            return r.Ok ? null : r.ErrorText;
        }
    }

    public async Task<string?> RestartAsync(int appId)
    {
        var app = await SetDesiredStateAsync(appId, DesiredState.Running);
        var error = await RestartCoreAsync(app);
        Recorded(app, "restarted", error);
        return error;
    }

    private async Task<string?> RestartCoreAsync(AppDefinition app)
    {
        var appId = app.Id;
        if (!app.Deployed) return await RunOtherAsync(app, ContainerAction.Restart, missingIsFine: false, CancellationToken.None);
        if (app.IsCompose) return await ProjectAsync(app, "restart");
        using (await locks.AcquireAsync(appId))
        {
            if (!await docker.ContainerExistsAsync(app.ContainerName)) return "There's no container to restart. Start or deploy the app instead.";
            var r = await docker.RestartAsync(app.ContainerName);
            return r.Ok ? null : r.ErrorText;
        }
    }

    public bool CancelDeploy(int appId)
    {
        var cancelled = queue.Cancel(appId);
        if (cancelled && audit is not null)
        {
            var actor = Audit.AuditScope.Current;
            _ = Task.Run(async () =>
            {
                var app = await RowAsync(appId, CancellationToken.None);
                audit.Record(new Audit.AuditEntry("app.deploy.cancel", $"Asked to cancel the running deploy of {app?.Name ?? $"app {appId}"}.")
                {
                    Actor = actor, AppId = appId, AppName = app?.Name, Container = app?.ContainerName,
                });
            });
        }
        return cancelled;
    }

    // The app's deploys still waiting in the queue: the queue's worker skips one that isn't Queued any more. A promote
    // among them that never ran lets production follow its branch again. Each one cancelled is recorded, as the one who
    // asked for it, with why (<paramref name="reason"/>: "the app was stopped"), so its queued entry has its end.
    private async Task CancelQueuedAsync(int appId, string why, string reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var queued = await db.Deployments.AsNoTracking().Where(d => d.AppId == appId && d.Status == DeployStatus.Queued).ToListAsync();
        var app = queued.Count > 0 && audit is not null ? await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId) : null;
        foreach (var dep in queued)
        {
            // One by one, and only while it's still queued: one the queue got to meanwhile ends as a deploy does.
            var cancelled = await db.Deployments.Where(d => d.Id == dep.Id && d.Status == DeployStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeployStatus.Cancelled).SetProperty(d => d.Error, why).SetProperty(d => d.FinishedAt, now));
            if (cancelled == 0 || app is null) continue;
            audit?.Record(new Audit.AuditEntry("app.deploy.cancelled", Audit.AuditText.CancelledBeforeStart(app.Name, dep, reason))
            {
                Actor = Audit.AuditActors.ForDeploy(dep.RequestedBy, dep.Trigger),
                Outcome = Audit.AuditOutcome.Failed,
                AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
                Target = $"deploy #{Audit.AuditText.N(dep.Id)}",
                Correlation = Audit.AuditCorrelation.ForDeployment(dep.Id),
                Changes = [new("Trigger", null, Audit.AuditText.Trigger(dep)), new("Why", null, reason)],
            });
        }
        await RepoWatcher.ForgetUnheldPromotionAsync(db, appId);
    }

    /// <summary>
    /// Deletes the app. Its domains come out of the tunnel too; the report says if that has to be retried. Any other app's
    /// container is removed as <see cref="RemoveAsync(string, RemoveOptions, CancellationToken)"/> does it.
    /// </summary>
    /// <param name="removeDatabases">Its databases' data goes too (Settings › Databases); its previews' always does.</param>
    public async Task<SyncReport> DeleteAsync(int appId, bool removeImages, bool removeVolumes, bool removeDatabases = false)
    {
        // From here no deploy of it starts (DeployAsync refuses, the watcher's finds it not meant to run); one already
        // queued is cancelled, and one running is cancelled and finishes before its container is removed (the app's lock).
        await _deployGate.WaitAsync();
        _deleting[appId] = 0;
        _deployGate.Release();
        AppDefinition? before = null;
        try
        {
            if (audit is not null)
                await using (var db = await dbFactory.CreateDbContextAsync())
                    before = await db.Apps.AsNoTracking().Include(a => a.Volumes).Include(a => a.Domains).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == appId);
            queue.Cancel(appId);
            await CancelQueuedAsync(appId, "The app is being deleted.", "the app is being deleted");
            var report = await DeleteCoreAsync(appId, removeImages, removeVolumes, removeDatabases);
            // A preview's removal is its own entry, with why (PreviewService); its parent's deletion says it took them.
            if (before is { Deployed: true, ParentAppId: null }) Deleted(before, removeImages, removeVolumes);
            return report;
        }
        catch (Exception ex) when (before is { Deployed: true, ParentAppId: null })
        {
            audit?.Record(new Audit.AuditEntry("app.deleted", $"Couldn't delete {before.Name}: {Audit.AuditLog.Clean(ex.Message, 300)?.TrimEnd('.')}.")
            {
                AppId = before.Id, AppName = before.Name, Container = before.ContainerName, Outcome = Audit.AuditOutcome.Failed,
            });
            throw;
        }
        finally
        {
            // Once the row is gone nothing can deploy it anyway; after a failure the app may be deployed again.
            _deleting.TryRemove(appId, out _);
        }
    }

    // A Git app's deletion, with what went with it.
    private void Deleted(AppDefinition app, bool images, bool volumes)
    {
        var named = app.Volumes.Where(v => !VolumeNames.IsBind(v.Source)).Select(v => VolumeNames.Resolve(app.Slug, v.Source)).Distinct().ToList();
        var parts = new List<string> { app.IsCompose ? "its services' containers" : "its container" };
        if (images) parts.Add("its images");
        if (volumes && named.Count > 0) parts.Add($"its {(named.Count == 1 ? "volume" : "volumes")} {Audit.AuditText.Join(named)}");
        if (app.Domains.Count > 0) parts.Add($"{(app.Domains.Count == 1 ? "its domain" : "its domains")} {Audit.AuditText.Join(app.Domains.Select(d => d.Hostname).Order(StringComparer.Ordinal).ToList())}");
        var changes = new List<Audit.AuditChange>
        {
            new("Images", null, images ? "removed" : "kept"),
            new("Volumes", Audit.AuditText.Join(named) is { Length: > 0 } v ? v : "none", volumes ? "removed" : "kept"),
        };
        if (app.Domains.Count > 0) changes.Add(new("Domains", string.Join(", ", app.Domains.Select(d => d.Hostname).Order(StringComparer.Ordinal)), "removed"));
        audit?.Record(new Audit.AuditEntry("app.deleted", $"Deleted {app.Name}: {Audit.AuditText.Join(parts)} went with it{(volumes || named.Count == 0 ? "" : ", and its data stays in its volumes")}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
    }

    private async Task<SyncReport> DeleteCoreAsync(int appId, bool removeImages, bool removeVolumes, bool removeDatabases)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var app = await db.Apps.AsNoTracking().Include(a => a.Volumes).FirstOrDefaultAsync(a => a.Id == appId);
        if (app is null) return SyncReport.Nothing;
        if (!app.Deployed)
            return (await RemoveOtherAsync(app.ContainerName, app, new RemoveOptions(removeVolumes, removeImages, removeDatabases), CancellationToken.None)).Domains ?? SyncReport.Nothing;
        // Its previews go first, each with its container, images, own volumes and domain: they're nothing without it. Under
        // the previews' gate, previews are turned off and the app stops being meant to run first, so a watcher's pass under
        // way finishes before, and every later one finds nothing of it to make, deploy or preview.
        if (app.ParentAppId is null)
        {
            if (previewGate is not null) await previewGate.WaitAsync();
            try
            {
                await db.Apps.Where(a => a.Id == appId).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.PreviewMode, PreviewMode.Off)
                    .SetProperty(a => a.DesiredState, DesiredState.Stopped));
                try
                {
                    foreach (var previewId in await db.Apps.AsNoTracking().Where(a => a.ParentAppId == appId).Select(a => a.Id).ToListAsync())
                        await DeleteAsync(previewId, removeImages: true, removeVolumes: true);
                }
                catch
                {
                    // Its own container wasn't touched yet: it's left as it was, running and previewing as before.
                    await db.Apps.Where(a => a.Id == appId).ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.PreviewMode, app.PreviewMode)
                        .SetProperty(a => a.DesiredState, app.DesiredState));
                    throw;
                }
            }
            finally
            {
                previewGate?.Release();
            }
        }
        var skips = Previews.PreviewService.SkipsKey(appId);
        if (app.ParentAppId is null) await db.Settings.Where(s => s.Key == skips).ExecuteDeleteAsync();
        if (app.IsCompose)
        {
            using (await locks.AcquireAsync(appId)) await DownComposeAsync(app, removeImages, removeVolumes);
        }
        else
        using (await locks.AcquireAsync(appId))
        {
            await docker.RemoveAsync(app.ContainerName);
            await docker.RemoveAsync(app.ContainerName + DeployPipeline.PreviousSuffix);
            await docker.RemoveAsync(app.ContainerName + DeployPipeline.BridgeSuffix);
            if (removeImages)
            {
                foreach (var tag in await docker.ListImageTagsAsync(app.ImageRepository)) await docker.RemoveImageAsync(tag);
                await docker.PruneDanglingImagesAsync(app.Slug);
                // A tag a container still uses stays: the disk cleanup removes it once nothing does.
                if ((await docker.ListImageTagsAsync(app.ImageRepository)).Count > 0)
                    await Disk.DiskLeftovers.RecordAsync(dbFactory, app.ImageRepository, prefix: false, DateTime.UtcNow);
            }
            // Its images kept on purpose: an earlier app's leftovers under the slug no longer take any of them.
            else await Disk.DiskLeftovers.ForgetAsync(dbFactory, app.Slug);
            if (removeVolumes)
            {
                // One another app has too stays: it's that app's data as well (VolumeOwners).
                var owners = await VolumeOwners.LoadAsync(db);
                foreach (var name in app.Volumes.Where(v => !VolumeNames.IsBind(v.Source)).Select(v => VolumeNames.Resolve(app.Slug, v.Source)))
                {
                    if (owners.OtherThan(appId, name) is { } other) logger.LogWarning("Kept the volume {Volume} of {App}: {Other} has it too", name, app.Slug, other);
                    else await docker.RemoveVolumeAsync(name);
                }
            }
        }
        await restarts.SetAsync(app.ContainerName, null);
        // Its databases, once its container is gone: dropped when asked (a preview's always are), else left as they are.
        await LetGoOfDatabasesAsync(app, removeDatabases);
        var deploymentIds = await db.Deployments.Where(d => d.AppId == appId).Select(d => d.Id).ToListAsync();
        // The row goes together with the removal of its domains, under the domains' lock, so no route is left behind on a
        // port the next new app may get, and nothing adopts one in between.
        var report = await domains.DeleteAppAsync(appId);
        foreach (var problem in report.Notes.Where(n => n.Problem)) logger.LogWarning("Removing the domains of {App}: {Problem}", app.Slug, problem.Text);
        foreach (var id in deploymentIds)
        {
            DeployPaths.TryDeleteFile(DeployPaths.LogFile(options, id));
            DeployPaths.TryDeleteFile(DeployPaths.DockerfileCopy(options, id));
        }
        // The kept copy of the repo's GitHub variables goes with the repo's last app.
        var (owner, repo) = (app.Owner.ToLowerInvariant(), app.Repo.ToLowerInvariant());
        if (app.FromRepo && !await db.Apps.AnyAsync(a => a.Owner.ToLower() == owner && a.Repo.ToLower() == repo))
            await repoVariables.ForgetAsync(app.Owner, app.Repo);
        return report;
    }

    // ---- By key: the container's name, with or without a row ----

    /// <summary>
    /// Starts the app: a Git app as <see cref="StartAsync(int)"/> does; any other with a container as a container is; a
    /// missing one that isn't a Git app is created again from its saved settings (the change engine's create, which runs
    /// on its own: <see cref="PortChanger.JobFor"/>). Null when it worked or the create started, else what went wrong.
    /// </summary>
    public async Task<string?> StartAsync(string key, CancellationToken ct = default) =>
        await RowAsync(key, ct) is { } app ? await StartAsync(app.Id) : await RunUnadoptedAsync(key, ContainerAction.Start, ct);

    public async Task<string?> StopAsync(string key, CancellationToken ct = default) =>
        await RowAsync(key, ct) is { } app ? await StopAsync(app.Id) : await RunUnadoptedAsync(key, ContainerAction.Stop, ct);

    public async Task<string?> RestartAsync(string key, CancellationToken ct = default) =>
        await RowAsync(key, ct) is { } app ? await RestartAsync(app.Id) : await RunUnadoptedAsync(key, ContainerAction.Restart, ct);

    /// <summary>What removing the app would do to everything else, and what goes with it, for the confirmation.</summary>
    public async Task<IReadOnlyList<string>> RemoveWarningsAsync(string key, RemoveOptions options, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var app = await RowAsync(key, ct);
        var details = (await docker.InspectContainersAsync([key], ct)).FirstOrDefault();
        if (containers is not null && await containers.Value.FindAsync(key, ct) is { } row)
            // The volumes' line is said here, by what's chosen.
            warnings.AddRange((await containers.Value.WarningsAsync(row, ContainerAction.Remove, ct)).Where(w => !w.StartsWith("Its volume", StringComparison.Ordinal)
                && !w.StartsWith("Only the container goes", StringComparison.Ordinal)));
        var volumes = details?.Mounts.Where(m => m.Type == "volume" && m.Name is { Length: > 0 }).Select(m => m.Name!).Distinct().ToList() ?? [];
        if (volumes.Count > 0)
            warnings.Add(options.Volumes
                ? $"Its {(volumes.Count == 1 ? "volume goes" : "volumes go")} too: {Join(volumes)}. The data in {(volumes.Count == 1 ? "it" : "them")} is gone for good."
                : $"Its {(volumes.Count == 1 ? "volume stays" : "volumes stay")}: {Join(volumes)}. Only the container goes, so the data is still there for a new one.");
        else if (details is not null)
            warnings.Add("Only the container goes. Anything it kept inside itself rather than in a volume goes with it.");
        if (options.Image && details is not null)
            warnings.Add($"Its image, {details.Image}, goes too unless another container uses it or another app keeps it for Roll back.");
        if (app?.Domains.Count > 0)
        {
            var names = Join(app.Domains.Select(d => d.Hostname).Order(StringComparer.Ordinal).ToList());
            var one = app.Domains.Count == 1;
            // With domains off, Gitberth leaves the tunnel alone: the routes come out once they're on again.
            warnings.Add(await domains.EnabledAsync(ct)
                ? $"{names} {(one ? "stops" : "stop")} answering: {(one ? "its route goes" : "their routes go")} from the tunnel."
                : $"Domains are turned off, so {names} {(one ? "stays" : "stay")} in your Cloudflare Tunnel for now. Gitberth takes {(one ? "it" : "them")} out when domains are turned back on.");
        }
        return warnings;
    }

    /// <summary>
    /// Removes the app. A Git app is deleted as <see cref="DeleteAsync"/> does it. Any other app's container is removed
    /// (never with its volumes by Docker); with <see cref="RemoveOptions.Volumes"/>, the volumes it mounted are removed too,
    /// and with <see cref="RemoveOptions.Image"/>, its image when nothing else uses it. What Gitberth kept for it goes:
    /// its title, scheduled restart, stats choice, kept networks, port notes, and its row with its domains. While a change
    /// of its name isn't settled, those stay: they're the original's then, which may get the name back.
    /// </summary>
    public async Task<RemoveResult> RemoveAsync(string key, RemoveOptions options, CancellationToken ct = default)
    {
        var app = await RowAsync(key, ct);
        if (app is { Deployed: true })
            return new RemoveResult(true, $"{app.Name} is deleted.", await DeleteAsync(app.Id, removeImages: options.Image, removeVolumes: options.Volumes, removeDatabases: options.Databases));
        return await WhileRemovingAsync(app, () => RemoveOtherAsync(key, app, options, ct));
    }

    // An app's databases as it goes (Settings › Databases): never a reason to keep the app.
    private async Task LetGoOfDatabasesAsync(AppDefinition app, bool deleteData)
    {
        if (databases is null) return;
        try
        {
            await databases.Value.DeleteWithAppAsync(app.Id, deleteData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't let go of the databases of {App}", app.Slug);
        }
    }

    private async Task<RemoveResult> RemoveOtherAsync(string name, AppDefinition? app, RemoveOptions options, CancellationToken ct)
    {
        var result = await RemoveOtherCoreAsync(name, app, options, ct);
        audit?.Record(new Audit.AuditEntry("app.deleted", result.Ok ? result.Message : $"Couldn't remove {app?.Name ?? titles?.For(name) ?? name}: {Audit.AuditLog.Clean(result.Message, 300)?.TrimEnd('.')}.")
        {
            AppId = app?.Id, AppName = app?.Name ?? titles?.For(name) ?? name, Container = name,
            Outcome = result.Ok ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
            Changes = [new("Volumes", null, options.Volumes ? "removed" : "kept"), new("Image", null, options.Image ? "removed unless in use" : "kept")],
        });
        return result;
    }

    private async Task<RemoveResult> RemoveOtherCoreAsync(string name, AppDefinition? app, RemoveOptions options, CancellationToken ct)
    {
        if (containers is null) return new RemoveResult(false, $"Gitberth can't remove {name} from here.", null);
        var title = app?.Name ?? titles?.For(name) ?? name;
        var details = (await docker.InspectContainersAsync([name], ct)).FirstOrDefault();
        if (details is not null)
        {
            // Gitberth itself and a deploy's temporary containers stay, as the containers list says.
            if (await containers.Value.FindAsync(name, ct) is { Role: ContainerRole.Self or ContainerRole.AppInternal } row)
                return new RemoveResult(false, ContainerService.Refusal(row, ContainerAction.Remove)!, null);
            if (await containers.Value.RunContainerAsync(name, ContainerService.IsDatabaseImage(details.Image), ContainerAction.Remove, ct) is { } error)
                return new RemoveResult(false, error, null);
        }
        var notes = new List<string>();
        if (details is not null && options.Volumes)
            foreach (var volume in details.Mounts.Where(m => m.Type == "volume" && m.Name is { Length: > 0 }).Select(m => m.Name!).Distinct())
            {
                var r = await docker.RemoveVolumeAsync(volume, ct);
                notes.Add(r.Ok ? $"Its volume {volume} is removed." : $"Its volume {volume} stays: {r.ErrorText}");
            }
        if (details is not null && options.Image)
        {
            // By its ID, never by a name: `docker image rm postgres:18` only takes the name off an image another container
            // runs. Docker refuses an ID that another container (stopped ones too) uses, or that has other names.
            var (all, _) = await docker.ListContainersAsync(managedOnly: false, ct);
            var users = (await docker.InspectContainersAsync(all.Select(c => c.Name).Where(n => n != name).ToList(), ct))
                .Where(c => c.ImageId == details.ImageId).Select(c => c.Name).ToList();
            var keptFor = await KeptForRollBackAsync(details.ImageId, app?.Id, ct);
            if (users.Count > 0) notes.Add($"Its image {details.Image} stays: {Join(users)} {(users.Count == 1 ? "uses" : "use")} it.");
            else if (details.ImageId.Length == 0) notes.Add($"Its image {details.Image} stays: Docker didn't say which image it was.");
            // Another app went from it to a newer one: it's what that app's Roll back goes to.
            else if (keptFor.Count > 0) notes.Add($"Its image {details.Image} stays: {Join(keptFor)} {(keptFor.Count == 1 ? "keeps" : "keep")} it for Roll back.");
            else
            {
                var r = await docker.RemoveImageAsync(details.ImageId, ct);
                notes.Add(r.Ok ? $"Its image {details.Image} is removed." : $"Its image {details.Image} stays: {r.ErrorText}");
            }
        }

        // What's kept by name is the original's while a change of the name isn't settled.
        var settled = network is null || await network.ForgetAsync(name, ct);
        SyncReport? report = null;
        if (settled)
        {
            if (titles is not null) await titles.SetAsync(name, null, ct);
            await restarts.SetAsync(name, null, ct);
            if (health is not null) await health.SetGatheredAsync(name, true, ct);
            if (app is not null)
            {
                // Its databases go before its row does, which keeps them: dropped when asked, else left as they are.
                await LetGoOfDatabasesAsync(app, options.Databases);
                report = await domains.DeleteAppAsync(app.Id, ct);
                foreach (var problem in report.Notes.Where(n => n.Problem)) logger.LogWarning("Removing the domains of {App}: {Problem}", name, problem.Text);
            }
        }
        else
        {
            notes.Add($"A change of {name} isn't settled, so what Gitberth keeps for the name stays, for the original.");
        }
        catalog?.RaiseChanged();
        return new RemoveResult(true, string.Join(' ', new[] { details is null ? $"{title} had no container; Gitberth forgot it." : $"{title} is removed." }.Concat(notes)), report);
    }

    /// <summary>The app's row by its key (its container's name), with its ports and domains; null when it has none.</summary>
    private async Task<AppDefinition?> RowAsync(string key, CancellationToken ct)
    {
        var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key["gb-".Length..] : null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Container == key || (a.Container == null && slug != null && a.Slug == slug), ct);
    }

    private async Task<AppDefinition?> RowAsync(int appId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
    }

    // A container without a row, as the containers list does it (Gitberth itself and deploys' temporaries are refused there).
    private async Task<string?> RunUnadoptedAsync(string key, ContainerAction action, CancellationToken ct)
    {
        if (containers is null) return $"Gitberth can't act on {key} from here.";
        if (await containers.Value.FindAsync(key, ct) is not { } row) return $"There's no container called {key}.";
        var error = await containers.Value.RunAsync(row, action, ct);
        // An app's own container went through the app's actions, which say it themselves.
        if (row is not { Role: ContainerRole.App, App: not null } && action != ContainerAction.Remove)
            Recorded(key, null, row.Title, action switch { ContainerAction.Start => "started", ContainerAction.Stop => "stopped", _ => "restarted" }, error,
                ContainerService.Refusal(row, action) is null ? Audit.AuditOutcome.Failed : Audit.AuditOutcome.Denied);
        return error;
    }

    // An app that isn't a Git app, with a container: as any container. One that's missing: created again from its saved settings.
    private async Task<string?> StartOtherAsync(AppDefinition app, CancellationToken ct)
    {
        if (containers is null) return $"Gitberth can't start {app.Name} from here.";
        var details = (await docker.InspectContainersAsync([app.ContainerName], ct)).FirstOrDefault();
        if (details is not null)
            return await RefusedAsync(app.ContainerName, ContainerAction.Start, ct)
                ?? await containers.Value.RunContainerAsync(app.ContainerName, ContainerService.IsDatabase(details.Image, null, app.ImageRef), ContainerAction.Start, ct);
        return await CreateAgainAsync(app, ct);
    }

    private async Task<string?> RunOtherAsync(AppDefinition app, ContainerAction action, bool missingIsFine, CancellationToken ct)
    {
        if (containers is null) return $"Gitberth can't act on {app.Name} from here.";
        var details = (await docker.InspectContainersAsync([app.ContainerName], ct)).FirstOrDefault();
        if (details is null) return missingIsFine ? null : "There's no container to restart. Start the app instead.";
        return await RefusedAsync(app.ContainerName, action, ct)
            ?? await containers.Value.RunContainerAsync(app.ContainerName, ContainerService.IsDatabase(details.Image, null, app.ImageRef), action, ct);
    }

    // What the containers list refuses for any container, a row or not: stopping or removing Gitberth itself, say.
    private async Task<string?> RefusedAsync(string name, ContainerAction action, CancellationToken ct) =>
        containers is not null && await containers.Value.FindAsync(name, ct) is { } row && row.Role is not ContainerRole.App
            ? ContainerService.Refusal(row, action)
            : null;

    /// <summary>
    /// The name the app's container goes by now, when it was renamed outside Gitberth: the container with the ID its row
    /// knows, or (for an app Gitberth made) one labelled with its slug, under another name. Null when there's none.
    /// </summary>
    internal async Task<string?> RenamedToAsync(AppDefinition app, CancellationToken ct)
    {
        if (app.ContainerId is { Length: > 0 } id && (await docker.InspectContainersAsync([id], ct)).FirstOrDefault() is { } byId && byId.Name != app.ContainerName)
            return byId.Name;
        if (app.Container is not null) return null;
        var (all, _) = await docker.ListContainersAsync(managedOnly: false, ct);
        return all.FirstOrDefault(c => c.Name != app.ContainerName && c.Labels.TryGetValue("gitberth.app", out var slug) && slug == app.Slug
            && RecreateRules.MadeFromImage(c.Labels))?.Name;
    }

    /// <summary>
    /// Creates a missing app's container again from its saved settings, through the change engine (which starts it, checks
    /// it, and removes it again if it fails). Null when the create started; it runs on its own, and asks again under the
    /// container's lock, right before it creates anything, whether the app is still meant to run (a Stop, a deletion or a
    /// Forget that came meanwhile wins).
    /// </summary>
    internal async Task<string?> CreateAgainAsync(AppDefinition app, CancellationToken ct)
    {
        var appId = app.Id;
        if (catalog is null || (changer is null && Create is null)) return $"{app.ContainerName} is missing, and Gitberth can't create it again from here.";
        // Renamed outside Gitberth, it's still there, on the same volumes: a second one next to it is never made.
        if (await RenamedToAsync(app, ct) is { } other)
            return $"{app.ContainerName}'s container was renamed outside Gitberth: it's {other} now, so Gitberth doesn't create another next to it. "
                + $"Rename it back to {app.ContainerName}, or forget this app and use {other}.";
        if (await catalog.SnapshotAsync(app.Id, ct) is not { } snapshot)
            return $"{app.ContainerName} is missing, and Gitberth has no saved settings to create it again from.";
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).FirstAsync(a => a.Id == app.Id, ct);
        var request = new NewContainerRequest
        {
            Name = app.ContainerName,
            Settings = snapshot.Settings,
            Networks = snapshot.Networks,
            HealthPath = app.HealthCheckPath,
            WebPort = PortRules.WebOf(app.Ports)?.ContainerPort,
            SecretKeys = GitAppSettings.SecretKeysOf(app),
            Wanted = async c => await NotWantedAsync(appId, c) ? NotWanted(app.Name) : null,
        };
        // Its first start is Gitberth's, not a recreate made outside it (AppSync).
        catalog.Creating(app.ContainerName);
        if (Create is { } create) return await create(request, ct);
        var plan = await changer!.Value.PlanCreateAsync(request, ct);
        if (plan.Blocked is { } blocked) return blocked;
        changer.Value.Start(plan);
        return null;
    }

    /// <summary>Stands in for the change engine's create (plan, then start) in tests: what it returns is what the create says.</summary>
    internal Func<NewContainerRequest, CancellationToken, Task<string?>>? Create { get; init; }

    private static InvalidOperationException NotGit(AppDefinition app) =>
        new($"{app.Name} isn't a Git app: there's nothing to deploy. Gitberth recreates its container with the change engine.");

    // The apps (other than this one) that keep the image for Roll back, by name.
    private async Task<List<string>> KeptForRollBackAsync(string imageId, int? except, CancellationToken ct)
    {
        if (imageId.Length == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Where(a => a.PreviousImage == imageId && a.Id != (except ?? 0)).Select(a => a.Name).OrderBy(n => n).ToListAsync(ct);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private async Task<AppDefinition> SetDesiredStateAsync(int appId, DesiredState state)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var app = await db.Apps.FirstAsync(a => a.Id == appId);
        app.DesiredState = state;
        await db.SaveChangesAsync();
        return app;
    }
}

/// <param name="Volumes">Remove the volumes it mounts too (an Existing app's may be postgres-data: the page asks for its name typed in).</param>
/// <param name="Image">Remove its image too, when nothing else uses it.</param>
/// <param name="Databases">Delete its databases' data too (Settings › Databases): its Postgres databases dropped, its Redis's volume removed.</param>
public sealed record RemoveOptions(bool Volumes, bool Image, bool Databases = false);

/// <param name="Domains">The domains' sync after its row went; null when it had no row, or what's kept stays for a change's original.</param>
public sealed record RemoveResult(bool Ok, string Message, SyncReport? Domains);
