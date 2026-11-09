using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The "probe" and "check" helpers a port change runs, against the engine kept in memory, and the check by name from
/// Gitberth's own container.
/// </summary>
public class HostHelpersTests
{
    private const string GitberthImage = "sha256:0c1a38670237d7bc11ee185585ec1309b54599d259ccd7930e81c2114472b125";

    private readonly FakeClock _clock = new();
    private readonly FakeEngine _engine;
    // The check's exit code, by the port it connects to.
    private readonly Dictionary<int, int> _exits = new();
    // The address each check connected to.
    private readonly List<string> _checked = [];

    public HostHelpersTests()
    {
        _engine = new FakeEngine(_clock)
        {
            // The check exits a moment after it starts, as `timeout 3 bash -c 'exec 3<>/dev/tcp/…'` does.
            OnCreate = b =>
            {
                if (b.Config["Labels"]?[HostScanHelper.Label]?.GetValue<string>() != HostScanHelper.CheckKind) return;
                _checked.Add(b.Config["Cmd"]![4]!.GetValue<string>());
                b.ExitsAfter = TimeSpan.FromMilliseconds(100);
                b.ExitWith = _exits.GetValueOrDefault(int.Parse(b.Config["Cmd"]![5]!.GetValue<string>()), 1);
            },
        };
        // Gitberth's own image declares VOLUME /data.
        _engine.Images["gitberth:local"] = new JsonObject { ["Id"] = GitberthImage, ["Config"] = new JsonObject { ["Volumes"] = new JsonObject { ["/data"] = new JsonObject() } } };
        _engine.Images["nginx:stable-alpine"] = new JsonObject { ["Id"] = "sha256:0e52bd054126cdc040b1276c0af347aeb02a41b9cfe305d6094767ac30e97949" };
        // Gitberth itself, known by its hostname as Docker names it, and a sample that publishes 9100.
        var self = DockerFixtures.Inspect("gitberth");
        self["Name"] = "/" + Environment.MachineName;
        _engine.Add(self);
        var sample = DockerFixtures.Inspect("sample-index");
        sample["State"]!["Running"] = true;
        sample["State"]!["StartedAt"] = "2026-11-01T10:00:00Z";
        _engine.Add(sample);
    }

    private HostHelpers Helpers() => new(_engine, new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<HostHelpers>.Instance) { InContainer = true };

    // With the answers a name lookup from Gitberth's own container gives.
    private HostHelpers Helpers(Func<string, CancellationToken, Task<IPAddress[]>> resolve) =>
        new(_engine, new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<HostHelpers>.Instance) { InContainer = true, Resolve = resolve };

    [Fact]
    public void The_probe_and_the_check_run_gitberths_image_with_nothing_else()
    {
        var probe = HostScanHelper.ProbeBody(GitberthImage, ["/data"],
            [new ProbePort("", 5432, "tcp"), new ProbePort("127.0.0.1", 5433, "tcp"), new ProbePort("::1", 5433, "tcp"), new ProbePort("0.0.0.0", 53, "udp")]);

        Assert.Equal(GitberthImage, probe["Image"]!.GetValue<string>());
        Assert.Equal("""["sleep"]""", probe["Entrypoint"]!.ToJsonString());
        Assert.Equal("""["5"]""", probe["Cmd"]!.ToJsonString());
        Assert.Equal("""{"gitberth.helper":"probe"}""", probe["Labels"]!.ToJsonString());
        var host = probe["HostConfig"]!;
        Assert.Equal("bridge", host["NetworkMode"]!.GetValue<string>());
        // Only the ports to try, each on its own address.
        Assert.Equal("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}],"5433/tcp":[{"HostIp":"127.0.0.1","HostPort":"5433"},{"HostIp":"::1","HostPort":"5433"}],"53/udp":[{"HostIp":"0.0.0.0","HostPort":"53"}]}""",
            host["PortBindings"]!.ToJsonString());
        Assert.Equal("""{"5432/tcp":{},"5433/tcp":{},"53/udp":{}}""", probe["ExposedPorts"]!.ToJsonString());
        // Read-only, without capabilities or a log, 32 MiB, and a tmpfs where the image wants a volume: no volume is made.
        Assert.True(host["ReadonlyRootfs"]!.GetValue<bool>());
        Assert.Equal("""["ALL"]""", host["CapDrop"]!.ToJsonString());
        Assert.Equal(32L * 1024 * 1024, host["Memory"]!.GetValue<long>());
        Assert.Equal("""{"Type":"none"}""", host["LogConfig"]!.ToJsonString());
        Assert.Equal("""{"/data":""}""", host["Tmpfs"]!.ToJsonString());
        Assert.Null(host["RestartPolicy"]);

        var check = HostScanHelper.CheckBody(GitberthImage, [], "10.1.1.80", 5432);
        Assert.Equal("""["timeout"]""", check["Entrypoint"]!.ToJsonString());
        Assert.Equal(["3", "bash", "-c", "exec 3<>/dev/tcp/$0/$1", "10.1.1.80", "5432"], check["Cmd"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(("host", "check"), (check["HostConfig"]!["NetworkMode"]!.GetValue<string>(), check["Labels"]![HostScanHelper.Label]!.GetValue<string>()));
        Assert.Null(check["HostConfig"]!["PortBindings"]);
        Assert.StartsWith("gitberth-helper-check-", HostScanHelper.NewName(HostScanHelper.CheckKind));
    }

    [Fact]
    public async Task The_probe_says_which_port_is_taken_and_leaves_nothing_behind()
    {
        var helpers = Helpers();
        var boxes = _engine.Boxes.Count;
        var volumes = _engine.Volumes.Count;

        // sample-index publishes 9100 on 0.0.0.0, which every address takes too.
        var taken = await helpers.ProbeAsync([new ProbePort("", 9100, "tcp")], default);
        Assert.Contains("Bind for 0.0.0.0:9100 failed: port is already allocated", taken);
        Assert.Equal(9100, PortChanger.PortIn(taken!, [9100]));
        Assert.Null(await helpers.ProbeAsync([new ProbePort("", 9201, "tcp"), new ProbePort("127.0.0.1", 9202, "tcp")], default));

        // Each run made one container of Gitberth's image under a name of its own, and removed it; no volume was made.
        var made = _engine.Calls.Where(c => c.StartsWith("create ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, made.Count);
        Assert.All(made, c => Assert.StartsWith("create gitberth-helper-probe-", c));
        Assert.Equal(made.Select(c => c.Replace("create ", "remove ", StringComparison.Ordinal) + " force"), _engine.Calls.Where(c => c.StartsWith("remove ", StringComparison.Ordinal)));
        Assert.Equal((boxes, volumes), (_engine.Boxes.Count, _engine.Volumes.Count));
    }

    [Fact]
    public async Task The_check_reads_its_exit_code()
    {
        _exits[9100] = 0;
        _exits[9201] = 1;
        _exits[9202] = 124;
        _exits[9203] = 127;
        var helpers = Helpers();

        Assert.Equal(ConnectResult.Yes, await helpers.ConnectAsync("10.1.1.80", 9100, default));
        // Refused, or no answer in 3 s: nothing answers.
        Assert.Equal(ConnectResult.No, await helpers.ConnectAsync("10.1.1.80", 9201, default));
        Assert.Equal(ConnectResult.No, await helpers.ConnectAsync("10.1.1.80", 9202, default));
        // bash or timeout missing: it can't tell, which never fails a change.
        var unknown = await helpers.ConnectAsync("10.1.1.80", 9203, default);
        Assert.True(unknown.Unknown);
        Assert.Equal("the check helper exited with code 127", unknown.Error);
        Assert.All(_engine.Calls.Where(c => c.StartsWith("create ", StringComparison.Ordinal)), c => Assert.StartsWith("create gitberth-helper-check-", c));
        Assert.DoesNotContain(_engine.Boxes, b => b.Name.StartsWith(HostScanHelper.NamePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_gitberth_cannot_resolve_is_a_check_that_could_not_run()
    {
        // bash exits 1 for a name it can't resolve, as for a refused connect, so the helper is never asked: nothing answering
        // would undo a change that works.
        var helpers = Helpers((_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));

        var check = await helpers.ConnectAsync("docker.local", 9100, default);

        Assert.Equal(new ConnectResult(false, "Gitberth can't resolve docker.local"), check);
        Assert.True(check.Unknown);
        Assert.Empty(_checked);
    }

    [Fact]
    public async Task A_name_is_checked_at_the_address_it_resolves_to_ipv4_first()
    {
        _exits[9100] = 0;
        // The LAN's ports may be published on IPv4 only, so a name's IPv4 address is the one tried; an address isn't looked up.
        var helpers = Helpers((name, _) => Task.FromResult<IPAddress[]>(name == "docker.lan" ? [IPAddress.Parse("fd00::50"), IPAddress.Parse("10.1.1.80")] : []));

        Assert.Equal(ConnectResult.Yes, await helpers.ConnectAsync("docker.lan", 9100, default));
        Assert.Equal(ConnectResult.Yes, await helpers.ConnectAsync("10.1.1.80", 9100, default));
        Assert.Equal(["10.1.1.80", "10.1.1.80"], _checked);
    }

    [Fact]
    public async Task Docker_failing_leaves_the_probe_silent_and_the_check_unknown()
    {
        _engine.Fail = (call, _) => call == "create" ? new EngineException(500, "no space left on device") : null;
        var helpers = Helpers();

        // The new container's start would still find a taken port, and put the old one back.
        Assert.Null(await helpers.ProbeAsync([new ProbePort("", 9100, "tcp")], default));
        var check = await helpers.ConnectAsync("10.1.1.80", 9100, default);
        Assert.Equal(new ConnectResult(false, "no space left on device"), check);
    }

    [Fact]
    public void Leftovers_are_the_probes_and_checks_a_restart_left_behind()
    {
        var since = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        static JsonObject Listed(string id, string? kind, DateTimeOffset created) => new()
        {
            ["Id"] = id,
            ["Created"] = created.ToUnixTimeSeconds(),
            ["Labels"] = kind is null ? new JsonObject() : new JsonObject { [HostScanHelper.Label] = kind },
        };
        var list = new JsonArray(
            Listed("probe-old", "probe", since.AddMinutes(-3)),
            Listed("check-old", "check", since.AddSeconds(-5)),
            // This run's own, and the host scan's, which it removes itself; and anyone else's.
            Listed("probe-now", "probe", since.AddSeconds(2)),
            Listed("ports-old", "ports", since.AddMinutes(-3)),
            Listed("postgres", null, since.AddDays(-3)));

        Assert.Equal(["probe-old", "check-old"], HostScanHelper.Leftovers(list, since));
    }

    [Fact]
    public async Task Outside_a_container_the_check_connects_itself_and_there_is_no_probe()
    {
        var helpers = new HostHelpers(_engine, new GitberthOptions { DataDir = Path.GetTempPath() }, NullLogger<HostHelpers>.Instance);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(ConnectResult.Yes, await helpers.ConnectAsync("127.0.0.1", port, default));
        listener.Stop();
        Assert.Equal(ConnectResult.No, await helpers.ConnectAsync("127.0.0.1", port, default));
        Assert.Null(await helpers.ProbeAsync([new ProbePort("", port, "tcp")], default));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("create ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task By_name_only_over_a_network_gitberth_shares()
    {
        // Gitberth is on gitberth and gitberth_default; postgres on gitberth; the samples only on Docker's default bridge.
        Assert.Equal(["gitberth", "gitberth_default"], NameCheck.Named(DockerFixtures.Inspect("gitberth")).Order(StringComparer.Ordinal));
        Assert.Equal(["gitberth"], NameCheck.Named(DockerFixtures.Inspect("postgres")));
        Assert.Empty(NameCheck.Named(DockerFixtures.Inspect("sample-index")));

        // Outside a container Gitberth reaches nothing by name.
        var outside = new NameCheck(_engine, new GitberthOptions { DataDir = Path.GetTempPath() });
        Assert.Null(await outside.AnswersAsync("postgres", 5432, default));
        // Inside one, a container that's only on the default bridge can't be told either.
        var inside = new NameCheck(_engine, new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = true });
        Assert.Null(await inside.AnswersAsync("sample-index", 80, default));
        Assert.Null(await inside.AnswersAsync("gone", 80, default));
    }
}
