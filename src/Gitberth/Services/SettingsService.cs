using System.Globalization;
using Gitberth.Data;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

public static class SettingKeys
{
    public const string AdminPasswordHash = "admin.passwordHash";
    /// <summary>Changes with the admin password: a sign-in made under another stamp is signed out (AdminAuthService). Unset until the first change.</summary>
    public const string AdminStamp = "admin.stamp";
    public const string GitHubToken = "github.token";
    public const string GitHubLogin = "github.login";
    public const string GitHubTokenExpires = "github.tokenExpires";
    public const string PublicHost = "ui.publicHost";
    public const string PortRangeStart = "ports.rangeStart";
    public const string PortRangeEnd = "ports.rangeEnd";
    public const string WatchIntervalSeconds = "watch.intervalSeconds";
    public const string HealthSkipped = "health.skipped";
    /// <summary>Titles shown on the Health page for containers that aren't Gitberth apps: title by container name, as JSON (see ContainerTitles).</summary>
    public const string ContainerTitles = "health.titles";
    /// <summary>Scheduled restarts: minutes by container name, as JSON (see RestartSchedules).</summary>
    public const string RestartSchedules = "restart.schedules";
    /// <summary>"true" when Gitberth manages apps' domains through Cloudflare. Optional: off until it's turned on in Settings.</summary>
    public const string DomainsEnabled = "domains.enabled";
    public const string CloudflareToken = "cloudflare.token";
    /// <summary>The tunnel the token was saved for, so another cloudflared started later is never edited by mistake.</summary>
    public const string CloudflareTunnelId = "cloudflare.tunnelId";
    /// <summary>
    /// The block of LAN addresses Gitberth picks containers' own LAN addresses from: a CIDR (10.1.1.224/28) or a range
    /// (10.1.1.224-10.1.1.239) inside the host's subnet, kept out of the router's DHCP range. Unset while own LAN
    /// addresses are off.
    /// </summary>
    public const string OwnLanBlock = "lan.own.block";
}

/// <summary>Key/value settings stored in SQLite; secrets are encrypted.</summary>
public sealed class SettingsService(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (value is null)
        {
            if (row is not null) db.Settings.Remove(row);
        }
        else if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct);
        if (value is null) return null;
        var plain = protector.TryUnprotect(value);
        return plain.Length == 0 ? null : plain;
    }

    public Task SetSecretAsync(string key, string? value, CancellationToken ct = default) =>
        SetAsync(key, value is null ? null : protector.Protect(value), ct);

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
