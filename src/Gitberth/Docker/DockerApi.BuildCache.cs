using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Docker;

/// <summary>Docker's build cache as <c>docker system df</c> counts it: its size, and how many records it holds.</summary>
/// <param name="Bytes">Every record's size but the ones shared with another (they'd be counted twice).</param>
public sealed record BuildCacheUsage(long Bytes, int Records);

/// <summary>One record of the build cache, as system/df lists it.</summary>
/// <param name="Shared">Shared with another record or an image: removing it frees nothing.</param>
/// <param name="InUse">A build holds it now: BuildKit never prunes it.</param>
/// <param name="LastUsedAt">When a build last used it; null when none did since it was made.</param>
public sealed record BuildCacheRecord(string Id, long Size, bool Shared, bool InUse, DateTime? CreatedAt, DateTime? LastUsedAt)
{
    /// <summary>When a build last had it: its last use, else when it was made.</summary>
    public DateTime? Touched => LastUsedAt ?? CreatedAt;
}

/// <summary>The build cache's calls: how big it is, and clearing it. Builds themselves go through the docker CLI.</summary>
public sealed partial class DockerApi
{
    /// <summary>How long Docker may take to add the build cache up.</summary>
    public static readonly TimeSpan BuildCacheReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long clearing the build cache may take: a big one takes minutes to delete.</summary>
    public static readonly TimeSpan BuildCachePruneTimeout = TimeSpan.FromMinutes(10);

    /// <summary>From this API version on, system/df answers only for the type asked, so reading the build cache or the images is cheap.</summary>
    public static readonly Version DiskUsageByType = new(1, 42);

    /// <summary>GET system/df?type=build-cache: the build cache's records (an engine older than API 1.42 answers with everything, and only this is read).</summary>
    public async Task<BuildCacheUsage> BuildCacheAsync(CancellationToken ct = default) =>
        ParseBuildCache(await SendAsync(HttpMethod.Get, "system/df?type=build-cache", timeout: BuildCacheReadTimeout, ct: ct));

    /// <summary>GET system/df?type=build-cache, each record as it is: its size, whether it's shared or in use, and when a build last used it.</summary>
    public async Task<IReadOnlyList<BuildCacheRecord>> BuildCacheRecordsAsync(CancellationToken ct = default) =>
        ParseBuildCacheRecords(await SendAsync(HttpMethod.Get, "system/df?type=build-cache", timeout: BuildCacheReadTimeout, ct: ct));

    /// <summary>POST build/prune?all=true: every record of the build cache goes, not only the unused ones. Returns the bytes it freed.</summary>
    public async Task<long> PruneBuildCacheAsync(CancellationToken ct = default) =>
        ParsePrune(await SendAsync(HttpMethod.Post, "build/prune?all=true", timeout: BuildCachePruneTimeout, ct: ct));

    /// <summary>
    /// POST build/prune, never with all: the records no build used for <paramref name="unusedFor"/> go (its until filter, which
    /// every engine since API 1.39 reads the same way), or, with none, every record no build uses now. BuildKit keeps the
    /// records in use, its own internal ones and those shared with images, which free nothing anyway. Returns the bytes it freed.
    /// </summary>
    public async Task<long> PruneUnusedBuildCacheAsync(TimeSpan? unusedFor, CancellationToken ct = default) =>
        ParsePrune(await SendAsync(HttpMethod.Post, PrunePath(unusedFor), timeout: BuildCachePruneTimeout, ct: ct));

    /// <summary>build/prune's path: with the until filter ({"until":["168h"]}, URL-encoded) when there's an age.</summary>
    internal static string PrunePath(TimeSpan? unusedFor) => unusedFor is { } age
        ? "build/prune?filters=" + Uri.EscapeDataString($"{{\"until\":[\"{Hours(age)}h\"]}}")
        : "build/prune";

    /// <summary>GET system/df?type=image: what every image on the host takes (their layers, each counted once); null on an engine older than API 1.42.</summary>
    public async Task<long?> ImagesSizeAsync(CancellationToken ct = default)
    {
        // Older engines add volumes up too when asked, which takes long on a big host: not worth it for one figure.
        if (await ApiVersionAsync(ct) < DiskUsageByType) return null;
        var answer = JsonNode.Parse(await SendAsync(HttpMethod.Get, "system/df?type=image", timeout: BuildCacheReadTimeout, ct: ct));
        return answer?["LayersSize"] is JsonValue size && size.TryGetValue<long>(out var n) ? Math.Max(0, n) : null;
    }

    /// <summary>
    /// GET system/df?type=volume: what one volume takes (gitberth-buildkit's, Railpack's build cache); null when it isn't
    /// there or the engine doesn't say. The engine walks every volume to size it, which takes a second on a busy host.
    /// </summary>
    public async Task<long?> VolumeSizeAsync(string name, CancellationToken ct = default)
    {
        if (await ApiVersionAsync(ct) < DiskUsageByType) return null;
        return ParseVolumeSize(await SendAsync(HttpMethod.Get, "system/df?type=volume", timeout: BuildCacheReadTimeout, ct: ct), name);
    }

    /// <summary>A volume's UsageData.Size in system/df's answer; null when it isn't listed or isn't sized (-1).</summary>
    internal static long? ParseVolumeSize(string json, string name)
    {
        try
        {
            foreach (var volume in JsonNode.Parse(json)?["Volumes"] as JsonArray ?? [])
                if (volume?["Name"] is JsonValue n && n.TryGetValue<string>(out var named) && named == name)
                    return volume["UsageData"]?["Size"] is JsonValue size && size.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : null;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>The BuildCache list of system/df's answer, added up as <c>docker system df</c> does; none when it has no list.</summary>
    internal static BuildCacheUsage ParseBuildCache(string json) => Usage(ParseBuildCacheRecords(json));

    /// <summary>The records, added up as <c>docker system df</c> does: the shared ones aren't counted.</summary>
    public static BuildCacheUsage Usage(IReadOnlyList<BuildCacheRecord> records) =>
        new(records.Where(r => !r.Shared).Sum(r => Math.Max(0, r.Size)), records.Count);

    /// <summary>The BuildCache list of system/df's answer; empty when it has none.</summary>
    internal static IReadOnlyList<BuildCacheRecord> ParseBuildCacheRecords(string json)
    {
        var records = JsonNode.Parse(json)?["BuildCache"] as JsonArray ?? [];
        return records.OfType<JsonObject>().Select(r => new BuildCacheRecord(
            r["ID"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : "",
            r["Size"] is JsonValue size && size.TryGetValue<long>(out var n) ? n : 0,
            Flag(r["Shared"]), Flag(r["InUse"]), Time(r["CreatedAt"]), Time(r["LastUsedAt"]))).ToList();
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

    private static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // RFC 3339 with nanoseconds; null for none, or Docker's zero time.
    private static DateTime? Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year > 1
            ? t.UtcDateTime
            : null;

    private static string Hours(TimeSpan age) => Math.Max(1, (long)Math.Round(age.TotalHours)).ToString(CultureInfo.InvariantCulture);
}
