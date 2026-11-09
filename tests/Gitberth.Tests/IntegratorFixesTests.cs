using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Where an image comes from, by what's known: Docker's containerd image store gives every image a RepoDigest, the ones
/// built on this host included, so RepoDigests can't tell a registry's image from a local build. A registry is asked only
/// about references Gitberth pulled or a check found there, plus a few new ones at a time; never about Gitberth's own
/// builds or the sample checker's images; and a reference no registry has is left alone for a week.
/// </summary>
public sealed class ImageOriginTests : IDisposable
{
    private const string Old = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";
    private const string Remote = "sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42";

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly ImageKit.UpdateSteps _steps;
    private readonly ImageKit.Clock _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly Security.SecretProtector _protector = ImageKit.Protector();
    private readonly ImageService _images;
    private readonly UpdateService _updates;

    public ImageOriginTests()
    {
        _steps = new ImageKit.UpdateSteps(_db);
        _images = ImageKit.Images(_engine, _db, _protector);
        _updates = new UpdateService(_steps, _images, _db, NullLogger<UpdateService>.Instance) { Time = _clock };
    }

    public void Dispose() => _db.Dispose();

    private static string Hex(char kind, int n) => "sha256:" + kind + n.ToString("x").PadLeft(63, '0');

    private int Asked() => _engine.Calls.Count(c => c.StartsWith("distribution", StringComparison.Ordinal));

    private int Asked(string reference) => _engine.Calls.Count(c => c == $"distribution {reference}");

    // An image as the containerd store has it, local build or not: a tag, and a digest under the tag's name.
    private string Runs(string container, string tag, char kind, int n, Action<JsonObject>? change = null)
    {
        var id = Hex(kind, n);
        var image = ImageKit.Image(id, [tag], [$"{ImageReference.Parse(tag)!.Name}@{Hex('d', n)}"]);
        change?.Invoke(image);
        _engine.Add(image);
        _engine.Uses(container, id, tag);
        _steps.Containers[container] = ImageKit.Container(container, "c-" + container, tag, id);
        return id;
    }

    private void Found(string canonical, string digest) =>
        _engine.Distribution[canonical] = () => new JsonObject { ["Descriptor"] = new JsonObject { ["digest"] = digest } };

    private async Task CheckedAsync(string reference, string? digest, DateTime at)
    {
        await using var db = _db.CreateDbContext();
        db.ImageChecks.Add(new ImageCheck { Reference = reference, RemoteDigest = digest, CheckedAt = at });
        await db.SaveChangesAsync();
    }

    private async Task<ImageCheck?> CheckOfAsync(string reference)
    {
        await using var db = _db.CreateDbContext();
        return await db.ImageChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Reference == reference);
    }

    [Fact]
    public async Task A_sweep_asks_no_registry_about_the_samples_or_gitberths_own_images()
    {
        // The 78 samples the sample checker built here, Gitberth itself, a Git app's build run by hand, and an image a
        // Gitberth build labelled.
        for (var i = 0; i < 78; i++) Runs($"sample-app{i}", $"gitberth-sample-app{i}:latest", 'a', i);
        Runs("gitberth", "gitberth:local", 'b', 1);
        Runs("kanban-by-hand", "gitberth/kanban:425df8a", 'b', 2);
        Runs("shop", "shop:2", 'b', 3, i => i["Config"]!["Labels"]![ImageOrigins.BuildLabel] = "1d2948cc693a");
        // Registry images: two Gitberth hasn't asked about yet, and one it pulled itself (which records what it found).
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        _engine.Uses("postgres", Old, "postgres:18");
        _steps.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        _engine.Distribution["docker.io/library/postgres:18"] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();
        Runs("cloudflare-tunnel", "cloudflare/cloudflared:latest", 'c', 1);
        Found("docker.io/cloudflare/cloudflared:latest", Hex('d', 1));
        Runs("gb-whoami", "traefik/whoami:v1.10", 'c', 2);
        Found("docker.io/traefik/whoami:v1.10", Hex('d', 2));
        await CheckedAsync("docker.io/traefik/whoami:v1.10", Hex('d', 2), _clock.Now.UtcDateTime.AddHours(-7));

        var asked = await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);

        // BG-3 (adapted by the backend fixer): by itself the watcher asks only about what a registry is known to have.
        Assert.Equal(["docker.io/traefik/whoami:v1.10"], asked);
        Assert.Equal(1, Asked());
        Assert.DoesNotContain(_engine.Calls, c => c.Contains("gitberth", StringComparison.Ordinal) || c.Contains("shop", StringComparison.Ordinal));
        // The others are this host's until the owner checks them; what a check finds is a registry's from then on, and
        // the samples stay this host's.
        Assert.Equal(ImageOrigin.Local, (await _updates.StatusAsync("postgres")).Origin);
        Assert.Equal(ImageOrigin.Registry, (await _updates.CheckAsync("postgres")).Origin);
        Assert.Equal(ImageOrigin.Registry, (await _updates.CheckAsync("cloudflare-tunnel")).Origin);
        var sample = await _updates.StatusAsync("sample-app7");
        Assert.Equal((ImageOrigin.Local, false, "Up to date: it runs the image gitberth-sample-app7:latest names."), (sample.Origin, sample.Available, sample.Text));
    }

    // BG-3 (adapted by the backend fixer): the watcher no longer discovers new references a few at a time; it asks about
    // one only once a pull or the owner's own check is on record.
    [Fact]
    public async Task New_references_are_never_asked_by_the_watcher_until_the_owner_checks_one()
    {
        for (var i = 0; i < 12; i++)
        {
            Runs($"tool{i}", $"tool{i}:1", 'e', i);
            Found($"docker.io/library/tool{i}:1", Hex('d', i));
        }

        Assert.Empty(await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        _clock.Now += TimeSpan.FromHours(2);
        Assert.Empty(await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        Assert.Equal(0, Asked());
        // The owner checks one: from then on it's asked every few hours like any registry image.
        Assert.Equal(ImageOrigin.Registry, (await _updates.CheckAsync("tool3")).Origin);
        _clock.Now += TimeSpan.FromHours(7);
        Assert.Equal(["docker.io/library/tool3:1"], await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        Assert.Equal(2, Asked());
    }

    [Fact]
    public async Task A_reference_no_registry_has_is_only_on_this_host()
    {
        Runs("myapp", "myapp:1", 'f', 1);

        // Never asked by itself; the owner's check finds no registry has it.
        Assert.Empty(await _updates.CheckDueAsync(TimeSpan.FromHours(6), default));
        await _updates.CheckAsync("myapp");

        var check = await CheckOfAsync("docker.io/library/myapp:1");
        Assert.Equal((null, ImageOrigins.OnlyHere, _clock.Now.UtcDateTime.AddDays(7)), (check!.RemoteDigest, check.Error, check.RetryAfter));
        var status = await _updates.StatusAsync("myapp");
        Assert.Equal((ImageOrigin.Local, false, "Up to date: it runs the image myapp:1 names.", (string?)null), (status.Origin, status.Available, status.Text, status.Error));
        _clock.Now += TimeSpan.FromDays(8);
        await _updates.CheckDueAsync(TimeSpan.FromHours(6), default);
        Assert.Equal(1, Asked("docker.io/library/myapp:1"));
    }

    [Fact]
    public async Task Refused_without_a_sign_in_is_only_on_this_host_and_a_sign_in_asks_again()
    {
        Runs("private", "private:1", 'f', 2);
        _engine.Distribution["docker.io/library/private:1"] = () =>
            throw new DockerApiException(401, "errors:\ndenied: requested access to the resource is denied\nunauthorized: authentication required\n");

        // BG-3 (adapted by the backend fixer): the owner's checks, not the watcher's.
        await _updates.CheckAsync("private");
        Assert.Equal(ImageOrigins.OnlyHere, (await CheckOfAsync("docker.io/library/private:1"))!.Error);

        // The owner signs in to Docker Hub: the next check asks again, and a refusal now is a sign-in that failed.
        var saved = await new RegistryService(_db, _protector, _engine).SaveAsync(new RegistryInput(null, "docker.io", "someone", "a-password"));
        Assert.True(saved.Ok, saved.Message);
        _clock.Now += TimeSpan.FromHours(1);
        await _updates.CheckAsync("private");

        Assert.Equal(2, Asked("docker.io/library/private:1"));
        var check = await CheckOfAsync("docker.io/library/private:1");
        Assert.Equal(("Can't check: sign-in failed.", _clock.Now.UtcDateTime.AddHours(24)), (check!.Error, check.RetryAfter));
    }

    [Fact]
    public async Task Check_for_update_asks_about_a_local_image_but_never_about_a_sample()
    {
        Runs("myapp", "myapp:1", 'f', 3);
        Runs("sample-go-gin", "gitberth-sample-go-gin:latest", 'f', 4);
        await CheckedAsync("docker.io/library/myapp:1", null, _clock.Now.UtcDateTime);
        await using (var db = _db.CreateDbContext())
        {
            var only = await db.ImageChecks.SingleAsync();
            (only.Error, only.RetryAfter) = (ImageOrigins.OnlyHere, _clock.Now.UtcDateTime.AddDays(7));
            await db.SaveChangesAsync();
        }
        Found("docker.io/library/myapp:1", Hex('d', 3));

        var myapp = await _updates.CheckAsync("myapp");
        var sample = await _updates.CheckAsync("sample-go-gin");

        // Pressed by the owner: asked even within the week, and found, it's a registry's now.
        Assert.Equal(1, Asked("docker.io/library/myapp:1"));
        Assert.Equal(ImageOrigin.Registry, myapp.Origin);
        Assert.Equal((ImageOrigin.Local, 1), (sample.Origin, Asked()));
    }

    [Fact]
    public async Task An_update_pulls_only_what_a_registry_is_known_to_have()
    {
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        _engine.Uses("postgres", Old, "postgres:18");
        _steps.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-up-to-date.jsonl").Split('\n'), null);

        // Not known yet: nothing is pulled, and the tag names what it runs.
        Assert.Equal("It's up to date: postgres runs the image postgres:18 names.", (await _updates.PrepareAsync("postgres")).Blocked);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("pull", StringComparison.Ordinal));
        // Once a check found it on Docker Hub, an update pulls it.
        await CheckedAsync("docker.io/library/postgres:18", Old, _clock.Now.UtcDateTime);
        await _updates.PrepareAsync("postgres");
        Assert.Contains("pull docker.io/library/postgres:18", _engine.Calls);
    }

    [Fact]
    public async Task A_pull_records_that_the_registry_has_it_and_the_facts_say_so()
    {
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-postgres-18.jsonl").Split('\n'), () =>
            _engine.Add(ImageKit.Image(Hex('9', 1), ["postgres:18"], ["postgres@" + Remote], exposed: ["5432/tcp"])));

        var result = await _images.StartPull("postgres:18").Completion;

        Assert.True(result.Ok, result.Message);
        var check = await CheckOfAsync("docker.io/library/postgres:18");
        Assert.Equal((Remote, (string?)null), (check!.RemoteDigest, check.Error));
        Assert.Equal(ImageOrigin.Registry, (await _images.FactsAsync("postgres:18"))!.Origin);
        // An image nobody pulled or checked is this host's, whatever its RepoDigests say.
        Runs("myapp", "myapp:1", 'f', 5);
        Assert.Equal(ImageOrigin.Local, (await _images.FactsAsync("myapp:1"))!.Origin);
    }

    [Fact]
    public async Task The_catalog_says_registry_only_once_a_registry_is_known_to_have_it()
    {
        using var rig = new AppsRig();
        Assert.Equal(ImageOrigin.Local, Assert.Single((await rig.Catalog.ListAsync("10.1.1.80")).Apps, a => a.Key == "postgres").Origin);

        await rig.AddAsync(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = Old, CheckedAt = DateTime.UtcNow });
        await rig.AddAsync(new ImageCheck { Reference = "docker.io/library/gitberth-sample-bun-hono:latest", RemoteDigest = Remote, CheckedAt = DateTime.UtcNow });
        var list = await rig.Catalog.ListAsync("10.1.1.80");

        Assert.Equal(ImageOrigin.Registry, Assert.Single(list.Apps, a => a.Key == "postgres").Origin);
        Assert.Equal(ImageOrigin.Local, Assert.Single(list.Apps, a => a.Key == "sample-bun-hono").Origin);
        Assert.Equal(ImageOrigin.Built, Assert.Single(list.Apps, a => a.Key == "gb-orb").Origin);
    }
}

/// <summary>
/// A container an app made from an image (gitberth.source registry, local or manual) is an app under any name, one renamed
/// outside Gitberth included: deploy temporaries are known by the bridge label or a Git app's container under another
/// name, the engine's copies by the journal and helpers by their label.
/// </summary>
public sealed class RenamedImageAppTests
{
    private static readonly Dictionary<string, NavApp> Whoami = new(StringComparer.Ordinal) { ["whoami"] = new NavApp(3, "whoami", "whoami") };

    private static ContainerSummary Container(string name, params (string Key, string Value)[] labels) =>
        new(name, "running", "Up 2 hours", "traefik/whoami:v1.10", "", labels.ToDictionary(l => l.Key, l => l.Value));

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] labels) => labels.ToDictionary(l => l.Key, l => l.Value);

    [Fact]
    public void Its_role_is_a_containers_not_its_apps_temporary()
    {
        Assert.Equal(ContainerRole.Other, ContainerService.RoleOf(Container("whoami-old", ("gitberth.app", "whoami"), ("gitberth.source", "registry")), Whoami, null));
        Assert.Equal(ContainerRole.Other, ContainerService.RoleOf(Container("whoami-old", ("gitberth.app", "whoami"), ("gitberth.source", "manual")), Whoami, null));
        Assert.Equal(ContainerRole.App, ContainerService.RoleOf(Container("gb-whoami", ("gitberth.app", "whoami"), ("gitberth.source", "registry")), Whoami, null));
        // A Git app's container under another name is its deploy's; so is anything with the bridge label.
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(Container("gb-whoami_prev", ("gitberth.app", "whoami")), Whoami, null));
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(Container("gb-whoami_prev", ("gitberth.app", "whoami"), ("gitberth.source", "git")), Whoami, null));
        Assert.Equal(ContainerRole.AppInternal,
            ContainerService.RoleOf(Container("gb-whoami_next", ("gitberth.app", "whoami"), ("gitberth.source", "registry"), ("gitberth.bridge", "true")), Whoami, null));
    }

    [Fact]
    public async Task It_is_listed_adopted_and_joined_to_networks_like_any_container()
    {
        using var rig = new AppsRig();
        await rig.AddAsync(new AppDefinition { Name = "whoami", Slug = "whoami", Source = AppSource.Registry, ImageRef = "traefik/whoami:v1.10" });
        rig.Engine.AddLike("sample-index", "whoami-old", Labels(("gitberth.managed", "true"), ("gitberth.app", "whoami"), ("gitberth.source", "registry")));
        rig.Engine.AddLike("gb-kanban", "gb-kanban_prev");

        var list = await rig.Catalog.ListAsync("10.1.1.80");
        Assert.Equal(AppSource.Existing, Assert.Single(list.Apps, a => a.Key == "whoami-old").Source);
        Assert.DoesNotContain(list.Apps, a => a.Key == "gb-kanban_prev");

        var row = await rig.Catalog.AdoptAsync("whoami-old");
        Assert.Equal(("whoami-old", AppSource.Existing), (row.Container, row.Source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Catalog.AdoptAsync("gb-kanban_prev"));

        Assert.Null((await rig.Network.NetworksOfAsync("whoami-old")).Note);
        Assert.Equal("This is a deploy's temporary or previous container, which Gitberth removes itself.", (await rig.Network.NetworksOfAsync("gb-kanban_prev")).Note);
    }
}

/// <summary>
/// One record per change in Activity: the engine's. A recreate or an update is its journal row, a live change is
/// ContainerUpdater's entry; the settings and update services add nothing for the same change.
/// </summary>
public sealed class OneActivityRecordTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-activity-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ActivityLog _activity;

    public OneActivityRecordTests()
    {
        Directory.CreateDirectory(_dir);
        _activity = new ActivityLog(_db, new StatsStore(new GitberthOptions { DataDir = _dir }));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The rest of Gitberth, as far as the settings service asks: a real row on adoption, and the real Activity.</summary>
    private sealed class Host(FakeEngine engine, ImageKit.Db db, ActivityLog activity) : IAppSettingsHost
    {
        private int? _appId;

        public TaskCompletionSource Followed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AppView?> ViewAsync(string key, CancellationToken ct)
        {
            if (await engine.InspectContainerAsync(key, ct) is not { } inspect) return null;
            var summary = new AppSummary(key, _appId, key, key, AppSource.Existing, ContainerRole.Other, ImageOrigin.Local, "", null, AppState.Running, "Running",
                ContainerPorts.Str(inspect, "Id"), [], null, null, null, false, false, null);
            var row = _appId is int id ? new AppDefinition { Id = id, Name = key, Slug = key, Container = key, Source = AppSource.Existing } : null;
            return new AppView(summary, row, null, inspect, null, null, null, null);
        }

        public async Task<AppDefinition> AdoptAsync(string container, CancellationToken ct)
        {
            await using var context = db.CreateDbContext();
            var row = new AppDefinition { Name = container, Slug = container, Container = container, Source = AppSource.Existing };
            context.Apps.Add(row);
            await context.SaveChangesAsync(ct);
            _appId = row.Id;
            return row;
        }

        public Task SyncAsync(int appId, CancellationToken ct) => Task.CompletedTask;
        public Task<AppSnapshot?> SnapshotAsync(int appId, CancellationToken ct) => Task.FromResult<AppSnapshot?>(null);
        public Task SaveSnapshotAsync(int appId, AppSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public Task<AppOptions> OptionsAsync(string key, CancellationToken ct) => Task.FromResult(new AppOptions { Name = key });
        public Task<AppOptions> SaveOptionsAsync(string key, AppOptions options, CancellationToken ct) => Task.FromResult(options);
        public ContainerSettings GitContainer(AppDefinition app) => new();
        public GitOptions GitOptions(AppDefinition app) => new("main", null, DockerfileMode.Auto, null, null, true, true);
        public AppOptions GitAppOptions(AppDefinition app) => new() { Name = app.Name };
        public Task<GitSaved> SaveGitAsync(int appId, SettingsEdit edit, CancellationToken ct) => Task.FromResult(new GitSaved(false, false, new HashSet<SettingField>(), []));
        public Task MarkPendingAsync(int appId, CancellationToken ct) => Task.CompletedTask;
        public Task<int?> ApplyIfRunningAsync(int appId, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<bool> DeployActiveAsync(int appId, CancellationToken ct) => Task.FromResult(false);
        public Task<IDisposable> LockAppAsync(int appId, CancellationToken ct) => throw new InvalidOperationException("Not a Git app.");
        public Task<bool> UnnoticedAsync(AppDefinition app, bool running, CancellationToken ct) => Task.FromResult(false);

        public Task<SyncReport?> SyncDomainsAsync(int appId, CancellationToken ct)
        {
            Followed.TrySetResult();
            return Task.FromResult<SyncReport?>(null);
        }

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct) =>
            activity.AddAsync(container, appId, kind, text, tone, ct);
    }

    private (AppSettingsService Service, Host Host, FakeEngine Engine) Settings()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock) { OnCreate = b => b.Listens = [80] };
        var fakeHost = new FakeHost(engine);
        engine.Images["nginx:stable-alpine"] = ContainerSettingsJsonTests.NginxImage();
        var inspect = DockerFixtures.Inspect("sample-index");
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        engine.Add(inspect).Listens = [80, 3000];
        var locks = new ContainerLocks();
        var options = new GitberthOptions { DataDir = _dir };
        PortChanger? changer = null;
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => changer!), _activity, NullLogger<ContainerUpdater>.Instance);
        changer = new PortChanger(engine, new DbChangeJournal(_db), locks, fakeHost, fakeHost, fakeHost, fakeHost, fakeHost, options, NullLogger<PortChanger>.Instance, clock, updater);
        var host = new Host(engine, _db, _activity);
        return (new AppSettingsService(host, changer, updater, engine, options, NullLogger<AppSettingsService>.Instance), host, engine);
    }

    private async Task<IReadOnlyList<ActivityItem>> ItemsAsync(string container)
    {
        await using var db = _db.CreateDbContext();
        var appId = await db.Apps.Where(a => a.Container == container).Select(a => (int?)a.Id).FirstOrDefaultAsync();
        return await _activity.ForAsync(container, appId);
    }

    [Fact]
    public async Task A_live_change_is_one_record()
    {
        var (service, _, _) = Settings();
        var loaded = (await service.LoadAsync("sample-index"))!;
        var edit = new SettingsEdit(loaded.ContainerId, loaded.Container, loaded.Container with { Resources = loaded.Container.Resources with { MemoryBytes = 256L << 20 } },
            loaded.Options, loaded.Options);

        var outcome = await service.ApplyAsync(await service.PlanAsync("sample-index", edit));
        var result = await outcome.Job!.Completion;
        await Task.Delay(200);

        Assert.True(result.Ok, result.Message);
        var item = Assert.Single(await ItemsAsync("sample-index"));
        Assert.Equal((ActivityKind.Live, "Changed the memory limit, live: no restart."), (item.Kind, item.Text));
    }

    [Fact]
    public async Task A_recreate_is_one_record()
    {
        var (service, host, _) = Settings();
        var loaded = (await service.LoadAsync("sample-index"))!;
        var edit = new SettingsEdit(loaded.ContainerId, loaded.Container, loaded.Container with { Env = [.. loaded.Container.Env, new EnvSetting("TZ", "UTC")] },
            loaded.Options, loaded.Options);

        var outcome = await service.ApplyAsync(await service.PlanAsync("sample-index", edit));
        var result = await outcome.Job!.Completion;
        await host.Followed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        var item = Assert.Single(await ItemsAsync("sample-index"));
        Assert.Equal(ActivityKind.Change, item.Kind);
        Assert.NotNull(item.ChangeId);
    }

    /// <summary>The update's steps, with the engine's journal row written as PortChanger writes it, and the real Activity.</summary>
    private sealed class JournalledSteps(ImageKit.UpdateSteps inner, IChangeJournal journal, ActivityLog activity) : IUpdateSteps
    {
        public Task<JsonObject?> InspectContainerAsync(string container, CancellationToken ct) => inner.InspectContainerAsync(container, ct);
        public Task<AppDefinition> AdoptAsync(string key, CancellationToken ct) => inner.AdoptAsync(key, ct);
        public Task SyncAsync(int appId, CancellationToken ct) => inner.SyncAsync(appId, ct);
        public Task<SettingsView?> LoadAsync(string key, CancellationToken ct) => inner.LoadAsync(key, ct);
        public Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct) => inner.PlanAsync(container, request, ct);
        public Task<string?> BusyWithAsync(string container, CancellationToken ct) => inner.BusyWithAsync(container, ct);

        public PortChangeJob Start(PortChangePlan plan)
        {
            journal.AddAsync(new ContainerChange
            {
                ContainerName = plan.Container, Summary = "updated postgres:18 (3a42… → 9999…)", OldId = new string('a', 64), NewId = new string('b', 64),
                OldName = plan.Container + "_old_aaaaaaaaaaaa", TempName = plan.Container + "_new_aaaaaaaaaaaa", WasRunning = true,
                OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""", Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done,
                StartedAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow, Kind = ChangeKind.Update, Fields = "Image",
            }, CancellationToken.None).GetAwaiter().GetResult();
            return inner.Start(plan);
        }

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct) =>
            activity.AddAsync(container, appId, kind, text, tone, ct);
    }

    [Fact]
    public async Task An_update_is_one_record()
    {
        const string old = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";
        const string remote = "sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42";
        var engine = new ImageKit.Engine();
        var inner = new ImageKit.UpdateSteps(_db);
        var updates = new UpdateService(new JournalledSteps(inner, new DbChangeJournal(_db), _activity), ImageKit.Images(engine, _db), _db, NullLogger<UpdateService>.Instance);
        engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        engine.Uses("postgres", old, "postgres:18");
        inner.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        await using (var db = _db.CreateDbContext())
        {
            db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = remote, CheckedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-postgres-18.jsonl").Split('\n'), () =>
        {
            engine.Add(ImageKit.Image(ImageKit.Id('9'), [], ["postgres@" + remote], exposed: ["5432/tcp"], volumes: ["/var/lib/postgresql"]));
            engine.Tag("postgres:18", ImageKit.Id('9'));
        });

        var prepared = await updates.PrepareAsync("postgres");
        Assert.Null(prepared.Blocked);
        Assert.Equal(PortChangeOutcome.Done, (await updates.Start(prepared.Plan!).Completion).Outcome);
        await updates.FollowUpOf("postgres");

        var item = Assert.Single(await ItemsAsync("postgres"));
        Assert.Equal((ActivityKind.Change, ActivityTone.Good), (item.Kind, item.Tone));
        Assert.StartsWith("Update", item.Text, StringComparison.Ordinal);
    }
}

/// <summary>An app Gitberth made from an image (gb-&lt;slug&gt;, gitberth.source registry) has its ports changed like any container's.</summary>
public sealed class ImageAppPortsTests
{
    private static (PortChanger Changer, FakeEngine Engine) Rig()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock) { OnCreate = b => b.Listens = [80] };
        var host = new FakeHost(engine);
        engine.Images["nginx:stable-alpine"] = ContainerSettingsJsonTests.NginxImage();
        var inspect = DockerFixtures.Inspect("sample-index");
        inspect["Name"] = "/gb-whoami";
        foreach (var (key, value) in new[] { ("gitberth.managed", "true"), ("gitberth.app", "whoami"), ("gitberth.source", "registry") })
            inspect["Config"]!["Labels"]![key] = value;
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        engine.Add(inspect).Listens = [80];
        var locks = new ContainerLocks();
        return (new PortChanger(engine, new MemoryJournal(), locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, clock), engine);
    }

    [Fact]
    public async Task A_port_change_on_a_registry_app_plans_like_any_containers()
    {
        var (changer, engine) = Rig();

        var byPorts = await changer.PlanAsync("gb-whoami", [new PortRule(80, "tcp", PortAccess.Lan, 19181)]);
        Assert.True(byPorts.CanApply, byPorts.Blocked ?? byPorts.Verdict.Reason);
        Assert.Equal(RecreateKind.Allowed, byPorts.Verdict.Kind);

        // The settings form's way in, with only the ports changed (the Network tab's PlanPortsAsync makes this request).
        var loaded = ContainerSettingsJson.Read((await engine.InspectContainerAsync("gb-whoami", default))!, null);
        var byRequest = await changer.PlanAsync("gb-whoami", new ContainerEditRequest
        {
            ContainerId = ContainerPorts.Str(await engine.InspectContainerAsync("gb-whoami", default), "Id"),
            Loaded = loaded,
            Wanted = loaded with { Ports = [new PortRule(80, "tcp", PortAccess.Lan, 19181)] },
        });
        Assert.True(byRequest.CanApply, byRequest.Blocked ?? byRequest.Verdict.Reason);
        Assert.False(byRequest.LiveOnly);
    }
}

/// <summary>A non-Git app has no next deploy: its ports apply now, never "for next deploy".</summary>
public sealed class NonGitPortsTests
{
    [Fact]
    public async Task Saving_a_non_git_apps_ports_for_the_next_deploy_is_refused_with_why()
    {
        using var rig = new AppsRig();
        var id = await rig.AddAsync(new AppDefinition { Name = "whoami", Slug = "whoami", Source = AppSource.Registry, ImageRef = "traefik/whoami:v1.10" });

        var saved = await rig.Actions.ApplyPortsAsync(id, [], loaded: null, applyNow: false);

        Assert.False(saved.Ok);
        Assert.Equal("whoami isn't deployed from a repo, so there's no next deploy to wait for: its ports change when its container is recreated. Apply them now instead.",
            Assert.Single(saved.Problems).Message);
        Assert.Empty(rig.Engine.Writes);
    }
}
