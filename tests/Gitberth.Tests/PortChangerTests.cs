using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Changing a standalone container's ports by recreating it, against an engine kept in memory that takes host ports at
/// start, keeps names unique and can fail, or stop for good, at any call. postgres is the real container's inspect, as it
/// ran before the rename left its network mode on a network that's gone.
/// </summary>
public sealed class PortChangerTests
{
    private const string OldName = "postgres_old_0ebf31bbcd07";
    private const string TempName = "postgres_new_0ebf31bbcd07";
    private static readonly PortRule[] Publish5432 = [new(5432, "tcp", PortAccess.Lan, 5432)];

    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly FakeHost _host;
    private readonly MemoryJournal _journal = new();
    private readonly ContainerLocks _locks = new();

    public PortChangerTests()
    {
        _engine = new FakeEngine(_clock) { OnCreate = b => b.Listens = [5432] };
        _host = new FakeHost(_engine);
        _engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
        _engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = "sha256:0e52bd054126cdc040b1276c0af347aeb02a41b9cfe305d6094767ac30e97949" };
        _engine.Images["gitberth:local"] = new JsonObject { ["Id"] = "sha256:0c1a38670237d7bc11ee185585ec1309b54599d259ccd7930e81c2114472b125" };
    }

    private PortChanger Changer(ContainerLocks? locks = null) =>
        new(_engine, _journal, locks ?? _locks, _host, _host, _host, _host, _host,
            new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<PortChanger>.Instance, _clock);

    private FakeEngine.Box Postgres(bool running = true, Action<JsonObject>? change = null)
    {
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
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

    private FakeEngine.Box Sample(string name = "sample-index", Action<JsonObject>? change = null)
    {
        var inspect = DockerFixtures.Inspect("sample-index");
        inspect["Name"] = "/" + name;
        inspect["Id"] = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(name.PadRight(32, '-')[..32]));
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        change?.Invoke(inspect);
        var box = _engine.Add(inspect);
        box.Listens = [80];
        return box;
    }

    private async Task<PortChangeResult> ApplyAsync(IReadOnlyList<PortRule>? ports = null, string container = "postgres", List<PortChangeStep>? steps = null)
    {
        var changer = Changer();
        return await changer.ApplyAsync(await changer.PlanAsync(container, ports ?? Publish5432), steps is null ? null : new Progress(steps));
    }

    private string Policy(string container) => (string)_engine.Get(container).HostConfig["RestartPolicy"]!["Name"]!;

    // Whether Docker says it runs now: a stop still under way, or a restart by its policy, counts by this time.
    private async Task<bool> RunsAsync(string container) =>
        (await _engine.InspectContainerAsync(container, default))!["State"]!["Running"]!.GetValue<bool>();

    // A change that failed to put the original back: the new postgres exits after 4 s, and the original won't start.
    private async Task<PortChanger> FailedRollbackAsync()
    {
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = OriginalWontStart(_engine.Get("postgres").Id);
        var changer = Changer();
        Assert.Equal(PortChangeOutcome.NeedsDecision, (await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432))).Outcome);
        _engine.Fail = null;
        return changer;
    }

    private static string Seconds(TimeSpan? span) => span!.Value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    // The original won't start again, whatever it's called by then: a rollback gives it its name back before its start.
    private Func<string, string, EngineException?> OriginalWontStart(string id, string message = "OCI runtime create failed") =>
        (call, target) => call == "start" && _engine.Boxes.FirstOrDefault(b => b.Name == target)?.Id == id ? new EngineException(500, message) : null;

    // Fails the first matching call only, as a passing hiccup would.
    private static Func<string, string, EngineException?> Once(string call, string target, int status = 500, string message = "boom")
    {
        var done = 0;
        return (c, t) => c == call && t == target && Interlocked.Exchange(ref done, 1) == 0 ? new EngineException(status, message) : null;
    }

    private sealed class Progress(List<PortChangeStep> steps) : IProgress<PortChangeStep>
    {
        public void Report(PortChangeStep value)
        {
            lock (steps) steps.Add(value);
        }
    }

    [Fact]
    public async Task The_plan_reads_like_the_dialog()
    {
        Postgres();
        _host.Users = new ContainerUsers(["kanban"], [], []);

        var plan = await Changer().PlanAsync("postgres", Publish5432);

        Assert.True(plan.CanApply);
        Assert.Equal([new PortChangeLine("5432/tcp", "Internal", "LAN at 10.1.1.80:5432")], plan.Changes);
        Assert.Equal("5432/tcp  Internal → LAN at 10.1.1.80:5432", plan.Changes[0].Text);
        Assert.Equal("Its name, image postgres:18 (3a4264520e12, not pulled again), command, environment, volume postgres-data, "
            + "network gitberth (names postgres), restart policy, health check and hostname.", plan.Keeps);
        Assert.Equal("Its container ID, start time, restart count and log so far.", plan.AlsoChanges);
        Assert.Equal(["postgres restarts once: about 5 s offline.", "kanban uses postgres: its connections drop and reconnect."], plan.Impact);
        Assert.Equal("If the new container fails its checks, the old one comes back as it was.", plan.Footer);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.OfflineEstimate);
        Assert.Equal(
        [
            "Host port 5432 is free",
            "Created the new container",
            "Turned off the old container's restart policy",
            "Stopped the old container (SIGINT, waits up to 60 s)",
            "Swapped the names: the old one is postgres_old_0ebf31bbcd07",
            "Started the new container",
            "It reports healthy",
            "10.1.1.80:5432 answers",
            "Removed the old container (its volumes stay)",
        ], plan.Steps);
        Assert.Equal("published 5432/tcp on the LAN", plan.Summary);
        Assert.Equal([new NewBinding(5432, "tcp", "", 5432)], plan.Bindings);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_plan_never_holds_the_containers_settings()
    {
        // Its environment holds the database's password: a plan that's logged, kept or shown must not carry it. The create
        // body is built when the change is applied, from a fresh inspect.
        Postgres(change: i => i["Config"]!["Env"] = new JsonArray("POSTGRES_PASSWORD=s3cret-pass", "PGDATA=/var/lib/postgresql/18/docker"));

        var plan = await Changer().PlanAsync("postgres", Publish5432);

        Assert.True(plan.CanApply);
        Assert.DoesNotContain("s3cret-pass", plan.ToString());
        Assert.DoesNotContain(typeof(PortChangePlan).GetProperties(), p => typeof(JsonNode).IsAssignableFrom(p.PropertyType));
    }

    [Fact]
    public async Task Recreates_postgres_with_its_port_published()
    {
        var old = Postgres();
        var steps = new List<PortChangeStep>();

        var result = await ApplyAsync(steps: steps);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal($"postgres answers on 10.1.1.80:5432 (checked from the host). It was offline for {Seconds(result.Offline)}.", result.Message);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal(("postgres", true), (now.Name, now.Running));
        Assert.NotEqual(old.Id, now.Id);
        Assert.Equal([("0.0.0.0", 5432), ("::", 5432)], now.Live["5432/tcp"]);
        Assert.Equal("unless-stopped", Policy("postgres"));
        Assert.Equal("postgres:18", (string)now.Config["Image"]!);
        // The progress said each step as it ran, then done, in the dialog's words.
        Assert.Equal(
        [
            "Host port 5432 is free", "Created the new container", "Turned off the old container's restart policy",
            "Stopped the old container (SIGINT, waits up to 60 s)", "Swapped the names: the old one is postgres_old_0ebf31bbcd07",
            "Started the new container", "It reports healthy", "10.1.1.80:5432 answers", "Removed the old container (its volumes stay)",
        ], steps.Where(s => s.State == StepState.Done).Select(s => s.Text));
        Assert.All(steps, s => Assert.Equal(9, s.Count));
        var starting = Assert.Single(steps, s => s.State == StepState.Running && s.Number == 6);
        Assert.Equal(("Starting the new container", "Next: it must report healthy, then 10.1.1.80:5432 must answer."), (starting.Text, starting.Next));
        Assert.Equal("1.4 s", steps.Single(s => s is { Number: 4, State: StepState.Done }).TookText);
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.Done, ChangeStep.Finished, "published 5432/tcp on the LAN", old.Id, now.Id), (row.Outcome, row.Step, row.Summary, row.OldId, row.NewId));
    }

    [Fact]
    public async Task Creates_the_new_container_before_stopping_the_old_one()
    {
        Postgres();
        string? policyAtCreate = null;
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            policyAtCreate = (string)b.HostConfig["RestartPolicy"]!["Name"]!;
        };

        await ApplyAsync();

        Assert.Equal(
        [
            "create postgres_new_0ebf31bbcd07",
            "update postgres restart=no",
            "stop postgres t=60",
            "rename postgres postgres_old_0ebf31bbcd07",
            "rename postgres_new_0ebf31bbcd07 postgres",
            "start postgres",
            "update postgres restart=unless-stopped",
            "remove postgres_old_0ebf31bbcd07",
        ], _engine.Calls);
        // The new one has no restart policy until it has passed its checks: nothing restarts it while it's checked or
        // rolled back. Then it gets the original's.
        Assert.Equal(("no", "unless-stopped"), (policyAtCreate, Policy("postgres")));
    }

    [Fact]
    public async Task Turns_off_the_old_restart_policy_before_stopping_it()
    {
        Postgres();

        await ApplyAsync();

        // Neither the daemon nor "always" can start the old one next to the new one on the same volume.
        Assert.True(_engine.Calls.IndexOf("update postgres restart=no") < _engine.Calls.IndexOf("stop postgres t=60"));
    }

    [Theory]
    [InlineData("postgres", null, "stop postgres t=60", "Stopped the old container (SIGINT, waits up to 60 s)")]
    [InlineData("postgres", 7, "stop postgres t=7", "Stopped the old container (SIGINT, waits up to 7 s)")]
    [InlineData("postgres", -1, "stop postgres t=-1", "Stopped the old container (SIGINT, waits until it stops)")]
    [InlineData("sample-index", null, "stop sample-index t=10", "Stopped the old container (SIGQUIT, waits up to 10 s)")]
    [InlineData("sample-index", 0, "stop sample-index t=0", "Stopped the old container (SIGQUIT, kills it at once)")]
    public async Task Uses_a_longer_stop_timeout_for_a_database_without_its_own(string container, int? own, string stop, string step)
    {
        // A database stopped cleanly needs no crash recovery at its next start; others get Docker's 10 s. A container's
        // own timeout always counts: --stop-timeout 0 kills it at once, and -1 never does.
        Action<JsonObject> timeout = i => { if (own is { } t) i["Config"]!["StopTimeout"] = t; };
        if (container == "postgres") Postgres(change: timeout);
        else Sample(change: timeout);
        var changer = Changer();

        var plan = await changer.PlanAsync(container, container == "postgres" ? Publish5432 : [new PortRule(80, "tcp", PortAccess.Lan, 9201)]);
        await changer.ApplyAsync(plan);

        Assert.Contains(step, plan.Steps);
        Assert.Contains(stop, _engine.Calls);
    }

    [Fact]
    public async Task A_rejected_create_changes_nothing()
    {
        var old = Postgres();
        _engine.Fail = (call, _) => call == "create" ? new EngineException(400, "invalid mount config for type \"bind\": bind source path does not exist: /srv/missing") : null;

        var result = await ApplyAsync();

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged,
            "Nothing changed: Docker refused the new container (invalid mount config for type \"bind\": bind source path does not exist: /srv/missing)."), result);
        Assert.Same(old, Assert.Single(_engine.Boxes));
        Assert.True(old.Running);
        Assert.Empty(_engine.Calls);
        Assert.Equal(ChangeOutcome.NothingChanged, Assert.Single(_journal.Rows).Outcome);
    }

    [Fact]
    public async Task A_name_another_container_took_meanwhile_changes_nothing()
    {
        var old = Postgres();
        // Free at the preflight, then someone else's container takes the temporary name, made just now.
        FakeEngine.Box? theirs = null;
        _host.Probe = _ =>
        {
            theirs = Sample(TempName, i =>
            {
                i["State"]!["Running"] = false;
                i["Created"] = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            });
            return null;
        };

        var result = await ApplyAsync();

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, $"Nothing changed: another container is already called {TempName}."), result);
        // Docker's 409 says the name is someone else's: their container stays, and the original runs as before.
        Assert.Same(theirs, _engine.Find(TempName));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
        Assert.Equal((old.Id, true), (_engine.Get("postgres").Id, _engine.Get("postgres").Running));
    }

    [Theory]
    [InlineData(true, "address already in use")]
    [InlineData(false, "port is already allocated")]
    public async Task A_port_taken_at_start_brings_the_old_container_back(bool byAProgram, string reason)
    {
        var old = Postgres();
        // Free when the probe tried it, taken right after.
        _host.Probe = _ =>
        {
            if (byAProgram) _engine.HostPrograms.Add(5432);
            else Sample("sample-pg", i => i["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}]}"""));
            return null;
        };

        var result = await ApplyAsync();

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal($"postgres didn't start with port 5432 published ({reason}). The old postgres is back as it was; offline {Seconds(result.Offline)} in all.", result.Message);
        var back = _engine.Get("postgres");
        Assert.Equal((old.Id, true, "unless-stopped"), (back.Id, back.Running, Policy("postgres")));
        Assert.DoesNotContain(_engine.Boxes, b => b.Name is TempName or OldName);
        Assert.Equal(ChangeOutcome.RolledBack, Assert.Single(_journal.Rows).Outcome);
    }

    [Fact]
    public async Task A_new_container_that_exits_brings_the_old_one_back_with_its_policy()
    {
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
            b.Output = "2026-11-05 12:00:01.123 UTC [1] FATAL:  could not open file \"postgresql.conf\"\n";
        };

        var result = await ApplyAsync();

        // The original's unless-stopped would have Docker restart a crashing copy again and again, which reads as running
        // all along: the new one has no policy while it's checked, so its crash shows at once, with its own exit code.
        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal("The new postgres stopped after 4 s (exit code 1). The old one is back as it was; offline 6.3 s in all.", result.Message);
        Assert.Equal(TimeSpan.FromSeconds(6.3), result.Offline);
        Assert.Equal("2026-11-05 12:00:01.123 UTC [1] FATAL:  could not open file \"postgresql.conf\"", result.LastOutput);
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true, "unless-stopped"), (back.Id, back.Name, back.Running, Policy("postgres")));
    }

    [Fact]
    public async Task A_new_container_that_keeps_restarting_fails_its_checks_at_once()
    {
        // Something gave the new one a restart policy after all: Docker restarts it 100 ms after each crash, and it counts as
        // running all along, its health check starting over. The checks still end at the first restart, not at the end of
        // the health check's 70 s window.
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ListenAfter = TimeSpan.FromSeconds(30);
            b.ExitsAfter = TimeSpan.FromSeconds(4);
            b.HostConfig["RestartPolicy"] = new JsonObject { ["Name"] = "always", ["MaximumRetryCount"] = 0 };
        };

        var result = await ApplyAsync();

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.StartsWith("The new postgres stopped after 4 s (exit code ", result.Message);
        Assert.InRange(result.Offline!.Value, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", "unless-stopped"), (back.Id, back.Name, Policy("postgres")));
        Assert.True(await RunsAsync("postgres"));
    }

    [Theory]
    [InlineData(false, "The new postgres reported unhealthy (connection refused). The old one is back as it was.")]
    [InlineData(true, "The new postgres ran out of memory. The old one is back as it was.")]
    public async Task An_unhealthy_or_out_of_memory_container_brings_the_old_one_back(bool outOfMemory, string message)
    {
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            if (outOfMemory)
            {
                b.ExitsAfter = TimeSpan.FromSeconds(3);
                b.OutOfMemory = true;
            }
            else b.Unhealthy = "pg_isready: connection refused\nconnection refused\n";
        };

        var result = await ApplyAsync();

        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, message) { Offline = result.Offline, LastOutput = result.LastOutput }, result);
        Assert.True(_engine.Get("postgres").Running);
        // Killed after 3 s, it's found at the next read: the old one is back 5.3 s after the stop. Unhealthy only shows
        // when its health check says so, 10 s after its start, and the new one takes 1.4 s to stop.
        Assert.Equal(TimeSpan.FromSeconds(outOfMemory ? 5.3 : 13.7), result.Offline);
    }

    [Fact]
    public async Task A_failed_rollback_keeps_both_containers()
    {
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = OriginalWontStart(old.Id, "failed to create task for container: OCI runtime create failed");
        var changer = Changer();

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        // The original has its name back before its start (round 2, CONC-2), so it's postgres that won't start.
        Assert.Equal(PortChangeOutcome.NeedsDecision, result.Outcome);
        Assert.Equal("The new postgres failed, and the old one (postgres) didn't start again: "
            + "failed to create task for container: OCI runtime create failed. Nothing was deleted.", result.Message);
        Assert.Equal(["postgres", TempName], _engine.Boxes.Select(b => b.Name).Order());
        Assert.Equal(old.Id, _engine.Get("postgres").Id);
        var row = Assert.Single(_journal.Rows);
        Assert.Equal(ChangeOutcome.NeedsDecision, row.Outcome);
        Assert.Equal("A port change on postgres didn't finish. Both containers are kept: postgres_new_0ebf31bbcd07 (new, stopped) and postgres (the original).", row.Notice);
        // A further change waits for the decision.
        Assert.Equal("A port change on postgres waits for your decision: keep the new container or put the original back.",
            (await changer.PlanAsync("postgres", Publish5432)).Blocked);

        // [Start the original], once it can.
        _engine.Fail = null;
        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, "postgres is back as it was."), await changer.PutOriginalBackAsync("postgres"));
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true, "unless-stopped"), (back.Id, back.Name, back.Running, Policy("postgres")));
        Assert.Equal(ChangeOutcome.RolledBack, Assert.Single(_journal.Rows).Outcome);
    }

    [Fact]
    public async Task Keeps_the_new_one_on_request()
    {
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = OriginalWontStart(old.Id);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        _engine.Fail = null;
        _engine.Get(TempName).ExitsAfter = null;

        var kept = await changer.KeepNewAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.Done, "postgres keeps the new container. The original is removed (its volumes stay)."), kept);
        var now = Assert.Single(_engine.Boxes);
        Assert.NotEqual(old.Id, now.Id);
        // The failed rollback had turned its policy off; it has the original's again.
        Assert.Equal(("postgres", true, "unless-stopped"), (now.Name, now.Running, Policy("postgres")));
        Assert.Equal(ChangeOutcome.Done, Assert.Single(_journal.Rows).Outcome);
    }

    [Theory]
    [InlineData("create", TempName, false, ChangeOutcome.NothingChanged, "Nothing changed: Docker refused the new container (boom).")]
    [InlineData("update", "postgres", false, ChangeOutcome.NothingChanged, "Nothing changed: Docker didn't turn off postgres's restart policy (boom).")]
    [InlineData("stop", "postgres", false, ChangeOutcome.NothingChanged, "Couldn't stop postgres (boom), so nothing changed: it runs as before.")]
    [InlineData("stop", "postgres", true, ChangeOutcome.RolledBack, "Couldn't stop postgres cleanly (boom). postgres is back as it was; offline ")]
    [InlineData("rename", OldName, false, ChangeOutcome.RolledBack, "Couldn't rename postgres to postgres_old_0ebf31bbcd07 (boom). postgres is back as it was; offline ")]
    [InlineData("rename", "postgres", false, ChangeOutcome.RolledBack, "Couldn't give the new container the name postgres (boom). postgres is back as it was; offline ")]
    public async Task Each_failing_step_is_undone(string call, string target, bool afterItRan, ChangeOutcome outcome, string message)
    {
        var old = Postgres();
        // Before the call did anything, or after it did (its answer lost, or late).
        if (afterItRan) _engine.FailAfter = Once(call, target);
        else _engine.Fail = Once(call, target);

        var result = await ApplyAsync();

        Assert.StartsWith(message, result.Message);
        Assert.Equal(outcome, Assert.Single(_journal.Rows).Outcome);
        // Whatever failed, postgres is the original again: running, under its name, with its policy, and alone.
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true, "unless-stopped"), (now.Id, now.Name, now.Running, Policy("postgres")));
    }

    [Theory]
    [InlineData(OldName)]
    [InlineData(TempName)]
    public async Task Checks_that_both_temporary_names_are_free(string name)
    {
        Postgres();
        Sample(name, i => i["State"]!["Running"] = false);

        var result = await ApplyAsync();

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, $"Nothing changed: another container is already called {name}."), result);
        Assert.Empty(_engine.Calls);
        Assert.Empty(_journal.Rows);
    }

    [Fact]
    public async Task Recreates_a_stopped_container_without_starting_it()
    {
        var old = Postgres(running: false);
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);
        var result = await changer.ApplyAsync(plan);

        Assert.Equal(["postgres is stopped: it's recreated stopped, so nothing goes offline. It takes port 5432 when it starts."], plan.Impact);
        Assert.Equal("The original is kept until postgres's first start passes its checks.", plan.Footer);
        Assert.Null(plan.OfflineEstimate);
        Assert.Equal(["Host port 5432 is free", "Created the new container", "Turned off the old container's restart policy",
            "Swapped the names: the old one is postgres_old_0ebf31bbcd07"], plan.Steps);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.WaitsForStart,
            "postgres is recreated, still stopped: it takes port 5432 when it starts. The original stays, as postgres_old_0ebf31bbcd07, until that start passes its checks."), result);
        Assert.Equal(["create postgres_new_0ebf31bbcd07", "update postgres restart=no", "rename postgres postgres_old_0ebf31bbcd07", "rename postgres_new_0ebf31bbcd07 postgres"],
            _engine.Calls);
        var made = _engine.Get("postgres");
        Assert.NotEqual(old.Id, made.Id);
        Assert.Equal((false, (DateTimeOffset?)null), (made.Running, made.StartedAt));
        // The original is kept, and nothing starts it behind Gitberth's back; the new one gets the original's policy once
        // its first start has passed.
        Assert.Equal(("no", "no"), (Policy(OldName), Policy("postgres")));
        Assert.Equal(0, _host.Probes);
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.Running, ChangeStep.WaitingForStart), (row.Outcome, row.Step));
        Assert.Equal("postgres hasn't started since its last port change. Start it, or put the original back, before changing its ports again.",
            (await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5433)])).Blocked);
    }

    [Fact]
    public async Task The_first_start_of_a_recreated_container_finishes_the_change()
    {
        var old = Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        Assert.Null(await changer.ContainerStartedAsync("sample-index"));

        // The owner starts it.
        await _engine.StartAsync("postgres", default);
        var result = await changer.ContainerStartedAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.Done, "postgres started with its new ports and passed its checks. The original is removed (its volumes stay)."), result);
        var now = Assert.Single(_engine.Boxes);
        Assert.NotEqual(old.Id, now.Id);
        Assert.Equal(("postgres", true, "unless-stopped"), (now.Name, now.Running, Policy("postgres")));
        Assert.Contains("postgres-data", _engine.Volumes);
        Assert.Equal(ChangeOutcome.Done, Assert.Single(_journal.Rows).Outcome);
        // Later starts are its own business.
        Assert.Null(await changer.ContainerStartedAsync("postgres"));
    }

    [Fact]
    public async Task A_failed_first_start_keeps_the_original_for_a_decision()
    {
        var old = Postgres(running: false);
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(2);
        };
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.StartAsync("postgres", default);

        var result = await changer.ContainerStartedAsync("postgres");

        Assert.Equal(PortChangeOutcome.NeedsDecision, result!.Outcome);
        Assert.Equal("postgres's first start since its port change failed its checks: the new postgres stopped after 2 s (exit code 1).", result.Message);
        Assert.Equal("postgres's first start since its port change failed its checks: the new postgres stopped after 2 s (exit code 1). "
            + "The original is kept as postgres_old_0ebf31bbcd07. Both containers are kept: postgres (new, stopped) and postgres_old_0ebf31bbcd07 (the original).",
            Assert.Single(_journal.Rows).Notice);

        // [Put the original back]: it starts, since the owner had started postgres.
        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, "postgres is back as it was."), await changer.PutOriginalBackAsync("postgres"));
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true, "unless-stopped"), (back.Id, back.Name, back.Running, Policy("postgres")));
    }

    [Fact]
    public async Task Puts_the_original_back_before_the_first_start()
    {
        var old = Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        var back = await changer.PutOriginalBackAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, "postgres is back as it was."), back);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", false, "unless-stopped"), (now.Id, now.Name, now.Running, Policy("postgres")));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishing_postgres_also_gives_it_a_network_that_exists()
    {
        // As it is on the host today: stopped, and unable to start, since its network mode names gitdock.
        var old = _engine.Add(DockerFixtures.Inspect("postgres"));
        old.Listens = [5432];
        Assert.Contains("network gitdock not found", (await Assert.ThrowsAsync<EngineException>(() => _engine.StartAsync("postgres", default))).Message);
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);

        Assert.Contains("Its network gitdock no longer exists, so the new container uses gitberth instead.", plan.Impact);
        Assert.Equal(PortChangeOutcome.WaitsForStart, (await changer.ApplyAsync(plan)).Outcome);
        await _engine.StartAsync("postgres", default);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ContainerStartedAsync("postgres"))!.Outcome);
        Assert.Equal("gitberth", (string)_engine.Get("postgres").HostConfig["NetworkMode"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Never_removes_volumes(bool newOneFails)
    {
        // postgres-data mounted deeper, as before Postgres 18: the image's VOLUME is then an anonymous volume, which
        // holds the data.
        const string anonymous = "8e1b5c5a2d4f6b7c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e";
        var old = Postgres(change: i =>
        {
            i["HostConfig"]!["Binds"] = new JsonArray("postgres-data:/var/lib/postgresql/data");
            i["Mounts"] = new JsonArray(
                new JsonObject { ["Type"] = "volume", ["Name"] = "postgres-data", ["Destination"] = "/var/lib/postgresql/data", ["RW"] = true },
                new JsonObject { ["Type"] = "volume", ["Name"] = anonymous, ["Destination"] = "/var/lib/postgresql", ["RW"] = true });
        });
        if (newOneFails)
            _engine.OnCreate = b =>
            {
                b.Listens = [5432];
                b.ExitsAfter = TimeSpan.FromSeconds(4);
            };
        var volumes = _engine.Volumes.Order().ToList();
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);
        var result = await changer.ApplyAsync(plan);

        Assert.Contains("volumes (postgres-data and 1 anonymous)", plan.Keeps);
        Assert.Equal(newOneFails ? PortChangeOutcome.RolledBack : PortChangeOutcome.Done, result.Outcome);
        // No volume went, and none was made: the new container mounted the anonymous one by name.
        Assert.Equal(volumes, _engine.Volumes.Order().ToList());
        Assert.Contains(_engine.Get("postgres").Mounts, m => (string)m!["Name"]! == anonymous && (string)m["Destination"]! == "/var/lib/postgresql");
        // And there's no way to ask: the engine can't remove a volume (it can only look at one, for a plan's line: L6), and
        // its remove takes no v.
        Assert.DoesNotContain(typeof(IContainerEngine).GetMethods(), m => m.Name.Contains("Volume", StringComparison.OrdinalIgnoreCase) && m.Name != nameof(IContainerEngine.InspectVolumeAsync));
        Assert.Equal(["id", "force", "ct"], typeof(IContainerEngine).GetMethod(nameof(IContainerEngine.RemoveAsync))!.GetParameters().Select(p => p.Name));
    }

    [Theory]
    [InlineData("create", TempName, false, ChangeOutcome.NothingChanged)]
    [InlineData("create", TempName, true, ChangeOutcome.NothingChanged)]
    [InlineData("update", "postgres", true, ChangeOutcome.NothingChanged)]
    [InlineData("stop", "postgres", true, ChangeOutcome.RolledBack)]
    [InlineData("rename", OldName, true, ChangeOutcome.RolledBack)]
    [InlineData("rename", "postgres", true, ChangeOutcome.RolledBack)]
    [InlineData("start", "postgres", true, ChangeOutcome.Done)]
    [InlineData("remove", OldName, false, ChangeOutcome.Done)]
    public async Task Recovers_an_interrupted_change_at_each_step(string call, string target, bool afterItRan, ChangeOutcome outcome)
    {
        var old = Postgres();
        _engine.Hang = (call, target, afterItRan);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Gitberth starts again: new locks, the same journal and containers.
        _engine.Hang = null;
        await Changer(new ContainerLocks()).RecoverAsync();

        var row = Assert.Single(_journal.Rows);
        Assert.Equal(outcome, row.Outcome);
        Assert.Equal(outcome switch
        {
            ChangeOutcome.NothingChanged => "Gitberth restarted before it changed postgres's ports, so nothing changed.",
            ChangeOutcome.RolledBack => "Gitberth restarted while it recreated postgres. It put the original back.",
            _ => "Gitberth restarted while it recreated postgres. It finished the change: the new container runs and passed its checks.",
        }, row.Notice);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal(("postgres", true, "unless-stopped"), (now.Name, now.Running, Policy("postgres")));
        Assert.Equal(outcome == ChangeOutcome.Done, now.Id != old.Id);
        Assert.Equal(outcome == ChangeOutcome.Done, now.Live.ContainsKey("5432/tcp"));
        // The banner goes when it's dismissed.
        await changer.DismissAsync("postgres");
        Assert.Null(Assert.Single(_journal.Rows).Notice);
    }

    [Fact]
    public async Task Puts_the_original_back_when_the_new_one_failed_while_gitberth_was_away()
    {
        var old = Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(1);
        };
        _engine.Hang = ("start", "postgres", true);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _engine.Hang = null;

        await Changer(new ContainerLocks()).RecoverAsync();

        Assert.Equal(ChangeOutcome.RolledBack, Assert.Single(_journal.Rows).Outcome);
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", true), (now.Id, now.Name, now.Running));
    }

    [Fact]
    public async Task Waits_for_a_stopped_containers_first_start_across_a_restart()
    {
        Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        // Restarted before anyone started postgres: it keeps waiting.
        await Changer(new ContainerLocks()).RecoverAsync();
        Assert.Equal((ChangeOutcome.Running, ChangeStep.WaitingForStart), (_journal.Rows.Single().Outcome, _journal.Rows.Single().Step));
        Assert.Equal(2, _engine.Boxes.Count);

        // Started while Gitberth was away: the next start of Gitberth checks it and finishes.
        await _engine.StartAsync("postgres", default);
        await Changer(new ContainerLocks()).RecoverAsync();
        Assert.Equal(ChangeOutcome.Done, _journal.Rows.Single().Outcome);
        Assert.Equal(["postgres"], _engine.Boxes.Select(b => b.Name));
    }

    [Fact]
    public async Task Recovery_only_touches_containers_its_journal_names()
    {
        Postgres();
        // Names like Gitberth's and like Compose's own temporary ones, with no change on record.
        foreach (var name in new[] { "postgres_new_aaaaaaaaaaaa", "0ebf31bbcd07_postgres", OldName })
            Sample(name, i => i["State"]!["Running"] = false);

        await Changer().RecoverAsync();

        Assert.Equal(4, _engine.Boxes.Count);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_scheduled_restart_waits_for_the_lock()
    {
        Postgres();
        var changer = Changer();
        var plan = await changer.PlanAsync("postgres", Publish5432);
        var gate = new TaskCompletionSource();
        _engine.Gates["start postgres"] = gate;

        var running = changer.ApplyAsync(plan);
        await _engine.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A scheduled restart finds the lock taken and waits for its next tick; so does any other action.
        Assert.Null(await _locks.TryAcquireAsync("postgres"));
        Assert.True(_locks.IsHeld("postgres"));
        Assert.Equal("Gitberth is already changing postgres. Try again when that's done.", (await changer.PlanAsync("postgres", Publish5432)).Blocked);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "Gitberth is already changing postgres. Try again when that's done."),
            await changer.ApplyAsync(plan));

        gate.SetResult();
        Assert.Equal(PortChangeOutcome.Done, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        using var free = await _locks.TryAcquireAsync("postgres");
        Assert.NotNull(free);
    }

    [Fact]
    public async Task Measures_the_time_offline()
    {
        // The stop takes 1.4 s and the start 0.45 s; the new postgres answers 0.7 s after it starts, at the second try.
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ListenAfter = TimeSpan.FromSeconds(0.7);
        };

        var result = await ApplyAsync();

        Assert.Equal(TimeSpan.FromSeconds(2.1), result.Offline);
        Assert.Equal("postgres answers on 10.1.1.80:5432 (checked from the host). It was offline for 2.1 s.", result.Message);
        Assert.Equal(2100, Assert.Single(_journal.Rows).OfflineMs);
    }

    [Fact]
    public async Task A_port_that_never_answered_does_not_trigger_a_rollback()
    {
        var old = Postgres();
        old.Listens = [];
        _engine.OnCreate = b => b.Listens = [];
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);
        var result = await changer.ApplyAsync(plan);

        Assert.Contains("Nothing answers on postgres:5432 right now, so publishing it may not make it reachable.", plan.Impact);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.Done, "Published. Nothing answers on 5432 yet: it may listen only inside its container, or on another port.")
            { Offline = TimeSpan.FromSeconds(1.85), NothingAnswers = true }, result);
        Assert.NotEqual(old.Id, Assert.Single(_engine.Boxes).Id);
    }

    [Fact]
    public async Task A_port_that_answered_before_and_not_after_brings_the_old_one_back()
    {
        var old = Postgres();
        // The new one listens elsewhere.
        _engine.OnCreate = b => b.Listens = [];

        var result = await ApplyAsync();

        Assert.Equal($"The new postgres doesn't answer on 10.1.1.80:5432 (checked from the host), though it answered before the change. "
            + $"The old one is back as it was; offline {Seconds(result.Offline)} in all.", result.Message);
        Assert.Equal(old.Id, Assert.Single(_engine.Boxes).Id);
    }

    [Fact]
    public async Task A_slow_starter_gets_as_long_as_a_deploy_gives_it()
    {
        // No health check, and it answers 30 s after its start: well past the 15 s it has to stay up.
        Sample();
        _engine.OnCreate = b =>
        {
            b.Listens = [80];
            b.ListenAfter = TimeSpan.FromSeconds(30);
        };

        var result = await ApplyAsync([new PortRule(80, "tcp", PortAccess.Lan, 9201)], "sample-index");

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.InRange(result.Offline!.Value, TimeSpan.FromSeconds(31.4), TimeSpan.FromSeconds(32.2));
        Assert.Equal($"sample-index answers on 10.1.1.80:9201 (checked from the host). It was offline for {Seconds(result.Offline)}.", result.Message);
    }

    [Fact]
    public async Task A_plan_leaves_out_a_line_it_cannot_read()
    {
        Postgres();
        _host.UsersFail = true;

        var plan = await Changer().PlanAsync("postgres", Publish5432);

        Assert.True(plan.CanApply);
        Assert.Equal(["postgres restarts once: about 5 s offline."], plan.Impact);
    }

    [Fact]
    public async Task A_first_start_stopped_by_hand_while_gitberth_was_away_keeps_waiting()
    {
        Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.StartAsync("postgres", default);
        await _engine.StopAsync("postgres", 60, default);

        await Changer(new ContainerLocks()).RecoverAsync();

        Assert.Equal((ChangeOutcome.Running, ChangeStep.WaitingForStart), (_journal.Rows.Single().Outcome, _journal.Rows.Single().Step));
        await _engine.StartAsync("postgres", default);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ContainerStartedAsync("postgres"))!.Outcome);
    }

    [Fact]
    public async Task A_lan_check_that_cannot_run_does_not_fail_the_change()
    {
        Postgres();
        _host.LanCheckFails = "the check helper didn't start";

        var result = await ApplyAsync();

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal($"postgres is back with its new ports. It was offline for {Seconds(result.Offline)}.", result.Message);
    }

    [Fact]
    public async Task Checks_a_container_gitberth_cannot_reach_by_name_from_the_host()
    {
        // The samples sit on Docker's default bridge, which Gitberth isn't on: what they publish is tried from the host.
        var sample = Sample();
        _host.ByName = false;
        _engine.OnCreate = b => b.Listens = [];

        var result = await ApplyAsync([new PortRule(80, "tcp", PortAccess.Lan, 9201)], "sample-index");

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.StartsWith("The new sample-index doesn't answer on 10.1.1.80:9201 (checked from the host), though it answered before the change.", result.Message);
        Assert.Contains("10.1.1.80:9100", _host.LanChecks);
        Assert.Equal((sample.Id, true), (_engine.Get("sample-index").Id, _engine.Get("sample-index").Running));
    }

    [Fact]
    public async Task Refuses_to_apply_a_plan_for_a_container_recreated_since()
    {
        var old = Postgres();
        var changer = Changer();
        var plan = await changer.PlanAsync("postgres", Publish5432);
        _engine.Boxes.Remove(old);
        Postgres(change: i => i["Id"] = new string('b', 64));

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "postgres changed since you started editing."), await changer.ApplyAsync(plan));
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_taken_host_port_blocks_the_change()
    {
        Postgres();
        _host.Holders[5432] = "sample-pg (container)";
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);

        Assert.Equal("Port 5432 is taken by sample-pg (container).", plan.Blocked);
        Assert.False(plan.CanApply);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "Port 5432 is taken by sample-pg (container). Nothing changed."), await changer.ApplyAsync(plan));
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task The_probe_stops_the_change_before_anything_stops()
    {
        Postgres();
        _engine.HostPrograms.Add(5432);

        var result = await ApplyAsync();

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "Port 5432 is taken (address already in use). Nothing changed."), result);
        Assert.Empty(_engine.Calls);
        Assert.Empty(_journal.Rows);
    }

    [Fact]
    public async Task Unpublishing_says_what_stops_answering()
    {
        Postgres(change: i => i["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}]}"""));
        _host.Routes[5432] = ["status.example.com"];

        var plan = await Changer().PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Internal)]);

        Assert.Equal([new PortChangeLine("5432/tcp", "LAN at 10.1.1.80:5432", "Internal")], plan.Changes);
        Assert.Equal(
        [
            "postgres restarts once: about 5 s offline.",
            "10.1.1.80:5432 stops answering; apps keep postgres:5432.",
            "status.example.com reaches host port 5432 through the tunnel and stops working.",
        ], plan.Impact);
        Assert.Equal("unpublished 5432/tcp", plan.Summary);
        Assert.DoesNotContain(plan.Steps, s => s.StartsWith("Host port", StringComparison.Ordinal) || s.EndsWith("answers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moving_a_host_port_checks_only_the_new_one()
    {
        Postgres(change: i => i["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}]}"""));

        var plan = await Changer().PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5433)]);

        Assert.Equal([new PortChangeLine("5432/tcp", "LAN at 10.1.1.80:5432", "LAN at 10.1.1.80:5433")], plan.Changes);
        Assert.Equal("moved 5432/tcp to host port 5433", plan.Summary);
        Assert.Equal("Host port 5433 is free", plan.Steps[0]);
    }

    [Fact]
    public async Task Says_what_else_notices()
    {
        var postgres = Postgres();
        postgres.Changes = JsonNode.Parse("""
            [{"Path":"/etc","Kind":0},{"Path":"/etc/hosts.allow","Kind":1},{"Path":"/root","Kind":0},{"Path":"/root/.bashrc","Kind":0},
             {"Path":"/tmp","Kind":0},{"Path":"/tmp/x.sock","Kind":1},{"Path":"/var/run/postgresql/.s.PGSQL.5432.lock","Kind":1},
             {"Path":"/var/lib/postgresql","Kind":0}]
            """)!.AsArray();
        _host.Users = new ContainerUsers([], ["kanban"], [new AddressUser("sample-web", "172.21.0.4")]);

        var plan = await Changer().PlanAsync("postgres", Publish5432);

        Assert.Equal(
        [
            "postgres restarts once: about 5 s offline.",
            "2 files changed outside its volumes are lost: /etc/hosts.allow, /root/.bashrc.",
            "kanban is building and found postgres at its current address; that build may fail.",
            "sample-web names postgres's address 172.21.0.4 in its settings; that address can change.",
        ], plan.Impact);
    }

    [Fact]
    public async Task Names_three_lost_files_at_most()
    {
        Postgres().Changes = JsonNode.Parse("""[{"Path":"/a","Kind":1},{"Path":"/b","Kind":1},{"Path":"/c","Kind":2},{"Path":"/d","Kind":1},{"Path":"/e","Kind":0}]""")!.AsArray();

        var plan = await Changer().PlanAsync("postgres", Publish5432);

        Assert.Contains("5 files changed outside its volumes are lost: /a, /b, /c and 2 more.", plan.Impact);
    }

    [Fact]
    public async Task A_random_host_port_kept_becomes_fixed()
    {
        // Started with -P: Docker picked 32768 this time.
        Sample(change: i =>
        {
            i["HostConfig"]!["PortBindings"] = new JsonObject();
            i["HostConfig"]!["PublishAllPorts"] = true;
        });

        var plan = await Changer().PlanAsync("sample-index", [new PortRule(80, "tcp", PortAccess.Lan, 32768)]);

        Assert.Equal([new PortChangeLine("80/tcp", "LAN at 10.1.1.80:32768 (random)", "LAN at 10.1.1.80:32768")], plan.Changes);
        Assert.Equal(["sample-index restarts once: about 3 s offline.", "Its random host port 32768 becomes fixed at 32768."], plan.Impact);
    }

    [Fact]
    public async Task Gives_the_compose_change_for_what_it_wont_recreate()
    {
        _engine.Add(DockerFixtures.Inspect("gitberth"));

        var plan = await Changer().PlanAsync("gitberth", [new PortRule(8080, "tcp", PortAccess.Lan, 8081)]);

        Assert.Equal(RecreateKind.Self, plan.Verdict.Kind);
        Assert.Equal("Gitberth's own container. Its ports are set in /opt/gitberth/docker-compose.yml.", plan.Blocked);
        Assert.Equal([new PortChangeLine("8080/tcp", "LAN at 10.1.1.80:8080", "LAN at 10.1.1.80:8081")], plan.Changes);
        Assert.Equal("services:\n  gitberth:\n    ports:\n      - \"8081:8080\"", plan.ComposeSnippet);
        Assert.False(plan.CanApply);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "Gitberth's own container. Its ports are set in /opt/gitberth/docker-compose.yml."),
            await Changer().ApplyAsync(plan));
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task Turns_down_ports_that_cannot_be()
    {
        Postgres();
        var changer = Changer();

        Assert.Equal("Pick a host port between 1 and 65535.", (await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan)])).Blocked);
        Assert.Equal("Each port needs a container port between 1 and 65535.", (await changer.PlanAsync("postgres", [new PortRule(0, "tcp", PortAccess.Internal)])).Blocked);
        Assert.Equal("Host port 5432/tcp is listed twice.",
            (await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5432), new PortRule(8080, "tcp", PortAccess.HostOnly, 5432)])).Blocked);
        // Nothing to change: no Apply, and nothing to say.
        var same = await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Internal)]);
        Assert.Equal((false, null, 0), (same.CanApply, same.Blocked, same.Changes.Count));
    }

    [Theory]
    [InlineData("driver failed programming external connectivity on endpoint postgres (0ebf31bbcd07): Bind for 0.0.0.0:5432 failed: port is already allocated",
        "port is already allocated", 5432)]
    [InlineData("failed to set up container networking: driver failed programming external connectivity on endpoint postgres (0ebf31bbcd07): "
        + "failed to bind host port for 0.0.0.0:5432:172.21.0.4:5432/tcp: address already in use", "address already in use", 5432)]
    [InlineData("Error starting userland proxy: listen tcp4 0.0.0.0:15672: bind: address already in use", "address already in use", 15672)]
    [InlineData("failed to set up container networking: Address already in use", null, null)]
    public void Translates_dockers_port_errors(string message, string? taken, int? port)
    {
        // The last is a static address another container holds, not a port.
        Assert.Equal(taken, PortChanger.PortTaken(message));
        if (taken is not null) Assert.Equal(port, PortChanger.PortIn(message, [5672, 5432, 15672]));
    }

    [Fact]
    public void Reads_the_last_output_whether_or_not_it_has_a_tty()
    {
        // Without a TTY Docker frames stdout (1) and stderr (2); with one, the bytes come as written.
        byte[] stderr = [2, 0, 0, 0, 0, 0, 0, 18, .. "FATAL: bad config\n"u8.ToArray()];
        byte[] framed = [.. FakeEngine.Framed("listening on 5432\n"), .. stderr];

        Assert.Equal("listening on 5432\nFATAL: bad config\n", LogStream.Text(framed, tty: false));
        Assert.Equal("\u0001 raw\n", LogStream.Text("\u0001 raw\n"u8, tty: true));
        // A stream that isn't framed after all reads as it is.
        Assert.Equal("ok\n", LogStream.Text("ok\n"u8, tty: false));
    }

    [Fact]
    public async Task Runs_in_the_background_and_says_how_far_it_got()
    {
        Postgres();
        var changer = Changer();
        var plan = await changer.PlanAsync("postgres", Publish5432);
        var changed = 0;

        var job = changer.Start(plan);
        job.Changed += () => Interlocked.Increment(ref changed);
        var result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Same(job, changer.JobFor("postgres"));
        Assert.Equal(9, job.Steps.Count);
        Assert.All(job.Steps, s => Assert.Equal(StepState.Done, s.State));
        Assert.Null(job.Current);
    }

    [Fact]
    public async Task A_read_of_docker_that_fails_once_does_not_fail_a_working_change()
    {
        // The new postgres is healthy, and the read that checks its bindings fails once (Docker is slow): it's read again.
        Postgres();
        FakeEngine.Box? made = null;
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            made = b;
        };
        var healthyReads = 0;
        _engine.Fail = (call, target) => call == "inspect" && made is { } m && target == m.Id && _clock.GetUtcNow() >= m.StartedAt + m.HealthyAfter
            && Interlocked.Increment(ref healthyReads) == 2 ? new EngineException(0, "context deadline exceeded") : null;

        var result = await ApplyAsync();

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.True(healthyReads > 2);
    }

    [Fact]
    public async Task Never_starts_the_original_while_docker_cannot_say_the_new_one_is_down()
    {
        // The new postgres runs on postgres-data but doesn't answer on the LAN, though the old one did: the change is rolled
        // back. From then on Docker doesn't answer about the new container, so nothing says it's down.
        var old = Postgres();
        string? made = null;
        _engine.OnCreate = b =>
        {
            b.Listens = [];
            made = b.Id;
        };
        var docker = true;
        _host.Connecting = (_, _) => docker = false;
        _engine.Fail = (call, target) => call == "inspect" && target == made && !docker ? new EngineException(0, "context deadline exceeded") : null;
        var changer = Changer();

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NeedsDecision,
            "The new postgres failed, and the old one (postgres_old_0ebf31bbcd07) wasn't put back, because Docker didn't answer (context deadline exceeded). Nothing was deleted."),
            result with { LastOutput = null });
        // A second Postgres on the same data was never started.
        Assert.DoesNotContain($"start {OldName}", _engine.Calls);
        Assert.Equal("A port change on postgres didn't finish. Both containers are kept.", Assert.Single(_journal.Rows).Notice);
        _engine.Fail = null;
        Assert.Equal((true, false), (await RunsAsync("postgres"), await RunsAsync(OldName)));

        // Docker answers again, and the owner chooses.
        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, "postgres is back as it was."), await changer.PutOriginalBackAsync("postgres"));
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", "unless-stopped"), (back.Id, back.Name, Policy("postgres")));
        Assert.True(await RunsAsync("postgres"));
    }

    [Fact]
    public async Task Keeps_the_new_one_only_once_docker_says_the_original_is_down()
    {
        // The rollback couldn't give the original its name back, so the owner decides; meanwhile someone started the
        // original (as postgres_old_…).
        var old = Postgres();
        _engine.OnCreate = b => b.Listens = [];
        var rollingBack = false;
        _host.Connecting = (_, _) => rollingBack = true;
        _engine.Fail = (call, target) => call == "rename" && target == "postgres" && rollingBack ? new EngineException(500, "boom") : null;
        var changer = Changer();
        Assert.Equal(PortChangeOutcome.NeedsDecision, (await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432))).Outcome);
        _engine.Fail = null;
        await _engine.StartAsync(OldName, default);
        Assert.Equal((true, false), (await RunsAsync(OldName), await RunsAsync(TempName)));

        // [Keep the new one] while Docker doesn't answer about the original, which runs on postgres-data.
        _engine.Fail = (call, target) => call == "inspect" && target == old.Id ? new EngineException(0, "connection reset by peer") : null;
        var calls = _engine.Calls.Count;
        var kept = await changer.KeepNewAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NeedsDecision, "Couldn't keep the new postgres: Docker didn't answer (connection reset by peer)."), kept);
        Assert.Empty(_engine.Calls.Skip(calls));
        _engine.Fail = null;
        Assert.Equal((true, false), (await RunsAsync(OldName), await RunsAsync(TempName)));

        // Once it answers, the original stops before the new one starts.
        Assert.Equal(PortChangeOutcome.Done, (await changer.KeepNewAsync("postgres")).Outcome);
        Assert.True(_engine.Calls.IndexOf($"stop {OldName} t=60") < _engine.Calls.LastIndexOf("start postgres"));
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal(("postgres", "unless-stopped"), (now.Name, Policy("postgres")));
        Assert.NotEqual(old.Id, now.Id);
        Assert.True(await RunsAsync("postgres"));
    }

    [Fact]
    public async Task Recovery_never_takes_a_container_docker_did_not_answer_for_as_gone()
    {
        // Gitberth restarted just after the new postgres started; at startup Docker doesn't answer about it.
        Postgres();
        string? made = null;
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            made = b.Id;
        };
        _engine.Hang = ("start", "postgres", true);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _engine.Hang = null;
        _engine.Fail = (call, target) => call == "inspect" && target == made ? new EngineException(0, "connection reset by peer") : null;

        await Changer(new ContainerLocks()).RecoverAsync();

        // Not put back on a guess: the new one may run, so the original stays down for the owner to choose.
        Assert.Equal(ChangeOutcome.NeedsDecision, Assert.Single(_journal.Rows).Outcome);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("start " + OldName, StringComparison.Ordinal) || c == "rename " + OldName + " postgres");
        _engine.Fail = null;
        Assert.Equal((true, false), (await RunsAsync("postgres"), await RunsAsync(OldName)));
    }

    [Theory]
    [InlineData("update", "postgres", true, 1)]
    [InlineData("stop", "postgres", false, 1)]
    [InlineData("stop", "postgres", true, 1)]
    // The original's start, after the new one's: it has its name back by then.
    [InlineData("start", "postgres", true, 1)]
    public async Task Recovers_an_interrupted_rollback(string call, string target, bool afterItRan, int skips)
    {
        // The new postgres runs, but doesn't answer on the LAN though the old one did, so it's rolled back; Gitberth stops
        // during the rollback. However well the new one runs at startup, the rollback goes on: the change failed. (The
        // same update and stop came earlier, for the old container: those go through.)
        var old = Postgres();
        _engine.OnCreate = b => b.Listens = [];
        _engine.HangSkips = skips;
        _engine.Hang = (call, target, afterItRan);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ChangeStep.RollingBack, Assert.Single(_journal.Rows).Step);

        _engine.Hang = null;
        await Changer(new ContainerLocks()).RecoverAsync();

        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.RolledBack, "Gitberth restarted while it recreated postgres. It put the original back."), (row.Outcome, row.Notice));
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", "unless-stopped"), (now.Id, now.Name, Policy("postgres")));
        Assert.True(await RunsAsync("postgres"));
    }

    [Fact]
    public async Task Recovery_lets_a_stop_still_under_way_finish_before_it_starts_the_original()
    {
        // Gitberth went while postgres was stopping. Docker carries a stop on after its caller has gone: postgres is down 20 s
        // later. A start before then only says it runs (304), and the stop would take it down after all.
        var old = Postgres();
        _engine.StopLingers = TimeSpan.FromSeconds(20);
        _engine.Hang = ("stop", "postgres", true);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _engine.Hang = null;
        _engine.StopLingers = null;
        Assert.True(await RunsAsync("postgres"));

        await Changer(new ContainerLocks()).RecoverAsync();

        Assert.Equal(ChangeOutcome.RolledBack, Assert.Single(_journal.Rows).Outcome);
        // Long after the stop's time, postgres runs: it was started again once the stop had finished.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", "unless-stopped"), (now.Id, now.Name, Policy("postgres")));
        Assert.True(await RunsAsync("postgres"));
    }

    [Fact]
    public async Task A_first_start_while_the_original_runs_waits_for_a_decision()
    {
        // The owner started the kept original, then the new postgres: both on postgres-data.
        var old = Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.StartAsync(OldName, default);
        Assert.Null(await changer.ContainerStartedAsync(OldName));
        await _engine.StartAsync("postgres", default);

        var result = await changer.ContainerStartedAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NeedsDecision,
            "postgres and its original, postgres_old_0ebf31bbcd07, both run, on the same volumes. Keep the new one or put the original back: the other one stops."), result);
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.NeedsDecision,
            "postgres and its original both run, on the same volumes. Both containers are kept: postgres (new, running) and postgres_old_0ebf31bbcd07 (the original)."),
            (row.Outcome, row.Notice));
        // Nothing was removed while both ran.
        Assert.Equal(2, _engine.Boxes.Count);

        // [Put the original back] stops the new one first.
        Assert.Equal(new PortChangeResult(PortChangeOutcome.RolledBack, "postgres is back as it was."), await changer.PutOriginalBackAsync("postgres"));
        var back = Assert.Single(_engine.Boxes);
        Assert.Equal((old.Id, "postgres", "unless-stopped"), (back.Id, back.Name, Policy("postgres")));
        Assert.True(await RunsAsync("postgres"));
    }

    [Fact]
    public async Task Recovery_leaves_both_containers_running_on_one_volume_to_the_owner()
    {
        // While Gitberth was away, someone started both the kept original and the new postgres.
        Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await _engine.StartAsync(OldName, default);
        await _engine.StartAsync("postgres", default);

        await Changer(new ContainerLocks()).RecoverAsync();

        var row = Assert.Single(_journal.Rows);
        Assert.Equal(ChangeOutcome.NeedsDecision, row.Outcome);
        Assert.StartsWith("postgres and its original both run, on the same volumes.", row.Notice);
        Assert.Equal(2, _engine.Boxes.Count);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Neither_container_of_an_unsettled_change_starts_next_to_the_other()
    {
        Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        // The kept original comes back only through [Put the original back]; the new one starts while the original doesn't run.
        Assert.Equal("postgres_old_0ebf31bbcd07 is postgres's original, kept until its port change is settled: put the original back to use it.",
            await changer.WhyNotStartAsync(OldName));
        Assert.Null(await changer.WhyNotStartAsync("postgres"));
        Assert.Null(await changer.WhyNotStartAsync("sample-index"));
        await _engine.StartAsync(OldName, default);
        Assert.Equal("postgres's original, postgres_old_0ebf31bbcd07, runs on the same volumes: keep the new one or put the original back first.",
            await changer.WhyNotStartAsync("postgres"));
    }

    [Fact]
    public async Task A_scheduled_check_finds_a_first_start_the_event_watcher_missed()
    {
        // Until its first start passes, the new postgres has no restart policy: a start nobody checked is found by the timer.
        Postgres(running: false);
        var changer = Changer();
        await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        await changer.CheckWaitingAsync();
        Assert.Equal(ChangeStep.WaitingForStart, Assert.Single(_journal.Rows).Step);

        await _engine.StartAsync("postgres", default);
        await changer.CheckWaitingAsync();

        Assert.Equal(ChangeOutcome.Done, Assert.Single(_journal.Rows).Outcome);
        Assert.Equal(("postgres", "unless-stopped"), (Assert.Single(_engine.Boxes).Name, Policy("postgres")));
    }

    [Fact]
    public async Task Ends_a_change_whose_containers_are_gone()
    {
        // A failed rollback kept both; then both were removed outside Gitberth, and a postgres was made again by hand.
        Postgres();
        var changer = await FailedRollbackAsync();
        _engine.Boxes.Clear();

        var result = await changer.PutOriginalBackAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged,
            "Neither container of the port change on postgres is left, so there's nothing to keep or put back."), result);
        Assert.Equal(ChangeOutcome.NothingChanged, Assert.Single(_journal.Rows).Outcome);
        // It no longer holds up a change of the postgres that's there now.
        Postgres(change: i => i["Id"] = new string('c', 64));
        Assert.Null((await changer.PlanAsync("postgres", Publish5432)).Blocked);
    }

    [Fact]
    public async Task Ends_a_change_whose_name_another_container_took()
    {
        // The owner removed the original postgres (back under its name, stopped) and made one of their own; the kept new
        // container is still there.
        Postgres();
        var changer = await FailedRollbackAsync();
        _engine.Boxes.Remove(_engine.Get("postgres"));
        var theirs = Postgres(change: i => i["Id"] = new string('c', 64));

        var result = await changer.KeepNewAsync("postgres");

        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged,
            "postgres is another container now, so its port change is over. postgres_new_0ebf31bbcd07 is left from it; remove it when you like."), result);
        Assert.Equal(ChangeOutcome.NothingChanged, Assert.Single(_journal.Rows).Outcome);
        Assert.Contains(theirs, _engine.Boxes);
        Assert.Equal(2, _engine.Boxes.Count);
    }

    [Fact]
    public async Task A_moved_tag_keeps_postgres_a_database()
    {
        // A pull moved postgres:18 on since the container was made. The new one runs the same image, pinned by its digest,
        // so it keeps its repository's name: still a database, with Connect and 60 s to stop cleanly.
        _engine.Images["postgres:18"] = new JsonObject { ["Id"] = "sha256:" + new string('9', 64) };
        _engine.Images["the image it runs"] = DockerFixtures.Json("image-postgres.json").AsObject();
        Postgres();
        var changer = Changer();

        var plan = await changer.PlanAsync("postgres", Publish5432);
        Assert.Contains("image 3a4264520e12, the one it runs (postgres:18 has moved on; not pulled again)", plan.Keeps);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(plan)).Outcome);

        var now = _engine.Get("postgres");
        Assert.Equal(("postgres@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81"),
            ((string)now.Config["Image"]!, now.Image));
        var next = await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5433)]);
        Assert.Contains("Stopped the old container (SIGINT, waits up to 60 s)", next.Steps);
        Assert.Equal("postgres restarts once: about 5 s offline.", next.Impact[0]);
    }

    [Fact]
    public async Task Switching_a_port_to_host_only_is_a_change()
    {
        // The draft starts from the rules read from Docker, each carrying Docker's address (0.0.0.0 here).
        var sample = Sample();
        var rules = ContainerPorts.Rules((await _engine.InspectContainerAsync(sample.Id, default))!).Select(r => r with { Access = PortAccess.HostOnly }).ToList();

        var plan = await Changer().PlanAsync("sample-index", rules);

        Assert.Equal([new PortChangeLine("80/tcp", "LAN at 10.1.1.80:9100", "Host only at 127.0.0.1:9100")], plan.Changes);
        Assert.Equal("made 80/tcp host only", plan.Summary);
        Assert.Equal([new NewBinding(80, "tcp", "127.0.0.1", 9100)], plan.Bindings);
    }

    [Fact]
    public async Task Two_bindings_of_one_host_port_on_separate_addresses_are_fine()
    {
        // -p 127.0.0.1:8080:80 -p [::1]:8080:80: Docker allows both, so a change to another port isn't held up by them.
        var sample = Sample(change: i => i["HostConfig"]!["PortBindings"] = JsonNode.Parse(
            """{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"8080"},{"HostIp":"::1","HostPort":"8080"}]}"""));
        _engine.OnCreate = b => b.Listens = [80];
        var changer = Changer();
        var rules = ContainerPorts.Rules((await _engine.InspectContainerAsync(sample.Id, default))!).Append(new PortRule(9000, "tcp", PortAccess.Lan, 9000)).ToList();

        var plan = await changer.PlanAsync("sample-index", rules);

        Assert.Equal((null, true), (plan.Blocked, plan.CanApply));
        Assert.Equal([new PortChangeLine("9000/tcp", "Internal", "LAN at 10.1.1.80:9000")], plan.Changes);
        // 0.0.0.0 and :: are two bindings too; every address and one of its addresses clash.
        Assert.Null((await changer.PlanAsync("sample-index", [new(80, "tcp", PortAccess.Lan, 8080, "0.0.0.0"), new(80, "tcp", PortAccess.Lan, 8080, "::")])).Blocked);
        Assert.Equal("Host port 8080/tcp is listed twice.",
            (await changer.PlanAsync("sample-index", [new(80, "tcp", PortAccess.Lan, 8080, ""), new(80, "tcp", PortAccess.HostOnly, 8080, "::1")])).Blocked);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(plan)).Outcome);
    }

    [Fact]
    public async Task Widening_a_host_only_port_to_the_lan_checks_who_holds_it_elsewhere()
    {
        // Published on 127.0.0.1 only, then [Publish on LAN] on the same port: every other address of 5432 is new to it.
        Postgres(change: i => i["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":"5432"}]}"""));
        var changer = Changer();
        _host.Holders[5432] = "sample-pg (container)";

        Assert.Equal("Port 5432 is taken by sample-pg (container).", (await changer.PlanAsync("postgres", Publish5432)).Blocked);
        Assert.Equal(["5432/tcp on every address"], _host.HolderChecks);

        // Free: checked, but not probed (its own socket on 127.0.0.1 would be in the probe's way).
        _host.Holders.Clear();
        var plan = await changer.PlanAsync("postgres", Publish5432);
        Assert.Equal("Host port 5432 is free", plan.Steps[0]);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(plan)).Outcome);
        Assert.Equal(0, _host.Probes);

        // Narrowing it back takes nothing new, so there's nothing to check.
        _host.HolderChecks.Clear();
        var narrow = await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.HostOnly, 5432)]);
        Assert.True(narrow.CanApply);
        Assert.Empty(_host.HolderChecks);
        Assert.DoesNotContain(narrow.Steps, s => s.StartsWith("Host port", StringComparison.Ordinal));
    }

    // One of the samples, stopped on purpose (it exited cleanly), recreated stopped with 80 published on 9150: its change
    // waits for the first start. Returns the name its original is kept under.
    private async Task<(PortChanger Changer, string OldName)> WaitingSampleAsync(string name)
    {
        var sample = Sample(name, i =>
        {
            i["State"]!["Running"] = false;
            i["State"]!["Status"] = "exited";
            i["State"]!["ExitCode"] = 0;
            i["State"]!["FinishedAt"] = "2026-11-05T03:27:00Z";
        });
        _engine.OnCreate = b => b.Listens = [80];
        var changer = Changer();
        var plan = await changer.PlanAsync(name, [new PortRule(80, "tcp", PortAccess.Lan, 9150)]);
        Assert.True(plan.CanApply, plan.Blocked);
        Assert.Equal(PortChangeOutcome.WaitsForStart, (await changer.ApplyAsync(plan)).Outcome);
        return (changer, PortChanger.OldNameFor(name, sample.Id));
    }

    [Fact]
    public async Task The_timer_checks_a_first_start_it_finds_as_its_start_event_would()
    {
        // The new sample dies 5 s after each start, a fault that only shows once it runs. It was started while the event
        // stream was down, so the 5-minute timer is what finds the start, half a second in.
        var (changer, oldName) = await WaitingSampleAsync("sample-x");
        _engine.Get("sample-x").ExitsAfter = TimeSpan.FromSeconds(5);
        await _engine.StartAsync("sample-x", default);

        await changer.CheckWaitingAsync();

        // It gets the rest of its 15 s, counted from its own start: it dies at 5 s, and the original is kept for the owner.
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.NeedsDecision, "sample-x's first start since its port change failed its checks: the new sample-x stopped after 5 s (exit code 1)."),
            (row.Outcome, row.Error));
        Assert.Contains(_engine.Boxes, b => b.Name == oldName);
        Assert.Equal("no", Policy("sample-x"));
    }

    [Fact]
    public async Task Recovery_checks_a_first_start_made_while_gitberth_was_away_as_its_start_event_would()
    {
        var (_, oldName) = await WaitingSampleAsync("sample-x");
        _engine.Get("sample-x").ExitsAfter = TimeSpan.FromSeconds(5);
        // Started just before Gitberth came back.
        await _engine.StartAsync("sample-x", default);

        await Changer(new ContainerLocks()).RecoverAsync();

        Assert.Equal(ChangeOutcome.NeedsDecision, Assert.Single(_journal.Rows).Outcome);
        Assert.Contains(_engine.Boxes, b => b.Name == oldName);
    }

    [Fact]
    public async Task A_late_start_event_of_a_first_start_stopped_by_hand_since_keeps_waiting()
    {
        // Started and stopped again cleanly by hand while Gitberth was being redeployed; Docker replays the start once it's back.
        var (changer, _) = await WaitingSampleAsync("sample-y");
        await _engine.StartAsync("sample-y", default);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await _engine.StopAsync("sample-y", 10, default);

        // As for the timer and recovery, that start decides nothing: the next one is the one checked.
        Assert.Null(await changer.ContainerStartedAsync("sample-y"));
        await changer.CheckWaitingAsync();
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.Running, ChangeStep.WaitingForStart, (string?)null), (row.Outcome, row.Step, row.Notice));

        await _engine.StartAsync("sample-y", default);
        Assert.Equal(PortChangeOutcome.Done, (await changer.ContainerStartedAsync("sample-y"))!.Outcome);
    }

    [Fact]
    public async Task Recovery_finishes_a_change_only_once_the_new_container_has_stayed_up_15_s_from_its_start()
    {
        // Gitberth went just after the new sample started, and is back before the sample's 15 s are up. The sample dies 5 s
        // after its start.
        var sample = Sample();
        _engine.OnCreate = b =>
        {
            b.Listens = [80];
            b.ExitsAfter = TimeSpan.FromSeconds(5);
        };
        _engine.Hang = ("start", "sample-index", true);
        var changer = Changer();
        _ = changer.ApplyAsync(await changer.PlanAsync("sample-index", [new PortRule(80, "tcp", PortAccess.Lan, 9201)]));
        await _engine.Hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _engine.Hang = null;

        await Changer(new ContainerLocks()).RecoverAsync();

        // Its quick check counts from its own start, as the change's own checks would have: the original comes back.
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.RolledBack, "Gitberth restarted while it recreated sample-index. It put the original back."), (row.Outcome, row.Notice));
        var now = Assert.Single(_engine.Boxes);
        Assert.Equal((sample.Id, "sample-index", true), (now.Id, now.Name, now.Running));
    }

    /// <summary>The journal, paused after a read of a container's latest change: the moment between that read and a write.</summary>
    private sealed class PausingJournal(MemoryJournal inner) : IChangeJournal
    {
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ContainerChange> AddAsync(ContainerChange change, CancellationToken ct) => inner.AddAsync(change, ct);

        public Task UpdateAsync(ContainerChange change, CancellationToken ct) => inner.UpdateAsync(change, ct);

        public Task<IReadOnlyList<ContainerChange>> UnfinishedAsync(CancellationToken ct) => inner.UnfinishedAsync(ct);

        public Task DismissNoticeAsync(int id, CancellationToken ct) => inner.DismissNoticeAsync(id, ct);

        public Task<ContainerChange?> WithContainerAsync(string containerId, CancellationToken ct) => inner.WithContainerAsync(containerId, ct);

        public async Task<ContainerChange?> LatestAsync(string container, CancellationToken ct)
        {
            var row = await inner.LatestAsync(container, ct);
            if (Hold is { } hold)
            {
                Read.TrySetResult();
                await hold.Task;
            }
            return row;
        }
    }

    [Fact]
    public async Task Dismissing_the_banner_while_keep_the_new_one_runs_takes_down_the_banner_alone()
    {
        // A failed rollback: the new postgres exited and the original wouldn't start, so both are kept for the owner.
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = OriginalWontStart(_engine.Get("postgres").Id);
        var journal = new PausingJournal(_journal);
        var changer = new PortChanger(_engine, journal, _locks, _host, _host, _host, _host, _host,
            new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<PortChanger>.Instance, _clock);
        Assert.Equal(PortChangeOutcome.NeedsDecision, (await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432))).Outcome);
        _engine.Fail = null;
        _engine.Get(TempName).ExitsAfter = null;

        // The owner closes the banner (its read is held right there) while [Keep the new one] finishes.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.Hold = hold;
        var dismiss = changer.DismissAsync("postgres");
        await journal.Read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        journal.Hold = null;
        Assert.Equal(PortChangeOutcome.Done, (await changer.KeepNewAsync("postgres")).Outcome);
        hold.SetResult();
        await dismiss.WaitAsync(TimeSpan.FromSeconds(10));

        // Only the banner went: the change stays done, and the next change of postgres isn't held up by it.
        var row = Assert.Single(_journal.Rows);
        Assert.Equal((ChangeOutcome.Done, (string?)null), (row.Outcome, row.Notice));
        Assert.True((await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5433)])).CanApply);
    }

    [Fact]
    public async Task A_copy_a_finished_change_left_behind_never_starts_next_to_the_one_kept()
    {
        // The change worked, but Docker didn't remove the original: it stays, stopped, with postgres-data mounted.
        Postgres();
        _engine.Fail = (call, target) => call == "remove" && target == OldName ? new EngineException(500, "removal of container is already in progress") : null;
        var changer = Changer();
        var done = await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));
        Assert.Equal(PortChangeOutcome.Done, done.Outcome);
        Assert.Contains($"The old container, {OldName}, couldn't be removed", done.Message);
        _engine.Fail = null;

        Assert.Equal($"{OldName} is the old copy of postgres from its port change and mounts the same volumes; remove it instead.", await changer.WhyNotStartAsync(OldName));
        // The postgres it was replaced by starts as ever, and so does anything else.
        Assert.Null(await changer.WhyNotStartAsync("postgres"));
        Sample();
        Assert.Null(await changer.WhyNotStartAsync("sample-index"));
    }

    [Fact]
    public async Task A_new_container_a_rollback_left_behind_never_starts_either()
    {
        // The new postgres exited, and the original came back; Docker didn't remove the new one.
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = (call, target) => call == "remove" && target == TempName ? new EngineException(500, "removal of container is already in progress") : null;

        var result = await ApplyAsync();

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Contains($"The new container, {TempName}, couldn't be removed", result.Message);
        _engine.Fail = null;
        Assert.Equal($"{TempName} is the unused copy of postgres from its port change and mounts the same volumes; remove it instead.",
            await Changer().WhyNotStartAsync(TempName));
        Assert.Null(await Changer().WhyNotStartAsync("postgres"));
    }

    [Fact]
    public async Task A_copy_a_finished_change_left_behind_is_never_changed_either()
    {
        // As it never starts: a port change, a settings change or a live one would give a copy that mounts postgres-data
        // a life of its own (a restart policy that starts it at boot, ports that take postgres's).
        Postgres();
        _engine.Fail = (call, target) => call == "remove" && target == OldName ? new EngineException(500, "removal of container is already in progress") : null;
        var changer = Changer();
        Assert.Equal(PortChangeOutcome.Done, (await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432))).Outcome);
        _engine.Fail = null;
        var calls = _engine.Calls.Count;
        var why = $"{OldName} is the old copy of postgres from its port change and mounts the same volumes; remove it instead.";

        var ports = await changer.PlanAsync(OldName, [new PortRule(5432, "tcp", PortAccess.Lan, 5433)]);
        Assert.Equal((RecreateKind.Busy, why, false), (ports.Verdict.Kind, ports.Blocked, ports.CanApply));
        Assert.Equal(why, (await changer.ApplyAsync(ports)).Message);
        var parked = (await _engine.InspectContainerAsync(OldName, default))!;
        var loaded = ContainerSettingsJson.Read(parked, null);
        foreach (var wanted in new[]
                 {
                     loaded with { Env = [.. loaded.Env, new Gitberth.Apps.EnvSetting("TZ", "UTC")] },
                     loaded with { Restart = new Gitberth.Apps.RestartSetting("always") },
                 })
        {
            var plan = await changer.PlanAsync(OldName, new Gitberth.Apps.ContainerEditRequest { ContainerId = (string)parked["Id"]!, Loaded = loaded, Wanted = wanted });
            Assert.Equal((why, false), (plan.Blocked, plan.CanApply));
        }
        Assert.Equal(why, await changer.WhyNotChangeAsync(OldName));
        Assert.Equal(calls, _engine.Calls.Count);
        // The postgres it was replaced by changes as ever.
        Assert.Null(await changer.WhyNotChangeAsync("postgres"));
        Assert.True((await changer.PlanAsync("postgres", [new PortRule(5432, "tcp", PortAccess.Lan, 5433)])).CanApply);
    }

    [Fact]
    public async Task A_new_container_a_rollback_left_behind_is_never_changed_either()
    {
        Postgres();
        _engine.OnCreate = b =>
        {
            b.Listens = [5432];
            b.ExitsAfter = TimeSpan.FromSeconds(4);
        };
        _engine.Fail = (call, target) => call == "remove" && target == TempName ? new EngineException(500, "removal of container is already in progress") : null;
        Assert.Equal(PortChangeOutcome.RolledBack, (await ApplyAsync()).Outcome);
        _engine.Fail = null;

        var plan = await Changer().PlanAsync(TempName, [new PortRule(5432, "tcp", PortAccess.Lan, 5433)]);

        Assert.Equal($"{TempName} is the unused copy of postgres from its port change and mounts the same volumes; remove it instead.", plan.Blocked);
        Assert.Null(await Changer().WhyNotChangeAsync("postgres"));
    }

    [Fact]
    public async Task Gitberth_knows_its_own_container_by_its_id_whatever_its_hostname()
    {
        // Started with docker run --hostname and without Compose: its hostname is neither its container's name nor the
        // start of its ID, and no label says what it is. Its ID, read from its own mounts, does.
        var self = DockerFixtures.Inspect("gitberth");
        self["Config"]!["Labels"] = new JsonObject();
        _engine.Add(self);
        var options = new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = true, OwnContainer = (string)self["Id"]! };
        var changer = new PortChanger(_engine, _journal, _locks, _host, _host, _host, _host, _host, options, NullLogger<PortChanger>.Instance, _clock);

        var plan = await changer.PlanAsync("gitberth", [new PortRule(8080, "tcp", PortAccess.Lan, 8081)]);

        Assert.Equal(new RecreateVerdict(RecreateKind.Self, "Gitberth's own container: it can't recreate itself."), plan.Verdict);
        Assert.Equal(new PortChangeResult(PortChangeOutcome.NothingChanged, "Gitberth's own container: it can't recreate itself."), await changer.ApplyAsync(plan));
        Assert.Empty(_engine.Calls);
    }

    // postgres running on gitberth, answering by name; Gitberth's own container, whose image the helpers run; and the check
    // helper exiting as `timeout 3 bash -c 'exec 3<>/dev/tcp/$0/$1'` does: 0 when something answers at that address on
    // the host, else 1.
    private PortChanger WithCheckHelper(string lanAddress, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _engine.Images["gitberth:local"] = new JsonObject
        {
            ["Id"] = "sha256:0c1a38670237d7bc11ee185585ec1309b54599d259ccd7930e81c2114472b125",
            ["Config"] = new JsonObject { ["Volumes"] = new JsonObject { ["/data"] = new JsonObject() } },
        };
        var self = DockerFixtures.Inspect("gitberth");
        self["Name"] = "/" + Environment.MachineName;
        _engine.Add(self);
        Postgres();
        _engine.OnCreate = b =>
        {
            if (b.Config["Labels"]?[HostScanHelper.Label]?.GetValue<string>() == HostScanHelper.CheckKind)
            {
                var address = b.Config["Cmd"]![4]!.GetValue<string>();
                var port = int.Parse(b.Config["Cmd"]![5]!.GetValue<string>(), CultureInfo.InvariantCulture);
                b.ExitsAfter = TimeSpan.FromMilliseconds(100);
                b.ExitWith = IPAddress.TryParse(address, out _) && _engine.AnswersOnHost(address, port) ? 0 : 1;
                return;
            }
            b.Listens = [5432];
        };
        _host.Lan = lanAddress;
        var helpers = new HostHelpers(_engine, new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<HostHelpers>.Instance) { InContainer = true, Resolve = resolve };
        return new PortChanger(_engine, _journal, _locks, _host, _host, helpers, _host, _host,
            new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<PortChanger>.Instance, _clock);
    }

    [Fact]
    public async Task A_lan_address_gitberth_cannot_resolve_never_undoes_a_working_publish()
    {
        // The LAN address is a name the PCs resolve (mDNS, a hosts file) but Gitberth doesn't.
        var changer = WithCheckHelper("docker.local", (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        // postgres came back and answers by name; only the check from the host couldn't run, which never fails a change.
        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal($"postgres is back with its new ports. It was offline for {Seconds(result.Offline)}.", result.Message);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("create " + HostScanHelper.NamePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lan_address_that_is_a_name_is_checked_at_the_address_it_resolves_to()
    {
        var changer = WithCheckHelper("docker.lan", (name, _) => Task.FromResult(name == "docker.lan" ? [IPAddress.Parse("10.1.1.80")] : Array.Empty<IPAddress>()));

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", Publish5432));

        Assert.Equal($"postgres answers on docker.lan:5432 (checked from the host). It was offline for {Seconds(result.Offline)}.", result.Message);
    }
}
