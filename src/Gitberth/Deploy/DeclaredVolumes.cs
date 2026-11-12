using Gitberth.Data;
using Gitberth.Sources;

namespace Gitberth.Deploy;

/// <summary>
/// The VOLUMEs an app's image declares that none of its mounts cover. Docker makes a new anonymous volume for each of
/// those every time the container is made, so every deploy would start the app on an empty folder and leave the last
/// one's volume behind, data and all, with nothing in Gitberth pointing at it. A deploy keeps each in a named volume of
/// the app's own instead (gb-&lt;slug&gt;-&lt;last path segment&gt;), saved in its settings like any other, as New app
/// does for an image's volumes. A preview's are its own, named after its slug. A name that would be another app's volume
/// (<see cref="VolumeOwners"/>), or for a preview one Docker has already, gets a number too.
/// </summary>
public static class DeclaredVolumes
{
    /// <summary>The mounts to add: one per declared path that no mount is on, named after its last segment, -2 and on when taken.</summary>
    /// <param name="taken">Whether a name is taken by another app, given the app's volumes with it so far; null: only the app's own count.</param>
    /// <param name="tmpfs">Where the app has a tmpfs: that path is mounted already, and Docker refuses a second mount at it.</param>
    public static List<VolumeMount> Missing(IEnumerable<VolumeMount> mounts, IEnumerable<string> declared, Func<string, IReadOnlyList<string>, bool>? taken = null,
        IEnumerable<string>? tmpfs = null)
    {
        var have = mounts.ToList();
        var inMemory = (tmpfs ?? []).Select(Normal).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var added = new List<VolumeMount>();
        foreach (var target in declared.Select(Normal).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (inMemory.Contains(target) || have.Concat(added).Any(m => Normal(m.Target) == target)) continue;
            var name = DockerRunParser.Segment(target);
            var unique = name;
            for (var n = 2; have.Concat(added).Any(m => m.Source == unique) || taken?.Invoke(unique, [.. have.Concat(added).Select(m => m.Source)]) == true; n++)
                unique = $"{name}-{n}";
            added.Add(new VolumeMount { Source = unique, Target = target });
        }
        return added;
    }

    // "/data/" and "/data" are one place; a path that isn't absolute (or is the root) isn't one Docker mounts a volume at.
    private static string? Normal(string path)
    {
        var p = path.Trim();
        if (!p.StartsWith('/')) return null;
        p = p.TrimEnd('/');
        return p.Length == 0 ? null : p;
    }
}
