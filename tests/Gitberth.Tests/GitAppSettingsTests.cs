using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// A Git app's settings as one ContainerSettings and back (2.2): columns, rows and RuntimeExtras; the runtime fingerprint
/// in two (what a recreate takes, what docker update takes); the docker run flags for the extras; and saving from the
/// unified form.
/// </summary>
public sealed class GitAppSettingsTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to GitHub or Cloudflare.");
    }

    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly AppService _apps;
    private readonly HealthSettings _health;
    private readonly RestartSchedules _restarts;

    public GitAppSettingsTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var db = _db.CreateDbContext()) db.Database.EnsureCreated();
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        var settings = new SettingsService(_db, _protector);
        var cli = new DockerCli(options) { Runner = (_, _, _) => Task.FromResult(new ProcessResult(1, "", "no docker here")) };
        var domains = new DomainService(_db, settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _health = new HealthSettings(settings);
        _restarts = new RestartSchedules(settings);
        _apps = new AppService(_db, _protector, cli, settings, _health, domains, options, _restarts, new WatchState());
    }

    public void Dispose() => _connection.Dispose();

    private AppDefinition Kanban() => new()
    {
        Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:425df8a", MemoryLimitMb = 512, CpuLimit = 1.5,
        Ports =
        [
            new PortMapping { ContainerPort = 3000, HostPort = 3001, Access = PortAccess.Lan, IsWeb = true },
            new PortMapping { ContainerPort = 9229, Access = PortAccess.Internal },
        ],
        EnvVars =
        [
            new EnvVar { Key = "NEXT_PUBLIC_SITE", ProtectedValue = _protector.Protect("https://harborlab.com"), BuildTime = true },
            new EnvVar { Key = "DATABASE_URL", ProtectedValue = _protector.Protect("postgres://kanban:s3cret@postgres:5432/kanban"), IsSecret = true },
        ],
        Volumes = [new VolumeMount { Source = "uploads", Target = "/app/uploads" }, new VolumeMount { Source = "/srv/kanban", Target = "/srv", ReadOnly = true }],
        Networks = [new AppNetwork { Network = "media", Alias = "kanban-db" }],
    };

    // Every setting a Git app has beyond the classic ones, each given a value.
    private static ContainerSettings EveryExtra(ContainerSettings s) => s with
    {
        Entrypoint = ["tini", "--"],
        Command = ["node", "server.js", "--password=hunter2"],
        WorkingDir = "/app",
        User = "1000:1000",
        Mounts = [.. s.Mounts, new MountSetting(MountKind.Tmpfs, "", "/tmp/cache", ReadOnly: false, TmpfsBytes: 64L << 20)],
        Hostname = "kanban-host",
        Domainname = "lan",
        ExtraHosts = ["db.lan:10.1.1.5"],
        Dns = ["10.1.1.1"],
        DnsSearch = ["lan"],
        DnsOptions = ["ndots:2"],
        Restart = new RestartSetting("on-failure", 5),
        Resources = s.Resources with { MemoryReservationBytes = 256L << 20, MemorySwapBytes = 1L << 30, CpuShares = 512, CpusetCpus = "0-1", PidsLimit = 200 },
        ShmSizeBytes = 128L << 20,
        Ulimits = [new UlimitSetting("nofile", 1024, 2048)],
        Health = new HealthSetting(HealthMode.Custom, ["CMD-SHELL", "curl -f http://localhost:3000/health || exit 1"], TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5), 3, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1500)),
        Labels = new Dictionary<string, string>(s.Labels) { ["team"] = "web", ["com.example.tier"] = "front" },
        Devices = [new DeviceSetting("/dev/dri", "/dev/dri")],
        Gpus = "device=0,1",
        CapAdd = ["NET_ADMIN"],
        CapDrop = ["MKNOD"],
        Privileged = true,
        SecurityOpt = ["no-new-privileges"],
        ReadOnlyRootfs = true,
        Init = true,
        Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" },
        Logging = new LogSetting("local", new Dictionary<string, string> { ["max-size"] = "20m" }),
        StopSignal = "SIGINT",
        StopTimeoutSeconds = 30,
        Tty = true,
        OpenStdin = true,
    };

    [Fact]
    public void An_app_without_extras_keeps_its_runtime_key_split_in_two()
    {
        var app = Kanban();
        var (runtime, _) = AppService.Fingerprint(app, _protector, "gitberth");
        var (recreate, live) = AppService.RuntimeKeys(app, _protector, "gitberth");

        Assert.Equal(runtime, recreate + "#" + live);
        Assert.Equal("unless-stopped#512#1.5", live);
        Assert.DoesNotContain("extras", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Extras_change_only_the_recreate_key_and_live_settings_only_the_live_key()
    {
        var app = Kanban();
        var before = AppService.RuntimeKeys(app, _protector, "gitberth");

        app.RuntimeExtras = GitExtras.Write(new GitExtras { Hostname = "kanban-host" });
        var hostname = AppService.RuntimeKeys(app, _protector, "gitberth");
        Assert.NotEqual(before.Recreate, hostname.Recreate);
        Assert.Equal(before.Live, hostname.Live);
        Assert.EndsWith("#extras:" + app.RuntimeExtras, AppService.Fingerprint(app, _protector, "gitberth").Runtime, StringComparison.Ordinal);

        Action<AppDefinition>[] changes =
        [
            a => a.RestartPolicy = "always", a => a.MemoryLimitMb = 1024, a => a.CpuLimit = 2,
            a => a.RuntimeExtras = GitExtras.Write(new GitExtras { Hostname = "kanban-host", PidsLimit = 100 }),
            a => a.RuntimeExtras = GitExtras.Write(new GitExtras { Hostname = "kanban-host", MemoryReservation = 128L << 20 }),
        ];
        foreach (var change in changes)
        {
            var changed = Kanban();
            changed.RuntimeExtras = app.RuntimeExtras;
            change(changed);
            var keys = AppService.RuntimeKeys(changed, _protector, "gitberth");
            Assert.Equal(hostname.Recreate, keys.Recreate);
            Assert.NotEqual(hostname.Live, keys.Live);
        }
    }

    [Fact]
    public void Its_settings_read_and_written_back_leave_its_fingerprint_as_it_was()
    {
        var app = Kanban();
        var settings = GitAppSettings.ToContainer(app, _protector);
        Assert.Equal(["gb-kanban-uploads", "/srv/kanban"], settings.Mounts.Select(m => m.Source));
        Assert.Equal([("NEXT_PUBLIC_SITE", false, true), ("DATABASE_URL", true, false)], settings.Env.Select(e => (e.Key, e.Secret, e.BuildTime)));
        Assert.Equal((512L << 20, 1.5), (settings.Resources.MemoryBytes, settings.Resources.Cpus));

        var copy = Kanban();
        GitAppSettings.Apply(copy, settings, Enum.GetValues<SettingField>().ToHashSet(), _protector);

        Assert.Equal(AppService.Fingerprint(app, _protector, "gitberth"), AppService.Fingerprint(copy, _protector, "gitberth"));
        Assert.Null(copy.RuntimeExtras);
        Assert.Equal(["uploads", "/srv/kanban"], copy.Volumes.Select(v => v.Source));
        Assert.Empty(GitAppSettings.Changed(settings, GitAppSettings.ToContainer(copy, _protector)));
    }

    [Fact]
    public void Every_extra_is_kept_and_comes_back_as_it_was_saved()
    {
        var app = Kanban();
        var wanted = EveryExtra(GitAppSettings.ToContainer(app, _protector));
        var fields = GitAppSettings.Changed(GitAppSettings.ToContainer(app, _protector), wanted);
        Assert.DoesNotContain(SettingField.Env, fields);
        Assert.Contains(SettingField.Labels, fields);

        GitAppSettings.Apply(app, wanted, fields, _protector);

        Assert.Empty(GitAppSettings.Changed(wanted, GitAppSettings.ToContainer(app, _protector)));
        Assert.Equal(("on-failure", 5), (app.RestartPolicy, GitAppSettings.ToContainer(app, _protector).Restart.MaximumRetryCount));
        // Canonical: written again, it's the same text; maps sorted, Gitberth's own labels left out.
        Assert.Equal(app.RuntimeExtras, GitExtras.Write(GitExtras.Parse(app.RuntimeExtras)));
        Assert.Contains("\"labels\":{\"com.example.tier\":\"front\",\"team\":\"web\"}", app.RuntimeExtras, StringComparison.Ordinal);
        Assert.DoesNotContain("gitberth.app", app.RuntimeExtras, StringComparison.Ordinal);
        Assert.StartsWith("{\"entrypoint\":[\"tini\",\"--\"],\"command\":", app.RuntimeExtras, StringComparison.Ordinal);

        // Setting them back to nothing leaves no extras at all.
        GitAppSettings.Apply(app, GitAppSettings.ToContainer(Kanban(), _protector), fields, _protector);
        Assert.Null(app.RuntimeExtras);
    }

    [Fact]
    public void Docker_run_gets_every_extra_in_one_fixed_order_after_the_classic_flags()
    {
        var app = Kanban();
        GitAppSettings.Apply(app, EveryExtra(GitAppSettings.ToContainer(app, _protector)), Enum.GetValues<SettingField>().ToHashSet(), _protector);
        var env = new List<EnvValue> { new("DATABASE_URL", "postgres://kanban:s3cret@postgres:5432/kanban", true) };
        var spec = ContainerSpecFactory.For(app, "gitberth/kanban:425df8a", env, "gitberth", ["max-size=10m"]);
        var (args, processEnv) = spec.BuildRunCommand();

        var image = args.IndexOf("gitberth/kanban:425df8a");
        var cpus = args.IndexOf("--cpus");
        Assert.Equal(["--restart", "on-failure:5"], args.Skip(args.IndexOf("--restart")).Take(2));
        Assert.Equal([
            "--memory-reservation", "256m", "--memory-swap", "1g", "--cpu-shares", "512", "--cpuset-cpus", "0-1", "--pids-limit", "200",
            "--shm-size", "128m", "--ulimit", "nofile=1024:2048", "--hostname", "kanban-host", "--domainname", "lan", "--add-host", "db.lan:10.1.1.5",
            "--dns", "10.1.1.1", "--dns-search", "lan", "--dns-option", "ndots:2", "--workdir", "/app", "--user", "1000:1000", "--entrypoint", "tini",
            "--health-cmd", "curl -f http://localhost:3000/health || exit 1", "--health-interval", "30s", "--health-timeout", "5s", "--health-retries", "3",
            "--health-start-period", "10s", "--health-start-interval", "1500ms", "--label", "com.example.tier=front", "--label", "team=web",
            "--device", "/dev/dri:/dev/dri", "--gpus", "\"device=0,1\"", "--cap-add", "NET_ADMIN", "--cap-drop", "MKNOD", "--privileged",
            "--security-opt", "no-new-privileges", "--read-only", "--init", "--sysctl", "net.core.somaxconn=1024", "--tmpfs", "/tmp/cache:size=64m",
            "--log-driver", "local", "--log-opt", "max-size=20m", "--stop-signal", "SIGINT", "--stop-timeout", "30", "-t", "-i",
        ], args.Skip(cpus + 2).Take(image - cpus - 2));
        // The entrypoint's other words, then the command, after the image.
        Assert.Equal(["--", "node", "server.js", "--password=hunter2"], args.Skip(image + 1));
        // The daemon's json-file rotation doesn't go with a driver of the app's own; the app says it's a Git app.
        Assert.DoesNotContain("max-size=10m", args);
        Assert.Contains("gitberth.source=git", args);
        // A secret still travels in the docker CLI's environment, and the command's secret-looking option is masked when shown.
        Assert.Equal("postgres://kanban:s3cret@postgres:5432/kanban", processEnv["DATABASE_URL"]);
        Assert.DoesNotContain(args, a => a.Contains("s3cret", StringComparison.Ordinal));
        var shown = spec.ToDisplayString();
        Assert.Contains("--password=••••", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", shown, StringComparison.Ordinal);
        Assert.EndsWith("\\\n  gitberth/kanban:425df8a -- node server.js --password=••••", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_without_extras_gets_no_new_flag_or_label()
    {
        var args = ContainerSpecFactory.For(Kanban(), "gitberth/kanban:425df8a", [], "gitberth").BuildRunCommand().Args;
        Assert.DoesNotContain(args, a => a.StartsWith("gitberth.source", StringComparison.Ordinal));
        Assert.Equal("gitberth/kanban:425df8a", args[^1]);
        Assert.Equal(["--memory", "512m", "--cpus", "1.5"], args.Skip(args.Count - 5).Take(4));
    }

    // ---- Saving from the unified form ----

    private async Task<int> AddAsync(AppDefinition app)
    {
        await using var db = _db.CreateDbContext();
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private async Task<AppDefinition> LoadAsync(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery().SingleAsync(a => a.Id == id);
    }

    private async Task<SettingsEdit> EditAsync(int id, Func<ContainerSettings, ContainerSettings>? container = null, Func<AppOptions, AppOptions>? options = null,
        Func<GitOptions, GitOptions>? git = null)
    {
        var app = await LoadAsync(id);
        var loaded = GitAppSettings.ToContainer(app, _protector);
        var loadedOptions = GitAppSettings.ToOptions(app, _health, _restarts);
        var loadedGit = GitAppSettings.ToGit(app);
        return new SettingsEdit(null, loaded, (container ?? (s => s))(loaded), loadedOptions, (options ?? (o => o))(loadedOptions), loadedGit, (git ?? (g => g))(loadedGit));
    }

    [Fact]
    public async Task A_live_setting_is_saved_for_docker_update_and_nothing_is_pending()
    {
        var id = await AddAsync(Kanban());

        var saved = await _apps.SaveSettingsAsync(id, await EditAsync(id, s => s with { Resources = s.Resources with { MemoryBytes = 1L << 30 }, Restart = new RestartSetting("always") }));

        Assert.Equal((false, false), (saved.RecreateNeeded, saved.RebuildNeeded));
        Assert.Equal(new HashSet<SettingField> { SettingField.Memory, SettingField.RestartPolicy }, saved.LiveChanged.ToHashSet());
        var app = await LoadAsync(id);
        Assert.Equal((1024, "always", false, false), (app.MemoryLimitMb, app.RestartPolicy, app.ConfigChangePending, app.RebuildPending));

        // When docker update fails, the caller marks it pending: the next deploy applies it.
        await _apps.MarkPendingAsync(id);
        Assert.True((await LoadAsync(id)).ConfigChangePending);
    }

    [Fact]
    public async Task A_change_only_a_new_container_takes_is_pending_and_a_build_change_waits_for_a_build()
    {
        var id = await AddAsync(Kanban());
        var secret = (await LoadAsync(id)).EnvVars.Single(v => v.Key == "DATABASE_URL").ProtectedValue;

        var env = await _apps.SaveSettingsAsync(id, await EditAsync(id, s => s with { Env = [.. s.Env, new EnvSetting("LOG_LEVEL", "debug")], Hostname = "kanban-host" }));
        Assert.Equal((true, false), (env.RecreateNeeded, env.RebuildNeeded));
        Assert.Empty(env.LiveChanged);
        var app = await LoadAsync(id);
        Assert.Equal((true, false), (app.ConfigChangePending, app.RebuildPending));
        Assert.Equal("debug", _protector.Unprotect(app.EnvVars.Single(v => v.Key == "LOG_LEVEL").ProtectedValue));
        Assert.Equal("kanban-host", GitExtras.Parse(app.RuntimeExtras)!.Hostname);
        // The secret's encrypted value isn't written again when it didn't change.
        Assert.Equal(Kanban().EnvVars.Count + 1, app.EnvVars.Count);
        Assert.Equal(secret, app.EnvVars.Single(v => v.Key == "DATABASE_URL").ProtectedValue);

        var branch = await _apps.SaveSettingsAsync(id, await EditAsync(id, git: g => g with { Branch = "next" }));
        Assert.Equal((false, true), (branch.RecreateNeeded, branch.RebuildNeeded));
        Assert.Equal(("next", true), ((await LoadAsync(id)).Branch, (await LoadAsync(id)).RebuildPending));
    }

    [Fact]
    public async Task Gitberths_own_data_saves_without_anything_pending()
    {
        var id = await AddAsync(Kanban());

        var saved = await _apps.SaveSettingsAsync(id, await EditAsync(id, options: o => o with { Name = "Kanban Lab", WebPort = 9229, Pinned = true, HealthCheckPath = "/up", GatherStats = false }));

        Assert.Equal((false, false), (saved.RecreateNeeded, saved.RebuildNeeded));
        var app = await LoadAsync(id);
        Assert.Equal(("Kanban Lab", true, "/up", false), (app.Name, app.Pinned, app.HealthCheckPath, app.ConfigChangePending));
        // The web marker moved, but only within TCP rows that exist.
        Assert.Equal(9229, PortRules.WebOf(app.Ports)!.ContainerPort);
        Assert.False(_health.IsGathered("gb-kanban"));
    }

    [Fact]
    public async Task Saving_its_settings_as_they_loaded_changes_nothing_not_even_its_last_update()
    {
        var id = await AddAsync(Kanban());
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, new DateTime(2026, 11, 6, 9, 41, 0, DateTimeKind.Utc)));
        var before = await LoadAsync(id);

        var saved = await _apps.SaveSettingsAsync(id, await EditAsync(id));

        Assert.Equal((false, false, 0, 0), (saved.RecreateNeeded, saved.RebuildNeeded, saved.LiveChanged.Count, saved.Problems.Count));
        var after = await LoadAsync(id);
        Assert.Equal((before.UpdatedAt, false, false, (string?)null), (after.UpdatedAt, after.ConfigChangePending, after.RebuildPending, after.RuntimeExtras));
        Assert.Equal(before.EnvVars.OrderBy(v => v.Id).Select(v => v.ProtectedValue), after.EnvVars.OrderBy(v => v.Id).Select(v => v.ProtectedValue));
    }

    [Fact]
    public async Task A_new_git_app_is_a_pinned_git_app_named_for_its_slug()
    {
        var id = await _apps.CreateAsync(new Gitberth.Git.GitHubRepoRef("acme", "site"), new AppSettingsModel { Name = "Site" }, null);

        var app = await LoadAsync(id);
        Assert.Equal((AppSource.Git, true, (string?)null, "gb-site", "site", UpdatePolicy.Off), (app.Source, app.Pinned, app.Container, app.ContainerName, app.NetworkName, app.UpdatePolicy));
    }

    [Fact]
    public async Task A_new_app_never_gets_the_name_of_an_existing_apps_container()
    {
        // An Existing app whose container is called gb-site keeps that name while it's missing: a new app named Site can't have it.
        await AddAsync(new AppDefinition { Name = "old site", Slug = "gb-site", Source = AppSource.Existing, Container = "gb-site" });

        Assert.Equal(new NetworkName("site-2", "site is another app's"), await _apps.ChooseSlugAsync("Site"));
    }

    [Fact]
    public async Task A_named_volume_whose_name_a_preview_would_share_with_another_app_is_refused_and_nothing_is_saved()
    {
        // orb keeps data and test-cache; its preview of the branch test, orb-test, has its copies of them.
        var orb = await AddAsync(new AppDefinition
        {
            Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb",
            Volumes = [new VolumeMount { Source = "data", Target = "/data" }, new VolumeMount { Source = "test-cache", Target = "/cache" }],
        });
        await AddAsync(new AppDefinition
        {
            Name = "orb · test", Slug = "orb-test", Owner = "acme", Repo = "orb", Branch = "test", ParentAppId = orb,
            Volumes = [new VolumeMount { Source = "data", Target = "/data" }, new VolumeMount { Source = "test-cache", Target = "/cache" }],
        });
        var before = await LoadAsync(orb);

        // test-data would be gb-orb-test-data: the preview's data.
        var own = await _apps.SaveSettingsAsync(orb, await EditAsync(orb, s => s with { Mounts = [.. s.Mounts, new MountSetting(MountKind.Volume, "test-data", "/srv/test")] }));
        var problem = Assert.Single(own.Problems);
        Assert.Equal((SettingField.Mounts, "The volume test-data would be gb-orb-test-data, which is also the preview orb · test's. Give it another name, or remove that preview first."),
            (problem.Field, problem.Message));
        // cache would be gb-orb-test-cache in the preview: production's test-cache.
        var copy = await _apps.SaveSettingsAsync(orb, await EditAsync(orb, s => s with { Mounts = [.. s.Mounts, new MountSetting(MountKind.Volume, "cache", "/var/cache")] }));
        Assert.Equal("The volume cache would be gb-orb-test-cache in the preview orb · test, which is also orb's. Give it another name, or remove that preview first.",
            Assert.Single(copy.Problems).Message);
        var after = await LoadAsync(orb);
        Assert.Equal(before.Volumes.OrderBy(v => v.Id).Select(v => (v.Source, v.Target)), after.Volumes.OrderBy(v => v.Id).Select(v => (v.Source, v.Target)));
        Assert.Equal((before.UpdatedAt, false), (after.UpdatedAt, after.ConfigChangePending));

        // The older Settings form's check says the same.
        var model = _apps.ToModel(before);
        model.Volumes.Add(new VolumeRow { Source = "test-data", Target = "/srv/test" });
        Assert.Contains("The volume test-data would be gb-orb-test-data, which is also the preview orb · test's. Give it another name, or remove that preview first.",
            await _apps.ValidateAsync(orb, model));

        // A free name saves as usual, and so do the ones it has.
        var fine = await _apps.SaveSettingsAsync(orb, await EditAsync(orb, s => s with { Mounts = [.. s.Mounts, new MountSetting(MountKind.Volume, "logs", "/logs")] }));
        Assert.Empty(fine.Problems);
        Assert.Equal(["data", "test-cache", "logs"], (await LoadAsync(orb)).Volumes.OrderBy(v => v.Id).Select(v => v.Source));
    }

    [Fact]
    public async Task Problems_save_nothing_and_another_source_isnt_saved_here()
    {
        var id = await AddAsync(Kanban());
        var before = await LoadAsync(id);

        var saved = await _apps.SaveSettingsAsync(id, await EditAsync(id, s => s with { Env = [.. s.Env, new EnvSetting("2BAD", "x")] }, o => o with { HealthCheckPath = "up" }));

        Assert.Equal(2, saved.Problems.Count);
        Assert.Contains(saved.Problems, p => p.Field == SettingField.Env && p.Message.Contains("2BAD", StringComparison.Ordinal));
        var after = await LoadAsync(id);
        Assert.Equal((before.EnvVars.Count, before.HealthCheckPath, before.UpdatedAt), (after.EnvVars.Count, after.HealthCheckPath, after.UpdatedAt));

        var existing = await AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Source = AppSource.Existing, Container = "postgres" });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _apps.SaveSettingsAsync(existing, await EditAsync(existing, s => s with { Hostname = "x" })));
    }
}
