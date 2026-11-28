using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>The rig's apps by their slugs, for the B2 and B3 tests.</summary>
internal static class RigApps
{
    public static int OrbOf(AppsRig rig)
    {
        using var db = rig.Db.CreateDbContext();
        return db.Apps.AsNoTracking().Single(a => a.Slug == "orb").Id;
    }
}

/// <summary>
/// Scheduled commands' schedules (B2): what the card's choices write, as one cron expression, read back into the choices,
/// said in words, and when each runs next in Gitberth's time zone.
/// </summary>
public sealed class JobScheduleTests
{
    [Fact]
    public void The_cards_choices_are_one_cron_expression_and_read_back_as_they_were()
    {
        var cases = new (JobScheduleForm Form, string Cron, string Words)[]
        {
            (new(JobEvery.Minutes, Minutes: 1), "* * * * *", "every minute"),
            (new(JobEvery.Minutes, Minutes: 15), "*/15 * * * *", "every 15 minutes"),
            (new(JobEvery.Hourly, Minute: 5), "5 * * * *", "hourly at :05"),
            (new(JobEvery.Daily, Minute: 30, Hour: 3), "30 3 * * *", "daily at 03:30"),
            (new(JobEvery.Weekly, Minute: 0, Hour: 22, Day: DayOfWeek.Sunday), "0 22 * * 0", "every Sunday at 22:00"),
            (new(JobEvery.Cron, Cron: "30 2 * * 1-5"), "30 2 * * 1-5", "cron 30 2 * * 1-5"),
        };
        foreach (var (form, cron, words) in cases)
        {
            Assert.Equal((cron, (string?)null), JobSchedules.ToCron(form));
            Assert.Equal(words, JobSchedules.Describe(cron));
            var back = JobSchedules.FormOf(cron);
            Assert.Equal(form.Every, back.Every);
            Assert.Equal(cron, JobSchedules.ToCron(back).Cron);
        }
        // Sunday as 7 is read back as Sunday; an expression the choices don't make stays one of its own.
        Assert.Equal((JobEvery.Weekly, DayOfWeek.Sunday), (JobSchedules.FormOf("0 4 * * 7").Every, JobSchedules.FormOf("0 4 * * 7").Day));
        Assert.Equal(JobEvery.Cron, JobSchedules.FormOf("0 4 1 * *").Every);
    }

    [Fact]
    public void A_number_out_of_range_or_a_bad_expression_is_refused_with_why()
    {
        Assert.Equal("Every 1 to 59 minutes: for longer, run it hourly or daily.", JobSchedules.ToCron(new(JobEvery.Minutes, Minutes: 60)).Problem);
        Assert.Equal("Every 1 to 59 minutes: for longer, run it hourly or daily.", JobSchedules.ToCron(new(JobEvery.Minutes, Minutes: 0)).Problem);
        Assert.Equal("The minute is from 0 to 59.", JobSchedules.ToCron(new(JobEvery.Hourly, Minute: 60)).Problem);
        Assert.Equal("The hour is from 0 to 23.", JobSchedules.ToCron(new(JobEvery.Daily, Hour: 24)).Problem);
        Assert.Equal("The hour is from 0 to 23.", JobSchedules.ToCron(new(JobEvery.Weekly, Hour: -1)).Problem);
        // An hourly schedule never asks for an hour.
        Assert.Equal("15 * * * *", JobSchedules.ToCron(new(JobEvery.Hourly, Minute: 15, Hour: 99)).Cron);
        var (cron, problem) = JobSchedules.ToCron(new(JobEvery.Cron, Cron: "0 3 1 * 1"));
        Assert.Null(cron);
        Assert.Equal("\"0 3 1 * 1\" sets both the day of the month and the day of the week, which isn't allowed: set one of them to *.", problem);
        Assert.NotNull(JobSchedules.ToCron(new(JobEvery.Cron, Cron: "0 3 * * MON")).Problem);
        Assert.NotNull(JobSchedules.ToCron(new(JobEvery.Cron, Cron: "")).Problem);
    }

    [Fact]
    public void The_next_run_is_in_gitberths_time_zone_and_a_time_the_clock_skips_is_left_out()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // 03:00 in Berlin is 02:00 UTC in winter (CET) and 01:00 UTC in summer (CEST).
        Assert.Equal(new DateTime(2026, 11, 29, 2, 0, 0, DateTimeKind.Utc), JobSchedules.NextUtc("0 3 * * *", new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc), berlin));
        Assert.Equal(new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc), JobSchedules.NextUtc("0 3 * * *", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), berlin));
        // 02:30 doesn't come on 29 March 2026 (the clocks go from 02:00 to 03:00): the 30th's is next, 00:30 UTC.
        Assert.Equal(new DateTime(2026, 3, 30, 0, 30, 0, DateTimeKind.Utc), JobSchedules.NextUtc("30 2 * * *", new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc), berlin));
        Assert.Equal(new DateTime(2026, 11, 28, 12, 5, 0, DateTimeKind.Utc), JobSchedules.NextUtc("*/5 * * * *", new DateTime(2026, 11, 28, 12, 0, 30, DateTimeKind.Utc), TimeZoneInfo.Utc));
        Assert.Null(JobSchedules.NextUtc("0 0 30 2 *", new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
        Assert.Equal(("UTC", "Europe/Berlin"), (JobSchedules.ZoneName(TimeZoneInfo.Utc), JobSchedules.ZoneName(berlin)));
        // Due once its next time after the last look has come, and not again until the next.
        var last = new DateTime(2026, 11, 28, 1, 59, 50, DateTimeKind.Utc);
        Assert.True(JobSchedules.Due("0 3 * * *", last, last.AddSeconds(15), berlin));
        Assert.False(JobSchedules.Due("0 3 * * *", last.AddSeconds(15), last.AddSeconds(30), berlin));
    }
}

/// <summary>
/// One-off commands (B2): a new container like the app's, with no ports, names or restart policy of its own and Gitberth's
/// helper label; a deploy's release command with its secrets through the docker CLI's environment; and a command in the
/// running container under timeout, its output masked, each ending said in a sentence.
/// </summary>
public sealed class OneOffRunnerTests
{
    private static readonly GitberthOptions Options = new() { DataDir = Path.GetTempPath() };

    [Fact]
    public void A_one_off_container_keeps_the_apps_settings_but_no_ports_names_restart_or_labels()
    {
        var app = JsonNode.Parse("""
            {
              "Image": "acme/api:2", "Env": ["A=1"], "Entrypoint": ["/entry"], "Cmd": ["serve"], "Hostname": "api", "ExposedPorts": {"8080/tcp": {}},
              "Labels": {"com.docker.compose.service": "api", "gitberth.managed": "true"}, "Healthcheck": {"Test": ["CMD", "curl", "-f", "http://localhost/"]},
              "HostConfig": {"PortBindings": {"8080/tcp": [{"HostPort": "3000"}]}, "RestartPolicy": {"Name": "unless-stopped"}, "Binds": ["data:/data"], "Memory": 268435456},
              "NetworkingConfig": {"EndpointsConfig": {
                "gitberth": {"Aliases": ["api"], "DNSNames": ["api"]},
                "lan": {"IPAMConfig": {"IPv4Address": "192.168.50.40"}, "Aliases": ["api"]}
              }}
            }
            """)!.AsObject();

        var body = OneOffRunner.OneOffBody(app, "npm run migrate");

        Assert.Equal(["/bin/sh", "-c"], body["Entrypoint"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["npm run migrate"], body["Cmd"]!.AsArray().Select(n => (string)n!));
        Assert.Null(body["ExposedPorts"]);
        Assert.Null(body["Hostname"]);
        Assert.Equal("""{"gitberth.helper":"command"}""", body["Labels"]!.ToJsonString());
        Assert.Equal("NONE", (string)body["Healthcheck"]!["Test"]![0]!);
        Assert.Null(body["HostConfig"]!["PortBindings"]);
        Assert.Equal("no", (string)body["HostConfig"]!["RestartPolicy"]!["Name"]!);
        // Its volumes and limits stay; on its networks it answers to none of the app's names, and the one where the app has
        // an address of its own is left out.
        Assert.Equal("data:/data", (string)body["HostConfig"]!["Binds"]![0]!);
        Assert.Equal(268435456L, (long)body["HostConfig"]!["Memory"]!);
        var endpoints = body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["gitberth"], endpoints.Select(e => e.Key));
        Assert.Null(endpoints["gitberth"]!["Aliases"]);
        Assert.Null(endpoints["gitberth"]!["DNSNames"]);
        // The app's own body is as it was.
        Assert.Equal("/entry", (string)app["Entrypoint"]![0]!);
    }

    [Fact]
    public void A_deploys_release_runs_without_ports_or_names_and_its_secrets_go_through_the_environment()
    {
        var app = new AppDefinition { Name = "api", Slug = "api", RestartPolicy = "unless-stopped", MemoryLimitMb = 256 };
        app.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 3000, IsWeb = true });
        app.Volumes.Add(new VolumeMount { Source = "data", Target = "/data" });
        app.Networks.Add(new AppNetwork { Network = "backend", Alias = "api-db" });
        var env = new List<EnvValue> { new("NODE_ENV", "production", false), new("API_KEY", "s3cr3t-api-key", true) };
        var spec = ContainerSpecFactory.For(app, "gitberth/api:abc1234", env, "gitberth", ports: [], restartPolicy: "no", bridge: true);

        var (args, processEnv) = OneOffRunner.RunArgs(spec, "gitberth-command-1", "npm run migrate");

        var line = string.Join(' ', args);
        Assert.StartsWith("run --name gitberth-command-1 --label gitberth.helper=command --restart no ", line);
        Assert.DoesNotContain("-p", args);
        Assert.DoesNotContain("-d", args);
        Assert.DoesNotContain("alias", line);
        Assert.DoesNotContain("s3cr3t-api-key", line);
        Assert.Equal("s3cr3t-api-key", processEnv["API_KEY"]);
        Assert.Contains("NODE_ENV=production", args);
        Assert.Contains(args, a => a.EndsWith(":/data", StringComparison.Ordinal));
        Assert.Equal(2, args.Count(a => a == "--network"));
        Assert.Contains("256m", args);
        Assert.Equal(["--entrypoint", "/bin/sh", "gitberth/api:abc1234", "-c", "npm run migrate"], args.TakeLast(5));
    }

    private static (OneOffRunner Runner, List<IReadOnlyList<string>> Calls) Runner(Func<IReadOnlyList<string>, ProcessOptions?, CancellationToken, Task<ProcessResult>> answer)
    {
        var calls = new List<IReadOnlyList<string>>();
        var cli = new DockerCli(Options)
        {
            Runner = (args, options, ct) =>
            {
                lock (calls) calls.Add(args);
                return answer(args, options, ct);
            },
        };
        return (new OneOffRunner(cli, Options, NullLogger<OneOffRunner>.Instance), calls);
    }

    [Fact]
    public async Task A_command_in_the_running_container_runs_under_timeout_with_its_output_masked()
    {
        var exit = 0;
        var (runner, calls) = Runner((args, options, _) =>
        {
            if (args[0] == "exec")
            {
                options!.OnLine!("cleaning up");
                options.OnLine!("with s3cr3t-api-key");
            }
            return Task.FromResult(new ProcessResult(exit, "", ""));
        });
        var mask = new OutputMask(["s3cr3t-api-key"]);

        var ok = await runner.ExecAsync("gb-api", "npm run cleanup", TimeSpan.FromMinutes(2), mask, null, CancellationToken.None);
        Assert.Equal((CommandRunStatus.Succeeded, (int?)0, $"cleaning up\nwith {Security.Masking.Mask}\n"), (ok.Status, ok.ExitCode, ok.Output));
        // The command is a word of its own after the script: never put into it.
        Assert.Equal(["exec", "gb-api", "/bin/sh", "-c", OneOffRunner.ExecScript, "120", "npm run cleanup"], calls.Single());

        exit = 3;
        var failed = await runner.ExecAsync("gb-api", "npm run cleanup", TimeSpan.FromMinutes(2), mask, null, CancellationToken.None);
        Assert.Equal((CommandRunStatus.Failed, (int?)3, (string?)null), (failed.Status, failed.ExitCode, failed.Error));
        // timeout's kill.
        exit = 137;
        Assert.Equal(CommandRunStatus.TimedOut, (await runner.ExecAsync("gb-api", "sleep 999", TimeSpan.FromMinutes(1), mask, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_container_that_isnt_running_or_has_no_shell_is_said_in_a_sentence()
    {
        var stderr = "Error response from daemon: container 4f2a is not running";
        var (runner, _) = Runner((_, _, _) => Task.FromResult(new ProcessResult(1, "", stderr)));

        var stopped = await runner.ExecAsync("gb-api", "true", TimeSpan.FromMinutes(1), OutputMask.None, null, CancellationToken.None);
        Assert.Equal((CommandRunStatus.Failed, "gb-api isn't running, so nothing can run in it."), (stopped.Status, stopped.Error));

        stderr = "OCI runtime exec failed: exec failed: unable to start container process: exec: \"/bin/sh\": stat /bin/sh: no such file or directory: unknown";
        (runner, _) = Runner((_, _, _) => Task.FromResult(new ProcessResult(127, "", stderr)));
        var noShell = await runner.ExecAsync("gb-api", "true", TimeSpan.FromMinutes(1), OutputMask.None, null, CancellationToken.None);
        Assert.Equal("There's no shell (/bin/sh) in the image, so a command can't run in it.", noShell.Error);
    }

    [Fact]
    public async Task A_release_that_runs_too_long_is_stopped_and_its_container_removed()
    {
        var (runner, calls) = Runner(async (args, _, ct) =>
        {
            if (args[0] == "run") await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });
        var app = new AppDefinition { Name = "api", Slug = "api" };
        var spec = ContainerSpecFactory.For(app, "gitberth/api:abc1234", [], "gitberth", ports: [], restartPolicy: "no", bridge: true);

        var result = await runner.RunSpecAsync(spec, "sleep 999", TimeSpan.FromMilliseconds(200), null, CancellationToken.None);

        Assert.Equal(CommandRunStatus.TimedOut, result.Status);
        var name = calls[0][2];
        Assert.Equal(["rm", "-f", name], calls[^1]);
    }
}

/// <summary>
/// Release commands (B2): saved on the app (a container without a row gets one), audited with its secrets hidden; a
/// deploy's runs in a one-off container of the new version, and one that fails stops the deploy with why; the change
/// engine's runs only for apps no deploy brings up.
/// </summary>
public sealed class ReleaseCommandTests : IDisposable
{
    private readonly AppsRig _rig = new();
    private int? _orbId;

    // The rig's orb app (a Git app the rig's database starts with).
    private int OrbId => _orbId ??= RigApps.OrbOf(_rig);
    private readonly AuditLog _audit = new();
    private readonly List<IReadOnlyList<string>> _calls = [];
    private int _exit;
    private readonly ReleaseCommands _release;

    public ReleaseCommandTests()
    {
        var cli = new DockerCli(_rig.Options)
        {
            Runner = (args, options, ct) =>
            {
                lock (_calls) _calls.Add(args);
                if (args[0] != "run") return _rig.Engine.RunAsync(args, options, ct);
                options?.OnLine?.Invoke("migrating 3 tables");
                return Task.FromResult(new ProcessResult(_exit, "", ""));
            },
        };
        _release = new ReleaseCommands(_rig.Db, new OneOffRunner(cli, _rig.Options, NullLogger<OneOffRunner>.Instance, _rig.Api),
            NullLogger<ReleaseCommands>.Instance, new Lazy<AppCatalog>(() => _rig.Catalog), _audit);
    }

    public void Dispose()
    {
        _audit.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public void It_runs_when_the_image_changes_and_never_on_a_restart_with_the_same_image()
    {
        var app = new AppDefinition { Name = "orb", Slug = "orb", CurrentImage = "gitberth/orb:1d2948c", ReleaseCommand = "npm run migrate" };
        Assert.True(ReleaseCommands.Runs(app, build: true, "gitberth/orb:1d2948c"));
        Assert.True(ReleaseCommands.Runs(app, build: false, "gitberth/orb:0a1b2c3"));
        Assert.False(ReleaseCommands.Runs(app, build: false, "gitberth/orb:1d2948c"));
        app.ReleaseCommand = null;
        Assert.False(ReleaseCommands.Runs(app, build: true, "gitberth/orb:0a1b2c3"));
        Assert.Equal(TimeSpan.FromMinutes(10), ReleaseCommands.TimeoutOf(app));
        Assert.Equal("Write the release command on one line: join several with &&.", ReleaseCommands.Problem("a\nb", null));
        Assert.Equal("The release command's time limit is from 1 to 60 minutes.", ReleaseCommands.Problem("true", 61));
    }

    [Fact]
    public async Task Saving_it_is_audited_with_its_secrets_hidden_and_a_container_without_a_row_gets_one()
    {
        var saved = await _release.SaveAsync("gb-orb", "app migrate --token s3cr3t-api-key", 10, service: "web");
        Assert.True(saved.Ok, saved.Message);
        var row = await _rig.RowAsync(a => a.Id == OrbId);
        // Ten minutes is the default, kept as none; a service is only a Compose app's.
        Assert.Equal(("app migrate --token s3cr3t-api-key", (int?)null, (string?)null), (row!.ReleaseCommand, row.ReleaseTimeoutMinutes, row.ReleaseService));
        var entry = Assert.Single(_audit.Entries, e => e.Action == "app.release.changed");
        Assert.Equal("Set orb's release command.", entry.Summary);
        Assert.Equal(new AuditChange("Release command", null, $"app migrate --token {Security.Masking.Mask}"), Assert.Single(entry.Changes));

        Assert.Equal("Nothing changed.", (await _release.SaveAsync("gb-orb", " app migrate --token s3cr3t-api-key ", null, null)).Message);
        Assert.Equal("Saved: no release command.", (await _release.SaveAsync("gb-orb", "", 30, null)).Message);
        Assert.Equal("Removed orb's release command.", _audit.Entries[^1].Summary);
        Assert.False((await _release.SaveAsync("gb-orb", "true", 0, null)).Ok);

        // A container Gitberth keeps nothing about yet gets its row first.
        Assert.True((await _release.SaveAsync("sample-index", "php artisan migrate --force", 5, null)).Ok);
        var adopted = await _rig.RowAsync(a => a.Container == "sample-index");
        Assert.Equal(("php artisan migrate --force", (int?)5), (adopted!.ReleaseCommand, adopted.ReleaseTimeoutMinutes));
    }

    private DeployLog Log(out string path)
    {
        path = Path.Combine(_rig.Options.DataDir, $"release-{Guid.NewGuid():N}.log");
        return new DeployLog(path, new LiveLogHub().Open(1), []);
    }

    [Fact]
    public async Task A_deploys_release_that_fails_stops_it_and_says_the_running_version_stays()
    {
        var app = new AppDefinition { Name = "orb", Slug = "orb", CurrentImage = "gitberth/orb:1d2948c", ReleaseCommand = "npm run migrate" };
        var spec = ContainerSpecFactory.For(app, "gitberth/orb:0a1b2c3", [], "gitberth", ports: [], restartPolicy: "no", bridge: true);

        _exit = 0;
        var log = Log(out var okPath);
        await _release.RunForDeployAsync(app, spec, log, CancellationToken.None);
        log.Dispose();
        var text = await File.ReadAllTextAsync(okPath);
        Assert.Contains("Running the release command in the new version, before it takes over: npm run migrate", text);
        Assert.Contains("migrating 3 tables", text);
        Assert.Contains("The release command finished (exit code 0).", text);

        _exit = 3;
        log = Log(out _);
        var failed = await Assert.ThrowsAsync<DeployException>(() => _release.RunForDeployAsync(app, spec, log, CancellationToken.None));
        log.Dispose();
        Assert.Equal("The release command failed (exit code 3), so orb keeps running its current version. Its output is in the log above.", failed.Message);
        // Each run's container went, whatever its end; nothing of the running one was touched.
        var runs = _calls.Where(c => c[0] == "run").Select(c => c[2]).ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, name => Assert.Contains(_calls, c => c.SequenceEqual(["rm", "-f", name])));
        Assert.DoesNotContain(_calls, c => c[0] is "stop" or "rename" || c[0] == "rm" && !runs.Contains(c[^1]));
    }

    [Fact]
    public async Task The_change_engine_runs_it_only_for_an_app_no_deploy_brings_up_and_a_refusal_changes_nothing()
    {
        var body = new JsonObject { ["Image"] = "nginx:1.27", ["Env"] = new JsonArray("API_TOKEN=s3cr3t-api-key") };
        // A Git app's release command is its deploys': the change engine leaves it be.
        await _release.SaveAsync("gb-orb", "npm run migrate", null, null);
        Assert.Null(await _release.BeforeImageChangeAsync("gb-orb", body, CancellationToken.None));
        // No command, nothing to run.
        Assert.Null(await _release.BeforeImageChangeAsync("postgres", body, CancellationToken.None));
        // An image app's runs in a container of the new image: Docker refusing it means nothing changed.
        await _release.SaveAsync("sample-index", "php artisan migrate --force", null, null);
        var refused = await _release.BeforeImageChangeAsync("sample-index", body, CancellationToken.None);
        Assert.StartsWith("Nothing changed: its release command couldn't run (Docker refused the container to run it in", refused);
        Assert.Contains(_rig.Engine.Writes, w => w.StartsWith("engine POST", StringComparison.Ordinal) && w.Contains("/containers/create", StringComparison.Ordinal));
        Assert.Equal(["s3cr3t-api-key"], ReleaseCommands.SecretValues(body, null));
    }
}

/// <summary>
/// Scheduled commands (B2): added, changed and removed with the audit log; Run now and the schedule run them in the app's
/// container, written down from the start; one due while the last still runs is skipped; a stopped app's wait; a failure
/// is told to Notifications and Activity, and passing again once; the newest 20 runs are kept; a run cut short by a
/// restart is settled.
/// </summary>
public sealed class CommandJobsTests : IDisposable
{
    private readonly AppsRig _rig = new();
    private int? _orbId;

    // The rig's orb app (a Git app the rig's database starts with).
    private int OrbId => _orbId ??= RigApps.OrbOf(_rig);
    private readonly AuditLog _audit = new();
    private readonly NotificationSettings _choices;
    private readonly Notifier _notifier;
    private readonly List<string> _commands = [];
    private Func<IReadOnlyList<string>, ProcessOptions?, Task<ProcessResult>> _exec = (_, _) => Task.FromResult(new ProcessResult(0, "", ""));
    private readonly CommandJobs _jobs;

    public CommandJobsTests()
    {
        _choices = new NotificationSettings(_rig.Settings);
        _notifier = new Notifier(_choices, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), _rig.Settings, NullLogger<Notifier>.Instance);
        var cli = new DockerCli(_rig.Options)
        {
            Runner = async (args, options, ct) =>
            {
                lock (_commands) _commands.Add(string.Join(' ', args));
                return args[0] == "exec" ? await _exec(args, options) : await _rig.Engine.RunAsync(args, options, ct);
            },
        };
        var runner = new OneOffRunner(cli, _rig.Options, NullLogger<OneOffRunner>.Instance, _rig.Api);
        _jobs = new CommandJobs(_rig.Db, runner, _rig.Api, _rig.Activity, _notifier, NullLogger<CommandJobs>.Instance, new Lazy<AppCatalog>(() => _rig.Catalog), _audit);
    }

    public void Dispose()
    {
        _jobs.WhenIdleAsync().Wait(TimeSpan.FromSeconds(10));
        _notifier.Dispose();
        _audit.Dispose();
        _rig.Dispose();
    }

    private static JobInput Input(string name = "Nightly cleanup", string schedule = "0 3 * * *", string command = "npm run cleanup") =>
        new(name, schedule, command, CommandPlace.Exec, null, 30);

    private List<Notice> Notices()
    {
        var list = new List<Notice>();
        while (_notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    private async Task<List<ActivityEntry>> ActivityAsync()
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.Activity.AsNoTracking().Where(a => a.AppId == OrbId).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task Adding_changing_and_removing_a_command_is_audited_and_what_cant_be_saved_says_why()
    {
        var added = await _jobs.AddAsync("gb-orb", Input());
        Assert.True(added.Ok, added.Message);
        Assert.Equal($"Added Nightly cleanup: it runs daily at 03:00 ({JobSchedules.ZoneName()}).", added.Message);
        var entry = Assert.Single(_audit.Entries, e => e.Action == "app.job.added");
        Assert.Equal((OrbId, "Nightly cleanup"), (entry.AppId, entry.Target));

        Assert.Equal("Write the command on one line: join several with &&.", (await _jobs.AddAsync("gb-orb", Input(command: "a\nb"))).Message);
        Assert.Equal("Give the command a name, like Nightly cleanup.", (await _jobs.AddAsync("gb-orb", Input(name: " "))).Message);
        Assert.False((await _jobs.AddAsync("gb-orb", Input(schedule: "61 * * * *"))).Ok);
        Assert.Equal("The time limit is from 1 minute to 24 hours (1440 minutes).", (await _jobs.AddAsync("gb-orb", Input() with { TimeoutMinutes = 0 })).Message);

        // A secret in the command is hidden in the audit log.
        var changed = await _jobs.ChangeAsync(added.CommandId!.Value, Input(schedule: "*/15 * * * *", command: "app-cleanup --token s3cr3t-api-key"));
        Assert.True(changed.Ok, changed.Message);
        var change = Assert.Single(_audit.Entries, e => e.Action == "app.job.changed");
        Assert.Contains(new AuditChange("Schedule", "daily at 03:00", "every 15 minutes"), change.Changes);
        Assert.Contains(new AuditChange("Command", "npm run cleanup", $"app-cleanup --token {Security.Masking.Mask}"), change.Changes);
        Assert.Equal("Nothing changed.", (await _jobs.ChangeAsync(added.CommandId.Value, Input(schedule: "*/15 * * * *", command: "app-cleanup --token s3cr3t-api-key"))).Message);

        Assert.True((await _jobs.RemoveAsync(added.CommandId.Value)).Ok);
        Assert.Single(_audit.Entries, e => e.Action == "app.job.removed");
        Assert.Empty(await _jobs.ListAsync(OrbId));

        // A container Gitberth keeps nothing about yet gets its row on its first command.
        Assert.True((await _jobs.AddAsync("sample-index", Input())).Ok);
        Assert.NotNull(await _rig.RowAsync(a => a.Container == "sample-index"));
    }

    [Fact]
    public async Task Run_now_runs_in_the_container_its_output_masked_and_a_failure_is_told_once_and_passing_again_too()
    {
        await _choices.SaveDestinationAsync(new Destination { Id = "d1", Kind = DestinationKind.Webhook, Name = "Ops", Url = "https://hooks.example.com/gitberth" });
        // The app marks one of its variables secret: its value is hidden in what a command prints.
        await using (var db = _rig.Db.CreateDbContext())
        {
            var orb = await db.Apps.SingleAsync(a => a.Id == OrbId);
            orb.SecretKeys = """["VERCEL_GIT_REPO_OWNER"]""";
            await db.SaveChangesAsync();
        }
        var id = (await _jobs.AddAsync("gb-orb", Input("Report", command: "make report"))).CommandId!.Value;
        var gate = new TaskCompletionSource();
        _exec = async (_, options) =>
        {
            options!.OnLine!("report for octocat");
            await gate.Task;
            return new ProcessResult(7, "", "");
        };

        var ran = await _jobs.RunNowAsync(id);
        Assert.True(ran.Ok, ran.Message);
        // Written down at once, as running; a second Run now waits for it.
        Assert.True((await _jobs.ListAsync(OrbId)).Single().Running);
        Assert.Equal("Report is running now: wait for it to end.", (await _jobs.RunNowAsync(id)).Message);
        gate.SetResult();
        await _jobs.WhenIdleAsync();

        var run = (await _jobs.ListAsync(OrbId)).Single().Last!;
        Assert.Equal((CommandRunStatus.Failed, (int?)7, CommandTrigger.RunNow), (run.Status, run.ExitCode, run.Trigger));
        Assert.Equal($"report for {Security.Masking.Mask}\n", await _jobs.OutputAsync(run.Id));
        Assert.Equal($"exec gb-orb /bin/sh -c {OneOffRunner.ExecScript} 1800 make report", _commands.Single(c => c.StartsWith("exec ", StringComparison.Ordinal)));
        var notice = Assert.Single(Notices());
        Assert.Equal((NotifyEvent.JobFailed, NoticeLevel.Critical, "Scheduled command Report of orb failed"), (notice.Event, notice.Level, notice.Title));
        Assert.Contains(await ActivityAsync(), a => a.Kind == ActivityKind.Job && a.Text.StartsWith("Scheduled command Report of orb failed", StringComparison.Ordinal));
        Assert.Single(_audit.Entries, e => e.Action == "app.job.run");

        // Passing again is told once; passing after that isn't.
        _exec = (_, _) => Task.FromResult(new ProcessResult(0, "", ""));
        foreach (var _ in Enumerable.Range(0, 2))
        {
            Assert.True((await _jobs.RunNowAsync(id)).Ok);
            await _jobs.WhenIdleAsync();
        }
        Assert.Equal([NotifyEvent.JobOk], Notices().Select(n => n.Event));
    }

    [Fact]
    public async Task A_run_due_while_the_last_still_runs_is_skipped_and_a_stopped_apps_commands_wait()
    {
        var gate = new TaskCompletionSource();
        _exec = async (_, _) =>
        {
            await gate.Task;
            return new ProcessResult(0, "", "");
        };
        await _jobs.AddAsync("gb-orb", Input("Tick", "* * * * *", "true"));
        var t0 = new DateTime(2026, 11, 28, 12, 0, 50, DateTimeKind.Utc);

        Assert.Equal(1, await _jobs.RunDueAsync(t0, t0.AddSeconds(15), CancellationToken.None));
        Assert.Equal(0, await _jobs.RunDueAsync(t0.AddSeconds(15), t0.AddSeconds(30), CancellationToken.None));
        // The next minute's comes while it still runs: skipped.
        Assert.Equal(0, await _jobs.RunDueAsync(t0.AddSeconds(60), t0.AddSeconds(75), CancellationToken.None));
        gate.SetResult();
        await _jobs.WhenIdleAsync();
        var runs = (await _jobs.ListAsync(OrbId)).Single().Runs;
        Assert.Equal([CommandRunStatus.Skipped, CommandRunStatus.Succeeded], runs.Select(r => r.Status));
        Assert.Equal(CommandTrigger.Schedule, runs[1].Trigger);

        // A stopped app's commands don't run, and aren't written down as skipped either.
        await using (var db = _rig.Db.CreateDbContext())
        {
            var orb = await db.Apps.SingleAsync(a => a.Id == OrbId);
            orb.DesiredState = DesiredState.Stopped;
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await _jobs.RunDueAsync(t0.AddSeconds(120), t0.AddSeconds(135), CancellationToken.None));
        Assert.Equal(2, (await _jobs.ListAsync(OrbId)).Single().Runs.Count);
    }

    [Fact]
    public async Task The_newest_twenty_runs_are_kept_and_a_run_cut_short_by_a_restart_is_settled()
    {
        var id = (await _jobs.AddAsync("gb-orb", Input())).CommandId!.Value;
        for (var i = 0; i < CommandJobs.KeptRuns + 2; i++)
        {
            Assert.True((await _jobs.RunNowAsync(id)).Ok);
            await _jobs.WhenIdleAsync();
        }
        Assert.Equal(CommandJobs.KeptRuns, (await _jobs.ListAsync(OrbId)).Single().Runs.Count);

        await using (var db = _rig.Db.CreateDbContext())
        {
            db.CommandRuns.Add(new CommandRun { CommandId = id, AppId = OrbId, Trigger = CommandTrigger.Schedule, Status = CommandRunStatus.Running });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await _jobs.SettleAsync(CancellationToken.None));
        var last = (await _jobs.ListAsync(OrbId)).Single().Last!;
        Assert.Equal((CommandRunStatus.Failed, "Gitberth restarted while it ran, so how it ended isn't known."), (last.Status, last.Error));
    }
}
