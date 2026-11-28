using Gitberth.Agents;
using Gitberth.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Gitberth.Sources;

/// <summary>
/// The two ways an upload comes in (B6), both streamed to disk, never held in memory:
/// <list type="bullet">
/// <item>POST /uploads, from Gitberth's own pages: signed in, multipart/form-data with the page's antiforgery token
/// (__RequestVerificationToken) first, checked before a byte of a file is read, then <c>kind</c> (archive or folder), then
/// the file (one archive) or the folder's files (each named by its path in the folder). With ?app=&lt;key&gt; it's a new
/// version of that uploaded app, deployed at once ({deployment_id}); without, it's staged for New app ({id, name, …}). A
/// refusal answers one plain sentence.</item>
/// <item>POST /api/v1/apps/{name}/upload, for scripts: an API token (<c>curl -F file=@site.zip</c>). An app's new version
/// needs Operate; an upload for a name no app has makes that app, which needs Admin, as create_app does.</item>
/// </list>
/// </summary>
public static class UploadEndpoints
{
    public const string BrowserPath = "/uploads";

    public static void Map(WebApplication app)
    {
        // Signed in only, checked here: a page's upload is told 401, not sent to the sign-in page.
        app.MapPost(BrowserPath, BrowserAsync);
        app.MapPost(AgentPaths.ApiV1 + "/apps/{name}/upload", ApiAsync).RequireAuthorization(ApiTokenHandler.Policy).DisableAntiforgery();
    }

    /// <summary>The page's curl line for an app's new version, with a placeholder for the token (never a real one).</summary>
    public static string CurlLine(string root, string key, string? fileName = null) =>
        $"curl -X POST -H \"Authorization: Bearer $GITBERTH_TOKEN\" -F file=@{CurlFile(fileName)} {root.TrimEnd('/')}{AgentPaths.ApiV1}/apps/{Uri.EscapeDataString(key)}/upload";

    private static string CurlFile(string? name) =>
        name is { Length: > 0 } n && UploadArchives.KindOf(n) is not null && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? n : "site.zip";

    // ---- The browser's ----

    private static async Task BrowserAsync(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> antiforgeryOptions, UploadStore store,
        UploadApps uploads, Apps.AppCatalog catalog, ILoggerFactory loggers)
    {
        var ct = context.RequestAborted;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await PlainAsync(context, StatusCodes.Status401Unauthorized, "You're signed out. Sign in again, then upload it.");
            return;
        }
        // The limit is Gitberth's own (UploadLimits.MaxUpload, counted as it's written), not the server's default.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        if (Boundary(context.Request) is not { } boundary)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The upload has to come as a form upload.");
            return;
        }
        var options = antiforgeryOptions.Value;
        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = 16 * 1024 };

        // The page's token first: checked before anything of a file is read (put where the antiforgery check reads it).
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

        // A new version: the app is found before any file is read.
        int? appId = null;
        var appKey = context.Request.Query["app"].ToString();
        if (appKey.Length > 0)
        {
            var row = (await catalog.GetAsync(appKey, "", ct))?.Row;
            if (row is not { FromUpload: true, IsPreview: false })
            {
                await PlainAsync(context, StatusCodes.Status404NotFound, row is null ? $"There's no app called {appKey}." : $"{row.Name} isn't an uploaded app, so it takes no upload.");
                return;
            }
            appId = row.Id;
        }

        section = await reader.ReadNextSectionAsync(ct);
        var kind = section is not null && FieldName(section) == "kind" ? await ReadSmallAsync(section.Body, 64, ct) : null;
        if (kind is not ("archive" or "folder"))
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "The upload didn't say whether it's an archive or a folder. Reload the page and try again.");
            return;
        }
        var who = Audit.AuditActor.User(context.User.Identity?.Name ?? "admin", Audit.AuditCircuitHandler.ClientIp(context));
        using var scope = Audit.AuditScope.Begin(who);
        try
        {
            StagedUpload staged;
            if (kind == "archive")
            {
                section = await reader.ReadNextSectionAsync(ct);
                if (section is null || FieldName(section) != "file" || FileName(section) is not { } fileName)
                {
                    await PlainAsync(context, StatusCodes.Status400BadRequest, "No file came with the upload.");
                    return;
                }
                staged = await store.ReceiveArchiveAsync(fileName, section.Body, ct);
            }
            else
            {
                await using var folder = store.BeginFolder();
                while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
                {
                    if (FieldName(section) != "file" || FileName(section) is not { } path) continue;
                    await folder.AddAsync(path, section.Body, ct);
                }
                staged = await folder.FinishAsync(ct);
            }
            if (appId is int id)
            {
                var deployed = await uploads.NewVersionAsync(id, staged, ct);
                await Results.Json(new { app = appKey, deployment_id = deployed.DeploymentId, name = deployed.Name, sha = deployed.Sha, size = deployed.Size }).ExecuteAsync(context);
                return;
            }
            await Results.Json(new { id = staged.Id, name = staged.Name, size = staged.Size, files = staged.Files, folder = staged.Folder }).ExecuteAsync(context);
        }
        catch (UploadRefusedException ex)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await PlainAsync(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Gitberth.Uploads").LogWarning(ex, "An upload couldn't be written");
            await PlainAsync(context, StatusCodes.Status507InsufficientStorage, "The upload couldn't be written here: " + ex.Message.TrimEnd('.') + ".");
        }
    }

    // ---- The API's ----

    private static async Task ApiAsync(HttpContext context, string name, AgentContext agents, UploadStore store, UploadApps uploads, Audit.AuditLog audit,
        ToolDenials denials, ILoggerFactory loggers)
    {
        var ct = context.RequestAborted;
        var caller = ApiCaller.From(context.User);
        if (caller is null)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Send a token: Authorization: Bearer gbt_….");
            return;
        }
        if (!caller.May(ApiAccess.Operate))
        {
            await RefuseAsync(context, caller, ApiAccess.Operate, audit, denials);
            return;
        }
        // Which app, before any of the file is read: one that's there takes a new version; a name no app has makes one.
        Apps.AppView? view = null;
        try
        {
            view = await agents.AppAsync(name, ct);
        }
        catch (ToolError e) when (e.Status == StatusCodes.Status404NotFound)
        {
        }
        catch (ToolError e)
        {
            await AgentPaths.WriteErrorAsync(context, e.Status, e.Message);
            return;
        }
        if (view is not null && view.Row is not { FromUpload: true, IsPreview: false })
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status409Conflict,
                $"{view.Summary.Title} isn't an uploaded app, so it takes no upload. Upload to a name no app has to make a new one.");
            return;
        }
        if (view is null)
        {
            if (!caller.May(ApiAccess.Admin))
            {
                await RefuseAsync(context, caller, ApiAccess.Admin, audit, denials, making: name);
                return;
            }
            if (Services.AppService.NameProblem(name) is { } problem)
            {
                await AgentPaths.WriteErrorAsync(context, StatusCodes.Status400BadRequest, problem);
                return;
            }
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        if (Boundary(context.Request) is not { } boundary)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Send the archive as a form upload: curl -F file=@site.zip ….");
            return;
        }
        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = 16 * 1024 };
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null && FieldName(section) != "file")
            await section.Body.CopyToAsync(Stream.Null, ct);
        if (section is null || FileName(section) is not { } fileName)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "No file came with the upload: send it as file (curl -F file=@site.zip …).");
            return;
        }
        var wait = context.Request.Query["wait"].ToString() is "true" or "1";
        try
        {
            var staged = await store.ReceiveArchiveAsync(fileName, section.Body, ct);
            object result;
            if (view?.Row is { } row)
            {
                var deployed = await uploads.NewVersionAsync(row.Id, staged, ct);
                result = wait
                    ? new { app = view.Summary.Title, key = view.Summary.Key, created = false, upload = new { name = deployed.Name, sha = deployed.Sha, size = deployed.Size }, deploy = await agents.WaitForDeployAsync(deployed.DeploymentId, ct) }
                    : new
                    {
                        app = view.Summary.Title, key = view.Summary.Key, created = false, deployment_id = deployed.DeploymentId,
                        upload = new { name = deployed.Name, sha = deployed.Sha, size = deployed.Size },
                        message = $"Uploaded {deployed.Name} to {view.Summary.Title}: deploy #{deployed.DeploymentId} is queued (get_deployment follows it).",
                    };
            }
            else
            {
                try
                {
                    var made = await uploads.CreateDetectedAsync(staged, name.Trim(), ct);
                    result = wait
                        ? new { app = name.Trim(), key = made.Key, id = made.AppId, created = true, upload = new { name = staged.Name, sha = staged.Sha, size = staged.Size }, deploy = await agents.WaitForDeployAsync(made.DeploymentId, ct) }
                        : new { app = name.Trim(), key = made.Key, id = made.AppId, created = true, deployment_id = made.DeploymentId, upload = new { name = staged.Name, sha = staged.Sha, size = staged.Size }, message = made.Message };
                }
                finally
                {
                    // Made or not, the staged copy goes (a made app keeps its own).
                    store.Discard(staged.Id);
                }
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(ToolJson.Write(result), ct);
        }
        catch (UploadRefusedException ex)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Gitberth.Uploads").LogWarning(ex, "An upload couldn't be written");
            await AgentPaths.WriteErrorAsync(context, StatusCodes.Status507InsufficientStorage, "The upload couldn't be written here: " + ex.Message.TrimEnd('.') + ".");
        }
    }

    // As a tool's refusal: the token's access and what's needed, recorded at most once a minute per token.
    private static async Task RefuseAsync(HttpContext context, ApiCaller caller, ApiAccess needed, Audit.AuditLog audit, ToolDenials denials, string? making = null)
    {
        var message = $"The token {caller.Name} has {caller.Access} access, and {(making is null ? "an upload" : $"an upload that makes a new app ({making})")} needs {needed}. " +
            $"A person can make a token with {needed} access in Gitberth's Settings › AI agents and API.";
        if (denials.ShouldRecord(caller.TokenId, "upload") is int unrecorded)
            audit.Record(new Audit.AuditEntry("auth.token.denied", $"Refused an upload to the token {caller.Name}: it has {caller.Access} access, and it needs {needed}."
                + (unrecorded > 0 ? $" It was refused {unrecorded} more time{(unrecorded == 1 ? "" : "s")} since the last entry about it, not recorded one by one." : ""))
            {
                Actor = caller.Actor, Outcome = Audit.AuditOutcome.Denied, Target = "upload",
            });
        await AgentPaths.WriteErrorAsync(context, StatusCodes.Status403Forbidden, message);
    }

    // ---- Multipart ----

    private static string? Boundary(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type) && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            && HeaderUtilities.RemoveQuotes(type.Boundary).Value is { Length: > 0 } boundary
            ? boundary : null;

    private static string? FieldName(MultipartSection section) =>
        ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ? HeaderUtilities.RemoveQuotes(disposition.Name).Value : null;

    // A file part's name: its filename* (UTF-8) when it has one, else its filename; a folder's file carries its path in it.
    private static string? FileName(MultipartSection section)
    {
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) return null;
        var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value is { Length: > 0 } star ? star : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

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
