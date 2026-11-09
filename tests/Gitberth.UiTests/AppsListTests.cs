using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The one Apps list: every container on the host as an app, whoever started it, with the host strip, search, filters
/// and the menu's pinned apps. It only looks.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class AppsListTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    private static async Task<List<string>> TitlesAsync(IPage page) =>
        (await page.Locator(".gb-arow:not(.gb-arow-head) .gb-arow-title").AllInnerTextsAsync()).Select(t => t.Trim()).ToList();

    private static async Task<List<string>> KeysAsync(IPage page)
    {
        var keys = new List<string>();
        foreach (var link in await page.Locator(".gb-arow:not(.gb-arow-head) a.gb-arow-title").AllAsync())
            if (await link.GetAttributeAsync("href") is { } href) keys.Add(Uri.UnescapeDataString(href.TrimStart('/')["apps/".Length..]));
        return keys;
    }

    [UiFact]
    public async Task Every_container_is_listed_as_an_app_and_nothing_else()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        await page.Locator(".gb-arow:not(.gb-arow-head)").First.WaitForAsync(Slow);
        var keys = await KeysAsync(page);

        // The apps Gitberth deploys, the database, the tunnel, Gitberth itself and the samples, side by side.
        Assert.Subset(keys.ToHashSet(), new HashSet<string> { "gb-kanban", "gb-orb", "postgres", "cloudflare-tunnel", "gitberth" });
        Assert.True(keys.Count(k => k.StartsWith("sample-", StringComparison.Ordinal)) >= 70, $"Only {keys.Count} apps listed.");
        // Never a deploy's or a change's temporary, and never twice.
        Assert.DoesNotContain(keys, k => k.EndsWith("_next", StringComparison.Ordinal) || k.EndsWith("_prev", StringComparison.Ordinal)
            || k.Contains("_new_", StringComparison.Ordinal) || k.Contains("_old_", StringComparison.Ordinal));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // The foot counts them all.
        Assert.Contains($"{keys.Count} of {keys.Count} apps", await page.Locator(".gb-list-foot").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task One_row_design_for_every_source()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        await page.Locator(".gb-arow:not(.gb-arow-head)").First.WaitForAsync(Slow);
        foreach (var (name, source) in new[] { ("kanban", "Git"), ("postgres", "Existing"), ("cloudflare-tunnel", "Existing") })
        {
            var row = page.Locator(".gb-arow", new() { Has = page.Locator($".gb-arow-title:text-is('{name}')") }).First;
            Assert.Equal(1, await row.Locator(".gb-logo").CountAsync());
            Assert.Equal(1, await row.Locator(".gb-st").CountAsync());
            Assert.Contains(source, await row.Locator(".gb-src").InnerTextAsync());
        }
        // Gitberth itself and its tunnel are marked System.
        Assert.Contains("System", await page.Locator(".gb-arow", new() { Has = page.Locator(".gb-arow-title:text-is('gitberth')") }).First.InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Search_finds_names_images_and_ports()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        await page.Locator(".gb-arow:not(.gb-arow-head)").First.WaitForAsync(Slow);
        var search = page.Locator("#gb-apps-search");

        await search.FillAsync("postgres:18");
        await page.WaitForTimeoutAsync(600);
        Assert.Contains("postgres", await TitlesAsync(page));

        // Gitberth publishes 8080.
        await search.FillAsync("8080");
        await page.WaitForTimeoutAsync(600);
        Assert.Contains("gitberth", await TitlesAsync(page));

        await search.FillAsync("no-such-app-anywhere");
        await page.WaitForTimeoutAsync(600);
        Assert.Contains("No apps match", await page.Locator(".gb-empty-title").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_status_filters_count_and_narrow()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        await page.Locator(".gb-arow:not(.gb-arow-head)").First.WaitForAsync(Slow);
        var running = page.Locator(".gb-toolbar .gb-seg-btn", new() { HasTextString = "Running" });
        var count = int.Parse((await running.Locator(".n").InnerTextAsync()).Trim());
        await running.ClickAsync();
        await page.WaitForTimeoutAsync(600);
        Assert.Equal(count, (await TitlesAsync(page)).Count);
        Assert.Equal("true", await running.GetAttributeAsync("aria-checked"));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_samples_fold_into_one_group_with_their_stacks()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        var group = page.Locator(".gb-grp", new() { HasTextString = "sample-*" });
        await group.WaitForAsync(Slow);
        Assert.Contains("Name prefix", await group.InnerTextAsync());
        Assert.True(await page.Locator(".gb-sub-grp", new() { HasTextString = "Node.js" }).CountAsync() == 1);

        // Folding it hides its rows.
        var before = (await TitlesAsync(page)).Count(t => t.StartsWith("sample-", StringComparison.Ordinal));
        await group.Locator(".gb-grp-chev").ClickAsync();
        await page.WaitForTimeoutAsync(400);
        Assert.True(before > 0);
        Assert.Equal(0, (await TitlesAsync(page)).Count(t => t.StartsWith("sample-", StringComparison.Ordinal)));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Pinned_obs_and_kanban_are_in_the_menu()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps");
        var pinned = page.Locator(".gb-side nav[aria-label='Pinned apps'] .gb-nav-app");
        await pinned.First.WaitForAsync(Slow);
        var names = (await pinned.AllInnerTextsAsync()).Select(t => t.Trim()).ToList();
        Assert.Contains(names, n => n.StartsWith("orb", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("kanban", StringComparison.Ordinal));
        // The menu counts the running apps.
        Assert.True(int.TryParse(await page.Locator(".gb-side .gb-nav-count").InnerTextAsync(), out var running) && running > 0);
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_list_fits_a_phone_as_cards()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps", width: 390, height: 844);
        await page.Locator(".gb-arow:not(.gb-arow-head)").First.WaitForAsync(Slow);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), "The page scrolls sideways at 390px.");
        // Name, state and address only: the ports and the source wait for the app's page.
        Assert.False(await page.Locator(".gb-arow:not(.gb-arow-head) .gb-c-ports").First.IsVisibleAsync());
        Assert.True(await page.Locator(".gb-arow:not(.gb-arow-head) .gb-c-st").First.IsVisibleAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_top_bar_search_opens_an_app()
    {
        var (page, errors) = await gitberth.OpenAsync("/");
        await page.Locator("#gb-search-input").WaitForAsync(Slow);
        await page.Keyboard.PressAsync("Control+k");
        await page.Keyboard.TypeAsync("postgres");
        var hit = page.Locator(".gb-search-hit").First;
        await hit.WaitForAsync(Slow);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(u => u.Contains("/apps/postgres", StringComparison.Ordinal), new() { Timeout = 30_000 });
        Assert.Empty(errors);
    }
}
