using System.Collections.Concurrent;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Git;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

public sealed record WatchInfo(DateTime CheckedAt, string? RemoteSha, string? Error);

/// <summary>What the watcher last saw for each app, shared with the UI, plus a way to ask for an immediate check.</summary>
public sealed class WatchState
{
    private readonly ConcurrentDictionary<int, WatchInfo> _apps = new();
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _defaultBranches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The repo's default branch as Gitberth last saw it, or null when it hasn't looked yet.</summary>
    public string? DefaultBranch(string owner, string repo) => _defaultBranches.TryGetValue($"{owner}/{repo}", out var branch) ? branch : null;

    internal void RecordDefaultBranch(string owner, string repo, string? branch)
    {
        if (branch is not null) _defaultBranches[$"{owner}/{repo}"] = branch;
    }
    private readonly SemaphoreSlim _wake = new(0, 1);

    public int IntervalSeconds { get; internal set; } = RepoWatcher.DefaultIntervalSeconds;

    public WatchInfo? Get(int appId) => _apps.TryGetValue(appId, out var info) ? info : null;

    /// <summary>Wake the watcher now and forget any error back-off.</summary>
    public void CheckNow()
    {
        _retryAfter.Clear();
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    // Repos GitHub told about through the webhook since the watcher's last pass, by owner/repo.
    private readonly ConcurrentDictionary<string, byte> _webhookRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// GitHub said something changed in a repo (a push, a pull request, a branch deleted): the watcher looks now, and the
    /// deploys that pass starts for that repo are counted as started by GitHub (<see cref="DeployTrigger.Webhook"/>).
    /// </summary>
    public void CheckNowFromWebhook(string owner, string repo)
    {
        _webhookRepos[$"{owner}/{repo}"] = 0;
        CheckNow();
    }

    /// <summary>Whether GitHub told about this repo since the last time it was asked; asking clears it.</summary>
    public bool TakeWebhookPoke(string owner, string repo) => _webhookRepos.TryRemove($"{owner}/{repo}", out _);

    internal void Record(int appId, string? sha, string? error) => _apps[appId] = new WatchInfo(DateTime.UtcNow, sha, error);

    internal bool InBackoff(string repo) => _retryAfter.TryGetValue(repo, out var until) && DateTime.UtcNow < until;

    internal void Failed(string repo) => _retryAfter[repo] = DateTime.UtcNow.AddMinutes(5);

    internal void Succeeded(string repo) => _retryAfter.TryRemove(repo, out _);

    internal Task WaitAsync(TimeSpan delay, CancellationToken ct) => _wake.WaitAsync(delay, ct);
}

/// <summary>The latest deployment of an app, and how often its commit has failed on this Gitberth build.</summary>
public sealed record LastAttempt(string? CommitSha, DeployStatus Status, DeployTrigger Trigger, string? GitberthBuild, DateTime? FinishedAt, int FailuresOnThisBuild);

/// <summary>
/// Polls each watched repo with one `git ls-remote` and deploys apps whose branch moved.
/// A commit that was already tried isn't deployed again as if it were new, so a broken commit doesn't loop and
/// a manual rollback isn't undone. A failed build is retried a bounded number of times; see <see cref="ShouldRetry"/>.
/// </summary>
/// <param name="previews">Makes, removes and deploys the watched apps' previews; without it (in tests) there are none.</param>
public sealed class RepoWatcher(
    IDbContextFactory<GitberthDb> dbFactory,
    GitClient git,
    AppActions actions,
    DeployPipeline pipeline,
    SettingsService settings,
    WatchState state,
    ILogger<RepoWatcher> logger,
    Previews.PreviewService? previews = null) : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;

    /// <summary>
    /// The apps whose repos are watched: Git apps (no other has a repo) set to deploy by themselves and meant to run. A
    /// preview is never one: its deploys are its parent's previews' (<see cref="Previews.PreviewService"/>).
    /// </summary>
    internal static IQueryable<AppDefinition> Watched(IQueryable<AppDefinition> apps) =>
        apps.Where(a => a.Source == AppSource.Git && a.AutoDeploy && a.DesiredState == DesiredState.Running && a.ParentAppId == null);

    /// <summary>
    /// Whether production stays on a promoted preview's commit: the branch's commit is still the one it was promoted over.
    /// Any other commit (a new one on the branch) is deployed as usual, and production follows its branch again. So does
    /// the same commit once the promote is known not to hold (<see cref="ForgetUnheldPromotionAsync"/>).
    /// </summary>
    public static bool StaysPromoted(AppDefinition app, string remoteSha) =>
        app.PromotedOverCommit is { } promoted && string.Equals(promoted, remoteSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the app's PromotedOverCommit unless its newest Promote deploy is queued, under way or worked: a promote that
    /// never ran (Gitberth restarted, a newer deploy took its place, the app was stopped) or failed doesn't keep production
    /// off its branch. One statement, so a promote saved meanwhile (its deploy is queued with it) stays. Returns whether it
    /// cleared it.
    /// </summary>
    internal static async Task<bool> ForgetUnheldPromotionAsync(GitberthDb db, int appId, CancellationToken ct = default) =>
        await db.Apps.Where(a => a.Id == appId && a.PromotedOverCommit != null
                // No promote that's queued, under way or worked and has no newer promote after it.
                && !db.Deployments.Any(d => d.AppId == a.Id && d.Trigger == DeployTrigger.Promote
                    && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building
                        || d.Status == DeployStatus.Starting || d.Status == DeployStatus.Succeeded)
                    && !db.Deployments.Any(e => e.AppId == a.Id && e.Trigger == DeployTrigger.Promote && e.Id > d.Id)))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PromotedOverCommit, (string?)null), ct) > 0;

    /// <summary>Deploy when the branch tip is neither what's running nor the commit that was last tried.</summary>
    public static bool ShouldDeploy(AppDefinition app, string remoteSha, bool deployInProgress) =>
        !deployInProgress
        && !string.Equals(remoteSha, app.CurrentCommit, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(remoteSha, app.LastAttemptedCommit, StringComparison.OrdinalIgnoreCase);

    /// <summary>How long after a failed build the same commit is tried once more, in case it was a network or registry hiccup.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether to try the branch tip again after its build failed: once, <see cref="RetryDelay"/> after a failure that
    /// wasn't itself a retry, and once after each Gitberth update, which may have changed how the repo is built. Only
    /// failed builds are retried: a deploy that started and was rolled back would take the running app down again, and
    /// a manual rollback is never undone.
    /// </summary>
    public static bool ShouldRetry(AppDefinition app, string remoteSha, LastAttempt? last, string build, DateTime now) =>
        last is { Status: DeployStatus.Failed } && last.Trigger != DeployTrigger.Rollback
        && string.Equals(last.CommitSha, remoteSha, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(app.CurrentCommit, remoteSha, StringComparison.OrdinalIgnoreCase)
        && (last.GitberthBuild != build
            || (last.Trigger != DeployTrigger.Retry && last.FailuresOnThisBuild < 2 && last.FinishedAt is { } finished && now - finished >= RetryDelay));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var interval = await settings.GetIntAsync(SettingKeys.WatchIntervalSeconds, DefaultIntervalSeconds, stoppingToken);
                    state.IntervalSeconds = Math.Clamp(interval, 10, 3600);
                    await CheckAsync(stoppingToken);
                    // Previews whose parent is gone or has previews turned off go.
                    if (previews is not null) await previews.TidyAsync(stoppingToken);
                    // A temporary container a deploy couldn't move visitors off goes as soon as the tunnel can be changed.
                    await pipeline.TidyBridgesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Checking repositories for changes failed");
                }
                await state.WaitAsync(TimeSpan.FromSeconds(state.IntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        List<AppDefinition> apps;
        HashSet<int> busy;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            apps = await Watched(db.Apps.AsNoTracking()).ToListAsync(ct);
            busy = (await db.Deployments
                .Where(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting)
                .Select(d => d.AppId)
                .ToListAsync(ct)).ToHashSet();
        }

        foreach (var repo in apps.GroupBy(a => $"{a.Owner}/{a.Repo}", StringComparer.OrdinalIgnoreCase))
        {
            var first = repo.First();
            // GitHub told about this repo: it's looked at now, even while in back-off, and what this pass starts is GitHub's.
            var poked = state.TakeWebhookPoke(first.Owner, first.Repo);
            if (state.InBackoff(repo.Key) && !poked) continue;
            var trigger = poked ? DeployTrigger.Webhook : DeployTrigger.Poll;
            RemoteInfo remote;
            try
            {
                remote = await git.LsRemoteAsync(new GitHubRepoRef(first.Owner, first.Repo), ct);
                state.Succeeded(repo.Key);
                state.RecordDefaultBranch(first.Owner, first.Repo, remote.DefaultBranch);
            }
            catch (GitException ex)
            {
                state.Failed(repo.Key);
                foreach (var app in repo) state.Record(app.Id, null, ex.Message);
                logger.LogWarning("Can't check {Repo} for changes: {Error} Retrying in 5 minutes.", repo.Key, ex.Message);
                continue;
            }

            foreach (var app in repo)
            {
                var sha = remote.Branches.FirstOrDefault(b => b.Name == app.Branch)?.Sha;
                state.Record(app.Id, sha, sha is null ? $"Branch '{app.Branch}' doesn't exist anymore." : null);
                if (sha is null || busy.Contains(app.Id)) continue;
                switch (await NextDeployAsync(dbFactory, app, sha, trigger, logger, ct))
                {
                    case DeployTrigger.Retry:
                        logger.LogInformation("Retrying {App} at {Sha}, whose last build failed", app.Name, sha[..7]);
                        await actions.DeployAsync(app.Id, DeployTrigger.Retry, ct, expectedSha: sha);
                        break;
                    case { } why:
                        logger.LogInformation("New commit {Sha} on {Repo} ({Branch}); deploying {App}", sha[..7], repo.Key, app.Branch, app.Name);
                        await actions.DeployAsync(app.Id, why, ct, expectedSha: sha);
                        break;
                }
            }

            // The repo's branches and pull requests, for the previews of its apps that have them on.
            if (previews is not null) await previews.CheckRepoAsync(first.Owner, first.Repo, remote, poked, repo.ToList(), busy, ct);
        }
    }

    /// <summary>
    /// What the watcher starts for an app whose branch is at <paramref name="sha"/>: a deploy of a new commit
    /// (<paramref name="trigger"/>), a retry of one whose build failed (<see cref="DeployTrigger.Retry"/>), or nothing
    /// (null). Promoted, production stays on the preview's commit while its branch is still at the commit it was promoted
    /// over and that promote is queued, under way or worked. Otherwise it follows its branch again (a new commit there,
    /// or a promote that never ran or failed), and the branch's commit is deployed like any other.
    /// </summary>
    internal static async Task<DeployTrigger?> NextDeployAsync(IDbContextFactory<GitberthDb> dbFactory, AppDefinition app, string sha, DeployTrigger trigger,
        ILogger logger, CancellationToken ct)
    {
        if (app.PromotedOverCommit is { } promoted)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // The branch moved on: production follows it again, unless a newer promote was saved meanwhile.
            if (!StaysPromoted(app, sha))
                await db.Apps.Where(a => a.Id == app.Id && a.PromotedOverCommit == promoted).ExecuteUpdateAsync(s => s.SetProperty(a => a.PromotedOverCommit, (string?)null), ct);
            else if (await ForgetUnheldPromotionAsync(db, app.Id, ct))
                logger.LogInformation("The promote of {App} never ran or failed, so it follows {Branch} again", app.Name, app.Branch);
            else
                return null;
        }
        if (ShouldDeploy(app, sha, deployInProgress: false)) return trigger;
        // Tried already: again only when its build failed and is due another try.
        return !string.Equals(sha, app.CurrentCommit, StringComparison.OrdinalIgnoreCase)
            && ShouldRetry(app, sha, await LastAttemptAsync(dbFactory, app.Id, sha, ct), BuildStamp.Current, DateTime.UtcNow)
            ? DeployTrigger.Retry
            : null;
    }

    /// <summary>
    /// The latest build of this commit of the app, for <see cref="ShouldRetry"/> (previews' too). Settings changes in between
    /// (which recreate the running container) don't hide it, but a manual rollback afterwards means it's left alone.
    /// </summary>
    internal static async Task<LastAttempt?> LastAttemptAsync(IDbContextFactory<GitberthDb> dbFactory, int appId, string sha, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var last = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == appId && d.CommitSha == sha && d.Trigger != DeployTrigger.Rollback)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (last is null) return null;
        if (await db.Deployments.AnyAsync(d => d.AppId == appId && d.Id > last.Id && d.Trigger == DeployTrigger.Rollback, ct)) return null;
        var failures = await db.Deployments.CountAsync(
            d => d.AppId == appId && d.CommitSha == sha && d.GitberthBuild == BuildStamp.Current && d.Status == DeployStatus.Failed, ct);
        return new LastAttempt(last.CommitSha, last.Status, last.Trigger, last.GitberthBuild, last.FinishedAt, failures);
    }
}
