using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>
/// The ports Gitberth notes for a container it didn't deploy (5.3): ones its program listens on that its image doesn't
/// declare, or, for a container on the host's network, the ones it's reached on. A note only lists the port on the
/// container's page; nothing restarts for one. Removing the container forgets them (<see cref="NetworkService.ForgetAsync(string, CancellationToken)"/>).
/// </summary>
public sealed class ContainerPortNotes(IDbContextFactory<GitberthDb> dbFactory)
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
        db.ContainerPortNotes.RemoveRange(saved.Where(n => !wanted.Contains((n.Port, n.Protocol))));
        db.ContainerPortNotes.AddRange(wanted.Where(w => !saved.Any(n => n.Port == w.Port && n.Protocol == w.Protocol))
            .Select(w => new ContainerPortNote { ContainerName = container, Port = w.Port, Protocol = w.Protocol }));
        await db.SaveChangesAsync(ct);
    }
}
