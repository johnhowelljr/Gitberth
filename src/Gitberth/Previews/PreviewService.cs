using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Previews;

/// <summary>A Git app's preview settings, as its Settings tab edits them.</summary>
/// <param name="Limit">How many previews may run at once.</param>
/// <param name="ExpiryDays">A preview with no new commit for this many days is removed.</param>
/// <param name="Forks">Also preview pull requests from forks.</param>
/// <param name="Protect">Put previews' domains behind Cloudflare Access.</param>
public sealed record PreviewSettings(PreviewMode Mode, int Limit, int ExpiryDays, bool Forks, bool Protect)
{
    public static PreviewSettings Of(AppDefinition app) => new(app.PreviewMode, app.PreviewLimit, app.PreviewExpiryDays, app.PreviewForks, app.ProtectPreviews);

    /// <summary>Why they can't be saved, or null.</summary>
    public string? Problem =>
        Limit is < 1 or > 20 ? "Between 1 and 20 previews can run at once."
        : ExpiryDays is < 1 or > 365 ? "Previews are removed after 1 to 365 days without a new commit."
        : null;
}

/// <summary>What saving a Git app's preview settings did.</summary>
/// <param name="Problem">Why nothing was saved; null when the settings were.</param>
/// <param name="Warning">They were saved, but the previews' domains didn't follow in Cloudflare yet: why.</param>
public sealed record PreviewSave(string? Problem, string? Warning = null);

/// <summary>One preview, as its parent's Previews tab lists it.</summary>
/// <param name="Key">Its own page's key: its container's name.</param>
/// <param name="Url">The pull request on GitHub, or the branch.</param>
/// <param name="Domain">Its domain, when it has one.</param>
/// <param name="LanUrl">Where the LAN reaches its web port.</param>
/// <param name="DeploymentId">Its newest deploy, for its log.</param>
/// <param name="CanPromote">It runs a commit production can build.</param>
public sealed record PreviewRow(int AppId, string Key, string Name, PreviewKind Kind, string Branch, int? PullRequest, string? Title, string Url,
    PreviewState State, string StateText, string? Commit, string? CommitMessage, string? Domain, string? LanUrl, DateTime LastActivityAt, DateTime ExpiresAt,
    int? DeploymentId, bool CanPromote, bool FromFork, bool Running)
{
    public string Label => PreviewNames.Label(Kind, Branch, PullRequest);
}

/// <summary>What a preview's own page says about it.</summary>
/// <param name="ParentKey">The parent's page's key.</param>
/// <param name="LeftOut">The parent's host folders, which the preview doesn't mount.</param>
public sealed record PreviewOf(PreviewInfo Info, int ParentId, string ParentName, string ParentKey, IReadOnlyList<string> LeftOut, string Url)
{
    public string Label => PreviewNames.Label(Info.Kind, Info.Branch, Info.PullRequest);
}

/// <summary>
/// Preview deployments: each branch or pull request of a Git app that gets one runs as an app of its own (its own slug,
/// container, image repository, queue slot, lock, volumes and domain) with its parent's settings and its parent's
/// variables for previews. The watcher hands every pass's view of a repo to <see cref="CheckRepoAsync"/>, which makes,
/// updates and removes previews (<see cref="PreviewPlanner"/>) and starts their deploys within the parent's limit. The
/// Previews tab lists them and acts on them; Promote builds a preview's commit as production.
/// </summary>
/// <param name="hostPorts">Picks a preview's host port; without it (in tests) the first one no saved port has from its container port up.</param>
/// <param name="gate">One change to previews at a time, shared with deleting an app (AppActions); without it, one of its own.</param>
public sealed class PreviewService(
    IDbContextFactory<GitberthDb> dbFactory,
    AppActions actions,
    AppService apps,
    DomainService domains,
    GitHubPulls pulls,
    GitClient git,
    DockerCli docker,
    ActivityLog activity,
    WatchState watch,
    ILogger<PreviewService> logger,
    HostPortMap? hostPorts = null,
    PreviewGate? gate = null,
    RepoVariables? repoVariables = null,
    Security.SecretProtector? protector = null)
{
    /// <summary>How often a repo's pull requests are asked for at most when nothing says they changed.</summary>
    internal TimeSpan PullsEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The clock the planner reads; tests move it.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    // One change to previews at a time: the watcher's pass, a removal by hand, a settings save, a promote, and an app
    // being deleted with its previews.
    private readonly PreviewGate _gate = gate ?? new PreviewGate();

    /// <summary>A repo's open pull requests as GitHub last listed them, and the heads the repo listed then.</summary>
    private sealed record PullCache(DateTime At, IReadOnlyList<PullRequestInfo>? Pulls, IReadOnlyDictionary<int, string> Heads, string? Error);

    private readonly ConcurrentDictionary<string, PullCache> _pulls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Settings key that keeps the commits a parent's previews aren't made for: every branch's commit when branch
    /// previews were turned on, and a preview's commit when it expired or was removed by hand. Not a setting anyone edits.
    /// </summary>
    public static string SkipsKey(int parentId) => $"previews.skip.{parentId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Why GitHub's pull requests of the repo couldn't be read last time, or null.</summary>
    public string? PullsProblem(string owner, string repo) => _pulls.TryGetValue($"{owner}/{repo}", out var cache) ? cache.Error : null;

    // ---- The watcher's pass ----

    /// <summary>
    /// One repo's previews, on a watcher pass: for each parent that has previews on, the planner's changes (new previews,
    /// new commits, removals), then the deploys that are due, within the parent's limit.
    /// </summary>
    /// <param name="watched">The repo's watched apps (Git apps that deploy by themselves and are meant to run).</param>
    /// <param name="poked">GitHub told about the repo: its pull requests are asked for now, and deploys count as GitHub's.</param>
    /// <param name="busy">Apps with a deploy queued or under way.</param>
    public async Task CheckRepoAsync(string owner, string repo, RemoteInfo remote, bool poked, IReadOnlyList<AppDefinition> watched,
        IReadOnlySet<int> busy, CancellationToken ct = default)
    {
        var parents = watched.Where(a => a.ParentAppId is null && a.PreviewMode != PreviewMode.Off).ToList();
        if (parents.Count == 0) return;
        var open = await PullsAsync(owner, repo, remote.PullHeads, poked, ct);
        var trigger = poked ? DeployTrigger.Webhook : DeployTrigger.Poll;
        foreach (var parent in parents)
        {
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    await CheckParentAsync(parent.Id, remote, open, ct);
                    await DeployDueAsync(parent.Id, trigger, busy, ct);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the previews of {App} failed", parent.Name);
            }
        }
    }

    /// <summary>
    /// Previews whose parent is gone or has previews turned off: removed on any pass, whatever their repo, so turning
    /// previews off finishes even when the removal was cut short.
    /// </summary>
    public async Task TidyAsync(CancellationToken ct = default)
    {
        List<(int Id, int? Parent)> orphans;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var previews = await db.Apps.AsNoTracking().Where(a => a.ParentAppId != null).Select(a => new { a.Id, a.ParentAppId }).ToListAsync(ct);
            if (previews.Count == 0) return;
            var on = (await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null && a.PreviewMode != PreviewMode.Off).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            orphans = previews.Where(p => !on.Contains(p.ParentAppId!.Value)).Select(p => (p.Id, p.ParentAppId)).ToList();
        }
        if (orphans.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var (id, _) in orphans) await RemoveCoreAsync(id, "previews were turned off", ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The open pull requests: asked for again when the repo lists a new or moved pull request head, when GitHub told about
    // the repo, or every PullsEvery; null when GitHub hasn't answered yet.
    private async Task<IReadOnlyList<PullRequestInfo>?> PullsAsync(string owner, string repo, IReadOnlyList<RemotePull> heads, bool poked, CancellationToken ct)
    {
        var key = $"{owner}/{repo}";
        var now = Now();
        var current = heads.GroupBy(h => h.Number).ToDictionary(g => g.Key, g => g.Last().Sha);
        _pulls.TryGetValue(key, out var cache);
        var changed = cache is null || cache.Heads.Count != current.Count || current.Any(h => !cache.Heads.TryGetValue(h.Key, out var sha) || sha != h.Value);
        if (cache is not null && !poked && !changed && now - cache.At < PullsEvery) return cache.Pulls;
        try
        {
            var list = await pulls.ListOpenAsync(owner, repo, ct);
            _pulls[key] = new PullCache(now, list, current, null);
            return list;
        }
        catch (GitHubApiException ex)
        {
            if (cache?.Error != ex.Message) logger.LogWarning("Can't list the pull requests of {Repo}: {Error}", key, ex.Message);
            // Asked again in PullsEvery, or sooner when the repo's pull requests change again.
            _pulls[key] = new PullCache(now, cache?.Pulls, current, ex.Message);
            return cache?.Pulls;
        }
    }

    private async Task CheckParentAsync(int parentId, RemoteInfo remote, IReadOnlyList<PullRequestInfo>? open, CancellationToken ct)
    {
        AppDefinition? parent;
        List<PreviewInfo> infos;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            parent = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
                .FirstOrDefaultAsync(a => a.Id == parentId, ct);
            infos = await db.Previews.AsNoTracking().Where(p => p.ParentAppId == parentId).ToListAsync(ct);
        }
        if (parent is null) return;
        var skips = await LoadSkipsAsync(parentId, ct);
        var existing = infos.Select(i => new ExistingPreview(i.AppId, i.Kind, i.Branch, i.PullRequest, i.Title, i.HeadSha, i.FromFork, i.LastActivityAt)).ToList();
        var plan = PreviewPlanner.Plan(new PreviewSettingsFacts(parent.PreviewMode, parent.Branch, parent.PreviewForks, parent.PreviewExpiryDays),
            remote.Branches, open, remote.PullHeads, existing, skips, Now());

        foreach (var removal in plan.Remove)
        {
            var reason = removal.Reason;
            // Closed or merged: GitHub says which, when it can.
            if (reason == PreviewPlanner.ClosedReason && infos.FirstOrDefault(i => i.AppId == removal.AppId)?.PullRequest is int number)
                reason = await pulls.MergedAsync(parent.Owner, parent.Repo, number, ct) switch
                {
                    true => "the pull request was merged",
                    false => "the pull request was closed",
                    null => reason,
                };
            await RemoveCoreAsync(removal.AppId, reason, ct);
        }
        if (plan.Update.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = Now();
            foreach (var u in plan.Update)
            {
                var row = await db.Previews.FirstOrDefaultAsync(p => p.AppId == u.AppId, ct);
                if (row is null) continue;
                row.HeadSha = u.HeadSha;
                row.Title = u.Title;
                if (u.NewCommit) row.LastActivityAt = now;
            }
            await db.SaveChangesAsync(ct);
        }
        foreach (var target in plan.Create)
        {
            try
            {
                await CreateAsync(parent, target, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or DockerApiException or HttpRequestException or TimeoutException)
            {
                logger.LogWarning(ex, "Couldn't make the preview of {App} for {Preview}", parent.Name, PreviewNames.Label(target.Kind, target.Branch, target.PullRequest));
            }
        }
        if (!SameSkips(skips, plan.Skips)) await SaveSkipsAsync(parentId, plan.Skips, ct);
    }

    // The previews whose newest commit hasn't been tried (or whose failed build is due a retry), started oldest first while
    // the parent has room: one that runs already keeps its place, one that doesn't waits for one.
    private async Task DeployDueAsync(int parentId, DeployTrigger trigger, IReadOnlySet<int> busy, CancellationToken ct)
    {
        int limit;
        List<(AppDefinition Row, PreviewInfo Info)> mine;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            limit = await db.Apps.AsNoTracking().Where(a => a.Id == parentId).Select(a => a.PreviewLimit).FirstOrDefaultAsync(ct);
            var infos = await db.Previews.AsNoTracking().Where(p => p.ParentAppId == parentId).ToListAsync(ct);
            var ids = infos.Select(i => i.AppId).ToList();
            var rows = await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
            mine = infos.Join(rows, i => i.AppId, r => r.Id, (i, r) => (r, i)).ToList();
        }
        if (mine.Count == 0) return;
        var runs = mine.Select(m => new PreviewPlanner.Run(m.Row.Id, m.Row.CurrentImage, m.Row.DesiredState, busy.Contains(m.Row.Id), m.Info.CreatedAt)).ToList();
        var wanting = new Dictionary<int, (DeployTrigger Trigger, string Sha)>();
        foreach (var (row, info) in mine)
        {
            if (info.HeadSha is not { } sha || busy.Contains(row.Id) || row.DesiredState != DesiredState.Running) continue;
            if (RepoWatcher.ShouldDeploy(row, sha, deployInProgress: false)) wanting[row.Id] = (trigger, sha);
            else if (!string.Equals(sha, row.CurrentCommit, StringComparison.OrdinalIgnoreCase)
                && RepoWatcher.ShouldRetry(row, sha, await RepoWatcher.LastAttemptAsync(dbFactory, row.Id, sha, ct), BuildStamp.Current, DateTime.UtcNow))
                wanting[row.Id] = (DeployTrigger.Retry, sha);
        }
        foreach (var id in PreviewPlanner.Admit(runs, wanting.Keys, limit))
        {
            var (why, sha) = wanting[id];
            logger.LogInformation("Deploying preview {Preview} at {Sha}", mine.First(m => m.Row.Id == id).Row.Name, sha[..Math.Min(7, sha.Length)]);
            await actions.DeployAsync(id, why, ct, expectedSha: sha);
        }
    }

    // ---- Making, removing, promoting ----

    /// <summary>
    /// A new preview of <paramref name="parent"/>: its row (settings copied, a host port of its own for its web port, its
    /// own domain under the parent's main one when domains are on) and what it previews. Its first deploy is the watcher's.
    /// </summary>
    internal async Task<int> CreateAsync(AppDefinition parent, PreviewTarget target, CancellationToken ct)
    {
        var label = PreviewNames.Label(target.Kind, target.Branch, target.PullRequest);
        // Its named volumes are named after its slug (gb-<slug>-<name>): a slug that gives one of them the name of another
        // app's volume, or of one Docker has already, would have the preview use, and its removal delete, someone else's data.
        var volumes = PreviewCopy.Volumes(parent.Volumes).Select(v => v.Source).ToList();
        var takenVolumes = volumes.Count == 0 ? null : await VolumesTakenAsync(ct);
        string? slug = null;
        // The usual name first; when it's taken (another app, a container, a route, a volume), another hash of what it previews.
        for (var salt = 0; salt < 5 && slug is null; salt++)
        {
            var candidate = PreviewNames.Slug(parent.Slug, target.Kind, target.Branch, target.PullRequest, salt);
            if (takenVolumes is not null && volumes.Any(v => takenVolumes.Contains(VolumeNames.Resolve(candidate, v)))) continue;
            if (await apps.ChooseSlugAsync(candidate, ct) is { Taken: null } chosen && chosen.Slug == candidate) slug = candidate;
        }
        if (slug is null) throw new InvalidOperationException($"No free name was found for the preview of {parent.Name} for {label}.");

        var now = Now();
        var info = new PreviewInfo
        {
            ParentAppId = parent.Id, Kind = target.Kind, Branch = target.Branch, PullRequest = target.PullRequest, Title = target.Title, Ref = target.Ref,
            HeadSha = target.HeadSha, FromFork = target.FromFork, LastActivityAt = now, CreatedAt = now,
        };
        var row = new AppDefinition
        {
            Name = PreviewNames.Name(parent.Name, target.Kind, target.Branch, target.PullRequest),
            Slug = slug,
            ParentAppId = parent.Id,
            DesiredState = DesiredState.Running,
            DetectedKind = parent.DetectedKind,
            DetectedSummary = parent.DetectedSummary,
            DetectedPort = parent.DetectedPort,
        };
        PreviewCopy.Sync(row, parent, info);
        if (PortRules.WebOf(row.Ports) is { } web) web.HostPort = await HostPortAsync(web.ContainerPort, ct);
        var hostname = await domains.EnabledAsync(ct) && await domains.MainHostnameAsync(parent.Id, ct) is { } main
            ? PreviewNames.Hostname(main, parent.Slug, target.Kind, target.Branch, target.PullRequest)
            : null;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (hostname is not null && PortRules.WebOf(row.Ports) is not null)
            {
                if (await db.Domains.AsNoTracking().AnyAsync(d => d.Hostname == hostname, ct))
                    logger.LogWarning("{Hostname} belongs to another app, so the preview of {App} for {Preview} has no domain", hostname, parent.Name, label);
                else
                {
                    var domain = new CustomDomain { Hostname = hostname, Protected = parent.ProtectPreviews };
                    // Still on its way out from an earlier preview: its old route is this one's to replace.
                    if (await db.DomainRemovals.FindAsync([hostname], ct) is { } removal)
                    {
                        domain.Service = removal.Service;
                        db.DomainRemovals.Remove(removal);
                    }
                    // Once GitHub's webhook host, it's the preview's now (see DomainService.ForgetEarlierHostsAsync).
                    await DomainService.ForgetEarlierHostsAsync(db, [hostname], ct);
                    row.Domains.Add(domain);
                }
            }
            db.Apps.Add(row);
            await db.SaveChangesAsync(ct);
            info.AppId = row.Id;
            db.Previews.Add(info);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        hostPorts?.ForgetSaved();
        await activity.AddAsync(parent.ContainerName, parent.Id, ActivityKind.Event,
            $"Preview {label} added{(target.Title is { Length: > 0 } title ? $": {title}" : "")}.", ActivityTone.Neutral, ct);
        return row.Id;
    }

    // Every volume name an app's row gives (gb-<slug>-<name>), and every volume Docker has. Never guessed: without
    // Docker's list, the preview waits for the next look. Later changes are checked where they're made (VolumeOwners).
    private async Task<HashSet<string>> VolumesTakenAsync(CancellationToken ct)
    {
        var taken = new HashSet<string>(await docker.VolumeNamesAsync(ct)
            ?? throw new InvalidOperationException("Docker didn't say which volumes it has, so the preview's volumes can't be checked yet."), StringComparer.Ordinal);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        taken.UnionWith((await VolumeOwners.LoadAsync(db, ct)).Names);
        return taken;
    }

    // The host port for a preview's web port: the map's suggestion (its container port when free, else the next free one).
    private async Task<int> HostPortAsync(int containerPort, CancellationToken ct)
    {
        if (hostPorts is not null) return await hostPorts.SuggestAsync(containerPort, "tcp", null, [], ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var taken = (await db.Ports.AsNoTracking().Where(p => p.HostPort != null).Select(p => p.HostPort!.Value).ToListAsync(ct)).ToHashSet();
        var port = containerPort;
        while (taken.Contains(port) || HostPortMap.Reserved.Contains(port)) port++;
        return port;
    }

    /// <summary>
    /// Removes a preview by hand: its container, images, own volumes, domain (its route and DNS record go through the usual
    /// removal) and rows. It isn't made again for the same commit; a new commit on its branch or pull request brings it back.
    /// </summary>
    public async Task RemoveAsync(int previewId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            PreviewInfo? info;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                info = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == previewId, ct);
            if (info is null) throw new InvalidOperationException("That preview doesn't exist any more.");
            if (info.HeadSha is { } sha)
            {
                var skips = new Dictionary<string, string>(await LoadSkipsAsync(info.ParentAppId, ct), StringComparer.Ordinal)
                {
                    [PreviewPlanner.SkipKeyOf(info.Kind, info.Branch, info.PullRequest)] = sha,
                };
                await SaveSkipsAsync(info.ParentAppId, skips, ct);
            }
            await RemoveCoreAsync(previewId, null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Removes the preview and notes it on its parent's Activity ("Preview PR #12 removed: the pull request was closed or merged").
    private async Task RemoveCoreAsync(int previewId, string? reason, CancellationToken ct)
    {
        PreviewInfo? info;
        AppDefinition? parent;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            info = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == previewId, ct);
            var parentId = info?.ParentAppId ?? await db.Apps.AsNoTracking().Where(a => a.Id == previewId).Select(a => a.ParentAppId).FirstOrDefaultAsync(ct);
            parent = parentId is int id ? await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) : null;
        }
        var report = await actions.DeleteAsync(previewId, removeImages: true, removeVolumes: true);
        foreach (var problem in report.Notes.Where(n => n.Problem)) logger.LogWarning("Removing the domain of preview {Preview}: {Problem}", previewId, problem.Text);
        if (parent is not null && info is not null)
        {
            var label = PreviewNames.Label(info.Kind, info.Branch, info.PullRequest);
            await activity.AddAsync(parent.ContainerName, parent.Id, ActivityKind.Removed, $"Preview {label} removed{(reason is null ? "" : ": " + reason)}.",
                ActivityTone.Neutral, ct);
        }
    }

    /// <summary>
    /// Production builds the preview's commit with its own (production's) variables, and stays on it: the watcher doesn't
    /// deploy the production branch's commit of this moment, only a newer one, and a manual Deploy builds the branch
    /// again. Returns the production deploy.
    /// </summary>
    /// <exception cref="InvalidOperationException">The preview hasn't run yet, or the production branch can't be read.</exception>
    public async Task<int> PromoteAsync(int previewId, CancellationToken ct = default)
    {
        PreviewInfo info;
        AppDefinition preview, parent;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            info = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == previewId, ct) ?? throw new InvalidOperationException("That preview doesn't exist any more.");
            preview = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == previewId, ct);
            parent = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == info.ParentAppId, ct)
                ?? throw new InvalidOperationException("The app this previews doesn't exist any more.");
        }
        if (preview.CurrentCommit is not { } commit) throw new InvalidOperationException("The preview hasn't been deployed yet, so there's nothing to promote.");
        var head = await (ProductionHead ?? BranchHeadAsync)(parent, ct);
        if (head is null)
            throw new InvalidOperationException($"Gitberth can't see the newest commit on {parent.Branch} right now, so production can't be kept on the preview's. Try again in a moment.");
        // The branch's commit production stays off is saved with the promote's deploy, never without it.
        var deployment = await actions.DeployAsync(parent.Id, DeployTrigger.Promote, ct, expectedSha: commit, gitRef: info.Ref, commit: commit, promotedOver: head);
        await activity.AddAsync(parent.ContainerName, parent.Id, ActivityKind.Event,
            $"Preview {PreviewNames.Label(info.Kind, info.Branch, info.PullRequest)} promoted: production builds {commit[..Math.Min(7, commit.Length)]} and stays on it until the next commit on {parent.Branch}.",
            ActivityTone.Neutral, ct);
        return deployment;
    }

    /// <summary>Stands in for reading the production branch's newest commit from GitHub, in tests.</summary>
    internal Func<AppDefinition, CancellationToken, Task<string?>>? ProductionHead { get; set; }

    // The production branch's newest commit: GitHub's now, else what the watcher saw last.
    private async Task<string?> BranchHeadAsync(AppDefinition parent, CancellationToken ct)
    {
        try
        {
            return (await git.LsRemoteAsync(new GitHubRepoRef(parent.Owner, parent.Repo), ct)).Branches.FirstOrDefault(b => b.Name == parent.Branch)?.Sha;
        }
        catch (GitException)
        {
            return watch.Get(parent.Id)?.RemoteSha;
        }
    }

    // ---- Settings ----

    /// <summary>
    /// Saves a Git app's preview settings. Turning previews off removes its previews (the page asks first), and turning
    /// forks off removes the previews of pull requests from forks; turning the protection of their domains on or off
    /// follows on every preview's domain, and what the tunnel didn't take yet is said (<see cref="PreviewSave.Warning"/>):
    /// above all a domain that may still be open while protection is on.
    /// </summary>
    public async Task<PreviewSave> SaveSettingsAsync(int parentId, PreviewSettings wanted, CancellationToken ct = default)
    {
        if (wanted.Problem is { } problem) return new(problem);
        string? warning = null;
        await _gate.WaitAsync(ct);
        try
        {
            PreviewSettings before;
            List<int> previewIds, forkIds;
            List<string> hostnames = [];
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == parentId, ct);
                if (app is null) return new("That app doesn't exist any more.");
                if (!app.Deployed || app.IsPreview) return new($"{app.Name} isn't deployed from a repo, so it has no previews.");
                before = PreviewSettings.Of(app);
                (app.PreviewMode, app.PreviewLimit, app.PreviewExpiryDays, app.PreviewForks, app.ProtectPreviews) = (wanted.Mode, wanted.Limit, wanted.ExpiryDays, wanted.Forks, wanted.Protect);
                app.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                previewIds = await db.Apps.AsNoTracking().Where(a => a.ParentAppId == parentId).Select(a => a.Id).ToListAsync(ct);
                forkIds = await db.Previews.AsNoTracking().Where(p => p.ParentAppId == parentId && p.FromFork).Select(p => p.AppId).ToListAsync(ct);
                if (wanted.Protect != before.Protect && previewIds.Count > 0)
                {
                    await db.Domains.Where(d => previewIds.Contains(d.AppId)).ExecuteUpdateAsync(s => s.SetProperty(d => d.Protected, wanted.Protect), ct);
                    // The domains of the previews that stay: a fork's that goes below is no one's to warn about.
                    var staying = wanted.Forks ? previewIds : previewIds.Except(forkIds).ToList();
                    hostnames = await db.Domains.AsNoTracking().Where(d => staying.Contains(d.AppId)).Select(d => d.Hostname).ToListAsync(ct);
                }
            }
            if (wanted.Mode == PreviewMode.Off && before.Mode != PreviewMode.Off)
            {
                foreach (var id in previewIds) await RemoveCoreAsync(id, "previews were turned off", ct);
                await SaveSkipsAsync(parentId, new Dictionary<string, string>(), ct);
            }
            else
            {
                // Forks turned off: their previews go now, so none of their code is built or run again.
                if (!wanted.Forks)
                    foreach (var id in forkIds) await RemoveCoreAsync(id, PreviewPlanner.ForksOffReason, ct);
                if (wanted.Protect != before.Protect && hostnames.Count > 0)
                {
                    // The tunnel and Cloudflare Access follow the domains' new protection; what didn't is said, not dropped.
                    try
                    {
                        if (await domains.EnabledAsync(ct)) warning = DomainsWarning(await domains.SyncAsync(ct), hostnames, wanted.Protect);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "The previews' domains couldn't be synced after their protection changed");
                        warning = $"Gitberth couldn't update the previews' domains in Cloudflare ({ex.Message}).";
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        watch.CheckNow();
        return new(null, warning);
    }

    // What the sync left undone for the previews' domains: one that may still be open while they're to be protected, or
    // still behind Access while they're not, else its first problem.
    private static string? DomainsWarning(SyncReport report, List<string> hostnames, bool protect) =>
        (protect ? DomainService.NotProtectedYet(report, hostnames) : DomainService.NotOpenYet(report, hostnames))
        ?? report.For(hostnames).Notes.FirstOrDefault(n => n.Problem)?.Text;

    // ---- What the pages show ----

    /// <summary>The parent's previews, newest activity first, with their state, addresses and when they expire.</summary>
    public async Task<IReadOnlyList<PreviewRow>> ListAsync(int parentId, string lanHost, CancellationToken ct = default)
    {
        AppDefinition? parent;
        List<(AppDefinition Row, PreviewInfo Info)> mine;
        Dictionary<int, Deployment> latest;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            parent = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == parentId, ct);
            if (parent is null) return [];
            var infos = await db.Previews.AsNoTracking().Where(p => p.ParentAppId == parentId).ToListAsync(ct);
            var ids = infos.Select(i => i.AppId).ToList();
            var rows = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
            mine = infos.Join(rows, i => i.AppId, r => r.Id, (i, r) => (r, i)).ToList();
            // A skipped commit left the preview as it was: its state, commit and log are its newest real deploy's.
            var newest = await db.Deployments.AsNoTracking().Where(d => ids.Contains(d.AppId) && d.Status != DeployStatus.Skipped)
                .GroupBy(d => d.AppId).Select(g => g.Max(d => d.Id)).ToListAsync(ct);
            latest = (await db.Deployments.AsNoTracking().Where(d => newest.Contains(d.Id)).ToListAsync(ct)).ToDictionary(d => d.AppId);
        }
        if (mine.Count == 0) return [];
        var (containers, _) = await docker.ListContainersAsync(managedOnly: true, ct);
        var running = containers.Where(c => c.State == "running").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var runs = mine.Select(m => new PreviewPlanner.Run(m.Row.Id, m.Row.CurrentImage, m.Row.DesiredState, latest.GetValueOrDefault(m.Row.Id)?.IsActive == true, m.Info.CreatedAt)).ToList();
        var occupied = runs.Count(PreviewPlanner.Occupies);
        return mine.Select(m =>
            {
                var (row, info) = m;
                var deploy = latest.GetValueOrDefault(row.Id);
                var isRunning = running.Contains(row.ContainerName);
                var (state, text) = PreviewStates.Of(new PreviewFacts(deploy, isRunning, row.DesiredState, row.CurrentImage, occupied, parent.PreviewLimit));
                var web = PortRules.WebOf(row.Ports);
                var domain = DomainService.MainOf(row.Domains);
                var commitMessage = deploy?.CommitSha == row.CurrentCommit ? deploy?.CommitMessage : null;
                return new PreviewRow(row.Id, row.ContainerName, row.Name, info.Kind, info.Branch, info.PullRequest, info.Title, UrlOf(parent, info),
                    state, text, row.CurrentCommit is { Length: >= 7 } sha ? sha[..7] : info.HeadSha is { Length: >= 7 } head ? head[..7] : null, commitMessage,
                    domain, web?.HostPort is int port ? $"http://{Host(lanHost)}:{port.ToString(CultureInfo.InvariantCulture)}" : null,
                    info.LastActivityAt, info.LastActivityAt.AddDays(parent.PreviewExpiryDays), deploy?.Id, row.CurrentCommit is not null, info.FromFork, isRunning);
            })
            .OrderByDescending(r => r.LastActivityAt).ToList();
    }

    // An IPv6 host goes in brackets in a URL.
    private static string Host(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    /// <summary>The pull request on GitHub, or the branch's code.</summary>
    public static string UrlOf(AppDefinition parent, PreviewInfo info) =>
        info.Kind == PreviewKind.PullRequest
            ? $"{parent.RepoWebUrl}/pull/{info.PullRequest?.ToString(CultureInfo.InvariantCulture)}"
            : $"{parent.RepoWebUrl}/tree/{Uri.EscapeDataString(info.Branch).Replace("%2F", "/", StringComparison.Ordinal)}";

    /// <summary>How many previews the app has.</summary>
    public async Task<int> CountAsync(int parentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().CountAsync(a => a.ParentAppId == parentId, ct);
    }

    /// <summary>
    /// The variables that would give the app's previews production's database (<see cref="PreviewDatabases"/>): its Previews
    /// section says so before a preview is refused for it. Its GitHub variables as last read when GitHub can't be asked.
    /// </summary>
    public async Task<IReadOnlyList<PreviewDatabases.Shared>> SharedDatabasesAsync(int appId, CancellationToken ct = default)
    {
        if (protector is null) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().Include(a => a.EnvVars).FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null || !app.Deployed || app.IsPreview) return [];
        IEnumerable<(string, string)> github = [];
        if (repoVariables is not null)
        {
            try
            {
                github = (await repoVariables.ForDeployAsync(app.Owner, app.Repo, ct)).Variables.Select(v => (v.Name, v.Value));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Couldn't read {Repo}'s GitHub variables for its Previews section", $"{app.Owner}/{app.Repo}");
            }
        }
        return PreviewDatabases.Find(app.EnvVars.Select(v => (v.Key, protector.TryUnprotect(v.ProtectedValue), v.Scope)), github);
    }

    /// <summary>What a preview previews and of which app, for its own page's banner; null when the app isn't a preview.</summary>
    public async Task<PreviewOf?> OfAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var info = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == appId, ct);
        if (info is null) return null;
        var parent = await db.Apps.AsNoTracking().Include(a => a.Volumes).FirstOrDefaultAsync(a => a.Id == info.ParentAppId, ct);
        if (parent is null) return null;
        return new PreviewOf(info, parent.Id, parent.Name, parent.ContainerName, PreviewCopy.LeftOut(parent.Volumes).Select(v => v.Source).ToList(), UrlOf(parent, info));
    }

    // ---- What's remembered ----

    private async Task<IReadOnlyDictionary<string, string>> LoadSkipsAsync(int parentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = SkipsKey(parentId);
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private async Task SaveSkipsAsync(int parentId, IReadOnlyDictionary<string, string> skips, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = SkipsKey(parentId);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (skips.Count == 0)
        {
            if (row is not null) db.Settings.Remove(row);
        }
        else
        {
            var json = JsonSerializer.Serialize(new SortedDictionary<string, string>(skips.ToDictionary(), StringComparer.Ordinal));
            if (row is null) db.Settings.Add(new Setting { Key = key, Value = json });
            else row.Value = json;
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool SameSkips(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
