using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The one App page, the same for every source: the hero, the six tabs, the redirects from the old pages, the settings
/// editor with its apply bar, and the dashboard. It only looks: an edit is discarded, and the change dialog is cancelled.
/// (The Network tab's own tests are D2's.)
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class AppPageTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    private static readonly string[] Tabs = ["Overview", "Environment", "Network", "Settings", "Logs", "Activity"];

    // A sample that runs: the first Node.js one the list shows.
    private async Task<string> SampleAsync()
    {
        var (page, _) = await gitberth.OpenAsync("/apps?q=sample-node");
        var link = page.Locator(".gb-arow:not(.gb-arow-head) a.gb-arow-title").First;
        await link.WaitForAsync(Slow);
        var href = (await link.GetAttributeAsync("href"))!;
        await page.Context.CloseAsync();
        return Uri.UnescapeDataString(href.TrimStart('/')["apps/".Length..]);
    }

    [UiFact]
    public async Task The_dashboard_has_its_figures_and_the_pinned_apps()
    {
        var (page, errors) = await gitberth.OpenAsync("/");
        await page.Locator(".gb-kpis2 .gb-hs").First.WaitForAsync(Slow);
        var figures = (await page.Locator(".gb-kpis2 .gb-hs-k").AllInnerTextsAsync()).Select(t => t.Trim()).ToList();
        Assert.Equal(["Apps", "Needs attention", "Deploys", "Updates"], figures);
        var pinned = page.Locator("section", new() { Has = page.Locator(".gb-card-title", new() { HasTextString = "Pinned" }) });
        await pinned.Locator(".gb-arow").First.WaitForAsync(Slow);
        var names = (await pinned.Locator(".gb-arow-title").AllInnerTextsAsync()).Select(t => t.Trim()).ToList();
        Assert.Contains("kanban", names);
        Assert.Contains("orb", names);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_dashboard_fits_a_phone()
    {
        var (page, errors) = await gitberth.OpenAsync("/", width: 390, height: 844);
        await page.Locator(".gb-kpis2 .gb-hs").First.WaitForAsync(Slow);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), "The dashboard scrolls sideways at 390px.");
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Every_kind_of_app_has_the_same_six_tabs_and_each_opens()
    {
        foreach (var key in new[] { "gb-orb", "postgres", await SampleAsync(), "cloudflare-tunnel" })
        {
            var (page, errors) = await gitberth.OpenAsync($"/apps/{key}");
            await page.Locator(".gb-hero h1").WaitForAsync(Slow);
            var tabs = (await page.Locator(".gb-tabbar .gb-tabbar-item").AllInnerTextsAsync()).Select(t => t.Trim().Split('\n')[0].Trim()).ToList();
            Assert.Equal(Tabs, tabs);
            foreach (var tab in Tabs.Skip(1))
            {
                await page.Locator(".gb-tabbar .gb-tabbar-item", new() { HasTextString = tab }).ClickAsync();
                await page.WaitForURLAsync(u => u.EndsWith("/" + tab.ToLowerInvariant(), StringComparison.Ordinal), new() { Timeout = 30_000 });
                Assert.Equal("page", await page.Locator(".gb-tabbar .gb-tabbar-item.on").GetAttributeAsync("aria-current"));
            }
            Assert.Empty(errors);
            await page.Context.CloseAsync();
        }
    }

    [UiFact]
    public async Task The_hero_is_the_same_for_every_source()
    {
        foreach (var (key, source) in new[] { ("gb-kanban", "Git"), ("postgres", "Existing") })
        {
            var (page, errors) = await gitberth.OpenAsync($"/apps/{key}");
            var hero = page.Locator(".gb-hero");
            await hero.WaitForAsync(Slow);
            Assert.Equal(1, await hero.Locator(".gb-logo--xl").CountAsync());
            Assert.Equal(1, await hero.Locator(".gb-st--hero").CountAsync());
            Assert.Contains(source, await hero.Locator(".gb-src").InnerTextAsync());
            Assert.Equal(6, await hero.Locator(".gb-hero-stat").CountAsync());
            // One primary action: Open for a web app, Connect for a database.
            Assert.Equal(1, await hero.Locator(".gb-hero-actions .gb-btn--primary").CountAsync());
            Assert.Empty(errors);
            await page.Context.CloseAsync();
        }
        var (pg, _) = await gitberth.OpenAsync("/apps/postgres");
        await pg.Locator(".gb-hero").WaitForAsync(Slow);
        Assert.Contains("Connect", await pg.Locator(".gb-hero-actions .gb-btn--primary").InnerTextAsync());
    }

    [UiFact]
    public async Task The_old_pages_redirect()
    {
        foreach (var (from, to) in new[] { ("/containers", "/apps"), ("/containers/postgres", "/apps/postgres"), ("/containers/postgres/network", "/apps/postgres/network") })
        {
            var (page, errors) = await gitberth.OpenAsync(from);
            await page.WaitForURLAsync(u => new Uri(u).AbsolutePath == to, new() { Timeout = 30_000 });
            Assert.Empty(errors);
            await page.Context.CloseAsync();
        }
        // A Git app's old numeric link goes to its container's name, and the old tab names to the new ones.
        var (app, appErrors) = await gitberth.OpenAsync("/apps/1/deployments");
        await app.WaitForURLAsync(u => u.Contains("/apps/gb-", StringComparison.Ordinal) && u.EndsWith("/activity", StringComparison.Ordinal), new() { Timeout = 30_000 });
        Assert.Empty(appErrors);
    }

    [UiFact]
    public async Task A_samples_settings_are_editable_and_the_apply_bar_says_what_applying_does()
    {
        var key = await SampleAsync();
        var (page, errors) = await gitberth.OpenAsync($"/apps/{key}/settings");
        var memory = page.Locator("input[aria-label='Memory limit']");
        await memory.WaitForAsync(Slow);
        Assert.True(await page.Locator("#set-cmd").IsEditableAsync());
        Assert.True(await memory.IsEditableAsync());

        await memory.FillAsync("512");
        var bar = page.Locator(".gb-applybar:not(.gb-card-foot)");
        await bar.WaitForAsync(Slow);
        Assert.Contains("1 unsaved change", await bar.Locator(".gb-applybar-title").InnerTextAsync());
        // The consequence line comes from the plan.
        await page.WaitForFunctionAsync("() => (document.querySelector('.gb-applybar-sub')?.innerText ?? '').length > 5", null, new() { Timeout = 30_000 });
        Assert.Contains("Changed", await page.Locator("#sec-resources").InnerTextAsync());
        Assert.Equal(1, await page.Locator(".gb-toc a .gb-dot").CountAsync());

        // Review and apply opens the change dialog with its lines; cancelling changes nothing.
        await bar.Locator(".gb-btn--primary").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.WaitForAsync(Slow);
        Assert.Contains("Memory", await dialog.InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
        await page.Keyboard.PressAsync("Escape");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });

        await bar.Locator("button", new() { HasTextString = "Discard" }).ClickAsync();
        await bar.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        Assert.Equal("", await memory.InputValueAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Gitberths_own_settings_are_read_only_with_the_compose_note()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/gitberth/settings");
        var note = page.Locator(".gb-secs > .gb-ro-note");
        await note.WaitForAsync(Slow);
        Assert.Contains("Compose", await note.InnerTextAsync());
        Assert.False(await page.Locator("#set-cmd").IsEditableAsync());
        Assert.False(await page.Locator("#set-image").IsEditableAsync());
        // It can't be removed from here.
        Assert.Equal(0, await page.Locator("#sec-remove").CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Long_variable_names_never_overlap_their_values()
    {
        foreach (var width in new[] { 1440, 390 })
        {
            var (page, errors) = await gitberth.OpenAsync("/apps/gb-kanban/environment", width);
            await page.Locator(".gb-envrow:not(.gb-envrow--head)").First.WaitForAsync(Slow);
            var overlaps = await page.EvaluateAsync<int>("""
                () => [...document.querySelectorAll('.gb-envrow')].filter(r => {
                  const k = r.querySelector('.gb-envkey'), v = r.querySelector('.gb-envval');
                  if (!k || !v) return false;
                  const a = k.getBoundingClientRect(), b = v.getBoundingClientRect();
                  return a.right > b.left + 1 && a.bottom > b.top + 1 && b.bottom > a.top + 1;
                }).length
                """);
            Assert.Equal(0, overlaps);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), $"The Environment tab scrolls sideways at {width}px.");
            Assert.Empty(errors);
            await page.Context.CloseAsync();
        }
    }

    [UiFact]
    public async Task A_variable_has_a_secret_switch_and_a_url_password_is_always_secret()
    {
        var key = await SampleAsync();
        var (page, errors) = await gitberth.OpenAsync($"/apps/{key}/environment");
        await page.Locator(".gb-envtbl").WaitForAsync(Slow);
        await page.Locator("button", new() { HasTextString = "Add variable" }).ClickAsync();
        var row = page.Locator(".gb-envrow--new").Last;
        await row.Locator("input[aria-label='Variable name']").FillAsync("GBTEST_PLAIN");
        await row.Locator("input[aria-label='Value of GBTEST_PLAIN']").FillAsync("hello");

        // A plain value: the switch is off, and turning it on masks it and marks it Secret.
        var toggle = row.Locator("button.gb-secretbtn");
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));
        await toggle.ClickAsync();
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-pressed"));
        Assert.Equal("password", await row.Locator("input[aria-label='Value of GBTEST_PLAIN']").GetAttributeAsync("type"));

        // A URL with a password in it is secret whatever the switch says: it's on, and can't be turned off.
        await toggle.ClickAsync();
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));
        await row.Locator("input[aria-label='Value of GBTEST_PLAIN']").FillAsync("postgres://app:pw@db/app");
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-pressed"));
        Assert.True(await toggle.IsDisabledAsync());

        var bar = page.Locator(".gb-applybar:not(.gb-card-foot)");
        await bar.Locator("button", new() { HasTextString = "Discard" }).ClickAsync();
        await bar.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_tabs_fit_a_phone_without_scrolling()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/postgres", width: 390, height: 844);
        var bar = page.Locator(".gb-tabbar");
        await bar.WaitForAsync(Slow);
        Assert.True(await bar.EvaluateAsync<bool>("b => b.scrollWidth <= b.clientWidth + 1"), "The tab bar scrolls at 390px.");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_deploy_page_links_back_to_the_app_by_its_name()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/gb-kanban/activity");
        var log = page.Locator("a[href*='/deployments/']").First;
        await log.WaitForAsync(Slow);
        await log.ClickAsync();
        await page.Locator(".gb-phead").WaitForAsync(Slow);
        Assert.Equal("apps/gb-kanban", (await page.Locator(".gb-crumbs a").Nth(1).GetAttributeAsync("href"))?.TrimStart('/'));
        Assert.Empty(errors);
    }
}
