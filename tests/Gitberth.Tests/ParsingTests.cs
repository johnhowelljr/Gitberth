using System.Globalization;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;

namespace Gitberth.Tests;

public class GitHubRepoRefTests
{
    [Theory]
    [InlineData("https://github.com/acme/web", "acme", "web", null)]
    [InlineData("https://github.com/acme/web.git", "acme", "web", null)]
    [InlineData("github.com/acme/web/", "acme", "web", null)]
    [InlineData("git@github.com:acme/web.git", "acme", "web", null)]
    [InlineData("  https://www.github.com/acme/web  ", "acme", "web", null)]
    [InlineData("https://github.com/acme/web/tree/feature/x/src/api", "acme", "web", "feature/x/src/api")]
    public void Parses_pasted_urls(string input, string owner, string repo, string? tree)
    {
        Assert.True(GitHubRepoRef.TryParse(input, out var parsed, out var error), error);
        Assert.Equal(new GitHubRepoRef(owner, repo, tree), parsed);
        Assert.Equal($"https://github.com/{owner}/{repo}.git", parsed!.CloneUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://gitlab.com/acme/web")]
    [InlineData("https://github.com/acme")]
    [InlineData("not a url at all")]
    public void Rejects_bad_urls(string input)
    {
        Assert.False(GitHubRepoRef.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Tree_path_uses_the_longest_matching_branch()
    {
        Assert.Equal(("feature/x", "src/api"), GitHubRepoRef.ResolveTreePath("feature/x/src/api", ["main", "feature", "feature/x"]));
        Assert.Equal(("main", (string?)null), GitHubRepoRef.ResolveTreePath("main", ["main"]));
        Assert.Null(GitHubRepoRef.ResolveTreePath("nope/x", ["main"]));
    }

    [Fact]
    public void Git_errors_are_explained()
    {
        var repo = new GitHubRepoRef("acme", "web");
        Assert.Contains("needs a GitHub token", GitClient.Explain("fatal: could not read Username for 'https://github.com': terminal prompts disabled", repo, null, hasToken: false));
        Assert.Contains("rejected the token", GitClient.Explain("fatal: Authentication failed for 'https://github.com/acme/web.git/'", repo, null, hasToken: true));
        Assert.Contains("can't find acme/web", GitClient.Explain("remote: Repository not found.\nfatal: repository 'https://github.com/acme/web.git/' not found", repo, null, hasToken: true));
        Assert.Contains("Branch 'dev' doesn't exist", GitClient.Explain("fatal: Remote branch dev not found in upstream origin", repo, "dev", hasToken: true));
        Assert.Equal("Can't reach github.com. Check the host's internet connection and DNS.",
            GitClient.Explain("fatal: unable to access 'https://github.com/acme/web.git/': Could not resolve host: github.com", repo, null, hasToken: true));
    }
}

public class NodeVersionTests
{
    [Theory]
    [InlineData(">=18", 24)]
    [InlineData("^20.11.0", 20)]
    [InlineData("20.x", 20)]
    [InlineData("v22.3.0", 22)]
    [InlineData(">=18 <23", 22)]
    [InlineData("18 || 20", 20)]
    [InlineData("lts/iron", 20)]
    [InlineData("lts/*", 24)]
    [InlineData("node", 24)]
    [InlineData("~26.1", 26)]
    [InlineData("16", 16)]
    public void Picks_a_major_version(string spec, int expected) => Assert.Equal(expected, NodeVersions.FromSpec(spec, 24));
}

public class DotNetFrameworkTests
{
    [Fact]
    public void Picks_the_best_target_framework()
    {
        var best = DotNetProject.ParseTargetFrameworks("net8.0-windows;net10.0;net9.0")!;
        Assert.Equal("net10.0", best.Moniker);
        Assert.Equal("10.0", best.Version);
        Assert.False(best.WindowsOnly);
        Assert.True(DotNetProject.ParseTargetFrameworks("net48")!.IsNetFramework);
        Assert.Equal("3.1", DotNetProject.ParseTargetFrameworks("netcoreapp3.1")!.Version);
        Assert.Equal("11.0-preview", DockerfileTemplates.DotNetTag(DotNetProject.ParseTargetFrameworks("net11.0")!));
    }
}

public class DockerfileMarkerTests
{
    private const string Input = "FROM x\n# gitberth:build-args\nRUN build\n# gitberth:volumes\nCMD y\n";

    [Fact]
    public void Expands_build_args_and_volume_dirs()
    {
        var output = DockerfileMarkers.Expand(Input, ["VITE_A", "B"], ["/app/data", "/var/x y"]);
        Assert.Contains("ARG VITE_A\nARG B\nRUN build", output);
        Assert.Contains("RUN mkdir -p '/app/data' '/var/x y' && chown -R \"${APP_UID:-0}\" '/app/data' '/var/x y'", output);
        Assert.DoesNotContain("gitberth:", output);
    }

    [Fact]
    public void Removes_markers_when_there_is_nothing_to_add() =>
        Assert.Equal("FROM x\nRUN build\nCMD y\n", DockerfileMarkers.Expand(Input, [], []));
}

public class ContainerSpecTests
{
    [Fact]
    public void Builds_docker_run_args_without_leaking_secrets()
    {
        var app = new Data.AppDefinition
        {
            Id = 1,
            Slug = "web",
            MemoryLimitMb = 512,
            CpuLimit = 1.5,
            Ports =
            [
                new Data.PortMapping { Id = 1, HostPort = 8101, ContainerPort = 8080 },
                new Data.PortMapping { Id = 2, HostPort = 5353, ContainerPort = 53, Protocol = "udp", Access = Network.PortAccess.HostOnly },
                new Data.PortMapping { Id = 3, HostPort = 3002, ContainerPort = 3000, Access = Network.PortAccess.HostOnly, IsWeb = true },
                new Data.PortMapping { Id = 4, ContainerPort = 9229, Access = Network.PortAccess.Internal },
            ],
            Volumes =
            [
                new Data.VolumeMount { Id = 1, Source = "data", Target = "/app/data" },
                new Data.VolumeMount { Id = 2, Source = "/srv/web", Target = "/srv", ReadOnly = true },
            ],
        };
        List<EnvValue> env = [new("PLAIN", "hello world", false), new("API_KEY", "s3cr3t-value", true), new("DOCKER_HOST", "tcp://x", true)];
        var spec = ContainerSpecFactory.For(app, "gitberth/web:abc1234", env, "gitberth");
        var (args, processEnv) = spec.BuildRunCommand();

        Assert.Equal(["run", "-d", "--name", "gb-web"], args.Take(4));
        Assert.Equal("gitberth/web:abc1234", args[^1]);
        // On the shared network under its network name; an Internal port is only exposed, for containers there.
        Assert.Equal(["--network", "name=gitberth,alias=web"], args.Skip(args.IndexOf("--network")).Take(2));
        Assert.DoesNotContain("--network-alias", args);
        Assert.Equal(["8101:8080", "127.0.0.1:5353:53/udp", "127.0.0.1:3002:3000"], args.Select((a, i) => (a, i)).Where(x => x.a == "-p").Select(x => args[x.i + 1]));
        Assert.Equal(["--expose", "9229"], args.Skip(args.IndexOf("--expose")).Take(2));
        Assert.Contains("PLAIN=hello world", args);
        Assert.Contains("API_KEY", args);
        Assert.DoesNotContain(args, a => a.Contains("s3cr3t-value"));
        Assert.Equal("s3cr3t-value", processEnv["API_KEY"]);
        Assert.Contains("DOCKER_HOST=tcp://x", args);
        Assert.Contains("gb-web-data:/app/data", args);
        Assert.Contains("/srv/web:/srv:ro", args);
        Assert.Contains("512m", args);
        Assert.Contains("1.5", args);
        Assert.Contains("gitberth.app=web", args);

        var display = spec.ToDisplayString();
        Assert.DoesNotContain("s3cr3t-value", display);
        Assert.DoesNotContain("tcp://x", display);
        Assert.Contains("API_KEY=••••", display);
        Assert.Contains("'PLAIN=hello world'", display);
        // The equivalent docker run shows how each port and network is set up.
        Assert.Contains("--network name=gitberth,alias=web \\\n", display);
        Assert.Contains("-p 127.0.0.1:3002:3000 \\\n", display);
        Assert.Contains("--expose 9229 \\\n", display);
    }

    private static Data.AppDefinition WithNetworks() => new()
    {
        Id = 1,
        Slug = "kanban",
        Ports = [new Data.PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000, IsWeb = true }],
        Networks =
        [
            new Data.AppNetwork { Id = 1, Network = "media_default", Alias = "db" },
            // An own address on the LAN, which only the app's own container takes.
            new Data.AppNetwork { Id = 2, Network = "gitberth-lan", IPv4Address = "10.1.1.230" },
            // A row for the shared network only adds its name there, next to the app's network name.
            new Data.AppNetwork { Id = 3, Network = "gitberth", Alias = "api" },
        ],
    };

    private static List<string> Networks(List<string> args) => args.Select((a, i) => (a, i)).Where(x => x.a == "--network").Select(x => args[x.i + 1]).ToList();

    [Fact]
    public void Extra_networks_join_with_a_lower_gateway_priority()
    {
        var app = WithNetworks();

        // The shared network carries the published ports and the way out; the others never take them over.
        Assert.Equal(
            ["name=gitberth,alias=kanban,alias=api", "name=media_default,gw-priority=-1,alias=kanban,alias=db", "name=gitberth-lan,gw-priority=-1,alias=kanban,ip=10.1.1.230"],
            Networks(ContainerSpecFactory.For(app, "img", [], "gitberth").BuildRunCommand().Args));
        // Docker before 28 has no gateway priorities, and its CLI refuses one.
        Assert.Equal(
            ["name=gitberth,alias=kanban,alias=api", "name=media_default,alias=kanban,alias=db", "name=gitberth-lan,alias=kanban,ip=10.1.1.230"],
            Networks(ContainerSpecFactory.For(app, "img", [], "gitberth", gatewayPriority: false).BuildRunCommand().Args));
        // A deploy's new version, next to the old one: no names until it has passed its checks, and never the old one's
        // address. It isn't on gitberth-lan at all: joined without the address, Docker would give it another of the block's.
        Assert.Equal(
            ["name=gitberth", "name=media_default,gw-priority=-1"],
            Networks(ContainerSpecFactory.For(app, "img", [], "gitberth", name: "gb-kanban_next", bridge: true).BuildRunCommand().Args));
    }

    [Fact]
    public void A_network_whose_name_has_capitals_goes_to_docker_run_by_its_id()
    {
        var app = new Data.AppDefinition
        {
            Id = 1,
            Slug = "kanban",
            Ports = [new Data.PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000, IsWeb = true }],
            Networks = [new Data.AppNetwork { Id = 1, Network = "MediaNet", Alias = "db" }],
        };
        var (shared, media) = (new string('a', 64), new string('b', 64));

        // docker run lower-cases --network name=…, so MyApps and MediaNet go by their IDs, which have no capitals.
        var spec = ContainerSpecFactory.For(app, "img", [], "MyApps", networkIds: new Dictionary<string, string> { ["MyApps"] = shared, ["MediaNet"] = media });
        Assert.Equal([$"name={shared},alias=kanban", $"name={media},gw-priority=-1,alias=kanban,alias=db"], Networks(spec.BuildRunCommand().Args));
        // Docker files the container under their names, and the settings are the same either way.
        Assert.Equal(["MyApps", "MediaNet"], spec.Networks.Select(n => n.Name));
        Assert.Equal(ContainerSpecFactory.RuntimeKey(app, "MyApps"), spec.RuntimeKey);
        Assert.Contains("--network name=MediaNet,alias=kanban,alias=db", spec.RuntimeKey);
    }

    [Fact]
    public void Lan_ports_go_on_ipv4_only_when_the_host_has_an_ipv6_address_the_internet_reaches()
    {
        var app = new Data.AppDefinition
        {
            Slug = "web",
            Ports =
            [
                new Data.PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000, IsWeb = true },
                new Data.PortMapping { Id = 2, HostPort = 3002, ContainerPort = 3002, Access = Network.PortAccess.HostOnly },
            ],
        };
        var everywhere = ContainerSpecFactory.For(app, "img", [], "gitberth");
        var ipv4 = ContainerSpecFactory.For(app, "img", [], "gitberth", lanIpv4Only: true);

        Assert.Contains("3001:3000", everywhere.BuildRunCommand().Args);
        Assert.Contains("0.0.0.0:3001:3000", ipv4.BuildRunCommand().Args);
        Assert.Contains("127.0.0.1:3002:3002", ipv4.BuildRunCommand().Args);
        // Who reaches each port is the same either way, so neither container is out of date with the other's settings.
        Assert.Equal(everywhere.RuntimeKey, ipv4.RuntimeKey);
    }

    [Fact]
    public void The_runtime_key_has_the_port_and_network_arguments_but_not_the_web_marker()
    {
        var app = WithNetworks();
        var key = ContainerSpecFactory.RuntimeKey(app, "gitberth");
        Assert.Equal(
            "-p 3001:3000 --network name=gitberth,alias=kanban,alias=api --network name=gitberth-lan,alias=kanban,ip=10.1.1.230 --network name=media_default,alias=kanban,alias=db",
            key);

        // Moving the web marker restarts nothing; neither does an engine that now takes gateway priorities.
        app.Ports[0].IsWeb = false;
        Assert.Equal(key, ContainerSpecFactory.RuntimeKey(app, "gitberth"));
        Assert.Equal(ContainerSpecFactory.For(app, "img", [], "gitberth").RuntimeKey, ContainerSpecFactory.For(app, "img", [], "gitberth", gatewayPriority: false).RuntimeKey);

        // Who reaches a port, an added Internal one, and another network all do.
        app.Ports[0].Access = Network.PortAccess.HostOnly;
        Assert.NotEqual(key, ContainerSpecFactory.RuntimeKey(app, "gitberth"));
        app.Ports[0].Access = Network.PortAccess.Lan;
        app.Ports.Add(new Data.PortMapping { Id = 2, ContainerPort = 9229, Access = Network.PortAccess.Internal });
        Assert.Contains("--expose 9229", ContainerSpecFactory.RuntimeKey(app, "gitberth"));
        app.Ports.RemoveAt(1);
        app.Networks.Add(new Data.AppNetwork { Id = 4, Network = "cache_default" });
        Assert.NotEqual(key, ContainerSpecFactory.RuntimeKey(app, "gitberth"));
    }

    [Fact]
    public void Log_options_are_only_added_when_given()
    {
        var app = new Data.AppDefinition { Slug = "web" };
        var (plain, _) = ContainerSpecFactory.For(app, "img", [], "gitberth").BuildRunCommand();
        Assert.DoesNotContain("--log-opt", plain);

        var (rotated, _) = ContainerSpecFactory.For(app, "img", [], "gitberth", ["max-size=10m", "max-file=3"]).BuildRunCommand();
        Assert.Equal(["--log-opt", "max-size=10m", "--log-opt", "max-file=3"], rotated.Skip(rotated.IndexOf("--log-opt")).Take(4));
    }

    [Fact]
    public void Lists_the_names_a_build_can_reach_on_the_network()
    {
        const string inspect = """
            [
              { "Id": "4be2f39a8c1d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1", "Name": "/gb-kanban",
                "NetworkSettings": { "Networks": { "gitberth": { "IPAddress": "172.20.0.4", "Aliases": ["kanban", "4be2f39a8c1d"] } } } },
              { "Id": "9c0ffee", "Name": "/postgres",
                "NetworkSettings": { "Networks": { "gitberth": { "IPAddress": "172.20.0.5", "Aliases": null } } } },
              { "Id": "abc", "Name": "/elsewhere",
                "NetworkSettings": { "Networks": { "bridge": { "IPAddress": "172.17.0.2" } } } }
            ]
            """;
        Assert.Equal(
            [("gb-kanban", "172.20.0.4"), ("kanban", "172.20.0.4"), ("postgres", "172.20.0.5")],
            DockerCli.ParseNetworkHosts(inspect, "gitberth"));
        Assert.Empty(DockerCli.ParseNetworkHosts("Error: No such container", "gitberth"));
    }

    [Fact]
    public void Reads_the_names_from_inspect_lines_that_carry_nothing_else()
    {
        // As Docker 29 prints them for NetworkHostsAsync's format: one object per container, with no settings in it.
        const string lines = """
            {"Id":"4be2f39a8c1d","Name":"/gb-kanban","NetworkSettings":{"Networks":{"gitberth":{"IPAddress":"172.20.0.4","Aliases":["kanban"]}}}}
            {"Id":"9c0ffee","Name":"/postgres","NetworkSettings":{"Networks":{"gitberth":{"IPAddress":"172.20.0.5","Aliases":["postgres"]}}}}

            """;
        Assert.Equal(
            [("gb-kanban", "172.20.0.4"), ("kanban", "172.20.0.4"), ("postgres", "172.20.0.5")],
            DockerCli.ParseNetworkHostLines(lines, "gitberth"));
        Assert.Empty(DockerCli.ParseNetworkHostLines("", "gitberth"));
    }

    [Fact]
    public void Parses_published_ports() =>
        Assert.Equal([8101, 9000], DockerCli.ParsePublishedPorts("0.0.0.0:8101->8080/tcp, [::]:8101->8080/tcp, 127.0.0.1:9000->9000/udp, 3000/tcp"));

    [Theory]
    [InlineData("1.56", true)]
    [InlineData("1.48", true)]
    [InlineData("1.47", false)]
    [InlineData(null, false)]
    public async Task Joins_networks_without_taking_over_the_gateway(string? api, bool priority)
    {
        // At priority -1 the joined network never becomes the container's gateway, so its published ports and its way out
        // stay where they are. Docker before 28 (API 1.48) has no priorities, and its CLI refuses the flag.
        var calls = new List<IReadOnlyList<string>>();
        var docker = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(args[0] == "version"
                    ? api is null ? new ProcessResult(1, "", "Cannot connect to the Docker daemon") : new ProcessResult(0, api + "\n", "")
                    : new ProcessResult(0, "", ""));
            },
        };

        Assert.Null(await docker.ConnectToNetworkAsync("455b8ea31b6d"));

        Assert.Equal(["version", "--format", "{{.Server.APIVersion}}"], calls[0]);
        Assert.Equal(priority ? ["network", "connect", "--gw-priority", "-1", "gitberth", "455b8ea31b6d"] : ["network", "connect", "gitberth", "455b8ea31b6d"],
            calls[^1]);
    }

    [Fact]
    public async Task Asks_the_engines_api_version_once()
    {
        var asked = 0;
        var docker = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, _, _) =>
            {
                if (args[0] == "version") asked++;
                return Task.FromResult(new ProcessResult(0, args[0] == "version" ? "1.56" : "", ""));
            },
        };
        await docker.ConnectToNetworkAsync("a");
        await docker.ConnectToNetworkAsync("b");
        Assert.Equal(1, asked);
        Assert.Equal(new Version(1, 56), await docker.ServerApiVersionAsync());
    }

    [Fact]
    public async Task A_container_already_on_the_network_has_joined()
    {
        var docker = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, _, _) => Task.FromResult(args[0] == "version"
                ? new ProcessResult(0, "1.56", "")
                : new ProcessResult(1, "", "Error response from daemon: endpoint with name gitberth already exists in network gitberth")),
        };
        Assert.Null(await docker.ConnectToNetworkAsync("gitberth"));
    }
}

public class MiscParsingTests
{
    [Fact]
    public void Parses_dotenv()
    {
        var parsed = DotEnv.Parse("# comment\nA=1\nexport B=two words # trailing\nC=\"quoted # not comment\"\nD='single'\nE=\n=bad\nF=a=b\n");
        Assert.Equal(
            [("A", "1"), ("B", "two words"), ("C", "quoted # not comment"), ("D", "single"), ("E", ""), ("F", "a=b")],
            parsed.Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void Hashes_and_verifies_passwords()
    {
        var hash = AdminPassword.Hash("correct horse battery");
        Assert.True(AdminPassword.Verify("correct horse battery", hash));
        Assert.False(AdminPassword.Verify("wrong", hash));
        Assert.False(AdminPassword.Verify("x", "garbage"));
    }

    [Theory]
    [InlineData("2026-12-01 00:00:00 UTC", "2026-12-01T00:00:00+00:00")]
    [InlineData("2026-12-01 10:30:00 -0800", "2026-12-01T10:30:00-08:00")]
    [InlineData("2026-12-01 10:30:00 +0530", "2026-12-01T10:30:00+05:30")]
    public void Parses_token_expiry_header(string header, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), GitHubApi.ParseExpiry(header));

    [Theory]
    [InlineData("My Cool App!", "my-cool-app")]
    [InlineData("  api  ", "api")]
    [InlineData("web--front__end", "web-front-end")]
    public void Slugifies_names(string name, string slug) => Assert.Equal(slug, AppService.Slugify(name));
}
