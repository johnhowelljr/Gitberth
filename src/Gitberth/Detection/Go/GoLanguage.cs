using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// Go apps: a go.mod with a main package, at the module root, under cmd/, or anywhere when it's the only one. Built with
/// the golang image go.mod asks for, into a binary that runs from the module's folder with the repo's other files next to
/// it, as it would from a checkout.
/// </summary>
internal sealed partial class GoLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Go;

    public string Hint => "go";

    public IReadOnlyList<string> Markers { get; } = ["go.mod"];

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        if (!files.Exists("go.mod")) return InsideModule(repoRoot, appDir, files);
        var mod = GoMod.Parse(GoSources.Read(files.Full("go.mod")));
        var sources = GoSources.Load(files);
        var mains = GoSources.MainDirs(sources);
        // A module without a main package is a library: nothing to run.
        if (mains.Count == 0) return null;

        var chosen = GoSources.PickMain(mains, mod.Name, appDir.Length == 0 ? "" : GoSources.Last(appDir), sources);
        var r = new DetectionResult { Kind = ProjectKind.Go, DockerIgnore = GoDockerfiles.Ignore };
        r.Evidence.Add($"Found go.mod (module {mod.Module}); builds the main package in {Where(chosen)}");
        if (mains.Count > 1)
        {
            r.Warnings.Add($"Found {mains.Count} main packages and builds the one in {Where(chosen)}. The others are in "
                + $"{string.Join(", ", mains.Where(m => m != chosen).Select(Where))}. To run another one, switch the Dockerfile to Custom and change the path at the end of its go build line.");
        }

        var (version, source) = GoImages.Resolve(mod);
        var image = version;
        r.Evidence.Add($"Go {version} ({source})");
        if (version.NewerThan(GoImages.Latest))
        {
            image = GoImages.Latest;
            r.Warnings.Add($"go.mod asks for Go {version}, which is newer than Gitberth knows. The build starts from golang:{image}, and Go downloads the {version} toolchain itself.");
        }
        else if (GoImages.OldestSupported.NewerThan(version))
        {
            r.Warnings.Add($"Go {version} no longer gets security fixes; only Go {GoImages.OldestSupported} and {GoImages.Latest} do. Raise the go line in go.mod to build with a supported release.");
        }
        foreach (var path in mod.LocalReplacements.Where(p => p.StartsWith("../", StringComparison.Ordinal) || p == ".." || p.StartsWith('/')))
            r.Warnings.Add($"go.mod replaces a module with {path}, outside the app's folder, so the build can't see it. Vendor the modules (go mod vendor), or build from a folder that holds both.");

        var modules = Modules(mod, files, r);
        var cgo = GoSources.CgoReason(sources, mod);
        r.Evidence.Add(cgo is null
            ? "A static binary (CGO_ENABLED=0), on Alpine with CA certificates and time zones"
            : $"Built with cgo because {cgo}, so it runs on Debian slim, which has the C library it links to");
        if (GoSources.CgoLibraries(sources) is { Count: > 0 } libs)
            r.Warnings.Add($"The cgo code links {string.Join(", ", libs)}, which the images may not have. If the build or the start fails, install {(libs.Count == 1 ? "it" : "them")} in a custom Dockerfile.");
        // A front end built into the binary, whose build output isn't committed: go build stops at the //go:embed line.
        foreach (var (file, path, package) in GoSources.MissingEmbeds(sources, files))
        {
            r.Warnings.Add($"{file} embeds {path}, which isn't in the repo{(package is null ? "" : $"; {package} probably builds it")}. "
                + "Gitberth only builds the Go code, so the build fails until it's there: commit it, or switch the Dockerfile to Custom and build it before go build.");
        }

        var binary = BinaryName(chosen, mod, files.Root);
        var framework = GoSources.Framework(sources);
        var web = framework is not null || GoSources.MayServe(sources);
        r.Title = !web ? "Go worker" : $"Go ({framework?.Label ?? (GoSources.ServesGrpc(sources) ? "gRPC" : "net/http")})";
        var (start, port, env) = web ? Serve(r, sources, chosen, binary, framework, files) : ([$"/app/{binary}"], null, []);
        if (!web) r.Evidence.Add("Uses no net/http, net or web framework, so it runs as a worker without a port");
        r.ContainerPort = port;
        r.Evidence.Add("The repo's other files (templates, static files, migrations…) are next to the binary in /app, the working directory, as in the repo");
        r.Dockerfile = GoDockerfiles.For(new GoBuild(image.ToString(), modules, cgo is not null, chosen.Length == 0 ? "." : $"./{chosen}", binary, start, port, env));
        return new LanguageMatch(r, Strong: web);
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var file in GoSources.Load(files))
            foreach (var key in GoSources.EnvKeys(file.Text)) add(key, "", $"used in {file.Path}");
    }

    /// <summary>How the app starts, where it listens, and what the image sets for it.</summary>
    private static (IReadOnlyList<string> Start, int? Port, IReadOnlyList<string> Env) Serve(
        DetectionResult r, IReadOnlyList<GoFile> sources, string chosen, string binary, GoFramework? framework, RepoFiles files)
    {
        var exe = $"/app/{binary}";
        var mainFiles = sources.Where(f => f.Dir == chosen).ToList();
        var env = new List<string>();
        int port;
        IReadOnlyList<string> start;
        if (framework?.Label == "PocketBase")
        {
            // PocketBase's own `serve` command listens on 127.0.0.1:8090 unless told otherwise.
            port = framework.DefaultPort!.Value;
            start = Shell($"exec {exe} serve --http=0.0.0.0:${{PORT:-{port}}}");
            r.Evidence.Add($"Starts with `{binary} serve --http=0.0.0.0:$PORT`, PocketBase's server, on port {port} (its default)");
            r.Warnings.Add("PocketBase keeps its database and uploads in pb_data/, next to the binary. Add a volume at /app/pb_data, or they're lost on every deploy.");
        }
        else
        {
            var command = GoSources.ServeCommand(sources);
            var flag = GoSources.ListenFlag(command is { } c ? [c.File, .. mainFiles] : mainFiles);
            var listen = GoSources.ListenPort(mainFiles.Concat(sources.Where(f => f.Dir != chosen)));
            var words = command is { } cmd ? $" {cmd.Command}" : "";
            if (command is { } serve) r.Evidence.Add($"Starts its `{serve.Command}` command ({serve.File.Path})");
            if (flag is not null)
            {
                port = flag.Port;
                var value = (flag.TakesAddress ? ":" : "") + "${PORT:-" + port + "}";
                start = Shell($"exec {exe}{words} --{flag.Name}={value}");
                r.Evidence.Add($"Its --{flag.Name} flag sets where it listens ({flag.File}), so it starts with `{binary}{words} --{flag.Name}={(flag.TakesAddress ? ":" : "")}$PORT`");
                r.Evidence.Add($"Port {port}, the flag's default");
            }
            else
            {
                start = command is { } only ? [exe, only.Command] : [exe];
                if (listen is not null)
                {
                    port = listen.Port;
                    r.Evidence.Add(listen.FromEnv ? $"Listens on PORT, or {port} when it isn't set ({listen.File})" : $"Listens on port {port} ({listen.File})");
                }
                else if (framework?.Label == "Beego" && GoSources.BeegoPort(files) is int beego)
                {
                    port = beego;
                    r.Evidence.Add($"Listens on port {port} (httpport in conf/app.conf)");
                }
                else
                {
                    port = framework?.DefaultPort ?? 8080;
                    r.Evidence.Add(framework?.DefaultPort is null
                        ? $"Port {port} (default; the app gets PORT={port})"
                        : $"Port {port} ({framework.Label}'s default; the app gets PORT={port})");
                }
            }
            // An address flag is set to every interface by Gitberth; any other localhost address is in the code.
            if (flag is not { TakesAddress: true } && LocalBinding(mainFiles, listen) is { } local)
                r.Warnings.Add($"{local.File} listens on {local.Host}, which can't be reached from outside the container. Listen on \":{port}\" (every interface) instead.");
        }
        env.Add($"PORT={port}");
        if (framework?.Label == "Gin") env.Add("GIN_MODE=release");
        if (framework?.Label == "Buffalo")
        {
            // Buffalo listens on 127.0.0.1 in development, its default mode.
            env.Add("GO_ENV=production");
            env.Add("ADDR=0.0.0.0");
            r.Evidence.Add("Runs Buffalo in production mode on every interface (GO_ENV=production, ADDR=0.0.0.0)");
        }
        return (start, port, env);
    }

    /// <summary>A localhost address the app listens on: the one its port came from, else one in the main package.</summary>
    private static (string File, string Host)? LocalBinding(IReadOnlyList<GoFile> mainFiles, GoPort? listen)
    {
        if (listen?.Localhost is { } host) return (listen.File, host);
        foreach (var file in mainFiles)
            if (GoSources.LocalListen(file.Text) is { } local) return (file.Path, local);
        return null;
    }

    /// <summary>How the modules reach the build: vendored, downloaded first from go.sum, tidied into a go.sum, or none needed.</summary>
    private static GoModules Modules(GoMod mod, RepoFiles files, DetectionResult r)
    {
        // RepoFiles leaves vendor/ out of its index, so it's checked on disk.
        if (File.Exists(Path.Combine(files.Root, "vendor", "modules.txt")))
        {
            r.Evidence.Add("The modules come from vendor/");
            return GoModules.Vendor;
        }
        if (mod.Requirements.Count == 0)
        {
            r.Evidence.Add("Uses only the standard library, so there are no modules to download");
            return GoModules.None;
        }
        if (!files.Exists("go.sum"))
        {
            r.Warnings.Add("go.sum isn't committed, so the build runs go mod tidy to write it. Commit go.sum, so every build checks the modules against the checksums you tested with.");
            return GoModules.Tidy;
        }
        // A module replaced by a local folder needs that folder, which isn't there before the rest of the repo is copied.
        if (mod.LocalReplacements.Count > 0)
        {
            r.Evidence.Add("The modules download during the build, since go.mod replaces some with local folders");
            return GoModules.None;
        }
        r.Evidence.Add("The modules download from go.sum first, in a layer that's reused until go.mod or go.sum change");
        return GoModules.Download;
    }

    /// <summary>
    /// Named after its folder (or the module, at the root), unless that clashes with something at the top of the repo,
    /// which is copied next to it.
    /// </summary>
    private static string BinaryName(string chosen, GoMod mod, string root)
    {
        var name = chosen.Length > 0 ? GoSources.Last(chosen) : mod.Name;
        if (!SafeName().IsMatch(name)) name = "app";
        var taken = TopLevelNames(root);
        if (taken.Contains(name)) name += "-app";
        return taken.Contains(name) ? "gitberth-app" : name;
    }

    private static HashSet<string> TopLevelNames(string root)
    {
        try { return Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// A main package in a sub-folder of a module, like cmd/server, chosen as the app's folder: it only builds from the
    /// module, so say where that is rather than offering nothing.
    /// </summary>
    private static LanguageMatch? InsideModule(string repoRoot, string appDir, RepoFiles files)
    {
        if (appDir.Length == 0) return null;
        var main = files.Files.Where(f => !f.Contains('/') && f.EndsWith(".go", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase))
            .Any(f => GoSources.PackageClause().Match(GoSources.Read(files.Full(f))) is { Success: true } m && m.Groups[1].Value == "main");
        if (!main) return null;
        var parts = appDir.Split('/');
        for (var n = parts.Length - 1; n >= 0; n--)
        {
            var dir = string.Join('/', parts.Take(n));
            if (!File.Exists(Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar), "go.mod"))) continue;
            var module = dir.Length == 0 ? "the repo root" : $"{dir}/";
            var r = new DetectionResult { Kind = ProjectKind.Unknown, Title = "Go main package inside a module" };
            r.Warnings.Add($"{appDir}/ is a main package of the Go module in {module}, and only builds from there. Set the app's folder to {module} instead, and Gitberth builds the module's main package.");
            return new LanguageMatch(r, Strong: false);
        }
        return null;
    }

    private static string Where(string dir) => dir.Length == 0 ? "the module root" : $"{dir}/";

    private static string[] Shell(string command) => ["sh", "-c", command];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeName();
}
