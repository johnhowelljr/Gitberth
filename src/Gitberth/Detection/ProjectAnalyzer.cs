using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// Looks at a checked-out repo and decides how to build and run it.
/// First match wins: the repo's own Dockerfile, then .NET, then Node.js, then a static site.
/// </summary>
public sealed partial class ProjectAnalyzer
{
    private static readonly string[] SpaMarkers = ["vite", "react-scripts", "@angular/core", "@vue/cli-service"];

    private static readonly string[] ServerMarkers =
    [
        "express", "fastify", "koa", "@hapi/hapi", "@nestjs/core", "hono", "@hono/node-server", "restify", "polka",
        "@remix-run/node", "@remix-run/serve", "@react-router/node", "@react-router/serve", "@angular/ssr",
        "@tanstack/react-start", "@tanstack/start", "vike", "socket.io", "@adonisjs/core", "elysia",
        "@strapi/strapi", "@feathersjs/feathers", "sails", "@loopback/core",
    ];

    private static readonly string[] StaticDirs = ["", "public", "www", "site", "docs", "html", "src", "dist", "build"];

    /// <param name="repoRoot">The checked-out repo. With <see cref="AnalyzeOptions.AppDir"/> set, the app in that folder is analyzed.</param>
    /// <summary>The hint that picks the Node app at a folder that also has a Dockerfile or a .NET project.</summary>
    public const string NodeHint = "package.json";

    public DetectionResult Analyze(string repoRoot, AnalyzeOptions? options = null)
    {
        var (result, appRoot, files) = Detect(repoRoot, options ?? new AnalyzeOptions());
        if (files is not null) result.EnvSuggestions.AddRange(EnvScanner.Scan(appRoot, files, result.Kind));
        return result;
    }

    private static (DetectionResult Result, string AppRoot, RepoFiles? Files) Detect(string repoRoot, AnalyzeOptions options)
    {
        if (NormalizeDir(options.AppDir) is not { } appDir)
            return (Unknown("Subfolder outside the repo", $"The subfolder '{options.AppDir}' isn't inside the repo."), repoRoot, null);
        var appRoot = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        var files = RepoFiles.Scan(appRoot);
        List<LanguageMatch>? matches = null;
        List<LanguageMatch> Matches() => matches ??= Languages.All
            .Select(l => l.Detect(repoRoot, appDir, files, options)).OfType<LanguageMatch>().ToList();
        // Only a Node app is built from the repo root; the others build from their own folder, as before.
        var result = (Languages.ForHint(options.ProjectHint) is { } chosen ? InFolder(chosen.Detect(repoRoot, appDir, files, options)?.Result, appDir) : null)
            ?? (options.ProjectHint == NodeHint ? DetectNode(repoRoot, appDir, options) : null)
            ?? DetectDockerfile(repoRoot, appDir, options.ProjectHint)
            ?? InFolder(DetectDotNet(files, options), appDir)
            // A Django, Rails or Laravel app often has a package.json that only builds its front-end assets.
            ?? (NodeServes(appRoot) ? null : InFolder(Matches().FirstOrDefault(m => m.Strong)?.Result, appDir))
            ?? DetectNode(repoRoot, appDir, options)
            ?? InFolder(Matches().FirstOrDefault()?.Result, appDir)
            ?? InFolder(DetectStatic(appRoot), appDir)
            ?? Unknown("Unknown project type", "No Dockerfile, .NET project, package.json, other app or index.html was found. Customize the Dockerfile to build it anyway.");
        if (result.Kind == ProjectKind.Unknown) result.BuildContext = appDir;
        return (result, appRoot, files);
    }

    // Frameworks that render on a Node server, besides the server frameworks in ServerMarkers.
    private static readonly string[] SsrFrameworks = ["next", "nuxt", "@sveltejs/kit", "astro", "gatsby", "@remix-run/dev", "@solidjs/start", "@builder.io/qwik-city"];

    /// <summary>
    /// Whether the folder's package.json runs a server of its own (a server or SSR framework, or a start script that runs
    /// node), rather than only building front-end assets for an app in another language.
    /// </summary>
    internal static bool NodeServes(string folder)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path)) return false;
        try
        {
            var pkg = PackageJson.Load(path);
            return ServerMarkers.Any(pkg.Has) || SsrFrameworks.Any(pkg.Has)
                || (pkg.Scripts.TryGetValue("start", out var start) && NodeStartScript().IsMatch(start));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"./web/", "web\api" → "web", "web/api"; any folder name is kept as typed. Null when it climbs out of the repo.</summary>
    internal static string? NormalizeDir(string? dir)
    {
        var parts = new List<string>();
        foreach (var part in (dir ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static DetectionResult? InFolder(DetectionResult? r, string appDir)
    {
        if (r is not null) r.BuildContext = appDir;
        return r;
    }

    private static DetectionResult Unknown(string title, string warning)
    {
        var r = new DetectionResult { Kind = ProjectKind.Unknown, Title = title };
        r.Warnings.Add(warning);
        return r;
    }

    internal static bool IsDockerfileName(string name) =>
        name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase);

    private static DetectionResult? DetectDockerfile(string repoRoot, string appDir, string? hint)
    {
        var appRoot = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        var name = hint is not null && !hint.Contains('/') && IsDockerfileName(hint) && File.Exists(Path.Combine(appRoot, hint)) ? hint
            : new[] { "Dockerfile", "dockerfile", "Containerfile" }.FirstOrDefault(n => File.Exists(Path.Combine(appRoot, n)));
        if (name is null) return null;
        var text = File.ReadAllText(Path.Combine(appRoot, name));
        var path = RepoFiles.Join(appDir, name);
        var expose = ExposeLine().Match(text);
        var r = new DetectionResult
        {
            Kind = ProjectKind.Dockerfile,
            Title = "Your Dockerfile",
            RepoDockerfile = path,
            BuildContext = appDir.Length == 0 ? "" : DockerContext(repoRoot, appDir, text),
            ContainerPort = expose.Success ? int.Parse(expose.Groups[1].Value, CultureInfo.InvariantCulture) : null,
        };
        r.Evidence.Add($"Found {path} in the repo; it's used as-is");
        if (appDir.Length > 0)
            r.Evidence.Add(r.BuildContext.Length == 0 ? "Built from the repo root, which its COPY lines expect" : $"Built from {appDir}/");
        if (r.ContainerPort is null) r.Warnings.Add("The Dockerfile has no EXPOSE line, so set the container port yourself.");
        return r;
    }

    /// <summary>
    /// A Dockerfile in a sub-folder is built either from that folder (`docker build folder`) or from the repo root
    /// (`docker build -f folder/Dockerfile .`). Its COPY and ADD sources show which: count what exists relative to each.
    /// </summary>
    internal static string DockerContext(string repoRoot, string dir, string dockerfile)
    {
        var folder = Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
        int fromRoot = 0, fromFolder = 0;
        foreach (var source in CopySources(dockerfile))
        {
            if (PathExists(repoRoot, source)) fromRoot++;
            if (PathExists(folder, source)) fromFolder++;
        }
        if (fromRoot != fromFolder) return fromRoot > fromFolder ? "" : dir;
        // Nothing tells them apart (say, only `COPY . .`): a folder with its own project files is built from there,
        // while one that only holds Docker files (docker/, deploy/) is built from the root.
        return ProjectFiles.Any(p => PathExists(folder, p)) ? dir : "";
    }

    private static readonly string[] ProjectFiles =
        ["package.json", "*.csproj", "*.fsproj", "*.sln", "go.mod", "requirements.txt", "pyproject.toml", "Cargo.toml", "Gemfile", "pom.xml", "build.gradle", "composer.json", "index.html", "src"];

    /// <summary>The local source paths of COPY and ADD lines, leaving out copies from other stages and URLs. Wildcards are kept.</summary>
    internal static IEnumerable<string> CopySources(string dockerfile)
    {
        foreach (var instruction in ContinuedLines().Replace(dockerfile, " ").Split('\n'))
        {
            var m = CopyLine().Match(instruction);
            if (!m.Success || instruction.Contains("<<", StringComparison.Ordinal)) continue;
            // Flags (--from, --chown, --link) come first; then either plain words or a JSON array.
            var rest = m.Groups[2].Value.Trim();
            var copiesFromStage = false;
            while (rest.StartsWith("--", StringComparison.Ordinal))
            {
                var end = rest.IndexOfAny([' ', '\t']);
                copiesFromStage |= rest.StartsWith("--from", StringComparison.OrdinalIgnoreCase);
                rest = end < 0 ? "" : rest[end..].TrimStart();
            }
            if (copiesFromStage) continue;
            List<string> paths;
            if (rest.StartsWith('['))
            {
                try { paths = JsonSerializer.Deserialize<List<string>>(rest) ?? []; }
                catch (JsonException) { continue; }
            }
            else
            {
                paths = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            foreach (var source in paths.Take(paths.Count - 1))
            {
                if (source.Contains("://", StringComparison.Ordinal)) continue;
                // Strip "./" and leading slashes only, so dotfiles like .npmrc keep their name.
                var clean = source.Replace('\\', '/');
                while (clean.StartsWith("./", StringComparison.Ordinal)) clean = clean[2..];
                clean = clean.TrimStart('/').TrimEnd('/');
                if (clean is not ("" or ".")) yield return clean;
            }
        }
    }

    /// <summary>Whether a path exists under root; a wildcard (package*.json, apps/*/package.json) counts when anything matches it.</summary>
    private static bool PathExists(string root, string relative)
    {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { root };
        foreach (var part in parts)
        {
            var next = new List<string>();
            foreach (var dir in current.Where(Directory.Exists))
            {
                if (part.IndexOfAny(['*', '?', '[']) < 0)
                {
                    var path = Path.Combine(dir, part);
                    if (File.Exists(path) || Directory.Exists(path)) next.Add(path);
                    continue;
                }
                try { next.AddRange(Directory.EnumerateFileSystemEntries(dir, part.Replace('[', '?').Replace(']', '?')).Take(20)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            if (next.Count == 0) return false;
            current = next;
        }
        return true;
    }

    private static DetectionResult? DetectDotNet(RepoFiles files, AnalyzeOptions options)
    {
        var projects = files.WithExtension(".csproj", ".fsproj").Select(p => DotNetProject.Load(files, p)).OfType<DotNetProject>().ToList();
        if (projects.Count == 0) return null;

        var runnable = projects.Where(p => p.Kind != DotNetKind.Library)
            .OrderBy(p => p.Kind switch { DotNetKind.Web => 0, DotNetKind.Wasm => 1, DotNetKind.Worker => 2, _ => 3 })
            .ThenBy(p => p.LooksLikeClient)
            .ThenBy(p => p.RelativePath.Count(c => c == '/'))
            .ThenBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chosen = runnable.FirstOrDefault(p => options.ProjectHint is not null && p.RelativePath.Equals(options.ProjectHint, StringComparison.OrdinalIgnoreCase))
            ?? runnable.FirstOrDefault();
        if (chosen is null)
        {
            // Only libraries/tests: maybe it's a Node app with a .NET helper; let the other detectors try.
            return projects.Any(p => p.IsLegacyFramework) ? NetFrameworkUnsupported() : null;
        }

        var tfm = chosen.ResolveTargetFramework(files);
        if (tfm is { IsNetFramework: true }) return NetFrameworkUnsupported();

        var r = new DetectionResult
        {
            SelectedProject = chosen.RelativePath,
            ProjectCandidates = runnable.Select(p => p.RelativePath).ToList(),
            DockerIgnore = DockerfileTemplates.DotNetIgnore,
        };
        r.Evidence.Add($"Found {chosen.RelativePath} ({chosen.Sdk})");
        var solution = files.Files.FirstOrDefault(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        if (solution is not null) r.Evidence.Add($"Solution {solution}");
        if (runnable.Count > 1) r.Warnings.Add($"Found {runnable.Count} runnable projects and picked {chosen.RelativePath}. Choose another one if that's wrong.");

        if (tfm is null)
        {
            var latest = DockerfileTemplates.LatestDotNet;
            tfm = new TargetFrameworkInfo($"net{latest}.0", $"{latest}.0", latest, false, false);
            r.Warnings.Add($"Couldn't read the target framework, so {tfm.Moniker} is assumed.");
        }
        else
        {
            r.Evidence.Add($"Target framework {tfm.Moniker}");
        }
        if (tfm.WindowsOnly) r.Warnings.Add($"{tfm.Moniker} targets Windows and won't run in a Linux container.");
        if (tfm.Major > DockerfileTemplates.LatestDotNet) r.Warnings.Add($"{tfm.Moniker} is newer than Gitberth knows about, so the -preview images are used.");
        else if (tfm.Major is 8 or 9) r.Warnings.Add($"{tfm.Moniker} reaches end of support on Nov 10, 2026. Plan the move to net10.0.");
        else if (tfm.Major < 8) r.Warnings.Add($"{tfm.Moniker} is out of support. The build may still work, but net10.0 is recommended.");

        var tag = DockerfileTemplates.DotNetTag(tfm);
        var dir = chosen.Directory;
        var needsNode = files.Files.Any(f => f.EndsWith(".esproj", StringComparison.OrdinalIgnoreCase))
            || files.Exists(RepoFiles.Join(dir, "package.json"))
            || files.Exists(RepoFiles.Join(dir, "ClientApp/package.json"));
        if (needsNode) r.Evidence.Add("JavaScript front end found, so Node is added to the build stage");

        switch (chosen.Kind)
        {
            case DotNetKind.Web:
                r.Kind = ProjectKind.DotNetWeb;
                r.Title = $"ASP.NET Core ({tfm.Moniker})";
                r.ContainerPort = 8080;
                r.Dockerfile = DockerfileTemplates.DotNetWeb(tag, tfm.Major, chosen.RelativePath, chosen.AssemblyName, needsNode);
                r.Evidence.Add("Listens on port 8080 (ASPNETCORE_HTTP_PORTS)");
                break;
            case DotNetKind.Wasm:
                r.Kind = ProjectKind.DotNetWasm;
                r.Title = $"Blazor WebAssembly ({tfm.Moniker})";
                r.ContainerPort = 80;
                r.Dockerfile = DockerfileTemplates.DotNetWasm(tag, chosen.RelativePath);
                r.Evidence.Add("The published wwwroot is served by nginx");
                break;
            default:
                r.Kind = ProjectKind.DotNetWorker;
                r.Title = chosen.Kind == DotNetKind.Worker ? $".NET worker ({tfm.Moniker})" : $".NET console app ({tfm.Moniker})";
                r.Dockerfile = DockerfileTemplates.DotNetWorker(tag, tfm.Major, chosen.RelativePath, chosen.AssemblyName, chosen.UsesAspNetCore);
                r.Evidence.Add("No HTTP port; it runs as a background process");
                break;
        }
        return r;
    }

    private static DetectionResult NetFrameworkUnsupported() =>
        Unknown(".NET Framework project", ".NET Framework projects can't run in Linux containers; only SDK-style .NET 6+ projects are supported.");

    private static DetectionResult? DetectNode(string repoRoot, string appDir, AnalyzeOptions options)
    {
        var root = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path)) return null;
        PackageJson pkg;
        try
        {
            pkg = PackageJson.Load(path);
        }
        catch (JsonException)
        {
            return InFolder(Unknown("Node.js (unreadable package.json)", "package.json isn't valid JSON."), appDir);
        }

        var pm = PackageManager.Detect(root, pkg);
        var (node, nodeSource) = NodeVersions.Resolve(root, pkg.EnginesNode, options.DefaultNodeMajor);
        // A sub-folder app often relies on the repo root's .nvmrc.
        if (appDir.Length > 0 && pkg.EnginesNode is null && nodeSource == NodeVersions.DefaultSource)
            (node, nodeSource) = NodeVersions.Resolve(repoRoot, null, options.DefaultNodeMajor);
        var vercel = VercelConfig.Load(root);
        var r = new DetectionResult { DockerIgnore = DockerfileTemplates.NodeIgnore };
        // A server that needs Bun itself (Elysia, Bun.serve, a start script that runs bun) builds and runs on Bun's image.
        var bunReason = BuildsFrontEnd(pkg) ? null : BunReason(root, pkg, pm);
        if (bunReason is not null) pm = PackageManager.Bun(root);
        var b = PlanBuild(repoRoot, appDir, pkg, pm, vercel, r, onBun: bunReason is not null);
        // A workspace member installs with the workspace's package manager, from the lockfile at the workspace root.
        var installer = b.Pm;
        r.Evidence.Insert(0, $"Found {RepoFiles.Join(appDir, "package.json")}; dependencies install with {installer.Name}{(installer.HasLockfile ? " from the lockfile" : "")}");
        r.Evidence.Insert(1, bunReason is null ? $"Node {node} ({nodeSource})" : $"Runs on Bun (oven/bun:{b.Bun}), because {bunReason}");
        if (!installer.HasLockfile)
            r.Warnings.Add(installer.IsBun ? "No bun.lock is committed, so each build may resolve different dependency versions."
                : "No lockfile is committed, so each build may resolve different dependency versions.");
        if (bunReason is null) r.Warnings.AddRange(NodeVersions.Warnings(node));

        if (pkg.Has("next"))
        {
            var config = ReadFirst(root, "next.config.js", "next.config.mjs", "next.config.ts", "next.config.cjs", "next.config.mts") ?? "";
            if (NextExport().IsMatch(config)) return Spa(r, pkg, b, "Next.js (static export)", node, vercel?.OutputDirectory ?? "out", spaFallback: false);
            // Standalone output nests an app in a sub-folder under its path, so those run with `next start` instead.
            var standalone = NextStandalone().IsMatch(config) && b.AppDir.Length == 0;
            // A start script that runs `next dev` would serve a development build.
            var devStart = pkg.Scripts.TryGetValue("start", out var nextStart) && IsDevCommand(pkg, nextStart);
            r.Kind = ProjectKind.NextJs;
            r.Title = standalone ? "Next.js (standalone)" : "Next.js";
            r.ContainerPort = 3000;
            r.Dockerfile = standalone
                ? DockerfileTemplates.NextStandalone(node, b, Directory.Exists(Path.Combine(root, "public")))
                : DockerfileTemplates.NextServer(node, b, pkg.Scripts.ContainsKey("start") && !devStart);
            if (devStart && !standalone) r.Evidence.Add($"The start script (`{nextStart}`) runs a development server, so Gitberth runs `next start` instead");
            if (!standalone && b.AppDir.Length == 0) r.Evidence.Add("Tip: output: 'standalone' in next.config makes a much smaller image");
            return r;
        }
        if (pkg.Has("nuxt"))
        {
            r.Kind = ProjectKind.Nuxt;
            r.Title = "Nuxt";
            r.ContainerPort = 3000;
            r.Dockerfile = DockerfileTemplates.Nuxt(node, b);
            return r;
        }
        if (pkg.Has("@sveltejs/kit"))
        {
            if (pkg.Has("@sveltejs/adapter-node"))
            {
                r.Kind = ProjectKind.SvelteKit;
                r.Title = "SvelteKit (adapter-node)";
                r.ContainerPort = 3000;
                r.Dockerfile = DockerfileTemplates.SvelteKitNode(node, b);
                return r;
            }
            if (pkg.Has("@sveltejs/adapter-static")) return Spa(r, pkg, b, "SvelteKit (static)", node, vercel?.OutputDirectory ?? "build", spaFallback: true);
            if (SvelteKitVercelAdapter(root) is { } adapter) return SvelteKitOnNode(r, pkg, b, node, adapter);
            r.Warnings.Add("SvelteKit needs @sveltejs/adapter-node (or adapter-static) to run in Docker; falling back to the start script.");
        }
        else if (pkg.Has("astro") && AstroAdapter(root, pkg) is { } astro)
        {
            return Astro(r, pkg, b, node, vercel, astro);
        }
        else if (pkg.Has("astro") && !pkg.Has("@astrojs/node"))
        {
            return Spa(r, pkg, b, "Astro (static)", node, vercel?.OutputDirectory ?? "dist", spaFallback: false);
        }
        else if (SpaMarkers.Any(pkg.Has) && !ServerMarkers.Any(pkg.Has))
        {
            var (outDir, label) = SpaOutput(root, pkg);
            return Spa(r, pkg, b, label, node, vercel?.OutputDirectory ?? outDir, spaFallback: true);
        }

        return NodeServer(r, root, pkg, b, node);
    }

    /// <summary>Whether one of the front-end branches above builds the app, rather than the server one. Those never run on Bun.</summary>
    private static bool BuildsFrontEnd(PackageJson pkg) =>
        pkg.Has("next") || pkg.Has("nuxt") || pkg.Has("@sveltejs/kit") || pkg.Has("astro") || (SpaMarkers.Any(pkg.Has) && !ServerMarkers.Any(pkg.Has));

    /// <summary>
    /// How to install and build a Node app. When vercel.json says how, that wins: it's how the app is built today.
    /// Otherwise the app gets its own install, plus one for each other folder that its scripts work in
    /// (`cd admin &amp;&amp; npm run build`, `cd .. &amp;&amp; npm run x`) or that it depends on with "file:". Workspace members are left to the
    /// workspace root's install. An app in a sub-folder is built from the repo root so all of that is in reach. On Bun's
    /// image (<paramref name="onBun"/>) bun is the only package manager, so every folder installs with it.
    /// </summary>
    private static NodeBuild PlanBuild(string repoRoot, string appDir, PackageJson pkg, PackageManager pm, VercelConfig? vercel, DetectionResult r, bool onBun = false)
    {
        // A folder name that can't go into a Dockerfile unquoted (spaces and the like) is built from its own folder instead.
        var rootContext = appDir.Length > 0 && NodeScripts.IsSafePath(appDir);
        r.BuildContext = rootContext ? "" : appDir;
        var start = rootContext ? appDir : "";
        var baseRoot = rootContext || appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        PackageManager Manager(string folder, PackageJson p) => onBun ? PackageManager.Bun(folder) : PackageManager.Detect(folder, p);
        string? BunTag(bool used) => used || onBun ? BunVersions.Tag(pkg) : null;

        if (vercel?.InstallCommand is { } install)
        {
            r.Evidence.Add("Installs and builds the way its vercel.json says");
            var corepackNeeded = pm.UsesCorepack || install.Contains("pnpm", StringComparison.Ordinal) || install.Contains("yarn", StringComparison.Ordinal);
            var vercelBuild = vercel.BuildCommand ?? (pkg.Scripts.ContainsKey("build") ? pm.Run("build") : null);
            var bun = pm.IsBun || PackageManager.RunsBun(install) || (vercelBuild is not null && PackageManager.RunsBun(vercelBuild));
            return new NodeBuild(pm, start, [$"RUN {PackageManager.CacheMountFor(install)}{install}"], vercelBuild, corepackNeeded, BinOnPath: true)
            {
                Bun = BunTag(bun), OnBun = onBun,
            };
        }
        if (vercel?.BuildCommand is not null) r.Evidence.Add("Builds the way its vercel.json says");

        var packages = new Dictionary<string, PackageJson?>(StringComparer.Ordinal) { [start] = pkg };
        PackageJson? Package(string dir)
        {
            if (packages.TryGetValue(dir, out var known)) return known;
            var file = Path.Combine(baseRoot, dir.Replace('/', Path.DirectorySeparatorChar), "package.json");
            try { return packages[dir] = File.Exists(file) ? PackageJson.Load(file) : null; }
            catch (JsonException)
            {
                r.Warnings.Add($"{RepoFiles.Join(dir, "package.json")} isn't valid JSON, so its dependencies aren't installed.");
                return packages[dir] = null;
            }
        }

        // The package whose workspaces would cover the others: the repo root's for a sub-folder app, the app's own otherwise.
        var workspaceRoot = Package("");
        var pnpmGlobs = PnpmWorkspaceGlobs(baseRoot);
        bool Member(string dir) => dir.Length > 0
            && ((pnpmGlobs is not null && NodeScripts.IsWorkspace(dir, pnpmGlobs)) || (workspaceRoot is not null && NodeScripts.IsWorkspace(dir, workspaceRoot.Workspaces)));

        var used = NodeScripts.SubPackagesUsedBy(dir => Package(dir)?.Scripts, start, "build", "start").ToList();
        foreach (var local in pkg.LocalDependencies)
            if (NodeScripts.FromPackage(start, local) is { } dir && dir != start && !used.Contains(dir)) used.Add(dir);

        var installs = new List<string>();
        var corepack = pm.UsesCorepack;
        var bunUsed = pm.IsBun || (vercel?.BuildCommand is { } vercelCommand && PackageManager.RunsBun(vercelCommand));
        var member = rootContext && Member(start);
        var rootInstall = rootContext && workspaceRoot is not null && (member || used.Contains("") || used.Any(Member));
        if (rootInstall)
        {
            var rootPm = Manager(repoRoot, workspaceRoot!);
            installs.Add($"RUN {rootPm.CacheMount}cd {NodeScripts.Relative(start, "")} && {rootPm.InstallCommand}");
            corepack |= rootPm.UsesCorepack;
            bunUsed |= rootPm.IsBun;
            // A workspace member has no lockfile of its own, so it runs its scripts with the workspace's package manager.
            if (member) pm = rootPm;
            r.Evidence.Add(member ? $"Part of the repo's {rootPm.Name} workspace, so it installs from the repo root" : "Installs the repo root's packages too, which the app uses");
        }
        if (!member) installs.Add($"RUN {pm.CacheMount}{pm.InstallCommand}");
        var build = vercel?.BuildCommand ?? (pkg.Scripts.ContainsKey("build") ? pm.Run("build") : null);

        var others = new List<string>();
        foreach (var dir in used.Where(d => d.Length > 0 && !Member(d)))
        {
            if (Package(dir) is not { } other) continue;
            var otherPm = Manager(Path.Combine(baseRoot, dir.Replace('/', Path.DirectorySeparatorChar)), other);
            installs.Add($"RUN {otherPm.CacheMount}cd {NodeScripts.Relative(start, dir)} && {otherPm.InstallCommand}");
            corepack |= otherPm.UsesCorepack;
            bunUsed |= otherPm.IsBun;
            others.Add($"{dir}/");
        }
        if (others.Count > 0)
            r.Evidence.Add($"The app also uses {string.Join(", ", others)}, which {(others.Count == 1 ? "has its" : "have their")} own dependencies; those are installed too");
        return new NodeBuild(pm, start, installs, build, corepack, BinOnPath: vercel?.BuildCommand is not null) { Bun = BunTag(bunUsed), OnBun = onBun };
    }

    /// <summary>The package globs of a pnpm-workspace.yaml ("packages:" followed by "- 'apps/*'" lines), or null when there's none.</summary>
    internal static List<string>? PnpmWorkspaceGlobs(string root)
    {
        var path = Path.Combine(root, "pnpm-workspace.yaml");
        if (!File.Exists(path)) return null;
        var globs = new List<string>();
        var inPackages = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].TrimEnd();
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                inPackages = line.StartsWith("packages:", StringComparison.Ordinal);
                continue;
            }
            var item = line.Trim();
            if (inPackages && item.StartsWith('-'))
            {
                var glob = item[1..].Trim().Trim('\'', '"');
                if (glob.Length > 0 && !glob.StartsWith('!')) globs.Add(glob);
            }
        }
        return globs;
    }

    private static DetectionResult Spa(DetectionResult r, PackageJson pkg, NodeBuild b, string title, int node, string outDir, bool spaFallback)
    {
        r.Kind = ProjectKind.NodeSpa;
        r.Title = title;
        r.ContainerPort = 80;
        r.Evidence.Add($"Builds into {outDir}/, which nginx serves{(spaFallback ? " with client-side routing" : "")}");
        if (b.Build is null) r.Warnings.Add("package.json has no \"build\" script, which this setup needs.");
        r.Dockerfile = DockerfileTemplates.NodeStatic(node, b, outDir, spaFallback);
        return r;
    }

    private static (string OutDir, string Label) SpaOutput(string root, PackageJson pkg)
    {
        if (pkg.Has("@angular/core")) return (AngularOutDir(root), "Angular");
        if (pkg.Has("react-scripts")) return ("build", "React (Create React App)");
        if (pkg.Has("vite"))
        {
            var config = ReadFirst(root, "vite.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.mts") ?? "";
            var m = ViteOutDir().Match(config);
            var outDir = m.Success ? m.Groups[1].Value : "dist";
            if (outDir.StartsWith("./", StringComparison.Ordinal)) outDir = outDir[2..];
            var flavor = pkg.Has("react") ? " + React" : pkg.Has("vue") ? " + Vue" : pkg.Has("svelte") ? " + Svelte"
                : pkg.Has("solid-js") ? " + Solid" : pkg.Has("preact") ? " + Preact" : "";
            return (outDir.TrimEnd('/'), $"Vite{flavor}");
        }
        if (pkg.Has("@vue/cli-service")) return ("dist", "Vue CLI");
        return ("dist", "Static front end");
    }

    private static string AngularOutDir(string root)
    {
        var path = Path.Combine(root, "angular.json");
        if (!File.Exists(path)) return "dist";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var project = doc.RootElement.GetProperty("projects").EnumerateObject().First();
            var build = project.Value.GetProperty("architect").GetProperty("build");
            var builder = build.TryGetProperty("builder", out var b) ? b.GetString() ?? "" : "";
            string? outputPath = null;
            if (build.TryGetProperty("options", out var opts) && opts.TryGetProperty("outputPath", out var op))
                outputPath = op.ValueKind == JsonValueKind.String ? op.GetString() : op.TryGetProperty("base", out var basePath) ? basePath.GetString() : null;
            outputPath = (outputPath ?? $"dist/{project.Name}").TrimEnd('/');
            // The esbuild "application" builder (Angular 17+) writes the browser bundle to <outputPath>/browser.
            return builder.EndsWith(":application", StringComparison.Ordinal) ? $"{outputPath}/browser" : outputPath;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "dist";
        }
    }

    private static DetectionResult? DetectStatic(string root)
    {
        foreach (var dir in StaticDirs)
        {
            if (!File.Exists(Path.Combine(root, dir, "index.html"))) continue;
            var r = new DetectionResult
            {
                Kind = ProjectKind.Static,
                Title = "Static website",
                ContainerPort = 80,
                DockerIgnore = DockerfileTemplates.StaticIgnore,
                Dockerfile = DockerfileTemplates.Static(dir),
            };
            r.Evidence.Add($"Found {(dir.Length == 0 ? "" : dir + "/")}index.html, served by nginx");
            return r;
        }
        return null;
    }

    private static string? ReadFirst(string root, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length <= 256 * 1024) return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\s*EXPOSE\s+(\d{1,5})", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ExposeLine();

    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex ContinuedLines();

    [GeneratedRegex(@"^\s*(COPY|ADD)\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyLine();

    [GeneratedRegex(@"output\s*:\s*['""`]export['""`]")]
    private static partial Regex NextExport();

    [GeneratedRegex(@"output\s*:\s*['""`]standalone['""`]")]
    private static partial Regex NextStandalone();

    [GeneratedRegex(@"outDir\s*:\s*['""`]([^'""`]+)['""`]")]
    private static partial Regex ViteOutDir();

    [GeneratedRegex(@"(?:^|[\s;&|])(?:node|nodemon|ts-node|tsx|bun|pm2(?:-runtime)?)\s")]
    private static partial Regex NodeStartScript();
}
