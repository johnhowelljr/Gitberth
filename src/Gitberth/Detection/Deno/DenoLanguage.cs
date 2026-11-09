namespace Gitberth.Detection;

/// <summary>
/// Deno apps: deno.json or deno.jsonc with a start or serve task, or a main.ts (main.js, server.ts…) that serves HTTP with
/// Deno's own APIs. A package.json alone stays with Node.js. Runs on the official denoland/deno image, with the
/// dependencies downloaded into it while building.
/// </summary>
internal sealed class DenoLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Deno;

    public string Hint => "deno";

    public IReadOnlyList<string> Markers { get; } = ["deno.json", "deno.jsonc"];

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        var config = DenoConfig.Load(files);
        if (config is { Unreadable: true })
        {
            var unreadable = new DetectionResult { Kind = ProjectKind.Unknown, Title = "Deno (unreadable config)" };
            unreadable.Warnings.Add($"{config.File} isn't valid JSON, so Gitberth can't read its tasks.");
            return new LanguageMatch(unreadable, Strong: false);
        }

        var tasks = config?.Tasks ?? [];
        var startName = new[] { "start", "serve" }.FirstOrDefault(tasks.ContainsKey);
        var startTask = startName is null ? null : tasks[startName];
        // Fresh 1's start task, for one, runs its development server, which reloads on changes.
        var production = startTask is not null && !DenoSources.IsDevServer(startTask);
        var preview = tasks.TryGetValue("preview", out var p) && !DenoSources.IsDevServer(p) ? p : null;
        var entry = new[] { production ? startTask : null, preview }.OfType<string>().Select(DenoSources.TaskEntry).OfType<string>().FirstOrDefault(files.Exists)
            ?? DenoSources.EntryCandidates.FirstOrDefault(files.Exists);
        // Without deno.json only an entry file written for Deno that serves HTTP itself is claimed, so a package.json
        // alone stays with Node.js. That's the only file read then: every folder of a Node repo is looked at too.
        if (config is null && entry is null) return null;
        var entryText = entry is null ? "" : DenoSources.Read(files.Full(entry));
        if (config is null && (!DenoSources.UsesDeno(entryText) || !DenoSources.Serves(entryText))) return null;
        List<(string Path, string Text)> sources = config is null ? [(entry!, entryText)] : DenoSources.Load(files);
        var framework = DenoSources.Frameworks.FirstOrDefault(f => f.Marker.IsMatch(config?.Text ?? "") || sources.Any(s => f.Marker.IsMatch(s.Text)));
        var serves = framework is not null || sources.Any(s => DenoSources.Serves(s.Text)) || (startTask?.Contains("deno serve", StringComparison.Ordinal) ?? false);

        if (config is not null && startTask is null && (!serves || entry is null))
        {
            // deno.json for formatting, linting or a JSR package, with nothing to start.
            return null;
        }

        var (version, versionSource, versionWarning) = DenoVersions.Resolve(files);
        var deno1 = version.StartsWith("1.", StringComparison.Ordinal);
        var r = new DetectionResult { Kind = ProjectKind.Deno, DockerIgnore = DenoDockerfiles.Ignore };
        r.Evidence.Add(config is null ? $"Found {entry}, which serves with Deno's own APIs" : $"Found {config.File}");
        r.Evidence.Add($"Deno {version} ({versionSource})");
        if (versionWarning is not null) r.Warnings.Add(versionWarning);

        var (start, flagPort) = Start(r, config, startName, startTask, production, preview, entry, entryText);
        var build = tasks.ContainsKey("build");
        if (build) r.Evidence.Add("Runs `deno task build` while building the image");
        int? port = null;
        if (serves)
        {
            port = Port(r, flagPort, production ? startTask : preview, framework, entry, entryText, sources);
            var local = DenoSources.Localhost(entryText) ?? DenoSources.Localhost((production ? startTask : preview) ?? "");
            if (local is not null)
                r.Warnings.Add($"The app listens on {local}, which can't be reached from outside the container. Listen on 0.0.0.0 (Deno.serve's default) instead.");
        }
        else
        {
            r.Evidence.Add("Serves no HTTP (no Deno.serve, Deno.listen or web framework), so it runs as a worker without a port");
        }
        r.Title = !serves ? "Deno worker"
            : $"Deno ({framework?.Label ?? (flagPort || (production && startTask!.Contains("deno serve", StringComparison.Ordinal)) ? "deno serve" : "Deno.serve")})";
        r.ContainerPort = port;
        var install = !deno1 && (config?.HasImports == true || files.Exists("package.json") || build);
        r.Dockerfile = DenoDockerfiles.For(new DenoBuild(version, install, entry, deno1, build, start, port, port is int p2 ? [$"PORT={p2}"] : []));
        return new LanguageMatch(r, Strong: startTask is not null || serves);
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var (path, text) in DenoSources.Load(files))
            foreach (var key in DenoSources.EnvKeys(text)) add(key, "", $"used in {path}");
    }

    /// <summary>
    /// `deno task start` when that task runs the app as it runs in production; else the preview task; else `deno serve`
    /// for an entry that exports a fetch handler; else `deno run -A` on the entry. FlagPort says deno serve gets --port $PORT.
    /// </summary>
    private static (IReadOnlyList<string> Start, bool FlagPort) Start(
        DetectionResult r, DenoConfig? config, string? startName, string? startTask, bool production, string? preview, string? entry, string entryText)
    {
        if (production)
        {
            r.Evidence.Add($"Starts with `deno task {startName}` ({config!.File}: `{startTask}`)");
            return (["deno", "task", startName!], false);
        }
        if (startTask is not null) r.Evidence.Add($"The {startName} task runs a development server (`{startTask}`), so it isn't used to start the app");
        if (preview is not null)
        {
            r.Evidence.Add($"Starts with `deno task preview` (`{preview}`)");
            return (["deno", "task", "preview"], false);
        }
        if (entry is not null && DenoSources.ExportsFetch(entryText))
        {
            r.Evidence.Add($"{entry} exports a default fetch handler, so it starts with `deno serve -A --port $PORT {entry}`");
            return (["sh", "-c", $"exec deno serve -A --port ${{PORT:-8000}} {entry}"], true);
        }
        if (entry is not null)
        {
            r.Evidence.Add($"Starts with `deno run -A {entry}`");
            return (["deno", "run", "-A", entry], false);
        }
        r.Warnings.Add("No start task or entry file (main.ts, server.ts…) was found. Add a \"start\" task to deno.json, or customize the Dockerfile.");
        return (startName is null ? ["deno", "run", "-A", "main.ts"] : ["deno", "task", startName], false);
    }

    /// <summary>
    /// deno serve's --port; else a port in the code (PORT with a fallback, Deno.serve's or listen's port option, an address,
    /// a PORT constant), the entry file first; else the framework's default; else 8000, Deno.serve's own.
    /// </summary>
    private static int Port(DetectionResult r, bool flagPort, string? task, DenoFramework? framework, string? entry, string entryText, List<(string Path, string Text)> sources)
    {
        if (flagPort)
        {
            r.Evidence.Add("Listens on PORT, or 8000 when it isn't set (deno serve's --port)");
            return 8000;
        }
        if (task is not null && task.Contains("deno serve", StringComparison.Ordinal))
        {
            var flag = DenoSources.FlagPort(task);
            r.Evidence.Add(flag is int set ? $"Listens on port {set} (the task's --port)" : "Port 8000 (deno serve's default)");
            return flag ?? 8000;
        }
        var found = entry is not null && DenoSources.CodePort(entryText) is { } fromEntry ? (fromEntry.Port, fromEntry.FromEnv, File: entry) : ((int Port, bool FromEnv, string File)?)null;
        if (found is null)
        {
            foreach (var (path, text) in sources)
            {
                if (DenoSources.CodePort(text) is not { } code) continue;
                found = (code.Port, code.FromEnv, path);
                break;
            }
        }
        if (found is { } f)
        {
            r.Evidence.Add(f.FromEnv ? $"Listens on PORT, or {f.Port} when it isn't set ({f.File})" : $"Listens on port {f.Port} ({f.File})");
            return f.Port;
        }
        if (framework is { Label: not ("Hono" or "Oak") })
        {
            r.Evidence.Add($"Port {framework.DefaultPort} ({framework.Label}'s default)");
            return framework.DefaultPort;
        }
        r.Evidence.Add("Port 8000, Deno.serve's default when it's given no port (it doesn't read PORT)");
        return 8000;
    }
}
