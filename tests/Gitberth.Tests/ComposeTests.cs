using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Compose apps: which file and which web service New app finds, the file Gitberth runs (labels, names and their traps,
/// restart, the web service's ports, built images, what a preview loses), bind mounts' host paths, releases kept for
/// rollback, the .env it writes, and an inline app made from a pasted file.
/// </summary>
public sealed class ComposeTests
{
    private const string Release = "/data/compose/shop/abc1234";
    private const string ReleaseHost = "/var/lib/docker/volumes/gitberth-data/_data/compose/shop/abc1234";
    private const string Kept = "/data/compose/shop/data";
    private const string KeptHost = "/var/lib/docker/volumes/gitberth-data/_data/compose/shop/data";

    // ---- Detection ----

    [Fact]
    public void The_compose_files_in_a_folder_come_in_composes_own_order()
    {
        var dir = Directory.CreateTempSubdirectory("gb-compose-find").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services: {}");
            File.WriteAllText(Path.Combine(dir, "compose.yaml"), "services: {}");
            File.WriteAllText(Path.Combine(dir, "compose.override.yaml"), "services: {}");
            Assert.Equal(["compose.yaml", "docker-compose.yml"], ComposeFiles.Find(dir));
            Assert.Empty(ComposeFiles.Find(Path.Combine(dir, "nowhere")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_compose_file_is_read_for_its_services_ports_binds_copies_and_variables()
    {
        var info = ComposeFiles.Read("""
            x-common: &common
              restart: always
            services:
              web:
                <<: *common
                build: .
                ports:
                  - "8080:80"
                  - "127.0.0.1:9090:9090/udp"
                  - target: 3000
                    published: "3001"
                volumes:
                  - ./conf:/etc/conf:ro
                  - data:/var/data
                  - ${UPLOADS:-./uploads}:/uploads
                environment:
                  SECRET: ${SECRET:?set it}
                  NAME: $NAME
              worker:
                image: ghcr.io/acme/worker:2
                deploy:
                  replicas: 3
                expose: ["7000"]
            """);
        Assert.Null(info.Error);
        Assert.Equal(["web", "worker"], info.Services.Select(s => s.Name));
        var web = info.Service("web")!;
        Assert.True(web.Builds);
        Assert.Equal([new ComposePort(80, 8080), new ComposePort(9090, 9090, "udp", "127.0.0.1"), new ComposePort(3000, 3001)], web.Ports);
        Assert.Equal([new ComposeBind("./conf", "/etc/conf", true), new ComposeBind("${UPLOADS:-./uploads}", "/uploads", false)], web.Binds);
        Assert.Equal(3, info.Service("worker")!.Copies);
        Assert.Equal([new ComposePort(7000, null)], info.Service("worker")!.Ports);
        Assert.Equal([new ComposeVariable("UPLOADS", "./uploads", false), new ComposeVariable("SECRET", null, true), new ComposeVariable("NAME", null, false)],
            info.Variables);
    }

    [Fact]
    public void A_file_that_isnt_compose_says_why()
    {
        Assert.Contains("can't be read", ComposeFiles.Read("services: [unclosed").Error);
        Assert.Contains("no services", ComposeFiles.Read("volumes: {}").Error);
        Assert.Contains("start with services", ComposeFiles.Read("- a list").Error);
    }

    [Fact]
    public void The_web_service_is_the_one_that_publishes_a_web_port_never_the_database()
    {
        var info = ComposeFiles.Read("""
            services:
              db:
                image: postgres:18
                ports: ["5432:5432"]
              cache:
                image: redis:8-alpine
              app:
                build: .
                ports: ["3000:3000"]
            """);
        Assert.Equal(new ComposeWeb("app", 3000), ComposeFiles.PickWeb(info.Services));
    }

    [Fact]
    public void Without_published_ports_a_known_web_image_or_name_is_the_web_service()
    {
        var byImage = ComposeFiles.Read("services:\n  cache:\n    image: redis:8\n  proxy:\n    image: nginx:1.27\n");
        Assert.Equal(new ComposeWeb("proxy", 80), ComposeFiles.PickWeb(byImage.Services));
        var byName = ComposeFiles.Read("services:\n  worker:\n    image: acme/worker\n  web:\n    image: acme/site\n    expose: [\"8000\"]\n");
        Assert.Equal(new ComposeWeb("web", 8000), ComposeFiles.PickWeb(byName.Services));
        // Only databases: the first is it all the same, without a port it knows.
        var onlyData = ComposeFiles.Read("services:\n  db:\n    image: postgres:18\n");
        Assert.Equal("db", ComposeFiles.PickWeb(onlyData.Services)!.Service);
    }

    [Fact]
    public void Compose_is_the_first_choice_with_several_services_or_nothing_else_to_build()
    {
        var two = ComposeFiles.Read("services:\n  web:\n    build: .\n  cache:\n    image: redis\n");
        var one = ComposeFiles.Read("services:\n  web:\n    build: .\n");
        Assert.True(Components.NewApp.GitSource.PreferCompose(two, somethingElse: true));
        Assert.False(Components.NewApp.GitSource.PreferCompose(one, somethingElse: true));
        Assert.True(Components.NewApp.GitSource.PreferCompose(one, somethingElse: false));
        Assert.False(Components.NewApp.GitSource.PreferCompose(ComposeFiles.Read("nope: ["), somethingElse: false));
    }

    // ---- Names ----

    [Fact]
    public void A_member_is_named_after_its_app_and_service_never_like_a_deploys_temporary()
    {
        Assert.Equal("gb-shop", ComposeNames.Project("shop"));
        Assert.Equal("gb-shop_web", ComposeNames.Member("shop", "web"));
        Assert.Equal("gb-shop_next-1", ComposeNames.Member("shop", "next"));
        Assert.Equal("gb-shop_api_prev-1", ComposeNames.Member("shop", "api_prev"));
        Assert.Equal("gb-shop_api_NEXT-1", ComposeNames.Member("shop", "api_NEXT"));
        // A slug never has '_': no member is ever named like another app's container (gb-<slug>).
        Assert.Contains('_', ComposeNames.Member("shop", "db"));
        Assert.Equal("gitberth/shop/web", ComposeNames.ImageRepository("shop", "Web"));
        Assert.Equal("gitberth/shop/my.api", ComposeNames.ImageRepository("shop", "my..api"));
        Assert.Equal("gitberth/shop/a__b-c", ComposeNames.ImageRepository("shop", "A__b@c"));
        Assert.Equal("gitberth/shop/service", ComposeNames.ImageRepository("shop", "__"));
    }

    // ---- The file Gitberth runs ----

    private static JsonObject Resolved(string services, string? top = null) =>
        (JsonObject)JsonNode.Parse($$"""{"name":"whatever","services":{{{services}}}{{(top is null ? "" : "," + top)}}}""")!;

    private static ComposeTarget Target(bool preview = false, IReadOnlyDictionary<string, string>? images = null, IReadOnlyList<PortMapping>? ports = null,
        Func<string, PathKind>? kind = null, IReadOnlySet<(string, string)>? relative = null, bool lanIpv4Only = false) => new()
    {
        Slug = "shop", WebService = "web", DeploymentId = 42, Trigger = DeployTrigger.Poll, ReleaseDir = Release, ReleaseHostDir = ReleaseHost,
        DataDir = Kept, DataHostDir = KeptHost, Preview = preview, Tag = "abc1234", Images = images, LanIpv4Only = lanIpv4Only,
        Ports = ports ?? [new PortMapping { Id = 1, ContainerPort = 8080, HostPort = 3100, Access = PortAccess.Lan, IsWeb = true }],
        Kind = kind ?? (_ => PathKind.None), RelativeBinds = relative ?? new HashSet<(string, string)>(),
    };

    [Fact]
    public void Every_service_is_labelled_named_and_restarted_as_the_apps()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web:1","labels":{"gitberth.app":"other","gitberth.helper":"ports","team":"core"}},
            "cache":{"image":"redis:8-alpine","restart":"always","container_name":"my-redis"}
            """), Target());
        Assert.Empty(plan.Problems);
        Assert.Equal("gb-shop", (string?)plan.Model["name"]);
        var web = plan.Model["services"]!["web"]!;
        var cache = plan.Model["services"]!["cache"]!;
        Assert.Equal("gb-shop_web", (string?)web["container_name"]);
        Assert.Equal("gb-shop_cache", (string?)cache["container_name"]);
        Assert.Equal("unless-stopped", (string?)web["restart"]);
        Assert.Equal("always", (string?)cache["restart"]);
        var labels = (JsonObject)web["labels"]!;
        Assert.Equal(
            ["gitberth.app=shop", "gitberth.deployment=42", "gitberth.managed=true", "gitberth.service=web", "gitberth.trigger=Poll", "team=core"],
            labels.Select(l => $"{l.Key}={(string?)l.Value}").Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, string> { ["web"] = "gb-shop_web", ["cache"] = "gb-shop_cache" }, plan.Members);
        Assert.Equal("web", (string?)plan.Model[ComposeTransform.OwnKey]!["web"]);
    }

    [Fact]
    public void A_service_the_release_that_runs_ran_the_same_way_keeps_its_container()
    {
        var running = ComposeTransform.Apply(Resolved("""
            "web":{"image":"gitberth/shop/web:1111111","environment":{"GREETING":"hi"}},
            "cache":{"image":"redis:8-alpine"},
            "worker":{"image":"acme/worker:1","environment":{"QUEUE":"a"}}
            """), Target() with { DeploymentId = 41, Trigger = DeployTrigger.Manual }).Model;
        var next = ComposeTransform.Apply(Resolved("""
            "web":{"image":"gitberth/shop/web:2222222","environment":{"GREETING":"hi"}},
            "cache":{"image":"redis:8-alpine"},
            "worker":{"image":"acme/worker:1","environment":{"QUEUE":"b"}},
            "mail":{"image":"axllent/mailpit"}
            """), Target()).Model;
        // A new commit's web service and a changed variable's worker are recreated; the cache keeps running as it is.
        Assert.Equal(["cache"], ComposeTransform.KeepUnchanged(next, running));
        Assert.Equal(("41", "Manual"), ((string?)next["services"]!["cache"]!["labels"]!["gitberth.deployment"], (string?)next["services"]!["cache"]!["labels"]!["gitberth.trigger"]));
        Assert.Equal("42", (string?)next["services"]!["web"]!["labels"]!["gitberth.deployment"]);
        Assert.Empty(ComposeTransform.KeepUnchanged(next, null));
    }

    [Fact]
    public void A_service_that_asks_for_several_containers_is_refused_in_plain_words()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web:1"},
            "worker":{"image":"acme/worker:1","deploy":{"replicas":3}}
            """), Target());
        Assert.Equal(["The service worker asks for 3 containers (deploy.replicas or scale). Gitberth runs one container a service: set it to 1 in the Compose file."],
            plan.Problems);
        Assert.Contains("isn't in the Compose file", ComposeTransform.Apply(Resolved("""
            "api":{"image":"acme/api:1"}
            """), Target()).Problems.Single());
    }

    [Fact]
    public void Two_services_that_would_share_a_container_name_are_refused()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"a"},
            "next":{"image":"b"},
            "next-1":{"image":"c"}
            """), Target());
        Assert.Contains("Two services would both be called gb-shop_next-1. Rename one of them in the Compose file.", plan.Problems);
    }

    [Fact]
    public void The_web_service_publishes_the_apps_ports_in_place_of_its_files()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web:1","ports":[{"target":80,"published":"8080","protocol":"tcp"}],"expose":["9000"]},
            "db":{"image":"postgres:18","ports":[{"target":5432,"published":"5432","protocol":"tcp"}]}
            """), Target(ports:
            [
                new PortMapping { Id = 1, ContainerPort = 80, HostPort = 3100, Access = PortAccess.Lan, IsWeb = true },
                new PortMapping { Id = 2, ContainerPort = 9100, HostPort = 9100, Access = PortAccess.HostOnly, Protocol = "udp" },
                new PortMapping { Id = 3, ContainerPort = 7000, Access = PortAccess.Internal },
            ], lanIpv4Only: true));
        var web = plan.Model["services"]!["web"]!;
        Assert.Equal("""[{"mode":"ingress","target":80,"published":"3100","protocol":"tcp","host_ip":"0.0.0.0"},{"mode":"ingress","target":9100,"published":"9100","protocol":"udp","host_ip":"127.0.0.1"}]""",
            web["ports"]!.ToJsonString());
        Assert.Equal("""["9000","7000"]""", web["expose"]!.ToJsonString());
        // Another service's ports are the owner's, as written.
        Assert.Equal("5432", (string?)plan.Model["services"]!["db"]!["ports"]![0]!["published"]);
    }

    [Fact]
    public void A_built_service_is_tagged_as_the_apps_and_a_deploy_without_a_build_runs_the_image_it_had()
    {
        const string services = """
            "web":{"build":{"context":"/data/compose/shop/abc1234","dockerfile":"Dockerfile"},"pull_policy":"build"},
            "cache":{"image":"redis:8-alpine"}
            """;
        var plan = ComposeTransform.Apply(Resolved(services), Target());
        Assert.Equal("gitberth/shop/web:abc1234", (string?)plan.Model["services"]!["web"]!["image"]);
        Assert.Null(plan.Model["services"]!["web"]!["pull_policy"]);
        Assert.Equal("gitberth/shop/web:abc1234", plan.Builds.Single().Tag);
        Assert.Equal(new Dictionary<string, string> { ["web"] = "gitberth/shop/web:abc1234", ["cache"] = "redis:8-alpine" }, plan.Images);

        var again = ComposeTransform.Apply(Resolved(services), Target(images: new Dictionary<string, string> { ["web"] = "gitberth/shop/web:0ld0000" }));
        Assert.Empty(again.Builds);
        Assert.Equal("gitberth/shop/web:0ld0000", (string?)again.Model["services"]!["web"]!["image"]);
        Assert.Null(again.Model["services"]!["web"]!["build"]);
    }

    [Fact]
    public void A_build_from_outside_the_repo_is_refused()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"build":{"context":"/data","dockerfile":"Dockerfile"}}
            """), Target());
        Assert.Equal(["The service web builds from /data, outside the repo. Gitberth builds from the repo's own files only."], plan.Problems);
        var remote = ComposeTransform.Apply(Resolved("""
            "web":{"build":{"context":"https://github.com/acme/web.git#main"}}
            """), Target());
        Assert.Empty(remote.Problems);
    }

    [Fact]
    public void Relative_bind_mounts_go_to_the_releases_folder_on_the_host_or_to_the_apps_kept_folder()
    {
        var kinds = new Dictionary<string, PathKind>
        {
            [$"{Release}/nginx.conf"] = PathKind.File,
            [$"{Release}/config"] = PathKind.Directory,
        };
        var plan = ComposeTransform.Apply(Resolved($$"""
            "web":{"image":"nginx","volumes":[
              {"type":"bind","source":"{{Release}}/nginx.conf","target":"/etc/nginx/nginx.conf","read_only":true},
              {"type":"bind","source":"{{Release}}/config","target":"/config"},
              {"type":"bind","source":"{{Release}}/data","target":"/data"},
              {"type":"bind","source":"/srv/photos","target":"/photos"},
              {"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock"},
              {"type":"volume","source":"cache","target":"/cache"}]}
            """), Target(kind: p => kinds.GetValueOrDefault(p)));
        Assert.Empty(plan.Problems);
        var sources = plan.Model["services"]!["web"]!["volumes"]!.AsArray().Select(v => (string?)v!["source"]).ToList();
        Assert.Equal([$"{ReleaseHost}/nginx.conf", $"{KeptHost}/config", $"{KeptHost}/data", "/srv/photos", "/var/run/docker.sock", "cache"], sources);
        Assert.Equal([new ComposeMount($"{Kept}/config", $"{Release}/config", "config"), new ComposeMount($"{Kept}/data", null, "data")], plan.Kept);
    }

    [Fact]
    public void A_relative_bind_that_leaves_the_release_is_refused_and_an_absolute_one_is_the_owners()
    {
        // ../../etc from the release resolves outside it; the file wrote it as a relative path.
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"nginx","volumes":[{"type":"bind","source":"/data/compose/etc","target":"/host-etc"},
              {"type":"bind","source":"/etc/localtime","target":"/etc/localtime","read_only":true}]}
            """), Target(relative: new HashSet<(string, string)> { ("web", "/host-etc") }));
        Assert.Equal(["The service web mounts /data/compose/etc at /host-etc, outside the repo. A relative path in the Compose file stays inside it; write an absolute path for a folder of the host."],
            plan.Problems);
        Assert.Equal("/etc/localtime", (string?)plan.Model["services"]!["web"]!["volumes"]![1]!["source"]);
    }

    [Fact]
    public void The_relative_binds_are_told_from_the_file_with_the_apps_variables_filled_in()
    {
        var file = ComposeFiles.Read("""
            services:
              web:
                image: nginx
                volumes:
                  - ./conf:/conf
                  - ${UPLOADS:-./uploads}:/uploads
                  - ${PHOTOS}:/photos
                  - /srv/x:/x
                  - named:/named
            """);
        Assert.Equal(new HashSet<(string, string)> { ("web", "/conf"), ("web", "/uploads") },
            DeployPipeline.RelativeBinds(file, [("PHOTOS", "/mnt/photos")]));
        Assert.Contains(("web", "/photos"), DeployPipeline.RelativeBinds(file, [("PHOTOS", "photos")]));
    }

    [Fact]
    public void A_preview_gets_no_more_of_the_host_than_a_preview_of_one_container()
    {
        var plan = ComposeTransform.Apply(Resolved($$"""
            "web":{"image":"acme/web","privileged":true,"cap_add":["NET_ADMIN"],"network_mode":"host","pid":"host",
              "security_opt":["no-new-privileges:true","seccomp:unconfined"],"sysctls":{"net.core.somaxconn":"1024"},
              "extra_hosts":["host.docker.internal=host-gateway","api=10.0.0.5"],"devices":["/dev/ttyUSB0:/dev/ttyUSB0"],
              "volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock"},{"type":"bind","source":"{{Release}}/conf","target":"/conf"}],
              "build":{"context":"{{Release}}","ssh":["default"] } },
            "db":{"image":"postgres:18","ports":[{"target":5432,"published":"5432"}],"networks":{"default":null,"shared":null},
              "deploy":{"resources":{"reservations":{"devices":[{"capabilities":["gpu"] }] } } } }
            """, """
            "volumes":{"pgdata":{"name":"production-pgdata","external":true}},
            "networks":{"default":{"name":"gb-shop-pr-1_default"},"shared":{"name":"shared","external":true}},
            "secrets":{"key":{"file":"/etc/shadow"}}
            """), Target(preview: true) with { Slug = "shop-pr-1" });
        Assert.Empty(plan.Problems);
        var web = (JsonObject)plan.Model["services"]!["web"]!;
        foreach (var gone in new[] { "privileged", "cap_add", "network_mode", "pid", "sysctls", "devices" }) Assert.False(web.ContainsKey(gone), gone);
        Assert.Equal("""["no-new-privileges:true"]""", web["security_opt"]!.ToJsonString());
        Assert.Equal("""["api=10.0.0.5"]""", web["extra_hosts"]!.ToJsonString());
        Assert.Equal("[]", web["volumes"]!.ToJsonString());
        Assert.False(((JsonObject)web["build"]!).ContainsKey("ssh"));
        var db = (JsonObject)plan.Model["services"]!["db"]!;
        Assert.False(db.ContainsKey("ports"));
        Assert.Null(db["deploy"]!["resources"]!["reservations"]!["devices"]);
        Assert.False(((JsonObject)db["networks"]!).ContainsKey("shared"));
        Assert.False(((JsonObject)plan.Model["networks"]!).ContainsKey("shared"));
        Assert.Equal("{}", plan.Model["volumes"]!["pgdata"]!.ToJsonString());
        Assert.Equal("{}", plan.Model["secrets"]!.ToJsonString());
        Assert.Contains(plan.Notes, n => n.StartsWith("Taken out of web, because a preview gets no more of the host than a preview of one container: privileged mode, added capabilities", StringComparison.Ordinal));
        Assert.Contains("Not mounted in web: /var/run/docker.sock at /var/run/docker.sock, because a preview never mounts a folder of the host.", plan.Notes);
        Assert.Contains("Not mounted in web: ./conf at /conf, because a preview never mounts a folder of the host.", plan.Notes);
    }

    [Fact]
    public void Production_keeps_what_its_owners_file_asks_for()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web","privileged":true,"cap_add":["NET_ADMIN"],"volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock"}]}
            """, """
            "secrets":{"key":{"file":"/data/compose/shop/abc1234/secrets/key.txt"}}
            """), Target());
        var web = (JsonObject)plan.Model["services"]!["web"]!;
        Assert.True((bool)web["privileged"]!);
        Assert.Equal("/var/run/docker.sock", (string?)web["volumes"]![0]!["source"]);
        // A file secret is mounted from the Docker host: the release's file there.
        Assert.Equal($"{ReleaseHost}/secrets/key.txt", (string?)plan.Model["secrets"]!["key"]!["file"]);
    }

    // ---- Releases ----

    [Fact]
    public void A_failed_deploy_goes_back_to_the_newest_release_that_worked_and_is_kept()
    {
        var history = new[] { (1, DeployStatus.Succeeded), (2, DeployStatus.Failed), (3, DeployStatus.Succeeded), (4, DeployStatus.RolledBack), (5, DeployStatus.Building) };
        Assert.Equal(3, ComposeReleases.PickRollback(history, 5, _ => true));
        Assert.Equal(1, ComposeReleases.PickRollback(history, 5, id => id != 3));
        Assert.Null(ComposeReleases.PickRollback(history, 5, _ => false));
        Assert.Null(ComposeReleases.PickRollback([(1, DeployStatus.Building)], 1, _ => true));
        // Never a later one (a rollback of a rollback goes back in time).
        Assert.Equal(1, ComposeReleases.PickRollback(history, 2, _ => true));
    }

    [Fact]
    public void The_last_three_releases_that_worked_are_kept_and_the_one_that_runs()
    {
        var history = Enumerable.Range(1, 6).Select(i => (i, i == 5 ? DeployStatus.Failed : DeployStatus.Succeeded)).ToList();
        Assert.Equal(new HashSet<int> { 6, 4, 3 }, ComposeReleases.ToKeep(history, running: 6));
        Assert.Equal(new HashSet<int> { 6, 4, 3, 1 }, ComposeReleases.ToKeep(history, running: 1));
    }

    [Fact]
    public void Pruning_removes_the_releases_no_kept_deploy_ran_and_never_the_kept_data()
    {
        var root = Directory.CreateTempSubdirectory("gb-releases").FullName;
        try
        {
            var releases = new ComposeReleases(root);
            foreach (var (release, deploys) in new[] { ("aaaaaaa", new[] { 1, 2 }), ("bbbbbbb", new[] { 3 }), ("ccccccc", new[] { 4 }) })
            {
                Directory.CreateDirectory(releases.Dir(release));
                foreach (var id in deploys) File.WriteAllText(Path.Combine(releases.Dir(release), ComposeReleases.ResolvedName(id)), "{}");
            }
            Directory.CreateDirectory(releases.DataDir);
            Assert.Equal(Path.Combine(releases.Dir("aaaaaaa"), "gitberth-2.json"), releases.ResolvedOf(2));
            Assert.Equal(["bbbbbbb"], releases.Prune(new HashSet<int> { 2, 4 }));
            Assert.Null(releases.ResolvedOf(1));
            Assert.NotNull(releases.ResolvedOf(2));
            Assert.NotNull(releases.ResolvedOf(4));
            Assert.True(Directory.Exists(releases.DataDir));
            Assert.Equal(7, ComposeReleases.IdOf("services: {}").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- The .env and variables ----

    [Fact]
    public void The_env_file_has_the_repos_own_first_then_the_apps_values_as_compose_reads_them_back()
    {
        var text = ComposeEnvFile.Write([("PLAIN", "a $b \"c\""), ("QUOTE", "it's"), ("LINES", "one\ntwo"), ("not valid", "x")], "FROM_REPO=1\n");
        Assert.Equal("FROM_REPO=1\n# Set by Gitberth: the app's variables. Changed in Gitberth, not here.\nPLAIN='a $b \"c\"'\nQUOTE=\"it's\"\nLINES=\"one\\ntwo\"\n", text);
        Assert.Equal("\"back\\\\slash \\$HOME it's\"", ComposeEnvFile.Quote("back\\slash $HOME it's"));
        Assert.Equal("'back\\slash $HOME'", ComposeEnvFile.Quote("back\\slash $HOME"));
        // Compose reads a quote after a backslash as an escaped one: a value that ends with one goes unquoted.
        Assert.Equal("C:\\data\\", ComposeEnvFile.Quote("C:\\data\\"));
        Assert.Equal("", ComposeEnvFile.Write([]));
    }

    [Fact]
    public void Variables_fill_a_compose_text_as_compose_does()
    {
        var values = new Dictionary<string, string> { ["SET"] = "yes", ["EMPTY"] = "" };
        Assert.Equal("yes|fallback|", ComposeVariables.Fill("${SET:-no}|${MISSING:-fallback}|${EMPTY-unused}", values));
        Assert.Equal("fallback|other||$literal|yes", ComposeVariables.Fill("${EMPTY:-fallback}|${SET:+other}|${MISSING:+other}|$$literal|$SET", values));
        Assert.Equal("./yes/data", ComposeVariables.Fill("${DATA:-./${SET}/data}", values));
        Assert.Equal([new ComposeVariable("DATA", "./${SET}/data", false), new ComposeVariable("SET", null, false), new ComposeVariable("KEY", null, true)],
            ComposeVariables.Used("${DATA:-./${SET}/data} ${KEY:?needed} $$NOT"));
    }

    // ---- The app on its pages ----

    private static NetworkContainer Member(string slug, string service, string state = "running", string status = "Up 2 minutes") =>
        new(service, $"gb-{slug}_{service}", "x", "sha256:x", state, status, "",
            new Dictionary<string, string> { ["gitberth.app"] = slug, ["gitberth.service"] = service, ["gitberth.managed"] = "true" }, [], null, []);

    [Fact]
    public void A_compose_apps_row_counts_its_services_and_is_degraded_while_one_is_down()
    {
        IReadOnlyList<NetworkContainer> all = [Member("shop", "web"), Member("shop", "cache"), Member("other", "web")];
        Assert.Equal((AppState.Running, "Running 2/2"), AppCatalog.Aggregate(AppState.Running, "Running", "shop", ["web", "cache"], all));
        Assert.Equal((AppState.Healthy, "Healthy 2/2"), AppCatalog.Aggregate(AppState.Healthy, "Healthy", "shop", ["web", "cache"], all));
        IReadOnlyList<NetworkContainer> down = [Member("shop", "web"), Member("shop", "cache", "exited", "Exited (1) 1 minute ago")];
        Assert.Equal((AppState.Degraded, "Degraded 1/2"), AppCatalog.Aggregate(AppState.Running, "Running", "shop", ["web", "cache"], down));
        // A service the release runs without a container counts as down.
        Assert.Equal((AppState.Degraded, "Degraded 1/3"), AppCatalog.Aggregate(AppState.Running, "Running", "shop", ["web", "cache", "worker"], down));
        Assert.Equal((AppState.Deploying, "Building…"), AppCatalog.Aggregate(AppState.Deploying, "Building…", "shop", ["web", "cache"], down));
        Assert.Equal((AppState.Stopped, "Stopped"), AppCatalog.Aggregate(AppState.Stopped, "Stopped", "shop", ["web", "cache"], down));
        Assert.Equal("Running 3/3", Components.Shared.AppStatus.Of(AppState.Running, "Running 3/3").Word);
        Assert.Equal("Running", Components.Shared.AppStatus.Of(AppState.Running, "Up 3 minutes").Word);
        Assert.True(Components.Shared.AppStatus.Of(AppState.Degraded, "Degraded 2/3").Trouble);
    }

    [Fact]
    public void A_compose_apps_web_service_is_the_app_and_its_other_services_are_its_own()
    {
        var apps = new Dictionary<string, NavApp> { ["shop"] = new NavApp(1, "Shop", "shop") { Container = "gb-shop_web" } };
        ContainerSummary Summary(string service) => new($"gb-shop_{service}", "running", "Up", "x", "",
            new Dictionary<string, string> { ["gitberth.app"] = "shop", ["gitberth.service"] = service, ["com.docker.compose.project"] = "gb-shop" });
        Assert.Equal(ContainerRole.App, ContainerService.RoleOf(Summary("web"), apps, null));
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(Summary("cache"), apps, null));
    }

    [Fact]
    public void A_compose_apps_service_is_never_taken_for_a_foreign_compose_projects_container()
    {
        var inspect = (JsonObject)JsonNode.Parse("""
            {"Id":"abc","Name":"/gb-shop_cache","Config":{"Image":"redis:8","Labels":{"gitberth.app":"shop","gitberth.service":"cache","gitberth.managed":"true",
             "com.docker.compose.project":"gb-shop","com.docker.compose.service":"cache"}},"HostConfig":{"NetworkMode":"gb-shop_default"},"State":{"Running":true}}
            """)!;
        var verdict = RecreateRules.Classify("gb-shop_cache", inspect, new RecreateContext(new EngineVersion("29.0.0", "1.52"), null, [], new JsonArray(), ""), ChangeScope.Settings);
        Assert.Equal(RecreateKind.App, verdict.Kind);
        Assert.Null(verdict.Compose);
        Assert.Contains("the cache service of the app shop", verdict.Reason);
        Assert.Equal("It's a service of a Compose app, which a deploy updates.",
            Images.UpdateService.AutoRefusal(new AppDefinition { Source = AppSource.Existing }, inspect, null));
    }

    // ---- Audit: what docker compose does is Gitberth's own ----

    [Fact]
    public void A_compose_run_covers_its_members_and_its_network_and_the_web_services_join()
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var expected = new ExpectedChanges { Now = () => now };
        var buffer = new OutsideChangeBuffer(expected, _ => false);
        DockerChange Change(string type, string action, string id, string name, params (string, string)[] attrs) =>
            new(now, type, action, id, name, attrs.ToDictionary(a => a.Item1, a => a.Item2, StringComparer.Ordinal));
        using (expected.ForCli(ComposeRunner.Args("gb-shop", "/data/compose/shop/abc1234/gitberth-42.json", null, ["up", "-d", "--wait"])))
        {
            buffer.Feed(Change("network", "create", "n1", "gb-shop_default", ("name", "gb-shop_default")), now);
            buffer.Feed(Change("container", "create", "c1", "gb-shop_web", ("name", "gb-shop_web")), now);
            buffer.Feed(Change("network", "connect", "n1", "gb-shop_default", ("name", "gb-shop_default"), ("container", "c1")), now);
            buffer.Feed(Change("container", "start", "c1", "gb-shop_web", ("name", "gb-shop_web")), now);
            buffer.Feed(Change("volume", "create", "gb-shop_cachedata", "gb-shop_cachedata"), now);
        }
        using (expected.ForCli(["network", "connect", "--alias", "shop", "--gw-priority", "-1", "gitberth", "gb-shop_web"]))
            buffer.Feed(Change("network", "connect", "n0", "gitberth", ("name", "gitberth"), ("container", "c1")), now);
        Assert.Empty(buffer.Flush(now.AddMinutes(2)));
    }

    [Fact]
    public async Task A_services_recreated_container_and_the_networks_and_volumes_its_file_names_are_gitberths_own()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var expected = new ExpectedChanges { Now = () => now };
        var buffer = new OutsideChangeBuffer(expected, _ => false);
        DockerChange Change(string type, string action, string id, string name, params (string, string)[] attrs) =>
            new(now, type, action, id, name, attrs.ToDictionary(a => a.Item1, a => a.Item2, StringComparer.Ordinal));
        var dir = Directory.CreateTempSubdirectory("gb-compose-run").FullName;
        try
        {
            var file = Path.Combine(dir, "gitberth-7.json");
            File.WriteAllText(file, """
                {"name":"gb-shop","services":{"web":{"image":"acme/web"}},
                 "networks":{"default":{"name":"gb-shop_default"},"backend":{"name":"backend-net"},"proxy":{"name":"proxy","external":true}},
                 "volumes":{"data":{"name":"shop-data"},"cache":{"name":"gb-shop_cache"},"old":{"name":"legacy","external":true}}}
                """);
            var named = ComposeRunner.NamedIn(JsonNode.Parse(File.ReadAllText(file)) as JsonObject);
            Assert.Equal(["backend-net"], named.Networks);
            Assert.Equal(["proxy"], named.External);
            Assert.Equal(["shop-data"], named.Volumes);
            var cli = new DockerCli(new GitberthOptions { DataDir = dir }, expected)
            {
                Runner = (args, _, _) =>
                {
                    // docker compose up recreates web: the new container is made under the old one's ID before it's renamed.
                    buffer.Feed(Change("container", "create", "c2", "0123456789ab_gb-shop_web", ("name", "0123456789ab_gb-shop_web")), now);
                    buffer.Feed(Change("container", "destroy", "c1", "gb-shop_web", ("name", "gb-shop_web")), now);
                    buffer.Feed(Change("container", "rename", "c2", "gb-shop_web", ("name", "gb-shop_web"), ("oldName", "/0123456789ab_gb-shop_web")), now);
                    buffer.Feed(Change("network", "create", "n2", "backend-net", ("name", "backend-net")), now);
                    buffer.Feed(Change("network", "connect", "n3", "proxy", ("name", "proxy"), ("container", "c2")), now);
                    buffer.Feed(Change("volume", "create", "shop-data", "shop-data"), now);
                    buffer.Feed(Change("container", "start", "c2", "gb-shop_web", ("name", "gb-shop_web")), now);
                    return Task.FromResult(new ProcessResult(0, "", ""));
                },
            };
            var runner = new ComposeRunner(cli, new GitberthOptions { DataDir = dir }, NullLogger<ComposeRunner>.Instance);
            Assert.True((await runner.RunAsync("gb-shop", file, dir, ["up", "-d", "--wait"])).Ok);
            Assert.Empty(buffer.Flush(now.AddMinutes(2)));
            // Someone else's container that only looks like a replacement (no 12-character ID) is still theirs.
            Assert.False(ExpectedChanges.ReplacementOf("abc_gb-shop_web", "gb-shop"));
            Assert.False(ExpectedChanges.ReplacementOf("0123456789ab_gb-other_web", "gb-shop"));
            Assert.True(ExpectedChanges.ReplacementOf("0123456789ab_gb-shop_web", "gb-shop"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Gitberths_data_folder_is_found_on_the_docker_host_for_bind_mounts()
    {
        Assert.Equal("/var/lib/docker/volumes/gitberth-data/_data", ComposeRunner.MountSource("""
            [{"Type":"bind","Source":"/var/run/docker.sock","Destination":"/var/run/docker.sock"},
             {"Type":"volume","Name":"gitberth-data","Source":"/var/lib/docker/volumes/gitberth-data/_data","Destination":"/data"}]
            """, "/data"));
        Assert.Equal("/srv/gitberth", ComposeRunner.MountSource("""[{"Type":"bind","Source":"/srv/gitberth","Destination":"/data/"}]""", "/data"));
        Assert.Null(ComposeRunner.MountSource("""[{"Type":"bind","Source":"/srv/x","Destination":"/other"}]""", "/data"));
        Assert.Null(ComposeRunner.MountSource("not json", "/data"));

        var options = new GitberthOptions { DataDir = "/data", RunningInContainer = true };
        var runner = new ComposeRunner(new DockerCli(options), options, NullLogger<ComposeRunner>.Instance) { DataHostDir = "/var/lib/docker/volumes/gitberth-data/_data" };
        Assert.Equal("/var/lib/docker/volumes/gitberth-data/_data/compose/shop/abc1234", await runner.HostPathAsync("/data/compose/shop/abc1234"));
        Assert.Equal("/var/lib/docker/volumes/gitberth-data/_data", await runner.HostPathAsync("/data"));
        Assert.Null(await runner.HostPathAsync("/etc/compose"));
    }

    [Fact]
    public void A_preview_gets_plain_volumes_and_networks_and_builds_nothing_under_other_names()
    {
        var plan = ComposeTransform.Apply(Resolved($$$"""
            "web":{"build":{"context":"{{{Release}}}","tags":["redis:8-alpine"],"cache_to":["type=local,dest=/data"],
                "cache_from":["type=local,src=/data/keys","acme/web:cache","type=registry,ref=acme/web:cache"],"secrets":["key"]},
              "volumes":[{"type":"volume","source":"hostroot","target":"/host"}]},
            "lan":{"image":"acme/lan","networks":{"lan":null}}
            """, """
            "volumes":{"hostroot":{"driver":"local","driver_opts":{"type":"none","o":"bind","device":"/"}}},
            "networks":{"default":{"name":"proxy","external":true},"lan":{"driver":"macvlan","driver_opts":{"parent":"eth0"},"ipam":{"config":[{"subnet":"10.1.1.0/24"}]}}},
            "secrets":{"key":{"file":"/data/compose/shop-pr-1/abc1234/key.txt"}}
            """), Target(preview: true) with { Slug = "shop-pr-1" });
        Assert.Empty(plan.Problems);
        Assert.Equal("""{"driver":"local"}""", plan.Model["volumes"]!["hostroot"]!.ToJsonString());
        Assert.Equal("{}", plan.Model["networks"]!["default"]!.ToJsonString());
        Assert.Equal("{}", plan.Model["networks"]!["lan"]!.ToJsonString());
        var build = (JsonObject)plan.Builds.Single().Build;
        Assert.False(build.ContainsKey("tags"));
        Assert.False(build.ContainsKey("cache_to"));
        Assert.Equal("""["acme/web:cache","type=registry,ref=acme/web:cache"]""", build["cache_from"]!.ToJsonString());
        Assert.Equal("[]", build["secrets"]!.ToJsonString());
        Assert.Contains(plan.Notes, n => n.StartsWith("The volume hostroot is a plain one of the preview's", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.StartsWith("The network lan is a plain bridge of the preview's", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.StartsWith("The default network is the preview's own, not proxy", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Contains("its build's other tags", StringComparison.Ordinal));

        // Production keeps its owner's volume and network drivers as written.
        var production = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web"}
            """, """
            "volumes":{"nfs":{"driver_opts":{"type":"nfs","o":"addr=10.1.1.5","device":":/export"}}}
            """), Target());
        Assert.Equal("nfs", (string?)production.Model["volumes"]!["nfs"]!["driver_opts"]!["type"]);
    }

    // ---- The review's findings (D-1 … D-6) ----

    [Fact]
    public void D2_a_provider_service_is_refused_and_a_preview_keeps_only_the_keys_it_knows()
    {
        // provider: Compose would run its type as a program where it runs (type: sh runs the repo's ./compose), in production too.
        var provider = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web"},
            "evil":{"image":"alpine","provider":{"type":"sh","options":{"x":"y"}}}
            """), Target());
        Assert.Contains(provider.Problems, p => p.StartsWith("The service evil is a provider service (provider: sh)", StringComparison.Ordinal));
        Assert.NotEmpty(ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web","provider":{"type":"model"}}
            """), Target(preview: true) with { Slug = "shop-pr-1" }).Problems);

        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web","post_start":[{"command":["sh","-c","id"],"privileged":true,"user":"root"}],"pre_stop":[{"command":["true"]}],
              "use_api_socket":true,"runtime":"runc","logging":{"driver":"syslog","options":{"syslog-address":"unix:///dev/log"}},"frobnicate":1,"x-mine":{"a":1},
              "secrets":["proxy","token"]}
            """, """
            "models":{"m":{"model":"ai/x"}},"include":[],
            "secrets":{"proxy":{"environment":"HTTPS_PROXY"},"token":{"environment":"API_TOKEN"}}
            """), Target(preview: true) with { Slug = "shop-pr-1", Variables = new HashSet<string> { "API_TOKEN" } });
        Assert.Empty(plan.Problems);
        var web = (JsonObject)plan.Model["services"]!["web"]!;
        foreach (var gone in new[] { "post_start", "pre_stop", "use_api_socket", "runtime", "logging", "frobnicate" }) Assert.False(web.ContainsKey(gone), gone);
        Assert.True(web.ContainsKey("x-mine"));
        Assert.False(plan.Model.ContainsKey("models"));
        Assert.False(plan.Model.ContainsKey("include"));
        // A secret from a variable that isn't the app's (Gitberth's proxy, say) is the preview's no more; the app's own stays.
        Assert.Equal("""{"token":{"environment":"API_TOKEN"}}""", plan.Model["secrets"]!.ToJsonString());
        Assert.Equal("""["token"]""", web["secrets"]!.ToJsonString());
        Assert.Contains(plan.Notes, n => n.Contains("post_start hooks", StringComparison.Ordinal) && n.Contains("the Docker API socket", StringComparison.Ordinal));
        Assert.Contains("No secret proxy: it comes from HTTPS_PROXY, which isn't one of the app's variables.", plan.Notes);

        // Production keeps its owner's hooks.
        var production = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web","post_start":[{"command":["true"],"privileged":true}]}
            """), Target());
        Assert.True(((JsonObject)production.Model["services"]!["web"]!).ContainsKey("post_start"));
    }

    [Fact]
    public void D1_a_preview_never_joins_productions_network_or_volume_by_naming_it()
    {
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"acme/web","volumes":[{"type":"volume","source":"data","target":"/data"}]}
            """, """
            "networks":{"default":{"name":"gb-shop_default"},"back":{"name":"gb-shop-pr-1_back"}},
            "volumes":{"data":{"name":"gb-shop_data"},"cache":{"name":"gb-shop-pr-1_cache"}}
            """), Target(preview: true) with { Slug = "shop-pr-1" });
        Assert.Empty(plan.Problems);
        // Production's names go (Compose names them gb-shop-pr-1_default and _data); the preview's own stay.
        Assert.Equal("{}", plan.Model["networks"]!["default"]!.ToJsonString());
        Assert.Equal("""{"name":"gb-shop-pr-1_back"}""", plan.Model["networks"]!["back"]!.ToJsonString());
        Assert.Equal("{}", plan.Model["volumes"]!["data"]!.ToJsonString());
        Assert.Equal("""{"name":"gb-shop-pr-1_cache"}""", plan.Model["volumes"]!["cache"]!.ToJsonString());
        Assert.Contains("The network default is the preview's own, not gb-shop_default: its services would answer to production's names there.", plan.Notes);
    }

    [Fact]
    public async Task D5_gitberth_never_reads_or_writes_through_a_link_in_a_repo()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("gb-envlink").FullName;
        try
        {
            var release = Path.Combine(root, "abc1234");
            Directory.CreateDirectory(release);
            var secret = Path.Combine(root, "gitberth.db");
            File.WriteAllText(secret, "GITBERTH_ADMIN_PASSWORD=hunter2");
            // A pull request's .env that leads to Gitberth's own files (its environment, its database) is refused.
            File.CreateSymbolicLink(Path.Combine(release, ".gitberth-repo.env"), secret);
            var refused = await Assert.ThrowsAsync<DeployException>(() => DeployPipeline.RepoEnvAsync(release, release));
            Assert.Contains("link out of the repo", refused.Message);
            Assert.DoesNotContain("hunter2", refused.Message);
            // One that stays in the repo is read.
            File.Delete(Path.Combine(release, ".gitberth-repo.env"));
            File.WriteAllText(Path.Combine(release, ".env.example"), "A=1\n");
            File.CreateSymbolicLink(Path.Combine(release, ".gitberth-repo.env"), ".env.example");
            Assert.Equal("A=1\n", await DeployPipeline.RepoEnvAsync(release, release));

            // A file Gitberth writes into a release (its .env, the file a deploy runs) replaces a link there, never writes through it.
            File.CreateSymbolicLink(Path.Combine(release, "gitberth-12.json"), secret);
            File.CreateSymbolicLink(Path.Combine(release, ".env"), Path.Combine(root, "nowhere", "planted"));
            await DeployPipeline.WriteOwnFileAsync(Path.Combine(release, "gitberth-12.json"), "{}");
            await DeployPipeline.WriteOwnFileAsync(Path.Combine(release, ".env"), "B=2\n");
            Assert.Equal("GITBERTH_ADMIN_PASSWORD=hunter2", File.ReadAllText(secret));
            Assert.Null(new FileInfo(Path.Combine(release, "gitberth-12.json")).LinkTarget);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(release, "gitberth-12.json")));
            Assert.False(Directory.Exists(Path.Combine(root, "nowhere")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(release, ".env")));

            // The app's kept folder, which its containers write: a link one of them made there is left alone.
            var kept = Path.Combine(root, "data");
            Directory.CreateDirectory(kept);
            File.WriteAllText(Path.Combine(release, "conf.ini"), "new");
            File.CreateSymbolicLink(Path.Combine(kept, "conf.ini"), secret);
            DeployPipeline.SyncFile(Path.Combine(release, "conf.ini"), Path.Combine(kept, "conf.ini"), null);
            Assert.Equal("GITBERTH_ADMIN_PASSWORD=hunter2", File.ReadAllText(secret));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void D3_a_release_records_the_images_it_ran_and_runs_them_again_once_their_tag_moved()
    {
        var ran = new ComposeImages(new Dictionary<string, string> { ["web"] = "gitberth/shop/web:abc1234", ["cache"] = "redis:8-alpine" },
            new Dictionary<string, string> { ["web"] = "sha256:1111111111111111aaaa", ["cache"] = "sha256:2222222222222222bbbb" });
        var again = ComposeImages.Parse(ran.ToJson());
        Assert.Equal(ran.Refs, again.Refs);
        Assert.Equal(ran.Ids, again.Ids);
        // The format from before: the refs alone.
        Assert.Equal("redis:8-alpine", ComposeImages.Parse("""{"web":"gitberth/shop/web:abc1234","cache":"redis:8-alpine"}""").Refs["cache"]);
        Assert.Equal("redis:8-alpine", DeployPipeline.ParseImages(ran.ToJson())["cache"]);

        // redis:8-alpine names another image now: the cache runs by the app's own tag for the one it ran; a built image never moves.
        Assert.Equal(new Dictionary<string, string> { ["cache"] = "gitberth/shop/cache:pin-222222222222" },
            ran.Moved("shop", image => image == "redis:8-alpine" ? "sha256:9999" : "sha256:other"));
        Assert.Empty(ran.Moved("shop", image => image == "redis:8-alpine" ? "sha256:2222222222222222bbbb" : null));
        Assert.Equal(["gitberth/shop/cache:pin-222222222222"], ran.PinTags("shop"));

        // The transform runs the pin, and keeps the tag as what the release names.
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"gitberth/shop/web:abc1234"},
            "cache":{"image":"redis:8-alpine"}
            """), Target() with { Pins = new Dictionary<string, string> { ["cache"] = "gitberth/shop/cache:pin-222222222222" } });
        Assert.Equal("gitberth/shop/cache:pin-222222222222", (string?)plan.Model["services"]!["cache"]!["image"]);
        Assert.Equal("redis:8-alpine", plan.Images["cache"]);
    }

    [Fact]
    public void D6_deleting_a_preview_never_removes_what_it_pulled_and_no_app_removes_another_apps_images()
    {
        var used = new ComposeImages(new Dictionary<string, string>
        {
            ["web"] = "gitberth/shop-pr-1/web:abc1234", ["sleeper"] = "gitberth/shop/web:0ld0000", ["cache"] = "redis:8-alpine", ["db"] = "postgres:18",
        }, new Dictionary<string, string>());
        IReadOnlyList<string> own = ["gitberth/shop-pr-1/web:abc1234", "gitberth/shop-pr-1/cache:pin-222222222222"];
        var ids = new Dictionary<string, string?> { ["redis:8-alpine"] = "sha256:r", ["postgres:18"] = "sha256:p" };
        var preview = new AppDefinition { Slug = "shop-pr-1", ParentAppId = 7 };
        Assert.Equal(own, AppActions.ImagesToRemove(preview, own, used, new HashSet<string>(), ids, new HashSet<string>()));

        // An app of its own: its tags, and a pulled image nothing else needs; never gitberth/shop/… (another app's build), nor
        // one another app keeps (by tag or ID), nor one a container was made from.
        var app = new AppDefinition { Slug = "shop-pr-1" };
        Assert.Equal([.. own, "redis:8-alpine"], AppActions.ImagesToRemove(app, own, used, new HashSet<string>(), ids, new HashSet<string> { "sha256:p" }));
        Assert.Equal(own, AppActions.ImagesToRemove(app, own, used, new HashSet<string> { "sha256:r", "postgres:18" }, ids, new HashSet<string>()));

        // A preview can't run another app's build (gitberth/shop/…, which may hold its build-time values) or an image by its ID.
        var plan = ComposeTransform.Apply(Resolved("""
            "web":{"image":"gitberth/shop-pr-1/web:abc1234"},
            "sleeper":{"image":"gitberth/shop/web:0ld0000"},
            "byid":{"image":"89749bc4820a"},
            "from":{"build":{"context":"/data/compose/shop/abc1234","additional_contexts":{"base":"docker-image://gitberth/shop:abc"}}}
            """), Target(preview: true) with { Slug = "shop-pr-1" });
        Assert.Equal(3, plan.Problems.Count);
        Assert.Contains("The service sleeper runs gitberth/shop/web:0ld0000, an image Gitberth built for another app, which a preview never runs.", plan.Problems);
        Assert.Contains(plan.Problems, p => p.StartsWith("The service byid runs 89749bc4820a, an image ID", StringComparison.Ordinal));
        Assert.Contains(plan.Problems, p => p.StartsWith("The service from's build context base is gitberth/shop:abc", StringComparison.Ordinal));
    }

    [Fact]
    public void D4_a_compose_run_never_covers_another_project_that_only_starts_the_same()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var expected = new ExpectedChanges { Now = () => now };
        var buffer = new OutsideChangeBuffer(expected, _ => false);
        DockerChange Change(string type, string action, string id, string name, params (string, string)[] attrs) =>
            new(now, type, action, id, name, attrs.ToDictionary(a => a.Item1, a => a.Item2, StringComparer.Ordinal));
        using (expected.ForCli(ComposeRunner.Args("gb-shop", "/data/compose/shop/abc1234/gitberth-42.json", null, ["up", "-d", "--wait"])))
        {
            buffer.Feed(Change("container", "create", "c1", "gb-shop_web", ("name", "gb-shop_web"), ("com.docker.compose.project", "gb-shop")), now);
            // Someone else's, meanwhile: another app whose name starts the same, and production's preview.
            buffer.Feed(Change("container", "stop", "c2", "gb-shopify_web", ("name", "gb-shopify_web"), ("com.docker.compose.project", "gb-shopify")), now);
            buffer.Feed(Change("container", "destroy", "c3", "gb-shop-pr-1_web", ("name", "gb-shop-pr-1_web"), ("com.docker.compose.project", "gb-shop-pr-1")), now);
            buffer.Feed(Change("container", "create", "c4", "0123456789ab_gb-shopify_web", ("name", "0123456789ab_gb-shopify_web")), now);
            buffer.Feed(Change("network", "destroy", "n9", "gb-shopify_default", ("name", "gb-shopify_default")), now);
        }
        Assert.Equal(["0123456789ab_gb-shopify_web", "gb-shop-pr-1_web", "gb-shopify_default", "gb-shopify_web"],
            buffer.Flush(now.AddMinutes(2)).Select(o => o.Container ?? o.Target).Order(StringComparer.Ordinal).ToList());
        Assert.Empty(ComposeRunner.NamedIn(JsonNode.Parse("""{"name":"gb-shop","networks":{"x":{"name":"gb-shop_x"}}}""") as JsonObject).Networks);
        Assert.Equal(["gb-shopify_y"], ComposeRunner.NamedIn(JsonNode.Parse("""{"name":"gb-shop","volumes":{"y":{"name":"gb-shopify_y"}}}""") as JsonObject).Volumes);
    }

    [Fact]
    public void Every_service_joins_gitberths_network_only_when_the_app_asks_and_only_the_web_one_has_its_name()
    {
        var model = (JsonObject)JsonNode.Parse("""
            {"name":"gb-shop","services":{
              "web":{"container_name":"gb-shop_web"},"worker":{"container_name":"gb-shop_worker"},
              "sidecar":{"container_name":"gb-shop_sidecar","network_mode":"service:web"}},
             "x-gitberth":{"web":"web"}}
            """)!;
        Assert.Equal([new SharedJoin("web", "gb-shop_web", "shop", true), new SharedJoin("worker", "gb-shop_worker", null, true)],
            ComposeTransform.SharedJoins(model, "shop", everyService: true));
        // Off: only the web service; another that's there leaves.
        Assert.Equal([new SharedJoin("web", "gb-shop_web", "shop", true), new SharedJoin("worker", "gb-shop_worker", null, false)],
            ComposeTransform.SharedJoins(model, "shop", everyService: false));
        Assert.Equal("compose.everyServiceShared.7", ComposeNames.EveryServiceSharedKey(7));
    }

    [Fact]
    public void A_link_in_the_repo_never_leads_a_bind_or_a_build_out_of_the_release()
    {
        // Links out of the release, as the disk would say: ./etc leads to the host's /etc, ./data to the release's own folder.
        static bool Leads(string release, string path) => path.StartsWith($"{Release}/etc", StringComparison.Ordinal);
        var plan = ComposeTransform.Apply(Resolved($$$"""
            "web":{"image":"nginx","volumes":[{"type":"bind","source":"{{{Release}}}/etc","target":"/host-etc","read_only":true},
              {"type":"bind","source":"{{{Release}}}/data","target":"/data"}]},
            "api":{"build":{"context":"{{{Release}}}/etc"}}
            """), Target(relative: new HashSet<(string, string)> { ("web", "/host-etc"), ("web", "/data") }) with { LinksOut = Leads });
        Assert.Equal(2, plan.Problems.Count);
        Assert.Contains("The service web mounts ./etc at /host-etc, which is a link out of the repo. A relative path in the Compose file stays inside it; write an absolute path for a folder of the host.",
            plan.Problems);
        Assert.Contains("The service api builds from ./etc, outside the repo. Gitberth builds from the repo's own files only.", plan.Problems);

        // The disk's own answer, with real links (Linux, where Gitberth runs).
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("gb-links").FullName;
        try
        {
            var release = Path.Combine(root, "abc1234");
            Directory.CreateDirectory(Path.Combine(release, "conf"));
            File.CreateSymbolicLink(Path.Combine(release, "etc"), "/etc");
            File.CreateSymbolicLink(Path.Combine(release, "conf2"), "conf");
            File.CreateSymbolicLink(Path.Combine(release, "up"), "../..");
            File.CreateSymbolicLink(Path.Combine(release, "conf", "back"), "../conf2");
            Assert.False(ComposeTransform.LinksOut(release, Path.Combine(release, "conf")));
            Assert.False(ComposeTransform.LinksOut(release, Path.Combine(release, "conf2", "x.conf")));
            Assert.False(ComposeTransform.LinksOut(release, Path.Combine(release, "conf", "back")));
            Assert.False(ComposeTransform.LinksOut(release, Path.Combine(release, "missing", "folder")));
            Assert.True(ComposeTransform.LinksOut(release, Path.Combine(release, "etc")));
            Assert.True(ComposeTransform.LinksOut(release, Path.Combine(release, "etc", "shadow")));
            Assert.True(ComposeTransform.LinksOut(release, Path.Combine(release, "up", "keys")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- An inline app from a pasted file ----

    private const string Pasted = """
        services:
          site:
            image: nginx:1.27
            ports: ["8080:80", "8443:443"]
            environment:
              TITLE: ${TITLE:-Home}
          cache:
            image: redis:8-alpine
        """;

    [Fact]
    public async Task A_pasted_file_with_several_services_is_one_inline_app_with_its_variables()
    {
        using var rig = new AppsRig();
        var log = new AuditLog();
        var compose = new ComposeApps(rig.Db, rig.Apps, rig.Actions, rig.Protector, rig.Cli, rig.Health, rig.Stats, rig.Options, NullLogger<ComposeApps>.Instance, audit: log);
        var created = await compose.CreateInlineAsync(new InlineComposeApp("Home page", Pasted, "site", 80,
            [new ComposeValue("TITLE", "Welcome"), new ComposeValue("API_TOKEN", "s3cret-value")]), deploy: false);
        Assert.Equal("gb-home-page_site", created.Key);
        Assert.Null(created.DeploymentId);
        using var db = rig.Db.CreateDbContext();
        var app = db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Single(a => a.Id == created.AppId);
        Assert.Equal((AppSource.Compose, "site", "gb-home-page_site", true), (app.Source, app.WebService, app.Container, app.Deployed));
        Assert.True(app.IsCompose && !app.FromRepo);
        Assert.Equal(Pasted, rig.Protector.TryUnprotect(app.ComposeYamlProtected!));
        Assert.DoesNotContain("nginx", app.ComposeYamlProtected);
        Assert.Equal(["80/tcp:Lan:8080:True", "443/tcp:Lan:8443:False"],
            app.Ports.OrderBy(p => p.Id).Select(p => $"{p.ContainerPort}/{p.Protocol}:{p.Access}:{p.HostPort}:{p.IsWeb}"));
        Assert.Equal(["API_TOKEN:True", "TITLE:False"], app.EnvVars.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.IsSecret}"));
        var entry = log.Entries.Single();
        Assert.Equal(("app.created", "Created Home page from a Compose file with 2 services."), (entry.Action, entry.Summary));
        Assert.DoesNotContain(entry.Changes!, c => (c.To ?? "").Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_cant_be_one_app_is_refused_with_why()
    {
        Assert.Empty(ComposeApps.Problems(Pasted, "site"));
        Assert.Equal(["There's no service web in the file (it has site, cache)."], ComposeApps.Problems(Pasted, "web"));
        Assert.Contains("asks for 2 containers", ComposeApps.Problems("services:\n  web:\n    image: a\n    scale: 2\n", "web").Single());
        using var rig = new AppsRig();
        var compose = new ComposeApps(rig.Db, rig.Apps, rig.Actions, rig.Protector, rig.Cli, rig.Health, rig.Stats, rig.Options, NullLogger<ComposeApps>.Instance);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => compose.CreateInlineAsync(new InlineComposeApp("x", Pasted, "nope", 80, []), deploy: false));
        Assert.StartsWith("There's no service nope in the file", ex.Message);
    }
}
