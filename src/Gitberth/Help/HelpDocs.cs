using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Help;

/// <summary>
/// Gitberth's built-in Help: the repo's docs folder, embedded in the assembly (Gitberth.Docs/&lt;path&gt;), read and
/// rendered once (the docs never change while Gitberth runs). /help shows docs/README.md, /help/&lt;page&gt; a page;
/// docs/help.yaml gives the sections' order and "Help for this page". The pictures come from <see cref="Media"/>, and
/// <see cref="Search"/> finds pages, headings and text.
/// </summary>
public sealed class HelpDocs
{
    /// <summary>Where the docs are in Gitberth's assembly: Gitberth.Docs/guides/networking.md.</summary>
    public const string ResourcePrefix = "Gitberth.Docs/";

    /// <summary>The index's id: docs/README.md, shown at /help.</summary>
    public const string IndexId = "README";

    public const string ManifestPath = "help.yaml";

    private static readonly Lazy<HelpDocs> BundledDocs = new(() => FromAssembly(typeof(HelpDocs).Assembly));

    private readonly Dictionary<string, HelpDoc> _byId;
    private readonly Dictionary<string, HelpMedia> _media;
    private readonly Dictionary<string, int> _order;
    private readonly Dictionary<string, string> _sectionOf;

    /// <summary>The docs Gitberth ships, read from its assembly once.</summary>
    public static HelpDocs Bundled => BundledDocs.Value;

    /// <param name="files">Each file's path under docs/ (guides/networking.md, images/apps-list.webp, help.yaml) and bytes.</param>
    public HelpDocs(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        var list = files.Select(f => (Path: f.Path.Replace('\\', '/'), f.Bytes)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        Version = VersionOf(list);
        _media = list.Select(f => HelpMedia.From(f.Path, f.Bytes)).OfType<HelpMedia>().ToDictionary(m => m.Path, StringComparer.Ordinal);
        var markdown = list.Where(f => f.Path.EndsWith(".md", StringComparison.Ordinal)).ToList();
        Links = new HelpLinks(markdown.Select(f => IdOf(f.Path)).ToHashSet(StringComparer.Ordinal), _media, Version);

        var problems = new List<string>();
        var manifest = list.Where(f => f.Path == ManifestPath).Select(f => Utf8(f.Bytes)).FirstOrDefault();
        if (manifest is null) problems.Add("docs/help.yaml is missing.");
        Manifest = HelpManifest.Parse(manifest ?? "");
        problems.AddRange(Manifest.Problems);

        var docs = markdown.Select(f => HelpMarkdown.Render(f.Path, Utf8(f.Bytes), Links)).ToList();
        _byId = docs.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var doc in docs.Where(d => d.Title == d.Id))
            problems.Add($"docs/{doc.Path} has no title: its first line should be a \"# \" heading.");
        Index = _byId.GetValueOrDefault(IndexId) ?? new HelpDoc
        {
            Id = IndexId, Path = IndexId + ".md", Title = "Help", TitleId = "help", Html = "", Headings = [], Passages = [], Links = [], Diagrams = 0,
        };
        if (!_byId.ContainsKey(IndexId)) problems.Add("docs/README.md (the index /help shows) is missing.");

        // The pages in the manifest's order; a page it doesn't list comes after them, so it still opens.
        var listed = new List<string>();
        _sectionOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in Manifest.Sections)
        {
            foreach (var page in section.Pages)
            {
                if (!_byId.ContainsKey(page) || page == IndexId) problems.Add($"help.yaml's section {section.Title} lists {page}, which isn't a page of the docs (docs/{page}.md).");
                else if (!_sectionOf.TryAdd(page, section.Title)) problems.Add($"help.yaml lists {page} twice (in {_sectionOf[page]} and {section.Title}): each page goes in one section.");
                else listed.Add(page);
            }
        }
        var unlisted = docs.Where(d => d.Id != IndexId && !_sectionOf.ContainsKey(d.Id)).Select(d => d.Id).ToList();
        foreach (var page in unlisted) problems.Add($"docs/{page}.md isn't in any section of help.yaml.");
        Pages = listed.Concat(unlisted).Select(id => _byId[id]).ToList();
        _order = Pages.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        foreach (var rule in Manifest.Routes)
        {
            if (Find(rule.Link.Page) is not { } target) problems.Add($"help.yaml's route {rule.Route} opens {rule.Link.Page}, which isn't a page of the docs.");
            else if (rule.Link.Anchor is { } anchor && !target.HasAnchor(anchor)) problems.Add($"help.yaml's route {rule.Route} opens {rule.Link.Page}#{anchor}, a heading that page doesn't have.");
        }
        Problems = problems;
    }

    /// <summary>The docs in an assembly: every resource under <see cref="ResourcePrefix"/>.</summary>
    public static HelpDocs FromAssembly(Assembly assembly) =>
        new(assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n =>
        {
            using var stream = assembly.GetManifestResourceStream(n)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (n[ResourcePrefix.Length..], memory.ToArray());
        }));

    /// <summary>Gitberth's version and the docs' own hash: what the pictures' addresses carry, so browsers keep them long.</summary>
    public string Version { get; }

    public HelpManifest Manifest { get; }

    public HelpLinks Links { get; }

    /// <summary>What's wrong with the docs or help.yaml: a page in no section, a route to a page that isn't there.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>docs/README.md: what /help shows under its search.</summary>
    public HelpDoc Index { get; }

    /// <summary>Every page but the index, in the sections' order.</summary>
    public IReadOnlyList<HelpDoc> Pages { get; }

    public static string IdOf(string path) => path.EndsWith(".md", StringComparison.Ordinal) ? path[..^".md".Length] : path;

    /// <summary>A page's address in Gitberth, relative to its base like every link of Gitberth's: help/guides/networking.</summary>
    public static string PageHref(string id) => id == IndexId ? "help" : "help/" + string.Join('/', id.Split('/').Select(Uri.EscapeDataString));

    public HelpDoc? Find(string? id) => id is not null && _byId.TryGetValue(id.Trim('/'), out var doc) ? doc : null;

    /// <summary>The title of the section a page is in ("Run your apps"); null for one in none.</summary>
    public string? SectionOf(HelpDoc doc) => _sectionOf.GetValueOrDefault(doc.Id);

    public HelpDoc? Previous(HelpDoc doc) => _order.TryGetValue(doc.Id, out var i) && i > 0 ? Pages[i - 1] : null;

    public HelpDoc? Next(HelpDoc doc) => _order.TryGetValue(doc.Id, out var i) && i < Pages.Count - 1 ? Pages[i + 1] : null;

    /// <summary>"Help for this page": the help the first matching route gives for an address's path (/apps/web/settings).</summary>
    public HelpLink? ForPath(string path) => Manifest.ForPath(path) is { } link && Find(link.Page) is not null ? link : null;

    /// <summary>A picture of the docs by its path under docs/ (images/apps-list.webp).</summary>
    public HelpMedia? Media(string path) => _media.GetValueOrDefault(path);

    // ---- Search ----

    /// <summary>
    /// Pages, headings and text that have every word of the query (any case): pages whose title has them first, then
    /// headings that have them, then passages (a heading and its text) that have them, then pages that have them across
    /// passages. Each in the sections' order. A result links to the heading where the words are, with a short snippet. A
    /// page's "Related" list, which only names other pages, is left out.
    /// </summary>
    public IReadOnlyList<HelpHit> Search(string? query, int max = 50)
    {
        var words = Words(query);
        if (words.Count == 0) return [];
        var hits = new List<HelpHit>();
        var taken = new HashSet<(string, string?)>();
        void Add(HelpDoc doc, HelpPassage? passage, HelpHitKind kind, string text)
        {
            if (taken.Add((doc.Id, passage?.Anchor))) hits.Add(new HelpHit(doc, passage, kind, Snippet.Of(text, words)));
        }
        static IEnumerable<HelpPassage> Read(HelpDoc doc) => doc.Passages.Where(p => !string.Equals(p.Heading, "Related", StringComparison.OrdinalIgnoreCase));

        foreach (var doc in Pages.Where(d => HasAll(d.Title, words)))
            Add(doc, null, HelpHitKind.Title, doc.Lead);
        foreach (var doc in Pages)
            foreach (var passage in Read(doc).Where(p => p.Anchor is not null && HasAll(p.Heading, words)))
                Add(doc, passage, HelpHitKind.Heading, passage.Text);
        foreach (var doc in Pages)
            foreach (var passage in Read(doc).Where(p => HasAll(p.Heading + " " + p.Text, words)))
                Add(doc, passage.Anchor is null ? null : passage, HelpHitKind.Text, passage.Text);
        foreach (var doc in Pages.Where(d => !taken.Any(t => t.Item1 == d.Id)))
        {
            if (!HasAll(string.Join(' ', Read(doc).Select(p => p.Heading + " " + p.Text)), words)) continue;
            var first = Read(doc).First(p => (p.Heading + " " + p.Text).Contains(words[0], StringComparison.OrdinalIgnoreCase));
            Add(doc, first.Anchor is null ? null : first, HelpHitKind.Text, first.Text);
        }
        return hits.Take(max).ToList();
    }

    /// <summary>The query's words: split at spaces, punctuation around them dropped, each once.</summary>
    internal static List<string> Words(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '“', '”', '‘', '’'))
            .Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool HasAll(string text, List<string> words) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ---- Reading the files ----

    private static string Utf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static string VersionOf(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\n"));
            hash.AppendData(bytes);
        }
        return AppVersion.Number + "-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }
}

public enum HelpHitKind { Title, Heading, Text }

/// <summary>A search result: a page, or a heading on it (Passage), with a snippet whose words are marked.</summary>
public sealed record HelpHit(HelpDoc Doc, HelpPassage? Passage, HelpHitKind Kind, Snippet Snippet)
{
    public string Href => Passage?.Anchor is { } anchor ? Doc.Href + "#" + anchor : Doc.Href;

    /// <summary>What the result is called: the heading, or the page's title.</summary>
    public string Title => Passage is { Anchor: not null } p ? p.Heading : Doc.Title;
}

/// <summary>A short stretch of text around the first word found, split into the parts to show plain and the words to mark.</summary>
public sealed record Snippet(IReadOnlyList<(string Text, bool Marked)> Parts)
{
    private const int Length = 170;

    public override string ToString() => string.Concat(Parts.Select(p => p.Text));

    public static Snippet Of(string text, IReadOnlyList<string> words)
    {
        var first = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = first < 50 ? 0 : text.LastIndexOf(' ', first - 40) + 1;
        var end = Math.Min(text.Length, start + Length);
        if (end < text.Length && text.LastIndexOf(' ', end) is var space && space > start + Length / 2) end = space;
        var window = text[start..end].Trim();
        var shown = (start > 0 ? "…" : "") + window + (end < text.Length ? "…" : "");

        var marks = new bool[shown.Length];
        foreach (var word in words)
            for (var at = shown.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = shown.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
                Array.Fill(marks, true, at, word.Length);
        var parts = new List<(string, bool)>();
        for (var i = 0; i < shown.Length;)
        {
            var j = i;
            while (j < shown.Length && marks[j] == marks[i]) j++;
            parts.Add((shown[i..j], marks[i]));
            i = j;
        }
        return new Snippet(parts);
    }
}
