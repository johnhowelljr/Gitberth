using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>
/// The ports Gitberth notes for a container it didn't deploy (5.3): ones its program listens on that its image doesn't
/// declare, or, for a container on the host's network, the ones it's reached on. A note only lists the port on the
/// container's page; nothing restarts for one. Removing the container forgets them (<see cref="NetworkService.ForgetAsync(string, CancellationToken)"/>).
/// </summary>
public sealed class ContainerPortNotes(IDbContextFactory<GitberthDb> dbFactory, Audit.AuditLog? audit = null)
{
    /// <summary>The container's notes, by port.</summary>
    public async Task<IReadOnlyList<ContainerPortNote>> ListAsync(string container, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContainerPortNotes.AsNoTracking().Where(n => n.ContainerName == container).OrderBy(n => n.Port).ThenBy(n => n.Protocol).ToListAsync(ct);
    }

    /// <summary>Makes <paramref name="ports"/> the container's notes: the ones not among them go, and the new ones are added.</summary>
    public async Task SaveAsync(string container, IEnumerable<(int Port, string Protocol)> ports, CancellationToken ct = default)
    {
        var wanted = ports.Select(p => (p.Port, Protocol: p.Protocol.Trim().ToLowerInvariant())).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = await db.ContainerPortNotes.Where(n => n.ContainerName == container).ToListAsync(ct);
        var gone = saved.Where(n => !wanted.Contains((n.Port, n.Protocol))).ToList();
        var added = wanted.Where(w => !saved.Any(n => n.Port == w.Port && n.Protocol == w.Protocol)).ToList();
        db.ContainerPortNotes.RemoveRange(gone);
        db.ContainerPortNotes.AddRange(added.Select(w => new ContainerPortNote { ContainerName = container, Port = w.Port, Protocol = w.Protocol }));
        await db.SaveChangesAsync(ct);
        if (audit is null || (gone.Count == 0 && added.Count == 0)) return;
        static string Ports(IEnumerable<(int Port, string Protocol)> ports) =>
            ports.Any() ? string.Join(", ", ports.OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal).Select(p => $"{p.Port}/{p.Protocol}")) : "none";
        var before = saved.Select(n => (n.Port, n.Protocol)).ToList();
        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"noted {Ports(added)}");
        if (gone.Count > 0) parts.Add($"took off {Ports(gone.Select(n => (n.Port, n.Protocol)))}");
        // The app it belongs to, when it has a row: the notes are saved whether or not that can be read.
        (int Id, string Name)? app = null;
        try
        {
            app = (await db.Apps.AsNoTracking().Select(a => new { a.Id, a.Name, a.Container, a.Slug }).ToListAsync(ct))
                .Where(a => (a.Container ?? AppDefinition.ContainerNameFor(a.Slug)) == container).Select(a => ((int, string)?)(a.Id, a.Name)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        audit.Record(new Audit.AuditEntry("app.ports.noted", $"Changed the ports noted for {app?.Name ?? container}: {string.Join(" and ", parts)}.")
        {
            AppId = app?.Id, AppName = app?.Name, Container = container,
            Changes = [new Audit.AuditChange("Noted ports", Ports(before), Ports(wanted))],
        });
    }
}
