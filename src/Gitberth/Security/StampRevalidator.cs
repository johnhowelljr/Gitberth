using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Gitberth.Security;

/// <summary>
/// An open page's sign-in, checked against the admin password's stamp every minute (the cookie is only checked on a
/// request, and an open page makes none). Once the password changed in another browser, the page goes to the sign-in
/// page. It reads the kept stamp (<see cref="AdminAuthService.StampAsync"/>), never the database.
/// </summary>
public sealed class StampRevalidator(ILoggerFactory loggers, AdminAuthService auth) : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        state.User.Identity?.IsAuthenticated != true || await auth.IsCurrentAsync(state.User, cancellationToken);
}
