using Gitberth.Data;
using Gitberth.Health;
using Gitberth.Keeper;

namespace Gitberth.Tests;

/// <summary>
/// The keep-running check's rules, pure: which stops are brought back (a crash its restart policy leaves, running out of
/// memory, a start Docker couldn't make, a stop when Docker or the host restarted, a deploy cut short) and which are left
/// (a stop someone asked for, a program that ended with 0), the grace, the tries and their back-off, and the words.
/// </summary>
public sealed class KeepRulesTests
{
    private static readonly DateTime T = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    // A container that exited a minute before now: well past the grace.
    private static KeepFacts Exited(int code, string policy = "no", KeepKind kind = KeepKind.Image) =>
        new(kind, "exited", code, T.AddMinutes(-1), Policy: policy);

    private static KeepVerdict Decide(KeepFacts f) => KeepRules.Decide(f, T, Grace);

    // ---- Brought back ----

    [Theory]
    [InlineData("no", "it crashed (exit code 3), and its restart policy (no) doesn't start it again")]
    [InlineData("on-failure:3", "it crashed (exit code 3), and its restart policy (on-failure:3) didn't start it again")]
    [InlineData("on-failure", "it crashed (exit code 3), and its restart policy (on-failure) didn't start it again")]
    public void A_crash_its_restart_policy_leaves_is_brought_back(string policy, string told)
    {
        var v = Decide(Exited(3, policy));

        Assert.Equal(KeepAction.BringBack, v.Action);
        Assert.Equal(KeepCause.Crashed, v.Reason!.Cause);
        Assert.Equal(8, v.Rule);
        Assert.Equal(told, v.Reason.Told);
    }

    [Fact]
    public void Running_out_of_memory_and_a_start_Docker_couldnt_make_are_brought_back_too()
    {
        var oom = Decide(Exited(137) with { OutOfMemory = true });
        Assert.Equal((KeepAction.BringBack, KeepCause.OutOfMemory, 4), (oom.Action, oom.Reason!.Cause, oom.Rule));

        var failed = Decide(Exited(128) with { Error = "driver failed programming external connectivity: Bind for 0.0.0.0:3000 failed: port is already allocated" });
        Assert.Equal((KeepAction.BringBack, KeepCause.CouldntStart, 5), (failed.Action, failed.Reason!.Cause, failed.Rule));
        Assert.Equal("Docker couldn't start it: driver failed programming external connectivity: Bind for 0.0.0.0:3000 failed: port is already allocated",
            failed.Reason.Words);
    }

    [Fact]
    public void A_death_Gitberth_heard_says_whether_it_was_a_crash()
    {
        // A non-ending signal (USR1) before an exit with 138 is a crash, as the charts read it.
        var events = new List<ContainerEvent>
        {
            new(T.AddMinutes(-1).AddSeconds(-1), "c1", "web", "kill", Signal: 10),
            new(T.AddMinutes(-1), "c1", "web", "die", ExitCode: 138),
        };
        var (crashed, oom) = EventMarks.CrashOf(events, 1);
        Assert.True(crashed);
        Assert.False(EventMarks.AskedToStop(events, 1));
        var v = Decide(Exited(138) with { Died = new KeepDeath(crashed, oom, false) });
        Assert.Equal((KeepAction.BringBack, KeepCause.Crashed, 6), (v.Action, v.Reason!.Cause, v.Rule));
    }

    // ---- Left alone ----

    [Theory]
    [InlineData("no")]
    [InlineData("always")]
    [InlineData("unless-stopped")]
    [InlineData("on-failure:3")]
    public void An_exit_with_0_is_left_under_every_policy(string policy)
    {
        var v = Decide(Exited(0, policy));

        Assert.Equal((KeepAction.Leave, KeepCause.Ended, 9), (v.Action, v.Reason!.Cause, v.Rule));
    }

    [Theory]
    [InlineData(130)]
    [InlineData(137)]
    [InlineData(143)]
    public void A_stops_exit_code_is_left_when_no_event_says_otherwise(int code)
    {
        var v = Decide(Exited(code));

        Assert.Equal((KeepAction.Leave, KeepCause.Stopped, 9), (v.Action, v.Reason!.Cause, v.Rule));
    }

    [Fact]
    public void A_kill_before_the_death_is_a_stop_whatever_the_exit_code()
    {
        var events = new List<ContainerEvent>
        {
            new(T.AddMinutes(-1).AddSeconds(-2), "c1", "web", "kill", Signal: 15),
            new(T.AddMinutes(-1), "c1", "web", "die", ExitCode: 1),
        };
        var (crashed, oom) = EventMarks.CrashOf(events, 1);
        Assert.False(crashed);
        Assert.True(EventMarks.AskedToStop(events, 1));

        var v = Decide(Exited(1) with { Died = new KeepDeath(crashed, oom, true) });

        Assert.Equal((KeepAction.Leave, KeepCause.Stopped, 7), (v.Action, v.Reason!.Cause, v.Rule));
        Assert.Equal("It was stopped (exit code 1), not by a crash, so Gitberth leaves it stopped. Start it to bring it back.", KeepText.Left(v.Reason));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("restarting")]
    [InlineData("paused")]
    [InlineData("removing")]
    public void One_that_runs_or_is_on_its_way_is_nothing_to_do(string state) =>
        Assert.Equal(KeepAction.None, Decide(new KeepFacts(KeepKind.Image, state)).Action);

    [Fact]
    public void Created_and_dead_are_left()
    {
        var created = Decide(new KeepFacts(KeepKind.Image, "created"));
        Assert.Equal((KeepAction.Leave, 10), (created.Action, created.Rule));
        Assert.Null(created.Reason);

        var dead = Decide(new KeepFacts(KeepKind.Image, "dead", 1, T.AddMinutes(-5)));
        Assert.Equal((KeepAction.Leave, KeepCause.Dead), (dead.Action, dead.Reason!.Cause));
    }

    [Fact]
    public void A_missing_container_is_made_again()
    {
        var v = Decide(new KeepFacts(KeepKind.Git, null));

        Assert.Equal((KeepAction.MakeAgain, KeepCause.Gone, 11), (v.Action, v.Reason!.Cause, v.Rule));
    }

    // ---- Docker or the host restarted (rule 2) ----

    [Fact]
    public void A_stop_with_Gitberths_own_is_a_restart_of_Docker_or_the_host()
    {
        // Gitberth stopped 10 s after the app and started 40 s after it: they went down together.
        var withIt = Exited(143) with { FinishedAt = T.AddMinutes(-2), OwnStoppedAt = T.AddMinutes(-2).AddSeconds(10), OwnStartedAt = T.AddMinutes(-2).AddSeconds(40) };
        var v = Decide(withIt);
        Assert.Equal((KeepAction.BringBack, KeepCause.HostRestarted, 2), (v.Action, v.Reason!.Cause, v.Rule));
        // Even an exit with 0: Docker would have started it again itself with always.
        Assert.Equal(KeepAction.BringBack, Decide(withIt with { ExitCode = 0 }).Action);

        // 61 s apart isn't together.
        var apart = Decide(withIt with { OwnStoppedAt = T.AddMinutes(-2).AddSeconds(61) });
        Assert.Equal((KeepAction.Leave, 9), (apart.Action, apart.Rule));
        // A Gitberth container that never stopped (an upgrade made a new one) says nothing of a restart.
        Assert.Equal(KeepAction.Leave, Decide(withIt with { OwnStoppedAt = null }).Action);
        // A stop after Gitberth started again is someone's, not the restart's.
        Assert.Equal(KeepAction.Leave, Decide(withIt with { FinishedAt = T.AddMinutes(-2).AddSeconds(45), OwnStoppedAt = T.AddMinutes(-2).AddSeconds(30) }).Action);
    }

    // ---- A deploy cut short (rule 3) ----

    [Fact]
    public void A_git_app_a_failed_deploy_stopped_is_brought_back_but_not_after_one_that_worked()
    {
        var during = Exited(143, kind: KeepKind.Git) with
        {
            LastDeploy = new KeepRun(T.AddMinutes(-1).AddSeconds(-20), T.AddMinutes(-1).AddSeconds(20), DeployStatus.Failed),
        };
        var v = Decide(during);
        // It wins over the stop's exit code.
        Assert.Equal((KeepAction.BringBack, KeepCause.DeployCutShort, 3), (v.Action, v.Reason!.Cause, v.Rule));
        Assert.Equal(KeepAction.BringBack, Decide(during with { LastDeploy = during.LastDeploy! with { Status = DeployStatus.RolledBack } }).Action);

        Assert.Equal(KeepAction.Leave, Decide(during with { LastDeploy = during.LastDeploy! with { Status = DeployStatus.Succeeded } }).Action);
        // Another kind of app has no deploys of Gitberth's.
        Assert.Equal(KeepAction.Leave, Decide(during with { Kind = KeepKind.Image }).Action);
        // A stop outside the deploy's run is no part of it.
        Assert.Equal(KeepAction.Leave, Decide(during with { FinishedAt = T.AddMinutes(-3) }).Action);
    }

    [Fact]
    public void A_stop_Gitberth_knows_it_didnt_make_while_a_deploy_ran_is_left_when_the_deploy_fails()
    {
        // docker stop during a long build that then failed: Gitberth heard it, and wasn't stopping the container then.
        var during = Exited(1, kind: KeepKind.Git) with
        {
            LastDeploy = new KeepRun(T.AddMinutes(-11), T.AddMinutes(-1).AddSeconds(30), DeployStatus.Failed),
            Died = new KeepDeath(Crashed: false, OutOfMemory: false, AskedToStop: true, ByGitberth: false),
        };
        var v = Decide(during);
        Assert.Equal((KeepAction.Leave, KeepCause.Stopped, 7), (v.Action, v.Reason!.Cause, v.Rule));

        // The deploy's own stop (its swap), or one Gitberth can't say of any more, is still the deploy's.
        var own = Decide(during with { Died = during.Died! with { ByGitberth = true } });
        Assert.Equal((KeepAction.BringBack, KeepCause.DeployCutShort), (own.Action, own.Reason!.Cause));
        var unknown = Decide(during with { Died = during.Died! with { ByGitberth = null } });
        Assert.Equal((KeepAction.BringBack, KeepCause.DeployCutShort), (unknown.Action, unknown.Reason!.Cause));
    }

    [Fact]
    public void A_cancelled_deploy_to_bring_it_back_is_said_plainly()
    {
        var why = new KeepReason(KeepCause.Cancelled);

        Assert.Equal("the deploy that was to bring it back was cancelled", why.Words);
        Assert.Equal("The deploy that was to bring it back was cancelled, so Gitberth leaves it as it is. Start or deploy it to bring it back.", KeepText.Left(why));
    }

    // ---- The grace ----

    [Fact]
    public void A_stop_is_brought_back_only_once_its_30_seconds_old()
    {
        Assert.Equal(KeepAction.Wait, KeepRules.Decide(Exited(3) with { FinishedAt = T.AddSeconds(-10) }, T, Grace).Action);
        Assert.Equal(KeepAction.Wait, KeepRules.Decide(Exited(3) with { FinishedAt = T.AddSeconds(-29) }, T, Grace).Action);
        Assert.Equal(KeepAction.BringBack, KeepRules.Decide(Exited(3) with { FinishedAt = T.AddSeconds(-30) }, T, Grace).Action);
        // Left alone at once: nothing to wait for.
        Assert.Equal(KeepAction.Leave, KeepRules.Decide(Exited(0) with { FinishedAt = T.AddSeconds(-1) }, T, Grace).Action);
    }

    // ---- The tries ----

    [Fact]
    public void Four_tries_at_once_then_1_5_and_15_minutes_after_the_one_before_and_10_minutes_up_clear_them()
    {
        Assert.Equal(DateTime.MinValue, KeepRules.NextTry(0, null));
        Assert.Equal(T.AddMinutes(1), KeepRules.NextTry(1, T));
        Assert.Equal(T.AddMinutes(5), KeepRules.NextTry(2, T));
        Assert.Equal(T.AddMinutes(15), KeepRules.NextTry(3, T));
        Assert.Null(KeepRules.NextTry(4, T));

        Assert.False(KeepRules.IsSteady(T, T.AddMinutes(10).AddSeconds(-1)));
        Assert.True(KeepRules.IsSteady(T, T.AddMinutes(10)));
        Assert.True(KeepRules.IsSteady(null, T));
    }

    // ---- The words ----

    [Fact]
    public void What_a_try_says_in_Activity_and_the_audit_log()
    {
        var crash = new KeepReason(KeepCause.Crashed, 3, Policy: "no");
        Assert.Equal("Gitberth started it again: it crashed (exit code 3), and its restart policy (no) doesn't start it again. Try 1 of 4.",
            KeepText.Tried(KeepHow.Started, crash, 1));
        Assert.Equal("Gitberth started worker again: it crashed (exit code 3), and its restart policy (no) doesn't start it again. Try 2 of 4.",
            KeepText.Tried(KeepHow.Started, crash, 2, services: ["worker"]));
        var gone = new KeepReason(KeepCause.Gone);
        Assert.Equal("Gitberth made its container again from gitberth/shop:1a2b3c4 (deploy #12): its container was gone. Try 1 of 4.",
            KeepText.Tried(KeepHow.MadeFromImage, gone, 1, "gitberth/shop:1a2b3c4", 12));
        Assert.Equal("Gitberth built it again from 1a2b3c4 (deploy #13): its container and its image were gone.",
            KeepText.Tried(KeepHow.Built, gone, 1, deployment: 13, commit: "1a2b3c4d5e6f"));
        Assert.Equal("Gitberth made its container again from its saved settings: its container was gone. Try 3 of 4.", KeepText.Tried(KeepHow.MadeFromSettings, gone, 3));
        // Docker's words in the middle of a sentence: one line, without its capital or its full stop.
        Assert.Equal("Gitberth couldn't start it: port is already allocated. Try 2 of 4.", KeepText.Failed("Port is already allocated.\n", 2));
        Assert.Equal("Gitberth started it again: it stopped when Docker or the host restarted. Try 1 of 4.",
            KeepText.Tried(KeepHow.Started, new KeepReason(KeepCause.HostRestarted, 143, Policy: "no"), 1));

        Assert.Equal("Brought shop back: it crashed (exit code 3), and its restart policy (no) doesn't start it again. It runs again.", KeepText.Recovered("shop", KeepHow.Started, crash));
        Assert.Equal("Bringing shop back: its container was gone, so deploy #12 makes it again from gitberth/shop:1a2b3c4.",
            KeepText.Recovered("shop", KeepHow.MadeFromImage, gone, "gitberth/shop:1a2b3c4", 12));
        Assert.Equal("Couldn't bring shop back: no such image.", KeepText.CouldntRecover("shop", "No such image."));
        Assert.Equal("made its container again (deploy #12)", KeepText.HowWords(KeepHow.MadeFromImage, 12));
        Assert.Equal("Gitberth tried again, as it does every 6 hours since it stopped bringing it back, and started it.", KeepText.QuietTry(KeepHow.Started, null));
    }

    [Fact]
    public void What_a_give_up_says_everywhere()
    {
        var crash = new KeepReason(KeepCause.Crashed, 3, Policy: "no");
        var over = TimeSpan.FromMinutes(21);
        Assert.Equal("Gitberth stopped bringing it back. It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). It tries again every 6 hours; start it once it's fixed, and its Logs tab says why it stops.",
            KeepText.GaveUp(crash, 4, over));
        Assert.Equal("Stopped bringing shop back. It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). Gitberth tries again every 6 hours.",
            KeepText.StoppedTrying("shop", crash, 4, over));
        var (title, text) = KeepText.GaveUpNotice("shop", crash, 4, over);
        Assert.Equal("Gitberth stopped bringing shop back", title);
        Assert.Equal("It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). Gitberth tries again every 6 hours; it stays down until then, or until you start or deploy it. Its log on Gitberth has the details.", text);
        Assert.Equal("shop (gb-shop): Gitberth stopped bringing it back. It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). It tries again every 6 hours.",
            KeepText.GaveUpProblem("shop", "gb-shop", crash, 4, over));
        Assert.Equal("It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). Its Logs tab says why. Start it once it's fixed; until then Gitberth tries again every 6 hours, next in about 30 minutes.",
            KeepText.GaveUpBanner(crash, 4, over, T.AddMinutes(30), T));

        // A build that didn't work is one try, and says so.
        var build = new KeepReason(KeepCause.BuildFailed);
        Assert.Equal("Its container and its image were gone, and building it again didn't work. Gitberth tries again every 6 hours; it stays down until then, or until you start or deploy it. The deploy's log on Gitberth has the details.",
            KeepText.GaveUpNotice("shop", build, 1, TimeSpan.Zero).Text);
        Assert.Equal("Its container and its image were gone, and building it again didn't work. The deploy's log says why. Deploy it once it's fixed; until then Gitberth tries again every 6 hours.",
            KeepText.GaveUpBanner(build, 1, TimeSpan.Zero, null, T));
    }

    [Fact]
    public void What_notices_and_the_settings_line_say()
    {
        var crash = new KeepReason(KeepCause.Crashed, 3, Policy: "no");
        Assert.Equal(("shop is running again", "It crashed (exit code 3), and its restart policy (no) doesn't start it again, so Gitberth started it. Its log on Gitberth has the details."),
            KeepText.BackNotice("shop", KeepHow.Started, crash));
        Assert.Equal(("shop's container is back", "It was gone, so Gitberth made it again from its last image (deploy #12)."),
            KeepText.BackNotice("shop", KeepHow.MadeFromImage, new KeepReason(KeepCause.Gone), "gitberth/shop:1a2b3c4", 12));
        Assert.Equal(("Gitberth started 3 apps again", "They stopped when Docker or the host restarted, and Docker didn't start them again: shop, blog and wiki."),
            KeepText.BatchNotice(["shop", "blog", "wiki"]));
        Assert.Equal("Nothing brought back in the last 24 hours.", KeepText.Recent(0, []));
        Assert.Equal("Brought back 3 times in the last 24 hours: blog and shop.", KeepText.Recent(3, ["blog", "shop"]));
        Assert.Equal("It crashed (exit code 3), and its restart policy (no) doesn't start it again. Gitberth starts it within a minute (try 2 of 4).",
            KeepText.SoonBanner(crash, 2, T.AddSeconds(30), T));
        Assert.Equal("It stopped 4 times in 21 minutes; the last time it crashed (exit code 3). Gitberth stopped trying 2 h ago, and tries again every 6 hours.",
            KeepText.GaveUpLine(crash, 4, TimeSpan.FromMinutes(21), T.AddHours(-2), T));
    }
}
