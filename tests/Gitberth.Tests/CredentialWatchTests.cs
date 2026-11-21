using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Agents;
using Gitberth.Audit;
using Gitberth.Components.Shared;
using Gitberth.Credentials;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The token watch: a refusal heard from a call counts only once the service's own check refuses the token too (one
/// notice, one audit entry, a critical banner with the top bar's pill); a 401 the check doesn't confirm changes nothing;
/// taken again ends it, and one back within a day isn't notified again; a new token clears it at once and tells nothing it
/// is past; the expiry is said the moment it passes (the held clock's wait is due exactly then); Cloudflare is never asked
/// while domains are off; a restart tells nothing again and keeps a dismissal, until the banner says something else; API
/// tokens in use only; a registry's refusal is confirmed by signing in again. A token past its expiry is never checked for
/// its refusals; GitLab not saying which token it is keeps what's known of it; a save that didn't check the token keeps its
/// refusal; a page never waits for a check that waits on its service, whose answer about a token saved since is dropped.
/// Real settings, notifier (read through its queue), audit log, board, registries and API tokens; GitHub, GitLab and
/// Cloudflare stood in for.
/// </summary>
public sealed class CredentialWatchTests : IDisposable
{
    private const string Token = "github_pat_saved_one";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private sealed class Factory(Func<HttpMessageHandler> make) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(make(), disposeHandler: false);
    }

    private sealed class NoMail : IMailer
    {
        public Task SendAsync(Destination destination, MailNotice mail, CancellationToken ct) => Task.CompletedTask;
    }

    private readonly ImageKit.Db _db = new();
    private readonly SecretProtector _protector = ImageKit.Protector();
    private readonly SettingsService _settings;
    private readonly NotificationSettings _choices;
    private readonly Notifier _notifier;
    private readonly AuditLog _audit = new();
    private readonly CredentialSignals _signals = new();
    private readonly HeldClock _clock = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly RegistryService _registries;
    private readonly ApiTokens _apiTokens;
    private readonly GitberthOptions _options = new() { DataDir = Path.GetTempPath() };
    private readonly List<string> _calls = [];
    private readonly List<CredentialWatch> _made = [];

    // What GitHub, GitLab and Cloudflare answer: the test's.
    private Func<HttpRequestMessage, HttpResponseMessage> _github = _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""");
    private Func<HttpRequestMessage, HttpResponseMessage> _gitlab = r => r.RequestUri!.AbsolutePath.EndsWith("/self", StringComparison.Ordinal)
        ? Json(HttpStatusCode.OK, """{"scopes":["api"],"expires_at":null,"active":true}""")
        : Json(HttpStatusCode.OK, """{"username":"e2e-bot"}""");
    private Func<CloudflareTokenCheck?> _cloudflare = () => throw new InvalidOperationException("Cloudflare was asked about its token");

    public CredentialWatchTests()
    {
        _settings = new SettingsService(_db, _protector);
        _choices = new NotificationSettings(_settings);
        _notifier = new Notifier(_choices, new NotificationSender(new Factory(() => new Stub(_ => new HttpResponseMessage(HttpStatusCode.NoContent))), new NoMail()),
            _settings, NullLogger<Notifier>.Instance);
        _registries = new RegistryService(_db, _protector, _engine);
        _apiTokens = new ApiTokens(_db, _settings);
    }

    public void Dispose()
    {
        foreach (var watch in _made) watch.Dispose();
        _db.Dispose();
    }

    private AttentionBoard Board { get; } = new();

    // Every GitHub and GitLab call goes through the observer, as in Gitberth, and is noted ("GET /user").
    private Factory Observed(string kind, Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(() => new CredentialObserver(kind, _signals)
        {
            InnerHandler = new Stub(r =>
            {
                lock (_calls) _calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");
                return answer(r);
            }),
        });

    private CredentialWatch Watch(AttentionBoard? board = null)
    {
        var github = new GitHubApi(Observed(CredentialWatch.GitHubKey, r => _github(r)));
        var gitlab = new GitLabApi(Observed(CredentialWatch.GitLabKey, r => _gitlab(r)), _settings);
        var cli = new DockerCli(_options);
        var domains = new DomainService(_db, _settings, new CloudflareApi(new ImageKit.NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var watch = new CredentialWatch(_settings, github, gitlab, new GitLabSettings(_settings, gitlab, new GitLabIntegrationState(), _audit), domains, _registries,
            _apiTokens, _notifier, board ?? Board, _signals, NullLogger<CredentialWatch>.Instance, _audit, _clock)
        {
            CloudflareCheck = _ => Task.FromResult(_cloudflare()),
        };
        _made.Add(watch);
        return watch;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json = "{}", params (string Name, string Value)[] headers)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) answer.Headers.TryAddWithoutValidation(name, value);
        return answer;
    }

    private Task WithDestinationAsync() =>
        _choices.SaveDestinationAsync(new Destination { Id = "w1", Kind = DestinationKind.Webhook, Url = "https://notify.example.com/hook" });

    // As Settings › GitHub access saves a token GitHub took.
    private async Task SaveGitHubAsync(CredentialWatch watch, string token, DateTimeOffset? expires)
    {
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, token);
        await _settings.SetAsync(SettingKeys.GitHubLogin, "e2e-bot");
        await _settings.SetAsync(SettingKeys.GitHubTokenExpires, expires?.ToString("O", CultureInfo.InvariantCulture));
        await watch.SavedAsync(CredentialWatch.GitHubKey);
    }

    private Task<TimeSpan> LookAsync(CredentialWatch watch, bool check = false) =>
        watch.LookAsync(check, _clock.GetUtcNow() + CredentialWatch.Every, CancellationToken.None);

    // A call Gitberth makes anyway with the saved token: a deploy's commit status, answered as the test says.
    private async Task StatusAsync()
    {
        try
        {
            await new GitHubWrites(Observed(CredentialWatch.GitHubKey, r => _github(r))).SetStatusAsync(Token, "acme", "web", Sha,
                new CommitStatus("pending", "Building", "Gitberth: web", null));
        }
        catch (GitHubWriteException)
        {
            // A refused status is the deploy's business: it carries on.
        }
    }

    private List<Notice> Published()
    {
        var list = new List<Notice>();
        while (_notifier.Queued.TryRead(out var notice)) list.Add(notice);
        return list;
    }

    private List<AuditRecord> Entries(string action) => [.. _audit.Entries.Where(e => e.Action == action)];

    private int Calls(string call)
    {
        lock (_calls) return _calls.Count(c => c == call);
    }

    [Fact]
    public async Task A_refusal_heard_from_a_call_and_refused_again_by_the_check_is_said_once_everywhere()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, _clock.GetUtcNow().AddDays(60));
        Assert.Empty(Published());

        // GitHub stops taking the token: a deploy's commit status is refused, and so is the check made at once.
        _github = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        await StatusAsync();
        await LookAsync(watch);

        var notice = Assert.Single(Published());
        Assert.Equal((NotifyEvent.TokenRefused, NoticeLevel.Critical, "GitHub refused the token", "github", "", (string?)null),
            (notice.Event, notice.Level, notice.Title, notice.Credential, notice.App, notice.Container));
        Assert.StartsWith("GitHub refused the token for e2e-bot at 12:00 UTC while Gitberth was setting a commit status on acme/web, and again when "
                          + "Gitberth checked it: it has expired or was revoked. Private repos don't deploy", notice.Text);
        var entry = Assert.Single(Entries("settings.github.token.refused"));
        Assert.Equal((AuditOutcome.Failed, AuditActor.Gitberth, "GitHub token"), (entry.Outcome, entry.Actor, entry.Target));
        Assert.Equal("GitHub refused the token for e2e-bot while Gitberth was setting a commit status on acme/web, and again when it checked: "
                     + "it has expired or was revoked.", entry.Summary);
        var item = Assert.Single(Board.Items);
        Assert.Equal(("credential:github", AttentionLevel.Critical, "GitHub token refused", (string?)null), (item.Key, item.Level, item.Pill, item.Dismiss));
        Assert.Equal("settings#github", item.Href);
        Assert.Equal(1, Calls("GET /user"));
        Assert.Equal(_clock.GetUtcNow(), watch.RefusedAt(CredentialWatch.GitHubKey));

        // Refused again a minute later: nothing new is said, and GitHub isn't asked again.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await StatusAsync();
        await LookAsync(watch);
        Assert.Empty(Published());
        Assert.Single(Entries("settings.github.token.refused"));
        Assert.Equal(1, Calls("GET /user"));
    }

    [Fact]
    public async Task A_401_the_check_doesnt_confirm_changes_nothing_and_a_token_is_confirmed_at_most_once_a_minute()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, null);
        // Statuses are refused (one endpoint, or an incident), but /user takes the token.
        _github = r => r.RequestUri!.AbsolutePath == "/user" ? Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""") : Json(HttpStatusCode.Unauthorized);

        await StatusAsync();
        await LookAsync(watch);
        Assert.Equal(1, Calls("GET /user"));
        Assert.Empty(Published());
        Assert.Empty(Board.Items);
        Assert.DoesNotContain(_audit.Entries, e => e.Action.StartsWith("settings.github.token.", StringComparison.Ordinal));

        // Another one at once waits for its minute.
        await StatusAsync();
        Assert.Equal(TimeSpan.FromMinutes(1), await LookAsync(watch));
        Assert.Equal(1, Calls("GET /user"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await LookAsync(watch);
        Assert.Equal(2, Calls("GET /user"));
        Assert.Empty(Published());
        Assert.Empty(Board.Items);
    }

    [Fact]
    public async Task Taken_again_ends_the_refusal_and_one_back_within_a_day_is_shown_and_recorded_but_not_notified()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, null);
        _github = _ => Json(HttpStatusCode.Unauthorized);
        var said = await watch.CheckNowAsync(CredentialWatch.GitHubKey);
        Assert.Equal(((bool?)false, "GitHub refused the token: it has expired or was revoked. Save a new one."), (said.Took, said.Message));
        Assert.StartsWith("GitHub refused the token for e2e-bot when Gitberth checked it at 12:00 UTC: it has expired or was revoked.", Assert.Single(Published()).Text);

        // GitHub takes it again (its incident is over): the next answer to it ends the refusal.
        _github = _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""");
        _clock.Advance(TimeSpan.FromHours(2));
        await StatusAsync();
        await LookAsync(watch);
        Assert.Equal("GitHub takes the token for e2e-bot again.", Assert.Single(Entries("settings.github.token.accepted")).Summary);
        Assert.Empty(Board.Items);
        Assert.Null(watch.RefusedAt(CredentialWatch.GitHubKey));

        // Refused again 3 hours after the notice: shown and recorded, not notified again.
        _github = _ => Json(HttpStatusCode.Unauthorized);
        _clock.Advance(TimeSpan.FromHours(1));
        await watch.CheckNowAsync(CredentialWatch.GitHubKey);
        Assert.Equal(2, Entries("settings.github.token.refused").Count);
        Assert.Equal(AttentionLevel.Critical, Assert.Single(Board.Items).Level);
        Assert.Empty(Published());
    }

    [Fact]
    public async Task Saving_a_new_token_clears_a_refusal_at_once_and_tells_nothing_it_is_past()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, null);
        _github = _ => Json(HttpStatusCode.Unauthorized);
        await watch.CheckNowAsync(CredentialWatch.GitHubKey);
        Assert.Single(Published());

        // A new token, 10 days from its expiry: no refusal any more, the 3 weeks' banner, and no notice (its date is on the page).
        _github = _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""");
        await SaveGitHubAsync(watch, "github_pat_new_one", _clock.GetUtcNow().AddDays(10));
        var item = Assert.Single(Board.Items);
        Assert.Equal((AttentionLevel.Info, "The GitHub token expires in 10 days", (string?)null), (item.Level, item.Title, item.Pill));
        Assert.NotNull(item.Dismiss);
        Assert.Null(watch.RefusedAt(CredentialWatch.GitHubKey));
        await LookAsync(watch);
        Assert.Empty(Published());

        // 3 days on, it's within the week: told then.
        _clock.Advance(TimeSpan.FromDays(3));
        await LookAsync(watch);
        var notice = Assert.Single(Published());
        Assert.Equal((NotifyEvent.TokenExpiring, NoticeLevel.Warning, "The GitHub token expires in 7 days"), (notice.Event, notice.Level, notice.Title));
        Assert.Equal(_clock.GetUtcNow().AddDays(7), notice.Expires);
        Assert.Equal(AttentionLevel.Warn, Assert.Single(Board.Items).Level);
    }

    [Fact]
    public async Task The_expiry_passing_with_no_call_is_said_at_that_very_moment()
    {
        await WithDestinationAsync();
        var watch = Watch();
        var expires = _clock.GetUtcNow().AddMinutes(30);
        await SaveGitHubAsync(watch, Token, expires);
        // Saved within its last day: that warning counts as told.
        Assert.Empty(Published());

        await watch.StartAsync(CancellationToken.None);
        try
        {
            // The first check with GitHub comes 60 s after start; then the wait is for the expiry's very moment.
            var first = await _clock.NextWaitAsync();
            Assert.Equal(TimeSpan.FromSeconds(60), first.Due);
            _clock.Advance(first.Due);
            var second = await _clock.NextWaitAsync();
            Assert.Equal(expires, second.At);
            Assert.Equal(1, Calls("GET /user"));
            Assert.Empty(Published());

            _clock.Advance(second.Due);
            var third = await _clock.NextWaitAsync();
            var notice = Assert.Single(Published());
            Assert.Equal((NotifyEvent.TokenExpiring, NoticeLevel.Critical, "The GitHub token has expired"), (notice.Event, notice.Level, notice.Title));
            var entry = Assert.Single(Entries("settings.github.token.expired"));
            Assert.Equal("The GitHub token for e2e-bot expired on Nov 5, 2026: private repos don't deploy until a new one is saved.", entry.Summary);
            Assert.Equal("2026-11-05", Assert.Single(entry.Changes).To);
            var item = Assert.Single(Board.Items);
            Assert.Equal((AttentionLevel.Critical, "GitHub token expired"), (item.Level, item.Pill));
            // Nothing more is due: the watch looks again on its tick.
            Assert.Equal(CredentialWatch.Tick, third.Due);
        }
        finally
        {
            await watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cloudflare_is_never_asked_while_domains_are_off_and_its_verify_counts_while_they_are_on()
    {
        await WithDestinationAsync();
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "cf-token-saved");
        var watch = Watch();
        // Off: the stand-in throws if it's asked at all.
        await LookAsync(watch, check: true);
        Assert.Empty(Board.Items);
        Assert.Empty(Published());
        Assert.Equal(DomainService.TurnedOff, (await watch.CheckNowAsync(CredentialWatch.CloudflareKey)).Message);

        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        var expires = _clock.GetUtcNow().AddDays(10);
        _cloudflare = () => new CloudflareTokenCheck(false, "active", expires, null);
        await LookAsync(watch, check: true);
        Assert.Equal(expires, DateTimeOffset.Parse(await _settings.GetAsync(CredentialKeys.CloudflareTokenExpires) ?? "", CultureInfo.InvariantCulture));
        var warned = Assert.Single(Published());
        Assert.Equal((NoticeLevel.Info, "The Cloudflare API token expires in 10 days", "cloudflare"), (warned.Level, warned.Title, warned.Credential));
        Assert.Equal(AttentionLevel.Info, Assert.Single(Board.Items).Level);

        // Disabled in Cloudflare: refused at the next check.
        _cloudflare = () => new CloudflareTokenCheck(true, "disabled", expires, "Cloudflare says Gitberth's API token is disabled. Save a new one in Settings › Domains.");
        _clock.Advance(TimeSpan.FromHours(6));
        await LookAsync(watch, check: true);
        var refused = Assert.Single(Published());
        Assert.Equal((NotifyEvent.TokenRefused, "Cloudflare refused Gitberth's API token"), (refused.Event, refused.Title));
        Assert.Contains(": it has expired, or was disabled or deleted. Gitberth can't add, move or remove domains", refused.Text, StringComparison.Ordinal);
        Assert.Single(Entries("domains.token.refused"));
        Assert.Equal("Cloudflare token refused", Assert.Single(Board.Items).Pill);

        // Active again, and Check the token: taken.
        _cloudflare = () => new CloudflareTokenCheck(false, "active", expires, null);
        var said = await watch.CheckNowAsync(CredentialWatch.CloudflareKey);
        Assert.Equal(((bool?)true, "Cloudflare took the token, and it expires on Nov 15, 2026."), (said.Took, said.Message));
        Assert.Equal("Cloudflare takes Gitberth's API token again.", Assert.Single(Entries("domains.token.accepted")).Summary);
        Assert.Equal(AttentionLevel.Info, Assert.Single(Board.Items).Level);
        Assert.Empty(Published());
    }

    [Fact]
    public async Task A_restart_tells_nothing_again_and_keeps_a_dismissal()
    {
        await WithDestinationAsync();
        // A token found 5 days from its expiry, not saved through the page (the first look after an upgrade): its
        // week's warning, once.
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, Token);
        await _settings.SetAsync(SettingKeys.GitHubTokenExpires, _clock.GetUtcNow().AddDays(5).ToString("O", CultureInfo.InvariantCulture));
        var watch = Watch();
        await LookAsync(watch);
        Assert.Equal("The GitHub token expires in 5 days", Assert.Single(Published()).Title);
        Assert.True(Board.Dismiss(CredentialWatch.BoardKey(CredentialWatch.GitHubKey)));
        await Eventually.UntilAsync(async () => (await _settings.GetAsync(CredentialKeys.State))?.Contains("\"dismissed\"", StringComparison.Ordinal) == true,
            "the dismissal to be kept");

        // Gitberth restarts: a new board, a new watch, the same settings.
        var board = new AttentionBoard();
        var again = Watch(board);
        await LookAsync(again);
        Assert.Empty(Published());
        Assert.Single(board.Items);
        Assert.Empty(board.Shown);

        // Its next threshold shows it again, and tells it.
        _clock.Advance(TimeSpan.FromDays(4.25));
        await LookAsync(again);
        Assert.Equal("The GitHub token expires tomorrow", Assert.Single(Published()).Title);
        Assert.Single(board.Shown);
    }

    [Fact]
    public async Task An_api_token_in_use_is_warned_a_week_ahead_and_one_nobody_uses_never()
    {
        await WithDestinationAsync();
        var now = _clock.GetUtcNow().UtcDateTime;
        var used = await ApiTokenAsync("Claude Code", now.AddDays(6), now.AddDays(-1), "192.0.2.10");
        await ApiTokenAsync("Nightly report", now.AddDays(6), now.AddDays(-40), "192.0.2.11");
        await ApiTokenAsync("Not used yet", now.AddDays(3), null, null);
        var watch = Watch();

        await LookAsync(watch);

        var notice = Assert.Single(Published());
        Assert.Equal((NoticeLevel.Warning, "The API token Claude Code expires in 6 days", "api-token:Claude Code"), (notice.Level, notice.Title, notice.Credential));
        Assert.Contains("It was last used on Nov 4 from 192.0.2.10: whatever uses it can't reach Gitberth after that.", notice.Text, StringComparison.Ordinal);
        var item = Assert.Single(Board.Items);
        Assert.Equal((CredentialWatch.BoardKey(CredentialWatch.ApiTokenPrefix + used), AttentionLevel.Warn, "settings#agents"), (item.Key, item.Level, item.Href));

        // Revoked: warned about no more.
        Assert.True(await _apiTokens.RevokeAsync(used));
        await LookAsync(watch);
        Assert.Empty(Board.Items);
    }

    private async Task<int> ApiTokenAsync(string name, DateTime expires, DateTime? lastUsed, string? ip)
    {
        var id = (await _apiTokens.CreateAsync(name, ApiAccess.Read, 30)).Token!.Id;
        await using var db = _db.CreateDbContext();
        await db.ApiTokens.Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, expires).SetProperty(t => t.LastUsedAt, lastUsed).SetProperty(t => t.LastUsedIp, ip));
        return id;
    }

    [Fact]
    public async Task A_registrys_refusal_is_confirmed_by_signing_in_again_and_gone_once_its_entered_again()
    {
        await WithDestinationAsync();
        var saved = await _registries.SaveAsync(new RegistryInput(null, "ghcr.io", "e2e-bot", "ghp_registry_secret"));
        Assert.True(saved.Ok);
        var watch = Watch();
        await LookAsync(watch);
        var heard = new CredentialAnswer("registry:ghcr.io", null, CredentialVerdict.Refused, CredentialText.RegistryDoing("ghcr.io/acme/web:latest"));

        // An update check is turned away, but signing in again works: nothing to say.
        _signals.Report(heard);
        await LookAsync(watch);
        Assert.Empty(Board.Items);
        Assert.Equal(2, _engine.Calls.Count(c => c == "auth ghcr.io"));

        // Refused both times: said.
        _engine.Auth = _ => new DockerApiException(401, "unauthorized: incorrect username or password");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _signals.Report(heard);
        await LookAsync(watch);
        var notice = Assert.Single(Published());
        Assert.Equal((NotifyEvent.TokenRefused, "ghcr.io refused the sign-in", "registry:ghcr.io"), (notice.Event, notice.Title, notice.Credential));
        Assert.StartsWith("ghcr.io refused the sign-in for e2e-bot at 12:01 UTC when Gitberth checked ghcr.io/acme/web:latest for an update, and again when "
                          + "Gitberth signed in with it: its password or token has expired or was revoked.", notice.Text);
        Assert.Equal("ghcr.io", Assert.Single(Entries("settings.registry.refused")).Target);
        Assert.Equal(_clock.GetUtcNow(), watch.RefusedAt("registry:ghcr.io"));
        Assert.Equal("ghcr.io sign-in refused", Assert.Single(Board.Items).Pill);

        // Entered again: the refusal goes at once.
        _engine.Auth = _ => null;
        Assert.True((await _registries.SaveAsync(new RegistryInput(saved.Id, "ghcr.io", "e2e-bot", "ghp_new_secret"))).Ok);
        await watch.SavedAsync("registry:ghcr.io");
        Assert.Empty(Board.Items);
        Assert.Null(watch.RefusedAt("registry:ghcr.io"));
    }

    [Fact]
    public async Task An_update_check_tells_what_the_registry_said_of_its_saved_sign_in()
    {
        var heard = new List<CredentialAnswer>();
        _signals.Answered += a =>
        {
            lock (heard) heard.Add(a);
        };
        Assert.True((await _registries.SaveAsync(new RegistryInput(null, "ghcr.io", "e2e-bot", "ghp_registry_secret"))).Ok);
        var updates = new UpdateService(new ImageKit.UpdateSteps(_db), ImageKit.Images(_engine, _db, _protector), _db, NullLogger<UpdateService>.Instance, signals: _signals);
        var reference = ImageReference.Parse("ghcr.io/acme/web:latest")!;

        _engine.Distribution[reference.Canonical] = () => throw new DockerApiException(401, "unauthorized: authentication required");
        await updates.CheckReferenceAsync(reference, null, CancellationToken.None);
        _engine.Distribution[reference.Canonical] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();
        await updates.CheckReferenceAsync(reference, null, CancellationToken.None);

        Assert.Equal([(CredentialVerdict.Refused, "when Gitberth checked ghcr.io/acme/web:latest for an update"), (CredentialVerdict.Accepted, (string?)null)],
            heard.Select(a => (a.Verdict, a.Doing)));
        Assert.All(heard, a => Assert.Equal(("registry:ghcr.io", (string?)null), (a.Kind, a.TokenHash)));
    }

    [Fact]
    public async Task GitLabs_check_says_its_own_word_and_keeps_who_the_token_is()
    {
        await WithDestinationAsync();
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-saved");
        await _settings.SetAsync(SettingKeys.GitLabLogin, "e2e-bot");
        var watch = Watch();
        await watch.SavedAsync(CredentialWatch.GitLabKey);
        const string revoked = "Token was revoked. You have to re-authorize from the user.";
        _gitlab = _ => Json(HttpStatusCode.Unauthorized, "{}", ("WWW-Authenticate", $"Bearer realm=\"\", error=\"invalid_token\", error_description=\"{revoked}\""));

        var said = await watch.CheckNowAsync(CredentialWatch.GitLabKey);

        Assert.Equal(((bool?)false, "GitLab says this token is invalid, expired or revoked."), (said.Took, said.Message));
        var notice = Assert.Single(Published());
        Assert.Equal(("GitLab refused the token", "gitlab"), (notice.Title, notice.Credential));
        Assert.StartsWith("GitLab refused the token for e2e-bot when Gitberth checked it at 12:00 UTC: GitLab says it was revoked. Private projects don't deploy", notice.Text);
        Assert.Single(Entries("settings.gitlab.token.refused"));
        // A refusal says nothing new about who the token is.
        Assert.Equal("e2e-bot", await _settings.GetAsync(SettingKeys.GitLabLogin));
    }

    private static DateTimeOffset? DateOf(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    [Fact]
    public async Task GitLab_failing_to_say_which_token_it_is_keeps_its_known_expiry_and_scopes_and_tells_nothing_twice()
    {
        await WithDestinationAsync();
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-saved");
        await _settings.SetAsync(SettingKeys.GitLabLogin, "e2e-bot");
        await _settings.SetAsync(SettingKeys.GitLabScopes, "api");
        var full = _gitlab = r => r.RequestUri!.AbsolutePath.EndsWith("/self", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"scopes":["api"],"expires_at":"2026-11-15","active":true}""")
            : Json(HttpStatusCode.OK, """{"username":"e2e-bot"}""");
        var watch = Watch();
        // The check learns its expiry, 10 days away: the 3 weeks' warning, once.
        await LookAsync(watch, check: true);
        var expires = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expires, DateOf(await _settings.GetAsync(SettingKeys.GitLabTokenExpires)));
        Assert.Single(Published());

        // Check the token: GitLab says who it is, but which token it is (its scopes, its expiry) fails for a moment.
        _gitlab = r => r.RequestUri!.AbsolutePath.EndsWith("/self", StringComparison.Ordinal)
            ? Json(HttpStatusCode.ServiceUnavailable, """{"message":"503 Service Unavailable"}""")
            : Json(HttpStatusCode.OK, """{"username":"e2e-bot"}""");
        var said = await watch.CheckNowAsync(CredentialWatch.GitLabKey);
        Assert.True(said.Took);
        Assert.Equal(expires, DateOf(await _settings.GetAsync(SettingKeys.GitLabTokenExpires)));
        Assert.Equal("api", await _settings.GetAsync(SettingKeys.GitLabScopes));
        Assert.Equal(AttentionLevel.Info, Assert.Single(Board.Items).Level);

        // Its expiry forgotten meanwhile (a check elsewhere that couldn't say), then said again: the date the warning
        // was told for, so nothing is told again.
        await _settings.SetAsync(SettingKeys.GitLabTokenExpires, null);
        await LookAsync(watch);
        _gitlab = full;
        _clock.Advance(CredentialWatch.Every);
        await LookAsync(watch, check: true);
        Assert.Equal(expires, DateOf(await _settings.GetAsync(SettingKeys.GitLabTokenExpires)));
        Assert.Empty(Published());
        Assert.Equal(AttentionLevel.Info, Assert.Single(Board.Items).Level);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_refused_as_it_should_be_and_never_checked_for_that()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, _clock.GetUtcNow().AddMinutes(30));
        _clock.Advance(TimeSpan.FromHours(1));
        await LookAsync(watch);
        Assert.Equal("The GitHub token has expired", Assert.Single(Published()).Title);

        // Deploys still use it, and GitHub refuses it, as it should: that's its expiry, said already. No check is made for
        // it, at once or a minute later, and nothing new is said.
        _github = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        for (var i = 0; i < 3; i++)
        {
            await StatusAsync();
            Assert.Equal(CredentialWatch.Tick, await LookAsync(watch));
            _clock.Advance(CredentialWatch.ConfirmEvery);
        }
        Assert.Equal(0, Calls("GET /user"));
        Assert.Empty(Published());
        Assert.Empty(Entries("settings.github.token.refused"));
        Assert.Equal("GitHub token expired", Assert.Single(Board.Items).Pill);
    }

    [Fact]
    public async Task Saving_or_checking_a_token_never_waits_for_a_check_that_waits_on_its_service()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, null);
        // The periodic check's /user hangs (GitHub slow), and then refuses the token it was asked about; any later call is answered at once.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        _github = r =>
        {
            if (r.RequestUri!.AbsolutePath != "/user" || Interlocked.Exchange(ref first, 1) == 1) return Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""");
            asked.TrySetResult();
            release.Task.Wait(Eventually.Limit);
            return Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        };
        var look = Task.Run(() => LookAsync(watch, check: true));
        await asked.Task.WaitAsync(Eventually.Limit);

        // Meanwhile Settings saves a new token, and checks it: neither waits for the look.
        await SaveGitHubAsync(watch, "github_pat_new_one", null).WaitAsync(Eventually.Limit);
        var said = await watch.CheckNowAsync(CredentialWatch.GitHubKey).WaitAsync(Eventually.Limit);
        Assert.True(said.Took);
        var checkedAt = watch.CheckedAt(CredentialWatch.GitHubKey);
        Assert.NotNull(checkedAt);

        // The look's answer was about the token before: it changes nothing now.
        release.SetResult();
        await look.WaitAsync(Eventually.Limit);
        Assert.Null(watch.RefusedAt(CredentialWatch.GitHubKey));
        Assert.Empty(Entries("settings.github.token.refused"));
        Assert.Empty(Published());
        Assert.Empty(Board.Items);
        Assert.Equal(checkedAt, watch.CheckedAt(CredentialWatch.GitHubKey));
    }

    [Fact]
    public async Task Saving_gitlabs_address_keeps_a_refusal_unless_the_saves_own_check_took_the_token()
    {
        await WithDestinationAsync();
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-saved");
        await _settings.SetAsync(SettingKeys.GitLabLogin, "e2e-bot");
        var watch = Watch();
        await watch.SavedAsync(CredentialWatch.GitLabKey);
        _gitlab = _ => Json(HttpStatusCode.Unauthorized);
        await watch.CheckNowAsync(CredentialWatch.GitLabKey);
        var refusedAt = watch.RefusedAt(CredentialWatch.GitLabKey);
        Assert.NotNull(refusedAt);
        Assert.Single(Published());

        // Another address saved, the same token: what the save's own check said of it decides (GitLabSection.Took).
        GitLabSave Saved(GitLabTokenCheck? check) => new(true, "Saved: Gitberth's GitLab is gitlab.example.com.", check);
        Assert.Null(GitLabSection.Took(Saved(null)));
        Assert.Null(GitLabSection.Took(Saved(new GitLabTokenCheck(true, null, null, null, "gitlab.example.com couldn't be reached."))));
        Assert.False(GitLabSection.Took(Saved(new GitLabTokenCheck(false, null, null, null, "GitLab says this token is invalid, expired or revoked."))));
        Assert.True(GitLabSection.Took(Saved(new GitLabTokenCheck(true, "e2e-bot", ["api"], null, null))));

        // It said nothing of the token: the refusal stays, critical, with its pill.
        await watch.SavedAsync(CredentialWatch.GitLabKey);
        Assert.Equal(refusedAt, watch.RefusedAt(CredentialWatch.GitLabKey));
        Assert.Equal(("GitLab token refused", AttentionLevel.Critical), (Assert.Single(Board.Items).Pill, Board.Items[0].Level));
        // Refused there too: the same refusal, said once.
        await watch.SavedAsync(CredentialWatch.GitLabKey, took: false);
        Assert.Equal(refusedAt, watch.RefusedAt(CredentialWatch.GitLabKey));
        Assert.Single(Entries("settings.gitlab.token.refused"));
        // Taken there: the refusal ends, and the audit log says so.
        await watch.SavedAsync(CredentialWatch.GitLabKey, took: true);
        Assert.Null(watch.RefusedAt(CredentialWatch.GitLabKey));
        Assert.Single(Entries("settings.gitlab.token.accepted"));
        Assert.Empty(Board.Items);
        Assert.Empty(Published());
    }

    [Fact]
    public async Task A_dismissal_ends_once_the_banner_says_something_else_and_a_restart_never_hides_it_again()
    {
        await WithDestinationAsync();
        var watch = Watch();
        await SaveGitHubAsync(watch, Token, _clock.GetUtcNow().AddDays(10));
        Assert.True(Board.Dismiss(CredentialWatch.BoardKey(CredentialWatch.GitHubKey)));
        await Eventually.UntilAsync(async () => (await _settings.GetAsync(CredentialKeys.State))?.Contains("\"dismissed\"", StringComparison.Ordinal) == true,
            "the dismissal to be kept");

        // Refused, then taken again within the same 3 weeks: the banner it dismissed shows again, and the dismissal is gone.
        _github = _ => Json(HttpStatusCode.Unauthorized);
        await watch.CheckNowAsync(CredentialWatch.GitHubKey);
        _github = _ => Json(HttpStatusCode.OK, """{"login":"e2e-bot"}""");
        await watch.CheckNowAsync(CredentialWatch.GitHubKey);
        Assert.Equal(AttentionLevel.Info, Assert.Single(Board.Shown).Level);
        Assert.DoesNotContain("\"dismissed\"", await _settings.GetAsync(CredentialKeys.State) ?? "", StringComparison.Ordinal);

        // Gitberth restarts: it shows as it did before.
        var board = new AttentionBoard();
        await LookAsync(Watch(board));
        Assert.Equal(AttentionLevel.Info, Assert.Single(board.Shown).Level);
    }
}
