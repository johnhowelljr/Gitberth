using System.Net;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// GitHub integration: commit statuses and the pull request comment (what they say, and that a missing permission rests
/// rather than costing every deploy a call), the webhook's signature and answers, and the webhook kept on each repo.
/// Nothing here reaches GitHub: a stand-in answers every request.
/// </summary>
public sealed class GitHubIntegrationTests : IDisposable
{
    private sealed class GitHubStub : HttpMessageHandler
    {
        private readonly List<(string Call, string Body)> _calls = [];

        /// <summary>GitHub's answers; anything it returns null for gets a 201 with {"id":1}.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }

        public List<string> Calls
        {
            get { lock (_calls) return [.. _calls.Select(c => c.Call)]; }
        }

        public string BodyOf(string call)
        {
            lock (_calls) return _calls.Last(c => c.Call == call).Body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_calls) _calls.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", body));
            return Respond?.Invoke(request) ?? Json(HttpStatusCode.Created, """{"id":1}""");
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly GitHubStub _github = new();
    private readonly GitHubIntegrationState _state = new();
    private readonly GitHubReporter _reporter;
    private readonly int _appId, _previewId;

    public GitHubIntegrationTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "acme", Repo = "web", CurrentImage = "gitberth/web:abc1234" };
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true, Access = PortAccess.Lan });
            app.Domains.Add(new CustomDomain { Hostname = "web.example.com" });
            db.Apps.Add(app);
            db.SaveChanges();
            var preview = new AppDefinition { Name = "Web · PR #12", Slug = "web-pr-12", Owner = "acme", Repo = "web", ParentAppId = app.Id, AutoDeploy = false };
            preview.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 8201, IsWeb = true, Access = PortAccess.Lan });
            db.Apps.Add(preview);
            db.SaveChanges();
            db.Previews.Add(new PreviewInfo { AppId = preview.Id, ParentAppId = app.Id, Kind = PreviewKind.PullRequest, PullRequest = 12, Branch = "feature", Ref = "refs/pull/12/head" });
            db.SaveChanges();
            (_appId, _previewId) = (app.Id, preview.Id);
        }
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _reporter = new GitHubReporter(_db, _settings, new GitHubWrites(new Factory(_github)), _state,
            new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<GitHubReporter>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task WithTokenAsync()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await _settings.SetAsync(SettingKeys.PublicHost, "192.168.50.10");
        await _settings.SetAsync(SettingKeys.UiAddress, "http://192.168.50.10:8080/");
    }

    private const string Sha = "abc1234def5678abc1234def5678abc1234def56";

    private static DeployReport Report(int appId, int deploymentId, DeployStatus status, string? sha = Sha, string? error = null) =>
        new(appId, deploymentId, DeployTrigger.Webhook, sha, status, error);

    private static JsonElement BodyJson(string body) => JsonDocument.Parse(body).RootElement;

    // ---- What GitHub is told ----

    [Fact]
    public void Each_app_and_its_previews_report_under_a_context_of_their_own()
    {
        Assert.Equal("Gitberth: Web", GitHubReports.Context("Web", preview: false));
        Assert.Equal("Gitberth preview: Web", GitHubReports.Context("Web", preview: true));
    }

    [Theory]
    [InlineData(DeployStatus.Building, null, "pending")]
    [InlineData(DeployStatus.Succeeded, null, "success")]
    [InlineData(DeployStatus.Failed, "npm ci exited with 1", "failure")]
    [InlineData(DeployStatus.RolledBack, "It stopped", "failure")]
    [InlineData(DeployStatus.Cancelled, "Cancelled", "error")]
    [InlineData(DeployStatus.Failed, "Gitberth restarted during this deploy. It's tried again automatically.", "error")]
    public void A_deploy_maps_to_a_commit_status(DeployStatus status, string? error, string state)
    {
        var s = GitHubReports.Status(status, error, "Gitberth: Web", "https://web.example.com/", "http://192.168.50.10:8080/apps/1/deployments/5", preview: false);
        Assert.Equal(state, s.State);
        Assert.Equal("Gitberth: Web", s.Context);
        Assert.True(s.Description.Length <= GitHubReports.DescriptionLimit);
        // The deploy's error stays on Gitberth: a public repo would show it to everyone.
        if (error is not null) Assert.DoesNotContain(error, s.Description);
        Assert.Equal(state == "success" ? "https://web.example.com/" : "http://192.168.50.10:8080/apps/1/deployments/5", s.TargetUrl);
    }

    [Fact]
    public void Success_says_where_the_app_is_live()
    {
        Assert.Equal("Deploy is live at web.example.com", GitHubReports.Status(DeployStatus.Succeeded, null, "c", "https://web.example.com/", null, false).Description);
        Assert.Equal("Preview is live", GitHubReports.Status(DeployStatus.Succeeded, null, "c", null, null, true).Description);
    }

    [Fact]
    public void The_address_is_the_main_domain_else_the_lan_address_of_a_lan_web_port()
    {
        var app = new AppDefinition { Ports = [new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true, Access = PortAccess.Lan }] };
        Assert.Equal("http://192.168.50.10:3001/", GitHubReports.Address(app, "192.168.50.10"));
        Assert.Null(GitHubReports.Address(app, null));
        app.Ports[0].Access = PortAccess.HostOnly;
        Assert.Null(GitHubReports.Address(app, "192.168.50.10"));
        app.Domains.Add(new CustomDomain { Id = 1, Hostname = "web.example.com" });
        Assert.Equal("https://web.example.com/", GitHubReports.Address(app, "192.168.50.10"));
    }

    [Fact]
    public void The_log_link_needs_the_address_gitberth_was_opened_at()
    {
        Assert.Equal("http://192.168.50.10:8080/apps/3/deployments/41", GitHubReports.LogUrl("http://192.168.50.10:8080/", 3, 41));
        Assert.Null(GitHubReports.LogUrl(null, 3, 41));
        Assert.Null(GitHubReports.LogUrl("not a url", 3, 41));
    }

    [Fact]
    public void The_pull_request_comment_says_state_address_commit_and_time()
    {
        var body = GitHubReports.CommentBody(new PreviewComment("Web", "Ready", "https://web-pr-12.example.com/", Sha, "http://192.168.50.10:8080/apps/2/deployments/9",
            new DateTime(2026, 11, 9, 14, 3, 0, DateTimeKind.Utc)));
        Assert.StartsWith(GitHubReports.CommentMarker, body);
        Assert.Contains("Gitberth preview of Web: Ready", body);
        Assert.Contains("[web-pr-12.example.com](https://web-pr-12.example.com/)", body);
        Assert.Contains("`abc1234`", body);
        Assert.Contains("Nov 9, 2026 14:03 UTC", body);
        Assert.Contains("(http://192.168.50.10:8080/apps/2/deployments/9)", body);

        var failed = GitHubReports.CommentBody(new PreviewComment("Web", "Failed", null, null, null, DateTime.UtcNow));
        Assert.Contains("No address yet", failed);
        Assert.Contains("The deploy failed.", failed);
        Assert.DoesNotContain("Deploy log", failed);
    }

    // ---- The API calls ----

    [Fact]
    public async Task A_status_is_posted_on_the_commit_with_its_context_and_link()
    {
        await new GitHubWrites(new Factory(_github)).SetStatusAsync("t", "acme", "web", Sha, new CommitStatus("pending", "Building", "Gitberth: Web", "http://x/"));
        Assert.Equal([$"POST /repos/acme/web/statuses/{Sha}"], _github.Calls);
        var body = BodyJson(_github.BodyOf($"POST /repos/acme/web/statuses/{Sha}"));
        Assert.Equal("pending", body.GetProperty("state").GetString());
        Assert.Equal("Gitberth: Web", body.GetProperty("context").GetString());
        Assert.Equal("http://x/", body.GetProperty("target_url").GetString());
    }

    [Theory]
    [InlineData(GitHubFeature.Statuses, "Commit statuses: Read and write")]
    [InlineData(GitHubFeature.Comments, "Pull requests: Read and write")]
    [InlineData(GitHubFeature.Webhooks, "Webhooks: Read and write")]
    public void A_missing_permission_names_the_one_the_token_needs(GitHubFeature feature, string permission)
    {
        var text = GitHubWrites.MissingPermissionText(feature, "do it");
        Assert.Contains(permission, text);
        Assert.Contains(feature == GitHubFeature.Webhooks ? "admin:repo_hook" : "the repo scope", text);
    }

    [Fact]
    public async Task A_refusal_is_a_permission_problem_but_a_rate_limit_is_not()
    {
        var api = new GitHubWrites(new Factory(_github));
        _github.Respond = _ => Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}""");
        var refused = await Assert.ThrowsAsync<GitHubWriteException>(() => api.SetStatusAsync("t", "acme", "web", Sha, new CommitStatus("pending", "x", "c", null)));
        Assert.True(refused.MissingPermission);
        Assert.Contains("Commit statuses: Read and write", refused.Message);

        _github.Respond = _ =>
        {
            var r = Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""");
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        };
        var limited = await Assert.ThrowsAsync<GitHubWriteException>(() => api.SetStatusAsync("t", "acme", "web", Sha, new CommitStatus("pending", "x", "c", null)));
        Assert.False(limited.MissingPermission);
        Assert.Contains("rate limit", limited.Message);
    }

    [Fact]
    public async Task Hooks_are_listed_and_saved_with_the_secret_and_events()
    {
        var api = new GitHubWrites(new Factory(_github));
        _github.Respond = r => r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """[{"id":7,"active":true,"events":["push"],"config":{"url":"https://hooks.example.com/hooks/github"}}]""")
            : null;
        var hook = Assert.Single(await api.ListHooksAsync("t", "acme", "web"));
        Assert.Equal((7L, "https://hooks.example.com/hooks/github", true), (hook.Id, hook.Url, hook.Active));
        Assert.Equal(["push"], hook.Events);

        await api.SaveHookAsync("t", "acme", "web", 7, "https://hooks.example.com/hooks/github", "s3cret");
        await api.SaveHookAsync("t", "acme", "web", null, "https://hooks.example.com/hooks/github", "s3cret");
        Assert.Contains("PATCH /repos/acme/web/hooks/7", _github.Calls);
        var body = BodyJson(_github.BodyOf("POST /repos/acme/web/hooks"));
        Assert.Equal(["push", "pull_request", "delete"], body.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("s3cret", body.GetProperty("config").GetProperty("secret").GetString());
        Assert.Equal("json", body.GetProperty("config").GetProperty("content_type").GetString());
    }

    // ---- The reporter ----

    [Fact]
    public async Task Without_a_token_nothing_is_reported()
    {
        await _reporter.StartedAsync(Report(_appId, 1, DeployStatus.Building), CancellationToken.None);
        await _reporter.FinishedAsync(Report(_appId, 1, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task A_deploy_sets_pending_then_success_with_its_domain()
    {
        await WithTokenAsync();
        await _reporter.StartedAsync(Report(_appId, 5, DeployStatus.Building), CancellationToken.None);
        var pending = BodyJson(_github.BodyOf($"POST /repos/acme/web/statuses/{Sha}"));
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal($"http://192.168.50.10:8080/apps/{_appId}/deployments/5", pending.GetProperty("target_url").GetString());

        await _reporter.FinishedAsync(Report(_appId, 5, DeployStatus.Succeeded), CancellationToken.None);
        var success = BodyJson(_github.BodyOf($"POST /repos/acme/web/statuses/{Sha}"));
        Assert.Equal("success", success.GetProperty("state").GetString());
        Assert.Equal("https://web.example.com/", success.GetProperty("target_url").GetString());
        Assert.Equal("Gitberth: Web", success.GetProperty("context").GetString());
        // A production app has no pull request to comment on.
        Assert.DoesNotContain(_github.Calls, c => c.Contains("comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_recreate_that_never_built_reports_nothing()
    {
        await WithTokenAsync();
        await _reporter.FinishedAsync(Report(_appId, 6, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task Statuses_turned_off_post_none()
    {
        await WithTokenAsync();
        await _settings.SetAsync(SettingKeys.GitHubStatuses, "false");
        await _reporter.StartedAsync(Report(_appId, 7, DeployStatus.Building), CancellationToken.None);
        await _reporter.FinishedAsync(Report(_appId, 7, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task A_preview_comments_once_then_edits_its_comment_and_makes_a_new_one_when_it_was_deleted()
    {
        await WithTokenAsync();
        _github.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/issues/12/comments", StringComparison.Ordinal) ? Json(HttpStatusCode.Created, """{"id":555}""") : null;

        await _reporter.StartedAsync(Report(_previewId, 8, DeployStatus.Building), CancellationToken.None);
        Assert.Contains("POST /repos/acme/web/issues/12/comments", _github.Calls);
        var status = BodyJson(_github.BodyOf($"POST /repos/acme/web/statuses/{Sha}"));
        Assert.Equal("Gitberth preview: Web", status.GetProperty("context").GetString());
        await using (var db = _db.CreateDbContext())
            Assert.Equal(555, (await db.Previews.SingleAsync()).CommentId);

        await _reporter.FinishedAsync(Report(_previewId, 8, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Contains("PATCH /repos/acme/web/issues/comments/555", _github.Calls);
        Assert.Contains("Ready", _github.BodyOf("PATCH /repos/acme/web/issues/comments/555"));
        Assert.Contains("http://192.168.50.10:8201/", _github.BodyOf("PATCH /repos/acme/web/issues/comments/555"));

        // Someone deleted it: the next deploy makes a new one and remembers that.
        _github.Respond = r => r.Method == HttpMethod.Patch ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : r.RequestUri!.AbsolutePath.EndsWith("/issues/12/comments", StringComparison.Ordinal) ? Json(HttpStatusCode.Created, """{"id":777}""") : null;
        await _reporter.StartedAsync(Report(_previewId, 9, DeployStatus.Building), CancellationToken.None);
        await using (var db = _db.CreateDbContext())
            Assert.Equal(777, (await db.Previews.SingleAsync()).CommentId);
    }

    [Fact]
    public async Task A_preview_that_failed_before_its_commit_was_known_still_says_so_on_its_pull_request()
    {
        await WithTokenAsync();
        await _reporter.FinishedAsync(Report(_previewId, 10, DeployStatus.Failed, sha: null, error: "Branch 'feature' doesn't exist"), CancellationToken.None);
        Assert.Equal(["POST /repos/acme/web/issues/12/comments"], _github.Calls);
        Assert.Contains("Failed", _github.BodyOf("POST /repos/acme/web/issues/12/comments"));
    }

    [Fact]
    public async Task A_missing_permission_is_said_once_and_rests_instead_of_costing_every_deploy_a_call()
    {
        await WithTokenAsync();
        await _settings.SetAsync(SettingKeys.GitHubComments, "false");
        _github.Respond = _ => Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}""");
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        _reporter.Now = () => now;

        await _reporter.StartedAsync(Report(_appId, 11, DeployStatus.Building), CancellationToken.None);
        var problem = Assert.Single(_state.Problems);
        Assert.Equal(GitHubFeature.Statuses, problem.Feature);
        Assert.Contains("Commit statuses: Read and write", problem.Text);

        await _reporter.FinishedAsync(Report(_appId, 11, DeployStatus.Succeeded), CancellationToken.None);
        await _reporter.StartedAsync(Report(_appId, 12, DeployStatus.Building), CancellationToken.None);
        Assert.Single(_github.Calls);

        // An hour on, it's tried again; and working clears what Settings says.
        now += GitHubIntegrationState.PauseFor;
        _github.Respond = null;
        await _reporter.StartedAsync(Report(_appId, 13, DeployStatus.Building), CancellationToken.None);
        Assert.Equal(2, _github.Calls.Count);
        Assert.Empty(_state.Problems);
    }

    [Fact]
    public async Task A_new_token_clears_what_was_refused()
    {
        var now = DateTime.UtcNow;
        Assert.True(_state.Report(GitHubFeature.Comments, "no", now));
        Assert.False(_state.Report(GitHubFeature.Comments, "no", now));
        Assert.True(_state.Paused(GitHubFeature.Comments, now));
        _state.Clear();
        Assert.False(_state.Paused(GitHubFeature.Comments, now));
        await Task.CompletedTask;
    }

    // ---- The webhook ----

    private static readonly byte[] PushBody = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","repository":{"full_name":"Acme/Web","name":"Web","owner":{"login":"Acme"}}}""");

    private (GitHubWebhookEndpoint Endpoint, WatchState Watch) Endpoint()
    {
        var watch = new WatchState();
        return (new GitHubWebhookEndpoint(_db, _settings, watch, new WebhookThrottle(), NullLogger<GitHubWebhookEndpoint>.Instance), watch);
    }

    [Fact]
    public void A_signature_is_checked_against_the_secret()
    {
        var header = WebhookSignature.Sign("s3cret", PushBody);
        Assert.StartsWith("sha256=", header);
        Assert.True(WebhookSignature.Verify("s3cret", PushBody, header));
        Assert.True(WebhookSignature.Verify("s3cret", PushBody, header.ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal)));
        Assert.False(WebhookSignature.Verify("other", PushBody, header));
        Assert.False(WebhookSignature.Verify("s3cret", [.. PushBody, (byte)' '], header));
        Assert.False(WebhookSignature.Verify("s3cret", PushBody, "sha256=zz"));
        Assert.False(WebhookSignature.Verify("s3cret", PushBody, "sha1=" + header[7..]));
        Assert.False(WebhookSignature.Verify(null, PushBody, header));
        Assert.False(WebhookSignature.Verify("s3cret", PushBody, null));
        Assert.Equal(64, WebhookSignature.NewSecret().Length);
        Assert.NotEqual(WebhookSignature.NewSecret(), WebhookSignature.NewSecret());
    }

    [Fact]
    public void A_delivery_names_its_repository()
    {
        Assert.Equal(("Acme", "Web"), WebhookEvents.RepoOf(PushBody));
        Assert.Equal(("acme", "web"), WebhookEvents.RepoOf(Encoding.UTF8.GetBytes("""{"repository":{"name":"web","owner":{"login":"acme"}}}""")));
        Assert.Null(WebhookEvents.RepoOf(Encoding.UTF8.GetBytes("""{"zen":"Keep it simple."}""")));
        Assert.Null(WebhookEvents.RepoOf(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(WebhookEvents.RepoOf([]));
    }

    [Fact]
    public async Task A_push_to_a_deployed_repo_wakes_the_watcher_for_that_repo()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();

        Assert.Equal(202, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), PushBody, "203.0.113.5"));
        // By the repo as the app has it, whatever case GitHub used.
        Assert.True(watch.TakeWebhookPoke("acme", "web"));
    }

    [Theory]
    [InlineData("pull_request", 202)]
    [InlineData("delete", 202)]
    [InlineData("ping", 200)]
    [InlineData("issues", 204)]
    [InlineData(null, 204)]
    public async Task Each_event_gets_its_answer(string? eventName, int status)
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, _) = Endpoint();
        Assert.Equal(status, await endpoint.HandleAsync(eventName, WebhookSignature.Sign("s3cret", PushBody), PushBody, "203.0.113.5"));
    }

    [Fact]
    public async Task A_push_to_a_repo_gitberth_doesnt_deploy_is_ignored()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();
        var other = Encoding.UTF8.GetBytes("""{"repository":{"full_name":"acme/other"}}""");
        Assert.Equal(204, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", other), other, "203.0.113.5"));
        Assert.False(watch.TakeWebhookPoke("acme", "other"));
    }

    [Fact]
    public async Task Bad_signatures_get_401_and_too_many_get_429_for_that_sender_only()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        endpoint.Now = () => now;
        for (var i = 0; i < WebhookThrottle.Limit; i++)
            Assert.Equal(401, await endpoint.HandleAsync("push", "sha256=00", PushBody, "203.0.113.5"));
        // Blocked without a check, even with the right signature.
        Assert.Equal(429, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), PushBody, "203.0.113.5"));
        Assert.False(watch.TakeWebhookPoke("acme", "web"));
        // GitHub, from another address, still gets through.
        Assert.Equal(202, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), PushBody, "140.82.115.1"));
        // Once the window has passed, the sender is checked again.
        now += WebhookThrottle.Window;
        Assert.Equal(202, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), PushBody, "203.0.113.5"));
    }

    [Fact]
    public async Task Without_a_secret_every_delivery_is_refused()
    {
        var (endpoint, _) = Endpoint();
        Assert.Equal(401, await endpoint.HandleAsync("push", WebhookSignature.Sign("", PushBody), PushBody, "203.0.113.5"));
    }

    // ---- The webhook on each repo ----

    private WebhookKeeper Keeper() => new(_db, _settings, new GitHubWrites(new Factory(_github)), _state, NullLogger<WebhookKeeper>.Instance);

    private async Task WebhookReadyAsync()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "hooks.example.com");
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
    }

    [Fact]
    public async Task The_webhook_is_added_to_each_repo_once()
    {
        await WebhookReadyAsync();
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : null;
        var keeper = Keeper();

        await keeper.EnsureAllAsync();
        await keeper.EnsureAllAsync();

        // The app and its preview share a repo: one webhook, added once.
        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls);
        Assert.Equal("https://hooks.example.com/hooks/github", BodyJson(_github.BodyOf("POST /repos/acme/web/hooks")).GetProperty("config").GetProperty("url").GetString());
        Assert.True(Assert.Single(_state.Hooks).Ok);
    }

    [Fact]
    public async Task A_webhook_from_an_earlier_host_name_is_moved_rather_than_doubled()
    {
        await WebhookReadyAsync();
        _github.Respond = r => r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """[{"id":9,"active":true,"events":["push"],"config":{"url":"https://old-hooks.example.com/hooks/github"}},{"id":3,"config":{"url":"https://ci.example.com/x"}}]""")
            : null;
        await Keeper().EnsureAllAsync();
        Assert.Equal(["GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/9"], _github.Calls);
    }

    [Fact]
    public async Task Nothing_is_set_up_without_domains_a_host_name_or_a_token()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "hooks.example.com");
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        await Keeper().EnsureAllAsync();
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task A_token_without_the_webhooks_permission_is_said_once_and_rests()
    {
        await WebhookReadyAsync();
        _github.Respond = _ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        await keeper.EnsureAllAsync();
        Assert.Single(_github.Calls);
        Assert.Contains("Webhooks: Read and write", Assert.Single(_state.Problems).Text);
        Assert.False(Assert.Single(_state.Hooks).Ok);
    }

    [Fact]
    public void A_webhook_is_gitberths_by_its_url_or_its_path()
    {
        Assert.True(WebhookKeeper.Ours("https://hooks.example.com/hooks/github", "https://hooks.example.com/hooks/github"));
        Assert.True(WebhookKeeper.Ours("https://old.example.com/hooks/github/", "https://hooks.example.com/hooks/github"));
        Assert.False(WebhookKeeper.Ours("https://ci.example.com/github", "https://hooks.example.com/hooks/github"));
        Assert.False(WebhookKeeper.Ours(null, "https://hooks.example.com/hooks/github"));
    }
}
