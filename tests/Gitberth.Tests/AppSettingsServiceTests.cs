using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Applying any app's settings by the dispatch of section 2.2, with the rest of Gitberth faked behind its seam and the
/// change engine on the engine kept in memory: a Git app saves and applies its limits live or deploys, any other app goes
/// through the engine (adopted first), Compose gets its YAML, Gitberth itself is left to its file, and an app without a
/// container changes its snapshot.
/// </summary>
public sealed class AppSettingsServiceTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    private readonly FakeHost _host;
    private readonly ContainerLocks _locks = new();
    private readonly FakeApps _apps;
    private readonly AppSettingsService _service;

    public AppSettingsServiceTests()
    {
        _engine = new FakeEngine(_clock) { OnCreate = b => b.Listens = [80] };
        _host = new FakeHost(_engine);
        _engine.Images["nginx:stable-alpine"] = ContainerSettingsJsonTests.NginxImage();
        _apps = new FakeApps(_engine);
        PortChanger? changer = null;
        var updater = new ContainerUpdater(_engine, _locks, new Lazy<PortChanger>(() => changer!), null!, NullLogger<ContainerUpdater>.Instance);
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        changer = new PortChanger(_engine, new MemoryJournal(), _locks, _host, _host, _host, _host, _host, options, NullLogger<PortChanger>.Instance, _clock, updater);
        _service = new AppSettingsService(_apps, changer, updater, _engine, options, NullLogger<AppSettingsService>.Instance);
    }

    /// <summary>The rest of Gitberth, as far as the service asks: views, rows, snapshots, the Git apps' saves and deploys.</summary>
    private sealed class FakeApps(FakeEngine engine) : IAppSettingsHost
    {
        public Dictionary<string, AppView> Views { get; } = new(StringComparer.Ordinal);
        public List<string> Log { get; } = [];
        public GitSaved Saved { get; set; } = new(false, false, new HashSet<SettingField>(), []);
        public AppSnapshot? Snapshot { get; set; }
        public AppSnapshot? SavedSnapshot { get; private set; }
        public int CallsAtAdoption { get; private set; } = -1;

        private void Note(string what)
        {
            lock (Log) Log.Add(what);
        }

        public async Task<AppView?> ViewAsync(string key, CancellationToken ct)
        {
            if (!Views.TryGetValue(key, out var view)) return null;
            // The container as it is now, as the catalog reads it.
            return view.Inspect is null ? view : view with { Inspect = await engine.InspectContainerAsync(view.Summary.Container, ct) };
        }

        public Task<AppDefinition> AdoptAsync(string container, CancellationToken ct)
        {
            CallsAtAdoption = engine.Calls.Count;
            Note("adopt " + container);
            return Task.FromResult(new AppDefinition { Id = 7, Name = container, Slug = container });
        }

        public Task SyncAsync(int appId, CancellationToken ct)
        {
            Note("sync " + appId);
            return Task.CompletedTask;
        }

        public Task<AppSnapshot?> SnapshotAsync(int appId, CancellationToken ct) => Task.FromResult(Snapshot);

        public Task SaveSnapshotAsync(int appId, AppSnapshot snapshot, CancellationToken ct)
        {
            Note("snapshot " + appId);
            SavedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<AppOptions> OptionsAsync(string key, CancellationToken ct) => Task.FromResult(new AppOptions { Name = key });

        public Task<AppOptions> SaveOptionsAsync(string key, AppOptions options, CancellationToken ct)
        {
            Note("options " + key);
            return Task.FromResult(options);
        }

        public ContainerSettings GitContainer(AppDefinition app) => new();

        public GitOptions GitOptions(AppDefinition app) => new("main", null, DockerfileMode.Auto, null, null, true, true);

        public AppOptions GitAppOptions(AppDefinition app) => new() { Name = app.Name };

        public Task<GitSaved> SaveGitAsync(int appId, SettingsEdit edit, CancellationToken ct)
        {
            Note("save " + appId);
            return Task.FromResult(Saved);
        }

        public Task MarkPendingAsync(int appId, CancellationToken ct)
        {
            Note("pending " + appId);
            return Task.CompletedTask;
        }

        public Task<int?> ApplyIfRunningAsync(int appId, CancellationToken ct)
        {
            Note("deploy " + appId);
            return Task.FromResult<int?>(41);
        }

        /// <summary>A deploy of the app is queued or running.</summary>
        public bool DeployActive { get; set; }

        public Task<bool> DeployActiveAsync(int appId, CancellationToken ct) => Task.FromResult(DeployActive);

        public Task<IDisposable> LockAppAsync(int appId, CancellationToken ct)
        {
            Note("lock " + appId);
            return Task.FromResult<IDisposable>(new Released());
        }

        public Task<bool> UnnoticedAsync(AppDefinition app, bool running, CancellationToken ct) => Task.FromResult(false);

        public Task<SyncReport?> SyncDomainsAsync(int appId, CancellationToken ct)
        {
            Note("domains " + appId);
            return Task.FromResult<SyncReport?>(null);
        }

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
        {
            Note($"activity {kind}: {text}");
            return Task.CompletedTask;
        }

        private sealed class Released : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static AppSummary Summary(string container, AppSource source, int? appId, bool running = true, ContainerRole role = ContainerRole.Other) =>
        new(container, appId, container, container, source, role, ImageOrigin.Local, "", null, running ? AppState.Running : AppState.Stopped, "", null, [], null, null, null,
            false, false, null);

    // A container added to the engine, and the catalog's view of it.
    private AppView Container(string fixture, AppSource source, AppDefinition? row = null, Action<JsonObject>? change = null, string? name = null)
    {
        var inspect = DockerFixtures.Inspect(fixture);
        if (name is not null) inspect["Name"] = "/" + name;
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        inspect["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        change?.Invoke(inspect);
        _engine.Add(inspect).Listens = [80, 3000];
        var key = name ?? ((string)inspect["Name"]!).TrimStart('/');
        var view = new AppView(Summary(key, source, row?.Id), row, null, inspect, null, null, null, null);
        _apps.Views[key] = view;
        return view;
    }

    private async Task<SettingsEdit> EditAsync(string key, Func<ContainerSettings, ContainerSettings> change)
    {
        var loaded = await _service.LoadAsync(key) ?? throw new InvalidOperationException(key);
        return new SettingsEdit(loaded.ContainerId, loaded.Container, change(loaded.Container), loaded.Options, loaded.Options);
    }

    private static ContainerSettings WithEnv(ContainerSettings s, string key, string value) => s with { Env = [.. s.Env, new EnvSetting(key, value)] };

    [Fact]
    public async Task A_git_apps_limit_is_saved_then_applied_live_with_nothing_pending()
    {
        var row = new AppDefinition { Id = 1, Name = "kanban", Slug = "kanban" };
        Container("gb-kanban", AppSource.Git, row);
        var settings = ContainerSettingsJson.Read((await _engine.InspectContainerAsync("gb-kanban", default))!, null);
        var edit = new SettingsEdit(null, settings, settings with { Resources = settings.Resources with { MemoryBytes = 1L << 30 } }, new AppOptions(), new AppOptions());
        _apps.Saved = new GitSaved(false, false, new HashSet<SettingField> { SettingField.Memory }, []);

        var plan = await _service.PlanAsync("gb-kanban", edit);
        var outcome = await _service.ApplyAsync(plan);

        Assert.Equal("No restart.", plan.Consequence);
        Assert.Equal(ApplyKind.Live, Assert.Single(plan.Lines).How);
        Assert.Null(plan.Change);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("Changed the memory limit. No restart.", outcome.Message);
        Assert.Equal(["save 1", "lock 1"], _apps.Log.Where(l => !l.StartsWith("activity", StringComparison.Ordinal)));
        Assert.Equal(1L << 30, ContainerPorts.Number(_engine.Get("gb-kanban").HostConfig, "Memory"));
        Assert.Null(outcome.DeploymentId);
    }

    [Fact]
    public async Task A_git_apps_environment_is_saved_and_applied_by_a_deploy_without_a_build()
    {
        var row = new AppDefinition { Id = 1, Name = "kanban", Slug = "kanban" };
        Container("gb-kanban", AppSource.Git, row);
        var settings = new ContainerSettings { Env = [new EnvSetting("TZ", "UTC")] };
        var edit = new SettingsEdit(null, settings, settings with { Env = [new EnvSetting("TZ", "Europe/London")] }, new AppOptions(), new AppOptions());
        _apps.Saved = new GitSaved(true, false, new HashSet<SettingField>(), []);

        var plan = await _service.PlanAsync("gb-kanban", edit);
        var now = await _service.ApplyAsync(plan, ApplyTiming.Now);

        Assert.Equal("Applies with a deploy without a build: a few seconds offline.", plan.Consequence);
        Assert.Equal(ApplyKind.Deploy, Assert.Single(plan.Lines).How);
        Assert.Equal((true, 41), (now.Ok, now.DeploymentId));
        Assert.Equal(["save 1", "deploy 1"], _apps.Log.Where(l => !l.StartsWith("activity", StringComparison.Ordinal)));
        Assert.Empty(_engine.Calls);

        // Saved for the next deploy: no deploy, it stays pending.
        _apps.Log.Clear();
        var later = await _service.ApplyAsync(plan, ApplyTiming.NextDeploy);
        Assert.Equal(("Saved. It applies with the next deploy.", (int?)null), (later.Message, later.DeploymentId));
        Assert.Equal(["save 1"], _apps.Log.Where(l => !l.StartsWith("activity", StringComparison.Ordinal)));
        // Its image and network mode are its repo's and the shared network's.
        var image = await _service.PlanAsync("gb-kanban", edit with { Wanted = settings with { Image = "nginx:alpine" } });
        Assert.Equal("A Git app's image is built from its repo.", Assert.Single(image.Problems).Message);
    }

    [Fact]
    public async Task An_existing_container_is_adopted_before_the_engine_changes_it()
    {
        Container("sample-index", AppSource.Existing);
        var edit = await EditAsync("sample-index", s => WithEnv(s, "TZ", "UTC"));

        var plan = await _service.PlanAsync("sample-index", edit);
        Assert.True(plan.CanApply, plan.Blocked);
        Assert.True(plan.NeedsConfirm);
        Assert.Empty(_apps.Log);

        var outcome = await _service.ApplyAsync(plan);
        var result = await outcome.Job!.Completion;

        Assert.Equal(PortChangeOutcome.Done, result.Outcome);
        Assert.Equal(0, _apps.CallsAtAdoption);
        Assert.Contains("TZ=UTC", _engine.Get("sample-index").Config["Env"]!.AsArray().Select(e => (string)e!));
        // Once it's done, the row follows the new container, and the domains its ports.
        for (var i = 0; i < 100 && !_apps.Log.Contains("domains 7"); i++) await Task.Delay(20);
        Assert.Equal(["adopt sample-index", "sync 7", "domains 7"], _apps.Log.Where(l => !l.StartsWith("activity", StringComparison.Ordinal)));
        // Its record in Activity is the engine's journal row: the service writes none of its own.
        Assert.DoesNotContain(_apps.Log, l => l.StartsWith("activity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_compose_container_gets_its_yaml_for_a_recreate_and_its_limits_live()
    {
        Container("sample-index", AppSource.Existing, change: i =>
        {
            i["Config"]!["Labels"]!["com.docker.compose.project"] = "media";
            i["Config"]!["Labels"]!["com.docker.compose.service"] = "web";
            i["Config"]!["Labels"]!["com.docker.compose.project.config_files"] = "/opt/media/compose.yml";
            i["Config"]!["Labels"]!["com.docker.compose.project.working_dir"] = "/opt/media";
        });

        var env = await _service.PlanAsync("sample-index", await EditAsync("sample-index", s => WithEnv(s, "API_KEY", "abc123456")));

        Assert.False(env.CanApply);
        Assert.Equal("""
            # Put API_KEY in the project's .env (/opt/media/.env): the file reads it from there.
            services:
              web:
                environment:
                  API_KEY: ${API_KEY}
            """.ReplaceLineEndings("\n"), env.ComposeSnippet);
        Assert.Equal("docker compose -p media -f /opt/media/compose.yml up -d web", env.ComposeCommand);
        Assert.Equal(ApplyKind.Compose, Assert.Single(env.Lines).How);
        Assert.False((await _service.ApplyAsync(env)).Ok);

        var memory = await _service.PlanAsync("sample-index", await EditAsync("sample-index", s => s with { Resources = s.Resources with { MemoryBytes = 128L << 20 } }));
        Assert.True(memory.CanApply, memory.Blocked);
        Assert.True(memory.Change!.LiveOnly);
        Assert.Contains("Compose sets these again when it recreates the container.", memory.Change.Impact);
        Assert.Equal("No restart.", memory.Consequence);
    }

    [Fact]
    public async Task Gitberth_itself_is_changed_in_its_compose_file()
    {
        Container("gitberth", AppSource.Existing);

        var plan = await _service.PlanAsync("gitberth", await EditAsync("gitberth", s => WithEnv(s, "TZ", "UTC")));

        Assert.False(plan.CanApply);
        Assert.NotNull(plan.ComposeSnippet);
        Assert.Equal("docker compose -p gitberth -f /opt/gitberth/docker-compose.yml up -d gitberth", plan.ComposeCommand);
        Assert.False((await _service.ApplyAsync(plan)).Ok);
        Assert.Empty(_engine.Calls);
        // Its title still saves.
        var loaded = (await _service.LoadAsync("gitberth"))!;
        Assert.Equal(Enum.GetValues<SettingField>().Length, loaded.Access.ReadOnly.Count);
        var title = await _service.ApplyAsync(await _service.PlanAsync("gitberth",
            new SettingsEdit(loaded.ContainerId, loaded.Container, loaded.Container, loaded.Options, loaded.Options with { Name = "Gitberth" })));
        Assert.True(title.Ok, title.Message);
        Assert.Equal(["options gitberth"], _apps.Log);
    }

    [Fact]
    public async Task An_app_without_a_container_changes_its_snapshot()
    {
        var row = new AppDefinition { Id = 3, Name = "whoami", Slug = "whoami" };
        var saved = new ContainerSettings { Image = "traefik/whoami:v1.10", Env = [new EnvSetting("WHOAMI_PORT_NUMBER", "80")] };
        _apps.Snapshot = new AppSnapshot(saved, [new NetworkJoin("gitberth", ["whoami"])]);
        _apps.Views["gb-whoami"] = new AppView(Summary("gb-whoami", AppSource.Registry, 3, running: false) with { State = AppState.NotCreated }, row, null, null, null, null, null, null);

        var loaded = (await _service.LoadAsync("gb-whoami"))!;
        Assert.Null(loaded.ContainerId);
        Assert.Equal("traefik/whoami:v1.10", loaded.Container.Image);
        var plan = await _service.PlanAsync("gb-whoami", new SettingsEdit(null, loaded.Container, WithEnv(loaded.Container, "TZ", "UTC"), loaded.Options, loaded.Options));
        var outcome = await _service.ApplyAsync(plan);

        Assert.Equal(ApplyKind.Snapshot, Assert.Single(plan.Lines).How);
        Assert.Equal("Saved. It applies when gb-whoami is created.", plan.Consequence);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(["WHOAMI_PORT_NUMBER", "TZ"], _apps.SavedSnapshot!.Settings.Env.Select(e => e.Key));
        Assert.Equal("gitberth", Assert.Single(_apps.SavedSnapshot.Networks).Name);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task The_network_tabs_ports_go_through_the_engine_for_any_app()
    {
        Container("sample-index", AppSource.Registry, new AppDefinition { Id = 5, Name = "web", Slug = "web" });
        var rows = new List<PortRow> { new() { ContainerPort = 80, Protocol = "tcp", Access = PortAccess.Lan, HostPort = 9201, IsWeb = true } };

        var plan = await _service.PlanPortsAsync("sample-index", _engine.Get("sample-index").Id, rows, null);

        Assert.True(plan.CanApply, plan.Blocked);
        // L3 (changed by the backend fixer): only its ports change, so it's journaled as a port change.
        Assert.Equal(ChangeKind.Ports, plan.Change!.Kind);
        Assert.Equal([SettingField.Ports], plan.Change.Fields);
        Assert.Equal(new SettingLine(SettingField.Ports, "80/tcp", "LAN at 10.1.1.80:9100", "LAN at 10.1.1.80:9201", ApplyKind.Recreate), plan.Lines[0]);
        Assert.Contains(plan.Lines, l => l is { Label: "Web port", After: "80", How: ApplyKind.Gitberth });
        Assert.Equal("moved 80/tcp to host port 9201", plan.Change.Summary);
    }

    /// <summary>CONC-4: a save while a deploy is under way is the next deploy's; that deploy read the app before it.</summary>
    [Fact]
    public async Task A_git_apps_change_saved_while_a_deploy_runs_is_saved_for_the_next_deploy()
    {
        var row = new AppDefinition { Id = 1, Name = "kanban", Slug = "kanban" };
        Container("gb-kanban", AppSource.Git, row);
        var settings = new ContainerSettings { Env = [new EnvSetting("TZ", "UTC")] };
        var edit = new SettingsEdit(null, settings, settings with { Env = [new EnvSetting("TZ", "Europe/London")] }, new AppOptions(), new AppOptions());
        _apps.Saved = new GitSaved(true, false, new HashSet<SettingField>(), []);
        _apps.DeployActive = true;

        var plan = await _service.PlanAsync("gb-kanban", edit);
        var outcome = await _service.ApplyAsync(plan, ApplyTiming.Now);

        Assert.Equal("Saved for the next deploy: a deploy is already under way.", plan.Consequence);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(("Saved for the next deploy: a deploy is already under way.", (int?)null), (outcome.Message, outcome.DeploymentId));
        // Nothing is queued behind it, and the deploy under way isn't followed as if it applied the change.
        Assert.Equal(["save 1"], _apps.Log.Where(l => !l.StartsWith("activity", StringComparison.Ordinal)));
        Assert.Contains(_apps.Log, l => l.EndsWith(": saved for the next deploy.", StringComparison.Ordinal));
    }

    /// <summary>L7: the form has no swap limit, so one set before follows a new memory limit, keeping its ratio, and Docker takes it live.</summary>
    [Fact]
    public async Task Raising_the_memory_of_a_container_with_its_own_swap_limit_raises_the_swap_with_it_live()
    {
        Container("sample-index", AppSource.Existing, change: i =>
        {
            i["HostConfig"]!["Memory"] = 512L << 20;
            i["HostConfig"]!["MemorySwap"] = 768L << 20;
        });
        var edit = await EditAsync("sample-index", s => s with { Resources = s.Resources with { MemoryBytes = 1L << 30 } });
        Assert.Equal(768L << 20, edit.Loaded.Resources.MemorySwapBytes);

        var plan = await _service.PlanAsync("sample-index", edit);
        Assert.True(plan.CanApply, plan.Blocked ?? string.Join(" ", plan.Problems.Select(p => p.Message)));
        Assert.True(plan.Change!.LiveOnly);
        Assert.Equal("1 GiB, swap 1.5 GiB", Assert.Single(plan.Lines).After);
        var result = await (await _service.ApplyAsync(plan)).Job!.Completion;

        Assert.True(result.Ok, result.Message);
        var host = _engine.Get("sample-index").HostConfig;
        Assert.Equal((1L << 30, 1536L << 20), (ContainerPorts.Number(host, "Memory"), ContainerPorts.Number(host, "MemorySwap")));
        // Unlimited swap stays unlimited, and none of its own stays Docker's.
        var unlimited = new ContainerSettings { Resources = new ResourceSetting(512L << 20, MemorySwapBytes: -1) };
        Assert.Equal(-1, ContainerUpdater.FollowSwap(unlimited, unlimited with { Resources = unlimited.Resources with { MemoryBytes = 1L << 30 } }).Resources.MemorySwapBytes);
        var none = new ContainerSettings { Resources = new ResourceSetting(512L << 20) };
        Assert.Null(ContainerUpdater.FollowSwap(none, none with { Resources = none.Resources with { MemoryBytes = 1L << 30 } }).Resources.MemorySwapBytes);
    }

    /// <summary>L4: a container limited by a CPU quota changes its shares live; only the shares go to docker update.</summary>
    [Fact]
    public async Task Changing_cpu_shares_on_a_container_with_a_cpu_quota_is_live_and_leaves_the_quota()
    {
        Container("sample-index", AppSource.Existing, change: i =>
        {
            i["HostConfig"]!["CpuQuota"] = 50000;
            i["HostConfig"]!["CpuPeriod"] = 100000;
            i["HostConfig"]!["CpuShares"] = 1024;
        });
        var edit = await EditAsync("sample-index", s => s with { Resources = s.Resources with { CpuShares = 512 } });
        Assert.Equal(0.5, edit.Loaded.Resources.Cpus);

        var plan = await _service.PlanAsync("sample-index", edit);
        Assert.True(plan.CanApply, plan.Blocked ?? string.Join(" ", plan.Problems.Select(p => p.Message)));
        Assert.True(plan.Change!.LiveOnly, "a shares change was planned as a recreate");
        var result = await (await _service.ApplyAsync(plan)).Job!.Completion;

        Assert.True(result.Ok, result.Message);
        Assert.Equal("update sample-index CpuShares=512", _engine.Calls.Single(c => c.StartsWith("update sample-index Cpu", StringComparison.Ordinal)));
        Assert.Equal((512L, 50000L), (ContainerPorts.Number(_engine.Get("sample-index").HostConfig, "CpuShares"), ContainerPorts.Number(_engine.Get("sample-index").HostConfig, "CpuQuota")));
    }
}
