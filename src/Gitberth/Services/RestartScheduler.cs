using System.Collections.Concurrent;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// Restarts containers on the schedules set for them, for apps that leak memory or otherwise do better with a fresh start
/// now and then. A container is due once it has been up for its interval, so a deploy or a manual restart starts the
/// count again. A Gitberth app is recreated from its current image, like applying settings: it goes through a deploy's
/// checks, keeps running if the new container fails them, and restarts without downtime when it has a domain. Other
/// containers get `docker restart`, with the stop they ask for, unless Gitberth is changing them: then the restart waits
/// for the next tick, and so does one of either container of a port change that isn't settled.
/// </summary>
/// <param name="containerLocks">The locks a port change and a container's actions hold.</param>
/// <param name="changer">Says when a container mustn't be started (a port change that isn't settled).</param>
public sealed class RestartScheduler(
    IDbContextFactory<GitberthDb> dbFactory,
    DockerCli docker,
    RestartSchedules schedules,
    AppActions actions,
    AppLocks locks,
    ILogger<RestartScheduler> logger,
    ContainerLocks? containerLocks = null,
    PortChanger? changer = null) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly ContainerLocks _containerLocks = containerLocks ?? new ContainerLocks();

    // When each container was last restarted on its schedule. One that didn't come back up isn't tried again before its
    // next interval, so a broken app isn't restarted every minute.
    private readonly ConcurrentDictionary<string, DateTime> _restarted = new(StringComparer.Ordinal);

    /// <summary>When Gitberth last restarted the container on its schedule, since Gitberth started.</summary>
    public DateTime? LastRestart(string container) => _restarted.TryGetValue(container, out var at) ? at : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // After start-up has repaired anything a restart of Gitberth interrupted.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Scheduled restarts failed");
                }
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether a container that started at <paramref name="startedAt"/> is due, given when it was last restarted on schedule.</summary>
    internal static bool IsDue(DateTime startedAt, TimeSpan every, DateTime? restarted, DateTime now) =>
        now - startedAt >= every && (restarted is not { } last || now - last >= every);

    /// <summary>Restarts every container that's due. Returns the ones it restarted, or queued a recreate for.</summary>
    internal async Task<List<string>> TickAsync(DateTime now, CancellationToken ct = default)
    {
        var done = new List<string>();
        var all = schedules.All;
        if (all.IsEmpty) return done;
        Dictionary<string, AppDefinition> apps;
        HashSet<int> deploying;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Only a Git app restarts through a deploy; any other app's container restarts as a container (below).
            apps = (await db.Apps.AsNoTracking().ToListAsync(ct)).Where(a => a.Deployed).ToDictionary(a => a.ContainerName, StringComparer.Ordinal);
            deploying = (await db.Deployments.AsNoTracking()
                .Where(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting)
                .Select(d => d.AppId).ToListAsync(ct)).ToHashSet();
        }

        foreach (var (container, minutes) in all.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var every = TimeSpan.FromMinutes(minutes);
            if (await docker.StartedAtAsync(container, ct) is not { } startedAt || !IsDue(startedAt, every, LastRestart(container), now)) continue;
            if (apps.TryGetValue(container, out var app))
            {
                // A deploy is replacing it already, or it's meant to be stopped: nothing to restart.
                if (deploying.Contains(app.Id) || app.DesiredState == DesiredState.Stopped) continue;
                _restarted[container] = now;
                await RestartAppAsync(app, every);
            }
            else if (!await RestartStandaloneAsync(container, minutes, now, ct))
            {
                continue;
            }
            done.Add(container);
        }
        return done;
    }

    // A container Gitberth didn't deploy. One Gitberth is changing (a port change recreates it) waits for the next tick,
    // as does either container of a port change that isn't settled: the two mount the same volumes. False when it waits.
    private async Task<bool> RestartStandaloneAsync(string container, int minutes, DateTime now, CancellationToken ct)
    {
        using var held = await _containerLocks.TryAcquireAsync(container);
        if (held is null)
        {
            logger.LogInformation("{Container} is being recreated; its scheduled restart waits.", container);
            return false;
        }
        if (changer is not null && await changer.WhyNotStartAsync(container, ct) is { } why)
        {
            logger.LogInformation("{Container}'s scheduled restart waits: {Why}", container, why);
            return false;
        }
        _restarted[container] = now;
        var r = await docker.RestartStandaloneAsync(container, ContainerService.IsDatabaseImage, ct);
        if (r.Ok) logger.LogInformation("Restarted {Container} on its schedule ({Every})", container, RestartInterval.FromMinutes(minutes));
        else logger.LogWarning("Restarting {Container} on its schedule failed: {Error}", container, r.ErrorText);
        return true;
    }

    private async Task RestartAppAsync(AppDefinition app, TimeSpan every)
    {
        var interval = RestartInterval.FromMinutes((int)every.TotalMinutes);
        // A recreate would also apply settings saved since the container started. Those wait for the user, so the
        // container restarts as it is.
        if (app.ConfigChangePending)
        {
            ProcessResult r;
            using (await locks.AcquireAsync(app.Id))
                r = await docker.RestartAsync(app.ContainerName);
            if (r.Ok) logger.LogInformation("Restarted {App} on its schedule ({Every}), as it is: its saved settings aren't applied yet", app.Slug, interval);
            else logger.LogWarning("Restarting {App} on its schedule failed: {Error}", app.Slug, r.ErrorText);
            return;
        }
        var deployment = await actions.RecreateAsync(app.Id, DeployTrigger.Restart);
        logger.LogInformation("Restarting {App} on its schedule ({Every}) as deploy {Deployment}", app.Slug, interval, deployment);
    }
}
