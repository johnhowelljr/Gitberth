using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Gitberth.Tests;

/// <summary>A test against a real Docker engine, skipped unless GITBERTH_ENGINE_IT=1.</summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITBERTH_ENGINE_IT") != "1")
            Skip = "Runs against a real Docker engine through /var/run/docker.sock: set GITBERTH_ENGINE_IT=1 to run it.";
    }
}

/// <summary>
/// PortChanger against the real engine, through the real adapters: the Engine API client (DockerEngine), the probe and
/// check helpers, the check by name and the journal in a database file. Opt-in (GITBERTH_ENGINE_IT=1), with Docker's
/// socket at /var/run/docker.sock. Its guard refuses every change to a container whose name doesn't start with gbtest-ub-,
/// and any container that would be on a network other than Docker's own bridge and host, or mount any volume. What it
/// makes is removed at the end, whatever happened. The LAN address it checks is GITBERTH_ENGINE_IT_LAN (10.1.1.80).
/// </summary>
public sealed class EngineIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Prefix = "gbtest-ub-it-";
    private const string HelperPrefix = "gbtest-ub-helper-";
    private const string Image = "nginx:stable-alpine";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-engine-it-" + Guid.NewGuid().ToString("N"));
    private readonly string _lan = Environment.GetEnvironmentVariable("GITBERTH_ENGINE_IT_LAN") ?? "10.1.1.80";
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private DockerApi _api = null!;
    private DockerEngine _real = null!;
    private GuardedEngine _engine = null!;
    private DbChangeJournal _journal = null!;
    private GitberthOptions _options = null!;
    private HostHelpers _helpers = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GITBERTH_ENGINE_IT") != "1") return;
        Directory.CreateDirectory(_dir);
        _options = new GitberthOptions { DataDir = _dir, RunningInContainer = true };
        _api = new DockerApi(_options);
        _real = new DockerEngine(_api);
        _engine = new GuardedEngine(_real);
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options);
        await using (var context = db.CreateDbContext()) await context.Database.MigrateAsync();
        _journal = new DbChangeJournal(db);
        _helpers = new HostHelpers(_engine, _options, Log<HostHelpers>()) { NamePrefix = HelperPrefix, InContainer = true };
        Assert.NotNull(await _real.InspectImageAsync(Image, default));
        // The small public images the settings tests run, by fixed tags; pulled when the host hasn't got them.
        foreach (var (repository, tag) in new[] { ("alpine", "3.19"), ("alpine", "3.20") })
            if (await _real.InspectImageAsync($"{repository}:{tag}", default) is null)
            {
                var pulled = await _api.SendAsync(HttpMethod.Post, $"images/create?fromImage={repository}&tag={tag}", timeout: TimeSpan.FromMinutes(5));
                Assert.DoesNotContain("errorDetail", pulled);
                output.WriteLine($"pulled {repository}:{tag}");
            }
    }

    public async Task DisposeAsync()
    {
        if (_real is null) return;
        // Everything this run made, the copies and helpers too, by the names the guard let it make.
        foreach (var c in (await _real.ListContainersAsync(default)).OfType<JsonObject>())
        {
            var name = NameOf(c);
            if (name.StartsWith(Prefix + _run, StringComparison.Ordinal) || name.StartsWith(HelperPrefix, StringComparison.Ordinal))
            {
                output.WriteLine($"removing {name}");
                await _engine.RemoveAsync(name, force: true, default);
            }
        }
        _api.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [EngineFact]
    public async Task Publishes_moves_and_unpublishes_a_port_on_the_real_engine()
    {
        var name = Name("web");
        var id = await NginxAsync(name, hostPort: null);
        var changer = Changer();
        var a = await FreePortAsync(19281);
        var b = await FreePortAsync(a + 1);

        // Published on a free host port: the new container answers there, and the original is gone.
        var published = await ApplyAsync(changer, name, [new PortRule(80, "tcp", PortAccess.Lan, a)]);
        Assert.Equal(PortChangeOutcome.Done, published.Outcome);
        Assert.Equal($"{name} answers on {_lan}:{a} (checked from the host). It was offline for {Seconds(published.Offline)}.", published.Message);
        Assert.True(await AnswersAsync(a));
        var first = await InspectAsync(name);
        Assert.NotEqual(id, Str(first, "Id"));
        Assert.Equal("unless-stopped", Policy(first));
        Assert.Null(await _real.InspectContainerAsync(id, default));

        // Moved to another: it answers there, and the old one is free again, for Docker too.
        var moved = await ApplyAsync(changer, name, [new PortRule(80, "tcp", PortAccess.Lan, b)]);
        Assert.Equal(PortChangeOutcome.Done, moved.Outcome);
        Assert.True(await AnswersAsync(b));
        Assert.False(await AnswersAsync(a, expect: false));
        Assert.Null(await _helpers.ProbeAsync([new ProbePort("", a, "tcp")], default));

        // Put back as it was: unpublished, so nothing answers on the host.
        var back = await ApplyAsync(changer, name, [new PortRule(80, "tcp", PortAccess.Internal)]);
        Assert.Equal(PortChangeOutcome.Done, back.Outcome);
        Assert.False(await AnswersAsync(b, expect: false));
        var last = await InspectAsync(name);
        Assert.Equal("{}", (last["HostConfig"]!["PortBindings"] ?? new JsonObject()).ToJsonString());
        Assert.Equal("unless-stopped", Policy(last));

        Assert.Equal(["published 80/tcp on the LAN", $"moved 80/tcp to host port {b}", "unpublished 80/tcp"],
            (await HistoryAsync(name)).Select(r => r.Summary));
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task Recreates_a_stopped_container_without_starting_it()
    {
        var name = Name("idle");
        var id = await NginxAsync(name, hostPort: null);
        await _engine.StopAsync(id, 10, default);
        var port = await FreePortAsync(19301);
        var changer = Changer();

        var result = await ApplyAsync(changer, name, [new PortRule(80, "tcp", PortAccess.Lan, port)]);

        // Recreated, still stopped, with no restart policy until its first start passes; the original is kept aside.
        Assert.Equal(PortChangeOutcome.WaitsForStart, result.Outcome);
        var made = await InspectAsync(name);
        Assert.NotEqual(id, Str(made, "Id"));
        Assert.False(Running(made));
        Assert.Equal("no", Policy(made));
        var original = await InspectAsync(PortChanger.OldNameFor(name, id));
        Assert.Equal(id, Str(original, "Id"));
        Assert.False(Running(original));
        Assert.False(await AnswersAsync(port, expect: false));

        // Its first start is checked like a running container's change; only then does the original go.
        await _engine.StartAsync(name, default);
        var started = await changer.ContainerStartedAsync(name);
        Assert.Equal(PortChangeOutcome.Done, started?.Outcome);
        Assert.True(await AnswersAsync(port));
        Assert.Equal("unless-stopped", Policy(await InspectAsync(name)));
        Assert.Null(await _real.InspectContainerAsync(id, default));
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task Settles_a_change_gitberth_stopped_in_the_middle_of()
    {
        // A change can't be cancelled once it has begun (a half-made swap is worse than either end); cancelled before,
        // it changes nothing.
        var name = Name("cut");
        var from = await FreePortAsync(19311);
        var id = await NginxAsync(name, hostPort: from);
        var to = await FreePortAsync(from + 1);
        var cancelled = Changer();
        var plan = await cancelled.PlanAsync(name, [new PortRule(80, "tcp", PortAccess.Lan, to)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ApplyAsync(plan, null, new CancellationToken(canceled: true)));
        Assert.Equal(id, Str(await InspectAsync(name), "Id"));
        Assert.Empty(await HistoryAsync(name));

        // Gitberth stops once the original is stopped and moved aside, before the new one has its name: the change is
        // undone, and the original runs again as it was.
        var stops = new GuardedEngine(_real) { HangAfter = ("rename", PortChanger.OldNameFor(name, id)) };
        var gone = Changer(stops);
        var interrupted = gone.ApplyAsync(await gone.PlanAsync(name, [new PortRule(80, "tcp", PortAccess.Lan, to)]));
        await stops.Hung.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ChangeStep.Renaming, Assert.Single(await _journal.UnfinishedAsync(default)).Step);

        await Changer().RecoverAsync();

        var back = await InspectAsync(name);
        Assert.Equal((id, true, "unless-stopped"), (Str(back, "Id"), Running(back), Policy(back)));
        Assert.True(await AnswersAsync(from));
        Assert.Null(await _real.InspectContainerAsync(PortChanger.TempNameFor(name, id), default));
        var undone = (await HistoryAsync(name))[^1];
        Assert.Equal(ChangeOutcome.RolledBack, undone.Outcome);
        Assert.Equal($"Gitberth restarted while it recreated {name}. It put the original back.", undone.Notice);
        Assert.False(interrupted.IsCompleted);

        // Gitberth stops just after the new container started: it runs and passes the quick checks, so it's finished.
        var late = new GuardedEngine(_real) { HangAfter = ("start", name) };
        var cut = Changer(late);
        var unfinished = cut.ApplyAsync(await cut.PlanAsync(name, [new PortRule(80, "tcp", PortAccess.Lan, to)]));
        await late.Hung.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ChangeStep.Starting, Assert.Single(await _journal.UnfinishedAsync(default)).Step);

        await Changer().RecoverAsync();

        var finished = await InspectAsync(name);
        Assert.NotEqual(id, Str(finished, "Id"));
        Assert.Equal((true, "unless-stopped"), (Running(finished), Policy(finished)));
        Assert.True(await AnswersAsync(to));
        Assert.False(await AnswersAsync(from, expect: false));
        Assert.Null(await _real.InspectContainerAsync(id, default));
        var done = (await HistoryAsync(name))[^1];
        Assert.Equal(ChangeOutcome.Done, done.Outcome);
        Assert.Equal($"Gitberth restarted while it recreated {name}. It finished the change: the new container runs and passed its checks.", done.Notice);
        Assert.False(unfinished.IsCompleted);
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task Knows_its_own_container_by_the_id_in_its_mounts()
    {
        // The test runs in a container on the host's network, whose hostname is the host's: no container is called that.
        // The ID in its own mounts finds it all the same, as it finds Gitberth's own container started with --hostname.
        var id = HostScanner.OwnIdFromMounts();
        Assert.NotNull(id);
        var own = await InspectAsync(id!);
        Assert.Equal(Environment.MachineName, Str(own["Config"], "Hostname"));
        Assert.Equal(id, _options.OwnContainer);
        output.WriteLine($"own container {Str(own, "Name").TrimStart('/')} ({id[..12]}), hostname {Environment.MachineName}");
    }

    [EngineFact]
    public async Task A_lan_address_nothing_resolves_is_a_check_that_could_not_run()
    {
        // .invalid never resolves (RFC 2606): the check isn't made at all, so it can't read as nothing answering.
        var check = await _helpers.ConnectAsync("gbtest-fix.invalid", 80, default);

        Assert.Equal(new ConnectResult(false, "Gitberth can't resolve gbtest-fix.invalid"), check);
        Assert.DoesNotContain((await _real.ListContainersAsync(default)).OfType<JsonObject>(), c => NameOf(c).StartsWith(HelperPrefix, StringComparison.Ordinal));
    }

    [EngineFact]
    public async Task Changes_the_environment_and_memory_of_a_running_container_by_recreating_it()
    {
        var name = Name("env");
        var id = await AlpineAsync(name, "alpine:3.20", ["GBTEST_COLOR=blue", "GBTEST_KEEP=1"]);
        var changer = Changer();
        var request = await EditAsync(name, s => s with
        {
            Env = [.. s.Env.Where(e => e.Key != "GBTEST_COLOR"), new EnvSetting("GBTEST_COLOR", "green"), new EnvSetting("GBTEST_NEW", "yes")],
            Resources = s.Resources with { MemoryBytes = 64L << 20 },
        });

        var result = await ApplyAsync(changer, name, request);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var now = await InspectAsync(name);
        Assert.NotEqual(id, Str(now, "Id"));
        Assert.Null(await _real.InspectContainerAsync(id, default));
        Assert.True(Running(now));
        var env = Env(now);
        Assert.Contains("GBTEST_COLOR=green", env);
        Assert.Contains("GBTEST_NEW=yes", env);
        Assert.Contains("GBTEST_KEEP=1", env);
        Assert.DoesNotContain("GBTEST_COLOR=blue", env);
        Assert.Equal(64L << 20, ContainerPorts.Number(now["HostConfig"], "Memory"));
        // Everything else as it was: its command, its hostname, its restart policy.
        Assert.Equal("""["sleep","3600"]""", now["Config"]!["Cmd"]!.ToJsonString());
        Assert.Equal(id[..12], Str(now["Config"], "Hostname"));
        Assert.Equal("unless-stopped", Policy(now));
        var row = (await HistoryAsync(name)).Single();
        Assert.Equal((ChangeOutcome.Done, ChangeKind.Settings, "Env,Memory"), (row.Outcome, row.Kind, row.Fields));
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task Changes_a_memory_limit_live_without_a_restart()
    {
        var name = Name("live");
        var id = await AlpineAsync(name, "alpine:3.20", []);
        var started = Str((await InspectAsync(name))["State"], "StartedAt");
        var changer = Changer();
        var request = await EditAsync(name, s => s with { Resources = s.Resources with { MemoryBytes = 96L << 20, PidsLimit = 100 }, Restart = new RestartSetting("always") });

        var plan = await changer.PlanAsync(name, request);
        Assert.True(plan.LiveOnly, plan.Blocked);
        var result = await changer.ApplyAsync(plan);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        output.WriteLine(result.Message);
        var now = await InspectAsync(name);
        // The same container, never restarted, with its new limits.
        Assert.Equal((id, started, true), (Str(now, "Id"), Str(now["State"], "StartedAt"), Running(now)));
        Assert.Equal((96L << 20, 192L << 20, 100L), (ContainerPorts.Number(now["HostConfig"], "Memory"), ContainerPorts.Number(now["HostConfig"], "MemorySwap"),
            ContainerPorts.Number(now["HostConfig"], "PidsLimit")));
        Assert.Equal("always", Policy(now));
        Assert.Empty(await HistoryAsync(name));
    }

    [EngineFact]
    public async Task Updates_a_container_to_a_new_image_without_the_old_images_defaults()
    {
        var name = Name("update");
        var id = await AlpineAsync(name, "alpine:3.19", ["GBTEST_KEEP=1"]);
        var ran = Str(await InspectAsync(name), "Image");
        var newImage = (await _real.InspectImageAsync("alpine:3.20", default))!;
        var changer = Changer();
        var request = await EditAsync(name, s => s with { Image = "alpine:3.20" }, ChangeKind.Update, Str(newImage, "Id"));

        var result = await ApplyAsync(changer, name, request);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var now = await InspectAsync(name);
        Assert.Equal(("alpine:3.20", Str(newImage, "Id")), (Str(now["Config"], "Image"), Str(now, "Image")));
        // The body didn't carry the old image's PATH: the new container has the new image's, as Docker gives it.
        var body = _engine.Created[PortChanger.TempNameFor(name, id)];
        Assert.DoesNotContain(Env(body), e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal(["GBTEST_KEEP=1"], Env(body));
        var path = Assert.Single(Env(now), e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Contains(path, Env(newImage["Config"]!));
        Assert.Contains("GBTEST_KEEP=1", Env(now));
        // Its own command and hostname stay.
        Assert.Equal("""["sleep","3600"]""", now["Config"]!["Cmd"]!.ToJsonString());
        Assert.Equal(id[..12], Str(now["Config"], "Hostname"));
        var row = (await HistoryAsync(name)).Single();
        // DATA-8 (the backend fixer): an update's journal row keeps the image it ran by its ID, for Roll back after a restart.
        Assert.Equal((ChangeKind.Update, ran, "alpine:3.20"), (row.Kind, row.ImageBefore, row.ImageAfter));
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task A_command_that_fails_puts_the_original_back()
    {
        var name = Name("bad");
        var id = await AlpineAsync(name, "alpine:3.20", ["GBTEST_KEEP=1"]);
        var changer = Changer();
        var request = await EditAsync(name, s => s with { Command = ["sh", "-c", "echo gbtest-broken; exit 3"] });

        var result = await ApplyAsync(changer, name, request);

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Contains("exit code 3", result.Message);
        Assert.Equal("gbtest-broken", result.LastOutput);
        var back = await InspectAsync(name);
        Assert.Equal((id, true, "unless-stopped"), (Str(back, "Id"), Running(back), Policy(back)));
        Assert.Equal("""["sleep","3600"]""", back["Config"]!["Cmd"]!.ToJsonString());
        Assert.Equal(ChangeOutcome.RolledBack, (await HistoryAsync(name)).Single().Outcome);
        await NothingLeftAsync(name);
    }

    [EngineFact]
    public async Task Creates_a_container_and_a_failed_create_leaves_nothing_behind()
    {
        var name = Name("new");
        var changer = Changer();
        var settings = new ContainerSettings
        {
            Image = "alpine:3.20",
            Command = ["sleep", "3600"],
            Env = [new EnvSetting("GBTEST", "created")],
            Labels = new Dictionary<string, string> { ["gbtest"] = "engine-it" },
            Restart = new RestartSetting("unless-stopped"),
            StopTimeoutSeconds = 1,
        };

        var plan = await changer.PlanCreateAsync(new NewContainerRequest { Name = name, Settings = settings, Networks = [new NetworkJoin("bridge", [])] });
        Assert.True(plan.CanApply, plan.Blocked);
        var made = await changer.ApplyAsync(plan, new Steps([], output));

        output.WriteLine($"{made.Outcome}: {made.Message}");
        Assert.Equal(PortChangeOutcome.Done, made.Outcome);
        var created = await InspectAsync(name);
        Assert.Equal((true, "unless-stopped", "bridge"), (Running(created), Policy(created), Str(created["HostConfig"], "NetworkMode")));
        Assert.Contains("GBTEST=created", Env(created));
        Assert.Contains(Env(created), e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal($"There's already a container called {name}.", (await changer.PlanCreateAsync(new NewContainerRequest
            { Name = name, Settings = settings, Networks = [new NetworkJoin("bridge", [])] })).Blocked);

        var failing = Name("fails");
        var bad = await changer.PlanCreateAsync(new NewContainerRequest
        {
            Name = failing, Settings = settings with { Command = ["sh", "-c", "echo gbtest-no-such-thing >&2; exit 1"] }, Networks = [new NetworkJoin("bridge", [])],
        });
        var failed = await changer.ApplyAsync(bad, new Steps([], output));

        output.WriteLine($"{failed.Outcome}: {failed.Message}");
        Assert.Equal(PortChangeOutcome.RolledBack, failed.Outcome);
        Assert.Equal("gbtest-no-such-thing", failed.LastOutput);
        Assert.Null(await _real.InspectContainerAsync(failing, default));
        Assert.DoesNotContain((await _real.ListContainersAsync(default)).OfType<JsonObject>(), c => NameOf(c).StartsWith(failing, StringComparison.Ordinal));
        Assert.Empty(await HistoryAsync(failing));
    }

    // An alpine that sleeps, with the variables given; it stops at once (its own stop timeout of 1 s), unless-stopped.
    private async Task<string> AlpineAsync(string name, string image, IReadOnlyList<string> env)
    {
        var id = await _engine.CreateAsync(name, new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray("sleep", "3600"),
            ["Env"] = new JsonArray(env.Select(e => (JsonNode)JsonValue.Create(e)).ToArray()),
            ["StopTimeout"] = 1,
            ["Labels"] = new JsonObject { ["gbtest"] = "engine-it" },
            ["HostConfig"] = new JsonObject { ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" } },
        }, default);
        await _engine.StartAsync(id, default);
        return id;
    }

    // The container's settings as the owner loads them, changed as asked.
    private async Task<ContainerEditRequest> EditAsync(string name, Func<ContainerSettings, ContainerSettings> change, ChangeKind kind = ChangeKind.Settings,
        string? expectedImageId = null)
    {
        var inspect = await InspectAsync(name);
        var loaded = ContainerSettingsJson.Read(inspect, await _real.InspectImageAsync(Str(inspect, "Image"), default));
        return new ContainerEditRequest { ContainerId = Str(inspect, "Id"), Loaded = loaded, Wanted = change(loaded), Kind = kind, ExpectedImageId = expectedImageId };
    }

    private async Task<PortChangeResult> ApplyAsync(PortChanger changer, string name, ContainerEditRequest request)
    {
        var plan = await changer.PlanAsync(name, request);
        output.WriteLine($"plan for {name}: {plan} keeps {plan.Keeps}; impact {string.Join(" | ", plan.Impact)}");
        Assert.True(plan.CanApply, plan.Blocked);
        var result = await changer.ApplyAsync(plan, new Steps([], output));
        output.WriteLine($"{result.Outcome}: {result.Message}");
        return result;
    }

    private static List<string> Env(JsonNode config) => (config["Config"] ?? config)["Env"] is JsonArray env ? env.Select(e => (string)e!).ToList() : [];

    private string Name(string what) => $"{Prefix}{_run}-{what}";

    private PortChanger Changer(GuardedEngine? engine = null)
    {
        var host = new ItHost(_real, _lan);
        return new PortChanger(engine ?? _engine, _journal, new ContainerLocks(), host, _helpers, _helpers, new NameCheck(_real, _options), host, _options,
            Log<PortChanger>()) { StableFor = TimeSpan.FromSeconds(5) };
    }

    private async Task<PortChangeResult> ApplyAsync(PortChanger changer, string name, IReadOnlyList<PortRule> ports)
    {
        var plan = await changer.PlanAsync(name, ports);
        output.WriteLine($"plan for {name}: {string.Join("; ", plan.Changes.Select(c => c.Text))} {plan.Blocked}");
        Assert.True(plan.CanApply, plan.Blocked);
        var steps = new List<PortChangeStep>();
        var result = await changer.ApplyAsync(plan, new Steps(steps, output));
        output.WriteLine($"{result.Outcome}: {result.Message}");
        return result;
    }

    // An nginx, started, with port 80 published on the host port given (every address) or not at all.
    private async Task<string> NginxAsync(string name, int? hostPort)
    {
        var bindings = new JsonObject();
        if (hostPort is { } port)
            bindings["80/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "", ["HostPort"] = port.ToString(CultureInfo.InvariantCulture) });
        var id = await _engine.CreateAsync(name, new JsonObject
        {
            ["Image"] = Image,
            ["Labels"] = new JsonObject { ["gbtest"] = "engine-it" },
            ["HostConfig"] = new JsonObject
            {
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
                ["PortBindings"] = bindings,
            },
        }, default);
        await _engine.StartAsync(id, default);
        if (hostPort is { } published) Assert.True(await AnswersAsync(published));
        return id;
    }

    // A host port nothing holds: no container publishes it, nothing answers there, and Docker can bind it.
    private async Task<int> FreePortAsync(int from)
    {
        var published = (await _real.ListContainersAsync(default)).OfType<JsonObject>()
            .SelectMany(c => c["Ports"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => p["PublicPort"]?.GetValue<int>() ?? 0).ToHashSet();
        for (var port = from; port < from + 200; port++)
            if (!published.Contains(port) && !await ConnectsAsync(port) && await _helpers.ProbeAsync([new ProbePort("", port, "tcp")], default) is null)
                return port;
        throw new InvalidOperationException($"No free host port from {from}.");
    }

    // An HTTP GET on the LAN address, as a PC would make it; tried for a few seconds while the answer isn't the one expected.
    private async Task<bool> AnswersAsync(int port, bool expect = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var answered = false;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                answered = (await http.GetAsync($"http://{_lan}:{port.ToString(CultureInfo.InvariantCulture)}/")).IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                answered = false;
            }
            if (answered == expect) break;
            await Task.Delay(500);
        }
        output.WriteLine($"http://{_lan}:{port}/ {(answered ? "answers" : "doesn't answer")}");
        return answered;
    }

    private async Task<bool> ConnectsAsync(int port)
    {
        using var client = new TcpClient();
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(_lan, port, timer.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonObject> InspectAsync(string name) => await _real.InspectContainerAsync(name, default) ?? throw new InvalidOperationException($"There's no {name}.");

    // Every change of the container the journal has, oldest first.
    private async Task<List<ContainerChangeEntry>> HistoryAsync(string name)
    {
        await using var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options).CreateDbContext();
        return await db.ContainerChanges.AsNoTracking().Where(c => c.ContainerName == name).OrderBy(c => c.Id).ToListAsync();
    }

    // Only the container itself: no copy, no original kept aside, no helper.
    private async Task NothingLeftAsync(string name)
    {
        var names = (await _real.ListContainersAsync(default)).OfType<JsonObject>().Select(NameOf).ToList();
        Assert.Single(names, n => n == name);
        Assert.DoesNotContain(names, n => n.StartsWith(name + "_", StringComparison.Ordinal) || n.StartsWith(HelperPrefix, StringComparison.Ordinal));
        Assert.Empty(await _journal.UnfinishedAsync(default));
    }

    private static string NameOf(JsonObject listed) =>
        (listed["Names"] as JsonArray ?? []).Select(n => n?.GetValue<string>().TrimStart('/') ?? "").FirstOrDefault(n => !n.Contains('/')) ?? "";

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    private static bool Running(JsonObject inspect) => inspect["State"]?["Running"]?.GetValueKind() == JsonValueKind.True;

    private static string Policy(JsonObject inspect) => Str(inspect["HostConfig"]?["RestartPolicy"], "Name");

    private static string Seconds(TimeSpan? span) => (span ?? TimeSpan.Zero).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private ILogger<T> Log<T>() => new OutputLogger<T>(output);

    /// <summary>Who holds a host port, from Docker's list of what runs; the LAN address given; no tunnel and nobody using the test's containers.</summary>
    private sealed class ItHost(IContainerEngine engine, string lan) : IHostPortChecks, IContainerUsers
    {
        public Task<string> LanAddressAsync(CancellationToken ct) => Task.FromResult(lan);

        public Task<string> LanHostIpAsync(CancellationToken ct) => Task.FromResult("");

        public async Task<string?> HolderAsync(string container, int hostPort, string protocol, string hostIp, CancellationToken ct) =>
            (await engine.ListContainersAsync(ct)).OfType<JsonObject>()
                .Where(c => NameOf(c) != container && (c["Ports"] as JsonArray ?? []).OfType<JsonObject>()
                    .Any(p => p["PublicPort"]?.GetValue<int>() == hostPort && Str(p, "Type") == protocol && ProcNet.Overlap(Str(p, "IP"), hostIp)))
                .Select(c => $"{NameOf(c)} (container)")
                .FirstOrDefault();

        public Task<IReadOnlyList<string>> TunnelRoutesAsync(int hostPort, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ContainerUsers> UsersOfAsync(string container, IReadOnlyCollection<string> addresses, CancellationToken ct) => Task.FromResult(ContainerUsers.None);
    }

    /// <summary>
    /// The real engine, but a call that changes something is refused unless its container is named gbtest-* (a new one's
    /// name, an existing one's as Docker has it, a rename's new name), and a create may use only Docker's own bridge or
    /// host network and no volume or bind at all. It can hang for good after one call, as if Gitberth stopped right there.
    /// </summary>
    private sealed class GuardedEngine(IContainerEngine real) : IContainerEngine
    {
        /// <summary>The names this run may touch: builder B's throwaways only, as other builders test on the same host.</summary>
        public const string Guard = "gbtest-ub-";

        /// <summary>The call, and the name its container has, after which nothing more ever comes back.</summary>
        public (string Call, string Container)? HangAfter { get; init; }

        public TaskCompletionSource Hung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Each create body sent, by the name it was made under.</summary>
        public Dictionary<string, JsonObject> Created { get; } = new(StringComparer.Ordinal);

        public Task<EngineVersion> VersionAsync(CancellationToken ct) => real.VersionAsync(ct);

        public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => real.InspectContainerAsync(idOrName, ct);

        public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) => real.InspectImageAsync(nameOrId, ct);

        public Task<JsonArray> ListContainersAsync(CancellationToken ct) => real.ListContainersAsync(ct);

        public Task<JsonArray> ListNetworksAsync(CancellationToken ct) => real.ListNetworksAsync(ct);

        public Task<int> WaitAsync(string id, CancellationToken ct) => real.WaitAsync(id, ct);

        public Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct) => real.LogsAsync(id, tail, ct);

        public Task<JsonArray> ChangesAsync(string id, CancellationToken ct) => real.ChangesAsync(id, ct);

        public async Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct)
        {
            Ours(name);
            var host = body["HostConfig"] as JsonObject;
            var mode = Str(host, "NetworkMode");
            if (mode is not ("" or "default" or "bridge" or "host") && !mode.StartsWith(Guard, StringComparison.Ordinal)) Refuse($"{name} on network {mode}");
            foreach (var (network, _) in body["NetworkingConfig"]?["EndpointsConfig"] as JsonObject ?? [])
                if (network != "bridge" && !network.StartsWith(Guard, StringComparison.Ordinal)) Refuse($"{name} on network {network}");
            if (host?["Binds"] is JsonArray { Count: > 0 } || host?["Mounts"] is JsonArray { Count: > 0 } || body["Volumes"] is JsonObject { Count: > 0 })
                Refuse($"{name} with a volume");
            var id = await real.CreateAsync(name, body, ct);
            lock (Created) Created[name] = (JsonObject)body.DeepClone();
            await HangAsync("create", name);
            return id;
        }

        public async Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct)
        {
            var name = await TargetAsync(id, ct);
            await real.UpdateRestartPolicyAsync(id, policy, ct);
            await HangAsync("update", name);
        }

        public async Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct)
        {
            var name = await TargetAsync(id, ct);
            var answer = await real.UpdateAsync(id, resources, ct);
            await HangAsync("update", name);
            return answer;
        }

        public async Task StopAsync(string id, int timeoutSeconds, CancellationToken ct)
        {
            var name = await TargetAsync(id, ct);
            await real.StopAsync(id, timeoutSeconds, ct);
            await HangAsync("stop", name);
        }

        public async Task RenameAsync(string id, string name, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            Ours(name);
            await real.RenameAsync(id, name, ct);
            await HangAsync("rename", name);
        }

        public async Task StartAsync(string id, CancellationToken ct)
        {
            var name = await TargetAsync(id, ct);
            await real.StartAsync(id, ct);
            await HangAsync("start", name);
        }

        public async Task RemoveAsync(string id, bool force, CancellationToken ct)
        {
            await TargetAsync(id, ct);
            await real.RemoveAsync(id, force, ct);
        }

        // This test makes no network; one named gbtest-* would be the only kind let through.
        public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct)
        {
            Ours(Str(body, "Name"));
            return real.CreateNetworkAsync(body, ct);
        }

        public async Task RemoveNetworkAsync(string idOrName, CancellationToken ct)
        {
            var network = (await real.ListNetworksAsync(ct)).OfType<JsonObject>().FirstOrDefault(n => Str(n, "Name") == idOrName || Str(n, "Id") == idOrName);
            Ours(network is null ? idOrName : Str(network, "Name"));
            await real.RemoveNetworkAsync(idOrName, ct);
        }

        // The container's name as Docker has it, which must be a gbtest one. One that isn't there is Docker's 404 to give.
        private async Task<string> TargetAsync(string idOrName, CancellationToken ct)
        {
            if (await real.InspectContainerAsync(idOrName, ct) is not { } inspect) return idOrName;
            var name = Str(inspect, "Name").TrimStart('/');
            Ours(name);
            return name;
        }

        private async Task HangAsync(string call, string container)
        {
            if (HangAfter is not { } hang || hang.Call != call || hang.Container != container) return;
            Hung.TrySetResult();
            await new TaskCompletionSource().Task;
        }

        private static void Ours(string name)
        {
            if (!name.StartsWith(Guard, StringComparison.Ordinal)) Refuse(name);
        }

        private static void Refuse(string what) => throw new InvalidOperationException($"The engine test refused to touch {what}: only {Guard}* containers, on Docker's own networks, without volumes.");
    }

    private sealed class Steps(List<PortChangeStep> steps, ITestOutputHelper output) : IProgress<PortChangeStep>
    {
        public void Report(PortChangeStep value)
        {
            lock (steps) steps.Add(value);
            try
            {
                output.WriteLine($"  {value.Number}/{value.Count} {value.State} {value.Text} {value.TookText}");
            }
            catch (InvalidOperationException)
            {
                // A report after the test ended.
            }
        }
    }

    private sealed class OutputLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                output.WriteLine($"[{typeof(T).Name} {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
