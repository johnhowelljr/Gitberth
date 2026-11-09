using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// Housekeeping after a (re)start: fail interrupted deploys, join the app network, repair half-finished swaps. Apart
/// from those, once Docker answers: remove the helpers a restart left behind, finish or undo the port changes it cut
/// short, then check the networks Gitberth keeps containers on. Each port forwarder an older Gitberth made is named once.
/// </summary>
/// <param name="engine">The Engine API, asked whether Docker is up before port changes are settled.</param>
/// <param name="changer">Finishes or undoes the port changes a restart cut short, acting only on the containers its journal names.</param>
public sealed class StartupTasks(
    IDbContextFactory<GitberthDb> dbFactory,
    DockerCli docker,
    AppLocks locks,
    AppActions actions,
    DeployPipeline pipeline,
    GitberthOptions options,
    ILogger<StartupTasks> logger,
    IContainerEngine? engine = null,
    PortChanger? changer = null,
    HostHelpers? helpers = null,
    NetworkKeeper? keeper = null) : BackgroundService
{
    private static readonly TimeSpan DockerRetry = TimeSpan.FromSeconds(10);

    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>The settling once Docker answers (<see cref="SettleAsync"/>), which the start doesn't wait for; a test can.</summary>
    internal Task Settled { get; private set; } = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            var interrupted = await db.Deployments
                .Where(d => d.QueuedAt < _startedAt)
                .Where(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting)
                .ToListAsync(stoppingToken);
            foreach (var dep in interrupted)
            {
                dep.Status = DeployStatus.Failed;
                dep.Error = "Gitberth restarted during this deploy.";
                dep.FinishedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
        }

        foreach (var dir in Directory.EnumerateDirectories(options.WorkDir)) DeployPaths.TryDeleteDirectory(dir);

        // Apart from the rest: Docker may not be up yet, and settling a change can wait out a container's stop timeout.
        Settled = Task.Run(() => SettleAsync(stoppingToken), CancellationToken.None);

        var info = await docker.GetInfoAsync(stoppingToken);
        if (info.ServerVersion is null)
        {
            logger.LogWarning("Docker isn't reachable ({Error}). Is /var/run/docker.sock mounted into this container?", info.Error);
            return;
        }
        logger.LogInformation("Connected to Docker Engine {Version}", info.ServerVersion);

        try
        {
            await docker.EnsureNetworkAsync(stoppingToken);
            if (options.OwnContainer is { } self)
            {
                // By its ID, which its hostname is only unless it was set otherwise; joining the app network lets health checks reach apps by name.
                var error = await docker.ConnectToNetworkAsync(self, stoppingToken);
                if (error is not null) logger.LogWarning("Couldn't join the '{Network}' network ({Error}); health URL checks may fail.", options.Network, error);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Docker network setup failed");
        }

        // Git apps: their deploys' containers. Any other app's missing container is created again from its saved settings
        // once the changes a restart cut short are settled (SettleAsync).
        List<AppDefinition> apps;
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
            apps = await db.Apps.AsNoTracking().Where(a => a.Source == AppSource.Git).ToListAsync(stoppingToken);
        foreach (var app in apps)
        {
            bool missing;
            using (await locks.AcquireAsync(app.Id, stoppingToken))
            {
                var exists = await docker.ContainerExistsAsync(app.ContainerName, stoppingToken);
                var previous = exists ? null : await MovedAsideAsync(app, stoppingToken);
                missing = !exists && previous is null;
                if (previous is not null)
                {
                    logger.LogWarning("Restoring {Container} after an interrupted deploy", app.ContainerName);
                    var renamed = await docker.RenameAsync(previous, app.ContainerName, stoppingToken);
                    if (renamed.Ok && app.DesiredState == DesiredState.Running) await docker.StartAsync(app.ContainerName, stoppingToken);
                }
            }
            // Meant to run but has no container at all (removed by hand, or lost when Gitberth stopped mid-swap): start it again
            // from its image, or, when that's gone too (restored onto a new host, say), build it again, as Start does.
            if (missing && app.DesiredState == DesiredState.Running && app.CurrentImage is not null)
            {
                if (await actions.HasLastImageAsync(app, stoppingToken))
                {
                    logger.LogWarning("{Container} is missing; starting it again from {Image}", app.ContainerName, app.CurrentImage);
                    await actions.RecreateAsync(app.Id, DeployTrigger.Start, ct: stoppingToken);
                }
                else
                {
                    logger.LogWarning("{Container} and its image {Image} are missing; building it again", app.ContainerName, app.CurrentImage);
                    await actions.DeployAsync(app.Id, DeployTrigger.Start, stoppingToken);
                }
            }
        }

        await TidyAsync(stoppingToken);
        await NameForwardersAsync(stoppingToken);
    }

    /// <summary>
    /// Once Docker answers: the probe and check helpers a restart left behind go; each port change it cut short is
    /// finished or undone, on the containers its journal names alone (never on names that only look like Gitberth's
    /// temporary ones, such as Compose's own); then the networks Gitberth keeps containers on are checked.
    /// </summary>
    private async Task SettleAsync(CancellationToken ct)
    {
        if (engine is null || changer is null) return;
        try
        {
            for (var said = false; ; said = true)
            {
                try
                {
                    await engine.VersionAsync(ct);
                    break;
                }
                catch (EngineException ex)
                {
                    if (!said) logger.LogInformation("Port changes wait for Docker to answer ({Reason})", ex.Message);
                    await Task.Delay(DockerRetry, ct);
                }
            }
            if (helpers is not null) await QuietlyAsync(() => helpers.RemoveLeftoversAsync(ct), "Removing helper containers left behind failed");
            await QuietlyAsync(() => changer.RecoverAsync(ct), "Settling the port changes a restart cut short failed");
            if (keeper is not null) await QuietlyAsync(() => keeper.ReconcileAsync(ct), "Checking the networks Gitberth keeps containers on failed");
            await QuietlyAsync(() => CreateMissingAsync(ct), "Creating missing apps again failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Settling the port changes a restart cut short failed");
        }
    }

    /// <summary>
    /// An app Gitberth created from a registry image, a local image or by hand that's meant to run but has no container
    /// (removed by hand, or Gitberth restarted in the middle of creating it) is created again from its saved settings, as
    /// a Git app is from its image (parity with Git). Only those, and only once they had a container (a create that never
    /// worked isn't made by itself): an Existing app is never made again by itself, and nothing is done for a name whose
    /// change isn't settled, without saved settings, or for an app whose container was renamed outside Gitberth (it's still
    /// there, on the same volumes: <see cref="AppActions.CreateAgainAsync"/> refuses it). Docker not answering for a
    /// container isn't taken as its being gone.
    /// </summary>
    internal async Task CreateMissingAsync(CancellationToken ct)
    {
        if (engine is null) return;
        List<AppDefinition> apps;
        HashSet<string> changing;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            apps = await db.Apps.AsNoTracking()
                .Where(a => (a.Source == AppSource.Registry || a.Source == AppSource.Local || a.Source == AppSource.Manual)
                    && a.DesiredState == DesiredState.Running && a.SpecProtected != null && a.ContainerId != null)
                .ToListAsync(ct);
            if (apps.Count == 0) return;
            changing = (await db.ContainerChanges.AsNoTracking()
                .Where(c => c.Outcome == ChangeOutcome.Running || c.Outcome == ChangeOutcome.NeedsDecision)
                .Select(c => c.ContainerName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        }
        foreach (var app in apps.Where(a => !changing.Contains(a.ContainerName)))
        {
            if (await engine.InspectContainerAsync(app.ContainerName, ct) is not null) continue;
            logger.LogWarning("{Container} is missing; creating it again from its saved settings", app.ContainerName);
            if (await actions.CreateAgainAsync(app, ct) is { } error) logger.LogWarning("Couldn't create {Container} again: {Error}", app.ContainerName, error);
        }
    }

    private async Task QuietlyAsync(Func<Task> step, string failure)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Failure}", failure);
        }
    }

    // Older Gitberths published a container's port through a forwarder container; each one left is named once.
    private async Task NameForwardersAsync(CancellationToken ct)
    {
        var (containers, _) = await docker.ListContainersAsync(managedOnly: false, ct);
        foreach (var c in containers)
            if (ForwarderWarning(c) is { } warning) logger.LogWarning("{Warning}", warning);
    }

    /// <summary>
    /// "gb-publish-postgres-5432 is an old Gitberth port forwarder; remove it on postgres's Network tab." for a forwarder
    /// (<see cref="ContainerRole.LegacyForwarder"/>), whose label names what it publishes ("postgres:5432"); else null.
    /// </summary>
    internal static string? ForwarderWarning(ContainerSummary c) =>
        c.Labels.TryGetValue(ContainerService.PublishesLabel, out var publishes) && publishes.LastIndexOf(':') is > 0 and var colon
            ? $"{c.Name} is an old Gitberth port forwarder; remove it on {publishes[..colon]}'s Network tab."
            : null;

    /// <summary>The app's old container that an interrupted swap moved aside, if there is one.</summary>
    private async Task<string?> MovedAsideAsync(AppDefinition app, CancellationToken ct)
    {
        var previous = app.ContainerName + DeployPipeline.PreviousSuffix;
        return await docker.ContainerExistsAsync(previous, ct) ? previous : null;
    }

    private async Task TidyAsync(CancellationToken stoppingToken)
    {
        // A deploy that was running its new version next to the old one: visitors go back to the app's own container.
        try
        {
            await pipeline.TidyBridgesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Removing temporary containers left by interrupted deploys failed");
        }
    }
}
