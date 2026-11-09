using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gitberth.Services;

public sealed record TokenCheck(bool Valid, string? Login, DateTimeOffset? ExpiresAt, string? Warning);

/// <summary>One of a repo's GitHub Actions variables (Settings > Secrets and variables > Actions > Variables).</summary>
public sealed record RepoVariable(string Name, string Value);

public sealed class GitHubApiException(string message) : Exception(message);

/// <summary>
/// Gitberth's GitHub API calls: who owns the token and when it expires, and the repo variables apps read their
/// environment from. (Clones and branch checks go through git.)
/// </summary>
public sealed class GitHubApi(IHttpClientFactory httpFactory)
{
    private const string ApiRoot = "https://api.github.com/";

    /// <summary>All of the repo's variables. GitHub pages them 30 at a time and allows up to 1,000.</summary>
    public async Task<List<RepoVariable>> ListVariablesAsync(string token, string owner, string repo, CancellationToken ct = default)
    {
        var all = new List<RepoVariable>();
        for (var page = 1; page <= 34; page++)
        {
            using var response = await SendAsync(token, HttpMethod.Get, $"repos/{owner}/{repo}/actions/variables?per_page=30&page={page}", null, ct);
            await EnsureOkAsync(response, owner, repo, writing: false, ct);
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var items = doc.RootElement.GetProperty("variables");
                foreach (var v in items.EnumerateArray())
                    all.Add(new RepoVariable(v.GetProperty("name").GetString() ?? "", v.TryGetProperty("value", out var value) ? value.GetString() ?? "" : ""));
                var total = doc.RootElement.TryGetProperty("total_count", out var t) ? t.GetInt32() : 0;
                if (items.GetArrayLength() == 0 || all.Count >= total) break;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new GitHubApiException($"GitHub's list of {owner}/{repo}'s variables couldn't be read.");
            }
        }
        return all;
    }

    /// <summary>Creates the variable, or updates it when it already exists.</summary>
    public async Task SetVariableAsync(string token, string owner, string repo, string name, string value, CancellationToken ct = default)
    {
        var body = new { name, value };
        using var update = await SendAsync(token, HttpMethod.Patch, $"repos/{owner}/{repo}/actions/variables/{Uri.EscapeDataString(name)}", body, ct);
        if (update.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureOkAsync(update, owner, repo, writing: true, ct);
            return;
        }
        using var create = await SendAsync(token, HttpMethod.Post, $"repos/{owner}/{repo}/actions/variables", body, ct);
        await EnsureOkAsync(create, owner, repo, writing: true, ct);
    }

    public async Task DeleteVariableAsync(string token, string owner, string repo, string name, CancellationToken ct = default)
    {
        using var response = await SendAsync(token, HttpMethod.Delete, $"repos/{owner}/{repo}/actions/variables/{Uri.EscapeDataString(name)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureOkAsync(response, owner, repo, writing: true, ct);
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
            throw new GitHubApiException($"api.github.com couldn't be reached ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubApiException("api.github.com didn't answer in time.");
        }
    }

    // Fine-grained tokens answer 403 when the Variables permission is missing, and 404 when the repo isn't one they can see.
    // Classic tokens need the repo scope. A 403 or 429 can also be GitHub's rate limit, which isn't about permissions.
    private static async Task EnsureOkAsync(HttpResponseMessage response, string owner, string repo, bool writing, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (IsRateLimited(response))
            throw new GitHubApiException("GitHub's API rate limit for this token is used up for the moment. Try again in a few minutes.");
        const string fix = "A classic token needs the repo scope. For a fine-grained token, edit it at github.com/settings/personal-access-tokens and set Repository permissions > Variables to Read and write";
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub says the token is invalid or expired. Update it in Settings.",
            HttpStatusCode.Forbidden => $"The GitHub token isn't allowed to {(writing ? "change" : "read")} {owner}/{repo}'s variables. {fix}.",
            HttpStatusCode.NotFound => $"GitHub can't find {owner}/{repo}'s variables with this token. {fix}, and check that the token can access the repo.",
            HttpStatusCode.UnprocessableEntity => "GitHub rejected the variable: " + await ErrorMessageAsync(response, ct),
            _ => $"GitHub answered {(int)response.StatusCode}: {await ErrorMessageAsync(response, ct)}",
        };
        throw new GitHubApiException(message);
    }

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

    public async Task<TokenCheck> CheckTokenAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Gitberth");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await httpFactory.CreateClient("github").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new TokenCheck(false, null, null, "GitHub says this token is invalid or expired.");

            var expires = response.Headers.TryGetValues("github-authentication-token-expiration", out var values)
                ? ParseExpiry(values.FirstOrDefault())
                : null;
            if (!response.IsSuccessStatusCode)
                return new TokenCheck(true, null, expires, $"Saved, but GitHub answered {(int)response.StatusCode} when asked who owns it. Repo access is checked when you add an app.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var login = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
            return new TokenCheck(true, login, expires, null);
        }
        catch (HttpRequestException ex)
        {
            return new TokenCheck(true, null, null, $"Saved, but api.github.com couldn't be reached to verify it ({ex.Message}).");
        }
        catch (TaskCanceledException)
        {
            return new TokenCheck(true, null, null, "Saved, but api.github.com didn't answer in time to verify it.");
        }
    }

    /// <summary>Header values look like "2026-12-01 00:00:00 UTC" or "2026-12-01 00:00:00 -0800".</summary>
    internal static DateTimeOffset? ParseExpiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Replace(" UTC", " +00:00", StringComparison.Ordinal);
        string[] formats = ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss zz00", "yyyy-MM-dd HH:mm:ss K"];
        if (DateTimeOffset.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact)) return exact;
        if (v.Length > 5 && (v[^5] == '+' || v[^5] == '-') && char.IsDigit(v[^1]))
            v = v[..^2] + ":" + v[^2..];
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
