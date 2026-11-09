using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
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
/// An app's ports as its Network tab and New app save them: on their own, checked first, and never touched by the
/// Settings tab's Save. kanban has its web port on the LAN at 3001 and a second port at 3002.
/// </summary>
public sealed class AppPortsTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>Domains are off, so Cloudflare is never asked.</summary>
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cloudflare was asked while domains are off.");
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly GitberthOptions _options = new() { DataDir = Path.GetTempPath() };
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly SettingsService _settings;
    private readonly DomainService _domains;
    private readonly AppService _apps;
    private readonly int _appId;

    public AppPortsTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:1" };
            app.Ports.AddRange([new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true }, new PortMapping { ContainerPort = 9000, HostPort = 3002 }]);
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        _settings = new SettingsService(_db, _protector);
        // Saving ports runs nothing: they're live once a deploy recreates the container.
        var cli = new DockerCli(_options) { Runner = (args, _, _) => throw new InvalidOperationException($"docker {string.Join(' ', args)} was run.") };
        _domains = new DomainService(_db, _settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _apps = new AppService(_db, _protector, cli, _settings, new HealthSettings(_settings), _domains, _options, new RestartSchedules(_settings), new WatchState());
    }

    public void Dispose() => _connection.Dispose();

    private static PortRow Row(int container, PortAccess access = PortAccess.Lan, int? host = null, bool web = false, string protocol = "tcp") =>
        new() { ContainerPort = container, Access = access, HostPort = host, IsWeb = web, Protocol = protocol };

    private async Task<AppDefinition> AppAsync() => (await _apps.GetAsync(_appId))!;

    private static List<(int, PortAccess, int?, bool)> Saved(AppDefinition app) =>
        app.Ports.OrderBy(p => p.Id).Select(p => (p.ContainerPort, p.Access, p.HostPort, p.IsWeb)).ToList();

    [Fact]
    public async Task An_internal_port_survives_a_save()
    {
        var before = await AppAsync();

        // Its second port is only for other containers now: it keeps no host port.
        var saved = await _apps.SavePortsAsync(_appId, [Row(3000, host: 3001, web: true), Row(9000, PortAccess.Internal, host: 3002)], AppSettingsModel.PortsKey(before.Ports));

        Assert.Equal((true, true), (saved.Ok, saved.NeedsRestart));
        var after = await AppAsync();
        Assert.Equal([(3000, PortAccess.Lan, 3001, true), (9000, PortAccess.Internal, null, false)], Saved(after));
        // It loads as it was saved, so saving that again changes nothing.
        var model = _apps.ToModel(after);
        Assert.Equal((PortAccess.Internal, (int?)null), (model.Ports[1].Access, model.Ports[1].HostPort));
        var again = await _apps.SavePortsAsync(_appId, model.Ports, model.PortsLoaded);
        Assert.Equal((true, false), (again.Ok, again.NeedsRestart));
        // Nor does the Settings tab's Save drop it.
        await _apps.SaveAsync(_appId, model);
        Assert.Equal(Saved(after), Saved(await AppAsync()));
    }

    [Fact]
    public async Task A_settings_save_keeps_the_ports()
    {
        var model = _apps.ToModel(await AppAsync());
        // A form whose ports are gone and whose loaded ports are unknown: Save once rewrote every port from those, and
        // the domains' routes went with the web port.
        model.Ports.Clear();
        model.PortsLoaded = null;
        model.MemoryLimitMb = 256;

        var outcome = await _apps.SaveAsync(_appId, model);

        Assert.True(outcome.NeedsRestart);
        Assert.Equal([(3000, PortAccess.Lan, 3001, true), (9000, PortAccess.Lan, 3002, false)], Saved(await AppAsync()));
        // An existing app's ports aren't the Settings tab's to check either.
        model.Ports.Add(new PortRow { ContainerPort = 0 });
        Assert.Empty(await _apps.ValidateAsync(_appId, model));
    }

    [Fact]
    public async Task Saving_ports_needs_a_restart_unless_only_the_web_marker_moved()
    {
        // The marker moves to 9000, which the container publishes already: live at once.
        var moved = await _apps.SavePortsAsync(_appId, [Row(3000, host: 3001), Row(9000, host: 3002, web: true)], AppSettingsModel.PortsKey((await AppAsync()).Ports));
        Assert.Equal((true, false), (moved.Ok, moved.NeedsRestart));
        var app = await AppAsync();
        Assert.False(app.ConfigChangePending);
        Assert.Equal(9000, PortRules.WebOf(app.Ports)!.ContainerPort);

        // A new host port is only live once the container is recreated: saved settings aren't live yet.
        var changed = await _apps.SavePortsAsync(_appId, [Row(3000, host: 3005), Row(9000, host: 3002, web: true)], AppSettingsModel.PortsKey(app.Ports));
        Assert.Equal((true, true), (changed.Ok, changed.NeedsRestart));
        Assert.True((await AppAsync()).ConfigChangePending);
    }

    [Fact]
    public async Task Ports_that_changed_while_the_page_was_open_are_not_saved()
    {
        var loaded = AppSettingsModel.PortsKey((await AppAsync()).Ports);
        // A deploy follows the app to another container port meanwhile.
        await using (var db = _db.CreateDbContext())
            await db.Ports.Where(p => p.AppId == _appId && p.ContainerPort == 3000).ExecuteUpdateAsync(s => s.SetProperty(p => p.ContainerPort, 8080));

        var saved = await _apps.SavePortsAsync(_appId, [Row(3000, host: 3005, web: true), Row(9000, host: 3002)], loaded);

        Assert.Equal([new PortProblem(null, PortField.Table, "Kanban's ports changed while this page was open (a deploy can move the container port). Reload, then change them again.")],
            saved.Problems);
        Assert.Equal([(8080, PortAccess.Lan, 3001, true), (9000, PortAccess.Lan, 3002, false)], Saved(await AppAsync()));
    }

    [Fact]
    public async Task Nothing_is_saved_until_the_rows_pass_their_checks()
    {
        var saved = await _apps.SavePortsAsync(_appId, [Row(3000, host: 3001, web: true), Row(9000, host: 3001)], AppSettingsModel.PortsKey((await AppAsync()).Ports));

        Assert.Equal([new PortProblem(1, PortField.HostPort, "Host port 3001 is listed twice.")], saved.Problems);
        Assert.False(saved.NeedsRestart);
        Assert.Equal([(3000, PortAccess.Lan, 3001, true), (9000, PortAccess.Lan, 3002, false)], Saved(await AppAsync()));
    }

    [Fact]
    public async Task A_new_app_saves_its_ports_with_its_settings()
    {
        var model = new AppSettingsModel { Name = "Blog", Ports = [Row(4321, host: 4321, web: true), Row(9229, PortAccess.Internal)] };

        Assert.Empty(await _apps.ValidateAsync(null, model));
        var id = await _apps.CreateAsync(new GitHubRepoRef("acme", "blog"), model, null);

        Assert.Equal([(4321, PortAccess.Lan, 4321, true), (9229, PortAccess.Internal, null, false)], Saved((await _apps.GetAsync(id))!));
        // Its rows are checked as the Network tab's are.
        Assert.Contains("The web port must be TCP.", await _apps.ValidateAsync(null, new AppSettingsModel { Name = "Dns", Ports = [Row(53, host: 53, web: true, protocol: "udp")] }));
    }

    [Fact]
    public async Task The_docker_run_preview_never_starts_the_host_scan_helper()
    {
        // The host's /proc as the helper prints it, with an IPv6 address the internet reaches on its way out.
        var files = DockerFixtures.ProcFiles();
        files["/proc/net/if_inet6"] += "\n20010db8000000000000000000000050 02 40 00 80     eth0\n";
        var output = string.Concat(files.Select(f => $"==> {f.Key} <==\n{f.Value}\n"));
        var runs = 0;
        ProcessResult Helper()
        {
            Interlocked.Increment(ref runs);
            return new ProcessResult(0, output, "");
        }
        var cli = new DockerCli(_options)
        {
            Runner = (args, _, _) => Task.FromResult(args[0] switch
            {
                "version" => new ProcessResult(0, "1.56\n", ""),
                "info" => new ProcessResult(0, "json-file\n", ""),
                "container" => new ProcessResult(0, "sha256:0c1a38670237\n", ""),
                "run" => Helper(),
                _ => new ProcessResult(0, "", ""),
            }),
        };
        // Every scan is out of date at once.
        var scanner = new HostScanner(cli, _options, NullLogger<HostScanner>.Instance) { InContainer = true, KeptFor = TimeSpan.Zero };
        var apps = new AppService(_db, _protector, cli, _settings, new HealthSettings(_settings), _domains, _options, new RestartSchedules(_settings), new WatchState(),
            hostScanner: scanner);
        var app = (await _apps.GetAsync(_appId))!;
        var off = new VercelView([], [], Off: true);

        // The app page asks for it every 5 seconds. Before the host is scanned, the LAN's ports are written as Docker's default...
        Assert.Contains("-p 3001:3000 ", await apps.DockerRunPreviewAsync(app, off));
        // ...and after, as the last scan says, however old it is.
        await scanner.GetAsync();
        for (var i = 0; i < 3; i++) Assert.Contains("-p 0.0.0.0:3001:3000 ", await apps.DockerRunPreviewAsync(app, off));
        // Only that scan ran the helper: a preview never starts one.
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref runs));
    }
}
