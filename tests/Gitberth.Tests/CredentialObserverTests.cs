using System.Net;
using System.Text;
using Gitberth.Credentials;
using Gitberth.Data;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// The handler on the github and gitlab clients: it reports a 401 as a refusal of the token the request carried (with what
/// Gitberth was doing, and GitLab's own word), a 2xx as an acceptance (with GitHub's expiry header), and nothing else; the
/// call itself goes on exactly as without it. The real GitHubApi, GitHubWrites and GitLabApi over a stub.
/// </summary>
public sealed class CredentialObserverTests : IDisposable
{
    private const string Saved = "github_pat_saved_one";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    // A new observer for each client, over the same stub: an observer, like any handler, is part of one client's chain.
    private sealed class Factory(Func<HttpMessageHandler> make) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(make(), disposeHandler: false);
    }

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly CredentialSignals _signals = new();
    private readonly List<CredentialAnswer> _heard = [];
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SettingsService _settings;

    public CredentialObserverTests()
    {
        _signals.Answered += a =>
        {
            lock (_heard) _heard.Add(a);
        };
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
    }

    public void Dispose() => _connection.Dispose();

    private Factory Observed(string kind, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var stub = new Stub(answer);
        return new Factory(() => new CredentialObserver(kind, _signals) { InnerHandler = stub });
    }

    private static Factory Bare(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var stub = new Stub(answer);
        return new Factory(() => stub);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json = "{}", params (string Name, string Value)[] headers)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) answer.Headers.TryAddWithoutValidation(name, value);
        return answer;
    }

    private List<CredentialAnswer> Heard()
    {
        lock (_heard) return [.. _heard];
    }

    [Fact]
    public async Task A_401_is_reported_as_a_refusal_of_the_token_sent_with_what_gitberth_was_doing()
    {
        var writes = new GitHubWrites(Observed(CredentialWatch.GitHubKey, _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""")));

        var refused = await Assert.ThrowsAsync<GitHubWriteException>(() =>
            writes.SetStatusAsync(Saved, "acme", "web", Sha, new CommitStatus("pending", "Building", "Gitberth: web", null)));

        Assert.True(refused.BadToken);
        var answer = Assert.Single(Heard());
        Assert.Equal(new CredentialAnswer(CredentialWatch.GitHubKey, CredentialSignals.Hash(Saved), CredentialVerdict.Refused,
            "while Gitberth was setting a commit status on acme/web"), answer);
    }

    [Fact]
    public async Task Each_answer_carries_the_hash_of_the_token_it_was_sent_and_a_call_without_one_reports_nothing()
    {
        var github = new GitHubApi(Observed(CredentialWatch.GitHubKey, _ => Json(HttpStatusCode.Unauthorized)));

        // A new token being checked before it's saved: its own hash, so the watch can tell it from the saved one's.
        Assert.False((await github.CheckTokenAsync("github_pat_typed_just_now")).Valid);
        var answer = Assert.Single(Heard());
        Assert.Equal((CredentialSignals.Hash("github_pat_typed_just_now"), "while Gitberth was checking the token"), (answer.TokenHash, answer.Doing));
        Assert.NotEqual(CredentialSignals.Hash(Saved), answer.TokenHash);

        // No token saved (a public repo's pull requests, asked without one): nothing about any token.
        var pulls = new GitHubPulls(Observed(CredentialWatch.GitHubKey, _ => Json(HttpStatusCode.Unauthorized)), _settings);
        await Assert.ThrowsAsync<GitHubApiException>(() => pulls.ListOpenAsync("acme", "web"));
        Assert.Single(Heard());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task Anything_but_a_401_or_a_2xx_says_nothing_and_the_call_ends_as_it_would_without_the_observer(HttpStatusCode status, bool rateLimit)
    {
        HttpResponseMessage Answer(HttpRequestMessage _) =>
            rateLimit ? Json(status, """{"message":"API rate limit exceeded"}""", ("x-ratelimit-remaining", "0")) : Json(status, """{"message":"Nope"}""");

        var watched = new GitHubApi(Observed(CredentialWatch.GitHubKey, Answer));
        var bare = new GitHubApi(Bare(Answer));
        Assert.Equal(await bare.CheckTokenAsync(Saved), await watched.CheckTokenAsync(Saved));
        var withObserver = await Assert.ThrowsAsync<GitHubApiException>(() => watched.ListVariablesAsync(Saved, "acme", "web"));
        var without = await Assert.ThrowsAsync<GitHubApiException>(() => bare.ListVariablesAsync(Saved, "acme", "web"));
        Assert.Equal(without.Message, withObserver.Message);
        Assert.Empty(Heard());
    }

    [Fact]
    public async Task No_answer_at_all_reaches_the_call_as_it_was_and_reports_nothing()
    {
        HttpResponseMessage Unreachable(HttpRequestMessage _) => throw new HttpRequestException("Connection refused");
        var watched = await new GitHubApi(Observed(CredentialWatch.GitHubKey, Unreachable)).CheckTokenAsync(Saved);
        var bare = await new GitHubApi(Bare(Unreachable)).CheckTokenAsync(Saved);

        Assert.Equal(bare, watched);
        Assert.Contains("couldn't be reached", watched.Warning, StringComparison.Ordinal);
        Assert.Empty(Heard());
    }

    [Theory]
    [InlineData("2026-12-01 00:00:00 UTC", "2026-12-01T00:00:00+00:00")]
    [InlineData("2026-12-01 00:00:00 -0800", "2026-12-01T08:00:00+00:00")]
    public async Task GitHubs_expiry_header_on_an_answer_is_reported_with_the_acceptance(string header, string expected)
    {
        var github = new GitHubApi(Observed(CredentialWatch.GitHubKey,
            _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""", (CredentialObserver.GitHubExpiryHeader, header))));

        var check = await github.CheckTokenAsync(Saved);

        Assert.Equal("e2e-bot", check.Login);
        var answer = Assert.Single(Heard());
        Assert.Equal((CredentialVerdict.Accepted, CredentialSignals.Hash(Saved)), (answer.Verdict, answer.TokenHash));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), answer.ExpiresAt);
    }

    [Fact]
    public async Task GitLabs_401_carries_its_own_word_and_what_was_asked_of_which_project()
    {
        const string revoked = "Token was revoked. You have to re-authorize from the user.";
        var api = new GitLabApi(Observed(CredentialWatch.GitLabKey, _ => Json(HttpStatusCode.Unauthorized,
            $$"""{"error":"invalid_token","error_description":"{{revoked}}"}""",
            ("WWW-Authenticate", $"Bearer realm=\"\", error=\"invalid_token\", error_description=\"{revoked}\""))), _settings);
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-saved");

        Assert.False((await api.CheckTokenAsync("https://gitlab.com", "glpat-saved")).Valid);
        await Assert.ThrowsAnyAsync<HostApiException>(() => api.ListOpenMergeRequestsAsync(new GitLabRepoRef("acme/platform", "web")));

        var heard = Heard();
        Assert.Equal(2, heard.Count);
        Assert.All(heard, a => Assert.Equal((CredentialWatch.GitLabKey, CredentialSignals.Hash("glpat-saved"), CredentialVerdict.Refused, "GitLab says it was revoked."),
            (a.Kind, a.TokenHash, a.Verdict, a.Words)));
        Assert.Equal(["while Gitberth was checking the token", "while Gitberth was reading acme/platform/web's merge requests"], heard.Select(a => a.Doing));
    }

    [Fact]
    public async Task A_listener_that_fails_never_reaches_the_call()
    {
        _signals.Answered += _ => throw new InvalidOperationException("a listener broke");
        var github = new GitHubApi(Observed(CredentialWatch.GitHubKey, _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""")));

        Assert.Equal(new TokenCheck(true, "e2e-bot", null, null), await github.CheckTokenAsync(Saved));
        Assert.Single(Heard());
    }

    [Theory]
    [InlineData("github", "GET", "https://api.github.com/user", "checking the token")]
    [InlineData("github", "POST", "https://api.github.com/repos/acme/web/statuses/abc", "setting a commit status on acme/web")]
    [InlineData("github", "POST", "https://api.github.com/repos/acme/web/hooks", "setting up the webhook on acme/web")]
    [InlineData("github", "PATCH", "https://api.github.com/repos/acme/web/issues/comments/7", "commenting on a pull request of acme/web")]
    [InlineData("github", "GET", "https://api.github.com/repos/acme/web/actions/variables?per_page=30&page=1", "reading acme/web's variables")]
    [InlineData("github", "PATCH", "https://api.github.com/repos/acme/web/actions/variables/API_URL", "saving acme/web's variables")]
    [InlineData("github", "GET", "https://api.github.com/repos/acme/web/pulls?state=open", "reading acme/web's pull requests")]
    [InlineData("github", "GET", "https://api.github.com/repos/acme/web/commits/main", "reading acme/web")]
    [InlineData("github", "GET", "https://api.github.com/rate_limit", "talking to GitHub")]
    [InlineData("gitlab", "GET", "https://gitlab.com/api/v4/personal_access_tokens/self", "checking the token")]
    [InlineData("gitlab", "POST", "https://gitlab.com/api/v4/projects/acme%2Fweb/statuses/abc", "setting a commit status on acme/web")]
    [InlineData("gitlab", "POST", "https://example.com/gitlab/api/v4/projects/acme%2Fweb/merge_requests/3/notes", "commenting on a merge request of acme/web")]
    [InlineData("gitlab", "PUT", "https://gitlab.com/api/v4/projects/acme%2Fweb/hooks/9", "setting up the project hook on acme/web")]
    public void What_a_call_was_for_comes_from_its_address(string kind, string method, string url, string doing) =>
        Assert.Equal("while Gitberth was " + doing, CredentialText.ApiDoing(kind, new HttpMethod(method), new Uri(url)));
}
