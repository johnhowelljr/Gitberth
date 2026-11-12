using System.Collections.Concurrent;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Domains;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// Keeps Gitberth's webhook on every repo it deploys, once instant deploys have a host name: added where it's missing,
/// and set again (URL, secret, events) where Gitberth's is there already (the one whose ID it recorded there, or one with
/// its payload URL), so a repo added later gets it too and a moved host name follows. Any other webhook is left alone,
/// even on the same path or on a host name Gitberth had before: another Gitberth's, say. A webhook follows a new host
/// name in the first run after the change or not at all: when that run can't move it (no token, GitHub refuses or
/// fails), its ID is forgotten and it stays where it is, since the old name may be another Gitberth's by the next run,
/// and so may the webhook; a new one is added once GitHub lets Gitberth. A repo's ID is forgotten too once no app deploys
/// it, and all of them while instant deploys are off: the webhook stays where it was on GitHub, so it may be another
/// Gitberth's by the time it would be moved. Each repo is done once per host name while Gitberth runs; then every 10
/// minutes it only looks for new repos. A repo GitHub refuses (403 or 404) says so in Settings' list and rests on its
/// own; a token GitHub refuses (401) is said once in Settings and stops them all for a while. Settings also shows what to
/// paste into GitHub by hand.
/// </summary>
/// <param name="domains">The tunnel: a webhook is added or moved once cloudflared runs a route written just before, so GitHub's first ping gets through.</param>
public sealed class WebhookKeeper(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, GitHubWrites github, GitHubIntegrationState state,
    ILogger<WebhookKeeper> logger, DomainService? domains = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>How recent a change to the tunnel is waited for before a webhook is added or moved.</summary>
    internal static readonly TimeSpan RecentRoute = TimeSpan.FromMinutes(2);

    /// <summary>How long that wait may take; then the webhook is added anyway (its later deliveries get through).</summary>
    internal TimeSpan RouteWait { get; set; } = TimeSpan.FromSeconds(30);

    // GitHub pings a webhook the moment it's made, through the route Gitberth wrote to the tunnel just before (a host name
    // set a moment ago): cloudflared takes a new configuration a few seconds later, and until then the ping would get 404.
    private async Task RouteRunsAsync(CancellationToken ct)
    {
        if (domains?.LastTunnelChange is not { } change || Now() - change.At > RecentRoute) return;
        if (!await domains.WaitForTunnelAsync(change.Version, change.At, RouteWait, ct))
            logger.LogInformation("cloudflared didn't confirm the webhook's route within {Seconds} seconds; adding the webhook anyway", RouteWait.TotalSeconds);
    }

    /// <summary>A webhook Gitberth added or took over on a repo: its ID, and the payload URL Gitberth last set on it.</summary>
    internal sealed record KeptHook(long Id, string Url);

    // Repos whose webhook was set up, with the URL it was set up for. One whose webhook isn't recorded with that URL (it
    // was forgotten, or GitHub didn't say its ID) is set up again, which finds it by its payload URL.
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

    /// <summary>
    /// The repo's webhook that is Gitberth's: the one it added or took over there (<paramref name="kept"/>) while that still
    /// has the payload URL Gitberth last set on it (or the one Gitberth has now), else one with its payload URL, which is
    /// how one added before Gitberth recorded them is found. Null when there's none: any other is someone else's, even on
    /// the same path or on a host name Gitberth had before, and is never changed.
    /// </summary>
    internal static RepoHook? Ours(IReadOnlyList<RepoHook> hooks, string payloadUrl, KeptHook? kept) =>
        hooks.FirstOrDefault(h => kept is not null && h.Id == kept.Id && (SameUrl(h.Url, kept.Url) || SameUrl(h.Url, payloadUrl)))
        ?? hooks.FirstOrDefault(h => SameUrl(h.Url, payloadUrl));

    private static bool SameUrl(string? hookUrl, string url) =>
        hookUrl is not null && string.Equals(hookUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The webhooks Gitberth recorded, by owner/repo in lower case (<see cref="SettingKeys.GitHubWebhookIds"/>).</summary>
    internal static Dictionary<string, KeptHook> KeptHooks(string? saved)
    {
        var kept = new Dictionary<string, KeptHook>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (saved is not null && JsonSerializer.Deserialize<Dictionary<string, KeptHook>>(saved) is { } parsed)
                foreach (var (repo, hook) in parsed.Where(p => p.Value is { Url.Length: > 0 })) kept[repo] = hook;
        }
        catch (JsonException)
        {
            // Unreadable: none is known, so only a webhook with Gitberth's payload URL counts as its own.
        }
        return kept;
    }

    // What's recorded, as the setting keeps it; null when nothing is.
    private static string? AsSetting(Dictionary<string, KeptHook> kept) => kept.Count == 0 ? null
        : JsonSerializer.Serialize(kept.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));

    /// <summary>
    /// Sets the webhook up on each repo not done yet for the current host name. Nothing happens on GitHub without domains,
    /// a webhook host name, its secret and a GitHub token, or while GitHub refuses the token. A repo GitHub refused waits
    /// its turn. What's recorded is forgotten without a host name, for a repo no app deploys any more, and for a webhook
    /// this run left on an earlier host name.
    /// </summary>
    public async Task EnsureAllAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            var saved = await settings.GetAsync(SettingKeys.GitHubWebhookIds, ct);
            var kept = KeptHooks(saved);
            async Task SaveAsync()
            {
                var now = AsSetting(kept);
                if (now == saved) return;
                await settings.SetAsync(SettingKeys.GitHubWebhookIds, now, ct);
                saved = now;
            }
            // Instant deploys are off: the webhooks stay where they are, and another Gitberth may take over that host name
            // and them meanwhile. Back on, only one with the payload URL counts as Gitberth's again.
            if (await settings.GetAsync(SettingKeys.GitHubWebhookHost, ct) is not { } host)
            {
                kept.Clear();
                await SaveAsync();
                return;
            }
            var url = DomainService.WebhookUrl(host);
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
            // A repo no app deploys any more keeps its webhook where it is, so it's no longer Gitberth's to move: deployed
            // again, only one with the payload URL counts as Gitberth's, else a new one is added. Without a token too.
            var deployed = repos.Select(r => $"{r.Owner}/{r.Repo}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in kept.Keys.Where(k => !deployed.Contains(k)).ToList()) kept.Remove(gone);
            if (await settings.GetAsync(SettingKeys.DomainsEnabled, ct) == "true"
                && await settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret, ct) is { } secret
                && await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is { } token)
                await SetUpAsync(repos, url, secret, token, kept, SaveAsync, ct);
            // One still on an earlier host name was Gitberth's to move in this run only. One it couldn't move stays where
            // it is: that name may be another Gitberth's from now on, and so may the webhook. A new one is added instead.
            foreach (var left in kept.Where(k => !SameUrl(k.Value.Url, url)).Select(k => k.Key).ToList()) kept.Remove(left);
            await SaveAsync();
        }
        finally
        {
            _one.Release();
        }
    }

    // Sets the webhook up on each repo not done yet for this payload URL, and records it with that URL (save writes it).
    private async Task SetUpAsync(List<(string Owner, string Repo)> repos, string url, string secret, string token, Dictionary<string, KeptHook> kept,
        Func<Task> save, CancellationToken ct)
    {
        if (_done.Values.Any(u => u != url))
        {
            _done.Clear();
            state.ForgetHooks();
        }
        var routed = false;
        foreach (var (owner, repo) in repos)
        {
            var name = $"{owner}/{repo}";
            var key = name.ToLowerInvariant();
            var known = kept.GetValueOrDefault(key);
            if (_done.TryGetValue(name, out var doneFor) && doneFor == url && known is not null && SameUrl(known.Url, url)) continue;
            // A refused token stops every repo; a refused repo rests on its own, and the others carry on.
            if (state.Paused(GitHubFeature.Webhooks, Now())) return;
            if (state.Paused(GitHubFeature.Webhooks, name, Now())) continue;
            if (!routed)
            {
                await RouteRunsAsync(ct);
                routed = true;
            }
            try
            {
                var existing = Ours(await github.ListHooksAsync(token, owner, repo, ct), url, known);
                // Recorded with this URL, so this webhook follows the next host name, and none on an old one is taken for it.
                if (await github.SaveHookAsync(token, owner, repo, existing?.Id, url, secret, ct) is long id) kept[key] = new KeptHook(id, url);
                else kept.Remove(key);
                await save();
                _done[name] = url;
                state.Worked(GitHubFeature.Webhooks, name);
                state.Hook(new HookResult(name, true, existing is null ? "Webhook added" : "Webhook set up", Now()));
            }
            catch (GitHubWriteException ex) when (ex.BadToken)
            {
                state.Hook(new HookResult(name, false, ex.Message, Now()));
                if (state.Report(GitHubFeature.Webhooks, ex.Message, Now())) logger.LogWarning("GitHub refused Gitberth's token: {Problem}", ex.Message);
                return;
            }
            catch (GitHubWriteException ex) when (ex.MissingPermission)
            {
                state.Hook(new HookResult(name, false, ex.Message, Now()));
                if (state.Refused(GitHubFeature.Webhooks, name, ex.Message, Now())) logger.LogWarning("GitHub refused Gitberth's webhook on {Repo}: {Problem}", name, ex.Message);
            }
            catch (GitHubWriteException ex)
            {
                state.Hook(new HookResult(name, false, ex.Message, Now()));
            }
        }
    }
}
