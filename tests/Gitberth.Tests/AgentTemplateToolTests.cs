using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Agents;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Templates;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// App templates through the tools: list_templates lists the catalog with each template's inputs, and create_app's template
/// source installs one as New app's Templates gallery does (<see cref="TemplateInstaller"/>), its secrets made and never
/// returned, its first deploy the token's.
/// </summary>
public sealed class AgentTemplateToolTests : IDisposable
{
    private readonly AgentsRig _agents = new();

    public void Dispose() => _agents.Dispose();

    private static readonly ApiCaller Admin = new(1, "ci-agent", ApiAccess.Admin, AuditVia.Api, "10.0.0.9");
    private static readonly ApiCaller Reader = new(3, "reader", ApiAccess.Read, AuditVia.Mcp, null);

    private Task<(int Status, string Body)> CallAsync(string tool, object? args, ApiCaller caller) =>
        CallJsonAsync(tool, args is null ? "{}" : JsonSerializer.Serialize(args), caller);

    private Task<(int Status, string Body)> CallJsonAsync(string tool, string json, ApiCaller caller)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return AgentEndpoints.InvokeAsync(ToolCatalog.Find(tool) ?? throw new InvalidOperationException(tool), caller, dict, _agents.Services, CancellationToken.None);
    }

    private static string Error(string body) => JsonNode.Parse(body)!["error"]!.GetValue<string>();

    [Fact]
    public async Task List_templates_lists_the_catalog_with_each_ones_inputs_and_what_it_needs()
    {
        var (status, body) = await CallAsync("list_templates", null, Reader);
        Assert.Equal(200, status);
        var list = JsonNode.Parse(body)!["templates"]!.AsArray();
        Assert.Equal(TemplateCatalog.Bundled.All.Select(t => t.Id).Order(), list.Select(t => t!["id"]!.GetValue<string>()).Order());
        var grafana = list.Single(t => t!["id"]!.GetValue<string>() == "grafana")!;
        var input = grafana["inputs"]!.AsArray().Single()!;
        Assert.Equal("ADMIN_USER", input["name"]!.GetValue<string>());
        Assert.Equal("admin", input["default"]!.GetValue<string>());
        Assert.True(input["required"]!.GetValue<bool>());
        Assert.Equal(["ADMIN_PASSWORD"], grafana["generated_secrets"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Null(grafana["needs"]!["postgres_server"]);
        var miniflux = list.Single(t => t!["id"]!.GetValue<string>() == "miniflux")!;
        Assert.True(miniflux["needs"]!["postgres_server"]!.GetValue<bool>());
        Assert.Equal(8080, miniflux["web"]!["port"]!.GetValue<int>());
        // Which ones are open until someone sets them up: exactly the catalog's.
        Assert.Equal(TemplateCatalog.Bundled.All.Where(t => t.OpenUntilSetup).Select(t => t.Id).Order(),
            list.Where(t => t!["open_until_setup"]?.GetValue<bool>() == true).Select(t => t!["id"]!.GetValue<string>()).Order());
        Assert.Null(grafana["open_until_setup"]);
    }

    [Fact]
    public async Task List_templates_searches_and_takes_a_category_in_any_case()
    {
        var (_, feeds) = await CallAsync("list_templates", new { query = "feed reader" }, Reader);
        var ids = JsonNode.Parse(feeds)!["templates"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("miniflux", ids);
        Assert.DoesNotContain("grafana", ids);

        var (status, monitoring) = await CallAsync("list_templates", new { category = "monitoring" }, Reader);
        Assert.Equal(200, status);
        var found = JsonNode.Parse(monitoring)!["templates"]!.AsArray();
        Assert.NotEmpty(found);
        Assert.All(found, t => Assert.Equal("Monitoring", t!["category"]!.GetValue<string>()));

        var (bad, why) = await CallAsync("list_templates", new { category = "no such thing" }, Reader);
        Assert.Equal(400, bad);
        Assert.Contains("Monitoring", Error(why));
    }

    [Fact]
    public async Task A_template_installs_as_the_gallery_installs_it_and_its_secret_never_comes_back()
    {
        var (status, body) = await CallAsync("create_app", new { source = "template", template = "grafana", inputs = new { ADMIN_USER = "boss" } }, Admin);
        Assert.True(status == 200, body);
        var result = JsonNode.Parse(body)!;
        Assert.Equal("grafana", result["template"]!.GetValue<string>());
        Assert.NotEmpty(result["notes"]!.AsArray());
        var id = result["id"]!.GetValue<int>();
        var deployment = result["deployment_id"]!.GetValue<int>();

        await using var db = _agents.Rig.Db.CreateDbContext();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal("grafana", app.TemplateId);
        Assert.Equal(AppSource.Compose, app.Source);
        var vars = await db.EnvVars.AsNoTracking().Where(v => v.AppId == id).ToListAsync();
        Assert.Equal("boss", _agents.Rig.Protector.Unprotect(vars.Single(v => v.Key == "ADMIN_USER").ProtectedValue));
        var password = vars.Single(v => v.Key == "ADMIN_PASSWORD");
        Assert.True(password.IsSecret);
        var made = _agents.Rig.Protector.Unprotect(password.ProtectedValue);
        Assert.Equal(20, made.Length);
        Assert.DoesNotContain(made, body);

        // Its first deploy was asked for by the token.
        var dep = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == deployment);
        Assert.Equal(id, dep.AppId);
        Assert.Equal(AuditActors.Format(Admin.Actor), dep.RequestedBy);
    }

    [Fact]
    public async Task A_second_install_without_a_name_gets_a_free_one()
    {
        var (first, one) = await CallAsync("create_app", new { source = "template", template = "it-tools" }, Admin);
        Assert.True(first == 200, one);
        var (second, two) = await CallAsync("create_app", new { source = "template", template = "it-tools" }, Admin);
        Assert.True(second == 200, two);
        Assert.NotEqual(JsonNode.Parse(one)!["key"]!.GetValue<string>(), JsonNode.Parse(two)!["key"]!.GetValue<string>());
        Assert.Contains("IT-Tools 2", JsonNode.Parse(two)!["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"source":"template","template":"no-such-template"}""", 404, "list_templates")]
    [InlineData("""{"source":"template"}""", 400, "list_templates")]
    [InlineData("""{"source":"template","template":"grafana","inputs":{"NOPE":"x"}}""", 400, "ADMIN_USER")]
    [InlineData("""{"source":"template","template":"grafana","inputs":{"ADMIN_USER":["a"]}}""", 400, "ADMIN_USER")]
    [InlineData("""{"source":"template","template":"grafana","env":[{"name":"A","value":"b"}]}""", 400, "inputs, not env")]
    [InlineData("""{"source":"image","image":"nginx:1.27","template":"grafana"}""", 400, "source template")]
    [InlineData("""{"source":"git","repo":"https://github.com/octocat/hello","domain":"x.example.com"}""", 400, "add_domain")]
    public async Task A_template_call_that_cant_work_is_refused_with_what_to_do_and_makes_nothing(string json, int expected, string said)
    {
        var (status, body) = await CallJsonAsync("create_app", json, Admin);
        Assert.True(status == expected, body);
        Assert.Contains(said, Error(body));
        await using var db = _agents.Rig.Db.CreateDbContext();
        Assert.False(await db.Apps.AnyAsync(a => a.TemplateId != null));
    }

    [Fact]
    public void A_read_token_may_list_templates_and_only_an_admin_token_installs_or_sets_up_a_server()
    {
        var audit = new AuditLog();
        Assert.Null(AgentEndpoints.Refusal(Reader, ToolCatalog.Find("list_templates")!, audit));
        Assert.Contains("needs Admin", AgentEndpoints.Refusal(Reader, ToolCatalog.Find("create_app")!, audit));
        var operate = new ApiCaller(4, "ops", ApiAccess.Operate, AuditVia.Api, null);
        Assert.Contains("needs Admin", AgentEndpoints.Refusal(operate, ToolCatalog.Find("create_postgres_server")!, audit));
        Assert.Null(AgentEndpoints.Refusal(Admin, ToolCatalog.Find("create_postgres_server")!, audit));
    }
}
