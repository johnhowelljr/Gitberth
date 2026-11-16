using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Gitberth.Audit;

/// <summary>
/// Who acts through a browser's page: when a page's circuit opens, the signed-in user and the browser's address (from the
/// connection the circuit came in on), and around everything the browser then sends the circuit (an event, a navigation,
/// a call from its JavaScript) that person as the audit log's actor, so whatever a click changes is recorded as theirs.
/// </summary>
public sealed class AuditCircuitHandler(AuthenticationStateProvider auth, IHttpContextAccessor accessor) : CircuitHandler
{
    // Read while the circuit is being made, inside the request of the connection it came in on: the only moment a circuit
    // has its HttpContext.
    private string? _ip = ClientIp(accessor.HttpContext);
    private string? _user;

    /// <summary>The person acting through this circuit; null while nobody's signed in.</summary>
    public AuditActor? Actor => _user is { } user ? AuditActor.User(user, _ip) : null;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _ip ??= ClientIp(accessor.HttpContext);
        _user = await UserAsync();
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            // The sign-in is checked again every minute (StampRevalidator), and reading it is cheap.
            _user = await UserAsync() ?? _user;
            if (Actor is not { } actor)
            {
                await next(context);
                return;
            }
            using (AuditScope.Begin(actor))
                await next(context);
        };

    private async Task<string?> UserAsync()
    {
        try
        {
            var state = await auth.GetAuthenticationStateAsync();
            return state.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;
        }
        catch (InvalidOperationException)
        {
            // Asked before the circuit's sign-in was set.
            return null;
        }
    }

    /// <summary>
    /// The client's address for a request. Gitberth's own pages are only ever reached on the LAN, so it's the connection's;
    /// with <paramref name="throughTunnel"/> (the webhook and deploy-hook paths, which the tunnel carries) Cloudflare's
    /// CF-Connecting-IP comes first, as those endpoints read their sender.
    /// </summary>
    public static string? ClientIp(HttpContext? context, bool throughTunnel = false)
    {
        if (context is null) return null;
        if (throughTunnel && context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } forwarded) return forwarded.Trim();
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return null;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
