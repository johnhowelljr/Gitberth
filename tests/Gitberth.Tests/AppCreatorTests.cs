using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Creating an app from a registry image, a local image or a paste: it comes out exactly like one deployed from GitHub
/// (gb-&lt;slug&gt;, Gitberth's labels, the shared network with its alias, unless-stopped, LAN ports on suggested host
/// ports, named volumes), and the create runs pull, row, engine, sync, Activity, in that order.
/// </summary>
public class AppCreatorTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly ImageKit.CreateSteps _steps = new();

    public AppCreatorTests() => _engine.Timeline = _steps.Timeline;

    public void Dispose() => _db.Dispose();

    private AppCreator Creator(DiskSpace? disk = null, Func<string, string>? containerFor = null) =>
        new(_steps, ImageKit.Images(_engine, _db, disk: disk), new GitberthOptions { DataDir = Path.GetTempPath(), Network = "gitberth" }, NullLogger<AppCreator>.Instance)
        {
            ContainerFor = containerFor,
        };

    // postgres runs on the host already, publishing 5432.
    private void PostgresOnTheHost()
    {
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        _steps.Taken.Add("postgres");
        _steps.Uses.Add(new HostPortUse(5432, "tcp", "", HostPortUser.Container, "postgres", "containers/postgres", 5432, HostPortState.Published, Container: "postgres"));
    }

    private void Nginx(params string[] exposed) =>
        _engine.Add(ImageKit.Image(ImageKit.Id('6'), ["nginx:1.27"], ["nginx@" + ImageKit.Digest('6')], exposed: exposed));

    [Fact]
    public async Task A_registry_image_comes_out_as_a_github_app_would()
    {
        PostgresOnTheHost();
        var creator = Creator();

        var draft = await creator.DraftFromImageAsync("postgres:18", AppSource.Registry);

        Assert.Equal(("postgres", "postgres-2", AppSource.Registry), (draft.Name, draft.Slug, draft.Source));
        Assert.Equal([new PortRule(5432, "tcp", PortAccess.Lan, 5433)], draft.Settings.Ports);
        Assert.Equal([new MountSetting(MountKind.Volume, "gb-postgres-2-postgresql", "/var/lib/postgresql")], draft.Settings.Mounts);
        var password = draft.Settings.Env.Single(e => e.Key == "POSTGRES_PASSWORD");
        Assert.Equal((false, true, 24), (password.FromImage, password.Secret, password.Value.Length));
        Assert.Contains(draft.Settings.Env, e => e is { Key: "PG_MAJOR", FromImage: true });
        Assert.Contains(draft.Issues, i => i.Text.StartsWith("Gitberth generated POSTGRES_PASSWORD and marked it secret", StringComparison.Ordinal));
        // A database's port isn't a web page.
        Assert.Null(draft.Options.WebPort);
        Assert.Equal(("postgres:18", UpdatePolicy.Notify, (IReadOnlyList<string>)["POSTGRES_PASSWORD"]), (draft.Options.TrackedImage, draft.Options.Updates, draft.Options.SecretKeys),
            new SecretsEqual());

        var plan = await creator.PlanAsync(draft);

        Assert.True(plan.CanCreate, string.Join("; ", plan.Problems.Select(p => p.Message)));
        Assert.Equal(("postgres-2", "gb-postgres-2", "postgres is taken on gitberth: it's called postgres-2"), (plan.Slug, plan.Container, plan.NameTaken));
        Assert.Equal(new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "postgres-2", ["gitberth.source"] = "registry" }, plan.Settings.Labels);
        var shared = Assert.Single(plan.Networks);
        Assert.Equal(("gitberth", "postgres-2", false), (shared.Name, Assert.Single(shared.Aliases), shared.LowPriority));
        Assert.Equal(new RestartSetting("unless-stopped"), plan.Settings.Restart);
        // The image supplies its own variables: only the container's own go in, so an update doesn't pin the old ones.
        Assert.Equal(["POSTGRES_PASSWORD"], plan.Settings.Env.Select(e => e.Key));
        Assert.Null(plan.Settings.Hostname);
        Assert.False(plan.NeedsPull);
        Assert.Equal("Creates gb-postgres-2 and starts it. Reached at 10.1.1.80:5433.", plan.Consequence);
        // The docker run it amounts to names it and its image, and never shows the password.
        Assert.Contains("gb-postgres-2", plan.DockerRun);
        Assert.Contains("postgres:18", plan.DockerRun);
        Assert.DoesNotContain(password.Value, plan.DockerRun);
    }

    private sealed class SecretsEqual : IEqualityComparer<(string?, UpdatePolicy, IReadOnlyList<string>)>
    {
        public bool Equals((string?, UpdatePolicy, IReadOnlyList<string>) x, (string?, UpdatePolicy, IReadOnlyList<string>) y) =>
            x.Item1 == y.Item1 && x.Item2 == y.Item2 && x.Item3.SequenceEqual(y.Item3);

        public int GetHashCode((string?, UpdatePolicy, IReadOnlyList<string>) obj) => 0;
    }

    [Fact]
    public async Task With_json_file_logging_it_rotates_as_github_apps_do()
    {
        Nginx("80/tcp");
        _engine.Info["LoggingDriver"] = "json-file";

        var draft = await Creator().DraftFromImageAsync("nginx:1.27", AppSource.Registry);

        Assert.Equal("", draft.Settings.Logging.Driver);
        Assert.Equal(new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "3" }, draft.Settings.Logging.Options);
        Assert.Equal(80, draft.Options.WebPort);
        var plan = await Creator().PlanAsync(draft);
        Assert.Equal("Creates gb-nginx and starts it. Reached at 10.1.1.80:80.", plan.Consequence);
    }

    [Fact]
    public async Task An_image_not_on_the_host_is_pulled_first()
    {
        var creator = Creator();

        var draft = await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry);
        var plan = await creator.PlanAsync(draft);

        Assert.Equal("nginx:1.27 isn't on this host yet: once it's pulled, its ports and volumes are filled in.", Assert.Single(draft.Issues).Text);
        Assert.True(plan.NeedsPull);
        Assert.True(plan.CanCreate);
        Assert.Equal("Pulls nginx:1.27, then creates gb-nginx and starts it. Containers on Gitberth's network reach it as nginx.", plan.Consequence);
    }

    [Fact]
    public async Task A_pull_is_refused_when_dockers_disk_is_too_full_and_nothing_is_made()
    {
        var creator = Creator(new DiskSpace(3_330_000_000, 246L << 30));
        _engine.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Downloaded newer image for nginx:1.27\"}"], () => Nginx("80/tcp"));

        var plan = await creator.PlanAsync(await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry));
        var result = await creator.Start(plan).Completion;

        Assert.False(plan.CanCreate);
        Assert.Equal("Only 3.1 GB free on Docker's disk. Free some space first.", Assert.Single(plan.Notes));
        Assert.False(result.Ok);
        Assert.Equal("Only 3.1 GB free on Docker's disk. Free some space first.", result.Message);
        Assert.Empty(_steps.Timeline);
        Assert.Empty(_steps.Rows);
    }

    [Fact]
    public async Task A_host_port_that_is_taken_is_a_problem()
    {
        PostgresOnTheHost();
        var creator = Creator();
        var draft = await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=x postgres:18"));

        var plan = await creator.PlanAsync(draft);

        Assert.False(plan.CanCreate);
        var problem = Assert.Single(plan.Problems);
        Assert.Equal(SettingField.Ports, problem.Field);
        Assert.Contains("postgres", problem.Message);
    }

    [Fact]
    public async Task Creates_in_order_pull_row_engine_then_sync_and_activity()
    {
        _engine.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Downloaded newer image for nginx:1.27\"}"], () => Nginx("80/tcp"));
        var creator = Creator();
        var draft = await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run -d --name web --network media -p 8080:80 nginx:1.27"));
        var plan = await creator.PlanAsync(draft);
        Assert.True(plan.CanCreate, string.Join("; ", plan.Problems.Select(p => p.Message)));

        var job = creator.Start(plan);
        var result = await job.Completion;

        Assert.True(result.Ok, result.Message);
        Assert.Equal(("gb-web", 1), (result.Key, result.AppId));
        Assert.Equal("Created gb-web from nginx:1.27. It answers at 10.1.1.80:5432.", result.Message);
        Assert.Equal(["pull docker.io/library/nginx:1.27", "row web", "plan-create gb-web", "engine gb-web", "remember media", "sync 1", "activity Created"], _steps.Timeline);
        Assert.Equal("Done", job.Phase);
        Assert.NotNull(job.Pull);
        Assert.NotNull(job.Engine);
        Assert.Same(job, creator.JobFor("web"));

        var row = Assert.Single(_steps.Rows);
        Assert.Equal(("web", "web", AppSource.Manual, "nginx:1.27"), (row.Name, row.Slug, row.Source, row.ImageRef));
        Assert.Equal("manual", row.Snapshot.Settings.Labels["gitberth.source"]);
        Assert.Equal(["gitberth", "media"], row.Snapshot.Networks.Select(n => n.Name));
        Assert.Equal(UpdatePolicy.Notify, row.Options.Updates);
        var request = Assert.Single(_steps.Requests);
        Assert.Equal(("gb-web", true), (request.Name, request.Start));
        Assert.Equal([("gitberth", false), ("media", true)], request.Networks.Select(n => (n.Name, n.LowPriority)));
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 8080)], request.Settings.Ports);
        Assert.Equal(("gb-web", ActivityKind.Created, "Created from nginx:1.27", ActivityTone.Good), Assert.Single(_steps.Activity));
    }

    // H4 (changed by the backend fixer, by the coordinator's decision): a create that fails removes its row as well as its
    // container, so nothing is made again by itself and a retry gets the same name; the result says why, with the last output.
    [Fact]
    public async Task A_failed_first_start_removes_the_row_and_leaves_nothing_in_docker()
    {
        Nginx("80/tcp");
        _steps.Result = new PortChangeResult(PortChangeOutcome.RolledBack, "gb-web exited with code 1 within 15 s, so Gitberth removed it.") { LastOutput = "nginx: [emerg] no config" };
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --name web -p 8080:80 nginx:1.27")));
        var job = creator.Start(plan);
        var result = await job.Completion;

        Assert.False(result.Ok);
        Assert.Equal(("gb-web exited with code 1 within 15 s, so Gitberth removed it.", (string?)null, (int?)null, "nginx: [emerg] no config"),
            (result.Message, result.Key, result.AppId, result.LastOutput));
        Assert.Equal("Failed", job.Phase);
        Assert.Equal(["row web", "plan-create gb-web", "engine gb-web", "discard 1"], _steps.Timeline);
        var row = Assert.Single(_steps.Rows);
        Assert.Equal("nginx:1.27", row.Snapshot.Settings.Image);
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 8080)], row.Snapshot.Settings.Ports);
        Assert.Empty(_steps.Activity);
    }

    [Fact]
    public async Task An_engine_that_refuses_the_create_removes_the_row_and_says_why()
    {
        Nginx("80/tcp");
        _steps.EngineBlocks = "Host port 8080 is taken by sample-web.";
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --name web -p 8080:80 nginx:1.27")));
        var result = await creator.Start(plan).Completion;

        Assert.False(result.Ok);
        Assert.Equal("gb-web wasn't created: Host port 8080 is taken by sample-web.", result.Message);
        Assert.Equal(["row web", "plan-create gb-web", "discard 1"], _steps.Timeline);
    }

    [Fact]
    public async Task A_parse_error_blocks_the_create()
    {
        Nginx("80/tcp");
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run -e API_TOKEN nginx:1.27")));

        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Problems, p => p.Message.StartsWith("Give API_TOKEN a value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Volumes_follow_the_name_the_app_ends_up_with()
    {
        Nginx("80/tcp");
        _steps.Taken.Add("web");
        var creator = Creator();

        var draft = await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --name web -v /data -v shared:/shared nginx:1.27"));
        Assert.Equal(("web-2", "gb-web-2-data"), (draft.Slug, draft.Settings.Mounts[0].Source));
        Assert.Equal("shared", draft.Settings.Mounts[1].Source);

        var renamed = await creator.PlanAsync(draft with { Name = "cache" });
        Assert.Equal(["gb-cache-data", "shared"], renamed.Settings.Mounts.Select(m => m.Source));
    }

    [Fact]
    public async Task Publish_all_lists_every_port_the_image_exposes()
    {
        Nginx("80/tcp", "443/tcp");
        _steps.Uses.Add(new HostPortUse(80, "tcp", "", HostPortUser.Container, "sample-index", null, 80, HostPortState.Published, Container: "sample-index"));

        var draft = await Creator().DraftFromParsedAsync(DockerRunParser.Parse("docker run -P nginx:1.27"));

        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 81), new PortRule(443, "tcp", PortAccess.Lan, 443)], draft.Settings.Ports);
        Assert.Contains(draft.Issues, i => i.Text.StartsWith("-P publishes every port the image exposes: 80/tcp, 443/tcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Networks_aliases_and_an_own_lan_address_go_in_the_create()
    {
        Nginx("80/tcp");
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(
            DockerRunParser.Parse("docker run --name web --network name=media,alias=www --network name=gitberth-lan,ip=10.1.1.230 nginx:1.27")));

        Assert.True(plan.CanCreate, string.Join("; ", plan.Problems.Select(p => p.Message)));
        Assert.Equal([("gitberth", "web", (string?)null), ("media", "www", null), ("gitberth-lan", "", "10.1.1.230")],
            plan.Networks.Select(n => (n.Name, n.Aliases.FirstOrDefault() ?? "", n.IPv4Address)));

        var result = await creator.Start(plan).Completion;
        Assert.True(result.Ok);
        Assert.Contains("remember gitberth-lan 10.1.1.230", _steps.Timeline);
    }

    [Fact]
    public async Task An_alias_on_the_shared_network_joins_the_apps_own()
    {
        Nginx("80/tcp");
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --name web --network-alias www nginx:1.27")));

        Assert.Equal(["web", "www"], Assert.Single(plan.Networks).Aliases);
    }

    [Fact]
    public async Task A_network_that_does_not_exist_is_a_problem()
    {
        Nginx("80/tcp");
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --network nosuch nginx:1.27")));

        Assert.Contains(plan.Problems, p => p.Message == "There's no network called nosuch: create it in Configure, or take it out.");
    }

    [Fact]
    public async Task On_the_hosts_network_it_joins_none()
    {
        Nginx("80/tcp");
        var creator = Creator();

        var plan = await creator.PlanAsync(await creator.DraftFromParsedAsync(DockerRunParser.Parse("docker run --name tunnel --network host nginx:1.27")));

        Assert.Empty(plan.Networks);
        Assert.Equal("host", plan.Settings.NetworkMode);
        Assert.Equal("Creates gb-tunnel and starts it. It runs on the host's network.", plan.Consequence);
    }

    [Fact]
    public async Task A_local_image_without_a_tag_runs_by_its_id_and_is_never_updated()
    {
        var id = ImageKit.Id('7');
        _engine.Add(ImageKit.Image(id, [], exposed: ["3000/tcp"]));
        var creator = Creator();

        var draft = await creator.DraftFromImageAsync(id, AppSource.Local);
        var plan = await creator.PlanAsync(draft with { Name = "tool" });
        var result = await creator.Start(plan).Completion;

        Assert.True(result.Ok, result.Message);
        Assert.Contains(draft.Issues, i => i.Text.StartsWith("It runs the image 777777777777 by its ID", StringComparison.Ordinal));
        Assert.False(plan.NeedsPull);
        Assert.Equal("local", plan.Settings.Labels["gitberth.source"]);
        Assert.Equal((id, UpdatePolicy.Off), (_steps.Rows[0].ImageRef, _steps.Rows[0].Options.Updates));
    }

    [Fact]
    public async Task The_engine_test_names_its_containers_itself()
    {
        Nginx("80/tcp");
        var creator = Creator(containerFor: slug => $"gbtest-uc-1234-{slug}");

        var plan = await creator.PlanAsync(await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry));
        await creator.Start(plan).Completion;

        Assert.Equal("gbtest-uc-1234-nginx", plan.Container);
        Assert.Equal("gbtest-uc-1234-nginx", Assert.Single(_steps.Requests).Name);
        Assert.Equal("nginx", plan.Settings.Labels["gitberth.app"]);
    }

    [Fact]
    public void Hints_fill_in_what_a_database_needs_and_nothing_else()
    {
        var (postgres, notes) = ImageHints.For("postgres:18", []);
        Assert.Equal("POSTGRES_PASSWORD", Assert.Single(postgres).Key);
        Assert.Single(notes);

        Assert.Empty(ImageHints.For("postgres:18", [new EnvSetting("POSTGRES_PASSWORD", "mine")]).Notes);
        Assert.Empty(ImageHints.For("postgres:18", [new EnvSetting("POSTGRES_HOST_AUTH_METHOD", "trust")]).Notes);
        Assert.Equal("MYSQL_ROOT_PASSWORD", Assert.Single(ImageHints.For("mysql:8", []).Env).Key);
        Assert.Equal("MARIADB_ROOT_PASSWORD", Assert.Single(ImageHints.For("mariadb:11", []).Env).Key);
        Assert.Empty(ImageHints.For("mariadb:11", [new EnvSetting("MYSQL_ROOT_PASSWORD", "x")]).Notes);
        Assert.Empty(ImageHints.For("nginx:1.27", []).Env);
        Assert.NotEqual(ImageHints.Password(), ImageHints.Password());
        Assert.Matches("^[A-HJ-NP-Za-km-z2-9]{24}$", ImageHints.Password());
    }
}
