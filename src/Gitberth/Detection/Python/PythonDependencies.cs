using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A package the project depends on, by its normalized name (PEP 503: lower case, runs of "-", "_" and "." as one "-").</summary>
internal sealed record PythonDependency(string Name, IReadOnlyList<string> Extras)
{
    public bool HasExtra(string extra) => Extras.Any(e => e.Equals(extra, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a requirements file asks pip to install.</summary>
/// <param name="InstallsProject">It installs the project itself too ("-e ." or ".").</param>
/// <param name="OnlyIncludes">It has no requirements of its own, only -r lines that pull in other files.</param>
internal sealed record RequirementsFile(IReadOnlyList<PythonDependency> Dependencies, bool InstallsProject, bool OnlyIncludes);

/// <summary>Reads the dependencies a project declares: requirements files, pyproject.toml, Pipfile, setup.py and setup.cfg.</summary>
internal static partial class PythonDependencies
{
    public static string Normalize(string name) => NameSeparators().Replace(name.Trim(), "-").ToLowerInvariant();

    /// <summary>"fastapi[standard]>=0.115; python_version >= '3.10'" → fastapi with the extra "standard"; null for anything else.</summary>
    public static PythonDependency? FromPep508(string spec)
    {
        var m = Requirement().Match(spec);
        if (!m.Success) return null;
        var extras = m.Groups["extras"].Success
            ? m.Groups["extras"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new PythonDependency(Normalize(m.Groups["name"].Value), extras);
    }

    /// <summary>
    /// A pip requirements file and the files it includes with -r. Constraints (-c), index options and per-requirement
    /// options like --hash are skipped; a VCS link counts by its #egg= name.
    /// </summary>
    public static RequirementsFile ReadRequirements(RepoFiles files, string path)
    {
        var deps = new List<PythonDependency>();
        var installsProject = false;
        var onlyIncludes = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Read(string file, int depth)
        {
            if (depth > 5 || !seen.Add(file) || PythonFiles.Read(files, file) is not { } text) return;
            var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
            foreach (var line in Lines(text))
            {
                if (Option(line, "-r", "--requirement") is { } include)
                {
                    if (PythonFiles.Combine(dir, include) is { } included) Read(included, depth + 1);
                    continue;
                }
                if (Option(line, "-c", "--constraint") is not null) continue;
                var target = Option(line, "-e", "--editable") ?? line;
                if (target.StartsWith('-')) continue;
                if (depth == 0) onlyIncludes = false;
                // Per-requirement options (--hash=…, --config-settings …) come after the requirement itself.
                var options = target.IndexOf(" -", StringComparison.Ordinal);
                if (options > 0) target = target[..options].TrimEnd();
                if (target.StartsWith('.') || target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var local = target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? target[5..] : target;
                    if (local.Split('[')[0].TrimEnd('/') is "." or "") installsProject = true;
                    continue;
                }
                if (Egg().Match(target) is { Success: true } egg)
                {
                    deps.Add(new PythonDependency(Normalize(egg.Groups[1].Value), []));
                    continue;
                }
                if (target.Contains("://", StringComparison.Ordinal) && !target.Contains('@')) continue;
                if (FromPep508(target) is { } dep) deps.Add(dep);
            }
        }

        Read(path, 0);
        return new RequirementsFile(deps, installsProject, onlyIncludes && seen.Count > 1);
    }

    /// <summary>[project] dependencies, plus Poetry's [tool.poetry.dependencies] table.</summary>
    public static IEnumerable<PythonDependency> FromPyproject(Dictionary<string, object?> pyproject)
    {
        foreach (var spec in Toml.Strings(pyproject, "project", "dependencies"))
        {
            if (FromPep508(spec) is { } dep) yield return dep;
        }
        if (Toml.Table(pyproject, "tool", "poetry", "dependencies") is { } poetry)
        {
            foreach (var dep in FromTable(poetry)) yield return dep;
        }
    }

    /// <summary>
    /// A Poetry or Pipfile dependency table: name = "version", or name = { version = "…", extras = […] }. Optional
    /// ones only install with an extra, and "python" is the Python version, not a package.
    /// </summary>
    public static IEnumerable<PythonDependency> FromTable(Dictionary<string, object?> table)
    {
        foreach (var (key, value) in table)
        {
            if (key.Equals("python", StringComparison.OrdinalIgnoreCase)) continue;
            var details = value as Dictionary<string, object?>;
            if (Toml.Bool(details, "optional") == true) continue;
            yield return new PythonDependency(Normalize(key), Toml.Strings(details, "extras").ToList());
        }
    }

    /// <summary>install_requires from setup.py and setup.cfg, as dependencies.</summary>
    public static IEnumerable<PythonDependency> FromSetup(string? setupPy, string? setupCfg) =>
        SetupRequires(setupPy, setupCfg).Select(FromPep508).OfType<PythonDependency>();

    /// <summary>The requirement specs in install_requires: setup.py's literal list, or setup.cfg's [options] (one per line, or separated by ";").</summary>
    public static IEnumerable<string> SetupRequires(string? setupPy, string? setupCfg)
    {
        if (setupPy is not null && SetupPyRequires().Match(setupPy) is { Success: true } list)
        {
            foreach (Match quoted in Quoted().Matches(list.Groups[1].Value))
            {
                if (FromPep508(quoted.Groups[1].Value) is not null) yield return quoted.Groups[1].Value.Trim();
            }
        }
        if (setupCfg is null) yield break;
        var inRequires = false;
        foreach (var raw in setupCfg.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Split('#')[0];
            if (line.Trim().Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                var m = SetupCfgRequires().Match(line);
                inRequires = m.Success;
                if (!m.Success) continue;
                line = m.Groups[1].Value;
            }
            if (!inRequires) continue;
            foreach (var part in line.Split(';'))
            {
                if (FromPep508(part) is not null) yield return part.Trim();
            }
        }
    }

    /// <summary>Whether setup.py or setup.cfg names the packages or modules it ships; without that, setuptools guesses, and fails on an app's flat layout.</summary>
    public static bool SetupDeclaresPackages(string? setupPy, string? setupCfg) =>
        (setupPy is not null && SetupPyPackages().IsMatch(setupPy)) || (setupCfg is not null && SetupCfgPackages().IsMatch(setupCfg));

    /// <summary>The logical lines of a requirements file: continuations joined, comments and blank lines dropped.</summary>
    private static IEnumerable<string> Lines(string text)
    {
        var pending = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.EndsWith('\\') && !raw.TrimStart().StartsWith('#'))
            {
                pending += raw[..^1];
                continue;
            }
            var line = Comment().Replace(pending + raw, "").Trim();
            pending = "";
            if (line.Length > 0) yield return line;
        }
        var last = Comment().Replace(pending, "").Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>The value of "-r file", "-rfile", "--requirement file" or "--requirement=file"; null when the line isn't that option.</summary>
    private static string? Option(string line, string shortName, string longName)
    {
        if (line.StartsWith(longName, StringComparison.Ordinal))
        {
            var rest = line[longName.Length..];
            return rest.StartsWith('=') ? rest[1..].Trim() : rest.Length > 0 && char.IsWhiteSpace(rest[0]) ? rest.Trim() : null;
        }
        return line.StartsWith(shortName, StringComparison.Ordinal) && !line.StartsWith("--", StringComparison.Ordinal)
            ? line[shortName.Length..].TrimStart('=').Trim()
            : null;
    }

    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex NameSeparators();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)\s*(?:\[(?<extras>[^\]]*)\])?\s*(?:$|[<>=!~;@(,]|\s)")]
    private static partial Regex Requirement();

    [GeneratedRegex(@"#egg=([A-Za-z0-9][A-Za-z0-9._-]*)")]
    private static partial Regex Egg();

    [GeneratedRegex(@"(^|\s+)#.*$")]
    private static partial Regex Comment();

    [GeneratedRegex(@"install_requires\s*=\s*\[([^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex SetupPyRequires();

    [GeneratedRegex(@"['""]([^'""]+)['""]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^install_requires\s*=(.*)$")]
    private static partial Regex SetupCfgRequires();

    [GeneratedRegex(@"\b(?:packages|py_modules)\s*=")]
    private static partial Regex SetupPyPackages();

    [GeneratedRegex(@"^(?:packages|py_modules)\s*=", RegexOptions.Multiline)]
    private static partial Regex SetupCfgPackages();
}
