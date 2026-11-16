using System.Collections.Concurrent;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Who made a change: the ambient actor a page, an endpoint or a job sets, a deploy that remembers who asked for it and
/// is told against them whoever's code runs it, sign-ins, and no secret value ever written down.
/// </summary>
public sealed class AuditActorTests
{
    private sealed class Running : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static readonly AuditActor Admin = AuditActor.User("admin", "10.1.1.5");

    [Fact]
    public void The_ambient_actor_is_Gitberth_until_a_scope_says_otherwise_and_comes_back_after()
    {
        Assert.Equal(AuditActor.Gitberth, AuditScope.Current);
        Assert.False(AuditScope.IsSet);
        using (AuditScope.Begin(Admin))
        {
            Assert.Equal(Admin, AuditScope.Current);
            using (AuditScope.Begin(AuditActor.System(AuditVia.Watcher)))
                Assert.Equal(AuditVia.Watcher, AuditScope.Current.Via);
            Assert.Equal(Admin, AuditScope.Current);
        }
        Assert.Equal(AuditActor.Gitberth, AuditScope.Current);
    }

    [Fact]
    public async Task What_a_page_starts_carries_its_person_along_but_suppressed_work_does_not()
    {
        var log = new AuditLog();
        Task followUp, worker;
        using (AuditScope.Begin(Admin))
        {
            // A change engine job, a create, an update: started by the person, carried on after the click returns.
            followUp = Task.Run(async () =>
            {
                await Task.Delay(20);
                log.Record(new AuditEntry("app.settings.changed", "Changed web's settings: memory limit."));
            });
            // A long-lived worker (the deploy queue's) is started without the caller's context.
            using (ExecutionContext.SuppressFlow())
                worker = Task.Run(() => log.Record(new AuditEntry("app.deploy.succeeded", "Deploy #1 of web succeeded.")));
        }
        await Task.WhenAll(followUp, worker);
        var entries = log.Entries.ToDictionary(e => e.Action, e => e.Actor);
        Assert.Equal(Admin, entries["app.settings.changed"]);
        Assert.Equal(AuditActor.Gitberth, entries["app.deploy.succeeded"]);
    }

    [Fact]
    public void Who_asked_for_a_deploy_is_kept_as_a_line_and_read_back()
    {
        Assert.Equal("User|UI|10.1.1.5|admin", AuditActors.Format(Admin));
        Assert.Equal(Admin, AuditActors.Parse(AuditActors.Format(Admin)));
        var hook = new AuditActor(AuditActorKind.DeployHook, "cms | prod", AuditVia.Hook, "203.0.113.7");
        Assert.Equal(hook, AuditActors.Parse(AuditActors.Format(hook)));
        var github = new AuditActor(AuditActorKind.GitHub, "GitHub", AuditVia.Webhook);
        Assert.Equal(github, AuditActors.Parse(AuditActors.Format(github)));
        // Gitberth's own deploys keep nothing: their trigger says which job started them.
        Assert.Null(AuditActors.Format(AuditActor.System(AuditVia.Watcher)));
        Assert.Equal(AuditActor.System(AuditVia.Watcher), AuditActors.ForDeploy(null, DeployTrigger.Poll));
        Assert.Equal(AuditActor.System(AuditVia.Schedule), AuditActors.ForDeploy(null, DeployTrigger.Restart));
        Assert.Equal(AuditActor.System(AuditVia.Startup), AuditActors.ForDeploy(null, DeployTrigger.Start));
        Assert.Equal(Admin, AuditActors.ForDeploy("User|UI|10.1.1.5|admin", DeployTrigger.Poll));
        Assert.Null(AuditActors.Parse("Nobody|UI||x"));
        Assert.Null(AuditActors.Parse("User|UI|"));
        Assert.Null(AuditActors.Parse(""));
    }

    [Fact]
    public void A_deploy_page_names_who_asked_only_when_that_says_more_than_its_trigger()
    {
        Assert.Equal("by admin", Format.RequestedBy(new Deployment { Trigger = DeployTrigger.Manual, RequestedBy = "User|UI|10.1.1.5|admin" }));
        Assert.Equal("by the token ci", Format.RequestedBy(new Deployment { Trigger = DeployTrigger.Manual, RequestedBy = "ApiToken|Api||ci" }));
        Assert.Null(Format.RequestedBy(new Deployment { Trigger = DeployTrigger.Hook, HookName = "cms", RequestedBy = "DeployHook|Hook|203.0.113.7|cms" }));
        Assert.Null(Format.RequestedBy(new Deployment { Trigger = DeployTrigger.Webhook, RequestedBy = "GitHub|Webhook||GitHub" }));
        Assert.Null(Format.RequestedBy(new Deployment { Trigger = DeployTrigger.Poll }));
    }

    [Fact]
    public async Task The_deploy_queue_runs_each_deploy_as_the_one_who_asked_never_as_whoever_started_its_worker()
    {
        using var rig = new AppsRig();
        var seen = new ConcurrentDictionary<int, AuditActor>();
        var queue = new DeployQueue(rig.Pipeline, new LiveLogHub(), rig.Db, new Running(), NullLogger<DeployQueue>.Instance)
        {
            Runner = (request, _) =>
            {
                seen[request.DeploymentId] = AuditScope.Current;
                return Task.CompletedTask;
            },
        };
        int hooked, polled;
        await using (var db = rig.Db.CreateDbContext())
        {
            var a = new Deployment { AppId = rig.ObsId, Trigger = DeployTrigger.Hook, HookName = "cms", RequestedBy = "DeployHook|Hook|203.0.113.7|cms" };
            var b = new Deployment { AppId = rig.ObsId, Trigger = DeployTrigger.Poll };
            db.Deployments.AddRange(a, b);
            await db.SaveChangesAsync();
            (hooked, polled) = (a.Id, b.Id);
        }

        // The person's page is the first to queue: the worker starts inside their scope.
        using (AuditScope.Begin(Admin))
            queue.Enqueue(new DeployRequest(rig.ObsId, hooked, Build: true));
        await WaitAsync(() => seen.ContainsKey(hooked));
        queue.Enqueue(new DeployRequest(rig.ObsId, polled, Build: true));
        await WaitAsync(() => seen.ContainsKey(polled));

        Assert.Equal(new AuditActor(AuditActorKind.DeployHook, "cms", AuditVia.Hook, "203.0.113.7"), seen[hooked]);
        Assert.Equal(AuditActor.System(AuditVia.Watcher), seen[polled]);
    }

    [Fact]
    public async Task A_deploy_queued_from_a_page_keeps_who_asked_and_says_so_in_the_log()
    {
        using var rig = new AppsRig();
        var log = new AuditLog();
        var queue = new DeployQueue(rig.Pipeline, new LiveLogHub(), rig.Db, new Running(), NullLogger<DeployQueue>.Instance) { Runner = (_, _) => Task.CompletedTask };
        var actions = new AppActions(rig.Db, queue, rig.Cli, rig.AppLocks, rig.Options, new RepoVariables(new GitHubApi(new NoHttp()), rig.Settings), rig.Domains,
            rig.Restarts, rig.Apps, NullLogger<AppActions>.Instance, rig.Catalog, audit: log);

        int deployment;
        using (AuditScope.Begin(Admin))
            deployment = await actions.DeployAsync(rig.ObsId, DeployTrigger.Manual, noCache: true);

        await using (var db = rig.Db.CreateDbContext())
            Assert.Equal("User|UI|10.1.1.5|admin", (await db.Deployments.SingleAsync(d => d.Id == deployment)).RequestedBy);
        var queued = Assert.Single(log.Entries, e => e.Action == "app.deploy.queued");
        Assert.Equal(Admin, queued.Actor);
        Assert.Equal((rig.ObsId, "orb", "gb-orb"), (queued.AppId!.Value, queued.AppName!, queued.Container!));
        Assert.Equal(AuditCorrelation.ForDeployment(deployment), queued.Correlation);
        Assert.Equal(deployment, queued.DeploymentId);
        Assert.Equal("Queued a deploy of orb without the build cache.", queued.Summary);
        Assert.Contains(queued.Changes, c => c.Field == "Build cache" && c.To == "not used");
    }

    [Fact]
    public async Task A_deploy_that_starts_is_recorded_as_the_one_who_asked_and_so_is_how_it_ends()
    {
        using var rig = new AppsRig();
        var log = new AuditLog();
        var hub = new LiveLogHub();
        var pipeline = new DeployPipeline(rig.Db, new Gitberth.Git.GitClient(rig.Options, rig.Settings), rig.Cli, new Gitberth.Detection.ProjectAnalyzer(), rig.AppLocks, hub,
            rig.Protector, rig.Options, new NoHttp(), new RepoVariables(new GitHubApi(new NoHttp()), rig.Settings), rig.Domains, new WatchState(), new Running(),
            NullLogger<DeployPipeline>.Instance, audit: log);
        var queue = new DeployQueue(pipeline, hub, rig.Db, new Running(), NullLogger<DeployQueue>.Instance, log);
        // A preview whose pull request is gone: it starts, and fails before anything is fetched or built.
        int deployment, preview;
        await using (var db = rig.Db.CreateDbContext())
        {
            var app = new AppDefinition { Name = "orb · PR #4", Slug = "orb-pr-4", Owner = "acme", Repo = "orb", ParentAppId = rig.ObsId };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Created, CommitSha = "4f1e2d3c4b5a69788796", RequestedBy = "User|UI|10.1.1.5|admin" };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            (deployment, preview) = (dep.Id, app.Id);
        }

        queue.Enqueue(new DeployRequest(preview, deployment, Build: true));
        await WaitAsync(() => log.Entries.Any(e => e.Action == "app.deploy.failed"));

        var entries = log.Entries.Where(e => e.DeploymentId == deployment).ToList();
        Assert.Equal(["app.deploy.started", "app.deploy.failed"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(Admin, e.Actor));
        Assert.Equal($"Started deploy #{deployment} of orb · PR #4 (first deploy, 4f1e2d3).", entries[0].Summary);
        Assert.Equal((preview, "gb-orb-pr-4", $"deploy #{deployment}"), (entries[0].AppId!.Value, entries[0].Container!, entries[0].Target!));
        Assert.Contains(entries[0].Changes, c => c.Field == "Commit" && c.To == "4f1e2d3");
    }

    [Fact]
    public async Task Queued_deploys_a_stop_or_a_delete_cancels_are_each_recorded_as_cancelled()
    {
        using var rig = new AppsRig();
        var log = new AuditLog();
        var queue = new DeployQueue(rig.Pipeline, new LiveLogHub(), rig.Db, new Running(), NullLogger<DeployQueue>.Instance) { Runner = (_, _) => Task.CompletedTask };
        var actions = new AppActions(rig.Db, queue, rig.Cli, rig.AppLocks, rig.Options, new RepoVariables(new GitHubApi(new NoHttp()), rig.Settings), rig.Domains,
            rig.Restarts, rig.Apps, NullLogger<AppActions>.Instance, rig.Catalog, audit: log);
        async Task<(int Hooked, int Polled)> QueueTwoAsync(int appId)
        {
            await using var db = rig.Db.CreateDbContext();
            var hooked = new Deployment { AppId = appId, Trigger = DeployTrigger.Hook, HookName = "cms", RequestedBy = "DeployHook|Hook|203.0.113.7|cms" };
            var polled = new Deployment { AppId = appId, Trigger = DeployTrigger.Poll, CommitSha = "9a8b7c6d5e4f" };
            db.Deployments.AddRange(hooked, polled);
            await db.SaveChangesAsync();
            return (hooked.Id, polled.Id);
        }

        // Stopped by admin: each queued deploy ends, as the one who asked for it, and the stop is admin's.
        var (hooked, polled) = await QueueTwoAsync(rig.ObsId);
        using (AuditScope.Begin(Admin))
            await actions.StopAsync(rig.ObsId);
        var cancelled = log.Entries.Where(e => e.Action == "app.deploy.cancelled").ToDictionary(e => e.DeploymentId!.Value);
        Assert.Equal([hooked, polled], cancelled.Keys.Order());
        Assert.Equal(new AuditActor(AuditActorKind.DeployHook, "cms", AuditVia.Hook, "203.0.113.7"), cancelled[hooked].Actor);
        Assert.Equal(AuditActor.System(AuditVia.Watcher), cancelled[polled].Actor);
        Assert.Equal($"Deploy #{polled} of orb was cancelled before it started: the app was stopped.", cancelled[polled].Summary);
        Assert.All(cancelled.Values, e => Assert.Equal((AuditOutcome.Failed, "gb-orb"), (e.Outcome, e.Container!)));
        Assert.Equal(Admin, Assert.Single(log.Entries, e => e.Action == "app.stopped").Actor);
        await using (var db = rig.Db.CreateDbContext())
            Assert.All(await db.Deployments.Where(d => d.Id == hooked || d.Id == polled).ToListAsync(), d => Assert.Equal(DeployStatus.Cancelled, d.Status));

        // Deleted: the same, with why.
        var (_, again) = await QueueTwoAsync(rig.KanbanId);
        using (AuditScope.Begin(Admin))
            await actions.DeleteAsync(rig.KanbanId, removeImages: false, removeVolumes: false);
        var deleted = Assert.Single(log.Entries, e => e.Action == "app.deploy.cancelled" && e.DeploymentId == again);
        Assert.Equal($"Deploy #{again} of Kanban was cancelled before it started: the app is being deleted.", deleted.Summary);
        Assert.Equal(2, log.Entries.Count(e => e.Action == "app.deploy.cancelled" && e.AppId == rig.KanbanId));
    }

    // ---- The repo's variables in GitHub ----

    private sealed class GitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    [Fact]
    public async Task Variables_saved_to_or_deleted_from_the_repos_GitHub_variables_are_recorded_with_values_only_when_shown()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        var settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        await settings.SetSecretAsync(SettingKeys.GitHubToken, "github_pat_test");
        var github = new GitHub(r => r.RequestUri!.AbsolutePath.EndsWith("/NOPE", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Resource not accessible by personal access token"}""") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        var log = new AuditLog();
        var variables = new RepoVariables(new GitHubApi(github), settings, log);
        var orb = new AppDefinition { Id = 3, Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb" };

        using (AuditScope.Begin(Admin))
        {
            await variables.SetAsync("acme", "orb", "SITE_NAME", "Orb shop", app: orb);
            await variables.SetAsync("acme", "orb", "SITE_NAME", "Orb", app: orb, previous: "Orb shop");
            await variables.SetAsync("acme", "orb", "AUTH_SECRET", "s3cr3t-auth-value", app: orb);
            await variables.SetAsync("acme", "orb", "DATABASE_URL", "postgres://orb:pa55w0rd-in-url@postgres/orb", app: orb);
            await variables.DeleteAsync("acme", "orb", "SITE_NAME", app: orb, previous: "Orb");
            await Assert.ThrowsAsync<GitHubApiException>(() => variables.SetAsync("acme", "orb", "NOPE", "x", app: orb));
            await Assert.ThrowsAsync<GitHubApiException>(() => variables.SetAsync("acme", "orb", "2BAD", "x", app: orb));
        }

        var entries = log.Entries;
        Assert.Equal(["app.env.github.set", "app.env.github.set", "app.env.github.set", "app.env.github.set", "app.env.github.removed", "app.env.github.set", "app.env.github.set"],
            entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal((Admin, 3, "gb-orb"), (e.Actor, e.AppId!.Value, e.Container!)));
        Assert.Equal("Saved the variable SITE_NAME to acme/orb's variables in GitHub: every app from the repo gets it at its next deploy.", entries[0].Summary);
        Assert.Equal([("Repo", null, "acme/orb"), ("Value", null, "Orb shop"), ("Secret", null, "off")], entries[0].Changes.Select(c => (c.Field, c.From, c.To)));
        Assert.Equal(("Orb shop", "Orb"), (entries[1].Changes[1].From, entries[1].Changes[1].To));
        Assert.Equal(("AUTH_SECRET", "Saved the secret variable AUTH_SECRET to acme/orb's variables in GitHub: every app from the repo gets it at its next deploy."),
            (entries[2].Target, entries[2].Summary));
        Assert.Equal([("Value", null, null), ("Secret", null, "on")], entries[2].Changes.Skip(1).Select(c => (c.Field, c.From, c.To)));
        Assert.Equal((null, null), (entries[3].Changes[1].From, entries[3].Changes[1].To));
        Assert.Equal(("Deleted the variable SITE_NAME from acme/orb's variables in GitHub: no app from the repo gets it from there now.", "Orb"),
            (entries[4].Summary, entries[4].Changes[1].From));
        // A refusal is recorded with GitHub's words, and a name GitHub wouldn't take with why.
        Assert.Equal(AuditOutcome.Failed, entries[5].Outcome);
        Assert.StartsWith("Couldn't save the variable NOPE to acme/orb's variables in GitHub: ", entries[5].Summary);
        Assert.Contains("acme/orb", entries[5].Summary);
        Assert.Equal((AuditOutcome.Failed, "Couldn't save the variable 2BAD to acme/orb's variables in GitHub: Use letters, digits and underscores, not starting with a digit."),
            (entries[6].Outcome, entries[6].Summary));
        var everything = string.Join('\n', entries.Select(AuditExport.JsonLine));
        Assert.DoesNotContain("s3cr3t-auth-value", everything);
        Assert.DoesNotContain("pa55w0rd-in-url", everything);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP here.");
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done(), "It didn't happen in time.");
    }

    // ---- Sign-ins ----

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Sign_ins_good_failed_and_refused_are_recorded_with_where_they_came_from()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        var log = new AuditLog();
        var auth = new AdminAuthService(new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider())),
            new GitberthOptions { DataDir = Path.GetTempPath(), InitialAdminPassword = "the-right-password" }, new LoginThrottle(), NullLogger<AdminAuthService>.Instance, log);
        await auth.EnsureAdminPasswordAsync();

        Assert.True((await auth.ValidateAsync("admin", "the-right-password", "10.1.1.5")).Ok);
        Assert.False((await auth.ValidateAsync("admin", "wrong", "10.1.1.9")).Ok);
        Assert.False((await auth.ValidateAsync("my-secret-typed-here", "wrong", "10.1.1.9")).Ok);
        for (var i = 0; i < 3; i++) await auth.ValidateAsync("admin", "wrong", "10.1.1.9");
        Assert.False((await auth.ValidateAsync("admin", "the-right-password", "10.1.1.9")).Ok);
        var change = await auth.ChangePasswordAsync("not-it", "a-new-password-123");
        Assert.NotNull(change.Error);

        var entries = log.Entries;
        Assert.Equal(["auth.signin.succeeded", "auth.signin.failed", "auth.signin.failed"], entries.Take(3).Select(e => e.Action));
        Assert.Equal(AuditActor.User("admin", "10.1.1.5"), entries[0].Actor);
        Assert.Equal(("10.1.1.9", AuditOutcome.Failed), (entries[1].Actor.Ip!, entries[1].Outcome));
        Assert.Contains("wrong password", entries[1].Summary);
        // A name that isn't the admin's is never written: it may be a password typed in the wrong field.
        Assert.Equal("unknown user", entries[2].Actor.Name);
        Assert.DoesNotContain(entries, e => e.Summary.Contains("my-secret-typed-here") || e.Actor.Name.Contains("my-secret"));
        var refused = Assert.Single(entries, e => e.Action == "auth.signin.refused");
        Assert.Equal(AuditOutcome.Denied, refused.Outcome);
        Assert.Equal(AuditOutcome.Failed, entries[^1].Outcome);
        Assert.Equal("auth.password.changed", entries[^1].Action);
        Assert.DoesNotContain(entries, e => AuditExport.JsonLine(e).Contains("the-right-password") || AuditExport.JsonLine(e).Contains("a-new-password"));
    }

    // ---- Secrets ----

    private static AppSummary Web(AppSource source) => new("gb-web", 7, "web", "gb-web", source, ContainerRole.App, ImageOrigin.Registry, "nginx:1.27", null,
        AppState.Running, "Running", null, [], null, null, null, false, false, null);

    [Fact]
    public async Task No_secret_value_is_ever_written_to_the_log_or_its_exports()
    {
        var loaded = new ContainerSettings { Image = "nginx:1.27", Env = [new EnvSetting("PLAIN", "old-plain"), new EnvSetting("TOKEN_X", "old-token-value")] };
        var wanted = loaded with
        {
            Env =
            [
                new EnvSetting("PLAIN", "new-plain"),
                new EnvSetting("TOKEN_X", "new-token-value"),
                new EnvSetting("API_KEY", "s3cr3t-api-key", Secret: true),
                new EnvSetting("MY_VALUE", "marked-by-its-name-list"),
                new EnvSetting("DATABASE_URL", "postgres://app:pa55w0rd-in-url@db/app"),
                new EnvSetting("VISIBLE", new string('v', 300)),
            ],
            Resources = loaded.Resources with { MemoryBytes = 512L << 20 },
        };
        var options = new AppOptions { Name = "web", SecretKeys = ["MY_VALUE"] };
        var edit = new SettingsEdit(null, loaded, wanted, options, options);
        var fields = SettingsDiff.Changed(loaded, wanted);
        var plan = new SettingsPlan
        {
            Key = "gb-web", Edit = edit,
            Lines = SettingsDiff.Lines(loaded, wanted, fields, options.SecretKeys, _ => ApplyKind.Recreate),
        };

        var log = new AuditLog();
        foreach (var entry in SettingsAudit.For(Web(AppSource.Registry), 7, plan)) log.Record(entry);

        var entries = log.Entries;
        // By the variables' names: API_KEY, DATABASE_URL, MY_VALUE, PLAIN, TOKEN_X, VISIBLE.
        Assert.Equal(["app.settings.changed", "app.env.added", "app.env.added", "app.env.added", "app.env.changed", "app.env.changed", "app.env.added"],
            entries.Select(e => e.Action));
        var settings = entries[0];
        Assert.Equal("Changed web's settings: memory limit.", settings.Summary);
        Assert.Equal(("Memory limit", "none", "512 MiB"), (settings.Changes[0].Field, settings.Changes[0].From!, settings.Changes[0].To!));
        Assert.Equal("Added the secret variable API_KEY to web.", entries.Single(e => e.Target == "API_KEY").Summary);
        var plain = entries.Single(e => e.Target == "PLAIN");
        Assert.Equal(("old-plain", "new-plain"), (plain.Changes[0].From!, plain.Changes[0].To!));
        Assert.Equal(SettingsAudit.ValueMost, entries.Single(e => e.Target == "VISIBLE").Changes[0].To!.Length);

        using var csv = new MemoryStream();
        await log.ExportAsync(new AuditQuery(), AuditExportFormat.Csv, csv);
        using var jsonl = new MemoryStream();
        await log.ExportAsync(new AuditQuery(), AuditExportFormat.JsonLines, jsonl);
        var everything = string.Join('\n', entries.Select(AuditExport.JsonLine)) + System.Text.Encoding.UTF8.GetString(csv.ToArray())
            + System.Text.Encoding.UTF8.GetString(jsonl.ToArray());
        foreach (var secret in new[] { "s3cr3t-api-key", "marked-by-its-name-list", "pa55w0rd-in-url", "new-token-value", "old-token-value" })
            Assert.DoesNotContain(secret, everything);
        Assert.Contains("new-plain", everything);
    }

    [Fact]
    public void A_rename_is_its_own_entry_and_ports_alone_are_a_ports_change()
    {
        var options = new AppOptions { Name = "web" };
        var renamed = new SettingsPlan
        {
            Key = "gb-web", Edit = new SettingsEdit(null, new ContainerSettings(), new ContainerSettings(), options, options with { Name = "shop" }),
            Lines = AppSettingsService.OptionLines(options, options with { Name = "shop", GatherStats = false }).ToList(),
        };
        var entries = SettingsAudit.For(Web(AppSource.Existing), 7, renamed);
        Assert.Equal(["app.renamed", "app.settings.changed"], entries.Select(e => e.Action));
        Assert.Equal("Renamed web to shop.", entries[0].Summary);
        Assert.Equal("Changed web's settings: gather stats.", entries[1].Summary);

        var ports = new SettingsPlan
        {
            Key = "gb-web", Edit = new SettingsEdit(null, new ContainerSettings(), new ContainerSettings(), options, options),
            Lines = [new SettingLine(SettingField.Ports, "8080/tcp", "LAN 8080", "LAN 8081", ApplyKind.Recreate)],
        };
        var changed = Assert.Single(SettingsAudit.For(Web(AppSource.Registry), 7, ports, "the new container didn't start"));
        Assert.Equal(("app.ports.changed", AuditOutcome.Failed), (changed.Action, changed.Outcome));
        Assert.Equal("Couldn't change web's ports: the new container didn't start.", changed.Summary);
    }

    [Fact]
    public void A_secret_variable_added_is_one_entry_and_a_mark_changed_on_one_that_stays_is_a_settings_change()
    {
        var loaded = new ContainerSettings { Env = [new EnvSetting("KEPT", "a")] };
        var options = new AppOptions { Name = "web" };
        SettingsPlan Plan(ContainerSettings wanted, AppOptions after) => new()
        {
            Key = "gb-web", Edit = new SettingsEdit(null, loaded, wanted, options, after),
            Lines = [.. SettingsDiff.Lines(loaded, wanted, SettingsDiff.Changed(loaded, wanted), after.SecretKeys, _ => ApplyKind.Recreate),
                .. AppSettingsService.OptionLines(options, after)],
        };

        // A new variable marked secret: its own entry says it's secret, and nothing else is changed.
        var added = SettingsAudit.For(Web(AppSource.Git), 7, Plan(loaded with { Env = [.. loaded.Env, new EnvSetting("NEW_SETTING", "s3cr3t")] },
            options with { SecretKeys = ["NEW_SETTING"] }));
        Assert.Equal(["app.env.added"], added.Select(e => e.Action));
        Assert.Equal("Added the secret variable NEW_SETTING to web (all deploys).", added[0].Summary);

        // A variable that stays, marked secret: the settings entry says the list of secret names changed.
        var marked = SettingsAudit.For(Web(AppSource.Git), 7, Plan(loaded, options with { SecretKeys = ["KEPT"] }));
        var entry = Assert.Single(marked, e => e.Action == "app.settings.changed");
        Assert.Equal("Changed web's settings: secret variables.", entry.Summary);
        Assert.DoesNotContain(marked, e => e.Changes!.Any(c => c.From == "a" || c.To == "a"));
    }
}
