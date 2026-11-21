using System.Net;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Keeper;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The keep-running check on CT 101's containers (AppsRig), its passes made by hand at the times each test says, so no
/// test waits: what it starts and makes again, what it leaves alone, when it waits, how it tries and gives up, and what it
/// says in Activity, the audit log, notifications and on the attention board. AppsRig's deploy queue never runs what's
/// queued, so a deploy the check asks for is looked at as its row.
/// </summary>
public sealed class AppKeeperTests : IAsyncLifetime
{
    private static readonly DateTime T = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly KeeperRig _k = new();

    private AppsRig Rig => _k.Rig;

    public async Task InitializeAsync() => await _k.TellAsync();

    public Task DisposeAsync()
    {
        _k.Dispose();
        return Task.CompletedTask;
    }

    // ---- Brought back ----

    [Fact]
    public async Task A_crash_its_restart_policy_leaves_is_started_again_once_its_grace_is_over_and_told_once_it_runs()
    {
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");

        await _k.PassAsync(T.AddSeconds(10));
        Assert.Equal(0, _k.Starts("gb-orb"));
        Assert.Equal(KeepState.Soon, _k.Keeper.StatusOf(Rig.ObsId).State);

        await _k.PassAsync(T.AddSeconds(31));
        Assert.Equal(1, _k.Starts("gb-orb"));
        var tried = Assert.Single(await _k.ActivityAsync(Rig.ObsId));
        Assert.Equal((ActivityKind.BroughtBack, ActivityTone.Warning), (tried.Kind, tried.Tone));
        Assert.Equal("Gitberth started it again: it crashed (exit code 3), and its restart policy (no) doesn't start it again. Try 1 of 4.", tried.Text);
        var entry = Assert.Single(_k.Audit.Entries, e => e.Action == "app.recovered");
        Assert.Equal((AuditActorKind.Gitberth, AuditVia.KeepRunning, AuditOutcome.Succeeded), (entry.Actor.Kind, entry.Actor.Via, entry.Outcome));
        Assert.Equal("Brought orb back: it crashed (exit code 3), and its restart policy (no) doesn't start it again. It runs again.", entry.Summary);
        // Told once it's seen running, not before.
        Assert.Empty(_k.Notices());

        await _k.PassAsync(T.AddSeconds(61));
        var notice = Assert.Single(_k.Notices());
        Assert.Equal((NotifyEvent.BroughtBack, NoticeLevel.Warning, "orb is running again", "gb-orb"), (notice.Event, notice.Level, notice.Title, notice.Container));
        // Running, it's nothing to do any more; nor is it told twice.
        await _k.PassAsync(T.AddMinutes(5));
        Assert.Empty(_k.Notices());
        Assert.Equal(1, _k.Starts("gb-orb"));
        Assert.Equal(KeepState.None, _k.Keeper.StatusOf(Rig.ObsId).State);
    }

    [Fact]
    public async Task It_tries_4_times_backing_off_then_gives_up_says_so_once_and_tries_quietly_every_6_hours()
    {
        // Crashes at once each time it's started.
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        await _k.PassAsync(T.AddSeconds(31));
        Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(40));
        await _k.PassAsync(T.AddSeconds(71));
        // 1 minute after the first try, not before.
        Assert.Equal(1, _k.Starts("gb-orb"));
        Assert.Equal(KeepState.Soon, _k.Keeper.StatusOf(Rig.ObsId).State);
        await _k.PassAsync(T.AddSeconds(92));
        Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(100));
        await _k.PassAsync(T.AddSeconds(92 + 299));
        Assert.Equal(2, _k.Starts("gb-orb"));
        await _k.PassAsync(T.AddSeconds(92 + 301));
        Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(400));
        await _k.PassAsync(T.AddSeconds(393 + 901));
        Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(1300));
        Assert.Equal(4, _k.Starts("gb-orb"));
        // Only the give-up is told: the first try never ran long enough to be.
        Assert.Empty(_k.Notices());

        var gaveUpAt = T.AddSeconds(1331);
        await _k.PassAsync(gaveUpAt);
        Assert.Equal(4, _k.Starts("gb-orb"));
        var notice = Assert.Single(_k.Notices());
        Assert.Equal((NotifyEvent.GaveUp, NoticeLevel.Critical, "Gitberth stopped bringing orb back"), (notice.Event, notice.Level, notice.Title));
        Assert.StartsWith("It stopped 4 times in 21 minutes; the last time it crashed (exit code 3).", notice.Text, StringComparison.Ordinal);
        var activity = await _k.ActivityAsync(Rig.ObsId);
        Assert.Equal(4, activity.Count(a => a.Kind == ActivityKind.BroughtBack));
        Assert.Equal(ActivityTone.Danger, Assert.Single(activity, a => a.Kind == ActivityKind.GaveUp).Tone);
        var stopped = Assert.Single(_k.Audit.Entries, e => e.Action == "app.recovery.stopped");
        Assert.Equal((AuditVia.KeepRunning, AuditOutcome.Failed), (stopped.Actor.Via, stopped.Outcome));
        // On the attention board, the top bar's pill and the agents' health too.
        var item = Assert.Single(_k.Board.Items);
        Assert.Equal((AttentionLevel.Critical, "orb keeps stopping", (string?)null), (item.Level, item.Pill, item.Dismiss));
        Assert.Contains("orb (gb-orb): Gitberth stopped bringing it back.", Assert.Single(_k.Board.Problems), StringComparison.Ordinal);
        Assert.Equal("gb-orb", Assert.Single(_k.Keeper.GaveUp).Key);
        Assert.Equal(KeepState.GaveUp, _k.Keeper.StatusOf(Rig.ObsId).State);

        // Quiet until 6 hours have passed: then one try, told only if it comes back.
        await _k.PassAsync(gaveUpAt.AddHours(2));
        Assert.Equal(4, _k.Starts("gb-orb"));
        await _k.PassAsync(gaveUpAt.AddHours(6).AddSeconds(1));
        Assert.Equal(5, _k.Starts("gb-orb"));
        Assert.Empty(_k.Notices());
        Assert.Equal(KeepState.GaveUp, _k.Keeper.StatusOf(Rig.ObsId).State);
        await _k.PassAsync(gaveUpAt.AddHours(6).AddMinutes(11));
        var back = Assert.Single(_k.Notices());
        Assert.Equal((NotifyEvent.BroughtBack, NoticeLevel.Good, "orb is running again"), (back.Event, back.Level, back.Title));
        Assert.Empty(_k.Board.Items);
        Assert.Empty(_k.Keeper.GaveUp);
    }

    [Fact]
    public async Task A_give_up_ends_when_someone_starts_it_and_a_later_crash_counts_from_nothing()
    {
        await GiveUpOnOrbAsync();
        // Someone started it from outside, a minute later.
        var started = T.AddSeconds(1331).AddMinutes(1);
        Rig.Engine.SetRunning("gb-orb", started);

        await _k.PassAsync(started.AddMinutes(1));

        Assert.Empty(_k.Board.Items);
        Assert.Empty(_k.Keeper.GaveUp);
        Assert.Equal(KeepState.None, _k.Keeper.StatusOf(Rig.ObsId).State);
        Rig.Engine.SetExited("gb-orb", 3, started.AddMinutes(2));
        await _k.PassAsync(started.AddMinutes(2).AddSeconds(31));
        Assert.Equal(5, _k.Starts("gb-orb"));
        Assert.EndsWith("Try 1 of 4.", (await _k.ActivityAsync(Rig.ObsId)).Last(a => a.Kind == ActivityKind.BroughtBack).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_give_up_holds_through_deploys_that_never_touched_its_container()
    {
        await GiveUpOnOrbAsync();
        var gaveUpAt = T.AddSeconds(1331);
        // A docs-only push the build filters skip, and a deploy cancelled before it ran.
        await Rig.AddAsync(new Deployment { AppId = Rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Skipped, StartedAt = gaveUpAt.AddMinutes(1), FinishedAt = gaveUpAt.AddMinutes(2) });
        await Rig.AddAsync(new Deployment { AppId = Rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Cancelled, FinishedAt = gaveUpAt.AddMinutes(3) });

        await _k.PassAsync(gaveUpAt.AddMinutes(4));

        Assert.Equal(KeepState.GaveUp, _k.Keeper.StatusOf(Rig.ObsId).State);
        Assert.Single(_k.Board.Items);
        Assert.Equal(4, _k.Starts("gb-orb"));
    }

    [Fact]
    public async Task A_start_soon_after_a_quiet_try_is_someone_elses_and_ends_the_give_up()
    {
        await GiveUpOnOrbAsync();
        var quiet = T.AddSeconds(1331).AddHours(6).AddSeconds(1);
        // The quiet try's start, as Docker stamps it; it crashes again a minute later, and that's still the check's own.
        Rig.Engine.StartsAt = quiet;
        await _k.PassAsync(quiet);
        Assert.Equal(5, _k.Starts("gb-orb"));
        Rig.Engine.SetExited("gb-orb", 3, quiet.AddMinutes(1));
        await _k.PassAsync(quiet.AddMinutes(2));
        Assert.Equal(KeepState.GaveUp, _k.Keeper.StatusOf(Rig.ObsId).State);

        // Its owner fixes it and starts it, 5 minutes after the quiet try.
        Rig.Engine.SetRunning("gb-orb", quiet.AddMinutes(5));
        await _k.PassAsync(quiet.AddMinutes(6));

        Assert.Empty(_k.Board.Items);
        Assert.Equal(KeepState.None, _k.Keeper.StatusOf(Rig.ObsId).State);
        // Nor is it told as the check's own try having worked.
        await _k.PassAsync(quiet.AddMinutes(20));
        Assert.DoesNotContain(_k.Notices(), n => n.Event == NotifyEvent.BroughtBack);
    }

    [Fact]
    public async Task A_give_up_holds_for_the_check_after_a_restart_without_being_told_again()
    {
        await GiveUpOnOrbAsync();
        Assert.Single(_k.Notices());

        // Gitberth restarted: a new check reads what the old one kept.
        var again = _k.New();
        await again.PassAsync(T.AddSeconds(1331).AddMinutes(5));

        Assert.Equal(KeepState.GaveUp, again.StatusOf(Rig.ObsId).State);
        Assert.Equal("gb-orb", Assert.Single(again.GaveUp).Key);
        Assert.Empty(_k.Notices());
        Assert.Equal(4, _k.Starts("gb-orb"));
        Assert.Single(await _k.ActivityAsync(Rig.ObsId), a => a.Kind == ActivityKind.GaveUp);
    }

    [Fact]
    public async Task Several_apps_back_after_docker_restarted_are_told_in_one_notice()
    {
        // Gitberth's own container stopped with them, and started 40 s later.
        Rig.Engine.SetExited("gitberth", 0, T.AddSeconds(5));
        Rig.Engine.SetRunning("gitberth", T.AddSeconds(40));
        Rig.Engine.SetExited("gb-orb", 143, T, policy: "no");
        Rig.Engine.SetExited("gb-kanban", 0, T.AddSeconds(2), policy: "no");

        await _k.PassAsync(T.AddSeconds(60));
        Assert.Equal((1, 1), (_k.Starts("gb-orb"), _k.Starts("gb-kanban")));
        Assert.Equal("Gitberth started it again: it stopped when Docker or the host restarted. Try 1 of 4.", Assert.Single(await _k.ActivityAsync(Rig.ObsId)).Text);
        await _k.PassAsync(T.AddSeconds(90));

        var notice = Assert.Single(_k.Notices());
        Assert.Equal(("Gitberth started 2 apps again", ""), (notice.Title, notice.App));
        Assert.EndsWith(": orb and Kanban.", notice.Text, StringComparison.Ordinal);
    }

    // ---- Made again ----

    [Fact]
    public async Task A_git_apps_missing_container_is_made_again_from_its_image_by_one_recover_deploy()
    {
        Rig.Engine.Remove("gb-orb");

        await _k.PassAsync(T);
        Assert.Empty(await _k.DeploymentsAsync(Rig.ObsId));
        await _k.PassAsync(T.AddSeconds(31));
        var dep = Assert.Single(await _k.DeploymentsAsync(Rig.ObsId));
        Assert.Equal((DeployTrigger.Recover, DeployStatus.Queued, "gitberth/orb:1d2948c"), (dep.Trigger, dep.Status, dep.ImageTag));
        Assert.Equal($"Gitberth made its container again from gitberth/orb:1d2948c (deploy #{dep.Id}): its container was gone. Try 1 of 4.",
            Assert.Single(await _k.ActivityAsync(Rig.ObsId)).Text);
        // The deploy has the app now: nothing more is queued for it.
        await _k.PassAsync(T.AddSeconds(62));
        await _k.PassAsync(T.AddMinutes(10));
        Assert.Single(await _k.DeploymentsAsync(Rig.ObsId));
        // Its deploy is Gitberth's, through the keep-running check.
        Assert.Equal(AuditVia.KeepRunning, AuditActors.ForDeploy(dep.RequestedBy, dep.Trigger).Via);
    }

    [Fact]
    public async Task With_its_image_gone_too_the_commit_it_ran_is_built_again_on_its_own_branch()
    {
        Rig.Engine.Remove("gb-kanban");
        // Docker's 404 for the image.
        Rig.Engine.Images.Remove("gitberth/kanban:425df8a");

        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));

        var dep = Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId));
        Assert.Equal((DeployTrigger.Recover, DeployStatus.Queued, (string?)null), (dep.Trigger, dep.Status, dep.ImageTag));
        Assert.Equal($"Gitberth built it again from 425df8a (deploy #{dep.Id}): its container and its image were gone.", Assert.Single(await _k.ActivityAsync(Rig.KanbanId)).Text);

        // The build failed: a build counts as every try, so Gitberth gives up at once.
        await using (var db = Rig.Db.CreateDbContext())
            await db.Deployments.Where(d => d.Id == dep.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeployStatus.Failed).SetProperty(d => d.StartedAt, T.AddMinutes(1)).SetProperty(d => d.FinishedAt, T.AddMinutes(2)));
        await _k.PassAsync(T.AddMinutes(3));
        var notice = Assert.Single(_k.Notices());
        Assert.Equal("Its container and its image were gone, and building it again didn't work. Gitberth tries again every 6 hours; it stays down until then, or until you start or deploy it. The deploy's log on Gitberth has the details.",
            notice.Text);
        Assert.Equal(dep.Id, notice.DeploymentId);
        Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId));
    }

    [Fact]
    public async Task A_registry_apps_missing_container_is_created_again_from_its_settings_but_never_an_adopted_one()
    {
        var pg = await Rig.Catalog.AddAsync(new NewAppRow("pg", "pg", AppSource.Registry, "postgres:18", null,
            new AppSnapshot(new ContainerSettings { Image = "postgres:18" }, [new NetworkJoin("gitberth", ["pg"])]), new AppOptions { Name = "pg" }));
        var legacy = await Rig.AddAsync(new AppDefinition
        {
            Name = "legacy", Slug = "legacy", Source = AppSource.Existing, Container = "legacy-web", ContainerId = "0123456789ab", DesiredState = DesiredState.Running,
        });
        await using (var db = Rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == pg.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, "fedcba987654"));

        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        await _k.PassAsync(T.AddSeconds(62));

        Assert.Equal("gb-pg", Assert.Single(Rig.Created).Name);
        Assert.Equal("Gitberth made its container again from its saved settings: its container was gone. Try 1 of 4.", Assert.Single(await _k.ActivityAsync(pg.Id)).Text);
        // The adopted one is said once, and left.
        var note = Assert.Single(await _k.ActivityAsync(legacy));
        Assert.Equal((ActivityKind.Event, KeepText.ExistingGone), (note.Kind, note.Text));
    }

    [Fact]
    public async Task A_compose_apps_crashed_service_starts_alone()
    {
        var shop = await Rig.AddAsync(new AppDefinition { Name = "shop", Slug = "shop", Source = AppSource.Compose, CurrentImage = "gitberth/shop/web:1a2b3c4" });
        await Rig.AddAsync(new Deployment
        {
            AppId = shop, Trigger = DeployTrigger.Manual, Status = DeployStatus.Succeeded, FinishedAt = T.AddHours(-1),
            Images = new ComposeImages(new Dictionary<string, string> { ["web"] = "gitberth/shop/web:1a2b3c4", ["worker"] = "gitberth/shop/worker:1a2b3c4", ["migrate"] = "gitberth/shop/web:1a2b3c4" },
                new Dictionary<string, string>()).ToJson(),
        });
        Rig.Engine.AddLike("sample-index", "gb-shop-web-1", Service("shop", "web"));
        Rig.Engine.AddLike("sample-index", "gb-shop-worker-1", Service("shop", "worker"));
        Rig.Engine.AddLike("sample-index", "gb-shop-migrate-1", Service("shop", "migrate"));
        Rig.Engine.SetExited("gb-shop-worker-1", 1, T, policy: "no");
        // A one-shot job that ended as it should.
        Rig.Engine.SetExited("gb-shop-migrate-1", 0, T, policy: "no");

        await _k.PassAsync(T.AddSeconds(31));

        Assert.Single(Rig.Engine.Commands, c => c.StartsWith("compose", StringComparison.Ordinal) && c.Contains("-p gb-shop", StringComparison.Ordinal) && c.EndsWith(" start worker", StringComparison.Ordinal));
        Assert.Equal("Gitberth started worker again: it crashed (exit code 1), and its restart policy (no) doesn't start it again. Try 1 of 4.", Assert.Single(await _k.ActivityAsync(shop)).Text);
    }

    [Fact]
    public async Task A_compose_service_compose_never_creates_isnt_a_container_gone()
    {
        var shop = await Rig.AddAsync(new AppDefinition { Name = "shop", Slug = "shop", Source = AppSource.Compose, CurrentImage = "gitberth/shop/web:1a2b3c4" });
        // adminer is behind a profile: the release names it, but compose up never made its container, so it has no image ID.
        await Rig.AddAsync(new Deployment
        {
            AppId = shop, Trigger = DeployTrigger.Manual, Status = DeployStatus.Succeeded, FinishedAt = T.AddHours(-1),
            Images = new ComposeImages(new Dictionary<string, string> { ["web"] = "gitberth/shop/web:1a2b3c4", ["worker"] = "gitberth/shop/worker:1a2b3c4", ["adminer"] = "adminer:4" },
                new Dictionary<string, string> { ["web"] = "sha256:" + new string('a', 64), ["worker"] = "sha256:" + new string('b', 64) }).ToJson(),
        });
        Rig.Engine.AddLike("sample-index", "gb-shop-web-1", Service("shop", "web"));
        Rig.Engine.AddLike("sample-index", "gb-shop-worker-1", Service("shop", "worker"));

        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        await _k.PassAsync(T.AddMinutes(5));

        Assert.Single(await _k.DeploymentsAsync(shop));
        Assert.Equal(KeepState.None, _k.Keeper.StatusOf(shop).State);
        Assert.Empty(await _k.ActivityAsync(shop));
        // Start and start-up see every service it brought up there too.
        Assert.True(await Rig.Actions.CompleteAsync((await Rig.RowAsync(a => a.Id == shop))!));
    }

    [Fact]
    public async Task A_git_compose_apps_build_to_bring_it_back_fetches_the_commit_it_ran()
    {
        var requests = new List<DeployRequest>();
        var seen = new TaskCompletionSource();
        var queue = new DeployQueue(Rig.Pipeline, new LiveLogHub(), Rig.Db, new Running(), NullLogger<DeployQueue>.Instance)
        {
            Runner = (request, _) =>
            {
                lock (requests) requests.Add(request);
                seen.TrySetResult();
                return Task.CompletedTask;
            },
        };
        var actions = new AppActions(Rig.Db, queue, Rig.Cli, Rig.AppLocks, Rig.Options, new RepoVariables(new GitHubApi(new ImageKit.NoHttp()), Rig.Settings),
            Rig.Domains, Rig.Restarts, Rig.Apps, NullLogger<AppActions>.Instance, Rig.Catalog);
        // A Compose app from a repo, whose current release's files are gone along with its containers.
        var shop = await Rig.AddAsync(new AppDefinition
        {
            Name = "shop", Slug = "shop", Owner = "acme", Repo = "shop", Branch = "main", ComposeFile = "compose.yaml",
            CurrentImage = "gitberth/shop/web:1a2b3c4", CurrentCommit = "1a2b3c4d5e6f",
        });

        var r = await actions.BringBackAsync(new BringBackRequest(shop, BringBackKind.Recreate));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(KeepHow.Built, r.How);
        // The commit it ran, on its own branch: never the branch's newer tip.
        var request = Assert.Single(requests);
        Assert.Equal(("refs/heads/main", "1a2b3c4d5e6f"), (request.GitRef, request.Commit));
        // A Compose file pasted into Gitberth builds the file Gitberth keeps, and a preview its own ref.
        (string?, string?) none = (null, null);
        Assert.Equal(none, AppActions.RecoverSource(new AppDefinition { Source = AppSource.Compose, CurrentCommit = "1a2b3c4d5e6f" }));
        Assert.Equal(none, AppActions.RecoverSource(new AppDefinition { Owner = "acme", Repo = "shop", CurrentCommit = "1a2b3c4d5e6f", ParentAppId = 1 }));
    }

    // ---- Left alone ----

    [Fact]
    public async Task A_stop_outside_gitberth_is_left_and_said_once()
    {
        var id = (string)Rig.Engine.Inspect("gb-orb")["Id"]!;
        // docker stop: SIGTERM, which this program ended with 1.
        Rig.Events.Heard(new ContainerEvent(T.AddSeconds(-2), id, "gb-orb", "kill", Signal: 15), T.AddSeconds(-2));
        Rig.Events.Heard(new ContainerEvent(T, id, "gb-orb", "die", ExitCode: 1), T);
        Rig.Engine.SetExited("gb-orb", 1, T, policy: "no");

        await _k.PassAsync(T.AddSeconds(31));
        await _k.PassAsync(T.AddSeconds(62));

        Assert.Equal(0, _k.Starts("gb-orb"));
        var note = Assert.Single(await _k.ActivityAsync(Rig.ObsId));
        Assert.Equal("It was stopped (exit code 1), not by a crash, so Gitberth leaves it stopped. Start it to bring it back.", note.Text);
        Assert.Equal(KeepState.Left, _k.Keeper.StatusOf(Rig.ObsId).State);
        Assert.Empty(_k.Notices());
    }

    [Fact]
    public async Task A_stop_it_left_stays_left_after_a_restart_once_its_events_are_gone()
    {
        var id = (string)Rig.Engine.Inspect("gb-orb")["Id"]!;
        // docker stop: SIGTERM, which this program ended with 1.
        Rig.Events.Heard(new ContainerEvent(T.AddSeconds(-2), id, "gb-orb", "kill", Signal: 15), T.AddSeconds(-2));
        Rig.Events.Heard(new ContainerEvent(T, id, "gb-orb", "die", ExitCode: 1), T);
        Rig.Engine.SetExited("gb-orb", 1, T, policy: "no");
        await _k.PassAsync(T.AddSeconds(31));
        Assert.Equal(KeepState.Left, _k.Keeper.StatusOf(Rig.ObsId).State);

        // Gitberth restarts 8 hours later: the events it keeps (7 hours) no longer say it was a stop, and its exit code alone
        // reads as a crash. What the check read then stands.
        var again = _k.New(new ContainerEventWatcher(Rig.Api, Rig.Store, NullLogger<ContainerEventWatcher>.Instance));
        await again.PassAsync(T.AddHours(8));
        await again.PassAsync(T.AddHours(8).AddSeconds(31));

        Assert.Equal(0, _k.Starts("gb-orb"));
        Assert.Equal(KeepState.Left, again.StatusOf(Rig.ObsId).State);
        // Said once, before the restart.
        Assert.Single(await _k.ActivityAsync(Rig.ObsId));
    }

    [Fact]
    public async Task A_docker_stop_during_a_long_build_that_then_fails_is_left()
    {
        // The check hears every event from its first look on.
        await _k.PassAsync(T.AddMinutes(-10));
        // A watcher's deploy built from 5 minutes before the stop until 5 minutes after it, and failed.
        await Rig.AddAsync(new Deployment { AppId = Rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Failed, StartedAt = T.AddMinutes(-5), FinishedAt = T.AddMinutes(5) });
        // Meanwhile its owner ran docker stop, which Gitberth wasn't making.
        var id = (string)Rig.Engine.Inspect("gb-orb")["Id"]!;
        _k.Clock = T;
        Rig.Events.Heard(new ContainerEvent(T.AddSeconds(-1), id, "gb-orb", "kill", Signal: 15), T.AddSeconds(-1));
        Rig.Events.Heard(new ContainerEvent(T, id, "gb-orb", "die", ExitCode: 1), T);
        Rig.Engine.SetExited("gb-orb", 1, T, policy: "no");

        await _k.PassAsync(T.AddMinutes(5).AddSeconds(31));

        Assert.Equal(0, _k.Starts("gb-orb"));
        var status = _k.Keeper.StatusOf(Rig.ObsId);
        Assert.Equal((KeepState.Left, KeepCause.Stopped), (status.State, status.Reason?.Cause));
    }

    [Fact]
    public async Task A_deploys_own_stop_of_the_old_container_is_still_brought_back_when_the_deploy_fails()
    {
        await _k.PassAsync(T.AddMinutes(-10));
        await Rig.AddAsync(new Deployment { AppId = Rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Failed, StartedAt = T.AddMinutes(-5), FinishedAt = T.AddMinutes(1) });
        var id = (string)Rig.Engine.Inspect("gb-orb")["Id"]!;
        // The swap's docker stop, which Gitberth registered as its own while it ran.
        _k.Clock = T.AddSeconds(-2);
        using (_k.Expected.Expect("container", ExpectedChanges.Stopped, "gb-orb"))
        {
            _k.Clock = T;
            Rig.Events.Heard(new ContainerEvent(T.AddSeconds(-1), id, "gb-orb", "kill", Signal: 15), T.AddSeconds(-1));
            Rig.Events.Heard(new ContainerEvent(T, id, "gb-orb", "die", ExitCode: 143), T);
        }
        Rig.Engine.SetExited("gb-orb", 143, T, policy: "no");

        await _k.PassAsync(T.AddMinutes(1).AddSeconds(31));

        Assert.Equal(1, _k.Starts("gb-orb"));
        Assert.Equal("Gitberth started it again: a deploy stopped it and couldn't start it again. Try 1 of 4.", Assert.Single(await _k.ActivityAsync(Rig.ObsId)).Text);
    }

    [Fact]
    public async Task A_cancelled_deploy_to_bring_it_back_leaves_it_until_someone_deploys_it()
    {
        Rig.Engine.Remove("gb-kanban");
        Rig.Engine.Images.Remove("gitberth/kanban:425df8a");
        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        var dep = Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId));

        // Its owner cancels the build: their own doing, so no give-up, and no build again every 6 hours.
        await EndDeployAsync(dep.Id, DeployStatus.Cancelled, T.AddMinutes(1), T.AddMinutes(2));
        await _k.PassAsync(T.AddMinutes(3));
        await _k.PassAsync(T.AddHours(7));

        Assert.Empty(_k.Notices());
        Assert.Empty(_k.Board.Items);
        Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId));
        var status = _k.Keeper.StatusOf(Rig.KanbanId);
        Assert.Equal((KeepState.Left, KeepCause.Cancelled), (status.State, status.Reason?.Cause));
        Assert.Equal("The deploy that was to bring it back was cancelled, so Gitberth leaves it as it is. Start or deploy it to bring it back.",
            (await _k.ActivityAsync(Rig.KanbanId)).Last().Text);

        // Someone deploys it: theirs, and the check looks after it again (its container is still gone).
        await Rig.AddAsync(new Deployment { AppId = Rig.KanbanId, Trigger = DeployTrigger.Manual, Status = DeployStatus.Failed, StartedAt = T.AddHours(7).AddMinutes(1), FinishedAt = T.AddHours(7).AddMinutes(2) });
        await _k.PassAsync(T.AddHours(7).AddMinutes(3));
        Assert.NotEqual(KeepState.Left, _k.Keeper.StatusOf(Rig.KanbanId).State);
        Assert.Equal(2, (await _k.DeploymentsAsync(Rig.KanbanId)).Count(d => d.Trigger == DeployTrigger.Recover));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(143)]
    public async Task An_exit_with_0_or_a_stops_code_is_left(int code)
    {
        Rig.Engine.SetExited("gb-orb", code, T, policy: "no");

        await _k.PassAsync(T.AddMinutes(1));

        Assert.Equal(0, _k.Starts("gb-orb"));
        Assert.Equal(KeepState.Left, _k.Keeper.StatusOf(Rig.ObsId).State);
    }

    [Fact]
    public async Task An_app_stopped_in_gitberth_is_never_touched()
    {
        await using (var db = Rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == Rig.ObsId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        Rig.Engine.Remove("gb-kanban");
        await using (var db = Rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == Rig.KanbanId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));

        await _k.PassAsync(T.AddSeconds(31));
        await _k.PassAsync(T.AddSeconds(62));

        Assert.Equal(0, _k.Starts("gb-orb"));
        Assert.Empty(await _k.DeploymentsAsync(Rig.KanbanId));
        Assert.Empty(await _k.ActivityAsync(Rig.ObsId));
        Assert.Equal(KeepState.None, _k.Keeper.StatusOf(Rig.ObsId).State);
    }

    [Fact]
    public async Task Turned_off_it_does_nothing_and_says_so()
    {
        await _k.Switch.SetAsync(false);
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");

        await _k.PassAsync(T.AddMinutes(1));

        Assert.Equal(0, _k.Starts("gb-orb"));
        Assert.False(_k.Keeper.On);
        Assert.Equal(KeepState.Off, _k.Keeper.StatusOf(Rig.ObsId).State);
        var entry = Assert.Single(_k.Audit.Entries, e => e.Action == "settings.keep.changed");
        Assert.Equal("Gitberth no longer brings apps back by themselves.", entry.Summary);

        await _k.Switch.SetAsync(true);
        await _k.PassAsync(T.AddMinutes(2));
        Assert.Equal(1, _k.Starts("gb-orb"));
    }

    // ---- Busy ----

    [Fact]
    public async Task A_deploy_a_change_or_a_held_lock_has_the_app_and_waiting_for_it_isnt_a_try()
    {
        await Rig.AddAsync(new Deployment { AppId = Rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Queued });
        await Rig.AddAsync(new ContainerChangeEntry { ContainerName = "gb-kanban", Summary = "ports", OldId = "x", OldName = "gb-kanban", TempName = "gb-kanban-new", Outcome = ChangeOutcome.NeedsDecision });
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        Rig.Engine.SetExited("gb-kanban", 3, T, policy: "no");

        await _k.PassAsync(T.AddMinutes(1));
        Assert.Equal((0, 0), (_k.Starts("gb-orb"), _k.Starts("gb-kanban")));
        Assert.Empty(await _k.ActivityAsync(Rig.ObsId));
        Assert.Empty(await _k.ActivityAsync(Rig.KanbanId));

        // The deploy is over and the change settled, but a stop of orb's holds its lock.
        await using (var db = Rig.Db.CreateDbContext())
        {
            await db.Deployments.ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeployStatus.Cancelled).SetProperty(d => d.FinishedAt, T));
            await db.ContainerChanges.ExecuteUpdateAsync(s => s.SetProperty(c => c.Outcome, ChangeOutcome.Done));
        }
        using (await Rig.AppLocks.AcquireAsync(Rig.ObsId))
            await _k.PassAsync(T.AddMinutes(2));
        Assert.Equal((0, 1), (_k.Starts("gb-orb"), _k.Starts("gb-kanban")));
        Assert.Empty(await _k.ActivityAsync(Rig.ObsId));

        await _k.PassAsync(T.AddMinutes(3));
        Assert.Equal(1, _k.Starts("gb-orb"));
        Assert.EndsWith("Try 1 of 4.", Assert.Single(await _k.ActivityAsync(Rig.ObsId)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_not_answering_is_never_a_container_gone()
    {
        await _k.PassAsync(T.AddMinutes(-1));
        Rig.Engine.Remove("gb-orb");
        Rig.Engine.SetExited("gb-kanban", 3, T, policy: "no");
        _k.Engine.Down = true;

        await _k.PassAsync(T);
        await _k.PassAsync(T.AddMinutes(1));
        await _k.PassAsync(T.AddMinutes(2));
        Assert.Empty(await _k.DeploymentsAsync(Rig.ObsId));
        Assert.Equal(0, _k.Starts("gb-kanban"));

        // It answers, but not about kanban: that pass stops there. orb's first look counts; a second is still needed.
        _k.Engine.Down = false;
        _k.Engine.InspectFails = true;
        await _k.PassAsync(T.AddMinutes(3));
        Assert.Equal(0, _k.Starts("gb-kanban"));
        Assert.Empty(await _k.DeploymentsAsync(Rig.ObsId));
        _k.Engine.InspectFails = false;
        await _k.PassAsync(T.AddMinutes(3).AddSeconds(31));
        Assert.Single(await _k.DeploymentsAsync(Rig.ObsId));
        Assert.Equal(1, _k.Starts("gb-kanban"));
    }

    [Fact]
    public async Task Docker_not_answering_about_an_image_isnt_its_being_gone()
    {
        Rig.Engine.Remove("gb-orb");
        _k.Engine.ImageInspectFails = true;
        Rig.Engine.Failing["image inspect"] = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?";
        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        // No build of it in its image's place, and no try counted.
        Assert.Empty(await _k.DeploymentsAsync(Rig.ObsId));
        Assert.Empty(await _k.ActivityAsync(Rig.ObsId));

        _k.Engine.ImageInspectFails = false;
        Rig.Engine.Failing.Remove("image inspect");
        await _k.PassAsync(T.AddSeconds(62));
        Assert.Equal("gitberth/orb:1d2948c", Assert.Single(await _k.DeploymentsAsync(Rig.ObsId)).ImageTag);
        Assert.EndsWith("Try 1 of 4.", Assert.Single(await _k.ActivityAsync(Rig.ObsId)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_try_counts_and_the_other_apps_are_looked_at_while_activity_cant_be_written()
    {
        await using (var db = Rig.Db.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER no_room BEFORE INSERT ON ActivityEntries BEGIN SELECT RAISE(ABORT, 'database or disk is full'); END;");
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        Rig.Engine.SetExited("gb-kanban", 3, T, policy: "no");

        await _k.PassAsync(T.AddSeconds(31));
        // Both tried, though neither try could be written down.
        Assert.Equal((1, 1), (_k.Starts("gb-orb"), _k.Starts("gb-kanban")));
        Assert.Equal(2, _k.Audit.Entries.Count(e => e.Action == "app.recovered"));

        // orb crashes again at once: its next try waits its minute, as the first one counted.
        Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(40));
        await _k.PassAsync(T.AddSeconds(71));
        Assert.Equal(1, _k.Starts("gb-orb"));
        await _k.PassAsync(T.AddSeconds(92));
        Assert.Equal(2, _k.Starts("gb-orb"));
    }

    [Fact]
    public async Task A_build_a_restart_of_Gitberth_cut_short_isnt_a_failed_one()
    {
        Rig.Engine.Remove("gb-kanban");
        Rig.Engine.Images.Remove("gitberth/kanban:425df8a");
        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        var dep = Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId));

        // Gitberth restarted mid-build: start-up marks it so, and queues a build of its own for the missing container.
        await EndDeployAsync(dep.Id, DeployStatus.Failed, T.AddMinutes(1), T.AddMinutes(2), "Gitberth restarted during this deploy.");
        await Rig.AddAsync(new Deployment { AppId = Rig.KanbanId, Trigger = DeployTrigger.Start, Status = DeployStatus.Building, StartedAt = T.AddMinutes(2) });
        var again = _k.New();
        await again.PassAsync(T.AddMinutes(3));

        Assert.Empty(_k.Notices());
        Assert.Empty(_k.Board.Items);
        Assert.NotEqual(KeepState.GaveUp, again.StatusOf(Rig.KanbanId).State);
    }

    [Fact]
    public async Task A_stop_while_its_container_is_made_again_stays()
    {
        Rig.Engine.Remove("gb-orb");
        await _k.PassAsync(T);
        // Its owner stops it while the check asks Docker about its container.
        var stopped = 0;
        Rig.Engine.BeforeCommand = async line =>
        {
            if (!line.StartsWith("container inspect", StringComparison.Ordinal) || Interlocked.Exchange(ref stopped, 1) == 1) return;
            await using var db = Rig.Db.CreateDbContext();
            await db.Apps.Where(a => a.Id == Rig.ObsId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        };

        await _k.PassAsync(T.AddSeconds(31));

        Assert.Equal(1, stopped);
        Assert.Equal(DesiredState.Stopped, (await Rig.RowAsync(a => a.Id == Rig.ObsId))!.DesiredState);
        Assert.Empty(await _k.DeploymentsAsync(Rig.ObsId));
    }

    [Fact]
    public async Task A_stop_while_a_registry_apps_container_is_started_again_stays()
    {
        var web = await Rig.Catalog.AddAsync(new NewAppRow("web", "web", AppSource.Registry, "nginx:1.27", null,
            new AppSnapshot(new ContainerSettings { Image = "nginx:1.27" }, [new NetworkJoin("gitberth", ["web"])]), new AppOptions { Name = "web" }));
        Rig.Engine.AddLike("sample-index", "gb-web");
        Rig.Engine.SetExited("gb-web", 3, T, policy: "no");
        // Its owner stops it while the check looks at its container, before the start takes the container's lock.
        var stopped = 0;
        Rig.Engine.BeforeCommand = async line =>
        {
            if (line != "container inspect gb-web" || Interlocked.Exchange(ref stopped, 1) == 1) return;
            await using var db = Rig.Db.CreateDbContext();
            await db.Apps.Where(a => a.Id == web.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        };

        await _k.PassAsync(T.AddSeconds(31));

        Assert.Equal(1, stopped);
        Assert.Equal(0, _k.Starts("gb-web"));
        Assert.Empty(await _k.ActivityAsync(web.Id));
    }

    [Fact]
    public async Task A_forgotten_apps_container_isnt_made_again()
    {
        var pg = await Rig.Catalog.AddAsync(new NewAppRow("pg", "pg", AppSource.Registry, "postgres:18", null,
            new AppSnapshot(new ContainerSettings { Image = "postgres:18" }, [new NetworkJoin("gitberth", ["pg"])]), new AppOptions { Name = "pg" }));
        await using (var db = Rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == pg.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContainerId, "fedcba987654"));
        await _k.PassAsync(T);
        await _k.PassAsync(T.AddSeconds(31));
        var create = Assert.Single(Rig.Created);
        // The change engine asks again under the container's lock, right before it makes anything: still wanted.
        Assert.Null(await create.Wanted!(CancellationToken.None));

        // Forgotten meanwhile: nothing is made for a record that's gone.
        await Rig.Actions.ForgetAsync(pg.Id);
        Assert.Equal("pg was stopped, deleted or forgotten meanwhile.", await create.Wanted!(CancellationToken.None));
        Assert.False(Rig.Actions.IsDeleting(pg.Id));
    }

    [Fact]
    public async Task Docker_slow_to_answer_never_holds_up_another_apps_deploy()
    {
        Rig.Engine.Remove("gb-orb");
        await _k.PassAsync(T);
        // Docker takes its time over the check's first question about orb's container.
        var asked = new TaskCompletionSource();
        var answer = new TaskCompletionSource();
        Rig.Engine.BeforeCommand = async line =>
        {
            if (line.StartsWith("container inspect", StringComparison.Ordinal) && asked.TrySetResult()) await answer.Task;
        };
        var pass = _k.PassAsync(T.AddSeconds(31));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Meanwhile a person deploys kanban: the deploy gate isn't the check's while it waits for Docker.
        var deployed = await Rig.Actions.DeployAsync(Rig.KanbanId).WaitAsync(TimeSpan.FromSeconds(30));

        answer.SetResult();
        await pass;
        Assert.Equal(deployed, Assert.Single(await _k.DeploymentsAsync(Rig.KanbanId)).Id);
        Assert.Single(await _k.DeploymentsAsync(Rig.ObsId));
    }

    // ---- Told ----

    [Fact]
    public async Task A_muted_app_is_brought_back_without_a_notice()
    {
        await _k.Choices.SetMutedAsync("gb-orb", true);
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");

        await _k.PassAsync(T.AddSeconds(31));
        await _k.PassAsync(T.AddSeconds(61));

        Assert.Equal(1, _k.Starts("gb-orb"));
        Assert.Empty(_k.Notices());
    }

    [Fact]
    public async Task What_was_brought_back_lately_is_counted_for_settings()
    {
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        await _k.PassAsync(T.AddSeconds(31));

        var (times, apps) = await _k.Keeper.RecentAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.Equal((1, "orb"), (times, Assert.Single(apps)));
    }

    [Theory]
    [InlineData(DeployStatus.Succeeded)]
    [InlineData(DeployStatus.Failed)]
    [InlineData(DeployStatus.RolledBack)]
    public void A_recover_deploy_is_told_by_the_check_itself_however_it_ends(DeployStatus status)
    {
        // Each try's failure isn't told: the check's own notices say it came back, or that it gave up.
        Assert.Null(DeployNotifier.EventFor(new DeployReport(1, 5, DeployTrigger.Recover, "abc1234def", status, status == DeployStatus.Succeeded ? null : "The new container didn't start")));
        // Another deploy's failure still is.
        Assert.Equal(status == DeployStatus.Succeeded ? NotifyEvent.DeploySucceeded : NotifyEvent.DeployFailed,
            DeployNotifier.EventFor(new DeployReport(1, 5, DeployTrigger.Webhook, "abc1234def", status, null)));
    }

    [Fact]
    public async Task A_recover_build_posts_no_commit_status_on_the_commit_it_ran()
    {
        var github = new GitHubStand();
        var reporter = new GitHubReporter(Rig.Db, Rig.Settings, new GitHubWrites(github), new GitHubIntegrationState(), Rig.Options, NullLogger<GitHubReporter>.Instance);
        await Rig.Settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_test");

        await reporter.StartedAsync(new DeployReport(Rig.ObsId, 7, DeployTrigger.Recover, "1d2948cc693a", DeployStatus.Building, null), CancellationToken.None);
        await reporter.FinishedAsync(new DeployReport(Rig.ObsId, 7, DeployTrigger.Recover, "1d2948cc693a", DeployStatus.Failed, "npm ci exited with 1"), CancellationToken.None);
        Assert.Empty(github.Calls);

        // A deploy someone pushed still does.
        await reporter.StartedAsync(new DeployReport(Rig.ObsId, 8, DeployTrigger.Webhook, "1d2948cc693a", DeployStatus.Building, null), CancellationToken.None);
        Assert.Equal("POST /repos/acme/orb/statuses/1d2948cc693a", Assert.Single(github.Calls));
    }

    [Fact]
    public void A_recover_deploy_reads_as_brought_back() =>
        Assert.Equal("Brought back", Format.Trigger(DeployTrigger.Recover));

    // orb crashes each time it's started until Gitberth gives up on it, 1331 s after T.
    private async Task GiveUpOnOrbAsync()
    {
        var times = new[] { (Exit: 0, Pass: 31), (Exit: 40, Pass: 92), (Exit: 100, Pass: 393), (Exit: 400, Pass: 1294), (Exit: 1300, Pass: 1331) };
        Rig.Engine.SetExited("gb-orb", 3, T, policy: "no");
        foreach (var (exit, pass) in times)
        {
            Rig.Engine.SetExited("gb-orb", 3, T.AddSeconds(exit));
            await _k.PassAsync(T.AddSeconds(pass));
        }
        Assert.Equal(KeepState.GaveUp, _k.Keeper.StatusOf(Rig.ObsId).State);
    }

    // A deploy as the pipeline ends it.
    private async Task EndDeployAsync(int id, DeployStatus status, DateTime started, DateTime finished, string? error = null)
    {
        await using var db = Rig.Db.CreateDbContext();
        await db.Deployments.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.StartedAt, started)
            .SetProperty(d => d.FinishedAt, finished).SetProperty(d => d.Error, error));
    }

    private static Dictionary<string, string> Service(string app, string service) => new()
    {
        ["gitberth.managed"] = "true", ["gitberth.app"] = app, [ComposeNames.ServiceLabel] = service,
    };

    /// <summary>Gitberth runs on: the deploy queue takes each deploy off and hands it to its runner.</summary>
    private sealed class Running : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>GitHub's stand-in: every call is recorded and answered 201 {"id":1}; nothing reaches GitHub.</summary>
    private sealed class GitHubStand : HttpMessageHandler, IHttpClientFactory
    {
        private readonly List<string> _calls = [];

        public List<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_calls) _calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":1}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>
/// AppsRig with the keep-running check on it: its notices (one destination, so they're queued), its attention board and
/// audit log in reach, what Gitberth itself is changing in Docker (on a clock of the test's), the apps' last images on the
/// host, and an engine that can stop answering.
/// </summary>
internal sealed class KeeperRig : IDisposable
{
    public KeeperRig()
    {
        Engine = new SwitchEngine(new DockerEngine(Rig.Api));
        Expected = new ExpectedChanges { Now = () => Clock };
        Choices = new NotificationSettings(Rig.Settings);
        Notifier = new Notifier(Choices, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), Rig.Settings, NullLogger<Notifier>.Instance);
        Switch = new KeeperSettings(Rig.Settings, Audit);
        // orb's and kanban's last images are on this host, as the Engine API answers for them.
        Rig.Engine.Images["gitberth/orb:1d2948c"] = new JsonObject { ["Id"] = "sha256:" + new string('1', 64) };
        Rig.Engine.Images["gitberth/kanban:425df8a"] = new JsonObject { ["Id"] = "sha256:" + new string('4', 64) };
        Keeper = New();
    }

    public AppsRig Rig { get; } = new();
    public SwitchEngine Engine { get; }

    /// <summary>What Gitberth itself is changing in Docker, by <see cref="Clock"/>.</summary>
    public ExpectedChanges Expected { get; }

    /// <summary>The time <see cref="Expected"/> goes by: the morning before the tests' own times, unless a test moves it.</summary>
    public DateTime Clock { get; set; } = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    public AttentionBoard Board { get; } = new();
    public AuditLog Audit { get; } = new();
    public NotificationSettings Choices { get; }
    public Notifier Notifier { get; }
    public KeeperSettings Switch { get; }
    public AppKeeper Keeper { get; }

    /// <summary>A destination, so notices are queued rather than dropped as unwanted.</summary>
    public Task TellAsync() => Choices.SaveDestinationAsync(new Destination { Id = "d1", Kind = DestinationKind.Webhook, Name = "Ops", Url = "https://hooks.example.com/gitberth" });

    /// <summary>Another check on the same database and Docker: Gitberth after a restart, with the events its watcher kept (<paramref name="events"/>).</summary>
    public AppKeeper New(ContainerEventWatcher? events = null) => new(Rig.Db, Engine, Rig.Actions, Rig.ContainerLocks, events ?? Rig.Events, Notifier, Rig.Activity,
        Rig.Settings, Switch, Board, Rig.Options, NullLogger<AppKeeper>.Instance, Audit, expected: Expected);

    public Task<KeepPass> PassAsync(DateTime at) => Keeper.PassAsync(at);

    /// <summary>The notices queued since the last call.</summary>
    public List<Notice> Notices()
    {
        var list = new List<Notice>();
        while (Notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    /// <summary>How often `docker start` ran for the container.</summary>
    public int Starts(string name) => Rig.Engine.Writes.Count(w => w == "docker start " + name);

    public async Task<List<ActivityEntry>> ActivityAsync(int appId)
    {
        await using var db = Rig.Db.CreateDbContext();
        return await db.Activity.AsNoTracking().Where(a => a.AppId == appId).OrderBy(a => a.Id).ToListAsync();
    }

    public async Task<List<Deployment>> DeploymentsAsync(int appId)
    {
        await using var db = Rig.Db.CreateDbContext();
        return await db.Deployments.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.Id).ToListAsync();
    }

    public void Dispose()
    {
        Audit.Dispose();
        Rig.Dispose();
    }
}

/// <summary>The rig's engine, which can stop answering: every list and inspect fails, as when Docker restarts.</summary>
internal sealed class SwitchEngine(IContainerEngine inner) : IContainerEngine
{
    /// <summary>Listing the containers fails.</summary>
    public bool Down { get; set; }

    /// <summary>Inspecting a container fails.</summary>
    public bool InspectFails { get; set; }

    /// <summary>Inspecting an image fails (Docker doesn't answer: it isn't a 404).</summary>
    public bool ImageInspectFails { get; set; }

    private static EngineException NoAnswer() => new(0, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?");

    public Task<EngineVersion> VersionAsync(CancellationToken ct) => inner.VersionAsync(ct);
    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => Down || InspectFails ? throw NoAnswer() : inner.InspectContainerAsync(idOrName, ct);
    public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) => Down || ImageInspectFails ? throw NoAnswer() : inner.InspectImageAsync(nameOrId, ct);
    public Task<JsonArray> ListContainersAsync(CancellationToken ct) => Down ? throw NoAnswer() : inner.ListContainersAsync(ct);
    public Task<JsonArray> ListNetworksAsync(CancellationToken ct) => inner.ListNetworksAsync(ct);
    public Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct) => inner.CreateAsync(name, body, ct);
    public Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct) => inner.UpdateRestartPolicyAsync(id, policy, ct);
    public Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct) => inner.UpdateAsync(id, resources, ct);
    public Task StopAsync(string id, int timeoutSeconds, CancellationToken ct) => inner.StopAsync(id, timeoutSeconds, ct);
    public Task RenameAsync(string id, string name, CancellationToken ct) => inner.RenameAsync(id, name, ct);
    public Task StartAsync(string id, CancellationToken ct) => inner.StartAsync(id, ct);
    public Task<int> WaitAsync(string id, CancellationToken ct) => inner.WaitAsync(id, ct);
    public Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct) => inner.LogsAsync(id, tail, ct);
    public Task<JsonArray> ChangesAsync(string id, CancellationToken ct) => inner.ChangesAsync(id, ct);
    public Task RemoveAsync(string id, bool force, CancellationToken ct) => inner.RemoveAsync(id, force, ct);
    public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct) => inner.CreateNetworkAsync(body, ct);
    public Task RemoveNetworkAsync(string idOrName, CancellationToken ct) => inner.RemoveNetworkAsync(idOrName, ct);
}
