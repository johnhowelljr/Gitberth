using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Images;
using Gitberth.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>What a deploy hook does when it's called: builds a Git app again, or updates an app that runs a registry's tag.</summary>
public enum HookKind { Git, Image }

/// <summary>A deploy hook as the app's Settings shows it.</summary>
/// <param name="Token">The secret part of its URL; null when it can't be read back (the key it was encrypted with is gone).</param>
public sealed record HookView(int Id, string Name, string? Token, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>A new hook, or why none was made.</summary>
/// <param name="AppId">The app's row, made for it when the container had none.</param>
public sealed record HookCreated(HookView? Hook, string? Problem, int? AppId = null);

/// <summary>
/// Deploy hooks, like Vercel's: secret URLs that deploy an app when something calls them (a CMS whose content changed, a
/// scheduled job, CI). The token is 32 random bytes in base64url; it's found by its SHA-256, and kept encrypted so its URL
/// can be shown again. A Git app (not a preview) can have them, and so can an app that runs a registry's tag: never one
/// that runs a local image or one pinned to a digest, which have nothing to pull, nor Gitberth itself.
/// </summary>
public sealed class DeployHooks(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector, AppCatalog catalog)
{
    /// <summary>The most hooks one app has.</summary>
    public const int MaxPerApp = 10;

    /// <summary>The longest name a hook has.</summary>
    public const int MaxName = 50;

    /// <summary>Where a hook's URL lives on Gitberth: /hooks/deploy/&lt;token&gt;.</summary>
    public const string PathPrefix = "/hooks/deploy/";

    // One create at a time: the count and the names are checked and the row added together.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A new token: 32 random bytes, base64url without padding (43 characters).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What's stored to find a token: its SHA-256, as lowercase hex.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Whether it can be a token at all (the tunnel's rule takes the same): 20 to 100 base64url characters.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: >= 20 and <= 100 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The hook's URL on Gitberth's own address (http://192.168.50.10:8080/).</summary>
    public static string LanUrl(string root, string token) => new Uri(new Uri(root.EndsWith('/') ? root : root + "/"), PathPrefix.TrimStart('/') + token).ToString();

    /// <summary>The hook's URL through the tunnel, on the host name GitHub's webhooks have (hooks.example.com).</summary>
    public static string PublicUrl(string host, string token) => $"https://{host}{PathPrefix}{token}";

    /// <summary>What a hook does on this app, or null when it can't have hooks.</summary>
    public static HookKind? KindOf(AppSummary app, AppDefinition? row) =>
        app.Source == AppSource.Git ? (row is { IsPreview: true } ? null : HookKind.Git)
        // Gitberth itself would be recreated by its own hook, cutting the call short.
        : app.Role == ContainerRole.Self ? null
        : app.Origin == ImageOrigin.Registry ? HookKind.Image
        : null;

    /// <summary>Why a name can't be a hook's, or null.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give it a name, like Contentful or Nightly."
        : name.Trim().Length > MaxName ? $"Keep it to {MaxName} characters."
        : name.Any(char.IsControl) ? "Use letters, digits, spaces and punctuation."
        : null;

    public async Task<IReadOnlyList<HookView>> ListAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hooks = await db.DeployHooks.AsNoTracking().Where(h => h.AppId == appId).OrderBy(h => h.Id).ToListAsync(ct);
        return hooks.Select(h => View(h, protector.TryUnprotect(h.ProtectedToken))).ToList();
    }

    /// <summary>
    /// A new hook for the app with this key (its container's name). A container without a row gets one first, as any edit
    /// adopts it (<see cref="AppCatalog.AdoptAsync"/>).
    /// </summary>
    public async Task<HookCreated> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        if (NameProblem(name) is { } problem) return new HookCreated(null, problem);
        name = name.Trim();
        // Asked of the app as its page shows it: where its image comes from decides whether a hook can do anything.
        if (await catalog.GetAsync(key, "", ct) is not { } view) return new HookCreated(null, $"There's no app called {key}.");
        if (KindOf(view.Summary, view.Row) is null)
            return new HookCreated(null, $"{view.Summary.Title} can't have deploy hooks: only Git apps and apps that run a registry's tag can.");
        await _gate.WaitAsync(ct);
        try
        {
            var row = view.Row ?? await catalog.AdoptAsync(key, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var names = await db.DeployHooks.AsNoTracking().Where(h => h.AppId == row.Id).Select(h => h.Name).ToListAsync(ct);
            if (names.Count >= MaxPerApp) return new HookCreated(null, $"An app has at most {MaxPerApp} deploy hooks. Delete one first.");
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) return new HookCreated(null, $"It has a deploy hook called {name} already.");
            var token = NewToken();
            var hook = new DeployHook { AppId = row.Id, Name = name, TokenHash = Hash(token), ProtectedToken = protector.Protect(token) };
            db.DeployHooks.Add(hook);
            await db.SaveChangesAsync(ct);
            return new HookCreated(View(hook, token), null, row.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the app's hook: its URL answers 404 from then on. False when it had none by that ID.</summary>
    public async Task<bool> DeleteAsync(int appId, int hookId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeployHooks.Where(h => h.Id == hookId && h.AppId == appId).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>The hook with this token and its app; null when there's none.</summary>
    public async Task<(DeployHook Hook, AppDefinition App)?> FindAsync(string? token, CancellationToken ct = default)
    {
        if (!LooksLikeToken(token)) return null;
        var hash = Hash(token!);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.DeployHooks.AsNoTracking().FirstOrDefaultAsync(h => h.TokenHash == hash, ct) is not { } hook) return null;
        return await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == hook.AppId, ct) is { } app ? (hook, app) : null;
    }

    /// <summary>Notes a call of the hook, for Last used.</summary>
    public async Task UsedAsync(int hookId, DateTime at, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.DeployHooks.Where(h => h.Id == hookId).ExecuteUpdateAsync(s => s.SetProperty(h => h.LastUsedAt, at), ct);
    }

    private static HookView View(DeployHook hook, string token) =>
        new(hook.Id, hook.Name, token.Length > 0 ? token : null, hook.CreatedAt, hook.LastUsedAt);
}

/// <summary>
/// POST /hooks/deploy/&lt;token&gt;: a deploy hook was called. A Git app's branch is built again from its newest commit
/// (201, with the deploy's number); an app that runs a registry's tag pulls it and is updated when the image moved, in the
/// background (202). A stopped app isn't started (409), and neither is one being deleted; an update or change already under
/// way for an image app is a 409 too. An unknown token gets a bare 404 and counts towards a throttle of its own, which
/// answers 429 once a sender sent 10 in 10 minutes; a good token always gets through. Anonymous and without antiforgery
/// (the token is the secret); the body means nothing and at most 64 KB of it is read. Any other method gets a bare 405, a
/// failure a bare 500: the webhook's host never shows Gitberth's pages. Each call with a good token writes one line on the
/// app's Activity.
/// </summary>
public sealed class DeployHookEndpoint(DeployHooks hooks, AppActions actions, IHookUpdates updates, ActivityLog activity, ILogger<DeployHookEndpoint> logger)
{
    public const string Route = "/hooks/deploy/{**token}";
    public const int MaxBody = 64 * 1024;

    /// <summary>Unknown tokens by sender, apart from GitHub's bad signatures: neither counts against the other.</summary>
    internal WebhookThrottle Throttle { get; } = new();

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>The answer to one call: a status code, and for one that started something, what it started.</summary>
    public sealed record Answer(int Status, JsonObject? Body = null);

    /// <summary>
    /// Maps the endpoint for every method and every path under /hooks/deploy, anonymous and without antiforgery, so it only
    /// ever answers a status code: never the "not found" page a 405 or a token with a slash would be turned into.
    /// </summary>
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder app) =>
        app.Map(Route, context => context.RequestServices.GetRequiredService<DeployHookEndpoint>().HandleAsync(context))
            .AllowAnonymous()
            .DisableAntiforgery();

    /// <summary>One call of a hook's URL, from <paramref name="sender"/>.</summary>
    public async Task<Answer> CallAsync(string? token, string sender, CancellationToken ct = default)
    {
        // Found first: a good token always works, and only unknown ones are ever throttled.
        if (await hooks.FindAsync(token, ct) is not { } found || found.App.IsPreview)
        {
            var now = Now();
            if (Throttle.Blocked(sender, now)) return new Answer(StatusCodes.Status429TooManyRequests);
            Throttle.Failed(sender, now);
            return new Answer(StatusCodes.Status404NotFound);
        }
        var (hook, app) = found;
        await hooks.UsedAsync(hook.Id, Now(), ct);
        var said = $"Deploy hook \"{hook.Name}\"";
        if (app.Deployed)
        {
            if (app.DesiredState != DesiredState.Running)
            {
                await NoteAsync(app, ActivityKind.Deploy, $"{said}: the app is stopped, so nothing was deployed", ct);
                return new Answer(StatusCodes.Status409Conflict);
            }
            // Null when it stopped, or began to be deleted, since it was read: nothing is said of an app on its way out.
            if (await actions.DeployFromHookAsync(app.Id, hook.Name, ct) is not int deployment) return new Answer(StatusCodes.Status409Conflict);
            logger.LogInformation("Deploy hook {Hook} started deploy {Deployment} of {App}", hook.Name, deployment, app.Name);
            await NoteAsync(app, ActivityKind.Deploy, $"{said} started deploy #{deployment}", ct);
            return new Answer(StatusCodes.Status201Created, new JsonObject { ["deployment"] = deployment, ["app"] = app.Name, ["status"] = "queued" });
        }
        // The update writes its own line once it's done: whether the image moved isn't known until the pull is.
        if (await updates.UpdateFromHookAsync(app.ContainerName, hook.Name, ct) is null)
        {
            await NoteAsync(app, ActivityKind.Updated, $"{said}: an update or change of it was under way, so nothing more was done", ct);
            return new Answer(StatusCodes.Status409Conflict);
        }
        logger.LogInformation("Deploy hook {Hook} is updating {App}", hook.Name, app.Name);
        return new Answer(StatusCodes.Status202Accepted, new JsonObject { ["app"] = app.Name, ["status"] = "updating" });
    }

    /// <summary>The endpoint itself: reads at most <see cref="MaxBody"/> of the body, and answers a status code (with JSON when something started).</summary>
    public async Task HandleAsync(HttpContext context)
    {
        // A bare status code, never Gitberth's "not found" or error page: whatever calls a hook only needs the code.
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
            // A CMS may send its whole entry: it's read up to 64 KB, so a small body leaves the connection usable, and no further.
            var chunk = new byte[16 * 1024];
            for (long read = 0; read < MaxBody;)
            {
                var n = await request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxBody - read)), context.RequestAborted);
                if (n == 0) break;
                read += n;
            }
            var token = request.RouteValues.TryGetValue("token", out var value) ? value as string
                : request.Path.StartsWithSegments(DeployHooks.PathPrefix.TrimEnd('/'), out var rest) ? rest.Value?.TrimStart('/') : null;
            var sender = request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } ip ? ip : context.Connection.RemoteIpAddress?.ToString() ?? "";
            var answer = await CallAsync(token, sender, context.RequestAborted);
            context.Response.StatusCode = answer.Status;
            if (answer.Body is { } body)
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
            }
        }
        catch (Exception ex)
        {
            // Not the error page either: the code alone, and the reason in Gitberth's log (unless the caller just hung up).
            if (!context.RequestAborted.IsCancellationRequested) logger.LogError(ex, "A deploy hook's call couldn't be handled");
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    // A line on the app's Activity; one that can't be written never changes the answer.
    private async Task NoteAsync(AppDefinition app, ActivityKind kind, string text, CancellationToken ct)
    {
        try
        {
            await activity.AddAsync(app.ContainerName, app.Id, kind, text, ActivityTone.Neutral, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't write what a deploy hook did to {App}", app.Name);
        }
    }
}
