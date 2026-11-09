using Gitberth.Apps;
using Gitberth.Network;
using Gitberth.Sources;

namespace Gitberth.Tests;

/// <summary>A pasted Compose service read as an app's settings: every key carried over, dropped with its reason, or an error.</summary>
public class ComposeParserTests
{
    private static ParsedApp Service(string body) => ComposeParser.Parse("services:\n  app:\n" + string.Join('\n', body.Replace("\r\n", "\n").Split('\n').Select(l => "    " + l)));

    private static IEnumerable<ParseIssue> Errors(ParsedApp app) => app.Issues.Where(i => i.Severity == ParseSeverity.Error);

    private static IEnumerable<ParseIssue> Dropped(ParsedApp app) => app.Issues.Where(i => i.Severity == ParseSeverity.Dropped);

    [Fact]
    public void Lists_the_services_of_a_file()
    {
        var read = ComposeParser.Read(ImageKit.Fixture("Paste", "paperless.yml"));

        Assert.Equal(["broker", "db", "webserver"], read.Services);
        Assert.Empty(read.Issues);
    }

    [Fact]
    public void Several_services_need_one_picked()
    {
        var app = ComposeParser.Parse(ImageKit.Fixture("Paste", "paperless.yml"));

        Assert.StartsWith("The paste has 3 services (broker, db, webserver): pick one.", Assert.Single(Errors(app)).Text);
        Assert.Contains(Errors(ComposeParser.Parse(ImageKit.Fixture("Paste", "paperless.yml"), "nope")), e => e.Text.StartsWith("There's no service nope", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_a_service_with_merge_keys_long_forms_and_top_level_names()
    {
        var app = ComposeParser.Parse(ImageKit.Fixture("Paste", "paperless.yml"), "webserver");
        var s = app.Settings;

        Assert.Equal("paperless", app.Name);
        Assert.Equal("ghcr.io/paperless-ngx/paperless-ngx:2.13", s.Image);
        Assert.Equal([new PortRule(8000, "tcp", PortAccess.Lan, 8000), new PortRule(9000, "tcp", PortAccess.HostOnly, 9100)], s.Ports);
        Assert.Equal(
        [
            new MountSetting(MountKind.Volume, "gb-paperless-data", "/usr/src/paperless/data"),
            new MountSetting(MountKind.Bind, "/srv/paperless/consume", "/usr/src/paperless/consume"),
            new MountSetting(MountKind.Bind, "/srv/paperless/export", "/usr/src/paperless/export", ReadOnly: true),
            new MountSetting(MountKind.Tmpfs, "", "/tmp/paperless", TmpfsBytes: 64L << 20),
        ], s.Mounts);
        Assert.Contains(app.Issues, i => i.Text.StartsWith("Compose would call the volume paperless_data; Gitberth calls it gb-<app>-data.", StringComparison.Ordinal));

        Assert.Equal(["PAPERLESS_REDIS", "PAPERLESS_DBHOST", "PAPERLESS_SECRET_KEY", "PAPERLESS_URL"], s.Env.Select(e => e.Key));
        // $$ is a $ the container sees.
        Assert.Equal("${NOT_INTERPOLATED}", s.Env.Single(e => e.Key == "PAPERLESS_SECRET_KEY").Value);
        Assert.True(s.Env.Single(e => e.Key == "PAPERLESS_SECRET_KEY").Secret);
        Assert.Equal("Give PAPERLESS_URL a value: Compose would take it from the shell or the project's .env, which Gitberth can't see.", Assert.Single(Errors(app)).Text);

        var backend = Assert.Single(app.Networks);
        Assert.Equal(("shared-backend", "paper"), (backend.Name, Assert.Single(backend.Aliases)));
        Assert.Contains(app.Issues, i => i.Text.StartsWith("networks: default: Gitberth puts it on its shared network", StringComparison.Ordinal));

        Assert.Equal(new ResourceSetting(1L << 30, 256L << 20, null, 1.5, null, null, 200), s.Resources);
        // From the anchor, through <<: *common.
        Assert.Equal(new RestartSetting("unless-stopped"), s.Restart);
        Assert.Equal("json-file", s.Logging.Driver);
        Assert.Equal("10m", s.Logging.Options!["max-size"]);

        Assert.Contains(Dropped(app), d => d.Text == "Not carried over: depends_on: create db and broker first. Once they're apps, this one reaches them by name on Gitberth's network.");
        Assert.Contains(Dropped(app), d => d.Fragment == "env_file");
        // x- keys are Compose's own extensions: passed over without a word.
        Assert.DoesNotContain(app.Issues, i => i.Text.Contains("x-notes", StringComparison.Ordinal));
    }

    [Fact]
    public void Takes_defaults_and_flags_variables_that_have_none()
    {
        var db = ComposeParser.Parse(ImageKit.Fixture("Paste", "paperless.yml"), "db");

        Assert.Equal("postgres:16", db.Settings.Image);
        Assert.Contains(db.Issues, i => i.Severity == ParseSeverity.Note && i.Text == "PG_VERSION isn't set here: it takes its default, 16.");
        Assert.Equal("DB_PASSWORD has no value here (it comes from the project's .env or the shell): fill it in.", Assert.Single(Errors(db)).Text);
        Assert.Equal(new EnvSetting("POSTGRES_PASSWORD", "", Secret: true), db.Settings.Env.Single(e => e.Key == "POSTGRES_PASSWORD"));
        Assert.Equal([new MountSetting(MountKind.Volume, "paperless_pgdata_real", "/var/lib/postgresql/data")], db.Settings.Mounts);
        var health = db.Settings.Health;
        Assert.Equal(HealthMode.Custom, health.Mode);
        Assert.Equal(["CMD-SHELL", "pg_isready -U paperless"], health.Test);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 5), (health.Interval!.Value, health.Timeout!.Value, health.Retries!.Value));

        var broker = ComposeParser.Parse(ImageKit.Fixture("Paste", "paperless.yml"), "broker");
        Assert.True(broker.Ok);
        Assert.Equal([new MountSetting(MountKind.Volume, "redisdata", "/data")], broker.Settings.Mounts);
    }

    [Theory]
    [InlineData("${A:-b}", "b", ParseSeverity.Note)]
    [InlineData("${A-b}", "b", ParseSeverity.Note)]
    [InlineData("${A}", "", ParseSeverity.Error)]
    [InlineData("$A", "", ParseSeverity.Error)]
    [InlineData("${A:?needs a token}", "", ParseSeverity.Error)]
    [InlineData("${A:+on}", "", ParseSeverity.Note)]
    [InlineData("${A:-${B:-c}}", "c", ParseSeverity.Note)]
    [InlineData("${1bad}", "", ParseSeverity.Error)]
    public void Replaces_variables_as_compose_would_with_nothing_set(string text, string becomes, ParseSeverity severity)
    {
        var issues = new List<ParseIssue>();

        Assert.Equal(becomes, ComposeParser.Interpolate(text, issues, []));
        Assert.Equal(severity, issues[0].Severity);
    }

    [Fact]
    public void Dollars_and_messages_of_variables()
    {
        var issues = new List<ParseIssue>();

        Assert.Equal("a$b price $5", ComposeParser.Interpolate("a$$b price $5", issues, []));
        Assert.Empty(issues);
        Assert.Equal("", ComposeParser.Interpolate("${TOKEN:?needs a token}", issues, []));
        Assert.Equal("TOKEN has no value here: needs a token. Fill it in.", Assert.Single(issues).Text);
        // A secret default isn't repeated in the note.
        var secret = new List<ParseIssue>();
        ComposeParser.Interpolate("${DB_PASSWORD:-changeme}", secret, []);
        Assert.Equal("DB_PASSWORD isn't set here: it takes its default.", Assert.Single(secret).Text);
    }

    [Fact]
    public void Reads_gpu_devices_and_drops_replicas()
    {
        var app = ComposeParser.Parse(ImageKit.Fixture("Paste", "ollama.yml"));

        Assert.True(app.Ok);
        Assert.Equal("all", app.Settings.Gpus);
        Assert.Equal([new PortRule(11434, "tcp", PortAccess.Lan, 11434)], app.Settings.Ports);
        Assert.Equal([new MountSetting(MountKind.Volume, "gb-ollama-ollama", "/root/.ollama")], app.Settings.Mounts);
        Assert.Contains(Dropped(app), d => d.Text == "Not carried over: deploy: replicas: one container per app; it's created once.");

        var ids = Service("image: x/y:1\ngpus:\n  - driver: nvidia\n    device_ids: ['0', '2']\n    capabilities: [gpu]");
        Assert.Equal("device=0,2", ids.Settings.Gpus);
        Assert.Equal("2", Service("image: x/y:1\ndeploy:\n  resources:\n    reservations:\n      devices:\n        - capabilities: [gpu]\n          count: 2").Settings.Gpus);
        Assert.Equal("all", Service("image: x/y:1\ngpus: all").Settings.Gpus);
    }

    [Fact]
    public void Reads_ports_and_volumes_short_and_long()
    {
        var app = Service("""
            image: nginx:1.27
            ports:
              - 80
              - "8080:80"
              - "127.0.0.1:8443:443"
              - "[::1]:9000:9000/udp"
              - "6000-6001:6000-6001"
              - target: 81
              - target: 82
                published: 8082
                protocol: udp
                mode: host
            expose:
              - "3000"
              - 4000/udp
            volumes:
              - /var/cache/nginx
              - logs:/var/log/nginx:ro
              - type: volume
                target: /data
              - type: bind
                source: /srv/www
                target: /usr/share/nginx/html
                bind:
                  propagation: rshared
                  create_host_path: true
              - type: volume
                source: logs2
                target: /logs2
                volume:
                  nocopy: true
            tmpfs:
              - /run:size=32m
            """);

        Assert.Equal(
        [
            new PortRule(80, "tcp", PortAccess.Lan),
            new PortRule(80, "tcp", PortAccess.Lan, 8080),
            new PortRule(443, "tcp", PortAccess.HostOnly, 8443),
            new PortRule(9000, "udp", PortAccess.HostOnly, 9000),
            new PortRule(6000, "tcp", PortAccess.Lan, 6000),
            new PortRule(6001, "tcp", PortAccess.Lan, 6001),
            new PortRule(81, "tcp", PortAccess.Lan),
            new PortRule(82, "udp", PortAccess.Lan, 8082),
            new PortRule(3000, "tcp", PortAccess.Internal),
            new PortRule(4000, "udp", PortAccess.Internal),
        ], app.Settings.Ports);
        Assert.Equal(
        [
            new MountSetting(MountKind.Volume, "gb-app-nginx", "/var/cache/nginx"),
            new MountSetting(MountKind.Volume, "gb-app-logs", "/var/log/nginx", ReadOnly: true),
            new MountSetting(MountKind.Volume, "gb-app-data", "/data"),
            new MountSetting(MountKind.Bind, "/srv/www", "/usr/share/nginx/html"),
            new MountSetting(MountKind.Volume, "gb-app-logs2", "/logs2"),
            new MountSetting(MountKind.Tmpfs, "", "/run", TmpfsBytes: 32L << 20),
        ], app.Settings.Mounts);
        Assert.Contains(Dropped(app), d => d.Text.Contains("propagation: rshared", StringComparison.Ordinal));
        Assert.Contains(Dropped(app), d => d.Text.Contains("nocopy", StringComparison.Ordinal));
        Assert.Contains(app.Issues, i => i.Text.StartsWith("logs isn't declared under a top-level volumes:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("volumes:\n  - ./data:/data", "./data is relative")]
    [InlineData("volumes:\n  - ~/data:/data", "~/data is relative")]
    [InlineData("volumes:\n  - type: bind\n    source: ./x\n    target: /x", "./x is relative")]
    [InlineData("volumes:\n  - type: volume\n    source: x", "needs target:")]
    [InlineData("ports:\n  - target: 80\n    bogus: 1", "bogus isn't a port setting")]
    [InlineData("ports:\n  - published: 80", "needs target:")]
    public void A_mount_or_port_it_cannot_place_is_an_error(string yaml, string says) =>
        Assert.Contains(Errors(Service("image: nginx:1.27\n" + yaml)), e => e.Text.Contains(says, StringComparison.Ordinal));

    [Fact]
    public void Reads_top_level_volume_names_old_external_and_bind_driver_opts()
    {
        var app = ComposeParser.Parse("""
            services:
              app:
                image: nginx:1.27
                volumes:
                  - old:/old
                  - nfs:/nfs
                  - local:/local
            volumes:
              old:
                external:
                  name: legacy-volume
              nfs:
                driver: local
                driver_opts:
                  type: none
                  o: bind
                  device: /srv/shared
              local:
                driver_opts:
                  type: nfs
                  o: addr=10.1.1.5,rw
                  device: ":/exports"
            """);

        Assert.Equal(
        [
            new MountSetting(MountKind.Volume, "legacy-volume", "/old"),
            new MountSetting(MountKind.Bind, "/srv/shared", "/nfs"),
            new MountSetting(MountKind.Volume, "gb-app-local", "/local"),
        ], app.Settings.Mounts);
        Assert.Contains(app.Issues, i => i.Text == "nfs is a bind of /srv/shared (its driver_opts): Gitberth mounts that folder directly.");
        Assert.Contains(Dropped(app), d => d.Text.Contains("local's driver, options and labels", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_environment_as_a_map_and_as_a_list()
    {
        var map = Service("image: nginx:1.27\nenvironment:\n  A: 1\n  B: \"x=y\"\n  DEBUG: true\n  EMPTY: \"\"\n  UNSET:");
        Assert.Equal([new EnvSetting("A", "1"), new EnvSetting("B", "x=y"), new EnvSetting("DEBUG", "true"), new EnvSetting("EMPTY", ""), new EnvSetting("UNSET", "")],
            map.Settings.Env);
        Assert.Contains(Errors(map), e => e.Text.StartsWith("Give UNSET a value", StringComparison.Ordinal));

        var list = Service("image: nginx:1.27\nenvironment:\n  - A=1\n  - B=x=y\n  - EMPTY=\n  - UNSET");
        Assert.Equal([new EnvSetting("A", "1"), new EnvSetting("B", "x=y"), new EnvSetting("EMPTY", ""), new EnvSetting("UNSET", "")], list.Settings.Env);
        Assert.Contains(Errors(list), e => e.Text.StartsWith("Give UNSET a value", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_health_checks_as_a_string_a_list_or_none()
    {
        var shell = Service("image: x/y:1\nhealthcheck:\n  test: curl -f http://localhost/\n  start_period: 1m\n  start_interval: 2s").Settings.Health;
        Assert.Equal((HealthMode.Custom, "CMD-SHELL", "curl -f http://localhost/"), (shell.Mode, shell.Test![0], shell.Test[1]));
        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2)), (shell.StartPeriod!.Value, shell.StartInterval!.Value));

        var exec = Service("image: x/y:1\nhealthcheck:\n  test: [\"CMD\", \"/healthz\", \"--quick\"]").Settings.Health;
        Assert.Equal(["CMD", "/healthz", "--quick"], exec.Test);

        Assert.Equal(HealthMode.Disabled, Service("image: x/y:1\nhealthcheck:\n  disable: true").Settings.Health.Mode);
        Assert.Equal(HealthMode.Disabled, Service("image: x/y:1\nhealthcheck:\n  test: [\"NONE\"]").Settings.Health.Mode);
        Assert.Contains(Errors(Service("image: x/y:1\nhealthcheck:\n  test: [\"curl\", \"x\"]")), e => e.Text.Contains("starts with CMD, CMD-SHELL or NONE", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_every_carried_key()
    {
        var app = Service("""
            image: x/y:1
            container_name: web
            command: sh -c 'echo hi && sleep 100'
            entrypoint: ["/bin/sh", "-c"]
            hostname: web1
            domainname: example.lan
            extra_hosts:
              db: 10.1.1.20
            dns: 1.1.1.1
            dns_search: [lan]
            dns_opt: ["ndots:2"]
            restart: "no"
            mem_limit: 512m
            mem_reservation: 256m
            memswap_limit: -1
            cpus: 0.5
            cpu_shares: 512
            cpuset: "0,1"
            pids_limit: 100
            shm_size: 64m
            ulimits:
              nproc: 512
              nofile:
                soft: 1024
                hard: 2048
            working_dir: /app
            user: "1000:1000"
            labels:
              - a=b
            devices:
              - /dev/dri:/dev/dri
              - source: /dev/sda
                target: /dev/xvda
                permissions: r
            cap_add: [net_admin]
            cap_drop: [ALL]
            privileged: true
            security_opt: ["no-new-privileges:true"]
            read_only: true
            init: true
            sysctls:
              net.core.somaxconn: 1024
            logging:
              driver: local
              options:
                max-size: 20m
            stop_signal: SIGINT
            stop_grace_period: 1m30s
            tty: true
            stdin_open: true
            platform: linux/arm64
            pull_policy: always
            """);

        var s = app.Settings;
        Assert.True(app.Ok, string.Join("; ", Errors(app).Select(e => e.Text)));
        Assert.Empty(Dropped(app));
        Assert.Equal("web", app.Name);
        Assert.Equal(["sh", "-c", "echo hi && sleep 100"], s.Command);
        Assert.Equal(["/bin/sh", "-c"], s.Entrypoint);
        Assert.Equal(("web1", "example.lan"), (s.Hostname, s.Domainname));
        Assert.Equal(["db:10.1.1.20"], s.ExtraHosts);
        Assert.Equal(["1.1.1.1"], s.Dns);
        Assert.Equal(["lan"], s.DnsSearch);
        Assert.Equal(["ndots:2"], s.DnsOptions);
        Assert.Equal(new RestartSetting("no"), s.Restart);
        Assert.Equal(new ResourceSetting(512L << 20, 256L << 20, -1, 0.5, 512, "0,1", 100), s.Resources);
        Assert.Equal(64L << 20, s.ShmSizeBytes);
        Assert.Equal([new UlimitSetting("nproc", 512, 512), new UlimitSetting("nofile", 1024, 2048)], s.Ulimits);
        Assert.Equal(("/app", "1000:1000"), (s.WorkingDir, s.User));
        Assert.Equal("b", s.Labels["a"]);
        Assert.Equal([new DeviceSetting("/dev/dri", "/dev/dri"), new DeviceSetting("/dev/sda", "/dev/xvda", "r")], s.Devices);
        Assert.Equal(["NET_ADMIN"], s.CapAdd);
        Assert.Equal(["ALL"], s.CapDrop);
        Assert.Equal((true, true, true, true, true), (s.Privileged, s.ReadOnlyRootfs, s.Init, s.Tty, s.OpenStdin));
        Assert.Equal(["no-new-privileges:true"], s.SecurityOpt);
        Assert.Equal("1024", s.Sysctls["net.core.somaxconn"]);
        Assert.Equal(("local", "20m"), (s.Logging.Driver, s.Logging.Options!["max-size"]));
        Assert.Equal(("SIGINT", 90), (s.StopSignal, s.StopTimeoutSeconds!.Value));
        Assert.Equal(("linux/arm64", true), (app.Platform, app.PullFirst));
    }

    [Fact]
    public void A_cpu_quota_and_a_restart_policy_under_deploy_are_converted_with_a_note()
    {
        var quota = Service("image: x/y:1\ncpu_quota: 50000\ncpu_period: 100000");
        Assert.Equal(0.5, quota.Settings.Resources.Cpus);
        Assert.Contains(quota.Issues, i => i.Text.StartsWith("cpu_quota 50000 with a period of 100000 is 0.5 CPUs", StringComparison.Ordinal));

        var policy = Service("image: x/y:1\ndeploy:\n  restart_policy:\n    condition: on-failure\n    max_attempts: 3\n    delay: 5s");
        Assert.Equal(new RestartSetting("on-failure", 3), policy.Settings.Restart);
        Assert.Contains(Dropped(policy), d => d.Text.Contains("delay", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_networks_and_network_modes()
    {
        var host = Service("image: x/y:1\nnetwork_mode: host\nports: [\"80:80\"]");
        Assert.Equal("host", host.Settings.NetworkMode);
        Assert.Empty(host.Settings.Ports);
        Assert.Contains(Dropped(host), d => d.Text.Contains("listens on the host's own ports", StringComparison.Ordinal));

        Assert.Contains(Errors(Service("image: x/y:1\nnetwork_mode: \"service:db\"")), e => e.Text.Contains("create db first", StringComparison.Ordinal));
        Assert.Equal("container:vpn", Service("image: x/y:1\nnetwork_mode: container:vpn").Settings.NetworkMode);

        var lan = Service("image: x/y:1\nnetworks:\n  gitberth-lan:\n    ipv4_address: 10.1.1.230\n  media:");
        Assert.Equal("10.1.1.230", lan.LanAddress);
        Assert.Equal("media", Assert.Single(lan.Networks).Name);
        Assert.Contains(lan.Issues, i => i.Text.StartsWith("Compose would make the network as <project>_media", StringComparison.Ordinal));
    }

    public static TheoryData<string, string> DroppedKeys => new()
    {
        { "depends_on: [db]", "create db first" },
        { "links: [db]", "links are Docker's old way" },
        { "external_links: [other_db]", "links are Docker's old way" },
        { "volumes_from: [data]", "mount the volumes it needs by name" },
        { "secrets: [token]", "put the value in Environment" },
        { "configs: [cfg]", "put the value in Environment" },
        { "profiles: [debug]", "they choose which services Compose starts" },
        { "extends:\n  service: base", "the service it extends isn't in the paste" },
        { "env_file: .env", "paste its lines under environment" },
        { "deploy:\n  replicas: 3", "one container per app" },
        { "scale: 2", "one container per app" },
        { "mac_address: 02:42:ac:11:00:02", "MAC address of its own" },
        { "pid: host", "its own namespace" },
        { "oom_score_adj: 500", "the kernel's own handling of memory" },
        { "group_add: [audio]", "set the user it runs as" },
    };

    [Theory]
    [MemberData(nameof(DroppedKeys))]
    public void Lists_every_key_it_does_not_carry_over_with_the_reason(string yaml, string reason)
    {
        var app = Service("image: nginx:1.27\nrestart: always\n" + yaml);

        Assert.True(app.Ok, string.Join("; ", Errors(app).Select(e => e.Text)));
        var dropped = Assert.Single(Dropped(app));
        Assert.StartsWith("Not carried over: ", dropped.Text);
        Assert.Contains(reason, dropped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_to_build_belongs_to_from_github()
    {
        var build = Service("build: .\nports: [\"3000:3000\"]");
        Assert.Equal("Code to build: this service has build: and no image:. Use the GitHub repository source to build an app from its code.", Assert.Single(Errors(build)).Text);

        var both = Service("build: .\nimage: me/app:1");
        Assert.True(both.Ok);
        Assert.Contains(both.Issues, i => i.Severity == ParseSeverity.Note && i.Text.StartsWith("It has build: as well", StringComparison.Ordinal));

        Assert.Contains(Errors(Service("ports: [\"80:80\"]")), e => e.Text.StartsWith("The service has no image", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_a_service_pasted_on_its_own_or_without_services()
    {
        var keys = ComposeParser.Parse("image: nginx:1.27\nports:\n  - \"8080:80\"\n");
        Assert.True(keys.Ok);
        Assert.Null(keys.Name);
        Assert.Equal("nginx:1.27", keys.Settings.Image);

        var bare = ComposeParser.Read("web:\n  image: nginx:1.27\ncache:\n  image: redis:7\n");
        Assert.Equal(["web", "cache"], bare.Services);
        Assert.Equal("Read as services: the paste has no services: line.", Assert.Single(bare.Issues).Text);
        Assert.Equal("redis:7", ComposeParser.Parse("web:\n  image: nginx:1.27\ncache:\n  image: redis:7\n", "cache").Settings.Image);
    }

    [Theory]
    [InlineData("services:\n  web:\n    image: [unclosed\n", "The YAML can't be read at line")]
    [InlineData("", "The paste is empty")]
    [InlineData("- just\n- a list\n", "That isn't a Compose file")]
    [InlineData("services:\n  web:\n    image: nginx\n    imagee: nginx\n", "Unknown key imagee: remove it or set it by hand.")]
    [InlineData("services:\n  web:\n    image: nginx\n    labels:\n      gitberth.app: x\n", "Labels starting with gitberth. are Gitberth's own")]
    public void What_it_cannot_read_is_an_error(string yaml, string says) =>
        Assert.Contains(Errors(ComposeParser.Parse(yaml)), e => e.Text.StartsWith(says, StringComparison.Ordinal));
}
