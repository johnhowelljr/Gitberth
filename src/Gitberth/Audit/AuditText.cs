using System.Globalization;
using Gitberth.Data;

namespace Gitberth.Audit;

/// <summary>The audit log's sentences that more than one place says: deploys, lists, on and off.</summary>
public static class AuditText
{
    /// <summary>What a queued deploy is for, as its entry says it: "Queued a deploy of web: a new commit, abc1234."</summary>
    public static string Queued(string app, Deployment dep, int? fromDeployment = null)
    {
        var sha = dep.ShortSha;
        var at = sha.Length > 0 ? $" ({sha})" : "";
        var noCache = dep.NoCache ? " without the build cache" : "";
        return dep.Trigger switch
        {
            DeployTrigger.Created => $"Queued the first deploy of {app}{noCache}.",
            DeployTrigger.Start => $"Queued a deploy of {app} to start it.",
            DeployTrigger.ConfigChange => $"Queued a deploy without a build of {app}, to apply its settings.",
            DeployTrigger.Rollback => fromDeployment is int from ? $"Queued a rollback of {app} to deploy #{N(from)}{at}." : $"Queued a rollback of {app}{at}.",
            DeployTrigger.Poll => $"Queued a deploy of {app}: a new commit{at}.",
            DeployTrigger.Retry => $"Queued another try of {app}{at}, whose last build failed.",
            DeployTrigger.Restart => $"Queued the scheduled restart of {app}.",
            DeployTrigger.Webhook => $"Queued a deploy of {app}: {WebhookHost(dep)} said its branch changed{at}.",
            DeployTrigger.Promote => $"Queued the promotion of a preview's commit to {app}{at}.",
            DeployTrigger.Hook => $"Queued a deploy of {app} for the deploy hook {dep.HookName ?? "?"}{noCache}.",
            DeployTrigger.Recover => $"Queued a deploy of {app} to bring it back: its container was gone.",
            // [builder B, B6]
            DeployTrigger.Upload => $"Queued a deploy of {app}: a new upload{at}.",
            _ => $"Queued a deploy of {app}{noCache}.",
        };
    }

    /// <summary>A deploy the queue got to, as its entry says it: "Started deploy #12 of web (new commit, abc1234)."</summary>
    public static string Started(string app, Deployment dep)
    {
        var sha = dep.ShortSha;
        return $"Started deploy #{N(dep.Id)} of {app} ({Trigger(dep.Trigger)}{(sha.Length > 0 ? $", {sha}" : "")}).";
    }

    /// <summary>A queued deploy cancelled before it started, because of <paramref name="why"/> ("the app was stopped").</summary>
    public static string CancelledBeforeStart(string app, Deployment dep, string why) => $"Deploy #{N(dep.Id)} of {app} was cancelled before it started: {why}.";

    /// <summary>The action and sentence of a deploy that ended, and whether it went wrong.</summary>
    public static (string Action, string Summary, AuditOutcome Outcome) Ended(string app, Deployment dep)
    {
        var id = $"#{N(dep.Id)}";
        var sha = dep.ShortSha;
        var why = Reason(dep.Error);
        return dep.Status switch
        {
            DeployStatus.Succeeded => ("app.deploy.succeeded", sha.Length > 0 ? $"Deploy {id} of {app} succeeded: {sha} is live." : $"Deploy {id} of {app} succeeded.", AuditOutcome.Succeeded),
            DeployStatus.RolledBack => ("app.deploy.rolledback", $"Deploy {id} of {app} didn't pass its checks, so the previous version runs again{why}.", AuditOutcome.Failed),
            DeployStatus.Cancelled => ("app.deploy.cancelled", $"Deploy {id} of {app} was cancelled{why}.", AuditOutcome.Failed),
            DeployStatus.Skipped => ("app.deploy.skipped", $"Deploy {id} of {app} was skipped: {Lower(dep.SkipReason ?? "nothing to build")}.", AuditOutcome.Succeeded),
            _ => ("app.deploy.failed", $"Deploy {id} of {app} failed{why}.", AuditOutcome.Failed),
        };
    }

    /// <summary>
    /// Whose webhook asked for a deploy: GitLab's when the deploy was asked for by GitLab (its <see cref="Deployment.RequestedBy"/>),
    /// else GitHub's, as every one was before GitLab.
    /// </summary>
    public static string WebhookHost(Deployment dep) => AuditActors.Parse(dep.RequestedBy)?.Kind == AuditActorKind.GitLab ? "GitLab" : "GitHub";

    /// <summary>The deploy's trigger in the words the deploy pages use: a GitLab push's is "GitLab webhook".</summary>
    public static string Trigger(Deployment dep) =>
        dep.Trigger == DeployTrigger.Webhook && WebhookHost(dep) == "GitLab" ? "GitLab webhook" : Trigger(dep.Trigger);

    /// <summary>The trigger in the words the deploy pages use.</summary>
    public static string Trigger(DeployTrigger trigger) => trigger switch
    {
        DeployTrigger.Created => "first deploy",
        DeployTrigger.Manual => "by hand",
        DeployTrigger.Start => "start",
        DeployTrigger.ConfigChange => "settings change",
        DeployTrigger.Rollback => "rollback",
        DeployTrigger.Poll => "new commit",
        DeployTrigger.Retry => "retry",
        DeployTrigger.Restart => "scheduled restart",
        DeployTrigger.Webhook => "GitHub webhook",
        DeployTrigger.Promote => "promote",
        DeployTrigger.Hook => "deploy hook",
        DeployTrigger.Recover => "brought back",
        // [builder B, B6]
        DeployTrigger.Upload => "new upload",
        _ => trigger.ToString(),
    };

    /// <summary>"on" or "off".</summary>
    public static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>"a, b and c".</summary>
    public static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>A number as the log writes it.</summary>
    public static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    // ": the reason", without a full stop of its own, for the end of a sentence.
    private static string Reason(string? error) =>
        string.IsNullOrWhiteSpace(error) || error == "Cancelled" ? "" : ": " + Lower(error.Trim().TrimEnd('.'));

    private static string Lower(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
