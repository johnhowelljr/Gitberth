using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A binary target of a crate.</summary>
/// <param name="Crate">The package it belongs to, for `cargo build -p`.</param>
/// <param name="Dir">The crate's folder, relative to the app's ("" for the app's own crate).</param>
/// <param name="RequiresFeatures">It only builds with features that aren't on by default.</param>
internal sealed record CargoBin(string Name, string Crate, string Dir, bool RequiresFeatures);

/// <summary>What Gitberth reads from one Cargo.toml: the package, its binaries and dependencies, and any workspace it roots.</summary>
internal sealed partial class CargoManifest
{
    public required string Dir { get; init; }

    /// <summary>The package name, or null for a virtual workspace manifest.</summary>
    public string? Name { get; init; }

    /// <summary>rust-version as written, inherited from the workspace when it says `rust-version.workspace = true`.</summary>
    public string? RustVersion { get; init; }

    public string? DefaultRun { get; init; }

    public List<CargoBin> Bins { get; } = [];

    /// <summary>The crates it depends on, by their real names (a dependency renamed with `package = "…"` counts as that package).</summary>
    public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);

    /// <summary>The features asked for on each dependency, for the ones that pull in C libraries (diesel's postgres, …).</summary>
    public Dictionary<string, List<string>> Features { get; } = new(StringComparer.Ordinal);

    /// <summary>Workspace member globs, when this manifest roots a workspace.</summary>
    public List<string>? Members { get; init; }

    public List<string> Exclude { get; init; } = [];

    public Dictionary<string, object> Document { get; init; } = [];

    public bool IsWorkspace => Members is not null;

    /// <summary>Reads the Cargo.toml in <paramref name="dir"/> (relative to <paramref name="appRoot"/>), or null when there's none.</summary>
    /// <param name="workspace">The workspace root's manifest, for values a member inherits.</param>
    public static CargoManifest? Load(string appRoot, string dir, CargoManifest? workspace = null)
    {
        var folder = dir.Length == 0 ? appRoot : Path.Combine(appRoot, dir.Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(folder, "Cargo.toml");
        if (!File.Exists(path)) return null;
        var doc = RustToml.Parse(RustSources.Read(path));
        var package = RustToml.Table(RustToml.Get(doc, "package"));
        var ws = RustToml.Table(RustToml.Get(doc, "workspace"));
        var name = RustToml.Str(package, "name");
        var rustVersion = RustToml.Get(package, "rust-version") switch
        {
            string v => v,
            Dictionary<string, object> inherit when RustToml.Str(inherit, "workspace") == "true" =>
                RustToml.Str(ws, "package", "rust-version") ?? RustToml.Str(workspace?.Document, "workspace", "package", "rust-version"),
            _ => null,
        };
        var manifest = new CargoManifest
        {
            Dir = dir,
            Name = name,
            RustVersion = rustVersion,
            DefaultRun = RustToml.Str(package, "default-run"),
            Members = ws is null ? null : RustToml.Strings(RustToml.Get(ws, "members")).ToList(),
            Exclude = RustToml.Strings(RustToml.Get(ws, "exclude")).ToList(),
            Document = doc,
        };
        manifest.ReadDependencies(doc, workspace);
        if (name is not null) manifest.ReadBins(folder, doc, package);
        return manifest;
    }

    /// <summary>Whether a dependency asks for a feature, like diesel's "postgres".</summary>
    public bool HasFeature(string crate, string feature) => Features.TryGetValue(crate, out var list) && list.Contains(feature);

    private void ReadDependencies(Dictionary<string, object> doc, CargoManifest? workspace)
    {
        var tables = new List<Dictionary<string, object>?> { RustToml.Table(RustToml.Get(doc, "dependencies")) };
        if (RustToml.Table(RustToml.Get(doc, "target")) is { } targets)
            tables.AddRange(targets.Values.Select(t => RustToml.Table(RustToml.Get(RustToml.Table(t), "dependencies"))));
        var inherited = RustToml.Table(RustToml.Get(doc, "workspace", "dependencies")) ?? RustToml.Table(RustToml.Get(workspace?.Document, "workspace", "dependencies"));
        foreach (var table in tables.OfType<Dictionary<string, object>>())
        {
            foreach (var (key, spec) in table)
            {
                var details = RustToml.Table(spec);
                // `serde = { workspace = true }` takes its package name and features from [workspace.dependencies].
                var shared = details is not null && RustToml.Str(details, "workspace") == "true" ? RustToml.Table(RustToml.Get(inherited, key)) : null;
                var crate = RustToml.Str(details, "package") ?? RustToml.Str(shared, "package") ?? key;
                Dependencies.Add(crate);
                var features = RustToml.Strings(RustToml.Get(details, "features")).Concat(RustToml.Strings(RustToml.Get(shared, "features"))).ToList();
                if (!Features.TryGetValue(crate, out var list)) Features[crate] = list = [];
                list.AddRange(features);
            }
        }
    }

    // Cargo's target discovery: src/main.rs is a binary named like the package, so is each src/bin/*.rs and src/bin/*/main.rs;
    // [[bin]] entries add to those or name them. RepoFiles leaves bin/ folders out of its index, so they're read from disk.
    private void ReadBins(string folder, Dictionary<string, object> doc, Dictionary<string, object>? package)
    {
        foreach (var bin in RustToml.Tables(RustToml.Get(doc, "bin")))
        {
            if (RustToml.Str(bin, "name") is not { } binName) continue;
            Bins.Add(new CargoBin(binName, Name!, Dir, RustToml.Strings(RustToml.Get(bin, "required-features")).Any()));
        }
        void Add(string binName)
        {
            if (!Bins.Any(b => b.Name == binName)) Bins.Add(new CargoBin(binName, Name!, Dir, false));
        }
        var declaredPaths = RustToml.Tables(RustToml.Get(doc, "bin")).Select(b => RustToml.Str(b, "path")?.Replace('\\', '/').TrimStart('.', '/')).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (File.Exists(Path.Combine(folder, "src", "main.rs")) && !declaredPaths.Contains("src/main.rs")) Add(Name!);
        if (RustToml.Str(package, "autobins") == "false") return;
        var binDir = Path.Combine(folder, "src", "bin");
        if (!Directory.Exists(binDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(binDir, "*.rs").Order(StringComparer.Ordinal))
                if (!declaredPaths.Contains($"src/bin/{Path.GetFileName(file)}")) Add(Path.GetFileNameWithoutExtension(file));
            foreach (var sub in Directory.EnumerateDirectories(binDir).Order(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(sub, "main.rs")) && !declaredPaths.Contains($"src/bin/{Path.GetFileName(sub)}/main.rs")) Add(Path.GetFileName(sub));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The folders, relative to the workspace root, that its member globs ("crates/*", "api") name and that hold a Cargo.toml.</summary>
    public IEnumerable<string> MemberDirs(string root)
    {
        var found = new List<string>();
        foreach (var glob in Members ?? [])
        {
            var parts = glob.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
            var dirs = new List<string> { "" };
            foreach (var part in parts)
            {
                var next = new List<string>();
                foreach (var dir in dirs)
                {
                    var full = dir.Length == 0 ? root : Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
                    if (part.IndexOfAny(['*', '?']) < 0)
                    {
                        if (Directory.Exists(Path.Combine(full, part))) next.Add(RepoFiles.Join(dir, part));
                        continue;
                    }
                    var pattern = new Regex("^" + Regex.Escape(part).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
                    try { next.AddRange(Directory.EnumerateDirectories(full).Select(Path.GetFileName).OfType<string>().Where(n => pattern.IsMatch(n)).Order(StringComparer.Ordinal).Select(n => RepoFiles.Join(dir, n))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                dirs = next;
            }
            found.AddRange(dirs.Where(d => d.Length > 0 && File.Exists(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar), "Cargo.toml"))));
        }
        return found.Distinct().Where(d => !Exclude.Any(e => d == e.Replace('\\', '/').Trim('/')));
    }

    /// <summary>Whether <paramref name="dir"/> (relative to this workspace's root) is one of its members.</summary>
    public bool HasMember(string root, string dir) => MemberDirs(root).Contains(dir);

    /// <summary>The package names in a Cargo.lock.</summary>
    public static HashSet<string> LockedPackages(string lockText) =>
        LockPackageName().Matches(lockText).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^name\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex LockPackageName();
}
