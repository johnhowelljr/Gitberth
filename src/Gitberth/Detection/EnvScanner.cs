using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

public static partial class EnvRules
{
    private static readonly string[] BuildTimePrefixes = ["VITE_", "NEXT_PUBLIC_", "REACT_APP_", "PUBLIC_", "NG_APP_"];

    /// <summary>A secret-looking name: the one rule's (<see cref="Security.Masking.IsSecretName"/>).</summary>
    public static bool LooksSecret(string key) => Security.Masking.IsSecretName(key);

    /// <summary>Front-end frameworks bake these into the bundle at build time.</summary>
    public static bool IsBuildTimePrefix(string key) => BuildTimePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    public static bool IsValidKey(string key) => ValidKey().IsMatch(key);

    /// <summary>A URL with a password in it, like postgres://user:password@host/db: the one rule's (<see cref="Security.Masking.HasUrlPassword"/>).</summary>
    public static bool HasCredentials(string value) => Security.Masking.HasUrlPassword(value);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex ValidKey();
}

/// <summary>Minimal .env parser: KEY=value, `export KEY=value`, quotes, # comments.</summary>
public static class DotEnv
{
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\\n", "\n").Replace("\\\"", "\"");
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }
            list.Add(new KeyValuePair<string, string>(key, value));
        }
        return list;
    }
}

/// <summary>Finds environment variables an app probably needs: example .env files, then references in the code.</summary>
internal static partial class EnvScanner
{
    private const int MaxSuggestions = 40;
    private const int MaxFilesToScan = 3000;
    private const int MaxFileBytes = 512 * 1024;

    private static readonly string[] ExampleFiles = [".env.example", ".env.sample", ".env.template", ".env.dist", "example.env", "sample.env", ".env.defaults"];
    private static readonly string[] NodeSourceExtensions = [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".jsx", ".tsx", ".vue", ".svelte", ".astro"];

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "NODE_ENV", "PORT", "HOST", "HOSTNAME", "CI", "TZ", "MODE", "DEV", "PROD", "SSR", "BASE_URL", "NEXT_RUNTIME",
        "VERCEL", "VERCEL_ENV", "VERCEL_URL", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS",
        "AUTH_URL", "NEXTAUTH_URL",
    };

    public static List<EnvSuggestion> Scan(string root, RepoFiles files, ProjectKind kind)
    {
        var list = new List<EnvSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key, string value, string source)
        {
            key = key.Trim();
            // Gitberth sets Vercel's variables, and Auth.js's address, itself: an example value (often localhost) would only override them.
            if (list.Count >= MaxSuggestions || !EnvRules.IsValidKey(key) || IgnoredKeys.Contains(key)
                || key.StartsWith("npm_", StringComparison.Ordinal) || VercelSystemVariable().IsMatch(key) || !seen.Add(key)) return;
            var secret = Security.Masking.IsSecretName(key);
            list.Add(new EnvSuggestion(key, secret ? "" : value, secret, kind.IsNode() && EnvRules.IsBuildTimePrefix(key), source));
        }

        if (kind == ProjectKind.DotNetWeb)
            Add("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true", "default: trust X-Forwarded-* headers from Cloudflare Tunnel or another proxy");

        foreach (var name in ExampleFiles)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            foreach (var (key, value) in DotEnv.Parse(SafeRead(path))) Add(key, value, $"from {name}");
        }

        if (kind.IsNode()) ScanNodeSources(files, Add);
        if (kind.IsDotNet())
        {
            ScanAppSettings(files, Add);
            ScanCSharp(files, Add);
        }
        Languages.For(kind)?.ScanEnv(files, Add);
        // A repo with its own Dockerfile still reads its variables in its language: a Rails app's RAILS_MASTER_KEY, say.
        if (kind == ProjectKind.Dockerfile)
            foreach (var language in Languages.All.Where(l => l.Markers.Any(files.Exists)))
                language.ScanEnv(files, Add);
        return list;
    }

    private static void ScanNodeSources(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var rel in files.WithExtension(NodeSourceExtensions).Take(MaxFilesToScan))
        {
            foreach (Match m in NodeEnvReference().Matches(SafeRead(files.Full(rel))))
                add(m.Groups["key"].Value, "", $"used in {rel}");
        }
    }

    private static void ScanAppSettings(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var rel in files.Files.Where(f => Path.GetFileName(f) is "appsettings.json" or "appsettings.Production.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(SafeRead(files.Full(rel)), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var section in doc.RootElement.EnumerateObject())
                {
                    if (section.Name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase) && section.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var cs in section.Value.EnumerateObject())
                            add($"ConnectionStrings__{cs.Name}", "", $"connection string in {rel}; leave empty to keep its value");
                    }
                    else if (section.Name is not ("Logging" or "AllowedHosts" or "Kestrel"))
                    {
                        Walk(section.Value, section.Name, rel, add, 0);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void Walk(JsonElement element, string path, string source, Action<string, string, string> add, int depth)
    {
        if (depth > 6) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) Walk(p.Value, $"{path}__{p.Name}", source, add, depth + 1);
                break;
            case JsonValueKind.String when IsPlaceholder(element.GetString()):
            case JsonValueKind.Null:
                add(path, "", $"empty in {source}");
                break;
        }
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (value.StartsWith('<') && value.EndsWith('>'))
        || value.Equals("TODO", StringComparison.OrdinalIgnoreCase)
        || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
        || value.Contains("CHANGEME", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("your_", StringComparison.OrdinalIgnoreCase);

    private static void ScanCSharp(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var rel in files.WithExtension(".cs").Take(MaxFilesToScan))
        {
            var text = SafeRead(files.Full(rel));
            if (text.Length == 0) continue;
            foreach (Match m in CsEnvironmentVariable().Matches(text)) add(m.Groups[1].Value, "", $"used in {rel}");
            foreach (Match m in CsConnectionString().Matches(text)) add($"ConnectionStrings__{m.Groups[1].Value}", "", $"used in {rel}; leave empty to keep the appsettings value");
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"(?:process\.env\.|process\.env\[\s*['""]|import\.meta\.env\.|Bun\.env\.|Bun\.env\[\s*['""])(?<key>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex NodeEnvReference();

    // The variables Gitberth sets the way Vercel does, and their framework copies (see Deploy/VercelEnv.cs).
    [GeneratedRegex(@"^(?:NEXT_PUBLIC_|NUXT_ENV_|REACT_APP_|GATSBY_|VITE_|PUBLIC_|VUE_APP_|REDWOOD_ENV_|SANITY_STUDIO_)?VERCEL_(?:ENV|TARGET_ENV|URL|BRANCH_URL|PROJECT_PRODUCTION_URL|GIT_[A-Z_]+)$")]
    private static partial Regex VercelSystemVariable();

    [GeneratedRegex(@"Environment\.GetEnvironmentVariable\(\s*""([^""]+)""")]
    private static partial Regex CsEnvironmentVariable();

    [GeneratedRegex(@"GetConnectionString\(\s*""([^""]+)""")]
    private static partial Regex CsConnectionString();
}
