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

/// <summary>
/// Deployment protection (Cloudflare Access in front of a domain) and the route GitHub's webhook takes to Gitberth: the
/// tunnel rules they need, the Access requests, and a sync through a stand-in Cloudflare and docker. Nothing here reaches
/// Cloudflare.
/// </summary>
public sealed class DeploymentProtectionTests : IDisposable
{
    private const string Hostname = "app.example.com";
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string TunnelId = "11111111-2222-4333-8444-555555555555";

    private static JsonArray Ingress(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static readonly AppDefinition Web = new() { Id = 1, Name = "Web", Slug = "web", Ports = [new PortMapping { Id = 1, ContainerPort = 3000, HostPort = 3001, IsWeb = true }] };

    // ---- The webhook's route ----

    [Fact]
    public void The_webhook_gets_only_its_path_on_its_host_and_a_404_for_the_rest()
    {
        var plan = TunnelRoutes.PlanWebhook(Ingress("""[{"hostname":"app.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]"""),
            "hooks.example.com", "http://localhost:8080", []);

        Assert.True(plan.Changed);
        Assert.Equal(
            """[{"hostname":"hooks.example.com","path":"^/hooks/github$","service":"http://localhost:8080"},{"hostname":"hooks.example.com","service":"http_status:404"},{"hostname":"app.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.Contains(plan.Notes, n => n.Text == "Routed https://hooks.example.com/hooks/github to Gitberth for GitHub's webhooks");
        // Never behind Access, and nothing to change the second time.
        Assert.DoesNotContain("originRequest", plan.Ingress.ToJsonString());
        Assert.False(TunnelRoutes.PlanWebhook(plan.Ingress, "hooks.example.com", "http://localhost:8080", []).Changed);
    }

    [Fact]
    public void The_webhook_follows_gitberths_port_and_leaves_its_old_host()
    {
        var first = TunnelRoutes.PlanWebhook(Ingress("""[{"service":"http_status:404"}]"""), "hooks.example.com", "http://localhost:8080", []).Ingress;
        var moved = TunnelRoutes.PlanWebhook(first, "hooks.example.com", "http://localhost:8090", []);
        Assert.Contains("http://localhost:8090", moved.Ingress.ToJsonString());
        Assert.DoesNotContain("http://localhost:8080", moved.Ingress.ToJsonString());

        var gone = TunnelRoutes.PlanWebhook(moved.Ingress, "gh.example.com", "http://localhost:8090", ["hooks.example.com"]);
        Assert.DoesNotContain("hooks.example.com", gone.Ingress.ToJsonString());
        Assert.Contains("gh.example.com", gone.Ingress.ToJsonString());
        Assert.Contains(gone.Notes, n => n.Text == "Removed the route GitHub's webhook took through hooks.example.com");
    }

    [Fact]
    public void Someone_elses_rules_on_the_host_stay_and_get_no_404_ahead_of_them()
    {
        var plan = TunnelRoutes.PlanWebhook(Ingress("""[{"hostname":"hooks.example.com","service":"http://localhost:9000"},{"service":"http_status:404"}]"""),
            "hooks.example.com", "http://localhost:8080", []);
        Assert.Equal(
            """[{"hostname":"hooks.example.com","path":"^/hooks/github$","service":"http://localhost:8080"},{"hostname":"hooks.example.com","service":"http://localhost:9000"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.True(TunnelRoutes.UsedByOthers(plan.Ingress, "hooks.example.com"));
        Assert.False(TunnelRoutes.UsedByOthers(TunnelRoutes.PlanWebhook(Ingress("[]"), "hooks.example.com", "http://localhost:8080", []).Ingress, "hooks.example.com"));
    }

    [Fact]
    public void Without_a_way_to_gitberth_the_hosts_rules_stay_as_they_are()
    {
        var current = Ingress("""[{"hostname":"app.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]""");
        Assert.False(TunnelRoutes.PlanWebhook(current, "hooks.example.com", null, []).Changed);
    }

    [Theory]
    [InlineData(null, null, 8080)]
    [InlineData("5000", null, 5000)]
    [InlineData("8081;8082", null, 8081)]
    [InlineData(null, "http://+:5005;https://+:5006", 5005)]
    public void Gitberths_own_port_comes_from_aspnetcores_settings(string? ports, string? urls, int expected) =>
        Assert.Equal(expected, DomainService.OwnPort(ports, urls));

    [Fact]
    public void The_tunnel_reaches_gitberth_by_its_published_port_or_its_name()
    {
        var onHost = new TunnelInfo("tunnel", AccountId, TunnelId, HostNetwork: true, OnAppNetwork: false);
        var onShared = onHost with { HostNetwork = false, OnAppNetwork = true };
        var inspect = """[{"Name":"/gitberth","HostConfig":{"NetworkMode":"gitberth_default"},"NetworkSettings":{"Ports":{"8080/tcp":[{"HostIp":"0.0.0.0","HostPort":"8090"},{"HostIp":"::","HostPort":"8090"}]}}}]""";

        Assert.Equal("http://localhost:8090", DomainService.OwnService(inspect, 8080, onHost));
        Assert.Equal("http://gitberth:8080", DomainService.OwnService(inspect, 8080, onShared));
        Assert.Null(DomainService.OwnService("""[{"Name":"/gitberth","NetworkSettings":{"Ports":{}}}]""", 8080, onHost));
        Assert.Null(DomainService.OwnService(inspect, 8080, onShared with { OnAppNetwork = false }));
        // Gitberth on the host itself.
        Assert.Equal("http://localhost:5000", DomainService.OwnService(null, 5000, onHost));
        Assert.Null(DomainService.OwnService(null, 5000, onShared));
    }

    // ---- Access on a domain's rules ----

    private static TunnelRoutes.Protection Guard(string aud) => new(TunnelRoutes.AccessBlock("acme", aud), ["http://localhost:3001"]);

    [Fact]
    public void A_protected_domains_own_rules_get_the_access_check_and_nothing_else_does()
    {
        var current = Ingress("""
            [{"hostname":"app.example.com","path":"^/api","service":"http://localhost:9000"},
             {"hostname":"app.example.com","service":"http://localhost:3001","originRequest":{"connectTimeout":"10s"}},
             {"hostname":"other.example.com","service":"http://localhost:3002"},
             {"service":"http_status:404"}]
            """);
        var plan = TunnelRoutes.ApplyAccess(current, new Dictionary<string, TunnelRoutes.Protection> { [Hostname] = Guard("aud1") }, new HashSet<string>(), [Web]);

        Assert.True(plan.Changed);
        var rules = plan.Ingress.OfType<JsonObject>().ToList();
        Assert.Null(rules[0]["originRequest"]);
        Assert.Equal("""{"connectTimeout":"10s","access":{"required":true,"teamName":"acme","audTag":["aud1"]}}""", rules[1]["originRequest"]!.ToJsonString());
        Assert.Null(rules[2]["originRequest"]);
        Assert.Contains(plan.Notes, n => n.Text == "app.example.com is behind Cloudflare Access now: visitors sign in first");
        Assert.False(TunnelRoutes.ApplyAccess(plan.Ingress, new Dictionary<string, TunnelRoutes.Protection> { [Hostname] = Guard("aud1") }, new HashSet<string>(), [Web]).Changed);
    }

    [Fact]
    public void Only_the_access_check_of_an_application_gitberth_made_is_taken_off()
    {
        var current = Ingress("""
            [{"hostname":"app.example.com","service":"http://localhost:3001","originRequest":{"access":{"required":true,"teamName":"acme","audTag":["aud1"]}}},
             {"hostname":"mine.example.com","service":"http://localhost:3005","originRequest":{"access":{"required":true,"teamName":"acme","audTag":["their-own"]}}},
             {"service":"http_status:404"}]
            """);
        var plan = TunnelRoutes.ApplyAccess(current, new Dictionary<string, TunnelRoutes.Protection>(), new HashSet<string> { "aud1" }, [Web]);
        var rules = plan.Ingress.OfType<JsonObject>().ToList();
        Assert.Null(rules[0]["originRequest"]);
        Assert.NotNull(rules[1]["originRequest"]);
        Assert.False(TunnelRoutes.BehindAccess(plan.Ingress, Hostname));
        Assert.True(TunnelRoutes.BehindAccess(current, Hostname));
        Assert.Contains(plan.Notes, n => n.Text == "app.example.com isn't behind Cloudflare Access any more");
    }

    // ---- Access's API ----

    [Fact]
    public void The_access_application_lets_in_the_listed_emails_with_cloudflares_default_sign_in()
    {
        var body = CloudflareApi.AccessAppBody(Hostname, ["ann@example.com", "bob@example.com"]);
        Assert.Equal("Gitberth: app.example.com", body["name"]!.GetValue<string>());
        Assert.Equal(Hostname, body["domain"]!.GetValue<string>());
        Assert.Equal("self_hosted", body["type"]!.GetValue<string>());
        // No identity providers named: every one the account has, the one-time code by email by default.
        Assert.Null(body["allowed_idps"]);
        var policy = Assert.Single(body["policies"]!.AsArray())!;
        Assert.Equal("allow", policy["decision"]!.GetValue<string>());
        Assert.Equal("""[{"email":{"email":"ann@example.com"}},{"email":{"email":"bob@example.com"}}]""", policy["include"]!.ToJsonString());

        var parsed = CloudflareApi.ParseAccessApp(new JsonObject
        {
            ["id"] = "a1", ["name"] = "Gitberth: app.example.com", ["domain"] = "App.Example.com", ["aud"] = "aud1",
            ["policies"] = JsonNode.Parse("""[{"decision":"allow","include":[{"email":{"email":"Bob@example.com"}},{"email":{"email":"ann@example.com"}}]},{"decision":"deny","include":[{"email":{"email":"eve@example.com"}}]}]"""),
        })!;
        Assert.Equal(("a1", Hostname, "aud1"), (parsed.Id, parsed.Domain, parsed.Aud));
        Assert.Equal(["ann@example.com", "bob@example.com"], parsed.Emails!);
        Assert.Null(CloudflareApi.ParseAccessApp(new JsonObject { ["id"] = "a2", ["name"] = "x" })!.Emails);
    }

    [Fact]
    public void A_refused_access_call_names_the_access_permission()
    {
        var text = CloudflareApi.Explain(HttpStatusCode.Forbidden, null, "list the account's Cloudflare Access applications",
            CloudflareApi.Permissions + ", plus " + CloudflareApi.AccessPermission + " to protect domains");
        Assert.Contains("Account › Access: Apps and Policies › Edit", text);
        // Everything else keeps the permissions it always named.
        Assert.DoesNotContain("Access", CloudflareApi.Explain(HttpStatusCode.Forbidden, null, "read the tunnel's routes"));
    }

    [Theory]
    [InlineData("ann@example.com\nBob@Example.com\n\nann@example.com", new[] { "ann@example.com", "bob@example.com" }, null)]
    [InlineData("ann@example.com, bob@example.com", new[] { "ann@example.com", "bob@example.com" }, null)]
    [InlineData("", new string[0], null)]
    [InlineData("ann@example.com\nnot-an-email", new[] { "ann@example.com" }, "\"not-an-email\" isn't an email address.")]
    public void Emails_are_one_per_line_lower_case_and_each_once(string text, string[] emails, string? problem)
    {
        var parsed = DomainService.ParseEmails(text);
        Assert.Equal(emails, parsed.Emails);
        Assert.Equal(problem, parsed.Problem);
    }

    [Fact]
    public void The_team_name_is_the_first_label_of_the_team_domain() => Assert.Equal("acme", DomainService.TeamName("acme.cloudflareaccess.com"));

    // ---- A sync through a stand-in Cloudflare ----

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>A Cloudflare with a tunnel, the zone example.com, Zero Trust (team acme) and its Access applications.</summary>
    private sealed class Cloudflare : HttpMessageHandler
    {
        private readonly List<string> _calls = [];
        public JsonArray Ingress { get; set; } = (JsonArray)JsonNode.Parse("""[{"hostname":"app.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]""")!;
        public List<JsonObject> Apps { get; } = [];
        public string? TeamDomain { get; set; } = "acme.cloudflareaccess.com";
        public int Puts { get; private set; }
        private int _next = 1;

        public List<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        private static HttpResponseMessage Ok(JsonNode? result) =>
            new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["success"] = true, ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (path, query) = (request.RequestUri!.AbsolutePath, request.RequestUri.Query);
            lock (_calls) _calls.Add($"{request.Method} {path}");
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (path.EndsWith("/configurations", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Put)
                {
                    Ingress = (JsonArray)body!["config"]!["ingress"]!.DeepClone();
                    Puts++;
                    return Ok(new JsonObject { ["version"] = Puts });
                }
                return Ok(new JsonObject { ["config"] = new JsonObject { ["ingress"] = Ingress.DeepClone() } });
            }
            if (path.EndsWith("/zones", StringComparison.Ordinal))
                return Ok(JsonNode.Parse(query.Contains("name=example.com&", StringComparison.Ordinal) ? """[{"id":"z1","name":"example.com"}]""" : "[]"));
            if (path.EndsWith("/zones/z1/dns_records", StringComparison.Ordinal))
                return request.Method == HttpMethod.Post ? Ok(new JsonObject { ["id"] = "r-new" })
                    : Ok(query.Contains("name=app.example.com&", StringComparison.Ordinal)
                        ? JsonNode.Parse($$"""[{"id":"r1","type":"CNAME","name":"app.example.com","content":"{{TunnelId}}.cfargotunnel.com","proxied":true}]""")
                        : new JsonArray());
            if (path.EndsWith("/access/organizations", StringComparison.Ordinal))
                return TeamDomain is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(new JsonObject { ["auth_domain"] = TeamDomain });
            if (path.EndsWith("/access/apps", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get) return Ok(new JsonArray([.. Apps.Select(a => (JsonNode?)a.DeepClone())]));
                var made = (JsonObject)body!.DeepClone();
                made["id"] = $"a{_next}";
                made["aud"] = $"aud{_next++}";
                Apps.Add(made);
                return Ok(made.DeepClone());
            }
            if (path.Contains("/access/apps/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                var app = Apps.FirstOrDefault(a => a["id"]!.GetValue<string>() == id);
                if (app is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Delete)
                {
                    Apps.Remove(app);
                    return Ok(new JsonObject { ["id"] = id });
                }
                if (request.Method == HttpMethod.Put)
                {
                    foreach (var (key, value) in body!.AsObject().ToList()) app[key] = value?.DeepClone();
                    return Ok(app.DeepClone());
                }
                return Ok(app.DeepClone());
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly Cloudflare _cloudflare = new();
    private readonly DomainService _domains;
    private readonly int _appId;

    public DeploymentProtectionTests()
    {
        Directory.CreateDirectory(_dir);
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "acme", Repo = "web" };
            app.Ports.Add(new PortMapping { HostPort = 3001, ContainerPort = 3000, IsWeb = true });
            app.Domains.Add(new CustomDomain { Hostname = Hostname, Service = "http://localhost:3001" });
            db.Apps.Add(app);
            db.SaveChanges();
            _appId = app.Id;
        }
        _settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var docker = new DockerCli(new GitberthOptions { DataDir = _dir, DockerPath = WriteFakeDocker(_dir) });
        _domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance);
        _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token").GetAwaiter().GetResult();
        _settings.SetAsync(SettingKeys.DomainsEnabled, "true").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A docker that lists a running cloudflared on the host's network (run with a tunnel token) and the app's container.</summary>
    private static string WriteFakeDocker(string dir)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c2VjcmV0"}"""));
        string[] ps =
        [
            """{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""",
            """{"Names":"gb-web","State":"running","Status":"Up 1 hour","Image":"gitberth/web:1","Ports":"0.0.0.0:3001->3000/tcp","Labels":"gitberth.managed=true","Command":"node server.js"}""",
        ];
        var inspect = """[{"Id":"t1","Name":"/tunnel","Args":["tunnel","run","--token","TOKEN"],"Config":{"Cmd":["tunnel","run","--token","TOKEN"],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{"host":{}}}}]"""
            .Replace("TOKEN", token, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, "docker.cmd");
            File.WriteAllLines(cmd,
            [
                "@echo off",
                "if \"%~1\"==\"ps\" (",
                .. ps.Select(line => "echo " + line),
                "exit /b 0",
                ")",
                "if \"%~1\"==\"container\" (",
                "echo " + inspect,
                "exit /b 0",
                ")",
                "exit /b 1",
            ]);
            return cmd;
        }
        var sh = Path.Combine(dir, "docker");
        File.WriteAllLines(sh,
        [
            "#!/bin/sh",
            "case \"$1\" in",
            "ps)",
            .. ps.Select(line => $"echo '{line}'"),
            ";;",
            "container)",
            $"echo '{inspect}'",
            ";;",
            "*) exit 1 ;;",
            "esac",
        ]);
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private void Protect(bool on = true, string? accessAppId = null)
    {
        using var db = _db.CreateDbContext();
        var domain = db.Domains.Single();
        (domain.Protected, domain.AccessAppId) = (on, accessAppId);
        db.SaveChanges();
    }

    private CustomDomain Domain()
    {
        using var db = _db.CreateDbContext();
        return db.Domains.AsNoTracking().Single();
    }

    private JsonObject RuleFor(string hostname) => _cloudflare.Ingress.OfType<JsonObject>().Single(r => r["hostname"]?.GetValue<string>() == hostname && r["path"] is null);

    [Fact]
    public async Task Nothing_asks_access_while_no_domain_is_protected()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        var report = await _domains.SyncAsync();
        Assert.DoesNotContain(report.Notes, n => n.Problem);
        Assert.DoesNotContain(_cloudflare.Calls, c => c.Contains("/access/", StringComparison.Ordinal));
        Assert.Equal(0, _cloudflare.Puts);
    }

    [Fact]
    public async Task A_protected_domain_gets_its_access_application_and_the_tunnel_checks_the_sign_in()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com\nbob@example.com");
        Protect();

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(report.Notes, n => n.Problem);
        var made = Assert.Single(_cloudflare.Apps);
        Assert.Equal("Gitberth: app.example.com", made["name"]!.GetValue<string>());
        Assert.Equal("""{"access":{"required":true,"teamName":"acme","audTag":["aud1"]}}""", RuleFor(Hostname)["originRequest"]!.ToJsonString());
        Assert.Equal("a1", Domain().AccessAppId);
        Assert.Contains(report.Notes, n => n.Text == "Made the Cloudflare Access application for app.example.com");

        // Nothing more to do the next time; a new list of people updates the application in place.
        var calls = _cloudflare.Calls.Count;
        await _domains.SyncAsync();
        Assert.DoesNotContain(_cloudflare.Calls.Skip(calls), c => c.StartsWith("POST", StringComparison.Ordinal) || c.StartsWith("PUT", StringComparison.Ordinal));
        var (error, saved) = await _domains.SaveProtectionEmailsAsync("carol@example.com");
        Assert.Null(error);
        Assert.Contains(saved.Notes, n => n.Text == "Updated who may sign in to app.example.com");
        Assert.Equal("""[{"email":{"email":"carol@example.com"}}]""", Assert.Single(_cloudflare.Apps)["policies"]![0]!["include"]!.ToJsonString());
    }

    [Fact]
    public async Task The_overview_says_a_protected_domain_is_behind_access()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        var before = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(before.Working);
        Assert.True(before.Protected);

        await _domains.SyncAsync();
        var overview = await _domains.GetOverviewAsync(_appId);
        var after = Assert.Single(overview.Domains);
        Assert.True(after.Working, after.Summary);
        Assert.EndsWith("behind Cloudflare Access", after.Summary);
        Assert.Null(overview.ProtectionProblem);
    }

    [Fact]
    public async Task A_protected_domain_nobody_may_sign_in_to_is_not_served_at_all()
    {
        Protect();

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        Assert.Contains(report.Notes, n => n.Problem && n.Hostname == Hostname && n.Text.Contains("nobody is allowed in yet", StringComparison.Ordinal));
        Assert.Empty(_cloudflare.Apps);
        var overview = await _domains.GetOverviewAsync(_appId);
        Assert.NotNull(overview.ProtectionProblem);
    }

    [Fact]
    public async Task Without_zero_trust_a_protected_domain_waits_and_says_why()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        _cloudflare.TeamDomain = null;
        Protect();

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        Assert.Contains(report.Notes, n => n.Problem && n.Text.Contains("Zero Trust isn't set up", StringComparison.Ordinal));
        var status = await _domains.ProtectionAsync();
        Assert.False(status.Ready);
        Assert.Contains("Zero Trust isn't set up", status.Problem);
        // Another domain couldn't be protected either, and the switch says why.
        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: true);
        Assert.Contains("Zero Trust isn't set up", error);
    }

    [Fact]
    public async Task Taking_protection_off_removes_the_check_then_the_application_gitberth_made()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // Someone else's application for another name stays.
        _cloudflare.Apps.Add(new JsonObject { ["id"] = "x9", ["name"] = "Team wiki", ["domain"] = "wiki.example.com", ["aud"] = "audx" });

        var (error, report) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        Assert.Null(error);
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Equal("x9", Assert.Single(_cloudflare.Apps)["id"]!.GetValue<string>());
        Assert.Null(Domain().AccessAppId);
        Assert.Contains(report.Notes, n => n.Text == "Removed the Cloudflare Access application Gitberth made for app.example.com");
        var calls = _cloudflare.Calls;
        Assert.True(calls.LastIndexOf($"PUT /client/v4/accounts/{AccountId}/cfd_tunnel/{TunnelId}/configurations") < calls.IndexOf($"DELETE /client/v4/accounts/{AccountId}/access/apps/a1"),
            "The tunnel stops checking for the application before it goes.");
    }

    [Fact]
    public async Task A_removed_domains_access_application_goes_with_it()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();

        await _domains.RemoveAsync(_appId, Hostname);

        Assert.Empty(_cloudflare.Apps);
        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
    }

    [Fact]
    public async Task Protecting_needs_protection_set_up_first()
    {
        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: true);
        Assert.Contains("Settings › Domains › Protection", error);
        Assert.False(Domain().Protected);

        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: true);
        Assert.Null(error);
        Assert.True(Domain().Protected);
        Assert.Equal("a1", Domain().AccessAppId);
    }

    [Fact]
    public async Task Who_may_sign_in_is_checked_and_never_left_empty_while_a_domain_is_protected()
    {
        Assert.Equal("\"nobody\" isn't an email address.", (await _domains.SaveProtectionEmailsAsync("nobody")).Error);
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        Assert.Contains("Turn protection off on those domains first", (await _domains.SaveProtectionEmailsAsync("  ")).Error);
        Assert.Equal("ann@example.com", await _settings.GetAsync(SettingKeys.ProtectionEmails));
    }

    // ---- The webhook's host name ----

    [Fact]
    public async Task The_webhook_host_routes_its_path_to_gitberth_with_a_dns_record_and_a_secret()
    {
        var (error, report) = await _domains.SetWebhookHostAsync("https://Hooks.example.com/");

        Assert.Null(error);
        Assert.Equal("hooks.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookHost));
        Assert.Equal(64, (await _settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret))!.Length);
        var rule = _cloudflare.Ingress.OfType<JsonObject>().Single(r => r["path"]?.GetValue<string>() == TunnelRoutes.WebhookPath);
        Assert.Equal("hooks.example.com", rule["hostname"]!.GetValue<string>());
        Assert.StartsWith("http://localhost:", rule["service"]!.GetValue<string>());
        Assert.Equal(TunnelRoutes.NotFoundService, RuleFor("hooks.example.com")["service"]!.GetValue<string>());
        Assert.Contains($"POST /client/v4/zones/z1/dns_records", _cloudflare.Calls);
        Assert.Contains(report.Notes, n => n.Text == "Added a DNS record sending hooks.example.com to the tunnel");
        // Never behind Access.
        Assert.DoesNotContain(_cloudflare.Calls, c => c.Contains("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_webhook_host_takes_the_route_from_the_old_one_and_keeps_the_secret()
    {
        await _domains.SetWebhookHostAsync("hooks.example.com");
        var secret = await _settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret);

        await _domains.SetWebhookHostAsync("gh.example.com");

        Assert.DoesNotContain("hooks.example.com", _cloudflare.Ingress.ToJsonString());
        Assert.Contains("gh.example.com", _cloudflare.Ingress.ToJsonString());
        Assert.Equal(secret, await _settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret));

        await _domains.SetWebhookHostAsync("");
        Assert.Null(await _settings.GetAsync(SettingKeys.GitHubWebhookHost));
        Assert.DoesNotContain(TunnelRoutes.WebhookPath, _cloudflare.Ingress.ToJsonString());
    }

    [Fact]
    public async Task The_webhook_host_and_app_domains_never_share_a_name()
    {
        Assert.Contains("is a domain of Web", (await _domains.SetWebhookHostAsync(Hostname)).Error);
        await _domains.SetWebhookHostAsync("hooks.example.com");
        Assert.Contains("GitHub's webhooks", (await _domains.AddAsync(_appId, "hooks.example.com")).Error);
    }

    [Fact]
    public async Task A_host_with_routes_of_its_own_isnt_taken_for_the_webhook()
    {
        _cloudflare.Ingress = Ingress("""[{"hostname":"hooks.example.com","service":"http://localhost:9000"},{"service":"http_status:404"}]""");
        Assert.Contains("already has routes in your tunnel", (await _domains.SetWebhookHostAsync("hooks.example.com")).Error);
        Assert.Null(await _settings.GetAsync(SettingKeys.GitHubWebhookHost));
    }

    [Fact]
    public async Task Instant_deploys_need_domains_on()
    {
        await _settings.SetAsync(SettingKeys.DomainsEnabled, null);
        Assert.Contains("turn them on in Settings › Domains", (await _domains.SetWebhookHostAsync("hooks.example.com")).Error);
        Assert.Empty(_cloudflare.Calls);
    }
}
