using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Compose;

/// <summary>
/// The names a Compose app's things get in Docker. Its project is gb-&lt;slug&gt; (never gitberth, which is Gitberth's own),
/// each service's container gb-&lt;slug&gt;_&lt;service&gt;, and a service Gitberth builds is tagged
/// gitberth/&lt;slug&gt;/&lt;service&gt;:&lt;commit&gt;. A slug never contains '_' or '/', so no member is ever named like another
/// app's container (gb-&lt;slug&gt;), and no built service's images are another app's (gitberth/&lt;slug&gt;).
/// </summary>
public static partial class ComposeNames
{
    /// <summary>The files Compose looks for in a folder, in its own order of preference.</summary>
    public static readonly IReadOnlyList<string> Files = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>The label on every member: which service of the app it is.</summary>
    public const string ServiceLabel = "gitberth.service";

    /// <summary>An app's Compose project: gb-&lt;slug&gt;.</summary>
    public static string Project(string slug) => $"gb-{slug}";

    /// <summary>
    /// The setting that puts every service of a Compose app on Gitberth's shared network ("true"), not only its web one:
    /// its previews follow it. Kept as a setting of its own, gone with the app.
    /// </summary>
    public static string EveryServiceSharedKey(int appId) => $"compose.everyServiceShared.{appId}";

    /// <summary>
    /// A member's container: gb-&lt;slug&gt;_&lt;service&gt;. One that would end like a deploy's temporary container (_next, _prev)
    /// gets "-1" added, since Gitberth removes those by name.
    /// </summary>
    public static string Member(string slug, string service)
    {
        var name = $"gb-{slug}_{service}";
        return EndsLikeTemporary(name) ? name + "-1" : name;
    }

    /// <summary>Whether a container name ends like a deploy's temporary or previous container (_next, _prev), in any case.</summary>
    public static bool EndsLikeTemporary(string name) =>
        name.EndsWith(Deploy.DeployPipeline.BridgeSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(Deploy.DeployPipeline.PreviousSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The repository a built service's images are tagged in: gitberth/&lt;slug&gt;/&lt;service&gt;, in the lower case Docker wants.</summary>
    public static string ImageRepository(string slug, string service) => $"gitberth/{slug}/{ImagePart(service)}";

    /// <summary>A service's name as a part of an image's name: lower case, with anything else than letters, digits, '.', '_' and '-' as '-'.</summary>
    internal static string ImagePart(string service)
    {
        var text = new StringBuilder();
        foreach (var c in service.ToLowerInvariant()) text.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-');
        // A part starts and ends with a letter or digit, and has no two separators in a row (but "__").
        var part = Separators().Replace(text.ToString(), m => m.Value.StartsWith('_') && m.Value.Length >= 2 && m.Value.All(c => c == '_') ? "__" : m.Value[..1]).Trim('.', '_', '-');
        return part.Length == 0 ? "service" : part;
    }

    /// <summary>Whether a name can be a Compose service's (letters, digits, '.', '_' and '-').</summary>
    public static bool IsServiceName(string name) => ServiceName().IsMatch(name);

    [GeneratedRegex("[._-]{2,}")]
    private static partial Regex Separators();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ServiceName();
}
