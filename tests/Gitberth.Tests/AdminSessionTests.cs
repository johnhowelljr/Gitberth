using System.Security.Claims;
using Gitberth.Data;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Changing the admin password signs every other browser out: each sign-in carries the password's stamp, a cookie with an
/// older one is refused on its next request, and the browser that changed it is signed back in once, at once. The stamp
/// is read from the database once and then kept.
/// </summary>
public sealed class AdminSessionTests : IDisposable
{
    private const string First = "first-password-1234";
    private const string Second = "second-password-5678";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SettingsService _settings;
    private DateTime _now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    public AdminSessionTests()
    {
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
    }

    public void Dispose() => _connection.Dispose();

    // A new one is Gitberth after a restart: it reads the stamp the last one saved.
    private async Task<AdminAuthService> AuthAsync()
    {
        var auth = new AdminAuthService(_settings, new GitberthOptions { DataDir = Path.GetTempPath(), InitialAdminPassword = First }, new LoginThrottle(),
            NullLogger<AdminAuthService>.Instance) { Now = () => _now };
        await auth.EnsureAdminPasswordAsync();
        return auth;
    }

    // A cookie made before sign-ins carried a stamp.
    private static ClaimsPrincipal Unstamped() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, AdminAuthService.UserName)], CookieAuthenticationDefaults.AuthenticationScheme));

    [Fact]
    public async Task Changing_the_password_signs_every_other_browser_out_and_this_one_back_in()
    {
        var auth = await AuthAsync();
        var phone = await auth.PrincipalAsync();
        // Sign-ins from before stamps go on counting until the password first changes, so an upgrade signs no one out.
        Assert.True(await auth.IsCurrentAsync(Unstamped()));
        Assert.True(await auth.IsCurrentAsync(phone));

        var change = await auth.ChangePasswordAsync(First, Second);

        Assert.Null(change.Error);
        Assert.False(await auth.IsCurrentAsync(phone));
        Assert.False(await auth.IsCurrentAsync(Unstamped()));
        // The browser that changed it gets one new sign-in, once.
        var laptop = auth.RedeemRenewal(change.Renewal);
        Assert.NotNull(laptop);
        Assert.True(await auth.IsCurrentAsync(laptop));
        Assert.Null(auth.RedeemRenewal(change.Renewal));
        Assert.Null(auth.RedeemRenewal("not-a-renewal"));
        Assert.Null(auth.RedeemRenewal(null));
        // A new sign-in is under the new password; after a restart the old ones still don't count.
        Assert.True(await auth.IsCurrentAsync(await auth.PrincipalAsync()));
        var restarted = await AuthAsync();
        Assert.False(await restarted.IsCurrentAsync(phone));
        Assert.True(await restarted.IsCurrentAsync(laptop));
        Assert.True((await restarted.ValidateAsync("admin", Second, "client")).Ok);
    }

    [Fact]
    public async Task A_refused_change_signs_no_one_out()
    {
        var auth = await AuthAsync();
        var phone = await auth.PrincipalAsync();

        var wrong = await auth.ChangePasswordAsync("wrong-password-0000", Second);
        Assert.Equal(("The current password is wrong.", (string?)null), (wrong.Error, wrong.Renewal));
        Assert.Equal("Use at least 10 characters.", (await auth.ChangePasswordAsync(First, "short")).Error);

        Assert.True(await auth.IsCurrentAsync(phone));
        Assert.Null(await _settings.GetAsync(SettingKeys.AdminStamp));
    }

    [Fact]
    public async Task A_renewal_lasts_two_minutes_and_never_outlives_a_later_change()
    {
        var auth = await AuthAsync();
        var late = await auth.ChangePasswordAsync(First, Second);
        _now += AdminAuthService.RenewalLifetime + TimeSpan.FromSeconds(1);
        Assert.Null(auth.RedeemRenewal(late.Renewal));

        // Changed again before the first renewal was used: that one stands for the password it was made for.
        var first = await auth.ChangePasswordAsync(Second, First);
        var second = await auth.ChangePasswordAsync(First, Second);
        Assert.False(await auth.IsCurrentAsync(auth.RedeemRenewal(first.Renewal)!));
        Assert.True(await auth.IsCurrentAsync(auth.RedeemRenewal(second.Renewal)!));
    }

    [Fact]
    public async Task The_stamp_is_read_from_the_database_once()
    {
        var auth = await AuthAsync();
        await auth.ChangePasswordAsync(First, Second);
        var stamp = await auth.StampAsync();

        // Every request checks it, so it's never read again: a value written behind its back isn't seen.
        await _settings.SetAsync(SettingKeys.AdminStamp, "written-elsewhere");

        Assert.Equal(stamp, await auth.StampAsync());
        Assert.Equal(32, stamp.Length);
    }

    [Fact]
    public async Task The_cookie_check_refuses_an_old_sign_in_and_removes_its_cookie()
    {
        var auth = await AuthAsync();
        var phone = await auth.PrincipalAsync();
        var services = new ServiceCollection().AddLogging().AddSingleton(auth);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => o.Cookie.Name = "gitberth.auth");
        await using var provider = services.BuildServiceProvider();

        async Task<(ClaimsPrincipal? Principal, string SetCookie)> CheckAsync(ClaimsPrincipal user)
        {
            var http = new DefaultHttpContext { RequestServices = provider };
            var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
            var context = new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(),
                new AuthenticationTicket(user, CookieAuthenticationDefaults.AuthenticationScheme));
            await AdminAuthService.ValidateCookieAsync(context);
            return (context.Principal, http.Response.Headers.SetCookie.ToString());
        }

        var before = await CheckAsync(phone);
        Assert.Same(phone, before.Principal);
        Assert.Equal("", before.SetCookie);

        var change = await auth.ChangePasswordAsync(First, Second);
        var after = await CheckAsync(phone);

        Assert.Null(after.Principal);
        Assert.StartsWith("gitberth.auth=;", after.SetCookie);
        var renewed = auth.RedeemRenewal(change.Renewal)!;
        Assert.Same(renewed, (await CheckAsync(renewed)).Principal);
    }
}
