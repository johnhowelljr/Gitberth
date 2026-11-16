using System.Net;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Uptime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Uptime checks: what an answer means ("Server error 502", "Showing the down page", "Certificate problem", "Timed out",
/// "Not reachable"), where each app is checked, two failures in a row opening an incident that one good check closes,
/// the down and back-up notices (muted apps left out), the certificate warnings, and what the history keeps.
/// </summary>
public sealed class UptimeTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static readonly DateTime T0 = new(2026, 11, 12, 14, 5, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly NotificationSettings _choices;
    private readonly Notifier _notifier;
    private readonly UptimeStore _store;
    private readonly GitberthOptions _options;
    private readonly int _appId;

    public UptimeTests()
    {
        Directory.CreateDirectory(_dir);
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "web", Slug = "web", Owner = "acme", Repo = "web", CurrentImage = "gitberth/web:1", CreatedAt = T0.AddDays(-30) };
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true });
            app.Domains.Add(new CustomDomain { Hostname = "web.example.com" });
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        _options = new GitberthOptions { DataDir = _dir, PublicHost = "192.168.1.10" };
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _choices = new NotificationSettings(_settings);
        _notifier = new Notifier(_choices, new NotificationSender(new Factory(new Answer(_ => new HttpResponseMessage(HttpStatusCode.NoContent))), new NoMail()), _settings,
            NullLogger<Notifier>.Instance);
        _store = new UptimeStore(_options);
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class NoMail : IMailer
    {
        public Task SendAsync(Destination destination, MailNotice mail, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task WithDestinationAsync() =>
        await _choices.SaveDestinationAsync(new Destination { Id = "w1", Kind = DestinationKind.Webhook, Url = "https://notify.example.com/hook" });

    private List<Notice> Published()
    {
        var list = new List<Notice>();
        while (_notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    private UptimeMonitor Monitor(HttpMessageHandler? handler = null, Clock? clock = null)
    {
        var cli = new DockerCli(_options);
        var domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(new Answer(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))),
            new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        return new UptimeMonitor(_db, _store, new UptimeChecker(new Factory(handler ?? new Answer(_ => new HttpResponseMessage(HttpStatusCode.OK))), clock),
            new UptimeCertificates(), _settings, domains, _notifier, new DownStates(), _options, NullLogger<UptimeMonitor>.Instance, time: clock);
    }

    private static UptimeMonitor.Target Target(int appId) => new(appId, "web", "gb-web", "https://web.example.com/");

    private UptimeResult Fail(DateTime at, string problem = "Server error 502") => new(_appId, at, false, 502, 30, problem, "https://web.example.com/");

    private UptimeResult Ok(DateTime at, int ms = 120) => new(_appId, at, true, 200, ms, null, "https://web.example.com/");

    // ---- What an answer means ----

    [Theory]
    [InlineData(200, false, true, null)]
    [InlineData(302, false, true, null)]
    [InlineData(404, false, true, null)]
    [InlineData(403, false, true, null)]
    [InlineData(500, false, false, "Server error 500")]
    [InlineData(502, false, false, "Server error 502")]
    [InlineData(530, false, false, "Server error 530")]
    [InlineData(503, true, false, "Showing the down page")]
    public void An_answer_below_500_that_isnt_the_down_page_is_up(int status, bool downPage, bool up, string? problem) =>
        Assert.Equal((up, problem), UptimeChecker.Classify(status, downPage, null));

    [Fact]
    public void Errors_read_as_people_say_them()
    {
        Assert.Equal("Certificate problem", UptimeChecker.Classify(null, false, new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established")).Problem);
        Assert.Equal("Certificate problem", UptimeChecker.Classify(null, false, new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid"))).Problem);
        Assert.Equal("Timed out", UptimeChecker.Classify(null, false, new TaskCanceledException("timeout", new TimeoutException())).Problem);
        Assert.Equal("Not reachable", UptimeChecker.Classify(null, false, new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known")).Problem);
        Assert.Equal("Not reachable", UptimeChecker.Classify(null, false, new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")).Problem);
    }

    [Fact]
    public async Task A_check_asks_once_without_following_redirects_and_knows_the_down_page_by_its_header()
    {
        var answer = new Answer(r =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("X-Gitberth-Page", "down");
            return response;
        });
        var checker = new UptimeChecker(new Factory(answer), new Clock(T0));

        var result = await checker.CheckAsync(_appId, "https://web.example.com/health", CancellationToken.None);

        Assert.Equal((false, 503, "Showing the down page", T0), (result.Up, result.Status, result.Problem, result.At));
        var request = Assert.Single(answer.Requests);
        Assert.Equal("https://web.example.com/health", request.RequestUri!.ToString());
        Assert.True(request.Headers.ConnectionClose);
        Assert.StartsWith("Gitberth-Uptime/", request.Headers.UserAgent.ToString());

        var timedOut = await new UptimeChecker(new Factory(new Answer(_ => throw new TaskCanceledException("t", new TimeoutException()))))
            .CheckAsync(_appId, "https://web.example.com/", CancellationToken.None);
        Assert.Equal((false, (int?)null, "Timed out"), (timedOut.Up, timedOut.Status, timedOut.Problem));
    }

    [Theory]
    [InlineData("10.89.0.2:443", "10.89.0.2", 443)]
    [InlineData("edge.test", "edge.test", 443)]
    [InlineData("edge.test:8443", "edge.test", 8443)]
    [InlineData("[fd00::2]:443", "fd00::2", 443)]
    [InlineData("[fd00::2]", "fd00::2", 443)]
    public void The_diagnostic_connect_to_setting_reads_a_host_and_a_port(string value, string host, int port) =>
        Assert.Equal((host, port), UptimeChecker.ConnectTo(value));

    [Fact]
    public void An_unusable_connect_to_setting_is_ignored()
    {
        Assert.Null(UptimeChecker.ConnectTo(null));
        Assert.Null(UptimeChecker.ConnectTo("  "));
        Assert.Null(UptimeChecker.ConnectTo("edge.test:http"));
    }

    // ---- Where an app is checked ----

    [Fact]
    public void Each_app_is_checked_on_its_main_domain_else_its_lan_address_else_not_at_all()
    {
        var app = new AppDefinition
        {
            Id = 3, Name = "web", Slug = "web", CurrentImage = "gitberth/web:1", UptimePath = "health",
            Ports = [new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true }],
            Domains = [new CustomDomain { Id = 1, Hostname = "www.example.com" }, new CustomDomain { Id = 2, Hostname = "web.example.com", Main = true }],
        };
        Assert.Equal("https://web.example.com/health", UptimeMonitor.TargetOf(app, domainsOn: true, "192.168.1.10").Address);
        // Domains off: its LAN address, on its published web port.
        Assert.Equal("http://192.168.1.10:3001/health", UptimeMonitor.TargetOf(app, domainsOn: false, "192.168.1.10").Address);
        // A web port on one address of its own.
        var custom = new AppDefinition { Id = 4, Name = "c", Slug = "c", Source = Apps.AppSource.Registry, Ports = [new PortMapping { ContainerPort = 80, HostPort = 8088, Access = PortAccess.Custom, HostIp = "192.168.1.20", IsWeb = true }] };
        Assert.Equal("http://192.168.1.20:8088/", UptimeMonitor.TargetOf(custom, false, "192.168.1.10").Address);
        // Nothing the LAN reaches: not checked, and why.
        var hostOnly = new AppDefinition { Id = 5, Name = "h", Slug = "h", Source = Apps.AppSource.Registry, Ports = [new PortMapping { ContainerPort = 80, HostPort = 8089, Access = PortAccess.HostOnly, IsWeb = true }] };
        var none = UptimeMonitor.TargetOf(hostOnly, false, "192.168.1.10");
        Assert.Equal((null, UptimeStatus.NotChecked), (none.Address, none.Status));
        Assert.Equal("It has no domain, and no web port on the LAN to check.", none.Why);
        // Stopped on purpose, checks off, never deployed: not checked.
        Assert.Equal(UptimeStatus.Paused, UptimeMonitor.TargetOf(new AppDefinition { Id = 6, Name = "s", Slug = "s", CurrentImage = "x", DesiredState = DesiredState.Stopped }, true, null).Status);
        Assert.Equal("Uptime checks are off for this app.", UptimeMonitor.TargetOf(new AppDefinition { Id = 7, Name = "o", Slug = "o", UptimeCheck = false }, true, null).Why);
        Assert.Equal("It's checked once its first deploy is live.", UptimeMonitor.TargetOf(new AppDefinition { Id = 8, Name = "n", Slug = "n" }, true, null).Why);
        Assert.Equal("/", UptimeMonitor.PathOf(null));
        Assert.Equal("/api/health", UptimeMonitor.PathOf(" api/health "));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/health", null)]
    [InlineData("health", "Start the path with /, like /health.")]
    [InlineData("/he alth", "Use a path like /health, without spaces.")]
    [InlineData("//evil.example.com/", "Use a path like /health, without spaces.")]
    public void The_path_a_check_asks_for_starts_with_a_slash(string? path, string? problem) => Assert.Equal(problem, UptimeSettings.PathProblem(path));

    // ---- Incidents and their notices ----

    [Fact]
    public async Task Two_failures_in_a_row_open_an_incident_that_one_good_check_closes_and_each_is_told()
    {
        await WithDestinationAsync();
        var monitor = Monitor();
        var target = Target(_appId);

        await monitor.RecordAsync(target, Fail(T0), CancellationToken.None);
        Assert.Empty(Published());
        Assert.Null(monitor.For(_appId).Open);

        await monitor.RecordAsync(target, Fail(T0.AddMinutes(1), "Not reachable"), CancellationToken.None);
        var down = Assert.Single(Published());
        Assert.Equal((NotifyEvent.Down, NoticeLevel.Critical, "web is down"), (down.Event, down.Level, down.Title));
        Assert.Equal("Its uptime check of https://web.example.com/ failed twice in a row: Server error 502. Gitberth tells you when it's back up.", down.Text);
        Assert.Equal(("gb-web", _appId, "https://web.example.com/"), (down.Container, down.AppId, down.Address));
        var open = monitor.For(_appId);
        Assert.Equal(UptimeStatus.Down, open.Status);
        Assert.Equal((T0, "Server error 502"), (open.Open!.Started, open.Open.Cause));

        // Still down: nothing more is said.
        await monitor.RecordAsync(target, Fail(T0.AddMinutes(2)), CancellationToken.None);
        Assert.Empty(Published());

        await monitor.RecordAsync(target, Ok(T0.AddMinutes(4)), CancellationToken.None);
        var up = Assert.Single(Published());
        Assert.Equal((NotifyEvent.BackUp, NoticeLevel.Good, "web is back up"), (up.Event, up.Level, up.Title));
        Assert.Equal("It was down for 4 minutes (Server error 502).", up.Text);
        Assert.Equal(UptimeStatus.Up, monitor.For(_appId).Status);

        var incident = Assert.Single(await _store.IncidentsAsync(_appId, T0.AddDays(-1)));
        Assert.Equal((T0, T0.AddMinutes(4), "Server error 502", "https://web.example.com/"), (incident.Started, incident.Ended, incident.Cause, incident.Address));
        // Every check is kept, and counted in its hour.
        Assert.Equal(4, (await _store.ResultsAsync(_appId, T0.AddHours(-1))).Count);
        var hour = Assert.Single(await _store.HoursAsync([_appId], T0.AddHours(-1)));
        Assert.Equal((4, 1, 120), (hour.Checks, hour.Ups, hour.AverageMs));
    }

    [Fact]
    public async Task A_failure_between_good_checks_opens_nothing()
    {
        var monitor = Monitor();
        await monitor.RecordAsync(Target(_appId), Ok(T0), CancellationToken.None);
        await monitor.RecordAsync(Target(_appId), Fail(T0.AddMinutes(1)), CancellationToken.None);
        await monitor.RecordAsync(Target(_appId), Ok(T0.AddMinutes(2)), CancellationToken.None);
        await monitor.RecordAsync(Target(_appId), Fail(T0.AddMinutes(3)), CancellationToken.None);
        Assert.Empty(await _store.IncidentsAsync(_appId, T0.AddDays(-1)));
        Assert.Equal(UptimeStatus.Up, monitor.For(_appId).Status);
    }

    [Fact]
    public async Task After_a_restart_an_open_incident_and_the_failures_so_far_are_picked_up()
    {
        var before = Monitor();
        await before.RecordAsync(Target(_appId), Fail(T0), CancellationToken.None);
        await before.RecordAsync(Target(_appId), Fail(T0.AddMinutes(1)), CancellationToken.None);

        var after = Monitor();
        await after.LoadAsync(CancellationToken.None);
        Assert.Equal(UptimeStatus.Down, after.For(_appId).Status);
        await WithDestinationAsync();
        await after.RecordAsync(Target(_appId), Ok(T0.AddMinutes(2)), CancellationToken.None);
        Assert.Equal(NotifyEvent.BackUp, Assert.Single(Published()).Event);
        Assert.Empty(await _store.OpenIncidentsAsync());
    }

    [Fact]
    public async Task Down_and_back_up_go_with_their_switch_and_an_app_left_out_isnt_told()
    {
        await WithDestinationAsync();
        var notice = new Notice(NotifyEvent.Down, NoticeLevel.Critical, "web is down", "x", null, "web", "gb-web", T0);
        Assert.True(_notifier.Wanted(notice));
        Assert.True(_notifier.Wanted(notice with { Event = NotifyEvent.BackUp }));
        Assert.True(_notifier.Wanted(notice with { Event = NotifyEvent.CertificateExpiring }));
        await _choices.SetMutedAsync("gb-web", true);
        Assert.False(_notifier.Wanted(notice));
        await _choices.SetMutedAsync("gb-web", false);
        await _choices.SetEventAsync(NotifyEvent.Down, false);
        Assert.False(_notifier.Wanted(notice));
        Assert.False(_notifier.Wanted(notice with { Event = NotifyEvent.BackUp }));
        // Saved with every kind Settings knows of now, so a kind added later starts on its own default.
        var saved = JsonNode.Parse((await _settings.GetAsync(SettingKeys.NotifyEvents))!)!;
        Assert.DoesNotContain("Down", saved["on"]!.AsArray().Select(x => (string?)x));
        Assert.Contains("Down", saved["known"]!.AsArray().Select(x => (string?)x));
        var again = new NotificationSettings(_settings);
        await again.LoadAsync();
        Assert.False(again.IsOn(NotifyEvent.Down));
        Assert.True(again.IsOn(NotifyEvent.CertificateExpiring));
    }

    [Fact]
    public void The_new_kinds_have_their_own_names_in_a_webhook()
    {
        Assert.Equal("uptime.down", NotificationFormats.EventName(NotifyEvent.Down));
        Assert.Equal("uptime.up", NotificationFormats.EventName(NotifyEvent.BackUp));
        Assert.Equal("certificate.expiring", NotificationFormats.EventName(NotifyEvent.CertificateExpiring));
        // None falls through to "test".
        Assert.All(Enum.GetValues<NotifyEvent>().Where(e => e != NotifyEvent.Test), e => Assert.NotEqual("test", NotificationFormats.EventName(e)));

        var sent = NotificationFormats.Webhook(new Notice(NotifyEvent.Down, NoticeLevel.Critical, "web is down", "Its uptime check failed", "http://gitberth.lan/apps/gb-web", "web", "gb-web", T0)
        {
            AppId = 3, Address = "https://web.example.com/",
        }, null);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal(("uptime.down", "critical", "https://web.example.com/", "gb-web", 3), ((string?)body["event"], (string?)body["level"], (string?)body["address"], (string?)body["container"], (int?)body["appId"]));
        Assert.Contains(sent.Headers, h => h.Key == "X-Gitberth-Event" && h.Value == "uptime.down");
    }

    // ---- Certificates ----

    [Fact]
    public void A_certificate_is_told_at_fourteen_days_then_at_three_and_never_twice_a_day()
    {
        var notAfter = T0.AddDays(13.5);
        var (first, warn) = UptimeMonitor.CertificateCheck(null, "Web.Example.com", notAfter, T0);
        Assert.Equal((14, "web.example.com"), (warn, first.Host));
        Assert.Equal(14, first.WarnedDays);
        Assert.Null(UptimeMonitor.CertificateCheck(first, "web.example.com", notAfter, T0.AddHours(1)).Warn);
        // Within 3 days, a day after the last notice: told again, once.
        var (third, at3) = UptimeMonitor.CertificateCheck(first with { WarnedAt = T0 }, "web.example.com", notAfter, notAfter.AddDays(-2.5));
        Assert.Equal(3, at3);
        Assert.Null(UptimeMonitor.CertificateCheck(third, "web.example.com", notAfter, notAfter.AddDays(-1)).Warn);
        // Too close to the last notice: it waits for the next day.
        Assert.Null(UptimeMonitor.CertificateCheck(first with { WarnedAt = notAfter.AddDays(-2.6) }, "web.example.com", notAfter, notAfter.AddDays(-2.5)).Warn);
        // Renewed: nothing to tell, and the next expiry starts over.
        var (renewed, none) = UptimeMonitor.CertificateCheck(third, "web.example.com", T0.AddDays(90), T0.AddDays(12));
        Assert.Null(none);
        Assert.Null(renewed.WarnedDays);
        Assert.Null(UptimeMonitor.CertificateCheck(null, "web.example.com", T0.AddDays(60), T0).Warn);
    }

    [Fact]
    public async Task The_history_forgets_old_checks_and_everything_of_apps_that_are_gone()
    {
        var now = T0;
        await _store.AddAsync(Ok(now.AddDays(-8)));
        await _store.AddAsync(Ok(now.AddHours(-1)));
        await _store.AddAsync(new UptimeResult(999, now.AddHours(-1), true, 200, 50, null, "https://gone.example.com/"));
        var id = await _store.OpenIncidentAsync(999, now.AddHours(-2), "Timed out", null);
        await _store.CloseIncidentAsync(id, now.AddHours(-1));

        await _store.PruneAsync(now, [_appId]);

        Assert.Single(await _store.ResultsAsync(_appId, now.AddDays(-30)));
        Assert.Empty(await _store.ResultsAsync(999, now.AddDays(-30)));
        Assert.Empty(await _store.IncidentsAsync(999, now.AddDays(-30)));
        Assert.Equal(2, (await _store.HoursAsync([_appId], now.AddDays(-30))).Count);
        Assert.Equal("4 minutes", UptimeMonitor.Duration(TimeSpan.FromMinutes(4)));
        Assert.Equal("1 hour 5 minutes", UptimeMonitor.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("under a minute", UptimeMonitor.Duration(TimeSpan.FromSeconds(40)));
        Assert.Equal("2 days", UptimeMonitor.Duration(TimeSpan.FromHours(48)));
    }
}
