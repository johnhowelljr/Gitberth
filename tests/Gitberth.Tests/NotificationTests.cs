using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Notifications;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Notifications: what each destination is sent and how a refusal reads, what Settings keeps (and keeps secret), which
/// deploys and container events are told, and that a crash loop says so once and then counts. Nothing leaves the test: a
/// stand-in answers every request and a fake takes the mail.
/// </summary>
public sealed class NotificationTests : IDisposable
{
    private sealed class Endpoint : HttpMessageHandler
    {
        private readonly List<(HttpRequestMessage Request, string Body)> _calls = [];
        public Func<HttpRequestMessage, int, HttpResponseMessage>? Respond { get; set; }

        public List<(HttpRequestMessage Request, string Body)> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            int n;
            lock (_calls)
            {
                _calls.Add((request, body));
                n = _calls.Count;
            }
            return Respond?.Invoke(request, n) ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Mailer : IMailer
    {
        public List<(Destination To, MailNotice Mail)> Sent { get; } = [];
        public Exception? Throw { get; set; }

        public Task SendAsync(Destination destination, MailNotice mail, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Sent.Add((destination, mail));
            return Task.CompletedTask;
        }
    }

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly NotificationSettings _choices;
    private readonly Endpoint _endpoint = new();
    private readonly Mailer _mailer = new();
    private readonly NotificationSender _sender;
    private readonly Notifier _notifier;
    private readonly int _appId;

    private static readonly Destination Discord = new()
    {
        Id = "d1", Kind = DestinationKind.Discord, Name = "Ops", Url = "https://discord.com/api/webhooks/123/SECRETPART",
    };

    private static readonly Destination Email = new()
    {
        Id = "m1", Kind = DestinationKind.Email, SmtpHost = "smtp.example.com", SmtpPort = 587, SmtpUser = "me@example.com", SmtpPassword = "app-pass",
        From = "gitberth@example.com", To = "me@example.com, ops@example.com",
    };

    public NotificationTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "web", Slug = "web", Owner = "acme", Repo = "web", CurrentImage = "gitberth/web:abc1234" };
            app.Domains.Add(new CustomDomain { Hostname = "web.example.com" });
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _choices = new NotificationSettings(_settings);
        _sender = new NotificationSender(new Factory(_endpoint), _mailer);
        _notifier = new Notifier(_choices, _sender, _settings, NullLogger<Notifier>.Instance) { RetryAfter = TimeSpan.Zero };
    }

    public void Dispose() => _connection.Dispose();

    private static Notice Sample(NotifyEvent e = NotifyEvent.DeployFailed, string? container = "gb-web", bool preview = false) =>
        new(e, NoticeLevel.Critical, "Deploy of web failed", "Deploy #5 (New commit, commit abc1234) failed. npm ci exited with 1",
            "http://192.168.50.10:8080/apps/1/deployments/5", "web", container, new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc))
        { Preview = preview, AppId = 1, DeploymentId = 5, Commit = "abc1234def", Trigger = "Poll" };

    private List<Notice> Published()
    {
        var list = new List<Notice>();
        while (_notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    // ---- What a destination may be ----

    [Theory]
    [InlineData(465, "Port 465")]
    [InlineData(0, "1 to 65535")]
    public void A_mail_server_port_that_cant_work_is_refused(int port, string said) =>
        Assert.Contains(said, DestinationText.Problem(Email with { SmtpPort = port }));

    [Fact]
    public void A_mail_destination_needs_a_server_a_sender_and_good_addresses()
    {
        Assert.Null(DestinationText.Problem(Email));
        Assert.Contains("mail server", DestinationText.Problem(Email with { SmtpHost = "" }));
        Assert.Contains("only the mail server's name", DestinationText.Problem(Email with { SmtpHost = "smtp://smtp.example.com" }));
        Assert.Contains("comes from", DestinationText.Problem(Email with { From = "nobody" }));
        Assert.Contains("at least one address", DestinationText.Problem(Email with { To = " , " }));
        Assert.Contains("isn't a mail address", DestinationText.Problem(Email with { To = "me@example.com, oops" }));
        Assert.Contains("password for the user name", DestinationText.Problem(Email with { SmtpPassword = null }));
        Assert.Null(DestinationText.Problem(Email with { SmtpUser = null, SmtpPassword = null, Security = SmtpSecurity.None, SmtpPort = 25 }));
    }

    [Fact]
    public void A_web_destination_needs_its_kind_of_url()
    {
        Assert.Null(DestinationText.Problem(Discord));
        Assert.Contains("/api/webhooks/", DestinationText.Problem(Discord with { Url = "https://discord.com/channels/1/2" }));
        Assert.Contains("https://", DestinationText.Problem(Discord with { Url = "http://discord.com/api/webhooks/1/x" }));
        Assert.Null(DestinationText.Problem(new Destination { Kind = DestinationKind.Slack, Url = "https://hooks.slack.com/services/T/B/X" }));
        Assert.Contains("topic", DestinationText.Problem(new Destination { Kind = DestinationKind.Ntfy, Url = "https://ntfy.sh/" }));
        // A LAN ntfy server or webhook may be plain http.
        Assert.Null(DestinationText.Problem(new Destination { Kind = DestinationKind.Ntfy, Url = "http://192.168.1.5:8090/alerts" }));
        Assert.Null(DestinationText.Problem(new Destination { Kind = DestinationKind.Webhook, Url = "http://homeassistant.local:8123/api/webhook/x" }));
        Assert.Contains("Token field", DestinationText.Problem(new Destination { Kind = DestinationKind.Webhook, Url = "https://me:pw@example.com/hook" }));
        Assert.Contains("Enter", DestinationText.Problem(new Destination { Kind = DestinationKind.Webhook, Url = "not a url" }));
    }

    [Fact]
    public void Where_a_destination_goes_is_shown_without_its_secret()
    {
        Assert.Equal("https://discord.com/…", DestinationText.Target(Discord));
        Assert.DoesNotContain("SECRET", DestinationText.Target(Discord));
        Assert.Equal("ntfy.sh/gi••••", DestinationText.Target(new Destination { Kind = DestinationKind.Ntfy, Url = "https://ntfy.sh/gitberth-x7k2" }));
        Assert.Equal("me@example.com, ops@example.com via smtp.example.com", DestinationText.Target(Email));
    }

    // ---- What Settings keeps ----

    [Fact]
    public async Task Destinations_are_saved_encrypted_and_read_back()
    {
        await _choices.SaveDestinationAsync(Discord);
        await _choices.SaveDestinationAsync(Email);
        var raw = await _settings.GetAsync(SettingKeys.NotifyDestinations);
        Assert.NotNull(raw);
        Assert.DoesNotContain("SECRETPART", raw);
        Assert.DoesNotContain("app-pass", raw);

        var again = new NotificationSettings(_settings);
        await again.LoadAsync();
        Assert.Equal(new[] { "d1", "m1" }, again.Destinations.Select(d => d.Id));
        Assert.Equal(Discord.Url, again.Destinations[0].Url);
        Assert.Equal("app-pass", again.Destinations[1].SmtpPassword);
        Assert.Equal("me@example.com, ops@example.com", again.Destinations[1].To);
    }

    [Fact]
    public async Task Saving_replaces_by_id_drops_what_the_kind_doesnt_use_and_refuses_a_bad_one()
    {
        await _choices.SaveDestinationAsync(Discord with { Token = "leftover", SmtpHost = "x" });
        Assert.Null(_choices.Destinations.Single().Token);
        Assert.Null(_choices.Destinations.Single().SmtpHost);
        await _choices.SaveDestinationAsync(Discord with { Name = "  Team  " });
        Assert.Equal("Team", _choices.Destinations.Single().Name);
        await Assert.ThrowsAsync<ArgumentException>(() => _choices.SaveDestinationAsync(Discord with { Id = "d2", Url = "https://example.com/x" }));
        Assert.Single(_choices.Destinations);
    }

    [Fact]
    public async Task At_most_ten_destinations()
    {
        for (var i = 0; i < NotificationSettings.MaxDestinations; i++) await _choices.SaveDestinationAsync(Discord with { Id = $"d{i}" });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _choices.SaveDestinationAsync(Discord with { Id = "one-more" }));
        Assert.Contains("at most 10", ex.Message);
        // Editing one that's there still works.
        await _choices.SaveDestinationAsync(Discord with { Id = "d3", Name = "Edited" });
    }

    [Fact]
    public async Task Failures_are_on_by_default_and_the_choices_are_kept()
    {
        Assert.True(_choices.IsOn(NotifyEvent.DeployFailed));
        Assert.True(_choices.IsOn(NotifyEvent.Crashed));
        Assert.True(_choices.IsOn(NotifyEvent.Unhealthy));
        Assert.True(_choices.IsOn(NotifyEvent.Healthy));
        Assert.False(_choices.IsOn(NotifyEvent.DeploySucceeded));

        await _choices.SetEventAsync(NotifyEvent.DeploySucceeded, true);
        await _choices.SetEventAsync(NotifyEvent.Unhealthy, false);
        await _choices.SetPreviewsAsync(true);
        await _choices.SetMutedAsync("sample-redis", true);
        var again = new NotificationSettings(_settings);
        await again.LoadAsync();
        Assert.True(again.IsOn(NotifyEvent.DeploySucceeded));
        // Passing again goes with failing.
        Assert.False(again.IsOn(NotifyEvent.Healthy));
        Assert.True(again.Previews);
        Assert.True(again.IsMuted("sample-redis"));
        Assert.False(again.IsMuted("gb-web"));

        await again.SetMutedAsync("sample-redis", false);
        Assert.Null(await _settings.GetAsync(SettingKeys.NotifyMuted));
        await Assert.ThrowsAsync<ArgumentException>(() => again.SetEventAsync(NotifyEvent.Test, false));
    }

    [Fact]
    public void Settings_that_cant_be_read_fall_back_to_the_defaults()
    {
        Assert.Empty(NotificationSettings.ParseDestinations("not json"));
        Assert.True(NotificationSettings.DefaultEvents.SetEquals(NotificationSettings.ParseEvents("{oops")));
        Assert.Equal(new[] { NotifyEvent.Crashed }, NotificationSettings.ParseEvents("""["Crashed","Nonsense","Test"]"""));
        Assert.Empty(NotificationSettings.ParseMuted("[]"));
    }

    // ---- What each destination is sent ----

    [Fact]
    public void Discord_gets_an_embed_that_never_pings_anyone()
    {
        var sent = NotificationFormats.Discord(Sample() with { Title = "@everyone look" });
        var json = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("Gitberth", json.GetProperty("username").GetString());
        Assert.Equal(0, json.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        var embed = json.GetProperty("embeds")[0];
        Assert.Equal("@everyone look", embed.GetProperty("title").GetString());
        Assert.Equal("http://192.168.50.10:8080/apps/1/deployments/5", embed.GetProperty("url").GetString());
        Assert.Equal(0xDA3633, embed.GetProperty("color").GetInt32());
        Assert.Equal("2026-11-12T06:00:00Z", embed.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void Slack_gets_escaped_text_with_the_link()
    {
        var sent = NotificationFormats.Slack(Sample() with { Text = "<!channel> & <http://evil|x>" });
        var text = JsonDocument.Parse(sent.Body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("&lt;!channel&gt; &amp; &lt;http://evil|x&gt;", text);
        Assert.Contains("<http://192.168.50.10:8080/apps/1/deployments/5|Open in Gitberth>", text);
        Assert.StartsWith(":rotating_light: *Deploy of web failed*", text);
    }

    [Fact]
    public void Ntfy_gets_the_text_and_its_headers()
    {
        var sent = NotificationFormats.Ntfy(Sample(), "tk_abc");
        Assert.Equal(Sample().Text, sent.Body);
        var h = sent.Headers.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("Deploy of web failed", h["Title"]);
        Assert.Equal("4", h["Priority"]);
        Assert.Equal("rotating_light", h["Tags"]);
        Assert.Equal("http://192.168.50.10:8080/apps/1/deployments/5", h["Click"]);
        Assert.Equal("Bearer tk_abc", h["Authorization"]);
        // Anything but ASCII goes RFC 2047-encoded, which ntfy reads.
        var encoded = NotificationFormats.Ntfy(Sample() with { Title = "web · PR #12 is live" }, null).Headers.First(x => x.Key == "Title").Value;
        Assert.Equal("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("web · PR #12 is live")) + "?=", encoded);
        Assert.DoesNotContain(NotificationFormats.Ntfy(Sample(), null).Headers, x => x.Key == "Authorization");
    }

    [Fact]
    public void A_webhook_gets_everything_as_json()
    {
        var sent = NotificationFormats.Webhook(Sample(), "s3cret");
        var json = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("deploy.failed", json.GetProperty("event").GetString());
        Assert.Equal("critical", json.GetProperty("level").GetString());
        Assert.Equal("web", json.GetProperty("app").GetString());
        Assert.Equal("gb-web", json.GetProperty("container").GetString());
        Assert.Equal(5, json.GetProperty("deployment").GetInt32());
        Assert.Equal("abc1234def", json.GetProperty("commit").GetString());
        Assert.Equal("Poll", json.GetProperty("trigger").GetString());
        Assert.False(json.GetProperty("preview").GetBoolean());
        Assert.Contains(sent.Headers, h => h.Key == "Authorization" && h.Value == "Bearer s3cret");
        Assert.Contains(sent.Headers, h => h.Key == "X-Gitberth-Event" && h.Value == "deploy.failed");
    }

    [Fact]
    public void A_mail_has_the_title_as_its_subject_and_the_link_in_its_body()
    {
        var mail = NotificationFormats.Mail(Sample() with { Title = "Deploy of web\nfailed" });
        Assert.Equal("[Gitberth] Deploy of web failed", mail.Subject);
        Assert.Contains("npm ci exited with 1", mail.Body);
        Assert.Contains("http://192.168.50.10:8080/apps/1/deployments/5", mail.Body);
        Assert.Contains("Settings › Notifications", mail.Body);
    }

    // ---- Sending ----

    [Fact]
    public async Task A_web_destination_is_posted_to_with_its_headers()
    {
        var ntfy = new Destination { Kind = DestinationKind.Ntfy, Url = "https://ntfy.example.com/ops", Token = "tk_1" };
        await _sender.SendAsync(ntfy, Sample(), default);
        var (request, body) = Assert.Single(_endpoint.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example.com/ops", request.RequestUri!.ToString());
        Assert.Equal("Bearer tk_1", request.Headers.Authorization!.ToString());
        Assert.Equal("Deploy of web failed", request.Headers.GetValues("Title").Single());
        Assert.Equal(Sample().Text, body);
    }

    [Theory]
    [InlineData(404, "webhook was deleted, or the URL is wrong", false)]
    [InlineData(401, "the webhook was deleted or its URL is wrong", false)]
    [InlineData(429, "too many", true)]
    [InlineData(502, "had a problem (502)", true)]
    [InlineData(400, "didn't take it (400", false)]
    public async Task A_refusal_is_said_in_words_without_the_url(int status, string said, bool transient)
    {
        _endpoint.Respond = (_, _) => new HttpResponseMessage((HttpStatusCode)status);
        var ex = await Assert.ThrowsAsync<NotifyException>(() => _sender.SendAsync(Discord, Sample(), default));
        Assert.Contains(said, ex.Message);
        Assert.StartsWith("Discord", ex.Message);
        Assert.DoesNotContain("SECRETPART", ex.Message);
        Assert.Equal(transient, ex.Transient);
    }

    [Fact]
    public async Task A_mail_goes_through_the_mailer_and_its_refusals_are_said()
    {
        await _sender.SendAsync(Email, Sample(), default);
        Assert.Equal("[Gitberth] Deploy of web failed", Assert.Single(_mailer.Sent).Mail.Subject);

        _mailer.Throw = new SmtpException(SmtpStatusCode.ServiceNotAvailable, "Try later");
        var busy = await Assert.ThrowsAsync<NotifyException>(() => _sender.SendAsync(Email, Sample(), default));
        Assert.True(busy.Transient);
        _mailer.Throw = new SmtpException(SmtpStatusCode.MailboxUnavailable, "5.7.8 Username and Password not accepted; authentication failed");
        var refused = await Assert.ThrowsAsync<NotifyException>(() => _sender.SendAsync(Email, Sample(), default));
        Assert.False(refused.Transient);
        Assert.Contains("refused the sign-in", refused.Message);
        Assert.DoesNotContain("app-pass", refused.Message);
    }

    // ---- The queue ----

    [Fact]
    public async Task Nothing_is_queued_without_a_destination_or_when_settings_dont_want_it()
    {
        Assert.False(_notifier.Publish(Sample()));
        await _choices.SaveDestinationAsync(Discord);
        Assert.True(_notifier.Publish(Sample()));
        Assert.False(_notifier.Publish(Sample(NotifyEvent.DeploySucceeded)));
        Assert.False(_notifier.Publish(Sample(preview: true)));
        await _choices.SetMutedAsync("gb-web", true);
        Assert.False(_notifier.Publish(Sample()));
        Assert.True(_notifier.Publish(Sample(container: "gb-api")));
        await _choices.SetPreviewsAsync(true);
        Assert.True(_notifier.Publish(Sample(container: "gb-api", preview: true)));
        Assert.Equal(3, Published().Count);
    }

    [Fact]
    public async Task A_busy_destination_gets_a_second_try_and_its_delivery_is_kept()
    {
        _endpoint.Respond = (_, n) => new HttpResponseMessage(n == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.NoContent);
        await _notifier.DeliverAsync(Discord, Sample(), default);
        Assert.Equal(2, _endpoint.Calls.Count);
        var last = _notifier.LastFor("d1");
        Assert.True(last!.Ok);
        Assert.Equal("Deploy of web failed", last.Title);
    }

    [Fact]
    public async Task A_refusing_destination_isnt_tried_again_and_says_why()
    {
        _endpoint.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        await _notifier.DeliverAsync(Discord, Sample(), default);
        Assert.Single(_endpoint.Calls);
        var last = _notifier.LastFor("d1")!;
        Assert.False(last.Ok);
        Assert.Contains("404", last.Error);
    }

    [Fact]
    public async Task A_test_goes_now_and_links_to_settings()
    {
        await _settings.SetAsync(SettingKeys.UiAddress, "http://192.168.50.10:8080/");
        Assert.Null(await _notifier.SendTestAsync(Discord));
        var embed = JsonDocument.Parse(Assert.Single(_endpoint.Calls).Body).RootElement.GetProperty("embeds")[0];
        Assert.Equal("Gitberth can reach Ops", embed.GetProperty("title").GetString());
        Assert.Equal("http://192.168.50.10:8080/settings#notifications", embed.GetProperty("url").GetString());

        _endpoint.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Contains("403", await _notifier.SendTestAsync(Discord));
        Assert.False(_notifier.LastFor("d1")!.Ok);
    }

    // ---- Deploys ----

    [Theory]
    [InlineData(DeployStatus.Failed, DeployTrigger.Poll, "npm ci exited with 1", NotifyEvent.DeployFailed)]
    [InlineData(DeployStatus.RolledBack, DeployTrigger.Webhook, "It stopped", NotifyEvent.DeployFailed)]
    [InlineData(DeployStatus.Failed, DeployTrigger.Restart, "The new container didn't start", NotifyEvent.DeployFailed)]
    [InlineData(DeployStatus.Succeeded, DeployTrigger.Poll, null, NotifyEvent.DeploySucceeded)]
    [InlineData(DeployStatus.Succeeded, DeployTrigger.Rollback, null, NotifyEvent.DeploySucceeded)]
    [InlineData(DeployStatus.Succeeded, DeployTrigger.Restart, null, null)]
    [InlineData(DeployStatus.Succeeded, DeployTrigger.ConfigChange, null, null)]
    [InlineData(DeployStatus.Succeeded, DeployTrigger.Start, null, null)]
    [InlineData(DeployStatus.Cancelled, DeployTrigger.Manual, "Cancelled", null)]
    [InlineData(DeployStatus.Failed, DeployTrigger.Poll, "Gitberth restarted during this deploy. It's tried again automatically.", null)]
    public void Which_finished_deploys_are_told(DeployStatus status, DeployTrigger trigger, string? error, NotifyEvent? told) =>
        Assert.Equal(told, DeployNotifier.EventFor(new DeployReport(1, 5, trigger, "abc1234def", status, error)));

    [Fact]
    public void A_deploy_notice_says_what_happened()
    {
        var at = DateTime.UtcNow;
        var failed = DeployNotifier.Build(new DeployReport(1, 5, DeployTrigger.Poll, "abc1234def5678", DeployStatus.Failed, "npm ci exited with 1\nsee the log"),
            NotifyEvent.DeployFailed, "web", "gb-web", false, null, "http://x/apps/1/deployments/5", at);
        Assert.Equal("Deploy of web failed", failed.Title);
        Assert.Equal("Deploy #5 (New commit, commit abc1234) failed. npm ci exited with 1 see the log", failed.Text);
        Assert.Equal(NoticeLevel.Critical, failed.Level);

        var rolled = DeployNotifier.Build(new DeployReport(1, 6, DeployTrigger.Webhook, null, DeployStatus.RolledBack, null),
            NotifyEvent.DeployFailed, "web", "gb-web", false, null, null, at);
        Assert.Equal("web: the new version didn't pass its checks", rolled.Title);
        Assert.Contains("the previous version still runs", rolled.Text);
        Assert.EndsWith("The deploy log has the details.", rolled.Text);

        var live = DeployNotifier.Build(new DeployReport(1, 7, DeployTrigger.Manual, "abc1234def5678", DeployStatus.Succeeded, null),
            NotifyEvent.DeploySucceeded, "web", "gb-web", false, "web.example.com", null, at);
        Assert.Equal("web is live", live.Title);
        Assert.Equal("Deploy #7 (Manual, commit abc1234) succeeded. It answers at https://web.example.com/.", live.Text);
        Assert.Equal(NoticeLevel.Good, live.Level);

        var long_ = DeployNotifier.Build(new DeployReport(1, 8, DeployTrigger.Poll, null, DeployStatus.Failed, new string('x', 2000)),
            NotifyEvent.DeployFailed, "web", "gb-web", false, null, null, at);
        Assert.True(long_.Text.Length < DeployNotifier.ErrorLimit + 100);
    }

    [Fact]
    public async Task A_failed_deploy_is_published_with_a_link_to_its_log()
    {
        await _choices.SaveDestinationAsync(Discord);
        await _settings.SetAsync(SettingKeys.UiAddress, "http://192.168.50.10:8080/");
        var deploys = new DeployNotifier(_db, _choices, _notifier);
        await deploys.FinishedAsync(new DeployReport(_appId, 9, DeployTrigger.Poll, "abc1234def", DeployStatus.Failed, "Build failed"), default);
        // A success isn't told until it's turned on.
        await deploys.FinishedAsync(new DeployReport(_appId, 10, DeployTrigger.Poll, "abc1234def", DeployStatus.Succeeded, null), default);
        var n = Assert.Single(Published());
        Assert.Equal($"http://192.168.50.10:8080/apps/{_appId}/deployments/9", n.Link);
        Assert.Equal("gb-web", n.Container);
        Assert.Equal(_appId, n.AppId);
    }

    [Fact]
    public async Task Every_reporter_hears_of_a_deploy_even_when_one_fails()
    {
        var heard = new List<string>();
        var reporters = new DeployReporters([new Throwing(), new Recording(heard)], NullLogger<DeployReporters>.Instance);
        await reporters.FinishedAsync(new DeployReport(1, 1, DeployTrigger.Poll, null, DeployStatus.Failed, null), default);
        Assert.Equal(new[] { "finished 1" }, heard);
    }

    private sealed class Throwing : IDeployReporter
    {
        public Task StartedAsync(DeployReport report, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task FinishedAsync(DeployReport report, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class Recording(List<string> heard) : IDeployReporter
    {
        public Task StartedAsync(DeployReport report, CancellationToken ct) => Task.CompletedTask;

        public Task FinishedAsync(DeployReport report, CancellationToken ct)
        {
            heard.Add($"finished {report.DeploymentId}");
            return Task.CompletedTask;
        }
    }

    // ---- Crashes and health ----

    private static readonly DateTime T0 = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    private static ContainerEvent E(double seconds, string action, string name = "gb-web", int? exit = null, int? signal = null, int? deployment = null, string id = "c1") =>
        new(T0.AddSeconds(seconds), id, name, action, exit, signal, deployment);

    [Fact]
    public void A_crash_is_an_error_exit_or_running_out_of_memory_never_a_stop()
    {
        static (bool, bool) Crash(params ContainerEvent[] events) => EventMarks.CrashOf(events, Array.FindIndex(events, e => e.Action == "die"));
        Assert.Equal((true, false), Crash(E(0, "start"), E(10, "die", exit: 1)));
        Assert.Equal((true, true), Crash(E(0, "start"), E(9, "oom"), E(10, "die", exit: 137)));
        Assert.Equal((false, false), Crash(E(0, "start"), E(10, "die", exit: 0)));
        Assert.Equal((false, false), Crash(E(0, "start"), E(9, "kill", signal: 15), E(10, "die", exit: 143)));
        Assert.Equal((false, false), Crash(E(0, "start"), E(10, "die", exit: 1), E(11, "stop")));
        // SIGTERM or SIGINT from outside, with no kill on record (a host shutting down, say).
        Assert.Equal((false, false), Crash(E(0, "start"), E(10, "die", exit: 143)));
        Assert.Equal((false, false), Crash(E(0, "start"), E(10, "die", exit: 130)));
    }

    private (ContainerAlerts Alerts, ContainerEventWatcher Watcher, Clock Clock) Alerts()
    {
        var watcher = new ContainerEventWatcher(new DockerApi("unix:///var/run/docker.sock", new HttpClientHandler()),
            new StatsStore(new GitberthOptions { DataDir = Path.GetTempPath() }), NullLogger<ContainerEventWatcher>.Instance);
        var clock = new Clock(T0.AddSeconds(30));
        var alerts = new ContainerAlerts(watcher, _notifier, _db, new GitberthOptions { DataDir = Path.GetTempPath(), OwnContainer = "gitberth" },
            NullLogger<ContainerAlerts>.Instance, clock) { Settle = TimeSpan.Zero, Since = T0.AddSeconds(-60) };
        return (alerts, watcher, clock);
    }

    private static void Hear(ContainerEventWatcher watcher, params ContainerEvent[] events)
    {
        foreach (var e in events) watcher.Remember(e, e.Time);
    }

    [Fact]
    public async Task A_crash_is_told_with_whether_it_came_back_then_a_loop_is_counted()
    {
        await _choices.SaveDestinationAsync(Discord);
        var (alerts, watcher, clock) = Alerts();
        Hear(watcher, E(0, "start"), E(10, "die", exit: 1), E(11, "start"));
        await alerts.DiedAsync(E(10, "die", exit: 1), default);
        var first = Assert.Single(Published());
        Assert.Equal("web crashed", first.Title);
        Assert.Equal("Its process exited with code 1 and it restarted itself. Its log on Gitberth has the details.", first.Text);
        Assert.Equal(NoticeLevel.Warning, first.Level);
        Assert.Equal(_appId, first.AppId);

        // It keeps crashing: counted, not told, until the window is over.
        for (var i = 0; i < 4; i++)
        {
            var die = E(20 + i * 10, "die", exit: 1);
            Hear(watcher, die, E(21 + i * 10, "start"));
            await alerts.DiedAsync(die, default);
        }
        Assert.Empty(Published());
        await alerts.SummarizeAsync(default);
        Assert.Empty(Published());
        clock.Now += ContainerAlerts.Window;
        await alerts.SummarizeAsync(default);
        var summary = Assert.Single(Published());
        Assert.Equal("web keeps crashing", summary.Title);
        Assert.Equal("It crashed 4 more times in the last 30 minutes, and keeps restarting. Its log on Gitberth has the details.", summary.Text);
        await alerts.SummarizeAsync(default);
        Assert.Empty(Published());
    }

    [Fact]
    public async Task Running_out_of_memory_and_staying_down_are_said()
    {
        await _choices.SaveDestinationAsync(Discord);
        var (alerts, watcher, _) = Alerts();
        Hear(watcher, E(0, "start", name: "sample-redis"), E(9, "oom", name: "sample-redis"), E(10, "die", name: "sample-redis", exit: 137));
        await alerts.DiedAsync(E(10, "die", name: "sample-redis", exit: 137), default);
        var n = Assert.Single(Published());
        Assert.Equal("sample-redis ran out of memory", n.Title);
        Assert.Contains("and it's stopped now", n.Text);
        Assert.Equal(NoticeLevel.Critical, n.Level);
        Assert.Null(n.AppId);
    }

    [Fact]
    public async Task Stops_old_events_deploys_and_gitberth_itself_are_never_told()
    {
        await _choices.SaveDestinationAsync(Discord);
        var (alerts, watcher, _) = Alerts();
        // A stop someone asked for.
        Hear(watcher, E(0, "start"), E(9, "kill", signal: 15), E(10, "die", exit: 143));
        await alerts.DiedAsync(E(10, "die", exit: 143), default);
        // From before Gitberth started listening (a host that rebooted).
        Hear(watcher, E(-120, "die", name: "gb-api", exit: 1, id: "c2"));
        await alerts.DiedAsync(E(-120, "die", name: "gb-api", exit: 1, id: "c2"), default);
        // The container a deploy starts next to the running one, and Gitberth's own.
        Hear(watcher, E(20, "die", name: "gb-web" + DeployPipeline.BridgeSuffix, exit: 1, id: "c3"), E(21, "die", name: "gitberth", exit: 1, id: "c4"));
        await alerts.DiedAsync(E(20, "die", name: "gb-web" + DeployPipeline.BridgeSuffix, exit: 1, id: "c3"), default);
        await alerts.DiedAsync(E(21, "die", name: "gitberth", exit: 1, id: "c4"), default);
        // A new version a deploy is still checking: the deploy tells how it went.
        int deployment;
        await using (var db = _db.CreateDbContext())
        {
            var d = new Deployment { AppId = _appId, Status = DeployStatus.Starting, Trigger = DeployTrigger.Poll };
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            deployment = d.Id;
        }
        Hear(watcher, E(30, "die", exit: 1, deployment: deployment, id: "c5"));
        await alerts.DiedAsync(E(30, "die", exit: 1, deployment: deployment, id: "c5"), default);
        Assert.Empty(Published());
    }

    [Fact]
    public async Task A_failing_health_check_is_told_once_and_its_passing_again_too()
    {
        await _choices.SaveDestinationAsync(Discord);
        var (alerts, _, _) = Alerts();
        await alerts.HealthAsync(E(10, "health_status: unhealthy"), default);
        await alerts.HealthAsync(E(40, "health_status: unhealthy"), default);
        await alerts.HealthAsync(E(70, "health_status: healthy"), default);
        await alerts.HealthAsync(E(80, "health_status: healthy"), default);
        var told = Published();
        Assert.Equal(new[] { "web's health check is failing", "web's health check passes again" }, told.Select(n => n.Title));
        Assert.Equal(new[] { NotifyEvent.Unhealthy, NotifyEvent.Healthy }, told.Select(n => n.Event));

        // Flapping within the window: the failing isn't told again, and so neither is the passing.
        await alerts.HealthAsync(E(90, "health_status: unhealthy"), default);
        await alerts.HealthAsync(E(100, "health_status: healthy"), default);
        Assert.Empty(Published());
    }
}
