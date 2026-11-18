using System.Net;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Previews;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Previews of a GitLab project's merge requests, on CT 101's rig: listed through GitLab's API (a stub), made as "MR !12"
/// fetching refs/merge-requests/12/head, a fork's only while forks are on (as on GitHub), removed with why when it's
/// merged or closed, and promoted. No deploy runs: the rig's queue never starts one.
/// </summary>
public sealed class GitLabPreviewTests : IDisposable
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Main = "1d2948cc693a1d2948cc693a1d2948cc693a1d29";

    private readonly AppsRig _rig = new();
    private readonly GitLabStub _gitlab = new();
    private readonly PreviewService _previews;
    private readonly int _orb;
    private string _open = "[]";
    private readonly Dictionary<int, string> _state = [];

    public GitLabPreviewTests()
    {
        _previews = new PreviewService(_rig.Db, _rig.Actions, _rig.Apps, _rig.Domains, new GitHubPulls(new ImageKit.NoHttp(), _rig.Settings),
            new GitClient(_rig.Options, _rig.Settings), _rig.Cli, _rig.Activity, new WatchState(), NullLogger<PreviewService>.Instance, gate: _rig.PreviewGate,
            gitLab: new GitLabApi(new GitLabStub.Factory(_gitlab), _rig.Settings));
        _gitlab.Respond = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v4/projects/acme%2Forb/merge_requests") return GitLabStub.Json(HttpStatusCode.OK, _open);
            if (path.StartsWith("/api/v4/projects/acme%2Forb/merge_requests/", StringComparison.Ordinal)
                && int.TryParse(path["/api/v4/projects/acme%2Forb/merge_requests/".Length..], out var iid))
                return GitLabStub.Json(HttpStatusCode.OK, $$"""{"iid":{{iid}},"state":"{{_state.GetValueOrDefault(iid, "closed")}}"}""");
            return GitLabStub.Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}""");
        };
        using var db = _rig.Db.CreateDbContext();
        var orb = db.Apps.Single(a => a.Slug == "orb");
        // The rig's orb, from a GitLab project: acme/orb on gitlab.com.
        orb.Owner = "gitlab:acme";
        db.SaveChanges();
        _orb = orb.Id;
    }

    public void Dispose() => _rig.Dispose();

    private static string MergeRequest(int iid, string title, string branch, string sha, int sourceProject = 5) => new JsonObject
    {
        ["iid"] = iid, ["title"] = title, ["source_branch"] = branch, ["target_branch"] = "main", ["sha"] = sha,
        ["source_project_id"] = sourceProject, ["target_project_id"] = 5, ["web_url"] = $"https://gitlab.com/acme/orb/-/merge_requests/{iid}", ["draft"] = false,
    }.ToJsonString();

    private async Task TurnOnAsync(bool forks = false)
    {
        await using var db = _rig.Db.CreateDbContext();
        await db.Apps.Where(a => a.Id == _orb).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewMode, PreviewMode.PullRequests).SetProperty(a => a.PreviewForks, forks));
    }

    private async Task CheckAsync(bool poked = true, params RemotePull[] heads)
    {
        List<AppDefinition> watched;
        await using (var db = _rig.Db.CreateDbContext())
            watched = await RepoWatcher.Watched(db.Apps.AsNoTracking()).Where(a => a.Owner == "gitlab:acme" && a.Repo == "orb").ToListAsync();
        var remote = new RemoteInfo("main", [new RemoteBranch("main", Main), new RemoteBranch("feature/login", A)], heads);
        await _previews.CheckRepoAsync("gitlab:acme", "orb", remote, poked, watched, new HashSet<int>());
    }

    private async Task<List<(AppDefinition Row, PreviewInfo Info)>> PreviewsAsync()
    {
        await using var db = _rig.Db.CreateDbContext();
        var infos = await db.Previews.AsNoTracking().ToListAsync();
        var rows = await db.Apps.AsNoTracking().Where(a => a.ParentAppId != null).ToListAsync();
        return infos.Join(rows, i => i.AppId, r => r.Id, (i, r) => (r, i)).OrderBy(x => x.r.Id).ToList();
    }

    [Fact]
    public async Task An_open_merge_request_becomes_a_preview_that_fetches_its_head_from_the_target_project()
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(12, "Add login", "feature/login", A)}]";

        await CheckAsync();

        var (row, info) = Assert.Single(await PreviewsAsync());
        Assert.Equal(("orb-pr-12", "orb · MR !12", _orb), (row.Slug, row.Name, row.ParentAppId));
        Assert.Equal(("refs/merge-requests/12/head", A, "Add login", PreviewKind.PullRequest, false), (info.Ref, info.HeadSha, info.Title, info.Kind, info.FromFork));
        // A preview's row is its parent's project's: the same Owner and Repo.
        Assert.Equal(("gitlab:acme", "orb"), (row.Owner, row.Repo));
        Assert.Equal($"GET /api/v4/projects/acme%2Forb/merge_requests?state=opened&per_page=100&page=1", _gitlab.Calls[0]);
        await using (var db = _rig.Db.CreateDbContext())
        {
            // Its first deploy is GitLab's: the hook told about the project.
            var deploy = await db.Deployments.AsNoTracking().SingleAsync(d => d.AppId == row.Id);
            Assert.Equal((DeployTrigger.Webhook, A), (deploy.Trigger, deploy.CommitSha));
        }
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview MR !12 added: Add login.");

        var listed = Assert.Single(await _previews.ListAsync(_orb, "192.168.50.10"));
        Assert.Equal(("MR !12", "https://gitlab.com/acme/orb/-/merge_requests/12", GitHost.GitLab), (listed.Label, listed.Url, listed.Host));
        var of = await _previews.OfAsync(row.Id);
        Assert.Equal(("MR !12", "https://gitlab.com/acme/orb/-/merge_requests/12"), (of?.Label, of?.Url));
    }

    [Fact]
    public async Task A_new_commit_on_the_merge_request_is_seen_in_its_head_ref_between_two_asks()
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var asked = _gitlab.Calls.Count;

        // GitLab's ls-remote lists refs/merge-requests/12/head at a new commit: the merge requests are asked for again.
        _open = $"[{MergeRequest(12, "Add login", "feature/login", B)}]";
        await CheckAsync(poked: false, new RemotePull(12, B));

        Assert.True(_gitlab.Calls.Count > asked);
        Assert.Equal(B, Assert.Single(await PreviewsAsync()).Info.HeadSha);
    }

    [Fact]
    public async Task A_forks_merge_request_waits_for_forks_to_be_turned_on()
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(13, "From a fork", "patch-1", B, sourceProject: 99)}]";

        await CheckAsync();
        Assert.Empty(await PreviewsAsync());

        await TurnOnAsync(forks: true);
        await CheckAsync();
        var (row, info) = Assert.Single(await PreviewsAsync());
        Assert.Equal(("orb · MR !13", true, "refs/merge-requests/13/head"), (row.Name, info.FromFork, info.Ref));

        // Forks off again: its preview goes, said as GitLab says it.
        await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, Forks: false, Protect: true));
        Assert.Empty(await PreviewsAsync());
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview MR !13 removed: merge requests from forks aren't previewed any more.");
    }

    [Theory]
    [InlineData("merged", "Preview MR !12 removed: the merge request was merged.")]
    [InlineData("closed", "Preview MR !12 removed: the merge request was closed.")]
    public async Task A_merge_request_that_closes_takes_its_preview_and_gitlab_says_how(string state, string said)
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        Assert.Single(await PreviewsAsync());

        _open = "[]";
        _state[12] = state;
        await CheckAsync();

        Assert.Empty(await PreviewsAsync());
        Assert.Contains("GET /api/v4/projects/acme%2Forb/merge_requests/12", _gitlab.Calls);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == said);
    }

    [Fact]
    public async Task Gitlab_out_of_reach_leaves_the_previews_as_they_are_and_says_why()
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var respond = _gitlab.Respond;
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""");

        await CheckAsync();

        Assert.Single(await PreviewsAsync());
        Assert.StartsWith("GitLab's API rate limit for this token is used up for the moment", _previews.PullsProblem("gitlab:acme", "orb"));
        _gitlab.Respond = respond;
    }

    [Fact]
    public async Task A_merge_requests_preview_is_promoted_from_its_own_ref()
    {
        await TurnOnAsync();
        _open = $"[{MergeRequest(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var (row, _) = Assert.Single(await PreviewsAsync());
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(Main);

        var deployment = await _previews.PromoteAsync(row.Id);

        await using var after = _rig.Db.CreateDbContext();
        var dep = await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == deployment);
        Assert.Equal((_orb, DeployTrigger.Promote, A), (dep.AppId, dep.Trigger, dep.CommitSha));
        Assert.Equal(Main, (await after.Apps.AsNoTracking().SingleAsync(a => a.Id == _orb)).PromotedOverCommit);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text.StartsWith("Preview MR !12 promoted: production builds aaaaaaa", StringComparison.Ordinal));
    }
}
