using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>What a tsconfig.json says about where tsc writes its output, and how the code marks its types.</summary>
/// <param name="Folder">The tsconfig's own folder, relative to the app ("" for the app's folder).</param>
/// <param name="OutDir">compilerOptions.outDir, relative to the app; null when it isn't set.</param>
/// <param name="RootDir">compilerOptions.rootDir, relative to the app; null when it isn't set.</param>
/// <param name="Include">The include patterns, relative to the app.</param>
/// <param name="PathAliases">The keys of compilerOptions.paths, like "@/*".</param>
internal sealed record TsConfig(string Folder, string? OutDir, string? RootDir, IReadOnlyList<string> Include, IReadOnlyList<string> PathAliases,
    bool VerbatimModuleSyntax, bool EmitDecoratorMetadata)
{
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The app's tsconfig.json (or <paramref name="file"/>), with what it inherits from the files it extends inside the repo:
    /// the nearest setting wins. A base in node_modules isn't read, since detection runs before the install. Null when
    /// there's no such file.
    /// </summary>
    public static TsConfig? Load(string root, string file = "tsconfig.json")
    {
        var appRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(appRoot, file));
        if (!File.Exists(path)) return null;
        string? Relative(string folder, string value)
        {
            var full = Path.GetFullPath(Path.Combine(folder, value));
            var relative = Path.GetRelativePath(appRoot, full).Replace('\\', '/');
            return relative == "." ? "" : relative.StartsWith("..", StringComparison.Ordinal) ? null : relative.TrimEnd('/');
        }

        string? outDir = null, rootDir = null;
        List<string>? include = null, aliases = null;
        bool? verbatim = null, metadata = null;
        var current = path;
        for (var depth = 0; depth < 5 && current is not null && File.Exists(current); depth++)
        {
            JsonElement json;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(current), Jsonc);
                json = doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                break;
            }
            if (json.ValueKind != JsonValueKind.Object) break;
            var folder = Path.GetDirectoryName(current)!;
            if (json.TryGetProperty("compilerOptions", out var options) && options.ValueKind == JsonValueKind.Object)
            {
                outDir ??= Str(options, "outDir") is { } o ? Relative(folder, o) : null;
                rootDir ??= Str(options, "rootDir") is { } d ? Relative(folder, d) : null;
                verbatim ??= Bool(options, "verbatimModuleSyntax");
                metadata ??= Bool(options, "emitDecoratorMetadata");
                if (aliases is null && options.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
                    aliases = paths.EnumerateObject().Select(p => p.Name).ToList();
            }
            if (include is null && json.TryGetProperty("include", out var patterns) && patterns.ValueKind == JsonValueKind.Array)
            {
                // Patterns hold wildcards, so only their folder part is resolved: "src/**/*" in the app's folder stays "src/**/*".
                var prefix = Relative(folder, ".");
                include = patterns.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetString()!.Replace('\\', '/').TrimStart('.', '/'))
                    .Where(p => prefix is not null && p.Length > 0)
                    .Select(p => prefix!.Length == 0 ? p : $"{prefix}/{p}").ToList();
            }
            current = Extends(json, folder);
        }
        var own = Relative(Path.GetDirectoryName(path)!, ".") ?? "";
        return new TsConfig(own, outDir, rootDir, include ?? [], aliases ?? [], verbatim == true, metadata == true);
    }

    /// <summary>The file a tsconfig extends, when it's one in the repo ("./tsconfig.base.json"); null for a package or none.</summary>
    private static string? Extends(JsonElement json, string folder)
    {
        if (!json.TryGetProperty("extends", out var extends)) return null;
        var value = extends.ValueKind == JsonValueKind.String ? extends.GetString()
            : extends.ValueKind == JsonValueKind.Array ? extends.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).LastOrDefault(e => e?.StartsWith('.') == true)
            : null;
        if (value is null || !value.StartsWith('.')) return null;
        var path = Path.GetFullPath(Path.Combine(folder, value));
        return File.Exists(path) ? path : File.Exists(path + ".json") ? path + ".json" : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>
/// Whether Node runs a TypeScript entry by itself. Node 22.18+, 23.6+ and 24+ strip types from .ts, .mts and .cts files
/// with no flag, and do nothing else: enums, namespaces, constructor parameter properties, decorators and import aliases
/// are errors, .tsx files don't run, tsconfig.json isn't read (so "paths" don't resolve), an import has to name its file
/// exactly (./db.ts, not ./db or ./db.js), and an import without `type` is a value. The entry and every file it imports
/// are checked for those. Type-only imports can't be told from values without the types, so the code must be written to
/// mark them: there's no tsconfig.json (it's written for Node), or the tsconfig sets verbatimModuleSyntax, under which tsc
/// rejects an unmarked one.
/// </summary>
internal static partial class TypeStripping
{
    /// <summary>The first major whose image Gitberth uses, node:22-slim (the newest 22.x), strips types by default.</summary>
    public const int FirstMajor = 22;

    private const int MaxFiles = 300;

    /// <summary>
    /// Why Node can't run <paramref name="entry"/> as it is (null when it can), and whether that's because of decorators.
    /// Without <paramref name="strict"/>, only what certainly fails counts, not an unmarked tsconfig.
    /// </summary>
    public static (string? Why, bool Decorators) Check(string root, string entry, int node, bool strict = true)
    {
        if (node < FirstMajor) return ($"Node {node} doesn't strip types (22.18 and later do)", false);
        if (entry.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)) return ($"{entry} is a .tsx file", false);
        var config = TsConfig.Load(root);
        if (strict && config is { VerbatimModuleSyntax: false })
            return ("tsconfig.json doesn't set verbatimModuleSyntax, so type-only imports may not say `import type`", false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };
        var queue = new Queue<string>([entry]);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (Read(root, file) is not { } source) continue;
            var text = WithoutComments(source);
            if (Unsupported(text) is { } feature) return ($"{file} {feature}", feature == "uses decorators");
            foreach (var (spec, typeOnly) in Imports(text))
            {
                // `import type` and `export type` lines are removed whole, so what they name doesn't matter.
                if (typeOnly) continue;
                if (!spec.StartsWith('.'))
                {
                    if (config?.PathAliases.Any(a => Aliases(a, spec)) == true) return ($"{file} imports {spec} through tsconfig paths", false);
                    continue;
                }
                if (Target(root, file, spec) is not { } target) continue;
                var full = Path.Combine(root, target);
                if (File.Exists(full))
                {
                    var extension = Path.GetExtension(target).ToLowerInvariant();
                    if (extension == ".tsx") return ($"{file} imports {spec}, a .tsx file", false);
                    if (extension is ".ts" or ".mts" or ".cts" && seen.Count < MaxFiles && seen.Add(target)) queue.Enqueue(target);
                    continue;
                }
                if (Directory.Exists(full)) return ($"{file} imports the folder {spec}", false);
                var js = Path.GetExtension(target).ToLowerInvariant();
                var stem = js is ".js" or ".mjs" or ".cjs" or ".jsx" ? target[..^js.Length] : target;
                var ts = new[] { ".ts", ".mts", ".cts", ".tsx" }.Select(e => stem + e).FirstOrDefault(p => File.Exists(Path.Combine(root, p)));
                if (ts is not null)
                    return (stem == target ? $"{file} imports {spec} without its file extension" : $"{file} imports {spec}, which is {Path.GetFileName(ts)} in the repo", false);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// The modules a piece of code imports or requires, with whether the statement is type-only (`import type`,
    /// `export type`), which Node removes whole.
    /// </summary>
    internal static IEnumerable<(string Spec, bool TypeOnly)> Imports(string text)
    {
        foreach (Match m in FromImport().Matches(text)) yield return (m.Groups["spec"].Value, m.Groups["type"].Success);
        foreach (Match m in BareImport().Matches(text)) yield return (m.Groups["spec"].Value, false);
        foreach (Match m in DynamicImport().Matches(text)) yield return (m.Groups["spec"].Value, false);
        foreach (Match m in RequireCall().Matches(text)) yield return (m.Groups["spec"].Value, false);
    }

    /// <summary>
    /// The repo file a relative import most likely means, the way a bundler or tsc would find it (./db, ./db.js for db.ts,
    /// a folder's index), for reading its code; null when there's none.
    /// </summary>
    internal static string? Locate(string root, string from, string spec)
    {
        if (Target(root, from, spec) is not { } target) return null;
        var extension = Path.GetExtension(target).ToLowerInvariant();
        var stem = extension is ".js" or ".mjs" or ".cjs" ? target[..^extension.Length] : target;
        var candidates = new[] { target }
            .Concat(new[] { ".ts", ".mts", ".cts", ".js", ".mjs", ".cjs" }.Select(e => stem + e))
            .Concat(new[] { "/index.ts", "/index.js" }.Select(e => target + e));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(root, c)));
    }

    /// <summary>
    /// The code with its // and /* */ comments blanked out, keeping its length and lines, so commented-out code and JSDoc
    /// tags don't count. Strings are skipped, so "https://" in one isn't a comment.
    /// </summary>
    internal static string WithoutComments(string text)
    {
        var chars = text.ToCharArray();
        char? quote = null;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (quote is { } q)
            {
                if (c == '\\') i++;
                // Only template literals span lines; a quote left open at a line's end is something else, like a regex.
                else if (c == q || (c == '\n' && q != '`')) quote = null;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }
            if (c != '/' || i + 1 >= chars.Length || chars[i + 1] is not ('/' or '*')) continue;
            var end = chars[i + 1] == '/' ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            var stop = end < 0 ? chars.Length : chars[i + 1] == '/' ? end : end + 2;
            for (var j = i; j < stop; j++)
            {
                if (chars[j] != '\n') chars[j] = ' ';
            }
            i = stop - 1;
        }
        return new string(chars);
    }

    /// <summary>The TypeScript-only syntax in the code that Node can't strip, as "declares an enum" and the like; null when there's none.</summary>
    private static string? Unsupported(string text) =>
        EnumDeclaration().IsMatch(text) ? "declares an enum"
        : NamespaceDeclaration().IsMatch(text) ? "declares a namespace"
        : ParameterProperty().IsMatch(text) ? "uses constructor parameter properties"
        : Decorator().IsMatch(text) ? "uses decorators"
        : ImportAlias().IsMatch(text) ? "uses `import … =`"
        : ExportAssignment().IsMatch(text) ? "uses `export =`"
        : null;

    /// <summary>A relative import as a path in the app folder, or null when it leaves the folder.</summary>
    private static string? Target(string root, string from, string spec)
    {
        var appRoot = Path.GetFullPath(root);
        var folder = Path.GetDirectoryName(Path.Combine(appRoot, from.Replace('/', Path.DirectorySeparatorChar)))!;
        var full = Path.GetFullPath(Path.Combine(folder, spec.Split('?', '#')[0]));
        var relative = Path.GetRelativePath(appRoot, full).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    /// <summary>Whether an import goes through a tsconfig paths key ("@/*" covers "@/lib/db").</summary>
    private static bool Aliases(string key, string spec)
    {
        var star = key.IndexOf('*');
        return star < 0 ? spec == key : spec.StartsWith(key[..star], StringComparison.Ordinal) && spec.EndsWith(key[(star + 1)..], StringComparison.Ordinal);
    }

    private static string? Read(string root, string file)
    {
        try
        {
            var info = new FileInfo(Path.Combine(root, file));
            return info.Exists && info.Length <= 256 * 1024 ? File.ReadAllText(info.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // import x from './x', import { a } from "./a", export * from './b', import type { T } from './t'. What's between the
    // keyword and `from` never holds = or parentheses, so a match can't run on from `export type X = …` or a function.
    [GeneratedRegex(@"(?:^|[;}\s])(?:import|export)\s+(?<type>type\s+)?[^'"";=()]*?\bfrom\s*['""](?<spec>[^'""\n]+)['""]", RegexOptions.Multiline)]
    private static partial Regex FromImport();

    [GeneratedRegex(@"(?:^|[;}\s])import\s*['""](?<spec>[^'""\n]+)['""]", RegexOptions.Multiline)]
    private static partial Regex BareImport();

    [GeneratedRegex(@"\bimport\s*\(\s*['""](?<spec>[^'""\n]+)['""]\s*\)")]
    private static partial Regex DynamicImport();

    [GeneratedRegex(@"\brequire\s*\(\s*['""](?<spec>[^'""\n]+)['""]\s*\)")]
    private static partial Regex RequireCall();

    // `declare enum` has no code, so it's fine.
    [GeneratedRegex(@"^[ \t]*(?:export[ \t]+)?(?:const[ \t]+)?enum[ \t]+[A-Za-z_$]", RegexOptions.Multiline)]
    private static partial Regex EnumDeclaration();

    [GeneratedRegex(@"^[ \t]*(?:export[ \t]+)?(?:namespace|module)[ \t]+[A-Za-z_$][\w$.]*[ \t]*\{", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(@"\bconstructor\s*\([^)]*?\b(?:public|private|protected|readonly|override)\s+[A-Za-z_$]")]
    private static partial Regex ParameterProperty();

    [GeneratedRegex(@"^[ \t]*@[A-Za-z_$][\w$.]*(?:\(|\s|$)", RegexOptions.Multiline)]
    private static partial Regex Decorator();

    [GeneratedRegex(@"^[ \t]*(?:export[ \t]+)?import[ \t]+[A-Za-z_$][\w$]*[ \t]*=(?!=)", RegexOptions.Multiline)]
    private static partial Regex ImportAlias();

    [GeneratedRegex(@"^[ \t]*export[ \t]*=(?!=)", RegexOptions.Multiline)]
    private static partial Regex ExportAssignment();
}
