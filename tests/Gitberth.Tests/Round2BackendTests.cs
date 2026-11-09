using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Round 2 of the unified-apps review, the backend's items: one masking rule everywhere (SEC-4), a change that ends
/// waiting for the owner (CONC-2), two first checks of the same image reference (CONC-5), and the mounts and GPU options a
/// change keeps though Gitberth doesn't model them (L5).
/// </summary>
public sealed class Round2BackendTests
{
    // A stand-in; no value from the host.
    private const string Pass = "Hunter2Round2";

    // ---- SEC-4: one rule ----

    [Theory]
    [InlineData("postgres://app:Ab3/x9+QHunter2@db:5432/app", true)]
    [InlineData("redis://:Hunter2Round2@cache:6379", true)]
    [InlineData("jdbc:postgresql://app:Hunter2Round2@db/app", true)]
    [InlineData("--db mysql://app:Hunter2Round2@db/app", true)]
    [InlineData("http://localhost:5173/@vite/client", false)]
    [InlineData("https://example.com/a:b@c", false)]
    [InlineData("postgres://postgres/kanban", false)]
    public void A_url_password_is_found_anywhere_in_a_value(string value, bool found) => Assert.Equal(found, Masking.HasUrlPassword(value));

    [Theory]
    [InlineData("splunk-token", true)]
    [InlineData("com.example.api-key", true)]
    [InlineData("ConnectionStrings__Main", true)]
    [InlineData("traefik.http.routers.web.rule", false)]
    [InlineData("max-size", false)]
    public void A_labels_or_log_options_name_is_read_like_a_variables(string name, bool secret) => Assert.Equal(secret, Masking.IsSecretName(name));

    [Fact]
    public void A_command_line_hides_its_secrets_word_for_word()
    {
        Assert.Equal(["redis-server", "--requirepass", Masking.Mask], Masking.Args(["redis-server", "--requirepass", Pass]));
        Assert.Equal(["redis-server", "--masterauth", Masking.Mask, "--port", "6379"], Masking.Args(["redis-server", "--masterauth", Pass, "--port", "6379"]));
        Assert.Equal(["sh", "-c", $"cloudflared tunnel --no-autoupdate run --token {Masking.Mask}"],
            Masking.Args(["sh", "-c", $"cloudflared tunnel --no-autoupdate run --token {Pass}"]));
        Assert.Equal(["server", $"--password={Masking.Mask}", "--port", "8080"], Masking.Args(["server", "--password=" + Pass, "--port", "8080"]));
        Assert.Equal(["env", $"API_KEY={Masking.Mask}", "serve"], Masking.Args(["env", "API_KEY=" + Pass, "serve"]));
        // A marked variable's name hides it too; one-letter options and plain words don't.
        Assert.Equal([$"PIN={Masking.Mask}"], Masking.Args(["PIN=" + Pass], ["PIN"]));
        Assert.Equal(["serve", "--port", "8080", "-p", Pass], Masking.Args(["serve", "--port", "8080", "-p", Pass]));
        // A secret quoted inside another option's value is found too.
        Assert.Equal($"run --log \"--password {Masking.Mask}\" --verbose", Masking.Text($"run --log \"--password {Pass}\" --verbose"));
    }

    [Fact]
    public void A_value_is_secret_by_its_name_or_by_a_secret_in_it()
    {
        Assert.True(Masking.IsSecret("DATABASE_URL", "postgres://app:Ab3/x9+QHunter2@db:5432/app"));
        Assert.True(Masking.IsSecret("EXTRA_ARGS", "--token " + Pass));
        Assert.True(Masking.IsSecret("PIN", "1234", ["PIN"]));
        Assert.False(Masking.IsSecret("TZ", "Europe/London"));
        Assert.Equal("postgres://app:••••@db:5432/app", Masking.Value("DATABASE_URL", "postgres://app:Ab3/x9+QHunter2@db:5432/app"));
        Assert.Equal(Masking.Mask, Masking.Value("TZ", "Europe/London", secret: true));
    }

    [Fact]
    public void Labels_log_options_and_the_health_command_are_masked_in_the_plan_the_docker_run_and_the_snippet()
    {
        var loaded = new ContainerSettings { Image = "redis:8" };
        var wanted = loaded with
        {
            Labels = new Dictionary<string, string> { ["com.example.api-key"] = Pass, ["backup.target"] = $"s3://backup:{Pass}@minio:9000/bucket", ["team"] = "media" },
            Logging = new LogSetting("splunk", new Dictionary<string, string> { ["splunk-token"] = Pass, ["splunk-url"] = $"https://splunk:{Pass}@logs.example:8088" }),
            Health = new HealthSetting(HealthMode.Custom, ["CMD-SHELL", $"redis-cli --pass {Pass} ping"], TimeSpan.FromSeconds(30)),
        };
        var fields = new HashSet<SettingField> { SettingField.Labels, SettingField.Logging, SettingField.Health };

        var lines = SettingsDiff.Lines(loaded, wanted, fields, [], _ => ApplyKind.Recreate);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.DoesNotContain(Pass, l.After, StringComparison.Ordinal));
        Assert.Contains("team=media", lines.Single(l => l.Field == SettingField.Labels).After, StringComparison.Ordinal);

        var run = ContainerSettingsJson.DockerRun("cache", wanted, [], []);
        Assert.DoesNotContain(Pass, run, StringComparison.Ordinal);
        Assert.Contains("--health-cmd 'redis-cli --pass •••• ping'", run, StringComparison.Ordinal);

        var yaml = ComposeSnippet.For(new ComposeService("cache", "redis", ["/opt/cache/compose.yml"], "/opt/cache"), wanted, fields, []);
        Assert.DoesNotContain(Pass, yaml, StringComparison.Ordinal);
        Assert.StartsWith("# Values shown as •••• are hidden: keep them as your file has them.\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_git_apps_deploy_logs_its_labels_log_options_and_health_command_masked()
    {
        var spec = new ContainerSpec
        {
            Name = "gb-cache", Image = "gitberth/cache:1", Networks = [new NetworkJoin("gitberth", ["cache"])],
            Env = [new EnvValue("REDIS_URL", $"redis://:{Pass}@redis:6379", false)],
            Extras = new GitExtras
            {
                Labels = new SortedDictionary<string, string> { ["com.example.api-key"] = Pass },
                LogDriver = "splunk",
                LogOptions = new SortedDictionary<string, string> { ["splunk-token"] = Pass },
                Health = new GitHealth(HealthMode.Custom, ["CMD-SHELL", $"redis-cli --pass {Pass} ping"]),
                Command = ["redis-server", "--requirepass", Pass],
            },
        };

        var logged = spec.ToDisplayString();

        Assert.DoesNotContain(Pass, logged, StringComparison.Ordinal);
        Assert.Contains("com.example.api-key=••••", logged, StringComparison.Ordinal);
        Assert.Contains("REDIS_URL=redis://:••••@redis:6379", logged, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changes_messages_are_scrubbed_by_the_same_rule()
    {
        var scrub = Masking.Scrubber([KeyValuePair.Create("POSTGRES_PASSWORD", Pass), KeyValuePair.Create("TZ", "Europe/London")]);

        var text = scrub($"FATAL: password {Pass} rejected; tried postgres://app:other-pass@db/app with --token abcdef in Europe/London");

        Assert.Equal("FATAL: password •••• rejected; tried postgres://app:••••@db/app with --token •••• in Europe/London", text);
    }

    // ---- CONC-2: a change left for the owner ends too ----

    [Fact]
    public async Task A_change_whose_original_wont_start_again_ends_with_the_original_holding_its_name()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock)
        {
            OnCreate = b =>
            {
                b.Listens = [5432];
                b.ExitsAfter = TimeSpan.FromSeconds(2);
            },
        };
        var host = new FakeHost(engine);
        engine.Images["postgres:18"] = DockerFixtures.Json("image-postgres.json").AsObject();
        var inspect = DockerFixtures.Inspect("postgres");
        inspect["HostConfig"]!["NetworkMode"] = "gitberth";
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        engine.Add(inspect).Listens = [5432];
        var originalId = engine.Get("postgres").Id;
        // The new one fails its checks; the original, back under its name, won't start.
        engine.Fail = (call, target) => call == "start" && engine.Boxes.FirstOrDefault(b => b.Name == target)?.Id == originalId
            ? new EngineException(500, "OCI runtime create failed") : null;
        var locks = new ContainerLocks();
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, new MemoryJournal(), locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, clock, updater);
        var ended = new List<ContainerChange>();
        changer.Ended += c => ended.Add(c);
        var live = (await engine.InspectContainerAsync("postgres", default))!;
        var loaded = ContainerSettingsJson.Read(live, await engine.InspectImageAsync((string)live["Image"]!, default));
        var request = new ContainerEditRequest
        {
            ContainerId = originalId, Loaded = loaded, Wanted = loaded with { Env = [.. loaded.Env, new EnvSetting("TZ", "Europe/London")] }, Kind = ChangeKind.Settings,
        };

        var result = await changer.ApplyAsync(await changer.PlanAsync("postgres", request));

        Assert.Equal(PortChangeOutcome.NeedsDecision, result.Outcome);
        // AppSync hears of it, and the row follows what holds the name: the original, renamed back before its start.
        Assert.Equal(ChangeOutcome.NeedsDecision, Assert.Single(ended).Outcome);
        Assert.Equal(originalId, engine.Get("postgres").Id);
        Assert.False(engine.Get("postgres").Running);
        Assert.Contains(engine.Boxes, b => b.Name == PortChanger.TempNameFor("postgres", originalId) && !b.Running);
    }

    // ---- CONC-5: the second of two first checks updates the first's row ----

    /// <summary>The other check, inserting the reference's row just before this one saves its own.</summary>
    private sealed class OtherCheckFirst(DbContextOptions<GitberthDb> plain) : SaveChangesInterceptor
    {
        public int Collisions { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var added = eventData.Context?.ChangeTracker.Entries<ImageCheck>().FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
            if (added is not null && Collisions == 0)
            {
                Collisions++;
                await using var other = new GitberthDb(plain);
                other.ImageChecks.Add(new ImageCheck { Reference = added.Reference, CheckedAt = DateTime.UtcNow, Error = "Can't check: the other check's answer." });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Two_first_checks_of_the_same_reference_dont_collide()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var plain = new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options;
        using (var db = new GitberthDb(plain)) db.Database.EnsureCreated();
        var other = new OtherCheckFirst(plain);
        var checks = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).AddInterceptors(other).Options);
        using var images = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        engine.Distribution["registry.example.com/tool:2"] = () => new JsonObject { ["Descriptor"] = new JsonObject { ["digest"] = ImageKit.Digest('u') } };
        var updates = new UpdateService(new ImageKit.UpdateSteps(images), ImageKit.Images(engine, images), checks, NullLogger<UpdateService>.Instance);

        var check = await updates.CheckReferenceAsync(ImageReference.Parse("registry.example.com/tool:2")!, null, default);

        Assert.Equal(1, other.Collisions);
        Assert.Equal((ImageKit.Digest('u'), (string?)null), (check.RemoteDigest, check.Error));
        using var after = new GitberthDb(plain);
        var row = Assert.Single(after.ImageChecks);
        Assert.Equal((ImageKit.Digest('u'), (string?)null), (row.RemoteDigest, row.Error));
    }

    // ---- L5: what a change keeps though Gitberth doesn't model it ----

    [Fact]
    public void A_mounts_edit_keeps_mount_types_gitberth_doesnt_model_and_a_gpus_edit_keeps_the_driver_and_options()
    {
        var body = new JsonObject
        {
            ["HostConfig"] = new JsonObject
            {
                ["Mounts"] = new JsonArray(
                    new JsonObject { ["Type"] = "image", ["Source"] = "tools:1", ["Target"] = "/opt/tools", ["ReadOnly"] = true },
                    new JsonObject { ["Type"] = "npipe", ["Source"] = @"\\.\pipe\docker_engine", ["Target"] = @"\\.\pipe\docker_engine" },
                    new JsonObject { ["Type"] = "volume", ["Source"] = "data", ["Target"] = "/data", ["ReadOnly"] = false }),
                ["DeviceRequests"] = new JsonArray(new JsonObject
                {
                    ["Driver"] = "nvidia", ["Count"] = -1, ["DeviceIDs"] = null, ["Capabilities"] = new JsonArray(new JsonArray("gpu", "compute")),
                    ["Options"] = new JsonObject { ["com.nvidia.visible"] = "all" },
                }),
            },
        };
        var settings = new ContainerSettings
        {
            Image = "x:1",
            // The volume stays and a bind is added; the image mount isn't in the form at all.
            Mounts = [new MountSetting(MountKind.Volume, "data", "/data"), new MountSetting(MountKind.Bind, "/srv/x", "/x")],
            Gpus = "device=0",
        };

        ContainerSettingsJson.Write(body, settings, new HashSet<SettingField> { SettingField.Mounts, SettingField.Gpus });

        var host = body["HostConfig"]!.AsObject();
        var mounts = host["Mounts"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(["/data", "/opt/tools", @"\\.\pipe\docker_engine"], mounts.Select(m => (string)m["Target"]!));
        Assert.Equal(("image", "tools:1", true), ((string)mounts[1]["Type"]!, (string)mounts[1]["Source"]!, (bool)mounts[1]["ReadOnly"]!));
        Assert.Equal(["/srv/x:/x"], host["Binds"]!.AsArray().Select(b => (string)b!));
        var gpu = Assert.IsType<JsonObject>(Assert.Single(host["DeviceRequests"]!.AsArray()));
        Assert.Equal(("nvidia", 0L, "all"), ((string)gpu["Driver"]!, ContainerPorts.Number(gpu, "Count"), (string)gpu["Options"]!["com.nvidia.visible"]!));
        Assert.Equal(["0"], gpu["DeviceIDs"]!.AsArray().Select(d => (string)d!));
        Assert.Equal("""[["gpu","compute"]]""", gpu["Capabilities"]!.ToJsonString());
    }
}
