using System.Collections.Concurrent;

namespace Gitberth.Services;

/// <summary>A GitHub feature the token isn't allowed to use, as Settings shows it, and since when.</summary>
/// <param name="Repo">The repo GitHub refused it on (owner/name); null when GitHub refused the token itself.</param>
public sealed record GitHubProblem(GitHubFeature Feature, string Text, DateTime At, string? Repo = null);

/// <summary>Whether Gitberth's webhook is on a repo: added, already there, or why not.</summary>
public sealed record HookResult(string Repo, bool Ok, string Text, DateTime At);

/// <summary>
/// What Gitberth learned while reporting to GitHub, kept in memory: a feature GitHub refuses isn't tried again for
/// <see cref="PauseFor"/> (or until the token is saved again), so a missing permission never costs every deploy a failed
/// call. A refusal on one repo (the token lacks the permission there, or can't see it) rests only that repo; a refused
/// token rests the feature everywhere. Settings shows both. Also each repo's webhook, as the last attempt left it.
/// </summary>
public sealed class GitHubIntegrationState
{
    /// <summary>How long a feature the token isn't allowed to use rests before Gitberth tries it again.</summary>
    public static readonly TimeSpan PauseFor = TimeSpan.FromHours(1);

    // Refused everywhere: GitHub refused the token itself.
    private readonly ConcurrentDictionary<GitHubFeature, GitHubProblem> _problems = new();
    // Refused on one repo, by feature and repo (lower case).
    private readonly ConcurrentDictionary<(GitHubFeature Feature, string Repo), GitHubProblem> _refused = new();
    private readonly ConcurrentDictionary<string, HookResult> _hooks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a problem or a webhook result changes, so an open Settings page shows it.</summary>
    public event Action? Changed;

    /// <summary>The clock <see cref="Problems"/> reads; the tests set their own.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// What Settings warns about: a refused token, and each repo statuses or comments were refused on within the last
    /// <see cref="PauseFor"/>. After that Gitberth asks that repo again with its next deploy, which says it again if GitHub
    /// still refuses, so a repo no longer deployed (or fixed on GitHub) doesn't keep its warning. A repo's webhook says so in
    /// the list of repos instead.
    /// </summary>
    public IReadOnlyList<GitHubProblem> Problems
    {
        get
        {
            var now = Now();
            return
            [
                .. _problems.Values.OrderBy(p => p.Feature),
                .. _refused.Values.Where(p => p.Feature != GitHubFeature.Webhooks && now - p.At < PauseFor)
                    .OrderBy(p => p.Feature).ThenBy(p => p.Repo, StringComparer.OrdinalIgnoreCase),
            ];
        }
    }

    public IReadOnlyList<HookResult> Hooks => _hooks.Values.OrderBy(h => h.Repo, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether the feature rests everywhere after GitHub refused the token.</summary>
    public bool Paused(GitHubFeature feature, DateTime now) => _problems.TryGetValue(feature, out var p) && now - p.At < PauseFor;

    /// <summary>Whether the feature rests on the repo: GitHub refused the token, or refused it on this repo.</summary>
    public bool Paused(GitHubFeature feature, string repo, DateTime now) =>
        Paused(feature, now) || (_refused.TryGetValue((feature, repo.ToLowerInvariant()), out var p) && now - p.At < PauseFor);

    /// <summary>GitHub refused the token: the feature rests everywhere, and Settings says why. Returns whether it's news (to log once).</summary>
    public bool Report(GitHubFeature feature, string text, DateTime now)
    {
        var known = _problems.TryGetValue(feature, out var before) && before.Text == text;
        _problems[feature] = new GitHubProblem(feature, text, now);
        if (!known) Changed?.Invoke();
        return !known;
    }

    /// <summary>GitHub refused the feature on one repo: that repo rests, the others carry on. Returns whether it's news (to log once, and show again once it had gone).</summary>
    public bool Refused(GitHubFeature feature, string repo, string text, DateTime now)
    {
        var key = (feature, repo.ToLowerInvariant());
        var known = _refused.TryGetValue(key, out var before) && before.Text == text && now - before.At < PauseFor;
        _refused[key] = new GitHubProblem(feature, text, now, repo);
        if (!known) Changed?.Invoke();
        return !known;
    }

    /// <summary>The feature worked on the repo: the token is good, and what was said about either goes.</summary>
    public void Worked(GitHubFeature feature, string repo)
    {
        var token = _problems.TryRemove(feature, out _);
        var here = _refused.TryRemove((feature, repo.ToLowerInvariant()), out _);
        if (token || here) Changed?.Invoke();
    }

    /// <summary>A new token: everything is tried again.</summary>
    public void Clear()
    {
        _problems.Clear();
        _refused.Clear();
        _hooks.Clear();
        Changed?.Invoke();
    }

    internal void Hook(HookResult result)
    {
        _hooks[result.Repo] = result;
        Changed?.Invoke();
    }

    /// <summary>The webhook's host name changed: every repo is tried again.</summary>
    internal void ForgetHooks()
    {
        _hooks.Clear();
        foreach (var key in _refused.Keys.Where(k => k.Feature == GitHubFeature.Webhooks).ToList()) _refused.TryRemove(key, out _);
        Changed?.Invoke();
    }
}
