using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
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
/// Own LAN addresses: the block and the addresses in it, what the lan-check helper finds, and a container's own address
/// kept across a recreate, against CT 101's network (eth0, 10.1.1.0/24, the router at .1, the host at .80) and a LAN kept
/// in memory, where some devices answer and the router does or doesn't.
/// </summary>
public sealed class LanAddressTests : IDisposable
{
    private const string GitberthImage = "sha256:0c1a38670237d7bc11ee185585ec1309b54599d259ccd7930e81c2114472b125";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private static LanSubnet Subnet => LanSubnet.From(HostScanHelper.Read(DockerFixtures.ProcFiles(), DateTime.UtcNow))!;

    private static List<NetworkInfo> Docker() => NetworkInfo.ParseList(DockerFixtures.Text("networks-json.json"));

    private static NetworkInfo Net(string name, string subnet, bool helper = false) =>
        new("id", name, "ipvlan", "local", false, false, [subnet], [], helper ? new Dictionary<string, string> { [HostScanHelper.Label] = "lan-check" } : [],
            new Dictionary<string, string>());

    private static string? Problem(string text, IEnumerable<NetworkInfo>? networks = null) => LanAddresses.CheckBlock(text, Subnet, networks ?? Docker()).Problem;

    private static HashSet<string> Set(params string[] addresses) => new(addresses, StringComparer.Ordinal);

    private static uint Ip(string text) => LanAddresses.TryParse(text, out var ip) ? ip : throw new FormatException(text);

    [Fact]
    public void Reads_the_hosts_network_from_its_scan()
    {
        // The default route's interface, the network routed straight onto it, the router, and the host's address there.
        var scan = HostScanHelper.Read(DockerFixtures.ProcFiles(), DateTime.UtcNow);
        Assert.Equal("10.1.1.0/24", scan.Subnet);
        Assert.Equal(("eth0", "10.1.1.0/24", "10.1.1.1", "10.1.1.80", "10.1.1.255"), (Subnet.Interface, Subnet.Cidr, Subnet.RouterText, Subnet.HostText, LanAddresses.Text(Subnet.Broadcast)));
        Assert.Null(LanSubnet.From(HostScan.Failed("the helper container failed")));
    }

    [Fact]
    public void Validates_the_block()
    {
        var (block, problem) = LanAddresses.CheckBlock("10.1.1.224/28", Subnet, Docker());
        Assert.Null(problem);
        Assert.Equal(("10.1.1.224/28", 16, "10.1.1.224-10.1.1.239", "10.1.1.239", "10.1.1.224/28"), (block!.Text, block.Count, block.Range, block.LastText, block.IpRange));
        Assert.Equal("16 addresses, 10.1.1.224-10.1.1.239", block.Summary);
        // A range, spaces and all; Docker's ip-range takes one CIDR, the largest inside it, for a container joined by hand.
        var range = LanAddresses.CheckBlock(" 10.1.1.225 - 10.1.1.238 ", Subnet, Docker()).Block!;
        Assert.Equal(("10.1.1.225-10.1.1.238", 14, "10.1.1.228/30"), (range.Text, range.Count, range.IpRange));
        Assert.Equal("10.1.1.192/27", LanAddresses.ParseBlock("10.1.1.192-10.1.1.223")!.IpRange);

        // What it may be: a /24 to a /30 where one of its size starts, or a range of 4 to 256 addresses.
        Assert.Equal("A block is a /24 to a /30, like 10.1.1.224/28.", Problem("10.1.0.0/23"));
        Assert.Equal("A block is a /24 to a /30, like 10.1.1.224/28.", Problem("10.1.1.252/31"));
        Assert.Equal("10.1.1.230/28 doesn't start a block of its size: 10.1.1.224/28 does.", Problem("10.1.1.230/28"));
        Assert.Equal("A range holds 4 to 256 addresses.", Problem("10.1.1.230-10.1.1.232"));
        Assert.Equal("Write the block like 10.1.1.224/28, or as a range like 10.1.1.224-10.1.1.239.", Problem("10.1.1.224"));
        Assert.Equal("Write the block like 10.1.1.224/28, or as a range like 10.1.1.224-10.1.1.239.", Problem("10.1.1.239-10.1.1.224"));
        Assert.Equal("Type a block, like 10.1.1.224/28.", Problem(" "));

        // Where it may be: in the host's network, without its own or broadcast address, the router or the host.
        Assert.Equal("10.1.2.0/28 isn't in this host's network, 10.1.1.0/24.", Problem("10.1.2.0/28"));
        Assert.Equal("10.1.1.0/24 holds 10.1.1.0, the network's own address.", Problem("10.1.1.0/24"));
        Assert.Equal("10.1.1.240/28 holds 10.1.1.255, the network's broadcast address.", Problem("10.1.1.240/28"));
        Assert.Equal("10.1.1.1-10.1.1.8 holds the router's address, 10.1.1.1.", Problem("10.1.1.1-10.1.1.8"));
        Assert.Equal("10.1.1.80/30 holds this host's own address, 10.1.1.80.", Problem("10.1.1.80/30"));

        // Nor in a Docker network's addresses; gitberth-lan's and the lan-check's are the host's own network, and don't count.
        var networks = Docker();
        networks.Add(Net("backup", "10.1.1.64/26"));
        Assert.Equal("10.1.1.96/28 overlaps Docker's network backup (10.1.1.64/26).", Problem("10.1.1.96/28", networks));
        networks.Add(Net(LanAddresses.Network, "10.1.1.0/24"));
        networks.Add(Net("gitberth-helper-lan-check-1a2b3c4d5e6f", "10.1.1.0/24", helper: true));
        Assert.Null(Problem("10.1.1.224/28", networks));
        // The example is the highest /28 that's neither the network's ends, the router's or the host's.
        Assert.Equal("10.1.1.224/28", LanAddresses.Example(Subnet));
    }

    [Fact]
    public void Suggests_the_highest_block_where_no_device_answers()
    {
        // What answered on CT 101's LAN on 2026-11-06, from the highest 64 addresses.
        var answering = Set("10.1.1.198", "10.1.1.199", "10.1.1.200", "10.1.1.204", "10.1.1.206", "10.1.1.214", "10.1.1.222", "10.1.1.223",
            "10.1.1.228", "10.1.1.233", "10.1.1.241", "10.1.1.242", "10.1.1.244", "10.1.1.250", "10.1.1.254");

        // .240/28 holds the broadcast address, and devices answer in .224/28, .208/28 and .192/28.
        Assert.Equal("10.1.1.176/28", LanAddresses.Suggest(Subnet, Docker(), answering, Ip("10.1.1.1"))?.Text);
        // Only what was tried counts: below it, nothing is known.
        Assert.Null(LanAddresses.Suggest(Subnet, Docker(), answering, Ip("10.1.1.180")));
        Assert.Equal("10.1.1.224/28", LanAddresses.Suggest(Subnet, Docker(), Set(), Ip("10.1.1.1"))?.Text);
    }

    [Fact]
    public void Picks_the_next_free_address()
    {
        var block = LanAddresses.ParseBlock("10.1.1.224/28")!;

        // The lan-check helper sits at the block's last address, which automatic addresses leave to it.
        Assert.Equal("10.1.1.239", LanAddresses.HelperAddress(block, Set()));
        Assert.Equal("10.1.1.224", LanAddresses.NextFree(block, Set(), Set(), "10.1.1.239"));
        // A container holds .224 (or Gitberth keeps it for one), and a device answers on .225: the next is .226.
        Assert.Equal("10.1.1.226", LanAddresses.NextFree(block, Set("10.1.1.224"), Set("10.1.1.225"), "10.1.1.239"));
        // A container with .239 as its static address moves the helper down.
        Assert.Equal("10.1.1.238", LanAddresses.HelperAddress(block, Set("10.1.1.239")));

        // Every address taken: nowhere to sit, and nothing to give.
        var all = block.Addresses.Select(LanAddresses.Text).ToHashSet(StringComparer.Ordinal);
        Assert.Null(LanAddresses.HelperAddress(block, all));
        Assert.Null(LanAddresses.NextFree(block, all.Where(a => a != "10.1.1.239").ToHashSet(StringComparer.Ordinal), Set(), "10.1.1.239"));
    }

    [Fact]
    public void Validates_a_static_address()
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal) { ["10.1.1.230"] = "Kanban" };

        // Any address of the host's network, in the block or not, as Docker allows a fixed address anywhere in the subnet.
        Assert.Null(LanAddresses.WhyNotStatic("10.1.1.100", Subnet, held));
        Assert.Null(LanAddresses.WhyNotStatic(" 10.1.1.231 ", Subnet, held));
        Assert.Equal("10.1.1.230 is Kanban's own address.", LanAddresses.WhyNotStatic("10.1.1.230", Subnet, held));
        Assert.Equal("10.1.1.1 is the router's.", LanAddresses.WhyNotStatic("10.1.1.1", Subnet, held));
        Assert.Equal("10.1.1.80 is this host's own address.", LanAddresses.WhyNotStatic("10.1.1.80", Subnet, held));
        Assert.Equal("10.1.1.0 is the network's own address.", LanAddresses.WhyNotStatic("10.1.1.0", Subnet, held));
        Assert.Equal("10.1.1.255 is the network's broadcast address.", LanAddresses.WhyNotStatic("10.1.1.255", Subnet, held));
        Assert.Equal("10.1.3.168 isn't in this host's network, 10.1.1.0/24.", LanAddresses.WhyNotStatic("10.1.3.168", Subnet, held));
        foreach (var wrong in new[] { "10.1.1", "10.1.1.300", "10.1.1.0x10", "printer.lan", "" })
            Assert.Equal("Type an address of this host's network, 10.1.1.0/24.", LanAddresses.WhyNotStatic(wrong, Subnet, held));
    }

    [Fact]
    public void Parses_arp_answers()
    {
        // The lan-check on CT 101, at .247 on an ipvlan network, over .240-.255 and the router (the devices' MACs replaced).
        var scan = LanScan.Read(DockerFixtures.Text("lan-check-output.txt"), "10.1.1.1");

        Assert.Null(scan.Error);
        // A complete ARP entry (0x2, a MAC) is a device, whatever its ports said: .244 and .254 opened none. An incomplete
        // one (0x0, no MAC) is nobody, and the router is told apart.
        Assert.Equal(["10.1.1.242", "10.1.1.244", "10.1.1.250", "10.1.1.254"], scan.Answering.Order(StringComparer.Ordinal));
        Assert.Equal([80, 443], scan.Open["10.1.1.250"]);
        Assert.True(scan.RouterAnswers);

        // The router by its ARP entry alone, or by a port alone; a device by an open port alone.
        const string Head = "==> /proc/net/arp <==\nIP address       HW type     Flags       HW address            Mask     Device\n";
        Assert.True(LanScan.Read(Head + "10.1.1.1         0x1         0x2         02:00:00:00:00:01     *        eth0\n", "10.1.1.1").RouterAnswers);
        Assert.True(LanScan.Read("open 10.1.1.1 53\n" + Head, "10.1.1.1").RouterAnswers);
        Assert.Equal(["10.1.1.9"], LanScan.Read("open 10.1.1.9 22\n" + Head, null).Answering);
        // An entry Linux gave up on is nobody, and a router that never answered fails the test.
        var nobody = LanScan.Read(Head + "10.1.1.1         0x1         0x0         00:00:00:00:00:00     *        eth0\n", "10.1.1.1");
        Assert.Equal((false, 0), (nobody.RouterAnswers, nobody.Answering.Count));
        Assert.Null(LanScan.Read(Head, null).RouterAnswers);
        // Output that stops before the ARP table says nothing.
        Assert.Equal("the check helper's output was cut short", LanScan.Read("open 10.1.1.9 22\n", null).Error);
    }

    [Fact]
    public void The_lan_check_runs_gitberths_image_with_nothing_else()
    {
        var body = HostScanHelper.LanCheckBody(GitberthImage, ["/data"], "gitberth-lan", "10.1.1.239", "10.1.1.1", [80, 443, 22], ["10.1.1.224", "10.1.1.225"]);

        Assert.Equal(GitberthImage, body["Image"]!.GetValue<string>());
        Assert.Equal("""["bash"]""", body["Entrypoint"]!.ToJsonString());
        var cmd = body["Cmd"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal("-c", cmd[0]);
        Assert.Equal(["bash", "10.1.1.1", "80,443,22", "10.1.1.224", "10.1.1.225"], cmd[2..]);
        // TCP connects in batches, cut off after 3 s, the router on DNS and the web ports, then the ARP table. No ping: no NET_RAW.
        Assert.Contains("exec 3<>\"/dev/tcp/$1/$2\"", cmd[1]);
        Assert.Contains("for p in 53 80 443", cmd[1]);
        Assert.Contains("sleep 3", cmd[1]);
        Assert.Contains("[ $n -lt 60 ]", cmd[1]);
        Assert.EndsWith("cat /proc/net/arp\n", cmd[1]);
        Assert.DoesNotContain("ping", cmd[1]);
        // On gitberth-lan at its address, read-only, without capabilities, 32 MiB; a log, since that's how it answers.
        var host = body["HostConfig"]!;
        Assert.Equal("gitberth-lan", host["NetworkMode"]!.GetValue<string>());
        Assert.Equal("""{"EndpointsConfig":{"gitberth-lan":{"IPAMConfig":{"IPv4Address":"10.1.1.239"}}}}""", body["NetworkingConfig"]!.ToJsonString());
        Assert.True(host["ReadonlyRootfs"]!.GetValue<bool>());
        Assert.Equal("""["ALL"]""", host["CapDrop"]!.ToJsonString());
        Assert.Equal(32L * 1024 * 1024, host["Memory"]!.GetValue<long>());
        Assert.Equal("""{"Type":"json-file"}""", host["LogConfig"]!.ToJsonString());
        Assert.Equal("""{"/data":""}""", host["Tmpfs"]!.ToJsonString());
        Assert.Equal("""{"gitberth.helper":"lan-check"}""", body["Labels"]!.ToJsonString());

        // On the host's own network it needs no address.
        var onHost = HostScanHelper.LanCheckBody(GitberthImage, [], "host", null, null, [80], ["10.1.1.200"]);
        Assert.Equal(("host", null), (onHost["HostConfig"]!["NetworkMode"]!.GetValue<string>(), onHost["NetworkingConfig"]));
        Assert.Equal("-", onHost["Cmd"]![3]!.GetValue<string>());

        // gitberth-lan itself: ipvlan L2 on the host's interface, in its network, the router as gateway, the block for Docker to give out.
        Assert.Equal("""{"Driver":"ipvlan","Options":{"parent":"eth0","ipvlan_mode":"l2"},"IPAM":{"Driver":"default","Config":[{"Subnet":"10.1.1.0/24","Gateway":"10.1.1.1","IPRange":"10.1.1.224/28"}]},"Labels":{"gitberth.managed":"true"},"Name":"gitberth-lan"}""",
            LanAddresses.NetworkBody("gitberth-lan", Subnet, "10.1.1.224/28", new Dictionary<string, string> { ["gitberth.managed"] = "true" }).ToJsonString());
    }

    [Fact]
    public void Leftovers_are_the_lan_checks_and_their_networks_a_restart_left_behind()
    {
        var since = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var containers = new JsonArray(
            new JsonObject { ["Id"] = "check-old", ["Created"] = since.AddMinutes(-1).ToUnixTimeSeconds(), ["Labels"] = new JsonObject { [HostScanHelper.Label] = "lan-check" } },
            new JsonObject { ["Id"] = "check-now", ["Created"] = since.AddSeconds(1).ToUnixTimeSeconds(), ["Labels"] = new JsonObject { [HostScanHelper.Label] = "lan-check" } });
        Assert.Equal(["check-old"], HostScanHelper.Leftovers(containers, since));

        // Docker writes a network's creation time with nanoseconds, in the host's zone; gitberth-lan is never a leftover.
        static JsonObject Network(string id, string created, string? kind) => new()
        {
            ["Id"] = id,
            ["Created"] = created,
            ["Labels"] = kind is null ? new JsonObject { ["gitberth.managed"] = "true" } : new JsonObject { [HostScanHelper.Label] = kind },
        };
        var networks = new JsonArray(
            Network("net-old", "2026-11-06T07:59:58.462757753-04:00", "lan-check"),
            Network("net-now", "2026-11-06T08:00:03.000000001-04:00", "lan-check"),
            Network("gitberth-lan", "2026-11-01T07:59:58.462757753-04:00", null));
        Assert.Equal(["net-old"], HostScanHelper.LeftoverNetworks(networks, since));
    }

    [Fact]
    public void An_app_takes_its_address_with_every_deploy_but_not_the_new_version_next_to_it()
    {
        var app = new AppDefinition
        {
            Id = 1,
            Slug = "kanban",
            Ports = [new PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000, IsWeb = true }],
            Networks = [new AppNetwork { Id = 1, Network = LanAddresses.Network, IPv4Address = "10.1.1.230" }],
        };
        static List<string> Networks(ContainerSpec spec)
        {
            var args = spec.BuildRunCommand().Args;
            return args.Select((a, i) => (a, i)).Where(x => x.a == "--network").Select(x => args[x.i + 1]).ToList();
        }

        // The app's own container: pinned, at -1, so its published ports and its way out stay on the shared network.
        var final = ContainerSpecFactory.For(app, "img", [], "gitberth");
        Assert.Equal(["name=gitberth,alias=kanban", "name=gitberth-lan,gw-priority=-1,alias=kanban,ip=10.1.1.230"], Networks(final));
        Assert.Contains("--network name=gitberth-lan,gw-priority=-1,alias=kanban,ip=10.1.1.230", final.ToDisplayString().Replace(" \\\n  ", " ", StringComparison.Ordinal));
        // A deploy's new version next to the old one: the old one has the address until it stops, so it isn't on gitberth-lan.
        Assert.Equal(["name=gitberth"], Networks(ContainerSpecFactory.For(app, "img", [], "gitberth", name: "gb-kanban_next", bridge: true)));

        // A container on gitberth-lan without the address, or another one, isn't up to date with the app's settings.
        static JsonObject With(string? fixedIp) => new()
        {
            ["HostConfig"] = new JsonObject { ["PortBindings"] = JsonNode.Parse("""{"3000/tcp":[{"HostIp":"","HostPort":"3001"}]}""") },
            ["NetworkSettings"] = new JsonObject
            {
                ["Networks"] = new JsonObject
                {
                    ["gitberth"] = new JsonObject { ["Aliases"] = new JsonArray("kanban") },
                    [LanAddresses.Network] = new JsonObject { ["Aliases"] = new JsonArray("kanban"), ["IPAMConfig"] = fixedIp is null ? null : new JsonObject { ["IPv4Address"] = fixedIp } },
                },
            },
        };
        Assert.True(final.UpToDate(With("10.1.1.230")));
        Assert.False(final.UpToDate(With(null)));
        Assert.False(final.UpToDate(With("10.1.1.231")));
    }

    // ---- Against a LAN kept in memory: the engine (FakeEngine) and its API over HTTP, CT 101's host scan, and devices. ----

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly Lan _lan;
    private readonly GitberthOptions _options = new() { DataDir = Path.GetTempPath(), RunningInContainer = true };

    public LanAddressTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Apps.Add(new AppDefinition { Name = "Kanban", Slug = "kanban", Owner = "acme", Repo = "kanban" });
            db.SaveChanges();
        }
        _engine = new FakeEngine(_clock) { OnCreate = b => b.Listens = [80] };
        _lan = new Lan(_engine);
        _engine.OnCreate = b =>
        {
            b.Listens = [80];
            _lan.Answer(b);
        };
        _engine.Images["gitberth:local"] = new JsonObject { ["Id"] = GitberthImage, ["Config"] = new JsonObject { ["Volumes"] = new JsonObject { ["/data"] = new JsonObject() } } };
        _engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = "sha256:0e52bd054126cdc040b1276c0af347aeb02a41b9cfe305d6094767ac30e97949" };
        foreach (var name in new[] { "sample-index", "gb-kanban", "gitberth", "cloudflare-tunnel" })
        {
            var inspect = DockerFixtures.Inspect(name);
            if (name is "sample-index" or "gb-kanban")
            {
                inspect["State"]!["Running"] = true;
                inspect["State"]!["Status"] = "running";
                inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
            }
            _engine.Add(inspect).Listens = name == "gb-kanban" ? [3000] : [80];
        }
    }

    public void Dispose()
    {
        _lan.Dispose();
        _connection.Dispose();
    }

    private DockerApi Api() => new("unix:///var/run/docker.sock", _lan);

    private NetworkService Networks() => new(Api(), _db, _options);

    private HostHelpers Helpers() => new(_engine, _options, NullLogger<HostHelpers>.Instance) { InContainer = true, Image = "gitberth:local" };

    private LanAddresses Addresses(NetworkService? networks = null) =>
        new(new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider())),
            new HostScanner(new DockerCli(_options), _options, NullLogger<HostScanner>.Instance)
            {
                InContainer = false,
                OnLinux = true,
                ReadFile = path => DockerFixtures.ProcFiles().GetValueOrDefault(path),
            },
            Api(), _engine, Helpers(), networks ?? Networks(), _db, NullLogger<LanAddresses>.Instance);

    private JsonObject Endpoint(string container, string network) =>
        (JsonObject)_engine.InspectContainerAsync(container, default).Result!["NetworkSettings"]!["Networks"]![network]!;

    [Fact]
    public async Task Turns_on_only_when_the_block_passes_its_test()
    {
        var lan = Addresses();
        _lan.Devices["10.1.1.230"] = [80];

        // A device answers in the block: the test says where, and nothing stays behind, its network included.
        var test = await lan.TestAsync("10.1.1.224/28");
        Assert.Equal([(false, "A device answers on 10.1.1.230: pick a block no device uses."), (true, "The router answers from the block")],
            test.Lines.Select(l => (l.Ok, l.Text)));
        Assert.Contains(_engine.Calls, c => c.StartsWith("create network gitberth-helper-lan-check-", StringComparison.Ordinal));
        Assert.DoesNotContain(_engine.Networks.OfType<JsonObject>(), n => ((string)n["Name"]!).StartsWith("gitberth-helper-", StringComparison.Ordinal));
        Assert.DoesNotContain(_engine.Boxes, b => b.Name.StartsWith("gitberth-helper-", StringComparison.Ordinal));
        // It sat at the block's last address, on a network whose only address to give is that one.
        var made = _lan.Helpers.Single();
        Assert.Equal(("10.1.1.239", "10.1.1.239/32"), (made.Address, made.IpRange));
        Assert.DoesNotContain("10.1.1.239", made.Targets);

        // Turned on with it anyway: gitberth-lan is made, tested from, and removed again.
        var refused = await lan.TurnOnAsync("10.1.1.224/28");
        Assert.False(refused.Passed);
        Assert.Equal("Gitberth removed gitberth-lan again.", refused.Note);
        Assert.Contains("create network gitberth-lan", _engine.Calls);
        Assert.Contains("remove network gitberth-lan", _engine.Calls);
        Assert.False((await lan.StateAsync()).On);

        // The router dropping the block's addresses fails it too (Proxmox's IP filter, say).
        _lan.RouterAnswers = false;
        Assert.Equal((false, "The router 10.1.1.1 doesn't answer from the block. A firewall between this host and the router may drop addresses other than 10.1.1.80: on Proxmox, the container's IP filter."),
            (await lan.TestAsync("10.1.1.208/28")).Lines.Select(l => (l.Ok, l.Text)).Last());

        // A free block with the router answering: on, with the block saved, and gitberth-lan as Turn on makes it.
        _lan.RouterAnswers = true;
        var on = await lan.TurnOnAsync("10.1.1.208/28");
        Assert.True(on.Passed);
        Assert.Equal(["No device answers on 10.1.1.208-10.1.1.223", "The router answers from the block"], on.Lines.Select(l => l.Text));
        var state = await lan.StateAsync();
        Assert.Equal((true, "10.1.1.208/28", "10.1.1.0/24"), (state.On, state.Block, state.Subnet?.Cidr));
        var network = _engine.Networks.OfType<JsonObject>().Single(n => (string)n["Name"]! == "gitberth-lan");
        Assert.Equal("""{"parent":"eth0","ipvlan_mode":"l2"}""", network["Options"]!.ToJsonString());
        Assert.Equal("""[{"Subnet":"10.1.1.0/24","Gateway":"10.1.1.1","IPRange":"10.1.1.208/28"}]""", network["IPAM"]!["Config"]!.ToJsonString());
        Assert.Equal("""{"gitberth.managed":"true"}""", network["Labels"]!.ToJsonString());
        Assert.Equal("There's a network called gitberth-lan already, so own LAN addresses are on.", (await lan.TurnOnAsync("10.1.1.192/28")).Problem);
    }

    [Fact]
    public async Task Gives_a_container_its_own_address_and_takes_it_away()
    {
        var lan = Addresses();
        Assert.Equal(LanAddresses.OffText, (await lan.GiveAsync("sample-index", null)).Error);
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);
        _lan.Devices["10.1.1.224"] = [443];
        _lan.Writes.Clear();

        // Automatic: the next free address, past the one a device answers on, joined at once and pinned, at -1.
        var given = await lan.GiveAsync("sample-index", null);
        Assert.Equal((null, "sample-index has its own address 10.1.1.225 now."), (given.Error, given.Done));
        Assert.Equal(("/v1.56/networks/gitberth-lan/connect", """{"Container":"sample-index","EndpointConfig":{"GwPriority":-1,"IPAMConfig":{"IPv4Address":"10.1.1.225"}}}"""),
            _lan.Writes.Single());
        // Then the helper tries it on the ports it's known to listen on.
        Assert.Equal(("10.1.1.225 answers on 80.", true), (given.Check, given.CheckOk));
        var view = await lan.ForAsync("sample-index");
        Assert.Equal((true, "10.1.1.225", true, true), (view.On, view.Address, view.Automatic, view.Attached));
        await using (var db = _db.CreateDbContext())
            Assert.Equal(("sample-index", "gitberth-lan", "10.1.1.225"), db.KeptNetworks.AsEnumerable().Select(k => (k.ContainerName, k.Network, k.IPv4Address)).Single());

        // Static: any free address of the network, in the block or not; not another container's, nor one a device answers on.
        Assert.Equal("10.1.1.225 is sample-index's own address.", (await lan.GiveAsync("gb-kanban", "10.1.1.225")).Error);
        _lan.Devices["10.1.1.100"] = [];
        Assert.Equal("A device answers on 10.1.1.100. Pick another address.", (await lan.GiveAsync("gb-kanban", "10.1.1.100")).Error);
        var kanban = await lan.GiveAsync("gb-kanban", "10.1.1.101");
        Assert.Equal("Kanban has its own address 10.1.1.101 now.", kanban.Done);
        Assert.False((await lan.ForAsync("gb-kanban")).Automatic);
        // An app's is saved for its deploys, which give it to the app's own container.
        await using (var db = _db.CreateDbContext())
            Assert.Equal((LanAddresses.Network, "10.1.1.101"), db.AppNetworks.AsEnumerable().Select(n => (n.Network, n.IPv4Address)).Single());

        // Changed: it leaves with the old address and joins with the new one.
        _lan.Writes.Clear();
        Assert.Equal("Kanban has its own address 10.1.1.102 now.", (await lan.GiveAsync("gb-kanban", "10.1.1.102")).Done);
        Assert.Equal(["/v1.56/networks/gitberth-lan/disconnect", "/v1.56/networks/gitberth-lan/connect"], _lan.Writes.Select(w => w.Path));
        Assert.Equal("10.1.1.102", (string)Endpoint("gb-kanban", LanAddresses.Network)["IPAMConfig"]!["IPv4Address"]!);
        Assert.Equal(["Kanban 10.1.1.102", "sample-index 10.1.1.225"], (await lan.HoldersAsync()).Select(h => $"{h.Title} {h.Address}").Order(StringComparer.Ordinal));

        // Removed: off gitberth-lan, and no longer kept.
        Assert.Equal("sample-index no longer has its own address 10.1.1.225.", (await lan.RemoveAsync("sample-index")).Done);
        Assert.Null((await lan.ForAsync("sample-index")).Address);
        await using (var db = _db.CreateDbContext()) Assert.Empty(db.KeptNetworks);

        // Never for Gitberth itself, nor a container on the host's network.
        Assert.Equal("Gitberth's own container keeps to the host's address: its page is there.", (await lan.ForAsync("gitberth")).Refusal);
        Assert.Equal("cloudflare-tunnel uses the host's network, so it's on the host's own address.", (await lan.GiveAsync("cloudflare-tunnel", null)).Error);
    }

    [Fact]
    public async Task Turning_off_takes_every_address_away_and_removes_the_network()
    {
        var lan = Addresses();
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);
        Assert.True((await lan.GiveAsync("sample-index", null)).Ok);
        Assert.True((await lan.GiveAsync("gb-kanban", null)).Ok);
        // Kept for one that's gone since, too.
        await using (var db = _db.CreateDbContext())
        {
            db.KeptNetworks.Add(new KeptNetwork { ContainerName = "pg-old", Network = LanAddresses.Network, IPv4Address = "10.1.1.230" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, (await lan.HoldersAsync()).Count);

        Assert.Null(await lan.TurnOffAsync());

        Assert.False((await lan.StateAsync()).On);
        Assert.Contains("remove network gitberth-lan", _engine.Calls);
        Assert.DoesNotContain(_engine.Boxes, b => b.Endpoints.ContainsKey(LanAddresses.Network));
        await using (var db = _db.CreateDbContext())
        {
            Assert.Empty(db.KeptNetworks);
            Assert.Empty(db.AppNetworks);
            Assert.Null(await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OwnLanBlock));
        }
    }

    [Fact]
    public async Task Keeps_the_address_across_a_recreate()
    {
        var networks = Networks();
        var lan = Addresses(networks);
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);
        Assert.Equal("sample-index has its own address 10.1.1.230 now.", (await lan.GiveAsync("sample-index", "10.1.1.230")).Done);

        // Recreated elsewhere (docker run, compose up, an image update): the new container isn't on gitberth-lan. When it
        // starts, NetworkKeeper joins it again with the same address, pinned, at -1.
        var box = _engine.Get("sample-index");
        box.Endpoints.Remove(LanAddresses.Network);
        box.Addresses.Remove(LanAddresses.Network);
        _lan.Writes.Clear();
        var api = Api();
        var keeper = new NetworkKeeper(networks, api, new ContainerEventWatcher(api, new StatsStore(_options), NullLogger<ContainerEventWatcher>.Instance),
            new ContainerLocks(), _db, NullLogger<NetworkKeeper>.Instance);
        Assert.Equal(["sample-index joined gitberth-lan again."], (await keeper.KeepAsync("sample-index", default)).Select(r => r.Done));
        Assert.Equal(("/v1.56/networks/gitberth-lan/connect", """{"Container":"sample-index","EndpointConfig":{"GwPriority":-1,"IPAMConfig":{"IPv4Address":"10.1.1.230"}}}"""),
            _lan.Writes.Single());

        // Recreated by Gitberth to publish a port: PortChanger's clone carries the endpoint with its address, and the new
        // container takes it once the old one has stopped, so its devices find it where they did.
        var host = new FakeHost(_engine) { ByName = false };
        var changer = new PortChanger(_engine, new MemoryJournal(), new ContainerLocks(), host, host, host, host, host, _options, NullLogger<PortChanger>.Instance, _clock);
        var plan = await changer.PlanAsync("sample-index", [new PortRule(80, "tcp", PortAccess.Lan, 9201)]);
        Assert.Contains("gitberth-lan (names sample-index, IP 10.1.1.230)", plan.Keeps);
        var result = await changer.ApplyAsync(plan);
        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var made = _engine.Get("sample-index");
        Assert.NotEqual(box.Id, made.Id);
        var endpoint = made.Endpoints[LanAddresses.Network]!;
        Assert.Equal(("10.1.1.230", -1), ((string)endpoint["IPAMConfig"]!["IPv4Address"]!, (int)endpoint["GwPriority"]!));
        // ipvlan gives every address the interface's own MAC: create would refuse one of the old container's.
        Assert.Null(endpoint["MacAddress"]);
        Assert.Equal("10.1.1.230", made.Addresses[LanAddresses.Network]);
        Assert.Equal("10.1.1.230", (await lan.ForAsync("sample-index")).Address);
    }

    [Fact]
    public async Task Join_never_offers_gitberth_lan_and_leaving_it_asks_the_cards_question()
    {
        var networks = Networks();
        var lan = Addresses(networks);
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);

        // Addresses come only from the LAN address card.
        Assert.DoesNotContain(LanAddresses.Network, (await networks.NetworksOfAsync("sample-index")).Joinable);
        Assert.Equal("gitberth-lan is for own LAN addresses: give sample-index one in its LAN address card.",
            (await networks.JoinAsync("sample-index", LanAddresses.Network)).Error);

        // With one, gitberth-lan is on its Networks card, kept; leaving it takes the address away, after the card's question.
        Assert.True((await lan.GiveAsync("sample-index", "10.1.1.231")).Ok);
        var row = (await networks.NetworksOfAsync("sample-index")).Networks.Single(n => n.Network == LanAddresses.Network);
        Assert.Equal(("10.1.1.231", "Own LAN address · kept", null), (row.IpAddress, row.Note, row.LeaveRefusal));
        Assert.Equal("10.1.1.231", (await networks.PlanLeaveAsync("sample-index", LanAddresses.Network)).OwnAddress);
        Assert.Null((await networks.PlanLeaveAsync("sample-index", "bridge")).OwnAddress);
        Assert.True((await networks.LeaveAsync("sample-index", LanAddresses.Network)).Ok);
        Assert.Null((await lan.ForAsync("sample-index")).Address);

        // The Network page names it, and lists a member's address while it's stopped.
        var listed = await networks.ListAsync();
        Assert.Equal("Own LAN addresses", listed.Networks.Single(n => n.Network.Name == LanAddresses.Network).Note);
    }

    [Fact]
    public async Task An_own_address_waits_while_a_port_change_isnt_settled()
    {
        var journal = new MemoryJournal();
        var locks = new ContainerLocks();
        var host = new FakeHost(_engine) { ByName = false };
        var changer = new PortChanger(_engine, journal, locks, host, host, host, host, host, _options, NullLogger<PortChanger>.Instance, _clock);
        var networks = new NetworkService(Api(), _db, _options, locks, changer: new Lazy<PortChanger>(() => changer));
        var lan = Addresses(networks);
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);
        Assert.True((await lan.GiveAsync("sample-index", "10.1.1.230")).Ok);
        // A change of sample-index a restart of Gitberth cut short, which recovery hasn't settled yet: it carries the
        // container's networks over itself, or puts the original back on the ones it had.
        var row = await journal.AddAsync(new ContainerChange
        {
            ContainerName = "sample-index",
            Summary = "published 80/tcp on the LAN",
            OldId = _engine.Get("sample-index").Id,
            OldName = "sample-index_old_" + _engine.Get("sample-index").Id[..12],
            TempName = "sample-index_new_" + _engine.Get("sample-index").Id[..12],
            WasRunning = true,
            OldRestartPolicy = """{"Name":"no","MaximumRetryCount":0}""",
            Step = ChangeStep.Verifying,
        }, default);
        _lan.Writes.Clear();
        const string busy = "Gitberth is already changing sample-index. Try again when that's done.";

        Assert.Equal(busy, (await lan.GiveAsync("sample-index", "10.1.1.231")).Error);
        Assert.Equal(busy, (await lan.RemoveAsync("sample-index")).Error);
        Assert.Empty(_lan.Writes);
        Assert.Equal("10.1.1.230", (await lan.ForAsync("sample-index")).Address);

        // Settled: it goes ahead.
        await journal.UpdateAsync(row with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done }, default);
        Assert.Equal("sample-index no longer has its own address 10.1.1.230.", (await lan.RemoveAsync("sample-index")).Done);
    }

    [Fact]
    public async Task The_addresses_card_lists_its_own_address_on_every_port()
    {
        var lan = Addresses();
        Assert.True((await lan.TurnOnAsync("10.1.1.224/28")).Passed);
        Assert.True((await lan.GiveAsync("sample-index", "10.1.1.230")).Ok);
        var snapshot = await Networks().SnapshotAsync();
        var sample = snapshot.Containers.Single(c => c.Name == "sample-index");

        var addresses = AddressBook.ForContainer(sample, snapshot.ConfigOf(sample), ContainerRole.Other, "10.1.1.80", "gitberth").Addresses;

        // Its web port as a link, on its own address, as well as on its port on the host.
        var own = addresses.Single(a => a.Kind == AddressKind.OwnLan);
        Assert.Equal(("http://10.1.1.230:80", "http://10.1.1.230:80", 80, true), (own.Text, own.Href, own.ContainerPort, own.Live));
        Assert.Contains(addresses, a => a.Kind == AddressKind.Lan);
        // A database's port is copied, not opened; with no port known, the address alone.
        var postgres = sample with { Image = "postgres:18" };
        Assert.Equal("10.1.1.230:80", AddressBook.ForContainer(postgres, snapshot.ConfigOf(sample), ContainerRole.Database, "10.1.1.80", "gitberth").Addresses
            .Single(a => a.Kind == AddressKind.OwnLan) is { Href: null } copied ? copied.Text : null);
        var bare = sample with { Ports = [] };
        Assert.Equal("10.1.1.230", AddressBook.ForContainer(bare, NetworkPortConfig.None, ContainerRole.Other, "10.1.1.80", "gitberth").Addresses
            .Single(a => a.Kind == AddressKind.OwnLan).Text);
    }

    /// <summary>
    /// A LAN and the Engine API over it, kept in memory. The API's answers come from the engine (FakeEngine): its list,
    /// inspects and networks, and connects and disconnects that change its containers, with Docker's refusal of an address
    /// another running container holds. The lan-check helpers the engine runs answer for the LAN's <see cref="Devices"/>,
    /// the router, and the containers with an own address there (ipvlan L2 children reach each other).
    /// </summary>
    private sealed class Lan(FakeEngine engine) : HttpMessageHandler
    {
        public Dictionary<string, int[]> Devices { get; } = new(StringComparer.Ordinal);

        public bool RouterAnswers { get; set; } = true;

        /// <summary>Each POST: its path and body.</summary>
        public List<(string Path, string Body)> Writes { get; } = [];

        /// <summary>Each lan-check helper: where it sat, the ip-range of the network it was on, and what it tried.</summary>
        public List<(string? Address, string? IpRange, IReadOnlyList<string> Targets)> Helpers { get; } = [];

        /// <summary>Gives a lan-check helper what it would print on this LAN, and has it exit a moment after it starts.</summary>
        public void Answer(FakeEngine.Box helper)
        {
            if (helper.Config["Labels"]?[HostScanHelper.Label]?.GetValue<string>() != HostScanHelper.LanCheckKind) return;
            var args = helper.Config["Cmd"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            var (router, ports, targets) = (args[3], args[4].Split(',').Select(int.Parse).ToList(), args[5..]);
            var network = (string)helper.HostConfig["NetworkMode"]!;
            var at = helper.Endpoints[network]?["IPAMConfig"]?["IPv4Address"]?.GetValue<string>();
            var range = engine.Networks.OfType<JsonObject>().FirstOrDefault(n => (string)n["Name"]! == network)?["IPAM"]?["Config"]?[0]?["IPRange"]?.GetValue<string>();
            Helpers.Add((at, range, targets));
            // On gitberth-lan the other containers' own addresses answer too.
            var children = engine.Boxes.Where(b => b.Running && b.Addresses.TryGetValue(network, out _) && network != "host")
                .ToDictionary(b => b.Addresses[network], b => b.Listens.ToArray(), StringComparer.Ordinal);
            var output = new StringBuilder();
            var arp = new List<string>();
            foreach (var target in targets)
            {
                var open = Devices.TryGetValue(target, out var device) ? device : children.GetValueOrDefault(target);
                if (open is null)
                {
                    arp.Add($"{target,-16} 0x1         0x0         00:00:00:00:00:00     *        eth0");
                    continue;
                }
                foreach (var port in ports.Where(open.Contains)) output.Append($"open {target} {port}\n");
                arp.Add($"{target,-16} 0x1         0x2         02:00:00:00:00:{arp.Count:x2}     *        eth0");
            }
            if (router != "-" && RouterAnswers)
            {
                output.Append($"open {router} 53\n");
                arp.Add($"{router,-16} 0x1         0x2         02:00:00:00:00:01     *        eth0");
            }
            output.Append("==> /proc/net/arp <==\nIP address       HW type     Flags       HW address            Mask     Device\n");
            foreach (var line in arp) output.Append(line).Append('\n');
            helper.Output = output.ToString();
            helper.ExitsAfter = TimeSpan.FromMilliseconds(100);
            helper.ExitWith = 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Writes) Writes.Add((path, body));
                return Change(path, JsonNode.Parse(body)!.AsObject());
            }
            if (path == "/version") return Answer(DockerFixtures.Text("version.json"));
            if (path == "/v1.56/containers/json") return Answer(await ListAsync());
            if (path == "/v1.56/networks") return Answer(engine.Networks.ToJsonString());
            var parts = path.Split('/');
            if (parts is [_, _, "networks", var id])
                return Network(Uri.UnescapeDataString(id)) is { } network ? Answer(network.ToJsonString()) : Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound);
            if (parts is [_, _, "containers", var name, "json"])
                return await engine.InspectContainerAsync(Uri.UnescapeDataString(name), cancellationToken) is { } inspect
                    ? Answer(inspect.ToJsonString())
                    : Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound);
            return Answer("""{"message":"page not found"}""", HttpStatusCode.NotFound);
        }

        // Docker's list: what each container's inspect says, the way GET containers/json gives it.
        private async Task<string> ListAsync()
        {
            var list = new JsonArray();
            foreach (var box in engine.Boxes.ToList())
            {
                var inspect = (await engine.InspectContainerAsync(box.Id, default))!;
                list.Add(new JsonObject
                {
                    ["Id"] = box.Id,
                    ["Names"] = new JsonArray("/" + box.Name),
                    ["Image"] = box.Config["Image"]?.DeepClone(),
                    ["ImageID"] = box.Image,
                    ["Command"] = "run",
                    ["State"] = inspect["State"]!["Status"]!.DeepClone(),
                    ["Status"] = "",
                    ["Labels"] = box.Config["Labels"]?.DeepClone(),
                    ["Ports"] = new JsonArray(box.Live.SelectMany(l => l.Value.Select(b => (JsonNode)new JsonObject
                    {
                        ["IP"] = b.Ip,
                        ["PrivatePort"] = int.Parse(l.Key.Split('/')[0]),
                        ["PublicPort"] = b.Port,
                        ["Type"] = l.Key.Split('/')[1],
                    })).ToArray()),
                    ["HostConfig"] = new JsonObject { ["NetworkMode"] = box.HostConfig["NetworkMode"]?.DeepClone() },
                    ["NetworkSettings"] = new JsonObject { ["Networks"] = inspect["NetworkSettings"]!["Networks"]!.DeepClone() },
                });
            }
            return list.ToJsonString();
        }

        // POST networks/{network}/connect and /disconnect, on the engine's containers.
        private HttpResponseMessage Change(string path, JsonObject body)
        {
            var parts = path.Split('/');
            if (parts is not [_, _, "networks", var id, var action] || Network(Uri.UnescapeDataString(id)) is not { } network)
                return Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound);
            if (engine.Find((string)body["Container"]!) is not { } box) return Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound);
            var name = (string)network["Name"]!;
            if (action == "disconnect")
            {
                box.Addresses.Remove(name);
                return box.Endpoints.Remove(name) ? Answer("") : Answer($$"""{"message":"container is not connected to network {{name}}"}""", HttpStatusCode.Forbidden);
            }
            if (box.Endpoints.ContainsKey(name)) return Answer($$"""{"message":"endpoint with name {{box.Name}} already exists in network {{name}}"}""", HttpStatusCode.Forbidden);
            var config = (JsonObject)(body["EndpointConfig"]?.DeepClone() ?? new JsonObject());
            var fixedIp = config["IPAMConfig"]?["IPv4Address"]?.GetValue<string>();
            if (box.Running && fixedIp is not null && engine.Boxes.Any(o => o != box && o.Running && o.Addresses.GetValueOrDefault(name) == fixedIp))
                return Answer("""{"message":"Address already in use"}""", HttpStatusCode.Forbidden);
            box.Endpoints[name] = config;
            if (box.Running) box.Addresses[name] = fixedIp ?? "10.1.1.224";
            return Answer("");
        }

        private JsonObject? Network(string nameOrId) =>
            engine.Networks.OfType<JsonObject>().FirstOrDefault(n => (string)n["Name"]! == nameOrId || (string)n["Id"]! == nameOrId);

        private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
