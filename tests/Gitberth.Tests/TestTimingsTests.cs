using Gitberth.Audit;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The end-to-end rig's shorter waits (<see cref="TestTimings"/>) and the guard that keeps every install as it is: unset
/// or blank changes nothing, anything it doesn't understand stops start-up, each setting reaches its service and can only
/// shorten its wait, the restart scheduler counts a schedule's minutes by it while saying them in real minutes, and only
/// the rig's compose file sets it.
/// </summary>
public sealed class TestTimingsTests : IDisposable
{
    private readonly AppsRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>The four services the settings reach, built as the other tests build them, and given out as Program.cs registers them.</summary>
    private sealed record Built(DeployPipeline Pipeline, PortChanger Changer, PreviewService Previews, RestartScheduler Scheduler, IServiceProvider Provider);

    private Built Build()
    {
        var engine = new FakeEngine(new FakeClock());
        var host = new FakeHost(engine);
        var changer = new PortChanger(engine, new MemoryJournal(), _rig.ContainerLocks, host, host, host, host, host, _rig.Options, NullLogger<PortChanger>.Instance);
        var previews = new PreviewService(_rig.Db, _rig.Actions, _rig.Apps, _rig.Domains, new GitHubPulls(new ImageKit.NoHttp(), _rig.Settings),
            new GitClient(_rig.Options, _rig.Settings), _rig.Cli, _rig.Activity, new WatchState(), NullLogger<PreviewService>.Instance, gate: _rig.PreviewGate);
        var scheduler = new RestartScheduler(_rig.Db, _rig.Cli, _rig.Restarts, _rig.Actions, _rig.AppLocks, NullLogger<RestartScheduler>.Instance, _rig.ContainerLocks);
        var services = new ServiceCollection();
        services.AddSingleton(_rig.Pipeline);
        services.AddSingleton(changer);
        services.AddSingleton(previews);
        services.AddSingleton(scheduler);
        return new Built(_rig.Pipeline, changer, previews, scheduler, services.BuildServiceProvider());
    }

    private static GitberthOptions Options(string? value) => GitberthOptions.FromConfiguration(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [TestTimings.Variable] = value })
        .Build(), Path.Combine(Path.GetTempPath(), "gitberth-test-timings"));

    // Every wait as an install has it.
    private static void AsInstalled(Built s)
    {
        Assert.Equal(TimeSpan.FromSeconds(15), s.Pipeline.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(15), s.Changer.StableFor);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Previews.PullsEvery);
        Assert.Equal(TimeSpan.FromMinutes(1), s.Scheduler.TickEvery);
        Assert.Equal(TimeSpan.FromMinutes(1), s.Scheduler.MinuteLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_it_changes_nothing(string? value)
    {
        Assert.Null(Options(value).TestTimings);
        Assert.Null(TestTimings.Parse(value));
        // Freshly built services keep the waits an install has: nothing sets them without the variable.
        AsInstalled(Build());
    }

    [Fact]
    public void It_reads_its_settings_in_seconds_and_says_them_back_in_a_fixed_order()
    {
        var timings = TestTimings.Parse("stableFor=3, pullsEvery=20,restartMinute=20,restartTick=2")!;

        Assert.Equal([("pullsEvery", 20.0), ("restartMinute", 20.0), ("restartTick", 2.0), ("stableFor", 3.0)],
            timings.Values.Select(v => (v.Key, v.Value.TotalSeconds)));
        Assert.Equal("pullsEvery=20,restartMinute=20,restartTick=2,stableFor=3", timings.ToString());
        Assert.Equal(timings.ToString(), TestTimings.Parse(timings.ToString())!.ToString());
        // As Program.cs reads it, from the environment; fractions of a second too.
        Assert.Equal("restartTick=0.5,stableFor=3", Options(" stableFor=3,restartTick=0.5 ").TestTimings!.ToString());
    }

    [Theory]
    [InlineData("stablefor=3", "stablefor")]
    [InlineData("stableFor", "stableFor")]
    [InlineData("stableFor=0", "stableFor")]
    [InlineData("stableFor=-1", "stableFor")]
    [InlineData("stableFor=abc", "stableFor")]
    [InlineData("stableFor=NaN", "stableFor")]
    [InlineData("stableFor=Infinity", "stableFor")]
    [InlineData("stableFor=0.05", "stableFor")]
    [InlineData("stableFor=4000", "stableFor")]
    [InlineData("stableFor=3,stableFor=4", "stableFor")]
    [InlineData("watch=2", "watch")]
    [InlineData("stableFor=3,", "stableFor")]
    [InlineData("pullsEvery=20,,stableFor=3", "pullsEvery")]
    public void It_refuses_what_it_doesnt_know_and_so_stops_start_up(string value, string key)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => TestTimings.Parse(value));
        Assert.StartsWith(TestTimings.Variable + ": ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(key, refused.Message, StringComparison.Ordinal);
        // Program.cs reads the options first of all: start-up stops there, before anything runs.
        Assert.Equal(refused.Message, Assert.Throws<InvalidOperationException>(() => Options(value)).Message);
    }

    [Theory]
    [InlineData(",")]
    [InlineData("=3")]
    public void Nothing_but_separators_is_refused_too_never_taken_as_unset(string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Options(value));
        Assert.StartsWith(TestTimings.Variable + ": ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void It_sets_each_wait_on_its_service()
    {
        var s = Build();

        TestTimings.Parse("stableFor=3,pullsEvery=20,restartMinute=20,restartTick=2")!.Apply(s.Provider);

        // The stay-up check of deploys and of the change engine alike.
        Assert.Equal(TimeSpan.FromSeconds(3), s.Pipeline.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(3), s.Changer.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(20), s.Previews.PullsEvery);
        Assert.Equal(TimeSpan.FromSeconds(20), s.Scheduler.MinuteLength);
        Assert.Equal(TimeSpan.FromSeconds(2), s.Scheduler.TickEvery);
    }

    [Theory]
    [InlineData("pullsEvery=400")]
    [InlineData("pullsEvery=300")]
    [InlineData("stableFor=15")]
    [InlineData("restartTick=60")]
    [InlineData("stableFor=3,restartMinute=61")]
    public void It_only_shortens_a_wait_and_otherwise_changes_nothing(string value)
    {
        var s = Build();

        var refused = Assert.Throws<InvalidOperationException>(() => TestTimings.Parse(value)!.Apply(s.Provider));

        Assert.Contains("isn't shorter than", refused.Message, StringComparison.Ordinal);
        // Not even the settings that would have been shorter.
        AsInstalled(s);
    }

    [Fact]
    public async Task The_scheduler_counts_a_schedules_minutes_by_its_length_and_still_says_them_in_real_minutes()
    {
        // A container started at 01:00:00, restarted every 5 minutes.
        _rig.Engine.AddLike("sample-bun-elysia", "gb-sched");
        await _rig.Restarts.SetAsync("gb-sched", new RestartInterval(5, RestartUnit.Minutes));
        using var audit = new AuditLog();
        var scheduler = new RestartScheduler(_rig.Db, _rig.Cli, _rig.Restarts, _rig.Actions, _rig.AppLocks, NullLogger<RestartScheduler>.Instance,
            _rig.ContainerLocks, audit: audit);
        var start = new DateTime(2026, 11, 7, 1, 0, 0, DateTimeKind.Utc);

        // As installed, 6 s after the start is nowhere near 5 minutes.
        Assert.Empty(await scheduler.TickAsync(start.AddSeconds(6)));
        // With a minute of 1 s, it's due 5 s after the start: not at 4 s, at 6 s.
        scheduler.MinuteLength = TimeSpan.FromSeconds(1);
        Assert.Empty(await scheduler.TickAsync(start.AddSeconds(4)));
        Assert.Equal(["gb-sched"], await scheduler.TickAsync(start.AddSeconds(6)));
        Assert.Equal(["docker restart -t 20 gb-sched"], _rig.Engine.Writes);

        // What it says about the schedule is in the schedule's own minutes.
        var entry = Assert.Single(audit.Entries, e => e.Action == "app.restarted");
        Assert.Equal("Restarted gb-sched on its schedule (every 5 minutes).", entry.Summary);
    }

    [Fact]
    public void Only_the_rigs_compose_file_sets_it()
    {
        var root = RepoRoot();
        bool Names(string file) => File.ReadAllText(file).Contains(TestTimings.Variable, StringComparison.Ordinal);
        string Relative(string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

        // Nothing an install runs from, nor its docs, names it: docker-compose.yml passes Gitberth three named variables only.
        var install = new[] { "docker-compose.yml", "docker-compose.build.yml", ".env.example", "install.sh", "Dockerfile" }
            .Select(f => Path.Combine(root, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*", SearchOption.AllDirectories));
        Assert.Empty(install.Where(Names).Select(Relative));
        // In Gitberth's own code only TestTimings reads it; everything else goes by TestTimings.Variable.
        var code = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(f => !Relative(f).Split('/').Any(part => part is "bin" or "obj"));
        Assert.Equal(["src/Gitberth/TestTimings.cs"], code.Where(Names).Select(Relative));
        // The rig's compose file sets it.
        Assert.Contains($"{TestTimings.Variable}:", File.ReadAllText(Path.Combine(root, "tests", "e2e", "rig", "compose.e2e.yml")), StringComparison.Ordinal);
    }

    // The repo's root: the folder above the tests' own that has docs/reference, as AgentToolTests finds it.
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "reference"))) return dir.FullName;
        throw new InvalidOperationException("The repo's root wasn't found above the test's folder.");
    }
}
