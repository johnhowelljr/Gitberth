using Gitberth.Audit;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

/// <summary>
/// The domains' entries in the audit log: each change a person (or a deploy, or a sync) makes to an app's domains, to the
/// Cloudflare settings and protection, and to the tunnel. DNS records and Access applications are recorded where they're
/// written (<see cref="CloudflareApi"/>).
/// </summary>
public sealed partial class DomainService
{
    /// <summary>A domain change of an app: done, or (with <paramref name="error"/>) why it wasn't.</summary>
    private async Task AuditAppAsync(int appId, string action, string hostname, Func<string, string> done, Func<string, string>? failed = null, string? error = null,
        IReadOnlyList<AuditChange>? changes = null)
    {
        if (audit is null) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var app = await db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => new { a.Name, a.Container, a.Slug }).FirstOrDefaultAsync();
            var name = app?.Name ?? $"app {appId}";
            audit.Record(new AuditEntry(action, error is null ? done(name) : (failed ?? (n => $"Couldn't change {hostname} on {n}"))(name) + $": {Reason(error)}.")
            {
                Outcome = error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                AppId = app is null ? null : appId, AppName = app?.Name, Container = app is null ? null : app.Container ?? Data.AppDefinition.ContainerNameFor(app.Slug),
                Target = hostname, Changes = changes,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Couldn't record a change of {Hostname}", hostname);
        }
    }

    /// <summary>A change to the domains' own settings (Settings › Domains, the webhook host, protection).</summary>
    private void AuditSetting(string action, string summary, string? target = null, IReadOnlyList<AuditChange>? changes = null, string? error = null) =>
        audit?.Record(new AuditEntry(action, error is null ? summary : $"{summary.TrimEnd('.')} failed: {Reason(error)}.")
        {
            Outcome = error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            Target = target, Changes = changes,
        });

    /// <summary>
    /// A sync changed the tunnel's routes: one entry with what it did (its notes), as whoever's change caused the sync (a
    /// person, a deploy, the retry).
    /// </summary>
    private void AuditTunnel(TunnelInfo tunnel, long? version, IEnumerable<DomainNote> notes)
    {
        if (audit is null) return;
        var said = notes.Select(n => n.Text).Where(t => t.Length > 0).Distinct().ToList();
        audit.Record(new AuditEntry("cloudflare.tunnel.changed",
            said.Count == 0 ? $"Changed the routes of the tunnel {tunnel.TunnelId[..Math.Min(8, tunnel.TunnelId.Length)]}."
            : said.Count == 1 ? $"Changed the tunnel's routes: {said[0].TrimEnd('.')}."
            : $"Changed the tunnel's routes: {said[0].TrimEnd('.')}, and {said.Count - 1} more.")
        {
            Target = $"tunnel {tunnel.TunnelId[..Math.Min(8, tunnel.TunnelId.Length)]}",
            Changes = [.. said.Take(20).Select(t => new AuditChange("Route", null, t)), .. version is long v ? new[] { new AuditChange("Version", null, AuditText.N(v)) } : []],
        });
    }

    private static string Reason(string error) => AuditLog.Clean(error, 300)?.TrimEnd('.') ?? "";
}
