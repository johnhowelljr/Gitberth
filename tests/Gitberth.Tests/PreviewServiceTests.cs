using System.Net;
using System.Text;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
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
        /// <summary>What the list of open pull requests answers instead of the list, when set.</summary>
        public HttpStatusCode? PullsFail { get; set; }
        /// <summary>One pull request's page fails (so whether it was merged can't be told).</summary>
        public bool PullFails { get; set; }
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.RequestUri.PathAndQuery);
            if (path.StartsWith("/repos/acme/orb/pulls/", StringComparison.Ordinal) && int.TryParse(path["/repos/acme/orb/pulls/".Length..], out var number))
                return Task.FromResult(PullFails ? new HttpResponseMessage(HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"number\": " + number + ", \"merged\": " + (Merged.Contains(number) ? "true" : "false") + "}", Encoding.UTF8, "application/json"),
                });
            if (path == "/repos/acme/orb/pulls" && PullsFail is { } status) return Task.FromResult(new HttpResponseMessage(status));
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
            new GitClient(_rig.Options, _rig.Settings), _rig.Cli, _rig.Activity, new WatchState(), NullLogger<PreviewService>.Instance, gate: _rig.PreviewGate);
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

    // A pass that sees these branches, told by GitHub (so its pull requests are asked for).
    private async Task CheckBranchesAsync(params RemoteBranch[] branches)
    {
        List<AppDefinition> watched;
        await using (var db = _rig.Db.CreateDbContext())
            watched = await RepoWatcher.Watched(db.Apps.AsNoTracking()).Where(a => a.Owner == "acme" && a.Repo == "orb").ToListAsync();
        await _previews.CheckRepoAsync("acme", "orb", new RemoteInfo("main", branches, []), poked: true, watched, new HashSet<int>());
    }

    private static async Task WaitForAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200 && !await done(); i++) await Task.Delay(50);
        Assert.True(await done());
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
    public async Task A_pull_request_closed_without_merging_says_so_and_one_github_cant_tell_about_says_closed_or_merged()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}, {Pull(13, "Dark mode", "dark", B)}]";
        await CheckAsync();
        Assert.Equal(2, (await PreviewsAsync()).Count);

        _github.Pulls = $"[{Pull(13, "Dark mode", "dark", B)}]";
        await CheckAsync(poked: true);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed: the pull request was closed.");

        // GitHub's page of the pull request fails: the reason stays as the list tells it.
        _github.Pulls = "[]";
        _github.PullFails = true;
        await CheckAsync(poked: true);
        Assert.Empty(await PreviewsAsync());
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == $"Preview PR #13 removed: {PreviewPlanner.ClosedReason}.");
    }

    [Fact]
    public async Task When_github_cant_list_the_pull_requests_the_previews_stay_and_the_problem_is_kept()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        Assert.Null(_previews.PullsProblem("acme", "orb"));

        // GitHub fails: the last list stands, so nothing is removed as closed.
        _github.PullsFail = HttpStatusCode.InternalServerError;
        await CheckAsync(poked: true);
        Assert.Single(await PreviewsAsync());
        Assert.Equal("GitHub answered 500 when asked for acme/orb's pull requests.", _previews.PullsProblem("ACME", "orb"));

        // It answers again: the problem goes, and a closed pull request takes its preview.
        _github.PullsFail = null;
        _github.Pulls = "[]";
        await CheckAsync(poked: true);
        Assert.Null(_previews.PullsProblem("acme", "orb"));
        Assert.Empty(await PreviewsAsync());
    }

    [Fact]
    public async Task A_preview_whose_build_failed_is_tried_again_once_the_retry_is_due()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        var first = Assert.Single(await DeploymentsAsync(preview.Id));

        // Its build failed a minute ago: not yet.
        async Task FailedAsync(TimeSpan ago)
        {
            await using var db = _rig.Db.CreateDbContext();
            await db.Deployments.Where(d => d.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeployStatus.Failed)
                .SetProperty(d => d.FinishedAt, DateTime.UtcNow - ago).SetProperty(d => d.GitberthBuild, BuildStamp.Current));
        }
        await FailedAsync(TimeSpan.FromMinutes(1));
        await CheckAsync();
        Assert.Single(await DeploymentsAsync(preview.Id));

        // Due: one retry of the same commit.
        await FailedAsync(RepoWatcher.RetryDelay + TimeSpan.FromMinutes(1));
        await CheckAsync();
        var retry = (await DeploymentsAsync(preview.Id)).Last();
        Assert.Equal((DeployTrigger.Retry, A), (retry.Trigger, retry.CommitSha));
    }

    [Fact]
    public async Task A_previews_domain_another_app_has_is_left_out_and_one_on_its_way_out_is_taken_over()
    {
        await TurnOnAsync();
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _rig.AddAsync(new CustomDomain { AppId = _orb, Hostname = "app.example.com" });
        // PR #12's name is kanban's; PR #13's is still being removed after an earlier preview.
        await _rig.AddAsync(new CustomDomain { AppId = _rig.KanbanId, Hostname = "app-pr-12.example.com" });
        await using (var db = _rig.Db.CreateDbContext())
        {
            db.DomainRemovals.Add(new DomainRemoval { Hostname = "app-pr-13.example.com", Service = "http://192.168.50.10:3005" });
            await db.SaveChangesAsync();
        }
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}, {Pull(13, "Dark mode", "dark", B)}]";

        await CheckAsync();

        var previews = await PreviewsAsync();
        Assert.Empty(previews.Single(p => p.Info.PullRequest == 12).Row.Domains);
        var taken = Assert.Single(previews.Single(p => p.Info.PullRequest == 13).Row.Domains);
        Assert.Equal(("app-pr-13.example.com", "http://192.168.50.10:3005"), (taken.Hostname, taken.Service));
        await using (var db = _rig.Db.CreateDbContext())
        {
            Assert.Empty(await db.DomainRemovals.ToListAsync());
            Assert.Equal(_rig.KanbanId, (await db.Domains.SingleAsync(d => d.Hostname == "app-pr-12.example.com")).AppId);
        }
    }

    [Fact]
    public async Task A_pass_removes_the_previews_of_an_app_whose_previews_were_turned_off_if_that_was_cut_short()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();

        // Previews on: a pass leaves them.
        await _previews.TidyAsync();
        Assert.Single(await PreviewsAsync());

        // Turned off without the removal (Gitberth restarted in the middle of it, say): the next pass removes them.
        await TurnOnAsync(PreviewMode.Off);
        await _previews.TidyAsync();
        Assert.Empty(await PreviewsAsync());
        Assert.Contains("docker rm -f gb-orb-pr-12", _rig.Engine.Writes);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed: previews were turned off.");
    }

    [Fact]
    public async Task Promote_is_refused_while_the_production_branch_cant_be_read()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == preview.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(null);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _previews.PromoteAsync(preview.Id));

        Assert.Equal("Gitberth can't see the newest commit on main right now, so production can't be kept on the preview's. Try again in a moment.", refused.Message);
        // Nothing was queued, and production isn't held.
        Assert.DoesNotContain(await DeploymentsAsync(_orb), d => d.Trigger == DeployTrigger.Promote);
        Assert.Null((await _rig.RowAsync(a => a.Id == _orb))!.PromotedOverCommit);
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
    public async Task A_watcher_pass_while_the_parent_is_being_deleted_never_makes_its_previews_again()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var first = Assert.Single(await PreviewsAsync()).Row;
        // The watcher's view of the repo from before the deletion: orb, with previews on.
        List<AppDefinition> watched;
        await using (var db = _rig.Db.CreateDbContext())
            watched = await RepoWatcher.Watched(db.Apps.AsNoTracking()).Where(a => a.Owner == "acme" && a.Repo == "orb").ToListAsync();

        // The deletion is held once orb's previews are gone, just before its own container goes, and a pass runs then.
        var held = new TaskCompletionSource();
        var go = new TaskCompletionSource();
        _rig.Engine.BeforeCommand = async line =>
        {
            if (line != "rm -f gb-orb") return;
            held.TrySetResult();
            await go.Task;
        };
        var deleting = _rig.Actions.DeleteAsync(_orb, removeImages: false, removeVolumes: false);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var remote = new RemoteInfo("main", [new RemoteBranch("main", Main), new RemoteBranch("feature/login", A)], [new RemotePull(12, A)]);
        await _previews.CheckRepoAsync("acme", "orb", remote, poked: true, watched, new HashSet<int>());
        go.SetResult();
        await deleting;

        // The pass made nothing of orb: no preview row, none deploying, nothing left for a later tidy-up.
        Assert.Empty(await PreviewsAsync());
        await using (var db = _rig.Db.CreateDbContext())
        {
            Assert.False(await db.Apps.AnyAsync(a => a.ParentAppId == _orb || a.Id > first.Id));
            Assert.False(await db.Deployments.AnyAsync(d => d.AppId > first.Id));
        }
        Assert.Null(await _rig.Settings.GetAsync(PreviewService.SkipsKey(_orb)));
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

        Assert.Equal("Between 1 and 20 previews can run at once.", (await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 0, 7, false, true))).Problem);
        Assert.Equal(new PreviewSave(null), await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, false, Protect: false)));
        Assert.False(Assert.Single(Assert.Single(await PreviewsAsync()).Row.Domains).Protected);

        Assert.Equal(new PreviewSave(null), await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.Off, 3, 7, false, false)));
        Assert.Empty(await PreviewsAsync());
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #12 removed: previews were turned off.");
    }

    [Fact]
    public async Task A_sync_that_fails_after_protection_changes_is_said_not_dropped()
    {
        await TurnOnAsync();
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _rig.AddAsync(new CustomDomain { AppId = _orb, Hostname = "app.example.com" });
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        // Domains are on, but Gitberth has no Cloudflare token: the sync can't reach the tunnel.

        // Off, the answer says the domain isn't open yet, and why.
        var off = await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, false, Protect: false));
        Assert.Null(off.Problem);
        Assert.Equal("app-pr-12.example.com isn't open to everyone yet: Gitberth couldn't change the tunnel. Domains aren't routed: Gitberth has no Cloudflare token. Add one in Settings.",
            off.Warning);

        // Back on, the domain is saved as protected, and the answer says it may still be open.
        var on = await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, false, Protect: true));
        Assert.Null(on.Problem);
        Assert.StartsWith("app-pr-12.example.com isn't behind Cloudflare Access yet, so anyone may still open it.", on.Warning);
        Assert.Contains("Gitberth has no Cloudflare token", on.Warning);
        Assert.True(Assert.Single(Assert.Single(await PreviewsAsync()).Row.Domains).Protected);
    }

    [Fact]
    public async Task Turning_forks_and_protection_off_at_once_names_only_the_domains_that_stay()
    {
        await TurnOnAsync();
        Assert.Equal(new PreviewSave(null), await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, Forks: true, Protect: true)));
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _rig.AddAsync(new CustomDomain { AppId = _orb, Hostname = "app.example.com" });
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}, {Pull(13, "Fix typo", "patch-1", B, repo: "someone/orb")}]";
        await CheckAsync();
        Assert.Equal(2, (await PreviewsAsync()).Count(p => p.Row.Domains.Count == 1));

        // The sync can't reach the tunnel (no Cloudflare token): the answer names the domain of the preview that stays,
        // not the fork's, which goes with its preview.
        var save = await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, Forks: false, Protect: false));
        Assert.Null(save.Problem);
        Assert.StartsWith("app-pr-12.example.com isn't open to everyone yet", save.Warning);
        Assert.DoesNotContain("app-pr-13", save.Warning);
        Assert.Equal(12, Assert.Single(await PreviewsAsync()).Info.PullRequest);
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

    [Fact]
    public async Task A_previews_name_never_gives_its_volumes_the_name_of_another_apps_or_of_one_docker_has()
    {
        // orb keeps its data in two named volumes: gb-orb-data, and gb-orb-test-data at /srv/test.
        await _rig.AddAsync(new VolumeMount { AppId = _orb, Source = "data", Target = "/data" });
        await _rig.AddAsync(new VolumeMount { AppId = _orb, Source = "test-data", Target = "/srv/test" });
        await TurnOnAsync(PreviewMode.PullRequestsAndBranches);
        await _rig.Settings.SetAsync(PreviewService.SkipsKey(_orb), "{\"@branches\":\"1\"}");
        // An older preview's volume Docker still has.
        _rig.Engine.Volumes.Add("gb-orb-pr-12-data");
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";

        // A branch called test would be orb-test, whose data volume is gb-orb-test-data: production's /srv/test.
        await CheckBranchesAsync(new RemoteBranch("main", Main), new RemoteBranch("test", B));

        List<AppDefinition> rows;
        await using (var db = _rig.Db.CreateDbContext())
            rows = await db.Apps.AsNoTracking().Include(a => a.Volumes).Where(a => a.ParentAppId == _orb).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Matches("^orb-test-[0-9a-f]{4}$", rows.Single(r => r.Name == "orb · test").Slug);
        Assert.Matches("^orb-pr-12-[0-9a-f]{4}$", rows.Single(r => r.Name == "orb · PR #12").Slug);
        var names = rows.SelectMany(r => r.Volumes.Select(v => VolumeNames.Resolve(r.Slug, v.Source))).ToList();
        Assert.Equal(4, names.Distinct().Count());
        Assert.DoesNotContain(names, n => n is "gb-orb-data" or "gb-orb-test-data" or "gb-orb-pr-12-data");
        // Nothing was taken from Docker: listing its volumes only reads.
        Assert.DoesNotContain(_rig.Engine.Writes, w => w.StartsWith("docker volume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_preview_never_deletes_a_volume_another_app_has()
    {
        // A preview from before its volumes were checked: its data is gb-orb-test-data, which is orb's test-data too.
        await _rig.AddAsync(new VolumeMount { AppId = _orb, Source = "test-data", Target = "/srv/test" });
        var preview = new AppDefinition { Name = "orb · test", Slug = "orb-test", Owner = "acme", Repo = "orb", Branch = "test", ParentAppId = _orb, AutoDeploy = false };
        preview.Volumes.AddRange([new VolumeMount { Source = "data", Target = "/data" }, new VolumeMount { Source = "logs", Target = "/logs" }]);
        var previewId = await _rig.AddAsync(preview);
        await _rig.AddAsync(new PreviewInfo { AppId = previewId, ParentAppId = _orb, Kind = PreviewKind.Branch, Branch = "test", Ref = "refs/heads/test", HeadSha = A });

        await _previews.RemoveAsync(previewId);

        Assert.Empty(await PreviewsAsync());
        Assert.Contains("docker volume rm gb-orb-test-logs", _rig.Engine.Writes);
        Assert.DoesNotContain("docker volume rm gb-orb-test-data", _rig.Engine.Writes);
    }

    [Fact]
    public async Task Turning_forks_off_removes_their_previews_and_none_of_their_code_is_deployed_again()
    {
        await TurnOnAsync();
        Assert.Equal(new PreviewSave(null), await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, Forks: true, Protect: true)));
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}, {Pull(13, "Fix typo", "patch-1", B, repo: "someone/orb")}]";
        await CheckAsync();
        var fork = (await PreviewsAsync()).Single(p => p.Info.FromFork).Row;

        // Forks turned off, before the fork's preview is removed: a deploy of it (a retry, a redeploy) builds nothing.
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _orb).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewForks, false));
        var deploymentId = await _rig.Actions.DeployAsync(fork.Id);
        await _rig.Pipeline.RunAsync(new DeployRequest(fork.Id, deploymentId, Build: true), CancellationToken.None);
        var refused = (await DeploymentsAsync(fork.Id)).Single(d => d.Id == deploymentId);
        Assert.Equal((DeployStatus.Failed, "orb doesn't preview pull requests from forks any more, so this one isn't deployed."), (refused.Status, refused.Error));

        // Saving the setting removes it at once; the pull request from this repo keeps its preview.
        Assert.Equal(new PreviewSave(null), await _previews.SaveSettingsAsync(_orb, new PreviewSettings(PreviewMode.PullRequests, 3, 7, Forks: false, Protect: true)));
        var left = Assert.Single(await PreviewsAsync());
        Assert.Equal((12, false), (left.Info.PullRequest, left.Info.FromFork));
        Assert.Contains($"docker rm -f {fork.ContainerName}", _rig.Engine.Writes);
        Assert.Contains(await _rig.Activity.ForAsync("gb-orb", _orb), i => i.Text == "Preview PR #13 removed: pull requests from forks aren't previewed any more.");
    }

    [Fact]
    public async Task A_promote_that_never_runs_lets_production_follow_its_branch_again()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == preview.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(Main);
        async Task<string?> PromotedOverAsync() => (await _rig.RowAsync(a => a.Id == _orb))!.PromotedOverCommit;
        // What the watcher does when the branch is still at the commit production was promoted over.
        async Task<bool> WatcherForgetsAsync()
        {
            await using var db = _rig.Db.CreateDbContext();
            return await RepoWatcher.ForgetUnheldPromotionAsync(db, _orb);
        }
        async Task<DeployStatus> StatusAsync(int id) => (await DeploymentsAsync(_orb)).Single(d => d.Id == id).Status;

        // Queued, it holds.
        var stopped = await _previews.PromoteAsync(preview.Id);
        Assert.False(await WatcherForgetsAsync());
        Assert.Equal(Main, await PromotedOverAsync());

        // Stopped before it ran: production follows its branch again.
        await _rig.Actions.StopAsync(_orb);
        Assert.Equal(DeployStatus.Cancelled, await StatusAsync(stopped));
        Assert.Null(await PromotedOverAsync());

        // Superseded by a newer deploy (settings applied) before it ran: the same.
        var superseded = await _previews.PromoteAsync(preview.Id);
        Assert.Equal(Main, await PromotedOverAsync());
        await _rig.Actions.RecreateAsync(_orb, DeployTrigger.ConfigChange);
        await WaitForAsync(async () => await StatusAsync(superseded) == DeployStatus.Cancelled && await PromotedOverAsync() is null);

        // Superseded by a newer promote: that one holds.
        var first = await _previews.PromoteAsync(preview.Id);
        var second = await _previews.PromoteAsync(preview.Id);
        await WaitForAsync(async () => await StatusAsync(first) == DeployStatus.Cancelled);
        Assert.False(await WatcherForgetsAsync());
        Assert.Equal(Main, await PromotedOverAsync());

        // One that worked holds, whatever ran after it (a scheduled restart, say).
        await using (var db = _rig.Db.CreateDbContext())
        {
            await db.Deployments.Where(d => d.Id == second).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeployStatus.Succeeded));
            db.Deployments.Add(new Deployment { AppId = _orb, Trigger = DeployTrigger.Restart, Status = DeployStatus.Succeeded });
            await db.SaveChangesAsync();
        }
        Assert.False(await WatcherForgetsAsync());
        Assert.Equal(Main, await PromotedOverAsync());

        // A newer one cut short by a restart of Gitberth doesn't.
        var cut = await _previews.PromoteAsync(preview.Id);
        Directory.CreateDirectory(_rig.Options.WorkDir);
        var startup = new StartupTasks(_rig.Db, _rig.Cli, _rig.AppLocks, _rig.Actions, _rig.Pipeline, _rig.Options, NullLogger<StartupTasks>.Instance);
        await startup.StartAsync(CancellationToken.None);
        if (startup.ExecuteTask is { } pass) await pass.WaitAsync(TimeSpan.FromSeconds(30));
        await startup.StopAsync(CancellationToken.None);
        Assert.Equal(DeployStatus.Failed, await StatusAsync(cut));
        Assert.Null(await PromotedOverAsync());

        // Left behind without its promote (an older Gitberth's), it doesn't hold either.
        await using (var db = _rig.Db.CreateDbContext())
        {
            await db.Deployments.Where(d => d.AppId == _orb && d.Trigger == DeployTrigger.Promote).ExecuteDeleteAsync();
            await db.Apps.Where(a => a.Id == _orb).ExecuteUpdateAsync(s => s.SetProperty(a => a.PromotedOverCommit, Main));
        }
        Assert.True(await WatcherForgetsAsync());
        Assert.Null(await PromotedOverAsync());
    }

    [Fact]
    public async Task The_watcher_deploys_the_branch_head_once_a_promote_no_longer_holds()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == preview.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(Main);
        async Task<AppDefinition> OrbAsync() => (await _rig.RowAsync(a => a.Id == _orb))!;
        // What the watcher starts for orb when its branch is at this commit: a deploy (Poll here), a retry, or nothing.
        async Task<DeployTrigger?> NextAsync(string head) =>
            await RepoWatcher.NextDeployAsync(_rig.Db, await OrbAsync(), head, DeployTrigger.Poll, NullLogger.Instance, CancellationToken.None);
        async Task SetStatusAsync(int deploymentId, DeployStatus status)
        {
            await using var db = _rig.Db.CreateDbContext();
            await db.Deployments.Where(d => d.Id == deploymentId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status));
        }

        // Promoted over Main and queued: production stays where it is.
        var never = await _previews.PromoteAsync(preview.Id);
        Assert.Null(await NextAsync(Main));
        Assert.Equal(Main, (await OrbAsync()).PromotedOverCommit);

        // That promote ended without running, and nothing let production go when it did: the watcher does, and deploys
        // Main, which production doesn't run and hasn't tried (a promote's commit isn't the branch's, so it isn't noted).
        await SetStatusAsync(never, DeployStatus.Failed);
        Assert.Equal(("1d2948cc693a", null), ((await OrbAsync()).CurrentCommit, (await OrbAsync()).LastAttemptedCommit));
        Assert.Equal(DeployTrigger.Poll, await NextAsync(Main));
        Assert.Null((await OrbAsync()).PromotedOverCommit);

        // One that worked holds while the branch stays at Main; a new commit there is deployed, and production follows it.
        var worked = await _previews.PromoteAsync(preview.Id);
        await SetStatusAsync(worked, DeployStatus.Succeeded);
        Assert.Null(await NextAsync(Main));
        Assert.Equal(Main, (await OrbAsync()).PromotedOverCommit);
        Assert.Equal(DeployTrigger.Poll, await NextAsync(B));
        Assert.Null((await OrbAsync()).PromotedOverCommit);
    }

    [Fact]
    public async Task A_promote_that_never_runs_leaves_a_rollback_where_it_is()
    {
        await TurnOnAsync();
        _github.Pulls = $"[{Pull(12, "Add login", "feature/login", A)}]";
        await CheckAsync();
        var preview = Assert.Single(await PreviewsAsync()).Row;
        _previews.ProductionHead = (_, _) => Task.FromResult<string?>(Main);
        // orb tried Main, then was rolled back to the commit before it: the watcher leaves it there.
        await using (var db = _rig.Db.CreateDbContext())
        {
            await db.Apps.Where(a => a.Id == preview.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentCommit, A).SetProperty(a => a.CurrentImage, "gitberth/orb-pr-12:aaaaaaa"));
            db.Deployments.Add(new Deployment { AppId = _orb, Trigger = DeployTrigger.Poll, CommitSha = Main, Status = DeployStatus.Succeeded });
            db.Deployments.Add(new Deployment { AppId = _orb, Trigger = DeployTrigger.Rollback, CommitSha = "1d2948cc693a", Status = DeployStatus.Succeeded });
            await db.SaveChangesAsync();
            await db.Apps.Where(a => a.Id == _orb).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastAttemptedCommit, Main));
        }
        async Task<AppDefinition> OrbAsync() => (await _rig.RowAsync(a => a.Id == _orb))!;
        async Task<DeployTrigger?> NextAsync() =>
            await RepoWatcher.NextDeployAsync(_rig.Db, await OrbAsync(), Main, DeployTrigger.Poll, NullLogger.Instance, CancellationToken.None);
        Assert.Null(await NextAsync());

        // The preview is promoted, and the promote is cancelled before it runs: the rollback stays.
        var never = await _previews.PromoteAsync(preview.Id);
        Assert.Equal(Main, (await OrbAsync()).LastAttemptedCommit);
        await using (var db = _rig.Db.CreateDbContext())
            await db.Deployments.Where(d => d.Id == never).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeployStatus.Cancelled));
        Assert.Null(await NextAsync());
        Assert.Equal(("1d2948cc693a", null), ((await OrbAsync()).CurrentCommit, (await OrbAsync()).PromotedOverCommit));
    }
}
