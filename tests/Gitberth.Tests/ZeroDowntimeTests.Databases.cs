using System.Text.Json;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>A deploy sets what the app's databases give it (Settings › Databases), over its own values and GitHub's.</summary>
public sealed partial class ZeroDowntimeTests
{
    [Fact]
    public async Task A_database_gitberth_set_wins_over_the_environment_tabs_value_and_githubs_and_never_shows_in_the_log()
    {
        const string password = "Pw3xampleDatabasePasswordAbCdEfGh";
        await using (var db = _db.CreateDbContext())
        {
            // The app's own DATABASE_URL on its Environment tab, and its database's.
            db.EnvVars.Add(new EnvVar { AppId = _appId, Key = "DATABASE_URL", ProtectedValue = _protector.Protect("postgres://old:oldpass@elsewhere:5432/old") });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                AppId = _appId, Kind = DatabaseKind.Postgres, Server = "postgres", Name = "web", Role = "web", ProtectedPassword = _protector.Protect(password),
                Variable = "DATABASE_URL",
            });
            await db.SaveChangesAsync();
        }
        // The repo's GitHub variables as last read: DATABASE_URL too, and one of their own.
        await _settings.SetSecretAsync(RepoVariables.SavedKey("acme", "web"),
            JsonSerializer.Serialize(new[] { new RepoVariable("DATABASE_URL", "postgres://gh:ghpass@github-db:5432/gh"), new RepoVariable("FROM_GITHUB", "yes") }));
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var servers = new DatabaseServers(docker, _settings, _options, NullLogger<DatabaseServers>.Instance);
        var databases = new DatabaseService(_db, docker, _protector, servers, _options, NullLogger<DatabaseService>.Instance);
        var pipeline = new DeployPipeline(_db, new GitClient(_options, _settings), docker, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(), _protector, _options,
            new Factory(_cloudflare), new RepoVariables(new GitHubApi(new Factory(_cloudflare)), _settings), _domains, new WatchState(), new Lifetime(),
            NullLogger<DeployPipeline>.Instance, databases: databases)
        {
            StableFor = TimeSpan.FromMilliseconds(40),
            StatePoll = TimeSpan.FromMilliseconds(5),
            // A wait on the fake cloudflared's own confirmation, which comes: long, as in ZeroDowntimeTests.
            TunnelWait = Eventually.Limit,
            ListenWait = TimeSpan.Zero,
        };
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = _appId, Trigger = DeployTrigger.ConfigChange, ImageTag = "gitberth/web:2", CommitSha = NewCommit };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }

        await pipeline.RunAsync(new DeployRequest(_appId, id, Build: false, Image: "gitberth/web:2"), CancellationToken.None);

        await using (var db = _db.CreateDbContext())
            Assert.Equal(DeployStatus.Succeeded, db.Deployments.Single(d => d.Id == id).Status);
        var container = _docker.Containers.Single(c => c.Name == "gb-web");
        Assert.Contains($"DATABASE_URL=postgres://web:{password}@postgres:5432/web", container.Env);
        Assert.DoesNotContain(container.Env, e => e.Contains("oldpass", StringComparison.Ordinal) || e.Contains("ghpass", StringComparison.Ordinal));
        Assert.Contains("FROM_GITHUB=yes", container.Env);
        // Through the CLI's environment, never its command line.
        Assert.DoesNotContain(_docker.Runs.SelectMany(r => r), a => a.Contains(password, StringComparison.Ordinal));
        var log = File.ReadAllText(DeployPaths.LogFile(_options, id));
        Assert.Contains("Set by Gitberth from its databases: DATABASE_URL (the Postgres database web on postgres)", log);
        Assert.Contains("In place of the value set on the Environment tab: DATABASE_URL.", log);
        Assert.Contains("Set in Gitberth for this app, so the GitHub value isn't used: DATABASE_URL", log);
        Assert.DoesNotContain(password, log, StringComparison.Ordinal);
        // The database a deploy set is the one there is: nothing is left to make live.
        await using (var db = _db.CreateDbContext())
            Assert.False(db.Apps.Single(a => a.Id == _appId).ConfigChangePending);
    }
}
