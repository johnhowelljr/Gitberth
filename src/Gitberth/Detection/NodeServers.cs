using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

// Node.js and Bun servers: what starts them, the port they listen on, and what would keep them from working in a container.
public sealed partial class ProjectAnalyzer
{
    /// <summary>Server frameworks, first match wins, with the port each uses when the code doesn't say (null: none of its own).</summary>
    private static readonly (string Package, string Label, int? Port)[] ServerFrameworks =
    [
        ("@nestjs/core", "NestJS", 3000), ("@strapi/strapi", "Strapi", 1337), ("@adonisjs/core", "AdonisJS", 3333),
        ("@feathersjs/feathers", "Feathers", 3030), ("@loopback/core", "LoopBack", 3000), ("sails", "Sails", 1337),
        ("express", "Express", null), ("fastify", "Fastify", 3000), ("koa", "Koa", null), ("@hapi/hapi", "hapi", null),
        ("hono", "Hono", 3000), ("elysia", "Elysia", null), ("restify", "Restify", null),
        ("@remix-run/serve", "Remix", 3000), ("@react-router/serve", "React Router", 3000),
    ];

    // Files a server usually starts from, tried in this order when package.json doesn't name one.
    private static readonly string[] JsEntries =
    [
        "bin/www", "server.js", "index.js", "app.js", "main.js", "server.mjs", "index.mjs", "app.mjs", "server.cjs", "index.cjs",
        "src/server.js", "src/index.js", "src/app.js", "src/main.js", "src/server.mjs", "src/index.mjs",
    ];

    private static readonly string[] TsEntries =
    [
        "src/index.ts", "src/server.ts", "src/main.ts", "src/app.ts", "index.ts", "server.ts", "main.ts", "app.ts",
        "src/index.mts", "src/server.mts", "index.mts", "server.mts",
    ];

    // Where the code that picks the port usually is, after the entry and the files it imports.
    private static readonly string[] PortFiles =
    [
        "src/main.ts", "src/index.ts", "src/server.ts", "src/app.ts", "server.ts", "index.ts", "server.js", "index.js", "app.js",
        "bin/www", "src/server.js", "src/index.js", "src/app.js", "src/main.js", "main.js", "server.mjs", "index.mjs",
        "src/config.ts", "src/config/index.ts", "src/env.ts", "config.js",
    ];

    private static readonly string[] OutputFolders = ["dist", "build", "lib", "out", "output", ".output"];
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".tsx", ".jsx"];
    private static readonly string[] TsExtensions = [".ts", ".mts", ".cts", ".tsx"];

    // Options of node, nodemon, tsx and the like that take the next word as their value, which is never the app's file.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "--env-file", "--env-file-if-exists", "-C",
        "--conditions", "--config", "--exec", "-x", "--ext", "-e", "--ignore", "-i", "--signal", "--delay", "--title",
    };

    // Packages that keep sessions or sign people in, which need the framework to trust the tunnel's X-Forwarded-Proto.
    private static readonly string[] ExpressSessions = ["express-session", "cookie-session", "passport"];
    private static readonly string[] FastifySessions = ["@fastify/session", "@fastify/secure-session", "@fastify/passport"];
    private static readonly string[] KoaSessions = ["koa-session", "koa-passport", "koa-generic-session"];

    /// <summary>How a server starts.</summary>
    /// <param name="Command">The image's CMD.</param>
    /// <param name="Source">The file whose code shows where it listens, when known.</param>
    /// <param name="Script">The package.json script it runs, for a PORT= in it.</param>
    /// <param name="Build">The build, with anything starting it needs (tsx, a build command).</param>
    /// <param name="RunDir">The folder it starts in, when it isn't the app's own.</param>
    /// <param name="Env">More ENV settings it needs, like "LOG_LEVEL=info".</param>
    private sealed record ServerStart(string[] Command, string? Source, string? Script, NodeBuild Build, string? RunDir = null, string? Env = null);

    /// <summary>A Node.js or Bun server: its start command, its port, and anything that would stop it working in a container.</summary>
    private static DetectionResult NodeServer(DetectionResult r, string root, PackageJson pkg, NodeBuild b, int node)
    {
        var framework = ServerFrameworks.FirstOrDefault(f => pkg.Has(f.Package));
        r.Kind = ProjectKind.NodeServer;
        var runtime = b.OnBun ? "Bun" : "Node.js";
        r.Title = framework.Label is null ? $"{runtime} server" : $"{runtime} server ({framework.Label})";
        // Feathers apps, and others, compile with "compile".
        if (b.Build is null && pkg.Scripts.ContainsKey("compile"))
        {
            b = b with { Build = b.Pm.Run("compile") };
            r.Evidence.Add($"Builds with `{b.Build}`, as package.json has no \"build\" script");
        }

        var start = framework.Label == "AdonisJS" && AdonisServer(root) is { } server ? AdonisStart(r, root, b, server)
            : b.OnBun ? BunStart(r, root, pkg, b)
            : NodeStart(r, root, pkg, b, node, framework.Label);
        var env = start.Env;
        // fastify-cli listens on localhost unless told otherwise (it only guesses Docker), so it's told.
        if (start.Script is { } script && LastCommand(script) is ["fastify", "start", ..])
        {
            env = env is null ? "FASTIFY_ADDRESS=0.0.0.0" : env + " FASTIFY_ADDRESS=0.0.0.0";
            r.Evidence.Add("fastify-cli gets FASTIFY_ADDRESS=0.0.0.0, so it listens beyond localhost");
        }

        var files = ServerFiles(root, start.Source);
        var found = ServerPort(root, start.Script, files);
        var port = found?.Port ?? framework.Port ?? 3000;
        r.ContainerPort = port;
        r.Evidence.Add(found is { } f ? $"Listens on port {port} (from {f.From})"
            : framework.Port is not null ? $"Port {port} ({framework.Label}'s default; the app gets PORT={port})"
            : $"Port {port} (default; the app gets PORT={port})");
        ListenWarnings(r, root, pkg, files, start.Script);
        if (framework.Label == "Strapi") StrapiWarnings(r, root, start.Build);
        r.Dockerfile = DockerfileTemplates.NodeServer(node, start.Build, start.Command, port, env, start.RunDir);
        return r;
    }

    /// <summary>
    /// How a Node app starts. start:prod (NestJS) or start, unless it runs a development server; NestJS runs its build's
    /// output rather than `nest start`. Otherwise the file a development start script runs, main, the dev script's file,
    /// or one of the usual names; TypeScript runs as <see cref="TypeScriptStart"/> says.
    /// </summary>
    private static ServerStart NodeStart(DetectionResult r, string root, PackageJson pkg, NodeBuild b, int node, string? framework)
    {
        var script = ProductionScript(pkg);
        if (framework == "NestJS" && (script is null || (script.Value.Name == "start" && LastCommand(script.Value.Command) is ["nest", "start", ..])))
            return NestStart(r, root, pkg, b, script?.Command);
        if (script is { } s)
        {
            var command = s.Name == "start" ? b.Pm.StartCommand : b.Pm.RunCommand(s.Name);
            r.Evidence.Add($"Starts with `{string.Join(' ', command)}`");
            var file = ScriptFile(root, pkg, s.Command);
            if (file is not null) CheckScriptFile(r, root, b, node, s.Name, s.Command, file);
            return new ServerStart(command, file is null ? null : SourceFor(root, file), s.Command, b);
        }

        if (FindServerEntry(root, pkg, b, out var dev, out var missingMain) is not { } entry)
        {
            r.Warnings.Add(MissingEntry(missingMain));
            return new ServerStart(b.Pm.StartCommand, null, null, b);
        }
        string[] run = ["node", entry];
        string? note = null;
        if (TsExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
            (run, b, note) = TypeScriptStart(r, root, pkg, b, node, entry);
        r.Evidence.Add($"Starts with `{string.Join(' ', run)}`");
        if (note is not null) r.Evidence.Add(note);
        if (dev is { } d) r.Evidence.Add($"The {d.Name} script (`{d.Command}`) is for development, so Gitberth runs its file directly");
        return new ServerStart(run, SourceFor(root, entry), null, b);
    }

    /// <summary>
    /// A Bun app runs its start script with bun, or else the file its dev script, main or the usual names point at. Bun
    /// runs TypeScript as it is, so there's nothing to compile first.
    /// </summary>
    private static ServerStart BunStart(DetectionResult r, string root, PackageJson pkg, NodeBuild b)
    {
        if (ProductionScript(pkg) is { } s)
        {
            var command = s.Name == "start" ? b.Pm.StartCommand : b.Pm.RunCommand(s.Name);
            r.Evidence.Add($"Starts with `{string.Join(' ', command)}`");
            var file = ScriptFile(root, pkg, s.Command);
            return new ServerStart(command, file is null ? null : SourceFor(root, file), s.Command, b);
        }
        if (FindServerEntry(root, pkg, b, out var dev, out var missingMain) is not { } entry)
        {
            r.Warnings.Add(MissingEntry(missingMain));
            return new ServerStart(b.Pm.StartCommand, null, null, b);
        }
        var typescript = TsExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase);
        r.Evidence.Add($"Starts with `bun run {entry}`{(typescript ? "; Bun runs TypeScript as it is" : "")}");
        if (dev is { } d) r.Evidence.Add($"The {d.Name} script (`{d.Command}`) is for development, so Gitberth runs its file directly");
        return new ServerStart(["bun", "run", entry], SourceFor(root, entry), null, b);
    }

    /// <summary>
    /// `nest start` compiles the app every time it starts, so a NestJS app without start:prod runs the build's output:
    /// nest-cli.json's entryFile (main) under sourceRoot (src), compiled with the tsconfig `nest build` uses.
    /// </summary>
    private static ServerStart NestStart(DetectionResult r, string root, PackageJson pkg, NodeBuild b, string? script)
    {
        var cli = ReadFirst(root, "nest-cli.json") ?? "";
        var entryFile = NestEntryFile().Match(cli) is { Success: true } e ? e.Groups[1].Value : "main";
        var sourceRoot = NestSourceRoot().Match(cli) is { Success: true } s ? Clean(s.Groups[1].Value) : "src";
        var source = RepoFiles.Join(sourceRoot, entryFile + ".ts");
        if (b.Build is null)
        {
            b = b with { Build = "npx nest build" };
            r.Evidence.Add("Builds with `npx nest build`, as package.json has no \"build\" script");
        }
        var built = BuiltPath(root, pkg, b, source) ?? $"dist/{entryFile}.js";
        r.Evidence.Add($"Starts with `node {built}`, the build's output, rather than `nest start`, which compiles the app again each time it starts");
        return new ServerStart(["node", built], File.Exists(Path.Combine(root, source)) ? source : SourceFor(root, built), script, b);
    }

    /// <summary>The compiled server AdonisJS's build writes to build/: bin/server.js from v6 on, server.js in v5. Null when it's neither.</summary>
    private static string? AdonisServer(string root) =>
        File.Exists(Path.Combine(root, "adonisrc.ts")) || File.Exists(Path.Combine(root, "adonisrc.js")) ? "bin/server.js"
        : File.Exists(Path.Combine(root, ".adonisrc.json")) ? "server.js"
        : null;

    /// <summary>
    /// AdonisJS builds with `node ace build` into the tsconfig's outDir (build/), which holds the compiled app and a copy
    /// of package.json, and starts from there, as its deployment guide says; packages resolve from the node_modules above
    /// it. Its env.ts validates the environment at startup, so the variables it requires must be set.
    /// </summary>
    private static ServerStart AdonisStart(DetectionResult r, string root, NodeBuild b, string server)
    {
        if (b.Build is null)
        {
            b = b with { Build = "node ace build" };
            r.Evidence.Add("Builds with `node ace build`, as package.json has no \"build\" script");
        }
        var output = TsConfig.Load(root)?.OutDir is { Length: > 0 } outDir ? outDir : "build";
        r.Evidence.Add($"Starts with `node {server}` in {output}/, the compiled app `node ace build` writes");
        var schema = ReadFirst(root, "start/env.ts", "start/env.js", "env.ts") ?? "";
        string? env = null;
        if (schema.Contains("LOG_LEVEL", StringComparison.Ordinal))
        {
            env = "LOG_LEVEL=info";
            r.Evidence.Add("LOG_LEVEL is info unless you set it, because the app's env.ts requires it");
        }
        if (schema.Contains("APP_KEY", StringComparison.Ordinal))
            r.Warnings.Add("AdonisJS won't start without APP_KEY. Set it on the Environment tab to a random string of 32 characters or more; `node ace generate:key` prints one.");
        var source = Path.ChangeExtension(server, ".ts");
        return new ServerStart(["node", server], File.Exists(Path.Combine(root, source)) ? source : null, null, b, $"{b.WorkDir}/{output}", env);
    }

    /// <summary>
    /// Runs a TypeScript entry. When the build compiles it (tsc, tsup), its output runs. Otherwise Node runs it as it is
    /// when it can strip the types (see <see cref="TypeStripping"/>), and tsx runs it when Node can't: the app's own tsx,
    /// or one Gitberth adds to the image, so it works without a build step either way.
    /// </summary>
    private static (string[] Command, NodeBuild Build, string Note) TypeScriptStart(DetectionResult r, string root, PackageJson pkg, NodeBuild b, int node, string ts)
    {
        if (BuiltPath(root, pkg, b, ts) is { } built)
            return (["node", built], b, $"{built} is what the build compiles {ts} into");
        var (why, decorators) = TypeStripping.Check(root, ts, node);
        if (why is null) return (["node", ts], b, $"Node {node} runs {ts} by stripping its types, so there's no build step");
        if (decorators && TsConfig.Load(root) is { EmitDecoratorMetadata: true })
            r.Warnings.Add($"{ts} uses decorators with emitDecoratorMetadata, which tsx can't emit, so dependency injection may fail. Add a \"build\" script that runs tsc, and a \"start\" script that runs its output.");
        string[] command = ["node", "--import", "tsx", ts];
        if (pkg.Has("tsx")) return (command, b, $"tsx runs {ts}, because {why}");
        b = b with { Installs = [.. b.Installs, $"RUN {b.Pm.CacheMount}{b.Pm.AddDevCommand("tsx@4")}"] };
        return (command, b, $"Gitberth adds tsx to the image to run {ts}, because {why}");
    }

    /// <summary>
    /// Where the build writes a TypeScript file, when its script runs tsc, `nest build` or tsup: the tsconfig's outDir,
    /// keeping the path below rootDir. Without a rootDir, TypeScript 6 and later keep the path from the tsconfig's folder,
    /// while 5 and earlier start from the folder all the sources share (src, usually). Null when nothing compiles it.
    /// </summary>
    internal static string? BuiltPath(string root, PackageJson pkg, NodeBuild b, string ts)
    {
        if (b.Build is null) return null;
        var script = pkg.Scripts.GetValueOrDefault("build") ?? pkg.Scripts.GetValueOrDefault("compile") ?? b.Build;
        var words = CommandSeparators().Split(script).SelectMany(c => c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
        var js = Path.GetExtension(ts).ToLowerInvariant() switch { ".mts" => ".mjs", ".cts" => ".cjs", _ => ".js" };
        if (words.Contains("tsup")) return $"dist/{Path.GetFileNameWithoutExtension(ts)}{js}";
        var nest = words.Contains("nest");
        if (!words.Contains("tsc") && !words.Contains("lb-tsc") && !nest) return null;
        var project = words.SkipWhile(w => w is not ("-p" or "--project")).Skip(1).FirstOrDefault()
            ?? (nest && File.Exists(Path.Combine(root, "tsconfig.build.json")) ? "tsconfig.build.json" : "tsconfig.json");
        // `tsc -p .` names a folder, which means its tsconfig.json.
        if (Directory.Exists(Path.Combine(root, project))) project = Path.Combine(project, "tsconfig.json");
        if (TsConfig.Load(root, project) is not { } config) return null;
        var rootDir = config.RootDir ?? (TypeScriptMajor(pkg) is < 6 ? CommonSourceFolder(root, config) : config.Folder);
        if (rootDir.Length > 0 && !ts.StartsWith(rootDir + "/", StringComparison.Ordinal)) return null;
        var relative = rootDir.Length == 0 ? ts : ts[(rootDir.Length + 1)..];
        var output = Path.ChangeExtension(relative, js).Replace('\\', '/');
        return config.OutDir is { } outDir ? RepoFiles.Join(outDir, output) : RepoFiles.Join(rootDir, output);
    }

    /// <summary>The TypeScript major package.json asks for ("^5.9.3" → 5), or null when it doesn't say.</summary>
    private static int? TypeScriptMajor(PackageJson pkg) =>
        pkg.Versions.TryGetValue("typescript", out var spec) && FirstNumber().Match(spec) is { Success: true } m
            ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The folder TypeScript 5 and earlier take as rootDir when none is set: the one all the compiled sources share. Tests,
    /// declarations and the output are left out, as a tsconfig usually excludes them; "src" when that's where they all are.
    /// </summary>
    private static string CommonSourceFolder(string root, TsConfig config)
    {
        if (config.Include.Count > 0 && config.Include.All(i => i == "src" || i.StartsWith("src/", StringComparison.Ordinal))) return "src";
        var sources = RepoFiles.Scan(root, 5000).Files
            .Where(f => TsExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !f.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split('/').Any(p => p is "test" or "tests" or "__tests__" or "e2e") && !TestFile().IsMatch(f))
            .Where(f => config.OutDir is not { } outDir || !f.StartsWith(outDir + "/", StringComparison.Ordinal));
        return sources.Any() && sources.All(f => f.StartsWith("src/", StringComparison.Ordinal)) ? "src" : "";
    }

    /// <summary>The script that runs the app in production: start:prod when it exists, else start; null when there's none, or it runs a development server.</summary>
    private static (string Name, string Command)? ProductionScript(PackageJson pkg)
    {
        foreach (var name in new[] { "start:prod", "start" })
        {
            if (pkg.Scripts.TryGetValue(name, out var command) && !string.IsNullOrWhiteSpace(command) && !IsDevCommand(pkg, command))
                return (name, command);
        }
        return null;
    }

    /// <summary>
    /// The file a server starts from when no script says how to run it: the one a development start script runs
    /// (`nodemon server.js`), package.json's main (also when it's the build's output), the dev script's file, then the
    /// usual names. Null when there's none.
    /// </summary>
    /// <param name="dev">The development script the file came from, if it did.</param>
    /// <param name="missingMain">main, when it names a file that isn't there and no build makes it.</param>
    private static string? FindServerEntry(string root, PackageJson pkg, NodeBuild b, out (string Name, string Command)? dev, out string? missingMain)
    {
        dev = null;
        missingMain = null;
        bool Usable(string path) => File.Exists(Path.Combine(root, path)) || (b.Build is not null && IsBuildOutput(root, path));
        if (pkg.Scripts.TryGetValue("start", out var start) && ScriptFile(root, pkg, start) is { } fromStart && Usable(fromStart))
        {
            dev = ("start", start);
            return fromStart;
        }
        if (!string.IsNullOrWhiteSpace(pkg.Main))
        {
            var main = Clean(pkg.Main);
            if (Usable(main)) return main;
            missingMain = main;
        }
        if (pkg.Scripts.TryGetValue("dev", out var devScript) && ScriptFile(root, pkg, devScript) is { } fromDev && Usable(fromDev))
        {
            dev = ("dev", devScript);
            return fromDev;
        }
        return JsEntries.Concat(TsEntries).FirstOrDefault(f => File.Exists(Path.Combine(root, f)));
    }

    private static string MissingEntry(string? missingMain) =>
        (missingMain is null ? "" : $"package.json's \"main\" is {missingMain}, which isn't in the repo, and no build script makes it. ")
        + "No start script or server file was found (server.js, index.js, app.js, bin/www, src/index.ts and the like). "
        + "Add a \"start\" script to package.json, or customize the Dockerfile.";

    /// <summary>Warns when a start script runs a file that won't be there, or TypeScript that plain node can't run.</summary>
    private static void CheckScriptFile(DetectionResult r, string root, NodeBuild b, int node, string name, string command, string file)
    {
        var full = Path.Combine(root, file);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            if (b.Build is null)
                r.Warnings.Add($"The \"{name}\" script runs {file}, which isn't in the repo, and there's no build script to make it.");
            return;
        }
        // tsx, ts-node and bun run TypeScript themselves; so does node with a loader such as `--import tsx`.
        var words = LastCommand(command);
        if (!TsExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || words is not ["node", ..]
            || words.Any(w => w.Contains("tsx", StringComparison.Ordinal) || w.Contains("ts-node", StringComparison.Ordinal))) return;
        if (TypeStripping.Check(root, file, node, strict: false).Why is { } why)
            r.Warnings.Add($"The \"{name}\" script runs {file} with node, which can't run it, because {why}. Run it with tsx (`node --import tsx {file}`), or compile it in a \"build\" script.");
    }

    /// <summary>
    /// The file a script runs (`node server.js`, `nodemon src/index.ts`, `node -r dotenv/config dist/main`, `node .`,
    /// `bun run src/index.ts`), following `npm run x` into script x; null when it names none.
    /// </summary>
    internal static string? ScriptFile(string root, PackageJson pkg, string command, int depth = 0)
    {
        var words = LastCommand(command);
        if (words.Count == 0) return null;
        if (ScriptCalled(pkg, words) is { } inner)
            return depth < 3 && pkg.Scripts.TryGetValue(inner, out var next) ? ScriptFile(root, pkg, next, depth + 1) : null;
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.StartsWith('-'))
            {
                if (!word.Contains('=') && ValueOptions.Contains(word)) i++;
                continue;
            }
            // tsx watch x, bun run x, pm2 start x
            if (i == 1 && word is "watch" or "run" or "start" or "serve") continue;
            var path = Clean(word);
            if (path == ".") return pkg.Main is { Length: > 0 } main ? Clean(main) : "index.js";
            if (path.Length == 0 || path.StartsWith("ecosystem.config", StringComparison.Ordinal)) continue;
            if (ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(root, path)) || IsBuildOutput(root, path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Whether a script runs a development server or watcher (nodemon, ts-node-dev, tsx watch, node --watch, bun --hot,
    /// nest start --watch, next dev, vite, ng serve and the like), itself or through the script it runs.
    /// </summary>
    internal static bool IsDevCommand(PackageJson pkg, string command, int depth = 0)
    {
        var words = LastCommand(command);
        if (words.Count == 0) return false;
        if (ScriptCalled(pkg, words) is { } inner)
            return depth < 3 && pkg.Scripts.TryGetValue(inner, out var next) && IsDevCommand(pkg, next, depth + 1);
        var rest = words.Skip(1).ToList();
        var sub = rest.FirstOrDefault(w => !w.StartsWith('-'));
        return words[0] switch
        {
            "nodemon" or "ts-node-dev" or "tsnd" or "webpack-dev-server" or "live-server" => true,
            "node" when sub == "ace" => rest.Contains("serve"),
            "node" or "bun" or "deno" => rest.Any(w => w == "--hot" || w.StartsWith("--watch", StringComparison.Ordinal)),
            "tsx" or "tsc" => sub == "watch" || rest.Contains("--watch") || rest.Contains("-w"),
            "vite" => sub is null or "dev" or "serve",
            "next" or "nuxt" or "nuxi" or "astro" or "remix" or "react-router" or "svelte-kit" or "wrangler" => sub == "dev",
            "react-scripts" => sub == "start",
            "ng" or "vue-cli-service" or "webpack" => sub == "serve",
            "strapi" => sub is "develop" or "dev",
            "nest" => sub == "start" && rest.Any(w => w is "--watch" or "-w" or "--debug" or "-d"),
            _ => false,
        };
    }

    /// <summary>The words of a script's last command, the one that keeps running: without env assignments, cross-env, or what it pipes into.</summary>
    internal static List<string> LastCommand(string command)
    {
        var last = CommandSeparators().Split(command).Select(c => c.Trim())
            .LastOrDefault(c => c.Length > 0 && !c.StartsWith("exit", StringComparison.Ordinal) && !c.StartsWith("echo", StringComparison.Ordinal)) ?? "";
        var pipe = last.IndexOf('|');
        if (pipe >= 0) last = last[..pipe];
        var words = last.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\'', '(', ')')).Where(w => w.Length > 0).ToList();
        while (words.Count > 0 && (words[0] is "cross-env" or "cross-env-shell" or "env" || EnvAssignment().IsMatch(words[0]))) words.RemoveAt(0);
        // dotenv -e .env -- node server.js
        if (words.Count > 0 && words[0] is "dotenv" or "dotenvx" or "env-cmd" && words.IndexOf("--") is var dash and >= 0) words.RemoveRange(0, dash + 1);
        return words;
    }

    /// <summary>The package.json script a command runs (`npm run x`, `npm start`, `pnpm x`, `yarn x`, `bun run x`), or null.</summary>
    private static string? ScriptCalled(PackageJson pkg, List<string> words)
    {
        if (words.Count < 2 || words[0] is not ("npm" or "pnpm" or "yarn" or "bun")) return null;
        var args = words.Skip(1).Where(w => !w.StartsWith('-')).ToList();
        if (args.Count == 0) return null;
        var name = args[0] is "run" or "run-script" ? args.ElementAtOrDefault(1) : args[0];
        return name is not null && pkg.Scripts.ContainsKey(name) ? name : null;
    }

    /// <summary>Whether a script's last command is bun or bunx (`bun run x` too: bun runs the script), or runs a script whose is.</summary>
    private static bool RunsBun(PackageJson pkg, string command, int depth = 0)
    {
        var words = LastCommand(command);
        if (words.Count == 0) return false;
        if (words[0] is "bun" or "bunx") return true;
        return depth < 3 && ScriptCalled(pkg, words) is { } called && pkg.Scripts.TryGetValue(called, out var next) && RunsBun(pkg, next, depth + 1);
    }

    /// <summary>
    /// Why the app needs Bun itself rather than Node, or null when Node runs it. A start script decides: it runs bun or
    /// it doesn't. Without one, Bun runs the app when its dev script does, when it uses Elysia (without its Node adapter),
    /// Bun's types or Bun's APIs, or when it installs with bun. Front ends never get here: they build on Node, and one
    /// that installs with bun gets bun's binary in the Node image.
    /// </summary>
    internal static string? BunReason(string root, PackageJson pkg, PackageManager pm)
    {
        if (ProductionScript(pkg) is { } script) return RunsBun(pkg, script.Command) ? $"its {script.Name} script runs bun" : null;
        foreach (var name in new[] { "start", "dev" })
        {
            if (pkg.Scripts.TryGetValue(name, out var command) && RunsBun(pkg, command)) return $"its {name} script runs bun";
        }
        if (pkg.Has("elysia") && !pkg.Has("@elysiajs/node")) return "Elysia runs on Bun";
        if (pkg.Has("@types/bun") || pkg.Has("bun-types")) return "it's written for Bun (it has Bun's types)";
        var main = string.IsNullOrWhiteSpace(pkg.Main) ? null : Clean(pkg.Main);
        foreach (var file in new[] { main }.Concat(JsEntries).Concat(TsEntries).OfType<string>().Where(f => File.Exists(Path.Combine(root, f))).Take(4))
        {
            if (Code(root, file) is { } text && BunApi().IsMatch(text)) return $"{file} uses Bun's APIs";
        }
        return pm.IsBun ? "it installs with bun and has no start script" : null;
    }

    /// <summary>
    /// Whether a package.json without a start script or server dependency still runs a server, because its entry file
    /// listens (`createServer(...).listen()`, `Bun.serve()`). Libraries don't, so they aren't offered as apps.
    /// </summary>
    internal static bool ServesFromEntry(string folder, PackageJson pkg)
    {
        var build = new NodeBuild(PackageManager.Detect(folder, pkg), "", [], pkg.Scripts.ContainsKey("build") ? "build" : null, false);
        return FindServerEntry(folder, pkg, build, out _, out _) is { } entry && SourceFor(folder, entry) is { } source
            && Code(folder, source) is { } text && Listens().IsMatch(text);
    }

    /// <summary>A file's code without its comments, or null when it can't be read.</summary>
    private static string? Code(string root, string file) => ReadFirst(root, file) is { } text ? TypeStripping.WithoutComments(text) : null;

    /// <summary>
    /// The file whose code shows where an entry listens: the entry itself, or for a build output (dist/main.js, lib/) the
    /// source it's compiled from (src/main.ts, src/index.ts). Null when there's none.
    /// </summary>
    internal static string? SourceFor(string root, string entry)
    {
        if (File.Exists(Path.Combine(root, entry))) return entry;
        var stem = entry.TrimEnd('/');
        var extension = Path.GetExtension(stem);
        if (extension is ".js" or ".mjs" or ".cjs") stem = stem[..^extension.Length];
        var config = TsConfig.Load(root);
        foreach (var output in new[] { config?.OutDir }.OfType<string>().Concat(OutputFolders))
        {
            if (stem == output) stem = "";
            else if (stem.StartsWith(output + "/", StringComparison.Ordinal)) stem = stem[(output.Length + 1)..];
            else continue;
            break;
        }
        var stems = stem.Length == 0 ? new[] { "index" } : [stem, stem + "/index"];
        var folders = new[] { config?.RootDir is { Length: > 0 } rootDir ? rootDir + "/" : null, "src/", "" }.OfType<string>().Distinct();
        foreach (var name in stems)
            foreach (var folder in folders)
                foreach (var ext in new[] { ".ts", ".mts", ".cts", ".js", ".mjs", ".cjs" })
                    if (File.Exists(Path.Combine(root, folder + name + ext))) return folder + name + ext;
        return null;
    }

    /// <summary>Whether a path is in a folder a build writes (dist/, build/, lib/, the tsconfig's outDir).</summary>
    private static bool IsBuildOutput(string root, string path)
    {
        if (OutputFolders.Contains(path.Split('/')[0], StringComparer.OrdinalIgnoreCase)) return true;
        return TsConfig.Load(root)?.OutDir is { Length: > 0 } outDir && (path == outDir || path.StartsWith(outDir + "/", StringComparison.Ordinal));
    }

    /// <summary>"./src/index.ts" → "src/index.ts".</summary>
    private static string Clean(string path)
    {
        var clean = path.Trim().Replace('\\', '/');
        while (clean.StartsWith("./", StringComparison.Ordinal)) clean = clean[2..];
        return clean.Length > 1 ? clean.TrimEnd('/') : clean;
    }

    /// <summary>The files to read for the port and the host: the entry's source, the local files it imports, then the usual places.</summary>
    private static List<string> ServerFiles(string root, string? source)
    {
        var files = new List<string>();
        void Add(string? file)
        {
            if (file is not null && !files.Contains(file) && File.Exists(Path.Combine(root, file))) files.Add(file);
        }
        Add(source);
        if (source is not null && Code(root, source) is { } text)
        {
            foreach (var spec in TypeStripping.Imports(text).Where(s => s.Spec.StartsWith('.')).Take(8))
                Add(TypeStripping.Locate(root, source, spec.Spec));
        }
        foreach (var file in PortFiles) Add(file);
        return files;
    }

    /// <summary>
    /// The port the app listens on, and where that was seen: PORT= or --port in its start script, Strapi's config/server,
    /// Feathers' config/default.json, then the code (see <see cref="PortInCode"/>). Null when nothing says.
    /// </summary>
    internal static (int Port, string From)? ServerPort(string root, string? script, IReadOnlyList<string> files)
    {
        if (script is not null && PortInScript().Match(script) is { Success: true } scripted && Port(scripted) is int fromScript)
            return (fromScript, "the start script");
        foreach (var config in new[] { "config/server.ts", "config/server.js" })
        {
            if (ReadFirst(root, config) is { } strapi && EnvHelperPort().Match(strapi) is { Success: true } m && Port(m) is int fromConfig)
                return (fromConfig, config);
        }
        if (ReadFirst(root, "config/default.json") is { } feathers && JsonPort().Match(feathers) is { Success: true } j && Port(j) is int fromJson)
            return (fromJson, "config/default.json");
        foreach (var file in files)
        {
            if (ReadFirst(root, file) is { } text && PortInCode(text) is int port) return (port, file);
        }
        return null;
    }

    /// <summary>
    /// The port some server code listens on: the default it gives PORT (`process.env.PORT || 4000`, `Number(process.env.PORT)
    /// ?? 4000`, `const { PORT = 4000 } = process.env`, zod's `.default(4000)`, `env.int('PORT', 4000)`), a port in the
    /// options of listen, serve, a hapi server or a default export (`listen({ port: 4000 })`, `serve({ fetch, port })`),
    /// `listen(4000)`, or `listen(PORT)` with `const PORT = 4000`. Null when it doesn't say.
    /// </summary>
    internal static int? PortInCode(string code)
    {
        var text = TypeStripping.WithoutComments(code);
        foreach (var pattern in new[] { EnvPortFallback(), EnvPortTernary(), EnvPortDestructured(), ZodPort(), EnvalidPort(), EnvHelperPort() })
        {
            if (pattern.Match(text) is { Success: true } m && Port(m) is int port) return port;
        }
        foreach (var (_, args) in Calls(text, PortCall()))
        {
            if (args.TrimStart().StartsWith('{') && PortOption(text, args) is int option) return option;
        }
        foreach (Match m in DefaultExportObject().Matches(text))
        {
            if (Balanced(text, m.Index + m.Length - 1) is { } body && PortOption(text, body) is int exported) return exported;
        }
        if (ListenLiteral().Match(text) is { Success: true } literal && Port(literal) is int listened) return listened;
        foreach (Match m in ListenIdentifier().Matches(text))
        {
            if (Constant(text, m.Groups["id"].Value) is int constant) return constant;
        }
        return null;
    }

    /// <summary>The port in an options object: `port: 4000`, or `port: PORT` / `port` with a constant behind it.</summary>
    private static int? PortOption(string text, string options)
    {
        if (OptionPort().Match(options) is { Success: true } literal) return Port(literal);
        if (OptionPortName().Match(options) is { Success: true } named) return Constant(text, named.Groups["id"].Value);
        return OptionPortShorthand().IsMatch(options) ? Constant(text, "port") : null;
    }

    /// <summary>The number a constant is set to (`const PORT = 4000`, `let port: number = 4000`), or null.</summary>
    private static int? Constant(string text, string name)
    {
        var m = Regex.Match(text, $@"\b(?:const|let|var)\s+{Regex.Escape(name)}\s*(?::\s*\w+\s*)?=\s*['""`]?(?<port>\d{{2,5}})\b");
        return m.Success ? Port(m) : null;
    }

    private static int? Port(Match m) =>
        int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : null;

    /// <summary>
    /// Warns about code that keeps the server out of reach from outside its container (a localhost-only host, Fastify's
    /// default binding), or unaware that it sits behind Cloudflare Tunnel's proxy (sessions without the trust switch).
    /// </summary>
    private static void ListenWarnings(DetectionResult r, string root, PackageJson pkg, IReadOnlyList<string> files, string? script)
    {
        var sources = files.Select(f => (File: f, Text: Code(root, f) ?? "")).ToList();
        foreach (var (file, text) in sources)
        {
            if (!Calls(text, BindCall()).Any(c => ListensOnLocalhost(text, c.Args))) continue;
            r.Warnings.Add($"{file} listens on localhost only, so nothing outside the container can reach it. Listen on 0.0.0.0, or on process.env.HOST, which Gitberth sets to 0.0.0.0.");
            break;
        }
        if (pkg.Has("fastify") && !(script is not null && LastCommand(script) is ["fastify", "start", ..]))
        {
            foreach (var (file, text) in sources.Where(s => ImportsFastify().IsMatch(s.Text)))
            {
                if (!Calls(text, ListenCall()).Any(c => !BindsAllInterfaces(c.Args))) continue;
                r.Warnings.Add($"{file}: Fastify listens on localhost unless listen() gets a host, so nothing outside the container can reach it. Add host: '0.0.0.0' to the listen options (or host: process.env.HOST, which Gitberth sets to 0.0.0.0).");
                break;
            }
        }
        if (pkg.Has("@nestjs/platform-fastify"))
        {
            foreach (var (file, text) in sources.Where(s => s.Text.Contains("NestFactory", StringComparison.Ordinal)))
            {
                if (!Calls(text, ListenCall()).Any(c => TopLevelArgs(c.Args).Count == 1 && !BindsAllInterfaces(c.Args))) continue;
                r.Warnings.Add($"{file}: NestJS on Fastify listens on localhost unless listen() gets a host, so nothing outside the container can reach it. Pass '0.0.0.0' as listen()'s second argument.");
                break;
            }
        }

        var code = string.Join('\n', sources.Select(s => s.Text));
        if ((pkg.Has("express") || pkg.Has("@nestjs/platform-express")) && ExpressSessions.Any(pkg.Has) && !code.Contains("trust proxy", StringComparison.Ordinal))
            r.Warnings.Add("Behind Cloudflare Tunnel, Express sees every request as plain http from the tunnel unless it trusts the proxy, so secure session cookies are never sent and req.ip is the tunnel's. Add app.set('trust proxy', 1).");
        if (pkg.Has("fastify") && FastifySessions.Any(pkg.Has) && !code.Contains("trustProxy", StringComparison.Ordinal))
            r.Warnings.Add("Behind Cloudflare Tunnel, Fastify sees every request as plain http from the tunnel unless it trusts the proxy, so secure session cookies are never sent. Pass trustProxy: true to fastify().");
        if (pkg.Has("koa") && KoaSessions.Any(pkg.Has) && !KoaProxy().IsMatch(code))
            r.Warnings.Add("Behind Cloudflare Tunnel, Koa sees every request as plain http from the tunnel unless it trusts the proxy, so secure session cookies are never sent. Set app.proxy = true.");
    }

    /// <summary>Whether a listen or serve call's arguments pin a localhost host: `host: 'localhost'`, `listen(port, '127.0.0.1')`, or a constant set to one.</summary>
    private static bool ListensOnLocalhost(string text, string args)
    {
        if (LocalhostOption().IsMatch(args)) return true;
        var positional = TopLevelArgs(args);
        if (positional.Count < 2) return false;
        var host = positional[1].Trim();
        if (LocalhostLiteral().IsMatch(host)) return true;
        return Regex.IsMatch(host, @"^[A-Za-z_$][\w$]*$")
            && Regex.IsMatch(text, $@"\b(?:const|let|var)\s+{Regex.Escape(host)}\s*(?::\s*\w+\s*)?=\s*['""`](?:localhost|127\.0\.0\.1|::1)['""`]");
    }

    /// <summary>Whether Fastify's listen() arguments give it a host: a host option, or an all-interfaces address.</summary>
    private static bool BindsAllInterfaces(string args) =>
        HostWord().IsMatch(args) || args.Contains("0.0.0.0", StringComparison.Ordinal) || AllInterfacesV6().IsMatch(args);

    /// <summary>
    /// What Strapi needs to start in a container: the secrets its config reads with no default, and somewhere to keep its
    /// SQLite database, which otherwise lives inside the container and starts empty after each deploy.
    /// </summary>
    private static void StrapiWarnings(DetectionResult r, string root, NodeBuild b)
    {
        var required = new List<string>();
        foreach (var config in new[] { "server", "admin", "plugins", "api" })
        {
            var text = ReadFirst(root, $"config/{config}.ts", $"config/{config}.js") ?? "";
            foreach (Match m in StrapiRequiredEnv().Matches(text))
            {
                var key = m.Groups[1].Value;
                if (key is not ("HOST" or "PORT") && !required.Contains(key)) required.Add(key);
            }
        }
        if (required.Count > 0)
            r.Warnings.Add($"Strapi won't start without {Sentence(required)}, which its config reads with no default. Set them on the Environment tab.");
        var database = ReadFirst(root, "config/database.ts", "config/database.js") ?? "";
        if (StrapiSqlite().IsMatch(database))
            r.Warnings.Add($"Strapi keeps its SQLite database in .tmp/data.db inside the container, so it starts empty after each deploy. Add a volume at {b.WorkDir}/.tmp (and one at {b.WorkDir}/public/uploads for uploaded files), or set DATABASE_CLIENT=postgres and DATABASE_URL.");
    }

    private static string Sentence(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>Calls whose name matches <paramref name="name"/> (ending in "("), with the text of their arguments.</summary>
    private static IEnumerable<(int Index, string Args)> Calls(string text, Regex name)
    {
        foreach (Match m in name.Matches(text))
        {
            if (Balanced(text, m.Index + m.Length - 1) is { } args) yield return (m.Index, args);
        }
    }

    /// <summary>What's between the bracket at <paramref name="open"/> and the one that closes it, skipping strings and comments; null when it doesn't close nearby.</summary>
    private static string? Balanced(string text, int open)
    {
        if (text[open] is not ('(' or '{' or '[')) return null;
        var depth = 0;
        char? quote = null;
        for (var i = open; i < text.Length && i < open + 4000; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == '\\') i++;
                else if (c == q) quote = null;
                continue;
            }
            if (SkipComment(text, ref i)) continue;
            if (c is '"' or '\'' or '`') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']' && --depth == 0) return text[(open + 1)..i];
        }
        return null;
    }

    /// <summary>The arguments of a call, split at its top-level commas.</summary>
    private static List<string> TopLevelArgs(string args)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        char? quote = null;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (quote is { } q)
            {
                if (c == '\\') i++;
                else if (c == q) quote = null;
                continue;
            }
            if (SkipComment(args, ref i)) continue;
            if (c is '"' or '\'' or '`') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(args[start..i]);
                start = i + 1;
            }
        }
        if (args[start..].Trim().Length > 0) parts.Add(args[start..]);
        return parts;
    }

    /// <summary>Moves past a // or /* */ comment that starts at <paramref name="i"/>, to its last character.</summary>
    private static bool SkipComment(string text, ref int i)
    {
        if (text[i] != '/' || i + 1 >= text.Length || text[i + 1] is not ('/' or '*')) return false;
        var end = text[i + 1] == '/' ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        i = end < 0 ? text.Length - 1 : text[i + 1] == '/' ? end : end + 1;
        return true;
    }

    [GeneratedRegex(@"\s*(?:&&|\|\||;)\s*")]
    private static partial Regex CommandSeparators();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvAssignment();

    [GeneratedRegex(@"""entryFile""\s*:\s*""([^""]+)""")]
    private static partial Regex NestEntryFile();

    [GeneratedRegex(@"""sourceRoot""\s*:\s*""([^""]+)""")]
    private static partial Regex NestSourceRoot();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"\.(?:spec|test)\.[cm]?tsx?$", RegexOptions.IgnoreCase)]
    private static partial Regex TestFile();

    [GeneratedRegex(@"(?:--port[ =]|-p\s+|\bPORT=)(?<port>\d{2,5})\b")]
    private static partial Regex PortInScript();

    // process.env.PORT || 4000, Number(process.env.PORT) || 4000, parseInt(process.env.PORT, 10) || 4000, Bun.env.PORT ?? 4000
    [GeneratedRegex(@"\benv(?:\.PORT\b|\[\s*['""`]PORT['""`]\s*\])(?:\s*,\s*\d+)?\s*\)?\s*(?:\|\||\?\?)\s*['""`]?(?<port>\d{2,5})\b")]
    private static partial Regex EnvPortFallback();

    // process.env.PORT ? Number(process.env.PORT) : 4000
    [GeneratedRegex(@"\benv\.PORT\b\s*\?[^:;\n]{1,80}:\s*['""`]?(?<port>\d{2,5})\b")]
    private static partial Regex EnvPortTernary();

    // const { PORT = 4000 } = process.env
    [GeneratedRegex(@"\{[^{}]*?\bPORT\s*=\s*['""`]?(?<port>\d{2,5})['""`]?[^{}]*\}\s*=\s*(?:process\.|Bun\.)?env\b")]
    private static partial Regex EnvPortDestructured();

    // PORT: z.coerce.number().default(4000)
    [GeneratedRegex(@"\bPORT\s*:\s*z\.[^,\n]*?\.default\(\s*['""`]?(?<port>\d{2,5})")]
    private static partial Regex ZodPort();

    // PORT: port({ default: 4000 })
    [GeneratedRegex(@"\bPORT\s*:\s*(?:port|num|str)\(\s*\{[^}]*?\bdefault\s*:\s*['""`]?(?<port>\d{2,5})")]
    private static partial Regex EnvalidPort();

    // env.int('PORT', 1337) in Strapi's config/server, env('PORT', 4000)
    [GeneratedRegex(@"\benv(?:\.int)?\(\s*['""`]PORT['""`]\s*,\s*['""`]?(?<port>\d{2,5})")]
    private static partial Regex EnvHelperPort();

    [GeneratedRegex(@"""port""\s*:\s*(?<port>\d{2,5})\b")]
    private static partial Regex JsonPort();

    [GeneratedRegex(@"(?:\.listen|\bserve|\b[Ss]erver|\bcreateServer)\s*\(")]
    private static partial Regex PortCall();

    [GeneratedRegex(@"\bexport\s+default\s*\{")]
    private static partial Regex DefaultExportObject();

    [GeneratedRegex(@"\bport\s*:\s*['""`]?(?<port>\d{2,5})\b")]
    private static partial Regex OptionPort();

    [GeneratedRegex(@"\bport\s*:\s*(?<id>[A-Za-z_$][\w$]*)\s*[,}\n]")]
    private static partial Regex OptionPortName();

    [GeneratedRegex(@"(?:^|[{,\s])port\s*(?:[,}]|$)")]
    private static partial Regex OptionPortShorthand();

    [GeneratedRegex(@"\.listen\(\s*['""`]?(?<port>\d{2,5})\b")]
    private static partial Regex ListenLiteral();

    [GeneratedRegex(@"\.listen\(\s*(?<id>[A-Za-z_$][\w$]*)\s*[,)]")]
    private static partial Regex ListenIdentifier();

    [GeneratedRegex(@"\.listen\s*\(")]
    private static partial Regex ListenCall();

    [GeneratedRegex(@"(?:\.listen|\bserve|\b[Ss]erver|\bcreateServer)\s*\(")]
    private static partial Regex BindCall();

    [GeneratedRegex(@"\b(?:host|hostname)\s*:\s*['""`](?:localhost|127\.0\.0\.1|::1)['""`]")]
    private static partial Regex LocalhostOption();

    [GeneratedRegex(@"^['""`](?:localhost|127\.0\.0\.1|::1)['""`]$")]
    private static partial Regex LocalhostLiteral();

    [GeneratedRegex(@"\bhost(?:name)?\b")]
    private static partial Regex HostWord();

    [GeneratedRegex(@"['""`]::['""`]")]
    private static partial Regex AllInterfacesV6();

    [GeneratedRegex(@"(?:\brequire\(\s*['""]fastify['""]\s*\)|\bfrom\s+['""]fastify['""])")]
    private static partial Regex ImportsFastify();

    [GeneratedRegex(@"\bproxy\s*(?:=|:)\s*true\b")]
    private static partial Regex KoaProxy();

    [GeneratedRegex(@"\benv(?:\.(?:array|json))?\(\s*['""]([A-Z][A-Z0-9_]*)['""]\s*\)")]
    private static partial Regex StrapiRequiredEnv();

    [GeneratedRegex(@"(?:\bclient\s*:\s*['""]sqlite['""]|['""]DATABASE_CLIENT['""]\s*,\s*['""]sqlite['""])")]
    private static partial Regex StrapiSqlite();

    [GeneratedRegex(@"\bBun\.(?:serve|file|write|spawn|env|sql|s3|password|sleep)\b|\bfrom\s+['""]bun(?::[\w-]+)?['""]|\brequire\(\s*['""]bun:")]
    private static partial Regex BunApi();

    [GeneratedRegex(@"\.listen\s*\(|\bcreateServer\s*\(|\bBun\.serve\s*\(|\bDeno\.serve\s*\(|\bserve\s*\(\s*\{|\bexport\s+default\s*\{[^}]*\bfetch\b")]
    private static partial Regex Listens();
}
