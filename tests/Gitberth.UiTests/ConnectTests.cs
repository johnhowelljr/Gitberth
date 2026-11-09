using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// Connecting to the shared postgres, and the start of publishing it. Read-only: the publish is only opened and
/// cancelled, which changes nothing, and the password is never shown.
/// </summary>
[Collection(GitberthCollection.Name)]
public sealed class ConnectTests(GitberthBrowser gitberth)
{
    private static readonly LocatorWaitForOptions Slow = new() { Timeout = 60_000 };

    [UiFact]
    public async Task Postgres_hands_over_its_internal_address_with_the_password_hidden()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/postgres");
        var connect = page.Locator(".gb-hero-actions .gb-btn", new() { HasTextString = "Connect" });
        await connect.WaitForAsync(Slow);
        await connect.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.WaitForAsync();
        Assert.Contains("Connect to postgres", await dialog.Locator(".gb-dialog-title").InnerTextAsync());

        // Inside Docker, apps reach it by its name, on its own port.
        await dialog.Locator(".mud-tab", new() { HasTextString = "Internal" }).ClickAsync();
        var fields = dialog.Locator("dl.gb-connect");
        await fields.WaitForAsync();
        Assert.Equal("postgres", (await fields.Locator("dt:text-is('Host') + dd code").InnerTextAsync()).Trim());
        Assert.Equal("5432", (await fields.Locator("dt:text-is('Port') + dd code").InnerTextAsync()).Trim());

        // The password shows as dots, in its field and in the URL, until Show; copying it is there all the same.
        Assert.Equal("••••••••", (await fields.Locator("dt:text-is('Password') + dd code").InnerTextAsync()).Trim());
        var url = (await fields.Locator("dt:text-is('URL') + dd code").InnerTextAsync()).Trim();
        Assert.StartsWith("postgres://", url);
        Assert.Contains(":••••@postgres:5432/", url);
        Assert.Equal("false", await fields.Locator("button[aria-pressed]").GetAttributeAsync("aria-pressed"));
        Assert.Equal(1, await fields.Locator("button[aria-label='Copy the password']").CountAsync());

        // Only the separate values and the URL: no other formats to pick from.
        Assert.Equal(0, await dialog.Locator("text=JDBC").CountAsync());
        Assert.Empty(errors);
    }

    [UiFact]
    public async Task Cancelling_publish_changes_nothing()
    {
        var (page, errors) = await gitberth.OpenAsync("/apps/postgres");
        var card = page.Locator(".gb-addresses");
        await card.WaitForAsync(Slow);
        var id = await page.Locator("code.gb-ctr-id").GetAttributeAsync("title");
        Assert.False(string.IsNullOrEmpty(id));

        // Published already: there's no publish to cancel.
        var publish = card.Locator("button", new() { HasTextString = "Publish on LAN" });
        if (await publish.CountAsync() == 0) return;
        await publish.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        try
        {
            await dialog.WaitForAsync(new() { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            // Compose owns it: publishing is a change to its file, on the Network tab, and nothing here changes anything.
            Assert.EndsWith("/containers/postgres/network", page.Url);
            return;
        }
        Assert.Contains("Publish postgres on the LAN", await dialog.Locator(".gb-dialog-title").InnerTextAsync());

        // What would change is worked out on a fresh look at Docker, and nothing changes until it's confirmed.
        await page.WaitForFunctionAsync("() => !(document.querySelector('.mud-dialog .gb-portchange-plan [role=status]')?.textContent ?? '').includes('Working out')",
            null, new() { Timeout = 60_000 });
        if (await dialog.Locator(".gb-change-lines").CountAsync() > 0)
            Assert.Contains("Internal → LAN", await dialog.Locator(".gb-change-lines").InnerTextAsync());
        await dialog.Locator("button", new() { HasTextString = "Cancel" }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await page.ReloadAsync();
        await page.Locator("code.gb-ctr-id").WaitForAsync(Slow);
        Assert.Equal(id, await page.Locator("code.gb-ctr-id").GetAttributeAsync("title"));
        Assert.Contains("Not published", await page.Locator(".gb-addresses dd[data-kind='LAN']").InnerTextAsync());
        Assert.Empty(errors);
    }
}
