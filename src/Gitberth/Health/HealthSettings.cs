using System.Collections.Immutable;
using System.Text.Json;
using Gitberth.Services;

namespace Gitberth.Health;

/// <summary>
/// The containers whose stats Gitberth doesn't gather. Kept by container name, so it covers containers Gitberth
/// didn't start as well, and held in memory so the sampler can check it every 5 seconds without a database call.
/// </summary>
public sealed class HealthSettings(SettingsService settings)
{
    private static readonly ImmutableHashSet<string> None = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableHashSet<string> _skipped = None;

    public ImmutableHashSet<string> Skipped => _skipped;

    public bool IsGathered(string container) => !_skipped.Contains(container);

    /// <summary>Called once at start-up, before anything reads <see cref="Skipped"/>.</summary>
    public async Task LoadAsync(CancellationToken ct = default) =>
        _skipped = Parse(await settings.GetAsync(SettingKeys.HealthSkipped, ct));

    public async Task SetGatheredAsync(string container, bool gather, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = gather ? _skipped.Remove(container) : _skipped.Add(container);
            if (next.Count == _skipped.Count) return;
            await settings.SetAsync(SettingKeys.HealthSkipped, next.IsEmpty ? null : JsonSerializer.Serialize(next.Order(StringComparer.Ordinal)), ct);
            _skipped = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ImmutableHashSet<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToImmutableHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return None;
        }
    }
}
