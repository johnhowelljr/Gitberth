using System.Globalization;
using Gitberth.Services;

namespace Gitberth.Audit;

/// <summary>
/// Settings › Audit log: how long entries are kept (<see cref="RetentionDays"/>, 30 to 3650 days, a year unless changed) and
/// whether Docker changes made outside Gitberth are recorded (on unless turned off). Read once at startup and kept in
/// memory, so the event watcher never asks the database; every change is itself recorded.
/// </summary>
public sealed class AuditSettings(SettingsService settings, AuditLog audit)
{
    public const string RetentionKey = "audit.retentionDays";
    public const string OutsideKey = "audit.outsideChanges";
    public const int DefaultRetentionDays = 365;
    public const int MinRetentionDays = 30;
    public const int MaxRetentionDays = 3650;

    private volatile int _retention = DefaultRetentionDays;
    private volatile bool _outside = true;

    /// <summary>How many days entries are kept.</summary>
    public int RetentionDays => _retention;

    /// <summary>Whether Docker changes made outside Gitberth are recorded.</summary>
    public bool RecordOutside => _outside;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _retention = Retention(await settings.GetAsync(RetentionKey, ct));
        _outside = await settings.GetAsync(OutsideKey, ct) != "false";
        audit.RecordOutside = _outside;
    }

    /// <summary>The days a saved value stands for: the default when unset or unreadable, else within the limits.</summary>
    internal static int Retention(string? saved) =>
        int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? Math.Clamp(days, MinRetentionDays, MaxRetentionDays) : DefaultRetentionDays;

    /// <summary>Why <paramref name="days"/> can't be the retention; null when it can.</summary>
    public static string? RetentionProblem(int days) =>
        days is < MinRetentionDays or > MaxRetentionDays ? $"Keep entries for {MinRetentionDays} to {MaxRetentionDays} days." : null;

    /// <summary>Saves both, and records what changed. Returns why it wasn't saved, or null.</summary>
    public async Task<string?> SaveAsync(int retentionDays, bool recordOutside, CancellationToken ct = default)
    {
        if (RetentionProblem(retentionDays) is { } problem)
        {
            audit.Record(new AuditEntry("settings.audit.changed", $"Didn't change the audit log's settings: {problem}")
            {
                Outcome = AuditOutcome.Failed,
                Target = "audit log",
            });
            return problem;
        }
        var changes = new List<AuditChange>();
        if (retentionDays != _retention) changes.Add(new AuditChange("Keep entries for", Days(_retention), Days(retentionDays)));
        if (recordOutside != _outside) changes.Add(new AuditChange("Record changes made outside Gitberth", OnOff(_outside), OnOff(recordOutside)));
        if (changes.Count == 0) return null;
        await settings.SetAsync(RetentionKey, retentionDays == DefaultRetentionDays ? null : retentionDays.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(OutsideKey, recordOutside ? null : "false", ct);
        _retention = retentionDays;
        _outside = recordOutside;
        // Turned off, this change is still recorded: it's a person's, not Docker's.
        audit.RecordOutside = recordOutside;
        var parts = new List<string>();
        if (changes.Any(c => c.Field == "Keep entries for")) parts.Add($"keeps entries for {Days(retentionDays)} now");
        if (changes.Any(c => c.Field.StartsWith("Record", StringComparison.Ordinal)))
            parts.Add(recordOutside ? "records Docker changes made outside Gitberth again" : "no longer records Docker changes made outside Gitberth");
        audit.Record(new AuditEntry("settings.audit.changed", $"The audit log {string.Join(" and ", parts)}.")
        {
            Target = "audit log",
            Changes = changes,
        });
        return null;
    }

    internal static string Days(int days) => days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";

    private static string OnOff(bool on) => on ? "on" : "off";
}

/// <summary>Prunes the audit log once a day (the first time a few minutes after Gitberth starts), as its retention says.</summary>
public sealed class AuditMaintenance(AuditLog audit, AuditSettings settings, ILogger<AuditMaintenance> logger) : BackgroundService
{
    internal static readonly TimeSpan FirstPrune = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Every = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = AuditScope.Begin(AuditActor.System(AuditVia.Schedule));
        try
        {
            await Task.Delay(FirstPrune, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var removed = await audit.PruneAsync(settings.RetentionDays, stoppingToken);
                    if (removed > 0) logger.LogInformation("Pruned {Count} audit entries older than {Days} days", removed, settings.RetentionDays);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't prune the audit log; trying again tomorrow");
                }
                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
