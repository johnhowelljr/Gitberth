using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Git;
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
/// own; a token GitHub refuses (401) is said once in Settings and stops them all for a while. One GitHub failed for a
/// moment (no answer in time, no connection, a 5xx, a 201 that didn't say which webhook it made) is tried again soon: 15
/// and 30 seconds, then 1, 2 and 5 minutes after, before the 10-minute look takes over. Such a quick try looks at the repos
/// due one only; a rate limit is the token's, so it ends the host's look, and its repos wait for the next 10-minute one.
/// A POST whose answer was lost may have made the webhook all the same: the next try lists first and finds it by its
/// payload URL, so it's never added twice, and says it was added. Settings also shows what to paste into GitHub by hand.
/// A GitLab project gets Gitberth's project hook the same way (push and merge request events, its own secret token, at
/// /hooks/gitlab on the same host name), with GitLab's token, IDs and state of its own. GitLab makes a second hook with the
/// same URL when asked again (GitHub refuses one), so after a POST whose answer was lost it's asked again a minute later at
/// the soonest, once that POST is surely done; and a second copy of Gitberth's hook found on a project is removed.
/// </summary>
/// <param name="domains">The tunnel: a webhook is added or moved once cloudflared runs a route written just before, so GitHub's first ping gets through.</param>
/// <param name="audit">Where webhooks added on GitHub and moved to a new host name are recorded, and the ones GitHub refused.</param>
/// <param name="gitLab">GitLab's API, for GitLab projects' hooks; without it (in older tests) they're left alone.</param>
/// <param name="gitLabState">What GitLab refused, as Settings › GitLab shows it.</param>
public sealed class WebhookKeeper(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, GitHubWrites github, GitHubIntegrationState state,
    ILogger<WebhookKeeper> logger, DomainService? domains = null, Audit.AuditLog? audit = null, GitLabApi? gitLab = null, GitLabIntegrationState? gitLabState = null)
    : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How soon a webhook the host failed for a moment is set up again, after each such failure in a row: 15 s, 30 s, then
    /// 1, 2 and 5 minutes; after those, at the 10-minute look. The first comes 15 s after the client gave up (30 s after
    /// the request), so a request the host was still finishing is done by then.
    /// </summary>
    internal static readonly TimeSpan[] SoonAfter =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// The soonest GitLab is asked again after a POST of the hook whose answer was lost. GitLab may still be making that hook
    /// (a request it's busy with can take up to a minute there), and asked again before it's done, it makes a second one.
    /// </summary>
    internal static readonly TimeSpan GitLabAfterLostPost = TimeSpan.FromMinutes(1);

    // The shortest wait between two looks, so a retry that came due while a look ran never makes the keeper spin.
    private static readonly TimeSpan Least = TimeSpan.FromSeconds(1);

    /// <summary>How long after Gitberth starts the first look runs.</summary>
    internal TimeSpan FirstLookAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The clock its waits go by, and <see cref="Now"/> unless that's set; tests hold it.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How recent a change to the tunnel is waited for before a webhook is added or moved.</summary>
    internal static readonly TimeSpan RecentRoute = TimeSpan.FromMinutes(2);

    /// <summary>How long that wait may take; then the webhook is added anyway (its later deliveries get through).</summary>
    internal TimeSpan RouteWait { get; set; } = TimeSpan.FromSeconds(30);

    // GitHub pings a webhook the moment it's made, through the route Gitberth wrote to the tunnel just before (a host name
    // set a moment ago): cloudflared takes a new configuration a few seconds later, and until then the ping would get 404.
    private async Task RouteRunsAsync(CancellationToken ct)
    {
        // A host name saved a moment ago is saved before the sync that writes its route: waiting for any domain change under
        // way to finish means the route's tunnel change is the one recorded below, not an older one.
        if (domains is not null) await domains.SettledAsync(ct);
        if (domains?.LastTunnelChange is not { } change || Now() - change.At > RecentRoute) return;
        if (!await domains.WaitForTunnelAsync(change.Version, change.At, RouteWait, ct))
            logger.LogInformation("cloudflared didn't confirm the webhook's route within {Seconds} seconds; adding the webhook anyway", RouteWait.TotalSeconds);
    }

    /// <summary>A webhook Gitberth added or took over on a repo: its ID, and the payload URL Gitberth last set on it.</summary>
    internal sealed record KeptHook(long Id, string Url);

    /// <summary>
    /// One host's webhooks, as the keeper keeps them: where their IDs and secret are saved, the URL they send to, which apps'
    /// repos are its, what it refused (its state), and how it's called with the token (null when none is saved).
    /// </summary>
    private sealed record HookHost(string Name, string AuditName, string IdsKey, string SecretKey, GitHubIntegrationState State, Func<string, string> UrlOf,
        bool GitLab, Func<CancellationToken, Task<HookCalls?>> ConnectAsync);

    /// <summary>
    /// A host's calls for one repo (by the app's Owner and Repo), with its token. Delete removes a second copy of its hook:
    /// GitLab's only (GitHub never makes one).
    /// </summary>
    private sealed record HookCalls(Func<string, string, CancellationToken, Task<List<RepoHook>>> List,
        Func<string, string, long?, string, string, CancellationToken, Task<long?>> Save, Func<string, string, long, CancellationToken, Task>? Delete = null);

    // Repos whose webhook was set up, with the URL it was set up for, by host. One whose webhook isn't recorded with that
    // URL (it was forgotten, or the host didn't say its ID) is set up again, which finds it by its payload URL.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _done = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _one = new(1, 1);

    // Completed by a poke: the wait after the look running now ends, for a full look. Replaced before each look.
    private TaskCompletionSource _wake = NewWake();

    // When the next full look is due: 10 minutes after the last one ended. A look before then that a quick try's time
    // woke looks at the repos due one only.
    private DateTime? _fullAt;

    /// <summary>
    /// A repo whose webhook the host failed for a moment, for this payload URL: how many times in a row, and when it's
    /// tried again (null once the quick tries are used up).
    /// </summary>
    private sealed record Retry(int Count, DateTime? At, string Url);

    // The repos due a quick try, by host and repo (owner/repo in lower case); each look writes its host's afresh, so a repo
    // that worked, rests, or isn't deployed any more is never tried early.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Retry>> _retries = new(StringComparer.Ordinal);

    /// <summary>A POST of the webhook whose outcome Gitberth didn't hear (or that didn't say the webhook's ID): Said, when its audit entry is recorded already.</summary>
    private sealed record Posted(string Url, bool Said);

    // Repos Gitberth POSTed its webhook to without hearing back, by host: found at the next try by the payload URL, the
    // webhook was added, not set up.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Posted>> _posted = new(StringComparer.Ordinal);

    private Func<DateTime>? _now;

    internal Func<DateTime> Now
    {
        get => _now ??= () => Time.GetUtcNow().UtcDateTime;
        set => _now = value;
    }

    private ConcurrentDictionary<string, string> Done(HookHost host) => _done.GetOrAdd(host.Name, _ => new(StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<string, Retry> NoRetries = new Dictionary<string, Retry>();

    /// <summary>
    /// How long until the keeper looks again by itself: at the next full look (10 minutes after the last), or sooner when a
    /// webhook that failed for a moment is due a quick try.
    /// </summary>
    internal TimeSpan NextLook()
    {
        var now = Now();
        var next = _fullAt ?? now + Every;
        foreach (var at in _retries.Values.SelectMany(r => r.Values).Select(r => r.At).OfType<DateTime>())
            if (at < next) next = at;
        var wait = next - now;
        return wait < Least ? Least : wait > Every ? Every : wait;
    }

    /// <summary>Look now, at every repo: the host name, the token or the apps changed.</summary>
    public void Poke() => Volatile.Read(ref _wake).TrySetResult();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Gitberth's own work, through this job: what it changes is recorded so in the audit log.
        using var auditActor = Gitberth.Audit.AuditScope.Begin(Gitberth.Audit.AuditActor.System(Gitberth.Audit.AuditVia.Sync));
        try
        {
            await Task.Delay(FirstLookAfter, Time, stoppingToken);
            var full = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                // Taken before the look: a poke while it runs ends the wait after it.
                Interlocked.Exchange(ref _wake, NewWake());
                try
                {
                    if (full) await EnsureAllAsync(stoppingToken);
                    else await RetryDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Setting up GitHub's webhooks failed");
                    // A look that failed as a whole (its database, say) waits for the next one, never a quick try.
                    _retries.Clear();
                }
                // Poked, or the full look's time: every repo. Only a quick try's time: the repos due one.
                full = await WaitAsync(NextLook(), stoppingToken) || Now() >= _fullAt;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Until the next look's time, or a poke: true when poked.
    private async Task<bool> WaitAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        var poked = Volatile.Read(ref _wake).Task;
        if (!poked.IsCompleted)
        {
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(poked, Task.Delay(wait, Time, cut.Token));
            // Poked early: the wait's timer goes now, not at its time.
            cut.Cancel();
        }
        stoppingToken.ThrowIfCancellationRequested();
        return poked.IsCompleted;
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>The webhooks Gitberth recorded, by owner/repo in lower case (<see cref="SettingKeys.GitHubWebhookIds"/>), or GitLab's by project path.</summary>
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

    // GitHub: the token and its calls, as they've always been.
    private HookHost GitHubHost => new("GitHub", "github", SettingKeys.GitHubWebhookIds, SettingKeys.GitHubWebhookSecret, state, DomainService.WebhookUrl,
        GitLab: false, async ct => await settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is { } token
            ? new HookCalls((owner, repo, c) => github.ListHooksAsync(token, owner, repo, c),
                (owner, repo, id, url, secret, c) => github.SaveHookAsync(token, owner, repo, id, url, secret, c))
            : null);

    // GitLab: its token and instance, its project hooks (push and merge request events) under its own secret token.
    private HookHost GitLabHost => new("GitLab", "gitlab", SettingKeys.GitLabWebhookIds, SettingKeys.GitLabWebhookSecret, gitLabState!, DomainService.GitLabWebhookUrl,
        GitLab: true, async ct =>
        {
            var conn = await gitLab!.ConnectionAsync(ct);
            if (!conn.HasToken) return null;
            return new HookCalls(
                async (owner, repo, c) => (await gitLab.ListHooksAsync(conn, (GitLabRepoRef)RepoRef.Of(owner, repo), c))
                    .Select(h => new RepoHook(h.Id, h.Url, Active: true, [.. h.PushEvents ? new[] { "push" } : [], .. h.MergeRequestsEvents ? new[] { "merge_requests" } : []]))
                    .ToList(),
                (owner, repo, id, url, secret, c) => gitLab.SaveHookAsync(conn, (GitLabRepoRef)RepoRef.Of(owner, repo), id, url, secret, c),
                (owner, repo, id, c) => gitLab.DeleteHookAsync(conn, (GitLabRepoRef)RepoRef.Of(owner, repo), id, c));
        });

    /// <summary>
    /// Sets the webhook up on each repo not done yet for the current host name, on GitHub and on GitLab. Nothing happens on a
    /// host without domains, a webhook host name, its secret and the host's token, or while the host refuses the token. A
    /// repo the host refused waits its turn. What's recorded is forgotten without a host name, for a repo no app deploys any
    /// more, and for a webhook this run left on an earlier host name.
    /// </summary>
    public async Task EnsureAllAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            await EnsureHostAsync(GitHubHost, quick: false, ct);
            if (gitLab is not null && gitLabState is not null) await EnsureHostAsync(GitLabHost, quick: false, ct);
        }
        finally
        {
            _fullAt = Now() + Every;
            _one.Release();
        }
    }

    /// <summary>
    /// A quick look: on each host, only the repos whose quick try is due now are set up again; every other repo waits for
    /// its own quick try or for the full look (one the host rate-limited or refused, one not tried yet). A host asks nothing
    /// when none of its repos is due. One whose host name changed since gets its full look instead, so each of its webhooks
    /// follows the new name in the first look after the change.
    /// </summary>
    internal async Task RetryDueAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            await EnsureHostAsync(GitHubHost, quick: true, ct);
            if (gitLab is not null && gitLabState is not null) await EnsureHostAsync(GitLabHost, quick: true, ct);
        }
        finally
        {
            _one.Release();
        }
    }

    // The host's repos whose quick try is due now, by repo in lower case.
    private Dictionary<string, Retry> DueNow(HookHost hookHost)
    {
        var now = Now();
        return (_retries.GetValueOrDefault(hookHost.Name) ?? NoRetries).Where(r => r.Value.At is { } at && at <= now)
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureHostAsync(HookHost hookHost, bool quick, CancellationToken ct)
    {
        var due = quick ? DueNow(hookHost) : null;
        if (due is { Count: 0 }) return;
        var saved = await settings.GetAsync(hookHost.IdsKey, ct);
        var kept = KeptHooks(saved);
        async Task SaveAsync()
        {
            var now = AsSetting(kept);
            if (now == saved) return;
            await settings.SetAsync(hookHost.IdsKey, now, ct);
            saved = now;
        }
        // Instant deploys are off: the webhooks stay where they are, and another Gitberth may take over that host name
        // and them meanwhile. Back on, only one with the payload URL counts as Gitberth's again.
        if (await settings.GetAsync(SettingKeys.GitHubWebhookHost, ct) is not { } host)
        {
            kept.Clear();
            _retries[hookHost.Name] = NoRetries;
            _posted.TryRemove(hookHost.Name, out _);
            await SaveAsync();
            return;
        }
        var url = hookHost.UrlOf(host);
        // The host name changed since the look that set these tries: this host gets its full look.
        if (due is not null && (due.Values.Any(r => !SameUrl(r.Url, url)) || Done(hookHost).Values.Any(u => u != url))) due = null;
        List<(string Owner, string Repo)> repos;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            repos = (await db.Apps.AsNoTracking().Where(a => a.Source == AppSource.Git && a.Owner != "" && a.Repo != "")
                    .Select(a => new { a.Owner, a.Repo }).ToListAsync(ct))
                .Where(a => RepoRef.IsGitLab(a.Owner) == hookHost.GitLab)
                .Select(a => (a.Owner, a.Repo))
                .DistinctBy(a => $"{a.Owner}/{a.Repo}".ToLowerInvariant())
                .OrderBy(a => $"{a.Owner}/{a.Repo}", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        // A repo no app deploys any more keeps its webhook where it is, so it's no longer Gitberth's to move: deployed
        // again, only one with the payload URL counts as Gitberth's, else a new one is added. Without a token too.
        var deployed = repos.Select(r => KeyOf(r.Owner, r.Repo)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in kept.Keys.Where(k => !deployed.Contains(k)).ToList()) kept.Remove(gone);
        if (await settings.GetAsync(SettingKeys.DomainsEnabled, ct) == "true"
            && await SecretAsync(hookHost, ct) is { } secret
            && await hookHost.ConnectAsync(ct) is { } calls)
            await SetUpAsync(hookHost, repos, url, secret, calls, kept, SaveAsync, due, ct);
        else
            // Nothing can be set up now: a saved token or domains turned on again pokes the keeper.
            _retries[hookHost.Name] = NoRetries;
        // One still on an earlier host name was Gitberth's to move in this run only. One it couldn't move stays where
        // it is: that name may be another Gitberth's from now on, and so may the webhook. A new one is added instead.
        foreach (var left in kept.Where(k => !SameUrl(k.Value.Url, url)).Select(k => k.Key).ToList()) kept.Remove(left);
        await SaveAsync();
    }

    // The host's secret: GitHub's is made with the webhook's host name; GitLab's too, else here for a host name set before
    // Gitberth knew GitLab.
    private async Task<string?> SecretAsync(HookHost hookHost, CancellationToken ct)
    {
        if (await settings.GetSecretAsync(hookHost.SecretKey, ct) is { } secret) return secret;
        if (!hookHost.GitLab) return null;
        await settings.SetSecretAsync(hookHost.SecretKey, WebhookSignature.NewSecret(), ct);
        return await settings.GetSecretAsync(hookHost.SecretKey, ct);
    }

    // owner/repo, or a GitLab project's group/sub/project, as the repo is named in what's kept, said and shown.
    private static string KeyOf(string owner, string repo) => RepoRef.FullNameOf(owner, repo);

    // A webhook the host didn't take, once per repo and reason while Gitberth runs: the keeper tries again soon or at its
    // next look.
    private readonly ConcurrentDictionary<string, string> _refused = new(StringComparer.OrdinalIgnoreCase);

    // Recorded in the audit log the first time for this repo and reason; true then, so it's logged once too.
    private bool Refused(HookHost hookHost, string repo, string why)
    {
        var key = $"{hookHost.AuditName}|{repo}";
        if (_refused.TryGetValue(key, out var said) && said == why) return false;
        _refused[key] = why;
        audit?.Record(new Audit.AuditEntry($"{hookHost.AuditName}.webhook.added", $"Couldn't set up Gitberth's webhook on {repo}: {Audit.AuditLog.Clean(why, 300)?.TrimEnd('.')}.")
        {
            Target = repo, Outcome = Audit.AuditOutcome.Failed,
        });
        return true;
    }

    // "15 seconds", "a minute", "5 minutes": when the next try is, as Settings and the log say it.
    private static string In(TimeSpan wait) =>
        wait < TimeSpan.FromMinutes(1) ? $"{(int)wait.TotalSeconds} seconds" : wait == TimeSpan.FromMinutes(1) ? "a minute" : $"{(int)wait.TotalMinutes} minutes";

    // Sets the webhook up on each repo not done yet for this payload URL, and records it with that URL (save writes it). A
    // quick look (due: the repos due a quick try) sets up only those, and keeps the other repos' tries as they were.
    private async Task SetUpAsync(HookHost hookHost, List<(string Owner, string Repo)> repos, string url, string secret, HookCalls calls,
        Dictionary<string, KeptHook> kept, Func<Task> save, IReadOnlyDictionary<string, Retry>? due, CancellationToken ct)
    {
        var done = Done(hookHost);
        var hostState = hookHost.State;
        var before = _retries.GetValueOrDefault(hookHost.Name) ?? NoRetries;
        var posted = _posted.GetOrAdd(hookHost.Name, _ => new(StringComparer.OrdinalIgnoreCase));
        if (done.Values.Any(u => u != url))
        {
            done.Clear();
            hostState.ForgetHooks();
            // A new host name: every repo starts afresh.
            before = NoRetries;
            posted.Clear();
        }
        var deployed = repos.Select(r => KeyOf(r.Owner, r.Repo).ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in posted.Keys.Where(k => !deployed.Contains(k)).ToList()) posted.TryRemove(gone, out _);
        // This look's quick tries: a repo that fails for a moment now is tried again soon, and no other is (a quick look
        // keeps those of the repos it doesn't try).
        var next = new Dictionary<string, Retry>(StringComparer.OrdinalIgnoreCase);
        if (due is not null)
            foreach (var (key, retry) in before.Where(r => !due.ContainsKey(r.Key))) next[key] = retry;
        // The next quick try of a repo that failed for a moment again, and how long until it (never under least); at the
        // 10-minute look once the quick tries are used up. Failures for another payload URL (an earlier host name) don't count.
        TimeSpan Soon(string key, TimeSpan least = default)
        {
            var count = before.GetValueOrDefault(key) is { } earlier && SameUrl(earlier.Url, url) ? earlier.Count : 0;
            if (count >= SoonAfter.Length)
            {
                next[key] = new Retry(count, null, url);
                return Every;
            }
            var wait = SoonAfter[count] < least ? least : SoonAfter[count];
            next[key] = new Retry(count + 1, Now() + wait, url);
            return wait;
        }
        var routed = false;
        try
        {
            foreach (var (owner, repo) in repos)
            {
                var name = KeyOf(owner, repo);
                var key = name.ToLowerInvariant();
                if (due is not null && !due.ContainsKey(key)) continue;
                var known = kept.GetValueOrDefault(key);
                if (done.TryGetValue(name, out var doneFor) && doneFor == url && known is not null && SameUrl(known.Url, url)) continue;
                // A refused token stops every repo; a refused repo rests on its own, and the others carry on.
                if (hostState.Paused(GitHubFeature.Webhooks, Now())) return;
                if (hostState.Paused(GitHubFeature.Webhooks, name, Now())) continue;
                if (!routed)
                {
                    await RouteRunsAsync(ct);
                    routed = true;
                }
                var posting = false;
                try
                {
                    var hooks = await calls.List(owner, repo, ct);
                    var existing = Ours(hooks, url, known);
                    posting = existing is null;
                    var id = await calls.Save(owner, repo, existing?.Id, url, secret, ct);
                    // A POST before whose outcome Gitberth didn't hear: the webhook found now by its URL is the one it made.
                    var lost = posted.TryGetValue(key, out var earlier) && SameUrl(earlier.Url, url) ? earlier : null;
                    // Recorded with this URL, so this webhook follows the next host name, and none on an old one is taken for it.
                    if (id is long made)
                    {
                        kept[key] = new KeptHook(made, url);
                        posted.TryRemove(key, out _);
                    }
                    else
                    {
                        kept.Remove(key);
                        // Added, but the host didn't say which webhook it made: looked up soon by its payload URL, and recorded then.
                        if (posting)
                        {
                            posted[key] = new Posted(url, Said: true);
                            Soon(key);
                        }
                    }
                    await save();
                    var copiesGone = await RemoveCopiesAsync(hookHost, calls, owner, repo, name, [.. hooks.Where(h => h.Id != existing?.Id && SameUrl(h.Url, url))], ct);
                    // Done for this host name, unless a copy is left, or may yet come: POSTed again after a POST whose answer
                    // was lost, GitLab may still make that one's hook too. The next full look then finds it.
                    if (copiesGone && !(posting && lost is not null && calls.Delete is not null)) done[name] = url;
                    hostState.Worked(GitHubFeature.Webhooks, name);
                    var added = existing is null || lost is not null;
                    hostState.Hook(new HookResult(name, true, added ? "Webhook added" : "Webhook set up", Now()));
                    // Added, or moved to a new host name; set up again as it was (after a restart, say) changes nothing.
                    if (added && lost is not { Said: true })
                        audit?.Record(new Audit.AuditEntry($"{hookHost.AuditName}.webhook.added", $"Added Gitberth's webhook to {name} on {hookHost.Name}: pushes reach Gitberth at {url}.")
                        {
                            Target = name, Changes = [new("Payload URL", null, url)],
                        });
                    else if (!added && !SameUrl(existing!.Url, url))
                        audit?.Record(new Audit.AuditEntry($"{hookHost.AuditName}.webhook.moved", $"Moved Gitberth's webhook on {name} to {url}.")
                        {
                            Target = name, Changes = [new("Payload URL", existing.Url, url)],
                        });
                }
                catch (HostWriteException ex) when (ex.BadToken)
                {
                    hostState.Hook(new HookResult(name, false, ex.Message, Now()));
                    if (hostState.Report(GitHubFeature.Webhooks, ex.Message, Now())) logger.LogWarning("{Host} refused Gitberth's token: {Problem}", hookHost.Name, ex.Message);
                    Refused(hookHost, name, ex.Message);
                    return;
                }
                catch (HostWriteException ex) when (ex.MissingPermission)
                {
                    hostState.Hook(new HookResult(name, false, ex.Message, Now()));
                    if (hostState.Refused(GitHubFeature.Webhooks, name, ex.Message, Now())) logger.LogWarning("{Host} refused Gitberth's webhook on {Repo}: {Problem}", hookHost.Name, name, ex.Message);
                    Refused(hookHost, name, ex.Message);
                }
                catch (HostWriteException ex) when (ex.RateLimited)
                {
                    hostState.Hook(new HookResult(name, false, ex.Message, Now()));
                    if (Refused(hookHost, name, ex.Message)) logger.LogWarning("Couldn't set up Gitberth's webhook on {Repo}: {Problem}", name, ex.Message);
                    // The rate limit is the token's, so it's every repo's on this host: none is asked again before the next
                    // full look, quick tries included.
                    next.Clear();
                    return;
                }
                catch (HostWriteException ex)
                {
                    var problem = ex.Message;
                    if (ex.RetrySoon)
                    {
                        // The POST may have made the webhook all the same, its answer lost on the way: the next try finds it,
                        // GitLab's once it's surely made (see GitLabAfterLostPost).
                        var wait = Soon(key, posting && hookHost.GitLab ? GitLabAfterLostPost : TimeSpan.Zero);
                        problem = $"{ex.Message} Gitberth tries again in {In(wait)}.";
                        if (posting && !(posted.TryGetValue(key, out var p) && SameUrl(p.Url, url))) posted[key] = new Posted(url, Said: false);
                    }
                    hostState.Hook(new HookResult(name, false, problem, Now()));
                    if (Refused(hookHost, name, ex.Message)) logger.LogWarning("Couldn't set up Gitberth's webhook on {Repo}: {Problem}", name, problem);
                }
            }
        }
        finally
        {
            _retries[hookHost.Name] = next;
        }
    }

    // Removes second copies of Gitberth's hook from a repo: GitLab makes one when a POST follows one whose answer was lost
    // but that made the hook after all (GitHub refuses one, so there's none of its own to remove). True once none is left;
    // one that couldn't be removed now is tried again at the next full look.
    private async Task<bool> RemoveCopiesAsync(HookHost hookHost, HookCalls calls, string owner, string repo, string name, IReadOnlyList<RepoHook> copies,
        CancellationToken ct)
    {
        if (copies.Count == 0 || calls.Delete is null) return true;
        var gone = true;
        foreach (var copy in copies)
        {
            try
            {
                await calls.Delete(owner, repo, copy.Id, ct);
                audit?.Record(new Audit.AuditEntry($"{hookHost.AuditName}.webhook.removed",
                    $"Removed a second copy of Gitberth's webhook from {name} on {hookHost.Name}, so each push deploys once.")
                {
                    Target = name, Changes = [new("Webhook", copy.Id.ToString(CultureInfo.InvariantCulture), null)],
                });
            }
            catch (HostWriteException ex)
            {
                gone = false;
                logger.LogWarning("Couldn't remove a second copy of Gitberth's webhook from {Repo}: {Problem}", name, ex.Message);
            }
        }
        return gone;
    }
}
