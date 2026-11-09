using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Gitberth.Tests;

/// <summary>
/// Every page needs signing in, wherever its file is: only [Authorize] on the page itself keeps an anonymous visitor out,
/// and a page outside Components/Pages doesn't get that folder's. New app (Components/NewApp) once opened without it.
/// </summary>
public class PageAuthorizationTests
{
    // The pages a visitor may open without signing in, on purpose.
    private static readonly HashSet<string> Anonymous = ["Login", "Error", "NotFound"];

    [Fact]
    public void Every_page_needs_signing_in_except_the_ones_meant_for_everyone()
    {
        var pages = typeof(Program).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .ToList();
        Assert.NotEmpty(pages);

        foreach (var page in pages)
        {
            var open = page.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length > 0;
            var locked = page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Length > 0;
            if (Anonymous.Contains(page.Name))
                Assert.True(open, $"{page.Name} is meant to open without signing in, but has no [AllowAnonymous].");
            else
                Assert.True(locked && !open, $"{page.FullName} ({string.Join(", ", page.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(r => r.Template))}) opens without signing in: give it [Authorize].");
        }
    }
}
