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
/// One commit of production's own branch, asked for by name (deploy_app's commit), through the deploy pipeline with git and
/// docker stand-ins. A commit on the branch is fetched by its SHA and built. One that isn't (a fork's, which GitHub serves
/// by its SHA through the repo too) fails at the fetch with "That commit isn't on main.", and nothing of it is fetched or
/// built. One production ran before is built again without reading the branch. None of them changes what the watcher
/// knows about the branch, so its tip isn't deployed straight over them.
/// </summary>
public sealed class DeployCommitPipelineTests : IDisposable
{
    private const string Earlier = "1111111111111111111111111111111111111111";
    private const string Tip = "2222222222222222222222222222222222222222";
    private const string Fork = "9999999999999999999999999999999999999999";
    private const string CloneUrl = "https://github.com/acme/orb.git";

    private static ProcessResult Ok(string output) => new(0, output, "");

    /// <summary>
    /// git as the deploy uses it: the branch's history (a bare clone without files), commits fetched by SHA and checked
    /// out, and the checked-out commit's log. GitHub serves the fork's commit by its SHA, as it does.
    /// </summary>
    private sealed class FakeGit
    {
        /// <summary>main's commits, oldest first.</summary>
        public List<string> Branch { get; } = [Earlier, Tip];
        public HashSet<string> Served { get; } = [Earlier, Tip, Fork];
        public List<string> Commands { get; } = [];
        /// <summary>GIT_NO_LAZY_FETCH as each command that read the branch's history had it.</summary>
        public List<string?> NoLazyFetch { get; } = [];
        private string _checkedOut = Tip;

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken ct)
        {
            lock (Commands)
            {
                Commands.Add(string.Join(' ', args));
                if (args[0] is "clone" && args.Contains("--bare") || args[0] is "rev-parse" or "merge-base")
                    NoLazyFetch.Add(options.Environment?.GetValueOrDefault("GIT_NO_LAZY_FETCH"));
                return Task.FromResult(args switch
                {
                    ["clone", "--bare", .., var dir] => Made(dir),
                    ["clone", .., var dir] => Checkout(dir, Tip),
                    ["init", "--quiet", var dir] => Made(dir),
                    // Only what the branch's history has: a commit that isn't in it is never fetched to be looked at.
                    ["rev-parse", "--verify", "--quiet", var spec] => Resolve(spec[..^"^{commit}".Length]),
                    ["merge-base", "--is-ancestor", var sha, "HEAD"] => new ProcessResult(Branch.Contains(sha) ? 0 : 1, "", ""),
                    ["fetch", .., var what] when what.StartsWith("refs/", StringComparison.Ordinal) => Ok(""),
                    ["fetch", .., var sha] => Served.Contains(sha) ? Ok("") : new ProcessResult(128, "", $"fatal: remote error: upload-pack: not our ref {sha}"),
                    ["checkout", "--quiet", "--detach", var sha] => Checkout(options.WorkingDirectory!, sha),
                    ["log", ..] => Ok($"{_checkedOut}\u001fAda\u001f2026-11-12T10:00:00+00:00\u001fCommit {_checkedOut[..7]}\u001fCommit {_checkedOut[..7]}\n"),
                    _ => Ok(""),
                });
            }
        }

        public bool Ran(string start)
        {
            lock (Commands) return Commands.Any(c => c.StartsWith(start, StringComparison.Ordinal));
        }

        private ProcessResult Resolve(string prefix)
        {
            var found = Branch.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return found.Count == 1 ? Ok(found[0] + "\n") : new ProcessResult(1, "", "");
        }

        private static ProcessResult Made(string dir)
        {
            Directory.CreateDirectory(dir);
            return Ok("");
        }

        private ProcessResult Checkout(string dir, string sha)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.22\nEXPOSE 8080\nCMD [\"true\"]\n");
            _checkedOut = sha;
            return Ok("");
        }
    }

    /// <summary>The docker CLI: every build fails (nothing here builds), and is counted.</summary>
    private sealed class FakeDocker
    {
        public List<List<string>> Builds { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            lock (Builds)
            {
                if (args is ["build", ..])
                {
                    Builds.Add([.. args]);
                    return Task.FromResult(new ProcessResult(1, "", "the test's docker builds nothing"));
                }
                return Task.FromResult(args is ["image", "inspect", ..] ? new ProcessResult(1, "", "Error: No such image") : Ok(""));
            }
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
    private readonly GitberthOptions _options = new() { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-commit-" + Guid.NewGuid().ToString("N")[..8]) };
    private readonly FakeGit _git = new();
    private readonly FakeDocker _docker = new();
    private readonly DeployPipeline _pipeline;

    public DeployCommitPipelineTests()
    {
        Directory.CreateDirectory(_options.LogsDir);
        Directory.CreateDirectory(_options.WorkDir);
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(_db, protector);
        var cli = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new NoHttp();
        var domains = new DomainService(_db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _pipeline = new DeployPipeline(_db, new GitClient(_options, settings) { Runner = _git.RunAsync }, cli, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(),
            protector, _options, http, new RepoVariables(new GitHubApi(http), settings), domains, new WatchState(), new Lifetime(), NullLogger<DeployPipeline>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_options.DataDir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<AppDefinition> AddAppAsync()
    {
        var app = new AppDefinition
        {
            Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", Branch = "main", CurrentImage = "gitberth/orb:2222222", CurrentCommit = Tip, LastAttemptedCommit = Tip,
        };
        app.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 3000, IsWeb = true });
        return await _db.AddAsync(app);
    }

    private async Task AddDeploymentAsync(int appId, DeployStatus status, string commit)
    {
        await using var db = _db.CreateDbContext();
        db.Deployments.Add(new Deployment { AppId = appId, Trigger = DeployTrigger.Promote, Status = status, CommitSha = commit, FinishedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    // As deploy_app queues it: the app's own branch and the commit asked for.
    private async Task<(Deployment Deployment, AppDefinition App, string Log)> DeployAsync(AppDefinition app, string commit)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Manual };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(app.Id, id, Build: true, GitRef: DeployPipeline.OwnBranchRef(app), Commit: commit), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return (await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id), await after.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id),
            await File.ReadAllTextAsync(DeployPaths.LogFile(_options, id)));
    }

    [Fact]
    public async Task A_commit_on_the_branch_is_fetched_by_its_sha_and_built()
    {
        var app = await AddAppAsync();

        var (dep, after, log) = await DeployAsync(app, "1111111");

        // The branch's history, read without its files and without fetching anything it doesn't have.
        Assert.True(_git.Ran($"clone --bare --quiet --filter=tree:0 --single-branch --no-tags --branch main {CloneUrl} "), string.Join("\n", _git.Commands));
        Assert.True(_git.Ran("rev-parse --verify --quiet 1111111^{commit}"));
        Assert.True(_git.Ran($"merge-base --is-ancestor {Earlier} HEAD"));
        Assert.All(_git.NoLazyFetch, v => Assert.Equal("1", v));
        Assert.Equal(3, _git.NoLazyFetch.Count);
        // Then the whole SHA is fetched and checked out, and built.
        Assert.True(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} {Earlier}"));
        Assert.True(_git.Ran($"checkout --quiet --detach {Earlier}"));
        Assert.Equal(Earlier, dep.CommitSha);
        Assert.Single(_docker.Builds);
        Assert.Contains("Fetching 1111111 of acme/orb (main)", log);
        Assert.Contains("Checking that 1111111 is on main", log);
        // What the watcher knows about the branch stays: once the commit asked for runs, the tip (tried already) isn't
        // deployed straight over it. A new commit on the branch is.
        Assert.Equal((Tip, Tip), (after.LastAttemptedCommit, after.CurrentCommit));
        after.CurrentCommit = Earlier;
        Assert.False(RepoWatcher.ShouldDeploy(after, Tip, deployInProgress: false));
        Assert.True(RepoWatcher.ShouldDeploy(after, "3333333333333333333333333333333333333333", deployInProgress: false));
    }

    [Fact]
    public async Task A_commit_that_isnt_on_the_branch_fails_at_the_fetch_and_nothing_of_it_is_fetched_or_built()
    {
        var app = await AddAppAsync();

        foreach (var commit in new[] { Fork, Fork[..12] })
        {
            var (dep, after, log) = await DeployAsync(app, commit);

            Assert.Equal(DeployStatus.Failed, dep.Status);
            Assert.Equal("That commit isn't on main.", dep.Error);
            Assert.Null(dep.CommitSha);
            Assert.Contains("Failed: That commit isn't on main.", log);
            Assert.Equal((Tip, Tip), (after.LastAttemptedCommit, after.CurrentCommit));
        }
        Assert.False(_git.Ran($"fetch --depth 1 --no-tags {CloneUrl} {Fork}"));
        Assert.False(_git.Ran("init"));
        Assert.Empty(_docker.Builds);
    }

    [Fact]
    public async Task A_commit_production_ran_before_is_built_again_without_reading_the_branch()
    {
        var app = await AddAppAsync();
        // A promoted preview's commit that ran in production, and one whose deploy failed: only the first counts.
        await AddDeploymentAsync(app.Id, DeployStatus.Succeeded, Fork);
        await AddDeploymentAsync(app.Id, DeployStatus.Failed, "8888888888888888888888888888888888888888");

        var (dep, _, log) = await DeployAsync(app, "9999999");

        Assert.Equal(Fork, dep.CommitSha);
        Assert.Contains("9999999 ran in production before, so it can be built again.", log);
        Assert.False(_git.Ran("clone --bare"));
        Assert.True(_git.Ran($"checkout --quiet --detach {Fork}"));
        Assert.Single(_docker.Builds);

        var (failed, _, _) = await DeployAsync(app, "8888888");
        Assert.Equal("That commit isn't on main.", failed.Error);
        Assert.Single(_docker.Builds);
    }

    [Fact]
    public async Task Without_a_commit_the_branchs_tip_is_cloned_as_ever()
    {
        var app = await AddAppAsync();
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Manual };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }

        await _pipeline.RunAsync(new DeployRequest(app.Id, id, Build: true), CancellationToken.None);

        Assert.True(_git.Ran($"clone --depth 1 --single-branch --no-tags --branch main {CloneUrl} "));
        Assert.False(_git.Ran("clone --bare"));
        await using var after = _db.CreateDbContext();
        Assert.Equal(Tip, (await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id)).CommitSha);
    }
}
