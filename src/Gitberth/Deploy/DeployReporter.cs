using Gitberth.Data;

namespace Gitberth.Deploy;

/// <summary>
/// A deploy as the outside world hears of it: GitHub's commit statuses and pull request comments. The pipeline reports when
/// a deploy's commit is known and when the deploy ends. The reporter looks up the rest (the app or preview, its address)
/// itself.
/// </summary>
/// <param name="AppId">The app deployed: a preview's own row for a preview (<see cref="AppDefinition.ParentAppId"/>).</param>
/// <param name="CommitSha">The commit built or run; null when the deploy ended before it was known (a failed clone).</param>
/// <param name="Status">Building while it runs; how it ended in <see cref="IDeployReporter.FinishedAsync"/>.</param>
/// <param name="Error">Why it failed, as the deploy log says it; never a secret value (the log masks them).</param>
/// <param name="Skip">Why it was skipped, when <paramref name="Status"/> is Skipped.</param>
public sealed record DeployReport(int AppId, int DeploymentId, DeployTrigger Trigger, string? CommitSha, DeployStatus Status, string? Error,
    SkipKind? Skip = null)
{
    /// <summary>The deploy hook that started it, by name, when <see cref="Trigger"/> is Hook.</summary>
    public string? HookName { get; init; }
}

/// <summary>What the pipeline tells about each deploy. It must never throw into a deploy, nor hold one up for long.</summary>
public interface IDeployReporter
{
    /// <summary>The deploy knows its commit and is building it.</summary>
    Task StartedAsync(DeployReport report, CancellationToken ct);

    /// <summary>The deploy ended: <see cref="DeployReport.Status"/> says how.</summary>
    Task FinishedAsync(DeployReport report, CancellationToken ct);
}

/// <summary>Reports nowhere: used until something is set up to hear about deploys, and in tests.</summary>
public sealed class NoDeployReporter : IDeployReporter
{
    public Task StartedAsync(DeployReport report, CancellationToken ct) => Task.CompletedTask;
    public Task FinishedAsync(DeployReport report, CancellationToken ct) => Task.CompletedTask;
}
