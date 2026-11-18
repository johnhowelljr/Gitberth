using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>Reads what a GitLab webhook delivery is about.</summary>
public static class GitLabWebhookEvents
{
    /// <summary>The events that can mean a deploy: a push (a new commit, or a branch deleted), a merge request opened, changed or closed.</summary>
    public static bool Deploys(string? eventName) => eventName is "Push Hook" or "Merge Request Hook";

    /// <summary>
    /// The secret token GitLab sent back (X-Gitlab-Token) is the one Gitberth set on its hook. Compared in fixed time, as
    /// SHA-256 hashes, so neither its characters nor its length can be told from how long the answer takes.
    /// </summary>
    public static bool TokenMatches(string? secret, string? given)
    {
        if (string.IsNullOrEmpty(secret) || given is null) return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>The project a delivery is about: project.path_with_namespace (a merge request's target project); null when it doesn't say.</summary>
    public static string? ProjectOf(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!JsonDocument.TryParseValue(ref reader, out var parsed) || parsed is null) return null;
            using var doc = parsed;
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("project", out var project)
                && project.ValueKind == JsonValueKind.Object && project.TryGetProperty("path_with_namespace", out var path) && path.ValueKind == JsonValueKind.String
                && path.GetString() is { Length: > 0 } name ? name.Trim('/') : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// POST /hooks/gitlab: GitLab tells Gitberth about a push or a merge request on a project it deploys, and the watcher looks
/// at that project at once instead of at its next check. Anonymous and without antiforgery: GitLab sends back the secret
/// token Gitberth set on its hook (X-Gitlab-Token), which is checked first, in fixed time, before the body is read. A bad
/// one gets a bare 401 and counts towards <see cref="WebhookThrottle"/> (shared with GitHub's), which answers too many of
/// them 429; a delivery about a project Gitberth deploys gets 202; anything else 204. GitLab gives a delivery 10 seconds and
/// takes anything but 2xx as failed (and turns off a hook that keeps failing), so the answer never waits on more than one
/// small database read, and a delivery too big to read (over 1 MB) still gets 202, after waking the watcher for every
/// project. Any other method gets a bare 405, and a failure a bare 500: the webhook's host never shows Gitberth's pages.
/// It never deploys by itself: the watcher decides.
/// </summary>
public sealed class GitLabWebhookEndpoint(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, WatchState watch, WebhookThrottle throttle,
    ILogger<GitLabWebhookEndpoint> logger)
{
    public const string Path = "/hooks/gitlab";
    public const int MaxBody = 1024 * 1024;

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Maps the endpoint for every method, anonymous and without antiforgery, so its host only ever answers a status code:
    /// never the "not found" page a 405 would be turned into, nor the error page.
    /// </summary>
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder app) =>
        app.Map(Path, context => context.RequestServices.GetRequiredService<GitLabWebhookEndpoint>().HandleAsync(context))
            .AllowAnonymous()
            .DisableAntiforgery();

    /// <summary>Whether the token is the hook's: a bad one is counted against its sender and answered 401, or 429 past the throttle's limit.</summary>
    /// <returns>Null when it's the hook's; else the status code to answer.</returns>
    public async Task<int?> CheckTokenAsync(string? token, string sender, CancellationToken ct = default)
    {
        var secret = await settings.GetSecretAsync(SettingKeys.GitLabWebhookSecret, ct);
        if (GitLabWebhookEvents.TokenMatches(secret, token)) return null;
        var now = Now();
        if (throttle.Blocked(sender, now)) return StatusCodes.Status429TooManyRequests;
        throttle.Failed(sender, now);
        return StatusCodes.Status401Unauthorized;
    }

    /// <summary>The answer to one delivery, as an HTTP status code.</summary>
    /// <param name="host">The host name it came in at: a delivery with the hook's token there proves GitLab reaches it, certificate and all.</param>
    public async Task<int> HandleAsync(string? eventName, string? token, byte[] body, string sender, CancellationToken ct = default, string? host = null)
    {
        // Checked first: a delivery with the hook's token always gets through, and only bad ones are ever throttled.
        if (await CheckTokenAsync(token, sender, ct) is int refused) return refused;
        await DeliveredAtAsync(host, ct);
        if (!GitLabWebhookEvents.Deploys(eventName) || GitLabWebhookEvents.ProjectOf(body) is not { } path) return StatusCodes.Status204NoContent;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployed = await db.Apps.AsNoTracking().Where(a => a.Source == AppSource.Git && a.Owner.StartsWith(RepoRef.GitLabPrefix))
            .Select(a => new { a.Owner, a.Repo }).Distinct().ToListAsync(ct);
        var match = deployed.FirstOrDefault(a => string.Equals(RepoRef.FullNameOf(a.Owner, a.Repo), path, StringComparison.OrdinalIgnoreCase));
        if (match is null) return StatusCodes.Status204NoContent;
        logger.LogInformation("GitLab says {Project} changed ({Event}): checking it now", path, eventName);
        watch.CheckNowFromWebhook(match.Owner, match.Repo, sender);
        return StatusCodes.Status202Accepted;
    }

    // Where a delivery with the hook's token came in: proof GitLab reaches the webhook's host name, certificate and all, as a
    // signed GitHub delivery is.
    private async Task DeliveredAtAsync(string? host, CancellationToken ct)
    {
        if (host?.Trim().ToLowerInvariant() is { Length: > 0 } at && await settings.GetAsync(SettingKeys.GitHubWebhookDelivered, ct) != at)
            await settings.SetAsync(SettingKeys.GitHubWebhookDelivered, at, ct);
    }

    /// <summary>The endpoint itself: checks the token, reads at most <see cref="MaxBody"/>, and answers with a status code and no body.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        // A bare status code, never Gitberth's "not found" or error page: GitLab only needs the code.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Post;
            return;
        }
        try
        {
            var sender = request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } ip ? ip : context.Connection.RemoteIpAddress?.ToString() ?? "";
            var token = request.Headers["X-Gitlab-Token"].FirstOrDefault();
            // The token first, from its header: a bad one never has its body read.
            if (await CheckTokenAsync(token, sender, context.RequestAborted) is int refused)
            {
                context.Response.StatusCode = refused;
                return;
            }
            using var buffer = new MemoryStream();
            var tooBig = request.ContentLength > MaxBody;
            if (!tooBig)
            {
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxBody)
                    {
                        tooBig = true;
                        break;
                    }
                }
            }
            if (tooBig)
            {
                // GitLab's, but too big to read: every project is looked at now, and GitLab hears it was taken, so it never
                // turns the hook off for it.
                await DeliveredAtAsync(request.Host.Host, context.RequestAborted);
                logger.LogInformation("GitLab sent a delivery too big to read ({Event}): checking every repository now", request.Headers["X-Gitlab-Event"].FirstOrDefault());
                watch.CheckNow();
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            context.Response.StatusCode = await HandleAsync(request.Headers["X-Gitlab-Event"].FirstOrDefault(), token, buffer.ToArray(), sender,
                context.RequestAborted, request.Host.Host);
        }
        catch (Exception ex)
        {
            // Not the error page either: the code alone, and the reason in Gitberth's log (unless GitLab just hung up).
            if (!context.RequestAborted.IsCancellationRequested) logger.LogError(ex, "GitLab's webhook delivery couldn't be handled");
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }
}
