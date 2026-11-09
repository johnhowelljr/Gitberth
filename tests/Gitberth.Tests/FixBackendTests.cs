using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Gitberth.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The backend fix round's own tests, for the review findings the reviewers' repro tests don't cover: a failed create
/// leaves no row (H4), a renamed app is shown as renamed (BG-1a), a recreate's new volume is checked like a create's
/// (DATA-1), Stop cancels a queued deploy (CONC-3), a health URL Gitberth can't reach isn't checked (F2), and a live
/// change's Activity entry carries the app (the trial's).
/// </summary>
public sealed class FixBackendTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _images = new();

    public void Dispose() => _db.Dispose();

    // ---- H4 / BG-1b: a create that fails removes its row, so nothing is made again by itself and a retry keeps the name ----

    private async Task<(CreateResult Result, ImageKit.CreateSteps Steps)> CreateAsync(Action<ImageKit.CreateSteps> set)
    {
        _images.Add(ImageKit.Image(ImageKit.Id('6'), ["nginx:1.27"], ["nginx@" + ImageKit.Digest('6')], exposed: ["80/tcp"]));
        var steps = new ImageKit.CreateSteps();
        set(steps);
        var creator = new AppCreator(steps, ImageKit.Images(_images, _db), new GitberthOptions { DataDir = Path.GetTempPath(), Network = "gitberth" },
            NullLogger<AppCreator>.Instance);
        var plan = await creator.PlanAsync(await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry));
        return (await creator.Start(plan).Completion, steps);
    }

    [Fact]
    public async Task H4_a_create_whose_first_start_fails_removes_its_row_and_says_why()
    {
        var (result, steps) = await CreateAsync(s => s.Result = new PortChangeResult(PortChangeOutcome.RolledBack,
            "gb-nginx didn't start (exit code 1). It was removed: nothing is left in Docker but its volumes.") { LastOutput = "nginx: [emerg] bad config" });

        Assert.False(result.Ok);
        Assert.Null(result.AppId);
        Assert.Contains("didn't start", result.Message, StringComparison.Ordinal);
        Assert.Equal("nginx: [emerg] bad config", result.LastOutput);
        Assert.Equal([1], steps.Discarded);
    }

    [Fact]
    public async Task H4_a_create_the_engine_refuses_removes_its_row()
    {
        var (result, steps) = await CreateAsync(s => s.EngineBlocks = "Port 80 is taken by sample-index.");

        Assert.False(result.Ok);
        Assert.Null(result.AppId);
        Assert.Equal("gb-nginx wasn't created: Port 80 is taken by sample-index.", result.Message);
        Assert.Equal([1], steps.Discarded);
    }

    [Fact]
    public async Task H4_a_failed_create_whose_container_docker_kept_keeps_its_row()
    {
        var (result, steps) = await CreateAsync(s =>
        {
            s.Result = new PortChangeResult(PortChangeOutcome.RolledBack, "gb-nginx didn't start. It couldn't be removed (busy): remove gb-nginx when you like.");
            s.LeftBehind = true;
        });

        Assert.False(result.Ok);
        Assert.Equal(1, result.AppId);
        Assert.Empty(steps.Discarded);
    }

    // ---- BG-1a: an app whose container was renamed outside Gitberth shows as renamed, with the other name ----

    [Fact]
    public async Task BG1a_an_app_renamed_outside_gitberth_is_shown_renamed_with_its_new_name()
    {
        using var rig = new AppsRig();
        var row = await rig.Catalog.AddAsync(new NewAppRow("pg", "pg", AppSource.Registry, "postgres:18", null,
            new AppSnapshot(new ContainerSettings { Image = "postgres:18" }, [new NetworkJoin("gitberth", ["pg"])]), new AppOptions { Name = "pg" }));
        var labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "pg", ["gitberth.source"] = "registry" };
        var kept = rig.Engine.AddLike("postgres", "gb-pg-before-upgrade", labels);
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, kept));

        var view = await rig.Catalog.GetAsync("gb-pg", "10.1.1.80");
        var started = await rig.Actions.StartAsync("gb-pg");

        Assert.Equal(AppState.Renamed, view?.Summary.State);
        Assert.Equal("gb-pg-before-upgrade", view?.Summary.RenamedTo);
        Assert.StartsWith("gb-pg's container was renamed outside Gitberth: it's gb-pg-before-upgrade now", started, StringComparison.Ordinal);
        Assert.Empty(rig.Created);
    }

    // ---- DATA-1: a recreate that adds a volume a running database writes is refused, as a create is ----

    [Fact]
    public async Task DATA1_a_recreate_that_mounts_a_running_databases_volume_is_refused()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock);
        var host = new FakeHost(engine);
        engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
        engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = DockerFixtures.Inspect("sample-index")["Image"]!.DeepClone(), ["Config"] = new JsonObject() };
        var postgres = DockerFixtures.Inspect("postgres");
        postgres["HostConfig"]!["NetworkMode"] = "gitberth";
        postgres["State"]!["Running"] = true;
        postgres["State"]!["Status"] = "running";
        engine.Add(postgres);
        var web = DockerFixtures.Inspect("sample-index");
        web["State"]!["Running"] = true;
        web["State"]!["Status"] = "running";
        engine.Add(web);
        var changer = Changer(engine, host, clock);
        var now = (await engine.InspectContainerAsync("sample-index", default))!;
        var loaded = ContainerSettingsJson.Read(now, await engine.InspectImageAsync((string)now["Image"]!, default));

        var plan = await changer.PlanAsync("sample-index", new ContainerEditRequest
        {
            ContainerId = (string)now["Id"]!,
            Loaded = loaded,
            Wanted = loaded with { Mounts = [.. loaded.Mounts, new MountSetting(MountKind.Volume, "postgres-data", "/srv/data")] },
        });
        var readOnly = await changer.PlanAsync("sample-index", new ContainerEditRequest
        {
            ContainerId = (string)now["Id"]!,
            Loaded = loaded,
            Wanted = loaded with { Mounts = [.. loaded.Mounts, new MountSetting(MountKind.Volume, "postgres-data", "/srv/data", ReadOnly: true)] },
        });

        Assert.False(plan.CanApply);
        Assert.StartsWith("postgres runs and writes the volume postgres-data. Recreating sample-index on it as well", plan.Blocked, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres-data", readOnly.Blocked ?? "", StringComparison.Ordinal);
    }

    // ---- F2: a health URL is checked by name only when Gitberth can reach the container by name ----

    [Fact]
    public async Task F2_a_health_url_off_the_shared_network_isnt_checked_and_the_plan_says_so()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock);
        engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = DockerFixtures.Inspect("sample-index")["Image"]!.DeepClone(), ["Config"] = new JsonObject() };
        var changer = Changer(engine, new FakeHost(engine), clock);
        var settings = new ContainerSettings { Image = "nginx:stable-alpine", Restart = new RestartSetting("unless-stopped") };

        var bridge = await changer.PlanCreateAsync(new NewContainerRequest { Name = "gbtest-web", Settings = settings, Networks = [], HealthPath = "/health", WebPort = 80 });
        var shared = await changer.PlanCreateAsync(new NewContainerRequest
        {
            Name = "gbtest-web", Settings = settings, Networks = [new NetworkJoin("gitberth", ["web"])], HealthPath = "/health", WebPort = 80,
        });

        Assert.Contains(bridge.Impact, i => i.StartsWith("Its health URL isn't checked after the start", StringComparison.Ordinal));
        Assert.DoesNotContain(bridge.Steps, s => s.Contains("/health", StringComparison.Ordinal));
        Assert.Contains(shared.Steps, s => s.Contains("http://gbtest-web:80/health", StringComparison.Ordinal));
    }

    // ---- CONC-3: Stop cancels a deploy queued before it, which would start the app again ----

    [Fact]
    public async Task CONC3_stopping_an_app_cancels_its_queued_deploy()
    {
        using var rig = new AppsRig();
        var queued = new Deployment { AppId = rig.KanbanId, Trigger = DeployTrigger.Start };
        await rig.AddAsync(queued);

        await rig.Actions.StopAsync(rig.KanbanId);

        await using var db = rig.Db.CreateDbContext();
        var after = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == queued.Id);
        Assert.Equal(DeployStatus.Cancelled, after.Status);
        Assert.Equal(DesiredState.Stopped, (await db.Apps.AsNoTracking().SingleAsync(a => a.Id == rig.KanbanId)).DesiredState);
    }

    // ---- The trial's: a live change's Activity entry is the app's ----

    [Fact]
    public async Task An_activity_entry_written_without_an_app_is_the_containers_apps()
    {
        using var rig = new AppsRig();

        await rig.Activity.AddAsync("gb-kanban", null, ActivityKind.Live, "Memory limit 512 MB, live: no restart.");

        await using var db = rig.Db.CreateDbContext();
        Assert.Equal(rig.KanbanId, (await db.Activity.AsNoTracking().SingleAsync(a => a.ContainerName == "gb-kanban")).AppId);
    }

    private static PortChanger Changer(FakeEngine engine, FakeHost host, FakeClock clock)
    {
        PortChanger? changer = null;
        var locks = new ContainerLocks();
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, new MemoryJournal(), locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath(), Network = "gitberth" },
            NullLogger<PortChanger>.Instance, clock, updater);
        return changer;
    }
}
