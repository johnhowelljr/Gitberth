using System.Globalization;
using Gitberth.Apps;
using Gitberth.Services;

namespace Gitberth.Images;

/// <summary>
/// Settings › Updates as it's saved: how often images are checked, when automatic updates run, and new image apps' policy.
/// The Settings page and the AI agents' tool save it the same way, with the same checks and the same audit entry.
/// </summary>
public static class UpdateSettingsSave
{
    public const string DefaultWindow = "03:00-05:00";

    /// <summary>"When I ask", "Tell me", "Automatically": the policies as Settings names them.</summary>
    public static string PolicyName(UpdatePolicy policy) => policy switch
    {
        UpdatePolicy.Off => "When I ask",
        UpdatePolicy.Auto => "Automatically",
        _ => "Tell me",
    };

    public static string? HoursProblem(int hours) => hours is < 1 or > 168 ? "Every 1 to 168 hours." : null;

    public static string? WindowProblem(string? from, string? to) =>
        TimeOnly.TryParse(from, CultureInfo.InvariantCulture, out var f) && TimeOnly.TryParse(to, CultureInfo.InvariantCulture, out var t)
            ? f == t ? "Give the window some length." : null
            : "Pick both times.";

    /// <summary>Saves them and records what changed; the problem when one is wrong (then nothing is saved).</summary>
    public static async Task<string?> SaveAsync(SettingsService settings, Audit.AuditLog? audit, int hours, string from, string to, UpdatePolicy policy,
        CancellationToken ct = default)
    {
        if ((HoursProblem(hours) ?? WindowProblem(from, to)) is { } problem) return problem;
        var window = $"{from.Trim()}-{to.Trim()}";
        var before = (Hours: await settings.GetIntAsync(UpdateWatcher.CheckHoursKey, 6, ct), Window: await settings.GetAsync(UpdateWatcher.AutoWindowKey, ct) ?? DefaultWindow,
            Policy: await UpdateWatcher.DefaultPolicyAsync(settings, ct));
        await settings.SetAsync(UpdateWatcher.CheckHoursKey, hours.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(UpdateWatcher.AutoWindowKey, window, ct);
        await settings.SetAsync(UpdateWatcher.DefaultPolicyKey, policy.ToString(), ct);
        var changes = new List<Audit.AuditChange>();
        if (before.Hours != hours) changes.Add(new("Check for updates every", $"{before.Hours} hours", $"{hours} hours"));
        if (before.Window != window) changes.Add(new("Automatic updates run between", before.Window.Replace("-", " and "), window.Replace("-", " and ")));
        if (before.Policy != policy) changes.Add(new("New apps from an image", PolicyName(before.Policy), PolicyName(policy)));
        if (changes.Count > 0)
            audit?.Record(new Audit.AuditEntry("settings.updates.changed",
                $"Changed the update settings: {Audit.AuditText.Join(changes.Select(c => c.Field.ToLowerInvariant()).ToList())}.") { Target = "updates", Changes = changes });
        return null;
    }
}
