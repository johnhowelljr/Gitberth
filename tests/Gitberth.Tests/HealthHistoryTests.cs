using Gitberth.Components.Shared;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Health;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>The Health page's 6 hours: readings kept on disk, averaged for the long view, and the marks for deploys, restarts and stops.</summary>
public class HealthHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = T0;
    private static readonly DateTime To = T0.AddHours(6);

    private static ContainerEvent E(double seconds, string id, string action, int? exit = null, int? signal = null, int? deployment = null, string? trigger = null) =>
        new(T0.AddSeconds(seconds), id, "gb-web", action, exit, signal, deployment, trigger);

    private static (List<ChartMark> Marks, List<DownSpan> Down) Marks(bool running, params ContainerEvent[] events) =>
        EventMarks.For(events, From, To, running);

    [Fact]
    public void A_docker_restart_is_one_mark()
    {
        var (marks, down) = Marks(true,
            E(10, "a", "kill", signal: 15), E(10.5, "a", "die", exit: 143), E(10.6, "a", "stop"), E(11, "a", "start"), E(11.1, "a", "restart"));

        var m = Assert.Single(marks);
        Assert.Equal((T0.AddSeconds(11), MarkKind.Restart, "Restarted", MarkTone.Neutral), (m.Time, m.Kind, m.Label, m.Tone));
        Assert.Empty(down);
    }

    [Fact]
    public void A_long_stop_is_shaded_between_its_stop_and_start()
    {
        var (marks, down) = Marks(true,
            E(10, "a", "kill", signal: 15), E(10.5, "a", "die", exit: 0), E(10.6, "a", "stop"), E(310, "a", "start"));

        Assert.Equal(["Stopped", "Started"], marks.Select(m => m.Label));
        Assert.Equal(T0.AddSeconds(10.5), marks[0].Time);
        Assert.Equal(new DownSpan(T0.AddSeconds(10.5), T0.AddSeconds(310)), Assert.Single(down));
    }

    [Fact]
    public void A_crash_the_restart_policy_recovered_from_is_marked_as_one()
    {
        var (marks, down) = Marks(true, E(20, "a", "die", exit: 1), E(21, "a", "start"));

        var m = Assert.Single(marks);
        Assert.Equal((MarkKind.Crash, "Crashed", "Crashed (exit code 1) and restarted itself", MarkTone.Danger), (m.Kind, m.Label, m.Detail, m.Tone));
        Assert.Empty(down);
    }

    [Fact]
    public void Running_out_of_memory_is_named()
    {
        var (marks, _) = Marks(true, E(30, "a", "oom"), E(30.01, "a", "die", exit: 137), E(31, "a", "start"));
        Assert.Equal(("Out of memory", "Ran out of memory and restarted", MarkTone.Danger), (marks[0].Label, marks[0].Detail, marks[0].Tone));
    }

    [Fact]
    public void A_deploys_swap_is_one_mark_with_its_number_and_trigger()
    {
        // The old container stops and the new one, which Gitberth labelled with the deploy, starts a moment later.
        var (marks, down) = Marks(true,
            E(40, "old", "kill", signal: 15), E(40.3, "old", "die", exit: 0), E(40.4, "old", "stop"),
            E(41, "new", "start", deployment: 12, trigger: "Poll"));

        var m = Assert.Single(marks);
        Assert.Equal((MarkKind.Deploy, "Deploy #12", "New commit: deploy #12"), (m.Kind, m.Label, m.Detail));
        Assert.Empty(down);
    }

    [Fact]
    public void A_scheduled_restart_is_a_restart_not_a_deploy()
    {
        var (marks, _) = Marks(true,
            E(40, "old", "kill", signal: 15), E(40.3, "old", "die", exit: 0), E(41, "new", "start", deployment: 13, trigger: "Restart"));
        Assert.Equal((MarkKind.Restart, "Scheduled restart"), (marks[0].Kind, marks[0].Label));
    }

    [Fact]
    public void A_container_that_is_still_down_is_shaded_to_now()
    {
        var (marks, down) = Marks(false, E(50, "a", "die", exit: 1));
        Assert.Equal("Crashed", Assert.Single(marks).Label);
        Assert.Equal(new DownSpan(T0.AddSeconds(50), To), Assert.Single(down));
    }

    [Fact]
    public void A_stop_whose_start_was_missed_is_marked_but_not_shaded()
    {
        // It runs now, so it started again while Gitberth wasn't watching: when isn't known.
        var (marks, down) = Marks(true, E(10, "a", "kill", signal: 15), E(10.5, "a", "die", exit: 0));
        Assert.Equal("Stopped", Assert.Single(marks).Label);
        Assert.Empty(down);
    }

    [Fact]
    public void A_long_pause_is_shaded()
    {
        var (marks, down) = Marks(true, E(60, "a", "pause"), E(200, "a", "unpause"));
        Assert.Equal(["Paused", "Resumed"], marks.Select(m => m.Label));
        Assert.Equal(new DownSpan(T0.AddSeconds(60), T0.AddSeconds(200)), Assert.Single(down));
    }

    [Fact]
    public void Health_changes_are_marked_when_they_change()
    {
        var (marks, _) = Marks(true,
            E(70, "a", "health_status: unhealthy"), E(75, "a", "health_status: unhealthy"),
            E(80, "a", "health_status: healthy"), E(85, "a", "health_status: healthy"));

        Assert.Equal([("Unhealthy", MarkTone.Warning), ("Healthy", MarkTone.Good)], marks.Select(m => (m.Label, m.Tone)));
    }

    [Fact]
    public void Putting_the_previous_container_back_is_a_rollback()
    {
        var (marks, _) = Marks(true,
            E(-100, "a", "start", deployment: 11, trigger: "Manual"),
            E(100, "a", "kill", signal: 15), E(100.5, "a", "die", exit: 0), E(101, "b", "start", deployment: 12, trigger: "Manual"),
            E(110, "b", "die", exit: 1), E(111, "a", "start", deployment: 11, trigger: "Manual"));

        Assert.Equal(["Deploy #12", "Rolled back"], marks.Select(m => m.Label));
    }

    [Fact]
    public void Events_before_the_window_set_the_scene_without_being_marked()
    {
        var (marks, down) = Marks(false, E(-3600, "a", "start"), E(-1800, "a", "kill", signal: 15), E(-1799, "a", "die", exit: 0));
        // Down since before the window: shaded from its start, and the stop itself is off the chart.
        Assert.Empty(marks);
        Assert.Equal(new DownSpan(From, To), Assert.Single(down));
    }

    [Fact]
    public void A_new_container_under_the_same_name_is_recreated_not_restarted()
    {
        // docker compose up replacing a container: only the old one's stop is on record before it.
        var (marks, _) = Marks(true, E(10, "old", "kill", signal: 15), E(10.5, "old", "die", exit: 0), E(10.6, "old", "stop"), E(11, "new", "start"));
        Assert.Equal(("Recreated", MarkKind.Recreate), (Assert.Single(marks).Label, marks[0].Kind));
    }

    [Fact]
    public void A_signal_that_doesnt_end_the_process_isnt_a_stop()
    {
        // SIGHUP asks a server to reload; the crash after it is its own.
        var (marks, _) = Marks(true, E(10, "a", "kill", signal: 1), E(12, "a", "die", exit: 2), E(13, "a", "start"));
        Assert.Equal("Crashed", marks[0].Label);
    }

    [Fact]
    public void The_long_view_averages_each_step_and_leaves_out_the_one_filling_up()
    {
        var points = Enumerable.Range(1, 13)
            .Select(i => new StatsPoint(T0.AddSeconds(i * 5), Cpu: i, RxRate: i * 10, TxRate: 0, Memory: i < 4 ? null : 100))
            .ToList();
        var averages = ContainerStatsCollector.Averages(points, TimeSpan.FromSeconds(30), now: T0.AddSeconds(66));

        // 5..25 s, then 30..55 s; the step from 60 s isn't over at 66 s.
        Assert.Equal(2, averages.Count);
        Assert.Equal(new StatsPoint(T0.AddSeconds(30), 3, 30, 0, 100), averages[0]);
        Assert.Equal(new StatsPoint(T0.AddSeconds(60), 8.5, 85, 0, 100), averages[1]);
    }

    [Fact]
    public void Averages_leave_gaps_where_a_step_had_no_readings()
    {
        var points = new List<StatsPoint> { new(T0.AddSeconds(5), 1, null, null), new(T0.AddSeconds(95), 3, null, null) };
        var averages = ContainerStatsCollector.Averages(points, TimeSpan.FromSeconds(30), now: T0.AddHours(1));
        Assert.Equal([T0.AddSeconds(30), T0.AddSeconds(120)], averages.Select(a => a.Time));
        Assert.Null(averages[0].RxRate);
        Assert.Null(averages[0].Memory);
    }

    [Fact]
    public void Memory_is_part_of_each_reading()
    {
        var a = new ContainerStatsCollector.RawSample("c1", T0, new StatsReading(0, 100, 0, null));
        var b = new ContainerStatsCollector.RawSample("c1", T0.AddSeconds(5), new StatsReading(500_000_000, 150_000_000, 0, null));
        Assert.Equal(150_000_000, ContainerStatsCollector.Rate(a, b)!.Value.Memory);
        // None means the engine couldn't say.
        Assert.Null(ContainerStatsCollector.Rate(a, b with { Reading = b.Reading with { MemoryBytes = 0 } })!.Value.Memory);
    }

    [Theory]
    [InlineData(165 * 1048576.0, 10 * 1048576.0, 200 * 1048576.0)]
    [InlineData(6.8 * 1048576.0, 10 * 1048576.0, 10 * 1048576.0)]
    [InlineData(900 * 1048576.0, 0, 1073741824.0)]
    [InlineData(1.2 * 1073741824.0, 0, 2 * 1073741824.0)]
    [InlineData(0, 0, 1024)]
    public void The_memory_axis_tops_out_at_a_round_binary_size(double max, double floor, double expected) =>
        Assert.Equal(expected, ChartMath.NiceMaxBytes(max, floor), 3);

    [Fact]
    public void Reads_an_event_from_dockers_stream()
    {
        const string json = """
            {"status":"die","id":"abc","from":"gitberth/web:1","Type":"container","Action":"die",
             "Actor":{"ID":"abc","Attributes":{"exitCode":"137","gitberth.app":"web","gitberth.deployment":"12","gitberth.trigger":"Poll","image":"gitberth/web:1","name":"gb-web"}},
             "scope":"local","time":1793638800,"timeNano":1793638800123456789}
            """;
        var e = DockerApi.ParseEvent(json.ReplaceLineEndings(""))!;

        Assert.Equal(new ContainerEvent(T0.AddTicks(1_234_567), "abc", "gb-web", "die", 137, null, 12, "Poll"), e);
    }

    [Theory]
    [InlineData("""{"Type":"container","Action":"exec_create: sh -c true","Actor":{"ID":"abc","Attributes":{"name":"gb-web"}},"timeNano":1762102800000000000}""", null)]
    [InlineData("""{"Type":"network","Action":"connect","Actor":{"ID":"n1","Attributes":{"name":"gitberth"}},"timeNano":1762102800000000000}""", null)]
    [InlineData("""{"Type":"container","Action":"health_status: unhealthy","Actor":{"ID":"abc","Attributes":{"name":"gb-web"}},"timeNano":1762102800000000000}""", "health_status: unhealthy")]
    [InlineData("""{"Type":"container","Action":"health_status","Actor":{"ID":"abc","Attributes":{"name":"gb-web","health_status":"healthy"}},"time":1762102800}""", "health_status: healthy")]
    [InlineData("""{"Type":"container","Action":"kill","Actor":{"ID":"abc","Attributes":{"name":"gb-web","signal":"15"}},"timeNano":1762102800000000000}""", "kill")]
    [InlineData("not json", null)]
    public void Keeps_only_the_events_the_charts_mark(string json, string? action) =>
        Assert.Equal(action, DockerApi.ParseEvent(json)?.Action);

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("not json", new string[0])]
    [InlineData("""{"postgres":"  Shared database ","sample-web":"","":"x"}""", new[] { "postgres=Shared database" })]
    public void Reads_the_titles_given_to_containers(string? json, string[] expected) =>
        Assert.Equal(expected, ContainerTitles.Parse(json).Select(t => $"{t.Key}={t.Value}").Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("Shared database", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_title_is_short(string? title, string? problem) => Assert.Equal(problem, ContainerTitles.Problem(title));

    [Fact]
    public void A_title_longer_than_the_limit_is_refused() =>
        Assert.NotNull(ContainerTitles.Problem(new string('x', ContainerTitles.MaxLength + 1)));

    [Theory]
    [InlineData("Kanban website", null)]
    [InlineData("  ", "Give the app a name.")]
    [InlineData("!!!", "The name needs at least one letter or digit.")]
    public void An_app_name_needs_a_letter_or_digit(string name, string? problem) =>
        Assert.Equal(problem, Gitberth.Services.AppService.NameProblem(name));

    [Fact]
    public void Deploys_label_their_containers_with_the_deploy()
    {
        var app = new AppDefinition { Slug = "web", Name = "web" };
        var labels = ContainerSpecFactory.For(app, "gitberth/web:1", [], "gitberth", deploy: (12, DeployTrigger.Poll)).Labels;
        Assert.Equal("12", labels[ContainerSpecFactory.DeploymentLabel]);
        Assert.Equal("Poll", labels[ContainerSpecFactory.TriggerLabel]);
        Assert.False(ContainerSpecFactory.For(app, "gitberth/web:1", [], "gitberth").Labels.ContainsKey(ContainerSpecFactory.DeploymentLabel));
    }

    [Fact]
    public async Task Readings_and_events_survive_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory("gitberth-stats-");
        try
        {
            var store = new StatsStore(new GitberthOptions { DataDir = dir.FullName });
            var now = DateTime.UtcNow;
            await store.AddPointsAsync([
                ("gb-web", new StatsPoint(now.AddHours(-7), 9, null, null)),
                ("gb-web", new StatsPoint(now.AddSeconds(-10), 1.5, 100, 200, 4096)),
                ("gb-web", new StatsPoint(now.AddSeconds(-5), 2.5, null, null)),
                ("gb-db", new StatsPoint(now.AddSeconds(-5), 0.5, 1, 1, 1)),
            ]);
            var start = new ContainerEvent(now.AddMinutes(-1), "abc", "gb-web", "start", Deployment: 12, Trigger: "Poll");
            await store.AddEventsAsync([start, start, new ContainerEvent(now.AddHours(-8), "abc", "gb-web", "die", ExitCode: 1)]);

            var points = await store.LoadPointsAsync(now.AddHours(-6));
            Assert.Equal([1.5, 2.5], points["gb-web"].Select(p => p.Cpu));
            Assert.Equal(new StatsPoint(now.AddSeconds(-10), 1.5, 100, 200, 4096), points["gb-web"][0]);
            Assert.Single(points["gb-db"]);
            Assert.Equal([start], await store.LoadEventsAsync(now.AddHours(-6)));

            await store.PruneAsync(now.AddHours(-6), now.AddHours(-7));
            Assert.Equal(3, (await store.LoadPointsAsync(DateTime.MinValue)).Sum(p => p.Value.Count));
            Assert.Equal([start], await store.LoadEventsAsync(DateTime.MinValue));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_watcher_ignores_repeats_and_keeps_events_in_order()
    {
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        using var docker = new DockerApi(options);
        var watcher = new ContainerEventWatcher(docker, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance);
        var now = DateTime.UtcNow;
        var late = new ContainerEvent(now.AddSeconds(-5), "a", "gb-web", "start");
        var early = new ContainerEvent(now.AddSeconds(-10), "a", "gb-web", "die", ExitCode: 1);

        Assert.True(watcher.Remember(late, now));
        Assert.False(watcher.Remember(late, now));
        // The engine replaying an older one after a reconnect.
        Assert.True(watcher.Remember(early, now));
        Assert.False(watcher.Remember(new ContainerEvent(now.AddHours(-8), "a", "gb-web", "start"), now));
        Assert.Equal([early, late], watcher.Events);
    }

    [Fact]
    public void The_watcher_passes_each_start_it_hears_on_once()
    {
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        using var docker = new DockerApi(options);
        var watcher = new ContainerEventWatcher(docker, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance);
        var now = DateTime.UtcNow;
        var started = new List<string>();
        // A handler that throws keeps the start from nobody else, and doesn't stop the watching.
        watcher.Started += _ => throw new InvalidOperationException("boom");
        watcher.Started += started.Add;

        var start = new ContainerEvent(now.AddSeconds(-3), "a", "postgres", "start");
        Assert.True(watcher.Heard(start, now));
        // The engine replaying it after a reconnect: heard once.
        Assert.False(watcher.Heard(start, now));
        Assert.True(watcher.Heard(new ContainerEvent(now.AddSeconds(-2), "a", "postgres", "die", ExitCode: 0), now));
        Assert.True(watcher.Heard(new ContainerEvent(now.AddSeconds(-1), "b", "sample-index", "start"), now));

        Assert.Equal(["postgres", "sample-index"], started);
        Assert.Equal(3, watcher.Events.Count);
    }
}
