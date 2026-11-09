using System.Text.Json;

namespace Gitberth.Detection;

/// <summary>The parts of a vercel.json that say how an app is built, so Gitberth can build it the same way.</summary>
internal sealed record VercelConfig(string? InstallCommand, string? BuildCommand, string? OutputDirectory, string? Framework)
{
    /// <summary>The folder's vercel.json, or null when it has none or the file describes another folder (rootDirectory).</summary>
    public static VercelConfig? Load(string folder)
    {
        if (Read(folder) is not { } root) return null;
        if (RootDirectory(root) is { Length: > 0 }) return null;
        return new VercelConfig(Str(root, "installCommand"), Str(root, "buildCommand"), Str(root, "outputDirectory"), Str(root, "framework"));
    }

    /// <summary>The folder that the repo root's vercel.json points at with rootDirectory, like "website".</summary>
    public static string? MainFolder(string repoRoot) => Read(repoRoot) is { } root ? RootDirectory(root) : null;

    internal static JsonElement? Read(string folder)
    {
        var path = Path.Combine(folder, "vercel.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? RootDirectory(JsonElement root) =>
        Str(root, "rootDirectory") is { } dir && NodeScripts.FromPackage("", dir) is { Length: > 0 } normalized ? normalized : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
