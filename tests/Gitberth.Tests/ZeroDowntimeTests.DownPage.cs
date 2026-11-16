using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Public;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// A deploy that has to stop the running container first (the plain swap: here the app has a volume, so two copies can't
/// run at once): its domains go to the down page, and cloudflared runs that, before the old container stops; they come
/// back once the new version passed its checks, or once the old one is back after a rollback.
/// </summary>
public sealed partial class ZeroDowntimeTests
{
    private const string OwnService = "http://localhost:8080";

    /// <summary>A pipeline whose swaps put the domains on the down page, with a domain sync that knows it.</summary>
    private (DeployPipeline Pipeline, DownStates States) WithDownPage()
    {
        var states = new DownStates();
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new Factory(_cloudflare);
        var domains = new DomainService(_db, _settings, new CloudflareApi(http), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: states)
        {
            TunnelPoll = TimeSpan.FromMilliseconds(5),
        };
        var pipeline = new DeployPipeline(_db, new GitClient(_options, _settings), docker, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(), _protector,
            _options, http, new RepoVariables(new GitHubApi(http), _settings), domains, new WatchState(), new Lifetime(), NullLogger<DeployPipeline>.Instance,
            downStates: states)
        {
            StableFor = TimeSpan.FromMilliseconds(40),
            StatePoll = TimeSpan.FromMilliseconds(5),
            TunnelWait = TimeSpan.FromSeconds(5),
            ListenWait = TimeSpan.Zero,
        };
        return (pipeline, states);
    }

    private async Task WithVolumeAsync()
    {
        await using var db = _db.CreateDbContext();
        var app = await db.Apps.SingleAsync(a => a.Id == _appId);
        db.Volumes.Add(new VolumeMount { AppId = app.Id, Source = "data", Target = "/data" });
        await db.SaveChangesAsync();
    }

    private async Task<(Deployment Deployment, string Log)> RedeployWithAsync(DeployPipeline pipeline, string image = "gitberth/web:2")
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = _appId, Trigger = DeployTrigger.ConfigChange, ImageTag = image, CommitSha = NewCommit };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await pipeline.RunAsync(new DeployRequest(_appId, id, Build: false, Image: image), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return (after.Deployments.Single(d => d.Id == id), File.ReadAllText(DeployPaths.LogFile(_options, id)));
    }

    private JsonObject? RuleAtStop { get; set; }

    // The tunnel's rule for the app's domain at the moment the old container is stopped.
    private void WatchTheStop() => _docker.OnChange = change =>
    {
        if (change == "stop gb-web") RuleAtStop = (JsonObject?)_cloudflare.Ingress.OfType<JsonObject>().SingleOrDefault(r => (string?)r["hostname"] == Hostname)?.DeepClone();
    };

    private int DomainId()
    {
        using var db = _db.CreateDbContext();
        return db.Domains.Single().Id;
    }

    [Fact]
    public async Task A_plain_swap_shows_the_down_page_before_the_old_container_stops_and_comes_back_after_the_checks()
    {
        await WithVolumeAsync();
        _cloudflare.ConfirmAfter = TimeSpan.FromMilliseconds(200);
        var (pipeline, states) = WithDownPage();
        WatchTheStop();

        var (dep, log) = await RedeployWithAsync(pipeline);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(
        [
            $"PUT {OwnService}",
            "cloudflared runs 11",
            "stop gb-web",
            "rename gb-web gb-web_prev",
            "run gb-web 3001",
            "rm gb-web_prev",
            "PUT http://localhost:3001",
            "cloudflared runs 12",
        ], Timeline());
        // While the old one stopped, visitors got the down page: Gitberth's own service, with the domain's marker.
        Assert.Equal((OwnService, MarkerHosts.Down(DomainId())), ((string?)RuleAtStop!["service"], TunnelRoutes.HostHeaderOf(RuleAtStop)));
        // And back: the app's port, the marker gone.
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.Null(_cloudflare.Ingress.OfType<JsonObject>().Single(r => (string?)r["hostname"] == Hostname)["originRequest"]);
        Assert.False(states.IsUpdating(_appId));
        Assert.Contains($"Sending {Hostname} to the down page while the container is replaced", log);
        Assert.Contains("Sending visitors back to the app", log);
    }

    [Fact]
    public async Task A_rolled_back_swap_sends_visitors_back_to_the_old_version()
    {
        await WithVolumeAsync();
        var (pipeline, states) = WithDownPage();
        // The new version exits at once: its checks fail, and the old one is started again.
        _docker.Crashes = c => c.Image == "gitberth/web:2";

        var (dep, _) = await RedeployWithAsync(pipeline);

        Assert.Equal(DeployStatus.RolledBack, dep.Status);
        var timeline = Timeline();
        Assert.True(timeline.IndexOf($"PUT {OwnService}") < timeline.IndexOf("stop gb-web"), string.Join(" | ", timeline));
        Assert.True(timeline.IndexOf("start gb-web") < timeline.LastIndexOf("PUT http://localhost:3001"), string.Join(" | ", timeline));
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.Null(states.For(_appId));
    }

    [Fact]
    public async Task With_its_down_page_off_a_plain_swap_leaves_the_tunnel_alone()
    {
        await WithVolumeAsync();
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DownPage, false));
        var (pipeline, _) = WithDownPage();

        var (dep, _) = await RedeployWithAsync(pipeline);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.DoesNotContain(Timeline(), t => t.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stopped_app_shows_the_down_page_and_its_domain_goes_back_once_it_runs()
    {
        var (_, states) = WithDownPage();
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: states);
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        _docker.Containers.Single().Running = false;
        states.Set(_appId, DownReason.Stopped);
        states.MarkReady();

        var down = await domains.SyncAsync();

        Assert.Equal(OwnService, TunnelService(Hostname));
        Assert.Contains(down.Notes, n => n.Text == $"{Hostname} shows Gitberth's down page now");
        var card = Assert.Single((await domains.GetOverviewAsync(_appId)).Domains);
        Assert.True(card.DownPage);
        Assert.Equal("Shows the down page: Web is stopped.", card.Summary);
        await using (var db = _db.CreateDbContext())
            Assert.Equal(OwnService, (await db.Domains.SingleAsync()).Service);
        // Gitberth's own port isn't one routed to an app.
        Assert.DoesNotContain(await domains.RoutedHostPortsAsync(), r => r.Port == 8080);

        // Started again: once its container runs, the domain goes back to it.
        _docker.Containers.Single().Running = true;
        states.Set(_appId, null);
        var back = await domains.SyncAsync();

        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.Contains(back.Notes, n => n.Text == $"{Hostname} goes to Web again");
        Assert.False(Assert.Single((await domains.GetOverviewAsync(_appId)).Domains).DownPage);
    }

    [Fact]
    public async Task A_domain_left_on_the_down_page_goes_back_where_its_app_will_be_once_its_down_page_is_turned_off()
    {
        var (_, states) = WithDownPage();
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: states);
        _docker.Containers.Single().Running = false;
        states.Set(_appId, DownReason.NotRunning);
        states.MarkReady();
        await domains.SyncAsync();
        Assert.Equal(OwnService, TunnelService(Hostname));

        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DownPage, false));
        states.Set(_appId, null);
        await domains.SyncAsync();

        // Still stopped: Cloudflare's own error, as before 1.3, but never a rule left on Gitberth.
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.Null(_cloudflare.Ingress.OfType<JsonObject>().Single(r => (string?)r["hostname"] == Hostname)["originRequest"]);
    }

    private int Puts() => Timeline().Count(t => t.StartsWith("PUT", StringComparison.Ordinal));

    [Fact]
    public async Task A_crashed_apps_domain_stays_on_its_down_page_while_nothing_is_decided_for_it_yet()
    {
        var (_, states) = WithDownPage();
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: states);
        _docker.Containers.Single().Running = false;
        states.Set(_appId, DownReason.NotRunning);
        states.MarkReady();
        await domains.SyncAsync();
        Assert.Equal(OwnService, TunnelService(Hostname));
        var puts = Puts();

        // Gitberth restarts: the watcher's first look starts the container's grace afresh, so nothing is decided for the app
        // yet, and that look asks for a sync all the same. (A container that stopped a moment ago is in the same place.)
        var restarted = new DownStates();
        restarted.MarkReady();
        var again = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: restarted);
        var report = await again.SyncAsync();

        // It stays on its down page: no Cloudflare error for the grace, and no tunnel change to undo once it's over.
        Assert.Equal(OwnService, TunnelService(Hostname));
        Assert.Equal(puts, Puts());
        Assert.DoesNotContain(report.Notes, n => n.Text == $"{Hostname} goes to Web again");
    }

    private string? HostHeader(string hostname)
    {
        lock (_docker) return TunnelRoutes.HostHeaderOf(_cloudflare.Ingress.OfType<JsonObject>().Single(r => (string?)r["hostname"] == hostname));
    }

    // The owner set a Host header on the domain's route in Cloudflare: an origin that answers only to that name, say.
    private void WithOwnersHostHeader() => _cloudflare.Ingress =
    [
        new JsonObject { ["hostname"] = Hostname, ["service"] = "http://localhost:3001", ["originRequest"] = new JsonObject { ["httpHostHeader"] = "intranet.local" } },
        new JsonObject { ["service"] = "http_status:404" },
    ];

    [Fact]
    public async Task A_domain_whose_route_sets_the_owners_host_header_keeps_it_through_a_stop_and_a_start()
    {
        WithOwnersHostHeader();
        var (_, states) = WithDownPage();
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance, down: states);
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        _docker.Containers.Single().Running = false;
        states.Set(_appId, DownReason.Stopped);
        states.MarkReady();

        var stopped = await domains.SyncAsync();

        // Left as before 1.3: the down page's marker would take the Host header's place, and couldn't give it back.
        Assert.Equal(("http://localhost:3001", "intranet.local"), (TunnelService(Hostname), HostHeader(Hostname)));
        Assert.Equal(0, Puts());
        Assert.Contains(stopped.Notes, n => n.Problem && n.Hostname == Hostname && n.Text == $"{Hostname} can't show Gitberth's down page: its route in the tunnel "
            + "sets the Host header \"intranet.local\" for Web, which the down page would replace. Visitors get Cloudflare's error page instead.");
        var card = Assert.Single((await domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(card.DownPage);
        Assert.Equal("Not serving: Web isn't running, or its saved ports aren't live yet. It doesn't show the down page: its route in the tunnel sets the Host header "
            + "\"intranet.local\", which the down page would replace.", card.Summary);

        // Started again: the domain goes on to the app, its Host header and all.
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Running));
        _docker.Containers.Single().Running = true;
        states.Set(_appId, null);
        await domains.SyncAsync();

        Assert.Equal(("http://localhost:3001", "intranet.local"), (TunnelService(Hostname), HostHeader(Hostname)));
        Assert.Equal(0, Puts());
    }

    [Fact]
    public async Task A_plain_swap_leaves_a_domain_whose_route_sets_the_owners_host_header_on_the_app()
    {
        await WithVolumeAsync();
        WithOwnersHostHeader();
        var (pipeline, _) = WithDownPage();

        var (dep, log) = await RedeployWithAsync(pipeline);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(0, Puts());
        Assert.Equal(("http://localhost:3001", "intranet.local"), (TunnelService(Hostname), HostHeader(Hostname)));
        Assert.Contains($"warning: {Hostname} can't show Gitberth's down page: its route in the tunnel sets the Host header \"intranet.local\" for Web", log);
    }
}
