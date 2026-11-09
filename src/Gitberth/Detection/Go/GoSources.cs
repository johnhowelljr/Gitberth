using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A Go file that's part of the module's build: not a test, not in a folder Go or Gitberth leaves out.</summary>
internal sealed record GoFile(string Path, string Dir, string Package, string Text, IReadOnlyList<string> Imports)
{
    /// <summary>Whether it imports <paramref name="module"/> or one of its packages.</summary>
    public bool Uses(string module) => Imports.Any(i => i == module || i.StartsWith(module + "/", StringComparison.Ordinal));
}

/// <param name="Module">The module path; its major versions (…/v2) and packages count too.</param>
/// <param name="DefaultPort">Where it listens when the code doesn't say, or null when it always needs an address.</param>
internal sealed record GoFramework(string Module, string Label, int? DefaultPort);

/// <summary>A command-line flag that sets where the app listens, like `flag.String("addr", "localhost:8080", …)`.</summary>
/// <param name="TakesAddress">Its value is host:port rather than a bare port.</param>
internal sealed record GoListenFlag(string Name, bool TakesAddress, int Port, string File);

/// <summary>Where the code says the app listens.</summary>
/// <param name="FromEnv">The port comes from PORT, with this as the fallback.</param>
/// <param name="Localhost">The address it binds to only accepts connections from inside the container ("localhost:8080").</param>
internal sealed record GoPort(int Port, string File, bool FromEnv, string? Localhost);

/// <summary>Reads a Go module's own sources: packages, imports, where it listens and the variables it reads.</summary>
internal static partial class GoSources
{
    private const int MaxFiles = 3000;
    private const int MaxBytes = 512 * 1024;

    // Folders of examples, tests and tooling rather than the app. Go itself skips testdata and names starting with . or _.
    private static readonly HashSet<string> AsideDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "example", "examples", "test", "tests", "e2e", "hack", "scripts", "tools", "docs", "doc", "benchmark", "benchmarks",
        "fixtures", "mocks", "third_party",
    };

    /// <summary>The web frameworks Gitberth names, most specific first: PocketBase and Buffalo are built on others.</summary>
    public static readonly GoFramework[] Frameworks =
    [
        new("github.com/pocketbase/pocketbase", "PocketBase", 8090),
        new("github.com/gobuffalo/buffalo", "Buffalo", 3000),
        new("github.com/beego/beego", "Beego", 8080),
        new("github.com/astaxie/beego", "Beego", 8080),
        new("github.com/gofiber/fiber", "Fiber", 3000),
        new("github.com/gin-gonic/gin", "Gin", 8080),
        new("github.com/labstack/echo", "Echo", null),
        new("github.com/kataras/iris", "Iris", null),
        new("github.com/cloudwego/hertz", "Hertz", 8888),
        new("github.com/go-chi/chi", "Chi", null),
        new("github.com/gorilla/mux", "Gorilla mux", null),
        new("github.com/julienschmidt/httprouter", "httprouter", null),
        new("github.com/valyala/fasthttp", "fasthttp", null),
    ];

    /// <summary>Modules that are C code underneath, so they only build with cgo.</summary>
    public static readonly string[] CgoModules = ["github.com/mattn/go-sqlite3", "github.com/confluentinc/confluent-kafka-go"];

    private static readonly string[] ServerNames = ["server", "api", "web", "app", "http", "httpd", "backend", "service", "serve", "main"];

    private static readonly HashSet<string> ListenFlagNames = new(StringComparer.Ordinal)
    {
        "port", "addr", "address", "listen", "listen-addr", "listen-address", "http", "http-addr", "http-address", "http-port", "bind",
    };

    /// <summary>The module's files that go into a build, leaving out tests, folders Go skips, nested modules and examples or tooling.</summary>
    public static List<GoFile> Load(RepoFiles files)
    {
        var nested = files.Files.Where(f => f.EndsWith("/go.mod", StringComparison.Ordinal)).Select(f => f[..^"go.mod".Length]).ToList();
        var list = new List<GoFile>();
        foreach (var rel in files.WithExtension(".go"))
        {
            if (list.Count >= MaxFiles) break;
            if (rel.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = rel.LastIndexOf('/');
            var dir = slash < 0 ? "" : rel[..slash];
            if (dir.Length > 0 && (dir.Split('/').Any(s => s.StartsWith('.') || s.StartsWith('_') || s == "testdata" || AsideDirs.Contains(s))
                || nested.Any(n => (dir + "/").StartsWith(n, StringComparison.Ordinal)))) continue;
            var text = Read(files.Full(rel));
            var package = PackageClause().Match(text);
            // A `//go:build ignore` file (a generator script, say) is never part of a build.
            if (!package.Success || BuildIgnore().IsMatch(text[..package.Index])) continue;
            list.Add(new GoFile(rel, dir, package.Groups[1].Value, text, ImportsOf(text)));
        }
        return list;
    }

    /// <summary>The folders holding a main package, shallowest first.</summary>
    public static List<string> MainDirs(IEnumerable<GoFile> files) =>
        files.Where(f => f.Package == "main").Select(f => f.Dir).Distinct()
            .OrderBy(d => d.Length == 0 ? 0 : d.Count(c => c == '/') + 1).ThenBy(d => d, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The main package to build: the module root's, else one under cmd/, else any other. Among several, the one named like
    /// the module or the app's folder wins, then one that serves HTTP itself, then a usual server name like server or api.
    /// </summary>
    public static string PickMain(IReadOnlyList<string> mains, string moduleName, string folderName, IReadOnlyList<GoFile> files)
    {
        if (mains.Contains("")) return "";
        var cmd = mains.Where(d => d.StartsWith("cmd/", StringComparison.Ordinal) && d.Count(c => c == '/') == 1).ToList();
        return (cmd.Count > 0 ? cmd : mains)
            .OrderBy(d => Named(d, moduleName) || Named(d, folderName) ? 0 : 1)
            .ThenBy(d => ServesHttp(files.Where(f => f.Dir == d)) ? 0 : 1)
            .ThenBy(d => Array.IndexOf(ServerNames, Last(d).ToLowerInvariant()) is var i and >= 0 ? i : ServerNames.Length)
            .ThenBy(d => d.Count(c => c == '/'))
            .ThenBy(d => d, StringComparer.Ordinal)
            .First();
    }

    public static GoFramework? Framework(IReadOnlyList<GoFile> files) => Frameworks.FirstOrDefault(fw => files.Any(f => f.Uses(fw.Module)));

    /// <summary>Whether these files start a server: a web framework, or a listen call of the standard library.</summary>
    public static bool ServesHttp(IEnumerable<GoFile> files) =>
        files.Any(f => Frameworks.Any(fw => f.Uses(fw.Module)) || ServeCall().IsMatch(f.Text));

    /// <summary>
    /// Whether the app may serve anything at all: it uses net/http, net or a web framework somewhere. Only an app that uses
    /// none of them is taken for a worker, since an app that serves without a port to reach it would be broken.
    /// </summary>
    public static bool MayServe(IReadOnlyList<GoFile> files) =>
        files.Any(f => f.Imports.Any(i => i is "net/http" or "net") || Frameworks.Any(fw => f.Uses(fw.Module)));

    /// <summary>A gRPC server: one that creates a grpc.Server, rather than only calling other services.</summary>
    public static bool ServesGrpc(IReadOnlyList<GoFile> files) =>
        files.Any(f => f.Uses("google.golang.org/grpc") && f.Text.Contains("grpc.NewServer(", StringComparison.Ordinal));

    /// <summary>Why the module needs cgo: its own `import "C"`, or a module that wraps C code. Null when it doesn't.</summary>
    public static string? CgoReason(IReadOnlyList<GoFile> files, GoMod mod)
    {
        if (files.FirstOrDefault(f => f.Imports.Contains("C")) is { } file) return $"{file.Path} imports \"C\"";
        return CgoModules.FirstOrDefault(mod.Requires) is { } module ? $"{module[(module.LastIndexOf('/') + 1)..]} is C code underneath" : null;
    }

    /// <summary>C libraries the module's own cgo code links (#cgo LDFLAGS: -lvips, #cgo pkg-config: vips), which a base image may not have.</summary>
    public static List<string> CgoLibraries(IReadOnlyList<GoFile> files)
    {
        var libs = new List<string>();
        foreach (var file in files.Where(f => f.Imports.Contains("C")))
        {
            foreach (Match m in CgoDirective().Matches(file.Text))
            {
                var names = m.Groups["kind"].Value == "pkg-config"
                    ? m.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(v => !v.StartsWith('-'))
                    : LinkedLibrary().Matches(m.Groups["value"].Value).Select(l => l.Groups[1].Value);
                foreach (var name in names)
                    if (name is not ("m" or "c" or "dl" or "pthread" or "rt") && !libs.Contains(name)) libs.Add(name);
            }
        }
        return libs;
    }

    /// <summary>
    /// A cobra or urfave/cli subcommand that starts the server (`Use: "serve"`), for apps that only print their help
    /// without one. Returns the command and the file that declares it, whose flags belong to it.
    /// </summary>
    public static (string Command, GoFile File)? ServeCommand(IReadOnlyList<GoFile> files)
    {
        foreach (var file in files)
        {
            var cli = file.Uses("github.com/spf13/cobra") ? CobraServe().Match(file.Text)
                : file.Uses("github.com/urfave/cli") ? UrfaveServe().Match(file.Text) : Match.Empty;
            if (cli.Success) return (cli.Groups["cmd"].Value, file);
        }
        return null;
    }

    /// <summary>A flag that sets the listen address or port, with its default, among the given files (flag, pflag or cobra).</summary>
    public static GoListenFlag? ListenFlag(IEnumerable<GoFile> files)
    {
        foreach (var file in files)
        {
            foreach (var m in FlagDeclaration().Matches(file.Text).Concat(ShorthandFlagDeclaration().Matches(file.Text)))
            {
                var name = m.Groups["name"].Value;
                if (!ListenFlagNames.Contains(name)) continue;
                var value = m.Groups["value"].Value;
                if (value.StartsWith('"'))
                {
                    var address = AddressLiteral().Match(value);
                    if (address.Success) return new GoListenFlag(name, true, Port(address.Groups["port"].Value), file.Path);
                    if (BarePort().Match(value) is { Success: true } bare) return new GoListenFlag(name, false, Port(bare.Groups[1].Value), file.Path);
                }
                else if (m.Groups["type"].Value != "String" && value.Length is >= 2 and <= 5)
                {
                    return new GoListenFlag(name, false, Port(value), file.Path);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Where the files say the app listens, in order of how sure that is: PORT with a fallback, a listen call with an
    /// address, then a port-like constant. Null when none of them says.
    /// </summary>
    public static GoPort? ListenPort(IEnumerable<GoFile> files)
    {
        var ordered = files.ToList();
        foreach (var file in ordered)
        {
            if (FirstPort(file.Text, PortFromEnv(), PortFromEnvHelper(), PortFromTag(), PortViperDefault()) is int env)
                return new GoPort(env, file.Path, true, LocalListen(file.Text));
        }
        foreach (var file in ordered)
        {
            foreach (var m in ListenLiteral().Matches(file.Text).Concat(AddrField().Matches(file.Text)).Concat(NetListen().Matches(file.Text)).OrderBy(m => m.Index))
            {
                var host = m.Groups["host"].Value;
                return new GoPort(Port(m.Groups["port"].Value), file.Path, false, IsLocal(host) ? $"{host}:{m.Groups["port"].Value}" : LocalListen(file.Text));
            }
        }
        foreach (var file in ordered)
        {
            if (FirstPort(file.Text, PortConstant(), AddrConstant()) is int constant) return new GoPort(constant, file.Path, false, LocalListen(file.Text));
        }
        return null;
    }

    /// <summary>A listen address that only accepts connections from inside the container, written in a listen call: "localhost:8080", or "localhost" when the port is added in code.</summary>
    public static string? LocalListen(string text) =>
        LocalListenCall().Match(text) is { Success: true } m ? m.Groups["host"].Value + (m.Groups["port"].Success ? ":" + m.Groups["port"].Value : "") : null;

    /// <summary>
    /// What a //go:embed directive asks for that isn't in the repo, like a front end's build output (web/dist), with the
    /// package.json above it that probably builds it. Checked on disk, since RepoFiles leaves dist/ and build/ out.
    /// </summary>
    public static List<(string File, string Path, string? PackageJson)> MissingEmbeds(IReadOnlyList<GoFile> files, RepoFiles repo)
    {
        var missing = new List<(string, string, string?)>();
        foreach (var file in files)
        {
            foreach (Match m in EmbedDirective().Matches(file.Text))
            {
                foreach (Match pattern in EmbedPattern().Matches(m.Groups[1].Value))
                {
                    var raw = pattern.Groups["quoted"].Success ? pattern.Groups["quoted"].Value : pattern.Value;
                    if (raw.StartsWith("all:", StringComparison.Ordinal)) raw = raw[4..];
                    // Up to the first wildcard: that much must exist for the pattern to match anything.
                    var fixedPart = string.Join('/', raw.Split('/').TakeWhile(s => s.IndexOfAny(['*', '?', '[']) < 0));
                    var path = ProjectAnalyzer.NormalizeDir(RepoFiles.Join(file.Dir, fixedPart));
                    if (path is not { Length: > 0 } || File.Exists(repo.Full(path)) || Directory.Exists(repo.Full(path))) continue;
                    if (missing.Any(x => x.Item2 == path)) continue;
                    var parts = path.Split('/');
                    var package = Enumerable.Range(0, parts.Length).Reverse().Select(n => RepoFiles.Join(string.Join('/', parts.Take(n)), "package.json")).FirstOrDefault(repo.Exists);
                    missing.Add((file.Path, path, package));
                }
            }
        }
        return missing;
    }

    /// <summary>Beego's conf/app.conf `httpport = 8080`.</summary>
    public static int? BeegoPort(RepoFiles files)
    {
        if (!files.Exists("conf/app.conf")) return null;
        var m = BeegoHttpPort().Match(Read(files.Full("conf/app.conf")));
        return m.Success ? Port(m.Groups[1].Value) : null;
    }

    /// <summary>The environment variables a Go file reads: os.Getenv and LookupEnv, env helpers, struct tags and viper's BindEnv.</summary>
    public static IEnumerable<string> EnvKeys(string text)
    {
        foreach (Match m in GetenvCall().Matches(text)) yield return m.Groups[1].Value;
        foreach (Match m in EnvHelperCall().Matches(text))
            if (!m.Groups["fn"].Value.StartsWith("Set", StringComparison.OrdinalIgnoreCase) && !m.Groups["fn"].Value.StartsWith("Unset", StringComparison.OrdinalIgnoreCase))
                yield return m.Groups["key"].Value;
        foreach (Match m in EnvStructTag().Matches(text)) yield return m.Groups[1].Value;
        foreach (Match m in ViperBindEnvNamed().Matches(text)) yield return m.Groups[1].Value;
        // Without SetEnvPrefix, viper.BindEnv("db_url") reads DB_URL; with a prefix the name isn't certain enough to suggest.
        if (!text.Contains("SetEnvPrefix(", StringComparison.Ordinal))
            foreach (Match m in ViperBindEnvKey().Matches(text)) yield return m.Groups[1].Value.ToUpperInvariant();
    }

    public static bool IsLocal(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    public static string Last(string dir) => dir.Length == 0 ? "" : dir[(dir.LastIndexOf('/') + 1)..];

    private static bool Named(string dir, string name) => name.Length > 0 && Last(dir).Equals(name, StringComparison.OrdinalIgnoreCase);

    private static int? FirstPort(string text, params Regex[] patterns)
    {
        Match? best = null;
        foreach (var pattern in patterns)
        {
            var m = pattern.Match(text);
            if (m.Success && (best is null || m.Index < best.Index)) best = m;
        }
        return best is null ? null : Port(best.Groups["port"].Value);
    }

    private static int Port(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);

    private static List<string> ImportsOf(string text)
    {
        var imports = new List<string>();
        foreach (Match m in ImportDeclaration().Matches(text))
            foreach (Match path in ImportPath().Matches(m.Groups["body"].Value)) imports.Add(path.Groups[1].Value);
        return imports;
    }

    internal static string Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^\s*package\s+([A-Za-z_]\w*)", RegexOptions.Multiline)]
    internal static partial Regex PackageClause();

    [GeneratedRegex(@"^\s*//\s*(?:go:build|\+build)\s+ignore\b", RegexOptions.Multiline)]
    private static partial Regex BuildIgnore();

    [GeneratedRegex(@"^import\s*(?:\((?<body>[^)]*)\)|(?<body>(?:[\w.]+\s+)?""[^""\n]+""))", RegexOptions.Multiline)]
    private static partial Regex ImportDeclaration();

    [GeneratedRegex(@"""([^""\s]+)""")]
    private static partial Regex ImportPath();

    [GeneratedRegex(@"\b(?:ListenAndServe(?:TLS)?|http\.Serve|net\.Listen|ListenAndServeTLS)\s*\(|\bhttp\.Server\s*\{")]
    private static partial Regex ServeCall();

    [GeneratedRegex(@"^\s*(?://\s*)?#cgo\s+(?:[\w,!]+\s+)?(?<kind>LDFLAGS|pkg-config)\s*:\s*(?<value>.+)$", RegexOptions.Multiline)]
    private static partial Regex CgoDirective();

    [GeneratedRegex(@"(?:^|\s)-l([\w.+-]+)")]
    private static partial Regex LinkedLibrary();

    [GeneratedRegex(@"\bUse\s*:\s*""(?<cmd>serve|server|start|web|run|http|api)\b")]
    private static partial Regex CobraServe();

    [GeneratedRegex(@"\bName\s*:\s*""(?<cmd>serve|server|start|web|run|http)""")]
    private static partial Regex UrfaveServe();

    // flag.String("addr", ":8080", …), flag.IntVar(&port, "port", 8080, …), cmd.Flags().StringVar(&addr, "addr", …).
    [GeneratedRegex(@"(?:\b(?:flag|pflag)|\.(?:Persistent)?Flags\(\))\.(?<type>String|Int|Uint)(?:Var)?\(\s*(?:&[\w.\[\]]+\s*,\s*)?""(?<name>[\w.-]+)""\s*,\s*(?<value>""[^""\n]*""|\d+)")]
    private static partial Regex FlagDeclaration();

    // The pflag forms with a one-letter shorthand: StringP("port", "p", "8080", …), IntVarP(&port, "port", "p", 8080, …).
    [GeneratedRegex(@"(?:\b(?:flag|pflag)|\.(?:Persistent)?Flags\(\))\.(?<type>String|Int|Uint)(?:Var)?P\(\s*(?:&[\w.\[\]]+\s*,\s*)?""(?<name>[\w.-]+)""\s*,\s*""\w?""\s*,\s*(?<value>""[^""\n]*""|\d+)")]
    private static partial Regex ShorthandFlagDeclaration();

    [GeneratedRegex(@"^""(?<host>\[[^\]]*\]|[^"":]*):(?<port>\d{2,5})""$")]
    private static partial Regex AddressLiteral();

    [GeneratedRegex(@"^""(\d{2,5})""$")]
    private static partial Regex BarePort();

    // port := os.Getenv("PORT"); if port == "" { port = "8080" }, or cmp.Or(os.Getenv("PORT"), "8080"). A full address
    // after it ("127.0.0.1:8080") is a listen call that ignores PORT, which ListenLiteral reads.
    [GeneratedRegex(@"\b(?:Getenv|LookupEnv)\(\s*""PORT""\s*\)[\s\S]{0,200}?"":?(?<port>\d{2,5})""")]
    private static partial Regex PortFromEnv();

    // getEnv("PORT", "8080"), envOr("PORT", ":8080").
    [GeneratedRegex(@"\b\w*[Ee]nv\w*\(\s*""PORT""\s*,\s*""(?:[\w.]*:)?(?<port>\d{2,5})""")]
    private static partial Regex PortFromEnvHelper();

    // Port int `env:"PORT" envDefault:"8080"`, `envconfig:"PORT" default:"8080"`, `env:"PORT, default=8080"`.
    [GeneratedRegex(@"\benv(?:config)?:""PORT(?:""[^`\n]*?\b(?:envDefault|default):""(?:[\w.]*:)?(?<port>\d{2,5})""|\s*,[^""]*?\bdefault=:?(?<port>\d{2,5}))")]
    private static partial Regex PortFromTag();

    [GeneratedRegex(@"\bSetDefault\(\s*""(?:port|PORT|http\.port|server\.port|http_port)""\s*,\s*""?:?(?<port>\d{2,5})")]
    private static partial Regex PortViperDefault();

    // http.ListenAndServe(":8080", …), r.Run(":8080"), e.Start("0.0.0.0:1323"), app.Listen(":3000"), server.WithHostPorts(":8888").
    [GeneratedRegex(@"(?:\bListenAndServe(?:TLS)?|\.Listen|\.Run|\.RunTLS|\.Start|\.StartTLS|\bWithHostPorts)\(\s*""(?<host>\[[^\]]*\]|[^"":\n]*):(?<port>\d{2,5})""")]
    private static partial Regex ListenLiteral();

    [GeneratedRegex(@"\bAddr\s*[:=]\s*""(?<host>\[[^\]]*\]|[^"":\n]*):(?<port>\d{2,5})""")]
    private static partial Regex AddrField();

    [GeneratedRegex(@"\bnet\.Listen\(\s*""tcp[46]?""\s*,\s*""(?<host>\[[^\]]*\]|[^"":\n]*):(?<port>\d{2,5})""")]
    private static partial Regex NetListen();

    [GeneratedRegex(@"\b(?i:(?:default)?(?:http|server|listen|web|app)?port)\s*(?::=|=)\s*""?(?<port>\d{2,5})\b")]
    private static partial Regex PortConstant();

    [GeneratedRegex(@"\b(?i:(?:default)?(?:http|server|listen|web|app)?(?:addr|address))\s*(?::=|=)\s*""(?:\[[^\]]*\]|[^"":\n]*):(?<port>\d{2,5})""")]
    private static partial Regex AddrConstant();

    // A listen call given localhost, written out or built: "localhost:8080", "127.0.0.1:"+port, fmt.Sprintf("localhost:%d", port).
    [GeneratedRegex(@"(?:\bListenAndServe(?:TLS)?\(|\.Listen\(|\.Run\(|\.Start\(|\bAddr\s*[:=]|\bnet\.Listen\(\s*""tcp[46]?""\s*,)\s*(?:fmt\.Sprintf\(\s*)?""(?<host>localhost|127\.0\.0\.1|\[::1\]):(?:(?<port>\d{2,5})"")?")]
    private static partial Regex LocalListenCall();

    [GeneratedRegex(@"^\s*//go:embed\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex EmbedDirective();

    // Patterns are separated by spaces, and quoted ("…" or `…`) when they hold one.
    [GeneratedRegex(@"""(?<quoted>[^""]+)""|`(?<quoted>[^`]+)`|[^\s""`]+")]
    private static partial Regex EmbedPattern();

    [GeneratedRegex(@"^\s*httpport\s*=\s*(\d{2,5})", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BeegoHttpPort();

    [GeneratedRegex(@"\b(?:os|syscall)\.(?:Getenv|LookupEnv)\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\)")]
    private static partial Regex GetenvCall();

    // Helpers like getEnv("DATABASE_URL", "…") or mustEnv("API_KEY"); upper-case names only, so map keys don't count.
    [GeneratedRegex(@"\b(?<fn>\w*[Ee]nv\w*)\(\s*""(?<key>[A-Z][A-Z0-9_]*)""\s*[,)]")]
    private static partial Regex EnvHelperCall();

    [GeneratedRegex(@"\benv(?:config)?:""([A-Za-z_][A-Za-z0-9_]*)[,""]")]
    private static partial Regex EnvStructTag();

    [GeneratedRegex(@"\.BindEnv\(\s*""[^""]*""\s*,\s*""([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex ViperBindEnvNamed();

    [GeneratedRegex(@"\.BindEnv\(\s*""([A-Za-z][A-Za-z0-9_]*)""\s*\)")]
    private static partial Regex ViperBindEnvKey();
}
