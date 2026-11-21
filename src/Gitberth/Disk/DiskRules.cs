using System.Text.Json;
using System.Text.Json.Serialization;
using Gitberth.Images;

namespace Gitberth.Disk;

/// <summary>Which run of the disk cleanup: the daily one, or one because the disk is getting low or is almost full.</summary>
public enum DiskTier { Routine, Low, Critical }

/// <summary>What a look at the disk tells: nothing new, getting low, almost full, or room again after one of those.</summary>
public enum DiskNotice { None, Low, TooLow, RoomAgain }

/// <summary>One run of the disk cleanup, as kept: when, what it freed, the free space after it, and who asked.</summary>
/// <param name="By">"Gitberth", or the person who clicked Clean up now.</param>
/// <param name="Failed">
/// Docker failed or refused part of it (the build cache's prune, or the look at the images): it isn't a run that found
/// nothing left to free, so it never rests, and it's tried again soon.
/// </param>
public sealed record DiskRun(DateTime At, long Freed, long FreeAfter, string By, bool Failed = false);

/// <summary>What a run did, for Settings › Disk, the audit log and the snackbar.</summary>
/// <param name="CacheFreed">What the build cache's prunes freed, as Docker counts it.</param>
/// <param name="CacheBefore">The build cache's size before the run (its records nobody shares); null when this engine can't say cheaply.</param>
/// <param name="Images">The images it removed: tags by name, untagged ones by their short ID.</param>
/// <param name="Freed">What the run freed: what the disk gained, and at least what the build cache freed.</param>
/// <param name="Problem">What Docker (or Gitberth's database) said when a part of the run failed; null when none did.</param>
public sealed record DiskResult(DateTime At, DiskTier Tier, string By, long Freed, long CacheFreed, long? CacheBefore, long? CacheAfter,
    IReadOnlyList<string> Images, long FreeBefore, long FreeAfter, long Total, string? Problem = null)
{
    /// <summary>It removed something: build cache, or an image.</summary>
    [JsonIgnore]
    public bool Removed => CacheFreed > 0 || Images.Count > 0;
}

/// <summary>The disk cleanup's own record, kept in disk.state so a restart neither runs it again early nor tells the same disk twice.</summary>
public sealed class DiskState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The last run of each kind, whatever it found.</summary>
    public Dictionary<DiskTier, DiskRun> Last { get; set; } = [];
    /// <summary>What was told last: Low or TooLow until room again was told (Ok).</summary>
    public DiskLevel Told { get; set; }
    /// <summary>When "getting low" was told last: not again within a day.</summary>
    public DateTime? LowToldAt { get; set; }
    /// <summary>What the attention board shows: it stays until there's room again, so a disk at the line doesn't flicker.</summary>
    public DiskLevel Shown { get; set; }
    /// <summary>The last run that ran, for Settings.</summary>
    public DiskResult? LastResult { get; set; }
    /// <summary>What the last run for a low or almost full disk cleared of the build cache, and when: the banner says it.</summary>
    public long? ClearedBytes { get; set; }
    public DateTime? ClearedAt { get; set; }
    /// <summary>The board item's stamp a person dismissed.</summary>
    public string? Dismissed { get; set; }

    public static DiskState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DiskState();
        try
        {
            return JsonSerializer.Deserialize<DiskState>(json, Json) ?? new DiskState();
        }
        catch (JsonException)
        {
            return new DiskState();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// The disk cleanup's rules, pure: when each run is due, how much of the build cache each removes, the cache's cap, what to
/// tell and what the attention board shows. A run that found little to free doesn't run again at once, so BuildKit isn't
/// asked over and over when there's nothing left.
/// </summary>
public static class DiskRules
{
    public const long MiB = 1L << 20;
    public const long GiB = 1L << 30;

    /// <summary>How often the disk is looked at.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(60);
    /// <summary>The first look after Gitberth starts, once start-up's own work is done.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(2);
    /// <summary>The routine run's day.</summary>
    public static readonly TimeSpan RoutineEvery = TimeSpan.FromHours(24);
    /// <summary>The routine run comes no sooner than this after Gitberth starts.</summary>
    public static readonly TimeSpan FirstRoutine = TimeSpan.FromMinutes(15);
    /// <summary>A low disk's run, at most this often.</summary>
    public static readonly TimeSpan LowEvery = TimeSpan.FromHours(1);
    /// <summary>An almost full disk's run, at most this often.</summary>
    public static readonly TimeSpan CriticalEvery = TimeSpan.FromMinutes(30);
    /// <summary>A routine run that waited (a deploy, a build): tried again after this.</summary>
    public static readonly TimeSpan RoutineRetry = TimeSpan.FromMinutes(10);
    /// <summary>A low or almost full disk's run that waited for a build: tried again after this.</summary>
    public static readonly TimeSpan UrgentRetry = TimeSpan.FromMinutes(2);
    /// <summary>A run that freed less than this isn't repeated at the same kind for <see cref="LittleRest"/>...</summary>
    public const long FreedLittle = 256 * MiB;
    public static readonly TimeSpan LittleRest = TimeSpan.FromHours(6);
    /// <summary>...unless the disk filled up by this much more since.</summary>
    public const long FilledMore = 2 * GiB;
    /// <summary>"Getting low" isn't told twice within this.</summary>
    public static readonly TimeSpan LowToldAgainAfter = TimeSpan.FromHours(24);
    /// <summary>A notice waits for its run (one a build holds off) at most this long after the disk got low.</summary>
    public static readonly TimeSpan NoticeWaitsForRun = TimeSpan.FromMinutes(10);

    public const int DefaultKeepDays = 7;
    public const int MinKeepDays = 1;
    public const int MaxKeepDays = 90;

    /// <summary>The build cache kept under this (routine and low runs): a tenth of the disk, between 2 and 20 GiB.</summary>
    public static long CacheCap(long totalBytes) => Math.Clamp(totalBytes / 10, 2 * GiB, 20 * GiB);

    /// <summary>The age prune's filter: no build used for N days (routine), a day (low); none, every unused record, when it's almost full.</summary>
    public static TimeSpan? Until(DiskTier tier, int keepDays) => tier switch
    {
        DiskTier.Routine => TimeSpan.FromDays(Math.Clamp(keepDays, MinKeepDays, MaxKeepDays)),
        DiskTier.Low => TimeSpan.FromHours(24),
        _ => null,
    };

    /// <summary>
    /// While the cache is over its cap, the next prunes, by age, each younger than the one before (routine and low only):
    /// 72 h, 24 h and 6 h, and a low disk 1 h too; only the ones younger than the age prune's own, which took the rest.
    /// Never anything a build used within the last hour.
    /// </summary>
    public static IReadOnlyList<TimeSpan> CapSteps(DiskTier tier, int keepDays)
    {
        if (tier == DiskTier.Critical) return [];
        TimeSpan[] steps = tier == DiskTier.Low
            ? [TimeSpan.FromHours(72), TimeSpan.FromHours(24), TimeSpan.FromHours(6), TimeSpan.FromHours(1)]
            : [TimeSpan.FromHours(72), TimeSpan.FromHours(24), TimeSpan.FromHours(6)];
        var first = Until(tier, keepDays)!.Value;
        return [.. steps.Where(s => s < first)];
    }

    /// <summary>
    /// The run due now, the worst first; null for none. An almost full disk's every 30 minutes, a low one's every hour, the
    /// routine one once a day (not in the first 15 minutes after a start). One that waited, or that Docker failed, is tried
    /// again only after its <paramref name="notBefore"/>; one that freed little rests (<see cref="Rests"/>). Off
    /// (<paramref name="auto"/>), none. A disk that couldn't be read gets no low runs, but the routine one still goes. A run
    /// kept with a time later than now (the clock went back since) counts as long ago.
    /// </summary>
    public static DiskTier? Due(DateTime now, DateTime startedAt, DiskSpace disk, DiskState state, bool auto, IReadOnlyDictionary<DiskTier, DateTime> notBefore)
    {
        if (!auto) return null;
        if (disk.Known && disk.TooLow && UrgentDue(DiskTier.Critical, CriticalEvery, now, disk, state, notBefore)) return DiskTier.Critical;
        // Almost full and its run not due: a low run would remove less than the one that ran.
        if (disk.Known && disk.Low && !disk.TooLow && UrgentDue(DiskTier.Low, LowEvery, now, disk, state, notBefore)) return DiskTier.Low;
        if (notBefore.TryGetValue(DiskTier.Routine, out var routineAt) && now < routineAt) return null;
        if (now < startedAt + FirstRoutine) return null;
        return state.Last.GetValueOrDefault(DiskTier.Routine) is not { } last || last.Failed || !Within(last.At, now, RoutineEvery) ? DiskTier.Routine : null;
    }

    private static bool UrgentDue(DiskTier tier, TimeSpan every, DateTime now, DiskSpace disk, DiskState state, IReadOnlyDictionary<DiskTier, DateTime> notBefore)
    {
        if (notBefore.TryGetValue(tier, out var at) && now < at) return false;
        if (state.Last.GetValueOrDefault(tier) is not { } last || last.Failed) return true;
        return !Within(last.At, now, every) && !Rests(last, now, disk);
    }

    /// <summary>
    /// A run that freed under 256 MiB rests 6 hours, unless the disk filled up by another 2 GiB since. Never one Docker failed
    /// or refused: it says nothing of what's left to free.
    /// </summary>
    public static bool Rests(DiskRun last, DateTime now, DiskSpace disk) =>
        !last.Failed && last.Freed < FreedLittle && Within(last.At, now, LittleRest) && disk.FreeBytes > last.FreeAfter - FilledMore;

    /// <summary>
    /// What to tell now, from what was told last and the disk as it is: getting low once (not again within a day), almost
    /// full once, and room again once, only after one of those was told. Nothing for a disk that couldn't be read.
    /// </summary>
    public static DiskNotice Tell(DiskLevel told, DateTime? lowToldAt, DiskSpace disk, DateTime now)
    {
        if (!disk.Known) return DiskNotice.None;
        if (disk.TooLow) return told == DiskLevel.TooLow ? DiskNotice.None : DiskNotice.TooLow;
        if (disk.Low)
            return told != DiskLevel.Ok || lowToldAt is { } at && Within(at, now, LowToldAgainAfter) ? DiskNotice.None : DiskNotice.Low;
        return told != DiskLevel.Ok && disk.RoomAgain ? DiskNotice.RoomAgain : DiskNotice.None;
    }

    /// <summary>
    /// Whether a kept time is less than <paramref name="span"/> before now. One later than now means the clock went back
    /// since it was kept (a host whose clock ran ahead, set right): that counts as long ago, never as just now.
    /// </summary>
    internal static bool Within(DateTime at, DateTime now, TimeSpan span) => at <= now && now - at < span;

    /// <summary>What told is after a notice.</summary>
    public static DiskLevel Told(DiskLevel told, DiskNotice notice) => notice switch
    {
        DiskNotice.Low => DiskLevel.Low,
        DiskNotice.TooLow => DiskLevel.TooLow,
        DiskNotice.RoomAgain => DiskLevel.Ok,
        _ => told,
    };

    /// <summary>What the attention board shows: as the disk is, and a warning that showed stays until there's room again.</summary>
    public static DiskLevel Shown(DiskLevel before, DiskSpace disk) =>
        !disk.Known ? DiskLevel.Ok
        : disk.TooLow ? DiskLevel.TooLow
        : disk.Low || before != DiskLevel.Ok && !disk.RoomAgain ? DiskLevel.Low
        : DiskLevel.Ok;

    /// <summary>The days the build cache is kept, from its setting: 7 when unset or out of range.</summary>
    public static int KeepDays(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days)
        && days is >= MinKeepDays and <= MaxKeepDays ? days : DefaultKeepDays;
}
