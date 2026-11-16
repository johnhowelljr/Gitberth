using System.Threading.Channels;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Health;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

/// <summary>
/// Decides which apps' domains show Gitberth's down page instead of Cloudflare's error, and asks for a domain sync when
/// that changes. An app with its down page on (the default) shows it once it's stopped (at once), and once its container
/// hasn't been running for <see cref="Grace"/> (a crash, a crash loop, a stop outside Gitberth; "restarting" counts as not
/// running). It goes back to the app as soon as the container runs again. It follows Docker's container events and looks at
/// every app every <see cref="Sweep"/>. A sync costs Cloudflare calls, so changes are coalesced into one sync, and an app
/// that keeps going down and up (<see cref="FlapChanges"/> changes within <see cref="FlapWindow"/>) only goes back once it
/// has run steadily for <see cref="SteadyFor"/>.
/// </summary>
public sealed class DownWatcher(DownStates states, DomainService domains, DockerCli docker, ContainerEventWatcher events,
    IDbContextFactory<GitberthDb> dbFactory, ILogger<DownWatcher> logger, TimeProvider? time = null) : BackgroundService
{
    /// <summary>How long a container may be down before its domains show the down page: a restart takes less.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    /// <summary>How often every app is looked at, events or not.</summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromSeconds(15);

    /// <summary>How many changes within <see cref="FlapWindow"/> make an app one that keeps going down and up.</summary>
    public const int FlapChanges = 4;

    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long an app that keeps going down and up has to run before its domains go back to it.</summary>
    public static readonly TimeSpan SteadyFor = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<int, Track> _tracks = [];
    private readonly SemaphoreSlim _evaluating = new(1, 1);
    // A look is due: an event, a nudge, or a container's grace running out.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    // A sync is due; several changes in a row make one.
    private readonly Channel<bool> _sync = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private DateTime? _nextDue;

    /// <summary>How long a sync waits for more changes to come with it.</summary>
    internal TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Raised after each sync this watcher asked for, for tests.</summary>
    internal event Action<SyncReport>? Synced;

    /// <summary>What the watcher remembers of an app between looks.</summary>
    internal sealed class Track
    {
        public DateTime? NotRunningSince;
        public DateTime? RunningSince;
        public List<DateTime> Changes = [];
    }

    /// <summary>An app as the watcher sees it.</summary>
    /// <param name="Running">Its container runs now; false when it's stopped, restarting or missing.</param>
    internal sealed record Facts(bool DownPage, bool HasDomains, bool HasWebPort, DesiredState Desired, bool Running);

    /// <summary>
    /// What an app's domains should show now, given what they show (<paramref name="current"/>) and the app's track: the
    /// reason for its down page, or null for the app itself. Updates the track's running and not-running times.
    /// </summary>
    internal static DownReason? Decide(Facts app, DownReason? current, Track track, DateTime now)
    {
        if (app.Running)
        {
            track.NotRunningSince = null;
            track.RunningSince ??= now;
        }
        else
        {
            track.RunningSince = null;
            track.NotRunningSince ??= now;
        }
        if (!app.DownPage || !app.HasDomains || !app.HasWebPort) return null;
        if (app.Desired == DesiredState.Stopped) return DownReason.Stopped;
        if (app.Running)
        {
            if (current is null) return null;
            // Back at once, unless it keeps going down and up: then once it has run steadily for a while.
            return Flapping(track, now) && now - track.RunningSince < SteadyFor ? current : null;
        }
        // Already on the down page (started again after a stop, but not running yet; or down already): it stays there.
        if (current is not null) return DownReason.NotRunning;
        return now - track.NotRunningSince >= Grace ? DownReason.NotRunning : null;
    }

    private static bool Flapping(Track track, DateTime now) => track.Changes.Count(c => now - c < FlapWindow) >= FlapChanges;

    /// <summary>When a container that isn't running gets the down page, if it still isn't running; null when none is waiting.</summary>
    internal static DateTime? DueAt(Facts app, DownReason? decided, Track track) =>
        decided is null && app.DownPage && app.HasDomains && app.HasWebPort && !app.Running && track.NotRunningSince is { } since ? since + Grace
        : decided is not null && app.Running && track.RunningSince is { } runs ? runs + SteadyFor
        : null;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        events.Happened += OnEvent;
        states.Nudged += OnNudged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        events.Happened -= OnEvent;
        states.Nudged -= OnNudged;
        return base.StopAsync(cancellationToken);
    }

    private void OnEvent(ContainerEvent e)
    {
        if (e.Action is "start" or "die" or "stop" or "kill" or "restart" or "oom" or "destroy" or "pause" or "unpause") _wake.Writer.TryWrite(true);
    }

    private void OnNudged(int appId) => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var syncing = SyncLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't work out which apps show the down page");
                }
                var now = _time.GetUtcNow().UtcDateTime;
                var wait = _nextDue is { } due && due - now < Sweep ? due - now + TimeSpan.FromMilliseconds(200) : Sweep;
                using var sweep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                sweep.CancelAfter(wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
                try
                {
                    if (await _wake.Reader.WaitToReadAsync(sweep.Token)) _wake.Reader.TryRead(out _);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        _sync.Writer.TryComplete();
        await syncing;
    }

    /// <summary>Looks at every app with domains once, and asks for a sync when what any of them should show changed.</summary>
    internal async Task EvaluateAsync(CancellationToken ct)
    {
        await _evaluating.WaitAsync(ct);
        try
        {
            if (!await domains.EnabledAsync(ct))
            {
                // Domains off: Gitberth leaves the tunnel alone. Turned on again, it works the states out afresh.
                states.Clear();
                _tracks.Clear();
                states.MarkReady();
                _nextDue = null;
                return;
            }
            List<(int Id, string Container, DesiredState Desired, bool DownPage, bool HasWeb)> apps;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                apps = (await db.Apps.AsNoTracking().Where(a => a.Domains.Any())
                        .Select(a => new { a.Id, a.Slug, a.Container, a.DesiredState, a.DownPage, HasWeb = a.Ports.Any(p => p.IsWeb && p.Protocol == "tcp") })
                        .ToListAsync(ct))
                    .Select(a => (a.Id, a.Container ?? AppDefinition.ContainerNameFor(a.Slug), a.DesiredState, a.DownPage, a.HasWeb)).ToList();
            var (containers, error) = await docker.ListContainersAsync(managedOnly: false, ct);
            // Docker can't be read: nothing is known, so nothing changes.
            if (error is not null) return;
            var running = containers.Where(c => c.State == "running").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var now = _time.GetUtcNow().UtcDateTime;
            var changed = !states.Ready && apps.Count > 0;
            DateTime? nextDue = null;
            var watched = states.Watched;
            foreach (var app in apps)
            {
                if (!_tracks.TryGetValue(app.Id, out var track)) _tracks[app.Id] = track = new Track();
                var facts = new Facts(app.DownPage, HasDomains: true, app.HasWeb, app.Desired, running.Contains(app.Container));
                var current = watched.TryGetValue(app.Id, out var was) ? was : (DownReason?)null;
                var decided = Decide(facts, current, track, now);
                if (decided != current)
                {
                    track.Changes.Add(now);
                    track.Changes.RemoveAll(c => now - c >= FlapWindow);
                    if ((decided is null) != (current is null))
                        logger.LogInformation("The domains of {Container} {What}", app.Container, decided is null ? "go back to it" : $"show the down page ({decided})");
                }
                if (states.Set(app.Id, decided) && (decided is null) != (current is null)) changed = true;
                if (DueAt(facts, decided, track) is { } due && (nextDue is null || due < nextDue)) nextDue = due;
            }
            // Apps that went (deleted, or their last domain removed): nothing of theirs is shown any more.
            foreach (var gone in _tracks.Keys.Except(apps.Select(a => a.Id)).ToList())
            {
                _tracks.Remove(gone);
                states.Set(gone, null);
            }
            foreach (var gone in states.Watched.Keys.Except(apps.Select(a => a.Id)).ToList()) states.Set(gone, null);
            _nextDue = nextDue;
            states.MarkReady();
            if (changed) _sync.Writer.TryWrite(true);
        }
        finally
        {
            _evaluating.Release();
        }
    }

    // One sync at a time, for any number of changes that came in meanwhile.
    private async Task SyncLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _sync.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(Settle, _time, stoppingToken);
                _sync.Reader.TryRead(out _);
                try
                {
                    if (!await domains.EnabledAsync(stoppingToken) || !await domains.TokenSavedAsync(stoppingToken)) continue;
                    var report = await domains.SyncAsync(stoppingToken);
                    foreach (var note in report.Notes.Where(n => n.Problem)) logger.LogInformation("Down page sync: {Note}", note.Text);
                    Synced?.Invoke(report);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't route domains to or from the down page");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }
}
