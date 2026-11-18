using System.Collections.Concurrent;
using System.Globalization;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>A preview's comment on its pull request, as it says it.</summary>
/// <param name="State">Building, Ready, Failed or Cancelled.</param>
/// <param name="Address">Where the preview answers: its domain, else its LAN address; null when it has neither.</param>
/// <param name="LogUrl">The deploy's log on Gitberth's own pages; null when Gitberth's address isn't known.</param>
public sealed record PreviewComment(string ParentName, string State, string? Address, string? Commit, string? LogUrl, DateTime At);

/// <summary>What GitHub is told about a deploy: pure, so each rule is tested on its own.</summary>
public static class GitHubReports
{
    /// <summary>The first line of every comment Gitberth keeps on a pull request, so people (and Gitberth) can tell it apart.</summary>
    public const string CommentMarker = "<!-- gitberth:preview -->";

    /// <summary>GitHub's limit on a status's description.</summary>
    public const int DescriptionLimit = 140;

    /// <summary>
    /// The status's context: one per app, since one repo can hold several apps (and a preview reports apart from its
    /// production app on the same commit after a promote).
    /// </summary>
    public static string Context(string appName, bool preview) => preview ? $"Gitberth preview: {appName}" : $"Gitberth: {appName}";

    /// <summary>
    /// The commit status for a deploy that started or ended: pending while it builds; success with the app's address once
    /// it runs; failure when its build or checks failed; error when it was cancelled or Gitberth restarted during it (not
    /// the commit's fault). A commit that was skipped (its message asked, or nothing the app builds from changed) gets
    /// success too, so branch protection that requires the status still passes. The text never repeats the deploy's
    /// error, which a public repo would show to everyone: the link goes to the log instead.
    /// </summary>
    /// <param name="skip">Why a skipped deploy was skipped.</param>
    public static CommitStatus Status(DeployStatus status, string? error, string context, string? address, string? logUrl, bool preview, SkipKind? skip = null)
    {
        var what = preview ? "Preview" : "Deploy";
        var (state, text, link) = status switch
        {
            DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting => ("pending", $"Building and deploying on Gitberth", logUrl),
            DeployStatus.Succeeded => ("success", address is null ? $"{what} is live" : $"{what} is live at {Bare(address)}", address ?? logUrl),
            DeployStatus.Skipped => ("success", skip == SkipKind.Asked ? "Skipped: the commit asked not to be deployed" : "Skipped: nothing the app builds from changed", logUrl),
            DeployStatus.RolledBack => ("failure", "The new version didn't pass its checks; the previous one still runs", logUrl),
            DeployStatus.Cancelled => ("error", $"{what} cancelled", logUrl),
            _ when error?.StartsWith("Gitberth restarted", StringComparison.Ordinal) == true => ("error", "Gitberth restarted during the deploy; it's tried again", logUrl),
            _ => ("failure", $"{what} failed: see the deploy log on Gitberth", logUrl),
        };
        return new CommitStatus(state, Limit(text, DescriptionLimit), context, link);
    }

    /// <summary>A preview's state in its comment.</summary>
    public static string CommentState(DeployStatus status) => status switch
    {
        DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting => "Building",
        DeployStatus.Succeeded => "Ready",
        DeployStatus.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>The comment on a pull request: the preview's state, address, commit and time, and a link to its deploy log.</summary>
    public static string CommentBody(PreviewComment c)
    {
        var lines = new List<string>
        {
            CommentMarker,
            $"**Gitberth preview of {Escape(c.ParentName)}: {c.State}**",
            "",
            "| | |",
            "| :-- | :-- |",
            $"| Preview | {(c.Address is null ? "No address yet" : $"[{Escape(Bare(c.Address))}]({c.Address})")} |",
            $"| Commit | {(c.Commit is { Length: > 0 } sha ? $"`{sha[..Math.Min(7, sha.Length)]}`" : "Not known yet")} |",
            $"| Updated | {c.At.ToUniversalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture)} UTC |",
        };
        if (c.State == "Failed") lines.AddRange(["", "The deploy failed. The previous version of the preview, if there was one, still runs."]);
        if (c.LogUrl is not null) lines.AddRange(["", $"[Deploy log]({c.LogUrl}) (on your network)"]);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Where the app answers, for the success link and the comment: its main domain, else the LAN address of its web port
    /// when the LAN reaches it. Null when it has neither.
    /// </summary>
    public static string? Address(AppDefinition app, string? lanHost)
    {
        if (DomainService.MainOf(app.Domains) is { } main) return $"https://{main}/";
        if (PortRules.WebOf(app.Ports) is not { HostPort: int port } web || string.IsNullOrWhiteSpace(lanHost)) return null;
        return web.Access switch
        {
            PortAccess.Lan => $"http://{HostForUrl(lanHost)}:{port}/",
            PortAccess.Custom when !string.IsNullOrWhiteSpace(web.HostIp) => $"http://{HostForUrl(web.HostIp)}:{port}/",
            _ => null,
        };
    }

    /// <summary>The deploy's log on Gitberth's pages, from the address they were last opened at; null when that isn't known.</summary>
    public static string? LogUrl(string? uiAddress, int appId, int deploymentId) =>
        Uri.TryCreate(uiAddress, UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? new Uri(root, $"apps/{appId}/deployments/{deploymentId}").ToString()
            : null;

    private static string HostForUrl(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private static string Bare(string address) => address.Replace("https://", "", StringComparison.Ordinal).Replace("http://", "", StringComparison.Ordinal).TrimEnd('/');

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// Tells GitHub about deploys: a commit status per app on the commit it builds (when that's on in Settings, as it is
/// unless turned off), and for a preview of a pull request one comment on the pull request, made once and then kept up
/// to date. It uses the saved token; without one it does nothing. A feature GitHub refuses on a repo is shown in Settings
/// and rests on that repo for an hour, or everywhere when it refused the token itself (<see cref="GitHubIntegrationState"/>).
/// A GitLab app's deploys are told to GitLab the same way (<see cref="ForGitLab"/>): a commit status, and one note on a
/// merge request, each on its own switch in Settings › GitLab, with GitLab's token and its own state.
/// It never fails a deploy: the pipeline gives it 10 seconds and logs whatever goes wrong.
/// </summary>
/// <param name="gitLab">GitLab's API; without it (in older tests) GitLab apps' deploys aren't reported.</param>
/// <param name="gitLabState">What GitLab refused, as Settings › GitLab shows it.</param>
public sealed class GitHubReporter(
    IDbContextFactory<GitberthDb> dbFactory,
    SettingsService settings,
    GitHubWrites github,
    GitHubIntegrationState state,
    GitberthOptions options,
    ILogger<GitHubReporter> logger,
    HostScanner? hostScanner = null,
    GitLabApi? gitLab = null,
    GitLabIntegrationState? gitLabState = null) : IDeployReporter
{
    /// <summary>
    /// Where a deploy is reported: a host's two switches, what it refused (its state), and its calls for this repo. GitHub's
    /// comment is found by the ID Gitberth kept; GitLab's also by its marker (<see cref="FindComment"/>), when that was lost.
    /// </summary>
    private sealed record Target(string Name, string StatusesKey, string CommentsKey, GitHubIntegrationState State,
        Func<CommitStatus, string, Task> SetStatus, Func<long, int, string, Task<bool>> UpdateComment, Func<int, string, Task<long>> CreateComment,
        Func<int, Task<long?>>? FindComment = null);

    /// <summary>A GitHub commit status's state as GitLab's: pending is running (it builds), failure failed, error canceled.</summary>
    internal static string GitLabState(string gitHubState) => gitHubState switch
    {
        "pending" => "running",
        "success" => "success",
        "failure" => "failed",
        _ => "canceled",
    };

    /// <summary>
    /// The branch a GitLab status is for: the one the deploy builds (the app's, or its preview's), none for a fork's merge
    /// request (its branch is another project's) or a promote (production builds a preview's commit).
    /// </summary>
    internal static string? GitLabRefOf(AppDefinition app, PreviewInfo? preview, DeployTrigger trigger) =>
        preview is not null ? preview.FromFork ? null : preview.Branch
        : trigger == DeployTrigger.Promote ? null
        : app.Branch;

    // Deploys that reported their start: only those report how they ended, so a recreate (a restart, a settings change)
    // doesn't post a status on a commit nobody pushed just now.
    private readonly ConcurrentDictionary<int, byte> _started = new();

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public Task StartedAsync(DeployReport report, CancellationToken ct)
    {
        _started[report.DeploymentId] = 0;
        return ReportAsync(report, ended: false, ct);
    }

    public Task FinishedAsync(DeployReport report, CancellationToken ct)
    {
        var started = _started.TryRemove(report.DeploymentId, out _);
        // A skipped commit never started a build: it gets its success status, and a preview's comment stays as it is,
        // still about the version that runs.
        if (report.Status == DeployStatus.Skipped) return ReportAsync(report, ended: true, ct, comment: false);
        // A preview whose deploy failed before its commit was known still says so on its pull request.
        if (!started && !(report.CommitSha is null && report.Status == DeployStatus.Failed)) return Task.CompletedTask;
        return ReportAsync(report, ended: true, ct, statuses: started);
    }

    private async Task ReportAsync(DeployReport report, bool ended, CancellationToken ct, bool statuses = true, bool comment = true)
    {
        var token = await settings.GetSecretAsync(SettingKeys.GitHubToken, ct);
        var gitLabConn = gitLab is not null && gitLabState is not null ? await gitLab.ConnectionAsync(ct) : null;
        if (token is null && gitLabConn is not { HasToken: true }) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == report.AppId, ct);
        if (app is null || !app.Deployed || app.Owner.Length == 0) return;
        var preview = app.IsPreview ? await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == app.Id, ct) : null;
        // Each host with its own token: a GitHub repo's deploy is GitHub's to hear, a GitLab project's GitLab's.
        Target? target = RepoRef.Of(app.Owner, app.Repo) is GitLabRepoRef project
            ? gitLabConn is { HasToken: true } ? ForGitLab(gitLabConn, project, GitLabRefOf(app, preview, report.Trigger), ct) : null
            : token is not null ? ForGitHub(token, app.Owner, app.Repo, ct) : null;
        if (target is null) return;
        var parentName = app.ParentAppId is int parentId
            ? await db.Apps.AsNoTracking().Where(a => a.Id == parentId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? app.Name
            : app.Name;
        var uiAddress = await settings.GetAsync(SettingKeys.UiAddress, ct);
        var logUrl = GitHubReports.LogUrl(uiAddress, app.Id, report.DeploymentId);
        var address = ended && report.Status == DeployStatus.Succeeded || preview is not null ? GitHubReports.Address(app, await LanHostAsync(uiAddress, ct)) : null;
        var status = ended ? report.Status : DeployStatus.Building;
        // A repo the host refused rests on its own; other repos' deploys still report. (owner/repo, or group/sub/project.)
        var repo = RepoRef.FullNameOf(app.Owner, app.Repo);

        if (statuses && report.CommitSha is { } sha && await OnAsync(target.StatusesKey, ct) && !target.State.Paused(GitHubFeature.Statuses, repo, Now()))
        {
            var context = GitHubReports.Context(parentName, app.IsPreview);
            var commitStatus = GitHubReports.Status(status, report.Error, context, address, logUrl, app.IsPreview, report.Skip);
            await TryAsync(target, GitHubFeature.Statuses, repo, () => target.SetStatus(commitStatus, sha));
        }

        if (comment && preview is { Kind: PreviewKind.PullRequest, PullRequest: int number } && await OnAsync(target.CommentsKey, ct)
            && !target.State.Paused(GitHubFeature.Comments, repo, Now()))
        {
            var body = GitHubReports.CommentBody(new PreviewComment(parentName, GitHubReports.CommentState(status),
                status == DeployStatus.Succeeded || app.CurrentImage is not null ? address : null, report.CommitSha ?? preview.HeadSha, logUrl, Now()));
            await TryAsync(target, GitHubFeature.Comments, repo, async () =>
            {
                if (preview.CommentId is long id && await target.UpdateComment(id, number, body)) return;
                // Gitberth's comment that it lost track of (its ID wasn't saved): that one is kept up to date, never a second.
                if (preview.CommentId is null && target.FindComment is { } find && await find(number) is long found && await target.UpdateComment(found, number, body))
                {
                    await db.Previews.Where(p => p.AppId == app.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CommentId, found), ct);
                    return;
                }
                // None yet, or someone deleted it: a new one, remembered for next time.
                var made = await target.CreateComment(number, body);
                await db.Previews.Where(p => p.AppId == app.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CommentId, made), ct);
            });
        }
    }

    // GitHub's calls for a repo, with the GitHub token.
    private Target ForGitHub(string token, string owner, string repo, CancellationToken ct) => new("GitHub", SettingKeys.GitHubStatuses, SettingKeys.GitHubComments, state,
        (status, sha) => github.SetStatusAsync(token, owner, repo, sha, status, ct),
        (id, _, body) => github.UpdateCommentAsync(token, owner, repo, id, body, ct),
        (number, body) => github.CreateCommentAsync(token, owner, repo, number, body, ct));

    // The state each GitLab status was last set to, by project, commit, name and branch: the same state isn't posted twice,
    // since GitLab's state machine refuses that move (its 400 "Cannot transition status"). Forgotten when it grows large.
    private readonly ConcurrentDictionary<string, string> _gitLabStatuses = new(StringComparer.Ordinal);

    /// <summary>
    /// GitLab's calls for a project, with the GitLab token: the status under the same name GitHub's context has, in GitLab's
    /// states, on <paramref name="gitRef"/>, never the same state twice in a row; the merge request's note, found again by
    /// its marker when its ID was lost.
    /// </summary>
    private Target ForGitLab(GitLabConnection conn, GitLabRepoRef project, string? gitRef, CancellationToken ct) =>
        new("GitLab", SettingKeys.GitLabStatuses, SettingKeys.GitLabComments, gitLabState!,
            async (status, sha) =>
            {
                var key = $"{project.PathWithNamespace}|{sha}|{status.Context}|{gitRef}".ToLowerInvariant();
                var state = GitLabState(status.State);
                if (_gitLabStatuses.TryGetValue(key, out var last) && last == state) return;
                await gitLab!.SetStatusAsync(conn, project, sha, state, status.Context, status.Description, status.TargetUrl, gitRef, ct);
                if (_gitLabStatuses.Count > 2000) _gitLabStatuses.Clear();
                _gitLabStatuses[key] = state;
            },
            (id, number, body) => gitLab!.UpdateNoteAsync(conn, project, number, id, body, ct),
            (number, body) => gitLab!.CreateNoteAsync(conn, project, number, body, ct),
            async number => (await gitLab!.ListNotesAsync(conn, project, number, ct))
                .FirstOrDefault(n => !n.System && n.Body.StartsWith(GitHubReports.CommentMarker, StringComparison.Ordinal))?.Id);

    private async Task<bool> OnAsync(string key, CancellationToken ct) => await settings.GetAsync(key, ct) != "false";

    private async Task TryAsync(Target target, GitHubFeature feature, string repo, Func<Task> call)
    {
        var hostState = target.State;
        try
        {
            await call();
            hostState.Worked(feature, repo);
        }
        catch (HostWriteException ex) when (ex.BadToken)
        {
            if (hostState.Report(feature, ex.Message, Now())) logger.LogWarning("{Host} refused Gitberth's token: {Problem}", target.Name, ex.Message);
        }
        catch (HostWriteException ex) when (ex.MissingPermission)
        {
            if (hostState.Refused(feature, repo, ex.Message, Now())) logger.LogWarning("{Host} refused Gitberth on {Repo}: {Problem}", target.Name, repo, ex.Message);
        }
        catch (HostWriteException ex)
        {
            logger.LogWarning("Couldn't report a deploy to {Host}: {Problem}", target.Name, ex.Message);
        }
    }

    // The LAN address of this host, as every page shows it; the address Gitberth's pages were opened at stands in last.
    private async Task<string?> LanHostAsync(string? uiAddress, CancellationToken ct)
    {
        var browserHost = Uri.TryCreate(uiAddress, UriKind.Absolute, out var ui) ? ui.Host : "";
        var host = await AddressBook.LanHostFromAsync(await settings.GetAsync(SettingKeys.PublicHost, ct), options.PublicHost,
            async () => hostScanner is null ? null : await hostScanner.DetectedAddressAsync(), browserHost);
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }
}
