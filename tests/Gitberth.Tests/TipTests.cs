using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gitberth.Components.Shared;
using Gitberth.Help;

namespace Gitberth.Tests;

/// <summary>
/// The tips' catalog (Help/Tips/&lt;area&gt;.yaml): it passes its own checks against the docs as shipped; every key the UI
/// uses (Tip="…", Key="…", data-tip="…", Tip: "…" in C#, and the badges' own) is in it and every tip in it is used; its
/// text comes out as safe HTML with only the marks it allows; and the checks catch what a tip can get wrong.
/// </summary>
public sealed partial class TipTests
{
    [Fact]
    public void The_catalog_passes_its_checks()
    {
        var tips = TipCatalog.Bundled;
        Assert.True(tips.Entries.Count > 40, $"only {tips.Entries.Count} tips: the catalog isn't read from the assembly");
        var problems = tips.Check(HelpDocs.Bundled);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_key_the_ui_uses_is_in_the_catalog_and_every_tip_is_used()
    {
        var used = UsedKeys();
        Assert.Contains("nav.apps", used.Keys);
        Assert.Contains("topbar.help", used.Keys);
        Assert.Contains("status.running", used.Keys);

        var catalog = TipCatalog.Bundled.Entries.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = used.Where(u => !catalog.Contains(u.Key)).Select(u => $"{u.Key} (in {string.Join(", ", u.Value)})").ToList();
        Assert.True(missing.Count == 0, "Keys the UI uses that the catalog doesn't have:\n" + string.Join("\n", missing));
        var unused = catalog.Where(k => !used.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "Tips no part of the UI uses (a key in .razor or .cs, or a badge's own):\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Each_badge_and_status_has_a_tip_by_what_it_shows()
    {
        Assert.Equal("status.running", TipKeys.State(Apps.AppState.Running));
        Assert.Equal("status.needs-decision", TipKeys.State(Apps.AppState.NeedsDecision));
        Assert.Equal("status.deploy.rolled-back", TipKeys.Deploy(Data.DeployStatus.RolledBack));
        Assert.Equal("badge.access.host-only", TipKeys.Access(Network.AddressKind.HostOnly));
        Assert.Equal("badge.access.own-lan", TipKeys.Access(Network.AddressKind.OwnLan));
        Assert.Equal("badge.source.manual", TipKeys.Source(Apps.AppSource.Manual));
        var catalog = TipCatalog.Bundled;
        Assert.All(TipKeys.Computed, key => Assert.True(catalog.Find(key) is not null, $"no tip {key}"));
        // The labels' own words are the tips' titles: the state's word, the kind's, the source's.
        Assert.Equal("Exited with an error", catalog.Find(TipKeys.State(Apps.AppState.Crashed))!.Title);
        Assert.Equal(AccessTag.Of(Network.AddressKind.HostOnly).Word, catalog.Find(TipKeys.Access(Network.AddressKind.HostOnly))!.Title);
        Assert.Equal(SourceBadge.WordOf(Apps.AppSource.Local), catalog.Find(TipKeys.Source(Apps.AppSource.Local))!.Title);
    }

    [Fact]
    public void A_tips_text_is_escaped_with_only_its_marks_allowed()
    {
        Assert.Equal("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;quotes&quot;", TipText.ToHtml("<script>alert('x')</script> & \"quotes\""));
        Assert.Equal("A <strong>bold</strong>, <em>emphasised</em> and <code>&lt;code&gt; **as is**</code> word.",
            TipText.ToHtml("A **bold**, *emphasised* and `<code> **as is**` word."));
        Assert.Equal("See <a href=\"help/guides/networking#own-lan-addresses\">own <strong>LAN</strong> addresses</a>.",
            TipText.ToHtml("See [own **LAN** addresses](help:guides/networking#own-lan-addresses)."));
        Assert.Equal("Read <a class=\"gb-ext\" target=\"_blank\" rel=\"noopener\" href=\"https://example.com/x?a=1&amp;b=2\">the site&#39;s docs</a>.",
            TipText.ToHtml("Read [the site's docs](https://example.com/x?a=1&b=2)."));
        // A link to anything else stays its words (or, when it isn't a link at all, plain text); a mark left open stays as it is.
        Assert.Equal("Click me, or me.", TipText.ToHtml("[Click me](javascript:void), or [me](http://example.com)."));
        Assert.Equal("[x](javascript:alert(1))", TipText.ToHtml("[x](javascript:alert(1))"));
        Assert.Equal("2 * 3 and **open", TipText.ToHtml("2 * 3 and **open"));
        Assert.Equal("A tip of 42 words.", TipText.Visible("A [tip](help:faq) of **42** `words`."));
        Assert.Equal(["help:faq", "https://example.com"], TipText.Links("[a](help:faq) and [b](https://example.com)").Select(l => l.Target));
    }

    [Fact]
    public void The_checks_catch_what_a_tip_gets_wrong()
    {
        var docs = HelpDocs.Bundled;
        var tips = new TipCatalog(
        [
            Yaml("good", """
                good.one:
                  title: One
                  text: A tip that's right, with [a link](help:faq) and **marks**.
                  default: "8080"
                  example: 9000
                  more: guides/networking#who-reaches-a-port-access
                good.title-only:
                  title: Title only
                good.long-title:
                  title: A title that goes on and on and on, far past what a card can hold in one line
                  text: Fine.
                good.long-text:
                  title: Long
                  text: "{{long}}"
                good.chatty:
                  title: Chatty
                  text: One. Two. Three. Four. Five.
                good.echo:
                  title: Echo
                  text: Echo.
                good.extra:
                  title: Extra
                  text: It has a field tips don't have.
                  colour: blue
                good.lost:
                  title: Lost
                  text: Its more is nowhere.
                  more: guides/nowhere
                good.no-heading:
                  title: No heading
                  text: Its heading isn't on the page.
                  more: guides/networking#no-such-heading
                good.bad-link:
                  title: Bad link
                  text: An [old link](http://example.com) and a [lost one](help:guides/nowhere).
                Bad.Shape:
                  title: Shape
                  text: Its key is in capitals.
                other.area:
                  title: Area
                  text: Its key belongs in other.yaml.
                good.list:
                  title: [a, list]
                  text: Its title is a list.
                """.Replace("{{long}}", new string('x', 451))),
            Yaml("dupe", """
                dupe.one:
                  title: Dupe
                  text: Fine here.
                """),
            Yaml("dupe2", "dupe.one:\n  title: Again\n  text: Defined twice.\n"),
            Yaml("broken", "broken.one: [unclosed\n"),
        ]);
        var problems = string.Join("\n", tips.Check(docs));

        Assert.DoesNotContain("good.one:", problems);
        Assert.Contains("good.yaml: good.title-only: it needs a text", problems);
        Assert.Contains("good.yaml: good.long-title: its title is 77 characters; at most 60.", problems);
        Assert.Contains("good.yaml: good.long-text: its text is 451 characters as read; at most 450.", problems);
        Assert.Contains("good.yaml: good.chatty: its text has 5 sentences; at most 4.", problems);
        Assert.Contains("good.yaml: good.echo: its text only repeats its title", problems);
        Assert.Contains("good.yaml: good.extra: it has a field tips don't have: colour", problems);
        Assert.Contains("good.lost: more leads to guides/nowhere, which isn't a help page.", problems);
        Assert.Contains("good.no-heading: more leads to guides/networking#no-such-heading, a heading that page doesn't have.", problems);
        Assert.Contains("good.bad-link: the link [old link](http://example.com) is neither help:<page>#<heading> nor https://.", problems);
        Assert.Contains("good.bad-link: the link [lost one] leads to guides/nowhere, which isn't a help page.", problems);
        Assert.Contains("good.yaml: Bad.Shape: a key is <area>.<thing>[.<detail>]", problems);
        Assert.Contains("good.yaml: other.area: its key belongs in its own area's file", problems);
        Assert.Contains("good.yaml: good.list: its title is text, not a list or a mapping.", problems);
        Assert.Contains("dupe2.yaml: dupe.one: it's defined twice", problems);
        Assert.Contains("broken.yaml: it isn't valid YAML", problems);

        var one = tips.Find("good.one")!;
        Assert.Equal(("8080", "9000"), (one.Default, one.Example));
        Assert.Equal(new HelpLink("guides/networking", "who-reaches-a-port-access"), one.More);
        Assert.Equal("A tip that&#39;s right, with <a href=\"help/faq\">a link</a> and <strong>marks</strong>.", one.Html);
        Assert.Null(tips.Find("good.title-only"));
    }

    [Fact]
    public void The_catalog_goes_to_the_browser_as_one_document()
    {
        using var json = JsonDocument.Parse(TipCatalog.Bundled.Json(HelpDocs.Bundled));
        Assert.Equal(TipCatalog.Bundled.Version, json.RootElement.GetProperty("version").GetString());
        var apps = json.RootElement.GetProperty("tips").GetProperty("nav.apps");
        Assert.Equal("Apps", apps.GetProperty("title").GetString());
        Assert.Equal(TipCatalog.Bundled.Find("nav.apps")!.Html, apps.GetProperty("html").GetString());
        Assert.Equal("help/guides/manage-apps#the-apps-list", apps.GetProperty("more").GetString());
        Assert.Equal(HelpDocs.Bundled.Find("guides/manage-apps")!.Title, apps.GetProperty("moreTitle").GetString());
        var search = json.RootElement.GetProperty("tips").GetProperty("topbar.search");
        Assert.False(search.TryGetProperty("more", out _));
        Assert.Equal("postgres, 5432 or ghcr.io", search.GetProperty("example").GetString());
        Assert.StartsWith("help-tips.json?v=", HelpEndpoints.TipsUrl(TipCatalog.Bundled));
    }

    // ---- What the UI uses ----

    /// <summary>
    /// Every key in Gitberth's .razor and .cs files (comments left out): Tip="…" and Key="…" on a Tip, data-tip="…",
    /// Tip: "…" or Tip = "…" in C# (TabItem, SegOption), and a component's own default (Tip ?? "…"); plus the keys the
    /// badges compute (<see cref="TipKeys.Computed"/>). A key built at run time any other way can't be checked: list it in
    /// TipKeys.
    /// </summary>
    internal static Dictionary<string, List<string>> UsedKeys()
    {
        var src = Path.Combine(HelpTests.RepoRoot, "src", "Gitberth");
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string where)
        {
            if (!used.TryGetValue(key, out var list)) used[key] = list = [];
            if (!list.Contains(where)) list.Add(where);
        }
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            text = file.EndsWith(".razor", StringComparison.Ordinal) ? RazorComment().Replace(text, "") : CsComment().Replace(text, "");
            var name = Path.GetRelativePath(src, file).Replace('\\', '/');
            foreach (Match m in TipKey().Matches(text)) Add(m.Groups["key"].Value, name);
        }
        foreach (var key in TipKeys.Computed) Add(key, "TipKeys.cs");
        return used;
    }

    private static (string, byte[]) Yaml(string area, string yaml) => (area + ".yaml", Encoding.UTF8.GetBytes(yaml));

    [GeneratedRegex(@"(?:<Tip\b[^>]*?\bKey=""|\bTip=""|\bdata-tip=""|\bTip:\s*""|\bTip\s*=\s*""|\bTip\s*\?\?\s*"")(?<key>[a-z0-9][a-z0-9.-]*)""")]
    private static partial Regex TipKey();

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CsComment();
}
