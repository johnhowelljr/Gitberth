using System.Collections.Concurrent;
using System.Diagnostics;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

public enum CronOutcome { Answered, NoAnswer, Skipped, NotCalled }

/// <summary>One run of a cron job, or one that was due but didn't happen.</summary>
/// <param name="StartedAt">When it started, or was due.</param>
/// <param name="Status">The HTTP status the app answered with, when it answered.</param>
/// <param name="Detail">What went wrong or why it didn't run, as a sentence; null for a plain answer.</param>
/// <param name="Manual">Started with Run now rather than by its schedule.</param>
public sealed record CronRun(DateTime StartedAt, TimeSpan Duration, CronOutcome Outcome, int? Status, string? Detail, bool Manual)
{
    public bool Succeeded => Outcome == CronOutcome.Answered && Status is >= 200 and < 300;
}

/// <param name="Description">The schedule in words, like "every day at 06:00 UTC", when that's easy.</param>
/// <param name="NextRun">Null when the schedule never comes round, like February 30th.</param>
/// <param name="RunningSince">When the run that's still going started.</param>
/// <param name="Recent">The latest runs, newest first. They're kept in memory, so they start over when Gitberth restarts.</param>
public sealed record CronJobStatus(string Path, string Schedule, string? Description, DateTime? NextRun, DateTime? RunningSince, IReadOnlyList<CronRun> Recent)
{
    public CronRun? Last => Recent.Count > 0 ? Recent[0] : null;
}

/// <param name="Jobs">The jobs in the app's current image: none for most apps.</param>
/// <param name="Problem">Why they don't run at the moment (the app is stopped, or has no web port), or null.</param>
public sealed record AppCronJobs(IReadOnlyList<CronJobStatus> Jobs, string? Problem)
{
    public static readonly AppCronJobs None = new([], null);
}

/// <summary>
/// Runs the cron jobs in apps' images the way Vercel runs a project's crons: in each minute a schedule matches (UTC), a
/// GET of the job's path on the app's running container, with Authorization: Bearer CRON_SECRET when the app has one.
/// As on Vercel, only production runs them: a preview never does.
/// As on Vercel, a failed run isn't retried, a redirect isn't followed, and minutes missed while Gitberth wasn't running
/// aren't caught up. Unlike Vercel, a run that comes due while the job's previous run is still going is skipped.
/// </summary>
public sealed class CronScheduler(
    IDbContextFactory<GitberthDb> dbFactory,
    DockerCli docker,
    IHttpClientFactory httpFactory,
    GitberthOptions options,
    ILogger<CronScheduler> logger) : BackgroundService
{
    /// <summary>How long a run may take, reading the answer included. The "cron" HttpClient has the same timeout.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long an image's jobs are remembered: a tag can be rebuilt with other jobs.</summary>
    private static readonly TimeSpan LabelsFor = TimeSpan.FromMinutes(5);

    private const int KeepRuns = 10;

    internal const string NoPort = "The app has no web port, so Gitberth can't call its jobs. Set one on the Network tab.";

    /// <summary>As on Vercel, only production runs a project's cron jobs.</summary>
    internal const string PreviewProblem = "Previews don't run these jobs: only production does, as on Vercel.";

    /// <summary>Prints only the CRON_SECRET line of a container's environment, never the app's other secrets.</summary>
    internal const string SecretFormat = """{{range .Config.Env}}{{if eq (index (split . "=") 0) "CRON_SECRET"}}{{.}}{{"\n"}}{{end}}{{end}}""";

    // A job is a path and a schedule, so two entries for one path are two jobs, as on Vercel.
    private readonly ConcurrentDictionary<(int AppId, string Path, string Schedule), JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Entry> Jobs, DateTime ReadAt)> _images = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Raised with the app's ID when one of its jobs starts or finishes, on the thread running it.</summary>
    public event Action<int>? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var next = Minute(DateTime.UtcNow).AddMinutes(1);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Timers can wake a little early and the clock can move, so each minute runs once. Minutes missed while
                // Gitberth was busy or asleep are skipped, as Vercel doesn't catch up on them either.
                var now = DateTime.UtcNow;
                if (now < next)
                {
                    await Task.Delay(next - now, stoppingToken);
                    continue;
                }
                var minute = Minute(now);
                try
                {
                    await TickAsync(minute, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Starting the cron jobs due at {Minute:HH:mm} UTC failed", minute);
                }
                next = minute.AddMinutes(1);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Runs still going end with Gitberth.
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Starts the jobs due in <paramref name="minute"/> and returns their runs, which finish in the background.</summary>
    internal async Task<List<Task<CronRun>>> TickAsync(DateTime minute, CancellationToken ct)
    {
        foreach (var (key, image) in _images)
            if (DateTime.UtcNow - image.ReadAt >= LabelsFor) _images.TryRemove(key, out _);

        List<AppDefinition> apps;
        // Production apps only: a preview's image carries the jobs too, but as on Vercel only production runs them.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            apps = await db.Apps.AsNoTracking().Include(a => a.Ports)
                .Where(a => a.DesiredState == DesiredState.Running && a.CurrentImage != null && a.ParentAppId == null)
                .ToListAsync(ct);

        var runs = new List<Task<CronRun>>();
        (List<ContainerSummary> Containers, string? Error)? ps = null;
        foreach (var app in apps)
        {
            var due = (await JobsAsync(app, ct)).Where(j => j.Schedule.Matches(minute)).ToList();
            if (due.Count == 0) continue;
            // One `docker ps` a minute, and only when something is due.
            ps ??= await docker.ListContainersAsync(managedOnly: true, ct);
            var target = await TargetAsync(app, ps.Value, ct);
            runs.AddRange(due.Select(job => RunAsync(app, job.Job, target, manual: false)));
        }
        return runs;
    }

    /// <summary>The jobs in the app's current image, with when they run next and what happened lately.</summary>
    public async Task<AppCronJobs> GetAsync(int appId, CancellationToken ct = default)
    {
        var app = await LoadAppAsync(appId, ct);
        if (app?.CurrentImage is null) return AppCronJobs.None;
        var jobs = await JobsAsync(app, ct);
        if (jobs.Count == 0) return AppCronJobs.None;
        var now = DateTime.UtcNow;
        var list = jobs.Select(j =>
        {
            var (since, recent) = _jobs.TryGetValue((app.Id, j.Job.Path, j.Job.Schedule), out var state) ? state.Snapshot() : default;
            return new CronJobStatus(j.Job.Path, j.Job.Schedule, j.Schedule.Describe(), j.Schedule.Next(now), since, recent ?? []);
        }).ToList();
        var problem = app.ParentAppId is not null ? PreviewProblem
            : app.DesiredState == DesiredState.Stopped ? "The app is stopped, so its jobs don't run until you start it."
            : PortRules.WebOf(app.Ports) is null ? NoPort
            : null;
        return new AppCronJobs(list, problem);
    }

    /// <summary>
    /// Runs a job of the app's current image now, unless the job's previous run is still going: then this one is skipped,
    /// as a scheduled one would be. Finishes when the run does.
    /// </summary>
    public async Task<CronRun> RunNowAsync(int appId, string path, string schedule)
    {
        var app = await LoadAppAsync(appId, CancellationToken.None) ?? throw new InvalidOperationException("This app doesn't exist anymore.");
        if (app.ParentAppId is not null) throw new InvalidOperationException(PreviewProblem);
        IReadOnlyList<Entry> jobs = app.CurrentImage is null ? [] : await JobsAsync(app, CancellationToken.None);
        var job = jobs.FirstOrDefault(j => j.Job.Path == path && j.Job.Schedule == schedule)?.Job
            ?? throw new InvalidOperationException($"The app's current image has no cron job {path} ({schedule}).");
        var target = await TargetAsync(app, await docker.ListContainersAsync(managedOnly: true, _stopping.Token), _stopping.Token);
        return await RunAsync(app, job, target, manual: true);
    }

    /// <summary>
    /// Where an app's jobs are called, the way the deploy's health check calls it: at its web port. When Gitberth runs in a
    /// container that's by name on the shared network, published or not, as long as the running container has the port;
    /// otherwise it's on localhost at the host port the running container publishes for it. Null, with the reason, when
    /// the app can't be called.
    /// </summary>
    internal static (string? Url, string? Problem) BaseUrl(AppDefinition app, ContainerSummary? container, bool inContainer)
    {
        if (PortRules.WebOf(app.Ports) is not { } web) return (null, NoPort);
        if (container is not { State: "running" }) return (null, "The app's container isn't running.");
        var port = web.ContainerPort;
        if (inContainer)
            return DockerCli.ParseContainerPorts(container.Ports).Contains(new ExposedPort(port, "tcp"))
                ? ($"http://{app.ContainerName}:{port}", null)
                : (null, $"The running container doesn't have port {port} yet. Restart it with the new settings.");
        var binding = DockerCli.ParsePortBindings(container.Ports).FirstOrDefault(b => b.Protocol == "tcp" && b.ContainerPort == port);
        if (binding is not null) return ($"http://localhost:{binding.HostPort}", null);
        return (null, web.Access == PortAccess.Internal
            ? $"The web port {port} is Internal, and Gitberth runs outside Docker here, so it can only call the app through a host port."
            : $"The running container doesn't publish port {port} yet. Restart it with the new settings.");
    }

    /// <summary>The value of CRON_SECRET in what <see cref="SecretFormat"/> printed; null when it's not set or empty.</summary>
    internal static string? SecretFrom(string output)
    {
        const string prefix = "CRON_SECRET=";
        var line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line is not null && line.Length > prefix.Length ? line[prefix.Length..] : null;
    }

    /// <summary>The request Vercel makes for a cron job: a GET with its user agent and schedule, and the secret when there is one.</summary>
    private static HttpRequestMessage Request(string url, string schedule, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "vercel-cron/1.0");
        request.Headers.TryAddWithoutValidation("x-vercel-cron-schedule", schedule);
        if (secret is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        return request;
    }

    private async Task<CronRun> RunAsync(AppDefinition app, CronJob job, Target target, bool manual)
    {
        var state = _jobs.GetOrAdd((app.Id, job.Path, job.Schedule), _ => new JobState());
        var started = DateTime.UtcNow;
        if (!state.TryStart(started))
        {
            var skipped = new CronRun(started, TimeSpan.Zero, CronOutcome.Skipped, null, "The previous run was still going.", manual);
            state.Add(skipped, finished: false);
            Log(app, job, skipped);
            Raise(app.Id);
            return skipped;
        }
        Raise(app.Id);
        var run = target.BaseUrl is null
            ? new CronRun(started, TimeSpan.Zero, CronOutcome.NotCalled, null, target.Problem, manual)
            : await CallAsync(target.BaseUrl + job.Path, job.Schedule, target.Secret, started, manual);
        state.Add(run, finished: true);
        Log(app, job, run);
        Raise(app.Id);
        return run;
    }

    /// <summary>One GET, never retried. Whatever happens ends up in the run, with the secret masked out of any error.</summary>
    private async Task<CronRun> CallAsync(string url, string schedule, string? secret, DateTime started, bool manual)
    {
        var clock = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        cts.CancelAfter(RunTimeout);
        CronRun Failed(string detail) => new(started, clock.Elapsed, CronOutcome.NoAnswer, null, Mask(detail, secret), manual);
        try
        {
            using var request = Request(url, schedule, secret);
            using var response = await httpFactory.CreateClient("cron").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // Read to the end, so a run lasts until the app has finished answering.
            await response.Content.CopyToAsync(Stream.Null, cts.Token);
            var status = (int)response.StatusCode;
            var detail = status is >= 300 and < 400 ? Redirected(response.Headers.Location) : null;
            return new CronRun(started, clock.Elapsed, CronOutcome.Answered, status, Mask(detail, secret), manual);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return Failed("Gitberth stopped before the job finished.");
        }
        catch (OperationCanceledException)
        {
            return Failed($"No answer within {RunTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex)
        {
            return Failed(ex.InnerException is { Message: var inner } && !ex.Message.Contains(inner, StringComparison.Ordinal) ? $"{ex.Message} {inner}" : ex.Message);
        }
    }

    /// <summary>Where to call the app's jobs and the CRON_SECRET to send, or why they can't be called.</summary>
    private async Task<Target> TargetAsync(AppDefinition app, (List<ContainerSummary> Containers, string? Error) ps, CancellationToken ct)
    {
        if (PortRules.WebOf(app.Ports) is not null && ps.Error is { } error) return Target.Fail($"Gitberth can't reach Docker: {error}");
        var (url, problem) = BaseUrl(app, ps.Containers.FirstOrDefault(c => c.Name == app.ContainerName), options.RunningInContainer);
        if (url is null) return Target.Fail(problem!);
        // From the app's own container, so it's exactly what the app checks the header against. Only the exit code is
        // looked at on failure: the output is never logged.
        var r = await docker.RunAsync(["container", "inspect", "--format", SecretFormat, app.ContainerName], null, ct);
        return r.Ok ? new Target(url, SecretFrom(r.StdOut), null) : Target.Fail("Gitberth couldn't read the app's container.");
    }

    /// <summary>
    /// The jobs in the app's current image, from its label. The app's last update is part of the key, so a deploy that
    /// rebuilt the same tag is seen right away.
    /// </summary>
    private async Task<IReadOnlyList<Entry>> JobsAsync(AppDefinition app, CancellationToken ct)
    {
        var key = $"{app.CurrentImage}|{app.UpdatedAt.Ticks}";
        if (_images.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.ReadAt < LabelsFor) return cached.Jobs;
        var labels = await docker.ImageLabelsAsync(app.CurrentImage!, ct);
        var jobs = new List<Entry>();
        foreach (var job in CronJobs.FromLabel(labels.GetValueOrDefault(ImageLabels.Crons)))
            if (CronSchedule.TryParse(job.Schedule, out var schedule, out _)) jobs.Add(new Entry(job, schedule));
        // Gitberth's images always carry labels, so none at all means the image couldn't be read: try again next time.
        if (labels.Count > 0) _images[key] = (jobs, DateTime.UtcNow);
        return jobs;
    }

    private async Task<AppDefinition?> LoadAppAsync(int appId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Include(a => a.Ports).FirstOrDefaultAsync(a => a.Id == appId, ct);
    }

    private static DateTime Minute(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static string Redirected(Uri? location)
    {
        var to = location?.OriginalString is { Length: > 0 } l ? $" to {(l.Length > 200 ? l[..200] + "…" : l)}" : "";
        return $"Redirected{to}. Cron jobs don't follow redirects.";
    }

    private static string? Mask(string? text, string? secret) =>
        text is not null && secret is { Length: > 0 } ? text.Replace(secret, Security.Masking.Mask, StringComparison.Ordinal) : text;

    // A job that can't be called (its app crashed, say) would say so every time it's due, so that stays at Debug.
    private void Log(AppDefinition app, CronJob job, CronRun run) =>
        logger.Log(run.Succeeded || run.Outcome == CronOutcome.NotCalled ? LogLevel.Debug : LogLevel.Information,
            "Cron job {Path} ({Schedule}) of {App}: {Outcome} {Status} in {Milliseconds} ms {Detail}",
            job.Path, job.Schedule, app.Slug, run.Outcome, run.Status, (int)run.Duration.TotalMilliseconds, run.Detail);

    private void Raise(int appId)
    {
        // One broken subscriber (say, a page whose connection just dropped) must not stop a run.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<int>)handler)(appId); }
            catch (Exception ex) { logger.LogDebug(ex, "A page failed to refresh its scheduled jobs"); }
        }
    }

    private sealed record Entry(CronJob Job, CronSchedule Schedule);

    /// <summary>Where a run goes. A class rather than a record, so the secret can't end up in a ToString.</summary>
    private sealed class Target(string? baseUrl, string? secret, string? problem)
    {
        public string? BaseUrl { get; } = baseUrl;
        public string? Secret { get; } = secret;
        public string? Problem { get; } = problem;

        public static Target Fail(string problem) => new(null, null, problem);
    }

    private sealed class JobState
    {
        private readonly Lock _lock = new();
        private readonly List<CronRun> _recent = [];
        private DateTime? _runningSince;

        /// <summary>Marks the job as running, unless it already is.</summary>
        public bool TryStart(DateTime at)
        {
            lock (_lock)
            {
                if (_runningSince is not null) return false;
                _runningSince = at;
                return true;
            }
        }

        public void Add(CronRun run, bool finished)
        {
            lock (_lock)
            {
                if (finished) _runningSince = null;
                _recent.Insert(0, run);
                if (_recent.Count > KeepRuns) _recent.RemoveAt(_recent.Count - 1);
            }
        }

        public (DateTime? RunningSince, CronRun[] Recent) Snapshot()
        {
            lock (_lock) return (_runningSince, [.. _recent]);
        }
    }
}
