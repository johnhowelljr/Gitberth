using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>Reading the host's network from its /proc files, as captured on CT 101, and the helper container that prints them.</summary>
public class ProcNetTests
{
    private static readonly GitberthOptions Options = new() { DataDir = Path.GetTempPath() };

    private static HostScan Scan() => HostScanHelper.Read(DockerFixtures.ProcFiles(), DateTime.UtcNow);

    private static List<HostPortUse> HostOnly(HostScan scan) =>
        HostPortMap.Build(new HostPortSources(NetworkSnapshot.Empty, [], [], new HashSet<int>(), scan, null));

    [Fact]
    public void Reads_listening_tcp_sockets()
    {
        var v4 = ProcNet.ParseSockets(DockerFixtures.Text("proc-net-tcp.txt"), "tcp");
        Assert.Contains(new ProcNetSocket("tcp", "0.0.0.0", 8080), v4);
        Assert.Contains(new ProcNetSocket("tcp", "0.0.0.0", 3001), v4);
        Assert.Contains(new ProcNetSocket("tcp", "0.0.0.0", 9114), v4);
        // Mail, on the host's loopback only.
        Assert.Contains(new ProcNetSocket("tcp", "127.0.0.1", 25), v4);
        Assert.Equal(64, v4.Count);

        // sshd, Docker's API, cloudflared's metrics and docker-proxy for Gitberth and the apps. The SSH session that's
        // open (state 01, from 10.1.3.168) isn't listening.
        Assert.Equal(
            [new("tcp", "::", 8080), new("tcp", "::", 20241), new("tcp", "::", 2375), new("tcp", "::", 3000), new("tcp", "::", 3001), new("tcp", "::", 22), new("tcp", "::1", 25)],
            ProcNet.ParseSockets(DockerFixtures.Text("proc-net-tcp6.txt"), "tcp"));
    }

    [Fact]
    public void A_tcp6_wildcard_also_takes_the_ipv4_port()
    {
        var ssh = new ProcNetSocket("tcp", "::", 22);
        Assert.True(ssh.AnyAddress);
        Assert.True(ssh.Blocks("0.0.0.0"));
        Assert.True(ssh.Blocks("127.0.0.1"));
        Assert.True(ssh.Blocks(""));
        // One on a single address clashes with that address and with every address, not with another one.
        var mail = new ProcNetSocket("tcp", "127.0.0.1", 25);
        Assert.True(mail.Blocks(null));
        Assert.True(mail.Blocks("127.0.0.1"));
        Assert.False(mail.Blocks("10.1.1.80"));

        // Docker's API listens on [::] only, and still holds 2375 for IPv4.
        var scan = Scan();
        Assert.DoesNotContain(scan.Sockets, s => s.Port == 2375 && s.Address == "0.0.0.0");
        Assert.False(HostPortMap.Check(HostOnly(scan), scan.Random, 2375, "tcp", "0.0.0.0", null).Free);
    }

    [Theory]
    // Every address (empty, or not known) clashes with any other.
    [InlineData("", "127.0.0.1", true)]
    [InlineData(null, "10.1.1.80", true)]
    [InlineData("", "::", true)]
    // Every address of one family clashes with that family's addresses only: Docker publishes every address as both.
    [InlineData("0.0.0.0", "127.0.0.1", true)]
    [InlineData("0.0.0.0", "10.1.1.80", true)]
    [InlineData("0.0.0.0", "::", false)]
    [InlineData("0.0.0.0", "::1", false)]
    [InlineData("::", "::1", true)]
    [InlineData("[::]", "fd00::5", true)]
    [InlineData("::", "127.0.0.1", false)]
    // Two single addresses clash only when they're the same one.
    [InlineData("127.0.0.1", "::1", false)]
    [InlineData("10.1.1.80", "127.0.0.1", false)]
    [InlineData("10.1.1.80", "10.1.1.80", true)]
    [InlineData("::ffff:10.1.1.80", "10.1.1.80", true)]
    public void Docker_bindings_clash_by_dockers_own_rule(string? a, string? b, bool clash)
    {
        Assert.Equal(clash, ProcNet.Overlap(a, b));
        Assert.Equal(clash, ProcNet.Overlap(b, a));
        // The recreate engine reads its bindings by the same rule.
        Assert.Equal(clash, ContainerPorts.Overlap(a ?? "", b ?? ""));
        // So does a socket on the host, unless it's on every IPv6 address.
        if (!ProcNet.IsIpv6Any(a)) Assert.Equal(clash, ProcNet.SocketBlocks(a, b));
    }

    [Theory]
    // /proc doesn't say whether a [::] socket is IPv6-only; a dual-stack one holds the IPv4 port too.
    [InlineData("::", "0.0.0.0")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("::", "10.1.1.80")]
    [InlineData("[::]", "")]
    [InlineData("::", "::1")]
    public void A_host_socket_on_every_ipv6_address_holds_the_port_for_every_binding(string socket, string binding)
    {
        Assert.True(ProcNet.SocketBlocks(socket, binding));
        Assert.True(new ProcNetSocket("tcp", socket, 22).Blocks(binding));
    }

    [Fact]
    public void A_host_socket_on_ipv4_only_leaves_ipv6_to_others()
    {
        // As between Docker's bindings: an IPv4 socket on every address doesn't hold the port for ::, nor ::1 for 127.0.0.1.
        Assert.False(new ProcNetSocket("tcp", "0.0.0.0", 8443).Blocks("::"));
        Assert.True(new ProcNetSocket("tcp", "0.0.0.0", 8443).Blocks("10.1.1.80"));
        Assert.False(new ProcNetSocket("tcp", "::1", 25).Blocks("127.0.0.1"));
        Assert.True(new ProcNetSocket("tcp", "::1", 25).Blocks(""));
    }

    [Fact]
    public void Udp_counts_for_udp_only()
    {
        Assert.Empty(ProcNet.ParseSockets(DockerFixtures.Text("proc-net-udp.txt"), "udp"));
        // cloudflared's QUIC sockets: UDP has no listening state, and a bound socket holds its port.
        var udp = ProcNet.ParseSockets(DockerFixtures.Text("proc-net-udp6.txt"), "udp");
        Assert.Equal([35387, 43616, 47333, 33585], udp.Select(s => s.Port));
        Assert.All(udp, s => Assert.Equal("udp", s.Protocol));

        var scan = Scan();
        var uses = HostOnly(scan);
        Assert.False(HostPortMap.Check(uses, scan.Random, 35387, "udp", null, null).Free);
        Assert.Equal("35387/tcp is free, but in the host's random range (32768-60999).", HostPortMap.Check(uses, scan.Random, 35387, "tcp", null, null).Sentence);
    }

    [Theory]
    [InlineData("32768\t60999\n", 32768, 60999)]
    [InlineData("1024 65535", 1024, 65535)]
    public void Reads_the_random_range(string text, int from, int to) => Assert.Equal((from, to), ProcNet.ParsePortRange(text));

    [Theory]
    [InlineData("")]
    [InlineData("cat: /proc/sys/net/ipv4/ip_local_port_range: No such file or directory")]
    [InlineData("60999 32768")]
    public void Reads_no_random_range_from_anything_else(string text)
    {
        Assert.Null(ProcNet.ParsePortRange(text));
        // Linux's own default stands in.
        Assert.Equal((32768, 60999), HostScan.Failed("x").Random);
    }

    [Fact]
    public void Reads_the_hosts_address_interface_and_gateway()
    {
        var scan = Scan();
        Assert.Null(scan.Error);
        Assert.Equal(("eth0", "10.1.1.80", "10.1.1.1", false), (scan.Interface, scan.Address, scan.Gateway, scan.GlobalIpv6));
        Assert.Equal((32768, 60999), scan.Random);

        // Docker's bridges are the host's addresses too, but the default route doesn't leave by them.
        Assert.Equal(["10.1.1.80", "127.0.0.1", "172.17.0.1", "172.18.0.1", "172.19.0.1", "172.21.0.1"],
            ProcNet.ParseLocalAddresses(DockerFixtures.Text("proc-net-fib_trie.txt")));
        var routes = ProcNet.ParseRoutes(DockerFixtures.Text("proc-net-route.txt"));
        Assert.Equal(new ProcNetRoute("eth0", "0.0.0.0", "10.1.1.1", "0.0.0.0", 3, 0), routes[0]);
        Assert.True(routes[1].Contains("10.1.1.80"));
        Assert.False(routes[1].Contains("172.21.0.1"));
    }

    [Fact]
    public void Keeps_every_address_of_the_host()
    {
        // Settings asks whether the LAN address saved there is still one of them.
        Assert.Equal(["10.1.1.80", "127.0.0.1", "172.17.0.1", "172.18.0.1", "172.19.0.1", "172.21.0.1"], Scan().LocalAddresses);
        Assert.Null(HostScan.Failed("x").LocalAddresses);
    }

    [Fact]
    public void Spots_a_global_ipv6_address_on_the_default_interface()
    {
        const string global = "20010db8000000000000000000000050 02 40 00 80     eth0";
        Assert.True(ProcNet.ParseIfInet6(global).Single().Global);
        Assert.Equal("2001:db8::50", ProcNet.ParseIfInet6(global).Single().Address);
        // Link-local, and unique local addresses (which Linux also calls global), aren't.
        Assert.False(ProcNet.ParseIfInet6("fe80000000000000be2411fffe93e276 02 40 20 80     eth0").Single().Global);
        Assert.False(ProcNet.ParseIfInet6("fd000000000000000000000000000050 02 40 00 80     eth0").Single().Global);
        Assert.False(ProcNet.ParseIfInet6("00000000000000000000000000000001 01 80 10 80       lo").Single().Global);

        var files = DockerFixtures.ProcFiles();
        files["/proc/net/if_inet6"] += "\n" + global + "\n";
        Assert.True(HostScanHelper.Read(files, DateTime.UtcNow).GlobalIpv6);
        // On another interface it isn't the host's way out.
        files["/proc/net/if_inet6"] = DockerFixtures.Text("proc-net-if_inet6.txt") + "\n" + global.Replace("eth0", "eth1", StringComparison.Ordinal);
        Assert.False(HostScanHelper.Read(files, DateTime.UtcNow).GlobalIpv6);
    }

    [Fact]
    public void A_host_gitberth_couldnt_read_gets_the_lans_ports_on_ipv4_only()
    {
        // Gitberth can't tell whether the host has an IPv6 address the internet reaches, so it publishes as if it had.
        Assert.True(HostScan.Failed("the helper container didn't finish within 20 seconds").LanIpv4Only);
        var files = DockerFixtures.ProcFiles();
        Assert.False(HostScanHelper.Read(files, DateTime.UtcNow).LanIpv4Only);
        files["/proc/net/if_inet6"] += "\n20010db8000000000000000000000050 02 40 00 80     eth0\n";
        Assert.True(HostScanHelper.Read(files, DateTime.UtcNow).LanIpv4Only);
    }

    [Fact]
    public void Reads_gitberths_own_container_id_from_its_mounts()
    {
        // Docker mounts a container's hostname, hosts and resolv.conf from its own folder, whatever hostname it was given:
        // these lines are CT 101's, from a container started with --hostname.
        const string id = "455b8ea31b6d686dbf8c70b3b35d65f4f0633c59d43a45d446b77b7b041976ce";
        var mountinfo = string.Join('\n',
            "987 868 0:84 / / ro,relatime master:399 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/ABC:/var/lib/docker/overlay2/l/DEF",
            "993 987 252:11 /var/lib/docker/volumes/gitberth_gitberth-data/_data /data rw,relatime master:1 - ext4 /dev/mapper/pve-vm--101--disk--0 rw,stripe=16",
            $"996 987 252:11 /var/lib/docker/containers/{id}/hostname /etc/hostname ro,relatime - ext4 /dev/mapper/pve-vm--101--disk--0 rw,stripe=16",
            $"997 987 252:11 /var/lib/docker/containers/{id}/hosts /etc/hosts ro,relatime - ext4 /dev/mapper/pve-vm--101--disk--0 rw,stripe=16",
            $"998 987 252:11 /var/lib/docker/containers/{id}/resolv.conf /etc/resolv.conf ro,relatime - ext4 /dev/mapper/pve-vm--101--disk--0 rw,stripe=16");

        Assert.Equal(id, HostScanner.ContainerIdIn(mountinfo));
        // Outside a container there's no such mount.
        Assert.Null(HostScanner.ContainerIdIn("22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n"));
        Assert.Null(HostScanner.ContainerIdIn(null));
        // Outside a container Gitberth has no container of its own; in one it's that ID, else its hostname.
        Assert.Null(new GitberthOptions { DataDir = Path.GetTempPath() }.OwnContainer);
        Assert.Equal(id, new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = true, OwnContainer = id }.OwnContainer);
        Assert.NotNull(new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = true }.OwnContainer);
    }

    [Fact]
    public void The_helper_runs_gitberths_own_image_read_only_on_the_hosts_network()
    {
        var args = HostScanHelper.RunArgs("sha256:0c1a38670237", "gitberth-helper-ports-1a2b3c4d5e6f");
        Assert.Equal(["run", "--rm", "--network", "host"], args.Take(4));
        var line = string.Join(' ', args);
        foreach (var option in new[] { "--read-only", "--cap-drop ALL", "--memory 32m", "--label gitberth.helper=ports", "--name gitberth-helper-ports-1a2b3c4d5e6f", "--log-driver none", "--pull never" })
            Assert.Contains(option, line);
        // It prints the host's files and nothing else.
        Assert.Equal(HostScanHelper.Files, args.Skip(args.IndexOf("sha256:0c1a38670237") + 4));
        Assert.True(HostScanHelper.IsHelper(new Dictionary<string, string> { [HostScanHelper.Label] = "ports" }));
        Assert.Matches("^gitberth-helper-ports-[0-9a-f]{12}$", HostScanHelper.NewName());
    }

    [Fact]
    public async Task The_helpers_script_prints_only_listening_tcp_sockets_and_every_other_file_whole()
    {
        // The script is sh; the test run on the server, in Linux, covers it.
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory("gitberth-proc-");
        try
        {
            // The host's files, with 20,000 connections added to its IPv4 TCP table, as on a busy host.
            var connections = string.Concat(Enumerable.Range(0, 20_000).Select(i =>
                $"{i + 100,4}: 5001010A:{(i % 30000) + 32768:X4} A803010A:0016 01 00000000:00000000 02:000AFC49 00000000     0        0 243390634 2 000000001d9033d3 20 4 33 10 -1\n"));
            var fixtures = DockerFixtures.ProcFiles();
            fixtures["/proc/net/tcp"] += connections;
            var paths = new List<string>();
            foreach (var (path, text) in fixtures)
            {
                var file = Path.Combine(dir.FullName, path.TrimStart('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, text);
                paths.Add(file);
            }
            var args = HostScanHelper.RunArgs("img", "name");
            var script = args[args.IndexOf("-c") + 1];

            var r = await ProcessRunner.RunAsync("sh", ["-c", script, "sh", .. paths], new ProcessOptions { MaxCapture = HostScanHelper.MaxOutput });

            Assert.True(r.Ok, r.ErrorText);
            Assert.True(r.StdOut.Length < 64 * 1024, $"The helper printed {r.StdOut.Length} characters.");
            var files = HostScanHelper.SplitFiles(r.StdOut).ToDictionary(f => "/" + Path.GetRelativePath(dir.FullName, f.Key).Replace('\\', '/'), f => f.Value);
            var expected = HostScanHelper.Read(DockerFixtures.ProcFiles(), DateTime.UtcNow);
            var printed = HostScanHelper.Read(files, DateTime.UtcNow);
            Assert.Equal(expected.Sockets, printed.Sockets);
            Assert.Equal((expected.Interface, expected.Address, expected.Gateway, expected.Random), (printed.Interface, printed.Address, printed.Gateway, printed.Random));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Output_that_lost_its_beginning_is_a_failed_scan_not_a_partial_one()
    {
        var output = string.Concat(DockerFixtures.ProcFiles().Select(f => $"==> {f.Key} <==\n{f.Value}\n"));
        Assert.Null(HostScanHelper.ReadOutput(output, DateTime.UtcNow).Error);

        // Gitberth keeps only the end of a long output: the IPv4 listeners at the start of /proc/net/tcp would be gone.
        var cut = HostScanHelper.ReadOutput(output[output.IndexOf("==> /proc/net/tcp6", StringComparison.Ordinal)..], DateTime.UtcNow);
        Assert.Equal("the helper's output was cut short", cut.Error);
        Assert.Empty(cut.Sockets);
    }

    [Fact]
    public void Splits_the_helpers_output_back_into_files()
    {
        var files = HostScanHelper.SplitFiles(
            "==> /proc/net/tcp <==\n  sl  local_address rem_address   st\n   0: 0100007F:0019 00000000:0000 0A\n==> /proc/net/tcp6 <==\n==> /proc/sys/net/ipv4/ip_local_port_range <==\n32768\t60999\n");

        Assert.Equal(["/proc/net/tcp", "/proc/net/tcp6", "/proc/sys/net/ipv4/ip_local_port_range"], files.Keys);
        Assert.Equal([new ProcNetSocket("tcp", "127.0.0.1", 25)], ProcNet.ParseSockets(files["/proc/net/tcp"], "tcp"));
        // A file the host doesn't have (no IPv6) leaves only its name.
        Assert.Equal("", files["/proc/net/tcp6"]);
        Assert.Equal((32768, 60999), ProcNet.ParsePortRange(files["/proc/sys/net/ipv4/ip_local_port_range"]));
        Assert.NotNull(HostScanHelper.Read(new Dictionary<string, string>(), DateTime.UtcNow).Error);
    }

    [Fact]
    public async Task Outside_a_container_the_scan_reads_proc_itself_and_on_windows_nothing()
    {
        var files = DockerFixtures.ProcFiles();
        var linux = new HostScanner(new DockerCli(Options), Options, NullLogger<HostScanner>.Instance)
        {
            InContainer = false,
            OnLinux = true,
            ReadFile = path => files.GetValueOrDefault(path),
        };
        Assert.Equal("10.1.1.80", (await linux.GetAsync()).Address);

        var windows = new HostScanner(new DockerCli(Options), Options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false };
        var skipped = await windows.GetAsync();
        Assert.NotNull(skipped.Error);
        Assert.Empty(skipped.Sockets);
    }

    /// <summary>
    /// The docker CLI as the helper sees it: Gitberth's own image, <paramref name="leftovers"/> from `docker ps`, and the
    /// host's files from `docker run`, unless <paramref name="run"/> answers instead. Every call is kept.
    /// </summary>
    private static DockerCli Cli(List<IReadOnlyList<string>> calls, Func<CancellationToken, Task<ProcessResult>>? run = null, string leftovers = "") => new(Options)
    {
        Runner = (args, _, ct) =>
        {
            lock (calls) calls.Add(args);
            return args[0] switch
            {
                "container" => Task.FromResult(new ProcessResult(0, "sha256:0c1a38670237\n", "")),
                "ps" => Task.FromResult(new ProcessResult(0, leftovers, "")),
                "rm" => Task.FromResult(new ProcessResult(0, "", "")),
                _ => run?.Invoke(ct) ?? Task.FromResult(new ProcessResult(0, HelperOutput(), "")),
            };
        },
    };

    /// <summary>What the helper prints on CT 101: each of the host's files after a line naming it.</summary>
    private static string HelperOutput() => string.Concat(DockerFixtures.ProcFiles().Select(f => $"==> {f.Key} <==\n{f.Value}\n"));

    private static string NameIn(IReadOnlyList<string> runArgs) => runArgs[runArgs.ToList().IndexOf("--name") + 1];

    [Fact]
    public async Task In_a_container_the_scan_runs_the_helper_from_gitberths_image_and_keeps_it_for_30_seconds()
    {
        var calls = new List<IReadOnlyList<string>>();
        var scanner = new HostScanner(Cli(calls), Options, NullLogger<HostScanner>.Instance) { InContainer = true };

        var scan = await scanner.GetAsync();
        Assert.Equal(("eth0", "10.1.1.80"), (scan.Interface, scan.Address));
        Assert.Equal(["container", "inspect", "--format", "{{.Image}}", Environment.MachineName], calls[0]);
        // The first scan looks for helpers an earlier Gitberth left behind.
        Assert.Equal(HostScanHelper.LeftoverArgs, calls[1]);
        Assert.Equal(HostScanHelper.RunArgs("sha256:0c1a38670237", NameIn(calls[2])), calls[2]);

        await Task.WhenAll(scanner.GetAsync(), scanner.GetAsync());
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task A_helper_that_fails_says_why()
    {
        var calls = new List<IReadOnlyList<string>>();
        var docker = Cli(calls, _ => Task.FromResult(
            new ProcessResult(125, "", "docker: Error response from daemon: No such image: sha256:0c1a38670237\nRun 'docker run --help' for more information")));
        var scan = await new HostScanner(docker, Options, NullLogger<HostScanner>.Instance) { InContainer = true }.GetAsync();

        Assert.Equal("docker: Error response from daemon: No such image: sha256:0c1a38670237", scan.Error);
        Assert.Empty(scan.Sockets);
    }

    [Fact]
    public async Task A_helper_that_doesnt_finish_is_removed_and_any_left_behind_is_removed_next_time()
    {
        var calls = new List<IReadOnlyList<string>>();
        var hang = true;
        var docker = Cli(calls, async ct =>
        {
            if (!hang) return new ProcessResult(0, HelperOutput(), "");
            // A slow engine: the CLI is stopped before Docker starts the helper, which `--rm` never removes then.
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        }, leftovers: "4e1b1c0ffee0\n");
        var scanner = new HostScanner(docker, Options, NullLogger<HostScanner>.Instance) { InContainer = true, HelperTimeout = TimeSpan.FromMilliseconds(200) };

        var timedOut = await scanner.GetAsync();
        Assert.Equal("the helper container didn't finish within 0 seconds", timedOut.Error);
        var run = calls.Single(c => c[0] == "run");
        Assert.Equal(["rm", "-f", "-v", NameIn(run)], calls[^1]);

        // The next scan looks for helpers left behind (one the engine created after the CLI gave up) and removes them first.
        hang = false;
        calls.Clear();
        scanner.Forget();
        var scan = await scanner.GetAsync();
        Assert.Null(scan.Error);
        Assert.Equal(HostScanHelper.LeftoverArgs, calls[0]);
        Assert.Equal(["rm", "-f", "-v", "4e1b1c0ffee0"], calls[1]);
        Assert.Equal("run", calls[2][0]);

        // Once nothing's left over it stops looking.
        calls.Clear();
        scanner.Forget();
        await scanner.GetAsync();
        Assert.Equal(["run"], calls.Select(c => c[0]));
    }

    [Fact]
    public async Task A_scan_from_before_a_container_started_or_stopped_is_read_again()
    {
        var calls = new List<IReadOnlyList<string>>();
        DateTime? ranAt = null;
        var scanner = new HostScanner(Cli(calls, async _ =>
        {
            ranAt ??= DateTime.UtcNow;
            await Task.Delay(5);
            return new ProcessResult(0, HelperOutput(), "");
        }), Options, NullLogger<HostScanner>.Instance) { InContainer = true };
        int Runs() => calls.Count(c => c[0] == "run");

        var first = await scanner.GetAsync();
        // Stamped before the helper ran: a container that stops while it runs makes the scan out of date.
        Assert.True(first.ReadAt <= ranAt, $"Read at {first.ReadAt:O}, after the helper started at {ranAt:O}.");

        await scanner.GetAsync(changedAt: first.ReadAt.AddSeconds(-1));
        Assert.Equal(1, Runs());
        var again = await scanner.GetAsync(changedAt: first.ReadAt.AddMilliseconds(1));
        Assert.Equal(2, Runs());
        Assert.True(again.ReadAt > first.ReadAt);

        // Gitberth's own changes drop it too, but the last one stays at hand.
        scanner.Forget();
        Assert.Same(again, scanner.Last);
        await scanner.GetAsync();
        Assert.Equal(3, Runs());
    }

    [Fact]
    public async Task The_lan_address_never_waits_for_a_new_scan_after_the_first()
    {
        var calls = new List<IReadOnlyList<string>>();
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = HelperOutput();
        var runs = 0;
        var docker = Cli(calls, _ => Interlocked.Increment(ref runs) == 1 ? Task.FromResult(new ProcessResult(0, output, "")) : release.Task);
        // Every scan is out of date at once.
        var scanner = new HostScanner(docker, Options, NullLogger<HostScanner>.Instance) { InContainer = true, KeptFor = TimeSpan.Zero };

        // The very first look waits for the helper.
        Assert.Equal("10.1.1.80", await scanner.DetectedAddressAsync());

        // After that, the last address comes back at once while the helper runs again in the background.
        var address = scanner.DetectedAddressAsync();
        Assert.True(address.IsCompleted, "The LAN address waited for the helper.");
        Assert.Equal("10.1.1.80", await address);
        Assert.Equal("10.1.1.80", (await scanner.LatestAsync()).Address);
        release.SetResult(new ProcessResult(0, output.Replace("5001010A", "5501010A", StringComparison.Ordinal), ""));
    }
}
