using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gitberth.Audit;
using Gitberth.Services;

namespace Gitberth.Health;

/// <summary>Settings › Health alerts: when an app's CPU or memory counts as high, for every app that has no numbers of its own.</summary>
public sealed record AlertRules
{
    public bool On { get; init; } = true;
    public int CpuPercent { get; init; } = 90;
    public int CpuMinutes { get; init; } = 10;
    public int MemoryPercent { get; init; } = 90;
    public int MemoryMinutes { get; init; } = 5;
}

/// <summary>An app's alerts: Gitberth's defaults, numbers of its own, or none.</summary>
public enum AppAlertMode { Defaults, Own, Off }

/// <summary>One app's own choices (its Settings tab), kept by container name so they work for every container.</summary>
public sealed record AppHealthRules
{
    public AppAlertMode Alerts { get; init; } = AppAlertMode.Defaults;
    public int CpuPercent { get; init; } = 90;
    public int CpuMinutes { get; init; } = 10;
    public int MemoryPercent { get; init; } = 90;
    public int MemoryMinutes { get; init; } = 5;

    /// <summary>Whether Gitberth restarts it when Docker says it's unhealthy (while Settings › Keep apps running has it on).</summary>
    public bool RestartUnhealthy { get; init; } = true;
}

/// <summary>What an app's alerts are now, its own or the defaults: off, or the two thresholds and how long each must last.</summary>
public sealed record AlertThresholds(bool On, int CpuPercent, int CpuMinutes, int MemoryPercent, int MemoryMinutes);

/// <summary>All of it, as the setting health.rules holds it.</summary>
public sealed record HealthRulesConfig
{
    public AlertRules Alerts { get; init; } = new();

    /// <summary>Settings › Keep apps running: restart apps Docker says are unhealthy. On unless turned off.</summary>
    public bool RestartUnhealthy { get; init; } = true;

    public ImmutableDictionary<string, AppHealthRules> Apps { get; init; } = ImmutableDictionary<string, AppHealthRules>.Empty;
}

/// <summary>
/// Health alerts' and unhealthy restarts' settings (health.rules): Gitberth's defaults, the Keep apps running switch, and
/// each app's own choices by container name. Held in memory (read every 5 seconds by the alerts); each change is recorded
/// in the audit log.
/// </summary>
public sealed class HealthRules(SettingsService settings, AuditLog? audit = null)
{
    public const string Key = "health.rules";

    /// <summary>The thresholds a setting may take: percent 10 to 100, minutes 1 to 60.</summary>
    public const int MinPercent = 10, MaxPercent = 100, MinMinutes = 1, MaxMinutes = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthRulesConfig _current = new();

    public HealthRulesConfig Current => _current;

    /// <summary>Raised after a change was saved, for open pages, the alerts and the restarts.</summary>
    public event Action? Changed;

    /// <summary>Called once at start-up, before anything reads <see cref="Current"/>.</summary>
    public async Task LoadAsync(CancellationToken ct = default) => _current = Parse(await settings.GetAsync(Key, ct));

    internal static HealthRulesConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HealthRulesConfig();
        try
        {
            return JsonSerializer.Deserialize<HealthRulesConfig>(json, Json) ?? new HealthRulesConfig();
        }
        catch (JsonException)
        {
            return new HealthRulesConfig();
        }
    }

    /// <summary>The container's alerts now: its own numbers, or the defaults; off when either is.</summary>
    public AlertThresholds For(string container) => For(_current, container);

    internal static AlertThresholds For(HealthRulesConfig c, string container)
    {
        var own = c.Apps.GetValueOrDefault(container);
        if (!c.Alerts.On || own?.Alerts == AppAlertMode.Off) return new AlertThresholds(false, 0, 0, 0, 0);
        return own is { Alerts: AppAlertMode.Own }
            ? new AlertThresholds(true, own.CpuPercent, own.CpuMinutes, own.MemoryPercent, own.MemoryMinutes)
            : new AlertThresholds(true, c.Alerts.CpuPercent, c.Alerts.CpuMinutes, c.Alerts.MemoryPercent, c.Alerts.MemoryMinutes);
    }

    /// <summary>Whether an unhealthy container is restarted: Keep apps running's switch, and its own.</summary>
    public bool RestartsUnhealthy(string container) => _current.RestartUnhealthy && _current.Apps.GetValueOrDefault(container)?.RestartUnhealthy != false;

    public AppHealthRules App(string container) => _current.Apps.GetValueOrDefault(container) ?? new AppHealthRules();

    /// <summary>Why the numbers can't be saved, in a sentence; null when they can.</summary>
    public static string? Problem(int cpuPercent, int cpuMinutes, int memoryPercent, int memoryMinutes)
    {
        if (cpuPercent is < MinPercent or > MaxPercent || memoryPercent is < MinPercent or > MaxPercent) return $"A percentage goes from {MinPercent} to {MaxPercent}.";
        if (cpuMinutes is < MinMinutes or > MaxMinutes || memoryMinutes is < MinMinutes or > MaxMinutes) return $"Minutes go from {MinMinutes} to {MaxMinutes}.";
        return null;
    }

    public Task SaveAlertsAsync(AlertRules next, CancellationToken ct = default)
    {
        if (Problem(next.CpuPercent, next.CpuMinutes, next.MemoryPercent, next.MemoryMinutes) is { } problem) throw new ArgumentException(problem, nameof(next));
        return UpdateAsync(c => c with { Alerts = next }, c =>
        {
            var was = c.Alerts;
            if (was == next) return null;
            return new AuditEntry("settings.health.alerts.changed", next.On
                ? $"Health alerts: CPU above {next.CpuPercent}% for {next.CpuMinutes} min, memory above {next.MemoryPercent}% for {next.MemoryMinutes} min."
                : "Health alerts are off.")
            {
                Target = "health alerts",
                Changes = [new("Health alerts", Words(was), Words(next))],
            };
        }, ct);
    }

    public Task SetRestartUnhealthyAsync(bool on, CancellationToken ct = default) =>
        UpdateAsync(c => c with { RestartUnhealthy = on }, c => c.RestartUnhealthy == on ? null
            : new AuditEntry("settings.keep.unhealthy.changed", on ? "Gitberth restarts apps that turn unhealthy now." : "Gitberth no longer restarts apps that turn unhealthy.")
            {
                Target = "keep apps running", Changes = [new("Restart apps that turn unhealthy", AuditText.OnOff(!on), AuditText.OnOff(on))],
            }, ct);

    public Task SetAppAsync(string container, AppHealthRules next, CancellationToken ct = default)
    {
        if (next.Alerts == AppAlertMode.Own && Problem(next.CpuPercent, next.CpuMinutes, next.MemoryPercent, next.MemoryMinutes) is { } problem)
            throw new ArgumentException(problem, nameof(next));
        return UpdateAsync(c => c with { Apps = next == new AppHealthRules() ? c.Apps.Remove(container) : c.Apps.SetItem(container, next) }, c =>
        {
            var was = c.Apps.GetValueOrDefault(container) ?? new AppHealthRules();
            if (was == next) return null;
            var changes = new List<AuditChange>();
            if (AppWords(was) != AppWords(next)) changes.Add(new("Health alerts", AppWords(was), AppWords(next)));
            if (was.RestartUnhealthy != next.RestartUnhealthy) changes.Add(new("Restart when unhealthy", AuditText.OnOff(was.RestartUnhealthy), AuditText.OnOff(next.RestartUnhealthy)));
            return new AuditEntry("app.health.changed", $"Changed {container}'s health alerts and restarts.") { Container = container, Target = container, Changes = changes };
        }, ct);
    }

    internal static string Words(AlertRules r) => r.On ? $"CPU {r.CpuPercent}% for {r.CpuMinutes} min, memory {r.MemoryPercent}% for {r.MemoryMinutes} min" : "off";

    internal static string AppWords(AppHealthRules r) => r.Alerts switch
    {
        AppAlertMode.Off => "off",
        AppAlertMode.Own => $"CPU {r.CpuPercent}% for {r.CpuMinutes} min, memory {r.MemoryPercent}% for {r.MemoryMinutes} min",
        _ => "Gitberth's defaults",
    };

    private async Task UpdateAsync(Func<HealthRulesConfig, HealthRulesConfig> change, Func<HealthRulesConfig, AuditEntry?> entry, CancellationToken ct)
    {
        AuditEntry? said;
        await _gate.WaitAsync(ct);
        try
        {
            var next = change(_current);
            said = entry(_current);
            if (said is null) return;
            await settings.SetAsync(Key, JsonSerializer.Serialize(next, Json), ct);
            _current = next;
        }
        finally
        {
            _gate.Release();
        }
        audit?.Record(said);
        Changed?.Invoke();
    }
}
