using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Deploy;

/// <summary>What clearing the build cache did, as Settings › Deploys says it.</summary>
/// <param name="Freed">The bytes Docker freed; 0 when it was refused or failed.</param>
public sealed record BuildCacheCleared(bool Ok, string Message, long Freed = 0);

/// <summary>
/// Docker's build cache, for Settings › Deploys: how big it is, and clearing it. Clearing is refused while a build runs,
/// and a build that gets to its turn meanwhile waits for it, so no build ever loses its cache halfway through.
/// </summary>
public sealed class BuildCache(DockerApi api, DeployPipeline pipeline, ILogger<BuildCache> logger)
{
    /// <summary>The cache's size as Docker adds it up.</summary>
    public Task<BuildCacheUsage> UsageAsync(CancellationToken ct = default) => api.BuildCacheAsync(ct);

    /// <summary>Every record of the build cache goes: the next build of every app runs each step again.</summary>
    public async Task<BuildCacheCleared> ClearAsync(CancellationToken ct = default)
    {
        using var hold = pipeline.TryHoldBuilds();
        if (hold is null) return new BuildCacheCleared(false, "A build is running, so the cache wasn't cleared. Clear it once the build is done.");
        try
        {
            var freed = await api.PruneBuildCacheAsync(ct);
            logger.LogInformation("Cleared Docker's build cache: {Bytes} bytes freed", freed);
            return new BuildCacheCleared(true, ClearedText(freed), freed);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            logger.LogWarning(ex, "Clearing Docker's build cache failed");
            return new BuildCacheCleared(false, $"Docker didn't clear the build cache: {ex.Message.TrimEnd('.')}.");
        }
    }

    /// <summary>What clearing freed, and what it means for the next builds.</summary>
    internal static string ClearedText(long freed) => freed > 0
        ? $"Cleared the build cache: {Format.Memory(freed)} freed. The next builds take longer, since every step runs again."
        : "Cleared the build cache: it held nothing to free. The next builds take longer, since every step runs again.";
}
