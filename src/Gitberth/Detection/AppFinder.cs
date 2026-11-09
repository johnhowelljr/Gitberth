using System.Text.Json;

namespace Gitberth.Detection;

public sealed partial class ProjectAnalyzer
{
    // Never offered: these hold tests, CI and tooling rather than something to run.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "e2e", "fixtures", "fixture", "__mocks__", ".github", ".devcontainer", "scripts", "cypress",
    };

    private static readonly HashSet<string> MainNames = new(StringComparer.OrdinalIgnoreCase) { "web", "website", "site", "www", "frontend", "client", "app", "webapp", "main" };
    private static readonly HashSet<string> ServiceNames = new(StringComparer.OrdinalIgnoreCase) { "api", "server", "backend", "service" };
    private static readonly HashSet<string> SecondaryNames = new(StringComparer.OrdinalIgnoreCase) { "admin", "dashboard", "backoffice", "console", "cms", "internal", "worker" };

    // Offered, but rarely the thing to run.
    private static readonly HashSet<string> AsideNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs", "doc", "documentation", "example", "examples", "sample", "samples", "demo", "demos", "playground", "sandbox",
        "storybook", "stories", "extension", "extensions", "template", "templates", "benchmark", "benchmarks", "tools", "dev",
    };

    private static readonly string[] DeployConfigs = ["vercel.json", "netlify.toml", "fly.toml", "render.yaml", "railway.json", "Procfile"];

    /// <summary>
    /// Everything in the repo that Gitberth can run, best match first: the root app, every other folder with a
    /// package.json, a Dockerfile or another language's project file (up to three levels down, leaving out tests and
    /// tooling), each Dockerfile variant, each runnable .NET project, and each kind of app in a folder that holds two.
    /// </summary>
    public List<AppCandidate> FindApps(string repoRoot, int maxFolders = 40)
    {
        var files = RepoFiles.Scan(repoRoot);
        var vercelMain = VercelConfig.MainFolder(repoRoot);
        var found = new List<AppCandidate>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string dir, string? hint, bool rootLevel)
        {
            if (!tried.Add($"{dir}|{hint}") || tried.Count > maxFolders) return;
            DetectionResult r;
            try { r = Detect(repoRoot, new AnalyzeOptions(hint, AppDir: dir)).Result; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return; }
            if (!r.CanBuild || !Runnable(repoRoot, dir, r)) return;
            // .NET projects are found from the root, with every project in the repo; sub-folders only add Node apps and Dockerfiles.
            if (!rootLevel && r.Kind.IsDotNet()) return;
            if (r.Kind.IsDotNet() && hint is null && r.ProjectCandidates.Count > 1)
            {
                foreach (var project in r.ProjectCandidates) Consider(dir, project, rootLevel);
                return;
            }
            // The wizard keeps the analyzed project as the hint, so a lone .NET project carries it too and stays selected.
            if (r.Kind.IsDotNet()) hint ??= r.SelectedProject;
            var where = r.Kind == ProjectKind.Dockerfile ? r.RepoDockerfile! : r.Kind.IsDotNet() ? r.SelectedProject ?? "" : dir.Length == 0 ? "repo root" : dir + "/";
            var reasons = new List<string> { where };
            if (vercelMain is not null && vercelMain.Equals(dir, StringComparison.OrdinalIgnoreCase)) reasons.Add("the repo's vercel.json points here");
            else if (HasDeployConfig(repoRoot, dir, vercelMain)) reasons.Add("has its own deploy config");
            var title = r.Kind == ProjectKind.Dockerfile ? "Your Dockerfile" : r.Title;
            var score = Score(dir, hint, r.Kind, HasDeployConfig(repoRoot, dir, vercelMain), vercelMain);
            // A worker or a one-off script in another language has no port to open.
            if (Languages.For(r.Kind) is not null && r.ContainerPort is null) score -= 25;
            found.Add(new AppCandidate(dir, hint, r.Kind, title, string.Join(" · ", reasons), score));
        }

        // The other kinds of app in a folder whose first match is something else: the Node app next to a Dockerfile,
        // the Python API next to a Node front end.
        void ConsiderOthers(string dir, bool rootLevel)
        {
            var folder = dir.Length == 0 ? repoRoot : Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(folder, "package.json")) && !found.Any(c => c.Dir == dir && c.Kind.IsNode()))
                Consider(dir, NodeHint, rootLevel);
            foreach (var language in Languages.All)
            {
                if (language.Markers.Any(m => File.Exists(Path.Combine(folder, m))) && !found.Any(c => c.Dir == dir && c.Kind == language.Kind))
                    Consider(dir, language.Hint, rootLevel);
            }
        }

        Consider("", null, rootLevel: true);
        ConsiderOthers("", rootLevel: true);

        var others = new List<(string Dir, string? Hint)>();
        foreach (var file in files.Files)
        {
            var slash = file.LastIndexOf('/');
            var (dir, name) = slash < 0 ? ("", file) : (file[..slash], file[(slash + 1)..]);
            var isPackage = name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || Languages.MarkerNames.Contains(name);
            var isDockerfile = IsDockerfileName(name);
            if (!isPackage && !isDockerfile) continue;
            if (dir.Length > 0 && (dir.Count(c => c == '/') > 2 || dir.Split('/').Any(SkippedFolders.Contains) || !NodeScripts.IsSafePath(dir))) continue;
            if (isDockerfile && !name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) others.Add((dir, name));
            else if (dir.Length > 0) others.Add((dir, null));
        }
        // Most promising folders first, so a big monorepo spends the budget on likely apps rather than in alphabetical order.
        foreach (var (dir, hint) in others.Distinct()
                     .OrderBy(o => vercelMain is not null && vercelMain.Equals(o.Dir, StringComparison.OrdinalIgnoreCase) ? -1 : Promise(o.Dir))
                     .ThenBy(o => o.Dir.Count(c => c == '/'))
                     .ThenBy(o => o.Dir, StringComparer.OrdinalIgnoreCase))
        {
            Consider(dir, hint, rootLevel: dir.Length == 0);
            if (hint is null) ConsiderOthers(dir, rootLevel: false);
        }

        return found
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Dir.Count(ch => ch == '/'))
            .ThenBy(c => c.Dir, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Hint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// How likely a candidate is the app to run. A Dockerfile and server frameworks beat static front ends, the repo root
    /// and names like web/site/frontend win, docs/examples/admin lose, and a root vercel.json pointing at a folder decides it.
    /// </summary>
    internal static int Score(string dir, string? hint, ProjectKind kind, bool hasDeployConfig, string? vercelMain)
    {
        var score = kind switch
        {
            ProjectKind.Dockerfile => 50,
            ProjectKind.NextJs or ProjectKind.Nuxt or ProjectKind.SvelteKit or ProjectKind.DotNetWeb => 45,
            ProjectKind.NodeServer or ProjectKind.Python or ProjectKind.Go or ProjectKind.Ruby or ProjectKind.Php or ProjectKind.Java
                or ProjectKind.Rust or ProjectKind.Deno => 40,
            ProjectKind.NodeSpa or ProjectKind.DotNetWasm => 35,
            ProjectKind.Static => 25,
            ProjectKind.DotNetWorker => 15,
            _ => 0,
        };
        var segments = dir.Length == 0 ? [] : dir.Split('/');
        if (segments.Length == 0) score += 12;
        else score -= 3 * (segments.Length - 1);
        var name = segments.Length == 0 ? "" : segments[^1];
        if (MainNames.Contains(name)) score += 10;
        else if (ServiceNames.Contains(name)) score += 5;
        else if (SecondaryNames.Contains(name)) score -= 10;
        if (segments.Any(AsideNames.Contains)) score -= 25;
        // Dockerfile.dev, Dockerfile.test, Dockerfile.e2e-appliance: build helpers more often than the app.
        if (hint is not null && IsDockerfileName(hint) && hint.Split('.', '-', '_').Any(p => p is "dev" or "test" or "tests" or "e2e" or "ci" or "debug" or "local")) score -= 25;
        if (hasDeployConfig) score += 5;
        if (vercelMain is not null && vercelMain.Equals(dir, StringComparison.OrdinalIgnoreCase)) score += 30;
        return score;
    }

    // Lower is more promising: the order folders are looked at in.
    private static int Promise(string dir)
    {
        var segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(AsideNames.Contains)) return 4;
        var name = segments.Length == 0 ? "" : segments[^1];
        return MainNames.Contains(name) ? 0 : ServiceNames.Contains(name) ? 1 : SecondaryNames.Contains(name) ? 3 : 2;
    }

    // A vercel.json at the root that points elsewhere (rootDirectory) belongs to that folder, not the root.
    private static bool HasDeployConfig(string repoRoot, string dir, string? vercelMain)
    {
        var folder = dir.Length == 0 ? repoRoot : Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
        return DeployConfigs.Any(f => File.Exists(Path.Combine(folder, f)) && !(dir.Length == 0 && f == "vercel.json" && vercelMain is not null));
    }

    // A package.json with no framework, start script or server dependency is a library, not something to run, unless
    // its entry file listens (a TypeScript or Bun server). An Astro server runs its adapter's entry file, so it has no start script.
    private static bool Runnable(string repoRoot, string dir, DetectionResult r)
    {
        if (r.Kind != ProjectKind.NodeServer) return true;
        var path = Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar), "package.json");
        try
        {
            var pkg = PackageJson.Load(path);
            return pkg.Scripts.ContainsKey("start") || ServerMarkers.Any(pkg.Has) || pkg.Has("astro") || ServesFromEntry(Path.GetDirectoryName(path)!, pkg);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }
}
