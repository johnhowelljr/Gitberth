using System.Text.Json;

namespace Gitberth.Detection;

/// <summary>
/// A package.json next to a Ruby, PHP or JVM app that only builds its front-end assets: Rails' jsbundling, Laravel's
/// Vite, a Spring Boot app's React folder. Node goes into the app's own build stage, the way the .NET SDK image gets it,
/// because the asset build often needs the app's language too (assets:precompile, Wayfinder, CSS imported from vendor/).
/// Used by the Ruby, PHP and Java languages.
/// </summary>
/// <param name="Bun">The oven/bun tag whose bun installs and builds the assets, when the front end uses bun.</param>
internal sealed record NodeAssets(PackageManager Pm, int Major, string VersionSource, bool HasBuild, string? Bun = null)
{
    /// <summary>The folder's package.json, or null when there's none or it isn't valid JSON (a warning says so).</summary>
    public static NodeAssets? Detect(string folder, DetectionResult r)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path)) return null;
        PackageJson pkg;
        try
        {
            pkg = PackageJson.Load(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            r.Warnings.Add("package.json isn't valid JSON, so the front-end assets aren't built.");
            return null;
        }
        var (major, source) = NodeVersions.Resolve(folder, pkg.EnginesNode, NodeVersions.DefaultMajor);
        var pm = PackageManager.Detect(folder, pkg);
        return new NodeAssets(pm, major, source, pkg.Scripts.ContainsKey("build"), pm.IsBun ? BunVersions.Tag(pkg) : null);
    }

    /// <summary>Copies Node and npm into a Debian or Ubuntu build stage, plus corepack for pnpm and yarn, or bun's binary for bun.</summary>
    public IEnumerable<string> Setup()
    {
        yield return $"COPY --from=node:{Major}-slim /usr/local/bin/node /usr/local/bin/node";
        yield return $"COPY --from=node:{Major}-slim /usr/local/lib/node_modules /usr/local/lib/node_modules";
        yield return "RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx";
        if (Pm.UsesCorepack)
        {
            yield return "ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0";
            yield return "RUN npm install -g corepack@latest && corepack enable";
        }
        if (Bun is { } tag)
        {
            yield return $"COPY --from=oven/bun:{tag} /usr/local/bin/bun /usr/local/bin/bun";
            yield return "RUN ln -s bun /usr/local/bin/bunx";
        }
    }

    public string Install => $"RUN {Pm.CacheMount}{Pm.InstallCommand}";

    public string Build => $"RUN {Pm.Run("build")}";

    /// <summary>Evidence and warnings about the install, in Gitberth's usual words.</summary>
    public void Describe(DetectionResult r, string what)
    {
        r.Evidence.Add($"package.json builds {what}, with Node {Major} ({VersionSource}); its dependencies install with {Pm.Name}{(Pm.HasLockfile ? " from the lockfile" : "")}");
        r.Warnings.AddRange(NodeVersions.Warnings(Major));
    }
}
