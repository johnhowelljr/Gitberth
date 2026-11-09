using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The change engine for any setting (section 2.3 of the unified design), against the engine kept in memory: a recreate
/// that writes only the changed fields, a live docker update, an update on a new image, and creating a container, each with
/// the checks, rollback and recovery a port change has.
/// </summary>
public sealed class PortChangerSettingsTests
{
    private const string OldName = "postgres_old_0ebf31bbcd07";
    private const string Secret = "s3cret-pass";

    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly FakeHost _host;
    private readonly MemoryJournal _journal = new();
    private readonly ContainerLocks _locks = new();
    private readonly FakeProbe _probe = new();

    public PortChangerSettingsTests()
    {
        _engine = new FakeEngine(_clock) { OnCreate = b => b.Listens = [5432] };
        _host = new FakeHost(_engine);
        _engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
        _engine.Images["alpine:3.20"] = ContainerSettingsJsonTests.Image("sha256:" + new string('a', 64), new JsonObject
        {
            ["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"),
            ["Cmd"] = new JsonArray("/bin/sh"),
        });
    }

    private sealed class FakeProbe : IHealthProbe
    {
        public Func<string, int, string, string?> Answer { get; set; } = (_, _, _) => null;
        public List<string> Checks { get; } = [];

        public Task<string?> CheckAsync(string host, int port, string path, CancellationToken ct)
        {
            lock (Checks) Checks.Add(HealthProbe.Url(host, port, path));
            return Task.FromResult(Answer(host, port, path));
        }
    }

    private PortChanger Changer(ContainerLocks? locks = null)
    {
        PortChanger? changer = null;
        var updater = new ContainerUpdater(_engine, locks ?? _locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(_engine, _journal, locks ?? _locks, _host, _host, _host, _host, _host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, _clock, updater, _probe);
        return changer;
    }

    private FakeEngine.Box Postgres(bool running = true, Action<JsonObject>? change = null)
    {
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
        inspect["Config"]!["Env"]![0] = "POSTGRES_PASSWORD=" + Secret;
        if (running)
        {
            inspect["State"]!["Running"] = true;
            inspect["State"]!["Status"] = "running";
            inspect["State"]!["ExitCode"] = 0;
            inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        }
        change?.Invoke(inspect);
        var box = _engine.Add(inspect);
        box.Listens = [5432];
        return box;
    }

    private async Task<ContainerSettings> SettingsAsync(string container)
    {
        var inspect = (await _engine.InspectContainerAsync(container, default))!;
        return ContainerSettingsJson.Read(inspect, await _engine.InspectImageAsync((string)inspect["Image"]!, default));
    }

    private async Task<ContainerEditRequest> EditAsync(string container, Func<ContainerSettings, ContainerSettings> change, ChangeKind kind = ChangeKind.Settings,
        string? expectedImageId = null, string? healthPath = null, int? webPort = null)
    {
        var loaded = await SettingsAsync(container);
        return new ContainerEditRequest
        {
            ContainerId = _engine.Get(container).Id,
            Loaded = loaded,
            Wanted = change(loaded),
            Kind = kind,
            ExpectedImageId = expectedImageId,
            HealthPath = healthPath,
            WebPort = webPort,
        };
    }

    private static ContainerSettings WithEnv(ContainerSettings s, string key, string value) =>
        s with { Env = [.. s.Env.Where(e => e.Key != key), new EnvSetting(key, value)] };

    private string Policy(string container) => (string)_engine.Get(container).HostConfig["RestartPolicy"]!["Name"]!;

    private static List<string> Env(FakeEngine.Box box) => box.Config["Env"]!.AsArray().Select(e => (string)e!).ToList();

    [Fact]
    public async Task The_plan_calls_it_by_the_apps_name_and_its_steps_speak_of_the_old_and_new_container()
    {
        // An app's dialog names it as its page does; only an address (or the parked name) keeps the container's.
        Postgres();
        var changer = Changer();
        var request = await EditAsync("postgres", s => WithEnv(s, "TZ", "Europe/London")) with { Title = "Main database" };

        var plan = await changer.PlanAsync("postgres", request);

        Assert.Equal("Main database restarts once: about 5 s offline.", plan.Impact[0]);
        Assert.Contains("Stopped the old container (SIGINT, waits up to 60 s)", plan.Steps);
        Assert.Contains("Started the new container", plan.Steps);
        Assert.DoesNotContain(plan.Steps, s => s.Contains("postgres", StringComparison.Ordinal) && !s.StartsWith("Swapped the names", StringComparison.Ordinal));
        // Without a name of its own, a container is called by its own.
        Assert.Equal("postgres restarts once: about 5 s offline.", (await changer.PlanAsync("postgres", request with { Title = null })).Impact[0]);
    }

    [Fact]
    public async Task Recreates_a_running_container_with_a_new_variable_and_nothing_else_changed()
    {
        var old = Postgres();
        var changer = Changer();
        var request = await EditAsync("postgres", s => WithEnv(s, "TZ", "Europe/London"));

        var plan = await changer.PlanAsync("postgres", request);

        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Equal((ChangeKind.Settings, false), (plan.Kind, plan.LiveOnly));
        Assert.Equal([SettingField.Env], plan.Fields);
        Assert.Empty(plan.Changes);
        Assert.Equal([new SettingLine(SettingField.Env, "Added TZ", "", "Europe/London", ApplyKind.Recreate)], plan.Settings);
        Assert.Equal("changed TZ", plan.Summary);
        // The environment changes, so it isn't kept; the rest is.
        Assert.Equal("Its name, image postgres:18 (3a4264520e12, not pulled again), command, volume postgres-data, network gitberth (names postgres), "
            + "restart policy, health check and hostname.", plan.Keeps);
        Assert.Equal("postgres restarts once: about 5 s offline.", plan.Impact[0]);
        Assert.DoesNotContain(Secret, plan.ToString());

        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal(
        [
            "create postgres_new_0ebf31bbcd07", "update postgres restart=no", "stop postgres t=60", $"rename postgres {OldName}",
            "rename postgres_new_0ebf31bbcd07 postgres", "start postgres", "update postgres restart=unless-stopped", $"remove {OldName}",
        ], _engine.Calls);
        var now = Assert.Single(_engine.Boxes);
        Assert.NotEqual(old.Id, now.Id);
        Assert.Contains("TZ=Europe/London", Env(now));
        Assert.Contains("POSTGRES_PASSWORD=" + Secret, Env(now));
        // Everything else is as it was: the image, the entrypoint and command, the health check, the volume, the hostname.
        Assert.Equal(old.Config["Image"]!.ToJsonString(), now.Config["Image"]!.ToJsonString());
        Assert.Equal(old.Config["Entrypoint"]!.ToJsonString(), now.Config["Entrypoint"]!.ToJsonString());
        Assert.Equal(old.Config["Cmd"]!.ToJsonString(), now.Config["Cmd"]!.ToJsonString());
        Assert.Equal(old.Config["Healthcheck"]!.ToJsonString(), now.Config["Healthcheck"]!.ToJsonString());
        Assert.Equal("0ebf31bbcd07", (string)now.Config["Hostname"]!);
        Assert.Equal(old.HostConfig["Binds"]!.ToJsonString(), now.HostConfig["Binds"]!.ToJsonString());
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeKind.Settings, "Env", (string?)null, "postgres:18", (string?)null, ChangeOutcome.Done),
            (row.Kind, row.Fields, row.NewRestartPolicy, row.ImageBefore, row.ImageAfter, row.Outcome));
        Assert.Equal($"postgres is back with its new settings. It was offline for {(result.Offline!.Value.TotalSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s.",
            result.Message);
    }

    [Fact]
    public async Task Changing_nothing_has_nothing_to_apply()
    {
        Postgres();
        var plan = await Changer().PlanAsync("postgres", await EditAsync("postgres", s => s with { Env = s.Env.ToList() }));

        Assert.False(plan.CanApply);
        Assert.Empty(plan.Fields);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task The_journal_row_holds_no_value_of_a_setting()
    {
        Postgres();
        var changer = Changer();

        await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "API_TOKEN", "tok-9f8e7d6c5b4a"))));

        var row = Assert.Single(_journal.Rows);
        Assert.Equal("Env", row.Fields);
        Assert.DoesNotContain(Secret, row.ToString());
        Assert.DoesNotContain("tok-9f8e7d6c5b4a", row.ToString());
        Assert.Equal("changed API_TOKEN", row.Summary);
    }

    [Fact]
    public async Task A_new_container_that_fails_to_start_puts_the_original_back()
    {
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(2);
            b.Output = "FATAL: password authentication failed for user postgres with " + Secret;
        };
        var changer = Changer();

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "POSTGRES_PASSWORD", "n3w-" + Secret))));

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true, "unless-stopped"), (back.Id, back.Name, back.Running, Policy("postgres")));
        Assert.Contains("POSTGRES_PASSWORD=" + Secret, Env(back));
        // Neither password shows in what's kept or shown.
        Assert.DoesNotContain(Secret, result.Message + result.LastOutput);
        Assert.DoesNotContain(Secret, _journal.Rows.Single().ToString());
        Assert.Equal(ChangeOutcome.RolledBack, _journal.Rows.Single().Outcome);
    }

    [Fact]
    public async Task A_new_restart_policy_is_given_at_the_end_and_the_old_one_comes_back_on_a_rollback()
    {
        // With another field, a restart policy is part of the recreate: the new container gets it once it has passed.
        Postgres();
        var changer = Changer();
        var request = await EditAsync("postgres", s => WithEnv(s, "TZ", "UTC") with { Restart = new RestartSetting("always") });

        var plan = await changer.PlanAsync("postgres", request);
        Assert.False(plan.LiveOnly);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(plan)).Outcome);

        Assert.Equal("always", Policy("postgres"));
        Assert.Equal("""{"Name":"always","MaximumRetryCount":0}""", _journal.Rows.Single().NewRestartPolicy);
        Assert.Equal("update postgres restart=always", _engine.Calls[^2]);

        // The same change on a container whose new one fails: the original keeps its own policy.
        _engine.Boxes.Clear();
        _engine.Calls.Clear();
        Postgres(change: i => i["Id"] = "1ebf31bbcd07" + new string('c', 52));
        _engine.OnCreate = b => b.ExitsAfter = TimeSpan.FromSeconds(1);
        Assert.Equal(PortChangeOutcome.RolledBack, (await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres",
            s => WithEnv(s, "TZ", "UTC") with { Restart = new RestartSetting("always") })))).Outcome);
        Assert.Equal("unless-stopped", Policy("postgres"));
    }

    [Fact]
    public async Task Keeping_the_new_container_gives_it_the_new_restart_policy()
    {
        // The new container fails, and so does putting the original back: both are kept for the owner.
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        // The original takes its name back before its start (round 2, CONC-2): its start is refused by its ID.
        var originalId = _engine.Get("postgres").Id;
        _engine.Fail = (call, target) => call == "start" && _engine.Boxes.FirstOrDefault(b => b.Name == target)?.Id == originalId
            ? new EngineException(500, "OCI runtime create failed") : null;
        var changer = Changer();
        Assert.Equal(PortChangeOutcome.NeedsDecision, (await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres",
            s => WithEnv(s, "TZ", "UTC") with { Restart = new RestartSetting("on-failure", 3) })))).Outcome);
        Assert.StartsWith("A settings change on postgres didn't finish.", _journal.Rows.Single().Notice);
        _engine.Fail = null;
        _engine.OnCreate = null;

        var kept = await changer.KeepNewAsync("postgres");

        Assert.Equal(PortChangeOutcome.Done, kept.Outcome);
        Assert.Equal("""{"Name":"on-failure","MaximumRetryCount":3}""", _engine.Get("postgres").HostConfig["RestartPolicy"]!.ToJsonString());
    }

    [Theory]
    [InlineData("create", "postgres_new_0ebf31bbcd07", false, ChangeOutcome.NothingChanged)]
    [InlineData("update", "postgres", true, ChangeOutcome.NothingChanged)]
    [InlineData("stop", "postgres", true, ChangeOutcome.RolledBack)]
    [InlineData("rename", OldName, true, ChangeOutcome.RolledBack)]
    [InlineData("rename", "postgres", true, ChangeOutcome.RolledBack)]
    [InlineData("start", "postgres", true, ChangeOutcome.Done)]
    [InlineData("remove", OldName, false, ChangeOutcome.Done)]
    public async Task Recovers_an_interrupted_settings_change_with_the_right_restart_policy(string call, string target, bool afterItRan, ChangeOutcome outcome)
    {
        var old = Postgres();
        _engine.Hang = (call, target, afterItRan);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "UTC") with { Restart = new RestartSetting("always") })));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _engine.Hang = null;
        await Changer(new ContainerLocks()).RecoverAsync();

        var row = Assert.Single(_journal.Rows);
        Assert.Equal(outcome, row.Outcome);
        Assert.Equal(outcome switch
        {
            ChangeOutcome.NothingChanged => "Gitberth restarted before it changed postgres's settings, so nothing changed.",
            ChangeOutcome.RolledBack => "Gitberth restarted while it recreated postgres. It put the original back.",
            _ => "Gitberth restarted while it recreated postgres. It finished the change: the new container runs and passed its checks.",
        }, row.Notice);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal(("postgres", true), (now.Name, now.Running));
        // Finished, the new container has the new policy; undone, the original has its own back.
        Assert.Equal(outcome == ChangeOutcome.Done ? "always" : "unless-stopped", Policy("postgres"));
        Assert.Equal(outcome == ChangeOutcome.Done, now.Id != old.Id);
        Assert.Equal(outcome == ChangeOutcome.Done, Env(now).Contains("TZ=UTC"));
    }

    [Fact]
    public async Task A_stopped_container_is_recreated_stopped_and_its_first_start_is_checked()
    {
        var old = Postgres(running: false);
        var changer = Changer();

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres",
            s => WithEnv(s, "TZ", "UTC") with { Restart = new RestartSetting("always") })));

        Assert.Equal(PortChangeOutcome.WaitsForStart, result.Outcome);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("stop", StringComparison.Ordinal));
        Assert.Equal(("no", false), (Policy("postgres"), _engine.Get("postgres").Running));
        Assert.Equal(old.Id, _engine.Get(OldName).Id);

        await _engine.StartAsync("postgres", default);
        var started = await changer.ContainerStartedAsync("postgres");

        Assert.Equal(PortChangeOutcome.Done, started?.Outcome);
        Assert.Equal("postgres started with its new settings and passed its checks. The original is removed (its volumes stay).", started!.Message);
        Assert.Equal("always", Policy("postgres"));
        Assert.Single(_engine.Boxes);
    }

    [Fact]
    public async Task Limits_alone_are_a_docker_update_with_no_restart()
    {
        var old = Postgres();
        _engine.MemoryInUse["postgres"] = 600L << 20;
        var changer = Changer();
        var request = await EditAsync("postgres", s => s with
        {
            Resources = s.Resources with { MemoryBytes = 512L << 20, Cpus = 1.5, PidsLimit = 300 },
            Restart = new RestartSetting("always"),
        });

        var plan = await changer.PlanAsync("postgres", request);

        Assert.True(plan.CanApply, plan.Blocked);
        Assert.True(plan.LiveOnly);
        Assert.Equal(("", "", "No restart.", (TimeSpan?)null), (plan.Keeps, plan.AlsoChanges, plan.Footer, plan.OfflineEstimate));
        Assert.Equal(["It uses 600 MiB now: with a 512 MiB limit Docker stops it when it needs more."], plan.Impact);
        Assert.All(plan.Settings, l => Assert.Equal(ApplyKind.Live, l.How));
        Assert.Equal(["Updated its limits"], plan.Steps);

        var job = changer.Start(plan);
        var result = await job.Completion;

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal("4 setting changes. No restart.", result.Message);
        // An update call, no create: the same container, its limits set.
        Assert.Equal(["update postgres RestartPolicy={\"Name\":\"always\",\"MaximumRetryCount\":0} Memory=536870912 MemorySwap=1073741824 NanoCpus=1500000000 PidsLimit=300"],
            _engine.Calls);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, true, 536870912L, 1073741824L), (now.Id, now.Running, ContainerPorts.Number(now.HostConfig, "Memory"), ContainerPorts.Number(now.HostConfig, "MemorySwap")));
        Assert.Empty(_journal.Rows);
        Assert.Contains(job.Steps, s => s is { Text: "Updated its limits", State: StepState.Done });
    }

    [Fact]
    public async Task Taking_a_limit_away_or_moving_a_quota_to_cpus_is_a_recreate()
    {
        var sample = DockerFixtures.Inspect("sample-index");
        sample["State"]!["Running"] = true;
        sample["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        sample["HostConfig"]!["CpuQuota"] = 50000;
        sample["HostConfig"]!["CpuPeriod"] = 100000;
        _engine.Images["nginx:stable-alpine"] = ContainerSettingsJsonTests.NginxImage();
        _engine.Add(sample).Listens = [80];
        _engine.OnCreate = b => b.Listens = [80];
        var changer = Changer();

        var removed = await changer.PlanAsync("sample-index", await EditAsync("sample-index", s => s with { Resources = s.Resources with { MemoryBytes = null } }));
        var quota = await changer.PlanAsync("sample-index", await EditAsync("sample-index", s => s with { Resources = s.Resources with { Cpus = 1 } }));
        var raised = await changer.PlanAsync("sample-index", await EditAsync("sample-index", s => s with { Resources = s.Resources with { MemoryBytes = 128L << 20 } }));

        Assert.Equal((false, true), (removed.LiveOnly, removed.CanApply));
        Assert.Equal((false, true), (quota.LiveOnly, quota.CanApply));
        Assert.True(raised.LiveOnly);
        // The recreate clears the quota, and the new container has its CPUs.
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(quota)).Outcome);
        var now = _engine.Get("sample-index");
        Assert.Equal((0L, 1000000000L), (ContainerPorts.Number(now.HostConfig, "CpuQuota"), ContainerPorts.Number(now.HostConfig, "NanoCpus")));
    }

    [Fact]
    public async Task Live_and_recreate_fields_together_are_one_recreate_with_the_limits_in_its_body()
    {
        Postgres();
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "UTC") with { Resources = s.Resources with { MemoryBytes = 1L << 30 } }));
        var result = await changer.ApplyAsync(plan);

        Assert.False(plan.LiveOnly);
        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal(1L << 30, ContainerPorts.Number(_engine.Get("postgres").HostConfig, "Memory"));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("update postgres Memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_recreates_on_the_new_image_without_the_old_images_defaults()
    {
        // postgres:18 was pulled again: it names a newer image now, with another PG_VERSION and PATH.
        var old = Postgres();
        var oldImage = DockerFixtures.Json("image-postgres.json").AsObject();
        var newImage = (JsonObject)oldImage.DeepClone();
        newImage["Id"] = "sha256:9bc1d" + new string('e', 59);
        newImage["RepoDigests"] = new JsonArray("postgres@sha256:9bc1d" + new string('e', 59));
        newImage["Metadata"] = new JsonObject { ["LastTagTime"] = "2026-11-05T11:58:00Z" };
        newImage["Config"]!["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/lib/postgresql/18/bin:/opt/bin", "GOSU_VERSION=1.19",
            "LANG=en_US.utf8", "PG_MAJOR=18", "PG_VERSION=18.7-1.pgdg13+1", "PGDATA=/var/lib/postgresql/18/docker");
        _engine.Images["postgres:18"] = newImage;
        _engine.Images["postgres@old"] = oldImage;
        var changer = Changer();
        var request = await EditAsync("postgres", s => s, ChangeKind.Update, expectedImageId: (string)newImage["Id"]!);

        var plan = await changer.PlanAsync("postgres", request);

        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Equal(ChangeKind.Update, plan.Kind);
        Assert.Equal([SettingField.Image], plan.Fields);
        Assert.Equal(new SettingLine(SettingField.Image, "Image", "postgres:18 (3a4264520e12)", "postgres:18 (9bc1deeeeeee, pulled 2 min ago)", ApplyKind.Recreate), Assert.Single(plan.Settings));
        Assert.DoesNotContain("not pulled again", plan.Keeps);
        // DATA-3 (changed by the backend fixer): the old line promised what isn't so; another major version is refused instead.
        Assert.Contains("postgres checks its data files when it starts: if it can't use them, it fails its checks and the old container comes back.", plan.Impact);
        Assert.Contains("If the new version changes its data when it starts, putting the old one back doesn't undo that.", plan.Impact);
        Assert.Contains("From the new image: PG_VERSION 18.6-1.pgdg13+2 → 18.7-1.pgdg13+1", plan.Impact);
        Assert.Equal("updated postgres:18 (3a42… → 9bc1…)", plan.Summary);

        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal(((string)newImage["Id"]!, "postgres:18"), (now.Image, (string)now.Config["Image"]!));
        // The old image's variables went, so the daemon gives the new image's; the password stays.
        Assert.Equal(["POSTGRES_PASSWORD=" + Secret], Env(now));
        Assert.False(now.Config.ContainsKey("Entrypoint"));
        Assert.Equal("0ebf31bbcd07", (string)now.Config["Hostname"]!);
        var row = _journal.Rows.Single();
        // DATA-8 (changed by the backend fixer): an update keeps its tag, so the journal keeps the image it ran by its ID, for
        // Roll back after a restart of Gitberth.
        Assert.Equal((ChangeKind.Update, "Image", "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", "postgres:18"),
            (row.Kind, row.Fields, row.ImageBefore, row.ImageAfter));
        Assert.NotEqual(old.Id, now.Id);
    }

    [Fact]
    public async Task An_update_is_blocked_when_the_tag_moved_again()
    {
        Postgres();
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s, ChangeKind.Update, expectedImageId: "sha256:" + new string('f', 64)));

        Assert.Equal("postgres:18 moved again while it was pulled: check again.", plan.Blocked);
        Assert.False(plan.CanApply);
        var missing = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Image = "postgres:19" }));
        Assert.Equal("postgres:19 isn't on this host yet.", missing.Blocked);
    }

    [Fact]
    public async Task The_new_health_check_decides_the_wait()
    {
        // Its health check turned off: the new container only has to stay up, not report healthy.
        Postgres();
        var changer = Changer();
        var steps = new List<PortChangeStep>();

        var plan = await changer.PlanAsync("postgres", await EditAsync("postgres", s => s with { Health = new HealthSetting(HealthMode.Disabled) }));
        var result = await changer.ApplyAsync(plan, new Progress(steps));

        Assert.Contains("It stayed up for 15 s", plan.Steps);
        Assert.DoesNotContain("health check", plan.Keeps);
        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal("""{"Test":["NONE"]}""", _engine.Get("postgres").Config["Healthcheck"]!.ToJsonString());
    }

    [Fact]
    public async Task Its_health_url_must_answer_after_the_start()
    {
        Postgres();
        var changer = Changer();
        _probe.Answer = (_, _, _) => "http://postgres:8080/health answered HTTP 503";

        var plan = await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "UTC"), healthPath: "health", webPort: 8080));
        var failed = await changer.ApplyAsync(plan);

        Assert.Contains("http://postgres:8080/health answers", plan.Steps);
        Assert.Equal(PortChangeOutcome.RolledBack, failed.Outcome);
        Assert.StartsWith("The new postgres didn't answer its health URL: http://postgres:8080/health answered HTTP 503.", failed.Message);

        _probe.Answer = (_, _, _) => null;
        var passed = await changer.ApplyAsync(await changer.PlanAsync("postgres", await EditAsync("postgres", s => WithEnv(s, "TZ", "UTC"), healthPath: "/health", webPort: 8080)));
        Assert.Equal(PortChangeOutcome.Done, passed.Outcome);
        Assert.All(_probe.Checks, c => Assert.Equal("http://postgres:8080/health", c));
    }

    [Fact]
    public async Task The_tunnel_moves_off_the_hosts_network_and_says_its_domains_stop_meanwhile()
    {
        var tunnel = DockerFixtures.Inspect("cloudflare-tunnel");
        tunnel["State"]!["Running"] = true;
        tunnel["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        _engine.Images["cloudflare/cloudflared:latest"] = ContainerSettingsJsonTests.Image((string)tunnel["Image"]!, new JsonObject { ["Entrypoint"] = new JsonArray("cloudflared") });
        _engine.Add(tunnel);
        var changer = Changer();

        var plan = await changer.PlanAsync("cloudflare-tunnel", await EditAsync("cloudflare-tunnel", s => WithEnv(s, "TUNNEL_METRICS", "0.0.0.0:2000")));

        // The host's network refuses a port change, not a settings change.
        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Contains("Every domain it serves stops answering until it's back.", plan.Impact);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(plan)).Outcome);
        Assert.Equal("host", (string)_engine.Get("cloudflare-tunnel").HostConfig["NetworkMode"]!);
        Assert.Equal("always", Policy("cloudflare-tunnel"));
    }

    [Fact]
    public async Task Creates_a_container_checks_it_and_gives_it_its_restart_policy()
    {
        _engine.OnCreate = b => b.Listens = [80];
        var changer = Changer();
        var request = new NewContainerRequest
        {
            Name = "gb-web",
            Settings = new ContainerSettings
            {
                Image = "alpine:3.20",
                Command = ["httpd", "-f"],
                Ports = [new PortRule(80, "tcp", PortAccess.Lan, 9300)],
                Labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "web", ["gitberth.source"] = "registry" },
                Restart = new RestartSetting("unless-stopped"),
            },
            Networks = [new NetworkJoin("gitberth", ["web"])],
        };

        var plan = await changer.PlanCreateAsync(request);

        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Equal(["Creates gb-web and starts it. Reached at 10.1.1.80:9300."], plan.Impact);
        Assert.Equal(["Host port 9300 is free", "Created gb-web", "Started gb-web", "It stayed up for 15 s", "10.1.1.80:9300 answers", "Set its restart policy"], plan.Steps);
        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal("gb-web is created and answers on 10.1.1.80:9300 (checked from the host).", result.Message);
        var web = _engine.Get("gb-web");
        Assert.Equal((true, "unless-stopped", "gitberth"), (web.Running, Policy("gb-web"), (string)web.HostConfig["NetworkMode"]!));
        Assert.Equal("""{"gitberth":{"Aliases":["web"]}}""", web.Endpoints.ToJsonString());
        Assert.Equal(["create gb-web", "start gb-web", "update gb-web restart=unless-stopped"], _engine.Calls);
        // No journal row: there's no original to put back.
        Assert.Empty(_journal.Rows);
    }

    [Fact]
    public async Task A_create_is_refused_when_the_name_or_a_port_is_taken()
    {
        Postgres();
        _host.Holders[9300] = "sample-go-gin (container)";
        var changer = Changer();
        var settings = new ContainerSettings { Image = "alpine:3.20", Ports = [new PortRule(80, "tcp", PortAccess.Lan, 9300)] };

        var taken = await changer.PlanCreateAsync(new NewContainerRequest { Name = "postgres", Settings = settings with { Ports = [] }, Networks = [new NetworkJoin("gitberth", [])] });
        var port = await changer.PlanCreateAsync(new NewContainerRequest { Name = "gb-web", Settings = settings, Networks = [new NetworkJoin("gitberth", [])] });
        var image = await changer.PlanCreateAsync(new NewContainerRequest { Name = "gb-web", Settings = settings with { Image = "alpine:9", Ports = [] }, Networks = [new NetworkJoin("gitberth", [])] });

        Assert.Equal("There's already a container called postgres.", taken.Blocked);
        Assert.Equal("Port 9300 is taken by sample-go-gin (container).", port.Blocked);
        Assert.Equal("alpine:9 isn't on this host yet.", image.Blocked);
        Assert.Equal("Nothing was created: port 9300 is taken by sample-go-gin (container).", (await changer.ApplyAsync(port)).Message);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_create_that_fails_its_checks_leaves_nothing_behind()
    {
        _engine.OnCreate = b =>
        {
            b.ExitsAfter = TimeSpan.FromSeconds(3);
            b.Output = "sh: httpd: not found";
        };
        var changer = Changer();
        var plan = await changer.PlanCreateAsync(new NewContainerRequest
        {
            Name = "gb-web",
            Settings = new ContainerSettings { Image = "alpine:3.20", Command = ["httpd", "-f"], Restart = new RestartSetting("always") },
            Networks = [new NetworkJoin("gitberth", ["web"])],
        });

        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal("The new gb-web stopped after 3 s (exit code 1). It was removed: nothing is left in Docker but its volumes.", result.Message);
        Assert.Equal("sh: httpd: not found", result.LastOutput);
        Assert.Empty(_engine.Boxes);
        Assert.Equal(["create gb-web", "start gb-web", "remove gb-web force"], _engine.Calls);
    }

    private sealed class Progress(List<PortChangeStep> steps) : IProgress<PortChangeStep>
    {
        public void Report(PortChangeStep value)
        {
            lock (steps) steps.Add(value);
        }
    }
}
