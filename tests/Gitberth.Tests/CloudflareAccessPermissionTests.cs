using System.Net;
using System.Text;
using Gitberth.Domains;

namespace Gitberth.Tests;

/// <summary>
/// Protecting domains needs two of Cloudflare's Access permission groups: "Apps and Policies" for the Access applications,
/// and "Organizations, Identity Providers, and Groups" to read the team domain visitors sign in at. A token with only the
/// first can never protect a domain, so Settings, the docs and every refusal name both.
/// </summary>
public sealed class CloudflareAccessPermissionTests
{
    private sealed class Cloudflare(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Refused() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("""{"success":false,"errors":[{"code":10000,"message":"Authentication error"}],"messages":[],"result":null}""", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void What_protecting_domains_needs_names_both_access_groups()
    {
        Assert.Equal("Account › Access: Apps and Policies › Edit and Account › Access: Organizations, Identity Providers, and Groups › Read", CloudflareApi.AccessPermission);
        Assert.Contains(CloudflareApi.AccessAppsPermission, CloudflareApi.AccessPermission);
        Assert.Contains(CloudflareApi.AccessOrganizationPermission, CloudflareApi.AccessPermission);
    }

    [Fact]
    public async Task A_token_that_cant_read_the_team_domain_is_told_both_permissions()
    {
        var cloudflare = new Cloudflare(_ => Refused());
        var api = new CloudflareApi(new Factory(cloudflare));

        var refused = await Assert.ThrowsAsync<CloudflareApiException>(() => api.GetAccessTeamDomainAsync("token", "account"));

        Assert.Equal(["GET /client/v4/accounts/account/access/organizations"], cloudflare.Asked);
        Assert.Equal("Cloudflare didn't let Gitberth's token read the account's Cloudflare Access settings. The token needs "
            + CloudflareApi.Permissions + ", plus " + CloudflareApi.AccessPermission + " to protect domains.", refused.Message);
        Assert.Contains("Organizations, Identity Providers, and Groups › Read", refused.Message);
        Assert.False(refused.Transient);
    }

    [Fact]
    public async Task A_token_that_cant_manage_access_applications_is_told_both_too()
    {
        var api = new CloudflareApi(new Factory(new Cloudflare(_ => Refused())));

        var refused = await Assert.ThrowsAsync<CloudflareApiException>(() => api.ListAccessAppsAsync("token", "account"));

        Assert.Contains(CloudflareApi.AccessAppsPermission, refused.Message);
        Assert.Contains(CloudflareApi.AccessOrganizationPermission, refused.Message);
    }

    [Fact]
    public async Task Zero_trust_not_set_up_is_no_team_domain_rather_than_an_error()
    {
        var api = new CloudflareApi(new Factory(new Cloudflare(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"success":false,"errors":[{"code":7003,"message":"Could not route"}],"messages":[],"result":null}""", Encoding.UTF8, "application/json"),
        })));

        Assert.Null(await api.GetAccessTeamDomainAsync("token", "account"));
    }
}
