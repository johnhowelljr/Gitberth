using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Gitberth updating itself (Settings › Updates › Gitberth, src/Gitberth/Images/UpdateGitberth*.cs): versions, whether it
/// can, the new container's settings, the updater's swap and its way back, the check and what it tells, and an app's newer
/// image told to whoever chose Tell me.
/// </summary>
public class UpdateGitberthTests
{
    private const string Published = UpdateGitberthRules.PublishedImage;

    // ---- the rules ----

    [Theory]
    [InlineData("v1.10.0", "1.10.0")]
    [InlineData("1.10.0", "1.10.0")]
    [InlineData("v1.10", null)]
    [InlineData("v1.10.0-rc.1", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("latest", null)]
    public void A_release_tag_is_a_version_only_when_it_names_one(string tag, string? version) =>
        Assert.Equal(version, UpdateGitberthRules.VersionOf(tag));

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.9.0", "1.9.0", false)]
    [InlineData("1.8.2", "1.9.0", false)]
    [InlineData("1.9.0", "1.9.0-rc.1", true)]
    [InlineData(null, "1.9.0", false)]
    [InlineData("v2.0.0", "1.9.0", true)]
    public void Newer_compares_versions_and_a_pre_release_comes_before_its_release(string? latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateGitberthRules.Newer(latest, current));

    [Fact]
    public void Gitberth_replaces_itself_only_from_its_published_image_in_a_container()
    {
        Assert.Contains("isn't running in a container", UpdateGitberthRules.CantUpdate(false, null, Published));
        Assert.Contains("isn't its published image", UpdateGitberthRules.CantUpdate(true, "gitberth:local", Published));
        Assert.Contains("pinned by its digest", UpdateGitberthRules.CantUpdate(true, $"{Published}@sha256:{new string('a', 64)}", Published));
        Assert.Null(UpdateGitberthRules.CantUpdate(true, $"{Published}:latest", Published));
        Assert.Null(UpdateGitberthRules.CantUpdate(true, $"{Published}:1.9.0", Published));
        // The end-to-end rig names its own (GITBERTH_UPDATE_IMAGE).
        Assert.Null(UpdateGitberthRules.CantUpdate(true, "gitberth:e2e", "gitberth"));
    }

    [Fact]
    public void Latest_stays_latest_and_a_pinned_version_moves_to_the_new_one()
    {
        Assert.Equal(($"{Published}:1.10.0", $"{Published}:latest", true), UpdateGitberthRules.Target($"{Published}:latest", "1.10.0"));
        Assert.Equal(($"{Published}:1.10.0", $"{Published}:latest", true), UpdateGitberthRules.Target(Published, "1.10.0"));
        Assert.Equal(($"{Published}:1.10.0", $"{Published}:1.10.0", false), UpdateGitberthRules.Target($"{Published}:1.9", "1.10.0"));
    }

    [Fact]
    public void Composes_env_moves_only_the_line_that_pins_the_old_image()
    {
        var env = $"TZ=UTC\r\nGITBERTH_IMAGE=\"{Published}:1.9.0\"\r\nGITBERTH_PORT=8080\r\n";
        Assert.Equal($"TZ=UTC\r\nGITBERTH_IMAGE={Published}:1.10.0\r\nGITBERTH_PORT=8080\r\n",
            UpdateGitberthRules.EnvWith(env, $"{Published}:1.9.0", $"{Published}:1.10.0"));
        // It pins something else, or nothing: nothing is written.
        Assert.Null(UpdateGitberthRules.EnvWith($"GITBERTH_IMAGE={Published}:1.8\n", $"{Published}:1.9.0", $"{Published}:1.10.0"));
        Assert.Null(UpdateGitberthRules.EnvWith("TZ=UTC\n", $"{Published}:1.9.0", $"{Published}:1.10.0"));
    }

    [Fact]
    public void The_new_container_keeps_the_old_ones_settings_but_not_what_its_image_gave_it()
    {
        var inspect = OldGitberth();
        var image = JsonNode.Parse("""
            {"Config":{"Env":["PATH=/usr/bin","ASPNETCORE_HTTP_PORTS=8080"],"Labels":{"org.opencontainers.image.version":"1.9.0"},
              "Entrypoint":["dotnet","Gitberth.dll"],"ExposedPorts":{"8080/tcp":{}}}}
            """)!.AsObject();

        var body = UpdateGitberthRules.CreateBody(inspect, image, $"{Published}:1.10.0");

        Assert.Equal($"{Published}:1.10.0", (string?)body["Image"]);
        Assert.Equal(["TZ=UTC", "GITBERTH_PUBLIC_HOST=192.0.2.10"], body["Env"]!.AsArray().Select(e => (string?)e));
        var labels = body["Labels"]!.AsObject();
        Assert.Equal("gitberth", (string?)labels["com.docker.compose.project"]);
        Assert.False(labels.ContainsKey("org.opencontainers.image.version"));
        Assert.Null(body["Entrypoint"]);
        Assert.Empty(body["ExposedPorts"]!.AsObject());
        // Docker's made-up hostname and MAC address go; the host settings come as they were.
        Assert.Null(body["Hostname"]);
        Assert.Null(body["MacAddress"]);
        Assert.Equal("unless-stopped", (string?)body["HostConfig"]!["RestartPolicy"]!["Name"]);
        var endpoints = body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["gitberth"], endpoints["gitberth_default"]!["Aliases"]!.AsArray().Select(a => (string?)a));
        Assert.Equal("10.89.0.3", (string?)endpoints["lan"]!["IPAMConfig"]!["IPv4Address"]);

        // On the host's network there's no endpoint to give.
        inspect["HostConfig"]!["NetworkMode"] = "host";
        Assert.Null(UpdateGitberthRules.CreateBody(inspect, image, $"{Published}:1.10.0")["NetworkingConfig"]);
    }

    [Fact]
    public void The_newest_copies_of_the_data_are_kept()
    {
        var at = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var copies = new[] { ("gitberth-data-before-1.6.0", at), ("gitberth-data-before-1.7.0", at.AddDays(5)), ("gitberth-data-before-1.8.0", at.AddDays(9)),
            ("gitberth-data-before-1.9.0", at.AddDays(20)) };
        Assert.Equal(["gitberth-data-before-1.7.0", "gitberth-data-before-1.6.0"], UpdateGitberthRules.CopiesToRemove(copies, "gitberth-data-before-1.9.0"));
        Assert.Equal("gitberth-data-before-1.9.0", UpdateGitberthRules.CopyName("1.9.0"));
        Assert.Equal("gitberth-before-1.9.0-rc-1", UpdateGitberthRules.BeforeName("1.9.0+rc/1"));
    }

    [Fact]
    public void Release_notes_show_no_html_no_pictures_and_only_web_links()
    {
        var html = UpdateGitberthRules.NotesHtml("""
            ## Fixes
            <script>alert(1)</script>
            [a link](javascript:alert(1)) ![a picture](https://example.com/p.png) [the release](https://github.com/johnhowelljr/Gitberth/releases)
            """);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("href=\"https://example.com/p.png\"", html);
        Assert.Contains("href=\"https://github.com/johnhowelljr/Gitberth/releases\" rel=\"noopener noreferrer\" target=\"_blank\"", html);
    }

    [Fact]
    public void A_release_is_read_from_githubs_answer_but_never_a_draft_or_a_pre_release()
    {
        var json = JsonNode.Parse("""
            {"tag_name":"v1.10.0","name":"Gitberth 1.10.0","body":"## New\n- Updates itself","html_url":"https://github.com/johnhowelljr/Gitberth/releases/tag/v1.10.0",
             "published_at":"2026-11-20T09:30:00Z","draft":false,"prerelease":false}
            """)!.AsObject();
        var release = UpdateGitberth.ReleaseOf(json)!;
        Assert.Equal(("1.10.0", "v1.10.0", "Gitberth 1.10.0"), (release.Version, release.Tag, release.Name));
        Assert.Equal(new DateTime(2026, 11, 20, 9, 30, 0, DateTimeKind.Utc), release.PublishedAt);
        json["prerelease"] = true;
        Assert.Null(UpdateGitberth.ReleaseOf(json));
        json["prerelease"] = false;
        json["tag_name"] = "nightly";
        Assert.Null(UpdateGitberth.ReleaseOf(json));
    }

    // ---- the updater ----

    [Fact]
    public async Task The_updater_swaps_the_containers_and_the_old_one_goes_once_the_new_one_answers()
    {
        var docker = new FakeDocker(OldGitberth());
        (string, string)? compose = null;
        var updater = Updater(docker, url => url.Contains("172.18.0.9") ? "1.10.0" : null, compose: (old, image) => compose = (old, image));

        var result = await updater.RunAsync(Plan(compose: true), default);

        Assert.True(result.Ok, result.Why);
        Assert.Equal(["stop gitberth", "rename gitberth gitberth-before-1.9.0", $"create gitberth {Published}:1.10.0", "start gitberth", "remove gitberth-before-1.9.0"], docker.Calls);
        Assert.Equal(($"{Published}:1.9.0", $"{Published}:1.10.0"), compose);
        Assert.Equal(0, docker.Restored);
    }

    [Fact]
    public async Task A_new_version_that_stops_is_removed_and_the_old_one_comes_back_with_its_data()
    {
        var docker = new FakeDocker(OldGitberth()) { NewRuns = false, NewLogs = "Starting Gitberth…\nUnhandled exception. The database is newer than this Gitberth.\n" };
        var updater = Updater(docker, url => url.Contains("172.18.0.2") ? "1.9.0" : null);

        var result = await updater.RunAsync(Plan(), default);

        Assert.False(result.Ok);
        Assert.Equal("It stopped right after it started (exit code 3): Unhandled exception. The database is newer than this Gitberth.", result.Why);
        Assert.Equal(["stop gitberth", "rename gitberth gitberth-before-1.9.0", $"create gitberth {Published}:1.10.0", "start gitberth", "stop gitberth", "remove gitberth",
            "rename gitberth-before-1.9.0 gitberth", "start gitberth"], docker.Calls);
        Assert.Equal(1, docker.Restored);
        Assert.Equal("The update to 1.10.0 failed and Gitberth went back to 1.9.0: It stopped right after it started (exit code 3): Unhandled exception. The database is newer than this Gitberth.",
            UpdateGitberthRules.Said(result));
    }

    [Fact]
    public async Task A_new_version_its_restart_policy_keeps_starting_counts_as_stopped()
    {
        // Docker says a container in a restart loop is running, and restarting.
        var docker = new FakeDocker(OldGitberth()) { NewRestarts = true, NewLogs = "Gitberth 9.9.2 refuses to start.\n" };

        var result = await Updater(docker, url => url.Contains("172.18.0.2") ? "1.9.0" : null).RunAsync(Plan(), default);

        Assert.Equal((false, "It stopped right after it started (exit code 3): Gitberth 9.9.2 refuses to start."), (result.Ok, result.Why));
        Assert.Equal(1, docker.Restored);
    }
    [Fact]
    public async Task A_new_version_that_answers_as_another_goes_too()
    {
        var docker = new FakeDocker(OldGitberth());
        var updater = Updater(docker, _ => "1.9.0");

        var result = await updater.RunAsync(Plan(), default);

        Assert.Equal((false, "It answers as version 1.9.0, not 1.10.0."), (result.Ok, result.Why));
        Assert.Equal("start gitberth", docker.Calls[^1]);
        Assert.Equal(1, docker.Restored);
    }

    [Fact]
    public async Task An_old_gitberth_that_cant_be_put_aside_runs_again_as_it_was()
    {
        var docker = new FakeDocker(OldGitberth()) { RenameFails = true };

        var result = await Updater(docker, _ => null).RunAsync(Plan(), default);

        Assert.False(result.Ok);
        Assert.StartsWith("The old Gitberth couldn't be put aside (", result.Why);
        Assert.Equal(["stop gitberth", "start gitberth"], docker.Calls);
        Assert.Equal(0, docker.Restored);
    }

    // ---- the check, the status and how an update ended ----

    [Fact]
    public async Task A_new_version_is_told_once_and_not_at_all_when_set_to_do_nothing()
    {
        using var rig = new AutoRig();
        await rig.TellAsync();
        var service = Service(rig, _ => Task.FromResult<GitberthRelease?>(Release("1.10.0")));

        var state = await service.CheckAsync();

        Assert.Equal(("1.10.0", "1.10.0"), (state.Latest?.Version, state.ToldVersion));
        var told = Assert.Single(rig.Notices());
        Assert.Equal((NotifyEvent.UpdateAvailable, "Gitberth 1.10.0 is out"), (told.Event, told.Title));
        await service.CheckAsync();
        Assert.Empty(rig.Notices());
        var status = await service.StatusAsync();
        Assert.True(status.Newer);
        Assert.Contains("isn't running in a container", status.CantUpdate);
        // Update now says why not, and changes nothing.
        Assert.Equal(status.CantUpdate, await service.UpdateNowAsync());

        // The same Gitberth, set to Do nothing, then 1.11.0 out: nothing told.
        var quiet = Service(rig, _ => Task.FromResult<GitberthRelease?>(Release("1.11.0")));
        await quiet.SetPolicyAsync(GitberthUpdatePolicy.Nothing);
        Assert.Equal(("1.11.0", "1.10.0"), ((await quiet.CheckAsync()).Latest?.Version, (await quiet.StatusAsync()).State.ToldVersion));
        Assert.Empty(rig.Notices());
        Assert.Contains(rig.Audit.Entries, e => e.Action == "settings.updates.gitberth.changed" && e.Summary == "When a new version of Gitberth comes out: do nothing.");
    }

    [Fact]
    public async Task A_check_that_fails_says_why_and_keeps_what_it_knew()
    {
        using var rig = new AutoRig();
        var answers = new Queue<Func<GitberthRelease?>>([() => Release("1.10.0"), () => throw new HttpRequestException("GitHub answered 503 Service Unavailable")]);
        var service = Service(rig, _ => Task.FromResult(answers.Dequeue()()));

        await service.CheckAsync();
        var state = await service.CheckAsync();

        Assert.Equal(("GitHub answered 503 Service Unavailable.", "1.10.0"), (state.CheckProblem, state.Latest?.Version));
        Assert.Equal(rig.Clock.Now.UtcDateTime, state.CheckedAt);
    }

    [Fact]
    public async Task How_the_last_update_ended_is_read_once_recorded_and_told()
    {
        using var rig = new AutoRig();
        await rig.TellAsync();
        var service = Service(rig, _ => Task.FromResult<GitberthRelease?>(null), version: "1.9.0");
        var dir = Path.Combine(rig.Host.Options.DataDir, "update");
        Directory.CreateDirectory(dir);
        var failed = new GitberthUpdateResult("1.9.0", "1.10.0", false, "It didn't answer within 3 minutes.", rig.Clock.Now.UtcDateTime, "admin");
        await File.WriteAllTextAsync(Path.Combine(dir, "result.json"), JsonSerializer.Serialize(failed, GitberthUpdateState.Json));
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), "{}");

        Assert.True(await service.ReadResultAsync(default));
        Assert.False(await service.ReadResultAsync(default));

        Assert.False(File.Exists(Path.Combine(dir, "result.json")) || File.Exists(Path.Combine(dir, "plan.json")));
        Assert.Equal(failed, (await service.StatusAsync()).State.LastResult);
        var entry = rig.Audit.Entries.Single(e => e.Action == "settings.updates.gitberth.failed");
        Assert.Equal("The update to 1.10.0 failed and Gitberth went back to 1.9.0: It didn't answer within 3 minutes.", entry.Summary);
        var told = Assert.Single(rig.Notices());
        Assert.Equal((NotifyEvent.GitberthUpdateFailed, NoticeLevel.Critical), (told.Event, told.Level));
        Assert.Contains("went back to 1.9.0, with its data as it was before", told.Text);
    }

    [Fact]
    public async Task A_newer_image_of_an_app_set_to_tell_me_is_told_once_and_a_muted_one_isnt()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var protector = ImageKit.Protector();
        var clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var settings = new SettingsService(db, protector);
        var notify = new NotificationSettings(settings);
        await notify.SaveDestinationAsync(new Destination { Id = "d1", Kind = DestinationKind.Webhook, Name = "Ops", Url = "https://hooks.example.com/gitberth" });
        await notify.SetMutedAsync("pg-copy", true);
        var notifier = new Notifier(notify, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), settings, NullLogger<Notifier>.Instance);
        var updates = new UpdateService(new ImageKit.UpdateSteps(db), ImageKit.Images(engine, db, protector), db, NullLogger<UpdateService>.Instance)
        {
            Time = clock, Notifier = notifier,
        };
        engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());
        engine.Distribution["docker.io/library/postgres:18"] = () => JsonNode.Parse(ImageKit.Fixture("Images", "distribution-postgres-18.json"))!.AsObject();
        engine.Uses("postgres", "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", "postgres:18");
        engine.Uses("pg-copy", "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", "docker.io/library/postgres:18");
        await using (var context = db.CreateDbContext())
        {
            context.ImageChecks.Add(new ImageCheck
            {
                Reference = "docker.io/library/postgres:18", RemoteDigest = "sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81",
                CheckedAt = clock.Now.UtcDateTime.AddHours(-7),
            });
            await context.SaveChangesAsync();
        }

        await updates.CheckDueAsync(TimeSpan.FromHours(6), default);
        clock.Now += TimeSpan.FromHours(7);
        await updates.CheckDueAsync(TimeSpan.FromHours(6), default);

        var told = new List<Notice>();
        while (notifier.Queued.TryRead(out var n)) told.Add(n);
        var notice = Assert.Single(told);
        Assert.Equal((NotifyEvent.UpdateAvailable, "postgres", "A newer image of postgres is out"), (notice.Event, notice.Container, notice.Title));
        Assert.StartsWith("postgres:18 has a newer image (e3d2…).", notice.Text);
    }

    // ---- helpers ----

    private static GitberthRelease Release(string version) =>
        new(version, "v" + version, "Gitberth " + version, "- Better", $"https://github.com/johnhowelljr/Gitberth/releases/tag/v{version}", new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));

    // Never Docker itself: not in a container, so nothing asks it, and an address where nothing listens should anything try.
    private static UpdateGitberth Service(AutoRig rig, Func<CancellationToken, Task<GitberthRelease?>> latest, string version = "1.9.0")
    {
        var options = new GitberthOptions { DataDir = rig.Host.Options.DataDir, RunningInContainer = false, DockerHost = "tcp://127.0.0.1:9" };
        return new UpdateGitberth(rig.Settings, new ImageKit.NoHttp(), new DockerApi(options), options, rig.Notifier, rig.Host.Db, rig.Backup, rig.Auto, rig.Host.Gate,
            rig.Restore, NullLogger<UpdateGitberth>.Instance, rig.Audit, rig.Clock)
        {
            Latest = latest, Version = version,
        };
    }

    private static GitberthUpdatePlan Plan(bool compose = false) => new()
    {
        Container = "0ld0123456789abcdef", Name = "gitberth", Image = $"{Published}:1.10.0", From = "1.9.0", To = "1.10.0",
        CopyVolume = "gitberth-data-before-1.9.0", Network = "gitberth_default", Port = 8080,
        ComposeImage = compose ? $"{Published}:1.10.0" : null, ComposeOld = compose ? $"{Published}:1.9.0" : null, By = "admin",
    };

    private static GitberthUpdater Updater(FakeDocker docker, Func<string, string?> version, Action<string, string>? compose = null) =>
        new(docker, (url, _) => Task.FromResult(version(url)), _ =>
        {
            docker.Restored++;
            return Task.FromResult<string?>(null);
        }, (old, image, _) =>
        {
            compose?.Invoke(old, image);
            return Task.CompletedTask;
        }, new StringWriter())
        {
            AnswerWithin = TimeSpan.FromSeconds(2), AskEvery = TimeSpan.FromMilliseconds(20),
        };

    private static JsonObject OldGitberth() => JsonNode.Parse("""
        {"Id":"0ld0123456789abcdef","Name":"/gitberth","Image":"sha256:01d","State":{"Running":true,"ExitCode":0},
         "Config":{"Hostname":"0ld012345678","Image":"PUBLISHED:1.9.0","MacAddress":"02:42:ac:12:00:02",
           "Env":["PATH=/usr/bin","ASPNETCORE_HTTP_PORTS=8080","TZ=UTC","GITBERTH_PUBLIC_HOST=192.0.2.10"],
           "Labels":{"com.docker.compose.project":"gitberth","org.opencontainers.image.version":"1.9.0"},
           "Entrypoint":["dotnet","Gitberth.dll"],"ExposedPorts":{"8080/tcp":{}}},
         "HostConfig":{"NetworkMode":"gitberth_default","RestartPolicy":{"Name":"unless-stopped"},"Binds":["/var/run/docker.sock:/var/run/docker.sock"]},
         "NetworkSettings":{"Networks":{
           "gitberth_default":{"IPAddress":"172.18.0.2","Aliases":["gitberth","0ld012345678"]},
           "lan":{"IPAddress":"10.89.0.3","Aliases":["gitberth"],"IPAMConfig":{"IPv4Address":"10.89.0.3"}}}}}
        """.Replace("PUBLISHED", Published))!.AsObject();

    /// <summary>Docker as the updater sees it: Gitberth's container, and the one it makes; each call written down by name.</summary>
    private sealed class FakeDocker(JsonObject old) : IUpdaterDocker
    {
        private readonly Dictionary<string, JsonObject> _containers = new(StringComparer.Ordinal) { [(string)old["Id"]!] = old };

        public List<string> Calls { get; } = [];
        public int Restored { get; set; }
        public bool NewRuns { get; init; } = true;
        public string NewLogs { get; init; } = "";
        public bool RenameFails { get; init; }
        public bool NewRestarts { get; init; }

        private JsonObject? Find(string idOrName) =>
            _containers.TryGetValue(idOrName, out var c) ? c : _containers.Values.FirstOrDefault(c => (string?)c["Name"] == "/" + idOrName);

        private string NameOf(string id) => ((string)Find(id)!["Name"]!).TrimStart('/');

        public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => Task.FromResult(Find(idOrName)?.DeepClone().AsObject());

        public Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct) =>
            Task.FromResult<JsonObject?>(JsonNode.Parse("""{"Config":{"Env":["PATH=/usr/bin","ASPNETCORE_HTTP_PORTS=8080"]}}""")!.AsObject());

        public Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add("stop " + NameOf(idOrName));
            Find(idOrName)!["State"]!["Running"] = false;
            return Task.CompletedTask;
        }

        public Task RenameContainerAsync(string idOrName, string name, CancellationToken ct)
        {
            if (RenameFails) throw new DockerApiException(409, "Conflict. The container name is already in use");
            Calls.Add($"rename {NameOf(idOrName)} {name}");
            Find(idOrName)!["Name"] = "/" + name;
            return Task.CompletedTask;
        }

        public Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct)
        {
            Calls.Add($"create {name} {(string?)body["Image"]}");
            const string id = "new0123456789abcdef";
            _containers[id] = new JsonObject
            {
                ["Id"] = id, ["Name"] = "/" + name, ["State"] = new JsonObject { ["Running"] = false, ["ExitCode"] = 0 },
                ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["gitberth_default"] = new JsonObject { ["IPAddress"] = "172.18.0.9" } } },
            };
            return Task.FromResult(id);
        }

        public Task StartContainerAsync(string idOrName, CancellationToken ct)
        {
            Calls.Add("start " + NameOf(idOrName));
            var c = Find(idOrName)!;
            var isNew = ((string)c["Id"]!).StartsWith("new", StringComparison.Ordinal);
            c["State"] = new JsonObject { ["Running"] = !isNew || NewRuns || NewRestarts, ["Restarting"] = isNew && NewRestarts, ["ExitCode"] = isNew && (!NewRuns || NewRestarts) ? 3 : 0 };
            if (isNew && NewRestarts) c["RestartCount"] = 2;
            return Task.CompletedTask;
        }

        public Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct)
        {
            Calls.Add("remove " + NameOf(idOrName));
            _containers.Remove((string)Find(idOrName)!["Id"]!);
            return Task.CompletedTask;
        }

        public Task<byte[]> ContainerLogsAsync(string idOrName, int tail, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetBytes(NewLogs));
    }
}
