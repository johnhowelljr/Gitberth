using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Credentials;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// What git and Cloudflare's API tell the token watch: git turned away with the saved token (GitHub's or GitLab's) is a
/// refusal of that token, without one or for another reason nothing; Cloudflare refusing the token itself is said so (and
/// reported), a permission it lacks as before; verify's status and expiry are read, as a user's token or an account's.
/// </summary>
public sealed class CredentialSourcesTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly CredentialSignals _signals = new();
    private readonly List<CredentialAnswer> _heard = [];

    public CredentialSourcesTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = _db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _signals.Answered += a =>
        {
            lock (_heard) _heard.Add(a);
        };
    }

    public void Dispose() => _connection.Dispose();

    private List<CredentialAnswer> Heard()
    {
        lock (_heard) return [.. _heard];
    }

    private GitClient Git(string stderr) =>
        new(new GitberthOptions { DataDir = Path.GetTempPath() }, _settings, _signals) { Runner = (_, _, _) => Task.FromResult(new ProcessResult(128, "", stderr)) };

    // ---- git ----

    [Fact]
    public async Task Git_turned_away_with_the_github_token_reports_its_refusal_and_without_one_nothing()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "github_pat_saved");
        var git = Git("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/acme/web.git/'");
        var repo = new GitHubRepoRef("acme", "web");

        var refused = await Assert.ThrowsAsync<GitException>(() => git.LsRemoteAsync(repo));

        Assert.Equal("GitHub rejected the token. Check that it hasn't expired and update it in Settings.", refused.Message);
        Assert.Equal(new CredentialAnswer(CredentialWatch.GitHubKey, CredentialSignals.Hash("github_pat_saved"), CredentialVerdict.Refused,
            "while Gitberth was looking at acme/web"), Assert.Single(Heard()));

        // No token saved: GitHub asking for one is the repo being private, not a token refused.
        await _settings.SetAsync(SettingKeys.GitHubToken, null);
        await Assert.ThrowsAsync<GitException>(() => Git("fatal: could not read Username for 'https://github.com': terminal prompts disabled").LsRemoteAsync(repo));
        Assert.Single(Heard());
    }

    [Fact]
    public async Task Git_failing_for_another_reason_says_nothing_about_the_token()
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "github_pat_saved");
        await Assert.ThrowsAsync<GitException>(() => Git("remote: Repository not found.\nfatal: repository 'https://github.com/acme/gone.git/' not found")
            .LsRemoteAsync(new GitHubRepoRef("acme", "gone")));
        await Assert.ThrowsAsync<GitException>(() => Git("fatal: unable to access 'https://github.com/acme/web.git/': Could not resolve host: github.com")
            .CloneAsync(new GitHubRepoRef("acme", "web"), "main", Path.Combine(Path.GetTempPath(), "gitberth-never-made"), null));
        Assert.Empty(Heard());
    }

    [Fact]
    public async Task Git_turned_away_with_the_gitlab_token_reports_the_gitlab_tokens_refusal()
    {
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-saved");
        var git = Git("remote: HTTP Basic: Access denied. If a password was provided for Git authentication, the password was incorrect.\nfatal: Authentication failed");

        await Assert.ThrowsAsync<GitException>(() => git.CloneAsync(new GitLabRepoRef("acme/platform", "web"), "main", Path.Combine(Path.GetTempPath(), "gitberth-never-made"), null));

        Assert.Equal(new CredentialAnswer(CredentialWatch.GitLabKey, CredentialSignals.Hash("glpat-saved"), CredentialVerdict.Refused,
            "while Gitberth was cloning acme/platform/web"), Assert.Single(Heard()));
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/web.git/'", false, true)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", false, true)]
    [InlineData("remote: HTTP Basic: Access denied", true, true)]
    [InlineData("remote: HTTP Basic: Access denied", false, false)]
    [InlineData("fatal: repository 'https://github.com/acme/web.git/' not found", false, false)]
    public void One_rule_says_when_git_was_turned_away_for_its_credentials(string stderr, bool gitLab, bool refused) =>
        Assert.Equal(refused, GitClient.AuthFailed(stderr, gitLab));

    // ---- Cloudflare ----

    private static HttpResponseMessage Cloudflare(HttpStatusCode status, int code = 0, JsonObject? result = null, string message = "Nope")
    {
        var body = code == 0 && (int)status < 300
            ? new JsonObject { ["success"] = true, ["errors"] = new JsonArray(), ["result"] = result ?? new JsonObject() }
            : new JsonObject { ["success"] = false, ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message }), ["result"] = null };
        return new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static JsonNode? Errors(int code, string message) =>
        code == 0 ? null : new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message }) };

    // Cloudflare's own words with each code.
    private const string InvalidToken = "Invalid access token", NotAllowed = "Unauthorized to access requested resource";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 0, "")]
    [InlineData(HttpStatusCode.BadRequest, 1000, "Invalid API Token")]
    [InlineData(HttpStatusCode.Forbidden, 9109, InvalidToken)]
    [InlineData(HttpStatusCode.BadRequest, 6003, "Invalid request headers")]
    [InlineData(HttpStatusCode.BadRequest, 6111, "Invalid format for Authorization header")]
    public void Cloudflare_refusing_the_token_itself_says_it_expired_or_was_disabled_or_deleted(HttpStatusCode status, int code, string message)
    {
        Assert.True(CloudflareApi.TokenRefused(status, code, message));
        Assert.Equal("Cloudflare refused Gitberth's API token itself, so it couldn't read the tunnel's routes: the token has expired, or was disabled or "
                     + "deleted. Save a new one in Settings › Domains.", CloudflareApi.Explain(status, Errors(code, message), "read the tunnel's routes"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 0, "")]
    [InlineData(HttpStatusCode.Forbidden, 10000, "Authentication error")]
    [InlineData(HttpStatusCode.BadRequest, 10000, "Authentication error")]
    // A token that works, without the permission for an account's Access applications, say: the same code as a dead one.
    [InlineData(HttpStatusCode.Forbidden, 9109, NotAllowed)]
    public void A_permission_the_token_lacks_is_said_as_before(HttpStatusCode status, int code, string message)
    {
        Assert.False(CloudflareApi.TokenRefused(status, code, message));
        Assert.Equal($"Cloudflare didn't let Gitberth's token read the tunnel's routes. The token needs {CloudflareApi.Permissions}.",
            CloudflareApi.Explain(status, Errors(code, message), "read the tunnel's routes"));
    }

    [Fact]
    public async Task Cloudflares_answers_with_the_token_tell_the_watch_whether_it_was_refused_itself()
    {
        var answers = new Queue<HttpResponseMessage>([Cloudflare(HttpStatusCode.Forbidden, 9109, message: InvalidToken),
            Cloudflare(HttpStatusCode.Forbidden, 9109, message: NotAllowed), Cloudflare(HttpStatusCode.Forbidden, 10000), Cloudflare(HttpStatusCode.OK)]);
        var api = new CloudflareApi(new Factory(new Stub(_ => answers.Dequeue())), signals: _signals);

        var refused = await Assert.ThrowsAsync<CloudflareApiException>(() => api.GetTunnelConfigAsync("cf-token", "acc", "tun"));
        Assert.True(refused.TokenRefused);
        Assert.False((await Assert.ThrowsAsync<CloudflareApiException>(() => api.GetTunnelConfigAsync("cf-token", "acc", "tun"))).TokenRefused);
        Assert.False((await Assert.ThrowsAsync<CloudflareApiException>(() => api.GetTunnelConfigAsync("cf-token", "acc", "tun"))).TokenRefused);
        await api.GetTunnelConfigAsync("cf-token", "acc", "tun");

        Assert.Equal([(CredentialVerdict.Refused, (string?)"when Gitberth tried to read the tunnel's routes"), (CredentialVerdict.Accepted, null)],
            Heard().Select(a => (a.Verdict, a.Doing)));
        Assert.All(Heard(), a => Assert.Equal((CredentialWatch.CloudflareKey, CredentialSignals.Hash("cf-token")), (a.Kind, a.TokenHash)));
    }

    [Fact]
    public async Task Verify_reads_the_tokens_status_and_expiry_as_a_users_token_or_the_accounts()
    {
        var user = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/user/tokens/verify", StringComparison.Ordinal)
            ? Cloudflare(HttpStatusCode.OK, result: new JsonObject { ["id"] = "t1", ["status"] = "active", ["expires_on"] = "2026-12-01T00:00:00Z" })
            : Cloudflare(HttpStatusCode.NotFound, 7003));
        Assert.Equal(new CloudflareTokenCheck(false, "active", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), null),
            await new CloudflareApi(new Factory(user)).CheckTokenAsync("cf-token", "acc"));
        Assert.Equal(["/client/v4/user/tokens/verify"], user.Paths);

        // An account's own token: the user's verify refuses it, the account's takes it.
        var account = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/accounts/acc/tokens/verify", StringComparison.Ordinal)
            ? Cloudflare(HttpStatusCode.OK, result: new JsonObject { ["id"] = "t2", ["status"] = "active" })
            : Cloudflare(HttpStatusCode.Unauthorized, 1000));
        Assert.Equal(new CloudflareTokenCheck(false, "active", null, null), await new CloudflareApi(new Factory(account)).CheckTokenAsync("cf-token", "acc"));

        // Disabled, as Cloudflare says: refused, and why.
        var disabled = new Stub(_ => Cloudflare(HttpStatusCode.OK, result: new JsonObject { ["id"] = "t3", ["status"] = "disabled" }));
        var check = await new CloudflareApi(new Factory(disabled)).CheckTokenAsync("cf-token", "acc");
        Assert.Equal((true, "disabled"), (check.Refused, check.Status));
        Assert.Equal("Cloudflare says Gitberth's API token is disabled. Save a new one in Settings › Domains.", check.Problem);

        // Refused both ways: refused. Cloudflare failing both ways: a problem, never a refusal.
        var gone = await new CloudflareApi(new Factory(new Stub(_ => Cloudflare(HttpStatusCode.Unauthorized, 1000)))).CheckTokenAsync("cf-token", "acc");
        Assert.True(gone.Refused);
        var down = await new CloudflareApi(new Factory(new Stub(_ => Cloudflare(HttpStatusCode.BadGateway, 0)))).CheckTokenAsync("cf-token", "acc");
        Assert.Equal((false, (string?)null), (down.Refused, down.Status));
        Assert.NotNull(down.Problem);

        // Saving keeps the expiry, and throws with why when the token isn't taken.
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), await new CloudflareApi(new Factory(user)).VerifyTokenAsync("cf-token", "acc"));
        var notSaved = await Assert.ThrowsAsync<CloudflareApiException>(() => new CloudflareApi(new Factory(disabled)).VerifyTokenAsync("cf-token", "acc"));
        Assert.True(notSaved.TokenRefused);
    }

    // Cloudflare's verify, held until the test lets it answer (it refuses the token as a user's, so the account's verify
    // would be asked next) or the caller gives up on it.
    private sealed class HeldVerify : HttpMessageHandler
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            Asked.TrySetResult();
            await Answer.Task.WaitAsync(cancellationToken);
            return Cloudflare(HttpStatusCode.Unauthorized, 1000, message: "Invalid API Token");
        }
    }

    // cloudflared on the host's network, with a token the locator can read.
    private static Task<ProcessResult> TunnelAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"a":"0123456789abcdef0123456789abcdef","t":"11111111-2222-4333-8444-555555555555","s":"c2VjcmV0"}"""));
        const string networks = """{"host":{}}""";
        return Task.FromResult(args switch
        {
            ["ps", ..] => new ProcessResult(0,
                """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""", ""),
            ["container", "inspect", "tunnel"] => new ProcessResult(0,
                $$$"""[{"Name":"/tunnel","Args":["tunnel","run","--token","{{{token}}}"],"Config":{"Cmd":[],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{{{networks}}}}}]""", ""),
            _ => new ProcessResult(1, "", "not here"),
        });
    }

    [Fact]
    public async Task Domains_turned_off_while_the_token_is_checked_stop_the_check_before_cloudflare_hears_more()
    {
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "cf-token");
        var cloudflare = new HeldVerify();
        var cli = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() }) { Runner = TunnelAsync };
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(cloudflare)), new TunnelLocator(cli), cli,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DomainService>.Instance);

        var check = domains.CheckTokenAsync();
        await Task.WhenAny(cloudflare.Asked.Task, check).WaitAsync(Eventually.Limit);
        if (check.IsCompleted) Assert.Fail($"The check ended before Cloudflare was asked: {(await check)?.Problem}");
        await domains.SetEnabledAsync(false);
        // Were the check still on, Cloudflare's answer would send it on to the account's verify now.
        cloudflare.Answer.SetResult();

        Assert.Null(await check.WaitAsync(Eventually.Limit));
        Assert.Equal(["/client/v4/user/tokens/verify"], cloudflare.Paths);
    }
}
