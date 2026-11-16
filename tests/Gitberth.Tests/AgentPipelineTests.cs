using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Agents;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gitberth.Tests;

/// <summary>
/// The token scheme in Program.cs's pipeline, on an in-memory server: a bearer counts on /mcp and /api only and the
/// sign-in cookie never does there; both answer their own status codes and JSON, never the "not found" page; a browser
/// page's request (an Origin header) is refused; bad tokens are throttled; and the MCP server speaks JSON-RPC, showing a
/// token only the tools its access allows.
/// </summary>
public sealed class AgentPipelineTests : IAsyncLifetime
{
    private readonly AppsRig _rig = new();
    private readonly AuditLog _audit = new();
    private ApiTokens _tokens = default!;
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _tokens = new ApiTokens(_rig.Db, _rig.Settings, _audit);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_rig.Db);
        builder.Services.AddSingleton(_rig.Settings);
        builder.Services.AddSingleton(_audit);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => o.LoginPath = "/login");
        builder.Services.AddAuthorization();
        builder.Services.AddGitberthAgents();
        builder.Services.AddSingleton(_tokens);
        _app = builder.Build();
        // As in Program.cs: the status-code pages, then the agents' paths' own answers, then authentication.
        _app.UseStatusCodePagesWithReExecute("/not-found");
        _app.Use(AgentPaths.Middleware);
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.Use(AgentPaths.ActorMiddleware);
        _app.MapGet("/not-found", () => Results.Content("<html>the not-found page</html>", "text/html"));
        _app.MapGet("/private", () => "signed in").RequireAuthorization();
        // Something on the agents' paths that fails on Gitberth's side.
        _app.MapGet("/api/v1/broken", string () => throw new InvalidOperationException("the disk is on fire at /data/gitberth.db"));
        _app.MapGet("/signin", async (HttpContext context) =>
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme))));
        _app.MapGitberthAgents();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _rig.Dispose();
    }

    private async Task<string> TokenAsync(ApiAccess access, string name = "agent")
    {
        var made = await _tokens.CreateAsync(name, access, null);
        return made.Secret!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = null, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"]?.GetValue<string>();

    [Fact]
    public async Task Without_a_token_the_api_and_mcp_answer_401_json_with_a_bearer_challenge_never_the_not_found_page()
    {
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/tools"), (HttpMethod.Post, "/mcp"), (HttpMethod.Post, "/api/v1/tools/list_apps") })
        {
            var response = await _client.SendAsync(Request(method, path, body: method == HttpMethod.Post ? "{}" : null));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Authorization: Bearer", await ErrorOf(response));
        }
        var missing = await _client.SendAsync(Request(HttpMethod.Get, "/api/v2/nothing"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("not-found page", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_good_token_lists_the_tools_and_says_which_it_may_call()
    {
        var token = await TokenAsync(ApiAccess.Operate);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tools = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["tools"]!.AsArray();
        Assert.Equal(ToolCatalog.All.Count, tools.Count);
        Assert.True(tools.Single(t => t!["name"]!.GetValue<string>() == "deploy_app")!["allowed"]!.GetValue<bool>());
        Assert.False(tools.Single(t => t!["name"]!.GetValue<string>() == "delete_app")!["allowed"]!.GetValue<bool>());
        Assert.Equal("object", tools[0]!["input_schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_sign_in_cookie_never_counts_on_mcp_or_the_api()
    {
        var signin = await _client.GetAsync("/signin");
        var cookie = signin.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var page = new HttpRequestMessage(HttpMethod.Get, "/private");
        page.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(page)).StatusCode);

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/tools"), (HttpMethod.Post, "/mcp") })
        {
            var request = Request(method, path, body: method == HttpMethod.Post ? "{}" : null);
            request.Headers.Add("Cookie", cookie);
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_token_never_counts_anywhere_else()
    {
        var token = await TokenAsync(ApiAccess.Admin);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/private", token));
        // The cookie's challenge: off to sign in, as without anything.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_request_with_an_origin_header_is_refused_with_403_even_with_a_good_token()
    {
        var token = await TokenAsync(ApiAccess.Admin);
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/tools"), (HttpMethod.Post, "/mcp") })
        {
            var request = Request(method, path, token, method == HttpMethod.Post ? "{}" : null);
            request.Headers.Add("Origin", "http://evil.example");
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("Origin", await ErrorOf(response));
        }
    }

    [Fact]
    public async Task Revoked_and_expired_tokens_get_401_and_bad_tokens_are_throttled_and_audited_once_a_minute()
    {
        var revoked = await TokenAsync(ApiAccess.Read, "old");
        await _tokens.RevokeAsync((await _tokens.ListAsync()).Single(t => t.Name == "old").Id);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", revoked));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("The token old was revoked.", await ErrorOf(response));
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());

        var expired = await TokenAsync(ApiAccess.Read, "short");
        await using (var db = _rig.Db.CreateDbContext())
            await db.ApiTokens.Where(t => t.Name == "short").ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", expired));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("expired", await ErrorOf(response));

        HttpStatusCode last = 0;
        for (var i = 0; i < ApiTokenThrottle.MaxFailures + 1; i++)
            last = (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", ApiTokens.NewToken()))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        // A good token still gets in from the same address.
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", await TokenAsync(ApiAccess.Read, "good")))).StatusCode);
        Assert.Single(_audit.Entries, e => e.Action == "auth.token.refused");
        Assert.Single(_audit.Entries, e => e.Action == "auth.token.expired");
    }

    [Fact]
    public async Task A_read_token_is_refused_a_deploy_over_the_api_with_403_and_why()
    {
        var token = await TokenAsync(ApiAccess.Read, "reader");
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/v1/tools/deploy_app", token, "{\"app\":\"orb\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("needs Operate", await ErrorOf(response));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Request(HttpMethod.Post, "/api/v1/tools/no_such_tool", token, "{}"))).StatusCode);
    }

    [Fact]
    public async Task A_body_that_isnt_a_json_object_is_a_400_that_says_so()
    {
        var token = await TokenAsync(ApiAccess.Read, "reader");
        foreach (var body in new[] { "[1, 2]", "not json", "\"text\"", "{\"app\": ", "{\"app\": \"orb\"} trailing" })
        {
            var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/v1/tools/list_apps", token, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("JSON object", await ErrorOf(response));
        }
    }

    [Fact]
    public async Task A_body_that_names_an_argument_twice_is_a_400_that_says_which()
    {
        var token = await TokenAsync(ApiAccess.Read, "reader");
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/v1/tools/list_apps", token, "{\"filter\": \"orb\", \"filter\": \"kanban\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The body names filter twice: give each argument once.", await ErrorOf(response));
    }

    // RFC 6750's challenge, in plain ASCII (Kestrel refuses anything else in a header, and the client would see a broken
    // answer): TestServer doesn't check headers, so the header's text is checked here.
    private static string Challenge(HttpResponseMessage response) =>
        string.Join(", ", response.Headers.TryGetValues("WWW-Authenticate", out var values) ? values : []);

    [Theory]
    [InlineData("Basic YWRtaW46cGFzc3dvcmQ=")]
    [InlineData("Token x")]
    [InlineData("")]
    public async Task A_request_without_a_bearer_token_gets_401_json_and_a_plain_ascii_bearer_challenge(string authorization)
    {
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/tools"), (HttpMethod.Post, "/mcp") })
        {
            var request = new HttpRequestMessage(method, path) { Content = method == HttpMethod.Post ? new StringContent("{}", Encoding.UTF8, "application/json") : null };
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Authorization: Bearer", await ErrorOf(response));
            var challenge = Challenge(response);
            Assert.Equal("Bearer realm=\"Gitberth\"", challenge);
            Assert.Matches(@"^[\x20-\x7E]*$", challenge);
        }
    }

    [Fact]
    public async Task A_revoked_tokens_challenge_is_plain_ascii_whatever_the_token_is_called()
    {
        const string name = "Café – bot";
        var token = await TokenAsync(ApiAccess.Read, name);
        await _tokens.RevokeAsync((await _tokens.ListAsync()).Single(t => t.Name == name).Id);

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The body says it all, name included; the header only which kind of refusal, in fixed words.
        Assert.Equal("The token Café – bot was revoked.", await ErrorOf(response));
        var challenge = Challenge(response);
        Assert.Equal("Bearer realm=\"Gitberth\", error=\"invalid_token\", error_description=\"The access token was revoked\"", challenge);
        Assert.Matches(@"^[\x20-\x7E]*$", challenge);
        // An unknown one, and one that isn't even a token.
        foreach (var (bad, description) in new[] { (ApiTokens.NewToken(), "The access token is not known"), ("not-a-token", "The access token is malformed") })
        {
            response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", bad));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal($"Bearer realm=\"Gitberth\", error=\"invalid_token\", error_description=\"{description}\"", Challenge(response));
        }
    }

    [Fact]
    public async Task Something_failing_on_gitberths_side_is_a_json_500_and_a_caller_that_hung_up_isnt_answered()
    {
        var response = await _client.GetAsync("/api/v1/broken");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = await ErrorOf(response);
        Assert.Equal("Gitberth couldn't answer that: something went wrong on its side, and its log says what.", error);
        Assert.DoesNotContain("not-found page", error);

        // The caller hung up: nothing is written, and the cancellation goes on up.
        var aborted = new DefaultHttpContext { RequestServices = _app.Services };
        aborted.Request.Path = "/api/v1/tools";
        aborted.Response.Body = new MemoryStream();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        aborted.RequestAborted = gone.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentPaths.Middleware(aborted, c => throw new OperationCanceledException(c.RequestAborted)));
        Assert.Equal(0, aborted.Response.Body.Length);

        // A cancellation that isn't the caller's (a time-out inside) fails like anything else.
        var timedOut = new DefaultHttpContext { RequestServices = _app.Services };
        timedOut.Request.Path = "/mcp";
        timedOut.Response.Body = new MemoryStream();
        await AgentPaths.Middleware(timedOut, _ => throw new TaskCanceledException());
        Assert.Equal(StatusCodes.Status500InternalServerError, timedOut.Response.StatusCode);
        Assert.Equal("no-store", timedOut.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task An_operate_token_may_turn_previews_on_or_off_but_is_refused_their_settings()
    {
        var token = await TokenAsync(ApiAccess.Operate, "ops");
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/v1/tools/set_preview_settings", token, "{\"app\":\"orb\",\"forks\":true}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The token ops has Operate access, and set_preview_settings needs Admin. " +
            "A person can make a token with Admin access in Gitberth's Settings › AI agents and API.", await ErrorOf(response));
        var tools = JsonNode.Parse(await (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/tools", token))).Content.ReadAsStringAsync())!["tools"]!.AsArray();
        bool Allowed(string name) => tools.Single(t => t!["name"]!.GetValue<string>() == name)!["allowed"]!.GetValue<bool>();
        Assert.True(Allowed("set_previews"));
        Assert.False(Allowed("set_preview_settings"));
        Assert.Contains(_audit.Entries, e => e.Action == "auth.token.denied" && e.Target == "set_preview_settings" && e.Actor.Name == "ops");
    }

    // ---- MCP ----

    private async Task<JsonNode> RpcAsync(string token, string method, JsonObject? @params = null, int id = 1)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) body["params"] = @params;
        var request = Request(HttpMethod.Post, "/mcp", token, body.ToJsonString());
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        // Streamable HTTP answers JSON, or an event stream whose data lines carry it.
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).Last(l => l.Length > 0)
            : text;
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task The_mcp_server_initializes_and_lists_only_the_tools_a_tokens_access_allows()
    {
        var token = await TokenAsync(ApiAccess.Read, "reader");
        var init = await RpcAsync(token, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });
        Assert.Equal("gitberth", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("list_apps", init["result"]!["instructions"]!.GetValue<string>());

        var list = await RpcAsync(token, "tools/list", id: 2);
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_apps", names);
        Assert.Contains("query_audit_log", names);
        Assert.DoesNotContain("deploy_app", names);
        Assert.DoesNotContain("delete_app", names);
        var listApps = list["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "list_apps")!;
        Assert.True(listApps["annotations"]!["readOnlyHint"]!.GetValue<bool>());

        var call = await RpcAsync(token, "tools/call", new JsonObject { ["name"] = "deploy_app", ["arguments"] = new JsonObject { ["app"] = "orb" } }, id: 3);
        Assert.True(call["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("needs Operate", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains(_audit.Entries, e => e.Action == "auth.token.denied" && e.Actor.Name == "reader" && e.Actor.Via == AuditVia.Mcp);
    }
}
