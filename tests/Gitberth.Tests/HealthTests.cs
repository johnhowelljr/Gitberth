using Gitberth.Components.Shared;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Services;

namespace Gitberth.Tests;

public class HealthTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>An engine with whoami, created from traefik/whoami:latest, which was pulled again since: the list names its image by ID.</summary>
    private sealed class MovedTagEngine : HttpMessageHandler
    {
        public const string ImageId = "sha256:7777777777777777777777777777777777777777777777777777777777777777";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var rest = path.StartsWith("/v1.", StringComparison.Ordinal) ? path[(path.IndexOf('/', 1) + 1)..] : path.TrimStart('/');
            var (status, body) = rest switch
            {
                "version" => (System.Net.HttpStatusCode.OK, DockerFixtures.Text("version.json")),
                "info" => (System.Net.HttpStatusCode.OK, """{"NCPU":2,"MemTotal":4294967296,"OSType":"linux","Architecture":"x86_64"}"""),
                "containers/json" => (System.Net.HttpStatusCode.OK,
                    $$$"""[{"Id":"{{{new string('9', 64)}}}","Names":["/whoami"],"Image":"{{{ImageId}}}","State":"running","Status":"Up 2 hours","Labels":{}}]"""),
                _ when rest == $"containers/{new string('9', 64)}/json" => (System.Net.HttpStatusCode.OK, """{"Config":{"Image":"traefik/whoami:latest"}}"""),
                _ => (System.Net.HttpStatusCode.NotFound, """{"message":"not here"}"""),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task A_container_whose_tag_moved_on_shows_the_reference_it_was_created_with()
    {
        var options = new GitberthOptions { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-health-" + Guid.NewGuid().ToString("N")[..8]) };
        Directory.CreateDirectory(options.DataDir);
        using var db = new ImageKit.Db();
        try
        {
            var api = new DockerApi("unix:///var/run/docker.sock", new MovedTagEngine());
            var store = new StatsStore(options);
            var events = new ContainerEventWatcher(api, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerEventWatcher>.Instance);
            var stats = new ContainerStatsCollector(api, new HealthSettings(new SettingsService(db, ImageKit.Protector())), store, events,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerStatsCollector>.Instance);

            await stats.SampleAsync(default);

            Assert.Null(stats.Current.Error);
            Assert.Equal("traefik/whoami:latest", Assert.Single(stats.Current.Containers).Image);
        }
        finally
        {
            try { Directory.Delete(options.DataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Parses_running_containers()
    {
        const string json = """
            [
              {"Id":"abc123","Names":["/gb-orb"],"Image":"gitberth/orb:1d2948c","State":"running","Status":"Up 3 hours (healthy)",
               "Labels":{"gitberth.managed":"true","gitberth.app":"orb"},"HostConfig":{"NetworkMode":"gitberth"}},
              {"Id":"def456","Names":["/web/db","/db"],"Image":"postgres:17","State":"running","Status":"Up 2 days",
               "Labels":{},"HostConfig":{"NetworkMode":"host"}}
            ]
            """;
        var list = DockerApi.ParseContainers(json);

        Assert.Equal(2, list.Count);
        Assert.Equal("gb-orb", list[0].Name);
        Assert.Equal("orb", list[0].Labels["gitberth.app"]);
        Assert.Equal("gitberth", list[0].NetworkMode);
        Assert.Equal("db", list[1].Name);
        Assert.Equal("host", list[1].NetworkMode);
    }

    [Fact]
    public void Reads_stats_the_way_docker_stats_does()
    {
        // cgroup v2: memory leaves out reclaimable page cache, and traffic is summed over every network.
        const string json = """
            {"read":"2026-11-02T17:00:00.123456789Z",
             "cpu_stats":{"cpu_usage":{"total_usage":5000000000},"online_cpus":6},
             "memory_stats":{"usage":104857600,"limit":17179869184,"stats":{"inactive_file":10485760,"anon":50000000}},
             "networks":{"eth0":{"rx_bytes":1000,"tx_bytes":2000},"eth1":{"rx_bytes":500,"tx_bytes":0}}}
            """;
        var r = DockerApi.ParseStats(json);

        Assert.Equal(5_000_000_000UL, r.CpuNanos);
        Assert.Equal(94_371_840UL, r.MemoryBytes);
        Assert.Equal(17_179_869_184UL, r.MemoryLimit);
        Assert.Equal(new NetCounters(1500, 2000), r.Network);
        Assert.Equal(DateTimeKind.Utc, r.ReadAt!.Value.Kind);
        Assert.Equal(T0.AddTicks(1_234_567), r.ReadAt.Value, TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData("2026-11-02T12:00:00.5-05:00", true)] // the engine's local time zone
    [InlineData("0001-01-01T00:00:00Z", false)]       // Docker's "no time"
    [InlineData("not a time", false)]
    public void Reads_when_the_engine_took_the_reading(string read, bool known)
    {
        var r = DockerApi.ParseStats($$$"""{"cpu_stats":{"cpu_usage":{"total_usage":1}},"read":"{{{read}}}"}""");
        Assert.Equal(known ? T0.AddMilliseconds(500) : null, r.ReadAt);
    }

    [Fact]
    public void Host_network_containers_have_no_network_counters()
    {
        var r = DockerApi.ParseStats("""{"cpu_stats":{"cpu_usage":{"total_usage":1}},"memory_stats":{"usage":10,"stats":{"total_inactive_file":4}}}""");
        Assert.Null(r.Network);
        Assert.Null(r.ReadAt);
        Assert.Equal(6UL, r.MemoryBytes);
    }

    [Fact]
    public void Recognises_dockers_placeholder_for_a_failed_read()
    {
        var placeholder = DockerApi.ParseStats("""{"read":"0001-01-01T00:00:00Z","preread":"0001-01-01T00:00:00Z","name":"/web","id":"abc","memory_stats":{}}""");
        Assert.True(ContainerStatsCollector.IsPlaceholder(placeholder));

        var real = DockerApi.ParseStats("""{"read":"2026-11-02T17:00:00Z","cpu_stats":{"cpu_usage":{"total_usage":12345}}}""");
        Assert.False(ContainerStatsCollector.IsPlaceholder(real));
    }

    [Fact]
    public void Reads_host_info() =>
        Assert.Equal(new DockerHostInfo("docker", 6, 17_179_869_184, "29.9.2"),
            DockerApi.ParseHostInfo("""{"Name":"docker","NCPU":6,"MemTotal":17179869184,"ServerVersion":"29.9.2"}"""));

    [Fact]
    public void Rates_are_averages_over_the_interval()
    {
        var a = Sample("c1", 0, cpu: 10_000_000_000, rx: 1_000, tx: 5_000);
        var b = Sample("c1", 5, cpu: 12_500_000_000, rx: 6_000, tx: 5_000);
        var p = ContainerStatsCollector.Rate(a, b)!.Value;

        Assert.Equal(T0.AddSeconds(5), p.Time);
        Assert.Equal(50, p.Cpu, 6); // 2.5 s of CPU time in 5 s is half a core
        Assert.Equal(1000, p.RxRate!.Value, 6);
        Assert.Equal(0, p.TxRate!.Value, 6);
    }

    [Fact]
    public void A_burst_between_readings_is_counted_in_full()
    {
        // The counters are running totals: a 1-second burst at two full cores lands in the next point as 40% over 5 s.
        var a = Sample("c1", 0, cpu: 0, rx: 0, tx: 0);
        var b = Sample("c1", 5, cpu: 2_000_000_000, rx: 0, tx: 0);
        Assert.Equal(40, ContainerStatsCollector.Rate(a, b)!.Value.Cpu, 6);
    }

    [Fact]
    public void The_engine_timestamps_time_the_interval_when_present()
    {
        // The second answer arrived 0.4 s late, but the engine took both readings exactly 5 s apart.
        var a = new ContainerStatsCollector.RawSample("c1", T0, new StatsReading(0, 0, 0, null, T0.AddSeconds(-0.1)));
        var b = new ContainerStatsCollector.RawSample("c1", T0.AddSeconds(5.4), new StatsReading(5_000_000_000, 0, 0, null, T0.AddSeconds(4.9)));
        var p = ContainerStatsCollector.Rate(a, b)!.Value;
        Assert.Equal(100, p.Cpu, 6);
        Assert.Equal(T0.AddSeconds(5.4), p.Time); // plotted on Gitberth's own clock

        // Without them, the arrival times are all there is.
        var c = ContainerStatsCollector.Rate(a with { Reading = a.Reading with { ReadAt = null } }, b)!.Value;
        Assert.Equal(5_000_000_000 / 5.4e9 * 100, c.Cpu, 6);
    }

    [Fact]
    public void Restarts_replacements_and_long_gaps_are_not_averaged()
    {
        var a = Sample("c1", 0, cpu: 10_000_000_000, rx: 1_000, tx: 1_000);
        Assert.Null(ContainerStatsCollector.Rate(a, Sample("c1", 5, cpu: 2_000_000, rx: 10, tx: 10)));      // restarted
        Assert.Null(ContainerStatsCollector.Rate(a, Sample("c2", 5, cpu: 11_000_000_000, rx: 10, tx: 10))); // redeployed
        Assert.Null(ContainerStatsCollector.Rate(a, Sample("c1", 20, cpu: 11_000_000_000, rx: 2_000, tx: 2_000)));
        Assert.NotNull(ContainerStatsCollector.Rate(a, Sample("c1", 15, cpu: 11_000_000_000, rx: 2_000, tx: 2_000))); // two missed readings

        // Only the network counters went backwards: keep the CPU, drop the traffic.
        var p = ContainerStatsCollector.Rate(a, Sample("c1", 5, cpu: 13_000_000_000, rx: 10, tx: 10))!.Value;
        Assert.Equal(60, p.Cpu, 6);
        Assert.Null(p.RxRate);
        Assert.Null(p.TxRate);
    }

    [Fact]
    public void Containers_without_a_network_get_cpu_but_no_traffic()
    {
        var a = new ContainerStatsCollector.RawSample("c1", T0, new StatsReading(0, 0, 0, null));
        var b = new ContainerStatsCollector.RawSample("c1", T0.AddSeconds(5), new StatsReading(500_000_000, 0, 0, null));
        var p = ContainerStatsCollector.Rate(a, b)!.Value;
        Assert.Equal(10, p.Cpu, 6);
        Assert.Null(p.RxRate);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("""["gitberth","cloudflare-tunnel",""]""", new[] { "cloudflare-tunnel", "gitberth" })]
    [InlineData("not json", new string[0])]
    public void Reads_the_containers_that_are_switched_off(string? json, string[] expected) =>
        Assert.Equal(expected, HealthSettings.Parse(json).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(0.3, 5, 5)]
    [InlineData(5, 5, 5)]
    [InlineData(5.2, 5, 10)]
    [InlineData(12, 5, 20)]
    [InlineData(260, 5, 500)]
    [InlineData(1000, 5, 1000)]
    [InlineData(1234, 10000, 10000)]
    [InlineData(23456, 10000, 50000)]
    public void The_axis_tops_out_at_a_round_number(double max, double floor, double expected) =>
        Assert.Equal(expected, ChartMath.NiceMax(max, floor), 6);

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.4234, "0.42%")]
    [InlineData(3.14, "3.1%")]
    [InlineData(45.6, "46%")]
    [InlineData(250, "250%")]
    public void Formats_cpu(double value, string expected) => Assert.Equal(expected, Format.Cpu(value));

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(999.7, "1 kB/s")]
    [InlineData(1234, "1.2 kB/s")]
    [InlineData(12345, "12 kB/s")]
    [InlineData(1500000, "1.5 MB/s")]
    public void Formats_rates(double value, string expected) => Assert.Equal(expected, Format.Rate(value));

    [Theory]
    [InlineData(47400000, "45 MiB")]
    [InlineData(1288490189, "1.2 GiB")]
    [InlineData(17179869184, "16 GiB")]
    public void Formats_memory(double value, string expected) => Assert.Equal(expected, Format.Memory(value));

    private static ContainerStatsCollector.RawSample Sample(string id, int seconds, ulong cpu, ulong rx, ulong tx) =>
        new(id, T0.AddSeconds(seconds), new StatsReading(cpu, 0, 0, new NetCounters(rx, tx)));
}
