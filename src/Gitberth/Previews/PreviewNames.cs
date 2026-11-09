using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Services;

namespace Gitberth.Previews;

/// <summary>
/// What a preview is called: its slug (and so its container, image repository, volumes and network name), its name in
/// Gitberth, and its domain. Pure, so every rule is tested on its own.
/// </summary>
public static class PreviewNames
{
    /// <summary>The longest slug any app has (AppService.Slugify): container names, volume names and labels stay short.</summary>
    public const int MaxSlug = 40;

    /// <summary>The longest DNS label.</summary>
    public const int MaxLabel = 63;

    /// <summary>
    /// The preview's slug: &lt;parent&gt;-pr-&lt;n&gt; for a pull request, &lt;parent&gt;-&lt;branch&gt; for a branch, lower case,
    /// letters, digits and '-' only (never '_': a deploy's temporary containers end in _next and _prev). Longer than
    /// <see cref="MaxSlug"/>, it's cut and a short hash of what it previews keeps it apart from the others.
    /// </summary>
    /// <param name="salt">0 for the usual slug; another number gives another hash, for when that one is taken.</param>
    public static string Slug(string parentSlug, PreviewKind kind, string branch, int? pullRequest, int salt = 0)
    {
        var what = kind == PreviewKind.PullRequest ? $"pr-{pullRequest?.ToString(CultureInfo.InvariantCulture)}" : BranchSlug(branch);
        var full = $"{parentSlug}-{what}";
        if (full.Length <= MaxSlug && salt == 0) return full;
        var hash = Hash(Identity(kind, branch, pullRequest) + (salt == 0 ? "" : "#" + salt.ToString(CultureInfo.InvariantCulture)));
        var room = MaxSlug - hash.Length - 1;
        return $"{(full.Length > room ? full[..room] : full).TrimEnd('-')}-{hash}";
    }

    /// <summary>A branch as part of a name: feature/Login_Page is feature-login-page; one with no letter or digit is "branch".</summary>
    public static string BranchSlug(string branch) => AppService.Slugify(branch) is { Length: > 0 } slug ? slug : "branch";

    /// <summary>"orb · PR #12", or "orb · feature/login".</summary>
    public static string Name(string parentName, PreviewKind kind, string branch, int? pullRequest) => $"{parentName} · {Label(kind, branch, pullRequest)}";

    /// <summary>How the preview is called in sentences and lists: "PR #12", or the branch.</summary>
    public static string Label(PreviewKind kind, string branch, int? pullRequest) =>
        kind == PreviewKind.PullRequest ? $"PR #{pullRequest?.ToString(CultureInfo.InvariantCulture)}" : branch;

    /// <summary>What a deploy of it fetches: refs/pull/&lt;n&gt;/head, or refs/heads/&lt;branch&gt;.</summary>
    public static string Ref(PreviewKind kind, string branch, int? pullRequest) =>
        kind == PreviewKind.PullRequest ? $"refs/pull/{pullRequest?.ToString(CultureInfo.InvariantCulture)}/head" : $"refs/heads/{branch}";

    /// <summary>
    /// The preview's domain, from its parent's main one: the first label with -pr-&lt;n&gt; or -git-&lt;branch&gt; added, the rest
    /// as it is (app.example.com gives app-pr-12.example.com), so it's one level below the zone and Cloudflare's free
    /// certificate covers it. A main domain at a zone's apex (example.com) gives &lt;parent&gt;-pr-12.example.com. A label
    /// longer than <see cref="MaxLabel"/> is cut and given a short hash. Null when no usable name comes out.
    /// </summary>
    public static string? Hostname(string mainDomain, string parentSlug, PreviewKind kind, string branch, int? pullRequest)
    {
        var main = mainDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var labels = main.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0)) return null;
        var what = kind == PreviewKind.PullRequest ? $"-pr-{pullRequest?.ToString(CultureInfo.InvariantCulture)}" : $"-git-{BranchSlug(branch)}";
        var apex = labels.Length == 2 || labels.Length == 3 && SecondLevel.Contains($"{labels[1]}.{labels[2]}");
        var (first, rest) = apex ? (parentSlug + what, main) : (labels[0] + what, string.Join('.', labels.Skip(1)));
        if (first.Length > MaxLabel)
        {
            var hash = Hash(Identity(kind, branch, pullRequest));
            first = $"{first[..(MaxLabel - hash.Length - 1)].TrimEnd('-')}-{hash}";
        }
        var hostname = $"{first}.{rest}";
        return DomainNames.Problem(hostname) is null ? hostname : null;
    }

    // Public suffixes of two labels that are common enough to matter: example.co.uk is a zone's apex, not a subdomain of co.uk.
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "me.uk", "ac.uk", "com.au", "net.au", "org.au", "co.nz", "org.nz", "co.jp", "com.br", "co.za", "com.mx", "co.in", "com.sg",
        "com.tr", "co.kr", "com.cn", "com.tw", "com.hk",
    };

    // What the preview is of, the same however it's named.
    private static string Identity(PreviewKind kind, string branch, int? pullRequest) =>
        kind == PreviewKind.PullRequest ? $"pr/{pullRequest?.ToString(CultureInfo.InvariantCulture)}" : "branch/" + branch;

    /// <summary>Four hex characters of the text's SHA-256.</summary>
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..4].ToLowerInvariant();
}
