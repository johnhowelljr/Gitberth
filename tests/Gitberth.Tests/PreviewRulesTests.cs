using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>Preview deployments' rules, each on its own: names, domains, which branches and pull requests get one, and the limit.</summary>
public class PreviewRulesTests
{
    // ---- Names ----

    [Fact]
    public void A_pull_requests_preview_is_named_after_its_number_and_a_branchs_after_the_branch()
    {
        Assert.Equal("orb-pr-12", PreviewNames.Slug("orb", PreviewKind.PullRequest, "feature/login", 12));
        Assert.Equal("orb-feature-login-page", PreviewNames.Slug("orb", PreviewKind.Branch, "feature/Login_Page", null));
        Assert.Equal("orb · PR #12", PreviewNames.Name("orb", PreviewKind.PullRequest, "feature/login", 12));
        Assert.Equal("orb · feature/login", PreviewNames.Name("orb", PreviewKind.Branch, "feature/login", null));
        Assert.Equal("refs/pull/12/head", PreviewNames.Ref(PreviewKind.PullRequest, "feature/login", 12));
        Assert.Equal("refs/heads/feature/login", PreviewNames.Ref(PreviewKind.Branch, "feature/login", null));
    }

    [Theory]
    [InlineData("a-rather-long-app-name-for-a-preview", "renovate/major-typescript-eslint-monorepo")]
    [InlineData("orb", "dependabot/npm_and_yarn/packages/web/next-15.5.7")]
    [InlineData("x", "___")]
    public void A_long_slug_is_cut_to_forty_characters_with_a_hash_and_never_has_an_underscore(string parent, string branch)
    {
        var slug = PreviewNames.Slug(parent, PreviewKind.Branch, branch, null);
        Assert.True(slug.Length <= PreviewNames.MaxSlug, slug);
        Assert.Matches("^[a-z0-9-]+$", slug);
        Assert.DoesNotContain('_', slug);
        Assert.False(slug.EndsWith('-'));
        // Another salt gives another name, for when the first is taken.
        Assert.NotEqual(slug, PreviewNames.Slug(parent, PreviewKind.Branch, branch, null, salt: 1));
    }

    [Fact]
    public void Two_long_branches_that_start_alike_get_different_slugs()
    {
        var a = PreviewNames.Slug("kanban", PreviewKind.Branch, "feature/a-very-long-branch-name-about-login-one", null);
        var b = PreviewNames.Slug("kanban", PreviewKind.Branch, "feature/a-very-long-branch-name-about-login-two", null);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("app.example.com", PreviewKind.PullRequest, "x", 12, "app-pr-12.example.com")]
    [InlineData("app.example.com", PreviewKind.Branch, "feature/login", null, "app-git-feature-login.example.com")]
    [InlineData("example.com", PreviewKind.PullRequest, "x", 12, "orb-pr-12.example.com")]
    [InlineData("example.co.uk", PreviewKind.PullRequest, "x", 7, "orb-pr-7.example.co.uk")]
    [InlineData("shop.example.co.uk", PreviewKind.PullRequest, "x", 7, "shop-pr-7.example.co.uk")]
    [InlineData("App.Example.com.", PreviewKind.PullRequest, "x", 3, "app-pr-3.example.com")]
    public void A_previews_domain_is_one_level_below_the_zone(string main, PreviewKind kind, string branch, int? pr, string expected) =>
        Assert.Equal(expected, PreviewNames.Hostname(main, "orb", kind, branch, pr));

    [Fact]
    public void A_domain_label_is_cut_to_sixty_three_characters()
    {
        var host = PreviewNames.Hostname("app.example.com", "orb", PreviewKind.Branch, new string('b', 80), null)!;
        var label = host.Split('.')[0];
        Assert.True(label.Length <= PreviewNames.MaxLabel, label);
        Assert.EndsWith(".example.com", host);
    }

    // ---- What git lists ----

    [Fact]
    public void Ls_remote_lists_pull_request_heads_but_not_their_merge_refs()
    {
        var remote = GitClient.ParseLsRemote(
            "ref: refs/heads/main\tHEAD\n1111111111111111111111111111111111111111\tHEAD\n1111111111111111111111111111111111111111\trefs/heads/main\n"
            + "2222222222222222222222222222222222222222\trefs/heads/feature/login\n3333333333333333333333333333333333333333\trefs/pull/12/head\n"
            + "4444444444444444444444444444444444444444\trefs/pull/12/merge\n5555555555555555555555555555555555555555\trefs/tags/v1\n");
        Assert.Equal("main", remote.DefaultBranch);
        Assert.Equal(["feature/login", "main"], remote.Branches.Select(b => b.Name));
        Assert.Equal([new RemotePull(12, "3333333333333333333333333333333333333333")], remote.PullHeads);
    }

    [Theory]
    [InlineData("refs/heads/main", true)]
    [InlineData("refs/heads/feature/login", true)]
    [InlineData("refs/pull/12/head", true)]
    [InlineData("refs/pull/12/merge", false)]
    [InlineData("--upload-pack=x", false)]
    [InlineData("refs/heads/a..b", false)]
    [InlineData("refs/heads/", false)]
    [InlineData("main", false)]
    public void Only_a_branch_or_a_pull_requests_head_is_fetched(string gitRef, bool safe) => Assert.Equal(safe, GitClient.IsSafeRef(gitRef));

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("1d2948c", true)]
    [InlineData("1d2948", false)]
    [InlineData("--upload-pack=x", false)]
    [InlineData("1d2948cz", false)]
    [InlineData("refs/heads/main", false)]
    public void Only_a_commit_id_is_fetched_as_a_commit(string commit, bool sha) => Assert.Equal(sha, GitClient.IsSha(commit));

    [Fact]
    public void A_pull_request_from_a_fork_is_told_apart_and_one_from_a_deleted_fork_counts_as_one()
    {
        const string json = """
            [
              {"number": 12, "title": "Add login", "html_url": "https://github.com/acme/orb/pull/12",
               "head": {"ref": "feature/login", "sha": "aaaa", "repo": {"full_name": "acme/orb"}}, "base": {"repo": {"full_name": "acme/orb"}}},
              {"number": 13, "title": "Fix typo", "head": {"ref": "patch-1", "sha": "bbbb", "repo": {"full_name": "someone/orb"}}, "base": {"repo": {"full_name": "acme/orb"}}},
              {"number": 14, "title": "Old", "head": {"ref": "x", "sha": "cccc", "repo": null}, "base": {"repo": {"full_name": "acme/orb"}}}
            ]
            """;
        var pulls = GitHubPulls.Parse(json, "acme", "orb");
        Assert.Equal([false, true, true], pulls.Select(p => p.FromFork));
        Assert.Equal(new PullRequestInfo(12, "Add login", "feature/login", "aaaa", false, "https://github.com/acme/orb/pull/12"), pulls[0]);
        Assert.Throws<GitHubApiException>(() => GitHubPulls.Parse("{\"message\":\"Not Found\"}", "acme", "orb"));
    }

    // ---- Which branches and pull requests get a preview ----

    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccccccccccc";

    private static PreviewSettingsFacts Settings(PreviewMode mode = PreviewMode.PullRequestsAndBranches, bool forks = false, int days = 7) =>
        new(mode, "main", forks, days);

    private static readonly Dictionary<string, string> Noted = new() { [PreviewPlanner.BaselineKey] = "1" };

    private static PreviewPlan Plan(PreviewSettingsFacts settings, RemoteBranch[] branches, PullRequestInfo[]? pulls = null, ExistingPreview[]? existing = null,
        IReadOnlyDictionary<string, string>? skips = null, RemotePull[]? heads = null) =>
        PreviewPlanner.Plan(settings, branches, pulls, heads ?? [], existing ?? [], skips ?? Noted, Now);

    [Fact]
    public void Every_open_pull_request_gets_a_preview_but_one_from_a_fork_only_when_forks_are_allowed()
    {
        PullRequestInfo[] pulls = [new(12, "Add login", "feature/login", A, false), new(13, "Fix typo", "patch-1", B, FromFork: true)];

        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], pulls);
        Assert.Equal(new int?[] { 12 }, plan.Create.Select(c => c.PullRequest));
        Assert.Equal("refs/pull/12/head", plan.Create[0].Ref);

        plan = Plan(Settings(PreviewMode.PullRequests, forks: true), [new("main", C)], pulls);
        Assert.Equal(new int?[] { 12, 13 }, plan.Create.Select(c => c.PullRequest));
        Assert.True(plan.Create[1].FromFork);
    }

    [Fact]
    public void Turning_branch_previews_on_notes_every_branch_and_only_a_new_commit_gets_a_preview()
    {
        RemoteBranch[] branches = [new("main", A), new("feature/login", B), new("old-idea", C)];

        // The first look builds nothing: every branch's commit is noted, the production branch's too (should it stop being
        // production, it gets a preview only with a later commit).
        var first = PreviewPlanner.Plan(Settings(), branches, [], [], [], new Dictionary<string, string>(), Now);
        Assert.Empty(first.Create);
        Assert.Equal(B, first.Skips["branch:feature/login"]);
        Assert.Equal(A, first.Skips["branch:main"]);

        // A new commit on feature/login gets it a preview; old-idea, untouched, stays without one.
        var second = Plan(Settings(), [new("main", A), new("feature/login", A), new("old-idea", C)], [], skips: first.Skips);
        Assert.Equal(["feature/login"], second.Create.Select(c => c.Branch));
        Assert.Equal(PreviewKind.Branch, second.Create[0].Kind);
        Assert.Equal("refs/heads/feature/login", second.Create[0].Ref);

        // A branch made after previews were turned on gets one straight away.
        var third = Plan(Settings(), [new("main", A), new("fresh", B)], [], skips: first.Skips);
        Assert.Equal(["fresh"], third.Create.Select(c => c.Branch));
    }

    [Fact]
    public void The_production_branch_never_gets_a_preview() =>
        Assert.Empty(Plan(Settings(), [new("main", B)], []).Create);

    [Fact]
    public void A_branch_a_pull_request_previews_has_no_preview_of_its_own()
    {
        PullRequestInfo[] pulls = [new(12, "Add login", "feature/login", B, false)];
        var plan = Plan(Settings(), [new("main", A), new("feature/login", B)], pulls,
            [new ExistingPreview(5, PreviewKind.Branch, "feature/login", null, null, A, false, Now)]);
        Assert.Equal(new int?[] { 12 }, plan.Create.Select(c => c.PullRequest));
        Assert.Equal([(5, "a pull request previews the branch now")], plan.Remove.Select(r => (r.AppId, r.Reason)));
    }

    [Fact]
    public void A_closed_pull_request_and_a_deleted_branch_take_their_previews_with_them()
    {
        ExistingPreview[] existing =
        [
            new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, false, Now),
            new(6, PreviewKind.Branch, "gone", null, null, B, false, Now),
        ];
        var plan = Plan(Settings(), [new("main", A)], [], existing);
        Assert.Equal([(5, "PR #12", "the pull request was closed or merged"), (6, "gone", "the branch was deleted")],
            plan.Remove.Select(r => (r.AppId, r.Label, r.Reason)));
        Assert.All(plan.Remove, r => Assert.False(r.Remember));
    }

    [Fact]
    public void A_new_commit_or_title_is_noted_and_a_new_commit_starts_the_expiry_again()
    {
        ExistingPreview[] existing = [new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, false, Now.AddDays(-6))];
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login (v2)", "feature/login", B, false)], existing);
        Assert.Equal([new PreviewUpdate(5, B, "Add login (v2)", NewCommit: true)], plan.Update);

        // Only the title: no new commit.
        plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Renamed", "feature/login", A, false)], existing);
        Assert.Equal([new PreviewUpdate(5, A, "Renamed", NewCommit: false)], plan.Update);
    }

    [Fact]
    public void A_preview_without_a_new_commit_for_the_expiry_days_is_removed_and_comes_back_with_the_next_commit()
    {
        ExistingPreview[] existing = [new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, false, Now.AddDays(-7))];
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login", "feature/login", A, false)], existing);
        var removal = Assert.Single(plan.Remove);
        Assert.Equal("no new commit for 7 days", removal.Reason);
        Assert.True(removal.Remember);
        Assert.Equal(A, plan.Skips["pr:12"]);

        // Next pass: the same commit isn't previewed again; a new one is.
        Assert.Empty(Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login", "feature/login", A, false)], skips: plan.Skips).Create);
        Assert.Single(Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login", "feature/login", B, false)], skips: plan.Skips).Create);

        // Six days is within the expiry.
        Assert.Empty(Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login", "feature/login", A, false)],
            [existing[0] with { LastActivityAt = Now.AddDays(-6) }]).Remove);
    }

    [Fact]
    public void Turning_previews_off_removes_them_all_and_turning_branches_off_removes_the_branch_ones()
    {
        ExistingPreview[] existing =
        [
            new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, false, Now),
            new(6, PreviewKind.Branch, "fresh", null, null, B, false, Now),
        ];
        var off = Plan(Settings(PreviewMode.Off), [new("main", A), new("fresh", B)], [new(12, "Add login", "feature/login", A, false)], existing);
        Assert.Equal([5, 6], off.Remove.Select(r => r.AppId));
        Assert.Empty(off.Skips);

        var pullsOnly = Plan(Settings(PreviewMode.PullRequests), [new("main", A), new("fresh", B)], [new(12, "Add login", "feature/login", A, false)], existing,
            new Dictionary<string, string> { [PreviewPlanner.BaselineKey] = "1", ["branch:fresh"] = A });
        Assert.Equal([(6, "branch previews were turned off")], pullsOnly.Remove.Select(r => (r.AppId, r.Reason)));
        // Turned on again, branches are noted afresh.
        Assert.False(pullsOnly.Skips.ContainsKey(PreviewPlanner.BaselineKey));
        Assert.False(pullsOnly.Skips.ContainsKey("branch:fresh"));
    }

    [Fact]
    public void Forbidding_forks_removes_the_previews_of_pull_requests_from_forks()
    {
        ExistingPreview[] existing = [new(5, PreviewKind.PullRequest, "patch-1", 13, "Fix typo", A, FromFork: true, Now)];
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(13, "Fix typo", "patch-1", A, FromFork: true)], existing);
        Assert.Equal("pull requests from forks aren't previewed any more", Assert.Single(plan.Remove).Reason);
    }

    [Fact]
    public void Without_github_nothing_is_made_or_removed_for_pull_requests_but_their_new_commits_still_count()
    {
        ExistingPreview[] existing = [new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, false, Now)];
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], pulls: null, existing, heads: [new RemotePull(12, B), new RemotePull(40, C)]);
        Assert.Empty(plan.Create);
        Assert.Empty(plan.Remove);
        Assert.Equal([new PreviewUpdate(5, B, "Add login", NewCommit: true)], plan.Update);
    }

    [Fact]
    public void The_repos_own_list_of_pull_request_heads_is_fresher_than_githubs()
    {
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], [new(12, "Add login", "feature/login", A, false)], heads: [new RemotePull(12, B)]);
        Assert.Equal(B, Assert.Single(plan.Create).HeadSha);
    }

    [Fact]
    public void What_is_remembered_goes_with_its_branch_or_pull_request()
    {
        var skips = new Dictionary<string, string> { [PreviewPlanner.BaselineKey] = "1", ["branch:gone"] = A, ["branch:kept"] = B, ["pr:12"] = A, ["pr:13"] = B };
        var plan = Plan(Settings(), [new("main", A), new("kept", B)], [new(13, "Open", "x", B, false)], skips: skips);
        Assert.Equal(["@branches", "branch:kept", "branch:main", "pr:13"], plan.Skips.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_merged_pull_requests_branch_that_stays_gets_no_preview_until_a_new_commit()
    {
        ExistingPreview pr = new(5, PreviewKind.PullRequest, "feature/login", 12, "Add login", B, false, Now);
        RemoteBranch[] branches = [new("main", A), new("feature/login", B)];

        // While the pull request previews it, the branch has no preview of its own, and its commit is noted on every look.
        var open = Plan(Settings(), branches, [new(12, "Add login", "feature/login", B, false)], [pr]);
        Assert.Empty(open.Create);
        Assert.Equal(B, open.Skips["branch:feature/login"]);

        // Merged on GitHub, the branch kept: the pull request's preview goes, and the branch gets none for the same commit.
        var merged = Plan(Settings(), branches, [], [pr], open.Skips);
        Assert.Equal([(5, PreviewPlanner.ClosedReason)], merged.Remove.Select(r => (r.AppId, r.Reason)));
        Assert.Empty(merged.Create);
        Assert.Empty(Plan(Settings(), branches, [], skips: merged.Skips).Create);

        // A new commit on it does.
        var pushed = Plan(Settings(), [new("main", A), new("feature/login", C)], [], skips: merged.Skips);
        Assert.Equal([("feature/login", C)], pushed.Create.Select(c => (c.Branch, c.HeadSha)));
    }

    [Fact]
    public void A_branch_that_stops_being_production_gets_a_preview_only_with_a_new_commit()
    {
        var onMain = Plan(Settings(), [new("main", A), new("develop", B)], []);
        Assert.Equal(A, onMain.Skips["branch:main"]);

        // Production moved to develop: main, at the commit production last had, gets no preview; a new commit gives it one.
        var settings = Settings() with { ProductionBranch = "develop" };
        Assert.Empty(Plan(settings, [new("main", A), new("develop", B)], [], skips: onMain.Skips).Create);
        Assert.Equal(["main"], Plan(settings, [new("main", C), new("develop", B)], [], skips: onMain.Skips).Create.Select(c => c.Branch));
    }

    [Fact]
    public void With_forks_off_a_forks_preview_goes_and_takes_no_new_commit_even_when_github_cannot_be_asked()
    {
        ExistingPreview[] existing =
        [
            new(5, PreviewKind.PullRequest, "patch-1", 13, "Fix typo", A, FromFork: true, Now),
            new(6, PreviewKind.PullRequest, "feature/login", 12, "Add login", A, FromFork: false, Now),
        ];
        // GitHub's list unknown (unreachable, rate limited, or just restarted); the repo lists new commits on both.
        var plan = Plan(Settings(PreviewMode.PullRequests), [new("main", C)], pulls: null, existing, heads: [new RemotePull(13, B), new RemotePull(12, B)]);
        Assert.Equal([(5, PreviewPlanner.ForksOffReason)], plan.Remove.Select(r => (r.AppId, r.Reason)));
        Assert.Equal([6], plan.Update.Select(u => u.AppId));
        Assert.Empty(plan.Create);

        // With forks on, the fork's preview takes its new commit like any other.
        var allowed = Plan(Settings(PreviewMode.PullRequests, forks: true), [new("main", C)], pulls: null, existing, heads: [new RemotePull(13, B)]);
        Assert.Empty(allowed.Remove);
        Assert.Equal([new PreviewUpdate(5, B, "Fix typo", NewCommit: true)], allowed.Update);
    }

    // ---- The limit ----

    private static PreviewPlanner.Run Run(int id, bool image = false, bool busy = false, DesiredState state = DesiredState.Running, int minutes = 0) =>
        new(id, image ? $"gitberth/orb-{id}:abc1234" : null, state, busy, Now.AddMinutes(minutes));

    [Fact]
    public void A_preview_waits_while_the_limit_runs_and_starts_when_one_goes()
    {
        PreviewPlanner.Run[] runs = [Run(1, image: true), Run(2, image: true), Run(3, busy: true), Run(4, minutes: 1), Run(5, minutes: 2)];
        Assert.Empty(PreviewPlanner.Admit(runs, [4, 5], limit: 3));
        // One that runs already deploys its new commit whatever the limit.
        Assert.Equal([1], PreviewPlanner.Admit(runs, [1, 4], limit: 3));
        // One goes: the oldest waiting one starts.
        Assert.Equal([4], PreviewPlanner.Admit(runs.Where(r => r.AppId != 2).ToList(), [5, 4], limit: 3));
        // A stopped preview neither runs nor takes a place.
        Assert.Equal([4], PreviewPlanner.Admit([Run(1, image: true, state: DesiredState.Stopped), Run(2, image: true), Run(4)], [4, 1], limit: 2));
    }

    [Fact]
    public void A_waiting_preview_says_how_many_run_already()
    {
        Assert.Equal((PreviewState.Waiting, "Waiting: 3 previews run already"),
            PreviewStates.Of(new PreviewFacts(null, Running: false, DesiredState.Running, null, Occupied: 3, Limit: 3)));
        Assert.Equal((PreviewState.Waiting, "Waiting for its first deploy"),
            PreviewStates.Of(new PreviewFacts(null, Running: false, DesiredState.Running, null, Occupied: 1, Limit: 3)));
        Assert.Equal(PreviewState.Building,
            PreviewStates.Of(new PreviewFacts(new Deployment { Status = DeployStatus.Building }, false, DesiredState.Running, null, 1, 3)).State);
        Assert.Equal(PreviewState.Ready,
            PreviewStates.Of(new PreviewFacts(new Deployment { Status = DeployStatus.Succeeded }, true, DesiredState.Running, "img", 1, 3)).State);
        Assert.Equal(PreviewState.Failed,
            PreviewStates.Of(new PreviewFacts(new Deployment { Status = DeployStatus.Failed }, false, DesiredState.Running, null, 1, 3)).State);
        Assert.Equal(PreviewState.Stopped,
            PreviewStates.Of(new PreviewFacts(new Deployment { Status = DeployStatus.Succeeded }, false, DesiredState.Stopped, "img", 1, 3)).State);
    }

    // ---- Promote ----

    [Fact]
    public void Production_stays_on_a_promoted_commit_until_its_branch_moves()
    {
        var app = new AppDefinition { CurrentCommit = B, LastAttemptedCommit = B, PromotedOverCommit = A };
        Assert.True(RepoWatcher.StaysPromoted(app, A));
        Assert.False(RepoWatcher.StaysPromoted(app, C));
        Assert.False(RepoWatcher.StaysPromoted(new AppDefinition(), A));
    }

    // ---- What a preview copies ----

    [Fact]
    public void A_preview_copies_its_parents_settings_but_nothing_production_holds()
    {
        var parent = new AppDefinition
        {
            Id = 1, Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", Branch = "main", Subdirectory = "web", HealthCheckPath = "/health",
            MemoryLimitMb = 512, RestartPolicy = "always", RuntimeExtras = "{\"init\":true}",
            Ports =
            [
                new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3000, IsWeb = true },
                new PortMapping { Id = 2, ContainerPort = 9229, HostPort = 9229, Access = PortAccess.HostOnly },
                new PortMapping { Id = 3, ContainerPort = 9229, HostPort = 9230 },
            ],
            Volumes = [new VolumeMount { Id = 1, Source = "data", Target = "/data" }, new VolumeMount { Id = 2, Source = "/srv/orb/uploads", Target = "/uploads" }],
            Networks = [new AppNetwork { Id = 1, Network = "backend", Alias = "api" }, new AppNetwork { Id = 2, Network = "gitberth-lan", IPv4Address = "192.168.50.20" }],
        };
        var info = new PreviewInfo { Kind = PreviewKind.PullRequest, Branch = "feature/login", PullRequest = 12, Ref = "refs/pull/12/head" };
        var preview = new AppDefinition { Slug = "orb-pr-12", ParentAppId = 1 };

        PreviewCopy.Sync(preview, parent, info);

        Assert.Equal(("acme", "orb", "feature/login", "web", "/health", 512, "always", "{\"init\":true}", false),
            (preview.Owner, preview.Repo, preview.Branch, preview.Subdirectory, preview.HealthCheckPath, preview.MemoryLimitMb, preview.RestartPolicy, preview.RuntimeExtras, preview.AutoDeploy));
        // Its web port is on the LAN (its host port is its maker's to pick); the rest stay inside Docker, once each.
        Assert.Equal([(3000, PortAccess.Lan, true), (9229, PortAccess.Internal, false)], preview.Ports.Select(p => (p.ContainerPort, p.Access, p.IsWeb)));
        Assert.All(preview.Ports, p => Assert.NotEqual(3000, p.HostPort));
        // Its own volume (named after its slug), never production's folder.
        Assert.Equal(["data"], preview.Volumes.Select(v => v.Source));
        Assert.Equal(["/srv/orb/uploads"], PreviewCopy.LeftOut(parent.Volumes).Select(v => v.Source));
        // On the network, but not under production's name there, nor at its address.
        var network = Assert.Single(preview.Networks);
        Assert.Equal(("backend", null, null), (network.Network, network.Alias, network.IPv4Address));

        // Synced again, the preview's host port stays.
        preview.Ports[0].HostPort = 3002;
        PreviewCopy.Sync(preview, parent, info);
        Assert.Equal(3002, preview.Ports[0].HostPort);
    }

    [Fact]
    public void A_preview_keeps_every_volume_it_has_and_gets_the_parents_new_ones_where_it_has_none()
    {
        // The preview has its copy of data, the volume its image declared (cache), and a host folder from an older Gitberth.
        var preview = new AppDefinition
        {
            Slug = "orb-test", ParentAppId = 1,
            Volumes =
            [
                new VolumeMount { Id = 1, Source = "data", Target = "/data" },
                new VolumeMount { Id = 2, Source = "cache", Target = "/cache" },
                new VolumeMount { Id = 3, Source = "/srv/orb/uploads", Target = "/uploads" },
            ],
        };
        // The parent renamed data to store, added logs read-only, and put a read-only volume where the preview's cache is.
        VolumeMount[] parent =
        [
            new() { Id = 4, Source = "store", Target = "/data/" },
            new() { Id = 5, Source = "logs", Target = "/logs", ReadOnly = true },
            new() { Id = 6, Source = "scratch", Target = "/cache", ReadOnly = true },
            new() { Id = 7, Source = "/srv/orb", Target = "/srv" },
        ];

        var after = PreviewCopy.VolumesAfterSync(preview.Volumes, parent);

        // Its data and cache stay where they are, under their names: its container may still mount them, and only their
        // rows keep another app from taking the names. Each path is mounted as the parent mounts it. logs is new; a host
        // folder never stays.
        Assert.Equal([("data", "/data", false), ("cache", "/cache", true), ("logs", "/logs", true)], after.Select(v => (v.Source, v.Target, v.ReadOnly)));
        Assert.Equal(3, preview.Volumes.Count);
        Assert.False(preview.Volumes[1].ReadOnly);

        // Synced in place, the same. Then the parent drops all but logs, now writable: the preview keeps the rest as they are.
        PreviewCopy.SyncVolumes(preview.Volumes, parent);
        Assert.Equal(after.Select(v => (v.Source, v.Target, v.ReadOnly)), preview.Volumes.Select(v => (v.Source, v.Target, v.ReadOnly)));
        PreviewCopy.SyncVolumes(preview.Volumes, [new VolumeMount { Source = "logs", Target = "/logs" }]);
        Assert.Equal([("data", "/data", false), ("cache", "/cache", true), ("logs", "/logs", false)], preview.Volumes.Select(v => (v.Source, v.Target, v.ReadOnly)));
    }

    [Fact]
    public void A_preview_keeps_its_volume_where_the_parent_has_a_tmpfs_now_and_gets_its_other_tmpfs()
    {
        // The parent swapped its volume cache at /cache for a tmpfs, and keeps /tmp/scratch in memory too.
        var parent = new AppDefinition
        {
            Id = 1, Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb",
            RuntimeExtras = GitExtras.Write(new GitExtras { Tmpfs = [new GitTmpfs("/cache/"), new GitTmpfs("/tmp/scratch", Bytes: 1 << 20)] }),
        };
        var preview = new AppDefinition { Slug = "orb-pr-12", ParentAppId = 1, Volumes = [new VolumeMount { Id = 1, Source = "cache", Target = "/cache" }] };
        var info = new PreviewInfo { Kind = PreviewKind.PullRequest, Branch = "feature/login", PullRequest = 12, Ref = "refs/pull/12/head" };

        PreviewCopy.Sync(preview, parent, info);

        // One mount at /cache, the preview's own volume (Docker refuses a volume and a tmpfs at one path); /tmp/scratch comes along.
        Assert.Equal([("cache", "/cache")], preview.Volumes.Select(v => (v.Source, v.Target)));
        Assert.Equal(["/tmp/scratch"], GitExtras.Parse(preview.RuntimeExtras)!.Tmpfs!.Select(t => t.Target));
        var (tmpfs, volume) = Assert.Single(PreviewCopy.TmpfsUnderVolumes(GitExtras.Parse(parent.RuntimeExtras), preview.Volumes));
        Assert.Equal(("/cache/", "cache"), (tmpfs.Target, volume.Source));

        // A preview without a volume there gets every tmpfs.
        var fresh = new AppDefinition { Slug = "orb-pr-13", ParentAppId = 1 };
        PreviewCopy.Sync(fresh, parent, info);
        Assert.Equal(parent.RuntimeExtras, fresh.RuntimeExtras);
    }

    [Fact]
    public void A_preview_never_gets_the_hosts_devices_and_a_forks_nothing_that_reaches_the_host()
    {
        var extras = new GitExtras
        {
            Devices = [new DeviceSetting("/dev/ttyUSB0", "/dev/ttyUSB0")], Privileged = true, CapAdd = ["NET_ADMIN", "SYS_TIME"], CapDrop = ["MKNOD"],
            SecurityOpt = ["seccomp=unconfined", "no-new-privileges:true"], Gpus = "all", Init = true, ReadOnlyRootfs = true, PidsLimit = 200,
        };
        var parent = new AppDefinition { Id = 1, Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", RuntimeExtras = GitExtras.Write(extras) };
        PreviewInfo Info(bool fork) => new() { Kind = PreviewKind.PullRequest, Branch = "patch-1", PullRequest = 13, Ref = "refs/pull/13/head", FromFork = fork };

        // A pull request from this repo: everything but the devices, which stay production's (a Zigbee stick is one
        // container's), and privileged mode, which would give it every device of the host, the stick too.
        var own = new AppDefinition { Slug = "orb-pr-13", ParentAppId = 1 };
        PreviewCopy.Sync(own, parent, Info(fork: false));
        Assert.Equal(GitExtras.Write(extras with { Devices = null, Privileged = null }), own.RuntimeExtras);
        var ownArgs = ContainerSpec.ExtraArgs(GitExtras.Parse(own.RuntimeExtras)!);
        Assert.DoesNotContain("--device", ownArgs);
        Assert.DoesNotContain("--privileged", ownArgs);
        Assert.Equal(["--cap-add", "NET_ADMIN", "--cap-add", "SYS_TIME"], ownArgs.SkipWhile(a => a != "--cap-add").Take(4));
        Assert.Equal(
        [
            "Not given the host's device, because a preview never takes production's: /dev/ttyUSB0.",
            "Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's.",
        ], PreviewCopy.ExtrasLeftOut(extras, fromFork: false));
        // Privileged without a device of its own: still every device of the host.
        var privileged = new GitExtras { Privileged = true, Init = true };
        Assert.Equal(GitExtras.Write(new GitExtras { Init = true }), GitExtras.Write(PreviewCopy.Extras(privileged, fromFork: false)));
        Assert.Equal(["Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's."],
            PreviewCopy.ExtrasLeftOut(privileged, fromFork: false));

        // From a fork: nothing that reaches past the container either. What only takes rights away stays.
        var fork = new AppDefinition { Slug = "orb-pr-13", ParentAppId = 1 };
        PreviewCopy.Sync(fork, parent, Info(fork: true));
        var args = ContainerSpec.ExtraArgs(GitExtras.Parse(fork.RuntimeExtras)!);
        foreach (var flag in new[] { "--device", "--privileged", "--cap-add", "--gpus" }) Assert.DoesNotContain(flag, args);
        Assert.Equal(["--security-opt", "no-new-privileges:true"], args.SkipWhile(a => a != "--security-opt").Take(2));
        Assert.DoesNotContain("seccomp=unconfined", args);
        Assert.Contains("--init", args);
        Assert.Contains("--read-only", args);
        Assert.Equal(["--cap-drop", "MKNOD"], args.SkipWhile(a => a != "--cap-drop").Take(2));
        Assert.Equal(
        [
            "Not given the host's device, because a preview never takes production's: /dev/ttyUSB0.",
            "Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's.",
            "Runs without added capabilities (NET_ADMIN, SYS_TIME), security options (seccomp=unconfined) or GPUs (all), because the pull request comes from a fork and its code mustn't reach the host.",
        ], PreviewCopy.ExtrasLeftOut(extras, fromFork: true));

        // A parent without any of them: nothing to say, and nothing changes.
        Assert.Empty(PreviewCopy.ExtrasLeftOut(new GitExtras { Init = true }, fromFork: true));
        Assert.Empty(PreviewCopy.ExtrasLeftOut(null, fromFork: true));
        Assert.Null(PreviewCopy.Extras(null, fromFork: true));
    }

    // ---- Environments ----

    private static EnvVar Var(int id, string key, EnvScope scope, string value) => new() { Id = id, Key = key, Scope = scope, ProtectedValue = value };

    [Fact]
    public void Production_gets_all_and_production_values_and_a_preview_all_and_preview_ones()
    {
        EnvVar[] vars =
        [
            Var(1, "API_URL", EnvScope.All, "https://api.example.com"),
            Var(2, "API_URL", EnvScope.Preview, "https://staging.example.com"),
            Var(3, "STRIPE_KEY", EnvScope.Production, "live"),
            Var(4, "STRIPE_KEY", EnvScope.Preview, "test"),
            Var(5, "LOG_LEVEL", EnvScope.All, "info"),
            Var(6, "DEBUG", EnvScope.Preview, "1"),
        ];
        Assert.Equal(["API_URL=https://api.example.com", "STRIPE_KEY=live", "LOG_LEVEL=info"],
            EnvScopes.ForDeploy(vars, preview: false).Select(v => $"{v.Key}={v.ProtectedValue}"));
        Assert.Equal(["API_URL=https://staging.example.com", "STRIPE_KEY=test", "LOG_LEVEL=info", "DEBUG=1"],
            EnvScopes.ForDeploy(vars, preview: true).Select(v => $"{v.Key}={v.ProtectedValue}"));
    }

    [Fact]
    public void A_preview_value_never_leaves_production_pending()
    {
        var protector = new Security.SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var app = new AppDefinition { Slug = "orb", EnvVars = [new EnvVar { Key = "API_URL", ProtectedValue = protector.Protect("a"), BuildTime = true }] };
        var before = AppService.Fingerprint(app, protector, "gitberth");
        app.EnvVars.Add(new EnvVar { Key = "API_URL", Scope = EnvScope.Preview, ProtectedValue = protector.Protect("b"), BuildTime = true });
        Assert.Equal(before, AppService.Fingerprint(app, protector, "gitberth"));
        app.EnvVars.Add(new EnvVar { Key = "API_URL", Scope = EnvScope.Production, ProtectedValue = protector.Protect("c"), BuildTime = true });
        var after = AppService.Fingerprint(app, protector, "gitberth");
        Assert.NotEqual(before.Build, after.Build);
        Assert.NotEqual(before.Runtime, after.Runtime);
    }

    [Fact]
    public void A_name_can_have_a_value_per_scope_and_moving_one_to_another_scope_is_a_change()
    {
        var protector = new Security.SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var app = new AppDefinition { Slug = "orb", EnvVars = [new EnvVar { Id = 1, Key = "API_URL", ProtectedValue = protector.Protect("a") }] };
        var loaded = GitAppSettings.ToContainer(app, protector);
        var draft = Components.Apps.ContainerSettingsDraft.From(loaded);
        var row = draft.AddEnv("API_URL", "b");
        row.Scope = EnvScope.Preview;
        Assert.Empty(draft.EnvProblems());
        draft.Env[0].Scope = EnvScope.Production;
        var wanted = draft.ToSettings();
        Assert.Contains(SettingField.Env, draft.ChangedFields());
        Assert.Contains(SettingField.Env, GitAppSettings.Changed(loaded, wanted));
        Assert.Contains(SettingField.Env, SettingsDiff.Changed(loaded, wanted));
        Assert.DoesNotContain(SettingsRules.Check(wanted with { Image = "x" }, new SettingsContext(4, 1L << 33, new HashSet<string>(), GitApp: true)), p => p.Field == SettingField.Env);
        Assert.Equal(["Added API_URL (Production)", "Added API_URL (Preview)", "Removed API_URL"],
            SettingsDiff.Lines(loaded, wanted, new HashSet<SettingField> { SettingField.Env }, [], _ => ApplyKind.Deploy).Select(l => l.Label));

        GitAppSettings.Apply(app, wanted, new HashSet<SettingField> { SettingField.Env }, protector);
        Assert.Equal([("API_URL", EnvScope.Production, "a"), ("API_URL", EnvScope.Preview, "b")],
            app.EnvVars.Select(v => (v.Key, v.Scope, protector.TryUnprotect(v.ProtectedValue))));

        // Two of the same name and scope are refused.
        var twice = Components.Apps.ContainerSettingsDraft.From(GitAppSettings.ToContainer(app, protector));
        twice.AddEnv("API_URL", "c").Scope = EnvScope.Preview;
        Assert.Equal(["API_URL (Preview) is set twice."], twice.EnvProblems());
    }

    [Fact]
    public void A_change_for_previews_only_is_no_change_for_production()
    {
        EnvSetting[] before = [new("API_URL", "a"), new("API_URL", "b", Scope: EnvScope.Preview)];
        Assert.True(AppSettingsService.SameForProduction(before, [new("API_URL", "a"), new("API_URL", "c", Scope: EnvScope.Preview)]));
        Assert.True(AppSettingsService.SameForProduction(before, [new("API_URL", "a")]));
        Assert.False(AppSettingsService.SameForProduction(before, [new("API_URL", "a"), new("API_URL", "c", Scope: EnvScope.Production)]));
        Assert.False(AppSettingsService.SameForProduction(before, [new("API_URL", "z"), new("API_URL", "b", Scope: EnvScope.Preview)]));
    }

    // ---- Vercel's variables ----

    [Fact]
    public void A_preview_tells_the_app_it_is_a_preview_with_its_own_address_and_productions()
    {
        var facts = new DeployFacts("app-pr-12.example.com", "acme", "orb", "feature/login", "abc", "Add login", "Ada", AppTraits.None,
            Production: false, ProductionDomain: "app.example.com", PullRequest: 12, Preview: true);
        var vars = VercelEnv.For(facts).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("preview", vars["VERCEL_ENV"]);
        Assert.Equal("preview", vars["VERCEL_TARGET_ENV"]);
        Assert.Equal("app-pr-12.example.com", vars["VERCEL_URL"]);
        Assert.Equal("app-pr-12.example.com", vars["VERCEL_BRANCH_URL"]);
        Assert.Equal("app.example.com", vars["VERCEL_PROJECT_PRODUCTION_URL"]);
        Assert.Equal("feature/login", vars["VERCEL_GIT_COMMIT_REF"]);
        Assert.Equal("12", vars["VERCEL_GIT_PULL_REQUEST_ID"]);

        // Without a domain of its own: no URL, the production one still.
        vars = VercelEnv.For(facts with { MainDomain = null }).ToDictionary(v => v.Key, v => v.Value);
        Assert.False(vars.ContainsKey("VERCEL_URL"));
        Assert.Equal("app.example.com", vars["VERCEL_PROJECT_PRODUCTION_URL"]);
        Assert.Equal(["Vercel's variables are set, with VERCEL_PROJECT_PRODUCTION_URL=app.example.com and VERCEL_ENV=preview. VERCEL_URL isn't: the app's code checks process.env.VERCEL, and code like that takes VERCEL_URL to mean it runs on Vercel."],
            DeployPipeline.DescribeVercelVariables([], facts with { MainDomain = "app-pr-12.example.com", Traits = new AppTraits(null, false, ChecksVercel: true) }));

        // Production's stay as they were.
        var production = VercelEnv.For(new DeployFacts("app.example.com", "acme", "orb", "main", "abc", null, null, AppTraits.None)).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("app.example.com", production["VERCEL_PROJECT_PRODUCTION_URL"]);
        Assert.False(production.ContainsKey("VERCEL_GIT_PULL_REQUEST_ID"));
    }
}
