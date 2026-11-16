namespace Gitberth.Templates;

/// <summary>The templates' icons for the gallery, signed in only: /templates/&lt;id&gt;/icon.</summary>
public static class TemplateEndpoints
{
    /// <summary>The address of a template's icon, as the gallery's img tags use it.</summary>
    public static string IconPath(string id) => $"templates/{Uri.EscapeDataString(id)}/icon";

    public static void Map(WebApplication app)
    {
        // A template that didn't pass its checks is left out of the gallery: said once, at start.
        foreach (var problem in TemplateCatalog.Bundled.LoadProblems)
            app.Logger.LogWarning("App template left out: {Problem}", problem);

        app.MapGet("/templates/{id}/icon", (string id, TemplateCatalog catalog, HttpContext context) =>
        {
            if (catalog.Icon(id) is not { } icon) return Results.NotFound();
            // A picture and nothing more: an SVG opened on its own runs no script and loads nothing.
            context.Response.Headers.CacheControl = "private, max-age=86400";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(icon.Bytes, icon.ContentType);
        }).RequireAuthorization();
    }
}
