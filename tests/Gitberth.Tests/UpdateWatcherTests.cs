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

/// <summary>The checks every few hours, one per reference, and the automatic updates inside their window.</summary>
public class UpdateWatcherTests : IDisposable
{
    private const string Old = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";
    private const string Remote = "sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42";
    private static readonly string NginxOld = ImageKit.Id('n');
    private static readonly string NginxNew = ImageKit.Id('m');

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly ImageKit.UpdateSteps _steps;
    private readonly ImageKit.Clock _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly Security.SecretProtector _protector = ImageKit.Protector();
    private readonly UpdateService _updates;

    public UpdateWatcherTests()
    {
        _steps = new ImageKit.UpdateSteps(_db);
        _updates = new UpdateService(_steps, ImageKit.Images(_engine, _db, _protector), _db, NullLogger<UpdateService>.Instance) { Time = _clock };
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        _engine.Distribution["docker.io/library/postgres:18"] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();
    }

    public void Dispose() => _db.Dispose();

    private int Asked(string reference) => _engine.Calls.Count(c => c == $"distribution {reference}");

    private UpdateWatcher Watcher() =>
        new(_updates, new SettingsService(_db, ImageKit.Protector()), new GitberthOptions { DataDir = Path.GetTempPath(), OwnContainer = "0123456789ab" },
            NullLogger<UpdateWatcher>.Instance, _clock);

    [Fact]
    public async Task Asks_once_per_reference_however_many_apps_follow_it()
    {
        _engine.Uses("postgres", Old, "postgres:18");
        _engine.Uses("pg-copy", Old, "docker.io/library/postgres:18");
        var redis = ImageKit.Id('r');
        _engine.Add(ImageKit.Image(redis, ["redis:7"], ["redis@" + ImageKit.Digest('r')]));
        _engine.Uses("cache", redis, "redis:7");
        _engine.Distribution["docker.io/library/redis:7"] = () => new JsonObject { ["Descriptor"] = new JsonObject { ["digest"] = ImageKit.Digest('r') } };
        // Built on the host: checked for free whenever it's shown, never asked of a registry.
        _engine.Add(ImageKit.Image(ImageKit.Id('a'), ["gitberth-sample-go-gin:latest"]));
        _engine.Uses("sample-go-gin", ImageKit.Id('a'), "gitberth-sample-go-gin");
        // Gitberth's helpers and a change's copies aren't followed.
        _engine.Uses("postgres_new_0ebf31bbcd07", Old, "postgres:18");
        // BG-3 (the backend fixer): the watcher asks only about what a registry is known to have, here from checks 7 h ago.
        await using (var db = _db.CreateDbContext())
        {
            db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = Old, CheckedAt = _clock.Now.UtcDateTime.AddHours(-7) });
            db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/redis:7", RemoteDigest = ImageKit.Digest('r'), CheckedAt = _clock.Now.UtcDateTime.AddHours(-7) });
            await db.SaveChangesAsync();
        }

        var checkedNow = await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);

        Assert.Equal(["docker.io/library/postgres:18", "docker.io/library/redis:7"], checkedNow);
        Assert.Equal((1, 1), (Asked("docker.io/library/postgres:18"), Asked("docker.io/library/redis:7")));
        Assert.Equal(2, _engine.Calls.Count);
        // A newer digest is news for each container on the old one, once.
        Assert.Equal(["pg-copy", "postgres"], _steps.Activity.Where(a => a.Kind == ActivityKind.UpdateFound).Select(a => a.Container).Order());
        Assert.Equal("postgres:18 has a newer image (e3d2…).", _steps.Activity[0].Text);

        await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);
        Assert.Equal(2, _engine.Calls.Count);
        Assert.Equal(2, _steps.Activity.Count);
    }

    [Fact]
    public void A_reference_is_due_once_its_hours_have_passed_and_not_before_its_retry()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var checks = new Dictionary<string, ImageCheck>
        {
            ["recent"] = new() { Reference = "recent", CheckedAt = now.AddHours(-1) },
            ["old"] = new() { Reference = "old", CheckedAt = now.AddHours(-7) },
            ["refused"] = new() { Reference = "refused", CheckedAt = now.AddHours(-7), RetryAfter = now.AddHours(17) },
            ["waited"] = new() { Reference = "waited", CheckedAt = now.AddHours(-25), RetryAfter = now.AddHours(-1) },
        };

        var due = UpdateService.Due(["new", "recent", "old", "refused", "waited", "new"], checks, now, TimeSpan.FromHours(6));

        Assert.Equal(["new", "old", "waited"], due);
    }

    [Fact]
    public async Task A_refused_sign_in_is_not_asked_again_for_a_day()
    {
        // Signed in to Docker Hub: a refusal is the sign-in's (without one, it would mean no registry has it).
        Assert.True((await new RegistryService(_db, _protector, _engine).SaveAsync(new RegistryInput(null, "docker.io", "someone", "a-password"))).Ok);
        _engine.Uses("postgres", Old, "postgres:18");
        _steps.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        _engine.Distribution["docker.io/library/postgres:18"] = () =>
            throw new DockerApiException(403, "errors:\ndenied: requested access to the resource is denied\nunauthorized: authentication required\n");

        // BG-3 (the backend fixer): the owner's Check for update asks first; the watcher then keeps asking, after a day.
        await _updates.CheckAsync("postgres");

        await using (var db = _db.CreateDbContext())
        {
            var check = await db.ImageChecks.SingleAsync();
            Assert.Equal(("Can't check: sign-in failed.", _clock.Now.UtcDateTime.AddHours(24)), (check.Error, check.RetryAfter));
        }
        _clock.Now += TimeSpan.FromHours(7);
        await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);
        Assert.Equal(1, Asked("docker.io/library/postgres:18"));

        _clock.Now += TimeSpan.FromHours(18);
        await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);
        Assert.Equal(2, Asked("docker.io/library/postgres:18"));
    }

    [Theory]
    [InlineData("postgres:18", null, null, "Databases are never updated by themselves.")]
    [InlineData("mariadb:11", null, null, "Databases are never updated by themselves.")]
    [InlineData("nginx:1.27", "com.docker.compose.project", "shop", "Compose looks after it.")]
    [InlineData("nginx:1.27", "com.docker.compose.project", "gitberth", "Compose looks after it.")]
    [InlineData("nginx:1.27", "com.docker.swarm.service.id", "x1", "Swarm looks after it.")]
    [InlineData("nginx:1.27", "io.kubernetes.pod.name", "web", "Kubernetes looks after it.")]
    [InlineData("nginx:1.27", null, null, null)]
    public void Some_apps_are_never_updated_by_themselves(string image, string? label, string? value, string? why)
    {
        var row = new AppDefinition { Name = "web", Slug = "web", Source = AppSource.Registry, ImageRef = image, UpdatePolicy = UpdatePolicy.Auto };
        var inspect = ImageKit.Container("gb-web", "f00d", image, NginxOld, label is null ? null : new Dictionary<string, string> { [label] = value! });

        Assert.Equal(why, UpdateService.AutoRefusal(row, inspect, "0123456789ab"));
    }

    [Fact]
    public void Gitberth_itself_and_a_github_app_are_never_updated_by_themselves()
    {
        var row = new AppDefinition { Name = "x", Slug = "x", Source = AppSource.Existing, ImageRef = "gitberth:local" };
        var self = ImageKit.Container("gitberth", "0123456789abcdef", "gitberth:local", NginxOld);
        var compose = ImageKit.Container("gitberth-1", "fedcba", "gitberth:local", NginxOld,
            new Dictionary<string, string> { ["com.docker.compose.project"] = "gitberth", ["com.docker.compose.service"] = "gitberth" });

        Assert.Equal("Gitberth doesn't update itself.", UpdateService.AutoRefusal(row, self, "0123456789ab"));
        Assert.Equal("Gitberth doesn't update itself.", UpdateService.AutoRefusal(row, self, "gitberth"));
        Assert.Equal("Gitberth doesn't update itself.", UpdateService.AutoRefusal(row, compose, null));
        Assert.Equal("A GitHub app is updated by a new commit.", UpdateService.AutoRefusal(new AppDefinition { Source = AppSource.Git }, self, null));
    }

    // gb-web runs nginx:1.27 from Docker Hub, with Auto; Docker Hub has a newer one, checked just now.
    private async Task<AppDefinition> AutoAppAsync(string image = "nginx:1.27", string running = "")
    {
        var runs = running.Length > 0 ? running : NginxOld;
        if (_engine.Find(runs) is null) _engine.Add(ImageKit.Image(runs, [image], [$"{ImageReference.Parse(image)!.Name}@{ImageKit.Digest('1')}"]));
        _steps.Containers["gb-web"] = ImageKit.Container("gb-web", "c0ffee", image, runs);
        _engine.Uses("gb-web", runs, image);
        var reference = ImageReference.Parse(image)!;
        await using (var db = _db.CreateDbContext())
        {
            db.ImageChecks.Add(new ImageCheck { Reference = reference.Canonical, RemoteDigest = ImageKit.Digest('2'), CheckedAt = _clock.Now.UtcDateTime });
            await db.SaveChangesAsync();
        }
        _engine.Pulls[$"{reference.FromImage}:{reference.TagOrDigest}"] = (["{\"status\":\"Digest: " + ImageKit.Digest('2') + "\"}"], () =>
        {
            _engine.Add(ImageKit.Image(NginxNew, [], [$"{reference.Name}@{ImageKit.Digest('2')}"]));
            _engine.Tag(image, NginxNew);
        });
        return await _db.AddAsync(new AppDefinition { Name = "web", Slug = "web", Source = AppSource.Registry, ImageRef = image, UpdatePolicy = UpdatePolicy.Auto });
    }

    [Fact]
    public async Task Automatic_updates_run_only_inside_their_window()
    {
        await AutoAppAsync();

        await Watcher().RunOnceAsync(default);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("start", StringComparison.Ordinal));

        _clock.Now = new DateTimeOffset(2026, 11, 8, 3, 30, 0, TimeSpan.Zero);
        await using (var db = _db.CreateDbContext())
            await db.ImageChecks.ExecuteUpdateAsync(s => s.SetProperty(c => c.CheckedAt, _clock.Now.UtcDateTime));
        await Watcher().RunOnceAsync(default);

        Assert.Contains("start Update gb-web", _steps.Calls);
        // Its record in Activity is the engine's journal row, not a second entry.
        Assert.DoesNotContain(_steps.Activity, a => a.Kind == ActivityKind.Updated);
    }

    [Fact]
    public async Task The_window_is_the_owners_choice_and_can_run_past_midnight()
    {
        await AutoAppAsync();
        await new SettingsService(_db, ImageKit.Protector()).SetAsync(UpdateWatcher.AutoWindowKey, "11:00-13:00");

        await Watcher().RunOnceAsync(default);

        Assert.Contains("start Update gb-web", _steps.Calls);
        var late = AutoWindow.Parse("22:30-02:00")!;
        Assert.True(late.Contains(new TimeOnly(1, 0)));
        Assert.True(late.Contains(new TimeOnly(23, 0)));
        Assert.False(late.Contains(new TimeOnly(12, 0)));
        Assert.Null(AutoWindow.Parse("soon"));
        Assert.Equal("03:00-05:00", AutoWindow.Default.ToString());
    }

    /// <summary>Gitberth's clock in a time zone of its own (its TZ), which needn't be the host's.</summary>
    private sealed class Zoned(ImageKit.Clock clock, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.Now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public async Task The_window_is_in_gitberths_own_time_zone_and_settings_says_which()
    {
        // 01:30 UTC is 03:30 where Gitberth's TZ puts it: inside the window, as Settings says it's counted.
        _clock.Now = new DateTimeOffset(2026, 11, 8, 1, 30, 0, TimeSpan.Zero);
        await AutoAppAsync();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Plus-Two", TimeSpan.FromHours(2), "Test/Plus-Two", "Test/Plus-Two");
        var watcher = new UpdateWatcher(_updates, new SettingsService(_db, ImageKit.Protector()), new GitberthOptions { DataDir = Path.GetTempPath(), OwnContainer = "0123456789ab" },
            NullLogger<UpdateWatcher>.Instance, new Zoned(_clock, zone));

        await watcher.RunOnceAsync(default);

        Assert.Contains("start Update gb-web", _steps.Calls);
        Assert.Equal("Test/Plus-Two", UpdateWatcher.ZoneName(new Zoned(_clock, zone)));
        // Unset, it's UTC, however the platform names it.
        Assert.Equal("UTC", UpdateWatcher.ZoneName(_clock));
        Assert.Equal("UTC", UpdateWatcher.ZoneName(new Zoned(_clock, TimeZoneInfo.CreateCustomTimeZone("Etc/UTC", TimeSpan.Zero, "Etc/UTC", "Etc/UTC"))));
    }

    [Fact]
    public async Task A_database_is_never_updated_by_itself_even_with_auto()
    {
        await AutoAppAsync("postgres:18", Old);

        var updated = await _updates.AutoUpdateAsync("0123456789ab", default);

        Assert.Empty(updated);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("adopt", StringComparison.Ordinal));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("pull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_automatic_update_that_fails_goes_back_to_notify()
    {
        var row = await AutoAppAsync();
        _steps.Result = new PortChangeResult(PortChangeOutcome.RolledBack, "gb-web exited with code 1 within 15 s, so Gitberth put the original back.");

        var updated = await _updates.AutoUpdateAsync("0123456789ab", default);

        Assert.Empty(updated);
        Assert.Equal(UpdatePolicy.Notify, (await _db.AppAsync(row.Id)).UpdatePolicy);
        var said = _steps.Activity.Last();
        Assert.Equal(ActivityTone.Warning, said.Tone);
        // Said with the policies' names as Settings shows them, never the enum's.
        Assert.Equal("Automatic update to nginx:1.27 failed and was rolled back: gb-web exited with code 1 within 15 s, so Gitberth put the original back. " +
                     "Updates are back to Tell me until you choose Automatically again.", said.Text);
    }

    [Fact]
    public async Task An_app_set_to_when_i_ask_is_never_checked_by_itself()
    {
        _engine.Uses("postgres", Old, "postgres:18");
        _steps.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        var row = await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", UpdatePolicy = UpdatePolicy.Off });
        // Docker Hub is known to have it, checked 7 hours ago: due, but nobody but this app follows it.
        await using (var db = _db.CreateDbContext())
        {
            db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = Old, CheckedAt = _clock.Now.UtcDateTime.AddHours(-7) });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        Assert.Equal(0, Asked("docker.io/library/postgres:18"));

        // The owner asks: it's checked, and its page says what was found.
        var status = await _updates.CheckAsync("postgres");
        Assert.Equal(1, Asked("docker.io/library/postgres:18"));
        Assert.True(status.Available);

        // Another app on the same reference that's set to Tell me has it checked, and Docker Hub has moved on again: the
        // news is that app's, not this one's.
        _engine.Uses("pg-copy", Old, "postgres:18");
        _engine.Distribution["docker.io/library/postgres:18"] = () => new JsonObject { ["Descriptor"] = new JsonObject { ["digest"] = ImageKit.Digest('9') } };
        _clock.Now += TimeSpan.FromHours(7);
        Assert.Equal(["docker.io/library/postgres:18"], await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        Assert.Equal(["pg-copy"], _steps.Activity.Where(a => a.Kind == ActivityKind.UpdateFound).Select(a => a.Container));
        Assert.Equal(UpdatePolicy.Off, (await _db.AppAsync(row.Id)).UpdatePolicy);
    }

    [Fact]
    public async Task An_unsettled_change_holds_an_automatic_update_back()
    {
        await AutoAppAsync();
        _steps.Busy = "A change on gb-web waits for you: keep the new one or put the original back.";

        Assert.Empty(await _updates.AutoUpdateAsync(null, default));
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
    }
}
