using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Disk;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The disk cleanup: Docker's disk's two lines, when each run is due, what each asks of the build cache (an engine that
/// answers in memory), which images it removes and which it never touches (an image engine in memory, and AppsRig for an
/// app's own tags), the leftovers of deleted apps, what it tells, what the dashboard shows, and what the audit log keeps.
/// Every look is made by hand at the time a test says, so no test waits.
/// </summary>
public sealed class DiskCleanupTests : IAsyncLifetime
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;
    private static readonly DateTime T = new(2026, 11, 20, 4, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<DiskTier, DateTime> NotWaiting = [];
    private static readonly DiskSpace Plenty = new(40 * GiB, 64 * GiB);
    private static readonly DiskSpace Low = new(12 * GiB, 64 * GiB);
    private static readonly DiskSpace Full = new(3 * GiB, 64 * GiB);
    private static readonly DiskSpace Unread = new(long.MaxValue / 4, long.MaxValue / 4);

    private readonly DiskRig _d = new(T);

    public async Task InitializeAsync() => await _d.TellAsync();

    public Task DisposeAsync()
    {
        _d.Dispose();
        return Task.CompletedTask;
    }

    // ---- The two lines ----

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void The_warning_always_comes_before_pulls_stop(int gib)
    {
        var disk = new DiskSpace(0, gib * GiB);

        Assert.True(disk.WarnBelow >= 2 * disk.RefuseBelow, $"{gib} GiB: warns under {disk.WarnBelow}, refuses under {disk.RefuseBelow}");
    }

    [Fact]
    public void The_lines_of_a_64_GiB_disk_and_a_terabyte_and_room_again_well_above_the_line()
    {
        var install = new DiskSpace(40 * GiB, 64 * GiB);
        Assert.Equal((15 * GiB, 5 * GiB), (install.WarnBelow, install.RefuseBelow));
        var terabyte = new DiskSpace(0, 1024 * GiB);
        Assert.Equal((2 * (1024 * GiB / 20), 1024 * GiB / 20), (terabyte.WarnBelow, terabyte.RefuseBelow));
        Assert.Equal((10 * GiB, 5 * GiB), (new DiskSpace(0, 32 * GiB).WarnBelow, new DiskSpace(0, 32 * GiB).RefuseBelow));
        Assert.Equal("Gitberth warns under 15.0 GB free and stops pulling images under 5.0 GB.", DiskText.Lines(install));

        Assert.Equal((DiskLevel.Ok, DiskLevel.Low, DiskLevel.TooLow), (Plenty.Level, Low.Level, Full.Level));
        // 15 GiB and 1.28 GiB more before it's fine again.
        Assert.False(new DiskSpace(16 * GiB, 64 * GiB).RoomAgain);
        Assert.True(new DiskSpace(17 * GiB, 64 * GiB).RoomAgain);
        Assert.False(Unread.Known);
        Assert.False(Unread.Low);
    }

    // ---- When it runs ----

    [Fact]
    public void The_daily_run_comes_once_a_day_never_in_the_first_15_minutes_and_not_while_it_waits()
    {
        var state = new DiskState();

        Assert.Null(DiskRules.Due(T.AddMinutes(14), T, Plenty, state, true, NotWaiting));
        Assert.Equal(DiskTier.Routine, DiskRules.Due(T.AddMinutes(15), T, Plenty, state, true, NotWaiting));
        state.Last[DiskTier.Routine] = new DiskRun(T.AddMinutes(15), 0, Plenty.FreeBytes, "Gitberth");
        Assert.Null(DiskRules.Due(T.AddHours(24), T, Plenty, state, true, NotWaiting));
        Assert.Equal(DiskTier.Routine, DiskRules.Due(T.AddMinutes(15).AddHours(24), T, Plenty, state, true, NotWaiting));
        // Waiting for a deploy or a build: not before its time.
        var waits = new Dictionary<DiskTier, DateTime> { [DiskTier.Routine] = T.AddDays(2).AddMinutes(10) };
        Assert.Null(DiskRules.Due(T.AddDays(2), T, Plenty, state, true, waits));
        // Off: nothing runs by itself, however full the disk.
        Assert.Null(DiskRules.Due(T.AddDays(3), T, Full, state, false, NotWaiting));
    }

    [Fact]
    public void A_low_disk_runs_hourly_an_almost_full_one_every_30_minutes_and_one_that_freed_little_rests()
    {
        var state = new DiskState { Last = { [DiskTier.Routine] = new DiskRun(T, 0, 0, "Gitberth") } };

        Assert.Equal(DiskTier.Low, DiskRules.Due(T.AddMinutes(1), T, Low, state, true, NotWaiting));
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T.AddMinutes(1), T, Full, state, true, NotWaiting));
        state.Last[DiskTier.Low] = new DiskRun(T, 2 * GiB, 12 * GiB, "Gitberth");
        Assert.Null(DiskRules.Due(T.AddMinutes(59), T, Low, state, true, NotWaiting));
        Assert.Equal(DiskTier.Low, DiskRules.Due(T.AddMinutes(60), T, Low, state, true, NotWaiting));
        state.Last[DiskTier.Critical] = new DiskRun(T, 1 * GiB, 3 * GiB, "Gitberth");
        Assert.Null(DiskRules.Due(T.AddMinutes(29), T, Full, state, true, NotWaiting));
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T.AddMinutes(30), T, Full, state, true, NotWaiting));

        // Under 256 MiB freed: it rests 6 hours...
        state.Last[DiskTier.Critical] = new DiskRun(T, 100 * MiB, 3 * GiB, "Gitberth");
        Assert.Null(DiskRules.Due(T.AddHours(5), T, Full, state, true, NotWaiting));
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T.AddHours(6), T, Full, state, true, NotWaiting));
        // ...unless the disk filled up by another 2 GiB since.
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T.AddHours(1), T, new DiskSpace(1 * GiB - 1, 64 * GiB), state, true, NotWaiting));
        // A disk that couldn't be read gets no low runs.
        Assert.Null(DiskRules.Due(T.AddHours(1), T, Unread, state, true, NotWaiting));
    }

    [Fact]
    public void A_run_docker_failed_never_rests()
    {
        // A nearly full disk's run that Docker failed or refused freed nothing: that says nothing of what's left to free.
        var failed = new DiskRun(T, 0, Full.FreeBytes, "Gitberth", Failed: true);
        Assert.False(DiskRules.Rests(failed, T.AddMinutes(3), Full));
        var state = new DiskState { Last = { [DiskTier.Routine] = new DiskRun(T, 0, 0, "Gitberth"), [DiskTier.Critical] = failed } };
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T.AddMinutes(3), T, Full, state, true, NotWaiting));
        // Its retry waits as a run that waited does.
        Assert.Null(DiskRules.Due(T.AddMinutes(1), T, Full, state, true, new Dictionary<DiskTier, DateTime> { [DiskTier.Critical] = T.AddMinutes(2) }));
        // A daily run Docker failed comes back too, rather than in a day.
        state.Last[DiskTier.Routine] = new DiskRun(T, 0, 0, "Gitberth", Failed: true);
        Assert.Equal(DiskTier.Routine, DiskRules.Due(T.AddMinutes(30), T.AddDays(-1), Plenty, state, true, NotWaiting));
    }

    [Fact]
    public void A_time_kept_after_now_counts_as_long_ago()
    {
        // The clock ran 3 hours ahead when these were kept, and was set right since: they're long ago, not just now.
        var ahead = new DiskState
        {
            Last = { [DiskTier.Routine] = new DiskRun(T.AddHours(3), 0, 0, "Gitberth"), [DiskTier.Critical] = new DiskRun(T.AddHours(3), 100 * MiB, Full.FreeBytes, "Gitberth") },
        };
        Assert.Equal(DiskTier.Critical, DiskRules.Due(T, T.AddDays(-1), Full, ahead, true, NotWaiting));
        Assert.Equal(DiskTier.Routine, DiskRules.Due(T, T.AddDays(-1), Plenty, ahead, true, NotWaiting));
        Assert.False(DiskRules.Rests(ahead.Last[DiskTier.Critical], T, Full));
        Assert.Equal(DiskNotice.Low, DiskRules.Tell(DiskLevel.Ok, T.AddHours(3), Low, T));
    }

    [Fact]
    public async Task A_run_docker_refused_on_a_nearly_full_disk_is_tried_again_in_2_minutes()
    {
        _d.Disk = Full;
        _d.Cache.Bytes = 5 * GiB;
        _d.Cache.Refuse = HttpStatusCode.InternalServerError;

        var refused = await _d.Cleanup.LookAsync(T.AddMinutes(3));
        Assert.Equal(DiskTier.Critical, refused.Tier);
        Assert.NotNull(refused.Ran!.Problem);
        Assert.StartsWith($"Today {Clock(T.AddMinutes(3))}, by Gitberth: Docker didn't clean up (", DiskText.Last(refused.Ran, T.AddMinutes(3)), StringComparison.Ordinal);

        // Docker answers again: the run comes back 2 minutes later, not 6 hours.
        _d.Cache.Refuse = null;
        _d.Cache.Posts.Clear();
        Assert.Null((await _d.Cleanup.LookAsync(T.AddMinutes(4))).Tier);
        var again = await _d.Cleanup.LookAsync(T.AddMinutes(5));
        Assert.Equal(DiskTier.Critical, again.Tier);
        Assert.Equal(["POST /v1.56/build/prune"], _d.Cache.Posts);
        Assert.Null(again.Ran!.Problem);
    }

    [Fact]
    public async Task A_run_that_fails_on_an_answer_it_cant_read_waits_before_it_runs_again()
    {
        _d.Disk = Full;
        _d.Cache.Bytes = 5 * GiB;
        _d.Cache.Garbled = true;

        var first = await _d.Cleanup.LookAsync(T.AddMinutes(3));
        Assert.NotNull(first.Ran!.Problem);
        // Not at the next look, a minute later: 2 minutes after it.
        Assert.Null((await _d.Cleanup.LookAsync(T.AddMinutes(4))).Tier);
        _d.Cache.Garbled = false;
        Assert.Equal(DiskTier.Critical, (await _d.Cleanup.LookAsync(T.AddMinutes(5))).Tier);
        Assert.Equal(["POST /v1.56/build/prune"], _d.Cache.Posts);
    }

    [Fact]
    public void How_much_of_the_cache_each_run_takes_and_its_cap()
    {
        Assert.Equal(TimeSpan.FromHours(168), DiskRules.Until(DiskTier.Routine, 7));
        Assert.Equal(TimeSpan.FromHours(24), DiskRules.Until(DiskTier.Low, 7));
        Assert.Null(DiskRules.Until(DiskTier.Critical, 7));
        Assert.Equal([TimeSpan.FromHours(72), TimeSpan.FromHours(24), TimeSpan.FromHours(6)], DiskRules.CapSteps(DiskTier.Routine, 7));
        Assert.Equal([TimeSpan.FromHours(24), TimeSpan.FromHours(6)], DiskRules.CapSteps(DiskTier.Routine, 2));
        Assert.Equal([TimeSpan.FromHours(6), TimeSpan.FromHours(1)], DiskRules.CapSteps(DiskTier.Low, 7));
        Assert.Empty(DiskRules.CapSteps(DiskTier.Critical, 7));
        Assert.Equal((2 * GiB, 64 * GiB / 10, 20 * GiB), (DiskRules.CacheCap(8 * GiB), DiskRules.CacheCap(64 * GiB), DiskRules.CacheCap(1024 * GiB)));
        // The filter as every engine since API 1.39 reads it, URL-encoded JSON; never all=true.
        Assert.Equal("build/prune?filters=%7B%22until%22%3A%5B%22168h%22%5D%7D", DockerApi.PrunePath(TimeSpan.FromDays(7)));
        Assert.Equal("build/prune", DockerApi.PrunePath(null));
        Assert.Equal((7, 7, 7, 14), (DiskRules.KeepDays(null), DiskRules.KeepDays("0"), DiskRules.KeepDays("abc"), DiskRules.KeepDays("14")));
    }

    // ---- What a run asks of the build cache ----

    [Fact]
    public async Task The_daily_run_prunes_by_age_then_steps_down_to_its_cap_and_never_asks_for_everything()
    {
        _d.Disk = Plenty;
        _d.Cache.Bytes = 12 * GiB;
        _d.Cache.Frees["168h"] = 2 * GiB;
        _d.Cache.Frees["72h"] = 2 * GiB;
        _d.Cache.Frees["24h"] = 3 * GiB;
        _d.Cache.Frees["6h"] = 4 * GiB;

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal(DiskTier.Routine, look.Tier);
        // 10 GiB after the age prune, 8 after 72 h, 5 after 24 h: under the cap of 6.4 GiB, so no 6 h.
        Assert.Equal([Prune("168h"), Prune("72h"), Prune("24h")], _d.Cache.Posts);
        Assert.DoesNotContain(_d.Cache.Calls, c => c.Contains("all=true", StringComparison.Ordinal));
        Assert.Equal((7 * GiB, (long?)12 * GiB, (long?)5 * GiB), (look.Ran!.CacheFreed, look.Ran.CacheBefore, look.Ran.CacheAfter));
    }

    [Fact]
    public async Task A_low_disk_clears_what_no_build_used_for_a_day_and_an_almost_full_one_all_the_unused_cache()
    {
        _d.Disk = Low;
        _d.Cache.Bytes = 9 * GiB;
        _d.Cache.Frees["24h"] = 1 * GiB;
        _d.Cache.Frees["6h"] = 1 * GiB;
        _d.Cache.Frees["1h"] = 1 * GiB;

        var low = await _d.Cleanup.LookAsync(T.AddMinutes(3));

        Assert.Equal(DiskTier.Low, low.Tier);
        Assert.Equal([Prune("24h"), Prune("6h"), Prune("1h")], _d.Cache.Posts);

        _d.Cache.Posts.Clear();
        _d.Disk = Full;
        var full = await _d.Cleanup.LookAsync(T.AddMinutes(4));

        Assert.Equal(DiskTier.Critical, full.Tier);
        Assert.Equal(["POST /v1.56/build/prune"], _d.Cache.Posts);
        Assert.Equal(6 * GiB, full.Ran!.CacheFreed);
    }

    [Fact]
    public async Task While_a_build_holds_the_gate_nothing_is_pruned_and_the_run_comes_back_2_minutes_later()
    {
        _d.Disk = Low;
        _d.Cache.Bytes = 3 * GiB;
        using (var build = _d.Apps.Pipeline.TryHoldBuilds())
        {
            Assert.NotNull(build);
            var held = await _d.Cleanup.LookAsync(T.AddMinutes(3));
            Assert.Equal((DiskTier.Low, (DiskResult?)null), (held.Tier, held.Ran));
            Assert.Empty(_d.Cache.Posts);
            Assert.Equal(DiskText.BuildRunning, (await _d.Cleanup.CleanNowAsync()).Message);
            // The notice waits for the run, so it says what the run freed.
            Assert.Empty(_d.Notices());
        }
        Assert.Null((await _d.Cleanup.LookAsync(T.AddMinutes(4))).Tier);
        var again = await _d.Cleanup.LookAsync(T.AddMinutes(5));
        Assert.Equal(DiskTier.Low, again.Tier);
        Assert.Equal([Prune("24h")], _d.Cache.Posts);
        Assert.Single(_d.Notices());
    }

    [Fact]
    public async Task A_deploy_waits_for_the_cleanup_and_its_log_says_so()
    {
        Assert.Equal("Waiting for another build to finish…", _d.Apps.Pipeline.WaitLine);
        using (_d.Apps.Pipeline.TryHoldBuilds("the disk cleanup to finish"))
            Assert.Equal("Waiting for the disk cleanup to finish…", _d.Apps.Pipeline.WaitLine);
        Assert.Equal("Waiting for another build to finish…", _d.Apps.Pipeline.WaitLine);

        // The daily run waits for a deploy that's queued: it may build any moment.
        await _d.Apps.AddAsync(new Deployment { AppId = _d.Apps.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Queued });
        Assert.Null((await _d.Cleanup.LookAsync(T.AddMinutes(16))).Tier);
        Assert.Empty(_d.Cache.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_prune_docker_refuses_or_doesnt_have_is_left_and_the_rest_goes_on(HttpStatusCode status)
    {
        _d.Cache.Refuse = status;
        _d.Images.Add("sha256:" + new string('c', 64), [], app: "orb");

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal(0, look.Ran!.CacheFreed);
        Assert.Equal([ShortId('c')], look.Ran.Images);
    }

    // ---- Which images go ----

    [Fact]
    public async Task Only_untagged_images_of_gitberths_builds_that_nothing_keeps_are_removed()
    {
        // orb runs gitberth/orb:1d2948c; kanban keeps an untagged image for its Roll back; a Compose release names one by ID.
        var running = _d.Images.Add(Id('a'), ["gitberth/orb:1d2948c"], app: "orb");
        var previous = _d.Images.Add(Id('b'), [], app: "kanban");
        var released = _d.Images.Add(Id('d'), [], app: "shop");
        var old = _d.Images.Add(Id('c'), [], app: "orb");
        var held = _d.Images.Add(Id('f'), [], app: "orb");
        var someoneElses = _d.Images.Add(Id('e'), [], app: null);
        _d.Images.InUse.Add(held);
        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _d.Apps.KanbanId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviousImage, previous));
        await _d.Apps.AddAsync(new Deployment
        {
            AppId = _d.Apps.KanbanId, Trigger = DeployTrigger.Manual, Status = DeployStatus.Failed,
            Images = """{"images":{"web":"gitberth/shop/web:1a2b3c4"},"ids":{"web":"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}}""",
        });

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal([ShortId('c')], look.Ran!.Images);
        Assert.Equal([$"remove {old}", $"remove {held}"], _d.Images.Calls);
        Assert.Equal([running, previous, released, held, someoneElses], _d.Images.All.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task A_deleted_apps_tag_a_container_held_is_recorded_and_goes_once_nothing_uses_it()
    {
        // orb is deleted with its images, but a container someone made from one still uses it.
        _d.Apps.Engine.Tags.AddRange(["gitberth/orb:1d2948c", "gitberth/orb:0ld0001"]);
        _d.Apps.Engine.Failing["image rm gitberth/orb:1d2948c"] = "Error response from daemon: conflict: unable to remove repository reference \"gitberth/orb:1d2948c\" (must force) - container 3f2a is using its referenced image";
        await _d.Apps.Actions.DeleteAsync(_d.Apps.ObsId, removeImages: true, removeVolumes: false);
        var entry = Assert.Single(await _d.LeftoversAsync());
        Assert.Equal(("gitberth/orb", false, "orb"), (entry.Repo, entry.Prefix, entry.Slug));

        var image = _d.Images.Add(Id('a'), ["gitberth/orb:1d2948c"], app: "orb");
        _d.Images.InUse.Add(image);
        // Listed in Settings › Disk as waiting, and the run leaves it while the container lives.
        var listed = Assert.Single(await _d.Cleanup.DeletedAppsAsync());
        Assert.Equal(("orb", true), (listed.Slug, listed.Waiting));
        var first = await _d.Cleanup.LookAsync(T.AddMinutes(16));
        Assert.Empty(first.Ran!.Images);
        Assert.Single(await _d.LeftoversAsync());

        _d.Images.InUse.Clear();
        var answer = await _d.Cleanup.CleanNowAsync();

        Assert.True(answer.Ok);
        Assert.Equal(["gitberth/orb:1d2948c"], answer.Result!.Images);
        Assert.Empty(_d.Images.All);
        Assert.Empty(await _d.LeftoversAsync());
    }

    [Fact]
    public async Task A_compose_apps_prefix_is_recorded_too_and_a_new_app_with_the_slug_takes_its_tags_over()
    {
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/shop/", prefix: true, T.AddDays(-2));
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/blog", prefix: false, T.AddDays(-2));
        _d.Images.Add(Id('a'), ["gitberth/shop/web:1a2b3c4"], app: "shop");
        _d.Images.Add(Id('b'), ["gitberth/blog:2b3c4d5"], app: "blog");
        // A new blog since: its tags are its own now.
        await _d.Apps.AddAsync(new AppDefinition { Name = "blog", Slug = "blog", Owner = "you", Repo = "blog", CurrentImage = "gitberth/blog:2b3c4d5", CreatedAt = T.AddDays(-1) });

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal(["gitberth/shop/web:1a2b3c4"], look.Ran!.Images);
        Assert.Equal(["gitberth/blog:2b3c4d5"], _d.Images.All.SelectMany(i => i.Tags));
        Assert.Empty(await _d.LeftoversAsync());
    }

    [Fact]
    public async Task The_entry_of_an_app_being_deleted_is_left_until_its_row_is_gone()
    {
        // Its images went first; its row goes a moment later. An app made before the entry is that one.
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/orb", prefix: false, DateTime.UtcNow.AddSeconds(5));
        _d.Images.Add(Id('a'), ["gitberth/orb:1d2948c"], app: "orb");

        await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Single(_d.Images.All);
        Assert.Single(await _d.LeftoversAsync());
    }

    [Fact]
    public async Task Images_of_deleted_apps_kept_on_purpose_are_listed_and_removed_only_on_request()
    {
        _d.Images.Add(Id('a'), ["gitberth/notes:1a2b3c4"], app: "notes", size: 300 * MiB);
        _d.Images.Add(Id('b'), ["gitberth/notes:2b3c4d5", "gitberth/notes:latest"], app: "notes", size: 200 * MiB);

        var listed = Assert.Single(await _d.Cleanup.DeletedAppsAsync());
        Assert.Equal(("notes", 3, 500 * MiB, false), (listed.Slug, listed.Tags.Count, listed.Size, listed.Waiting));
        Assert.Equal("3 images, about 500 MB", DiskText.Group(listed.Tags.Count, listed.Size));
        // Never by a run: the owner kept them.
        await _d.Cleanup.LookAsync(T.AddMinutes(16));
        Assert.Equal(2, _d.Images.All.Count);

        var answer = await _d.Cleanup.RemoveDeletedAppsImagesAsync(listed.Tags);

        Assert.Equal((true, "Removed 3 images of deleted apps."), (answer.Ok, answer.Message));
        Assert.Empty(_d.Images.All);
        var entry = Assert.Single(_d.Audit.Entries, e => e.Action == "settings.disk.images.removed");
        Assert.Equal("3: gitberth/notes:1a2b3c4, gitberth/notes:2b3c4d5, gitberth/notes:latest", Assert.Single(entry.Changes).To);
    }

    [Fact]
    public async Task Each_apps_images_beyond_its_keep_set_go_but_never_while_it_deploys_or_its_lock_is_held()
    {
        foreach (var tag in new[] { "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc" })
            await _d.Apps.AddAsync(new Deployment { AppId = _d.Apps.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = tag });
        _d.Apps.Engine.Tags.AddRange(["gitberth/orb:1d2948c", "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc", "gitberth/orb:0ld0001"]);

        using (await _d.Apps.AppLocks.AcquireAsync(_d.Apps.ObsId))
            await _d.Cleanup.CleanNowAsync();
        Assert.Contains("gitberth/orb:0ld0001", _d.Apps.Engine.Tags);

        await _d.Cleanup.CleanNowAsync();

        // The last 3 builds and the one it runs stay.
        Assert.Equal(["gitberth/orb:1d2948c", "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc"], _d.Apps.Engine.Tags);
    }

    [Fact]
    public async Task A_leftover_entry_never_takes_an_image_made_after_its_deletion()
    {
        // shop was deleted with its images at T, and a container kept one; a later shop came, deployed and went, keeping its own.
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/shop", prefix: false, T);
        _d.Images.Add(Id('a'), ["gitberth/shop:aaa1111"], app: "shop", created: T.AddDays(-1));
        _d.Images.Add(Id('b'), ["gitberth/shop:bbb2222"], app: "shop", created: T.AddHours(1));

        var listed = Assert.Single(await _d.Cleanup.DeletedAppsAsync());
        Assert.True(listed.Waiting);
        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal(["gitberth/shop:aaa1111"], look.Ran!.Images);
        Assert.Equal(["gitberth/shop:bbb2222"], _d.Images.All.SelectMany(i => i.Tags));
        // What's left is the later app's, kept: listed so.
        Assert.False(Assert.Single(await _d.Cleanup.DeletedAppsAsync()).Waiting);
    }

    [Fact]
    public async Task An_app_deleted_with_its_images_kept_drops_an_earlier_apps_leftovers_of_its_slug()
    {
        // An earlier orb's deletion left an entry; this orb is deleted with Also delete its images unticked.
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/orb", prefix: false, T.AddDays(-3));

        await _d.Apps.Actions.DeleteAsync(_d.Apps.ObsId, removeImages: false, removeVolumes: false);

        Assert.Empty(await _d.LeftoversAsync());
        // A later deletion with its images moves the entry's time on: what was made before it goes.
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/blog", prefix: false, T.AddDays(-2));
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/blog", prefix: false, T);
        Assert.Equal(T, Assert.Single(await _d.LeftoversAsync()).Since);
    }

    [Fact]
    public async Task An_image_a_live_app_names_or_keeps_is_never_a_deleted_apps()
    {
        // shop is gone, and its deletion asked for its images to go; storefront was made from one of them, and kanban keeps
        // another for its Roll back, by ID.
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/shop", prefix: false, T);
        _d.Images.Add(Id('a'), ["gitberth/shop:1a2b3c4"], app: "shop");
        var kept = _d.Images.Add(Id('b'), ["gitberth/shop:2b3c4d5"], app: "shop");
        await _d.Apps.AddAsync(new AppDefinition { Name = "storefront", Slug = "storefront", Source = AppSource.Local, ImageRef = "gitberth/shop:1a2b3c4" });
        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _d.Apps.KanbanId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviousImage, kept));

        Assert.Empty(await _d.Cleanup.DeletedAppsAsync());
        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Empty(look.Ran!.Images);
        Assert.Equal(2, _d.Images.All.Count);
        // Kept for the next run, which takes them once no app names them.
        Assert.Single(await _d.LeftoversAsync());
    }

    [Fact]
    public async Task Remove_these_images_removes_only_the_ones_the_person_confirmed()
    {
        _d.Images.Add(Id('a'), ["gitberth/notes:1a2b3c4"], app: "notes");
        var confirmed = Assert.Single(await _d.Cleanup.DeletedAppsAsync()).Tags;
        // Meanwhile blog is deleted with its images kept: listed next time, not removed with what was asked about.
        _d.Images.Add(Id('b'), ["gitberth/blog:2b3c4d5"], app: "blog");

        var answer = await _d.Cleanup.RemoveDeletedAppsImagesAsync(confirmed);

        Assert.Equal("Removed 1 image of deleted apps.", answer.Message);
        Assert.Equal(["gitberth/blog:2b3c4d5"], _d.Images.All.SelectMany(i => i.Tags));
    }

    [Fact]
    public async Task One_image_docker_fails_to_remove_doesnt_hold_up_the_rest()
    {
        // An untagged image of an old build whose removal Docker fails (500), listed first; then a deleted app's leftover.
        var stuck = _d.Images.Add(Id('c'), [], app: "orb");
        _d.Images.Failing.Add(stuck);
        await DiskLeftovers.RecordAsync(_d.Apps.Db, "gitberth/shop", prefix: false, T);
        _d.Images.Add(Id('a'), ["gitberth/shop:1a2b3c4"], app: "shop");

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(16));

        Assert.Equal(["gitberth/shop:1a2b3c4"], look.Ran!.Images);
        Assert.Empty(await _d.LeftoversAsync());
        Assert.Equal([stuck], _d.Images.All.Select(i => i.Id));
    }

    [Fact]
    public async Task An_app_deleted_while_a_run_goes_keeps_the_images_its_owner_kept()
    {
        foreach (var tag in new[] { "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc" })
            await _d.Apps.AddAsync(new Deployment { AppId = _d.Apps.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = tag });
        _d.Apps.Engine.Tags.AddRange(["gitberth/orb:1d2948c", "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc", "gitberth/orb:0ld0001"]);
        // While the run removes an old build's image, after it read the apps, orb is deleted with its images kept.
        _d.Images.Add(Id('c'), [], app: "kanban");
        var deleted = 0;
        _d.Images.BeforeRemove = async _ =>
        {
            if (Interlocked.Exchange(ref deleted, 1) == 0) await _d.Apps.Actions.DeleteAsync(_d.Apps.ObsId, removeImages: false, removeVolumes: false);
        };

        await _d.Cleanup.CleanNowAsync();

        Assert.Equal(1, deleted);
        Assert.Equal(["gitberth/orb:1d2948c", "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc", "gitberth/orb:0ld0001"], _d.Apps.Engine.Tags);
    }

    [Fact]
    public async Task A_compose_deploy_queued_while_the_keep_sets_go_keeps_its_new_release()
    {
        var shop = await _d.Apps.AddAsync(new AppDefinition { Name = "shop", Slug = "shop", Source = AppSource.Compose, CurrentImage = "gitberth/shop/web:aaaaaaa" });
        var ran = new Deployment { AppId = shop, Trigger = DeployTrigger.Manual, Status = DeployStatus.Succeeded, ImageTag = "gitberth/shop/web:aaaaaaa" };
        await _d.Apps.AddAsync(ran);
        var releases = ComposeReleases.For(_d.Apps.Options, "shop");
        Release(releases, "aaaaaaa", ran.Id);
        // A push queues a deploy of shop while the cleanup prunes orb's images: it moves its clone in as release bbbbbbb.
        var queued = 0;
        _d.Apps.Engine.BeforeCommand = async line =>
        {
            if (!line.StartsWith("image ls gitberth/orb", StringComparison.Ordinal) || Interlocked.Exchange(ref queued, 1) == 1) return;
            var building = new Deployment { AppId = shop, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building };
            await _d.Apps.AddAsync(building);
            Release(releases, "bbbbbbb", building.Id);
        };

        await _d.Cleanup.CleanNowAsync();

        Assert.Equal(1, queued);
        Assert.True(Directory.Exists(releases.Dir("bbbbbbb")));
        Assert.True(Directory.Exists(releases.Dir("aaaaaaa")));
    }

    [Fact]
    public async Task A_compose_prune_keeps_the_files_of_a_deploy_still_under_way()
    {
        var shop = await _d.Apps.AddAsync(new AppDefinition { Name = "shop", Slug = "shop", Source = AppSource.Compose, CurrentImage = "gitberth/shop/web:aaaaaaa" });
        var ran = new Deployment { AppId = shop, Trigger = DeployTrigger.Manual, Status = DeployStatus.Succeeded };
        var building = new Deployment { AppId = shop, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building };
        await _d.Apps.AddAsync(ran);
        await _d.Apps.AddAsync(building);
        var releases = ComposeReleases.For(_d.Apps.Options, "shop");
        Release(releases, "aaaaaaa", ran.Id);
        Release(releases, "bbbbbbb", building.Id);
        // Its file for docker compose build, while it builds, is its own too.
        File.WriteAllText(Path.Combine(releases.Dir("bbbbbbb"), $"gitberth-{building.Id}.build.json"), "{}");

        await using (var db = _d.Apps.Db.CreateDbContext())
            await _d.Apps.Pipeline.PruneComposeAsync(db, (await _d.Apps.RowAsync(a => a.Id == shop))!, ran.Id);

        Assert.True(File.Exists(Path.Combine(releases.Dir("bbbbbbb"), ComposeReleases.ResolvedName(building.Id))));
        Assert.True(File.Exists(Path.Combine(releases.Dir("bbbbbbb"), $"gitberth-{building.Id}.build.json")));
    }

    [Fact]
    public async Task The_keep_sets_say_only_what_they_removed()
    {
        foreach (var tag in new[] { "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc" })
            await _d.Apps.AddAsync(new Deployment { AppId = _d.Apps.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = tag });
        _d.Apps.Engine.Tags.AddRange(["gitberth/orb:1d2948c", "gitberth/orb:aaaaaaa", "gitberth/orb:bbbbbbb", "gitberth/orb:ccccccc", "gitberth/orb:0ld0001"]);
        // Someone removes another image by hand while the run goes: not the cleanup's to claim.
        _d.Images.Add(Id('n'), ["gitberth/notes:1a2b3c4"], app: "notes");
        _d.Apps.Engine.BeforeCommand = line =>
        {
            if (line.StartsWith("image ls gitberth/orb", StringComparison.Ordinal)) _d.Images.All.Clear();
            return Task.CompletedTask;
        };

        var answer = await _d.Cleanup.CleanNowAsync();

        Assert.Equal(["gitberth/orb:0ld0001"], answer.Result!.Images);
    }

    private static void Release(ComposeReleases releases, string id, int deployment)
    {
        Directory.CreateDirectory(releases.Dir(id));
        File.WriteAllText(Path.Combine(releases.Dir(id), ComposeReleases.ResolvedName(deployment)), "{}");
    }

    // ---- What it tells and shows ----

    [Fact]
    public async Task It_tells_each_step_down_once_room_again_once_and_nothing_again_after_a_restart()
    {
        _d.Disk = Low;
        var low = await _d.Cleanup.LookAsync(T.AddMinutes(3));
        var notice = Assert.Single(_d.Notices());
        Assert.Equal((DiskNotice.Low, NotifyEvent.DiskLow, NoticeLevel.Warning, "Docker's disk is getting low"), (low.Told, notice.Event, notice.Level, notice.Title));
        Assert.Equal("12.0 GB free of 64.0 GB. Gitberth stops pulling images under 5.0 GB. Settings › Disk shows what takes the room.", notice.Text);
        Assert.Equal(("", (string?)null), (notice.App, notice.Container));
        await _d.Cleanup.LookAsync(T.AddMinutes(4));
        Assert.Empty(_d.Notices());

        // Gitberth restarted: what was told is read back.
        _d.Cleanup = _d.New();
        await _d.Cleanup.LookAsync(T.AddMinutes(5));
        Assert.Empty(_d.Notices());

        _d.Disk = Full;
        await _d.Cleanup.LookAsync(T.AddMinutes(6));
        var full = Assert.Single(_d.Notices());
        Assert.Equal((NotifyEvent.DiskLow, NoticeLevel.Critical, "Docker's disk is almost full"), (full.Event, full.Level, full.Title));

        // Better, but not room again yet: nothing. Then room again, once.
        _d.Disk = new DiskSpace(16 * GiB, 64 * GiB);
        await _d.Cleanup.LookAsync(T.AddMinutes(7));
        Assert.Empty(_d.Notices());
        _d.Disk = new DiskSpace(18 * GiB, 64 * GiB);
        await _d.Cleanup.LookAsync(T.AddMinutes(8));
        var room = Assert.Single(_d.Notices());
        Assert.Equal((NotifyEvent.DiskOk, NoticeLevel.Good, "Docker's disk has room again", "18.0 GB free of 64.0 GB."), (room.Event, room.Level, room.Title, room.Text));
        await _d.Cleanup.LookAsync(T.AddMinutes(9));
        Assert.Empty(_d.Notices());

        // Low again within a day: not told again.
        _d.Disk = Low;
        await _d.Cleanup.LookAsync(T.AddMinutes(10));
        Assert.Empty(_d.Notices());
    }

    [Fact]
    public async Task What_it_keeps_that_couldnt_be_written_is_written_by_the_next_look()
    {
        // The database refuses disk.state for a moment (busy, or full).
        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""CREATE TRIGGER no_state BEFORE INSERT ON "Settings" WHEN NEW."Key" = 'disk.state' BEGIN SELECT RAISE(ABORT, 'database is locked'); END;""");
        _d.Disk = Low;
        await Assert.ThrowsAnyAsync<Exception>(() => _d.Cleanup.LookAsync(T.AddMinutes(3)));
        Assert.Single(_d.Notices());

        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER no_state;");
        await _d.Cleanup.LookAsync(T.AddMinutes(4));

        // Written now: after a restart, the getting-low notice isn't told again.
        Assert.NotNull(await _d.Apps.Settings.GetAsync(SettingKeys.DiskState));
        _d.Cleanup = _d.New();
        await _d.Cleanup.LookAsync(T.AddMinutes(5));
        Assert.Empty(_d.Notices());
    }

    [Fact]
    public async Task A_disk_that_cant_be_read_tells_nothing_and_shows_nothing()
    {
        _d.Disk = Unread;

        var look = await _d.Cleanup.LookAsync(T.AddMinutes(3));

        Assert.Equal((DiskNotice.None, DiskLevel.Ok), (look.Told, look.Shown));
        Assert.Empty(_d.Notices());
        Assert.Empty(_d.Board.Items);
    }

    [Fact]
    public async Task The_dashboard_shows_a_low_disk_until_theres_room_again_and_an_almost_full_one_in_the_top_bar_too()
    {
        _d.Disk = Low;
        _d.Cache.Bytes = 2 * GiB;
        _d.Cache.Frees["24h"] = 2 * GiB;
        await _d.Cleanup.LookAsync(T.AddMinutes(3));
        var item = Assert.Single(_d.Board.Items);
        Assert.Equal((AttentionLevel.Warn, "Docker's disk is getting low", "low", (string?)null, "settings#disk"), (item.Level, item.Title, item.Dismiss, item.Pill, item.Href));
        Assert.StartsWith("12.0 GB free of 64.0 GB. Gitberth stops pulling images under 5.0 GB. It cleared 2.0 GB of old build cache at ", item.Text, StringComparison.Ordinal);
        Assert.Equal("Docker's disk is getting low: 12.0 GB free of 64.0 GB; pulls stop under 5.0 GB.", Assert.Single(_d.Board.Problems));

        _d.Disk = Full;
        await _d.Cleanup.LookAsync(T.AddMinutes(4));
        item = Assert.Single(_d.Board.Items);
        Assert.Equal((AttentionLevel.Critical, "Disk almost full", (string?)null), (item.Level, item.Pill, item.Dismiss));

        // Above the line but not room again: it stays, as getting low.
        _d.Disk = new DiskSpace(16 * GiB, 64 * GiB);
        await _d.Cleanup.LookAsync(T.AddMinutes(5));
        Assert.Equal(AttentionLevel.Warn, Assert.Single(_d.Board.Items).Level);
        _d.Disk = new DiskSpace(18 * GiB, 64 * GiB);
        await _d.Cleanup.LookAsync(T.AddMinutes(6));
        Assert.Empty(_d.Board.Items);
    }

    // ---- The audit log ----

    [Fact]
    public async Task A_run_that_removed_something_is_in_the_audit_log_and_one_that_found_nothing_isnt()
    {
        _d.Cache.Bytes = 3 * GiB;
        var first = await _d.Cleanup.LookAsync(T.AddMinutes(16));
        Assert.Empty(_d.Audit.Entries);
        Assert.False(first.Ran!.Removed);

        _d.Cache.Frees["168h"] = 2 * GiB;
        _d.Images.Add(Id('c'), [], app: "orb");
        var answer = await _d.Cleanup.CleanNowAsync();

        Assert.Equal("Cleaned up: 2.0 GB freed.", answer.Message);
        var entry = Assert.Single(_d.Audit.Entries, e => e.Action == "settings.disk.cleaned");
        Assert.Equal("Cleaned up Docker's disk: 2.0 GB freed (build cache no build used for 7 days, and 1 image no app needs). 40.0 GB free now.", entry.Summary);
        Assert.Equal([("Build cache", "3.0 GB", "1.0 GB"), ("Images removed", null, $"1: {ShortId('c')}"), ("Free", "40.0 GB", "40.0 GB")],
            entry.Changes.Select(c => (c.Field, c.From, c.To)));
    }

    [Fact]
    public async Task The_daily_run_is_gitberths_through_the_disk_cleanup()
    {
        _d.Cache.Bytes = 3 * GiB;
        _d.Cache.Frees["168h"] = 1 * GiB;

        await _d.Cleanup.LookAsync(T.AddMinutes(16));

        var entry = Assert.Single(_d.Audit.Entries, e => e.Action == "settings.disk.cleaned");
        Assert.Equal((AuditActorKind.Gitberth, AuditVia.Cleanup), (entry.Actor.Kind, entry.Actor.Via));
        Assert.Equal($"Today {Clock(T.AddMinutes(16))}, by Gitberth: 1.0 GB freed (build cache 1.0 GB). 40.0 GB free after.",
            DiskText.Last((await _d.Cleanup.ReportAsync()).Last, T.AddMinutes(20)));
    }

    [Fact]
    public async Task The_switch_and_the_days_are_saved_and_recorded()
    {
        await _d.Choices.SetOnAsync(false);
        Assert.Equal("Keep it from 1 to 90 days.", await _d.Choices.SetKeepDaysAsync(91));
        Assert.Null(await _d.Choices.SetKeepDaysAsync(14));

        Assert.False(await _d.Choices.OnAsync());
        Assert.Equal(14, await _d.Choices.KeepDaysAsync());
        Assert.Equal(["Gitberth no longer cleans up Docker's disk by itself.", "Changed how long Docker's unused build cache is kept: 14 days."],
            _d.Audit.Entries.Where(e => e.Action == "settings.disk.changed").Select(e => e.Summary));
        Assert.Equal(("7 days", "14 days"), (_d.Audit.Entries.Last().Changes[0].From, _d.Audit.Entries.Last().Changes[0].To));
        // Off, nothing runs by itself.
        Assert.Null((await _d.Cleanup.LookAsync(T.AddMinutes(16))).Tier);
    }

    [Fact]
    public void Its_own_removals_are_never_changes_made_outside_gitberth()
    {
        // A build cache prune makes no event the audit log follows; an image removal is expected while it runs.
        Assert.Empty(ExpectedChanges.ApiExpectations(HttpMethod.Post, DockerApi.PrunePath(TimeSpan.FromDays(7)), null));
        Assert.Empty(ExpectedChanges.ApiExpectations(HttpMethod.Post, DockerApi.PrunePath(null), null));
        var removal = Assert.Single(ExpectedChanges.ApiExpectations(HttpMethod.Delete, $"images/{Uri.EscapeDataString(Id('c'))}", null));
        Assert.Equal(("image", ExpectedChanges.ImageRemove), (removal.Type, removal.Actions));
    }

    // ---- The words ----

    [Fact]
    public void What_it_says()
    {
        Assert.Equal(("2.1 GB", "340 MB", "12 KB"), (DiskText.Size(2254857830), DiskText.Size(340 * MiB), DiskText.Size(12 * 1024)));
        Assert.Equal("2.0 GB of it no build used for 7 days: the next cleanup removes that.", DiskText.Stale(2 * GiB, 7));
        Assert.Null(DiskText.Stale(0, 7));
        Assert.Equal("No cleanup yet.", DiskText.Last(null, T));
        var result = new DiskResult(T.AddDays(-1), DiskTier.Critical, "admin", 6 * GiB, 6 * GiB, null, null, [], 3 * GiB, 9 * GiB, 64 * GiB);
        Assert.Equal($"Yesterday {Clock(T)}, by admin: 6.0 GB freed (build cache 6.0 GB). 9.0 GB free after.", DiskText.Last(result, T));
        Assert.Equal($"Yesterday {Clock(T)}, by admin: 1 image removed. 9.0 GB free after.",
            DiskText.Last(result with { Freed = 0, CacheFreed = 0, Images = ["gitberth/orb:1d2948c"] }, T));
        Assert.Equal("Cleaned up: 1 image removed.", DiskText.Cleaned(result with { Freed = 0, CacheFreed = 0, Images = ["gitberth/orb:1d2948c"] }));
        Assert.Equal("Docker's disk was almost full (3.0 GB free), so Gitberth cleared all the build cache no build was using: 6.0 GB freed. 9.0 GB free now.",
            DiskText.Audit(result, 7));
        Assert.Equal(("Docker's disk is almost full",
                "3.0 GB free of 64.0 GB on homelab: Gitberth doesn't pull images until there's more room, and builds may fail. It cleared all the build cache no build was using (6.0 GB). Free some space: Settings › Disk shows what takes it."),
            DiskText.Notice(DiskNotice.TooLow, Full, "homelab", 6 * GiB));
        Assert.Equal("12.0 GB free of 64.0 GB on homelab. Gitberth stops pulling images under 5.0 GB. It cleared 2.0 GB of build cache no build used for a day. Settings › Disk shows what takes the room.",
            DiskText.Notice(DiskNotice.Low, Low, "homelab", 2 * GiB).Text);
        Assert.Equal("3.0 GB free: Gitberth doesn't pull images until there's more room, and builds may fail. Free some space: Settings › Disk shows what takes it.",
            DiskText.Banner(DiskLevel.TooLow, Full, null));
        Assert.Equal("Nothing to clean up.", DiskText.Cleaned(result with { CacheFreed = 0 }));
        Assert.Equal("Removed 2 images of deleted apps. 1 stays while a container uses it: Gitberth removes it once nothing does.", DiskText.RemovedDeleted(2, 1));
    }

    [Fact]
    public async Task Clear_build_cache_and_clean_up_now_say_which_of_them_holds_builds_off()
    {
        var cache = new Gitberth.Deploy.BuildCache(_d.Api, _d.Apps.Pipeline, NullLogger<Gitberth.Deploy.BuildCache>.Instance);
        using (_d.Apps.Pipeline.TryHoldBuilds("the disk cleanup to finish", DiskText.Holding))
            Assert.Equal("The disk cleanup is running, so the cache wasn't cleared. Clear it once that's done.", (await cache.ClearAsync()).Message);
        using (_d.Apps.Pipeline.TryHoldBuilds("the build cache to be cleared", Gitberth.Deploy.BuildCache.Clearing))
            Assert.Equal("The build cache is being cleared: clean up once that's done.", (await _d.Cleanup.CleanNowAsync()).Message);
        // A build has the gate: as before.
        using (_d.Apps.Pipeline.TryHoldBuilds())
        {
            Assert.Equal("A build is running, so the cache wasn't cleared. Clear it once the build is done.", (await cache.ClearAsync()).Message);
            Assert.Equal(DiskText.BuildRunning, (await _d.Cleanup.CleanNowAsync()).Message);
        }
    }

    [Fact]
    public async Task Railpacks_own_cache_goes_by_the_same_age_and_gitberth_buildkit_with_the_last_app_that_builds_with_it()
    {
        var commands = new List<string>();
        var running = true;
        var docker = new DockerCli(_d.Apps.Options)
        {
            Runner = (a, _, _) =>
            {
                lock (commands) commands.Add(string.Join(' ', a));
                return Task.FromResult(a is ["container", "inspect", ..]
                    ? new ProcessResult(0, $"{(running ? "true" : "false")} {Gitberth.Railpack.RailpackRelease.BuildKitImage} 0a1b2c3d4e5f\n", "")
                    : new ProcessResult(0, "", ""));
            },
        };
        using var buildKit = new Gitberth.Railpack.RailpackBuildKit(docker, _d.Apps.Options, NullLogger<Gitberth.Railpack.RailpackBuildKit>.Instance, _d.Api);
        _d.Cleanup = _d.New(buildKit);
        string[] Removals() => commands.Where(c => c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("volume ", StringComparison.Ordinal)).ToArray();

        // While it runs: what no build used for the days the cleanup keeps, through buildx, and nothing goes.
        await _d.Cleanup.CleanNowAsync();
        Assert.Contains("buildx prune --builder gitberth-buildkit --force --filter until=168h", commands);
        Assert.Empty(Removals());

        // Stopped, while an app builds with Railpack: it stays for that app's next build.
        running = false;
        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _d.Apps.ObsId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DetectedKind, "Railpack"));
        commands.Clear();
        await _d.Cleanup.CleanNowAsync();
        Assert.Empty(Removals());
        Assert.DoesNotContain(commands, c => c.StartsWith("buildx", StringComparison.Ordinal));

        // No app builds with it any more: it goes, with its volume.
        await using (var db = _d.Apps.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _d.Apps.ObsId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DetectedKind, "Static"));
        commands.Clear();
        await _d.Cleanup.CleanNowAsync();
        Assert.Equal(["rm -f gitberth-buildkit", "volume rm gitberth-buildkit"], Removals());
    }

    [Fact]
    public void The_dashboard_and_the_apps_strip_say_the_disk_in_the_lines_own_units()
    {
        // 14.9 GiB free of 64 GiB: under the warning line of 15 GiB, and every figure says so in the same units.
        var disk = new DiskSpace(14 * GiB + 900 * MiB, 64 * GiB);
        Assert.True(disk.Low);
        Assert.Equal("14.9 GB free", DiskText.Free(disk));
        Assert.Contains("warns under 15.0 GB", DiskText.Lines(disk), StringComparison.Ordinal);
        Assert.Equal(("49", "64"), (DiskText.WholeGb(disk.TotalBytes - disk.FreeBytes), DiskText.WholeGb(disk.TotalBytes)));
        // The build cache's row in Settings › Disk, as the rest of the section says sizes.
        Assert.Equal("2.1 GB on this host. It makes builds quick: steps that didn't change aren't run again.", DiskText.Cache(2254857830));
    }

    private static string Prune(string until) => $"POST /v1.56/build/prune?filters=%7B%22until%22%3A%5B%22{until}%22%5D%7D";

    // As the words say a time: in Gitberth's time zone.
    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Id(char c) => "sha256:" + new string(c, 64);

    private static string ShortId(char c) => new(c, 12);
}

/// <summary>
/// The disk cleanup on AppsRig (its database, apps, deploy pipeline and locks), with Docker's build cache and images in
/// memory, a disk the test sets, and its notices, board and audit log in reach.
/// </summary>
internal sealed class DiskRig : IDisposable
{
    private readonly ImageKit.Clock _clock;

    public DiskRig(DateTime now)
    {
        _clock = new ImageKit.Clock(now);
        Api = new DockerApi("unix:///var/run/docker.sock", Cache);
        Notify = new NotificationSettings(Apps.Settings);
        Notifier = new Notifier(Notify, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), Apps.Settings, NullLogger<Notifier>.Instance);
        Choices = new DiskSettings(Apps.Settings, Audit);
        Cleanup = New();
    }

    public AppsRig Apps { get; } = new();
    public CacheEngine Cache { get; } = new();
    public DockerApi Api { get; }
    public ImageStore Images { get; } = new();
    public AuditLog Audit { get; } = new();
    public AttentionBoard Board { get; } = new();
    public NotificationSettings Notify { get; }
    public Notifier Notifier { get; }
    public DiskSettings Choices { get; }
    public DiskSpace Disk { get; set; } = new(40L << 30, 64L << 30);
    public DiskCleanup Cleanup { get; set; }

    /// <summary>Another cleanup on the same database: Gitberth after a restart; with gitberth-buildkit when a test gives one.</summary>
    public DiskCleanup New(Gitberth.Railpack.RailpackBuildKit? buildKit = null) => new(Api, null, Apps.Pipeline, Apps.Db, Apps.Settings, Choices, Apps.AppLocks, Notifier, Board,
        NullLogger<DiskCleanup>.Instance, Audit, time: _clock, engine: Images, actions: Apps.Actions, buildKit: buildKit) { ReadDisk = () => Disk };

    /// <summary>A destination, so notices are queued rather than dropped as unwanted.</summary>
    public Task TellAsync() => Notify.SaveDestinationAsync(new Destination { Id = "d1", Kind = DestinationKind.Webhook, Name = "Ops", Url = "https://hooks.example.com/gitberth" });

    public List<Notice> Notices()
    {
        var list = new List<Notice>();
        while (Notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    public async Task<IReadOnlyList<DiskLeftover>> LeftoversAsync() => DiskLeftovers.Read(await Apps.Settings.GetAsync(SettingKeys.DiskLeftovers));

    public void Dispose()
    {
        Audit.Dispose();
        Api.Dispose();
        Apps.Dispose();
    }
}

/// <summary>
/// Docker's build cache through the Engine API, in memory: system/df says its size, and each prune frees what the test
/// set for its filter (every unused record with none). Docker's refusal when the test sets one.
/// </summary>
internal sealed class CacheEngine : HttpMessageHandler
{
    public long Bytes { get; set; }
    /// <summary>What a prune with until=&lt;hours&gt;h frees ("168h").</summary>
    public Dictionary<string, long> Frees { get; } = new(StringComparer.Ordinal);
    public HttpStatusCode? Refuse { get; set; }
    /// <summary>system/df answers what can't be read, as an engine mid-restart can.</summary>
    public bool Garbled { get; set; }
    public List<string> Calls { get; } = [];
    public List<string> Posts { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        lock (Calls) Calls.Add(call);
        var path = request.RequestUri.AbsolutePath;
        if (path == "/version") return Answer(DockerFixtures.Text("version.json"));
        if (request.Method == HttpMethod.Post && path == "/v1.56/build/prune")
        {
            lock (Posts) Posts.Add(call);
            if (Refuse is { } status) return Answer("""{"message":"a prune operation is already running"}""", status);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var until = query.Contains("until", StringComparison.Ordinal) ? JsonNode.Parse(query[(query.IndexOf('{'))..])!["until"]![0]!.GetValue<string>() : null;
            var freed = Math.Min(Bytes, until is null ? Bytes : Frees.GetValueOrDefault(until));
            Bytes -= freed;
            return Answer($$"""{"CachesDeleted":["a"],"SpaceReclaimed":{{freed}}}""");
        }
        if (path == "/v1.56/system/df" && Garbled) return Answer("""{"BuildCache":[{"ID":""");
        if (path == "/v1.56/system/df" && request.RequestUri.Query.Contains("build-cache", StringComparison.Ordinal))
            return Answer($$"""{"BuildCache":[{"ID":"a","Type":"regular","Size":{{Bytes}},"Shared":false,"InUse":false}]}""");
        if (path == "/v1.56/system/df") return Answer("""{"LayersSize":1234567890}""");
        return Answer("""{"message":"not here"}""", HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}

/// <summary>
/// Docker's images, in memory, as the cleanup sees them: each with its tags, its gitberth.app label, its size and when it
/// was made, and the ones containers use. A removal is never forced: Docker's 409 for one a container uses, or by ID for
/// one with a tag; a 500 for one the test says Docker fails on.
/// </summary>
internal sealed class ImageStore : IImageEngine
{
    /// <summary>When an image was made unless a test says: well before anything the tests delete.</summary>
    public static readonly DateTime Long = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    public sealed record Image(string Id, List<string> Tags, string? App, long Size, DateTime Created);

    public List<Image> All { get; } = [];
    public HashSet<string> InUse { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    /// <summary>IDs or tags whose removal Docker fails (a 500, as a busy layer or a broken store gives).</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>Awaited before each removal, with what's removed: a test does something meanwhile.</summary>
    public Func<string, Task>? BeforeRemove { get; set; }

    public string Add(string id, string[] tags, string? app, long size = 100L << 20, DateTime? created = null)
    {
        All.Add(new Image(id, [.. tags], app, size, created ?? Long));
        return id;
    }

    public Task<JsonArray> ListImagesAsync(CancellationToken ct) => Task.FromResult(new JsonArray([.. All.Select(i => (JsonNode)new JsonObject
    {
        ["Id"] = i.Id,
        ["RepoTags"] = new JsonArray([.. i.Tags.Select(t => (JsonNode)t)]),
        ["Labels"] = i.App is null ? new JsonObject() : new JsonObject { ["gitberth.app"] = i.App },
        ["Size"] = i.Size,
        ["Created"] = new DateTimeOffset(i.Created).ToUnixTimeSeconds(),
    })]));

    public async Task<JsonArray> RemoveImageAsync(string idOrName, CancellationToken ct)
    {
        if (BeforeRemove is { } before) await before(idOrName);
        return Remove(idOrName);
    }

    private JsonArray Remove(string idOrName)
    {
        Calls.Add($"remove {idOrName}");
        if (Failing.Contains(idOrName)) throw new DockerApiException(500, $"error removing image {idOrName}: layer is busy");
        if (All.FirstOrDefault(i => i.Id == idOrName) is { } byId)
        {
            if (InUse.Contains(byId.Id) || byId.Tags.Count > 0) throw new DockerApiException(409, $"conflict: unable to delete {byId.Id[7..19]} (must be forced)");
            All.Remove(byId);
            return new JsonArray(new JsonObject { ["Deleted"] = byId.Id });
        }
        var tagged = All.FirstOrDefault(i => i.Tags.Contains(idOrName)) ?? throw new DockerApiException(404, $"No such image: {idOrName}");
        if (tagged.Tags.Count == 1 && InUse.Contains(tagged.Id))
            throw new DockerApiException(409, $"conflict: unable to remove repository reference \"{idOrName}\" (must force) - container 3f2a is using its referenced image");
        tagged.Tags.Remove(idOrName);
        if (tagged.Tags.Count > 0) return new JsonArray(new JsonObject { ["Untagged"] = idOrName });
        All.Remove(tagged);
        return new JsonArray(new JsonObject { ["Untagged"] = idOrName }, new JsonObject { ["Deleted"] = tagged.Id });
    }

    public Task<JsonArray> ListContainersAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonArray> SearchImagesAsync(string term, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonObject> DistributionAsync(string reference, string? registryAuth, CancellationToken ct) => throw new NotSupportedException();
    public IAsyncEnumerable<JsonObject> PullAsync(string fromImage, string tagOrDigest, string? platform, string? registryAuth, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonObject> AuthAsync(JsonObject credentials, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonObject> InfoAsync(CancellationToken ct) => throw new NotSupportedException();
}
