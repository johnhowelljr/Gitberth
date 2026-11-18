using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Gitberth.Components.Layout;
using Gitberth.Help;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gitberth.Tests;

/// <summary>
/// The built-in Help: docs/ embedded and rendered as GitHub renders it. The manifest (docs/help.yaml) lists every page
/// once and its routes open pages that are there; every page renders; every link, heading and picture between pages
/// resolves (checked on the rendered pages, as a reader would follow them); diagrams come out as containers the page
/// draws; headings get GitHub's ids; search puts titles first; routes match as help.yaml says; and the Help's own
/// addresses need signing in.
/// </summary>
public sealed partial class HelpTests
{
    private static readonly HelpDocs Docs = HelpDocs.Bundled;

    // ---- The docs as shipped ----

    [Fact]
    public void The_manifest_is_valid_and_lists_every_page_once()
    {
        Assert.Empty(Docs.Problems);
        var listed = Docs.Manifest.Sections.SelectMany(s => s.Pages).ToList();
        var pages = Directory.EnumerateFiles(DocsFolder, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(DocsFolder, f).Replace('\\', '/')[..^".md".Length])
            .Where(id => id != HelpDocs.IndexId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(pages, listed.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.All(Docs.Manifest.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.NotEmpty(Docs.Manifest.Routes);
        foreach (var route in Docs.Manifest.Routes)
        {
            var doc = Docs.Find(route.Link.Page);
            Assert.True(doc is not null, $"help.yaml's route {route.Route} opens {route.Link.Page}, which isn't a page");
            if (route.Link.Anchor is { } anchor) Assert.True(doc.HasAnchor(anchor), $"{route.Route}: {route.Link.Page} has no #{anchor}");
        }
        // The Help menu's three pages, and the Settings page's help the end-to-end scenario opens.
        foreach (var page in new[] { HelpMenu.GettingStarted, HelpMenu.Troubleshooting, HelpMenu.Faq })
            Assert.True(Docs.Find(page) is not null, $"the Help menu opens {page}, which isn't a page of the docs");
        Assert.NotNull(Docs.ForPath("settings"));
    }

    /// <summary>What the end-to-end scenario (tests/e2e/scenarios/test_34_help.py) counts on in the docs as shipped.</summary>
    [Fact]
    public void The_docs_have_what_the_end_to_end_scenario_opens()
    {
        var drawn = Docs.Find("guides/domains");
        Assert.True(drawn is { Diagrams: > 0 } && drawn.Links.Any(l => l is { Image: true, Target.Kind: HelpTargetKind.Media }),
            "test_34_help.py's DRAWN_PAGE (guides/domains) needs a ```mermaid diagram and a picture: point it at a page that has both");
        Assert.Equal("help/guides/troubleshooting", Docs.Search("troubleshooting")[0].Href);
        Assert.Equal("reference/gitberth-settings", Docs.ForPath("settings")!.Page);
    }

    [Fact]
    public void Every_page_renders_with_its_title_and_headings()
    {
        Assert.Equal("Gitberth documentation", Docs.Index.Title);
        Assert.NotEmpty(Docs.Index.Html);
        foreach (var doc in Docs.Pages.Append(Docs.Index))
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.Title));
            Assert.NotEqual(doc.Id, doc.Title);
            Assert.True(doc.Html.Length > 200, $"{doc.Id} rendered almost nothing");
            // The title is the page's head, not part of its body; comments (screenshots to take) never show.
            Assert.DoesNotContain("<h1", doc.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("<!--", doc.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", doc.Html, StringComparison.OrdinalIgnoreCase);
            var ids = IdAttribute().Matches(doc.Html).Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(doc.Headings, h => Assert.Contains($"id=\"{h.Id}\"", doc.Html, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_docs_use_no_raw_html_but_comments()
    {
        // Gitberth shows raw HTML as text (it never runs the docs' markup), while GitHub would render it: the two would differ.
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseAutoLinks().Build();
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(DocsFolder, "*.md", SearchOption.AllDirectories))
        {
            var doc = Markdown.Parse(File.ReadAllText(file), pipeline);
            var name = Path.GetRelativePath(DocsFolder, file).Replace('\\', '/');
            found.AddRange(doc.Descendants<HtmlBlock>().Where(b => b.Type != HtmlBlockType.Comment).Select(b => $"{name}:{b.Line + 1}: {b.Lines.ToString().Trim()}"));
            found.AddRange(doc.Descendants<HtmlInline>().Where(h => !h.Tag.StartsWith("<!--", StringComparison.Ordinal)).Select(h => $"{name}:{h.Line + 1}: {h.Tag}"));
        }
        Assert.True(found.Count == 0, "Raw HTML in the docs (write it as Markdown, or put a placeholder like <name> in backticks):\n" + string.Join("\n", found));
    }

    /// <summary>
    /// Every address a reader can follow from a rendered page: another page and its heading, a picture, or a file of the
    /// repo on GitHub (which must be in the repo, with the heading it names). A bare #anchor would lead to Gitberth's
    /// dashboard (the base address is /), so there's none.
    /// </summary>
    [Fact]
    public void Every_link_heading_and_picture_between_pages_resolves()
    {
        var broken = new List<string>();
        var checkedLinks = 0;
        foreach (var doc in Docs.Pages.Append(Docs.Index))
        {
            foreach (var link in doc.Links.Where(l => l.Target.Kind == HelpTargetKind.Missing))
                broken.Add($"{doc.Path}: {link.Url} leads nowhere ({link.Target.Path})");
            foreach (Match m in UrlAttribute().Matches(doc.Html))
            {
                var url = WebUtility.HtmlDecode(m.Groups[2].Value);
                checkedLinks++;
                if (Resolve(url) is { } why) broken.Add($"{doc.Path}: {m.Groups[1].Value}=\"{url}\": {why}");
            }
        }
        Assert.True(checkedLinks > 300, $"only {checkedLinks} links found: the check isn't reading the pages");
        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    // Null when the address leads somewhere real; else why not.
    private static string? Resolve(string url)
    {
        if (url.StartsWith('#')) return "a bare #anchor resolves against Gitberth's base, the dashboard";
        if (url.StartsWith("help-media/", StringComparison.Ordinal))
        {
            var path = Uri.UnescapeDataString(url["help-media/".Length..].Split('?')[0]);
            if (!url.Contains("?v=" + Uri.EscapeDataString(Docs.Version), StringComparison.Ordinal)) return "the picture's address doesn't carry the version";
            return Docs.Media(path) is null ? $"no picture docs/{path}" : null;
        }
        if (url == "help" || url.StartsWith("help/", StringComparison.Ordinal) || url.StartsWith("help#", StringComparison.Ordinal))
        {
            var hash = url.IndexOf('#');
            var path = hash < 0 ? url : url[..hash];
            var doc = path == "help" ? Docs.Index : Docs.Find(Uri.UnescapeDataString(path["help/".Length..]));
            if (doc is null) return "no such page";
            var anchor = hash < 0 ? null : Uri.UnescapeDataString(url[(hash + 1)..]);
            return anchor is null || doc.HasAnchor(anchor) ? null : $"{doc.Id} has no heading #{anchor}";
        }
        if (url.StartsWith(HelpLinks.RepoBlob, StringComparison.Ordinal))
        {
            var rest = url[HelpLinks.RepoBlob.Length..];
            var hash = rest.IndexOf('#');
            var file = Path.Combine(RepoRoot, Uri.UnescapeDataString(hash < 0 ? rest : rest[..hash]));
            if (!File.Exists(file)) return "no such file in the repo";
            if (hash >= 0 && file.EndsWith(".md", StringComparison.Ordinal))
            {
                var anchor = Uri.UnescapeDataString(rest[(hash + 1)..]);
                var slugger = new HelpMarkdown.Slugger();
                var headings = File.ReadAllLines(file).Where(l => Heading().IsMatch(l)).Select(l => slugger.Slug(HeadingText(l))).ToList();
                if (!headings.Contains(anchor)) return $"{Path.GetFileName(file)} has no heading #{anchor}";
            }
            return null;
        }
        if (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal))
            return null;
        return "not an address Help gives";
    }

    private static string HeadingText(string line) =>
        Regex.Replace(Regex.Replace(line.TrimStart('#').Trim(), @"`([^`]*)`", "$1"), @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("**", "");

    [Fact]
    public void Diagrams_come_out_as_containers_the_page_draws()
    {
        var sources = 0;
        foreach (var doc in Docs.Pages)
        {
            var markdown = File.ReadAllText(Path.Combine(DocsFolder, doc.Path));
            var blocks = Regex.Matches(markdown, @"^```mermaid\s*$", RegexOptions.Multiline).Count;
            sources += blocks;
            Assert.Equal(blocks, doc.Diagrams);
            Assert.Equal(blocks, Regex.Matches(doc.Html, @"<div class=""gb-mermaid"" data-mermaid><pre class=""gb-mermaid-src"">").Count);
        }
        Assert.True(sources > 0, "no ```mermaid block in the docs: the check isn't reading them");

        var one = Render("# T\n\n```mermaid\nflowchart LR\n    a[\"A & B\"] --> b\n```\n");
        Assert.Contains("<div class=\"gb-mermaid\" data-mermaid><pre class=\"gb-mermaid-src\">flowchart LR\n    a[&quot;A &amp; B&quot;] --&gt; b\n</pre></div>", one.Html);
        Assert.Equal(1, one.Diagrams);
        // Any other code: Gitberth's code frame, with its language and a copy button.
        var code = Render("# T\n\n```bash\necho <hi>\n```\n").Html;
        Assert.Contains("<div class=\"gb-code gb-doc-code\">", code);
        Assert.Contains("<span class=\"gb-code-title\">bash</span>", code);
        Assert.Contains("data-doc-copy", code);
        Assert.Contains("<pre><code class=\"language-bash\">echo &lt;hi&gt;\n</code></pre>", code);
    }

    [Fact]
    public void Headings_get_githubs_ids()
    {
        var doc = Render("# Usage\n\n## Usage\n\n## Ports & addresses\n\n## `docker run` options\n\n## What's new?\n\n### Usage\n\n## Héllo wörld\n\n## Usage 1\n\n## Under_score -- dash\n");
        Assert.Equal("usage", doc.TitleId);
        Assert.Equal(["usage-1", "ports--addresses", "docker-run-options", "whats-new", "usage-2", "héllo-wörld", "usage-1-1", "under_score----dash"],
            doc.Headings.Select(h => h.Id));
        Assert.True(doc.HasAnchor("usage"));
        Assert.Equal([(2, "Usage"), (2, "Ports & addresses"), (2, "docker run options")], doc.Headings.Take(3).Select(h => (h.Level, h.Text)));
        Assert.Equal(["usage-1", "ports--addresses", "docker-run-options", "whats-new", "usage-2"], doc.Contents.Take(5).Select(h => h.Id));
    }

    [Fact]
    public void Notes_and_alerts_are_callouts_and_other_quotes_stay_quotes()
    {
        var html = Render("# T\n\n> **Note**\n> Read this first.\n\n> [!WARNING]\n> Careful.\n\n> **Tip:** Short.\n\n> **Too many failed attempts.** See below.\n").Html;
        Assert.Contains("<div class=\"gb-callout gb-callout--note\" role=\"note\"><p class=\"gb-callout-title\">", html);
        Assert.Contains("Note</p>\n<p>Read this first.</p>", html);
        Assert.Contains("<div class=\"gb-callout gb-callout--warning\" role=\"note\">", html);
        Assert.Contains("Warning</p>\n<p>Careful.</p>", html);
        Assert.Contains("Tip</p>\n<p>Short.</p>", html);
        Assert.DoesNotContain("<strong>Note</strong>", html);
        Assert.Contains("<blockquote>\n<p><strong>Too many failed attempts.</strong> See below.</p>\n</blockquote>", html);
    }

    [Fact]
    public void Links_lead_where_github_would_and_open_in_gitberth()
    {
        var links = new HelpLinks(new HashSet<string> { "README", "faq", "guides/networking", "guides/domains" },
            new Dictionary<string, HelpMedia> { ["images/a.webp"] = new("images/a.webp", [], "image/webp", 10, 5) }, "9.9.9-abc");

        Assert.Equal("help/guides/networking#own-lan-addresses", links.Resolve("README.md", "guides/networking.md#own-lan-addresses").Href);
        Assert.Equal("help/guides/networking", links.Resolve("guides/domains.md", "networking.md").Href);
        Assert.Equal("help/faq", links.Resolve("guides/domains.md", "../faq.md").Href);
        Assert.Equal("help", links.Resolve("guides/domains.md", "../README.md").Href);
        Assert.Equal("help/guides/domains#the-down-page", links.Resolve("guides/domains.md", "#the-down-page").Href);
        Assert.Equal(("help/guides/domains", "the-down-page"), (links.Resolve("guides/domains.md", "#the-down-page").Href.Split('#')[0], links.Resolve("guides/domains.md", "#the-down-page").Anchor));
        // Outside docs/: the repo's file on GitHub.
        Assert.Equal(new HelpTarget(HelpTargetKind.Repo, "https://github.com/johnhowelljr/Gitberth/blob/main/README.md#license", "README.md", "license"),
            links.Resolve("faq.md", "../README.md#license"));
        Assert.Equal("https://github.com/johnhowelljr/Gitberth/blob/main/install.sh", links.Resolve("guides/domains.md", "../../install.sh").Href);
        Assert.Equal("https://github.com/johnhowelljr/Gitberth/blob/main/docker-compose.yml", links.Resolve("guides/domains.md", "/docker-compose.yml").Href);
        // Pictures: the media endpoint, with the version.
        Assert.Equal(new HelpTarget(HelpTargetKind.Media, "help-media/images/a.webp?v=9.9.9-abc", "images/a.webp"), links.Resolve("guides/domains.md", "../images/a.webp"));
        // Other sites as they are; what isn't there is named.
        Assert.Equal(HelpTargetKind.External, links.Resolve("faq.md", "https://example.com/x").Kind);
        Assert.Equal(HelpTargetKind.External, links.Resolve("faq.md", "mailto:you@example.com").Kind);
        Assert.Equal(HelpTargetKind.Missing, links.Resolve("faq.md", "guides/nowhere.md").Kind);
        Assert.Equal(HelpTargetKind.Missing, links.Resolve("faq.md", "images/nowhere.png").Kind);
        Assert.Equal(HelpTargetKind.Missing, links.Resolve("faq.md", "../../../etc/passwd").Kind);
    }

    [Fact]
    public void Links_out_open_in_a_new_tab_with_a_mark_and_pictures_load_lazily_with_their_size()
    {
        var html = Render("# T\n\n[Site](https://example.com) and <https://example.org>, [repo](../install.sh), [page](faq.md#x).\n\n![A picture](images/a.png)\n",
            ("images/a.png", Png(640, 360))).Html;
        Assert.Contains("<a href=\"https://example.com\" class=\"gb-ext\" target=\"_blank\" rel=\"noopener\">Site</a>", html);
        Assert.Contains("<a href=\"https://example.org\" class=\"gb-ext\" target=\"_blank\" rel=\"noopener\">https://example.org</a>", html);
        Assert.Contains("<a href=\"https://github.com/johnhowelljr/Gitberth/blob/main/install.sh\" class=\"gb-ext\" target=\"_blank\" rel=\"noopener\">repo</a>", html);
        Assert.Contains("<a href=\"help/faq#x\">page</a>", html);
        Assert.Matches(@"<a href=""help-media/images/a\.png\?v=[^""]+"" class=""gb-doc-zoom"" target=""_blank"" aria-label=""Open the picture larger: A picture""><img src=""help-media/images/a\.png\?v=[^""]+"" loading=""lazy"" decoding=""async"" width=""640"" height=""360"" alt=""A picture"" /></a>", html);

        // And the docs' own pictures, each with its size.
        var pictures = Docs.Pages.SelectMany(d => ImgTag().Matches(d.Html).Select(m => m.Value)).ToList();
        Assert.NotEmpty(pictures);
        Assert.All(pictures, img => Assert.Matches(@"loading=""lazy"".*width=""\d+"" height=""\d+""", img));
    }

    [Fact]
    public void Pictures_say_their_size_from_their_header()
    {
        Assert.Equal((640, 360), HelpMedia.SizeOf("image/png", Png(640, 360)));
        Assert.Equal((300, 150), HelpMedia.SizeOf("image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"300\" height=\"150\"></svg>")));
        Assert.Equal((24, 12), HelpMedia.SizeOf("image/svg+xml", Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 24 12\"></svg>")));
        // A docs screenshot (WebP): the size its header says.
        var shot = Docs.Pages.SelectMany(d => d.Links).First(l => l is { Image: true, Target.Kind: HelpTargetKind.Media } && l.Target.Path!.EndsWith(".webp", StringComparison.Ordinal));
        var media = Docs.Media(shot.Target.Path!)!;
        Assert.Equal("image/webp", media.ContentType);
        Assert.True(media is { Width: > 100, Height: > 50 }, $"{media.Path}: {media.Width}x{media.Height}");
        Assert.Equal((null, null), HelpMedia.SizeOf("image/webp", [1, 2, 3]));
    }

    // ---- Search ----

    [Fact]
    public void Search_finds_titles_first_then_headings_then_text_with_every_word()
    {
        var docs = Set(
            ("guides/domains.md", "# Domains\n\nPublic addresses for your apps.\n\n## Turn on domains\n\nIn Settings.\n"),
            ("guides/networking.md", "# Networking\n\nPorts.\n\n## Domains and ports\n\nHow they meet.\n\n## Other\n\nA domains word in the text, with the tunnel.\n"),
            ("faq.md", "# FAQ\n\n## Questions\n\nCan I use domains? Yes, and the tunnel too.\n\n## Related\n\n- [Domains](guides/domains.md)\n"));

        var hits = docs.Search("domains");
        Assert.Equal(
        [
            (HelpHitKind.Title, "help/guides/domains"),
            (HelpHitKind.Heading, "help/guides/domains#turn-on-domains"),
            (HelpHitKind.Heading, "help/guides/networking#domains-and-ports"),
            (HelpHitKind.Text, "help/guides/networking#other"),
            (HelpHitKind.Text, "help/faq#questions"),
        ], hits.Select(h => (h.Kind, h.Href)));
        Assert.Equal("Domains", hits[0].Title);
        Assert.Equal("Turn on domains", hits[1].Title);

        // Every word, in one passage: "domains tunnel" is in two passages, "domains banana" in none.
        Assert.Equal(["help/guides/networking#other", "help/faq#questions"], docs.Search("Domains,  TUNNEL").Select(h => h.Href));
        Assert.Empty(docs.Search("domains banana"));
        Assert.Empty(docs.Search("   "));

        // The snippet marks the words it found.
        var snippet = docs.Search("tunnel")[0].Snippet;
        Assert.Contains(("tunnel", true), snippet.Parts);
        Assert.Equal("A domains word in the text, with the tunnel.", snippet.ToString());
    }

    [Fact]
    public void A_long_passage_is_cut_around_the_words_found()
    {
        var text = string.Join(' ', Enumerable.Repeat("filler", 60)) + " the needle is here " + string.Join(' ', Enumerable.Repeat("after", 60));
        var snippet = Snippet.Of(text, ["needle"]);
        var shown = snippet.ToString();
        Assert.StartsWith("…", shown);
        Assert.EndsWith("…", shown);
        Assert.True(shown.Length < 190, shown);
        Assert.Single(snippet.Parts, p => p.Marked);
    }

    // ---- Routes ----

    [Fact]
    public void Routes_match_one_segment_any_rest_and_the_first_rule_wins()
    {
        bool Matches(string route, string path) => new HelpRouteRule(route, new HelpLink("x")).Matches(path);

        Assert.True(Matches("/", ""));
        Assert.True(Matches("/", "/"));
        Assert.False(Matches("/", "apps"));
        Assert.True(Matches("/apps/*/settings", "apps/postgres/settings"));
        Assert.True(Matches("/apps/*/settings", "/Apps/postgres/Settings/?tab=1#x"));
        Assert.False(Matches("/apps/*/settings", "apps/postgres/network"));
        Assert.False(Matches("/apps/*/settings", "apps/settings"));
        Assert.False(Matches("/apps/*/settings", "apps/a/b/settings"));
        Assert.True(Matches("/apps/**", "apps"));
        Assert.True(Matches("/apps/**", "apps/postgres/deployments/12"));
        Assert.True(Matches("/apps/**/logs", "apps/a/b/logs"));
        Assert.True(Matches("/apps/**/logs", "apps/logs"));
        Assert.False(Matches("/apps/**/logs", "apps/a/b"));
        Assert.True(Matches("/apps/*", "apps/gb%20web"));

        var manifest = HelpManifest.Parse("""
            sections:
              - title: All
                pages: [a, b, c]
            routes:
              - route: /apps/new
                page: b#start
              - route: /apps/**
                page: a
              - route: /apps/new
                page: c
            """);
        Assert.Empty(manifest.Problems);
        Assert.Equal(new HelpLink("b", "start"), manifest.ForPath("apps/new"));
        Assert.Equal(new HelpLink("a"), manifest.ForPath("apps/new/x"));
        Assert.Null(manifest.ForPath("settings"));
        Assert.Equal("help/b#start", manifest.ForPath("apps/new")!.Href);

        // The shipped routes: Settings opens Gitberth's settings, an app's Settings tab its own.
        Assert.Equal("reference/gitberth-settings", Docs.ForPath("settings")!.Page);
        Assert.Equal("reference/app-settings", Docs.ForPath("apps/web/settings")!.Page);
        Assert.Equal(Docs.Manifest.ForPath("apps/new")!.Page, Docs.ForPath("apps/new")!.Page);
    }

    [Fact]
    public void The_manifest_says_what_is_wrong_with_it()
    {
        var docs = Set(("a.md", "# A\n"), ("b.md", "# B\n"), ("c.md", "no title\n"), ("help.yaml", """
            sections:
              - title: One
                pages: [a, nowhere, a]
              - title: ""
                pages: []
            routes:
              - route: apps
                page: a
              - route: /x
                page: a#nothing
            colour: blue
            """));
        var problems = string.Join("\n", docs.Problems);
        Assert.Contains("help.yaml has a key Help doesn't know: colour.", problems);
        Assert.Contains("lists nowhere, which isn't a page of the docs", problems);
        Assert.Contains("lists a twice", problems);
        Assert.Contains("A section of help.yaml has no title.", problems);
        Assert.Contains("The route apps must start with /.", problems);
        Assert.Contains("a#nothing, a heading that page doesn't have", problems);
        Assert.Contains("docs/b.md isn't in any section of help.yaml.", problems);
        Assert.Contains("docs/c.md has no title", problems);
        // A page the manifest doesn't list still opens, after the listed ones.
        Assert.Equal(["a", "b", "c"], docs.Pages.Select(d => d.Id));
    }

    // ---- The endpoints ----

    [Fact]
    public async Task The_endpoints_need_signing_in_and_serve_pictures_with_their_type()
    {
        await using var app = await HostAsync();
        var anonymous = app.GetTestClient();
        var signedIn = app.GetTestClient();
        signedIn.DefaultRequestHeaders.Add(SignedIn.Header, "admin");
        var picture = Docs.Pages.SelectMany(d => d.Links).First(l => l is { Image: true, Target.Kind: HelpTargetKind.Media }).Target;

        foreach (var url in new[] { picture.Href, HelpEndpoints.TipsUrl(TipCatalog.Bundled), "help-media/images/nowhere.webp" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/" + url)).StatusCode);

        var image = await signedIn.GetAsync("/" + picture.Href);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(HelpMedia.TypeOf(picture.Path!), image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Docs.Media(picture.Path!)!.Bytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Contains("max-age=31536000", image.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/help-media/images/nowhere.webp")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/help-media/help.yaml")).StatusCode);

        var tips = await signedIn.GetAsync("/" + HelpEndpoints.TipsUrl(TipCatalog.Bundled));
        Assert.Equal(HttpStatusCode.OK, tips.StatusCode);
        Assert.Equal("application/json", tips.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"nav.apps\"", await tips.Content.ReadAsStringAsync());

        Assert.Equal(["image/webp", "image/png", "image/svg+xml", "image/jpeg", "image/gif"],
            new[] { "a.webp", "a.PNG", "a.svg", "a.jpg", "a.gif" }.Select(HelpMedia.TypeOf));
        Assert.Null(HelpMedia.TypeOf("help.yaml"));
    }

    [Fact]
    public async Task An_svg_picture_runs_nothing()
    {
        var docs = Set(("a.md", "# A\n\n![x](images/x.svg)\n"), ("images/x.svg", Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 4 4\"></svg>")));
        await using var app = await HostAsync(docs);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(SignedIn.Header, "admin");
        var svg = await client.GetAsync("/help-media/images/x.svg");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static async Task<WebApplication> HostAsync(HelpDocs? docs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(docs ?? Docs);
        builder.Services.AddSingleton(TipCatalog.Bundled);
        builder.Services.AddAuthentication(SignedIn.Name).AddScheme<AuthenticationSchemeOptions, SignedIn>(SignedIn.Name, null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        HelpEndpoints.Map(app);
        await app.StartAsync();
        return app;
    }

    /// <summary>A sign-in for the tests: a request with the header is signed in as its value; one without is challenged (401).</summary>
    private sealed class SignedIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "test";
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.TryGetValue(Header, out var user)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Name)), Name))
                : AuthenticateResult.NoResult());
    }

    // ---- Helpers ----

    /// <summary>One page rendered as the docs are, with any pictures it names.</summary>
    private static HelpDoc Render(string markdown, params (string Path, byte[] Bytes)[] media)
    {
        var links = new HelpLinks(new HashSet<string> { "README", "faq", "page" },
            media.Select(m => HelpMedia.From(m.Path, m.Bytes)!).ToDictionary(m => m.Path), "1.0.0-test");
        return HelpMarkdown.Render("page.md", markdown, links);
    }

    /// <summary>A docs folder of its own: pages, pictures and a help.yaml (one section of every page, unless given).</summary>
    private static HelpDocs Set(params (string Path, object Content)[] files)
    {
        var list = files.Select(f => (f.Path, f.Content as byte[] ?? Encoding.UTF8.GetBytes((string)f.Content))).ToList();
        if (list.All(f => f.Path != HelpDocs.ManifestPath))
        {
            var pages = list.Where(f => f.Path.EndsWith(".md", StringComparison.Ordinal) && f.Path != "README.md").Select(f => "      - " + f.Path[..^3]);
            list.Add((HelpDocs.ManifestPath, Encoding.UTF8.GetBytes("sections:\n  - title: All\n    pages:\n" + string.Join("\n", pages) + "\nroutes: []\n")));
        }
        if (list.All(f => f.Path != "README.md")) list.Add(("README.md", Encoding.UTF8.GetBytes("# Index\n")));
        return new HelpDocs(list);
    }

    private static byte[] Png(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    internal static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Gitberth")) && File.Exists(Path.Combine(dir.FullName, "docs", "help.yaml")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("The repo (src/Gitberth and docs/help.yaml) wasn't found above " + AppContext.BaseDirectory);
        }
    }

    private static string DocsFolder => Path.Combine(RepoRoot, "docs");

    [GeneratedRegex(@"\sid=""([^""]+)""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"\s(href|src)=""([^""]*)""")]
    private static partial Regex UrlAttribute();

    [GeneratedRegex(@"<img\b[^>]*>")]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"^#{1,6} ")]
    private static partial Regex Heading();
}
