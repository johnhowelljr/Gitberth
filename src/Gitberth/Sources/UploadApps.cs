using Gitberth.Apps;
using Gitberth.Components.Shared.Network;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Sources;

/// <summary>A new version of an uploaded app, kept and queued: its deploy, and what came.</summary>
public sealed record UploadDeploy(int AppId, int DeploymentId, string Name, string Sha, long Size);

/// <summary>
/// Uploaded folders as apps (B6): New app's Deploy of a staged upload, a new version of an uploaded app (its page's drop
/// zone, or POST /api/v1/apps/{name}/upload), and the API's upload for an app that doesn't exist yet, detected and made as
/// New app would make it. Each upload is a deploy of its own (trigger Upload; its "commit" the archive's SHA-256), and the
/// app's newest upload is what Deploy builds.
/// </summary>
public sealed class UploadApps(UploadStore store, IDbContextFactory<GitberthDb> dbFactory, AppService apps, AppActions actions, AppCatalog catalog,
    ProjectAnalyzer analyzer, ILogger<UploadApps> logger, Railpack.IRailpackPlanner? railpack = null, Audit.AuditLog? audit = null)
{
    private static readonly Railpack.NoRailpack NoPlanner = new();

    /// <summary>Who's uploading, as an upload's "author" says it: admin, the token ci.</summary>
    public static string? Who() => Audit.AuditScope.Current is { Kind: not Audit.AuditActorKind.Gitberth } actor ? Audit.AuditActors.Describe(actor) : null;

    /// <summary>
    /// New app's Deploy: the staged upload becomes an app with these settings, its archive kept, and its first deploy
    /// queued. Returns the app's ID.
    /// </summary>
    public async Task<(int AppId, int DeploymentId)> CreateAsync(StagedUpload staged, AppSettingsModel model, DetectionResult? detection, CancellationToken ct = default)
    {
        var appId = await apps.CreateUploadAsync(staged, model, detection, Who(), ct);
        try
        {
            store.Keep(appId, staged, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Couldn't keep the upload of app {AppId}", appId);
            throw new InvalidOperationException($"{model.Name} was made, but its upload couldn't be kept here ({ex.Message.TrimEnd('.')}). Upload it again on its page.");
        }
        var deployment = await actions.DeployAsync(appId, DeployTrigger.Created, ct, expectedSha: staged.Sha);
        return (appId, deployment);
    }

    /// <summary>
    /// A new version of an uploaded app: kept (its oldest beyond the newest five go, never the one it runs), made the one
    /// Deploy builds, and deployed at once. Refused for any other app, and while it's being deleted.
    /// </summary>
    public async Task<UploadDeploy> NewVersionAsync(int appId, StagedUpload staged, CancellationToken ct = default)
    {
        AppDefinition app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct) ?? throw new InvalidOperationException("That app no longer exists.");
            if (!app.FromUpload)
                throw new InvalidOperationException($"{app.Name} isn't an uploaded app, so it takes no upload: {(app.FromRepo ? "it's deployed from its repo" : "its image is its source")}.");
            if (app.IsPreview) throw new InvalidOperationException("A preview takes no upload.");
            var before = app.UploadSha;
            store.Keep(appId, staged, [app.CurrentCommit, before]);
            app.UploadName = staged.Name;
            app.UploadSha = staged.Sha;
            app.UploadSize = staged.Size;
            app.UploadedAt = staged.At;
            app.UploadedBy = Who();
            app.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        audit?.Record(new Audit.AuditEntry("app.upload", $"Uploaded a new version of {app.Name}: {staged.Name} ({UploadArchives.Size(staged.Size)}).")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
            Changes = [new("Upload", app.CurrentCommit is { Length: >= 7 } running ? UploadArchives.Short(running) : null, UploadArchives.Short(staged.Sha)), new("File", null, staged.Name)],
        });
        var deployment = await actions.DeployAsync(appId, DeployTrigger.Upload, ct, expectedSha: staged.Sha);
        return new UploadDeploy(appId, deployment, staged.Name, staged.Sha, staged.Size);
    }

    /// <summary>What an upload is, as New app (and the API) work it out: the apps in it, the one picked, and how it'd run.</summary>
    /// <param name="Candidates">The apps Gitberth found in it, best first.</param>
    /// <param name="Dir">The folder of the app picked, inside the upload ("" for its root).</param>
    /// <param name="ComposeFile">A Compose file in that folder, when there is one; its text and what it has.</param>
    public sealed record Analysis(IReadOnlyList<AppCandidate> Candidates, string Dir, string? Hint, DetectionResult Detection, IReadOnlyList<string> ComposeFiles,
        IReadOnlyDictionary<string, string> ComposeTexts, string? ComposeFile, ComposeFileInfo? ComposeInfo);

    /// <summary>
    /// The staged upload looked at as a repo is: its apps (the best one picked unless <paramref name="dir"/> says), then the
    /// one in its folder, and the Compose files there (never one that's a link out of the upload).
    /// </summary>
    public async Task<Analysis> AnalyzeAsync(StagedUpload staged, string? dir = null, string? hint = null, CancellationToken ct = default)
    {
        var root = store.SourceOf(staged);
        var candidates = await Task.Run(() => analyzer.FindApps(root), ct);
        if (dir is null && candidates.Count > 0) (dir, hint) = (candidates[0].Dir, candidates[0].Hint);
        var sub = ProjectAnalyzer.NormalizeDir(dir) ?? throw new InvalidOperationException($"The folder '{dir}' isn't inside the upload.");
        if (sub.Length > 0 && !Directory.Exists(Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar))))
            throw new InvalidOperationException($"The folder '{sub}' isn't in the upload.");
        var detection = await Task.Run(() => analyzer.Analyze(root, new AnalyzeOptions(hint, AppDir: sub)), ct);
        var folder = sub.Length == 0 ? root : Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
        var files = ComposeFiles.Find(folder).Where(f => !ComposeTransform.LinksOut(root, Path.Combine(folder, f))).ToList();
        var texts = files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(folder, f)), StringComparer.Ordinal);
        var first = files.FirstOrDefault();
        return new Analysis(candidates, sub, detection.SelectedProject ?? hint, detection, files, texts, first, first is null ? null : ComposeFiles.Read(texts[first]));
    }

    /// <summary>
    /// The API's upload for an app that doesn't exist yet: detected as New app does (its best app, Compose when its file
    /// has several services or nothing else builds, Railpack for what Gitberth doesn't know), made with New app's defaults
    /// under <paramref name="name"/> and deployed. Refused with what would make it work when nothing in it can be built.
    /// </summary>
    public async Task<(int AppId, int DeploymentId, string Key, string Message)> CreateDetectedAsync(StagedUpload staged, string name, CancellationToken ct = default)
    {
        var a = await AnalyzeAsync(staged, ct: ct);
        var source = store.SourceOf(staged);
        var folder = a.Dir.Length == 0 ? source : Path.Combine(source, a.Dir.Replace('/', Path.DirectorySeparatorChar));
        var useCompose = a.ComposeInfo is { } found && Components.NewApp.GitSource.PreferCompose(found, a.Detection.Kind != ProjectKind.Unknown);
        var (detection, mode, _) = useCompose ? (a.Detection, DockerfileMode.Auto, false)
            : await ChooseBuilderAsync(a, source, folder, ct);
        if (!useCompose && detection.Kind == ProjectKind.Unknown) throw new UploadRefusedException(CantTell(detection));
        var model = new AppSettingsModel
        {
            Name = name,
            Branch = "main",
            Subdirectory = a.Dir.Length == 0 ? null : a.Dir,
            ProjectHint = a.Hint,
            DockerfileMode = mode,
        };
        if (useCompose && a.ComposeInfo is { } info)
        {
            var web = ComposeFiles.PickWeb(info.Services)?.Service ?? info.Services[0].Name;
            (model.ComposeFile, model.WebService) = (a.ComposeFile, web);
            model.Ports = (await ComposePortsAsync(info, web, ct)).ToList();
            foreach (var v in info.Variables) model.Env.Add(new EnvRow { Key = v.Name, Value = "", IsSecret = Security.Masking.IsSecret(v.Name, "") });
            if (ComposeApps.Problems(a.ComposeTexts[a.ComposeFile!], web) is { Count: > 0 } problems) throw new UploadRefusedException(string.Join(" ", problems));
        }
        else
        {
            var rules = new List<PortRule>();
            if (detection.ContainerPort is int port) rules.Add(new PortRule(port, "tcp", PortAccess.Lan, await SuggestAsync(port, ct)));
            model.Ports = ContainerPortsDraft.ForNew(rules, detection.ContainerPort).ToPortRows().ToList();
            foreach (var s in detection.EnvSuggestions)
                model.Env.Add(new EnvRow { Key = s.Key, Value = s.Value, IsSecret = s.IsSecret && !EnvRules.IsRailpackKey(s.Key), BuildTime = s.BuildTime });
        }
        var portProblems = await apps.ValidatePortsAsync(null, model.Ports, ct: ct);
        var all = await apps.ValidateAsync(null, model, ct);
        if (all.Count > 0 || portProblems.Count > 0) throw new UploadRefusedException(string.Join(" ", all.Concat(portProblems.Select(p => p.Message)).Distinct()));
        var (appId, deployment) = await CreateAsync(staged, model, detection, ct);
        var key = await catalog.KeyForIdAsync(appId, ct) ?? AppDefinition.ContainerNameFor(AppService.Slugify(name));
        return (appId, deployment, key, $"Created {model.Name} from {staged.Name} ({detection.Title}): deploy #{deployment} is queued.");
    }

    private async Task<(DetectionResult Detection, DockerfileMode Mode, bool Tried)> ChooseBuilderAsync(Analysis a, string source, string folder, CancellationToken ct)
    {
        try
        {
            return await Agents.Tools.AppAdminTools.ChooseBuilderAsync(railpack ?? NoPlanner, alwaysRailpack: false, a.Detection, source, a.Dir, folder, [], ct);
        }
        catch (Agents.ToolError ex)
        {
            throw new UploadRefusedException(ex.Message);
        }
    }

    /// <summary>
    /// What New app and the API say when nothing in an upload can be built: that Gitberth can't tell what it is, and what
    /// would make it work.
    /// </summary>
    public static string CantTell(DetectionResult detection) =>
        "Gitberth can't tell what this is, so it can't build it. Add a Dockerfile, or the file its language starts from: "
        + "a package.json, requirements.txt or pyproject.toml, go.mod, Gemfile, composer.json, Cargo.toml, a .csproj, or an index.html for a static site."
        + (detection.Warnings.FirstOrDefault() is { } why ? " " + why : "");

    // The web service's ports, as New app's Compose path proposes them.
    private async Task<IReadOnlyList<PortRow>> ComposePortsAsync(ComposeFileInfo info, string web, CancellationToken ct)
    {
        if (info.Service(web) is not { } service) return [];
        var port = ComposeFiles.WebPortOf(service);
        var rules = new List<PortRule>();
        foreach (var p in service.Ports.DistinctBy(p => (p.Target, p.Protocol)))
            rules.Add(p.Published is int published
                ? new PortRule(p.Target, p.Protocol, PortAccess.Lan, await SuggestAsync(published, ct))
                : new PortRule(p.Target, p.Protocol, p.Target == port ? PortAccess.Lan : PortAccess.Internal, p.Target == port ? await SuggestAsync(p.Target, ct) : null));
        if (port is int webPort && rules.All(r => r.ContainerPort != webPort)) rules.Insert(0, new PortRule(webPort, "tcp", PortAccess.Lan, await SuggestAsync(webPort, ct)));
        return ContainerPortsDraft.ForNew(rules.OrderBy(r => r.ContainerPort == port ? 0 : 1).ToList(), port).ToPortRows();
    }

    private async Task<int?> SuggestAsync(int port, CancellationToken ct)
    {
        try
        {
            return await apps.SuggestHostPortAsync(port, "tcp", null, [], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
