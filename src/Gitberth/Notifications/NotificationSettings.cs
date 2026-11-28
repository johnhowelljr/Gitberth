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
/// <param name="audit">Where destinations added and removed and the switches are recorded (names and kinds only, never addresses or tokens).</param>
public sealed class NotificationSettings(SettingsService settings, Audit.AuditLog? audit = null)
{
    /// <summary>
    /// What's sent when nothing was chosen yet: failures (an app down, an app Gitberth had to bring back, a certificate or a
    /// token about to expire, Docker's disk getting full too) and new versions, not successes.
    /// </summary>
    public static readonly ImmutableHashSet<NotifyEvent> DefaultEvents =
    [
        NotifyEvent.DeployFailed, NotifyEvent.Crashed, NotifyEvent.Unhealthy, NotifyEvent.Down, NotifyEvent.CertificateExpiring,
        NotifyEvent.BroughtBack, NotifyEvent.TokenExpiring, NotifyEvent.DiskLow,
        // ---- builder A ----
        NotifyEvent.BackupFailed, NotifyEvent.UpdateAvailable,
        // [builder B, B2]
        NotifyEvent.JobFailed,
    ];

    /// <summary>
    /// The kinds Settings turns on and off, in the order its switches are in. A health check passing again goes with
    /// Unhealthy, an app back up with Down, Gitberth giving up on an app with BroughtBack, a token refused with
    /// TokenExpiring, and room on the disk again with DiskLow; a test always goes.
    /// </summary>
    public static readonly NotifyEvent[] Choosable =
    [
        NotifyEvent.DeployFailed, NotifyEvent.DeploySucceeded, NotifyEvent.Crashed, NotifyEvent.Unhealthy, NotifyEvent.Down,
        NotifyEvent.BroughtBack, NotifyEvent.CertificateExpiring, NotifyEvent.TokenExpiring, NotifyEvent.DiskLow,
        // ---- builder A ----
        NotifyEvent.BackupFailed, NotifyEvent.UpdateAvailable,
        // [builder B, B2]
        NotifyEvent.JobFailed,
    ];

    /// <summary>
    /// The kinds Settings offered when the choices were saved as a plain list (1.2): a kind added since is on or off as its
    /// default says until it's chosen, so an app going down is told even to someone who changed their choices before.
    /// </summary>
    private static readonly NotifyEvent[] ChoosableIn12 = [NotifyEvent.DeployFailed, NotifyEvent.DeploySucceeded, NotifyEvent.Crashed, NotifyEvent.Unhealthy];

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
        NotifyEvent.BackUp => _events.Contains(NotifyEvent.Down),
        NotifyEvent.GaveUp => _events.Contains(NotifyEvent.BroughtBack),
        NotifyEvent.TokenRefused => _events.Contains(NotifyEvent.TokenExpiring),
        NotifyEvent.DiskOk => _events.Contains(NotifyEvent.DiskLow),
        // ---- builder A ----
        NotifyEvent.BackupOk => _events.Contains(NotifyEvent.BackupFailed),
        NotifyEvent.GitberthUpdated or NotifyEvent.GitberthUpdateFailed => _events.Contains(NotifyEvent.UpdateAvailable),
        // [builder B, B2]
        NotifyEvent.JobOk => _events.Contains(NotifyEvent.JobFailed),
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
            var before = at < 0 ? null : _destinations[at];
            _destinations = next;
            // Its name and kind only: an address, a webhook URL or a token is never written to the log.
            audit?.Record(new Audit.AuditEntry(at < 0 ? "settings.notifications.destination.added" : "settings.notifications.destination.changed",
                at < 0 ? $"Added the notification destination {clean.Name} ({DestinationText.KindName(clean.Kind)})."
                    : $"Changed the notification destination {clean.Name} ({DestinationText.KindName(clean.Kind)}).")
            {
                Target = clean.Name,
                Changes = before is not null && before.Name != clean.Name ? [new("Name", before.Name, clean.Name)] : [new("Kind", null, DestinationText.KindName(clean.Kind))],
            });
        });
    }

    public Task RemoveDestinationAsync(string id, CancellationToken ct = default) => UpdateAsync(async () =>
    {
        var next = _destinations.RemoveAll(c => c.Id == id);
        if (next.Count == _destinations.Count) return;
        var gone = _destinations.FirstOrDefault(c => c.Id == id);
        await settings.SetSecretAsync(SettingKeys.NotifyDestinations, next.IsEmpty ? null : JsonSerializer.Serialize(next, Json), ct);
        _destinations = next;
        if (gone is not null)
            audit?.Record(new Audit.AuditEntry("settings.notifications.destination.removed",
                $"Removed the notification destination {gone.Name} ({DestinationText.KindName(gone.Kind)}).") { Target = gone.Name });
    });

    public Task SetEventAsync(NotifyEvent e, bool on, CancellationToken ct = default)
    {
        if (!Choosable.Contains(e)) throw new ArgumentException($"{e} isn't chosen in Settings.", nameof(e));
        var what = When(e);
        return UpdateAsync(async () =>
        {
            var next = on ? _events.Add(e) : _events.Remove(e);
            if (next.SetEquals(_events)) return;
            await settings.SetAsync(SettingKeys.NotifyEvents, WriteEvents(next), ct);
            _events = next;
            audit?.Record(new Audit.AuditEntry("settings.notifications.changed", on ? $"Notifications are sent when {what} now." : $"No more notifications when {what}.")
            {
                Target = "notifications", Changes = [new($"When {what}", on ? "off" : "on", on ? "on" : "off")],
            });
        });
    }

    /// <summary>
    /// A switch of Settings › Notifications as the audit log's sentences name it: its title, in a sentence ("Notifications
    /// are sent when an app goes down now."). Every kind Settings offers has its own, so none is recorded as another.
    /// </summary>
    internal static string When(NotifyEvent e) => e switch
    {
        NotifyEvent.DeployFailed => "a deploy fails",
        NotifyEvent.DeploySucceeded => "a deploy succeeds",
        NotifyEvent.Crashed => "an app crashes",
        NotifyEvent.Unhealthy => "a health check fails",
        NotifyEvent.Down => "an app goes down",
        NotifyEvent.BroughtBack => "Gitberth brings an app back",
        NotifyEvent.CertificateExpiring => "a certificate is about to expire",
        NotifyEvent.TokenExpiring => "a token is about to expire or is refused",
        NotifyEvent.DiskLow => "Docker's disk gets full",
        // ---- builder A ----
        NotifyEvent.BackupFailed => "an automatic backup fails",
        NotifyEvent.UpdateAvailable => "a new version is out",
        // [builder B, B2]
        NotifyEvent.JobFailed => "a scheduled command fails",
        _ => throw new ArgumentException($"{e} isn't chosen in Settings.", nameof(e)),
    };

    /// <summary>The choices as they're saved: the kinds that are on, and every kind Settings offered then.</summary>
    internal static string WriteEvents(IEnumerable<NotifyEvent> on) => JsonSerializer.Serialize(new SavedEvents
    {
        On = [.. on.Where(Choosable.Contains).Order().Select(x => x.ToString())],
        Known = [.. Choosable.Order().Select(x => x.ToString())],
    }, Json);

    private sealed class SavedEvents
    {
        public List<string> On { get; set; } = [];
        public List<string> Known { get; set; } = [];
    }

    public Task SetPreviewsAsync(bool on, CancellationToken ct = default) => UpdateAsync(async () =>
    {
        if (on == _previews) return;
        await settings.SetAsync(SettingKeys.NotifyPreviews, on ? "true" : null, ct);
        _previews = on;
        audit?.Record(new Audit.AuditEntry("settings.notifications.changed", on ? "Notifications are sent about previews' deploys too now." : "No more notifications about previews' deploys.")
        {
            Target = "notifications", Changes = [new("Previews too", on ? "off" : "on", on ? "on" : "off")],
        });
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

    /// <summary>
    /// The kinds that are on, as saved: {"on": [...], "known": [...]} (a kind that wasn't known then is as its default says),
    /// or the plain list of 1.2 (which knew its four kinds). Unset or unreadable: the defaults.
    /// </summary>
    internal static ImmutableHashSet<NotifyEvent> ParseEvents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultEvents;
        try
        {
            IEnumerable<string> on, known;
            if (json.TrimStart().StartsWith('['))
            {
                on = JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
                known = ChoosableIn12.Select(e => e.ToString());
            }
            else
            {
                var saved = JsonSerializer.Deserialize<SavedEvents>(json, Json) ?? new SavedEvents();
                (on, known) = (saved.On, saved.Known);
            }
            var chosen = Names(on).Where(Choosable.Contains).ToHashSet();
            var seen = Names(known).ToHashSet();
            return [.. chosen, .. DefaultEvents.Where(e => Choosable.Contains(e) && !seen.Contains(e))];
        }
        catch (JsonException)
        {
            return DefaultEvents;
        }

        static IEnumerable<NotifyEvent> Names(IEnumerable<string> names) =>
            names.Select(s => Enum.TryParse<NotifyEvent>(s, out var e) && Enum.IsDefined(e) ? e : (NotifyEvent?)null).OfType<NotifyEvent>();
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
