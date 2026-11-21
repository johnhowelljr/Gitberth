using System.Globalization;
using Gitberth.Audit;
using Gitberth.Services;

namespace Gitberth.Disk;

/// <summary>
/// Settings › Disk's choices: whether Gitberth cleans up Docker's disk by itself (on unless turned off), and how many days
/// the build cache no build uses is kept. Each change is recorded in the audit log.
/// </summary>
public sealed class DiskSettings(SettingsService settings, AuditLog? audit = null)
{
    /// <summary>Raised after a choice changed, for open pages.</summary>
    public event Action? Changed;

    public async Task<bool> OnAsync(CancellationToken ct = default) => await settings.GetAsync(SettingKeys.DiskCleanup, ct) != "false";

    public async Task<int> KeepDaysAsync(CancellationToken ct = default) => DiskRules.KeepDays(await settings.GetAsync(SettingKeys.DiskKeepDays, ct));

    public async Task SetOnAsync(bool on, CancellationToken ct = default)
    {
        if (await OnAsync(ct) == on) return;
        await settings.SetAsync(SettingKeys.DiskCleanup, on ? null : "false", ct);
        audit?.Record(new AuditEntry("settings.disk.changed",
            on ? "Gitberth cleans up Docker's disk by itself now." : "Gitberth no longer cleans up Docker's disk by itself.")
        {
            Target = "Docker's disk", Changes = [new("Clean up by itself", AuditText.OnOff(!on), AuditText.OnOff(on))],
        });
        Changed?.Invoke();
    }

    /// <summary>Saves the days; null when saved (or unchanged), else why not.</summary>
    public async Task<string?> SetKeepDaysAsync(int days, CancellationToken ct = default)
    {
        if (days is < DiskRules.MinKeepDays or > DiskRules.MaxKeepDays)
            return $"Keep it from {DiskRules.MinKeepDays} to {DiskRules.MaxKeepDays} days.";
        var before = await KeepDaysAsync(ct);
        if (before == days) return null;
        await settings.SetAsync(SettingKeys.DiskKeepDays, days == DiskRules.DefaultKeepDays ? null : days.ToString(CultureInfo.InvariantCulture), ct);
        audit?.Record(new AuditEntry("settings.disk.changed", $"Changed how long Docker's unused build cache is kept: {DiskText.Days(days)}.")
        {
            Target = "Docker's disk", Changes = [new("Keep unused build cache", DiskText.Days(before), DiskText.Days(days))],
        });
        Changed?.Invoke();
        return null;
    }
}
