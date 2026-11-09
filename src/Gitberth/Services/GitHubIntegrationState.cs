using System.Collections.Concurrent;

namespace Gitberth.Services;

/// <summary>A GitHub feature the token isn't allowed to use, as Settings shows it, and since when.</summary>
public sealed record GitHubProblem(GitHubFeature Feature, string Text, DateTime At);

/// <summary>Whether Gitberth's webhook is on a repo: added, already there, or why not.</summary>
public sealed record HookResult(string Repo, bool Ok, string Text, DateTime At);

/// <summary>
/// What Gitberth learned while reporting to GitHub, kept in memory: a feature the token isn't allowed to use is shown once
/// in Settings and isn't tried again for <see cref="PauseFor"/> (or until the token is saved again), so a missing
/// permission never costs every deploy a failed call. Also each repo's webhook, as the last attempt left it.
/// </summary>
public sealed class GitHubIntegrationState
{
    /// <summary>How long a feature the token isn't allowed to use rests before Gitberth tries it again.</summary>
    public static readonly TimeSpan PauseFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<GitHubFeature, GitHubProblem> _problems = new();
    private readonly ConcurrentDictionary<string, HookResult> _hooks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a problem or a webhook result changes, so an open Settings page shows it.</summary>
    public event Action? Changed;

    public IReadOnlyList<GitHubProblem> Problems => _problems.Values.OrderBy(p => p.Feature).ToList();

    public IReadOnlyList<HookResult> Hooks => _hooks.Values.OrderBy(h => h.Repo, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether the feature rests after the token was refused it.</summary>
    public bool Paused(GitHubFeature feature, DateTime now) => _problems.TryGetValue(feature, out var p) && now - p.At < PauseFor;

    /// <summary>The token was refused the feature: it rests, and Settings says why. Returns whether it's news (to log once).</summary>
    public bool Report(GitHubFeature feature, string text, DateTime now)
    {
        var known = _problems.TryGetValue(feature, out var before) && before.Text == text;
        _problems[feature] = new GitHubProblem(feature, text, now);
        if (!known) Changed?.Invoke();
        return !known;
    }

    /// <summary>The feature worked: whatever was said about it goes.</summary>
    public void Worked(GitHubFeature feature)
    {
        if (_problems.TryRemove(feature, out _)) Changed?.Invoke();
    }

    /// <summary>A new token: everything is tried again.</summary>
    public void Clear()
    {
        _problems.Clear();
        _hooks.Clear();
        Changed?.Invoke();
    }

    internal void Hook(HookResult result)
    {
        _hooks[result.Repo] = result;
        Changed?.Invoke();
    }

    internal void ForgetHooks()
    {
        _hooks.Clear();
        Changed?.Invoke();
    }
}
