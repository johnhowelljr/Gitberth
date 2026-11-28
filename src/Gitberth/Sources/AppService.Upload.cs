using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Sources;

namespace Gitberth.Services;

// An uploaded folder's app (B6): made as a Git app is (CreateAsync), with the upload as its source instead of a repo.
public sealed partial class AppService
{
    /// <summary>
    /// Adds an app made from an uploaded folder or archive, with its settings and ports, under the network name
    /// <see cref="ChooseSlugAsync"/> gives it. Its source is the upload (Source Upload, no repo or branch); the caller keeps
    /// the archive (<see cref="UploadStore.Keep"/>) and deploys it.
    /// </summary>
    /// <param name="by">Who uploaded it, as the audit log names them.</param>
    public async Task<int> CreateUploadAsync(StagedUpload upload, AppSettingsModel m, DetectionResult? detection, string? by, CancellationToken ct = default)
    {
        var slug = (await ChooseSlugAsync(m.Name, ct)).Slug;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = new AppDefinition
        {
            Slug = slug,
            Source = AppSource.Upload,
            // A new deployed app goes in the menu, as a Git app does.
            Pinned = true,
            DetectedKind = detection?.Kind.ToString(),
            DetectedSummary = detection?.Title,
            DetectedPort = detection?.ContainerPort,
            UploadName = upload.Name,
            UploadSha = upload.Sha,
            UploadSize = upload.Size,
            UploadedAt = upload.At,
            UploadedBy = by,
            // Nothing watches an upload: it's deployed when a new one comes.
            AutoDeploy = false,
            // Vercel's variables describe a repo's deploy (its commit, branch, owner): an upload has none of them.
            VercelVariables = false,
        };
        // An upload has no branch: the row keeps the default one, which nothing of an upload's reads (the settings' checks
        // ask every deployed app for one).
        if (string.IsNullOrWhiteSpace(m.Branch)) m.Branch = "main";
        Apply(app, m);
        (app.AutoDeploy, app.VercelVariables) = (false, false);
        if (m.ComposeFile is { Length: > 0 } composeFile && m.WebService is { Length: > 0 } web)
        {
            app.ComposeFile = composeFile.Trim().Trim('/');
            app.WebService = web;
            app.Container = Compose.ComposeNames.Member(slug, web);
        }
        else if (m.Limits is { } limits) GitAppSettings.Apply(app, limits, GitAppSettings.Resources, protector);
        app.Ports.AddRange(PortRules.ToMappings(m.Ports));
        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);
        hostPorts?.ForgetSaved();
        await health.SetGatheredAsync(app.ContainerName, m.GatherStats, ct);
        await restarts.SetAsync(app.ContainerName, m.RestartInterval, ct);
        if (notify is not null) await notify.SetMutedAsync(app.ContainerName, false, ct);
        if (audit is not null)
        {
            var changes = new List<Audit.AuditChange>
            {
                new("Source", null, $"upload of {upload.Name}"),
                new("Upload", null, $"{UploadArchives.Short(upload.Sha)} ({UploadArchives.Size(upload.Size)})"),
                new("Ports", null, SettingsAudit.Ports(app.Ports)),
            };
            if (app.Subdirectory is { } folder) changes.Add(new("Subfolder", null, folder));
            if (app.ComposeFile is { } file)
            {
                changes.Add(new("Compose file", null, file));
                changes.Add(new("Web service", null, app.WebService));
            }
            else if (detection is not null) changes.Add(new("Detected", null, detection.Title));
            if (app.EnvVars.Count > 0) changes.Add(new("Variables", null, string.Join(", ", app.EnvVars.Select(v => v.Key).Order(StringComparer.Ordinal))));
            audit.Record(new Audit.AuditEntry("app.created", app.ComposeFile is { } compose
                ? $"Created {app.Name} from an upload of {upload.Name}, deployed with Compose from {compose}."
                : $"Created {app.Name} from an upload of {upload.Name}.")
            {
                AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
            });
        }
        return app.Id;
    }
}
