using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Gitberth.Previews;

namespace Gitberth.Services;

/// <summary>
/// A repo's open pull requests, for its apps' previews. Public repos answer without a token, but GitHub then allows only
/// 60 asks an hour from this host, so the watcher asks when the repo's pull requests changed, when GitHub told about
/// them, and every 5 minutes, never on every pass. With a saved token it's 5,000 an hour, and private repos answer.
/// </summary>
public sealed class GitHubPulls(IHttpClientFactory httpFactory, SettingsService settings)
{
    private const string ApiRoot = "https://api.github.com/";

    /// <summary>How many pages of 100 are read at most.</summary>
    private const int MaxPages = 5;

    /// <exception cref="GitHubApiException">GitHub couldn't be reached, or said no.</exception>
    public async Task<List<PullRequestInfo>> ListOpenAsync(string owner, string repo, CancellationToken ct = default)
    {
        var token = await settings.GetSecretAsync(SettingKeys.GitHubToken, ct);
        var all = new List<PullRequestInfo>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}repos/{owner}/{repo}/pulls?state=open&per_page=100&page={page}");
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("Gitberth");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response;
            try
            {
                response = await httpFactory.CreateClient("github").SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new GitHubApiException($"api.github.com couldn't be reached ({ex.Message}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GitHubApiException("api.github.com didn't answer in time.");
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode) throw new GitHubApiException(Explain(response, owner, repo, token is { Length: > 0 }));
                var items = Parse(await response.Content.ReadAsStringAsync(ct), owner, repo);
                all.AddRange(items);
                if (items.Count < 100) break;
            }
        }
        return all;
    }

    /// <summary>Whether a closed pull request was merged; null when GitHub can't say.</summary>
    public async Task<bool?> MergedAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        var token = await settings.GetSecretAsync(SettingKeys.GitHubToken, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}repos/{owner}/{repo}/pulls/{number}");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Gitberth");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await httpFactory.CreateClient("github").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("merged", out var merged) && merged.ValueKind is JsonValueKind.True or JsonValueKind.False ? merged.GetBoolean() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The pull requests in GitHub's answer; one from a fork whose repo was deleted counts as from a fork.</summary>
    /// <exception cref="GitHubApiException">The answer isn't a list of pull requests.</exception>
    internal static List<PullRequestInfo> Parse(string json, string owner, string repo)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<PullRequestInfo>();
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                var head = pr.GetProperty("head");
                var headRepo = head.TryGetProperty("repo", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("full_name", out var name)
                    ? name.GetString() : null;
                var baseRepo = pr.TryGetProperty("base", out var b) && b.TryGetProperty("repo", out var br) && br.ValueKind == JsonValueKind.Object
                    && br.TryGetProperty("full_name", out var bn) ? bn.GetString() : $"{owner}/{repo}";
                list.Add(new PullRequestInfo(
                    pr.GetProperty("number").GetInt32(),
                    pr.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    head.GetProperty("ref").GetString() ?? "",
                    head.GetProperty("sha").GetString() ?? "",
                    FromFork: headRepo is null || !string.Equals(headRepo, baseRepo, StringComparison.OrdinalIgnoreCase),
                    Url: pr.TryGetProperty("html_url", out var url) ? url.GetString() : null));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitHubApiException($"GitHub's list of {owner}/{repo}'s pull requests couldn't be read.");
        }
    }

    private static string Explain(HttpResponseMessage response, string owner, string repo, bool hasToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
            return hasToken
                ? "GitHub's API rate limit for this token is used up for the moment, so pull requests are checked again later."
                : "GitHub's API rate limit for this host is used up for the moment (60 an hour without a token). Add a GitHub token in Settings, or wait.";
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub says the token is invalid or expired. Update it in Settings.",
            HttpStatusCode.NotFound or HttpStatusCode.Forbidden => hasToken
                ? $"GitHub can't list {owner}/{repo}'s pull requests with this token. Check that it can access the repo (Pull requests: Read-only)."
                : $"GitHub can't list {owner}/{repo}'s pull requests. If the repo is private, add a token in Settings.",
            _ => $"GitHub answered {(int)response.StatusCode} when asked for {owner}/{repo}'s pull requests.",
        };
    }
}
