using Gitberth.Detection;

namespace Gitberth.Railpack;

/// <summary>
/// Railpack's providers (the kinds of project it builds) as Gitberth names them: each one's title, the files that mark
/// it, its logo, and the tools it installs. Pure: it reads nothing but the app's folder.
/// </summary>
public static class RailpackProviders
{
    /// <summary>The file a repo configures Railpack with, in the app's folder.</summary>
    public const string ConfigFile = "railpack.json";

    private const string WithRailpack = " with Railpack";

    // A project Railpack builds from the repo's own steps (railpack.json, RAILPACK_ commands), no provider matching it.
    private const string NoProvider = "Custom build";

    private sealed record Provider(string Name, string Title, string[] Markers, string Logo);

    // Railpack 0.43.0's providers in the order it tries them (core/providers), with zig, which its schema leaves out.
    // A marker starting with "*." is any file with that extension.
    private static readonly Provider[] All =
    [
        new("php", "PHP", ["composer.json", "index.php"], "php"),
        new("golang", "Go", ["go.mod", "go.work", "main.go"], "go"),
        new("java", "Java", ["pom.xml", "gradlew", "build.gradle", "build.gradle.kts"], "openjdk"),
        new("rust", "Rust", ["Cargo.toml"], "rust"),
        new("ruby", "Ruby", ["Gemfile"], "ruby"),
        new("elixir", "Elixir", ["mix.exs"], "elixir"),
        new("python", "Python", ["requirements.txt", "pyproject.toml", "Pipfile", "main.py", "app.py"], "python"),
        new("deno", "Deno", ["deno.json", "deno.jsonc"], "deno"),
        new("dotnet", ".NET", ["*.csproj"], "dotnet"),
        new("node", "Node.js", ["package.json"], "nodedotjs"),
        new("gleam", "Gleam", ["gleam.toml"], "gleam"),
        new("cpp", "C/C++", ["CMakeLists.txt", "meson.build"], "cplusplus"),
        new("zig", "Zig", ["build.zig"], "zig"),
        new("staticfile", "Static site", ["Staticfile", "index.html"], "html5"),
        new("shell", "Shell script", ["start.sh"], "gnubash"),
    ];

    // The tools Railpack's providers install with mise, by mise's name; any other keeps its own name.
    private static readonly Dictionary<string, string> PackageTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node"] = "Node.js", ["bun"] = "Bun", ["deno"] = "Deno", ["npm"] = "npm", ["pnpm"] = "pnpm", ["yarn"] = "Yarn",
        ["python"] = "Python", ["uv"] = "uv", ["poetry"] = "Poetry", ["pdm"] = "PDM", ["pipx"] = "pipx",
        ["go"] = "Go", ["golang"] = "Go", ["ruby"] = "Ruby", ["php"] = "PHP", ["composer"] = "Composer",
        ["java"] = "Java", ["gradle"] = "Gradle", ["maven"] = "Maven", ["rust"] = "Rust",
        ["elixir"] = "Elixir", ["erlang"] = "Erlang", ["gleam"] = "Gleam", ["dotnet"] = ".NET", ["zig"] = "Zig",
        ["cmake"] = "CMake", ["ninja"] = "Ninja", ["meson"] = "Meson", ["caddy"] = "Caddy",
    };

    /// <summary>A provider's name as Gitberth keeps it: lower case, as Railpack writes most (it writes Dotnet and Elixir capitalised).</summary>
    public static string Normalize(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// A provider as people name it: golang → Go, dotnet → .NET, shell → Shell script. One Gitberth doesn't list keeps its
    /// name, capitalised; none at all (the repo's own steps) is a custom build.
    /// </summary>
    public static string Title(string? provider)
    {
        var name = Normalize(provider);
        if (Find(name) is { } known) return known.Title;
        return name.Length == 0 ? NoProvider : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// What Railpack found, as the app's card and DetectedSummary say it: "Shell script with Railpack", "PHP (Laravel) with
    /// Railpack", "Ruby (Rails) with Railpack".
    /// </summary>
    public static string Summary(RailpackInfo info) => Title(info.Provider) + Framework(info.Metadata) + WithRailpack;

    /// <summary>The provider's marker files that are in <paramref name="folder"/> (start.sh, mix.exs…), in the order Railpack checks them.</summary>
    public static IReadOnlyList<string> Markers(string? provider, string folder)
    {
        if (Find(Normalize(provider)) is not { } known || !Directory.Exists(folder)) return [];
        var found = new List<string>();
        foreach (var marker in known.Markers)
        {
            if (marker.StartsWith("*.", StringComparison.Ordinal))
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(folder, marker, SearchOption.TopDirectoryOnly)
                        .Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (File.Exists(Path.Combine(folder, marker))) found.Add(marker);
        }
        return found;
    }

    /// <summary>The logo of a Railpack app, from its DetectedSummary ("Elixir with Railpack" → elixir); null when there's none.</summary>
    public static string? LogoSlug(string? detectedSummary)
    {
        var title = (detectedSummary ?? "").Trim();
        if (title.EndsWith(WithRailpack, StringComparison.Ordinal)) title = title[..^WithRailpack.Length];
        // " (Laravel)", " (Rails)": the framework has its language's logo.
        if (title.IndexOf(" (", StringComparison.Ordinal) is > 0 and var paren) title = title[..paren];
        return All.FirstOrDefault(p => p.Title == title)?.Logo;
    }

    /// <summary>A tool as people name it: node → Node.js, erlang → Erlang. One Gitberth doesn't list keeps its own name.</summary>
    public static string PackageTitle(string name) => PackageTitles.TryGetValue(name.Trim(), out var title) ? title : name.Trim();

    /// <summary>
    /// Whether New app starts with Build with Railpack on: the app's folder has a railpack.json (its author's word on how
    /// Railway's builder makes it), unless the repo's own Dockerfile builds it.
    /// </summary>
    public static bool Suggested(string folder, DetectionResult detection) =>
        detection.Kind != ProjectKind.Dockerfile && File.Exists(Path.Combine(folder, ConfigFile));

    private static Provider? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    // " (Laravel)" or " (Symfony)" from phpFramework, " (Rails)" from rubyRails, else nothing.
    private static string Framework(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("phpFramework", out var php))
        {
            if (php.Trim().Equals("laravel", StringComparison.OrdinalIgnoreCase)) return " (Laravel)";
            if (php.Trim().Equals("symfony", StringComparison.OrdinalIgnoreCase)) return " (Symfony)";
        }
        return metadata.TryGetValue("rubyRails", out var rails) && rails.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? " (Rails)" : "";
    }
}
