using System.Collections.Immutable;
using System.Text.Json;

namespace Gitberth.Services;

public enum RestartUnit { Minutes, Hours, Days }

/// <summary>How often a container is restarted: each time it has been up this long.</summary>
public readonly record struct RestartInterval(int Amount, RestartUnit Unit)
{
    public const int MinimumMinutes = 5;
    public const int MaximumDays = 365;

    public int TotalMinutes => (int)Minutes(Amount, Unit);

    public TimeSpan Span => TimeSpan.FromMinutes(TotalMinutes);

    /// <summary>The interval in the largest unit it fills exactly: 1440 minutes is 1 day, 90 minutes stays 90 minutes.</summary>
    public static RestartInterval FromMinutes(int minutes) =>
        minutes % 1440 == 0 ? new(minutes / 1440, RestartUnit.Days)
        : minutes % 60 == 0 ? new(minutes / 60, RestartUnit.Hours)
        : new(minutes, RestartUnit.Minutes);

    /// <summary>Why an amount and unit can't be a schedule, or null when they can.</summary>
    public static string? Problem(int? amount, RestartUnit unit)
    {
        if (amount is not > 0) return "Enter how often to restart it, as a whole number.";
        var minutes = Minutes(amount.Value, unit);
        if (minutes < MinimumMinutes) return $"The shortest schedule is every {MinimumMinutes} minutes.";
        if (minutes > MaximumDays * 1440L) return $"The longest schedule is every {MaximumDays} days.";
        return null;
    }

    /// <summary>"every 6 hours", "every day", "every 90 minutes".</summary>
    public override string ToString() => Amount == 1
        ? Unit switch { RestartUnit.Minutes => "every minute", RestartUnit.Hours => "every hour", _ => "every day" }
        : $"every {Amount} {Unit.ToString().ToLowerInvariant()}";

    private static long Minutes(int amount, RestartUnit unit) => unit switch
    {
        RestartUnit.Minutes => amount,
        RestartUnit.Hours => amount * 60L,
        _ => amount * 1440L,
    };
}

/// <summary>
/// The containers Gitberth restarts on a schedule. Kept by container name, so it covers containers Gitberth didn't start
/// as well, and held in memory like the Health page's choices, saved as one setting.
/// </summary>
public sealed class RestartSchedules(SettingsService settings)
{
    private static readonly ImmutableDictionary<string, int> None = ImmutableDictionary.Create<string, int>(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableDictionary<string, int> _minutes = None;

    /// <summary>Every schedule, in minutes, by container name.</summary>
    public ImmutableDictionary<string, int> All => _minutes;

    public RestartInterval? For(string container) => _minutes.TryGetValue(container, out var minutes) ? RestartInterval.FromMinutes(minutes) : null;

    /// <summary>Called once at start-up, before anything reads the schedules.</summary>
    public async Task LoadAsync(CancellationToken ct = default) =>
        _minutes = Parse(await settings.GetAsync(SettingKeys.RestartSchedules, ct));

    /// <param name="every">Null to stop restarting the container on a schedule.</param>
    public async Task SetAsync(string container, RestartInterval? every, CancellationToken ct = default)
    {
        if (every is { } e && RestartInterval.Problem(e.Amount, e.Unit) is { } problem) throw new ArgumentException(problem, nameof(every));
        await _gate.WaitAsync(ct);
        try
        {
            var next = every is { } interval ? _minutes.SetItem(container, interval.TotalMinutes) : _minutes.Remove(container);
            if (next == _minutes) return;
            await settings.SetAsync(SettingKeys.RestartSchedules,
                next.IsEmpty ? null : JsonSerializer.Serialize(new SortedDictionary<string, int>(next, StringComparer.Ordinal)), ct);
            _minutes = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ImmutableDictionary<string, int> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Key) && RestartInterval.Problem(s.Value, RestartUnit.Minutes) is null)
                .ToImmutableDictionary(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return None;
        }
    }
}
