using Gitberth.Data;
using Gitberth.Git;
using Gitberth.Sources;

namespace Gitberth.Deploy;

/// <summary>
/// An uploaded app's deploy (B6): its source is one of its kept uploads (the one asked for, else its newest), unpacked into
/// the deploy's work folder as safely as it was when it came, its root the one top-level folder it has. Its "commit" is the
/// archive's SHA-256, its message "Upload of site.zip" and its author who uploaded it; from there it's built and run as a
/// Git app's commit is, with Compose when it has a Compose file.
/// </summary>
public sealed partial class DeployPipeline
{
    private UploadStore? _ownUploads;

    private UploadStore Uploads => uploads ?? (_ownUploads ??= new UploadStore(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<UploadStore>.Instance));

    /// <summary>Where a deploy's source is, as its log and errors say it: on the app's branch, or in its upload.</summary>
    private static string Where(AppDefinition app) => app.FromUpload ? "in the upload" : $"on {app.Branch}";

    // The folder a Compose file is looked for in, when the app has no subfolder.
    private static string RootWords(AppDefinition app) => app.FromUpload ? "the upload's root" : "the repo's root";

    private async Task<(CommitInfo Commit, string SrcDir)> FetchUploadAsync(GitberthDb db, AppDefinition app, Deployment dep, string? asked, string workDir,
        DeployLog log, CancellationToken ct)
    {
        DeployPaths.TryDeleteDirectory(workDir);
        Directory.CreateDirectory(workDir);
        await SetStatusAsync(db, dep, DeployStatus.Cloning);
        var sha = asked is { Length: > 0 } ? asked.ToLowerInvariant() : app.UploadSha
            ?? throw new DeployException($"Nothing has been uploaded for {app.Name} yet. Upload its folder or archive on its page.");
        var archive = Uploads.ArchiveOf(app.Id, sha)
            ?? throw new DeployException(sha == app.UploadSha
                ? $"The upload {UploadArchives.Short(sha)} of {app.Name} isn't kept here any more. Upload it again on its page."
                : $"The upload {UploadArchives.Short(sha)} isn't kept any more (Gitberth keeps the newest {UploadStore.Kept}). Upload it again on the app's page.");
        var current = sha == app.UploadSha;
        var name = current ? app.UploadName ?? Path.GetFileName(archive) : dep.CommitMessage is { Length: > 0 } said && said.StartsWith("Upload of ", StringComparison.Ordinal)
            ? said["Upload of ".Length..] : Path.GetFileName(archive);
        var size = new FileInfo(archive).Length;
        log.Step($"Unpacking {name} ({UploadArchives.Short(sha)}, {UploadArchives.Size(size)})");
        var kind = UploadArchives.KindOfExtension(Path.GetExtension(archive)) ?? throw new DeployException($"{Path.GetFileName(archive)} isn't an archive Gitberth reads.");
        Unpacked unpacked;
        var into = Path.Combine(workDir, "upload");
        try
        {
            unpacked = await UploadArchives.UnpackAsync(archive, kind, into, ct);
        }
        catch (UploadRefusedException ex)
        {
            throw new DeployException(ex.Message);
        }
        // Its root becomes the source, as a clone's folder is.
        var srcDir = Path.Combine(workDir, "src");
        Directory.Move(unpacked.Root, srcDir);
        log.Line($"{unpacked.Files} file{(unpacked.Files == 1 ? "" : "s")}, {UploadArchives.Size(unpacked.Bytes)} unpacked"
            + (unpacked.Root != into ? $", from its folder {Path.GetFileName(unpacked.Root)}/" : "") + ".");
        var author = current ? app.UploadedBy ?? "someone" : dep.CommitAuthor ?? "someone";
        var at = current ? app.UploadedAt ?? DateTime.UtcNow : DateTime.UtcNow;
        var commit = new CommitInfo(sha, author, at, $"Upload of {name}");
        dep.CommitSha = sha;
        dep.CommitMessage = commit.Subject.Length > 500 ? commit.Subject[..500] : commit.Subject;
        dep.CommitAuthor = author;
        app.LastAttemptedCommit = sha;
        // Saved now, so a restart during the build still knows which upload was being built.
        await db.SaveChangesAsync(CancellationToken.None);
        log.Step($"Upload {UploadArchives.Short(sha)} by {author}: {commit.Subject}");
        await ReportAsync(started: true, dep);
        return (commit, srcDir);
    }
}
