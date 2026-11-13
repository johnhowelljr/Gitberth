using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Docker;

/// <summary>Docker's build cache as <c>docker system df</c> counts it: its size, and how many records it holds.</summary>
/// <param name="Bytes">Every record's size but the ones shared with another (they'd be counted twice).</param>
public sealed record BuildCacheUsage(long Bytes, int Records);

/// <summary>The build cache's calls: how big it is, and clearing it. Builds themselves go through the docker CLI.</summary>
public sealed partial class DockerApi
{
    /// <summary>How long Docker may take to add the build cache up.</summary>
    public static readonly TimeSpan BuildCacheReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long clearing the build cache may take: a big one takes minutes to delete.</summary>
    public static readonly TimeSpan BuildCachePruneTimeout = TimeSpan.FromMinutes(10);

    /// <summary>GET system/df?type=build-cache: the build cache's records (an engine older than API 1.42 answers with everything, and only this is read).</summary>
    public async Task<BuildCacheUsage> BuildCacheAsync(CancellationToken ct = default) =>
        ParseBuildCache(await SendAsync(HttpMethod.Get, "system/df?type=build-cache", timeout: BuildCacheReadTimeout, ct: ct));

    /// <summary>POST build/prune?all=true: every record of the build cache goes, not only the unused ones. Returns the bytes it freed.</summary>
    public async Task<long> PruneBuildCacheAsync(CancellationToken ct = default) =>
        ParsePrune(await SendAsync(HttpMethod.Post, "build/prune?all=true", timeout: BuildCachePruneTimeout, ct: ct));

    /// <summary>The BuildCache list of system/df's answer, added up as <c>docker system df</c> does; none when it has no list.</summary>
    internal static BuildCacheUsage ParseBuildCache(string json)
    {
        var records = JsonNode.Parse(json)?["BuildCache"] as JsonArray ?? [];
        long bytes = 0;
        var count = 0;
        foreach (var record in records.OfType<JsonObject>())
        {
            count++;
            if (record["Shared"] is JsonValue shared && shared.TryGetValue<bool>(out var isShared) && isShared) continue;
            if (record["Size"] is JsonValue size && size.TryGetValue<long>(out var n) && n > 0) bytes += n;
        }
        return new BuildCacheUsage(bytes, count);
    }

    /// <summary>build/prune's SpaceReclaimed; 0 when it doesn't say.</summary>
    internal static long ParsePrune(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["SpaceReclaimed"] is JsonValue freed && freed.TryGetValue<long>(out var n) ? Math.Max(0, n) : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
