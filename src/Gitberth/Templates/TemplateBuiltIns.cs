using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Templates;

/// <summary>
/// Where an app installed from a template is reached, as each of its deploys tells it (<see cref="TemplateCatalog.BuiltIns"/>:
/// APP_URL, APP_HOST, APP_SCHEME, APP_HTTPS): its main domain's https:// address, else its web port's LAN address. So a
/// domain added or taken off later, or a new host port, reaches the app with one deploy without a build (the app is marked
/// for it). A variable of the same name on the app's Environment tab wins.
/// </summary>
public sealed class TemplateBuiltIns(DomainService domains, AppService apps)
{
    /// <summary>The built-ins for an address, in order.</summary>
    public static IReadOnlyList<(string Key, string Value)> Values(TemplateAddress address) =>
    [
        ("APP_URL", address.Url),
        ("APP_HOST", address.Host),
        ("APP_SCHEME", address.Https ? "https" : "http"),
        ("APP_HTTPS", address.Https ? "true" : "false"),
    ];

    /// <summary>
    /// This host's LAN address as Gitberth knows it without a browser: the one saved in Settings, else GITBERTH_PUBLIC_HOST,
    /// else the one detected on the host's default route (as the template page's own is, while it's known).
    /// </summary>
    public Task<string> LanHostAsync() => apps.GetPublicHostAsync("http://localhost/");

    /// <summary>The app's address now: its main domain, else its web port's LAN address. <paramref name="app"/> needs its Ports.</summary>
    public async Task<TemplateAddress> AddressAsync(AppDefinition app, CancellationToken ct = default) =>
        TemplateAddress.For(await domains.MainHostnameAsync(app.Id, ct), await LanHostAsync(), PortRules.WebOf(app.Ports)?.HostPort);

    /// <summary>What a deploy of the app sets: the built-ins for an app installed from a template, nothing for any other.</summary>
    public async Task<IReadOnlyList<(string Key, string Value)>> ForAsync(AppDefinition app, CancellationToken ct = default) =>
        app.TemplateId is null ? [] : Values(await AddressAsync(app, ct));
}
