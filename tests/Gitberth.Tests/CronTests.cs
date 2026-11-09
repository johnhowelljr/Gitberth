using System.Globalization;
using System.Net;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

public class CronScheduleTests
{
    private static CronSchedule Parse(string expression)
    {
        Assert.True(CronSchedule.TryParse(expression, out var schedule, out var error), error);
        return schedule;
    }

    private static DateTime Utc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Theory]
    [InlineData("* * * * *", "2026-11-04T10:17", true)]
    [InlineData("*/5 * * * *", "2026-11-04T10:15", true)]
    [InlineData("*/5 * * * *", "2026-11-04T10:17", false)]
    [InlineData("0 5 * * *", "2026-11-04T05:00", true)]
    [InlineData("0 5 * * *", "2026-11-04T05:01", false)]
    [InlineData("0 5 * * *", "2026-11-04T06:00", false)]
    [InlineData("1-10/2 * * * *", "2026-11-04T10:09", true)]
    [InlineData("1-10/2 * * * *", "2026-11-04T10:10", false)]
    [InlineData("5/15 * * * *", "2026-11-04T10:50", true)] // 5 to 59, every 15: 5, 20, 35, 50
    [InlineData("5/15 * * * *", "2026-11-04T10:00", false)]
    [InlineData("59/2147483647 * * * *", "2026-11-04T10:59", true)] // a huge step doesn't wrap round
    [InlineData("59/2147483647 * * * *", "2026-11-04T10:57", false)]
    [InlineData("0,30 9-17 * * *", "2026-11-04T17:30", true)]
    [InlineData("0,30 9-17 * * *", "2026-11-04T18:00", false)]
    [InlineData("0 0 1,15 * *", "2026-11-15T00:00", true)]
    [InlineData("0 0 1,15 * *", "2026-11-14T00:00", false)]
    [InlineData("0 0 * 2 *", "2026-11-01T00:00", false)]
    [InlineData("0 0 * */3 *", "2026-10-01T00:00", true)] // January, April, July, October
    [InlineData("0 12 * * 1-5", "2026-11-04T12:00", true)] // a Wednesday
    [InlineData("0 12 * * 1-5", "2026-11-08T12:00", false)] // a Sunday
    [InlineData("0 0 * * 0", "2026-11-08T00:00", true)]
    [InlineData("0 0 * * 7", "2026-11-08T00:00", true)] // 7 is Sunday too
    [InlineData("0 0 * * 7", "2026-11-07T00:00", false)]
    [InlineData("0 0 * * 5-7", "2026-11-08T00:00", true)] // Friday to Sunday
    [InlineData("0 0 * * 5-7", "2026-11-09T00:00", false)]
    public void Matches_the_minutes_a_job_runs_in(string expression, string utc, bool expected) =>
        Assert.Equal(expected, Parse(expression).Matches(Utc(utc)));

    [Theory]
    [InlineData("", "The schedule is empty.")]
    [InlineData("0 5 * *", "needs five fields")]
    [InlineData("0 5 * * * 2026", "needs five fields")]
    [InlineData("@daily", "needs five fields")]
    [InlineData("60 * * * *", "There's no minute 60: minutes go from 0 to 59.")]
    [InlineData("* 24 * * *", "There's no hour 24")]
    [InlineData("* * 0 * *", "There's no day 0 of the month")]
    [InlineData("* * 32 * *", "There's no day 32 of the month")]
    [InlineData("* * * 0 *", "There's no month 0")]
    [InlineData("* * * 13 *", "There's no month 13")]
    [InlineData("* * * * 8", "There's no day 8 of the week")]
    [InlineData("0 5 * * MON", "\"MON\" isn't supported: write days of the week as numbers")]
    [InlineData("0 5 * * 1-FRI", "\"1-FRI\" isn't supported")]
    [InlineData("0 5 * JAN *", "\"JAN\" isn't supported: write months as numbers")]
    [InlineData("0 5 1 * 1", "sets both the day of the month and the day of the week")]
    [InlineData("0 5 */2 * 1-5", "sets both the day of the month and the day of the week")]
    [InlineData("*/0 * * * *", "The step in \"*/0\" must be at least 1.")]
    [InlineData("5-1 * * * *", "\"5-1\" goes backwards")]
    [InlineData("1,,2 * * * *", "\"1,,2\" isn't a valid minute.")]
    [InlineData("-1 * * * *", "isn't a valid minute")]
    [InlineData("1-5/x * * * *", "isn't a valid minute")]
    [InlineData("L * * * *", "isn't a valid minute")]
    [InlineData("0 0 ? * *", "isn't a valid day of the month")]
    public void Says_in_plain_words_why_a_schedule_is_rejected(string expression, string because)
    {
        Assert.False(CronSchedule.TryParse(expression, out var schedule, out var error));
        Assert.Null(schedule);
        Assert.Contains(because, error);
    }

    [Theory]
    [InlineData("0 6 * * *", "2026-11-04T10:17:30", "2026-11-05T06:00")]
    [InlineData("0 6 * * *", "2026-11-04T05:59:59", "2026-11-04T06:00")]
    [InlineData("0 6 * * *", "2026-11-04T06:00:00", "2026-11-05T06:00")] // the run in 06:00 is this one
    [InlineData("*/15 * * * *", "2026-11-04T10:17:00", "2026-11-04T10:30")]
    [InlineData("59 23 * * *", "2026-12-31T23:59:30", "2027-01-01T23:59")]
    [InlineData("0 0 1 * *", "2026-12-15T08:00:00", "2027-01-01T00:00")]
    [InlineData("30 9 * * 1", "2026-11-04T10:00:00", "2026-11-09T09:30")] // the next Monday
    [InlineData("0 0 * * 7", "2026-11-04T10:00:00", "2026-11-08T00:00")]
    [InlineData("0 12 * 6 *", "2026-11-04T00:00:00", "2027-06-01T12:00")]
    [InlineData("0 0 29 2 *", "2026-11-04T00:00:00", "2028-02-29T00:00")] // the next leap day
    public void Works_out_the_next_run(string expression, string after, string expected)
    {
        var next = Parse(expression).Next(Utc(after));
        Assert.Equal(Utc(expected), next);
        Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
    }

    [Fact]
    public void A_day_that_never_comes_has_no_next_run()
    {
        Assert.Null(Parse("0 0 30 2 *").Next(Utc("2026-11-04T00:00")));
        Assert.Null(Parse("0 0 31 4,6,9,11 *").Next(Utc("2026-11-04T00:00")));
    }

    [Theory]
    [InlineData("0 6 * * *", "every day at 06:00 UTC")]
    [InlineData("30 9 * * 1-5", "Monday to Friday at 09:30 UTC")]
    [InlineData("0 0 * * 0", "every Sunday at 00:00 UTC")]
    [InlineData("0 0 * * 7", "every Sunday at 00:00 UTC")]
    [InlineData("0 8 * * 1,3,5", "every Monday, Wednesday and Friday at 08:00 UTC")]
    [InlineData("0 6,18 * * *", "every day at 06:00 and 18:00 UTC")]
    [InlineData("0 8 1 * *", "on the 1st of every month at 08:00 UTC")]
    [InlineData("15 4 22 * *", "on the 22nd of every month at 04:15 UTC")]
    [InlineData("0 0 1 1 *", "every year on 1 January at 00:00 UTC")]
    [InlineData("0 7 * 12 *", "every day in December at 07:00 UTC")]
    [InlineData("* * * * *", "every minute")]
    [InlineData("*/5 * * * *", "every 5 minutes")]
    [InlineData("0 * * * *", "every hour")]
    [InlineData("15 * * * *", "every hour at :15")]
    [InlineData("0 */6 * * *", "every 6 hours")]
    [InlineData("1-10/2 3 * * *", null)]
    [InlineData("*/5 * * * 1", null)]
    [InlineData("0 9-17 * * *", null)]
    public void Describes_common_schedules(string expression, string? expected) =>
        Assert.Equal(expected, Parse(expression).Describe());
}

public class CronJobsTests
{
    private const string Hello = """{ "path": "/api/hello", "schedule": "0 5 * * *" }""";

    // Kanban's layout: the root vercel.json points at website/, which has its own, and both list the same cron.
    private static TempRepo Kanban() => new TempRepo()
        .With("vercel.json", $$"""{ "rootDirectory": "website", "installCommand": "cd .. && npm ci", "crons": [{{Hello}}] }""")
        .With("website/vercel.json", $$"""{ "framework": "nextjs", "crons": [{{Hello}}] }""")
        .With("website/package.json", "{}");

    [Fact]
    public void Kanban_gets_its_cron_from_its_folders_vercel_json()
    {
        using var repo = Kanban();
        var (label, warnings) = CronJobs.ForBuild(repo.Root, "website");

        Assert.Equal("""[{"path":"/api/hello","schedule":"0 5 * * *"}]""", label);
        Assert.Empty(warnings);
        // The root's vercel.json describes website/, so an app at the root doesn't get its jobs.
        var (rootLabel, rootWarnings) = CronJobs.ForBuild(repo.Root, "");
        Assert.Null(rootLabel);
        Assert.Empty(rootWarnings);
    }

    [Fact]
    public void The_root_vercel_json_counts_only_for_the_folder_its_root_directory_points_at()
    {
        using var repo = new TempRepo()
            .With("vercel.json", $$"""{ "rootDirectory": "./website/", "crons": [{{Hello}}] }""")
            .With("website/package.json", "{}")
            .With("admin/package.json", "{}")
            .With("docs/vercel.json", """{ "crons": [{ "path": "/api/docs", "schedule": "0 0 * * *" }] }""");

        Assert.Equal([new CronJob("/api/hello", "0 5 * * *")], CronJobs.Read(repo.Root, "website/").Jobs);
        Assert.Empty(CronJobs.Read(repo.Root, "admin").Jobs);
        Assert.Empty(CronJobs.Read(repo.Root, "").Jobs);
        // A folder with its own vercel.json uses that one, as Vercel does.
        Assert.Equal([new CronJob("/api/docs", "0 0 * * *")], CronJobs.Read(repo.Root, "docs").Jobs);
    }

    [Fact]
    public void A_root_vercel_json_without_a_root_directory_belongs_to_the_root_app()
    {
        using var repo = new TempRepo().With("vercel.json", $$"""{ "crons": [{{Hello}}] }""").With("api/package.json", "{}");
        Assert.Single(CronJobs.Read(repo.Root, "").Jobs);
        Assert.Empty(CronJobs.Read(repo.Root, "api").Jobs);
    }

    [Fact]
    public void Leaves_out_entries_it_cannot_use_and_says_why()
    {
        using var repo = new TempRepo().With("vercel.json", """
            {
              "crons": [
                { "path": "/api/daily", "schedule": "0 6 * * *" },
                { "path": "/api/daily", "schedule": "0 18 * * *" },
                { "path": "api/no-slash", "schedule": "0 6 * * *" },
                { "path": "/api/short", "schedule": "0 6 * *" },
                { "path": "/api/both", "schedule": "0 6 1 * 1" },
                { "path": "/api/named", "schedule": "0 6 * * MON" },
                { "schedule": "0 6 * * *" },
                { "path": "/api/no-schedule" },
                { "path": "/api/daily", "schedule": "0 6 * * *" }
              ]
            }
            """);
        var (jobs, warnings) = CronJobs.Read(repo.Root, "");

        // Two jobs can share a path, each on its own schedule.
        Assert.Equal([new CronJob("/api/daily", "0 6 * * *"), new CronJob("/api/daily", "0 18 * * *")], jobs);
        Assert.Equal(
        [
            "The cron job api/no-slash in vercel.json isn't scheduled. Its path must start with /.",
            "The cron job /api/short in vercel.json isn't scheduled. \"0 6 * *\" needs five fields: minute, hour, day of the month, month and day of the week.",
            "The cron job /api/both in vercel.json isn't scheduled. \"0 6 1 * 1\" sets both the day of the month and the day of the week, which Vercel doesn't allow: set one of them to *.",
            "The cron job /api/named in vercel.json isn't scheduled. \"MON\" isn't supported: write days of the week as numbers, from 0 (Sunday) to 6 (Saturday).",
            "The cron job #7 in vercel.json isn't scheduled. It has no path.",
            "The cron job /api/no-schedule in vercel.json isn't scheduled. It has no schedule.",
            "The cron job /api/daily (0 6 * * *) is listed twice in vercel.json. It runs once.",
        ], warnings);
    }

    [Fact]
    public void A_crons_value_that_is_not_a_list_or_a_file_that_is_not_json_is_reported()
    {
        using var repo = new TempRepo().With("vercel.json", """{ "crons": { "path": "/api/x" } }""").With("web/vercel.json", "{ not json");
        Assert.Equal(["\"crons\" in vercel.json isn't a list, so none of it is scheduled."], CronJobs.Read(repo.Root, "").Warnings);
        var (jobs, warnings) = CronJobs.Read(repo.Root, "web");
        Assert.Empty(jobs);
        Assert.StartsWith("web/vercel.json couldn't be read as JSON", Assert.Single(warnings));
    }

    [Fact]
    public void An_app_without_crons_gets_no_label_and_no_warnings()
    {
        using var repo = new TempRepo().With("vercel.json", """{ "buildCommand": "npm run build" }""").With("api/package.json", "{}");
        foreach (var dir in new[] { "", "api", "missing" })
        {
            var (label, warnings) = CronJobs.ForBuild(repo.Root, dir);
            Assert.Null(label);
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public void Keeps_at_most_a_hundred_jobs()
    {
        var crons = string.Join(",", Enumerable.Range(0, 101).Select(i => $$"""{ "path": "/api/{{i}}", "schedule": "0 5 * * *" }"""));
        using var repo = new TempRepo().With("vercel.json", $$"""{ "crons": [{{crons}}] }""");
        var (jobs, warnings) = CronJobs.Read(repo.Root, "");
        Assert.Equal(100, jobs.Count);
        Assert.Equal("vercel.json lists more than 100 cron jobs. Only the first 100 are scheduled.", Assert.Single(warnings));
    }

    [Fact]
    public void The_label_is_compact_json_that_reads_back_the_same()
    {
        CronJob[] jobs = [new("/api/hello", "0 5 * * *"), new("/api/report?kind=daily&format=<csv>", "*/15 * * * *")];
        var label = CronJobs.ToLabel(jobs);
        Assert.StartsWith("""[{"path":"/api/hello","schedule":"0 5 * * *"},{"path":""", label);
        Assert.DoesNotContain(" ", label.Replace("0 5 * * *", "").Replace("*/15 * * * *", ""));
        Assert.Equal(jobs, CronJobs.FromLabel(label));

        // As `docker image inspect` prints the image's labels: the value as a string inside JSON.
        var inspect = JsonSerializer.Serialize(new Dictionary<string, string> { ["gitberth.app"] = "web", [ImageLabels.Crons] = label });
        Assert.Equal(jobs, CronJobs.FromLabel(DockerCli.ParseLabelsJson(inspect)[ImageLabels.Crons]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"path":"/api/x","schedule":"0 5 * * *"}""")]
    [InlineData("""[{"path":"api/x","schedule":"0 5 * * *"},{"path":"/api/y","schedule":"0 5 1 * 1"},{"path":"/api/z"}]""")]
    public void A_label_with_nothing_usable_has_no_jobs(string? label) => Assert.Empty(CronJobs.FromLabel(label));
}

/// <summary>
/// The scheduler against a stand-in docker (the app's running container, its image's labels and its CRON_SECRET) and a
/// stand-in app that answers over HTTP.
/// </summary>
public sealed class CronSchedulerTests : IDisposable
{
    private const string Hello = """[{"path":"/api/hello","schedule":"* * * * *"}]""";
    private static readonly DateTime Six = new(2026, 11, 4, 6, 0, 0, DateTimeKind.Utc);

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>What a request carried, read as it arrived.</summary>
    private sealed record Seen(string Method, string Url, string? Authorization, string UserAgent, string? Schedule);

    private sealed class AppStub : HttpMessageHandler
    {
        private readonly List<Seen> _requests = [];

        /// <summary>When set, requests wait for it, like a job that takes a while.</summary>
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<Seen> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var schedule = request.Headers.TryGetValues("x-vercel-cron-schedule", out var values) ? string.Join(",", values) : null;
            lock (_requests)
                _requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), schedule));
            Arrived.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
            return Respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly AppStub _app = new();
    private readonly int _appId;

    public CronSchedulerTests()
    {
        Directory.CreateDirectory(_dir);
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using var db = _db.CreateDbContext();
        db.Database.EnsureCreated();
        var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "acme", Repo = "web", CurrentImage = "gitberth/web:abc1234" };
        app.Ports.Add(new PortMapping { HostPort = 3001, ContainerPort = 3000, IsWeb = true });
        db.Apps.Add(app);
        db.SaveChanges();
        _appId = app.Id;
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A scheduler whose docker shows the app's container running, its image labelled with these crons, and this CRON_SECRET.</summary>
    private CronScheduler Scheduler(string? crons = Hello, string? secret = "s3cret")
    {
        var options = new GitberthOptions { DataDir = _dir, DockerPath = WriteFakeDocker(_dir, crons, secret) };
        return new CronScheduler(_db, new DockerCli(options), new Factory(_app), options, NullLogger<CronScheduler>.Instance);
    }

    private static string WriteFakeDocker(string dir, string? crons, string? secret)
    {
        const string ps = """{"Names":"gb-web","State":"running","Status":"Up 1 hour","Image":"gitberth/web:abc1234","Ports":"0.0.0.0:3001->3000/tcp, [::]:3001->3000/tcp","Labels":"gitberth.managed=true,gitberth.app=web","Command":"node server.js"}""";
        var labels = new Dictionary<string, string> { ["gitberth.app"] = "web" };
        if (crons is not null) labels[ImageLabels.Crons] = crons;
        // Quotes inside the values come out as ", so cmd sees nothing it would act on.
        var inspect = JsonSerializer.Serialize(labels);
        string[] env = secret is null ? [] : ["CRON_SECRET=" + secret];
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, "docker.cmd");
            File.WriteAllLines(cmd,
            [
                "@echo off",
                ">>\"%~dp0calls.log\" echo %~1 %~2",
                "if \"%~1\"==\"ps\" (", "echo " + ps, "exit /b 0", ")",
                "if \"%~1\"==\"image\" (", "echo " + inspect, "exit /b 0", ")",
                "if \"%~1\"==\"container\" (", .. env.Select(line => "echo " + line), "exit /b 0", ")",
                "exit /b 1",
            ]);
            return cmd;
        }
        var sh = Path.Combine(dir, "docker");
        File.WriteAllLines(sh,
        [
            "#!/bin/sh",
            "echo \"$1 $2\" >> \"$(dirname \"$0\")/calls.log\"",
            "case \"$1\" in",
            $"ps) printf '%s\\n' '{ps}' ;;",
            $"image) printf '%s\\n' '{inspect}' ;;",
            $"container) {(secret is null ? "true" : $"printf '%s\\n' '{env[0]}'")} ;;",
            "*) exit 1 ;;",
            "esac",
        ]);
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private string DockerCalls() => File.Exists(Path.Combine(_dir, "calls.log")) ? File.ReadAllText(Path.Combine(_dir, "calls.log")) : "";

    private static async Task<CronRun[]> Finished(Task<List<Task<CronRun>>> tick) => await Task.WhenAll(await tick).WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Calls_a_due_job_the_way_vercel_does()
    {
        var scheduler = Scheduler();
        var run = Assert.Single(await Finished(scheduler.TickAsync(Six, CancellationToken.None)));

        Assert.Equal(new Seen("GET", "http://localhost:3001/api/hello", "Bearer s3cret", "vercel-cron/1.0", "* * * * *"), Assert.Single(_app.Requests));
        Assert.Equal((CronOutcome.Answered, (int?)200, (string?)null, false), (run.Outcome, run.Status, run.Detail, run.Manual));
        Assert.Contains("container inspect", DockerCalls());
        var job = Assert.Single((await scheduler.GetAsync(_appId)).Jobs);
        Assert.Equal(("/api/hello", "* * * * *", "every minute"), (job.Path, job.Schedule, job.Description));
        Assert.Equal(run, job.Last);
        Assert.Null(job.RunningSince);
        Assert.True(job.NextRun > DateTime.UtcNow);
    }

    [Fact]
    public async Task Without_a_cron_secret_there_is_no_authorization_header()
    {
        await Finished(Scheduler(secret: null).TickAsync(Six, CancellationToken.None));
        var seen = Assert.Single(_app.Requests);
        Assert.Null(seen.Authorization);
        Assert.Equal(("vercel-cron/1.0", "* * * * *"), (seen.UserAgent, seen.Schedule));
    }

    [Fact]
    public async Task Only_the_jobs_due_in_that_minute_run()
    {
        var scheduler = Scheduler("""[{"path":"/api/morning","schedule":"0 6 * * *"},{"path":"/api/evening","schedule":"0 18 * * *"}]""");
        await Finished(scheduler.TickAsync(Six, CancellationToken.None));
        Assert.Equal(["http://localhost:3001/api/morning"], _app.Requests.Select(r => r.Url));

        Assert.Empty(await scheduler.TickAsync(Six.AddMinutes(1), CancellationToken.None));
        Assert.Single(_app.Requests);
    }

    [Fact]
    public async Task A_redirect_ends_the_run()
    {
        _app.Respond = _ => new HttpResponseMessage(HttpStatusCode.PermanentRedirect) { Headers = { Location = new Uri("https://web.example.com/api/hello") } };
        var run = Assert.Single(await Finished(Scheduler().TickAsync(Six, CancellationToken.None)));

        Assert.Single(_app.Requests);
        Assert.Equal((CronOutcome.Answered, (int?)308, false), (run.Outcome, run.Status, run.Succeeded));
        Assert.Equal("Redirected to https://web.example.com/api/hello. Cron jobs don't follow redirects.", run.Detail);
    }

    [Fact]
    public async Task A_run_is_skipped_while_the_jobs_previous_run_is_still_going()
    {
        var scheduler = Scheduler();
        _app.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.RunNowAsync(_appId, "/api/hello", "* * * * *");
        await _app.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(Assert.Single((await scheduler.GetAsync(_appId)).Jobs).RunningSince);

        var skipped = Assert.Single(await Finished(scheduler.TickAsync(Six, CancellationToken.None)));
        var again = await scheduler.RunNowAsync(_appId, "/api/hello", "* * * * *");
        Assert.Equal((CronOutcome.Skipped, false, "The previous run was still going."), (skipped.Outcome, skipped.Manual, skipped.Detail));
        Assert.Equal((CronOutcome.Skipped, true), (again.Outcome, again.Manual));
        Assert.Single(_app.Requests);

        _app.Hold.SetResult();
        var done = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((CronOutcome.Answered, (int?)200, true), (done.Outcome, done.Status, done.Manual));
        var job = Assert.Single((await scheduler.GetAsync(_appId)).Jobs);
        Assert.Null(job.RunningSince);
        Assert.Equal([done, again, skipped], job.Recent);
    }

    [Fact]
    public async Task The_secret_never_shows_in_what_is_kept()
    {
        _app.Respond = _ => throw new HttpRequestException("The app said: Bearer s3cret isn't right");
        var run = Assert.Single(await Finished(Scheduler().TickAsync(Six, CancellationToken.None)));
        Assert.Equal((CronOutcome.NoAnswer, "The app said: Bearer •••• isn't right"), (run.Outcome, run.Detail));
    }

    [Fact]
    public async Task An_app_without_a_tcp_port_shows_that_its_jobs_cannot_be_called()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Ports.RemoveRange(db.Ports);
            db.Ports.Add(new PortMapping { AppId = _appId, HostPort = 5353, ContainerPort = 53, Protocol = "udp" });
            db.SaveChanges();
        }
        var scheduler = Scheduler();
        var run = Assert.Single(await Finished(scheduler.TickAsync(Six, CancellationToken.None)));

        Assert.Equal((CronOutcome.NotCalled, CronScheduler.NoPort), (run.Outcome, run.Detail));
        Assert.Empty(_app.Requests);
        Assert.Equal(CronScheduler.NoPort, (await scheduler.GetAsync(_appId)).Problem);
        Assert.Equal(CronOutcome.NotCalled, (await scheduler.RunNowAsync(_appId, "/api/hello", "* * * * *")).Outcome);
    }

    [Fact]
    public async Task A_stopped_app_runs_nothing()
    {
        using (var db = _db.CreateDbContext())
            db.Apps.Where(a => a.Id == _appId).ExecuteUpdate(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));
        var scheduler = Scheduler();

        Assert.Empty(await scheduler.TickAsync(Six, CancellationToken.None));
        Assert.Empty(_app.Requests);
        Assert.Equal("The app is stopped, so its jobs don't run until you start it.", (await scheduler.GetAsync(_appId)).Problem);
    }

    [Fact]
    public async Task An_image_without_crons_shows_no_jobs_and_calls_nothing()
    {
        var scheduler = Scheduler(crons: null);
        Assert.Empty(await scheduler.TickAsync(Six, CancellationToken.None));
        Assert.Same(AppCronJobs.None, await scheduler.GetAsync(_appId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunNowAsync(_appId, "/api/hello", "* * * * *"));
        Assert.Empty(_app.Requests);
        Assert.DoesNotContain("container", DockerCalls());
    }

    [Fact]
    public void Calls_the_app_by_name_inside_a_container_without_a_published_port()
    {
        // The web port, which isn't the first TCP one here.
        var app = new AppDefinition
        {
            Slug = "web",
            Ports =
            [
                new PortMapping { Id = 1, HostPort = 5353, ContainerPort = 53, Protocol = "udp" },
                new PortMapping { Id = 2, HostPort = 9001, ContainerPort = 9000 },
                new PortMapping { Id = 3, HostPort = 3001, ContainerPort = 3000, IsWeb = true },
            ],
        };
        var running = Container("running", "0.0.0.0:5353->53/udp, 0.0.0.0:9001->9000/tcp, 0.0.0.0:3001->3000/tcp, [::]:3001->3000/tcp");

        Assert.Equal(("http://gb-web:3000", (string?)null), CronScheduler.BaseUrl(app, running, inContainer: true));
        Assert.Equal(("http://localhost:3001", (string?)null), CronScheduler.BaseUrl(app, running, inContainer: false));

        // An Internal web port has no host port: by name, inside a container, it's called all the same.
        var inside = new AppDefinition { Slug = "web", Ports = [new PortMapping { Id = 1, ContainerPort = 3000, Access = PortAccess.Internal, IsWeb = true }] };
        Assert.Equal(("http://gb-web:3000", (string?)null), CronScheduler.BaseUrl(inside, Container("running", "3000/tcp"), inContainer: true));
        Assert.Contains("Internal", CronScheduler.BaseUrl(inside, Container("running", "3000/tcp"), inContainer: false).Problem);
    }

    [Fact]
    public void Says_why_an_app_cannot_be_called()
    {
        var app = new AppDefinition { Slug = "web", Ports = [new PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000, IsWeb = true }] };

        Assert.Equal(((string?)null, CronScheduler.NoPort), CronScheduler.BaseUrl(new AppDefinition { Slug = "web" }, Container("running", ""), inContainer: true));
        // A TCP port that isn't the web port isn't called: a worker has none.
        var worker = new AppDefinition { Slug = "web", Ports = [new PortMapping { Id = 1, HostPort = 3001, ContainerPort = 3000 }] };
        Assert.Equal(((string?)null, CronScheduler.NoPort), CronScheduler.BaseUrl(worker, Container("running", "0.0.0.0:3001->3000/tcp"), inContainer: true));
        Assert.Equal(((string?)null, "The app's container isn't running."), CronScheduler.BaseUrl(app, Container("exited", ""), inContainer: false));
        Assert.Equal(((string?)null, "The app's container isn't running."), CronScheduler.BaseUrl(app, null, inContainer: true));
        // A port change saved but not applied: the running container still has the old one.
        Assert.Contains("doesn't publish port 3000", CronScheduler.BaseUrl(app, Container("running", "0.0.0.0:3001->8080/tcp"), inContainer: false).Problem);
        Assert.Contains("doesn't have port 3000", CronScheduler.BaseUrl(app, Container("running", "0.0.0.0:3001->8080/tcp"), inContainer: true).Problem);
    }

    [Theory]
    [InlineData("CRON_SECRET=s3cret\n", "s3cret")]
    [InlineData("CRON_SECRET=a=b c\r\n", "a=b c")]
    [InlineData("CRON_SECRET=\n", null)]
    [InlineData("", null)]
    public void Reads_the_cron_secret_from_what_inspect_printed(string output, string? expected) =>
        Assert.Equal(expected, CronScheduler.SecretFrom(output));

    private static ContainerSummary Container(string state, string ports) => new("gb-web", state, "", "gitberth/web:abc1234", ports, new Dictionary<string, string>());
}
