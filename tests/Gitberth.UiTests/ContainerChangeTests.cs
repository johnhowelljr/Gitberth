using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// A UI test that changes a container, so it runs only when asked for with GITBERTH_UI_MUTATE=1, besides what every UI test
/// needs. These tests only touch sample-* containers that Docker Compose doesn't own, and put back what they changed:
/// <code>
/// $env:GITBERTH_UI_MUTATE = "1"; dotnet test tests/Gitberth.UiTests --filter ContainerChangeTests
/// </code>
/// </summary>
public sealed class UiMutateFactAttribute : FactAttribute
{
    public UiMutateFactAttribute()
    {
        if (UiTarget.Url is null || UiTarget.Password is null) Skip = UiTarget.Missing;
        else if (Environment.GetEnvironmentVariable("GITBERTH_UI_MUTATE") != "1") Skip = "Set GITBERTH_UI_MUTATE=1 to run the UI tests that change a sample container.";
    }
}

/// <summary>
/// Changing an app's ports and networks from its Network tab, on the real host: a sample's host port moves and comes back,
/// through the change dialog and the recreate, and a sample joins the shared network and leaves it again. The HTTP check
/// runs from this machine, a PC on the LAN, so it proves the port is reached from the LAN and not only from the host.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed partial class ContainerChangeTests(GitberthBrowser gitberth)
{
    // A recreate checks the new container for 15 s, or until it's healthy, and answers in under a minute.
    private static readonly LocatorWaitForOptions Recreate = new() { Timeout = 180_000 };
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    [UiMutateFact]
    public async Task Publishes_a_sample_on_another_host_port_and_back()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps?q=sample-");
        if (await SampleAsync(page, hostPort: 9101) is not { } name) return;

        var lan = await ChangeHostPortAsync(page, name, from: 9101, to: 9201);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Any answer at all is the sample's: a closed port is refused before HTTP starts.
        Assert.True((await http.GetAsync($"http://{lan}:9201/")).StatusCode > 0);

        // And back where it was.
        await ChangeHostPortAsync(page, name, from: 9201, to: 9101);
        Assert.True((await http.GetAsync($"http://{lan}:9101/")).StatusCode > 0);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://{lan}:9201/"));
        Assert.Empty(errors);
    }

    [UiMutateFact]
    public async Task Joins_a_sample_to_gitberth_and_leaves_again()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps?q=sample-");
        if (await SampleAsync(page, hostPort: null) is not { } name) return;
        var card = page.Locator("section", new() { Has = page.Locator("#gb-networks-title") });
        await card.Locator(".gb-nrow, .gb-empty").First.WaitForAsync(Slow);
        var shared = card.Locator(".gb-nrow", new() { Has = page.Locator(".gb-nrow-name", new() { HasTextRegex = new Regex("^gitberth$") }) });
        // One the shared network has already isn't the sample this test is for.
        if (await shared.CountAsync() > 0) return;

        // Joining asks nothing and restarts nothing.
        await card.Locator("select[aria-label='Join a network']").SelectOptionAsync("gitberth");
        await card.Locator(".gb-njoin button", new() { HasTextString = "Join" }).ClickAsync();
        await shared.WaitForAsync(Slow);
        Assert.Contains("kept", await shared.InnerTextAsync());
        Assert.Contains($"if {name} is recreated", await card.InnerTextAsync());
        // The diagram draws the new path by name.
        await page.Locator(".gb-fl-lab", new() { HasTextString = $"{name}:" }).First.WaitForAsync(Slow);

        // Leaving asks first, and takes it off at once.
        await shared.Locator("button", new() { HasTextString = "Leave" }).ClickAsync();
        await page.Locator(".mud-dialog button", new() { HasTextString = "Leave network" }).ClickAsync();
        await shared.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });

        Assert.Empty(errors);
    }

    // A sample-* app that Docker Compose doesn't own and Gitberth may recreate, with a published host port when asked; its
    // Network tab is left open. Null when there's none (the test then has nothing to do).
    private static async Task<string?> SampleAsync(IPage page, int? hostPort)
    {
        var links = page.Locator(".gb-arow:not(.gb-arow-head) a.gb-arow-title", new() { HasTextString = "sample-" });
        await links.First.WaitForAsync(Slow);
        var keys = new List<string>();
        foreach (var link in await links.AllAsync())
            if (await link.GetAttributeAsync("href") is { } href) keys.Add(Uri.UnescapeDataString(href.Split('/')[^1]));
        foreach (var key in keys)
        {
            await page.GotoAsync($"{UiTarget.Url}/apps/{Uri.EscapeDataString(key)}/network");
            // The recreate verdict comes a moment after the table: the Access control, or the line that says why not.
            await page.Locator("[aria-label^='Access for'], #gb-ports-title ~ * .gb-banner, .gb-card .gb-banner").First.WaitForAsync(Slow);
            var editable = await page.Locator("[role=radiogroup][aria-label^='Access for']").CountAsync() > 0;
            if (!editable) continue;
            if (hostPort is int wanted && !(await page.Locator(".gb-hpfield input").EvaluateAllAsync<int[]>("es => es.map(e => +e.value)")).Contains(wanted)) continue;
            return key;
        }
        return null;
    }

    // The host port typed into the Ports table, through the bar, the change dialog and the recreate; returns the LAN
    // address of the host.
    private static async Task<string> ChangeHostPortAsync(IPage page, string name, int from, int to)
    {
        await page.GotoAsync($"{UiTarget.Url}/apps/{Uri.EscapeDataString(name)}/network");
        var field = page.Locator(".gb-hpfield input").First;
        await field.WaitForAsync(Slow);
        foreach (var input in await page.Locator(".gb-hpfield input").AllAsync())
            if (await input.InputValueAsync() == from.ToString())
            {
                field = input;
                break;
            }
        await field.FillAsync(to.ToString());
        var bar = page.Locator(".gb-applybar");
        await bar.WaitForAsync(Slow);
        await bar.Locator(".gb-btn--primary").ClickAsync();

        var dialog = page.Locator(".gb-cdlg");
        // The change is worked out for the new port: LAN at <host>:9201.
        var change = dialog.Locator(".gb-diff-v", new() { HasTextRegex = new Regex($@":{to}\b") });
        await change.First.WaitForAsync(Slow);
        var lan = HostOf().Match((await change.First.InnerTextAsync()).Trim()).Groups[1].Value;
        Assert.NotEqual("", lan);
        await dialog.Locator(".gb-btn--primary", new() { HasTextString = "Recreate" }).ClickAsync();

        // It runs, then says how it ended.
        var result = dialog.Locator(".gb-result");
        await result.WaitForAsync(Recreate);
        Assert.Equal(1, await dialog.Locator("button", new() { HasTextString = "Done" }).CountAsync());
        await dialog.Locator("button", new() { HasTextString = "Done" }).ClickAsync();
        await page.Locator(".gb-fl-lab", new() { HasTextRegex = new Regex($@":{to}\b") }).First.WaitForAsync(Slow);
        return lan;
    }

    // "LAN at 10.1.1.80:9201" gives 10.1.1.80.
    [GeneratedRegex(@"LAN at ([^\s:]+):\d+")]
    private static partial Regex HostOf();
}
