using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Every container is an app (1.3, 1.4): listed live from CT 101's containers, with or without a row, helpers left out and
/// temporaries folded into their app; adopted, without a Docker write, the first time Gitberth stores something about it.
/// </summary>
public sealed class AppCatalogTests : IDisposable
{
    private readonly AppsRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private Task<AppList> ListAsync() => _rig.Catalog.ListAsync("10.1.1.80");

    private static AppSummary Of(AppList list, string key) => Assert.Single(list.Apps, a => a.Key == key);

    [Fact]
    public async Task Every_container_is_listed_as_an_app_and_obs_and_kanban_come_first()
    {
        var list = await ListAsync();

        Assert.Null(list.DockerError);
        Assert.Equal(["gb-kanban", "gb-orb", "cloudflare-tunnel", "gitberth", "postgres", "sample-bun-elysia", "sample-bun-hono", "sample-index"],
            list.Apps.Select(a => a.Key));
        var kanban = Of(list, "gb-kanban");
        Assert.Equal(("Kanban", AppSource.Git, ContainerRole.App, ImageOrigin.Built, AppState.Running, true, false, "acme/kanban · main · 425df8a"),
            (kanban.Title, kanban.Source, kanban.Role, kanban.Origin, kanban.State, kanban.Pinned, kanban.System, kanban.Repo));
        Assert.Equal(_rig.KanbanId, kanban.AppId);
        Assert.Equal("apps/gb-kanban", kanban.Href);
        Assert.Contains(kanban.Chips, c => c.Label == "3001:3000");

        // Without a row, a container is an app all the same: source Existing, its own name, its state and origin (local until
        // a registry is known to have its tag: RepoDigests are every image's in the containerd store).
        var postgres = Of(list, "postgres");
        Assert.Equal((null, "postgres", AppSource.Existing, ContainerRole.Database, ImageOrigin.Local, AppState.Crashed, "Exited (128)", false),
            (postgres.AppId, postgres.Title, postgres.Source, postgres.Role, postgres.Origin, postgres.State, postgres.StateText, postgres.Pinned));
        Assert.Equal((ImageOrigin.Local, AppState.Stopped), (Of(list, "sample-bun-hono").Origin, Of(list, "sample-bun-hono").State));
        Assert.Equal((ContainerRole.Self, true), (Of(list, "gitberth").Role, Of(list, "gitberth").System));
        Assert.Equal((ContainerRole.Tunnel, true), (Of(list, "cloudflare-tunnel").Role, Of(list, "cloudflare-tunnel").System));
    }

    [Fact]
    public async Task Listing_writes_nothing_to_docker_or_the_database()
    {
        var apps = await _rig.CountAppsAsync();

        await ListAsync();
        await _rig.Catalog.GetAsync("postgres", "10.1.1.80");
        await _rig.Catalog.GetAsync("gb-orb", "10.1.1.80");

        Assert.All(_rig.Engine.Requests, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
        Assert.Empty(_rig.Engine.Writes);
        Assert.Equal(apps, await _rig.CountAppsAsync());
    }

    [Fact]
    public async Task Helpers_are_left_out_and_temporaries_are_folded_into_their_app()
    {
        _rig.Engine.AddLike("sample-index", "gitberth-helper-ports-1a2b3c4d5e6f", new Dictionary<string, string> { [HostScanHelper.Label] = "ports" });
        // A deploy's new version next to kanban, and its previous container kept aside.
        _rig.Engine.AddLike("gb-kanban", "gb-kanban_next", new Dictionary<string, string> { [ContainerSpecFactory.BridgeLabel] = "true" });
        _rig.Engine.AddLike("gb-kanban", "gb-kanban_prev", state: "exited");
        await _rig.AddAsync(new Deployment { AppId = _rig.KanbanId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building });
        // A change of sample-index waiting for the owner: its original is kept aside under its old name.
        var oldId = (string)_rig.Engine.Inspect("sample-index")["Id"]!;
        _rig.Engine.AddLike("sample-index", PortChanger.OldNameFor("sample-index", oldId), state: "exited");
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "sample-index", Summary = "published 80/tcp on the LAN", OldId = oldId, NewId = "new", OldName = PortChanger.OldNameFor("sample-index", oldId),
            TempName = PortChanger.TempNameFor("sample-index", oldId), OldRestartPolicy = "{}", Step = ChangeStep.Finished, Outcome = ChangeOutcome.NeedsDecision, StartedAt = DateTime.UtcNow,
        });
        // One under way on sample-bun-hono: mid-swap, only its temporary name is there.
        var honoId = (string)_rig.Engine.Inspect("sample-bun-hono")["Id"]!;
        _rig.Engine.AddLike("sample-bun-hono", PortChanger.OldNameFor("sample-bun-hono", honoId), state: "exited");
        _rig.Engine.Remove("sample-bun-hono");
        await _rig.AddAsync(new ContainerChangeEntry
        {
            ContainerName = "sample-bun-hono", Summary = "settings", OldId = honoId, OldName = PortChanger.OldNameFor("sample-bun-hono", honoId),
            TempName = PortChanger.TempNameFor("sample-bun-hono", honoId), OldRestartPolicy = "{}", Step = ChangeStep.Renaming, Outcome = ChangeOutcome.Running, StartedAt = DateTime.UtcNow,
        });

        var list = await ListAsync();

        Assert.Equal(["gb-kanban", "gb-orb", "cloudflare-tunnel", "gitberth", "postgres", "sample-bun-elysia", "sample-bun-hono", "sample-index"], list.Apps.Select(a => a.Key));
        Assert.Equal((AppState.Deploying, "Building…"), (Of(list, "gb-kanban").State, Of(list, "gb-kanban").StateText));
        Assert.Equal(AppState.NeedsDecision, Of(list, "sample-index").State);
        // The app stands for the change while its name has no container.
        Assert.Equal((AppState.Changing, (string?)_rig.Engine.Inspect(PortChanger.OldNameFor("sample-bun-hono", honoId))["Id"]!.GetValue<string>()),
            (Of(list, "sample-bun-hono").State, Of(list, "sample-bun-hono").ContainerId));
    }

    [Fact]
    public async Task Rows_without_a_container_say_why()
    {
        await _rig.AddAsync(new AppDefinition { Name = "new site", Slug = "new-site", Owner = "acme", Repo = "site" });
        await _rig.AddAsync(new AppDefinition { Name = "whoami", Slug = "whoami", Source = AppSource.Registry, ImageRef = "traefik/whoami:v1.10" });
        await _rig.AddAsync(new AppDefinition { Name = "cache", Slug = "cache", Source = AppSource.Registry, ImageRef = "redis:7", ContainerId = "c".PadRight(64, 'c') });
        await _rig.AddAsync(new AppDefinition { Name = "old db", Slug = "old-db", Source = AppSource.Existing, Container = "old-db", ImageRef = "postgres:17" });
        _rig.Engine.Remove("gb-orb");

        var list = await ListAsync();

        Assert.Equal((AppState.NotCreated, "Not deployed"), (Of(list, "gb-new-site").State, Of(list, "gb-new-site").StateText));
        Assert.Equal((AppState.NotCreated, "Not created"), (Of(list, "gb-whoami").State, Of(list, "gb-whoami").StateText));
        Assert.Equal(AppState.Missing, Of(list, "gb-cache").State);
        Assert.Equal((AppState.Missing, AppSource.Existing, ContainerRole.Database, "postgres:17"),
            (Of(list, "old-db").State, Of(list, "old-db").Source, Of(list, "old-db").Role, Of(list, "old-db").Image));
        Assert.Equal((AppState.Missing, "Container missing"), (Of(list, "gb-orb").State, Of(list, "gb-orb").StateText));
    }

    [Fact]
    public async Task One_app_by_its_key_or_an_old_numeric_id()
    {
        var orb = await _rig.Catalog.GetAsync(_rig.ObsId.ToString(System.Globalization.CultureInfo.InvariantCulture), "10.1.1.80");
        Assert.NotNull(orb);
        Assert.Equal(("gb-orb", "orb", AppSource.Git), (orb.Summary.Key, orb.Row!.Name, orb.Summary.Source));
        Assert.NotNull(orb.Git);
        Assert.NotNull(orb.Details);
        Assert.NotNull(orb.Inspect);
        Assert.Equal(80, orb.AppAddresses!.WebPort);

        var postgres = await _rig.Catalog.GetAsync("postgres", "10.1.1.80");
        Assert.NotNull(postgres);
        Assert.Null(postgres.Row);
        Assert.Null(postgres.Git);
        Assert.Equal(("postgres", "postgres:18"), (postgres.Details!.Name, postgres.Details.Image));
        Assert.NotNull(postgres.Summary.StartedAt);
        Assert.Equal("postgres", postgres.Addresses!.Container);

        Assert.Null(await _rig.Catalog.GetAsync("nope", "10.1.1.80"));
        Assert.Equal("gb-kanban", await _rig.Catalog.KeyForIdAsync(_rig.KanbanId));
    }

    [Fact]
    public async Task Adopting_makes_one_row_from_the_container_and_writes_nothing_to_docker()
    {
        await _rig.Titles.SetAsync("postgres", "Main database");
        var changed = 0;
        _rig.Catalog.Changed += () => Interlocked.Increment(ref changed);

        // Twice at once, then again: one row.
        var rows = await Task.WhenAll(_rig.Catalog.AdoptAsync("postgres"), _rig.Catalog.AdoptAsync("postgres"));
        var again = await _rig.Catalog.AdoptAsync("postgres");

        Assert.Equal(rows[0].Id, rows[1].Id);
        Assert.Equal(rows[0].Id, again.Id);
        Assert.Equal(1, changed);
        var row = (await _rig.RowAsync(a => a.Container == "postgres"))!;
        Assert.Equal(("Main database", "postgres", AppSource.Existing, "postgres", "postgres:18", UpdatePolicy.Notify, false, false),
            (row.Name, row.Slug, row.Source, row.ContainerName, row.ImageRef, row.UpdatePolicy, row.AutoDeploy, row.Pinned));
        Assert.Equal((string)_rig.Engine.Inspect("postgres")["Id"]!, row.ContainerId);
        Assert.Equal("postgres", row.NetworkName);
        // Its ports as a mirror: 5432 is only exposed, and a database's port isn't a web one.
        Assert.Equal([(5432, PortAccess.Internal, (int?)null, false)], row.Ports.Select(p => (p.ContainerPort, p.Access, p.HostPort, p.IsWeb)));
        // The settings to create it again are kept, encrypted.
        Assert.DoesNotContain("POSTGRES", row.SpecProtected!, StringComparison.Ordinal);
        var snapshot = await _rig.Catalog.SnapshotAsync(row.Id);
        Assert.Equal("postgres:18", snapshot!.Settings.Image);
        Assert.Equal("gitberth", snapshot.Networks[0].Name);
        Assert.Equal(1, await _rig.CountAppsAsync() - 2);
        Assert.Contains(await _rig.Activity.ForAsync("postgres", row.Id), a => a.Kind == ActivityKind.Adopted);
        Assert.All(_rig.Engine.Requests, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
        Assert.Empty(_rig.Engine.Writes);

        // Listed as the row now, still under its own name.
        var listed = Of(await ListAsync(), "postgres");
        Assert.Equal((row.Id, "Main database", AppSource.Existing), (listed.AppId, listed.Title, listed.Source));
    }

    [Fact]
    public async Task A_web_app_is_adopted_with_its_web_port_and_names_that_slug_into_one_get_their_own()
    {
        _rig.Engine.AddLike("sample-bun-elysia", "my_app");
        _rig.Engine.AddLike("sample-bun-elysia", "my.app");

        var first = await _rig.Catalog.AdoptAsync("my_app");
        var second = await _rig.Catalog.AdoptAsync("my.app");

        Assert.Equal(("my-app", "my-app-2"), (first.Slug, second.Slug));
        var web = Assert.Single(first.Ports, p => p.IsWeb);
        Assert.Equal("tcp", web.Protocol);
        // Its image is a tag built here: that's what its updates follow.
        Assert.Equal(ContainerPorts.Str(_rig.Engine.Inspect("my_app")["Config"], "Image"), first.ImageRef);
    }

    [Fact]
    public async Task Helpers_temporaries_and_unknown_names_are_never_adopted()
    {
        _rig.Engine.AddLike("sample-index", "gitberth-helper-ports-1a2b3c4d5e6f", new Dictionary<string, string> { [HostScanHelper.Label] = "ports" });
        _rig.Engine.AddLike("gb-kanban", "gb-kanban_next", new Dictionary<string, string> { [ContainerSpecFactory.BridgeLabel] = "true" });
        _rig.Engine.AddLike("gb-kanban", "gb-kanban_prev", state: "exited");

        foreach (var name in new[] { "gitberth-helper-ports-1a2b3c4d5e6f", "gb-kanban_next", "gb-kanban_prev", "nope" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Catalog.AdoptAsync(name));
        Assert.Equal(2, await _rig.CountAppsAsync());
        // A Git app's container already has its row.
        Assert.Equal(_rig.KanbanId, (await _rig.Catalog.AdoptAsync("gb-kanban")).Id);
    }

    [Fact]
    public async Task A_container_whose_tag_moved_on_shows_the_reference_it_was_created_with()
    {
        // whoami:latest was pulled again since it was created: Docker's list names its image by the ID now.
        _rig.Engine.AddLike("sample-index", "whoami", configImage: "traefik/whoami:latest", listedImage: "sha256:" + new string('7', 64));

        var list = await ListAsync();

        Assert.Equal(("traefik/whoami:latest", true), (Of(list, "whoami").Image, Of(list, "whoami").TagMoved));
        Assert.Equal(("nginx:stable-alpine", false), (Of(list, "sample-index").Image, Of(list, "sample-index").TagMoved));
        Assert.Equal("traefik/whoami:latest", (await _rig.Catalog.GetAsync("whoami", "192.168.50.10"))!.Summary.Image);
    }

    [Fact]
    public async Task A_rolled_back_app_shows_the_tag_it_follows_not_the_digest_it_runs()
    {
        // whoami follows traefik/whoami:latest; a roll back made its container again from the older image's digest.
        _rig.Engine.AddLike("sample-index", "whoami", configImage: "traefik/whoami:latest");
        await _rig.Catalog.AdoptAsync("whoami");
        _rig.Engine.Remove("whoami");
        var digest = "traefik/whoami@sha256:" + new string('6', 64);
        _rig.Engine.AddLike("sample-index", "whoami", configImage: digest, listedImage: digest);

        var whoami = Of(await ListAsync(), "whoami");

        Assert.Equal(("traefik/whoami:latest", true), (whoami.Image, whoami.TagMoved));
    }

    [Fact]
    public async Task An_app_set_to_when_i_ask_has_no_badge_in_the_list()
    {
        // Docker Hub has a newer postgres:18 than the image postgres runs.
        using var kitDb = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        engine.Add(ImageKit.Image(ContainerPorts.Str(_rig.Engine.Inspect("postgres"), "Image"), ["postgres:18"], ["postgres@" + ImageKit.Digest('1')]));
        _rig.Updates = new UpdateService(new ImageKit.UpdateSteps(kitDb),
            new ImageService(engine, new RegistryService(_rig.Db, _rig.Protector, engine), _rig.Options, new ImageKit.NoHttp(), NullLogger<ImageService>.Instance, _rig.Db),
            _rig.Db, NullLogger<UpdateService>.Instance);
        await _rig.AddAsync(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = ImageKit.Digest('2'), CheckedAt = DateTime.UtcNow });
        Assert.True(Of(await ListAsync(), "postgres").Update?.Available);

        await _rig.Catalog.SaveOptionsAsync("postgres", (await _rig.Catalog.OptionsAsync("postgres")) with { Updates = UpdatePolicy.Off });

        // Nothing in the list (and so nothing on the dashboard's Updates tile), whatever the check of its reference found.
        Assert.Null(Of(await ListAsync(), "postgres").Update);
    }

    [Fact]
    public async Task Without_update_badges_the_list_is_the_same()
    {
        // The rig's UpdateService fails, as a broken one would: no badges, never an error.
        var list = await ListAsync();
        Assert.Null(list.DockerError);
        Assert.All(list.Apps, a => Assert.Null(a.Update));
    }

    [Fact]
    public async Task Gitberths_own_data_adopts_only_when_it_needs_a_row()
    {
        var options = await _rig.Catalog.OptionsAsync("sample-index");
        Assert.Equal(("sample-index", true, UpdatePolicy.Notify), (options.Name, options.GatherStats, options.Updates));

        // A title, stats and a scheduled restart are kept by the container's name.
        await _rig.Catalog.SaveOptionsAsync("sample-index", options with { Name = "Index page", GatherStats = false, ScheduledRestart = new RestartInterval(6, RestartUnit.Hours) });
        Assert.Equal(2, await _rig.CountAppsAsync());
        Assert.Equal(("Index page", false, new RestartInterval(6, RestartUnit.Hours)),
            (_rig.Titles.For("sample-index"), _rig.Health.IsGathered("sample-index"), _rig.Restarts.For("sample-index")!.Value));

        // A health URL is the row's: it's adopted first; its name stays its title too.
        var saved = await _rig.Catalog.SaveOptionsAsync("sample-index", (await _rig.Catalog.OptionsAsync("sample-index")) with { HealthCheckPath = "/health", Name = "Index" });
        var row = (await _rig.RowAsync(a => a.Container == "sample-index"))!;
        Assert.Equal(("Index", "/health", "Index"), (row.Name, row.HealthCheckPath, _rig.Titles.For("sample-index")));
        Assert.Equal(("Index", "/health"), (saved.Name, saved.HealthCheckPath));

        // A Git app's name is its row's only; its updates are its deploys.
        await _rig.Catalog.SaveOptionsAsync("gb-orb", (await _rig.Catalog.OptionsAsync("gb-orb")) with { Name = "Object State", Updates = UpdatePolicy.Auto, Pinned = false });
        var orb = (await _rig.RowAsync(a => a.Id == _rig.ObsId))!;
        Assert.Equal(("Object State", UpdatePolicy.Off, false), (orb.Name, orb.UpdatePolicy, orb.Pinned));
        Assert.Null(_rig.Titles.For("gb-orb"));
        var named = await _rig.Catalog.OptionsAsync("gb-orb");
        await Assert.ThrowsAsync<ArgumentException>(() => _rig.Catalog.SaveOptionsAsync("gb-orb", named with { Name = " " }));
    }

    [Fact]
    public async Task A_git_apps_secret_switch_marks_its_variable_secret_for_its_deploys()
    {
        await using (var db = _rig.Db.CreateDbContext())
        {
            db.EnvVars.AddRange(
                new EnvVar { AppId = _rig.ObsId, Key = "API_HOST", ProtectedValue = _rig.Protector.Protect("api.internal") },
                new EnvVar { AppId = _rig.ObsId, Key = "DATABASE_URL", ProtectedValue = _rig.Protector.Protect("postgres://orb:hunter2@postgres/orb") });
            await db.SaveChangesAsync();
        }

        await _rig.Catalog.SaveOptionsAsync("gb-orb", (await _rig.Catalog.OptionsAsync("gb-orb")) with { SecretKeys = ["API_HOST"] });
        await using (var db = _rig.Db.CreateDbContext())
        {
            var secret = db.EnvVars.Where(v => v.AppId == _rig.ObsId).ToDictionary(v => v.Key, v => v.IsSecret);
            Assert.Equal((true, true), (secret["API_HOST"], secret["DATABASE_URL"]));
        }

        // Switched off again; a URL with a password stays secret whatever the switch says.
        await _rig.Catalog.SaveOptionsAsync("gb-orb", (await _rig.Catalog.OptionsAsync("gb-orb")) with { SecretKeys = [] });
        await using (var db = _rig.Db.CreateDbContext())
        {
            var secret = db.EnvVars.Where(v => v.AppId == _rig.ObsId).ToDictionary(v => v.Key, v => v.IsSecret);
            Assert.Equal((false, true), (secret["API_HOST"], secret["DATABASE_URL"]));
        }
    }

    [Fact]
    public async Task A_new_apps_row_holds_its_snapshot_ports_and_networks()
    {
        var settings = new ContainerSettings
        {
            Image = "traefik/whoami:v1.10",
            Ports = [new PortRule(80, "tcp", PortAccess.Lan, 8081), new PortRule(9000, "tcp", PortAccess.Internal)],
            Env = [new EnvSetting("WHOAMI_NAME", "whoami-lab-name", Secret: true)],
        };
        var snapshot = new AppSnapshot(settings, [new NetworkJoin("gitberth", ["whoami"]), new NetworkJoin("media", ["whoami", "who"], LowPriority: true),
            new NetworkJoin("gitberth-lan", ["whoami"], "10.1.1.230", LowPriority: true)]);

        var row = await _rig.Catalog.AddAsync(new NewAppRow("whoami", "whoami", AppSource.Registry, "traefik/whoami:v1.10", null, snapshot,
            new AppOptions { Name = "whoami", WebPort = 80, GatherStats = false, Pinned = true }));

        var saved = (await _rig.RowAsync(a => a.Id == row.Id))!;
        Assert.Equal(("gb-whoami", AppSource.Registry, false, DesiredState.Running, true, UpdatePolicy.Notify), (saved.ContainerName, saved.Source, saved.AutoDeploy, saved.DesiredState, saved.Pinned, saved.UpdatePolicy));
        Assert.Equal([(80, PortAccess.Lan, (int?)8081, true), (9000, PortAccess.Internal, null, false)], saved.Ports.OrderBy(p => p.Id).Select(p => (p.ContainerPort, p.Access, p.HostPort, p.IsWeb)));
        Assert.Equal([("media", "who", (string?)null), ("gitberth-lan", null, "10.1.1.230")], saved.Networks.OrderBy(n => n.Id).Select(n => (n.Network, n.Alias, n.IPv4Address)));
        Assert.False(_rig.Health.IsGathered("gb-whoami"));
        var back = await _rig.Catalog.SnapshotAsync(row.Id);
        Assert.Equal([("WHOAMI_NAME", "whoami-lab-name", true)], back!.Settings.Env.Select(e => (e.Key, e.Value, e.Secret)));
        Assert.Equal(["gitberth", "media", "gitberth-lan"], back.Networks.Select(n => n.Name));
        Assert.DoesNotContain("whoami-lab-name", saved.SpecProtected!, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => _rig.Catalog.AddAsync(new NewAppRow("x", "x", AppSource.Existing, null, null, snapshot, new AppOptions())));
    }

    [Fact]
    public async Task Syncing_keeps_the_web_marker_on_its_port_and_takes_the_new_id()
    {
        var row = await _rig.Catalog.AdoptAsync("sample-bun-elysia");
        var web = Assert.Single(row.Ports, p => p.IsWeb).ContainerPort;
        var id = _rig.Engine.Recreate("sample-bun-elysia");

        await _rig.Catalog.SyncAsync(row.Id);

        var synced = (await _rig.RowAsync(a => a.Id == row.Id))!;
        Assert.Equal(id, synced.ContainerId);
        Assert.Equal(web, Assert.Single(synced.Ports, p => p.IsWeb).ContainerPort);
        // A Git app's row is the truth for its settings: syncing leaves it alone.
        var before = (await _rig.RowAsync(a => a.Id == _rig.ObsId))!;
        await _rig.Catalog.SyncAsync(_rig.ObsId);
        Assert.Equal((before.ContainerId, before.UpdatedAt), ((await _rig.RowAsync(a => a.Id == _rig.ObsId))!.ContainerId, (await _rig.RowAsync(a => a.Id == _rig.ObsId))!.UpdatedAt));
    }

    [Fact]
    public async Task Forgetting_an_app_removes_its_row_and_domains_and_leaves_its_container()
    {
        var row = await _rig.Catalog.AdoptAsync("sample-index");
        await _rig.AddAsync(new CustomDomain { AppId = row.Id, Hostname = "index.example.com" });

        await _rig.Catalog.ForgetAsync(row.Id);

        Assert.Null(await _rig.RowAsync(a => a.Id == row.Id));
        await using (var db = _rig.Db.CreateDbContext()) Assert.Empty(db.Domains.Where(d => d.AppId == row.Id));
        Assert.True(_rig.Engine.Has("sample-index"));
        Assert.Empty(_rig.Engine.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Catalog.ForgetAsync(_rig.ObsId));
    }

    [Theory]
    [InlineData("postgres:18", true)]
    [InlineData("nginx", true)]
    [InlineData("registry.lan:5000/team/app:1.2", true)]
    [InlineData("postgres@sha256:0123456789abcdef", false)]
    [InlineData("sha256:3a4264520e12", false)]
    [InlineData("3a4264520e12aaaa3a4264520e12aaaa3a4264520e12aaaa3a4264520e12aaaa", false)]
    [InlineData("", false)]
    public void A_tag_is_followed_and_a_digest_or_an_id_is_pinned(string reference, bool tag) => Assert.Equal(tag, AppCatalog.IsTag(reference));
}
