using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Gitberth.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The audit log's storage: entries numbered and hashed onto the one before, a check that finds the first entry changed
/// or missing, a prune that keeps the rest checkable, the page's queries, and the exports.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-audit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<AuditLog> _logs = [];

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Gone already, or still held for a moment: it's a temp folder.
        }
    }

    // The log Gitberth runs: its file in the data folder, its writer in the background.
    private AuditLog FileLog(TimeSpan? retryCap = null, Func<Task>? beforeBatch = null, Func<AuditDraft, Exception?>? fault = null)
    {
        Directory.CreateDirectory(_dir);
        var log = new AuditLog(new GitberthOptions { DataDir = _dir }, NullLogger<AuditLog>.Instance)
        {
            RetryCap = retryCap ?? TimeSpan.FromSeconds(30),
            BeforeBatch = beforeBatch,
            StoreFault = fault,
        };
        _logs.Add(log);
        return log;
    }

    private string DbPath => Path.Combine(_dir, "audit.db");

    private void Tamper(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static AuditActor Admin => AuditActor.User("admin", "10.1.1.5");

    [Fact]
    public void An_in_memory_log_keeps_each_entry_at_once_with_its_actor_time_and_number()
    {
        var log = new AuditLog();
        using (AuditScope.Begin(Admin))
            log.Record(new AuditEntry("app.created", "Created web from the image nginx:1.27.") { AppId = 3, AppName = "web", Container = "gb-web" });
        log.Record(new AuditEntry("app.deploy.queued", "Queued a deploy of web.") { Actor = AuditActor.System(AuditVia.Watcher), AppId = 3 });

        var entries = log.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal([1L, 2L], entries.Select(e => e.Id));
        Assert.Equal(Admin, entries[0].Actor);
        Assert.Equal("gb-web", entries[0].Container);
        Assert.Equal(AuditActor.System(AuditVia.Watcher), entries[1].Actor);
        Assert.True(DateTime.UtcNow - entries[0].At < TimeSpan.FromMinutes(1));
        Assert.Equal(AuditHash.Genesis, entries[0].PrevHash);
        Assert.Equal(entries[0].Hash, entries[1].PrevHash);
    }

    [Fact]
    public void Recording_never_throws_and_cleans_what_it_keeps()
    {
        var log = new AuditLog();
        log.Record(null!);
        log.Record(new AuditEntry("App.Renamed", "Renamed\nweb\tto shop.") { Target = new string('x', 5000) });

        var entry = Assert.Single(log.Entries);
        Assert.Equal("app.renamed", entry.Action);
        Assert.Equal("Renamed web to shop.", entry.Summary);
        Assert.True(entry.Target!.Length <= AuditLog.TextMax);
        Assert.EndsWith("…", entry.Target);
    }

    [Fact]
    public async Task An_untouched_log_checks_out_and_a_changed_entry_is_found()
    {
        var log = FileLog();
        for (var i = 1; i <= 5; i++) log.Record(new AuditEntry("app.started", $"Started app {i}.") { Actor = Admin });
        Assert.True(await log.FlushAsync());

        var ok = await log.VerifyAsync();
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(5, ok.Checked);

        Tamper("UPDATE entries SET summary = 'Started nothing.' WHERE id = 3");
        var broken = await log.VerifyAsync();
        Assert.False(broken.Ok);
        Assert.Equal(3, broken.BrokenId);
        Assert.Contains("changed after it was recorded", broken.Message);
    }

    [Fact]
    public async Task A_removed_entry_breaks_the_chain_at_the_one_after_it()
    {
        var log = FileLog();
        for (var i = 1; i <= 5; i++) log.Record(new AuditEntry("app.stopped", $"Stopped app {i}."));
        await log.FlushAsync();

        Tamper("DELETE FROM entries WHERE id = 2");
        var broken = await log.VerifyAsync();
        Assert.False(broken.Ok);
        Assert.Equal(3, broken.BrokenId);
        Assert.Contains("doesn't follow", broken.Message);
    }

    [Fact]
    public async Task Removing_the_newest_entries_is_found_too()
    {
        var log = FileLog();
        for (var i = 1; i <= 4; i++) log.Record(new AuditEntry("app.stopped", $"Stopped app {i}."));
        await log.FlushAsync();

        Tamper("DELETE FROM entries WHERE id >= 3");
        var broken = await log.VerifyAsync();
        Assert.False(broken.Ok);
        Assert.Contains("newest entries are missing", broken.Message);
    }

    [Fact]
    public async Task A_prune_removes_the_old_run_keeps_an_anchor_and_says_so()
    {
        var log = new AuditLog();
        var old = DateTime.UtcNow.AddDays(-400);
        log.Record(new AuditEntry("app.created", "Created a.") { At = old });
        log.Record(new AuditEntry("app.created", "Created b.") { At = old.AddMinutes(1) });
        log.Record(new AuditEntry("app.created", "Created c."));
        // Recorded later with an old time (a replayed Docker event): it waits until the run before it goes.
        log.Record(new AuditEntry("docker.container.started", "Started outside Gitberth: d.") { At = old.AddMinutes(2), Actor = AuditActor.Outside });
        log.Record(new AuditEntry("app.created", "Created e."));

        Assert.Equal(2, await log.PruneAsync(365));

        var entries = log.Entries;
        Assert.Equal(["Created c.", "Started outside Gitberth: d.", "Created e."], entries.Take(3).Select(e => e.Summary));
        var pruned = entries[^1];
        Assert.Equal("audit.pruned", pruned.Action);
        Assert.Contains("Removed 2 entries older than 365 days", pruned.Summary);
        Assert.NotEqual(AuditHash.Genesis, entries[0].PrevHash);
        Assert.True((await log.VerifyAsync()).Ok);
        // Nothing older than the cutoff at the start any more: nothing to remove, nothing recorded.
        var count = log.Entries.Count;
        Assert.Equal(0, await log.PruneAsync(365 * 2));
        Assert.Equal(count, log.Entries.Count);
    }

    [Fact]
    public async Task A_prune_of_everything_still_leaves_a_log_that_checks_out()
    {
        var log = FileLog();
        log.Record(new AuditEntry("app.created", "Created a.") { At = DateTime.UtcNow.AddDays(-90) });
        log.Record(new AuditEntry("app.created", "Created b.") { At = DateTime.UtcNow.AddDays(-80) });
        await log.FlushAsync();

        Assert.Equal(2, await log.PruneAsync(30));
        await log.FlushAsync();
        var left = Assert.Single(log.Entries);
        Assert.Equal("audit.pruned", left.Action);
        Assert.True((await log.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task The_writer_keeps_every_entry_in_order_from_many_threads_and_after_a_restart()
    {
        var log = FileLog();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++) log.Record(new AuditEntry("app.started", $"Started {t}-{i}."));
        })));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        var entries = log.Entries;
        Assert.Equal(2000, entries.Count);
        Assert.Equal(Enumerable.Range(1, 2000).Select(i => (long)i), entries.Select(e => e.Id));
        log.Dispose();

        // Gitberth starts again: the chain goes on from where it ended.
        var again = FileLog();
        again.Record(new AuditEntry("app.stopped", "Stopped after the restart."));
        await again.FlushAsync();
        var all = again.Entries;
        Assert.Equal(2001, all[^1].Id);
        Assert.Equal(all[^2].Hash, all[^1].PrevHash);
        Assert.True((await again.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Nothing_is_lost_while_the_file_cant_be_written_for_long_and_all_of_it_goes_in_in_order_after()
    {
        var gate = Task.CompletedTask;
        var log = FileLog(retryCap: TimeSpan.FromSeconds(2), beforeBatch: () => gate);
        log.Record(new AuditEntry("app.created", "Before the disk failed."));
        Assert.True(await log.FlushAsync());
        var good = await File.ReadAllBytesAsync(DbPath);

        // The disk fails, and stays failing for longer than the writer once took to give up on an entry (about 40 s): the
        // file is still there, but no write of it works (SQLite says it isn't a database, as it says a full or failing
        // disk's write failed). Thirty entries come meanwhile, all in one batch, as they do when many come at once.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate = hold.Task;
        await File.WriteAllBytesAsync(DbPath, Enumerable.Repeat((byte)'x', good.Length).ToArray());
        for (var i = 1; i <= 30; i++) log.Record(new AuditEntry("app.started", $"Started app {i}."));
        hold.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(45));
        Assert.Equal(30, log.Pending);

        // The disk works again: every entry goes in, in order, and the chain checks out.
        foreach (var extra in new[] { "-wal", "-shm" }) File.Delete(DbPath + extra);
        await File.WriteAllBytesAsync(DbPath, good);
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        var entries = log.Entries;
        Assert.Equal(["Before the disk failed.", .. Enumerable.Range(1, 30).Select(i => $"Started app {i}.")], entries.Select(e => e.Summary));
        Assert.Equal(Enumerable.Range(1, 31).Select(i => (long)i), entries.Select(e => e.Id));
        var check = await log.VerifyAsync();
        Assert.True(check.Ok, check.Message);
    }

    [Fact]
    public async Task Stopping_tries_what_waits_again_at_once_rather_than_after_its_wait()
    {
        var log = FileLog();
        log.Record(new AuditEntry("app.created", "Before the disk failed."));
        Assert.True(await log.FlushAsync());
        var good = await File.ReadAllBytesAsync(DbPath);
        await File.WriteAllBytesAsync(DbPath, Enumerable.Repeat((byte)'x', good.Length).ToArray());
        log.Record(new AuditEntry("app.started", "While it failed."));
        // Tried at once, 2 s and 6 s later: the next try is 6 s away when the disk works again and Gitberth stops.
        await Task.Delay(TimeSpan.FromSeconds(7));
        foreach (var extra in new[] { "-wal", "-shm" }) File.Delete(DbPath + extra);
        await File.WriteAllBytesAsync(DbPath, good);

        var stopping = System.Diagnostics.Stopwatch.StartNew();
        await log.StopAsync(CancellationToken.None);
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(4), $"Stopping took {stopping.Elapsed}.");
        Assert.Equal(0, log.Pending);
        Assert.Equal(["Before the disk failed.", "While it failed."], log.Entries.Select(e => e.Summary));
    }

    [Fact]
    public async Task A_file_removed_while_Gitberth_runs_is_made_again_and_the_log_goes_on_in_it()
    {
        var log = FileLog(retryCap: TimeSpan.FromMilliseconds(500));
        log.Record(new AuditEntry("app.created", "In the old file."));
        Assert.True(await log.FlushAsync());
        foreach (var file in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" }) File.Delete(file);

        log.Record(new AuditEntry("app.created", "In the new file."));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        var entry = Assert.Single(log.Entries);
        Assert.Equal((1L, "In the new file.", AuditHash.Genesis), (entry.Id, entry.Summary, entry.PrevHash));
        Assert.True((await log.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task An_entry_the_file_refuses_while_it_takes_the_others_is_the_only_one_left_out()
    {
        var log = FileLog(retryCap: TimeSpan.FromMilliseconds(200),
            fault: d => d.Summary.StartsWith("Refused", StringComparison.Ordinal) ? new SqliteException("datatype mismatch", SQLitePCL.raw.SQLITE_MISMATCH) : null);
        foreach (var summary in new[] { "One.", "Refused one.", "Two.", "Three." }) log.Record(new AuditEntry("app.started", summary));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(["One.", "Two.", "Three."], log.Entries.Select(e => e.Summary));

        // Refused with nothing after it, it waits: the next entry shows the file works, and only then is it left out.
        log.Record(new AuditEntry("app.started", "Refused last."));
        Assert.False(await log.FlushAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, log.Pending);
        log.Record(new AuditEntry("app.started", "Four."));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(["One.", "Two.", "Three.", "Four."], log.Entries.Select(e => e.Summary));
        Assert.True((await log.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task When_the_file_refuses_every_entry_it_is_the_file_and_nothing_is_left_out()
    {
        var refusing = new StrongBox<bool>(true);
        var log = FileLog(retryCap: TimeSpan.FromMilliseconds(200),
            fault: _ => Volatile.Read(ref refusing.Value) ? new SqliteException("no such table: entries", SQLitePCL.raw.SQLITE_ERROR) : null);
        for (var i = 1; i <= 5; i++) log.Record(new AuditEntry("app.started", $"Started app {i}."));
        // Long enough for the entries to be tried one by one several times over.
        Assert.False(await log.FlushAsync(TimeSpan.FromSeconds(12)));
        Assert.Equal(5, log.Pending);

        Volatile.Write(ref refusing.Value, false);
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"Started app {i}."), log.Entries.Select(e => e.Summary));
    }

    [Fact]
    public void A_full_failing_read_only_or_held_file_is_the_files_fault_never_an_entrys()
    {
        foreach (var code in new[] { SQLitePCL.raw.SQLITE_FULL, SQLitePCL.raw.SQLITE_IOERR, SQLitePCL.raw.SQLITE_READONLY, SQLitePCL.raw.SQLITE_CANTOPEN,
                     SQLitePCL.raw.SQLITE_BUSY, SQLitePCL.raw.SQLITE_LOCKED, SQLitePCL.raw.SQLITE_NOTADB, SQLitePCL.raw.SQLITE_CORRUPT })
            Assert.True(AuditLog.IsStoreFailure(new SqliteException("the file", code)), code.ToString());
        // An extended code (a failed write, a read-only remount) is its primary one's.
        Assert.True(AuditLog.IsStoreFailure(new SqliteException("disk I/O error", SQLitePCL.raw.SQLITE_IOERR_WRITE)));
        Assert.True(AuditLog.IsStoreFailure(new SqliteException("attempt to write a readonly database", SQLitePCL.raw.SQLITE_READONLY_DBMOVED)));
        Assert.True(AuditLog.IsStoreFailure(new IOException("No space left on device")));
        Assert.True(AuditLog.IsStoreFailure(new UnauthorizedAccessException()));
        // What an entry could cause is tried entry by entry instead.
        Assert.False(AuditLog.IsStoreFailure(new SqliteException("constraint failed", SQLitePCL.raw.SQLITE_CONSTRAINT)));
        Assert.False(AuditLog.IsStoreFailure(new ArgumentException("bad text")));
    }

    [Fact]
    public async Task An_entry_cut_inside_an_emoji_or_holding_half_of_one_checks_out_in_memory_and_on_disk()
    {
        // Cut just after the first half of an emoji: the whole emoji goes, never half of it.
        var memory = new AuditLog();
        memory.Record(new AuditEntry("app.renamed", "Renamed.") { Target = new string('x', AuditLog.TextMax - 2) + "\U0001F600tail" });
        Assert.Equal(new string('x', AuditLog.TextMax - 2) + "…", Assert.Single(memory.Entries).Target);
        var inMemory = await memory.VerifyAsync();
        Assert.True(inMemory.Ok, inMemory.Message);

        var log = FileLog();
        log.Record(new AuditEntry("app.renamed", new string('y', AuditLog.SummaryMax - 2) + "\U0001F680more"));
        // Half of one that came in alone (a cut made elsewhere) is kept as SQLite keeps it: U+FFFD.
        log.Record(new AuditEntry("app.renamed", "Renamed \uD83D to shop.") { AppName = "web\uDE00", Changes = [new AuditChange("Name", "a\uD800", "b")] });
        // An emoji within the limits stays as it is.
        log.Record(new AuditEntry("app.renamed", "Renamed \U0001F680 to shop.") { Target = "\U0001F600" });
        Assert.True(await log.FlushAsync());
        var onDisk = await log.VerifyAsync();
        Assert.True(onDisk.Ok, onDisk.Message);
        var entries = log.Entries;
        Assert.Equal(new string('y', AuditLog.SummaryMax - 2) + "…", entries[0].Summary);
        Assert.Equal(("Renamed � to shop.", "web�", "a�"), (entries[1].Summary, entries[1].AppName, entries[1].Changes[0].From));
        Assert.Equal(("Renamed \U0001F680 to shop.", "\U0001F600"), (entries[2].Summary, entries[2].Target));
        Assert.All(entries, e => Assert.False(HasHalf(e.Summary + e.AppName + e.Target), e.Summary));
    }

    [Fact]
    public void Clean_never_leaves_half_a_character_wherever_it_cuts()
    {
        var text = string.Concat(Enumerable.Repeat("a\U0001F600", 40));
        for (var max = 2; max < text.Length + 2; max++)
        {
            var clean = AuditLog.Clean(text, max)!;
            Assert.True(clean.Length <= max, $"{max}: {clean.Length}");
            Assert.False(HasHalf(clean), $"{max}: {clean}");
        }
    }

    // Half of a character: a first half without its second, or a second without its first.
    private static bool HasHalf(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(text[i])) return true;
        }
        return false;
    }

    [Fact]
    public async Task Turned_off_changes_made_outside_Gitberth_are_left_out_and_everything_else_stays()
    {
        var log = new AuditLog { RecordOutside = false };
        log.Record(new AuditEntry("docker.container.stopped", "Stopped outside Gitberth: db.") { Actor = AuditActor.Outside });
        log.Record(new AuditEntry("app.stopped", "Stopped web."));
        Assert.Equal(["app.stopped"], log.Entries.Select(e => e.Action));
        Assert.Equal(1, await log.CountAsync(new AuditQuery()));
    }

    // ---- Queries ----

    private static AuditLog Sample()
    {
        var log = new AuditLog();
        var now = DateTime.UtcNow;
        log.Record(new AuditEntry("auth.signin.failed", "A sign-in from 10.1.1.9 failed: wrong password.") { Actor = AuditActor.User("admin", "10.1.1.9"), Outcome = AuditOutcome.Failed, At = now.AddDays(-10) });
        log.Record(new AuditEntry("app.created", "Created shop from the image nginx:1.27.") { Actor = Admin, AppId = 1, AppName = "shop", Container = "gb-shop", At = now.AddDays(-3) });
        log.Record(new AuditEntry("app.deploy.queued", "Queued a deploy of shop.") { Actor = AuditActor.System(AuditVia.Watcher), AppId = 1, AppName = "shop", Container = "gb-shop", At = now.AddHours(-30) });
        log.Record(new AuditEntry("app.deploy.failed", "Deploy #4 of shop failed: the build failed.") { Actor = AuditActor.System(AuditVia.Watcher), AppId = 1, Container = "gb-shop", Outcome = AuditOutcome.Failed, At = now.AddHours(-29) });
        log.Record(new AuditEntry("app.domain.added", "Added the domain shop.example.com to shop.") { Actor = Admin, AppId = 1, AppName = "shop", Container = "gb-shop", Target = "shop.example.com", At = now.AddHours(-2) });
        log.Record(new AuditEntry("docker.container.stopped", "Stopped outside Gitberth: postgres.") { Actor = AuditActor.Outside, Container = "postgres", At = now.AddHours(-1) });
        log.Record(new AuditEntry("settings.updates.changed", "Changed the update settings: 100% of them.") { Actor = Admin, At = now.AddMinutes(-5) });
        // An app's container renamed since: its old entries name it by its id only.
        log.Record(new AuditEntry("app.renamed", "Renamed shop to store.") { Actor = Admin, AppId = 1, AppName = "store", At = now });
        return log;
    }

    private static async Task<List<string>> Actions(AuditLog log, AuditQuery q) => (await log.QueryAsync(q)).Entries.Select(e => e.Action).ToList();

    [Fact]
    public async Task Queries_filter_by_time_actor_app_area_outcome_and_words_newest_first()
    {
        var log = Sample();
        var now = DateTime.UtcNow;

        Assert.Equal("app.renamed", (await Actions(log, new AuditQuery()))[0]);
        Assert.Equal(["app.renamed", "settings.updates.changed", "docker.container.stopped", "app.domain.added"],
            await Actions(log, new AuditQuery { From = now.AddDays(-1) }));
        Assert.Equal(["auth.signin.failed"], await Actions(log, new AuditQuery { To = now.AddDays(-5) }));
        Assert.Equal(["app.deploy.failed", "app.deploy.queued"], await Actions(log, new AuditQuery { ActorKind = AuditActorKind.Gitberth, Actor = "Gitberth" }));
        Assert.Equal(["app.domain.added", "app.deploy.failed", "app.deploy.queued", "app.created"], await Actions(log, new AuditQuery { Container = "gb-shop" }));
        Assert.Equal(5, (await log.QueryAsync(new AuditQuery { AppId = 1, Container = "gb-shop" })).Entries.Count);
        Assert.Equal(["app.deploy.failed", "app.deploy.queued"], await Actions(log, new AuditQuery { ActionPrefix = "app.deploy." }));
        Assert.Equal(["app.deploy.queued"], await Actions(log, new AuditQuery { ActionPrefix = "app.deploy.queued" }));
        Assert.Equal(["app.renamed", "app.created"], await Actions(log, new AuditQuery { Area = AuditArea.Apps }));
        Assert.Equal(["app.deploy.failed", "app.deploy.queued"], await Actions(log, new AuditQuery { Area = AuditArea.Deploys }));
        Assert.Equal(["app.domain.added"], await Actions(log, new AuditQuery { Area = AuditArea.Domains }));
        Assert.Equal(["settings.updates.changed"], await Actions(log, new AuditQuery { Area = AuditArea.Settings }));
        Assert.Equal(["auth.signin.failed"], await Actions(log, new AuditQuery { Area = AuditArea.SignIns }));
        Assert.Equal(["docker.container.stopped"], await Actions(log, new AuditQuery { Area = AuditArea.Outside }));
        Assert.Equal(["docker.container.stopped"], await Actions(log, new AuditQuery { OutsideOnly = true }));
        Assert.Equal(["app.deploy.failed", "auth.signin.failed"], await Actions(log, new AuditQuery { Outcome = AuditOutcome.Failed }));
        Assert.Equal(["app.domain.added"], await Actions(log, new AuditQuery { Text = "example.com" }));
        Assert.Equal(["auth.signin.failed"], await Actions(log, new AuditQuery { Text = "10.1.1.9" }));
        // Words are words: % and _ match only themselves.
        Assert.Equal(["settings.updates.changed"], await Actions(log, new AuditQuery { Text = "100%" }));
        Assert.Empty(await Actions(log, new AuditQuery { Text = "_" }));
        Assert.Equal(["app.deploy.failed", "app.deploy.queued"], await Actions(log, new AuditQuery { Area = AuditArea.Deploys, Container = "gb-shop", From = now.AddDays(-2) }));
    }

    [Fact]
    public async Task Pages_go_back_by_number_and_the_count_covers_them_all()
    {
        var log = Sample();
        var first = await log.QueryAsync(new AuditQuery { Limit = 3 });
        Assert.True(first.More);
        Assert.Equal([8L, 7L, 6L], first.Entries.Select(e => e.Id));
        var second = await log.QueryAsync(new AuditQuery { Limit = 3, BeforeId = first.Entries[^1].Id });
        Assert.Equal([5L, 4L, 3L], second.Entries.Select(e => e.Id));
        var last = await log.QueryAsync(new AuditQuery { Limit = 3, BeforeId = second.Entries[^1].Id });
        Assert.False(last.More);
        Assert.Equal([2L, 1L], last.Entries.Select(e => e.Id));
        Assert.Equal(8, await log.CountAsync(new AuditQuery { Limit = 3, BeforeId = 4 }));
        Assert.Equal(4, await log.CountAsync(new AuditQuery { Container = "gb-shop" }));
    }

    [Fact]
    public async Task The_page_lists_who_and_which_apps_the_log_names()
    {
        var log = Sample();
        var actors = await log.ActorsAsync();
        Assert.Equal(new AuditActorRef(AuditActorKind.User, "admin"), actors[0]);
        Assert.Contains(new AuditActorRef(AuditActorKind.Docker, "outside Gitberth"), actors);
        Assert.Contains(new AuditActorRef(AuditActorKind.Gitberth, "Gitberth"), actors);
        var apps = await log.AppsAsync();
        Assert.Equal(["postgres", "gb-shop"], apps.Select(a => a.Container));
        Assert.Equal("shop", apps.Single(a => a.Container == "gb-shop").Name);
        var summary = await log.SummaryAsync();
        Assert.Equal(8, summary.Count);
        Assert.True(summary.Oldest < summary.Newest);
    }

    [Fact]
    public void The_page_filters_travel_in_the_address_and_back()
    {
        var filter = new AuditFilter { Range = AuditRange.All, Who = "User:admin", App = "gb-web", Area = AuditArea.Outside, Outcome = AuditOutcome.Failed, Text = "shop now" };
        var query = filter.ToQueryString();
        Assert.Equal("range=all&who=User%3Aadmin&app=gb-web&area=outside&outcome=failed&q=shop%20now", query);
        var back = System.Web.HttpUtility.ParseQueryString(query);
        Assert.Equal(filter, AuditFilter.Parse(back["range"], back["who"], back["app"], back["area"], back["outcome"], back["q"]));
        Assert.Equal("", AuditFilter.Default.ToQueryString());
        Assert.Equal(AuditFilter.Default, AuditFilter.Parse("nonsense", "Nobody:x", " ", "nope", "maybe", " "));

        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var q = filter.ToQuery(now) with { };
        Assert.Null(q.From);
        Assert.Equal((AuditActorKind.User, "admin"), (q.ActorKind!.Value, q.Actor!));
        Assert.Equal(now.AddDays(-7), AuditFilter.Default.ToQuery(now).From);
        Assert.Equal("audit/export?format=jsonl&range=all&app=gb-web", AuditEndpoints.ExportUrl(AuditFilter.Default with { Range = AuditRange.All, App = "gb-web" }, AuditExportFormat.JsonLines));
    }

    // ---- Exports ----

    [Fact]
    public async Task An_export_has_every_entry_of_the_view_as_csv_or_json_lines()
    {
        var log = new AuditLog();
        for (var i = 0; i < 1003; i++) log.Record(new AuditEntry("app.started", $"Started app {i}.") { Actor = Admin });
        log.Record(new AuditEntry("app.renamed", "Renamed \"web\", the front end, to =shop.")
        {
            Actor = Admin, Target = "=HYPERLINK(\"x\")", Changes = [new AuditChange("Name", "web", "=shop")],
        });

        using var csv = new MemoryStream();
        await log.ExportAsync(new AuditQuery(), AuditExportFormat.Csv, csv);
        var lines = Encoding.UTF8.GetString(csv.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(AuditExport.CsvHeader, lines[0]);
        Assert.Equal(1 + 1004, lines.Count);
        // The newest first: the rename, quoted where it must be, and never read as a formula.
        Assert.StartsWith("1004,", lines[1]);
        Assert.Contains("\"Renamed \"\"web\"\", the front end, to =shop.\"", lines[1]);
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", lines[1]);
        Assert.Contains("Name: web → =shop", lines[1]);

        using var jsonl = new MemoryStream();
        await log.ExportAsync(new AuditQuery { ActionPrefix = "app.renamed" }, AuditExportFormat.JsonLines, jsonl);
        var line = Assert.Single(Encoding.UTF8.GetString(jsonl.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("app.renamed", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("admin", doc.RootElement.GetProperty("actor").GetString());
        Assert.Equal("10.1.1.5", doc.RootElement.GetProperty("ip").GetString());
        Assert.Equal("=shop", doc.RootElement.GetProperty("changes")[0].GetProperty("to").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("hash").GetString()!.Length);
    }

    [Fact]
    public void The_hash_covers_every_field_and_the_one_before()
    {
        var row = new AuditHash.Row(7, 1763112000000, "User", "admin", "UI", "10.1.1.5", "app.renamed", "Succeeded", 3, "web", "gb-web", "web", "Renamed web.", null, null);
        var hash = AuditHash.Of(AuditHash.Genesis, row);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, AuditHash.Of(AuditHash.Genesis, row));
        Assert.NotEqual(hash, AuditHash.Of(new string('1', 64), row));
        Assert.NotEqual(hash, AuditHash.Of(AuditHash.Genesis, row with { Ip = "10.1.1.6" }));
        Assert.NotEqual(hash, AuditHash.Of(AuditHash.Genesis, row with { AppId = null }));
        Assert.NotEqual(hash, AuditHash.Of(AuditHash.Genesis, row with { Id = 8 }));
        Assert.Equal("""[7,1763112000000,"User","admin","UI","10.1.1.5","app.renamed","Succeeded",3,"web","gb-web","web","Renamed web.",null,null]""",
            AuditHash.Canonical(row));
    }

    [Fact]
    public async Task Retention_is_kept_within_its_limits_and_its_change_is_recorded()
    {
        Assert.Equal(365, AuditSettings.Retention(null));
        Assert.Equal(30, AuditSettings.Retention("2"));
        Assert.Equal(3650, AuditSettings.Retention("99999"));
        Assert.Equal(90, AuditSettings.Retention("90"));
        Assert.NotNull(AuditSettings.RetentionProblem(29));
        Assert.Null(AuditSettings.RetentionProblem(3650));

        using var rig = new AppsRig();
        var log = new AuditLog();
        var settings = new AuditSettings(rig.Settings, log);
        await settings.LoadAsync();
        Assert.Equal((365, true), (settings.RetentionDays, settings.RecordOutside));
        Assert.NotNull(await settings.SaveAsync(10, true));
        Assert.Null(await settings.SaveAsync(90, false));
        Assert.False(log.RecordOutside);

        var again = new AuditSettings(rig.Settings, new AuditLog());
        await again.LoadAsync();
        Assert.Equal((90, false), (again.RetentionDays, again.RecordOutside));
        var entries = log.Entries.Where(e => e.Action == "settings.audit.changed").ToList();
        Assert.Equal([AuditOutcome.Failed, AuditOutcome.Succeeded], entries.Select(e => e.Outcome));
        Assert.Equal([("Keep entries for", "365 days", "90 days"), ("Record changes made outside Gitberth", "on", "off")],
            entries[1].Changes.Select(c => (c.Field, c.From!, c.To!)));
    }
}
