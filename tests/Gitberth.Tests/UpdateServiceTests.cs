using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Images;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// What "update" means by where the image comes from, and the update and rollback themselves, with the catalog, the
/// settings and the change engine stood in for. postgres runs the image captured from the host (3a4264520e12), whose
/// digest Docker Hub no longer serves for postgres:18 (it serves e3d24f6906d0, captured the same day).
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private const string Old = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81";
    private const string Remote = "sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42";
    private static readonly string New = ImageKit.Id('9');

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly ImageKit.UpdateSteps _steps;
    private readonly ImageKit.Clock _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly UpdateService _updates;

    public UpdateServiceTests()
    {
        _steps = new ImageKit.UpdateSteps(_db);
        _updates = new UpdateService(_steps, ImageKit.Images(_engine, _db), _db, NullLogger<UpdateService>.Instance) { Time = _clock };
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        _steps.Containers["postgres"] = DockerFixtures.Inspect("postgres");
        _engine.Uses("postgres", Old, "postgres:18");
    }

    public void Dispose() => _db.Dispose();

    private async Task CheckedAsync(string reference, string? digest, string? error = null, DateTime? at = null, DateTime? retryAfter = null)
    {
        await using var db = _db.CreateDbContext();
        db.ImageChecks.Add(new ImageCheck { Reference = reference, RemoteDigest = digest, Error = error, CheckedAt = at ?? _clock.Now.UtcDateTime, RetryAfter = retryAfter });
        await db.SaveChangesAsync();
    }

    private async Task<ImageCheck?> CheckOfAsync(string reference)
    {
        await using var db = _db.CreateDbContext();
        return await db.ImageChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Reference == reference);
    }

    // The pull of postgres:18 brings the newer image and moves the tag onto it, as Docker does; a check found it on Docker Hub
    // (only a reference a registry is known to have is pulled).
    private void NewerOnDockerHub()
    {
        KnownOnDockerHub();
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-postgres-18.jsonl").Split('\n'), () =>
        {
            _engine.Add(ImageKit.Image(New, [], ["postgres@" + Remote], exposed: ["5432/tcp"], volumes: ["/var/lib/postgresql"]));
            _engine.Tag("postgres:18", New);
        });
    }

    private void KnownOnDockerHub()
    {
        using var db = _db.CreateDbContext();
        db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = Remote, CheckedAt = _clock.Now.UtcDateTime });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_digest_among_the_running_images_repo_digests_is_no_update()
    {
        await CheckedAsync("docker.io/library/postgres:18", Old);

        var status = await _updates.StatusAsync("postgres");

        Assert.Equal((ImageOrigin.Registry, "postgres:18", false, Old, Old), (status.Origin, status.Tracked, status.Available, status.RunningDigest, status.LatestDigest));
        Assert.Equal("Up to date (postgres:18).", status.Text);
        Assert.False(status.CanRollBack);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("distribution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_digest_on_the_registry_is_an_update()
    {
        _engine.Distribution["docker.io/library/postgres:18"] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();

        var status = await _updates.CheckAsync("postgres");

        Assert.True(status.Available);
        Assert.Equal((Old, Remote), (status.RunningDigest, status.LatestDigest));
        Assert.Equal("Update available: postgres:18 (e3d2…)", status.Text);
        Assert.Equal(["distribution docker.io/library/postgres:18"], _engine.Calls);
        var check = await CheckOfAsync("docker.io/library/postgres:18");
        Assert.Equal((Remote, (string?)null, _clock.Now.UtcDateTime), (check!.RemoteDigest, check.Error, check.CheckedAt));
        // From then on, everyone on postgres:18 shows it without asking again.
        Assert.True((await _updates.StatusAsync("postgres")).Available);
        Assert.Single(_engine.Calls);
    }

    [Fact]
    public async Task Not_checked_yet_is_said_so()
    {
        var status = await _updates.StatusAsync("postgres");

        // Its RepoDigests don't make it a registry's (every image has them in the containerd store): a check has to find it.
        Assert.Equal((ImageOrigin.Local, false, "Not checked yet."), (status.Origin, status.Available, status.Text));
    }

    [Fact]
    public async Task A_local_tag_that_names_a_newer_image_is_an_update()
    {
        var a = ImageKit.Id('a');
        _engine.Add(ImageKit.Image(a, ["gitberth-sample-go-gin:latest"], created: "2026-11-01T00:00:00Z"));
        _steps.Containers["sample-go-gin"] = ImageKit.Container("sample-go-gin", "c1", "gitberth-sample-go-gin", a);

        var before = await _updates.StatusAsync("sample-go-gin");
        Assert.Equal((ImageOrigin.Local, false, "Up to date: it runs the image gitberth-sample-go-gin names."), (before.Origin, before.Available, before.Text));

        // The sample checker rebuilt it three hours ago.
        _engine.Add(ImageKit.Image(ImageKit.Id('b'), [], created: "2026-11-07T09:00:00Z"));
        _engine.Tag("gitberth-sample-go-gin:latest", ImageKit.Id('b'));
        _clock.Now += TimeSpan.FromSeconds(20);

        var after = await _updates.StatusAsync("sample-go-gin");
        Assert.Equal((ImageOrigin.Local, true, "A newer local image (built 3 h ago)"), (after.Origin, after.Available, after.Text));
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_digest_pin_is_pinned_unless_the_app_follows_a_tag()
    {
        _steps.Containers["pinned"] = ImageKit.Container("pinned", "c2", "postgres@" + Old, Old);

        var pinned = await _updates.StatusAsync("pinned");
        Assert.Equal((ImageOrigin.Pinned, false, "Pinned to a digest: nothing to check."), (pinned.Origin, pinned.Available, pinned.Text));

        // A settings change pinned the clone; the app's row still follows the tag.
        await _db.AddAsync(new AppDefinition { Name = "pinned", Slug = "pinned", Container = "pinned", Source = AppSource.Existing, ImageRef = "postgres:18" });
        await CheckedAsync("docker.io/library/postgres:18", Remote);
        var followed = await _updates.StatusAsync("pinned");
        Assert.Equal((ImageOrigin.Registry, "postgres:18", true), (followed.Origin, followed.Tracked, followed.Available));
    }

    [Fact]
    public async Task Badges_come_from_the_last_checks_without_asking_any_registry()
    {
        await CheckedAsync("docker.io/library/postgres:18", Remote);
        var a = ImageKit.Id('a');
        _engine.Add(ImageKit.Image(a, ["gitberth-sample-go-gin:latest"]));

        var badges = await _updates.BadgesAsync(
        [
            new UpdateSubject("postgres", "postgres:18", Old, false),
            new UpdateSubject("sample-go-gin", "gitberth-sample-go-gin", a, false),
            new UpdateSubject("gb-orb", null, ImageKit.Id('o'), true),
            new UpdateSubject("pinned", null, Old, false),
        ]);

        Assert.Equal(["pinned", "postgres", "sample-go-gin"], badges.Keys.Order());
        Assert.Equal(new UpdateBadge(true, "Update available: postgres:18 (e3d2…)", _clock.Now.UtcDateTime, null), badges["postgres"]);
        Assert.False(badges["sample-go-gin"].Available);
        Assert.Equal("Pinned to a digest: nothing to check.", badges["pinned"].Text);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_sign_in_that_failed_is_the_badges_text()
    {
        await CheckedAsync("docker.io/library/postgres:18", null, "Can't check: sign-in failed.");

        var status = await _updates.StatusAsync("postgres");

        Assert.Equal((false, "Can't check: sign-in failed.", "Can't check: sign-in failed."), (status.Available, status.Text, status.Error));
    }

    [Fact]
    public async Task Prepares_an_update_by_pulling_the_tag_and_planning_on_the_new_image()
    {
        NewerOnDockerHub();
        var seen = new ImageKit.Seen<PullProgress>();

        var prepared = await _updates.PrepareAsync("postgres", seen);

        Assert.Null(prepared.Blocked);
        Assert.True(prepared.Pull!.Ok);
        Assert.Equal((New, Remote), (prepared.Pull.ImageId, prepared.Pull.Digest));
        Assert.NotEmpty(seen.Values);
        Assert.Equal(["adopt postgres", "plan Update postgres postgres:18"], _steps.Calls);
        var request = Assert.Single(_steps.Requests);
        Assert.Equal((ChangeKind.Update, "postgres:18", New), (request.Kind, request.Wanted.Image, request.ExpectedImageId));
        Assert.Equal("postgres:18", request.Loaded.Image);
        Assert.Equal((string)DockerFixtures.Inspect("postgres")["Id"]!, request.ContainerId);
        Assert.Equal(["POSTGRES_PASSWORD"], request.SecretKeys);
        Assert.Equal(80, request.WebPort);
        Assert.Equal(ChangeKind.Update, prepared.Plan!.Kind);

        // Adopted: the row keeps the tag, so a pinned clone still follows postgres:18.
        await using var db = _db.CreateDbContext();
        var row = await db.Apps.SingleAsync();
        Assert.Equal((AppSource.Existing, "postgres", "postgres:18"), (row.Source, row.Container, row.ImageRef));
    }

    [Fact]
    public async Task Prepare_is_blocked_when_the_tag_moved_again_while_it_was_pulled()
    {
        NewerOnDockerHub();
        var pulled = false;
        var then = _engine.Pulls["docker.io/library/postgres:18"].Then!;
        _engine.Pulls["docker.io/library/postgres:18"] = (_engine.Pulls["docker.io/library/postgres:18"].Lines, () => { then(); pulled = true; });
        _engine.OnInspect = name =>
        {
            // Someone else's pull, between this pull and the plan.
            if (!pulled || name != "postgres:18") return;
            pulled = false;
            _engine.Add(ImageKit.Image(ImageKit.Id('c'), []));
            _engine.Tag("postgres:18", ImageKit.Id('c'));
        };

        var prepared = await _updates.PrepareAsync("postgres");

        Assert.Equal("postgres:18 moved again while it was pulled: check again.", prepared.Blocked);
        Assert.Null(prepared.Plan);
        Assert.Empty(_steps.Requests);
    }

    [Fact]
    public async Task Prepare_says_why_there_is_nothing_to_update()
    {
        KnownOnDockerHub();
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-up-to-date.jsonl").Split('\n'), null);
        Assert.Equal("It's up to date: postgres runs the image postgres:18 names.", (await _updates.PrepareAsync("postgres")).Blocked);

        await _db.AddAsync(new AppDefinition { Name = "orb", Slug = "orb", Source = AppSource.Git });
        Assert.Equal("A GitHub app is updated by a new commit: use Deploy.", (await _updates.PrepareAsync("gb-orb")).Blocked);

        _steps.Containers["pinned"] = ImageKit.Container("pinned", "c2", "postgres@" + Old, Old);
        Assert.StartsWith("It's pinned to a digest, so there's nothing to update.", (await _updates.PrepareAsync("pinned")).Blocked);

        Assert.Equal("gone has no container to update: start it first.", (await _updates.PrepareAsync("gone")).Blocked);

        _engine.Pulls.Clear();
        var failed = await _updates.PrepareAsync("postgres");
        Assert.False(failed.Pull!.Ok);
        Assert.Equal(failed.Pull.Message, failed.Blocked);
        Assert.Empty(_steps.Requests);
    }

    [Fact]
    public async Task A_rollback_plan_goes_back_to_the_previous_image_without_a_pull()
    {
        var previous = ImageKit.Id('p');
        _engine.Add(ImageKit.Image(previous, []));
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = previous });

        Assert.True((await _updates.StatusAsync("postgres")).CanRollBack);
        var prepared = await _updates.PrepareRollbackAsync("postgres");

        Assert.Null(prepared.Blocked);
        Assert.Null(prepared.Pull);
        var request = Assert.Single(_steps.Requests);
        Assert.Equal((ChangeKind.Rollback, previous, previous), (request.Kind, request.Wanted.Image, request.ExpectedImageId));
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("pull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Roll_back_names_its_image_and_is_offered_only_while_that_image_is_here()
    {
        var previous = ImageKit.Id('p');
        _engine.Add(ImageKit.Image(previous, [], ["postgres@" + ImageKit.Digest('a')]));
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = previous });

        // Named by its digest, as the card's Running and Latest lines name theirs.
        var status = await _updates.StatusAsync("postgres");
        Assert.Equal((true, "postgres:18 (aaaa…)"), (status.CanRollBack, status.Previous));

        // Someone removed it: there's nothing to go back to, so nothing is offered.
        _engine.Images.RemoveAll(i => (string)i["Id"]! == previous);
        var gone = await _updates.StatusAsync("postgres");
        Assert.Equal((false, (string?)null), (gone.CanRollBack, gone.Previous));
    }

    [Fact]
    public async Task Roll_back_is_not_offered_to_the_image_it_runs_already()
    {
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = Old });

        var status = await _updates.StatusAsync("postgres");

        Assert.Equal((false, (string?)null), (status.CanRollBack, status.Previous));
        // An image kept by its ID only is named by it.
        var previous = ImageKit.Id('q');
        _engine.Add(ImageKit.Image(previous, []));
        await using (var db = _db.CreateDbContext())
            await db.Apps.ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviousImage, previous));
        Assert.Equal("postgres:18 (qqqq…)", (await _updates.StatusAsync("postgres")).Previous);
    }

    [Fact]
    public async Task The_image_kept_for_another_apps_roll_back_survives_this_apps_update()
    {
        // replica was updated off the older image too, and keeps it for its own Roll back.
        NewerOnDockerHub();
        var older = ImageKit.Id('x');
        _engine.Add(ImageKit.Image(older, []));
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = older });
        await _db.AddAsync(new AppDefinition { Name = "replica", Slug = "replica", Container = "replica", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = older });

        await _updates.Start((await _updates.PrepareAsync("postgres")).Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.DoesNotContain($"remove {older}", _engine.Calls);
        Assert.Contains(_engine.Images, i => (string)i["Id"]! == older);
    }

    [Fact]
    public async Task The_image_the_tag_moved_off_stays_while_another_app_keeps_it_for_roll_back()
    {
        // postgres was rolled back off the image the host's postgres:18 still names; replica was updated off it since, and
        // keeps it for its own Roll back.
        var rolledBack = ImageKit.Id('b');
        _engine.Add(ImageKit.Image(rolledBack, [], ["postgres@" + ImageKit.Digest('d')]));
        _engine.Tag("postgres:18", rolledBack);
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18" });
        await _db.AddAsync(new AppDefinition { Name = "replica", Slug = "replica", Container = "replica", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = rolledBack });
        NewerOnDockerHub();

        await _updates.Start((await _updates.PrepareAsync("postgres")).Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.DoesNotContain($"remove {rolledBack}", _engine.Calls);
        Assert.Contains(_engine.Images, i => (string)i["Id"]! == rolledBack);
    }

    [Fact]
    public async Task An_app_set_to_when_i_ask_gets_no_badge_from_another_apps_check()
    {
        // pg-copy is set to Tell me and was checked: postgres follows the same reference, but is set to When I ask.
        await CheckedAsync("docker.io/library/postgres:18", Remote);

        var badges = await _updates.BadgesAsync(
        [
            new UpdateSubject("postgres", "postgres:18", Old, false, UpdatePolicy.Off),
            new UpdateSubject("pg-copy", "postgres:18", Old, false, UpdatePolicy.Notify),
        ]);

        Assert.Equal(["pg-copy"], badges.Keys);
        Assert.True(badges["pg-copy"].Available);
        // Its own page still says what's known, and its own Check for update still asks.
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", UpdatePolicy = UpdatePolicy.Off });
        Assert.True((await _updates.StatusAsync("postgres")).Available);
        _engine.Distribution["docker.io/library/postgres:18"] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();
        Assert.True((await _updates.CheckAsync("postgres")).Available);
        Assert.Contains("distribution docker.io/library/postgres:18", _engine.Calls);
    }

    [Fact]
    public async Task A_rollback_needs_a_previous_image_that_is_still_there()
    {
        Assert.StartsWith("There's no earlier image to go back to", (await _updates.PrepareRollbackAsync("postgres")).Blocked);

        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, PreviousImage = ImageKit.Id('x') });
        Assert.Equal("The image it ran before (xxxxxxxxxxxx) isn't on this host any more.", (await _updates.PrepareRollbackAsync("postgres")).Blocked);
    }

    [Fact]
    public async Task After_an_update_it_keeps_the_previous_image_and_removes_the_one_before_when_unused()
    {
        NewerOnDockerHub();
        var older = ImageKit.Id('x');
        _engine.Add(ImageKit.Image(older, []));
        var row = await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = older });

        var prepared = await _updates.PrepareAsync("postgres");
        var job = _updates.Start(prepared.Plan!);
        Assert.Equal(PortChangeOutcome.Done, (await job.Completion).Outcome);
        await _updates.FollowUpOf("postgres");

        Assert.Equal(Old, (await _db.AppAsync(row.Id)).PreviousImage);
        Assert.Contains($"remove {older}", _engine.Calls);
        Assert.DoesNotContain($"remove {Old}", _engine.Calls);
        Assert.Contains($"sync {row.Id}", _steps.Calls);
        // Its record in Activity is the engine's journal row: nothing is written twice.
        Assert.Empty(_steps.Activity);
        // The digest it pulled is what the registry has: the badge needn't wait for the next check.
        Assert.Equal(Remote, (await CheckOfAsync("docker.io/library/postgres:18"))!.RemoteDigest);
        Assert.True((await _updates.StatusAsync("postgres")).CanRollBack);
    }

    [Fact]
    public async Task The_image_before_the_previous_one_stays_while_a_container_uses_it()
    {
        NewerOnDockerHub();
        var older = ImageKit.Id('x');
        _engine.Add(ImageKit.Image(older, []));
        _engine.Uses("someone-else", older);
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = older });

        await (_updates.Start((await _updates.PrepareAsync("postgres")).Plan!)).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
        Assert.Contains(_engine.Images, i => (string)i["Id"]! == older);
    }

    [Fact]
    public async Task An_update_the_engine_rolled_back_changes_nothing_and_says_so()
    {
        NewerOnDockerHub();
        _steps.Result = new PortChangeResult(PortChangeOutcome.RolledBack, "postgres exited with code 1 within 15 s, so Gitberth put the original back.");

        var prepared = await _updates.PrepareAsync("postgres");
        var result = await _updates.Start(prepared.Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        await using var db = _db.CreateDbContext();
        Assert.Null((await db.Apps.SingleAsync()).PreviousImage);
        // The engine's journal row says it was put back.
        Assert.Empty(_steps.Activity);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("sync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_a_rollback_there_is_nothing_more_to_go_back_to()
    {
        var previous = ImageKit.Id('p');
        _engine.Add(ImageKit.Image(previous, []));
        var row = await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18", PreviousImage = previous });

        await _updates.Start((await _updates.PrepareRollbackAsync("postgres")).Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.Null((await _db.AppAsync(row.Id)).PreviousImage);
        Assert.Empty(_steps.Activity);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_after_a_rollback_removes_the_image_the_tag_moved_off()
    {
        // An update brought an image that was then rolled back: postgres runs Old again with nothing to go back to, and the
        // host's postgres:18 still names the rolled-back image (as on the real engine, where the trial saw it).
        var rolledBack = ImageKit.Id('b');
        _engine.Add(ImageKit.Image(rolledBack, [], ["postgres@" + ImageKit.Id('d')]));
        _engine.Tag("postgres:18", rolledBack);
        var row = await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18" });
        // The tag moved again on Docker Hub: the update's pull moves the host's postgres:18 onto the newer image.
        NewerOnDockerHub();

        await _updates.Start((await _updates.PrepareAsync("postgres")).Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        // Nameless and unused, nothing else would ever remove it; the image it ran stays for Roll back.
        Assert.Equal(Old, (await _db.AppAsync(row.Id)).PreviousImage);
        Assert.Contains($"remove {rolledBack}", _engine.Calls);
        Assert.DoesNotContain(_engine.Images, i => (string)i["Id"]! == rolledBack);
        Assert.DoesNotContain($"remove {Old}", _engine.Calls);
    }

    [Fact]
    public async Task The_image_the_tag_moved_off_stays_while_another_container_runs_it()
    {
        var rolledBack = ImageKit.Id('b');
        _engine.Add(ImageKit.Image(rolledBack, [], ["postgres@" + ImageKit.Id('d')]));
        _engine.Tag("postgres:18", rolledBack);
        _engine.Uses("postgres-replica", rolledBack, "postgres:18");
        await _db.AddAsync(new AppDefinition { Name = "postgres", Slug = "postgres", Container = "postgres", Source = AppSource.Existing, ImageRef = "postgres:18" });
        NewerOnDockerHub();

        await _updates.Start((await _updates.PrepareAsync("postgres")).Plan!).Completion;
        await _updates.FollowUpOf("postgres");

        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
        Assert.Contains(_engine.Images, i => (string)i["Id"]! == rolledBack);
    }
}
