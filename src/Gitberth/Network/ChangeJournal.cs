using Gitberth.Apps;

namespace Gitberth.Network;

/// <summary>
/// How far a port change got. Each step is written before the call that does it, so after a restart Gitberth knows what
/// may already have happened, and finishes the change or undoes it.
/// </summary>
public enum ChangeStep
{
    /// <summary>The new container may exist under its temporary name.</summary>
    Creating,
    /// <summary>The original's restart policy may be "no".</summary>
    Disarming,
    /// <summary>The original may be stopped.</summary>
    Stopping,
    /// <summary>The names may be swapped, or half swapped.</summary>
    Renaming,
    /// <summary>The new container may have started under the container's name.</summary>
    Starting,
    Verifying,
    /// <summary>It was stopped: the new container waits for its first start, and the original is kept until that passes.</summary>
    WaitingForStart,
    /// <summary>The new container passed; it may have the original's restart policy, and the original may be gone.</summary>
    RemovingOld,
    /// <summary>
    /// The original is being put back: the new container may be stopping or gone, the names half swapped back. After a
    /// restart of Gitberth this goes on being undone, never finished, however well the new container runs.
    /// </summary>
    RollingBack,
    Finished,
}

public enum ChangeOutcome
{
    /// <summary>Not over: under way, or waiting for the new container's first start.</summary>
    Running,
    Done,
    RolledBack,
    NothingChanged,
    /// <summary>Both containers are kept until the owner chooses: keep the new one, or put the original back.</summary>
    NeedsDecision,
}

/// <summary>
/// One change of one container: its ports, its settings, an update or a rollback of its image. It never holds the
/// container's config or environment, which carry secrets: only names, IDs, the step, the restart policies to put back or
/// to give, the names of the settings it changed and the image references.
/// </summary>
public sealed record ContainerChange
{
    public int Id { get; init; }
    /// <summary>The container's name, which the new container takes over: postgres.</summary>
    public required string ContainerName { get; init; }
    /// <summary>What changed, for "Last change": published 5432/tcp on the LAN.</summary>
    public required string Summary { get; init; }
    public required string OldId { get; init; }
    public string? NewId { get; init; }
    /// <summary>The name the original is moved to while the new container takes its place: postgres_old_0ebf31bbcd07.</summary>
    public required string OldName { get; init; }
    /// <summary>The new container's name until the swap: postgres_new_0ebf31bbcd07.</summary>
    public required string TempName { get; init; }
    public bool WasRunning { get; init; }
    /// <summary>The original's restart policy, as Docker's JSON, to put back: {"Name":"unless-stopped","MaximumRetryCount":0}.</summary>
    public required string OldRestartPolicy { get; init; }
    public ChangeStep Step { get; init; }
    /// <summary>
    /// When the original's stop was asked for (UTC). Docker finishes a stop after its caller has gone, so until the stop
    /// timeout has passed, a restart of Gitberth may find the original still on its way down.
    /// </summary>
    public DateTime? StopAt { get; init; }
    public ChangeOutcome Outcome { get; init; } = ChangeOutcome.Running;
    public int? OfflineMs { get; init; }
    /// <summary>How it ended, as the owner read it.</summary>
    public string? Error { get; init; }
    /// <summary>A banner for the container's page until it's dismissed: after a restart of Gitberth, or while a decision waits.</summary>
    public string? Notice { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    /// <summary>What kind of change it is, for its banner and outcome texts: "An update of postgres was interrupted."</summary>
    public ChangeKind Kind { get; init; } = ChangeKind.Ports;
    /// <summary>The settings it changed, by name (Env,Memory); never their values.</summary>
    public string? Fields { get; init; }
    /// <summary>The restart policy the new container gets once it has passed, as Docker's JSON; null: the original's.</summary>
    public string? NewRestartPolicy { get; init; }
    /// <summary>The image the original ran: its reference (postgres:18), or its ID when it has no tag.</summary>
    public string? ImageBefore { get; init; }
    /// <summary>The image the new container runs, the same way; null when the change kept the image.</summary>
    public string? ImageAfter { get; init; }

    /// <summary>Its containers may not be as they should be yet: it's under way, waits for a start, or waits for the owner.</summary>
    public bool Unfinished => Outcome is ChangeOutcome.Running or ChangeOutcome.NeedsDecision;
}

/// <summary>Where port changes are written down, so one that a restart of Gitberth cut short is finished or undone.</summary>
public interface IChangeJournal
{
    /// <summary>Saves a new change and returns it with its Id.</summary>
    Task<ContainerChange> AddAsync(ContainerChange change, CancellationToken ct);

    /// <summary>Saves the change by its Id.</summary>
    Task UpdateAsync(ContainerChange change, CancellationToken ct);

    /// <summary>Every change that isn't over (<see cref="ContainerChange.Unfinished"/>), oldest first.</summary>
    Task<IReadOnlyList<ContainerChange>> UnfinishedAsync(CancellationToken ct);

    /// <summary>The container's most recent change, for "Last change" and its banner.</summary>
    Task<ContainerChange?> LatestAsync(string container, CancellationToken ct);

    /// <summary>Takes the change's banner down, and nothing else: whatever the change says by then stays as it is.</summary>
    Task DismissNoticeAsync(int id, CancellationToken ct);

    /// <summary>The latest change whose original or new container has this ID, finished or not; null for none.</summary>
    Task<ContainerChange?> WithContainerAsync(string containerId, CancellationToken ct);
}
