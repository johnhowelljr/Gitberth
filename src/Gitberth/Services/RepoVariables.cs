using System.Text.Json;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Git;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <param name="Saved">True when the host couldn't be read and these are the copy kept from the last successful read.</param>
public sealed record RepoVariablesResult(IReadOnlyList<RepoVariable> Variables, string? Error, bool Saved = false)
{
    /// <summary>What a deploy's log says about the variables it left out (a GitLab file variable, a protected one for a preview).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A GitLab project's variables under every scope, which <see cref="Variables"/> picked this deploy's from; null for a GitHub repo's.</summary>
    public IReadOnlyList<RepoVariable>? All { get; init; }

    /// <summary>What production gets: <see cref="Variables"/>, or for a GitLab preview's deploy the project's variables for production.</summary>
    public IReadOnlyList<RepoVariable> ForProduction => All is null ? Variables : RepoVariables.Scoped(All, preview: false).Used;
}

/// <summary>
/// The environment variables stored with each repo on its host: a GitHub repo's Actions variables, or a GitLab project's
/// CI/CD variables. Every app from the repo gets them, at build time and when it runs; an app's own values set in Gitberth
/// win. A GitLab variable applies by its environment scope: * to every deploy, production to the app's own, preview/* to
/// its previews (one for a scope wins over the * one of the same name); a protected one only ever reaches production,
/// a masked one is secret, and a file one isn't passed. Any other scope is left to GitLab's own pipelines.
/// </summary>
/// <param name="audit">Where a variable saved to or deleted from GitHub through Gitberth is recorded, with who did it; none in older tests.</param>
/// <param name="gitLab">GitLab's API, for a GitLab project's variables; without it (in older tests) they can't be read.</param>
public sealed partial class RepoVariables(GitHubApi github, SettingsService settings, Audit.AuditLog? audit = null, GitLabApi? gitLab = null)
{
    /// <summary>The environment scopes a GitLab variable is used under: every deploy, production, previews.</summary>
    public const string AllScope = "*";
    public const string ProductionScope = "production";
    public const string PreviewScope = "preview/*";

    /// <summary>
    /// The variables for a deploy. Each successful read is kept, encrypted like the app's own values, and used when the
    /// host can't be read: an outage or an expired token never starts an app without its environment.
    /// </summary>
    /// <param name="preview">For a preview's deploy: a GitLab project's variables for previews rather than production's.</param>
    public async Task<RepoVariablesResult> ForDeployAsync(string owner, string repo, CancellationToken ct = default, bool preview = false)
    {
        var fresh = await ReadAsync(owner, repo, ct);
        if (fresh.Error is null)
        {
            await KeepAsync(owner, repo, fresh.Variables, onlyIfKept: false, ct);
            return ForEnvironment(owner, fresh, preview);
        }
        return ParseSaved(await settings.GetSecretAsync(SavedKey(owner, repo), ct)) is { Count: > 0 } copy
            ? ForEnvironment(owner, fresh with { Variables = copy, Saved = true }, preview)
            : fresh;
    }

    /// <summary>
    /// The repo's variables as its host has them now (a GitLab project's under every scope, each with its own). A kept copy is
    /// refreshed too, so a deleted variable can't come back from it.
    /// </summary>
    public async Task<RepoVariablesResult> GetAsync(string owner, string repo, CancellationToken ct = default)
    {
        var fresh = await ReadAsync(owner, repo, ct);
        if (fresh.Error is null) await KeepAsync(owner, repo, fresh.Variables, onlyIfKept: true, ct);
        return fresh;
    }

    /// <summary>Drops the kept copy, once no app uses the repo any more.</summary>
    public Task ForgetAsync(string owner, string repo, CancellationToken ct = default) => settings.SetSecretAsync(SavedKey(owner, repo), null, ct);

    // GitHub names are case-insensitive, so Acme/Web and acme/web share one copy (and GitLab's paths are too).
    internal static string SavedKey(string owner, string repo) => $"repoVariables.{owner}/{repo}".ToLowerInvariant();

    internal static List<RepoVariable>? ParseSaved(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<List<RepoVariable>>(json); }
        catch (JsonException) { return null; }
    }

    // A GitHub repo's variables are every deploy's. A GitLab project's are this deploy's environment's.
    private static RepoVariablesResult ForEnvironment(string owner, RepoVariablesResult read, bool preview)
    {
        if (!RepoRef.IsGitLab(owner)) return read;
        var (used, notes) = Scoped(read.Variables, preview);
        return read with { Variables = used, Notes = notes, All = read.Variables };
    }

    /// <summary>
    /// A GitLab project's variables for one deploy: scope * and production for production, * and preview/* for a preview,
    /// the scope's own winning over *'s of the same name. Left out, and said: a file variable (only plain ones are
    /// passed), and for a preview a protected one (GitLab gives those to protected branches only, so only production).
    /// </summary>
    internal static (List<RepoVariable> Used, List<string> Notes) Scoped(IReadOnlyList<RepoVariable> all, bool preview)
    {
        var own = preview ? PreviewScope : ProductionScope;
        var chosen = new Dictionary<string, RepoVariable>(StringComparer.Ordinal);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var guarded = new SortedSet<string>(StringComparer.Ordinal);
        // *'s first, so the scope's own value of a name takes its place.
        foreach (var v in all.Where(v => v.Scope is null or AllScope).Concat(all.Where(v => v.Scope == own)))
        {
            if (v.IsFile)
            {
                files.Add(v.Name);
                continue;
            }
            if (preview && v.Protected)
            {
                guarded.Add(v.Name);
                continue;
            }
            chosen[v.Name] = v;
        }
        var notes = new List<string>();
        if (files.Count > 0)
            notes.Add($"Not used from GitLab: {string.Join(", ", files)}, {(files.Count == 1 ? "a file variable" : "file variables")}. Gitberth passes plain ones (Variable type) only.");
        if (guarded.Count > 0)
            notes.Add($"Not given to previews: {string.Join(", ", guarded)}, protected in GitLab, which only production gets.");
        return (chosen.Values.OrderBy(v => v.Name, StringComparer.Ordinal).ToList(), notes);
    }

    private async Task<RepoVariablesResult> ReadAsync(string owner, string repo, CancellationToken ct)
    {
        if (RepoRef.Of(owner, repo) is GitLabRepoRef project) return await ReadGitLabAsync(project, ct);
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

    private async Task<RepoVariablesResult> ReadGitLabAsync(GitLabRepoRef project, CancellationToken ct)
    {
        if (gitLab is null) return new RepoVariablesResult([], "GitLab isn't set up in this Gitberth.");
        var conn = await gitLab.ConnectionAsync(ct);
        if (!conn.HasToken)
            return new RepoVariablesResult([], "No GitLab token is saved, so the project's CI/CD variables can't be read. Add one in Settings › GitLab.");
        try
        {
            return new RepoVariablesResult((await gitLab.ListVariablesAsync(conn, project, ct))
                .Select(v => new RepoVariable(v.Key, v.Value, v.Scope, v.Masked, v.Protected, v.Type == "file")).ToList(), null);
        }
        catch (GitLabApiException ex)
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
    /// Saves a variable to the repo's variables on its host (a GitLab project's under <paramref name="scope"/>), for every
    /// app from the repo, and records it in the audit log (or that it failed, with the host's words): its value only when
    /// it isn't one the editor hides.
    /// </summary>
    /// <param name="app">The app whose Environment tab it was saved from, which the entry is told against.</param>
    /// <param name="previous">Its value before, when the repo had it, for the entry's before and after.</param>
    /// <param name="scope">A GitLab variable's environment scope (*, production or preview/*); * when not given. GitHub's have none.</param>
    /// <exception cref="HostApiException">The host refused it, or couldn't be reached.</exception>
    public async Task SetAsync(string owner, string repo, string name, string value, CancellationToken ct = default, AppDefinition? app = null, string? previous = null,
        string? scope = null)
    {
        var key = name.Trim();
        if (RepoRef.Of(owner, repo) is GitLabRepoRef project)
        {
            var where = scope ?? AllScope;
            try
            {
                if (GitLabProblem(name) is { } problem) throw new GitLabApiException(problem);
                if (ScopeProblem(where) is { } badScope) throw new GitLabApiException(badScope);
                if (value.Length == 0) throw new GitLabApiException("Gitberth doesn't keep a variable without a value. To remove one, delete it.");
                await gitLab!.SetVariableAsync(await GitLabConnectionAsync(ct), project, key, value, where, ct);
            }
            catch (GitLabApiException ex)
            {
                Record("app.env.gitlab.set", owner, repo, key, app, previous, value, ex.Message, where);
                throw;
            }
            Record("app.env.gitlab.set", owner, repo, key, app, previous, value, error: null, where);
            return;
        }
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

    /// <summary>Deletes a variable from the repo's variables on its host (a GitLab project's from <paramref name="scope"/>), and records it in the audit log (or that it failed).</summary>
    /// <param name="app">The app whose Environment tab it was deleted from, which the entry is told against.</param>
    /// <param name="previous">Its value, for the entry's before.</param>
    /// <param name="scope">A GitLab variable's environment scope; * when not given.</param>
    /// <exception cref="HostApiException">The host refused it, or couldn't be reached.</exception>
    public async Task DeleteAsync(string owner, string repo, string name, CancellationToken ct = default, AppDefinition? app = null, string? previous = null,
        string? scope = null)
    {
        if (RepoRef.Of(owner, repo) is GitLabRepoRef project)
        {
            var where = scope ?? AllScope;
            try
            {
                await gitLab!.DeleteVariableAsync(await GitLabConnectionAsync(ct), project, name, where, ct);
            }
            catch (GitLabApiException ex)
            {
                Record("app.env.gitlab.removed", owner, repo, name, app, previous, null, ex.Message, where);
                throw;
            }
            Record("app.env.gitlab.removed", owner, repo, name, app, previous, null, error: null, where);
            return;
        }
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
    private void Record(string action, string owner, string repo, string name, AppDefinition? app, string? before, string? after, string? error, string? scope = null)
    {
        if (audit is null) return;
        var removing = action.EndsWith(".removed", StringComparison.Ordinal);
        var secret = Masking.IsSecretName(name) || (before is not null && Masking.IsSecret(name, before)) || (after is not null && Masking.IsSecret(name, after));
        var gitLabProject = RepoRef.IsGitLab(owner);
        var where = RepoRef.FullNameOf(owner, repo);
        var noun = secret ? "secret variable" : "variable";
        var store = gitLabProject ? $"{where}'s CI/CD variables in GitLab{(scope is null or AllScope ? "" : $" (scope {scope})")}" : $"{where}'s variables in GitHub";
        // Who gets it: every app from a GitHub repo; a GitLab variable by its scope.
        var reach = !gitLabProject ? "every app from the repo gets it at its next deploy"
            : scope == PreviewScope ? "every preview of an app from the project gets it at its next deploy"
            : scope == ProductionScope ? "every app from the project gets it at its next deploy, and its previews don't"
            : "every app from the project, and every preview of one, gets it at its next deploy";
        var summary = error is null
            ? removing
                ? $"Deleted the {noun} {name} from {store}: no app from the repo gets it from there now."
                : $"Saved the {noun} {name} to {store}: {reach}."
            : removing
                ? $"Couldn't delete the {noun} {name} from {store}: {Audit.AuditLog.Clean(error, 300)?.TrimEnd('.')}."
                : $"Couldn't save the {noun} {name} to {store}: {Audit.AuditLog.Clean(error, 300)?.TrimEnd('.')}.";
        var changes = new List<Audit.AuditChange>
        {
            new(gitLabProject ? "Project" : "Repo", null, where),
            new("Value", secret ? null : Shown(before), secret || removing ? null : Shown(after)),
            new("Secret", null, Audit.AuditText.OnOff(secret)),
        };
        if (gitLabProject) changes.Add(new("Environment scope", null, scope ?? AllScope));
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

    /// <summary>GitLab's rules for variable keys (letters, digits and underscores, at most 255), and a name an app can read.</summary>
    public static string? GitLabProblem(string name)
    {
        name = name.Trim();
        if (!GitHubName().IsMatch(name) || name.Length > 255) return "Use letters, digits and underscores, not starting with a digit.";
        return null;
    }

    /// <summary>Why Gitberth doesn't save a GitLab variable under this environment scope; null for *, production and preview/*.</summary>
    public static string? ScopeProblem(string scope) => scope is AllScope or ProductionScope or PreviewScope
        ? null
        : "Gitberth saves GitLab variables for every deploy (*), production, or previews (preview/*).";

    private async Task<string> TokenAsync(CancellationToken ct) =>
        await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) ?? throw new GitHubApiException("No GitHub token is saved. Add one in Settings.");

    private async Task<GitLabConnection> GitLabConnectionAsync(CancellationToken ct)
    {
        if (gitLab is null) throw new GitLabApiException("GitLab isn't set up in this Gitberth.");
        var conn = await gitLab.ConnectionAsync(ct);
        return conn.HasToken ? conn : throw new GitLabApiException("No GitLab token is saved. Add one in Settings › GitLab.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex GitHubName();
}
