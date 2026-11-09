using System.Net;
using System.Text;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Previews on CT 101's rig: made from a repo's pull requests (GitHub answered by a stub), kept out of the Apps list,
/// limited, removed with their parent or by hand, promoted, and turned off. No deploy runs: the rig's queue never starts one.
/// </summary>
public sealed class PreviewServiceTests : IDisposable
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Main = "1d2948cc693a1d2948cc693a1d2948cc693a1d29";

    private sealed class GitHub : HttpMessageHandler
    {
        public string Pulls { get; set; } = "[]";
        /// <summary>Closed pull requests GitHub says were merged.</summary>
        public HashSet<int> Merged { get; } = [];
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.RequestUri.PathAndQuery);
            if (path.StartsWith("/repos/acme/orb/pulls/", StringComparison.Ordinal) && int.TryParse(path["/repos/acme/orb/pulls/".Length..], out var number))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"number\": " + number + ", \"merged\": " + (Merged.Contains(number) ? "true" : "false") + "}", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(path == "/repos/acme/orb/pulls"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Pulls, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly AppsRig _rig = new();
    private readonly GitHub _github = new();
    private readonly PreviewService _previews;
    // The rig's orb app: every preview here is one of its.
    private readonly int _orb;

    public PreviewServiceTests()
    {
        _previews = new PreviewService(_rig.Db, _rig.Actions, _rig.Apps, _rig.Domains, new GitHubPulls(new Factory(_github), _rig.Settings),
            new GitClient(_rig.Options, _rig.Settings), _rig.Cli, _rig.Activity, new WatchState(), NullLogger<PreviewService>.Instance);
        using var db = _rig.Db.CreateDbContext();
        _orb = db.Apps.Single(a => a.Slug == "orb").Id;
    }

    public void Dispose() => _rig.Dispose();

    private static string Pull(int number, string title, string branch, string sha, string repo = "acme/orb") =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["number"] = number, ["title"] = title, ["html_url"] = $"https://github.com/acme/orb/pull/{number}",
            ["head"] = new System.Text.Json.Nodes.JsonObject { ["ref"] = branch, ["sha"] = sha, ["repo"] = new System.Text.Json.Nodes.JsonObject { ["full_name"] = repo } },
            ["base"] = new System.Text.Json.Nodes.JsonObject { ["repo"] = new System.Text.Json.Nodes.JsonObject { ["full_name"] = "acme/orb" } },
        }.ToJsonString();

    private async Task TurnOnAsync(PreviewMode mode = PreviewMode.PullRequests, int limit = 3)
    {
        await using var db = _rig.Db.CreateDbContext();
        await db.Apps.Where(a => a.Id == _orb).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewMode, mode).SetProperty(a => a.PreviewLimit, limit));
    }

    private async Task CheckAsync(bool poked = false, params RemotePull[] heads)
    {
        List<AppDefinition> watched;
        await using (var db = _rig.Db.CreateDbContext())
            watched = await RepoWatcher.Watched(db.Apps.AsNoTracking()).Where(a => a.Owner == "acme" && a.Repo == "orb").ToListAsync();
        var remote = new RemoteInfo("main", [new RemoteBranch("main", Main), new RemoteBranch("feature/login", A)], heads);
        await _previews.CheckRepoAsync("acme", "orb", remote, poked, watched, new HashSet<int>());
    }

    private async Task<List<(AppDefinition Row, PreviewInfo Info)>> PreviewsAsync()
    {
        await using var db = _rig.Db.CreateDbContext();
        var infos = await db.Previews.AsNoTracking().ToListAsync();
        var rows = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).Where(a => a.ParentAppId != null).ToListAsync();
        return infos.Join(rows, i => i.AppId, r => r.Id, (i, r) => (r, i)).OrderBy(x => x.r.Id).ToList();
    }

    private async Task<List<Deployment>> DeploymentsAsync(int appId)
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.Deployments.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.Id).ToListAsync();
    }

    [Fact]
    public async Task An_open_pull_request_becomes_an_app_of_its_own_that_deploys_and_stays_out_of_the_apps_list()
    {
        await TurnOnAsync();
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _rig.AddAsync(new CustomDomain { AppId = _orb, Hostname = "app.example.com" });
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";

        await CheckAsync(poked: true);

        var (row, info) = Assert.Single(await PreviewsAsync());
        Assert.Equal(("orb-pr-12", "orb · PR #12", _orb, false), (row.Slug, row.Name, row.ParentAppId, row.AutoDeploy));
        Assert.Equal(("refs/pull/12/head", A, "Add login", PreviewKind.PullRequest), (info.Ref, info.HeadSha, info.Title, info.Kind));
        // Its web port is on the LAN on a host port of its own, not production's.
        var web = Assert.Single(row.Ports);
        Assert.Equal((80, PortAccess.Lan, true), (web.ContainerPort, web.Access, web.IsWeb));
        Assert.NotNull(web.HostPort);
        Assert.NotEqual(3000, web.HostPort);
        // Its domain is one level below the zone, protected as its parent says.
        var domain = Assert.Single(row.Domains);
        Assert.Equal(("app-pr-12.example.com", true), (domain.Hostname, domain.Protected));

        // Its first deploy is GitHub's: the webhook told about the repo.
        var deploy = Assert.Single(await DeploymentsAsync(row.Id));
        Assert.Equal((DeployTrigger.Webhook, A), (deploy.Trigger, deploy.CommitSha));

        // Kept out of the Apps list (so the menu, search and the dashboard), but its own page finds it.
        Assert.DoesNotContain((await _rig.Catalog.ListAsync("192.168.50.10")).Apps, a => a.Key == "gb-orb-pr-12");
        Assert.NotNull(await _rig.Catalog.GetAsync("gb-orb-pr-12", "192.168.50.10"));
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 added: Add login.");

        // The tab lists it, and its own page says what it previews.
        var listed = Assert.Single(await _previews.ListAsync(_orb, "192.168.50.10"));
        Assert.Equal(("PR #12", "https://github.com/acme/orb/pull/12", "app-pr-12.example.com", PreviewState.Building), (listed.Label, listed.Url, listed.Domain, listed.State));
        Assert.Equal($"http://192.168.50.10:{web.HostPort}", listed.LanUrl);
        var of = await _previews.OfAsync(row.Id);
        Assert.Equal(("orb", "gb-orb", 12), (of?.ParentName, of?.ParentKey, of?.Info.PullRequest));
        Assert.Null(await _previews.OfAsync(_orb));
    }

    [Fact]
    public async Task Pull_requests_are_asked_for_when_they_change_or_github_tells_but_not_on_every_pass()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";

        await CheckAsync(false, new RemotePull(12, A));
        await CheckAsync(false, new RemotePull(12, A));
        Assert.Single(_github.Calls);
        // A new commit on a pull request, or GitHub telling, asks again.
        await CheckAsync(false, new RemotePull(12, B));
        await CheckAsync(true, new RemotePull(12, B));
        Assert.Equal(3, _github.Calls.Count);
        Assert.Equal("/repos/acme/orb/pulls?state=open&per_page=100&page=1", _github.Calls[0]);
    }

    [Fact]
    public async Task Only_the_limit_deploys_and_the_others_wait()
    {
        await TurnOnAsync(limit: 1);
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}, {Pull(13, "Dark mode", "dark", B)}]";

        await CheckAsync();

        var previews = await PreviewsAsync();
        Assert.Equal(2, previews.Count);
        Assert.Single(await DeploymentsAsync(previews[0].Row.Id));
        Assert.Empty(await DeploymentsAsync(previews[1].Row.Id));
        var waiting = (await _previews.ListAsync(_orb, "192.168.50.10")).Single(p => p.PullRequest == 13);
        Assert.Equal((PreviewState.Waiting, "Waiting: 1 preview runs already"), (waiting.State, waiting.StateText));
        // Two previews never get the same host port.
        Assert.NotEqual(previews[0].Row.Ports.Single().HostPort, previews[1].Row.Ports.Single().HostPort);
    }

    [Fact]
    public async Task A_closed_pull_request_takes_its_preview_and_says_so_on_the_parents_activity()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;

        _github.Pulls = "[]";
        _github.Merged.Add(12);
        await CheckAsync(poked: true);

        Assert.Empty(await PreviewsAsync());
        Assert.Contains($"docker rm -f {preview.ContainerName}", _rig.Engine.Writes);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed: the pull request was merged.");
    }

    [Fact]
    public async Task A_preview_removed_by_hand_comes_back_only_with_a_new_commit()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        await _previews.RemoveAsync(Assert.Single(await PreviewsAsync()).Row.Id);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed.");

        await CheckAsync(poked: true);
        Assert.Empty(await PreviewsAsync());

        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", B)}]";
        await CheckAsync(poked: true);
        Assert.Equal(B, Assert.Single(await PreviewsAsync()).Info.HeadSha);
    }

    [Fact]
    public async Task Deleting_the_parent_deletes_its_previews()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();

        await _rig.Actions.DeleteAsync(_orb, removeImages: false, removeVolumes: false);

        Assert.Empty(await PreviewsAsync());
        Assert.Contains("docker rm -f gb-orb-pr-12", _rig.Engine.Writes);
        Assert.Null(await _rig.Settings.GetAsync(PreviewService.SkipsKey(_orb)));
    }

    [Fact]
    public async Task Turning_previews_off_removes_them_and_protection_follows_the_setting()
    {
        await TurnOnAsync();
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _rig.AddAsync(new CustomDomain { AppId = _orb, Hostname = "app.example.com" });
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        // Domains off for the rest: Cloudflare is never asked here.
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, null);

        Assert.Equal("Between 1 and 20 previews can run at once.", await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 0, 7, false, true)));
        Assert.Null(await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, false, Protect: false)));
        Assert.False(Assert.Single(Assert.Single(await PreviewsAsync()).Row.Domains).Protected);

        Assert.Null(await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.Off, 3, 7, false, false)));
        Assert.Empty(await PreviewsAsync());
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed: previews were turned off.");
    }

    [Fact]
    public async Task Promote_builds_the_previews_commit_as_production_which_stays_on_it_until_a_manual_deploy()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _previews.PromoteAsync(preview.Id));
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == preview.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(Main);

        var deploymentId = await _previews.PromoteAsync(preview.Id);

        var deploy = (await DeploymentsAsync(_orb)).Single(d => d.Id == deploymentId);
        Assert.Equal((DeployTrigger.Promote, A), (deploy.Trigger, deploy.CommitSha));
        var parent = await _rig.RowAsync(a => a.Id == _orb);
        Assert.Equal(Main, parent!.PromotedOverCommit);
        Assert.True(RepoWatcher.StaysPromoted(parent, Main));

        await _rig.Actions.DeployAsync(_orb);
        Assert.Null((await _rig.RowAsync(a => a.Id == _orb))!.PromotedOverCommit);
    }
}
