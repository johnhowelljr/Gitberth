using System.Net;
using System.Text;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// A domain for an app whose web port is Internal (6d). cloudflared on the host's network reaches apps only through a host
/// port, so adding the domain publishes the web port on the host only first; on Gitberth's network it reaches them by name.
/// No Cloudflare token is saved here, so nothing is routed: what's tested is what's saved.
/// </summary>
public sealed class DomainPublishTests : IDisposable
{
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string TunnelId = "11111111-2222-4333-8444-555555555555";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cloudflare was asked, but no token is saved.");
    }

    /// <summary>An engine with no containers: the host-port map only has the apps' saved ports.</summary>
    private sealed class EmptyEngine : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/version" ? DockerFixtures.Text("version.json") : "[]", Encoding.UTF8, "application/json"),
            });
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly DomainService _domains;
    private readonly DockerApi _api = new("unix:///var/run/docker.sock", new EmptyEngine());
    // Where cloudflared runs: the host's network, or Gitberth's.
    private bool _tunnelOnHost = true;
    private readonly int _appId;

    public DomainPublishTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            // orb holds 3000 on the host, so kanban's web port 3000 is offered 3001.
            var orb = new AppDefinition { Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb" };
            orb.Ports.Add(new PortMapping { ContainerPort = 80, HostPort = 3000, IsWeb = true });
            var kanban = new AppDefinition { Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:1" };
            kanban.Ports.AddRange([new PortMapping { ContainerPort = 3000, Access = PortAccess.Internal, IsWeb = true }, new PortMapping { ContainerPort = 9229, Access = PortAccess.Internal }]);
            db.Apps.AddRange(orb, kanban);
            db.SaveChanges();
            _appId = kanban.Id;
            db.Settings.Add(new Setting { Key = SettingKeys.DomainsEnabled, Value = "true" });
            db.SaveChanges();
        }
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        var settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var cli = new DockerCli(options) { Runner = RunAsync };
        // Lazy, as Gitberth gives it: the map reads the tunnel's routes from the domains, which are made below.
        var map = new Lazy<HostPortMap>(() => new HostPortMap(new NetworkService(_api, _db, options), _db, _domains!,
            new HostScanner(cli, options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false }, settings,
            new ContainerEventWatcher(_api, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance), NullLogger<HostPortMap>.Instance));
        _domains = new DomainService(_db, settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance, map);
    }

    public void Dispose()
    {
        _api.Dispose();
        _connection.Dispose();
    }

    // A running cloudflared started with its tunnel's token, on the host's network or on Gitberth's.
    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c2VjcmV0"}"""));
        var networks = _tunnelOnHost ? """{"host":{}}""" : """{"gitberth":{}}""";
        return Task.FromResult(args switch
        {
            ["ps", ..] => new ProcessResult(0, """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""", ""),
            ["container", "inspect", "tunnel"] => new ProcessResult(0,
                $$$"""[{"Name":"/tunnel","Args":["tunnel","run","--token","{{{token}}}"],"Config":{"Cmd":[],"Env":[]},"HostConfig":{"NetworkMode":"{{{(_tunnelOnHost ? "host" : "bridge")}}}"},"NetworkSettings":{"Networks":{{{networks}}}}}]""", ""),
            _ => new ProcessResult(1, "", "unknown command " + string.Join(' ', args)),
        });
    }

    private AppDefinition Kanban()
    {
        using var db = _db.CreateDbContext();
        return db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().Single(a => a.Id == _appId);
    }

    [Fact]
    public async Task Adding_a_domain_to_an_internal_web_port_needs_a_host_port_first()
    {
        var added = await _domains.AddAsync(_appId, "app.harborlab.com");

        Assert.Equal(("app.harborlab.com needs the web port on the host. Set it to LAN or Host only.", true), (added.Error, added.NeedsHostBinding));
        Assert.Empty(Kanban().Domains);

        // On Gitberth's network cloudflared reaches kanban by name, so the domain is added as it is.
        _tunnelOnHost = false;
        var byName = await _domains.AddAsync(_appId, "app.harborlab.com");
        Assert.Equal((null, false), (byName.Error, byName.NeedsHostBinding));
        Assert.Equal(["app.harborlab.com"], Kanban().Domains.Select(d => d.Hostname));
        Assert.Equal(PortAccess.Internal, PortRules.WebOf(Kanban().Ports)!.Access);
    }

    [Fact]
    public async Task Add_and_publish_puts_the_web_port_on_the_host_only()
    {
        var added = await _domains.AddAndPublishAsync(_appId, "app.harborlab.com");

        // The web port's own number is orb's on the host, so the next one; kanban isn't running here, so nothing restarts yet.
        Assert.Equal((null, false, 3001, (int?)null), (added.Error, added.NeedsHostBinding, added.HostPort, added.DeploymentId));
        var kanban = Kanban();
        Assert.Equal(["app.harborlab.com"], kanban.Domains.Select(d => d.Hostname));
        Assert.Equal([(3000, PortAccess.HostOnly, (int?)3001, (string?)null, true), (9229, PortAccess.Internal, null, null, false)],
            kanban.Ports.OrderBy(p => p.Id).Select(p => (p.ContainerPort, p.Access, p.HostPort, p.HostIp, p.IsWeb)));
        // It takes the port when it's recreated.
        Assert.True(kanban.ConfigChangePending);
        // Its next domain is added as usual: the web port is on the host now.
        var next = await _domains.AddAsync(_appId, "harborlab.com");
        Assert.Equal((null, false), (next.Error, next.NeedsHostBinding));
        Assert.Equal(3001, PortRules.WebOf(Kanban().Ports)!.HostPort);
    }
}
