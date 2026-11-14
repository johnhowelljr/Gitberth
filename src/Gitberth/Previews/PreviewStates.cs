using Gitberth.Data;

namespace Gitberth.Previews;

/// <summary>A preview's state, as its parent's Previews tab shows it.</summary>
public enum PreviewState { Building, Ready, Failed, Waiting, Stopped }

/// <summary>What a preview's state is worked out from.</summary>
/// <param name="Deploy">
/// Its newest deploy that wasn't skipped (<see cref="PreviewService.ListAsync"/>); null before the first. A skipped commit
/// left the preview as it was, so it never says anything about its state.
/// </param>
/// <param name="Running">Its container runs.</param>
/// <param name="Occupied">How many of the parent's previews have their place (<see cref="PreviewPlanner.Occupies"/>).</param>
public sealed record PreviewFacts(Deployment? Deploy, bool Running, DesiredState DesiredState, string? CurrentImage, int Occupied, int Limit);

public static class PreviewStates
{
    /// <summary>Building while a deploy runs; Stopped when someone stopped it; Ready while it runs; Failed when its deploy failed and nothing runs; Waiting before its first deploy.</summary>
    public static (PreviewState State, string Text) Of(PreviewFacts f)
    {
        // A skipped deploy changed nothing: the preview is as its deploy before left it.
        if (f.Deploy is { Status: DeployStatus.Skipped }) f = f with { Deploy = null };
        if (f.Deploy is { IsActive: true } active)
            return (PreviewState.Building, active.Status switch
            {
                DeployStatus.Cloning => "Fetching the code…",
                DeployStatus.Building => "Building…",
                DeployStatus.Starting => "Starting…",
                _ => "Queued…",
            });
        if (f.DesiredState == DesiredState.Stopped) return (PreviewState.Stopped, "Stopped");
        if (f.Running)
            return f.Deploy is { Status: DeployStatus.Failed or DeployStatus.RolledBack }
                ? (PreviewState.Ready, "Ready; its newest commit didn't deploy")
                : (PreviewState.Ready, "Ready");
        if (f.Deploy is { Status: DeployStatus.Failed or DeployStatus.RolledBack or DeployStatus.Cancelled }) return (PreviewState.Failed, "Failed");
        if (f.CurrentImage is null)
            return f.Occupied >= f.Limit
                ? (PreviewState.Waiting, $"Waiting: {f.Occupied} {(f.Occupied == 1 ? "preview runs" : "previews run")} already")
                : (PreviewState.Waiting, "Waiting for its first deploy");
        return (PreviewState.Stopped, "Not running");
    }
}
