using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Agents;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Previews;
using Gitberth.Public;
using Gitberth.Services;
using Gitberth.Sources;
using Gitberth.Detection;
using Gitberth.Templates;
using Gitberth.Uptime;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The tools AI agents and scripts call: every one has an access level and the hints that go with it, the docs and the
/// OpenAPI document list each one, and (on CT 101's containers, <see cref="AppsRig"/>) they read without ever giving a
/// secret's value, act as the token in the audit log, and refuse a destructive call without its confirm.
/// </summary>
public sealed class AgentToolTests : IDisposable
{
    /// <summary>Every tool and the least access it needs. A new tool goes here too, deliberately.</summary>
    private static readonly Dictionary<string, ApiAccess> Levels = new()
    {
        ["list_apps"] = ApiAccess.Read, ["get_app"] = ApiAccess.Read, ["get_app_logs"] = ApiAccess.Read, ["get_app_stats"] = ApiAccess.Read,
        ["list_deployments"] = ApiAccess.Read, ["get_deployment"] = ApiAccess.Read, ["list_previews"] = ApiAccess.Read,
        ["get_health"] = ApiAccess.Read, ["list_domains"] = ApiAccess.Read, ["get_uptime"] = ApiAccess.Read, ["query_audit_log"] = ApiAccess.Read,
        ["list_databases"] = ApiAccess.Read, ["list_updates"] = ApiAccess.Read, ["list_images"] = ApiAccess.Read, ["get_settings"] = ApiAccess.Read,
        // Docker Hub's search and an image's tags, as New app shows them.
        ["search_images"] = ApiAccess.Read, ["list_image_tags"] = ApiAccess.Read,
        ["deploy_app"] = ApiAccess.Operate, ["restart_app"] = ApiAccess.Operate, ["start_app"] = ApiAccess.Operate, ["stop_app"] = ApiAccess.Operate,
        ["rollback_app"] = ApiAccess.Operate, ["cancel_deploy"] = ApiAccess.Operate, ["promote_preview"] = ApiAccess.Operate,
        ["check_updates"] = ApiAccess.Operate, ["apply_update"] = ApiAccess.Operate, ["run_cron_now"] = ApiAccess.Operate, ["set_previews"] = ApiAccess.Operate,
        ["create_app"] = ApiAccess.Admin, ["delete_app"] = ApiAccess.Admin, ["set_env_vars"] = ApiAccess.Admin, ["update_app_settings"] = ApiAccess.Admin,
        ["add_database"] = ApiAccess.Admin, ["remove_database"] = ApiAccess.Admin, ["create_deploy_hook"] = ApiAccess.Admin, ["delete_deploy_hook"] = ApiAccess.Admin,
        ["add_domain"] = ApiAccess.Admin, ["remove_domain"] = ApiAccess.Admin, ["set_main_domain"] = ApiAccess.Admin, ["protect_domain"] = ApiAccess.Admin,
        ["set_notifications"] = ApiAccess.Admin, ["set_update_settings"] = ApiAccess.Admin, ["set_uptime_settings"] = ApiAccess.Admin, ["set_status_page"] = ApiAccess.Admin,
        ["set_preview_settings"] = ApiAccess.Admin,
        // App templates (TemplateCatalog), and the Postgres server the ones with a database need.
        ["list_templates"] = ApiAccess.Read, ["create_postgres_server"] = ApiAccess.Admin,
    };

    private const string Secret = "s3cr3t-route-7f4e9a";
    private const string Plain = "visible-value-31";

    private readonly AgentsRig _agents = new();

    public void Dispose() => _agents.Dispose();

    private static readonly ApiCaller Admin = new(1, "ci-agent", ApiAccess.Admin, AuditVia.Api, "10.0.0.9");
    private static readonly ApiCaller Operator = new(2, "ops-agent", ApiAccess.Operate, AuditVia.Mcp, "10.0.0.8");

    private Task<(int Status, string Body)> CallAsync(string tool, object? args = null, ApiCaller? caller = null)
    {
        var dict = args is null ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return AgentEndpoints.InvokeAsync(ToolCatalog.Find(tool) ?? throw new InvalidOperationException(tool), caller ?? Admin, dict, _agents.Services, CancellationToken.None);
    }

    // ---- the catalog ----

    [Fact]
    public void Every_tool_has_an_access_level()
    {
        var missing = ToolCatalog.Methods().Where(m => m.Method.GetCustomAttributes(typeof(ToolAccessAttribute), false).Length == 0).Select(m => m.Tool.Name).ToList();
        Assert.Empty(missing);
        Assert.Equal(Levels.OrderBy(l => l.Key), ToolCatalog.All.ToDictionary(t => t.Name, t => t.Access).OrderBy(l => l.Key));
    }

    [Fact]
    public void Tools_are_snake_case_and_described_and_read_tools_are_read_only_and_only_read_tools_are()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Matches("^[a-z]+(_[a-z]+)*$", tool.Name);
            Assert.True(tool.Description.Length > 40, tool.Name);
            Assert.Equal(tool.Access == ApiAccess.Read, tool.ReadOnly);
            Assert.Equal(tool.ReadOnly, tool.Annotations.ReadOnlyHint);
            Assert.Equal(tool.Destructive, tool.Annotations.DestructiveHint);
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
        }
    }

    [Fact]
    public void Destructive_tools_take_confirm_and_say_so()
    {
        var destructive = ToolCatalog.All.Where(t => t.Destructive).Select(t => t.Name).Order().ToList();
        Assert.Equal(["delete_app", "delete_deploy_hook", "remove_database", "remove_domain", "rollback_app", "set_previews", "stop_app"], destructive);
        foreach (var name in destructive.Where(n => n != "delete_deploy_hook"))
        {
            var tool = ToolCatalog.Find(name)!;
            Assert.Contains(AgentDocs.Arguments(tool.InputSchema), a => a.Name == "confirm");
            Assert.Contains("confirm", tool.Description);
        }
    }

    [Fact]
    public void The_openapi_document_lists_every_tool()
    {
        var doc = AgentEndpoints.OpenApi("http://10.1.1.80:8080/");
        Assert.Equal("3.1.0", doc["openapi"]!.GetValue<string>());
        var paths = doc["paths"]!.AsObject();
        foreach (var tool in ToolCatalog.All)
        {
            var post = paths[$"/api/v1/tools/{tool.Name}"]?["post"];
            Assert.NotNull(post);
            Assert.Equal(tool.Name, post!["operationId"]!.GetValue<string>());
            Assert.Equal(tool.Access.ToString(), post["x-gitberth-access"]!.GetValue<string>());
            Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(post["requestBody"]!["content"]!["application/json"]!["schema"]).ValueKind);
        }
        Assert.Equal("http://10.1.1.80:8080", doc["servers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(ToolCatalog.All.Count + 1, paths.Count);
    }

    [Fact]
    public void The_api_reference_in_the_docs_is_the_tools_own()
    {
        var path = DocsFile("reference", "api.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var expected = AgentDocs.Reference();
        var start = text.IndexOf(AgentDocs.Start, StringComparison.Ordinal);
        var end = text.IndexOf(AgentDocs.End, StringComparison.Ordinal);
        var actual = start >= 0 && end > start ? text[start..(end + AgentDocs.End.Length)] + "\n" : "";
        if (actual != expected) File.WriteAllText(path + ".expected", expected);
        Assert.True(actual == expected, $"docs/reference/api.md's tool reference isn't the tools' own: the expected text is in {path}.expected");
    }

    private static string DocsFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "reference"))) return Path.Combine([dir.FullName, "docs", .. parts]);
        throw new InvalidOperationException("docs/ wasn't found above the test's folder.");
    }

    [Fact]
    public void A_tool_above_the_tokens_access_is_refused_with_why_and_the_refusal_is_audited()
    {
        var reader = new ApiCaller(3, "reader", ApiAccess.Read, AuditVia.Mcp, null);
        var audit = new AuditLog();
        var why = AgentEndpoints.Refusal(reader, ToolCatalog.Find("deploy_app")!, audit);
        Assert.Equal("The token reader has Read access, and deploy_app needs Operate. A person can make a token with Operate access in Gitberth's Settings › AI agents and API.", why);
        Assert.Null(AgentEndpoints.Refusal(reader, ToolCatalog.Find("list_apps")!, audit));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("auth.token.denied", entry.Action);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal(AuditActorKind.ApiToken, entry.Actor.Kind);
    }

    [Fact]
    public void Each_clients_setup_has_the_mcp_address_and_the_token()
    {
        const string token = "gbt_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef";
        var setups = AgentSetup.For("http://10.1.1.80:8080/", token);
        Assert.Equal(["claude-code", "vscode", "cursor", "claude-desktop", "generic", "curl"], setups.Select(s => s.Id));
        Assert.All(setups, s => Assert.Contains("http://10.1.1.80:8080/", s.Code));
        // VS Code asks for the token itself and keeps it in its secret storage; the others carry it.
        Assert.All(setups.Where(s => s.Id != "vscode"), s => Assert.Contains(token, s.Code));
        Assert.Contains("claude mcp add --transport http gitberth http://10.1.1.80:8080/mcp --header \"Authorization: Bearer " + token + "\"", setups[0].Code);
        Assert.Contains("--allow-http", setups.Single(s => s.Id == "claude-desktop").Code);
        Assert.DoesNotContain("--allow-http", AgentSetup.For("http://localhost:8080/", token).Single(s => s.Id == "claude-desktop").Code);
        foreach (var json in setups.Where(s => s.Id is "vscode" or "cursor" or "claude-desktop")) JsonDocument.Parse(json.Code).Dispose();
    }

    // ---- calling the tools ----

    [Fact]
    public async Task No_read_tool_ever_returns_a_secrets_value()
    {
        var orb = _agents.Rig.ObsId;
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            db.EnvVars.Add(new EnvVar { AppId = orb, Key = "PAYMENT_ROUTE", ProtectedValue = _agents.Rig.Protector.Protect(Secret), IsSecret = true });
            db.EnvVars.Add(new EnvVar { AppId = orb, Key = "PLAIN_SETTING", ProtectedValue = _agents.Rig.Protector.Protect(Plain) });
            db.Deployments.Add(new Deployment { AppId = orb, Status = DeployStatus.Succeeded, ImageTag = "gitberth/orb:1d2948c", CommitSha = "1d2948cc693a", FinishedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var deployment = (await _agents.Rig.Db.CreateDbContext().Deployments.AsNoTracking().FirstAsync(d => d.AppId == orb)).Id;
        // postgres runs Docker Hub's image with its password in its environment.
        var postgresEnv = _agents.Rig.Engine.Inspect("postgres")["Config"]!["Env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        var postgresPassword = postgresEnv.Single(e => e.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal))["POSTGRES_PASSWORD=".Length..];

        var outputs = new List<(string Tool, string Body)>();
        foreach (var app in new[] { "gb-orb", "postgres" })
        {
            foreach (var tool in ToolCatalog.All.Where(t => t.Access == ApiAccess.Read))
            {
                var args = AgentDocs.Arguments(tool.InputSchema).Select(a => a.Name).ToHashSet();
                object? call = args.Contains("deployment_id") ? new { deployment_id = deployment }
                    : args.Contains("app") && AgentDocs.Arguments(tool.InputSchema).Single(a => a.Name == "app").Required ? new { app }
                    : null;
                outputs.Add((tool.Name, (await CallAsync(tool.Name, call, Admin)).Body));
                if (args.Contains("app") && call is null) outputs.Add((tool.Name + "(app)", (await CallAsync(tool.Name, new { app }, Admin)).Body));
            }
        }
        Assert.All(outputs, o => Assert.DoesNotContain(Secret, o.Body));
        Assert.All(outputs, o => Assert.DoesNotContain(postgresPassword, o.Body));
        // The app's page shows its variables all the same: the secret by name, the plain one with its value.
        var orbApp = outputs.First(o => o.Tool == "get_app").Body;
        Assert.Contains("PAYMENT_ROUTE", orbApp);
        Assert.Contains(Plain, orbApp);
        Assert.Contains("POSTGRES_PASSWORD", outputs.Last(o => o.Tool == "get_app").Body);
    }

    [Fact]
    public async Task What_a_tool_changes_is_recorded_against_the_token()
    {
        var (status, body) = await CallAsync("set_notifications", new { previews = true }, Admin);
        Assert.Equal(200, status);
        Assert.Contains("\"previews\":true", body);
        var entry = _agents.Audit.Entries.Last(e => e.Action == "settings.notifications.changed");
        Assert.Equal(new AuditActor(AuditActorKind.ApiToken, "ci-agent", AuditVia.Api, "10.0.0.9"), entry.Actor);
        Assert.Equal("the token ci-agent", AuditActors.Describe(entry.Actor));
    }

    [Fact]
    public async Task An_agent_chooses_every_notification_settings_offers()
    {
        // Every switch of Settings › Notifications is an argument of its own, and previews too.
        var args = AgentDocs.Arguments(ToolCatalog.Find("set_notifications")!.InputSchema).Select(a => a.Name).ToList();
        Assert.Equal(NotificationSettings.Choosable.Length + 1, args.Count);
        var (status, body) = await CallAsync("set_notifications", new { brought_back = false, token_expiring = false, disk_low = false }, Admin);
        Assert.Equal(200, status);
        var events = JsonNode.Parse(body)!["events"]!;
        Assert.Equal((false, false, false, true), ((bool)events["BroughtBack"]!, (bool)events["TokenExpiring"]!, (bool)events["DiskLow"]!, (bool)events["Down"]!));
        Assert.Contains(_agents.Audit.Entries, e => e.Summary == "No more notifications when Docker's disk gets full.");
    }

    [Fact]
    public async Task Get_health_tells_what_the_dashboards_banners_warn_about()
    {
        _agents.Attention.Set(new AttentionItem("disk", "disk", AttentionLevel.Warn, "Docker's disk is getting low", "12.3 GB free of 64.0 GB."));
        _agents.Attention.Set(new AttentionItem("credential:github", "credentials", AttentionLevel.Info, "The GitHub token expires in 3 weeks", "Make a new one."));
        var (status, body) = await CallAsync("get_health", null, Admin);
        Assert.Equal(200, status);
        var problems = JsonNode.Parse(body)!["problems"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Contains("Docker's disk is getting low: 12.3 GB free of 64.0 GB.", problems);
        // Three weeks ahead is a banner, not a problem yet.
        Assert.DoesNotContain(problems, p => p.Contains("GitHub token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deploy_a_token_asked_for_is_queued_as_the_tokens()
    {
        var (status, body) = await CallAsync("deploy_app", new { app = "orb" }, Operator);
        Assert.Equal(200, status);
        var id = JsonNode.Parse(body)!["deployment_id"]!.GetValue<int>();
        await using var db = _agents.Rig.Db.CreateDbContext();
        var dep = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.Equal(AuditActors.Format(Operator.Actor), dep.RequestedBy);
        Assert.Equal(Operator.Actor, AuditActors.ForDeploy(dep.RequestedBy, dep.Trigger));
    }

    [Theory]
    [InlineData("stop_app", null)]
    [InlineData("stop_app", "kanban")]
    [InlineData("delete_app", null)]
    [InlineData("rollback_app", "Orb2")]
    public async Task A_destructive_tool_without_the_apps_name_in_confirm_is_refused_and_changes_nothing(string tool, string? confirm)
    {
        // Only rollback_app takes a deploy: any other tool refuses an argument it doesn't take.
        object args = tool == "rollback_app" ? new { app = "gb-orb", confirm, deployment_id = 1 } : new { app = "gb-orb", confirm };
        var (status, body) = await CallAsync(tool, args, Admin);
        Assert.True(status == 400, body);
        Assert.Contains("pass confirm: \"orb\"", JsonNode.Parse(body)!["error"]!.GetValue<string>());
        await using var db = _agents.Rig.Db.CreateDbContext();
        Assert.Equal(DesiredState.Running, (await db.Apps.AsNoTracking().SingleAsync(a => a.Id == _agents.Rig.ObsId)).DesiredState);
        Assert.Empty(_agents.Rig.Engine.Writes);
    }

    [Fact]
    public async Task Removing_a_domain_needs_the_domain_in_confirm()
    {
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            db.Domains.Add(new CustomDomain { AppId = _agents.Rig.ObsId, Hostname = "orb.example.com" });
            await db.SaveChangesAsync();
        }
        var (status, body) = await CallAsync("remove_domain", new { app = "orb", hostname = "orb.example.com", confirm = "orb" }, Admin);
        Assert.Equal(400, status);
        Assert.Contains("orb.example.com", body);
        await using var check = _agents.Rig.Db.CreateDbContext();
        Assert.True(await check.Domains.AnyAsync(d => d.Hostname == "orb.example.com"));
    }

    [Fact]
    public async Task An_app_that_isnt_there_is_a_404_with_where_to_look()
    {
        var (status, body) = await CallAsync("get_app", new { app = "no-such-app" }, Admin);
        Assert.Equal(404, status);
        Assert.Contains("list_apps", body);
    }

    [Fact]
    public async Task A_registry_image_app_isnt_deployed_but_told_to_update_instead()
    {
        var (status, body) = await CallAsync("deploy_app", new { app = "postgres" }, Operator);
        Assert.Equal(409, status);
        Assert.Contains("isn't deployed from a repo or a Compose file", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    // ---- images: Docker Hub's search, an image's tags, and an app's tag ----

    [Fact]
    public async Task Search_images_and_list_image_tags_say_why_when_docker_hub_cant_be_reached()
    {
        // No network here: Docker Hub can't be reached, and the answer says so (a 200, as the picker's line does).
        var (status, body) = await CallAsync("list_image_tags", new { image = "nginx:1.27" }, Admin);
        Assert.True(status == 200, body);
        var tags = JsonNode.Parse(body)!;
        Assert.Equal(("nginx", "Docker Hub", "unreachable", "Docker Hub can't be reached from this host, so its tags can't be listed."),
            (tags["image"]!.GetValue<string>(), tags["registry"]!.GetValue<string>(), tags["problem"]!.GetValue<string>(), tags["message"]!.GetValue<string>()));
        Assert.Empty(tags["tags"]!.AsArray());

        (status, body) = await CallAsync("search_images", new { query = "nginx" }, Admin);
        Assert.True(status == 200, body);
        var found = JsonNode.Parse(body)!;
        Assert.Equal(("unreachable", "nginx"), (found["problem"]!.GetValue<string>(), found["query"]!.GetValue<string>()));
        Assert.StartsWith("Docker Hub can't be reached from this host, so there's no search.", found["message"]!.GetValue<string>());

        // What isn't a query, an image or a page count is the caller's to fix.
        Assert.Equal(400, (await CallAsync("search_images", new { query = "n" }, Admin)).Status);
        Assert.Equal(400, (await CallAsync("list_image_tags", new { image = "Not An Image" }, Admin)).Status);
        Assert.Equal(400, (await CallAsync("list_image_tags", new { image = "nginx", pages = 11 }, Admin)).Status);
        Assert.Equal(400, (await CallAsync("list_image_tags", new { image = "nginx", limit = 0 }, Admin)).Status);
    }

    [Fact]
    public async Task Update_app_settings_image_is_refused_for_a_git_app_as_its_settings_tab_has_no_image()
    {
        var (status, body) = await CallAsync("update_app_settings", new { app = "orb", image = "nginx:1.31" }, Admin);

        Assert.Equal(400, status);
        Assert.Equal("orb is a Git app: its image is built from its repo, so image is an image app's.", JsonNode.Parse(body)!["error"]!.GetValue<string>());
        Assert.Empty(_agents.ImageEngine.Calls);
        Assert.Equal(400, (await CallAsync("update_app_settings", new { app = "postgres", image = "Not An Image" }, Admin)).Status);
    }

    [Fact]
    public async Task Update_app_settings_image_pulls_what_the_host_doesnt_have_first_then_plans_as_the_tab_does()
    {
        // postgres runs postgres:18. The pull of postgres:19 puts it on the host, for Docker and for the change's plan.
        _agents.ImageEngine.Pulls["docker.io/library/postgres:19"] = (["{\"status\":\"Status: Downloaded newer image for postgres:19\"}"], () =>
        {
            _agents.ImageEngine.Add(ImageKit.Image(ImageKit.Id('9'), ["postgres:19"], ["postgres@" + ImageKit.Digest('9')]));
            _agents.Rig.Engine.Images["postgres:19"] = new JsonObject
            {
                ["Id"] = ImageKit.Id('9'),
                ["RepoTags"] = new JsonArray("postgres:19"),
                ["RepoDigests"] = new JsonArray("postgres@" + ImageKit.Digest('9')),
                ["Config"] = new JsonObject { ["Env"] = new JsonArray("PG_MAJOR=19") },
            };
        });

        var (status, body) = await CallAsync("update_app_settings", new { app = "postgres", image = "postgres:19" }, Admin);

        Assert.Equal(["pull docker.io/library/postgres:19"], _agents.ImageEngine.Calls);
        // The database guard's words, as the tab's plan gives them: a major version can't be changed here.
        Assert.Equal(409, status);
        Assert.Equal("A new major version of PostgreSQL can't read 18's data: dump it and restore it into a new app instead.", JsonNode.Parse(body)!["error"]!.GetValue<string>());
        Assert.Empty(_agents.Rig.Engine.Writes);

        // A pull that fails says why, and nothing is planned.
        (status, body) = await CallAsync("update_app_settings", new { app = "postgres", image = "postgres:18.9-none" }, Admin);
        Assert.Equal(409, status);
        Assert.StartsWith("Docker Hub refused postgres:18.9-none", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    // ---- Railpack ----

    /// <summary>Railpack's planner: the plan it gives, and what it was asked.</summary>
    private sealed class Planner(Railpack.RailpackPlanResult result) : Railpack.IRailpackPlanner
    {
        public List<Railpack.RailpackPlanRequest> Asked { get; } = [];

        public Task<Railpack.RailpackPlanResult> PlanAsync(Railpack.RailpackPlanRequest request, Action<string>? onLine, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(result);
        }

        public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>(Railpack.RailpackRelease.Version);
    }

    [Fact]
    public async Task Create_app_builds_with_railpack_when_asked_and_for_a_project_gitberth_doesnt_know()
    {
        var dir = Directory.CreateTempSubdirectory("gitberth-choose-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "start.sh"), "echo hi\n");
            var shell = new Planner(new Railpack.RailpackPlanResult(true, null, null,
                Railpack.RailpackInfoParser.Parse(RailpackFixtures.Info("info-shell.json"), RailpackFixtures.Plan("plan-shell.json")), RailpackFixtures.Text("plan-shell.json"), []));
            var unknown = new ProjectAnalyzer().Analyze(dir);
            Assert.True(unknown.Unrecognized);

            // Detect automatically: Railpack's plan is the card, with its port; Build stays Detect automatically.
            var (fallback, mode, tried) = await Agents.Tools.AppAdminTools.ChooseBuilderAsync(shell, alwaysRailpack: false, unknown, dir, "", dir,
                [new EnvValue("RAILPACK_NODE_VERSION", "22", false)], CancellationToken.None);
            Assert.Equal((ProjectKind.Railpack, 8080, true, DockerfileMode.Auto, true), (fallback.Kind, fallback.ContainerPort, fallback.Unrecognized, mode, tried));
            Assert.Equal("Shell script with Railpack", fallback.Title);
            var asked = Assert.Single(shell.Asked);
            Assert.Equal((dir, "", false, "RAILPACK_NODE_VERSION"), (asked.RepoDir, asked.AppDir, asked.HasStartCommand, asked.Env.Single().Key));

            // build railpack: always Railpack, on the port Gitberth detected.
            File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>hi</h1>");
            var site = new ProjectAnalyzer().Analyze(dir);
            var (chosen, always, _) = await Agents.Tools.AppAdminTools.ChooseBuilderAsync(shell, alwaysRailpack: true, site, dir, "", dir, [], CancellationToken.None);
            Assert.Equal((ProjectKind.Railpack, 80, false, DockerfileMode.Railpack), (chosen.Kind, chosen.ContainerPort, chosen.Unrecognized, always));

            // A kind Gitberth knows isn't Railpack's to plan.
            Assert.Equal((site, DockerfileMode.Auto, false), await Agents.Tools.AppAdminTools.ChooseBuilderAsync(shell, false, site, dir, "", dir, [], CancellationToken.None));
            Assert.Equal(2, shell.Asked.Count);

            // Railpack can't plan it either: as before (the Starter Dockerfile follows); asked for, create_app says why.
            var nothing = new Planner(new Railpack.RailpackPlanResult(false, Railpack.RailpackFailure.NothingFound,
                Railpack.RailpackText.CantBuild(Railpack.RailpackText.FoundNothing), null, null, []));
            Assert.Equal((unknown, DockerfileMode.Auto, true), await Agents.Tools.AppAdminTools.ChooseBuilderAsync(nothing, false, unknown, dir, "", dir, [], CancellationToken.None));
            var refused = await Assert.ThrowsAsync<ToolError>(() => Agents.Tools.AppAdminTools.ChooseBuilderAsync(nothing, true, unknown, dir, "", dir, [], CancellationToken.None));
            Assert.Equal(Railpack.RailpackText.CantBuild(Railpack.RailpackText.FoundNothing), refused.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_agent_sets_a_git_apps_build_and_get_app_says_it_with_what_was_detected()
    {
        await using (var db = _agents.Rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _agents.Rig.ObsId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.DetectedKind, "Railpack").SetProperty(a => a.DetectedSummary, "Shell script with Railpack"));

        var (status, body) = await CallAsync("update_app_settings", new { app = "orb", build = "railpack", next_deploy = true }, Admin);

        Assert.True(status == 200, body);
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            var orb = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == _agents.Rig.ObsId);
            // A new builder is a new image: it waits for the next deploy.
            Assert.Equal((DockerfileMode.Railpack, true), (orb.DockerfileMode, orb.RebuildPending));
        }
        var git = JsonNode.Parse((await CallAsync("get_app", new { app = "orb" }, Admin)).Body)!["git"]!;
        Assert.Equal(("railpack", "Shell script with Railpack"), (git["build"]!.GetValue<string>(), git["detected"]!.GetValue<string>()));

        // A custom Dockerfile is the Settings tab's, and only a Git app has a build.
        (status, body) = await CallAsync("update_app_settings", new { app = "orb", build = "custom" }, Admin);
        Assert.Equal((400, "build is auto or railpack: a custom Dockerfile is written in the app's Settings tab."), (status, JsonNode.Parse(body)!["error"]!.GetValue<string>()));
        (status, body) = await CallAsync("update_app_settings", new { app = "postgres", build = "railpack" }, Admin);
        Assert.Equal(400, status);
        Assert.EndsWith("isn't deployed from a repo: build is a Git app's.", JsonNode.Parse(body)!["error"]!.GetValue<string>());
        (status, body) = await CallAsync("create_app", new { source = "image", image = "nginx:1.27", build = "railpack" }, Admin);
        Assert.Equal((400, "build goes with source git: it's how a repo's image is built."), (status, JsonNode.Parse(body)!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_agent_cant_make_a_railpack_setting_secret_but_can_name_a_secret_in_one()
    {
        // Holding a secret, or marked secret: refused, with what to do instead, and nothing saved.
        foreach (var setting in new object[] { new { name = "RAILPACK_START_CMD", value = "./server --api-key=s3cr3t-api-key" }, new { name = "RAILPACK_START_CMD", value = "./server", secret = true } })
        {
            var (refused, why) = await CallAsync("set_env_vars", new { app = "orb", set = new[] { setting }, next_deploy = true }, Admin);
            Assert.Equal((400, Railpack.RailpackText.SecretSetting("RAILPACK_START_CMD")), (refused, JsonNode.Parse(why)!["error"]!.GetValue<string>()));
        }
        await using (var db = _agents.Rig.Db.CreateDbContext())
            Assert.False(await db.EnvVars.AnyAsync(v => v.AppId == _agents.Rig.ObsId && v.Key == "RAILPACK_START_CMD"));

        // The secret in a variable of its own, named in the command: saved, the command as a plain value.
        var (status, body) = await CallAsync("set_env_vars", new
        {
            app = "orb",
            set = new object[] { new { name = "RAILPACK_START_CMD", value = "./server --api-key $API_KEY" }, new { name = "API_KEY", value = "s3cr3t-api-key", secret = true } },
            next_deploy = true,
        }, Admin);
        Assert.True(status == 200, body);
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            var saved = await db.EnvVars.AsNoTracking().Where(v => v.AppId == _agents.Rig.ObsId).ToDictionaryAsync(v => v.Key);
            Assert.Equal((false, true, true), (saved["RAILPACK_START_CMD"].IsSecret, saved["RAILPACK_START_CMD"].BuildTime, saved["API_KEY"].IsSecret));
        }
    }
}

/// <summary>The tools' services on CT 101's containers (<see cref="AppsRig"/>), as Program.cs registers them, for calling tools in tests.</summary>
internal sealed class AgentsRig : IDisposable
{
    private readonly ImageKit.Db _kitDb = new();

    public AgentsRig()
    {
        var rig = Rig;
        var engine = new DockerEngine(rig.Api);
        var host = new FakeHost(new FakeEngine(new FakeClock()));
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, rig.ContainerLocks, new Lazy<PortChanger>(() => changer!), rig.Activity, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, rig.Journal, rig.ContainerLocks, host, host, host, host, rig.Containers, rig.Options, NullLogger<PortChanger>.Instance, updater: updater);
        var settings = new AppSettingsService(rig.Catalog, rig.Apps, new Lazy<AppActions>(() => rig.Actions), changer, updater, engine, rig.AppLocks, rig.Domains,
            rig.Health, rig.Restarts, rig.Protector, rig.Db, rig.Options, rig.Activity, NullLogger<AppSettingsService>.Instance);
        var imageEngine = ImageEngine;
        var images = new ImageService(imageEngine, new RegistryService(rig.Db, rig.Protector, imageEngine), rig.Options, new ImageKit.NoHttp(), NullLogger<ImageService>.Instance, rig.Db);
        var updates = rig.Updates = new UpdateService(new ImageKit.UpdateSteps(_kitDb), images, rig.Db, NullLogger<UpdateService>.Instance);
        var previews = new PreviewService(rig.Db, rig.Actions, rig.Apps, rig.Domains, new GitHubPulls(new ImageKit.NoHttp(), rig.Settings),
            new GitClient(rig.Options, rig.Settings), rig.Cli, rig.Activity, new WatchState(), NullLogger<PreviewService>.Instance, gate: rig.PreviewGate);
        var crons = new CronScheduler(rig.Db, rig.Cli, new ImageKit.NoHttp(), rig.Options, NullLogger<CronScheduler>.Instance);
        var down = new DownStates();
        var uptimeSettings = new UptimeSettings(rig.Settings, rig.Db, rig.Domains, down, Audit);
        var store = new UptimeStore(rig.Options);
        var notifications = new NotificationSettings(rig.Settings, Audit);
        var notifier = new Notifier(notifications, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), rig.Settings, NullLogger<Notifier>.Instance);
        var monitor = new UptimeMonitor(rig.Db, store, new UptimeChecker(new ImageKit.NoHttp()), new UptimeCertificates(), rig.Settings, rig.Domains, notifier, down,
            rig.Options, NullLogger<UptimeMonitor>.Instance);

        var s = new ServiceCollection();
        s.AddSingleton(rig.Db);
        s.AddSingleton(rig.Options);
        s.AddSingleton(rig.Settings);
        s.AddSingleton(rig.Protector);
        s.AddSingleton(rig.Cli);
        s.AddSingleton(rig.Api);
        s.AddSingleton(rig.Domains);
        s.AddSingleton(rig.Apps);
        s.AddSingleton(rig.Book);
        s.AddSingleton(rig.Stats);
        s.AddSingleton(rig.Catalog);
        s.AddSingleton(rig.Actions);
        s.AddSingleton(rig.Databases);
        s.AddSingleton(rig.Activity);
        s.AddSingleton(Audit);
        s.AddSingleton(new AuditSettings(rig.Settings, Audit));
        s.AddSingleton(settings);
        s.AddSingleton(images);
        s.AddSingleton(updates);
        s.AddSingleton(previews);
        s.AddSingleton(crons);
        s.AddSingleton(uptimeSettings);
        s.AddSingleton(store);
        s.AddSingleton(monitor);
        s.AddSingleton(new StatusPage(rig.Db, store, monitor, uptimeSettings));
        s.AddSingleton(notifications);
        s.AddSingleton(Attention);
        s.AddSingleton(new ComposeApps(rig.Db, rig.Apps, rig.Actions, rig.Protector, rig.Cli, rig.Health, rig.Stats, rig.Options, NullLogger<ComposeApps>.Instance));
        s.AddSingleton(new DeployHooks(rig.Db, rig.Protector, rig.Catalog, Audit));
        s.AddSingleton(new DatabaseServers(rig.Cli, rig.Settings, rig.Options, NullLogger<DatabaseServers>.Instance));
        s.AddSingleton(new GitClient(rig.Options, rig.Settings));
        s.AddSingleton(new ProjectAnalyzer());
        s.AddSingleton(new RepoVariables(new GitHubApi(new ImageKit.NoHttp()), rig.Settings));
        // Templates install as the gallery installs them, on the rig's own services.
        s.AddSingleton(TemplateCatalog.Bundled);
        s.AddSingleton(sp => new TemplateInstaller(TemplateCatalog.Bundled, sp.GetRequiredService<ComposeApps>(), rig.Apps, rig.Actions, rig.Databases,
            sp.GetRequiredService<DatabaseServers>(), rig.Domains, NullLogger<TemplateInstaller>.Instance));
        // New apps from an image aren't made here.
        s.AddSingleton(new Lazy<AppCreator>(() => throw new InvalidOperationException("No new apps from images here.")));
        s.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        s.AddSingleton<AgentContext>();
        foreach (var type in ToolCatalog.Types) s.AddSingleton(type);
        Services = s.BuildServiceProvider();
    }

    public AppsRig Rig { get; } = new();
    /// <summary>The image calls ImageService makes here (pulls among them); no network for it: Docker Hub can't be reached.</summary>
    public ImageKit.Engine ImageEngine { get; } = new();
    public AuditLog Audit { get; } = new();
    /// <summary>What the dashboard's banners say, which get_health tells too.</summary>
    public AttentionBoard Attention { get; } = new();
    public ServiceProvider Services { get; }

    public void Dispose()
    {
        Services.Dispose();
        _kitDb.Dispose();
        Rig.Dispose();
    }
}
