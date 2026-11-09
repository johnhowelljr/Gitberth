using System.Collections.Concurrent;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Domains;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// Keeps Gitberth's webhook on every repo it deploys, once instant deploys have a host name: added where it's missing,
/// and set again (URL, secret, events) where one with Gitberth's path is there already, so a repo added later gets it
/// too and a moved host name follows. Each repo is done once per host name while Gitberth runs; then every 10 minutes it
/// only looks for new repos. A token without Webhooks: Read and write is said once in Settings, which also shows what to
/// paste into GitHub by hand.
/// </summary>
public sealed class WebhookKeeper(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, GitHubWrites github, GitHubIntegrationState state,
    ILogger<WebhookKeeper> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    // Repos whose webhook was set up, with the URL it was set up for.
    private readonly ConcurrentDictionary<string, string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _one = new(1, 1);

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Look now: the host name, the token or the apps changed.</summary>
    public void Poke()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EnsureAllAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Setting up GitHub's webhooks failed");
                }
                await _wake.WaitAsync(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether a webhook's URL is Gitberth's: the same payload URL, or Gitberth's path on another host name it had before.</summary>
    internal static bool Ours(string? hookUrl, string payloadUrl) =>
        hookUrl is not null && (string.Equals(hookUrl.TrimEnd('/'), payloadUrl, StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(hookUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/') == "/hooks/github"));

    /// <summary>
    /// Sets the webhook up on each repo not done yet for the current host name. Nothing happens without domains, a webhook
    /// host name, its secret and a GitHub token, or while the token is known to lack the permission.
    /// </summary>
    public async Task EnsureAllAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (await settings.GetAsync(SettingKeys.DomainsEnabled, ct) != "true"
                || await settings.GetAsync(SettingKeys.GitHubWebhookHost, ct) is not { } host
                || await settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret, ct) is not { } secret
                || await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is not { } token) return;
            var url = DomainService.WebhookUrl(host);
            if (_done.Values.Any(u => u != url))
            {
                _done.Clear();
                state.ForgetHooks();
            }
            List<(string Owner, string Repo)> repos;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                repos = (await db.Apps.AsNoTracking().Where(a => a.Source == AppSource.Git && a.Owner != "" && a.Repo != "")
                        .Select(a => new { a.Owner, a.Repo }).ToListAsync(ct))
                    .Select(a => (a.Owner, a.Repo))
                    .DistinctBy(a => $"{a.Owner}/{a.Repo}".ToLowerInvariant())
                    .OrderBy(a => $"{a.Owner}/{a.Repo}", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            foreach (var (owner, repo) in repos)
            {
                var name = $"{owner}/{repo}";
                if (_done.TryGetValue(name, out var doneFor) && doneFor == url) continue;
                if (state.Paused(GitHubFeature.Webhooks, Now())) return;
                try
                {
                    var existing = (await github.ListHooksAsync(token, owner, repo, ct)).FirstOrDefault(h => Ours(h.Url, url));
                    await github.SaveHookAsync(token, owner, repo, existing?.Id, url, secret, ct);
                    _done[name] = url;
                    state.Worked(GitHubFeature.Webhooks);
                    state.Hook(new HookResult(name, true, existing is null ? "Webhook added" : "Webhook set up", Now()));
                }
                catch (GitHubWriteException ex) when (ex.MissingPermission)
                {
                    state.Hook(new HookResult(name, false, ex.Message, Now()));
                    if (state.Report(GitHubFeature.Webhooks, ex.Message, Now())) logger.LogWarning("GitHub refused Gitberth's token: {Problem}", ex.Message);
                    return;
                }
                catch (GitHubWriteException ex)
                {
                    state.Hook(new HookResult(name, false, ex.Message, Now()));
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }
}
