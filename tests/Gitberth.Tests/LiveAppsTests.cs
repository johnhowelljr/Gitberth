using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// orb (gb-orb, 3000->80) and kanban (gb-kanban, 3001->3000) run in containers the old pipeline started with
/// `-p 3000:80 --network gitberth --network-alias orb`. After the upgrade and the NetworkModel migration, Gitberth must see
/// them as before: up to date, with nothing pending, nothing to recreate or restart, and their domains and jobs going where
/// they went. Their containers are the real inspects from CT 101, and their ports come out of the real migration.
/// </summary>
public sealed class LiveAppsTests : IDisposable
{
    // The last migration before NetworkModel.
    private const string Before = "20261104215155_RenameGitDockBuildColumn";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>Domains are off here, so Cloudflare is never asked.</summary>
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cloudflare was asked while domains are off.");
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;

    public LiveAppsTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext()) db.GetService<IMigrator>().Migrate(Before);
        // As the old tables held them, written by hand: today's model has columns they didn't.
        OldSchema.InsertApp(_connection, 1, "orb", "gitberth/orb:1d2948c");
        OldSchema.InsertDomain(_connection, 1, "orchardlane.com", "http://localhost:3000");
        OldSchema.InsertDomain(_connection, 1, "www.orchardlane.com", "http://localhost:3000");
        OldSchema.InsertApp(_connection, 2, "kanban", "gitberth/kanban:425df8a");
        OldSchema.InsertDomain(_connection, 2, "app.harborlab.com", "http://localhost:3001", main: true);
        OldSchema.InsertDomain(_connection, 2, "harborlab.com", "http://localhost:3001");
        // As the old table held them: on every address, which the old pipeline published as -p 3000:80. Its form kept no
        // address or an empty one.
        foreach (var (id, appId, hostIp, hostPort, containerPort) in new[] { (1, 1, (string?)null, 3000, 80), (2, 2, "", 3001, 3000) })
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = """INSERT INTO "Ports" ("Id", "AppId", "Protocol", "HostIp", "HostPort", "ContainerPort") VALUES ($id, $app, 'tcp', $ip, $host, $container)""";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$app", appId);
            insert.Parameters.AddWithValue("$ip", (object?)hostIp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$host", hostPort);
            insert.Parameters.AddWithValue("$container", containerPort);
            insert.ExecuteNonQuery();
        }
        using (var db = _db.CreateDbContext()) db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private AppDefinition App(string slug)
    {
        using var db = _db.CreateDbContext();
        return db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Networks).Include(a => a.Domains).AsSplitQuery().Single(a => a.Slug == slug);
    }

    /// <summary>The Ports column docker ps shows for a container, from its live bindings: "0.0.0.0:3000->80/tcp, [::]:3000->80/tcp".</summary>
    private static string PsPorts(JsonObject inspect) =>
        string.Join(", ", (inspect["NetworkSettings"]?["Ports"] as JsonObject ?? new JsonObject()).SelectMany(p => p.Value is JsonArray { Count: > 0 } bindings
            ? bindings.OfType<JsonObject>().Select(b => $"{AddressBook.HostPart((string)b["HostIp"]!)}:{(string)b["HostPort"]!}->{p.Key}")
            : [p.Key]));

    [Theory]
    [InlineData("orb", "3000:80")]
    [InlineData("kanban", "3001:3000")]
    public void Their_old_containers_are_up_to_date_with_the_migrated_ports(string slug, string published)
    {
        var app = App(slug);
        var web = Assert.Single(app.Ports);
        Assert.Equal((PortAccess.Lan, (string?)null, true), (web.Access, web.HostIp, web.IsWeb));
        var container = DockerFixtures.Inspect(app.ContainerName);

        var spec = ContainerSpecFactory.For(app, app.CurrentImage!, [], "gitberth");
        Assert.True(spec.UpToDate(container));
        // Today's docker run publishes the same port and gives the same name as the old one did, in the newer form.
        var args = spec.BuildRunCommand().Args;
        Assert.Equal(["-p", published], args.Skip(args.IndexOf("-p")).Take(2));
        Assert.Equal(["--network", $"name=gitberth,alias={slug}"], args.Skip(args.IndexOf("--network")).Take(2));
        // A host the internet reaches over IPv6 would get the LAN's ports on IPv4 only at the next deploy: still the same port.
        Assert.True(ContainerSpecFactory.For(app, app.CurrentImage!, [], "gitberth", lanIpv4Only: true).UpToDate(container));
    }

    [Fact]
    public void A_saved_change_is_one_the_old_container_does_not_have()
    {
        var container = DockerFixtures.Inspect("gb-kanban");
        bool UpToDate(Action<AppDefinition> change)
        {
            var app = App("kanban");
            change(app);
            return ContainerSpecFactory.For(app, app.CurrentImage!, [], "gitberth").UpToDate(container);
        }

        Assert.False(UpToDate(a => a.Ports[0].HostPort = 3005));
        Assert.False(UpToDate(a => a.Ports[0].Access = PortAccess.HostOnly));
        Assert.False(UpToDate(a => (a.Ports[0].Access, a.Ports[0].HostPort) = (PortAccess.Internal, null)));
        Assert.False(UpToDate(a => a.Ports.Add(new PortMapping { Id = 9, ContainerPort = 9229, Access = PortAccess.Internal })));
        Assert.False(UpToDate(a => a.Networks.Add(new AppNetwork { Id = 1, Network = "media_default" })));
        // Moving the web marker changes nothing in the container.
        Assert.True(UpToDate(a => a.Ports[0].IsWeb = false));
    }

    [Fact]
    public async Task Nothing_is_pending_and_saving_them_as_they_are_restarts_nothing()
    {
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(_db, protector);
        // Nothing here runs docker.
        var cli = new DockerCli(options) { Runner = (args, _, _) => throw new InvalidOperationException($"docker {string.Join(' ', args)} was run.") };
        var domains = new DomainService(_db, settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var apps = new AppService(_db, protector, cli, settings, new HealthSettings(settings), domains, options, new RestartSchedules(settings), new WatchState());

        foreach (var slug in new[] { "orb", "kanban" })
        {
            var app = App(slug);
            Assert.False(app.ConfigChangePending || app.RebuildPending);
            var model = apps.ToModel((await apps.GetAsync(app.Id))!);
            // The Network tab's Save with the ports as they loaded, and the Settings tab's Save.
            var ports = await apps.SavePortsAsync(app.Id, model.Ports, model.PortsLoaded);
            Assert.Equal((true, false), (ports.Ok, ports.NeedsRestart));
            Assert.Equal(new SaveOutcome(false, false), await apps.SaveAsync(app.Id, model));
            var after = App(slug);
            Assert.False(after.ConfigChangePending || after.RebuildPending);
        }
    }

    [Theory]
    [InlineData("orb", "http://localhost:3000", "http://orb:80")]
    [InlineData("kanban", "http://localhost:3001", "http://kanban:3000")]
    public void Their_domains_jobs_and_addresses_are_where_they_were(string slug, string onHost, string byName)
    {
        var app = App(slug);
        var inspect = DockerFixtures.Inspect(app.ContainerName);
        var ports = PsPorts(inspect);
        var tunnel = new TunnelInfo("cloudflare-tunnel", "0123456789abcdef0123456789abcdef", "11111111-2222-3333-4444-555555555555", HostNetwork: true, OnAppNetwork: false);

        // The routes Gitberth wrote before the upgrade are where it sends the domains now, so its first sync changes nothing.
        Assert.Equal(onHost, TunnelRoutes.ServiceFor(app, ports, tunnel));
        Assert.All(app.Domains, d => Assert.True(TunnelRoutes.SameService(d.Service, onHost, app)));
        Assert.Equal(byName, TunnelRoutes.ServiceFor(app, ports, tunnel with { HostNetwork = false, OnAppNetwork = true }));

        // Scheduled jobs are called as before, by name inside a container and on the host port outside one.
        var running = new ContainerSummary(app.ContainerName, "running", "Up 5 days", app.CurrentImage!, ports, new Dictionary<string, string>());
        var web = app.Ports.Single();
        Assert.Equal(($"http://{app.ContainerName}:{web.ContainerPort}", (string?)null), CronScheduler.BaseUrl(app, running, inContainer: true));
        Assert.Equal(($"http://localhost:{web.HostPort}", (string?)null), CronScheduler.BaseUrl(app, running, inContainer: false));

        // The Addresses card has no saved value that isn't live yet, and its domains are live.
        var listed = NetworkContainer.ParseList(DockerFixtures.Text("containers-json.json")).Single(c => c.Name == app.ContainerName);
        var addresses = AddressBook.ForApp(app, listed, NetworkPortConfig.From(inspect), "10.1.1.80", domainsOn: true);
        Assert.Equal([$"http://10.1.1.80:{web.HostPort}"], addresses.Lan.Select(a => a.Text));
        Assert.All(addresses.Lan, a => Assert.Null(a.Note));
        Assert.All(addresses.Domains, d => Assert.True(d.Live));
    }
}
