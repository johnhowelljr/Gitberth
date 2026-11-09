using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

internal sealed class PackageJson
{
    public string? Main { get; private init; }
    public string? EnginesNode { get; private init; }
    public string? EnginesBun { get; private init; }
    public string? PackageManagerField { get; private init; }
    public Dictionary<string, string> Scripts { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
    /// <summary>The version each dependency asks for ("^2.5.0", "workspace:*"), from the first section that lists it.</summary>
    public Dictionary<string, string> Versions { get; } = new(StringComparer.Ordinal);
    /// <summary>npm/yarn workspace globs, from "workspaces": [...] or "workspaces": { "packages": [...] }.</summary>
    public List<string> Workspaces { get; } = [];
    /// <summary>Folders it depends on through "file:" or "link:" specs (like "server": "file:../server"), relative to the package.</summary>
    public List<string> LocalDependencies { get; } = [];

    public bool Has(string dependency) => Dependencies.Contains(dependency);

    public static PackageJson Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("package.json isn't a JSON object.");
        var engines = root.TryGetProperty("engines", out var e) && e.ValueKind == JsonValueKind.Object ? e : (JsonElement?)null;
        var pkg = new PackageJson
        {
            Main = Str(root, "main"),
            EnginesNode = engines is { } n ? Str(n, "node") : null,
            EnginesBun = engines is { } b ? Str(b, "bun") : null,
            PackageManagerField = Str(root, "packageManager"),
        };
        if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
        {
            foreach (var s in scripts.EnumerateObject())
            {
                if (s.Value.ValueKind == JsonValueKind.String) pkg.Scripts[s.Name] = s.Value.GetString()!;
            }
        }
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (root.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in deps.EnumerateObject())
                {
                    pkg.Dependencies.Add(d.Name);
                    if (d.Value.ValueKind == JsonValueKind.String) pkg.Versions.TryAdd(d.Name, d.Value.GetString()!);
                    if (d.Value.ValueKind == JsonValueKind.String && LocalFolder(d.Value.GetString()!) is { } folder && !pkg.LocalDependencies.Contains(folder))
                        pkg.LocalDependencies.Add(folder);
                }
            }
        }
        if (root.TryGetProperty("workspaces", out var workspaces))
        {
            var globs = workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var packages) ? packages : workspaces;
            if (globs.ValueKind == JsonValueKind.Array)
                pkg.Workspaces.AddRange(globs.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!));
        }
        return pkg;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // "file:../server" or "link:../server" names a folder; "file:../pkg.tgz" is a tarball, which installs like any other package.
    private static string? LocalFolder(string spec)
    {
        if (!spec.StartsWith("file:", StringComparison.Ordinal) && !spec.StartsWith("link:", StringComparison.Ordinal)) return null;
        var path = spec[5..].Trim();
        return path.Length == 0 || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? null : path;
    }
}

/// <summary>How a Node app is installed and built, and where it sits in the repo.</summary>
/// <param name="AppDir">The app's folder relative to the repo root ("" for the root). The image's WORKDIR is /app/AppDir.</param>
/// <param name="Installs">RUN lines that install dependencies, and any that swap in a Node adapter, run from the app's folder.</param>
/// <param name="Build">The build command, or null when there's nothing to build.</param>
/// <param name="BinOnPath">Put node_modules/.bin on PATH, as Vercel does, for commands like `next build` taken from vercel.json.</param>
internal sealed record NodeBuild(PackageManager Pm, string AppDir, IReadOnlyList<string> Installs, string? Build, bool UsesCorepack, bool BinOnPath = false)
{
    public string WorkDir => AppDir.Length == 0 ? "/app" : $"/app/{AppDir}";

    /// <summary>The oven/bun tag whose bun the build uses, when bun installs or runs anything; null when it doesn't.</summary>
    public string? Bun { get; init; }

    /// <summary>The app runs on Bun rather than Node (Elysia, Bun.serve), so the image is oven/bun's.</summary>
    public bool OnBun { get; init; }
}

internal sealed record PackageManager(string Name, bool HasLockfile, bool YarnBerry = false)
{
    public static PackageManager Detect(string root, PackageJson pkg)
    {
        bool Exists(string file) => File.Exists(Path.Combine(root, file));
        var declared = pkg.PackageManagerField?.Split('@')[0].Trim().ToLowerInvariant();
        var name = declared is "npm" or "pnpm" or "yarn" or "bun" ? declared
            : Exists("pnpm-lock.yaml") ? "pnpm"
            : Exists("yarn.lock") ? "yarn"
            : Exists("bun.lock") || Exists("bun.lockb") ? "bun"
            : "npm";
        if (name == "bun") return Bun(root);

        var lockfile = name switch
        {
            "pnpm" => Exists("pnpm-lock.yaml"),
            "yarn" => Exists("yarn.lock"),
            _ => Exists("package-lock.json") || Exists("npm-shrinkwrap.json"),
        };
        var berry = name == "yarn" && (Exists(".yarnrc.yml") || (pkg.PackageManagerField is { } field && !field.StartsWith("yarn@1", StringComparison.Ordinal)));
        return new PackageManager(name, lockfile, berry);
    }

    /// <summary>bun, from bun.lock or the older binary bun.lockb when the folder has one.</summary>
    public static PackageManager Bun(string root) =>
        new("bun", File.Exists(Path.Combine(root, "bun.lock")) || File.Exists(Path.Combine(root, "bun.lockb")));

    /// <summary>pnpm and yarn come from Corepack, which Node 25+ no longer bundles, so it's installed explicitly.</summary>
    public bool UsesCorepack => Name is "pnpm" or "yarn";

    public bool IsBun => Name == "bun";

    /// <summary>The cache mount for a free-form install command, such as one from vercel.json.</summary>
    public static string CacheMountFor(string command) =>
        command.Contains("pnpm", StringComparison.Ordinal) ? "--mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store "
        : RunsBun(command) ? "--mount=type=cache,id=bun,target=/root/.bun/install/cache "
        : command.Contains("npm ", StringComparison.Ordinal) ? "--mount=type=cache,id=npm,target=/root/.npm "
        : "";

    /// <summary>Whether a free-form command runs bun or bunx, so the image needs bun.</summary>
    public static bool RunsBun(string command) =>
        command.Split([' ', '\t', '&', '|', ';', '('], StringSplitOptions.RemoveEmptyEntries).Any(w => w is "bun" or "bunx");

    public string InstallCommand => Name switch
    {
        "pnpm" => HasLockfile ? "pnpm install --frozen-lockfile" : "pnpm install",
        "yarn" when YarnBerry => HasLockfile ? "yarn install --immutable" : "yarn install",
        "yarn" => HasLockfile ? "yarn install --frozen-lockfile" : "yarn install",
        "bun" => HasLockfile ? "bun install --frozen-lockfile" : "bun install",
        _ => HasLockfile ? "npm ci" : "npm install",
    };

    /// <summary>
    /// Adds a dev dependency after the install. pnpm and yarn 1 refuse to add one to a workspace's root package unless
    /// told to; yarn 2+ has no such check, and rejects the flag.
    /// </summary>
    public string AddDevCommand(string package) => Name switch
    {
        "pnpm" => $"pnpm add -D --ignore-workspace-root-check {package}",
        "yarn" when YarnBerry => $"yarn add -D {package}",
        "yarn" => $"yarn add -D --ignore-workspace-root-check {package}",
        "bun" => $"bun add -d {package}",
        _ => $"npm install -D {package}",
    };

    public string CacheMount => Name switch
    {
        "pnpm" => "--mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store ",
        "npm" => "--mount=type=cache,id=npm,target=/root/.npm ",
        "bun" => "--mount=type=cache,id=bun,target=/root/.bun/install/cache ",
        _ => "",
    };

    public string Run(string script) => string.Join(' ', RunCommand(script));

    /// <summary>Runs a package.json script, as a CMD.</summary>
    public string[] RunCommand(string script) => Name switch
    {
        "pnpm" => ["pnpm", "run", script],
        "yarn" => ["yarn", script],
        "bun" => ["bun", "run", script],
        _ => ["npm", "run", script],
    };

    public string[] StartCommand => Name switch
    {
        "pnpm" => ["pnpm", "start"],
        "yarn" => ["yarn", "start"],
        "bun" => ["bun", "run", "start"],
        _ => ["npm", "start"],
    };
}

/// <summary>
/// Reads package.json scripts to find the folders they work in: `cd admin &amp;&amp; npm run build`,
/// `npm --prefix admin run build`, `yarn --cwd admin build`, `pnpm -C admin build`, `cd .. &amp;&amp; npm run x`, and so on,
/// following `npm run` chains (with their pre/post hooks) into other packages, npm-run-all and concurrently.
/// </summary>
internal static partial class NodeScripts
{
    private static readonly HashSet<string> Managers = new(StringComparer.Ordinal) { "npm", "pnpm", "yarn" };
    private static readonly HashSet<string> InstallVerbs = new(StringComparer.Ordinal) { "ci", "install", "i", "add" };
    private static readonly HashSet<string> NpmScriptVerbs = new(StringComparer.Ordinal) { "start", "test", "stop", "restart" };

    /// <summary>The single-package form: sub-folders of the package that its own scripts work in.</summary>
    public static IReadOnlyList<string> SubPackagesUsedBy(IReadOnlyDictionary<string, string> scripts, params string[] entryScripts) =>
        SubPackagesUsedBy(dir => dir.Length == 0 ? scripts : null, "", entryScripts);

    /// <summary>
    /// Folders, relative to the repo root, that the app's scripts run commands in other than the app's own folder,
    /// in the order first seen. Includes "" when a script goes up to the repo root. Leaves out folders the scripts
    /// install themselves (`cd admin &amp;&amp; npm ci &amp;&amp; ...`). Callers check that each has a package.json.
    /// </summary>
    /// <param name="scriptsOf">A folder's package.json scripts, or null when it has none.</param>
    public static IReadOnlyList<string> SubPackagesUsedBy(Func<string, IReadOnlyDictionary<string, string>?> scriptsOf, string appDir, params string[] entryScripts)
    {
        var used = new List<string>();
        var selfInstalled = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string package, string name)
        {
            if (!visited.Add(package + "|" + name) || scriptsOf(package) is not { } scripts) return;
            // npm runs pre<name> and post<name> around a script.
            foreach (var hook in new[] { "pre" + name, name, "post" + name })
            {
                if (scripts.TryGetValue(hook, out var command)) Scan(command, package);
            }
        }

        void Use(string? dir)
        {
            if (dir is not null && dir != appDir && !used.Contains(dir)) used.Add(dir);
        }

        void Scan(string command, string? start)
        {
            // dir is null once a `cd` goes somewhere this can't follow (outside the repo, a variable, ...).
            string? dir = start;
            var outer = new Stack<string?>();
            foreach (var piece in Separators().Split(command))
            {
                switch (piece.Trim())
                {
                    case "": case "&&": case "||": case ";": case "|":
                        continue;
                    case "(":
                        outer.Push(dir);
                        continue;
                    case ")":
                        dir = outer.Count > 0 ? outer.Pop() : start;
                        continue;
                }

                var words = Words(piece);
                if (words.Count == 0) continue;
                if (words[0] == "cd")
                {
                    dir = words.Count > 1 && dir is not null ? Combine(dir, words[1]) : null;
                    continue;
                }
                if (words[0] == "concurrently")
                {
                    foreach (Match quoted in Quoted().Matches(piece)) Scan(quoted.Groups[1].Value + quoted.Groups[2].Value, dir);
                    continue;
                }
                if (words[0] is "run-s" or "run-p" or "npm-run-all")
                {
                    if (dir is null) continue;
                    Use(dir);
                    var names = scriptsOf(dir)?.Keys ?? Enumerable.Empty<string>();
                    foreach (var pattern in words.Skip(1).Where(w => !w.StartsWith('-')))
                        foreach (var name in names.Where(k => ScriptGlob(pattern).IsMatch(k)).Order(StringComparer.Ordinal)) Visit(dir, name);
                    continue;
                }
                if (!Managers.Contains(words[0]))
                {
                    // Any other tool (tsc, vite, node, ...) run inside another package uses its dependencies.
                    Use(dir);
                    continue;
                }

                var (target, args) = ManagerArgs(words, dir);
                if (target is null || args.Contains("-w") || args.Contains("--workspace") || args.Any(a => a.StartsWith("--workspace=", StringComparison.Ordinal))) continue;
                var verb = args.FirstOrDefault(a => !a.StartsWith('-'));
                var installs = verb is null ? words[0] == "yarn" : InstallVerbs.Contains(verb);
                if (installs)
                {
                    selfInstalled.Add(target);
                    continue;
                }
                Use(target);
                if (verb is null) continue;
                var positional = args.Where(a => !a.StartsWith('-')).ToList();
                var script = verb is "run" or "run-script" ? positional.ElementAtOrDefault(1)
                    : words[0] == "npm" ? (NpmScriptVerbs.Contains(verb) ? verb : null)
                    : verb; // pnpm and yarn run a script by its bare name
                if (script is not null) Visit(target, script);
            }
        }

        foreach (var entry in entryScripts) Visit(appDir, entry);
        return used.Where(d => !selfInstalled.Contains(d)).ToList();
    }

    /// <summary>The path from one repo folder to another, for `cd` in a Dockerfile: "admin", "../server", "..".</summary>
    public static string Relative(string from, string to)
    {
        var a = from.Length == 0 ? [] : from.Split('/');
        var b = to.Length == 0 ? [] : to.Split('/');
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        var path = Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)).ToList();
        return path.Count == 0 ? "." : string.Join('/', path);
    }

    /// <summary>A path relative to a package (like "../server" from "file:../server"), as a repo folder; null when it leaves the repo.</summary>
    public static string? FromPackage(string packageDir, string path) => Combine(packageDir, path);

    /// <summary>Whether a repo folder can go into a Dockerfile's WORKDIR and `cd` lines without quoting.</summary>
    public static bool IsSafePath(string dir) => dir.Length > 0 && dir.Split('/').All(p => p is not ("" or "." or "..") && SafeName().IsMatch(p));

    /// <summary>Whether a folder is covered by one of the root's workspace globs ("packages/*", "apps/**", "admin").</summary>
    public static bool IsWorkspace(string dir, IEnumerable<string> globs) =>
        globs.Any(g => Normalize(g.Trim()) is { Length: > 0 } glob
            && new Regex("^" + Regex.Escape(glob).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$").IsMatch(dir));

    /// <summary>The folder a package-manager command runs in (from --prefix, --cwd, -C or --dir), and its remaining arguments.</summary>
    private static (string? Dir, List<string> Args) ManagerArgs(List<string> words, string? dir)
    {
        var args = new List<string>();
        for (var i = 1; i < words.Count; i++)
        {
            var w = words[i];
            var flag = w.Split('=', 2)[0];
            if (flag is "--prefix" or "--cwd" or "--dir" or "-C")
            {
                var value = w.Contains('=') ? w.Split('=', 2)[1] : words.ElementAtOrDefault(++i);
                dir = value is null || dir is null ? null : Combine(dir, value);
                continue;
            }
            args.Add(w);
        }
        return (dir, args);
    }

    /// <summary>Words of a simple command, without quotes, env assignments or cross-env.</summary>
    private static List<string> Words(string command)
    {
        var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\'')).ToList();
        while (words.Count > 0 && (words[0] == "cross-env" || EnvAssignment().IsMatch(words[0]))) words.RemoveAt(0);
        return words;
    }

    /// <summary>dir + path, normalized; null when it leaves the repo or isn't a plain relative path.</summary>
    private static string? Combine(string dir, string path)
    {
        if (path.StartsWith('/') || path.StartsWith('~') || path.Contains('$') || path == "-") return null;
        var parts = dir.Length == 0 ? new List<string>() : dir.Split('/').ToList();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (!SafeName().IsMatch(part)) return null;
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string Normalize(string glob) => glob.Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');

    // npm-run-all globs: * stays inside one ':' segment, ** crosses them.
    private static Regex ScriptGlob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*\*", ".+").Replace(@"\*", "[^:]+") + "$");

    [GeneratedRegex(@"(&&|\|\||;|\||\(|\))")]
    private static partial Regex Separators();

    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvAssignment();

    [GeneratedRegex(@"^[A-Za-z0-9._@+-]+$")]
    private static partial Regex SafeName();
}

public static partial class NodeVersions
{
    /// <summary>Used when the repo doesn't say. Node 26 became Active LTS on 2026-10-28; bump this once your apps run on it.</summary>
    public const int DefaultMajor = 24;

    public const string DefaultSource = "Gitberth's default";

    private static readonly int[] Known = [26, 24, 22, 20, 18];

    private static readonly Dictionary<string, int> Codenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = 4, ["boron"] = 6, ["carbon"] = 8, ["dubnium"] = 10, ["erbium"] = 12, ["fermium"] = 14,
        ["gallium"] = 16, ["hydrogen"] = 18, ["iron"] = 20, ["jod"] = 22, ["krypton"] = 24,
    };

    /// <summary>.nvmrc, then .node-version, then engines.node, then the default.</summary>
    public static (int Major, string Source) Resolve(string root, string? engines, int defaultMajor)
    {
        foreach (var file in new[] { ".nvmrc", ".node-version" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            var spec = File.ReadAllText(path).Trim();
            if (FromSpec(spec, defaultMajor) is int fromFile) return (fromFile, $"{file}: {spec}");
        }
        if (!string.IsNullOrWhiteSpace(engines) && FromSpec(engines, defaultMajor) is int fromEngines)
            return (fromEngines, $"engines.node: {engines}");
        return (defaultMajor, DefaultSource);
    }

    public static int? FromSpec(string spec, int defaultMajor)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return null;
        if (spec.StartsWith("lts/", StringComparison.OrdinalIgnoreCase))
            return Codenames.TryGetValue(spec[4..], out var codename) ? codename : defaultMajor;
        if (spec is "node" or "latest" or "current" or "stable" or "*" or "x") return defaultMajor;
        foreach (var candidate in Known.Prepend(defaultMajor).Distinct())
        {
            if (Satisfies(candidate, spec)) return candidate;
        }
        var first = FirstNumber().Match(spec);
        return first.Success ? int.Parse(first.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Major-version-level check of an npm semver range; good enough to pick a Docker tag.</summary>
    internal static bool Satisfies(int major, string spec)
    {
        foreach (var alternative in spec.Split("||"))
        {
            var part = alternative.Trim();
            if (part.Length == 0) continue;
            var hyphen = HyphenRange().Match(part);
            if (hyphen.Success)
            {
                var low = int.Parse(hyphen.Groups[1].Value, CultureInfo.InvariantCulture);
                var high = int.Parse(hyphen.Groups[2].Value, CultureInfo.InvariantCulture);
                if (major >= low && major <= high) return true;
                continue;
            }
            var any = false;
            var ok = true;
            foreach (Match m in Comparator().Matches(part))
            {
                any = true;
                var n = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
                var precise = IsNonZero(m.Groups["minor"]) || IsNonZero(m.Groups["patch"]);
                ok &= m.Groups["op"].Value switch
                {
                    ">=" => major >= n,
                    ">" => precise ? major >= n : major > n,
                    "<=" => major <= n,
                    "<" => precise ? major <= n : major < n,
                    _ => major == n,
                };
            }
            if (any && ok) return true;
        }
        return false;
    }

    public static IEnumerable<string> Warnings(int major)
    {
        if (major < 22) yield return $"Node {major} is past end-of-life. Consider moving to Node {DefaultMajor}.";
        else if (major % 2 == 1) yield return $"Node {major} is a short-lived Current release; even-numbered LTS versions are safer for production.";
    }

    private static bool IsNonZero(Group g) =>
        g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0;

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"^v?(\d+)(?:\.[\dxX*]+)*\s+-\s+v?(\d+)")]
    private static partial Regex HyphenRange();

    [GeneratedRegex(@"(?<op>>=|<=|>|<|=|\^|~)?\s*v?(?<major>\d+)(?:\.(?<minor>\d+|[xX*]))?(?:\.(?<patch>\d+|[xX*]))?")]
    private static partial Regex Comparator();
}

/// <summary>Which oven/bun image tag a Bun app builds with.</summary>
public static partial class BunVersions
{
    /// <summary>Used when the repo doesn't say: oven/bun:1 is the newest 1.x release.</summary>
    public const string DefaultTag = "1";

    /// <summary>
    /// The exact version packageManager pins ("bun@1.2.21"), else an exact engines.bun, else the major engines.bun asks
    /// for, else <see cref="DefaultTag"/>. oven/bun publishes a tag for every release and every major.
    /// </summary>
    internal static string Tag(PackageJson pkg)
    {
        if (pkg.PackageManagerField is { } field && field.StartsWith("bun@", StringComparison.OrdinalIgnoreCase)
            && Exact().Match(field[4..].Split('+')[0].Trim()) is { Success: true } pinned) return pinned.Value;
        if (pkg.EnginesBun?.Trim() is { Length: > 0 } engines)
        {
            if (Exact().Match(engines.TrimStart('v', '=')) is { Success: true } exact) return exact.Value;
            if (Major().Match(engines) is { Success: true } major) return major.Value;
        }
        return DefaultTag;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex Exact();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Major();
}
