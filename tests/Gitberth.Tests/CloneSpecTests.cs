using System.Text.Json.Nodes;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// The create body for a container like an existing one with other ports: built from real inspects of the host's
/// containers (postgres, the samples, Gitberth, cloudflared), and from copies of them changed the way other containers
/// differ: Compose labels, a static address, -P, an anonymous volume, an unconfined container.
/// </summary>
public class CloneSpecTests
{
    private static readonly string[] RealContainers = ["postgres", "sample-index", "sample-bun-elysia", "sample-bun-hono", "gitberth", "cloudflare-tunnel"];

    public static TheoryData<string> Real() => new(RealContainers);

    private static JsonArray Networks() => DockerFixtures.Json("networks-json.json").AsArray();

    // The image a container runs, as the tag still names it.
    private static JsonObject SameImage(JsonObject inspect) => new() { ["Id"] = (string)inspect["Image"]! };

    private static JsonObject Build(JsonObject inspect, IReadOnlyList<PortRule>? ports = null, JsonArray? networks = null, string lanHostIp = "") =>
        CloneSpec.Build(inspect, SameImage(inspect), ports ?? ContainerPorts.Rules(inspect), networks ?? Networks(), lanHostIp);

    private static JsonObject Running(JsonObject inspect)
    {
        inspect["State"]!["Running"] = true;
        inspect["State"]!["Status"] = "running";
        return inspect;
    }

    private static JsonObject HostConfig(JsonObject body) => body["HostConfig"]!.AsObject();

    private static JsonObject Endpoint(JsonObject body, string network) => body["NetworkingConfig"]!["EndpointsConfig"]![network]!.AsObject();

    [Theory]
    [MemberData(nameof(Real))]
    public void Keeps_everything_but_the_ports_it_changes(string container)
    {
        var inspect = DockerFixtures.Inspect(container);

        var body = Build(inspect);

        // Config goes back as Docker wrote it, the image by its tag.
        var config = (JsonObject)body.DeepClone();
        config.Remove("HostConfig");
        config.Remove("NetworkingConfig");
        Assert.True(JsonNode.DeepEquals(inspect["Config"], config), $"{container}'s Config changed:\n{config}");

        // HostConfig too, but for the system paths Docker works out again, and postgres's network mode (below).
        var expected = (JsonObject)inspect["HostConfig"]!.DeepClone();
        expected["MaskedPaths"] = null;
        expected["ReadonlyPaths"] = null;
        if (container == "postgres") expected["NetworkMode"] = "gitberth";
        Assert.True(JsonNode.DeepEquals(expected, HostConfig(body)), $"{container}'s HostConfig changed:\n{HostConfig(body)}");

        // One endpoint per network, as configured.
        var networks = inspect["NetworkSettings"]!["Networks"]!.AsObject();
        Assert.Equal(networks.Select(n => n.Key), body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject().Select(e => e.Key));
    }

    [Fact]
    public void Changes_only_the_ports_asked_for()
    {
        var postgres = DockerFixtures.Inspect("postgres");

        var body = Build(postgres, [new PortRule(5432, "tcp", PortAccess.Lan, 5432)]);

        Assert.Equal("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}]}""", HostConfig(body)["PortBindings"]!.ToJsonString());
        Assert.False((bool)HostConfig(body)["PublishAllPorts"]!);
        Assert.Equal(["postgres-data:/var/lib/postgresql"], HostConfig(body)["Binds"]!.AsArray().Select(b => (string)b!));
        Assert.Equal("postgres:18", (string)body["Image"]!);
        Assert.Equal("SIGINT", (string)body["StopSignal"]!);
        Assert.Equal("unless-stopped", (string)HostConfig(body)["RestartPolicy"]!["Name"]!);
        Assert.Equal("journald", (string)HostConfig(body)["LogConfig"]!["Type"]!);
        Assert.Equal(["CMD-SHELL", "pg_isready -U postgres"], body["Healthcheck"]!["Test"]!.AsArray().Select(t => (string)t!));
        Assert.Contains("POSTGRES_PASSWORD=REDACTED", body["Env"]!.AsArray().Select(e => (string)e!));
    }

    [Fact]
    public void Pins_the_image_by_id_when_the_tag_has_moved_and_it_has_no_digest()
    {
        var postgres = DockerFixtures.Inspect("postgres");
        var image = DockerFixtures.Json("image-postgres.json").AsObject();

        Assert.Equal("postgres:18", CloneSpec.ImageFor(postgres, image));

        // A pull since moved postgres:18 to a newer image, and the image it runs couldn't be read: the container keeps
        // the one it runs, by its ID.
        image["Id"] = "sha256:9999999999999999999999999999999999999999999999999999999999999999";
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", CloneSpec.ImageFor(postgres, image));
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81",
            (string)CloneSpec.Build(postgres, image, [], Networks())["Image"]!);
        // The tag is gone altogether.
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", CloneSpec.ImageFor(postgres, null));
        // No digest left: on this host's containerd image store, an image whose only tag moved on has none (checked on Docker
        // 29.9.2, where a create from repo@sha256:<its id> is then refused); its ID is the one reference that still works.
        var built = DockerFixtures.Json("image-postgres.json").AsObject();
        built["RepoTags"] = new JsonArray();
        built["RepoDigests"] = new JsonArray();
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", CloneSpec.ImageFor(postgres, image, built));
    }

    [Fact]
    public void Pins_the_image_by_digest_when_the_tag_has_moved()
    {
        // A pull since moved postgres:18 on. A digest of the image it runs pins that image and keeps its repository's name,
        // which is how Gitberth knows a database (sha256:… alone would make it an ordinary container).
        var postgres = DockerFixtures.Inspect("postgres");
        var moved = new JsonObject { ["Id"] = "sha256:9999999999999999999999999999999999999999999999999999999999999999" };
        var runs = DockerFixtures.Json("image-postgres.json").AsObject();
        const string pinned = "postgres@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";

        Assert.Equal(pinned, CloneSpec.ImageFor(postgres, moved, runs));
        Assert.Equal(pinned, (string)CloneSpec.Build(postgres, moved, [], Networks(), running: runs)["Image"]!);
        Assert.Equal(ContainerRole.Database,
            ContainerService.RoleOf(new ContainerSummary("postgres", "", "", pinned, "", new Dictionary<string, string>()), new Dictionary<string, NavApp>(), null));
        // A digest from the tag's own repository comes first.
        runs["RepoDigests"] = new JsonArray("mirror.example.com/library/postgres@sha256:aaaa", pinned);
        Assert.Equal(pinned, CloneSpec.ImageFor(postgres, moved, runs));
        // The inspect of another image isn't the one it runs.
        runs["Id"] = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", CloneSpec.ImageFor(postgres, moved, runs));
    }

    [Fact]
    public void Keeps_the_hostname_and_drops_the_mac_address_from_config()
    {
        var postgres = DockerFixtures.Inspect("postgres");
        postgres["Config"]!["MacAddress"] = "02:42:ac:11:00:02";

        var body = Build(postgres);

        // Create refuses Config.MacAddress since API 1.52; a MAC belongs to an endpoint now.
        Assert.False(body.ContainsKey("MacAddress"));
        // The old short ID, so software that keys its data to the hostname (RabbitMQ) finds it.
        Assert.Equal("0ebf31bbcd07", (string)body["Hostname"]!);
    }

    [Fact]
    public void Carries_anonymous_volumes_over_by_name()
    {
        // The pre-18 habit: postgres-data mounted deeper, so the image's VOLUME /var/lib/postgresql is an anonymous volume,
        // and Postgres 18 keeps its data in there.
        var postgres = DockerFixtures.Inspect("postgres");
        const string anonymous = "8e1b5c5a2d4f6b7c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e";
        const string cache = "1111111111111111111111111111111111111111111111111111111111111111";
        const string logs = "2222222222222222222222222222222222222222222222222222222222222222";
        postgres["HostConfig"]!["Binds"] = new JsonArray("postgres-data:/var/lib/postgresql/data", "/cache");
        postgres["HostConfig"]!["Mounts"] = new JsonArray(
            new JsonObject { ["Type"] = "volume", ["Target"] = "/logs" },
            new JsonObject { ["Type"] = "tmpfs", ["Target"] = "/scratch" });
        postgres["Mounts"] = new JsonArray(
            new JsonObject { ["Type"] = "volume", ["Name"] = "postgres-data", ["Destination"] = "/var/lib/postgresql/data", ["RW"] = true },
            new JsonObject { ["Type"] = "volume", ["Name"] = anonymous, ["Destination"] = "/var/lib/postgresql", ["RW"] = true },
            new JsonObject { ["Type"] = "volume", ["Name"] = cache, ["Destination"] = "/cache", ["RW"] = false },
            new JsonObject { ["Type"] = "volume", ["Name"] = logs, ["Destination"] = "/logs", ["RW"] = true },
            new JsonObject { ["Type"] = "tmpfs", ["Destination"] = "/scratch", ["RW"] = true });

        var body = Build(postgres);

        // "/cache" alone in Binds would make a new, empty volume: it goes, and the one it made is mounted by name.
        Assert.Equal(["postgres-data:/var/lib/postgresql/data"], HostConfig(body)["Binds"]!.AsArray().Select(b => (string)b!));
        Assert.Equal(
            $$"""[{"Type":"volume","Target":"/logs","Source":"{{logs}}"},{"Type":"tmpfs","Target":"/scratch"},{"Type":"volume","Source":"{{anonymous}}","Target":"/var/lib/postgresql","ReadOnly":false},{"Type":"volume","Source":"{{cache}}","Target":"/cache","ReadOnly":true}]""",
            HostConfig(body)["Mounts"]!.ToJsonString());
        Assert.Equal(
            [new CarriedVolume("postgres-data", "/var/lib/postgresql/data", false), new CarriedVolume(anonymous, "/var/lib/postgresql", true),
             new CarriedVolume(cache, "/cache", true), new CarriedVolume(logs, "/logs", true)],
            CloneSpec.Volumes(postgres));
    }

    [Fact]
    public void A_volume_mounted_where_the_image_declares_one_is_not_anonymous()
    {
        // As on this host: postgres-data sits right at the VOLUME path, so nothing else needs carrying.
        var postgres = DockerFixtures.Inspect("postgres");

        Assert.False(HostConfig(Build(postgres)).ContainsKey("Mounts"));
        Assert.Equal([new CarriedVolume("postgres-data", "/var/lib/postgresql", false)], CloneSpec.Volumes(postgres));
    }

    [Theory]
    [MemberData(nameof(Real))]
    public void Leaves_dockers_default_masked_and_read_only_paths_to_docker(string container)
    {
        // They list Docker's defaults, which change between versions and with the host's CPUs: null lets the daemon write
        // its current ones.
        var body = Build(DockerFixtures.Inspect(container));

        Assert.Null(HostConfig(body)["MaskedPaths"]);
        Assert.Null(HostConfig(body)["ReadonlyPaths"]);
        Assert.True(HostConfig(body).ContainsKey("MaskedPaths"));
    }

    [Fact]
    public void Keeps_an_unconfined_containers_empty_masked_paths()
    {
        // --security-opt systempaths=unconfined makes the CLI send both lists empty. Null would mask /proc again.
        var unconfined = DockerFixtures.Inspect("sample-index");
        unconfined["HostConfig"]!["MaskedPaths"] = new JsonArray();
        unconfined["HostConfig"]!["ReadonlyPaths"] = new JsonArray();

        var body = Build(unconfined);

        Assert.Equal("[]", HostConfig(body)["MaskedPaths"]!.ToJsonString());
        Assert.Equal("[]", HostConfig(body)["ReadonlyPaths"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_masked_paths_set_by_hand()
    {
        var custom = DockerFixtures.Inspect("sample-index");
        custom["HostConfig"]!["MaskedPaths"] = new JsonArray("/proc/kcore", "/opt/secrets");

        Assert.Equal("""["/proc/kcore","/opt/secrets"]""", HostConfig(Build(custom))["MaskedPaths"]!.ToJsonString());
    }

    [Fact]
    public void Copies_each_network_with_aliases_ip_mac_and_priority_but_not_live_state()
    {
        // Gitberth's own container runs on two networks.
        var body = Build(DockerFixtures.Inspect("gitberth"));

        Assert.Equal("""{"IPAMConfig":{},"Links":null,"Aliases":[],"DriverOpts":{},"GwPriority":0,"MacAddress":"36:df:50:08:02:9e"}""",
            Endpoint(body, "gitberth").ToJsonString());
        Assert.Equal("""{"IPAMConfig":null,"Links":null,"Aliases":["gitberth","gitberth"],"DriverOpts":null,"GwPriority":0,"MacAddress":"ae:a2:60:31:b6:49"}""",
            Endpoint(body, "gitberth_default").ToJsonString());
        Assert.Equal("gitberth_default", (string)HostConfig(body)["NetworkMode"]!);
    }

    [Fact]
    public void Keeps_a_static_address_and_a_running_containers_mac_address()
    {
        var postgres = Running(DockerFixtures.Inspect("postgres"));
        postgres["NetworkSettings"]!["Networks"] = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "172.30.0.10" },
                ["Links"] = null,
                ["Aliases"] = new JsonArray("postgres", "db"),
                ["DriverOpts"] = new JsonObject(),
                ["GwPriority"] = -1,
                ["NetworkID"] = "77777777777777777777777777777777",
                ["EndpointID"] = "88888888888888888888888888888888",
                ["Gateway"] = "172.30.0.1",
                ["IPAddress"] = "172.30.0.10",
                ["IPPrefixLen"] = 24,
                ["MacAddress"] = "02:42:ac:1e:00:0a",
                ["DNSNames"] = new JsonArray("postgres", "db", "0ebf31bbcd07"),
            },
        };
        postgres["HostConfig"]!["NetworkMode"] = "backend";
        var networks = Networks();
        networks.Add(new JsonObject { ["Name"] = "backend", ["Id"] = "77777777777777777777777777777777", ["Driver"] = "bridge" });

        var body = Build(postgres, networks: networks);

        Assert.Equal("""{"IPAMConfig":{"IPv4Address":"172.30.0.10"},"Links":null,"Aliases":["postgres","db"],"DriverOpts":{},"GwPriority":-1,"MacAddress":"02:42:ac:1e:00:0a"}""",
            Endpoint(body, "backend").ToJsonString());
        Assert.Equal("backend", (string)HostConfig(body)["NetworkMode"]!);
    }

    [Fact]
    public void A_stopped_container_and_an_ipvlan_address_get_no_mac_address()
    {
        // Docker clears a stopped container's MAC, and ipvlan uses its parent's: create would refuse one there.
        Assert.False(Endpoint(Build(DockerFixtures.Inspect("postgres")), "gitberth").ContainsKey("MacAddress"));

        var lan = Running(DockerFixtures.Inspect("postgres"));
        lan["NetworkSettings"]!["Networks"]!["gitberth-lan"] = new JsonObject
        {
            ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "10.1.1.230" },
            ["GwPriority"] = -1,
            ["MacAddress"] = "bc:24:11:00:00:01",
            ["IPAddress"] = "10.1.1.230",
        };
        var networks = Networks();
        networks.Add(new JsonObject { ["Name"] = "gitberth-lan", ["Id"] = "66666666666666666666666666666666", ["Driver"] = "ipvlan" });

        var body = Build(lan, networks: networks);

        Assert.Equal("""{"IPAMConfig":{"IPv4Address":"10.1.1.230"},"GwPriority":-1}""", Endpoint(body, "gitberth-lan").ToJsonString());
    }

    [Fact]
    public void Drops_an_alias_that_is_the_old_short_id()
    {
        // Engines before 26 put the short ID in Aliases; the new container gets its own.
        var postgres = DockerFixtures.Inspect("postgres");
        postgres["NetworkSettings"]!["Networks"]!["gitberth"]!["Aliases"] = new JsonArray("postgres", "0ebf31bbcd07", "db");

        Assert.Equal("""["postgres","db"]""", Endpoint(Build(postgres), "gitberth")["Aliases"]!.ToJsonString());
    }

    [Fact]
    public void Copies_its_own_links_as_docker_wrote_them()
    {
        // Docker's create reads back the /target:/self/alias form inspect shows. Others linking to it are a refusal
        // (RecreateRules), not something to rewrite.
        var web = DockerFixtures.Inspect("sample-index");
        web["HostConfig"]!["Links"] = new JsonArray("/sample-db:/sample-index/db");

        Assert.Equal("""["/sample-db:/sample-index/db"]""", HostConfig(Build(web))["Links"]!.ToJsonString());
    }

    [Fact]
    public void Gives_every_published_port_an_explicit_host_port()
    {
        // -P: Docker picked 32768 at this start, and would pick another at the next.
        var random = Running(DockerFixtures.Inspect("sample-index"));
        random["HostConfig"]!["PublishAllPorts"] = true;
        random["HostConfig"]!["PortBindings"] = new JsonObject { ["8080/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "", ["HostPort"] = "" }) };
        random["Config"]!["ExposedPorts"]!["8080/tcp"] = new JsonObject();
        random["NetworkSettings"]!["Ports"] = new JsonObject
        {
            ["80/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "0.0.0.0", ["HostPort"] = "32768" }, new JsonObject { ["HostIp"] = "::", ["HostPort"] = "32768" }),
            ["8080/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "0.0.0.0", ["HostPort"] = "32769" }, new JsonObject { ["HostIp"] = "::", ["HostPort"] = "32769" }),
        };

        Assert.Equal(
            [new CurrentPort(80, "tcp", [new HostBinding("", 32768, Random: true)]), new CurrentPort(8080, "tcp", [new HostBinding("", 32769, Random: true)])],
            ContainerPorts.Read(random), new CurrentPortComparer());

        var body = Build(random);

        Assert.Equal("""{"80/tcp":[{"HostIp":"","HostPort":"32768"}],"8080/tcp":[{"HostIp":"","HostPort":"32769"}]}""", HostConfig(body)["PortBindings"]!.ToJsonString());
        Assert.False((bool)HostConfig(body)["PublishAllPorts"]!);
    }

    [Fact]
    public void Publishes_each_access_on_its_own_address()
    {
        var postgres = DockerFixtures.Inspect("postgres");
        PortRule[] rules =
        [
            new(5432, "tcp", PortAccess.Lan, 5432),
            new(8080, "tcp", PortAccess.HostOnly, 3002),
            new(9000, "tcp", PortAccess.Custom, 9000, "10.1.1.80"),
            new(53, "udp", PortAccess.Lan, 5353),
            new(9229, "tcp", PortAccess.Internal),
        ];

        var body = Build(postgres, rules);

        Assert.Equal(
            """{"5432/tcp":[{"HostIp":"","HostPort":"5432"}],"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"3002"}],"9000/tcp":[{"HostIp":"10.1.1.80","HostPort":"9000"}],"53/udp":[{"HostIp":"","HostPort":"5353"}]}""",
            HostConfig(body)["PortBindings"]!.ToJsonString());
        // A host with a global IPv6 address publishes the LAN on IPv4 only.
        Assert.Equal("0.0.0.0", (string)HostConfig(Build(postgres, [rules[0]], lanHostIp: "0.0.0.0"))["PortBindings"]!["5432/tcp"]![0]!["HostIp"]!);
    }

    [Fact]
    public void A_lan_port_nobody_touched_keeps_its_address()
    {
        // sample-index publishes on 0.0.0.0 (IPv4 only): a draft that leaves it as LAN on 9100 changes nothing.
        var sample = DockerFixtures.Inspect("sample-index");

        var body = Build(sample, [new PortRule(80, "tcp", PortAccess.Lan, 9100)]);

        Assert.Equal("""{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"9100"}]}""", HostConfig(body)["PortBindings"]!.ToJsonString());
        // Moved to another host port, it keeps the form it has for the LAN, 0.0.0.0, rather than the host's default.
        Assert.Equal("""{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"9201"}]}""",
            HostConfig(Build(sample, [new PortRule(80, "tcp", PortAccess.Lan, 9201)]))["PortBindings"]!.ToJsonString());
    }

    [Fact]
    public void A_port_that_becomes_lan_takes_the_form_the_container_has_for_the_lan()
    {
        // 8080 was taken to Host only; the container's other port is on 0.0.0.0. Back on the LAN, 8080 is too, whatever
        // the row's old 127.0.0.1 says: a round trip through Host only ends as it began.
        var sample = DockerFixtures.Inspect("sample-index");
        sample["Config"]!["ExposedPorts"]!["8080/tcp"] = new JsonObject();
        sample["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"9100"}],"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"9214"}]}""");
        var rules = ContainerPorts.Rules(sample).Select(r => r.Key == "8080/tcp" ? r with { Access = PortAccess.Lan } : r).ToList();
        Assert.Equal(new PortRule(8080, "tcp", PortAccess.Lan, 9214, "127.0.0.1"), rules[1]);

        Assert.Equal(new NewBinding(8080, "tcp", "0.0.0.0", 9214), CloneSpec.Bindings(sample, rules, "")[1]);
        // A port published anew takes it too.
        Assert.Equal(new NewBinding(9000, "tcp", "0.0.0.0", 9000), CloneSpec.Bindings(sample, [.. rules, new PortRule(9000, "tcp", PortAccess.Lan, 9000)], "")[2]);

        // With no LAN form of its own (its only port was Host only), the host's choice applies.
        var single = DockerFixtures.Inspect("sample-index");
        single["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"9214"}]}""");
        var back = ContainerPorts.Rules(single).Select(r => r with { Access = PortAccess.Lan }).ToList();
        Assert.Equal("", Assert.Single(CloneSpec.Bindings(single, back, "")).HostIp);
        // On a host with a global IPv6 address (the LAN on 0.0.0.0), a form that would reach IPv6 too isn't copied.
        var everywhere = DockerFixtures.Inspect("gitberth");
        everywhere["Config"]!["ExposedPorts"]!["9090/tcp"] = new JsonObject();
        Assert.Equal("0.0.0.0", CloneSpec.Bindings(everywhere, [new PortRule(9090, "tcp", PortAccess.Lan, 9090)], "0.0.0.0").Single().HostIp);
        Assert.Equal("", CloneSpec.Bindings(everywhere, [new PortRule(9090, "tcp", PortAccess.Lan, 9090)], "").Single().HostIp);
    }

    [Fact]
    public void A_row_whose_access_changed_gets_that_accesss_address()
    {
        // The draft starts from the rules read from Docker, each with Docker's address: switching a row's access moves it.
        var sample = DockerFixtures.Inspect("sample-index");
        var lan = Assert.Single(ContainerPorts.Rules(sample));
        Assert.Equal(new PortRule(80, "tcp", PortAccess.Lan, 9100, "0.0.0.0"), lan);

        Assert.Equal(new NewBinding(80, "tcp", "127.0.0.1", 9100), Assert.Single(CloneSpec.Bindings(sample, [lan with { Access = PortAccess.HostOnly }], "")));
        // And back: a Host only row read as 127.0.0.1, switched to LAN, gets the LAN's address.
        var hostOnly = DockerFixtures.Inspect("sample-index");
        hostOnly["HostConfig"]!["PortBindings"] = JsonNode.Parse("""{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"9100"}]}""");
        var local = Assert.Single(ContainerPorts.Rules(hostOnly));
        Assert.Equal(new NewBinding(80, "tcp", "", 9100), Assert.Single(CloneSpec.Bindings(hostOnly, [local with { Access = PortAccess.Lan }], "")));
        Assert.Equal(new NewBinding(80, "tcp", "0.0.0.0", 9100), Assert.Single(CloneSpec.Bindings(hostOnly, [local with { Access = PortAccess.Lan }], "0.0.0.0")));
        // An address that belongs to its access stays, and Custom keeps its own.
        Assert.Equal("::1", Assert.Single(CloneSpec.Bindings(hostOnly, [local with { HostIp = "::1" }], "")).HostIp);
        Assert.Equal("10.1.1.80", Assert.Single(CloneSpec.Bindings(sample, [lan with { Access = PortAccess.Custom, HostIp = "10.1.1.80" }], "")).HostIp);
    }

    [Theory]
    [InlineData("", "127.0.0.1", true)]
    [InlineData("", "::1", true)]
    [InlineData("0.0.0.0", "127.0.0.1", true)]
    [InlineData("0.0.0.0", "10.1.1.80", true)]
    [InlineData("::", "::1", true)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "::1", false)]
    [InlineData("10.1.1.80", "127.0.0.1", false)]
    [InlineData("0.0.0.0", "::", false)]
    [InlineData("0.0.0.0", "::1", false)]
    [InlineData("::", "10.1.1.80", false)]
    public void Bindings_of_one_host_port_clash_only_where_their_addresses_overlap(string a, string b, bool clash)
    {
        Assert.Equal(clash, ContainerPorts.Overlap(a, b));
        Assert.Equal(clash, ContainerPorts.Overlap(b, a));
    }

    [Fact]
    public void Adds_new_container_ports_to_exposed_ports()
    {
        var body = Build(DockerFixtures.Inspect("postgres"), [new PortRule(5432, "tcp", PortAccess.Lan, 5432), new PortRule(9187, "tcp", PortAccess.Internal)]);

        Assert.Equal(["5432/tcp", "9187/tcp"], body["ExposedPorts"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Uses_a_network_that_exists_when_its_network_mode_names_one_that_is_gone()
    {
        // On this host postgres's network mode is still "gitdock", removed in the rename, while its only network is
        // gitberth: a create with "gitdock" would fail, and so does postgres's own start today.
        var postgres = DockerFixtures.Inspect("postgres");

        Assert.Equal(("gitberth", "gitdock"), CloneSpec.NetworkModeFor(postgres, Networks()));
        Assert.Equal("gitberth", (string)HostConfig(Build(postgres))["NetworkMode"]!);
        // One that exists stays as it is.
        Assert.Equal(("bridge", (string?)null), CloneSpec.NetworkModeFor(DockerFixtures.Inspect("sample-index"), Networks()));
        Assert.Equal(("host", (string?)null), CloneSpec.NetworkModeFor(DockerFixtures.Inspect("cloudflare-tunnel"), Networks()));
    }

    [Fact]
    public void Leaves_out_a_network_that_no_longer_exists()
    {
        var postgres = DockerFixtures.Inspect("postgres");
        postgres["NetworkSettings"]!["Networks"]!["gitdock"] = new JsonObject { ["Aliases"] = new JsonArray("postgres") };

        var endpoints = Build(postgres)["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();

        Assert.Equal(["gitberth"], endpoints.Select(e => e.Key));
    }

    [Fact]
    public void Keeps_a_compose_containers_labels_as_they_are()
    {
        // Compose compares its config-hash label at the next "up"; a copy keeps it, so nothing about it looks changed.
        var compose = DockerFixtures.Inspect("postgres");
        compose["Config"]!["Labels"] = new JsonObject
        {
            ["com.docker.compose.project"] = "db",
            ["com.docker.compose.service"] = "postgres",
            ["com.docker.compose.config-hash"] = "a3233e1b1ce0125e02b238b096adb219459b71805318343efac890914620f82e",
            ["com.docker.compose.project.config_files"] = "/opt/db/docker-compose.yml",
        };

        Assert.True(JsonNode.DeepEquals(compose["Config"]!["Labels"], Build(compose)["Labels"]));
    }

    [Fact]
    public void A_container_in_another_containers_network_takes_that_ones_hostname()
    {
        var sidecar = DockerFixtures.Inspect("sample-index");
        sidecar["HostConfig"]!["NetworkMode"] = "container:0ebf31bbcd0757dab8ce5e88612810632828061d60c481b87b242f53a32b7150";
        sidecar["NetworkSettings"]!["Networks"] = new JsonObject();

        var body = Build(sidecar);

        Assert.False(body.ContainsKey("Hostname"));
        Assert.Equal("container:0ebf31bbcd0757dab8ce5e88612810632828061d60c481b87b242f53a32b7150", (string)HostConfig(body)["NetworkMode"]!);
    }

    [Fact]
    public void Reads_a_containers_ports_as_docker_has_them()
    {
        Assert.Equal([new CurrentPort(5432, "tcp", [])], ContainerPorts.Read(DockerFixtures.Inspect("postgres")), new CurrentPortComparer());
        Assert.Equal([new CurrentPort(80, "tcp", [new HostBinding("0.0.0.0", 9100)])], ContainerPorts.Read(DockerFixtures.Inspect("sample-index")), new CurrentPortComparer());
        Assert.Equal([new CurrentPort(8080, "tcp", [new HostBinding("", 8080)])], ContainerPorts.Read(DockerFixtures.Inspect("gitberth")), new CurrentPortComparer());
        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 9100, "0.0.0.0")], ContainerPorts.Rules(DockerFixtures.Inspect("sample-index")));
        Assert.Equal([new PortRule(5432, "tcp", PortAccess.Internal)], ContainerPorts.Rules(DockerFixtures.Inspect("postgres")));
        Assert.Empty(ContainerPorts.Read(DockerFixtures.Inspect("cloudflare-tunnel")));
    }

    [Theory]
    [InlineData("", 5432, "LAN at 10.1.1.80:5432")]
    [InlineData("0.0.0.0", 9100, "LAN at 10.1.1.80:9100")]
    [InlineData("127.0.0.1", 3002, "Host only at 127.0.0.1:3002")]
    [InlineData("::1", 3002, "Host only at [::1]:3002")]
    [InlineData("10.1.1.80", 5432, "LAN (10.1.1.80 only) at 10.1.1.80:5432")]
    public void Writes_a_binding_in_the_dialogs_words(string hostIp, int port, string text) =>
        Assert.Equal(text, ContainerPorts.Describe(hostIp, port, "10.1.1.80"));

    // Records holding lists compare by reference; these compare what they hold.
    private sealed class CurrentPortComparer : IEqualityComparer<CurrentPort>
    {
        public bool Equals(CurrentPort? x, CurrentPort? y) =>
            x is not null && y is not null && x.ContainerPort == y.ContainerPort && x.Protocol == y.Protocol && x.Bindings.SequenceEqual(y.Bindings);

        public int GetHashCode(CurrentPort obj) => HashCode.Combine(obj.ContainerPort, obj.Protocol);
    }
}
