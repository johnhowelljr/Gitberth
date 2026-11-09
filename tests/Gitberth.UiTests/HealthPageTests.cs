using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The Health page: a row per container, the busiest first, each opening to its CPU, memory and network charts; short
/// enough to read, and a title given to a container is put back.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class HealthPageTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    private async Task<(IPage Page, List<string> Errors)> OpenAsync(int width = 1440, string path = "/health")
    {
        var (page, errors) = await gitberth.OpenAsync(path, width);
        await page.Locator(".gb-hitem").First.WaitForAsync(Slow);
        return (page, errors);
    }

    // Opens the first row and returns it.
    private static async Task<ILocator> OpenFirstAsync(IPage page)
    {
        var item = page.Locator(".gb-hitem").First;
        await item.Locator(".gb-grp-chev").ClickAsync();
        await item.Locator(".gb-hbody").WaitForAsync(Slow);
        return item;
    }

    [UiFact]
    public async Task A_row_opens_to_cpu_memory_and_network()
    {
        var (page, errors) = await OpenAsync();
        var item = await OpenFirstAsync(page);
        var titles = await item.Locator(".gb-chart-title").AllInnerTextsAsync();
        Assert.True(titles.Take(2).SequenceEqual(["CPU", "Memory"]), string.Join(", ", titles));
        // Network has a chart of its own, or a note for containers on the host's network.
        Assert.True(titles.Count == 3 || await item.Locator(".gb-chart-note").CountAsync() == 1);
        Assert.Equal("true", await item.Locator(".gb-grp-chev").GetAttributeAsync("aria-expanded"));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_page_stays_short()
    {
        var (page, errors) = await OpenAsync();
        // The busiest dozen, the rest behind "Show the other N", the stopped ones folded.
        Assert.True(await page.Locator(".gb-hitem").CountAsync() <= 12);
        var height = await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight");
        Assert.True(height < 3000, $"The Health page is {height}px tall.");
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_charts_draw_and_switch_between_1_hour_and_6_hours()
    {
        var (page, errors) = await OpenAsync();
        var item = await OpenFirstAsync(page);
        // A line needs two readings, which a running container has within 10 seconds.
        await item.Locator(".gb-chart-canvas path.gb-chart-line[d]").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 });
        Assert.Contains("60 min ago", await item.Locator(".gb-chart-x").First.InnerTextAsync());

        await page.Locator(".gb-phead .gb-seg-btn", new() { HasTextString = "6 hours" }).ClickAsync();
        await item.Locator(".gb-chart-x", new() { HasTextString = "6 h ago" }).First.WaitForAsync();
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_row_shows_its_readings_as_a_table()
    {
        var (page, errors) = await OpenAsync();
        var item = await OpenFirstAsync(page);
        await item.Locator(".gb-btn", new() { HasTextString = "As a table" }).ClickAsync();
        var headers = await item.Locator(".gb-mini-table:not(.gb-mini-table--events) th").AllInnerTextsAsync();
        Assert.Equal(["TIME", "CPU", "MEMORY"], headers.Take(3).Select(h => h.Trim().ToUpperInvariant()));

        await item.Locator(".gb-btn", new() { HasTextString = "Charts" }).ClickAsync();
        await item.Locator(".gb-chart-plot").First.WaitForAsync();
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_link_from_an_app_opens_its_charts()
    {
        var (page, errors) = await OpenAsync(path: "/health#hc-title-postgres");
        var item = page.Locator(".gb-hitem", new() { Has = page.Locator("#hc-title-postgres") });
        await item.Locator(".gb-hbody").WaitForAsync(Slow);
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_page_fits_a_phone()
    {
        var (page, errors) = await OpenAsync(width: 390);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth"),
            "The page scrolls sideways at 390px.");
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_container_can_be_given_a_title_and_have_it_put_back()
    {
        var (page, errors) = await OpenAsync();
        // A container that isn't a Gitberth app (renaming an app would rename the app).
        var item = page.Locator(".gb-hitem", new() { Has = page.Locator(".gb-arow-title:not([href^='apps/gb-'])") }).First;
        if (await item.CountAsync() == 0) return;
        var title = item.Locator(".gb-arow-title");
        var id = await title.GetAttributeAsync("id");
        var before = (await title.InnerTextAsync()).Trim();
        await item.Locator(".gb-grp-chev").ClickAsync();

        // The field holds the title it has now, or nothing when it shows its own name: put that back at the end.
        var original = await RenameAsync(page, item, "UI test title");
        Assert.Equal("UI test title", (await page.Locator($"[id='{id}']").InnerTextAsync()).Trim());
        // Under a title, the container's own name stays in view.
        Assert.Contains(id!["hc-title-".Length..], await item.Locator(".gb-arow-sub").InnerTextAsync());

        await RenameAsync(page, item, original);
        Assert.Equal(before, (await page.Locator($"[id='{id}']").InnerTextAsync()).Trim());
        Assert.Empty(errors);
    }

    // Renames through the row's Rename and returns what the field held before.
    private static async Task<string> RenameAsync(IPage page, ILocator item, string to)
    {
        await item.Locator(".gb-btn", new() { HasTextString = "Rename" }).ClickAsync();
        var field = page.Locator(".mud-dialog input").First;
        await field.WaitForAsync();
        var was = await field.InputValueAsync();
        await field.FillAsync(to);
        await field.PressAsync("Enter");
        await page.Locator(".mud-dialog").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.WaitForTimeoutAsync(500);
        return was;
    }
}
