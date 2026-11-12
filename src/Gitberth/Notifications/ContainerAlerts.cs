using System.Threading.Channels;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Health;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Notifications;

/// <summary>
/// Turns Docker's events into notices: a container that crashed or ran out of memory, and one whose health check started
/// failing (and passes again). A crash loop says so once, then how many more times every 30 minutes while it goes on, so
/// a container restarting every few seconds doesn't flood anyone. Left out: events from before Gitberth started (a host
/// that rebooted says nothing of the sort), Gitberth's own container, the containers a deploy starts next to the running
/// one, and a new version's crash while its deploy checks it (the deploy's own notice says it).
/// </summary>
public sealed class ContainerAlerts(
    ContainerEventWatcher watcher,
    Notifier notifier,
    IDbContextFactory<GitberthDb> dbFactory,
    GitberthOptions options,
    ILogger<ContainerAlerts> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>How long after a notice about a container the next one waits; what happened meanwhile is counted.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>How long after a die Gitberth looks at what came around it: a stop just after, a start again.</summary>
    internal TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Channel<ContainerEvent> _events = Channel.CreateUnbounded<ContainerEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    /// <summary>When it started listening: older events (replayed after a restart of Gitberth or the host) aren't told.</summary>
    internal DateTime Since { get; set; }

    private sealed class State
    {
        public DateTime? CrashSent;
        public int MoreCrashes;
        public bool Unhealthy;
        public DateTime? UnhealthySent;
        public bool UnhealthyTold;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Since = _time.GetUtcNow().UtcDateTime;
        watcher.Happened += OnEvent;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        watcher.Happened -= OnEvent;
        return base.StopAsync(cancellationToken);
    }

    private void OnEvent(ContainerEvent e)
    {
        if (e.Action == "die" || e.Action.StartsWith("health_status", StringComparison.Ordinal)) _events.Writer.TryWrite(e);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        var summaries = SummariesAsync(timer, stoppingToken);
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (e.Action == "die") _ = DiedAsync(e, stoppingToken);
                    else await HealthAsync(e, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't tell about an event of {Container}", e.Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await summaries;
    }

    /// <summary>Whether an event is one to tell about at all.</summary>
    internal bool Relevant(ContainerEvent e) =>
        e.Time >= Since
        && e.Name != options.OwnContainer
        && !e.Name.EndsWith(DeployPipeline.BridgeSuffix, StringComparison.Ordinal)
        && !e.Name.EndsWith(DeployPipeline.PreviousSuffix, StringComparison.Ordinal);

    /// <summary>A die, once what came right after it is known: a crash is told, unless a deploy is still checking the container.</summary>
    internal async Task DiedAsync(ContainerEvent die, CancellationToken ct)
    {
        try
        {
            if (!Relevant(die)) return;
            await Task.Delay(Settle, _time, ct);
            var events = watcher.Events;
            var at = IndexOf(events, die);
            if (at < 0) return;
            var (crashed, outOfMemory) = EventMarks.CrashOf(events, at);
            if (!crashed || await DeployIsCheckingAsync(die, ct)) return;
            var back = events.Skip(at + 1).Any(x => x.Name == die.Name && x.Action == "start");
            var now = _time.GetUtcNow().UtcDateTime;
            lock (_lock)
            {
                var state = StateOf(die.Name);
                if (state.CrashSent is { } sent && now - sent < Window)
                {
                    state.MoreCrashes++;
                    return;
                }
                state.CrashSent = now;
                state.MoreCrashes = 0;
            }
            var (app, appId) = await AppOfAsync(die.Name, ct);
            var title = outOfMemory ? $"{app} ran out of memory" : $"{app} crashed";
            var how = outOfMemory ? "It used all the memory it may" : $"Its process exited with code {die.ExitCode}";
            var text = $"{how}{(back ? " and it restarted itself." : ", and it's stopped now.")} Its log on Gitberth has the details.";
            notifier.Publish(new Notice(NotifyEvent.Crashed, back ? NoticeLevel.Warning : NoticeLevel.Critical, title, text,
                await notifier.LinkAsync($"apps/{Uri.EscapeDataString(die.Name)}/logs", ct), app, die.Name, die.Time) { AppId = appId });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't tell about {Container} stopping", die.Name);
        }
    }

    /// <summary>A health check that starts failing is told once a window; passing again is told when its failing was.</summary>
    internal async Task HealthAsync(ContainerEvent e, CancellationToken ct)
    {
        if (!Relevant(e)) return;
        var unhealthy = e.Action.EndsWith("unhealthy", StringComparison.Ordinal);
        var healthy = !unhealthy && e.Action.EndsWith("healthy", StringComparison.Ordinal);
        if (!unhealthy && !healthy) return;
        var now = _time.GetUtcNow().UtcDateTime;
        NotifyEvent? tell = null;
        lock (_lock)
        {
            var state = StateOf(e.Name);
            if (unhealthy && !state.Unhealthy)
            {
                state.Unhealthy = true;
                if (state.UnhealthySent is not { } sent || now - sent >= Window)
                {
                    state.UnhealthySent = now;
                    state.UnhealthyTold = true;
                    tell = NotifyEvent.Unhealthy;
                }
            }
            else if (healthy && state.Unhealthy)
            {
                state.Unhealthy = false;
                if (state.UnhealthyTold) tell = NotifyEvent.Healthy;
                state.UnhealthyTold = false;
            }
        }
        if (tell is not { } kind) return;
        var (app, appId) = await AppOfAsync(e.Name, ct);
        var link = await notifier.LinkAsync($"apps/{Uri.EscapeDataString(e.Name)}", ct);
        notifier.Publish(kind == NotifyEvent.Unhealthy
            ? new Notice(kind, NoticeLevel.Warning, $"{app}'s health check is failing", "Docker says it's unhealthy: its health check failed several times in a row. It still runs.", link, app, e.Name, e.Time) { AppId = appId }
            : new Notice(kind, NoticeLevel.Good, $"{app}'s health check passes again", "Docker says it's healthy again.", link, app, e.Name, e.Time) { AppId = appId });
    }

    /// <summary>Every minute: a container that crashed again since its notice, and whose window is over, gets a count.</summary>
    private async Task SummariesAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await SummarizeAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't tell about repeated crashes");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task SummarizeAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<(string Name, int Count)> due = [];
        lock (_lock)
        {
            foreach (var (name, state) in _states)
            {
                if (state.MoreCrashes == 0 || state.CrashSent is not { } sent || now - sent < Window) continue;
                due.Add((name, state.MoreCrashes));
                state.CrashSent = now;
                state.MoreCrashes = 0;
            }
        }
        foreach (var (name, count) in due)
        {
            var events = watcher.Events;
            var running = events.LastOrDefault(x => x.Name == name && x.Action is "start" or "die") is { Action: "start" };
            var (app, appId) = await AppOfAsync(name, ct);
            var times = count == 1 ? "once more" : $"{count} more times";
            notifier.Publish(new Notice(NotifyEvent.Crashed, NoticeLevel.Critical, $"{app} keeps crashing",
                $"It crashed {times} in the last {Window.TotalMinutes:0} minutes{(running ? ", and keeps restarting." : ", and it's stopped now.")} Its log on Gitberth has the details.",
                await notifier.LinkAsync($"apps/{Uri.EscapeDataString(name)}/logs", ct), app, name, now) { AppId = appId });
        }
    }

    private State StateOf(string name)
    {
        if (!_states.TryGetValue(name, out var state)) _states[name] = state = new State();
        return state;
    }

    private static int IndexOf(IReadOnlyList<ContainerEvent> events, ContainerEvent e)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i].Time == e.Time && events[i].Id == e.Id && events[i].Action == e.Action) return i;
        return -1;
    }

    /// <summary>A container a deploy started and still checks (or that failed its checks): that deploy tells how it went.</summary>
    private async Task<bool> DeployIsCheckingAsync(ContainerEvent die, CancellationToken ct)
    {
        if (die.Deployment is not int id) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var status = await db.Deployments.AsNoTracking().Where(d => d.Id == id).Select(d => (DeployStatus?)d.Status).FirstOrDefaultAsync(ct);
        return status is DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting or DeployStatus.Failed or DeployStatus.RolledBack
            // Its deploy finished just now: the swap's own checks were still watching it.
            || status == DeployStatus.Succeeded && await db.Deployments.AsNoTracking().AnyAsync(d => d.Id == id && d.FinishedAt > die.Time, ct);
    }

    /// <summary>The app's name as Gitberth shows it, and its id when it has a row; the container's name otherwise.</summary>
    private async Task<(string Name, int? Id)> AppOfAsync(string container, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apps = await db.Apps.AsNoTracking().Select(a => new { a.Id, a.Name, a.Slug, a.Container }).ToListAsync(ct);
        var app = apps.FirstOrDefault(a => (a.Container ?? AppDefinition.ContainerNameFor(a.Slug)) == container);
        return app is null ? (container, null) : (app.Name, app.Id);
    }
}
