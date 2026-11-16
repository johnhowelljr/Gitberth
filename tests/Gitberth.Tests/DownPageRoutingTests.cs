using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Public;

namespace Gitberth.Tests;

/// <summary>
/// The down page in the tunnel: a domain that shows it goes to Gitberth's own service with a marker host header, keeps
/// its other settings (Access), is recognized as Gitberth's both ways, and goes back to the app without the marker. No
/// rule that goes to an app ever keeps a marker, and every rule to Gitberth's own service carries the webhook path or one.
/// </summary>
public class DownPageRoutingTests
{
    private const string Own = "http://gitberth:8080";
    private const string AppService = "http://web:3000";

    private static readonly AppDefinition Web = new()
    {
        Id = 1, Slug = "web", Name = "web",
        Ports = [new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true }],
    };

    private static JsonArray Ingress(string json) => JsonNode.Parse(json)!.AsArray();

    private static List<JsonObject> RulesFor(JsonArray ingress, string host) =>
        ingress.OfType<JsonObject>().Where(r => (string?)r["hostname"] == host).ToList();

    private static string? Marker(JsonObject rule) => TunnelRoutes.HostHeaderOf(rule);

    private static DomainRoute Down(string host, int domainId, string? previous = AppService, bool keepPaths = false, bool bare = false) =>
        new(host, Web.Id, Own, previous, keepPaths, MarkerHosts.Down(domainId), bare);

    private static DomainRoute Up(string host, string? previous = Own, bool keepPaths = false) => new(host, Web.Id, AppService, previous, keepPaths);

    [Fact]
    public void Marker_hosts_name_a_domain_or_the_status_page_and_nothing_else()
    {
        Assert.Equal("d-42.down.gitberth.invalid", MarkerHosts.Down(42));
        Assert.Equal(new Marker(MarkerKind.Down, 42), MarkerHosts.Parse("d-42.down.gitberth.invalid"));
        Assert.Equal(new Marker(MarkerKind.Down, 42), MarkerHosts.Parse("D-42.DOWN.Gitberth.Invalid:8080"));
        Assert.Equal(new Marker(MarkerKind.Status, null), MarkerHosts.Parse("status.gitberth.invalid."));
        foreach (var other in new[] { "d-0.down.gitberth.invalid", "d-042.down.gitberth.invalid", "d--1.down.gitberth.invalid", "d-1x.down.gitberth.invalid",
                     "x.gitberth.invalid", "gitberth.invalid", "d-1.down.gitberth.invalid.evil.com", "app.example.com", "", "localhost:8080" })
            Assert.Null(MarkerHosts.Parse(other));
        Assert.True(MarkerHosts.IsMarker("x.gitberth.invalid"));
        Assert.True(MarkerHosts.IsMarker("gitberth.invalid"));
        Assert.False(MarkerHosts.IsMarker("gitberth.invalid.example.com"));
        Assert.False(MarkerHosts.IsMarker("notgitberth.invalid"));
        Assert.False(MarkerHosts.IsMarker(null));
    }

    [Fact]
    public void A_domain_on_the_down_page_goes_to_gitberth_with_its_marker_and_keeps_its_access_check()
    {
        var access = TunnelRoutes.AccessBlock("acme", "aud1");
        var ingress = new JsonArray(
            new JsonObject { ["hostname"] = "web.example.com", ["service"] = AppService, ["originRequest"] = new JsonObject { ["access"] = access.DeepClone(), ["connectTimeout"] = "10s" } },
            new JsonObject { ["service"] = "http_status:404" });

        var plan = TunnelRoutes.Plan(ingress, [Web], [Down("web.example.com", 7)], []);

        var rule = Assert.Single(RulesFor(plan.Ingress, "web.example.com"));
        Assert.Equal(Own, (string?)rule["service"]);
        Assert.Equal("d-7.down.gitberth.invalid", Marker(rule));
        Assert.True(JsonNode.DeepEquals(access, rule["originRequest"]!["access"]));
        Assert.Equal("10s", (string?)rule["originRequest"]!["connectTimeout"]);
        Assert.True(TunnelRoutes.IsPublicPage(rule));
        Assert.True(TunnelRoutes.ShowsDownPage(plan.Ingress, "web.example.com"));
        Assert.Contains(plan.Notes, n => n.Text == "web.example.com shows Gitberth's down page now");
    }

    [Fact]
    public void Back_from_the_down_page_the_marker_comes_off_and_every_other_setting_stays()
    {
        var ingress = new JsonArray(
            new JsonObject
            {
                ["hostname"] = "web.example.com", ["service"] = Own,
                ["originRequest"] = new JsonObject { ["httpHostHeader"] = MarkerHosts.Down(7), ["access"] = TunnelRoutes.AccessBlock("acme", "aud1"), ["noTLSVerify"] = true },
            },
            new JsonObject { ["service"] = "http_status:404" });

        // The down rule is Gitberth's through the service it remembers (where it sent the domain before): it's re-pointed.
        var plan = TunnelRoutes.Plan(ingress, [Web], [Up("web.example.com")], []);

        var rule = Assert.Single(RulesFor(plan.Ingress, "web.example.com"));
        Assert.Equal(AppService, (string?)rule["service"]);
        Assert.Null(Marker(rule));
        Assert.NotNull(rule["originRequest"]!["access"]);
        Assert.True((bool)rule["originRequest"]!["noTLSVerify"]!);
        Assert.False(TunnelRoutes.ShowsDownPage(plan.Ingress, "web.example.com"));
        Assert.Contains(plan.Notes, n => n.Text == "web.example.com goes to web again");
        Assert.DoesNotContain(plan.Notes, n => n.Text.StartsWith("Pointed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_marker_comes_off_even_when_the_rule_already_goes_to_the_app_and_a_host_header_of_someone_elses_stays()
    {
        // Left behind by an older sync: the right service, still marked. And another host whose owner set a host header of their own.
        var ingress = Ingress($$"""
            [
              { "hostname": "web.example.com", "service": "{{AppService}}", "originRequest": { "httpHostHeader": "d-7.down.gitberth.invalid" } },
              { "hostname": "www.example.com", "service": "{{AppService}}", "originRequest": { "httpHostHeader": "intranet.local" } },
              { "service": "http_status:404" }
            ]
            """);

        var plan = TunnelRoutes.Plan(ingress, [Web], [Up("web.example.com", previous: AppService), Up("www.example.com", previous: AppService)], []);

        Assert.True(plan.Changed);
        var web = Assert.Single(RulesFor(plan.Ingress, "web.example.com"));
        Assert.Null(web["originRequest"]);
        var www = Assert.Single(RulesFor(plan.Ingress, "www.example.com"));
        Assert.Equal("intranet.local", Marker(www));
    }

    [Fact]
    public void A_host_header_the_owner_set_on_a_domains_rules_to_its_app_is_found_and_a_marker_isnt_one()
    {
        var ingress = Ingress($$"""
            [
              { "hostname": "web.example.com", "path": "^/api", "service": "{{AppService}}", "originRequest": { "httpHostHeader": "intranet.local" } },
              { "hostname": "web.example.com", "service": "{{AppService}}" },
              { "hostname": "www.example.com", "service": "{{Own}}", "originRequest": { "httpHostHeader": "d-8.down.gitberth.invalid" } },
              { "hostname": "api.example.com", "service": "http://other:1", "originRequest": { "httpHostHeader": "theirs.local" } },
              { "service": "http_status:404" }
            ]
            """);

        // On any of the domain's rules to the app, a path rule's too: the down page would replace it, and couldn't give it back.
        Assert.Equal("intranet.local", TunnelRoutes.OwnersHostHeader(ingress, "web.example.com", Web, AppService));
        // The down page's own marker isn't the owner's, and neither is one on a rule to anything but the app.
        Assert.Null(TunnelRoutes.OwnersHostHeader(ingress, "www.example.com", Web, AppService, Own));
        Assert.Null(TunnelRoutes.OwnersHostHeader(ingress, "api.example.com", Web, AppService));
        // Where the app is isn't known (it never ran, and has no web port the tunnel could reach): no rule is the app's.
        Assert.Null(TunnelRoutes.OwnersHostHeader(ingress, "web.example.com", Web, (string?)null));
    }

    [Fact]
    public void A_sync_with_the_down_page_already_in_place_changes_nothing()
    {
        var once = TunnelRoutes.Plan(Ingress($$"""[{ "hostname": "web.example.com", "service": "{{AppService}}" }, { "service": "http_status:404" }]"""),
            [Web], [Down("web.example.com", 7)], []);
        var again = TunnelRoutes.Plan(once.Ingress, [Web], [Down("web.example.com", 7, previous: Own)], []);

        Assert.True(once.Changed);
        Assert.False(again.Changed);
        Assert.Empty(again.Notes);
    }

    [Fact]
    public void A_domain_kept_on_its_paths_shows_the_down_page_on_those_paths_only_and_comes_back_the_same()
    {
        var ingress = Ingress($$"""
            [
              { "hostname": "web.example.com", "path": "^/app", "service": "{{AppService}}" },
              { "hostname": "web.example.com", "service": "http://other:9000" },
              { "service": "http_status:404" }
            ]
            """);

        var down = TunnelRoutes.Plan(ingress, [Web], [Down("web.example.com", 7, keepPaths: true)], []);
        var rules = RulesFor(down.Ingress, "web.example.com");
        Assert.Equal(("^/app", Own, "d-7.down.gitberth.invalid"), ((string?)rules[0]["path"], (string?)rules[0]["service"], Marker(rules[0])));
        // Someone else's rule on the host is never touched.
        Assert.Equal(("http://other:9000", (string?)null), ((string?)rules[1]["service"], Marker(rules[1])));

        var back = TunnelRoutes.Plan(down.Ingress, [Web], [Up("web.example.com", keepPaths: true)], []);
        Assert.True(JsonNode.DeepEquals(ingress, back.Ingress));
    }

    [Fact]
    public void A_held_back_domain_shows_only_the_down_page_without_an_access_check_and_isnt_counted_as_served_open()
    {
        var ingress = Ingress($$"""
            [
              { "hostname": "web.example.com", "service": "{{AppService}}", "originRequest": { "access": { "required": true, "teamName": "acme", "audTag": ["aud1"] } } },
              { "service": "http_status:404" }
            ]
            """);

        var plan = TunnelRoutes.Plan(ingress, [Web], [Down("web.example.com", 7, bare: true)], []);

        var rule = Assert.Single(RulesFor(plan.Ingress, "web.example.com"));
        Assert.Equal(Own, (string?)rule["service"]);
        Assert.Equal(new JsonObject { ["httpHostHeader"] = "d-7.down.gitberth.invalid" }.ToJsonString(), rule["originRequest"]!.ToJsonString());
        // It reveals nothing of the app: never "served without Cloudflare Access".
        Assert.False(TunnelRoutes.ServesWithoutAccess(plan.Ingress, "web.example.com"));
        Assert.False(TunnelRoutes.OpenTo(plan.Ingress, "web.example.com", Web, AppService, Own));
        Assert.False(TunnelRoutes.HasAppRules(plan.Ingress, "web.example.com"));
        // The app's own rule without Access would be.
        Assert.True(TunnelRoutes.ServesWithoutAccess(Ingress($$"""[{ "hostname": "web.example.com", "service": "{{AppService}}" }]"""), "web.example.com"));
    }

    [Fact]
    public void Access_goes_on_a_protected_domains_down_page_as_on_its_app()
    {
        var ingress = Ingress($$"""[{ "hostname": "web.example.com", "service": "{{Own}}", "originRequest": { "httpHostHeader": "d-7.down.gitberth.invalid" } }, { "service": "http_status:404" }]""");
        var protect = new Dictionary<string, TunnelRoutes.Protection>(StringComparer.OrdinalIgnoreCase)
        {
            ["web.example.com"] = new(TunnelRoutes.AccessBlock("acme", "aud1"), [AppService, Own]),
        };

        var guarded = TunnelRoutes.ApplyAccess(ingress, protect, new HashSet<string>(), [Web]);

        var rule = Assert.Single(RulesFor(guarded.Ingress, "web.example.com"));
        Assert.Equal("d-7.down.gitberth.invalid", Marker(rule));
        Assert.NotNull(rule["originRequest"]!["access"]);
        Assert.True(TunnelRoutes.BehindAccess(guarded.Ingress, "web.example.com"));
    }

    [Fact]
    public void Where_a_stopped_apps_domains_would_go_comes_from_its_saved_web_port()
    {
        var host = new TunnelInfo("tunnel", "a", "t", HostNetwork: true, OnAppNetwork: false);
        var shared = host with { HostNetwork = false, OnAppNetwork = true };
        Assert.Equal("http://localhost:3001", TunnelRoutes.ExpectedService(Web, host));
        Assert.Equal("http://web:3000", TunnelRoutes.ExpectedService(Web, shared));
        var hostOnly = new AppDefinition { Slug = "web", Ports = [new PortMapping { ContainerPort = 3000, HostPort = 3005, Access = Network.PortAccess.HostOnly, IsWeb = true }] };
        Assert.Equal("http://127.0.0.1:3005", TunnelRoutes.ExpectedService(hostOnly, host));
        var internalOnly = new AppDefinition { Slug = "web", Ports = [new PortMapping { ContainerPort = 3000, Access = Network.PortAccess.Internal, IsWeb = true }] };
        Assert.Null(TunnelRoutes.ExpectedService(internalOnly, host));
        Assert.Null(TunnelRoutes.ExpectedService(new AppDefinition { Slug = "worker" }, host));
    }

    [Fact]
    public void The_status_page_takes_its_whole_host_and_leaves_with_it()
    {
        var ingress = Ingress("""[{ "hostname": "*.example.com", "service": "http://wild:1" }, { "service": "http_status:404" }]""");

        var plan = TunnelRoutes.PlanStatus(ingress, "status.example.com", Own, []);

        var rule = Assert.Single(RulesFor(plan.Ingress, "status.example.com"));
        Assert.Equal((Own, MarkerHosts.Status), ((string?)rule["service"], Marker(rule)));
        // Ahead of the wildcard that would catch it.
        Assert.Equal("status.example.com", (string?)plan.Ingress[0]!["hostname"]);
        Assert.False(TunnelRoutes.PlanStatus(plan.Ingress, "status.example.com", Own, []).Changed);
        // Moved to another host: the old one's rule goes, someone else's rules there stay.
        var withTheirs = new JsonArray([.. plan.Ingress.Select(r => r!.DeepClone())]);
        withTheirs.Insert(1, new JsonObject { ["hostname"] = "status.example.com", ["path"] = "^/x", ["service"] = "http://theirs:1" });
        var moved = TunnelRoutes.PlanStatus(withTheirs, "uptime.example.com", Own, ["status.example.com"]);
        Assert.Equal(["^/x"], RulesFor(moved.Ingress, "status.example.com").Select(r => (string?)r["path"]));
        Assert.Single(RulesFor(moved.Ingress, "uptime.example.com"));
        // Without Gitberth's own service (cloudflared can't reach it), nothing changes.
        Assert.False(TunnelRoutes.PlanStatus(ingress, "status.example.com", null, []).Changed);
    }

    /// <summary>
    /// The invariant, over every way a domain moves (to the down page and back, held back, kept on its paths, with Access,
    /// next to GitHub's webhook host and the status page): every rule whose service is Gitberth's own carries the webhook
    /// path or a marker host header, and no rule that goes to an app carries a marker.
    /// </summary>
    [Fact]
    public void Every_rule_to_gitberth_carries_the_webhook_path_or_a_marker_and_no_rule_to_an_app_keeps_one()
    {
        var hosts = new[] { "a.example.com", "b.example.com", "c.example.com" };
        var ids = new Dictionary<string, int> { ["a.example.com"] = 1, ["b.example.com"] = 2, ["c.example.com"] = 3 };
        var states = new[] { "up", "down", "held", "up", "down", "up" };
        var start = Ingress($$"""
            [
              { "hostname": "a.example.com", "service": "{{AppService}}" },
              { "hostname": "b.example.com", "path": "^/api", "service": "{{AppService}}" },
              { "hostname": "c.example.com", "service": "{{AppService}}", "originRequest": { "access": { "required": true, "teamName": "acme", "audTag": ["aud1"] } } },
              { "hostname": "other.example.com", "service": "http://other:1", "originRequest": { "httpHostHeader": "intranet.local" } },
              { "service": "http_status:404" }
            ]
            """);
        foreach (var order in new[] { states, states.Reverse().ToArray(), ["down", "down", "up", "held", "held", "up"] })
        {
            var ingress = start;
            var remembered = hosts.ToDictionary(h => h, _ => (string?)AppService);
            foreach (var state in order)
            {
                var routes = hosts.Select(h => state switch
                {
                    "up" => new DomainRoute(h, Web.Id, AppService, remembered[h], KeepPaths: h == "b.example.com"),
                    "down" => new DomainRoute(h, Web.Id, Own, remembered[h], h == "b.example.com", MarkerHosts.Down(ids[h])),
                    _ => new DomainRoute(h, Web.Id, Own, remembered[h], h == "b.example.com", MarkerHosts.Down(ids[h]), Bare: true),
                }).ToList();
                var plan = TunnelRoutes.Plan(ingress, [Web], routes, []);
                var hooked = TunnelRoutes.PlanWebhook(plan.Ingress, "hooks.example.com", Own, []);
                var statused = TunnelRoutes.PlanStatus(hooked.Ingress, "status.example.com", Own, []);
                ingress = statused.Ingress;
                foreach (var h in hosts) remembered[h] = TunnelRoutes.ServiceOf(ingress, h, routes.First(r => r.Hostname == h).Service, Web) ?? remembered[h];

                foreach (var rule in ingress.OfType<JsonObject>())
                {
                    var service = (string?)rule["service"];
                    var path = (string?)rule["path"];
                    if (TunnelRoutes.SameService(service, Own))
                        Assert.True(path is TunnelRoutes.WebhookPath || MarkerHosts.IsMarker(Marker(rule)), $"{state}: {rule.ToJsonString()} goes to Gitberth unmarked");
                    if (TunnelRoutes.SameService(service, AppService, Web))
                        Assert.False(MarkerHosts.IsMarker(Marker(rule)), $"{state}: {rule.ToJsonString()} goes to the app with a marker");
                }
                // Each domain is where it should be, and nobody else's rule changed.
                foreach (var h in hosts) Assert.Equal(state != "up", TunnelRoutes.ShowsDownPage(ingress, h) || (h == "b.example.com" && RulesFor(ingress, h).All(r => TunnelRoutes.IsPublicPage(r))));
                Assert.Equal("intranet.local", Marker(RulesFor(ingress, "other.example.com").Single()));
                if (state == "held") Assert.All(hosts, h => Assert.All(RulesFor(ingress, h), r => Assert.Null(r["originRequest"]?["access"])));
            }
        }
    }
}
