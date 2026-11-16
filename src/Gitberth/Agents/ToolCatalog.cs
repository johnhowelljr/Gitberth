using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Gitberth.Data;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Gitberth.Agents;

/// <summary>The least access a token needs to call a tool. Every tool has one (a unit test fails when a tool has none).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAccessAttribute(ApiAccess access) : Attribute
{
    public ApiAccess Access { get; } = access;
}

/// <summary>
/// A tool's refusal or failure, with the plain sentence the UI shows for the same thing. Over MCP it's a tool error with
/// that message (the model reads it and can correct itself); over the HTTP API it's {"error": message} with <see cref="Status"/>.
/// </summary>
public sealed class ToolError(string message, int status = StatusCodes.Status400BadRequest) : McpException(message)
{
    public int Status { get; } = status;

    public static ToolError NotFound(string message) => new(message, StatusCodes.Status404NotFound);

    public static ToolError Conflict(string message) => new(message, StatusCodes.Status409Conflict);

    public static ToolError Forbidden(string message) => new(message, StatusCodes.Status403Forbidden);
}

/// <summary>How tools read their arguments and write their results: snake_case, enums as snake_case words, nulls left out.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.MakeReadOnly();
        return o;
    }

    public static string Write(object? value) => value switch
    {
        null => "null",
        string s => s,
        _ => JsonSerializer.Serialize(value, value.GetType(), Options),
    };
}

/// <summary>One tool, as MCP lists it and the HTTP API serves it: the same function behind both.</summary>
/// <param name="Function">Binds the arguments, runs the tool's method, and turns a failure into a <see cref="ToolError"/>.</param>
/// <param name="Mcp">The same function, as the MCP server registers it (with its annotations).</param>
public sealed record AgentTool(string Name, string Title, string Description, ApiAccess Access, bool ReadOnly, bool Destructive, bool Idempotent,
    AIFunction Function, McpServerTool Mcp)
{
    public JsonElement InputSchema => Function.JsonSchema;

    public ToolAnnotations Annotations => Mcp.ProtocolTool.Annotations ?? new ToolAnnotations();
}

/// <summary>
/// Every tool AI agents and scripts have, found on the tool classes (<see cref="Types"/>) by their [McpServerTool] methods:
/// its name (snake_case), description (written for a model), annotations and access level ([ToolAccess]). The MCP server
/// registers <see cref="AgentTool.Mcp"/>; the HTTP API lists and calls <see cref="AgentTool.Function"/>, the same function.
/// Each tool's class is resolved from the request's services, and its methods call the same services the UI calls.
/// </summary>
public static class ToolCatalog
{
    /// <summary>The classes that hold tools. A new one goes here.</summary>
    public static readonly Type[] Types =
    [
        typeof(Tools.AppReadTools), typeof(Tools.SystemReadTools), typeof(Tools.OperateTools), typeof(Tools.AppAdminTools),
        typeof(Tools.NetworkAdminTools), typeof(Tools.SettingsAdminTools),
    ];

    private static readonly Lazy<IReadOnlyList<AgentTool>> all = new(Build);
    private static readonly Lazy<IReadOnlyDictionary<string, AgentTool>> byName =
        new(() => all.Value.ToDictionary(t => t.Name, StringComparer.Ordinal));

    public static IReadOnlyList<AgentTool> All => all.Value;

    public static AgentTool? Find(string? name) => name is not null && byName.Value.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>The tools a token with <paramref name="access"/> may call.</summary>
    public static IEnumerable<AgentTool> For(ApiAccess access) => All.Where(t => access >= t.Access);

    /// <summary>The methods that are tools, with their class, for the tests that check every one has an access level.</summary>
    public static IEnumerable<(Type Type, MethodInfo Method, McpServerToolAttribute Tool)> Methods() =>
        from type in Types
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        let tool = method.GetCustomAttribute<McpServerToolAttribute>()
        where tool is not null
        orderby type.Name, method.MetadataToken
        select (type, method, tool);

    private static IReadOnlyList<AgentTool> Build()
    {
        var tools = new List<AgentTool>();
        foreach (var (type, method, attr) in Methods())
        {
            var name = attr.Name ?? throw new InvalidOperationException($"{type.Name}.{method.Name} has no tool name.");
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            // A tool without an explicit level needs the most: Admin.
            var access = method.GetCustomAttribute<ToolAccessAttribute>()?.Access ?? ApiAccess.Admin;
            var inner = AIFunctionFactory.Create(method, args => Resolve(args, type), new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = ToolJson.Options,
                ExcludeResultSchema = true,
                // The tool's result goes out as the JSON text it serialises to (one text block over MCP, the body over HTTP).
                MarshalResult = static (result, _, _) => new ValueTask<object?>(ToolJson.Write(result)),
            });
            var function = new ToolFunction(inner);
            var mcp = McpServerTool.Create(function, new McpServerToolCreateOptions
            {
                Name = name,
                Title = attr.Title,
                Description = description,
                ReadOnly = attr.ReadOnly,
                Destructive = attr.Destructive,
                Idempotent = attr.Idempotent,
                OpenWorld = attr.OpenWorld,
                SerializerOptions = ToolJson.Options,
            });
            tools.Add(new AgentTool(name, attr.Title ?? name, description, access, attr.ReadOnly, attr.Destructive, attr.Idempotent, function, mcp));
        }
        var duplicate = tools.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Two tools are called {duplicate.Key}.");
        return tools;
    }

    private static object Resolve(AIFunctionArguments args, Type type) =>
        (args.Services ?? throw new InvalidOperationException("A tool was called without services."))
        .GetService(type) ?? ActivatorUtilities.CreateInstance(args.Services, type);
}

/// <summary>
/// A tool's function as both MCP and the HTTP API call it: any failure becomes a <see cref="ToolError"/> with the plain
/// sentence the UI would show (the services' own messages), so over MCP the model reads why rather than "an error occurred".
/// </summary>
internal sealed class ToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
{
    // The arguments the tool takes, from its input schema: any other is refused, never quietly dropped.
    private readonly string[] _takes = inner.JsonSchema.ValueKind == JsonValueKind.Object && inner.JsonSchema.TryGetProperty("properties", out var props)
        && props.ValueKind == JsonValueKind.Object ? props.EnumerateObject().Select(p => p.Name).ToArray() : [];

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (arguments.Keys.FirstOrDefault(k => !_takes.Contains(k, StringComparer.Ordinal)) is { } unknown)
            throw new ToolError(_takes.Length == 0 ? $"{Name} takes no arguments, so not {unknown}."
                : $"{Name} doesn't take {unknown}. It takes {string.Join(", ", _takes)}.");
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (ToolError)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new ToolError(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (JsonException ex)
        {
            throw new ToolError($"An argument isn't what {Name} expects: {ex.Message}", StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolError(ex.Message, StatusCodes.Status409Conflict);
        }
        catch (Exception ex)
        {
            throw new ToolError(ex.Message, StatusCodes.Status500InternalServerError);
        }
    }
}
