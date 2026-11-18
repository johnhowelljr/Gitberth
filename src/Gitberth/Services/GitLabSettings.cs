using System.Globalization;
using Gitberth.Git;

namespace Gitberth.Services;

/// <summary>Settings › GitLab as it's saved: the address, and what the last check said about the token (never the token).</summary>
/// <param name="Instance">GitLab's address: gitlab.com unless another is set.</param>
/// <param name="Scopes">The token's scopes as the last check read them; null when GitLab didn't say.</param>
public sealed record GitLabStatus(string Instance, bool TokenSaved, string? Login, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt)
{
    /// <summary>The scopes the token lacks, each with what it's needed for; empty when it has them all, or they aren't known.</summary>
    public IReadOnlyList<(string Scope, string For)> Missing => Scopes is null ? [] : GitLabScopes.Missing(Scopes);
}

/// <summary>What a save or a check of Settings › GitLab did, in a sentence, and what GitLab said about the token.</summary>
/// <param name="Ok">It was saved (or checked); false when nothing changed, and <paramref name="Message"/> says why.</param>
public sealed record GitLabSave(bool Ok, string Message, GitLabTokenCheck? Check = null);

/// <summary>
/// Settings › GitLab: Gitberth's GitLab's address (gitlab.com, or a self-hosted one), and its token, kept encrypted and never
/// shown again. A token is checked before it's saved (who it is, and which scopes it lacks for what), and refused when
/// GitLab refuses it. Each change is in the audit log, without the token; a new token or address lets every GitLab feature
/// be tried again (<see cref="GitLabIntegrationState"/>).
/// </summary>
public sealed class GitLabSettings(SettingsService settings, GitLabApi api, GitLabIntegrationState state, Audit.AuditLog? audit = null)
{
    public async Task<GitLabStatus> StatusAsync(CancellationToken ct = default)
    {
        var instance = await api.InstanceAsync(ct);
        var scopes = await settings.GetAsync(SettingKeys.GitLabScopes, ct) is { Length: > 0 } s
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var expires = DateTimeOffset.TryParse(await settings.GetAsync(SettingKeys.GitLabTokenExpires, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e)
            ? e : (DateTimeOffset?)null;
        return new GitLabStatus(instance, await settings.GetAsync(SettingKeys.GitLabToken, ct) is not null, await settings.GetAsync(SettingKeys.GitLabLogin, ct), scopes, expires);
    }

    /// <summary>
    /// Saves the address (empty: gitlab.com) and, when one is given, a new token, once GitLab took it. A new address with
    /// a token saved already checks that token there too, and says what GitLab said.
    /// </summary>
    public async Task<GitLabSave> SaveAsync(string? address, string? newToken, CancellationToken ct = default)
    {
        if (GitLabRepoRef.NormalizeInstance(address, out var problem) is not { } instance) return new GitLabSave(false, problem ?? "That isn't GitLab's address.");
        var before = await api.InstanceAsync(ct);
        var moved = !string.Equals(before, instance, StringComparison.OrdinalIgnoreCase);
        var token = string.IsNullOrWhiteSpace(newToken) ? null : newToken.Trim();
        var saved = await settings.GetSecretAsync(SettingKeys.GitLabToken, ct);
        GitLabTokenCheck? check = null;
        if (token is not null)
        {
            check = await api.CheckTokenAsync(instance, token, ct);
            if (!check.Valid)
            {
                audit?.Record(new Audit.AuditEntry("settings.gitlab.token.saved", $"Didn't save the GitLab token: {HostOf(instance)} refused it.")
                {
                    Target = "GitLab token", Outcome = Audit.AuditOutcome.Failed,
                });
                return new GitLabSave(false, check.Warning ?? "GitLab refused the token.", check);
            }
        }
        if (moved)
        {
            await settings.SetAsync(SettingKeys.GitLabUrl, instance == GitLabRepoRef.DefaultInstance ? null : instance, ct);
            // The hooks Gitberth kept were the old GitLab's: on the new one, only a hook with Gitberth's URL counts as its own.
            await settings.SetAsync(SettingKeys.GitLabWebhookIds, null, ct);
            audit?.Record(new Audit.AuditEntry("settings.gitlab.changed", $"Gitberth's GitLab is {HostOf(instance)} now (it was {HostOf(before)}): GitLab apps clone, and are reported, there.")
            {
                Target = "GitLab", Changes = [new("Address", before, instance)],
            });
        }
        if (token is not null)
        {
            await KeepAsync(token, check!, ct);
            audit?.Record(new Audit.AuditEntry("settings.gitlab.token.saved",
                $"{(saved is null ? "Saved" : "Replaced")} the GitLab token{(check!.Username is null ? "" : $" for {check.Username}")}.")
            {
                Target = "GitLab token",
                Changes = [new("Token", saved is null ? null : "(saved)", "(saved)"), .. check.Scopes is { } scopes ? new[] { new Audit.AuditChange("Scopes", null, string.Join(", ", scopes)) } : []],
            });
        }
        else if (moved && saved is not null)
        {
            // The same token at a new address: checked there, and what's known about it follows.
            check = await api.CheckTokenAsync(instance, saved, ct);
            await KeepCheckAsync(check, ct);
        }
        if (moved || token is not null) state.Clear();
        var message = (moved, token is not null) switch
        {
            (true, true) => $"Saved: Gitberth's GitLab is {HostOf(instance)}, with the new token.",
            (true, false) => $"Saved: Gitberth's GitLab is {HostOf(instance)}.",
            (false, true) => "Token saved.",
            _ => "Nothing to save: that's GitLab's address already.",
        };
        return new GitLabSave(moved || token is not null, message, check);
    }

    /// <summary>Checks the saved token again (who it is, its scopes, its expiry), and keeps what GitLab said.</summary>
    public async Task<GitLabSave> CheckAsync(CancellationToken ct = default)
    {
        if (await settings.GetSecretAsync(SettingKeys.GitLabToken, ct) is not { } token) return new GitLabSave(false, "No GitLab token is saved.");
        var check = await api.CheckTokenAsync(await api.InstanceAsync(ct), token, ct);
        await KeepCheckAsync(check, ct);
        if (check.Valid) state.Clear();
        return new GitLabSave(check.Valid, check.Valid ? $"GitLab took the token{(check.Username is null ? "" : $": it's {check.Username}'s")}." : check.Warning ?? "GitLab refused the token.", check);
    }

    /// <summary>Forgets the token, and what was known about it.</summary>
    public async Task RemoveTokenAsync(CancellationToken ct = default)
    {
        var was = await settings.GetAsync(SettingKeys.GitLabLogin, ct);
        foreach (var key in new[] { SettingKeys.GitLabToken, SettingKeys.GitLabLogin, SettingKeys.GitLabScopes, SettingKeys.GitLabTokenExpires })
            await settings.SetAsync(key, null, ct);
        audit?.Record(new Audit.AuditEntry("settings.gitlab.token.removed",
            $"Removed the GitLab token{(was is null ? "" : $" of {was}")}: private projects can't be cloned, and GitLab hears nothing from Gitberth, until another is saved.")
        {
            Target = "GitLab token",
        });
        state.Clear();
    }

    private async Task KeepAsync(string token, GitLabTokenCheck check, CancellationToken ct)
    {
        await settings.SetSecretAsync(SettingKeys.GitLabToken, token, ct);
        await KeepCheckAsync(check, ct);
    }

    // Who the token is, its scopes and its expiry, as the check said; what it couldn't say is forgotten, never guessed.
    private async Task KeepCheckAsync(GitLabTokenCheck check, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.GitLabLogin, check.Username, ct);
        await settings.SetAsync(SettingKeys.GitLabScopes, check.Scopes is { Count: > 0 } scopes ? string.Join(',', scopes) : null, ct);
        await settings.SetAsync(SettingKeys.GitLabTokenExpires, check.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture), ct);
    }

    private static string HostOf(string instance) => new GitLabConnection(instance, null).Host;
}
