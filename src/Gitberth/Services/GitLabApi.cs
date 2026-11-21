using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gitberth.Git;
using Gitberth.Previews;

namespace Gitberth.Services;

/// <summary>Gitberth's GitLab, as Settings › GitLab has it: its address, and the token when one is saved.</summary>
public sealed record GitLabConnection(string Instance, string? Token)
{
    /// <summary>The REST API's root: &lt;instance&gt;/api/v4/.</summary>
    public string ApiRoot => Instance.TrimEnd('/') + "/api/v4/";

    /// <summary>gitlab.com, or gitlab.example.com:8443: the instance as sentences name it.</summary>
    public string Host => Uri.TryCreate(Instance, UriKind.Absolute, out var uri) ? uri.Authority : Instance;

    public bool HasToken => !string.IsNullOrEmpty(Token);
}

/// <summary>GitLab couldn't be reached, or said no, when Gitberth read something or saved a variable.</summary>
/// <param name="status">GitLab's answer, when it gave one.</param>
/// <param name="retryAfter">How long GitLab asked to wait (429's Retry-After), when it said.</param>
public sealed class GitLabApiException(string message, HttpStatusCode? status = null, TimeSpan? retryAfter = null) : HostApiException(message)
{
    public HttpStatusCode? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
}

/// <summary>GitLab refused, or failed, a commit status, a merge request's comment or a project hook.</summary>
/// <param name="retrySoon">GitLab failed for a moment: it didn't answer in time, couldn't be reached, or failed itself (a 5xx).</param>
/// <param name="status">GitLab's answer, when it gave one: a 429 is its rate limit.</param>
public sealed class GitLabWriteException(string message, bool missingPermission = false, bool badToken = false, HttpStatusCode? status = null, bool retrySoon = false)
    : HostWriteException(message, missingPermission, badToken, retrySoon, rateLimited: status == HttpStatusCode.TooManyRequests)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Who a token is, what it may do, and when it expires, as Settings › GitLab shows it.</summary>
/// <param name="Valid">GitLab took it (GET /user answered); false when it refused it (401).</param>
/// <param name="Username">GET /user's username.</param>
/// <param name="Scopes">GET /personal_access_tokens/self's scopes; null when GitLab didn't say (an old GitLab, or another kind of token).</param>
/// <param name="ExpiresAt">Its expires_at; null when it doesn't expire, or GitLab didn't say.</param>
/// <param name="Warning">Why it couldn't be checked fully, when it couldn't.</param>
public sealed record GitLabTokenCheck(bool Valid, string? Username, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt, string? Warning)
{
    /// <summary>The scopes it lacks, each with what it's needed for; empty when it has them all, or they aren't known.</summary>
    public IReadOnlyList<(string Scope, string For)> Missing => Scopes is null ? [] : GitLabScopes.Missing(Scopes);
}

/// <summary>The token scopes Gitberth uses, and what for (GitLab's own words for them: api, read_api, read_repository).</summary>
public static class GitLabScopes
{
    public const string Api = "api";
    public const string ReadApi = "read_api";
    public const string ReadRepository = "read_repository";
    public const string WriteRepository = "write_repository";

    /// <summary>What each scope is needed for, in the words Settings uses.</summary>
    public const string ApiFor = "commit statuses, merge request comments, Gitberth's project hooks (instant deploys) and saving CI/CD variables";
    public const string ReadApiFor = "merge requests for previews, CI/CD variables at deploy, and build filters";
    public const string ReadRepositoryFor = "cloning private projects";

    /// <summary>
    /// The scopes a token with <paramref name="scopes"/> lacks: api to write (which also reads, and clones), else read_api to
    /// read; read_repository (or write_repository, or api) to clone.
    /// </summary>
    public static IReadOnlyList<(string Scope, string For)> Missing(IReadOnlyCollection<string> scopes)
    {
        bool Has(string s) => scopes.Contains(s, StringComparer.Ordinal);
        var missing = new List<(string, string)>();
        if (!Has(Api)) missing.Add((Api, ApiFor));
        if (!Has(Api) && !Has(ReadApi)) missing.Add((ReadApi, ReadApiFor));
        if (!Has(Api) && !Has(ReadRepository) && !Has(WriteRepository)) missing.Add((ReadRepository, ReadRepositoryFor));
        return missing;
    }
}

/// <summary>One of a project's CI/CD variables (Settings › CI/CD › Variables).</summary>
/// <param name="Scope">Its environment_scope: * (every environment), production, preview/*, or any other.</param>
/// <param name="Type">env_var, or file (its value is a file's contents).</param>
public sealed record GitLabVariable(string Key, string Value, string Scope, bool Masked, bool Protected, string Type);

/// <summary>A project hook, as GitLab lists it (the token is never in it).</summary>
public sealed record GitLabHook(long Id, string? Url, bool PushEvents, bool MergeRequestsEvents);

/// <summary>A note (comment) on a merge request.</summary>
/// <param name="System">GitLab's own note ("added 1 commit"), never a person's.</param>
public sealed record GitLabNote(long Id, string Body, string? Author, bool System);

/// <summary>The files a compare lists: each diff's old and new path; <see cref="TimedOut"/> when GitLab gave up on the list.</summary>
public sealed record GitLabCompare(IReadOnlyList<string> Paths, bool TimedOut);

/// <summary>
/// Gitberth's GitLab API calls (REST v4, docs.gitlab.com), all of them, on Gitberth's GitLab (Settings › GitLab): who a token
/// is and what it may do, a project's merge requests and the files a push changed, commit statuses and the comment on a
/// merge request with its preview, the project hook for instant deploys, and the project's CI/CD variables. A token goes
/// as PRIVATE-TOKEN; without one, a public project still answers its reads. Lists are read 100 at a time, following
/// X-Next-Page. GitLab's rate limit (429, with Retry-After) is said, never pushed through: the call fails with when to
/// try again, and whoever asked asks again later, as with GitHub.
/// </summary>
public sealed class GitLabApi(IHttpClientFactory httpFactory, SettingsService settings)
{
    /// <summary>The HttpClient's name (Program.cs).</summary>
    public const string ClientName = "gitlab";

    /// <summary>How many pages of 100 a list reads at most.</summary>
    internal const int MaxPages = 10;

    /// <summary>The events Gitberth's project hook asks for, as Settings shows them.</summary>
    public static readonly IReadOnlyList<string> HookEvents = ["Push events", "Merge request events"];

    /// <summary>Gitberth's GitLab now: Settings' address (gitlab.com when none is set) and token.</summary>
    public async Task<GitLabConnection> ConnectionAsync(CancellationToken ct = default) =>
        new(await InstanceAsync(ct), await settings.GetSecretAsync(SettingKeys.GitLabToken, ct));

    /// <summary>Settings' address for Gitberth's GitLab; gitlab.com when none is set.</summary>
    public async Task<string> InstanceAsync(CancellationToken ct = default) =>
        GitLabRepoRef.NormalizeInstance(await settings.GetAsync(SettingKeys.GitLabUrl, ct), out _) ?? GitLabRepoRef.DefaultInstance;

    // ---- The token ----

    /// <summary>Who the token is (GET /user), and its scopes and expiry (GET /personal_access_tokens/self), on <paramref name="instance"/>.</summary>
    public async Task<GitLabTokenCheck> CheckTokenAsync(string instance, string token, CancellationToken ct = default)
    {
        var conn = new GitLabConnection(instance, token);
        string? username;
        try
        {
            using var user = await SendAsync(conn, HttpMethod.Get, "user", null, ct);
            if (user.StatusCode == HttpStatusCode.Unauthorized) return new GitLabTokenCheck(false, null, null, null, "GitLab says this token is invalid, expired or revoked.");
            if (!user.IsSuccessStatusCode)
                return new GitLabTokenCheck(true, null, null, null, $"GitLab answered {(int)user.StatusCode} when asked who the token is ({await ErrorTextAsync(user, ct)}).");
            using var doc = JsonDocument.Parse(await user.Content.ReadAsStringAsync(ct));
            username = Str(doc.RootElement, "username");
        }
        catch (GitLabApiException ex)
        {
            return new GitLabTokenCheck(true, null, null, null, ex.Message);
        }
        catch (JsonException)
        {
            return new GitLabTokenCheck(true, null, null, null, $"{conn.Host} didn't answer like GitLab when asked who the token is. Check the address.");
        }
        try
        {
            using var self = await SendAsync(conn, HttpMethod.Get, "personal_access_tokens/self", null, ct);
            if (!self.IsSuccessStatusCode)
                return new GitLabTokenCheck(true, username, null, null,
                    "GitLab didn't say which scopes the token has (an older GitLab, or a project or group token), so they can't be checked here: Gitberth needs api, read_api and read_repository.");
            using var doc = JsonDocument.Parse(await self.Content.ReadAsStringAsync(ct));
            var scopes = doc.RootElement.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            var expires = Str(doc.RootElement, "expires_at") is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
            return new GitLabTokenCheck(true, username, scopes, expires, null);
        }
        catch (Exception ex) when (ex is GitLabApiException or JsonException)
        {
            return new GitLabTokenCheck(true, username, null, null, "GitLab didn't say which scopes the token has, so they can't be checked here: Gitberth needs api, read_api and read_repository.");
        }
    }

    // ---- Reads ----

    /// <summary>The project's open merge requests, for its apps' previews: one from a fork (another source project) counts as from a fork.</summary>
    /// <exception cref="GitLabApiException">GitLab couldn't be reached, or said no.</exception>
    public async Task<List<PullRequestInfo>> ListOpenMergeRequestsAsync(GitLabRepoRef project, CancellationToken ct = default)
    {
        var conn = await ConnectionAsync(ct);
        var all = new List<PullRequestInfo>();
        await foreach (var mr in ListAsync(conn, $"projects/{project.ApiId}/merge_requests?state=opened", $"list {project.FullName}'s merge requests", ct))
            all.Add(MergeRequest(mr, project));
        return all;
    }

    /// <summary>Whether a merge request that isn't open any more was merged; null when GitLab can't say.</summary>
    public async Task<bool?> MergedAsync(GitLabRepoRef project, int iid, CancellationToken ct = default)
    {
        try
        {
            var conn = await ConnectionAsync(ct);
            using var response = await SendAsync(conn, HttpMethod.Get, $"projects/{project.ApiId}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}", null, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Str(doc.RootElement, "state") switch
            {
                "merged" => true,
                "closed" or "locked" => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is GitLabApiException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The files that differ between two commits (GET …/repository/compare, straight: from's tree against to's), each
    /// diff's old and new path, for the build filters.
    /// </summary>
    /// <exception cref="GitLabApiException">GitLab couldn't be reached, said no, or answered something else.</exception>
    public async Task<GitLabCompare> CompareAsync(GitLabRepoRef project, string from, string to, CancellationToken ct = default)
    {
        var conn = await ConnectionAsync(ct);
        using var response = await SendAsync(conn, HttpMethod.Get,
            $"projects/{project.ApiId}/repository/compare?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&straight=true", null, ct);
        await EnsureReadAsync(response, conn, $"compare two commits of {project.FullName}", ct);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var timedOut = doc.RootElement.TryGetProperty("compare_timeout", out var t) && t.ValueKind == JsonValueKind.True;
            var paths = new List<string>();
            if (doc.RootElement.TryGetProperty("diffs", out var diffs) && diffs.ValueKind == JsonValueKind.Array)
                foreach (var d in diffs.EnumerateArray())
                    foreach (var p in new[] { Str(d, "old_path"), Str(d, "new_path") })
                        if (p is { Length: > 0 } && !paths.Contains(p, StringComparer.Ordinal)) paths.Add(p);
            return new GitLabCompare(paths, timedOut);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GitLabApiException($"GitLab's compare of {project.FullName}'s commits couldn't be read.");
        }
    }

    /// <summary>The project's CI/CD variables, every environment scope.</summary>
    /// <exception cref="GitLabApiException">GitLab couldn't be reached, or said no.</exception>
    public async Task<List<GitLabVariable>> ListVariablesAsync(GitLabConnection conn, GitLabRepoRef project, CancellationToken ct = default)
    {
        var all = new List<GitLabVariable>();
        await foreach (var v in ListAsync(conn, $"projects/{project.ApiId}/variables", $"read {project.FullName}'s CI/CD variables", ct))
        {
            if (Str(v, "key") is not { Length: > 0 } key) continue;
            all.Add(new GitLabVariable(key, Str(v, "value") ?? "", Str(v, "environment_scope") is { Length: > 0 } scope ? scope : "*",
                Bool(v, "masked"), Bool(v, "protected"), Str(v, "variable_type") ?? "env_var"));
        }
        return all;
    }

    // ---- Writes: variables ----

    /// <summary>Saves a variable for one environment scope: changes it when the project has it there, else adds it (not masked, not protected).</summary>
    /// <exception cref="GitLabApiException">GitLab refused it, or couldn't be reached.</exception>
    public async Task SetVariableAsync(GitLabConnection conn, GitLabRepoRef project, string key, string value, string scope, CancellationToken ct = default)
    {
        var doing = $"save {key} to {project.FullName}'s CI/CD variables";
        using (var update = await SendAsync(conn, HttpMethod.Put, VariablePath(project, key, scope), new Dictionary<string, object> { ["value"] = value }, ct))
        {
            if (update.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureReadAsync(update, conn, doing, ct, writing: true);
                return;
            }
        }
        var body = new Dictionary<string, object>
        {
            ["key"] = key, ["value"] = value, ["environment_scope"] = scope, ["masked"] = false, ["protected"] = false, ["variable_type"] = "env_var",
        };
        using var create = await SendAsync(conn, HttpMethod.Post, $"projects/{project.ApiId}/variables", body, ct);
        await EnsureReadAsync(create, conn, doing, ct, writing: true);
    }

    /// <summary>Deletes a variable from one environment scope; one that's gone already is fine.</summary>
    /// <exception cref="GitLabApiException">GitLab refused it, or couldn't be reached.</exception>
    public async Task DeleteVariableAsync(GitLabConnection conn, GitLabRepoRef project, string key, string scope, CancellationToken ct = default)
    {
        using var response = await SendAsync(conn, HttpMethod.Delete, VariablePath(project, key, scope), null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureReadAsync(response, conn, $"delete {key} from {project.FullName}'s CI/CD variables", ct, writing: true);
    }

    private static string VariablePath(GitLabRepoRef project, string key, string scope) =>
        $"projects/{project.ApiId}/variables/{Uri.EscapeDataString(key)}?filter%5Benvironment_scope%5D={Uri.EscapeDataString(scope)}";

    // ---- Writes: what deploys report ----

    /// <summary>
    /// A commit status (pending, running, success, failed or canceled) under <paramref name="name"/>, one per app. GitLab
    /// moves a pending or running status of the same name and ref on through its state machine, and answers a move it
    /// doesn't make (running to running, after Gitberth restarted during a build, say) with its 400 "Cannot transition
    /// status": the status is in that state already, so that's taken as done, never as a failure.
    /// </summary>
    /// <param name="gitRef">The branch it's for; null to leave it to GitLab.</param>
    /// <returns>False when GitLab had it in that state already (the 400 "Cannot transition status").</returns>
    public async Task<bool> SetStatusAsync(GitLabConnection conn, GitLabRepoRef project, string sha, string state, string name, string description, string? targetUrl,
        string? gitRef, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["state"] = state, ["name"] = name, ["description"] = description };
        if (targetUrl is not null) body["target_url"] = targetUrl;
        if (gitRef is not null) body["ref"] = gitRef;
        using var response = await SendAsync(conn, HttpMethod.Post, $"projects/{project.ApiId}/statuses/{Uri.EscapeDataString(sha)}", body, ct, write: true);
        if (response.StatusCode == HttpStatusCode.BadRequest && IsUnchangedStatus(await ErrorTextAsync(response, ct))) return false;
        await EnsureWriteAsync(response, GitHubFeature.Statuses, $"set a commit status on {project.FullName}", ct);
        return true;
    }

    /// <summary>GitLab's 400 for a status move its state machine doesn't make: "Cannot transition status via :run from :running …".</summary>
    internal static bool IsUnchangedStatus(string error) => error.Contains("Cannot transition status", StringComparison.OrdinalIgnoreCase);

    /// <summary>The merge request's notes, newest first as GitLab lists them, to find Gitberth's own.</summary>
    public async Task<List<GitLabNote>> ListNotesAsync(GitLabConnection conn, GitLabRepoRef project, int iid, CancellationToken ct = default)
    {
        var all = new List<GitLabNote>();
        var doing = $"read the comments on {project.FullName}'s merge request !{iid.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            await foreach (var n in ListAsync(conn, $"projects/{project.ApiId}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}/notes", doing, ct, maxPages: 3))
                if (Long(n, "id") is long id)
                    all.Add(new GitLabNote(id, Str(n, "body") ?? "", n.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "username") : null, Bool(n, "system")));
        }
        catch (GitLabApiException ex)
        {
            throw AsWrite(ex, GitHubFeature.Comments, doing);
        }
        return all;
    }

    /// <returns>The new note's ID.</returns>
    public async Task<long> CreateNoteAsync(GitLabConnection conn, GitLabRepoRef project, int iid, string body, CancellationToken ct = default)
    {
        var doing = $"comment on {project.FullName}'s merge request !{iid.ToString(CultureInfo.InvariantCulture)}";
        using var response = await SendAsync(conn, HttpMethod.Post, $"projects/{project.ApiId}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}/notes",
            new Dictionary<string, object> { ["body"] = body }, ct, write: true);
        await EnsureWriteAsync(response, GitHubFeature.Comments, doing, ct);
        return ReadId(await response.Content.ReadAsStringAsync(ct)) ?? throw new GitLabWriteException("GitLab didn't say which comment it made.");
    }

    /// <returns>False when the note isn't there any more (someone deleted it), so a new one is made.</returns>
    public async Task<bool> UpdateNoteAsync(GitLabConnection conn, GitLabRepoRef project, int iid, long noteId, string body, CancellationToken ct = default)
    {
        using var response = await SendAsync(conn, HttpMethod.Put,
            $"projects/{project.ApiId}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}/notes/{noteId.ToString(CultureInfo.InvariantCulture)}",
            new Dictionary<string, object> { ["body"] = body }, ct, write: true);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;
        await EnsureWriteAsync(response, GitHubFeature.Comments, $"update its comment on {project.FullName}", ct);
        return true;
    }

    // ---- Writes: the project hook ----

    public async Task<List<GitLabHook>> ListHooksAsync(GitLabConnection conn, GitLabRepoRef project, CancellationToken ct = default)
    {
        var doing = $"read {project.FullName}'s webhooks";
        var all = new List<GitLabHook>();
        try
        {
            await foreach (var h in ListAsync(conn, $"projects/{project.ApiId}/hooks", doing, ct, maxPages: 3))
                if (Long(h, "id") is long id) all.Add(new GitLabHook(id, Str(h, "url"), Bool(h, "push_events"), Bool(h, "merge_requests_events")));
        }
        catch (GitLabApiException ex)
        {
            throw AsWrite(ex, GitHubFeature.Webhooks, doing);
        }
        return all;
    }

    /// <summary>
    /// Adds Gitberth's project hook (push and merge request events, its secret token, TLS checked), or sets an existing one
    /// (<paramref name="existingId"/>) to the same.
    /// </summary>
    /// <returns>The hook's ID; null when GitLab didn't say which one it added.</returns>
    public async Task<long?> SaveHookAsync(GitLabConnection conn, GitLabRepoRef project, long? existingId, string url, string secret, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["url"] = url, ["token"] = secret, ["push_events"] = true, ["merge_requests_events"] = true, ["enable_ssl_verification"] = true,
        };
        using var response = existingId is long id
            ? await SendAsync(conn, HttpMethod.Put, $"projects/{project.ApiId}/hooks/{id.ToString(CultureInfo.InvariantCulture)}", body, ct, write: true)
            : await SendAsync(conn, HttpMethod.Post, $"projects/{project.ApiId}/hooks", body, ct, write: true);
        await EnsureWriteAsync(response, GitHubFeature.Webhooks, $"add a webhook to {project.FullName}", ct);
        return existingId ?? ReadId(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Removes a project hook (a second copy of Gitberth's own); one that's gone already counts as removed.</summary>
    public async Task DeleteHookAsync(GitLabConnection conn, GitLabRepoRef project, long id, CancellationToken ct = default)
    {
        using var response = await SendAsync(conn, HttpMethod.Delete, $"projects/{project.ApiId}/hooks/{id.ToString(CultureInfo.InvariantCulture)}", null, ct, write: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureWriteAsync(response, GitHubFeature.Webhooks, $"remove a second copy of its webhook from {project.FullName}", ct);
    }

    // ---- The scope each feature needs ----

    /// <summary>What a token needs for a feature, and the role on the project its user needs, as the messages say it.</summary>
    public static string NeedFor(GitHubFeature feature) => feature switch
    {
        GitHubFeature.Statuses => "the api scope, and at least the Developer role on the project",
        GitHubFeature.Comments => "the api scope, and at least the Reporter role on the project",
        _ => "the api scope, and at least the Maintainer role on the project",
    };

    // ---- Requests ----

    private async Task<HttpResponseMessage> SendAsync(GitLabConnection conn, HttpMethod method, string path, object? body, CancellationToken ct, bool write = false)
    {
        using var request = new HttpRequestMessage(method, conn.ApiRoot + path);
        if (conn.HasToken) request.Headers.Add("PRIVATE-TOKEN", conn.Token);
        request.Headers.UserAgent.ParseAdd("Gitberth");
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            return await httpFactory.CreateClient(ClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            var text = $"{conn.Host} couldn't be reached ({ex.Message}).";
            throw write ? new GitLabWriteException(text, retrySoon: true) : new GitLabApiException(text);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client gave up: GitLab may still have done it, and only its answer was lost.
            var text = $"{conn.Host} didn't answer in time.";
            throw write ? new GitLabWriteException(text, retrySoon: true) : new GitLabApiException(text);
        }
    }

    /// <summary>
    /// Every item of a list GitLab pages, 100 at a time: page after page while X-Next-Page names one, at most
    /// <paramref name="maxPages"/> of them.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> ListAsync(GitLabConnection conn, string path, string doing,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, int maxPages = MaxPages)
    {
        var page = "1";
        for (var read = 0; read < maxPages && page is not null; read++)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            using var response = await SendAsync(conn, HttpMethod.Get, $"{path}{separator}per_page=100&page={page}", null, ct);
            await EnsureReadAsync(response, conn, doing, ct);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                throw new GitLabApiException($"GitLab's answer couldn't be read when Gitberth tried to {doing}.");
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new GitLabApiException($"GitLab's answer couldn't be read when Gitberth tried to {doing}.");
                foreach (var item in doc.RootElement.EnumerateArray()) yield return item.Clone();
            }
            page = NextPage(response);
        }
    }

    /// <summary>X-Next-Page: the next page's number; null on the last page (the header empty, or not there).</summary>
    internal static string? NextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Next-Page", out var values) && values.FirstOrDefault()?.Trim() is { Length: > 0 } next
            && int.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? next : null;

    /// <summary>How long a 429 asks to wait: Retry-After in seconds or as a date, else RateLimit-Reset (a Unix time); null when it says neither.</summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) return delta;
            if (retry.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        }
        if (response.Headers.TryGetValues("RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(unix);
            return at > now ? at - now : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>GitLab's rate limit, as Gitberth says it: when to try again, when GitLab said.</summary>
    internal static string RateLimitText(TimeSpan? retryAfter, string doing) =>
        $"GitLab's API rate limit for this token is used up for the moment, so Gitberth couldn't {doing}. " + (retryAfter is { } wait
            ? $"GitLab asks to wait {Wait(wait)}; Gitberth asks again after that."
            : "Gitberth asks again later.");

    private static string Wait(TimeSpan t) => t.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} seconds" : $"{(int)Math.Ceiling(t.TotalMinutes)} minutes";

    // A read (or a variable's save): 401 the token, 403 a scope or role it lacks, 404 a project it can't see (or that doesn't
    // exist), 429 the rate limit.
    private static async Task EnsureReadAsync(HttpResponseMessage response, GitLabConnection conn, string doing, CancellationToken ct, bool writing = false)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfterOf(response, DateTimeOffset.UtcNow);
            throw new GitLabApiException(RateLimitText(wait, doing), status, wait);
        }
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "GitLab says the token is invalid, expired or revoked. Update it in Settings › GitLab.",
            HttpStatusCode.Forbidden => $"The GitLab token isn't allowed to {doing}: it needs {(writing ? "the api scope" : "the read_api (or api) scope")}, and its user a role on the project that may (Maintainer, for CI/CD variables).",
            HttpStatusCode.NotFound => conn.HasToken
                ? $"GitLab can't {doing}: the project isn't there, or the token's user can't see it. Check the URL, and that the user is a member of the project."
                : $"GitLab can't {doing} without a token. If the project is private, or this needs a sign-in, add a token in Settings › GitLab.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => $"GitLab refused to {doing}: {await ErrorTextAsync(response, ct)}",
            _ => $"GitLab answered {(int)status} when Gitberth tried to {doing}: {await ErrorTextAsync(response, ct)}",
        };
        throw new GitLabApiException(message, status);
    }

    // What deploys report and the hook: 401 is the token itself, everywhere; 403 or 404 is about this project only (a scope
    // the token lacks, a role its user lacks there, or a project it can't see). 429 is the rate limit, not a permission.
    private static async Task EnsureWriteAsync(HttpResponseMessage response, GitHubFeature feature, string doing, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
            throw new GitLabWriteException(RateLimitText(RetryAfterOf(response, DateTimeOffset.UtcNow), doing), status: status);
        if (status == HttpStatusCode.Unauthorized)
            throw new GitLabWriteException("GitLab says the token is invalid, expired or revoked. Update it in Settings › GitLab.", badToken: true, status: status);
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new GitLabWriteException(MissingPermissionText(feature, doing), missingPermission: true, status: status);
        throw new GitLabWriteException($"GitLab didn't let Gitberth {doing}: {await ErrorTextAsync(response, ct)} (HTTP {(int)status}).", status: status,
            retrySoon: (int)status >= 500);
    }

    /// <summary>What to tell someone whose token can't do this on a project: the scope and role it needs.</summary>
    internal static string MissingPermissionText(GitHubFeature feature, string doing) =>
        $"The GitLab token isn't allowed to {doing}. It needs {NeedFor(feature)}. Gitberth stops trying on this project for an hour, or until the token is saved again.";

    // A read that's part of a write feature (a hook's list, a merge request's notes), said as that feature's refusal. One
    // with no answer at all (no time, no connection, an answer that couldn't be read) or GitLab's own failure is for a moment.
    private static GitLabWriteException AsWrite(GitLabApiException ex, GitHubFeature feature, string doing) => ex.Status switch
    {
        HttpStatusCode.Unauthorized => new GitLabWriteException(ex.Message, badToken: true, status: ex.Status),
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound => new GitLabWriteException(MissingPermissionText(feature, doing), missingPermission: true, status: ex.Status),
        _ => new GitLabWriteException(ex.Message, status: ex.Status, retrySoon: ex.Status is null || (int)ex.Status >= 500),
    };

    /// <summary>
    /// GitLab's own words for an error: its message (a string, or field by field) or error; a short line of plain text
    /// (Rack::Attack's "Retry later", say) as it is; else the reason phrase.
    /// </summary>
    internal static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var m))
                {
                    if (m.ValueKind == JsonValueKind.String) return m.GetString() ?? "";
                    if (m.ValueKind == JsonValueKind.Object)
                        return string.Join("; ", m.EnumerateObject().Select(p => $"{p.Name} {(p.Value.ValueKind == JsonValueKind.Array ? string.Join(", ", p.Value.EnumerateArray().Select(v => v.ToString())) : p.Value.ToString())}"));
                    if (m.ValueKind == JsonValueKind.Array) return string.Join("; ", m.EnumerateArray().Select(v => v.ToString()));
                }
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Not JSON: a line of plain text is GitLab's words too; an HTML page isn't.
            var line = text.Trim();
            if (line.Length is > 0 and <= 300 && !line.Contains('<') && !line.Contains('\n')) return line;
        }
        return response.ReasonPhrase ?? "";
    }

    private static PullRequestInfo MergeRequest(JsonElement mr, GitLabRepoRef project)
    {
        try
        {
            var source = Long(mr, "source_project_id");
            var target = Long(mr, "target_project_id");
            return new PullRequestInfo(
                mr.GetProperty("iid").GetInt32(),
                Str(mr, "title") ?? "",
                Str(mr, "source_branch") ?? "",
                Str(mr, "sha") ?? "",
                // Another source project is someone else's code: a fork, as GitHub's pull requests from forks.
                FromFork: source is null || target is null || source != target,
                Url: Str(mr, "web_url"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitLabApiException($"GitLab's list of {project.FullName}'s merge requests couldn't be read.");
        }
    }

    private static long? ReadId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Long(doc.RootElement, "id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
