using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>What Gitberth reads from a PHP app: composer.json, composer.lock, and the folder its index.php is in.</summary>
internal sealed partial class PhpApp
{
    /// <summary>Folders a web server serves PHP apps from, in the order they're tried; "" is the app's own folder.</summary>
    internal static readonly string[] DocRoots = ["public", "web", "webroot", "", "htdocs", "www", "html"];

    public required string Folder { get; init; }

    /// <summary>The app has a composer.json, so its packages install with Composer.</summary>
    public bool Composer { get; private init; }

    public bool Locked { get; private init; }

    /// <summary>composer.json's require (without require-dev), and what composer.lock installs for production.</summary>
    public Dictionary<string, string> Require { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> LockedPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The extensions that composer.json and the locked packages ask for (ext-intl → intl), lower case.</summary>
    public SortedSet<string> RequiredExtensions { get; } = new(StringComparer.Ordinal);

    /// <summary>config.platform.php: the PHP version Composer resolves for, when the app pins one.</summary>
    public string? PlatformPhp { get; private set; }

    public bool Has(string package) => Require.ContainsKey(package) || LockedPackages.Contains(package);

    /// <summary>The version a package's constraint or lock entry starts with ("^11.0" → 11.0), or null.</summary>
    public (int Major, int Minor)? VersionOf(string package)
    {
        var text = LockedVersions.GetValueOrDefault(package) ?? Require.GetValueOrDefault(package);
        var m = text is null ? Match.Empty : MajorMinor().Match(text);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0) : null;
    }

    private Dictionary<string, string> LockedVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The PHP app in this folder, or null without composer.json or an index.php. A broken composer.json is a warning.</summary>
    public static PhpApp? Load(string folder, List<string> warnings)
    {
        var path = Path.Combine(folder, "composer.json");
        if (!File.Exists(path)) return DocRoot(folder) is null ? null : new PhpApp { Folder = folder };
        var app = new PhpApp { Folder = folder, Composer = true, Locked = File.Exists(Path.Combine(folder, "composer.lock")) };
        try
        {
            using var doc = Parse(path);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("composer.json isn't a JSON object.");
            if (Object(root, "require") is { } require)
            {
                foreach (var p in require.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) app.Require[p.Name] = p.Value.GetString()!;
            }
            if (Object(root, "config") is { } config && Object(config, "platform") is { } platform
                && platform.TryGetProperty("php", out var php) && php.ValueKind == JsonValueKind.String)
                app.PlatformPhp = php.GetString();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("composer.json isn't valid JSON, so Gitberth can't tell which PHP version and extensions the app needs.");
        }
        if (app.Locked) app.ReadLock(Path.Combine(folder, "composer.lock"));
        foreach (var name in app.Require.Keys) app.AddExtension(name);
        return app;
    }

    private void ReadLock(string path)
    {
        try
        {
            using var doc = Parse(path);
            if (Array(doc.RootElement, "packages") is not { } packages) return;
            foreach (var package in packages.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object))
            {
                if (package.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    LockedPackages.Add(name.GetString()!);
                    if (package.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                        LockedVersions[name.GetString()!] = version.GetString()!;
                }
                if (Object(package, "require") is { } require)
                    foreach (var p in require.EnumerateObject()) AddExtension(p.Name);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private void AddExtension(string requirement)
    {
        if (!requirement.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)) return;
        var name = requirement[4..].ToLowerInvariant().Replace('-', '_');
        if (name is "zend_opcache" or "opcache") name = "opcache";
        if (ExtensionName().IsMatch(name)) RequiredExtensions.Add(name);
    }

    /// <summary>The first folder with an index.php, from <see cref="DocRoots"/>; null when there's none.</summary>
    public static string? DocRoot(string folder) =>
        DocRoots.Cast<string?>().FirstOrDefault(d => File.Exists(Path.Combine(folder, d!, "index.php")));

    private static JsonDocument Parse(string path) =>
        JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    private static JsonElement? Object(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement? Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    [GeneratedRegex(@"^[a-z0-9_]+$")]
    private static partial Regex ExtensionName();

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?")]
    private static partial Regex MajorMinor();
}

/// <summary>Which php image an app gets, from composer.json's require.php or config.platform.php.</summary>
internal static partial class PhpVersions
{
    /// <summary>Used when composer.json doesn't say, or allows it. PHP 8.5 came out in November 2025; 8.4 has the widest extension support.</summary>
    public const string Default = "8.4";

    public const string DefaultSource = "Gitberth's default";

    /// <summary>Versions with php:&lt;version&gt;-apache images, newest first.</summary>
    internal static readonly string[] Known = ["8.5", "8.4", "8.3", "8.2", "8.1", "8.0", "7.4", "7.3", "7.2", "7.1", "7.0", "5.6"];

    /// <summary>
    /// config.platform.php wins, since Composer resolved the lockfile for it. Otherwise the default when require.php allows
    /// it, else the newest version that does.
    /// </summary>
    public static (string Version, string Source, string? Warning) Resolve(PhpApp app)
    {
        if (app.PlatformPhp is { } platform && Minor(platform) is { } pinned && Known.Contains(pinned))
            return (pinned, $"config.platform.php in composer.json: {platform}", null);
        if (app.Require.GetValueOrDefault("php") is not { } constraint) return (Default, DefaultSource, null);
        if (Known.Prepend(Default).Distinct().FirstOrDefault(v => Satisfies(v, constraint)) is { } fits)
            return (fits, $"composer.json requires php {constraint}", null);
        return (Known[0], DefaultSource, $"composer.json asks for PHP {constraint}, which none of the PHP versions Gitberth knows meets, so PHP {Known[0]} is used.");
    }

    /// <summary>
    /// Whether a minor version (meaning its newest patch) meets a Composer constraint: "^8.2", "~8.2.0", ">=8.1 <8.4",
    /// "8.2.*", "^7.4 || ^8.0", "8.1 - 8.3".
    /// </summary>
    internal static bool Satisfies(string minor, string constraint)
    {
        var candidate = Parse(minor);
        foreach (var alternative in Or().Split(constraint.Trim()))
        {
            var hyphen = HyphenRange().Match(alternative.Trim());
            if (hyphen.Success)
            {
                if (candidate.CompareTo(Parse(hyphen.Groups[1].Value)) >= 0 && candidate.CompareTo(Parse(hyphen.Groups[2].Value)) <= 0) return true;
                continue;
            }
            var parts = And().Split(OperatorSpace().Replace(alternative.Trim(), "$1")).Where(p => p.Length > 0).ToList();
            if (parts.Count > 0 && parts.All(p => SatisfiesOne(candidate, p))) return true;
        }
        return false;
    }

    private static bool SatisfiesOne((int Major, int Minor) candidate, string part)
    {
        if (part is "*" or "x") return true;
        var m = Comparison().Match(part);
        if (!m.Success) return false;
        var pieces = m.Groups["version"].Value.Split('.');
        var wildcard = pieces.Any(p => p is "*" or "x" or "X");
        var numbers = pieces.TakeWhile(p => p is not ("*" or "x" or "X")).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length == 0) return true;
        var floor = (numbers[0], numbers.Length > 1 ? numbers[1] : 0);
        return m.Groups["op"].Value switch
        {
            "^" => candidate.CompareTo(floor) >= 0 && candidate.Major == numbers[0],
            // ~8.2 allows 8.x from 8.2; ~8.2.1 allows 8.2.x only.
            "~" => numbers.Length >= 3 ? candidate == floor : candidate.CompareTo(floor) >= 0 && candidate.Major == numbers[0],
            ">=" or ">" => candidate.CompareTo(floor) >= 0,
            "<" => candidate.CompareTo(floor) < 0,
            "<=" => candidate.CompareTo(floor) <= 0,
            "!=" => true,
            _ when wildcard || numbers.Length == 1 => candidate.Major == numbers[0] && (numbers.Length == 1 || candidate.Minor == numbers[1]),
            _ => candidate == floor,
        };
    }

    public static IEnumerable<string> Warnings(string version)
    {
        var v = Parse(version);
        if (v.CompareTo((8, 2)) < 0) yield return $"PHP {version} is past end-of-life. Consider moving to PHP {Default}.";
        else if (v == (8, 2)) yield return $"PHP 8.2 reaches end-of-life on Dec 31, 2026. Plan the move to PHP {Default}.";
    }

    private static string? Minor(string version)
    {
        var m = MinorVersion().Match(version);
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
    }

    private static (int Major, int Minor) Parse(string version)
    {
        var m = MinorVersion().Match(version);
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : MajorOnly().Match(version) is { Success: true } major ? (int.Parse(major.Groups[1].Value, CultureInfo.InvariantCulture), 0) : (0, 0);
    }

    [GeneratedRegex(@"\s*\|\|?\s*")]
    private static partial Regex Or();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex And();

    [GeneratedRegex(@"(>=|<=|!=|==|>|<|=|\^|~)\s+")]
    private static partial Regex OperatorSpace();

    [GeneratedRegex(@"^v?(\d+(?:\.\d+)*)\s+-\s+v?(\d+(?:\.\d+)*)$")]
    private static partial Regex HyphenRange();

    [GeneratedRegex(@"^(?<op>>=|<=|!=|==|>|<|=|\^|~)?v?(?<version>\d+(?:\.(?:\d+|[*xX]))*)(?:[-@].*)?$")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex MinorVersion();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex MajorOnly();
}
