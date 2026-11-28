using System.ComponentModel;
using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>
/// Running apps, as the buttons on an app's page do: deploy, restart, start, stop, roll back, cancel, promote a preview,
/// updates, cron jobs, and previews on or off. Each goes through the same service call the button makes, with the same
/// refusals. Operate access.
/// </summary>
[McpServerToolType]
public sealed class OperateTools(AgentContext ctx, AppActions actions, AppService apps, UpdateService updates, PreviewService previews, CronScheduler crons)
{
    [McpServerTool(Name = "deploy_app", Title = "Deploy an app", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Deploys a Git app (clone, build, start, zero-downtime where it can) or a Compose app (its images pulled anew), like the Deploy button: " +
        "production always builds the app's own branch, its newest commit, or with commit one earlier commit of that branch (or one production ran before), " +
        "to go back or forward to it. A preview's code reaches production only through promote_preview. " +
        "Returns at once with the deployment_id; with wait it follows the deploy (up to 10 minutes) and returns how it ended with the log's last lines. " +
        "An app that runs a registry's image isn't deployed: use apply_update.")]
    public async Task<object> DeployApp(
        [Description("The app: its key, name or number.")] string app,
        [Description("Build without the build cache: every step runs again and base images are pulled.")] bool no_cache = false,
        [Description("A Git app: build exactly this commit of the app's own branch (its SHA, 7 to 40 hex characters) instead of its newest. " +
            "Only a commit on the branch, or one production ran before, is built: any other fails when it's fetched.")] string? commit = null,
        [Description("Wait for the deploy to end (up to 10 minutes) and return its outcome.")] bool wait = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = AgentContext.DeployedRow(view, "be deployed");
        string? sha = null;
        if (!string.IsNullOrWhiteSpace(commit))
        {
            sha = commit.Trim().ToLowerInvariant();
            if (sha.Length is < 7 or > 40 || !GitClient.IsSha(sha)) throw new ToolError("commit is a commit's SHA: 7 to 40 hex characters, like 1d2948c.");
            // [builder B, B6] An uploaded app builds its newest upload: a new version comes as an upload, not a commit.
            if (row.FromUpload) throw new ToolError($"{view.Summary.Title} is an uploaded app: deploy_app builds its newest upload, so commit doesn't apply. POST a new one to /api/v1/apps/{view.Summary.Key}/upload.");
            if (row.Source != AppSource.Git) throw new ToolError($"{view.Summary.Title} is a Compose app: it deploys its file as it is, so commit doesn't apply.");
            if (row.IsPreview) throw new ToolError($"{view.Summary.Title} is a preview: it builds its branch's newest commit. promote_preview puts its commit into production.");
        }
        // A commit goes with the app's own branch: the deploy fetches it from there, after checking it's on it.
        var id = await actions.DeployAsync(row.Id, DeployTrigger.Manual, ct, gitRef: sha is null ? null : DeployPipeline.OwnBranchRef(row), commit: sha, noCache: no_cache);
        return await DeployedAsync(id, wait, ct);
    }

    [McpServerTool(Name = "restart_app", Title = "Restart an app", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Restarts the app's container (a Compose app: every service), like Restart on its page. Nothing is rebuilt or recreated.")]
    public async Task<object> RestartApp(
        [Description("The app: its key, name or number.")] string app,
        CancellationToken ct = default) => await RunAsync(app, ContainerAction.Restart, null, ct);

    [McpServerTool(Name = "start_app", Title = "Start an app", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Starts a stopped app, like Start on its page. A Git app whose settings changed while it was stopped is recreated with them; " +
        "one whose container is gone is made again (from its last image, or built).")]
    public async Task<object> StartApp(
        [Description("The app: its key, name or number.")] string app,
        CancellationToken ct = default) => await RunAsync(app, ContainerAction.Start, null, ct);

    [McpServerTool(Name = "stop_app", Title = "Stop an app", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Stops the app until it's started again (its domains show the down page meanwhile, and a Git app's new commits aren't deployed). " +
        "Destructive for whoever uses it: pass confirm with the app's name, exactly as get_app or list_apps names it.")]
    public async Task<object> StopApp(
        [Description("The app: its key, name or number.")] string app,
        [Description("The app's name, repeated, to show the stop is meant.")] string? confirm = null,
        CancellationToken ct = default) => await RunAsync(app, ContainerAction.Stop, confirm, ct);

    [McpServerTool(Name = "rollback_app", Title = "Roll back an app", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Rolls the app back. A Git or Compose app goes back to an earlier successful deploy whose image is still kept (list_deployments says can_roll_back): " +
        "pass its deployment_id. An app that runs a registry's image goes back to the image it ran before its last update: leave deployment_id out. " +
        "Pass confirm with the app's name. Returns at once (with the new deployment_id, for a Git app); with wait it follows it to the end.")]
    public async Task<object> RollbackApp(
        [Description("The app: its key, name or number.")] string app,
        [Description("The app's name, repeated, to show the rollback is meant.")] string? confirm = null,
        [Description("The earlier deploy to go back to (a Git or Compose app's).")] int? deployment_id = null,
        [Description("Wait for it to end (up to 10 minutes) and return the outcome.")] bool wait = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        AgentContext.Confirm(confirm, view.Summary, "roll back");
        if (AppCommands.Refusal(view.Summary, ContainerAction.Restart) is { } refusal) throw ToolError.Conflict(refusal);
        if (view.Row is { Deployed: true } row)
        {
            if (deployment_id is not int to) throw new ToolError($"Say which deploy of {view.Summary.Title} to go back to: deployment_id, from list_deployments (one with can_roll_back).");
            var dep = await ctx.DeploymentAsync(to, ct);
            if (dep is null || dep.AppId != row.Id) throw ToolError.NotFound($"{view.Summary.Title} has no deploy #{to}.");
            var kept = await actions.RollbackImagesAsync(row.Id, ct);
            if (!AppActivityTab.CanRollBackTo(dep, row.CurrentImage, kept))
                throw ToolError.Conflict(dep.Status != DeployStatus.Succeeded
                    ? $"Deploy #{to} didn't succeed, so there's nothing to go back to."
                    : $"Deploy #{to}'s image isn't on this host any more (only the last few are kept), or it's the one running now.");
            var id = await actions.RollbackAsync(row.Id, to);
            return await DeployedAsync(id, wait, ct);
        }
        if (deployment_id is not null) throw new ToolError($"{view.Summary.Title} isn't deployed from a repo: leave deployment_id out to go back to the image it ran before its last update.");
        var prepared = await updates.PrepareRollbackAsync(view.Summary.Key, ct);
        return await StartChangeAsync(view.Summary, prepared, "roll back", "There's nothing to roll back to.", wait, ct);
    }

    [McpServerTool(Name = "cancel_deploy", Title = "Cancel a deploy", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Cancels the app's running deploy, like Cancel on the deploy's page. The app keeps running what it ran before.")]
    public async Task<object> CancelDeploy(
        [Description("The app: its key, name or number.")] string app,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = AgentContext.DeployedRow(view, "have deploys to cancel");
        var cancelled = actions.CancelDeploy(row.Id);
        return new
        {
            app = view.Summary.Title,
            cancelled,
            message = cancelled ? $"Asked the running deploy of {view.Summary.Title} to stop: it ends as Cancelled." : $"{view.Summary.Title} has no deploy running.",
        };
    }

    [McpServerTool(Name = "promote_preview", Title = "Promote a preview", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Deploys a preview's commit to production, like Promote on the Previews tab: production runs that commit until its branch gets a newer one. " +
        "Name the preview by its key from list_previews.")]
    public async Task<object> PromotePreview(
        [Description("The preview: its key (from list_previews) or number.")] string preview,
        [Description("Wait for production's deploy to end (up to 10 minutes) and return the outcome.")] bool wait = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(preview, ct);
        if (view.Row is not { IsPreview: true } row) throw new ToolError($"{view.Summary.Title} isn't a preview: list_previews lists an app's.");
        var id = await previews.PromoteAsync(row.Id, ct);
        return await DeployedAsync(id, wait, ct);
    }

    [McpServerTool(Name = "check_updates", Title = "Check for updates", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Asks the registry whether a newer image is waiting for an app that runs a registry's tag, like Check for update on its page. Nothing is pulled or changed.")]
    public async Task<object> CheckUpdates(
        [Description("The app: its key, name or number.")] string app,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        if (view.Summary.Source is AppSource.Git or AppSource.Compose or AppSource.Upload)
            throw new ToolError($"{view.Summary.Title} is built or deployed by Gitberth: deploy_app brings it up to date.");
        var status = await updates.CheckAsync(view.Summary.Key, ct);
        return new
        {
            app = view.Summary.Title,
            available = status.Available,
            text = status.Text,
            tracked = status.Tracked,
            checked_at = status.CheckedAt,
            error = status.Error,
            can_roll_back = status.CanRollBack,
        };
    }

    [McpServerTool(Name = "apply_update", Title = "Apply an update", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Updates an app that runs a registry's tag, like Update on its page: pulls the tag and recreates the app on the new image, " +
        "putting the old one back if the new one doesn't stay up. Returns once it started; with wait it follows it to the end.")]
    public async Task<object> ApplyUpdate(
        [Description("The app: its key, name or number.")] string app,
        [Description("Wait for the update to end (up to 10 minutes) and return the outcome.")] bool wait = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        if (view.Summary.Source is AppSource.Git or AppSource.Compose or AppSource.Upload)
            throw new ToolError($"{view.Summary.Title} is built or deployed by Gitberth: deploy_app brings it up to date.");
        if (AppCommands.Refusal(view.Summary, ContainerAction.Restart) is { } refusal) throw ToolError.Conflict(refusal);
        var prepared = await updates.PrepareAsync(view.Summary.Key, ct: ct);
        return await StartChangeAsync(view.Summary, prepared, "update", "There's nothing to update: it runs the newest image its tag names.", wait, ct);
    }

    [McpServerTool(Name = "run_cron_now", Title = "Run a cron job now", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Runs one of the app's cron jobs (from its vercel.json) now, like Run now on its Scheduled jobs card, and returns how it went. get_app lists the jobs.")]
    public async Task<object> RunCronNow(
        [Description("The app: its key, name or number.")] string app,
        [Description("The job's path, as get_app's cron_jobs gives it (/api/cron/cleanup).")] string path,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var id = view.Summary.AppId ?? throw ToolError.Conflict($"{view.Summary.Title} has no cron jobs.");
        var jobs = await crons.GetAsync(id, ct);
        var job = jobs.Jobs.FirstOrDefault(j => string.Equals(j.Path, path?.Trim(), StringComparison.Ordinal))
            ?? throw ToolError.NotFound(jobs.Jobs.Count == 0 ? $"{view.Summary.Title} has no cron jobs." : $"{view.Summary.Title} has no cron job {path}: it has {string.Join(", ", jobs.Jobs.Select(j => j.Path))}.");
        if (jobs.Problem is { } problem) throw ToolError.Conflict(problem);
        var run = await crons.RunNowAsync(id, job.Path, job.Schedule);
        return new
        {
            app = view.Summary.Title,
            path = job.Path,
            succeeded = run.Succeeded,
            outcome = run.Outcome.ToString(),
            http_status = run.Status,
            seconds = Math.Round(run.Duration.TotalSeconds, 2),
            detail = run.Detail,
        };
    }

    [McpServerTool(Name = "set_previews", Title = "Turn previews on or off", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Operate)]
    [Description("Turns a Git app's preview deployments on or off, as the choice at the top of its Settings tab's Previews section does: off, pull_requests, " +
        "or pull_requests_and_branches. Nothing else about previews changes (set_preview_settings changes the rest, with Admin access). " +
        "Turning previews off removes every preview the app has, with its containers, images, volumes, domains and databases: when it has any, pass confirm with the app's name.")]
    public async Task<object> SetPreviews(
        [Description("The app: its key, name or number.")] string app,
        [Description("off, pull_requests, or pull_requests_and_branches.")] string mode,
        [Description("When mode is off and the app has previews: the app's name, repeated, to show removing them is meant.")] string? confirm = null,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = view.Row is { Source: AppSource.Git, ParentAppId: null } r ? r : throw ToolError.Conflict($"{view.Summary.Title} isn't a Git app, so it has no previews.");
        var wanted = ParseMode(mode);
        var current = PreviewSettings.Of(row);
        // As the Previews section asks first: turning them off removes those there are.
        if (wanted == PreviewMode.Off && current.Mode != PreviewMode.Off && await previews.CountAsync(row.Id, ct) is var count and > 0)
            AgentContext.Confirm(confirm, view.Summary, $"turn previews off and remove {(count == 1 ? "the preview" : $"the {count} previews")} of");
        var saved = await previews.SaveSettingsAsync(row.Id, current with { Mode = wanted }, ct);
        if (saved.Problem is { } problem) throw new ToolError(problem);
        return new
        {
            app = view.Summary.Title,
            mode = wanted.ToString(),
            message = wanted == PreviewMode.Off ? "Previews are off." : "Saved. Gitberth looks at the repo again now.",
            warning = saved.Warning,
        };
    }

    // ---- shared ----

    private async Task<object> RunAsync(string app, ContainerAction action, string? confirm, CancellationToken ct)
    {
        var view = await ctx.AppAsync(app, ct);
        if (action == ContainerAction.Stop) AgentContext.Confirm(confirm, view.Summary, "stop");
        if (AppCommands.Refusal(view.Summary, action) is { } refusal) throw ToolError.Conflict(refusal);
        var error = action switch
        {
            ContainerAction.Start => await actions.StartAsync(view.Summary.Key, ct),
            ContainerAction.Stop => await actions.StopAsync(view.Summary.Key, ct),
            _ => await actions.RestartAsync(view.Summary.Key, ct),
        };
        if (error is not null) throw ToolError.Conflict(error);
        var done = action switch { ContainerAction.Start => "Started", ContainerAction.Stop => "Stopped", _ => "Restarted" };
        return new { app = view.Summary.Title, key = view.Summary.Key, done = true, message = $"{done} {view.Summary.Title}." };
    }

    private async Task<object> DeployedAsync(int deploymentId, bool wait, CancellationToken ct)
    {
        if (wait) return await ctx.WaitForDeployAsync(deploymentId, ct);
        var dep = await apps.GetDeploymentAsync(deploymentId, ct) ?? throw ToolError.NotFound($"There's no deploy #{deploymentId}.");
        return new
        {
            deployment_id = dep.Id,
            status = dep.Status.ToString(),
            message = $"Deploy #{dep.Id} is queued. get_deployment follows it, or call again with wait.",
            page = $"{await ctx.RootAsync(ct)}apps/{dep.AppId}/deployments/{dep.Id}",
        };
    }

    // An update or an image rollback: the plan the page's dialog shows, started as its Start does, and followed when asked.
    private async Task<object> StartChangeAsync(AppSummary app, UpdatePrepared prepared, string verb, string nothing, bool wait, CancellationToken ct)
    {
        if (prepared.Blocked is { } blocked) throw ToolError.Conflict(blocked);
        if (prepared.Plan is not { } plan)
            return new { app = app.Title, changed = false, message = prepared.UpToDate ? "It's up to date: it runs the newest image its tag names." : nothing };
        if (plan.Blocked is { } planBlocked) throw ToolError.Conflict(planBlocked);
        var job = updates.Start(plan);
        if (ChangeText.NotThisChange(job, plan, app.Title) is { } notMine) throw ToolError.Conflict(notMine);
        if (!wait)
            return new { app = app.Title, changed = true, started = true, message = $"Started: Gitberth is going to {verb} {app.Title}. get_app shows how it ended, or call again with wait.", impact = plan.Impact };
        if (await AgentContext.WaitAsync(job.Completion, ct) is not { } result)
            return new { app = app.Title, still_running = true, container = job.Container, message = $"It's still going after 10 minutes: get_app shows how {app.Title} ends up." };
        try
        {
            await updates.FollowUpOf(plan.Container).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
        }
        return new { app = app.Title, changed = result.Ok, outcome = result.Outcome.ToString(), message = result.Message };
    }

    private static PreviewMode ParseMode(string mode) => mode?.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "off" or "none" or "false" => PreviewMode.Off,
        "pull_requests" or "pullrequests" or "prs" => PreviewMode.PullRequests,
        "pull_requests_and_branches" or "pullrequestsandbranches" or "branches" or "all" => PreviewMode.PullRequestsAndBranches,
        _ => throw new ToolError("mode is off, pull_requests or pull_requests_and_branches."),
    };
}
