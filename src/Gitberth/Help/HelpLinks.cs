using System.Text.RegularExpressions;

namespace Gitberth.Help;

/// <summary>What a link in the docs leads to, once it's read as GitHub reads it.</summary>
public enum HelpTargetKind
{
    /// <summary>A page of the docs (or a heading on it): opened in Help.</summary>
    Page,
    /// <summary>A picture of the docs: served by Help's media endpoint.</summary>
    Media,
    /// <summary>A file of the repo outside the docs (README.md, install.sh): its page on GitHub.</summary>
    Repo,
    /// <summary>Another site: opened in a new tab.</summary>
    External,
    /// <summary>A path in the docs that isn't there: a broken link (the unit tests name each one).</summary>
    Missing,
}

/// <param name="Href">The address the rendered page uses.</param>
/// <param name="Path">Page: its id; Media: its path under docs/; Repo and Missing: the path in the repo.</param>
public sealed record HelpTarget(HelpTargetKind Kind, string Href, string? Path = null, string? Anchor = null);

/// <summary>
/// Reads the docs' links the way GitHub does (relative to the page's folder in the repo) and says where each leads in
/// Gitberth: a page of the docs is opened in Help (help/&lt;page&gt;#anchor), a picture comes from the media endpoint, a
/// file of the repo outside docs/ is its page on GitHub, and another site opens in a new tab.
/// </summary>
public sealed partial class HelpLinks(IReadOnlySet<string> pages, IReadOnlyDictionary<string, HelpMedia> media, string version)
{
    /// <summary>The repo's files on GitHub: the project's own public repository.</summary>
    public const string RepoBlob = "https://github.com/johnhowelljr/Gitberth/blob/main/";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex Scheme();

    /// <summary>The ids of the docs' pages (guides/networking; README for the index).</summary>
    public IReadOnlySet<string> Pages => pages;

    public HelpMedia? Media(string path) => media.GetValueOrDefault(path);

    /// <summary>A picture's address: under help-media/, with the version so it can be kept for a long time.</summary>
    public string MediaUrl(string path) => "help-media/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + "?v=" + Uri.EscapeDataString(version);

    /// <summary>Where a link of the page at fromPath (its path under docs/: guides/networking.md) leads.</summary>
    public HelpTarget Resolve(string fromPath, string url)
    {
        url = url.Trim();
        var fromId = HelpDocs.IdOf(fromPath);
        if (url.Length == 0) return new HelpTarget(HelpTargetKind.Missing, url, fromPath);
        if (url.StartsWith('#'))
            return new HelpTarget(HelpTargetKind.Page, HelpDocs.PageHref(fromId) + url, fromId, Anchor(url[1..]));
        if (url.StartsWith("//", StringComparison.Ordinal)) return new HelpTarget(HelpTargetKind.External, "https:" + url);
        if (Scheme().IsMatch(url)) return new HelpTarget(HelpTargetKind.External, url);

        var hash = url.IndexOf('#');
        var fragment = hash < 0 ? null : url[(hash + 1)..];
        var pathPart = hash < 0 ? url : url[..hash];
        var query = pathPart.IndexOf('?');
        if (query >= 0) pathPart = pathPart[..query];
        string decoded;
        try { decoded = Uri.UnescapeDataString(pathPart); }
        catch (UriFormatException) { decoded = pathPart; }

        // In the repo's terms: a path that starts with / is from the repo's root, any other from the page's folder.
        var folder = fromPath.Contains('/') ? fromPath[..(fromPath.LastIndexOf('/') + 1)] : "";
        var repoPath = Normalize(decoded.StartsWith('/') ? decoded : "docs/" + folder + decoded);
        if (repoPath is null) return new HelpTarget(HelpTargetKind.Missing, url, decoded);

        if (repoPath.StartsWith("docs/", StringComparison.Ordinal))
        {
            var docsPath = repoPath["docs/".Length..];
            if (docsPath.EndsWith(".md", StringComparison.Ordinal))
            {
                var id = HelpDocs.IdOf(docsPath);
                return pages.Contains(id)
                    ? new HelpTarget(HelpTargetKind.Page, HelpDocs.PageHref(id) + (fragment is { Length: > 0 } ? "#" + fragment : ""), id, Anchor(fragment))
                    : new HelpTarget(HelpTargetKind.Missing, url, repoPath);
            }
            if (media.ContainsKey(docsPath)) return new HelpTarget(HelpTargetKind.Media, MediaUrl(docsPath), docsPath);
            if (HelpMedia.TypeOf(docsPath) is not null) return new HelpTarget(HelpTargetKind.Missing, url, repoPath);
        }
        // Outside docs/ (or a file of docs/ that's neither a page nor a picture): the file on GitHub.
        return new HelpTarget(HelpTargetKind.Repo, RepoBlob + string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString))
            + (fragment is { Length: > 0 } ? "#" + fragment : ""), repoPath, Anchor(fragment));
    }

    private static string? Anchor(string? fragment) => fragment is { Length: > 0 } ? Uri.UnescapeDataString(fragment) : null;

    /// <summary>a/b/../c → a/c; null when it climbs above the repo's root or names a folder.</summary>
    internal static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 || path.EndsWith('/') ? null : string.Join('/', parts);
    }
}
