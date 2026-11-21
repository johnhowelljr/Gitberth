using System.Text.Json;
using Gitberth.Data;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Disk;

/// <summary>
/// Images of a deleted app or preview that its deletion was asked to remove but couldn't (a container still used one): a
/// repository (gitberth/shop) or, for a Compose app, a prefix (gitberth/shop/). The disk cleanup removes them once nothing
/// uses them: only the ones made before the deletion, never a later app's with the slug.
/// </summary>
/// <param name="Since">When the app was deleted: a new app with the slug, made after it, has those tags as its own.</param>
public sealed record DiskLeftover(string Repo, bool Prefix, DateTime Since)
{
    /// <summary>The deleted app's slug: shop of gitberth/shop and of gitberth/shop/.</summary>
    public string Slug => DiskLeftovers.SlugOf(Repo) ?? "";

    /// <summary>Whether the tag is one of these: in the repository, or under the prefix.</summary>
    public bool Holds(string tag) => Prefix ? tag.StartsWith(Repo, StringComparison.Ordinal) : DiskLeftovers.Repository(tag) == Repo;

    /// <summary>
    /// Whether the tag, of an image made at <paramref name="created"/>, is one the deleted app's deletion asked to remove: in
    /// the repository or under the prefix, and made before the deletion. One made after it is a later app's with the slug.
    /// </summary>
    public bool Takes(string tag, DateTime? created) => Holds(tag) && (created is not { } at || at < Since);
}

/// <summary>The list in disk.leftovers, and what tags of Gitberth's belong to which app.</summary>
public static class DiskLeftovers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Deletions add to the list while the cleanup takes from it.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IReadOnlyList<DiskLeftover> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DiskLeftover>>(json, Json)?.Where(l => l.Repo.StartsWith("gitberth/", StringComparison.Ordinal)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? ToJson(IReadOnlyCollection<DiskLeftover> leftovers) => leftovers.Count == 0 ? null : JsonSerializer.Serialize(leftovers, Json);

    /// <summary>
    /// Records that a deleted app's images didn't all go (gitberth/shop, or gitberth/shop/ for a Compose app). Recorded
    /// already (an earlier app with the slug): the time moves to this deletion's, which asked for everything made before it
    /// to go. The cleanup removes them once no container uses them.
    /// </summary>
    public static async Task RecordAsync(IDbContextFactory<GitberthDb> dbFactory, string repo, bool prefix, DateTime now, CancellationToken ct = default) =>
        await ChangeAsync(dbFactory, list =>
        {
            list.RemoveAll(l => l.Repo == repo);
            list.Add(new DiskLeftover(repo, prefix, now));
            return true;
        }, ct);

    /// <summary>
    /// An app with the slug was deleted with its images kept: an earlier deletion's entry for the slug goes, so nothing of
    /// the slug's is removed by itself (Settings › Disk lists them, kept).
    /// </summary>
    public static Task ForgetAsync(IDbContextFactory<GitberthDb> dbFactory, string slug, CancellationToken ct = default) =>
        ChangeAsync(dbFactory, list => list.RemoveAll(l => l.Slug == slug) > 0, ct);

    /// <summary>Changes the list (<paramref name="change"/> says whether it did), one change at a time.</summary>
    public static async Task ChangeAsync(IDbContextFactory<GitberthDb> dbFactory, Func<List<DiskLeftover>, bool> change, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DiskLeftovers, ct);
            var list = Read(row?.Value).ToList();
            if (!change(list)) return;
            var json = ToJson(list);
            if (json is null)
            {
                if (row is not null) db.Settings.Remove(row);
            }
            else if (row is null)
            {
                db.Settings.Add(new Setting { Key = SettingKeys.DiskLeftovers, Value = json });
            }
            else
            {
                row.Value = json;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The repository of a tag: gitberth/shop of gitberth/shop:1a2b3c4 (a registry's port isn't a tag).</summary>
    public static string Repository(string tag)
    {
        var colon = tag.LastIndexOf(':');
        return colon > tag.LastIndexOf('/') ? tag[..colon] : tag;
    }

    /// <summary>The app a tag of Gitberth's belongs to: shop of gitberth/shop:1a2b3c4 and of gitberth/shop/web:pin-…; null for any other.</summary>
    public static string? SlugOf(string tag)
    {
        if (!tag.StartsWith("gitberth/", StringComparison.Ordinal)) return null;
        var rest = tag["gitberth/".Length..];
        var end = rest.IndexOfAny(['/', ':']);
        var slug = end < 0 ? rest : rest[..end];
        return slug.Length > 0 ? slug : null;
    }
}
