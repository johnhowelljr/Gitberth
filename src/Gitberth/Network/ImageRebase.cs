using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Network;

/// <summary>
/// Takes out of a create body what the old image supplied, so the new image's defaults apply (section 2.6). An inspect's
/// Config is the image's config with the container's settings merged on top: recreated on a new image with those merged
/// values, the container would keep the old image's PG_VERSION, PATH or command (the Watchtower problem). What the owner
/// set differently stays; the hostname always stays. Pure: JSON in, JSON out.
/// </summary>
public static class ImageRebase
{
    /// <summary>Rebases the body on <paramref name="newImage"/>, and returns the notes: "From the new image: PG_VERSION 18.0 → 18.1".</summary>
    /// <param name="body">The create body, as CloneSpec.Build and ContainerSettingsJson.Write made it.</param>
    /// <param name="oldImage">GET /images/{id}/json of the image the container ran.</param>
    /// <param name="newImage">GET /images/{ref}/json of the image it's recreated on.</param>
    public static IReadOnlyList<string> Apply(JsonObject body, JsonObject oldImage, JsonObject newImage)
    {
        var old = oldImage["Config"] as JsonObject ?? new JsonObject();
        var next = newImage["Config"] as JsonObject ?? new JsonObject();

        // Variables: one the old image set to exactly this value goes, so the new image's value (or none) applies.
        var oldEnv = Pairs(old["Env"]);
        var kept = (ContainerSettingsJson.StrList(body["Env"]) ?? []).Select(e => e.Split('=', 2)).Select(kv => (Key: kv[0], Value: kv.Length > 1 ? kv[1] : ""))
            .Where(kv => !(oldEnv.TryGetValue(kv.Key, out var v) && v == kv.Value)).ToList();
        if (body["Env"] is JsonArray) body["Env"] = new JsonArray(kept.Select(kv => (JsonNode)JsonValue.Create(kv.Key + "=" + kv.Value)).ToArray());

        // Labels: the same rule.
        if (body["Labels"] is JsonObject labels)
        {
            var oldLabels = ContainerSettingsJson.Map(old["Labels"]);
            foreach (var key in labels.Select(l => l.Key).ToList())
                if (oldLabels.TryGetValue(key, out var value) && Str(labels, key) == value) labels.Remove(key);
        }

        // Entrypoint and command: both the old image's, both go; the old image's entrypoint with a command of the
        // container's own, only the command stays; an entrypoint of its own, both stay as they are.
        var entrypointInherited = SameList(body["Entrypoint"], old["Entrypoint"]);
        var cmdInherited = SameList(body["Cmd"], old["Cmd"]);
        if (entrypointInherited)
        {
            body.Remove("Entrypoint");
            if (cmdInherited) body.Remove("Cmd");
        }

        foreach (var key in new[] { "WorkingDir", "User", "StopSignal" })
            if (body[key] is JsonValue && Str(body, key) == Str(old, key)) body.Remove(key);
        if (body["Healthcheck"] is JsonObject check && old["Healthcheck"] is JsonObject oldCheck && JsonNode.DeepEquals(check, oldCheck)) body.Remove("Healthcheck");
        if (body["Shell"] is JsonArray && SameList(body["Shell"], old["Shell"])) body.Remove("Shell");

        // Ports the old image exposed go, unless they're published: the new image exposes its own.
        if (body["ExposedPorts"] is JsonObject exposed)
        {
            var published = (body["HostConfig"]?["PortBindings"] as JsonObject ?? []).Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in (old["ExposedPorts"] as JsonObject ?? []).Select(p => p.Key))
                if (!published.Contains(key)) exposed.Remove(key);
            if (exposed.Count == 0) body.Remove("ExposedPorts");
        }

        // The old image's VOLUME paths: the anonymous volumes the container made are already mounted by name.
        if (body["Volumes"] is JsonObject volumes)
        {
            foreach (var key in (old["Volumes"] as JsonObject ?? []).Select(v => v.Key)) volumes.Remove(key);
            if (volumes.Count == 0) body.Remove("Volumes");
        }

        // What the new image changes, for the plan: its variables the container takes from it.
        var own = kept.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var (key, value) in Pairs(next["Env"]))
        {
            if (own.Contains(key)) continue;
            if (!oldEnv.TryGetValue(key, out var before)) notes.Add($"From the new image: {key} {value} (new)");
            else if (before != value) notes.Add($"From the new image: {key} {before} → {value}");
        }
        foreach (var (key, before) in oldEnv)
            if (!own.Contains(key) && !Pairs(next["Env"]).ContainsKey(key)) notes.Add($"From the new image: {key} {before} → (none)");
        return notes;
    }

    // KEY=value pairs in order; the last of a key wins, as Docker reads them.
    private static Dictionary<string, string> Pairs(JsonNode? env)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in ContainerSettingsJson.StrList(env) ?? [])
        {
            var kv = item.Split('=', 2);
            pairs[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        return pairs;
    }

    private static bool SameList(JsonNode? a, JsonNode? b) =>
        (ContainerSettingsJson.StrList(a) ?? []).SequenceEqual(ContainerSettingsJson.StrList(b) ?? [], StringComparer.Ordinal);

    private static string Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
}
