using Gitberth.Apps;
using Gitberth.Network;
using Gitberth.Sources;

namespace Gitberth.Tests;

/// <summary>A pasted docker run read as an app's settings: every option carried over, dropped with its reason, or an error.</summary>
public class DockerRunParserTests
{
    private static ParsedApp Run(string args) => DockerRunParser.Parse("docker run " + args);

    private static IEnumerable<ParseIssue> Errors(ParsedApp app) => app.Issues.Where(i => i.Severity == ParseSeverity.Error);

    private static IEnumerable<ParseIssue> Dropped(ParsedApp app) => app.Issues.Where(i => i.Severity == ParseSeverity.Dropped);

    [Fact]
    public void Reads_a_readme_one_liner()
    {
        var app = DockerRunParser.Parse(ImageKit.Fixture("Paste", "uptime-kuma.sh"));

        Assert.True(app.Ok);
        Assert.Equal("uptime-kuma", app.Name);
        var s = app.Settings;
        Assert.Equal("louislam/uptime-kuma:1", s.Image);
        Assert.Null(s.Command);
        Assert.Equal(new RestartSetting("always"), s.Restart);
        Assert.Equal([new PortRule(3001, "tcp", PortAccess.Lan, 3001)], s.Ports);
        Assert.Equal([new MountSetting(MountKind.Volume, "uptime-kuma", "/app/data")], s.Mounts);
        Assert.Contains(app.Issues, i => i.Text == "Gitberth names the container gb-uptime-kuma; other containers reach it as uptime-kuma.");
        Assert.Empty(Dropped(app));
    }

    [Fact]
    public void Reads_a_long_command_with_crlf_continuations_sudo_and_combined_flags()
    {
        var app = DockerRunParser.Parse(ImageKit.Fixture("Paste", "jellyfin.sh"));

        Assert.True(app.Ok, string.Join("; ", Errors(app).Select(e => e.Text)));
        var s = app.Settings;
        Assert.Equal("Media Server", app.Name);
        Assert.Equal((true, true), (s.Tty, s.OpenStdin));
        Assert.Equal("jellyfin/jellyfin:10.10", s.Image);
        Assert.Equal(["--ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg"], s.Command);
        Assert.Equal("media", s.Hostname);

        Assert.Equal(["TZ", "DATABASE_URL", "PUID", "PGID"], s.Env.Select(e => e.Key));
        var url = s.Env.Single(e => e.Key == "DATABASE_URL");
        Assert.Equal("postgres://app:s3cr=t@db:5432/app?sslmode=disable", url.Value);
        Assert.True(url.Secret);
        Assert.False(s.Env.Single(e => e.Key == "TZ").Secret);

        Assert.Equal(
        [
            new PortRule(8096, "tcp", PortAccess.Lan, 8096),
            new PortRule(8920, "tcp", PortAccess.HostOnly, 8920),
            new PortRule(7359, "udp", PortAccess.HostOnly, 7359),
            new PortRule(1900, "udp", PortAccess.Lan, 1900),
            new PortRule(9000, "tcp", PortAccess.Lan, 9000),
            new PortRule(9001, "tcp", PortAccess.Lan, 9001),
            new PortRule(9002, "tcp", PortAccess.Lan, 9002),
        ], s.Ports);

        Assert.Equal(
        [
            new MountSetting(MountKind.Bind, "/srv/media/config", "/config"),
            new MountSetting(MountKind.Volume, "jellyfin-cache", "/cache", ReadOnly: true),
            new MountSetting(MountKind.Volume, "gb-media-server-transcode", "/transcode"),
            new MountSetting(MountKind.Bind, "/mnt/movies", "/data/movies", ReadOnly: true),
            new MountSetting(MountKind.Tmpfs, "", "/tmp/scratch", TmpfsBytes: 256L << 20),
            new MountSetting(MountKind.Tmpfs, "", "/run", TmpfsBytes: 64L << 20),
        ], s.Mounts);

        var network = Assert.Single(app.Networks);
        Assert.Equal(("media", "jellyfin"), (network.Name, Assert.Single(network.Aliases)));
        Assert.True(network.LowPriority);

        Assert.Equal(new RestartSetting("unless-stopped"), s.Restart);
        Assert.Equal(new ResourceSetting(2L << 30, 512L << 20, null, 2.5, null, null, 512), s.Resources);
        Assert.Equal(256L << 20, s.ShmSizeBytes);
        Assert.Equal([new UlimitSetting("nofile", 65536, 65536)], s.Ulimits);
        Assert.Equal([new DeviceSetting("/dev/dri", "/dev/dri")], s.Devices);
        Assert.Equal("all", s.Gpus);
        Assert.Equal(["SYS_ADMIN"], s.CapAdd);
        Assert.Equal(["no-new-privileges:true"], s.SecurityOpt);
        Assert.Equal(HealthMode.Custom, s.Health.Mode);
        Assert.Equal(["CMD-SHELL", "curl -fsS http://localhost:8096/health || exit 1"], s.Health.Test);
        Assert.Equal((TimeSpan.FromSeconds(30), 3), (s.Health.Interval, s.Health.Retries));
        Assert.Equal("json-file", s.Logging.Driver);
        Assert.Equal("10m", s.Logging.Options!["max-size"]);
        Assert.Equal(30, s.StopTimeoutSeconds);
        Assert.Equal("media", s.Labels["com.example.team"]);
        Assert.Equal(["host.docker.internal:host-gateway"], s.ExtraHosts);
        Assert.Equal(["1.1.1.1"], s.Dns);

        Assert.Equal("--rm", Assert.Single(Dropped(app), d => d.Fragment == "--rm").Fragment);
        Assert.Contains(Dropped(app), d => d.Text.Contains("noexec", StringComparison.Ordinal));
        Assert.Contains(app.Issues, i => i.Text == "[::1]:7359:7359/udp: Host only is published on 127.0.0.1.");
        Assert.Contains(app.Issues, i => i.Text.StartsWith("/transcode has no volume name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--name web nginx")]
    [InlineData("--name=web nginx")]
    [InlineData("-e A=1 --name web nginx")]
    public void Takes_both_value_forms(string args)
    {
        var app = Run(args);

        Assert.Equal(("web", "nginx"), (app.Name, app.Settings.Image));
    }

    [Theory]
    [InlineData("-itd nginx")]
    [InlineData("-dit nginx")]
    [InlineData("-i -t -d nginx")]
    [InlineData("--interactive --tty --detach nginx")]
    public void Combines_short_flags(string args)
    {
        var app = Run(args);

        Assert.True(app.Ok);
        Assert.Equal((true, true, "nginx"), (app.Settings.Tty, app.Settings.OpenStdin, app.Settings.Image));
    }

    [Theory]
    [InlineData("-p8080:80 nginx")]
    [InlineData("-p=8080:80 nginx")]
    [InlineData("-dp 8080:80 nginx")]
    [InlineData("-dp8080:80 nginx")]
    [InlineData("--publish=8080:80 nginx")]
    public void A_short_flag_takes_its_value_attached_or_next(string args) =>
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 8080)], Run(args).Settings.Ports);

    [Theory]
    [InlineData("80", 80, "tcp", PortAccess.Lan, null, null)]
    [InlineData("8080:80", 80, "tcp", PortAccess.Lan, 8080, null)]
    [InlineData("0.0.0.0:8080:80", 80, "tcp", PortAccess.Lan, 8080, null)]
    [InlineData("[::]:8080:80", 80, "tcp", PortAccess.Lan, 8080, null)]
    [InlineData("127.0.0.1:8080:80", 80, "tcp", PortAccess.HostOnly, 8080, null)]
    [InlineData("[::1]:8080:80", 80, "tcp", PortAccess.HostOnly, 8080, null)]
    [InlineData("127.0.0.1::80", 80, "tcp", PortAccess.HostOnly, null, null)]
    [InlineData("10.1.1.80:8080:80", 80, "tcp", PortAccess.Custom, 8080, "10.1.1.80")]
    [InlineData("53:53/udp", 53, "udp", PortAccess.Lan, 53, null)]
    [InlineData("5060:5060/sctp", 5060, "sctp", PortAccess.Lan, 5060, null)]
    public void Reads_every_form_of_publish(string spec, int port, string protocol, PortAccess access, int? hostPort, string? hostIp) =>
        Assert.Equal([new PortRule(port, protocol, access, hostPort, hostIp)], Run($"-p {spec} nginx").Settings.Ports);

    [Fact]
    public void Reads_ranges_of_ports()
    {
        Assert.Equal([new PortRule(9000, "tcp", PortAccess.Lan, 8000), new PortRule(9001, "tcp", PortAccess.Lan, 8001), new PortRule(9002, "tcp", PortAccess.Lan, 8002)],
            Run("-p 8000-8002:9000-9002 nginx").Settings.Ports);
        Assert.Equal([new PortRule(7000, "udp", PortAccess.Lan), new PortRule(7001, "udp", PortAccess.Lan)], Run("-p 7000-7001/udp nginx").Settings.Ports);

        // A host range for one port: Docker would pick one in it; Gitberth suggests one.
        var picked = Run("-p 8000-8010:80 nginx");
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan)], picked.Settings.Ports);
        Assert.Contains(picked.Issues, i => i.Severity == ParseSeverity.Note && i.Text.Contains("Gitberth suggests one", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-p 8080:80/xyz", "the protocol is tcp, udp or sctp")]
    [InlineData("-p 70000:80", "the host port is a number from 1 to 65535")]
    [InlineData("-p 8080:0", "the container port is a number from 1 to 65535")]
    [InlineData("-p 1-200:1-200", "give at most 100 at a time")]
    [InlineData("-p 8000-8001:80-82", "the host and container ranges must be the same length")]
    [InlineData("-p ::1:80:80", "put an IPv6 address in brackets")]
    [InlineData("-p nothost:80:80", "nothost isn't an address of the host")]
    public void A_publish_docker_would_refuse_is_an_error(string option, string says)
    {
        var app = Run(option + " nginx");

        Assert.Contains(says, Assert.Single(Errors(app)).Text);
        Assert.Empty(app.Settings.Ports);
    }

    [Fact]
    public void Publish_all_and_expose()
    {
        Assert.True(Run("-P nginx").PublishAll);
        Assert.True(Run("--publish-all nginx").PublishAll);
        Assert.Equal([new PortRule(8080, "tcp", PortAccess.Internal)], Run("--expose 8080 nginx").Settings.Ports);
        Assert.Equal([new PortRule(7000, "udp", PortAccess.Internal), new PortRule(7001, "udp", PortAccess.Internal)], Run("--expose 7000-7001/udp nginx").Settings.Ports);
    }

    [Theory]
    [InlineData("pgdata:/var/lib/postgresql", MountKind.Volume, "pgdata", "/var/lib/postgresql", false)]
    [InlineData("pgdata:/var/lib/postgresql:ro", MountKind.Volume, "pgdata", "/var/lib/postgresql", true)]
    [InlineData("/srv/data:/data", MountKind.Bind, "/srv/data", "/data", false)]
    [InlineData("/srv/data:/data:rw", MountKind.Bind, "/srv/data", "/data", false)]
    [InlineData("/srv/data:/data:ro,z", MountKind.Bind, "/srv/data", "/data", true)]
    [InlineData("/data", MountKind.Volume, "gb-web-data", "/data", false)]
    [InlineData("/var/lib/postgresql/data:ro", MountKind.Volume, "gb-web-data", "/var/lib/postgresql/data", true)]
    public void Reads_every_form_of_volume(string spec, MountKind kind, string source, string target, bool readOnly) =>
        Assert.Equal([new MountSetting(kind, source, target, readOnly)], Run($"--name web -v {spec} nginx").Settings.Mounts);

    [Fact]
    public void Volume_options_gitberth_does_not_keep_are_dropped_with_a_note()
    {
        var app = Run("-v /srv/a:/a:Z -v /srv/b:/b:rshared -v vol:/c:nocopy -v /srv/d:/d:cached nginx");

        Assert.True(app.Ok);
        Assert.Equal(4, app.Settings.Mounts.Count);
        Assert.Equal(4, Dropped(app).Count());
        Assert.All(Dropped(app), d => Assert.StartsWith("Not carried over: ", d.Text));
    }

    [Theory]
    [InlineData("./data:/data", "./data is relative to the folder the command ran in")]
    [InlineData("~/data:/data", "~/data is relative to the folder the command ran in")]
    [InlineData("C:\\data:/data", "is a Windows path")]
    [InlineData("/srv/data:data", "the path in the container must be absolute")]
    [InlineData("/srv/data:/data:bogus", "bogus isn't a volume option")]
    [InlineData("ab:/b:ro:x", "isn't a volume")]
    [InlineData("bad name!:/data", "isn't a volume name")]
    public void A_volume_gitberth_cannot_place_is_an_error(string spec, string says) =>
        Assert.Contains(says, Assert.Single(Errors(Run($"-v \"{spec}\" nginx"))).Text);

    [Fact]
    public void Reads_mount()
    {
        var app = Run("--name web --mount type=bind,source=/srv/a,target=/a,readonly " +
                      "--mount type=volume,src=media,dst=/media --mount type=volume,target=/cache " +
                      "--mount type=tmpfs,destination=/scratch,tmpfs-size=64m --mount \"type=volume,source=x,target=/x,volume-opt=type=nfs\" nginx");

        Assert.Equal(
        [
            new MountSetting(MountKind.Bind, "/srv/a", "/a", ReadOnly: true),
            new MountSetting(MountKind.Volume, "media", "/media"),
            new MountSetting(MountKind.Volume, "gb-web-cache", "/cache"),
            new MountSetting(MountKind.Tmpfs, "", "/scratch", TmpfsBytes: 64L << 20),
            new MountSetting(MountKind.Volume, "x", "/x"),
        ], app.Settings.Mounts);
        Assert.Contains(Dropped(app), d => d.Text.Contains("volume-opt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("type=bind,target=/a", "a bind mount needs source=")]
    [InlineData("type=bind,source=./a,target=/a", "./a is relative")]
    [InlineData("type=volume,source=x", "needs target=")]
    [InlineData("type=volume,source=x,target=/x,bogus=1", "bogus isn't a --mount option")]
    public void A_mount_gitberth_cannot_place_is_an_error(string spec, string says) =>
        Assert.Contains(Errors(Run($"--mount {spec} nginx")), e => e.Text.Contains(says, StringComparison.Ordinal));

    [Fact]
    public void An_image_mount_is_dropped_and_tmpfs_options_too()
    {
        var app = Run("--mount type=image,source=busybox,target=/b --tmpfs /run:rw,noexec,nosuid,size=65536k,mode=1777 nginx");

        Assert.Equal([new MountSetting(MountKind.Tmpfs, "", "/run", TmpfsBytes: 65536L * 1024)], app.Settings.Mounts);
        Assert.Equal(4, Dropped(app).Count());
    }

    [Fact]
    public void Reads_environment()
    {
        var app = Run("-e A=1 -e B=x=y=z --env C= -e A=2 -e API_TOKEN=abcd nginx");

        Assert.True(app.Ok);
        Assert.Equal([new EnvSetting("B", "x=y=z"), new EnvSetting("C", ""), new EnvSetting("A", "2"), new EnvSetting("API_TOKEN", "abcd", Secret: true)],
            app.Settings.Env);
    }

    [Fact]
    public void A_variable_without_a_value_is_an_error_with_its_row_there_to_fill_in()
    {
        var app = Run("-e DB_PASSWORD nginx");

        Assert.False(app.Ok);
        Assert.Equal("Give DB_PASSWORD a value: docker run would take it from your shell, which Gitberth can't see.", Assert.Single(Errors(app)).Text);
        Assert.Equal([new EnvSetting("DB_PASSWORD", "", Secret: true)], app.Settings.Env);
    }

    [Theory]
    [InlineData("docker run nginx")]
    [InlineData("docker container run nginx")]
    [InlineData("sudo docker run nginx")]
    [InlineData("sudo docker container run nginx")]
    public void Takes_the_ways_docker_run_is_written(string text)
    {
        var app = DockerRunParser.Parse(text);

        Assert.Equal("nginx", app.Settings.Image);
        Assert.DoesNotContain(app.Issues, i => i.Severity == ParseSeverity.Error);
    }

    [Theory]
    [InlineData("", "Paste a docker run command")]
    [InlineData("docker ps -a", "Gitberth reads docker run commands, not docker ps.")]
    [InlineData("docker", "Paste a docker run command")]
    [InlineData("docker compose up -d", "That's a Compose command")]
    [InlineData("docker-compose up", "That's a Compose command")]
    [InlineData("podman run nginx", "That's a podman command")]
    [InlineData("nginx -p 80:80", "it starts with docker run")]
    [InlineData("docker run -d -p 80:80", "There's no image in the command")]
    [InlineData("docker run nginx:Latest!", "isn't an image name Docker takes")]
    public void What_is_not_a_docker_run_is_an_error(string text, string says) =>
        Assert.Contains(DockerRunParser.Parse(text).Issues, i => i.Severity == ParseSeverity.Error && i.Text.Contains(says, StringComparison.Ordinal));

    [Fact]
    public void Docker_create_is_read_as_run_with_a_note()
    {
        var app = DockerRunParser.Parse("docker create --name x nginx");

        Assert.Equal("nginx", app.Settings.Image);
        Assert.Contains(app.Issues, i => i.Text.StartsWith("docker create is read as docker run", StringComparison.Ordinal));
    }

    [Fact]
    public void Splits_the_image_from_its_command()
    {
        var app = Run("--rm -it alpine:3.20 sh -c \"echo hi && sleep 5\"");

        Assert.Equal("alpine:3.20", app.Settings.Image);
        Assert.Equal(["sh", "-c", "echo hi && sleep 5"], app.Settings.Command);

        var ended = Run("-d -- nginx -g \"daemon off;\"");
        Assert.Equal("nginx", ended.Settings.Image);
        Assert.Equal(["-g", "daemon off;"], ended.Settings.Command);

        var byId = Run("3a4264520e12");
        Assert.True(byId.Ok);
        Assert.Equal("3a4264520e12", byId.Settings.Image);
    }

    [Fact]
    public void Docker_options_after_the_image_go_to_its_program_with_a_note()
    {
        var app = Run("nginx -p 80:80");

        Assert.Equal(["-p", "80:80"], app.Settings.Command);
        Assert.Empty(app.Settings.Ports);
        Assert.Contains(app.Issues, i => i.Severity == ParseSeverity.Note && i.Text.StartsWith("-p comes after the image", StringComparison.Ordinal));
    }

    public static TheoryData<string, string> DroppedOptions => new()
    {
        { "--rm", "Gitberth keeps its apps" },
        { "--link db:db", "join a network instead" },
        { "--volumes-from data", "Mount the volumes it needs by name" },
        { "--env-file .env", "paste its lines into Environment" },
        { "--label-file labels", "add its labels under Labels" },
        { "--cidfile /tmp/id", "keeps the container's ID itself" },
        { "-a stdout", "its output is on the Logs tab" },
        { "--attach stderr", "its output is on the Logs tab" },
        { "--sig-proxy", "a terminal attached" },
        { "--detach-keys ctrl-x", "a terminal attached" },
        { "--disable-content-trust", "pulls as Docker is configured" },
        { "-q", "quiets docker run's own output" },
        { "--cgroupns host", "cgroup namespace" },
        { "--cgroup-parent /x", "Docker's own cgroup" },
        { "--runtime runc", "default runtime" },
        { "--isolation process", "Windows containers" },
        { "--userns host", "user namespace" },
        { "--pid host", "process namespace" },
        { "--ipc host", "IPC namespace" },
        { "--uts host", "UTS namespace" },
        { "--mac-address 02:42:ac:11:00:02", "MAC address" },
        { "--oom-kill-disable", "memory runs out" },
        { "--oom-score-adj 500", "weighs the container" },
        { "--blkio-weight 300", "disk I/O weights" },
        { "--blkio-weight-device /dev/sda:200", "disk I/O weights" },
        { "--device-read-bps /dev/sda:1mb", "disk I/O limits" },
        { "--device-write-bps /dev/sda:1mb", "disk I/O limits" },
        { "--device-read-iops /dev/sda:100", "disk I/O limits" },
        { "--device-write-iops /dev/sda:100", "disk I/O limits" },
        { "--kernel-memory 50m", "no longer supports" },
        { "--group-add audio", "extra groups" },
        { "--storage-opt size=10G", "storage driver options" },
        { "--annotation a=b", "annotations" },
        { "--cpu-rt-runtime 950000", "real-time CPU" },
        { "--cpuset-mems 0", "memory node" },
        { "--device-cgroup-rule 'c 1:3 mr'", "device cgroup rules" },
        { "--ip6 fd00::5", "IPv6 address" },
        { "--memory-swappiness 0", "kernel's own setting" },
        { "--volume-driver local", "local driver" },
    };

    [Theory]
    [MemberData(nameof(DroppedOptions))]
    public void Lists_every_option_it_does_not_carry_over_with_the_reason(string option, string reason)
    {
        var app = Run($"{option} nginx");

        Assert.True(app.Ok, string.Join("; ", Errors(app).Select(e => e.Text)));
        Assert.Equal("nginx", app.Settings.Image);
        var dropped = Assert.Single(Dropped(app));
        Assert.StartsWith("Not carried over: ", dropped.Text);
        Assert.Contains(reason, dropped.Text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(option.Split(' ')[0], dropped.Fragment);
    }

    [Theory]
    [InlineData("--rm=false")]
    [InlineData("--oom-kill-disable=false")]
    public void A_dropped_switch_turned_off_is_nothing_to_drop(string option) => Assert.Empty(Dropped(Run($"{option} nginx")));

    [Theory]
    [InlineData("--foo nginx", "Unknown option --foo: remove it or set it by hand.")]
    [InlineData("--foo=bar nginx", "Unknown option --foo: remove it or set it by hand.")]
    [InlineData("-Z nginx", "Unknown option -Z: remove it or set it by hand.")]
    [InlineData("--help nginx", "--help only prints docker run's help: remove it.")]
    [InlineData("nginx --name", null)]
    public void An_unknown_option_is_an_error(string args, string? says)
    {
        var app = Run(args);
        if (says is null)
        {
            // After the image it's the program's.
            Assert.True(app.Ok);
            return;
        }
        Assert.Equal(says, Assert.Single(Errors(app)).Text);
    }

    [Fact]
    public void An_option_without_its_value_is_an_error()
    {
        Assert.Contains(Errors(DockerRunParser.Parse("docker run --name")), e => e.Text == "--name needs a value.");
    }

    [Fact]
    public void Reads_networks()
    {
        var host = Run("--network host -p 80:80 --network-alias web nginx");
        Assert.Equal("host", host.Settings.NetworkMode);
        Assert.Empty(host.Settings.Ports);
        Assert.Empty(host.Networks);
        Assert.Contains(Dropped(host), d => d.Text.Contains("listens on the host's own ports", StringComparison.Ordinal));

        Assert.Equal("container:vpn", Run("--net container:vpn nginx").Settings.NetworkMode);
        Assert.Equal("none", Run("--network=none nginx").Settings.NetworkMode);

        var bridge = Run("--network bridge nginx");
        Assert.Equal("", bridge.Settings.NetworkMode);
        Assert.Empty(bridge.Networks);
        Assert.Contains(bridge.Issues, i => i.Severity == ParseSeverity.Note && i.Text.Contains("shared network", StringComparison.Ordinal));

        // An alias with no network named is one on Gitberth's shared network.
        var alias = Run("--network-alias db postgres:18");
        Assert.Equal(("", "db"), (Assert.Single(alias.Networks).Name, Assert.Single(alias.Networks[0].Aliases)));

        // --ip and --network-alias go to the first --network, wherever they're written.
        var media = Run("--ip 172.20.0.5 --net-alias m --network media --network backend nginx");
        Assert.Equal(["media", "backend"], media.Networks.Select(n => n.Name));
        Assert.Equal(("172.20.0.5", "m"), (media.Networks[0].IPv4Address, Assert.Single(media.Networks[0].Aliases)));
        Assert.All(media.Networks, n => Assert.True(n.LowPriority));

        var lan = Run("--network gitberth-lan --ip 10.1.1.230 nginx");
        Assert.Equal("10.1.1.230", lan.LanAddress);
        Assert.Empty(lan.Networks);

        var noIp = Run("--network gitberth-lan nginx");
        Assert.Null(noIp.LanAddress);
        Assert.Contains(Dropped(noIp), d => d.Text.Contains("LAN address card", StringComparison.Ordinal));

        var orphan = Run("--ip 172.20.0.5 nginx");
        Assert.Contains(Dropped(orphan), d => d.Text.Contains("Docker picks it", StringComparison.Ordinal));

        var longForm = Run("--network name=media,alias=m,ip=172.20.0.9 nginx");
        Assert.Equal(("media", "m", "172.20.0.9"), (longForm.Networks[0].Name, longForm.Networks[0].Aliases[0], longForm.Networks[0].IPv4Address));

        Assert.Contains(Errors(Run("--ip 10.1.1.300 --network media nginx")), e => e.Text.Contains("--ip is an IPv4 address", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_names_and_name_resolution()
    {
        var app = Run("-h web --domainname example.lan --add-host db:10.1.1.20 --add-host=cache=10.1.1.21 --dns 1.1.1.1 --dns-search lan --dns-opt ndots:2 --dns-option timeout:1 nginx");

        var s = app.Settings;
        Assert.True(app.Ok);
        Assert.Equal(("web", "example.lan"), (s.Hostname, s.Domainname));
        Assert.Equal(["db:10.1.1.20", "cache:10.1.1.21"], s.ExtraHosts);
        Assert.Equal(["1.1.1.1"], s.Dns);
        Assert.Equal(["lan"], s.DnsSearch);
        Assert.Equal(["ndots:2", "timeout:1"], s.DnsOptions);
        Assert.Contains(Errors(Run("--add-host db nginx")), e => e.Text.Contains("--add-host is name:address", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--restart no", "no", 0)]
    [InlineData("--restart=always", "always", 0)]
    [InlineData("--restart unless-stopped", "unless-stopped", 0)]
    [InlineData("--restart on-failure", "on-failure", 0)]
    [InlineData("--restart on-failure:5", "on-failure", 5)]
    public void Reads_restart_policies(string option, string name, int retries)
    {
        var app = Run($"{option} nginx");

        Assert.Equal(new RestartSetting(name, retries), app.Settings.Restart);
        Assert.DoesNotContain(app.Issues, i => i.Text.StartsWith("No --restart", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_restart_it_gets_unless_stopped_with_a_note()
    {
        var app = Run("nginx");

        Assert.Equal(new RestartSetting("unless-stopped"), app.Settings.Restart);
        Assert.Contains(app.Issues, i => i.Text.StartsWith("No --restart: Gitberth uses unless-stopped", StringComparison.Ordinal));
        Assert.Contains(Errors(Run("--restart sometimes nginx")), e => e.Text.Contains("the restart policy is", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_resources_in_dockers_units()
    {
        var app = Run("-m 512m --memory-reservation 1.5g --memory-swap -1 --cpus 0.5 -c 512 --cpuset-cpus 0-1 --pids-limit 100 --shm-size 64MiB --ulimit nproc=512 --ulimit nofile=1024:2048 nginx");

        var s = app.Settings;
        Assert.True(app.Ok);
        Assert.Equal(new ResourceSetting(512L << 20, (long)(1.5 * (1L << 30)), -1, 0.5, 512, "0-1", 100), s.Resources);
        Assert.Equal(64L << 20, s.ShmSizeBytes);
        Assert.Equal([new UlimitSetting("nproc", 512, 512), new UlimitSetting("nofile", 1024, 2048)], s.Ulimits);

        Assert.Equal(1024, Run("-m 1k nginx").Settings.Resources.MemoryBytes);
        Assert.Equal(1048576, Run("-m 1048576 nginx").Settings.Resources.MemoryBytes);
        Assert.Contains(Errors(Run("-m lots nginx")), e => e.Text.Contains("give a size such as 512m", StringComparison.Ordinal));
        Assert.Contains(Errors(Run("--ulimit nofile nginx")), e => e.Text.Contains("--ulimit is name=soft[:hard]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cpu_quota_becomes_cpus_with_a_note()
    {
        var app = Run("--cpu-quota 50000 --cpu-period 100000 nginx");

        Assert.Equal(0.5, app.Settings.Resources.Cpus);
        Assert.Contains(app.Issues, i => i.Text.StartsWith("--cpu-quota 50000 with a period of 100000 is 0.5 CPUs", StringComparison.Ordinal));
        Assert.Equal(1.5, Run("--cpu-quota 150000 nginx").Settings.Resources.Cpus);
        Assert.Contains(Dropped(Run("--cpu-period 50000 nginx")), d => d.Text.Contains("limits nothing on its own", StringComparison.Ordinal));
        Assert.Contains(Dropped(Run("--cpus 2 --cpu-quota 50000 nginx")), d => d.Text.Contains("--cpus says the same", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_the_command_settings()
    {
        var s = Run("-w /app -u 1000:1000 --entrypoint /bin/sh nginx -c 'exec nginx'").Settings;

        Assert.Equal(("/app", "1000:1000"), (s.WorkingDir, s.User));
        Assert.Equal(["/bin/sh"], s.Entrypoint);
        Assert.Equal(["-c", "exec nginx"], s.Command);
        Assert.Empty(Run("--entrypoint \"\" nginx").Settings.Entrypoint!);
        Assert.Null(Run("nginx").Settings.Entrypoint);
    }

    [Fact]
    public void Reads_labels_and_refuses_gitberths_own()
    {
        var app = Run("-l a=b --label c --label=d=e=f nginx");
        Assert.Equal(new Dictionary<string, string> { ["a"] = "b", ["c"] = "", ["d"] = "e=f" }, app.Settings.Labels);

        var ours = Run("-l gitberth.app=x nginx");
        Assert.Equal("Labels starting with gitberth. are Gitberth's own: remove gitberth.app.", Assert.Single(Errors(ours)).Text);
        Assert.Empty(ours.Settings.Labels);
    }

    [Fact]
    public void Reads_health_checks()
    {
        var custom = Run("--health-cmd 'pg_isready' --health-interval 1m30s --health-timeout 500ms --health-retries 5 --health-start-period 10s --health-start-interval 2s postgres:18").Settings.Health;
        Assert.Equal(HealthMode.Custom, custom.Mode);
        Assert.Equal(["CMD-SHELL", "pg_isready"], custom.Test);
        Assert.Equal((TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(500), 5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)),
            (custom.Interval, custom.Timeout, custom.Retries, custom.StartPeriod, custom.StartInterval));

        Assert.Equal(HealthMode.Disabled, Run("--no-healthcheck nginx").Settings.Health.Mode);
        Assert.Equal(HealthMode.Image, Run("nginx").Settings.Health.Mode);

        var timing = Run("--health-interval 10s nginx");
        Assert.Equal((HealthMode.Custom, (IReadOnlyList<string>?)null, (TimeSpan?)TimeSpan.FromSeconds(10)), (timing.Settings.Health.Mode, timing.Settings.Health.Test, timing.Settings.Health.Interval));
        Assert.Contains(timing.Issues, i => i.Text.StartsWith("--health-* without --health-cmd keeps the image's own check", StringComparison.Ordinal));

        Assert.Contains(Errors(Run("--no-healthcheck --health-cmd true nginx")), e => e.Text.StartsWith("--no-healthcheck can't go with", StringComparison.Ordinal));
        Assert.Contains(Errors(Run("--health-interval soon nginx")), e => e.Text.Contains("give a time such as 30s", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_devices_gpus_and_security()
    {
        var s = Run("--device /dev/ttyUSB0 --device /dev/sda:/dev/xvda:r --gpus 2 --cap-add net_admin --cap-drop ALL --privileged " +
                    "--security-opt seccomp=unconfined --read-only --init --sysctl net.core.somaxconn=1024 nginx").Settings;

        Assert.Equal([new DeviceSetting("/dev/ttyUSB0", "/dev/ttyUSB0"), new DeviceSetting("/dev/sda", "/dev/xvda", "r")], s.Devices);
        Assert.Equal("2", s.Gpus);
        Assert.Equal(["NET_ADMIN"], s.CapAdd);
        Assert.Equal(["ALL"], s.CapDrop);
        Assert.Equal((true, true, true), (s.Privileged, s.ReadOnlyRootfs, s.Init));
        Assert.Equal(["seccomp=unconfined"], s.SecurityOpt);
        Assert.Equal("1024", s.Sysctls["net.core.somaxconn"]);

        Assert.Equal("all", Run("--gpus all nginx").Settings.Gpus);
        Assert.Equal("device=0,1", Run("--gpus '\"device=0,1\"' nginx").Settings.Gpus);
        Assert.Equal("2", Run("--gpus count=2 nginx").Settings.Gpus);
        Assert.Contains(Dropped(Run("--device nvidia.com/gpu=all nginx")), d => d.Text.Contains("CDI device name", StringComparison.Ordinal));
        Assert.Contains(Errors(Run("--device dev nginx")), e => e.Text.Contains("--device is /dev/host", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_logging_stopping_and_terminal()
    {
        var s = Run("--log-driver local --log-opt max-size=20m --log-opt max-file=5 --stop-signal SIGINT --stop-timeout 60 -t nginx").Settings;

        Assert.Equal("local", s.Logging.Driver);
        Assert.Equal(new Dictionary<string, string> { ["max-size"] = "20m", ["max-file"] = "5" }, s.Logging.Options);
        Assert.Equal(("SIGINT", 60), (s.StopSignal, s.StopTimeoutSeconds));
        Assert.Equal((true, false), (s.Tty, s.OpenStdin));
        Assert.Equal((false, false), (Run("--tty=false nginx").Settings.Tty, Run("nginx").Settings.OpenStdin));
    }

    [Fact]
    public void Reads_the_pull_settings()
    {
        Assert.Equal("linux/arm64", Run("--platform linux/arm64 nginx").Platform);
        Assert.True(Run("--pull always nginx").PullFirst);
        Assert.False(Run("--pull missing nginx").PullFirst);
        Assert.Contains(Run("--pull never nginx").Issues, i => i.Severity == ParseSeverity.Note);
        Assert.Contains(Errors(Run("--pull sometimes nginx")), e => e.Text.Contains("--pull is always, missing or never", StringComparison.Ordinal));
    }
}
