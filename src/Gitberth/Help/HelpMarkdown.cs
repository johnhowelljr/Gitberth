using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Gitberth.Help;

/// <summary>A heading of a page, with its GitHub-style id.</summary>
public sealed record HelpHeading(int Level, string Id, string Text);

/// <summary>A stretch of a page for search: a heading (none for the page's opening) and the text under it, until the next.</summary>
public sealed record HelpPassage(string? Anchor, string Heading, int Level, string Text);

/// <summary>A link or picture of a page as written, and where it leads.</summary>
public sealed record HelpPageLink(string Url, bool Image, HelpTarget Target);

/// <summary>A page of the docs, rendered once for Help.</summary>
public sealed class HelpDoc
{
    /// <summary>Its path under docs/ without .md: guides/networking; README is the index.</summary>
    public required string Id { get; init; }

    /// <summary>Its path under docs/: guides/networking.md.</summary>
    public required string Path { get; init; }

    /// <summary>Its first "# " heading.</summary>
    public required string Title { get; init; }

    /// <summary>The title's own id, as GitHub gives it: a link to it opens the page at its top.</summary>
    public required string TitleId { get; init; }

    /// <summary>The page as HTML, without its title (the page's head shows that).</summary>
    public required string Html { get; init; }

    /// <summary>Every heading under the title, in order.</summary>
    public required IReadOnlyList<HelpHeading> Headings { get; init; }

    /// <summary>Its text, heading by heading, for search.</summary>
    public required IReadOnlyList<HelpPassage> Passages { get; init; }

    /// <summary>Every link and picture, as written, with where it leads.</summary>
    public required IReadOnlyList<HelpPageLink> Links { get; init; }

    /// <summary>How many ```mermaid diagrams it has.</summary>
    public required int Diagrams { get; init; }

    public string Href => HelpDocs.PageHref(Id);

    /// <summary>"On this page": its h2 and h3 headings.</summary>
    public IEnumerable<HelpHeading> Contents => Headings.Where(h => h.Level is 2 or 3);

    /// <summary>The first lines of its opening, for a search result about the page itself.</summary>
    public string Lead => Passages.FirstOrDefault(p => p.Anchor is null)?.Text is { Length: > 0 } text ? text : Passages.FirstOrDefault()?.Text ?? "";

    /// <summary>A heading's id is on the page (the title's included).</summary>
    public bool HasAnchor(string id) => id == TitleId || Headings.Any(h => h.Id == id);
}

/// <summary>
/// The docs' Markdown rendered the way GitHub renders it, with Gitberth's looks: GitHub-flavoured tables, task lists,
/// autolinks and strikethrough; headings with GitHub's ids; GitHub's alerts (&gt; [!NOTE]) and the docs' "&gt; **Note**"
/// quotes as callouts; fenced code in Gitberth's code frame with a copy button; ```mermaid blocks as diagram containers
/// the page draws; links leading where they lead in Gitberth (<see cref="HelpLinks"/>); pictures loaded lazily, with
/// their size, opening larger on a click. Raw HTML isn't passed through: comments (notes for whoever edits the docs) are
/// dropped and anything else is shown as text.
/// </summary>
internal static class HelpMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAlertBlocks()
        .Build();

    // The callout kinds, as GitHub's alerts name them, and the words a "> **Note**" quote may start with.
    private static readonly Dictionary<string, string> Callouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["note"] = "Note", ["tip"] = "Tip", ["important"] = "Important", ["warning"] = "Warning", ["caution"] = "Caution",
    };

    private const string CalloutKey = "gb-callout";

    public static HelpDoc Render(string path, string markdown, HelpLinks links)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var id = HelpDocs.IdOf(path);
        var slugger = new Slugger();

        // The title: the first "# " heading. It takes its id first, as on GitHub, and the page's head shows it.
        var h1 = doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        var title = h1 is null ? id : Text(h1.Inline).Trim();
        var titleId = slugger.Slug(title);
        h1?.Parent?.Remove(h1);

        var headings = new List<HelpHeading>();
        foreach (var heading in doc.Descendants<HeadingBlock>())
        {
            var text = Text(heading.Inline).Trim();
            var slug = slugger.Slug(text);
            heading.GetAttributes().Id = slug;
            headings.Add(new HelpHeading(heading.Level, slug, text));
        }

        foreach (var quote in doc.Descendants<QuoteBlock>().ToList())
        {
            if (quote is AlertBlock alert)
                quote.SetData(CalloutKey, Callouts.GetValueOrDefault(alert.Kind.ToString()) ?? "Note");
            else if (LeadingLabel(quote) is { } kind)
                quote.SetData(CalloutKey, kind);
        }

        var found = new List<HelpPageLink>();
        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            var url = link.Url ?? "";
            var target = links.Resolve(path, url);
            found.Add(new HelpPageLink(url, link.IsImage, target));
            if (link.IsImage) Picture(link, target, links);
            else Link(link, target);
        }
        foreach (var auto in doc.Descendants<AutolinkInline>().ToList())
        {
            if (auto.IsEmail) continue;
            var target = links.Resolve(path, auto.Url);
            found.Add(new HelpPageLink(auto.Url, false, target));
            auto.GetAttributes().AddClass("gb-ext");
            auto.GetAttributes().AddProperty("target", "_blank");
            auto.GetAttributes().AddProperty("rel", "noopener");
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new CodeRenderer());
        renderer.ObjectRenderers.Replace<HtmlBlockRenderer>(new RawBlockRenderer());
        renderer.ObjectRenderers.Replace<HtmlInlineRenderer>(new RawInlineRenderer());
        renderer.ObjectRenderers.Replace<AlertBlockRenderer>(new AlertRenderer());
        renderer.ObjectRenderers.Replace<QuoteBlockRenderer>(new QuoteRenderer());
        renderer.Render(doc);
        writer.Flush();

        return new HelpDoc
        {
            Id = id, Path = path, Title = title, TitleId = titleId, Html = writer.ToString(), Headings = headings,
            Passages = Passages(doc), Links = found,
            Diagrams = doc.Descendants<FencedCodeBlock>().Count(IsMermaid),
        };
    }

    private static bool IsMermaid(FencedCodeBlock block) => Language(block).Equals("mermaid", StringComparison.OrdinalIgnoreCase);

    private static string Language(CodeBlock block) => (block as FencedCodeBlock)?.Info?.Trim().Split(' ', 2)[0] ?? "";

    // ---- Links and pictures ----

    private static void Link(LinkInline link, HelpTarget target)
    {
        link.Url = target.Href;
        if (target.Kind is HelpTargetKind.External or HelpTargetKind.Repo)
        {
            var attributes = link.GetAttributes();
            attributes.AddClass("gb-ext");
            attributes.AddProperty("target", "_blank");
            attributes.AddProperty("rel", "noopener");
        }
        else if (target.Kind == HelpTargetKind.Media)
        {
            link.GetAttributes().AddProperty("target", "_blank");
        }
    }

    // A picture keeps its room while it loads, loads when it's near, and opens larger in the page's viewer on a click
    // (its link opens it in a tab of its own where there's no script).
    private static void Picture(LinkInline image, HelpTarget target, HelpLinks links)
    {
        image.Url = target.Href;
        var attributes = image.GetAttributes();
        attributes.AddProperty("loading", "lazy");
        attributes.AddProperty("decoding", "async");
        if (target is { Kind: HelpTargetKind.Media, Path: { } mediaPath } && links.Media(mediaPath) is { Width: { } w, Height: { } h })
        {
            attributes.AddProperty("width", w.ToString(CultureInfo.InvariantCulture));
            attributes.AddProperty("height", h.ToString(CultureInfo.InvariantCulture));
        }
        if (image.Parent is LinkInline) return;
        var zoom = new LinkInline(target.Href, "");
        var z = zoom.GetAttributes();
        z.AddClass("gb-doc-zoom");
        z.AddProperty("target", "_blank");
        z.AddProperty("aria-label", "Open the picture larger: " + AltText(image));
        image.ReplaceBy(zoom, false);
        zoom.AppendChild(image);
    }

    private static string AltText(LinkInline image)
    {
        var text = new StringBuilder();
        for (var child = image.FirstChild; child is not null; child = child.NextSibling) Append(text, child);
        return text.ToString().Trim();
    }

    // ---- "> **Note**" quotes ----

    // A quote whose first words are a bold Note, Tip, Important, Warning or Caution (alone, or with a colon) is that
    // callout; the word becomes its title.
    private static string? LeadingLabel(QuoteBlock quote)
    {
        if (quote.Count == 0 || quote[0] is not ParagraphBlock { Inline: { FirstChild: EmphasisInline { DelimiterCount: 2 } strong } inline } paragraph)
            return null;
        if (!Callouts.TryGetValue(Text(strong).Trim().TrimEnd(':').Trim(), out var kind)) return null;
        var next = strong.NextSibling;
        strong.Remove();
        if (next is LineBreakInline) { var after = next.NextSibling; next.Remove(); next = after; }
        if (next is LiteralInline literal) literal.Content = new Markdig.Helpers.StringSlice(literal.Content.ToString().TrimStart());
        if (inline.FirstChild is null) quote.Remove(paragraph);
        return kind;
    }

    private static void WriteCallout(HtmlRenderer renderer, ContainerBlock block, string kind)
    {
        renderer.EnsureLine();
        renderer.Write("<div class=\"gb-callout gb-callout--").Write(kind.ToLowerInvariant()).Write("\" role=\"note\">");
        renderer.Write("<p class=\"gb-callout-title\">").Write(CalloutIcon(kind)).WriteEscape(kind).WriteLine("</p>");
        var implicitParagraph = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = false;
        renderer.WriteChildren(block);
        renderer.ImplicitParagraph = implicitParagraph;
        renderer.WriteLine("</div>");
    }

    // Material's outlined icons, drawn inline like SvgIcon.
    private static string CalloutIcon(string kind) => "<svg class=\"gb-i gb-i--sm\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\">" + kind switch
    {
        "Warning" or "Caution" => "<path d=\"M12 5.99 19.53 19H4.47L12 5.99M12 2 1 21h22L12 2zm1 14h-2v2h2v-2zm0-6h-2v4h2v-4z\"/>",
        "Tip" => "<path d=\"M9 21c0 .55.45 1 1 1h4c.55 0 1-.45 1-1v-1H9v1zm3-19C8.14 2 5 5.14 5 9c0 2.38 1.19 4.47 3 5.74V17c0 .55.45 1 1 1h6c.55 0 1-.45 1-1v-2.26c1.81-1.27 3-3.36 3-5.74 0-3.86-3.14-7-7-7zm2.85 11.1-.85.6V16h-4v-2.3l-.85-.6C7.8 12.16 7 10.63 7 9c0-2.76 2.24-5 5-5s5 2.24 5 5c0 1.63-.8 3.16-2.15 4.1z\"/>",
        "Important" => "<path d=\"M20 2H4c-1.1 0-1.99.9-1.99 2L2 22l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H5.17l-.59.59-.58.58V4h16v12zM11 12h2v2h-2zm0-6h2v4h-2z\"/>",
        _ => "<path d=\"M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z\"/>",
    } + "</svg>";

    private sealed class AlertRenderer : HtmlObjectRenderer<AlertBlock>
    {
        protected override void Write(HtmlRenderer renderer, AlertBlock obj) =>
            WriteCallout(renderer, obj, obj.GetData(CalloutKey) as string ?? "Note");
    }

    private sealed class QuoteRenderer : HtmlObjectRenderer<QuoteBlock>
    {
        private readonly QuoteBlockRenderer _plain = new();

        protected override void Write(HtmlRenderer renderer, QuoteBlock obj)
        {
            if (obj.GetData(CalloutKey) is string kind) WriteCallout(renderer, obj, kind);
            else ((IMarkdownObjectRenderer)_plain).Write(renderer, obj);
        }
    }

    // ---- Code ----

    // ```mermaid: a diagram container the page draws (its source stays readable until then, and if it can't be drawn).
    // Any other code: Gitberth's code frame, with its language and a copy button.
    private sealed class CodeRenderer : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            var language = Language(obj);
            renderer.EnsureLine();
            if (language.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
            {
                renderer.Write("<div class=\"gb-mermaid\" data-mermaid><pre class=\"gb-mermaid-src\">");
                renderer.WriteLeafRawLines(obj, true, true);
                renderer.WriteLine("</pre></div>");
                return;
            }
            renderer.Write("<div class=\"gb-code gb-doc-code\"><div class=\"gb-code-bar\"><span class=\"gb-code-title\">");
            renderer.WriteEscape(language);
            renderer.Write("</span><button type=\"button\" class=\"gb-copybtn\" data-doc-copy aria-label=\"Copy the code\" data-tip-text=\"Copy\">");
            renderer.Write("<svg class=\"gb-i\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z\"/></svg>");
            renderer.Write("</button></div><pre><code");
            if (language.Length > 0) renderer.Write(" class=\"language-").WriteEscape(language).Write("\"");
            renderer.Write(">");
            renderer.WriteLeafRawLines(obj, true, true);
            renderer.WriteLine("</code></pre></div>");
        }
    }

    // ---- Raw HTML ----

    private sealed class RawBlockRenderer : HtmlObjectRenderer<HtmlBlock>
    {
        protected override void Write(HtmlRenderer renderer, HtmlBlock obj)
        {
            if (obj.Type == HtmlBlockType.Comment) return;
            renderer.EnsureLine();
            renderer.Write("<p>");
            renderer.WriteLeafRawLines(obj, true, true);
            renderer.WriteLine("</p>");
        }
    }

    private sealed class RawInlineRenderer : HtmlObjectRenderer<HtmlInline>
    {
        protected override void Write(HtmlRenderer renderer, HtmlInline obj)
        {
            if (obj.Tag is { } tag && !tag.StartsWith("<!--", StringComparison.Ordinal)) renderer.WriteEscape(tag);
        }
    }

    // ---- Text ----

    /// <summary>The text of inlines as a reader sees it: code included, pictures' and HTML left out (GitHub's heading text).</summary>
    internal static string Text(Inline? inline)
    {
        var text = new StringBuilder();
        Append(text, inline);
        return text.ToString();
    }

    private static void Append(StringBuilder text, Inline? inline)
    {
        switch (inline)
        {
            case LiteralInline literal: text.Append(literal.Content.ToString()); break;
            case CodeInline code: text.Append(code.Content); break;
            case LineBreakInline: text.Append(' '); break;
            case HtmlEntityInline entity: text.Append(entity.Transcoded.ToString()); break;
            case AutolinkInline auto: text.Append(auto.Url); break;
            case LinkInline { IsImage: true }: break;
            case HtmlInline: break;
            case ContainerInline container:
                for (var child = container.FirstChild; child is not null; child = child.NextSibling) Append(text, child);
                break;
        }
    }

    // The page's text, heading by heading: each heading starts a passage that runs to the next one.
    private static List<HelpPassage> Passages(MarkdownDocument doc)
    {
        var passages = new List<HelpPassage>();
        string? anchor = null;
        var heading = "";
        var level = 1;
        var text = new StringBuilder();
        void Flush()
        {
            var body = string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (anchor is not null || body.Length > 0) passages.Add(new HelpPassage(anchor, heading, level, body));
            text.Clear();
        }
        void Walk(Block block)
        {
            switch (block)
            {
                case HeadingBlock h:
                    Flush();
                    (anchor, heading, level) = (h.GetAttributes().Id, Text(h.Inline).Trim(), h.Level);
                    break;
                case FencedCodeBlock fenced when IsMermaid(fenced):
                    break;
                case CodeBlock code:
                    text.Append(' ').Append(code.Lines.ToString()).Append(' ');
                    break;
                case HtmlBlock:
                    break;
                case LeafBlock leaf:
                    text.Append(' ').Append(Text(leaf.Inline)).Append(' ');
                    break;
                case ContainerBlock container:
                    foreach (var child in container) Walk(child);
                    break;
            }
        }
        foreach (var block in doc) Walk(block);
        Flush();
        return passages;
    }

    /// <summary>
    /// GitHub's heading ids: lower case; letters, digits, marks, - and _ kept, each space a -, anything else dropped; a
    /// repeat gets -1, -2 and so on (as github-slugger does, the title's own id included).
    /// </summary>
    internal sealed class Slugger
    {
        private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

        public string Slug(string text)
        {
            var slug = Plain(text);
            var original = slug;
            while (_seen.ContainsKey(slug))
            {
                _seen[original]++;
                slug = original + "-" + _seen[original].ToString(CultureInfo.InvariantCulture);
            }
            _seen[slug] = 0;
            return slug;
        }

        internal static string Plain(string text)
        {
            var slug = new StringBuilder(text.Length);
            foreach (var ch in text.ToLowerInvariant())
            {
                if (ch == ' ') slug.Append('-');
                else if (ch is '-' or '_' || char.IsLetterOrDigit(ch)) slug.Append(ch);
                else if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                         or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation)
                    slug.Append(ch);
            }
            return slug.ToString();
        }
    }
}
