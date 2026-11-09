using Gitberth.Data;
using Gitberth.Network;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>The journal of port changes as Gitberth's database keeps it, so a restart of Gitberth finds an unfinished one.</summary>
public sealed class ChangeJournalTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private static readonly DateTime At = new(2026, 11, 5, 21, 14, 3, 250, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbChangeJournal _journal;

    public ChangeJournalTests()
    {
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _journal = new DbChangeJournal(db);
    }

    public void Dispose() => _connection.Dispose();

    private static ContainerChange Change(string name) => new()
    {
        ContainerName = name,
        Summary = "published 5432/tcp on the LAN",
        OldId = "0ebf31bbcd07" + new string('a', 52),
        OldName = $"{name}_old_0ebf31bbcd07",
        TempName = $"{name}_new_0ebf31bbcd07",
        WasRunning = true,
        OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""",
        Step = ChangeStep.Creating,
        StartedAt = At,
    };

    [Fact]
    public async Task Keeps_each_change_as_it_was_written()
    {
        var postgres = await _journal.AddAsync(Change("postgres"), CancellationToken.None);
        var sample = await _journal.AddAsync(Change("sample-index"), CancellationToken.None);
        Assert.Equal((1, 2), (postgres.Id, sample.Id));

        // One is rolled back while Gitberth stops; the other finishes.
        var rollingBack = postgres with
        {
            NewId = "f00d" + new string('b', 60), Step = ChangeStep.RollingBack, StopAt = At.AddSeconds(1), Error = "The new postgres stopped after 4 s (exit code 1).",
            Notice = "Gitberth restarted while it recreated postgres.",
        };
        await _journal.UpdateAsync(rollingBack, CancellationToken.None);
        var done = sample with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done, OfflineMs = 2100, FinishedAt = At.AddSeconds(6) };
        await _journal.UpdateAsync(done, CancellationToken.None);

        // Read back field for field: only the unfinished one is left for recovery, oldest first.
        Assert.Equal([rollingBack], await _journal.UnfinishedAsync(CancellationToken.None));
        Assert.Equal(done, await _journal.LatestAsync("sample-index", CancellationToken.None));
        // A container's newest change is its latest; one that never changed has none.
        var again = await _journal.AddAsync(Change("sample-index") with { Outcome = ChangeOutcome.NeedsDecision }, CancellationToken.None);
        Assert.Equal(again, await _journal.LatestAsync("sample-index", CancellationToken.None));
        Assert.Equal([rollingBack, again], await _journal.UnfinishedAsync(CancellationToken.None));
        Assert.Null(await _journal.LatestAsync("postgres-2", CancellationToken.None));
    }

    [Fact]
    public async Task Keeps_what_kind_of_change_it_is_and_its_setting_names_never_their_values()
    {
        var update = await _journal.AddAsync(Change("postgres") with
        {
            Kind = Apps.ChangeKind.Update,
            Fields = "Env,Image,RestartPolicy",
            NewRestartPolicy = """{"Name":"always","MaximumRetryCount":0}""",
            ImageBefore = "postgres:18",
            ImageAfter = "postgres:18",
        }, CancellationToken.None);

        Assert.Equal(update, await _journal.LatestAsync("postgres", CancellationToken.None));
        // A change written before kinds were kept reads as a port change.
        var old = await _journal.AddAsync(Change("sample-index"), CancellationToken.None);
        Assert.Equal((Apps.ChangeKind.Ports, null, null), (old.Kind, old.Fields, old.NewRestartPolicy));
        Assert.Equal(Apps.ChangeKind.Ports, (await _journal.LatestAsync("sample-index", CancellationToken.None))!.Kind);
    }

    [Fact]
    public async Task Taking_a_banner_down_leaves_the_rest_of_the_change_as_it_is_by_then()
    {
        // The owner reads the banner of a change that waits for a decision; meanwhile [Keep the new one] finishes it.
        var waiting = await _journal.AddAsync(Change("postgres") with { Outcome = ChangeOutcome.NeedsDecision, Notice = "A port change on postgres didn't finish." },
            CancellationToken.None);
        var done = waiting with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done, Error = null, FinishedAt = At.AddSeconds(9) };
        await _journal.UpdateAsync(done, CancellationToken.None);

        await _journal.DismissNoticeAsync(waiting.Id, CancellationToken.None);

        Assert.Equal(done with { Notice = null }, await _journal.LatestAsync("postgres", CancellationToken.None));
    }

    [Fact]
    public async Task Finds_the_change_a_container_came_from_by_its_id()
    {
        var first = await _journal.AddAsync(Change("postgres") with { NewId = "b" + new string('1', 63), Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done },
            CancellationToken.None);
        // The next change of postgres replaced the container the first one made.
        var second = await _journal.AddAsync(Change("postgres") with { OldId = first.NewId!, NewId = "c" + new string('2', 63) }, CancellationToken.None);

        Assert.Equal(first, await _journal.WithContainerAsync(first.OldId, CancellationToken.None));
        Assert.Equal(second, await _journal.WithContainerAsync(first.NewId!, CancellationToken.None));
        Assert.Equal(second, await _journal.WithContainerAsync(second.NewId!, CancellationToken.None));
        Assert.Null(await _journal.WithContainerAsync("d" + new string('3', 63), CancellationToken.None));
    }
}
