using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>What Gitberth found to run in a Python project, and how it starts.</summary>
internal sealed class PythonApp
{
    public string Title { get; set; } = "Python";

    /// <summary>A web framework, a Procfile web line, manage.py or a data-app framework said so.</summary>
    public bool Strong { get; set; }

    /// <summary>The port it listens on; null for a worker.</summary>
    public int? Port { get; set; }

    /// <summary>The command that runs it, as a shell runs it (with ${PORT:-8000} and the like).</summary>
    public string Command { get; set; } = "";

    /// <summary>Commands that run before it, at every start: migrations, a Procfile's release command.</summary>
    public List<string> BeforeStart { get; } = [];

    /// <summary>Collects Django's static files at start, for when the build couldn't (settings that need run-time variables).</summary>
    public string? CollectStaticAtStart { get; set; }

    /// <summary>RUN lines after the dependencies are installed.</summary>
    public List<string> BuildSteps { get; } = [];

    /// <summary>ENV lines for the image, for the build and the app.</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];

    /// <summary>Comments above the ENV line, for settings whose purpose isn't obvious.</summary>
    public List<string> EnvComments { get; } = [];

    /// <summary>The server the command needs, "gunicorn" or "uvicorn", which is installed when the app doesn't depend on it.</summary>
    public string? Server { get; set; }

    /// <summary>The code lives under src/, which goes on PYTHONPATH.</summary>
    public bool SrcLayout { get; set; }

    public List<string> Evidence { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>Finds what a Python project runs: Django, a web or data-app framework, a Procfile, or a plain script.</summary>
internal static partial class PythonApps
{
    public const int DefaultPort = 8000;

    private enum Runner { Gunicorn, Uvicorn, Sanic, Streamlit, Gradio, Script }

    /// <param name="Module">The import name, which is also the package name.</param>
    /// <param name="Constructor">The classes whose instance is the app, for finding `app = Flask(__name__)`.</param>
    /// <param name="ByDependency">The dependency alone says it's this framework; aiohttp and Tornado are often only libraries.</param>
    private sealed record Framework(string Name, string Module, Runner Runner, int Port, string? Constructor, bool ByDependency)
    {
        public Regex? Assignment { get; } = Constructor is null ? null : AssignmentOf(Constructor);
        public Regex Import { get; } = ImportOf(Module);
    }

    // In the order they win when a project has several: data apps before the web frameworks they're built on (Dash on
    // Flask, Gradio on FastAPI), FastAPI before Starlette, Quart before Flask.
    private static readonly Framework[] Frameworks =
    [
        new("Streamlit", "streamlit", Runner.Streamlit, 8501, null, true),
        new("Gradio", "gradio", Runner.Gradio, 7860, null, true),
        new("Dash", "dash", Runner.Gunicorn, 8050, "Dash", true),
        new("FastAPI", "fastapi", Runner.Uvicorn, DefaultPort, "FastAPI", true),
        new("Litestar", "litestar", Runner.Uvicorn, DefaultPort, "Litestar", true),
        new("Starlette", "starlette", Runner.Uvicorn, DefaultPort, "Starlette", true),
        new("Quart", "quart", Runner.Uvicorn, DefaultPort, "Quart", true),
        new("Sanic", "sanic", Runner.Sanic, DefaultPort, "Sanic", true),
        new("Falcon", "falcon", Runner.Gunicorn, DefaultPort, "App|API", true),
        new("Flask", "flask", Runner.Gunicorn, DefaultPort, "Flask", true),
        new("Bottle", "bottle", Runner.Gunicorn, DefaultPort, "Bottle|default_app", true),
        new("aiohttp", "aiohttp", Runner.Script, 8080, "Application", false),
        new("Tornado", "tornado", Runner.Script, 8888, null, false),
    ];

    // Files that are run, in the order they're looked for. main.py and app.py are often modules too, so they count
    // only with an `if __name__ == "__main__":` block.
    private static readonly string[] ScriptNames = ["main.py", "app.py", "server.py", "run.py", "start.py", "bot.py", "worker.py"];

    /// <summary>What to run, or null when nothing in the project runs (a library, or only a dependency file).</summary>
    public static PythonApp? Plan(PythonProject p)
    {
        var app = new PythonApp();
        var django = DjangoApp.Find(p);
        var framework = django is null ? FindFramework(p) : null;
        if (django is not null) app.Title = django.Asgi ? "Python (Django, ASGI)" : "Python (Django)";
        else if (framework is not null) app.Title = $"Python ({framework.Name})";

        if (p.Procfile?.Web is { } web) FromProcfile(p, app, web, django is not null ? DefaultPort : framework?.Port, framework is null && django is null);
        else if (django is not null) django.Plan(app);
        else if (framework is not null)
        {
            framework.Plan(app);
            // Vercel runs Python functions from api/ next to a front end, which stays the app; the API is offered too.
            if (framework.File?.StartsWith("api/", StringComparison.Ordinal) == true && p.Files.Exists("package.json")) app.Strong = false;
        }
        else if (p.Procfile?.Background is { } background)
        {
            app.Title = "Python worker (Procfile)";
            app.Command = background.Value;
            app.Evidence.Add($"Runs the Procfile's {background.Key} process, `{background.Value}`, with no HTTP port");
        }
        else if (!PlanScript(p, app)) return null;

        django?.AddBuildAndChecks(app);
        if (p.Procfile is { } procfile)
        {
            if (procfile.Release is { } release)
            {
                app.BeforeStart.Insert(0, release);
                app.Evidence.Add($"Runs the Procfile's release command, `{release}`, before it starts");
            }
            var others = procfile.Web is null ? [] : procfile.Processes.Keys.Where(k => k is not ("web" or "release")).ToList();
            if (others.Count > 0)
                app.Warnings.Add($"The Procfile also has {string.Join(", ", others)}, which Gitberth doesn't run. To run it too, add the repo again as another app and give it a custom Dockerfile with that command.");
        }
        return app;
    }

    // ---- Web and data-app frameworks ----

    /// <summary>Where a framework's app object is created in one file.</summary>
    /// <param name="Variable">A module-level `app = Flask(__name__)`.</param>
    /// <param name="Factory">A module-level `def create_app()`.</param>
    /// <param name="Runs">The file runs the server itself: run_app(), launch(), listen(), or a Streamlit script.</param>
    /// <param name="Server">For Dash, the Flask server gunicorn loads (`server = app.server`).</param>
    /// <param name="Asgi">For Falcon, a falcon.asgi.App.</param>
    private sealed record Hit(Framework Framework, string File, string? Variable = null, string? Factory = null, bool AsyncFactory = false,
        bool Runs = false, string? Server = null, bool Asgi = false);

    /// <summary>A framework, and where its app object is created (or the result of an app factory, `app = create_app()`).</summary>
    private sealed class FrameworkApp(PythonProject p, Framework framework, Hit? hit, (string File, string Variable)? result)
    {
        public string Name => framework.Name;
        public int Port => framework.Port;

        /// <summary>The file the app object is in, when it was found.</summary>
        public string? File => hit?.File ?? result?.File;

        public void Plan(PythonApp app)
        {
            app.Strong = true;
            app.Port = framework.Port;
            switch (framework.Runner)
            {
                case Runner.Streamlit:
                    var page = hit?.File ?? Fallback(app, "streamlit_app.py", "app.py", "main.py");
                    app.Command = $"streamlit run {ShellQuote(page)} --server.port=${{PORT:-{app.Port}}} --server.address=0.0.0.0 --server.headless=true";
                    app.Env.Add(new("STREAMLIT_BROWSER_GATHER_USAGE_STATS", "false"));
                    app.Evidence.Add($"Starts with `streamlit run {page}`");
                    return;
                case Runner.Gradio:
                    var demo = hit?.File ?? Fallback(app, "app.py", "main.py");
                    app.Port = LaunchPort(p.Text(demo)) ?? app.Port;
                    app.EnvComments.Add("# Gradio's launch() listens where GRADIO_SERVER_NAME and GRADIO_SERVER_PORT say, unless its arguments say otherwise.");
                    app.Env.Add(new("GRADIO_SERVER_NAME", "0.0.0.0"));
                    app.Env.Add(new("GRADIO_ANALYTICS_ENABLED", "False"));
                    app.Command = $"export GRADIO_SERVER_PORT=${{PORT:-{app.Port}}} && python {ShellQuote(demo)}";
                    app.Evidence.Add($"Starts with `python {demo}`, whose launch() listens on 0.0.0.0");
                    LocalhostWarning(app, demo, p.Text(demo));
                    return;
                case Runner.Script when hit is { Runs: true }:
                    var code = p.Text(hit.File);
                    app.Port = PortInCode(code) ?? framework.Port;
                    app.Command = $"python {ShellQuote(hit.File)}";
                    app.Evidence.Add($"Starts with `python {hit.File}`, which runs its own {framework.Name} server");
                    LocalhostWarning(app, hit.File, code);
                    if (framework.Name == "Tornado" && !XHeaders().IsMatch(code ?? ""))
                        app.Warnings.Add("Tornado only reads the visitor's IP and https from Cloudflare Tunnel's X-Forwarded-For and X-Forwarded-Proto headers with xheaders=True. Pass it to listen() or HTTPServer().");
                    return;
            }

            string spec;
            var factory = false;
            if (hit?.Variable is { } variable && ModuleOf(p, hit.File) is { } module)
            {
                app.SrcLayout = module.Src;
                if (framework.Name == "Dash")
                {
                    if (hit.Server is null)
                    {
                        DashDevServer(app, hit.File);
                        return;
                    }
                    // gunicorn serves a Dash app's Flask server, not the Dash object.
                    variable = hit.Server;
                }
                spec = $"{module.Name}:{variable}";
            }
            else if (result is { } r && ModuleOf(p, r.File) is { } resultModule)
            {
                spec = $"{resultModule.Name}:{r.Variable}";
                app.SrcLayout = resultModule.Src;
            }
            else if (hit?.Factory is { } function && ModuleOf(p, hit.File) is { } factoryModule)
            {
                spec = $"{factoryModule.Name}:{function}";
                factory = true;
                app.SrcLayout = factoryModule.Src;
            }
            else
            {
                var guess = p.Files.Exists("main.py") ? "main" : p.Files.Exists("app.py") || p.Files.Exists("app/__init__.py") ? "app"
                    : framework.Name == "FastAPI" ? "main" : "app";
                spec = $"{guess}:app";
                app.Warnings.Add($"Couldn't find where the {framework.Name} app is created, so `{spec}` is assumed. If that's wrong, add a Procfile with a web line, like `web: {Example(framework.Runner)}`, or customize the Dockerfile.");
            }

            var port = app.Port.Value;
            if (framework.Name == "aiohttp")
            {
                // An aiohttp app that doesn't call run_app() is served by gunicorn with aiohttp's own worker, which
                // awaits an async factory itself.
                var target = factory && !hit!.AsyncFactory ? ShellQuote($"{spec}()") : spec;
                app.Command = Gunicorn(p, target, port, workerClass: "aiohttp.GunicornWebWorker");
                app.Server = "gunicorn";
                app.Evidence.Add($"Starts with `gunicorn {spec}` and aiohttp's gunicorn worker");
            }
            else if (framework.Runner == Runner.Uvicorn || (framework.Name is "Falcon" or "Dash" && hit?.Asgi == true))
            {
                app.Command = Uvicorn(spec, port, factory);
                app.Server = "uvicorn";
                app.Evidence.Add($"Starts with `uvicorn {spec}{(factory ? " --factory" : "")}`, trusting Cloudflare Tunnel's X-Forwarded-* headers");
            }
            else if (framework.Runner == Runner.Sanic)
            {
                app.Command = $"sanic {spec}{(factory ? " --factory" : "")} --host=0.0.0.0 --port=${{PORT:-{port}}}";
                app.EnvComments.Add("# Sanic reads its settings from SANIC_ variables: with one proxy in front (Cloudflare Tunnel), it trusts X-Forwarded-For and X-Forwarded-Proto.");
                app.Env.Add(new("SANIC_PROXIES_COUNT", "1"));
                app.Evidence.Add($"Starts with `sanic {spec}`, Sanic's own server");
            }
            else
            {
                var target = factory ? $"{spec}()" : spec;
                app.Command = Gunicorn(p, ShellQuote(target), port);
                app.Server = "gunicorn";
                app.Evidence.Add($"Starts with `gunicorn {target}`, trusting Cloudflare Tunnel's X-Forwarded-* headers");
            }
        }

        // Without `server = app.server` there's nothing for gunicorn to load, so the app runs the way it does locally.
        private static void DashDevServer(PythonApp app, string file)
        {
            app.Command = $"python {ShellQuote(file)}";
            app.EnvComments.Add("# Dash's development server listens where HOST and PORT say.");
            app.Env.Add(new("HOST", "0.0.0.0"));
            app.Evidence.Add($"Starts with `python {file}`, on Dash's development server");
            app.Warnings.Add($"{file} has no `server = app.server`, so it runs on Dash's development server. Add that line and Gitberth serves it with gunicorn instead.");
        }

        private string Fallback(PythonApp app, params string[] names)
        {
            var file = names.FirstOrDefault(p.Files.Exists) ?? names[0];
            app.Warnings.Add($"Couldn't find the {framework.Name} app's main file, so {file} is assumed. If that's wrong, add a Procfile with a web line, like `web: {Example(framework.Runner)}`, or customize the Dockerfile.");
            return file;
        }
    }

    /// <summary>
    /// The framework whose app object the code creates (in conventional files first: main.py, app.py, wsgi.py…), or,
    /// when none is found, the first framework the dependencies list.
    /// </summary>
    private static FrameworkApp? FindFramework(PythonProject p)
    {
        var hits = new List<Hit>();
        (string File, string Variable)? result = null;
        foreach (var file in p.Sources)
        {
            if (p.Text(file) is not { } text) continue;
            if (result is null && FactoryResult().Match(text) is { Success: true } called) result = (file, called.Groups["var"].Value);
            foreach (var framework in Frameworks)
            {
                if (text.Contains(framework.Module, StringComparison.Ordinal) && framework.Import.IsMatch(text) && FileHit(framework, file, text) is { } hit) hits.Add(hit);
            }
        }
        foreach (var framework in Frameworks)
        {
            var mine = hits.Where(h => h.Framework == framework)
                .OrderBy(h => h.File.Count(c => c == '/')).ThenBy(h => NameRank(framework, h.File)).ThenBy(h => h.File, StringComparer.Ordinal).ToList();
            if (mine.Count == 0) continue;
            // A module-level app object beats an app factory's result, which beats the factory itself.
            var best = mine.FirstOrDefault(h => h.Runs) ?? mine.FirstOrDefault(h => h.Variable is not null) ?? mine[0];
            return new FrameworkApp(p, framework, best, best.Variable is null && !best.Runs ? result : null);
        }
        var listed = Frameworks.FirstOrDefault(f => f.ByDependency && p.Has(f.Module));
        return listed is null ? null : new FrameworkApp(p, listed, null, result);
    }

    private static Hit? FileHit(Framework framework, string file, string text)
    {
        switch (framework.Runner)
        {
            case Runner.Streamlit:
                // Pages of a multipage app import streamlit too, but they aren't what `streamlit run` starts.
                return file.Count(c => c == '/') <= 1 && !file.StartsWith("pages/", StringComparison.Ordinal) ? new Hit(framework, file, Runs: true) : null;
            case Runner.Gradio:
                return text.Contains(".launch(", StringComparison.Ordinal) ? new Hit(framework, file, Runs: true) : null;
        }
        if (framework.Name == "Tornado")
            return text.Contains(".listen(", StringComparison.Ordinal) || text.Contains("HTTPServer(", StringComparison.Ordinal) ? new Hit(framework, file, Runs: true) : null;
        if (framework.Name == "aiohttp" && RunApp().IsMatch(text)) return new Hit(framework, file, Runs: true);

        if (framework.Assignment?.Match(text) is { Success: true } assignment)
        {
            var variable = assignment.Groups["var"].Value;
            var prefix = assignment.Groups["prefix"].Value;
            return new Hit(framework, file, Variable: variable,
                Server: framework.Name == "Dash" ? DashServer(text, variable) : null,
                Asgi: framework.Name switch
                {
                    "Falcon" => prefix.Contains("asgi.", StringComparison.Ordinal) || (prefix.Length == 0 && FalconAsgiImport().IsMatch(text)),
                    // Dash 4 can run on FastAPI or Quart, whose app.server is ASGI.
                    "Dash" => DashAsgiBackend().IsMatch(text),
                    _ => false,
                });
        }
        if (Factory().Match(text) is { Success: true } factory)
            return new Hit(framework, file, Factory: factory.Groups["name"].Value, AsyncFactory: factory.Groups["async"].Success,
                Asgi: framework.Name == "Falcon" && FalconAsgiImport().IsMatch(text));
        return null;
    }

    // `server = app.server`, or the Flask app the Dash app was handed.
    private static string? DashServer(string text, string dashVariable)
    {
        var m = Regex.Match(text, $@"^(?<var>[A-Za-z_]\w*)[ \t]*=[ \t]*{Regex.Escape(dashVariable)}\.server\b", RegexOptions.Multiline);
        if (m.Success) return m.Groups["var"].Value;
        var flask = Frameworks.First(f => f.Name == "Flask").Assignment!.Match(text);
        return flask.Success ? flask.Groups["var"].Value : null;
    }

    // Conventional file names first: main.py for most frameworks, streamlit_app.py for Streamlit, app.py for Gradio.
    private static int NameRank(Framework framework, string file)
    {
        string[] order = framework.Runner switch
        {
            Runner.Streamlit => ["streamlit_app.py", "app.py", "main.py", "Home.py", "home.py", "dashboard.py"],
            Runner.Gradio => ["app.py", "main.py", "demo.py"],
            _ => ["main.py", "app.py", "server.py", "api.py", "wsgi.py", "asgi.py", "application.py", "__init__.py", "index.py", "run.py", "web.py"],
        };
        var index = Array.IndexOf(order, file[(file.LastIndexOf('/') + 1)..]);
        return index < 0 ? order.Length : index;
    }

    /// <summary>
    /// A file's module name. Code under src/ (without src/__init__.py, and not imported as "src.") is a src layout:
    /// src/ goes on PYTHONPATH and the module is named without it.
    /// </summary>
    private static (string Name, bool Src)? ModuleOf(PythonProject p, string file)
    {
        if (file.StartsWith("src/", StringComparison.Ordinal) && !p.Files.Exists("src/__init__.py") && !SrcImport().IsMatch(p.Text(file) ?? ""))
            return PythonFiles.ModuleName(file[4..]) is { } inSrc ? (inSrc, true) : null;
        return PythonFiles.ModuleName(file) is { } module ? (module, false) : null;
    }

    private static string Example(Runner runner) => runner switch
    {
        Runner.Uvicorn => "uvicorn <module>:<app> --host 0.0.0.0 --port $PORT",
        Runner.Sanic => "sanic <module>:<app> --host 0.0.0.0 --port $PORT",
        Runner.Streamlit => "streamlit run <file> --server.port $PORT",
        Runner.Gradio or Runner.Script => "python <file>",
        _ => "gunicorn <module>:<app> --bind 0.0.0.0:$PORT",
    };

    // ---- Commands ----

    /// <summary>
    /// gunicorn on $PORT, with WEB_CONCURRENCY workers (2 when it isn't set, which suits a small home server) and the
    /// X-Forwarded-* headers from Cloudflare Tunnel trusted. A gunicorn.conf.py, which gunicorn reads by itself, keeps
    /// its say over the workers and the access log.
    /// </summary>
    internal static string Gunicorn(PythonProject p, string target, int port, string? chdir = null, string? workerClass = null)
    {
        var config = p.Files.Exists("gunicorn.conf.py") || (chdir is not null && p.Files.Exists($"{chdir}/gunicorn.conf.py"));
        var parts = new List<string> { "gunicorn", target };
        if (chdir is not null) parts.Add($"--chdir {ShellQuote(chdir)}");
        if (workerClass is not null) parts.Add($"--worker-class {workerClass}");
        parts.Add($"--bind 0.0.0.0:${{PORT:-{port}}}");
        if (!config) parts.Add("--workers ${WEB_CONCURRENCY:-2}");
        parts.Add("--forwarded-allow-ips='*'");
        if (!config) parts.Add("--access-logfile -");
        return string.Join(' ', parts);
    }

    internal static string Uvicorn(string spec, int port, bool factory, string? appDir = null) =>
        $"uvicorn {spec}{(factory ? " --factory" : "")}{(appDir is null ? "" : " --app-dir " + ShellQuote(appDir))} --host 0.0.0.0 --port ${{PORT:-{port}}} --proxy-headers --forwarded-allow-ips='*'";

    /// <summary>
    /// The Procfile's web command, run as Heroku and Render run it. Its port comes from the command (`--bind :5000`,
    /// `--port 8501`), from the file it runs, or else the framework's; the app gets it as PORT, which `$PORT` reads.
    /// </summary>
    private static void FromProcfile(PythonProject p, PythonApp app, string web, int? frameworkPort, bool noFramework)
    {
        app.Strong = true;
        app.Command = web;
        if (noFramework) app.Title = "Python (Procfile)";
        var script = ProcfileScript().Match(web);
        var literal = PortInCommand(web);
        var usesPort = PortVariable().IsMatch(web);
        app.Port = literal ?? (script.Success && !usesPort ? PortInCode(p.Text(script.Groups[1].Value)) : null) ?? frameworkPort ?? DefaultPort;
        app.Evidence.Add($"Starts with the Procfile's web command, `{web}`");
        app.EnvComments.Add("# gunicorn and uvicorn read FORWARDED_ALLOW_IPS, so the Procfile's command trusts Cloudflare Tunnel's X-Forwarded-* headers too.");
        app.Env.Add(new("FORWARDED_ALLOW_IPS", "*"));
        var first = web.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (first is "gunicorn" or "uvicorn") app.Server = first;
        if (first == "uvicorn" && !web.Contains("--host", StringComparison.Ordinal))
        {
            app.EnvComments.Add("# uvicorn reads UVICORN_HOST when the command doesn't pass --host.");
            app.Env.Add(new("UVICORN_HOST", "0.0.0.0"));
        }
        if (DevServer().IsMatch(web) && !web.Contains("0.0.0.0", StringComparison.Ordinal))
            app.Warnings.Add("The Procfile's web command runs a development server on 127.0.0.1, which nothing outside the container reaches. Use gunicorn, or have it listen on 0.0.0.0.");
        else if (literal is null && !usesPort && first is not ("gunicorn" or "uvicorn" or "streamlit") && !script.Success)
            app.Warnings.Add($"The Procfile's web command doesn't say which port it listens on, so port {app.Port} is assumed. Use $PORT in it.");
    }

    // ---- Plain scripts ----

    /// <summary>
    /// A main.py or app.py with `if __name__ == "__main__":`, a run-me file like bot.py or worker.py, or a package with
    /// __main__.py (python -m). It's a worker unless its code clearly serves HTTP.
    /// </summary>
    private static bool PlanScript(PythonProject p, PythonApp app)
    {
        string? file = ScriptNames.FirstOrDefault(name => p.Text(name) is { } text && (name is not ("main.py" or "app.py") || MainGuard().IsMatch(text)));
        string? module = null;
        if (file is null && p.Files.Exists("__main__.py")) file = "__main__.py";
        // A package's __main__.py is usually a library's command line, which isn't something to keep running.
        if (file is null && !p.IsPackage
            && p.Sources.FirstOrDefault(f => f.EndsWith("/__main__.py", StringComparison.Ordinal)
                && (f.Count(c => c == '/') == 1 || (f.Count(c => c == '/') == 2 && f.StartsWith("src/", StringComparison.Ordinal)))) is { } main
            && ModuleOf(p, main) is { } package)
        {
            file = main;
            module = package.Name;
            app.SrcLayout = package.Src;
        }
        if (file is null) return false;

        var code = p.Text(file) ?? "";
        app.Command = module is null ? $"python {ShellQuote(file)}" : $"python -m {module}";
        var shown = module is null ? file : module;
        if (ServesHttp().IsMatch(code))
        {
            app.Title = $"Python ({shown})";
            app.Port = PortInCode(code) ?? DefaultPort;
            app.Evidence.Add($"Starts with `{app.Command}`, which serves HTTP itself");
            LocalhostWarning(app, file, code);
        }
        else
        {
            app.Title = $"Python worker ({shown})";
            app.Evidence.Add($"Runs `{app.Command}` with no HTTP port: no web framework or server was found");
        }
        return true;
    }

    // ---- Ports ----

    /// <summary>
    /// The port Python code listens on: PORT's default (os.environ.get("PORT", 8080)), a PORT = 8000 constant, a port=
    /// argument, listen(8888), or an address like HTTPServer(("", 8000), …).
    /// </summary>
    internal static int? PortInCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var pattern in new[] { PortDefault(), PortConstant(), PortKeyword(), ListenPort(), AddressPort() })
        {
            var m = pattern.Match(code);
            if (m.Success && int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536) return port;
        }
        return null;
    }

    // The port Gradio's launch() is given (server_port=7861).
    private static int? LaunchPort(string? code) =>
        code is not null && ServerPort().Match(code) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>A port written into a command: --bind 0.0.0.0:5000, -b :5000, --port 8501, --server.port=8501.</summary>
    internal static int? PortInCommand(string command) =>
        CommandPort().Match(command) is { Success: true } m && int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;

    private static void LocalhostWarning(PythonApp app, string file, string? code)
    {
        if (code is not null && Localhost().Match(code) is { Success: true } m)
            app.Warnings.Add($"{file} listens on {m.Groups[1].Value} only, which nothing outside the container reaches. Listen on 0.0.0.0 instead.");
    }

    /// <summary>A word as sh reads it: as-is when it's plain, else in single quotes.</summary>
    internal static string ShellQuote(string s) => SafeWord().IsMatch(s) ? s : "'" + s.Replace("'", "'\\''") + "'";

    private static Regex AssignmentOf(string constructors) => new(
        $@"^(?<var>[A-Za-z_]\w*)[ \t]*(?::[^=\n]*)?=[ \t]*(?<prefix>(?:[A-Za-z_]\w*\.)*)(?:{constructors})[ \t]*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static Regex ImportOf(string module) => new(
        $@"^[ \t]*(?:from[ \t]+{Regex.Escape(module)}(?:\.\w+)*[ \t]+import|import[ \t]+(?:[\w.]+(?:[ \t]+as[ \t]+\w+)?[ \t]*,[ \t]*)*{Regex.Escape(module)}(?:\.\w+)*)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private const string FactoryNames = "create_app|make_app|get_app|build_app|app_factory|init_app|create_application|get_application";

    [GeneratedRegex(@"^(?<async>async[ \t]+)?def[ \t]+(?<name>" + FactoryNames + @")[ \t]*\(", RegexOptions.Multiline)]
    private static partial Regex Factory();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)[ \t]*(?::[^=\n]*)?=[ \t]*(?:[A-Za-z_]\w*\.)*(?:" + FactoryNames + @")[ \t]*\(", RegexOptions.Multiline)]
    private static partial Regex FactoryResult();

    [GeneratedRegex(@"\brun_app\(")]
    private static partial Regex RunApp();

    [GeneratedRegex(@"^[ \t]*(?:from[ \t]+falcon\.asgi[ \t]+import|from[ \t]+falcon[ \t]+import[^\n]*\basgi\b|import[ \t]+falcon\.asgi)", RegexOptions.Multiline)]
    private static partial Regex FalconAsgiImport();

    [GeneratedRegex(@"\bbackend\s*=\s*['""](?:fastapi|quart)['""]")]
    private static partial Regex DashAsgiBackend();

    [GeneratedRegex(@"^[ \t]*(?:from[ \t]+src\.|import[ \t]+src\.)", RegexOptions.Multiline)]
    private static partial Regex SrcImport();

    [GeneratedRegex(@"\bxheaders[ \t]*=[ \t]*True\b")]
    private static partial Regex XHeaders();

    [GeneratedRegex(@"^if[ \t]+__name__[ \t]*==[ \t]*['""]__main__['""][ \t]*:", RegexOptions.Multiline)]
    private static partial Regex MainGuard();

    [GeneratedRegex(@"\b(?:HTTPServer|ThreadingHTTPServer|TCPServer|make_server|serve_forever|run_simple|uvicorn\.run|waitress\.serve|run_app|start_server)\(|\.listen\(|\bapp\.run\(")]
    private static partial Regex ServesHttp();

    [GeneratedRegex(@"\b(?:environ\.get|getenv)\(\s*['""]PORT['""]\s*,\s*['""]?(?<port>\d{2,5})|\benviron\.get\(\s*['""]PORT['""]\s*\)\s*or\s*['""]?(?<port>\d{2,5})")]
    private static partial Regex PortDefault();

    [GeneratedRegex(@"^[ \t]*PORT[ \t]*=[ \t]*(?<port>\d{2,5})\b", RegexOptions.Multiline)]
    private static partial Regex PortConstant();

    [GeneratedRegex(@"\bport\s*=\s*(?<port>\d{2,5})\b")]
    private static partial Regex PortKeyword();

    [GeneratedRegex(@"\.listen\(\s*(?<port>\d{2,5})\b")]
    private static partial Regex ListenPort();

    [GeneratedRegex(@"(?:Server|make_server|serve)\(\s*\(?\s*['""][\w.:]*['""]\s*,\s*(?<port>\d{2,5})\b")]
    private static partial Regex AddressPort();

    [GeneratedRegex(@"\bserver_port\s*=\s*(\d{2,5})\b")]
    private static partial Regex ServerPort();

    [GeneratedRegex(@"(?:--bind|-b)[= ]\s*(?:[^\s:'""]*:)?(?<port>\d{2,5})\b|(?:--port|-p|--server\.port)[= ]\s*(?<port>\d{2,5})\b")]
    private static partial Regex CommandPort();

    [GeneratedRegex(@"\$\{?PORT\b")]
    private static partial Regex PortVariable();

    [GeneratedRegex(@"^python3?\s+(?!-)(\S+\.py)\b")]
    private static partial Regex ProcfileScript();

    [GeneratedRegex(@"\b(?:runserver|flask\s+run)\b")]
    private static partial Regex DevServer();

    // Only where a server is told where to listen: run(host="127.0.0.1"), launch(server_name="localhost"), HTTPServer(("localhost", 8000), …).
    [GeneratedRegex(@"(?:\b(?:run|launch|run_app|listen|serve|make_server)\(|Server\(\(?)[^)\n]*?(?:\b(?:host|server_name|address)\s*=\s*)?['""](127\.0\.0\.1|localhost)['""]")]
    private static partial Regex Localhost();

    [GeneratedRegex(@"^[A-Za-z0-9_./:=@%+-]+$")]
    private static partial Regex SafeWord();
}
