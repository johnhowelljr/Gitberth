using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Safety review of the unified-apps release: what Gitberth does on its own, with nobody touching it (StartupTasks and the
/// update watcher). Each test states what must hold; one that fails is a finding.
/// </summary>
public sealed class RevSafetyBackgroundTests
{
    private static StartupTasks Startup(AppsRig rig) =>
        new(rig.Db, rig.Cli, rig.AppLocks, rig.Actions, rig.Pipeline, rig.Options, NullLogger<StartupTasks>.Instance, new DockerEngine(rig.Api));

    /// <summary>
    /// The owner made an app with New app; its first start failed, so AppCreator left the row it wrote (AppCreator.cs:296,
    /// DesiredState Running, the snapshot) and nothing in Docker (:336-340), and the page offers [Start]. Gitberth restarts
    /// later (its own update, a host reboot): nobody pressed Start, so nothing may be created.
    /// </summary>
    [Fact]
    public async Task BG1_a_create_whose_first_start_failed_is_not_made_again_by_itself_at_the_next_start()
    {
        using var rig = new AppsRig();
        var row = await rig.Catalog.AddAsync(new NewAppRow("uptime", "uptime", AppSource.Registry, "louislam/uptime-kuma:1", null,
            new AppSnapshot(new ContainerSettings { Image = "louislam/uptime-kuma:1", Ports = [new PortRule(3001, "tcp", PortAccess.Lan, 3001)] },
                [new NetworkJoin("gitberth", ["uptime"])]),
            new AppOptions { Name = "uptime", WebPort = 3001 }));
        await rig.Activity.AddAsync("gb-uptime", row.Id, ActivityKind.Created, "Its first start failed, so nothing was left in Docker: it exited with code 1.",
            ActivityTone.Warning);

        await Startup(rig).CreateMissingAsync(CancellationToken.None);

        Assert.True(rig.Created.Count == 0,
            $"At its next start Gitberth created {string.Join(", ", rig.Created.Select(c => c.Name))} again by itself (and the engine starts it): " +
            "the owner saw its first start fail and never pressed Start.");
    }

    /// <summary>
    /// A database Gitberth created (gb-pg, on its volume) is renamed by hand to keep it aside (docker rename gb-pg
    /// gb-pg-before-upgrade): the same container, the ID its row knows, still running on the same volume. At its next start
    /// Gitberth must not make a second gb-pg on that volume next to it.
    /// </summary>
    [Fact]
    public async Task BG1_an_app_renamed_by_hand_is_not_made_again_next_to_itself_on_its_volume()
    {
        using var rig = new AppsRig();
        var volume = rig.Engine.Inspect("postgres")["Mounts"]!.AsArray().OfType<JsonObject>().First(m => ContainerPorts.Str(m, "Type") == "volume")["Name"]!
            .GetValue<string>();
        var row = await rig.Catalog.AddAsync(new NewAppRow("pg", "pg", AppSource.Registry, "postgres:18", null,
            new AppSnapshot(new ContainerSettings { Image = "postgres:18", Mounts = [new MountSetting(MountKind.Volume, volume, "/var/lib/postgresql")] },
                [new NetworkJoin("gitberth", ["pg"])]),
            new AppOptions { Name = "pg" }));
        var labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "pg", [ContainerSpecFactory.SourceLabel] = "registry" };
        var kept = rig.Engine.AddLike("postgres", "gb-pg-before-upgrade", labels);
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, kept));

        await Startup(rig).CreateMissingAsync(CancellationToken.None);

        var made = rig.Created.Select(c => $"{c.Name} (image {c.Settings.Image}, volume {string.Join(", ", c.Settings.Mounts.Select(m => m.Source))})").ToList();
        Assert.True(made.Count == 0,
            $"With its own container still running as gb-pg-before-upgrade (ID {kept[..12]}, which its row knows, on {volume}), Gitberth created " +
            $"{string.Join("; ", made)} by itself at its start: a second database server on the same data.");
    }

    /// <summary>
    /// Automatic updates are never for a database, whatever its image is called. The owner sets Auto on an app (the Settings
    /// tab only greys Auto out for what ContainerService.IsDatabaseImage knows), and the window opens at 03:30.
    /// </summary>
    [Theory]
    [InlineData("bitnami/postgresql:16")]
    [InlineData("tensorchord/pgvecto-rs:pg16-v0.3.0")]
    [InlineData("ghcr.io/cloudnative-pg/postgresql:16")]
    [InlineData("bitnami/mongodb:7.0")]
    [InlineData("mysql/mysql-server:8.0")]
    public async Task BG2_a_database_is_never_updated_by_itself_whatever_its_image_is_called(string image)
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var steps = new ImageKit.UpdateSteps(db);
        var clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 8, 3, 30, 0, TimeSpan.Zero));
        var updates = new UpdateService(steps, ImageKit.Images(engine, db), db, NullLogger<UpdateService>.Instance) { Time = clock };
        var reference = ImageReference.Parse(image)!;
        var old = ImageKit.Id('o');
        var newer = ImageKit.Id('p');
        // A database image says what it is: its data folder and its port.
        engine.Add(ImageKit.Image(old, [image], [$"{reference.Name}@{ImageKit.Digest('1')}"], exposed: ["5432/tcp"], volumes: ["/var/lib/postgresql/data"]));
        steps.Containers["gb-db"] = ImageKit.Container("gb-db", "c0ffee", image, old);
        engine.Uses("gb-db", old, image);
        await using (var context = db.CreateDbContext())
        {
            context.ImageChecks.Add(new ImageCheck { Reference = reference.Canonical, RemoteDigest = ImageKit.Digest('2'), CheckedAt = clock.Now.UtcDateTime });
            await context.SaveChangesAsync();
        }
        engine.Pulls[$"{reference.FromImage}:{reference.TagOrDigest}"] = (["{\"status\":\"Digest: " + ImageKit.Digest('2') + "\"}"], () =>
        {
            engine.Add(ImageKit.Image(newer, [], [$"{reference.Name}@{ImageKit.Digest('2')}"]));
            engine.Tag(image, newer);
        });
        await db.AddAsync(new AppDefinition { Name = "db", Slug = "db", Source = AppSource.Registry, ImageRef = image, UpdatePolicy = UpdatePolicy.Auto });

        var refusal = UpdateService.AutoRefusal(await db.AppAsync(1), steps.Containers["gb-db"], "0123456789ab");
        var updated = await updates.AutoUpdateAsync("0123456789ab", default);

        Assert.True(refusal is not null && updated.Count == 0,
            $"{image} was updated by itself at 03:30 ({string.Join(", ", steps.Calls)}): IsDatabaseImage doesn't know '{ContainerService.Repository(image)}', " +
            $"so AutoRefusal said {(refusal is null ? "nothing" : refusal)}.");
    }

    /// <summary>
    /// No registry is asked about an image built on this host. A hand-built image (docker build -t family-photos:dev; the
    /// containerd store gives it a digest under its own name) run by a container is never sent to Docker Hub by the watcher.
    /// </summary>
    [Fact]
    public async Task BG3_the_watcher_never_asks_a_registry_about_an_image_built_on_this_host()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var steps = new ImageKit.UpdateSteps(db);
        var clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var updates = new UpdateService(steps, ImageKit.Images(engine, db), db, NullLogger<UpdateService>.Instance) { Time = clock };
        var local = ImageKit.Id('b');
        engine.Add(ImageKit.Image(local, ["family-photos:dev"], ["family-photos@" + ImageKit.Digest('b')]));
        engine.Uses("family-photos", local, "family-photos:dev");

        var asked = await updates.CheckDueAsync(TimeSpan.FromHours(6), default);

        Assert.True(!engine.Calls.Contains("distribution docker.io/library/family-photos:dev"),
            $"With nobody touching it, the watcher asked Docker Hub about the local image family-photos:dev ({string.Join(", ", engine.Calls)}; checked {string.Join(", ", asked)}).");
    }

    /// <summary>
    /// gb-web was created from nginx:1.27 (its row follows that tag); the owner since changed its image to nginx:1.28 on the
    /// Settings tab, and it runs that. Docker Hub has nothing newer for either tag. It has no update, and the automatic update
    /// at 03:30 must not take it back to nginx:1.27.
    /// </summary>
    [Fact]
    public async Task BG4_an_image_changed_in_settings_is_never_taken_back_by_an_automatic_update()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var steps = new ImageKit.UpdateSteps(db);
        var clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 8, 3, 30, 0, TimeSpan.Zero));
        var updates = new UpdateService(steps, ImageKit.Images(engine, db), db, NullLogger<UpdateService>.Instance) { Time = clock };
        var n127 = ImageKit.Id('7');
        var n128 = ImageKit.Id('8');
        engine.Add(ImageKit.Image(n127, ["nginx:1.27"], ["nginx@" + ImageKit.Digest('a')]));
        engine.Add(ImageKit.Image(n128, ["nginx:1.28"], ["nginx@" + ImageKit.Digest('b')]));
        await db.AddAsync(new AppDefinition { Name = "web", Slug = "web", Source = AppSource.Registry, ImageRef = "nginx:1.27", UpdatePolicy = UpdatePolicy.Auto });
        steps.Containers["gb-web"] = ImageKit.Container("gb-web", "c0ffee", "nginx:1.28", n128);
        engine.Uses("gb-web", n128, "nginx:1.28");
        await using (var context = db.CreateDbContext())
        {
            context.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/nginx:1.27", RemoteDigest = ImageKit.Digest('a'), CheckedAt = clock.Now.UtcDateTime });
            context.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/nginx:1.28", RemoteDigest = ImageKit.Digest('b'), CheckedAt = clock.Now.UtcDateTime });
            await context.SaveChangesAsync();
        }
        engine.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Image is up to date for nginx:1.27\"}"], null);
        var watcher = new UpdateWatcher(updates, new SettingsService(db, ImageKit.Protector()),
            new GitberthOptions { DataDir = Path.GetTempPath(), OwnContainer = "0123456789ab" }, NullLogger<UpdateWatcher>.Instance, clock);

        var status = await updates.StatusAsync("gb-web");
        await watcher.RunOnceAsync(default);

        var to = steps.Requests.Select(r => $"{r.Kind}: {r.Loaded.Image} -> {r.Wanted.Image}").ToList();
        Assert.True(!status.Available && to.Count == 0 && !steps.Calls.Any(c => c.StartsWith("start", StringComparison.Ordinal)),
            $"The badge says \"{status.Text}\" for an app that runs nginx:1.28, and the watcher ran {string.Join("; ", to)} by itself at 03:30 " +
            $"({string.Join(", ", steps.Calls)}): Apps.ImageRef still follows the tag it was created from.");
    }

    /// <summary>
    /// gb-web (Auto) was updated to nginx:1.27's newer image; it misbehaved, so the owner pressed Roll back, and it runs the
    /// image it ran before (by ID, as PrepareRollbackAsync writes it). The next window must not update it again by itself.
    /// </summary>
    [Fact]
    public async Task BG4_a_rollback_the_owner_chose_is_not_undone_by_the_next_automatic_update()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var steps = new ImageKit.UpdateSteps(db);
        var clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 9, 3, 30, 0, TimeSpan.Zero));
        var updates = new UpdateService(steps, ImageKit.Images(engine, db), db, NullLogger<UpdateService>.Instance) { Time = clock };
        var before = ImageKit.Id('5');
        var after = ImageKit.Id('6');
        engine.Add(ImageKit.Image(before, [], ["nginx@" + ImageKit.Digest('c')]));
        engine.Add(ImageKit.Image(after, ["nginx:1.27"], ["nginx@" + ImageKit.Digest('d')]));
        // Adapted by the backend fixer: the rollback runs through the service (the decision is that its follow-up pauses
        // automatic updates), rather than starting from the state the old follow-up left.
        await db.AddAsync(new AppDefinition
        {
            Name = "web", Slug = "web", Source = AppSource.Registry, ImageRef = "nginx:1.27", UpdatePolicy = UpdatePolicy.Auto, PreviousImage = before,
        });
        steps.Containers["gb-web"] = ImageKit.Container("gb-web", "c0ffee", "nginx:1.27", after);
        engine.Uses("gb-web", after, "nginx:1.27");
        await using (var context = db.CreateDbContext())
        {
            context.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/nginx:1.27", RemoteDigest = ImageKit.Digest('d'), CheckedAt = clock.Now.UtcDateTime });
            await context.SaveChangesAsync();
        }
        engine.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Image is up to date for nginx:1.27\"}"], null);
        var watcher = new UpdateWatcher(updates, new SettingsService(db, ImageKit.Protector()),
            new GitberthOptions { DataDir = Path.GetTempPath(), OwnContainer = "0123456789ab" }, NullLogger<UpdateWatcher>.Instance, clock);

        // The owner's Roll back: by the image's repository digest, so it keeps its name.
        var rollback = await updates.PrepareRollbackAsync("gb-web");
        Assert.Equal("nginx@" + ImageKit.Digest('c'), rollback.Plan?.Request?.Wanted.Image);
        Assert.True((await updates.Start(rollback.Plan!).Completion).Ok);
        await updates.FollowUpOf("gb-web");
        steps.Calls.Clear();

        await watcher.RunOnceAsync(default);

        Assert.True(!steps.Calls.Any(c => c.StartsWith("start", StringComparison.Ordinal)),
            $"The night after the owner rolled gb-web back, the watcher updated it again by itself ({string.Join(", ", steps.Calls)}).");
        Assert.Equal(UpdatePolicy.Notify, (await db.AppAsync(1)).UpdatePolicy);
        Assert.Contains(steps.Activity, a => a.Text.StartsWith("Automatic updates paused after a roll back", StringComparison.Ordinal));
    }
}
