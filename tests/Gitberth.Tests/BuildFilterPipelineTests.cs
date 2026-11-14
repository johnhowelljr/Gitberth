using System.Net;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// A new commit through the deploy pipeline with build filters and the commit-message markers: git and docker stand-ins
/// answer what a deploy asks (clone, the commit, fetching the running commit, the diff, image checks, the build). A skipped
/// commit ends Skipped with nothing built and the running version kept, the watcher doesn't start it again, and the next
/// commit is compared with the running one. Also GitHub's status for a skipped commit, and a build without the cache.
/// </summary>
public sealed class BuildFilterPipelineTests : IDisposable
{
    private const string Running = "1111111111111111111111111111111111111111";
    private const string DocsCommit = "2222222222222222222222222222222222222222";
    private const string CodeCommit = "3333333333333333333333333333333333333333";
    private const string Image = "gitberth/orb:1111111";
    private const string CloneUrl = "https://github.com/acme/orb.git";

    private static ProcessResult Ok(string output) => new(0, output, "");

    /// <summary>git as a deploy uses it: a clone (or a fetch of a ref) that checks out a folder with a Dockerfile, the tip's commit, fetches by SHA, and diffs.</summary>
    private sealed class FakeGit
    {
        public string Tip { get; set; } = DocsCommit;
        public string Message { get; set; } = "Update the docs";
        /// <summary>The files that differ between a commit and the tip, by that commit.</summary>
        public Dictionary<string, string[]> ChangedSince { get; } = new(StringComparer.Ordinal);
        /// <summary>Commits GitHub doesn't have any more (a force-push).</summary>
        public HashSet<string> Gone { get; } = new(StringComparer.Ordinal);
        public bool DiffFails { get; set; }
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken ct)
        {
            lock (Commands) Commands.Add(string.Join(' ', args));
            var subject = Message.Split('\n')[0];
            return Task.FromResult(args[0] switch
            {
                "clone" or "init" => Checkout(args[^1]),
                "fetch" when args[^1].StartsWith("refs/", StringComparison.Ordinal) => Ok(""),
                "fetch" => Gone.Contains(args[^1]) ? new ProcessResult(128, "", $"fatal: remote error: upload-pack: not our ref {args[^1]}") : Ok(""),
                "log" => Ok($"{Tip}\u001fAda\u001f2026-11-12T10:00:00+00:00\u001f{subject}\u001f{Message}\n"),
                "diff" => DiffFails ? new ProcessResult(128, "", "fatal: bad object") : Ok(string.Concat((ChangedSince.GetValueOrDefault(args[4]) ?? []).Select(f => f + "\0")) + "\n"),
                _ => Ok(""),
            });
        }

        public int Count(string command)
        {
            lock (Commands) return Commands.Count(c => c == command);
        }

        public bool Ran(string start)
        {
            lock (Commands) return Commands.Any(c => c.StartsWith(start, StringComparison.Ordinal));
        }

        private static ProcessResult Checkout(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.22\nEXPOSE 8080\nCMD [\"true\"]\n");
            return Ok("");
        }
    }

    /// <summary>The docker CLI: images by name, and every build, which fails (nothing here builds).</summary>
    private sealed class FakeDocker
    {
        public HashSet<string> Images { get; } = new(StringComparer.Ordinal) { Image };
        public List<List<string>> Builds { get; } = [];
        /// <summary>What a build that pulls the base images says, as BuildKit does when one is only on this host; null: the usual failure.</summary>
        public string? PullError { get; set; }

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            lock (Builds)
            {
                return Task.FromResult(args switch
                {
                    ["image", "inspect", .., var image] => Images.Contains(image) ? Ok("sha256:1\n") : new ProcessResult(1, "", $"Error: No such image: {image}"),
                    ["build", ..] => Build(args),
                    _ => Ok(""),
                });
            }
        }

        private ProcessResult Build(IReadOnlyList<string> args)
        {
            Builds.Add([.. args]);
            return new ProcessResult(1, "", PullError is { } error && args.Contains("--pull") ? error : "the test's docker builds nothing");
        }
    }

    /// <summary>What the pipeline told: each report, started or finished.</summary>
    private sealed class Reports : IDeployReporter
    {
        public List<(bool Started, DeployReport Report)> All { get; } = [];

        public Task StartedAsync(DeployReport report, CancellationToken ct)
        {
            lock (All) All.Add((true, report));
            return Task.CompletedTask;
        }

        public Task FinishedAsync(DeployReport report, CancellationToken ct)
        {
            lock (All) All.Add((false, report));
            return Task.CompletedTask;
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to GitHub or Cloudflare.");
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly ImageKit.Db _db = new();
    private readonly GitberthOptions _options = new() { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-filters-" + Guid.NewGuid().ToString("N")[..8]) };
    private readonly FakeGit _git = new();
    private readonly FakeDocker _docker = new();
    private readonly Reports _reports = new();
    private readonly DeployPipeline _pipeline;

    public BuildFilterPipelineTests()
    {
        Directory.CreateDirectory(_options.LogsDir);
        Directory.CreateDirectory(_options.WorkDir);
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(_db, protector);
        var cli = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new NoHttp();
        var domains = new DomainService(_db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _pipeline = new DeployPipeline(_db, new GitClient(_options, settings) { Runner = _git.RunAsync }, cli, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(),
            protector, _options, http, new RepoVariables(new GitHubApi(http), settings), domains, new WatchState(), new Lifetime(), NullLogger<DeployPipeline>.Instance,
            reporter: _reports);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_options.DataDir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<int> AddAppAsync(string? watch = null, string? ignored = null, string? current = Running, string? image = Image, string slug = "orb")
    {
        var app = new AppDefinition
        {
            Name = slug, Slug = slug, Owner = "acme", Repo = "orb", CurrentImage = image, CurrentCommit = current, LastAttemptedCommit = current,
            WatchPaths = watch, IgnoredPaths = ignored,
        };
        app.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 3000, IsWeb = true });
        return (await _db.AddAsync(app)).Id;
    }

    private async Task<(Deployment Deployment, AppDefinition App, string Log)> DeployAsync(int appId, DeployTrigger trigger, bool noCache = false)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = appId, Trigger = trigger, NoCache = noCache };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(appId, id, Build: true, NoCache: noCache), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return (await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id), await after.Apps.AsNoTracking().SingleAsync(a => a.Id == appId),
            await File.ReadAllTextAsync(DeployPaths.LogFile(_options, id)));
    }

    private List<(bool Started, DeployReport Report)> ReportsOf(int deploymentId)
    {
        lock (_reports.All) return _reports.All.Where(r => r.Report.DeploymentId == deploymentId).ToList();
    }

    [Fact]
    public async Task A_docs_only_commit_is_skipped_and_the_running_version_stays()
    {
        var appId = await AddAppAsync(watch: "src/\npackage.json");
        _git.ChangedSince[Running] = ["docs/a.md", "README.md"];

        var (dep, app, log) = await DeployAsync(appId, DeployTrigger.Poll);

        Assert.Equal(DeployStatus.Skipped, dep.Status);
        Assert.Equal("Only files the build filters leave out changed: docs/a.md, README.md", dep.SkipReason);
        Assert.Null(dep.Error);
        Assert.Equal((DocsCommit, "Update the docs", "Ada"), (dep.CommitSha, dep.CommitMessage, dep.CommitAuthor));
        Assert.Null(dep.ImageTag);
        Assert.NotNull(dep.FinishedAt);
        // The running version stays; the commit counts as tried.
        Assert.Equal((Running, Image, DocsCommit), (app.CurrentCommit, app.CurrentImage, app.LastAttemptedCommit));
        Assert.Empty(_docker.Builds);
        // The running commit was fetched into the shallow clone, and the two compared.
        Assert.Equal(1, _git.Count($"fetch --depth 1 --no-tags {CloneUrl} {Running}"));
        Assert.Equal(1, _git.Count($"diff --name-only --no-renames -z {Running} HEAD --"));
        Assert.Contains("Skipped: only files the build filters leave out changed: docs/a.md, README.md.", log);
        Assert.Contains("Nothing was built, and orb keeps running 1111111.", log);
        // GitHub never hears of a build: only that it ended, skipped because nothing it builds from changed.
        var (started, report) = Assert.Single(ReportsOf(dep.Id));
        Assert.False(started);
        Assert.Equal((DeployStatus.Skipped, SkipKind.Filtered, DocsCommit), (report.Status, report.Skip, report.CommitSha));
    }

    [Fact]
    public async Task The_watcher_doesnt_start_a_skipped_commit_again_and_the_next_commit_is_compared_with_the_running_one()
    {
        var appId = await AddAppAsync(ignored: "*.md\ndocs/");
        _git.ChangedSince[Running] = ["docs/a.md"];
        var (first, app, _) = await DeployAsync(appId, DeployTrigger.Poll);
        Assert.Equal(DeployStatus.Skipped, first.Status);

        // No loop: the skipped commit is the one last tried, so the watcher leaves it.
        Assert.Null(await RepoWatcher.NextDeployAsync(_db, app, DocsCommit, DeployTrigger.Poll, NullLogger.Instance, CancellationToken.None));

        // Another docs commit: still compared with the commit that runs, so the changes add up.
        _git.Tip = "4444444444444444444444444444444444444444";
        _git.Message = "More docs";
        _git.ChangedSince[Running] = ["docs/a.md", "docs/b.md"];
        var (second, _, _) = await DeployAsync(appId, DeployTrigger.Webhook);
        Assert.Equal(DeployStatus.Skipped, second.Status);

        // A commit that changes code: the diff from the running commit has every change since, and it's built.
        _git.Tip = CodeCommit;
        _git.Message = "Change the code";
        _git.ChangedSince[Running] = ["docs/a.md", "docs/b.md", "app.py"];
        var (third, after, log) = await DeployAsync(appId, DeployTrigger.Poll);

        Assert.NotEqual(DeployStatus.Skipped, third.Status);
        Assert.Single(_docker.Builds);
        Assert.Contains("The build filters let it through: app.py changed since 1111111, with 2 more files.", log);
        Assert.Equal(3, _git.Count($"fetch --depth 1 --no-tags {CloneUrl} {Running}"));
        Assert.Equal(3, _git.Count($"diff --name-only --no-renames -z {Running} HEAD --"));
        Assert.False(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} {DocsCommit}"));
        Assert.Equal((Running, CodeCommit), (after.CurrentCommit, after.LastAttemptedCommit));
        // A commit that's built tells GitHub it started.
        Assert.Contains(ReportsOf(third.Id), r => r.Started);
    }

    [Fact]
    public async Task A_commit_that_asks_not_to_be_deployed_is_skipped_without_comparing_anything()
    {
        var appId = await AddAppAsync();
        _git.Message = "Tidy up\n\nNothing to ship here [skip ci]";

        var (dep, app, log) = await DeployAsync(appId, DeployTrigger.Webhook);

        Assert.Equal(DeployStatus.Skipped, dep.Status);
        Assert.Equal("The commit message says [skip ci]", dep.SkipReason);
        // The subject is the deploy's message, as for any deploy.
        Assert.Equal("Tidy up", dep.CommitMessage);
        Assert.Equal((Running, DocsCommit), (app.CurrentCommit, app.LastAttemptedCommit));
        // Without build filters only the message is read: nothing more is fetched.
        Assert.False(_git.Ran("diff"));
        Assert.False(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} {Running}"));
        Assert.Empty(_docker.Builds);
        Assert.Contains("Skipped: the commit message says [skip ci].", log);
        Assert.Equal(SkipKind.Asked, Assert.Single(ReportsOf(dep.Id)).Report.Skip);
    }

    [Theory]
    [InlineData(DeployTrigger.Manual)]
    [InlineData(DeployTrigger.Created)]
    [InlineData(DeployTrigger.Start)]
    [InlineData(DeployTrigger.Retry)]
    [InlineData(DeployTrigger.Promote)]
    public async Task A_deploy_a_person_or_gitberth_started_always_builds(DeployTrigger trigger)
    {
        var appId = await AddAppAsync(watch: "src/");
        _git.ChangedSince[Running] = ["docs/a.md"];
        _git.Message = "Docs only [skip ci]";

        var (dep, _, _) = await DeployAsync(appId, trigger);

        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Null(dep.SkipReason);
        Assert.Single(_docker.Builds);
        Assert.False(_git.Ran("diff"));
    }

    [Fact]
    public async Task Without_filters_or_a_marker_nothing_more_is_fetched()
    {
        var appId = await AddAppAsync(watch: "# nothing but a comment\n");

        var (dep, _, _) = await DeployAsync(appId, DeployTrigger.Poll);

        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Single(_docker.Builds);
        Assert.False(_git.Ran("diff"));
        Assert.Equal(0, _git.Count($"fetch --depth 1 --no-tags {CloneUrl} {Running}"));
    }

    [Fact]
    public async Task When_the_filters_cant_tell_the_commit_is_built_and_the_log_says_why()
    {
        // The commit that runs is gone from GitHub (a force-push).
        var gone = await AddAppAsync(watch: "src/");
        _git.Gone.Add(Running);
        var (dep, _, log) = await DeployAsync(gone, DeployTrigger.Poll);
        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains("The build filters can't tell what changed since 1111111, the commit that runs (", log);
        Assert.Contains("Building it.", log);
        _git.Gone.Clear();

        // git can't compare them.
        var broken = await AddAppAsync(watch: "src/", slug: "orb-two");
        _git.DiffFails = true;
        (dep, _, log) = await DeployAsync(broken, DeployTrigger.Poll);
        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains("git couldn't compare the commits", log);
        _git.DiffFails = false;

        // No commit has run yet.
        var first = await AddAppAsync(watch: "src/", current: null, slug: "orb-three");
        (dep, _, log) = await DeployAsync(first, DeployTrigger.Poll);
        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains("The build filters can't tell what changed: no commit has run yet. Building it.", log);
        Assert.Equal(3, _docker.Builds.Count);
    }

    [Fact]
    public async Task Without_an_image_to_keep_running_nothing_is_skipped()
    {
        // Never deployed: a skip would leave nothing running.
        var none = await AddAppAsync(image: null);
        _git.Message = "First commit [skip ci]";
        var (dep, _, log) = await DeployAsync(none, DeployTrigger.Poll);
        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains("The commit message says [skip ci], but nothing runs yet, so it's built.", log);

        // Its image was removed from the host.
        var removed = await AddAppAsync(watch: "src/", slug: "orb-two");
        _docker.Images.Clear();
        _git.Message = "Docs";
        _git.ChangedSince[Running] = ["docs/a.md"];
        (dep, _, log) = await DeployAsync(removed, DeployTrigger.Poll);
        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains($"The build filters can't skip it: the running version's image {Image} is gone, so it's built.", log);
        Assert.False(_git.Ran("diff"));
    }

    [Fact]
    public async Task A_preview_goes_through_its_parents_filters_compared_with_its_own_running_commit()
    {
        const string previewRunning = "5555555555555555555555555555555555555555";
        const string previewImage = "gitberth/orb-pr-12:5555555";
        var parentId = await AddAppAsync(watch: "src/");
        var preview = new AppDefinition
        {
            Name = "orb · PR #12", Slug = "orb-pr-12", Owner = "acme", Repo = "orb", ParentAppId = parentId, AutoDeploy = false,
            CurrentImage = previewImage, CurrentCommit = previewRunning, LastAttemptedCommit = previewRunning,
        };
        preview.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 8201, IsWeb = true });
        var previewId = (await _db.AddAsync(preview)).Id;
        await using (var db = _db.CreateDbContext())
        {
            db.Previews.Add(new PreviewInfo { AppId = previewId, ParentAppId = parentId, Kind = PreviewKind.PullRequest, PullRequest = 12, Branch = "feature", Ref = "refs/pull/12/head" });
            await db.SaveChangesAsync();
        }
        _docker.Images.Add(previewImage);
        _git.ChangedSince[previewRunning] = ["docs/x.md"];

        var (dep, app, _) = await DeployAsync(previewId, DeployTrigger.Poll);

        Assert.Equal(DeployStatus.Skipped, dep.Status);
        Assert.Equal("src/", app.WatchPaths);
        Assert.Equal((previewRunning, previewImage), (app.CurrentCommit, app.CurrentImage));
        Assert.True(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} refs/pull/12/head"));
        Assert.Equal(1, _git.Count($"fetch --depth 1 --no-tags {CloneUrl} {previewRunning}"));
        Assert.False(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} {Running}"));
    }

    [Fact]
    public async Task A_deploy_without_the_build_cache_builds_every_step_again_and_pulls()
    {
        var appId = await AddAppAsync();

        var (dep, _, log) = await DeployAsync(appId, DeployTrigger.Manual, noCache: true);

        var build = Assert.Single(_docker.Builds);
        Assert.Contains("--no-cache", build);
        Assert.Contains("--pull", build);
        Assert.True(dep.NoCache);
        Assert.Contains("Building gitberth/orb:2222222 without the build cache: every step runs again, and the base images are pulled", log);

        // An ordinary deploy keeps the cache.
        await DeployAsync(appId, DeployTrigger.Manual);
        Assert.DoesNotContain("--no-cache", _docker.Builds[1]);
        Assert.DoesNotContain("--pull", _docker.Builds[1]);
    }

    [Fact]
    public async Task A_base_image_only_this_host_has_is_built_again_without_pulling_and_still_without_the_cache()
    {
        var appId = await AddAppAsync();
        _docker.PullError = "ERROR: failed to solve: base:local: failed to resolve source metadata for docker.io/library/base:local: "
            + "pull access denied, repository does not exist or may require authorization";

        var (_, _, log) = await DeployAsync(appId, DeployTrigger.Manual, noCache: true);

        Assert.Equal(2, _docker.Builds.Count);
        Assert.Contains("--pull", _docker.Builds[0]);
        Assert.DoesNotContain("--pull", _docker.Builds[1]);
        Assert.Contains("--no-cache", _docker.Builds[1]);
        Assert.Contains("A base image is only on this host, not in a registry, so it's built again without pulling the base images", log);
    }

    [Fact]
    public async Task Any_other_failed_build_without_the_cache_is_not_tried_again()
    {
        var appId = await AddAppAsync();

        await DeployAsync(appId, DeployTrigger.Manual, noCache: true);

        Assert.Single(_docker.Builds);
    }

    [Theory]
    [InlineData("failed to resolve source metadata for docker.io/library/base:local: pull access denied, repository does not exist", true)]
    [InlineData("failed to resolve source metadata for ghcr.io/acme/base:1: ghcr.io/acme/base:1: not found", true)]
    [InlineData("process \"/bin/sh -c npm ci\" did not complete successfully: exit code: 1", false)]
    [InlineData("pull access denied", false)]
    public void A_failed_pull_of_a_base_image_is_told_from_other_failures(string output, bool expected) =>
        Assert.Equal(expected, DeployPipeline.BasePullFailed(output));

    [Fact]
    public async Task Deploy_without_the_cache_is_saved_with_the_deploy_it_queues()
    {
        using var rig = new AppsRig();
        var id = await rig.Actions.DeployAsync((await rig.RowAsync(a => a.Slug == "orb"))!.Id, noCache: true);
        await using var db = rig.Db.CreateDbContext();
        Assert.True((await db.Deployments.SingleAsync(d => d.Id == id)).NoCache);
    }

    // ---- GitHub ----

    private sealed class GitHubStub : HttpMessageHandler
    {
        public List<(string Call, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls) Calls.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", body));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":1}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task A_skipped_commit_gets_a_success_status_and_a_previews_comment_stays_as_it_was()
    {
        var parentId = await AddAppAsync();
        var preview = await _db.AddAsync(new AppDefinition { Name = "orb · PR #12", Slug = "orb-pr-12", Owner = "acme", Repo = "orb", ParentAppId = parentId, CurrentImage = "img" });
        await using (var db = _db.CreateDbContext())
        {
            db.Previews.Add(new PreviewInfo { AppId = preview.Id, ParentAppId = parentId, Kind = PreviewKind.PullRequest, PullRequest = 12, Branch = "feature", Ref = "refs/pull/12/head", CommentId = 55 });
            await db.SaveChangesAsync();
        }
        var settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        await settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await settings.SetAsync(SettingKeys.UiAddress, "http://10.1.1.80:8080/");
        var github = new GitHubStub();
        var reporter = new GitHubReporter(_db, settings, new GitHubWrites(new Factory(github)), new GitHubIntegrationState(), _options, NullLogger<GitHubReporter>.Instance);

        await reporter.FinishedAsync(new DeployReport(parentId, 7, DeployTrigger.Poll, DocsCommit, DeployStatus.Skipped, null, SkipKind.Filtered), CancellationToken.None);
        await reporter.FinishedAsync(new DeployReport(preview.Id, 8, DeployTrigger.Webhook, CodeCommit, DeployStatus.Skipped, null, SkipKind.Asked), CancellationToken.None);

        Assert.Equal([$"POST /repos/acme/orb/statuses/{DocsCommit}", $"POST /repos/acme/orb/statuses/{CodeCommit}"], github.Calls.Select(c => c.Call));
        var production = JsonDocument.Parse(github.Calls[0].Body).RootElement;
        Assert.Equal("success", production.GetProperty("state").GetString());
        Assert.Equal("Skipped: nothing the app builds from changed", production.GetProperty("description").GetString());
        Assert.Equal("Gitberth: orb", production.GetProperty("context").GetString());
        Assert.Equal($"http://10.1.1.80:8080/apps/{parentId}/deployments/7", production.GetProperty("target_url").GetString());
        var ofPreview = JsonDocument.Parse(github.Calls[1].Body).RootElement;
        Assert.Equal(("success", "Skipped: the commit asked not to be deployed", "Gitberth preview: orb"),
            (ofPreview.GetProperty("state").GetString(), ofPreview.GetProperty("description").GetString(), ofPreview.GetProperty("context").GetString()));
    }
}
