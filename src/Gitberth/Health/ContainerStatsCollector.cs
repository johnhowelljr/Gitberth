using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;

namespace Gitberth.Health;

/// <summary>
/// CPU and network averaged over one sampling interval and memory as read at its end, stamped at the end of it. Rates
/// and memory are null when unknown.
/// </summary>
public readonly record struct StatsPoint(DateTime Time, double Cpu, double? RxRate, double? TxRate, double? Memory = null);

/// <param name="Gathered">False when stats are switched off for this container; it then has no new readings.</param>
/// <param name="MemoryBytes">Null when there's no current reading of this container.</param>
/// <param name="Recent">The last hour: every reading.</param>
/// <param name="History">The last 6 hours: averages over <see cref="ContainerStatsCollector.HistoryStep"/>.</param>
/// <param name="Marks">Deploys, restarts, stops and crashes in the last 6 hours, oldest first.</param>
/// <param name="Down">When it wasn't running in the last 6 hours.</param>
public sealed record ContainerHealth(
    string Name,
    string Image,
    string State,
    string Status,
    string? AppSlug,
    string? NetworkMode,
    bool Gathered,
    ulong? MemoryBytes,
    ulong? MemoryLimit,
    IReadOnlyList<StatsPoint> Recent,
    IReadOnlyList<StatsPoint> History,
    IReadOnlyList<ChartMark> Marks,
    IReadOnlyList<DownSpan> Down)
{
    /// <summary>Running, paused or restarting: anything but stopped.</summary>
    public bool IsUp => ContainerStatsCollector.IsUp(State);
}

public sealed record HealthSnapshot(DateTime? SampledAt, string? Error, DockerHostInfo? Host, IReadOnlyList<ContainerHealth> Containers)
{
    public static readonly HealthSnapshot Empty = new(null, null, null, []);
}

/// <summary>
/// Reads every running container's counters every 5 seconds and keeps the last 6 hours, in memory and on disk
/// (StatsStore), so the charts come back after a restart of Gitberth. CPU time and network bytes are running totals, so
/// each point is the exact average over its interval and nothing between two readings is missed. Memory is a plain
/// reading taken at that moment. A container that stops stays on the page while it has readings or events to show.
/// </summary>
public sealed class ContainerStatsCollector(
    DockerApi docker,
    HealthSettings settings,
    StatsStore store,
    ContainerEventWatcher events,
    ILogger<ContainerStatsCollector> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How long readings are kept and shown.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(6);

    /// <summary>The short view, which shows every reading.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    /// <summary>The long view averages readings over this long, so 6 hours draw as many points as the last hour.</summary>
    public static readonly TimeSpan HistoryStep = TimeSpan.FromSeconds(30);

    /// <summary>Readings further apart than this aren't averaged, and charts break the line there.</summary>
    public static readonly TimeSpan MaxGap = Interval * 3;

    // A container that keeps crashing could mark thousands; the latest are the ones worth showing.
    private const int MaxMarks = 100;
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);

    // Keyed by name rather than ID, so an app's history carries on across deploys, which replace its container.
    // Only the sampling loop touches this.
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    // Config.Image of the containers Docker lists by their image's ID, by container ID. Only the sampling loop touches this.
    private readonly Dictionary<string, string> _configImages = new(StringComparer.Ordinal);
    private DockerHostInfo? _host;
    private DateTime _pruned;
    private bool _saveFailing;

    /// <summary>The latest readings. Replaced as a whole after each sample, so it's safe to read from any thread.</summary>
    public HealthSnapshot Current { get; private set; } = HealthSnapshot.Empty;

    /// <summary>Raised on the sampling thread after each sample.</summary>
    public event Action? Updated;

    internal static bool IsUp(string state) => state is "running" or "paused" or "restarting";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RestoreAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await SampleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Reading container stats failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The readings saved before Gitberth last stopped, so the charts carry on where they were.
    private async Task RestoreAsync(CancellationToken ct)
    {
        try
        {
            var saved = await store.LoadPointsAsync(DateTime.UtcNow - Kept, ct);
            foreach (var (name, points) in saved)
            {
                var track = new Track { LastUp = points[^1].Time };
                track.Points.AddRange(points);
                _tracks[name] = track;
            }
            if (saved.Count > 0)
                logger.LogInformation("Restored {Points} readings of {Containers} containers", saved.Sum(s => s.Value.Count), saved.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the saved readings, so the charts start empty");
        }
    }

    /// <summary>One sample: every container's readings, published as <see cref="Current"/>.</summary>
    internal async Task SampleAsync(CancellationToken ct)
    {
        List<RunningContainer> listed;
        try
        {
            // Gitberth's own short-lived helpers have no history worth charting, and don't count as running.
            listed = (await docker.ListAllAsync(ct)).Where(c => !HostScanHelper.IsHelper(c.Labels)).ToList();
            _host ??= await docker.GetHostInfoAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Keep the last good readings on screen; the error says they're stale.
            Publish(Current with { Error = $"Can't reach the Docker engine at {docker.Endpoint}: {Reason(ex)}" });
            return;
        }

        var start = DateTime.UtcNow;
        var byName = events.Events.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // Stopped containers only while they have something from the last hours to show; most are long done with.
        var shown = listed.Where(c => IsUp(c.State) || _tracks.ContainsKey(c.Name)
            || (byName.TryGetValue(c.Name, out var list) && list[^1].Time >= start - Kept)).ToList();

        await ConfigImagesAsync(shown, ct);
        var skipped = settings.Skipped;
        var readings = new (StatsReading? Reading, DateTime At)[shown.Count];
        var wanted = Enumerable.Range(0, shown.Count).Where(i => shown[i].State is "running" or "paused" && !skipped.Contains(shown[i].Name));
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    readings[i] = (await docker.GetStatsAsync(shown[i].Id, token), DateTime.UtcNow);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Usually a container that stopped between the list and the stats call.
                    logger.LogDebug(ex, "No stats for {Container}", shown[i].Name);
                }
            });

        var now = DateTime.UtcNow;
        var fresh = new List<(string Container, StatsPoint Point)>();
        var containers = new List<ContainerHealth>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var c = shown[i];
            var slug = c.Labels.TryGetValue("gitberth.app", out var s) ? s : null;
            var (marks, down) = MarksFor(byName, c, now);
            _tracks.TryGetValue(c.Name, out var track);
            if (skipped.Contains(c.Name) || !IsUp(c.State))
            {
                // No new readings: switched off, or stopped. Its history stays until it ages out.
                track?.Trim(now - Kept);
                containers.Add(new ContainerHealth(c.Name, ImageOf(c), c.State, c.Status, slug, c.NetworkMode, !skipped.Contains(c.Name),
                    null, null, Recent(track, now), History(track, now), marks, down));
                continue;
            }

            if (track is null) _tracks[c.Name] = track = new Track();
            // Docker answers some failed reads with an empty placeholder (zero time, zero counters). Comparing the next
            // real reading with it would plot the container's lifetime CPU as one point, so keep the last real one.
            if (readings[i] is { Reading: { } reading, At: var at } && !IsPlaceholder(reading))
            {
                var sample = new RawSample(c.Id, at, reading);
                if (track.Last is { } last && Rate(last, sample) is { } point)
                {
                    track.Points.Add(point);
                    fresh.Add((c.Name, point));
                }
                track.Last = sample;
            }
            track.LastUp = now;
            track.Trim(now - Kept);

            // Memory is a plain reading, so only show one that's current and from this very container.
            var current = track.Last is { } l && l.ContainerId == c.Id && now - l.At <= MaxGap ? l.Reading : null;
            containers.Add(new ContainerHealth(
                c.Name, ImageOf(c), c.State, c.Status, slug, c.NetworkMode, true,
                current?.MemoryBytes, current?.MemoryLimit, Recent(track, now), History(track, now), marks, down));
        }

        // A container that's gone (or stopped and quiet for hours) is forgotten once its readings have aged out.
        foreach (var gone in _tracks.Where(t => now - t.Value.LastUp > Kept).Select(t => t.Key).ToList())
            _tracks.Remove(gone);

        Publish(new HealthSnapshot(now, null, _host, containers));
        await SaveAsync(fresh, now, ct);
    }

    // Docker's list names a container's image by its ID once the tag it was created with names another image (pulled again,
    // or rebuilt): the reference it was created with is read from it once, and kept by its ID while it's listed.
    private async Task ConfigImagesAsync(List<RunningContainer> shown, CancellationToken ct)
    {
        foreach (var c in shown.Where(c => ImageReference.IsImageId(c.Image) && !_configImages.ContainsKey(c.Id)))
        {
            try
            {
                _configImages[c.Id] = ContainerPorts.Str((await docker.InspectContainerAsync(c.Id, ct))?["Config"], "Image");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Couldn't read the image {Container} was created with", c.Name);
            }
        }
        var listed = shown.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _configImages.Keys.Where(id => !listed.Contains(id)).ToList()) _configImages.Remove(gone);
    }

    private string ImageOf(RunningContainer c) => _configImages.GetValueOrDefault(c.Id) is { Length: > 0 } image ? image : c.Image;

    private static (IReadOnlyList<ChartMark> Marks, IReadOnlyList<DownSpan> Down) MarksFor(
        Dictionary<string, List<ContainerEvent>> byName, RunningContainer c, DateTime now)
    {
        if (!byName.TryGetValue(c.Name, out var list)) return ([], []);
        var (marks, down) = EventMarks.For(list, now - Kept, now, c.State == "running");
        if (marks.Count > MaxMarks) marks.RemoveRange(0, marks.Count - MaxMarks);
        if (down.Count > MaxMarks) down.RemoveRange(0, down.Count - MaxMarks);
        return (marks, down);
    }

    private async Task SaveAsync(List<(string Container, StatsPoint Point)> fresh, DateTime now, CancellationToken ct)
    {
        try
        {
            await store.AddPointsAsync(fresh, ct);
            if (now - _pruned > PruneEvery)
            {
                await store.PruneAsync(now - Kept, now - ContainerEventWatcher.Kept, ct);
                _pruned = now;
            }
            if (_saveFailing) logger.LogInformation("Saving readings works again");
            _saveFailing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Said once: the charts still work, they just won't survive a restart.
            if (!_saveFailing) logger.LogWarning(ex, "Couldn't save readings, so the charts won't survive a restart of Gitberth");
            _saveFailing = true;
        }
    }

    private static IReadOnlyList<StatsPoint> Recent(Track? track, DateTime now)
    {
        if (track is null) return [];
        var from = FirstAtOrAfter(track.Points, now - RecentWindow);
        return track.Points.GetRange(from, track.Points.Count - from).ToArray();
    }

    private static IReadOnlyList<StatsPoint> History(Track? track, DateTime now) =>
        track is null ? [] : Averages(track.Points, HistoryStep, now);

    /// <summary>
    /// Readings averaged over steps of the clock, each stamped at the end of its step. The steps are fixed to the clock,
    /// so a finished step's average never changes; the one still filling up is left out, as it would change under the chart.
    /// </summary>
    internal static List<StatsPoint> Averages(IReadOnlyList<StatsPoint> points, TimeSpan step, DateTime now)
    {
        var result = new List<StatsPoint>();
        var i = 0;
        while (i < points.Count)
        {
            var bucket = points[i].Time.Ticks / step.Ticks;
            var end = new DateTime((bucket + 1) * step.Ticks, DateTimeKind.Utc);
            if (end > now) break;
            double cpu = 0, rx = 0, tx = 0, memory = 0;
            int n = 0, net = 0, mem = 0;
            for (; i < points.Count && points[i].Time.Ticks / step.Ticks == bucket; i++)
            {
                var p = points[i];
                cpu += p.Cpu;
                n++;
                if (p is { RxRate: { } r, TxRate: { } t })
                {
                    rx += r;
                    tx += t;
                    net++;
                }
                if (p.Memory is { } m)
                {
                    memory += m;
                    mem++;
                }
            }
            result.Add(new StatsPoint(end, cpu / n, net > 0 ? rx / net : null, net > 0 ? tx / net : null, mem > 0 ? memory / mem : null));
        }
        return result;
    }

    // Points are oldest first.
    private static int FirstAtOrAfter(List<StatsPoint> points, DateTime time)
    {
        int lo = 0, hi = points.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    internal sealed record RawSample(string ContainerId, DateTime At, StatsReading Reading);

    /// <summary>Docker's stand-in for a read that failed: no timestamp and no counters. A running container always has used some CPU.</summary>
    internal static bool IsPlaceholder(StatsReading reading) => reading is { ReadAt: null, CpuNanos: 0 };

    /// <summary>The average use between two readings, or null when they can't be compared.</summary>
    internal static StatsPoint? Rate(RawSample previous, RawSample current)
    {
        var (a, b) = (previous.Reading, current.Reading);
        // The engine stamps each reading, which times the interval exactly; arrival times also include request latency.
        var interval = a.ReadAt is { } r0 && b.ReadAt is { } r1 && r1 > r0 ? r1 - r0 : current.At - previous.At;
        if (current.ContainerId != previous.ContainerId || interval <= TimeSpan.Zero || interval > MaxGap) return null;

        // Counters start again from zero when a container restarts.
        if (b.CpuNanos < a.CpuNanos) return null;

        // 100% is one core busy for the whole interval, the same scale `docker stats` uses.
        var seconds = interval.TotalSeconds;
        var cpu = (b.CpuNanos - a.CpuNanos) / (seconds * 1e9) * 100;

        double? rx = null, tx = null;
        if (a.Network is { } n0 && b.Network is { } n1 && n1.RxBytes >= n0.RxBytes && n1.TxBytes >= n0.TxBytes)
        {
            rx = (n1.RxBytes - n0.RxBytes) / seconds;
            tx = (n1.TxBytes - n0.TxBytes) / seconds;
        }
        // A running container always uses some memory: none means the engine couldn't say.
        return new StatsPoint(current.At, cpu, rx, tx, b.MemoryBytes > 0 ? b.MemoryBytes : null);
    }

    private void Publish(HealthSnapshot snapshot)
    {
        Current = snapshot;
        // One broken subscriber (say, a page whose connection just dropped) must not stop sampling.
        foreach (var handler in Updated?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { logger.LogDebug(ex, "A health page failed to refresh"); }
        }
    }

    private static string Reason(Exception ex)
    {
        while (ex is HttpRequestException { InnerException: { } inner }) ex = inner;
        return ex is TaskCanceledException or TimeoutException ? "it didn't answer within 10 seconds." : ex.Message;
    }

    private sealed class Track
    {
        public RawSample? Last;
        /// <summary>When the container was last seen up; for one restored from disk, its newest reading.</summary>
        public DateTime LastUp;
        public readonly List<StatsPoint> Points = [];

        public void Trim(DateTime before)
        {
            var n = 0;
            while (n < Points.Count && Points[n].Time < before) n++;
            if (n > 0) Points.RemoveRange(0, n);
        }
    }
}
