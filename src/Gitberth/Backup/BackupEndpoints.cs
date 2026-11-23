using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Gitberth.Backup;

/// <summary>
/// The two endpoints a backup file goes through, both for signed-in people only:
/// <list type="bullet">
/// <item>GET /backup/download/{ticket}: the save, made while it's sent (no-store, as an attachment).</item>
/// <item>POST /backup/upload: multipart/form-data, the page's antiforgery token (__RequestVerificationToken) first, then the
/// file (file). The token is checked before a byte of the file is read, as /logout checks its own; the file is streamed to
/// /data/restore/upload.gbak, never held, its size not limited. A refusal answers one plain sentence.</item>
/// </list>
/// </summary>
public static class BackupEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/backup/download/{id}", DownloadAsync).RequireAuthorization();
        // Signed in only, checked here: a page's upload is told 401, not sent to the sign-in page.
        app.MapPost("/backup/upload", UploadAsync);
    }

    private static async Task DownloadAsync(HttpContext context, BackupService backup, string id)
    {
        if (backup.Saving)
        {
            await PlainAsync(context, StatusCodes.Status409Conflict, "Another save is being made. Wait for it to end.");
            return;
        }
        if (backup.Claim(id) is not { } job)
        {
            await PlainAsync(context, StatusCodes.Status404NotFound, "This save has expired or was downloaded already. Save again.");
            return;
        }
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = job.FileName, FileName = job.FileName }.ToString();
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        // The save is the signed-in person's, in the audit log.
        using var actor = Audit.AuditScope.Begin(Audit.AuditActor.User(context.User.Identity?.Name ?? "admin", context.Connection.RemoteIpAddress?.ToString()));
        try
        {
            await backup.WriteAsync(job, context.Response.Body, context.RequestAborted);
        }
        catch (Exception) when (context.Response.HasStarted)
        {
            // The file is cut off: the browser is told the download failed, rather than given a file that looks whole.
            context.Abort();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Response.Headers.Remove(HeaderNames.ContentDisposition);
            await PlainAsync(context, StatusCodes.Status409Conflict, ex is InvalidOperationException or BackupSaveException ? ex.Message : "The save failed.");
        }
    }

    private static async Task UploadAsync(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> antiforgeryOptions, RestoreService restore)
    {
        var ct = context.RequestAborted;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await PlainAsync(context, StatusCodes.Status401Unauthorized, "You're signed out. Sign in again, then upload the file.");
            return;
        }
        // A backup is as big as what it holds: no limit on this request's body alone.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The file has to come as a form upload.");
            return;
        }
        var options = antiforgeryOptions.Value;
        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = 16 * 1024 };

        // The page's token first: checked before anything of the file is read (put where the antiforgery check reads it).
        var section = await reader.ReadNextSectionAsync(ct);
        if (section is null || FieldName(section) != options.FormFieldName || options.HeaderName is null)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The page's form token didn't come first. Reload the page and try again.");
            return;
        }
        var token = await ReadSmallAsync(section.Body, 8 * 1024, ct);
        context.Request.Headers[options.HeaderName] = token;
        if (token is null || !await antiforgery.IsRequestValidAsync(context))
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The page's form token wasn't accepted. Reload the page and try again.");
            return;
        }

        section = await reader.ReadNextSectionAsync(ct);
        if (section is null || FieldName(section) != "file")
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "No file came with the upload.");
            return;
        }
        try
        {
            var bytes = await restore.ReceiveAsync(section.Body, ct);
            await Results.Json(new { bytes }).ExecuteAsync(context);
        }
        catch (InvalidOperationException ex)
        {
            await PlainAsync(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            await PlainAsync(context, StatusCodes.Status507InsufficientStorage, "The file couldn't be written here: " + ex.Message.TrimEnd('.') + ".");
        }
    }

    private static string? FieldName(MultipartSection section) =>
        ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ? HeaderUtilities.RemoveQuotes(disposition.Name).Value : null;

    // A form field's value, never more than max bytes of it.
    private static async Task<string?> ReadSmallAsync(Stream body, int max, CancellationToken ct)
    {
        var buffer = new byte[max + 1];
        var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return read > max ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
    }

    private static Task PlainAsync(HttpContext context, int status, string sentence)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(sentence);
    }
}
