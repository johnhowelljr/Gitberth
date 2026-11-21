using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Keeper;

/// <summary>What the keep-running check says of an app now.</summary>
public enum KeepState
{
    None,
    /// <summary>It's down for a reason to bring it back: a try is coming.</summary>
    Soon,
    /// <summary>Gitberth tried as often as it does, and only tries every 6 hours now.</summary>
    GaveUp,
    /// <summary>It stopped without a crash (a stop, or its program ended), or someone cancelled the deploy that was to bring it back: left as it is.</summary>
    Left,
    /// <summary>Settings › Keep apps running is off.</summary>
    Off,
}

/// <summary>What the keep-running check says of an app now, for its page, Settings and the dashboard.</summary>
/// <param name="NextTry">The try that comes next (Soon), 1 to 4.</param>
/// <param name="At">When: the next try (Soon), the next quiet try (GaveUp).</param>
/// <param name="Tries">How many tries were made (GaveUp).</param>
/// <param name="Over">From the first try to giving up (GaveUp).</param>
public sealed record KeepStatus(KeepState State, KeepReason? Reason = null, int NextTry = 0, DateTime? At = null, int Tries = 0, TimeSpan Over = default,
    DateTime? GaveUpAt = null)
{
    public static readonly KeepStatus Nothing = new(KeepState.None);
}

/// <summary>An app Gitberth stopped bringing back, for Settings › Keep apps running.</summary>
public sealed record KeepGaveUp(int AppId, string Key, string Name, KeepReason Reason, int Tries, TimeSpan Over, DateTime At, DateTime? Next);

/// <summary>What one pass did, for the tests.</summary>
internal sealed class KeepPass
{
    public List<string> Did { get; } = [];
}

/// <summary>
/// The keep-running check: every 30 seconds it looks at every app that's meant to run, and starts again one that stopped
/// by itself (a crash its restart policy leaves, a start Docker couldn't make, running out of memory, a stop when Docker or
/// the host restarted), makes again one whose container is gone, and leaves alone one that someone stopped. Up to 4 tries
/// in a row (at once, then 1, 5 and 15 minutes after the one before); 10 minutes up clears them. Then it says it gave up
/// and tries only every 6 hours, quietly, until someone starts, stops or deploys the app. Never while a deploy, a change or
/// a deletion has the app, never when Docker doesn't answer, never Gitberth itself. What it does goes to the app's
/// Activity, the audit log (as Gitberth through the keep-running check), notifications, and the attention board. What it
/// counts and what it left are kept across restarts of Gitberth, and a try counts even when writing about it fails.
/// </summary>
/// <param name="startup">Start-up's own recovery, which goes first (its missing containers, its cut-short changes); none in tests.</param>
/// <param name="changer">Says whether a change of a container isn't settled; none in tests.</param>
/// <param name="expected">
/// What Gitberth itself is changing in Docker: a death it hears is its own stop (a deploy's swap) or someone else's. None in
/// older tests.
/// </param>
public sealed class AppKeeper(
    IDbContextFactory<GitberthDb> dbFactory,
    IContainerEngine engine,
    AppActions actions,
    ContainerLocks containerLocks,
    ContainerEventWatcher events,
    Notifier notifier,
    ActivityLog activity,
    SettingsService settings,
    KeeperSettings keepSettings,
    AttentionBoard board,
    GitberthOptions options,
    ILogger<AppKeeper> logger,
    AuditLog? audit = null,
    DownStates? downStates = null,
    Lazy<PortChanger>? changer = null,
    StartupTasks? startup = null,
    TimeProvider? time = null,
    ExpectedChanges? expected = null) : BackgroundService
{
    /// <summary>Its items on the attention board.</summary>
    public const string Source = "keep-running";

    /// <summary>How often apps are looked at.</summary>
    internal TimeSpan PassEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a container is down (or missing, on two looks) before it's brought back.</summary>
    internal TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The first look after Gitberth starts, once start-up's own recovery is done.</summary>
    internal TimeSpan FirstPassAfter { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    // A start the quiet try made without a time read for it (a create's), give or take the clocks.
    private static readonly TimeSpan OwnStartSlack = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Deaths heard from here on are judged as they happen; Gitberth's own changes before it started aren't known.
    private readonly DateTime _since = expected?.Now() ?? DateTime.MinValue;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<int, Track> _tracks = [];
    private readonly Dictionary<string, DateTime> _missingSince = new(StringComparer.Ordinal);
    // What was said once already in an app's Activity ("left:<id>:<when>"), kept across restarts with the tracks.
    private readonly List<string> _noted = [];
    // A stop already read as a stop, by container ID: not inspected again while nothing happens to it (any event of the
    // container's, or seeing it run, forgets it).
    private readonly Dictionary<string, Look> _read = new(StringComparer.Ordinal);
    // The stops it left, by container ID and when each stopped, kept across restarts: a stop read as someone's stays theirs
    // once its events are gone and only its exit code is left to read (a docker stop's can look like a crash's).
    private readonly Dictionary<string, LeftStop> _left = new(StringComparer.Ordinal);
    // Deaths heard, judged as they happened: whether Gitberth itself was stopping the container then (a deploy's swap).
    private readonly Dictionary<(string Id, DateTime At), bool> _byGitberth = [];
    private readonly Lock _readGate = new();
    private ImmutableDictionary<int, KeepStatus> _status = ImmutableDictionary<int, KeepStatus>.Empty;
    private ImmutableList<KeepGaveUp> _gaveUp = [];
    private string? _saved;
    private bool _loaded, _listening, _dockerDown, _on = true;
    private Own? _own;

    /// <summary>Raised after what it says of an app changed, for open pages.</summary>
    public event Action? Changed;

    /// <summary>Whether Settings › Keep apps running is on, as the last pass found it.</summary>
    public bool On => _on;

    /// <summary>What it says of the app now.</summary>
    public KeepStatus StatusOf(int appId) => !_on ? new KeepStatus(KeepState.Off) : _status.GetValueOrDefault(appId) ?? KeepStatus.Nothing;

    /// <summary>The apps it stopped bringing back, longest ago first.</summary>
    public IReadOnlyList<KeepGaveUp> GaveUp => _gaveUp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every container event from now on, the ones while start-up's own recovery runs included.
        Listen();
        try
        {
            // Start-up's own recovery first: its missing containers, and the changes a restart cut short.
            if (startup is not null)
            {
                await Quietly(startup.ExecuteTask, stoppingToken);
                await Quietly(startup.Settled, stoppingToken);
            }
            await Task.Delay(FirstPassAfter, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PassAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The keep-running check's look at the apps failed");
                }
                await Task.Delay(PassEvery, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_listening) events.Happened -= Heard;
        }
    }

    private static async Task Quietly(Task? task, CancellationToken ct)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
        }
    }

    private void Listen()
    {
        lock (_readGate)
        {
            if (_listening) return;
            _listening = true;
        }
        events.Happened += Heard;
    }

    // Something happened to a container: what was read of its stop is read again. A death is judged as it's heard, while
    // Gitberth still knows what it was doing itself: its own stop (a deploy's swap) or someone else's.
    private void Heard(ContainerEvent e)
    {
        var byGitberth = e.Action == "die" && expected is not null && e.Time >= _since
            ? expected.MatchesByName(new DockerChange(e.Time, "container", "die", e.Id, e.Name, NoAttributes))
            : (bool?)null;
        lock (_readGate)
        {
            _read.Remove(e.Id);
            if (byGitberth is { } ours) _byGitberth[(e.Id, e.Time)] = ours;
        }
    }

    /// <summary>One look at every app that's meant to run, as at <paramref name="now"/>; one at a time.</summary>
    internal async Task<KeepPass> PassAsync(DateTime now, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            Listen();
            // What it changes is Gitberth's, through this job, in the audit log; its deploys are told as such too.
            using var actor = AuditScope.Begin(AuditActor.System(AuditVia.KeepRunning));
            return await PassCoreAsync(now, ct);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<KeepPass> PassCoreAsync(DateTime now, CancellationToken ct)
    {
        var pass = new KeepPass();
        if (!_loaded)
        {
            await LoadAsync(ct);
            _loaded = true;
        }
        if (!await keepSettings.OnAsync(ct))
        {
            if (_on)
            {
                _on = false;
                board.Replace(Source, []);
                Changed?.Invoke();
            }
            return pass;
        }
        var wasOn = _on;
        _on = true;

        var world = await ReadAsync(ct);
        List<Box> boxes;
        Own own;
        try
        {
            boxes = Boxes(await engine.ListContainersAsync(ct));
            own = await OwnAsync(boxes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Docker not answering isn't a container being gone: nothing is decided until it answers.
            if (!_dockerDown) logger.LogInformation("The keep-running check waits for Docker, which doesn't answer: {Reason}", ex.Message);
            _dockerDown = true;
            return pass;
        }
        if (_dockerDown) logger.LogInformation("Docker answers again: the keep-running check looks at the apps again");
        _dockerDown = false;
        var ids = boxes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        lock (_readGate)
        {
            foreach (var gone in _read.Keys.Where(id => !ids.Contains(id)).ToList()) _read.Remove(gone);
            foreach (var old in _byGitberth.Keys.Where(k => k.At < now - ContainerEventWatcher.Kept).ToList()) _byGitberth.Remove(old);
        }
        foreach (var gone in _left.Keys.Where(id => !ids.Contains(id)).ToList()) _left.Remove(gone);

        var statuses = ImmutableDictionary.CreateBuilder<int, KeepStatus>();
        var told = new List<(AppDefinition App, Back What)>();
        foreach (var app in world.Apps)
        {
            ct.ThrowIfCancellationRequested();
            if (own.Name is { } me && app.ContainerName == me) continue;
            if (app.DesiredState != DesiredState.Running)
            {
                // Stopped in Gitberth: whatever went before is over, giving up included.
                _tracks.Remove(app.Id);
                continue;
            }
            var kind = KindOf(app);
            if (kind is KeepKind.Git or KeepKind.Compose && app.CurrentImage is null) continue;
            // Kept before anything is done: a try counts even when writing about it fails afterwards.
            if (!_tracks.TryGetValue(app.Id, out var track)) _tracks[app.Id] = track = new Track();
            KeepStatus? status;
            try
            {
                status = await LookAtAsync(new Seen(app, kind, track, world, boxes, own, now), pass, told, ct);
            }
            catch (EngineException ex)
            {
                // A container Docker didn't answer for isn't gone either: the pass stops here.
                logger.LogInformation("The keep-running check stopped its look: Docker didn't answer for {Container} ({Reason})", app.ContainerName, ex.Message);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One app's trouble (the database refusing a write, say) is its own: the apps after it are still looked at.
                logger.LogWarning(ex, "The keep-running check's look at {Container} failed", app.ContainerName);
                status = _status.GetValueOrDefault(app.Id);
            }
            finally
            {
                if (track.IsEmpty) _tracks.Remove(app.Id);
            }
            if (status is { State: not KeepState.None }) statuses[app.Id] = status;
        }
        foreach (var gone in _tracks.Keys.Where(id => !world.ById.ContainsKey(id)).ToList()) _tracks.Remove(gone);

        await BestEffortAsync(() => TellAsync(told, ct), "tell which apps it brought back");
        await BestEffortAsync(() => SaveAsync(ct), "keep what it counted for after a restart");
        Publish(world, statuses.ToImmutable(), now, changed: !wasOn);
        logger.LogDebug("The keep-running check looked at {Apps} apps: {Did}", world.Apps.Count, pass.Did.Count == 0 ? "nothing to do" : string.Join("; ", pass.Did));
        return pass;
    }

    // What follows a pass's looks, as well as it goes: what it did stays done, and the next pass tries again.
    private async Task BestEffortAsync(Func<Task> step, string what)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The keep-running check couldn't {What}", what);
        }
    }

    // ---- One app ----

    private sealed record Seen(AppDefinition App, KeepKind Kind, Track Track, World World, List<Box> Boxes, Own Own, DateTime Now);

    /// <summary>What its containers say: the verdict, the container it's about (its own), and a Compose app's services to start.</summary>
    /// <param name="Restarts">Docker's restarts of it since the last start someone asked for (its RestartCount).</param>
    private sealed record Look(KeepVerdict Verdict, string? ContainerId = null, DateTime? StartedAt = null, DateTime? FinishedAt = null,
        IReadOnlyList<string>? Services = null, bool Own = true, int Restarts = 0);

    private async Task<KeepStatus?> LookAtAsync(Seen s, KeepPass pass, List<(AppDefinition App, Back What)> told, CancellationToken ct)
    {
        var (app, _, track, world, _, _, now) = s;
        // A deploy this check queued, under way: it has the app.
        if (track.Deployment is int queued && !track.DeploymentEnded && world.Ours.GetValueOrDefault(queued) is { IsActive: true })
            return Soon(track, track.Why, now);
        // Something else has it (another deploy, start-up's own, a change, a deletion): waiting for it isn't a try, and
        // what this check's own deploy came to is read once nothing has the app.
        if (Busy(s) is { } busy)
        {
            logger.LogDebug("The keep-running check leaves {Container} for now: {Why}", app.ContainerName, busy);
            return track.GaveUpAt is not null ? GaveUpStatus(track) : _status.GetValueOrDefault(app.Id);
        }
        if (track.Deployment is int id && !track.DeploymentEnded && await DeployEndedAsync(s, world.Ours.GetValueOrDefault(id), pass, ct) is { } ended)
            return ended;

        var look = await LookAsync(s, ct);
        // Someone started it (Start, docker start) or deployed it since Gitberth gave up or left it: that's theirs, and a stop
        // later counts from 0, even when their deploy didn't start it.
        if ((track.GaveUpAt ?? track.LeftAt) is { } since
            && (world.DeployedSince.Contains(app.Id) || look.StartedAt is { } started && started > since && !Ours(track, started, look.Restarts, world)))
        {
            track.Clear();
            board.Clear(ItemKey(app.Id));
        }
        // Its deploy was cancelled: nothing is tried until someone starts, stops or deploys it.
        if (track.LeftAt is not null) return new KeepStatus(KeepState.Left, track.Why);

        switch (look.Verdict.Action)
        {
            case KeepAction.None:
                return await RunningAsync(s, look, pass, told, ct);
            case KeepAction.Leave:
                if (look.Own && look.Verdict.Reason is { } left && look.ContainerId is { } containerId)
                {
                    await NoteOnceAsync(app, $"left:{containerId}:{look.FinishedAt?.Ticks}", left.Cause == KeepCause.Dead ? KeepText.Dead : KeepText.Left(left), ct);
                    // A dead container's page says so already; a stop Gitberth leaves gets its banner.
                    return track.GaveUpAt is not null ? GaveUpStatus(track) : left.Cause == KeepCause.Dead ? null : new KeepStatus(KeepState.Left, left);
                }
                return track.GaveUpAt is not null ? GaveUpStatus(track) : null;
            case KeepAction.Wait:
                return track.GaveUpAt is not null ? GaveUpStatus(track) : Soon(track, look.Verdict.Reason, now, (look.FinishedAt ?? now) + Grace);
            default:
                return await TryAsync(s, look, pass, ct);
        }
    }

    // What the deploy a try queued came to, once it's over; a status to say when that decides it, else null.
    private async Task<KeepStatus?> DeployEndedAsync(Seen s, Deployment? dep, KeepPass pass, CancellationToken ct)
    {
        var (app, _, track, _, _, _, now) = s;
        track.DeploymentEnded = true;
        // The try is over when its deploy is: the next one, and the 10 minutes up, count from then.
        if (dep?.FinishedAt is { } done && done > track.LastTryAt) track.LastTryAt = done;
        switch (dep)
        {
            case { Status: DeployStatus.Cancelled }:
                // Someone cancelled it: their own doing. The incident is over, and the app is left as it is (no tries, nor
                // any every 6 hours) until someone starts, stops or deploys it.
                track.Clear();
                track.LeftAt = now;
                track.Why = new KeepReason(KeepCause.Cancelled);
                board.Clear(ItemKey(app.Id));
                await NoteOnceAsync(app, $"cancelled:{dep.Id.ToString(CultureInfo.InvariantCulture)}", KeepText.Left(track.Why), ct);
                logger.LogInformation("The deploy that was to bring {Container} back was cancelled: the keep-running check leaves it as it is", app.ContainerName);
                pass.Did.Add($"{app.ContainerName}: left, its deploy cancelled");
                return new KeepStatus(KeepState.Left, track.Why);
            case { Status: DeployStatus.Failed } when CutShortByRestart(dep):
                // Gitberth restarted while it ran: no try of the app's, and start-up's own recovery brings the app back.
                track.Spent = false;
                track.Tell = false;
                return null;
            case { Status: DeployStatus.Failed or DeployStatus.RolledBack } when track.Builds && track.GaveUpAt is null:
                // The build didn't work: a build counts as every try, so Gitberth gives up at once.
                await GiveUpAsync(s, new KeepReason(KeepCause.BuildFailed), pass, ct);
                return GaveUpStatus(track);
            default:
                return null;
        }
    }

    // As start-up marks a deploy a restart of Gitberth cut short (the deploy notices read it the same way).
    private static bool CutShortByRestart(Deployment dep) => dep.Error?.StartsWith("Gitberth restarted", StringComparison.Ordinal) == true;

    // Busy: something else has the app right now. Waiting for it isn't a try.
    private string? Busy(Seen s)
    {
        var app = s.App;
        if (s.World.Deploying.Contains(app.Id)) return "a deploy of it is queued or running";
        if (s.World.Changing.Contains(app.ContainerName)) return "a change of it is under way, or waits for a decision";
        if (downStates?.IsUpdating(app.Id) == true) return "a deploy is replacing its container";
        if (actions.IsDeleting(app.Id)) return "it's being deleted";
        if (app.ParentAppId is int parent && s.World.ById.GetValueOrDefault(parent) is not { PreviewMode: not PreviewMode.Off })
            return "the app it previews is gone, or has previews off";
        return null;
    }

    private async Task<Look> LookAsync(Seen s, CancellationToken ct)
    {
        var app = s.App;
        if (s.Kind == KeepKind.Compose && s.World.Services.GetValueOrDefault(app.Id) is { Count: > 0 } services)
            return await LookAtServicesAsync(s, services, ct);
        var box = s.Boxes.FirstOrDefault(b => b.Name == app.ContainerName);
        if (box is null)
        {
            if (s.Kind == KeepKind.Existing)
            {
                await NoteOnceAsync(app, $"gone:{app.ContainerId ?? app.ContainerName}", KeepText.ExistingGone, ct);
                return new Look(new KeepVerdict(KeepAction.Leave), Own: false);
            }
            // Never made, so nothing to make again; renamed outside Gitberth, it's still there, and the app's page says so.
            if (s.Kind == KeepKind.Image && (app.ContainerId is null || await actions.RenamedToAsync(app, ct) is not null))
                return new Look(new KeepVerdict(KeepAction.None), Own: false);
            return Missing(app.ContainerName, s.Now);
        }
        _missingSince.Remove(app.ContainerName);
        return await DecideAsync(s, box, own: true, ct);
    }

    // A Compose app: each service its current release brought up, alone. A missing one brings the release up again.
    private async Task<Look> LookAtServicesAsync(Seen s, IReadOnlyList<string> services, CancellationToken ct)
    {
        var app = s.App;
        var members = s.Boxes.Where(b => b.Labels.GetValueOrDefault("gitberth.app") == app.Slug && b.Labels.ContainsKey(ComposeNames.ServiceLabel))
            .GroupBy(b => b.Labels[ComposeNames.ServiceLabel], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (services.Any(service => !members.ContainsKey(service))) return Missing("compose:" + app.Slug, s.Now);
        _missingSince.Remove("compose:" + app.Slug);
        var start = new List<string>();
        KeepReason? why = null;
        Look? wait = null, left = null, web = null;
        foreach (var service in services)
        {
            var box = members[service];
            var own = box.Name == app.ContainerName;
            var look = await DecideAsync(s, box, own, ct);
            if (own) web = look;
            switch (look.Verdict.Action)
            {
                case KeepAction.BringBack:
                    start.Add(service);
                    why ??= look.Verdict.Reason;
                    break;
                case KeepAction.Wait:
                    wait ??= look;
                    break;
                case KeepAction.Leave when own:
                    left = look;
                    break;
            }
        }
        if (start.Count > 0)
            return new Look(new KeepVerdict(KeepAction.BringBack, why, 6), web?.ContainerId, web?.StartedAt, web?.FinishedAt, start, Restarts: web?.Restarts ?? 0);
        return wait ?? left ?? web ?? new Look(new KeepVerdict(KeepAction.None));
    }

    // Missing: made again once two looks a pass apart both found it gone.
    private Look Missing(string key, DateTime now)
    {
        if (!_missingSince.TryGetValue(key, out var since))
        {
            _missingSince[key] = now;
            return new Look(new KeepVerdict(KeepAction.Wait, new KeepReason(KeepCause.Gone), 11), FinishedAt: now);
        }
        return now - since >= Grace
            ? new Look(new KeepVerdict(KeepAction.MakeAgain, new KeepReason(KeepCause.Gone), 11))
            : new Look(new KeepVerdict(KeepAction.Wait, new KeepReason(KeepCause.Gone), 11), FinishedAt: since);
    }

    private async Task<Look> DecideAsync(Seen s, Box box, bool own, CancellationToken ct)
    {
        var running = box.State is "running" or "restarting" or "paused" or "removing";
        if (running)
        {
            lock (_readGate) _read.Remove(box.Id);
            _left.Remove(box.Id);
            // When it started matters only to tell whose start it was after a give-up or a cancel, or that a try has worked.
            if (!own || s.Track is { GaveUpAt: null, LeftAt: null, Tries: 0 }) return new Look(new KeepVerdict(KeepAction.None, Rule: 1), box.Id, Own: own);
            var current = await engine.InspectContainerAsync(box.Id, ct);
            return new Look(new KeepVerdict(KeepAction.None, Rule: 1), box.Id, Time(current?["State"], "StartedAt"), Own: own, Restarts: Int(current, "RestartCount"));
        }
        Look? known;
        lock (_readGate) known = _read.GetValueOrDefault(box.Id);
        if (known is not null) return known with { Own = own };

        var inspect = await engine.InspectContainerAsync(box.Id, ct);
        if (inspect is null) return Missing(s.App.ContainerName, s.Now);
        var state = inspect["State"];
        var finished = Time(state, "FinishedAt");
        // The same stop it left before (the same container, stopped at the same moment): left still, as it was read then.
        var verdict = _left.GetValueOrDefault(box.Id) is { } kept && kept.FinishedAt == finished
            ? new KeepVerdict(KeepAction.Leave, kept.Reason, kept.Rule)
            : KeepRules.Decide(new KeepFacts(s.Kind, Str(state, "Status") ?? box.State, Int(state, "ExitCode"), finished, Bool(state, "OOMKilled"), Str(state, "Error"),
                Policy(inspect["HostConfig"]?["RestartPolicy"]), DeathOf(box.Id, finished), s.Own.StoppedAt, s.Own.StartedAt,
                s.Kind == KeepKind.Git ? await LastDeployAsync(s.App.Id, ct) : null), s.Now, Grace);
        var look = new Look(verdict, box.Id, Time(state, "StartedAt"), finished, Own: own, Restarts: Int(inspect, "RestartCount"));
        if (verdict.Action == KeepAction.Leave)
        {
            lock (_readGate) _read[box.Id] = look;
            _left[box.Id] = new LeftStop(finished, verdict.Reason, verdict.Rule);
        }
        return look;
    }

    // The die event Gitberth heard for this stop: the same container, within moments of the time Docker says it finished.
    private KeepDeath? DeathOf(string id, DateTime? finished)
    {
        if (finished is not { } at) return null;
        var mine = events.Events.Where(e => e.Id == id).ToList();
        for (var i = mine.Count - 1; i >= 0; i--)
        {
            if (mine[i].Action != "die" || (mine[i].Time - at).Duration() > TimeSpan.FromSeconds(5)) continue;
            var (crashed, oom) = EventMarks.CrashOf(mine, i);
            bool? byGitberth;
            lock (_readGate) byGitberth = _byGitberth.TryGetValue((id, mine[i].Time), out var ours) ? ours : null;
            return new KeepDeath(crashed, oom, EventMarks.AskedToStop(mine, i), byGitberth);
        }
        return null;
    }

    private async Task<KeepRun?> LastDeployAsync(int appId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var last = await db.Deployments.AsNoTracking().Where(d => d.AppId == appId && d.FinishedAt != null).OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        return last is { StartedAt: { } started } ? new KeepRun(started, last.FinishedAt, last.Status) : null;
    }

    // ---- What it does ----

    // It runs: a first try's notice once it does, an incident over after 10 minutes up, a quiet try that worked.
    private async Task<KeepStatus?> RunningAsync(Seen s, Look look, KeepPass pass, List<(AppDefinition App, Back What)> told, CancellationToken ct)
    {
        var (app, _, track, _, _, _, now) = s;
        if (track.GaveUpAt is not null)
        {
            if (track.QuietAt is { } quiet && now - quiet >= KeepRules.Steady)
            {
                var (title, text) = KeepText.CameBackNotice(app.Name);
                await PublishAsync(app, NotifyEvent.BroughtBack, NoticeLevel.Good, title, text, $"apps/{Uri.EscapeDataString(app.ContainerName)}", ct);
                await NoteAsync(app, ActivityKind.BroughtBack, KeepText.CameBack, ActivityTone.Good, ct);
                logger.LogInformation("{Container} runs again: the keep-running check's try after giving up worked", app.ContainerName);
                pass.Did.Add($"{app.ContainerName}: came back");
                track.Clear();
                board.Clear(ItemKey(app.Id));
                return null;
            }
            return GaveUpStatus(track);
        }
        if (track.Tell && track.Tries > 0)
        {
            told.Add((app, new Back(track.How, track.Why ?? new KeepReason(KeepCause.Crashed), track.Image, track.Deployment)));
            track.Tell = false;
        }
        if (track.Tries > 0 && KeepRules.IsSteady(track.LastTryAt, now)) track.Clear();
        return null;
    }

    // Down for a reason to bring it back: a try when one is due, else the give-up; quietly every 6 hours after that.
    private async Task<KeepStatus?> TryAsync(Seen s, Look look, KeepPass pass, CancellationToken ct)
    {
        var (app, kind, track, _, _, _, now) = s;
        var why = look.Verdict.Reason ?? new KeepReason(KeepCause.Crashed);
        if (track.GaveUpAt is not null)
        {
            if (track.NextQuietAt is { } next && now < next) return GaveUpStatus(track);
            if (await WaitsForChangeAsync(app, kind, ct)) return GaveUpStatus(track);
            var quiet = await DoAsync(s, look, ct);
            if (quiet.Busy) return GaveUpStatus(track);
            track.QuietAt = now;
            track.NextQuietAt = now + KeepRules.QuietEvery;
            track.QuietStartedAt = null;
            Queued(track, quiet);
            // The start it made, so a later one is known for someone else's.
            if (quiet is { Error: null, DeploymentId: null, How: KeepHow.Started or KeepHow.PutBack })
                track.QuietStartedAt = await StartedAtAsync(app.ContainerName, ct);
            await NoteAsync(app, ActivityKind.BroughtBack, KeepText.QuietTry(quiet.How, quiet.Error, quiet.DeploymentId),
                quiet.Error is null ? ActivityTone.Warning : ActivityTone.Danger, ct);
            Audited(app, quiet, why, "after giving up", look.Services);
            logger.LogInformation("Bringing {Container} back again, 6 hours after giving up: {Why}{Error}", app.ContainerName, why.Words,
                quiet.Error is null ? "" : $" (it didn't start: {quiet.Error})");
            pass.Did.Add($"{app.ContainerName}: quiet try");
            return GaveUpStatus(track);
        }

        var made = track.Tries;
        // A build is expensive, and the repo watcher has retries of its own: one counts as every try.
        if (track.Spent || KeepRules.NextTry(made, track.LastTryAt) is not { } due)
        {
            await GiveUpAsync(s, why, pass, ct);
            return GaveUpStatus(track);
        }
        if (now < due) return Soon(track, why, now, due);
        if (await WaitsForChangeAsync(app, kind, ct)) return Soon(track, why, now, now + PassEvery);
        var r = await DoAsync(s, look, ct);
        if (r.Busy) return Soon(track, why, now, now + PassEvery);

        var n = made + 1;
        track.Tries = n;
        track.Spent = r.How == KeepHow.Built;
        track.FirstTryAt ??= now;
        track.LastTryAt = now;
        track.Why = why;
        track.How = r.How;
        track.Image = r.Image;
        track.Services = look.Services?.ToList();
        track.Tell = made == 0 && r.Error is null;
        Queued(track, r);
        var text = r.Error is null
            ? KeepText.Tried(r.How, why, n, r.Image, r.DeploymentId, app.CurrentCommit, look.Services)
            : KeepText.Failed(r.Error, n);
        await NoteAsync(app, ActivityKind.BroughtBack, text, r.Error is null ? ActivityTone.Warning : ActivityTone.Danger, ct);
        Audited(app, r, why, $"{n} of {KeepRules.Tries}", look.Services);
        logger.LogInformation("Bringing {Container} back: {Why} (try {Try} of {Tries}){Error}", app.ContainerName, why.Words, n, KeepRules.Tries,
            r.Error is null ? "" : $"; it didn't start: {r.Error}");
        pass.Did.Add($"{app.ContainerName}: try {n} ({r.How})");
        if (r.Error is not null && r.How == KeepHow.Built)
        {
            await GiveUpAsync(s, new KeepReason(KeepCause.BuildFailed, Error: r.Error), pass, ct);
            return GaveUpStatus(track);
        }
        return r.Error is null ? null : Soon(track, why, now, KeepRules.NextTry(track.Tries, now));
    }

    // A container the change engine holds, or whose change isn't settled, waits: that isn't a try.
    private async Task<bool> WaitsForChangeAsync(AppDefinition app, KeepKind kind, CancellationToken ct)
    {
        if (kind is not (KeepKind.Image or KeepKind.Existing)) return false;
        if (containerLocks.IsHeld(app.ContainerName)) return true;
        return changer is not null && await changer.Value.WhyNotStartAsync(app.ContainerName, ct) is not null;
    }

    // What the try asks of the app's actions. A Git app's missing container is made again from its image, or built again
    // when Docker says the image is gone too (its 404). Docker not answering about the image isn't its being gone: that
    // throws, and the pass stops there, as it does for a container.
    private async Task<BringBackResult> DoAsync(Seen s, Look look, CancellationToken ct)
    {
        var kind = look.Verdict.Action == KeepAction.MakeAgain ? BringBackKind.Recreate : BringBackKind.Start;
        if (kind == BringBackKind.Recreate && s.Kind == KeepKind.Git && s.App.CurrentImage is { } image && await engine.InspectImageAsync(image, ct) is null)
            kind = BringBackKind.Build;
        return await actions.BringBackAsync(new BringBackRequest(s.App.Id, kind, look.Services), ct);
    }

    // When the container last started, as Docker says; null when it doesn't answer, and the try stands all the same.
    private async Task<DateTime?> StartedAtAsync(string container, CancellationToken ct)
    {
        try
        {
            return Time((await engine.InspectContainerAsync(container, ct))?["State"], "StartedAt");
        }
        catch (EngineException)
        {
            return null;
        }
    }

    private static void Queued(Track track, BringBackResult r)
    {
        track.Deployment = r.DeploymentId;
        track.DeploymentEnded = false;
        track.Builds = r.How == KeepHow.Built;
    }

    private async Task GiveUpAsync(Seen s, KeepReason why, KeepPass pass, CancellationToken ct)
    {
        var (app, _, track, _, _, _, now) = s;
        track.GaveUpAt = now;
        track.NextQuietAt = now + KeepRules.QuietEvery;
        track.Tell = false;
        track.Why = why;
        var tries = Math.Max(1, track.Tries);
        var over = now - (track.FirstTryAt ?? now);
        await NoteAsync(app, ActivityKind.GaveUp, KeepText.GaveUp(why, tries, over), ActivityTone.Danger, ct);
        audit?.Record(new AuditEntry("app.recovery.stopped", KeepText.StoppedTrying(app.Name, why, tries, over))
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Outcome = AuditOutcome.Failed,
            Changes = [new("Why", null, why.Words), new("Tries", null, tries.ToString(CultureInfo.InvariantCulture))],
        });
        var (title, text) = KeepText.GaveUpNotice(app.Name, why, tries, over);
        // A build that didn't work: its deploy's log says why; else the app's own log.
        var link = why.Cause == KeepCause.BuildFailed && track.Deployment is int id
            ? $"apps/{app.Id.ToString(CultureInfo.InvariantCulture)}/deployments/{id.ToString(CultureInfo.InvariantCulture)}"
            : $"apps/{Uri.EscapeDataString(app.ContainerName)}/logs";
        await PublishAsync(app, NotifyEvent.GaveUp, NoticeLevel.Critical, title, text, link, ct, why.Cause == KeepCause.BuildFailed ? track.Deployment : null);
        logger.LogWarning("Gave up bringing {Container} back after {Tries} tries in {Over}: {Why}. Trying again every 6 hours.", app.ContainerName, tries,
            KeepText.Span(over), why.Words);
        pass.Did.Add($"{app.ContainerName}: gave up");
    }

    private void Audited(AppDefinition app, BringBackResult r, KeepReason why, string attempt, IReadOnlyList<string>? services) =>
        audit?.Record(new AuditEntry("app.recovered", r.Error is null
            ? KeepText.Recovered(app.Name, r.How, why, r.Image, r.DeploymentId, app.CurrentCommit, services)
            : KeepText.CouldntRecover(app.Name, r.Error))
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
            Outcome = r.Error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            Correlation = r.DeploymentId is int id ? AuditCorrelation.ForDeployment(id) : null,
            Changes = [new("Why", null, why.Words), new("Try", null, attempt), new("How", null, KeepText.HowWords(r.How, r.DeploymentId, services))],
        });

    /// <summary>An incident's first try, to be told once the app runs: what it did, and why.</summary>
    private sealed record Back(KeepHow How, KeepReason Why, string? Image, int? Deployment);

    // The first try of each incident, told once the app runs; several brought back after Docker or the host restarted, as
    // one notice (of the ones that would be told at all: a muted app or a preview with previews off stays out of it too).
    private async Task TellAsync(List<(AppDefinition App, Back What)> told, CancellationToken ct)
    {
        var notices = new List<(Back What, Notice Notice)>();
        foreach (var (app, what) in told)
        {
            var (title, text) = KeepText.BackNotice(app.Name, what.How, what.Why, what.Image, what.Deployment, app.CurrentCommit);
            var link = what.Deployment is int id
                ? $"apps/{app.Id.ToString(CultureInfo.InvariantCulture)}/deployments/{id.ToString(CultureInfo.InvariantCulture)}"
                : $"apps/{Uri.EscapeDataString(app.ContainerName)}/logs";
            notices.Add((what, await NoticeAsync(app, NotifyEvent.BroughtBack, NoticeLevel.Warning, title, text, link, ct, what.Deployment)));
        }
        var restarted = notices.Where(n => n.What.Why.Cause == KeepCause.HostRestarted && notifier.Wanted(n.Notice)).ToList();
        if (restarted.Count >= 2)
        {
            var (title, text) = KeepText.BatchNotice(restarted.Select(n => n.Notice.App).ToList());
            notifier.Publish(new Notice(NotifyEvent.BroughtBack, NoticeLevel.Warning, title, text, await notifier.LinkAsync("apps", ct), "", null,
                _time.GetUtcNow().UtcDateTime));
            notices = notices.Except(restarted).ToList();
        }
        foreach (var (_, notice) in notices) notifier.Publish(notice);
    }

    // A notice, as well as it goes: one that can't be made (its link unread) is logged, and what the check did still counts.
    private async Task PublishAsync(AppDefinition app, NotifyEvent kind, NoticeLevel level, string title, string text, string path, CancellationToken ct,
        int? deployment = null)
    {
        try
        {
            notifier.Publish(await NoticeAsync(app, kind, level, title, text, path, ct, deployment));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The keep-running check couldn't send its notice about {Container}: {Title}", app.ContainerName, title);
        }
    }

    private async Task<Notice> NoticeAsync(AppDefinition app, NotifyEvent kind, NoticeLevel level, string title, string text, string path, CancellationToken ct,
        int? deployment = null) =>
        new(kind, level, title, text, await notifier.LinkAsync(path, ct), app.Name, app.ContainerName, _time.GetUtcNow().UtcDateTime)
        {
            AppId = app.Id, Preview = app.IsPreview, DeploymentId = deployment,
        };

    // An entry in the app's Activity, as well as the database takes it: one that can't be written is logged, and what the
    // check did still counts (a try forgotten for want of its entry would be made again and again).
    private async Task NoteAsync(AppDefinition app, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
    {
        try
        {
            await activity.AddAsync(app.ContainerName, app.Id, kind, text, tone, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The keep-running check couldn't write to the Activity of {Container}: {Text}", app.ContainerName, text);
        }
    }

    private async Task NoteOnceAsync(AppDefinition app, string key, string text, CancellationToken ct)
    {
        if (_noted.Contains(key)) return;
        _noted.Add(key);
        if (_noted.Count > 500) _noted.RemoveRange(0, _noted.Count - 500);
        await NoteAsync(app, ActivityKind.Event, text, ActivityTone.Neutral, ct);
    }

    // ---- What it says ----

    private KeepStatus Soon(Track track, KeepReason? why, DateTime now, DateTime? at = null) =>
        new(KeepState.Soon, why ?? track.Why, Math.Min(track.Tries + 1, KeepRules.Tries), at ?? now);

    private static KeepStatus GaveUpStatus(Track track) =>
        new(KeepState.GaveUp, track.Why, 0, track.NextQuietAt, Math.Max(1, track.Tries), (track.GaveUpAt ?? DateTime.MinValue) - (track.FirstTryAt ?? track.GaveUpAt ?? DateTime.MinValue),
            track.GaveUpAt);

    // A start that came of the quiet try: the start it made (read right after it), or one its deploy made before it was
    // done. One Docker's restart policy made since the last start someone asked for (its RestartCount) can't be told apart
    // from that start, so it counts as the check's. Any other later start is someone else's.
    private static bool Ours(Track track, DateTime started, int restarts, World world)
    {
        if (track.QuietAt is not { } quiet || started < quiet - OwnStartSlack) return false;
        if (restarts > 0) return true;
        if (track.QuietStartedAt is { } mine) return started <= mine;
        if (track.Deployment is int id && world.Ours.GetValueOrDefault(id)?.FinishedAt is { } done && done > quiet) return started <= done;
        return started <= quiet + OwnStartSlack;
    }

    private static string ItemKey(int appId) => $"keep:{appId.ToString(CultureInfo.InvariantCulture)}";

    // The statuses for the pages, the give-ups for Settings and the dashboard's board.
    private void Publish(World world, ImmutableDictionary<int, KeepStatus> statuses, DateTime now, bool changed)
    {
        var gaveUp = _tracks.Where(t => t.Value is { GaveUpAt: not null, Why: not null } && world.ById.ContainsKey(t.Key))
            .Select(t => (Row: world.ById[t.Key], Track: t.Value))
            .Where(x => x.Row.DesiredState == DesiredState.Running)
            .OrderBy(x => x.Track.GaveUpAt)
            .Select(x => new KeepGaveUp(x.Row.Id, x.Row.ContainerName, x.Row.Name, x.Track.Why!, Math.Max(1, x.Track.Tries),
                x.Track.GaveUpAt!.Value - (x.Track.FirstTryAt ?? x.Track.GaveUpAt!.Value), x.Track.GaveUpAt!.Value, x.Track.NextQuietAt))
            .ToImmutableList();
        board.Replace(Source, gaveUp.Select(g => Item(g, now)));
        changed |= !_gaveUp.SequenceEqual(gaveUp) || _status.Count != statuses.Count || statuses.Any(s => !Equals(_status.GetValueOrDefault(s.Key), s.Value));
        _gaveUp = gaveUp;
        _status = statuses;
        if (changed) Changed?.Invoke();
    }

    private static AttentionItem Item(KeepGaveUp g, DateTime now)
    {
        var key = Uri.EscapeDataString(g.Key);
        return new AttentionItem(ItemKey(g.AppId), Source, AttentionLevel.Critical, $"Gitberth stopped bringing {g.Name} back",
            KeepText.GaveUpBanner(g.Reason, g.Tries, g.Over, g.Next, now), $"apps/{key}",
            Tip: "app.banner.keeper-gave-up",
            Actions: [new AttentionAction($"Open {g.Name}", $"apps/{key}"), new AttentionAction("Logs", $"apps/{key}/logs")])
        {
            Pill = $"{g.Name} keeps stopping",
            Problem = KeepText.GaveUpProblem(g.Name, g.Key, g.Reason, g.Tries, g.Over),
        };
    }

    /// <summary>How often apps were brought back in the 24 hours before <paramref name="now"/>, and which.</summary>
    public async Task<(int Times, IReadOnlyList<string> Apps)> RecentAsync(DateTime now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = now.AddHours(-24);
        // The tries that started something: not the ones that couldn't (Danger), nor a quiet try's having worked (Good).
        var tries = await db.Activity.AsNoTracking().Where(a => a.Kind == ActivityKind.BroughtBack && a.At >= since && a.Tone == ActivityTone.Warning)
            .Select(a => new { a.AppId, a.ContainerName }).ToListAsync(ct);
        var ids = tries.Select(t => t.AppId).OfType<int>().Distinct().ToList();
        var names = await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return (tries.Count, tries.Select(t => t.AppId is int id && names.TryGetValue(id, out var name) ? name : t.ContainerName).Distinct().Order().ToList());
    }

    // ---- What it reads ----

    /// <param name="Ours">The deploys the tries queued, by ID.</param>
    /// <param name="DeployedSince">
    /// An app Gitberth gave up on or left that someone deployed since: a deploy that ended after it and touched the container
    /// (not a try's, not a commit the build filters skipped, not one cancelled before it ran).
    /// </param>
    private sealed record World(List<AppDefinition> Apps, Dictionary<int, AppDefinition> ById, HashSet<int> Deploying, HashSet<string> Changing,
        Dictionary<int, Deployment> Ours, Dictionary<int, IReadOnlyList<string>> Services, HashSet<int> DeployedSince);

    private async Task<World> ReadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apps = await db.Apps.AsNoTracking().ToListAsync(ct);
        var deploying = (await db.Deployments.AsNoTracking()
            .Where(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting)
            .Select(d => d.AppId).Distinct().ToListAsync(ct)).ToHashSet();
        var changing = (await db.ContainerChanges.AsNoTracking()
            .Where(c => c.Outcome == ChangeOutcome.Running || c.Outcome == ChangeOutcome.NeedsDecision)
            .Select(c => c.ContainerName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var queued = _tracks.Values.Select(t => t.Deployment).OfType<int>().ToList();
        var ours = queued.Count == 0 ? [] : await db.Deployments.AsNoTracking().Where(d => queued.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        // A Compose app's services: the ones its current release (its newest successful deploy) brought up. One Compose never
        // creates (behind a profile, or with no replicas) isn't a container gone.
        var composeIds = apps.Where(a => a.IsCompose).Select(a => a.Id).ToList();
        var services = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var id in composeIds)
        {
            var images = await db.Deployments.AsNoTracking().Where(d => d.AppId == id && d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id)
                .Select(d => d.Images).FirstOrDefaultAsync(ct);
            var names = ComposeImages.Parse(images).BroughtUp;
            if (names.Count > 0) services[id] = names;
        }
        var since = _tracks.Where(t => (t.Value.GaveUpAt ?? t.Value.LeftAt) is not null).ToDictionary(t => t.Key, t => (t.Value.GaveUpAt ?? t.Value.LeftAt)!.Value);
        var deployedSince = new HashSet<int>();
        if (since.Count > 0)
        {
            var sinceIds = since.Keys.ToList();
            var first = since.Values.Min();
            var ended = await db.Deployments.AsNoTracking()
                .Where(d => sinceIds.Contains(d.AppId) && d.FinishedAt != null && d.FinishedAt > first && d.Trigger != DeployTrigger.Recover
                    && (d.Status == DeployStatus.Succeeded || d.Status == DeployStatus.Failed || d.Status == DeployStatus.RolledBack))
                .Select(d => new { d.AppId, d.FinishedAt }).ToListAsync(ct);
            deployedSince = ended.Where(d => d.FinishedAt > since[d.AppId]).Select(d => d.AppId).ToHashSet();
        }
        return new World(apps, apps.ToDictionary(a => a.Id), deploying, changing, ours, services, deployedSince);
    }

    private sealed record Box(string Id, string Name, string State, IReadOnlyDictionary<string, string> Labels);

    private static List<Box> Boxes(JsonArray listed) => listed.OfType<JsonObject>().Select(c => new Box(
            Str(c, "Id") ?? "",
            ((c["Names"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "").TrimStart('/'),
            Str(c, "State") ?? "",
            (c["Labels"] as JsonObject ?? []).Where(l => l.Value is JsonValue).ToDictionary(l => l.Key, l => l.Value!.GetValue<string>(), StringComparer.Ordinal)))
        .Where(b => b.Id.Length > 0 && b.Name.Length > 0)
        .ToList();

    private sealed record Own(string? Name, DateTime? StartedAt, DateTime? StoppedAt);

    // Gitberth's own container, once per start of Gitberth: when it started, and when it last stopped (a new one never did).
    private async Task<Own> OwnAsync(List<Box> boxes, CancellationToken ct)
    {
        if (_own is { } known) return known;
        var self = options.OwnContainer;
        var mine = self is null ? null : boxes.FirstOrDefault(b => b.Name == self || b.Id.StartsWith(self, StringComparison.Ordinal));
        if (mine is null) return _own = new Own(null, null, null);
        var state = (await engine.InspectContainerAsync(mine.Id, ct))?["State"];
        return _own = new Own(mine.Name, Time(state, "StartedAt"), Time(state, "FinishedAt"));
    }

    private static KeepKind KindOf(AppDefinition app) => app switch
    {
        { IsCompose: true } => KeepKind.Compose,
        { Source: AppSource.Git } => KeepKind.Git,
        { Source: AppSource.Existing } => KeepKind.Existing,
        _ => KeepKind.Image,
    };

    private static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static bool Bool(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // RFC 3339 with nanoseconds; Docker writes the zero time ("0001-01-01T00:00:00Z") when there's none.
    private static DateTime? Time(JsonNode? node, string key) =>
        Str(node, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year > 1
            ? t.UtcDateTime
            : null;

    // "no", "on-failure:3", "unless-stopped", "always".
    private static string Policy(JsonNode? policy) => Str(policy, "Name") switch
    {
        null or "" or "no" => "no",
        "on-failure" when Int(policy, "MaximumRetryCount") is > 0 and var n => $"on-failure:{n.ToString(CultureInfo.InvariantCulture)}",
        var name => name,
    };

    // ---- Kept across restarts ----

    /// <summary>What the check keeps of one app, in keep.state.</summary>
    internal sealed class Track
    {
        public int Tries { get; set; }
        public DateTime? FirstTryAt { get; set; }
        public DateTime? LastTryAt { get; set; }
        public KeepReason? Why { get; set; }
        public KeepHow How { get; set; }
        public string? Image { get; set; }
        public List<string>? Services { get; set; }
        /// <summary>The deploy its last try queued, and whether it has ended and whether it builds.</summary>
        public int? Deployment { get; set; }
        public bool DeploymentEnded { get; set; }
        public bool Builds { get; set; }
        /// <summary>A build was tried: it counts as every try, so the next stop gives up.</summary>
        public bool Spent { get; set; }
        /// <summary>The incident's first try worked: told once the app runs.</summary>
        public bool Tell { get; set; }
        public DateTime? GaveUpAt { get; set; }
        public DateTime? NextQuietAt { get; set; }
        public DateTime? QuietAt { get; set; }
        /// <summary>The start the last quiet try made, read right after it; null for one a deploy or a create made.</summary>
        public DateTime? QuietStartedAt { get; set; }
        /// <summary>When someone cancelled the deploy that was to bring it back: left as it is until someone starts, stops or deploys it.</summary>
        public DateTime? LeftAt { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Tries == 0 && GaveUpAt is null && LeftAt is null && !Tell;

        public void Clear()
        {
            Tries = 0;
            FirstTryAt = LastTryAt = GaveUpAt = NextQuietAt = QuietAt = QuietStartedAt = LeftAt = null;
            Why = null;
            Image = null;
            Services = null;
            Deployment = null;
            DeploymentEnded = Builds = Spent = Tell = false;
        }
    }

    /// <summary>A stop the check left: when the container stopped (Docker's FinishedAt), and why it was left.</summary>
    internal sealed record LeftStop(DateTime? FinishedAt, KeepReason? Reason, int Rule);

    private sealed class Saved
    {
        public Dictionary<int, Track> Tracks { get; set; } = [];
        public List<string> Noted { get; set; } = [];
        public Dictionary<string, LeftStop> Left { get; set; } = [];
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync(SettingKeys.KeepState, ct);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json, Json);
            foreach (var (id, track) in saved?.Tracks ?? []) _tracks[id] = track;
            _noted.AddRange(saved?.Noted ?? []);
            foreach (var (id, left) in saved?.Left ?? []) _left[id] = left;
            _saved = json;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The keep-running check's saved state couldn't be read; it starts afresh");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var json = _tracks.Count == 0 && _noted.Count == 0 && _left.Count == 0
            ? null
            : JsonSerializer.Serialize(new Saved { Tracks = new(_tracks), Noted = [.. _noted], Left = new(_left, StringComparer.Ordinal) }, Json);
        if (json == _saved) return;
        await settings.SetAsync(SettingKeys.KeepState, json, ct);
        _saved = json;
    }
}
