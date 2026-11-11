using System.Globalization;
using Gitberth.Data;
using Gitberth.Git;

namespace Gitberth.Previews;

/// <summary>An open pull request, as GitHub lists it.</summary>
/// <param name="HeadRef">The branch it comes from (in the fork, for one from a fork).</param>
/// <param name="FromFork">Its branch is in another repo: someone else's code.</param>
public sealed record PullRequestInfo(int Number, string Title, string HeadRef, string HeadSha, bool FromFork, string? Url = null);

/// <summary>Something that should get a preview: a branch, or a pull request.</summary>
public sealed record PreviewTarget(PreviewKind Kind, string Branch, int? PullRequest, string? Title, string HeadSha, bool FromFork)
{
    public string Ref => PreviewNames.Ref(Kind, Branch, PullRequest);
    public string SkipKey => PreviewPlanner.SkipKeyOf(Kind, Branch, PullRequest);
}

/// <summary>A preview Gitberth has, as the planner needs it.</summary>
public sealed record ExistingPreview(int AppId, PreviewKind Kind, string Branch, int? PullRequest, string? Title, string? HeadSha, bool FromFork, DateTime LastActivityAt)
{
    public string SkipKey => PreviewPlanner.SkipKeyOf(Kind, Branch, PullRequest);
    public string Label => PreviewNames.Label(Kind, Branch, PullRequest);
}

/// <summary>A preview's newest commit (and its pull request's title) changed.</summary>
/// <param name="NewCommit">The commit moved: the preview's expiry starts again.</param>
public sealed record PreviewUpdate(int AppId, string HeadSha, string? Title, bool NewCommit);

/// <summary>A preview to remove, and why, as its parent's Activity says it.</summary>
/// <param name="Remember">
/// Don't make it again for the same commit: it expired, or was removed by hand. A new commit brings it back.
/// </param>
public sealed record PreviewRemoval(int AppId, string Label, string Reason, bool Remember);

/// <param name="Skips">The commits not to preview, by branch or pull request (and whether branches' first commits were noted).</param>
public sealed record PreviewPlan(IReadOnlyList<PreviewTarget> Create, IReadOnlyList<PreviewUpdate> Update, IReadOnlyList<PreviewRemoval> Remove,
    IReadOnlyDictionary<string, string> Skips);

/// <summary>A parent's preview settings, as the planner reads them.</summary>
/// <param name="ProductionBranch">The parent's own branch: it never gets a preview.</param>
public sealed record PreviewSettingsFacts(PreviewMode Mode, string ProductionBranch, bool Forks, int ExpiryDays);

/// <summary>
/// Which branches and pull requests of a Git app get a preview, which previews go, and which wait their turn: pure, from
/// what the repo lists and what Gitberth has, so every rule is tested on its own.
/// <list type="bullet">
/// <item>A pull request (an open one, from this repo, or from a fork when the app allows forks) gets a preview.</item>
/// <item>A branch other than the production one gets a preview when it gets a new commit after branch previews were
/// turned on: the first time the planner sees branches it notes their commits, so turning previews on doesn't build
/// every old branch at once. A branch a pull request previews gets no preview of its own, and once the pull request is
/// closed or merged, only a later commit gives it one.</item>
/// <item>A preview goes when its pull request closes or is merged, its branch is deleted, previews (or that kind of
/// them) are turned off, forks are turned off for a fork's pull request, or it has had no new commit for the expiry
/// days.</item>
/// </list>
/// </summary>
public static class PreviewPlanner
{
    /// <summary>Set in the skips once branches' commits were noted, so only commits after that get a preview.</summary>
    public const string BaselineKey = "@branches";

    /// <summary>Why a pull request's preview goes when it's no longer open (GitHub is asked which, when it can be).</summary>
    public const string ClosedReason = "the pull request was closed or merged";

    /// <summary>Why a fork's pull request's preview goes when forks are turned off.</summary>
    public const string ForksOffReason = "pull requests from forks aren't previewed any more";

    public static string SkipKeyOf(PreviewKind kind, string branch, int? pullRequest) =>
        kind == PreviewKind.PullRequest ? "pr:" + pullRequest?.ToString(CultureInfo.InvariantCulture) : "branch:" + branch;

    /// <param name="pulls">The open pull requests; null when GitHub couldn't be asked (nothing is made or removed for pull requests then).</param>
    /// <param name="pullHeads">Every pull request's head as the repo lists it, fresher than <paramref name="pulls"/> between two asks.</param>
    /// <param name="skips">The commits not to preview, as the last plan left them.</param>
    public static PreviewPlan Plan(PreviewSettingsFacts parent, IReadOnlyList<RemoteBranch> branches, IReadOnlyList<PullRequestInfo>? pulls,
        IReadOnlyList<RemotePull> pullHeads, IReadOnlyList<ExistingPreview> existing, IReadOnlyDictionary<string, string> skips, DateTime now)
    {
        var create = new List<PreviewTarget>();
        var update = new List<PreviewUpdate>();
        var remove = new List<PreviewRemoval>();
        var next = new Dictionary<string, string>(skips, StringComparer.Ordinal);
        var gone = new HashSet<int>();

        void Remove(ExistingPreview p, string reason, bool remember = false)
        {
            remove.Add(new PreviewRemoval(p.AppId, p.Label, reason, remember));
            gone.Add(p.AppId);
            if (remember && p.HeadSha is { } sha) next[p.SkipKey] = sha;
        }

        if (parent.Mode == PreviewMode.Off)
        {
            foreach (var p in existing) Remove(p, "previews were turned off");
            return new PreviewPlan(create, update, remove, new Dictionary<string, string>());
        }

        var heads = pullHeads.GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.Last().Sha);
        // The open pull requests, each at its newest head.
        var open = pulls?.Select(p => heads.TryGetValue(p.Number, out var sha) ? p with { HeadSha = sha } : p).ToList();
        var eligible = open?.Where(p => !p.FromFork || parent.Forks).ToList();
        // A branch a pull request of this repo previews has no preview of its own.
        var previewedByPull = (eligible?.Where(p => !p.FromFork).Select(p => p.HeadRef)
                ?? existing.Where(p => p.Kind == PreviewKind.PullRequest && !p.FromFork).Select(p => p.Branch))
            .ToHashSet(StringComparer.Ordinal);

        // ---- Pull requests ----
        foreach (var p in existing.Where(p => p.Kind == PreviewKind.PullRequest))
        {
            // Forks turned off: a fork's preview goes, whether or not GitHub can be asked, and never takes a new commit.
            if (p.FromFork && !parent.Forks)
            {
                Remove(p, ForksOffReason);
                continue;
            }
            if (open is null)
            {
                // GitHub can't be asked: the repo's own list still has its newest commit.
                if (p.PullRequest is int n && heads.TryGetValue(n, out var head) && head != p.HeadSha) update.Add(new PreviewUpdate(p.AppId, head, p.Title, NewCommit: true));
                continue;
            }
            var pull = open.FirstOrDefault(o => o.Number == p.PullRequest);
            if (pull is null) Remove(p, ClosedReason);
            else if (pull.FromFork && !parent.Forks) Remove(p, ForksOffReason);
            else if (pull.HeadSha != p.HeadSha || pull.Title != p.Title) update.Add(new PreviewUpdate(p.AppId, pull.HeadSha, pull.Title, pull.HeadSha != p.HeadSha));
        }
        foreach (var pull in eligible ?? [])
        {
            if (existing.Any(p => p.Kind == PreviewKind.PullRequest && p.PullRequest == pull.Number)) continue;
            var target = new PreviewTarget(PreviewKind.PullRequest, pull.HeadRef, pull.Number, pull.Title, pull.HeadSha, pull.FromFork);
            if (skips.TryGetValue(target.SkipKey, out var skipped) && skipped == pull.HeadSha) continue;
            create.Add(target);
        }

        // ---- Branches ----
        var branchShas = branches.GroupBy(b => b.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Sha, StringComparer.Ordinal);
        if (parent.Mode != PreviewMode.PullRequestsAndBranches)
        {
            foreach (var p in existing.Where(p => p.Kind == PreviewKind.Branch)) Remove(p, "branch previews were turned off");
            // Turned on again later, branches' commits are noted again then.
            foreach (var key in next.Keys.Where(k => k == BaselineKey || k.StartsWith("branch:", StringComparison.Ordinal)).ToList()) next.Remove(key);
        }
        else
        {
            var candidates = branches.Where(b => b.Name != parent.ProductionBranch && !previewedByPull.Contains(b.Name)).ToList();
            // A branch a pull request previews, and the production branch, have their commit noted on every look: when the
            // pull request is merged and its branch stays (or production moves to another branch), the branch gets a
            // preview only with a later commit.
            foreach (var b in branches.Where(b => b.Name == parent.ProductionBranch || previewedByPull.Contains(b.Name))) next["branch:" + b.Name] = b.Sha;
            // The first look: every branch's commit is noted, and only a later one gets a preview.
            var first = !skips.ContainsKey(BaselineKey);
            if (first)
            {
                next[BaselineKey] = "1";
                foreach (var b in candidates) next["branch:" + b.Name] = b.Sha;
            }
            foreach (var p in existing.Where(p => p.Kind == PreviewKind.Branch))
            {
                if (!branchShas.TryGetValue(p.Branch, out var sha)) Remove(p, "the branch was deleted");
                else if (p.Branch == parent.ProductionBranch) Remove(p, "it's the production branch now");
                else if (previewedByPull.Contains(p.Branch)) Remove(p, "a pull request previews the branch now");
                else if (sha != p.HeadSha) update.Add(new PreviewUpdate(p.AppId, sha, p.Title, NewCommit: true));
            }
            if (!first)
            {
                foreach (var b in candidates)
                {
                    if (existing.Any(p => p.Kind == PreviewKind.Branch && p.Branch == b.Name)) continue;
                    var target = new PreviewTarget(PreviewKind.Branch, b.Name, null, null, b.Sha, FromFork: false);
                    if (skips.TryGetValue(target.SkipKey, out var skipped) && skipped == b.Sha) continue;
                    create.Add(target);
                }
            }
        }

        // ---- Expiry: no new commit for the expiry days ----
        var moved = update.Where(u => u.NewCommit).Select(u => u.AppId).ToHashSet();
        foreach (var p in existing.Where(p => !gone.Contains(p.AppId) && !moved.Contains(p.AppId)))
        {
            if (now - p.LastActivityAt >= TimeSpan.FromDays(Math.Max(1, parent.ExpiryDays)))
                Remove(p, $"no new commit for {parent.ExpiryDays} {(parent.ExpiryDays == 1 ? "day" : "days")}", remember: true);
        }

        // ---- What's remembered: only for branches and pull requests that are still there ----
        foreach (var key in next.Keys.ToList())
        {
            if (key.StartsWith("branch:", StringComparison.Ordinal) && !branchShas.ContainsKey(key["branch:".Length..])) next.Remove(key);
            else if (key.StartsWith("pr:", StringComparison.Ordinal) && open is not null
                && !open.Any(p => SkipKeyOf(PreviewKind.PullRequest, p.HeadRef, p.Number) == key)) next.Remove(key);
        }
        return new PreviewPlan(create, update, remove, next);
    }

    /// <summary>A preview as the deploy decision sees it.</summary>
    /// <param name="Busy">A deploy of it is queued or under way.</param>
    public sealed record Run(int AppId, string? CurrentImage, DesiredState DesiredState, bool Busy, DateTime CreatedAt);

    /// <summary>Whether a preview takes one of its parent's places: it runs (or is meant to and has an image), or it's deploying.</summary>
    public static bool Occupies(Run p) => p.Busy || p.CurrentImage is not null && p.DesiredState == DesiredState.Running;

    /// <summary>
    /// Which of the previews that want a deploy get one now: one that already has its place does; one that hasn't waits
    /// while <paramref name="limit"/> previews have theirs, oldest first, and starts when one goes.
    /// </summary>
    public static List<int> Admit(IReadOnlyList<Run> all, IEnumerable<int> wanting, int limit)
    {
        var used = all.Count(Occupies);
        var admitted = new List<int>();
        var byId = all.ToDictionary(p => p.AppId);
        foreach (var id in wanting.Where(byId.ContainsKey).OrderBy(id => byId[id].CreatedAt).ThenBy(id => id))
        {
            var p = byId[id];
            if (p.Busy || p.DesiredState != DesiredState.Running) continue;
            if (!Occupies(p))
            {
                if (used >= Math.Max(1, limit)) continue;
                used++;
            }
            admitted.Add(id);
        }
        return admitted;
    }
}
