using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Gitberth.Detection;

/// <summary>What a JVM build makes for the image: one runnable jar, Quarkus' quarkus-app folder, or installDist's bin/ and lib/.</summary>
internal enum JvmArtifact { Jar, QuarkusApp, InstallDist }

/// <summary>One project of a Maven or Gradle build, and how it runs when it's an application.</summary>
internal sealed class JvmModule
{
    /// <summary>Its folder, relative to the build's root folder; "" for the root project.</summary>
    public required string Dir { get; init; }

    /// <summary>Its build file's text (pom.xml, build.gradle.kts or build.gradle).</summary>
    public required string Text { get; init; }

    /// <summary>The Gradle project name (installDist names its folder and start script after it), or the Maven artifactId.</summary>
    public required string Name { get; init; }

    public string? Framework { get; set; }

    /// <summary>What it builds into; null when it isn't an application, like a library.</summary>
    public JvmArtifact? Artifact { get; set; }

    /// <summary>The Gradle task that builds the artifact, or Maven goals added after `package`.</summary>
    public string Task { get; set; } = "";

    /// <summary>"war" for a Spring Boot app packaged as an executable war, which `java -jar` runs too; else "jar".</summary>
    public string Archive { get; set; } = "jar";

    public bool Kotlin { get; set; }
}

/// <summary>A Maven or Gradle build in a folder: its build tool, wrapper, projects, and the one Gitberth runs.</summary>
internal sealed partial class JvmProject
{
    public required string Root { get; init; }
    public required bool Gradle { get; init; }

    /// <summary>The repo's mvnw or gradlew builds it, with the build tool version the project chose.</summary>
    public required bool Wrapper { get; init; }

    /// <summary>The project's applications, the chosen one first.</summary>
    public required List<JvmModule> Apps { get; init; }

    /// <summary>Every build file's text and the version catalog's, for facts that can live in any of them.</summary>
    public required string AllBuildText { get; init; }

    public JvmModule App => Apps[0];

    // Names that usually hold the app in a multi-module build, and names that usually don't.
    private static readonly HashSet<string> MainNames = new(StringComparer.OrdinalIgnoreCase) { "app", "application", "web", "webapp", "server", "api", "backend", "service", "main", "boot" };
    private static readonly string[] AsideWords = ["demo", "example", "sample", "test", "e2e", "benchmark", "bench", "docs", "tools", "integration"];

    /// <summary>
    /// The build in this folder, with its applications ranked, or null when there's no build file or nothing in it runs
    /// (a library). Warnings say what was picked from several, and why a wrapper wasn't used.
    /// </summary>
    /// <param name="buildRoot">For a module of a Gradle build further up: that build's folder, whose version catalog it uses.</param>
    public static JvmProject? Load(string root, List<string> warnings, string? buildRoot = null)
    {
        var pom = File.Exists(Path.Combine(root, "pom.xml"));
        var gradleFiles = new[] { "build.gradle.kts", "build.gradle", "settings.gradle.kts", "settings.gradle" }.Any(f => File.Exists(Path.Combine(root, f)));
        // A repo that has both is usually moving between them; its wrapper says which one it builds with.
        var gradle = gradleFiles && (!pom || File.Exists(Path.Combine(root, "gradlew")));
        if (!gradle && !pom) return null;
        var (modules, text) = gradle ? GradleModules(root, buildRoot ?? root) : MavenModules(root, warnings);
        var apps = modules.Where(m => m.Artifact is not null)
            .OrderBy(m => Rank(m.Dir))
            .ThenBy(m => m.Dir.Count(c => c == '/'))
            .ThenBy(m => m.Dir, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (apps.Count == 0) return null;
        if (apps.Count > 1)
            warnings.Add($"The build has {apps.Count} applications ({string.Join(", ", apps.Select(a => a.Dir.Length == 0 ? "the root project" : a.Dir))}) and Gitberth runs {Describe(apps[0])}. To run another, switch the Dockerfile to Custom and change the module it builds.");
        return new JvmProject { Root = root, Gradle = gradle, Wrapper = UsesWrapper(root, gradle, warnings), Apps = apps, AllBuildText = text };
    }

    private static string Describe(JvmModule m) => m.Dir.Length == 0 ? "the root project" : m.Dir;

    private static int Rank(string dir)
    {
        var name = dir.Split('/')[^1];
        if (AsideWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return 2;
        return dir.Length == 0 || MainNames.Contains(name) ? 0 : 1;
    }

    private static bool UsesWrapper(string root, bool gradle, List<string> warnings)
    {
        var script = gradle ? "gradlew" : "mvnw";
        if (!File.Exists(Path.Combine(root, script))) return false;
        // gradlew runs gradle-wrapper.jar; mvnw downloads Maven itself and only needs its properties file.
        var needed = gradle ? Path.Combine("gradle", "wrapper", "gradle-wrapper.jar") : Path.Combine(".mvn", "wrapper", "maven-wrapper.properties");
        if (File.Exists(Path.Combine(root, needed))) return true;
        warnings.Add($"{script} is in the repo but {needed.Replace('\\', '/')} isn't, so the build uses {(gradle ? "Gradle" : "Maven")} from its image instead.");
        return false;
    }

    // ---- Maven ----

    private static (List<JvmModule> Modules, string Text) MavenModules(string root, List<string> warnings)
    {
        var modules = new List<JvmModule>();
        var texts = new List<string>();
        var rootPom = MavenPom.Load(Path.Combine(root, "pom.xml"));
        if (rootPom is null)
        {
            warnings.Add("pom.xml isn't valid XML.");
            return (modules, "");
        }
        void Walk(string dir, MavenPom pom, int depth)
        {
            texts.Add(pom.Text);
            modules.Add(ClassifyMaven(dir, pom, rootPom));
            if (depth >= 3) return;
            foreach (var module in pom.Modules)
            {
                if (ProjectAnalyzer.NormalizeDir(dir.Length == 0 ? module : $"{dir}/{module}") is not { Length: > 0 } sub) continue;
                var file = sub.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? sub : $"{sub}/pom.xml";
                if (MavenPom.Load(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))) is { } child)
                    Walk(file.Contains('/') ? file[..file.LastIndexOf('/')] : "", child, depth + 1);
            }
        }
        Walk("", rootPom, 0);
        return (modules, string.Join('\n', texts));
    }

    private static JvmModule ClassifyMaven(string dir, MavenPom pom, MavenPom rootPom)
    {
        // Plugins in the root's <build><plugins> apply to every module too.
        var plugins = new HashSet<string>(pom.Plugins, StringComparer.Ordinal);
        if (!ReferenceEquals(pom, rootPom)) plugins.UnionWith(rootPom.Plugins);
        var m = new JvmModule { Dir = dir, Text = pom.Text, Name = pom.ArtifactId ?? (dir.Length == 0 ? "app" : dir.Split('/')[^1]) };
        m.Kotlin = plugins.Contains("kotlin-maven-plugin");
        if (pom.Packaging is "pom") return m;
        if (pom.Packaging is "war") m.Archive = "war";
        if (plugins.Contains("spring-boot-maven-plugin"))
            (m.Framework, m.Artifact) = ("Spring Boot", JvmArtifact.Jar);
        else if (plugins.Contains("quarkus-maven-plugin"))
            (m.Framework, m.Artifact) = ("Quarkus", JvmArtifact.QuarkusApp);
        else if (plugins.Contains("micronaut-maven-plugin") || pom.ParentArtifactId == "micronaut-parent")
            (m.Framework, m.Artifact) = ("Micronaut", JvmArtifact.Jar);
        else if (plugins.Contains("maven-shade-plugin") || (plugins.Contains("maven-assembly-plugin") && pom.Text.Contains("jar-with-dependencies", StringComparison.Ordinal)))
            (m.Framework, m.Artifact) = (FrameworkFromDependencies(pom.Text), JvmArtifact.Jar);
        else if (pom.ParentArtifactId == "spring-boot-starter-parent")
        {
            // The parent manages spring-boot-maven-plugin, so its repackage goal can run even though the pom doesn't list it.
            (m.Framework, m.Artifact, m.Task) = ("Spring Boot", JvmArtifact.Jar, "spring-boot:repackage");
        }
        return m;
    }

    // ---- Gradle ----

    private static (List<JvmModule> Modules, string Text) GradleModules(string root, string catalogRoot)
    {
        var settings = Read(root, "settings.gradle.kts", "settings.gradle") ?? "";
        var catalog = Read(catalogRoot, Path.Combine("gradle", "libs.versions.toml")) ?? "";
        var aliases = CatalogPlugins(catalog);
        var rootName = RootProjectName().Match(settings) is { Success: true } named ? named.Groups[1].Value : "app";
        var modules = new List<JvmModule>();
        var texts = new List<string> { settings, catalog };
        void Add(string dir, string name)
        {
            var folder = dir.Length == 0 ? root : Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
            var text = Read(folder, "build.gradle.kts", "build.gradle");
            if (text is null) return;
            texts.Add(text);
            modules.Add(ClassifyGradle(dir, name, text, catalog, aliases, folder));
        }
        Add("", rootName);
        foreach (var path in Includes(settings))
            Add(path.Replace(':', '/'), path.Split(':')[^1]);
        return (modules, string.Join('\n', texts));
    }

    private static JvmModule ClassifyGradle(string dir, string name, string text, string catalog, IReadOnlyDictionary<string, string> aliases, string folder)
    {
        var ids = PluginIds(text, aliases);
        var m = new JvmModule
        {
            Dir = dir, Text = text,
            // The application plugin names the install folder and start script after applicationName.
            Name = ApplicationName().Match(text) is { Success: true } app ? app.Groups[1].Value : name,
        };
        m.Kotlin = ids.Contains("org.jetbrains.kotlin.jvm") || Directory.Exists(Path.Combine(folder, "src", "main", "kotlin"));
        var application = ids.Contains("application") || ids.Contains("io.micronaut.application") || ids.Contains("io.ktor.plugin");
        if (ids.Contains("org.springframework.boot"))
        {
            // With the war plugin, Spring Boot's executable archive comes from bootWar.
            var war = ids.Contains("war");
            (m.Framework, m.Artifact, m.Task, m.Archive) = ("Spring Boot", JvmArtifact.Jar, war ? "bootWar" : "bootJar", war ? "war" : "jar");
        }
        else if (ids.Contains("io.quarkus"))
            (m.Framework, m.Artifact, m.Task) = ("Quarkus", JvmArtifact.QuarkusApp, "quarkusBuild");
        else if (application)
            (m.Framework, m.Artifact, m.Task) = (ids.Contains("io.micronaut.application") ? "Micronaut" : FrameworkFromDependencies(text + "\n" + catalog), JvmArtifact.InstallDist, "installDist");
        else if (ids.Any(id => id.EndsWith(".shadow", StringComparison.Ordinal)))
            (m.Framework, m.Artifact, m.Task) = (FrameworkFromDependencies(text + "\n" + catalog), JvmArtifact.Jar, "shadowJar");
        return m;
    }

    /// <summary>
    /// The plugins a Gradle build file applies: plugins { id("…") }, kotlin("jvm"), the bare `application`, apply plugin,
    /// and version catalog aliases (alias(libs.plugins.spring.boot)). Declarations with `apply false` don't count.
    /// </summary>
    internal static HashSet<string> PluginIds(string text, IReadOnlyDictionary<string, string> aliases)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("apply false", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (Match m in PluginId().Matches(line)) ids.Add(m.Groups[1].Value);
            foreach (Match m in KotlinPlugin().Matches(line)) ids.Add("org.jetbrains.kotlin." + m.Groups[1].Value);
            foreach (Match m in CatalogAlias().Matches(line))
                if (aliases.TryGetValue(NormalizeAlias(m.Groups[1].Value), out var id)) ids.Add(id);
            if (line is "application" or "`application`") ids.Add("application");
        }
        return ids;
    }

    /// <summary>The projects settings.gradle includes, as paths like "app" or "services:api".</summary>
    internal static List<string> Includes(string settings)
    {
        var paths = new List<string>();
        // include("a", "b") may span lines; Groovy's include 'a', 'b' has no parentheses.
        foreach (var include in IncludeCall().Matches(settings).Concat(IncludeLine().Matches(settings)))
            foreach (Match quoted in QuotedString().Matches(include.Groups[1].Value))
            {
                var path = quoted.Groups[1].Value.Trim(':');
                if (path.Length > 0 && !paths.Contains(path)) paths.Add(path);
            }
        return paths;
    }

    /// <summary>The [plugins] of gradle/libs.versions.toml, by alias with dots ("spring.boot" → org.springframework.boot).</summary>
    internal static Dictionary<string, string> CatalogPlugins(string toml)
    {
        var plugins = new Dictionary<string, string>(StringComparer.Ordinal);
        var inPlugins = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inPlugins = line == "[plugins]";
                continue;
            }
            if (inPlugins && CatalogPluginLine().Match(line) is { Success: true } m)
                plugins[NormalizeAlias(m.Groups[1].Value)] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }
        return plugins;
    }

    // Gradle turns dashes and underscores in catalog aliases into dots for the libs.plugins accessors.
    private static string NormalizeAlias(string alias) => alias.Replace('-', '.').Replace('_', '.').ToLowerInvariant();

    private static string? FrameworkFromDependencies(string text) =>
        text.Contains("ktor-server", StringComparison.Ordinal) || text.Contains("ktor.server", StringComparison.Ordinal) ? "Ktor"
        : text.Contains("io.javalin", StringComparison.Ordinal) ? "Javalin"
        : text.Contains("io.helidon", StringComparison.Ordinal) ? "Helidon"
        : text.Contains("io.vertx", StringComparison.Ordinal) ? "Vert.x"
        : text.Contains("io.micronaut", StringComparison.Ordinal) ? "Micronaut"
        : null;

    /// <summary>
    /// The folder of the build this folder is a module of, relative to the repo root, or null when it's a build of its own:
    /// a pom.xml whose parent is the pom.xml above it, or a Gradle project that a settings file further up includes.
    /// </summary>
    internal static string? BuildAbove(string repoRoot, string appDir)
    {
        if (appDir.Length == 0) return null;
        var folder = Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        if (MavenPom.Load(Path.Combine(folder, "pom.xml")) is { ParentArtifactId: { } parentId } pom && pom.ParentRelativePath is { Length: > 0 } relative)
        {
            var target = ProjectAnalyzer.NormalizeDir($"{appDir}/{relative.Replace('\\', '/')}");
            if (target is not null)
            {
                var file = target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? target : RepoFiles.Join(target, "pom.xml");
                if (MavenPom.Load(Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar))) is { } parent && parent.ArtifactId == parentId)
                    return file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
            }
        }
        if (new[] { "settings.gradle.kts", "settings.gradle" }.Any(f => File.Exists(Path.Combine(folder, f)))) return null;
        var parts = appDir.Split('/');
        for (var up = parts.Length - 1; up >= 0; up--)
        {
            var above = string.Join('/', parts.Take(up));
            var settings = Read(above.Length == 0 ? repoRoot : Path.Combine(repoRoot, above.Replace('/', Path.DirectorySeparatorChar)), "settings.gradle.kts", "settings.gradle");
            if (settings is null) continue;
            var project = string.Join(':', parts.Skip(up));
            return Includes(settings).Any(p => p.Equals(project, StringComparison.Ordinal)) ? above : null;
        }
        return null;
    }

    internal static string? Read(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            if (RubyApp.Read(Path.Combine(folder, name)) is { } text) return text;
        }
        return null;
    }

    // id("x"), id 'x', apply plugin: 'x', apply(plugin = "x").
    [GeneratedRegex(@"(?:\bid\s*\(?\s*|\bapply\s*\(?\s*plugin\s*[:=]\s*)[""']([A-Za-z0-9_.\-]+)[""']")]
    private static partial Regex PluginId();

    [GeneratedRegex(@"\bkotlin\(\s*""([A-Za-z0-9_.\-]+)""\s*\)")]
    private static partial Regex KotlinPlugin();

    [GeneratedRegex(@"\balias\(\s*libs\.plugins\.([A-Za-z0-9_.]+)\s*\)")]
    private static partial Regex CatalogAlias();

    [GeneratedRegex(@"\binclude(?:Flat)?\s*\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex IncludeCall();

    [GeneratedRegex(@"^\s*include(?:Flat)?\s+(['""].*)$", RegexOptions.Multiline)]
    private static partial Regex IncludeLine();

    [GeneratedRegex(@"[""']([^""']+)[""']")]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"rootProject\.name\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex RootProjectName();

    [GeneratedRegex(@"applicationName\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex ApplicationName();

    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+)\s*=\s*(?:\{[^}]*\bid\s*=\s*""([^""]+)""|""([^"":]+)(?::[^""]*)?"")")]
    private static partial Regex CatalogPluginLine();
}

/// <summary>The parts of a pom.xml Gitberth needs, read without regard to the POM namespace.</summary>
internal sealed class MavenPom
{
    public required string Text { get; init; }
    public string? ArtifactId { get; init; }
    public string? Packaging { get; init; }
    public string? ParentArtifactId { get; init; }

    /// <summary>Where Maven looks for the parent pom: "../pom.xml" unless the pom says otherwise, or "" for none.</summary>
    public string? ParentRelativePath { get; init; }

    public List<string> Modules { get; } = [];

    /// <summary>The artifactIds of the plugins in &lt;build&gt;&lt;plugins&gt; (not pluginManagement).</summary>
    public HashSet<string> Plugins { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>maven-compiler-plugin's release, target and source, from plugins or pluginManagement.</summary>
    public Dictionary<string, string> Compiler { get; } = new(StringComparer.Ordinal);

    public static MavenPom? Load(string path)
    {
        var text = RubyApp.Read(path);
        if (text is null) return null;
        XElement root;
        try
        {
            root = XDocument.Parse(text).Root!;
        }
        catch (XmlException)
        {
            return null;
        }
        var parent = Child(root, "parent");
        var relative = parent is null ? null : Child(parent, "relativePath") is { } r ? r.Value.Trim() : "../pom.xml";
        var pom = new MavenPom
        {
            Text = text,
            ArtifactId = Child(root, "artifactId")?.Value.Trim(),
            Packaging = Child(root, "packaging")?.Value.Trim(),
            ParentArtifactId = parent is null ? null : Child(parent, "artifactId")?.Value.Trim(),
            ParentRelativePath = relative,
        };
        if (Child(root, "modules") is { } modules) pom.Modules.AddRange(modules.Elements().Where(e => e.Name.LocalName == "module").Select(e => e.Value.Trim()));
        if (Child(root, "properties") is { } props)
            foreach (var p in props.Elements()) pom.Properties[p.Name.LocalName] = p.Value.Trim();
        var build = Child(root, "build");
        foreach (var plugin in Children(Child(build, "plugins"), "plugin"))
            if (Child(plugin, "artifactId")?.Value.Trim() is { } id) pom.Plugins.Add(id);
        foreach (var plugin in Children(Child(build, "plugins"), "plugin").Concat(Children(Child(Child(build, "pluginManagement"), "plugins"), "plugin")))
        {
            if (Child(plugin, "artifactId")?.Value.Trim() != "maven-compiler-plugin" || Child(plugin, "configuration") is not { } config) continue;
            foreach (var key in new[] { "release", "target", "source" })
                if (Child(config, key)?.Value.Trim() is { Length: > 0 } value) pom.Compiler.TryAdd(key, value);
        }
        return pom;
    }

    private static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? e, string name) => e?.Elements().Where(x => x.Name.LocalName == name) ?? [];
}

/// <summary>Which Java a JVM build gets.</summary>
internal static partial class JavaVersions
{
    /// <summary>The LTS used when the build doesn't say. 25 is the newer LTS, but older Gradle and Kotlin releases can't run on it.</summary>
    public const int Default = 21;

    public const string DefaultSource = "Gitberth's default";

    /// <summary>Feature releases with current eclipse-temurin and maven images: the LTS ones and the two newest.</summary>
    private static readonly int[] Current = [8, 11, 17, 21, 25, 26, 27];

    /// <summary>
    /// The Java version, where it came from, and whether a Gradle toolchain asks for it (then the build's JDK has to be
    /// exactly that one). Looks at toolchains, Maven's compiler settings, Gradle's compatibility settings, then version files.
    /// </summary>
    public static (int Version, string Source, bool Toolchain) Resolve(JvmProject project)
    {
        var app = project.App;
        if (project.Gradle)
        {
            var conventions = ConventionScripts(project.Root);
            foreach (var (text, where) in new[] { (app.Text, "the build file"), (project.AllBuildText, "the build"), (conventions, "the build's convention plugins") })
            {
                if (Toolchain().Match(text) is { Success: true } t) return (int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), $"Gradle toolchain in {where}", true);
            }
            foreach (var text in new[] { app.Text, project.AllBuildText, conventions })
            {
                if (Compatibility().Match(text) is { Success: true } c && Parse(c.Groups[1].Value) is int v) return (v, "Gradle's Java compatibility setting", false);
            }
        }
        else if (MavenVersion(project) is { } maven)
        {
            return (maven.Version, maven.Source, false);
        }
        if (FromFiles(project.Root) is { } file) return (file.Version, file.Source, false);
        return (Default, DefaultSource, false);
    }

    /// <summary>The image's Java: very old short-term releases move up to the next LTS, which runs what they compiled.</summary>
    public static int ImageVersion(int version) => version switch
    {
        < 8 => 8,
        9 or 10 => 11,
        >= 12 and <= 16 => 17,
        >= 18 and <= 20 => 21,
        >= 22 and <= 24 => 25,
        _ => version,
    };

    public static IEnumerable<string> Warnings(int version)
    {
        var image = ImageVersion(version);
        if (image != version) yield return $"Java {version} is a short-term release that no longer gets updates, so it runs on Java {image}.";
        else if (version > Current[^1]) yield return $"Java {version} is newer than Gitberth knows about; the build assumes eclipse-temurin:{version} images exist.";
    }

    private static (int Version, string Source)? MavenVersion(JvmProject project)
    {
        var root = MavenPom.Load(Path.Combine(project.Root, "pom.xml"));
        var app = project.App.Dir.Length == 0 ? root : MavenPom.Load(Path.Combine(project.Root, project.App.Dir.Replace('/', Path.DirectorySeparatorChar), "pom.xml"));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pom in new[] { root, app }.OfType<MavenPom>())
            foreach (var (k, v) in pom.Properties) properties[k] = v;
        string? Resolve(string? value, int depth = 0)
        {
            if (value is null || depth > 3) return value;
            var m = PropertyReference().Match(value);
            return m.Success ? Resolve(properties.GetValueOrDefault(m.Groups[1].Value), depth + 1) : value;
        }
        foreach (var pom in new[] { app, root }.OfType<MavenPom>().Distinct())
            foreach (var key in new[] { "release", "target", "source" })
                if (Parse(Resolve(pom.Compiler.GetValueOrDefault(key))) is int v) return (v, $"<{key}> of maven-compiler-plugin");
        foreach (var key in new[] { "maven.compiler.release", "java.version", "maven.compiler.target", "maven.compiler.source", "jdk.version", "java.release", "kotlin.compiler.jvmTarget" })
            if (Parse(Resolve(properties.GetValueOrDefault(key))) is int v) return (v, $"<{key}> in pom.xml");
        return null;
    }

    private static (int Version, string Source)? FromFiles(string root)
    {
        if (RubyApp.Read(Path.Combine(root, ".java-version"))?.Trim() is { Length: > 0 } javaVersion && Parse(javaVersion) is int a) return (a, $".java-version: {javaVersion}");
        if (RubyApp.Read(Path.Combine(root, ".sdkmanrc")) is { } sdkman && SdkmanJava().Match(sdkman) is { Success: true } s && Parse(s.Groups[1].Value) is int b)
            return (b, $".sdkmanrc: java={s.Groups[1].Value}");
        if (RubyApp.Read(Path.Combine(root, ".tool-versions")) is { } tools && ToolVersionsJava().Match(tools) is { Success: true } t && Parse(t.Groups[1].Value) is int c)
            return (c, $".tool-versions: java {t.Groups[1].Value}");
        if (RubyApp.Read(Path.Combine(root, "system.properties")) is { } system && SystemProperties().Match(system) is { Success: true } p && Parse(p.Groups[1].Value) is int d)
            return (d, $"system.properties: java.runtime.version={p.Groups[1].Value}");
        return null;
    }

    /// <summary>buildSrc and build-logic convention plugins, where multi-project builds often set the toolchain.</summary>
    private static string ConventionScripts(string root)
    {
        var texts = new List<string>();
        foreach (var dir in new[] { "buildSrc", "build-logic" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.gradle*", SearchOption.AllDirectories).Take(50))
                    if (RubyApp.Read(file) is { } text) texts.Add(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return string.Join('\n', texts);
    }

    /// <summary>"1.8" → 8, "17" → 17, "21.0.2" → 21, "temurin-21.0.2+13" → 21; null for anything else.</summary>
    internal static int? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = JavaNumber().Match(value.Trim());
        if (!m.Success) return null;
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (n == 1 && m.Groups[2].Success) n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return n is >= 5 and <= 99 ? n : null;
    }

    [GeneratedRegex(@"(?:JavaLanguageVersion\.of\(\s*""?|jvmToolchain\(\s*)(\d+)")]
    private static partial Regex Toolchain();

    [GeneratedRegex(@"(?:sourceCompatibility|targetCompatibility|jvmTarget)\s*(?:=|\.set\()\s*(?:JavaVersion\.VERSION_|JvmTarget\.JVM_)?[""']?(1[._]\d+|\d+)")]
    private static partial Regex Compatibility();

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex PropertyReference();

    [GeneratedRegex(@"^java\s*=\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex SdkmanJava();

    [GeneratedRegex(@"^java\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex ToolVersionsJava();

    [GeneratedRegex(@"java\.runtime\.version\s*=\s*(\S+)")]
    private static partial Regex SystemProperties();

    // An optional vendor prefix (temurin-, openjdk-, zulu-), then 1.8-style or plain feature numbers.
    [GeneratedRegex(@"^(?:[A-Za-z]+[-_])*(\d+)(?:[._](\d+))?")]
    private static partial Regex JavaNumber();
}
