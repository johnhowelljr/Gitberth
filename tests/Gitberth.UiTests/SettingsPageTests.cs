using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The Settings page: its sections and their index, the LAN address of this host, the shared network, own LAN addresses
/// (the Block field's line never has a gap), and the new Registries and Updates sections. It only looks.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class SettingsPageTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    [UiFact]
    public async Task Has_every_section_with_its_index()
    {
        var (page, errors) = await gitberth.OpenAsync("/settings");
        await page.Locator("#system").WaitForAsync(Slow);
        Assert.Equal(["GitHub access", "Registries", "Updates", "Domains", "Network", "Own LAN addresses", "Deploys", "Admin password", "System"],
            (await page.Locator(".gb-toc a").AllInnerTextsAsync()).Select(t => t.Trim()).ToList());
        foreach (var id in new[] { "github", "registries", "updates", "domains", "network", "own-lan", "deploys", "password", "system" })
            Assert.Equal(1, await page.Locator($"section#{id}").CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Shows_the_detected_lan_address_and_the_shared_network()
    {
        var (page, errors) = await gitberth.OpenAsync("/settings");
        var network = page.Locator("section#network");
        await network.Locator("code", new() { HasTextString = "gitberth" }).WaitForAsync(Slow);
        Assert.Equal("LAN address of this host", (await network.Locator("label[for=lan-host]").InnerTextAsync()).Trim());
        Assert.Matches(@"Detected: \d+\.\d+\.\d+\.\d+ on \S+\. Every LAN address", (await network.Locator(".gb-help").First.InnerTextAsync()).Trim());
        Assert.Matches(@"bridge · \d+\.\d+\.\d+\.\d+/\d+ · (.+ · )?\d+ containers?", await network.InnerTextAsync());
        Assert.Equal(1, await network.Locator("a[href='network']").CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Own_lan_addresses_are_off_and_the_block_line_has_no_gap()
    {
        var (page, errors) = await gitberth.OpenAsync("/settings");
        var own = page.Locator("section#own-lan");
        await own.Locator("#lan-block, .gb-kv").First.WaitForAsync(Slow);
        var text = await own.InnerTextAsync();
        Assert.Contains("Docker can't use your router's DHCP, so automatic addresses come from a block you keep out of the router's DHCP range.", text);
        // The old line read "like , or a range" when the host's network couldn't be read.
        Assert.DoesNotContain("like ,", text);
        if (await own.Locator("#lan-block").CountAsync() > 0)
        {
            Assert.Equal("Off", (await own.Locator(".gb-h3 .gb-tag").InnerTextAsync()).Trim());
            Assert.Equal(1, await own.Locator("button", new() { HasTextString = "Turn on" }).CountAsync());
        }
        Assert.DoesNotContain("ipvlan", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("macvlan", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Registries_never_show_a_secret_and_updates_have_their_window()
    {
        var (page, errors) = await gitberth.OpenAsync("/settings");
        var registries = page.Locator("section#registries");
        await registries.Locator("#reg-host").WaitForAsync(Slow);
        Assert.Equal("password", await registries.Locator("#reg-secret").GetAttributeAsync("type"));
        Assert.Equal("", await registries.Locator("#reg-secret").InputValueAsync());
        Assert.True(await registries.Locator("button", new() { HasTextString = "Verify and save" }).IsDisabledAsync());

        var updates = page.Locator("section#updates");
        Assert.InRange(int.Parse(await updates.Locator("#upd-hours").InputValueAsync()), 1, 168);
        Assert.Equal(2, await updates.Locator("input[type=time]").CountAsync());
        // The app's own words for the three policies.
        Assert.Equal(["When I ask", "Tell me", "Automatically"], (await updates.Locator("[role=radio]").AllInnerTextsAsync()).Select(t => t.Trim()).ToList());
        Assert.Empty(errors);
    }
}
