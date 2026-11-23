using System.Globalization;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>
/// Save and restore's work at start, the first of Gitberth's jobs: the containers a save stopped or paused and couldn't
/// bring back are brought back; and a restore's second phase runs, on the data its first phase staged (put in place before
/// the database was opened: <see cref="RestoreBoot"/>). Git apps are deployed again one at a time at the commit they ran,
/// Compose apps at their current release; then the gate opens, the domains sync and the uploaded file goes. Each step is
/// written to plan.json as it ends, so a restart in the middle goes on from there.
/// </summary>
/// <param name="audit">Where the restore's end is recorded (backup.restored): in the restored audit log, phase 1's being the fresh install's.</param>
public sealed class RestoreRunner(RestoreService restore, BackupService backup, RestoreGate gate, AppActions actions, DomainService domains,
    IDbContextFactory<GitberthDb> dbFactory, IBackupEngine engine, ILogger<RestoreRunner> logger, Apps.AppCatalog? catalog = null,
    Audit.AuditLog? audit = null) : BackgroundService
{
    /// <summary>How often a deploy is looked at until it ends.</summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Phase 2 as it ran (tests wait for it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var actor = Audit.AuditScope.Begin(Audit.AuditActor.System(Audit.AuditVia.Startup));
        try
        {
            await WaitForDockerAsync(stoppingToken);
            await backup.RecoverAsync(stoppingToken);
            Running = PhaseTwoAsync(stoppingToken);
            await Running;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Save and restore's start-up work failed");
        }
    }

    private async Task WaitForDockerAsync(CancellationToken ct)
    {
        for (var said = false; ; said = true)
        {
            try
            {
                await engine.ApiVersionAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException)
            {
                if (!said) logger.LogInformation("Save and restore waits for Docker to answer ({Reason})", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>The second phase, when a restore's first phase ended and the restore isn't finished.</summary>
    internal async Task PhaseTwoAsync(CancellationToken ct)
    {
        restore.Reload();
        if (restore.Status() is not { Finished: false, Phase: 2 } status) return;
        restore.Stopping = ct;
        gate.Close();
        restore.Log.Line("Gitberth started again on the saved data.");
        await restore.StepAsync("restart", () => Task.FromResult("Started on the saved data."));
        foreach (var step in status.Steps.Where(s => s.Id.StartsWith("deploy:", StringComparison.Ordinal)))
            if (int.TryParse(step.Id["deploy:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
                await restore.StepAsync(step.Id, () => DeployAsync(appId, ct));

        // The apps Gitberth doesn't deploy (images, adopted containers) run in containers phase 1 made: their rows learn the
        // new containers' IDs, ports and settings before Gitberth's jobs look, so none is taken for recreated outside it.
        await SyncRowsAsync(ct);

        // Every app is back: Gitberth's jobs carry on, and the domains are pointed at the apps.
        gate.Open();
        await restore.StepAsync("domains", async () =>
        {
            if (!await domains.EnabledAsync(ct)) return RestoreService.Skip("Domains aren't turned on.");
            var report = await domains.SyncAsync(ct);
            return report.Notes.FirstOrDefault(n => n.Problem) is { } problem ? "Synced, with a problem: " + problem.Text : "Synced.";
        });
        await restore.StepAsync("finish", () =>
        {
            try
            {
                File.Delete(restore.Paths.Upload);
            }
            catch (IOException)
            {
            }
            Audited();
            return Task.FromResult("The uploaded file is removed.");
        });
        restore.Finish();
        restore.Log.Line(restore.Status() is { Failed: > 0 } done
            ? $"The restore finished, with {done.Failed.ToString(CultureInfo.InvariantCulture)} step(s) that failed: see above."
            : "The restore finished: everything is back.");
    }

    // The restore in the audit log Gitberth now runs on (the restored one): which backup, and how many steps failed.
    private void Audited()
    {
        if (audit is null || restore.PlanState is not { } plan) return;
        var failed = plan.Steps.Count(s => s.State == RestoreStepState.Failed);
        audit.Record(new Audit.AuditEntry("backup.restored",
            $"Restored the backup made on {plan.Host} on {plan.SavedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
            + (failed == 0 ? "." : $", with {failed.ToString(CultureInfo.InvariantCulture)} step(s) that failed."))
        {
            Outcome = failed == 0 ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
            Target = plan.Host,
            Changes =
            [
                new("Saved at", null, plan.SavedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)),
                new("Steps", null, plan.Steps.Count.ToString(CultureInfo.InvariantCulture)),
                new("Failed", null, failed.ToString(CultureInfo.InvariantCulture)),
            ],
        });
    }

    // Each app's row brought in line with the container that holds its name now (AppCatalog skips Git and Compose apps,
    // whose rows are the truth). Again after a restart in the middle: it changes nothing twice.
    private async Task SyncRowsAsync(CancellationToken ct)
    {
        if (catalog is null) return;
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await db.Apps.AsNoTracking().Select(a => a.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                await catalog.SyncAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "App {AppId}'s row couldn't be brought in line after the restore", id);
            }
        }
    }

    // A Git app built at the commit it ran (its branch's tip when it never ran one), a Compose file Gitberth keeps brought
    // up at its current release; one at a time, each waited for.
    private async Task<string> DeployAsync(int appId, CancellationToken ct)
    {
        AppDefinition? app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return RestoreService.Skip("It isn't in the saved data.");
        if (app.DesiredState != DesiredState.Running) return RestoreService.Skip("It was stopped: it's deployed when you start it.");
        var deployment = app.FromRepo
            ? await actions.DeployAsync(app.Id, DeployTrigger.Start, ct, gitRef: app.CurrentCommit is null ? null : DeployPipeline.OwnBranchRef(app), commit: app.CurrentCommit)
            : await actions.RecreateAsync(app.Id, DeployTrigger.Start, ct: ct);
        restore.Log.Line($"{app.Name}: deploy #{deployment.ToString(CultureInfo.InvariantCulture)} queued.");
        while (true)
        {
            await Task.Delay(Poll, ct);
            Deployment? dep;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                dep = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deployment, ct);
            if (dep is null) throw new InvalidOperationException("Its deploy is gone.");
            switch (dep.Status)
            {
                case DeployStatus.Succeeded:
                    return dep.CommitSha is { Length: >= 7 } sha ? $"Deployed at {sha[..7]}." : "Deployed.";
                case DeployStatus.Failed or DeployStatus.RolledBack or DeployStatus.Cancelled or DeployStatus.Skipped:
                    throw new InvalidOperationException($"Its deploy #{deployment.ToString(CultureInfo.InvariantCulture)} ended {dep.Status.ToString().ToLowerInvariant()}"
                        + (dep.Error is { Length: > 0 } error ? ": " + error : "."));
            }
        }
    }
}
