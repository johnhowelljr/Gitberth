using System.Globalization;
using Gitberth.Images;

namespace Gitberth.Disk;

/// <summary>Everything the disk cleanup says: Settings › Disk, the attention board, notices, the audit log. Pure, so each is tested.</summary>
public static class DiskText
{
    /// <summary>"2.1 GB", "340 MB", "12 KB": a size as the disk's lines say it (the free-space lines' own units, as the Apps strip says them).</summary>
    public static string Size(long bytes)
    {
        var b = Math.Max(0, bytes);
        return b >= DiskRules.GiB ? (b / (double)DiskRules.GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : b >= DiskRules.MiB ? (b / (double)DiskRules.MiB).ToString("0", CultureInfo.InvariantCulture) + " MB"
            : Math.Ceiling(b / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }

    /// <summary>"41.2 GB free of 64.0 GB".</summary>
    public static string FreeOf(DiskSpace disk) => $"{Size(disk.FreeBytes)} free of {Size(disk.TotalBytes)}";

    /// <summary>"14.9 GB free": the dashboard's and the Apps strip's figure, in the lines' own units, so it's under them when they say so.</summary>
    public static string Free(DiskSpace disk) => $"{Size(disk.FreeBytes)} free";

    /// <summary>"49": whole gigabytes in the lines' own units, for the Apps strip's "49 of 64 GB".</summary>
    public static string WholeGb(long bytes) => (Math.Max(0, bytes) / (double)DiskRules.GiB).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The build cache's line in Settings › Disk, in the same units as the rest of the section.</summary>
    public static string Cache(long bytes) => $"{Size(bytes)} on this host. It makes builds quick: steps that didn't change aren't run again.";

    /// <summary>"Gitberth warns under 15.0 GB free and stops pulling images under 5.0 GB."</summary>
    public static string Lines(DiskSpace disk) => $"Gitberth warns under {Size(disk.WarnBelow)} free and stops pulling images under {Size(disk.RefuseBelow)}.";

    /// <summary>"a day", "7 days".</summary>
    public static string Days(int n) => n == 1 ? "a day" : $"{n.ToString(CultureInfo.InvariantCulture)} days";

    /// <summary>The build cache row's line about what the next cleanup takes; null when there's none of it.</summary>
    public static string? Stale(long stale, int keepDays) =>
        stale > 0 ? $"{Size(stale)} of it no build used for {Days(keepDays)}: the next cleanup removes that." : null;

    /// <summary>What the switch's cleanup does, with the days it keeps the cache and its cap.</summary>
    public static string AutoSub(int keepDays, long cap) =>
        $"Every day: build cache no build used for {Days(keepDays)}, and the cache kept under {Size(cap)}; leftover images of deleted apps and previews; "
        + "untagged images of Gitberth's old builds. When the disk gets low, more of the cache goes. Nothing an app runs, rolls back to or builds with is touched.";

    /// <summary>"Today 04:12, by Gitberth: 2.1 GB freed (build cache 2.0 GB, 3 images). 41.2 GB free after." / "No cleanup yet."</summary>
    public static string Last(DiskResult? result, DateTime now) => result is null
        ? "No cleanup yet."
        : $"{When(result.At, now)}, by {result.By}: {Did(result)}. {Size(result.FreeAfter)} free after.";

    // "2.1 GB freed (build cache 2.0 GB, 3 images)", "1 image removed" (the disk gained too little to say), "nothing to clean up",
    // or "Docker didn't clean up (…)" when a part of it failed and nothing went.
    private static string Did(DiskResult r) =>
        !r.Removed ? r.Problem is { } problem ? $"Docker didn't clean up ({Clause(problem)})" : "nothing to clean up"
        : r.Freed > 0 ? $"{Size(r.Freed)} freed ({What(r)})" : $"{What(r)} removed";

    // "build cache 2.0 GB, 3 images".
    private static string What(DiskResult r) => string.Join(", ", new[]
    {
        r.CacheFreed > 0 ? $"build cache {Size(r.CacheFreed)}" : null,
        r.Images.Count > 0 ? Count(r.Images.Count, "image") : null,
    }.OfType<string>());

    /// <summary>"Today 04:12", "Yesterday 04:12", "14 Nov 04:12", in Gitberth's time zone.</summary>
    public static string When(DateTime at, DateTime now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? $"Today {time}" : local.Date == today.AddDays(-1) ? $"Yesterday {time}" : $"{local.ToString("d MMM", CultureInfo.InvariantCulture)} {time}";
    }

    /// <summary>Clean up now's answer.</summary>
    public static string Cleaned(DiskResult result) =>
        !result.Removed ? result.Problem is { } problem ? $"Docker didn't clean up: {Clause(problem)}." : "Nothing to clean up."
        : result.Freed > 0 ? $"Cleaned up: {Size(result.Freed)} freed."
        : $"Cleaned up: {Count(result.Images.Count, "image")} removed.";

    public const string BuildRunning = "A build is running: clean up once it's done.";
    public const string AlreadyRunning = "A cleanup is running already.";

    /// <summary>What Clear build cache is told while a run of the cleanup holds builds off.</summary>
    public const string Holding = "The disk cleanup is running";

    /// <summary>Clean up now's answer while something else holds builds off: a build, or Clear build cache (<paramref name="holder"/>).</summary>
    public static string HeldOff(string? holder) => holder is { Length: > 0 } h ? $"{h}: clean up once that's done." : BuildRunning;

    // ---- The audit log ----

    /// <summary>The entry of a run that removed something (settings.disk.cleaned).</summary>
    public static string Audit(DiskResult r, int keepDays) => r.Tier switch
    {
        DiskTier.Critical =>
            $"Docker's disk was almost full ({Size(r.FreeBefore)} free), so Gitberth cleared all the build cache no build was using{Also(r)}{Freed(r)}. {Size(r.FreeAfter)} free now.",
        DiskTier.Low =>
            $"Docker's disk was getting low ({Size(r.FreeBefore)} free), so Gitberth removed the build cache no build used for a day{Also(r)}{Freed(r)}. {Size(r.FreeAfter)} free now.",
        _ => r.Freed > 0
            ? $"Cleaned up Docker's disk: {Size(r.Freed)} freed ({Parts(r, keepDays)}). {Size(r.FreeAfter)} free now."
            : $"Cleaned up Docker's disk: removed {Parts(r, keepDays)}. {Size(r.FreeAfter)} free now.",
    };

    // ": 6.4 GB freed"; nothing when the disk gained too little to say.
    private static string Freed(DiskResult r) => r.Freed > 0 ? $": {Size(r.Freed)} freed" : "";

    // ", and 3 images no app needs".
    private static string Also(DiskResult r) => r.Images.Count > 0 ? $", and {Count(r.Images.Count, "image")} no app needs" : "";

    private static string Parts(DiskResult r, int keepDays) => string.Join(", and ", new[]
    {
        r.CacheFreed > 0 ? $"build cache no build used for {Days(keepDays)}" : null,
        r.Images.Count > 0 ? $"{Count(r.Images.Count, "image")} no app needs" : null,
    }.OfType<string>());

    /// <summary>The audit entry's line of the images it removed: their count, then up to 10 of them.</summary>
    public static string ImagesLine(IReadOnlyList<string> images) =>
        $"{images.Count.ToString(CultureInfo.InvariantCulture)}: {string.Join(", ", images.Take(10))}{(images.Count > 10 ? ", …" : "")}";

    // ---- The attention board ----

    public static string Title(DiskLevel level) => level == DiskLevel.TooLow ? "Docker's disk is almost full" : "Docker's disk is getting low";

    /// <summary>The dashboard's banner: how much is free, what that means, and what the cleanup cleared.</summary>
    /// <param name="cleared">What the last low or almost-full run cleared of the build cache, and when; null when none freed any.</param>
    public static string Banner(DiskLevel level, DiskSpace disk, (long Bytes, DateTime At)? cleared) => level == DiskLevel.TooLow
        ? $"{Size(disk.FreeBytes)} free: Gitberth doesn't pull images until there's more room, and builds may fail."
          + (cleared is { } c ? $" It cleared all the build cache no build was using ({Size(c.Bytes)}) at {Clock(c.At)}." : "")
          + " Free some space: Settings › Disk shows what takes it."
        : $"{FreeOf(disk)}. Gitberth stops pulling images under {Size(disk.RefuseBelow)}."
          + (cleared is { } d ? $" It cleared {Size(d.Bytes)} of old build cache at {Clock(d.At)}." : "");

    /// <summary>What agents hear (get_health's problems).</summary>
    public static string Problem(DiskLevel level, DiskSpace disk) => level == DiskLevel.TooLow
        ? $"Docker's disk is almost full: {FreeOf(disk)}; Gitberth pulls no image until there's more room, and builds may fail."
        : $"Docker's disk is getting low: {FreeOf(disk)}; pulls stop under {Size(disk.RefuseBelow)}.";

    // ---- Notices ----

    /// <summary>A notice's title and text; <paramref name="host"/> is left out when unknown.</summary>
    /// <param name="cleared">What the run before it cleared of the build cache; 0 for nothing.</param>
    public static (string Title, string Text) Notice(DiskNotice notice, DiskSpace disk, string? host, long cleared) => notice switch
    {
        DiskNotice.TooLow => ("Docker's disk is almost full",
            $"{FreeOf(disk)}{On(host)}: Gitberth doesn't pull images until there's more room, and builds may fail."
            + (cleared > 0 ? $" It cleared all the build cache no build was using ({Size(cleared)})." : "")
            + " Free some space: Settings › Disk shows what takes it."),
        DiskNotice.Low => ("Docker's disk is getting low",
            $"{FreeOf(disk)}{On(host)}. Gitberth stops pulling images under {Size(disk.RefuseBelow)}."
            + (cleared > 0 ? $" It cleared {Size(cleared)} of build cache no build used for a day." : "")
            + " Settings › Disk shows what takes the room."),
        _ => ("Docker's disk has room again", $"{FreeOf(disk)}{On(host)}."),
    };

    private static string On(string? host) => host is { Length: > 0 } h ? $" on {h}" : "";

    // ---- Images of deleted apps ----

    public const string Kept = "Kept when the app was deleted";
    public const string Waiting = "Waiting to be removed: a container uses it";

    /// <summary>"3 images, about 1.2 GB".</summary>
    public static string Group(int count, long size) => $"{Count(count, "image")}, about {Size(size)}";

    /// <summary>Remove these images' answer.</summary>
    public static string RemovedDeleted(int removed, int stayed) => (removed, stayed) switch
    {
        (0, 0) => "There were no images of deleted apps left.",
        (_, 0) => $"Removed {Count(removed, "image")} of deleted apps.",
        (0, _) => $"Nothing was removed: a container uses {(stayed == 1 ? "that image" : "those images")}. Gitberth removes {(stayed == 1 ? "it" : "them")} once nothing does.",
        _ => $"Removed {Count(removed, "image")} of deleted apps. {(stayed == 1 ? "1 stays while a container uses it" : $"{stayed.ToString(CultureInfo.InvariantCulture)} stay while containers use them")}: Gitberth removes {(stayed == 1 ? "it" : "them")} once nothing does.",
    };

    /// <summary>Remove these images' answer when Docker failed to remove some (not a container's refusal): what went, and what Docker said.</summary>
    public static string RemovedDeletedFailed(int removed, string problem) =>
        (removed > 0 ? $"Removed {Count(removed, "image")} of deleted apps. " : "") + $"Docker didn't remove the rest: {Clause(problem)}. Gitberth tries again at its next cleanup.";

    /// <summary>Clean up now's answer when something other than Docker's answer failed (Gitberth's database, say).</summary>
    public static string Couldnt(string problem) => $"Couldn't clean up: {Clause(problem)}.";

    public static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n.ToString(CultureInfo.InvariantCulture)} {what}s";

    private static string Clock(DateTime at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // What Docker said, in the middle of a sentence: one line, without its full stop, at most 300 characters.
    private static string Clause(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).TrimEnd('.');
        return line.Length > 300 ? line[..299] + "…" : line;
    }
}
