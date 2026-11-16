using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

/// <summary>
/// Gitberth's public pages through the tunnel: the status page's host name (Settings › Uptime and status page) sends the
/// whole host to Gitberth with the status page's marker, routed like GitHub's webhook host and with its DNS record like any
/// domain; and the domains held back on their down page.
/// </summary>
public sealed partial class DomainService
{
    // The protected domains the last sync held back on their down page (their protection couldn't be applied).
    private volatile HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    // Gitberth's own service as the last sync worked it out, and saved (SettingKeys.OwnService).
    private string? _ownServiceSaved;

    /// <summary>The protected domains the last sync held back: only their down page is served until their protection can be applied.</summary>
    public IReadOnlySet<string> HeldHosts => _held;

    /// <summary>The host name the status page is served on through the tunnel; null when there's none.</summary>
    public async Task<string?> StatusHostAsync(CancellationToken ct = default) => await settings.GetAsync(SettingKeys.StatusHost, ct);

    /// <summary>
    /// Sets (or, with an empty one, takes away) the host name the status page is served on through the tunnel, and routes it:
    /// the old one's route and the DNS record Gitberth added go the way a removed domain's do. A name one of the apps has,
    /// GitHub's webhook host, or one with routes of someone else's in the tunnel isn't taken. Needs domains on.
    /// </summary>
    public async Task<(string? Error, SyncReport Report)> SetStatusHostAsync(string? input, CancellationToken ct = default)
    {
        string? hostname = null;
        if (!string.IsNullOrWhiteSpace(input))
        {
            hostname = DomainNames.Normalize(input);
            if (DomainNames.Problem(hostname) is { } problem) return (problem, SyncReport.Nothing);
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return ("The status page's host name needs domains: turn them on in Settings › Domains first.", SyncReport.Nothing);
            var before = await StatusHostAsync(ct);
            if (string.Equals(before, hostname, StringComparison.OrdinalIgnoreCase)) return (null, await SyncIfTokenAsync(ct));
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (hostname is not null)
                {
                    if (await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Hostname == hostname, ct) is { } owner)
                    {
                        var name = await db.Apps.AsNoTracking().Where(a => a.Id == owner.AppId).Select(a => a.Name).FirstOrDefaultAsync(ct);
                        return ($"{hostname} is a domain of {name ?? "an app"}. Pick a name of its own for the status page, like status.example.com.", SyncReport.Nothing);
                    }
                    if (string.Equals(await WebhookHostAsync(ct), hostname, StringComparison.OrdinalIgnoreCase))
                        return ($"{hostname} is where GitHub's webhooks reach Gitberth. Pick a name of its own for the status page, like status.example.com.", SyncReport.Nothing);
                    if (await StatusHostTakenAsync(hostname, ct) is { } used) return (used, SyncReport.Nothing);
                    // Freed by an earlier change and still on its way out: it's this one's again.
                    if (await db.DomainRemovals.FindAsync([hostname], ct) is { } pending) db.DomainRemovals.Remove(pending);
                }
                if (before is not null && await db.DomainRemovals.FindAsync([before], ct) is null)
                    db.DomainRemovals.Add(new DomainRemoval { Hostname = before });
                await db.SaveChangesAsync(ct);
            }
            await settings.SetAsync(SettingKeys.StatusHost, hostname, ct);
            return (null, await SyncIfTokenAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Why the host can't be the status page's: someone else's rules are on it already. Null when it's free (or Cloudflare
    // can't be asked yet). The status page's own rule from before doesn't count.
    private async Task<string?> StatusHostTakenAsync(string hostname, CancellationToken ct)
    {
        if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return null;
        var (tunnel, _) = await ResolveTunnelAsync(connect: false, ct);
        if (tunnel is null) return null;
        try
        {
            var ingress = (await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, ct))["ingress"] as JsonArray ?? [];
            var others = ingress.OfType<JsonObject>().Any(r => r["hostname"] is JsonValue h && h.TryGetValue<string>(out var name)
                && string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase) && TunnelRoutes.HostHeaderOf(r) != Public.MarkerHosts.Status);
            return others ? $"{hostname} already has routes in your tunnel. Pick a name nothing uses yet, like status.example.com." : null;
        }
        catch (CloudflareApiException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Saves Gitberth's own service when it changed (one setting, written only then), so a domain on the down page, whose
    /// route goes there, isn't counted as routing Gitberth's own host port to that domain (<see cref="RoutedHostPortsAsync"/>).
    /// </summary>
    private async Task RememberOwnServiceAsync(string ownService, CancellationToken ct)
    {
        if (_ownServiceSaved == ownService) return;
        if (await settings.GetAsync(SettingKeys.OwnService, ct) != ownService) await settings.SetAsync(SettingKeys.OwnService, ownService, ct);
        _ownServiceSaved = ownService;
    }

    /// <summary>
    /// Why a domain shows the down page, in the Domains card's words; and whether Fix would change it (a domain the tunnel
    /// still sends to the down page though its app runs again).
    /// </summary>
    internal static (string Summary, DomainFix Fix) DownSummary(AppDefinition app, CustomDomain domain, DownReason? reason, bool held, bool running, string? protection) =>
        held || (domain.Protected && reason is null && running)
            ? ($"Shows only the down page until it can be put behind Cloudflare Access.{(protection is null ? "" : " " + protection)}", DomainFix.None)
            : reason switch
            {
                DownReason.Updating => ("Shows the down page while a deploy replaces the app's container.", DomainFix.None),
                DownReason.Stopped => ($"Shows the down page: {app.Name} is stopped.", DomainFix.None),
                DownReason.NotRunning => ($"Shows the down page: {app.Name}'s container isn't running.", DomainFix.None),
                _ when running => ($"The tunnel still shows the down page, though {app.Name} runs again.", DomainFix.Sync),
                _ => ($"Shows the down page: {app.Name} isn't running.", DomainFix.None),
            };

    /// <summary>
    /// The Host header of the owner's that a domain's rules to its app set (where the app runs, where Gitberth sent the
    /// domain, or where the app will be once it runs; never Gitberth's own service, whose rules always get their marker):
    /// the down page's marker would replace it for good, so the domain doesn't show the down page. Null when there's none.
    /// </summary>
    private static string? OwnersHostHeader(JsonArray ingress, TunnelInfo tunnel, AppDefinition app, CustomDomain domain, string? service, string? ownService) =>
        TunnelRoutes.OwnersHostHeader(ingress, domain.Hostname, app,
            [.. new[] { service, domain.Service, TunnelRoutes.ExpectedService(app, tunnel) }.Where(s => !TunnelRoutes.SameService(s, ownService))]);

    /// <summary>What's said about a domain kept off the down page because its route sets a Host header of the owner's.</summary>
    internal static string HostHeaderNote(AppDefinition app, string hostname, string header) =>
        $"{hostname} can't show Gitberth's down page: its route in the tunnel sets the Host header \"{header}\" for {app.Name}, which the down page would replace. Visitors get Cloudflare's error page instead.";
}
