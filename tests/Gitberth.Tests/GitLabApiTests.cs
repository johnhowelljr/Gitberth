using System.Net;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>A stand-in for GitLab's REST API: every request recorded, every answer the test's (an empty list by default).</summary>
internal sealed class GitLabStub : HttpMessageHandler
{
    private readonly List<(string Call, string Body, HttpRequestMessage Request)> _calls = [];

    /// <summary>GitLab's answers; anything it returns null for gets a 201 with {"id":1}, and any GET an empty list.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>"GET /api/v4/projects/acme%2Fweb/merge_requests?state=opened&amp;per_page=100&amp;page=1", as asked.</summary>
    public List<string> Calls
    {
        get { lock (_calls) return [.. _calls.Select(c => c.Call)]; }
    }

    public List<HttpRequestMessage> Requests
    {
        get { lock (_calls) return [.. _calls.Select(c => c.Request)]; }
    }

    public string BodyOf(string call)
    {
        lock (_calls) return _calls.Last(c => c.Call == call).Body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_calls) _calls.Add(($"{request.Method} {request.RequestUri!.PathAndQuery}", body, request));
        return Respond?.Invoke(request) ?? (request.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.Created, """{"id":1}"""));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>
/// GitLab's API as Gitberth calls it: the contract's calls only, the token as PRIVATE-TOKEN (none for a public project's
/// reads), lists page after page by X-Next-Page, GitLab's refusals in plain words, and its rate limit (429 with
/// Retry-After) said with when to try again, never pushed through. Nothing here reaches GitLab.
/// </summary>
public sealed class GitLabApiTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SettingsService _settings;
    private readonly GitLabStub _gitlab = new();
    private readonly GitLabApi _api;
    private static readonly GitLabRepoRef Web = new("acme/platform", "web");
    private const string WebId = "acme%2Fplatform%2Fweb";

    public GitLabApiTests()
    {
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _api = new GitLabApi(new GitLabStub.Factory(_gitlab), _settings);
    }

    public void Dispose() => _connection.Dispose();

    private static string MergeRequests(int from, int count, int? fromProject = null) => "[" + string.Join(",", Enumerable.Range(from, count).Select(n =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["iid"] = n, ["title"] = $"MR {n}", ["source_branch"] = $"b{n}", ["target_branch"] = "main", ["source_project_id"] = fromProject ?? 5, ["target_project_id"] = 5,
            ["sha"] = n.ToString("D40", System.Globalization.CultureInfo.InvariantCulture), ["web_url"] = $"https://gitlab.com/acme/platform/web/-/merge_requests/{n}",
            ["draft"] = false, ["author"] = new System.Text.Json.Nodes.JsonObject { ["username"] = "ada" },
        }.ToJsonString())) + "]";

    private static HttpResponseMessage Page(string json, string? next)
    {
        var answer = GitLabStub.Json(HttpStatusCode.OK, json);
        answer.Headers.Add("X-Next-Page", next ?? "");
        return answer;
    }

    private async Task<GitLabApiException> RefusedAsync() => await Assert.ThrowsAsync<GitLabApiException>(() => _api.ListOpenMergeRequestsAsync(Web));

    // ---- Reads ----

    [Fact]
    public async Task Merge_requests_are_read_page_after_page_while_gitlab_names_a_next_one()
    {
        _gitlab.Respond = r => r.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal) ? Page(MergeRequests(1, 100), "2")
            : r.RequestUri.Query.EndsWith("page=2", StringComparison.Ordinal) ? Page(MergeRequests(101, 2, fromProject: 9), "") : null;

        var open = await _api.ListOpenMergeRequestsAsync(Web);

        Assert.Equal(102, open.Count);
        Assert.Equal([$"GET /api/v4/projects/{WebId}/merge_requests?state=opened&per_page=100&page=1", $"GET /api/v4/projects/{WebId}/merge_requests?state=opened&per_page=100&page=2"],
            _gitlab.Calls);
        var first = open[0];
        Assert.Equal((1, "MR 1", "b1", false, "https://gitlab.com/acme/platform/web/-/merge_requests/1"), (first.Number, first.Title, first.HeadRef, first.FromFork, first.Url));
        // Another source project is someone else's code: a fork.
        Assert.True(open[^1].FromFork);
        // Without a token, nothing is sent for it: a public project answers anyway.
        Assert.All(_gitlab.Requests, r => Assert.False(r.Headers.Contains("PRIVATE-TOKEN")));
    }

    [Fact]
    public async Task The_token_goes_as_private_token_to_settings_gitlab()
    {
        await _settings.SetAsync(SettingKeys.GitLabUrl, "https://gitlab.example.com/gitlab");
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-example");

        await _api.ListOpenMergeRequestsAsync(Web);

        var asked = Assert.Single(_gitlab.Requests);
        Assert.Equal($"https://gitlab.example.com/gitlab/api/v4/projects/{WebId}/merge_requests?state=opened&per_page=100&page=1", asked.RequestUri!.AbsoluteUri);
        Assert.Equal("glpat-example", asked.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Null(asked.Headers.Authorization);
    }

    [Fact]
    public async Task A_list_stops_at_its_cap_whatever_gitlab_says_next()
    {
        _gitlab.Respond = _ => Page(MergeRequests(1, 1), "2");

        await _api.ListOpenMergeRequestsAsync(Web);

        Assert.Equal(GitLabApi.MaxPages, _gitlab.Calls.Count);
    }

    [Fact]
    public async Task What_gitlab_refuses_is_said_in_plain_words_with_and_without_a_token()
    {
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.NotFound, """{"message":"404 Project Not Found"}""");
        Assert.Equal("GitLab can't list acme/platform/web's merge requests without a token. If the project is private, or this needs a sign-in, add a token in Settings › GitLab.",
            (await RefusedAsync()).Message);

        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-example");
        Assert.Equal("GitLab can't list acme/platform/web's merge requests: the project isn't there, or the token's user can't see it. Check the URL, and that the user is a member of the project.",
            (await RefusedAsync()).Message);
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        Assert.Equal("GitLab says the token is invalid, expired or revoked. Update it in Settings › GitLab.", (await RefusedAsync()).Message);
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Forbidden, """{"error":"insufficient_scope"}""");
        var forbidden = await RefusedAsync();
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.StartsWith("The GitLab token isn't allowed to list acme/platform/web's merge requests: it needs the read_api (or api) scope", forbidden.Message);
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.BadGateway, """{"error":"bad gateway"}""");
        Assert.Equal("GitLab answered 502 when Gitberth tried to list acme/platform/web's merge requests: bad gateway", (await RefusedAsync()).Message);
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.OK, """{"not":"a list"}""");
        Assert.Equal("GitLab's answer couldn't be read when Gitberth tried to list acme/platform/web's merge requests.", (await RefusedAsync()).Message);
    }

    [Fact]
    public async Task Gitlabs_rate_limit_says_when_to_try_again_and_isnt_pushed_through()
    {
        // Rack::Attack's answer, as GitLab gives it: plain text, never JSON.
        _gitlab.Respond = _ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("Retry later\n", Encoding.UTF8, "text/plain") };
            limited.Headers.Add("Retry-After", "42");
            limited.Headers.Add("RateLimit-Remaining", "0");
            limited.Headers.Add("RateLimit-Reset", "1795000000");
            return limited;
        };

        var refused = await RefusedAsync();

        Assert.True(refused.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(42), refused.RetryAfter);
        Assert.Equal("GitLab's API rate limit for this token is used up for the moment, so Gitberth couldn't list acme/platform/web's merge requests. GitLab asks to wait 42 seconds; Gitberth asks again after that.",
            refused.Message);
        // Asked once: the watcher asks again later, as with GitHub.
        Assert.Single(_gitlab.Calls);
    }

    [Fact]
    public void Retry_after_is_read_as_seconds_a_date_or_the_reset_time()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.Add("Retry-After", "120");
        Assert.Equal(TimeSpan.FromSeconds(120), GitLabApi.RetryAfterOf(seconds, now));
        var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.Add("Retry-After", now.AddMinutes(5).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromMinutes(5), GitLabApi.RetryAfterOf(date, now));
        var reset = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        reset.Headers.Add("RateLimit-Reset", now.AddSeconds(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromSeconds(30), GitLabApi.RetryAfterOf(reset, now));
        Assert.Null(GitLabApi.RetryAfterOf(new HttpResponseMessage(HttpStatusCode.TooManyRequests), now));
        Assert.EndsWith("GitLab asks to wait 3 minutes; Gitberth asks again after that.", GitLabApi.RateLimitText(TimeSpan.FromSeconds(150), "x"));
        Assert.EndsWith("Gitberth asks again later.", GitLabApi.RateLimitText(null, "x"));
    }

    [Fact]
    public async Task Gitlab_out_of_reach_or_too_slow_is_said_so()
    {
        _gitlab.Respond = _ => throw new HttpRequestException("Connection refused");
        Assert.Equal("gitlab.com couldn't be reached (Connection refused).", (await RefusedAsync()).Message);
        _gitlab.Respond = _ => throw new TaskCanceledException("timeout");
        Assert.Equal("gitlab.com didn't answer in time.", (await RefusedAsync()).Message);

        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _api.ListOpenMergeRequestsAsync(Web, stopping.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"iid":12,"state":"merged"}""", true)]
    [InlineData(HttpStatusCode.OK, """{"iid":12,"state":"closed"}""", false)]
    [InlineData(HttpStatusCode.OK, """{"iid":12,"state":"opened"}""", null)]
    [InlineData(HttpStatusCode.OK, "not json", null)]
    [InlineData(HttpStatusCode.NotFound, """{"message":"404 Not found"}""", null)]
    public async Task Whether_a_merge_request_was_merged_or_null_when_gitlab_cant_say(HttpStatusCode status, string body, bool? merged)
    {
        _gitlab.Respond = _ => GitLabStub.Json(status, body);

        Assert.Equal(merged, await _api.MergedAsync(Web, 12));
        Assert.Equal($"GET /api/v4/projects/{WebId}/merge_requests/12", Assert.Single(_gitlab.Calls));
    }

    [Fact]
    public async Task A_compare_lists_each_diffs_old_and_new_path_and_says_when_gitlab_gave_up()
    {
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.OK,
            """{"commit":{},"diffs":[{"old_path":"docs/a.md","new_path":"docs/b.md"},{"old_path":"src/x.cs","new_path":"src/x.cs"}],"compare_timeout":false}""");

        var compared = await _api.CompareAsync(Web, "1111111", "2222222");

        Assert.Equal(["docs/a.md", "docs/b.md", "src/x.cs"], compared.Paths);
        Assert.False(compared.TimedOut);
        Assert.Equal($"GET /api/v4/projects/{WebId}/repository/compare?from=1111111&to=2222222&straight=true", Assert.Single(_gitlab.Calls));

        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.OK, """{"diffs":[],"compare_timeout":true}""");
        Assert.True((await _api.CompareAsync(Web, "1111111", "2222222")).TimedOut);
    }

    // ---- The token ----

    [Fact]
    public async Task A_token_check_says_who_it_is_its_scopes_and_what_it_lacks()
    {
        _gitlab.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/v4/user" => GitLabStub.Json(HttpStatusCode.OK, """{"id":7,"username":"ada"}"""),
            "/api/v4/personal_access_tokens/self" => GitLabStub.Json(HttpStatusCode.OK, """{"scopes":["read_api","read_repository"],"expires_at":"2027-01-31"}"""),
            _ => null,
        };

        var check = await _api.CheckTokenAsync("https://gitlab.com", "glpat-x");

        Assert.Equal((true, "ada"), (check.Valid, check.Username));
        Assert.Equal(["read_api", "read_repository"], check.Scopes);
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero), check.ExpiresAt);
        Assert.Equal([(GitLabScopes.Api, GitLabScopes.ApiFor)], check.Missing);
        Assert.All(_gitlab.Requests, r => Assert.Equal("glpat-x", r.Headers.GetValues("PRIVATE-TOKEN").Single()));
    }

    [Theory]
    [InlineData(new[] { "api" }, new string[0])]
    [InlineData(new[] { "read_api" }, new[] { "api", "read_repository" })]
    [InlineData(new[] { "read_repository" }, new[] { "api", "read_api" })]
    [InlineData(new[] { "read_user" }, new[] { "api", "read_api", "read_repository" })]
    [InlineData(new[] { "read_api", "write_repository" }, new[] { "api" })]
    public void Each_missing_scope_says_what_its_for(string[] scopes, string[] missing) =>
        Assert.Equal(missing, GitLabScopes.Missing(scopes).Select(m => m.Scope));

    [Fact]
    public async Task A_refused_token_isnt_valid_and_an_unknown_kind_still_is()
    {
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var refused = await _api.CheckTokenAsync("https://gitlab.com", "glpat-bad");
        Assert.False(refused.Valid);
        Assert.Equal("GitLab says this token is invalid, expired or revoked.", refused.Warning);

        // A project or group token, or an older GitLab: who it is, but not its scopes.
        _gitlab.Respond = r => r.RequestUri!.AbsolutePath == "/api/v4/user" ? GitLabStub.Json(HttpStatusCode.OK, """{"username":"project_5_bot"}""")
            : GitLabStub.Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}""");
        var bot = await _api.CheckTokenAsync("https://gitlab.com", "glpat-bot");
        Assert.Equal((true, "project_5_bot", (IReadOnlyList<string>?)null), (bot.Valid, bot.Username, bot.Scopes));
        Assert.Empty(bot.Missing);
        Assert.Contains("Gitberth needs api, read_api and read_repository", bot.Warning);

        // Something that isn't GitLab at that address.
        _gitlab.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") };
        var html = await _api.CheckTokenAsync("https://example.com", "glpat-x");
        Assert.Equal("example.com didn't answer like GitLab when asked who the token is. Check the address.", html.Warning);
    }

    // ---- Writes ----

    [Fact]
    public async Task A_status_is_posted_with_gitlabs_state_and_names()
    {
        var conn = new GitLabConnection("https://gitlab.com", "glpat-x");

        await _api.SetStatusAsync(conn, Web, "abc1234", "running", "Gitberth: Web", "Building and deploying on Gitberth", "http://ui/apps/1/deployments/2", "main");

        Assert.Equal($"POST /api/v4/projects/{WebId}/statuses/abc1234", Assert.Single(_gitlab.Calls));
        var body = JsonDocument.Parse(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/statuses/abc1234")).RootElement;
        Assert.Equal(("running", "Gitberth: Web", "Building and deploying on Gitberth", "http://ui/apps/1/deployments/2", "main"),
            (body.GetProperty("state").GetString(), body.GetProperty("name").GetString(), body.GetProperty("description").GetString(),
                body.GetProperty("target_url").GetString(), body.GetProperty("ref").GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.Forbidden, false, true)]
    [InlineData(HttpStatusCode.NotFound, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, false)]
    [InlineData(HttpStatusCode.BadRequest, false, false)]
    public async Task A_refused_write_says_whether_its_the_token_or_this_project(HttpStatusCode status, bool badToken, bool missingPermission)
    {
        _gitlab.Respond = _ => GitLabStub.Json(status, """{"message":"400 Bad request - state does not have a valid value"}""");

        var ex = await Assert.ThrowsAsync<GitLabWriteException>(() =>
            _api.SetStatusAsync(new GitLabConnection("https://gitlab.com", "glpat-x"), Web, "abc1234", "running", "Gitberth: Web", "x", null, null));

        Assert.Equal((badToken, missingPermission), (ex.BadToken, ex.MissingPermission));
        if (missingPermission) Assert.Contains("the api scope, and at least the Developer role", ex.Message);
        if (status == HttpStatusCode.BadRequest) Assert.Contains("state does not have a valid value", ex.Message);
    }

    [Fact]
    public async Task A_status_gitlab_has_in_that_state_already_is_done_not_a_failure()
    {
        // GitLab's state machine: running to running isn't a move it makes (Gitberth restarted during a build, and the retry
        // says it builds again), and it answers 400.
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.BadRequest,
            """{"message":"Cannot transition status via :run from :running (Reason(s): Status cannot transition via \"run\")"}""");

        var posted = await _api.SetStatusAsync(new GitLabConnection("https://gitlab.com", "glpat-x"), Web, "abc1234", "running", "Gitberth: Web", "x", null, "main");

        Assert.False(posted);
        Assert.Single(_gitlab.Calls);
        Assert.True(GitLabApi.IsUnchangedStatus("Cannot transition status via :enqueue from :pending"));
        Assert.False(GitLabApi.IsUnchangedStatus("state does not have a valid value"));
    }

    [Fact]
    public async Task The_merge_requests_note_is_made_found_and_kept_up_to_date()
    {
        var conn = new GitLabConnection("https://gitlab.com", "glpat-x");
        _gitlab.Respond = r => r.Method == HttpMethod.Get
            ? Page("""[{"id":3,"body":"LGTM","author":{"username":"bob"},"system":false},{"id":9,"body":"<!-- gitberth:preview -->\nold","author":{"username":"bot"},"system":false}]""", "")
            : r.Method == HttpMethod.Post ? GitLabStub.Json(HttpStatusCode.Created, """{"id":44}""")
            : r.RequestUri!.AbsolutePath.EndsWith("/notes/5", StringComparison.Ordinal) ? GitLabStub.Json(HttpStatusCode.NotFound, """{"message":"404 Not found"}""")
            : GitLabStub.Json(HttpStatusCode.OK, """{"id":9}""");

        Assert.Equal(44, await _api.CreateNoteAsync(conn, Web, 12, "hello"));
        Assert.Equal("hello", JsonDocument.Parse(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/merge_requests/12/notes")).RootElement.GetProperty("body").GetString());
        Assert.True(await _api.UpdateNoteAsync(conn, Web, 12, 9, "again"));
        Assert.Contains($"PUT /api/v4/projects/{WebId}/merge_requests/12/notes/9", _gitlab.Calls);
        // Someone deleted it: false, so a new one is made.
        Assert.False(await _api.UpdateNoteAsync(conn, Web, 12, 5, "again"));
        var notes = await _api.ListNotesAsync(conn, Web, 12);
        Assert.Equal([3L, 9L], notes.Select(n => n.Id));
        Assert.Contains($"GET /api/v4/projects/{WebId}/merge_requests/12/notes?per_page=100&page=1", _gitlab.Calls);
    }

    [Fact]
    public async Task The_project_hook_is_added_or_set_with_its_token_events_and_tls_checked()
    {
        var conn = new GitLabConnection("https://gitlab.com", "glpat-x");
        _gitlab.Respond = r => r.Method == HttpMethod.Get
            ? Page("""[{"id":7,"url":"https://hooks.example.com/hooks/gitlab","push_events":true,"merge_requests_events":true}]""", "")
            : r.Method == HttpMethod.Post ? GitLabStub.Json(HttpStatusCode.Created, """{"id":21}""") : GitLabStub.Json(HttpStatusCode.OK, """{"id":7}""");

        var hook = Assert.Single(await _api.ListHooksAsync(conn, Web));
        Assert.Equal(new GitLabHook(7, "https://hooks.example.com/hooks/gitlab", true, true), hook);
        Assert.Equal(7, await _api.SaveHookAsync(conn, Web, 7, "https://hooks.example.com/hooks/gitlab", "s3cret"));
        Assert.Equal(21, await _api.SaveHookAsync(conn, Web, null, "https://hooks.example.com/hooks/gitlab", "s3cret"));

        foreach (var call in new[] { $"PUT /api/v4/projects/{WebId}/hooks/7", $"POST /api/v4/projects/{WebId}/hooks" })
        {
            var body = JsonDocument.Parse(_gitlab.BodyOf(call)).RootElement;
            Assert.Equal(("https://hooks.example.com/hooks/gitlab", "s3cret", true, true, true),
                (body.GetProperty("url").GetString(), body.GetProperty("token").GetString(), body.GetProperty("push_events").GetBoolean(),
                    body.GetProperty("merge_requests_events").GetBoolean(), body.GetProperty("enable_ssl_verification").GetBoolean()));
        }
    }

    [Fact]
    public async Task A_hooks_list_refused_is_a_refusal_of_the_hook_on_this_project()
    {
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");

        var ex = await Assert.ThrowsAsync<GitLabWriteException>(() => _api.ListHooksAsync(new GitLabConnection("https://gitlab.com", "glpat-x"), Web));

        Assert.True(ex.MissingPermission);
        Assert.Contains("the api scope, and at least the Maintainer role", ex.Message);
    }

    [Fact]
    public async Task Variables_are_read_with_their_scope_and_saved_and_deleted_for_one_scope()
    {
        var conn = new GitLabConnection("https://gitlab.com", "glpat-x");
        _gitlab.Respond = r => r.Method == HttpMethod.Get
            ? Page("""[{"key":"API_URL","value":"https://api","environment_scope":"*","masked":false,"protected":false,"variable_type":"env_var"},{"key":"KEY","value":"k","environment_scope":"production","masked":true,"protected":true,"variable_type":"file"}]""", "")
            : r.Method == HttpMethod.Put ? GitLabStub.Json(HttpStatusCode.NotFound, """{"message":"404 Variable Not Found"}""")
            : r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : GitLabStub.Json(HttpStatusCode.Created, """{"key":"NEW"}""");

        var all = await _api.ListVariablesAsync(conn, Web);
        Assert.Equal([new GitLabVariable("API_URL", "https://api", "*", false, false, "env_var"), new GitLabVariable("KEY", "k", "production", true, true, "file")], all);

        // Not there for that scope: added, plain.
        await _api.SetVariableAsync(conn, Web, "NEW", "v", "preview/*");
        Assert.Equal([$"GET /api/v4/projects/{WebId}/variables?per_page=100&page=1",
            $"PUT /api/v4/projects/{WebId}/variables/NEW?filter%5Benvironment_scope%5D=preview%2F%2A",
            $"POST /api/v4/projects/{WebId}/variables"], _gitlab.Calls);
        var created = JsonDocument.Parse(_gitlab.BodyOf($"POST /api/v4/projects/{WebId}/variables")).RootElement;
        Assert.Equal(("NEW", "v", "preview/*", false, false, "env_var"),
            (created.GetProperty("key").GetString(), created.GetProperty("value").GetString(), created.GetProperty("environment_scope").GetString(),
                created.GetProperty("masked").GetBoolean(), created.GetProperty("protected").GetBoolean(), created.GetProperty("variable_type").GetString()));

        await _api.DeleteVariableAsync(conn, Web, "NEW", "production");
        Assert.Equal($"DELETE /api/v4/projects/{WebId}/variables/NEW?filter%5Benvironment_scope%5D=production", _gitlab.Calls[^1]);
    }

    [Fact]
    public async Task A_variable_gitlab_refuses_says_gitlabs_words()
    {
        _gitlab.Respond = _ => GitLabStub.Json(HttpStatusCode.BadRequest, """{"message":{"value":["is invalid"],"key":["has already been taken"]}}""");

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() =>
            _api.SetVariableAsync(new GitLabConnection("https://gitlab.com", "glpat-x"), Web, "API", "v", "*"));

        Assert.Equal("GitLab refused to save API to acme/platform/web's CI/CD variables: value is invalid; key has already been taken", ex.Message);
    }
}
