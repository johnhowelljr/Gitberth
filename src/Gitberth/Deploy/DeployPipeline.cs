using System.Net.Sockets;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <param name="Build">True: clone and build a new image. False: recreate the container from <paramref name="Image"/> (or the current image).</param>
/// <param name="GitRef">Build this ref rather than the app's branch: a preview promoted to production builds its refs/pull/12/head.</param>
/// <param name="Commit">Build exactly this commit of <paramref name="GitRef"/> (the one the promoted preview ran), not its tip.</param>
/// <param name="NoCache">Build without the build cache: every step runs again, and the base images are pulled.</param>
public sealed record DeployRequest(int AppId, int DeploymentId, bool Build, string? Image = null, string? GitRef = null, string? Commit = null,
    bool NoCache = false);

public sealed class DeployException(string message) : Exception(message);

/// <summary>A new commit the build filters, or its message, leave alone: the deploy ends Skipped and the running version stays.</summary>
internal sealed class DeploySkippedException(SkipVerdict verdict) : Exception(verdict.Reason)
{
    public SkipVerdict Verdict { get; } = verdict;
}

/// <summary>Whether a deploy can keep the app's domains' visitors on a running version throughout (<see cref="DeployPipeline.CanBridgeAsync"/>).</summary>
/// <param name="Ok">The new version starts next to the running one, so domain visitors aren't affected.</param>
/// <param name="Why">Why not, when the app has a domain that will notice; null when it can, or when no domain would notice.</param>
public sealed record BridgeCheck(bool Ok, string? Why)
{
    /// <summary>No domain would notice: domains are off, the app has none, or it isn't running.</summary>
    public static readonly BridgeCheck Unseen = new(false, null);
}

/// <summary>
/// One deploy: clone → detect → build → swap → verify. A failed build never touches the running container,
/// and a new container that doesn't stay up is replaced by the previous one again.
/// </summary>
/// <param name="hostPorts">Picks the spare port a new version starts on next to the old one; without it (in tests) no host port counts as taken.</param>
/// <param name="hostScanner">Says whether the host has an IPv6 address the internet can reach; without it (in tests) it has none.</param>
public sealed class DeployPipeline(
    IDbContextFactory<GitberthDb> dbFactory,
    GitClient git,
    DockerCli docker,
    ProjectAnalyzer analyzer,
    AppLocks locks,
    LiveLogHub hub,
    SecretProtector protector,
    GitberthOptions options,
    IHttpClientFactory httpFactory,
    RepoVariables repoVariables,
    DomainService domains,
    WatchState watch,
    IHostApplicationLifetime lifetime,
    ILogger<DeployPipeline> logger,
    HostPortMap? hostPorts = null,
    HostScanner? hostScanner = null,
    IDeployReporter? reporter = null)
{
    // How long a report may hold a deploy up: GitHub being slow never delays the next deploy for long.
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private const string Restarted = "Gitberth restarted during this deploy. It's tried again automatically.";

    private const int KeepImages = 3;
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    // Held from choosing a spare port until a container publishes it, so two deploys never pick the same one.
    private readonly SemaphoreSlim _spareGate = new(1, 1);

    /// <summary>
    /// The name endings of the container a new version starts in next to the old one (see <see cref="SwapWithBridgeAsync"/>),
    /// and of the old container while a swap moves it aside. A slug never contains '_', so no app's own container can be
    /// named like these.
    /// </summary>
    public const string BridgeSuffix = "_next";
    public const string PreviousSuffix = "_prev";

    // A variable is secret when it's marked so, or when its name or value looks it.
    private static bool Secret(EnvVar v, string value) => v.IsSecret || Masking.IsSecret(v.Key, value);

    /// <summary>
    /// Holds builds off while the build cache is cleared (<see cref="BuildCache"/>), or null when a build runs now. A deploy
    /// that gets to its build meanwhile waits for it, as it waits for another build.
    /// </summary>
    public IDisposable? TryHoldBuilds() => _buildGate.Wait(0) ? new GateHold(_buildGate) : null;

    private sealed class GateHold(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    /// <summary>How long a new container has to stay up before it counts as working.</summary>
    internal TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>How often a new container's state is read while it's checked.</summary>
    internal TimeSpan StatePoll { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How long to wait for cloudflared to say it runs a changed route.</summary>
    internal TimeSpan TunnelWait { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>How long a container may take to open the app's port before visitors are sent to it; zero to not wait.</summary>
    internal TimeSpan ListenWait { get; set; } = TimeSpan.FromSeconds(60);

    public async Task RunAsync(DeployRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var dep = await db.Deployments.FirstOrDefaultAsync(d => d.Id == request.DeploymentId, CancellationToken.None);
        var app = await db.Apps.Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == request.AppId, CancellationToken.None);
        if (dep is null || app is null || dep.Status != DeployStatus.Queued)
        {
            hub.Close(request.DeploymentId);
            return;
        }
        // Only a Git app has something to build and a container a deploy makes; any other app's container is the change
        // engine's. AppActions never queues one, so this only guards against a row that changed source meanwhile.
        if (!app.Deployed)
        {
            dep.Status = DeployStatus.Failed;
            dep.Error = $"{app.Name} isn't a Git app, so it isn't deployed.";
            dep.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            hub.Close(request.DeploymentId);
            return;
        }

        // A preview is deployed with its parent's settings, copied again now, and its parent's variables for previews.
        PreviewInfo? preview = null;
        AppDefinition? parent = null;
        if (app.ParentAppId is int parentId)
        {
            preview = await db.Previews.FirstOrDefaultAsync(p => p.AppId == app.Id, CancellationToken.None);
            parent = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
                .FirstOrDefaultAsync(a => a.Id == parentId, CancellationToken.None);
            // A fork's code is never built or run again once its app stops previewing forks (its preview goes on the next look).
            var forkRefused = preview is { FromFork: true } && parent is { PreviewForks: false };
            // Its volumes as the copy leaves them never take a name another app has, nor a new one a volume Docker has.
            // Checked before they're saved, so a preview that isn't started never holds such a name either, and nothing
            // else's deploy stops for it.
            var volumeProblem = preview is null || parent is null || forkRefused ? null : await PreviewVolumesProblemAsync(db, app, parent);
            if (preview is null || parent is null || forkRefused || volumeProblem is not null)
            {
                dep.Status = DeployStatus.Failed;
                dep.Error = forkRefused ? $"{parent!.Name} doesn't preview pull requests from forks any more, so this one isn't deployed."
                    : volumeProblem ?? "The app this previews is gone, so the preview isn't deployed.";
                dep.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                hub.Close(request.DeploymentId);
                return;
            }
            await db.Entry(app).Collection(a => a.Networks).LoadAsync(CancellationToken.None);
            Previews.PreviewCopy.Sync(app, parent, preview);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Empty values mean "not set", so a suggested-but-unfilled variable never overrides the app's own default. Production
        // gets the app's values for every deploy and for production; a preview its parent's for every deploy and for previews.
        var env = Apps.EnvScopes.ForDeploy((parent ?? app).EnvVars, preview: parent is not null)
            .Select(v => (Var: v, Value: protector.TryUnprotect(v.ProtectedValue)))
            .Where(x => x.Value.Length > 0)
            .ToList();
        // The app's own values that look secret (a secret-sounding name, a URL with a password) are secret whatever their mark:
        // they never go on docker's argv or into the log.
        var own = env.Select(x => new EnvValue(x.Var.Key, x.Value, Secret(x.Var, x.Value))).ToList();
        // A build that's cut short by a restart is retried by the watcher; a recreate or rollback isn't.
        var restarted = request.Build ? Restarted : "Gitberth restarted during this deploy, so it didn't finish. Run it again.";

        var live = hub.Open(dep.Id);
        using var log = new DeployLog(DeployPaths.LogFile(options, dep.Id), live, own.Where(e => e.IsSecret).Select(e => e.Value));
        var workDir = Path.Combine(options.WorkDir, $"dep-{dep.Id}");
        dep.StartedAt = DateTime.UtcNow;
        // Without the cache only when it builds: a recreate has nothing to build.
        dep.NoCache = request.Build && (request.NoCache || dep.NoCache);
        SkipVerdict? skipped = null;
        try
        {
            // The repo's variables in GitHub come first; the app's own values in Gitberth win. Like on Vercel, the GitHub
            // ones are there at build time too: they're plain config the repo already shares.
            var repo = await repoVariables.ForDeployAsync(app.Owner, app.Repo, CancellationToken.None);
            await ForgetVariablesIfDeletedAsync(db, app);
            var ownKeys = own.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Marked secret in Gitberth, even when the value itself comes from GitHub.
            var secretKeys = (parent ?? app).EnvVars.Where(v => v.IsSecret).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (parent is not null && preview is not null)
            {
                log.Step($"A preview of {parent.Name} ({Previews.PreviewNames.Label(preview.Kind, preview.Branch, preview.PullRequest)}): its settings are {parent.Name}'s, and so are its variables for previews");
                var leftOut = Previews.PreviewCopy.LeftOut(parent.Volumes);
                if (leftOut.Count > 0)
                    log.Line($"Not mounted, because a preview never writes into production's folders: {string.Join(", ", leftOut.Select(v => $"{v.Source} at {v.Target}"))}.");
                // The host's devices, and for a fork what reaches the host, stay production's.
                foreach (var line in Previews.PreviewCopy.ExtrasLeftOut(Apps.GitExtras.Parse(parent.RuntimeExtras), preview.FromFork)) log.Line(line);
                foreach (var (tmpfs, volume) in Previews.PreviewCopy.TmpfsUnderVolumes(Apps.GitExtras.Parse(parent.RuntimeExtras), app.Volumes))
                    log.Line($"No tmpfs at {tmpfs.Target}: the preview keeps its volume {VolumeNames.Resolve(app.Slug, volume.Source)} there, and Docker mounts one thing at a path.");
            }
            var usable = repo.Variables.Where(v => v.Value.Length > 0 && EnvRules.IsValidKey(v.Name)).ToList();
            // A preview never gets production's database without being told to: stopped before anything is built, since a
            // build can run migrations.
            if (parent is not null && preview is not null
                && Previews.PreviewDatabases.Find(parent.EnvVars.Select(v => (v.Key, protector.TryUnprotect(v.ProtectedValue), v.Scope)),
                    usable.Select(v => (v.Name, v.Value))) is { Count: > 0 } shared)
                throw new DeployException(Previews.PreviewDatabases.Refusal(shared, parent.Name));
            var overridden = usable.Where(v => ownKeys.Contains(v.Name)).Select(v => v.Name).ToList();
            var listener = usable.Where(v => !ownKeys.Contains(v.Name) && ListenerKeys.Contains(v.Name)).Select(v => v.Name).ToList();
            var fromGitHub = usable
                .Where(v => !ownKeys.Contains(v.Name) && !ListenerKeys.Contains(v.Name))
                .Select(v => new EnvValue(v.Name, v.Value, Masking.IsSecret(v.Name, v.Value, secretKeys)))
                .ToList();
            log.Mask(fromGitHub.Where(e => e.IsSecret).Select(e => e.Value));
            if (repo.Error is not null)
                log.Line(repo.Saved
                    ? "warning: the repo's GitHub variables couldn't be read, so the copy kept from the last read is used. " + repo.Error
                    : "warning: the repo's GitHub variables weren't used. " + repo.Error);
            if (fromGitHub.Count > 0) log.Step($"Using {fromGitHub.Count} variable{(fromGitHub.Count == 1 ? "" : "s")} from the repo's GitHub settings");
            if (overridden.Count > 0) log.Line($"Set in Gitberth for this app, so the GitHub value isn't used: {string.Join(", ", overridden)}");
            if (listener.Count > 0) log.Line($"Not used from GitHub, because Gitberth sets where the app listens: {string.Join(", ", listener)}. Set it on the app in Gitberth to override.");
            var runtimeEnv = fromGitHub.Concat(own).ToList();
            var buildEnv = fromGitHub.Concat(env.Where(x => x.Var.BuildTime).Select(x => new EnvValue(x.Var.Key, x.Value, Secret(x.Var, x.Value)))).ToList();
            var gitHubKeys = fromGitHub.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            // What Vercel's variables tell the app: its main domain, and whether its branch is the repo's production one. A
            // preview is a preview whatever its branch: its own domain, and its parent's as the production one.
            var vercelFor = !app.VercelVariables ? null
                : preview is not null && parent is not null
                    ? new VercelContext(await domains.MainHostnameAsync(app.Id, CancellationToken.None), Production: false,
                        await domains.MainHostnameAsync(parent.Id, CancellationToken.None), preview.PullRequest, Preview: true)
                    : new VercelContext(await domains.MainHostnameAsync(app.Id, CancellationToken.None), await IsProductionBranchAsync(app),
                        Branch: request.GitRef is null ? null : await PromotedBranchAsync(db, app.Id, request.GitRef));
            // What the build fetches: the preview's ref, or the promoted preview's exact commit; the app's branch otherwise.
            var fetch = new BuildSource(request.GitRef ?? preview?.Ref, request.GitRef is null ? null : request.Commit, Promoted: request.GitRef is not null);

            var prepared = request.Build
                ? await BuildAsync(db, app, dep, buildEnv, gitHubKeys, vercelFor, runtimeEnv.Select(e => e.Key).ToList(), fetch, workDir, log, ct)
                : await PrepareImageAsync(app, request, dep, log);

            ct.ThrowIfCancellationRequested();
            if (vercelFor is not null)
            {
                var facts = vercelFor.Facts(app, dep.CommitSha, dep.CommitMessage, dep.CommitAuthor, prepared.Traits);
                foreach (var line in DescribeVercelVariables(runtimeEnv, facts)) log.Line(line);
                runtimeEnv = VercelEnv.AddTo(runtimeEnv, VercelEnv.For(facts));
            }
            else
            {
                log.Line("Vercel's variables are turned off for this app.");
            }
            var owners = await VolumeOwners.LoadAsync(db, CancellationToken.None);
            await KeepDeclaredVolumesAsync(db, app, prepared.Image, owners, log);
            // A preview never starts on another app's volume, nor another app on a preview's: the data would be both's, and
            // removing the preview would delete it. Saves and the names above keep it from happening; this is the last word.
            if (owners.SharedWithPreview(app, app.Volumes) is { } sharedNow) throw new DeployException(SharedVolume(app, sharedNow));
            await SetStatusAsync(db, dep, DeployStatus.Starting);
            var outcome = await SwapAsync(app, prepared.Image, runtimeEnv, PortsFor(app, prepared.Port), (dep.Id, dep.Trigger), log, ct);
            if (!outcome.Ok && lifetime.ApplicationStopping.IsCancellationRequested)
            {
                // Not the new version's fault: Gitberth is stopping. Counted as a failed build so it's retried.
                dep.Status = DeployStatus.Failed;
                dep.Error = restarted;
                log.Step(restarted);
            }
            else if (outcome.Ok)
            {
                app.CurrentImage = prepared.Image;
                app.CurrentCommit = dep.CommitSha ?? app.CurrentCommit;
                // Pending is what's saved now but isn't in this container (or, after a build, its image): a save while it
                // ran (ports for the next deploy, say) stays pending, and one that changed nothing it runs with leaves
                // nothing pending that it made live. Written either way, over whatever a save wrote meanwhile.
                var ran = AppService.Fingerprint(app, protector, options.Network, outcome.Networks);
                var now = await SavedFingerprintAsync(app.Id);
                app.ConfigChangePending = now is { } saved && saved.Runtime != ran.Runtime;
                db.Entry(app).Property(a => a.ConfigChangePending).IsModified = true;
                if (request.Build)
                {
                    var mainNow = vercelFor is null ? null : await domains.MainHostnameAsync(app.Id, CancellationToken.None);
                    app.RebuildPending = StillNeedsBuild(ran.Build, now?.Build, vercelFor is not null, vercelFor?.MainDomain, mainNow);
                    db.Entry(app).Property(a => a.RebuildPending).IsModified = true;
                }
                app.UpdatedAt = DateTime.UtcNow;
                dep.Status = DeployStatus.Succeeded;
                // Only now that the container on it works: the web port follows, and detection's port is the new baseline.
                // The row goes by its container port, not its ID, which a save of the ports while this ran would have
                // changed: the app's TCP row on the port the image moved away from, whether or not the marker moved meanwhile.
                if (prepared.Port is int port && PortRules.WebOf(app.Ports) is { } web)
                {
                    await db.Ports.Where(p => p.AppId == app.Id && p.Protocol == "tcp" && p.ContainerPort == web.ContainerPort)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.ContainerPort, port), CancellationToken.None);
                    hostPorts?.ForgetSaved();
                }
                if (prepared.DetectedPort is int detected) app.DetectedPort = detected;
                log.Step("Deploy succeeded");
                // Saved before the domains are checked, so a slow Cloudflare never leaves a working deploy looking unfinished.
                dep.FinishedAt = DateTime.UtcNow;
                await SaveOutcomeAsync(db, dep, app);
                await RouteDomainsAsync(app, log);
            }
            else if (outcome.Cancelled)
            {
                dep.Status = DeployStatus.Cancelled;
                dep.Error = "Cancelled";
                log.Step("Cancelled. The running container wasn't touched.");
            }
            else
            {
                dep.Status = outcome.RolledBack ? DeployStatus.RolledBack : DeployStatus.Failed;
                dep.Error = outcome.Error;
                log.Step(outcome.OldKeptRunning ? $"The new version didn't pass its checks, so the current one kept running throughout. {outcome.Error}"
                    : outcome.RolledBack ? $"Rolled back to the previous container. {outcome.Error}"
                    : $"Failed: {outcome.Error}");
            }
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Gitberth is stopping (an update or a restart), not a cancel by the user: record a failure so it's retried.
            dep.Status = DeployStatus.Failed;
            dep.Error = restarted;
            log.Step(restarted);
        }
        catch (OperationCanceledException)
        {
            dep.Status = DeployStatus.Cancelled;
            dep.Error = "Cancelled";
            log.Step("Cancelled. The running container wasn't touched.");
        }
        catch (DeploySkippedException skip)
        {
            // Nothing was built and nothing changed: the commit counts as tried (LastAttemptedCommit, saved after the
            // clone), so the watcher doesn't start it again, and the next commit is compared with the running one.
            skipped = skip.Verdict;
            dep.Status = DeployStatus.Skipped;
            dep.SkipReason = skip.Verdict.Reason;
            log.Step($"Skipped: {LowerFirst(skip.Verdict.Reason)}.");
            log.Line(app.CurrentCommit is { Length: >= 7 } running
                ? $"Nothing was built, and {app.Name} keeps running {running[..7]}. Deploy it by hand to build this commit anyway."
                : $"Nothing was built, and {app.Name} keeps running its current version. Deploy it by hand to build this commit anyway.");
        }
        catch (DeployException ex)
        {
            dep.Status = DeployStatus.Failed;
            dep.Error = ex.Message;
            log.Step("Failed: " + ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deploy {DeploymentId} failed unexpectedly", dep.Id);
            dep.Status = DeployStatus.Failed;
            dep.Error = ex.Message;
            log.Step("Unexpected error: " + ex.Message);
        }
        finally
        {
            dep.FinishedAt = DateTime.UtcNow;
            await SaveOutcomeAsync(db, dep, app);
            hub.Close(dep.Id);
            DeployPaths.TryDeleteDirectory(workDir);
        }

        // A preview's commit that didn't make it to production: production follows its branch again, unless a newer
        // promote waits to run. Asked once this one's outcome is saved.
        if (dep.Trigger == DeployTrigger.Promote && dep.Status != DeployStatus.Succeeded) await ForgetPromotionAsync(db, dep);
        await ReportAsync(started: false, dep, skipped?.Kind);
        if (dep.Status == DeployStatus.Succeeded) await PruneImagesAsync(db, app);
    }

    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    // A promote that didn't work lets production follow its branch again (RepoWatcher); when that can't be saved now, the
    // watcher does it on its next look.
    private async Task ForgetPromotionAsync(GitberthDb db, Deployment dep)
    {
        try
        {
            await RepoWatcher.ForgetUnheldPromotionAsync(db, dep.AppId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't let app {AppId} follow its branch again after deploy {DeploymentId}", dep.AppId, dep.Id);
        }
    }

    /// <summary>
    /// Why a preview isn't deployed with its volumes as its copy of the parent's leaves them
    /// (<see cref="Previews.PreviewCopy.SyncVolumes"/>), or null: one would be another app's
    /// (<see cref="VolumeOwners.SharedWithPreview"/>), or one it doesn't have yet would be a volume Docker has already,
    /// which holds another app's data, or a removed app's.
    /// </summary>
    private async Task<string?> PreviewVolumesProblemAsync(GitberthDb db, AppDefinition preview, AppDefinition parent)
    {
        var after = Previews.PreviewCopy.VolumesAfterSync(preview.Volumes, parent.Volumes);
        if ((await VolumeOwners.LoadAsync(db, CancellationToken.None)).SharedWithPreview(preview, after) is { } shared) return SharedVolume(preview, shared);
        var added = after.Where(v => preview.Volumes.All(o => o.Source != v.Source)).Select(v => v.Source).Distinct(StringComparer.Ordinal).ToList();
        if (added.Count == 0) return null;
        if (await docker.VolumeNamesAsync(CancellationToken.None) is not { } existing)
            return "Docker didn't say which volumes it has, so the preview's new volumes can't be checked, and it isn't started. Deploy it again in a moment.";
        return added.FirstOrDefault(s => existing.Contains(VolumeNames.Resolve(preview.Slug, s))) is { } taken ? TakenVolume(preview, taken) : null;
    }

    /// <summary>
    /// Each VOLUME the image declares that none of the app's mounts covers becomes a named volume of its own, saved in its
    /// settings (<see cref="DeclaredVolumes"/>), before its container is made: otherwise Docker gives the new container an
    /// empty anonymous volume and leaves the old one's behind. Saved on its own, at once, so the rest of the app's changes
    /// still wait for the swap to work; read back into this deploy's copy, so what it runs matches what's saved. A name
    /// another app has, or that a preview of this one would then share (<see cref="VolumeOwners.ClashOf"/>), gets a number;
    /// so does a preview's new one that Docker has already, which holds another app's data, or a removed app's.
    /// </summary>
    private async Task KeepDeclaredVolumesAsync(GitberthDb db, AppDefinition app, string image, VolumeOwners owners, DeployLog log)
    {
        var declared = await docker.ImageVolumesAsync(image, CancellationToken.None);
        // A path the app has a tmpfs at is mounted already (a preview's are its parent's, copied before this).
        var tmpfs = (Apps.GitExtras.Parse(app.RuntimeExtras)?.Tmpfs ?? []).Select(t => t.Target).ToList();
        var existing = app.IsPreview && DeclaredVolumes.Missing(app.Volumes, declared, tmpfs: tmpfs).Count > 0
            ? await docker.VolumeNamesAsync(CancellationToken.None)
                ?? throw new DeployException("Docker didn't say which volumes it has, so the volumes the image declares can't be named yet. Deploy it again in a moment.")
            : null;
        var added = DeclaredVolumes.Missing(app.Volumes, declared,
            (source, sources) => owners.ClashOf(app, source, sources) is not null || existing?.Contains(VolumeNames.Resolve(app.Slug, source)) == true, tmpfs);
        if (added.Count == 0) return;
        await using (var save = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            foreach (var v in added) v.AppId = app.Id;
            save.Volumes.AddRange(added);
            await save.SaveChangesAsync(CancellationToken.None);
        }
        // A query, not the navigation's Load, which skips a collection that's loaded already: tracked, they join app.Volumes.
        await db.Volumes.Where(v => v.AppId == app.Id).LoadAsync(CancellationToken.None);
        foreach (var v in added)
            log.Line($"The image declares VOLUME {v.Target}: it's kept in the volume {VolumeNames.Resolve(app.Slug, v.Source)}, so its data stays from one deploy to the next (Settings › Volumes).");
    }

    /// <summary>Why a deploy stops when a preview would share a volume with another app (<see cref="VolumeOwners.SharedWithPreview"/>).</summary>
    private static string SharedVolume(AppDefinition app, VolumeOwners.Clash shared) => app.IsPreview
        ? $"The volume {shared.Source} would be {shared.Name}, which is also {shared.Who}'s, so the preview isn't started: a preview never shares a volume with another app. Remove the preview: it comes back under another name with its next commit."
        : $"The volume {shared.Source} would be {shared.Name}, which is also {shared.Who}'s, so {app.Name} isn't started with it: a preview never shares a volume with another app. Remove that preview: it comes back under another name with its next commit.";

    /// <summary>Why a preview's deploy stops when a volume it doesn't have yet would be one Docker has already.</summary>
    private static string TakenVolume(AppDefinition preview, string source) =>
        $"The volume {source} would be {VolumeNames.Resolve(preview.Slug, source)}, which Docker has already, so the preview isn't started: that volume holds another app's data, or a removed app's. Remove the preview: it comes back under another name with its next commit.";

    /// <summary>Tells the reporter (GitHub) about a deploy. Whatever goes wrong there is logged, never the deploy's problem.</summary>
    private async Task ReportAsync(bool started, Deployment dep, SkipKind? skip = null)
    {
        if (reporter is null) return;
        var report = new DeployReport(dep.AppId, dep.Id, dep.Trigger, dep.CommitSha, started ? DeployStatus.Building : dep.Status, dep.Error, skip) { HookName = dep.HookName };
        using var timeout = new CancellationTokenSource(ReportTimeout);
        try
        {
            if (started) await reporter.StartedAsync(report, timeout.Token);
            else await reporter.FinishedAsync(report, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't report deploy {DeploymentId}", dep.Id);
        }
    }

    // Where generated Dockerfiles make the app listen. A GitHub variable shared by the repo's apps doesn't move it.
    private static readonly HashSet<string> ListenerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PORT", "HOST", "HOSTNAME", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS",
    };

    /// <summary>
    /// Saves the deploy. When something changed the app underneath (its settings saved, or the app deleted, during the
    /// deploy), the deployment's own outcome is still saved, so a deploy can never stay "in progress".
    /// </summary>
    private async Task SaveOutcomeAsync(GitberthDb db, Deployment dep, AppDefinition app)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Couldn't save deploy {DeploymentId} together with its app; saving its outcome on its own", dep.Id);
        }
        try
        {
            db.ChangeTracker.Clear();
            await db.Deployments.Where(d => d.Id == dep.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, dep.Status)
                .SetProperty(d => d.Error, dep.Error)
                .SetProperty(d => d.StartedAt, dep.StartedAt)
                .SetProperty(d => d.FinishedAt, dep.FinishedAt)
                .SetProperty(d => d.CommitSha, dep.CommitSha)
                .SetProperty(d => d.CommitMessage, dep.CommitMessage)
                .SetProperty(d => d.CommitAuthor, dep.CommitAuthor)
                .SetProperty(d => d.ImageTag, dep.ImageTag)
                .SetProperty(d => d.SkipReason, dep.SkipReason)
                .SetProperty(d => d.NoCache, dep.NoCache), CancellationToken.None);
            await db.Apps.Where(a => a.Id == app.Id).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.CurrentImage, app.CurrentImage)
                .SetProperty(a => a.CurrentCommit, app.CurrentCommit)
                .SetProperty(a => a.LastAttemptedCommit, app.LastAttemptedCommit)
                .SetProperty(a => a.ConfigChangePending, app.ConfigChangePending)
                .SetProperty(a => a.RebuildPending, app.RebuildPending)
                .SetProperty(a => a.DetectedKind, app.DetectedKind)
                .SetProperty(a => a.DetectedSummary, app.DetectedSummary)
                .SetProperty(a => a.DetectedPort, app.DetectedPort), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't save the outcome of deploy {DeploymentId}", dep.Id);
        }
    }

    /// <summary>The app's settings as they're saved now (<see cref="AppService.Fingerprint"/>); null when the app is gone.</summary>
    private async Task<(string Runtime, string Build)?> SavedFingerprintAsync(int appId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == appId, CancellationToken.None);
        return app is null ? null : AppService.Fingerprint(app, protector, options.Network);
    }

    /// <summary>
    /// The kept copy of the repo's GitHub variables goes with its last app. Deleting that app while this deploy read the
    /// variables could otherwise leave a copy behind, so the deploy checks too after writing it.
    /// </summary>
    private async Task ForgetVariablesIfDeletedAsync(GitberthDb db, AppDefinition app)
    {
        var (owner, repo) = (app.Owner.ToLowerInvariant(), app.Repo.ToLowerInvariant());
        if (!await db.Apps.AsNoTracking().AnyAsync(a => a.Owner.ToLower() == owner && a.Repo.ToLower() == repo, CancellationToken.None))
            await repoVariables.ForgetAsync(app.Owner, app.Repo, CancellationToken.None);
    }

    /// <param name="Image">The image to run.</param>
    /// <param name="Port">The container port to publish instead of the app's own, because the app now listens elsewhere.</param>
    /// <param name="DetectedPort">The port detection gave, saved as the app's new baseline once the deploy works.</param>
    /// <param name="Traits">What the image's build found out about the app.</param>
    private sealed record Prepared(string Image, int? Port, int? DetectedPort, AppTraits Traits);

    /// <summary>
    /// A recreate or rollback: the image that's there, and what its build found out. One built before the app's port moved
    /// listens on its own port.
    /// </summary>
    private async Task<Prepared> PrepareImageAsync(AppDefinition app, DeployRequest request, Deployment dep, DeployLog log)
    {
        var image = request.Image ?? app.CurrentImage ?? throw new DeployException("There's no image yet. Deploy the app first.");
        dep.ImageTag = image;
        log.Step($"Recreating the container from {image}");
        if (!await docker.ImageExistsAsync(image, CancellationToken.None))
            throw new DeployException($"Image {image} no longer exists. Deploy to rebuild it.");
        var labels = await docker.ImageLabelsAsync(image, CancellationToken.None);
        var traits = AppTraits.FromLabels(labels);
        // An image from before Gitberth kept these (a rollback to an old deploy) is told what the current one was built with.
        if (traits is null && app.CurrentImage is { } current && current != image
            && AppTraits.FromLabels(await docker.ImageLabelsAsync(current, CancellationToken.None)) is { } now)
        {
            traits = now;
            log.Line($"{image} was built before Gitberth kept what a build finds out about the app, so it gets the variables {current} was built for.");
        }
        traits ??= AppTraits.None;
        if (int.TryParse(labels.GetValueOrDefault(ImageLabels.Port), out var port) && port is > 0 and < 65536
            && Follow(app.Ports, app.DetectedPort, app.DetectedKind, port) is { } follow)
        {
            log.Step($"{image} listens on port {follow.To}, so the web port follows it");
            return new Prepared(image, follow.To, follow.To, traits);
        }
        return new Prepared(image, null, null, traits);
    }

    /// <summary>Where Vercel's variables say the app lives, and whether it's the production deploy; null when they're off for the app.</summary>
    /// <param name="ProductionDomain">A preview's parent's main domain.</param>
    /// <param name="PullRequest">A pull request's preview: its number.</param>
    /// <param name="Branch">The branch the commit comes from, when it isn't the app's: a promoted preview's.</param>
    private sealed record VercelContext(string? MainDomain, bool Production, string? ProductionDomain = null, int? PullRequest = null, bool Preview = false,
        string? Branch = null)
    {
        public DeployFacts Facts(AppDefinition app, string? sha, string? message, string? author, AppTraits traits) =>
            new(MainDomain, app.Owner, app.Repo, Branch is { Length: > 0 } branch ? branch : app.Branch, sha, message, author, traits, Production, ProductionDomain, PullRequest, Preview);
    }

    /// <summary>
    /// The branch of the preview a promote builds (a pull request's own branch), for VERCEL_GIT_COMMIT_REF: as on Vercel,
    /// a promoted deployment keeps the branch its commit came from. Null when that preview is gone.
    /// </summary>
    private static Task<string?> PromotedBranchAsync(GitberthDb db, int appId, string gitRef) =>
        db.Previews.AsNoTracking().Where(p => p.ParentAppId == appId && p.Ref == gitRef).Select(p => (string?)p.Branch)
            .FirstOrDefaultAsync(CancellationToken.None);

    /// <summary>Whether the app deploys the repo's default branch, which is production on Vercel; any other branch is a preview there.</summary>
    private async Task<bool> IsProductionBranchAsync(AppDefinition app)
    {
        try
        {
            var remote = await git.LsRemoteAsync(new GitHubRepoRef(app.Owner, app.Repo), CancellationToken.None);
            watch.RecordDefaultBranch(app.Owner, app.Repo, remote.DefaultBranch);
        }
        catch (GitException)
        {
            // GitHub can't be reached: the default branch the watcher saw last, else production.
        }
        return watch.DefaultBranch(app.Owner, app.Repo) is not { } main || main == app.Branch;
    }

    /// <summary>What the deploy log says about Vercel's variables, given the deploy's other variables (which win).</summary>
    internal static IEnumerable<string> DescribeVercelVariables(IReadOnlyList<EnvValue> env, DeployFacts facts)
    {
        var copies = facts.Traits.Prefix is { } prefix ? $", also as {prefix}VERCEL_…" : "";
        var environment = facts.Production ? "production" : facts.Preview ? "preview" : $"preview, because {facts.Branch} isn't the repo's default branch";
        var url = env.FirstOrDefault(e => e.Key == "VERCEL_URL")?.Value ?? (facts.Traits.ChecksVercel ? null : facts.MainDomain);
        // A preview's production URL is its parent's main domain.
        var productionUrl = facts.Preview ? facts.ProductionDomain : facts.MainDomain;
        yield return url is not null
            ? $"Vercel's variables are set, with VERCEL_URL={url} and VERCEL_ENV={environment}{copies}."
            : productionUrl is { } main
                ? $"Vercel's variables are set, with VERCEL_PROJECT_PRODUCTION_URL={main} and VERCEL_ENV={environment}{copies}. VERCEL_URL isn't: "
                    + (facts.MainDomain is null ? "the preview has no domain of its own."
                        : "the app's code checks process.env.VERCEL, and code like that takes VERCEL_URL to mean it runs on Vercel.")
                : $"Vercel's variables are set, with VERCEL_ENV={environment}{copies}. VERCEL_URL isn't, because Gitberth doesn't route a domain to the app.";
        var own = env.FirstOrDefault(e => VercelEnv.AuthUrlKeys.Contains(e.Key));
        if (own is not null && facts.MainDomain is { } domain && IsLocal(own.Value))
            yield return $"warning: {own.Key} is {own.Value}, from the app's settings or GitHub, so sign-in links point there. Remove it, and Gitberth sets AUTH_URL and NEXTAUTH_URL to https://{domain}.";
        if (!facts.Traits.AuthJs || own is not null) yield break;
        yield return facts.MainDomain is { } address
            ? $"The app signs people in with Auth.js, so AUTH_URL and NEXTAUTH_URL are https://{address}, its main domain."
            : "warning: the app signs people in with Auth.js, which needs its public address, but it has no domain. Add one, or set AUTH_URL.";
    }

    private static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "0.0.0.0");

    /// <summary>The ports to publish: copies of the app's, with the web port moved when the app now listens elsewhere.</summary>
    private static List<PortMapping> PortsFor(AppDefinition app, int? port)
    {
        var web = PortRules.WebOf(app.Ports);
        return app.Ports.OrderBy(p => p.Id)
            .Select(p => new PortMapping
            {
                Id = p.Id, AppId = p.AppId, Access = p.Access, HostIp = p.HostIp, HostPort = p.HostPort, Protocol = p.Protocol, IsWeb = p.IsWeb,
                ContainerPort = p == web && port is int moved ? moved : p.ContainerPort,
            })
            .ToList();
    }

    /// <summary>What a build fetches when it isn't the app's branch tip.</summary>
    /// <param name="Ref">A preview's ref (refs/pull/12/head, refs/heads/feature); null: the app's branch.</param>
    /// <param name="Commit">The exact commit to build (a promoted preview's); null: the ref's tip.</param>
    /// <param name="Promoted">Production builds a preview's commit.</param>
    private sealed record BuildSource(string? Ref, string? Commit, bool Promoted);

    /// <param name="gitHubKeys">Which of <paramref name="buildEnv"/> come from the repo's GitHub variables rather than the app's own.</param>
    /// <param name="vercelFor">What Vercel's variables tell the app; null when they're off for it.</param>
    /// <param name="runtimeKeys">Every name the app or the repo sets, build-time or not: those win over Vercel's during the build too.</param>
    /// <param name="fetch">What to fetch, when it isn't the app's branch tip.</param>
    private async Task<Prepared> BuildAsync(GitberthDb db, AppDefinition app, Deployment dep, List<EnvValue> buildEnv, IReadOnlySet<string> gitHubKeys,
        VercelContext? vercelFor, IReadOnlyList<string> runtimeKeys, BuildSource fetch, string workDir, DeployLog log, CancellationToken ct)
    {
        DeployPaths.TryDeleteDirectory(workDir);
        var srcDir = Path.Combine(workDir, "src");
        var buildDir = Path.Combine(workDir, "build");
        Directory.CreateDirectory(buildDir);

        await SetStatusAsync(db, dep, DeployStatus.Cloning);
        log.Step(fetch switch
        {
            { Promoted: true, Commit: { Length: >= 7 } sha } => $"Fetching {sha[..7]} of {app.RepoFullName} from {fetch.Ref}, the preview promoted to production",
            { Ref: { } named } => $"Fetching {app.RepoFullName} ({named})",
            _ => $"Cloning {app.RepoFullName} ({app.Branch})",
        });
        CommitInfo commit;
        try
        {
            commit = fetch.Ref is { } gitRef
                ? await git.FetchAsync(new GitHubRepoRef(app.Owner, app.Repo), gitRef, fetch.Commit, srcDir, log.Line, ct)
                : await git.CloneAsync(new GitHubRepoRef(app.Owner, app.Repo), app.Branch, srcDir, log.Line, ct);
        }
        catch (GitException ex)
        {
            throw new DeployException(ex.Message);
        }
        dep.CommitSha = commit.Sha;
        dep.CommitMessage = commit.Subject.Length > 500 ? commit.Subject[..500] : commit.Subject;
        dep.CommitAuthor = commit.Author;
        // A promoted preview's commit isn't its branch's: what the watcher knows about the branch stays as it was, so a
        // promote that fails never makes the branch's commit look untried (and deployed over a rollback).
        if (!fetch.Promoted) app.LastAttemptedCommit = commit.Sha;
        // Saved now, so a restart during the build still knows which commit was being tried.
        await db.SaveChangesAsync(CancellationToken.None);
        log.Step($"Commit {commit.Sha[..7]} by {commit.Author}: {commit.Subject}");
        // Before GitHub hears of a build: a skipped commit never gets a pending status, only the success its skip reports.
        if (BuildFilters.CanSkip(dep.Trigger) && await SkipVerdictAsync(app, commit, srcDir, log, ct) is { } skip) throw new DeploySkippedException(skip);
        await ReportAsync(started: true, dep);

        var appDir = app.Subdirectory?.Trim().Trim('/') ?? "";
        var folder = Inside(srcDir, appDir) ?? throw new DeployException($"The subfolder '{app.Subdirectory}' doesn't exist on {app.Branch}.");

        // Vercel's variables are there while it builds too (front ends build NEXT_PUBLIC_VERCEL_URL in). What the build
        // finds out goes on the image, so a later recreate from it sets the same ones.
        var traits = VercelEnv.Detect(folder, srcDir);
        var vercel = vercelFor is null
            ? []
            : VercelEnv.ForBuild(vercelFor.Facts(app, commit.Sha, dep.CommitMessage, commit.Author, traits));
        var vercelKeys = vercel.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        buildEnv = VercelEnv.AddTo(buildEnv, vercel, runtimeKeys);
        // vercel.json's cron jobs travel with the image too, so a rollback runs the jobs of the image it goes back to.
        var (cronLabel, cronWarnings) = CronJobs.ForBuild(srcDir, appDir);
        foreach (var warning in cronWarnings) log.Line("warning: " + warning);
        if (cronLabel is not null)
            log.Step($"Cron jobs from vercel.json: {string.Join(", ", CronJobs.FromLabel(cronLabel).Select(j => $"{j.Path} ({j.Schedule})"))}");

        var volumeTargets = app.Volumes.Select(v => v.Target).ToList();
        // In a Dockerfile with Gitberth's build-args line, secret values go in as BuildKit secrets: the build steps see them,
        // but unlike build args they're never written into the image's history.
        var plainKeys = buildEnv.Where(e => !e.IsSecret).Select(e => e.Key).ToList();
        var secretKeys = buildEnv.Where(e => e.IsSecret).Select(e => e.Key).ToList();
        string dockerfilePath;
        string context;
        // The Dockerfile as written, before Gitberth fills in its marker lines: whether it has them decides how values go in.
        string source;
        // Gitberth filled in its marker lines (a generated or custom Dockerfile; the repo's own is built as it is).
        var filledIn = true;
        int? movedPort = null, detectedPort = null;
        if (app.DockerfileMode == DockerfileMode.Custom && !string.IsNullOrWhiteSpace(app.CustomDockerfile))
        {
            log.Step("Using your custom Dockerfile");
            // One started from a generated Dockerfile for a sub-folder app keeps building from the repo root, as it was written to.
            context = app.CustomDockerfile.Contains(DockerfileMarkers.RepoRootContext, StringComparison.Ordinal) ? srcDir : folder;
            source = app.CustomDockerfile;
            dockerfilePath = WriteDockerfile(buildDir, context, DockerfileMarkers.Expand(source, plainKeys, volumeTargets, secretKeys), null, dep.Id, log);
        }
        else
        {
            var detection = analyzer.Analyze(srcDir, new AnalyzeOptions(app.ProjectHint, AppDir: appDir));
            // Apps from before Gitberth remembered the port detection gave start from what it gave their kind.
            app.DetectedPort ??= DefaultPort(app.DetectedKind);
            if (detection.ContainerPort is int port)
            {
                detectedPort = port;
                if (Follow(app.Ports, app.DetectedPort, app.DetectedKind, port) is { } follow)
                {
                    log.Step($"The app now listens on port {follow.To}, not {follow.Web.ContainerPort}, so its web port moves with it");
                    movedPort = follow.To;
                }
            }
            app.DetectedKind = detection.Kind.ToString();
            app.DetectedSummary = detection.Title;
            foreach (var warning in detection.Warnings) log.Line("warning: " + warning);
            context = Inside(srcDir, detection.BuildContext) ?? throw new DeployException($"The build folder '{detection.BuildContext}' isn't inside the repo.");
            switch (detection.Kind)
            {
                case ProjectKind.Unknown:
                    throw new DeployException("Gitberth couldn't work out how to build this project. Add a Dockerfile to the repo, or set a custom Dockerfile in the app's settings.");
                case ProjectKind.Dockerfile:
                    log.Step($"Using the repo's {detection.RepoDockerfile}");
                    dockerfilePath = Inside(srcDir, detection.RepoDockerfile!, mustBeFolder: false)
                        ?? throw new DeployException($"{detection.RepoDockerfile} isn't inside the repo.");
                    source = File.ReadAllText(dockerfilePath);
                    filledIn = false;
                    break;
                default:
                    log.Step($"Detected {detection.Title}");
                    // A sub-folder app built from the root uses Gitberth's ignore file: the repo's root one was written for its own images.
                    var ownIgnore = appDir.Length > 0 && detection.BuildContext.Length == 0;
                    source = detection.Dockerfile!;
                    dockerfilePath = WriteDockerfile(buildDir, context, DockerfileMarkers.Expand(source, plainKeys, volumeTargets, secretKeys), detection.DockerIgnore, dep.Id, log, ownIgnore);
                    break;
            }
            // An app in a sub-folder that reaches the rest of the repo (`cd ..`, "file:../server", COPY website/) builds from the root.
            if (appDir.Length > 0 && detection.BuildContext.Length == 0) log.Step($"Building {appDir}/ from the repo root, so it can use the rest of the repo");
        }

        var tag = $"{app.ImageRepository}:{commit.Sha[..7]}";
        dep.ImageTag = tag;
        var args = new List<string>
        {
            "build", "--progress=plain", "-f", dockerfilePath, "-t", tag,
            "--label", $"gitberth.app={app.Slug}", "--label", $"gitberth.commit={commit.Sha}",
        };
        // Every step runs again, and the base images are pulled: for a build the cache got wrong, or a base image that moved.
        if (dep.NoCache) args.AddRange(["--no-cache", "--pull"]);
        // The port the image's app listens on: its web port.
        if ((movedPort ?? PortRules.WebOf(app.Ports)?.ContainerPort) is int published)
            args.AddRange(["--label", $"{ImageLabels.Port}={published}"]);
        args.AddRange(traits.LabelArgs());
        if (cronLabel is not null) args.AddRange(["--label", $"{ImageLabels.Crons}={cronLabel}"]);
        var values = await BuildValuesAsync(source, filledIn, buildEnv, gitHubKeys, vercelKeys, () => BuildSecrets.KeyAsync(dbFactory, protector, ct));
        if (values.SetBeforeFrom.Count > 0) log.Line($"Not passed to the build, because the Dockerfile sets them before its first FROM: {string.Join(", ", values.SetBeforeFrom)}");
        args.AddRange(values.Args);
        var processEnv = values.Environment;

        if (_buildGate.CurrentCount == 0) log.Step("Waiting for another build to finish…");
        await _buildGate.WaitAsync(ct);
        try
        {
            await SetStatusAsync(db, dep, DeployStatus.Building);
            log.Step(dep.NoCache ? $"Building {tag} without the build cache: every step runs again, and the base images are pulled" : $"Building {tag}");
            // A build can't join Gitberth's network, so it runs on the host's network with every container on Gitberth's
            // network added by name: the build reaches the same database or API the app uses when it runs.
            var hosts = await docker.NetworkHostsAsync(CancellationToken.None);
            if (hosts.Count > 0) log.Line("Reachable by name while building: " + string.Join(", ", hosts.Select(h => h.Host)));
            List<string> reach = ["--network", "host", .. hosts.SelectMany(h => new[] { "--add-host", $"{h.Host}:{h.Ip}" })];
            var reachable = true;
            Task<ProcessResult> Build() => docker.RunAsync(reachable ? [.. args, .. reach, context] : [.. args, context],
                new ProcessOptions { Environment = processEnv, OnLine = log.Line, MaxCapture = 16 * 1024 }, ct);
            var result = await Build();
            // "granting entitlement network.host is not allowed": a Docker host set up to refuse it.
            if (!result.Ok && (result.StdOut + result.StdErr).Contains("entitlement network.host", StringComparison.Ordinal))
            {
                log.Step("This Docker host doesn't let builds use its network, so the build can't reach the other containers. Building without them…");
                reachable = false;
                result = await Build();
            }
            // --pull asks a registry for every base image, and one only this host has (built or loaded here) is in none: it's
            // built again with the host's copy, still without the cache.
            if (!result.Ok && dep.NoCache && BasePullFailed(result.StdOut + result.StdErr))
            {
                log.Step("A base image is only on this host, not in a registry, so it's built again without pulling the base images…");
                args.Remove("--pull");
                result = await Build();
            }
            if (!result.Ok)
                throw new DeployException(result.ExitCode == -1 ? result.ErrorText : "The image build failed; the log above shows why. The running container wasn't touched.");
        }
        finally
        {
            _buildGate.Release();
        }
        return new Prepared(tag, movedPort, detectedPort, traits);
    }

    /// <summary>
    /// Whether a new commit is left alone (<see cref="BuildFilters"/>): its message asks not to be deployed, or none of the
    /// files it changed since the commit the app runs is one the app's build filters watch. Null: it's built. When the
    /// filters can't tell (nothing ran yet, the running commit can't be fetched, git can't compare them) or there's no
    /// image to keep running, it's built, and the log says why. Without filters only the message is read, and nothing
    /// more is fetched.
    /// </summary>
    private async Task<SkipVerdict?> SkipVerdictAsync(AppDefinition app, CommitInfo commit, string srcDir, DeployLog log, CancellationToken ct)
    {
        var asked = BuildFilters.ForMessage(commit.FullMessage);
        var filtered = BuildFilters.Any(app.WatchPaths, app.IgnoredPaths);
        if (asked is null && !filtered) return null;
        // A skip keeps the running version: without one, the commit is built whatever it says.
        var why = asked is not null ? $"The commit message says {BuildFilters.MarkerIn(commit.FullMessage)}, but" : "The build filters can't skip it:";
        if (app.CurrentImage is not { } image)
        {
            log.Line($"{why} nothing runs yet, so it's built.");
            return null;
        }
        if (!await docker.ImageExistsAsync(image, CancellationToken.None))
        {
            log.Line($"{why} the running version's image {image} is gone, so it's built.");
            return null;
        }
        if (asked is not null) return asked;

        // The files that changed since the commit that runs, so changes add up across skipped commits.
        if (app.CurrentCommit is not { } running)
        {
            log.Line("The build filters can't tell what changed: no commit has run yet. Building it.");
            return null;
        }
        IReadOnlyList<string> changed;
        try
        {
            var repo = new GitHubRepoRef(app.Owner, app.Repo);
            if (!string.Equals(running, commit.Sha, StringComparison.OrdinalIgnoreCase)) await git.FetchCommitAsync(repo, running, srcDir, ct);
            changed = await git.ChangedFilesAsync(srcDir, running, ct);
        }
        catch (GitException ex)
        {
            log.Line($"The build filters can't tell what changed since {Short(running)}, the commit that runs ({ex.Message.TrimEnd('.')}). Building it.");
            return null;
        }
        if (BuildFilters.Relevant(changed, app.WatchPaths, app.IgnoredPaths) is { } watched)
        {
            var more = changed.Count - 1;
            log.Line($"The build filters let it through: {watched} changed since {Short(running)}{(more > 0 ? $", with {more} more file{(more == 1 ? "" : "s")}" : "")}.");
            return null;
        }
        return BuildFilters.ForChanges(changed, app.WatchPaths, app.IgnoredPaths);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>
    /// Whether a build failed because BuildKit couldn't find a base image in its registry ("failed to resolve source
    /// metadata for docker.io/library/base:1: pull access denied…" or "…: not found"), as --pull makes it look for one only
    /// this host has.
    /// </summary>
    internal static bool BasePullFailed(string output) =>
        output.Contains("failed to resolve source metadata", StringComparison.OrdinalIgnoreCase)
        && (output.Contains("pull access denied", StringComparison.OrdinalIgnoreCase) || output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("repository does not exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>What hands a build its values: docker build's arguments, and the environment the docker CLI reads values from.</summary>
    /// <param name="SetBeforeFrom">The repo's GitHub values left out because the Dockerfile sets them before its first FROM.</param>
    internal sealed record BuildValues(List<string> Args, Dictionary<string, string> Environment, IReadOnlyList<string> SetBeforeFrom);

    /// <summary>
    /// The build's values as docker build takes them. Plain ones go in as build args, their values through the CLI's
    /// environment where they can. In a Dockerfile with Gitberth's build-args line, secret ones go in as BuildKit secrets,
    /// which are never written into the image's history; and where Gitberth filled that line in, the steps that see them
    /// are keyed to their values by <see cref="DockerfileMarkers.SecretsArg"/> (<see cref="BuildSecrets"/>), because
    /// BuildKit leaves secrets out of its cache.
    /// </summary>
    /// <param name="source">The Dockerfile as written, before Gitberth fills in its marker lines: whether it has them decides how values go in.</param>
    /// <param name="filledIn">Gitberth filled in its marker lines (a generated or custom Dockerfile); the repo's own is built as it is.</param>
    /// <param name="gitHubKeys">Which of <paramref name="buildEnv"/> come from the repo's GitHub variables.</param>
    /// <param name="vercelKeys">Which of <paramref name="buildEnv"/> are Vercel's.</param>
    /// <param name="key">This installation's key for the secrets' stamp, asked for only when secrets are mounted.</param>
    internal static async Task<BuildValues> BuildValuesAsync(string source, bool filledIn, IReadOnlyList<EnvValue> buildEnv, IReadOnlySet<string> gitHubKeys,
        IReadOnlySet<string> vercelKeys, Func<Task<byte[]>> key)
    {
        var args = new List<string>();
        var processEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var asSecrets = DockerfileMarkers.TakesSecrets(source);
        // A hand-written Dockerfile's ARGs before its first FROM pick base images and tool versions; a GitHub variable
        // shared by the whole repo (NODE_VERSION, say) shouldn't change them. The app's own build-time values still do.
        IReadOnlySet<string> globalArgs = asSecrets ? new HashSet<string>() : DockerfileMarkers.GlobalArgs(source);
        var setBeforeFrom = buildEnv.Where(e => gitHubKeys.Contains(e.Key) && globalArgs.Contains(e.Key)).Select(e => e.Key).ToList();
        // Without Gitberth's build-args line a Dockerfile only takes the ARGs it declares, and Docker warns about the rest.
        var declared = asSecrets ? null : DeclaredArgs(source);
        var skipped = setBeforeFrom.Concat(buildEnv.Where(e => declared is not null && vercelKeys.Contains(e.Key) && !declared.Contains(e.Key)).Select(e => e.Key)).ToList();
        var mounted = new List<EnvValue>();
        foreach (var e in buildEnv.Where(e => !skipped.Contains(e.Key)))
        {
            if (asSecrets && e.IsSecret)
            {
                // The docker CLI reads it from a neutral name, so no variable it uses itself can be shadowed.
                var name = $"GITBERTH_SECRET_{mounted.Count}";
                args.AddRange(["--secret", $"id={e.Key},env={name}"]);
                processEnv[name] = e.Value;
                mounted.Add(e);
                continue;
            }
            args.Add("--build-arg");
            if (EnvPassing.CanPassViaEnvironment(e.Key))
            {
                args.Add(e.Key);
                processEnv[e.Key] = e.Value;
            }
            else
            {
                args.Add($"{e.Key}={e.Value}");
            }
        }
        // BuildKit keeps secret values out of its cache, so the steps that see them are keyed to them by a build arg that
        // gives nothing away (BuildSecrets): a changed value, or another scope's, builds them again.
        if (filledIn && mounted.Count > 0)
            args.AddRange(["--build-arg", $"{DockerfileMarkers.SecretsArg}={BuildSecrets.Stamp(await key(), mounted)}"]);
        return new BuildValues(args, processEnv, setBeforeFrom);
    }

    /// <summary>Every ARG a Dockerfile declares, in any stage.</summary>
    internal static HashSet<string> DeclaredArgs(string dockerfile) =>
        dockerfile.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("ARG ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=')[0]))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A path inside the clone, or null when it leaves the clone or doesn't exist.</summary>
    private static string? Inside(string srcDir, string relative, bool mustBeFolder = true)
    {
        var full = Path.GetFullPath(Path.Combine(srcDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(srcDir);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return (mustBeFolder ? Directory.Exists(full) : File.Exists(full)) ? full : null;
    }

    /// <param name="preferOwnIgnore">Use Gitberth's ignore file even when the context has its own .dockerignore.</param>
    private string WriteDockerfile(string buildDir, string context, string content, string? dockerIgnore, int deploymentId, DeployLog log, bool preferOwnIgnore = false)
    {
        // The Dockerfile lives outside the clone, so the repo is never modified. BuildKit reads Dockerfile.dockerignore,
        // next to it, in place of the context's .dockerignore.
        var path = Path.Combine(buildDir, "Dockerfile");
        File.WriteAllText(path, content);
        if (dockerIgnore is not null && (preferOwnIgnore || !File.Exists(Path.Combine(context, ".dockerignore"))))
            File.WriteAllText(path + ".dockerignore", dockerIgnore);
        File.WriteAllText(DeployPaths.DockerfileCopy(options, deploymentId), content);
        foreach (var line in content.TrimEnd().Split('\n')) log.Line("  | " + line);
        return path;
    }

    /// <param name="OldKeptRunning">The new version failed next to the old one, which never stopped.</param>
    /// <param name="Cancelled">Cancelled while the new version was checked next to the old one, which never stopped.</param>
    /// <param name="Networks">The app's own networks as they were saved when the swap read them, under its lock: one that's gone included.</param>
    private sealed record SwapOutcome(bool Ok, bool RolledBack, string? Error, bool OldKeptRunning = false, bool Cancelled = false,
        IReadOnlyList<AppNetwork>? Networks = null);

    /// <summary>What every container one swap starts is created with besides the app's own settings.</summary>
    /// <param name="Networks">The app's own networks, read under its lock.</param>
    /// <param name="NetworkIds">The IDs docker run is given for networks whose names have capitals, by name (see <see cref="NetworkJoin.Id"/>).</param>
    /// <param name="GatewayPriority">The engine takes gateway priorities (API 1.48, Docker 28).</param>
    /// <param name="LanIpv4Only">The host has an IPv6 address the internet can reach, so the LAN's ports go on IPv4 only.</param>
    /// <param name="Deploy">This deploy, recorded on the containers it starts.</param>
    private sealed record SwapContext(IReadOnlyList<string> LogOptions, IReadOnlyList<AppNetwork> Networks, IReadOnlyDictionary<string, string> NetworkIds,
        bool GatewayPriority, bool LanIpv4Only, (int Id, DeployTrigger Trigger) Deploy);

    private ContainerSpec Spec(AppDefinition app, string image, List<EnvValue> env, IReadOnlyList<PortMapping> ports, SwapContext with,
        string? name = null, string? restartPolicy = null, bool bridge = false) =>
        ContainerSpecFactory.For(app, image, env, options.Network, with.LogOptions, ports, name, restartPolicy, bridge, with.Deploy,
            with.Networks, with.GatewayPriority, with.LanIpv4Only, with.NetworkIds);

    /// <param name="ports">What to publish. The app's saved ports only change once the swap has worked.</param>
    /// <param name="deploy">This deploy, recorded on the containers it starts.</param>
    private async Task<SwapOutcome> SwapAsync(AppDefinition app, string image, List<EnvValue> env, IReadOnlyList<PortMapping> ports,
        (int Id, DeployTrigger Trigger) deploy, DeployLog log, CancellationToken ct)
    {
        // Not cancellable once started: a half-finished swap is worse than a finished one.
        using var _ = await locks.AcquireAsync(app.Id, CancellationToken.None);
        // Stopped since this deploy was queued (a scheduled restart's, then the owner's Stop): it isn't started again.
        await using (var fresh = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            if (await fresh.Apps.AsNoTracking().Where(a => a.Id == app.Id).Select(a => (DesiredState?)a.DesiredState).FirstOrDefaultAsync() == DesiredState.Stopped)
            {
                log.Step("The app was stopped while this deploy waited, so it isn't started again.");
                return new SwapOutcome(false, false, "Cancelled: the app was stopped.", Cancelled: true);
            }
        var name = app.ContainerName;
        var previous = name + PreviousSuffix;
        try
        {
            await docker.EnsureNetworkAsync();
        }
        catch (InvalidOperationException ex)
        {
            return new SwapOutcome(false, false, ex.Message);
        }

        // A temporary container an interrupted deploy left behind goes first, once visitors are off it.
        await TidyBridgeAsync(app, log);
        if (await docker.ContainerExistsAsync(previous)) await docker.RemoveAsync(previous);
        var hadOld = await docker.ContainerExistsAsync(name);
        var (gatewayPriority, lanIpv4Only) = await HostTraitsAsync();
        // Read under the app's lock, which joining a network takes too: a network joined while the image built isn't lost.
        var (saved, there, ids) = await NetworksAsync(app, log);
        var with = new SwapContext(await docker.GetLogOptionsAsync(), there, ids, gatewayPriority, lanIpv4Only, deploy);
        if (hadOld)
        {
            var bridge = await CanBridgeAsync(app, ports);
            if (bridge.Why is { } why) log.Line($"The swap takes a few seconds of downtime: {why}.");
            if (bridge.Ok && await SwapWithBridgeAsync(app, image, env, ports, with, log, ct) is { } bridged) return bridged with { Networks = saved };
        }

        if (hadOld)
        {
            log.Step("Stopping the current container");
            await docker.StopAsync(name);
            var moved = await docker.RenameAsync(name, previous);
            if (!moved.Ok)
            {
                await docker.StartAsync(name);
                return new SwapOutcome(false, false, "Couldn't move the old container aside: " + moved.ErrorText);
            }
        }

        var spec = Spec(app, image, env, ports, with);
        log.Step("Starting the new container");
        log.Line(spec.ToDisplayString());
        var (args, processEnv) = spec.BuildRunCommand();
        var run = await docker.RunAsync(args, new ProcessOptions { Environment = processEnv });
        var failure = run.Ok ? null : "docker run failed: " + run.ErrorText;
        failure ??= await VerifyAsync(app, spec, log, ct);
        if (failure is null)
        {
            if (hadOld) await docker.RemoveAsync(previous);
            return new SwapOutcome(true, false, null, Networks: saved);
        }

        await ShowLastOutputAsync(name, log);
        await docker.RemoveAsync(name);
        if (!hadOld) return new SwapOutcome(false, false, failure);

        var restored = await docker.RenameAsync(previous, name);
        if (restored.Ok) restored = await docker.StartAsync(name);
        return restored.Ok
            ? new SwapOutcome(false, true, failure)
            : new SwapOutcome(false, false, $"{failure} Restoring the previous container also failed: {restored.ErrorText}");
    }

    // Only when the container exists; if docker run failed before creating it there's nothing to show.
    private async Task ShowLastOutputAsync(string container, DeployLog log)
    {
        var tail = await docker.LogsTailAsync(container, 60);
        var output = (tail.StdOut + tail.StdErr).TrimEnd();
        if (!tail.Ok || output.Length == 0) return;
        log.Step("Last output from the new container:");
        foreach (var line in output.Split('\n')) log.Line("  " + line);
    }

    /// <summary>
    /// Whether a deploy can start the new version next to the running one, with the domains' visitors kept on whichever is
    /// up (<see cref="SwapWithBridgeAsync"/>): the app has a domain, on a tunnel that reaches apps by their host port, a web
    /// port on the host that keeps its container port, no volume (two copies writing the same files at once isn't safe),
    /// and a spare port is free. It changes nothing, so anything that says domain visitors aren't affected asks it first.
    /// </summary>
    /// <param name="ports">What the deploy publishes; the app's saved ports when null. The tunnel follows the saved ones.</param>
    public async Task<BridgeCheck> CanBridgeAsync(AppDefinition app, IReadOnlyList<PortMapping>? ports = null, CancellationToken ct = default)
    {
        if (!await domains.EnabledAsync(ct) || !await domains.TokenSavedAsync(ct) || (await domains.HostnamesAsync(app.Id, ct)).Count == 0) return BridgeCheck.Unseen;
        if (await docker.InspectStateAsync(app.ContainerName, ct) is not { Running: true }) return BridgeCheck.Unseen;
        var main = PortRules.WebOf(ports ?? app.Ports);
        var saved = PortRules.WebOf(app.Ports);
        // The same rules as the Ports card's "domain visitors aren't affected", then what only a deploy can find out.
        var tunnelOnHost = (await domains.FindTunnelAsync(ct)).Tunnel is { HostNetwork: true };
        var why = BridgeRules.WhyNot(new BridgeFacts(DomainsOn: true, HasDomain: true, Running: true, app.Volumes.Count > 0, main is not null, tunnelOnHost, main?.Access == PortAccess.Internal))
            // The tunnel follows the port the app's saved settings name, which only changes once this deploy has worked.
            ?? (main!.ContainerPort != saved?.ContainerPort ? $"the web port moves to port {main.ContainerPort} with this deploy"
                : await docker.ContainerExistsAsync(app.ContainerName + BridgeSuffix, ct) ? "the temporary container of an earlier deploy couldn't be removed yet"
                : await SpareAsync(await domains.TunnelHostPortsAsync(ct), ct) is null ? "no host port is free for the new version to start on first"
                : null);
        return new BridgeCheck(why is null, why);
    }

    /// <summary>
    /// A free port on 127.0.0.1 for the new version to start on next to the old one, from the start of the spare ports up;
    /// null when there's none, or Docker can't say which are free. Without the host-port map (in tests) none is taken.
    /// </summary>
    /// <param name="routed">Ports the tunnel's rules go to: the new version mustn't get anyone else's visitors.</param>
    private async Task<int?> SpareAsync(IEnumerable<int> routed, CancellationToken ct = default)
    {
        if (hostPorts is null) return HostPortMap.Spare([], HostScan.DefaultRandomRange, 8100, routed);
        try
        {
            return await hostPorts.SpareAsync(routed, ct);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Docker didn't say which host ports are free, so a new version can't start next to the old one");
            return null;
        }
    }

    /// <summary>
    /// What the engine and the host decide for every container a deploy starts: whether networks can be joined at a lower
    /// gateway priority (API 1.48, Docker 28), and whether the LAN's ports go on IPv4 only, because the host has an IPv6
    /// address the internet can reach (or its network couldn't be read, so it may have) and the LAN is this network, not
    /// the internet. From the host's last scan: only the first after Gitberth starts is waited for.
    /// </summary>
    private async Task<(bool GatewayPriority, bool LanIpv4Only)> HostTraitsAsync()
    {
        var api = await docker.ServerApiVersionAsync(CancellationToken.None);
        var scan = hostScanner is null ? null : await hostScanner.LatestAsync(CancellationToken.None);
        return (api is not null && api >= DockerCli.GwPriorityApi, scan?.LanIpv4Only == true);
    }

    /// <summary>
    /// The app's own networks as they're saved now, and the ones of them that are there: one that isn't any more (removed
    /// outside Gitberth) is left out, with a warning, rather than failing the deploy. A network whose name has capitals,
    /// the shared one included, goes to docker run by its ID (see <see cref="NetworkJoin.Id"/>).
    /// </summary>
    private async Task<(List<AppNetwork> Saved, List<AppNetwork> There, Dictionary<string, string> Ids)> NetworksAsync(AppDefinition app, DeployLog log)
    {
        List<AppNetwork> rows;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            rows = await db.AppNetworks.AsNoTracking().Where(n => n.AppId == app.Id).OrderBy(n => n.Id).ToListAsync(CancellationToken.None);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (HasCapitals(options.Network) && await NetworkIdAsync(options.Network) is { } shared) ids[options.Network] = shared;
        var there = new List<AppNetwork>();
        foreach (var row in rows)
        {
            if (row.Network == options.Network)
            {
                there.Add(row);
            }
            else if (await NetworkIdAsync(row.Network) is { } id)
            {
                there.Add(row);
                if (HasCapitals(row.Network)) ids[row.Network] = id;
            }
            else
            {
                log.Line($"warning: the network {row.Network} isn't there any more, so the new container isn't on it.");
            }
        }
        return (rows, there, ids);
    }

    // A network's ID, by its exact name; null when there's no such network.
    private async Task<string?> NetworkIdAsync(string network) =>
        await docker.RunAsync(["network", "inspect", "--format", "{{.Id}}", network]) is { Ok: true } found && found.StdOut.Trim() is { Length: > 0 } id ? id : null;

    // docker run lower-cases what --network's name= says, so only a name without capitals can be given to it as it is.
    private static bool HasCapitals(string network) => network.Any(char.IsUpper);

    /// <summary>
    /// A swap that visitors on the app's domains don't notice:
    /// <list type="number">
    /// <item>the new version starts next to the old one, on a spare port, and has to pass the same checks and open its port;</item>
    /// <item>the tunnel sends the app's domains to it, and Gitberth waits until cloudflared runs that;</item>
    /// <item>the old container stops, and the new version starts again on the app's own port and is checked again;</item>
    /// <item>the domains go back to that port once it answers, and the temporary container goes.</item>
    /// </list>
    /// The old container serves until step 3, so a new version that fails step 1 never interrupts anything. Null when the
    /// new version couldn't be started on the side, or the tunnel couldn't be moved: then the usual swap runs. An own LAN
    /// address stays with the old container until step 3: the temporary one isn't on gitberth-lan (two containers can't
    /// hold one address), and Docker lets go of a stopped container's address, so the new one takes it when it starts, as
    /// in the usual swap and a rollback (checked on CT 101).
    /// </summary>
    private async Task<SwapOutcome?> SwapWithBridgeAsync(AppDefinition app, string image, List<EnvValue> env, IReadOnlyList<PortMapping> ports,
        SwapContext with, DeployLog log, CancellationToken ct)
    {
        var name = app.ContainerName;
        var bridge = name + BridgeSuffix;
        var previous = name + PreviousSuffix;
        if (PortRules.WebOf(ports) is not { } main) return null;
        // Ports the tunnel already sends somewhere, Gitberth's or not: the new version mustn't get anyone else's visitors.
        var routed = await domains.TunnelHostPortsAsync();
        ContainerSpec side;
        await _spareGate.WaitAsync(CancellationToken.None);
        try
        {
            if (await SpareAsync(routed) is not int spare)
            {
                log.Line("The swap takes a few seconds of downtime: no host port is free for the new version to start on first.");
                return null;
            }
            // Only its web port, on the host only: that's where the tunnel reaches it, and nobody else needs it yet. And
            // without the app's names on its networks for now: other apps only reach it once it has passed its checks.
            side = Spec(app, image, env,
                [new PortMapping { Id = main.Id, AppId = main.AppId, ContainerPort = main.ContainerPort, Protocol = main.Protocol, Access = PortAccess.HostOnly, HostPort = spare, IsWeb = true }],
                with, name: bridge, restartPolicy: "no", bridge: true);
            log.Step($"Starting the new version next to the current one, on 127.0.0.1:{spare}, so visitors are served throughout");
            log.Line(side.ToDisplayString());
            var (args, processEnv) = side.BuildRunCommand();
            var run = await docker.RunAsync(args, new ProcessOptions { Environment = processEnv });
            if (!run.Ok)
            {
                await docker.RemoveAsync(bridge);
                log.Line($"It couldn't start there ({run.ErrorText}), so the usual swap is used.");
                return null;
            }
            // The kept map doesn't have this port yet: another deploy reads the containers' ports again before it picks one.
            hostPorts?.ForgetSaved();
        }
        finally
        {
            _spareGate.Release();
        }

        var failure = await VerifyAsync(app, side, log, ct) ?? await ListeningAsync(bridge, side.Ports, log);
        if (failure is not null)
        {
            await ShowLastOutputAsync(bridge, log);
            await docker.RemoveAsync(bridge);
            return ct.IsCancellationRequested
                ? new SwapOutcome(false, false, "Cancelled", Cancelled: true)
                : new SwapOutcome(false, true, failure, OldKeptRunning: true);
        }
        // It passed: other apps reach it by the app's name now too, and it comes back like the app would if it crashes.
        if (await docker.AddNetworkAliasAsync(bridge, app.Slug) is { } aliasError) log.Line($"warning: other apps can't reach the new version by name yet ({aliasError}).");
        await docker.UpdateRestartPolicyAsync(bridge, app.RestartPolicy);

        log.Step($"Sending {string.Join(", ", await domains.HostnamesAsync(app.Id))} to the new version");
        var since = DateTime.UtcNow;
        var there = await domains.RouteToAsync(app.Id, bridge);
        if (!there.Moved)
        {
            foreach (var note in there.Report.Notes.Where(n => n.Problem)) log.Line("warning: " + note.Text);
            log.Line("The tunnel couldn't be pointed at the new version, so the usual swap is used.");
            // If the tunnel can't be put back either, the new version keeps serving whoever it gets until a later check moves them.
            await ComeHomeAsync(app, log);
            return null;
        }
        await WaitForTunnelAsync(there.Version is long version ? new TunnelChange(version, since) : null, log);
        // It might have stopped since its checks: then visitors go back to the old version, and the new one isn't shipped.
        var bridgeState = await docker.InspectStateAsync(bridge);
        if (bridgeState is not { Running: true })
        {
            await ShowLastOutputAsync(bridge, log);
            await ComeHomeAsync(app, log);
            return new SwapOutcome(false, true, $"The new version stopped right after its checks{(bridgeState is null ? "" : $" (exit code {bridgeState.ExitCode})")}.", OldKeptRunning: true);
        }

        log.Step("Stopping the previous version");
        await docker.StopAsync(name);
        var aside = await docker.RenameAsync(name, previous);
        if (!aside.Ok)
        {
            await docker.StartAsync(name);
            await ComeHomeAsync(app, log);
            return new SwapOutcome(false, false, "Couldn't move the old container aside: " + aside.ErrorText);
        }

        var spec = Spec(app, image, env, ports, with);
        log.Step($"Starting the new version on the app's own port, {main.HostPort}");
        log.Line(spec.ToDisplayString());
        var (ownArgs, ownEnv) = spec.BuildRunCommand();
        var started = await docker.RunAsync(ownArgs, new ProcessOptions { Environment = ownEnv });
        failure = started.Ok ? null : "docker run failed: " + started.ErrorText;
        failure ??= await VerifyAsync(app, spec, log, ct);
        if (failure is not null)
        {
            await ShowLastOutputAsync(name, log);
            await docker.RemoveAsync(name);
            var restored = await docker.RenameAsync(previous, name);
            if (restored.Ok) restored = await docker.StartAsync(name);
            // Visitors stay on the new version until the previous one answers on the app's port again.
            await ComeHomeAsync(app, log);
            return restored.Ok
                ? new SwapOutcome(false, true, failure)
                : new SwapOutcome(false, false, $"{failure} Restoring the previous container also failed: {restored.ErrorText}");
        }

        log.Step("Sending visitors back to the app's own port");
        await ComeHomeAsync(app, log);
        await docker.RemoveAsync(previous);
        return new SwapOutcome(true, false, null);
    }

    private async Task WaitForTunnelAsync(TunnelChange? change, DeployLog? log)
    {
        if (change is null) return;
        if (await domains.WaitForTunnelAsync(change.Version, change.At, TunnelWait)) log?.Line("cloudflared runs the new route.");
        else log?.Line($"warning: cloudflared didn't confirm the new route within {TunnelWait.TotalSeconds:0} seconds, so Gitberth carried on.");
    }

    /// <summary>
    /// Where Gitberth reaches a container's web port: inside a container, by name on the shared network, which needs no
    /// host port; run directly, on localhost at its host port. Null when there's no web port, or, run directly, it isn't
    /// published.
    /// </summary>
    private (string Host, int Port, PortMapping Web)? WebAddress(string container, IReadOnlyList<PortMapping> ports) =>
        PortRules.WebOf(ports) is not { } web ? null
        : options.RunningInContainer ? (container, web.ContainerPort, web)
        : web.Access != PortAccess.Internal && web.HostPort is int hostPort ? ("localhost", hostPort, web)
        : null;

    /// <summary>
    /// Waits until a container accepts connections on the app's web port, so visitors are only sent to it once it can
    /// answer. Staying up isn't the same as listening: a JVM app, say, can take longer than the first check. Null once it
    /// listens, or when there's nothing to wait for (no web port, or outside a container one that isn't published), else
    /// why not. Returns at once when the container stops.
    /// </summary>
    private async Task<string?> ListeningAsync(string container, IReadOnlyList<PortMapping> ports, DeployLog? log)
    {
        if (ListenWait <= TimeSpan.Zero || WebAddress(container, ports) is not (var host, var number, var port)) return null;
        var until = DateTime.UtcNow + ListenWait;
        var said = false;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, number, timeout.Token);
                return null;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
            }
            if (await docker.InspectStateAsync(container) is not { Running: true }) return $"{container} stopped before it opened port {port.ContainerPort}.";
            if (DateTime.UtcNow >= until) return $"{container} didn't open port {port.ContainerPort} within {ListenWait.TotalSeconds:0} seconds.";
            if (!said) log?.Line($"Waiting for {container} to open port {port.ContainerPort}…");
            said = true;
            await Task.Delay(500, CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends the app's domains back to its own container once that answers, then removes the temporary one when
    /// cloudflared is off it. While the tunnel can't be changed, the temporary container keeps serving and stays;
    /// <see cref="TidyBridgesAsync"/> tries again.
    /// </summary>
    private async Task<bool> ComeHomeAsync(AppDefinition app, DeployLog? log)
    {
        var bridge = app.ContainerName + BridgeSuffix;
        // Unless the temporary container has stopped anyway, visitors only go back to one that answers.
        if (await ListeningAsync(app.ContainerName, app.Ports, log) is { } notYet && await docker.InspectStateAsync(bridge) is { Running: true })
        {
            log?.Line($"warning: {notYet} Visitors stay on {bridge} for now; Gitberth moves them back on a later check.");
            logger.LogWarning("The domains of {App} stay on {Container}: {Why}", app.Slug, bridge, notYet);
            return false;
        }
        var since = DateTime.UtcNow;
        var home = await domains.RouteHomeAsync(app.Id);
        if (!home.Moved)
        {
            // No domain left (one removed during the deploy), and no route Gitberth wrote goes there: nobody is sent to it.
            if ((await domains.HostnamesAsync(app.Id)).Count == 0 && await RoutedToAsync(bridge) == false)
            {
                await docker.RemoveAsync(bridge);
                log?.Line($"Removed {bridge}: the app has no domain any more.");
                return true;
            }
            foreach (var note in home.Report.Notes.Where(n => n.Problem)) log?.Line("warning: " + note.Text);
            log?.Line(home.Report.Targets is null
                ? $"warning: Gitberth couldn't check where the tunnel sends the app's domains, so {bridge} keeps running for now. It tries again on a later check."
                : $"warning: the tunnel still sends visitors to {bridge}, so it keeps running for now. Gitberth moves them back and removes it on a later check.");
            logger.LogWarning("The domains of {App} may still go to {Container}; it stays until they're moved back", app.Slug, bridge);
            return false;
        }
        // When this sync had nothing left to change (an earlier one moved them), what cloudflared must run is the last change.
        await WaitForTunnelAsync(home.Version is long version ? new TunnelChange(version, since) : domains.LastTunnelChange, log);
        await docker.RemoveAsync(bridge);
        log?.Line($"Removed {bridge}");
        if (log is null) logger.LogInformation("Removed {Container}: the domains of {App} are back on its own container", bridge, app.Slug);
        return true;
    }

    /// <summary>
    /// Whether a route Gitberth wrote (an app's domain, or a removed one still on its way out) goes to a port the container
    /// publishes; null when Docker can't say.
    /// </summary>
    private async Task<bool?> RoutedToAsync(string container)
    {
        var (containers, error) = await docker.ListContainersAsync(managedOnly: true);
        if (error is not null) return null;
        var ports = DockerCli.ParsePublishedPorts(containers.FirstOrDefault(c => c.Name == container)?.Ports ?? "").ToHashSet();
        return (await domains.RoutedHostPortsAsync()).Any(r => ports.Contains(r.Port));
    }

    /// <summary>
    /// Removes temporary containers that deploys left behind (Gitberth restarted in the middle of one, or the tunnel couldn't
    /// be changed back), once the app's domains no longer go to them. Only containers labelled as one count, so no other
    /// container is ever touched. An app whose deploy is swapping right now is skipped.
    /// </summary>
    public async Task TidyBridgesAsync(CancellationToken ct = default)
    {
        var (containers, error) = await docker.ListContainersAsync(managedOnly: true, ct);
        if (error is not null) return;
        var owners = containers.Where(c => c.Labels.GetValueOrDefault(ContainerSpecFactory.BridgeLabel) == "true")
            .Select(c => c.Labels.GetValueOrDefault("gitberth.app")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (owners.Count == 0) return;
        List<AppDefinition> all;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            all = await db.Apps.AsNoTracking().Include(a => a.Ports).ToListAsync(ct);
        foreach (var app in all.Where(a => owners.Contains(a.Slug)))
        {
            using var held = await locks.TryAcquireAsync(app.Id);
            if (held is not null) await TidyBridgeAsync(app, log: null);
        }
    }

    private async Task TidyBridgeAsync(AppDefinition app, DeployLog? log)
    {
        var bridge = app.ContainerName + BridgeSuffix;
        if (!await docker.ContainerExistsAsync(bridge)) return;
        if (await domains.EnabledAsync())
        {
            await ComeHomeAsync(app, log);
            return;
        }
        // Domains are turned off, so Gitberth leaves the tunnel alone: the container only goes once no route Gitberth wrote goes to it.
        if (await RoutedToAsync(bridge) != false) return;
        await docker.RemoveAsync(bridge);
        log?.Line($"Removed {bridge}, left by an earlier deploy");
        if (log is null) logger.LogInformation("Removed {Container}, left by an earlier deploy", bridge);
    }

    /// <summary>
    /// Makes sure the app's domains reach the container that's now running (a changed host port moves its routes). A
    /// problem here is reported in the log but never fails a deploy that worked. Skipped while domains are turned off.
    /// </summary>
    private async Task RouteDomainsAsync(AppDefinition app, DeployLog log)
    {
        try
        {
            if (!await domains.EnabledAsync()) return;
            var hostnames = await domains.HostnamesAsync(app.Id);
            // Any app's domains, and removed ones still on their way out: a deploy can change which container answers on a port.
            if (!await domains.AnyAsync()) return;
            var report = await domains.SyncAsync(CancellationToken.None);
            var notes = report.Notes.Where(n => n.Hostname is null || hostnames.Contains(n.Hostname)).ToList();
            foreach (var note in notes) log.Line(note.Problem ? "warning: " + note.Text : note.Text);
            if (hostnames.Count > 0 && !notes.Any(n => n.Problem)) log.Line($"Domains: {string.Join(", ", hostnames)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Routing the domains of {App} failed", app.Slug);
            log.Line("warning: the app's domains couldn't be checked: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether an app a deploy just built still needs a build: its build settings were saved differently while it ran
    /// (<see cref="AppService.Fingerprint"/>), or, with Vercel's variables on, another domain is its main one now than the
    /// one the build put in the image. False once the app is gone.
    /// </summary>
    /// <param name="saved">The build settings saved now; null when the app is gone.</param>
    internal static bool StillNeedsBuild(string ran, string? saved, bool vercel, string? builtMain, string? mainNow) =>
        saved is not null && (saved != ran || vercel && mainNow != builtMain);

    /// <summary>
    /// The port to move a container port to when the app now listens elsewhere (another folder, framework or EXPOSE
    /// line), or null to leave it. Only a port detection picked moves; one set by hand stays.
    /// </summary>
    internal static int? PortToFollow(int current, int? lastDetected, string? lastKind, int detected)
    {
        if (detected == current) return null;
        return (lastDetected ?? DefaultPort(lastKind)) == current ? detected : null;
    }

    /// <summary>
    /// The web port and the container port it moves to when the app now listens elsewhere: only while it's still on the
    /// port detection picked (<see cref="PortToFollow"/>), however many ports the app has, and never onto a port another of
    /// them already has, UDP or not: an image Gitberth labelled before the web marker names the app's first port by ID,
    /// whatever its protocol. Null when nothing moves, as for a worker without a web port.
    /// </summary>
    internal static (PortMapping Web, int To)? Follow(IReadOnlyCollection<PortMapping> ports, int? lastDetected, string? lastKind, int detected)
    {
        if (PortRules.WebOf(ports) is not { } web || PortToFollow(web.ContainerPort, lastDetected, lastKind, detected) is not int to) return null;
        return ports.Any(p => p != web && p.ContainerPort == to) ? null : (web, to);
    }

    // What detection gives each kind of app, for apps added before Gitberth remembered the port it picked.
    private static int? DefaultPort(string? kind) => Enum.TryParse<ProjectKind>(kind, out var k)
        ? k switch
        {
            ProjectKind.Static or ProjectKind.NodeSpa or ProjectKind.DotNetWasm => 80,
            ProjectKind.NextJs or ProjectKind.Nuxt or ProjectKind.SvelteKit or ProjectKind.NodeServer => 3000,
            ProjectKind.DotNetWeb => 8080,
            _ => null,
        }
        : null;

    /// <summary>The container must stay up (no exit, no restart, healthy) and answer its health URL if one is set.</summary>
    private async Task<string?> VerifyAsync(AppDefinition app, ContainerSpec spec, DeployLog log, CancellationToken ct)
    {
        log.Step($"Checking that it stays up for {StableFor.TotalSeconds:0} seconds…");
        var until = DateTime.UtcNow + StableFor;
        ContainerState? state = null;
        while (DateTime.UtcNow < until || state?.Health == "starting")
        {
            if (ct.IsCancellationRequested) return "Cancelled while checking the new container.";
            await Task.Delay(StatePoll, CancellationToken.None);
            state = await docker.InspectStateAsync(spec.Name);
            if (state is null) return "The new container disappeared.";
            if (state.OomKilled) return "The new container ran out of memory. Raise its memory limit.";
            if (!state.Running || state.RestartCount > 0) return $"The new container exited (code {state.ExitCode}).";
            if (state.Health == "unhealthy") return "The image's HEALTHCHECK reports unhealthy.";
            if (DateTime.UtcNow > until + HealthTimeout) return "The image's HEALTHCHECK never reported healthy.";
        }

        if (string.IsNullOrWhiteSpace(app.HealthCheckPath)) return null;
        if (WebAddress(spec.Name, spec.Ports) is not (var host, var number, _))
        {
            log.Line(PortRules.WebOf(spec.Ports) is null
                ? "The app has no web port, so the health URL check is skipped."
                : "The web port isn't published, and Gitberth runs outside Docker here, so the health URL check is skipped.");
            return null;
        }
        var path = app.HealthCheckPath.StartsWith('/') ? app.HealthCheckPath : "/" + app.HealthCheckPath;
        var url = $"http://{host}:{number}{path}";
        log.Step($"Checking {url}");
        var client = httpFactory.CreateClient("health");
        var deadline = DateTime.UtcNow + HealthTimeout;
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return "Cancelled while checking the new container.";
            try
            {
                using var response = await client.GetAsync(url, CancellationToken.None);
                if ((int)response.StatusCode < 400)
                {
                    log.Line($"Health check OK ({(int)response.StatusCode})");
                    return null;
                }
                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex.Message;
            }
            var current = await docker.InspectStateAsync(spec.Name);
            if (current is null || !current.Running) return "The new container exited during the health check.";
            await Task.Delay(2000, CancellationToken.None);
        }
        return $"Health check failed for {url}: {last}";
    }

    /// <summary>
    /// Keeps the images of the last <see cref="KeepImages"/> different successful deploys, for rollback. Counted by image,
    /// so restarts and settings changes that reuse the current one don't push older images out.
    /// </summary>
    internal async Task PruneImagesAsync(GitberthDb db, AppDefinition app)
    {
        try
        {
            var keep = await db.Deployments.AsNoTracking()
                .Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded && d.ImageTag != null)
                .GroupBy(d => d.ImageTag!)
                .Select(g => new { Tag = g.Key, Last = g.Max(d => d.Id) })
                .OrderByDescending(t => t.Last)
                .Take(KeepImages)
                .Select(t => t.Tag)
                .ToListAsync();
            var keepSet = keep.ToHashSet(StringComparer.Ordinal);
            if (app.CurrentImage is not null) keepSet.Add(app.CurrentImage);
            foreach (var tag in await docker.ListImageTagsAsync(app.ImageRepository))
            {
                if (!keepSet.Contains(tag)) await docker.RemoveImageAsync(tag);
            }
            await docker.PruneDanglingImagesAsync(app.Slug);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image cleanup failed for {App}", app.Slug);
        }
    }

    private static async Task SetStatusAsync(GitberthDb db, Deployment dep, DeployStatus status)
    {
        dep.Status = status;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
