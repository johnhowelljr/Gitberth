using Microsoft.Playwright;

namespace Gitberth.UiTests;

/// <summary>
/// The running Gitberth these tests drive, from environment variables. They need one with Docker behind it, so they're
/// skipped unless GITBERTH_UI_URL and GITBERTH_UI_PASSWORD (the admin password) are set:
/// <code>
/// $env:GITBERTH_UI_URL = "http://192.168.1.50:8080"; $env:GITBERTH_UI_PASSWORD = "…"; dotnet test tests/Gitberth.UiTests
/// </code>
/// GITBERTH_UI_BROWSER picks an installed browser (msedge or chrome). Without it Playwright's own Chromium runs, which
/// needs installing once: <c>pwsh tests/Gitberth.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium</c>.
/// The tests only look, apart from renaming a container, which they put back.
/// </summary>
public static class UiTarget
{
    public static string? Url => Get("GITBERTH_UI_URL")?.TrimEnd('/');

    public static string? Password => Get("GITBERTH_UI_PASSWORD");

    public static string? Browser => Get("GITBERTH_UI_BROWSER");

    public const string Missing = "Set GITBERTH_UI_URL and GITBERTH_UI_PASSWORD to run the UI tests against a running Gitberth.";

    private static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;
}

/// <summary>A test that drives a running Gitberth in a browser, skipped when there's none to drive.</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (UiTarget.Url is null || UiTarget.Password is null) Skip = UiTarget.Missing;
    }
}

/// <summary>One browser for all the tests, signed in once.</summary>
public sealed class GitberthBrowser : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _signedIn;

    public async Task InitializeAsync()
    {
        if (UiTarget.Url is not { } url || UiTarget.Password is not { } password) return;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = UiTarget.Browser, Headless = true });
        var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{url}/login");
        await page.FillAsync("#username", "admin");
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync(url + "/");
        _signedIn = await context.StorageStateAsync();
        await context.CloseAsync();
    }

    /// <summary>A signed-in page at <paramref name="path"/>, collecting the browser's errors as it goes.</summary>
    public async Task<(IPage Page, List<string> Errors)> OpenAsync(string path, int width = 1440, int height = 1000)
    {
        var context = await _browser!.NewContextAsync(new()
        {
            StorageState = _signedIn,
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add("console: " + m.Text); };
        page.PageError += (_, e) => errors.Add("page: " + e);
        await page.GotoAsync(UiTarget.Url + path);
        return (page, errors);
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class GitberthCollection : ICollectionFixture<GitberthBrowser>
{
    public const string Name = "A running Gitberth";
}
