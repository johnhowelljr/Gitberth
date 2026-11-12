using Gitberth.Data;
using Gitberth.Services;

namespace Gitberth.Health;

/// <summary>
/// One step in a container's life, from Docker's event stream: start, die, kill, stop, oom, pause, unpause, restart, or a
/// health change like "health_status: unhealthy".
/// </summary>
/// <param name="Name">The container's name at the time.</param>
/// <param name="ExitCode">For die: how its main process ended.</param>
/// <param name="Signal">For kill: the signal sent.</param>
/// <param name="Deployment">For a container Gitberth started: the deploy that started it.</param>
/// <param name="Trigger">What started that deploy, as <see cref="DeployTrigger"/> names it.</param>
public sealed record ContainerEvent(DateTime Time, string Id, string Name, string Action,
    int? ExitCode = null, int? Signal = null, int? Deployment = null, string? Trigger = null)
{
    /// <summary>The actions the Health page marks. Docker sends many more (exec, attach, rename, destroy, …).</summary>
    public static bool Relevant(string action) =>
        action is "start" or "die" or "kill" or "stop" or "oom" or "pause" or "unpause" or "restart"
        || action.StartsWith("health_status", StringComparison.Ordinal);
}

/// <summary>What a mark is about. Recreate is a new container under the name that Gitberth didn't deploy (docker compose up, say).</summary>
public enum MarkKind { Deploy, Recreate, Restart, Crash, Stop, Start, Health }

public enum MarkTone { Neutral, Good, Warning, Danger }

/// <summary>Something that happened to a container, marked on its charts.</summary>
/// <param name="Label">A few words for the chart: "Deploy #12", "Restarted", "Crashed".</param>
/// <param name="Detail">All of it, for the tooltip and the table: "Crashed (exit code 1) and restarted itself".</param>
public sealed record ChartMark(DateTime Time, MarkKind Kind, string Label, string Detail, MarkTone Tone);

/// <summary>A stretch of time a container wasn't running (stopped, crashed or paused), shaded on its charts.</summary>
public readonly record struct DownSpan(DateTime From, DateTime To);

/// <summary>Turns one container's events into the marks on its charts and the stretches it was down.</summary>
public static class EventMarks
{
    /// <summary>A stop and a start closer together than this are one mark (a restart, or a deploy's swap), not downtime worth shading.</summary>
    public static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    // docker stop waits 10 seconds by default before it kills; Gitberth's own stops can wait longer.
    private static readonly TimeSpan StopWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OomWindow = TimeSpan.FromSeconds(10);

    // Signals that end a process when sent on purpose. Others, like HUP, only tell it something.
    private static readonly HashSet<int> Ending = [2, 3, 9, 15];

    private enum Why { Stopped, OutOfMemory, Exited, Paused }

    private sealed record Down(DateTime At, string Id, Why Why, int? ExitCode);

    private readonly record struct Note(MarkKind Kind, string Label, string Detail, MarkTone Tone);

    /// <param name="events">One container name's events, oldest first, with some from before <paramref name="from"/> to know how it stood then.</param>
    /// <param name="running">Whether it runs now. A stop with no start after it then means the start was missed, not that it's still down.</param>
    public static (List<ChartMark> Marks, List<DownSpan> Down) For(IReadOnlyList<ContainerEvent> events, DateTime from, DateTime to, bool running)
    {
        var marks = new List<ChartMark>();
        var spans = new List<DownSpan>();
        var started = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        int? newestDeploy = null;
        Down? down = null;
        var unhealthy = false;

        void Mark(DateTime at, Note n)
        {
            if (at >= from && at <= to) marks.Add(new ChartMark(at, n.Kind, n.Label, n.Detail, n.Tone));
        }

        void Shade(DateTime a, DateTime b)
        {
            if (b > from && a < to) spans.Add(new DownSpan(a < from ? from : a, b > to ? to : b));
        }

        void Up(ContainerEvent e, bool resumed)
        {
            // Another container under the name: the one that ran before, or the one that just stopped if that's all there is to go by.
            var replaced = (current ?? down?.Id) is { } before && before != e.Id;
            var fresh = started.Add(e.Id);
            current = e.Id;
            Note? cause = null;
            if (!resumed && e.Deployment is { } n && fresh)
            {
                // A deploy older than one already seen here is the previous version being put back.
                cause = n < newestDeploy ? new Note(MarkKind.Deploy, "Rolled back", $"The previous version (deploy #{n}) is running again", MarkTone.Neutral)
                    : Deployed(n, e.Trigger);
                if (!(n < newestDeploy)) newestDeploy = n;
            }
            else if (!resumed && replaced)
            {
                cause = fresh ? new Note(MarkKind.Recreate, "Recreated", "A new container took its place", MarkTone.Neutral)
                    : new Note(MarkKind.Deploy, "Rolled back", "The previous container is running again", MarkTone.Neutral);
            }

            if (down is not { } d)
            {
                // No stop on record before it: its first start, or one whose stop was missed.
                Mark(e.Time, cause ?? (resumed ? Resumed : Started));
                return;
            }
            down = null;
            if (e.Time - d.At <= Quick)
            {
                // One mark for the lot: a restart, a crash it recovered from, or a deploy's swap.
                Mark(e.Time, cause ?? Restarted(d, e.Time - d.At));
            }
            else
            {
                Mark(d.At, Stopped(d));
                Shade(d.At, e.Time);
                Mark(e.Time, cause ?? (resumed ? Resumed : Started));
            }
        }

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            switch (e.Action)
            {
                case "die":
                    // Paused and then killed: it's the death that counts.
                    if (down is null or { Why: Why.Paused }) down = new Down(e.Time, e.Id, WhyDied(events, i), e.ExitCode);
                    unhealthy = false;
                    break;
                case "pause":
                    down ??= new Down(e.Time, e.Id, Why.Paused, null);
                    break;
                case "unpause":
                    if (down is { Why: Why.Paused }) Up(e, resumed: true);
                    break;
                case "start":
                    unhealthy = false;
                    Up(e, resumed: false);
                    break;
                case var action when action.StartsWith("health_status", StringComparison.Ordinal):
                    if (action.EndsWith("unhealthy", StringComparison.Ordinal))
                    {
                        if (!unhealthy) Mark(e.Time, new Note(MarkKind.Health, "Unhealthy", "Its health check started failing", MarkTone.Warning));
                        unhealthy = true;
                    }
                    else if (action.EndsWith("healthy", StringComparison.Ordinal))
                    {
                        if (unhealthy) Mark(e.Time, new Note(MarkKind.Health, "Healthy", "Its health check passes again", MarkTone.Good));
                        unhealthy = false;
                    }
                    break;
            }
        }

        if (down is { } last)
        {
            Mark(last.At, Stopped(last));
            // Still down; unless it runs after all, when its start went unrecorded (Gitberth wasn't watching).
            if (!running) Shade(last.At, to);
        }
        marks.Sort((a, b) => a.Time.CompareTo(b.Time));
        return (marks, spans);
    }

    private static readonly Note Started = new(MarkKind.Start, "Started", "Started", MarkTone.Good);
    private static readonly Note Resumed = new(MarkKind.Start, "Resumed", "Resumed after a pause", MarkTone.Neutral);

    private static Note Deployed(int deployment, string? trigger) =>
        Enum.TryParse<DeployTrigger>(trigger, out var t)
            ? t switch
            {
                DeployTrigger.Restart => new(MarkKind.Restart, "Scheduled restart", $"Restarted on its schedule (deploy #{deployment})", MarkTone.Neutral),
                DeployTrigger.Rollback => new(MarkKind.Deploy, $"Rollback #{deployment}", $"Rolled back (deploy #{deployment})", MarkTone.Neutral),
                DeployTrigger.ConfigChange => new(MarkKind.Deploy, "Settings applied", $"Restarted with its new settings (deploy #{deployment})", MarkTone.Neutral),
                DeployTrigger.Created => new(MarkKind.Deploy, $"Deploy #{deployment}", $"First deploy (#{deployment})", MarkTone.Neutral),
                DeployTrigger.Start => new(MarkKind.Start, "Started", $"Started from Gitberth (deploy #{deployment})", MarkTone.Good),
                _ => new(MarkKind.Deploy, $"Deploy #{deployment}", $"{Format.Trigger(t)}: deploy #{deployment}", MarkTone.Neutral),
            }
            : new(MarkKind.Deploy, $"Deploy #{deployment}", $"Deploy #{deployment}", MarkTone.Neutral);

    // Stopped and back within moments.
    private static Note Restarted(Down d, TimeSpan away) => d.Why switch
    {
        Why.OutOfMemory => new(MarkKind.Crash, "Out of memory", "Ran out of memory and restarted", MarkTone.Danger),
        Why.Exited when d.ExitCode is { } code and not 0 => new(MarkKind.Crash, "Crashed", $"Crashed (exit code {code}) and restarted itself", MarkTone.Danger),
        Why.Exited => new(MarkKind.Restart, "Restarted", "Exited and restarted itself", MarkTone.Neutral),
        Why.Paused => new(MarkKind.Stop, "Paused", $"Paused for {Math.Max(1, (int)away.TotalSeconds)} seconds", MarkTone.Neutral),
        _ => new(MarkKind.Restart, "Restarted", "Restarted", MarkTone.Neutral),
    };

    // Stopped for a while, or still.
    private static Note Stopped(Down d) => d.Why switch
    {
        Why.OutOfMemory => new(MarkKind.Crash, "Out of memory", "Ran out of memory and stopped", MarkTone.Danger),
        Why.Exited when d.ExitCode is { } code and not 0 => new(MarkKind.Crash, "Crashed", $"Crashed (exit code {code})", MarkTone.Danger),
        Why.Exited => new(MarkKind.Stop, "Exited", "Its process ended (exit code 0)", MarkTone.Neutral),
        Why.Paused => new(MarkKind.Stop, "Paused", "Paused", MarkTone.Neutral),
        _ => new(MarkKind.Stop, "Stopped", "Stopped", MarkTone.Neutral),
    };

    /// <summary>
    /// Whether the die at <paramref name="i"/> was a crash, by the same rule as the charts' marks: it ran out of memory, or
    /// its process ended with an error on its own. Not a stop someone asked for (a kill or a stop around it), not an exit
    /// with 0, and not one ended by SIGTERM or SIGINT (143, 130), which is a stop from outside even when Docker didn't say so.
    /// </summary>
    public static (bool Crashed, bool OutOfMemory) CrashOf(IReadOnlyList<ContainerEvent> events, int i) => WhyDied(events, i) switch
    {
        Why.OutOfMemory => (true, true),
        Why.Exited when events[i].ExitCode is { } code and not (0 or 130 or 143) => (true, false),
        _ => (false, false),
    };

    /// <summary>Why the container in a die event stopped: asked to (a kill before it, or the stop Docker reports after it), out of memory, or on its own.</summary>
    private static Why WhyDied(IReadOnlyList<ContainerEvent> events, int i)
    {
        var die = events[i];
        for (var j = i - 1; j >= 0 && die.Time - events[j].Time <= StopWindow; j--)
        {
            var e = events[j];
            if (e.Id != die.Id) continue;
            if (e.Action == "oom" && die.Time - e.Time <= OomWindow) return Why.OutOfMemory;
            if (e.Action == "kill" && e.Signal is { } signal && Ending.Contains(signal)) return Why.Stopped;
            // An earlier life of the same container.
            if (e.Action is "die" or "start") break;
        }
        for (var j = i + 1; j < events.Count && events[j].Time - die.Time <= TimeSpan.FromSeconds(5); j++)
            if (events[j].Id == die.Id && events[j].Action == "stop") return Why.Stopped;
        return Why.Exited;
    }
}
