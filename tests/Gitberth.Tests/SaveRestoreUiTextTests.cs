using Gitberth.Backup;
using Gitberth.Components.Shared;

namespace Gitberth.Tests;

/// <summary>
/// Settings › Save and restore, the Restoring page and the dashboard's banner, in words (BackupText): the passphrase's
/// checks, which items a tick takes with it, sizes and totals, the save's progress line, the upload's lines and failures,
/// a backup's summary and warnings, the restore's headline, and when the dashboard offers a restore.
/// </summary>
public sealed class SaveRestoreUiTextTests
{
    private static readonly IReadOnlyList<BackupItem> Items =
    [
        new("gitberth", BackupItemKind.GitberthData, "Gitberth's data", null, 18_400_000, Required: true, Included: true, null),
        new("app:1", BackupItemKind.App, "website", "Git app", null, false, true, null),
        new("volume:gb-website-data", BackupItemKind.Volume, "gb-website-data", "used by website", 254L << 20, false, true, "app:1"),
        new("app:2", BackupItemKind.App, "postgres", "Registry app", null, false, true, null),
        new("database:postgres", BackupItemKind.Database, "postgres", "Postgres 18", null, false, true, "app:2"),
        new("volume:cache", BackupItemKind.Volume, "cache", "a cache", 3L << 30, false, Included: false, null),
        new("container:cloudflare-tunnel", BackupItemKind.Container, "cloudflare-tunnel", null, null, false, true, null),
    ];

    [Fact]
    public void The_passphrase_needs_twelve_characters_typed_the_same_twice()
    {
        Assert.Null(BackupText.TooShort(null));
        Assert.Null(BackupText.TooShort(""));
        Assert.Equal("At least 12 characters: 5 so far.", BackupText.TooShort("short"));
        Assert.Null(BackupText.TooShort("twelve chars"));
        Assert.Null(BackupText.Mismatch("twelve chars", ""));
        Assert.Null(BackupText.Mismatch("twelve chars", "twelve chars"));
        Assert.Equal("It isn't the same as the passphrase beside it.", BackupText.Mismatch("twelve chars", "twelve char"));
        Assert.True(BackupText.CanSave("twelve chars", "twelve chars"));
        Assert.False(BackupText.CanSave("twelve char", "twelve char"));
        Assert.False(BackupText.CanSave("twelve chars", "Twelve chars"));
        Assert.False(BackupText.CanSave(null, null));
    }

    [Fact]
    public void A_tick_takes_its_children_with_it_and_a_child_ticks_its_parent()
    {
        var ticked = BackupText.Defaults(Items);
        Assert.DoesNotContain("volume:cache", ticked);
        Assert.Contains("gitberth", ticked);

        // Unticking an app unticks its volume; ticking the volume again ticks the app.
        var off = BackupText.Toggle(Items, ticked, "app:1", false);
        Assert.DoesNotContain("app:1", off);
        Assert.DoesNotContain("volume:gb-website-data", off);
        Assert.Contains("app:2", off);
        var back = BackupText.Toggle(Items, off, "volume:gb-website-data", true);
        Assert.Contains("app:1", back);
        Assert.Contains("volume:gb-website-data", back);

        // A child unticked leaves its app ticked; Gitberth's data can't be left out; an unknown id changes nothing.
        Assert.Contains("app:2", BackupText.Toggle(Items, ticked, "database:postgres", false));
        Assert.Contains("gitberth", BackupText.Toggle(Items, ticked, "gitberth", false));
        Assert.True(ticked.SetEquals(BackupText.Toggle(Items, ticked, "volume:gone", false)));
        // The set it was given stays as it was.
        Assert.Contains("app:1", ticked);
    }

    [Fact]
    public void Sizes_and_the_total_say_what_is_known()
    {
        Assert.Equal("254 MB", BackupText.Size(Items[2]));
        Assert.Equal("measuring…", BackupText.Size(Items[4]));
        Assert.Null(BackupText.Size(Items[1]));
        Assert.Null(BackupText.Size(Items[6]));

        var ticked = BackupText.Defaults(Items);
        // A database still being measured: "at least".
        Assert.Equal("6 items chosen: at least 272 MB before compression.", BackupText.Total(Items, ticked));
        var measured = Items.Select(i => i.Id == "database:postgres" ? i with { Bytes = 40L << 20 } : i).ToList();
        Assert.Equal("6 items chosen: about 312 MB before compression.", BackupText.Total(measured, ticked));
        // Required items count whatever the ticks say.
        Assert.Equal("1 item chosen: about 18 MB before compression.", BackupText.Total(Items, new HashSet<string>()));
    }

    [Fact]
    public void The_progress_line_says_what_is_saved_and_how_it_ended()
    {
        var running = new SaveProgress("Volumes", "volume gb-website-data", 128L << 20, 3, 6, false, null);
        Assert.Equal("Volumes: volume gb-website-data · 3 of 6 done · 128 MB written", BackupText.Progress(running));
        Assert.Equal(0.5, BackupText.Fraction(running));
        Assert.Equal("Manifest · 0 of 6 done · 4 KB written", BackupText.Progress(running with { Phase = "Manifest", Current = null, BytesWritten = 4096, ItemsDone = 0 }));
        Assert.Null(BackupText.Fraction(running with { ItemsTotal = 0 }));

        var done = running with { Done = true, ItemsDone = 6, BytesWritten = 1L << 30, Current = null };
        Assert.Equal("Saved 6 items: 1.0 GB, compressed and encrypted. The file is in your browser's downloads.", BackupText.Progress(done));
        Assert.Equal(1.0, BackupText.Fraction(done));
        Assert.Equal("The save failed: Docker stopped answering. The file it downloaded can't be used: delete it, and save again.",
            BackupText.Progress(running with { Problem = "Docker stopped answering." }));
    }

    [Fact]
    public void The_upload_says_how_far_it_is_and_why_it_failed()
    {
        Assert.Equal("Uploading gitberth-backup-2026-11-22.gbak: 128 MB of 512 MB (25%)",
            BackupText.Uploading("gitberth-backup-2026-11-22.gbak", 128L << 20, 512L << 20));
        Assert.Equal("Uploading the file: 0 KB of 0 KB (0%)", BackupText.Uploading(null, 0, 0));
        Assert.Equal(0.25, BackupText.UploadFraction(128, 512));
        Assert.Equal(0.0, BackupText.UploadFraction(5, 0));
        Assert.Equal("gitberth-backup.gbak is uploaded (512 MB).", BackupText.Uploaded("gitberth-backup.gbak", 512L << 20));
        Assert.Equal("A backup file is uploaded (2.0 GB).", BackupText.Uploaded(null, 2L << 30));

        // Gitberth's own sentence when it gave one; else what the status means. Never a page of HTML or JSON.
        Assert.Equal("The file wasn't uploaded. There isn't enough free space for it.", BackupText.UploadFailed(507, "There isn't enough free space for it.\n"));
        Assert.Equal("The file wasn't uploaded: it's larger than Gitberth takes.", BackupText.UploadFailed(413, ""));
        Assert.Equal("The file wasn't uploaded: Gitberth refused it. Reload the page and try again.", BackupText.UploadFailed(400, "<html><body>Bad</body></html>"));
        Assert.Equal("The file wasn't uploaded: the connection to Gitberth was lost.", BackupText.UploadFailed(0, null));
        Assert.Equal("The file wasn't uploaded: Gitberth couldn't keep it (error 500).", BackupText.UploadFailed(500, "{\"error\":\"x\"}"));
        Assert.Equal("The file wasn't uploaded: you aren't signed in any more. Reload the page and sign in.", BackupText.UploadFailed(401, null));
    }

    [Fact]
    public void A_check_that_fails_says_why_in_its_own_words_or_the_problems()
    {
        Assert.Equal("This passphrase doesn't open the file.", BackupText.CheckProblem(UploadProblem.WrongPassphrase, null));
        Assert.Equal("Made by Gitberth 1.9.0: update this one first.", BackupText.CheckProblem(UploadProblem.TooNew, "Made by Gitberth 1.9.0: update this one first."));
        Assert.Equal("This file isn't a Gitberth backup.", BackupText.CheckProblem(UploadProblem.NotABackup, " "));
        Assert.All(Enum.GetValues<UploadProblem>(), p => Assert.False(string.IsNullOrWhiteSpace(BackupText.CheckProblem(p, null))));
        Assert.Equal("The file is damaged", BackupText.CheckTitle(UploadProblem.Damaged));
        Assert.All(Enum.GetValues<RestoreWarningKind>(), k => Assert.NotEqual("Worth knowing", BackupText.WarningTitle(k)));
    }

    [Fact]
    public void A_summary_says_where_when_and_what_it_brings_back()
    {
        var summary = new BackupSummary("1.7.0", new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc), "homelab", "amd64", "29.0.1", Items, []);
        Assert.Equal("Saved on homelab, 22 Nov 2026 18:00, by Gitberth 1.7.0 (amd64, Docker 29.0.1).", BackupText.Made(summary, TimeZoneInfo.Utc));
        Assert.Equal("It brings back 2 apps, 2 volumes, 1 database server and 1 other container, with Gitberth's data.", BackupText.Holds(Items));
        Assert.Equal("It brings back 1 image only that host had, with Gitberth's data.",
            BackupText.Holds([Items[0], new("image:x", BackupItemKind.Image, "backup-agent:1.0", null, 1, false, true, null)]));
        Assert.Equal("It holds Gitberth's data alone.", BackupText.Holds([Items[0]]));
        Assert.Equal(["Gitberth brings back 2 apps, 2 volumes, 1 database server and 1 other container from the backup saved on homelab, with its own data.", BackupText.SignInNote],
            BackupText.ConfirmLines(summary));
        Assert.Contains("admin password of the machine the backup was saved on", BackupText.SignInNote);
    }

    [Fact]
    public void The_restoring_page_says_where_the_restore_stands()
    {
        RestoreStepView Step(string id, RestoreStepState state) => new(id, id, state, null);
        var running = new RestoreStatus(true, false, 1,
            [Step("a", RestoreStepState.Done), Step("b", RestoreStepState.Skipped), Step("c", RestoreStepState.Running), Step("d", RestoreStepState.Waiting)], 0, DateTime.UtcNow);
        Assert.Equal("Restoring, part 1 of 2", BackupText.Headline(running));
        Assert.Equal("2 of 4 steps done", BackupText.Steps(running));
        Assert.Contains("restarts on the saved data", BackupText.Lead(running));
        Assert.Contains("comes back by itself; then sign in with the admin password of the machine the backup was saved on", BackupText.RestartNote);
        Assert.Equal("Restoring, part 2 of 2", BackupText.Headline(running with { Phase = 2 }));
        Assert.Equal("Restored", BackupText.Headline(running with { Active = false, Finished = true }));
        Assert.Equal("Restored, but 1 step failed", BackupText.Headline(running with { Active = false, Finished = true, Failed = 1 }));
        Assert.Equal("Restored, but 2 steps failed", BackupText.Headline(running with { Active = false, Finished = true, Failed = 2 }));
        Assert.Equal("The restore stopped", BackupText.Headline(running with { Active = false }));
        Assert.Equal(["done", "now", "todo", "failed", "skipped"],
            new[] { RestoreStepState.Done, RestoreStepState.Running, RestoreStepState.Waiting, RestoreStepState.Failed, RestoreStepState.Skipped }.Select(BackupText.StepClass));
    }

    [Fact]
    public void The_dashboard_offers_a_restore_only_on_a_gitberth_that_could_take_one()
    {
        var empty = BackupText.Banner(null, null);
        Assert.Equal(("Moving from another machine? Restore a backup.", "Restore a backup", "settings#backup"), (empty?.Title, empty?.Action, empty?.Href));
        Assert.Null(BackupText.Banner(null, "This Gitberth has apps: a backup is restored onto a fresh install only."));
        var running = new RestoreStatus(true, false, 2, [new("a", "a", RestoreStepState.Done, null), new("b", "b", RestoreStepState.Running, null)], 0, DateTime.UtcNow);
        var busy = BackupText.Banner(running, "A restore is running.");
        Assert.Equal(("A restore is running", "Gitberth is bringing a backup back: 1 of 2 steps done.", "restoring"), (busy?.Title, busy?.Text, busy?.Href));
        // A restore that's over says nothing on the dashboard.
        Assert.Null(BackupText.Banner(running with { Active = false, Finished = true }, "This Gitberth has apps."));
    }

    [Fact]
    public void Picked_items_say_how_each_comes_back_and_that_nothing_restarts()
    {
        Assert.Equal("website comes back as it was.", BackupText.PickLine(new RestorePick("app:1", RestoreAction.Restore), "website"));
        Assert.Equal("website comes back as a copy named website2, next to it: its own container, volumes and database, free ports, no domains.",
            BackupText.PickLine(new RestorePick("app:1", RestoreAction.Copy, "website2"), "website"));
        Assert.StartsWith("website: its saved settings and data replace what it has now.", BackupText.PickLine(new RestorePick("app:1", RestoreAction.Replace), "website"));
        Assert.Equal(("Restore 1 item?", "Restore 3 items?"), (BackupText.PickTitle(1), BackupText.PickTitle(3)));
        // A copy's name is checked as New app checks an app's.
        Assert.Null(BackupText.CopyNameProblem("website2"));
        Assert.Equal("Give the app a name.", BackupText.CopyNameProblem(" "));
        Assert.Equal("The name needs at least one letter or digit.", BackupText.CopyNameProblem("--"));

        // The Restoring page of picked items: no parts, no restart.
        var running = new RestoreStatus(true, false, 1, [new("a", "a", RestoreStepState.Running, null)], 0, DateTime.UtcNow);
        Assert.Equal("Restoring the picked items", BackupText.Headline(running, picked: true));
        Assert.Equal(BackupText.PickedNote, BackupText.Lead(running, picked: true));
        Assert.Contains("no restart", BackupText.PickedNote);
        Assert.Equal("Restored", BackupText.Headline(running with { Active = false, Finished = true }, picked: true));
        Assert.Equal("The picked items are back. Each app's page says how it runs.", BackupText.Lead(running with { Active = false, Finished = true }, picked: true));
        // A whole backup's page as before.
        Assert.Equal("Restoring, part 1 of 2", BackupText.Headline(running));
    }

    [Fact]
    public void The_file_goes_up_and_down_at_gitberths_own_addresses()
    {
        Assert.Equal("backup/upload", BackupText.UploadUrl);
        Assert.Equal("backup/download/a1B2-c3_d4", BackupText.DownloadUrl("a1B2-c3_d4"));
        Assert.Equal("backup/download/a%2Fb", BackupText.DownloadUrl("a/b"));
    }
}
