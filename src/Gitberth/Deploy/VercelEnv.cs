using System.Text.Json;
using System.Text.RegularExpressions;
using Gitberth.Detection;
using Gitberth.Docker;

namespace Gitberth.Deploy;

/// <summary>What a build found out about the app, kept on its image (<see cref="ImageLabels"/>) for later recreates.</summary>
/// <param name="Prefix">The prefix the app's framework gives copies of Vercel's variables, like NEXT_PUBLIC_.</param>
/// <param name="AuthJs">The app signs people in with Auth.js (next-auth).</param>
/// <param name="ChecksVercel">
/// The app's code asks process.env.VERCEL itself. Code like that (create-t3-app's env.js, say) takes VERCEL_URL to
/// mean it runs on Vercel, so the bare URL variables would mislead it.
/// </param>
public sealed record AppTraits(string? Prefix, bool AuthJs, bool ChecksVercel = false)
{
    public static AppTraits None { get; } = new(null, false);

    /// <summary>What an image was built with; null for an image built before Gitberth kept these (it has no marker label).</summary>
    public static AppTraits? FromLabels(IReadOnlyDictionary<string, string> labels) =>
        labels.ContainsKey(ImageLabels.Traits)
            ? new(labels.GetValueOrDefault(ImageLabels.EnvPrefix) is { Length: > 0 } prefix ? prefix : null,
                labels.GetValueOrDefault(ImageLabels.AuthJs) == "true", labels.GetValueOrDefault(ImageLabels.ChecksVercel) == "true")
            : null;

    /// <summary>The build's --label arguments that keep these on the image.</summary>
    public IEnumerable<string> LabelArgs() =>
    [
        "--label", $"{ImageLabels.Traits}=1",
        .. Prefix is null ? [] : new[] { "--label", $"{ImageLabels.EnvPrefix}={Prefix}" },
        .. AuthJs ? new[] { "--label", $"{ImageLabels.AuthJs}=true" } : [],
        .. ChecksVercel ? new[] { "--label", $"{ImageLabels.ChecksVercel}=true" } : [],
    ];
}

/// <summary>What a deploy tells the app about itself.</summary>
/// <param name="MainDomain">The app's main domain, or null when it has none.</param>
/// <param name="Production">Deployed from the repo's default branch; any other branch is a preview, as on Vercel.</param>
public sealed record DeployFacts(string? MainDomain, string Owner, string Repo, string Branch, string? CommitSha, string? CommitMessage, string? CommitAuthor,
    AppTraits Traits, bool Production = true);

/// <summary>
/// Vercel's system environment variables, so code written for Vercel finds what it expects: where the app lives
/// (VERCEL_URL and friends, from its main domain, as bare host names) and what was deployed (VERCEL_GIT_*). As on Vercel,
/// front ends also get copies with their framework's public prefix (NEXT_PUBLIC_VERCEL_URL), and both build steps and the
/// running app see them. Auth.js apps also get AUTH_URL and NEXTAUTH_URL: behind a proxy, and without Vercel's platform,
/// Auth.js can't work out its own address and falls back to localhost:3000. The app's own values and the repo's GitHub
/// variables win. Some names would make tools believe they run on Vercel itself, so they're left out: VERCEL=1 always,
/// and the bare VERCEL_ENV during builds (Nitro and std-env pick their Vercel preset from it).
/// </summary>
public static partial class VercelEnv
{
    // Packages that name the framework preset, first match wins; the prefixes are the ones in Vercel's framework docs.
    private static readonly (string Package, string Prefix)[] Frameworks =
    [
        ("next", "NEXT_PUBLIC_"), ("nuxt", "NUXT_ENV_"), ("gatsby", "GATSBY_"), ("react-scripts", "REACT_APP_"), ("astro", "PUBLIC_"),
        ("@shopify/hydrogen", "PUBLIC_"), ("@sveltejs/kit", "VITE_"), ("@solidjs/start", "VITE_"), ("solid-start", "VITE_"),
        ("@vue/cli-service", "VUE_APP_"), ("@redwoodjs/core", "REDWOOD_ENV_"), ("sanity", "SANITY_STUDIO_"), ("vite", "VITE_"),
    ];

    // vercel.json's "framework" names the preset outright; presets not listed here get no copies.
    private static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nextjs"] = "NEXT_PUBLIC_", ["blitzjs"] = "NEXT_PUBLIC_", ["nuxtjs"] = "NUXT_ENV_", ["gatsby"] = "GATSBY_",
        ["create-react-app"] = "REACT_APP_", ["astro"] = "PUBLIC_", ["hydrogen"] = "PUBLIC_", ["sveltekit"] = "VITE_", ["sveltekit-1"] = "VITE_",
        ["solidstart"] = "VITE_", ["solidstart-1"] = "VITE_", ["vue"] = "VUE_APP_", ["redwoodjs"] = "REDWOOD_ENV_", ["sanity"] = "SANITY_STUDIO_",
        ["sanity-v3"] = "SANITY_STUDIO_", ["vite"] = "VITE_",
    };

    /// <summary>Both names Auth.js reads its address from, as one setting.</summary>
    public static readonly string[] AuthUrlKeys = ["AUTH_URL", "NEXTAUTH_URL"];

    private static readonly string[] SourceExtensions = [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".jsx", ".tsx"];

    /// <summary>What a build of the app in this folder (or a monorepo app with no package.json of its own, in the repo root) tells about it.</summary>
    public static AppTraits Detect(string appFolder, string repoRoot)
    {
        var pkg = Package(appFolder) ?? Package(repoRoot);
        var prefix = VercelConfig.Load(appFolder)?.Framework is { } preset
            ? Presets.GetValueOrDefault(preset)
            : pkg is null ? null : Frameworks.FirstOrDefault(f => pkg.Has(f.Package)).Prefix;
        return new AppTraits(prefix, pkg is not null && UsesAuthJs(pkg, appFolder, repoRoot), ChecksVercel(appFolder));
    }

    /// <summary>The variables the running app gets, in a stable order. Commit details are left out when they aren't known.</summary>
    public static List<EnvValue> For(DeployFacts f)
    {
        var environment = f.Production ? "production" : "preview";
        // Code that checks process.env.VERCEL takes these to mean it runs on Vercel; the production URL is safe.
        var url = f.Traits.ChecksVercel ? null : f.MainDomain;
        (string Key, string? Value)[] vars =
        [
            ("VERCEL_ENV", environment),
            ("VERCEL_TARGET_ENV", environment),
            ("VERCEL_URL", url),
            ("VERCEL_BRANCH_URL", url),
            ("VERCEL_PROJECT_PRODUCTION_URL", f.MainDomain),
            ("VERCEL_GIT_PROVIDER", "github"),
            ("VERCEL_GIT_REPO_SLUG", f.Repo),
            ("VERCEL_GIT_REPO_OWNER", f.Owner),
            ("VERCEL_GIT_COMMIT_REF", f.Branch),
            ("VERCEL_GIT_COMMIT_SHA", f.CommitSha),
            ("VERCEL_GIT_COMMIT_MESSAGE", f.CommitMessage),
            ("VERCEL_GIT_COMMIT_AUTHOR_NAME", f.CommitAuthor),
        ];
        var list = vars.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => new EnvValue(v.Key, v.Value!, false)).ToList();
        if (f.Traits.Prefix is { } prefix) list.AddRange(list.Select(v => v with { Key = prefix + v.Key }).ToList());
        if (f.Traits.AuthJs && f.MainDomain is { } domain) list.AddRange(AuthUrlKeys.Select(k => new EnvValue(k, "https://" + domain, false)));
        return list;
    }

    /// <summary>
    /// The variables the build gets: the same, except the bare VERCEL_ENV, from which Nitro and std-env decide they build
    /// for Vercel (a Nuxt build then writes .vercel/output instead of .output), and Auth.js's address, which it only reads
    /// when it runs (and which BuildKit would warn about as a secret in an ARG).
    /// </summary>
    public static List<EnvValue> ForBuild(DeployFacts f) =>
        For(f).Where(v => v.Key != "VERCEL_ENV" && !AuthUrlKeys.Contains(v.Key)).ToList();

    /// <summary>
    /// The deploy's variables with Vercel's added, except where the app or the repo already sets the same name. Auth.js
    /// reads AUTH_URL and NEXTAUTH_URL as one setting, so when either is set, neither is added.
    /// </summary>
    /// <param name="alsoSet">Names the app sets that aren't in <paramref name="env"/>, such as run-time values during the build.</param>
    public static List<EnvValue> AddTo(IReadOnlyList<EnvValue> env, IEnumerable<EnvValue> vercel, IEnumerable<string>? alsoSet = null)
    {
        var taken = env.Select(e => e.Key).Concat(alsoSet ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownAuthUrl = AuthUrlKeys.Any(taken.Contains);
        return [.. env, .. vercel.Where(v => !taken.Contains(v.Key) && !(ownAuthUrl && AuthUrlKeys.Contains(v.Key)))];
    }

    /// <summary>
    /// Whether the app uses Auth.js: next-auth or an @auth/ package, in its own package.json or in a package of the same
    /// repo that it depends on (workspace:, file: or link:), the way monorepos like create-t3-turbo keep it.
    /// </summary>
    private static bool UsesAuthJs(PackageJson pkg, string appFolder, string repoRoot)
    {
        static bool Auth(IEnumerable<string> deps) => deps.Any(d => d == "next-auth" || d.StartsWith("@auth/", StringComparison.Ordinal));
        if (Auth(pkg.Dependencies)) return true;
        foreach (var dir in pkg.LocalDependencies)
            if (Package(Path.GetFullPath(Path.Combine(appFolder, dir))) is { } local && Auth(local.Dependencies)) return true;
        var workspace = pkg.Versions.Where(v => v.Value.StartsWith("workspace:", StringComparison.Ordinal)).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        return workspace.Count > 0 && RepoPackages(repoRoot).Any(p => workspace.Contains(p.Name) && Auth(p.Dependencies));
    }

    /// <summary>The repo's own packages, by the name in their package.json, a few levels down at most.</summary>
    private static IEnumerable<(string Name, IReadOnlyCollection<string> Dependencies)> RepoPackages(string repoRoot)
    {
        var files = RepoFiles.Scan(repoRoot);
        foreach (var rel in files.Files.Where(f => Path.GetFileName(f) == "package.json" && f.Count(c => c == '/') <= 3).Take(200))
        {
            (string? Name, List<string> Deps) read;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(files.Full(rel)), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var deps = new List<string>();
                foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
                    if (root.TryGetProperty(section, out var list) && list.ValueKind == JsonValueKind.Object) deps.AddRange(list.EnumerateObject().Select(d => d.Name));
                read = (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null, deps);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (read.Name is { } name) yield return (name, read.Deps);
        }
    }

    // Reads the app's own source, not its dependencies (RepoFiles leaves node_modules out).
    private static bool ChecksVercel(string appFolder)
    {
        var files = RepoFiles.Scan(appFolder);
        foreach (var rel in files.WithExtension(SourceExtensions).Take(3000))
        {
            try
            {
                var info = new FileInfo(files.Full(rel));
                if (info.Length <= 512 * 1024 && VercelCheck().IsMatch(File.ReadAllText(info.FullName))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    private static PackageJson? Package(string folder)
    {
        var path = Path.Combine(folder, "package.json");
        try
        {
            return File.Exists(path) ? PackageJson.Load(path) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"process\.env\.VERCEL(?![A-Za-z0-9_])|process\.env\[\s*['""]VERCEL['""]\s*\]")]
    private static partial Regex VercelCheck();
}
