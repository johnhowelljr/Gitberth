namespace Gitberth.Credentials;

/// <summary>
/// The settings the token watch keeps. Declared here rather than in SettingKeys, so this folder's keys never land on the
/// same lines as another job's.
/// </summary>
public static class CredentialKeys
{
    /// <summary>
    /// What the watch knows and has told about each token, as JSON by its key (github, gitlab, cloudflare,
    /// registry:&lt;host&gt;, api-token:&lt;id&gt;): its stamp, expiry, the warning told last, a refusal, a dismissal. Never a
    /// token: a stamp is a hash of the stored, encrypted text.
    /// </summary>
    public const string State = "credentials.state";

    /// <summary>When Gitberth's Cloudflare API token expires (ISO 8601), as Cloudflare's verify said; unset when it doesn't, or Cloudflare didn't say.</summary>
    public const string CloudflareTokenExpires = "cloudflare.tokenExpires";
}
