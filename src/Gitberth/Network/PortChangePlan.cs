using System.Globalization;
using Gitberth.Apps;

namespace Gitberth.Network;

/// <summary>One CHANGE line: 5432/tcp, Internal → LAN at 10.1.1.80:5432.</summary>
public sealed record PortChangeLine(string Port, string Before, string After)
{
    public string Text => $"{Port}  {Before} → {After}";
}

/// <summary>
/// What a change would do (its ports, its settings, an update, a rollback, or a new container), worked out before anything
/// changes, with every line of the confirmation (section 5.4, and 2.7 of the unified design) in its exact words: the UI
/// only lays them out. Its lines never hold a secret, and it never holds the container's config: the create body is built
/// again from a fresh inspect when the change is applied. The settings asked for are held in memory only
/// (<see cref="Request"/>, <see cref="Create"/>), never persisted or logged.
/// </summary>
public sealed record PortChangePlan
{
    public ChangeKind Kind { get; init; } = ChangeKind.Ports;
    public IReadOnlySet<SettingField> Fields { get; init; } = new HashSet<SettingField>();
    /// <summary>The CHANGE lines for the settings other than ports, values masked.</summary>
    public IReadOnlyList<SettingLine> Settings { get; init; } = [];
    /// <summary>Only live fields: docker update, no recreate.</summary>
    public bool LiveOnly { get; init; }
    public string? ImageAfter { get; init; }
    /// <summary>Held in memory only, never persisted or logged: the wanted settings (env values included).</summary>
    public ContainerEditRequest? Request { get; init; }
    public NewContainerRequest? Create { get; init; }

    public required string Container { get; init; }

    /// <summary>Whether Gitberth may recreate it (section 7.3).</summary>
    public required RecreateVerdict Verdict { get; init; }

    /// <summary>Why Apply is off: the verdict's line, a port problem, or a host port that's taken. Null when it isn't.</summary>
    public string? Blocked { get; init; }

    /// <summary>The CHANGE lines: one per port whose bindings change.</summary>
    public IReadOnlyList<PortChangeLine> Changes { get; init; } = [];

    /// <summary>The KEEPS line: Its name, image postgres:18 (3a4264520e12, not pulled again), command, environment, ….</summary>
    public string Keeps { get; init; } = "";

    /// <summary>The CHANGES line.</summary>
    public string AlsoChanges { get; init; } = "Its container ID, start time, restart count and log so far.";

    /// <summary>The IMPACT lines, the restart line first.</summary>
    public IReadOnlyList<string> Impact { get; init; } = [];

    /// <summary>The line under the lines: what happens if the new container fails.</summary>
    public string Footer { get; init; } = "";

    /// <summary>"about 5 s" for a database it recognizes, "about 3 s" for anything else; null when it's stopped.</summary>
    public TimeSpan? OfflineEstimate { get; init; }

    /// <summary>The steps the change takes, as the progress shows them once done (section 5.5).</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>For a Compose-managed container: the ports in its file, for the bindings asked for. The command is <see cref="RecreateVerdict.Compose"/>'s.</summary>
    public string? ComposeSnippet { get; init; }

    /// <summary>Every port the container should have afterwards, as asked.</summary>
    public IReadOnlyList<PortRule> Ports { get; init; } = [];

    /// <summary>The bindings it gets.</summary>
    public IReadOnlyList<NewBinding> Bindings { get; init; } = [];

    /// <summary>The container as it was planned: a recreate since makes Apply refuse.</summary>
    public string? ContainerId { get; init; }

    public bool Running { get; init; }

    /// <summary>For the journal and "Last change": published 5432/tcp on the LAN.</summary>
    public string Summary { get; init; } = "";

    public bool CanApply => Blocked is null && Verdict.Allowed && (Changes.Count > 0 || Settings.Count > 0 || Kind == ChangeKind.Create);

    /// <summary>
    /// What a plan prints as: its container, kind and lines. A record's own ToString would list <see cref="Request"/> and
    /// <see cref="Create"/>, whose environment holds secrets, so it's never used.
    /// </summary>
    public override string ToString() =>
        $"{Kind} {Container}: {string.Join("; ", Changes.Select(c => c.Text).Concat(Settings.Select(s => $"{s.Label}: {s.Before} → {s.After}")))}"
        + (Blocked is null ? "" : $" ({Blocked})");
}

public enum PortChangeOutcome
{
    /// <summary>The new container runs with the new ports and passed its checks; the original is gone.</summary>
    Done,
    /// <summary>It was stopped: it's recreated stopped, and the original is kept until its first start passes.</summary>
    WaitsForStart,
    NothingChanged,
    /// <summary>The new container failed; the original is back as it was.</summary>
    RolledBack,
    /// <summary>Both containers are kept: the owner keeps the new one or puts the original back.</summary>
    NeedsDecision,
}

/// <param name="Message">The result line, in the design's words.</param>
public sealed record PortChangeResult(PortChangeOutcome Outcome, string Message)
{
    /// <summary>How long the container didn't answer, from the stop to the first connect that worked.</summary>
    public TimeSpan? Offline { get; init; }

    /// <summary>The failed new container's last output, for [Show its last output].</summary>
    public string? LastOutput { get; init; }

    /// <summary>Done, but nothing answered on its LAN ports before or after: it may listen only inside, or elsewhere.</summary>
    public bool NothingAnswers { get; init; }

    public bool Ok => Outcome is PortChangeOutcome.Done or PortChangeOutcome.WaitsForStart;
}

public enum StepState { Running, Done, Failed }

/// <summary>A step of a change as it runs: "Stopped the old container (SIGINT, waits up to 60 s)", done in 1.4 s.</summary>
/// <param name="Number">1-based; with <paramref name="Count"/> it gives "step 4 of 9".</param>
/// <param name="Next">Under a running step, what comes after it: "Next: it stays up for 15 s, then 10.1.1.80:5432 must answer."</param>
public sealed record PortChangeStep(int Number, int Count, string Text, StepState State, TimeSpan? Took = null, string? Next = null)
{
    /// <summary>"1.4 s".</summary>
    public string? TookText => Took is { } t ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : null;
}

/// <summary>
/// A port change running in the background, so closing the dialog doesn't stop it. The dialog, the Ports card and the
/// Addresses card read its steps, and hear when they change.
/// </summary>
public sealed class PortChangeJob : IProgress<PortChangeStep>
{
    private readonly List<PortChangeStep> _steps = [];

    internal PortChangeJob(PortChangePlan plan) => Plan = plan;

    public PortChangePlan Plan { get; }

    public string Container => Plan.Container;

    /// <summary>Each step as it stands now, in order.</summary>
    public IReadOnlyList<PortChangeStep> Steps
    {
        get
        {
            lock (_steps) return _steps.ToList();
        }
    }

    /// <summary>The step under way, for "Recreating postgres: step 4 of 9…"; null between steps and once it's over.</summary>
    public PortChangeStep? Current => Steps.LastOrDefault(s => s.State == StepState.Running);

    public Task<PortChangeResult> Completion { get; internal set; } = Task.FromResult(new PortChangeResult(PortChangeOutcome.NothingChanged, ""));

    /// <summary>Raised on any thread when a step starts or ends, and when the change is over.</summary>
    public event Action? Changed;

    public void Report(PortChangeStep value)
    {
        lock (_steps)
        {
            var at = _steps.FindIndex(s => s.Number == value.Number);
            if (at >= 0) _steps[at] = value;
            else _steps.Add(value);
        }
        Changed?.Invoke();
    }

    internal void Finished() => Changed?.Invoke();
}
