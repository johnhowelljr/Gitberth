using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gitberth.Services;

/// <summary>
/// What Gitberth tells a Git host about deploys, or asks it to do, each with the token permission it needs: GitHub's, and
/// GitLab's the same three (commit statuses, a merge request's comment, the project hook).
/// </summary>
public enum GitHubFeature { Statuses, Comments, Webhooks }

/// <summary>A Git host refused, or failed, something Gitberth told it or asked it to do: GitHub's or GitLab's.</summary>
/// <param name="missingPermission">The host refused on this repo (403 or 404): the token lacks the permission there, or can't see the repo. Asking again there won't help for a while; other repos may be fine.</param>
/// <param name="badToken">The host refused the token itself (401): nothing works with it until it's replaced.</param>
/// <param name="retrySoon">It failed for a moment: the host didn't answer in time, couldn't be reached, or failed itself (a 5xx). What was asked may even have been done, its answer lost on the way. Asked again soon, it likely works; a refusal or a rate limit never is this.</param>
/// <param name="rateLimited">The host's rate limit for the token is used up: every call with it fails until it's reset, not only this one.</param>
public abstract class HostWriteException(string message, bool missingPermission = false, bool badToken = false, bool retrySoon = false, bool rateLimited = false)
    : Exception(message)
{
    public bool MissingPermission { get; } = missingPermission;
    public bool BadToken { get; } = badToken;
    public bool RetrySoon { get; } = retrySoon;
    public bool RateLimited { get; } = rateLimited;
}

/// <param name="missingPermission">GitHub refused on this repo (403 or 404): the token lacks the permission there, or can't see the repo. Asking again there won't help for a while; other repos may be fine.</param>
/// <param name="badToken">GitHub refused the token itself (401): nothing works with it until it's replaced.</param>
/// <param name="retrySoon">GitHub failed for a moment (see <see cref="HostWriteException"/>).</param>
/// <param name="rateLimited">GitHub's rate limit for the token is used up (a 429, or a 403 that says so).</param>
public sealed class GitHubWriteException(string message, bool missingPermission = false, bool badToken = false, bool retrySoon = false, bool rateLimited = false)
    : HostWriteException(message, missingPermission, badToken, retrySoon, rateLimited);

/// <summary>A commit status: pending, success, failure or error, with a line of text and a link, under a context of its own.</summary>
public sealed record CommitStatus(string State, string Description, string Context, string? TargetUrl);

/// <summary>A repo's webhook as GitHub lists it.</summary>
public sealed record RepoHook(long Id, string? Url, bool Active, IReadOnlyList<string> Events);

/// <summary>
/// Gitberth's GitHub API calls that report deploys and set up instant deploys: commit statuses, the comment on a pull
/// request with its preview, and the repo's webhook. They use the same token and client as <see cref="GitHubApi"/>.
/// </summary>
public sealed class GitHubWrites(IHttpClientFactory httpFactory)
{
    private const string ApiRoot = "https://api.github.com/";

    /// <summary>The events Gitberth's webhook asks for: a push, a pull request opened, changed or closed, a branch deleted.</summary>
    public static readonly IReadOnlyList<string> HookEvents = ["push", "pull_request", "delete"];

    /// <summary>The fine-grained token permission each feature needs, as GitHub's token page names it.</summary>
    public static string PermissionFor(GitHubFeature feature) => feature switch
    {
        GitHubFeature.Statuses => "Commit statuses: Read and write",
        GitHubFeature.Comments => "Pull requests: Read and write",
        _ => "Webhooks: Read and write",
    };

    /// <summary>What a classic token needs for it.</summary>
    public static string ClassicScopeFor(GitHubFeature feature) => feature == GitHubFeature.Webhooks ? "the repo and admin:repo_hook scopes" : "the repo scope";

    public async Task SetStatusAsync(string token, string owner, string repo, string sha, CommitStatus status, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["state"] = status.State,
            ["description"] = status.Description,
            ["context"] = status.Context,
        };
        if (status.TargetUrl is { } url) body["target_url"] = url;
        using var response = await SendAsync(token, HttpMethod.Post, $"repos/{owner}/{repo}/statuses/{Uri.EscapeDataString(sha)}", body, ct);
        await EnsureOkAsync(response, GitHubFeature.Statuses, $"set a commit status on {owner}/{repo}", ct);
    }

    /// <returns>The new comment's ID.</returns>
    public async Task<long> CreateCommentAsync(string token, string owner, string repo, int pullRequest, string body, CancellationToken ct = default)
    {
        using var response = await SendAsync(token, HttpMethod.Post, $"repos/{owner}/{repo}/issues/{pullRequest}/comments", new { body }, ct);
        await EnsureOkAsync(response, GitHubFeature.Comments, $"comment on {owner}/{repo}'s pull request #{pullRequest}", ct);
        return ReadId(await response.Content.ReadAsStringAsync(ct)) ?? throw new GitHubWriteException("GitHub didn't say which comment it made.");
    }

    /// <returns>False when the comment isn't there any more (someone deleted it), so a new one is made.</returns>
    public async Task<bool> UpdateCommentAsync(string token, string owner, string repo, long commentId, string body, CancellationToken ct = default)
    {
        using var response = await SendAsync(token, HttpMethod.Patch, $"repos/{owner}/{repo}/issues/comments/{commentId}", new { body }, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;
        await EnsureOkAsync(response, GitHubFeature.Comments, $"update its comment on {owner}/{repo}", ct);
        return true;
    }

    public async Task<List<RepoHook>> ListHooksAsync(string token, string owner, string repo, CancellationToken ct = default)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"repos/{owner}/{repo}/hooks?per_page=100", null, ct);
        await EnsureOkAsync(response, GitHubFeature.Webhooks, $"read {owner}/{repo}'s webhooks", ct);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.EnumerateArray().Select(h => new RepoHook(
                h.GetProperty("id").GetInt64(),
                h.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("url", out var url) ? url.GetString() : null,
                !h.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False,
                h.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array
                    ? events.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : [])).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitHubWriteException($"GitHub's list of {owner}/{repo}'s webhooks couldn't be read.");
        }
    }

    /// <summary>Adds Gitberth's webhook to the repo, or sets an existing one (same URL) to send what Gitberth needs, signed with its secret.</summary>
    /// <returns>The webhook's ID; null when GitHub didn't say which one it added.</returns>
    public async Task<long?> SaveHookAsync(string token, string owner, string repo, long? existingId, string url, string secret, CancellationToken ct = default)
    {
        var body = new
        {
            name = "web",
            active = true,
            events = HookEvents,
            config = new { url, content_type = "json", secret, insecure_ssl = "0" },
        };
        using var response = existingId is long id
            ? await SendAsync(token, HttpMethod.Patch, $"repos/{owner}/{repo}/hooks/{id}", body, ct)
            : await SendAsync(token, HttpMethod.Post, $"repos/{owner}/{repo}/hooks", body, ct);
        await EnsureOkAsync(response, GitHubFeature.Webhooks, $"add a webhook to {owner}/{repo}", ct);
        return existingId ?? ReadId(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Gitberth");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            return await httpFactory.CreateClient("github").SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubWriteException($"api.github.com couldn't be reached ({ex.Message}).", retrySoon: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client gave up: GitHub may still have done it, and only its answer was lost.
            throw new GitHubWriteException("api.github.com didn't answer in time.", retrySoon: true);
        }
    }

    // A fine-grained token answers 403 when it lacks the permission and 404 for a repo it can't see, which is about that
    // repo only; 401 is the token itself. A classic one needs its scopes. A 403 or 429 can also be the rate limit, which
    // isn't about permissions.
    private static async Task EnsureOkAsync(HttpResponseMessage response, GitHubFeature feature, string doing, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (IsRateLimited(response))
            throw new GitHubWriteException($"GitHub's API rate limit for this token is used up, so Gitberth couldn't {doing}. It tries again with the next deploy.", rateLimited: true);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new GitHubWriteException("GitHub says the token is invalid or expired. Update it in Settings.", badToken: true);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new GitHubWriteException(MissingPermissionText(feature, doing), missingPermission: true);
        var status = (int)response.StatusCode;
        // GitHub failing itself (a 5xx) is for a moment; so is its "Hook already exists" for a webhook an earlier request
        // made whose answer was lost: the next try finds that one by its payload URL.
        var soon = status >= 500 || (feature == GitHubFeature.Webhooks && status == 422 && await HookExistsAsync(response, ct));
        throw new GitHubWriteException($"GitHub didn't let Gitberth {doing}: {await ErrorMessageAsync(response, ct)} (HTTP {status}).", retrySoon: soon);
    }

    // GitHub's 422 for a second webhook with the same URL and an event in common: "Hook already exists on this repository".
    private static async Task<bool> HookExistsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.String && m.GetString()!.Contains("already exists", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What to tell someone whose token can't do this on a repo: the permission it needs, for either kind of token.</summary>
    internal static string MissingPermissionText(GitHubFeature feature, string doing) =>
        $"The GitHub token isn't allowed to {doing}. For a fine-grained token, edit it at github.com/settings/personal-access-tokens: the repo must be one of its repositories, "
        + $"with Repository permissions > {PermissionFor(feature)}; a classic token needs {ClassicScopeFor(feature)}. "
        + "Gitberth stops trying on this repo for an hour, or until the token is saved again.";

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")));

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "";
        }
    }

    private static long? ReadId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
