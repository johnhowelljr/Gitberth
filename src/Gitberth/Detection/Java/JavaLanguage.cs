using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// Java and Kotlin apps built with Maven or Gradle: Spring Boot, Quarkus, Micronaut, Ktor, and any other application
/// (Javalin, Helidon, Vert.x) that the application plugin or a shaded jar makes runnable. A multi-module build runs its
/// application module. The JDK image builds it, and eclipse-temurin's JRE runs it.
/// </summary>
internal sealed partial class JavaLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Java;

    public string Hint => "java";

    public IReadOnlyList<string> Markers { get; } = ["pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "PORT", "SERVER_PORT", "QUARKUS_HTTP_PORT", "QUARKUS_HTTP_HOST", "MICRONAUT_SERVER_PORT", "JAVA_TOOL_OPTIONS", "JAVA_OPTS", "JAVA_HOME",
        "SERVER_FORWARD_HEADERS_STRATEGY", "HOSTNAME", "HOME", "USER", "PATH",
    };

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        var folder = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        if (JvmProject.BuildAbove(repoRoot, appDir) is { } above) return ModuleOfBuildAbove(repoRoot, folder, above);
        var warnings = new List<string>();
        if (JvmProject.Load(folder, warnings) is not { } project) return null;
        var app = project.App;
        var r = new DetectionResult { Kind = ProjectKind.Java, DockerIgnore = JvmDockerfiles.Ignore };
        r.Warnings.AddRange(warnings);
        var language = app.Kotlin ? "Kotlin" : "Java";
        r.Title = app.Framework is null ? language : $"{language} ({app.Framework})";

        var (java, javaSource, toolchain) = JavaVersions.Resolve(project);
        var runtime = JavaVersions.ImageVersion(java);
        // A Gradle toolchain only accepts a JDK of exactly its version.
        var buildJava = project.Gradle && toolchain ? java : runtime;
        var tool = project.Gradle ? "Gradle" : "Maven";
        var buildFile = project.Gradle ? (File.Exists(Path.Combine(folder, "build.gradle.kts")) ? "build.gradle.kts" : File.Exists(Path.Combine(folder, "build.gradle")) ? "build.gradle" : "settings.gradle") : "pom.xml";
        r.Evidence.Add($"Found {buildFile}; builds with {tool} ({(project.Wrapper ? $"the repo's {(project.Gradle ? "gradlew" : "mvnw")}" : $"from the {(project.Gradle ? "gradle" : "maven")} image")}), skipping the tests");
        if (app.Dir.Length > 0) r.Evidence.Add($"Multi-module build: runs the {app.Dir} module");
        r.Evidence.Add($"Java {java} ({javaSource}){(runtime != java ? $", run on Java {runtime}" : "")}");
        r.Warnings.AddRange(JavaVersions.Warnings(java));

        var moduleFolder = app.Dir.Length == 0 ? folder : Path.Combine(folder, app.Dir.Replace('/', Path.DirectorySeparatorChar));
        var config = AppConfig(moduleFolder);
        var artifact = app.Artifact!.Value;
        var env = new List<string>();
        string? envComment = null;
        string? portVariable = null;
        (int Port, string Source)? port = null;
        switch (app.Framework)
        {
            case "Spring Boot":
                port = ConfigPort(config, "server.port");
                portVariable = "SERVER_PORT";
                if (!config.ContainsKey("server.forward-headers-strategy"))
                {
                    env.Add("SERVER_FORWARD_HEADERS_STRATEGY=framework");
                    envComment = "Spring Boot trusts the X-Forwarded-* headers of the proxy in front (Cloudflare Tunnel).";
                    r.Evidence.Add("Trusts the proxy's X-Forwarded-* headers (SERVER_FORWARD_HEADERS_STRATEGY=framework), so the app sees HTTPS and the visitor's address");
                }
                break;
            case "Quarkus":
                port = ConfigPort(config, "quarkus.http.port");
                portVariable = "QUARKUS_HTTP_PORT";
                env.Add("QUARKUS_HTTP_HOST=0.0.0.0");
                if (!config.Keys.Any(k => k.StartsWith("quarkus.http.proxy.", StringComparison.Ordinal)))
                {
                    env.AddRange(["QUARKUS_HTTP_PROXY_PROXY_ADDRESS_FORWARDING=true", "QUARKUS_HTTP_PROXY_ALLOW_X_FORWARDED=true"]);
                    envComment = "Quarkus listens on every address and trusts the proxy's X-Forwarded-* headers.";
                    r.Evidence.Add("Trusts the proxy's X-Forwarded-* headers (quarkus.http.proxy), so the app sees HTTPS and the visitor's address");
                }
                // An uber-jar replaces the quarkus-app folder with one *-runner.jar.
                if (config.GetValueOrDefault("quarkus.package.jar.type") == "uber-jar" || config.GetValueOrDefault("quarkus.package.type") == "uber-jar")
                    artifact = JvmArtifact.Jar;
                break;
            case "Micronaut":
                port = ConfigPort(config, "micronaut.server.port");
                portVariable = "MICRONAUT_SERVER_PORT";
                break;
            case "Ktor":
                port = ConfigPort(config, "ktor.deployment.port") ?? KtorConfPort(moduleFolder);
                if (!SourceMentions(moduleFolder, "ForwardedHeaders"))
                    r.Warnings.Add("Behind Cloudflare Tunnel the app sees plain HTTP from the proxy: install Ktor's XForwardedHeaders plugin so it sees HTTPS and the visitor's address.");
                break;
        }
        // Spring Boot, Quarkus and Micronaut take their port from their settings; the others set it in code.
        if (app.Framework is not ("Spring Boot" or "Quarkus" or "Micronaut")) port ??= CodePort(moduleFolder);
        var listen = port?.Port ?? 8080;
        r.ContainerPort = listen;
        r.Evidence.Add(port is { } p ? $"Listens on port {p.Port} ({p.Source})"
            : $"Port 8080 ({(app.Framework is null ? "Gitberth's" : app.Framework + "'s")} default; the app gets PORT=8080{(portVariable is null ? "" : $" and {portVariable}")})");

        var (path, run) = Output(project, app, artifact);
        r.Evidence.Add(artifact switch
        {
            JvmArtifact.QuarkusApp => "Runs quarkus-app/quarkus-run.jar, Quarkus' fast-jar layout",
            JvmArtifact.InstallDist => $"Runs {run}, the start script the application plugin's installDist makes",
            _ => $"Runs the {(app.Framework == "Spring Boot" ? "Spring Boot" : "runnable")} {app.Archive} with `{run}`",
        });
        r.Evidence.Add("The heap is sized from the container's memory limit (-XX:MaxRAMPercentage=75 in JAVA_TOOL_OPTIONS)");

        // A front end that Maven or Gradle doesn't build itself (frontend-maven-plugin, the node-gradle plugin, Vaadin) builds first.
        NodeAssets? node = null;
        if (!BuildsNodeItself().IsMatch(project.AllBuildText) && NodeAssets.Detect(folder, r) is { HasBuild: true } pkg)
        {
            node = pkg;
            node.Describe(r, "the front end before the JVM build, so the app includes it");
        }

        var buildImage = project.Wrapper
            ? $"eclipse-temurin:{buildJava}-jdk"
            : project.Gradle ? $"gradle:{(buildJava >= 17 ? 9 : 8)}-jdk{Math.Max(buildJava, 11)}" : $"maven:3-eclipse-temurin-{buildJava}";
        r.Dockerfile = JvmDockerfiles.Render(new JvmPlan(buildImage, project.Gradle, BuildCommand(project, app), node, artifact, path, app.Archive, runtime,
            env, envComment, portVariable, listen, run));
        return new LanguageMatch(r, Strong: true);
    }

    /// <summary>
    /// A folder that's one module of a build further up isn't offered on its own, since it can't build without the rest.
    /// When it is an application, analyzing it says where the build is.
    /// </summary>
    private static LanguageMatch? ModuleOfBuildAbove(string repoRoot, string folder, string above)
    {
        var buildRoot = above.Length == 0 ? repoRoot : Path.Combine(repoRoot, above.Replace('/', Path.DirectorySeparatorChar));
        if (JvmProject.Load(folder, [], buildRoot) is not { } module) return null;
        var where = above.Length == 0 ? "the repo root" : above + "/";
        var r = new DetectionResult { Kind = ProjectKind.Unknown, Title = "Module of a JVM build" };
        r.Warnings.Add($"This folder is a module of the {(module.Gradle ? "Gradle" : "Maven")} build in {where}, which builds it together with the other modules. Use {where} as the app's folder, and Gitberth runs this module from there.");
        return new LanguageMatch(r, Strong: false);
    }

    private static string BuildCommand(JvmProject project, JvmModule app)
    {
        if (project.Gradle)
        {
            var task = app.Dir.Length == 0 ? app.Task : $":{app.Dir.Replace('/', ':')}:{app.Task}";
            return $"{(project.Wrapper ? "sh gradlew" : "gradle")} --no-daemon {task}";
        }
        var module = app.Dir.Length == 0 ? "" : $"-pl {app.Dir} -am ";
        return $"{(project.Wrapper ? "sh mvnw" : "mvn")} -B -DskipTests {module}package{(app.Task.Length == 0 ? "" : " " + app.Task)}";
    }

    /// <summary>Where the build leaves what runs, relative to the build's root, and the command that runs it from /app.</summary>
    private static (string Path, string Run) Output(JvmProject project, JvmModule app, JvmArtifact artifact)
    {
        var dir = app.Dir.Length == 0 ? "" : app.Dir + "/";
        return artifact switch
        {
            JvmArtifact.QuarkusApp => ($"{dir}{(project.Gradle ? "build" : "target")}/quarkus-app", "java -jar quarkus-run.jar"),
            JvmArtifact.InstallDist => ($"{dir}build/install/{app.Name}", $"bin/{app.Name}"),
            // Quarkus writes its uber-jar into build/ itself rather than build/libs.
            _ => ($"{dir}{(project.Gradle ? app.Framework == "Quarkus" ? "build" : "build/libs" : "target")}", $"java -jar app.{app.Archive}"),
        };
    }

    /// <summary>
    /// The app's settings from src/main/resources: application.properties, and the first document of application.yml
    /// (flattened to dotted keys), which Spring Boot, Quarkus, Micronaut and Ktor all read.
    /// </summary>
    internal static Dictionary<string, string> AppConfig(string moduleFolder)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        var resources = Path.Combine(moduleFolder, "src", "main", "resources");
        foreach (var name in new[] { "application.yml", "application.yaml" })
            if (RubyApp.Read(Path.Combine(resources, name)) is { } yaml)
                foreach (var (k, v) in FlattenYaml(yaml)) config[k] = v;
        if (RubyApp.Read(Path.Combine(resources, "application.properties")) is { } properties)
        {
            foreach (var raw in properties.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or '!') continue;
                var at = line.IndexOfAny(['=', ':']);
                if (at > 0) config[line[..at].Trim()] = line[(at + 1)..].Trim();
            }
        }
        return config;
    }

    /// <summary>A YAML file's scalar values by dotted key ("server: port: 8081" → server.port = 8081), up to its first `---`.</summary>
    internal static Dictionary<string, string> FlattenYaml(string yaml)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = new List<(int Indent, string Key)>();
        var started = false;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimEnd() == "---")
            {
                if (started) break;
                continue;
            }
            var hash = raw.IndexOf(" #", StringComparison.Ordinal);
            var line = (raw.TrimStart().StartsWith('#') ? "" : hash >= 0 ? raw[..hash] : raw).TrimEnd();
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('-')) continue;
            var m = YamlKey().Match(line);
            if (!m.Success) continue;
            started = true;
            var indent = m.Groups[1].Value.Length;
            while (path.Count > 0 && path[^1].Indent >= indent) path.RemoveAt(path.Count - 1);
            var key = m.Groups[2].Value.Trim('"', '\'');
            var value = m.Groups[3].Value.Trim();
            if (value.Length == 0)
            {
                path.Add((indent, key));
                continue;
            }
            values[string.Join('.', path.Select(p => p.Key).Append(key))] = value.Trim('"', '\'');
        }
        return values;
    }

    /// <summary>The port a setting gives: 8081, ${PORT:8081}, or Ktor's "$PORT:8081"; null when it only says ${PORT}.</summary>
    internal static (int Port, string Source)? ConfigPort(IReadOnlyDictionary<string, string> config, string key)
    {
        if (!config.TryGetValue(key, out var value)) return null;
        var m = PortValue().Match(value);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), $"{key} in the app's config") : null;
    }

    /// <summary>Ktor's HOCON application.conf: `port = 8080` in its deployment block.</summary>
    private static (int Port, string Source)? KtorConfPort(string moduleFolder) =>
        RubyApp.Read(Path.Combine(moduleFolder, "src", "main", "resources", "application.conf")) is { } conf && ConfPort().Match(conf) is { Success: true } m
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "port in application.conf")
            : null;

    /// <summary>A port the code sets: embeddedServer(port = 8080), System.getenv("PORT") ?: 8080, .start(7070), .listen(8888).</summary>
    internal static (int Port, string Source)? CodePort(string moduleFolder)
    {
        foreach (var file in SourceFiles(moduleFolder))
        {
            var text = RubyApp.Read(file);
            if (text is not null && CodePortPattern().Match(text) is { Success: true } m)
            {
                var number = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                return (int.Parse(number, CultureInfo.InvariantCulture), $"set in {Path.GetFileName(file)}");
            }
        }
        return null;
    }

    private static bool SourceMentions(string moduleFolder, string text) =>
        SourceFiles(moduleFolder).Any(f => RubyApp.Read(f)?.Contains(text, StringComparison.Ordinal) == true);

    private static IEnumerable<string> SourceFiles(string moduleFolder)
    {
        var main = Path.Combine(moduleFolder, "src", "main");
        if (!Directory.Exists(main)) return [];
        try
        {
            return Directory.EnumerateFiles(main, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".kt", StringComparison.Ordinal) || f.EndsWith(".java", StringComparison.Ordinal))
                .Take(500).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var rel in files.Files.Where(f => !f.Contains("src/test/", StringComparison.Ordinal)).Take(5000))
        {
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            var code = name.EndsWith(".java", StringComparison.Ordinal) || name.EndsWith(".kt", StringComparison.Ordinal)
                || name.EndsWith(".groovy", StringComparison.Ordinal) || name.EndsWith(".scala", StringComparison.Ordinal);
            var settings = ConfigFile().IsMatch(name);
            if (!code && !settings) continue;
            var text = RubyApp.Read(files.Full(rel));
            if (string.IsNullOrEmpty(text)) continue;
            if (code)
            {
                foreach (Match m in Getenv().Matches(text))
                    if (!IgnoredKeys.Contains(m.Groups[1].Value)) add(m.Groups[1].Value, "", $"used in {rel}");
            }
            // ${DB_URL} and ${DB_URL:default} in settings and @Value annotations, ${?DB_URL} in HOCON. Lower-case names are properties.
            foreach (Match m in Placeholder().Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (IgnoredKeys.Contains(key)) continue;
                var fallback = m.Groups["default"].Success ? m.Groups["default"].Value.Trim() : "";
                add(key, "", fallback.Length == 0 ? $"used in {rel}" : $"used in {rel} (default {fallback})");
            }
        }
    }

    [GeneratedRegex(@"frontend-maven-plugin|com\.github\.node-gradle\.node|com\.moowork\.node|org\.siouan\.frontend|vaadin")]
    private static partial Regex BuildsNodeItself();

    [GeneratedRegex(@"^(\s*)([A-Za-z0-9_.\-""']+)\s*:(?:\s+(.*))?$")]
    private static partial Regex YamlKey();

    [GeneratedRegex(@"^(?:""|')?(?:\$\{?PORT\}?:-?|\$\{[A-Za-z_]+:)?(\d{2,5})\}?(?:""|')?$")]
    private static partial Regex PortValue();

    [GeneratedRegex(@"\bport\s*[=:]\s*(\d{2,5})\b")]
    private static partial Regex ConfPort();

    [GeneratedRegex(@"embeddedServer\([^)]*?\bport\s*=\s*(\d{2,5})|getenv\(\s*""PORT""\s*\)[^\n;]*?(?:\?:|orElse\(|\?\?)\s*""?(\d{2,5})|getOrDefault\(\s*""PORT""\s*,\s*""(\d{2,5})""|\.start\(\s*(\d{2,5})\s*\)|\.listen\(\s*(\d{2,5})")]
    private static partial Regex CodePortPattern();

    [GeneratedRegex(@"^(?:application|bootstrap)(?:-[\w-]+)?\.(?:properties|ya?ml|conf)$")]
    private static partial Regex ConfigFile();

    [GeneratedRegex(@"getenv\(\s*""([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex Getenv();

    [GeneratedRegex(@"\$\{\??(?<key>[A-Z][A-Z0-9_]*)(?::(?<default>[^}\n]*))?\}")]
    private static partial Regex Placeholder();
}
