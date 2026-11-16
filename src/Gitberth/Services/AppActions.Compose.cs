using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Deploy;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>
/// A Compose app's actions act on its whole project: Start, Stop and Restart are docker compose start, stop and restart;
/// Delete is docker compose down (its volumes and images only when asked); a deploy without a build runs a kept release
/// again; and a project with a container missing comes back with its current release.
/// </summary>
public sealed partial class AppActions
{
    private ComposeRunner? _ownComposer;

    private ComposeRunner Composer => composer ?? (_ownComposer ??= new ComposeRunner(docker, options,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposeRunner>.Instance));

    /// <summary>The key a Compose app's deploy has in <see cref="RollbackImagesAsync"/>: its release is kept, so it can run again.</summary>
    public static string ReleaseKey(int deploymentId) => "#" + deploymentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // A deploy without a build: the release a deploy ran (Roll back's, or the one that runs now), with the app's settings.
    private async Task<int> RecreateComposeAsync(GitberthDb db, AppDefinition app, DeployTrigger trigger, int? fromDeploymentId, CancellationToken ct)
    {
        var from = fromDeploymentId is int id
            ? await db.Deployments.FirstAsync(d => d.Id == id && d.AppId == app.Id, ct)
            : await db.Deployments.Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        // Nothing ran yet, or its files are gone: it's deployed again from its source.
        if (from is null || ComposeReleases.For(options, app.Slug).ResolvedOf(from.Id) is null)
        {
            await db.DisposeAsync();
            if (fromDeploymentId is not null) throw new InvalidOperationException($"The files of deploy #{fromDeploymentId} aren't kept any more, so it can't run again.");
            return await DeployAsync(app.Id, trigger, ct);
        }
        app.DesiredState = DesiredState.Running;
        var dep = new Deployment
        {
            AppId = app.Id, Trigger = trigger, ImageTag = from.ImageTag, Images = from.Images, CommitSha = from.CommitSha, CommitMessage = from.CommitMessage,
            CommitAuthor = from.CommitAuthor, RequestedBy = Audit.AuditActors.Format(Audit.AuditScope.Current),
        };
        db.Deployments.Add(dep);
        await db.SaveChangesAsync(ct);
        Queued(app, dep, fromDeploymentId);
        queue.Enqueue(new DeployRequest(app.Id, dep.Id, Build: false, Image: from.ImageTag, FromDeployment: from.Id));
        return dep.Id;
    }

    // A Compose app's deploys whose releases are kept, other than the one that runs: Roll back can run them again.
    private async Task<IReadOnlySet<string>> ComposeRollbacksAsync(AppDefinition app, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var succeeded = await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded)
            .OrderByDescending(d => d.Id).Select(d => d.Id).ToListAsync(ct);
        var releases = ComposeReleases.For(options, app.Slug);
        return succeeded.Skip(1).Where(id => releases.ResolvedOf(id) is not null).Select(ReleaseKey).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The file the app's current release runs (its newest successful deploy's), and that deploy; null when it isn't kept.</summary>
    internal async Task<(int Deployment, string File)?> CurrentReleaseAsync(AppDefinition app, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id)
            .Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        return id is int deployment && ComposeReleases.For(options, app.Slug).ResolvedOf(deployment) is { } file ? (deployment, file) : null;
    }

    /// <summary>
    /// Start: the project's containers start (docker compose start), unless saved settings aren't live yet or one of its
    /// services has no container, when its current release is brought up again (or, with none kept, deployed again).
    /// </summary>
    private async Task<string?> StartComposeAsync(AppDefinition app)
    {
        if (!app.ConfigChangePending && app.CurrentImage is not null)
        {
            using (await locks.AcquireAsync(app.Id))
            {
                if (await CompleteAsync(app))
                {
                    var r = await Composer.RunAsync(app.ComposeProject, null, null, ["start"]);
                    return r.Ok ? null : Last(r.ErrorText);
                }
            }
        }
        if (await CurrentReleaseAsync(app) is not null) await RecreateAsync(app.Id, DeployTrigger.Start);
        else await DeployAsync(app.Id, DeployTrigger.Start);
        return null;
    }

    /// <summary>Whether every service of the current release has its container.</summary>
    internal async Task<bool> CompleteAsync(AppDefinition app, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> images;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            images = DeployPipeline.ParseImages(await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded)
                .OrderByDescending(d => d.Id).Select(d => d.Images).FirstOrDefaultAsync(ct));
        if (images.Count == 0) return await docker.ContainerExistsAsync(app.ContainerName, ct);
        var (containers, error) = await docker.ListContainersAsync(managedOnly: true, ct);
        if (error is not null) return false;
        var services = containers.Where(c => c.Labels.GetValueOrDefault("gitberth.app") == app.Slug)
            .Select(c => c.Labels.GetValueOrDefault(ComposeNames.ServiceLabel)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return images.Keys.All(services.Contains);
    }

    // docker compose stop or restart, for the whole project, under the app's lock.
    private async Task<string?> ProjectAsync(AppDefinition app, string verb)
    {
        using (await locks.AcquireAsync(app.Id))
        {
            var current = await CurrentReleaseAsync(app);
            if (verb == "restart" && !await docker.ContainerExistsAsync(app.ContainerName)) return "There's no container to restart. Start or deploy the app instead.";
            var r = await Composer.RunAsync(app.ComposeProject, current?.File, null, verb == "stop" ? ["stop", "-t", "20"] : ["restart", "-t", "20"]);
            return r.Ok ? null : Last(r.ErrorText);
        }
    }

    /// <summary>
    /// docker compose down for the whole project; its named volumes and its kept data folder only with
    /// <paramref name="volumes"/>, and with <paramref name="images"/> the images of its own (gitberth/&lt;slug&gt;/…: what
    /// Gitberth built for it, and the pins of what it pulled) and, for an app that isn't a preview, the images it pulled
    /// that nothing else needs. Its releases go either way, and its own settings.
    /// </summary>
    private async Task DownComposeAsync(AppDefinition app, bool images, bool volumes)
    {
        var current = await CurrentReleaseAsync(app);
        ComposeImages used;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            used = ComposeImages.Parse(await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded)
                .OrderByDescending(d => d.Id).Select(d => d.Images).FirstOrDefaultAsync());
            var shared = ComposeNames.EveryServiceSharedKey(app.Id);
            await db.Settings.Where(s => s.Key == shared).ExecuteDeleteAsync();
        }
        var down = await Composer.RunAsync(app.ComposeProject, current?.File, null, ["down", "--remove-orphans", "-t", "20", .. volumes ? new[] { "-v" } : []]);
        if (!down.Ok) logger.LogWarning("docker compose down for {App} said: {Error}", app.Slug, Last(down.ErrorText));
        // Anything of the app's left behind (an orphan of an interrupted deploy) goes by its labels.
        var (left, _) = await docker.ListContainersAsync(managedOnly: true);
        foreach (var c in left.Where(c => c.Labels.GetValueOrDefault("gitberth.app") == app.Slug && c.Labels.ContainsKey(ComposeNames.ServiceLabel)))
            await docker.RemoveAsync(c.Name);
        if (images)
        {
            var own = await docker.ListImageTagsUnderAsync($"gitberth/{app.Slug}/");
            var (needed, ids, inUse) = await PulledFactsAsync(app, app.IsPreview ? ComposeImages.None : used);
            foreach (var image in ImagesToRemove(app, own, used, needed, ids, inUse)) await docker.RemoveImageAsync(image);
            await docker.PruneDanglingImagesAsync(app.Slug);
        }
        var releases = ComposeReleases.For(options, app.Slug);
        if (volumes) DeployPaths.TryDeleteDirectory(releases.Root);
        else if (Directory.Exists(releases.Root))
            foreach (var dir in Directory.EnumerateDirectories(releases.Root).Where(d => Path.GetFileName(d) != "data")) DeployPaths.TryDeleteDirectory(dir);
    }

    /// <summary>
    /// The images a deleted Compose app takes with it: the tags of its own (gitberth/&lt;slug&gt;/…, its builds and pins),
    /// and, for an app that isn't a preview (a preview's file is a pull request's, and what it pulled may be anyone's), each
    /// image it pulled that nothing else needs: never a gitberth/… one (another app's build), nor one that another app runs,
    /// follows or keeps for a rollback (<paramref name="needed"/>, by tag or ID), nor one a container was made from.
    /// </summary>
    /// <param name="own">The tags under gitberth/&lt;slug&gt;/ on this host.</param>
    /// <param name="ids">The ID each pulled image's tag names here (null: none).</param>
    /// <param name="inUse">The IDs of the images that containers were made from.</param>
    internal static List<string> ImagesToRemove(AppDefinition app, IReadOnlyList<string> own, ComposeImages used, IReadOnlySet<string> needed,
        IReadOnlyDictionary<string, string?> ids, IReadOnlySet<string> inUse)
    {
        var prefix = $"gitberth/{app.Slug}/";
        var remove = own.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (app.IsPreview) return remove;
        foreach (var image in PulledOf(used))
            if (!needed.Contains(image) && ids.GetValueOrDefault(image) is { } id && !needed.Contains(id) && !inUse.Contains(id)) remove.Add(image);
        return remove;
    }

    // The images a release pulled: never gitberth/… ones.
    private static IEnumerable<string> PulledOf(ComposeImages used) =>
        used.Refs.Values.Where(i => !i.StartsWith("gitberth/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal);

    // What deciding on a deleted app's pulled images needs: what other apps need, each image's ID, and which are in use.
    private async Task<(IReadOnlySet<string> Needed, Dictionary<string, string?> Ids, IReadOnlySet<string> InUse)> PulledFactsAsync(AppDefinition app, ComposeImages used)
    {
        var pulled = PulledOf(used).ToList();
        if (pulled.Count == 0) return (new HashSet<string>(), new Dictionary<string, string?>(), new HashSet<string>());
        HashSet<string> needed;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var rows = await db.Apps.AsNoTracking().Where(a => a.Id != app.Id).Select(a => new { a.CurrentImage, a.PreviousImage, a.ImageRef }).ToListAsync();
            var kept = await db.Deployments.AsNoTracking().Where(d => d.AppId != app.Id && d.Images != null).Select(d => d.Images).ToListAsync();
            needed = rows.SelectMany(r => new[] { r.CurrentImage, r.PreviousImage, r.ImageRef }).OfType<string>()
                .Concat(kept.Select(ComposeImages.Parse).SelectMany(i => i.Refs.Values.Concat(i.Ids.Values)))
                .ToHashSet(StringComparer.Ordinal);
        }
        var ids = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inUse = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in pulled)
        {
            ids[image] = await docker.ImageIdAsync(image);
            if (ids[image] is { } id && (await docker.ContainersOfImageAsync(id)).Count > 0) inUse.Add(id);
        }
        return (needed, ids, inUse);
    }

    private static string Last(string error) =>
        error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? error;
}
