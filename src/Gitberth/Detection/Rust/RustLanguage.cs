using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// Rust apps: a Cargo.toml with a binary, the crate's own or a workspace member's. Built in the rust image with Cargo's
/// caches kept between builds, and run on Debian slim from the crate's folder, with the repo's other files next to the binary.
/// </summary>
internal sealed partial class RustLanguage : ILanguage
{
    // Workspace members of examples, tests, benchmarks and build tooling rather than the app.
    private static readonly HashSet<string> AsideDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "example", "examples", "test", "tests", "bench", "benches", "benchmarks", "xtask", "tools", "fuzz", "docs",
    };

    private static readonly string[] ServerNames = ["server", "api", "web", "app", "backend", "service", "http"];

    public ProjectKind Kind => ProjectKind.Rust;

    public string Hint => "rust";

    public IReadOnlyList<string> Markers { get; } = ["Cargo.toml"];

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        var root = files.Exists("Cargo.toml") ? CargoManifest.Load(files.Root, "") : null;
        if (root is null) return null;
        if (WorkspaceAbove(repoRoot, appDir) is { } above) return root.Bins.Count == 0 ? null : MemberOnly(appDir, above);

        var members = root.IsWorkspace
            ? root.MemberDirs(files.Root).Where(d => !d.Split('/').Any(AsideDirs.Contains))
                .Select(d => CargoManifest.Load(files.Root, d, root)).OfType<CargoManifest>().Where(m => m.Name is not null and not "xtask").ToList()
            : [];
        var crates = members.Prepend(root).ToDictionary(m => m.Dir, StringComparer.Ordinal);
        var bins = crates.Values.SelectMany(c => c.Bins).ToList();
        // A binary that needs features beyond the defaults isn't built by a plain cargo build.
        var buildable = bins.Where(b => !b.RequiresFeatures).ToList() is { Count: > 0 } plain ? plain : bins;
        // Only libraries: nothing to run.
        if (buildable.Count == 0) return null;

        var bin = Pick(buildable, root, crates, appDir.Length == 0 ? "" : appDir[(appDir.LastIndexOf('/') + 1)..]);
        var crate = crates[bin.Dir];
        var r = new DetectionResult { Kind = ProjectKind.Rust, DockerIgnore = RustDockerfiles.Ignore };
        r.Evidence.Add(root.IsWorkspace
            ? $"Found Cargo.toml, a workspace; builds the {bin.Name} binary of {bin.Crate}{(bin.Dir.Length == 0 ? "" : $" ({bin.Dir}/)")}"
            : $"Found Cargo.toml; builds the {bin.Name} binary");
        if (buildable.Count > 1)
        {
            r.Warnings.Add($"Found {buildable.Count} binaries and builds {bin.Name}. The others are {string.Join(", ", buildable.Where(b => b != bin).Select(b => b.Name))}. "
                + "To run another one, switch the Dockerfile to Custom and change the binary in its cargo build line.");
        }

        var choice = RustImages.Resolve(files, crate.RustVersion ?? root.RustVersion);
        r.Evidence.Add(choice.Channel is null
            ? $"Rust {choice.Tag} ({choice.Source})"
            : $"Rust {choice.Channel} ({choice.Source}), which rustup installs during the build, on the rust:{choice.Tag} image");

        var locked = files.Exists("Cargo.lock");
        if (locked) r.Evidence.Add("Builds with --locked, from Cargo.lock");
        else r.Warnings.Add("No Cargo.lock is committed, so each build may resolve different dependency versions.");
        var packages = RuntimePackages(crates.Values, locked ? CargoManifest.LockedPackages(RustSources.Read(files.Full("Cargo.lock"))) : null, r);

        var target = BuildTarget(files);
        if (target is not null)
        {
            r.Evidence.Add($"Builds for {target}, as .cargo/config.toml says");
            if (target.Contains("musl", StringComparison.Ordinal))
                r.Warnings.Add($"The build targets {target}. Crates with C code need musl's C compiler for it (musl-tools), which the rust image doesn't have: if the build fails, add it in a custom Dockerfile.");
        }

        var sources = RustSources.Load(files, crate, bin);
        var framework = RustSources.Framework(crate);
        var web = RustSources.MayServe(crate, sources);
        r.Title = !web ? "Rust worker" : $"Rust ({framework?.Label ?? "TCP server"})";
        var name = RuntimeName(bin.Name, files.Root);
        var (start, port, env) = web ? Serve(r, framework, crate, sources, files, $"/app/{name}") : ([$"/app/{name}"], null, []);
        if (!web) r.Evidence.Add("Uses no web framework or TcpListener, so it runs as a worker without a port");
        if (crate.Dependencies.Contains("shuttle-runtime"))
            r.Warnings.Add("The app starts through #[shuttle_runtime::main], which only runs on Shuttle's platform. Give it a plain #[tokio::main] that listens on 0.0.0.0 and PORT to run it here.");
        r.ContainerPort = port;
        r.Evidence.Add("The repo's other files (templates, static files, config…) are next to the binary in /app, the working directory, as in the repo");
        r.Dockerfile = RustDockerfiles.For(new RustBuild(choice.Tag, locked, bin.Dir.Length == 0 ? null : bin.Crate, bin.Name,
            target is null ? "target/release" : $"target/{target}/release", target, name, packages, start, port, env));
        return new LanguageMatch(r, Strong: web);
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var file in RustSources.All(files))
        {
            foreach (var (key, compileTime) in RustSources.EnvKeys(file.Text))
            {
                // Cargo sets these for the build itself.
                if (key.StartsWith("CARGO", StringComparison.Ordinal) || key == "OUT_DIR") continue;
                add(key, "", compileTime ? $"read while it compiles, by env!() in {file.Path}: mark it Build-time" : $"used in {file.Path}");
            }
        }
        foreach (var config in files.Files.Where(f => f.StartsWith("config/", StringComparison.Ordinal) && (f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))))
            foreach (var key in RustSources.TeraEnvKeys(RustSources.Read(files.Full(config)))) add(key, "", $"used in {config}");
    }

    /// <summary>How the app starts, where it listens, and what the image sets for it.</summary>
    private static (IReadOnlyList<string> Start, int? Port, IReadOnlyList<string> Env) Serve(
        DetectionResult r, RustFramework? framework, CargoManifest crate, IReadOnlyList<RustFile> sources, RepoFiles files, string exe)
    {
        var env = new List<string>();
        int port;
        IReadOnlyList<string> start = [exe];
        switch (framework?.Label)
        {
            case "Rocket":
                var code = RustSources.RocketCodePort(sources);
                var toml = files.Exists("Rocket.toml") ? RustSources.RocketTomlPort(RustSources.Read(files.Full("Rocket.toml"))) : null;
                port = code ?? toml ?? framework.DefaultPort!.Value;
                r.Evidence.Add(code is not null ? $"Listens on port {port}, set in code" : toml is not null ? $"Listens on port {port} (Rocket.toml)" : $"Port {port} (Rocket's default)");
                // Rocket listens on 127.0.0.1 unless told otherwise, and reads its port from ROCKET_PORT, not PORT.
                env.AddRange(["ROCKET_ADDRESS=0.0.0.0", "ROCKET_IP_HEADER=X-Forwarded-For"]);
                start = ["sh", "-c", $"export ROCKET_PORT=${{PORT:-{port}}} && exec {exe}"];
                r.Evidence.Add("Rocket listens on every interface (ROCKET_ADDRESS=0.0.0.0), on PORT (as ROCKET_PORT)");
                r.Evidence.Add("Takes the client's address from X-Forwarded-For, which Cloudflare Tunnel sets (ROCKET_IP_HEADER)");
                break;
            case "Loco":
                port = framework.DefaultPort!.Value;
                start = ["sh", "-c", $"exec {exe} start --binding 0.0.0.0 --port ${{PORT:-{port}}}"];
                r.Evidence.Add($"Starts with `{exe[5..]} start --binding 0.0.0.0 --port $PORT`, on port {port} (Loco's default)");
                if (files.Exists("config/production.yaml"))
                {
                    env.Add("LOCO_ENV=production");
                    r.Evidence.Add("Runs in production, with config/production.yaml");
                }
                else
                {
                    r.Evidence.Add("Runs with config/development.yaml, since there's no config/production.yaml");
                }
                break;
            default:
                var listen = RustSources.ListenPort(sources);
                port = listen?.Port ?? LeptosPort(crate) ?? framework?.DefaultPort ?? 8080;
                r.Evidence.Add(listen is null
                    ? $"Port {port} (default; the app gets PORT={port})"
                    : listen.FromEnv ? $"Listens on PORT, or {port} when it isn't set ({listen.File})" : $"Listens on port {port} ({listen.File})");
                if (listen?.Localhost is { } local)
                    r.Warnings.Add($"{listen.File} listens on {local}, which can't be reached from outside the container. Listen on 0.0.0.0 instead, like \"0.0.0.0:{port}\".");
                if (framework?.Label == "Leptos")
                {
                    // Leptos reads its address from LEPTOS_SITE_ADDR, and site-addr in Cargo.toml defaults to 127.0.0.1.
                    env.Add($"LEPTOS_SITE_ADDR=0.0.0.0:{port}");
                    r.Warnings.Add("Leptos apps build with cargo-leptos, which also builds their WebAssembly front end. Gitberth runs a plain cargo build, so the pages work without their client-side code. For the full app, use a custom Dockerfile that runs cargo leptos build --release.");
                }
                break;
        }
        env.Insert(0, $"PORT={port}");
        return (start, port, env);
    }

    /// <summary>
    /// The binary to build: Cargo.toml's default-run, else one named like the package or the app's folder, then one whose
    /// crate uses a web framework, then Loco's -cli binary, then a usual server name, then the app's own crate's.
    /// </summary>
    private static CargoBin Pick(List<CargoBin> bins, CargoManifest root, Dictionary<string, CargoManifest> crates, string folder)
    {
        if (root.DefaultRun is { } run && bins.FirstOrDefault(b => b.Dir.Length == 0 && b.Name == run) is { } preferred) return preferred;
        return bins
            .OrderBy(b => Named(b, root.Name) || Named(b, folder) ? 0 : 1)
            .ThenBy(b => RustSources.Framework(crates[b.Dir]) is null ? 1 : 0)
            .ThenBy(b => crates[b.Dir].Dependencies.Contains("loco-rs") && b.Name.EndsWith("-cli", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(b => Array.IndexOf(ServerNames, b.Name.ToLowerInvariant()) is var i and >= 0 ? i : ServerNames.Length)
            .ThenBy(b => b.Dir.Length == 0 ? 0 : 1)
            .ThenBy(b => b.Dir.Count(c => c == '/'))
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .First();
    }

    private static bool Named(CargoBin bin, string? name) => !string.IsNullOrEmpty(name) && bin.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Debian packages for C libraries the binary links at run time, read from Cargo.lock, or without one from the
    /// crates' dependencies and features (diesel's postgres, rusqlite without bundled).
    /// </summary>
    private static List<string> RuntimePackages(IEnumerable<CargoManifest> crates, HashSet<string>? locked, DetectionResult r)
    {
        var all = crates.ToList();
        bool Uses(string sys) => locked?.Contains(sys) ?? all.Any(c => c.Dependencies.Contains(sys) || sys switch
        {
            "pq-sys" => c.HasFeature("diesel", "postgres"),
            "libsqlite3-sys" => c.HasFeature("diesel", "sqlite") || (c.Dependencies.Contains("rusqlite") && !c.HasFeature("rusqlite", "bundled")),
            "mysqlclient-sys" => c.HasFeature("diesel", "mysql"),
            _ => false,
        });
        var packages = new List<string>();
        foreach (var (sys, package, library) in RustSources.NativeLibraries)
        {
            if (!Uses(sys)) continue;
            packages.Add(package);
            r.Evidence.Add($"Links {library} (through {sys}), so the image gets {package}");
        }
        return packages;
    }

    /// <summary>Leptos' site-addr in [package.metadata.leptos]; its port, as the app listens there.</summary>
    private static int? LeptosPort(CargoManifest crate) =>
        RustToml.Str(crate.Document, "package", "metadata", "leptos", "site-addr") is { } addr && PortSuffix().Match(addr) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>A target triple .cargo/config.toml sets for every build ([build] target).</summary>
    private static string? BuildTarget(RepoFiles files)
    {
        var config = new[] { ".cargo/config.toml", ".cargo/config" }.FirstOrDefault(files.Exists);
        if (config is null) return null;
        return RustToml.Get(RustToml.Parse(RustSources.Read(files.Full(config))), "build", "target") switch
        {
            string triple => triple,
            List<object> triples => triples.OfType<string>().FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>The binary's name in /app, unless that clashes with something at the top of the repo, which is copied next to it.</summary>
    private static string RuntimeName(string binary, string root)
    {
        HashSet<string> taken;
        try { taken = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { taken = []; }
        if (!taken.Contains(binary)) return binary;
        return taken.Contains(binary + "-app") ? "gitberth-app" : binary + "-app";
    }

    /// <summary>The workspace above the app's folder that has it as a member, if any.</summary>
    private static (string Dir, CargoManifest Manifest)? WorkspaceAbove(string repoRoot, string appDir)
    {
        if (appDir.Length == 0) return null;
        var parts = appDir.Split('/');
        for (var n = parts.Length - 1; n >= 0; n--)
        {
            var dir = string.Join('/', parts.Take(n));
            if (CargoManifest.Load(repoRoot, dir) is not { IsWorkspace: true } manifest) continue;
            var folder = dir.Length == 0 ? repoRoot : Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            if (manifest.HasMember(folder, string.Join('/', parts.Skip(n)))) return (dir, manifest);
        }
        return null;
    }

    /// <summary>A workspace member chosen as the app's folder: it only builds from the workspace, so say where that is.</summary>
    private static LanguageMatch MemberOnly(string appDir, (string Dir, CargoManifest Manifest) workspace)
    {
        var where = workspace.Dir.Length == 0 ? "the repo root" : $"{workspace.Dir}/";
        var r = new DetectionResult { Kind = ProjectKind.Unknown, Title = "Rust workspace member" };
        r.Warnings.Add($"{appDir}/ is a member of the Cargo workspace in {where}, and only builds from there. Set the app's folder to {where} instead, and Gitberth builds the binary from there.");
        return new LanguageMatch(r, Strong: false);
    }

    [GeneratedRegex(@":(\d{2,5})$")]
    private static partial Regex PortSuffix();
}
