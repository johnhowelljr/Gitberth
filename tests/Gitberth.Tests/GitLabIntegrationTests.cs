using System.Net;
using System.Text;
using System.Text.Json;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// GitLab integration: what deploys tell GitLab (a commit status in GitLab's states, one note on a merge request kept up to
/// date), the project hook Gitberth keeps on each project, deliveries to /hooks/gitlab (their token checked in fixed time,
/// acted on at once), a project's CI/CD variables by environment scope, and Settings › GitLab. GitHub's side carries on
/// beside it as it was. Nothing here reaches GitLab or GitHub: stand-ins answer every request.
/// </summary>
public sealed class GitLabIntegrationTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private const string Sha = "abc1234def5678abc1234def5678abc1234def56";
    private const string WebId = "acme%2Fweb";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly GitLabStub _gitlab = new();
    private readonly GitLabStub _github = new();
    private readonly GitLabApi _api;
    private readonly GitHubIntegrationState _githubState = new();
    private readonly GitLabIntegrationState _state = new();
    private readonly int _appId, _previewId;

    public GitLabIntegrationTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "gitlab:acme", Repo = "web", Branch = "main", CurrentImage = "gitberth/web:abc1234" };
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true, Access = PortAccess.Lan });
            app.Domains.Add(new CustomDomain { Hostname = "web.example.com" });
            db.Apps.Add(app);
            db.SaveChanges();
            var preview = new AppDefinition { Name = "Web · MR !12", Slug = "web-pr-12", Owner = "gitlab:acme", Repo = "web", ParentAppId = app.Id, AutoDeploy = false };
            preview.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 8201, IsWeb = true, Access = PortAccess.Lan });
            db.Apps.Add(preview);
            db.SaveChanges();
            db.Previews.Add(new PreviewInfo
            {
                AppId = preview.Id, ParentAppId = app.Id, Kind = PreviewKind.PullRequest, PullRequest = 12, Branch = "feature", Ref = "refs/merge-requests/12/head",
            });
            db.SaveChanges();
            (_appId, _previewId) = (app.Id, preview.Id);
        }
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _api = new GitLabApi(new GitLabStub.Factory(_gitlab), _settings);
    }

    public void Dispose() => _connection.Dispose();

    private async Task WithTokenAsync()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
        await _settings.SetAsync(SettingKeys.PublicHost, "192.168.50.10");
        await _settings.SetAsync(SettingKeys.UiAddress, "http://192.168.50.10:8080/");
    }

    private GitHubReporter Reporter() => new(_db, _settings, new GitHubWrites(new GitLabStub.Factory(_github)), _githubState,
        new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<GitHubReporter>.Instance, gitLab: _api, gitLabState: _state);

    private static DeployReport Report(int appId, int deploymentId, DeployStatus status, DeployTrigger trigger = DeployTrigger.Webhook) =>
        new(appId, deploymentId, trigger, Sha, status, null);

    private static JsonElement BodyJson(string body) => JsonDocument.Parse(body).RootElement;

    private string Status(int at) =>
        _gitlab.Calls.Where(c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("/statuses/", StringComparison.Ordinal)).ElementAt(at);

    // ---- What deploys tell GitLab ----

    [Theory]
    [InlineData("pending", "running")]
    [InlineData("success", "success")]
    [InlineData("failure", "failed")]
    [InlineData("error", "canceled")]
    public void A_github_status_is_said_in_gitlabs_states(string github, string gitlab) => Assert.Equal(gitlab, GitHubReporter.GitLabState(github));

    [Fact]
    public async Task A_deploy_sets_a_status_on_its_branch_and_then_how_it_ended()
    {
        await WithTokenAsync();
        var reporter = Reporter();

        await reporter.StartedAsync(Report(_appId, 5, DeployStatus.Building), CancellationToken.None);
        await reporter.FinishedAsync(Report(_appId, 5, DeployStatus.Succeeded), CancellationToken.None);

        Assert.Equal([$"POST /api/v4/projects/{WebId}/statuses/{Sha}", $"POST /api/v4/projects/{WebId}/statuses/{Sha}"], _gitlab.Calls);
        var started = BodyJson(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/statuses/{Sha}"));
        Assert.Equal(("success", "Gitberth: Web", "main", "https://web.example.com/"),
            (started.GetProperty("state").GetString(), started.GetProperty("name").GetString(), started.GetProperty("ref").GetString(), started.GetProperty("target_url").GetString()));
        Assert.All(_gitlab.Requests, r => Assert.Equal("glpat-test", r.Headers.GetValues("PRIVATE-TOKEN").Single()));
        // GitHub hears nothing of a GitLab app.
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task A_merge_requests_preview_keeps_one_note_on_it()
    {
        await WithTokenAsync();
        var reporter = Reporter();
        _gitlab.Respond = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/notes", StringComparison.Ordinal)
            ? GitLabStub.Json(HttpStatusCode.Created, """{"id":77}""") : null;

        await reporter.StartedAsync(Report(_previewId, 6, DeployStatus.Building), CancellationToken.None);
        await reporter.FinishedAsync(Report(_previewId, 6, DeployStatus.Succeeded), CancellationToken.None);

        var calls = _gitlab.Calls;
        Assert.Equal(
        [
            $"POST /api/v4/projects/{WebId}/statuses/{Sha}",
            $"GET /api/v4/projects/{WebId}/merge_requests/12/notes?per_page=100&page=1",
            $"POST /api/v4/projects/{WebId}/merge_requests/12/notes",
            $"POST /api/v4/projects/{WebId}/statuses/{Sha}",
            $"PUT /api/v4/projects/{WebId}/merge_requests/12/notes/77",
        ], calls);
        var status = BodyJson(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/statuses/{Sha}"));
        Assert.Equal(("Gitberth preview: Web", "feature"), (status.GetProperty("name").GetString(), status.GetProperty("ref").GetString()));
        var note = BodyJson(_gitlab.BodyOf($"PUT /api/v4/projects/{WebId}/merge_requests/12/notes/77")).GetProperty("body").GetString()!;
        Assert.StartsWith(GitHubReports.CommentMarker, note);
        Assert.Contains("**Gitberth preview of Web: Ready**", note);
        await using var db = _db.CreateDbContext();
        Assert.Equal(77, (await db.Previews.SingleAsync(p => p.AppId == _previewId)).CommentId);
    }

    [Fact]
    public async Task Gitberths_note_whose_id_was_lost_is_found_by_its_marker_never_made_twice()
    {
        await WithTokenAsync();
        _gitlab.Respond = r => r.Method == HttpMethod.Get
            ? GitLabStub.Json(HttpStatusCode.OK, """[{"id":3,"body":"<!-- gitberth:preview --> quoted by someone","system":true},{"id":41,"body":"<!-- gitberth:preview -->\n**Gitberth preview of Web: Building**","system":false}]""")
            : null;

        await Reporter().StartedAsync(Report(_previewId, 7, DeployStatus.Building), CancellationToken.None);

        Assert.Contains($"PUT /api/v4/projects/{WebId}/merge_requests/12/notes/41", _gitlab.Calls);
        Assert.DoesNotContain($"POST /api/v4/projects/{WebId}/merge_requests/12/notes", _gitlab.Calls);
        await using var db = _db.CreateDbContext();
        Assert.Equal(41, (await db.Previews.SingleAsync(p => p.AppId == _previewId)).CommentId);
    }

    [Fact]
    public async Task A_forks_merge_request_and_a_promote_leave_the_branch_to_gitlab()
    {
        Assert.Equal("main", GitHubReporter.GitLabRefOf(new AppDefinition { Branch = "main" }, null, DeployTrigger.Webhook));
        Assert.Null(GitHubReporter.GitLabRefOf(new AppDefinition { Branch = "main" }, null, DeployTrigger.Promote));
        Assert.Equal("feature", GitHubReporter.GitLabRefOf(new AppDefinition(), new PreviewInfo { Branch = "feature" }, DeployTrigger.Poll));
        Assert.Null(GitHubReporter.GitLabRefOf(new AppDefinition(), new PreviewInfo { Branch = "feature", FromFork = true }, DeployTrigger.Poll));

        await using (var db = _db.CreateDbContext())
            await db.Previews.Where(p => p.AppId == _previewId).ExecuteUpdateAsync(s => s.SetProperty(p => p.FromFork, true));
        await WithTokenAsync();
        await _settings.SetAsync(SettingKeys.GitLabComments, "false");

        await Reporter().StartedAsync(Report(_previewId, 8, DeployStatus.Building), CancellationToken.None);

        var status = BodyJson(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/statuses/{Sha}"));
        Assert.False(status.TryGetProperty("ref", out _));
        // Comments are off: only the status.
        Assert.Single(_gitlab.Calls);
    }

    [Fact]
    public async Task Each_switch_and_each_hosts_token_stand_on_their_own()
    {
        // Only a GitHub token: a GitLab app isn't reported anywhere.
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await Reporter().StartedAsync(Report(_appId, 9, DeployStatus.Building), CancellationToken.None);
        Assert.Empty(_gitlab.Calls);
        Assert.Empty(_github.Calls);

        // GitLab's token, and its statuses off.
        await WithTokenAsync();
        await _settings.SetAsync(SettingKeys.GitLabStatuses, "false");
        await Reporter().StartedAsync(Report(_appId, 10, DeployStatus.Building), CancellationToken.None);
        Assert.Empty(_gitlab.Calls);
    }

    [Fact]
    public async Task The_same_state_isnt_posted_twice_and_gitlabs_refusal_of_an_unchanged_one_is_no_problem()
    {
        await WithTokenAsync();
        var reporter = Reporter();
        await reporter.StartedAsync(Report(_appId, 13, DeployStatus.Building), CancellationToken.None);
        // Told again that it builds: nothing new to say, so GitLab isn't asked a move its state machine refuses.
        await reporter.StartedAsync(Report(_appId, 13, DeployStatus.Building), CancellationToken.None);
        Assert.Single(_gitlab.Calls);

        // After a restart (another reporter) GitLab has it running already, and says so with its 400: that's done, not a problem.
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.BadRequest,
            """{"message":"Cannot transition status via :run from :running (Reason(s): Status cannot transition via \"run\")"}""");
        var restarted = Reporter();
        await restarted.StartedAsync(Report(_appId, 14, DeployStatus.Building), CancellationToken.None);
        Assert.Empty(_state.Problems);
        Assert.False(_state.Paused(GitHubFeature.Statuses, "acme/web", DateTime.UtcNow));

        // And how it ends is said as usual.
        _gitlab.Respond = null;
        await restarted.FinishedAsync(Report(_appId, 14, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Equal(3, _gitlab.Calls.Count);
        Assert.Equal("success", BodyJson(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/statuses/{Sha}")).GetProperty("state").GetString());
    }

    [Fact]
    public void A_gitlab_push_is_told_as_gitlabs_in_notifications()
    {
        var report = new DeployReport(_appId, 15, DeployTrigger.Webhook, Sha, DeployStatus.Succeeded, null);

        var gitlab = Gitberth.Notifications.DeployNotifier.Build(report, Gitberth.Notifications.NotifyEvent.DeploySucceeded, "Web", "gb-web", false, null, null,
            DateTime.UtcNow, gitLab: true);
        var github = Gitberth.Notifications.DeployNotifier.Build(report, Gitberth.Notifications.NotifyEvent.DeploySucceeded, "Web", "gb-web", false, null, null,
            DateTime.UtcNow);

        Assert.Equal("Deploy #15 (Push on GitLab, commit abc1234) succeeded.", gitlab.Text);
        Assert.Equal("Deploy #15 (Push on GitHub, commit abc1234) succeeded.", github.Text);
    }

    [Fact]
    public void A_gitlab_webhooks_deploy_is_gitlabs_in_the_audit_log_and_on_its_page()
    {
        var dep = new Deployment { Id = 16, Trigger = DeployTrigger.Webhook, CommitSha = Sha,
            RequestedBy = AuditActors.Format(RepoWatcher.WebhookActor(GitHost.GitLab, "34.74.90.1")) };
        Assert.Equal("GitLab|Webhook|34.74.90.1|GitLab", dep.RequestedBy);
        Assert.Equal("Queued a deploy of Web: GitLab said its branch changed (abc1234).", AuditText.Queued("Web", dep));
        Assert.Equal("GitLab webhook", AuditText.Trigger(dep));
        Assert.Equal("Push on GitLab", Format.Trigger(dep));

        var github = new Deployment { Id = 17, Trigger = DeployTrigger.Webhook, CommitSha = Sha,
            RequestedBy = AuditActors.Format(RepoWatcher.WebhookActor(GitHost.GitHub, "140.82.115.1")) };
        Assert.Equal("Queued a deploy of Web: GitHub said its branch changed (abc1234).", AuditText.Queued("Web", github));
        Assert.Equal(("GitHub webhook", "Push on GitHub"), (AuditText.Trigger(github), Format.Trigger(github)));
    }

    [Fact]
    public async Task A_project_gitlab_refuses_rests_and_a_refused_token_rests_everywhere()
    {
        await WithTokenAsync();
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");
        var reporter = Reporter();

        await reporter.StartedAsync(Report(_appId, 11, DeployStatus.Building), CancellationToken.None);
        await reporter.FinishedAsync(Report(_appId, 11, DeployStatus.Succeeded), CancellationToken.None);

        // Asked once: the project rests for an hour, and Settings › GitLab says why.
        Assert.Single(_gitlab.Calls);
        var problem = Assert.Single(_state.Problems);
        Assert.Equal((GitHubFeature.Statuses, "acme/web"), (problem.Feature, problem.Repo));
        Assert.Contains("the api scope, and at least the Developer role", problem.Text);
        Assert.Empty(_githubState.Problems);

        _state.Clear();
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        await reporter.StartedAsync(Report(_appId, 12, DeployStatus.Building), CancellationToken.None);
        Assert.True(_state.Paused(GitHubFeature.Statuses, DateTime.UtcNow));
        Assert.False(_githubState.Paused(GitHubFeature.Statuses, DateTime.UtcNow));
    }

    // ---- /hooks/gitlab ----

    private (GitLabWebhookEndpoint Endpoint, WatchState Watch) Endpoint()
    {
        var watch = new WatchState();
        return (new GitLabWebhookEndpoint(_db, _settings, watch, new WebhookThrottle(), NullLogger<GitLabWebhookEndpoint>.Instance), watch);
    }

    private static byte[] Push(string path) => Encoding.UTF8.GetBytes(
        $$"""{"object_kind":"push","ref":"refs/heads/main","before":"0000000000000000000000000000000000000000","after":"{{Sha}}","project":{"id":5,"path_with_namespace":"{{path}}"},"commits":[],"total_commits_count":0}""");

    [Fact]
    public void The_token_is_compared_in_fixed_time_and_the_project_read_from_the_body()
    {
        Assert.True(GitLabWebhookEvents.TokenMatches("s3cret", "s3cret"));
        Assert.False(GitLabWebhookEvents.TokenMatches("s3cret", "s3cre"));
        Assert.False(GitLabWebhookEvents.TokenMatches("s3cret", "S3CRET"));
        Assert.False(GitLabWebhookEvents.TokenMatches("s3cret", null));
        Assert.False(GitLabWebhookEvents.TokenMatches(null, "anything"));
        Assert.False(GitLabWebhookEvents.TokenMatches("", ""));

        Assert.Equal("acme/platform/web", GitLabWebhookEvents.ProjectOf(Push("acme/platform/web")));
        Assert.Equal("acme/web", GitLabWebhookEvents.ProjectOf(Encoding.UTF8.GetBytes(
            """{"object_kind":"merge_request","project":{"path_with_namespace":"acme/web"},"object_attributes":{"iid":12,"action":"open","source_project_id":9,"target_project_id":5}}""")));
        Assert.Null(GitLabWebhookEvents.ProjectOf("not json"u8));
        Assert.Null(GitLabWebhookEvents.ProjectOf("""{"project":{}}"""u8));
        Assert.True(GitLabWebhookEvents.Deploys("Push Hook"));
        Assert.True(GitLabWebhookEvents.Deploys("Merge Request Hook"));
        Assert.False(GitLabWebhookEvents.Deploys("Tag Push Hook"));
        Assert.False(GitLabWebhookEvents.Deploys("Pipeline Hook"));
    }

    [Fact]
    public async Task A_delivery_about_a_project_gitberth_deploys_wakes_the_watcher_for_it()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();

        Assert.Equal(StatusCodes.Status202Accepted, await endpoint.HandleAsync("Push Hook", "s3cret", Push("acme/web"), "34.74.90.1", host: "hooks.example.com"));
        Assert.True(watch.TakeWebhookPoke("gitlab:acme", "web"));
        Assert.Equal("34.74.90.1", watch.TakeWebhookSender("gitlab:acme", "web"));
        Assert.Equal("hooks.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookDelivered));

        Assert.Equal(StatusCodes.Status202Accepted, await endpoint.HandleAsync("Merge Request Hook", "s3cret", Encoding.UTF8.GetBytes(
            """{"object_kind":"merge_request","project":{"path_with_namespace":"ACME/Web"},"object_attributes":{"iid":12,"action":"update"}}"""), "34.74.90.1"));
        Assert.True(watch.TakeWebhookPoke("gitlab:acme", "web"));
    }

    [Fact]
    public async Task Anything_else_with_the_token_is_taken_and_left_alone()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabWebhookSecret, "s3cret");
        await using (var db = _db.CreateDbContext())
        {
            // A GitHub repo of the same name is GitHub's: a GitLab delivery never wakes it.
            db.Apps.Add(new AppDefinition { Name = "Other", Slug = "other", Owner = "acme", Repo = "other" });
            await db.SaveChangesAsync();
        }
        var (endpoint, watch) = Endpoint();

        Assert.Equal(StatusCodes.Status204NoContent, await endpoint.HandleAsync("Push Hook", "s3cret", Push("acme/other"), "1.2.3.4"));
        Assert.Equal(StatusCodes.Status204NoContent, await endpoint.HandleAsync("Tag Push Hook", "s3cret", Push("acme/web"), "1.2.3.4"));
        Assert.Equal(StatusCodes.Status204NoContent, await endpoint.HandleAsync("Push Hook", "s3cret", "{not json"u8.ToArray(), "1.2.3.4"));
        Assert.False(watch.TakeWebhookPoke("acme", "other"));
        Assert.False(watch.TakeWebhookPoke("gitlab:acme", "web"));
    }

    [Fact]
    public async Task A_bad_token_gets_401_and_too_many_from_one_sender_429_while_the_right_one_gets_through()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();

        for (var i = 0; i < WebhookThrottle.Limit; i++)
            Assert.Equal(StatusCodes.Status401Unauthorized, await endpoint.HandleAsync("Push Hook", "guess" + i, Push("acme/web"), "6.6.6.6"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await endpoint.HandleAsync("Push Hook", "guess", Push("acme/web"), "6.6.6.6"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await endpoint.HandleAsync("Push Hook", null, Push("acme/web"), "7.7.7.7"));
        Assert.False(watch.TakeWebhookPoke("gitlab:acme", "web"));
        // The hook's own token always gets through, even from a sender that sent bad ones.
        Assert.Equal(StatusCodes.Status202Accepted, await endpoint.HandleAsync("Push Hook", "s3cret", Push("acme/web"), "6.6.6.6"));

        // No secret yet: nothing can match it.
        await _settings.SetSecretAsync(SettingKeys.GitLabWebhookSecret, null);
        Assert.Equal(StatusCodes.Status401Unauthorized, await Endpoint().Endpoint.HandleAsync("Push Hook", "", Push("acme/web"), "8.8.8.8"));
    }

    private static DefaultHttpContext Request(string method, string? token, byte[] body, long? length = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = GitLabWebhookEndpoint.Path;
        context.Request.Host = new HostString("hooks.example.com");
        context.Request.Headers["X-Gitlab-Event"] = "Push Hook";
        if (token is not null) context.Request.Headers["X-Gitlab-Token"] = token;
        context.Request.Headers["CF-Connecting-IP"] = "34.74.90.1";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = length ?? body.Length;
        return context;
    }

    [Fact]
    public async Task The_endpoint_answers_bare_codes_and_checks_the_token_before_reading()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();

        var get = Request("GET", "s3cret", []);
        await endpoint.HandleAsync(get);
        Assert.Equal((405, "POST"), (get.Response.StatusCode, get.Response.Headers.Allow.ToString()));

        var bad = Request("POST", "nope", Push("acme/web"));
        await endpoint.HandleAsync(bad);
        Assert.Equal(401, bad.Response.StatusCode);
        Assert.Equal(0, bad.Request.Body.Position);

        var good = Request("POST", "s3cret", Push("acme/web"));
        await endpoint.HandleAsync(good);
        Assert.Equal(202, good.Response.StatusCode);
        Assert.Equal("34.74.90.1", watch.TakeWebhookSender("gitlab:acme", "web"));

        // GitLab's, but too big to read: still taken, so GitLab never turns the hook off for it.
        var huge = Request("POST", "s3cret", [], length: GitLabWebhookEndpoint.MaxBody + 1L);
        await endpoint.HandleAsync(huge);
        Assert.Equal(202, huge.Response.StatusCode);
    }

    // ---- The project hook ----

    private async Task HooksOnAsync(string host = "hooks.example.com")
    {
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, host);
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
    }

    private WebhookKeeper Keeper() => new(_db, _settings, new GitHubWrites(new GitLabStub.Factory(_github)), _githubState, NullLogger<WebhookKeeper>.Instance,
        gitLab: _api, gitLabState: _state);

    private async Task<Dictionary<string, WebhookKeeper.KeptHook>> KeptAsync(string key = SettingKeys.GitLabWebhookIds) =>
        WebhookKeeper.KeptHooks(await _settings.GetAsync(key));

    [Fact]
    public async Task Each_project_gets_gitberths_project_hook_with_its_own_secret_token()
    {
        await HooksOnAsync();
        _gitlab.Respond = r => r.Method == HttpMethod.Post ? GitLabStub.Json(HttpStatusCode.Created, """{"id":21}""") : null;

        await Keeper().EnsureAllAsync();

        Assert.Equal([$"GET /api/v4/projects/{WebId}/hooks?per_page=100&page=1", $"POST /api/v4/projects/{WebId}/hooks"], _gitlab.Calls);
        var body = BodyJson(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/hooks"));
        var secret = await _settings.GetSecretAsync(SettingKeys.GitLabWebhookSecret);
        Assert.NotNull(secret);
        Assert.Equal(("https://hooks.example.com/hooks/gitlab", secret, true, true, true),
            (body.GetProperty("url").GetString(), body.GetProperty("token").GetString(), body.GetProperty("push_events").GetBoolean(),
                body.GetProperty("merge_requests_events").GetBoolean(), body.GetProperty("enable_ssl_verification").GetBoolean()));
        Assert.Equal(new WebhookKeeper.KeptHook(21, "https://hooks.example.com/hooks/gitlab"), (await KeptAsync())["acme/web"]);
        var hook = Assert.Single(_state.Hooks);
        Assert.Equal(("acme/web", true, "Webhook added"), (hook.Repo, hook.Ok, hook.Text));
        // GitHub's webhook secret is never GitLab's, and GitHub heard nothing.
        Assert.NotEqual(secret, await _settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret));
        Assert.Empty(_github.Calls);
        Assert.Null(await _settings.GetAsync(SettingKeys.GitHubWebhookIds));

        // Done for this host name: the next run asks nothing.
        _gitlab.Respond = null;
        var calls = _gitlab.Calls.Count;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Equal(calls + 2, _gitlab.Calls.Count);
        await keeper.EnsureAllAsync();
        Assert.Equal(calls + 2, _gitlab.Calls.Count);
    }

    [Fact]
    public async Task The_project_hook_follows_a_new_host_name_and_anyone_elses_is_left_alone()
    {
        await HooksOnAsync();
        var hooks = """[{"id":9,"url":"https://hooks.example.com/hooks/gitlab","push_events":true,"merge_requests_events":true},{"id":3,"url":"https://ci.example.com/x"}]""";
        _gitlab.Respond = r => r.Method == HttpMethod.Get ? GitLabStub.Json(HttpStatusCode.OK, hooks) : GitLabStub.Json(HttpStatusCode.OK, """{"id":9}""");
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Contains($"PUT /api/v4/projects/{WebId}/hooks/9", _gitlab.Calls);

        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gl.example.com");
        await keeper.EnsureAllAsync();

        Assert.Equal("https://gl.example.com/hooks/gitlab", BodyJson(_gitlab.BodyOf($"PUT /api/v4/projects/{WebId}/hooks/9")).GetProperty("url").GetString());
        Assert.DoesNotContain(_gitlab.Calls, c => c.EndsWith("/hooks/3", StringComparison.Ordinal));
        Assert.Equal(new WebhookKeeper.KeptHook(9, "https://gl.example.com/hooks/gitlab"), (await KeptAsync())["acme/web"]);
    }

    [Fact]
    public async Task A_project_gitlab_refuses_says_so_and_rests_and_a_refused_token_stops_them_all()
    {
        await HooksOnAsync();
        _gitlab.Respond = r => r.Method == HttpMethod.Get ? null : GitLabStub.Json(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");
        var keeper = Keeper();

        await keeper.EnsureAllAsync();

        var hook = Assert.Single(_state.Hooks);
        Assert.False(hook.Ok);
        Assert.Contains("the api scope, and at least the Maintainer role", hook.Text);
        Assert.True(_state.Paused(GitHubFeature.Webhooks, "acme/web", DateTime.UtcNow));
        Assert.Empty(await KeptAsync());

        _state.Clear();
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        await keeper.EnsureAllAsync();
        Assert.True(_state.Paused(GitHubFeature.Webhooks, DateTime.UtcNow));
        Assert.False(_githubState.Paused(GitHubFeature.Webhooks, DateTime.UtcNow));
    }

    [Fact]
    public async Task Github_repos_and_gitlab_projects_each_get_their_own_hook_and_records()
    {
        await HooksOnAsync();
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "github-secret");
        await using (var db = _db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "Site", Slug = "site", Owner = "acme", Repo = "web" });
            await db.SaveChangesAsync();
        }
        _github.Respond = r => r.Method == HttpMethod.Get ? GitLabStub.Json(HttpStatusCode.OK, "[]") : GitLabStub.Json(HttpStatusCode.Created, """{"id":5}""");
        _gitlab.Respond = r => r.Method == HttpMethod.Post ? GitLabStub.Json(HttpStatusCode.Created, """{"id":21}""") : null;

        await Keeper().EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks?per_page=100", "POST /repos/acme/web/hooks"], _github.Calls);
        Assert.Equal("https://hooks.example.com/hooks/github", BodyJson(_github.BodyOf("POST /repos/acme/web/hooks")).GetProperty("config").GetProperty("url").GetString());
        Assert.Equal(["acme/web"], (await KeptAsync(SettingKeys.GitHubWebhookIds)).Keys);
        Assert.Equal(5, (await KeptAsync(SettingKeys.GitHubWebhookIds))["acme/web"].Id);
        Assert.Equal(21, (await KeptAsync())["acme/web"].Id);
        Assert.Equal("acme/web", Assert.Single(_githubState.Hooks).Repo);
        Assert.Equal("acme/web", Assert.Single(_state.Hooks).Repo);
    }

    [Fact]
    public async Task Without_a_host_name_or_a_gitlab_token_nothing_happens_on_gitlab()
    {
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-test");
        await Keeper().EnsureAllAsync();
        Assert.Empty(_gitlab.Calls);

        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "hooks.example.com");
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, null);
        await Keeper().EnsureAllAsync();
        Assert.Empty(_gitlab.Calls);
    }

    // ---- CI/CD variables ----

    private static RepoVariable Var(string name, string value, string scope, bool masked = false, bool isProtected = false, bool file = false) =>
        new(name, value, scope, masked, isProtected, file);

    [Fact]
    public void Production_and_previews_each_get_their_scopes_and_the_scopes_own_value_wins()
    {
        List<RepoVariable> all =
        [
            Var("API_URL", "https://api.example.com", "*"),
            Var("API_URL", "https://prod.example.com", "production"),
            Var("API_URL", "https://preview.example.com", "preview/*"),
            Var("SENTRY_DSN", "https://sentry", "production", isProtected: true),
            Var("STRIPE_KEY", "sk_live", "*", masked: true, isProtected: true),
            Var("CERT", "-----BEGIN-----", "*", file: true),
            Var("STAGING_ONLY", "x", "staging"),
            Var("REVIEW", "y", "review/*"),
        ];

        var (production, productionNotes) = RepoVariables.Scoped(all, preview: false);
        Assert.Equal([("API_URL", "https://prod.example.com"), ("SENTRY_DSN", "https://sentry"), ("STRIPE_KEY", "sk_live")], production.Select(v => (v.Name, v.Value)));
        Assert.Equal(["Not used from GitLab: CERT, a file variable. Gitberth passes plain ones (Variable type) only."], productionNotes);

        var (preview, previewNotes) = RepoVariables.Scoped(all, preview: true);
        Assert.Equal([("API_URL", "https://preview.example.com")], preview.Select(v => (v.Name, v.Value)));
        Assert.Equal(
        [
            "Not used from GitLab: CERT, a file variable. Gitberth passes plain ones (Variable type) only.",
            "Not given to previews: STRIPE_KEY, protected in GitLab, which only production gets.",
        ], previewNotes);
    }

    [Fact]
    public async Task A_deploy_reads_its_environments_variables_and_falls_back_to_the_kept_copy()
    {
        await WithTokenAsync();
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.OK,
            """[{"key":"API_URL","value":"https://api","environment_scope":"*"},{"key":"API_URL","value":"https://pr","environment_scope":"preview/*"},{"key":"DB","value":"prod-db","environment_scope":"production","protected":true}]""");
        var variables = new RepoVariables(new GitHubApi(new GitLabStub.Factory(_github)), _settings, gitLab: _api);

        var production = await variables.ForDeployAsync("gitlab:acme", "web");
        Assert.Equal([("API_URL", "https://api"), ("DB", "prod-db")], production.Variables.Select(v => (v.Name, v.Value)));
        var preview = await variables.ForDeployAsync("gitlab:acme", "web", preview: true);
        Assert.Equal([("API_URL", "https://pr")], preview.Variables.Select(v => (v.Name, v.Value)));
        // A preview's copy of production's database is made from what production gets.
        Assert.Equal([("API_URL", "https://api"), ("DB", "prod-db")], preview.ForProduction.Select(v => (v.Name, v.Value)));
        Assert.Equal($"GET /api/v4/projects/{WebId}/variables?per_page=100&page=1", _gitlab.Calls[0]);

        // GitLab can't be read: the copy kept from the last read, still by scope.
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.BadGateway, "{}");
        var kept = await variables.ForDeployAsync("gitlab:acme", "web", preview: true);
        Assert.True(kept.Saved);
        Assert.Equal([("API_URL", "https://pr")], kept.Variables.Select(v => (v.Name, v.Value)));
        Assert.StartsWith("GitLab answered 502", kept.Error);

        // Without a token, it says where to add one.
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, null);
        await variables.ForgetAsync("gitlab:acme", "web");
        Assert.Equal("No GitLab token is saved, so the project's CI/CD variables can't be read. Add one in Settings › GitLab.",
            (await variables.ForDeployAsync("gitlab:acme", "web")).Error);
    }

    [Fact]
    public void A_github_repos_kept_copy_is_written_as_it_always_was()
    {
        Assert.Equal("""[{"Name":"API_URL","Value":"x"}]""", JsonSerializer.Serialize(new List<RepoVariable> { new("API_URL", "x") }));
        Assert.Equal(new RepoVariable("API_URL", "x"), RepoVariables.ParseSaved("""[{"Name":"API_URL","Value":"x"}]""")![0]);
        Assert.Equal(new RepoVariable("K", "v", "production", Protected: true),
            RepoVariables.ParseSaved(JsonSerializer.Serialize(new List<RepoVariable> { new("K", "v", "production", Protected: true) }))![0]);
    }

    [Fact]
    public async Task The_environment_tab_saves_and_deletes_a_variable_for_one_scope_and_the_log_says_which()
    {
        await WithTokenAsync();
        _gitlab.Respond = r => r.Method == HttpMethod.Put ? GitLabStub.Json(HttpStatusCode.OK, """{"key":"API_URL"}""") : new HttpResponseMessage(HttpStatusCode.NoContent);
        var log = new AuditLog();
        var variables = new RepoVariables(new GitHubApi(new GitLabStub.Factory(_github)), _settings, log, _api);
        var web = new AppDefinition { Id = _appId, Name = "Web", Slug = "web", Owner = "gitlab:acme", Repo = "web" };

        await variables.SetAsync("gitlab:acme", "web", "API_URL", "https://pr", app: web, scope: RepoVariables.PreviewScope);
        await variables.DeleteAsync("gitlab:acme", "web", "API_URL", app: web, previous: "https://pr", scope: RepoVariables.PreviewScope);
        await Assert.ThrowsAsync<GitLabApiException>(() => variables.SetAsync("gitlab:acme", "web", "API_URL", "x", app: web, scope: "staging"));
        await Assert.ThrowsAsync<GitLabApiException>(() => variables.SetAsync("gitlab:acme", "web", "2BAD", "x", app: web));

        Assert.Equal([$"PUT /api/v4/projects/{WebId}/variables/API_URL?filter%5Benvironment_scope%5D=preview%2F%2A",
            $"DELETE /api/v4/projects/{WebId}/variables/API_URL?filter%5Benvironment_scope%5D=preview%2F%2A"], _gitlab.Calls);
        var entries = log.Entries;
        Assert.Equal(["app.env.gitlab.set", "app.env.gitlab.removed", "app.env.gitlab.set", "app.env.gitlab.set"], entries.Select(e => e.Action));
        Assert.Equal("Saved the variable API_URL to acme/web's CI/CD variables in GitLab (scope preview/*): every preview of an app from the project gets it at its next deploy.",
            entries[0].Summary);
        Assert.Contains(("Environment scope", null, "preview/*"), entries[0].Changes!.Select(c => (c.Field, c.From, c.To)));
        Assert.Equal(("Project", null, "acme/web"), (entries[0].Changes![0].Field, entries[0].Changes![0].From, entries[0].Changes![0].To));
        Assert.Equal(AuditOutcome.Failed, entries[2].Outcome);
        Assert.EndsWith("Gitberth saves GitLab variables for every deploy (*), production, or previews (preview/*).", entries[2].Summary);
        Assert.Equal(AuditOutcome.Failed, entries[3].Outcome);
    }

    // ---- Settings › GitLab ----

    [Fact]
    public async Task A_token_gitlab_refuses_isnt_saved_and_one_it_takes_is_kept_encrypted_with_what_its_check_said()
    {
        var log = new AuditLog();
        var gitLabSettings = new GitLabSettings(_settings, _api, _state, log);
        _state.Report(GitHubFeature.Statuses, "refused before", DateTime.UtcNow);

        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var refused = await gitLabSettings.SaveAsync("gitlab.example.com", "glpat-bad");
        Assert.False(refused.Ok);
        Assert.Equal("GitLab says this token is invalid, expired or revoked.", refused.Message);
        Assert.Null(await _settings.GetAsync(SettingKeys.GitLabToken));
        Assert.Null(await _settings.GetAsync(SettingKeys.GitLabUrl));
        Assert.Equal("https://gitlab.example.com/api/v4/user", _gitlab.Requests.Single().RequestUri!.AbsoluteUri);

        _gitlab.Respond = r => r.RequestUri!.AbsolutePath == "/api/v4/user" ? GitLabStub.Json(HttpStatusCode.OK, """{"username":"ada"}""")
            : GitLabStub.Json(HttpStatusCode.OK, """{"scopes":["api"],"expires_at":"2027-02-01"}""");
        await _settings.SetAsync(SettingKeys.GitLabWebhookIds, """{"acme/web":{"Id":9,"Url":"https://hooks.example.com/hooks/gitlab"}}""");
        var saved = await gitLabSettings.SaveAsync("https://gitlab.example.com/", "glpat-good");
        Assert.True(saved.Ok);
        Assert.Equal("Saved: Gitberth's GitLab is gitlab.example.com, with the new token.", saved.Message);
        Assert.Equal("https://gitlab.example.com", await _settings.GetAsync(SettingKeys.GitLabUrl));
        // The hooks kept were the old GitLab's.
        Assert.Null(await _settings.GetAsync(SettingKeys.GitLabWebhookIds));
        Assert.Equal("glpat-good", await _settings.GetSecretAsync(SettingKeys.GitLabToken));
        Assert.DoesNotContain("glpat-good", await _settings.GetAsync(SettingKeys.GitLabToken));
        var status = await gitLabSettings.StatusAsync();
        Assert.Equal(("https://gitlab.example.com", true, "ada", "api"), (status.Instance, status.TokenSaved, status.Login, string.Join(',', status.Scopes!)));
        Assert.Empty(status.Missing);
        Assert.Empty(_state.Problems);
        // The log says who and which scopes, never the token.
        Assert.Equal(["settings.gitlab.token.saved", "settings.gitlab.changed", "settings.gitlab.token.saved"], log.Entries.Select(e => e.Action));
        Assert.DoesNotContain("glpat", string.Join('\n', log.Entries.Select(AuditExport.JsonLine)));

        await gitLabSettings.RemoveTokenAsync();
        Assert.Equal((false, (string?)null), ((await gitLabSettings.StatusAsync()).TokenSaved, (await gitLabSettings.StatusAsync()).Login));
        Assert.False((await gitLabSettings.SaveAsync("ftp://x", null)).Ok);
    }
}
