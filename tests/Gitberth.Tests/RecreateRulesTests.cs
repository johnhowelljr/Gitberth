using System.Text.Json.Nodes;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>Which containers Gitberth recreates to change their ports, and the one line it shows for each that it doesn't (section 7.3).</summary>
public class RecreateRulesTests
{
    private static RecreateContext Context(IReadOnlyList<JsonObject>? others = null, string? self = null, string? busy = null, EngineVersion? engine = null) =>
        new(engine ?? new EngineVersion("29.9.2", "1.56"), self, others ?? [], DockerFixtures.Json("networks-json.json").AsArray(), "10.1.1.80", busy);

    private static RecreateVerdict Classify(JsonObject? inspect, RecreateContext? context = null, string name = "postgres") =>
        RecreateRules.Classify(inspect is null ? name : ((string)inspect["Name"]!).TrimStart('/'), inspect, context ?? Context());

    private static JsonObject With(JsonObject inspect, Action<JsonObject> change)
    {
        change(inspect);
        return inspect;
    }

    private static JsonObject Labelled(string container, params (string Key, string Value)[] labels) =>
        With(DockerFixtures.Inspect(container), i =>
        {
            foreach (var (key, value) in labels) i["Config"]!["Labels"]![key] = value;
        });

    // Another container, by name, with a setting of its HostConfig.
    private static JsonObject Other(string name, string field, JsonNode value) =>
        With(DockerFixtures.Inspect("sample-bun-hono"), i =>
        {
            i["Name"] = "/" + name;
            i["Id"] = "c0ffee" + new string('0', 58);
            i["HostConfig"]![field] = value;
        });

    [Fact]
    public void Allows_a_container_gitberth_didnt_deploy()
    {
        Assert.Equal(RecreateVerdict.Yes, Classify(DockerFixtures.Inspect("postgres")));
        Assert.Equal(RecreateVerdict.Yes, Classify(DockerFixtures.Inspect("sample-index")));
    }

    [Fact]
    public void Says_when_there_is_no_such_container() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Missing, "There's no container called pg-old."), Classify(null, name: "pg-old"));

    [Fact]
    public void Refuses_gitberth_itself_and_points_at_its_compose_file()
    {
        var verdict = Classify(DockerFixtures.Inspect("gitberth"));

        Assert.Equal(RecreateKind.Self, verdict.Kind);
        Assert.Equal("Gitberth's own container. Its ports are set in /opt/gitberth/docker-compose.yml.", verdict.Reason);
        Assert.Equal("docker compose -p gitberth -f /opt/gitberth/docker-compose.yml up -d gitberth", verdict.Compose!.Command);

        // Started some other way, it knows itself by its hostname, the short ID; or by its ID, read from its own mounts,
        // whatever its hostname.
        var plain = With(DockerFixtures.Inspect("gitberth"), i => i["Config"]!["Labels"] = new JsonObject());
        Assert.Equal(new RecreateVerdict(RecreateKind.Self, "Gitberth's own container: it can't recreate itself."), Classify(plain, Context(self: "455b8ea31b6d")));
        Assert.Equal(RecreateKind.Self, Classify(plain, Context(self: (string)plain["Id"]!)).Kind);
        Assert.Equal(RecreateKind.Allowed, Classify(plain, Context(self: "web-1")).Kind);
    }

    [Fact]
    public void The_hosts_network_has_no_ports_to_change_but_its_settings_change()
    {
        var tunnel = DockerFixtures.Inspect("cloudflare-tunnel");

        Assert.Equal(RecreateKind.HostNetwork, Classify(tunnel).Kind);
        Assert.Equal(RecreateKind.HostNetwork, RecreateRules.Classify("cloudflare-tunnel", tunnel, Context(), ChangeScope.Ports).Kind);
        Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("cloudflare-tunnel", tunnel, Context(), ChangeScope.Settings));
        foreach (var mode in new[] { "none", "container:" + new string('a', 64) })
        {
            var other = With(DockerFixtures.Inspect("sample-index"), i => i["HostConfig"]!["NetworkMode"] = mode);
            Assert.NotEqual(RecreateKind.Allowed, Classify(other).Kind);
            Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("sample-index", other, Context(), ChangeScope.Settings));
        }
    }

    [Fact]
    public void A_git_apps_container_is_the_apps_and_an_image_apps_is_like_any_other()
    {
        // orb and kanban have no source label: they're Git apps, whose settings go through a deploy, in either scope.
        var kanban = DockerFixtures.Inspect("gb-kanban");
        Assert.Equal(RecreateKind.App, RecreateRules.Classify("gb-kanban", kanban, Context(), ChangeScope.Settings).Kind);
        Assert.Equal(RecreateKind.App, RecreateRules.Classify("gb-kanban", Labelled("gb-kanban", ("gitberth.source", "git")), Context(), ChangeScope.Settings).Kind);
        // One Gitberth created from an image is recreated like any container.
        foreach (var source in new[] { "registry", "local", "manual" })
        {
            var app = Labelled("gb-kanban", ("gitberth.source", source));
            Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("gb-kanban", app, Context(), ChangeScope.Settings));
            Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("gb-kanban", app, Context()));
        }
        // A deploy's temporaries stay Gitberth's, whatever the source: the bridge (_next) by its label, and a Git app's
        // container under another name (_prev).
        var next = With(Labelled("gb-kanban", ("gitberth.source", "registry"), ("gitberth.bridge", "true")), i => i["Name"] = "/gb-kanban_next");
        Assert.Equal(RecreateKind.DeployTemporary, RecreateRules.Classify("gb-kanban_next", next, Context(), ChangeScope.Settings).Kind);
        var previous = With(DockerFixtures.Inspect("gb-kanban"), i => i["Name"] = "/gb-kanban_prev");
        Assert.Equal(RecreateKind.DeployTemporary, RecreateRules.Classify("gb-kanban_prev", previous, Context(), ChangeScope.Settings).Kind);
    }

    [Fact]
    public void An_image_apps_container_under_another_name_is_changed_like_any_other()
    {
        // Renamed outside Gitberth, or created under another name: its source label says no deploy made it.
        foreach (var source in new[] { "registry", "local", "manual" })
        {
            var renamed = With(Labelled("gb-kanban", ("gitberth.source", source)), i => i["Name"] = "/kanban-old");
            Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("kanban-old", renamed, Context(), ChangeScope.Settings));
            Assert.Equal(RecreateVerdict.Yes, RecreateRules.Classify("kanban-old", renamed, Context()));
            Assert.Null(RecreateRules.LiveRefusal("kanban-old", renamed, Context()));
        }
    }

    [Fact]
    public void Live_changes_are_refused_only_where_nothing_may_change_it()
    {
        // Gitberth itself, its helpers, Swarm, Kubernetes, a Git app's container and a change under way: refused.
        Assert.Equal("Gitberth's own container: change it in its Compose file.", RecreateRules.LiveRefusal("gitberth", DockerFixtures.Inspect("gitberth"), Context()));
        Assert.NotNull(RecreateRules.LiveRefusal("sample-index", Labelled("sample-index", ("com.docker.swarm.task.id", "x")), Context()));
        Assert.NotNull(RecreateRules.LiveRefusal("sample-index", Labelled("sample-index", ("io.kubernetes.pod.name", "x")), Context()));
        Assert.NotNull(RecreateRules.LiveRefusal("sample-index", Labelled("sample-index", ("gitberth.helper", "probe")), Context()));
        Assert.Equal("gb-kanban is the app kanban's container: its limits are the app's settings.",
            RecreateRules.LiveRefusal("gb-kanban", DockerFixtures.Inspect("gb-kanban"), Context()));
        Assert.Equal("Busy.", RecreateRules.LiveRefusal("sample-index", DockerFixtures.Inspect("sample-index"), Context(busy: "Busy.")));
        Assert.Equal("There's no container called gone.", RecreateRules.LiveRefusal("gone", null, Context()));

        // Compose is allowed, with its note; so are the host's network, a stopped container and any other.
        var composed = Labelled("sample-index", ("com.docker.compose.project", "media"), ("com.docker.compose.service", "web"));
        Assert.Null(RecreateRules.LiveRefusal("sample-index", composed, Context()));
        Assert.Equal("Compose sets these again when it recreates the container.", RecreateRules.ComposeLiveNote);
        Assert.Null(RecreateRules.LiveRefusal("cloudflare-tunnel", DockerFixtures.Inspect("cloudflare-tunnel"), Context()));
        Assert.Null(RecreateRules.LiveRefusal("postgres", DockerFixtures.Inspect("postgres"), Context()));
        Assert.Null(RecreateRules.LiveRefusal("gb-kanban", Labelled("gb-kanban", ("gitberth.source", "registry")), Context()));
    }

    [Fact]
    public void Refuses_gitberths_helpers() =>
        Assert.Equal(RecreateKind.Helper, Classify(Labelled("sample-index", ("gitberth.helper", "probe"))).Kind);

    [Fact]
    public void Sends_an_apps_container_to_the_app() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.App, "gb-kanban is the app kanban's container: its ports are on the app's Network tab."),
            Classify(DockerFixtures.Inspect("gb-kanban")));

    [Fact]
    public void Refuses_a_deploys_temporary_containers()
    {
        var next = With(DockerFixtures.Inspect("gb-kanban"), i => i["Name"] = "/gb-kanban_next");
        Assert.Equal(new RecreateVerdict(RecreateKind.DeployTemporary, "This is a deploy's temporary or previous container, which Gitberth removes itself."), Classify(next));
        Assert.Equal(RecreateKind.DeployTemporary, Classify(Labelled("gb-kanban", ("gitberth.bridge", "true"))).Kind);
        Assert.Equal(RecreateKind.DeployTemporary, Classify(Labelled("sample-index", ("gitberth.bridge", "true"))).Kind);
    }

    [Fact]
    public void Gives_a_compose_container_its_yaml_and_the_command_from_its_labels()
    {
        var postgres = Labelled("postgres",
            ("com.docker.compose.project", "db"),
            ("com.docker.compose.service", "postgres"),
            ("com.docker.compose.project.config_files", "/opt/db/docker-compose.yml,/opt/db/docker-compose.override.yml"),
            ("com.docker.compose.project.working_dir", "/opt/db"));

        var verdict = Classify(postgres);

        Assert.Equal(RecreateKind.Compose, verdict.Kind);
        Assert.Equal("Docker Compose manages this container (project db, service postgres). Compose would undo a change made here, so this is the change to make in its file.",
            verdict.Reason);
        var compose = verdict.Compose!;
        // The project and every file from the labels, so it picks the stack it was started as, from any folder.
        Assert.Equal("docker compose -p db -f /opt/db/docker-compose.yml -f /opt/db/docker-compose.override.yml up -d postgres", compose.Command);
        Assert.Equal("/opt/db/docker-compose.yml, /opt/db/docker-compose.override.yml", compose.File);
        Assert.Equal("services:\n  postgres:\n    ports:\n      - \"5432:5432\"", compose.Snippet([new NewBinding(5432, "tcp", "", 5432)]));
        Assert.Equal("services:\n  postgres:\n    ports:\n      - \"127.0.0.1:5433:5432\"\n      - \"53:53/udp\"",
            compose.Snippet([new NewBinding(5432, "tcp", "127.0.0.1", 5433), new NewBinding(53, "udp", "", 53)]));
        Assert.Equal("services:\n  postgres:\n    ports: []", compose.Snippet([]));

        // Without the file labels: "its Compose file", and a path with a space is quoted.
        var bare = new ComposeService("my db", "postgres", [], null);
        Assert.Equal("its Compose file", bare.File);
        Assert.Equal("docker compose -p 'my db' up -d postgres", bare.Command);
        Assert.Null(new ComposeService("db", null, [], null).Command);
    }

    [Fact]
    public void Refuses_a_swarm_task() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Swarm, "Part of a Swarm service: change its ports with docker service update --publish-add."),
            Classify(Labelled("sample-index", ("com.docker.swarm.task.id", "x1y2z3"), ("com.docker.swarm.service.id", "s1"))));

    [Fact]
    public void Refuses_a_kubernetes_container() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Kubernetes, "Managed by Kubernetes: change its ports there."),
            Classify(Labelled("sample-index", ("io.kubernetes.pod.name", "web-0"))));

    [Fact]
    public void Refuses_the_host_network() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.HostNetwork, "Uses the host's network, so Docker maps no ports: it listens on 10.1.1.80 directly."),
            Classify(DockerFixtures.Inspect("cloudflare-tunnel")));

    [Fact]
    public void Refuses_a_container_in_another_containers_network()
    {
        var vpn = Other("vpn", "NetworkMode", "bridge");
        var sidecar = With(DockerFixtures.Inspect("sample-index"), i => i["HostConfig"]!["NetworkMode"] = "container:" + (string)vpn["Id"]!);

        Assert.Equal(new RecreateVerdict(RecreateKind.SharesNetwork, "Shares the network of vpn: its ports are vpn's.", Link: "vpn"), Classify(sidecar, Context([vpn])));
    }

    [Fact]
    public void Refuses_a_container_without_a_network() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.NoNetwork, "No network (network mode none)."),
            Classify(With(DockerFixtures.Inspect("sample-index"), i => i["HostConfig"]!["NetworkMode"] = "none")));

    [Theory]
    [InlineData("NetworkMode", "sidecar uses its network; recreating it would cut sidecar off.")]
    [InlineData("PidMode", "sidecar shares its processes; recreating it would cut sidecar off.")]
    [InlineData("IpcMode", "sidecar shares its IPC namespace; recreating it would cut sidecar off.")]
    public void Refuses_a_container_whose_namespaces_others_use(string mode, string reason)
    {
        var postgres = DockerFixtures.Inspect("postgres");

        // By ID, as Docker writes it, or by name.
        Assert.Equal(new RecreateVerdict(RecreateKind.SharedByOthers, reason), Classify(postgres, Context([Other("sidecar", mode, "container:" + (string)postgres["Id"]!)])));
        Assert.Equal(new RecreateVerdict(RecreateKind.SharedByOthers, reason), Classify(postgres, Context([Other("sidecar", mode, "container:postgres")])));
    }

    [Fact]
    public void Refuses_a_container_others_link_to()
    {
        // A container linked with --link fails its next start once its target has been recreated.
        var web = Other("sample-web", "Links", new JsonArray("/postgres:/sample-web/db"));

        Assert.Equal(new RecreateVerdict(RecreateKind.LinkedTo, "sample-web links to postgres (--link); recreating postgres would stop sample-web from starting."),
            Classify(DockerFixtures.Inspect("postgres"), Context([web])));
        // Its own links are no reason: they're created again with it.
        Assert.Equal(RecreateVerdict.Yes, Classify(With(DockerFixtures.Inspect("postgres"), i => i["HostConfig"]!["Links"] = new JsonArray("/sample-db:/postgres/db"))));
    }

    [Fact]
    public void Refuses_a_container_started_with_rm() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.AutoRemove, "Started with --rm: stopping it deletes it, so Gitberth can't recreate it."),
            Classify(With(DockerFixtures.Inspect("sample-index"), i => i["HostConfig"]!["AutoRemove"] = true)));

    [Fact]
    public void Refuses_a_paused_container() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Paused, "Paused. Unpause it to change its ports."),
            Classify(With(DockerFixtures.Inspect("sample-index"), i => i["State"]!["Paused"] = true)));

    [Fact]
    public void Refuses_a_container_that_keeps_restarting() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Restarting, "sample-index keeps restarting. Stop it first: then it's recreated stopped."),
            Classify(With(DockerFixtures.Inspect("sample-index"), i => i["State"]!["Restarting"] = true)));

    [Fact]
    public void Refuses_on_docker_before_25() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.EngineTooOld, "Changing ports needs Docker 25 or later (API 1.44). This host has Docker 24.0.7 (API 1.43)."),
            Classify(DockerFixtures.Inspect("postgres"), Context(engine: new EngineVersion("24.0.7", "1.43"))));

    [Fact]
    public void Refuses_while_a_change_runs() =>
        Assert.Equal(new RecreateVerdict(RecreateKind.Busy, "Gitberth is already changing postgres. Try again when that's done."),
            Classify(DockerFixtures.Inspect("postgres"), Context(busy: "Gitberth is already changing postgres. Try again when that's done.")));

    [Fact]
    public void Refuses_a_container_whose_network_is_gone_and_has_no_other()
    {
        var postgres = With(DockerFixtures.Inspect("postgres"), i => i["NetworkSettings"]!["Networks"] = new JsonObject());

        Assert.Equal(new RecreateVerdict(RecreateKind.NetworkGone, "Its network gitdock no longer exists, and it's on no other network."), Classify(postgres));
    }

    [Theory]
    [InlineData("1.56", 1, 44, true)]
    [InlineData("1.44", 1, 44, true)]
    [InlineData("1.43", 1, 44, false)]
    [InlineData("1.48", 1, 48, true)]
    [InlineData("2.0", 1, 48, true)]
    [InlineData("", 1, 44, false)]
    public void Compares_api_versions_by_number(string api, int major, int minor, bool atLeast) =>
        Assert.Equal(atLeast, new EngineVersion("x", api).AtLeast(major, minor));
}
