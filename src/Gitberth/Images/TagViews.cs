namespace Gitberth.Images;

/// <summary>The rows a tag list shows under one variant chip, for the tag picker and list_image_tags.</summary>
public static class TagViews
{
    /// <summary>
    /// The Versions rows in <paramref name="family"/> (null: All), newest first; pre-releases only with
    /// <paramref name="preReleases"/>. A non-empty <paramref name="filter"/> searches All, Versions and Other, for any member
    /// containing it (any case), pre-releases included: what's typed is shown. Each row is labelled with its member in the
    /// family with the most parts (1.31.2-alpine under alpine; All: its canonical one, or with a filter the member that
    /// matches), and Follows lists that family's other members naming it now: versions longest first, then channels.
    /// </summary>
    public static IReadOnlyList<TagChoice> For(TagList list, string? family, bool preReleases, string? filter = null)
    {
        var tags = TagRanking.TagsOf(list);
        var text = filter?.Trim();
        if (!string.IsNullOrEmpty(text))
            return list.Versions.Concat(list.Other)
                .Where(r => Names(r).Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .Select(r => Choice(list, r, tags, Matching(r, tags, text)))
                .ToList();
        return list.Versions
            .Where(r => preReleases || !r.PreRelease)
            .Where(r => family is null || r.Families.Contains(family))
            .Select(r => Choice(list, r, tags, family ?? Of(r.Tag, tags).Family))
            .ToList();
    }

    /// <summary>
    /// The chip a tag opens the picker on (1.31-alpine: alpine; latest: the plain one, ""): its own family when that's a
    /// chip, else the shortest chip its image is listed under (1.31.2-alpine3.24: alpine); null (All) when none is.
    /// A tag the list doesn't hold (an older one) goes by its name.
    /// </summary>
    public static string? FamilyOf(TagList list, string tag)
    {
        var name = tag.Trim();
        var tags = TagRanking.TagsOf(list);
        var family = tags.TryGetValue(name, out var known) ? known.Family : TagRanking.Parse(name, TagRanking.FamilyTokens(tags.Keys.Append(name))).Family;
        if (list.Families.Any(f => f.Name == family)) return family;
        var row = list.Versions.Concat(list.Other).FirstOrDefault(r => Names(r).Contains(name, StringComparer.Ordinal));
        return row is null ? null : list.Families.Where(f => row.Families.Contains(f.Name)).OrderBy(f => f.Name.Length).Select(f => f.Name).FirstOrDefault();
    }

    /// <summary>One row as <paramref name="family"/>'s chip shows it (null: as All does), whether or not the chip lists it.</summary>
    internal static TagChoice View(TagList list, TagRow row, string? family)
    {
        var tags = TagRanking.TagsOf(list);
        return Choice(list, row, tags, family ?? Of(row.Tag, tags).Family);
    }

    private static TagChoice Choice(TagList list, TagRow row, Dictionary<string, TagRanking.ParsedTag> tags, string family)
    {
        var (label, follows) = TagRanking.View(Names(row).Select(n => Of(n, tags)).ToList(), family, Of(row.Tag, tags));
        return new TagChoice(row, label.Name, follows.Select(f => f.Name).ToList(), ReferenceEquals(row, list.Recommended));
    }

    // With a filter, a row is shown as the family of what was typed: its canonical member's when that matches, else the
    // matching member's with the most parts.
    private static string Matching(TagRow row, Dictionary<string, TagRanking.ParsedTag> tags, string text)
    {
        if (row.Tag.Contains(text, StringComparison.OrdinalIgnoreCase)) return Of(row.Tag, tags).Family;
        return Names(row).Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(n => Of(n, tags))
            .OrderByDescending(t => t.Parts).ThenBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.Ordinal).First().Family;
    }

    private static IEnumerable<string> Names(TagRow row) => row.Also.Prepend(row.Tag);

    // Every member is in the list's map; a row made by hand elsewhere is read as it stands.
    private static TagRanking.ParsedTag Of(string name, Dictionary<string, TagRanking.ParsedTag> tags) =>
        tags.TryGetValue(name, out var tag) ? tag : TagRanking.Parse(name, TagRanking.FamilyTokens(tags.Keys));
}
