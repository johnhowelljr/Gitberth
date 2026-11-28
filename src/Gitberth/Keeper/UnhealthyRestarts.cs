using System.Globalization;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Keeper;

/// <summary>
/// Settings › Keep apps running's second switch, Restart apps that turn unhealthy (on unless turned off; each app can be
/// left out on its Settings tab): a running container Docker says is unhealthy is restarted, at most 3 times in an hour.
/// After that Gitberth stops trying, says so (a notification and the attention board) and leaves it as it is until its
/// health check passes again or someone restarts it. Never while a deploy, a change or a removal has it, or Gitberth holds
/// its lock; never Gitberth itself, its helpers, or a deploy's containers beside the app. Each restart goes to the app's
/// Activity, the audit log (as Gitberth, through the keep-running check) and notifications (UnhealthyRestarted).
/// </summary>
public sealed class UnhealthyRestarts(
    DockerApi docker,
    HealthRules rules,
    AppActions actions,
    ContainerLocks locks,
    IDbContextFactory<GitberthDb> dbFactory,
    Notifier notifier,
    ActivityLog activity,
    AttentionBoard board,
    ContainerTitles titles,
    GitberthOptions options,
    ILogger<UnhealthyRestarts> logger,
    AuditLog? audit = null,
    DownStates? downStates = null,
    Lazy<PortChanger>? changer = null,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>Its items on the attention board.</summary>
    public const string Source = "unhealthy";

    /// <summary>The restarts an app gets in an hour before Gitberth stops trying.</summary>
    public const int MaxPerHour = 3;

    /// <summary>How often containers are looked at (the keep-running check's pace; the end-to-end rig's is shorter).</summary>
    internal TimeSpan PassEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The window the restarts are counted in.</summary>
    internal TimeSpan Window { get; set; } = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    private sealed class Track
    {
        public string Id = "";
        public readonly List<DateTime> Restarts = [];
        public DateTime? GaveUpAt;
        // When Docker said it started, right after the last restart: another start since is someone else's.
        public string? StartedAt;
        public string Name = "";
    }

    /// <summary>A container's health as `docker ps` says it: unhealthy, healthy, starting, or no health check.</summary>
    internal enum HealthState { None, Starting, Healthy, Unhealthy }

    internal static HealthState HealthOf(string status) =>
        status.Contains("(unhealthy)", StringComparison.Ordinal) ? HealthState.Unhealthy
        : status.Contains("(healthy)", StringComparison.Ordinal) ? HealthState.Healthy
        : status.Contains("(health: starting)", StringComparison.Ordinal) ? HealthState.Starting
        : HealthState.None;

    /// <summary>The containers Gitberth stopped restarting, by name.</summary>
    public IReadOnlyList<string> GaveUp => [.. _tracks.Where(t => t.Value.GaveUpAt is not null).Select(t => t.Key).Order(StringComparer.Ordinal)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var actor = AuditScope.Begin(AuditActor.System(AuditVia.KeepRunning));
        try
        {
            using var timer = new PeriodicTimer(PassEvery, _time);
            do
            {
                try
                {
                    await PassAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Looking for unhealthy apps failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One look at every container: the unhealthy ones restarted, or given up on; the healthy ones forgiven.</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return;
        try
        {
            var changed = false;
            if (!rules.Current.RestartUnhealthy)
            {
                if (_tracks.Count > 0) board.Replace(Source, []);
                _tracks.Clear();
                return;
            }
            var now = _time.GetUtcNow().UtcDateTime;
            var listed = await docker.ListAllAsync(ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in listed)
            {
                if (Never(c)) continue;
                seen.Add(c.Name);
                var health = HealthOf(c.Status);
                _tracks.TryGetValue(c.Name, out var track);
                // Made again (a deploy, a change): what was counted was another container's.
                if (track is not null && track.Id != c.Id)
                {
                    changed |= track.GaveUpAt is not null;
                    _tracks.Remove(c.Name);
                    track = null;
                }
                if (c.State != "running" || health == HealthState.Healthy || !rules.RestartsUnhealthy(c.Name))
                {
                    // Healthy again, stopped, or left out: it starts afresh.
                    if (track is not null && (health == HealthState.Healthy || c.State != "running" || !rules.RestartsUnhealthy(c.Name)))
                    {
                        changed |= track.GaveUpAt is not null;
                        _tracks.Remove(c.Name);
                    }
                    continue;
                }
                if (health != HealthState.Unhealthy) continue;
                track ??= _tracks[c.Name] = new Track { Id = c.Id, Name = c.Name };
                if (track.GaveUpAt is not null)
                {
                    // Someone restarted it since: Gitberth tries again.
                    if (await StartedAtAsync(c.Id, ct) is { } started && started != track.StartedAt)
                    {
                        track.GaveUpAt = null;
                        track.Restarts.Clear();
                        changed = true;
                    }
                    else continue;
                }
                track.Restarts.RemoveAll(at => now - at >= Window);
                if (await BusyAsync(c.Name, ct) is { } busy)
                {
                    logger.LogDebug("{Container} is unhealthy, but waits: {Why}", c.Name, busy);
                    continue;
                }
                if (track.Restarts.Count >= MaxPerHour)
                {
                    await GiveUpAsync(c, track, now, ct);
                    changed = true;
                    continue;
                }
                await RestartAsync(c, track, now, ct);
            }
            foreach (var gone in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                changed |= _tracks[gone].GaveUpAt is not null;
                _tracks.Remove(gone);
            }
            if (changed) board.Replace(Source, await ItemsAsync(ct));
        }
        finally
        {
            _one.Release();
        }
    }

    // Gitberth itself, its helpers, and the containers a deploy starts beside an app are never restarted.
    private bool Never(RunningContainer c) =>
        HostScanHelper.IsHelper(c.Labels)
        || options.OwnContainer is { Length: > 0 } self && (c.Id.StartsWith(self, StringComparison.Ordinal) || c.Name == self)
        || c.Name.EndsWith(DeployPipeline.BridgeSuffix, StringComparison.Ordinal)
        || c.Name.EndsWith(DeployPipeline.PreviousSuffix, StringComparison.Ordinal);

    /// <summary>What has the container now (a deploy, a change, a removal, its lock), in words; null when nothing does.</summary>
    internal async Task<string?> BusyAsync(string container, CancellationToken ct)
    {
        if (locks.IsHeld(container)) return "Gitberth holds it for a change";
        if (changer is not null && await changer.Value.WhyNotStartAsync(container, ct) is { } change) return change;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
        if (await db.Apps.AsNoTracking().Where(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug))
                .Select(a => (int?)a.Id).FirstOrDefaultAsync(ct) is not int appId) return null;
        if (actions.IsDeleting(appId)) return "it's being deleted";
        if (downStates?.IsUpdating(appId) == true) return "a deploy is replacing its container";
        return await db.Deployments.AnyAsync(d => d.AppId == appId && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning
                || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting), ct)
            ? "a deploy of it is queued or running"
            : null;
    }

    private async Task RestartAsync(RunningContainer c, Track track, DateTime now, CancellationToken ct)
    {
        track.Restarts.Add(now);
        var n = track.Restarts.Count;
        var error = await actions.RestartAsync(c.Name, ct);
        track.StartedAt = await StartedAtAsync(c.Id, ct);
        var (name, appId) = await AppOfAsync(c.Name, ct);
        var count = $"{n.ToString(CultureInfo.InvariantCulture)} of at most {MaxPerHour.ToString(CultureInfo.InvariantCulture)} in an hour";
        await NoteAsync(c.Name, appId, ActivityKind.Restarted, error is null
            ? $"Restarted by Gitberth: Docker said it was unhealthy ({count})."
            : $"Gitberth tried to restart it because Docker said it was unhealthy, and it didn't work: {error.TrimEnd('.')}.", error is null ? ActivityTone.Warning : ActivityTone.Danger, ct);
        notifier.Publish(new Notice(NotifyEvent.UnhealthyRestarted, NoticeLevel.Warning,
            error is null ? $"{name} was unhealthy, so Gitberth restarted it" : $"Gitberth couldn't restart {name}",
            error is null
                ? $"Docker said its health check kept failing. That's restart {count}; after that Gitberth stops trying and tells you."
                : $"Docker said its health check kept failing, and the restart didn't work: {error.TrimEnd('.')}. Gitberth tries again in a moment.",
            await notifier.LinkAsync($"apps/{Uri.EscapeDataString(c.Name)}/logs", ct), name, c.Name, now) { AppId = appId });
        logger.LogInformation("Restarted {Container}: Docker said it was unhealthy ({Count}){Error}", c.Name, count, error is null ? "" : $"; it failed: {error}");
    }

    private async Task GiveUpAsync(RunningContainer c, Track track, DateTime now, CancellationToken ct)
    {
        track.GaveUpAt = now;
        track.StartedAt = await StartedAtAsync(c.Id, ct) ?? track.StartedAt;
        var (name, appId) = await AppOfAsync(c.Name, ct);
        await NoteAsync(c.Name, appId, ActivityKind.GaveUp, GaveUpText, ActivityTone.Danger, ct);
        audit?.Record(new AuditEntry("app.unhealthy.stopped", $"Stopped restarting {name}: it turned unhealthy again after {MaxPerHour} restarts in an hour.")
        {
            AppId = appId, AppName = name, Container = c.Name, Outcome = AuditOutcome.Failed,
            Changes = [new("Restarts in an hour", null, MaxPerHour.ToString(CultureInfo.InvariantCulture))],
        });
        notifier.Publish(new Notice(NotifyEvent.UnhealthyRestarted, NoticeLevel.Critical, $"Gitberth stopped restarting {name}", GaveUpText,
            await notifier.LinkAsync($"apps/{Uri.EscapeDataString(c.Name)}/logs", ct), name, c.Name, now) { AppId = appId });
        logger.LogWarning("Stopped restarting {Container}: it turned unhealthy again after {Max} restarts in an hour", c.Name, MaxPerHour);
    }

    internal const string GaveUpText =
        "It turned unhealthy again after 3 restarts in an hour, so Gitberth stopped restarting it. It runs as it is until its health check passes again or someone restarts it.";

    private async Task<List<AttentionItem>> ItemsAsync(CancellationToken ct)
    {
        var items = new List<AttentionItem>();
        foreach (var t in _tracks.Values.Where(t => t.GaveUpAt is not null).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var (name, _) = await AppOfAsync(t.Name, ct);
            var href = $"apps/{Uri.EscapeDataString(t.Name)}";
            items.Add(new AttentionItem($"unhealthy:{t.Name}", Source, AttentionLevel.Critical, $"{name} stays unhealthy", GaveUpText, href, Tip: "dashboard.unhealthy-gave-up", Actions:
                [new AttentionAction($"Open {name}", href), new AttentionAction("Logs", $"{href}/logs")])
            {
                Problem = $"{name} ({t.Name}) stays unhealthy: Gitberth restarted it {MaxPerHour} times in an hour and stopped trying.",
            });
        }
        return items;
    }

    private async Task<string?> StartedAtAsync(string id, CancellationToken ct)
    {
        try
        {
            return (await docker.InspectContainerAsync(id, ct))?["State"]?["StartedAt"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't read when {Container} started", id);
            return null;
        }
    }

    private async Task NoteAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
    {
        try
        {
            await activity.AddAsync(container, appId, kind, text, tone, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't write to the Activity of {Container}: {Text}", container, text);
        }
    }

    private async Task<(string Name, int? AppId)> AppOfAsync(string container, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
            if (await db.Apps.AsNoTracking().Where(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug))
                    .Select(a => new { a.Id, a.Name }).FirstOrDefaultAsync(ct) is { } row)
                return (row.Name, row.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't find {Container}'s app", container);
        }
        return (titles.For(container) ?? container, null);
    }
}
