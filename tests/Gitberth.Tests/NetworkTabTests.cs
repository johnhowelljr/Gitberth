using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// What an app's Network tab works out before anything is saved: what changed in its Ports table, the line that says what
/// saving it does, each row's address, why a host port isn't the container's own, and Settings' check that the LAN address
/// of this host is still the host's. kanban has its web port 3000 on the LAN at 3001, and its debugger 9229 Internal.
/// </summary>
public sealed class NetworkTabTests
{
    private static List<PortMapping> Kanban() =>
    [
        new() { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true },
        new() { Id = 2, ContainerPort = 9229, Access = PortAccess.Internal },
    ];

    private static PortRow Row(int port, PortAccess access, int? host = null, bool web = false, string protocol = "tcp", string? ip = null) =>
        new() { ContainerPort = port, Access = access, HostPort = host, IsWeb = web, Protocol = protocol, HostIp = ip };

    [Fact]
    public void Counts_each_port_changed_added_or_removed_once()
    {
        var draft = ContainerPortsDraft.FromSaved(Kanban());
        Assert.Equal((false, 0), (draft.Dirty, draft.Changes));
        Assert.All(draft.Rows, r => Assert.True(draft.Unchanged(r)));
        Assert.True(draft.Owned);

        // A new host port for the web port: its field is checked again, and the container has to be recreated.
        draft.Rows[0].HostPort = 3005;
        Assert.Equal((true, 1, true), (draft.Dirty, draft.Changes, draft.NeedsRecreate));
        Assert.False(draft.Unchanged(draft.Rows[0]));
        Assert.True(draft.WasLoaded(draft.Rows[0]));

        // A port added, and one removed.
        var added = draft.NewRow();
        (added.ContainerPort, added.HostPort) = (8081, 8081);
        draft.Rows.Add(added);
        draft.Rows.RemoveAt(1);
        Assert.Equal(3, draft.Changes);
        Assert.False(draft.WasLoaded(draft.Rows[1]));

        // A row without a container port isn't a port yet.
        var started = ContainerPortsDraft.FromSaved(Kanban());
        started.Rows.Add(started.NewRow());
        Assert.Equal((false, 0), (started.Dirty, started.Changes));
    }

    [Fact]
    public void Moving_the_web_marker_is_one_change_and_restarts_nothing()
    {
        var saved = Kanban();
        saved[1] = new PortMapping { Id = 2, ContainerPort = 9000, HostPort = 9000 };
        var draft = ContainerPortsDraft.FromSaved(saved);
        draft.SetWeb(draft.Rows[1]);

        Assert.Equal((true, 1, false, true), (draft.Dirty, draft.Changes, draft.NeedsRecreate, draft.WebChanged));
        var web = draft.Web;
        Assert.Equal(9000, web?.ContainerPort);
        Assert.False(draft.Rows[0].IsWeb);
        Assert.Equal("1 change. Nothing restarts: domains move to port 9000 at once.",
            PortsBar.Line(draft.Changes, draft.NeedsRecreate, "kanban", deployed: true, running: true, unnoticed: true, web?.ContainerPort, hasDomains: true));
        // Without domains it's only the web port that moves; with the marker cleared there's none.
        Assert.Equal("1 change. Nothing restarts: port 9000 is the web port from now on.", PortsBar.Line(1, false, "kanban", true, true, false, 9000, hasDomains: false));
        Assert.Equal("1 change. Nothing restarts: kanban has no web port now.", PortsBar.Line(1, false, "kanban", true, true, false, null, hasDomains: false));
    }

    [Fact]
    public void The_bar_says_who_notices_the_restart()
    {
        // Domain visitors are only left alone when the deploy can start the new version next to the old one.
        Assert.Equal("1 change. kanban restarts: a few seconds offline on the LAN; domain visitors aren't affected.",
            PortsBar.Line(1, restart: true, "kanban", deployed: true, running: true, unnoticed: true, 3000, hasDomains: true));
        Assert.Equal("3 changes. kanban restarts: a few seconds offline.", PortsBar.Line(3, true, "kanban", true, true, unnoticed: false, 3000, true));
        Assert.Equal("1 change. The app is stopped: it takes the new ports when it starts.", PortsBar.Line(1, true, "kanban", true, running: false, false, 3000, true));
        Assert.Equal("2 changes. kanban isn't deployed yet: its first deploy uses the new ports.", PortsBar.Line(2, true, "kanban", deployed: false, false, false, 3000, false));
    }

    [Fact]
    public void A_deploy_or_a_docker_that_cant_be_read_doesnt_make_the_app_stopped()
    {
        var app = new AppDefinition { Id = 5, Name = "kanban", Slug = "kanban", CurrentImage = "gitberth/kanban:1" };
        var up = new ContainerSummary("gb-kanban", "running", "Up 3 hours", "gitberth/kanban:1", "0.0.0.0:3001->3000/tcp", new Dictionary<string, string>());
        bool Runs(ContainerSummary? container, Deployment? active = null, bool reachable = true) =>
            PortsBar.Runs(AppStatusResolver.Resolve(app, container, active, reachable).State);

        Assert.True(Runs(up));
        // The status says Deploying while the old version still runs, and the deploy leaves it running: applying restarts it.
        Assert.True(Runs(up, new Deployment { AppId = 5, Status = DeployStatus.Building }));
        // Docker can't be read: nothing says it won't restart.
        Assert.True(Runs(null, reachable: false));
        Assert.False(Runs(up with { State = "exited" }));
        Assert.False(Runs(null));
    }

    [Fact]
    public void Saving_says_when_the_new_ports_are_live()
    {
        var app = new AppDefinition { Name = "kanban", CurrentImage = "gitberth/kanban:1" };
        var restart = new PortsSaved([], NeedsRestart: true);
        // A stopped app takes them when it starts, as the bar said, whichever button saved them.
        Assert.Equal("Saved. kanban isn't running: it takes the new ports when it starts.", PortsBar.Saved(app, restart, applyNow: false, running: false));
        Assert.Equal("Saved. kanban isn't running: it takes the new ports when it starts.", PortsBar.Saved(app, restart, applyNow: true, running: true));
        Assert.Equal("Saved. The new ports are live after the next deploy.", PortsBar.Saved(app, restart, applyNow: false, running: true));
        Assert.Equal("Saved. Its first deploy uses the new ports.", PortsBar.Saved(new AppDefinition { Name = "kanban" }, restart, false, false));
        Assert.Equal("Saved.", PortsBar.Saved(app, new PortsSaved([], NeedsRestart: false), false, true));
    }

    [Fact]
    public void An_apply_that_followed_a_deploy_already_under_way_says_what_it_left_pending()
    {
        // Apply clicked before the page saw a deploy start follows that deploy, which read the app before the save.
        var started = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var dep = new Deployment { Id = 7, AppId = 5, Status = DeployStatus.Succeeded, StartedAt = started, FinishedAt = started.AddSeconds(40) };
        AppDefinition App(bool pending, int savedAt) =>
            new() { Id = 5, Name = "kanban", CurrentImage = "gitberth/kanban:1", ConfigChangePending = pending, UpdatedAt = started.AddSeconds(savedAt) };

        // It made the change live: the deploy wrote the app as it finished.
        Assert.Equal("Applied. kanban answers at http://10.1.1.80:3001.", PortsBar.Applied(App(pending: false, savedAt: 39), dep, "http://10.1.1.80:3001"));
        Assert.False(PortsBar.LeftPending(App(false, 39), dep));
        // Saved while it ran, so it isn't in it: said as the pending banner says it.
        Assert.Equal("Deploy #7 finished, but settings saved while it ran aren't live yet.", PortsBar.Applied(App(pending: true, savedAt: 39), dep, "http://10.1.1.80:3001"));
        Assert.True(PortsBar.LeftPending(App(true, 39), dep));
        // The page read the app just before the deploy's last write (and the deploy just after): the save it applied still
        // looks pending, from before it started.
        Assert.Equal("Applied.", PortsBar.Applied(App(pending: true, savedAt: -2), dep, null));
        // Saved again once it had finished: that's for the next deploy, and this one applied what it was for.
        Assert.Equal("Applied.", PortsBar.Applied(App(pending: true, savedAt: 41), dep, null));
    }

    [Fact]
    public void Domain_visitors_only_miss_nothing_when_the_deploy_can_bridge()
    {
        var bridge = new BridgeFacts(DomainsOn: true, HasDomain: true, Running: true, HasVolume: false, HasWebPort: true, TunnelOnHost: true);
        Assert.Null(BridgeRules.WhyNot(bridge));
        Assert.Equal("the app has no domain Gitberth routes", BridgeRules.WhyNot(bridge with { DomainsOn = false }));
        Assert.Equal("the app has no domain Gitberth routes", BridgeRules.WhyNot(bridge with { HasDomain = false }));
        Assert.Equal("the app isn't running", BridgeRules.WhyNot(bridge with { Running = false }));
        Assert.Equal("the app has a volume, and two copies writing the same files at once isn't safe", BridgeRules.WhyNot(bridge with { HasVolume = true }));
        Assert.Equal("the app has no web port", BridgeRules.WhyNot(bridge with { HasWebPort = false }));
        Assert.Equal("the tunnel doesn't reach apps by their host port", BridgeRules.WhyNot(bridge with { TunnelOnHost = false }));
        Assert.Equal("the web port is Internal, and the tunnel reaches apps only through a host port", BridgeRules.WhyNot(bridge with { WebPortInternal = true }));
    }

    [Fact]
    public void Each_row_has_the_address_its_access_gives()
    {
        Assert.Equal(("http://10.1.1.80:3001", "http://10.1.1.80:3001"), PortAddress.Of(Row(3000, PortAccess.Lan, 3001, web: true), "10.1.1.80", "kanban"));
        // A database's port is copied rather than opened; 443 is https.
        Assert.Equal(("10.1.1.80:5433", (string?)null), PortAddress.Of(Row(5432, PortAccess.Lan, 5433), "10.1.1.80", "kanban"));
        Assert.Equal(("https://10.1.1.80:8443", "https://10.1.1.80:8443"), PortAddress.Of(Row(443, PortAccess.Lan, 8443), "10.1.1.80", "kanban"));
        // The host only is never a link: the browser's machine can't reach it.
        Assert.Equal(("127.0.0.1:3002", (string?)null), PortAddress.Of(Row(3000, PortAccess.HostOnly, 3002, web: true), "10.1.1.80", "kanban"));
        // Internal is by network name, the web port as a URL, whatever host port the field still holds.
        Assert.Equal(("http://kanban:3000", (string?)null), PortAddress.Of(Row(3000, PortAccess.Internal, web: true), "10.1.1.80", "kanban"));
        Assert.Equal(("kanban:9229", (string?)null), PortAddress.Of(Row(9229, PortAccess.Internal, 3005), "10.1.1.80", "kanban"));
        // A kept Custom row is on its own address, bracketed for IPv6; a UDP one is never a link.
        Assert.Equal(("[fd00::5]:53", (string?)null), PortAddress.Of(Row(53, PortAccess.Custom, 53, protocol: "udp", ip: "fd00::5"), "10.1.1.80", "kanban"));
        // Without a host port, or a container port, there's no address yet.
        Assert.Null(PortAddress.Of(Row(3000, PortAccess.Lan), "10.1.1.80", "kanban"));
        Assert.Null(PortAddress.Of(new PortRow { HostPort = 3001 }, "10.1.1.80", "kanban"));
    }

    [Fact]
    public void Says_why_a_host_port_isnt_the_containers_own()
    {
        List<HostPortUse> uses =
        [
            new(3000, "tcp", "", HostPortUser.App, "orb", "apps/1", 80, HostPortState.Published, null, "gb-orb", 1),
            new(3001, "tcp", "", HostPortUser.App, "kanban", "apps/2", 3000, HostPortState.Published, null, "gb-kanban", 2),
            new(5432, "tcp", "", HostPortUser.Container, "postgres", "containers/postgres", 5432, HostPortState.Published, null, "postgres"),
            new(8080, "tcp", "", HostPortUser.Gitberth, "Gitberth", "containers/gitberth", 8080, HostPortState.Published, null, "gitberth"),
        ];
        HostPortCheck Check(int port) => HostPortMap.Check(uses, HostScan.DefaultRandomRange, port, "tcp", "", null);

        Assert.Equal("5432 is used by postgres; 5433 is free.", HostPortText.WhyNotOwn(5432, 5433, Check));
        Assert.Equal("3000 and 3001 are used; 3002 is free.", HostPortText.WhyNotOwn(3000, 3002, Check));
        Assert.Equal("8080 is Gitberth's own page; 8081 is free.", HostPortText.WhyNotOwn(8080, 8081, Check));
        // Its own port, or one in between, is free: the field just says Free.
        Assert.Null(HostPortText.WhyNotOwn(4000, 4000, Check));
        Assert.Null(HostPortText.WhyNotOwn(2999, 3002, Check));
    }

    [Fact]
    public void A_host_port_outside_the_range_is_wrong_before_its_saved()
    {
        // Nobody holds 70000 or 0, but the save refuses them: the field says so as they're typed in, as the save would.
        foreach (var port in new[] { 70000, 65536, 0, -1 })
            Assert.Equal("Pick a host port between 1 and 65535.", HostPortText.Problem(port, []));
        Assert.Equal("Host port 3001 is listed twice.", HostPortText.Problem(3001, [3001, 9229]));
        Assert.Null(HostPortText.Problem(3001, [3002]));
        Assert.Null(HostPortText.Problem(65535, []));
        Assert.Equal(HostPortText.Problem(70000, []), Assert.Single(PortRules.Check([Row(3000, PortAccess.Lan, 70000)], PortContext.None)).Message);
    }

    [Fact]
    public void Notices_when_the_lan_address_isnt_the_hosts_any_more()
    {
        var scan = new HostScan(DateTime.UtcNow, [], null, "eth0", "10.1.1.85", "10.1.1.1", false, LocalAddresses: ["10.1.1.85", "127.0.0.1", "172.21.0.1"]);
        Assert.Equal("10.1.1.85", LanAddressCheck.Drifted("10.1.1.80", scan));
        // Still one of the host's, a name (the PC resolves it), nothing set, or a host that couldn't be read: no prompt.
        Assert.Null(LanAddressCheck.Drifted(" 10.1.1.85 ", scan));
        Assert.Null(LanAddressCheck.Drifted("172.21.0.1", scan));
        Assert.Null(LanAddressCheck.Drifted("nas.lan", scan));
        Assert.Null(LanAddressCheck.Drifted("10", scan));
        Assert.Null(LanAddressCheck.Drifted(null, scan));
        Assert.Null(LanAddressCheck.Drifted("10.1.1.80", HostScan.Failed("the helper container failed")));
        // Without the host's addresses, the detected one is all there is to go by.
        Assert.Equal("10.1.1.85", LanAddressCheck.Drifted("10.1.1.80", scan with { LocalAddresses = null }));
    }
}
