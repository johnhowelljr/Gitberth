using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

public class DomainsTests
{
    private const string Account = "0123456789abcdef0123456789abcdef";
    private const string Tunnel = "11111111-2222-3333-4444-555555555555";

    private static readonly AppDefinition Orb = new()
    {
        Id = 1, Slug = "orb", Name = "orb",
        Ports = [new PortMapping { Id = 1, HostPort = 3000, ContainerPort = 80, IsWeb = true }],
    };

    private static readonly AppDefinition Kanban = new()
    {
        Id = 5, Slug = "kanban", Name = "kanban",
        Ports = [new PortMapping { Id = 2, HostPort = 3001, ContainerPort = 3000, IsWeb = true }],
    };

    private static readonly TunnelInfo HostTunnel = new("cloudflare-tunnel", Account, Tunnel, HostNetwork: true, OnAppNetwork: false);

    // Where each running app's domains go, as Gitberth works it out from `docker ps`.
    private static readonly Dictionary<int, string> Running = new() { [1] = "http://localhost:3000", [5] = "http://localhost:3001" };

    // The routes as they were made in Cloudflare's dashboard, path filter included.
    private static JsonArray DashboardIngress() => JsonNode.Parse("""
        [
          { "hostname": "orchardlane.com", "service": "http://localhost:3000" },
          { "hostname": "www.orchardlane.com", "service": "http://localhost:3000" },
          { "hostname": "harborlab.com", "service": "http://localhost:3001" },
          { "hostname": "www.harborlab.com", "service": "http://localhost:3001" },
          { "hostname": "app.harborlab.com", "path": "app", "service": "http://localhost:3001" },
          { "service": "http_status:404" }
        ]
        """)!.AsArray();

    private static JsonArray Ingress(string json) => JsonNode.Parse(json)!.AsArray();

    private static string Token(string json, bool urlSafe = false)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return urlSafe ? b64.TrimEnd('=').Replace('+', '-').Replace('/', '_') : b64;
    }

    [Fact]
    public void Reads_the_token_cloudflared_was_started_with()
    {
        var token = Token($$"""{"a":"{{Account}}","t":"{{Tunnel}}","s":"c2VjcmV0"}""");
        var found = TunnelLocator.Parse($$"""
            [{ "Name": "/cloudflare-tunnel", "Args": ["tunnel", "--no-autoupdate", "run", "--token", "{{token}}"],
               "Config": { "Env": ["PATH=/usr/bin"] }, "HostConfig": { "NetworkMode": "host" }, "NetworkSettings": { "Networks": { "host": {} } } }]
            """, "gitberth")!;
        Assert.Equal(("cloudflare-tunnel", true, false), (found.Name, found.HostNetwork, found.OnAppNetwork));
        Assert.Equal((Account, Tunnel), TunnelLocator.DecodeToken(found.Token!));
    }

    [Fact]
    public void Finds_a_token_in_the_environment_or_a_token_file()
    {
        var token = Token($$"""{"a":"{{Account}}","t":"{{Tunnel}}","s":"c2VjcmV0"}""", urlSafe: true);
        var fromEnv = TunnelLocator.Parse($$"""
            [{ "Name": "/tunnel", "Args": ["tunnel", "run"], "Config": { "Env": ["TUNNEL_TOKEN={{token}}"] },
               "HostConfig": { "NetworkMode": "bridge" }, "NetworkSettings": { "Networks": { "bridge": {}, "gitberth": {} } } }]
            """, "gitberth")!;
        Assert.Equal((Account, Tunnel), TunnelLocator.DecodeToken(fromEnv.Token!));
        Assert.True(fromEnv.OnAppNetwork);

        var fromFile = TunnelLocator.Parse("""
            [{ "Name": "/tunnel", "Args": ["tunnel", "run", "--token-file=/etc/cloudflared/token"], "HostConfig": { "NetworkMode": "host" } }]
            """, "gitberth")!;
        Assert.Equal("/etc/cloudflared/token", fromFile.TokenFile);
    }

    [Theory]
    [InlineData("""{"a":"not-an-account","t":"11111111-2222-3333-4444-555555555555","s":"x"}""")]
    [InlineData("""{"a":"0123456789abcdef0123456789abcdef","t":"../../zones","s":"x"}""")]
    [InlineData("""{"t":"11111111-2222-3333-4444-555555555555"}""")]
    public void Rejects_tokens_whose_ids_could_not_go_into_an_api_path(string json) =>
        Assert.Null(TunnelLocator.DecodeToken(Token(json)));

    [Fact]
    public void Reads_a_file_copied_out_of_a_container()
    {
        var header = new StringBuilder(new string('\0', 512));
        header.Remove(124, 12).Insert(124, Convert.ToString(9, 8).PadLeft(11, '0') + "\0");
        Assert.Equal("eyJhIjoi", DockerCli.TarFileText(header + "eyJhIjoi\n" + new string('\0', 503)));
        Assert.Null(DockerCli.TarFileText("short"));
    }

    [Fact]
    public void Reads_the_ports_a_running_container_publishes()
    {
        Assert.Equal(
            [new PublishedPort("0.0.0.0", 3001, 3000, "tcp"), new PublishedPort("127.0.0.1", 9000, 9000, "udp"), new PublishedPort("::", 3001, 3000, "tcp")],
            DockerCli.ParsePortBindings("[::]:3001->3000/tcp, 0.0.0.0:3001->3000/tcp, 127.0.0.1:9000->9000/udp, 8080/tcp"));
        Assert.Equal(
            [new PublishedPort("0.0.0.0", 3000, 3000, "tcp"), new PublishedPort("0.0.0.0", 3001, 3001, "tcp")],
            DockerCli.ParsePortBindings("0.0.0.0:3000-3001->3000-3001/tcp"));
    }

    [Fact]
    public void Reads_a_token_file_even_when_carriage_returns_were_dropped()
    {
        var header = new StringBuilder(new string('\0', 512));
        header.Remove(124, 12).Insert(124, Convert.ToString(10, 8).PadLeft(11, '0') + "\0");
        Assert.Equal("eyJhIjoi", DockerCli.TarFileText(header + "eyJhIjoi\n"));
    }

    private static readonly TunnelInfo SharedTunnel = HostTunnel with { HostNetwork = false, OnAppNetwork = true };

    [Fact]
    public void Routes_to_what_the_running_container_publishes_not_to_unapplied_settings()
    {
        // The Ports column of docker ps for the app's running container.
        const string published = "0.0.0.0:3001->3000/tcp, [::]:3001->3000/tcp";
        Assert.Equal("http://localhost:3001", TunnelRoutes.ServiceFor(Kanban, published, HostTunnel));
        // Saved as 8101 -> 3000 but not applied yet: the container still publishes 3001, so that's where the domain goes.
        var pending = new AppDefinition { Slug = "kanban", Ports = [new PortMapping { HostPort = 8101, ContainerPort = 3000, IsWeb = true }] };
        Assert.Equal("http://localhost:3001", TunnelRoutes.ServiceFor(pending, published, HostTunnel));
        // A new container port that the container doesn't have yet: nothing moves until the container does, by name either.
        var moving = new AppDefinition { Slug = "kanban", Ports = [new PortMapping { HostPort = 3001, ContainerPort = 8080, IsWeb = true }] };
        Assert.Null(TunnelRoutes.ServiceFor(moving, published, HostTunnel));
        Assert.Null(TunnelRoutes.ServiceFor(moving, published, SharedTunnel));
        Assert.Equal("http://127.0.0.1:3001", TunnelRoutes.ServiceFor(Kanban, "127.0.0.1:3001->3000/tcp", HostTunnel));
        Assert.Equal("http://kanban:3000", TunnelRoutes.ServiceFor(Kanban, published, SharedTunnel));
        Assert.Null(TunnelRoutes.ServiceFor(Kanban, null, HostTunnel));
        Assert.Null(TunnelRoutes.ServiceFor(Kanban, published, HostTunnel with { HostNetwork = false }));
        Assert.Null(TunnelRoutes.ServiceFor(new AppDefinition { Slug = "worker" }, published, HostTunnel));

        // The web port, not the first TCP one; a TCP port that isn't marked as the web port gets no domain.
        var twoPorts = new AppDefinition
        {
            Slug = "kanban",
            Ports = [new PortMapping { Id = 1, HostPort = 3009, ContainerPort = 9000 }, new PortMapping { Id = 2, HostPort = 3001, ContainerPort = 3000, IsWeb = true }],
        };
        Assert.Equal("http://localhost:3001", TunnelRoutes.ServiceFor(twoPorts, "0.0.0.0:3009->9000/tcp, " + published, HostTunnel));
        Assert.Null(TunnelRoutes.ServiceFor(new AppDefinition { Slug = "worker", Ports = [new PortMapping { HostPort = 3001, ContainerPort = 3000 }] }, published, HostTunnel));

        // An Internal web port: cloudflared on the host's network has no host port to go to; on the shared network it goes
        // by name, as long as the container has the port.
        var internalWeb = new AppDefinition { Slug = "kanban", Ports = [new PortMapping { ContainerPort = 3000, Access = Network.PortAccess.Internal, IsWeb = true }] };
        Assert.Null(TunnelRoutes.ServiceFor(internalWeb, "3000/tcp", HostTunnel));
        Assert.Equal("http://kanban:3000", TunnelRoutes.ServiceFor(internalWeb, "3000/tcp", SharedTunnel));
        Assert.Null(TunnelRoutes.ServiceFor(internalWeb, "8080/tcp", SharedTunnel));
    }

    [Fact]
    public void Takes_over_dashboard_routes_that_already_go_where_gitberth_would_send_them()
    {
        var found = TunnelRoutes.FindAdoptable(DashboardIngress(), [Orb, Kanban], Running, new HashSet<string>());
        Assert.Equal(
            [
                ("orchardlane.com", 1, false, "http://localhost:3000"), ("www.orchardlane.com", 1, false, "http://localhost:3000"),
                ("harborlab.com", 5, false, "http://localhost:3001"), ("www.harborlab.com", 5, false, "http://localhost:3001"),
                ("app.harborlab.com", 5, true, "http://localhost:3001"),
            ],
            found);
    }

    [Fact]
    public void A_host_that_already_serves_every_path_is_not_kept_on_its_paths()
    {
        var ingress = Ingress("""
            [{ "hostname": "harborlab.com", "path": "^/api", "service": "http://localhost:3001" }, { "hostname": "harborlab.com", "service": "http://localhost:3001" }]
            """);
        Assert.Equal([("harborlab.com", 5, false, "http://localhost:3001")], TunnelRoutes.FindAdoptable(ingress, [Kanban], Running, new HashSet<string>()));
    }

    [Fact]
    public void Leaves_alone_routes_it_could_not_reproduce()
    {
        var twoPorts = new AppDefinition
        {
            Id = 7, Slug = "shop", Name = "shop",
            Ports = [new PortMapping { Id = 3, HostPort = 3005, ContainerPort = 3000 }, new PortMapping { Id = 4, HostPort = 3006, ContainerPort = 4000 }],
        };
        var ingress = Ingress("""
            [
              { "hostname": "admin.shop.com", "service": "http://localhost:3006" },
              { "hostname": "tls.shop.com", "service": "https://localhost:3005" },
              { "hostname": "split.shop.com", "path": "^/api", "service": "http://localhost:9000" },
              { "hostname": "split.shop.com", "service": "http://localhost:3005" },
              { "hostname": "*.harborlab.com", "service": "http://localhost:3001" },
              { "hostname": "beta.harborlab.com", "service": "http://localhost:3001" },
              { "hostname": "stopped.example.com", "service": "http://localhost:3010" },
              { "hostname": "harborlab.com", "service": "http://localhost:3001" },
              { "service": "http_status:404" }
            ]
            """);
        var services = new Dictionary<int, string>(Running) { [7] = "http://localhost:3005" };
        var found = TunnelRoutes.FindAdoptable(ingress, [Orb, Kanban, twoPorts], services, new HashSet<string> { "harborlab.com" });
        Assert.Empty(found);
    }

    // The first sync on the real tunnel, after the domains were taken over.
    private static List<DomainRoute> Adopted(bool serveAllPaths = false) =>
    [
        new("orchardlane.com", 1, "http://localhost:3000", "http://localhost:3000"),
        new("www.orchardlane.com", 1, "http://localhost:3000", "http://localhost:3000"),
        new("harborlab.com", 5, "http://localhost:3001", "http://localhost:3001"),
        new("www.harborlab.com", 5, "http://localhost:3001", "http://localhost:3001"),
        new("app.harborlab.com", 5, "http://localhost:3001", "http://localhost:3001", KeepPaths: !serveAllPaths),
    ];

    [Fact]
    public void The_first_sync_on_the_real_tunnel_changes_nothing()
    {
        var kept = TunnelRoutes.Plan(DashboardIngress(), [Orb, Kanban], Adopted(), []);
        Assert.False(kept.Changed);
        Assert.Empty(kept.Notes);

        // As Cloudflare's API returns it, with empty origin settings on every rule: still nothing to write.
        var withOrigins = DashboardIngress();
        foreach (var rule in withOrigins.OfType<JsonObject>()) rule["originRequest"] = new JsonObject();
        Assert.False(TunnelRoutes.Plan(withOrigins, [Orb, Kanban], Adopted(), []).Changed);
    }

    [Fact]
    public void Serve_every_path_widens_only_that_domain_in_place()
    {
        var all = TunnelRoutes.Plan(DashboardIngress(), [Orb, Kanban], Adopted(serveAllPaths: true), []);
        Assert.True(all.Changed);
        Assert.Equal(
            """[{"hostname":"orchardlane.com","service":"http://localhost:3000"},{"hostname":"www.orchardlane.com","service":"http://localhost:3000"},{"hostname":"harborlab.com","service":"http://localhost:3001"},{"hostname":"www.harborlab.com","service":"http://localhost:3001"},{"hostname":"app.harborlab.com","service":"http://localhost:3001"},{"service":"http_status:404"}]""",
            all.Ingress.ToJsonString());
        var note = Assert.Single(all.Notes);
        Assert.Contains("path filter \"app\"", note.Text);
        Assert.False(TunnelRoutes.Plan(all.Ingress, [Orb, Kanban], Adopted(serveAllPaths: true), []).Changed);
    }

    [Fact]
    public void Widening_keeps_the_filtered_rules_origin_settings()
    {
        var ingress = Ingress("""[{ "hostname": "app.harborlab.com", "path": "app", "service": "http://localhost:3001", "originRequest": { "access": { "required": true, "teamName": "kanban" } } }]""");
        var plan = TunnelRoutes.Plan(ingress, [Kanban], [new DomainRoute("app.harborlab.com", 5, "http://localhost:3001", "http://localhost:3001")], []);
        Assert.Equal(
            """[{"hostname":"app.harborlab.com","service":"http://localhost:3001","originRequest":{"access":{"required":true,"teamName":"kanban"}}},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
    }

    [Fact]
    public void A_domain_kept_on_its_paths_follows_the_app_to_a_new_port_in_place()
    {
        var ingress = Ingress("""[{ "hostname": "app.harborlab.com", "path": "app", "service": "http://localhost:3001", "originRequest": { "noTLSVerify": true } }, { "hostname": "orchardlane.com", "service": "http://localhost:3000" }]""");
        var plan = TunnelRoutes.Plan(ingress, [Orb, Kanban], [new DomainRoute("app.harborlab.com", 5, "http://localhost:3005", "http://localhost:3001", KeepPaths: true)], []);
        Assert.Equal(
            """[{"hostname":"app.harborlab.com","path":"app","service":"http://localhost:3005","originRequest":{"noTLSVerify":true}},{"hostname":"orchardlane.com","service":"http://localhost:3000"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.Contains(plan.Notes, n => n.Text.StartsWith("Pointed app.harborlab.com (path \"app\") at http://localhost:3005", StringComparison.Ordinal));
        var state = TunnelRoutes.RouteState(ingress, "app.harborlab.com", Kanban, "http://localhost:3005", "http://localhost:3001", keepPaths: true);
        Assert.Equal((false, DomainFix.Sync), (state.Ok, state.Fix));
    }

    [Fact]
    public void Path_rules_to_the_apps_other_ports_are_someone_elses()
    {
        var twoPorts = new AppDefinition
        {
            Id = 7, Slug = "shop", Name = "shop",
            Ports = [new PortMapping { Id = 3, HostPort = 3005, ContainerPort = 3000 }, new PortMapping { Id = 4, HostPort = 3006, ContainerPort = 4000 }],
        };
        var ingress = Ingress("""[{ "hostname": "shop.com", "path": "^/admin", "service": "http://localhost:3006" }, { "hostname": "shop.com", "service": "http://localhost:3005" }, { "service": "http_status:404" }]""");
        var plan = TunnelRoutes.Plan(ingress, [twoPorts], [new DomainRoute("shop.com", 7, "http://localhost:3005", "http://localhost:3005")], []);
        Assert.False(plan.Changed);
    }

    [Fact]
    public void A_domain_whose_host_only_has_someone_elses_path_rule_gets_its_own_rule_after_it()
    {
        // Used to throw while writing the note, which stopped every later sync.
        var ingress = Ingress("""[{ "hostname": "harborlab.com", "path": "^/api", "service": "http://localhost:8080" }, { "service": "http_status:404" }]""");
        var route = new DomainRoute("harborlab.com", 1, "http://localhost:3000");
        var plan = TunnelRoutes.Plan(ingress, [Orb], [route], []);
        Assert.Equal(
            """[{"hostname":"harborlab.com","path":"^/api","service":"http://localhost:8080"},{"hostname":"harborlab.com","service":"http://localhost:3000"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.False(TunnelRoutes.Plan(plan.Ingress, [Orb], [route with { Previous = "http://localhost:3000" }], []).Changed);
    }

    [Fact]
    public void Remembers_the_service_as_the_tunnel_writes_it()
    {
        var ingress = Ingress("""[{ "hostname": "harborlab.com", "service": "http://gb-kanban:3000" }]""");
        Assert.Equal("http://gb-kanban:3000", TunnelRoutes.ServiceOf(ingress, "harborlab.com", "http://kanban:3000", Kanban));
        Assert.Null(TunnelRoutes.ServiceOf(ingress, "harborlab.com", "http://localhost:3001", Kanban));
    }

    [Theory]
    [InlineData("http://localhost:3001", 3001)]
    [InlineData("http://127.0.0.1:3001", 3001)]
    [InlineData("http://[::1]:3001", 3001)]
    [InlineData("http://10.1.1.80:8100", 8100)]
    [InlineData("http://kanban:3000", null)]
    [InlineData(null, null)]
    public void Knows_which_host_port_a_route_holds(string? service, int? port) =>
        Assert.Equal(port, TunnelRoutes.HostPortOf(service));

    [Fact]
    public void A_domain_narrowed_in_cloudflare_is_noticed()
    {
        var ingress = Ingress("""[{ "hostname": "harborlab.com", "path": "^/blog", "service": "http://localhost:3001" }]""");
        Assert.True(TunnelRoutes.Narrowed(ingress, "harborlab.com", Kanban, "http://localhost:3001", "http://localhost:3001"));
        Assert.False(TunnelRoutes.Narrowed(DashboardIngress(), "harborlab.com", Kanban, "http://localhost:3001", "http://localhost:3001"));
        Assert.False(TunnelRoutes.Narrowed(DashboardIngress(), "new.harborlab.com", Kanban, "http://localhost:3001", null));
    }

    [Fact]
    public void Keeps_other_services_path_rules_and_everyone_elses_order()
    {
        var ingress = Ingress("""
            [
              { "path": "^/healthz$", "service": "http_status:200" },
              { "hostname": "ssh.example.com", "service": "ssh://localhost:22" },
              { "hostname": "harborlab.com", "service": "http://localhost:3001", "originRequest": { "httpHostHeader": "harborlab.com" } },
              { "hostname": "harborlab.com", "path": "^/api", "service": "http://localhost:9000" },
              { "hostname": "*.example.com", "service": "http://localhost:7000" },
              { "hostname": "*", "service": "http_status:404" }
            ]
            """);
        var plan = TunnelRoutes.Plan(ingress, [Orb, Kanban],
            [new DomainRoute("harborlab.com", 5, "http://localhost:3002", "http://localhost:3001"), new DomainRoute("new.example.com", 5, "http://localhost:3002")], []);
        Assert.Equal(
            """[{"path":"^/healthz$","service":"http_status:200"},{"hostname":"new.example.com","service":"http://localhost:3002"},{"hostname":"ssh.example.com","service":"ssh://localhost:22"},{"hostname":"harborlab.com","path":"^/api","service":"http://localhost:9000"},{"hostname":"harborlab.com","service":"http://localhost:3002","originRequest":{"httpHostHeader":"harborlab.com"}},{"hostname":"*.example.com","service":"http://localhost:7000"},{"hostname":"*","service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.Contains(plan.Notes, n => n.Text.StartsWith("Pointed harborlab.com at http://localhost:3002", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Text == "Routed new.example.com to http://localhost:3002");
    }

    [Fact]
    public void A_removal_takes_out_only_the_rule_gitberth_wrote()
    {
        var ingress = Ingress("""
            [
              { "hostname": "harborlab.com", "path": "^/api", "service": "http://localhost:9000" },
              { "hostname": "harborlab.com", "service": "http://127.0.0.1:3001" },
              { "hostname": "orchardlane.com", "service": "http://localhost:3000" }
            ]
            """);
        var plan = TunnelRoutes.Plan(ingress, [Orb], [], [new DomainDrop("harborlab.com", "http://localhost:3001")]);
        Assert.Equal(
            """[{"hostname":"harborlab.com","path":"^/api","service":"http://localhost:9000"},{"hostname":"orchardlane.com","service":"http://localhost:3000"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.Contains(plan.Notes, n => n.Text == "Removed the tunnel's route for harborlab.com");
    }

    [Fact]
    public void Explains_why_a_route_does_not_work()
    {
        var ingress = DashboardIngress();
        const string at = "http://localhost:3001";
        var working = TunnelRoutes.RouteState(ingress, "harborlab.com", Kanban, at, at, keepPaths: false);
        Assert.Equal((true, "Port 3000, through host port 3001"), (working.Ok, working.Summary));
        // A host-only binding, a route by name on the shared network, and the new version during a deploy.
        var hostOnly = Ingress("""[{ "hostname": "harborlab.com", "service": "http://127.0.0.1:3002" }]""");
        Assert.Equal("Port 3000, through host port 3002 (host only)", TunnelRoutes.RouteState(hostOnly, "harborlab.com", Kanban, "http://127.0.0.1:3002", null, keepPaths: false).Summary);
        var byName = Ingress("""[{ "hostname": "harborlab.com", "service": "http://kanban:3000" }]""");
        Assert.Equal("Port 3000, by name (kanban:3000)", TunnelRoutes.RouteState(byName, "harborlab.com", Kanban, "http://kanban:3000", null, keepPaths: false).Summary);
        var bridge = Ingress("""[{ "hostname": "harborlab.com", "service": "http://127.0.0.1:8100" }]""");
        Assert.Equal("Port 3000 on the new version, through host port 8100",
            TunnelRoutes.RouteState(bridge, "harborlab.com", Kanban, "http://127.0.0.1:8100", at, keepPaths: false, newVersion: true).Summary);
        var filtered = TunnelRoutes.RouteState(ingress, "app.harborlab.com", Kanban, at, at, keepPaths: true);
        Assert.Equal((false, DomainFix.ServeAllPaths), (filtered.Ok, filtered.Fix));
        Assert.Contains("404", filtered.Summary);
        Assert.Equal(DomainFix.Sync, TunnelRoutes.RouteState(ingress, "orchardlane.com", Kanban, at, null, keepPaths: false).Fix);
        Assert.Equal(DomainFix.None, TunnelRoutes.RouteState(ingress, "orchardlane.com", Kanban, at, null, keepPaths: true).Fix);
        Assert.Equal("The tunnel has no route for it yet.", TunnelRoutes.RouteState(ingress, "new.harborlab.com", Kanban, at, null, keepPaths: false).Summary);
        var shadowed = Ingress("""[{ "hostname": "*.harborlab.com", "service": "http://localhost:7000" }, { "hostname": "new.harborlab.com", "service": "http://localhost:3001" }]""");
        Assert.False(TunnelRoutes.RouteState(shadowed, "new.harborlab.com", Kanban, at, at, keepPaths: false).Ok);
        // Read in order: another service's every-path rule above the app's own one takes everything.
        var shadowedByHost = Ingress("""[{ "hostname": "harborlab.com", "service": "http://localhost:7000" }, { "hostname": "harborlab.com", "service": "http://localhost:3001" }]""");
        Assert.False(TunnelRoutes.RouteState(shadowedByHost, "harborlab.com", Kanban, at, at, keepPaths: false).Ok);
    }

    [Theory]
    [InlineData("http://localhost:3001", "http://127.0.0.1:3001", true)]
    [InlineData("http://[::1]:3001", "http://localhost:3001", true)]
    [InlineData("http://gb-kanban:3000", "http://kanban:3000", true)]
    [InlineData("https://localhost:3001", "http://localhost:3001", false)]
    [InlineData("http://localhost:3002", "http://localhost:3001", false)]
    [InlineData("http://10.1.1.80:3001", "http://localhost:3001", false)]
    public void Compares_where_routes_go(string service, string target, bool same) =>
        Assert.Equal(same, TunnelRoutes.SameService(service, target, Kanban));

    [Theory]
    [InlineData("https://App.HarborLab.com/app", "app.harborlab.com")]
    [InlineData("harborlab.com.", "harborlab.com")]
    [InlineData("  www.orchardlane.com ", "www.orchardlane.com")]
    [InlineData("10.1.1.80", null)]
    [InlineData("", null)]
    public void Normalizes_what_people_type(string input, string? expected) =>
        Assert.Equal(expected, DomainNames.Normalize(input));

    [Theory]
    [InlineData("app.harborlab.com", true)]
    [InlineData("xn--bcher-kva.example", true)]
    [InlineData("localhost", false)]
    [InlineData("*.example.com", false)]
    [InlineData("-bad.example.com", false)]
    public void Accepts_only_routable_domains(string hostname, bool ok) =>
        Assert.Equal(ok, DomainNames.Problem(hostname) is null);

    [Theory]
    [InlineData("harborlab.com", false)]
    [InlineData("app.harborlab.com", false)]
    [InlineData("a.b.harborlab.com", true)]
    public void Warns_about_names_the_free_certificate_does_not_cover(string hostname, bool warned) =>
        Assert.Equal(warned, DomainNames.CertificateWarning(hostname, "harborlab.com") is not null);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Finds_the_zone_a_subdomain_lives_in_within_the_tunnels_account()
    {
        var handler = new StubHandler(r => r.RequestUri!.Query.Contains("name=harborlab.com")
            ? Json(HttpStatusCode.OK, """{"success":true,"result":[{"id":"zone1","name":"harborlab.com"}]}""")
            : Json(HttpStatusCode.OK, """{"success":true,"result":[]}"""));
        var zone = await new CloudflareApi(new Factory(handler)).FindZoneAsync("t", Account, "app.harborlab.com");
        Assert.Equal(new CloudflareZone("zone1", "harborlab.com"), zone);
        Assert.Equal(
            [$"GET /client/v4/zones?name=app.harborlab.com&account.id={Account}&status=active", $"GET /client/v4/zones?name=harborlab.com&account.id={Account}&status=active"],
            handler.Calls);
    }

    [Fact]
    public async Task Reads_only_the_records_that_decide_where_a_name_goes()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""
            {"success":true,"result":[
              {"id":"r1","type":"CNAME","name":"app.harborlab.com","content":"{{Tunnel}}.cfargotunnel.com","proxied":true,"comment":"Added by Gitberth"},
              {"id":"r2","type":"TXT","name":"app.harborlab.com","content":"v=spf1","proxied":false},
              {"id":"r3","type":"A","name":"x.app.harborlab.com","content":"1.2.3.4","proxied":false}
            ]}
            """));
        var records = await new CloudflareApi(new Factory(handler)).GetDnsRecordsAsync("t", "zone1", "app.harborlab.com");
        Assert.Equal([new DnsRecord("r1", "CNAME", "app.harborlab.com", $"{Tunnel}.cfargotunnel.com", true, "Added by Gitberth")], records);
    }

    [Theory]
    [InlineData("Added by Gitberth", true)]
    [InlineData("Added by GitDock", true)]
    [InlineData("Added by hand", false)]
    [InlineData(null, false)]
    public void Only_records_Gitberth_added_are_removed_with_a_domain(string? comment, bool ours) =>
        Assert.Equal(ours, CloudflareApi.AddedByUs(comment));

    [Fact]
    public async Task A_missing_permission_names_the_one_the_token_needs()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}"""));
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => new CloudflareApi(new Factory(handler)).GetTunnelConfigAsync("t", Account, Tunnel));
        Assert.Contains("read the tunnel's routes", ex.Message);
        Assert.Contains("Cloudflare Tunnel › Edit", ex.Message);
    }

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    [Fact]
    public async Task The_domains_card_says_why_an_app_isnt_routed_or_served()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        int appId;
        using (var context = db.CreateDbContext())
        {
            context.Database.EnsureCreated();
            // A TCP port, but none marked as the web port.
            var app = new AppDefinition { Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban" };
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001 });
            app.Domains.Add(new CustomDomain { Hostname = "app.harborlab.com" });
            context.Apps.Add(app);
            context.SaveChanges();
            appId = app.Id;
        }
        var settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        await settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");
        // cloudflared runs on the host's network; kanban's container isn't running. Cloudflare is never needed to say so.
        var token = Token($$"""{"a":"{{Account}}","t":"{{Tunnel}}","s":"c2VjcmV0"}""");
        var cli = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, _, _) => Task.FromResult(args switch
            {
                ["ps", ..] => new ProcessResult(0, """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""", ""),
                ["container", "inspect", "tunnel"] => new ProcessResult(0,
                    """[{"Name":"/tunnel","Args":["tunnel","run","--token","TOKEN"],"Config":{"Cmd":[],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{"host":{}}}}]"""
                        .Replace("TOKEN", token, StringComparison.Ordinal), ""),
                _ => new ProcessResult(1, "", "unknown command " + string.Join(' ', args)),
            }),
        };
        var domains = new DomainService(db, settings, new CloudflareApi(new Factory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))),
            new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        async Task<string> SummaryAsync() => Assert.Single((await domains.GetOverviewAsync(appId)).Domains).Summary;

        Assert.Equal("Not routed: kanban has no web port.", await SummaryAsync());

        await using (var context = db.CreateDbContext())
            await context.Ports.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsWeb, true));
        Assert.Equal("Not serving: kanban isn't running, or its saved ports aren't live yet.", await SummaryAsync());

        // An Internal web port has no host port, which is all cloudflared on the host's network can reach.
        await using (var context = db.CreateDbContext())
            await context.Ports.ExecuteUpdateAsync(s => s.SetProperty(p => p.Access, Network.PortAccess.Internal).SetProperty(p => p.HostPort, (int?)null));
        Assert.Equal("app.harborlab.com needs the web port on the host. Set it to LAN or Host only.", await SummaryAsync());
    }
}
