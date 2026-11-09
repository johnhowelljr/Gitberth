using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Components.Shared;

/// <summary>
/// The framework Gitberth detected for each Git app (AppDefinition.DetectedKind), by container name, for their logos in
/// lists: the catalog's summaries don't carry it. Read at most once a minute.
/// </summary>
public static class AppKinds
{
    public static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> cached = None;
    private static DateTime readAt = DateTime.MinValue;

    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(IDbContextFactory<GitberthDb> dbFactory, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - readAt < TimeSpan.FromMinutes(1)) return cached;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Apps.AsNoTracking().Where(a => a.DetectedKind != null).Select(a => new { a.Slug, a.DetectedKind }).ToListAsync(ct);
            cached = rows.ToDictionary(r => AppDefinition.ContainerNameFor(r.Slug), r => r.DetectedKind!);
            readAt = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // Logos fall back to the image's and the name's; nothing else depends on this.
        }
        return cached;
    }
}
