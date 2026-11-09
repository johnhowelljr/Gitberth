using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
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
/// Who uses each host port, on CT 101 as captured (Docker's list, each container's inspect and the host's /proc), plus a
/// few containers made up for the cases the host doesn't have.
/// </summary>
public class HostPortMapTests
{
    // Gitberth's hostname on CT 101: its container's short ID.
    private const string Self = "455b8ea31b6d";
    private static readonly (int From, int To) Random = (32768, 60999);

    /// <summary>The containers on CT 101, with what each was created with, and any others.</summary>
    private static NetworkSnapshot Docker(params NetworkContainer[] others)
    {
        var configs = new Dictionary<string, NetworkPortConfig>(StringComparer.Ordinal);
        foreach (var name in new[] { "postgres", "gitberth", "cloudflare-tunnel", "gb-orb", "gb-kanban", "sample-bun-elysia", "sample-bun-hono", "sample-index" })
        {
            var inspect = DockerFixtures.Inspect(name);
            configs[(string)inspect["Id"]!] = NetworkPortConfig.From(inspect);
        }
        return new NetworkSnapshot(DateTime.UtcNow, [.. NetworkContainer.ParseList(DockerFixtures.Text("containers-json.json")), .. others], configs);
    }

    /// <summary>Only these containers, configured as they're listed.</summary>
    private static NetworkSnapshot Only(params NetworkContainer[] containers) => new(DateTime.UtcNow, containers, new Dictionary<string, NetworkPortConfig>());

    private static NetworkContainer Running(string name, params NetworkPort[] ports) =>
        new($"c0ffee{name}", name, "img", "sha256:1", "running", "Up 1 hour", "", new Dictionary<string, string>(), ports, "bridge", []);

    private static NetworkPort Tcp(int host, int container, string ip = "0.0.0.0") => new(ip, container, host, "tcp");

    // Its first port is its web port, as the migration marks an app's first TCP port.
    private static AppDefinition App(int id, string slug, params (int Host, int Container)[] ports) => new()
    {
        Id = id,
        Name = slug,
        Slug = slug,
        Ports = ports.Select((p, i) => new PortMapping { Id = id * 10 + i, AppId = id, HostPort = p.Host, ContainerPort = p.Container, IsWeb = i == 0 }).ToList(),
    };

    // orb and kanban as they're saved on CT 101; kanban has two domains.
    private static List<AppDefinition> Apps()
    {
        var kanban = App(2, "kanban", (3001, 3000));
        kanban.Domains.AddRange([new CustomDomain { Id = 1, AppId = 2, Hostname = "app.harborlab.com" }, new CustomDomain { Id = 2, AppId = 2, Hostname = "harborlab.com" }]);
        return [App(1, "orb", (3000, 80)), kanban];
    }

    private static HostScan Host() => HostScanHelper.Read(DockerFixtures.ProcFiles(), DateTime.UtcNow);

    // Nothing listening on the host, for the cases that only want Docker's ports.
    private static HostScan Quiet() => new(DateTime.UtcNow, [], Random, "eth0", "10.1.1.80", "10.1.1.1", false);

    private static List<HostPortUse> Map(NetworkSnapshot? docker = null, List<AppDefinition>? apps = null, List<(int, int?, string)>? routes = null,
        HashSet<int>? tunnel = null, HostScan? scan = null) =>
        HostPortMap.Build(new HostPortSources(docker ?? Docker(), apps ?? Apps(), routes ?? [], tunnel ?? [], scan ?? Host(), Self));

    private static HostPortCheck Check(IReadOnlyList<HostPortUse> uses, int port, string protocol = "tcp", string? hostIp = null, HostPortOwner? self = null) =>
        HostPortMap.Check(uses, Random, port, protocol, hostIp, self);

    [Fact]
    public void Names_who_holds_each_port()
    {
        var uses = Map();

        Assert.Equal("22/tcp is SSH on the host.", Check(uses, 22).Sentence);
        Assert.Equal("2375/tcp is Docker's API on the host.", Check(uses, 2375).Sentence);
        Assert.Equal("3000/tcp is used by orb, port 80.", Check(uses, 3000).Sentence);
        Assert.Equal("3001/tcp is used by kanban, port 3000.", Check(uses, 3001).Sentence);
        Assert.Equal("8080/tcp is Gitberth.", Check(uses, 8080).Sentence);
        // cloudflared's metrics: it runs on the host's network, so it's a program on the host as far as ports go.
        Assert.Equal("20241/tcp is used by a program on the host, outside Docker.", Check(uses, 20241).Sentence);
        Assert.Equal("65001/tcp is free.", Check(uses, 65001).Sentence);
        Assert.Equal("Free", Check(uses, 65001).FieldText);

        var kanban = Assert.Single(uses, u => u.Port == 3001);
        // Its page is the app's, by its container's name.
        Assert.Equal(("kanban", "apps/gb-kanban", "LAN", 3000, "2 domains"), (kanban.Owner, kanban.Link, HostPortMap.Access(kanban.HostIp), kanban.ContainerPort, kanban.Note));
        Assert.Equal("Gitberth's own page.", Check(uses, 8080).FieldText);
        Assert.Equal("SSH (host)", Assert.Single(uses, u => u.Port == 22).Owner);
        // Mail listens on the host's loopback only (127.0.0.1 and ::1, one use).
        Assert.Equal("Host only", HostPortMap.Access(Assert.Single(uses, u => u.Port == 25).HostIp));
    }

    [Fact]
    public void A_stopped_containers_ports_are_still_taken()
    {
        // sample-index was created with -p 0.0.0.0:9100:80 and is stopped: nothing listens there, but it takes it when it starts.
        var check = Check(Map(), 9100);

        Assert.Equal(HostPortState.Stopped, check.Owner!.State);
        Assert.Equal("9100/tcp is used by sample-index (stopped): it takes it when it starts.", check.Sentence);
        Assert.Equal("Taken by sample-index (stopped): it takes it when it starts.", check.FieldText);
        Assert.Equal("Stopped: takes it when started", check.Owner.Note);
    }

    [Fact]
    public void A_restarting_containers_ports_are_still_taken()
    {
        // A crash-looping postgres (-p 5432:5432, --restart unless-stopped): between attempts Docker lists no ports for it
        // and nothing listens on 5432, but it takes 5432 again at its next start.
        var postgres = Running("pg") with { State = "restarting", Status = "Restarting (1) 20 seconds ago" };
        var snapshot = new NetworkSnapshot(DateTime.UtcNow, [postgres], new Dictionary<string, NetworkPortConfig>
        {
            [postgres.Id] = new([new PortBinding(null, 5432, 5432, "tcp")], [new ExposedPort(5432, "tcp")], PublishAll: false),
        });
        var uses = HostPortMap.Build(new HostPortSources(snapshot, [], [], new HashSet<int>(), Quiet(), null));

        var check = Check(uses, 5432);
        Assert.Equal((HostPortState.Stopped, HostPortUse.RestartingNote), (check.Owner!.State, check.Owner.Note));
        Assert.Equal("5432/tcp is used by pg (restarting): it takes it when it starts.", check.Sentence);
        Assert.Equal("Taken by pg (restarting): it takes it when it starts.", check.FieldText);
        Assert.Equal(5433, HostPortMap.Suggest(uses, Random, 5432, "tcp", null, []));
    }

    [Fact]
    public void A_saved_app_port_is_taken_before_it_is_live()
    {
        var apps = Apps();
        apps[1].Ports.Add(new PortMapping { Id = 99, AppId = 2, HostPort = 3005, ContainerPort = 9229 });
        var uses = Map(apps: apps);

        Assert.Equal("3005/tcp is saved for kanban, not live yet.", Check(uses, 3005).Sentence);
        Assert.Equal("Saved for kanban, not live yet.", Check(uses, 3005).FieldText);
        Assert.Equal("Saved, not live", Assert.Single(uses, u => u.Port == 3005).Note);
        // An app's own saved port doesn't count against it.
        Assert.True(Check(uses, 3005, self: new HostPortOwner(AppId: 2)).Free);
    }

    [Fact]
    public void A_port_a_route_points_at_is_taken()
    {
        var uses = Map(routes: [(3002, null, "old.example.com"), (3001, 2, "app.harborlab.com")], tunnel: [3001, 3002, 3010]);

        Assert.Equal("3002/tcp is where the tunnel sends old.example.com.", Check(uses, 3002).Sentence);
        Assert.Equal("Kept for the domain old.example.com.", Check(uses, 3002).FieldText);
        Assert.Equal(("The tunnel", "Tunnel route to old.example.com"), (Assert.Single(uses, u => u.Port == 3002).Owner, uses.Single(u => u.Port == 3002).Note));
        // A route to its own app's binding is how the domain gets there, not another use of the port.
        Assert.Single(uses, u => u.Port == 3001);
        // A rule made in Cloudflare's dashboard holds its port too.
        Assert.Equal("3010/tcp is where the tunnel sends a domain.", Check(uses, 3010).Sentence);
    }

    [Fact]
    public void Tcp_and_udp_are_counted_apart()
    {
        var uses = Map(Docker(Running("dns", new NetworkPort("0.0.0.0", 53, 53, "udp"))));

        Assert.Equal("53/udp is used by dns, port 53.", Check(uses, 53, "udp").Sentence);
        Assert.True(Check(uses, 53, "tcp").Free);
        Assert.True(Check(uses, 3000, "udp").Free);
    }

    [Fact]
    public void A_loopback_binding_conflicts_with_an_all_addresses_one()
    {
        var uses = Map(Docker(
            Running("pg-old", Tcp(5432, 5432, "127.0.0.1")),
            Running("cache", Tcp(6379, 6379), Tcp(6379, 6379, "::"))));

        // Every address includes the loopback, both ways round.
        Assert.False(Check(uses, 5432, hostIp: "").Free);
        Assert.False(Check(uses, 5432, hostIp: "127.0.0.1").Free);
        Assert.False(Check(uses, 6379, hostIp: "127.0.0.1").Free);
        Assert.Equal("Used by pg-old.", Check(uses, 5432).FieldText);
        // Another single address of the host doesn't clash.
        Assert.True(Check(uses, 5432, hostIp: "10.1.1.80").Free);
    }

    [Fact]
    public void Dockers_bindings_clash_as_docker_decides_and_the_hosts_sockets_as_safely_as_can_be()
    {
        // ipv4 publishes 5500 on 0.0.0.0 alone, ipv6 publishes 5600 on :: alone; the host has a socket on [::]:5700.
        var scan = Quiet() with { Sockets = [new ProcNetSocket("tcp", "::", 5700)] };
        var uses = Map(Only(Running("ipv4", Tcp(5500, 5500, "0.0.0.0")), Running("ipv6", Tcp(5600, 5600, "::"))), apps: [], scan: scan);

        // Between Docker's bindings Docker's own rule decides, as it does for a recreate: 0.0.0.0 and :: don't clash.
        Assert.Equal("0.0.0.0", Assert.Single(uses, u => u.Port == 5500).HostIp);
        Assert.True(Check(uses, 5500, hostIp: "::").Free);
        Assert.False(Check(uses, 5500, hostIp: "127.0.0.1").Free);
        Assert.False(Check(uses, 5500, hostIp: "").Free);
        Assert.True(Check(uses, 5600, hostIp: "0.0.0.0").Free);
        Assert.False(Check(uses, 5600, hostIp: "::1").Free);
        // A program's [::] socket may be dual-stack: it holds the port on IPv4 too.
        Assert.Equal("5700/tcp is used by a program on the host, outside Docker.", Check(uses, 5700, hostIp: "0.0.0.0").Sentence);
        Assert.False(Check(uses, 5700, hostIp: "127.0.0.1").Free);
        // A port change names who holds one.
        Assert.Equal("ipv4", Check(uses, 5500, hostIp: "").Holder);
        Assert.Equal("a program on the host", Check(uses, 5700).Holder);
        Assert.Null(Check(uses, 5501).Holder);
    }

    [Fact]
    public void A_port_change_names_the_domains_the_tunnel_sends_to_a_host_port()
    {
        // A rule made in Cloudflare's dashboard counts as much as Gitberth's; a rule to a container by name holds no host port.
        var ingress = new JsonArray(
            new JsonObject { ["hostname"] = "status.example.com", ["service"] = "http://localhost:3002" },
            new JsonObject { ["hostname"] = "app.harborlab.com", ["service"] = "http://kanban:3000" },
            new JsonObject { ["hostname"] = "old.example.com", ["path"] = "/api", ["service"] = "http://127.0.0.1:3002" },
            new JsonObject { ["service"] = "http_status:404" });

        Assert.Equal([("status.example.com", 3002), ("old.example.com", 3002)], HostPortChecks.Rules(ingress));
    }

    [Fact]
    public void A_port_change_names_who_holds_a_host_port()
    {
        var uses = Map(routes: [(3002, null, "old.example.com")], tunnel: [3002]);

        Assert.Equal("orb", Check(uses, 3000).Holder);
        Assert.Equal("Gitberth's own page", Check(uses, 8080).Holder);
        Assert.Equal("SSH on the host", Check(uses, 22).Holder);
        Assert.Equal("Docker's API on the host", Check(uses, 2375).Holder);
        Assert.Equal("sample-index (stopped)", Check(uses, 9100).Holder);
        Assert.Equal("the tunnel's route to old.example.com", Check(uses, 3002).Holder);
    }

    [Fact]
    public void A_containers_own_bindings_dont_count_against_it()
    {
        // A deploy's previous container, kept aside with the app's port.
        var previous = Running("gb-kanban_prev", Tcp(3001, 3000)) with { Labels = new Dictionary<string, string> { ["gitberth.app"] = "kanban" } };
        var uses = Map(Docker(previous));

        Assert.False(Check(uses, 3001).Free);
        Assert.True(Check(uses, 3001, self: new HostPortOwner(AppId: 2)).Free);
        Assert.True(Check(uses, 3001, self: new HostPortOwner("gb-kanban")).Free);
        Assert.True(Check(uses, 9100, self: new HostPortOwner("sample-index")).Free);
        Assert.False(Check(uses, 3001, self: new HostPortOwner("gb-orb")).Free);
        Assert.Equal("3001/tcp is used by a deploy of kanban, port 3000.",
            HostPortMap.Check(uses.Where(u => u.Container == "gb-kanban_prev").ToList(), Random, 3001, "tcp", null, null).Sentence);
    }

    [Fact]
    public void Suggests_the_containers_own_port_when_free()
    {
        Assert.Equal(5432, HostPortMap.Suggest(Map(), Random, 5432, "tcp", null, []));
        // Taken by itself is free to it.
        Assert.Equal(3000, HostPortMap.Suggest(Map(), Random, 3000, "tcp", new HostPortOwner(AppId: 1), []));
    }

    [Fact]
    public void Suggests_the_next_free_port_above_it_otherwise()
    {
        var uses = Map();
        // 3000 is orb's and 3001 kanban's.
        Assert.Equal(3002, HostPortMap.Suggest(uses, Random, 3000, "tcp", null, []));
        Assert.Equal(3003, HostPortMap.Suggest(uses, Random, 3000, "tcp", null, [3002]));
        Assert.Equal(9103, HostPortMap.Suggest(uses, Random, 9100, "tcp", null, []));
    }

    [Fact]
    public void Never_suggests_22_2375_2376_8080_or_the_random_range()
    {
        var uses = HostPortMap.Build(new HostPortSources(
            Only(Running("ftp", Tcp(21, 21)), Running("a", Tcp(2374, 2374)), Running("b", Tcp(8079, 8079)), Running("c", Tcp(32767, 32767))),
            [], [], new HashSet<int>(), Quiet(), null));

        // Nothing holds 22, 2375, 2376 or 8080 here, and they still aren't offered.
        Assert.Equal(23, HostPortMap.Suggest(uses, Random, 21, "tcp", null, []));
        Assert.Equal(2377, HostPortMap.Suggest(uses, Random, 2374, "tcp", null, []));
        Assert.Equal(8081, HostPortMap.Suggest(uses, Random, 8079, "tcp", null, []));
        Assert.Equal(61000, HostPortMap.Suggest(uses, Random, 32767, "tcp", null, []));
        // A container's own port is offered whatever it is, when it's free.
        Assert.Equal(22, HostPortMap.Suggest(uses, Random, 22, "tcp", null, []));
        Assert.Equal(40000, HostPortMap.Suggest(uses, Random, 40000, "tcp", null, []));
    }

    [Fact]
    public void The_spare_port_comes_from_8100_up()
    {
        HostPortSources Sources(params NetworkContainer[] containers) => new(Only(containers), [], [], new HashSet<int>(), Quiet(), null);

        Assert.Equal(8100, HostPortMap.Spare(HostPortMap.Build(Sources()), Random, 8100, []));

        // A deploy's temporary container binds 127.0.0.1: every address and 127.0.0.1 clash with it, another address doesn't.
        var busy = HostPortMap.Build(Sources(Running("a", Tcp(8100, 80)), Running("b", Tcp(8101, 80, "127.0.0.1")), Running("c", Tcp(8102, 80, "10.1.1.80"))));
        Assert.Equal(8102, HostPortMap.Spare(busy, Random, 8100, []));
        Assert.Equal(8103, HostPortMap.Spare(busy, Random, 8100, [8102]));

        // A full 8100-8199 doesn't stop zero-downtime deploys: it carries on up to the random range.
        var full = HostPortMap.Build(Sources([.. Enumerable.Range(8100, 100).Select(p => Running($"s{p}", Tcp(p, 80)))]));
        Assert.Equal(8200, HostPortMap.Spare(full, Random, 8100, []));
    }

    [Fact]
    public void Folds_runs_of_similar_containers()
    {
        var samples = Enumerable.Range(0, 79).Select(i => Running($"sample-app{i:00}", Tcp(9100 + i, 80)));
        var uses = HostPortMap.Build(new HostPortSources(
            Only([.. samples, Running("web", Tcp(9179, 80)), Running("db-a", Tcp(9200, 5432)), Running("db-b", Tcp(9201, 5432)), Running("db-c", Tcp(9202, 5432)), Running("db-d", Tcp(9203, 5432))]),
            [], [], new HashSet<int>(), Quiet(), null));

        var rows = HostPortMap.Fold(uses);

        var folded = Assert.Single(rows, r => r.Folded);
        Assert.Equal((9100, 9178, "sample-", 79), (folded.Port, folded.LastPort, folded.Prefix, folded.Uses.Count));
        // A container named otherwise ends the run, and four in a row stay lines of their own.
        Assert.Contains(rows, r => !r.Folded && r.First.Who == "web");
        Assert.Equal(4, rows.Count(r => !r.Folded && r.First.Who.StartsWith("db-", StringComparison.Ordinal)));
        Assert.Equal([9100, 9179, 9200, 9201, 9202, 9203], rows.Select(r => r.Port));
    }

    [Fact]
    public void Docker_proxys_sockets_are_the_containers_ports_not_programs_on_the_host()
    {
        var uses = Map();

        // docker-proxy listens on 8080, 3000 and 3001 for Gitberth and the apps.
        Assert.DoesNotContain(uses, u => u.Kind == HostPortUser.HostProgram && u.Port is 8080 or 3000 or 3001);
        Assert.Contains(uses, u => u.Kind == HostPortUser.HostProgram && u.Port == 20241);
    }

    [Fact]
    public void Without_the_hosts_sockets_ssh_and_dockers_api_are_still_taken()
    {
        var uses = Map(scan: HostScan.Failed("the helper container failed"));

        Assert.Equal("22/tcp is SSH on the host.", Check(uses, 22).Sentence);
        Assert.Equal("2376/tcp is Docker's API on the host.", Check(uses, 2376).Sentence);
        Assert.DoesNotContain(uses, u => u.Kind == HostPortUser.HostProgram);
        // The random range falls back to Linux's default.
        Assert.Equal((32768, 60999), HostScan.Failed("x").Random);
    }

    [Fact]
    public void The_page_leaves_out_programs_passing_udp_sockets_but_checks_count_them()
    {
        var uses = Map();
        var listed = HostPortMap.Listed(uses, Random);

        // cloudflared's QUIC sockets come and go in the random range.
        Assert.Contains(uses, u => u.Port == 35387 && u.Protocol == "udp");
        Assert.DoesNotContain(listed, u => u.Protocol == "udp" && u.Kind == HostPortUser.HostProgram);
        Assert.Contains(listed, u => u.Port == 20241);
    }

    [Fact]
    public void Only_a_start_or_a_stop_puts_the_hosts_sockets_out_of_date()
    {
        var t = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(HostPortMap.LastStartOrStop([]));
        Assert.Equal(t.AddSeconds(2), HostPortMap.LastStartOrStop(
        [
            new ContainerEvent(t, "79d00d033083", "gb-orb", "start"),
            new ContainerEvent(t.AddSeconds(2), "79d00d033083", "gb-orb", "die", ExitCode: 0),
            // A health check opens and closes no socket on the host.
            new ContainerEvent(t.AddSeconds(5), "79d00d033083", "gb-orb", "health_status: healthy"),
        ]));
    }

    [Fact]
    public async Task A_container_that_stops_is_read_again_from_the_hosts_sockets()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        await using (var context = db.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
            context.Apps.AddRange(
                new AppDefinition { Name = "orb", Slug = "orb", Ports = [new PortMapping { HostPort = 3000, ContainerPort = 80 }] },
                new AppDefinition { Name = "kanban", Slug = "kanban", Ports = [new PortMapping { HostPort = 3001, ContainerPort = 3000 }] });
            await context.SaveChangesAsync();
        }
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        using var engine = new Engine();
        using var api = new DockerApi("unix:///var/run/docker.sock", engine);
        // The host's sockets as the helper prints them: docker-proxy holds 3000 for orb while orb runs.
        var obsRunning = true;
        var runs = 0;
        var cli = new DockerCli(options)
        {
            Runner = (args, _, _) =>
            {
                var files = DockerFixtures.ProcFiles();
                if (!obsRunning)
                    foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
                        files[table] = string.Join('\n', files[table].Split('\n').Where(l => !l.Contains(":0BB8 ", StringComparison.Ordinal)));
                if (args[0] == "run") runs++;
                return Task.FromResult(args[0] switch
                {
                    "container" => new ProcessResult(0, "sha256:0c1a38670237\n", ""),
                    "run" => new ProcessResult(0, string.Concat(files.Select(f => $"==> {f.Key} <==\n{f.Value}\n")), ""),
                    _ => new ProcessResult(0, "", ""),
                });
            },
        };
        var settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var events = new ContainerEventWatcher(api, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance);
        var map = new HostPortMap(new NetworkService(api, db, options), db,
            new DomainService(db, settings, new CloudflareApi(new NoCloudflare()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance),
            new HostScanner(cli, options, NullLogger<HostScanner>.Instance) { InContainer = true }, settings, events, NullLogger<HostPortMap>.Instance);

        var before = await map.GetViewAsync();
        Assert.Equal("3000/tcp is used by orb, port 80.", Check(before.Uses, 3000).Sentence);

        // orb stops: Docker lists it without ports, docker-proxy lets go of 3000, and the event comes in.
        var orb = engine.List.Single(c => (string)c!["Names"]![0]! == "/gb-orb")!;
        orb["State"] = "exited";
        orb["Status"] = "Exited (0) 1 second ago";
        orb["Ports"] = new JsonArray();
        obsRunning = false;
        await Task.Delay(5);
        events.Remember(new ContainerEvent(DateTime.UtcNow, (string)orb["Id"]!, "gb-orb", "die", ExitCode: 0), DateTime.UtcNow);
        await Task.Delay(5);

        var after = await map.GetViewAsync();
        // The kept scan still had docker-proxy's socket on 3000, which nothing would account for now.
        Assert.Equal(2, runs);
        Assert.DoesNotContain(after.Uses, u => u.Port == 3000 && u.Kind == HostPortUser.HostProgram);
        Assert.Equal("3000/tcp is used by orb (stopped): it takes it when it starts.", Check(after.Uses, 3000).Sentence);

        // An event that comes in late, dated before that reading began, still makes it out of date. It's no start or
        // stop, so the host isn't scanned again.
        events.Remember(new ContainerEvent(after.ReadAt.AddMilliseconds(-1), "27c0957a4ff7", "gb-kanban", "health_status: healthy"), DateTime.UtcNow);
        Assert.NotSame(after, await map.GetViewAsync());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_host_that_couldnt_be_read_gets_the_lans_ports_on_ipv4_only()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        await using (var context = db.CreateDbContext()) await context.Database.EnsureCreatedAsync();
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        using var engine = new Engine();
        using var api = new DockerApi("unix:///var/run/docker.sock", engine);
        var cli = new DockerCli(options) { Runner = (args, _, _) => throw new InvalidOperationException($"docker {string.Join(' ', args)} was run.") };
        var settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var domains = new DomainService(db, settings, new CloudflareApi(new NoCloudflare()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        HostPortChecks Checks(HostScanner scanner) => new(
            new HostPortMap(new NetworkService(api, db, options), db, domains, scanner, settings,
                new ContainerEventWatcher(api, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance), NullLogger<HostPortMap>.Instance),
            scanner, settings, domains, new CloudflareApi(new NoCloudflare()), options, NullLogger<HostPortChecks>.Instance);

        // The host's network couldn't be read, so it may have an IPv6 address the internet reaches: a LAN port goes on
        // IPv4 only, never on every address on a guess.
        var failed = new HostScanner(cli, options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false };
        Assert.NotNull((await failed.GetAsync()).Error);
        Assert.Equal("0.0.0.0", await Checks(failed).LanHostIpAsync(default));

        // Read, and without one: every address of the host.
        var read = new HostScanner(cli, options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = true, ReadFile = path => DockerFixtures.ProcFiles().GetValueOrDefault(path) };
        Assert.Equal("", await Checks(read).LanHostIpAsync(default));
    }

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>Domains are off, so Cloudflare is never asked.</summary>
    private sealed class NoCloudflare : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cloudflare was asked while domains are off.");
    }

    /// <summary>The engine on CT 101, whose list of containers a test can change; each container's inspect as captured.</summary>
    private sealed class Engine : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _inspects = new(StringComparer.Ordinal);

        public Engine()
        {
            foreach (var name in new[] { "postgres", "gitberth", "cloudflare-tunnel", "gb-orb", "gb-kanban", "sample-bun-elysia", "sample-bun-hono", "sample-index" })
            {
                var inspect = DockerFixtures.Inspect(name);
                _inspects[(string)inspect["Id"]!] = inspect.ToJsonString();
            }
        }

        /// <summary>Docker's list of containers, as it answers now.</summary>
        public JsonArray List { get; } = DockerFixtures.Json("containers-json.json").AsArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/version" => DockerFixtures.Text("version.json"),
                "/v1.56/containers/json" => List.ToJsonString(),
                _ when path.StartsWith("/v1.56/containers/", StringComparison.Ordinal) => _inspects.GetValueOrDefault(path["/v1.56/containers/".Length..^"/json".Length]),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such container"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
