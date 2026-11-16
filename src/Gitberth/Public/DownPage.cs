using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Domains;
using static System.Net.WebUtility;

namespace Gitberth.Public;

/// <summary>What a domain's down page says.</summary>
/// <param name="Host">The domain, as visitors typed it.</param>
/// <param name="Reason">Why it's down; null when that isn't known (a visit just as the app came back).</param>
/// <param name="Message">The owner's own sentence, when they wrote one.</param>
/// <param name="StatusUrl">The status page on the internet, when it's on and lists this app.</param>
/// <param name="Minimal">A protected domain's: nothing but its host name and that it's unavailable.</param>
public sealed record DownView(string Host, DownReason? Reason, string? Message, string? StatusUrl, bool Minimal)
{
    /// <summary>When to try again, in seconds: soon while it's being updated or is down, later when it was stopped on purpose.</summary>
    public int RetryAfter => Reason switch
    {
        DownReason.Stopped => 600,
        DownReason.Held => 120,
        _ => 30,
    };
}

/// <summary>
/// The page a domain shows while its app can't be served: a plain 503 page with inline styles (light and dark, good on a
/// phone, nothing loaded from anywhere), or a small JSON answer for a client that asks for JSON. It never names the app,
/// its container, an address or an error; a protected domain's says nothing beyond its host name.
/// </summary>
public static class DownPage
{
    public const string Title = "This site is temporarily unavailable";

    /// <summary>The reason line, by state.</summary>
    public static string ReasonLine(DownReason? reason) => reason switch
    {
        DownReason.Updating => "It's being updated and will be back in a moment.",
        DownReason.Stopped or DownReason.NotRunning => "It isn't running right now.",
        DownReason.Held => "It's held back for the moment.",
        _ => "It isn't available right now.",
    };

    public static string Html(DownView view)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <meta name="color-scheme" content="light dark">
            """);
        html.Append("<title>").Append(HtmlEncode(view.Host)).Append(" is temporarily unavailable</title>\n");
        html.Append("<style>").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        html.Append("""<div class="mark" aria-hidden="true"><svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7.5v5.25l3 1.75"/></svg></div>""");
        html.Append('\n').Append("<h1>").Append(Title).Append("</h1>\n");
        html.Append("<p class=\"host\">").Append(HtmlEncode(view.Host)).Append("</p>\n");
        if (!view.Minimal)
        {
            // One of a few fixed lines (nothing in it comes from outside).
            html.Append("<p>").Append(ReasonLine(view.Reason)).Append("</p>\n");
            if (view.Message is { Length: > 0 } message) html.Append("<p class=\"msg\">").Append(HtmlEncode(message)).Append("</p>\n");
        }
        html.Append("<div class=\"foot\"><span>Please try again in a few minutes.</span>");
        if (!view.Minimal && view.StatusUrl is { } status)
            html.Append("<a href=\"").Append(HtmlEncode(status)).Append("\" rel=\"noopener\">See the status page</a>");
        html.Append("</div>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    public static string Json(DownView view)
    {
        var body = new JsonObject
        {
            ["status"] = 503,
            ["error"] = "unavailable",
            ["title"] = Title,
            ["host"] = view.Host,
            ["retryAfter"] = view.RetryAfter,
        };
        if (!view.Minimal)
        {
            body["reason"] = view.Reason switch
            {
                DownReason.Updating => "updating",
                DownReason.Stopped or DownReason.NotRunning => "not_running",
                DownReason.Held => "held",
                _ => "unavailable",
            };
            body["detail"] = ReasonLine(view.Reason);
            if (view.Message is { Length: > 0 } message) body["message"] = message;
            if (view.StatusUrl is { } status) body["statusPage"] = status;
        }
        return body.ToJsonString();
    }

    private const string Css = """
        :root{--bg:#f5f6f8;--card:#ffffff;--text:#15171c;--muted:#5d6472;--line:#e2e5ea;--accent:#c46a00;--soft:#fdf3e6;--link:#1f56d6}
        @media (prefers-color-scheme:dark){:root{--bg:#0c0e12;--card:#151920;--text:#e7e9ee;--muted:#9ba3b1;--line:#262b35;--accent:#f0a540;--soft:#2a2116;--link:#86aefc}}
        *{box-sizing:border-box}
        html,body{margin:0;min-height:100%}
        body{background:var(--bg);color:var(--text);font:16px/1.55 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:24px 16px;-webkit-text-size-adjust:100%}
        main{width:100%;max-width:520px;background:var(--card);border:1px solid var(--line);border-radius:16px;padding:32px 28px;box-shadow:0 1px 2px rgba(0,0,0,.05),0 10px 30px rgba(0,0,0,.07)}
        .mark{width:44px;height:44px;border-radius:12px;display:flex;align-items:center;justify-content:center;background:var(--soft);color:var(--accent);margin-bottom:18px}
        h1{font-size:1.4rem;line-height:1.25;margin:0 0 6px;letter-spacing:-.01em;font-weight:650}
        .host{color:var(--muted);margin:0 0 18px;font-size:.95rem;overflow-wrap:anywhere}
        p{margin:0 0 12px}
        .msg{border-left:3px solid var(--accent);background:var(--soft);padding:10px 14px;border-radius:0 8px 8px 0;margin:16px 0;overflow-wrap:anywhere}
        .foot{margin-top:20px;padding-top:16px;border-top:1px solid var(--line);color:var(--muted);font-size:.9rem;display:flex;flex-wrap:wrap;gap:8px 16px;justify-content:space-between}
        a{color:var(--link);text-underline-offset:2px}
        @media (max-width:420px){main{padding:26px 20px;border-radius:14px}h1{font-size:1.25rem}}
        """;
}
