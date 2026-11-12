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
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void Only_the_webhooks_own_rules_go_with_a_removed_host()
    {
        var current = Ingress("""
            [{"hostname":"app.example.com","path":"^/hooks/github$","service":"http://localhost:9000"},
             {"hostname":"app.example.com","service":"http_status:404"},
             {"hostname":"old.example.com","path":"^/hooks/github$","service":"http://localhost:8070"},
             {"hostname":"old.example.com","service":"http_status:404"},
             {"service":"http_status:404"}]
            """);

        // app.example.com is an app's domain on its way out: its rules on the same path and its 404 aren't the webhook's.
        // old.example.com was the webhook's host, so both of its rules go, whatever Gitberth's address was then.
        var plan = TunnelRoutes.PlanWebhook(current, null, null, ["app.example.com", "old.example.com"], earlier: ["old.example.com"]);

        Assert.Equal(
            """[{"hostname":"app.example.com","path":"^/hooks/github$","service":"http://localhost:9000"},{"hostname":"app.example.com","service":"http_status:404"},{"service":"http_status:404"}]""",
            plan.Ingress.ToJsonString());
        Assert.Equal(["Removed the route GitHub's webhook took through old.example.com"], plan.Notes.Select(n => n.Text));
        // Not remembered as the webhook's, a host still counts when its rule on the path goes to Gitberth.
        var mine = TunnelRoutes.PlanWebhook(current, null, "http://localhost:8070", ["app.example.com", "old.example.com"]);
        Assert.Contains("app.example.com", mine.Ingress.ToJsonString());
        Assert.DoesNotContain("old.example.com", mine.Ingress.ToJsonString());
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
        /// <summary>The tunnel's routes can't be read or changed for now: Cloudflare answers 503.</summary>
        public bool TunnelDown { get; set; }
        /// <summary>Cloudflare's answer to a request that fails for now (it may throw, as an unreachable Cloudflare does); null lets it through.</summary>
        public Func<HttpRequestMessage, HttpStatusCode?>? Fail { get; set; }
        /// <summary>Where app.example.com's DNS record sends it: this tunnel, unless another has it now.</summary>
        public string AppTarget { get; set; } = $"{TunnelId}.cfargotunnel.com";
        /// <summary>The routes of the account's other tunnels (another Gitberth's, say), by tunnel ID.</summary>
        public Dictionary<string, JsonArray> OtherTunnels { get; } = new();
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
            if (Fail?.Invoke(request) is { } failed) return new HttpResponseMessage(failed);
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (path.EndsWith("/configurations", StringComparison.Ordinal) && !path.Contains($"/cfd_tunnel/{TunnelId}/", StringComparison.Ordinal))
            {
                var other = path.Split('/')[^2];
                return request.Method == HttpMethod.Get && OtherTunnels.TryGetValue(other, out var theirs)
                    ? Ok(new JsonObject { ["config"] = new JsonObject { ["ingress"] = theirs.DeepClone() } })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/configurations", StringComparison.Ordinal))
            {
                if (TunnelDown) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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
                        ? JsonNode.Parse($$"""[{"id":"r1","type":"CNAME","name":"app.example.com","content":"{{AppTarget}}","proxied":true}]""")
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
    private readonly DockerCli _docker;
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
        _docker = new DockerCli(new GitberthOptions { DataDir = _dir, DockerPath = WriteFakeDocker(_dir) });
        _domains = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);
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
        // That waits for someone to be let in: it isn't asked again every 15 minutes.
        Assert.Null(await _domains.RetryRemovalsAsync());
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
        Assert.DoesNotContain(report.Notes, n => n.Text.Contains("tries again", StringComparison.Ordinal));
        // Setting Zero Trust up is the owner's to do: Cloudflare isn't asked again every 15 minutes meanwhile.
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
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
    public async Task A_domain_a_sync_couldnt_protect_says_it_may_be_open_and_is_tried_again()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        // Protection is ready, but the tunnel's routes can't be changed right now.
        _cloudflare.TunnelDown = true;

        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: true);

        Assert.StartsWith("app.example.com isn't behind Cloudflare Access yet, so anyone may still open it. Cloudflare couldn't read the tunnel's routes", error);
        Assert.EndsWith("Gitberth tries again every 15 minutes.", error);
        Assert.True(Domain().Protected);
        Assert.Null(RuleFor(Hostname)["originRequest"]);

        // The overview says what's true: it's served, without Access, and Fix syncs.
        _cloudflare.TunnelDown = false;
        var open = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(open.Working);
        Assert.StartsWith("Served without Cloudflare Access", open.Summary);
        Assert.Equal(DomainFix.Sync, open.Fix);

        // The retry puts it behind Access, and then has nothing left to do.
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.True(TunnelRoutes.BehindAccess(_cloudflare.Ingress, Hostname));
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
    }

    [Fact]
    public async Task A_protected_domain_is_checked_once_after_gitberth_starts()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        // Saved as protected before Gitberth stopped, its route still open: nothing has synced since it started.
        Protect();

        Assert.NotNull(await _domains.RetryRemovalsAsync());

        Assert.True(TunnelRoutes.BehindAccess(_cloudflare.Ingress, Hostname));
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Rules_gitberth_didnt_make_on_a_protected_domain_are_said_and_not_tried_again()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        _cloudflare.Ingress = Ingress("""[{"hostname":"app.example.com","path":"^/api","service":"http://localhost:9000"},{"hostname":"app.example.com","service":"http://localhost:3001"},{"service":"http_status:404"}]""");

        var (error, report) = await _domains.SetProtectedAsync(_appId, Hostname, on: true);

        // Gitberth's own rule is behind Access; the other isn't Gitberth's to change, so it's said, and a sync can't help.
        Assert.NotNull(RuleFor(Hostname)["originRequest"]);
        Assert.Contains("rules for it that Gitberth didn't make", error);
        Assert.DoesNotContain("tries again", error);
        Assert.Contains(report.Notes, n => n.Problem && n.Text.EndsWith("Remove them in Cloudflare.", StringComparison.Ordinal));
        Assert.Null(await _domains.RetryRemovalsAsync());

        // The card says whose rules they are, with no Fix: no sync changes them.
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(state.Working);
        Assert.Equal("Some of its rules in the tunnel aren't Gitberth's and skip Cloudflare Access: remove them in Cloudflare.", state.Summary);
        Assert.Equal(DomainFix.None, state.Fix);
    }

    // ---- Cloudflare Access failing for a moment, or for good ----

    [Theory]
    [InlineData("GET", HttpStatusCode.ServiceUnavailable)]
    [InlineData("POST", HttpStatusCode.TooManyRequests)]
    public async Task Access_failing_for_a_moment_keeps_a_protected_domain_out_and_the_retry_brings_it_back(string method, HttpStatusCode status)
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        // Listing the Access applications, or making the domain's, fails for now; the tunnel works.
        _cloudflare.Fail = r => r.Method.Method == method && r.RequestUri!.AbsolutePath.EndsWith("/access/apps", StringComparison.Ordinal) ? status : (HttpStatusCode?)null;

        var report = await _domains.SyncAsync();

        // Out of the tunnel rather than open, and the note says it's tried again.
        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        var note = Assert.Single(report.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.StartsWith("app.example.com isn't served: it's protected, and ", note.Text);
        Assert.EndsWith("Gitberth tries again every 15 minutes.", note.Text);

        // Once Access answers, the retry puts it back behind Access, and then has nothing left to do.
        _cloudflare.Fail = null;
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.True(TunnelRoutes.BehindAccess(_cloudflare.Ingress, Hostname));
        Assert.Equal("a1", Domain().AccessAppId);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Cloudflare_out_of_reach_for_access_is_tried_again()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? throw new HttpRequestException("Connection refused") : (HttpStatusCode?)null;

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        Assert.Contains(report.Notes, n => n.Problem && n.Hostname == Hostname && n.Text.Contains("api.cloudflare.com couldn't be reached", StringComparison.Ordinal)
            && n.Text.EndsWith("Gitberth tries again every 15 minutes.", StringComparison.Ordinal));
        // The card offers Fix meanwhile: nothing is wrong with how protection is set up.
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.StartsWith("Not served until it's behind Cloudflare Access. api.cloudflare.com couldn't be reached", state.Summary);
        Assert.Equal(DomainFix.Sync, state.Fix);
        Assert.NotNull(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_token_without_the_access_permission_isnt_asked_again_every_15_minutes()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        var note = Assert.Single(report.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.Contains(CloudflareApi.AccessPermission, note.Text);
        Assert.DoesNotContain("tries again", note.Text);
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
        // The token is the owner's to fix: the card has no Fix for it.
        Assert.Equal(DomainFix.None, Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Fix);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Only_cloudflare_busy_or_out_of_reach_is_a_failure_for_the_moment(HttpStatusCode status, bool transient)
    {
        Assert.Equal(transient, CloudflareApi.IsTransient(status));
        _cloudflare.Fail = _ => status;
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => new CloudflareApi(new Factory(_cloudflare)).ListAccessAppsAsync("token", AccountId));
        Assert.Equal(transient, ex.Transient);

        _cloudflare.Fail = _ => throw new HttpRequestException("Connection refused");
        Assert.True((await Assert.ThrowsAsync<CloudflareApiException>(() => new CloudflareApi(new Factory(_cloudflare)).ListAccessAppsAsync("token", AccountId))).Transient);
    }

    [Fact]
    public async Task An_access_application_cloudflare_couldnt_remove_for_a_moment_goes_with_the_retry()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;

        var (error, report) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        // The tunnel stops checking at once; the application, which still asks visitors to sign in, waits for the retry,
        // and that's what the switch and the card say rather than calling it open.
        Assert.Equal("app.example.com still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. "
            + "Cloudflare couldn't take app.example.com out of Cloudflare Access: HTTP 503. Gitberth tries again every 15 minutes.", error);
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Contains(report.Notes, n => n.Problem && n.Hostname == Hostname && n.Text == error);
        Assert.Single(_cloudflare.Apps);
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(state.Working);
        Assert.Equal("Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out.", state.Summary);

        _cloudflare.Fail = null;
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(Domain().AccessAppId);
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Turning_protection_off_while_access_cant_be_used_says_visitors_may_still_sign_in()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The owner takes Access off the Cloudflare token, then turns protection off.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var (error, report) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        // The check Gitberth put on the route comes off, but its application stays. Gitberth can't see it now, so it only
        // says visitors may still have to sign in: it may have been deleted by hand.
        Assert.StartsWith("app.example.com may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. "
            + "Cloudflare didn't let Gitberth's token", error);
        Assert.Contains(CloudflareApi.AccessPermission, error);
        Assert.EndsWith("Click Fix to try again.", error);
        Assert.DoesNotContain("tries again", error);
        Assert.Contains(report.Notes, n => n.Problem && n.Hostname == Hostname && n.Text == error);
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Single(_cloudflare.Apps);
        Assert.Equal("a1", Domain().AccessAppId);
        // The card says so, with Fix, rather than calling it live.
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(state.Working);
        Assert.Equal("Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out.", state.Summary);
        Assert.Equal(DomainFix.Sync, state.Fix);

        // Once the token may manage Access again, Fix removes the application.
        _cloudflare.Fail = null;
        Assert.DoesNotContain((await _domains.SyncAsync()).Notes, n => n.Problem);
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(Domain().AccessAppId);
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
    }

    [Fact]
    public async Task Fix_after_cloudflare_comes_back_asks_access_again_at_once()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? throw new HttpRequestException("Connection refused") : (HttpStatusCode?)null;
        await _domains.SyncAsync();
        Assert.Equal(DomainFix.Sync, Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Fix);

        // Cloudflare answers again a moment later, and the owner clicks Fix.
        _cloudflare.Fail = null;
        var calls = _cloudflare.Calls.Count;
        var report = await _domains.SyncAsync();

        Assert.Contains($"GET /client/v4/accounts/{AccountId}/access/organizations", _cloudflare.Calls.Skip(calls));
        Assert.Contains($"POST /client/v4/accounts/{AccountId}/access/apps", _cloudflare.Calls.Skip(calls));
        Assert.DoesNotContain(report.Notes, n => n.Problem);
        Assert.True(TunnelRoutes.BehindAccess(_cloudflare.Ingress, Hostname));
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
    }

    // ---- A removed domain's Access application ----

    private async Task<string?> LeftoversAsync() => await _settings.GetAsync(SettingKeys.ProtectionLeftovers);

    [Fact]
    public async Task A_removed_domains_access_application_cloudflare_couldnt_remove_for_a_moment_goes_with_the_retry()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;

        var report = await _domains.RemoveAsync(_appId, Hostname);

        // The route goes, the application stays for now, and the note says so.
        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        Assert.Single(_cloudflare.Apps);
        Assert.Contains(report.Notes, n => n.Problem && n.Text == "app.example.com still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. "
            + "Cloudflare couldn't take app.example.com out of Cloudflare Access: HTTP 503. Gitberth tries again every 15 minutes.");
        using (var db = _db.CreateDbContext()) Assert.Empty(db.DomainRemovals);

        // The retry removes it by itself, and then has nothing left to do.
        _cloudflare.Fail = null;
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_removed_domains_access_application_cloudflare_refuses_to_remove_says_what_to_do_and_waits_for_the_next_sync()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The token may still read Access, but not remove from it.
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var report = await _domains.RemoveAsync(_appId, Hostname);

        var note = Assert.Single(report.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.StartsWith("app.example.com still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. Cloudflare didn't let Gitberth's token", note.Text);
        Assert.EndsWith("Fix the token's permission, or delete \"Gitberth: app.example.com\" in Cloudflare Zero Trust. "
            + "Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", note.Text);
        Assert.Equal("""{"app.example.com":"a1"}""", await LeftoversAsync());

        // A refusal lasts, so Cloudflare isn't asked again every 15 minutes in vain.
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
        // Check again finds Access working (the token may still read it), so the retry tries once more: still refused, it
        // waits again.
        Assert.True((await _domains.ProtectionAsync()).Ready);
        Assert.Contains((await _domains.RetryRemovalsAsync())!.Notes, n => n.Problem && n.Text == note.Text);
        calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);
        // The next sync (a deploy, say) tries again: still refused, it's still there.
        Assert.Contains((await _domains.SyncAsync()).Notes, n => n.Problem && n.Text == note.Text);
        Assert.Single(_cloudflare.Apps);
        // The owner deletes it in the Zero Trust dashboard and clicks Check again: the retry sees it's gone, and that's the
        // end of it.
        _cloudflare.Apps.Clear();
        Assert.True((await _domains.ProtectionAsync()).Ready);
        Assert.DoesNotContain((await _domains.RetryRemovalsAsync())!.Notes, n => n.Problem);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_deleted_apps_access_application_waits_until_access_can_be_used_again()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The token can't list Access applications at all when the app is deleted.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.EndsWith("/access/apps", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var report = await _domains.DeleteAppAsync(_appId);

        // Gitberth can't see whether the application is still there, so it only says it may be. Deleting it by hand isn't
        // offered: Gitberth wouldn't see that either.
        var note = Assert.Single(report.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.StartsWith("app.example.com may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. "
            + "Cloudflare didn't let Gitberth's token list the account's Cloudflare Access applications.", note.Text);
        Assert.EndsWith("Fix the token's permission. Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", note.Text);
        Assert.Single(_cloudflare.Apps);

        // That lasts: Cloudflare isn't asked again every 15 minutes in vain.
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);

        // The token's permission is fixed: the next sync (Fix on another domain, say) removes the application.
        _cloudflare.Fail = null;
        Assert.Contains((await _domains.SyncAsync()).Notes, n => n.Text == "Removed the Cloudflare Access application Gitberth made for app.example.com");
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    /// <summary>What a background service logs, line by line.</summary>
    private sealed class Lines<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public List<string> All
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task An_application_left_to_remove_isnt_tried_or_logged_every_15_minutes_while_the_token_may_not_use_access()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The owner takes Access off the Cloudflare token and deletes the app, then deletes "Gitberth: app.example.com" in
        // Cloudflare Zero Trust by hand.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;
        await _domains.DeleteAppAsync(_appId);
        Assert.Equal("""{"app.example.com":"a1"}""", await LeftoversAsync());
        _cloudflare.Apps.Clear();

        // The retry asks Cloudflare nothing and logs nothing: it would say app.example.com still asks visitors to sign in.
        var logged = new Lines<RemovalRetry>();
        var retry = new RemovalRetry(_domains, logged) { Interval = TimeSpan.FromMilliseconds(20) };
        var tries = new List<SyncReport?>();
        var three = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retry.Tried += report =>
        {
            lock (tries)
            {
                tries.Add(report);
                if (tries.Count == 3) three.TrySetResult();
            }
        };
        var calls = _cloudflare.Calls.Count;
        await retry.StartAsync(CancellationToken.None);
        try
        {
            await three.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await retry.StopAsync(CancellationToken.None);
        }
        lock (tries) Assert.All(tries, report => Assert.Null(report));
        Assert.Empty(logged.All);
        Assert.Equal(calls, _cloudflare.Calls.Count);

        // After a restart it's tried once, asking for the team domain afresh, and the note doesn't say it's still there.
        var restarted = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);
        var note = Assert.Single((await restarted.RetryRemovalsAsync())!.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.StartsWith("app.example.com may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. "
            + "Cloudflare didn't let Gitberth's token read the account's Cloudflare Access settings.", note.Text);
        Assert.DoesNotContain("every 15 minutes", note.Text);
        Assert.DoesNotContain("delete", note.Text);
        Assert.Null(await restarted.RetryRemovalsAsync());

        // Once the token may use Access again, the next sync sees the application is gone, and forgets it.
        _cloudflare.Fail = null;
        Assert.DoesNotContain((await restarted.SyncAsync()).Notes, n => n.Problem);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await restarted.RetryRemovalsAsync());
    }

    [Fact]
    public async Task An_application_left_to_remove_is_tried_every_15_minutes_while_access_fails_for_a_moment()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // Cloudflare Access is busy when the app is deleted: its applications can't be listed for now.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.EndsWith("/access/apps", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;

        var report = await _domains.DeleteAppAsync(_appId);

        var note = Assert.Single(report.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.Equal("app.example.com may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. "
            + "Cloudflare couldn't list the account's Cloudflare Access applications: HTTP 503. Gitberth tries again every 15 minutes.", note.Text);
        // Still busy: every try asks again, and says so again.
        Assert.Contains((await _domains.RetryRemovalsAsync())!.Notes, n => n.Text == note.Text);
        Assert.Contains((await _domains.RetryRemovalsAsync())!.Notes, n => n.Text == note.Text);
        Assert.Single(_cloudflare.Apps);

        // Once Access answers, the retry removes it, and then has nothing left to do.
        _cloudflare.Fail = null;
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Without_zero_trust_an_application_left_to_remove_is_forgotten()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;
        await _domains.RemoveAsync(_appId, Hostname);
        Assert.Equal("""{"app.example.com":"a1"}""", await LeftoversAsync());
        // Gitberth restarts, and the tunnel's Cloudflare account has no Zero Trust now (the tunnel moved to another account,
        // say), so it has no Access applications at all.
        _cloudflare.Fail = null;
        _cloudflare.TeamDomain = null;
        var restarted = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);
        var calls = _cloudflare.Calls.Count;

        var report = await restarted.RetryRemovalsAsync();

        // There's nothing left to remove, so it's forgotten, without advice that wouldn't help.
        Assert.DoesNotContain(report!.Notes, n => n.Problem);
        Assert.Null(await LeftoversAsync());
        Assert.DoesNotContain(_cloudflare.Calls.Skip(calls), c => c.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Null(await restarted.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Without_zero_trust_a_domain_no_longer_protected_has_no_access_application_left()
    {
        // Its application was made in another Cloudflare account: the tunnel's has no Zero Trust.
        Protect(on: false, accessAppId: "a1");
        _cloudflare.TeamDomain = null;

        var report = await _domains.SyncAsync();

        Assert.DoesNotContain(report.Notes, n => n.Problem);
        Assert.Null(Domain().AccessAppId);
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_domain_added_again_takes_back_the_application_left_to_remove()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;
        await _domains.RemoveAsync(_appId, Hostname);
        Assert.NotNull(await LeftoversAsync());

        // Added back unprotected while Cloudflare still won't remove it: it's that domain's to finish now.
        Assert.Null((await _domains.AddAsync(_appId, Hostname)).Error);

        Assert.Null(await LeftoversAsync());
        Assert.Equal("a1", Domain().AccessAppId);
        Assert.Equal("Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out.", Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Summary);
        _cloudflare.Fail = null;
        await _domains.SyncAsync();
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(Domain().AccessAppId);
    }

    private const string OtherTunnelId = "99999999-8888-4777-8666-555555555555";

    /// <summary>
    /// The protected domain is removed while Cloudflare won't remove its Access application, so that's left to remove. Then
    /// another Gitberth on the same Cloudflare account serves the name through its own tunnel, with these rules.
    /// </summary>
    private async Task TakenOverByAnotherTunnelAsync(string rules)
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;
        await _domains.RemoveAsync(_appId, Hostname);
        Assert.Equal("""{"app.example.com":"a1"}""", await LeftoversAsync());
        _cloudflare.AppTarget = $"{OtherTunnelId}.cfargotunnel.com";
        _cloudflare.OtherTunnels[OtherTunnelId] = Ingress(rules);
        _cloudflare.Fail = null;
    }

    [Fact]
    public async Task A_removed_domains_access_application_another_gitberth_took_over_with_the_name_is_left_to_it()
    {
        // The other one protects the name with the application named after it, which it took up.
        await TakenOverByAnotherTunnelAsync("""[{"hostname":"app.example.com","service":"http://localhost:4000","originRequest":{"access":{"required":true,"teamName":"acme","audTag":["aud1"]}}},{"service":"http_status:404"}]""");
        var calls = _cloudflare.Calls.Count;

        var report = await _domains.RetryRemovalsAsync();

        // Removing it would turn everyone away there: it stays, and it's no longer Gitberth's to remove.
        Assert.DoesNotContain(_cloudflare.Calls.Skip(calls), c => c.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Single(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.Contains(report!.Notes, n => !n.Problem && n.Text == "Left \"Gitberth: app.example.com\" in Cloudflare Access: app.example.com is served through another tunnel now, which uses it.");
        Assert.DoesNotContain(report.Notes, n => n.Problem);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_removed_domains_access_application_still_goes_when_another_tunnel_serves_the_name_without_it()
    {
        // The other one serves the name open, so the application would make its visitors sign in.
        await TakenOverByAnotherTunnelAsync("""[{"hostname":"app.example.com","service":"http://localhost:4000"},{"service":"http_status:404"}]""");

        Assert.NotNull(await _domains.RetryRemovalsAsync());

        Assert.Empty(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Deleting_by_hand_isnt_offered_while_gitberth_cant_tell_whether_another_tunnel_uses_the_application()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;
        await _domains.RemoveAsync(_appId, Hostname);
        // Then the token loses Zone › Zone › Read, so Gitberth can't look up which tunnel the name goes to now.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.EndsWith("/zones", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;
        var calls = _cloudflare.Calls.Count;

        var report = await _domains.RetryRemovalsAsync();

        // Another Gitberth may use the application, so it isn't removed, and deleting it by hand isn't offered either.
        var note = Assert.Single(report!.Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.StartsWith("app.example.com still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. "
            + "Cloudflare didn't let Gitberth's token look up app.example.com in Cloudflare.", note.Text);
        Assert.EndsWith("Fix the token's permission. Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", note.Text);
        Assert.DoesNotContain(_cloudflare.Calls.Skip(calls), c => c.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Single(_cloudflare.Apps);
        Assert.Equal("""{"app.example.com":"a1"}""", await LeftoversAsync());
        // That lasts: it waits for the next sync rather than the retry every 15 minutes.
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task An_application_left_to_remove_is_tried_again_once_check_again_finds_access_working()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The owner takes Access off the Cloudflare token, then deletes the app: no app has a domain any more.
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;
        var note = Assert.Single((await _domains.DeleteAppAsync(_appId)).Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.EndsWith("Fix the token's permission. Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", note.Text);
        // A deploy still syncs the domains while it's left to remove.
        Assert.True(await _domains.AnyAsync());

        // Check again while the token still may not use Access changes nothing: the retry asks Cloudflare nothing.
        Assert.False((await _domains.ProtectionAsync()).Ready);
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);

        // The owner gives the token its permission back and clicks Check again: the retry removes the application, then
        // has nothing left to do.
        _cloudflare.Fail = null;
        Assert.True((await _domains.ProtectionAsync()).Ready);
        Assert.Contains((await _domains.RetryRemovalsAsync())!.Notes, n => n.Text == "Removed the Cloudflare Access application Gitberth made for app.example.com");
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(await LeftoversAsync());
        Assert.False(await _domains.AnyAsync());
        Assert.Null(await _domains.RetryRemovalsAsync());
        // Check again with nothing left to remove doesn't start the retry either.
        Assert.True((await _domains.ProtectionAsync()).Ready);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    // ---- Turning protection off while the tunnel can't change ----

    [Fact]
    public async Task Turning_protection_off_while_the_tunnel_cant_change_says_so_and_is_tried_again()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // Gitberth's application is deleted in the Zero Trust dashboard, so cloudflared now turns everyone away. The owner
        // turns protection off while Cloudflare can't change the tunnel.
        _cloudflare.Apps.Clear();
        _cloudflare.TunnelDown = true;

        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        Assert.StartsWith("app.example.com isn't open to everyone yet: Gitberth couldn't change the tunnel. Cloudflare couldn't read the tunnel's routes", error);
        Assert.EndsWith("Gitberth tries again every 15 minutes.", error);
        Assert.False(Domain().Protected);
        Assert.NotNull(RuleFor(Hostname)["originRequest"]);

        // The card says so too, rather than calling it live.
        _cloudflare.TunnelDown = false;
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(state.Working);
        Assert.Equal("Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out.", state.Summary);
        Assert.Equal(DomainFix.Sync, state.Fix);

        // The retry takes the check off, and then has nothing left to do.
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.True(Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Working);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Turning_off_a_domain_that_never_got_its_application_is_tried_again_when_the_tunnel_couldnt_change()
    {
        // Protected while nobody was allowed in: it got no Access application, and it's out of the tunnel.
        Protect();
        await _domains.SyncAsync();
        Assert.DoesNotContain(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        _cloudflare.TunnelDown = true;

        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        Assert.StartsWith("app.example.com isn't open to everyone yet: Gitberth couldn't change the tunnel.", error);
        Assert.EndsWith("Gitberth tries again every 15 minutes.", error);
        // And it does: the retry serves it again, open, and then has nothing left to do.
        _cloudflare.TunnelDown = false;
        Assert.NotNull(await _domains.RetryRemovalsAsync());
        Assert.Equal("http://localhost:3001", RuleFor(Hostname)["service"]!.GetValue<string>());
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task Turning_off_a_domain_the_tunnel_couldnt_take_is_still_tried_after_gitberth_restarts()
    {
        // As above: it never got an application, so once it's turned off nothing saved says a sync is due.
        Protect();
        await _domains.SyncAsync();
        _cloudflare.TunnelDown = true;
        Assert.EndsWith("Gitberth tries again every 15 minutes.", (await _domains.SetProtectedAsync(_appId, Hostname, on: false)).Error);

        // Gitberth restarts before the next try: the first try after it starts still serves the domain, open.
        _cloudflare.TunnelDown = false;
        var restarted = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);
        Assert.NotNull(await restarted.RetryRemovalsAsync());
        Assert.Equal("http://localhost:3001", RuleFor(Hostname)["service"]!.GetValue<string>());
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Null(await restarted.RetryRemovalsAsync());
    }

    [Fact]
    public async Task A_check_left_from_before_gitberth_restarted_is_taken_off_once_it_starts()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // Protection was turned off just before Gitberth stopped, and the tunnel never took it.
        Protect(on: false);
        _cloudflare.Apps.Clear();
        var restarted = new DomainService(_db, _settings, new CloudflareApi(new Factory(_cloudflare)), new TunnelLocator(_docker), _docker, NullLogger<DomainService>.Instance);

        Assert.NotNull(await restarted.RetryRemovalsAsync());

        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Null(await restarted.RetryRemovalsAsync());
    }

    // ---- A protected domain while nothing of the app is live ----

    [Fact]
    public async Task A_protected_domain_still_served_open_says_so_while_the_apps_saved_ports_arent_live()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        // The web port was changed in Ports and not applied: the container still publishes 3000 on 3001, where the route goes.
        using (var db = _db.CreateDbContext())
        {
            db.Ports.Single().ContainerPort = 4000;
            db.SaveChanges();
        }
        _cloudflare.TunnelDown = true;
        Assert.NotNull((await _domains.SetProtectedAsync(_appId, Hostname, on: true)).Error);
        _cloudflare.TunnelDown = false;

        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.False(state.Working);
        Assert.StartsWith("Served without Cloudflare Access, so anyone can open it.", state.Summary);
        Assert.Equal(DomainFix.Sync, state.Fix);

        // Fix puts the route it has behind Access; then the card says what else is wrong.
        await _domains.SyncAsync();
        Assert.True(TunnelRoutes.BehindAccess(_cloudflare.Ingress, Hostname));
        Assert.Equal("Not serving: Web isn't running, or its saved ports aren't live yet.", Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Summary);
    }

    [Fact]
    public async Task A_protected_domain_whose_tunnel_cant_be_read_isnt_said_to_be_merely_not_serving()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        using (var db = _db.CreateDbContext())
        {
            // The web port was changed in Ports and not applied; the app also has a domain that isn't protected.
            db.Ports.Single().ContainerPort = 4000;
            db.Domains.Add(new CustomDomain { AppId = _appId, Hostname = "www.example.com" });
            db.SaveChanges();
        }
        _cloudflare.TunnelDown = true;

        var overview = await _domains.GetOverviewAsync(_appId);

        // As when the app is live: the problem is said, and the protected domain, which may still be open, couldn't be checked.
        Assert.StartsWith("Cloudflare couldn't read the tunnel's routes", overview.Problem);
        var states = overview.Domains.ToDictionary(d => d.Hostname);
        Assert.False(states[Hostname].Working);
        Assert.Equal("Couldn't be checked.", states[Hostname].Summary);
        Assert.Equal("Not serving: Web isn't running, or its saved ports aren't live yet.", states["www.example.com"].Summary);
    }

    [Fact]
    public async Task Turning_protection_off_while_the_app_isnt_served_doesnt_send_the_owner_to_a_fix_the_card_hasnt()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The web port was changed in Ports and not applied, so nothing of the app is served. The owner takes Access off
        // the Cloudflare token, then turns protection off.
        using (var db = _db.CreateDbContext())
        {
            db.Ports.Single().ContainerPort = 4000;
            db.SaveChanges();
        }
        _cloudflare.Fail = r => r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        // The card has no Fix while the app isn't served, so the note says what does try again.
        Assert.StartsWith("app.example.com may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. ", error);
        Assert.EndsWith("Fix the token's permission. Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", error);
        Assert.DoesNotContain("Click Fix", error);
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.Equal("Not serving: Web isn't running, or its saved ports aren't live yet.", state.Summary);
        Assert.Equal(DomainFix.None, state.Fix);
        var calls = _cloudflare.Calls.Count;
        Assert.Null(await _domains.RetryRemovalsAsync());
        Assert.Equal(calls, _cloudflare.Calls.Count);

        // The token may use Access again and the owner clicks Check again: the retry removes the application.
        _cloudflare.Fail = null;
        Assert.True((await _domains.ProtectionAsync()).Ready);
        Assert.Contains((await _domains.RetryRemovalsAsync())!.Notes, n => n.Text == "Removed the Cloudflare Access application Gitberth made for app.example.com");
        Assert.Empty(_cloudflare.Apps);
        Assert.Null(Domain().AccessAppId);
        Assert.Null(await _domains.RetryRemovalsAsync());
    }

    [Fact]
    public async Task An_application_cloudflare_refuses_to_remove_says_click_fix_only_while_the_card_has_it()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        using (var db = _db.CreateDbContext())
        {
            db.Ports.Single().ContainerPort = 4000;
            db.SaveChanges();
        }
        // The token may still read Access, but not remove from it.
        _cloudflare.Fail = r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath.Contains("/access/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;

        var (error, _) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        // Nothing of the app is served, so there's no Fix: deleting it by hand is offered, since Gitberth would see that.
        Assert.StartsWith("app.example.com still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. Cloudflare didn't let Gitberth's token", error);
        Assert.EndsWith("Fix the token's permission, or delete \"Gitberth: app.example.com\" in Cloudflare Zero Trust. "
            + "Then click Check again in Settings › Domains › Protection: Gitberth tries again within 15 minutes. "
            + "It also tries the next time it updates your domains in Cloudflare.", error);
        Assert.Equal(DomainFix.None, Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains).Fix);

        // Served again (its saved port is live), the card has Fix, and that's what the note says to click.
        using (var db = _db.CreateDbContext())
        {
            db.Ports.Single().ContainerPort = 3000;
            db.SaveChanges();
        }
        var note = Assert.Single((await _domains.SyncAsync()).Notes, n => n.Problem && n.Hostname == Hostname);
        Assert.EndsWith("Click Fix to try again.", note.Text);
        Assert.DoesNotContain("Check again", note.Text);
        var state = Assert.Single((await _domains.GetOverviewAsync(_appId)).Domains);
        Assert.Equal("Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out.", state.Summary);
        Assert.Equal(DomainFix.Sync, state.Fix);
    }

    [Fact]
    public async Task A_check_whose_access_application_was_deleted_in_cloudflare_still_comes_off()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // Someone deletes Gitberth's application in the Zero Trust dashboard; another host has a check of their own.
        _cloudflare.Apps.Clear();
        _cloudflare.Ingress.Insert(0, JsonNode.Parse("""{"hostname":"wiki.example.com","service":"http://localhost:3005","originRequest":{"access":{"required":true,"teamName":"acme","audTag":["their-own"]}}}"""));

        var (error, report) = await _domains.SetProtectedAsync(_appId, Hostname, on: false);

        Assert.Null(error);
        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.Contains(report.Notes, n => n.Text == "app.example.com isn't behind Cloudflare Access any more");
        Assert.Null(Domain().AccessAppId);
        // A check Gitberth didn't put there stays.
        Assert.NotNull(RuleFor("wiki.example.com")["originRequest"]);
    }

    [Fact]
    public async Task A_check_gitberth_put_comes_off_even_when_access_isnt_asked_at_all()
    {
        await _settings.SetAsync(SettingKeys.ProtectionEmails, "ann@example.com");
        Protect();
        await _domains.SyncAsync();
        // The application is gone, and Gitberth forgot it before the check came off: nothing else leads to Access.
        _cloudflare.Apps.Clear();
        Protect(on: false);
        var calls = _cloudflare.Calls.Count;

        await _domains.SyncAsync();

        Assert.Null(RuleFor(Hostname)["originRequest"]);
        Assert.DoesNotContain(_cloudflare.Calls.Skip(calls), c => c.Contains("/access/", StringComparison.Ordinal));
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
    public async Task A_webhook_host_cloudflares_free_certificate_doesnt_cover_is_said_as_a_problem()
    {
        var (error, report) = await _domains.SetWebhookHostAsync("hooks.gitberth.example.com");

        // Routed as asked, but GitHub checks the certificate, so no delivery would get there: a problem, never "done".
        Assert.Null(error);
        Assert.Contains(report.Notes, n => n.Problem && n.Text == "GitHub's webhooks can't reach Gitberth at hooks.gitberth.example.com yet: "
            + "Cloudflare's free certificate covers example.com and *.example.com only, so hooks.gitberth.example.com needs Total TLS or an advanced certificate in Cloudflare.");

        // One level below the zone is covered.
        var (_, covered) = await _domains.SetWebhookHostAsync("hooks.example.com");
        Assert.DoesNotContain(covered.Notes, n => n.Text.StartsWith("GitHub's webhooks can't reach Gitberth", StringComparison.Ordinal));
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
        // Remembered, so the routes Gitberth made with the old name are known as its own.
        Assert.Equal("hooks.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore));

        await _domains.SetWebhookHostAsync("");
        Assert.Null(await _settings.GetAsync(SettingKeys.GitHubWebhookHost));
        Assert.DoesNotContain(TunnelRoutes.WebhookPath, _cloudflare.Ingress.ToJsonString());
        Assert.Equal("gh.example.com\nhooks.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore));
    }

    [Fact]
    public async Task A_removed_domain_keeps_rules_on_the_webhook_path_that_arent_gitberths()
    {
        await _domains.SetWebhookHostAsync("hooks.example.com");
        // The app takes GitHub's webhooks for itself on its own domain, through a rule made in the dashboard.
        _cloudflare.Ingress.Insert(0, JsonNode.Parse("""{"hostname":"app.example.com","path":"^/hooks/github$","service":"http://localhost:9000"}"""));

        await _domains.RemoveAsync(_appId, Hostname);

        var left = Assert.Single(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == Hostname);
        Assert.Equal("http://localhost:9000", left["service"]!.GetValue<string>());
        Assert.Contains(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == "hooks.example.com" && r["path"] is not null);
    }

    [Fact]
    public async Task An_earlier_webhook_host_given_to_an_app_is_the_apps_from_then_on()
    {
        await _domains.SetWebhookHostAsync("hooks.example.com");
        await _domains.SetWebhookHostAsync("gh.example.com");
        Assert.Equal("hooks.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore));

        // A CI app gets the old name as its domain, and a rule of its own takes GitHub's webhooks there on the same path.
        Assert.Null((await _domains.AddAsync(_appId, "hooks.example.com")).Error);
        Assert.Null(await _settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore));
        _cloudflare.Ingress.Insert(0, JsonNode.Parse("""{"hostname":"hooks.example.com","path":"^/hooks/github$","service":"http://localhost:9000"}"""));

        // Removing the domain later leaves that rule alone: it was never the webhook's.
        await _domains.RemoveAsync(_appId, "hooks.example.com");

        var left = Assert.Single(_cloudflare.Ingress.OfType<JsonObject>(), r => r["hostname"]?.GetValue<string>() == "hooks.example.com");
        Assert.Equal("http://localhost:9000", left["service"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_sync_forgets_earlier_webhook_hosts_that_are_apps_domains_now()
    {
        await _settings.SetAsync(SettingKeys.GitHubWebhookHostsBefore, "old.example.com\nhooks.example.com");
        // Made without going through the Domains card, as a preview's domain is.
        using (var db = _db.CreateDbContext())
        {
            db.Domains.Add(new CustomDomain { AppId = _appId, Hostname = "hooks.example.com" });
            db.SaveChanges();
        }

        await _domains.SyncAsync();

        Assert.Equal("old.example.com", await _settings.GetAsync(SettingKeys.GitHubWebhookHostsBefore));
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
