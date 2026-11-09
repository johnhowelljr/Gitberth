using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>The rules for an app's ports (5.9), as its Network tab and New app check them before saving.</summary>
public class PortRulesTests
{
    private static PortRow Row(int container, PortAccess access = PortAccess.Lan, int? host = null, string protocol = "tcp", bool web = false, string? ip = null) =>
        new() { ContainerPort = container, Access = access, HostPort = host, Protocol = protocol, IsWeb = web, HostIp = ip };

    private static PortContext WithDomains(bool tunnelOnHost) => PortContext.None with { Domains = ["app.harborlab.com", "harborlab.com"], TunnelOnHost = tunnelOnHost };

    [Fact]
    public void An_internal_port_needs_no_host_port()
    {
        // kanban's debugger, reached by name only.
        Assert.Empty(PortRules.Check([Row(3000, host: 3001, web: true), Row(9229, PortAccess.Internal)], PortContext.None));
        // Whatever its host-port field still holds doesn't count, and isn't saved.
        Assert.Empty(PortRules.Check([Row(9229, PortAccess.Internal, host: 3001), Row(3000, host: 3001, web: true)], PortContext.None));
        var saved = PortRules.ToMappings([Row(9229, PortAccess.Internal, host: 3001, ip: "10.1.1.80")]).Single();
        Assert.Equal((PortAccess.Internal, (int?)null, (string?)null), (saved.Access, saved.HostPort, saved.HostIp));

        // A published one needs one, on the host only as much as on the LAN.
        Assert.Equal([new PortProblem(0, PortField.HostPort, "Pick a host port between 1 and 65535.")], PortRules.Check([Row(3000)], PortContext.None));
        Assert.Equal([new PortProblem(0, PortField.HostPort, "Pick a host port between 1 and 65535.")], PortRules.Check([Row(3000, PortAccess.HostOnly, host: 70000)], PortContext.None));
        // Every row needs a container port, and only one row a given one.
        Assert.Equal([new PortProblem(0, PortField.ContainerPort, "Each port needs a container port between 1 and 65535.")],
            PortRules.Check([new PortRow { HostPort = 3001 }], PortContext.None));
        Assert.Equal([new PortProblem(1, PortField.ContainerPort, "3000/tcp is listed twice.")],
            PortRules.Check([Row(3000, host: 3001, web: true), Row(3000, PortAccess.Internal)], PortContext.None));
        // The same number over UDP is another port.
        Assert.Empty(PortRules.Check([Row(53, host: 53), Row(53, host: 53, protocol: "udp")], PortContext.None));
        // A kept Custom binding needs its address.
        Assert.Equal([new PortProblem(0, PortField.Access, "5432/tcp needs the address to publish on.")], PortRules.Check([Row(5432, PortAccess.Custom, host: 5432)], PortContext.None));
        Assert.Empty(PortRules.Check([Row(5432, PortAccess.Custom, host: 5432, ip: "10.1.1.80")], PortContext.None));
    }

    [Fact]
    public void The_web_port_is_tcp_and_there_is_at_most_one()
    {
        Assert.Equal([new PortProblem(1, PortField.Web, "Only one port can be the web port.")],
            PortRules.Check([Row(3000, host: 3001, web: true), Row(9000, host: 9000, web: true)], PortContext.None));
        Assert.Equal([new PortProblem(0, PortField.Web, "The web port must be TCP.")],
            PortRules.Check([Row(53, host: 5353, protocol: "udp", web: true)], PortContext.None));
        // A worker has none.
        Assert.Empty(PortRules.Check([Row(9000, PortAccess.Internal)], PortContext.None));
        Assert.Empty(PortRules.Check([], PortContext.None));

        // A UDP row is never saved as the web port, and an app's web port is the TCP row marked as it.
        Assert.False(PortRules.ToMappings([Row(53, host: 5353, protocol: "udp", web: true)]).Single().IsWeb);
        Assert.Equal(2, PortRules.WebOf([new PortMapping { Id = 1, ContainerPort = 53, Protocol = "udp", IsWeb = true }, new PortMapping { Id = 2, ContainerPort = 3000, IsWeb = true }])!.Id);
        Assert.Null(PortRules.WebOf([new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001 }]));
    }

    [Fact]
    public void A_domain_needs_the_web_port_on_the_host()
    {
        // cloudflared on the host's network reaches kanban only through a host port. The main domain is named.
        Assert.Equal([new PortProblem(0, PortField.Access, "app.harborlab.com needs the web port on the host. Set it to LAN or Host only.")],
            PortRules.Check([Row(3000, PortAccess.Internal, web: true)], WithDomains(tunnelOnHost: true)));
        Assert.Empty(PortRules.Check([Row(3000, PortAccess.HostOnly, host: 3002, web: true)], WithDomains(tunnelOnHost: true)));
        Assert.Empty(PortRules.Check([Row(3000, host: 3001, web: true)], WithDomains(tunnelOnHost: true)));
        // On the shared network it reaches kanban by name.
        Assert.Empty(PortRules.Check([Row(3000, PortAccess.Internal, web: true)], WithDomains(tunnelOnHost: false)));
        // Domains go to the web port, so there has to be one.
        Assert.Equal([new PortProblem(null, PortField.Web, "app.harborlab.com needs a web port: domains go to the web port.")],
            PortRules.Check([Row(3000, host: 3001)], WithDomains(tunnelOnHost: false)));
        // Without a domain an Internal web port is fine.
        Assert.Empty(PortRules.Check([Row(3000, PortAccess.Internal, web: true)], PortContext.None with { TunnelOnHost = true }));
    }

    [Fact]
    public void Names_the_owner_of_a_taken_host_port()
    {
        List<HostPortUse> uses =
        [
            new(3000, "tcp", "", HostPortUser.App, "orb", "apps/1", 80, HostPortState.Published, null, "gb-orb", 1),
            new(5432, "tcp", "", HostPortUser.Container, "pg-old", "containers/pg-old", 5432, HostPortState.Published, null, "pg-old"),
            new(8080, "tcp", "", HostPortUser.Gitberth, "Gitberth", "containers/gitberth", 8080, HostPortState.Published, null, "gitberth"),
            new(22, "tcp", "", HostPortUser.Reserved, "SSH", null, null, HostPortState.Listening),
            new(2375, "tcp", "", HostPortUser.Reserved, "Docker API", null, null, HostPortState.Listening),
            new(9105, "tcp", "", HostPortUser.Container, "sample-astro", "containers/sample-astro", 80, HostPortState.Stopped, HostPortUse.StoppedNote, "sample-astro"),
            new(3002, "tcp", null, HostPortUser.Route, "old.example.com", null, null, HostPortState.Route, "Tunnel route to old.example.com"),
            new(5000, "tcp", "", HostPortUser.HostProgram, "A program on the host", null, null, HostPortState.Listening),
            new(3006, "tcp", "", HostPortUser.App, "orb", "apps/1", 9000, HostPortState.Saved, "Saved, not live", "gb-orb", 1),
            new(6379, "tcp", "127.0.0.1", HostPortUser.Container, "cache", "containers/cache", 6379, HostPortState.Published, null, "cache"),
            // kanban's own: the port it's published on now, and one it saved.
            new(3001, "tcp", "", HostPortUser.App, "kanban", "apps/2", 3000, HostPortState.Published, null, "gb-kanban", 2),
            new(3005, "tcp", "", HostPortUser.App, "kanban", "apps/2", 9229, HostPortState.Saved, "Saved, not live", "gb-kanban", 2),
        ];
        var kanban = PortContext.None with { Uses = uses, Self = new HostPortOwner("gb-kanban", 2) };
        string? Problem(int hostPort, PortAccess access = PortAccess.Lan, string? ip = null) =>
            PortRules.Check([Row(3000, access, host: hostPort, ip: ip)], kanban).SingleOrDefault()?.Message;

        Assert.Equal("Port 3000 is used by orb.", Problem(3000));
        Assert.Equal("Port 5432 is used by pg-old.", Problem(5432));
        Assert.Equal("Port 8080 is Gitberth's own page.", Problem(8080));
        Assert.Equal("Port 22 is SSH on the host.", Problem(22));
        Assert.Equal("Port 2375 is Docker's API on the host.", Problem(2375));
        Assert.Equal("Port 9105 is used by sample-astro (stopped): it takes it when it starts.", Problem(9105));
        Assert.Equal("Port 3002 is where the tunnel sends old.example.com.", Problem(3002));
        Assert.Equal("Port 5000 is used by a program on the host.", Problem(5000));
        Assert.Equal("Port 3006 is saved for orb, not live yet.", Problem(3006));
        // The host only clashes with a binding on every address, and a loopback one with the host only.
        Assert.Equal("Port 3000 is used by orb.", Problem(3000, PortAccess.HostOnly));
        Assert.Equal("Port 6379 is used by cache.", Problem(6379, PortAccess.HostOnly));
        Assert.Equal("Port 6379 is used by cache.", Problem(6379));
        // Another single address of the host doesn't.
        Assert.Null(Problem(6379, PortAccess.Custom, "10.1.1.80"));
        // kanban's own ports are its own, and a free one is free.
        Assert.Null(Problem(3001));
        Assert.Null(Problem(3005));
        Assert.Null(Problem(3010));

        // Two of the table's own rows on one host port clash with each other too.
        Assert.Equal([new PortProblem(1, PortField.HostPort, "Host port 3010 is listed twice.")],
            PortRules.Check([Row(3000, host: 3010, web: true), Row(9229, PortAccess.HostOnly, host: 3010)], kanban));
        Assert.Empty(PortRules.Check([Row(3000, PortAccess.HostOnly, host: 3010, web: true), Row(9229, PortAccess.Custom, host: 3010, ip: "10.1.1.80")], kanban));
    }

    [Fact]
    public void Moving_the_web_marker_to_a_bound_port_needs_no_restart()
    {
        List<PortMapping> saved =
        [
            new() { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true },
            new() { Id = 2, ContainerPort = 9000, HostPort = 3002 },
        ];
        List<PortRow> Rows() => saved.Select(PortRow.From).ToList();

        // 9000 is published already, so domains, the health URL and jobs can follow the marker at once.
        var moved = Rows();
        (moved[0].IsWeb, moved[1].IsWeb) = (false, true);
        Assert.False(PortRules.NeedsRestart(saved, moved));
        Assert.NotEqual(AppSettingsModel.PortsKey(saved), AppSettingsModel.PortsKey(moved));
        // Nor does the same ports' order.
        Assert.False(PortRules.NeedsRestart(saved, Rows().AsEnumerable().Reverse()));

        // Whatever the container is created with does.
        var changes = new Action<PortRow>[]
        {
            r => r.HostPort = 3005,
            r => r.Access = PortAccess.HostOnly,
            r => r.Access = PortAccess.Internal,
            r => (r.Access, r.HostIp) = (PortAccess.Custom, "10.1.1.80"),
            r => r.ContainerPort = 8080,
            r => r.Protocol = "udp",
        };
        foreach (var change in changes)
        {
            var rows = Rows();
            change(rows[1]);
            Assert.True(PortRules.NeedsRestart(saved, rows));
        }
        Assert.True(PortRules.NeedsRestart(saved, [.. Rows(), Row(9229, PortAccess.Internal)]));
        Assert.True(PortRules.NeedsRestart(saved, Rows().Take(1)));
        // What an access doesn't use isn't a change: a LAN row's address, or an Internal row's host port.
        var lan = Rows();
        lan[1].HostIp = "10.1.1.80";
        Assert.False(PortRules.NeedsRestart(saved, lan));
        Assert.False(PortRules.NeedsRestart([new PortMapping { ContainerPort = 9229, Access = PortAccess.Internal }], [Row(9229, PortAccess.Internal, host: 3007)]));
    }
}
