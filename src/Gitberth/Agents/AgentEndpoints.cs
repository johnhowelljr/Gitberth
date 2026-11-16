using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Data;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Gitberth.Agents;

/// <summary>
/// AI agents and scripts: the MCP server at /mcp (stateless Streamable HTTP, on Gitberth's own port) and the same tools
/// over plain HTTP at /api/v1, both behind API tokens (Settings › AI agents and API).
/// </summary>
public static class AgentEndpoints
{
    /// <summary>What the MCP server tells a model about itself when it connects.</summary>
    public const string Instructions =
        "Gitberth deploys and runs apps on one Docker host: Git repos (built and deployed on every push), registry images, Compose files and containers made by hand. " +
        "Start with list_apps; name an app in other tools by its key from there (gb-web), or its name. get_app shows everything about one app. " +
        "Deploys and updates run in the background: deploy_app returns a deployment_id at once (get_deployment follows it), or pass wait to get the outcome. " +
        "Tools that stop, delete, roll back or turn previews off need confirm set to the app's name (or the domain), to show it's meant. " +
        "Every change is recorded in the audit log under this token's name (query_audit_log reads it). Secret values are never returned.";

    /// <summary>The services, the token scheme and its policy, and the MCP server with every tool.</summary>
    public static IServiceCollection AddGitberthAgents(this IServiceCollection services)
    {
        services.AddSingleton<ApiTokens>();
        services.AddSingleton<ApiTokenThrottle>();
        services.AddSingleton<ToolDenials>();
        services.AddHostedService<ApiTokenExpiry>();
        services.AddSingleton<AgentContext>();
        foreach (var type in ToolCatalog.Types) services.AddSingleton(type);
        services.AddAuthentication().AddScheme<ApiTokenOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, null);
        // The only policy that names the token's scheme, and it names nothing else: the cookie never counts on /mcp or /api.
        services.AddAuthorization(o => o.AddPolicy(ApiTokenHandler.Policy, p => p.AddAuthenticationSchemes(ApiTokenHandler.SchemeName).RequireAuthenticatedUser()));
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "gitberth", Title = "Gitberth", Version = AppVersion.Number };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(ToolCatalog.All.Select(t => t.Mcp))
            .WithRequestFilters(filters =>
            {
                // A token is shown the tools its access lets it call.
                filters.AddListToolsFilter(next => async (context, ct) =>
                {
                    var result = await next(context, ct);
                    var caller = ApiCaller.From(context.User);
                    result.Tools = result.Tools.Where(t => caller is not null && ToolCatalog.Find(t.Name) is { } tool && caller.May(tool.Access)).ToList();
                    return result;
                });
                // Each call: the access it needs, then the tool as the token's (the audit log's actor) for whatever it changes.
                filters.AddCallToolFilter(next => async (context, ct) =>
                {
                    var caller = ApiCaller.From(context.User);
                    if (caller is null) return Failed("Send a token: Authorization: Bearer gbt_….");
                    var tool = ToolCatalog.Find(context.Params?.Name);
                    if (tool is not null && Refusal(caller, tool, context.Services?.GetService<AuditLog>(), context.Services?.GetService<ToolDenials>()) is { } refused)
                        return Failed(refused);
                    using (AuditScope.Begin(caller.Actor))
                        return await next(context, ct);
                });
            });
        return services;
    }

    /// <summary>/mcp, /api/v1/tools, /api/v1/tools/{name} and /api/v1/openapi.json, behind the token's policy; anything else under /api is a JSON 404.</summary>
    public static void MapGitberthAgents(this WebApplication app)
    {
        app.MapMcp(AgentPaths.Mcp).RequireAuthorization(ApiTokenHandler.Policy).DisableAntiforgery();
        var api = app.MapGroup(AgentPaths.ApiV1).RequireAuthorization(ApiTokenHandler.Policy).DisableAntiforgery();
        api.MapGet("/tools", (HttpContext context) => Results.Json(ListTools(ApiCaller.From(context.User)), ToolJson.Options));
        api.MapPost("/tools/{name}", CallAsync);
        api.MapGet("/openapi.json", async (HttpContext context, AgentContext agents) =>
            Results.Content(OpenApi(await agents.RootAsync(context.RequestAborted)).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json; charset=utf-8"));
        app.Map(AgentPaths.Api + "/{**rest}", (HttpContext context) =>
            AgentPaths.WriteErrorAsync(context, StatusCodes.Status404NotFound, "There's nothing here. GET /api/v1/tools lists what there is.")).AllowAnonymous().DisableAntiforgery();
    }

    /// <summary>
    /// Why the caller can't call the tool, or null when it can. A refusal is recorded in the audit log at most once a minute
    /// per token and tool (<paramref name="denials"/>), the next entry saying how many weren't recorded one by one; without
    /// <paramref name="denials"/> every one is.
    /// </summary>
    internal static string? Refusal(ApiCaller caller, AgentTool tool, AuditLog? audit, ToolDenials? denials = null)
    {
        if (caller.May(tool.Access)) return null;
        var message = $"The token {caller.Name} has {caller.Access} access, and {tool.Name} needs {tool.Access}. " +
            $"A person can make a token with {tool.Access} access in Gitberth's Settings › AI agents and API.";
        if (audit is not null && (denials is null ? 0 : denials.ShouldRecord(caller.TokenId, tool.Name)) is int unrecorded)
            audit.Record(new AuditEntry("auth.token.denied", $"Refused {tool.Name} to the token {caller.Name}: it has {caller.Access} access, and {tool.Name} needs {tool.Access}."
                + (unrecorded > 0 ? $" It was refused {unrecorded} more time{(unrecorded == 1 ? "" : "s")} since the last entry about it, not recorded one by one." : ""))
            {
                Actor = caller.Actor, Outcome = AuditOutcome.Denied, Target = tool.Name,
                Changes = unrecorded > 0 ? [new AuditChange("Refused since the last entry", null, unrecorded.ToString(System.Globalization.CultureInfo.InvariantCulture))] : null,
            });
        return message;
    }

    private static CallToolResult Failed(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    /// <summary>GET /api/v1/tools: every tool, its description, access level, hints and input schema, and whether this token may call it.</summary>
    internal static object ListTools(ApiCaller? caller) => new
    {
        tools = ToolCatalog.All.Select(t => new
        {
            name = t.Name,
            title = t.Title,
            description = t.Description,
            access = t.Access.ToString(),
            allowed = caller?.May(t.Access) ?? false,
            read_only = t.ReadOnly,
            destructive = t.Destructive,
            idempotent = t.Idempotent,
            input_schema = t.InputSchema,
        }).ToList(),
    };

    /// <summary>POST /api/v1/tools/{name}: the arguments as a JSON object; the tool's JSON result, or {"error": …} with a status code.</summary>
    internal static async Task CallAsync(HttpContext context, string name)
    {
        var tool = ToolCatalog.Find(name);
        if (tool is null)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"There's no tool called {name}. GET /api/v1/tools lists them.");
            return;
        }
        var caller = ApiCaller.From(context.User);
        if (caller is null)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Send a token: Authorization: Bearer gbt_….");
            return;
        }
        if (Refusal(caller, tool, context.RequestServices.GetService<AuditLog>(), context.RequestServices.GetService<ToolDenials>()) is { } refused)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status403Forbidden, refused);
            return;
        }
        Dictionary<string, object?> args;
        try
        {
            args = await ReadArgumentsAsync(context.Request, context.RequestAborted);
        }
        catch (JsonException ex)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                ex.Message.StartsWith("The body", StringComparison.Ordinal) ? ex.Message : "The body must be a JSON object of the tool's arguments ({} for none), at most 1 MB.");
            return;
        }
        var (status, body) = await InvokeAsync(tool, caller, args, context.RequestServices, context.RequestAborted);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>Runs a tool for the caller, as the token's audit actor: (200, its JSON) or (its error's status, {"error": …}).</summary>
    public static async Task<(int Status, string Body)> InvokeAsync(AgentTool tool, ApiCaller caller, IDictionary<string, object?> args, IServiceProvider services,
        CancellationToken ct)
    {
        using (AuditScope.Begin(caller.Actor))
        {
            try
            {
                var result = await tool.Function.InvokeAsync(new AIFunctionArguments(args) { Services = services }, ct);
                return (StatusCodes.Status200OK, result as string ?? ToolJson.Write(result));
            }
            catch (ToolError e)
            {
                return (e.Status, new JsonObject { ["error"] = e.Message }.ToJsonString());
            }
        }
    }

    /// <summary>The most of a call's body that's read: a tool's arguments (a whole Compose file included) are far smaller.</summary>
    public const int MaxBody = 1024 * 1024;

    private static async Task<Dictionary<string, object?>> ReadArgumentsAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0) return [];
        if (request.ContentLength > MaxBody) throw new JsonException("Too large.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBody) throw new JsonException("Too large.");
            buffer.Write(chunk, 0, n);
        }
        // A body of nothing but spaces is no arguments, as an empty one is.
        if (buffer.Length == 0 || buffer.ToArray().All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return [];
        buffer.Position = 0;
        using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        if (doc.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Not an object.");
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            // Said once, so it means one thing: a name twice in the body is refused rather than one of them picked.
            if (!args.TryAdd(p.Name, p.Value.Clone())) throw new JsonException($"The body names {p.Name} twice: give each argument once.");
        return args;
    }

    /// <summary>An OpenAPI 3.1 document of the HTTP API, made from the same tool list the MCP server registers.</summary>
    public static JsonObject OpenApi(string root)
    {
        var paths = new JsonObject
        {
            ["/api/v1/tools"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["operationId"] = "list_tools",
                    ["summary"] = "List the tools",
                    ["description"] = "Every tool, its description, the access it needs, whether this token may call it, and its input schema.",
                    ["responses"] = Responses("The tools."),
                },
            },
        };
        foreach (var tool in ToolCatalog.All)
        {
            paths[$"/api/v1/tools/{tool.Name}"] = new JsonObject
            {
                ["post"] = new JsonObject
                {
                    ["operationId"] = tool.Name,
                    ["summary"] = tool.Title,
                    ["description"] = $"{tool.Description}\n\nNeeds a token with {tool.Access} access.",
                    ["tags"] = new JsonArray(tool.Access.ToString()),
                    ["x-gitberth-access"] = tool.Access.ToString(),
                    ["x-gitberth-read-only"] = tool.ReadOnly,
                    ["x-gitberth-destructive"] = tool.Destructive,
                    ["requestBody"] = new JsonObject
                    {
                        ["required"] = false,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = JsonNode.Parse(tool.InputSchema.GetRawText()) } },
                    },
                    ["responses"] = Responses("The tool's result, as JSON."),
                },
            };
        }
        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Gitberth API",
                ["version"] = AppVersion.Number,
                ["description"] = "The same tools Gitberth's MCP server (/mcp) gives AI agents, over plain HTTP: POST a JSON object of a tool's arguments to /api/v1/tools/{name}. " +
                    "Every request needs Authorization: Bearer gbt_… (a token made in Settings › AI agents and API). Every change is recorded in the audit log under the token's name.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = root.TrimEnd('/') }),
            ["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "A Gitberth API token: gbt_ and 43 characters." },
                },
                ["schemas"] = new JsonObject
                {
                    ["Error"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "string", ["description"] = "What went wrong, in one plain sentence." } },
                        ["required"] = new JsonArray("error"),
                    },
                },
            },
        };
    }

    private static JsonObject Responses(string ok)
    {
        JsonObject Error(string description) => new()
        {
            ["description"] = description,
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Error" } } },
        };
        return new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = ok,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } } },
            },
            ["400"] = Error("The arguments aren't what the tool takes, or it refused them."),
            ["401"] = Error("No token, or one that's unknown, revoked or expired."),
            ["403"] = Error("The token's access doesn't allow this tool, or the request came from a browser page (an Origin header)."),
            ["404"] = Error("No such tool, or what it names doesn't exist."),
            ["409"] = Error("It can't be done now, as the UI would say."),
            ["429"] = Error("Too many requests with a bad token from this address."),
            ["500"] = Error("Something failed on Gitberth's side; its log says what."),
        };
    }
}
