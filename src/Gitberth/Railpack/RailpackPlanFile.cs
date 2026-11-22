using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Railpack;

/// <summary>
/// The plan railpack prepare writes (railpack-plan.json), as Gitberth edits and reads it before Railpack's frontend builds
/// it. Pure: it works on the JSON alone. The shape is Railpack 0.43.0's (core/plan), so a Railpack bump checks it again.
/// </summary>
public static class RailpackPlanFile
{
    private const string All = "*";

    /// <summary>The plan as a JSON object; null when it isn't one.</summary>
    public static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The plan as the frontend reads it, indented as Railpack writes it.</summary>
    public static string Write(JsonObject plan) => plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Gitberth's edits, in place: <c>.git</c> excluded (Railpack 0.43.0 copies the clone's into the image), and every
    /// step that names a subset of the secrets given all of them (<c>"*"</c>): for a subset the frontend hashes the values in
    /// an unpinned <c>alpine:latest</c> that sees them. Without the build cache, every step that runs a command takes all
    /// of them too, so the salted secrets-hash reaches each one and every step runs again (the frontend ignores
    /// --no-cache). Names in <paramref name="leftOut"/> come out of the plan's secrets: the build never gets them.
    /// </summary>
    public static JsonObject Adjust(JsonObject plan, bool noCache, IReadOnlyCollection<string>? leftOut = null)
    {
        if (plan["exclude"] is not JsonArray exclude) plan["exclude"] = exclude = [];
        if (!Strings(exclude).Contains(".git", StringComparer.Ordinal)) exclude.Add(".git");

        foreach (var step in Steps(plan))
        {
            var secrets = Strings(step["secrets"] as JsonArray).ToList();
            var subset = secrets.Count > 0 && !(secrets.Count == 1 && secrets[0] == All);
            var runs = Commands(step).Any(c => c.ContainsKey("cmd"));
            if (subset || noCache && runs) step["secrets"] = new JsonArray(All);
        }

        if (leftOut is { Count: > 0 } && plan["secrets"] is JsonArray named)
            plan["secrets"] = new JsonArray(Strings(named).Where(n => !leftOut.Contains(n)).Select(n => (JsonNode?)n).ToArray());
        return plan;
    }

    /// <summary>The command that starts the app: deploy.startCommand; null when it has none.</summary>
    public static string? StartCommand(JsonObject plan) =>
        plan["deploy"] is JsonObject deploy && deploy["startCommand"] is JsonValue start && start.TryGetValue<string>(out var text) && text.Trim().Length > 0
            ? text
            : null;

    /// <summary>The secrets the build must be given (secrets[]): the names prepare was given, and any the repo's railpack.json asks for.</summary>
    public static IReadOnlyList<string> Secrets(JsonObject plan) => Strings(plan["secrets"] as JsonArray).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every image the build pulls: the steps' input images, the deploy's base image, and images commands copy from.</summary>
    public static IReadOnlyList<string> Images(JsonObject plan)
    {
        var images = new List<string>();
        void Add(JsonNode? node)
        {
            if (node is JsonObject o && o["image"] is JsonValue v && v.TryGetValue<string>(out var image) && image.Length > 0 && !images.Contains(image)) images.Add(image);
        }
        foreach (var step in Steps(plan))
        {
            if (step["inputs"] is JsonArray inputs) foreach (var input in inputs) Add(input);
            foreach (var command in Commands(step)) Add(command);
        }
        if (plan["deploy"] is JsonObject deploy)
        {
            Add(deploy["base"]);
            if (deploy["inputs"] is JsonArray inputs) foreach (var input in inputs) Add(input);
        }
        return images;
    }

    private static IEnumerable<JsonObject> Steps(JsonObject plan) => plan["steps"] is JsonArray steps ? steps.OfType<JsonObject>() : [];

    private static IEnumerable<JsonObject> Commands(JsonObject step) => step["commands"] is JsonArray commands ? commands.OfType<JsonObject>() : [];

    private static IEnumerable<string> Strings(JsonArray? array) =>
        array is null ? [] : array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>();
}
