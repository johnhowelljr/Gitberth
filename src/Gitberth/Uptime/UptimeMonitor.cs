using System.Collections.Concurrent;
using System.Globalization;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Uptime;

/// <summary>
/// Checks every app the way a visitor reaches it, every <see cref="SettingKeys.UptimeIntervalSeconds"/> (60 seconds unless
/// changed): its main domain over HTTPS through Cloudflare and the tunnel when domains are on and it has one, otherwise its
/// web port on the LAN, otherwise nothing. Two failed checks in a row open an incident (and send "down"), one good check
/// closes it (and sends "back up"); a domain's certificate that expires within 14 days, then 3, is told once each. A
/// stopped app isn't checked, and neither is one whose deploy is replacing its container right now. Every check is kept
/// (<see cref="UptimeStore"/>) for the app's Uptime card, the Health page and the status page.
/// </summary>
public sealed class UptimeMonitor(
    IDbContextFactory<GitberthDb> dbFactory,
    UptimeStore store,
    UptimeChecker checker,
    UptimeCertificates certificates,
    SettingsService settings,
    DomainService domains,
    Notifier notifier,
    DownStates downStates,
    GitberthOptions options,
    ILogger<UptimeMonitor> logger,
    AppService? apps = null,
    TimeProvider? time = null) : BackgroundService
{
    public const int DefaultInterval = 60, MinInterval = 30, MaxInterval = 900;

    /// <summary>How many failed checks in a row make an app down.</summary>
    public const int FailuresToDown = 2;

    /// <summary>The days before a certificate expires at which it's told about.</summary>
    public static readonly int[] CertificateWarnings = [14, 3];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, Track> _tracks = new();
    private readonly SemaphoreSlim _recording = new(1, 1);
    private DateTime _prunedAt;

    /// <summary>How long after Gitberth starts the first checks run.</summary>
    internal TimeSpan FirstPassAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Raised after each pass, and after Check now, for open pages.</summary>
    public event Action? Changed;

    private sealed class Track
    {
        public int Failures;
        public UptimeResult? FirstFailure;
        public UptimeResult? Last;
        public UptimeIncident? Open;
        public string? Address;
        public UptimeStatus Status = UptimeStatus.Unknown;
        public string? Why;
    }

    /// <summary>One app as a pass sees it: where it's checked, or why it isn't.</summary>
    internal sealed record Target(int AppId, string Name, string Container, string? Address, UptimeStatus Status = UptimeStatus.Unknown, string? Why = null);

    /// <summary>How often apps are checked, in seconds, as saved (30 to 900).</summary>
    public async Task<int> IntervalAsync(CancellationToken ct = default) =>
        Math.Clamp(await settings.GetIntAsync(SettingKeys.UptimeIntervalSeconds, DefaultInterval, ct), MinInterval, MaxInterval);

    // Each app's container, by name, as the last pass saw them: the Health page lists containers.
    private volatile Dictionary<string, int> _containers = new(StringComparer.Ordinal);

    /// <summary>The uptime of the app whose container this is; null when no app of Gitberth's has it (or it hasn't been looked at yet).</summary>
    public AppUptime? ForContainer(string container) => _containers.TryGetValue(container, out var appId) ? For(appId) : null;

    /// <summary>The app's uptime as its pages show it now.</summary>
    public AppUptime For(int appId)
    {
        if (!_tracks.TryGetValue(appId, out var t)) return AppUptime.Unknown(appId);
        var host = t.Address is { } a && a.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(a, UriKind.Absolute, out var uri) ? uri.Host : null;
        return new AppUptime(appId, t.Status, t.Address, t.Last, t.Open, host is null ? null : certificates.ExpiresOf(host), t.Why);
    }

    /// <summary>Checks one app now, as a pass would; returns how it stands afterwards.</summary>
    public async Task<AppUptime> CheckNowAsync(int appId, CancellationToken ct = default)
    {
        var target = (await TargetsAsync(ct)).FirstOrDefault(t => t.AppId == appId);
        if (target is null) return AppUptime.Unknown(appId);
        await RunAsync(target, ct);
        Changed?.Invoke();
        return For(appId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadAsync(stoppingToken);
            await Task.Delay(FirstPassAfter, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = _time.GetUtcNow().UtcDateTime;
                try
                {
                    await PassAsync(stoppingToken);
                    if (started - _prunedAt >= TimeSpan.FromDays(1)) await PruneAsync(started, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "An uptime check pass failed");
                }
                var interval = TimeSpan.FromSeconds(await IntervalAsync(stoppingToken));
                var left = interval - (_time.GetUtcNow().UtcDateTime - started);
                if (left > TimeSpan.Zero) await Task.Delay(left, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>How each app stood when Gitberth stopped: its open incident, and its last checks.</summary>
    internal async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            foreach (var incident in await store.OpenIncidentsAsync(ct))
            {
                var t = TrackOf(incident.AppId);
                t.Open = incident;
                t.Status = UptimeStatus.Down;
            }
            foreach (var (appId, results) in await store.LatestAsync(FailuresToDown + 1, ct))
            {
                var t = TrackOf(appId);
                t.Last = results[^1];
                t.Address = t.Last.Address;
                var failing = results.AsEnumerable().Reverse().TakeWhile(r => !r.Up).Reverse().ToList();
                t.Failures = failing.Count;
                t.FirstFailure = failing.FirstOrDefault();
                if (t.Open is null) t.Status = t.Last.Up ? UptimeStatus.Up : UptimeStatus.Unknown;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the uptime history");
        }
    }

    /// <summary>One pass: every app that has something to check is checked (four at a time).</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        foreach (var target in targets.Where(t => t.Address is null)) await NotCheckedAsync(target, ct);
        // An app whose deploy is replacing its container is being updated, not down.
        var due = targets.Where(t => t.Address is not null && !downStates.IsUpdating(t.AppId)).ToList();
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (t, c) => await RunAsync(t, c));
        // Apps that are gone.
        foreach (var gone in _tracks.Keys.Except(targets.Select(t => t.AppId)).ToList()) _tracks.TryRemove(gone, out _);
        Changed?.Invoke();
    }

    private async Task RunAsync(Target target, CancellationToken ct)
    {
        if (target.Address is null)
        {
            await NotCheckedAsync(target, ct);
            return;
        }
        var result = await checker.CheckAsync(target.AppId, target.Address, ct);
        await RecordAsync(target, result, ct);
        if (target.Address.StartsWith("https://", StringComparison.Ordinal)) await CertificateAsync(target, ct);
    }

    /// <summary>A check's outcome: kept, and an incident opened or closed (and told) when it says so.</summary>
    internal async Task RecordAsync(Target target, UptimeResult result, CancellationToken ct)
    {
        Notice? notice = null;
        await _recording.WaitAsync(ct);
        try
        {
            await store.AddAsync(result, ct);
            var t = TrackOf(target.AppId);
            t.Last = result;
            t.Address = result.Address;
            t.Why = null;
            if (result.Up)
            {
                t.Failures = 0;
                t.FirstFailure = null;
                if (t.Open is { } open)
                {
                    await store.CloseIncidentAsync(open.Id, result.At, ct);
                    t.Open = null;
                    notice = await BackUpAsync(target, open, result.At, ct);
                }
                t.Status = UptimeStatus.Up;
            }
            else
            {
                t.Failures++;
                t.FirstFailure ??= result;
                if (t.Failures >= FailuresToDown && t.Open is null)
                {
                    var first = t.FirstFailure;
                    var cause = first.Problem ?? result.Problem ?? UptimeChecker.NotReachable;
                    var id = await store.OpenIncidentAsync(target.AppId, first.At, cause, result.Address, ct);
                    t.Open = new UptimeIncident(id, target.AppId, first.At, null, cause, result.Address);
                    notice = await DownAsync(target, t.Open, result, ct);
                }
                if (t.Open is not null) t.Status = UptimeStatus.Down;
                else if (t.Status is not UptimeStatus.Up) t.Status = UptimeStatus.Unknown;
            }
        }
        finally
        {
            _recording.Release();
        }
        if (notice is not null) notifier.Publish(notice);
    }

    // Not checked (stopped, checks off, nothing to check): an incident it was in ends quietly, as it isn't an outage now.
    private async Task NotCheckedAsync(Target target, CancellationToken ct)
    {
        await _recording.WaitAsync(ct);
        try
        {
            var t = TrackOf(target.AppId);
            if (t.Open is { } open)
            {
                await store.CloseIncidentAsync(open.Id, _time.GetUtcNow().UtcDateTime, ct);
                t.Open = null;
            }
            t.Failures = 0;
            t.FirstFailure = null;
            t.Status = target.Status;
            t.Why = target.Why;
            t.Address = null;
        }
        finally
        {
            _recording.Release();
        }
    }

    private async Task<Notice> DownAsync(Target target, UptimeIncident incident, UptimeResult result, CancellationToken ct) =>
        new(NotifyEvent.Down, NoticeLevel.Critical, $"{target.Name} is down",
            $"Its uptime check of {result.Address} failed twice in a row: {incident.Cause}. Gitberth tells you when it's back up.",
            await notifier.LinkAsync($"apps/{Uri.EscapeDataString(target.Container)}", ct), target.Name, target.Container, result.At)
        {
            AppId = target.AppId, Address = result.Address,
        };

    private async Task<Notice> BackUpAsync(Target target, UptimeIncident incident, DateTime at, CancellationToken ct) =>
        new(NotifyEvent.BackUp, NoticeLevel.Good, $"{target.Name} is back up",
            $"It was down for {Duration(at - incident.Started)} ({incident.Cause}).",
            await notifier.LinkAsync($"apps/{Uri.EscapeDataString(target.Container)}", ct), target.Name, target.Container, at)
        {
            AppId = target.AppId, Address = incident.Address,
        };

    /// <summary>A duration as people say it: "4 minutes", "1 hour 5 minutes", "under a minute".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.FromMinutes(1)) return "under a minute";
        static string Of(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n.ToString(CultureInfo.InvariantCulture)} {unit}s";
        var minutes = (int)Math.Round(d.TotalMinutes);
        if (minutes < 60) return Of(minutes, "minute");
        if (d < TimeSpan.FromDays(1)) return minutes % 60 == 0 ? Of(minutes / 60, "hour") : $"{Of(minutes / 60, "hour")} {Of(minutes % 60, "minute")}";
        var hours = (int)Math.Round(d.TotalHours);
        return hours % 24 == 0 ? Of(hours / 24, "day") : $"{Of(hours / 24, "day")} {Of(hours % 24, "hour")}";
    }

    /// <summary>
    /// What the check's TLS handshake said of the domain's certificate: kept, and told when it expires within 14 days, then
    /// within 3, once each (and never twice in a day for a host). A renewed certificate starts over.
    /// </summary>
    private async Task CertificateAsync(Target target, CancellationToken ct)
    {
        if (!Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) || certificates.ExpiresOf(uri.Host) is not { } notAfter) return;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var saved = await store.CertificateAsync(uri.Host, ct);
            var (fact, warn) = CertificateCheck(saved, uri.Host, notAfter, now);
            if (saved is null || fact != saved && (fact.NotAfter != saved.NotAfter || fact.WarnedDays != saved.WarnedDays || now - saved.CheckedAt >= TimeSpan.FromHours(1)))
                await store.SaveCertificateAsync(fact, ct);
            if (warn is int days)
                notifier.Publish(new Notice(NotifyEvent.CertificateExpiring, NoticeLevel.Warning,
                    $"The certificate of {uri.Host} expires in {(days <= 1 ? "a day" : $"{days} days")}",
                    $"{target.Name}'s domain {uri.Host} has a certificate that expires on {notAfter.ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture)} UTC. "
                    + "Cloudflare renews its own certificates long before that, so check the domain's SSL/TLS settings in Cloudflare.",
                    await notifier.LinkAsync($"apps/{Uri.EscapeDataString(target.Container)}", ct), target.Name, target.Container, now)
                {
                    AppId = target.AppId, Address = target.Address,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't keep the certificate of {Host}", uri.Host);
        }
    }

    /// <summary>
    /// What to keep of a certificate a check just saw, and the days left to tell about (null when there's nothing to tell):
    /// 14 or 3, the first time it's that close, and never twice in a day.
    /// </summary>
    internal static (CertificateFact Fact, int? Warn) CertificateCheck(CertificateFact? saved, string host, DateTime notAfter, DateTime now)
    {
        var renewed = saved is null || saved.NotAfter != notAfter;
        var (warnedDays, warnedAt) = renewed ? (null, null) : (saved!.WarnedDays, saved.WarnedAt);
        var left = notAfter - now;
        int? threshold = CertificateWarnings.Where(d => left <= TimeSpan.FromDays(d)).Select(d => (int?)d).LastOrDefault();
        int? warn = null;
        if (threshold is int th && (warnedDays is null || th < warnedDays) && (warnedAt is null || now - warnedAt >= TimeSpan.FromDays(1)))
        {
            warn = Math.Max(0, (int)Math.Ceiling(left.TotalDays));
            (warnedDays, warnedAt) = (th, now);
        }
        return (new CertificateFact(host.ToLowerInvariant(), notAfter, now, warnedDays, warnedAt), warn);
    }

    private async Task PruneAsync(DateTime now, CancellationToken ct)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await db.Apps.AsNoTracking().Select(a => a.Id).ToListAsync(ct);
        await store.PruneAsync(now, ids, ct);
        _prunedAt = now;
    }

    /// <summary>Every app (previews aside) with where it's checked, or why it isn't.</summary>
    internal async Task<List<Target>> TargetsAsync(CancellationToken ct)
    {
        var domainsOn = await domains.EnabledAsync(ct);
        var lanHost = await LanHostAsync(ct);
        List<AppDefinition> rows;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            rows = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().Where(a => a.ParentAppId == null).ToListAsync(ct);
        _containers = rows.GroupBy(a => a.ContainerName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        return rows.Select(a => TargetOf(a, domainsOn, lanHost)).ToList();
    }

    /// <summary>Where an app is checked: its main domain over HTTPS, else its web port on the LAN; or why it isn't.</summary>
    internal static Target TargetOf(AppDefinition app, bool domainsOn, string? lanHost)
    {
        Target Not(UptimeStatus status, string why) => new(app.Id, app.Name, app.ContainerName, null, status, why);
        if (!app.UptimeCheck) return Not(UptimeStatus.NotChecked, "Uptime checks are off for this app.");
        if (app.DesiredState == DesiredState.Stopped) return Not(UptimeStatus.Paused, $"{app.Name} is stopped, so it isn't checked.");
        if (app.Deployed && app.CurrentImage is null) return Not(UptimeStatus.NotChecked, "It's checked once its first deploy is live.");
        var path = PathOf(app.UptimePath);
        if (domainsOn && DomainService.MainOf(app.Domains) is { } host) return new(app.Id, app.Name, app.ContainerName, $"https://{host}{path}");
        if (PortRules.WebOf(app.Ports) is { HostPort: int port } web && web.Access is PortAccess.Lan or PortAccess.Custom)
        {
            var address = web.Access == PortAccess.Custom && web.HostIp is { Length: > 0 } ip && ip is not "0.0.0.0" and not "::" ? ip : lanHost;
            if (address is not null)
                return new(app.Id, app.Name, app.ContainerName, $"http://{(address.Contains(':') ? $"[{address}]" : address)}:{port.ToString(CultureInfo.InvariantCulture)}{path}");
        }
        return Not(UptimeStatus.NotChecked, "It has no domain, and no web port on the LAN to check.");
    }

    /// <summary>The path a check asks for: the app's, starting with a slash, or "/".</summary>
    public static string PathOf(string? path) => string.IsNullOrWhiteSpace(path) ? "/" : path.Trim().StartsWith('/') ? path.Trim() : "/" + path.Trim();

    // This host's LAN address, as the apps' LAN links use it; null when it isn't known.
    private async Task<string?> LanHostAsync(CancellationToken ct)
    {
        try
        {
            var host = apps is null ? options.PublicHost : await apps.GetPublicHostAsync("http://localhost/");
            return string.IsNullOrWhiteSpace(host) || host is "localhost" or "127.0.0.1" or "::1" ? null : host.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't tell this host's LAN address");
            return options.PublicHost;
        }
    }

    private Track TrackOf(int appId) => _tracks.GetOrAdd(appId, _ => new Track());
}
