using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Gitberth.Tests;

/// <summary>A test of own LAN addresses against a real Docker engine and LAN, skipped unless GITBERTH_LAN_IT=1.</summary>
public sealed class LanEngineFactAttribute : FactAttribute
{
    public LanEngineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITBERTH_LAN_IT") != "1")
            Skip = "Runs against a real Docker engine and LAN through /var/run/docker.sock: set GITBERTH_LAN_IT=1 to run it.";
    }
}

/// <summary>
/// Own LAN addresses on the real engine and LAN, through Gitberth's own code: LanAddresses and the lan-check helper running
/// Gitberth's image, and NetworkService giving a container its address over the Engine API. Opt-in (GITBERTH_LAN_IT=1),
/// with Docker's socket at /var/run/docker.sock and the host's network (--network host), so the host scan reads the host.
/// Its guard lets a change through only for a container or network named gbtest-lan-*. The script that runs it makes the
/// own-addresses network and the container, says which test to run when, and removes everything after. Settings:
/// GITBERTH_LAN_IT_IMAGE (Gitberth's image), _NETWORK and _CONTAINER (the script's), _ADDRESS (the container's own address),
/// _HELD (a block with that address in it), _FREE (a block nobody uses), and _HELPER and _FREE_HELPER (where the helper sits
/// for each on the network).
/// </summary>
public sealed class LanEngineTests(ITestOutputHelper output) : IDisposable
{
    private const string Prefix = "gbtest-lan-";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-lan-it-" + Guid.NewGuid().ToString("N"));
    private readonly List<DockerApi> _apis = [];

    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable("GITBERTH_LAN_IT_" + name) is { Length: > 0 } value ? value : fallback;

    private static string Network => Setting("NETWORK", "gbtest-lan-net");

    private static string Container => Setting("CONTAINER", "gbtest-lan-web");

    private static string Address => Setting("ADDRESS", "10.1.1.249");

    public void Dispose()
    {
        foreach (var api in _apis) api.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [LanEngineFact]
    public async Task Tests_a_block_from_a_temporary_network_and_leaves_nothing()
    {
        // Own LAN addresses are off: no own-addresses network, so the test makes one of its own for each block.
        var lan = await AddressesAsync(networkName: Prefix + "off");
        var before = await NetworksAsync();

        var free = await lan.TestAsync(Setting("FREE", "10.1.1.236/30"));
        Say("free block", free);
        Assert.True(free.Passed);

        var held = await lan.TestAsync(Setting("HELD", "10.1.1.248/30"));
        Say("block with a device", held);
        Assert.False(held.Passed);
        Assert.StartsWith("A device answers on ", held.Lines[0].Text);
        Assert.Equal((true, "The router answers from the block"), (held.Lines[1].Ok, held.Lines[1].Text));

        // Its networks and helpers are gone again.
        Assert.Equal(before, await NetworksAsync());
        Assert.DoesNotContain((await new DockerEngine(Api()).ListContainersAsync(default)).OfType<JsonObject>(), c => Name(c).StartsWith(Prefix + "helper-", StringComparison.Ordinal));
    }

    [LanEngineFact]
    public async Task Gives_the_container_its_own_address_through_the_engine_api()
    {
        var networks = await NetworkServiceAsync();

        var change = await networks.SetLanAddressAsync(Container, Address);
        output.WriteLine($"{change.Error ?? change.Done}");
        Assert.True(change.Ok, change.Error);

        var inspect = (await Api().InspectContainerAsync(Container))!;
        var endpoint = inspect["NetworkSettings"]!["Networks"]![Network]!;
        output.WriteLine(endpoint.ToJsonString());
        Assert.Equal((Address, Address, -1L), (ContainerPorts.Str(endpoint["IPAMConfig"], "IPv4Address"), ContainerPorts.Str(endpoint, "IPAddress"), ContainerPorts.Number(endpoint, "GwPriority")));
        // Docker's default bridge keeps the gateway, and with it the published ports and the way out.
        Assert.Equal(0L, ContainerPorts.Number(inspect["NetworkSettings"]!["Networks"]!["bridge"], "GwPriority"));
        Assert.Equal(Address, (await networks.OwnAddressOfAsync(Container)).Address);
    }

    [LanEngineFact]
    public async Task The_lan_check_on_the_network_finds_the_address_in_use_and_the_router()
    {
        var helpers = Helpers();

        // Next to the container on the own-addresses network: ipvlan L2 children reach each other, so its address answers.
        var heldBlock = LanAddresses.ParseBlock(Setting("HELD", "10.1.1.248/30"))!;
        var helperAt = Setting("HELPER", heldBlock.LastText);
        var held = await helpers.LanCheckAsync(new LanCheck(Network, helperAt, null, "10.1.1.1", LanAddresses.DevicePorts,
            heldBlock.Addresses.Select(LanAddresses.Text).Where(a => a != helperAt).ToList()), default);
        output.WriteLine($"held block: answering {string.Join(", ", held.Answering.Order(StringComparer.Ordinal))}; open {string.Join("; ", held.Open.Select(o => $"{o.Key} {string.Join(",", o.Value)}"))}; router {held.RouterAnswers}; {held.Error}");
        Assert.Null(held.Error);
        Assert.Contains(Address, held.Answering);
        Assert.Contains(80, held.Open[Address]);
        Assert.True(held.RouterAnswers);

        var freeBlock = LanAddresses.ParseBlock(Setting("FREE", "10.1.1.236/30"))!;
        var freeAt = Setting("FREE_HELPER", freeBlock.LastText);
        var free = await helpers.LanCheckAsync(new LanCheck(Network, freeAt, null, "10.1.1.1", LanAddresses.DevicePorts,
            freeBlock.Addresses.Select(LanAddresses.Text).Where(a => a != freeAt).ToList()), default);
        output.WriteLine($"free block: answering {string.Join(", ", free.Answering)}; router {free.RouterAnswers}; {free.Error}");
        Assert.Null(free.Error);
        Assert.Empty(free.Answering);
        Assert.True(free.RouterAnswers);
    }

    [LanEngineFact]
    public async Task Turns_on_gives_an_address_and_turns_off()
    {
        // Settings' Turn on: the own-addresses network is made, and kept once the block passes its test from it.
        var lan = await AddressesAsync(networkName: Setting("NETWORK_ON", Prefix + "on"));
        var on = await lan.TurnOnAsync(Setting("BLOCK", "10.1.1.236/30"));
        Say("turn on", on);
        Assert.True(on.Passed);
        var state = await lan.StateAsync();
        Assert.True(state.On);

        // The LAN address card's Automatic: the next free address of the block, then the check from the network.
        var container = Setting("CONTAINER2", Prefix + "web2");
        var given = await lan.GiveAsync(container, null);
        output.WriteLine($"{given.Error ?? given.Done} | {given.Check}");
        Assert.True(given.Ok, given.Error);
        var view = await lan.ForAsync(container);
        Assert.True(view.Automatic);
        Assert.True(on.Block!.Contains(LanAddresses.TryParse(view.Address, out var ip) ? ip : 0));
        Assert.True(given.CheckOk, given.Check);

        // Turn off: the address goes, and the network.
        Assert.Null(await lan.TurnOffAsync());
        Assert.False((await lan.StateAsync()).On);
        Assert.Null((await Api().InspectContainerAsync(container))!["NetworkSettings"]!["Networks"]![Setting("NETWORK_ON", Prefix + "on")]);
    }

    [LanEngineFact]
    public async Task Suggests_the_highest_block_where_no_device_answers()
    {
        // From the host's own network, where the helper needs no address: the network's highest addresses, tried at once.
        var (block, problem) = await (await AddressesAsync(networkName: Prefix + "off")).SuggestAsync();
        output.WriteLine($"suggested {block?.Text} {problem}");
        Assert.NotNull(block);
    }

    [LanEngineFact]
    public async Task Takes_the_address_away_again()
    {
        var change = await (await NetworkServiceAsync()).SetLanAddressAsync(Container, null);
        output.WriteLine($"{change.Error ?? change.Done}");
        Assert.True(change.Ok, change.Error);
        Assert.Null((await Api().InspectContainerAsync(Container))!["NetworkSettings"]!["Networks"]![Network]);
    }

    private void Say(string what, LanTest test) =>
        output.WriteLine($"{what}: {test.Problem}{string.Join(" | ", test.Lines.Select(l => $"{(l.Ok ? "ok" : "x")} {l.Text}"))} {test.Note}");

    private GitberthOptions Options()
    {
        Directory.CreateDirectory(_dir);
        return new GitberthOptions { DataDir = _dir, RunningInContainer = true };
    }

    private async Task<Db> DbAsync()
    {
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options);
        Directory.CreateDirectory(_dir);
        await using (var context = db.CreateDbContext()) await context.Database.MigrateAsync();
        return db;
    }

    private async Task<NetworkService> NetworkServiceAsync(string? networkName = null) =>
        new(Api(), await DbAsync(), Options()) { LanNetwork = networkName ?? Network };

    // The engine for the helpers, guarded by container and network: they go by IDs, which only an inspect names.
    private HostHelpers Helpers() =>
        new(new GuardedEngine(new DockerEngine(Api(guarded: false))), Options(), new Output<HostHelpers>(output))
        {
            NamePrefix = Prefix + "helper-",
            InContainer = true,
            Image = Setting("IMAGE", "gitberth:local"),
        };

    private async Task<LanAddresses> AddressesAsync(string networkName)
    {
        var db = await DbAsync();
        var options = Options();
        // The host's own /proc: the test runs on the host's network.
        var scanner = new HostScanner(new DockerCli(options), options, new Output<HostScanner>(output)) { InContainer = false, OnLinux = true };
        return new LanAddresses(new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider())), scanner, Api(),
            new GuardedEngine(new DockerEngine(Api(guarded: false))), Helpers(), await NetworkServiceAsync(networkName), db, new Output<LanAddresses>(output));
    }

    private async Task<string> NetworksAsync() =>
        string.Join(",", (await Api().ListNetworksAsync()).Select(n => n.Name).Order(StringComparer.Ordinal));

    // The Engine API over Docker's socket; guarded, it reads anything and changes only a gbtest-lan-* container's networks.
    private DockerApi Api(bool guarded = true)
    {
        var socket = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await unix.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), ct);
                return new NetworkStream(unix, ownsSocket: true);
            },
        };
        var api = new DockerApi("unix:///var/run/docker.sock", guarded ? new Guard(socket) : socket);
        _apis.Add(api);
        return api;
    }

    private static string Name(JsonObject listed) =>
        (listed["Names"] as JsonArray ?? []).Select(n => n?.GetValue<string>().TrimStart('/') ?? "").FirstOrDefault(n => !n.Contains('/')) ?? "";

    private static void Ours(string? name)
    {
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"The LAN engine test refused to touch {name ?? "something unnamed"}: only gbtest-lan-* containers and networks.");
    }

    /// <summary>
    /// The Engine API, but a call that changes something is refused unless it's a connect or disconnect of a gbtest-lan-*
    /// container (by name, as NetworkService gives it) to or from a gbtest-lan-* network; reads go through.
    /// </summary>
    private sealed class Guard(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.Split('/') is not [_, _, "networks", var network, "connect" or "disconnect"]) throw new InvalidOperationException($"The LAN engine test refused {request.Method} {path}.");
                var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
                Ours(Uri.UnescapeDataString(network));
                Ours(body?["Container"]?.GetValue<string>());
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// The real engine, but a change is refused unless its container or network is named gbtest-lan-* (a new one's name, an
    /// existing one's as Docker has it), and a container may only be made on a gbtest-lan-* network or the host's.
    /// </summary>
    private sealed class GuardedEngine(IContainerEngine real) : IContainerEngine
    {
        public Task<EngineVersion> VersionAsync(CancellationToken ct) => real.VersionAsync(ct);

        public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => real.InspectContainerAsync(idOrName, ct);

        public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) => real.InspectImageAsync(nameOrId, ct);

        public Task<JsonArray> ListContainersAsync(CancellationToken ct) => real.ListContainersAsync(ct);

        public Task<JsonArray> ListNetworksAsync(CancellationToken ct) => real.ListNetworksAsync(ct);

        public Task<int> WaitAsync(string id, CancellationToken ct) => real.WaitAsync(id, ct);

        public Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct) => real.LogsAsync(id, tail, ct);

        public Task<JsonArray> ChangesAsync(string id, CancellationToken ct) => real.ChangesAsync(id, ct);

        public Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct)
        {
            Ours(name);
            var mode = ContainerPorts.Str(body["HostConfig"], "NetworkMode");
            if (mode != "host") Ours(mode);
            foreach (var (network, _) in body["NetworkingConfig"]?["EndpointsConfig"] as JsonObject ?? []) Ours(network);
            return real.CreateAsync(name, body, ct);
        }

        public async Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            await real.UpdateRestartPolicyAsync(id, policy, ct);
        }

        public async Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            return await real.UpdateAsync(id, resources, ct);
        }

        public async Task StopAsync(string id, int timeoutSeconds, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            await real.StopAsync(id, timeoutSeconds, ct);
        }

        public async Task RenameAsync(string id, string name, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            Ours(name);
            await real.RenameAsync(id, name, ct);
        }

        public async Task StartAsync(string id, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            await real.StartAsync(id, ct);
        }

        public async Task RemoveAsync(string id, bool force, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            await real.RemoveAsync(id, force, ct);
        }

        public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct)
        {
            Ours(ContainerPorts.Str(body, "Name"));
            return real.CreateNetworkAsync(body, ct);
        }

        public async Task RemoveNetworkAsync(string idOrName, CancellationToken ct)
        {
            var network = (await real.ListNetworksAsync(ct)).OfType<JsonObject>().FirstOrDefault(n => ContainerPorts.Str(n, "Name") == idOrName || ContainerPorts.Str(n, "Id") == idOrName);
            Ours(network is null ? idOrName : ContainerPorts.Str(network, "Name"));
            await real.RemoveNetworkAsync(idOrName, ct);
        }

        // The container's name as Docker has it, which must be a gbtest-lan one. One that isn't there is Docker's 404 to give.
        private async Task TargetAsync(string idOrName, CancellationToken ct)
        {
            if (await real.InspectContainerAsync(idOrName, ct) is { } inspect) Ours(ContainerPorts.Str(inspect, "Name").TrimStart('/'));
        }
    }

    private sealed class Output<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                output.WriteLine($"[{typeof(T).Name} {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
