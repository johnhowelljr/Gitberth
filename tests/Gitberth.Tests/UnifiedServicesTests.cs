using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The existing services with every container an app (8.1): the actions by key and for every source, removal and what
/// goes with it, the start's missing containers, scheduled restarts, the repo watcher, tunnel routes, the Activity tab and
/// AppSync. A Git app goes on as before; any other app's container is acted on as a container.
/// </summary>
public sealed class UnifiedServicesTests : IDisposable
{
    private readonly AppsRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static readonly Dictionary<string, string> RegistryLabels = new()
    {
        ["gitberth.managed"] = "true", ["gitberth.app"] = "whoami", [ContainerSpecFactory.SourceLabel] = "registry",
    };

    // A registry app with its container gb-whoami (made from a sample's), and its snapshot.
    private async Task<int> WhoamiAsync(string state = "running", bool container = true, DesiredState desired = DesiredState.Running)
    {
        if (container) _rig.Engine.AddLike("sample-bun-elysia", "gb-whoami", RegistryLabels, state);
        var row = await _rig.Catalog.AddAsync(new NewAppRow("whoami", "whoami", AppSource.Registry, "traefik/whoami:v1.10", null,
            new AppSnapshot(new ContainerSettings { Image = "traefik/whoami:v1.10", Ports = [new PortRule(80, "tcp", PortAccess.Lan, 8081)] },
                [new NetworkJoin("gitberth", ["whoami"])]),
            new AppOptions { Name = "whoami", WebPort = 80, SecretKeys = ["WHOAMI_TOKEN"] }));
        if (desired != DesiredState.Running)
        {
            await using var db = _rig.Db.CreateDbContext();
            await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, desired));
        }
        return row.Id;
    }

    private List<string> Writes() => _rig.Engine.Writes.ToList();

    private async Task<List<Deployment>> DeploysAsync()
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.Deployments.AsNoTracking().ToListAsync();
    }

    // ---- AppActions ----

    [Fact]
    public async Task A_container_without_a_row_is_acted_on_by_its_name()
    {
        Assert.Null(await _rig.Actions.StartAsync("sample-index"));
        Assert.Null(await _rig.Actions.StopAsync("sample-index"));
        Assert.Null(await _rig.Actions.RestartAsync("sample-index"));
        Assert.Equal(["docker start sample-index", "docker stop -t 20 sample-index", "docker restart -t 20 sample-index"], Writes());
        // Gitberth itself only restarts, as the containers list says; a name nobody has is said so.
        Assert.Contains("can't stop or remove its own container", await _rig.Actions.StopAsync("gitberth"));
        Assert.Equal("There's no container called nope.", await _rig.Actions.StartAsync("nope"));
        Assert.Equal(2, await _rig.CountAppsAsync());
    }

    [Fact]
    public async Task A_registry_apps_actions_go_to_its_container_and_never_deploy()
    {
        var id = await WhoamiAsync(state: "exited");

        Assert.Null(await _rig.Actions.StartAsync(id));
        Assert.Null(await _rig.Actions.RestartAsync("gb-whoami"));
        Assert.Null(await _rig.Actions.StopAsync(id));
        Assert.Equal(["docker start gb-whoami", "docker restart -t 20 gb-whoami", "docker stop -t 20 gb-whoami"], Writes());
        Assert.Equal(DesiredState.Stopped, (await _rig.RowAsync(a => a.Id == id))!.DesiredState);
        // Through the containers list as well: its role is an app's, whose actions dispatch on its source.
        var listed = (await _rig.Containers.FindAsync("gb-whoami"))!;
        Assert.Equal(ContainerRole.App, listed.Role);
        Assert.Null(await _rig.Containers.RunAsync(listed, ContainerAction.Start));
        Assert.Equal("docker start gb-whoami", Writes()[^1]);

        Assert.Empty(await DeploysAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Actions.DeployAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Actions.RecreateAsync(id, DeployTrigger.Manual));
        Assert.Null(await _rig.Actions.ApplyIfRunningAsync(id));
        Assert.Empty(await DeploysAsync());
    }

    [Fact]
    public async Task A_git_app_still_starts_as_before()
    {
        _rig.Engine.Remove("gb-orb");
        Assert.Null(await _rig.Actions.StartAsync("gb-orb"));
        // Missing: it's recreated from its image by a deploy, as in step 3.
        var deploy = Assert.Single(await DeploysAsync());
        Assert.Equal((DeployTrigger.Start, "gitberth/orb:1d2948c"), (deploy.Trigger, deploy.ImageTag));
        Assert.Empty(_rig.Created);
    }

    [Fact]
    public async Task A_missing_registry_app_is_created_again_from_its_snapshot()
    {
        var id = await WhoamiAsync(container: false);

        Assert.Null(await _rig.Actions.StartAsync("gb-whoami"));

        var request = Assert.Single(_rig.Created);
        Assert.Equal(("gb-whoami", "traefik/whoami:v1.10", 80, "gitberth"), (request.Name, request.Settings.Image, request.WebPort, request.Networks[0].Name));
        Assert.Equal(["WHOAMI_TOKEN"], request.SecretKeys);
        Assert.Empty(Writes());
        Assert.Empty(await DeploysAsync());

        // Without saved settings, it says so and creates nothing.
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.SpecProtected, (string?)null));
        Assert.Contains("no saved settings", await _rig.Actions.StartAsync(id));
        Assert.Single(_rig.Created);
    }

    [Fact]
    public async Task Removing_an_existing_app_removes_its_container_and_forgets_what_gitberth_kept()
    {
        var row = await _rig.Catalog.AdoptAsync("sample-bun-elysia");
        await _rig.Titles.SetAsync("sample-bun-elysia", "Elysia");
        await _rig.Restarts.SetAsync("sample-bun-elysia", new RestartInterval(6, RestartUnit.Hours));
        await _rig.Health.SetGatheredAsync("sample-bun-elysia", false);
        await _rig.AddAsync(new KeptNetwork { ContainerName = "sample-bun-elysia", Network = "media" });
        await _rig.AddAsync(new ContainerPortNote { ContainerName = "sample-bun-elysia", Port = 9229 });
        await _rig.AddAsync(new CustomDomain { AppId = row.Id, Hostname = "elysia.example.com" });
        // Domains are off here: the route stays in the tunnel until they're on again, and the warning says so.
        var warnings = await _rig.Actions.RemoveWarningsAsync("sample-bun-elysia", new RemoveOptions(Volumes: false, Image: false));
        Assert.Contains(warnings, w => w.Contains("Domains are turned off, so elysia.example.com stays in your Cloudflare Tunnel", StringComparison.Ordinal));
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        warnings = await _rig.Actions.RemoveWarningsAsync("sample-bun-elysia", new RemoveOptions(Volumes: false, Image: false));
        Assert.Contains(warnings, w => w.Contains("elysia.example.com stops answering", StringComparison.Ordinal));
        await _rig.Settings.SetAsync(SettingKeys.DomainsEnabled, null);

        var removed = await _rig.Actions.RemoveAsync("sample-bun-elysia", new RemoveOptions(Volumes: false, Image: false));

        Assert.True(removed.Ok, removed.Message);
        Assert.Equal(["docker rm -f sample-bun-elysia"], Writes());
        Assert.False(_rig.Engine.Has("sample-bun-elysia"));
        Assert.Null(await _rig.RowAsync(a => a.Id == row.Id));
        Assert.Equal((null, null, true), (_rig.Titles.For("sample-bun-elysia"), _rig.Restarts.For("sample-bun-elysia"), _rig.Health.IsGathered("sample-bun-elysia")));
        await using var db = _rig.Db.CreateDbContext();
        Assert.Empty(db.KeptNetworks);
        Assert.Empty(db.ContainerPortNotes);
        Assert.Empty(db.Domains);
    }

    [Fact]
    public async Task Removing_with_its_volumes_and_image_asks_docker_for_each()
    {
        var volumes = DockerCli.ParseContainerDetails($"[{_rig.Engine.Inspect("postgres").ToJsonString()}]")[0].Mounts.Where(m => m.Type == "volume").Select(m => m.Name!).ToList();
        Assert.NotEmpty(volumes);
        var imageId = (string)_rig.Engine.Inspect("postgres")["Image"]!;
        _rig.Engine.Failing["image rm"] ="Error response from daemon: conflict: unable to remove repository reference \"postgres:18\": container 1234 is using its referenced image";
        var warnings = await _rig.Actions.RemoveWarningsAsync("postgres", new RemoveOptions(Volumes: true, Image: true));
        Assert.Contains(warnings, w => w.Contains("is gone for good", StringComparison.Ordinal));

        var removed = await _rig.Actions.RemoveAsync("postgres", new RemoveOptions(Volumes: true, Image: true));

        Assert.True(removed.Ok);
        // DATA-4 (changed by the backend fixer): by its ID, never by a name another container's image may share.
        Assert.Equal(["docker rm -f postgres", .. volumes.Select(v => "docker volume rm " + v), "docker image rm " + imageId], Writes());
        Assert.Contains("Its image postgres:18 stays", removed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_with_its_image_leaves_an_image_another_app_keeps_for_roll_back()
    {
        // replica was updated off the image postgres runs, and keeps it to roll back to.
        var imageId = (string)_rig.Engine.Inspect("postgres")["Image"]!;
        await _rig.AddAsync(new AppDefinition { Name = "replica", Slug = "replica", Container = "replica", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = imageId });

        var removed = await _rig.Actions.RemoveAsync("postgres", new RemoveOptions(Volumes: false, Image: true));

        Assert.True(removed.Ok);
        Assert.DoesNotContain(Writes(), w => w.StartsWith("docker image rm", StringComparison.Ordinal));
        Assert.Contains("Its image postgres:18 stays: replica keeps it for Roll back.", removed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_the_container_of_an_unsettled_change_keeps_what_the_name_has_for_the_original()
    {
        // R11: postgres was recreated stopped; its new container waits for its first start, the original kept aside.
        var row = await _rig.Catalog.AdoptAsync("postgres");
        await _rig.Titles.SetAsync("postgres", "Main database");
        await _rig.Restarts.SetAsync("postgres", new RestartInterval(1, RestartUnit.Days));
        await _rig.AddAsync(new KeptNetwork { ContainerName = "postgres", Network = "gitberth-lan", IPv4Address = "10.1.1.230" });
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "postgres", Summary = "published 5432/tcp on the LAN", OldId = "0ebf31bbcd07" + new string('a', 52), NewId = (string)_rig.Engine.Inspect("postgres")["Id"]!,
            OldName = "postgres_old_0ebf31bbcd07", TempName = "postgres_new_0ebf31bbcd07", OldRestartPolicy = "{}", Step = ChangeStep.WaitingForStart,
            Outcome = ChangeOutcome.Running, StartedAt = DateTime.UtcNow,
        });

        var removed = await _rig.Actions.RemoveAsync("postgres", new RemoveOptions(Volumes: false, Image: false));

        Assert.True(removed.Ok);
        Assert.False(_rig.Engine.Has("postgres"));
        Assert.Contains("isn't settled", removed.Message, StringComparison.Ordinal);
        Assert.NotNull(await _rig.RowAsync(a => a.Id == row.Id));
        Assert.Equal(("Main database", new RestartInterval(1, RestartUnit.Days)), (_rig.Titles.For("postgres"), _rig.Restarts.For("postgres")!.Value));
        await using var db = _rig.Db.CreateDbContext();
        Assert.Equal("10.1.1.230", db.KeptNetworks.Single().IPv4Address);
    }

    [Fact]
    public async Task Removing_a_git_app_deletes_it_as_before()
    {
        var removed = await _rig.Actions.RemoveAsync("gb-orb", new RemoveOptions(Volumes: false, Image: false));

        Assert.True(removed.Ok);
        Assert.Null(await _rig.RowAsync(a => a.Id == _rig.ObsId));
        Assert.Contains("docker rm -f gb-orb", Writes());
    }

    // ---- StartupTasks ----

    [Fact]
    public async Task At_startup_a_missing_registry_app_is_created_again_and_nothing_else_is_touched()
    {
        // Its container was there before (its row knows its ID), and has gone since. (BG-1b, the backend fixer: a row that
        // never had a container is a create that never worked, and isn't made by itself.)
        var whoami = await WhoamiAsync(container: false);
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == whoami).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, "0123456789ab" + new string('c', 52)));
        await _rig.AddAsync(new AppDefinition { Name = "never", Slug = "never", Source = AppSource.Registry, SpecProtected = "x" });
        // Meant to be stopped, without saved settings, not Gitberth's to create, or in the middle of a change: left alone.
        await _rig.AddAsync(new AppDefinition { Name = "web", Slug = "web", Source = AppSource.Local, DesiredState = DesiredState.Stopped, SpecProtected = "x" });
        await _rig.AddAsync(new AppDefinition { Name = "tool", Slug = "tool", Source = AppSource.Manual });
        await _rig.AddAsync(new AppDefinition { Name = "old db", Slug = "old-db", Source = AppSource.Existing, Container = "old-db", SpecProtected = "x" });
        await _rig.AddAsync(new AppDefinition { Name = "busy", Slug = "busy", Source = AppSource.Registry, SpecProtected = "x" });
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "gb-busy", Summary = "x", OldId = "a", OldName = "gb-busy_old_a", TempName = "gb-busy_new_a", OldRestartPolicy = "{}", Step = ChangeStep.Renaming,
            Outcome = ChangeOutcome.Running, StartedAt = DateTime.UtcNow,
        });
        _rig.Engine.Remove("gb-kanban");
        var startup = new StartupTasks(_rig.Db, _rig.Cli, _rig.AppLocks, _rig.Actions, _rig.Pipeline, _rig.Options, NullLogger<StartupTasks>.Instance, new DockerEngine(_rig.Api));

        await startup.CreateMissingAsync(CancellationToken.None);

        Assert.Equal(["gb-whoami"], _rig.Created.Select(c => c.Name));
        // A Git app's missing container is the deploys' (StartupTasks' first pass), never the change engine's.
        Assert.Empty(Writes());
        Assert.All(_rig.Engine.Requests, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
    }

    // ---- RestartScheduler ----

    [Fact]
    public async Task A_registry_app_restarts_on_its_schedule_as_a_container_and_a_git_app_through_a_deploy()
    {
        await WhoamiAsync();
        await _rig.Restarts.SetAsync("gb-whoami", new RestartInterval(1, RestartUnit.Hours));
        await _rig.Restarts.SetAsync("gb-kanban", new RestartInterval(6, RestartUnit.Hours));
        var scheduler = new RestartScheduler(_rig.Db, _rig.Cli, _rig.Restarts, _rig.Actions, _rig.AppLocks, NullLogger<RestartScheduler>.Instance, _rig.ContainerLocks);

        var done = await scheduler.TickAsync(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["gb-kanban", "gb-whoami"], done);
        Assert.Equal(["docker restart -t 20 gb-whoami"], Writes());
        var deploy = Assert.Single(await DeploysAsync());
        Assert.Equal((_rig.KanbanId, DeployTrigger.Restart), (deploy.AppId, deploy.Trigger));
    }

    // ---- RepoWatcher ----

    [Fact]
    public async Task The_repo_watcher_watches_git_apps_only()
    {
        await _rig.AddAsync(new AppDefinition { Name = "whoami", Slug = "whoami", Source = AppSource.Registry, AutoDeploy = true });
        await _rig.AddAsync(new AppDefinition { Name = "paused", Slug = "paused", Owner = "acme", Repo = "paused", DesiredState = DesiredState.Stopped });
        await _rig.AddAsync(new AppDefinition { Name = "manual", Slug = "manual", Owner = "acme", Repo = "manual", AutoDeploy = false });

        await using var db = _rig.Db.CreateDbContext();
        Assert.Equal(["kanban", "orb"], RepoWatcher.Watched(db.Apps.AsNoTracking()).Select(a => a.Slug).OrderBy(s => s).ToList());
    }

    // ---- Tunnel routes and addresses ----

    [Fact]
    public void An_existing_apps_domains_and_addresses_go_by_its_containers_name()
    {
        var grafana = new AppDefinition
        {
            Id = 7, Name = "Grafana", Slug = "grafana-2", Source = AppSource.Existing, Container = "grafana",
            Ports = [new PortMapping { Id = 1, AppId = 7, ContainerPort = 3000, Access = PortAccess.Internal, IsWeb = true }],
        };
        var byName = new TunnelInfo("cloudflare-tunnel", "account", "tunnel", HostNetwork: false, OnAppNetwork: true);

        Assert.Equal("http://grafana:3000", TunnelRoutes.ServiceFor(grafana, "3000/tcp", byName));
        Assert.True(TunnelRoutes.SameService("http://grafana:3000", "http://grafana:3000/", grafana));
        // Its slug is only a key: nothing answers to it on the network.
        Assert.False(TunnelRoutes.SameService("http://grafana-2:3000", "http://grafana:3000", grafana));
        // A Git app goes by its slug, as ever.
        var kanban = new AppDefinition { Name = "kanban", Slug = "kanban", Ports = [new PortMapping { ContainerPort = 3000, Access = PortAccess.Internal, IsWeb = true }] };
        Assert.Equal("http://kanban:3000", TunnelRoutes.ServiceFor(kanban, "3000/tcp", byName));

        Assert.Equal(["http://grafana:3000"], AddressBook.ForApp(grafana, null, NetworkPortConfig.None, "10.1.1.80", domainsOn: false)
            .Addresses.Where(a => a.Kind == AddressKind.Internal).Select(a => a.Text));
    }

    // ---- ActivityLog ----

    [Fact]
    public async Task The_activity_tab_merges_entries_deploys_changes_and_events_newest_first_and_never_a_value()
    {
        var row = await _rig.Catalog.AdoptAsync("postgres");
        var at = DateTime.UtcNow;
        await _rig.AddAsync(new EnvVar { AppId = _rig.KanbanId, Key = "DATABASE_URL", ProtectedValue = _rig.Protector.Protect("postgres://kanban:s3cret-value@postgres/kanban") });
        await _rig.Activity.AddAsync("postgres", row.Id, ActivityKind.Settings, "changed POSTGRES_PASSWORD and the memory limit", ActivityTone.Good);
        await _rig.Activity.AddAsync("redis", null, ActivityKind.Settings, "another container's");
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "postgres", Summary = "published 5432/tcp on the LAN", OldId = "a", OldName = "postgres_old_a", TempName = "postgres_new_a", OldRestartPolicy = "{}",
            Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done, OfflineMs = 2400, StartedAt = at.AddMinutes(-30), FinishedAt = at.AddMinutes(-29),
        });
        await _rig.Store.AddEventsAsync([
            new ContainerEvent(at.AddMinutes(-20), "id1", "postgres", "die", ExitCode: 1),
            new ContainerEvent(at.AddMinutes(-10), "id1", "postgres", "start"),
            new ContainerEvent(at.AddMinutes(-5), "id9", "redis", "start"),
        ]);
        await _rig.AddAsync(new Deployment { AppId = _rig.KanbanId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Failed, CommitSha = "425df8a3bf54",
            CommitMessage = "Fix login\n\nlong body", QueuedAt = at.AddHours(-1), FinishedAt = at.AddMinutes(-50), Error = "build failed: DATABASE_URL=postgres://kanban:s3cret-value@x" });

        var postgres = await _rig.Activity.ForAsync("postgres", row.Id);
        Assert.Equal([ActivityKind.Settings, ActivityKind.Adopted, ActivityKind.Started, ActivityKind.Event, ActivityKind.Change], postgres.Select(i => i.Kind));
        Assert.Equal(("Crashed (exit code 1)", ActivityTone.Danger), (postgres[3].Text, postgres[3].Tone));
        Assert.Equal("Port change: published 5432/tcp on the LAN (done, offline 2.4 s)", postgres[4].Text);
        Assert.True(postgres.Zip(postgres.Skip(1)).All(p => p.First.At >= p.Second.At));

        var kanban = await _rig.Activity.ForAsync("gb-kanban", _rig.KanbanId);
        var deploy = Assert.Single(kanban, i => i.Kind == ActivityKind.Deploy);
        Assert.Equal(($"deployments/{deploy.DeploymentId}", ActivityTone.Danger), (deploy.Href, deploy.Tone));
        Assert.StartsWith($"Deploy #{deploy.DeploymentId} (New commit) failed · 425df8a Fix login", deploy.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(postgres.Concat(kanban), i => i.Text.Contains("s3cret-value", StringComparison.Ordinal));
    }

    // ---- AppSync ----

    [Fact]
    public async Task A_registry_app_recreated_outside_gitberth_is_read_again_and_said_so()
    {
        var id = await WhoamiAsync();
        await _rig.Catalog.SyncAsync(id);
        var sync = new AppSync(_rig.Events, _rig.Catalog, _rig.Db, _rig.Journal, _rig.Network, _rig.Activity, NullLogger<AppSync>.Instance);

        // Its own start: nothing to do.
        Assert.False(await sync.SeenAsync("gb-whoami", CancellationToken.None));
        var newId = _rig.Engine.Recreate("gb-whoami");
        Assert.True(await sync.SeenAsync("gb-whoami", CancellationToken.None));

        Assert.Equal(newId, (await _rig.RowAsync(a => a.Id == id))!.ContainerId);
        Assert.Contains(await _rig.Activity.ForAsync("gb-whoami", id), i => i.Kind == ActivityKind.RecreatedOutside && i.Tone == ActivityTone.Warning);
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task What_gitberth_made_itself_isnt_called_a_recreate_from_outside()
    {
        var id = await WhoamiAsync();
        await _rig.Catalog.SyncAsync(id);
        var sync = new AppSync(_rig.Events, _rig.Catalog, _rig.Db, _rig.Journal, _rig.Network, _rig.Activity, NullLogger<AppSync>.Instance);

        // Created again from its snapshot by Gitberth.
        _rig.Catalog.Creating("gb-whoami");
        _rig.Engine.Recreate("gb-whoami");
        Assert.True(await sync.SeenAsync("gb-whoami", CancellationToken.None));
        // A change's new container.
        var changed = _rig.Engine.Recreate("gb-whoami");
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "gb-whoami", Summary = "settings", OldId = "a", NewId = changed, OldName = "gb-whoami_old_a", TempName = "gb-whoami_new_a", OldRestartPolicy = "{}",
            Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done, StartedAt = DateTime.UtcNow,
        });
        Assert.True(await sync.SeenAsync("gb-whoami", CancellationToken.None));

        Assert.DoesNotContain(await _rig.Activity.ForAsync("gb-whoami", id), i => i.Kind == ActivityKind.RecreatedOutside);
        Assert.Equal(changed, (await _rig.RowAsync(a => a.Id == id))!.ContainerId);
    }

    [Fact]
    public async Task Git_apps_and_containers_without_a_row_are_left_alone()
    {
        var sync = new AppSync(_rig.Events, _rig.Catalog, _rig.Db, _rig.Journal, _rig.Network, _rig.Activity, NullLogger<AppSync>.Instance);
        _rig.Engine.Recreate("gb-kanban");
        _rig.Engine.Recreate("postgres");

        Assert.False(await sync.SeenAsync("gb-kanban", CancellationToken.None));
        Assert.False(await sync.SeenAsync("postgres", CancellationToken.None));
        Assert.Equal(2, await _rig.CountAppsAsync());
        Assert.Null((await _rig.RowAsync(a => a.Id == _rig.KanbanId))!.ContainerId);
        await using var db = _rig.Db.CreateDbContext();
        Assert.Empty(db.Activity);
    }
}
