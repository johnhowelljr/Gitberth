using System.Text.Json;

namespace Gitberth.Detection;

/// <summary>
/// Python apps: Django, FastAPI, Flask and the other web frameworks, data apps (Streamlit, Gradio, Dash), Procfile
/// apps and plain scripts, with dependencies from pip, uv, Poetry, PDM or Pipenv.
/// </summary>
internal sealed class PythonLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Python;

    public string Hint => "python";

    public IReadOnlyList<string> Markers { get; } = ["pyproject.toml", "requirements.txt", "Pipfile", "setup.py", "manage.py"];

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        if (PythonProject.Load(repoRoot, appDir, files) is not { } project || PythonApps.Plan(project) is not { } app) return null;
        var install = project.Install;
        var (version, versionSource, versionWarnings) = ResolveVersion(project);
        var r = new DetectionResult
        {
            Kind = ProjectKind.Python,
            Title = app.Title,
            ContainerPort = app.Port,
            DockerIgnore = PythonDockerfiles.Ignore(files),
        };

        r.Evidence.Add(InstallEvidence(project));
        if (project.UnusedDependencyFiles.Count > 0)
            r.Evidence.Add($"{string.Join(", ", project.UnusedDependencyFiles)} {(project.UnusedDependencyFiles.Count == 1 ? "is" : "are")} there too, but {install.Source} is what's installed");
        r.Evidence.Add($"Python {version} ({versionSource})");
        r.Evidence.AddRange(app.Evidence);
        if (app.Port is int port) r.Evidence.Add($"Listens on port {port} (the app gets PORT={port})");
        if (app.SrcLayout) r.Evidence.Add("The code is under src/, which is on PYTHONPATH");

        var servers = new List<string>();
        if (app.Server == "gunicorn" && !project.Has("gunicorn")) servers.Add(PythonDockerfiles.GunicornPackage);
        if (app.Server == "uvicorn" && !ProvidesUvicorn(project)) servers.Add(PythonDockerfiles.UvicornPackage);
        if (servers.Count > 0) r.Evidence.Add($"{app.Server} isn't in the dependencies, so it's installed in the image too");

        var native = NativeLibraries.For(project);
        if (native.CompileInFullImage)
            r.Evidence.Add($"{And(native.Compiled)} {(native.Compiled.Count == 1 ? "is" : "are")} built from source, so the build uses the full python:{version} image"
                + (native.RunInFullImage ? ", and the app runs on it too" : native.RuntimePackages.Count > 0 ? $", and the app's image adds {And(native.RuntimePackages)}" : ""));
        else if (native.RuntimePackages.Count > 0)
            r.Evidence.Add($"{And(native.RuntimeUsers)} {(native.RuntimeUsers.Count == 1 ? "loads" : "load")} {And(native.RuntimePackages)} at run time, so the image adds {(native.RuntimePackages.Count == 1 ? "it" : "them")}");

        var assets = Assets(files, options, r);
        r.Dockerfile = PythonDockerfiles.Dockerfile(new PythonImage(version, install, servers, native, assets, app));

        if (!install.Locked)
            r.Warnings.Add($"No {Lockfile(install.Tool)} is committed, so each build resolves the dependencies again and may get different versions.");
        r.Warnings.AddRange(versionWarnings);
        r.Warnings.AddRange(app.Warnings);
        if (!project.HasDependencyFile && app.Strong)
            r.Warnings.Add("No requirements.txt, pyproject.toml or Pipfile was found, so none of the app's packages are installed. Add a requirements.txt.");
        if (appDir.Length > 0 && PythonFiles.ReadPath(Path.Combine(repoRoot, "pyproject.toml"))?.Contains("[tool.uv.workspace]", StringComparison.Ordinal) == true)
            r.Warnings.Add("This folder is part of the uv workspace at the repo root, but it's built on its own, without the workspace's uv.lock or its other members. If it needs them, add a Dockerfile that builds from the repo root.");
        return new LanguageMatch(r, app.Strong);
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add) => PythonEnv.Scan(files, add);

    /// <summary>
    /// .python-version, then runtime.txt, then requires-python, then the Pipfile's python_version, then Poetry's python
    /// dependency. A sub-folder app falls back to the repo root's .python-version or runtime.txt; then Gitberth's default.
    /// </summary>
    internal static (PyVersion Version, string Source, List<string> Warnings) ResolveVersion(PythonProject p)
    {
        var warnings = new List<string>();
        (PyVersion, string)? FromFiles(Func<string, string?> read, string where)
        {
            if (FirstLine(read(".python-version")) is { } line)
            {
                if (line.Contains("pypy", StringComparison.OrdinalIgnoreCase) || line.Contains("graalpy", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($".python-version asks for {line}, which Gitberth doesn't build with, so CPython is used.");
                else if (PythonVersions.FromExact(line) is { } version) return (version, $"{where}.python-version: {line}");
            }
            if (FirstLine(read("runtime.txt")) is { } runtime && PythonVersions.FromExact(runtime) is { } fromRuntime) return (fromRuntime, $"{where}runtime.txt: {runtime}");
            return null;
        }

        var found = FromFiles(p.Text, "");
        if (found is null && Toml.String(p.Pyproject, "project", "requires-python") is { } requires && PythonVersions.FromSpec(requires) is { } fromRequires)
            found = (fromRequires, $"requires-python: {requires.Trim()}");
        if (found is null && (Toml.String(p.Pipfile, "requires", "python_full_version") ?? Toml.String(p.Pipfile, "requires", "python_version")) is { } pipfile
            && PythonVersions.FromExact(pipfile) is { } fromPipfile)
            found = (fromPipfile, $"Pipfile: python_version {pipfile}");
        if (found is null && Toml.String(p.Pyproject, "tool", "poetry", "dependencies", "python") is { } poetry && PythonVersions.FromSpec(poetry) is { } fromPoetry)
            found = (fromPoetry, $"Poetry: python = \"{poetry}\"");
        if (found is null && p.AppDir.Length > 0) found = FromFiles(file => PythonFiles.ReadPath(Path.Combine(p.RepoRoot, file)), "the repo root's ");
        var (resolved, source) = found ?? (PythonVersions.Default, PythonVersions.DefaultSource);
        warnings.AddRange(PythonVersions.Warnings(resolved, DateOnly.FromDateTime(DateTime.UtcNow)));
        return (resolved, source, warnings);
    }

    private static string? FirstLine(string? text) =>
        text?.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

    private static string InstallEvidence(PythonProject p)
    {
        var install = p.Install;
        var self = install.InstallsProject && install.Tool is not PythonTool.Pip ? ", along with the project itself, since it's a package" : "";
        return install.Tool switch
        {
            PythonTool.Pip when install.RequirementsFile is null && install.InstallsProject => $"Found {install.Source}; the project and its dependencies install with pip",
            PythonTool.Pip when install.RequirementsFile is null => $"Found {install.Source}; its install_requires install with pip, without the project itself, since it doesn't name its packages",
            PythonTool.Pip => $"Found {install.Source}; dependencies install with pip{(install.InstallsProject ? ", along with the project itself (-e .)" : "")}",
            PythonTool.Uv => $"Found pyproject.toml; dependencies install with uv{(install.Locked ? " from uv.lock" : "")}{self}",
            PythonTool.UvPip => $"Found pyproject.toml; its dependencies install with uv pip{self}",
            PythonTool.Poetry => $"Found pyproject.toml; the main dependencies install with Poetry{(install.Locked ? " from poetry.lock" : "")}{self}",
            PythonTool.Pdm => $"Found pyproject.toml; the production dependencies install with PDM{(install.Locked ? " from pdm.lock" : "")}{self}",
            PythonTool.Pipenv => $"Found Pipfile; the default packages install with Pipenv{(install.Locked ? " from Pipfile.lock" : "")}",
            _ => "No requirements.txt, pyproject.toml or Pipfile, so there's nothing to install",
        };
    }

    private static string Lockfile(PythonTool tool) => tool switch
    {
        PythonTool.Uv => "uv.lock",
        PythonTool.Poetry => "poetry.lock",
        PythonTool.Pdm => "pdm.lock",
        _ => "Pipfile.lock",
    };

    // uvicorn comes with fastapi[standard] and litestar[standard] too.
    private static bool ProvidesUvicorn(PythonProject p) =>
        p.Has("uvicorn")
        || p.Dependency("fastapi") is { } fastapi && (fastapi.HasExtra("standard") || fastapi.HasExtra("all"))
        || p.Dependency("litestar") is { } litestar && (litestar.HasExtra("standard") || litestar.HasExtra("full"));

    /// <summary>
    /// A package.json with a build script next to the app (Tailwind, Vite for Django or Flask templates) gets a Node
    /// stage that builds the assets; the Python image takes what it built, without node_modules.
    /// </summary>
    private static PythonAssets? Assets(RepoFiles files, AnalyzeOptions options, DetectionResult r)
    {
        if (!files.Exists("package.json")) return null;
        PackageJson pkg;
        try
        {
            pkg = PackageJson.Load(files.Full("package.json"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            r.Warnings.Add("package.json isn't valid JSON, so its front-end assets aren't built.");
            return null;
        }
        if (!pkg.Scripts.ContainsKey("build")) return null;
        var pm = PackageManager.Detect(files.Root, pkg);
        var (node, _) = NodeVersions.Resolve(files.Root, pkg.EnginesNode, options.DefaultNodeMajor);
        r.Evidence.Add($"package.json has a build script, so a Node {node} stage runs `{pm.Run("build")}` first, and the Python image gets what it built without node_modules");
        r.Warnings.AddRange(NodeVersions.Warnings(node));
        return new PythonAssets(node, pm, pm.IsBun ? BunVersions.Tag(pkg) : null);
    }

    private static string And(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
