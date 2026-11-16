using System.Net;
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
/// A GitLab app's new commit through the deploy pipeline: cloned from Settings' GitLab, its build filters told what changed
/// by GitLab's compare (no fetch of the running commit), its CI/CD variables for production read and said in the log.
/// git, docker and GitLab are stand-ins; nothing is built.
/// </summary>
public sealed class GitLabPipelineTests : IDisposable
{
    private const string Running = "1111111111111111111111111111111111111111";
    private const string Tip = "2222222222222222222222222222222222222222";
    private const string Image = "gitberth/orb:1111111";
    private const string OrbId = "acme%2Fplatform%2Forb";

    private static ProcessResult Ok(string output) => new(0, output, "");

    private sealed class FakeGit
    {
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken ct)
        {
            lock (Commands) Commands.Add(string.Join(' ', args));
            return Task.FromResult(args[0] switch
            {
                "clone" or "init" => Checkout(args[^1]),
                "log" => Ok($"{Tip}\u001fAda\u001f2026-11-12T10:00:00+00:00\u001fUpdate the docs\u001fUpdate the docs\n"),
                _ => Ok(""),
            });
        }

        private static ProcessResult Checkout(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.22\nEXPOSE 8080\nCMD [\"true\"]\n");
            return Ok("");
        }
    }

    private sealed class FakeDocker
    {
        public List<List<string>> Builds { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            lock (Builds)
            {
                return Task.FromResult(args switch
                {
                    ["image", "inspect", .., var image] => image == Image ? Ok("sha256:1\n") : new ProcessResult(1, "", $"Error: No such image: {image}"),
                    ["build", ..] => Build(args),
                    _ => Ok(""),
                });
            }
        }

        private ProcessResult Build(IReadOnlyList<string> args)
        {
            Builds.Add([.. args]);
            return new ProcessResult(1, "", "the test's docker builds nothing");
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly ImageKit.Db _db = new();
    private readonly GitberthOptions _options = new() { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-gitlab-" + Guid.NewGuid().ToString("N")[..8]) };
    private readonly FakeGit _git = new();
    private readonly FakeDocker _docker = new();
    private readonly GitLabStub _gitlab = new();
    private readonly SettingsService _settings;
    private readonly DeployPipeline _pipeline;

    /// <summary>What GitLab's compare says changed between the running commit and the tip.</summary>
    private string _compare = """{"diffs":[{"old_path":"docs/guide.md","new_path":"docs/guide.md"}],"compare_timeout":false}""";

    public GitLabPipelineTests()
    {
        Directory.CreateDirectory(_options.LogsDir);
        Directory.CreateDirectory(_options.WorkDir);
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        _settings = new SettingsService(_db, protector);
        var cli = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new ImageKit.NoHttp();
        var gitLab = new GitLabApi(new GitLabStub.Factory(_gitlab), _settings);
        _gitlab.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/repository/compare", StringComparison.Ordinal) => GitLabStub.Json(HttpStatusCode.OK, _compare),
            var p when p.EndsWith("/variables", StringComparison.Ordinal) => GitLabStub.Json(HttpStatusCode.OK,
                """[{"key":"SITE_NAME","value":"Orb","environment_scope":"*"},{"key":"SITE_NAME","value":"Orb preview","environment_scope":"preview/*"},{"key":"PAYMENTS_KEY","value":"pk-live-1234567890","environment_scope":"production","masked":true}]"""),
            _ => null,
        };
        var domains = new DomainService(_db, _settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _pipeline = new DeployPipeline(_db, new GitClient(_options, _settings) { Runner = _git.RunAsync }, cli, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(),
            protector, _options, http, new RepoVariables(new GitHubApi(http), _settings, gitLab: gitLab), domains, new WatchState(), new Lifetime(),
            NullLogger<DeployPipeline>.Instance, gitLab: gitLab);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_options.DataDir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<int> AddAppAsync(string? watch = "src/**")
    {
        var app = new AppDefinition
        {
            Name = "orb", Slug = "orb", Owner = "gitlab:acme/platform", Repo = "orb", CurrentImage = Image, CurrentCommit = Running, LastAttemptedCommit = Running,
            WatchPaths = watch,
        };
        app.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 3000, IsWeb = true });
        return (await _db.AddAsync(app)).Id;
    }

    private async Task<(Deployment Deployment, string Log)> DeployAsync(int appId)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = appId, Trigger = DeployTrigger.Webhook };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(appId, id, Build: true), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return (await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id), await File.ReadAllTextAsync(DeployPaths.LogFile(_options, id)));
    }

    [Fact]
    public async Task A_push_that_changed_only_what_the_filters_leave_out_is_skipped_on_gitlabs_compare()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
        var appId = await AddAppAsync();

        var (dep, log) = await DeployAsync(appId);

        Assert.Equal(DeployStatus.Skipped, dep.Status);
        Assert.Equal("Only files the build filters leave out changed: docs/guide.md", dep.SkipReason);
        // Cloned from GitLab, compared there: the running commit was never fetched.
        Assert.Contains(_git.Commands, c => c == $"clone --depth 1 --single-branch --no-tags --branch main https://gitlab.com/acme/platform/orb.git {Path.Combine(_options.WorkDir, $"dep-{dep.Id}", "src")}");
        Assert.DoesNotContain(_git.Commands, c => c.StartsWith("fetch", StringComparison.Ordinal) || c.StartsWith("diff", StringComparison.Ordinal));
        Assert.Contains($"GET /api/v4/projects/{OrbId}/repository/compare?from={Running}&to={Tip}&straight=true", _gitlab.Calls);
        Assert.Empty(_docker.Builds);
        // Production's variables: * and production, the masked one secret in the log.
        Assert.Contains("Using 2 variables from the project's GitLab CI/CD variables (scopes * and production)", log);
        Assert.DoesNotContain("pk-live-1234567890", log);
    }

    [Fact]
    public async Task A_push_that_changed_a_watched_file_is_built()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
        _compare = """{"diffs":[{"old_path":"docs/guide.md","new_path":"docs/guide.md"},{"old_path":"src/old.cs","new_path":"src/new.cs"}],"compare_timeout":false}""";
        var appId = await AddAppAsync();

        var (dep, log) = await DeployAsync(appId);

        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains("The build filters let it through: src/old.cs changed since 1111111, with 2 more files.", log);
        Assert.Single(_docker.Builds);
    }

    [Theory]
    [InlineData("""{"diffs":[],"compare_timeout":true}""", "GitLab's compare timed out on that many changes. Building it.")]
    [InlineData("not json", "GitLab's compare of acme/platform/orb's commits couldn't be read). Building it.")]
    public async Task When_gitlab_cant_say_what_changed_the_commit_is_built(string compare, string said)
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
        _compare = compare;
        var appId = await AddAppAsync();

        var (dep, log) = await DeployAsync(appId);

        Assert.NotEqual(DeployStatus.Skipped, dep.Status);
        Assert.Contains(said, log);
        Assert.Single(_docker.Builds);
    }

    [Fact]
    public async Task Without_a_token_a_public_projects_variables_arent_read_and_the_log_says_where_to_add_one()
    {
        var appId = await AddAppAsync(watch: null);

        var (_, log) = await DeployAsync(appId);

        Assert.Contains("warning: the project's GitLab CI/CD variables weren't used. No GitLab token is saved, so the project's CI/CD variables can't be read. Add one in Settings › GitLab.", log);
        Assert.Contains(_git.Commands, c => c.StartsWith("clone", StringComparison.Ordinal) && c.Contains("https://gitlab.com/acme/platform/orb.git", StringComparison.Ordinal));
        Assert.DoesNotContain(_gitlab.Calls, c => c.Contains("/variables", StringComparison.Ordinal));
    }
}
