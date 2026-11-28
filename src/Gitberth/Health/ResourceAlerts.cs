using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Health;

/// <summary>
/// Health alerts (Settings › Health alerts, and each app's own on its Settings tab): an app whose CPU stays above its line
/// for its minutes (of its CPU limit, or of the host's CPUs when it has none), or its memory (of its memory limit, or of
/// the host's memory), is told once (HighCpu, HighMemory), written in its Activity and kept on the attention board; once
/// every reading of its window is back under the line, that's told too (ResourcesOk) and the item goes. Looked at after
/// each of the sampler's readings, from the last hour it holds. What's alerting is kept across restarts of Gitberth, so
/// a restart doesn't tell it again.
/// </summary>
public sealed class ResourceAlerts(
    ContainerStatsCollector stats,
    HealthRules rules,
    DockerApi docker,
    Notifier notifier,
    ActivityLog activity,
    AttentionBoard board,
    ContainerTitles titles,
    SettingsService settings,
    IDbContextFactory<GitberthDb> dbFactory,
    ILogger<ResourceAlerts> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>Its items on the attention board.</summary>
    public const string Source = "resources";

    /// <summary>What's alerting (container names and kinds), kept across restarts.</summary>
    public const string StateKey = "health.alerts.state";

    /// <summary>How long a minute of an alert's window lasts: a minute, but for the end-to-end rig's (TestTimings).</summary>
    internal TimeSpan MinuteLength { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a container's CPU limit is kept between reads (a change of it recreates the container anyway).</summary>
    private static readonly TimeSpan LimitKept = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, (double? Cores, DateTime At)> _limits = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<ResourceKind>> _alerting = new(StringComparer.Ordinal);

    /// <summary>What's high now, by container.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<ResourceKind>> Alerting =>
        _alerting.ToDictionary(a => a.Key, a => (IReadOnlySet<ResourceKind>)a.Value.ToHashSet(), StringComparer.Ordinal);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        stats.Updated += Wake;
        rules.Changed += Wake;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        stats.Updated -= Wake;
        rules.Changed -= Wake;
        return base.StopAsync(cancellationToken);
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var actor = Audit.AuditScope.Begin(Audit.AuditActor.System(Audit.AuditVia.Health));
        try
        {
            _alerting = ParseState(await settings.GetAsync(StateKey, stoppingToken));
            // What was high before the restart is on the board again at once, not only once something changes.
            if (_alerting.Count > 0) board.Replace(Source, await ItemsAsync(stoppingToken));
            await foreach (var _ in _wake.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await LookAsync(stats.Current, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Looking at the health alerts failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One look at the latest readings: alerts that start, alerts that end, and the attention board.</summary>
    internal async Task LookAsync(HealthSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.SampledAt is not { } now) return;
        var changed = false;
        var up = snapshot.Containers.Where(c => c.IsUp && c.Gathered).ToDictionary(c => c.Name, StringComparer.Ordinal);
        // Stopped, gone or switched off: what it had goes quietly.
        foreach (var name in _alerting.Keys.Where(n => !up.ContainsKey(n) || !rules.For(n).On).ToList())
        {
            _alerting.Remove(name);
            changed = true;
        }
        foreach (var c in up.Values)
        {
            var r = rules.For(c.Name);
            if (!r.On) continue;
            var cores = await CoresAsync(c.Name, snapshot.Host, ct);
            foreach (var kind in new[] { ResourceKind.Cpu, ResourceKind.Memory })
            {
                var (percent, minutes) = kind == ResourceKind.Cpu ? (r.CpuPercent, r.CpuMinutes) : (r.MemoryPercent, r.MemoryMinutes);
                var window = MinuteLength * minutes;
                var capacity = kind == ResourceKind.Cpu ? cores * 100 : (double?)(c.MemoryLimit ?? snapshot.Host?.MemoryBytes);
                var shares = Shares(c.Recent, kind == ResourceKind.Cpu ? p => p.Cpu : p => p.Memory, capacity, now, window);
                var on = _alerting.TryGetValue(c.Name, out var kinds) && kinds.Contains(kind);
                switch (Step(on, shares, percent))
                {
                    case AlertStep.Starts:
                        _alerting.TryAdd(c.Name, []);
                        _alerting[c.Name].Add(kind);
                        changed = true;
                        await TellAsync(c, kind, true, shares!.Value.Average, percent, minutes, cores, snapshot.Host, ct);
                        break;
                    case AlertStep.Ends:
                        _alerting[c.Name].Remove(kind);
                        if (_alerting[c.Name].Count == 0) _alerting.Remove(c.Name);
                        changed = true;
                        await TellAsync(c, kind, false, shares!.Value.Average, percent, minutes, cores, snapshot.Host, ct);
                        break;
                }
            }
        }
        if (!changed) return;
        board.Replace(Source, await ItemsAsync(ct));
        try
        {
            await settings.SetAsync(StateKey, _alerting.Count == 0 ? null : JsonSerializer.Serialize(_alerting.ToDictionary(a => a.Key, a => a.Value.Order().ToArray())), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't keep what's alerting; a restart of Gitberth tells it again");
        }
    }

    /// <summary>
    /// The window's readings as shares of what the container may use (percent): their average and their peak, or null when
    /// the readings don't reach back over the whole window yet, or there's nothing to measure against.
    /// </summary>
    internal static (double Average, double Peak)? Shares(IReadOnlyList<StatsPoint> points, Func<StatsPoint, double?> value, double? capacity, DateTime now, TimeSpan window)
    {
        if (capacity is not > 0) return null;
        var from = now - window;
        var first = -1;
        for (var i = 0; i < points.Count; i++)
            if (points[i].Time >= from)
            {
                first = i;
                break;
            }
        // A reading is stamped at the end of its 5 seconds: the first must start the window, give or take one.
        if (first < 0 || points[first].Time > from + ContainerStatsCollector.Interval * 2) return null;
        var values = new List<double>();
        for (var i = first; i < points.Count; i++)
            if (value(points[i]) is { } v)
                values.Add(v / capacity.Value * 100);
        return values.Count == 0 ? null : (values.Average(), values.Max());
    }

    /// <summary>An alert starts when the window's average is over the line, and ends when every reading of it is back under.</summary>
    internal static AlertStep Step(bool alerting, (double Average, double Peak)? shares, int percent) => shares switch
    {
        null => AlertStep.None,
        { Average: var a } when !alerting && a > percent => AlertStep.Starts,
        { Peak: var p } when alerting && p <= percent => AlertStep.Ends,
        _ => AlertStep.None,
    };

    // The container's CPU limit in cores, else the host's CPUs; read now and then.
    private async Task<double> CoresAsync(string name, DockerHostInfo? host, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (!_limits.TryGetValue(name, out var known) || now - known.At > LimitKept)
        {
            double? cores = null;
            try
            {
                cores = CoresOf((await docker.InspectContainerAsync(name, ct))?["HostConfig"]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Couldn't read {Container}'s CPU limit", name);
            }
            _limits[name] = known = (cores, now);
        }
        return known.Cores ?? Math.Max(1, host?.Cpus ?? 1);
    }

    /// <summary>A container's CPU limit in cores (--cpus, or a quota over its period); null when it has none.</summary>
    internal static double? CoresOf(System.Text.Json.Nodes.JsonNode? hostConfig)
    {
        if (hostConfig?["NanoCpus"] is System.Text.Json.Nodes.JsonValue nano && nano.TryGetValue<long>(out var n) && n > 0) return n / 1e9;
        if (hostConfig?["CpuQuota"] is System.Text.Json.Nodes.JsonValue quota && quota.TryGetValue<long>(out var q) && q > 0)
        {
            var period = hostConfig["CpuPeriod"] is System.Text.Json.Nodes.JsonValue p && p.TryGetValue<long>(out var pp) && pp > 0 ? pp : 100_000;
            return (double)q / period;
        }
        return null;
    }

    private async Task TellAsync(ContainerHealth c, ResourceKind kind, bool starts, double share, int percent, int minutes, double cores, DockerHostInfo? host, CancellationToken ct)
    {
        var (name, appId) = await AppOfAsync(c.Name, ct);
        var (title, text) = Words(name, kind, starts, share, percent, minutes, Of(kind, c, cores, host));
        try
        {
            await activity.AddAsync(c.Name, appId, ActivityKind.Event, text, starts ? ActivityTone.Warning : ActivityTone.Good, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't write {Container}'s health alert to its Activity", c.Name);
        }
        notifier.Publish(new Notice(starts ? kind == ResourceKind.Cpu ? NotifyEvent.HighCpu : NotifyEvent.HighMemory : NotifyEvent.ResourcesOk,
            starts ? NoticeLevel.Warning : NoticeLevel.Good, title, text, await notifier.LinkAsync($"apps/{Uri.EscapeDataString(c.Name)}", ct), name, c.Name,
            _time.GetUtcNow().UtcDateTime) { AppId = appId });
        logger.LogInformation("{Title}", title);
    }

    // What the share is of: "of its 0.5-CPU limit", "of the host's 4 CPUs", "of its 512 MB limit", "of the host's memory".
    private static string Of(ResourceKind kind, ContainerHealth c, double cores, DockerHostInfo? host)
    {
        if (kind == ResourceKind.Cpu)
            return host is not null && Math.Abs(cores - host.Cpus) < 0.001 ? $"of the host's {Count(host.Cpus, "CPU")}" : $"of its {cores.ToString("0.##", CultureInfo.InvariantCulture)}-CPU limit";
        return c.MemoryLimit is ulong limit && host is not null && limit < host.MemoryBytes ? $"of its {Format.Memory(limit)} limit" : "of the host's memory";
    }

    private static string Count(int n, string what) => $"{n.ToString(CultureInfo.InvariantCulture)} {what}{(n == 1 ? "" : "s")}";

    /// <summary>A notice's title and text: an alert that starts, or ends.</summary>
    internal static (string Title, string Text) Words(string app, ResourceKind kind, bool starts, double share, int percent, int minutes, string of)
    {
        var what = kind == ResourceKind.Cpu ? "CPU" : "memory";
        var span = minutes == 1 ? "a minute" : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";
        return starts
            ? ($"{app}'s {what} is high",
                $"It used {share.ToString("0", CultureInfo.InvariantCulture)}% {of} on average over {span}, above its alert's {percent.ToString(CultureInfo.InvariantCulture)}%. " +
                "Gitberth says so again once it's back under.")
            : ($"{app}'s {what} is back under its line",
                $"It stayed under {percent.ToString(CultureInfo.InvariantCulture)}% {of} for {span}.");
    }

    private async Task<(string Name, int? AppId)> AppOfAsync(string container, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
            if (await db.Apps.AsNoTracking().Where(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug))
                    .Select(a => new { a.Id, a.Name }).FirstOrDefaultAsync(ct) is { } row)
                return (row.Name, row.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't find {Container}'s app", container);
        }
        return (titles.For(container) ?? container, null);
    }

    private async Task<List<AttentionItem>> ItemsAsync(CancellationToken ct)
    {
        var items = new List<AttentionItem>();
        foreach (var (name, kinds) in _alerting.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var (app, _) = await AppOfAsync(name, ct);
            var what = kinds.Count == 2 ? "CPU and memory" : kinds.Contains(ResourceKind.Cpu) ? "CPU" : "memory";
            var r = rules.For(name);
            var href = $"apps/{Uri.EscapeDataString(name)}";
            items.Add(new AttentionItem($"resources:{name}", Source, AttentionLevel.Warn, $"{app}'s {what} is high",
                $"Above its alert's line ({(kinds.Contains(ResourceKind.Cpu) ? $"CPU {r.CpuPercent}% for {r.CpuMinutes} min" : "")}" +
                $"{(kinds.Count == 2 ? ", " : "")}{(kinds.Contains(ResourceKind.Memory) ? $"memory {r.MemoryPercent}% for {r.MemoryMinutes} min" : "")}). " +
                "It goes once every reading of its window is back under.", href, Tip: "dashboard.resources-high", Actions:
                [new AttentionAction($"Open {app}", href), new AttentionAction("Health", $"health#hc-title-{Uri.EscapeDataString(name)}")],
                // Dismissed, it stays hidden while the same alerts last.
                Dismiss: string.Join(',', kinds.Order())));
        }
        return items;
    }

    internal static Dictionary<string, HashSet<ResourceKind>> ParseState(string? json)
    {
        var result = new Dictionary<string, HashSet<ResourceKind>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            foreach (var (name, kinds) in JsonSerializer.Deserialize<Dictionary<string, ResourceKind[]>>(json) ?? [])
                if (kinds is { Length: > 0 }) result[name] = [.. kinds];
        }
        catch (JsonException)
        {
        }
        return result;
    }
}

/// <summary>What an alert is about.</summary>
public enum ResourceKind { Cpu, Memory }

/// <summary>What a look at an alert does: nothing, start it, or end it.</summary>
internal enum AlertStep { None, Starts, Ends }
