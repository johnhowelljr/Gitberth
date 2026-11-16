using System.Text.Json;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <param name="Saved">True when GitHub couldn't be read and these are the copy kept from the last successful read.</param>
public sealed record RepoVariablesResult(IReadOnlyList<RepoVariable> Variables, string? Error, bool Saved = false);

/// <summary>
/// The environment variables stored with each repo in GitHub (its Actions variables). Every app from the repo gets
/// them, at build time and when it runs; an app's own values set in Gitberth win.
/// </summary>
/// <param name="audit">Where a variable saved to or deleted from GitHub through Gitberth is recorded, with who did it; none in older tests.</param>
public sealed partial class RepoVariables(GitHubApi github, SettingsService settings, Audit.AuditLog? audit = null)
{
    /// <summary>
    /// The variables for a deploy. Each successful read is kept, encrypted like the app's own values, and used when
    /// GitHub can't be read: an outage or an expired token never starts an app without its environment.
    /// </summary>
    public async Task<RepoVariablesResult> ForDeployAsync(string owner, string repo, CancellationToken ct = default)
    {
        var fresh = await ReadAsync(owner, repo, ct);
        if (fresh.Error is null)
        {
            await KeepAsync(owner, repo, fresh.Variables, onlyIfKept: false, ct);
            return fresh;
        }
        return ParseSaved(await settings.GetSecretAsync(SavedKey(owner, repo), ct)) is { Count: > 0 } copy ? fresh with { Variables = copy, Saved = true } : fresh;
    }

    /// <summary>The repo's variables as GitHub has them now. A kept copy is refreshed too, so a deleted variable can't come back from it.</summary>
    public async Task<RepoVariablesResult> GetAsync(string owner, string repo, CancellationToken ct = default)
    {
        var fresh = await ReadAsync(owner, repo, ct);
        if (fresh.Error is null) await KeepAsync(owner, repo, fresh.Variables, onlyIfKept: true, ct);
        return fresh;
    }

    /// <summary>Drops the kept copy, once no app uses the repo any more.</summary>
    public Task ForgetAsync(string owner, string repo, CancellationToken ct = default) => settings.SetSecretAsync(SavedKey(owner, repo), null, ct);

    // GitHub names are case-insensitive, so Acme/Web and acme/web share one copy.
    internal static string SavedKey(string owner, string repo) => $"repoVariables.{owner}/{repo}".ToLowerInvariant();

    internal static List<RepoVariable>? ParseSaved(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<List<RepoVariable>>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<RepoVariablesResult> ReadAsync(string owner, string repo, CancellationToken ct)
    {
        if (await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is not { } token)
            return new RepoVariablesResult([], "No GitHub token is saved, so the repo's GitHub variables can't be read. Add one in Settings.");
        try
        {
            return new RepoVariablesResult(await github.ListVariablesAsync(token, owner, repo, ct), null);
        }
        catch (GitHubApiException ex)
        {
            return new RepoVariablesResult([], ex.Message);
        }
    }

    // The copy is only a fallback: when two deploys from the same repo store it at once, the other one's copy is just as good.
    private async Task KeepAsync(string owner, string repo, IReadOnlyList<RepoVariable> variables, bool onlyIfKept, CancellationToken ct)
    {
        var key = SavedKey(owner, repo);
        var saved = await settings.GetSecretAsync(key, ct);
        if (onlyIfKept && saved is null) return;
        var json = variables.Count == 0 ? null : JsonSerializer.Serialize(variables);
        if (json == saved) return;
        try
        {
            await settings.SetSecretAsync(key, json, ct);
        }
        catch (DbUpdateException)
        {
        }
    }

    /// <summary>
    /// Saves a variable to the repo's variables in GitHub, for every app from the repo, and records it in the audit log
    /// (or that it failed, with GitHub's words): its value only when it isn't one the editor hides.
    /// </summary>
    /// <param name="app">The app whose Environment tab it was saved from, which the entry is told against.</param>
    /// <param name="previous">Its value before, when the repo had it, for the entry's before and after.</param>
    public async Task SetAsync(string owner, string repo, string name, string value, CancellationToken ct = default, AppDefinition? app = null, string? previous = null)
    {
        var key = name.Trim();
        try
        {
            if (Problem(name) is { } problem) throw new GitHubApiException(problem);
            if (value.Length == 0) throw new GitHubApiException("GitHub doesn't keep a variable without a value. To remove one, delete it.");
            await github.SetVariableAsync(await TokenAsync(ct), owner, repo, key, value, ct);
        }
        catch (GitHubApiException ex)
        {
            Record("app.env.github.set", owner, repo, key, app, previous, value, ex.Message);
            throw;
        }
        Record("app.env.github.set", owner, repo, key, app, previous, value, error: null);
    }

    /// <summary>Deletes a variable from the repo's variables in GitHub, and records it in the audit log (or that it failed).</summary>
    /// <param name="app">The app whose Environment tab it was deleted from, which the entry is told against.</param>
    /// <param name="previous">Its value, for the entry's before.</param>
    public async Task DeleteAsync(string owner, string repo, string name, CancellationToken ct = default, AppDefinition? app = null, string? previous = null)
    {
        try
        {
            await github.DeleteVariableAsync(await TokenAsync(ct), owner, repo, name, ct);
        }
        catch (GitHubApiException ex)
        {
            Record("app.env.github.removed", owner, repo, name, app, previous, null, ex.Message);
            throw;
        }
        Record("app.env.github.removed", owner, repo, name, app, previous, null, error: null);
    }

    // One entry per save or deletion, by whoever's acting (the person on the Environment tab): the variable's name, the repo,
    // and its value before and after only when neither is one the editor hides (Masking), cut short as an app's own are.
    private void Record(string action, string owner, string repo, string name, AppDefinition? app, string? before, string? after, string? error)
    {
        if (audit is null) return;
        var removing = action == "app.env.github.removed";
        var secret = Masking.IsSecretName(name) || (before is not null && Masking.IsSecret(name, before)) || (after is not null && Masking.IsSecret(name, after));
        var where = $"{owner}/{repo}";
        var noun = secret ? "secret variable" : "variable";
        var summary = error is null
            ? removing
                ? $"Deleted the {noun} {name} from {where}'s variables in GitHub: no app from the repo gets it from there now."
                : $"Saved the {noun} {name} to {where}'s variables in GitHub: every app from the repo gets it at its next deploy."
            : removing
                ? $"Couldn't delete the {noun} {name} from {where}'s variables in GitHub: {Audit.AuditLog.Clean(error, 300)?.TrimEnd('.')}."
                : $"Couldn't save the {noun} {name} to {where}'s variables in GitHub: {Audit.AuditLog.Clean(error, 300)?.TrimEnd('.')}.";
        var changes = new List<Audit.AuditChange>
        {
            new("Repo", null, where),
            new("Value", secret ? null : Shown(before), secret || removing ? null : Shown(after)),
            new("Secret", null, Audit.AuditText.OnOff(secret)),
        };
        audit.Record(new Audit.AuditEntry(action, summary)
        {
            Outcome = error is null ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
            AppId = app?.Id, AppName = app?.Name, Container = app?.ContainerName,
            Target = name,
            Changes = changes,
        });
    }

    // A plain value as the log keeps an app's own (Apps.SettingsAudit): cut short.
    private static string? Shown(string? value) =>
        value is null ? null : value.Length <= Apps.SettingsAudit.ValueMost ? value : value[..(Apps.SettingsAudit.ValueMost - 1)] + "…";

    /// <summary>GitHub's rules for variable names, so a bad name fails here with a clear message.</summary>
    public static string? Problem(string name)
    {
        name = name.Trim();
        if (!GitHubName().IsMatch(name)) return "Use letters, digits and underscores, not starting with a digit.";
        if (name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)) return "GitHub reserves names that start with GITHUB_.";
        return null;
    }

    private async Task<string> TokenAsync(CancellationToken ct) =>
        await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) ?? throw new GitHubApiException("No GitHub token is saved. Add one in Settings.");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex GitHubName();
}
