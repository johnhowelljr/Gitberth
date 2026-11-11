using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>
/// Which app has each named volume, as the apps' rows say. A named volume is named after its app
/// (gb-&lt;slug&gt;-&lt;name&gt;), but one app's name can still spell another's: orb's test-data and orb-test's data are
/// both gb-orb-test-data. A preview never shares one with another app: it would read and write that app's data, and
/// removing it would delete them. So a preview's name is picked so none of its volumes is taken (PreviewService), a
/// VOLUME an image declares is named so it isn't (DeclaredVolumes), a volume saved by hand whose name is taken is
/// refused, a deploy stops before it starts a preview on another app's volume or another app on a preview's, and removing
/// an app keeps a volume another app has. A preview keeps the row of every volume it has (PreviewCopy.SyncVolumes), so
/// its rows hold what its container mounts, even while a deploy of it is under way or after one failed.
/// </summary>
public sealed class VolumeOwners
{
    /// <summary>An app, as its volumes' names see it.</summary>
    public sealed record Owner(int Id, string Name, string Slug, int? ParentAppId);

    /// <summary>A name a named volume would get that another app has, or would get.</summary>
    /// <param name="Source">The volume, as the app's settings name it (data).</param>
    /// <param name="Name">The name Docker knows it by (gb-orb-test-data).</param>
    /// <param name="Preview">The preview whose copy of the volume it is; null for the app's own.</param>
    /// <param name="Other">The app that has it, or would.</param>
    /// <param name="OtherIsPreview">That app is a preview.</param>
    public sealed record Clash(string Source, string Name, string? Preview, string Other, bool OtherIsPreview)
    {
        /// <summary>The other app, as a sentence names it: "the preview orb · test", or "orb".</summary>
        public string Who => OtherIsPreview ? $"the preview {Other}" : Other;
    }

    private readonly Dictionary<int, Owner> _apps;
    // Every app's named volumes, by the names Docker knows them by.
    private readonly List<(int AppId, string Name)> _uses;

    public VolumeOwners(IEnumerable<Owner> apps, IEnumerable<(int AppId, string Source)> volumes)
    {
        _apps = apps.ToDictionary(a => a.Id);
        _uses = volumes.Where(v => !VolumeNames.IsBind(v.Source) && _apps.ContainsKey(v.AppId))
            .Select(v => (v.AppId, VolumeNames.Resolve(_apps[v.AppId].Slug, v.Source))).ToList();
    }

    /// <summary>Every app's named volumes, as they're saved.</summary>
    public static async Task<VolumeOwners> LoadAsync(GitberthDb db, CancellationToken ct = default)
    {
        var apps = await db.Apps.AsNoTracking().Select(a => new Owner(a.Id, a.Name, a.Slug, a.ParentAppId)).ToListAsync(ct);
        var volumes = await db.Volumes.AsNoTracking().Select(v => new { v.AppId, v.Source }).ToListAsync(ct);
        return new VolumeOwners(apps, volumes.Select(v => (v.AppId, v.Source)));
    }

    /// <summary>The names of every app's named volumes.</summary>
    public IEnumerable<string> Names => _uses.Select(u => u.Name);

    /// <summary>The app other than <paramref name="appId"/> that has the volume <paramref name="name"/>; null when none does.</summary>
    public string? OtherThan(int appId, string name) => OwnerOf(name, appId)?.Name;

    // The first app besides the one with this ID that has the volume.
    private Owner? OwnerOf(string name, int besides) =>
        _uses.Where(u => u.AppId != besides && u.Name == name).Select(u => _apps[u.AppId]).FirstOrDefault();

    /// <summary>
    /// A volume of <paramref name="app"/>'s that a preview would share with another app: the app is a preview and another
    /// app has it, or the app that has it is a preview. Null when there's none. Two apps that aren't previews are left as
    /// they were.
    /// </summary>
    public Clash? SharedWithPreview(AppDefinition app, IEnumerable<VolumeMount> volumes)
    {
        foreach (var source in volumes.Select(v => v.Source).Where(s => !VolumeNames.IsBind(s)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var name = VolumeNames.Resolve(app.Slug, source);
            var other = _uses.Where(u => u.AppId != app.Id && u.Name == name && (app.IsPreview || _apps[u.AppId].ParentAppId is not null))
                .Select(u => _apps[u.AppId]).FirstOrDefault();
            if (other is not null) return new Clash(source, name, null, other.Name, other.ParentAppId is not null);
        }
        return null;
    }

    /// <summary>
    /// Who would share the name <paramref name="app"/>'s named volume <paramref name="source"/> gets: in the app itself, or
    /// in a preview of it (a preview gets its parent's named volumes, named after its own name). That's another app that
    /// has the name, or the app or one of its previews that would get it from <paramref name="sources"/>, the app's named
    /// volumes with this one. A name the app, or the preview, has already is its own. Null when no one would.
    /// </summary>
    public Clash? ClashOf(AppDefinition app, string source, IEnumerable<string> sources)
    {
        if (VolumeNames.IsBind(source)) return null;
        var named = sources.Append(source).Where(s => !VolumeNames.IsBind(s)).Distinct(StringComparer.Ordinal).ToList();
        List<Owner> rows = [new(app.Id, app.Name, app.Slug, app.ParentAppId), .. _apps.Values.Where(a => a.ParentAppId == app.Id && a.Id != app.Id).OrderBy(a => a.Id)];
        foreach (var row in rows)
        {
            var name = VolumeNames.Resolve(row.Slug, source);
            if (_uses.Contains((row.Id, name))) continue;
            var other = OwnerOf(name, row.Id) ?? rows.FirstOrDefault(r => r.Id != row.Id && named.Any(s => VolumeNames.Resolve(r.Slug, s) == name));
            if (other is not null) return new Clash(source, name, row.Id == app.Id ? null : row.Name, other.Name, other.ParentAppId is not null);
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="app"/>'s volumes can't be saved as <paramref name="volumes"/>, in the words the form shows: a
    /// named volume whose name, the app's own or a preview's copy, another app has or would get (<see cref="ClashOf"/>).
    /// Empty when they can be.
    /// </summary>
    public List<string> Problems(AppDefinition app, IEnumerable<VolumeMount> volumes)
    {
        var sources = volumes.Select(v => v.Source.Trim()).Where(s => s.Length > 0 && !VolumeNames.IsBind(s)).Distinct(StringComparer.Ordinal).ToList();
        return sources.Select(s => ClashOf(app, s, sources)).OfType<Clash>()
            .Select(c => c.Preview is null
                ? $"The volume {c.Source} would be {c.Name}, which is also {c.Who}'s. Give it another name{(c.OtherIsPreview ? ", or remove that preview first" : "")}."
                : $"The volume {c.Source} would be {c.Name} in the preview {c.Preview}, which is also {c.Who}'s. Give it another name, or remove that preview first.")
            .ToList();
    }
}
