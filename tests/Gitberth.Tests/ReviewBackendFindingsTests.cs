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
/// The backend review's repros: each test states what the design (architecture.md) asks for and fails on the candidate.
/// </summary>
public sealed class ReviewBackendFindingsTests : IDisposable
{
    private const string Old = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _images = new();

    public void Dispose() => _db.Dispose();

    // ---- R1: the tracked tag never follows the image the app really runs ----

    [Fact]
    public async Task R1a_sync_after_a_recreate_on_another_tag_follows_the_new_tag()
    {
        using var rig = new AppsRig();
        var row = await rig.Catalog.AdoptAsync("sample-index");
        Assert.Equal("nginx:stable-alpine", row.ImageRef);

        // Recreated on nginx:1.27-alpine (outside Gitberth, or through the Settings tab's image field: both end in SyncAsync).
        rig.Engine.Recreate("sample-index");
        rig.Engine.Inspect("sample-index")["Config"]!["Image"] = "nginx:1.27-alpine";
        await rig.Catalog.SyncAsync(row.Id);

        Assert.Equal("nginx:1.27-alpine", (await rig.RowAsync(a => a.Id == row.Id))!.ImageRef);
    }

    [Fact]
    public async Task R1b_an_app_moved_to_a_newer_tag_is_never_offered_its_old_tag_as_an_update()
    {
        var steps = new ImageKit.UpdateSteps(_db);
        var updates = new UpdateService(steps, ImageKit.Images(_images, _db), _db, NullLogger<UpdateService>.Instance);
        // postgres:18 (Old) stays on the host; the app now runs postgres:19, but its row still follows postgres:18.
        _images.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        var nineteen = ImageKit.Id('9');
        _images.Add(ImageKit.Image(nineteen, ["postgres:19"], ["postgres@" + ImageKit.Digest('9')], created: "2026-11-06T00:00:00Z"));
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["Config"]!["Image"] = "postgres:19";
        inspect["Image"] = nineteen;
        steps.Containers["postgres"] = inspect;
        _images.Uses("postgres", nineteen, "postgres:19");
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18" });

        var status = await updates.StatusAsync("postgres");
        var prepared = await updates.PrepareAsync("postgres");

        // What happens: "A newer local image", and Update plans a recreate on postgres:18 (a downgrade; Auto does it at 03:00).
        Assert.False(status.Available, $"badge: {status.Text}; plan: {string.Join(", ", steps.Requests.Select(r => r.Wanted.Image))}");
        Assert.DoesNotContain(steps.Requests, r => r.Wanted.Image == "postgres:18");
        Assert.NotEqual(Old, prepared.Plan?.Request?.ExpectedImageId);
    }

    // ---- R3: 3.1's ports and named volumes only when the image is on the host at draft time, and never for a paste ----

    private AppCreator Creator(ImageKit.CreateSteps steps) =>
        new(steps, ImageKit.Images(_images, _db), new GitberthOptions { DataDir = Path.GetTempPath(), Network = "gitberth" }, NullLogger<AppCreator>.Instance);

    private void NginxWithVolume() =>
        _images.Add(ImageKit.Image(ImageKit.Id('6'), ["nginx:1.27"], ["nginx@" + ImageKit.Digest('6')], exposed: ["80/tcp"], volumes: ["/data"]));

    [Fact]
    public async Task R3a_a_registry_image_pulled_by_the_create_gets_its_exposed_ports_and_named_volumes()
    {
        var steps = new ImageKit.CreateSteps();
        _images.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Downloaded newer image for nginx:1.27\"}"], NginxWithVolume);
        var creator = Creator(steps);

        var plan = await creator.PlanAsync(await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry));
        var result = await creator.Start(plan).Completion;

        Assert.True(result.Ok, result.Message);
        var request = Assert.Single(steps.Requests);
        Assert.Contains(request.Settings.Ports, p => p.ContainerPort == 80 && p.Published);
        Assert.Contains(request.Settings.Mounts, m => m is { Kind: MountKind.Volume, Target: "/data", Source: "gb-nginx-data" });
    }

    [Fact]
    public async Task R3b_a_pasted_docker_run_names_the_images_volumes()
    {
        NginxWithVolume();
        var creator = Creator(new ImageKit.CreateSteps());

        var draft = await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run -d --name web -p 8080:80 nginx:1.27"));

        Assert.Contains(draft.Settings.Mounts, m => m is { Kind: MountKind.Volume, Target: "/data", Source: "gb-web-data" });
    }

    // ---- R4: a container in another's network namespace can't be recreated: the clone keeps the image's ExposedPorts ----

    [Fact]
    public async Task R4_a_container_sharing_anothers_network_can_have_its_environment_changed()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock);
        var host = new FakeHost(engine);
        var journal = new MemoryJournal();
        var locks = new ContainerLocks();
        engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
        engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = DockerFixtures.Inspect("sample-index")["Image"]!.DeepClone(), ["Config"] = new JsonObject() };
        var vpn = DockerFixtures.Inspect("sample-index");
        vpn["State"]!["Running"] = true;
        vpn["State"]!["Status"] = "running";
        var vpnBox = engine.Add(vpn);
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "container:" + vpnBox.Id;
        inspect["HostConfig"]!["PortBindings"] = new JsonObject();
        inspect["NetworkSettings"]!["Networks"] = new JsonObject();
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        engine.Add(inspect);
        // Docker's own check at POST /containers/create (moby runconfig validateNetContainerMode).
        engine.OnCreate = b =>
        {
            if (((string?)b.HostConfig["NetworkMode"] ?? "").StartsWith("container:", StringComparison.Ordinal)
                && b.Config["ExposedPorts"] is JsonObject { Count: > 0 })
                throw new EngineException(400, "conflicting options: port exposing and the container type network mode");
        };
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, journal, locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, clock, updater);
        var now = (await engine.InspectContainerAsync("postgres", default))!;
        var loaded = ContainerSettingsJson.Read(now, await engine.InspectImageAsync((string)now["Image"]!, default));

        var plan = await changer.PlanAsync("postgres", new ContainerEditRequest
        {
            ContainerId = (string)now["Id"]!,
            Loaded = loaded,
            Wanted = loaded with { Env = [.. loaded.Env, new EnvSetting("TZ", "Europe/London")] },
        });
        Assert.True(plan.CanApply, plan.Blocked);
        var result = await changer.ApplyAsync(plan);

        Assert.True(result.Outcome == PortChangeOutcome.Done, $"{result.Outcome}: {result.Message}");
    }

    // ---- R6: a rollback pins the container to a bare image ID, so postgres stops being a database to Gitberth ----

    [Fact]
    public async Task R6_a_rolled_back_postgres_is_still_known_as_postgres()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock) { OnCreate = b => b.Listens = [5432] };
        var host = new FakeHost(engine);
        var locks = new ContainerLocks();
        var oldImage = DockerFixtures.Json("image-postgres.json").AsObject();
        var newImage = (JsonObject)oldImage.DeepClone();
        var newId = "sha256:9bc1d" + new string('e', 59);
        newImage["Id"] = newId;
        newImage["RepoTags"] = new JsonArray("postgres:18");
        newImage["RepoDigests"] = new JsonArray("postgres@" + newId);
        oldImage["RepoTags"] = new JsonArray();
        engine.Images["postgres:18"] = newImage;
        engine.Images["postgres@old"] = oldImage;
        // postgres was updated to newImage; PreviousImage is the old one, by ID.
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
        inspect["Image"] = newId;
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        engine.Add(inspect).Listens = [5432];
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, new MemoryJournal(), locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, clock, updater);
        var now = (await engine.InspectContainerAsync("postgres", default))!;
        var loaded = ContainerSettingsJson.Read(now, newImage);

        var plan = await changer.PlanAsync("postgres", new ContainerEditRequest
        {
            ContainerId = (string)now["Id"]!, Loaded = loaded, Wanted = loaded with { Image = Old }, Kind = ChangeKind.Rollback, ExpectedImageId = Old,
        });
        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Contains(plan.Impact, i => i.StartsWith("postgres checks its data files", StringComparison.Ordinal));
        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var back = engine.Get("postgres");
        Assert.Equal(Old, back.Image);
        // Config.Image is "sha256:3a42…": ContainerService.IsDatabaseImage / RoleOf no longer see postgres (stop timeout 10 s, not 60 s).
        Assert.True(ContainerService.IsDatabaseImage((string)back.Config["Image"]!), (string)back.Config["Image"]!);
    }

    // ---- R5: a registry app renamed outside Gitberth is created again next to itself, on the same volumes ----

    [Fact]
    public async Task R5_startup_doesnt_create_a_second_container_for_an_app_whose_container_was_renamed()
    {
        using var rig = new AppsRig();
        var labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "whoami", [ContainerSpecFactory.SourceLabel] = "registry" };
        // gb-whoami, renamed by hand to whoami-old: same container (same ID, same volumes), another name.
        var id = rig.Engine.AddLike("sample-bun-elysia", "whoami-old", labels);
        var row = await rig.Catalog.AddAsync(new NewAppRow("whoami", "whoami", AppSource.Registry, "traefik/whoami:v1.10", null,
            new AppSnapshot(new ContainerSettings { Image = "traefik/whoami:v1.10", Mounts = [new MountSetting(MountKind.Volume, "gb-whoami-data", "/data")] },
                [new NetworkJoin("gitberth", ["whoami"])]),
            new AppOptions { Name = "whoami" }));
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, id));
        var startup = new StartupTasks(rig.Db, rig.Cli, rig.AppLocks, rig.Actions, rig.Pipeline, rig.Options, NullLogger<StartupTasks>.Instance, new DockerEngine(rig.Api));

        await startup.CreateMissingAsync(CancellationToken.None);

        Assert.True(rig.Created.Count == 0, $"created again: {string.Join(", ", rig.Created.Select(c => c.Name))} while {id[..12]} (whoami-old) still runs");
    }
}
