namespace Gitberth.Help;

/// <summary>
/// The Help's own addresses, signed in only like the rest of the UI: the docs' pictures (/help-media/&lt;path under
/// docs/&gt;) and the tips' catalog (/help-tips.json). Both are fixed for a build, and their addresses carry its version
/// (?v=), so browsers keep them for a year.
/// </summary>
public static class HelpEndpoints
{
    public const string TipsPath = "/help-tips.json";

    private const string KeepLong = "private, max-age=31536000, immutable";

    /// <summary>The catalog's address as tips.js fetches it, relative to the base like Gitberth's other links.</summary>
    public static string TipsUrl(TipCatalog tips) => TipsPath[1..] + "?v=" + Uri.EscapeDataString(tips.Version);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/help-media/{**path}", (string path, HelpDocs docs, HttpContext context) =>
        {
            if (docs.Media(path) is not { } media) return Results.NotFound();
            context.Response.Headers.CacheControl = KeepLong;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            // A picture and nothing more: an SVG opened on its own runs no script and loads nothing.
            if (media.ContentType == "image/svg+xml")
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.Bytes(media.Bytes, media.ContentType);
        }).RequireAuthorization();

        app.MapGet(TipsPath, (TipCatalog tips, HelpDocs docs, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = KeepLong;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Text(tips.CachedJson(docs), "application/json; charset=utf-8");
        }).RequireAuthorization();
    }
}
