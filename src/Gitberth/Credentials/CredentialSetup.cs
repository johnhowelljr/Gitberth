using Gitberth.Services;
using Microsoft.Extensions.Http;

namespace Gitberth.Credentials;

/// <summary>The tokens Gitberth uses: Program.cs's one line for this folder.</summary>
public static class CredentialSetup
{
    /// <summary>
    /// The watch on the tokens and sign-ins Gitberth uses (GitHub, GitLab, Cloudflare, registries, its own API tokens in
    /// use): warnings before one expires and a clear word the moment one is refused, with its background service and what
    /// it hears from the GitHub and GitLab clients. Program.cs calls it after every service it uses is registered.
    /// </summary>
    public static IServiceCollection AddCredentialWatch(this IServiceCollection services)
    {
        // Git, Cloudflare's API and the update checks take the signals as an optional constructor parameter.
        services.AddSingleton<CredentialSignals>();
        services.AddSingleton<CredentialWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<CredentialWatch>());
        // The observer joins the clients Program.cs made, without touching their lines: every GitHub and GitLab call reports
        // what its answer said of its token.
        foreach (var (client, kind) in new[] { ("github", CredentialWatch.GitHubKey), (GitLabApi.ClientName, CredentialWatch.GitLabKey) })
            services.Configure<HttpClientFactoryOptions>(client, o => o.HttpMessageHandlerBuilderActions.Add(b =>
                b.AdditionalHandlers.Add(new CredentialObserver(kind, b.Services.GetRequiredService<CredentialSignals>()))));
        return services;
    }
}
