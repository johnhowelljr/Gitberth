using Gitberth.Data;
using Gitberth.Domains;
using Facts = Gitberth.Domains.DownWatcher.Facts;
using Track = Gitberth.Domains.DownWatcher.Track;

namespace Gitberth.Tests;

/// <summary>
/// When an app's domains show the down page: at once once it's stopped; after 20 seconds of its container not running
/// (a restart takes less); back as soon as it runs again; and an app that keeps going down and up only comes back once it
/// has run for a minute, so a crash loop doesn't change the tunnel over and over.
/// </summary>
public class DownWatcherTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    private static Facts Running(bool running = true, DesiredState desired = DesiredState.Running, bool downPage = true) =>
        new(downPage, HasDomains: true, HasWebPort: true, desired, running);

    [Fact]
    public void A_stopped_app_shows_the_down_page_at_once_running_or_not()
    {
        var track = new Track();
        Assert.Equal(DownReason.Stopped, DownWatcher.Decide(Running(true, DesiredState.Stopped), null, track, T0));
        Assert.Equal(DownReason.Stopped, DownWatcher.Decide(Running(false, DesiredState.Stopped), DownReason.Stopped, track, T0.AddSeconds(1)));
    }

    [Fact]
    public void A_container_that_stopped_gets_the_down_page_after_twenty_seconds_not_before()
    {
        var track = new Track();
        Assert.Null(DownWatcher.Decide(Running(), null, track, T0));
        Assert.Null(DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(1)));
        Assert.Null(DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(20)));
        Assert.Equal(T0.AddSeconds(21), DownWatcher.DueAt(Running(false), null, track));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(21)));
    }

    [Fact]
    public void A_restart_within_the_grace_never_shows_the_down_page()
    {
        var track = new Track();
        Assert.Null(DownWatcher.Decide(Running(false), null, track, T0));
        Assert.Null(DownWatcher.Decide(Running(), null, track, T0.AddSeconds(5)));
        // Stopped again later: its time not running starts over.
        Assert.Null(DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(30)));
        Assert.Null(DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(45)));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(false), null, track, T0.AddSeconds(51)));
    }

    [Fact]
    public void It_goes_back_as_soon_as_the_container_runs_again()
    {
        var track = new Track();
        Assert.Null(DownWatcher.Decide(Running(), DownReason.NotRunning, track, T0));
        // Started again after a stop by its owner, but not running yet: it stays on the down page until it is.
        var started = new Track();
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(false), DownReason.Stopped, started, T0));
        Assert.Null(DownWatcher.Decide(Running(), DownReason.NotRunning, started, T0.AddSeconds(3)));
    }

    [Fact]
    public void An_app_that_keeps_going_down_and_up_comes_back_only_once_it_has_run_for_a_minute()
    {
        var track = new Track();
        // Four changes in ten minutes: down, up, down, up.
        track.Changes.AddRange([T0.AddMinutes(-8), T0.AddMinutes(-7), T0.AddMinutes(-5), T0.AddMinutes(-4)]);
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(), DownReason.NotRunning, track, T0));
        Assert.Equal(T0.AddSeconds(60), DownWatcher.DueAt(Running(), DownReason.NotRunning, track));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(), DownReason.NotRunning, track, T0.AddSeconds(59)));
        Assert.Null(DownWatcher.Decide(Running(), DownReason.NotRunning, track, T0.AddSeconds(60)));
        // Crashing again in the minute: the minute starts over.
        var again = new Track();
        again.Changes.AddRange(track.Changes);
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(), DownReason.NotRunning, again, T0));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(false), DownReason.NotRunning, again, T0.AddSeconds(30)));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(), DownReason.NotRunning, again, T0.AddSeconds(40)));
        Assert.Equal(DownReason.NotRunning, DownWatcher.Decide(Running(), DownReason.NotRunning, again, T0.AddSeconds(99)));
        Assert.Null(DownWatcher.Decide(Running(), DownReason.NotRunning, again, T0.AddSeconds(100)));
        // Old changes don't count: ten minutes on, it comes back at once again.
        Assert.Null(DownWatcher.Decide(Running(), DownReason.NotRunning, new Track { Changes = [.. track.Changes] }, T0.AddMinutes(10)));
    }

    [Fact]
    public void Without_its_down_page_a_domain_or_a_web_port_nothing_is_shown()
    {
        Assert.Null(DownWatcher.Decide(Running(false, DesiredState.Stopped, downPage: false), DownReason.Stopped, new Track(), T0));
        Assert.Null(DownWatcher.Decide(new Facts(true, HasDomains: false, HasWebPort: true, DesiredState.Stopped, false), null, new Track(), T0));
        Assert.Null(DownWatcher.Decide(new Facts(true, HasDomains: true, HasWebPort: false, DesiredState.Stopped, false), null, new Track(), T0));
    }

    [Fact]
    public void The_states_count_a_deploys_update_on_top_of_what_the_watcher_decided()
    {
        var states = new DownStates();
        var changes = 0;
        states.Changed += () => changes++;
        Assert.Null(states.For(1));
        Assert.True(states.Set(1, DownReason.NotRunning));
        Assert.False(states.Set(1, DownReason.NotRunning));
        states.BeginUpdate(1);
        states.BeginUpdate(1);
        Assert.Equal(DownReason.Updating, states.For(1));
        states.EndUpdate(1);
        Assert.True(states.IsUpdating(1));
        states.EndUpdate(1);
        Assert.False(states.IsUpdating(1));
        Assert.Equal(DownReason.NotRunning, states.For(1));
        Assert.True(states.Set(1, null));
        Assert.Null(states.For(1));
        Assert.Equal(6, changes);
        var nudged = new List<int>();
        states.Nudged += nudged.Add;
        states.Nudge(5);
        Assert.Equal([5], nudged);
    }
}
