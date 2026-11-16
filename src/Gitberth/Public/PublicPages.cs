using System.Collections.Concurrent;
using System.Text;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Uptime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Public;

/// <summary>
/// Gitberth's public pages, the only thing it ever answers a marker host (<see cref="MarkerHosts"/>) with: a domain's down
/// page and the status page. <see cref="Middleware"/> is the very first in the pipeline, so a request for a marker host
/// never reaches the sign-in page, Blazor, the hooks, static files or any other page of Gitberth's, whatever its path or
/// method. cloudflared sets that Host from the tunnel rule, so a visitor on the internet can't choose it; someone on the
/// LAN who sends one gets the same public pages and nothing more. The status page is also served on the LAN at /status
/// and /status.json, without signing in, while it's on.
/// </summary>
public sealed class PublicPages(IDbContextFactory<GitberthDb> dbFactory, DownStates downStates, DomainService domains, UptimeSettings uptimeSettings,
    StatusPage status, ILogger<PublicPages> logger, TimeProvider? time = null)
{
    /// <summary>The header every public page carries: "down" or "status". The uptime checks know the down page by it.</summary>
    public const string PageHeader = "X-Gitberth-Page";

    // What each domain's down page says, kept a few seconds so a busy down page doesn't read the database for every visitor.
    private readonly ConcurrentDictionary<int, (DateTime At, DownView? View)> _views = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long what a down page says is kept before it's read again.</summary>
    internal TimeSpan KeepView { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The first middleware: a request whose Host is a marker host is answered here, and the pipeline ends. Every other
    /// request goes on as before.
    /// </summary>
    public static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (!MarkerHosts.IsMarker(context.Request.Host.Value))
        {
            await next(context);
            return;
        }
        var pages = context.RequestServices.GetRequiredService<PublicPages>();
        try
        {
            await pages.AnswerAsync(context);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            pages.Failed(ex);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                Bare(context, StatusCodes.Status503ServiceUnavailable);
            }
        }
    }

    private void Failed(Exception ex) => logger.LogWarning(ex, "A public page failed");

    internal async Task AnswerAsync(HttpContext context)
    {
        switch (MarkerHosts.Parse(context.Request.Host.Value))
        {
            case { Kind: MarkerKind.Down, DomainId: int id }:
                await DownAsync(context, id);
                break;
            case { Kind: MarkerKind.Status }:
                await StatusMarkerAsync(context);
                break;
            default:
                Bare(context, StatusCodes.Status404NotFound);
                break;
        }
    }

    // ---- The down page ----

    private async Task DownAsync(HttpContext context, int domainId)
    {
        var view = await DownViewAsync(domainId, context.RequestAborted);
        if (view is null)
        {
            Bare(context, StatusCodes.Status404NotFound);
            return;
        }
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            Bare(context, StatusCodes.Status404NotFound);
            return;
        }
        var response = context.Response;
        Secure(response, "down");
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers.RetryAfter = view.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (path.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase))
            await WriteAsync(context, "text/plain; charset=utf-8", "User-agent: *\nDisallow: /\n");
        else if (WantsJson(request))
            await WriteAsync(context, "application/json; charset=utf-8", DownPage.Json(view));
        else
            await WriteAsync(context, "text/html; charset=utf-8", DownPage.Html(view));
    }

    /// <summary>What the domain's down page says now; null when there's no such domain.</summary>
    internal async Task<DownView?> DownViewAsync(int domainId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_views.TryGetValue(domainId, out var kept) && now - kept.At < KeepView) return kept.View;
        DownView? view = null;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var domain = await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == domainId, ct);
            var app = domain is null ? null : await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == domain.AppId, ct);
            if (domain is not null && app is not null) view = await ViewOfAsync(app, domain, ct);
        }
        if (_views.Count > 1000) _views.Clear();
        _views[domainId] = (now, view);
        return view;
    }

    /// <summary>What a domain's down page says: its host, why, the app's message, and the status page when it lists the app.</summary>
    internal async Task<DownView> ViewOfAsync(AppDefinition app, CustomDomain domain, CancellationToken ct)
    {
        var reason = downStates.For(app.Id) ?? (domains.HeldHosts.Contains(domain.Hostname) ? DownReason.Held : null);
        if (domain.Protected) return new DownView(domain.Hostname, reason, null, null, Minimal: true);
        var settings = await uptimeSettings.StatusAsync(ct);
        var statusUrl = settings.Enabled && settings.Host is { } host && settings.Lists(app) ? $"https://{host}/" : null;
        return new DownView(domain.Hostname, reason, app.DownPageMessage, statusUrl, Minimal: false);
    }

    /// <summary>A preview of a domain's down page for its owner (signed in, on the LAN): exactly what visitors see, answered 200.</summary>
    public async Task PreviewAsync(HttpContext context, string? hostname)
    {
        DisableStatusPages(context);
        await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
        var domain = hostname is null ? null : await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Hostname == hostname.ToLower(), context.RequestAborted);
        var app = domain is null ? null : await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == domain.AppId, context.RequestAborted);
        if (domain is null || app is null)
        {
            Bare(context, StatusCodes.Status404NotFound);
            return;
        }
        var view = await ViewOfAsync(app, domain, context.RequestAborted);
        // As it would read while the app is stopped, unless it's down for another reason now.
        if (view.Reason is null) view = view with { Reason = DownReason.NotRunning };
        Secure(context.Response, "down-preview");
        await WriteAsync(context, "text/html; charset=utf-8", DownPage.Html(view));
    }

    // ---- The status page ----

    private async Task StatusMarkerAsync(HttpContext context)
    {
        var path = (context.Request.Path.Value ?? "/").TrimEnd('/');
        if (path.Length == 0 || path.Equals("/status", StringComparison.OrdinalIgnoreCase)) await StatusAsync(context, json: false);
        else if (path.Equals("/status.json", StringComparison.OrdinalIgnoreCase)) await StatusAsync(context, json: true);
        else if (path.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase) && IsRead(context.Request))
        {
            Secure(context.Response, "status");
            await WriteAsync(context, "text/plain; charset=utf-8", "User-agent: *\nDisallow: /\n");
        }
        else Bare(context, StatusCodes.Status404NotFound);
    }

    /// <summary>The status page, as HTML or JSON; a bare 404 while it's off. Only GET and HEAD.</summary>
    public async Task StatusAsync(HttpContext context, bool json)
    {
        DisableStatusPages(context);
        if (!IsRead(context.Request))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            Bare(context, StatusCodes.Status405MethodNotAllowed);
            return;
        }
        if (await status.BuildAsync(context.RequestAborted) is not { } model)
        {
            Bare(context, StatusCodes.Status404NotFound);
            return;
        }
        Secure(context.Response, "status");
        if (json) await WriteAsync(context, "application/json; charset=utf-8", StatusPage.Json(model));
        else await WriteAsync(context, "text/html; charset=utf-8", StatusPage.Html(model));
    }

    // ---- Answers ----

    private static bool IsRead(HttpRequest request) => HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

    /// <summary>A client that asks for JSON and not for a page: an API client, curl -H "Accept: application/json".</summary>
    internal static bool WantsJson(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        return accept.Contains("json", StringComparison.OrdinalIgnoreCase) && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    // Nothing is cached, indexed, framed or loaded from anywhere; the page's own inline styles are all it has.
    private static void Secure(HttpResponse response, string page)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers[PageHeader] = page;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static void Bare(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = 0;
    }

    private static async Task WriteAsync(HttpContext context, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    // Gitberth's own pages turn an empty 404 into its "not found" page: never for these.
    private static void DisableStatusPages(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
    }
}
