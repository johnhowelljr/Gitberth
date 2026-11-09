using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Safety review of the unified-apps release (concurrency): two owner actions, or an owner action and a background job,
/// on the same container. Each failing test is a finding; the passing ones document the mechanism a finding rests on.
/// </summary>
public sealed class RevSafetyConcurrencyTests : IDisposable
{
    private const string Secret = "s3cret-pass";

    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly FakeHost _host;
    private readonly MemoryJournal _journal = new();
    private readonly ContainerLocks _locks = new();
    private readonly AppsRig _rig = new();

    public RevSafetyConcurrencyTests()
    {
        _engine = new FakeEngine(_clock) { OnCreate = b => b.Listens = [5432] };
        _host = new FakeHost(_engine);
        _engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
    }

    public void Dispose() => _rig.Dispose();

    private PortChanger Changer()
    {
        PortChanger? changer = null;
        var updater = new ContainerUpdater(_engine, _locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(_engine, _journal, _locks, _host, _host, _host, _host, _host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, _clock, updater);
        return changer;
    }

    private void Postgres()
    {
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
        inspect["Config"]!["Env"]![0] = "POSTGRES_PASSWORD=" + Secret;
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["ExitCode"] = 0;
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        _engine.Add(inspect).Listens = [5432];
    }

    private async Task<ContainerEditRequest> EditAsync(string container, Func<ContainerSettings, ContainerSettings> change)
    {
        var inspect = (await _engine.InspectContainerAsync(container, default))!;
        var loaded = ContainerSettingsJson.Read(inspect, await _engine.InspectImageAsync((string)inspect["Image"]!, default));
        return new ContainerEditRequest { ContainerId = _engine.Get(container).Id, Loaded = loaded, Wanted = change(loaded), Kind = ChangeKind.Settings };
    }

    private static ContainerSettings WithEnv(ContainerSettings s, string key, string value) =>
        s with { Env = [.. s.Env.Where(e => e.Key != key), new EnvSetting(key, value)] };

    private List<string> Env(string container) => _engine.Get(container).Config["Env"]!.AsArray().Select(e => (string)e!).ToList();

    /// <summary>
    /// CONC-1. The owner confirms a change (here a new POSTGRES_PASSWORD) while another change of the same container is
    /// running: a second tab's settings Apply, an Update, or the watcher's automatic update. PortChanger.Start hands back
    /// the change already running, and AppSettingsService.ApplyAsync and the change dialog follow that job as if it were the
    /// owner's: it ends Done, the dialog says so, and the owner's change was never made.
    /// </summary>
    [Fact]
    public async Task A_change_confirmed_while_another_runs_is_never_reported_done_without_being_made()
    {
        Postgres();
        var changer = Changer();
        // Both planned while nothing ran: neither plan is blocked.
        var first = await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "Europe/London")));
        var second = await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "POSTGRES_PASSWORD", "rotated-" + Secret)));
        Assert.True(first.CanApply, first.Blocked);
        Assert.True(second.CanApply, second.Blocked);

        // The first one is under way (held at its stop) when the owner confirms the second.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Gates["stop postgres"] = gate;
        var firstJob = changer.Start(first);
        await _engine.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var secondJob = changer.Start(second);
        gate.SetResult();
        await firstJob.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var said = await secondJob.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var made = Env("postgres").Contains("POSTGRES_PASSWORD=rotated-" + Secret);
        Assert.True(made || !said.Ok,
            $"The second change's job says {said.Outcome} (\"{said.Message}\"), and is the first's: {ReferenceEquals(secondJob, firstJob)}; postgres has the new password: {made}.");
    }

    /// <summary>
    /// CONC-2, the mechanism. It was: when a change failed its checks and the original was put back, the original was started
    /// while it still had its parked name (postgres_old_…), and only renamed back afterwards, so Docker's start event, which
    /// AppSync and NetworkKeeper go by, named postgres_old_…. Changed by the round-2 backend fixer to the decided fix: the
    /// original takes its name back first, and starts as postgres.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_change_starts_the_original_under_its_own_name()
    {
        Postgres();
        var oldId = _engine.Get("postgres").Id;
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(2);
        };
        var changer = Changer();

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "Europe/London"))));

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        var parked = PortChanger.OldNameFor("postgres", oldId);
        var renameBack = _engine.Calls.FindLastIndex(c => c == $"rename {parked} postgres");
        Assert.True(renameBack >= 0, string.Join(" | ", _engine.Calls));
        Assert.DoesNotContain($"start {parked}", _engine.Calls);
        Assert.True(_engine.Calls.FindIndex(renameBack, c => c == "start postgres") > renameBack, string.Join(" | ", _engine.Calls));
        Assert.Equal(oldId, _engine.Get("postgres").Id);
    }

    /// <summary>
    /// CONC-2. An adopted app's settings change starts its new container: AppSync hears that start (the journal knows the new
    /// ID, so it's Gitberth's own) and syncs the row to the new container: its ID, ports mirror and encrypted snapshot. The
    /// new container fails its checks and the original comes back, started under its parked name (see the evidence test),
    /// so AppSync's lookup by name finds no row and nothing syncs it back; the settings service and the update follow-up only
    /// sync after a change that worked. The row is left describing the rejected container: CreateMissingAsync and
    /// [Create it again] would make the app from the settings the owner's change was rolled back from.
    /// </summary>
    [Fact]
    public async Task After_a_rolled_back_change_the_apps_row_describes_the_original_again()
    {
        const string name = "myapp";
        _rig.Engine.AddLike("sample-bun-elysia", name);
        var row = await _rig.Catalog.AdoptAsync(name);
        var originalId = row.ContainerId!;
        var sync = new AppSync(_rig.Events, _rig.Catalog, _rig.Db, _rig.Journal, _rig.Network, _rig.Activity, NullLogger<AppSync>.Instance);

        // The change: its journal row, then its new container under the name, with the setting the owner asked for.
        var newId = _rig.Engine.Recreate(name);
        ((JsonArray)_rig.Engine.Inspect(name)["Config"]!["Env"]!).Add("REJECTED=yes");
        var change = await _rig.Journal.AddAsync(new ContainerChange
        {
            ContainerName = name, Summary = "added REJECTED", OldId = originalId, NewId = newId, OldName = PortChanger.OldNameFor(name, originalId),
            TempName = PortChanger.TempNameFor(name, originalId), WasRunning = true, OldRestartPolicy = """{"Name":"unless-stopped"}""", Step = ChangeStep.Verifying,
            StartedAt = DateTime.UtcNow, Kind = ChangeKind.Settings, Fields = "Env",
        }, default);
        Assert.True(await sync.SeenAsync(name, default));
        Assert.Equal(newId, (await _rig.RowAsync(a => a.Id == row.Id))!.ContainerId);

        // It fails its checks: the new one goes, the original (same ID, its own settings) takes its name back (it used to
        // start as postgres_old_… first, which no row is called) and starts; the change ends rolled back.
        _rig.Engine.Remove(name);
        Assert.Equal(originalId, _rig.Engine.AddLike("sample-bun-elysia", name));
        var ended = change with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.RolledBack, FinishedAt = DateTime.UtcNow };
        await _rig.Journal.UpdateAsync(ended, default);
        Assert.False(await sync.SeenAsync(PortChanger.OldNameFor(name, originalId), default));
        // Adapted by the backend fixer to the decided fix: the change engine says when a change ends, whatever its end
        // (PortChanger.Ended), and AppSync syncs the row then, the start event under the parked name notwithstanding.
        Assert.True(await sync.EndedAsync(ended, default));

        var now = (await _rig.RowAsync(a => a.Id == row.Id))!;
        var snapshot = (await _rig.Catalog.SnapshotAsync(row.Id))!;
        Assert.True(now.ContainerId == originalId && snapshot.Settings.Env.All(e => e.Key != "REJECTED"),
            $"The row names container {now.ContainerId?[..12]} (the original is {originalId[..12]}, the rejected one {newId[..12]}); "
            + $"its snapshot has REJECTED: {snapshot.Settings.Env.Any(e => e.Key == "REJECTED")}.");
    }
}
