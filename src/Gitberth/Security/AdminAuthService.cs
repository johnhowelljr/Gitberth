using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Gitberth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Gitberth.Security;

/// <summary>
/// Single admin account. The first password comes from GITBERTH_ADMIN_PASSWORD (or is generated and logged).
/// <para>
/// Each sign-in's cookie carries the password's stamp, which changes with the password: a cookie with an older stamp is
/// refused on its next request (<see cref="ValidateCookieAsync"/>), so a password change signs every other browser out.
/// The browser that changed it gets a new cookie through a one-time renewal (<see cref="RedeemRenewal"/>). The stamp is
/// read from the database once and then kept, so checking a request never touches SQLite.
/// </para>
/// </summary>
public sealed class AdminAuthService(SettingsService settings, GitberthOptions options, LoginThrottle throttle, ILogger<AdminAuthService> logger)
{
    public const string UserName = "admin";

    /// <summary>The claim a sign-in's cookie carries the password's stamp in.</summary>
    public const string StampClaim = "gitberth.stamp";

    /// <summary>How long the browser that changed the password has to pick up its new cookie.</summary>
    internal static readonly TimeSpan RenewalLifetime = TimeSpan.FromMinutes(2);

    // The current stamp, once read: "" until the password is first changed, which every sign-in from before stamps had has too.
    private volatile string? _stamp;
    // One-time renewals after a password change: the stamp each is for, and until when it counts.
    private readonly ConcurrentDictionary<string, (string Stamp, DateTime Until)> _renewals = new(StringComparer.Ordinal);

    /// <summary>Stands in for the clock in tests.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    public async Task EnsureAdminPasswordAsync()
    {
        if (await settings.GetAsync(SettingKeys.AdminPasswordHash) is not null) return;
        var password = options.InitialAdminPassword;
        if (password is null)
        {
            password = AdminPassword.Generate();
            logger.LogWarning("No GITBERTH_ADMIN_PASSWORD was set. Sign in as '{User}' with this generated password: {Password} (change it under Settings).", UserName, password);
        }
        await settings.SetAsync(SettingKeys.AdminPasswordHash, AdminPassword.Hash(password));
    }

    public async Task<(bool Ok, string? Error)> ValidateAsync(string? user, string? password, string client)
    {
        if (throttle.IsLocked(client)) return (false, "Too many failed attempts. Try again in a few minutes.");
        var hash = await settings.GetAsync(SettingKeys.AdminPasswordHash);
        var ok = hash is not null
            && string.Equals(user?.Trim(), UserName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(password)
            && AdminPassword.Verify(password, hash);
        if (!ok)
        {
            throttle.Fail(client);
            return (false, "Wrong username or password.");
        }
        throttle.Succeed(client);
        return (true, null);
    }

    /// <summary>
    /// Changes the password and its stamp, which signs every browser out on its next request. The result's renewal signs
    /// the browser that changed it back in (<see cref="RedeemRenewal"/>), once and within <see cref="RenewalLifetime"/>.
    /// </summary>
    public async Task<PasswordChange> ChangePasswordAsync(string current, string next)
    {
        var hash = await settings.GetAsync(SettingKeys.AdminPasswordHash);
        if (hash is null || !AdminPassword.Verify(current, hash)) return new PasswordChange("The current password is wrong.", null);
        if (next.Length < 10) return new PasswordChange("Use at least 10 characters.", null);
        await settings.SetAsync(SettingKeys.AdminPasswordHash, AdminPassword.Hash(next));
        var stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await settings.SetAsync(SettingKeys.AdminStamp, stamp);
        _stamp = stamp;
        var now = Now();
        foreach (var (key, (_, until)) in _renewals)
            if (until <= now) _renewals.TryRemove(key, out _);
        var renewal = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _renewals[renewal] = (stamp, now + RenewalLifetime);
        logger.LogInformation("The admin password was changed: every other signed-in browser is signed out.");
        return new PasswordChange(null, renewal);
    }

    /// <summary>The password's current stamp: read once, then kept. "" until the password is first changed.</summary>
    public async ValueTask<string> StampAsync(CancellationToken ct = default) =>
        _stamp ??= await settings.GetAsync(SettingKeys.AdminStamp, ct) ?? "";

    /// <summary>A sign-in as the cookie keeps it: the admin, with the password's stamp it was made under.</summary>
    public static ClaimsPrincipal Principal(string stamp) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName), new Claim(StampClaim, stamp)], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>A new sign-in under the current stamp, as the sign-in page makes it.</summary>
    public async Task<ClaimsPrincipal> PrincipalAsync(CancellationToken ct = default) => Principal(await StampAsync(ct));

    /// <summary>Whether a sign-in was made under the password as it is now. One from before stamps counts until the first change.</summary>
    public async ValueTask<bool> IsCurrentAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        user.Identity?.IsAuthenticated == true && (user.FindFirst(StampClaim)?.Value ?? "") == await StampAsync(ct);

    /// <summary>
    /// The sign-in a renewal from <see cref="ChangePasswordAsync"/> stands for, once: null when it's unknown, used or too
    /// old. It carries the stamp of the change that made it, so a later change still signs it out.
    /// </summary>
    public ClaimsPrincipal? RedeemRenewal(string? renewal) =>
        renewal is not null && _renewals.TryRemove(renewal, out var held) && held.Until > Now() ? Principal(held.Stamp) : null;

    /// <summary>
    /// The cookie's check on every request: a sign-in made under an older password is refused, and its cookie removed, so
    /// that browser lands on the sign-in page.
    /// </summary>
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<AdminAuthService>();
        if (context.Principal is { } user && await auth.IsCurrentAsync(user, context.HttpContext.RequestAborted)) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

/// <param name="Error">Why the password wasn't changed; null when it was.</param>
/// <param name="Renewal">Signs the browser that changed it back in (<see cref="AdminAuthService.RedeemRenewal"/>); null when it wasn't changed.</param>
public sealed record PasswordChange(string? Error, string? Renewal);
