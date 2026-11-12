using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Domains;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Notifications;

/// <summary>
/// Tells about deploys that ended: every failure (a scheduled restart's too, which nobody watches), and when that's on,
/// every success of a deploy that built a commit (not restarts, settings changes or starts, which would say the same thing
/// over and over). A deploy that Gitberth's own restart cut short is tried again by itself, so only the retry's outcome is
/// told; a cancel is the user's own doing, so nothing is.
/// </summary>
public sealed class DeployNotifier(IDbContextFactory<GitberthDb> dbFactory, NotificationSettings settings, Notifier notifier) : IDeployReporter
{
    /// <summary>The longest error a message quotes; the deploy log has all of it.</summary>
    public const int ErrorLimit = 400;

    public Task StartedAsync(DeployReport report, CancellationToken ct) => Task.CompletedTask;

    public async Task FinishedAsync(DeployReport report, CancellationToken ct)
    {
        if (EventFor(report) is not { } kind || settings.Destinations.Count == 0 || !settings.IsOn(kind)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == report.AppId, ct);
        if (app is null) return;
        var link = await notifier.LinkAsync($"apps/{app.Id}/deployments/{report.DeploymentId}", ct);
        var main = app.IsPreview || kind != NotifyEvent.DeploySucceeded ? null : DomainService.MainOf(app.Domains);
        notifier.Publish(Build(report, kind, app.Name, app.ContainerName, app.IsPreview, main, link, DateTime.UtcNow));
    }

    /// <summary>What a finished deploy is told as, or null when it isn't told.</summary>
    public static NotifyEvent? EventFor(DeployReport report) => report.Status switch
    {
        DeployStatus.Failed when report.Error?.StartsWith("Gitberth restarted", StringComparison.Ordinal) == true => null,
        DeployStatus.Failed or DeployStatus.RolledBack => NotifyEvent.DeployFailed,
        DeployStatus.Succeeded when report.Trigger is DeployTrigger.Restart or DeployTrigger.ConfigChange or DeployTrigger.Start => null,
        DeployStatus.Succeeded => NotifyEvent.DeploySucceeded,
        _ => null,
    };

    /// <summary>The message: pure, so each wording is tested.</summary>
    public static Notice Build(DeployReport report, NotifyEvent kind, string app, string container, bool preview, string? mainDomain, string? link, DateTime at)
    {
        var commit = report.CommitSha is { Length: >= 7 } sha ? sha[..7] : null;
        var what = $"{Format.Trigger(report.Trigger)}{(commit is null ? "" : $", commit {commit}")}";
        var (level, title, text) = (kind, report.Status) switch
        {
            (NotifyEvent.DeploySucceeded, _) => (NoticeLevel.Good, $"{app} is live",
                $"Deploy #{report.DeploymentId} ({what}) succeeded.{(mainDomain is null ? "" : $" It answers at https://{mainDomain}/.")}"),
            (_, DeployStatus.RolledBack) => (NoticeLevel.Critical, $"{app}: the new version didn't pass its checks",
                $"Deploy #{report.DeploymentId} ({what}) was rolled back, so the previous version still runs. {Error(report.Error)}"),
            _ => (NoticeLevel.Critical, $"Deploy of {app} failed",
                $"Deploy #{report.DeploymentId} ({what}) failed. {Error(report.Error)}"),
        };
        return new Notice(kind, level, title, text.TrimEnd(), link, app, container, at)
        {
            AppId = report.AppId, DeploymentId = report.DeploymentId, Commit = report.CommitSha, Trigger = report.Trigger.ToString(), Preview = preview,
        };
    }

    private static string Error(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "The deploy log has the details.";
        var text = string.Join(' ', error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= ErrorLimit ? text : text[..(ErrorLimit - 1)] + "…";
    }
}

/// <summary>
/// Tells every reporter (GitHub, notifications) at once: a slow GitHub never uses up the time the others have, and one
/// that fails never keeps the others from hearing.
/// </summary>
public sealed class DeployReporters(IReadOnlyList<IDeployReporter> reporters, ILogger<DeployReporters> logger) : IDeployReporter
{
    public Task StartedAsync(DeployReport report, CancellationToken ct) => EachAsync(r => r.StartedAsync(report, ct));

    public Task FinishedAsync(DeployReport report, CancellationToken ct) => EachAsync(r => r.FinishedAsync(report, ct));

    private Task EachAsync(Func<IDeployReporter, Task> call) => Task.WhenAll(reporters.Select(async reporter =>
    {
        try
        {
            await call(reporter);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Reporter} couldn't report a deploy", reporter.GetType().Name);
        }
    }));
}
