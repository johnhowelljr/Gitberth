using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gitberth.Services;

namespace Gitberth.Notifications;

/// <summary>
/// Where notifications go and what they're about, held in memory (read on every container event) and saved as settings:
/// the channels encrypted as one value, the rest in clear. Apps left out are kept by container name, like the Health
/// page's choices, so any container can be.
/// </summary>
public sealed class NotificationSettings(SettingsService settings)
{
    /// <summary>What's sent when nothing was chosen yet: failures, not successes.</summary>
    public static readonly ImmutableHashSet<NotifyEvent> DefaultEvents = [NotifyEvent.DeployFailed, NotifyEvent.Crashed, NotifyEvent.Unhealthy];

    /// <summary>The kinds Settings turns on and off. A health check passing again goes with Unhealthy; a test always goes.</summary>
    public static readonly NotifyEvent[] Choosable = [NotifyEvent.DeployFailed, NotifyEvent.DeploySucceeded, NotifyEvent.Crashed, NotifyEvent.Unhealthy];

    public const int MaxDestinations = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableList<Destination> _destinations = [];
    private ImmutableHashSet<NotifyEvent> _events = DefaultEvents;
    private ImmutableHashSet<string> _muted = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private bool _previews;

    public IReadOnlyList<Destination> Destinations => _destinations;
    public bool Previews => _previews;
    public ImmutableHashSet<string> Muted => _muted;

    /// <summary>Raised after any change is saved, for open Settings pages.</summary>
    public event Action? Changed;

    public bool IsOn(NotifyEvent e) => e switch
    {
        NotifyEvent.Test => true,
        NotifyEvent.Healthy => _events.Contains(NotifyEvent.Unhealthy),
        _ => _events.Contains(e),
    };

    /// <summary>Whether the container's app is left out of notifications (its Settings tab).</summary>
    public bool IsMuted(string? container) => container is not null && _muted.Contains(container);

    /// <summary>Called once at start-up, before anything reads the settings.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _destinations = ParseDestinations(await settings.GetSecretAsync(SettingKeys.NotifyDestinations, ct));
        _events = ParseEvents(await settings.GetAsync(SettingKeys.NotifyEvents, ct));
        _muted = ParseMuted(await settings.GetAsync(SettingKeys.NotifyMuted, ct));
        _previews = await settings.GetAsync(SettingKeys.NotifyPreviews, ct) == "true";
    }

    /// <summary>Adds the channel, or replaces the one with its id. Throws with the reason when it can't be used.</summary>
    public Task SaveDestinationAsync(Destination channel, CancellationToken ct = default)
    {
        var clean = Clean(channel);
        if (DestinationText.Problem(clean) is { } problem) throw new ArgumentException(problem, nameof(channel));
        return UpdateAsync(async () =>
        {
            var at = _destinations.FindIndex(c => c.Id == clean.Id);
            if (at < 0 && _destinations.Count >= MaxDestinations) throw new ArgumentException($"Gitberth sends to at most {MaxDestinations} places.", nameof(channel));
            var next = at < 0 ? _destinations.Add(clean) : _destinations.SetItem(at, clean);
            await settings.SetSecretAsync(SettingKeys.NotifyDestinations, JsonSerializer.Serialize(next, Json), ct);
            _destinations = next;
        });
    }

    public Task RemoveDestinationAsync(string id, CancellationToken ct = default) => UpdateAsync(async () =>
    {
        var next = _destinations.RemoveAll(c => c.Id == id);
        if (next.Count == _destinations.Count) return;
        await settings.SetSecretAsync(SettingKeys.NotifyDestinations, next.IsEmpty ? null : JsonSerializer.Serialize(next, Json), ct);
        _destinations = next;
    });

    public Task SetEventAsync(NotifyEvent e, bool on, CancellationToken ct = default)
    {
        if (!Choosable.Contains(e)) throw new ArgumentException($"{e} isn't chosen in Settings.", nameof(e));
        return UpdateAsync(async () =>
        {
            var next = on ? _events.Add(e) : _events.Remove(e);
            if (next.SetEquals(_events)) return;
            await settings.SetAsync(SettingKeys.NotifyEvents, JsonSerializer.Serialize(next.Order().Select(x => x.ToString()), Json), ct);
            _events = next;
        });
    }

    public Task SetPreviewsAsync(bool on, CancellationToken ct = default) => UpdateAsync(async () =>
    {
        if (on == _previews) return;
        await settings.SetAsync(SettingKeys.NotifyPreviews, on ? "true" : null, ct);
        _previews = on;
    });

    /// <summary>Leaves the container's app out of notifications, or lets it back in.</summary>
    public Task SetMutedAsync(string container, bool muted, CancellationToken ct = default) => UpdateAsync(async () =>
    {
        var next = muted ? _muted.Add(container) : _muted.Remove(container);
        if (next.SetEquals(_muted)) return;
        await settings.SetAsync(SettingKeys.NotifyMuted, next.IsEmpty ? null : JsonSerializer.Serialize(next.Order(StringComparer.Ordinal), Json), ct);
        _muted = next;
    });

    private async Task UpdateAsync(Func<Task> change)
    {
        await _gate.WaitAsync();
        try
        {
            await change();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>The channel as it's kept: trimmed, the fields its kind doesn't use left out.</summary>
    public static Destination Clean(Destination c)
    {
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var id = string.IsNullOrWhiteSpace(c.Id) ? Destination.NewId() : c.Id;
        return c.Kind == DestinationKind.Email
            ? new Destination
            {
                Id = id, Kind = c.Kind, Name = c.Name.Trim(), SmtpHost = T(c.SmtpHost), SmtpPort = c.SmtpPort, Security = c.Security,
                SmtpUser = T(c.SmtpUser), SmtpPassword = string.IsNullOrEmpty(c.SmtpPassword) ? null : c.SmtpPassword,
                From = T(c.From), To = string.Join(", ", DestinationText.Addresses(c.To)),
            }
            : new Destination
            {
                Id = id, Kind = c.Kind, Name = c.Name.Trim(), Url = c.Url.Trim(),
                Token = c.Kind is DestinationKind.Ntfy or DestinationKind.Webhook ? T(c.Token) : null,
            };
    }

    internal static ImmutableList<Destination> ParseDestinations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Destination>>(json, Json) ?? []).Where(c => c.Id.Length > 0 && Enum.IsDefined(c.Kind)).ToImmutableList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static ImmutableHashSet<NotifyEvent> ParseEvents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultEvents;
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json, Json) ?? [])
                .Select(s => Enum.TryParse<NotifyEvent>(s, out var e) ? e : (NotifyEvent?)null)
                .OfType<NotifyEvent>().Where(Choosable.Contains).ToImmutableHashSet();
        }
        catch (JsonException)
        {
            return DefaultEvents;
        }
    }

    internal static ImmutableHashSet<string> ParseMuted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json, Json) ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToImmutableHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        }
    }
}
