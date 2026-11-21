using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Credentials;

/// <summary>What a service said of a token Gitberth sent it.</summary>
public enum CredentialVerdict { Accepted, Refused }

/// <summary>
/// One answer a service gave to a call that carried a token: GitHub's or GitLab's API (through their HTTP clients),
/// Cloudflare's, git, or a registry an update check asked.
/// </summary>
/// <param name="Kind">Whose token: github, gitlab, cloudflare, or registry:&lt;host&gt;.</param>
/// <param name="TokenHash">
/// The SHA-256 of the token sent (<see cref="CredentialSignals.Hash"/>), so an answer to another token (a new one being
/// checked before it's saved, an old one still in flight) is told apart; null for a registry's, which only ever sends the
/// saved sign-in.
/// </param>
/// <param name="Verdict">Taken, or refused (a 401, or the service's own word for a token it doesn't take).</param>
/// <param name="Doing">What Gitberth was doing, as a sentence goes on: "while Gitberth was cloning acme/web".</param>
/// <param name="ExpiresAt">When the token expires, when the answer said so (GitHub's header).</param>
/// <param name="Words">The service's own word on why it refused, as a sentence: "GitLab says it was revoked."</param>
public sealed record CredentialAnswer(string Kind, string? TokenHash, CredentialVerdict Verdict, string? Doing = null, DateTimeOffset? ExpiresAt = null,
    string? Words = null);

/// <summary>
/// Where the calls Gitberth makes anyway report what each service said of the token they carried, for the token watch
/// (<see cref="CredentialWatch"/>). Reporting never throws and never waits, so a deploy's clone is neither slowed nor
/// broken by it.
/// </summary>
public sealed class CredentialSignals(ILogger<CredentialSignals>? logger = null)
{
    /// <summary>Raised on the reporting call's own thread: a listener only notes the answer and returns.</summary>
    public event Action<CredentialAnswer>? Answered;

    public void Report(CredentialAnswer answer)
    {
        foreach (var handler in Answered?.GetInvocationList() ?? [])
        {
            try { ((Action<CredentialAnswer>)handler)(answer); }
            catch (Exception ex) { logger?.LogDebug(ex, "A listener to what {Kind} said of its token failed", answer.Kind); }
        }
    }

    /// <summary>A token's SHA-256, as lower-case hex: what an answer carries and the watch compares, never the token itself.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// A saved token's stamp: the first 16 hex digits of the SHA-256 of what's stored (the encrypted text). It changes
    /// whenever the token is saved, even the same token again (the encryption differs each time), and says nothing about it.
    /// </summary>
    public static string Stamp(string stored) => Hash(stored)[..16];
}
