using System.Net;
using System.Text;
using Gitberth.Services;

namespace Gitberth.Tests;

public class GitHubVariablesTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Lists_every_page_of_variables()
    {
        var handler = new StubHandler(r => r.RequestUri!.Query.Contains("page=1")
            ? Json(HttpStatusCode.OK, """{"total_count":31,"variables":[""" + string.Join(",", Enumerable.Range(1, 30).Select(i => $$"""{"name":"V{{i}}","value":"{{i}}"}""")) + "]}")
            : Json(HttpStatusCode.OK, """{"total_count":31,"variables":[{"name":"DATABASE_URL","value":"postgres://kanban:pw@postgres:5432/kanban"}]}"""));
        var vars = await new GitHubApi(new Factory(handler)).ListVariablesAsync("t", "acme", "web");

        Assert.Equal(31, vars.Count);
        Assert.Equal("postgres://kanban:pw@postgres:5432/kanban", vars[^1].Value);
        Assert.Equal(["GET /repos/acme/web/actions/variables?per_page=30&page=1", "GET /repos/acme/web/actions/variables?per_page=30&page=2"], handler.Calls);
    }

    [Fact]
    public async Task Saving_updates_an_existing_variable_or_creates_it()
    {
        var handler = new StubHandler(r => r.Method == HttpMethod.Patch ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.Created));
        await new GitHubApi(new Factory(handler)).SetVariableAsync("t", "acme", "web", "AUTH_SECRET", "abc");
        Assert.Equal(["PATCH /repos/acme/web/actions/variables/AUTH_SECRET", "POST /repos/acme/web/actions/variables"], handler.Calls);
    }

    [Fact]
    public async Task A_token_without_the_variables_permission_gets_a_clear_message()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}"""));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubApi(new Factory(handler)).ListVariablesAsync("t", "acme", "web"));
        Assert.Contains("Variables to Read and write", ex.Message);
        Assert.Contains("acme/web", ex.Message);
    }

    [Fact]
    public async Task A_rate_limit_is_not_reported_as_a_permission_problem()
    {
        var handler = new StubHandler(_ =>
        {
            var r = Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""");
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        });
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubApi(new Factory(handler)).ListVariablesAsync("t", "acme", "web"));
        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task An_unreadable_answer_is_a_github_error_not_a_crash()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "<html>proxy error</html>"));
        await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubApi(new Factory(handler)).ListVariablesAsync("t", "acme", "web"));
    }

    [Fact]
    public void The_kept_copy_round_trips_and_ignores_damage()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new List<RepoVariable> { new("DATABASE_URL", "postgres://k:pw@postgres/k") });
        Assert.Equal([new RepoVariable("DATABASE_URL", "postgres://k:pw@postgres/k")], RepoVariables.ParseSaved(json));
        Assert.Null(RepoVariables.ParseSaved("{not json"));
        Assert.Equal(RepoVariables.SavedKey("Acme", "Web"), RepoVariables.SavedKey("acme", "web"));
    }

    [Theory]
    [InlineData("DATABASE_URL", null)]
    [InlineData("NEXT_PUBLIC_SITE", null)]
    [InlineData("GITHUB_TOKEN", "GitHub reserves names that start with GITHUB_.")]
    [InlineData("2FA_KEY", "Use letters, digits and underscores, not starting with a digit.")]
    public void Checks_names_the_way_github_does(string name, string? problem) =>
        Assert.Equal(problem, RepoVariables.Problem(name));
}
