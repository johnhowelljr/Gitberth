using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// A container's settings read from Docker's JSON and written into a create body (section 2.1 of the unified design),
/// against the inspects captured from the host: what the image supplies is told apart, writing a field touches only its
/// paths, writing nothing changes nothing, and what's written reads back the same.
/// </summary>
public sealed class ContainerSettingsJsonTests
{
    public static readonly TheoryData<string> Fixtures =
        ["postgres", "cloudflare-tunnel", "gitberth", "gb-orb", "gb-kanban", "sample-bun-elysia", "sample-bun-hono", "sample-index"];

    private static JsonArray Networks() => DockerFixtures.Json("networks-json.json").AsArray();

    private static JsonObject Build(JsonObject inspect, JsonObject? image) =>
        CloneSpec.Build(inspect, new JsonObject { ["Id"] = (string)inspect["Image"]! }, ContainerPorts.Rules(inspect), Networks(), "", image);

    /// <summary>The image each fixture runs, as far as its own config goes: postgres's captured, the others' as their base images set them.</summary>
    internal static JsonObject? ImageOf(string container) => container switch
    {
        "postgres" => DockerFixtures.Json("image-postgres.json").AsObject(),
        "sample-bun-elysia" or "sample-bun-hono" => BunImage(),
        "sample-index" => NginxImage(),
        _ => null,
    };

    internal static JsonObject BunImage() => Image("sha256:" + new string('b', 64), new JsonObject
    {
        ["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/local/bun-node-fallback-bin",
            "BUN_RUNTIME_TRANSPILER_CACHE_PATH=0", "BUN_INSTALL_BIN=/usr/local/bin"),
        ["Entrypoint"] = new JsonArray("/usr/local/bin/docker-entrypoint.sh"),
        ["Cmd"] = new JsonArray("/bin/sh"),
        ["WorkingDir"] = "/home/bun/app",
        ["Labels"] = new JsonObject
        {
            ["org.opencontainers.image.created"] = "2026-10-22T10:15:16.829Z",
            ["org.opencontainers.image.description"] = "Incredibly fast JavaScript runtime, bundler, test runner, and package manager – all in one",
            ["org.opencontainers.image.licenses"] = "NOASSERTION",
            ["org.opencontainers.image.revision"] = "38afee6c84fcf48c0edb2466aa4b27d287a59c30",
            ["org.opencontainers.image.source"] = "https://github.com/oven-sh/bun",
            ["org.opencontainers.image.title"] = "bun",
            ["org.opencontainers.image.url"] = "https://github.com/oven-sh/bun",
            ["org.opencontainers.image.version"] = "1.4.4",
        },
    });

    internal static JsonObject NginxImage() => Image("sha256:0e52bd054126cdc040b1276c0af347aeb02a41b9cfe305d6094767ac30e97949", new JsonObject
    {
        ["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "NGINX_VERSION=1.30.6", "PKG_RELEASE=1", "DYNPKG_RELEASE=1",
            "NJS_VERSION=1.0.2", "NJS_RELEASE=1", "ACME_VERSION=0.4.1"),
        ["Entrypoint"] = new JsonArray("/docker-entrypoint.sh"),
        ["Cmd"] = new JsonArray("nginx", "-g", "daemon off;"),
        ["ExposedPorts"] = new JsonObject { ["80/tcp"] = new JsonObject() },
        ["Labels"] = new JsonObject { ["maintainer"] = "NGINX Docker Maintainers <docker-maint@nginx.com>" },
        ["StopSignal"] = "SIGQUIT",
    });

    internal static JsonObject Image(string id, JsonObject config) => new() { ["Id"] = id, ["RepoTags"] = new JsonArray(), ["RepoDigests"] = new JsonArray(), ["Config"] = config };

    [Fact]
    public void Reads_postgres_with_its_own_variables_its_volume_and_its_health_check()
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect("postgres"), ImageOf("postgres"));

        Assert.Equal("postgres:18", s.Image);
        // The image's entrypoint, command and stop signal are its own: null.
        Assert.Null(s.Entrypoint);
        Assert.Null(s.Command);
        Assert.Null(s.StopSignal);
        Assert.Null(s.WorkingDir);
        Assert.Equal(["POSTGRES_PASSWORD"], s.Env.Where(e => !e.FromImage).Select(e => e.Key));
        Assert.True(s.Env.Single(e => e.Key == "POSTGRES_PASSWORD").Secret);
        Assert.Equal(["PATH", "GOSU_VERSION", "LANG", "PG_MAJOR", "PG_VERSION", "PGDATA"], s.Env.Where(e => e.FromImage).Select(e => e.Key));
        Assert.Equal([new MountSetting(MountKind.Volume, "postgres-data", "/var/lib/postgresql")], s.Mounts);
        Assert.Equal(new RestartSetting("unless-stopped"), s.Restart);
        Assert.Equal([new PortRule(5432, "tcp", PortAccess.Internal)], s.Ports);
        // Its network (gitdock, gone) is a bridge network: "".
        Assert.Equal("", s.NetworkMode);
        Assert.Equal("0ebf31bbcd07", s.Hostname);
        Assert.Equal(HealthMode.Custom, s.Health.Mode);
        Assert.Equal(["CMD-SHELL", "pg_isready -U postgres"], s.Health.Test);
        Assert.Equal((TimeSpan.FromSeconds(10), 5), (s.Health.Interval, s.Health.Retries));
        Assert.Equal(new ResourceSetting(), s.Resources);
        Assert.Null(s.ShmSizeBytes);
        Assert.Equal("journald", s.Logging.Driver);
    }

    [Fact]
    public void Reads_the_tunnel_on_the_hosts_network_with_its_command()
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect("cloudflare-tunnel"), null);

        Assert.Equal("host", s.NetworkMode);
        Assert.Equal(new RestartSetting("always"), s.Restart);
        Assert.Equal(["cloudflared"], s.Entrypoint);
        // The command holds the token: it's read as it is, and masked wherever it's shown.
        Assert.Equal(["tunnel", "--no-autoupdate", "run", "--token", "REDACTED"], s.Command);
        Assert.Equal(("65532:65532", "/home/nonroot"), (s.User, s.WorkingDir));
        Assert.Empty(s.Ports);
        Assert.DoesNotContain("REDACTED", string.Join(' ', Security.Masking.Args(s.Command!)));
    }

    [Fact]
    public void Reads_gitberths_compose_labels_and_its_mounts()
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect("gitberth"), null);

        Assert.Equal("gitberth", s.Labels["com.docker.compose.project"]);
        Assert.Equal("gitberth", s.Labels["com.docker.compose.service"]);
        Assert.Equal([new MountSetting(MountKind.Bind, "/var/run/docker.sock", "/var/run/docker.sock"), new MountSetting(MountKind.Volume, "gitberth_gitberth-data", "/data")],
            s.Mounts);
        Assert.Equal([new PortRule(8080, "tcp", PortAccess.Lan, 8080, "")], s.Ports);
        Assert.Equal("", s.NetworkMode);
    }

    [Theory]
    [InlineData("gb-orb", "orb", 3000)]
    [InlineData("gb-kanban", "kanban", 3001)]
    public void Reads_the_apps_containers(string container, string slug, int hostPort)
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect(container), null);

        Assert.Equal(slug, s.Labels["gitberth.app"]);
        Assert.Equal("true", s.Labels["gitberth.managed"]);
        Assert.Equal(hostPort, Assert.Single(s.Ports).HostPort);
        Assert.Equal(new RestartSetting("unless-stopped"), s.Restart);
        Assert.Equal("", s.NetworkMode);
        Assert.NotEmpty(s.Env);
    }

    [Theory]
    [InlineData("sample-bun-elysia", "fa8af48f1cd8")]
    [InlineData("sample-bun-hono", "a9d6a097c277")]
    public void Tells_a_samples_own_settings_from_its_images(string container, string hostname)
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect(container), ImageOf(container));

        // PATH and Bun's own variables come from the image; NODE_ENV, HOST and PORT are the container's.
        Assert.Equal(["PATH", "BUN_RUNTIME_TRANSPILER_CACHE_PATH", "BUN_INSTALL_BIN"], s.Env.Where(e => e.FromImage).Select(e => e.Key));
        Assert.Equal(["NODE_ENV", "HOST", "PORT"], s.Env.Where(e => !e.FromImage).Select(e => e.Key));
        // The image's labels are shown apart, read-only; the sample's own is the container's.
        Assert.Equal(["gitberth.sample"], s.Labels.Keys);
        Assert.Equal(8, s.ImageLabels.Count);
        // The hostname is kept as Docker has it, the short ID included.
        Assert.Equal(hostname, s.Hostname);
        // Its command and working folder are its own; its entrypoint is the image's.
        Assert.Null(s.Entrypoint);
        Assert.Equal(["bun", "run", "src/index.ts"], s.Command);
        Assert.Equal("/app", s.WorkingDir);
        // 1 GiB, with Docker's own swap (twice the limit): no swap limit of its own.
        Assert.Equal(new ResourceSetting(MemoryBytes: 1L << 30), s.Resources);
    }

    [Fact]
    public void Reads_the_nginx_sample_with_its_images_command_and_stop_signal()
    {
        var s = ContainerSettingsJson.Read(DockerFixtures.Inspect("sample-index"), ImageOf("sample-index"));

        Assert.Null(s.Entrypoint);
        Assert.Null(s.Command);
        Assert.Null(s.StopSignal);
        Assert.All(s.Env, e => Assert.True(e.FromImage));
        Assert.Equal(["gitberth.sample"], s.Labels.Keys);
        Assert.Equal("19b5a0b05faa", s.Hostname);
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 9100, "0.0.0.0")], s.Ports);
        Assert.Equal(64L << 20, s.Resources.MemoryBytes);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Writing_nothing_gives_the_clone_as_it_is(string container)
    {
        // The key property: "edit nothing" gives exactly the body a port change makes today.
        var inspect = DockerFixtures.Inspect(container);
        var clone = Build(inspect, ImageOf(container));
        var body = (JsonObject)clone.DeepClone();

        ContainerSettingsJson.Write(body, ContainerSettingsJson.Read(inspect, ImageOf(container)), new HashSet<SettingField>());

        Assert.Equal(clone.ToJsonString(), body.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_field_written_reads_back_as_it_was_read(string container)
    {
        var inspect = DockerFixtures.Inspect(container);
        var image = ImageOf(container);
        var read = ContainerSettingsJson.Read(inspect, image);
        var body = Build(inspect, image);

        ContainerSettingsJson.Write(body, read, Enum.GetValues<SettingField>().ToHashSet());

        Assert.Empty(SettingsDiff.Changed(read, ContainerSettingsJson.ReadBody(body, image)));
    }

    // A value of each field other than postgres's.
    public static readonly TheoryData<SettingField> AllFields = new(Enum.GetValues<SettingField>());

    internal static ContainerSettings Changed(ContainerSettings s, SettingField field) => field switch
    {
        SettingField.Image => s with { Image = "postgres:19" },
        SettingField.Entrypoint => s with { Entrypoint = ["/bin/sh", "-c"] },
        SettingField.Command => s with { Command = ["postgres", "-c", "max_connections=200"] },
        SettingField.WorkingDir => s with { WorkingDir = "/srv" },
        SettingField.User => s with { User = "999:999" },
        SettingField.Env => s with { Env = [.. s.Env, new EnvSetting("TZ", "UTC")] },
        SettingField.Ports => s with { Ports = [new PortRule(5432, "tcp", PortAccess.Lan, 5432)] },
        SettingField.Mounts => s with { Mounts = [.. s.Mounts, new MountSetting(MountKind.Tmpfs, "", "/scratch", TmpfsBytes: 64L << 20)] },
        SettingField.NetworkMode => s with { NetworkMode = "host" },
        SettingField.Hostname => s with { Hostname = "db", Domainname = "lan" },
        SettingField.ExtraHosts => s with { ExtraHosts = ["db.lan:10.1.1.5"] },
        SettingField.Dns => s with { Dns = ["10.1.1.1"], DnsSearch = ["lan"] },
        SettingField.RestartPolicy => s with { Restart = new RestartSetting("always") },
        SettingField.Memory => s with { Resources = s.Resources with { MemoryBytes = 512L << 20 } },
        SettingField.Cpu => s with { Resources = s.Resources with { Cpus = 1.5 } },
        SettingField.PidsLimit => s with { Resources = s.Resources with { PidsLimit = 200 } },
        SettingField.ShmSize => s with { ShmSizeBytes = 256L << 20 },
        SettingField.Ulimits => s with { Ulimits = [new UlimitSetting("nofile", 1024, 4096)] },
        SettingField.Health => s with { Health = new HealthSetting(HealthMode.Disabled) },
        SettingField.Labels => s with { Labels = new Dictionary<string, string>(s.Labels) { ["team"] = "data" } },
        SettingField.Devices => s with { Devices = [new DeviceSetting("/dev/fuse", "/dev/fuse")] },
        SettingField.Gpus => s with { Gpus = "all" },
        SettingField.Capabilities => s with { CapAdd = ["NET_ADMIN"] },
        SettingField.Privileged => s with { Privileged = true },
        SettingField.SecurityOpt => s with { SecurityOpt = ["no-new-privileges"] },
        SettingField.ReadOnlyRootfs => s with { ReadOnlyRootfs = true },
        SettingField.Init => s with { Init = true },
        SettingField.Sysctls => s with { Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" } },
        SettingField.Logging => s with { Logging = new LogSetting("json-file", new Dictionary<string, string> { ["max-size"] = "10m" }) },
        SettingField.Stop => s with { StopSignal = "SIGTERM", StopTimeoutSeconds = 30 },
        SettingField.Terminal => s with { Tty = true, OpenStdin = true },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Theory]
    [MemberData(nameof(AllFields))]
    public void Each_field_writes_exactly_its_paths(SettingField field)
    {
        var inspect = DockerFixtures.Inspect("postgres");
        var image = ImageOf("postgres");
        var before = Build(inspect, image);
        var body = (JsonObject)before.DeepClone();
        var wanted = Changed(ContainerSettingsJson.Read(inspect, image), field);
        Assert.Equal([field], SettingsDiff.Changed(ContainerSettingsJson.Read(inspect, image), wanted));

        ContainerSettingsJson.Write(body, wanted, new HashSet<SettingField> { field });

        var changed = Differences(before, body);
        Assert.NotEmpty(changed);
        Assert.All(changed, path => Assert.Contains(path, ContainerSettingsJson.Paths[field]));
        // And it reads back as asked.
        Assert.DoesNotContain(field, SettingsDiff.Changed(wanted, ContainerSettingsJson.ReadBody(body, image)));
    }

    // The paths whose values differ: Config's keys at the top, HostConfig's as HostConfig.Key, and NetworkingConfig whole.
    private static List<string> Differences(JsonObject a, JsonObject b)
    {
        var paths = new List<string>();
        foreach (var key in a.Select(kv => kv.Key).Union(b.Select(kv => kv.Key)).Where(k => k != "HostConfig"))
            if (!JsonNode.DeepEquals(a[key], b[key])) paths.Add(key);
        var ha = a["HostConfig"] as JsonObject ?? [];
        var hb = b["HostConfig"] as JsonObject ?? [];
        foreach (var key in ha.Select(kv => kv.Key).Union(hb.Select(kv => kv.Key)))
            if (!JsonNode.DeepEquals(ha[key], hb[key])) paths.Add("HostConfig." + key);
        return paths;
    }

    [Fact]
    public void Writes_mounts_in_the_form_each_target_had()
    {
        var inspect = DockerFixtures.Inspect("postgres");
        var body = Build(inspect, ImageOf("postgres"));
        var s = ContainerSettingsJson.Read(inspect, ImageOf("postgres"));

        ContainerSettingsJson.Write(body, s with
        {
            Mounts =
            [
                .. s.Mounts,
                new MountSetting(MountKind.Bind, "/srv/backups", "/backups", ReadOnly: true),
                new MountSetting(MountKind.Volume, "pg-wal", "/wal"),
                new MountSetting(MountKind.Tmpfs, "", "/scratch", TmpfsBytes: 64L << 20),
            ],
        }, new HashSet<SettingField> { SettingField.Mounts });

        var host = body["HostConfig"]!;
        // The volume postgres had in Binds stays there; a new bind is a Binds string, so a missing folder is made as -v does.
        Assert.Equal("""["postgres-data:/var/lib/postgresql","/srv/backups:/backups:ro"]""", host["Binds"]!.ToJsonString());
        Assert.Equal("""[{"Type":"volume","Source":"pg-wal","Target":"/wal","ReadOnly":false}]""", host["Mounts"]!.ToJsonString());
        Assert.Equal("""{"/scratch":"size=67108864"}""", host["Tmpfs"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_the_options_it_doesnt_model_at_each_target()
    {
        // A bind with SELinux relabelling, a volume with options of its own, a tmpfs with its mode.
        var inspect = DockerFixtures.Inspect("sample-index");
        var host = inspect["HostConfig"]!.AsObject();
        host["Binds"] = new JsonArray("/srv/site:/usr/share/nginx/html:ro,z");
        host["Mounts"] = JsonNode.Parse("""[{"Type":"volume","Source":"cache","Target":"/cache","VolumeOptions":{"NoCopy":true}}]""");
        host["Tmpfs"] = new JsonObject { ["/run/nginx"] = "size=1048576,mode=1777" };
        var body = Build(inspect, ImageOf("sample-index"));
        var s = ContainerSettingsJson.Read(inspect, ImageOf("sample-index"));
        Assert.Equal(
        [
            new MountSetting(MountKind.Bind, "/srv/site", "/usr/share/nginx/html", ReadOnly: true),
            new MountSetting(MountKind.Volume, "cache", "/cache"),
            new MountSetting(MountKind.Tmpfs, "", "/run/nginx", TmpfsBytes: 1048576),
        ], s.Mounts);

        ContainerSettingsJson.Write(body, s with
        {
            Mounts = [s.Mounts[0] with { ReadOnly = false }, s.Mounts[1] with { ReadOnly = true }, s.Mounts[2] with { TmpfsBytes = 2097152 }],
        }, new HashSet<SettingField> { SettingField.Mounts });

        var written = body["HostConfig"]!;
        Assert.Equal("""["/srv/site:/usr/share/nginx/html:z"]""", written["Binds"]!.ToJsonString());
        Assert.Equal("""[{"Type":"volume","Source":"cache","Target":"/cache","VolumeOptions":{"NoCopy":true},"ReadOnly":true}]""", written["Mounts"]!.ToJsonString());
        Assert.Equal("""{"/run/nginx":"size=2097152,mode=1777"}""", written["Tmpfs"]!.ToJsonString());
    }

    [Fact]
    public void Carries_what_it_has_no_field_for()
    {
        var inspect = DockerFixtures.Inspect("sample-bun-hono");
        var host = inspect["HostConfig"]!.AsObject();
        host["VolumesFrom"] = new JsonArray("data-only");
        host["Runtime"] = "runc";
        host["CgroupParent"] = "/gitberth";
        host["OomScoreAdj"] = 100;
        host["IpcMode"] = "shareable";
        host["GroupAdd"] = new JsonArray("audio");
        host["DeviceCgroupRules"] = new JsonArray("c 13:* rmw");
        host["BlkioWeight"] = 300;
        var body = Build(inspect, ImageOf("sample-bun-hono"));
        var expected = new[] { "VolumesFrom", "Runtime", "CgroupParent", "OomScoreAdj", "IpcMode", "GroupAdd", "DeviceCgroupRules", "BlkioWeight" }
            .ToDictionary(k => k, k => body["HostConfig"]![k]!.ToJsonString());

        ContainerSettingsJson.Write(body, ContainerSettingsJson.Read(inspect, ImageOf("sample-bun-hono")), Enum.GetValues<SettingField>().ToHashSet());

        Assert.All(expected, kv => Assert.Equal(kv.Value, body["HostConfig"]![kv.Key]!.ToJsonString()));
    }

    [Fact]
    public void A_network_mode_switch_clears_the_endpoints_and_back_puts_it_on_a_network()
    {
        // A sample on Docker's bridge goes onto the host's network: no endpoints, nothing published.
        var sample = DockerFixtures.Inspect("sample-index");
        var body = Build(sample, ImageOf("sample-index"));
        Assert.Equal(["bridge"], body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject().Select(e => e.Key));
        var s = ContainerSettingsJson.Read(sample, ImageOf("sample-index"));
        ContainerSettingsJson.Write(body, s with { NetworkMode = "host" }, new HashSet<SettingField> { SettingField.NetworkMode });
        Assert.Equal("host", (string)body["HostConfig"]!["NetworkMode"]!);
        Assert.Equal("{}", body["NetworkingConfig"]!["EndpointsConfig"]!.ToJsonString());
        Assert.Equal("{}", body["HostConfig"]!["PortBindings"]!.ToJsonString());

        // The tunnel comes off the host's network onto Docker's bridge; a Gitberth-made app onto the shared network, by its slug.
        var tunnel = DockerFixtures.Inspect("cloudflare-tunnel");
        var back = Build(tunnel, null);
        var t = ContainerSettingsJson.Read(tunnel, null);
        ContainerSettingsJson.Write(back, t with { NetworkMode = "" }, new HashSet<SettingField> { SettingField.NetworkMode });
        Assert.Equal(("bridge", """{"bridge":{}}"""), ((string)back["HostConfig"]!["NetworkMode"]!, back["NetworkingConfig"]!["EndpointsConfig"]!.ToJsonString()));

        var app = Build(tunnel, null);
        ContainerSettingsJson.Write(app, t with { NetworkMode = "", Labels = new Dictionary<string, string>(t.Labels) { ["gitberth.app"] = "tunnel" } },
            new HashSet<SettingField> { SettingField.NetworkMode });
        Assert.Equal(("gitberth", """{"gitberth":{"Aliases":["tunnel"]}}"""), ((string)app["HostConfig"]!["NetworkMode"]!, app["NetworkingConfig"]!["EndpointsConfig"]!.ToJsonString()));

        // Sharing another's network takes that one's hostname.
        var shared = Build(sample, ImageOf("sample-index"));
        ContainerSettingsJson.Write(shared, s with { NetworkMode = "container:vpn" }, new HashSet<SettingField> { SettingField.NetworkMode, SettingField.Hostname });
        Assert.False(shared.ContainsKey("Hostname"));
    }

    [Fact]
    public void A_new_body_has_every_network_the_ports_and_the_settings()
    {
        var settings = new ContainerSettings
        {
            Image = "postgres:18",
            Env = [new EnvSetting("POSTGRES_PASSWORD", "s3cret-pass")],
            Ports = [new PortRule(5432, "tcp", PortAccess.Lan, 5433), new PortRule(9187, "tcp", PortAccess.HostOnly, 9187), new PortRule(8080, "tcp", PortAccess.Internal)],
            Mounts = [new MountSetting(MountKind.Volume, "gb-pg-postgresql", "/var/lib/postgresql")],
            Labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "pg", ["gitberth.source"] = "registry" },
            Restart = new RestartSetting("unless-stopped"),
            Resources = new ResourceSetting(MemoryBytes: 1L << 30),
        };
        IReadOnlyList<NetworkJoin> networks = [new("gitberth", ["pg"]), new("media", ["db"], LowPriority: true), new("gitberth-lan", [], "10.1.1.240", LowPriority: true)];

        var body = ContainerSettingsJson.NewBody(settings, networks, "0.0.0.0", gatewayPriority: true);

        Assert.Equal("postgres:18", (string)body["Image"]!);
        Assert.Equal("gitberth", (string)body["HostConfig"]!["NetworkMode"]!);
        Assert.Equal("""{"gitberth":{"Aliases":["pg"]},"media":{"Aliases":["db"],"GwPriority":-1},"gitberth-lan":{"IPAMConfig":{"IPv4Address":"10.1.1.240"},"GwPriority":-1}}""",
            body["NetworkingConfig"]!["EndpointsConfig"]!.ToJsonString());
        Assert.Equal("""{"5432/tcp":[{"HostIp":"0.0.0.0","HostPort":"5433"}],"9187/tcp":[{"HostIp":"127.0.0.1","HostPort":"9187"}]}""",
            body["HostConfig"]!["PortBindings"]!.ToJsonString());
        Assert.Equal(["5432/tcp", "9187/tcp", "8080/tcp"], body["ExposedPorts"]!.AsObject().Select(p => p.Key));
        Assert.Equal("""[{"Type":"volume","Source":"gb-pg-postgresql","Target":"/var/lib/postgresql","ReadOnly":false}]""", body["HostConfig"]!["Mounts"]!.ToJsonString());
        Assert.Equal("""{"Name":"unless-stopped","MaximumRetryCount":0}""", body["HostConfig"]!["RestartPolicy"]!.ToJsonString());

        // An engine without gateway priorities gets none.
        Assert.DoesNotContain("GwPriority", ContainerSettingsJson.NewBody(settings, networks, "", gatewayPriority: false).ToJsonString());
        // And it reads back as it was set.
        Assert.Empty(SettingsDiff.Changed(settings, ContainerSettingsJson.ReadBody(body, null) with { Ports = settings.Ports }));
    }

    [Fact]
    public void The_docker_run_preview_masks_secrets_one_option_a_line()
    {
        var settings = new ContainerSettings
        {
            Image = "cloudflare/cloudflared:latest",
            Entrypoint = ["cloudflared"],
            Command = ["tunnel", "run", "--token", "eyJhIjoiYmMifQ"],
            Env = [new EnvSetting("DATABASE_URL", "postgres://app:hunter22@postgres/app"), new EnvSetting("API_KEY", "abc123456"), new EnvSetting("TZ", "UTC"),
                new EnvSetting("PATH", "/usr/bin", FromImage: true)],
            Ports = [new PortRule(5432, "tcp", PortAccess.Lan, 5432)],
            Restart = new RestartSetting("on-failure", 3),
        };

        var run = ContainerSettingsJson.DockerRun("gb-tunnel", settings, [new NetworkJoin("gitberth", ["tunnel"])], ["TZ"]);

        Assert.DoesNotContain("hunter22", run);
        Assert.DoesNotContain("abc123456", run);
        Assert.DoesNotContain("eyJhIjoiYmMifQ", run);
        Assert.DoesNotContain("UTC", run);
        Assert.DoesNotContain("PATH", run);
        Assert.StartsWith("docker run \\\n  -d \\\n  --name gb-tunnel \\\n  --restart on-failure:3 \\\n  --network name=gitberth,alias=tunnel \\\n  -p 5432:5432", run);
        Assert.EndsWith("cloudflare/cloudflared:latest tunnel run --token '••••'", run);
    }

    [Fact]
    public void Reads_an_images_facts()
    {
        var facts = ContainerSettingsJson.Facts(DockerFixtures.Json("image-postgres.json").AsObject());

        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", facts.Id);
        Assert.Equal(["postgres:18", "postgres:latest"], facts.Tags);
        // The image alone can't say a registry has it (RepoDigests are every image's in the containerd store): ImageService does.
        Assert.Equal(ImageOrigin.Local, facts.Origin);
        Assert.Equal([new ExposedPort(5432, "tcp")], facts.ExposedPorts);
        Assert.Equal(["/var/lib/postgresql"], facts.Volumes);
        Assert.Equal(["docker-entrypoint.sh"], facts.Entrypoint);
        Assert.Equal(["postgres"], facts.Command);
        Assert.Equal("SIGINT", facts.StopSignal);
        Assert.All(facts.Env, e => Assert.True(e.FromImage));
        Assert.Equal(644209868, facts.SizeBytes);
        Assert.Equal(("linux", "amd64"), (facts.Os, facts.Architecture));
    }
}
