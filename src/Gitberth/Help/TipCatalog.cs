using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Gitberth.Help;

/// <summary>
/// One tip: what a label of the UI is, when to change it and what happens. <see cref="Html"/> is its text as safe HTML
/// (<see cref="TipText"/>); <see cref="More"/> is the help page, and heading, with more to read.
/// </summary>
public sealed record TipEntry(string Key, string Title, string Text, string Html, string? Default, string? Example, HelpLink? More);

/// <summary>
/// The tips' catalog: Help/Tips/&lt;area&gt;.yaml, one file per area of the UI, embedded in the assembly. Each key is
/// &lt;area&gt;.&lt;thing&gt;[.&lt;detail&gt;] in lower case with -, defined once. An entry has a title (at most 60
/// characters) and a text (1 to 4 sentences, at most 450 characters as read), and may have a default, an example and
/// more (a help page, optionally #heading). <see cref="Check"/> says what's wrong; Development and the unit tests stop on
/// any of it, a release logs it and shows what it can.
/// </summary>
public sealed partial class TipCatalog
{
    /// <summary>Where the catalog's files are in Gitberth's assembly: Gitberth.Help.Tips.&lt;area&gt;.yaml.</summary>
    public const string ResourcePrefix = "Gitberth.Help.Tips.";

    public const int TitleMax = 60;
    public const int TextMax = 450;
    public const int SentenceMax = 4;
    public const int FactMax = 120;

    private static readonly string[] Fields = ["title", "text", "default", "example", "more"];

    private static readonly Lazy<TipCatalog> BundledCatalog = new(() => FromAssembly(typeof(TipCatalog).Assembly));

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$")]
    private static partial Regex KeyShape();

    // The end of a sentence: . ! or ? before a space and a capital (or the end), code spans left out first.
    [GeneratedRegex(@"[.!?](?=\s+[A-Z(""“]|\s*$)")]
    private static partial Regex SentenceEnd();

    private readonly Dictionary<string, TipEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];

    /// <summary>The catalog Gitberth ships, read from its assembly once.</summary>
    public static TipCatalog Bundled => BundledCatalog.Value;

    /// <param name="files">Each file's name (nav.yaml) and bytes.</param>
    public TipCatalog(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        var list = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            foreach (var (name, bytes) in list)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
                hash.AppendData(bytes);
            }
            Version = AppVersion.Number + "-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
        }
        foreach (var (name, bytes) in list.Where(f => f.Name.EndsWith(".yaml", StringComparison.Ordinal)))
            Load(name[..^".yaml".Length], Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    }

    /// <summary>The catalog in an assembly: every resource under <see cref="ResourcePrefix"/>.</summary>
    public static TipCatalog FromAssembly(Assembly assembly) =>
        new(assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n =>
        {
            using var stream = assembly.GetManifestResourceStream(n)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (n[ResourcePrefix.Length..], memory.ToArray());
        }));

    /// <summary>Gitberth's version and the catalog's own hash, which the catalog's address carries.</summary>
    public string Version { get; }

    /// <summary>Every tip that has a title and a text, by key.</summary>
    public IReadOnlyDictionary<string, TipEntry> Entries => _entries;

    public TipEntry? Find(string? key) => key is not null && _entries.TryGetValue(key, out var tip) ? tip : null;

    /// <summary>
    /// What's wrong with the catalog, each naming its file and key: YAML, a field it doesn't know, a missing title or text,
    /// a key of the wrong shape or area or defined twice, something too long, a link that isn't to Help or https, or
    /// more and help: links to a page or heading the docs don't have.
    /// </summary>
    public IReadOnlyList<string> Check(HelpDocs docs)
    {
        var problems = new List<string>(_problems);
        foreach (var tip in _entries.Values)
        {
            if (tip.More is { } more && Missing(docs, more) is { } gone) problems.Add($"{tip.Key}: more leads to {gone}.");
            foreach (var link in TipText.Links(tip.Text))
            {
                if (link.Help is { } help)
                {
                    if (Missing(docs, help) is { } why) problems.Add($"{tip.Key}: the link [{link.Words}] leads to {why}.");
                }
                else if (!link.External)
                {
                    problems.Add($"{tip.Key}: the link [{link.Words}]({link.Target}) is neither help:<page>#<heading> nor https://.");
                }
            }
        }
        return problems;
    }

    private static string? Missing(HelpDocs docs, HelpLink link) => docs.Find(link.Page) is not { } doc
        ? $"{link.Page}, which isn't a help page"
        : link.Anchor is { } anchor && !doc.HasAnchor(anchor) ? $"{link.Page}#{anchor}, a heading that page doesn't have" : null;

    private string? _json;

    /// <summary><see cref="Json"/>, made once: the catalog and the docs never change while Gitberth runs.</summary>
    public string CachedJson(HelpDocs docs) => _json ??= Json(docs);

    /// <summary>
    /// The catalog as tips.js reads it (one document, cached by version): each key's title, its text as safe HTML, its
    /// default and example (plain text), and where "Learn more" leads, with that page's title.
    /// </summary>
    public string Json(HelpDocs docs)
    {
        var tips = _entries.Values.OrderBy(t => t.Key, StringComparer.Ordinal).ToDictionary(t => t.Key, t => new
        {
            title = t.Title,
            html = t.Html,
            @default = t.Default,
            example = t.Example,
            more = t.More is { } m && docs.Find(m.Page) is not null ? m.Href : null,
            moreTitle = t.More is { } n ? docs.Find(n.Page)?.Title : null,
        });
        return JsonSerializer.Serialize(new { version = Version, tips }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    // ---- Reading a file ----

    private void Load(string area, string yaml)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0) return;
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
            {
                _problems.Add($"{area}.yaml: it isn't one YAML mapping of keys to tips.");
                return;
            }
            root = map;
        }
        catch (Exception e) when (e is YamlException or ArgumentException)
        {
            _problems.Add($"{area}.yaml: it isn't valid YAML (a key defined twice, say): {e.Message}");
            return;
        }

        foreach (var (keyNode, value) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            void Problem(string what) => _problems.Add($"{area}.yaml: {key}: {what}");
            if (!KeyShape().IsMatch(key)) Problem("a key is <area>.<thing>[.<detail>], in lower case with - (status.running, settings.gitlab.address).");
            else if (!key.StartsWith(area + ".", StringComparison.Ordinal)) Problem($"its key belongs in its own area's file: every key in {area}.yaml starts {area}.");
            if (value is not YamlMappingNode entry)
            {
                Problem("a tip is a mapping: title, text, and optionally default, example and more.");
                continue;
            }
            foreach (var field in entry.Children.Keys.Select(k => (k as YamlScalarNode)?.Value ?? "").Where(f => !Fields.Contains(f)))
                Problem($"it has a field tips don't have: {field} (they have {string.Join(", ", Fields)}).");
            string? Field(string name)
            {
                if (!entry.Children.TryGetValue(new YamlScalarNode(name), out var node)) return null;
                if (node is YamlScalarNode scalar) return scalar.Value?.Trim();
                Problem($"its {name} is text, not a list or a mapping.");
                return null;
            }

            var title = Field("title") ?? "";
            var text = Field("text") ?? "";
            var @default = Field("default");
            var example = Field("example");
            var more = Field("more");
            if (title.Length == 0) Problem("it needs a title.");
            else if (title.Length > TitleMax) Problem($"its title is {title.Length} characters; at most {TitleMax}.");
            if (title.Contains('\n')) Problem("its title is one line.");
            if (text.Length == 0) Problem("it needs a text: what it is, when to change it, what happens.");
            else
            {
                var visible = TipText.Visible(text);
                if (visible.Length > TextMax) Problem($"its text is {visible.Length} characters as read; at most {TextMax}.");
                var sentences = SentenceEnd().Matches(Regex.Replace(visible, "`[^`]*`", "x")).Count;
                if (sentences > SentenceMax) Problem($"its text has {sentences} sentences; at most {SentenceMax}.");
                if (string.Equals(visible.Trim().TrimEnd('.'), title, StringComparison.OrdinalIgnoreCase)) Problem("its text only repeats its title: a tip explains.");
            }
            foreach (var (name, fact) in new[] { ("default", @default), ("example", example) })
            {
                if (fact is null) continue;
                if (fact.Length == 0 || fact.Length > FactMax || fact.Contains('\n')) Problem($"its {name} is one line of at most {FactMax} characters.");
            }
            // more: a help page's id, optionally #heading (help: in front, as in a text's links, is allowed too).
            if (more is not null && more.StartsWith("help:", StringComparison.Ordinal)) more = more["help:".Length..];
            var moreLink = more is { Length: > 0 } && !more.Any(char.IsWhiteSpace) ? HelpManifest.ParseLink(more) : null;
            if (more is not null && moreLink is not { Page.Length: > 0 })
                Problem("its more is a help page's id, optionally with #heading (guides/networking#own-lan-addresses).");
            if (_entries.ContainsKey(key))
            {
                Problem("it's defined twice: a key is defined once, in its area's file.");
                continue;
            }
            if (title.Length == 0 || text.Length == 0) continue;
            _entries[key] = new TipEntry(key, title, text, TipText.ToHtml(text), @default, example, moreLink is { Page.Length: > 0 } ? moreLink : null);
        }
    }
}
