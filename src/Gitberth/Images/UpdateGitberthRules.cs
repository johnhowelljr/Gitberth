using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Gitberth.Images;

/// <summary>What Gitberth does when a new version of itself comes out.</summary>
public enum GitberthUpdatePolicy
{
    /// <summary>Tells (a notification, the card), and waits for Update now.</summary>
    Tell,

    /// <summary>Updates by itself between 03:00 and 05:00 (Gitberth's time zone), once nothing else runs.</summary>
    Auto,

    /// <summary>Says it on the card only.</summary>
    Nothing,
}

/// <summary>A release of Gitberth, as GitHub's latest release says it.</summary>
/// <param name="Version">1.10.0 (the tag without its v).</param>
/// <param name="Notes">Its release notes (Markdown).</param>
public sealed record GitberthRelease(string Version, string Tag, string? Name, string? Notes, string? Url, DateTime? PublishedAt);

/// <summary>How an update of Gitberth ended, as the updater wrote it (/data/update/result.json) and the card shows it.</summary>
/// <param name="Why">Why it failed, in a sentence; null when it worked.</param>
public sealed record GitberthUpdateResult(string From, string To, bool Ok, string? Why, DateTime At, string? By = null);

/// <summary>Gitberth's own update record (the setting gitberth.update): the policy, the last check and what it found, what was told, the last result.</summary>
public sealed class GitberthUpdateState
{
    public GitberthUpdatePolicy Policy { get; set; } = GitberthUpdatePolicy.Tell;
    public DateTime? CheckedAt { get; set; }

    /// <summary>Why the last check didn't work; null when it did.</summary>
    public string? CheckProblem { get; set; }

    public GitberthRelease? Latest { get; set; }

    /// <summary>The newest version a notification was sent about: each is told once.</summary>
    public string? ToldVersion { get; set; }

    public GitberthUpdateResult? LastResult { get; set; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GitberthUpdateState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new GitberthUpdateState();
        try
        {
            return JsonSerializer.Deserialize<GitberthUpdateState>(json, Json) ?? new GitberthUpdateState();
        }
        catch (JsonException)
        {
            return new GitberthUpdateState();
        }
    }
}

/// <summary>
/// The plan the updater follows (/data/update/plan.json), written by the Gitberth that's being updated: which container,
/// the image it moves to, the versions, and what to put back if the new one doesn't answer.
/// </summary>
public sealed class GitberthUpdatePlan
{
    /// <summary>The container being updated, by its ID, and its name (the new one gets it).</summary>
    public string Container { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The image the new container runs: the new version's tag (or latest, re-tagged to it).</summary>
    public string Image { get; set; } = "";

    public string From { get; set; } = "";
    public string To { get; set; } = "";

    /// <summary>The volume holding the copy of Gitberth's data from before, mounted in the updater at /copy.</summary>
    public string CopyVolume { get; set; } = "";

    /// <summary>The network the updater shares with the new container (its address there answers /healthz); "host" for the host's.</summary>
    public string Network { get; set; } = "";

    /// <summary>The port Gitberth listens on inside its container.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Compose's .env, at /compose/.env in the updater, when its GITBERTH_IMAGE pins the old version: its new value.</summary>
    public string? ComposeImage { get; set; }

    /// <summary>The image reference the .env line names now (only that line is changed).</summary>
    public string? ComposeOld { get; set; }

    /// <summary>Who asked: "admin", "Gitberth (by itself)".</summary>
    public string? By { get; set; }

    public DateTime At { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, GitberthUpdateState.Json);

    public static GitberthUpdatePlan? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GitberthUpdatePlan>(json, GitberthUpdateState.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Gitberth's update, pure: versions compared, whether its image is a published one and the image the new container runs,
/// the new container's create body from the old one's inspect, and Compose's .env line.
/// </summary>
public static class UpdateGitberthRules
{
    /// <summary>Gitberth's published image; GITBERTH_UPDATE_IMAGE names another (the end-to-end rig's).</summary>
    public const string PublishedImage = "ghcr.io/johnhowelljr/gitberth";

    public const string ImageVariable = "GITBERTH_UPDATE_IMAGE";

    /// <summary>The release GitHub says is the newest (never a pre-release or a draft).</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/johnhowelljr/Gitberth/releases/latest";

    /// <summary>The data copies kept (gitberth-data-before-&lt;version&gt;), the newest first.</summary>
    public const int CopiesKept = 2;

    /// <summary>The label every copy and updater carries (Gitberth's own helpers).</summary>
    public const string HelperKind = "update";

    /// <summary>"1.10.0" of v1.10.0 or 1.10.0; null for anything else (a pre-release isn't one: latest never names one).</summary>
    public static string? VersionOf(string? tag)
    {
        var text = (tag ?? "").Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return Version.TryParse(text, out var v) && v.Build >= 0 && v.Revision < 0 ? text : null;
    }

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="current"/> (a pre-release of a version counts as before it).</summary>
    public static bool Newer(string? latest, string current)
    {
        if (VersionOf(latest) is not { } l || !Version.TryParse(l, out var next)) return false;
        var dash = current.IndexOf('-');
        if (!Version.TryParse(dash < 0 ? current : current[..dash], out var now)) return true;
        if (next != now) return next > now;
        return dash >= 0;
    }

    /// <summary>
    /// Why Gitberth can't update itself, or null when it can: it must run in a container, from its published image
    /// (or the one <paramref name="published"/> names).
    /// </summary>
    public static string? CantUpdate(bool inContainer, string? reference, string published)
    {
        if (!inContainer) return "Gitberth isn't running in a container here, so it can't replace itself.";
        if (ImageReference.Parse(reference) is not { } running || ImageReference.Parse(published) is not { } ours)
            return "Gitberth runs from an image it can't name, so it can't replace itself.";
        if (running.Digest is not null) return $"Gitberth runs {running.Familiar}, pinned by its digest, so it doesn't move to another version by itself.";
        if (running.FromImage != ours.FromImage)
            return $"Gitberth runs {running.Familiar}, which isn't its published image ({ours.Name}): a build of your own is updated by building it again.";
        return null;
    }

    /// <summary>
    /// The image the new container runs: latest stays latest (re-tagged to the new version), any other tag moves to the new
    /// version's tag.
    /// </summary>
    public static (string Pull, string Run, bool Retag) Target(string reference, string version)
    {
        var running = ImageReference.Parse(reference)!;
        var pull = $"{running.Name}:{version}";
        return running.Tag is null or "latest" ? (pull, $"{running.Name}:latest", true) : (pull, pull, false);
    }

    /// <summary>
    /// Compose's .env with GITBERTH_IMAGE moved to <paramref name="image"/> when it names <paramref name="old"/>; null when
    /// nothing in it pins the old image (so nothing is written).
    /// </summary>
    public static string? EnvWith(string env, string old, string image)
    {
        var lines = env.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("GITBERTH_IMAGE=", StringComparison.Ordinal)) continue;
            var value = line["GITBERTH_IMAGE=".Length..].Trim().Trim('"', '\'');
            if (ImageReference.Parse(value)?.Canonical != ImageReference.Parse(old)?.Canonical) continue;
            lines[i] = "GITBERTH_IMAGE=" + image + (lines[i].EndsWith('\r') ? "\r" : "");
            changed = true;
        }
        return changed ? string.Join('\n', lines) : null;
    }

    /// <summary>
    /// The new container's create body: the old one's whole config (labels, Compose's too, environment, mounts, ports,
    /// networks and their aliases, restart policy, security options…) with the new image. What the old image only gave
    /// it by default (its environment, command, labels, ports and volumes as the image declared them) is left to the new
    /// image's own; the hostname and MAC address Docker made for the old container aren't kept.
    /// </summary>
    public static JsonObject CreateBody(JsonObject inspect, JsonObject? oldImage, string image)
    {
        var config = (JsonObject)(inspect["Config"]?.DeepClone() ?? new JsonObject());
        var defaults = oldImage?["Config"] as JsonObject;
        config["Image"] = image;
        // The old image's defaults go: the new image brings its own.
        if (config["Env"] is JsonArray env)
        {
            var given = Strings(defaults?["Env"]).ToHashSet(StringComparer.Ordinal);
            config["Env"] = new JsonArray([.. Strings(env).Where(e => !given.Contains(e)).Select(e => (JsonNode?)e)]);
        }
        if (config["Labels"] is JsonObject labels && defaults?["Labels"] is JsonObject imageLabels)
            foreach (var (k, v) in imageLabels)
                if (labels[k]?.ToJsonString() == v?.ToJsonString()) labels.Remove(k);
        foreach (var key in new[] { "Cmd", "Entrypoint", "WorkingDir", "User", "Healthcheck", "StopSignal", "Shell", "OnBuild" })
            if (config[key]?.ToJsonString() == defaults?[key]?.ToJsonString()) config.Remove(key);
        foreach (var key in new[] { "ExposedPorts", "Volumes" })
            if (config[key] is JsonObject mine && defaults?[key] is JsonObject theirs)
                foreach (var (k, _) in theirs)
                    mine.Remove(k);
        var id = inspect["Id"]?.GetValue<string>() ?? "";
        if (config["Hostname"]?.GetValue<string>() is { Length: > 0 } host && id.StartsWith(host, StringComparison.Ordinal)) config.Remove("Hostname");
        config.Remove("MacAddress");
        config.Remove("Domainname");

        var body = config;
        body["HostConfig"] = inspect["HostConfig"]?.DeepClone();
        // Each network it was on, with its aliases (not the container's own ID) and an address it was given.
        var endpoints = new JsonObject();
        var mode = inspect["HostConfig"]?["NetworkMode"]?.GetValue<string>() ?? "";
        foreach (var (name, node) in inspect["NetworkSettings"]?["Networks"] as JsonObject ?? [])
        {
            if (node is not JsonObject n) continue;
            var endpoint = new JsonObject();
            var aliases = Strings(n["Aliases"]).Where(a => !id.StartsWith(a, StringComparison.Ordinal)).ToList();
            if (aliases.Count > 0) endpoint["Aliases"] = new JsonArray([.. aliases.Select(a => (JsonNode?)a)]);
            if (n["IPAMConfig"] is JsonObject ipam && ipam.Count > 0) endpoint["IPAMConfig"] = ipam.DeepClone();
            if (n["Links"] is JsonArray { Count: > 0 } links) endpoint["Links"] = links.DeepClone();
            endpoints[name] = endpoint;
        }
        if (endpoints.Count > 0 && mode is not ("host" or "none") && !mode.StartsWith("container:", StringComparison.Ordinal))
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        return body;
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>();

    /// <summary>"gitberth-before-1.9.0": the old container while the new one starts.</summary>
    public static string BeforeName(string version) => "gitberth-before-" + Safe(version);

    /// <summary>"gitberth-data-before-1.9.0": the copy of Gitberth's data from before.</summary>
    public static string CopyName(string version) => "gitberth-data-before-" + Safe(version);

    private static string Safe(string version) => new(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());

    /// <summary>The copies to remove: all but the newest <see cref="CopiesKept"/> (by when each was made).</summary>
    public static IReadOnlyList<string> CopiesToRemove(IEnumerable<(string Name, DateTime Made)> copies, string keep) =>
        copies.Where(c => c.Name != keep).OrderByDescending(c => c.Made).Skip(CopiesKept - 1).Select(c => c.Name).ToList();

    /// <summary>The card's line for a result: "Updated to 1.10.0." or why it went back.</summary>
    public static string Said(GitberthUpdateResult r) => r.Ok
        ? $"Updated to {r.To}."
        : $"The update to {r.To} failed and Gitberth went back to {r.From}: {(r.Why ?? "it didn't say why").TrimEnd('.')}.";

    /// <summary>How long a version's check is good for.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private static readonly Markdig.MarkdownPipeline NotesPipeline = Markdig.MarkdownExtensions.DisableHtml(
        Markdig.MarkdownExtensions.UseEmphasisExtras(
            Markdig.MarkdownExtensions.UseAutoLinks(Markdig.MarkdownExtensions.UsePipeTables(new Markdig.MarkdownPipelineBuilder())),
            Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)).Build();

    private static bool WebAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>
    /// A release's notes as HTML for the card: Markdown as GitHub writes release notes, with no raw HTML (shown as text), no
    /// picture loaded from elsewhere (each becomes a link to it), and links only to http(s) addresses, opened in a new tab.
    /// </summary>
    public static string NotesHtml(string markdown)
    {
        var doc = Markdig.Markdown.Parse(markdown, NotesPipeline);
        foreach (var link in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(doc))
        {
            link.IsImage = false;
            if (!WebAddress(link.Url)) link.Url = "#";
            var attributes = Markdig.Renderers.Html.HtmlAttributesExtensions.GetAttributes(link);
            attributes.AddProperty("rel", "noopener noreferrer");
            attributes.AddProperty("target", "_blank");
        }
        foreach (var auto in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.AutolinkInline>(doc))
            if (auto.IsEmail || !WebAddress(auto.Url)) auto.Url = "#";
        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        NotesPipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return writer.ToString();
    }

    internal static string Invariant(int n) => n.ToString(CultureInfo.InvariantCulture);
}
