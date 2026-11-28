using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Gitberth.Workbench;

/// <summary>What a download's or an upload's ticket holds: the app, the mount, and the path (a file or folder to download, the folder to upload into).</summary>
public sealed record FileTicket(FilesOf Of, string Path, bool Folder);

/// <summary>
/// The Files tab's downloads and uploads (B3), which go straight between the browser and Gitberth rather than through the
/// page's connection: GET /files/download?ticket=… (a file, or a folder as a .tar.gz) and POST /files/upload?ticket=…
/// (multipart, any number of files, each streamed into the folder). Signed in, with a one-time ticket the page got a
/// moment before for exactly that file or folder; anything else is refused before anything is read.
/// </summary>
public static class FileEndpoints
{
    public const string DownloadPath = "/files/download";
    public const string UploadPath = "/files/upload";

    public static void Map(WebApplication app)
    {
        app.MapGet(DownloadPath, DownloadAsync);
        // The ticket is the form token here: one only the page's server side could give, for this one upload.
        app.MapPost(UploadPath, UploadAsync).DisableAntiforgery();
    }

    private static async Task DownloadAsync(HttpContext context, WorkbenchTickets tickets, AppFiles files)
    {
        if (await TicketAsync(context, tickets, TicketKind.Download) is not { } ticket) return;
        var details = (FileTicket)ticket.Details;
        using var actor = Audit.AuditScope.Begin(Audit.AuditActor.User(ticket.User, Audit.AuditCircuitHandler.ClientIp(context)));
        var name = DownloadName(details);
        context.Response.ContentType = details.Folder ? "application/gzip" : "application/octet-stream";
        context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = name, FileName = Ascii(name) }.ToString();
        context.Response.Headers.CacheControl = "no-store";
        var problem = await files.DownloadAsync(details.Of, details.Path, details.Folder, context.Response.Body, context.RequestAborted);
        if (problem is null) return;
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.Headers.ContentDisposition = default;
        await PlainAsync(context, StatusCodes.Status404NotFound, problem);
    }

    /// <summary>The download's file name: the file's own, or the folder's (the volume's, at its root) with .tar.gz.</summary>
    internal static string DownloadName(FileTicket t)
    {
        var last = t.Path.Length > 0 ? AppFiles.Split(t.Path).Name : t.Of.Mount.Volume ?? t.Of.Mount.Target.TrimEnd('/').Split('/')[^1];
        if (last.Length == 0) last = "files";
        return t.Folder ? last + ".tar.gz" : last;
    }

    private static string Ascii(string name)
    {
        var s = new string(name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        return s.Length == 0 ? "download" : s;
    }

    private static async Task UploadAsync(HttpContext context, WorkbenchTickets tickets, AppFiles files)
    {
        if (await TicketAsync(context, tickets, TicketKind.Upload) is not { } ticket) return;
        var details = (FileTicket)ticket.Details;
        using var actor = Audit.AuditScope.Begin(Audit.AuditActor.User(ticket.User, Audit.AuditCircuitHandler.ClientIp(context)));
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        var type = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ? media : null;
        var boundary = HeaderUtilities.RemoveQuotes(type?.Boundary ?? default).Value;
        if (type is null || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(boundary))
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The files have to come as a form upload.");
            return;
        }
        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = 16 * 1024 };
        var done = new List<string>();
        var refused = new List<string>();
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(context.RequestAborted)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition()) continue;
            var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "";
            var outcome = await files.WriteAsync(details.Of, details.Path, name, section.Body, ct: context.RequestAborted);
            (outcome.Ok ? done : refused).Add(outcome.Ok ? outcome.Message : $"{name}: {outcome.Message}");
            // What's left of a refused file is read past, so the next one can come.
            if (!outcome.Ok) await section.Body.CopyToAsync(Stream.Null, context.RequestAborted);
        }
        await Results.Json(new { uploaded = done, refused }).ExecuteAsync(context);
    }

    // The request's ticket, after the sign-in: null (and the refusal written) when there's none for this person and this.
    private static async Task<WorkbenchTicket?> TicketAsync(HttpContext context, WorkbenchTickets tickets, TicketKind kind)
    {
        if (context.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            await PlainAsync(context, StatusCodes.Status401Unauthorized, "You're signed out. Sign in again, then try once more.");
            return null;
        }
        if (tickets.Take(context.Request.Query["ticket"], kind, user) is { } ticket) return ticket;
        await PlainAsync(context, StatusCodes.Status403Forbidden, "That link was for one use, within 30 seconds: go back to the Files tab and try again.");
        return null;
    }

    private static async Task PlainAsync(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text, Encoding.UTF8);
    }
}
