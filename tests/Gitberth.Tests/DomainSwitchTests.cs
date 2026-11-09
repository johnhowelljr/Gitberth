using System.Net;
using System.Text;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Domains are optional: while they're turned off, nothing reaches Cloudflare or the tunnel's container, and nothing about
/// them changes. A stand-in docker reports a running cloudflared and the app's container, so the same calls do go out
/// once domains are on.
/// </summary>
public sealed class DomainSwitchTests : IDisposable
{
    private const string Hostname = "app.example.com";
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string TunnelId = "11111111-2222-4333-8444-555555555555";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class CloudflareStub : HttpMessageHandler
    {
        private readonly List<string> _calls = [];

        /// <summary>Requests wait until they're cancelled, like a Cloudflare that's slow to answer.</summary>
        public bool Hang { get; set; }
        /// <summary>Cloudflare's answers. Anything it returns null for gets a 500.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_calls) _calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            Started.TrySetResult();
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly GitberthOptions _options;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly SettingsService _settings;
    private readonly DockerCli _docker;
    private readonly CloudflareStub _cloudflare = new();
    private readonly DomainService _domains;
    private readonly int _appId;

    public DomainSwitchTests()
    {
        Directory.CreateDirectory(_dir);
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "acme", Repo = "web" };
            app.Ports.Add(new PortMapping { HostPort = 3001, ContainerPort = 3000, IsWeb = true });
            app.Domains.Add(new CustomDomain { Hostname = Hostname, KeepPaths = true, Service = "http://localhost:3001" });
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        _options = new GitberthOptions { DataDir = _dir, DockerPath = WriteFakeDocker(_dir) };
        _settings = new SettingsService(_db, _protector);
        _docker = new DockerCli(_options);
        _domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Every command the stand-in docker was run with, one per line.</summary>
    private string DockerCalls()
    {
        var log = Path.Combine(_dir, "calls.log");
        return File.Exists(log) ? File.ReadAllText(log) : "";
    }

    /// <summary>
    /// A docker that lists a running cloudflared (host network, run with a tunnel token) and the app's container, shows
    /// cloudflared's settings when inspected, and logs every command it's given.
    /// </summary>
    private static string WriteFakeDocker(string dir)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c2VjcmV0"}"""));
        string[] ps =
        [
            """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""",
            """{"Names":"gb-web","State":"running","Status":"Up 1 hour","Image":"gitberth/web:1","Ports":"0.0.0.0:3001->3000/tcp","Labels":"gitberth.managed=true","Command":"node server.js"}""",
        ];
        var inspect = """[{"Id":"t1","Name":"/tunnel","Args":["tunnel","run","--token","TOKEN"],"Config":{"Cmd":["tunnel","run","--token","TOKEN"],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{"host":{}}}}]"""
            .Replace("TOKEN", token, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            // cmd leaves what's between quotes alone, which in JSON is every character it would otherwise act on.
            var cmd = Path.Combine(dir, "docker.cmd");
            File.WriteAllLines(cmd,
            [
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%~1\"==\"ps\" (",
                .. ps.Select(line => "echo " + line),
                "exit /b 0",
                ")",
                "if \"%~1\"==\"container\" (",
                "echo " + inspect,
                "exit /b 0",
                ")",
                "exit /b 1",
            ]);
            return cmd;
        }
        var sh = Path.Combine(dir, "docker");
        File.WriteAllLines(sh,
        [
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "case \"$1\" in",
            "ps)",
            .. ps.Select(line => $"echo '{line}'"),
            ";;",
            "container)",
            $"echo '{inspect}'",
            ";;",
            "*) exit 1 ;;",
            "esac",
        ]);
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private static HttpResponseMessage Ok(string resultJson) =>
        new(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true,\"result\":" + resultJson + "}", Encoding.UTF8, "application/json") };

    private async Task TurnOnWithTokenAsync()
    {
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
    }

    private void UseOnlyDomain(string hostname)
    {
        using var db = _db.CreateDbContext();
        db.Domains.RemoveRange(db.Domains);
        db.Domains.Add(new CustomDomain { AppId = _appId, Hostname = hostname });
        db.SaveChanges();
    }

    /// <summary>
    /// A Cloudflare whose tunnel routes www.example.com to the app, with the zone example.com and these DNS records for
    /// www.example.com and example.com.
    /// </summary>
    private void CloudflareWith(string wwwRecords, string apexRecords) => _cloudflare.Respond = r =>
    {
        var (path, query) = (r.RequestUri!.AbsolutePath, r.RequestUri.Query);
        if (path.EndsWith("/configurations", StringComparison.Ordinal))
            return Ok("""{"config":{"ingress":[{"hostname":"www.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]}}""");
        if (path.EndsWith("/zones", StringComparison.Ordinal))
            return Ok(query.Contains("name=example.com&", StringComparison.Ordinal) ? """[{"id":"z1","name":"example.com"}]""" : "[]");
        if (path.EndsWith("/zones/z1/dns_records", StringComparison.Ordinal))
            return Ok(query.Contains("name=www.example.com&", StringComparison.Ordinal) ? wwwRecords
                : query.Contains("name=example.com&", StringComparison.Ordinal) ? apexRecords : "[]");
        return null;
    };

    [Fact]
    public async Task A_name_whose_cname_leads_through_another_name_to_the_tunnel_is_live()
    {
        await TurnOnWithTokenAsync();
        UseOnlyDomain("www.example.com");
        CloudflareWith(
            """[{"id":"r1","type":"CNAME","name":"www.example.com","content":"example.com","proxied":true}]""",
            $$"""[{"id":"r2","type":"CNAME","name":"example.com","content":"{{TunnelId}}.cfargotunnel.com","proxied":true}]""");

        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);

        Assert.True(state.Working, state.Summary);
        Assert.Equal(DomainFix.None, state.Fix);
    }

    [Fact]
    public async Task A_cname_to_somewhere_else_still_offers_to_point_it_here()
    {
        await TurnOnWithTokenAsync();
        UseOnlyDomain("www.example.com");
        CloudflareWith("""[{"id":"r1","type":"CNAME","name":"www.example.com","content":"cname.vercel-dns.com","proxied":false}]""", "[]");

        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);

        Assert.False(state.Working);
        Assert.Equal(DomainFix.PointDnsHere, state.Fix);
        Assert.Contains("cname.vercel-dns.com", state.Summary);
    }

    [Fact]
    public async Task Domains_are_off_until_turned_on()
    {
        Assert.False(await _domains.EnabledAsync());
        Assert.Empty((await _domains.SetEnabledAsync(true)).Notes);
        Assert.True(await _domains.EnabledAsync());
        await _domains.SetEnabledAsync(false);
        Assert.False(await _domains.EnabledAsync());
        Assert.Null(await _settings.GetAsync(SettingKeys.DomainsEnabled));
    }

    [Fact]
    public async Task While_off_nothing_reaches_cloudflare_or_the_tunnel_container_and_nothing_changes()
    {
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");

        Assert.Equal(DomainService.TurnedOff, Assert.Single((await _domains.SyncAsync()).Notes).Text);
        Assert.Equal(DomainService.TurnedOff, (await _domains.AddAsync(_appId, "new.example.com")).Error);
        Assert.Equal(DomainService.TurnedOff, Assert.Single((await _domains.RemoveAsync(_appId, Hostname)).Notes).Text);
        Assert.Equal(DomainService.TurnedOff, Assert.Single((await _domains.ServeAllPathsAsync(_appId, Hostname)).Notes).Text);
        Assert.Equal(DomainService.TurnedOff, Assert.Single((await _domains.PointDnsHereAsync(_appId, Hostname)).Notes).Text);
        Assert.Equal(DomainService.TurnedOff, (await _domains.SaveTokenAsync("token")).Error);
        var (tunnel, problem) = await _domains.FindTunnelAsync();
        Assert.Null(tunnel);
        Assert.Equal(DomainService.TurnedOff, problem);
        var overview = await _domains.GetOverviewAsync(_appId);
        Assert.False(overview.Enabled);
        Assert.False(Assert.Single(overview.Domains).Working);

        Assert.Empty(_cloudflare.Calls);
        Assert.Equal("", DockerCalls());
        using var db = _db.CreateDbContext();
        var domain = Assert.Single(db.Domains);
        Assert.Equal(Hostname, domain.Hostname);
        Assert.True(domain.KeepPaths);
        Assert.Empty(db.DomainRemovals);
    }

    [Fact]
    public async Task Turned_on_with_a_token_it_syncs_through_the_tunnel_container_and_cloudflare()
    {
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");

        var report = await _domains.SetEnabledAsync(true);

        Assert.Contains(report.Notes, n => n.Problem);
        Assert.Contains($"GET /client/v4/accounts/{AccountId}/cfd_tunnel/{TunnelId}/configurations", _cloudflare.Calls);
        Assert.Contains("container inspect tunnel", DockerCalls());
    }

    [Fact]
    public async Task Turning_off_stops_a_check_that_is_still_talking_to_cloudflare()
    {
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        _cloudflare.Hang = true;

        var checking = _domains.GetOverviewAsync(_appId);
        await _cloudflare.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await _domains.SetEnabledAsync(false);

        var overview = await checking.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(overview.Enabled);
        Assert.Single(_cloudflare.Calls);
    }

    // ---- Pending removals, tried again on a timer ----

    private const string Removed = "old.example.com";

    /// <summary>The app's own domain gone, and old.example.com taken off it with its route and DNS record still to remove.</summary>
    private void PendingRemoval()
    {
        using var db = _db.CreateDbContext();
        db.Domains.RemoveRange(db.Domains);
        db.DomainRemovals.Add(new DomainRemoval { Hostname = Removed, Service = "http://localhost:3001" });
        db.SaveChanges();
    }

    private bool Pending()
    {
        using var db = _db.CreateDbContext();
        return db.DomainRemovals.Any();
    }

    /// <summary>A Cloudflare whose tunnel still routes old.example.com, with the record Gitberth added for it in the zone example.com.</summary>
    private void CloudflareStillRouting() => _cloudflare.Respond = r =>
    {
        var (path, query) = (r.RequestUri!.AbsolutePath, r.RequestUri.Query);
        if (path.EndsWith("/configurations", StringComparison.Ordinal))
            return Ok($$$"""{"config":{"ingress":[{"hostname":"{{{Removed}}}","service":"http://localhost:3001"},{"service":"http_status:404"}]}}""");
        if (path.EndsWith("/zones", StringComparison.Ordinal))
            return Ok(query.Contains("name=example.com&", StringComparison.Ordinal) ? """[{"id":"z1","name":"example.com"}]""" : "[]");
        if (path.EndsWith("/zones/z1/dns_records", StringComparison.Ordinal))
            return Ok(query.Contains($"name={Removed}&", StringComparison.Ordinal)
                ? $$"""[{"id":"r9","type":"CNAME","name":"{{Removed}}","content":"{{TunnelId}}.cfargotunnel.com","proxied":true,"comment":"Added by Gitberth"}]""" : "[]");
        if (r.Method == HttpMethod.Delete && path.EndsWith("/zones/z1/dns_records/r9", StringComparison.Ordinal)) return Ok("{}");
        return null;
    };

    [Fact]
    public async Task Nothing_is_retried_with_nothing_pending_domains_off_or_no_token()
    {
        // Domains on with a token, nothing pending: not a single call.
        await TurnOnWithTokenAsync();
        Assert.Null(await _domains.RetryRemovalsAsync());

        // Pending, but domains off, or on without a token: still none, and the removal waits.
        PendingRemoval();
        await _settings.SetAsync(SettingKeys.DomainsEnabled, null);
        Assert.Null(await _domains.RetryRemovalsAsync());
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, null);
        Assert.Null(await _domains.RetryRemovalsAsync());

        Assert.Empty(_cloudflare.Calls);
        Assert.Equal("", DockerCalls());
        Assert.True(Pending());
    }

    [Fact]
    public async Task A_removal_that_failed_is_finished_by_the_next_retry()
    {
        await TurnOnWithTokenAsync();
        PendingRemoval();

        // Cloudflare fails: the removal is kept, and says it's tried again.
        var failed = await _domains.RetryRemovalsAsync();
        Assert.NotNull(failed);
        Assert.Contains(failed.Notes, n => n.Problem);
        Assert.True(Pending());

        // Once it answers, the route and the record Gitberth added go, and so does the removal.
        CloudflareStillRouting();
        var done = await _domains.RetryRemovalsAsync();

        Assert.NotNull(done);
        Assert.Contains(done.Notes, n => n.Text == $"Removed the DNS record Gitberth added for {Removed}");
        Assert.Contains($"PUT /client/v4/accounts/{AccountId}/cfd_tunnel/{TunnelId}/configurations", _cloudflare.Calls);
        Assert.Contains("DELETE /client/v4/zones/z1/dns_records/r9", _cloudflare.Calls);
        Assert.False(Pending());
        // Nothing left: the next one asks nothing.
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
    }

    [Fact]
    public async Task A_removal_says_when_it_is_tried_again()
    {
        await TurnOnWithTokenAsync();
        PendingRemoval();
        CloudflareStillRouting();
        var answer = _cloudflare.Respond!;
        // The tunnel answers, but the zone's records can't be read.
        _cloudflare.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/dns_records", StringComparison.Ordinal) ? null : answer(r);

        var report = await _domains.RetryRemovalsAsync();

        var note = Assert.Single(report!.Notes, n => n.Hostname == Removed && n.Problem);
        Assert.EndsWith("Gitberth tries again every 15 minutes until it's done.", note.Text);
        Assert.True(Pending());
    }

    [Fact]
    public async Task The_background_retry_finishes_a_pending_removal_on_its_own()
    {
        await TurnOnWithTokenAsync();
        PendingRemoval();
        CloudflareStillRouting();
        var retry = new RemovalRetry(_domains, NullLogger<RemovalRetry>.Instance) { Interval = TimeSpan.FromMilliseconds(50) };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retry.Tried += _ =>
        {
            if (!Pending()) finished.TrySetResult();
        };

        await retry.StartAsync(CancellationToken.None);
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await retry.StopAsync(CancellationToken.None);
        }

        Assert.Contains("DELETE /client/v4/zones/z1/dns_records/r9", _cloudflare.Calls);
        Assert.Equal(TimeSpan.FromMinutes(15), RemovalRetry.Every);
    }

    [Fact]
    public async Task An_app_deleted_while_off_keeps_the_port_and_name_its_routes_go_to_from_the_next_app()
    {
        // One route to a host port, and one to the app's name on Gitberth's network (cloudflared on that network).
        using (var db = _db.CreateDbContext())
        {
            db.Domains.Add(new CustomDomain { AppId = _appId, Hostname = "shop.example.com", Service = "http://web:3000" });
            db.SaveChanges();
        }

        Assert.Empty((await _domains.DeleteAppAsync(_appId)).Notes);

        using (var db = _db.CreateDbContext())
        {
            Assert.False(db.Apps.Any());
            Assert.Equal([Hostname, "shop.example.com"], db.DomainRemovals.Select(r => r.Hostname).OrderBy(h => h).ToList());
        }
        Assert.Equal([(3001, (int?)null, Hostname)], await _domains.RoutedHostPortsAsync());
        Assert.Equal(["web"], await _domains.RoutedNamesAsync());
        var apps = new AppService(_db, _protector, _docker, _settings, new HealthSettings(_settings), _domains, _options, new RestartSchedules(_settings), new Gitberth.Deploy.WatchState());
        var id = await apps.CreateAsync(new GitHubRepoRef("acme", "other"), new AppSettingsModel { Name = "Web" }, null);
        Assert.Equal("web-2", (await apps.GetAsync(id))!.Slug);
        Assert.Empty(_cloudflare.Calls);
        Assert.Equal("", DockerCalls());
    }
}
