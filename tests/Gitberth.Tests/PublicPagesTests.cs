using System.Net;
using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Notifications;
using Gitberth.Public;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Uptime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The public pages: a request for a marker host is answered by them alone, whatever its path or method, and never by
/// Gitberth's sign-in, Blazor, hooks, static files or "not found" page; the down page says only what it may (escaped);
/// the status page is off until it's turned on, escapes everything, and never shows an address or a container's name.
/// An in-memory server runs the same first middleware in front of stand-ins for Gitberth's own endpoints.
/// </summary>
public sealed class PublicPagesTests : IAsyncLifetime
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());

        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private const string UiMark = "GITBERTH-UI";
    private const string SecretName = "payroll-internal";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private Db _db = default!;
    private SettingsService _settings = default!;
    private DownStates _down = default!;
    private UptimeStore _store = default!;
    private WebApplication _app = default!;
    private HttpClient _client = default!;
    private int _appId, _openId, _protectedId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        await using (var db = _db.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var app = new AppDefinition
            {
                Name = SecretName, Slug = SecretName, Owner = "acme", Repo = "web", CurrentImage = "gitberth/x:1",
                DownPageMessage = "Back at 9:00 <script>alert(1)</script> & \"soon\"", CreatedAt = DateTime.UtcNow.AddDays(-100),
            };
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true });
            app.Domains.Add(new CustomDomain { Hostname = "shop.example.com" });
            app.Domains.Add(new CustomDomain { Hostname = "admin.example.com", Protected = true });
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            (_appId, _openId, _protectedId) = (app.Id, app.Domains.Single(d => !d.Protected).Id, app.Domains.Single(d => d.Protected).Id);
        }
        var options = new GitberthOptions { DataDir = _dir };
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _down = new DownStates();
        _down.Set(_appId, DownReason.Stopped);
        var cli = new DockerCli(options);
        var domains = new DomainService(_db, _settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance, down: _down);
        var uptime = new UptimeSettings(_settings, _db, domains, _down, new AuditLog());
        _store = new UptimeStore(options);
        var notifier = new Notifier(new NotificationSettings(_settings), new NotificationSender(new NoHttp(), new SmtpMailer()), _settings, NullLogger<Notifier>.Instance);
        var monitor = new UptimeMonitor(_db, _store, new UptimeChecker(new NoHttp()), new UptimeCertificates(), _settings, domains, notifier, _down, options,
            NullLogger<UptimeMonitor>.Instance);
        var pages = new PublicPages(_db, _down, domains, uptime, new StatusPage(_db, _store, monitor, uptime), NullLogger<PublicPages>.Instance) { KeepView = TimeSpan.Zero };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(pages);
        _app = builder.Build();
        // As in Program.cs: the public pages first, then what turns an empty 404 into the "not found" page, then the rest.
        _app.Use(PublicPages.Middleware);
        _app.UseStatusCodePagesWithReExecute("/not-found");
        foreach (var path in new[] { "/login", "/_blazor/negotiate", "/hooks/github", "/hooks/deploy/{**token}", "/apps", "/apps/{key}", "/css/app.css", "/not-found", "/", "/settings" })
            _app.Map(path, () => Results.Text($"{UiMark} {path}"));
        _app.MapMethods("/status", ["GET", "HEAD"], (HttpContext context, PublicPages p) => p.StatusAsync(context, json: false));
        _app.MapMethods("/status.json", ["GET", "HEAD"], (HttpContext context, PublicPages p) => p.StatusAsync(context, json: true));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<(HttpResponseMessage Response, string Body)> SendAsync(HttpMethod method, string url, string? accept = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}");
        var response = await _client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private string Down(int domainId) => "http://" + MarkerHosts.Down(domainId);

    [Theory]
    [InlineData("GET", "/login")]
    [InlineData("POST", "/_blazor/negotiate")]
    [InlineData("POST", "/hooks/github")]
    [InlineData("POST", "/hooks/deploy/abcdefghijklmnopqrstuvwxyz")]
    [InlineData("GET", "/apps")]
    [InlineData("GET", "/apps/gb-payroll-internal")]
    [InlineData("GET", "/css/app.css")]
    [InlineData("GET", "/not-found")]
    [InlineData("GET", "/")]
    [InlineData("DELETE", "/settings")]
    public async Task A_marker_request_gets_only_the_down_page_whatever_its_path_or_method(string method, string path)
    {
        var (response, body) = await SendAsync(new HttpMethod(method), Down(_openId) + path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(UiMark, body);
        Assert.Equal("down", response.Headers.GetValues(PublicPages.PageHeader).Single());
        Assert.Equal("600", response.Headers.GetValues("Retry-After").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Contains(DownPage.Title, body);
        Assert.Contains("shop.example.com", body);
    }

    [Fact]
    public async Task Any_other_request_goes_on_to_gitberth()
    {
        var (response, body) = await SendAsync(HttpMethod.Get, "http://gitberth.lan:8080/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{UiMark} /login", body);
        Assert.False(response.Headers.Contains(PublicPages.PageHeader));
    }

    [Theory]
    [InlineData("http://x.gitberth.invalid/login")]
    [InlineData("http://gitberth.invalid/")]
    [InlineData("http://d-999.down.gitberth.invalid/")]
    [InlineData("http://d-01.down.gitberth.invalid/apps")]
    public async Task An_unknown_marker_gets_a_bare_404_and_never_the_not_found_page(string url)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task The_down_page_says_why_and_the_owners_message_escaped_but_never_the_apps_name()
    {
        var (_, html) = await SendAsync(HttpMethod.Get, Down(_openId) + "/");

        Assert.Contains("It isn't running right now.", html);
        Assert.Contains("Back at 9:00 &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;soon&quot;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain(SecretName, html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("src=", html);
        Assert.Contains("prefers-color-scheme:dark", html);

        // Being updated: back soon.
        _down.BeginUpdate(_appId);
        var (updating, text) = await SendAsync(HttpMethod.Get, Down(_openId) + "/");
        Assert.Equal("30", updating.Headers.GetValues("Retry-After").Single());
        Assert.Contains("It's being updated and will be back in a moment.", text);
        _down.EndUpdate(_appId);
    }

    [Fact]
    public async Task A_client_that_asks_for_json_gets_a_small_json_answer()
    {
        var (response, body) = await SendAsync(HttpMethod.Get, Down(_openId) + "/api/orders", "application/json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal((503, "unavailable", "shop.example.com", "not_running"), ((int)json["status"]!, (string?)json["error"], (string?)json["host"], (string?)json["reason"]));
        Assert.DoesNotContain(SecretName, body);
    }

    [Fact]
    public async Task Robots_are_turned_away_and_there_is_no_favicon()
    {
        var (robots, rules) = await SendAsync(HttpMethod.Get, Down(_openId) + "/robots.txt");
        Assert.Equal("User-agent: *\nDisallow: /\n", rules);
        Assert.Equal("down", robots.Headers.GetValues(PublicPages.PageHeader).Single());
        var (favicon, icon) = await SendAsync(HttpMethod.Get, Down(_openId) + "/favicon.ico");
        Assert.Equal((HttpStatusCode.NotFound, ""), (favicon.StatusCode, icon));
        var (head, nothing) = await SendAsync(HttpMethod.Head, Down(_openId) + "/");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, ""), (head.StatusCode, nothing));
    }

    [Fact]
    public async Task A_protected_domains_down_page_says_nothing_beyond_its_host_name()
    {
        var (response, html) = await SendAsync(HttpMethod.Get, Down(_protectedId) + "/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("admin.example.com", html);
        Assert.DoesNotContain("Back at 9:00", html);
        Assert.DoesNotContain("running", html);
        Assert.DoesNotContain(SecretName, html);
        var (_, json) = await SendAsync(HttpMethod.Get, Down(_protectedId) + "/", "application/json");
        Assert.Null(JsonNode.Parse(json)!["reason"]);
    }

    [Fact]
    public async Task The_status_page_is_off_until_turned_on_on_the_lan_and_through_its_host()
    {
        // Off: a bare 404 everywhere, never the "not found" page.
        foreach (var url in new[] { "http://gitberth.lan:8080/status", "http://gitberth.lan:8080/status.json", "http://status.gitberth.invalid/", "http://status.gitberth.invalid/status.json" })
        {
            var (off, body) = await SendAsync(HttpMethod.Get, url);
            Assert.Equal((HttpStatusCode.NotFound, ""), (off.StatusCode, body));
        }

        await _settings.SetAsync(SettingKeys.StatusEnabled, "true");
        await _settings.SetAsync(SettingKeys.StatusTitle, "Acme <b>status</b>");

        var (lan, html) = await SendAsync(HttpMethod.Get, "http://gitberth.lan:8080/status");
        Assert.Equal(HttpStatusCode.OK, lan.StatusCode);
        Assert.Contains("<title>Acme &lt;b&gt;status&lt;/b&gt;</title>", html);
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"60\">", html);
        var (marker, sameHtml) = await SendAsync(HttpMethod.Get, "http://status.gitberth.invalid/");
        Assert.Equal((HttpStatusCode.OK, "status"), (marker.StatusCode, marker.Headers.GetValues(PublicPages.PageHeader).Single()));
        Assert.Contains("Acme &lt;b&gt;status&lt;/b&gt;", sameHtml);
        var (_, json) = await SendAsync(HttpMethod.Get, "http://status.gitberth.invalid/status.json");
        Assert.Equal("Acme <b>status</b>", (string?)JsonNode.Parse(json)!["title"]);
        // Nothing else of Gitberth's through the status host.
        foreach (var path in new[] { "/login", "/apps", "/hooks/github", "/css/app.css" })
        {
            var (other, body) = await SendAsync(HttpMethod.Get, "http://status.gitberth.invalid" + path);
            Assert.Equal((HttpStatusCode.NotFound, ""), (other.StatusCode, body));
        }
        var (post, _) = await SendAsync(HttpMethod.Post, "http://status.gitberth.invalid/");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task The_status_page_escapes_everything_and_shows_no_address_or_container()
    {
        await using (var db = _db.CreateDbContext())
        {
            var app = await db.Apps.SingleAsync();
            app.Name = "<img src=x onerror=alert(1)>";
            await db.SaveChangesAsync();
        }
        await _settings.SetAsync(SettingKeys.StatusEnabled, "true");
        await _settings.SetAsync(SettingKeys.StatusDescription, "Our \"apps\" & <i>more</i>");
        var now = DateTime.UtcNow;
        await _store.AddAsync(new UptimeResult(_appId, now.AddMinutes(-30), false, null, null, "Not reachable", "http://192.168.1.10:3001/"));
        var id = await _store.OpenIncidentAsync(_appId, now.AddMinutes(-30), "Server error 502", "https://shop.example.com/");
        await _store.CloseIncidentAsync(id, now.AddMinutes(-26));

        var (_, html) = await SendAsync(HttpMethod.Get, "http://status.gitberth.invalid/");
        var (_, json) = await SendAsync(HttpMethod.Get, "http://status.gitberth.invalid/status.json");

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("Our &quot;apps&quot; &amp; &lt;i&gt;more&lt;/i&gt;", html);
        Assert.Contains("was down for 4 minutes on", html);
        Assert.Contains("Server error 502", html);
        Assert.Equal(90, html.Split("<i class=").Length - 1);
        foreach (var hidden in new[] { "192.168.1.10", "shop.example.com", $"gb-{SecretName}", "gitberth/x" })
        {
            Assert.DoesNotContain(hidden, html);
            Assert.DoesNotContain(hidden, json);
        }
        var parsed = JsonNode.Parse(json)!;
        Assert.Equal("<img src=x onerror=alert(1)>", (string?)parsed["apps"]![0]!["name"]);
        Assert.Equal(90, parsed["apps"]![0]!["days"]!.AsArray().Count);
        Assert.StartsWith("<img src=x onerror=alert(1)> was down for 4 minutes on ", (string?)parsed["incidents"]![0]!["summary"]);
    }

    [Fact]
    public void The_banner_says_how_the_listed_apps_stand()
    {
        static StatusApp App(UptimeStatus s) => new("a", s, [], null, null);
        Assert.Equal(StatusOverall.Operational, StatusPage.Overall([App(UptimeStatus.Up), App(UptimeStatus.Paused)]));
        Assert.Equal(StatusOverall.Partial, StatusPage.Overall([App(UptimeStatus.Up), App(UptimeStatus.Down)]));
        Assert.Equal(StatusOverall.Major, StatusPage.Overall([App(UptimeStatus.Down)]));
        Assert.Equal(StatusOverall.Unknown, StatusPage.Overall([App(UptimeStatus.NotChecked)]));
        Assert.Equal("All systems operational", StatusPage.Banner(StatusOverall.Operational));
        var incident = new StatusIncident("web", new DateTime(2026, 11, 12, 14, 5, 0, DateTimeKind.Utc), new DateTime(2026, 11, 12, 14, 9, 0, DateTimeKind.Utc), "Server error 502");
        Assert.Equal("web was down for 4 minutes on Nov 12, 14:05–14:09 UTC: Server error 502", incident.Summary);
    }

    [Fact]
    public void A_day_of_the_bar_is_that_days_uptime_and_hours_from_before_the_app_was_made_dont_count()
    {
        var made = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var app = new AppDefinition { Id = 1, Name = "web", CreatedAt = made };
        List<UptimeHour> hours =
        [
            new(1, made.AddDays(-1), 60, 0, 0, 0),
            new(1, made.AddHours(1), 60, 60, 6000, 60),
            new(1, made.AddHours(2), 60, 57, 6000, 57),
        ];
        var row = StatusPage.App(app, hours, UptimeStatus.Up, new DateOnly(2026, 11, 10), made.AddHours(3), responseTimes: true);
        Assert.Equal(StatusPage.Days, row.Days.Count);
        Assert.Equal(97.5, row.Days[^1].Uptime);
        Assert.Null(row.Days[^2].Uptime);
        Assert.Equal(97.5, row.Uptime90);
        Assert.Equal(102, row.ResponseMs);
    }
}
