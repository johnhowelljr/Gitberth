using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The Network tab, the same for every app whatever its source: "How it's reached" drawn for a Git app, an existing
/// database, a sample, the tunnel on the host's network and Gitberth itself under Compose; the one Ports table with its
/// three-way Access control; the phone layout; and the change dialog's review, cancelled so nothing changes.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class NetworkTabTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    private async Task<IPage> TabAsync(string key, int width = 1440)
    {
        var (page, errors) = await gitberth.OpenAsync($"/apps/{key}/network", width);
        await page.Locator(".gb-stage .gb-fn--ctr").WaitForAsync(Slow);
        Assert.Empty(errors);
        return page;
    }

    // A running sample with a published port: the first one the Apps list shows.
    private async Task<string> SampleAsync()
    {
        var (page, _) = await gitberth.OpenAsync("/apps?q=sample-");
        var link = page.Locator(".gb-arow:not(.gb-arow-head) a.gb-arow-title", new() { HasTextString = "sample-" }).First;
        await link.WaitForAsync(Slow);
        var href = await link.GetAttributeAsync("href") ?? throw new InvalidOperationException("No sample in the list.");
        return href.Split('/')[^1];
    }

    [UiFact]
    public async Task Every_source_gets_the_same_tab_with_its_diagram()
    {
        foreach (var key in new[] { "gb-orb", "postgres", await SampleAsync(), "cloudflare-tunnel", "gitberth" })
        {
            var page = await TabAsync(key);
            Assert.Equal(1, await page.Locator(".gb-stage figure.gb-flow").CountAsync());
            Assert.StartsWith("How ", (await page.Locator(".gb-stage-title").InnerTextAsync()).Trim());
            // The drawing in words, for screen readers, names every path.
            Assert.Contains("From ", await page.Locator(".gb-stage figcaption").TextContentAsync() ?? "");
            // Ports (or the host network's note), domains (not for the tunnel), networks and the own LAN address.
            Assert.Equal(1, await page.Locator("#gb-ports-title").CountAsync());
            Assert.Equal(key == "cloudflare-tunnel" ? 0 : 1, await page.Locator("#gb-domains-title").CountAsync());
            Assert.Equal(1, await page.Locator("#gb-networks-title").CountAsync());
            Assert.Equal(1, await page.Locator("#gb-lanaddr").CountAsync());
        }
    }

    [UiFact]
    public async Task Postgres_unpublished_draws_the_lan_path_off_with_publish_on_lan()
    {
        var page = await TabAsync("postgres");
        var lan = page.Locator(".gb-fl.gb-fl--ghost", new() { HasTextString = "Not published" });
        if (await lan.CountAsync() == 0) return; // published on this host since: nothing to check here
        Assert.Equal(1, await lan.Locator("button", new() { HasTextString = "Publish on LAN" }).CountAsync());
        Assert.Contains("None yet", await page.Locator(".gb-fn--hp").InnerTextAsync());
        Assert.Contains("postgres:5432", await page.Locator(".gb-fl-lab").AllInnerTextsAsync());
    }

    [UiFact]
    public async Task The_tunnel_on_the_hosts_network_has_no_ports_table()
    {
        var page = await TabAsync("cloudflare-tunnel");
        Assert.Contains("Host network: no Docker ports", await page.Locator(".gb-fn--ctr").InnerTextAsync());
        Assert.Equal(0, await page.Locator(".gb-prow:not(.gb-prow--head)").CountAsync());
        Assert.Contains("any port it opens", string.Join(" ", await page.Locator(".gb-fl-lab").AllInnerTextsAsync()));
    }

    [UiFact]
    public async Task Gitberth_under_compose_says_a_change_is_the_edit_to_its_file()
    {
        var page = await TabAsync("gitberth");
        Assert.Contains("Docker Compose manages gitberth", await page.Locator(".gb-stage").InnerTextAsync());
    }

    [UiFact]
    public async Task Each_port_has_the_three_way_access_control()
    {
        var page = await TabAsync(await SampleAsync());
        var rows = page.Locator(".gb-prow:not(.gb-prow--head)");
        await rows.First.WaitForAsync(Slow);
        var access = rows.First.Locator("[role=radiogroup]");
        Assert.StartsWith("Access for ", await access.GetAttributeAsync("aria-label") ?? "");
        Assert.Equal(["Internal", "LAN", "Host only"], (await access.Locator("[role=radio]").AllInnerTextsAsync()).Select(t => t.Trim()).Take(3).ToList());
        Assert.Equal(1, await access.Locator("[role=radio][aria-checked=true]").CountAsync());
        // No exposure warning anywhere: the LAN is trusted.
        Assert.DoesNotContain("expos", (await page.Locator("#gb-ports-title").Locator("xpath=ancestor::section").InnerTextAsync()).Replace("Exposed by the image", ""),
            StringComparison.OrdinalIgnoreCase);
    }

    [UiFact]
    public async Task The_tab_fits_a_phone_and_the_diagram_runs_top_to_bottom()
    {
        var page = await TabAsync("gb-orb", width: 390);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), "The Network tab is wider than a phone.");
        // Narrow, the host port spans the drawing's width, and the container sits under it.
        var hp = await page.Locator(".gb-fn--hp").BoundingBoxAsync();
        var ctr = await page.Locator(".gb-fn--ctr").BoundingBoxAsync();
        Assert.NotNull(hp);
        Assert.NotNull(ctr);
        Assert.True(ctr!.Y > hp!.Y, "The container should be under the host port on a phone.");
    }

    [UiFact]
    public async Task Changing_a_host_port_shows_the_bar_then_the_dialogs_review_and_cancelling_changes_nothing()
    {
        var key = await SampleAsync();
        var page = await TabAsync(key);
        var field = page.Locator(".gb-prow:not(.gb-prow--head) .gb-hpfield input").First;
        if (await field.CountAsync() == 0) return; // no published port to change on this sample
        var before = await field.InputValueAsync();
        await field.FillAsync("39999");
        var bar = page.Locator(".gb-applybar");
        await bar.WaitForAsync(Slow);
        Assert.Contains("1 unsaved change", await bar.InnerTextAsync());
        Assert.DoesNotContain("Save for next deploy", await bar.InnerTextAsync());

        await bar.Locator(".gb-btn--primary").ClickAsync();
        var dialog = page.Locator(".gb-cdlg");
        await dialog.WaitForAsync(Slow);
        var text = await dialog.InnerTextAsync();
        Assert.Contains("WHAT CHANGES", text.ToUpperInvariant());
        Assert.Contains("39999", text);
        Assert.Contains("WHAT'S KEPT", text.ToUpperInvariant());
        Assert.Contains("IMPACT", text.ToUpperInvariant());
        await dialog.Locator("button", new() { HasTextString = "Cancel" }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });

        await bar.Locator("button", new() { HasTextString = "Discard" }).ClickAsync();
        Assert.Equal(before, await page.Locator(".gb-prow:not(.gb-prow--head) .gb-hpfield input").First.InputValueAsync());
    }
}
