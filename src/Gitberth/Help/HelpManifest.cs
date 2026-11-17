using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Gitberth.Help;

/// <summary>A section of the Help's table of contents: its title and its pages (ids), in order.</summary>
public sealed record HelpManifestSection(string Title, IReadOnlyList<string> Pages);

/// <summary>Where Help is, for "Help for this page": a page's id and, optionally, a heading's id on it.</summary>
public sealed record HelpLink(string Page, string? Anchor = null)
{
    /// <summary>The link, relative to the base address like Gitberth's own: help/guides/networking#own-lan-addresses.</summary>
    public string Href => HelpDocs.PageHref(Page) + (Anchor is null ? "" : "#" + Anchor);
}

/// <summary>
/// One rule of "Help for this page": a route of Gitberth's (/apps/*/settings) and the help page it opens. In a route, *
/// stands for one segment and ** for any number of them, none included; segments compare without regard to case.
/// </summary>
public sealed record HelpRouteRule(string Route, HelpLink Link)
{
    private readonly string[] _parts = Split(Route);

    public bool Matches(string path) => Match(_parts, 0, Split(path), 0);

    /// <summary>A path's segments: no query, no fragment, no empty segments ("/apps/x/" is apps, x).</summary>
    internal static string[] Split(string path)
    {
        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0) path = path[..end];
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Match(string[] route, int r, string[] path, int p)
    {
        if (r == route.Length) return p == path.Length;
        if (route[r] == "**")
        {
            for (var skip = p; skip <= path.Length; skip++)
                if (Match(route, r + 1, path, skip)) return true;
            return false;
        }
        if (p == path.Length) return false;
        if (route[r] != "*" && !string.Equals(route[r], Uri.UnescapeDataString(path[p]), StringComparison.OrdinalIgnoreCase)) return false;
        return Match(route, r + 1, path, p + 1);
    }
}

/// <summary>
/// docs/help.yaml: the Help's table of contents (sections of pages, in order) and "Help for this page" (routes to pages).
/// H2's docs own its content; this reads it and says what's wrong with it (<see cref="Problems"/>), never throwing.
/// </summary>
public sealed class HelpManifest
{
    private static readonly HashSet<string> TopKeys = ["sections", "routes"];

    public IReadOnlyList<HelpManifestSection> Sections { get; }

    public IReadOnlyList<HelpRouteRule> Routes { get; }

    /// <summary>What's wrong with the file itself (its YAML, a key it doesn't know, a route without a page).</summary>
    public IReadOnlyList<string> Problems { get; }

    private HelpManifest(IReadOnlyList<HelpManifestSection> sections, IReadOnlyList<HelpRouteRule> routes, IReadOnlyList<string> problems) =>
        (Sections, Routes, Problems) = (sections, routes, problems);

    /// <summary>The first route that matches the address's path, or null.</summary>
    public HelpLink? ForPath(string path) => Routes.FirstOrDefault(r => r.Matches(path))?.Link;

    /// <summary>A page id with an optional #anchor: guides/networking#own-lan-addresses.</summary>
    public static HelpLink ParseLink(string text)
    {
        var hash = text.IndexOf('#');
        return hash < 0 ? new HelpLink(text.Trim()) : new HelpLink(text[..hash].Trim(), text[(hash + 1)..].Trim() is { Length: > 0 } a ? a : null);
    }

    public static HelpManifest Parse(string yaml)
    {
        var problems = new List<string>();
        var sections = new List<HelpManifestSection>();
        var routes = new List<HelpRouteRule>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
                return new HelpManifest([], [], ["help.yaml isn't one YAML mapping."]);
            root = map;
        }
        catch (Exception e) when (e is YamlException or ArgumentException)
        {
            return new HelpManifest([], [], [$"help.yaml isn't valid YAML: {e.Message}"]);
        }

        foreach (var key in root.Children.Keys.Select(k => (k as YamlScalarNode)?.Value ?? "").Where(k => !TopKeys.Contains(k)))
            problems.Add($"help.yaml has a key Help doesn't know: {key}.");

        foreach (var node in Sequence(root, "sections"))
        {
            if (node is not YamlMappingNode m)
            {
                problems.Add("Each of help.yaml's sections is a mapping (title, pages).");
                continue;
            }
            Unknown(m, ["title", "pages"], "a section", problems);
            var title = Scalar(m, "title")?.Trim() ?? "";
            if (title.Length == 0) problems.Add("A section of help.yaml has no title.");
            var pages = Sequence(m, "pages").Select(p => (p as YamlScalarNode)?.Value?.Trim() ?? "").ToList();
            if (pages.Count == 0 || pages.Any(p => p.Length == 0)) problems.Add($"The section {title} needs its pages, each a page's id.");
            sections.Add(new HelpManifestSection(title, pages.Where(p => p.Length > 0).ToList()));
        }

        foreach (var node in Sequence(root, "routes"))
        {
            if (node is not YamlMappingNode m)
            {
                problems.Add("Each of help.yaml's routes is a mapping (route, page).");
                continue;
            }
            Unknown(m, ["route", "page"], "a route", problems);
            var route = Scalar(m, "route")?.Trim() ?? "";
            var page = Scalar(m, "page")?.Trim() ?? "";
            if (!route.StartsWith('/')) problems.Add($"The route {route} must start with /.");
            if (page.Length == 0) problems.Add($"The route {route} has no page.");
            else routes.Add(new HelpRouteRule(route, ParseLink(page)));
        }
        if (sections.Count == 0) problems.Add("help.yaml has no sections.");
        return new HelpManifest(sections, routes, problems);
    }

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode seq ? seq.Children : [];

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode s ? s.Value : null;

    private static void Unknown(YamlMappingNode map, string[] known, string what, List<string> problems)
    {
        foreach (var key in map.Children.Keys.Select(k => (k as YamlScalarNode)?.Value ?? "").Where(k => !known.Contains(k)))
            problems.Add($"In help.yaml, {what} has a key Help doesn't know: {key}.");
    }
}
