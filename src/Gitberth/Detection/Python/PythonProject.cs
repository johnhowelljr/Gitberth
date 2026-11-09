using System.Text.RegularExpressions;

namespace Gitberth.Detection;

internal enum PythonTool { None, Pip, Uv, UvPip, Poetry, Pdm, Pipenv }

/// <summary>How a project's dependencies get installed.</summary>
/// <param name="Source">The file they're installed from, for the evidence: "uv.lock", "requirements.txt"…</param>
/// <param name="Locked">A lockfile pins every version, or the tool has none to commit (pip's requirements files).</param>
/// <param name="InstallsProject">The project itself is installed too, because it's a package.</param>
/// <param name="RequirementsFile">The requirements file pip installs; null for a setup.py project.</param>
/// <param name="Packages">What pip installs for a setup.py app that isn't a package: the requirements its install_requires lists.</param>
internal sealed record PythonInstall(PythonTool Tool, string Source, bool Locked, bool InstallsProject, string? RequirementsFile = null, IReadOnlyList<string>? Packages = null)
{
    public string Name => Tool switch
    {
        PythonTool.Pip => "pip",
        PythonTool.Uv or PythonTool.UvPip => "uv",
        PythonTool.Poetry => "Poetry",
        PythonTool.Pdm => "PDM",
        PythonTool.Pipenv => "Pipenv",
        _ => "",
    };
}

/// <summary>A Heroku-style Procfile: a "name: command" line for each process type.</summary>
internal sealed partial class Procfile
{
    private Procfile(Dictionary<string, string> processes) => Processes = processes;

    public IReadOnlyDictionary<string, string> Processes { get; }

    public string? Web => Processes.GetValueOrDefault("web");

    /// <summary>Runs once before the new version starts (migrations, usually).</summary>
    public string? Release => Processes.GetValueOrDefault("release");

    /// <summary>The process to run when there's no web one: "worker", or the first other one.</summary>
    public KeyValuePair<string, string>? Background =>
        Processes.Where(p => p.Key is not ("web" or "release")).OrderBy(p => p.Key == "worker" ? 0 : 1).Cast<KeyValuePair<string, string>?>().FirstOrDefault();

    public static Procfile? Parse(string text)
    {
        var processes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = ProcessLine().Match(raw.Trim());
            if (m.Success && !processes.ContainsKey(m.Groups[1].Value)) processes[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        return processes.Count == 0 ? null : new Procfile(processes);
    }

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*:\s*(\S.*)$")]
    private static partial Regex ProcessLine();
}

/// <summary>What a folder holds of a Python project: its dependency files, the dependencies they list, and how they install.</summary>
internal sealed partial class PythonProject
{
    private static readonly string[] ProductionRequirements =
        ["requirements/production.txt", "requirements/prod.txt", "requirements-production.txt", "requirements-prod.txt"];

    private readonly Dictionary<string, string?> _texts = new(StringComparer.Ordinal);
    private List<string>? _sources;

    private PythonProject(RepoFiles files) => Files = files;

    public RepoFiles Files { get; }
    public string RepoRoot { get; private init; } = "";
    public string AppDir { get; private init; } = "";
    public Dictionary<string, object?>? Pyproject { get; private init; }
    public Dictionary<string, object?>? Pipfile { get; private init; }
    public Procfile? Procfile { get; private init; }
    public IReadOnlyDictionary<string, PythonDependency> Dependencies { get; private init; } = new Dictionary<string, PythonDependency>();
    public PythonInstall Install { get; private init; } = new(PythonTool.None, "", true, false);
    public bool IsPackage { get; private init; }
    /// <summary>The dependency files that are there but not what the install uses, like a requirements.txt next to a uv.lock.</summary>
    public List<string> UnusedDependencyFiles { get; } = [];

    public bool HasDependencyFile => Install.Tool != PythonTool.None;

    public bool Has(string name) => Dependencies.ContainsKey(name);

    public PythonDependency? Dependency(string name) => Dependencies.GetValueOrDefault(name);

    /// <summary>A file's text, read once.</summary>
    public string? Text(string relativePath)
    {
        if (!_texts.TryGetValue(relativePath, out var text)) _texts[relativePath] = text = PythonFiles.Read(Files, relativePath);
        return text;
    }

    /// <summary>The app's own Python files (no tests, migrations or virtualenvs), shallowest first, up to a few hundred.</summary>
    public IReadOnlyList<string> Sources => _sources ??= PythonFiles.Sources(Files).Take(400).ToList();

    /// <summary>The folder's Python project, or null when nothing in it is Python.</summary>
    public static PythonProject? Load(string repoRoot, string appDir, RepoFiles files)
    {
        Dictionary<string, object?>? ReadToml(string file) => PythonFiles.Read(files, file) is { } text ? Toml.Parse(text) : null;
        var pyproject = ReadToml("pyproject.toml");
        var pipfile = files.Exists("Pipfile") ? ReadToml("Pipfile") ?? new Dictionary<string, object?>(StringComparer.Ordinal) : null;
        var setupPy = PythonFiles.Read(files, "setup.py");
        var setupCfg = PythonFiles.Read(files, "setup.cfg");
        var requirementsPath = RequirementsPath(files);
        var isProjectFile = pyproject is not null && new[] { "project", "build-system" }.Any(pyproject.ContainsKey)
            || Toml.Table(pyproject, "tool") is { } tool && new[] { "poetry", "uv", "pdm" }.Any(tool.ContainsKey);
        var hasSetup = setupPy is not null || setupCfg?.Contains("install_requires", StringComparison.Ordinal) == true;
        var hasRootScript = files.Files.Any(f => !f.Contains('/') && f.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (requirementsPath is null && !isProjectFile && pipfile is null && !hasSetup && !hasRootScript && !files.Exists("manage.py")) return null;

        var deps = new Dictionary<string, PythonDependency>(StringComparer.Ordinal);
        void Add(IEnumerable<PythonDependency> list)
        {
            foreach (var d in list)
                deps[d.Name] = deps.TryGetValue(d.Name, out var known) ? known with { Extras = known.Extras.Union(d.Extras).ToList() } : d;
        }
        var requirements = requirementsPath is null ? null : PythonDependencies.ReadRequirements(files, requirementsPath);
        if (requirements is not null) Add(requirements.Dependencies);
        if (pyproject is not null) Add(PythonDependencies.FromPyproject(pyproject));
        if (Toml.Table(pipfile, "packages") is { } packages) Add(PythonDependencies.FromTable(packages));
        Add(PythonDependencies.FromSetup(setupPy, setupCfg));

        var isPackage = IsPackageProject(pyproject, files);
        var project = new PythonProject(files)
        {
            RepoRoot = repoRoot,
            AppDir = appDir,
            Pyproject = pyproject,
            Pipfile = pipfile,
            Procfile = PythonFiles.Read(files, "Procfile") is { } procfile ? Procfile.Parse(procfile) : null,
            Dependencies = deps,
            IsPackage = isPackage,
            Install = PlanInstall(files, pyproject, pipfile is not null, hasSetup ? PythonDependencies.SetupRequires(setupPy, setupCfg).ToList() : null,
                requirementsPath, requirements, isPackage),
        };
        var install = project.Install;
        foreach (var file in new[] { "uv.lock", "poetry.lock", "pdm.lock", "Pipfile.lock", requirementsPath ?? "requirements.txt", "Pipfile" })
        {
            if (files.Exists(file) && file != install.Source && !(file == "Pipfile" && install.Tool == PythonTool.Pipenv) && !project.UnusedDependencyFiles.Contains(file))
                project.UnusedDependencyFiles.Add(file);
        }
        return project;
    }

    /// <summary>
    /// The requirements file pip installs: the root requirements.txt, unless it only pulls in others with -r and a
    /// production one exists (requirements/production.txt, prod.txt). Without a root file, the production one.
    /// </summary>
    internal static string? RequirementsPath(RepoFiles files)
    {
        var production = ProductionRequirements.FirstOrDefault(files.Exists);
        if (files.Exists("requirements.txt"))
            return production is not null && PythonDependencies.ReadRequirements(files, "requirements.txt").OnlyIncludes ? production : "requirements.txt";
        return production ?? (files.Exists("requirements/base.txt") ? "requirements/base.txt" : null);
    }

    /// <summary>
    /// A lockfile decides the tool first (uv, Poetry, PDM, Pipenv), then a requirements file (pip), then a tool's own
    /// settings without a lockfile, then a plain pyproject.toml (installed with uv pip), then setup.py.
    /// </summary>
    /// <param name="setupRequires">install_requires from setup.py or setup.cfg; null when there's neither.</param>
    private static PythonInstall PlanInstall(RepoFiles files, Dictionary<string, object?>? pyproject, bool hasPipfile, List<string>? setupRequires,
        string? requirementsPath, RequirementsFile? requirements, bool isPackage)
    {
        var tool = Toml.Table(pyproject, "tool");
        if (files.Exists("uv.lock")) return new PythonInstall(PythonTool.Uv, "uv.lock", true, isPackage);
        if (files.Exists("poetry.lock")) return new PythonInstall(PythonTool.Poetry, "poetry.lock", true, isPackage);
        if (files.Exists("pdm.lock")) return new PythonInstall(PythonTool.Pdm, "pdm.lock", true, isPackage);
        if (hasPipfile && files.Exists("Pipfile.lock")) return new PythonInstall(PythonTool.Pipenv, "Pipfile.lock", true, false);
        if (requirementsPath is not null) return new PythonInstall(PythonTool.Pip, requirementsPath, true, requirements?.InstallsProject == true, requirementsPath);
        if (tool?.ContainsKey("uv") == true) return new PythonInstall(PythonTool.Uv, "pyproject.toml", false, isPackage);
        if (tool?.ContainsKey("poetry") == true) return new PythonInstall(PythonTool.Poetry, "pyproject.toml", false, isPackage);
        if (tool?.ContainsKey("pdm") == true) return new PythonInstall(PythonTool.Pdm, "pyproject.toml", false, isPackage);
        if (hasPipfile) return new PythonInstall(PythonTool.Pipenv, "Pipfile", false, false);
        if (Toml.Table(pyproject, "project") is not null) return new PythonInstall(PythonTool.UvPip, "pyproject.toml", true, isPackage);
        if (setupRequires is null) return new PythonInstall(PythonTool.None, "", true, false);
        // An app's setup.py that only lists its dependencies installs those; installing the app itself would have
        // setuptools guess its packages, which fails on a flat layout.
        var setup = files.Exists("setup.py") ? "setup.py" : "setup.cfg";
        return isPackage || setupRequires.Count == 0
            ? new PythonInstall(PythonTool.Pip, setup, true, true)
            : new PythonInstall(PythonTool.Pip, setup, true, false, Packages: setupRequires);
    }

    /// <summary>
    /// Whether the project is a package that can be built and installed, rather than an app that only has
    /// dependencies. That takes a build system, and code the build backend can find: a folder or module named after
    /// the project, or packages its settings name. An app's pyproject.toml often has neither, and installing it fails.
    /// </summary>
    internal static bool IsPackageProject(Dictionary<string, object?>? pyproject, RepoFiles files)
    {
        var setup = PythonDependencies.SetupDeclaresPackages(PythonFiles.Read(files, "setup.py"), PythonFiles.Read(files, "setup.cfg"));
        if (pyproject is null) return setup;
        if (Toml.Bool(pyproject, "tool", "poetry", "package-mode") == false) return false;
        if (Toml.Bool(pyproject, "tool", "uv", "package") is bool uvPackage) return uvPackage;
        if (Toml.Bool(pyproject, "tool", "pdm", "distribution") is bool distribution) return distribution;
        if (Toml.Table(pyproject, "build-system") is null) return setup;
        if (setup
            || Toml.Get(pyproject, "tool", "poetry", "packages") is not null
            || Toml.Get(pyproject, "tool", "setuptools", "packages") is not null
            || Toml.Get(pyproject, "tool", "setuptools", "py-modules") is not null
            || Toml.Get(pyproject, "tool", "hatch", "build", "targets", "wheel", "packages") is not null
            || Toml.Get(pyproject, "tool", "flit", "module") is not null) return true;
        var name = Toml.String(pyproject, "project", "name") ?? Toml.String(pyproject, "tool", "poetry", "name");
        if (name is null) return false;
        var module = ImportName().Replace(name.Trim(), "_").ToLowerInvariant();
        return files.Exists($"{module}/__init__.py") || files.Exists($"src/{module}/__init__.py")
            || files.Exists($"{module}.py") || files.Exists($"src/{module}.py");
    }

    [GeneratedRegex(@"[-.]+")]
    private static partial Regex ImportName();
}
