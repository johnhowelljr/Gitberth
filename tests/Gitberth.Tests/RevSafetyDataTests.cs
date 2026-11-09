using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Safety review (data in owner actions): what an owner's action can do beyond what it says, on CT 101 as it is. Each test
/// states the safe behaviour; a failure is a finding.
/// </summary>
public sealed class RevSafetyDataTests : IDisposable
{
    private const string Secret = "s3cret-pass";

    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly FakeHost _host;
    private readonly MemoryJournal _journal = new();
    private readonly ContainerLocks _locks = new();
    private readonly AppsRig _rig = new();

    public RevSafetyDataTests()
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

    // CT 101's postgres: postgres:18 on gitberth, its data in the named volume postgres-data, running.
    private FakeEngine.Box Postgres()
    {
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
        inspect["Config"]!["Env"]![0] = "POSTGRES_PASSWORD=" + Secret;
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["ExitCode"] = 0;
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        var box = _engine.Add(inspect);
        box.Listens = [5432];
        return box;
    }

    private async Task<ContainerEditRequest> EditAsync(string container, Func<ContainerSettings, ContainerSettings> change, ChangeKind kind = ChangeKind.Settings,
        string? expectedImageId = null)
    {
        var inspect = (await _engine.InspectContainerAsync(container, default))!;
        var loaded = ContainerSettingsJson.Read(inspect, await _engine.InspectImageAsync((string)inspect["Image"]!, default));
        return new ContainerEditRequest
        {
            ContainerId = _engine.Get(container).Id,
            Loaded = loaded,
            Wanted = change(loaded),
            Kind = kind,
            ExpectedImageId = expectedImageId,
        };
    }

    private static bool MountsWritable(FakeEngine.Box box, string volume) =>
        box.Mounts.OfType<JsonObject>().Any(m => (string?)m["Type"] == "volume" && (string?)m["Name"] == volume && m["RW"]?.GetValue<bool>() != false);

    /// <summary>
    /// DATA-1. New app from a paste of postgres's own docker run (-v postgres-data:/var/lib/postgresql postgres:18), or a
    /// missing app created again from its snapshot: the create mounts a volume the running postgres writes. Starting an old
    /// copy next to it is refused (ContainerService.SharedData); creating a new one must be too.
    /// </summary>
    [Fact]
    public async Task RevSafetyData_1_a_new_database_on_a_volume_a_running_database_writes_is_refused()
    {
        Postgres();
        var changer = Changer();
        var request = new NewContainerRequest
        {
            Name = "gb-postgres-2",
            Settings = new ContainerSettings
            {
                Image = "postgres:18",
                Env = [new EnvSetting("POSTGRES_PASSWORD", Secret)],
                Mounts = [new MountSetting(MountKind.Volume, "postgres-data", "/var/lib/postgresql")],
                Labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "postgres-2", ["gitberth.source"] = "manual" },
                Restart = new RestartSetting("unless-stopped"),
            },
            Networks = [new NetworkJoin("gitberth", ["postgres-2"])],
        };

        var plan = await changer.PlanCreateAsync(request);
        var result = plan.CanApply ? await changer.ApplyAsync(plan) : null;

        var writers = _engine.Boxes.Where(b => b.Running && MountsWritable(b, "postgres-data")).Select(b => b.Name).ToList();
        Assert.True(writers.Count <= 1,
            $"Plan blocked: {plan.Blocked ?? "no"}; impact: {string.Join(" | ", plan.Impact)}; result: {result?.Outcome} {result?.Message}. "
            + $"Running database containers writing postgres-data now: {string.Join(", ", writers)}.");
    }

    /// <summary>
    /// DATA-2. Roll back writes the previous image's ID into Config.Image (UpdateService.PrepareRollbackAsync gives
    /// Wanted.Image = the ID). Gitberth knows a database by its image's name, so after a rollback postgres is no longer one:
    /// the shared-data start guard, the 60 s stop and the database plan lines all stop applying to it.
    /// </summary>
    [Fact]
    public async Task RevSafetyData_2_a_rolled_back_database_is_still_known_as_a_database()
    {
        Postgres();
        var previous = (JsonObject)DockerFixtures.Json("image-postgres.json").AsObject().DeepClone();
        var previousId = "sha256:" + new string('b', 64);
        previous["Id"] = previousId;
        previous["RepoTags"] = new JsonArray();
        previous["RepoDigests"] = new JsonArray("postgres@" + previousId);
        _engine.Images["previous"] = previous;
        var changer = Changer();

        var rollback = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Image = previousId }, ChangeKind.Rollback, previousId));
        Assert.True(rollback.CanApply, rollback.Blocked);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(rollback)).Outcome);
        var configImage = (string)_engine.Get("postgres").Config["Image"]!;

        // The next change on it, and an old copy of it started next to it.
        _engine.Calls.Clear();
        var next = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Env = [.. s.Env, new EnvSetting("TZ", "UTC")] }));
        await changer.ApplyAsync(next);
        var data = new ContainerMount("volume", "postgres-data", "/var/lib/docker/volumes/postgres-data/_data", "/var/lib/postgresql", false);
        var oldCopyMayStart = ContainerService.SharedData("postgres-before-gitberth", [data], [("postgres", configImage, [data])]) is null;

        var problems = new List<string>();
        if (!ContainerService.IsDatabaseImage(configImage)) problems.Add($"Config.Image is {configImage}: not recognized as a database");
        if (!_engine.Calls.Contains("stop postgres t=60")) problems.Add($"the next recreate stops it with {_engine.Calls.FirstOrDefault(c => c.StartsWith("stop postgres", StringComparison.Ordinal))}, not t=60");
        if (oldCopyMayStart) problems.Add("ContainerService.SharedData lets postgres-before-gitberth start next to it");
        Assert.Empty(problems);
    }

    /// <summary>
    /// DATA-3. postgres 18's images keep their data in /var/lib/postgresql/18/docker (VOLUME /var/lib/postgresql); 17's in
    /// /var/lib/postgresql/data (VOLUME /var/lib/postgresql/data). Changing the image moves PGDATA to a folder no volume of
    /// the container holds: the new server initializes an empty cluster, starts, passes its checks, and the original is
    /// removed. The plan says "A new major version refuses to start, and the old container comes back."
    /// </summary>
    [Fact]
    public async Task RevSafetyData_3_a_database_whose_new_image_keeps_its_data_elsewhere_keeps_the_original()
    {
        Postgres();
        var seventeen = (JsonObject)DockerFixtures.Json("image-postgres.json").AsObject().DeepClone();
        seventeen["Id"] = "sha256:" + new string('7', 64);
        seventeen["RepoTags"] = new JsonArray("postgres:17");
        seventeen["RepoDigests"] = new JsonArray("postgres@sha256:" + new string('7', 64));
        seventeen["Config"]!["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/lib/postgresql/17/bin", "GOSU_VERSION=1.17",
            "LANG=en_US.utf8", "PG_MAJOR=17", "PG_VERSION=17.6-1.pgdg13+1", "PGDATA=/var/lib/postgresql/data");
        seventeen["Config"]!["Volumes"] = new JsonObject { ["/var/lib/postgresql/data"] = new JsonObject() };
        _engine.Images["postgres:17"] = seventeen;
        var original = _engine.Get("postgres").Id;
        var changer = Changer();

        // Adapted by the backend fixer to the coordinator's decision (DATA-3): another major version of a database is refused
        // at the plan, with why, rather than applied and caught afterwards.
        var plan = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Image = "postgres:17" }));
        Assert.False(plan.CanApply, "postgres:18 → postgres:17 was allowed");
        Assert.Equal("A new major version of PostgreSQL can't read 18's data: dump it and restore it into a new app instead.", plan.Blocked);
        var result = await changer.ApplyAsync(plan);

        Assert.True(_engine.Find(original) is not null && result.Outcome == PortChangeOutcome.NothingChanged,
            $"Got: {result.Outcome}: {result.Message} The original container is gone.");
    }

    /// <summary>
    /// DATA-3, same major version: a tag of the same major whose data folder the container's volume holds goes ahead; one
    /// whose data folder no volume holds is refused, since it would start on an empty database.
    /// </summary>
    [Fact]
    public async Task RevSafetyData_3b_a_same_major_change_goes_ahead_unless_its_data_folder_isnt_on_a_volume()
    {
        Postgres();
        var alpine = (JsonObject)DockerFixtures.Json("image-postgres.json").AsObject().DeepClone();
        alpine["Id"] = "sha256:" + new string('a', 64);
        alpine["RepoTags"] = new JsonArray("postgres:18-alpine");
        alpine["RepoDigests"] = new JsonArray("postgres@sha256:" + new string('a', 64));
        _engine.Images["postgres:18-alpine"] = alpine;
        var elsewhere = (JsonObject)alpine.DeepClone();
        elsewhere["Id"] = "sha256:" + new string('e', 64);
        elsewhere["RepoTags"] = new JsonArray("postgres:18-elsewhere");
        elsewhere["RepoDigests"] = new JsonArray("postgres@sha256:" + new string('e', 64));
        elsewhere["Config"]!["Volumes"] = new JsonObject { ["/srv/pgdata"] = new JsonObject() };
        elsewhere["Config"]!["Env"] = new JsonArray("PG_MAJOR=18", "PGDATA=/srv/pgdata");
        _engine.Images["postgres:18-elsewhere"] = elsewhere;
        var changer = Changer();

        var same = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Image = "postgres:18-alpine" }));
        var moved = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Image = "postgres:18-elsewhere" }));

        Assert.True(same.CanApply, same.Blocked);
        Assert.Contains(same.Impact, i => i.StartsWith("postgres checks its data files when it starts", StringComparison.Ordinal));
        Assert.False(moved.CanApply);
        Assert.StartsWith("postgres:18-elsewhere keeps its data in /srv/pgdata, which none of postgres's volumes holds", moved.Blocked, StringComparison.Ordinal);
    }

    /// <summary>
    /// DATA-4. Remove › "Also delete its image: when no other container uses it". On CT 101 postgres:18 is also tagged
    /// postgres:latest, so `docker image rm postgres:18` only untags it, whoever uses it: removing postgres-before-gitberth
    /// with the box ticked takes the tag off the image the live postgres runs. Nothing may be asked of Docker for an image
    /// another container runs.
    /// </summary>
    [Fact]
    public async Task RevSafetyData_4_removing_an_old_copy_with_its_image_leaves_the_image_postgres_runs()
    {
        _rig.Engine.AddLike("postgres", "postgres-before-gitberth", state: "exited");

        var removed = await _rig.Actions.RemoveAsync("postgres-before-gitberth", new RemoveOptions(Volumes: false, Image: true));

        Assert.True(removed.Ok, removed.Message);
        Assert.DoesNotContain("docker image rm postgres:18", _rig.Engine.Writes);
    }

    /// <summary>
    /// DATA-5. After an update, the image the app ran two updates ago goes when unused (UpdateService.FollowUpAsync). By
    /// then its followed tag has moved on, so a name it still has was given by someone else (postgres:latest on CT 101, or
    /// a "postgres:18-before-upgrade" kept by hand); RemoveIfUnusedAsync removes it all the same when it has one.
    /// </summary>
    [Fact]
    public async Task RevSafetyData_5_tidying_after_an_update_leaves_an_image_someone_else_named()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var images = ImageKit.Images(engine, db);
        engine.Add(ImageKit.Image(ImageKit.Id('3'), ["postgres:18-before-upgrade"], ["postgres@" + ImageKit.Digest('3')]));

        await images.RemoveIfUnusedAsync(ImageKit.Id('3'));

        Assert.DoesNotContain(engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
    }

    /// <summary>
    /// DATA-6 (defence in depth). Pinning Gitberth to the menu gives it a row (adoption); from then on AppActions' key and
    /// int Stop go to RunContainerAsync directly, past ContainerService.Refusal, so only the UI keeps Gitberth from stopping
    /// itself (unless-stopped: it would stay down).
    /// </summary>
    [Fact]
    public async Task RevSafetyData_6_gitberth_never_stops_itself_once_it_has_a_row()
    {
        var options = await _rig.Catalog.OptionsAsync("gitberth");
        await _rig.Catalog.SaveOptionsAsync("gitberth", options with { Pinned = true });
        Assert.NotNull(await _rig.RowAsync(a => a.Container == "gitberth"));

        var stopped = await _rig.Actions.StopAsync("gitberth");

        Assert.DoesNotContain(_rig.Engine.Writes, w => w.StartsWith("docker stop", StringComparison.Ordinal) && w.EndsWith(" gitberth", StringComparison.Ordinal));
        Assert.NotNull(stopped);
    }
}
