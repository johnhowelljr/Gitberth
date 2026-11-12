using System.Globalization;
using System.Text;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Git;

public sealed class GitException(string message) : Exception(message);

public sealed record RemoteBranch(string Name, string Sha);

/// <summary>A pull request's head as the repo advertises it (refs/pull/&lt;n&gt;/head): GitHub keeps it after the pull request closes.</summary>
public sealed record RemotePull(int Number, string Sha);

/// <param name="Pulls">The pull requests' heads (refs/pull/&lt;n&gt;/head), open or not; empty for a repo that has none.</param>
public sealed record RemoteInfo(string? DefaultBranch, IReadOnlyList<RemoteBranch> Branches, IReadOnlyList<RemotePull>? Pulls = null)
{
    public IReadOnlyList<RemotePull> PullHeads => Pulls ?? [];
}

public sealed record CommitInfo(string Sha, string Author, DateTime Date, string Subject);

/// <summary>Plain git (no GitHub API) for checking access, listing branches and cloning.</summary>
public sealed class GitClient(GitberthOptions options, SettingsService settings)
{
    public async Task<RemoteInfo> LsRemoteAsync(GitHubRepoRef repo, CancellationToken ct = default)
    {
        var (result, hasToken) = await RunAsync(["ls-remote", "--symref", repo.CloneUrl], null, null, ct);
        if (!result.Ok) throw new GitException(Explain(result.ErrorText, repo, null, hasToken));
        return ParseLsRemote(result.StdOut);
    }

    /// <summary>
    /// What `git ls-remote --symref` lists: the default branch (HEAD's target), every branch, and every pull request's head
    /// (GitHub advertises refs/pull/&lt;n&gt;/head for each, open or closed). Tags and merge refs are left out.
    /// </summary>
    internal static RemoteInfo ParseLsRemote(string output)
    {
        string? defaultBranch = null;
        var branches = new List<RemoteBranch>();
        var pulls = new List<RemotePull>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2) continue;
            if (parts[1] == "HEAD" && parts[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                defaultBranch = parts[0]["ref: refs/heads/".Length..];
            else if (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                branches.Add(new RemoteBranch(parts[1]["refs/heads/".Length..], parts[0]));
            else if (parts[1].StartsWith("refs/pull/", StringComparison.Ordinal) && parts[1].EndsWith("/head", StringComparison.Ordinal)
                && int.TryParse(parts[1]["refs/pull/".Length..^"/head".Length], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                pulls.Add(new RemotePull(number, parts[0]));
        }
        return new RemoteInfo(defaultBranch, branches.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(), pulls.OrderBy(p => p.Number).ToList());
    }

    /// <summary>
    /// A shallow copy of one ref of the repo (refs/heads/&lt;branch&gt;, or refs/pull/&lt;n&gt;/head for a pull request) in
    /// <paramref name="targetDir"/>, checked out at its tip, or at <paramref name="commit"/> when one is given (a preview
    /// promoted to production builds exactly the commit the preview ran, even after its branch moved on).
    /// </summary>
    public async Task<CommitInfo> FetchAsync(GitHubRepoRef repo, string gitRef, string? commit, string targetDir, Action<string>? onLine, CancellationToken ct = default)
    {
        if (!IsSafeRef(gitRef)) throw new GitException($"'{gitRef}' isn't a branch or pull request Gitberth can fetch.");
        if (commit is not null && !IsSha(commit)) throw new GitException($"'{commit}' isn't a commit.");
        Directory.CreateDirectory(targetDir);
        var (init, _) = await RunAsync(["init", "--quiet", targetDir], null, null, ct);
        if (!init.Ok) throw new GitException("Couldn't prepare the folder for the code: " + init.ErrorText);
        ProcessResult fetched;
        bool hasToken;
        if (commit is not null)
        {
            // GitHub serves any commit it has by its SHA; one that isn't on the ref any more is still built.
            (fetched, hasToken) = await RunAsync(["fetch", "--depth", "1", "--no-tags", repo.CloneUrl, commit], targetDir, onLine, ct);
            if (!fetched.Ok)
                (fetched, hasToken) = await RunAsync(["fetch", "--depth", "50", "--no-tags", repo.CloneUrl, gitRef], targetDir, onLine, ct);
        }
        else
        {
            (fetched, hasToken) = await RunAsync(["fetch", "--depth", "1", "--no-tags", repo.CloneUrl, gitRef], targetDir, onLine, ct);
        }
        if (!fetched.Ok)
        {
            var explained = Explain(fetched.ErrorText, repo, null, hasToken);
            throw new GitException(fetched.ErrorText.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
                ? $"{gitRef} doesn't exist in {repo.FullName} any more."
                : explained);
        }
        var (checkout, _) = await RunAsync(["checkout", "--quiet", "--detach", commit ?? "FETCH_HEAD"], targetDir, onLine, ct);
        if (!checkout.Ok)
            throw new GitException(commit is not null
                ? $"Commit {commit[..7]} isn't in {repo.FullName} any more, so it can't be built."
                : "Fetched, but couldn't check out the code: " + checkout.ErrorText);
        return await ReadCommitAsync(targetDir, ct);
    }

    // A ref Gitberth builds: a branch or a pull request's head, never anything that git could read as an option.
    internal static bool IsSafeRef(string gitRef) =>
        (gitRef.StartsWith("refs/heads/", StringComparison.Ordinal) && gitRef.Length > "refs/heads/".Length
            || gitRef.StartsWith("refs/pull/", StringComparison.Ordinal) && gitRef.EndsWith("/head", StringComparison.Ordinal))
        && !gitRef.Contains("..", StringComparison.Ordinal) && !gitRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    internal static bool IsSha(string value) => value.Length is >= 7 and <= 64 && value.All(char.IsAsciiHexDigit);

    private async Task<CommitInfo> ReadCommitAsync(string dir, CancellationToken ct)
    {
        var (log, _) = await RunAsync(["log", "-1", "--format=%H%x1f%an%x1f%aI%x1f%s"], dir, null, ct);
        if (!log.Ok) throw new GitException("Cloned, but couldn't read the commit: " + log.ErrorText);
        var f = log.StdOut.Trim().Split('\x1f');
        var date = DateTime.TryParse(f.ElementAtOrDefault(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;
        return new CommitInfo(f[0], f.ElementAtOrDefault(1) ?? "", date, f.ElementAtOrDefault(3) ?? "");
    }

    /// <summary>Shallow clone of the branch tip into <paramref name="targetDir"/>.</summary>
    public async Task<CommitInfo> CloneAsync(GitHubRepoRef repo, string branch, string targetDir, Action<string>? onLine, CancellationToken ct = default)
    {
        var (clone, hasToken) = await RunAsync(
            ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, repo.CloneUrl, targetDir], null, onLine, ct);
        if (!clone.Ok) throw new GitException(Explain(clone.ErrorText, repo, branch, hasToken));
        return await ReadCommitAsync(targetDir, ct);
    }

    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(options.GitPath, ["--version"], null, ct);
            return r.Ok ? r.StdOut.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task<(ProcessResult Result, bool HasToken)> RunAsync(IEnumerable<string> args, string? workDir, Action<string>? onLine, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };
        var token = await settings.GetSecretAsync(SettingKeys.GitHubToken, ct);
        if (!string.IsNullOrEmpty(token))
        {
            // Same header actions/checkout uses. Passed as env-based git config, so the token never
            // lands in .git/config, the remote URL, or the process arguments.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            env["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + basic;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                options.GitPath,
                ["-c", "credential.helper=", .. args],
                new ProcessOptions { WorkingDirectory = workDir, Environment = env, OnLine = onLine },
                ct);
            return (result, !string.IsNullOrEmpty(token));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitException($"git isn't installed or isn't on PATH ('{options.GitPath}').");
        }
    }

    internal static string Explain(string stderr, GitHubRepoRef repo, string? branch, bool hasToken)
    {
        var s = stderr.Trim();
        bool Has(string text) => s.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("could not read Username") || Has("terminal prompts disabled") || Has("Authentication failed") || Has("invalid credentials"))
        {
            return hasToken
                ? "GitHub rejected the token. Check that it hasn't expired and update it in Settings."
                : $"{repo.FullName} needs a GitHub token. Add one in Settings (Contents: Read-only on this repo).";
        }
        if (Has("Repository not found") || (Has("repository '") && Has("not found")))
        {
            return hasToken
                ? $"GitHub can't find {repo.FullName}, or your token can't see it. Check the URL, and that the token has Contents: Read-only access to this repo."
                : $"GitHub can't find {repo.FullName}. If it's private, add a token in Settings.";
        }
        if (branch is not null && Has("Remote branch") && Has("not found")) return $"Branch '{branch}' doesn't exist in {repo.FullName}.";
        if (Has("Could not resolve host")) return "Can't reach github.com. Check the host's internet connection and DNS.";
        return s.Length == 0 ? "git failed without saying why." : s;
    }
}
