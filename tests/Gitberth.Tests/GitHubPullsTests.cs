using System.Net;
using System.Text;
using Gitberth.Data;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// The open pull requests of a repo as GitHub lists them (page after page), whether a closed one was merged, and what the
/// Previews tab says when GitHub can't be asked: plain words, never GitHub's own error text.
/// </summary>
public sealed class GitHubPullsTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class GitHub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("[]") };
        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SettingsService _settings;
    private readonly GitHub _github = new();
    private readonly GitHubPulls _pulls;

    public GitHubPullsTests()
    {
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _pulls = new GitHubPulls(new Factory(_github), _settings);
    }

    public void Dispose() => _connection.Dispose();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Pulls(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(n =>
        "{\"number\":" + n + ",\"title\":\"PR " + n + "\",\"head\":{\"ref\":\"b" + n + "\",\"sha\":\"" + n.ToString("D40", System.Globalization.CultureInfo.InvariantCulture)
        + "\",\"repo\":{\"full_name\":\"acme/orb\"}},\"base\":{\"repo\":{\"full_name\":\"acme/orb\"}}}")) + "]";

    private async Task<string> RefusedAsync() => (await Assert.ThrowsAsync<GitHubApiException>(() => _pulls.ListOpenAsync("acme", "orb"))).Message;

    [Fact]
    public async Task Every_page_is_read_until_a_short_one_with_the_token_when_there_is_one()
    {
        _github.Answer = r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(r.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? Pulls(1, 100) : Pulls(101, 3)),
        };

        var open = await _pulls.ListOpenAsync("acme", "orb");

        Assert.Equal(103, open.Count);
        Assert.Equal(["/repos/acme/orb/pulls?state=open&per_page=100&page=1", "/repos/acme/orb/pulls?state=open&per_page=100&page=2"],
            _github.Asked.Select(r => r.RequestUri!.PathAndQuery));
        Assert.All(_github.Asked, r => Assert.Null(r.Headers.Authorization));
        Assert.All(_github.Asked, r => Assert.Equal("2022-11-28", r.Headers.GetValues("X-GitHub-Api-Version").Single()));

        _github.Asked.Clear();
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_example");
        await _pulls.ListOpenAsync("acme", "orb");
        Assert.All(_github.Asked, r => Assert.Equal(("Bearer", "ghp_example"), (r.Headers.Authorization!.Scheme, r.Headers.Authorization.Parameter)));
    }

    [Fact]
    public async Task What_github_refuses_is_said_in_plain_words_with_and_without_a_token()
    {
        HttpResponseMessage Status(HttpStatusCode status, string? remaining = null)
        {
            var answer = new HttpResponseMessage(status) { Content = Json("""{"message":"Bad credentials"}""") };
            if (remaining is not null) answer.Headers.Add("x-ratelimit-remaining", remaining);
            return answer;
        }

        _github.Answer = _ => Status(HttpStatusCode.TooManyRequests);
        Assert.Equal("GitHub's API rate limit for this host is used up for the moment (60 an hour without a token). Add a GitHub token in Settings, or wait.", await RefusedAsync());
        _github.Answer = _ => Status(HttpStatusCode.Forbidden, remaining: "0");
        Assert.Equal("GitHub's API rate limit for this host is used up for the moment (60 an hour without a token). Add a GitHub token in Settings, or wait.", await RefusedAsync());
        _github.Answer = _ => Status(HttpStatusCode.NotFound);
        Assert.Equal("GitHub can't list acme/orb's pull requests. If the repo is private, add a token in Settings.", await RefusedAsync());
        _github.Answer = _ => Status(HttpStatusCode.BadGateway);
        Assert.Equal("GitHub answered 502 when asked for acme/orb's pull requests.", await RefusedAsync());

        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_example");
        _github.Answer = _ => Status(HttpStatusCode.Forbidden, remaining: "0");
        Assert.Equal("GitHub's API rate limit for this token is used up for the moment, so pull requests are checked again later.", await RefusedAsync());
        // A 403 with requests left is a token that can't see the repo, not the rate limit.
        _github.Answer = _ => Status(HttpStatusCode.Forbidden, remaining: "4999");
        Assert.Equal("GitHub can't list acme/orb's pull requests with this token. Check that it can access the repo (Pull requests: Read-only).", await RefusedAsync());
        _github.Answer = _ => Status(HttpStatusCode.Unauthorized);
        Assert.Equal("GitHub says the token is invalid or expired. Update it in Settings.", await RefusedAsync());
    }

    [Fact]
    public async Task Github_out_of_reach_or_too_slow_is_said_so()
    {
        _github.Answer = _ => throw new HttpRequestException("Name or service not known");
        Assert.Equal("api.github.com couldn't be reached (Name or service not known).", await RefusedAsync());

        _github.Answer = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        Assert.Equal("api.github.com didn't answer in time.", await RefusedAsync());

        // Gitberth stopping isn't GitHub's fault: it's a cancellation, not a refusal.
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        _github.Answer = _ => throw new TaskCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _pulls.ListOpenAsync("acme", "orb", stopping.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"number":12,"merged":true}""", true)]
    [InlineData(HttpStatusCode.OK, """{"number":12,"merged":false}""", false)]
    [InlineData(HttpStatusCode.OK, """{"number":12}""", null)]
    [InlineData(HttpStatusCode.OK, """{"number":12,"merged":null}""", null)]
    [InlineData(HttpStatusCode.OK, "not json", null)]
    [InlineData(HttpStatusCode.NotFound, """{"message":"Not Found"}""", null)]
    public async Task Whether_a_closed_pull_request_was_merged_or_null_when_github_cant_say(HttpStatusCode status, string body, bool? merged)
    {
        _github.Answer = _ => new HttpResponseMessage(status) { Content = Json(body) };

        Assert.Equal(merged, await _pulls.MergedAsync("acme", "orb", 12));
        Assert.Equal("/repos/acme/orb/pulls/12", Assert.Single(_github.Asked).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Merged_is_null_when_github_is_out_of_reach()
    {
        _github.Answer = _ => throw new HttpRequestException("Connection refused");

        Assert.Null(await _pulls.MergedAsync("acme", "orb", 12));
    }
}
