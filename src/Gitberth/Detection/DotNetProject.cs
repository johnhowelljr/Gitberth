using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Gitberth.Detection;

internal enum DotNetKind { Library, Web, Wasm, Worker, Console }

public sealed record TargetFrameworkInfo(string Moniker, string Version, int Major, bool WindowsOnly, bool IsNetFramework);

/// <summary>What Gitberth needs to know about one .csproj/.fsproj.</summary>
internal sealed partial class DotNetProject
{
    private static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit",
    };

    public required string RelativePath { get; init; }
    public required string Sdk { get; init; }
    public string? TargetFrameworkRaw { get; init; }
    public string? AssemblyNameRaw { get; init; }
    public bool IsTest { get; init; }
    public bool IsExe { get; init; }
    public bool IsAspireHost { get; init; }
    public bool UsesAspNetCore { get; init; }
    public bool IsLegacyFramework { get; init; }

    public string Directory => RelativePath.Contains('/') ? RelativePath[..RelativePath.LastIndexOf('/')] : "";
    public string FileStem => Path.GetFileNameWithoutExtension(RelativePath);
    public string AssemblyName =>
        string.IsNullOrWhiteSpace(AssemblyNameRaw) || AssemblyNameRaw.Contains("$(", StringComparison.Ordinal) ? FileStem : AssemblyNameRaw.Trim();
    public bool LooksLikeClient => FileStem.EndsWith(".Client", StringComparison.OrdinalIgnoreCase);

    public DotNetKind Kind
    {
        get
        {
            if (IsTest || IsAspireHost) return DotNetKind.Library;
            if (SdkIs("Microsoft.NET.Sdk.Web")) return DotNetKind.Web;
            if (SdkIs("Microsoft.NET.Sdk.BlazorWebAssembly")) return DotNetKind.Wasm;
            if (SdkIs("Microsoft.NET.Sdk.Worker")) return DotNetKind.Worker;
            if (SdkIs("Microsoft.NET.Sdk") && IsExe) return DotNetKind.Console;
            return DotNetKind.Library;
        }
    }

    private bool SdkIs(string name) => string.Equals(Sdk, name, StringComparison.OrdinalIgnoreCase);

    public static DotNetProject? Load(RepoFiles files, string relativePath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(files.Full(relativePath));
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var root = doc.Root;
        if (root is null) return null;

        string? Prop(string name) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value.Trim();

        var sdks = new List<string>();
        if (root.Attribute("Sdk")?.Value is { } rootSdk) sdks.AddRange(rootSdk.Split(';'));
        sdks.AddRange(root.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value ?? ""));
        sdks.AddRange(root.Descendants().Where(e => e.Name.LocalName == "Import").Select(e => e.Attribute("Sdk")?.Value ?? ""));
        sdks = sdks.Select(s => s.Split('/')[0].Trim()).Where(s => s.Length > 0).ToList();

        var packages = root.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => e.Attribute("Include")?.Value ?? "").ToList();
        var frameworks = root.Descendants().Where(e => e.Name.LocalName == "FrameworkReference").Select(e => e.Attribute("Include")?.Value ?? "").ToList();
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var outputType = Prop("OutputType");

        return new DotNetProject
        {
            RelativePath = relativePath,
            Sdk = sdks.FirstOrDefault(s => s.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)) ?? sdks.FirstOrDefault() ?? "",
            TargetFrameworkRaw = Prop("TargetFramework") ?? Prop("TargetFrameworks"),
            AssemblyNameRaw = Prop("AssemblyName"),
            IsTest = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                || packages.Any(TestPackages.Contains)
                || TestProjectName().IsMatch(stem),
            IsExe = outputType is not null && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)),
            IsAspireHost = string.Equals(Prop("IsAspireHost"), "true", StringComparison.OrdinalIgnoreCase)
                || sdks.Any(s => s.Contains("Aspire", StringComparison.OrdinalIgnoreCase)),
            UsesAspNetCore = frameworks.Any(f => f.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)),
            IsLegacyFramework = sdks.Count == 0 && Prop("TargetFrameworkVersion") is not null,
        };
    }

    /// <summary>The project's target framework, falling back to Directory.Build.props files up the tree.</summary>
    public TargetFrameworkInfo? ResolveTargetFramework(RepoFiles files)
    {
        var raw = TargetFrameworkRaw;
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("$(", StringComparison.Ordinal))
            raw = FindInDirectoryBuildProps(files) ?? raw;
        return string.IsNullOrWhiteSpace(raw) ? null : ParseTargetFrameworks(raw);
    }

    private string? FindInDirectoryBuildProps(RepoFiles files)
    {
        var dir = Directory;
        while (true)
        {
            var candidate = RepoFiles.Join(dir, "Directory.Build.props");
            if (files.Exists(candidate))
            {
                try
                {
                    var value = XDocument.Load(files.Full(candidate)).Descendants()
                        .FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")?.Value.Trim();
                    if (!string.IsNullOrEmpty(value) && !value.Contains("$(", StringComparison.Ordinal)) return value;
                }
                catch (Exception ex) when (ex is XmlException or IOException)
                {
                }
            }
            if (dir.Length == 0) return null;
            dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
        }
    }

    /// <summary>Picks the best of "net10.0;net8.0" etc.: runnable on Linux first, then the newest.</summary>
    internal static TargetFrameworkInfo? ParseTargetFrameworks(string raw)
    {
        TargetFrameworkInfo? best = null;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = part.ToLowerInvariant();
            TargetFrameworkInfo? info = null;
            var modern = ModernTfm().Match(t);
            if (modern.Success)
            {
                var major = int.Parse(modern.Groups[1].Value, CultureInfo.InvariantCulture);
                info = new TargetFrameworkInfo(part, $"{major}.{modern.Groups[2].Value}", major, modern.Groups[3].Value.StartsWith("windows", StringComparison.Ordinal), false);
            }
            else if (CoreTfm().Match(t) is { Success: true } core)
            {
                info = new TargetFrameworkInfo(part, $"{core.Groups[1].Value}.{core.Groups[2].Value}", int.Parse(core.Groups[1].Value, CultureInfo.InvariantCulture), false, false);
            }
            else if (FrameworkTfm().IsMatch(t))
            {
                info = new TargetFrameworkInfo(part, "", 4, false, true);
            }
            if (info is not null && (best is null || Score(info) > Score(best))) best = info;
        }
        return best;

        static int Score(TargetFrameworkInfo i) => (i.IsNetFramework ? 0 : 10_000) + (i.WindowsOnly ? 0 : 1_000) + i.Major;
    }

    [GeneratedRegex(@"[.\-_](Tests?|UnitTests?|IntegrationTests?|Specs?|Benchmarks?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestProjectName();

    [GeneratedRegex(@"^net(\d+)\.(\d+)(?:-([a-z]+)[\d.]*)?$")]
    private static partial Regex ModernTfm();

    [GeneratedRegex(@"^netcoreapp(\d+)\.(\d+)$")]
    private static partial Regex CoreTfm();

    [GeneratedRegex(@"^net\d{2,3}$")]
    private static partial Regex FrameworkTfm();
}
