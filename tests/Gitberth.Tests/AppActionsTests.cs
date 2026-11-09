using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Starting an app and applying its ports. A deploy they start is only queued here, never run: what's tested is whether
/// the container is recreated or started as it was.
/// </summary>
public sealed class AppActionsTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>Nothing here goes out to GitHub or Cloudflare.</summary>
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to GitHub or Cloudflare.");
    }

    /// <summary>Gitberth is stopping, so nothing takes a deploy off the queue and runs it.</summary>
    private sealed class Stopping : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => new(canceled: true);
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly AppActions _actions;
    private readonly List<string> _docker = [];
    // kanban's container exists; whether it runs is up to the test.
    private bool _running = true;
    // Whether kanban's container is there at all, and the images on this host.
    private bool _exists = true;
    private readonly HashSet<string> _images = ["gitberth/kanban:1"];
    private readonly int _appId;

    public AppActionsTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:1", CurrentCommit = "1111111" };
            app.Ports.AddRange([new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true }, new PortMapping { ContainerPort = 9000, HostPort = 3002 }]);
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(_db, protector);
        var cli = new DockerCli(options) { Runner = RunAsync };
        var http = new NoHttp();
        var domains = new DomainService(_db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var restarts = new RestartSchedules(settings);
        var watch = new WatchState();
        var apps = new AppService(_db, protector, cli, settings, new HealthSettings(settings), domains, options, restarts, watch);
        var locks = new AppLocks();
        var hub = new LiveLogHub();
        var repoVariables = new RepoVariables(new GitHubApi(http), settings);
        var pipeline = new DeployPipeline(_db, new GitClient(options, settings), cli, new ProjectAnalyzer(), locks, hub, protector, options, http, repoVariables,
            domains, watch, new Stopping(), NullLogger<DeployPipeline>.Instance);
        var queue = new DeployQueue(pipeline, hub, _db, new Stopping(), NullLogger<DeployQueue>.Instance);
        _actions = new AppActions(_db, queue, cli, locks, options, repoVariables, domains, restarts, apps, NullLogger<AppActions>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    // Enough of the docker CLI for starting kanban's container: whether it and its image exist, whether it runs, and start.
    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
    {
        lock (_docker) _docker.Add(string.Join(' ', args));
        var mine = args[^1] == "gb-kanban" && _exists;
        return Task.FromResult(args switch
        {
            ["image", "inspect", "--format", _, var image] when _images.Contains(image) => new ProcessResult(0, "sha256:5d0da3dc9764\n", ""),
            ["container", "inspect", "--format", _, _] when mine => new ProcessResult(0, "27c0957a4ff7\n", ""),
            ["container", "inspect", _] when mine =>
                new ProcessResult(0, $$"""[{"State":{"Running":{{(_running ? "true" : "false")}},"Status":"{{(_running ? "running" : "exited")}}","ExitCode":0},"RestartCount":0}]""", ""),
            ["start", _] when mine => new ProcessResult(0, "gb-kanban\n", ""),
            // The images of kanban's builds still on the host: two of them, and a dangling one.
            ["image", "ls", "gitberth/kanban", "--format", _] => new ProcessResult(0, "gitberth/kanban:1\ngitberth/kanban:2\ngitberth/kanban:<none>\n", ""),
            _ => new ProcessResult(1, "", "Error: No such container: " + args[^1]),
        });
    }

    private async Task PendingAsync(bool pending)
    {
        await using var db = _db.CreateDbContext();
        await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ConfigChangePending, pending));
    }

    private List<Deployment> Deployments()
    {
        using var db = _db.CreateDbContext();
        return db.Deployments.AsNoTracking().OrderBy(d => d.Id).ToList();
    }

    private string Loaded()
    {
        using var db = _db.CreateDbContext();
        return AppSettingsModel.PortsKey(db.Ports.AsNoTracking().Where(p => p.AppId == _appId).ToList());
    }

    private static PortRow Row(int container, int host, bool web = false) => new() { ContainerPort = container, HostPort = host, IsWeb = web };

    [Fact]
    public async Task Start_applies_pending_settings()
    {
        // Ports saved for the next deploy while it was stopped: the container that's there still has the old ones.
        _running = false;
        await PendingAsync(true);

        Assert.Null(await _actions.StartAsync(_appId));

        // It's recreated from its current image rather than started as it was.
        Assert.DoesNotContain("start gb-kanban", _docker);
        var recreate = Assert.Single(Deployments());
        Assert.Equal((DeployTrigger.Start, "gitberth/kanban:1", DeployStatus.Queued), (recreate.Trigger, recreate.ImageTag, recreate.Status));

        // With nothing pending, it starts as it was.
        await PendingAsync(false);
        Assert.Null(await _actions.StartAsync(_appId));
        Assert.Contains("start gb-kanban", _docker);
        Assert.Single(Deployments());
    }

    [Fact]
    public async Task Roll_back_to_this_is_offered_only_for_a_deploy_whose_image_is_still_here()
    {
        var kept = await _actions.RollbackImagesAsync(_appId);

        Assert.Equal(["gitberth/kanban:1", "gitberth/kanban:2"], kept.Order());
        Deployment Succeeded(string image) => new() { AppId = _appId, Status = DeployStatus.Succeeded, ImageTag = image };
        // kanban runs :2; :1 is kept; :0's image went with the cleanup after later deploys, and a deploy can't build an old commit.
        Assert.True(AppActivityTab.CanRollBackTo(Succeeded("gitberth/kanban:1"), "gitberth/kanban:2", kept));
        Assert.False(AppActivityTab.CanRollBackTo(Succeeded("gitberth/kanban:0"), "gitberth/kanban:2", kept));
        Assert.False(AppActivityTab.CanRollBackTo(Succeeded("gitberth/kanban:2"), "gitberth/kanban:2", kept));
        Assert.False(AppActivityTab.CanRollBackTo(new Deployment { AppId = _appId, Status = DeployStatus.Failed, ImageTag = "gitberth/kanban:1" }, "gitberth/kanban:2", kept));
    }

    [Fact]
    public async Task Start_makes_a_gone_container_again_from_its_last_image_when_that_is_still_here()
    {
        _exists = false;
        await using (var db = _db.CreateDbContext())
            Assert.True(await _actions.HasLastImageAsync(db.Apps.Single(a => a.Id == _appId)));

        Assert.Null(await _actions.StartAsync(_appId));

        // A deploy without a build, on the image it ran.
        var recreate = Assert.Single(Deployments());
        Assert.Equal((DeployTrigger.Start, "gitberth/kanban:1", DeployStatus.Queued), (recreate.Trigger, recreate.ImageTag, recreate.Status));
        Assert.DoesNotContain("start gb-kanban", _docker);
    }

    [Fact]
    public async Task Start_builds_a_gone_container_again_when_its_image_is_gone_too()
    {
        // Restored onto a new host, say: neither the container nor the image it ran is here. A recreate from that image
        // could only fail ("Image … no longer exists"), so Start queues a normal deploy that builds it.
        (_exists, _running) = (false, false);
        _images.Clear();
        await using (var db = _db.CreateDbContext())
            Assert.False(await _actions.HasLastImageAsync(db.Apps.Single(a => a.Id == _appId)));

        Assert.Null(await _actions.StartAsync(_appId));

        var build = Assert.Single(Deployments());
        Assert.Equal((DeployTrigger.Start, null, DeployStatus.Queued), (build.Trigger, build.ImageTag, build.Status));
        // Settings saved for the next start go with that build too, rather than a recreate from the missing image.
        await PendingAsync(true);
        _exists = true;
        Assert.Null(await _actions.StartAsync(_appId));
        Assert.Equal([null, null], Deployments().Select(d => d.ImageTag));
    }

    [Fact]
    public void The_banner_says_what_start_does_for_a_git_app_whose_container_is_gone()
    {
        var app = new AppSummary("gb-kanban", 1, "Kanban", "gb-kanban", AppSource.Git, ContainerRole.App, ImageOrigin.Built, "gitberth/kanban:1", null,
            AppState.Missing, "Missing", null, [], null, null, null, false, false, null);

        Assert.Equal("Gitberth still has its settings and its last image. Start makes its container again from that image, without a build.",
            Gitberth.Components.Pages.AppPage.MissingLine(app, lastImage: true, "main"));
        Assert.Equal("Gitberth still has its settings, but its last image isn't on this host any more. Start builds it again from main and deploys it.",
            Gitberth.Components.Pages.AppPage.MissingLine(app, lastImage: false, "main"));
    }

    [Fact]
    public async Task Applying_ports_recreates_a_running_app_once()
    {
        var applied = await _actions.ApplyPortsAsync(_appId, [Row(3000, 3005, web: true), Row(9000, 3002)], Loaded(), applyNow: true);

        Assert.Equal((true, true), (applied.Ok, applied.NeedsRestart));
        var deploy = Assert.Single(Deployments());
        Assert.Equal((applied.DeploymentId, DeployTrigger.ConfigChange, "gitberth/kanban:1"), (deploy.Id, deploy.Trigger, deploy.ImageTag));
    }

    [Fact]
    public async Task Ports_wait_when_saved_for_the_next_deploy_or_while_it_is_stopped()
    {
        // Save for next deploy: saved, with the pending banner up.
        var saved = await _actions.ApplyPortsAsync(_appId, [Row(3000, 3005, web: true), Row(9000, 3002)], Loaded(), applyNow: false);
        Assert.Equal((true, true, null), (saved.Ok, saved.NeedsRestart, saved.DeploymentId));
        await using (var db = _db.CreateDbContext())
            Assert.True(db.Apps.Single(a => a.Id == _appId).ConfigChangePending);

        // A stopped app takes them when it starts.
        _running = false;
        var stopped = await _actions.ApplyPortsAsync(_appId, [Row(3000, 3006, web: true), Row(9000, 3002)], Loaded(), applyNow: true);
        Assert.Equal((true, true, null), (stopped.Ok, stopped.NeedsRestart, stopped.DeploymentId));

        // Moving only the web marker restarts nothing, even when asked to apply.
        _running = true;
        var marker = await _actions.ApplyPortsAsync(_appId, [Row(3000, 3006), Row(9000, 3002, web: true)], Loaded(), applyNow: true);
        Assert.Equal((true, false, null), (marker.Ok, marker.NeedsRestart, marker.DeploymentId));
        Assert.Empty(Deployments());
    }

    // S1: a build of a new commit runs. A recreate queued behind it would be pinned to the image from before it, and put
    // that back once it's done, so [Apply] follows the build instead, however often it's clicked.
    [Fact]
    public async Task Apply_while_a_deploy_of_the_app_runs_follows_that_deploy()
    {
        int building;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = _appId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building, CommitSha = "2222222", ImageTag = "gitberth/kanban:2222222" };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            building = dep.Id;
        }

        Assert.Equal(building, await _actions.ApplyIfRunningAsync(_appId));
        Assert.Equal(building, await _actions.ApplyIfRunningAsync(_appId));
        // Ports applied meanwhile are saved, and wait for it too.
        var applied = await _actions.ApplyPortsAsync(_appId, [Row(3000, 3005, web: true), Row(9000, 3002)], Loaded(), applyNow: true);
        Assert.Equal((true, true, building), (applied.Ok, applied.NeedsRestart, applied.DeploymentId));
        Assert.Equal([building], Deployments().Select(d => d.Id));
    }

    // S1: the second click follows the recreate the first one queued. Another app's deploy, or this app's finished ones, don't count.
    [Fact]
    public async Task A_second_apply_click_follows_the_recreate_the_first_one_queued()
    {
        await using (var db = _db.CreateDbContext())
        {
            var other = new AppDefinition { Name = "Orb", Slug = "orb", Owner = "acme", Repo = "orb", CurrentImage = "gitberth/orb:1", CurrentCommit = "3333333" };
            db.Apps.Add(other);
            await db.SaveChangesAsync();
            db.Deployments.AddRange(
                new Deployment { AppId = other.Id, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building, CommitSha = "4444444" },
                new Deployment { AppId = _appId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = "gitberth/kanban:1", CommitSha = "1111111" });
            await db.SaveChangesAsync();
        }

        var first = await _actions.ApplyIfRunningAsync(_appId);
        var second = await _actions.ApplyIfRunningAsync(_appId);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        var recreate = Assert.Single(Deployments(), d => d.Trigger == DeployTrigger.ConfigChange);
        Assert.Equal((first, _appId, "gitberth/kanban:1", DeployStatus.Queued), (recreate.Id, recreate.AppId, recreate.ImageTag, recreate.Status));
    }
}
