using System.Globalization;
using Gitberth.Backup;
using Gitberth.Disk;

namespace Gitberth.Components.Shared;

/// <summary>
/// What Settings › Save and restore, the Restoring page and the dashboard's banner say, and which items a tick takes
/// with it. Pure, so each is tested.
/// </summary>
public static class BackupText
{
    /// <summary>The passphrase's least length, as IBackupService.StartSaveAsync takes it.</summary>
    public const int MinPassphrase = 12;

    /// <summary>The upload's endpoint (POST, the antiforgery token as a form field, then the file).</summary>
    public const string UploadUrl = "backup/upload";

    /// <summary>Where a save's file is downloaded from, once.</summary>
    public static string DownloadUrl(string ticketId) => "backup/download/" + Uri.EscapeDataString(ticketId);

    /// <summary>Gitberth never keeps the passphrase: the line under both passphrase fields.</summary>
    public const string NeverKept = "Gitberth never keeps it: without it the file can't be opened.";

    // ---- Saving ----

    /// <summary>What's wrong with the passphrase as typed so far; null while it's empty or long enough.</summary>
    public static string? TooShort(string? passphrase) =>
        string.IsNullOrEmpty(passphrase) || passphrase.Length >= MinPassphrase ? null
        : $"At least {MinPassphrase} characters: {passphrase.Length.ToString(CultureInfo.InvariantCulture)} so far.";

    /// <summary>What's wrong with the passphrase typed again; null while it's empty or the same.</summary>
    public static string? Mismatch(string? passphrase, string? again) =>
        string.IsNullOrEmpty(again) || again == passphrase ? null : "It isn't the same as the passphrase beside it.";

    /// <summary>Whether a save can start: a long enough passphrase, typed the same twice.</summary>
    public static bool CanSave(string? passphrase, string? again) =>
        passphrase is { Length: >= MinPassphrase } && again == passphrase;

    /// <summary>The items ticked at first: those the list ticks by default, and those that can't be left out.</summary>
    public static HashSet<string> Defaults(IEnumerable<BackupItem> items) =>
        items.Where(i => i.Included || i.Required).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The ticks after one item is ticked or unticked: its children follow it, and a child ticked ticks its parent too (an
    /// app's volume isn't saved without the app). An item that can't be left out stays ticked.
    /// </summary>
    public static HashSet<string> Toggle(IReadOnlyList<BackupItem> items, IReadOnlySet<string> ticked, string id, bool on)
    {
        var next = new HashSet<string>(ticked, StringComparer.Ordinal);
        if (items.FirstOrDefault(i => i.Id == id) is not { Required: false } item) return next;
        void Set(string which, bool value)
        {
            if (value) next.Add(which);
            else next.Remove(which);
        }

        Set(id, on);
        foreach (var child in items.Where(i => i.Parent == id && !i.Required)) Set(child.Id, on);
        if (on && item.Parent is { } parent) next.Add(parent);
        return next;
    }

    /// <summary>Whether Docker measures the item (so a missing size is still coming), rather than having no size of its own.</summary>
    public static bool Measured(BackupItemKind kind) => kind is BackupItemKind.GitberthData or BackupItemKind.Volume or BackupItemKind.Database or BackupItemKind.Image;

    /// <summary>An item's size on its row: "254 MB", "measuring…" while Docker measures it, nothing for an app or container.</summary>
    public static string? Size(BackupItem item) =>
        item.Bytes is { } bytes ? DiskText.Size(bytes) : Measured(item.Kind) ? "measuring…" : null;

    /// <summary>"4 items chosen: about 1.2 GB before compression." ("at least" while a size is being measured).</summary>
    public static string Total(IReadOnlyList<BackupItem> items, IReadOnlySet<string> ticked)
    {
        var chosen = items.Where(i => i.Required || ticked.Contains(i.Id)).ToList();
        var known = chosen.Sum(i => i.Bytes ?? 0);
        var measuring = chosen.Any(i => i.Bytes is null && Measured(i.Kind));
        return $"{Count(chosen.Count, "item")} chosen: {(measuring ? "at least" : "about")} {DiskText.Size(known)} before compression.";
    }

    /// <summary>The save's line while it runs, and once it's over: "Volumes: volume gb-website-data · 3 of 6 done · 122 MB written".</summary>
    public static string Progress(SaveProgress p) =>
        p.Problem is { } problem ? $"The save failed: {Clause(problem)}. The file it downloaded can't be used: delete it, and save again."
        : p.Done ? $"Saved {Count(p.ItemsTotal, "item")}: {DiskText.Size(p.BytesWritten)}, compressed and encrypted. The file is in your browser's downloads."
        : $"{p.Phase}{(p.Current is { Length: > 0 } now ? ": " + now : "")} · {p.ItemsDone.ToString(CultureInfo.InvariantCulture)} of {p.ItemsTotal.ToString(CultureInfo.InvariantCulture)} done · {DiskText.Size(p.BytesWritten)} written";

    /// <summary>How far the save is, 0 to 1, by items; null before it knows how many.</summary>
    public static double? Fraction(SaveProgress p) =>
        p.Done ? 1 : p.ItemsTotal > 0 ? Math.Clamp((double)p.ItemsDone / p.ItemsTotal, 0, 1) : null;

    // ---- Restoring from a file ----

    /// <summary>"Uploading gitberth-backup-2026-11-22.gbak: 120 MB of 512 MB (23%)".</summary>
    public static string Uploading(string? name, long sent, long total) =>
        $"Uploading {(string.IsNullOrEmpty(name) ? "the file" : name)}: {DiskText.Size(sent)} of {DiskText.Size(total)} ({Percent(sent, total)})";

    /// <summary>The upload's progress, 0 to 1.</summary>
    public static double UploadFraction(long sent, long total) => total > 0 ? Math.Clamp((double)sent / total, 0, 1) : 0;

    /// <summary>"gitberth-backup-2026-11-22.gbak is uploaded (512 MB)." or, after a reload, "A backup file is uploaded (512 MB)."</summary>
    public static string Uploaded(string? name, long bytes) =>
        $"{(string.IsNullOrEmpty(name) ? "A backup file" : name)} is uploaded ({DiskText.Size(bytes)}).";

    /// <summary>
    /// Why an upload didn't work: the endpoint's own sentence when it gave one (never a page of HTML or JSON), else what
    /// its status means.
    /// </summary>
    public static string UploadFailed(int status, string? answer)
    {
        var said = OneLine(answer);
        if (said is { Length: > 0 and <= 300 } && !said.StartsWith('<') && !said.StartsWith('{'))
            return $"The file wasn't uploaded. {Clause(said)}.";
        return "The file wasn't uploaded: " + status switch
        {
            0 => "the connection to Gitberth was lost.",
            400 => "Gitberth refused it. Reload the page and try again.",
            401 or 403 => "you aren't signed in any more. Reload the page and sign in.",
            413 => "it's larger than Gitberth takes.",
            >= 500 => $"Gitberth couldn't keep it (error {status.ToString(CultureInfo.InvariantCulture)}).",
            _ => $"Gitberth answered {status.ToString(CultureInfo.InvariantCulture)}.",
        };
    }

    /// <summary>The check's answer when it isn't a summary: the problem's own words, else a sentence of the kind's.</summary>
    public static string CheckProblem(UploadProblem problem, string? message) => OneLine(message) is { Length: > 0 } said ? said : problem switch
    {
        UploadProblem.NotABackup => "This file isn't a Gitberth backup.",
        UploadProblem.WrongPassphrase => "This passphrase doesn't open the file.",
        UploadProblem.Damaged => "The file was changed or cut short after it was saved, so none of it is trusted.",
        UploadProblem.TooNew => "A newer Gitberth made this file: update Gitberth first.",
        UploadProblem.NotEmpty => "This Gitberth has apps already: a backup is restored onto a fresh install only.",
        _ => "The file can't be restored.",
    };

    /// <summary>The problem's title on its banner.</summary>
    public static string CheckTitle(UploadProblem problem) => problem switch
    {
        UploadProblem.NotABackup => "Not a Gitberth backup",
        UploadProblem.WrongPassphrase => "Wrong passphrase",
        UploadProblem.Damaged => "The file is damaged",
        UploadProblem.TooNew => "Made by a newer Gitberth",
        UploadProblem.NotEmpty => "This Gitberth isn't empty",
        _ => "It can't be restored",
    };

    /// <summary>"Saved on homelab, 22 Nov 2026 18:00, by Gitberth 1.7.0 (amd64, Docker 29.0.1)." in Gitberth's time zone.</summary>
    public static string Made(BackupSummary s, TimeZoneInfo? zone = null)
    {
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s.SavedAt, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
        return $"Saved on {s.Host}, {at.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}, by Gitberth {s.GitberthVersion} ({s.Architecture}, Docker {s.DockerVersion}).";
    }

    /// <summary>"It brings back 3 apps, 2 volumes, 1 database server and 1 other container, with Gitberth's data."</summary>
    public static string Holds(IReadOnlyList<BackupItem> items) =>
        HoldsList(items) is { } list ? $"It brings back {list}, with Gitberth's data." : "It holds Gitberth's data alone.";

    // "3 apps, 2 volumes and 1 database server"; null for Gitberth's data alone.
    private static string? HoldsList(IReadOnlyList<BackupItem> items)
    {
        var parts = new[]
        {
            Of(items, BackupItemKind.App, "app"),
            Of(items, BackupItemKind.Volume, "volume"),
            Of(items, BackupItemKind.Database, "database server"),
            Of(items, BackupItemKind.Container, "other container"),
            Of(items, BackupItemKind.Image, "image only that host had", "images only that host had"),
        }.OfType<string>().ToList();
        return parts.Count == 0 ? null : Join(parts);
    }

    /// <summary>A warning's title on its banner.</summary>
    public static string WarningTitle(RestoreWarningKind kind) => kind switch
    {
        RestoreWarningKind.TunnelElsewhere => "Stop the tunnel on the other machine",
        RestoreWarningKind.PortTaken => "A host port is taken here",
        RestoreWarningKind.NameTaken => "A container's name is taken here",
        RestoreWarningKind.VolumeHasData => "A volume here holds data already",
        RestoreWarningKind.ArchitectureDiffers => "Another kind of CPU",
        RestoreWarningKind.DockerTooOld => "Docker is older here",
        RestoreWarningKind.BindPathMissing => "A host folder isn't here",
        RestoreWarningKind.NeedsSpace => "Not enough disk space",
        _ => "Worth knowing",
    };

    /// <summary>What the restore's sign-in will be: the line every summary ends with, and the confirmation repeats.</summary>
    public const string SignInNote =
        "Gitberth restarts on the saved data partway through. Then you sign in with the admin password of the machine the backup was saved on, not this one's.";

    /// <summary>The confirmation's lines before Restore.</summary>
    public static IReadOnlyList<string> ConfirmLines(BackupSummary s) =>
    [
        HoldsList(s.Items) is { } list
            ? $"Gitberth brings back {list} from the backup saved on {s.Host}, with its own data."
            : $"Gitberth brings back its own data from the backup saved on {s.Host}.",
        SignInNote,
    ];

    // ---- The Restoring page ----

    /// <summary>The page's title line: where the restore stands.</summary>
    public static string Headline(RestoreStatus s) =>
        s.Finished ? s.Failed == 0 ? "Restored" : $"Restored, but {Count(s.Failed, "step")} failed"
        : s.Active ? $"Restoring, part {Math.Clamp(s.Phase, 1, 2).ToString(CultureInfo.InvariantCulture)} of 2"
        : "The restore stopped";

    /// <summary>The line under it.</summary>
    public static string Lead(RestoreStatus s) =>
        s.Finished ? s.Failed == 0 ? "Everything the backup holds is back. Each app's page says how it runs."
            : "The rest is back. The log says why each failed step failed; fix it on the app's page."
        : s.Active ? s.Phase <= 1
            ? "Volumes, databases, images and containers come from the file first, then Gitberth restarts on the saved data."
            : "Gitberth runs on the saved data now. It deploys each Git and Compose app at the commit it ran, one at a time, then routes the domains."
        : "It ended before every step was done. The log says where it stopped.";

    /// <summary>
    /// The Restoring page's line while a restore runs, and the reconnect box's while Gitberth restarts: the page comes back
    /// by itself, then the saved machine's admin password signs in.
    /// </summary>
    public const string RestartNote =
        "Gitberth restarts on the saved data partway through. This page comes back by itself; then sign in with the admin password of the machine the backup was saved on.";

    /// <summary>"5 of 12 steps done" (a step skipped counts as done).</summary>
    public static string Steps(RestoreStatus s) =>
        $"{s.Steps.Count(x => x.State is RestoreStepState.Done or RestoreStepState.Skipped).ToString(CultureInfo.InvariantCulture)} of {Count(s.Steps.Count, "step")} done";

    /// <summary>The step's mark: the change steps' look (done, now, todo, failed) and skipped.</summary>
    public static string StepClass(RestoreStepState state) => state switch
    {
        RestoreStepState.Done => "done",
        RestoreStepState.Running => "now",
        RestoreStepState.Failed => "failed",
        RestoreStepState.Skipped => "skipped",
        _ => "todo",
    };

    // ---- The dashboard ----

    /// <summary>The dashboard's banner: a restore that runs, or a Gitberth with no apps that could take one; null for neither.</summary>
    public static (string Title, string Text, string Action, string Href)? Banner(RestoreStatus? status, string? cantRestore) =>
        status is { Active: true, Finished: false }
            ? ("A restore is running", $"Gitberth is bringing a backup back: part {Math.Clamp(status.Phase, 1, 2).ToString(CultureInfo.InvariantCulture)} of 2, {Steps(status)}.",
               "See how it goes", "restoring")
        : cantRestore is null
            ? ("Moving from another machine? Restore a backup.",
               "This Gitberth has no apps yet. A backup saved in Settings › Save and restore on the other machine brings all of its apps here at once.",
               "Restore a backup", "settings#backup")
            : null;

    // ---- Words ----

    public static string Count(int n, string what, string? plural = null) =>
        n == 1 ? $"1 {what}" : $"{n.ToString(CultureInfo.InvariantCulture)} {plural ?? what + "s"}";

    private static string? Of(IReadOnlyList<BackupItem> items, BackupItemKind kind, string one, string? many = null) =>
        items.Count(i => i.Kind == kind) is var n and > 0 ? Count(n, one, many) : null;

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private static string Percent(long part, long whole) =>
        (whole > 0 ? Math.Clamp(part * 100 / whole, 0, 100) : 0).ToString(CultureInfo.InvariantCulture) + "%";

    private static string? OneLine(string? text) =>
        text is null ? null : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

    // A sentence in the middle of another: one line, without its full stop.
    private static string Clause(string text) => (OneLine(text) ?? "").TrimEnd('.');
}
