using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Help;

/// <summary>A link in a tip's text: [words](help:page#anchor) or [words](https://…).</summary>
public sealed record TipTextLink(string Words, string Target)
{
    /// <summary>help:page#anchor: a page of the built-in Help, opened in Gitberth.</summary>
    public HelpLink? Help => Target.StartsWith("help:", StringComparison.Ordinal) ? HelpManifest.ParseLink(Target["help:".Length..]) : null;

    public bool External => Target.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(Target, UriKind.Absolute, out _);
}

/// <summary>
/// A tip's text as safe HTML: everything is escaped first, then a few marks are allowed: **bold**, `code`, *emphasis*,
/// [words](help:page#anchor) as a link in Gitberth and [words](https://…) as a link to another site (a new tab). A link
/// to anything else stays text, and the catalog's checks name it.
/// </summary>
public static partial class TipText
{
    [GeneratedRegex(@"\[([^\[\]]+)\]\(([^()\s]+)\)")]
    private static partial Regex LinkMark();

    public static string ToHtml(string text)
    {
        var html = new StringBuilder(text.Length + 32);
        Inline(html, text, links: true);
        return html.ToString();
    }

    /// <summary>The text as a reader sees it, marks and link targets left out: what its length counts.</summary>
    public static string Visible(string text) =>
        LinkMark().Replace(text, m => m.Groups[1].Value).Replace("**", "").Replace("`", "").Replace("*", "");

    public static IReadOnlyList<TipTextLink> Links(string text) =>
        LinkMark().Matches(text).Select(m => new TipTextLink(m.Groups[1].Value, m.Groups[2].Value)).ToList();

    private static void Inline(StringBuilder html, string s, bool links)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '`' && s.IndexOf('`', i + 1) is var close and > 0)
            {
                html.Append("<code>");
                Escape(html, s[(i + 1)..close]);
                html.Append("</code>");
                i = close + 1;
            }
            else if (c == '[' && links && LinkMark().Match(s, i) is { Success: true } m && m.Index == i)
            {
                var link = new TipTextLink(m.Groups[1].Value, m.Groups[2].Value);
                if (link.Help is { } help)
                {
                    html.Append("<a href=\"");
                    Escape(html, help.Href);
                    html.Append("\">");
                    Inline(html, link.Words, links: false);
                    html.Append("</a>");
                }
                else if (link.External)
                {
                    html.Append("<a class=\"gb-ext\" target=\"_blank\" rel=\"noopener\" href=\"");
                    Escape(html, link.Target);
                    html.Append("\">");
                    Inline(html, link.Words, links: false);
                    html.Append("</a>");
                }
                else
                {
                    Inline(html, link.Words, links: false);
                }
                i = m.Index + m.Length;
            }
            else if (c == '*' && i + 1 < s.Length && s[i + 1] == '*' && s.IndexOf("**", i + 2, StringComparison.Ordinal) is var end and > 0)
            {
                html.Append("<strong>");
                Inline(html, s[(i + 2)..end], links);
                html.Append("</strong>");
                i = end + 2;
            }
            else if (c == '*' && Emphasis(s, i) is var stop and > 0)
            {
                html.Append("<em>");
                Inline(html, s[(i + 1)..stop], links);
                html.Append("</em>");
                i = stop + 1;
            }
            else
            {
                Escape(html, c.ToString());
                i++;
            }
        }
    }

    // The * that closes an emphasis opened at i: the next single one (not half of a **), with text in between.
    private static int Emphasis(string s, int i)
    {
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] != '*') continue;
            if (j + 1 < s.Length && s[j + 1] == '*') { j++; continue; }
            return j > i + 1 ? j : -1;
        }
        return -1;
    }

    private static void Escape(StringBuilder html, string text)
    {
        foreach (var c in text)
        {
            html.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
    }
}
