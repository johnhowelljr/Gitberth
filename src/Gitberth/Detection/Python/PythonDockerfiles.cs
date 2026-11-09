using System.Globalization;

namespace Gitberth.Detection;

/// <summary>Native dependencies: what compiles them, and the shared libraries they need when the app runs.</summary>
/// <param name="Compiled">The packages built from source, which need the full python image's compilers and headers.</param>
/// <param name="BuildPackages">apt packages the build needs on top of the full image.</param>
/// <param name="RuntimePackages">apt packages the slim image needs at run time, like libpq5 for psycopg2.</param>
/// <param name="RuntimeUsers">The dependencies that load them.</param>
/// <param name="RunInFullImage">The app runs on the full image too, whose libraries cover what's hard to list.</param>
internal sealed record NativeLibraries(IReadOnlyList<string> Compiled, IReadOnlyList<string> BuildPackages, IReadOnlyList<string> RuntimePackages,
    IReadOnlyList<string> RuntimeUsers, bool RunInFullImage)
{
    public static readonly NativeLibraries None = new([], [], [], [], false);

    public bool CompileInFullImage => Compiled.Count > 0;

    /// <summary>
    /// psycopg2 and mysqlclient compile against libpq and the MariaDB client, which the full image has and the slim
    /// one gets at run time; plain psycopg (3) loads libpq itself. python-ldap and uWSGI link more than is easy to list,
    /// so they run on the full image. Everything else is expected to ship wheels, which keeps the common case slim.
    /// </summary>
    public static NativeLibraries For(PythonProject p)
    {
        var compiled = new List<string>();
        var build = new List<string>();
        var runtime = new List<string>();
        var users = new List<string>();
        var full = false;
        if (p.Has("psycopg2"))
        {
            compiled.Add("psycopg2");
            runtime.Add("libpq5");
            users.Add("psycopg2");
        }
        if (p.Dependency("psycopg") is { } psycopg && !psycopg.HasExtra("binary"))
        {
            if (psycopg.HasExtra("c")) compiled.Add("psycopg[c]");
            runtime.Add("libpq5");
            users.Add("psycopg");
        }
        if (p.Has("mysqlclient"))
        {
            compiled.Add("mysqlclient");
            // mysqlclient finds the MariaDB client library with pkg-config, which the full image lacks.
            build.Add("pkg-config");
            runtime.Add("libmariadb3");
            users.Add("mysqlclient");
        }
        if (p.Has("python-ldap"))
        {
            compiled.Add("python-ldap");
            build.AddRange(["libldap-dev", "libsasl2-dev"]);
            full = true;
        }
        if (p.Has("uwsgi"))
        {
            compiled.Add("uwsgi");
            full = true;
        }
        if (compiled.Count == 0 && runtime.Count == 0) return None;
        return full ? new NativeLibraries(compiled, build, [], [], true) : new NativeLibraries(compiled, build, runtime.Distinct().ToList(), users, false);
    }
}

/// <summary>A Node stage that builds the front-end assets of a Python web app (Tailwind, or Vite for Django or Flask templates).</summary>
/// <param name="Bun">The oven/bun tag whose bun installs and builds them, when the front end uses bun.</param>
internal sealed record PythonAssets(int Node, PackageManager Pm, string? Bun = null);

/// <summary>Everything the generated Python Dockerfile is made from.</summary>
/// <param name="ServerPackages">gunicorn or uvicorn, installed when the start command needs them and the app doesn't depend on them.</param>
internal sealed record PythonImage(PyVersion Version, PythonInstall Install, IReadOnlyList<string> ServerPackages, NativeLibraries Native, PythonAssets? Assets, PythonApp App);

/// <summary>
/// Gitberth's Dockerfiles for Python. A build stage installs the dependencies into a virtualenv at /opt/venv, which the
/// final python:&lt;version&gt;-slim image copies with the app, so the tools that installed them stay behind.
/// </summary>
internal static class PythonDockerfiles
{
    /// <summary>uv's distroless image, which holds /uv and /uvx; its {major}.{minor} tag follows the newest patch release.</summary>
    public const string UvImage = "ghcr.io/astral-sh/uv:0.13";

    public const string GunicornPackage = "gunicorn<27";
    public const string UvicornPackage = "uvicorn[standard]<1";

    private const string Venv = "/opt/venv";
    private const string VenvEnv = "VIRTUAL_ENV=/opt/venv PATH=/opt/venv/bin:$PATH";
    private const string PipCache = "--mount=type=cache,id=pip,target=/root/.cache/pip";
    private const string UvCache = "--mount=type=cache,id=uv,target=/root/.cache/uv";

    /// <summary>What never goes into the image: git, virtualenvs, caches and build leftovers. An "env" folder only when it's a virtualenv.</summary>
    public static string Ignore(RepoFiles files) => string.Join('\n',
    [
        ".git", ".venv", "venv", .. files.Exists("env/pyvenv.cfg") ? new[] { "env" } : Array.Empty<string>(),
        "**/__pycache__", "**/*.py[cod]", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".nox", "**/*.egg-info",
        "**/node_modules", ".coverage", "htmlcov",
    ]) + "\n";

    public static string Dockerfile(PythonImage b)
    {
        var lines = new List<string?>();
        if (b.Assets is { } assets)
        {
            lines.AddRange(AssetsStage(assets));
            lines.Add("");
        }
        var source = b.Assets is null ? "COPY . ." : "COPY --from=assets /app ./";
        var version = b.Version.ToString();

        if (b.Install.Tool == PythonTool.None && b.ServerPackages.Count == 0)
        {
            // Nothing to install, so a single stage.
            lines.AddRange([$"FROM python:{version}-slim", "WORKDIR /app", .. b.App.EnvComments, Env(FinalEnv(b, venv: false)), source, DockerfileMarkers.BuildArgs, .. b.App.BuildSteps]);
            lines.AddRange(Final(b));
            return Lines(lines);
        }

        if (b.Native.CompileInFullImage) lines.Add($"# The full image has the compilers and headers that {And(b.Native.Compiled)} {(b.Native.Compiled.Count == 1 ? "builds" : "build")} with.");
        lines.Add($"FROM python:{version}{(b.Native.CompileInFullImage ? "" : "-slim")} AS build");
        lines.Add("WORKDIR /app");
        if (b.Native.BuildPackages.Count > 0) lines.Add(AptInstall(b.Native.BuildPackages));
        // Build steps such as collectstatic load the app, so they get its settings too.
        if (b.App.BuildSteps.Count > 0) lines.Add(Env(b.App.Env));
        lines.AddRange(ToolSetup(b.Install.Tool));
        lines.Add(source);
        lines.Add(DockerfileMarkers.BuildArgs);
        lines.AddRange(InstallSteps(b));
        lines.AddRange(b.App.BuildSteps);
        lines.Add("");
        lines.Add($"FROM python:{version}{(b.Native.RunInFullImage ? "" : "-slim")}");
        if (b.Native.RuntimePackages.Count > 0)
        {
            lines.Add($"# The shared libraries {And(b.Native.RuntimeUsers)} {(b.Native.RuntimeUsers.Count == 1 ? "loads" : "load")} at run time.");
            lines.Add(AptInstall(b.Native.RuntimePackages));
        }
        lines.Add("WORKDIR /app");
        lines.AddRange(b.App.EnvComments);
        lines.Add(Env(FinalEnv(b, venv: true)));
        lines.Add($"COPY --from=build {Venv} {Venv}");
        lines.Add("COPY --from=build /app /app");
        lines.AddRange(Final(b));
        return Lines(lines);
    }

    private static IEnumerable<string?> Final(PythonImage b) =>
    [
        DockerfileMarkers.Volumes,
        b.App.Port is int port ? $"EXPOSE {port}" : null,
        Cmd(b.App),
    ];

    private static IEnumerable<string> AssetsStage(PythonAssets a) =>
    [
        $"FROM node:{a.Node}-slim AS assets",
        "WORKDIR /app",
        .. a.Pm.UsesCorepack ? new[] { "ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0", "RUN npm install -g corepack@latest && corepack enable" } : Array.Empty<string>(),
        // bun's single binary, from its own image, like a Node app that installs with bun.
        .. a.Bun is { } bun ? new[] { $"COPY --from=oven/bun:{bun} /usr/local/bin/bun /usr/local/bin/bun", "RUN ln -s bun /usr/local/bin/bunx" } : Array.Empty<string>(),
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        $"RUN {a.Pm.CacheMount}{a.Pm.InstallCommand}",
        "# Only what the build made goes on to the Python image, not node_modules.",
        $"RUN {a.Pm.Run("build")} && rm -rf node_modules",
    ];

    // The tool that installs the dependencies, then the virtualenv it installs into (uv makes its own).
    private static IEnumerable<string> ToolSetup(PythonTool tool)
    {
        const string tools = "ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore";
        switch (tool)
        {
            case PythonTool.Uv:
                yield return $"COPY --from={UvImage} /uv /uvx /bin/";
                yield return "# uv syncs into /opt/venv, compiling the bytecode for a faster start.";
                yield return $"ENV UV_PROJECT_ENVIRONMENT={Venv} UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never {VenvEnv}";
                yield break;
            case PythonTool.UvPip:
                yield return $"COPY --from={UvImage} /uv /uvx /bin/";
                yield return "ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never";
                break;
            case PythonTool.Poetry:
                yield return tools;
                yield return $"RUN {PipCache} pip install 'poetry>=2,<3'";
                break;
            case PythonTool.Pdm:
                yield return $"{tools} PDM_CHECK_UPDATE=false PDM_IGNORE_SAVED_PYTHON=1";
                yield return $"RUN {PipCache} pip install 'pdm>=2,<3'";
                break;
            case PythonTool.Pipenv:
                yield return $"{tools} PIPENV_VERBOSITY=-1";
                yield return $"RUN {PipCache} pip install pipenv";
                break;
            default:
                yield return "ENV PIP_DISABLE_PIP_VERSION_CHECK=1";
                break;
        }
        yield return tool is PythonTool.Poetry or PythonTool.Pdm or PythonTool.Pipenv
            ? $"# The dependencies go into a virtualenv, which the final image copies; {new PythonInstall(tool, "", true, false).Name} installs into the active one."
            : "# The dependencies go into a virtualenv, which the final image copies.";
        yield return $"RUN python -m venv {Venv}";
        yield return $"ENV {VenvEnv}";
    }

    private static IEnumerable<string> InstallSteps(PythonImage b)
    {
        var install = b.Install;
        var servers = string.Concat(b.ServerPackages.Select(s => " " + PythonApps.ShellQuote(s)));
        switch (install.Tool)
        {
            case PythonTool.Uv:
                yield return "# --python: the image's own Python, even when .python-version names a patch release the image doesn't have.";
                yield return $"RUN {UvCache} uv sync{(install.Locked ? " --frozen" : "")} --no-dev{(install.InstallsProject ? "" : " --no-install-project")} --python /usr/local/bin/python";
                if (servers.Length > 0) yield return $"RUN {UvCache} uv pip install{servers}";
                break;
            case PythonTool.UvPip:
                yield return $"RUN {UvCache} uv pip install {(install.InstallsProject ? "." : "-r pyproject.toml")}{servers}";
                break;
            case PythonTool.Poetry:
                yield return $"RUN --mount=type=cache,id=poetry,target=/root/.cache/pypoetry poetry install --only main{(install.InstallsProject ? "" : " --no-root")} --no-interaction --no-ansi";
                if (servers.Length > 0) yield return $"RUN {PipCache} pip install{servers}";
                break;
            case PythonTool.Pdm:
                yield return $"RUN --mount=type=cache,id=pdm,target=/root/.cache/pdm pdm {(install.Locked ? "sync" : "install")} --prod{(install.InstallsProject ? "" : " --no-self")}";
                if (servers.Length > 0) yield return $"RUN {PipCache} pip install{servers}";
                break;
            case PythonTool.Pipenv:
                yield return $"RUN --mount=type=cache,id=pipenv,target=/root/.cache/pipenv {PipCache} pipenv {(install.Locked ? "sync" : "install")}";
                if (servers.Length > 0) yield return $"RUN {PipCache} pip install{servers}";
                break;
            case PythonTool.Pip:
                var what = install.RequirementsFile is { } file ? "-r " + PythonApps.ShellQuote(file)
                    : install.InstallsProject ? "." : string.Join(' ', (install.Packages ?? []).Select(PythonApps.ShellQuote));
                yield return $"RUN {PipCache} pip install {what}{servers}";
                break;
            default:
                yield return $"RUN {PipCache} pip install{servers}";
                break;
        }
    }

    private static List<KeyValuePair<string, string>> FinalEnv(PythonImage b, bool venv)
    {
        var env = new List<KeyValuePair<string, string>> { new("PYTHONUNBUFFERED", "1") };
        if (venv) env.Add(new("PATH", $"{Venv}/bin:$PATH"));
        if (b.App.SrcLayout) env.Add(new("PYTHONPATH", "/app/src"));
        if (b.App.Port is int port) env.Add(new("PORT", port.ToString(CultureInfo.InvariantCulture)));
        env.AddRange(b.App.Env);
        return env;
    }

    /// <summary>
    /// The start command. The server is exec'd, so it gets the container's stop signal; a shell runs it when it needs
    /// ${PORT} or runs other commands first.
    /// </summary>
    internal static string Cmd(PythonApp app)
    {
        if (app.BeforeStart.Count == 0 && app.CollectStaticAtStart is null && PlainWords(app.Command))
            return $"CMD {Json(app.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries))}";
        var script = WithExec(string.Join(" && ", app.BeforeStart.Append(app.Command)));
        if (app.CollectStaticAtStart is { } collect) script = $"if [ -f .collectstatic-at-start ]; then {collect}; fi; {script}";
        return $"CMD {Json(["sh", "-c", script])}";
    }

    /// <summary>
    /// "a &amp;&amp; b" → "a &amp;&amp; exec b". A command with other shell operators (;, ||, a pipe, &amp;) is left as it is,
    /// since exec'ing part of it would cut the rest off.
    /// </summary>
    internal static string WithExec(string script)
    {
        var last = 0;
        char? quote = null;
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '&' when i + 1 < script.Length && script[i + 1] == '&':
                    last = i + 2;
                    i++;
                    break;
                case ';' or '|' or '&' or '`' or '\n':
                    return script;
            }
        }
        var tail = script[last..].TrimStart();
        if (tail.StartsWith("exec ", StringComparison.Ordinal)) return script;
        return script[..last] + (last > 0 ? " " : "") + "exec " + tail;
    }

    private static bool PlainWords(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => w == PythonApps.ShellQuote(w) && !w.Contains('$'));

    private static string AptInstall(IEnumerable<string> packages) =>
        $"RUN apt-get update && apt-get install -y --no-install-recommends {string.Join(' ', packages)} && rm -rf /var/lib/apt/lists/*";

    private static string? Env(IReadOnlyCollection<KeyValuePair<string, string>> env) =>
        env.Count == 0 ? null : "ENV " + string.Join(' ', env.Select(e => $"{e.Key}={(e.Value.Contains(' ') ? "\"" + e.Value.Replace("\"", "\\\"") + "\"" : e.Value)}"));

    private static string And(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static string Json(IEnumerable<string> args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Lines(IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}
