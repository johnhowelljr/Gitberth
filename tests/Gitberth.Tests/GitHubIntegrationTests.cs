using System.Net;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    public void A_web_port_on_an_address_of_its_own_is_linked_at_that_address()
    {
        var app = new AppDefinition { Ports = [new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true, Access = PortAccess.Custom, HostIp = "10.1.1.90" }] };
        Assert.Equal("http://10.1.1.90:3001/", GitHubReports.Address(app, "192.168.50.10"));
        app.Ports[0].HostIp = "fd00::90";
        Assert.Equal("http://[fd00::90]:3001/", GitHubReports.Address(app, "192.168.50.10"));
        // Inside Docker only, or not published: nothing anyone could open.
        app.Ports[0].HostIp = null;
        Assert.Null(GitHubReports.Address(app, "192.168.50.10"));
        app.Ports[0] = new PortMapping { Id = 1, ContainerPort = 3000, IsWeb = true, Access = PortAccess.Internal };
        Assert.Null(GitHubReports.Address(app, "192.168.50.10"));
        // The LAN host given as an IPv6 address is bracketed too.
        app.Ports[0] = new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true, Access = PortAccess.Lan };
        Assert.Equal("http://[fd00::10]:3001/", GitHubReports.Address(app, "fd00::10"));
    }

    [Theory]
    [InlineData(DeployStatus.Queued, "Building")]
    [InlineData(DeployStatus.Cloning, "Building")]
    [InlineData(DeployStatus.Starting, "Building")]
    [InlineData(DeployStatus.Succeeded, "Ready")]
    [InlineData(DeployStatus.Cancelled, "Cancelled")]
    [InlineData(DeployStatus.Failed, "Failed")]
    [InlineData(DeployStatus.RolledBack, "Failed")]
    public void A_previews_comment_names_its_state(DeployStatus status, string state)
    {
        Assert.Equal(state, GitHubReports.CommentState(status));
        Assert.Contains($"Gitberth preview of Web: {state}**", GitHubReports.CommentBody(new PreviewComment("Web", state, null, Sha, null, DateTime.UtcNow)));
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

        // Each says which webhook it is: the one set, or the one GitHub made.
        Assert.Equal((long?)7, await api.SaveHookAsync("t", "acme", "web", 7, "https://hooks.example.com/hooks/github", "s3cret"));
        Assert.Equal((long?)1, await api.SaveHookAsync("t", "acme", "web", null, "https://hooks.example.com/hooks/github", "s3cret"));
        Assert.Contains("PATCH /repos/acme/web/hooks/7", _github.Calls);
        var body = BodyJson(_github.BodyOf("POST /repos/acme/web/hooks"));
        Assert.Equal(["push", "pull_request", "delete"], body.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("s3cret", body.GetProperty("config").GetProperty("secret").GetString());
        Assert.Equal("json", body.GetProperty("config").GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task Anything_else_github_wont_do_is_said_with_its_reason_and_code()
    {
        var api = new GitHubWrites(new Factory(_github));
        async Task<GitHubWriteException> RefusedAsync() =>
            await Assert.ThrowsAsync<GitHubWriteException>(() => api.SetStatusAsync("t", "acme", "web", Sha, new CommitStatus("pending", "x", "c", null)));

        _github.Respond = _ => Json(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");
        var invalid = await RefusedAsync();
        Assert.Equal("GitHub didn't let Gitberth set a commit status on acme/web: Validation Failed (HTTP 422).", invalid.Message);
        Assert.False(invalid.MissingPermission || invalid.BadToken);

        // An answer that isn't GitHub's JSON (a proxy's page): its reason phrase.
        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>"), ReasonPhrase = "Bad Gateway" };
        Assert.Equal("GitHub didn't let Gitberth set a commit status on acme/web: Bad Gateway (HTTP 502).", (await RefusedAsync()).Message);

        // 401 is the token itself; 404 is a repo the token can't see, so a permission it lacks there.
        _github.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        var token = await RefusedAsync();
        Assert.Equal((true, false, "GitHub says the token is invalid or expired. Update it in Settings."), (token.BadToken, token.MissingPermission, token.Message));
        _github.Respond = _ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        Assert.True((await RefusedAsync()).MissingPermission);

        // GitHub's secondary rate limit (403 with Retry-After) and a 429 are the rate limit, not a permission.
        _github.Respond = _ =>
        {
            var r = Json(HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit"}""");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return r;
        };
        var secondary = await RefusedAsync();
        Assert.False(secondary.MissingPermission);
        Assert.Equal("GitHub's API rate limit for this token is used up, so Gitberth couldn't set a commit status on acme/web. It tries again with the next deploy.", secondary.Message);
        _github.Respond = _ => Json(HttpStatusCode.TooManyRequests, "{}");
        Assert.False((await RefusedAsync()).MissingPermission);
    }

    [Fact]
    public async Task Github_out_of_reach_or_too_slow_is_said_in_plain_words()
    {
        var api = new GitHubWrites(new Factory(_github));
        _github.Respond = _ => throw new HttpRequestException("Connection refused");
        Assert.Equal("api.github.com couldn't be reached (Connection refused).",
            (await Assert.ThrowsAsync<GitHubWriteException>(() => api.ListHooksAsync("t", "acme", "web"))).Message);
        _github.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        Assert.Equal("api.github.com didn't answer in time.",
            (await Assert.ThrowsAsync<GitHubWriteException>(() => api.CreateCommentAsync("t", "acme", "web", 12, "hi"))).Message);
    }

    [Fact]
    public async Task Answers_github_sends_that_cant_be_read_are_said_or_left_out()
    {
        var api = new GitHubWrites(new Factory(_github));
        _github.Respond = _ => Json(HttpStatusCode.OK, """{"message":"not a list"}""");
        Assert.Equal("GitHub's list of acme/web's webhooks couldn't be read.",
            (await Assert.ThrowsAsync<GitHubWriteException>(() => api.ListHooksAsync("t", "acme", "web"))).Message);

        // A comment GitHub made but didn't name can't be edited later: said, so the next deploy makes one again.
        _github.Respond = _ => Json(HttpStatusCode.Created, "not json");
        Assert.Equal("GitHub didn't say which comment it made.",
            (await Assert.ThrowsAsync<GitHubWriteException>(() => api.CreateCommentAsync("t", "acme", "web", 12, "hi"))).Message);
        // A webhook GitHub added but didn't name: added all the same, just not recorded.
        Assert.Null(await api.SaveHookAsync("t", "acme", "web", null, "https://hooks.example.com/hooks/github", "s3cret"));
        // A comment that's gone is told apart from a failure.
        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.Gone);
        Assert.False(await api.UpdateCommentAsync("t", "acme", "web", 555, "hi"));
    }

    // ---- The reporter ----

    [Fact]
    public async Task A_report_github_cant_take_never_fails_the_deploy_and_nothing_rests()
    {
        await WithTokenAsync();
        _github.Respond = _ => throw new HttpRequestException("Connection refused");

        // Neither the start nor the end throws: the deploy goes on whatever GitHub does.
        await _reporter.StartedAsync(Report(_appId, 9, DeployStatus.Building), CancellationToken.None);
        await _reporter.FinishedAsync(Report(_appId, 9, DeployStatus.Succeeded), CancellationToken.None);
        Assert.Equal(2, _github.Calls.Count(c => c == $"POST /repos/acme/web/statuses/{Sha}"));

        // A passing failure isn't a refusal: the next deploy reports as usual.
        Assert.False(_state.Paused(GitHubFeature.Statuses, "acme/web", DateTime.UtcNow));
        _github.Respond = null;
        await _reporter.StartedAsync(Report(_appId, 10, DeployStatus.Building), CancellationToken.None);
        Assert.Equal(3, _github.Calls.Count(c => c == $"POST /repos/acme/web/statuses/{Sha}"));
    }

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
        _state.Now = () => now;

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
    public void A_repos_refusal_is_shown_only_while_it_rests()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        _state.Now = () => now;
        Assert.True(_state.Refused(GitHubFeature.Statuses, "acme/api", "no", now));
        Assert.False(_state.Refused(GitHubFeature.Statuses, "acme/api", "no", now));
        Assert.Equal("acme/api", Assert.Single(_state.Problems).Repo);

        // An hour on (the app deleted, say, or the permission fixed on GitHub), Settings stops warning, and the next
        // deploy asks again; a refusal then is news again.
        now += GitHubIntegrationState.PauseFor;
        Assert.Empty(_state.Problems);
        Assert.False(_state.Paused(GitHubFeature.Statuses, "acme/api", now));
        Assert.True(_state.Refused(GitHubFeature.Statuses, "acme/api", "no", now));
        Assert.Single(_state.Problems);
        // A refused token stays said until a call works or a token is saved.
        _state.Report(GitHubFeature.Comments, "bad token", now);
        now += 2 * GitHubIntegrationState.PauseFor;
        var problem = Assert.Single(_state.Problems);
        Assert.Equal((GitHubFeature.Comments, null), (problem.Feature, problem.Repo));
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
    public async Task A_delivery_sent_as_form_data_is_read_as_github_sends_it()
    {
        // A webhook made by hand with GitHub's default content type: the JSON is the payload field, URL-encoded, and the
        // signature is over the body as sent.
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();
        var form = Encoding.UTF8.GetBytes("payload=" + Uri.EscapeDataString(Encoding.UTF8.GetString(PushBody)).Replace("%20", "+", StringComparison.Ordinal));

        Assert.Equal(("Acme", "Web"), WebhookEvents.RepoOf(form));
        Assert.Equal(202, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", form), form, "203.0.113.5"));
        Assert.True(watch.TakeWebhookPoke("acme", "web"));
        // The signature is still the body's as sent: the JSON alone, signed, doesn't stand in for it.
        Assert.Equal(401, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), form, "203.0.113.5"));
        Assert.Null(WebhookEvents.RepoOf(Encoding.UTF8.GetBytes("other=1")));
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
    public async Task Bad_signatures_get_401_then_429_but_a_signed_delivery_always_gets_through()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        endpoint.Now = () => now;
        // Someone's webhook with the wrong secret, delivered from GitHub's own address.
        for (var i = 0; i < WebhookThrottle.Limit; i++)
            Assert.Equal(401, await endpoint.HandleAsync("push", "sha256=00", PushBody, "140.82.115.1"));
        Assert.Equal(429, await endpoint.HandleAsync("push", "sha256=00", PushBody, "140.82.115.1"));
        // Gitberth's own delivery from that same address is checked first, and gets through.
        Assert.Equal(202, await endpoint.HandleAsync("push", WebhookSignature.Sign("s3cret", PushBody), PushBody, "140.82.115.1"));
        Assert.True(watch.TakeWebhookPoke("acme", "web"));
        Assert.Equal(200, await endpoint.HandleAsync("ping", WebhookSignature.Sign("s3cret", PushBody), PushBody, "140.82.115.1"));
        // Another sender's bad signature is a plain 401; once the window has passed, so is this one's again.
        Assert.Equal(401, await endpoint.HandleAsync("push", "sha256=00", PushBody, "203.0.113.5"));
        now += WebhookThrottle.Window;
        Assert.Equal(401, await endpoint.HandleAsync("push", "sha256=00", PushBody, "140.82.115.1"));
    }

    [Fact]
    public void The_throttle_remembers_a_bounded_number_of_senders()
    {
        var throttle = new WebhookThrottle();
        var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < WebhookThrottle.MaxSenders; i++) throttle.Failed($"2001:db8::{i:x}", start);
        Assert.Equal(WebhookThrottle.MaxSenders, throttle.Count);

        // Full: a new sender isn't counted (only ever a 401 instead of a 429), and the list stops growing.
        for (var i = 0; i < WebhookThrottle.Limit; i++) throttle.Failed("203.0.113.5", start);
        Assert.Equal(WebhookThrottle.MaxSenders, throttle.Count);
        Assert.False(throttle.Blocked("203.0.113.5", start));

        // Once their window has passed, the old ones go and new ones count again.
        throttle.Failed("203.0.113.5", start + WebhookThrottle.Window);
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public void Old_senders_go_at_most_once_a_minute()
    {
        var throttle = new WebhookThrottle();
        var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        throttle.Failed("a", start);
        throttle.Failed("b", start + WebhookThrottle.Window - TimeSpan.FromSeconds(30));
        // a's window has passed, but the last look was 30 seconds ago: a stays for now.
        throttle.Failed("c", start + WebhookThrottle.Window);
        Assert.Equal(3, throttle.Count);
        // A minute on, it goes.
        throttle.Failed("c", start + WebhookThrottle.Window + TimeSpan.FromSeconds(40));
        Assert.Equal(2, throttle.Count);
    }

    [Fact]
    public async Task A_delivery_over_the_size_limit_gets_413_whether_or_not_it_says_its_length()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, watch) = Endpoint();
        DefaultHttpContext Signed(byte[] body)
        {
            var context = Request("POST", body);
            context.Request.Headers["X-GitHub-Event"] = "push";
            context.Request.Headers["X-Hub-Signature-256"] = WebhookSignature.Sign("s3cret", body);
            return context;
        }

        // It says it's too long: refused before a byte is read.
        var said = Signed(PushBody);
        said.Request.ContentLength = GitHubWebhookEndpoint.MaxBody + 1;
        await endpoint.HandleAsync(said);
        Assert.Equal(413, said.Response.StatusCode);
        Assert.Equal(0, said.Request.Body.Position);

        // It doesn't say (sent in chunks), and is too long, signed or not: refused once the limit is passed.
        var padded = new byte[GitHubWebhookEndpoint.MaxBody + 1];
        PushBody.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', PushBody.Length, padded.Length - PushBody.Length);
        var unsaid = Signed(padded);
        await endpoint.HandleAsync(unsaid);
        Assert.Equal(413, unsaid.Response.StatusCode);
        Assert.Equal(0, unsaid.Response.Body.Length);
        Assert.False(watch.TakeWebhookPoke("acme", "web"));

        // Up to the limit is read and judged as usual.
        var full = Signed(padded[..GitHubWebhookEndpoint.MaxBody]);
        await endpoint.HandleAsync(full);
        Assert.Equal(202, full.Response.StatusCode);
        Assert.True(watch.TakeWebhookPoke("acme", "web"));
    }

    [Fact]
    public async Task The_sender_is_cloudflares_visitor_address_when_it_came_through_the_tunnel()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var (endpoint, _) = Endpoint();
        async Task<int> BadAsync(string? visitor)
        {
            var context = Request("POST", PushBody);
            context.Request.Headers["X-GitHub-Event"] = "push";
            context.Request.Headers["X-Hub-Signature-256"] = "sha256=00";
            if (visitor is not null) context.Request.Headers["CF-Connecting-IP"] = visitor;
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
            await endpoint.HandleAsync(context);
            return context.Response.StatusCode;
        }

        // Every delivery reaches Gitberth from cloudflared's address: the throttle counts the visitor Cloudflare names.
        for (var i = 0; i < WebhookThrottle.Limit; i++) Assert.Equal(401, await BadAsync("198.51.100.7"));
        Assert.Equal(429, await BadAsync("198.51.100.7"));
        Assert.Equal(401, await BadAsync("198.51.100.8"));
        // Without the header, the connection's own address counts.
        Assert.Equal(401, await BadAsync(null));
    }

    [Fact]
    public async Task Without_a_secret_every_delivery_is_refused()
    {
        var (endpoint, _) = Endpoint();
        Assert.Equal(401, await endpoint.HandleAsync("push", WebhookSignature.Sign("", PushBody), PushBody, "203.0.113.5"));
    }

    private sealed class BrokenDb : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => throw new InvalidOperationException("The database is locked.");
    }

    // A request as the webhook's host gets it, with Gitberth's status-code pages on, as they are for every other path.
    private static DefaultHttpContext Request(string method, byte[]? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Body = new MemoryStream(body ?? []);
        context.Response.Body = new MemoryStream();
        context.Features.Set<IStatusCodePagesFeature>(new StatusCodePagesFeature());
        return context;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    public async Task Anything_but_post_gets_a_bare_405_and_never_gitberths_pages(string method)
    {
        var (endpoint, _) = Endpoint();
        var context = Request(method);

        await endpoint.HandleAsync(context);

        Assert.Equal(405, context.Response.StatusCode);
        Assert.Equal("POST", context.Response.Headers.Allow.ToString());
        Assert.False(context.Features.Get<IStatusCodePagesFeature>()!.Enabled);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task A_failure_while_handling_a_delivery_gets_a_bare_500()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubWebhookSecret, "s3cret");
        var endpoint = new GitHubWebhookEndpoint(new BrokenDb(), _settings, new WatchState(), new WebhookThrottle(), NullLogger<GitHubWebhookEndpoint>.Instance);
        var context = Request("POST", PushBody);
        context.Request.Headers["X-GitHub-Event"] = "push";
        context.Request.Headers["X-Hub-Signature-256"] = WebhookSignature.Sign("s3cret", PushBody);

        await endpoint.HandleAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.False(context.Features.Get<IStatusCodePagesFeature>()!.Enabled);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task The_webhook_path_is_mapped_for_every_method_anonymous_and_without_antiforgery()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        GitHubWebhookEndpoint.Map(app);

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == GitHubWebhookEndpoint.Path);
        // No method of its own: routing never answers 405 for it, so status-code pages never turn one into a page.
        Assert.Null(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>());
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.False(endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
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
    public async Task A_webhook_github_couldnt_save_for_a_moment_is_shown_and_tried_again_on_the_next_run()
    {
        await WebhookReadyAsync();
        var failing = true;
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]")
            : failing ? Json(HttpStatusCode.BadGateway, """{"message":"Server Error"}""") : Json(HttpStatusCode.Created, """{"id":4}""");
        var keeper = Keeper();

        await keeper.EnsureAllAsync();

        var shown = Assert.Single(_state.Hooks);
        Assert.Equal((false, "GitHub didn't let Gitberth add a webhook to acme/web: Server Error (HTTP 502)."), (shown.Ok, shown.Text));
        Assert.Empty(await RecordedHooksAsync());
        // Not a refusal: the repo doesn't rest, and the next run tries again.
        Assert.False(_state.Paused(GitHubFeature.Webhooks, "acme/web", DateTime.UtcNow));
        failing = false;
        await keeper.EnsureAllAsync();
        Assert.Equal((true, "Webhook added"), (Assert.Single(_state.Hooks).Ok, _state.Hooks[0].Text));
        Assert.Equal(4, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public void Where_cloudflared_reaches_gitberth_for_the_webhook()
    {
        var onHost = new TunnelInfo("tunnel", "account", "tunnel-id", HostNetwork: true, OnAppNetwork: false);
        var onShared = new TunnelInfo("tunnel", "account", "tunnel-id", HostNetwork: false, OnAppNetwork: true);
        string Inspect(string mode, string bindings) =>
            "[{\"Name\":\"/gitberth\",\"HostConfig\":{\"NetworkMode\":\"" + mode + "\"},\"NetworkSettings\":{\"Ports\":{\"8080/tcp\":" + bindings + "}}}]";

        // Gitberth on the host's own network: its own port on localhost.
        Assert.Equal("http://localhost:8080", DomainService.OwnService(Inspect("host", "null"), 8080, onHost));
        // Published on every address, or IPv6's: localhost and the published port.
        Assert.Equal("http://localhost:18080", DomainService.OwnService(Inspect("bridge", """[{"HostIp":"::","HostPort":"18080"}]"""), 8080, onHost));
        // Published on one LAN address only: that address.
        Assert.Equal("http://10.1.1.80:18080", DomainService.OwnService(Inspect("bridge", """[{"HostIp":"10.1.1.80","HostPort":"18080"}]"""), 8080, onHost));
        // Only on an IPv6 address, or with no usable port: cloudflared can't be pointed at it.
        Assert.Null(DomainService.OwnService(Inspect("bridge", """[{"HostIp":"fd00::80","HostPort":"18080"}]"""), 8080, onHost));
        Assert.Null(DomainService.OwnService(Inspect("bridge", """[{"HostIp":"","HostPort":""}]"""), 8080, onHost));
        // A container without a name on the shared network, or an answer that isn't Docker's: nothing.
        Assert.Null(DomainService.OwnService("""[{"HostConfig":{}}]""", 8080, onShared));
        Assert.Null(DomainService.OwnService("[]", 8080, onShared));
        Assert.Null(DomainService.OwnService("not json", 8080, onHost));
    }

    [Fact]
    public async Task The_webhook_gitberth_added_follows_a_new_host_name_rather_than_being_doubled()
    {
        await WebhookReadyAsync();
        var hooks = "[]";
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, """{"id":9}""") : null;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);

        // The host name moves: the webhook Gitberth added, still on the old address, is set to the new one.
        hooks = """[{"id":9,"active":true,"events":["push"],"config":{"url":"https://hooks.example.com/hooks/github"}},{"id":3,"config":{"url":"https://ci.example.com/x"}}]""";
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/9"], _github.Calls.TakeLast(2).ToList());
        Assert.Equal("https://gh.example.com/hooks/github", BodyJson(_github.BodyOf("PATCH /repos/acme/web/hooks/9")).GetProperty("config").GetProperty("url").GetString());
        Assert.Equal(new WebhookKeeper.KeptHook(9, "https://gh.example.com/hooks/github"), (await KeptHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task A_webhook_github_wouldnt_let_gitberth_move_to_a_new_host_name_is_left_where_it_is()
    {
        await WebhookReadyAsync();
        var (hooks, added) = ("[]", """{"id":9}""");
        Func<HttpRequestMessage, HttpResponseMessage?> github = r =>
            r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, added) : null;
        _github.Respond = github;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);

        // The token has expired when the host name moves: GitHub refuses it, so the webhook stays on the old name, and
        // Gitberth forgets it was its own.
        _github.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());

        // Another Gitberth takes hooks.example.com and that webhook. Once a working token is saved here, this one adds its
        // own beside it rather than move the other's.
        (hooks, added) = ("""[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""", """{"id":21}""");
        _github.Respond = github;
        _state.Clear();
        var calls = _github.Calls.Count;
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls.Skip(calls).ToList());
        Assert.DoesNotContain("PATCH /repos/acme/web/hooks/9", _github.Calls);
        Assert.Equal(new WebhookKeeper.KeptHook(21, "https://gh.example.com/hooks/github"), (await KeptHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task Without_a_token_a_webhook_isnt_kept_for_a_later_move()
    {
        await WebhookReadyAsync();
        var (hooks, added) = ("[]", """{"id":9}""");
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, added) : null;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        (hooks, added) = ("[]", """{"id":12}""");
        await AddAppAsync("api");
        await keeper.EnsureAllAsync();
        Assert.Equal([12, 9], (await RecordedHooksAsync()).OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Value).ToList());

        // The token is taken away. A repo no app deploys any more is forgotten all the same, and so is one whose webhook
        // the host name moved away from: nothing could move it.
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, null);
        await using (var db = _db.CreateDbContext())
        {
            db.Apps.RemoveRange(db.Apps.Where(a => a.Repo == "api"));
            await db.SaveChangesAsync();
        }
        var calls = _github.Calls.Count;
        await keeper.EnsureAllAsync();
        Assert.Equal(["acme/web"], (await RecordedHooksAsync()).Keys.ToList());
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());
        Assert.Equal(calls, _github.Calls.Count);

        // Another Gitberth takes hooks.example.com and that webhook; a token is saved here again: this one adds its own.
        (hooks, added) = ("""[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""", """{"id":21}""");
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_new");
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls.Skip(calls).ToList());
        Assert.Equal(21, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task A_repo_resting_when_the_host_name_moves_keeps_its_webhook_where_it_is()
    {
        await WebhookReadyAsync();
        var (hooks, added) = ("[]", """{"id":9}""");
        Func<HttpRequestMessage, HttpResponseMessage?> github = r =>
            r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, added) : null;
        _github.Respond = github;
        await Keeper().EnsureAllAsync();

        // After a restart GitHub refuses the repo, so it rests for an hour; the host name moves meanwhile.
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var keeper = Keeper();
        keeper.Now = () => now;
        _github.Respond = _ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        await keeper.EnsureAllAsync();
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());

        // Another Gitberth takes hooks.example.com and that webhook; after the hour, this one adds its own.
        (hooks, added) = ("""[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""", """{"id":21}""");
        _github.Respond = github;
        now += GitHubIntegrationState.PauseFor;
        var calls = _github.Calls.Count;
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls.Skip(calls).ToList());
        Assert.Equal(21, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task A_webhook_on_an_earlier_host_name_that_gitberth_never_recorded_is_left_alone()
    {
        await WebhookReadyAsync();
        // This Gitberth's webhooks moved from hooks.example.com to gh.example.com. Another Gitberth has taken
        // hooks.example.com since, and put its webhook on acme/web, which this one only starts deploying now.
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHostsBefore, "hooks.example.com");
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""")
            : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, """{"id":21}""") : null;

        await Keeper().EnsureAllAsync();

        // The other one's stays as it is; this one adds its own beside it, and remembers that one.
        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls);
        Assert.Equal(21, Assert.Single(await RecordedHooksAsync(), h => h.Key == "acme/web").Value);
    }

    [Fact]
    public async Task A_webhook_added_before_ids_were_recorded_is_found_by_its_url_once_and_recorded()
    {
        await WebhookReadyAsync();
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """[{"id":11,"config":{"url":"https://hooks.example.com/hooks/github"}}]""") : null;

        await Keeper().EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/11"], _github.Calls);
        Assert.Equal(11, (await RecordedHooksAsync())["acme/web"]);
    }

    private async Task<Dictionary<string, WebhookKeeper.KeptHook>> KeptHooksAsync() => WebhookKeeper.KeptHooks(await _settings.GetAsync(SettingKeys.GitHubWebhookIds));

    /// <summary>The webhook IDs recorded, by repo.</summary>
    private async Task<Dictionary<string, long>> RecordedHooksAsync() => (await KeptHooksAsync()).ToDictionary(k => k.Key, k => k.Value.Id);

    /// <summary>Deletes the app and its preview: no app deploys acme/web any more.</summary>
    private async Task DeleteAppsAsync()
    {
        await using var db = _db.CreateDbContext();
        db.Apps.RemoveRange(db.Apps);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_webhook_is_forgotten_once_no_app_deploys_its_repo_so_another_gitberths_is_never_taken_back()
    {
        await WebhookReadyAsync();
        var (hooks, added) = ("[]", """{"id":9}""");
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, added) : null;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);

        // The apps are deleted: the webhook stays on the repo, no longer Gitberth's to move. GitHub isn't asked anything.
        await DeleteAppsAsync();
        var calls = _github.Calls.Count;
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());
        Assert.Equal(calls, _github.Calls.Count);

        // This Gitberth moves to gh.example.com, and another takes hooks.example.com and that webhook. Deployed here again,
        // acme/web gets a webhook of its own beside the other one's.
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        (hooks, added) = ("""[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""", """{"id":21}""");
        await AddAppAsync("web");
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls.Skip(calls).ToList());
        Assert.Equal(21, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task A_repo_deployed_again_takes_its_webhook_back_by_its_payload_url()
    {
        await WebhookReadyAsync();
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""") : null;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        await DeleteAppsAsync();
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());

        // Deployed again under the same host name while Gitberth runs: the webhook is found by its payload URL and recorded.
        await AddAppAsync("web");
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/9", "GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/9"], _github.Calls);
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task Turning_instant_deploys_off_forgets_which_webhooks_were_gitberths()
    {
        await WebhookReadyAsync();
        var (hooks, added) = ("[]", """{"id":9}""");
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, hooks) : r.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, added) : null;
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        Assert.Equal(9, (await RecordedHooksAsync())["acme/web"]);

        // Turned off: the webhook stays on the repo, and Gitberth forgets it was its own, without asking GitHub anything.
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, null);
        var calls = _github.Calls.Count;
        await keeper.EnsureAllAsync();
        Assert.Empty(await RecordedHooksAsync());
        Assert.Equal(calls, _github.Calls.Count);

        // Another Gitberth took hooks.example.com and that webhook meanwhile. Turned on again with gh.example.com, this one
        // adds its own rather than move the other's.
        (hooks, added) = ("""[{"id":9,"config":{"url":"https://hooks.example.com/hooks/github"}}]""", """{"id":21}""");
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls.Skip(calls).ToList());
        Assert.Equal(21, (await RecordedHooksAsync())["acme/web"]);
    }

    [Fact]
    public async Task Another_gitberths_webhook_on_the_same_path_is_never_taken_over()
    {
        await WebhookReadyAsync();
        // A dev Gitberth's webhook on a host name this one never had, ahead of this one's own.
        _github.Respond = r => r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """[{"id":9,"config":{"url":"https://dev-hooks.example.com/hooks/github"}},{"id":11,"config":{"url":"https://hooks.example.com/hooks/github"}}]""")
            : null;
        await Keeper().EnsureAllAsync();
        Assert.Equal(["GET /repos/acme/web/hooks", "PATCH /repos/acme/web/hooks/11"], _github.Calls);

        // With only the other one there, this one adds its own beside it.
        _github.Respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """[{"id":9,"config":{"url":"https://dev-hooks.example.com/hooks/github"}}]""") : null;
        await _settings.SetAsync(SettingKeys.GitHubWebhookHost, "gh.example.com");
        await Keeper().EnsureAllAsync();
        Assert.Equal("POST /repos/acme/web/hooks", _github.Calls[^1]);
        Assert.DoesNotContain("PATCH /repos/acme/web/hooks/9", _github.Calls);
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
    public async Task A_token_without_the_webhooks_permission_says_so_for_the_repo_which_rests()
    {
        await WebhookReadyAsync();
        _github.Respond = _ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var keeper = Keeper();
        await keeper.EnsureAllAsync();
        await keeper.EnsureAllAsync();
        Assert.Single(_github.Calls);
        var hook = Assert.Single(_state.Hooks);
        Assert.False(hook.Ok);
        Assert.Contains("Webhooks: Read and write", hook.Text);
        // Said in the repo's line, not as a refusal of the whole token.
        Assert.Empty(_state.Problems);
    }

    private async Task<int> AddAppAsync(string repo)
    {
        await using var db = _db.CreateDbContext();
        var app = new AppDefinition { Name = repo, Slug = repo, Owner = "acme", Repo = repo, CurrentImage = $"gitberth/{repo}:1" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    [Fact]
    public async Task A_repo_github_refuses_rests_on_its_own_and_the_others_still_get_the_webhook()
    {
        await WebhookReadyAsync();
        // acme/api comes first, and the token can't see it.
        await AddAppAsync("api");
        _github.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/repos/acme/api/", StringComparison.Ordinal) ? Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
            : r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : null;
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var keeper = Keeper();
        keeper.Now = () => now;

        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/api/hooks", "GET /repos/acme/web/hooks", "POST /repos/acme/web/hooks"], _github.Calls);
        Assert.False(_state.Hooks.Single(h => h.Repo == "acme/api").Ok);
        Assert.True(_state.Hooks.Single(h => h.Repo == "acme/web").Ok);
        Assert.Empty(_state.Problems);
        // Only acme/api rests, for an hour.
        await keeper.EnsureAllAsync();
        Assert.Equal(3, _github.Calls.Count);
        now += GitHubIntegrationState.PauseFor;
        await keeper.EnsureAllAsync();
        Assert.Equal("GET /repos/acme/api/hooks", _github.Calls[^1]);
    }

    [Fact]
    public async Task A_token_github_refuses_stops_every_repo()
    {
        await WebhookReadyAsync();
        await AddAppAsync("api");
        _github.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        var keeper = Keeper();

        await keeper.EnsureAllAsync();
        await keeper.EnsureAllAsync();

        Assert.Equal(["GET /repos/acme/api/hooks"], _github.Calls);
        var problem = Assert.Single(_state.Problems);
        Assert.Equal((GitHubFeature.Webhooks, null), (problem.Feature, problem.Repo));
        Assert.Contains("invalid or expired", problem.Text);
    }

    [Fact]
    public async Task Statuses_github_refuses_on_one_repo_still_go_out_for_the_others()
    {
        await WithTokenAsync();
        var api = await AddAppAsync("api");
        _github.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/repos/acme/api/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}""") : null;

        await _reporter.StartedAsync(Report(api, 21, DeployStatus.Building), CancellationToken.None);
        await _reporter.StartedAsync(Report(_appId, 22, DeployStatus.Building), CancellationToken.None);
        await _reporter.StartedAsync(Report(api, 23, DeployStatus.Building), CancellationToken.None);

        Assert.Equal([$"POST /repos/acme/api/statuses/{Sha}", $"POST /repos/acme/web/statuses/{Sha}"], _github.Calls);
        var problem = Assert.Single(_state.Problems);
        Assert.Equal((GitHubFeature.Statuses, "acme/api"), (problem.Feature, problem.Repo));
        Assert.Contains("Gitberth stops trying on this repo", problem.Text);

        // A token GitHub refuses rests every repo.
        _github.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        await _reporter.StartedAsync(Report(_appId, 24, DeployStatus.Building), CancellationToken.None);
        await _reporter.StartedAsync(Report(_appId, 25, DeployStatus.Building), CancellationToken.None);
        Assert.Equal(3, _github.Calls.Count);
        Assert.Contains(_state.Problems, p => p is { Feature: GitHubFeature.Statuses, Repo: null });
    }

    [Fact]
    public void A_webhook_is_gitberths_only_by_the_id_it_recorded_or_its_url()
    {
        const string url = "https://hooks.example.com/hooks/github";
        const string old = "https://old.example.com/hooks/github";
        static RepoHook Hook(long id, string? hookUrl) => new(id, hookUrl, true, ["push"]);
        static WebhookKeeper.KeptHook Kept(long id) => new(id, old);
        // Someone else's on the same path, another Gitberth's say, is never taken.
        Assert.Null(WebhookKeeper.Ours([Hook(1, "https://dev-hooks.example.com/hooks/github")], url, null));
        Assert.Null(WebhookKeeper.Ours([Hook(2, "https://ci.example.com/github"), Hook(3, null)], url, null));
        Assert.Equal(4, WebhookKeeper.Ours([Hook(1, "https://dev-hooks.example.com/hooks/github"), Hook(4, url + "/")], url, null)?.Id);
        // The one Gitberth recorded is its own where Gitberth last set it, or on its payload URL, and comes first; one it
        // didn't record isn't, wherever.
        Assert.Equal(5, WebhookKeeper.Ours([Hook(5, old)], url, Kept(5))?.Id);
        Assert.Equal(5, WebhookKeeper.Ours([Hook(5, url)], url, Kept(5))?.Id);
        Assert.Equal(6, WebhookKeeper.Ours([Hook(6, old), Hook(7, url)], url, Kept(6))?.Id);
        Assert.Null(WebhookKeeper.Ours([Hook(8, old)], url, Kept(5)));
        // Pointed somewhere else since, by someone else, it isn't Gitberth's any more.
        Assert.Null(WebhookKeeper.Ours([Hook(5, "https://ci.example.com/github")], url, Kept(5)));
        // What's recorded is by repo, whatever its case; an unreadable record, or one without its URL, counts as none.
        var saved = JsonSerializer.Serialize(new Dictionary<string, WebhookKeeper.KeptHook> { ["acme/web"] = new(9, url) });
        Assert.Equal(new WebhookKeeper.KeptHook(9, url), WebhookKeeper.KeptHooks(saved)["Acme/Web"]);
        Assert.Empty(WebhookKeeper.KeptHooks("""{"acme/web":{"Id":9}}"""));
        Assert.Empty(WebhookKeeper.KeptHooks("""{"acme/web":9}"""));
        Assert.Empty(WebhookKeeper.KeptHooks("not json"));
        Assert.Empty(WebhookKeeper.KeptHooks(null));
    }
}
