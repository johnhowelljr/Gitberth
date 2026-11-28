using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Keeper;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Health alerts, the longer history and unhealthy restarts (src/Gitberth/Health/HealthRules.cs, ResourceAlerts.cs,
/// StatsStore.cs's roll-ups, ChartRange.cs; Keeper/UnhealthyRestarts.cs).
/// </summary>
public class HealthAlertsTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    // ---- the rules ----

    [Fact]
    public async Task An_apps_alerts_are_the_defaults_its_own_or_none_and_kept_by_container_name()
    {
        using var rig = new AutoRig();
        var rules = new HealthRules(rig.Settings, rig.Audit);
        await rules.LoadAsync();
        Assert.Equal(new AlertThresholds(true, 90, 10, 90, 5), rules.For("web"));
        Assert.True(rules.RestartsUnhealthy("web"));

        await rules.SetAppAsync("web", new AppHealthRules { Alerts = AppAlertMode.Own, CpuPercent = 50, CpuMinutes = 2, MemoryPercent = 70, MemoryMinutes = 3 });
        await rules.SetAppAsync("db", new AppHealthRules { Alerts = AppAlertMode.Off, RestartUnhealthy = false });
        await rules.SaveAlertsAsync(new AlertRules { CpuPercent = 80, CpuMinutes = 15, MemoryPercent = 85, MemoryMinutes = 4 });

        // Read again, as after a restart.
        var again = new HealthRules(rig.Settings);
        await again.LoadAsync();
        Assert.Equal(new AlertThresholds(true, 50, 2, 70, 3), again.For("web"));
        Assert.Equal(new AlertThresholds(true, 80, 15, 85, 4), again.For("cache"));
        Assert.False(again.For("db").On);
        Assert.False(again.RestartsUnhealthy("db"));
        // Off for every app, whatever an app says.
        await again.SaveAlertsAsync(again.Current.Alerts with { On = false });
        Assert.False(again.For("web").On);
        await again.SetRestartUnhealthyAsync(false);
        Assert.False(again.RestartsUnhealthy("web"));

        // Back to the defaults: nothing is kept for it.
        await again.SetAppAsync("web", new AppHealthRules());
        Assert.False(again.Current.Apps.ContainsKey("web"));
        Assert.Contains(rig.Audit.Entries, e => e.Action == "app.health.changed" && e.Container == "web");
        Assert.Contains(rig.Audit.Entries, e => e.Action == "settings.health.alerts.changed");
    }

    [Theory]
    [InlineData(9, 10, 90, 5, "A percentage goes from 10 to 100.")]
    [InlineData(90, 10, 101, 5, "A percentage goes from 10 to 100.")]
    [InlineData(90, 0, 90, 5, "Minutes go from 1 to 60.")]
    [InlineData(90, 10, 90, 61, "Minutes go from 1 to 60.")]
    [InlineData(90, 10, 90, 5, null)]
    public void Numbers_out_of_range_are_refused(int cpu, int cpuMinutes, int memory, int memoryMinutes, string? problem) =>
        Assert.Equal(problem, HealthRules.Problem(cpu, cpuMinutes, memory, memoryMinutes));

    [Fact]
    public void Settings_that_cant_be_read_are_the_defaults() =>
        Assert.Equal(new AlertRules(), HealthRules.Parse("{not json").Alerts);

    // ---- the alerts ----

    [Fact]
    public void An_alert_needs_its_whole_window_starts_over_the_line_and_ends_once_every_reading_is_back_under()
    {
        var window = TimeSpan.FromMinutes(1);
        List<StatsPoint> Points(int from, params double[] cpu) =>
            [.. cpu.Select((c, i) => new StatsPoint(T0.AddSeconds(from + i * 5), c, null, null))];

        // 40 s of readings for a minute's window: not yet.
        Assert.Null(ResourceAlerts.Shares(Points(20, 95, 95, 95, 95, 95, 95, 95, 95), p => p.Cpu, 100, T0.AddSeconds(60), window));
        // A whole minute at 95% of one core: above 90%.
        var hot = Points(0, Enumerable.Repeat(95.0, 13).ToArray());
        var shares = ResourceAlerts.Shares(hot, p => p.Cpu, 100, T0.AddSeconds(60), window);
        Assert.Equal(AlertStep.Starts, ResourceAlerts.Step(false, shares, 90));
        // Two cores' worth (a 2-CPU limit): under the line.
        Assert.Equal(AlertStep.None, ResourceAlerts.Step(false, ResourceAlerts.Shares(hot, p => p.Cpu, 200, T0.AddSeconds(60), window), 90));
        // Mostly back under, one reading still over: it lasts.
        var mostly = Points(0, [.. Enumerable.Repeat(20.0, 12), 95]);
        Assert.Equal(AlertStep.None, ResourceAlerts.Step(true, ResourceAlerts.Shares(mostly, p => p.Cpu, 100, T0.AddSeconds(60), window), 90));
        Assert.Equal(AlertStep.Ends, ResourceAlerts.Step(true, ResourceAlerts.Shares(Points(0, Enumerable.Repeat(20.0, 13).ToArray()), p => p.Cpu, 100,
            T0.AddSeconds(60), window), 90));
        // Nothing to measure against.
        Assert.Null(ResourceAlerts.Shares(hot, p => p.Memory, 0, T0.AddSeconds(60), window));
    }

    [Fact]
    public void A_containers_cpu_limit_is_its_cpus_or_its_quota()
    {
        Assert.Equal(0.5, ResourceAlerts.CoresOf(JsonNode.Parse("""{"NanoCpus":500000000}""")));
        Assert.Equal(1.5, ResourceAlerts.CoresOf(JsonNode.Parse("""{"NanoCpus":0,"CpuQuota":150000,"CpuPeriod":100000}""")));
        Assert.Null(ResourceAlerts.CoresOf(JsonNode.Parse("""{"NanoCpus":0,"CpuQuota":0}""")));
    }

    [Fact]
    public async Task A_high_app_is_told_kept_on_the_board_and_told_again_once_its_back_under()
    {
        using var rig = new AutoRig();
        await rig.TellAsync();
        var (alerts, board, rules) = Alerts(rig);
        await rules.SaveAlertsAsync(new AlertRules { CpuPercent = 90, CpuMinutes = 1, MemoryPercent = 90, MemoryMinutes = 1 });
        var host = new DockerHostInfo("host", 4, 8UL << 30, "29.0.0");
        HealthSnapshot Snapshot(DateTime now, double cpu) => new(now, null, host,
        [
            new ContainerHealth("web", "nginx:1", "running", "Up", null, null, true, 100UL << 20, 8UL << 30,
                [.. Enumerable.Range(0, 25).Select(i => new StatsPoint(now.AddSeconds(-120 + i * 5), cpu, null, null, 100 << 20))], [], [], []),
        ]);

        // 4 CPUs, no limit of its own: 380% is 95% of the host.
        await alerts.LookAsync(Snapshot(T0, 380), default);
        var told = Assert.Single(rig.Notices());
        Assert.Equal((NotifyEvent.HighCpu, "web's CPU is high"), (told.Event, told.Title));
        Assert.StartsWith("It used 95% of the host's 4 CPUs on average over a minute", told.Text);
        var item = Assert.Single(board.Items);
        Assert.Equal(("resources:web", ResourceAlerts.Source), (item.Key, item.Source));
        Assert.Equal(["web"], alerts.Alerting.Keys);

        // Still high: nothing more. A restart of Gitberth remembers it.
        await alerts.LookAsync(Snapshot(T0.AddSeconds(5), 380), default);
        Assert.Empty(rig.Notices());
        var (again, _, _) = Alerts(rig);
        await again.StartAsync(default);
        await Task.Delay(200);
        await again.LookAsync(Snapshot(T0.AddSeconds(10), 380), default);
        Assert.Empty(rig.Notices());
        await again.StopAsync(default);

        // Back under for the whole window.
        await alerts.LookAsync(Snapshot(T0.AddMinutes(5), 40), default);
        var ok = Assert.Single(rig.Notices());
        Assert.Equal((NotifyEvent.ResourcesOk, "web's CPU is back under its line"), (ok.Event, ok.Title));
        Assert.Empty(board.Items);
        Assert.Empty(alerts.Alerting);
        // Both in its Activity, by its container's name.
        using var db = rig.Host.Db.CreateDbContext();
        var said = db.Activity.Where(a => a.ContainerName == "web").OrderBy(a => a.Id).Select(a => a.Text).ToList();
        Assert.Equal(2, said.Count);
        Assert.StartsWith("It used 95%", said[0]);
        Assert.StartsWith("It stayed under 90%", said[1]);
    }

    // ---- the longer history ----

    [Fact]
    public async Task Readings_roll_up_into_5_minute_and_hourly_steps_and_each_kind_keeps_its_own_days()
    {
        var dir = Directory.CreateTempSubdirectory("gitberth-stats-");
        try
        {
            var store = new StatsStore(new GitberthOptions { DataDir = dir.FullName });
            var now = new DateTime(2026, 11, 28, 13, 2, 0, DateTimeKind.Utc);
            // 12:00:05 to 13:02:00, every 5 s: CPU 10 in the first 5 minutes, then 30; memory 100 MB with a 200 MB peak.
            var points = new List<(string, StatsPoint)>();
            for (var t = T0.AddSeconds(5); t <= now; t = t.AddSeconds(5))
                points.Add(("web", new StatsPoint(t, t <= T0.AddMinutes(5) ? 10 : 30, 1000, 2000, t == T0.AddMinutes(2) ? 200e6 : 100e6)));
            await store.AddPointsAsync(points);

            await store.RollUpAsync(now);
            await store.RollUpAsync(now);

            var five = (await store.LoadRolledAsync(false, T0))["web"];
            // 12:05 to 13:00: twelve finished steps; 13:00 to 13:05 is still filling up.
            Assert.Equal(12, five.Count);
            Assert.Equal((T0.AddMinutes(5), 10.0, 10.0, 200e6), (five[0].Time, five[0].Cpu, five[0].CpuMax, five[0].MemoryMax!.Value));
            Assert.Equal(100e6 + 100e6 / 60, five[0].Memory!.Value, 3);
            Assert.Equal((30.0, 1000.0, 2000.0), (five[1].Cpu, five[1].RxRate!.Value, five[1].TxRate!.Value));
            var hour = Assert.Single((await store.LoadRolledAsync(true, T0))["web"]);
            Assert.Equal((T0.AddHours(1), 200e6), (hour.Time, hour.MemoryMax!.Value));
            Assert.Equal((10.0 + 11 * 30) / 12, hour.Cpu, 6);

            // 15 days on, the 5-minute steps are gone and the hourly one stays.
            await store.RollUpAsync(now.AddDays(15));
            Assert.Empty(await store.LoadRolledAsync(false, T0));
            Assert.Single((await store.LoadRolledAsync(true, T0))["web"]);
            await store.RollUpAsync(now.AddDays(91));
            Assert.Empty(await store.LoadRolledAsync(true, T0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Each_range_reads_from_its_kind_and_draws_at_most_about_720_points()
    {
        Assert.Equal(["1 hour", "6 hours", "24 hours", "7 days", "30 days", "90 days"], ChartRanges.All.Select(ChartRanges.Label));
        Assert.All(ChartRanges.All, r => Assert.True(ChartRanges.Window(r) / ChartRanges.Step(r) <= 720, r.ToString()));
        Assert.Equal([false, false, true, true, true, true], ChartRanges.All.Select(ChartRanges.Stored));
        Assert.Equal(TimeSpan.FromHours(3), ChartRanges.Step(ChartRange.Quarter));
    }

    // ---- unhealthy restarts ----

    [Theory]
    [InlineData("Up 3 minutes (unhealthy)", "Unhealthy")]
    [InlineData("Up 3 minutes (healthy)", "Healthy")]
    [InlineData("Up 2 seconds (health: starting)", "Starting")]
    [InlineData("Up 3 minutes", "None")]
    public void Dockers_list_says_how_healthy_a_container_is(string status, string health) =>
        Assert.Equal(health, UnhealthyRestarts.HealthOf(status).ToString());

    [Fact]
    public void The_test_rigs_alert_minute_is_one_of_its_timings() =>
        Assert.Equal("alertMinute=1,keepEvery=3", TestTimings.Parse("keepEvery=3,alertMinute=1")!.ToString());

    // ---- helpers ----

    private static (ResourceAlerts Alerts, AttentionBoard Board, HealthRules Rules) Alerts(AutoRig rig)
    {
        // Never Docker itself: an address where nothing listens, so a container's own CPU limit reads as none.
        var options = new GitberthOptions { DataDir = rig.Host.Options.DataDir, DockerHost = "tcp://127.0.0.1:9" };
        var docker = new DockerApi(options);
        var store = new StatsStore(options);
        var stats = new ContainerStatsCollector(docker, new HealthSettings(rig.Settings), store,
            new ContainerEventWatcher(docker, store, NullLogger<ContainerEventWatcher>.Instance), NullLogger<ContainerStatsCollector>.Instance);
        var rules = new HealthRules(rig.Settings, rig.Audit);
        rules.LoadAsync().GetAwaiter().GetResult();
        return (new ResourceAlerts(stats, rules, docker, rig.Notifier, new ActivityLog(rig.Host.Db, store), rig.Board, new ContainerTitles(rig.Settings), rig.Settings,
            rig.Host.Db, NullLogger<ResourceAlerts>.Instance), rig.Board, rules);
    }
}
