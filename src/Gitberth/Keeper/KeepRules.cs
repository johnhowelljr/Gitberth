using Gitberth.Data;

namespace Gitberth.Keeper;

/// <summary>What sort of app a container is, for what the keep-running check may do with it.</summary>
public enum KeepKind
{
    /// <summary>Built from a repo: a missing container is made again from its image, or built again from the commit it ran.</summary>
    Git,
    /// <summary>A Compose app: a crashed service starts alone; a missing one brings the current release up again.</summary>
    Compose,
    /// <summary>From a registry, a local image or by hand: a missing container is made again from its saved settings.</summary>
    Image,
    /// <summary>A container Gitberth took over: started again after a crash, never made again when it's gone.</summary>
    Existing,
}

/// <summary>Why an app's container isn't running, as the keep-running check reads it.</summary>
public enum KeepCause
{
    /// <summary>It stopped when Docker or the host restarted.</summary>
    HostRestarted,
    /// <summary>A deploy stopped it, failed, and didn't start it again.</summary>
    DeployCutShort,
    OutOfMemory,
    /// <summary>Docker couldn't start it (a port taken, a folder missing at boot): its State.Error.</summary>
    CouldntStart,
    Crashed,
    /// <summary>Someone stopped it: a stop or a kill around its death, or a stop's exit code.</summary>
    Stopped,
    /// <summary>Its program ended by itself, with 0: how one-shot jobs end.</summary>
    Ended,
    Dead,
    /// <summary>Its container is gone.</summary>
    Gone,
    /// <summary>Its container and its image were gone, and the build that was to bring it back didn't work.</summary>
    BuildFailed,
    /// <summary>Someone cancelled the deploy that was to bring it back: it's left as it is until someone starts, stops or deploys it.</summary>
    Cancelled,
}

/// <summary>Why an app's container isn't running, with what Docker said about it.</summary>
/// <param name="Policy">Its restart policy as Docker has it ("no", "on-failure:3"): it says why Docker didn't start it again.</param>
public sealed record KeepReason(KeepCause Cause, int? ExitCode = null, string? Error = null, string? Policy = null)
{
    /// <summary>"it crashed (exit code 3)": the reason in a sentence of its own.</summary>
    public string Words => Cause switch
    {
        KeepCause.HostRestarted => "it stopped when Docker or the host restarted",
        KeepCause.DeployCutShort => "a deploy stopped it and couldn't start it again",
        KeepCause.OutOfMemory => "it ran out of memory",
        KeepCause.CouldntStart => $"Docker couldn't start it: {KeepText.Clause(Error)}",
        KeepCause.Crashed => $"it crashed (exit code {ExitCode ?? 1})",
        KeepCause.Stopped => $"it was stopped (exit code {ExitCode ?? 0})",
        KeepCause.Ended => "its program ended by itself (exit code 0)",
        KeepCause.Dead => "Docker can't start it: it's dead",
        KeepCause.BuildFailed when Error is { Length: > 0 } => $"its container and its image were gone, and building it again didn't work: {KeepText.Clause(Error)}",
        KeepCause.BuildFailed => "its container and its image were gone, and building it again didn't work",
        KeepCause.Cancelled => "the deploy that was to bring it back was cancelled",
        _ => "its container was gone",
    };

    /// <summary>
    /// The reason with why Docker didn't start it again, where its restart policy says: "it crashed (exit code 3), and its
    /// restart policy (no) doesn't start it again".
    /// </summary>
    public string Told => Cause is KeepCause.Crashed or KeepCause.OutOfMemory && PolicyWords(Policy) is { } policy ? $"{Words}, and {policy}" : Words;

    private static string? PolicyWords(string? policy) => policy switch
    {
        null or "" or "no" => "its restart policy (no) doesn't start it again",
        _ when policy.StartsWith("on-failure", StringComparison.Ordinal) => $"its restart policy ({policy}) didn't start it again",
        _ => null,
    };
}

/// <summary>What Docker says of one of an app's containers, for <see cref="KeepRules.Decide"/>.</summary>
/// <param name="State">Docker's word for it (running, restarting, paused, removing, exited, created, dead); null when there's none.</param>
/// <param name="FinishedAt">When it stopped (State.FinishedAt); null when Docker has none.</param>
/// <param name="Policy">Its restart policy: "no", "on-failure:3", "unless-stopped", "always".</param>
/// <param name="Died">How Gitberth heard it die (its die event); null when Gitberth wasn't watching then.</param>
/// <param name="OwnStoppedAt">When Gitberth's own container last stopped (its FinishedAt); null when it never did, as a new one hasn't.</param>
/// <param name="OwnStartedAt">When Gitberth's own container started.</param>
/// <param name="LastDeploy">A Git app's last deploy that ended: when it ran and how it ended.</param>
public sealed record KeepFacts(KeepKind Kind, string? State, int ExitCode = 0, DateTime? FinishedAt = null, bool OutOfMemory = false,
    string? Error = null, string? Policy = null, KeepDeath? Died = null, DateTime? OwnStoppedAt = null, DateTime? OwnStartedAt = null,
    KeepRun? LastDeploy = null);

/// <summary>A die event as Gitberth heard it: a crash (out of memory too), or a stop someone asked for.</summary>
/// <param name="ByGitberth">
/// Whether Gitberth itself was stopping the container then (a deploy's swap), as it knew when it heard the death; null when
/// it can't say (it heard the death only after a restart of its own).
/// </param>
public sealed record KeepDeath(bool Crashed, bool OutOfMemory, bool AskedToStop, bool? ByGitberth = null);

/// <summary>A deploy's run, and how it ended.</summary>
public sealed record KeepRun(DateTime StartedAt, DateTime? FinishedAt, DeployStatus Status);

public enum KeepAction
{
    /// <summary>It runs, or is on its way (restarting, paused, being removed): Docker's or its owner's.</summary>
    None,
    /// <summary>It should be brought back, but hasn't been down long enough yet.</summary>
    Wait,
    BringBack,
    /// <summary>Stopped on purpose, ended by itself, or never started: left as it is.</summary>
    Leave,
    /// <summary>Its container is gone: made again, as its kind is.</summary>
    MakeAgain,
}

/// <param name="Rule">Which of the rules decided (section 2.4 of the design), for tests and the debug log.</param>
public sealed record KeepVerdict(KeepAction Action, KeepReason? Reason = null, int Rule = 0);

/// <summary>
/// The keep-running check's rules, pure: what a container that isn't running means (bring it back, or leave it), and how
/// often an app is tried before Gitberth gives up on it. An app someone stopped is never brought back: a stop in Gitberth
/// makes it not meant to run, and a docker stop or kill is a stop whatever its program exits with.
/// </summary>
public static class KeepRules
{
    /// <summary>At most this many tries in a row; then Gitberth gives up and tries only every <see cref="QuietEvery"/>.</summary>
    public const int Tries = 4;

    /// <summary>The waits before the second, third and fourth try, each after the one before.</summary>
    public static readonly IReadOnlyList<TimeSpan> Gaps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>Up this long since the last try, the app is fine again: its tries count from nothing next time.</summary>
    public static readonly TimeSpan Steady = TimeSpan.FromMinutes(10);

    /// <summary>After giving up, how often Gitberth still tries, quietly, until someone starts, stops or deploys the app.</summary>
    public static readonly TimeSpan QuietEvery = TimeSpan.FromHours(6);

    /// <summary>A container that stopped this close to Gitberth's own stop stopped with Docker or the host.</summary>
    public static readonly TimeSpan OwnStopWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What to do about a container, first rule that matches. A missing one is to be made again (its two looks in a row are
    /// the check's); a stopped one is brought back only once it's been down for <paramref name="grace"/>: Docker's own
    /// back-off is over by then, and so is a docker restart or Gitberth's own short stop.
    /// </summary>
    public static KeepVerdict Decide(KeepFacts f, DateTime now, TimeSpan grace)
    {
        if (f.State is null) return new(KeepAction.MakeAgain, new KeepReason(KeepCause.Gone), 11);
        if (f.State is "running" or "restarting" or "paused" or "removing") return new(KeepAction.None, Rule: 1);
        if (f.State == "dead") return new(KeepAction.Leave, new KeepReason(KeepCause.Dead), 10);
        // Created and never started (a create Docker couldn't finish, or one that waits): nobody's to start.
        if (f.State != "exited") return new(KeepAction.Leave, Rule: 10);
        var verdict = Exited(f);
        return verdict.Action == KeepAction.BringBack && f.FinishedAt is { } at && now - at < grace ? verdict with { Action = KeepAction.Wait } : verdict;
    }

    private static KeepVerdict Exited(KeepFacts f)
    {
        var at = f.FinishedAt;
        // A daemon or host shutdown stops every container, Gitberth's too, within its stop timeout; a power loss stamps them
        // all at the daemon's next start. A Gitberth upgrade is a new container that never stopped, so it doesn't match.
        if (at is { } stopped && f.OwnStoppedAt is { } own && f.OwnStartedAt is { } started && (stopped - own).Duration() <= OwnStopWindow && stopped < started)
            return new(KeepAction.BringBack, new KeepReason(KeepCause.HostRestarted, f.ExitCode, Policy: f.Policy), 2);
        // A deploy's swap stops the old container; one that failed and couldn't start it again left it so. Not a stop Gitberth
        // knows it didn't make while the deploy ran (a docker stop during a long build): the rules below read that one.
        if (f.Kind == KeepKind.Git && at is { } t && f.LastDeploy is { Status: DeployStatus.Failed or DeployStatus.RolledBack } d
            && t >= d.StartedAt && (d.FinishedAt is not { } end || t <= end) && f.Died is not { ByGitberth: false })
            return new(KeepAction.BringBack, new KeepReason(KeepCause.DeployCutShort, f.ExitCode), 3);
        if (f.OutOfMemory) return new(KeepAction.BringBack, new KeepReason(KeepCause.OutOfMemory, f.ExitCode, Policy: f.Policy), 4);
        if (f.Error is { Length: > 0 } error) return new(KeepAction.BringBack, new KeepReason(KeepCause.CouldntStart, f.ExitCode, error), 5);
        // Heard dying: the event says whether it was a crash, as the charts and crash notices read it.
        if (f.Died is { } died)
            return died.Crashed
                ? new(KeepAction.BringBack, new KeepReason(died.OutOfMemory ? KeepCause.OutOfMemory : KeepCause.Crashed, f.ExitCode, Policy: f.Policy), 6)
                : new(KeepAction.Leave, new KeepReason(died.AskedToStop || f.ExitCode != 0 ? KeepCause.Stopped : KeepCause.Ended, f.ExitCode), 7);
        // Gitberth wasn't watching: the exit code is all there is. 0 is how a program ends; 130, 137 and 143 are SIGINT,
        // SIGKILL and SIGTERM, which a stop sends.
        return f.ExitCode is 0 or 130 or 137 or 143
            ? new(KeepAction.Leave, new KeepReason(f.ExitCode == 0 ? KeepCause.Ended : KeepCause.Stopped, f.ExitCode), 9)
            : new(KeepAction.BringBack, new KeepReason(KeepCause.Crashed, f.ExitCode, Policy: f.Policy), 8);
    }

    /// <summary>
    /// When the next try may be made, after <paramref name="made"/> tries in a row, the last at <paramref name="last"/>:
    /// at once for the first, then 1, 5 and 15 minutes after the one before. Null once all <see cref="Tries"/> were made.
    /// </summary>
    public static DateTime? NextTry(int made, DateTime? last) => made switch
    {
        <= 0 => DateTime.MinValue,
        >= Tries => null,
        _ => (last ?? DateTime.MinValue) + Gaps[made - 1],
    };

    /// <summary>Whether an app that runs has been up long enough since the last try for its tries to count from nothing again.</summary>
    public static bool IsSteady(DateTime? lastTry, DateTime now) => lastTry is not { } t || now - t >= Steady;
}
