using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The seams between the builders' parts, as they're wired together: the change engine's journal (B) on the database the
/// UnifiedApps migration makes (A), and the settings service (B) over the real catalog, adoption, Activity and Git apps'
/// saves (A), against CT 101's containers (<see cref="AppsRig"/>). Docker answers reads from the fixtures and takes only
/// docker update; nothing else is ever changed.
/// </summary>
public sealed class IntegrationSeamTests : IDisposable
{
    private static readonly DateTime At = new(2026, 11, 7, 6, 0, 0, 125, DateTimeKind.Utc);

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>The rig's engine, with docker update answered (and recorded) as Docker does it; every other call goes on to it.</summary>
    private sealed class Updates : DelegatingHandler
    {
        public List<(string Path, JsonObject Body)> Made { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Post || !path.EndsWith("/update", StringComparison.Ordinal)) return await base.SendAsync(request, cancellationToken);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (Made) Made.Add((path, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Warnings":[]}""", Encoding.UTF8, "application/json") };
        }
    }

    private readonly AppsRig _rig = new();
    private readonly Updates _updates;
    private readonly AppSettingsService _service;

    public IntegrationSeamTests()
    {
        _updates = new Updates { InnerHandler = _rig.Engine };
        var engine = new DockerEngine(new DockerApi("unix:///var/run/docker.sock", _updates));
        var host = new FakeHost(new FakeEngine(new FakeClock()));
        PortChanger? changer = null;
        // As Program.cs wires them: the updater writes Activity, and the engine hands live plans to it.
        var updater = new ContainerUpdater(engine, _rig.ContainerLocks, new Lazy<PortChanger>(() => changer!), _rig.Activity, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, _rig.Journal, _rig.ContainerLocks, host, host, host, host, _rig.Containers, _rig.Options, NullLogger<PortChanger>.Instance,
            updater: updater);
        _service = new AppSettingsService(_rig.Catalog, _rig.Apps, new Lazy<AppActions>(() => _rig.Actions), changer, updater, engine, _rig.AppLocks, _rig.Domains,
            _rig.Health, _rig.Restarts, _rig.Protector, _rig.Db, _rig.Options, _rig.Activity, NullLogger<AppSettingsService>.Instance);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task The_journal_keeps_a_changes_kind_and_setting_names_in_the_migrated_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        using (var context = db.CreateDbContext()) context.Database.Migrate();
        var journal = new DbChangeJournal(db);
        var change = new ContainerChange
        {
            ContainerName = "postgres",
            Summary = "updated postgres:18 (3a42â€¦ â†’ 9bc1â€¦)",
            OldId = new string('a', 64),
            OldName = "postgres_old_aaaaaaaaaaaa",
            TempName = "postgres_new_aaaaaaaaaaaa",
            WasRunning = true,
            OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""",
            Step = ChangeStep.Creating,
            StartedAt = At,
            Kind = ChangeKind.Update,
            Fields = "Image,RestartPolicy",
            NewRestartPolicy = """{"Name":"always","MaximumRetryCount":0}""",
            ImageBefore = "postgres:18",
            ImageAfter = "postgres:18",
        };

        var added = await journal.AddAsync(change, CancellationToken.None);
        var done = added with { NewId = new string('b', 64), Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done, OfflineMs = 1800, FinishedAt = At.AddSeconds(9) };
        await journal.UpdateAsync(done, CancellationToken.None);
        var ports = await journal.AddAsync(change with { ContainerName = "sample-index", Kind = ChangeKind.Ports, Fields = null, NewRestartPolicy = null, ImageBefore = null, ImageAfter = null },
            CancellationToken.None);

        // Read back field for field, the new ones included; a port change is the column's default.
        Assert.Equal(done, await journal.LatestAsync("postgres", CancellationToken.None));
        Assert.Equal(ports, await journal.LatestAsync("sample-index", CancellationToken.None));
        Assert.Equal(ChangeKind.Ports, ports.Kind);
        // Stored as the migration made the column: words, never numbers.
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Kind", "Fields", "ImageBefore" FROM "ContainerChanges" ORDER BY "Id" """;
        var rows = new List<(string, string?, string?)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        Assert.Equal([("Update", "Image,RestartPolicy", "postgres:18"), ("Ports", null, null)], rows);
    }

    [Fact]
    public async Task Gitberths_own_data_for_a_container_without_a_row_adopts_it_through_the_catalog()
    {
        var view = await _service.LoadAsync("sample-index") ?? throw new InvalidOperationException("sample-index");
        Assert.Null(view.App.AppId);
        var edit = new SettingsEdit(view.ContainerId, view.Container, view.Container, view.Options, view.Options with { HealthCheckPath = "/health" });

        var plan = await _service.PlanAsync("sample-index", edit);
        Assert.Equal([("Health URL", ApplyKind.Gitberth)], plan.Lines.Select(l => (l.Label, l.How)));
        var outcome = await _service.ApplyAsync(plan);

        Assert.True(outcome.Ok, outcome.Message);
        var row = await _rig.RowAsync(a => a.Container == "sample-index") ?? throw new InvalidOperationException("No row was made.");
        Assert.Equal((AppSource.Existing, "/health"), (row.Source, row.HealthCheckPath));
        Assert.Contains(await _rig.Activity.ForAsync("sample-index", row.Id), a => a.Kind == ActivityKind.Adopted);
        Assert.Empty(_rig.Engine.Writes);
        Assert.Empty(_updates.Made);
    }

    [Fact]
    public async Task A_live_change_to_a_container_without_a_row_adopts_it_then_updates_it_without_a_restart()
    {
        var view = await _service.LoadAsync("postgres") ?? throw new InvalidOperationException("postgres");
        Assert.Null(view.App.AppId);
        var wanted = view.Container with { Resources = view.Container.Resources with { MemoryBytes = 512L * 1024 * 1024 } };
        var edit = new SettingsEdit(view.ContainerId, view.Container, wanted, view.Options, view.Options);

        var plan = await _service.PlanAsync("postgres", edit);
        Assert.True(plan.CanApply, plan.Blocked ?? string.Join(" ", plan.Problems.Select(p => p.Message)));
        Assert.True(plan.Change is { LiveOnly: true }, "a memory limit is a docker update");
        Assert.Equal("No restart.", plan.Consequence);
        var outcome = await _service.ApplyAsync(plan);
        Assert.True(outcome.Ok, outcome.Message);
        var result = await outcome.Job!.Completion;

        Assert.True(result.Ok, result.Message);
        // Adopted first (its row keeps what Gitberth knows), then docker update with the memory and its swap, and nothing else.
        var row = await _rig.RowAsync(a => a.Container == "postgres") ?? throw new InvalidOperationException("No row was made.");
        Assert.Equal(AppSource.Existing, row.Source);
        var update = Assert.Single(_updates.Made);
        Assert.Equal($"/v1.56/containers/{view.ContainerId}/update", update.Path);
        Assert.Equal(512L * 1024 * 1024, update.Body["Memory"]!.GetValue<long>());
        Assert.Empty(_rig.Engine.Writes);
        Assert.Empty(await _rig.Journal.UnfinishedAsync(CancellationToken.None));
        await EventuallyAsync(async () => (await _rig.Activity.ForAsync("postgres", row.Id)).Any(a => a.Kind == ActivityKind.Live));
    }

    [Fact]
    public async Task A_git_apps_limit_is_saved_to_its_row_then_applied_live_with_nothing_pending()
    {
        var view = await _service.LoadAsync("gb-orb") ?? throw new InvalidOperationException("gb-orb");
        Assert.Equal((AppSource.Git, _rig.ObsId), (view.App.Source, view.App.AppId));
        var wanted = view.Container with { Resources = view.Container.Resources with { MemoryBytes = 512L * 1024 * 1024 } };
        var edit = new SettingsEdit(view.ContainerId, view.Container, wanted, view.Options, view.Options, view.Git, view.Git);

        var plan = await _service.PlanAsync("gb-orb", edit);
        Assert.Equal("No restart.", plan.Consequence);
        Assert.All(plan.Lines, l => Assert.Equal(ApplyKind.Live, l.How));
        var outcome = await _service.ApplyAsync(plan);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Null(outcome.DeploymentId);
        var row = await _rig.RowAsync(a => a.Id == _rig.ObsId) ?? throw new InvalidOperationException("orb");
        Assert.Equal(((int?)512, false, false), (row.MemoryLimitMb, row.ConfigChangePending, row.RebuildPending));
        var update = Assert.Single(_updates.Made);
        Assert.Equal(512L * 1024 * 1024, update.Body["Memory"]!.GetValue<long>());
        Assert.Empty(_rig.Engine.Writes);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("It didn't happen within 5 s.");
    }
}
