using System.Text.RegularExpressions;

namespace Gitberth.Git;

/// <summary>Where a Git app's code lives.</summary>
public enum GitHost { GitHub, GitLab }

/// <summary>
/// A repository a Git app deploys from, on GitHub (<see cref="GitHubRepoRef"/>) or GitLab (<see cref="GitLabRepoRef"/>). An
/// app keeps it in its Owner and Repo columns: a GitHub repo as owner and name, a GitLab project as "gitlab:" and its
/// namespace (groups and subgroups) and its path. GitHub never allows a ':' in an owner, so neither form is ever read as
/// the other, and "{Owner}/{Repo}" names one repo on one host wherever Gitberth keys something by it.
/// </summary>
public abstract record RepoRef
{
    /// <summary>What a GitLab project's Owner column starts with.</summary>
    public const string GitLabPrefix = "gitlab:";

    public abstract GitHost Host { get; }

    /// <summary>The app's Owner column for it.</summary>
    public abstract string AppOwner { get; }

    /// <summary>The app's Repo column for it.</summary>
    public abstract string AppRepo { get; }

    /// <summary>owner/repo on GitHub; group/subgroup/project on GitLab.</summary>
    public abstract string FullName { get; }

    /// <summary>The repo's own name (its last part): what a new app is called after.</summary>
    public abstract string Name { get; }

    /// <summary>The branch and folder a pasted link pointed at (/tree/&lt;branch&gt;/&lt;folder&gt;), or null.</summary>
    public abstract string? TreeOf { get; }

    /// <summary>The host's name, as sentences say it.</summary>
    public string HostName => Host == GitHost.GitLab ? "GitLab" : "GitHub";

    /// <summary>Whether an app's Owner column is a GitLab project's.</summary>
    public static bool IsGitLab(string? owner) => owner?.StartsWith(GitLabPrefix, StringComparison.Ordinal) == true;

    /// <summary>The host of an app's repo, from its Owner column.</summary>
    public static GitHost HostOf(string? owner) => IsGitLab(owner) ? GitHost.GitLab : GitHost.GitHub;

    /// <summary>The repo an app's Owner and Repo columns name.</summary>
    public static RepoRef Of(string owner, string repo) =>
        IsGitLab(owner) ? new GitLabRepoRef(owner[GitLabPrefix.Length..], repo) : new GitHubRepoRef(owner, repo);

    /// <summary>owner/repo, or group/subgroup/project for a GitLab app: as people read it.</summary>
    public static string FullNameOf(string owner, string repo) => IsGitLab(owner) ? $"{owner[GitLabPrefix.Length..]}/{repo}" : $"{owner}/{repo}";
}

/// <summary>
/// A project on Gitberth's GitLab (gitlab.com, or the self-hosted one in Settings › GitLab), by its namespace (a user or a
/// group, with subgroups: group/subgroup) and its path. The instance isn't part of it: it's Settings', so a project's URLs
/// are made with the instance's address (<see cref="CloneUrl"/>, <see cref="WebUrl"/>).
/// </summary>
public sealed partial record GitLabRepoRef(string Namespace, string Project, string? TreePath = null) : RepoRef
{
    /// <summary>The address Gitberth's GitLab has when none is set: gitlab.com.</summary>
    public const string DefaultInstance = "https://gitlab.com";

    public override GitHost Host => GitHost.GitLab;
    public override string AppOwner => GitLabPrefix + Namespace;
    public override string AppRepo => Project;
    public override string FullName => PathWithNamespace;
    public override string Name => Project;
    public override string? TreeOf => TreePath;

    /// <summary>group/subgroup/project, as GitLab's API names it (path_with_namespace).</summary>
    public string PathWithNamespace => $"{Namespace}/{Project}";

    /// <summary>The project as the API's :id: its path_with_namespace, URL-encoded (group%2Fsub%2Fproject).</summary>
    public string ApiId => Uri.EscapeDataString(PathWithNamespace);

    /// <summary>Where git clones it from on <paramref name="instance"/>.</summary>
    public string CloneUrl(string instance) => $"{instance.TrimEnd('/')}/{PathWithNamespace}.git";

    /// <summary>Its page on <paramref name="instance"/>.</summary>
    public string WebUrl(string instance) => $"{instance.TrimEnd('/')}/{PathWithNamespace}";

    /// <summary>
    /// The instance's address as Gitberth keeps it: scheme, host, port and any path GitLab is served under, without a
    /// trailing slash (https://gitlab.example.com, http://10.1.1.5:8929/gitlab). Null, with why, for anything else.
    /// </summary>
    public static string? NormalizeInstance(string? input, out string? error)
    {
        error = null;
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return DefaultInstance;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            error = "Give GitLab's address, like https://gitlab.com or https://gitlab.example.com.";
            return null;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Give GitLab's address alone, without a user, a query or a #.";
            return null;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}{path}";
    }

    /// <summary>
    /// A GitLab project parsed from what was pasted: https://&lt;instance&gt;/group/sub/project, with .git, with a page of
    /// it after /-/ (/-/tree/&lt;branch&gt;/&lt;folder&gt; keeps the branch and folder), without a scheme, or as
    /// git@host:group/project.git or ssh://git@host[:port]/group/project.git. Only projects on <paramref name="instance"/>
    /// (Settings › GitLab) are taken.
    /// </summary>
    public static bool TryParse(string? input, string instance, out GitLabRepoRef? project, out string? error)
    {
        project = null;
        error = null;
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
        {
            error = "Paste a GitLab project URL.";
            return false;
        }
        if (!Uri.TryCreate(instance, UriKind.Absolute, out var root))
        {
            error = "GitLab's address in Settings › GitLab isn't a URL.";
            return false;
        }
        var prefix = root.AbsolutePath.Trim('/');

        string path;
        var ssh = SshUrl().Match(s);
        if (ssh.Success)
        {
            if (!string.Equals(ssh.Groups["host"].Value, root.Host, StringComparison.OrdinalIgnoreCase))
            {
                error = $"That's on {ssh.Groups["host"].Value}, and Gitberth's GitLab is {root.Host} (Settings › GitLab).";
                return false;
            }
            path = ssh.Groups["path"].Value;
        }
        else
        {
            // Without a scheme (gitlab.example.com/group/project), it's the instance's, port and all.
            var bare = !s.Contains("://", StringComparison.Ordinal);
            if (bare) s = root.Scheme + "://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                error = "That doesn't look like a URL.";
                return false;
            }
            var samePort = uri.Port == root.Port || bare && uri.IsDefaultPort || uri.IsDefaultPort && root.IsDefaultPort;
            if (!string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase) || !samePort)
            {
                error = $"That's on {uri.Authority}, and Gitberth's GitLab is {root.Authority} (Settings › GitLab).";
                return false;
            }
            path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"GitLab is at {instance.TrimEnd('/')}: the project's URL starts with that.";
                    return false;
                }
                path = path[(prefix.Length + 1)..];
            }
        }

        // A page of the project: everything from /-/ on is GitLab's, not the project's path.
        string? tree = null;
        var dash = path.IndexOf("/-/", StringComparison.Ordinal);
        if (dash >= 0)
        {
            var page = path[(dash + 3)..];
            path = path[..dash];
            if (page.StartsWith("tree/", StringComparison.Ordinal) && page.Length > "tree/".Length) tree = page["tree/".Length..].Trim('/');
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            error = "The URL needs a group or user and a project, e.g. https://gitlab.com/you/your-app";
            return false;
        }
        var name = segments[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var ns = segments[..^1];
        if (ns.Length > 20 || ns.Append(name).Any(p => !PathPart().IsMatch(p) || p is "." or ".." || p.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".atom", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"'{string.Join('/', ns.Append(name))}' isn't a valid GitLab project path.";
            return false;
        }
        project = new GitLabRepoRef(string.Join('/', ns), name, string.IsNullOrEmpty(tree) ? null : tree);
        return true;
    }

    [GeneratedRegex(@"^(?:ssh://)?git@(?<host>[^:/\s]+)(?::\d+/|:|/)(?<path>[^\s]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SshUrl();

    // A group's or project's path: letters, digits, '_', '-' and '.', starting with a letter, digit or '_'.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,254}$")]
    private static partial Regex PathPart();
}

/// <summary>Reads a repository URL as GitHub's or GitLab's.</summary>
public static class RepoUrls
{
    /// <summary>
    /// The repository a pasted URL names: a github.com repository (<see cref="GitHubRepoRef.TryParse"/>), else a project on
    /// Gitberth's GitLab at <paramref name="gitLabInstance"/> (<see cref="GitLabRepoRef.TryParse"/>).
    /// </summary>
    public static bool TryParse(string? input, string gitLabInstance, out RepoRef? repo, out string? error)
    {
        repo = null;
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
        {
            error = "Paste a GitHub repository or GitLab project URL.";
            return false;
        }
        if (IsGitHub(s))
        {
            var ok = GitHubRepoRef.TryParse(s, out var github, out error);
            repo = github;
            return ok;
        }
        if (GitLabRepoRef.TryParse(s, gitLabInstance, out var gitlab, out var gitLabError))
        {
            (repo, error) = (gitlab, null);
            return true;
        }
        // Not GitHub's, and not on Gitberth's GitLab: say what is taken, unless GitLab's own words say more.
        var host = Uri.TryCreate(gitLabInstance, UriKind.Absolute, out var root) ? root.Authority : gitLabInstance;
        error = gitLabError is not null && (gitLabError.StartsWith("That's on", StringComparison.Ordinal) || gitLabError.StartsWith("That doesn't", StringComparison.Ordinal))
            ? $"Gitberth deploys from github.com and from its GitLab, {host}. For another GitLab, set its address in Settings › GitLab."
            : gitLabError;
        return false;
    }

    // github.com, www.github.com or git@github.com, with or without a scheme.
    private static bool IsGitHub(string s)
    {
        var t = s.ToLowerInvariant();
        if (t.StartsWith("git@github.com", StringComparison.Ordinal) || t.StartsWith("ssh://git@github.com", StringComparison.Ordinal)) return true;
        var withScheme = t.Contains("://", StringComparison.Ordinal) ? t : "https://" + t;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Host is "github.com" or "www.github.com";
    }
}
