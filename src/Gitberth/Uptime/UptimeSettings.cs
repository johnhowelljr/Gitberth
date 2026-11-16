using System.Globalization;
using System.Text.Json;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Uptime;

/// <summary>The status page as Settings › Uptime and status page has it.</summary>
/// <param name="Apps">The apps it lists, by id; null for every app with uptime checks.</param>
/// <param name="Host">The host name it's served on through the tunnel; null when it's only on the LAN.</param>
public sealed record StatusPageSettings(bool Enabled, string Title, string? Description, IReadOnlyList<int>? Apps, bool ResponseTimes, string? Host)
{
    public const string DefaultTitle = "Status";
    public const int MaxTitle = 60, MaxDescription = 280;

    /// <summary>Whether it lists the app: one chosen, or (when none are) any app with uptime checks on.</summary>
    public bool Lists(AppDefinition app) => !app.IsPreview && (Apps is null ? app.UptimeCheck : Apps.Contains(app.Id));
}

/// <summary>
/// Saves what the uptime checks, the status page and the down page are set to, and records each change in the audit log:
/// the check interval and the status page (Settings), and per app its uptime check and its down page (saved on their own,
/// outside the app's other settings).
/// </summary>
public sealed class UptimeSettings(SettingsService settings, IDbContextFactory<GitberthDb> dbFactory, DomainService domains, DownStates downStates, AuditLog audit)
{
    public const int MaxPath = 200, MaxMessage = 280;

    /// <summary>Raised after any change is saved, for open pages.</summary>
    public event Action? Changed;

    public async Task<int> IntervalAsync(CancellationToken ct = default) =>
        Math.Clamp(await settings.GetIntAsync(SettingKeys.UptimeIntervalSeconds, UptimeMonitor.DefaultInterval, ct), UptimeMonitor.MinInterval, UptimeMonitor.MaxInterval);

    /// <summary>Saves how often apps are checked; returns why it can't be, or null.</summary>
    public async Task<string?> SetIntervalAsync(int seconds, CancellationToken ct = default)
    {
        if (seconds is < UptimeMonitor.MinInterval or > UptimeMonitor.MaxInterval)
            return $"Check every {UptimeMonitor.MinInterval} to {UptimeMonitor.MaxInterval} seconds.";
        var before = await IntervalAsync(ct);
        if (before == seconds) return null;
        await settings.SetAsync(SettingKeys.UptimeIntervalSeconds, seconds == UptimeMonitor.DefaultInterval ? null : seconds.ToString(CultureInfo.InvariantCulture), ct);
        audit.Record(new AuditEntry("settings.uptime.changed", $"Changed how often apps are checked to every {Every(seconds)}.")
        {
            Target = "Uptime checks",
            Changes = [new AuditChange("Check interval", Every(before), Every(seconds))],
        });
        Changed?.Invoke();
        return null;
    }

    private static string Every(int seconds) => seconds % 60 == 0 ? (seconds == 60 ? "minute" : $"{seconds / 60} minutes") : $"{seconds} seconds";

    public async Task<StatusPageSettings> StatusAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.StatusEnabled, ct) == "true",
        await settings.GetAsync(SettingKeys.StatusTitle, ct) is { Length: > 0 } title ? title : StatusPageSettings.DefaultTitle,
        await settings.GetAsync(SettingKeys.StatusDescription, ct) is { Length: > 0 } description ? description : null,
        ParseApps(await settings.GetAsync(SettingKeys.StatusApps, ct)),
        await settings.GetAsync(SettingKeys.StatusResponseTimes, ct) == "true",
        await domains.StatusHostAsync(ct));

    internal static IReadOnlyList<int>? ParseApps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) is { } ids ? ids.Distinct().Order().ToList() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the status page's settings; its host name goes through the domains (routed like GitHub's webhook host). Returns
    /// why something couldn't be saved, or null. What could be saved is saved.
    /// </summary>
    public async Task<string?> SaveStatusAsync(StatusPageSettings next, CancellationToken ct = default)
    {
        var title = OneLine(next.Title);
        var description = OneLine(next.Description);
        if (title.Length > StatusPageSettings.MaxTitle) return $"Keep the title to {StatusPageSettings.MaxTitle} characters.";
        if (description.Length > StatusPageSettings.MaxDescription) return $"Keep the description to {StatusPageSettings.MaxDescription} characters.";
        var before = await StatusAsync(ct);
        var changes = new List<AuditChange>();
        void Change(string field, string? from, string? to)
        {
            if (from != to) changes.Add(new AuditChange(field, from, to));
        }
        await settings.SetAsync(SettingKeys.StatusEnabled, next.Enabled ? "true" : null, ct);
        Change("Status page", before.Enabled ? "on" : "off", next.Enabled ? "on" : "off");
        var savedTitle = title.Length == 0 ? StatusPageSettings.DefaultTitle : title;
        await settings.SetAsync(SettingKeys.StatusTitle, savedTitle == StatusPageSettings.DefaultTitle ? null : savedTitle, ct);
        Change("Title", before.Title, savedTitle);
        await settings.SetAsync(SettingKeys.StatusDescription, description.Length == 0 ? null : description, ct);
        Change("Description", before.Description, description.Length == 0 ? null : description);
        var apps = next.Apps?.Distinct().Order().ToList();
        await settings.SetAsync(SettingKeys.StatusApps, apps is null ? null : JsonSerializer.Serialize(apps), ct);
        Change("Apps", Listed(before.Apps), Listed(apps));
        await settings.SetAsync(SettingKeys.StatusResponseTimes, next.ResponseTimes ? "true" : null, ct);
        Change("Response times", before.ResponseTimes ? "shown" : "hidden", next.ResponseTimes ? "shown" : "hidden");
        string? error = null;
        var host = string.IsNullOrWhiteSpace(next.Host) ? null : DomainNames.Normalize(next.Host) ?? next.Host.Trim();
        if (!string.Equals(host, before.Host, StringComparison.OrdinalIgnoreCase))
        {
            (error, _) = await domains.SetStatusHostAsync(host, ct);
            if (error is null) Change("Host name", before.Host, host);
        }
        if (changes.Count > 0)
            audit.Record(new AuditEntry("settings.status.changed", changes.Count == 1 && changes[0].Field == "Status page"
                ? next.Enabled ? "Turned the status page on." : "Turned the status page off."
                : "Changed the status page's settings.")
            {
                Target = "Status page",
                Changes = changes,
            });
        Changed?.Invoke();
        return error;
    }

    private static string Listed(IReadOnlyList<int>? apps) => apps is null ? "every app with uptime checks" : string.Join(", ", apps.Select(a => $"#{a}"));

    private static string OneLine(string? text) => string.Join(' ', (text ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

    /// <summary>What's wrong with a path the uptime check would ask for, or null.</summary>
    public static string? PathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.Length > MaxPath) return $"Keep the path to {MaxPath} characters.";
        if (!p.StartsWith('/')) return "Start the path with /, like /health.";
        if (p.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || p.StartsWith("//", StringComparison.Ordinal)) return "Use a path like /health, without spaces.";
        return Uri.TryCreate("https://example.com" + p, UriKind.Absolute, out _) ? null : "Use a path like /health.";
    }

    /// <summary>Turns the app's uptime check on or off and sets the path it asks for; returns why it can't, or null.</summary>
    public async Task<string?> SetUptimeAsync(int appId, bool on, string? path, CancellationToken ct = default)
    {
        if (PathProblem(path) is { } problem) return problem;
        var clean = string.IsNullOrWhiteSpace(path) || path.Trim() == "/" ? null : path.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct) is not { } app) return "This app doesn't exist any more.";
        if (app.UptimeCheck == on && app.UptimePath == clean) return null;
        var changes = new List<AuditChange>();
        if (app.UptimeCheck != on) changes.Add(new AuditChange("Uptime check", app.UptimeCheck ? "on" : "off", on ? "on" : "off"));
        if (app.UptimePath != clean) changes.Add(new AuditChange("Path", UptimeMonitor.PathOf(app.UptimePath), UptimeMonitor.PathOf(clean)));
        var turned = app.UptimeCheck != on;
        (app.UptimeCheck, app.UptimePath) = (on, clean);
        await db.SaveChangesAsync(ct);
        audit.Record(new AuditEntry("app.uptime.changed", turned
            ? $"Turned {(on ? "on" : "off")} the uptime check of {app.Name}."
            : $"Changed the path {app.Name}'s uptime check asks for to {UptimeMonitor.PathOf(clean)}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
        Changed?.Invoke();
        return null;
    }

    /// <summary>Every app the status page could list (previews aside), by name, with whether its uptime check is on.</summary>
    public async Task<List<(int Id, string Name, bool UptimeCheck)>> AppsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null).Select(a => new { a.Id, a.Name, a.UptimeCheck }).ToListAsync(ct))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => (a.Id, a.Name, a.UptimeCheck)).ToList();
    }

    /// <summary>The app's down page and uptime check as saved; null when there's no such app.</summary>
    public async Task<(bool DownPage, string? Message, bool Uptime, string? Path)?> OfAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Where(a => a.Id == appId)
            .Select(a => new { a.DownPage, a.DownPageMessage, a.UptimeCheck, a.UptimePath })
            .FirstOrDefaultAsync(ct) is { } row ? (row.DownPage, row.DownPageMessage, row.UptimeCheck, row.UptimePath) : null;
    }

    /// <summary>
    /// Turns the app's down page on or off and sets its message; its domains follow at once (a stopped app's go to its down
    /// page, or back). Returns why it can't, or null.
    /// </summary>
    public async Task<string?> SetDownPageAsync(int appId, bool on, string? message, CancellationToken ct = default)
    {
        var text = OneLine(message);
        if (text.Length > MaxMessage) return $"Keep the message to {MaxMessage} characters.";
        var clean = text.Length == 0 ? null : text;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct) is not { } app) return "This app doesn't exist any more.";
        if (app.DownPage == on && app.DownPageMessage == clean) return null;
        var changes = new List<AuditChange>();
        if (app.DownPage != on) changes.Add(new AuditChange("Down page", app.DownPage ? "on" : "off", on ? "on" : "off"));
        if (app.DownPageMessage != clean) changes.Add(new AuditChange("Message", app.DownPageMessage, clean));
        var turned = app.DownPage != on;
        (app.DownPage, app.DownPageMessage) = (on, clean);
        await db.SaveChangesAsync(ct);
        audit.Record(new AuditEntry("app.downpage.changed", turned
            ? $"Turned {(on ? "on" : "off")} the down page for {app.Name}."
            : $"Changed the message on {app.Name}'s down page.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
        if (turned) downStates.Nudge(appId);
        Changed?.Invoke();
        return null;
    }
}
