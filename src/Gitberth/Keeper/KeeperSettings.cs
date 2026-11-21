using Gitberth.Audit;
using Gitberth.Services;

namespace Gitberth.Keeper;

/// <summary>
/// Settings › Keep apps running's switch: whether Gitberth brings apps back by itself. On unless turned off; each change is
/// recorded in the audit log.
/// </summary>
public sealed class KeeperSettings(SettingsService settings, AuditLog? audit = null)
{
    /// <summary>Raised after the switch changed, for open pages and the check itself.</summary>
    public event Action? Changed;

    public async Task<bool> OnAsync(CancellationToken ct = default) => await settings.GetAsync(SettingKeys.KeepEnabled, ct) != "false";

    public async Task SetAsync(bool on, CancellationToken ct = default)
    {
        if (await OnAsync(ct) == on) return;
        await settings.SetAsync(SettingKeys.KeepEnabled, on ? null : "false", ct);
        audit?.Record(new AuditEntry("settings.keep.changed",
            on ? "Gitberth brings apps back by themselves now." : "Gitberth no longer brings apps back by themselves.")
        {
            Target = "keep apps running", Changes = [new("Bring apps back by themselves", AuditText.OnOff(!on), AuditText.OnOff(on))],
        });
        Changed?.Invoke();
    }
}
