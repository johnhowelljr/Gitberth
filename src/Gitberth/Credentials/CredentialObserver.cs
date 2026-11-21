using System.Net;
using Gitberth.Services;

namespace Gitberth.Credentials;

/// <summary>
/// Sits on the github and gitlab HTTP clients (CredentialSetup) and reports what each answer said of the token its request
/// carried: a 401 is a refusal, a 2xx an acceptance (with GitHub's expiry header, which comes on every answer to a token
/// that expires). Anything else (a 403, a 404, the rate limit, a 5xx, no answer) says nothing about the token and is left
/// alone. Only the token's SHA-256 leaves this handler, and the call goes on exactly as it would without it: the answer,
/// its body and any exception are untouched, and nothing here can throw into it.
/// </summary>
/// <param name="kind">Whose token the client carries: github or gitlab.</param>
public sealed class CredentialObserver(string kind, CredentialSignals signals) : DelegatingHandler
{
    /// <summary>The header GitHub says a token's expiry in ("2026-12-01 00:00:00 UTC").</summary>
    internal const string GitHubExpiryHeader = "github-authentication-token-expiration";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var hash = HashOf(request);
        var response = await base.SendAsync(request, cancellationToken);
        if (hash is not null) Observe(request, response, hash);
        return response;
    }

    // The token the request carries: GitHub's as Authorization (Bearer), GitLab's as PRIVATE-TOKEN. A request without one
    // (a public repo's read) says nothing about any token.
    private static string? HashOf(HttpRequestMessage request)
    {
        try
        {
            if (request.Headers.Authorization is { Parameter: { Length: > 0 } bearer }) return CredentialSignals.Hash(bearer);
            return request.Headers.TryGetValues("PRIVATE-TOKEN", out var values) && values.FirstOrDefault() is { Length: > 0 } token
                ? CredentialSignals.Hash(token)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Observe(HttpRequestMessage request, HttpResponseMessage response, string hash)
    {
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var words = response.Headers.TryGetValues("WWW-Authenticate", out var challenges) ? CredentialText.GitLabWords(challenges) : null;
                signals.Report(new CredentialAnswer(kind, hash, CredentialVerdict.Refused, CredentialText.ApiDoing(kind, request.Method, request.RequestUri), Words: words));
            }
            else if (response.IsSuccessStatusCode)
            {
                var expires = response.Headers.TryGetValues(GitHubExpiryHeader, out var values) ? GitHubApi.ParseExpiry(values.FirstOrDefault()) : null;
                signals.Report(new CredentialAnswer(kind, hash, CredentialVerdict.Accepted, ExpiresAt: expires));
            }
        }
        catch (Exception)
        {
            // Never into the call it watches: a report that failed is one answer the watch didn't hear.
        }
    }
}
