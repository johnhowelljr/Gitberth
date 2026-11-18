using System.Globalization;
using System.Text;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Git;

public sealed class GitException(string message) : Exception(message);

public sealed record RemoteBranch(string Name, string Sha);

/// <summary>
/// A pull request's head as the repo advertises it (refs/pull/&lt;n&gt;/head): GitHub keeps it after the pull request closes.
/// A GitLab project advertises a merge request's the same way, as refs/merge-requests/&lt;iid&gt;/head.
/// </summary>
public sealed record RemotePull(int Number, string Sha);

/// <param name="Pulls">The pull requests' (or merge requests') heads, open or not; empty for a repo that has none.</param>
public sealed record RemoteInfo(string? DefaultBranch, IReadOnlyList<RemoteBranch> Branches, IReadOnlyList<RemotePull>? Pulls = null)
{
    public IReadOnlyList<RemotePull> PullHeads => Pulls ?? [];
}

/// <param name="Message">The whole message, subject and body; null when it wasn't read (the subject is all there is).</param>
public sealed record CommitInfo(string Sha, string Author, DateTime Date, string Subject, string? Message = null)
{
    /// <summary>The whole message, or the subject when that's all that was read.</summary>
    public string FullMessage => Message ?? Subject;
}

/// <summary>
/// Plain git (no GitHub or GitLab API) for checking access, listing branches and cloning, from github.com or from Gitberth's
/// GitLab (its address and token in Settings › GitLab).
/// </summary>
public sealed class GitClient(GitberthOptions options, SettingsService settings)
{
    /// <summary>The most a diff's list of files may take; a longer one counts as unknown, so the commit is built.</summary>
    internal const int DiffLimit = 8 * 1024 * 1024;

    /// <summary>Runs git's commands (without its -c options) in place of the git CLI, in tests.</summary>
    internal Func<IReadOnlyList<string>, ProcessOptions, CancellationToken, Task<ProcessResult>>? Runner { get; init; }

    public async Task<RemoteInfo> LsRemoteAsync(RepoRef repo, CancellationToken ct = default)
    {
        var remote = await RemoteAsync(repo, ct);
        var (result, hasToken) = await RunAsync(["ls-remote", "--symref", remote.Url], null, null, ct, remote: remote);
        if (!result.Ok) throw new GitException(Explain(result.ErrorText, repo, null, hasToken, remote.Instance));
        return ParseLsRemote(result.StdOut);
    }

    /// <summary>
    /// What `git ls-remote --symref` lists: the default branch (HEAD's target), every branch, and every pull request's head
    /// (GitHub advertises refs/pull/&lt;n&gt;/head for each, open or closed; GitLab refs/merge-requests/&lt;iid&gt;/head). Tags
    /// and merge refs are left out.
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
            else if (PullNumber(parts[1]) is int number)
                pulls.Add(new RemotePull(number, parts[0]));
        }
        return new RemoteInfo(defaultBranch, branches.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(), pulls.OrderBy(p => p.Number).ToList());
    }

    /// <summary>A pull request's head ref's number (refs/pull/12/head) or a merge request's (refs/merge-requests/12/head); null for any other ref.</summary>
    internal static int? PullNumber(string gitRef)
    {
        foreach (var prefix in PullPrefixes)
            if (gitRef.StartsWith(prefix, StringComparison.Ordinal) && gitRef.EndsWith("/head", StringComparison.Ordinal) && gitRef.Length > prefix.Length + "/head".Length
                && int.TryParse(gitRef[prefix.Length..^"/head".Length], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return number;
        return null;
    }

    private static readonly string[] PullPrefixes = ["refs/pull/", "refs/merge-requests/"];

    /// <summary>Where git reaches a repo, and the header that carries its host's token (Settings), if one is saved.</summary>
    /// <param name="Instance">Gitberth's GitLab's address, for a GitLab project; null for GitHub.</param>
    private sealed record Remote(string Url, string? HeaderKey, string? HeaderValue, string? Instance);

    // A GitHub repo: github.com, with the GitHub token. A GitLab project: on Settings' GitLab, with its token, as GitLab
    // takes it over HTTPS (user oauth2, the token as the password).
    private async Task<Remote> RemoteAsync(RepoRef repo, CancellationToken ct)
    {
        if (repo is GitHubRepoRef github) return new Remote(github.CloneUrl, null, null, null);
        var project = (GitLabRepoRef)repo;
        var instance = GitLabRepoRef.NormalizeInstance(await settings.GetAsync(SettingKeys.GitLabUrl, ct), out _) ?? GitLabRepoRef.DefaultInstance;
        var token = await settings.GetSecretAsync(SettingKeys.GitLabToken, ct);
        if (string.IsNullOrEmpty(token)) return new Remote(project.CloneUrl(instance), null, null, instance);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:" + token));
        return new Remote(project.CloneUrl(instance), $"http.{instance}/.extraheader", "AUTHORIZATION: basic " + basic, instance);
    }

    /// <summary>
    /// A shallow copy of one ref of the repo (refs/heads/&lt;branch&gt;, or a pull request's head: refs/pull/&lt;n&gt;/head on
    /// GitHub, refs/merge-requests/&lt;iid&gt;/head on GitLab) in <paramref name="targetDir"/>, checked out at its tip, or at
    /// <paramref name="commit"/> when one is given (a preview promoted to production builds exactly the commit the preview
    /// ran, even after its branch moved on).
    /// </summary>
    public async Task<CommitInfo> FetchAsync(RepoRef repo, string gitRef, string? commit, string targetDir, Action<string>? onLine, CancellationToken ct = default)
    {
        if (!IsSafeRef(gitRef)) throw new GitException($"'{gitRef}' isn't a branch or pull request Gitberth can fetch.");
        if (commit is not null && !IsSha(commit)) throw new GitException($"'{commit}' isn't a commit.");
        var remote = await RemoteAsync(repo, ct);
        Directory.CreateDirectory(targetDir);
        var (init, _) = await RunAsync(["init", "--quiet", targetDir], null, null, ct, remote: remote);
        if (!init.Ok) throw new GitException("Couldn't prepare the folder for the code: " + init.ErrorText);
        ProcessResult fetched;
        bool hasToken;
        if (commit is not null)
        {
            // GitHub and GitLab serve any commit they have by its SHA; one that isn't on the ref any more is still built.
            (fetched, hasToken) = await RunAsync(["fetch", "--depth", "1", "--no-tags", remote.Url, commit], targetDir, onLine, ct, remote: remote);
            if (!fetched.Ok)
                (fetched, hasToken) = await RunAsync(["fetch", "--depth", "50", "--no-tags", remote.Url, gitRef], targetDir, onLine, ct, remote: remote);
        }
        else
        {
            (fetched, hasToken) = await RunAsync(["fetch", "--depth", "1", "--no-tags", remote.Url, gitRef], targetDir, onLine, ct, remote: remote);
        }
        if (!fetched.Ok)
        {
            var explained = Explain(fetched.ErrorText, repo, null, hasToken, remote.Instance);
            throw new GitException(fetched.ErrorText.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
                ? $"{gitRef} doesn't exist in {repo.FullName} any more."
                : explained);
        }
        var (checkout, _) = await RunAsync(["checkout", "--quiet", "--detach", commit ?? "FETCH_HEAD"], targetDir, onLine, ct, remote: remote);
        if (!checkout.Ok)
            throw new GitException(commit is not null
                ? $"Commit {commit[..7]} isn't in {repo.FullName} any more, so it can't be built."
                : "Fetched, but couldn't check out the code: " + checkout.ErrorText);
        return await ReadCommitAsync(targetDir, ct, remote);
    }

    /// <summary>
    /// The whole SHA of <paramref name="commit"/> (whole, or its start) when it's on <paramref name="branch"/>: the branch's tip
    /// or one of its ancestors. Null when it isn't, such as a fork's commit, which GitHub serves by its SHA through the repo
    /// too. Reads the branch's history without any files (a bare, treeless clone in <paramref name="scratchDir"/>), which is
    /// quick even for a large repo, and never fetches a commit that isn't in that history.
    /// </summary>
    /// <exception cref="GitException">The branch couldn't be read.</exception>
    public async Task<string?> CommitOnBranchAsync(RepoRef repo, string branch, string commit, string scratchDir, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitException($"'{commit}' isn't a commit.");
        var remote = await RemoteAsync(repo, ct);
        // A partial clone fetches what it's missing when it's asked for it: not here, so a commit that isn't in the branch's
        // history is never fetched to be looked at.
        var noLazyFetch = new Dictionary<string, string> { ["GIT_NO_LAZY_FETCH"] = "1" };
        var (clone, hasToken) = await RunAsync(["clone", "--bare", "--quiet", "--filter=tree:0", "--single-branch", "--no-tags", "--branch", branch, remote.Url, scratchDir],
            null, null, ct, extraEnv: noLazyFetch, remote: remote);
        if (!clone.Ok) throw new GitException(Explain(clone.ErrorText, repo, branch, hasToken, remote.Instance));
        var (found, _) = await RunAsync(["rev-parse", "--verify", "--quiet", commit + "^{commit}"], scratchDir, null, ct, extraEnv: noLazyFetch, remote: remote);
        var sha = found.Ok ? found.StdOut.Trim() : "";
        if (!IsSha(sha)) return null;
        var (ancestor, _) = await RunAsync(["merge-base", "--is-ancestor", sha, "HEAD"], scratchDir, null, ct, extraEnv: noLazyFetch, remote: remote);
        return ancestor.ExitCode == 0 ? sha : null;
    }

    // A ref Gitberth builds: a branch or a pull request's (or merge request's) head, never anything that git could read as an option.
    internal static bool IsSafeRef(string gitRef) =>
        (gitRef.StartsWith("refs/heads/", StringComparison.Ordinal) && gitRef.Length > "refs/heads/".Length
            || gitRef.StartsWith("refs/pull/", StringComparison.Ordinal) && gitRef.EndsWith("/head", StringComparison.Ordinal)
            || gitRef.StartsWith("refs/merge-requests/", StringComparison.Ordinal) && gitRef.EndsWith("/head", StringComparison.Ordinal))
        && !gitRef.Contains("..", StringComparison.Ordinal) && !gitRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    internal static bool IsSha(string value) => value.Length is >= 7 and <= 64 && value.All(char.IsAsciiHexDigit);

    private async Task<CommitInfo> ReadCommitAsync(string dir, CancellationToken ct, Remote? remote = null)
    {
        // The body too (%B is the whole message), for the markers that ask a commit not to be deployed.
        var (log, _) = await RunAsync(["log", "-1", "--format=%H%x1f%an%x1f%aI%x1f%s%x1f%B"], dir, null, ct, remote: remote);
        if (!log.Ok) throw new GitException("Cloned, but couldn't read the commit: " + log.ErrorText);
        return ParseCommit(log.StdOut);
    }

    /// <summary>`git log -1 --format=%H%x1f%an%x1f%aI%x1f%s%x1f%B`: the commit, with its whole message.</summary>
    internal static CommitInfo ParseCommit(string output)
    {
        var f = output.Trim().Split('\x1f', 5);
        var date = DateTime.TryParse(f.ElementAtOrDefault(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;
        var subject = f.ElementAtOrDefault(3) ?? "";
        return new CommitInfo(f[0], f.ElementAtOrDefault(1) ?? "", date, subject, f.Length > 4 ? f[4].Trim() : null);
    }

    /// <summary>
    /// Fetches one more commit, by its SHA, into a shallow copy <see cref="CloneAsync"/> or <see cref="FetchAsync"/> made:
    /// only that commit, so its files can be compared with the checked-out one's (<see cref="ChangedFilesAsync"/>).
    /// </summary>
    /// <exception cref="GitException">The host doesn't have it (a force-push removed it), or can't be reached.</exception>
    public async Task FetchCommitAsync(RepoRef repo, string sha, string dir, CancellationToken ct = default)
    {
        if (!IsSha(sha)) throw new GitException($"'{sha}' isn't a commit.");
        var remote = await RemoteAsync(repo, ct);
        var (fetched, hasToken) = await RunAsync(["fetch", "--depth", "1", "--no-tags", remote.Url, sha], dir, null, ct, remote: remote);
        if (!fetched.Ok) throw new GitException(Explain(fetched.ErrorText, repo, null, hasToken, remote.Instance));
    }

    /// <summary>
    /// The files that differ between <paramref name="baseSha"/> (fetched already) and the checked-out commit, as paths from
    /// the repo's root. A rename counts as both its old and its new path.
    /// </summary>
    /// <exception cref="GitException">git couldn't compare them, or the list is too long to read whole.</exception>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string dir, string baseSha, CancellationToken ct = default)
    {
        if (!IsSha(baseSha)) throw new GitException($"'{baseSha}' isn't a commit.");
        // -z: every name as it is, never quoted; the -- keeps a file called HEAD from making git ask which is meant.
        var (diff, _) = await RunAsync(["diff", "--name-only", "--no-renames", "-z", baseSha, "HEAD", "--"], dir, null, ct, DiffLimit);
        if (!diff.Ok) throw new GitException("git couldn't compare the commits: " + diff.ErrorText);
        if (diff.StdOut.Length >= DiffLimit) throw new GitException("the commits differ in too many files to list.");
        return ParseNames(diff.StdOut);
    }

    /// <summary>`git diff --name-only -z`'s names (each ends with a NUL; the output is read as lines, so a newline follows).</summary>
    internal static IReadOnlyList<string> ParseNames(string output)
    {
        var text = output.EndsWith('\n') ? output[..^1] : output;
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Shallow clone of the branch tip into <paramref name="targetDir"/>.</summary>
    public async Task<CommitInfo> CloneAsync(RepoRef repo, string branch, string targetDir, Action<string>? onLine, CancellationToken ct = default)
    {
        var remote = await RemoteAsync(repo, ct);
        var (clone, hasToken) = await RunAsync(
            ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, remote.Url, targetDir], null, onLine, ct, remote: remote);
        if (!clone.Ok) throw new GitException(Explain(clone.ErrorText, repo, branch, hasToken, remote.Instance));
        return await ReadCommitAsync(targetDir, ct, remote);
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

    // A GitHub repo's commands (and any without a remote) carry the GitHub token's header, as they always have; a GitLab
    // project's carry the GitLab token's, for its instance only.
    private async Task<(ProcessResult Result, bool HasToken)> RunAsync(IReadOnlyList<string> args, string? workDir, Action<string>? onLine, CancellationToken ct,
        int maxCapture = 4 * 1024 * 1024, IReadOnlyDictionary<string, string>? extraEnv = null, Remote? remote = null)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };
        foreach (var (key, value) in extraEnv ?? new Dictionary<string, string>()) env[key] = value;
        bool hasToken;
        if (remote is { Instance: not null })
        {
            // Passed as env-based git config, like GitHub's below: the token never lands in .git/config, the remote URL, or the
            // process arguments.
            hasToken = remote.HeaderKey is not null;
            if (remote is { HeaderKey: { } key, HeaderValue: { } header })
            {
                env["GIT_CONFIG_COUNT"] = "1";
                env["GIT_CONFIG_KEY_0"] = key;
                env["GIT_CONFIG_VALUE_0"] = header;
            }
        }
        else
        {
            var token = await settings.GetSecretAsync(SettingKeys.GitHubToken, ct);
            hasToken = !string.IsNullOrEmpty(token);
            if (hasToken)
            {
                // Same header actions/checkout uses. Passed as env-based git config, so the token never
                // lands in .git/config, the remote URL, or the process arguments.
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
                env["GIT_CONFIG_COUNT"] = "1";
                env["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
                env["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + basic;
            }
        }

        try
        {
            var processOptions = new ProcessOptions { WorkingDirectory = workDir, Environment = env, OnLine = onLine, MaxCapture = maxCapture };
            var result = Runner is not null
                ? await Runner(args, processOptions, ct)
                : await ProcessRunner.RunAsync(options.GitPath, ["-c", "credential.helper=", .. args], processOptions, ct);
            return (result, hasToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitException($"git isn't installed or isn't on PATH ('{options.GitPath}').");
        }
    }

    /// <param name="instance">Gitberth's GitLab's address, for a GitLab project.</param>
    internal static string Explain(string stderr, RepoRef repo, string? branch, bool hasToken, string? instance = null)
    {
        var s = stderr.Trim();
        bool Has(string text) => s.Contains(text, StringComparison.OrdinalIgnoreCase);
        var gitLab = repo.Host == GitHost.GitLab;

        if (Has("could not read Username") || Has("terminal prompts disabled") || Has("Authentication failed") || Has("invalid credentials")
            || gitLab && Has("HTTP Basic: Access denied"))
        {
            return (gitLab, hasToken) switch
            {
                (false, true) => "GitHub rejected the token. Check that it hasn't expired and update it in Settings.",
                (false, false) => $"{repo.FullName} needs a GitHub token. Add one in Settings (Contents: Read-only on this repo).",
                (true, true) => "GitLab rejected the token, or it can't read this project. Check that it hasn't expired, and that it has the read_repository scope, in Settings › GitLab.",
                (true, false) => $"{repo.FullName} needs a GitLab token. Add one in Settings › GitLab (read_api and read_repository scopes).",
            };
        }
        // GitLab's rate limit holds for git too: said, and tried again later, never pushed through.
        if (gitLab && Has("returned error: 429"))
            return "GitLab's rate limit for git is used up for the moment: Gitberth tries again later.";
        // GitLab answers git a 403 for a token without a repository scope.
        if (gitLab && Has("returned error: 403"))
            return $"GitLab refused the token on {repo.FullName}: it needs the read_repository (or api) scope, and its user a role on the project. Check it in Settings › GitLab.";
        if (Has("Repository not found") || (Has("repository '") && Has("not found")) || gitLab && (Has("The project you were looking for could not be found") || Has("returned error: 404")))
        {
            return (gitLab, hasToken) switch
            {
                (false, true) => $"GitHub can't find {repo.FullName}, or your token can't see it. Check the URL, and that the token has Contents: Read-only access to this repo.",
                (false, false) => $"GitHub can't find {repo.FullName}. If it's private, add a token in Settings.",
                (true, true) => $"GitLab can't find {repo.FullName}, or your token can't see it. Check the URL, and that the token's user is a member of the project.",
                (true, false) => $"GitLab can't find {repo.FullName}. If it's private, add a token in Settings › GitLab.",
            };
        }
        if (branch is not null && Has("Remote branch") && Has("not found")) return $"Branch '{branch}' doesn't exist in {repo.FullName}.";
        if (Has("Could not resolve host"))
            return gitLab
                ? $"Can't reach {HostOf(instance)}. Check its address in Settings › GitLab, and the host's network and DNS."
                : "Can't reach github.com. Check the host's internet connection and DNS.";
        if (gitLab && (Has("Failed to connect") || Has("Connection refused") || Has("SSL certificate problem")))
            return $"Can't reach GitLab at {HostOf(instance)}: {s.Split('\n')[^1].Trim()}";
        return s.Length == 0 ? "git failed without saying why." : s;
    }

    private static string HostOf(string? instance) => Uri.TryCreate(instance, UriKind.Absolute, out var uri) ? uri.Authority : instance ?? "GitLab";
}
