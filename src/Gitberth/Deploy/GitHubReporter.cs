using System.Collections.Concurrent;
using System.Globalization;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Domains;
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
    /// the commit's fault). The text never repeats the deploy's error, which a public repo would show to everyone: the
    /// link goes to the log instead.
    /// </summary>
    public static CommitStatus Status(DeployStatus status, string? error, string context, string? address, string? logUrl, bool preview)
    {
        var what = preview ? "Preview" : "Deploy";
        var (state, text, link) = status switch
        {
            DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting => ("pending", $"Building and deploying on Gitberth", logUrl),
            DeployStatus.Succeeded => ("success", address is null ? $"{what} is live" : $"{what} is live at {Bare(address)}", address ?? logUrl),
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
/// to date. It uses the saved token; without one it does nothing. A feature the token isn't allowed to use is shown in
/// Settings and rests for an hour (<see cref="GitHubIntegrationState"/>). It never fails a deploy: the pipeline gives it
/// 10 seconds and logs whatever goes wrong.
/// </summary>
public sealed class GitHubReporter(
    IDbContextFactory<GitberthDb> dbFactory,
    SettingsService settings,
    GitHubWrites github,
    GitHubIntegrationState state,
    GitberthOptions options,
    ILogger<GitHubReporter> logger,
    HostScanner? hostScanner = null) : IDeployReporter
{
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
        // A preview whose deploy failed before its commit was known still says so on its pull request.
        if (!started && !(report.CommitSha is null && report.Status == DeployStatus.Failed)) return Task.CompletedTask;
        return ReportAsync(report, ended: true, ct, statuses: started);
    }

    private async Task ReportAsync(DeployReport report, bool ended, CancellationToken ct, bool statuses = true)
    {
        if (await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is not { } token) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == report.AppId, ct);
        if (app is null || !app.Deployed || app.Owner.Length == 0) return;
        var preview = app.IsPreview ? await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.AppId == app.Id, ct) : null;
        var parentName = app.ParentAppId is int parentId
            ? await db.Apps.AsNoTracking().Where(a => a.Id == parentId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? app.Name
            : app.Name;
        var uiAddress = await settings.GetAsync(SettingKeys.UiAddress, ct);
        var logUrl = GitHubReports.LogUrl(uiAddress, app.Id, report.DeploymentId);
        var address = ended && report.Status == DeployStatus.Succeeded || preview is not null ? GitHubReports.Address(app, await LanHostAsync(uiAddress, ct)) : null;
        var status = ended ? report.Status : DeployStatus.Building;

        if (statuses && report.CommitSha is { } sha && await OnAsync(SettingKeys.GitHubStatuses, ct) && !state.Paused(GitHubFeature.Statuses, Now()))
        {
            var context = GitHubReports.Context(parentName, app.IsPreview);
            var commitStatus = GitHubReports.Status(status, report.Error, context, address, logUrl, app.IsPreview);
            await TryAsync(GitHubFeature.Statuses, () => github.SetStatusAsync(token, app.Owner, app.Repo, sha, commitStatus, ct));
        }

        if (preview is { Kind: PreviewKind.PullRequest, PullRequest: int number } && await OnAsync(SettingKeys.GitHubComments, ct) && !state.Paused(GitHubFeature.Comments, Now()))
        {
            var body = GitHubReports.CommentBody(new PreviewComment(parentName, GitHubReports.CommentState(status),
                status == DeployStatus.Succeeded || app.CurrentImage is not null ? address : null, report.CommitSha ?? preview.HeadSha, logUrl, Now()));
            await TryAsync(GitHubFeature.Comments, async () =>
            {
                if (preview.CommentId is long id && await github.UpdateCommentAsync(token, app.Owner, app.Repo, id, body, ct)) return;
                // None yet, or someone deleted it: a new one, remembered for next time.
                var made = await github.CreateCommentAsync(token, app.Owner, app.Repo, number, body, ct);
                await db.Previews.Where(p => p.AppId == app.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CommentId, made), ct);
            });
        }
    }

    private async Task<bool> OnAsync(string key, CancellationToken ct) => await settings.GetAsync(key, ct) != "false";

    private async Task TryAsync(GitHubFeature feature, Func<Task> call)
    {
        try
        {
            await call();
            state.Worked(feature);
        }
        catch (GitHubWriteException ex) when (ex.MissingPermission)
        {
            if (state.Report(feature, ex.Message, Now())) logger.LogWarning("GitHub refused Gitberth's token: {Problem}", ex.Message);
        }
        catch (GitHubWriteException ex)
        {
            logger.LogWarning("Couldn't report a deploy to GitHub: {Problem}", ex.Message);
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
