using System.Collections.Immutable;
using System.Text.Json;
using Gitberth.Services;

namespace Gitberth.Health;

/// <summary>
/// Titles the Health page shows for containers that aren't Gitberth apps, in place of their container names, to tell
/// them apart (an app's card shows the app's name). Only Gitberth shows them: the containers keep their names. Kept by
/// container name, and held in memory like the page's other choices, saved as one setting.
/// </summary>
public sealed class ContainerTitles(SettingsService settings)
{
    public const int MaxLength = 60;

    private static readonly ImmutableDictionary<string, string> None = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableDictionary<string, string> _titles = None;

    /// <summary>The title given to a container, or null when it shows its own name.</summary>
    public string? For(string container) => _titles.TryGetValue(container, out var title) ? title : null;

    /// <summary>Called once at start-up, before anything reads the titles.</summary>
    public async Task LoadAsync(CancellationToken ct = default) =>
        _titles = Parse(await settings.GetAsync(SettingKeys.ContainerTitles, ct));

    /// <summary>Why a title can't be used, or null when it can.</summary>
    public static string? Problem(string? title) =>
        title?.Trim().Length > MaxLength ? $"Keep it to {MaxLength} characters." : null;

    /// <param name="title">Empty, or the container's own name, to show that again.</param>
    public async Task SetAsync(string container, string? title, CancellationToken ct = default)
    {
        if (Problem(title) is { } problem) throw new ArgumentException(problem, nameof(title));
        title = title?.Trim();
        await _gate.WaitAsync(ct);
        try
        {
            var next = string.IsNullOrEmpty(title) || title == container ? _titles.Remove(container) : _titles.SetItem(container, title);
            if (next == _titles) return;
            await settings.SetAsync(SettingKeys.ContainerTitles,
                next.IsEmpty ? null : JsonSerializer.Serialize(new SortedDictionary<string, string>(next, StringComparer.Ordinal)), ct);
            _titles = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ImmutableDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.Value))
                .ToImmutableDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return None;
        }
    }
}
