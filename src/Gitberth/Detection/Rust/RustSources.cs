using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A Rust source file, with its path relative to the app's folder.</summary>
internal sealed record RustFile(string Path, string Text);

/// <param name="Crate">The crate a Cargo.toml depends on.</param>
/// <param name="DefaultPort">Where it listens when the code doesn't say, or null when it always needs an address.</param>
internal sealed record RustFramework(string Crate, string Label, int? DefaultPort);

/// <summary>Where the code says the app listens.</summary>
/// <param name="FromEnv">The port comes from PORT, with this as the fallback.</param>
/// <param name="Localhost">The address only accepts connections from inside the container ("127.0.0.1:8080").</param>
internal sealed record RustPort(int Port, string File, bool FromEnv, string? Localhost);

/// <summary>Reads a crate's sources: where it listens, whether it serves at all, and the variables it reads.</summary>
internal static partial class RustSources
{
    private const int MaxFiles = 2000;
    private const int MaxBytes = 512 * 1024;

    /// <summary>The frameworks Gitberth names, most specific first: Loco and Leptos are built on Axum, for one.</summary>
    public static readonly RustFramework[] Frameworks =
    [
        new("loco-rs", "Loco", 5150),
        new("leptos", "Leptos", null),
        new("rocket", "Rocket", 8000),
        new("actix-web", "Actix Web", null),
        new("axum", "Axum", null),
        new("poem", "Poem", null),
        new("salvo", "Salvo", null),
        new("warp", "Warp", null),
        new("tide", "Tide", null),
        new("tonic", "tonic", null),
        new("hyper", "Hyper", null),
        new("tiny_http", "tiny_http", null),
    ];

    /// <summary>The crates that link a C library at run time, and the Debian package that has it.</summary>
    public static readonly (string Crate, string Package, string Library)[] NativeLibraries =
    [
        ("pq-sys", "libpq5", "libpq"),
        ("libsqlite3-sys", "libsqlite3-0", "SQLite"),
        ("mysqlclient-sys", "libmariadb3", "the MySQL client library"),
    ];

    public static RustFramework? Framework(CargoManifest crate) => Frameworks.FirstOrDefault(f => crate.Dependencies.Contains(f.Crate));

    /// <summary>Whether the crate may serve anything: a framework or a TcpListener. Anything else runs as a worker.</summary>
    public static bool MayServe(CargoManifest crate, IReadOnlyList<RustFile> files) =>
        Framework(crate) is not null || files.Any(f => f.Text.Contains("TcpListener", StringComparison.Ordinal));

    /// <summary>
    /// The binary's own source first (its [[bin]] path, src/main.rs, src/bin/name.rs or src/bin/name/main.rs), then the
    /// crate's other sources. src/bin isn't in RepoFiles' index, so it's read from disk.
    /// </summary>
    public static List<RustFile> Load(RepoFiles files, CargoManifest crate, CargoBin bin)
    {
        var prefix = crate.Dir.Length == 0 ? "" : crate.Dir + "/";
        var declared = RustToml.Tables(RustToml.Get(crate.Document, "bin"))
            .Where(b => RustToml.Str(b, "name") == bin.Name).Select(b => RustToml.Str(b, "path")).OfType<string>().FirstOrDefault();
        var candidates = new[] { declared, bin.Name == crate.Name ? "src/main.rs" : null, $"src/bin/{bin.Name}.rs", $"src/bin/{bin.Name}/main.rs", "src/main.rs" };
        var list = new List<RustFile>();
        var main = candidates.OfType<string>().Select(c => prefix + c.Replace('\\', '/').TrimStart('.', '/')).FirstOrDefault(c => File.Exists(files.Full(c)));
        if (main is not null) list.Add(new RustFile(main, Read(files.Full(main))));
        foreach (var rel in files.WithExtension(".rs").Where(f => f.StartsWith(prefix + "src/", StringComparison.Ordinal) && f != main).Take(MaxFiles))
            list.Add(new RustFile(rel, Read(files.Full(rel))));
        return list;
    }

    /// <summary>Every Rust source of the app for the variable scan, src/bin folders included.</summary>
    public static IEnumerable<RustFile> All(RepoFiles files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in files.WithExtension(".rs").Take(MaxFiles))
            if (seen.Add(rel)) yield return new RustFile(rel, Read(files.Full(rel)));
        foreach (var manifest in files.Files.Where(f => f == "Cargo.toml" || f.EndsWith("/Cargo.toml", StringComparison.Ordinal)))
        {
            var binDir = RepoFiles.Join(manifest[..^"Cargo.toml".Length].TrimEnd('/'), "src/bin");
            if (!Directory.Exists(files.Full(binDir))) continue;
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(files.Full(binDir), "*.rs", SearchOption.AllDirectories).Take(MaxFiles).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var full in found)
            {
                var rel = Path.GetRelativePath(files.Root, full).Replace('\\', '/');
                if (seen.Add(rel)) yield return new RustFile(rel, Read(full));
            }
        }
    }

    /// <summary>
    /// Where the files say the app listens, in order of how sure that is: PORT with a fallback, an address in the code
    /// ("0.0.0.0:3000", ("127.0.0.1", 8080), ([0, 0, 0, 0], 3000), Ipv4Addr::UNSPECIFIED), then a port constant.
    /// </summary>
    public static RustPort? ListenPort(IReadOnlyList<RustFile> files)
    {
        foreach (var file in files)
        {
            if (PortFromEnv().Match(file.Text) is { Success: true } env)
                return new RustPort(Port(env.Groups["port"].Value), file.Path, true, LocalBind(file.Text));
        }
        foreach (var file in files)
        {
            var first = new[] { AddressLiteral(), HostTuple(), IpArray(), IpConstant() }
                .Select(p => p.Match(file.Text)).Where(m => m.Success).OrderBy(m => m.Index).FirstOrDefault();
            if (first is null) continue;
            var port = first.Groups["port"].Value;
            var local = first.Groups["host"].Success && first.Groups["host"].Value is "127.0.0.1" or "localhost" or "[::1]" or "::1" ? $"{first.Groups["host"].Value}:{port}"
                : first.Groups["a"].Success && first.Groups["a"].Value == "127" ? $"127.0.0.1:{port}"
                : first.Groups["ip"].Success && first.Groups["ip"].Value == "LOCALHOST" ? $"localhost:{port}"
                : LocalBind(file.Text);
            return new RustPort(Port(port), file.Path, false, local);
        }
        foreach (var file in files)
        {
            if (PortConstant().Match(file.Text) is { Success: true } constant)
                return new RustPort(Port(constant.Groups["port"].Value), file.Path, false, LocalBind(file.Text));
        }
        return null;
    }

    /// <summary>A localhost address the code binds to, written out or formatted: "127.0.0.1:3000", format!("localhost:{port}").</summary>
    public static string? LocalBind(string text)
    {
        if (LocalAddress().Match(text) is { Success: true } m) return m.Groups["host"].Value;
        if (IpArray().Matches(text).FirstOrDefault(a => a.Groups["a"].Value == "127") is not null) return "127.0.0.1";
        return IpConstant().Matches(text).Any(c => c.Groups["ip"].Value == "LOCALHOST") ? "localhost" : null;
    }

    /// <summary>Rocket.toml's port, from the profile a release build uses (release, then global, then default).</summary>
    public static int? RocketTomlPort(string text)
    {
        var doc = RustToml.Parse(text);
        foreach (var profile in new[] { "release", "global", "default" })
            if (RustToml.Str(doc, profile, "port") is { } port && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    /// <summary>A port set in code on Rocket's figment: .merge(("port", 8080)).</summary>
    public static int? RocketCodePort(IReadOnlyList<RustFile> files) =>
        files.Select(f => RocketMergePort().Match(f.Text)).FirstOrDefault(m => m.Success) is { } m ? Port(m.Groups[1].Value) : null;

    /// <summary>
    /// The environment variables a Rust file reads, with whether it reads them while it compiles (env!, option_env!):
    /// std::env::var, dotenvy::var, and the fields of a struct filled by envy::from_env.
    /// </summary>
    public static IEnumerable<(string Key, bool CompileTime)> EnvKeys(string text)
    {
        foreach (Match m in EnvVarCall().Matches(text)) yield return (m.Groups[1].Value, false);
        foreach (Match m in EnvMacro().Matches(text)) yield return (m.Groups[1].Value, true);
        foreach (Match m in EnvyFromEnv().Matches(text))
        {
            var prefix = m.Groups["prefix"].Value;
            var body = new Regex(@"\bstruct\s+" + Regex.Escape(m.Groups["type"].Value) + @"\s*\{(?<body>[^}]*)\}").Match(text);
            if (!body.Success) continue;
            foreach (Match field in StructField().Matches(body.Groups["body"].Value))
                yield return ((prefix + field.Groups[1].Value).ToUpperInvariant(), false);
        }
    }

    /// <summary>Variables a Loco config (config/*.yaml) reads with Tera's get_env(name="…").</summary>
    public static IEnumerable<string> TeraEnvKeys(string text) => TeraGetEnv().Matches(text).Select(m => m.Groups[1].Value);

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

    private static int Port(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);

    // env::var("PORT").unwrap_or_else(|_| "3000".to_string()), ….ok().and_then(|p| p.parse().ok()).unwrap_or(3000).
    [GeneratedRegex(@"\bvar(?:_os)?\(\s*""PORT""\s*\)[\s\S]{0,200}?(?:""(?<port>\d{2,5})""|\bunwrap_or\(\s*(?<port>\d{2,5})\s*\))")]
    private static partial Regex PortFromEnv();

    [GeneratedRegex(@"""(?<host>0\.0\.0\.0|127\.0\.0\.1|localhost|\[::\]|\[::1\]|::|::1)?:(?<port>\d{2,5})""")]
    private static partial Regex AddressLiteral();

    [GeneratedRegex(@"\(\s*""(?<host>0\.0\.0\.0|127\.0\.0\.1|localhost|::|::1)""\s*,\s*(?<port>\d{2,5})\s*\)")]
    private static partial Regex HostTuple();

    [GeneratedRegex(@"\(\s*\[\s*(?<a>\d{1,3})\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\]\s*,\s*(?<port>\d{2,5})\s*\)")]
    private static partial Regex IpArray();

    [GeneratedRegex(@"\bIpv[46]Addr::(?<ip>LOCALHOST|UNSPECIFIED)\)?\s*,\s*(?<port>\d{2,5})\b")]
    private static partial Regex IpConstant();

    [GeneratedRegex(@"\b(?:const|static|let)\s+(?:mut\s+)?(?i:(?:default_?)?(?:http_?|server_?|listen_?|app_?)?port)\s*(?::\s*u16)?\s*=\s*(?<port>\d{2,5})\b")]
    private static partial Regex PortConstant();

    [GeneratedRegex(@"""(?<host>127\.0\.0\.1|localhost|\[::1\]):(?:\d{2,5}|\{)")]
    private static partial Regex LocalAddress();

    [GeneratedRegex(@"\.merge\(\s*\(\s*""port""\s*,\s*(\d{2,5})")]
    private static partial Regex RocketMergePort();

    [GeneratedRegex(@"\b(?:env|dotenvy?)::var(?:_os)?\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\)")]
    private static partial Regex EnvVarCall();

    [GeneratedRegex(@"\b(?:option_)?env!\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*[,)]")]
    private static partial Regex EnvMacro();

    [GeneratedRegex(@"\benvy::(?:prefixed\(\s*""(?<prefix>[A-Za-z0-9_]*)""\s*\)\s*\.\s*)?from_env::<\s*(?<type>\w+)\s*>")]
    private static partial Regex EnvyFromEnv();

    [GeneratedRegex(@"(?:^|[,{])\s*(?:#\[[^\]]*\]\s*)*(?:pub(?:\([^)]*\))?\s+)?([a-z_][a-z0-9_]*)\s*:", RegexOptions.Multiline)]
    private static partial Regex StructField();

    [GeneratedRegex(@"\bget_env\(\s*name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex TeraGetEnv();
}
