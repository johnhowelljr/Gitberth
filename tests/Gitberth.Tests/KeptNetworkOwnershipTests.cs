using System.Net;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// R11: what Gitberth keeps for a container goes by its name (kept networks, its own LAN address, port notes). While a
/// change of that name isn't settled, they belong to whichever container ends up with the name: the original, which
/// recovery or [Put the original back] may give the name back, or the new one if it's kept. Removing the container that
/// holds the name meanwhile forgets none of them; once the change is over, removing it forgets them as ever.
/// </summary>
public sealed class KeptNetworkOwnershipTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>Forgetting asks Docker nothing.</summary>
    private sealed class NoEngine : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Docker was asked: {request.Method} {request.RequestUri}");
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly NetworkService _network;

    public KeptNetworkOwnershipTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            // postgres has its own LAN address, joins media as db, and listens on 9187 besides what its image says.
            db.KeptNetworks.AddRange(
                new KeptNetwork { ContainerName = "postgres", Network = "gitberth-lan", IPv4Address = "10.1.1.230" },
                new KeptNetwork { ContainerName = "postgres", Network = "media", Alias = "db" });
            db.ContainerPortNotes.Add(new ContainerPortNote { ContainerName = "postgres", Port = 9187 });
            db.SaveChanges();
        }
        _network = new NetworkService(new DockerApi("unix:///var/run/docker.sock", new NoEngine()), _db, new GitberthOptions { DataDir = Path.GetTempPath() });
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> ChangeAsync(ChangeStep step, ChangeOutcome outcome)
    {
        await using var db = _db.CreateDbContext();
        var change = new ContainerChangeEntry
        {
            ContainerName = "postgres", Summary = "published 5432/tcp on the LAN", OldId = "0ebf31bbcd07aaaa", NewId = "9c1d0000bbbb", OldName = "postgres_old_0ebf31bbcd07",
            TempName = "postgres_new_0ebf31bbcd07", WasRunning = false, OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""", Step = step, Outcome = outcome,
            StartedAt = DateTime.UtcNow,
        };
        db.ContainerChanges.Add(change);
        await db.SaveChangesAsync();
        return change.Id;
    }

    private (int Networks, int Notes) Kept()
    {
        using var db = _db.CreateDbContext();
        return (db.KeptNetworks.Count(k => k.ContainerName == "postgres"), db.ContainerPortNotes.Count(n => n.ContainerName == "postgres"));
    }

    [Theory]
    // A stopped postgres recreated, its new container waiting for its first start: the original is kept as postgres_old_….
    [InlineData(ChangeStep.WaitingForStart, ChangeOutcome.Running)]
    // Both kept, waiting for the owner to choose.
    [InlineData(ChangeStep.Finished, ChangeOutcome.NeedsDecision)]
    // Cut short by a restart of Gitberth, not settled yet.
    [InlineData(ChangeStep.Starting, ChangeOutcome.Running)]
    public async Task Removing_the_new_container_of_an_unsettled_change_keeps_what_the_name_has_for_the_original(ChangeStep step, ChangeOutcome outcome)
    {
        var id = await ChangeAsync(step, outcome);

        await _network.ForgetAsync("postgres");

        Assert.Equal((2, 1), Kept());

        // Once the change is over, removing the container that has the name forgets them.
        await using (var db = _db.CreateDbContext())
        {
            var change = await db.ContainerChanges.SingleAsync(c => c.Id == id);
            change.Outcome = ChangeOutcome.RolledBack;
            change.Step = ChangeStep.Finished;
            await db.SaveChangesAsync();
        }
        await _network.ForgetAsync("postgres");
        Assert.Equal((0, 0), Kept());
    }

    [Fact]
    public async Task Another_names_change_or_a_finished_one_doesnt_keep_them()
    {
        await ChangeAsync(ChangeStep.Finished, ChangeOutcome.Done);
        await using (var db = _db.CreateDbContext())
        {
            db.ContainerChanges.Add(new ContainerChangeEntry
            {
                ContainerName = "redis", Summary = "x", OldId = "a", OldName = "redis_old_a", TempName = "redis_new_a", OldRestartPolicy = "{}", Step = ChangeStep.WaitingForStart,
                Outcome = ChangeOutcome.Running, StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await _network.ForgetAsync("postgres");

        Assert.Equal((0, 0), Kept());
    }
}
