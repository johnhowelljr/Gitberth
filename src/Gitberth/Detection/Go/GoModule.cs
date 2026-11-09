using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A Go release by its minor ("1.22.3" and "1.22rc1" are both 1.22): golang images are tagged that way.</summary>
internal readonly record struct GoVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";

    public bool NewerThan(GoVersion other) => Major != other.Major ? Major > other.Major : Minor > other.Minor;
}

internal sealed record GoRequirement(string Path, string Version, bool Indirect);

/// <summary>What Gitberth reads from a go.mod: the module path, the Go version it asks for, its requirements and local replacements.</summary>
internal sealed partial class GoMod
{
    public string Module { get; private set; } = "";

    /// <summary>The go line as written, like "1.22.3".</summary>
    public string? GoLine { get; private set; }

    /// <summary>The toolchain line as written, like "go1.23.4".</summary>
    public string? ToolchainLine { get; private set; }

    public List<GoRequirement> Requirements { get; } = [];

    /// <summary>Folders that stand in for a module (`replace x => ../shared`), as written.</summary>
    public List<string> LocalReplacements { get; } = [];

    /// <summary>The module's last path element without its major-version suffix: github.com/acme/shop/v2 → shop.</summary>
    public string Name
    {
        get
        {
            var parts = Module.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            return parts.Length > 1 && MajorSuffix().IsMatch(parts[^1]) ? parts[^2] : parts[^1];
        }
    }

    public static GoMod Parse(string text)
    {
        var mod = new GoMod();
        string? block = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var indirect = raw.Contains("// indirect", StringComparison.Ordinal);
            var comment = raw.IndexOf("//", StringComparison.Ordinal);
            var line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (line.Length == 0) continue;
            if (block is not null)
            {
                if (line == ")") block = null;
                else mod.Directive(block, line, indirect);
                continue;
            }
            var open = BlockStart().Match(line);
            if (open.Success)
            {
                block = open.Groups[1].Value;
                continue;
            }
            var space = line.IndexOfAny([' ', '\t']);
            if (space > 0) mod.Directive(line[..space], line[(space + 1)..].Trim(), indirect);
        }
        return mod;
    }

    private void Directive(string verb, string rest, bool indirect)
    {
        switch (verb)
        {
            case "module":
                Module = Unquote(rest);
                break;
            case "go":
                GoLine = rest;
                break;
            case "toolchain":
                ToolchainLine = rest;
                break;
            case "require":
                var parts = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) Requirements.Add(new GoRequirement(Unquote(parts[0]), parts[1], indirect));
                break;
            case "replace":
                var arrow = rest.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0) break;
                var target = rest[(arrow + 2)..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } t ? Unquote(t) : "";
                if (target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith("../", StringComparison.Ordinal) || target.StartsWith('/') || target is "." or "..")
                    LocalReplacements.Add(target);
                break;
        }
    }

    /// <summary>Whether a module the go.mod requires is <paramref name="module"/> or one of its major versions (…/v2).</summary>
    public bool Requires(string module) =>
        Requirements.Any(r => r.Path == module || (r.Path.StartsWith(module + "/", StringComparison.Ordinal) && MajorSuffix().IsMatch(r.Path[(module.Length + 1)..])));

    /// <summary>"1.22.3", "1.23rc1" or the toolchain line's "go1.24.0" → its minor; null for "default" or anything else.</summary>
    public static GoVersion? Version(string? text)
    {
        var m = VersionPattern().Match(text?.Trim() ?? "");
        return m.Success ? new GoVersion(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    private static string Unquote(string s) => s.Trim().Trim('"', '`');

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex MajorSuffix();

    [GeneratedRegex(@"^(\w+)\s*\($")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^(?:go)?(\d+)\.(\d+)")]
    private static partial Regex VersionPattern();
}

/// <summary>Which golang image builds a module.</summary>
internal static class GoImages
{
    /// <summary>
    /// The newest Go release Gitberth has checked a golang image for. A go.mod that asks for a newer one starts from this
    /// image, and Go downloads the toolchain itself (GOTOOLCHAIN=auto). Bump it when a new Go release ships.
    /// </summary>
    public static readonly GoVersion Latest = new(1, 27);

    /// <summary>Go fixes security issues only in its two newest releases.</summary>
    public static readonly GoVersion OldestSupported = new(1, 26);

    /// <summary>The go line or, when it's newer, the toolchain line. Neither: the newest release.</summary>
    public static (GoVersion Version, string Source) Resolve(GoMod mod)
    {
        var go = GoMod.Version(mod.GoLine);
        var toolchain = GoMod.Version(mod.ToolchainLine);
        if (toolchain is { } t && (go is null || t.NewerThan(go.Value))) return (t, $"go.mod's toolchain line: {mod.ToolchainLine}");
        if (go is { } g) return (g, $"go.mod's go line: {mod.GoLine}");
        return (Latest, "Gitberth's default; go.mod has no go line");
    }
}
