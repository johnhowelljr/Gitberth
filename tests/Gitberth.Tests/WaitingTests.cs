using System.Globalization;
using System.Text.RegularExpressions;
using Gitberth.Audit;
using Xunit.Sdk;

namespace Gitberth.Tests;

/// <summary>
/// How the tests wait: the held clock does exactly what Task.Delay on a real one does (a timer per wait, fired when its
/// time comes, disposed when its token is cancelled), the waits for background work wait on what they say, and no test
/// sleeps a fixed second or more.
/// </summary>
public sealed partial class WaitingTests
{
    private static readonly TimeSpan Two = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_delay_on_the_held_clock_ends_only_once_the_clock_passes_its_time_and_says_it_fired()
    {
        var clock = new HeldClock();
        var delay = Task.Delay(Two, clock);

        var wait = await clock.NextWaitAsync();
        Assert.Equal((1, Two), (wait.Number, wait.Due));
        // Nothing is due before its time, however long the machine takes.
        clock.Advance(Two - TimeSpan.FromMilliseconds(1));
        Assert.False(delay.IsCompleted);
        Assert.False(wait.Fired);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await delay.WaitAsync(Eventually.Limit);
        Assert.True(wait.Fired);
        Assert.False(wait.Cancelled);
    }

    [Fact]
    public async Task A_delay_whose_token_is_cancelled_ends_at_once_and_its_wait_is_cancelled_never_fired()
    {
        var clock = new HeldClock();
        using var stop = new CancellationTokenSource();
        var delay = Task.Delay(Two * 3, clock, stop.Token);
        var wait = await clock.NextWaitAsync();

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay.WaitAsync(Eventually.Limit));
        Assert.True(wait.Cancelled);
        // Its time passing changes nothing: Task.Delay disposed its timer.
        clock.Advance(Two * 3);
        Assert.False(wait.Fired);
    }

    [Fact]
    public async Task Each_wait_is_its_own_timer_listed_in_the_order_it_was_started()
    {
        var clock = new HeldClock();
        // Asked for before the code starts it: it comes when it's started.
        var first = clock.NextWaitAsync();
        var short1 = Task.Delay(Two, clock);
        var long1 = Task.Delay(Two * 2, clock);

        var (a, b) = (await first, await clock.NextWaitAsync());
        Assert.Equal([(1, Two), (2, Two * 2)], new[] { (a.Number, a.Due), (b.Number, b.Due) });
        clock.Advance(Two);
        await short1.WaitAsync(Eventually.Limit);
        Assert.False(long1.IsCompleted);
        clock.Advance(Two);
        await long1.WaitAsync(Eventually.Limit);
        Assert.All(clock.Waits, w => Assert.True(w.Fired));
    }

    [Fact]
    public async Task Release_all_lets_every_wait_go_and_the_next_wait_is_one_started_after()
    {
        var clock = new HeldClock();
        var waits = new[] { Task.Delay(Two, clock), Task.Delay(Two * 5, clock) };
        clock.ReleaseAll();
        await Task.WhenAll(waits).WaitAsync(Eventually.Limit);

        var later = Task.Delay(Two, clock);
        Assert.Equal(3, (await clock.NextWaitAsync()).Number);
        clock.Advance(Two);
        await later.WaitAsync(Eventually.Limit);
    }

    [Fact]
    public async Task Eventually_waits_for_background_work_and_says_what_never_happened()
    {
        var done = false;
        _ = Task.Run(async () =>
        {
            await Task.Yield();
            Volatile.Write(ref done, true);
        });
        await Eventually.UntilAsync(() => Volatile.Read(ref done), "the background work");
        Assert.Equal("found", await Eventually.UntilAsync<string>(() => Task.FromResult<string?>("found"), "a value"));

        var never = await Assert.ThrowsAsync<FailException>(() => Eventually.UntilAsync(() => false, "something that never happens", TimeSpan.FromMilliseconds(50)));
        Assert.Contains("something that never happens", never.Message);
    }

    [Fact]
    public async Task An_audit_entry_is_found_whether_it_was_recorded_before_the_wait_or_during_it()
    {
        var log = new AuditLog();
        log.Record(new AuditEntry("app.started", "Started web."));
        Assert.Equal("Started web.", (await log.RecordedAsync(e => e.Action == "app.started")).Summary);

        var later = log.RecordedAsync(e => e.Action == "app.stopped");
        _ = Task.Run(() => log.Record(new AuditEntry("app.stopped", "Stopped web.")));
        Assert.Equal("Stopped web.", (await later).Summary);
    }

    // Task.Delay or Thread.Sleep of a literal second or more: a test that waits a fixed time for something to have happened.
    [GeneratedRegex(@"\b(?:Task\.Delay|Thread\.Sleep)\(\s*(?:TimeSpan\.From(?<unit>Milliseconds|Seconds|Minutes|Hours|Days)\(\s*(?<n>[0-9_.]+)\s*\)|(?<ms>[0-9_]+)\b)")]
    private static partial Regex FixedSleep();

    [Fact]
    public void No_unit_test_sleeps_a_fixed_second_or_more()
    {
        var folder = Path.Combine(HelpTests.RepoRoot, "tests", "Gitberth.Tests");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in FixedSleep().Matches(lines[i]))
                    if (Milliseconds(m) >= 1000) found.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(found.Count == 0,
            "These wait a fixed time for something to happen, which a busy machine makes too short and an idle one wastes. Wait on what's "
            + "awaited instead (Eventually.UntilAsync, AuditWaits.RecordedAsync, a task's WaitAsync), or play the waits with HeldClock or "
            + "FakeClock:\n" + string.Join('\n', found));
    }

    private static double Milliseconds(Match m)
    {
        if (m.Groups["ms"].Success) return double.Parse(m.Groups["ms"].Value.Replace("_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        var n = double.Parse(m.Groups["n"].Value.Replace("_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return n * m.Groups["unit"].Value switch
        {
            "Milliseconds" => 1,
            "Seconds" => 1_000,
            "Minutes" => 60_000,
            "Hours" => 3_600_000,
            _ => 86_400_000,
        };
    }
}
