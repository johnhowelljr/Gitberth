using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Compose;

/// <summary>
/// What a Compose app's deploy ran, as Deployment.Images keeps it: each service's image as its file names it (a tag, or the
/// tag Gitberth built), and, once the deploy brought it up, the image ID each service's container ran. A pulled image's tag
/// can move on (a later pull of redis:8, :latest), so a rollback or a deploy without a build runs the image by its ID
/// again, through a tag of the app's own, gitberth/&lt;slug&gt;/&lt;service&gt;:pin-&lt;id&gt;, which also keeps it on this host
/// while a kept release needs it.
/// </summary>
/// <remarks>Stored as {"images": {service: ref}, "ids": {service: "sha256:…"}}; before 1.3's review, {service: ref} alone.</remarks>
public sealed record ComposeImages(IReadOnlyDictionary<string, string> Refs, IReadOnlyDictionary<string, string> Ids)
{
    public static readonly ComposeImages None = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>A deploy's images; <see cref="None"/> when it has none or they can't be read.</summary>
    public static ComposeImages Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return None;
            if (root["images"] is JsonObject refs && root["ids"] is JsonObject ids)
                return new ComposeImages(Strings(refs), Strings(ids));
            return new ComposeImages(Strings(root), new Dictionary<string, string>());
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static Dictionary<string, string> Strings(JsonObject node) =>
        node.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _)).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);

    public string ToJson() => new JsonObject
    {
        ["images"] = new JsonObject(Refs.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        ["ids"] = new JsonObject(Ids.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
    }.ToJsonString();

    /// <summary>
    /// The services this deploy brought up, by name: the ones whose container it found once they were up. A service Compose
    /// never creates (one behind a profile, or with no replicas) has none, so it isn't a container gone. A deploy recorded
    /// before the IDs were kept names every service of its file.
    /// </summary>
    public IReadOnlyList<string> BroughtUp => (Ids.Count > 0 ? Ids.Keys : Refs.Keys).Order(StringComparer.Ordinal).ToList();

    /// <summary>The app's own tag for the image a service ran: gitberth/&lt;slug&gt;/&lt;service&gt;:pin-&lt;first 12 of its ID&gt;.</summary>
    public static string PinTag(string slug, string service, string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return $"{ComposeNames.ImageRepository(slug, service)}:pin-{hex[..Math.Min(12, hex.Length)]}";
    }

    /// <summary>Whether a service's image is one Gitberth built for the app (its tags are the app's own, one per commit).</summary>
    public static bool BuiltFor(string slug, string image) => image.StartsWith($"gitberth/{slug}/", StringComparison.Ordinal);

    /// <summary>The pins of the services whose image this deploy pulled: the tags that keep their images (for pruning).</summary>
    public IEnumerable<string> PinTags(string slug) =>
        Ids.Where(p => Refs.TryGetValue(p.Key, out var image) && !BuiltFor(slug, image)).Select(p => PinTag(slug, p.Key, p.Value));

    /// <summary>
    /// The services whose pulled image's tag now names another image than the one this deploy ran (or none): they run by
    /// their pin. <paramref name="currentId"/> is the image ID a tag names on this host now (null: none).
    /// </summary>
    public Dictionary<string, string> Moved(string slug, Func<string, string?> currentId)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (service, id) in Ids)
        {
            if (!Refs.TryGetValue(service, out var image) || BuiltFor(slug, image)) continue;
            if (currentId(image) != id) pins[service] = PinTag(slug, service, id);
        }
        return pins;
    }
}
