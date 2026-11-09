using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>What Gitberth reads from a Ruby app: its Gemfile, Gemfile.lock and Procfile.</summary>
internal sealed partial class RubyApp
{
    private static readonly HashSet<string> DevGroups = new(StringComparer.Ordinal) { "development", "test" };

    public required string Folder { get; init; }
    public required string Gemfile { get; init; }
    public string? Lockfile { get; init; }

    /// <summary>Gems the Gemfile lists outside groups that only hold development and test.</summary>
    public HashSet<string> Gems { get; } = new(StringComparer.Ordinal);

    /// <summary>Gems the Gemfile lists only for development or test, which the image leaves out.</summary>
    public HashSet<string> DevGems { get; } = new(StringComparer.Ordinal);

    /// <summary>What the Gemfile asks of each gem ("~> 8.1", "require: false"), as written.</summary>
    public Dictionary<string, string> Requirements { get; } = new(StringComparer.Ordinal);

    /// <summary>Every gem in Gemfile.lock, dependencies included, with its version.</summary>
    public Dictionary<string, string> Locked { get; } = new(StringComparer.Ordinal);

    /// <summary>Locked gems that come as a precompiled build for Linux, like nokogiri (1.18.10-x86_64-linux-gnu).</summary>
    public HashSet<string> LinuxBuilds { get; } = new(StringComparer.Ordinal);

    /// <summary>Gemfile.lock's PLATFORMS.</summary>
    public List<string> Platforms { get; } = [];

    /// <summary>The Gemfile's `ruby "3.4.1"` or `ruby "~> 3.3"` requirements.</summary>
    public List<string> RubyRequirements { get; } = [];

    /// <summary>The file the Gemfile takes its Ruby version from (`ruby file: ".ruby-version"`).</summary>
    public string? RubyFile { get; private set; }

    /// <summary>Gemfile.lock's RUBY VERSION, like 3.4.1.</summary>
    public string? LockedRuby { get; private set; }

    /// <summary>The app's Procfile processes (web, worker, release), or empty.</summary>
    public Dictionary<string, string> Procfile { get; } = new(StringComparer.Ordinal);

    /// <summary>The gem is installed in the image: listed for production in the Gemfile, or locked and not dev-only.</summary>
    public bool InBundle(string gem) => Gems.Contains(gem) || (Locked.ContainsKey(gem) && !DevGems.Contains(gem));

    /// <summary>Gemfile.lock lists no platform Linux installs from, so Bundler would refuse to install from it there.</summary>
    public bool LacksLinuxPlatform =>
        Lockfile is not null && Platforms.Count > 0
        && !Platforms.Any(p => p == "ruby" || p.StartsWith("x86_64-linux", StringComparison.Ordinal) || p.StartsWith("aarch64-linux", StringComparison.Ordinal));

    /// <summary>nokogiri compiles from source (a lockfile without a precompiled Linux build), which needs a few more headers.</summary>
    public bool NokogiriFromSource => Lockfile is not null && Locked.ContainsKey("nokogiri") && !LinuxBuilds.Contains("nokogiri") && !LacksLinuxPlatform;

    /// <summary>The major and minor version of a gem, from Gemfile.lock or else the Gemfile's requirement; null when neither says.</summary>
    public (int Major, int Minor)? VersionOf(string gem)
    {
        var text = Locked.GetValueOrDefault(gem) ?? Requirements.GetValueOrDefault(gem);
        var m = text is null ? Match.Empty : MajorMinor().Match(text);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>A short hash of Gemfile.lock (or the Gemfile without one), for a gem cache that holds exactly this bundle.</summary>
    public string BundleHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Lockfile ?? Gemfile)))[..12].ToLowerInvariant();

    /// <summary>The Ruby app in this folder, or null when it has no Gemfile.</summary>
    public static RubyApp? Load(string folder)
    {
        var gemfile = Read(Path.Combine(folder, "Gemfile"));
        if (gemfile is null) return null;
        var app = new RubyApp { Folder = folder, Gemfile = gemfile, Lockfile = Read(Path.Combine(folder, "Gemfile.lock")) };
        app.ReadGemfile(gemfile);
        if (app.Lockfile is not null) app.ReadLockfile(app.Lockfile);
        foreach (var raw in (Read(Path.Combine(folder, "Procfile")) ?? "").Split('\n'))
        {
            var m = ProcfileLine().Match(raw.Trim());
            if (m.Success) app.Procfile.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
        }
        return app;
    }

    /// <summary>
    /// Gem lines, with the groups they're in: `group :development, :test do` blocks and `group:` options. Other blocks
    /// (platforms, source, if) keep the group of the block around them.
    /// </summary>
    private void ReadGemfile(string text)
    {
        var devBlocks = new Stack<bool>();
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (EndLine().IsMatch(line))
            {
                if (devBlocks.Count > 0) devBlocks.Pop();
                continue;
            }
            if (GroupBlock().Match(line) is { Success: true } group)
            {
                devBlocks.Push(IsDevOnly(group.Groups[1].Value));
                continue;
            }
            if (GemLine().Match(line) is { Success: true } gem)
            {
                var name = gem.Groups[1].Value;
                var rest = gem.Groups[2].Value;
                var inline = InlineGroups().Match(rest);
                var dev = inline.Success ? IsDevOnly(inline.Groups[1].Value) : devBlocks.Count > 0 && devBlocks.Peek();
                if (dev) DevGems.Add(name);
                else Gems.Add(name);
                Requirements.TryAdd(name, rest);
                continue;
            }
            if (RubyFileLine().Match(line) is { Success: true } file)
            {
                RubyFile = file.Groups[1].Value;
                continue;
            }
            if (RubyLine().Match(line) is { Success: true } ruby)
            {
                if (ruby.Groups[1].Value.Contains("ruby-version", StringComparison.Ordinal)) RubyFile = ".ruby-version";
                else RubyRequirements.AddRange(Quoted().Matches(ruby.Groups[1].Value).Select(q => q.Groups[1].Value));
                continue;
            }
            if (BlockStart().IsMatch(line)) devBlocks.Push(devBlocks.Count > 0 && devBlocks.Peek());
        }
    }

    private void ReadLockfile(string text)
    {
        var section = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0) continue;
            if (!char.IsWhiteSpace(raw[0]))
            {
                section = raw.Trim();
                continue;
            }
            switch (section)
            {
                case "GEM" or "GIT" or "PATH" when LockSpec().Match(raw) is { Success: true } spec:
                    var name = spec.Groups[1].Value;
                    var version = spec.Groups[2].Value;
                    // Versions never hold a dash; what follows one is the platform of a precompiled build.
                    var dash = version.IndexOf('-');
                    Locked.TryAdd(name, dash < 0 ? version : version[..dash]);
                    if (dash >= 0 && version[(dash + 1)..] is var platform
                        && (platform.StartsWith("x86_64-linux", StringComparison.Ordinal) || platform.StartsWith("aarch64-linux", StringComparison.Ordinal)))
                        LinuxBuilds.Add(name);
                    break;
                case "PLATFORMS":
                    Platforms.Add(raw.Trim());
                    break;
                case "RUBY VERSION" when LockedRubyLine().Match(raw) is { Success: true } ruby:
                    LockedRuby = ruby.Groups[1].Value;
                    break;
            }
        }
    }

    private static bool IsDevOnly(string groups)
    {
        var names = GroupName().Matches(groups).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        return names.Count > 0 && names.All(DevGroups.Contains);
    }

    /// <summary>A Ruby line without its comment; a # inside quotes stays.</summary>
    internal static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null && c == '#') return line[..i];
            if (c is '"' or '\'') quote = quote == c ? null : quote ?? c;
        }
        return line;
    }

    internal static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 512 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^end\b")]
    private static partial Regex EndLine();

    [GeneratedRegex(@"^group\s*\(?\s*(.+?)\s*\)?\s+do\b")]
    private static partial Regex GroupBlock();

    // A block opened by a keyword, or by `do` at the end of the line (platforms :mri do, source "..." do |x|).
    [GeneratedRegex(@"^(?:if|unless|case|while|until|begin)\b|\bdo(?:\s*\|[^|]*\|)?$")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^gem\s*\(?\s*[""']([A-Za-z0-9_.\-]+)[""']\s*(.*)$")]
    private static partial Regex GemLine();

    [GeneratedRegex(@"(?:\bgroups?:|:groups?\s*=>)\s*(\[[^\]]*\]|:\w+|[""']\w+[""'])")]
    private static partial Regex InlineGroups();

    [GeneratedRegex(@":(\w+)|[""'](\w+)[""']")]
    private static partial Regex GroupName();

    [GeneratedRegex(@"^ruby\s+file:\s*[""']([^""']+)[""']")]
    private static partial Regex RubyFileLine();

    [GeneratedRegex(@"^ruby(?:\s+|\s*\()(.+)$")]
    private static partial Regex RubyLine();

    [GeneratedRegex(@"[""']([^""']+)[""']")]
    private static partial Regex Quoted();

    // Exactly four spaces: a gem; six would be one of its dependencies.
    [GeneratedRegex(@"^ {4}([A-Za-z0-9_.\-]+) \(([^)\s]+)\)\s*$")]
    private static partial Regex LockSpec();

    [GeneratedRegex(@"ruby (\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex LockedRubyLine();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*:\s*(.+)$")]
    private static partial Regex ProcfileLine();

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex MajorMinor();
}

/// <summary>Which ruby image an app gets.</summary>
internal static partial class RubyVersions
{
    /// <summary>Used when the app doesn't say. Ruby 4.0 came out in December 2025; bump this once your apps run on it.</summary>
    public const string Default = "3.4";

    public const string DefaultSource = "Gitberth's default";

    /// <summary>Minor versions with ruby images, newest first.</summary>
    private static readonly string[] Known = ["4.0", "3.4", "3.3", "3.2", "3.1", "3.0", "2.7", "2.6"];

    /// <summary>
    /// The image tag ("3.4", or "3.4.1" when Bundler insists on that exact version) and where it came from: the Gemfile's
    /// `ruby` line, the file it names, .ruby-version, .tool-versions, Gemfile.lock's RUBY VERSION, then the default.
    /// </summary>
    public static (string Tag, string Source) Resolve(RubyApp app)
    {
        // Bundler refuses to run when the Ruby version isn't exactly the one the Gemfile pins, so a pin is followed exactly.
        if (app.RubyRequirements.Count == 1 && ExactVersion().IsMatch(app.RubyRequirements[0]))
            return (app.RubyRequirements[0], $"Gemfile: ruby \"{app.RubyRequirements[0]}\"");
        if (app.RubyRequirements.Count > 0)
        {
            var spec = string.Join(", ", app.RubyRequirements);
            if (FromRequirements(app.RubyRequirements) is { } fits) return (fits, $"Gemfile: ruby \"{spec}\"");
        }
        if (app.RubyFile is { } file && FromFile(Path.Combine(app.Folder, file)) is { } pinned)
            return (pinned, $"{file}: {pinned}");
        if (FromFile(Path.Combine(app.Folder, ".ruby-version")) is { } version)
            return (MinorOf(version), $".ruby-version: {version}");
        if (RubyApp.Read(Path.Combine(app.Folder, ".tool-versions")) is { } tools && ToolVersion().Match(tools) is { Success: true } tool)
            return (MinorOf(tool.Groups[1].Value), $".tool-versions: ruby {tool.Groups[1].Value}");
        if (app.LockedRuby is { } locked) return (MinorOf(locked), $"Gemfile.lock: ruby {locked}");
        return (Default, DefaultSource);
    }

    /// <summary>The default when it meets every requirement, else the newest known version that does.</summary>
    internal static string? FromRequirements(IReadOnlyList<string> requirements)
    {
        foreach (var candidate in Known.Prepend(Default).Distinct())
        {
            if (requirements.All(r => Satisfies(candidate, r))) return candidate;
        }
        return requirements.Select(r => VersionPart().Match(r)).FirstOrDefault(m => m.Success) is { } first ? MinorOf(first.Value) : null;
    }

    /// <summary>Whether a minor version ("3.4", meaning its newest patch) meets a RubyGems requirement like "~> 3.2" or ">= 3.1.0".</summary>
    internal static bool Satisfies(string minor, string requirement)
    {
        var m = Requirement().Match(requirement.Trim());
        if (!m.Success) return false;
        var candidate = Parse(minor);
        var wanted = m.Groups["version"].Value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var floor = (wanted[0], wanted.Length > 1 ? wanted[1] : 0);
        return m.Groups["op"].Value switch
        {
            // ~> 3.2 allows 3.x from 3.2; ~> 3.2.0 allows 3.2.x only.
            "~>" => wanted.Length >= 3 ? candidate == floor : candidate.CompareTo(floor) >= 0 && candidate.Major == wanted[0],
            ">=" or ">" => candidate.CompareTo(floor) >= 0,
            "<" => candidate.CompareTo(floor) < 0,
            "<=" => candidate.CompareTo(floor) <= 0,
            "!=" => true,
            _ => candidate == floor,
        };
    }

    public static IEnumerable<string> Warnings(string tag)
    {
        var (major, minor) = Parse(tag);
        if (major < 3 || (major == 3 && minor < 3)) yield return $"Ruby {major}.{minor} is past end-of-life. Consider moving to Ruby {Default}.";
    }

    internal static string MinorOf(string version)
    {
        var (major, minor) = Parse(version);
        return $"{major}.{minor}";
    }

    private static (int Major, int Minor) Parse(string version)
    {
        var m = VersionPart().Match(version);
        if (!m.Success) return (0, 0);
        var parts = m.Value.Split('.');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>A version file's content ("3.4.1", "ruby-3.4.1", "3.4"); null for other Rubies such as JRuby.</summary>
    private static string? FromFile(string path)
    {
        var text = RubyApp.Read(path)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var first = text.Split('\n')[0].Trim();
        if (first.StartsWith("ruby-", StringComparison.Ordinal)) first = first[5..];
        return VersionFile().IsMatch(first) ? first : null;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactVersion();

    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+)?(?:-?[a-z]+\d*)?$")]
    private static partial Regex VersionFile();

    [GeneratedRegex(@"^ruby\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.Multiline)]
    private static partial Regex ToolVersion();

    [GeneratedRegex(@"^(?<op>~>|>=|<=|!=|>|<|=)?\s*(?<version>\d+(?:\.\d+){0,2})")]
    private static partial Regex Requirement();

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionPart();
}
