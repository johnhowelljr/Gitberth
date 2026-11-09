using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Components.Shared.Network;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// The Network tab's "How it's reached" diagram as data, in the states the apps on CT 101 are in: kanban published on the
/// LAN with domains through the tunnel, postgres unpublished, kanban on the host only, cloudflare-tunnel on the host's
/// network, a container in another one's network, a stopped one, one on Docker's default bridge only. And where each
/// element goes on the grid, left to right and top to bottom.
/// </summary>
public sealed class ReachDiagramTests
{
    private static ReachInput Kanban(params PortRule[] ports) => new()
    {
        Name = "kanban", Title = "kanban", Image = "gitberth/kanban:425df8a", LanHost = "10.1.1.80",
        Ports = ports.Length > 0 ? ports : [new PortRule(3000, "tcp", PortAccess.Lan, 3001, "")], WebPort = 3000,
        Domains = ["app.harborlab.com", "harborlab.com", "www.harborlab.com"], Tunnel = new ReachTunnel("cloudflare-tunnel", HostNetwork: true, OnShared: false),
        Networks = [new ReachNetwork("gitberth", ["gitberth", "orb", "postgres"], true)], OwnLanPossible = true,
    };

    private static ReachInput Postgres() => new()
    {
        Name = "postgres", Title = "postgres", Image = "postgres:18", LanHost = "10.1.1.80", Database = true,
        Ports = [new PortRule(5432, "tcp", PortAccess.Internal)],
        Networks = [new ReachNetwork("gitberth", ["gitberth", "kanban", "orb"], true)],
    };

    [Fact]
    public void Kanban_is_reached_from_the_internet_through_the_tunnel_from_the_lan_and_by_name()
    {
        var m = ReachModel.Build(Kanban());

        Assert.Equal(["Internet", "Your LAN"], m.ThroughHost.Select(l => l.Source.Title));
        var internet = m.ThroughHost[0];
        Assert.Equal(("app.harborlab.com +2", "localhost:3001", "Cloudflare tunnel", "cloudflare-tunnel"),
            (internet.Source.Sub, internet.Label, internet.Via?.Title, internet.Via?.Sub));
        Assert.Equal(("any device on your network", "10.1.1.80:3001", false), (m.ThroughHost[1].Source.Sub, m.ThroughHost[1].Label, m.ThroughHost[1].Ghost));
        Assert.Equal((3001, ReachTone.Lan, "TCP · LAN · all addresses", false), (m.HostPort!.Port, m.HostPort.Tone, m.HostPort.Sub, m.HostPort.Ghost));

        // Straight in: its network by name, and its own LAN address, off, with the button that turns it on.
        Assert.Equal(("gitberth network", "kanban:3000"), (m.Direct[0].Source.Title, m.Direct[0].Label));
        Assert.Equal("gitberth · orb · postgres", m.Direct[0].Source.Sub);
        Assert.Equal((true, "Off", ReachAction.TurnOnLanAddress), (m.Direct[1].Ghost, m.Direct[1].Label, m.Direct[1].Action));

        Assert.Equal([3000], m.Container.Sockets.Select(s => s.Port));
        Assert.True(m.Container.Sockets[0].Web);
        Assert.Contains("From the internet (app.harborlab.com, harborlab.com, www.harborlab.com): localhost:3001, through the cloudflare tunnel", m.Caption);
        Assert.Contains("From your LAN: 10.1.1.80:3001, to host port 3001 to container port 3000.", m.Caption);
    }

    [Fact]
    public void An_unpublished_database_shows_the_lan_path_off_with_publish_on_lan()
    {
        var m = ReachModel.Build(Postgres());

        var lan = Assert.Single(m.ThroughHost);
        Assert.Equal((true, "Not published", ReachAction.PublishOnLan, "Publish on LAN"), (lan.Ghost, lan.Label, lan.Action, lan.ActionText));
        Assert.Equal((null, true, "Publish to give it one"), (m.HostPort!.Port, m.HostPort.Ghost, m.HostPort.Sub));
        Assert.Equal("postgres:5432", Assert.Single(m.Direct).Label);
        Assert.Equal("PostgreSQL", m.Container.Sockets[0].Service);
        // No own LAN address offered when the host has them off and the page didn't say it may have one.
        Assert.DoesNotContain(m.Direct, l => l.Action == ReachAction.TurnOnLanAddress);
    }

    [Fact]
    public void Host_only_is_reached_by_the_tunnel_and_not_from_the_lan()
    {
        var m = ReachModel.Build(Kanban(new PortRule(3000, "tcp", PortAccess.HostOnly, 3002, "127.0.0.1")));

        Assert.Equal((3002, ReachTone.Host, "TCP · 127.0.0.1 only"), (m.HostPort!.Port, m.HostPort.Tone, m.HostPort.Sub));
        Assert.Equal("localhost:3002", m.ThroughHost[0].Label);
        Assert.Equal((true, "Not on the LAN", ReachAction.PublishOnLan), (m.ThroughHost[1].Ghost, m.ThroughHost[1].Label, m.ThroughHost[1].Action));
        // The tunnel is drawn using the host-only port, so "This host" isn't drawn as well.
        Assert.DoesNotContain(m.ThroughHost, l => l.Source.Title == "This host");

        var noDomains = ReachModel.Build(Kanban(new PortRule(3000, "tcp", PortAccess.HostOnly, 3002, "127.0.0.1")) with { Domains = [] });
        Assert.Equal(("This host", "127.0.0.1:3002"), (noDomains.ThroughHost[0].Source.Title, noDomains.ThroughHost[0].Label));
    }

    [Fact]
    public void A_tunnel_on_the_shared_network_reaches_an_internal_web_port_by_name()
    {
        var m = ReachModel.Build(Kanban(new PortRule(3000, "tcp", PortAccess.Internal)) with { Tunnel = new ReachTunnel("cloudflared", false, true) });

        Assert.DoesNotContain(m.ThroughHost, l => l.Source.Title == "Internet");
        Assert.Equal(("Internet", "kanban:3000", "through the tunnel, by name"), (m.Direct[0].Source.Title, m.Direct[0].Label, m.Direct[0].Source.Sub));
    }

    [Fact]
    public void The_hosts_network_has_one_path_from_the_lan_on_any_port()
    {
        var m = ReachModel.Build(new ReachInput { Name = "cloudflare-tunnel", Title = "cloudflare-tunnel", LanHost = "10.1.1.80", NetworkMode = "host" });

        Assert.Empty(m.ThroughHost);
        Assert.Null(m.HostPort);
        Assert.Equal(("Your LAN", "10.1.1.80 · any port it opens"), (m.Direct[0].Source.Title, m.Direct[0].Label));
        Assert.Equal("Host network: no Docker ports", m.Container.Note);
    }

    [Fact]
    public void Another_containers_network_points_at_it_and_none_reaches_nothing()
    {
        var shared = ReachModel.Build(new ReachInput { Name = "sidecar", Title = "sidecar", LanHost = "10.1.1.80", NetworkMode = "container:vpn", SharesWith = "vpn" });
        Assert.Equal(("vpn's network", "apps/vpn"), (shared.Direct[0].Source.Title, shared.Direct[0].Source.Href));

        var none = ReachModel.Build(new ReachInput { Name = "job", Title = "job", LanHost = "10.1.1.80", NetworkMode = "none" });
        Assert.True(none.Direct[0].Ghost);
        Assert.Equal("No network", none.Container.Note);
    }

    [Fact]
    public void A_stopped_app_keeps_its_paths_and_says_so()
    {
        var m = ReachModel.Build(Kanban() with { Running = false, State = AppState.Stopped });

        Assert.True(m.Stopped);
        Assert.Equal(2, m.ThroughHost.Count);
        Assert.Contains("kanban is stopped: these answer again when it starts.", m.Caption);
    }

    [Fact]
    public void Only_on_the_default_bridge_it_offers_to_join_the_shared_network()
    {
        var m = ReachModel.Build(Postgres() with { Networks = [], OnlyDefaultBridge = true });

        var bridge = Assert.Single(m.Direct);
        Assert.Equal((true, ReachAction.JoinShared, "Join gitberth"), (bridge.Ghost, bridge.Action, bridge.ActionText));
    }

    [Fact]
    public void Many_networks_fold_into_the_last_path()
    {
        var m = ReachModel.Build(Postgres() with
        {
            Networks = [new("gitberth", [], true), new("media", [], false), new("db_default", [], false), new("monitoring", [], false)],
        });

        Assert.Equal(["gitberth network", "media network", "2 more networks"], m.Direct.Select(l => l.Source.Title));
        Assert.Equal("db_default · monitoring", m.Direct[2].Source.Sub);
    }

    [Fact]
    public void Several_published_ports_show_the_web_ports_and_count_the_rest()
    {
        var m = ReachModel.Build(Kanban(new PortRule(3000, "tcp", PortAccess.Lan, 3001, ""), new PortRule(9229, "tcp", PortAccess.Lan, 9229, ""),
            new PortRule(9230, "tcp", PortAccess.Internal), new PortRule(9231, "tcp", PortAccess.Internal)));

        Assert.Equal((3001, "TCP · LAN · all addresses · +1 more"), (m.HostPort!.Port, m.HostPort.Sub));
        Assert.Equal(3, m.Container.Sockets.Count);
        Assert.Equal(1, m.Container.MoreSockets);
        Assert.Equal(3000, m.Container.Sockets[0].Port);
    }

    [Fact]
    public void Each_element_has_its_place_left_to_right_and_top_to_bottom()
    {
        var cells = ReachLayout.Place(ReachModel.Build(Kanban()));
        string Wide(string part, int lane = -1) => cells.Single(c => c.Part == part && c.Lane == lane).Wide;
        string Narrow(string part, int lane = -1) => cells.Single(c => c.Part == part && c.Lane == lane).Narrow;

        // Left to right: the internet, its path, the tunnel, the path on; the LAN's path runs to the host port; the networks'
        // paths run past it to the container, which spans every row.
        Assert.Equal(("1/1/2/2", "1/2/2/3", "1/3/2/4", "1/4/2/5"), (Wide("src", 0), Wide("flow", 0), Wide("via", 0), Wide("flow-via", 0)));
        Assert.Equal(("2/1/3/2", "2/2/3/5"), (Wide("src", 1), Wide("flow", 1)));
        Assert.Equal(("1/5/3/6", "1/6/3/7"), (Wide("hp"), Wide("flow-hp")));
        Assert.Equal(("3/1/4/2", "3/2/4/7", "4/2/5/7"), (Wide("direct-src", 0), Wide("direct-flow", 0), Wide("direct-flow", 1)));
        Assert.Equal("1/7/5/8", Wide("ctr"));

        // Top to bottom on twelve columns: the two sources side by side, the tunnel under the internet, the LAN's path down to
        // the host port, the container, then the networks feeding up from underneath.
        Assert.Equal(("1/1/2/7", "1/7/2/13"), (Narrow("src", 0), Narrow("src", 1)));
        Assert.Equal(("3/1/4/7", "2/7/5/13"), (Narrow("via", 0), Narrow("flow", 1)));
        Assert.Equal(("5/1/6/13", "6/1/7/13", "7/1/8/13"), (Narrow("hp"), Narrow("flow-hp"), Narrow("ctr")));
        Assert.Equal(("8/1/9/7", "9/7/10/13"), (Narrow("direct-flow", 0), Narrow("direct-src", 1)));
    }

    [Fact]
    public void Without_a_host_port_the_container_comes_first_on_a_phone()
    {
        var cells = ReachLayout.Place(ReachModel.Build(new ReachInput { Name = "cloudflare-tunnel", Title = "cloudflare-tunnel", LanHost = "10.1.1.80", NetworkMode = "host" }));

        Assert.DoesNotContain(cells, c => c.Part == "hp");
        Assert.Equal(("1/7/2/8", "1/1/2/13"), (cells.Single(c => c.Part == "ctr").Wide, cells.Single(c => c.Part == "ctr").Narrow));
        Assert.Equal("2/1/3/13", cells.Single(c => c.Part == "direct-flow").Narrow);
        Assert.Equal((1, 4), ReachLayout.Split(0, 4));
        Assert.Equal((10, 13), ReachLayout.Split(3, 4));
    }

    [Fact]
    public void Access_hues_come_from_the_design_systems_classes()
    {
        Assert.Equal(["gb-ac-public", "gb-ac-lan", "gb-ac-host", "gb-ac-internal"], Enum.GetValues<ReachTone>().Select(ReachModel.ToneClass));
    }

    [Fact]
    public void The_ports_mode_follows_what_the_app_is_and_the_recreate_rules()
    {
        Assert.Equal(PortsMode.HostNetwork, PortsModes.Of(git: false, self: false, temporary: false, "host", null));
        Assert.Equal(PortsMode.NoTable, PortsModes.Of(false, false, false, "container:vpn", null));
        Assert.Equal(PortsMode.Git, PortsModes.Of(git: true, false, false, "", null));
        Assert.Equal(PortsMode.ReadOnly, PortsModes.Of(false, self: true, false, "", RecreateVerdict.Yes));
        Assert.Equal(PortsMode.ReadOnly, PortsModes.Of(false, false, false, "", verdict: null));
        Assert.Equal(PortsMode.Editable, PortsModes.Of(false, false, false, "", RecreateVerdict.Yes));
        // A Registry, Local or Manual app Gitberth made: its own ports change it.
        Assert.Equal(PortsMode.Editable, PortsModes.Of(false, false, false, "", new RecreateVerdict(RecreateKind.App, "An app")));
        Assert.Equal(PortsMode.Compose, PortsModes.Of(false, false, false, "", new RecreateVerdict(RecreateKind.Compose, "Compose")));
        Assert.Equal(PortsMode.Paused, PortsModes.Of(false, false, false, "", new RecreateVerdict(RecreateKind.Paused, "Paused")));
        Assert.Equal(PortsMode.ReadOnly, PortsModes.Of(false, false, false, "", new RecreateVerdict(RecreateKind.Swarm, "Swarm")));
    }

    [Fact]
    public void One_draft_serves_every_app_and_the_web_marker_restarts_nothing()
    {
        var inspect = JsonNode.Parse("""
            {"Id":"abc","State":{"Running":true},
             "Config":{"ExposedPorts":{"3000/tcp":{},"9229/tcp":{}}},
             "HostConfig":{"PortBindings":{"3000/tcp":[{"HostIp":"","HostPort":"3001"}]}}}
            """)!.AsObject();
        var draft = ContainerPortsDraft.From(inspect, ["3000/tcp"], [], webPort: 3000);

        Assert.False(draft.Owned);
        Assert.Equal(3000, draft.WebPort);
        Assert.False(draft.Dirty);
        Assert.Equal(("http://10.1.1.80:3001", "http://10.1.1.80:3001"), ContainerPortAddress.Of(draft.Rows[0], "10.1.1.80", "kanban", true, false)!.Value);
        Assert.Equal(((string, string?))("kanban:9229", null), ContainerPortAddress.Of(draft.Rows[1], "10.1.1.80", "kanban", true, false)!.Value);

        // The marker moves to 9229: nothing restarts, and the rows the settings service plans carry it.
        draft.SetWeb(draft.Rows[1]);
        Assert.Equal((true, false, true, 1), (draft.Dirty, draft.NeedsRecreate, draft.WebChanged, draft.Changes));
        var rows = draft.ToPortRows();
        Assert.Equal([(3000, false), (9229, true)], rows.Select(r => (r.ContainerPort!.Value, r.IsWeb)));
        Assert.Equal("http://kanban:9229", ContainerPortAddress.Of(draft.Rows[1], "10.1.1.80", "kanban", true, false)!.Value.Text);

        // An added Internal port is only a note on a container's own draft; on a new app's it's a port of its own.
        var note = draft.NewRow();
        (note.ContainerPort, note.Access) = (8080, PortAccess.Internal);
        draft.Rows.Add(note);
        Assert.Equal((true, false), (draft.NotesChanged, draft.ToPortRows().Any(r => r.ContainerPort == 8080)));
        var fresh = ContainerPortsDraft.ForNew([new PortRule(8080, "tcp", PortAccess.Internal)], webPort: null);
        Assert.Equal((true, 8080), (fresh.Owned, fresh.ToPortRows().Single().ContainerPort!.Value));
        Assert.Empty(fresh.Notes());
    }
}
