using System.Text.RegularExpressions;

namespace Gitberth.Git;

/// <summary>A GitHub repository parsed from whatever URL form the user pasted.</summary>
public sealed partial record GitHubRepoRef(string Owner, string Repo, string? TreePath = null)
{
    public string CloneUrl => $"https://github.com/{Owner}/{Repo}.git";
    public string WebUrl => $"https://github.com/{Owner}/{Repo}";
    public string FullName => $"{Owner}/{Repo}";

    /// <summary>
    /// Accepts https://github.com/owner/repo, a ".git" suffix, /tree/&lt;branch&gt;/&lt;subdir&gt;,
    /// github.com/owner/repo without a scheme, and git@github.com:owner/repo.git.
    /// </summary>
    public static bool TryParse(string? input, out GitHubRepoRef? repo, out string? error)
    {
        repo = null;
        error = null;
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
        {
            error = "Paste a GitHub repository URL.";
            return false;
        }

        var ssh = SshUrl().Match(s);
        if (ssh.Success) return Build(ssh.Groups["owner"].Value, ssh.Groups["repo"].Value, null, out repo, out error);

        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "That doesn't look like a URL.";
            return false;
        }
        if (uri.Host.ToLowerInvariant() is not ("github.com" or "www.github.com"))
        {
            error = "Only github.com repositories are supported right now.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2)
        {
            error = "The URL needs an owner and a repository, e.g. https://github.com/you/your-app";
            return false;
        }
        var tree = segments.Length > 3 && segments[2] == "tree" ? string.Join('/', segments.Skip(3)) : null;
        return Build(segments[0], segments[1], tree, out repo, out error);
    }

    /// <summary>Splits a /tree/... path into branch + subfolder using the real branch list (branch names can contain '/').</summary>
    public static (string Branch, string? Subdirectory)? ResolveTreePath(string treePath, IEnumerable<string> branches)
    {
        string? best = null;
        foreach (var b in branches)
        {
            if ((treePath == b || treePath.StartsWith(b + "/", StringComparison.Ordinal)) && (best is null || b.Length > best.Length))
                best = b;
        }
        if (best is null) return null;
        var rest = treePath.Length > best.Length ? treePath[(best.Length + 1)..].Trim('/') : "";
        return (best, rest.Length == 0 ? null : rest);
    }

    private static bool Build(string owner, string name, string? tree, out GitHubRepoRef? repo, out string? error)
    {
        repo = null;
        error = null;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!OwnerName().IsMatch(owner) || !RepoName().IsMatch(name) || name is "." or "..")
        {
            error = $"'{owner}/{name}' isn't a valid GitHub repository.";
            return false;
        }
        repo = new GitHubRepoRef(owner, name, string.IsNullOrEmpty(tree) ? null : tree);
        return true;
    }

    [GeneratedRegex(@"^(?:ssh://)?git@github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SshUrl();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")]
    private static partial Regex OwnerName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();
}
