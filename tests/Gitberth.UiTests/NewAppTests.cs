using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// New app: the four sources, a pasted docker run read back with what isn't carried over, the local images, and a
/// registry image's Configure step with "What will run". Nothing is created: the tests stop before Create.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class NewAppTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    [UiFact]
    public async Task Offers_four_sources_as_cards()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new");
        var cards = page.Locator(".gb-srccard");
        await cards.First.WaitForAsync(Slow);
        Assert.Equal(["GitHub repository", "Registry image", "Local image", "By hand"], (await page.Locator(".gb-srccard-t").AllInnerTextsAsync()).Select(t => t.Trim()).ToList());
        Assert.Equal(1, await page.Locator(".gb-srccard[aria-checked=true]").CountAsync());
        Assert.Equal(["Source", "Configure", "Create"], (await page.Locator(".gb-stp-t").AllInnerTextsAsync()).Select(t => t.Trim()).ToList());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_pasted_docker_run_is_read_back_with_what_isnt_carried_over()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new?from=paste");
        var paste = page.Locator("textarea.gb-paste");
        await paste.WaitForAsync(Slow);
        await paste.FillAsync("docker run -d --rm --name whoami -p 8088:80 -e TZ=Europe/Paris traefik/whoami");

        var read = page.Locator(".gb-read");
        await read.WaitForAsync(Slow);
        var text = await read.InnerTextAsync();
        Assert.Contains("whoami", text);
        Assert.Contains("traefik/whoami", text);
        Assert.Contains("8088:80", text);
        // --rm isn't carried over, and says why; nothing blocks.
        var dropped = page.Locator(".gb-issue-group", new() { HasTextString = "Not carried over" });
        Assert.Contains("--rm", await dropped.InnerTextAsync());
        Assert.Equal(0, await page.Locator(".gb-issue-group[role=alert]").CountAsync());

        // Continue prefills the Configure step: the port on the LAN at 8088.
        await page.Locator(".gb-btn--primary", new() { HasTextString = "Continue" }).ClickAsync();
        await page.Locator("#cfg-name").WaitForAsync(Slow);
        Assert.Equal("whoami", await page.Locator("#cfg-name").InputValueAsync());
        Assert.Contains("-p 8088:80", await page.Locator(".gb-preview .gb-code pre").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_pasted_network_that_doesnt_exist_is_flagged_with_create_and_remove()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new?from=paste");
        var paste = page.Locator("textarea.gb-paste");
        await paste.WaitForAsync(Slow);
        await paste.FillAsync("docker run -d --name t --network gbtest-ui-nosuchnet -p 8089:80 nginx:alpine");
        await page.Locator(".gb-btn--primary", new() { HasTextString = "Continue" }).ClickAsync();

        // Configure lists it, flagged, with the two ways out; Remove takes it out of the draft.
        var missing = page.Locator(".gb-netpick-row--missing", new() { HasTextString = "gbtest-ui-nosuchnet" });
        await missing.WaitForAsync(Slow);
        Assert.Contains("Doesn't exist", await missing.InnerTextAsync());
        Assert.Equal(1, await missing.Locator(".gb-btn", new() { HasTextString = "Create it" }).CountAsync());
        await missing.Locator(".gb-btn", new() { HasTextString = "Remove" }).ClickAsync();
        await missing.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        Assert.DoesNotContain("gbtest-ui-nosuchnet", await page.Locator(".gb-preview .gb-code pre").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_host_network_paste_is_shown_on_the_hosts_network()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new?from=paste");
        var paste = page.Locator("textarea.gb-paste");
        await paste.WaitForAsync(Slow);
        await paste.FillAsync("docker run --network host --privileged -v /srv/ha:/config ghcr.io/home-assistant/home-assistant:stable");
        await page.Locator(".gb-read").WaitForAsync(Slow);
        Assert.Contains("The host's own", await page.Locator(".gb-read").InnerTextAsync());

        await page.Locator(".gb-btn--primary", new() { HasTextString = "Continue" }).ClickAsync();
        await page.Locator("#cfg-name").WaitForAsync(Slow);
        // The mode is chosen in Configure, the networks list gives way to why, and the docker run says --network host.
        Assert.Equal("true", await page.Locator("#cfg-networks .gb-seg-btn", new() { HasTextString = "The host's" }).GetAttributeAsync("aria-checked"));
        Assert.Equal(0, await page.Locator("#cfg-networks .gb-netpick").CountAsync());
        Assert.Contains("--network host", await page.Locator(".gb-preview .gb-code pre").InnerTextAsync());
        // Advanced opens by itself: the paste set Privileged.
        Assert.Equal(1, await page.Locator("#cfg-advanced [aria-label='Privileged'][aria-checked=true]").CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task An_unknown_option_blocks_until_its_fixed()
    {
        var (page, _) = await gitberth.OpenAsync("/apps/new?from=paste");
        var paste = page.Locator("textarea.gb-paste");
        await paste.WaitForAsync(Slow);
        await paste.FillAsync("docker run --frobnicate nginx");
        await page.Locator(".gb-issue-group[role=alert]").WaitForAsync(Slow);
        Assert.Contains("--frobnicate", await page.Locator(".gb-issue-group[role=alert]").InnerTextAsync());
        Assert.True(await page.Locator(".gb-btn--primary", new() { HasTextString = "Continue" }).IsDisabledAsync());
    }

    [UiFact]
    public async Task Local_images_list_with_their_size_and_use()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new");
        await page.Locator(".gb-srccard", new() { HasTextString = "Local image" }).ClickAsync();
        await page.Locator(".gb-seg-btn", new() { HasTextString = "All" }).ClickAsync();
        var rows = page.Locator(".gb-resrow");
        await rows.First.WaitForAsync(Slow);
        Assert.Matches(@"(MB|GB|kB) · built or pulled", await rows.First.Locator(".gb-resrow-s").InnerTextAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task A_popular_image_goes_to_configure_with_what_will_run()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/new");
        await page.Locator(".gb-tile", new() { HasTextString = "redis" }).ClickAsync();
        await page.Locator(".gb-resrow--on").WaitForAsync(Slow);
        await page.Locator(".gb-btn--primary", new() { HasTextString = "Continue" }).ClickAsync();
        await page.Locator(".gb-preview").WaitForAsync(Slow);
        Assert.Contains("docker run", await page.Locator(".gb-preview .gb-code").InnerTextAsync());
        // New apps are on the LAN by default, and the diagram shows how it'll be reached.
        Assert.Equal(1, await page.Locator(".gb-preview .gb-stage").CountAsync());
        Assert.Contains("gitberth", await page.Locator(".gb-preview .gb-code pre").InnerTextAsync());
        Assert.Empty(errors);
    }
}
