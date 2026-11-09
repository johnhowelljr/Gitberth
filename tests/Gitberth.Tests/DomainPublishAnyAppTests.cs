using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// BG-6: "Add domain and publish" on an app that isn't deployed from a repo. Its ports are its container's, so publishing
/// the web port is a change to the container: the change engine's plan comes back for the change dialog and nothing is
/// saved; the domain is added only once that change has published the port. cloudflared runs on the host's network here,
/// and no Cloudflare token is saved, so nothing is routed and Cloudflare is never asked.
/// </summary>
public sealed class DomainPublishAnyAppTests : IDisposable
{
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string TunnelId = "11111111-2222-4333-8444-555555555555";
    private const string Web = "gbtest-r2b-web";

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cloudflare was asked, but no token is saved.");
    }

    private readonly AppsRig _rig = new();
    private readonly DomainService _domains;
    private readonly int _appId;

    public DomainPublishAnyAppTests()
    {
        // nginx on port 80, running with nothing published: its web port is Internal, and cloudflared can't reach it.
        _rig.Engine.AddLike("sample-index", Web);
        _rig.Engine.Inspect(Web)["HostConfig"]!["PortBindings"] = new JsonObject();
        _appId = _rig.AddAsync(new AppDefinition
        {
            Name = Web, Slug = Web, Source = AppSource.Existing, Container = Web,
            Ports = [new PortMapping { ContainerPort = 80, Access = PortAccess.Internal, IsWeb = true }],
        }).GetAwaiter().GetResult();
        _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true").GetAwaiter().GetResult();

        var engine = new DockerEngine(_rig.Api);
        var host = new FakeHost(new FakeEngine(new FakeClock()));
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, _rig.ContainerLocks, new Lazy<PortChanger>(() => changer!), _rig.Activity, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, _rig.Journal, _rig.ContainerLocks, host, host, host, host, _rig.Containers, _rig.Options, NullLogger<PortChanger>.Instance,
            updater: updater);
        var service = new AppSettingsService(_rig.Catalog, _rig.Apps, new Lazy<AppActions>(() => _rig.Actions), changer, updater, engine, _rig.AppLocks, _rig.Domains,
            _rig.Health, _rig.Restarts, _rig.Protector, _rig.Db, _rig.Options, _rig.Activity, NullLogger<AppSettingsService>.Instance);
        // cloudflared as it runs on CT 101 (the host's network), with a token the locator can read.
        var tunnelCli = new DockerCli(_rig.Options) { Runner = TunnelAsync };
        var map = new Lazy<HostPortMap>(() => new HostPortMap(_rig.Network, _rig.Db, _domains!,
            new HostScanner(_rig.Cli, _rig.Options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false }, _rig.Settings, _rig.Events,
            NullLogger<HostPortMap>.Instance));
        _domains = new DomainService(_rig.Db, _rig.Settings, new CloudflareApi(new NoHttp()), new TunnelLocator(tunnelCli), tunnelCli, NullLogger<DomainService>.Instance,
            map, appSettings: new Lazy<AppSettingsService>(() => service));
    }

    public void Dispose() => _rig.Dispose();

    private Task<ProcessResult> TunnelAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c2VjcmV0"}"""));
        const string networks = """{"host":{}}""";
        return args switch
        {
            ["ps", ..] => Task.FromResult(new ProcessResult(0,
                """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""", "")),
            ["container", "inspect", "tunnel"] => Task.FromResult(new ProcessResult(0,
                $$$"""[{"Name":"/tunnel","Args":["tunnel","run","--token","{{{token}}}"],"Config":{"Cmd":[],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{{{networks}}}}}]""", "")),
            _ => _rig.Engine.RunAsync(args, options, ct),
        };
    }

    private async Task<AppDefinition> RowAsync() => await _rig.RowAsync(a => a.Id == _appId) ?? throw new InvalidOperationException("The app's row is gone.");

    [Fact]
    public async Task Add_and_publish_on_an_app_that_isnt_deployed_from_a_repo_returns_the_change_and_saves_nothing()
    {
        var added = await _domains.AddAndPublishAsync(_appId, "web.example.com");

        Assert.Null(added.Error);
        var port = Assert.IsType<int>(added.HostPort);
        // The change dialog's plan: the web port goes from Internal to the host only, on the port the map offered.
        var plan = Assert.IsType<SettingsPlan>(added.Plan);
        Assert.True(plan.CanApply, plan.Blocked ?? string.Join(" ", plan.Problems.Select(p => p.Message)));
        var change = Assert.IsType<PortChangePlan>(plan.Change);
        var line = Assert.Single(change.Changes);
        Assert.Equal("80/tcp", line.Port);
        Assert.Contains($"127.0.0.1:{port}", line.After);
        // Nothing was saved or changed: no domain, the web port still Internal, and Docker untouched.
        var row = await RowAsync();
        Assert.Empty(row.Domains);
        Assert.Equal((PortAccess.Internal, (int?)null), (PortRules.WebOf(row.Ports)!.Access, PortRules.WebOf(row.Ports)!.HostPort));
        Assert.Empty(_rig.Engine.Writes);
    }

    [Fact]
    public async Task The_domain_is_added_once_the_change_has_published_the_web_port()
    {
        var done = Task.FromResult(new PortChangeResult(PortChangeOutcome.Done, $"{Web} is back."));

        var added = await _domains.AddWhenPublishedAsync(_appId, "web.example.com", done);

        Assert.Null(added.Error);
        Assert.Equal(["web.example.com"], (await RowAsync()).Domains.Select(d => d.Hostname));
    }

    [Fact]
    public async Task A_change_that_didnt_publish_the_web_port_adds_no_domain_and_says_so()
    {
        var rolledBack = Task.FromResult(new PortChangeResult(PortChangeOutcome.RolledBack, $"The new {Web} didn't start, so the original is back."));

        var added = await _domains.AddWhenPublishedAsync(_appId, "web.example.com", rolledBack);

        Assert.Equal($"web.example.com wasn't added: its web port wasn't published. The new {Web} didn't start, so the original is back.", added.Error);
        Assert.Empty((await RowAsync()).Domains);
    }
}
