using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The Network page: who holds each host port (a container, whoever made it, or the host; never "(app)"), a port check,
/// and Docker's networks with their members. It only looks.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class NetworkPageTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    private const string PortRows = ".gb-np-list > .gb-np-row:not(.gb-np-row--head)";

    private async Task<(IPage Page, List<string> Errors)> OpenAsync(int width = 1440)
    {
        var (page, errors) = await gitberth.OpenAsync("/network", width);
        // The first look at the host runs the helper container, which takes a moment.
        await page.Locator(PortRows).First.WaitForAsync(Slow);
        return (page, errors);
    }

    // Each line's host port ("3001/tcp") and who holds it, by the name it goes by.
    private static async Task<Dictionary<string, (string Name, string? Link)>> HoldersAsync(IPage page)
    {
        var holders = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var row in await page.Locator(PortRows).AllAsync())
        {
            var port = (await row.Locator(".gb-np-c-port").InnerTextAsync()).Trim();
            var name = (await row.Locator(".gb-np-c-who .gb-np-name").First.InnerTextAsync()).Trim();
            var link = await row.Locator(".gb-np-c-who a.gb-np-who").CountAsync() > 0 ? await row.Locator(".gb-np-c-who a.gb-np-who").GetAttributeAsync("href") : null;
            holders.TryAdd(port, (name, link));
        }
        return holders;
    }

    [UiFact]
    public async Task Lists_who_holds_each_host_port_as_one_kind_of_app()
    {
        var (page, errors) = await OpenAsync();
        var holders = await HoldersAsync(page);

        Assert.Equal("SSH", holders["22/tcp"].Name);
        Assert.Null(holders["22/tcp"].Link);
        Assert.Equal("Docker's API", holders["2375/tcp"].Name);
        Assert.Equal(("orb", "apps/gb-orb"), holders["3000/tcp"]);
        Assert.Equal(("kanban", "apps/gb-kanban"), holders["3001/tcp"]);
        Assert.Equal(("gitberth", "apps/gitberth"), holders["8080/tcp"]);
        // One citizen level: nothing is called an app or a container apart, and every container's line opens its page.
        var text = await page.Locator(".gb-np-list").InnerTextAsync();
        Assert.DoesNotContain("(app)", text);
        Assert.DoesNotContain("(container)", text);
        Assert.All(holders.Values.Where(h => h.Link is not null), h => Assert.StartsWith("apps/", h.Link));
        Assert.Contains("in use", await page.Locator(".gb-np-strip").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Filters_the_containers_from_the_host_without_an_apps_split()
    {
        var (page, errors) = await OpenAsync();
        var filter = page.Locator(".gb-np-card .gb-seg");
        Assert.Equal(["All", "Containers", "The host"], (await filter.Locator(".gb-seg-btn").AllInnerTextsAsync()).Select(t => new string(t.TakeWhile(c => !char.IsDigit(c)).ToArray()).Trim()));

        await filter.Locator(".gb-seg-btn", new() { HasTextString = "The host" }).ClickAsync();
        await page.Locator(PortRows, new() { HasTextString = "SSH" }).WaitForAsync();
        Assert.Equal(0, await page.Locator(PortRows, new() { HasTextString = "kanban" }).CountAsync());

        await filter.Locator(".gb-seg-btn", new() { HasTextString = "Containers" }).ClickAsync();
        await page.Locator(PortRows, new() { HasTextString = "kanban" }).WaitForAsync();
        Assert.Equal(0, await page.Locator(PortRows, new() { HasTextString = "SSH" }).CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Checks_whether_a_port_is_free()
    {
        var (page, errors) = await OpenAsync();
        var field = page.Locator(".gb-np-check input");
        // The answer's live region is there before the first answer, so screen readers announce that one too.
        Assert.Equal(1, await page.Locator(".gb-np-tools [role=status]").CountAsync());

        await field.FillAsync("65001");
        await page.Locator(".gb-np-answer", new() { HasTextString = "65001/tcp is free." }).WaitForAsync();

        await field.FillAsync("8080");
        await page.Locator(".gb-np-answer", new() { HasTextString = "8080/tcp is Gitberth." }).WaitForAsync();

        await field.FillAsync("3001");
        await page.Locator(".gb-np-answer a[href='apps/gb-kanban']").WaitForAsync();

        await field.FillAsync("53/udp");
        await page.Locator(".gb-np-answer", new() { HasTextString = "53/udp is" }).WaitForAsync();

        await field.FillAsync("web");
        await page.Locator(".gb-np-answer", new() { HasTextString = "Type a port between 1 and 65535" }).WaitForAsync();
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_run_of_samples_folds_and_opens_to_each_of_them()
    {
        var (page, errors) = await OpenAsync();
        var fold = page.Locator(".gb-np-row--fold").First;
        await fold.WaitForAsync(Slow);
        Assert.Contains("sample-*", await fold.InnerTextAsync());

        await fold.Locator("button.gb-np-foldbtn").ClickAsync();
        var first = page.Locator(".gb-np-sublist .gb-np-row a.gb-np-who").First;
        await first.WaitForAsync();
        Assert.StartsWith("apps/sample-", await first.GetAttributeAsync("href"));
        Assert.Equal("true", await fold.Locator("button.gb-np-foldbtn").GetAttributeAsync("aria-expanded"));
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Lists_the_docker_networks()
    {
        var (page, errors) = await OpenAsync();
        var gitberthNet = page.Locator(".gb-np-netitem", new() { HasTextString = "172.21.0.0/16" });
        await gitberthNet.WaitForAsync(Slow);
        Assert.Contains("gitberth", await gitberthNet.Locator(".gb-np-n-name").InnerTextAsync());
        Assert.Contains("Shared network", await gitberthNet.InnerTextAsync());

        await gitberthNet.Locator("button.gb-np-net").ClickAsync();
        var postgres = page.Locator(".gb-np-members .gb-np-mrow", new() { HasTextString = "postgres" }).First;
        await postgres.WaitForAsync(Slow);
        Assert.Equal("apps/postgres", await postgres.Locator("a.gb-np-who").GetAttributeAsync("href"));
        Assert.DoesNotContain("(app)", await page.Locator(".gb-np-members").First.InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task The_page_fits_a_phone()
    {
        var (page, errors) = await OpenAsync(width: 390);
        // Two-line cards, not a long column of labels and values.
        Assert.True(await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight") < 3200, "The page is too long at 390px.");

        // A network's members stack on a phone too.
        await page.Locator("button.gb-np-net").First.ClickAsync();
        await page.Locator(".gb-np-members").First.WaitForAsync(Slow);

        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth"),
            "The page scrolls sideways at 390px.");
        Assert.Empty(errors);
    }
}
