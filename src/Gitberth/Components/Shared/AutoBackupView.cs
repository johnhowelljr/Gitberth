using System.Globalization;
using Gitberth.Backup;
using Gitberth.Disk;

namespace Gitberth.Components.Shared;

/// <summary>
/// What Settings › Save and restore › Automatic backups says: the last run and the next, each run in the list, and the
/// restore list's rows. Pure, so each is tested.
/// </summary>
public static class AutoBackupView
{
    /// <summary>"27 Nov 2026 03:00", in Gitberth's time zone.</summary>
    public static string When(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"The last backup: 27 Nov 2026 03:00, 412 MB in 38 s." or "… failed: why."; null before the first.</summary>
    public static string? Last(AutoBackupRun? run, TimeZoneInfo zone) => run switch
    {
        null => null,
        { Ok: true } => $"The last backup: {When(run.At, zone)}, {DiskText.Size(run.Bytes)} in {Seconds(run.Seconds)}{(run.Manual ? " (Back up now)" : "")}.",
        _ => $"The last backup, {When(run.At, zone)}, failed: {Clause(run.Problem)}.",
    };

    /// <summary>"The next: 28 Nov 2026 03:00." or that it's off.</summary>
    public static string Next(AutoBackupConfig c, DateTime? next, TimeZoneInfo zone) =>
        c.Schedule == AutoSchedule.Off ? "Off: no backup runs on its own. Back up now still works once a destination and a passphrase are set."
        : next is { } at ? $"The next: {When(at, zone)} ({AutoBackupText.Schedule(c)})."
        : $"Scheduled {AutoBackupText.Schedule(c)}.";

    /// <summary>One run in the list: when, how it started, and how it ended.</summary>
    public static string Run(AutoBackupRun run, TimeZoneInfo zone) =>
        $"{When(run.At, zone)} · {(run.Manual ? "Back up now" + (run.By is { Length: > 0 } by ? $" ({by})" : "") : "scheduled")} · "
        + (run.Ok ? $"{DiskText.Size(run.Bytes)} in {Seconds(run.Seconds)}" : "failed: " + Clause(run.Problem));

    /// <summary>"38 s", "2 min 5 s".</summary>
    public static string Seconds(double seconds) => seconds < 60
        ? $"{Math.Max(0.1, seconds).ToString("0.#", CultureInfo.InvariantCulture)} s"
        : $"{((int)(seconds / 60)).ToString(CultureInfo.InvariantCulture)} min {((int)(seconds % 60)).ToString(CultureInfo.InvariantCulture)} s";

    /// <summary>A file at the destination, for the restore list: "27 Nov 2026 03:00 · 412 MB · key 3f9c2ab1 (this passphrase's) · homelab".</summary>
    public static string File(RemoteBackup b, string? currentKey, TimeZoneInfo zone) =>
        $"{When(b.SavedAt, zone)} · {DiskText.Size(b.Bytes)} · "
        + (b.KeyId is { } key ? $"key {key}{(key == currentKey ? " (the passphrase set now)" : " (an earlier passphrase)")}" : "key unknown")
        + $" · {b.Host}";

    /// <summary>The fetch's line: "Fetching gitberth-auto-….gbak: 120 MB of 412 MB".</summary>
    public static string Fetching(string name, long bytes, long total) =>
        $"Fetching {name}: {DiskText.Size(bytes)}{(total > 0 ? " of " + DiskText.Size(total) : "")}";

    /// <summary>The ticks' summary over the list: "Everything is saved." or "Everything but 2 items." with what's added.</summary>
    public static string What(IReadOnlyList<BackupItem> items, IReadOnlySet<string> ticked)
    {
        var defaults = items.Where(i => i.Included || i.Required).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var left = defaults.Count(id => !ticked.Contains(id));
        var added = ticked.Count(id => !defaults.Contains(id));
        var head = left == 0 ? "Everything the list ticks is saved" : $"Everything but {BackupText.Count(left, "item")} left out";
        return head + (added > 0 ? $", and {BackupText.Count(added, "item")} the list leaves out by default" : "") + ". A new app is saved by itself.";
    }

    private static string Clause(string? text) => (text ?? "it didn't say why").Trim().TrimEnd('.');
}
