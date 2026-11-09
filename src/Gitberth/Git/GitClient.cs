using System.Globalization;
using System.Text;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Git;

public sealed class GitException(string message) : Exception(message);

public sealed record RemoteBranch(string Name, string Sha);

public sealed record RemoteInfo(string? DefaultBranch, IReadOnlyList<RemoteBranch> Branches);

public sealed record CommitInfo(string Sha, string Author, DateTime Date, string Subject);

/// <summary>Plain git (no GitHub API) for checking access, listing branches and cloning.</summary>
public sealed class GitClient(GitberthOptions options, SettingsService settings)
{
    public async Task<RemoteInfo> LsRemoteAsync(GitHubRepoRef repo, CancellationToken ct = default)
    {
        var (result, hasToken) = await RunAsync(["ls-remote", "--symref", repo.CloneUrl], null, null, ct);
        if (!result.Ok) throw new GitException(Explain(result.ErrorText, repo, null, hasToken));

        string? defaultBranch = null;
        var branches = new List<RemoteBranch>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2) continue;
            if (parts[1] == "HEAD" && parts[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                defaultBranch = parts[0]["ref: refs/heads/".Length..];
            else if (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                branches.Add(new RemoteBranch(parts[1]["refs/heads/".Length..], parts[0]));
        }
        return new RemoteInfo(defaultBranch, branches.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Shallow clone of the branch tip into <paramref name="targetDir"/>.</summary>
    public async Task<CommitInfo> CloneAsync(GitHubRepoRef repo, string branch, string targetDir, Action<string>? onLine, CancellationToken ct = default)
    {
        var (clone, hasToken) = await RunAsync(
            ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, repo.CloneUrl, targetDir], null, onLine, ct);
        if (!clone.Ok) throw new GitException(Explain(clone.ErrorText, repo, branch, hasToken));

        var (log, _) = await RunAsync(["log", "-1", "--format=%H%x1f%an%x1f%aI%x1f%s"], targetDir, null, ct);
        if (!log.Ok) throw new GitException("Cloned, but couldn't read the commit: " + log.ErrorText);
        var f = log.StdOut.Trim().Split('\x1f');
        var date = DateTime.TryParse(f.ElementAtOrDefault(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;
        return new CommitInfo(f[0], f.ElementAtOrDefault(1) ?? "", date, f.ElementAtOrDefault(3) ?? "");
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
