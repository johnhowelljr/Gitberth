using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Gitberth.Agents;
using Gitberth.Audit;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Services;

namespace Gitberth.Credentials;

/// <summary>What sort of token or sign-in it is: what it's called, who says when it expires, and what checks it.</summary>
internal enum CredentialKind { GitHub, GitLab, Cloudflare, Registry, ApiToken }

/// <summary>One token or sign-in Gitberth uses, as its settings have it now. Never the token itself.</summary>
/// <param name="Key">github, gitlab, cloudflare, registry:&lt;host&gt; or api-token:&lt;id&gt;.</param>
/// <param name="Stamp">
/// A hash of what's stored for it (the encrypted token, a registry's encrypted secret; an API token's id): another stamp
/// whenever it's saved again, even the same token, and nothing about the token.
/// </param>
/// <param name="TokenHash">The token's SHA-256, in memory only, to tell an answer about it from one about another; null for a registry and an API token.</param>
/// <param name="ExpiresAt">When it expires, as its service last said; null when it doesn't, or that isn't known.</param>
/// <param name="Who">Whose it is: the GitHub or GitLab login, the registry's user name, the API token's name.</param>
/// <param name="Place">Where it's used: Gitberth's GitLab's address, the registry's host.</param>
/// <param name="Watched">Watched now: Cloudflare's only while domains are on (off, Gitberth never contacts Cloudflare).</param>
/// <param name="LastUsedAt">An API token's last use (and from where: <paramref name="LastUsedIp"/>), which tells whose agent it is.</param>
internal sealed record CredentialSubject(string Key, CredentialKind Kind, string Stamp, string? TokenHash, DateTimeOffset? ExpiresAt, string? Who,
    string? Place = null, bool Watched = true, DateTime? LastUsedAt = null, string? LastUsedIp = null);

/// <summary>
/// What the watch knows and has told about one token, kept in credentials.state so a restart repeats nothing: the stamp it's
/// about (a new one starts over), the expiry the warnings were for, the last warning told and when, a refusal and when it
/// was last notified, and the banner a person dismissed.
/// </summary>
internal sealed record CredentialFact
{
    public string Stamp { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    /// <summary>The threshold told last (21, 7, 1, or 0 for the expiry itself).</summary>
    public int? WarnedDays { get; init; }
    public DateTimeOffset? WarnedAt { get; init; }
    public DateTimeOffset? RefusedAt { get; init; }
    /// <summary>What Gitberth was doing when the service refused it: "while Gitberth was cloning acme/web"; null when a check found it.</summary>
    public string? RefusedDoing { get; init; }
    /// <summary>The service's own word on why (GitLab's): "GitLab says it was revoked."</summary>
    public string? RefusedWords { get; init; }
    public DateTimeOffset? RefusedNotifiedAt { get; init; }
    /// <summary>The dismissed banner's stamp: it stays hidden while the banner says the same.</summary>
    public string? Dismissed { get; init; }
}

/// <summary>What Check the token found, for its snackbar.</summary>
/// <param name="Took">True: the service took the token; false: it refused it; null: it couldn't be asked, and nothing changed.</param>
/// <param name="Warning">What the check couldn't find out, when it couldn't (GitLab not saying which scopes a token has).</param>
public sealed record CredentialCheck(bool? Took, string Message, string? Warning = null);

/// <summary>
/// Watches every token and sign-in Gitberth uses (GitHub's, GitLab's and Cloudflare's tokens, registry sign-ins, and its
/// own API tokens that are in use) so nobody finds out from a failed deploy: it tells 3 weeks, a week and a day before
/// one expires (an API token a week and a day before), once it has, and the moment its service refuses it, on the
/// dashboard and in its Settings section (the attention board), in a notice, and in the audit log for an expiry or a
/// refusal.
/// <para>
/// It learns from the calls Gitberth makes anyway (<see cref="CredentialSignals"/>: GitHub's and GitLab's clients, git,
/// Cloudflare's API, the update checks), checks each token itself 60 seconds after start and every 6 hours (one or two
/// requests: it notices a token revoked on the service's side within hours even when nothing else calls it), and wakes at
/// the moment each expiry crosses a threshold, with an hourly tick besides. A refusal counts only once the service's own
/// check refuses the token too (at once, at most once a minute), so one stray 401 never says a token is dead; a check
/// that couldn't be made (no answer, a 5xx, the rate limit) changes nothing.
/// </para>
/// </summary>
public sealed class CredentialWatch : BackgroundService
{
    public const string GitHubKey = "github", GitLabKey = "gitlab", CloudflareKey = "cloudflare";
    public const string RegistryPrefix = "registry:", ApiTokenPrefix = "api-token:";

    /// <summary>Whose items these are on the attention board.</summary>
    public const string Source = "credentials";

    /// <summary>How long before its expiry a token is highlighted with "renew it soon" in Settings: the first warning's 3 weeks, so the page and the banner agree.</summary>
    public static readonly TimeSpan WarnFrom = TimeSpan.FromDays(ExpiryRule.External[0]);

    /// <summary>How often each token is checked with its service.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    /// <summary>The longest the watch sleeps: it looks again (without asking any service) at least this often.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>At most one confirmation of a refusal per token this often.</summary>
    public static readonly TimeSpan ConfirmEvery = TimeSpan.FromMinutes(1);

    /// <summary>An API token is in use when a request came with it within this.</summary>
    public static readonly TimeSpan InUse = TimeSpan.FromDays(30);

    /// <summary>A refusal that comes back within this of the last one's notice is shown and recorded, not notified again.</summary>
    public static readonly TimeSpan RefusalQuiet = TimeSpan.FromDays(1);

    /// <summary>The attention board's key of the credential's item: credential:github.</summary>
    public static string BoardKey(string key) => "credential:" + key;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly SettingsService _settings;
    private readonly GitHubApi _github;
    private readonly GitLabApi _gitlab;
    private readonly GitLabSettings _gitlabSettings;
    private readonly DomainService _domains;
    private readonly RegistryService _registries;
    private readonly ApiTokens _apiTokens;
    private readonly Notifier _notifier;
    private readonly AttentionBoard _board;
    private readonly CredentialSignals _signals;
    private readonly ILogger<CredentialWatch> _logger;
    private readonly AuditLog? _audit;
    private readonly TimeProvider _time;

    // Everything below but the view, the words, the answers and the checking counts is read and changed under the gate.
    // The gate is never held while a service is asked: what to ask is found under it, asked outside it, and what was said
    // is applied under it again, so a page that saves or checks a token never waits behind a slow service.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CredentialFact> _facts = new(StringComparer.Ordinal);
    private List<CredentialSubject> _subjects = [];
    private bool _loaded;
    private string? _savedState;
    // A refusal heard from a call, waiting for its confirmation (the first one heard: it says what Gitberth was doing).
    private readonly Dictionary<string, CredentialAnswer> _toConfirm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _confirmedAt = new(StringComparer.Ordinal);

    // What a reporting call compares with, on its own thread and without the gate: each watched token's hash, whether it's
    // refused, its expiry. Replaced whole after each change, never changed in place.
    private volatile IReadOnlyDictionary<string, Known> _view = new Dictionary<string, Known>(StringComparer.Ordinal);
    // How many checks of each token run now (the periodic one and a page's may overlap): the answers to a check's own calls
    // are its result's, not news.
    private readonly ConcurrentDictionary<string, int> _checking = new(StringComparer.Ordinal);
    // GitLab's own word on the last refusal of each token, from any of its answers (a check's too).
    private readonly ConcurrentDictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly Channel<CredentialAnswer> _answers =
        Channel.CreateBounded<CredentialAnswer>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private TaskCompletionSource _wake = NewWake();

    private sealed record Known(string? TokenHash, DateTimeOffset? ExpiresAt, DateTimeOffset? RefusedAt, DateTimeOffset? CheckedAt);

    // What a check of a token found: taken (with the expiry it said, and whether its silence means "no expiry"), refused,
    // or nothing (Verdict null: it couldn't be asked, and Problem says why).
    private sealed record CheckResult(CredentialVerdict? Verdict, DateTimeOffset? ExpiresAt = null, bool Clears = false, string? Problem = null);

    // A token a look asks its service about: periodically, or to confirm a refusal heard (Doing: what Gitberth was doing).
    private sealed record Ask(CredentialSubject Subject, string? Doing, bool Confirming);

    /// <param name="time">The clock its waits and its sentences go by; tests hold it.</param>
    public CredentialWatch(SettingsService settings, GitHubApi github, GitLabApi gitlab, GitLabSettings gitlabSettings, DomainService domains,
        RegistryService registries, ApiTokens apiTokens, Notifier notifier, AttentionBoard board, CredentialSignals signals,
        ILogger<CredentialWatch> logger, AuditLog? audit = null, TimeProvider? time = null)
    {
        _settings = settings;
        _github = github;
        _gitlab = gitlab;
        _gitlabSettings = gitlabSettings;
        _domains = domains;
        _registries = registries;
        _apiTokens = apiTokens;
        _notifier = notifier;
        _board = board;
        _signals = signals;
        _logger = logger;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _signals.Answered += OnAnswered;
        _board.Dismissed += OnDismissed;
    }

    /// <summary>How long after Gitberth starts the first check with each service runs: never in the way of start-up.</summary>
    internal TimeSpan FirstCheckAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Asks Cloudflare about its token in place of <see cref="DomainService.CheckTokenAsync"/>, in tests (which have no tunnel).</summary>
    internal Func<CancellationToken, Task<CloudflareTokenCheck?>>? CloudflareCheck { get; init; }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>When the service refused this token (github, registry:ghcr.io), while it's refused; null when it isn't.</summary>
    public DateTimeOffset? RefusedAt(string key) => _view.TryGetValue(key, out var known) ? known.RefusedAt : null;

    /// <summary>When Gitberth last checked this token with its service (and got an answer); null when it hasn't since it was saved.</summary>
    public DateTimeOffset? CheckedAt(string key) => _view.TryGetValue(key, out var known) ? known.CheckedAt : null;

    /// <summary>Look again soon (without asking any service): something it watches changed, such as an API token revoked.</summary>
    public void Poke() => Wake();

    /// <summary>
    /// A page saved, replaced or removed this token (github, gitlab, cloudflare, registry:&lt;host&gt;): the watch reads it
    /// again at once, so a new token starts over with the thresholds it's past counted as told (its date is on the page), a
    /// removed one's banner and state go, and a refusal of the old token goes with it. Asks no service: the save asked
    /// already. <paramref name="took"/> is what the save's own check said of a token it kept (Settings › GitLab checks the
    /// saved token again at a new address): true ends a refusal, false starts one, null (nothing was asked of it, or it
    /// couldn't be) leaves a refusal as it is, so a save that didn't check the token never hides one.
    /// </summary>
    public async Task SavedAsync(string key, bool? took = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = Now;
            await ReadAsync(now, key, ct);
            _toConfirm.Remove(key);
            if (took is { } verdict && _subjects.FirstOrDefault(s => s.Key == key) is { Watched: true } subject)
            {
                if (verdict) await AcceptedAsync(subject, subject.ExpiresAt, clears: false, checkedNow: true, now, ct);
                else await RefusedAsync(subject, doing: null, now, ct);
            }
            await TellAsync(now, ct);
            await PublishAsync(now, ct);
        }
        finally
        {
            _gate.Release();
            Wake();
        }
    }

    /// <summary>
    /// Check the token (Settings › GitHub access, GitLab, Domains): asks the service now, and what it says counts at once, a
    /// refusal included (no second check). GitLab's goes through Settings › GitLab's own check, which keeps who the token is,
    /// its scopes and its expiry, and lets each GitLab feature try again. The service is asked outside the gate: a look
    /// asking a slow one meanwhile never holds this up, nor this a look.
    /// </summary>
    public async Task<CredentialCheck> CheckNowAsync(string key, CancellationToken ct = default)
    {
        CredentialSubject subject;
        await _gate.WaitAsync(ct);
        try
        {
            var now = Now;
            await ReadAsync(now, null, ct);
            await HeardAsync(now, ct);
            if (_subjects.FirstOrDefault(s => s.Key == key) is not { Watched: true } found)
                return new CredentialCheck(null, key == CloudflareKey && _subjects.Any(s => s.Key == key) ? DomainService.TurnedOff : "No token is saved.");
            subject = found;
            _toConfirm.Remove(key);
            _confirmedAt[key] = now;
        }
        finally
        {
            _gate.Release();
        }

        CheckResult result;
        CredentialCheck? said = null;
        if (subject.Kind == CredentialKind.GitLab)
        {
            GitLabSave checkedNow;
            Checking(key, on: true);
            try
            {
                checkedNow = await _gitlabSettings.CheckAsync(ct);
            }
            finally
            {
                Checking(key, on: false);
            }
            result = checkedNow.Check is not { } c ? new CheckResult(null, Problem: checkedNow.Message)
                : !c.Valid ? new CheckResult(CredentialVerdict.Refused)
                : c.Username is null ? new CheckResult(null, Problem: c.Warning)
                : new CheckResult(CredentialVerdict.Accepted, c.ExpiresAt, Clears: c.Warning is null);
            said = result.Verdict is null
                ? new CredentialCheck(null, CredentialText.CouldNotAsk(subject, result.Problem), checkedNow.Check?.Warning)
                : new CredentialCheck(result.Verdict == CredentialVerdict.Accepted, checkedNow.Message, checkedNow.Check?.Warning);
        }
        else
        {
            result = await CheckAsync(subject, ct);
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = Now;
            await ReadAsync(now, null, ct);
            // Saved again (or removed) while it was asked: the answer was about the token before.
            if (_subjects.FirstOrDefault(s => s.Key == key) is { Watched: true } current && current.Stamp == subject.Stamp)
            {
                await ApplyAsync(current, result, doing: null, now, ct);
                subject = _subjects.First(s => s.Key == key);
            }
            said ??= result.Verdict switch
            {
                CredentialVerdict.Accepted => new CredentialCheck(true, CredentialText.Took(subject, subject.ExpiresAt)),
                CredentialVerdict.Refused => new CredentialCheck(false, CredentialText.RefusedNow(subject)),
                _ => new CredentialCheck(null, CredentialText.CouldNotAsk(subject, result.Problem)),
            };
            await HeardAsync(now, ct);
            await TellAsync(now, ct);
            await PublishAsync(now, ct);
            return said;
        }
        finally
        {
            _gate.Release();
            Wake();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Gitberth's own work: an expiry or a refusal it finds is recorded as its own.
        using var actor = AuditScope.Begin(AuditActor.Gitberth);
        var nextCheck = Now + FirstCheckAfter;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Taken before the look: an answer heard while it runs wakes the wait after it.
                Interlocked.Exchange(ref _wake, NewWake());
                var wait = Tick;
                try
                {
                    var now = Now;
                    var check = now >= nextCheck;
                    if (check) nextCheck = now + Every;
                    wait = await LookAsync(check, nextCheck, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Looking at the tokens Gitberth uses failed");
                }
                await SleepAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        _signals.Answered -= OnAnswered;
        _board.Dismissed -= OnDismissed;
        base.Dispose();
    }

    /// <summary>
    /// One look: what's saved now, the answers heard since the last look, each token checked with its service when
    /// <paramref name="check"/>, the refusals due a confirmation, the thresholds crossed; then the banners and the state.
    /// The services are asked outside the gate (see it), and an answer about a token saved again meanwhile is dropped.
    /// Returns how long to sleep: until the next check, the next threshold, the next confirmation, or the tick.
    /// </summary>
    internal async Task<TimeSpan> LookAsync(bool check, DateTimeOffset nextCheck, CancellationToken ct)
    {
        List<Ask> asks;
        await _gate.WaitAsync(ct);
        try
        {
            var now = Now;
            await ReadAsync(now, null, ct);
            await HeardAsync(now, ct);
            asks = Due(check, now);
            if (asks.Count == 0)
            {
                await TellAsync(now, ct);
                await PublishAsync(now, ct);
                return NextWake(Now, nextCheck);
            }
        }
        finally
        {
            _gate.Release();
        }

        var answers = new List<(Ask Ask, CheckResult Result)>();
        foreach (var ask in asks) answers.Add((ask, await CheckAsync(ask.Subject, ct)));

        await _gate.WaitAsync(ct);
        try
        {
            var now = Now;
            await ReadAsync(now, null, ct);
            foreach (var (ask, result) in answers)
            {
                if (_subjects.FirstOrDefault(s => s.Key == ask.Subject.Key) is not { Watched: true } subject || subject.Stamp != ask.Subject.Stamp) continue;
                if (ask.Confirming && result.Verdict == CredentialVerdict.Accepted)
                    _logger.LogDebug("The {Kind} token was refused {Doing}, but its check takes it", subject.Key, ask.Doing);
                await ApplyAsync(subject, result, ask.Doing, now, ct);
            }
            await HeardAsync(now, ct);
            await TellAsync(now, ct);
            await PublishAsync(now, ct);
            return NextWake(Now, nextCheck);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate: what this look asks the services. Every token a service can be asked about when it's the periodic
    // check; and each refusal heard that's due its confirmation (at once, but at most once a minute per token), unless the
    // token is refused already. Only a check that refuses it too makes it refused.
    private List<Ask> Due(bool check, DateTimeOffset now)
    {
        var asks = new List<Ask>();
        if (check)
            foreach (var subject in _subjects.Where(s => s.Watched && s.Kind is CredentialKind.GitHub or CredentialKind.GitLab or CredentialKind.Cloudflare))
            {
                _toConfirm.Remove(subject.Key);
                _confirmedAt[subject.Key] = now;
                asks.Add(new Ask(subject, null, Confirming: false));
            }
        foreach (var (key, heard) in _toConfirm.ToList())
        {
            if (_confirmedAt.TryGetValue(key, out var last) && now - last < ConfirmEvery) continue;
            _toConfirm.Remove(key);
            _confirmedAt[key] = now;
            if (_subjects.FirstOrDefault(s => s.Key == key) is not { Watched: true } subject || _facts[key].RefusedAt is not null) continue;
            asks.Add(new Ask(subject, heard.Doing, Confirming: true));
        }
        return asks;
    }

    // ---- What's saved ----

    // Under the gate; reads only Gitberth's own settings and database. The tokens as saved now, each with its fact: a new
    // stamp starts over (a token saved just now with the thresholds it's past counted as told, one found otherwise with
    // nothing told); a removed token's fact goes. The same token keeps its refusal: only a check that took it ends one.
    private async Task ReadAsync(DateTimeOffset now, string? saved, CancellationToken ct)
    {
        if (!_loaded) await LoadAsync(ct);
        var subjects = await SubjectsAsync(now, ct);
        var facts = new Dictionary<string, CredentialFact>(StringComparer.Ordinal);
        foreach (var s in subjects)
        {
            var known = _facts.GetValueOrDefault(s.Key);
            var isSaved = saved == s.Key;
            CredentialFact fact;
            if (known is null || known.Stamp != s.Stamp)
            {
                fact = new CredentialFact { Stamp = s.Stamp, ExpiresAt = s.ExpiresAt, WarnedDays = isSaved ? ExpiryRule.Saved(s.ExpiresAt, now, ThresholdsOf(s.Kind)) : null };
                if (known is not null) Forget(s.Key);
            }
            else
            {
                fact = known;
                // A new expiry for the same token (GitLab's and Cloudflare's can be moved): its warnings start over. One that
                // isn't known any more keeps them, for the expiry they were told for: should the same date come back (a check
                // that couldn't say, then one that could), nothing is told twice.
                if (s.ExpiresAt is { } expires && expires != fact.ExpiresAt)
                    fact = fact with { ExpiresAt = expires, WarnedDays = isSaved ? ExpiryRule.Saved(expires, now, ThresholdsOf(s.Kind)) : null };
            }
            facts[s.Key] = fact;
        }
        foreach (var gone in _facts.Keys.Where(k => !facts.ContainsKey(k)).ToList()) Forget(gone);
        _facts = facts;
        _subjects = subjects;
    }

    private void Forget(string key)
    {
        _toConfirm.Remove(key);
        _words.TryRemove(key, out _);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        _savedState = await _settings.GetAsync(CredentialKeys.State, ct);
        try
        {
            _facts = _savedState is { Length: > 0 } json
                ? new Dictionary<string, CredentialFact>(JsonSerializer.Deserialize<Dictionary<string, CredentialFact>>(json, Json) ?? [], StringComparer.Ordinal)
                : new Dictionary<string, CredentialFact>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "What the token watch had told couldn't be read: it starts over");
            _facts = new Dictionary<string, CredentialFact>(StringComparer.Ordinal);
        }
        // A banner someone dismissed stays dismissed after a restart, while it says the same.
        foreach (var (key, fact) in _facts)
            if (fact.Dismissed is { Length: > 0 } stamp) _board.KeepDismissed(BoardKey(key), stamp);
        _loaded = true;
    }

    private async Task<List<CredentialSubject>> SubjectsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<CredentialSubject>();
        if (await TokenAsync(SettingKeys.GitHubToken, ct) is { } github)
            list.Add(new CredentialSubject(GitHubKey, CredentialKind.GitHub, github.Stamp, github.Hash, DateOf(await _settings.GetAsync(SettingKeys.GitHubTokenExpires, ct)),
                await _settings.GetAsync(SettingKeys.GitHubLogin, ct)));
        if (await TokenAsync(SettingKeys.GitLabToken, ct) is { } gitlab)
            list.Add(new CredentialSubject(GitLabKey, CredentialKind.GitLab, gitlab.Stamp, gitlab.Hash, DateOf(await _settings.GetAsync(SettingKeys.GitLabTokenExpires, ct)),
                await _settings.GetAsync(SettingKeys.GitLabLogin, ct), await _gitlab.InstanceAsync(ct)));
        if (await TokenAsync(SettingKeys.CloudflareToken, ct) is { } cloudflare)
            list.Add(new CredentialSubject(CloudflareKey, CredentialKind.Cloudflare, cloudflare.Stamp, cloudflare.Hash,
                DateOf(await _settings.GetAsync(CredentialKeys.CloudflareTokenExpires, ct)), null, Watched: await _domains.EnabledAsync(ct)));
        foreach (var r in await _registries.StampsAsync(ct))
            list.Add(new CredentialSubject(RegistryPrefix + r.Host, CredentialKind.Registry, r.Stamp, null, null, r.Username, r.Host));
        foreach (var t in await _apiTokens.ListAsync(ct))
        {
            // Only a token something uses: one nobody has used for a month expiring stops nothing.
            if (t.RevokedAt is not null || t.ExpiresAt is not { } expires || t.LastUsedAt is not { } used || Utc(used) < now - InUse) continue;
            list.Add(new CredentialSubject(ApiTokenPrefix + t.Id.ToString(CultureInfo.InvariantCulture), CredentialKind.ApiToken, t.Id.ToString(CultureInfo.InvariantCulture),
                null, Utc(expires), t.Name, LastUsedAt: used, LastUsedIp: t.LastUsedIp));
        }
        return list;
    }

    // A saved token's stamp (of the stored, encrypted text) and its hash; null when none is saved, or it can't be read (Gitberth's keys were lost).
    private async Task<(string Stamp, string Hash)?> TokenAsync(string key, CancellationToken ct) =>
        await _settings.GetAsync(key, ct) is { Length: > 0 } stored && await _settings.GetSecretAsync(key, ct) is { } token
            ? (CredentialSignals.Stamp(stored), CredentialSignals.Hash(token))
            : null;

    private static DateTimeOffset? DateOf(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private static IReadOnlyList<int> ThresholdsOf(CredentialKind kind) => kind switch
    {
        CredentialKind.ApiToken => ExpiryRule.ApiToken,
        CredentialKind.Registry => [],
        _ => ExpiryRule.External,
    };

    // The setting a learned expiry goes in; null for what has none (a registry) or Gitberth sets itself (an API token).
    private static string? ExpiryKey(CredentialKind kind) => kind switch
    {
        CredentialKind.GitHub => SettingKeys.GitHubTokenExpires,
        CredentialKind.GitLab => SettingKeys.GitLabTokenExpires,
        CredentialKind.Cloudflare => CredentialKeys.CloudflareTokenExpires,
        _ => null,
    };

    // ---- What the calls heard ----

    // On the reporting call's thread: only what's news is queued (a refusal of a token that isn't refused yet, an
    // acceptance of one that is, an expiry that isn't the one known), and the look wakes for it. A token past its known
    // expiry is refused as it should be, every time something still uses it: that's its expiry, said already, never news.
    private void OnAnswered(CredentialAnswer answer)
    {
        try
        {
            if (!_view.TryGetValue(answer.Kind, out var known)) return;
            if (answer.TokenHash is not null && answer.TokenHash != known.TokenHash) return;
            if (answer.Verdict == CredentialVerdict.Refused && known.ExpiresAt is { } expires && expires <= Now) return;
            if (answer.Verdict == CredentialVerdict.Refused && answer.Words is { } words) _words[answer.Kind] = words;
            if (IsChecking(answer.Kind)) return;
            var news = answer.Verdict == CredentialVerdict.Refused
                ? known.RefusedAt is null
                : known.RefusedAt is not null || answer.ExpiresAt is { } at && at != known.ExpiresAt;
            if (!news) return;
            _answers.Writer.TryWrite(answer);
            Wake();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "An answer about the {Kind} token couldn't be noted", answer.Kind);
        }
    }

    private void Checking(string key, bool on) => _checking.AddOrUpdate(key, on ? 1 : 0, (_, count) => on ? count + 1 : Math.Max(0, count - 1));

    private bool IsChecking(string key) => _checking.TryGetValue(key, out var count) && count > 0;

    // Under the gate: a refusal waits for its confirmation (not a token past its expiry: see OnAnswered); an acceptance
    // ends a refusal, and an expiry it carries is learned.
    private async Task HeardAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (_answers.Reader.TryRead(out var answer))
        {
            if (_subjects.FirstOrDefault(s => s.Key == answer.Kind) is not { Watched: true } subject) continue;
            // Saved again since: the answer was about the old token.
            if (answer.TokenHash is not null && answer.TokenHash != subject.TokenHash) continue;
            if (answer.Verdict == CredentialVerdict.Refused)
            {
                if (_facts[subject.Key].RefusedAt is null && !(subject.ExpiresAt is { } expires && expires <= now)) _toConfirm.TryAdd(subject.Key, answer);
                continue;
            }
            await AcceptedAsync(subject, answer.ExpiresAt, clears: false, checkedNow: false, now, ct);
        }
    }

    // Asks the token's service about it, outside the gate: GitHub's /user, GitLab's /user and /personal_access_tokens/self,
    // Cloudflare's verify, a registry's sign-in. The answers to its own calls are its result's, not news.
    private async Task<CheckResult> CheckAsync(CredentialSubject s, CancellationToken ct)
    {
        Checking(s.Key, on: true);
        try
        {
            switch (s.Kind)
            {
                case CredentialKind.GitHub:
                {
                    if (await _settings.GetSecretAsync(SettingKeys.GitHubToken, ct) is not { } token) return new CheckResult(null, Problem: "No GitHub token is saved.");
                    var check = await _github.CheckTokenAsync(token, ct);
                    // GitHub says an expiry only for a token that has one, and only when it answers: silence never clears it.
                    return !check.Valid ? new CheckResult(CredentialVerdict.Refused)
                        : check.Warning is null ? new CheckResult(CredentialVerdict.Accepted, check.ExpiresAt)
                        : new CheckResult(null, Problem: check.Warning);
                }
                case CredentialKind.GitLab:
                {
                    if (await _settings.GetSecretAsync(SettingKeys.GitLabToken, ct) is not { } token) return new CheckResult(null, Problem: "No GitLab token is saved.");
                    var check = await _gitlab.CheckTokenAsync(await _gitlab.InstanceAsync(ct), token, ct);
                    // Its expiry is known only when GitLab also said which token it is (personal_access_tokens/self).
                    return !check.Valid ? new CheckResult(CredentialVerdict.Refused)
                        : check.Username is null ? new CheckResult(null, Problem: check.Warning)
                        : new CheckResult(CredentialVerdict.Accepted, check.ExpiresAt, Clears: check.Warning is null);
                }
                case CredentialKind.Cloudflare:
                {
                    var check = await (CloudflareCheck ?? _domains.CheckTokenAsync)(ct);
                    return check is null ? new CheckResult(null, Problem: DomainService.TurnedOff)
                        : check.Refused ? new CheckResult(CredentialVerdict.Refused)
                        : check.Problem is { } problem ? new CheckResult(null, Problem: problem)
                        : new CheckResult(CredentialVerdict.Accepted, check.ExpiresOn, Clears: true);
                }
                case CredentialKind.Registry:
                    return await _registries.ConfirmAsync(s.Place!, ct) switch
                    {
                        true => new CheckResult(CredentialVerdict.Accepted),
                        false => new CheckResult(CredentialVerdict.Refused),
                        _ => new CheckResult(null, Problem: $"{CredentialText.RegistryName(s.Place)} or Docker couldn't be asked."),
                    };
                default:
                    return new CheckResult(null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "The {Kind} token couldn't be checked", s.Key);
            return new CheckResult(null, Problem: ex.Message);
        }
        finally
        {
            Checking(s.Key, on: false);
        }
    }

    private async Task ApplyAsync(CredentialSubject subject, CheckResult result, string? doing, DateTimeOffset now, CancellationToken ct)
    {
        switch (result.Verdict)
        {
            case CredentialVerdict.Refused:
                await RefusedAsync(subject, doing, now, ct);
                break;
            case CredentialVerdict.Accepted:
                await AcceptedAsync(subject, result.ExpiresAt, result.Clears, checkedNow: true, now, ct);
                break;
            default:
                _logger.LogDebug("The {Kind} token couldn't be checked: {Problem}", subject.Key, result.Problem);
                break;
        }
    }

    // Under the gate: a refusal confirmed (or found by a check). Each episode is shown and recorded; it's notified unless
    // the last one's notice is less than a day old, so a service that flaps never floods anyone.
    private async Task RefusedAsync(CredentialSubject s, string? doing, DateTimeOffset now, CancellationToken ct)
    {
        var fact = _facts[s.Key];
        if (fact.RefusedAt is not null) return;
        // A token past its expiry is refused as it should be: its expiry is what's said (its banner, its notice).
        if (s.ExpiresAt is { } expires && expires <= now) return;
        fact = fact with
        {
            RefusedAt = now, RefusedDoing = doing, RefusedWords = s.Kind == CredentialKind.GitLab ? _words.GetValueOrDefault(s.Key) : null,
        };
        var quiet = fact.RefusedNotifiedAt is { } told && now - told < RefusalQuiet;
        if (!quiet) fact = fact with { RefusedNotifiedAt = now };
        _facts[s.Key] = fact;
        var title = CredentialText.RefusedTitle(s);
        var text = CredentialText.RefusedText(s, fact);
        _logger.LogWarning("{Title}: {Text}", title, text);
        _audit?.Record(new AuditEntry(CredentialText.Action(s.Kind, "refused"), CredentialText.AuditRefused(s, fact))
        {
            Target = CredentialText.Target(s), Outcome = AuditOutcome.Failed, Actor = AuditActor.Gitberth,
            Changes = [new AuditChange("Refused", null, doing ?? "when Gitberth checked it")],
        });
        if (!quiet) await NotifyAsync(s, NotifyEvent.TokenRefused, NoticeLevel.Critical, title, text, now, ct);
    }

    // Under the gate: the service took the token. A refusal ends (and the audit log says so); an expiry it said is learned.
    private async Task AcceptedAsync(CredentialSubject s, DateTimeOffset? expiresAt, bool clears, bool checkedNow, DateTimeOffset now, CancellationToken ct)
    {
        var fact = _facts[s.Key];
        _words.TryRemove(s.Key, out _);
        if (fact.RefusedAt is not null)
        {
            fact = fact with { RefusedAt = null, RefusedDoing = null, RefusedWords = null };
            var said = CredentialText.AuditAccepted(s);
            _logger.LogInformation("{Said}", said);
            _audit?.Record(new AuditEntry(CredentialText.Action(s.Kind, "accepted"), said) { Target = CredentialText.Target(s), Actor = AuditActor.Gitberth });
        }
        if (checkedNow) fact = fact with { CheckedAt = now };
        var learned = expiresAt ?? (clears ? null : s.ExpiresAt);
        if (learned != s.ExpiresAt && ExpiryKey(s.Kind) is { } key)
        {
            await _settings.SetAsync(key, learned?.ToString("O", CultureInfo.InvariantCulture), ct);
            _logger.LogInformation("The {Kind} token {Expiry}", s.Key, learned is { } on ? $"expires on {on.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "doesn't expire");
            var updated = s with { ExpiresAt = learned };
            _subjects = [.. _subjects.Select(x => x.Key == s.Key ? updated : x)];
        }
        // Its warnings start over for a new date only: the one they were told for, said again, tells nothing again.
        if (learned is { } date && date != fact.ExpiresAt) fact = fact with { ExpiresAt = date, WarnedDays = null };
        _facts[s.Key] = fact;
    }

    // ---- What's told ----

    // Under the gate: each threshold crossed is told once (the lowest one crossed), and the expiry the moment it passes,
    // with one audit entry. Nothing while a token is refused: its refusal is what's said.
    private async Task TellAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in _subjects.Where(s => s.Watched && s.ExpiresAt is not null).ToList())
        {
            var fact = _facts[s.Key];
            if (fact.RefusedAt is not null) continue;
            var (tell, warnedDays, warnedAt) = ExpiryRule.Next(s.ExpiresAt, now, ThresholdsOf(s.Kind), fact.WarnedDays, fact.WarnedAt);
            if (tell is not int days) continue;
            _facts[s.Key] = fact with { WarnedDays = warnedDays, WarnedAt = warnedAt };
            var expired = days == ExpiryRule.Expired;
            var title = expired ? CredentialText.ExpiredTitle(s) : CredentialText.ExpiringTitle(s, now);
            var text = expired ? CredentialText.ExpiredText(s) : CredentialText.ExpiringText(s);
            if (expired)
            {
                _logger.LogWarning("{Title}: {Text}", title, text);
                // An API token's expiry is in the audit log already (ApiTokens records it).
                if (s.Kind != CredentialKind.ApiToken)
                    _audit?.Record(new AuditEntry(CredentialText.Action(s.Kind, "expired"), CredentialText.AuditExpired(s))
                    {
                        Target = CredentialText.Target(s), Outcome = AuditOutcome.Failed, Actor = AuditActor.Gitberth,
                        Changes = [new AuditChange("Expires", null, s.ExpiresAt!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))],
                    });
            }
            else
            {
                _logger.LogInformation("{Title}, on {Expires} UTC", title, s.ExpiresAt!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }
            var level = expired ? NoticeLevel.Critical : days > 7 ? NoticeLevel.Info : NoticeLevel.Warning;
            await NotifyAsync(s, NotifyEvent.TokenExpiring, level, title, text, now, ct);
        }
    }

    private async Task NotifyAsync(CredentialSubject s, NotifyEvent kind, NoticeLevel level, string title, string text, DateTimeOffset now, CancellationToken ct) =>
        _notifier.Publish(new Notice(kind, level, title, text, await _notifier.LinkAsync(CredentialText.Href(s.Kind), ct), "", null, now.UtcDateTime)
        {
            Credential = s.Kind switch
            {
                CredentialKind.ApiToken => ApiTokenPrefix + s.Who,
                _ => s.Key,
            },
            Expires = s.ExpiresAt,
        });

    // Under the gate: the banners as they stand now, what reporting calls compare with, and the state if it changed.
    private async Task PublishAsync(DateTimeOffset now, CancellationToken ct)
    {
        var items = Items(now);
        // A dismissal holds while the banner says what it said then. Once the banner says something else (a refusal came
        // between, its next threshold, no banner at all), the board lets the dismissal go, and so does what's saved: a
        // restart then shows what the page showed before it, never hides the banner again.
        foreach (var (key, fact) in _facts.ToList())
            if (fact.Dismissed is { } stamp && items.FirstOrDefault(i => i.Key == BoardKey(key))?.Dismiss != stamp)
                _facts[key] = fact with { Dismissed = null };
        _board.Replace(Source, items);
        _view = _subjects.Where(s => s.Watched).ToDictionary(s => s.Key, s => new Known(s.TokenHash, s.ExpiresAt, _facts[s.Key].RefusedAt, _facts[s.Key].CheckedAt),
            StringComparer.Ordinal);
        await SaveAsync(ct);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new SortedDictionary<string, CredentialFact>(_facts, StringComparer.Ordinal), Json);
        if (json == _savedState) return;
        await _settings.SetAsync(CredentialKeys.State, _facts.Count == 0 ? null : json, ct);
        _savedState = _facts.Count == 0 ? null : json;
    }

    /// <summary>Each watched token's banner: refused or expired (critical, with the top bar's pill), the last day (no dismiss), the week and 3 weeks before (dismissible).</summary>
    private List<AttentionItem> Items(DateTimeOffset now)
    {
        var items = new List<AttentionItem>();
        foreach (var s in _subjects.Where(s => s.Watched))
        {
            var fact = _facts[s.Key];
            var key = BoardKey(s.Key);
            var href = CredentialText.Href(s.Kind);
            var actions = ActionsOf(s);
            if (fact.RefusedAt is not null)
            {
                items.Add(new AttentionItem(key, Source, AttentionLevel.Critical, CredentialText.RefusedTitle(s), CredentialText.RefusedText(s, fact), href,
                    Tip: "credentials.refused", Actions: actions)
                {
                    Pill = CredentialText.Pill(s, refused: true), Problem = CredentialText.RefusedProblem(s, fact),
                });
                continue;
            }
            if (s.ExpiresAt is not { } expires || ExpiryRule.At(expires, now, ThresholdsOf(s.Kind)) is not int at) continue;
            if (at == ExpiryRule.Expired)
            {
                items.Add(new AttentionItem(key, Source, AttentionLevel.Critical, CredentialText.ExpiredTitle(s), CredentialText.ExpiredText(s), href,
                    Tip: "credentials.expired", Actions: actions)
                {
                    Pill = CredentialText.Pill(s, refused: false), Problem = CredentialText.ExpiredProblem(s),
                });
                continue;
            }
            // Dismissible until its next threshold or a new token; not on the last day.
            var dismiss = at <= 1 ? null : $"{fact.Stamp}:{expires.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{at.ToString(CultureInfo.InvariantCulture)}";
            items.Add(new AttentionItem(key, Source, at > 7 ? AttentionLevel.Info : AttentionLevel.Warn, CredentialText.ExpiringTitle(s, now), CredentialText.ExpiringText(s),
                href, Tip: "credentials.expiring", Actions: actions, Dismiss: dismiss)
            {
                Problem = CredentialText.ExpiringProblem(s, now),
            });
        }
        return items;
    }

    // Where a new token is made, and its Settings section (which a banner in that section leaves out).
    private static List<AttentionAction> ActionsOf(CredentialSubject s)
    {
        var settings = new AttentionAction("Open Settings", CredentialText.Href(s.Kind));
        return s.Kind switch
        {
            CredentialKind.GitHub => [new AttentionAction("Make a new token", CredentialText.GitHubTokens), settings],
            CredentialKind.GitLab => [new AttentionAction("Make a new token", CredentialText.GitLabTokens(s.Place!)), settings],
            CredentialKind.Cloudflare => [new AttentionAction("Make a new token", CredentialText.CloudflareTokens), settings],
            _ => [settings],
        };
    }

    // A banner dismissed on any page: kept, so every browser and a restart agree.
    private void OnDismissed(AttentionItem item)
    {
        if (item.Source != Source || item.Dismiss is not { } stamp) return;
        _ = KeepDismissalAsync(item.Key["credential:".Length..], stamp);
    }

    private async Task KeepDismissalAsync(string key, string stamp)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (!_facts.TryGetValue(key, out var fact)) return;
                _facts[key] = fact with { Dismissed = stamp };
                await SaveAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The dismissal of the {Kind} token's banner couldn't be kept", key);
        }
    }

    // ---- Waiting ----

    // Under the gate: until the next periodic check, the next threshold any expiry crosses, the next refusal due its
    // confirmation, or the tick, whichever comes first.
    private TimeSpan NextWake(DateTimeOffset now, DateTimeOffset nextCheck)
    {
        var next = nextCheck < now + Tick ? nextCheck : now + Tick;
        foreach (var s in _subjects.Where(s => s.Watched && _facts[s.Key].RefusedAt is null))
            if (ExpiryRule.NextCrossing(s.ExpiresAt, now, ThresholdsOf(s.Kind)) is { } crossing && crossing < next) next = crossing;
        foreach (var key in _toConfirm.Keys)
        {
            var due = _confirmedAt.TryGetValue(key, out var last) ? last + ConfirmEvery : now;
            if (due < next) next = due;
        }
        return next > now ? next - now : TimeSpan.Zero;
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    private async Task SleepAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        var wake = Volatile.Read(ref _wake).Task;
        if (wake.IsCompleted || wait <= TimeSpan.Zero) return;
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAny(Task.Delay(wait, _time, cut.Token), wake);
        // Woken early: the wait's timer goes now, not at its time.
        cut.Cancel();
        stoppingToken.ThrowIfCancellationRequested();
    }
}
