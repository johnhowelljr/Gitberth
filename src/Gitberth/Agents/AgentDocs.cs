using System.Text;
using System.Text.Json;
using Gitberth.Data;

namespace Gitberth.Agents;

/// <summary>
/// The tools' reference as docs/reference/api.md has it between its markers, made from the same list the MCP server and
/// the HTTP API serve: a unit test checks the file says exactly this, so the docs never drift from the tools.
/// </summary>
public static class AgentDocs
{
    public const string Start = "<!-- tools:start (made from the tools themselves: a unit test checks it) -->";
    public const string End = "<!-- tools:end -->";

    public static string Reference()
    {
        var sb = new StringBuilder();
        sb.Append(Start).Append('\n');
        foreach (var access in new[] { ApiAccess.Read, ApiAccess.Operate, ApiAccess.Admin })
        {
            sb.Append('\n').Append("## ").Append(access).Append(" tools").Append('\n');
            foreach (var tool in ToolCatalog.All.Where(t => t.Access == access))
            {
                sb.Append('\n').Append("### `").Append(tool.Name).Append("`\n\n");
                sb.Append(tool.Description).Append("\n\n");
                var hints = new List<string>();
                if (tool.ReadOnly) hints.Add("read-only");
                if (tool.Destructive) hints.Add("destructive: needs `confirm`");
                if (tool.Idempotent && !tool.ReadOnly) hints.Add("idempotent");
                sb.Append("Access: **").Append(tool.Access).Append("**");
                if (hints.Count > 0) sb.Append(" · ").Append(string.Join(", ", hints));
                sb.Append("\n\n");
                var args = Arguments(tool.InputSchema);
                if (args.Count == 0)
                {
                    sb.Append("No arguments.\n");
                    continue;
                }
                sb.Append("| Argument | Type | Required | What it is |\n|---|---|---|---|\n");
                foreach (var (name, type, required, description) in args)
                    sb.Append("| `").Append(name).Append("` | ").Append(type).Append(" | ").Append(required ? "yes" : "no").Append(" | ")
                        .Append(description.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append(" |\n");
            }
        }
        sb.Append('\n').Append(End).Append('\n');
        return sb.ToString();
    }

    /// <summary>Each argument of a tool's input schema: its name, type, whether it's required, and its description.</summary>
    public static List<(string Name, string Type, bool Required, string Description)> Arguments(JsonElement schema)
    {
        var list = new List<(string, string, bool, string)>();
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return list;
        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var p in props.EnumerateObject())
        {
            var description = p.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            list.Add((p.Name, TypeOf(p.Value), required.Contains(p.Name), description));
        }
        return list;
    }

    private static string TypeOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return "any";
        var names = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).Where(t => t is not null and not "null").Select(t => t!).ToList()
            : [type.GetString() ?? "any"];
        var name = names.Count == 0 ? "any" : string.Join(" or ", names);
        if (names.Contains("array") && schema.TryGetProperty("items", out var items))
            return TypeOf(items) is var item && item is not ("object" or "any") ? $"array of {item}s" : "array of objects";
        return name;
    }
}
