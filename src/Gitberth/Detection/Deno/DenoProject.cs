using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>What Gitberth reads from deno.json or deno.jsonc.</summary>
internal sealed class DenoConfig
{
    public required string File { get; init; }

    /// <summary>The tasks, by name; a task written as an object gives its command.</summary>
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw text, where frameworks show in the import map.</summary>
    public required string Text { get; init; }

    /// <summary>It lists dependencies in an import map ("imports"), which `deno install` downloads.</summary>
    public bool HasImports { get; private set; }

    /// <summary>The file is there but isn't valid JSON(C).</summary>
    public bool Unreadable { get; init; }

    public static DenoConfig? Load(RepoFiles files)
    {
        var name = new[] { "deno.json", "deno.jsonc" }.FirstOrDefault(files.Exists);
        if (name is null) return null;
        var text = DenoSources.Read(files.Full(name));
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var config = new DenoConfig { File = name, Text = text };
            config.HasImports = doc.RootElement.ValueKind == JsonValueKind.Object
                && ((doc.RootElement.TryGetProperty("imports", out var imports) && imports.ValueKind == JsonValueKind.Object && imports.EnumerateObject().Any())
                    || doc.RootElement.TryGetProperty("importMap", out _));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Object)
            {
                foreach (var task in tasks.EnumerateObject())
                {
                    var command = task.Value.ValueKind == JsonValueKind.String ? task.Value.GetString()
                        : task.Value.ValueKind == JsonValueKind.Object && task.Value.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()
                        : null;
                    if (command is not null) config.Tasks[task.Name] = command;
                }
            }
            return config;
        }
        catch (JsonException)
        {
            return new DenoConfig { File = name, Text = text, Unreadable = true };
        }
    }
}

/// <summary>A Deno framework Gitberth names, and where it listens when the code doesn't say.</summary>
internal sealed record DenoFramework(string Label, int DefaultPort, Regex Marker);

/// <summary>Reads a Deno app's sources: its entry file, whether and where it serves, and the variables it reads.</summary>
internal static partial class DenoSources
{
    private const int MaxFiles = 2000;
    private const int MaxBytes = 512 * 1024;

    /// <summary>Where a Deno app usually starts, when no task names its entry file.</summary>
    public static readonly string[] EntryCandidates = ["main.ts", "main.tsx", "main.js", "main.mjs", "server.ts", "server.tsx", "server.js", "app.ts", "index.ts", "src/main.ts", "src/server.ts", "src/index.ts"];

    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".mts", ".js", ".jsx", ".mjs"];

    // Not the app's own server code: examples, tests, scripts, files served to browsers, and Fresh's build output.
    private static readonly HashSet<string> AsideDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "example", "examples", "test", "tests", "__tests__", "bench", "benches", "scripts", "static", "_fresh",
    };

    /// <summary>Frameworks, most specific first. Deno.serve's own default is port 8000; Aleph's dev server uses 3000.</summary>
    public static readonly DenoFramework[] Frameworks =
    [
        new("Fresh", 8000, FreshMarker()),
        new("Aleph", 3000, AlephMarker()),
        new("Hono", 8000, HonoMarker()),
        new("Oak", 8000, OakMarker()),
    ];

    /// <summary>The app's own sources, leaving out tests, examples, browser files and Fresh's build output.</summary>
    public static List<(string Path, string Text)> Load(RepoFiles files) =>
        files.WithExtension(SourceExtensions)
            .Where(f => !TestFile().IsMatch(f) && !f.Split('/').SkipLast(1).Any(AsideDirs.Contains))
            .Take(MaxFiles)
            .Select(f => (f, Read(files.Full(f))))
            .ToList();

    /// <summary>The file a task runs (`deno run -A main.ts`, `deno serve --port 3000 server.ts`), or null.</summary>
    public static string? TaskEntry(string command) => TaskEntryPattern().Match(command) is { Success: true } m ? m.Groups["entry"].Value.TrimStart('.', '/') : null;

    /// <summary>A task that runs a development server, which reloads on changes, rather than the app as it runs in production.</summary>
    public static bool IsDevServer(string command) =>
        command.Contains("--watch", StringComparison.Ordinal) || DevScript().IsMatch(command) || ViteDev().IsMatch(command);

    /// <summary>Whether the code serves HTTP: Deno.serve, Deno.listen, a default export with fetch for deno serve, or a framework.</summary>
    public static bool Serves(string text) =>
        DenoServeCall().IsMatch(text) || DefaultFetchExport().IsMatch(text) || Frameworks.Any(f => f.Marker.IsMatch(text));

    /// <summary>A default export deno serve can run: an object with a fetch handler, or a Hono app (which has one).</summary>
    public static bool ExportsFetch(string text) =>
        DefaultFetchExport().IsMatch(text) || (HonoMarker().IsMatch(text) && DefaultExportName().Match(text) is { Success: true } m && text.Contains($"{m.Groups[1].Value} = new Hono", StringComparison.Ordinal));

    /// <summary>Code that's clearly for Deno, not Node or Bun: Deno's own APIs, or jsr:, npm: or deno.land imports.</summary>
    public static bool UsesDeno(string text) => DenoApi().IsMatch(text);

    /// <summary>The port the code listens on: PORT with a fallback, a port option, an address string, or a PORT constant.</summary>
    public static (int Port, bool FromEnv)? CodePort(string text)
    {
        if (EnvPort().Match(text) is { Success: true } env) return (Port(env.Groups[1].Value), true);
        foreach (var pattern in new[] { PortOption(), ListenAddress(), PortConstant() })
            if (pattern.Match(text) is { Success: true } m) return (Port(m.Groups[1].Value), false);
        return null;
    }

    /// <summary>A --port on a deno serve command line.</summary>
    public static int? FlagPort(string command) => PortFlag().Match(command) is { Success: true } m ? Port(m.Groups[1].Value) : null;

    /// <summary>A localhost hostname the code or a task listens on, which can't be reached from outside the container.</summary>
    public static string? Localhost(string text) =>
        LocalHostname().Match(text) is { Success: true } m ? m.Groups[1].Value
        : LocalHostFlag().Match(text) is { Success: true } f ? f.Groups[1].Value : null;

    /// <summary>The environment variables the code reads: Deno.env.get, and process.env from Node-style code.</summary>
    public static IEnumerable<string> EnvKeys(string text) =>
        DenoEnvGet().Matches(text).Concat(ProcessEnv().Matches(text)).Select(m => m.Groups[1].Value);

    internal static string Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static int Port(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?:^|/)[^/]*[._]test\.[cm]?[jt]sx?$|(?:^|/)(?:tests?|__tests__)/")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"\bdeno\s+(?:run|serve)\b[^&|;]*?\s(?<entry>[\w./@-]+\.(?:ts|tsx|mts|js|jsx|mjs))(?=\s|$|[""'])")]
    private static partial Regex TaskEntryPattern();

    [GeneratedRegex(@"(?:^|[\s/])dev\.[jt]s(?:\s*$|\s+(?!build\b))")]
    private static partial Regex DevScript();

    [GeneratedRegex(@"(?:^|\s)vite(?:\s+(?:dev|serve)\b|\s*$)")]
    private static partial Regex ViteDev();

    [GeneratedRegex(@"\bDeno\.(?:serve|listen|listenTls|serveHttp)\s*\(")]
    private static partial Regex DenoServeCall();

    [GeneratedRegex(@"\bexport\s+default\s*\{[^}]*\bfetch\b")]
    private static partial Regex DefaultFetchExport();

    [GeneratedRegex(@"\bexport\s+default\s+([A-Za-z_$][\w$]*)\s*;?\s*$", RegexOptions.Multiline)]
    private static partial Regex DefaultExportName();

    [GeneratedRegex(@"\bDeno\.\w|[""'](?:jsr:|npm:|https://deno\.land/)")]
    private static partial Regex DenoApi();

    [GeneratedRegex(@"\$fresh/|@fresh/core|deno\.land/x/fresh@|jsr:@fresh/")]
    private static partial Regex FreshMarker();

    [GeneratedRegex(@"deno\.land/x/aleph|[""']aleph/|jsr:@aleph/")]
    private static partial Regex AlephMarker();

    [GeneratedRegex(@"@hono/hono|npm:hono\b|deno\.land/x/hono|from\s+[""']hono[""']")]
    private static partial Regex HonoMarker();

    [GeneratedRegex(@"@oak/oak|deno\.land/x/oak")]
    private static partial Regex OakMarker();

    // Number(Deno.env.get("PORT") ?? 8000), parseInt(Deno.env.get("PORT") || "3000").
    [GeneratedRegex(@"Deno\.env\.get\(\s*[""']PORT[""']\s*\)\s*(?:\?\?|\|\|)\s*[""']?(\d{2,5})")]
    private static partial Regex EnvPort();

    // Deno.serve({ port: 3000 }, …), app.listen({ port: 8080 }).
    [GeneratedRegex(@"(?:\bDeno\.serve|\.listen|\bserve)\(\s*\{[^}]*?\bport\s*:\s*(\d{2,5})")]
    private static partial Regex PortOption();

    [GeneratedRegex(@"\.listen\(\s*[""'](?:[\w.]*):(\d{2,5})[""']")]
    private static partial Regex ListenAddress();

    [GeneratedRegex(@"\b(?:const|let|var)\s+(?:PORT|port)\s*=\s*(?:Number\(|parseInt\()?\s*[""']?(\d{2,5})")]
    private static partial Regex PortConstant();

    [GeneratedRegex(@"--port(?:=|\s+)(\d{2,5})")]
    private static partial Regex PortFlag();

    [GeneratedRegex(@"\bhostname\s*:\s*[""'](localhost|127\.0\.0\.1)[""']")]
    private static partial Regex LocalHostname();

    [GeneratedRegex(@"--host(?:=|\s+)(localhost|127\.0\.0\.1)\b")]
    private static partial Regex LocalHostFlag();

    [GeneratedRegex(@"\bDeno\.env\.get\(\s*[""']([A-Za-z_][A-Za-z0-9_]*)[""']\s*\)")]
    private static partial Regex DenoEnvGet();

    [GeneratedRegex(@"\bprocess\.env\.([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ProcessEnv();
}

/// <summary>Which denoland/deno image runs an app. Its tags are full versions only (2.9.8), never 2 or 2.9.</summary>
internal static partial class DenoVersions
{
    /// <summary>A current Deno 2 release Gitberth has checked an image for. Bump it now and then.</summary>
    public const string Default = "2.9.8";

    /// <summary>The last Deno 1 release.</summary>
    public const string LastDeno1 = "1.46.3";

    /// <summary>
    /// The version .dvmrc, .tool-versions or mise.toml asks for: the files setup-deno and version managers read. A full
    /// version is used as it is; a minor gets its first release (2.1 → 2.1.0), unless it's the default's.
    /// </summary>
    public static (string Version, string Source, string? Warning) Resolve(RepoFiles files)
    {
        foreach (var (name, pattern) in new[] { (".dvmrc", DvmrcVersion()), (".tool-versions", ToolVersionsDeno()), ("mise.toml", MiseDeno()), (".mise.toml", MiseDeno()) })
        {
            if (!files.Exists(name)) continue;
            var m = pattern.Match(DenoSources.Read(files.Full(name)));
            if (!m.Success) continue;
            var spec = m.Groups[1].Value.Trim();
            var (version, warning) = FromSpec(spec);
            return (version, $"{name}: {spec}", warning);
        }
        return (Default, "Gitberth's default", null);
    }

    public static (string Version, string? Warning) FromSpec(string spec)
    {
        var s = spec.Trim().TrimStart('v', 'V');
        if (s is "" or "latest" or "stable" or "*" or "lts") return (Default, null);
        var m = VersionParts().Match(s);
        if (!m.Success) return (Default, $"Couldn't read the Deno version \"{spec}\", so Deno {Default} is used.");
        var major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        // "2.x" and "2.1.x" ask for any release of that major or minor.
        var minor = m.Groups[2].Success && m.Groups[2].Value is not ("x" or "*") ? m.Groups[2].Value : null;
        var patch = minor is not null && m.Groups[3].Success && m.Groups[3].Value is not ("x" or "*") ? m.Groups[3].Value : null;
        var deno1 = major == 1 ? "Deno 1 no longer gets updates. Moving to Deno 2 is recommended." : null;
        if (patch is not null && minor is not null) return ($"{major}.{minor}.{patch}", deno1);
        if (minor is not null)
            return ($"{major}.{minor}" == Default[..Default.LastIndexOf('.')] ? Default : $"{major}.{minor}.0", deno1);
        return major switch
        {
            2 => (Default, null),
            1 => (LastDeno1, deno1),
            _ => (Default, $"Deno {major} is newer than Gitberth knows, so Deno {Default} is used. Pin a full version, like {major}.0.0, in .dvmrc to use it."),
        };
    }

    [GeneratedRegex(@"^\s*v?([^\s#]+)", RegexOptions.Multiline)]
    private static partial Regex DvmrcVersion();

    [GeneratedRegex(@"^\s*deno\s+([^\s#]+)", RegexOptions.Multiline)]
    private static partial Regex ToolVersionsDeno();

    [GeneratedRegex(@"^\s*deno\s*=\s*[""']([^""']+)[""']", RegexOptions.Multiline)]
    private static partial Regex MiseDeno();

    [GeneratedRegex(@"^(\d+)(?:\.(\d+|x|\*))?(?:\.(\d+|x|\*))?$")]
    private static partial Regex VersionParts();
}
