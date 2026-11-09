using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The words the networking redesign retired stay off every page. It only looks: it opens each kind of page and reads
/// what Gitberth wrote there, leaving out what others wrote (logs, a container's settings and labels, commit messages).
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class CopyTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    // What replaced them: Addresses and the Network tab, LAN, no SSH at all, Address, Access, the host, nothing.
    private static readonly string[] Retired = ["How to reach it", "From your PC", "ssh -N", "Endpoint", "Bind IP", "this machine", "forwarder"];

    // The one place "forwarder" stays: the notice about a port forwarder an older Gitberth left behind.
    private const string LegacyForwarder = "old Gitberth port forwarder";

    // The page's own words: its text, tooltips, accessible names and placeholders. Logs, a container's environment and
    // labels, commit messages and the docker run line are someone else's.
    private const string Script = """
        () => {
          const theirs = '.gb-terminal, .gb-env-table, .gb-envkey, .gb-envval, .gb-arow-sub, .gb-commit-msg, span:has(> .gb-sha), pre, script, style';
          const root = document.querySelector('.gb-shell') ?? document.body;
          const words = [];
          const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
          for (let n = walk.nextNode(); n; n = walk.nextNode())
            if (!n.parentElement?.closest(theirs)) words.push(n.nodeValue);
          for (const e of root.querySelectorAll('[title], [aria-label], [placeholder]'))
            if (!e.closest(theirs))
              for (const name of ['title', 'aria-label', 'placeholder'])
                if (e.hasAttribute(name)) words.push(e.getAttribute(name));
          return words.join('\n');
        }
        """;

    [UiFact]
    public async Task No_page_uses_the_retired_networking_words()
    {
        var (page, errors) = await gitberth.OpenAsync("/");
        await page.Locator(".gb-side").First.WaitForAsync(Slow);
        var paths = new List<string> { "/", "/apps", "/network", "/health", "/settings", "/apps/new" };

        // Every app's tabs but its logs, which are the app's own words.
        foreach (var app in await HrefsAsync(page, ".gb-side a.gb-nav-app"))
            if (!app.EndsWith("/new", StringComparison.Ordinal))
                paths.AddRange(new[] { "", "/network", "/activity", "/environment", "/settings" }.Select(tab => $"/{app}{tab}"));
        // The apps' last deploys, and the containers the dashboard puts first: Gitberth, the tunnel, databases.
        await page.Locator(".gb-kpis2").WaitForAsync(Slow);
        await SettleAsync(page);
        paths.AddRange((await HrefsAsync(page, ".gb-tl a[href*='/deployments/']")).Select(d => "/" + d));
        foreach (var container in await HrefsAsync(page, "a.gb-arow-title[href^='apps/']"))
            paths.AddRange(new[] { "", "/network" }.Select(tab => $"/{container}{tab}"));

        var found = new List<string>();
        foreach (var path in paths.Distinct())
        {
            await page.GotoAsync(UiTarget.Url + path);
            await SettleAsync(page);
            var words = (await page.EvaluateAsync<string>(Script)).Replace(LegacyForwarder, "", StringComparison.OrdinalIgnoreCase);
            foreach (var word in Retired)
            {
                // Endpoint is matched as written, so a setting named API_ENDPOINT doesn't count.
                var at = words.IndexOf(word, word == "Endpoint" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (at >= 0) found.Add($"{path}: \"{word}\" in \"{Around(words, at, word.Length)}\"");
            }
        }

        Assert.True(found.Count == 0, "Retired words on the pages:\n" + string.Join('\n', found));
        Assert.Empty(errors);
    }

    // The links a page lists, without a leading slash, each once.
    private static async Task<List<string>> HrefsAsync(IPage page, string selector)
    {
        var hrefs = new List<string>();
        foreach (var link in await page.Locator(selector).AllAsync())
            if (await link.GetAttributeAsync("href") is { } href) hrefs.Add(href.TrimStart('/'));
        return hrefs.Distinct().ToList();
    }

    // A page draws skeletons until what it reads has come back; the first look at the host's sockets takes a moment.
    private static async Task SettleAsync(IPage page)
    {
        await page.Locator(".gb-page").WaitForAsync(Slow);
        await page.WaitForFunctionAsync("() => !document.querySelector('.gb-page .gb-skel, .gb-page .mud-skeleton')", null, new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(800);
    }

    // The words around a find, on one line, for the failure message.
    private static string Around(string text, int at, int length)
    {
        var from = Math.Max(0, at - 40);
        var to = Math.Min(text.Length, at + length + 40);
        return text[from..to].ReplaceLineEndings(" ");
    }
}
