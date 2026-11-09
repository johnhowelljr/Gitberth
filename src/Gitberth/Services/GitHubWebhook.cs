using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>GitHub's webhook signature: HMAC-SHA256 of the body with the hook's secret, sent as X-Hub-Signature-256: sha256=&lt;hex&gt;.</summary>
public static class WebhookSignature
{
    /// <summary>A new secret for the webhook: 32 random bytes as hex.</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The header GitHub sends with this body, for tests and for checking.</summary>
    public static string Sign(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>Whether the header is the body's signature with the secret, compared in fixed time.</summary>
    public static bool Verify(string? secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(header.AsSpan("sha256=".Length).Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}

/// <summary>Reads what a webhook delivery is about.</summary>
public static class WebhookEvents
{
    /// <summary>The events that can mean a deploy: a push, a pull request opened, changed or closed, a branch deleted.</summary>
    public static bool Deploys(string? eventName) => eventName is "push" or "pull_request" or "delete";

    /// <summary>The repository a delivery is about (repository.owner.login and repository.name); null when it doesn't say.</summary>
    public static (string Owner, string Repo)? RepoOf(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!JsonDocument.TryParseValue(ref reader, out var parsed) || parsed is null) return null;
            using var doc = parsed;
            if (!doc.RootElement.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object) return null;
            if (repo.TryGetProperty("full_name", out var full) && full.GetString()?.Split('/') is [{ Length: > 0 } o, { Length: > 0 } r]) return (o, r);
            var owner = repo.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object && ow.TryGetProperty("login", out var login) ? login.GetString() : null;
            var name = repo.TryGetProperty("name", out var n) ? n.GetString() : null;
            return owner is { Length: > 0 } && name is { Length: > 0 } ? (owner, name) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Deliveries whose signature was wrong, by sender: after <see cref="Limit"/> in <see cref="Window"/> that sender is
/// answered 429 without a check until the window passes. Behind the tunnel the sender is Cloudflare's CF-Connecting-IP.
/// </summary>
public sealed class WebhookThrottle
{
    public const int Limit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> _failures = new();

    public bool Blocked(string sender, DateTime now) =>
        _failures.TryGetValue(sender, out var f) && now - f.Since < Window && f.Count >= Limit;

    public void Failed(string sender, DateTime now)
    {
        _failures.AddOrUpdate(sender, (1, now), (_, f) => now - f.Since >= Window ? (1, now) : (f.Count + 1, f.Since));
        // Old senders go, so the map can't grow without end.
        if (_failures.Count > 1000)
            foreach (var old in _failures.Where(x => now - x.Value.Since >= Window).Select(x => x.Key).ToList()) _failures.TryRemove(old, out _);
    }
}

/// <summary>
/// POST /hooks/github: GitHub tells Gitberth about a push, a pull request or a deleted branch, and the watcher looks at
/// that repo at once instead of at its next check. Anonymous and without antiforgery (GitHub signs it instead); the body
/// is capped at 1 MB. A bad signature gets a bare 401 and counts towards <see cref="WebhookThrottle"/>; ping gets 200; a
/// delivery about a repo Gitberth deploys gets 202; anything else 204. It never deploys by itself: the watcher decides.
/// </summary>
public sealed class GitHubWebhookEndpoint(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, WatchState watch, WebhookThrottle throttle,
    ILogger<GitHubWebhookEndpoint> logger)
{
    public const string Path = "/hooks/github";
    public const int MaxBody = 1024 * 1024;

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>The answer to one delivery, as an HTTP status code.</summary>
    public async Task<int> HandleAsync(string? eventName, string? signature, byte[] body, string sender, CancellationToken ct = default)
    {
        if (throttle.Blocked(sender, Now())) return StatusCodes.Status429TooManyRequests;
        var secret = await settings.GetSecretAsync(SettingKeys.GitHubWebhookSecret, ct);
        if (!WebhookSignature.Verify(secret, body, signature))
        {
            throttle.Failed(sender, Now());
            return StatusCodes.Status401Unauthorized;
        }
        if (eventName == "ping") return StatusCodes.Status200OK;
        if (!WebhookEvents.Deploys(eventName) || WebhookEvents.RepoOf(body) is not { } about) return StatusCodes.Status204NoContent;
        var (owner, repo) = about;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployed = await db.Apps.AsNoTracking().Where(a => a.Source == AppSource.Git).Select(a => new { a.Owner, a.Repo }).Distinct().ToListAsync(ct);
        var match = deployed.FirstOrDefault(a => string.Equals(a.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (match is null) return StatusCodes.Status204NoContent;
        logger.LogInformation("GitHub says {Repo} changed ({Event}): checking it now", $"{match.Owner}/{match.Repo}", eventName);
        watch.CheckNowFromWebhook(match.Owner, match.Repo);
        return StatusCodes.Status202Accepted;
    }

    /// <summary>The endpoint itself: reads at most <see cref="MaxBody"/> and answers with a status code and no body.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        // A bare 401 or 429, not Gitberth's "not found" page: GitHub only needs the code.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        var request = context.Request;
        if (request.ContentLength > MaxBody)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBody)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
        }
        var sender = request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } ip ? ip : context.Connection.RemoteIpAddress?.ToString() ?? "";
        context.Response.StatusCode = await HandleAsync(request.Headers["X-GitHub-Event"].FirstOrDefault(), request.Headers["X-Hub-Signature-256"].FirstOrDefault(),
            buffer.ToArray(), sender, context.RequestAborted);
    }
}
