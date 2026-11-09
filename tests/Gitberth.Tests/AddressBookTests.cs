using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>Where each container and app is reached, as every page shows it, from Docker's list and inspects captured on CT 101.</summary>
public class AddressBookTests
{
    private const string Lan = "10.1.1.80";

    private static Dictionary<string, NetworkContainer> Listed() =>
        NetworkContainer.ParseList(DockerFixtures.Text("containers-json.json")).ToDictionary(c => c.Name);

    private static NetworkPortConfig Config(string container) => NetworkPortConfig.From(DockerFixtures.Inspect(container));

    /// <summary>kanban with these ports saved, each published on its address the way the migration reads one: the first is its web port.</summary>
    private static AppDefinition Kanban(params (int Host, int Container, string? Ip)[] ports)
    {
        var app = new AppDefinition { Id = 2, Name = "kanban", Slug = "kanban" };
        var rows = ports.Length > 0 ? ports : [(3001, 3000, null)];
        app.Ports.AddRange(rows.Select((p, i) => new PortMapping
        {
            Id = 20 + i, AppId = 2, HostPort = p.Host, ContainerPort = p.Container, Access = ContainerPorts.AccessOf(p.Ip),
            HostIp = ContainerPorts.AccessOf(p.Ip) == PortAccess.Custom ? p.Ip : null, IsWeb = i == 0,
        }));
        app.Domains.AddRange([new CustomDomain { Id = 1, AppId = 2, Hostname = "harborlab.com" }, new CustomDomain { Id = 2, AppId = 2, Hostname = "app.harborlab.com", Main = true }]);
        return app;
    }

    private static NetworkContainer Running(string name, string networkMode, params NetworkPort[] ports) =>
        new($"c0ffee{name}", name, "img", "sha256:1", "running", "Up 1 hour", "", new Dictionary<string, string>(), ports, networkMode,
            networkMode == "bridge" ? [new NetworkEndpoint("bridge", "172.17.0.9", null, [], [], 0)] : [new NetworkEndpoint(networkMode, "172.21.0.9", null, [], [], 0)]);

    [Fact]
    public void An_app_has_its_domain_lan_and_internal_addresses()
    {
        var container = Listed()["gb-kanban"];
        var a = AddressBook.ForApp(Kanban(), container, Config("gb-kanban"), Lan, domainsOn: true);

        Assert.Equal(
            [
                (AddressKind.Domain, "https://app.harborlab.com", "https://app.harborlab.com/", true),
                (AddressKind.Domain, "https://harborlab.com", "https://harborlab.com/", true),
                (AddressKind.Lan, "http://10.1.1.80:3001", "http://10.1.1.80:3001", true),
                (AddressKind.Internal, "http://kanban:3000", null, true),
            ],
            a.Addresses.Select(x => (x.Kind, x.Text, x.Href, x.Live)));
        Assert.Equal("Main", a.Domains.First().Note);
        Assert.Equal((3000, 3001, true), (a.WebPort, a.Lan.Single().HostPort, a.Running));
    }

    [Fact]
    public async Task Every_address_bindings_use_the_lan_address_setting()
    {
        var listed = Listed();
        Assert.Equal("http://gitberth.lan:3001", AddressBook.ForApp(Kanban(), listed["gb-kanban"], Config("gb-kanban"), "gitberth.lan", domainsOn: false).Lan.Single().Text);
        var gitberth = AddressBook.ForContainer(listed["gitberth"], Config("gitberth"), ContainerRole.Self, "gitberth.lan", "gitberth");
        Assert.Equal(("8080:8080", AddressChipKind.Open, "http://gitberth.lan:8080"), (gitberth.Chips[0].Label, gitberth.Chips[0].Kind, gitberth.Chips[0].Value));
        Assert.Equal("gitberth.lan:8080 -> 8080/tcp (all addresses)", gitberth.Chips[0].Tooltip);

        // The LAN address of this host: Settings, then GITBERTH_PUBLIC_HOST, then what the host scan detected, then the browser's.
        var detected = 0;
        Task<string?> Detect()
        {
            detected++;
            return Task.FromResult<string?>("10.1.1.85");
        }
        Assert.Equal("10.1.1.90", await AddressBook.LanHostFromAsync("10.1.1.90", "10.1.1.80", Detect, "gitberth"));
        Assert.Equal("10.1.1.80", await AddressBook.LanHostFromAsync("  ", "10.1.1.80", Detect, "gitberth"));
        Assert.Equal(0, detected);
        Assert.Equal("10.1.1.85", await AddressBook.LanHostFromAsync(null, null, Detect, "gitberth"));
        Assert.Equal("gitberth", await AddressBook.LanHostFromAsync(null, null, () => Task.FromResult<string?>(null), "gitberth"));
    }

    [Fact]
    public void A_loopback_binding_is_host_only_and_never_a_link()
    {
        // Published for the tunnel only: -p 127.0.0.1:3002:3000.
        var container = Running("gb-kanban", "gitberth", new NetworkPort("127.0.0.1", 3000, 3002, "tcp"));
        var a = AddressBook.ForApp(Kanban((3002, 3000, "127.0.0.1")), container, NetworkPortConfig.None, Lan, domainsOn: false);

        var hostOnly = Assert.Single(a.HostOnly);
        Assert.Equal(("127.0.0.1:3002", null, "127.0.0.1:3002"), (hostOnly.Text, hostOnly.Href, hostOnly.Copy));
        Assert.Empty(a.Lan);
        // Neither the LAN nor the browser's machine reaches it, so there's nothing to open...
        Assert.Null(AddressBook.Primary(a));
        // ...but its domain is, when domains are on.
        Assert.Equal("https://app.harborlab.com/", AddressBook.Primary(AddressBook.ForApp(Kanban((3002, 3000, "127.0.0.1")), container, NetworkPortConfig.None, Lan, domainsOn: true)));

        var chip = Assert.Single(AddressBook.ForContainer(container, NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth").Chips);
        Assert.Equal((AddressChipKind.HostOnly, "3002:3000", "127.0.0.1:3002", "127.0.0.1:3002 -> 3000/tcp (host only)"), (chip.Kind, chip.Label, chip.Value, chip.Tooltip));
    }

    [Fact]
    public void A_domain_the_tunnel_reaches_by_name_is_live_without_a_host_port()
    {
        // kanban's web port is Internal: only exposed, for the containers on gitberth.
        var app = new AppDefinition { Id = 2, Name = "kanban", Slug = "kanban" };
        app.Ports.Add(new PortMapping { Id = 20, AppId = 2, ContainerPort = 3000, Access = PortAccess.Internal, IsWeb = true });
        app.Domains.Add(new CustomDomain { Id = 1, AppId = 2, Hostname = "app.harborlab.com", Main = true });
        var container = Running("gb-kanban", "gitberth", new NetworkPort(null, 3000, null, "tcp"));

        // cloudflared on gitberth sends the domain to http://kanban:3000, which answers...
        var byName = AddressBook.ForApp(app, container, NetworkPortConfig.None, Lan, domainsOn: true, tunnelByName: true);
        Assert.True(byName.Domains.Single().Live);
        Assert.Equal("https://app.harborlab.com/", AddressBook.Primary(byName));
        // ...but on the host's network it reaches an app only through a host port, which this one hasn't.
        Assert.False(AddressBook.ForApp(app, container, NetworkPortConfig.None, Lan, domainsOn: true).Domains.Single().Live);
        // Either way, only once the container has its web port.
        Assert.False(AddressBook.ForApp(app, Running("gb-kanban", "gitberth"), NetworkPortConfig.None, Lan, domainsOn: true, tunnelByName: true).Domains.Single().Live);
    }

    [Fact]
    public void Ipv6_addresses_are_bracketed()
    {
        var listed = Listed();
        Assert.Equal("http://[fd00::80]:3001", AddressBook.ForApp(Kanban(), listed["gb-kanban"], Config("gb-kanban"), "fd00::80", domainsOn: false).Lan.Single().Text);

        var container = Running("db", "gitberth", new NetworkPort("fd00::5", 5432, 5432, "tcp"), new NetworkPort("::1", 6379, 6379, "tcp"));
        var a = AddressBook.ForContainer(container, NetworkPortConfig.None, ContainerRole.Database, Lan, "gitberth");
        Assert.Equal(["[fd00::5]:5432", "[::1]:6379"], a.Addresses.Where(x => x.Kind is AddressKind.Lan or AddressKind.HostOnly).Select(x => x.Text));
        Assert.Equal("[fd00::80]", AddressBook.HostPart("fd00::80"));
        Assert.Equal("gitberth.lan", AddressBook.HostPart("gitberth.lan"));
    }

    [Fact]
    public void Databases_and_known_non_web_ports_are_copied_not_linked()
    {
        var postgres = Running("postgres", "gitberth", new NetworkPort("0.0.0.0", 5432, 5432, "tcp"), new NetworkPort("::", 5432, 5432, "tcp"));
        var chip = Assert.Single(AddressBook.ForContainer(postgres, NetworkPortConfig.None, ContainerRole.Database, Lan, "gitberth").Chips);
        Assert.Equal((AddressChipKind.Copy, "5432:5432", "10.1.1.80:5432"), (chip.Kind, chip.Label, chip.Value));

        // RabbitMQ is a database by its image, but its management UI is a web page, and it leads: the Apps list opens it.
        var rabbit = AddressBook.ForContainer(Running("rabbitmq", "gitberth", new NetworkPort("0.0.0.0", 5672, 5672, "tcp"), new NetworkPort("0.0.0.0", 15672, 15672, "tcp")),
            NetworkPortConfig.None, ContainerRole.Database, Lan, "gitberth");
        Assert.Equal([AddressChipKind.Open, AddressChipKind.Copy], rabbit.Chips.Select(c => c.Kind));
        Assert.Equal("http://10.1.1.80:15672", rabbit.Chips[0].Value);

        Assert.False(AddressBook.IsWeb(6379, "tcp", ContainerRole.Other, appWebPort: false));
        Assert.True(AddressBook.IsWeb(8080, "tcp", ContainerRole.Other, appWebPort: false));
        Assert.False(AddressBook.IsWeb(53, "udp", ContainerRole.Other, appWebPort: true));
        // An app's web port opens whatever its number.
        Assert.True(AddressBook.IsWeb(5432, "tcp", ContainerRole.App, appWebPort: true));
        // 443 and 8443 speak https.
        var tls = AddressBook.ForContainer(Running("proxy", "gitberth", new NetworkPort("0.0.0.0", 443, 8443, "tcp")), NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth");
        Assert.Equal("https://10.1.1.80:8443", tls.Chips.Single().Value);
    }

    [Fact]
    public void Live_bindings_win_and_saved_ones_show_as_not_live_yet()
    {
        var listed = Listed();
        // Saved as 3005, but the running container still publishes 3001.
        var moved = AddressBook.ForApp(Kanban((3005, 3000, null)), listed["gb-kanban"], Config("gb-kanban"), Lan, domainsOn: false);
        var lan = moved.Lan.Single();
        Assert.Equal(("http://10.1.1.80:3001", true, "Saved: 3005, not live yet"), (lan.Text, lan.Live, lan.Note));

        // A port saved but not on the running container at all yet.
        var added = AddressBook.ForApp(Kanban((3001, 3000, null), (9229, 9229, null)), listed["gb-kanban"], Config("gb-kanban"), Lan, domainsOn: false);
        Assert.Equal(("http://10.1.1.80:9229", false, "Saved: 9229, not live yet"), (added.Lan.Last().Text, added.Lan.Last().Live, added.Lan.Last().Note));

        // Stopped: what it was created with, not live.
        var stopped = listed["gb-kanban"] with { State = "exited", Status = "Exited (0) 1 minute ago", Ports = [] };
        var down = AddressBook.ForApp(Kanban(), stopped, Config("gb-kanban"), Lan, domainsOn: true);
        Assert.Equal(("http://10.1.1.80:3001", false, "Stopped"), (down.Lan.Single().Text, down.Lan.Single().Live, down.Lan.Single().Note));
        Assert.False(down.Domains.First().Live);
    }

    [Fact]
    public void A_restarting_container_shows_the_ports_it_was_created_with()
    {
        // Between one crash and the next start Docker lets go of its ports and lists none.
        var restarting = Listed()["gb-kanban"] with { State = "restarting", Status = "Restarting (1) 20 seconds ago", Ports = [] };

        var app = AddressBook.ForApp(Kanban(), restarting, Config("gb-kanban"), Lan, domainsOn: true);
        var lan = app.Lan.Single();
        Assert.Equal(("http://10.1.1.80:3001", false, "Restarting"), (lan.Text, lan.Live, lan.Note));
        Assert.False(app.Domains.First().Live);

        var container = AddressBook.ForContainer(restarting, Config("gb-kanban"), ContainerRole.App, Lan, "gitberth", Kanban());
        var chip = container.Chips.Single();
        Assert.Equal(("3001:3000", AddressChipKind.Copy, "10.1.1.80:3001 -> 3000/tcp (all addresses) · restarting"), (chip.Label, chip.Kind, chip.Tooltip));
        Assert.Contains(container.Addresses, a => a.Kind == AddressKind.Internal && a.Text == "http://kanban:3000");
    }

    [Fact]
    public void An_app_docker_wasnt_read_for_is_unknown_rather_than_internal_only()
    {
        Assert.Null(AddressList.Empty.For(2));

        var read = new AddressList(new Dictionary<string, ContainerAddresses>(), new Dictionary<int, AppAddresses> { [2] = AppAddresses.None(2) });
        Assert.Same(read.Apps[2], read.For(2));
        // An app added since the list was read isn't in it yet.
        Assert.Null(read.For(3));
    }

    [Fact]
    public void A_container_on_anothers_network_names_it_even_by_id()
    {
        var gluetun = Running("gluetun", "bridge") with { Id = "3f2a9c" + new string('0', 58) };
        // Compose writes network_mode: service:gluetun as container:<gluetun's full ID>.
        var qbittorrent = Running("qbittorrent", "bridge") with { NetworkMode = $"container:{gluetun.Id}", Networks = [] };
        var others = Listed().Values.Append(gluetun).ToList();

        Assert.Equal("gluetun", AddressBook.ForContainer(qbittorrent, NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth", others: others).SharesNetworkOf);
        Assert.Equal("gluetun", AddressBook.ForContainer(qbittorrent with { NetworkMode = "container:gluetun" }, NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth", others: others).SharesNetworkOf);
        // One that's gone shows as docker ps shows an ID: its first 12 characters.
        Assert.Equal("3f2a9c000000", AddressBook.ForContainer(qbittorrent, NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth", others: Listed().Values).SharesNetworkOf);
    }

    [Fact]
    public void Each_chip_is_named_by_the_label_it_shows_first()
    {
        var listed = Listed();
        var open = AddressBook.ForContainer(listed["gitberth"], Config("gitberth"), ContainerRole.Self, Lan, "gitberth").Chips[0];
        Assert.Equal("8080:8080, open http://10.1.1.80:8080", open.AccessibleName);
        var copy = AddressBook.ForContainer(Running("postgres", "gitberth", new NetworkPort("0.0.0.0", 5432, 5432, "tcp")), NetworkPortConfig.None, ContainerRole.Database, Lan, "gitberth").Chips.Single();
        Assert.Equal("5432:5432, copy 10.1.1.80:5432", copy.AccessibleName);
        var hostOnly = AddressBook.ForContainer(Running("web", "gitberth", new NetworkPort("127.0.0.1", 3000, 3002, "tcp")), NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth").Chips.Single();
        Assert.Equal("3002:3000, host only, copy 127.0.0.1:3002", hostOnly.AccessibleName);
        var unpublished = AddressBook.ForContainer(listed["postgres"], Config("postgres"), ContainerRole.Database, Lan, "gitberth").Chips.Single();
        Assert.Equal("5432/tcp, Not published. Containers on gitberth reach it as postgres:5432.", unpublished.AccessibleName);
    }

    [Fact]
    public void A_port_docker_picked_is_read_from_the_running_container()
    {
        // Started with -p 8080 (no host port): Docker picked 32768, and picks again at every start.
        var config = new NetworkPortConfig([new PortBinding(null, null, 8080, "tcp")], [new ExposedPort(8080, "tcp")], PublishAll: false);
        var container = Running("whoami", "gitberth", new NetworkPort("0.0.0.0", 8080, 32768, "tcp"), new NetworkPort("::", 8080, 32768, "tcp"));
        var a = AddressBook.ForContainer(container, config, ContainerRole.Other, Lan, "gitberth");

        var lan = a.Addresses.Single(x => x.Kind == AddressKind.Lan);
        Assert.Equal(("http://10.1.1.80:32768", 32768, "Random: changes at every start"), (lan.Text, lan.HostPort, lan.Note));
        Assert.Equal("32768:8080", a.Chips.Single().Label);
        // Stopped, there's no host port to show: Docker picks one when it starts.
        Assert.DoesNotContain(AddressBook.ForContainer(container with { State = "exited", Ports = [] }, config, ContainerRole.Other, Lan, "gitberth").Addresses,
            x => x.Kind == AddressKind.Lan);
    }

    [Fact]
    public void A_container_only_on_the_default_bridge_has_no_internal_address()
    {
        // sample-index: created with -p 0.0.0.0:9100:80 on Docker's default bridge, and stopped.
        var listed = Listed();
        var sample = AddressBook.ForContainer(listed["sample-index"], Config("sample-index"), ContainerRole.Other, Lan, "gitberth");
        Assert.DoesNotContain(sample.Addresses, x => x.Kind == AddressKind.Internal);
        Assert.Null(sample.Network);
        // Stopped: nothing answers there, so its address is to copy, not to open.
        var chip = sample.Chips.Single();
        Assert.Equal(("9100:80", AddressChipKind.Copy, "http://10.1.1.80:9100", "10.1.1.80:9100 -> 80/tcp (all IPv4 addresses) · stopped"),
            (chip.Label, chip.Kind, chip.Value, chip.Tooltip));
        Assert.Equal(("Stopped", false), (sample.Addresses.Single().Note, sample.Addresses.Single().Live));

        var unpublished = AddressBook.ForContainer(Running("web", "bridge", new NetworkPort(null, 80, null, "tcp")), NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth");
        Assert.Equal(("80/tcp", AddressChipKind.Unpublished, "Not published. It's only on Docker's default bridge, where containers have no names."),
            (unpublished.Chips[0].Label, unpublished.Chips[0].Kind, unpublished.Chips[0].Tooltip));
        var isolated = AddressBook.ForContainer(Running("batch", "none", new NetworkPort(null, 8000, null, "tcp")), NetworkPortConfig.None, ContainerRole.Other, Lan, "gitberth");
        Assert.Equal("Not published, and on no network.", isolated.Chips.Single().Tooltip);

        // postgres is on gitberth, stopped and unpublished: apps reach it by name.
        var postgres = AddressBook.ForContainer(listed["postgres"], Config("postgres"), ContainerRole.Database, Lan, "gitberth");
        Assert.Equal(("postgres:5432", false), (postgres.Addresses.Single().Text, postgres.Addresses.Single().Live));
        Assert.Equal(("5432/tcp", "Not published. Containers on gitberth reach it as postgres:5432."), (postgres.Chips.Single().Label, postgres.Chips.Single().Tooltip));

        // cloudflared is on the host's network: no ports of its own to show.
        var tunnel = AddressBook.ForContainer(listed["cloudflare-tunnel"], Config("cloudflare-tunnel"), ContainerRole.Tunnel, Lan, "gitberth");
        Assert.True(tunnel.HostNetwork);
        Assert.Empty(tunnel.Chips);
    }

    [Fact]
    public void The_primary_address_is_the_live_main_domain_then_the_web_port()
    {
        var listed = Listed();
        Assert.Equal("https://app.harborlab.com/", AddressBook.Primary(AddressBook.ForApp(Kanban(), listed["gb-kanban"], Config("gb-kanban"), Lan, domainsOn: true)));
        Assert.Equal("http://10.1.1.80:3001", AddressBook.Primary(AddressBook.ForApp(Kanban(), listed["gb-kanban"], Config("gb-kanban"), Lan, domainsOn: false)));

        // orb: no domains here; its web port is its TCP one.
        var orb = new AppDefinition { Id = 1, Name = "orb", Slug = "orb" };
        orb.Ports.AddRange([new PortMapping { Id = 2, HostPort = 5353, ContainerPort = 53, Protocol = "udp" }, new PortMapping { Id = 3, HostPort = 3000, ContainerPort = 80, IsWeb = true }]);
        Assert.Equal("http://10.1.1.80:3000", AddressBook.Primary(AddressBook.ForApp(orb, listed["gb-orb"], Config("gb-orb"), Lan, domainsOn: true)));

        // A worker with no ports has nothing to open.
        Assert.Null(AddressBook.Primary(AddressBook.ForApp(new AppDefinition { Id = 3, Slug = "worker" }, null, NetworkPortConfig.None, Lan, domainsOn: true)));
    }

    [Fact]
    public void A_search_for_a_port_finds_the_container()
    {
        var listed = Listed();
        var gitberth = AddressBook.ForContainer(listed["gitberth"], Config("gitberth"), ContainerRole.Self, Lan, "gitberth");
        Assert.True(gitberth.Matches("8080"));
        Assert.True(gitberth.Matches("8080:8080"));
        Assert.False(gitberth.Matches("5432"));
        Assert.True(AddressBook.ForContainer(listed["postgres"], Config("postgres"), ContainerRole.Database, Lan, "gitberth").Matches("5432/tcp"));
    }
}
