using Gitberth.Components.Pages;
using Gitberth.Docker;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// What the Network page works out from the host-port map and Docker's networks: one citizen level (a port is held by a
/// container, whoever made it, or by the host; never "(app)"), each holder's name, line and page, the Access and notes a
/// row shows, the filter's groups and counts, a folded run, the port check's parsing, the card's foot, and a network
/// member's name without "(app)".
/// </summary>
public sealed class NetworkPageTests
{
    private static HostPortUse Use(int port, HostPortUser kind, string who, string? container = null, string? hostIp = "", int? containerPort = 80,
        HostPortState state = HostPortState.Published, string? note = null, int? appId = null, string protocol = "tcp") =>
        new(port, protocol, hostIp, kind, who, container is null ? null : NetworkService.PageOf(container), containerPort, state, note, container, appId);

    private static readonly HostPortUse Ssh = Use(22, HostPortUser.Reserved, "SSH", hostIp: null, containerPort: null, state: HostPortState.Listening);
    private static readonly HostPortUse Orb = Use(3000, HostPortUser.App, "orb", "gb-orb", note: "2 domains", appId: 2);
    private static readonly HostPortUse Kanban = Use(3001, HostPortUser.App, "kanban", "gb-kanban", containerPort: 3000, appId: 1);
    private static readonly HostPortUse Self = Use(8080, HostPortUser.Gitberth, "Gitberth", "gitberth", containerPort: 8080);
    private static readonly HostPortUse PgOld = Use(5433, HostPortUser.Container, "pg-old", "pg-old", hostIp: "127.0.0.1", containerPort: 5432,
        state: HostPortState.Stopped, note: HostPortUse.StoppedNote);
    private static readonly HostPortUse Program = Use(9100, HostPortUser.HostProgram, "A program on the host", hostIp: "0.0.0.0", containerPort: null,
        state: HostPortState.Listening);

    private static List<HostPortRow> Rows(params HostPortUse[] uses) => HostPortMap.Fold(uses);

    [Fact]
    public void Every_container_is_one_group_and_the_host_the_other()
    {
        var rows = Rows(Ssh, Orb, Kanban, PgOld, Self, Program);

        Assert.Equal((6, 4, 2), NetworkPageView.Counts(rows));
        Assert.Equal([3000, 3001, 5433, 8080], rows.Where(r => NetworkPageView.InGroup(r, HostPortGroup.Containers)).Select(r => r.Port));
        Assert.Equal([22, 9100], rows.Where(r => NetworkPageView.InGroup(r, HostPortGroup.Host)).Select(r => r.Port));
        Assert.All(rows, r => Assert.True(NetworkPageView.InGroup(r, HostPortGroup.All)));
        Assert.Equal(6, NetworkPageView.InUse(rows));
        // orb and kanban on every address, pg-old on the host only; the host's own sockets aren't the containers'.
        Assert.Equal((3, 1), NetworkPageView.Reach(rows));
    }

    [Fact]
    public void A_holder_goes_by_its_own_name_with_its_page_never_app_or_container()
    {
        var kanban = NetworkPageView.Holder(Kanban);
        Assert.Equal(("kanban", "gb-kanban", "apps/gb-kanban", "gb-kanban"), (kanban.Name, kanban.Sub, kanban.Link, kanban.Container));
        Assert.Null(kanban.Icon);

        // A container nobody gave another name has no second line.
        var pgOld = NetworkPageView.Holder(PgOld);
        Assert.Equal(("pg-old", (string?)null, "apps/pg-old", true), (pgOld.Name, pgOld.Sub, pgOld.Link, pgOld.Down));

        var self = NetworkPageView.Holder(Self);
        Assert.Equal(("gitberth", "Gitberth itself", "apps/gitberth", true), (self.Name, self.Sub, self.Link, self.Self));

        var ssh = NetworkPageView.Holder(Ssh);
        Assert.Equal(("SSH", "On the host", (string?)null), (ssh.Name, ssh.Sub, ssh.Link));
        Assert.NotNull(ssh.Icon);
        Assert.Equal("Docker's API", NetworkPageView.Holder(Use(2375, HostPortUser.Reserved, "Docker API", hostIp: null, containerPort: null, state: HostPortState.Listening)).Name);
        Assert.Equal(("A program on the host", "Outside Docker"), (NetworkPageView.Holder(Program).Name, NetworkPageView.Holder(Program).Sub));

        // A container the owner gave another name in Gitberth goes by it, with the container's own under it.
        var renamed = NetworkPageView.Holder(PgOld, "Old Postgres");
        Assert.Equal(("Old Postgres", "pg-old"), (renamed.Name, renamed.Sub));
        Assert.Equal(("kanban", "gb-kanban"), (NetworkPageView.Holder(Kanban, "kanban").Name, NetworkPageView.Holder(Kanban, "kanban").Sub));

        // A deploy's temporary container is its app's, by the app's name and page.
        var deploy = NetworkPageView.Holder(Use(3001, HostPortUser.App, "kanban", "gb-kanban-new", note: HostPortUse.TemporaryNote, appId: 1));
        Assert.Equal(("kanban", "A deploy's container: gb-kanban-new"), (deploy.Name, deploy.Sub));

        // A tunnel route to an app's port links to that app; one without an app links nowhere.
        var route = NetworkPageView.Holder(new HostPortUse(4000, "tcp", null, HostPortUser.Route, "app.example.com", "apps/gb-kanban", null, HostPortState.Route,
            "Tunnel route to app.example.com", null, 1));
        Assert.Equal(("app.example.com", "apps/gb-kanban", (string?)null), (route.Name, route.Link, route.Container));

        foreach (var u in new[] { Ssh, Orb, Kanban, Self, PgOld, Program })
        {
            var who = NetworkPageView.Holder(u);
            Assert.DoesNotContain("(app)", who.Name + who.Sub, StringComparison.Ordinal);
            Assert.DoesNotContain("(container)", who.Name + who.Sub, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Access_says_who_reaches_it()
    {
        Assert.Equal((AddressKind.Lan, "LAN"), NetworkPageView.Access([Kanban]));
        Assert.Equal((AddressKind.HostOnly, "Host only"), NetworkPageView.Access([PgOld]));
        Assert.Equal((AddressKind.Lan, "10.1.1.80"), NetworkPageView.Access([Use(3002, HostPortUser.Container, "x", "x", hostIp: "10.1.1.80")]));
        Assert.Equal((AddressKind.Lan, "Mixed"), NetworkPageView.Access([Kanban, PgOld]));
        // A program on the host listens where it listens; SSH, assumed without the host's sockets, has no known address.
        Assert.Equal((AddressKind.Lan, "LAN"), NetworkPageView.Access([Program]));
        Assert.Null(NetworkPageView.Access([Ssh]));
    }

    [Fact]
    public void Notes_and_where_it_goes()
    {
        Assert.Equal([("2 domains", "good")], NetworkPageView.Notes(Orb).Select(n => (n.Text, n.Tone)));
        Assert.Equal([("Stopped", "neutral")], NetworkPageView.Notes(PgOld).Select(n => (n.Text, n.Tone)));
        Assert.Equal("Restarting", NetworkPageView.Notes(PgOld with { Note = HostPortUse.RestartingNote }).Single().Text);
        Assert.Equal("Random", NetworkPageView.Notes(Kanban with { Note = "Random: changes at every start" }).Single().Text);
        Assert.Equal("Saved, not live", NetworkPageView.Notes(Kanban with { Note = "Saved, not live", State = HostPortState.Saved }).Single().Text);
        Assert.Empty(NetworkPageView.Notes(Kanban));
        // A route's own note is its holder's line already.
        Assert.Empty(NetworkPageView.Notes(new HostPortUse(4000, "tcp", null, HostPortUser.Route, "a.example.com", null, null, HostPortState.Route, "Tunnel route to a.example.com")));

        Assert.Equal("3000", NetworkPageView.GoesTo([Kanban]));
        Assert.Equal("Several ports", NetworkPageView.GoesTo([Kanban, Orb]));
        Assert.Null(NetworkPageView.GoesTo([Ssh]));
        Assert.Equal("3001", NetworkPageView.PortText(Rows(Kanban)[0]));
    }

    [Fact]
    public void A_run_of_similar_containers_folds_into_one_line()
    {
        var samples = Enumerable.Range(0, 6)
            .Select(i => Use(9100 + i, HostPortUser.Container, $"sample-{i}", $"sample-{i}", containerPort: 8080,
                state: i < 2 ? HostPortState.Stopped : HostPortState.Published, note: i < 2 ? HostPortUse.StoppedNote : null))
            .ToArray();
        var row = Assert.Single(Rows(samples));

        Assert.True(row.Folded);
        Assert.Equal("9100–9105", NetworkPageView.PortText(row));
        Assert.Equal(("6 containers", "sample-*", 2), NetworkPageView.Fold(row));
        Assert.Equal("9100-9105/tcp", NetworkPageView.FoldKey(row));
        Assert.True(NetworkPageView.InGroup(row, HostPortGroup.Containers));
    }

    [Theory]
    [InlineData("5433", 5433, "tcp")]
    [InlineData(" 53/UDP ", 53, "udp")]
    [InlineData("8080/tcp", 8080, "tcp")]
    public void A_port_to_check_is_read(string text, int port, string protocol) =>
        Assert.Equal((port, protocol), NetworkPageView.ParseCheck(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("80/sctp")]
    [InlineData("web")]
    public void Anything_else_isnt_a_port(string text) => Assert.Null(NetworkPageView.ParseCheck(text));

    [Fact]
    public void The_foot_says_what_was_read_and_what_couldnt_be()
    {
        var at = new DateTime(2026, 11, 7, 8, 45, 11, DateTimeKind.Utc);
        var scanned = new HostPortView([], new HostScan(at, [], null, "eth0", "10.1.1.80", null, false), at, ["cloudflare-tunnel"]);
        Assert.Equal("Read at 08:45:11 from Docker, Gitberth's apps and the tunnel's routes, and the host's sockets. Containers on the host's network, like cloudflare-tunnel, show up as a program on the host.",
            NetworkPageView.Footer(scanned, "08:45:11"));

        var failed = new HostPortView([], HostScan.Failed("no access."), at, []);
        Assert.Equal("Read at 08:45:11 from Docker, Gitberth's apps and the tunnel's routes. The host's own sockets couldn't be read (no access), so ports held by programs outside Docker aren't listed.",
            NetworkPageView.Footer(failed, "08:45:11"));
    }

    [Fact]
    public void A_network_member_goes_by_its_own_name()
    {
        static NetworkMember M(string title) => new("c", title, "apps/c", true, "172.21.0.2", []);

        Assert.Equal(new MemberLook("kanban", null), NetworkPageView.Member(M("kanban (app)")));
        Assert.Equal(new MemberLook("gitberth", "Gitberth"), NetworkPageView.Member(M("gitberth (Gitberth)")));
        Assert.Equal(new MemberLook("gb-kanban-new", "Deploy of kanban"), NetworkPageView.Member(M("gb-kanban-new (a deploy of kanban)")));
        Assert.Equal(new MemberLook("postgres", null), NetworkPageView.Member(M("postgres")));
    }

    [Fact]
    public void A_network_says_what_it_is()
    {
        static NetworkView N(string name, string? note, bool system = false, bool shared = false, bool @internal = false) =>
            new(new NetworkInfo("id", name, "bridge", "local", @internal, false, [], [], new Dictionary<string, string>(), new Dictionary<string, string>()), note, system, shared, []);

        Assert.Equal(["Shared network"], NetworkPageView.Tags(N("gitberth", "Gitberth's shared network", shared: true)));
        Assert.Equal(["Docker's own"], NetworkPageView.Tags(N("bridge", "Docker's default: no names", system: true)));
        Assert.Equal(["Own LAN addresses"], NetworkPageView.Tags(N(LanAddresses.Network, "Own LAN addresses")));
        Assert.Equal(["Internal: no way out"], NetworkPageView.Tags(N("db", null, @internal: true)));
        Assert.Equal("Docker's default: containers here can't reach each other by name.", NetworkPageView.NoteOf(N("bridge", "Docker's default: no names", system: true)));
        Assert.Equal("Docker Compose (db)", NetworkPageView.NoteOf(N("db_default", "Docker Compose (db)")));
        Assert.Null(NetworkPageView.NoteOf(N("mynet", null)));
        Assert.Equal(("None", "1 container", "79 containers"), (NetworkPageView.MemberCount(0), NetworkPageView.MemberCount(1), NetworkPageView.MemberCount(79)));
    }
}
