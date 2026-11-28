using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>
/// A Compose app's deploy (a Git app with a Compose file, or an inline one): after the variables are put together, its
/// release is kept under /data/compose/&lt;slug&gt;/, its Compose file resolved by Compose with the app's variables (in the
/// project's .env), turned into the file Gitberth runs (<see cref="ComposeTransform"/>), its services built by Gitberth's
/// own build runner where it can (else by docker compose build), and the project brought up with docker compose up --wait.
/// Every service has to stay up, and the web service answer the app's health URL; otherwise the release that ran before
/// is brought up again (RolledBack), or, on a first deploy, what it started is taken down.
/// </summary>
public sealed partial class DeployPipeline
{
    private ComposeRunner? _ownComposer;

    private ComposeRunner Composer => composer ?? (_ownComposer ??= new ComposeRunner(docker, options,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposeRunner>.Instance));

    /// <summary>How long docker compose up waits for every service to run (and be healthy, for one with a health check).</summary>
    internal TimeSpan ComposeWait { get; set; } = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A Compose deploy's files: its release's folder, its Compose file, the folder Compose reads it from, and its images.</summary>
    /// <param name="Shown">The Compose file as the log names it: compose.yaml, or api/compose.yaml in a subfolder.</param>
    /// <param name="Images">A deploy without a build: the images the built services run.</param>
    /// <param name="RepoEnv">The repo's own .env next to the Compose file, which goes first in the one Gitberth writes.</param>
    /// <param name="Pins">A deploy without a build: its pulled services whose tag moved since, run by their pin.</param>
    private sealed record ComposeSource(string ReleaseDir, string ComposePath, string ProjectDir, string Shown, string Tag,
        IReadOnlyDictionary<string, string>? Images, AppTraits Traits, string? RepoEnv, IReadOnlyDictionary<string, string>? Pins = null);

    /// <summary>What a Compose app's .env holds (<see cref="ComposeEnvValues"/>), and what its deploy log says about it.</summary>
    /// <param name="Parts">The database variables' parts Gitberth set (DATABASE_URL_HOST…).</param>
    /// <param name="Replaced">The app's own variables a part took the place of.</param>
    /// <param name="BuiltIns">The template's built-ins Gitberth set (APP_URL…).</param>
    /// <param name="Overridden">The built-ins left to the app's own variables of those names.</param>
    internal sealed record ComposeEnv(List<(string Key, string Value)> Values, List<string> Parts, List<string> Replaced, List<string> BuiltIns,
        List<string> Overridden);

    /// <summary>
    /// What the project's .env holds: the app's variables; each database variable Gitberth sets, in parts too
    /// (<see cref="Databases.DatabaseUrls.Parts"/>: DATABASE_URL_HOST, _PORT, _NAME, _USER, _PASSWORD) for an app that wants
    /// them one by one, in place of the app's own variables of those names, as the URL itself is; and for an app installed
    /// from a template, where it's reached (<paramref name="builtIns"/>: APP_URL…), unless the app has its own variable of
    /// that name. Each reaches a service only where its file uses it (${DATABASE_URL_HOST}).
    /// </summary>
    internal static ComposeEnv ComposeEnvValues(IEnumerable<EnvValue> runtimeEnv, IEnumerable<Databases.ProvidedVariable> provided,
        IEnumerable<(string Key, string Value)>? builtIns = null)
    {
        var derived = provided.SelectMany(p => Databases.DatabaseUrls.Parts(p.Key, p.Value)).GroupBy(p => p.Key, StringComparer.Ordinal)
            .Select(g => g.Last()).ToList();
        var taken = derived.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var own = runtimeEnv.GroupBy(e => e.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Last().Value)).ToList();
        var values = own.Where(v => !taken.Contains(v.Key)).Concat(derived).ToList();
        var present = values.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        var (set, overridden) = (new List<string>(), new List<string>());
        foreach (var (key, value) in builtIns ?? [])
        {
            if (present.Contains(key))
            {
                overridden.Add(key);
                continue;
            }
            values.Add((key, value));
            set.Add(key);
        }
        return new ComposeEnv(values, derived.Select(p => p.Key).ToList(), own.Where(v => taken.Contains(v.Key)).Select(v => v.Key).ToList(), set, overridden);
    }

    /// <summary>
    /// What the deploy log says of what Gitberth set in a Compose app's .env besides the app's own variables: its databases'
    /// parts and the variables of the app's they took the place of, and its template's built-ins (not secret: an address),
    /// or the app's own values that are used in their place.
    /// </summary>
    internal static List<string> EnvLines(ComposeEnv env, IReadOnlyList<(string Key, string Value)> builtIns)
    {
        var lines = new List<string>();
        if (env.Parts.Count > 0) lines.Add($"Its databases' connection strings in parts too, for a file that uses them: {string.Join(", ", env.Parts)}.");
        if (env.Replaced.Count > 0)
            lines.Add($"In place of the value{(env.Replaced.Count == 1 ? "" : "s")} set on the Environment tab: {string.Join(", ", env.Replaced)} (its database's, as Gitberth sets them).");
        if (env.BuiltIns.Count > 0)
            lines.Add($"Where it's reached, as its template asks: {string.Join(", ", builtIns.Where(b => env.BuiltIns.Contains(b.Key)).Select(b => $"{b.Key}={b.Value}"))}.");
        if (env.Overridden.Count > 0)
            lines.Add($"Set on the Environment tab, so used in place of Gitberth's: {string.Join(", ", env.Overridden)}.");
        return lines;
    }

    private async Task<(Prepared Prepared, SwapOutcome Outcome)> ComposeAsync(GitberthDb db, AppDefinition app, Deployment dep, DeployRequest request,
        PreviewInfo? preview, List<EnvValue> runtimeEnv, List<EnvValue> buildEnv, IReadOnlySet<string> gitHubKeys, VercelContext? vercelFor, BuildSource fetch,
        IReadOnlyList<Databases.ProvidedVariable> provided, string workDir, DeployLog log, CancellationToken ct)
    {
        var releases = ComposeReleases.For(options, app.Slug);
        var history = await HistoryAsync(app.Id);
        // What runs now: the file its services were brought up with, and its release (for the kept folders' updates: a file
        // the app hasn't changed since that release takes the new one).
        var runningFile = ComposeReleases.PickRollback(history.Select(h => (h.Id, h.Status)), dep.Id, id => releases.ResolvedOf(id) is not null) is int last
            ? releases.ResolvedOf(last) : null;
        var running = runningFile is null ? null : Path.GetDirectoryName(runningFile);
        var source = request.Build
            ? await ComposeSourceAsync(db, app, dep, fetch, workDir, releases, log, ct)
            : await ComposeAgainAsync(db, app, dep, request, releases, log);
        ct.ThrowIfCancellationRequested();

        if (vercelFor is not null)
        {
            var facts = vercelFor.Facts(app, dep.CommitSha, dep.CommitMessage, dep.CommitAuthor, source.Traits);
            foreach (var line in DescribeVercelVariables(runtimeEnv, facts)) log.Line(line);
            runtimeEnv = VercelEnv.AddTo(runtimeEnv, VercelEnv.For(facts));
        }
        else if (app.FromRepo)
        {
            log.Line("Vercel's variables are turned off for this app.");
        }

        await SetStatusAsync(db, dep, DeployStatus.Building);
        // The app's variables fill the file's ${…}: Compose reads them from the project's .env, where env_file: .env finds them too.
        // Its databases' connection strings are there in parts as well (the password in them is masked in the log already), and
        // an app installed from a template is told where it's reached now: its main domain, else its LAN address.
        var builtInValues = builtIns is null || app.TemplateId is null || preview is not null ? [] : await builtIns.ForAsync(app, CancellationToken.None);
        var env = ComposeEnvValues(runtimeEnv, provided, builtInValues);
        var values = env.Values;
        await WriteOwnFileAsync(Path.Combine(source.ProjectDir, ".env"), ComposeEnvFile.Write(values, source.RepoEnv));
        if (values.Select(v => v.Key).Except(env.Parts).Except(env.BuiltIns).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } fromApp)
            log.Line($"The Compose file's variables come from the app's: {string.Join(", ", fromApp)}.");
        foreach (var line in EnvLines(env, builtInValues)) log.Line(line);
        if (source.Pins is { Count: > 0 } pins)
            log.Line($"Their tags moved on since that release, so these run the images it ran: {string.Join(", ", pins.Select(p => $"{p.Key} ({p.Value})"))}.");

        log.Step($"Reading {source.Shown} with docker compose config");
        var (resolved, error) = await Composer.ConfigAsync(app.ComposeProject, source.ComposePath, source.ProjectDir, source.ReleaseDir, sandbox: preview is not null, ct);
        if (resolved is null) throw new DeployException($"Compose couldn't read {source.Shown}: {error}");
        var target = await TargetAsync(app, dep, source, values, preview is not null);
        var plan = ComposeTransform.Apply(resolved, target);
        foreach (var note in plan.Notes) log.Line(note);
        if (plan.Problems.Count > 0) throw new DeployException(string.Join(" ", plan.Problems));
        log.Line($"Services: {string.Join(", ", plan.Services.Select(s => s == target.WebService ? $"{s} (the web service, {plan.Members[s]})" : s))}");

        Keep(plan.Kept, releases.DataDir, running, log);
        if (plan.Builds.Count > 0) await BuildServicesAsync(app, dep, plan, source, buildEnv, gitHubKeys, workDir, log, ct);
        else if (!request.Build && source.Images is { Count: > 0 }) log.Line("Nothing is built: the release's images are run again.");
        ct.ThrowIfCancellationRequested();

        // A service this deploy runs as the release before it did keeps running: docker compose recreates only what changed
        // (a scheduled restart's deploy restarts them all).
        var unchanged = dep.Trigger == DeployTrigger.Restart || runningFile is null ? []
            : ComposeTransform.KeepUnchanged(plan.Model, JsonNode.Parse(await File.ReadAllTextAsync(runningFile, CancellationToken.None)) as JsonObject);
        if (unchanged.Count > 0) log.Line($"Unchanged since the release that runs, so not restarted: {string.Join(", ", unchanged)}.");

        // Its config files (B3), each in its service's volumes.
        await AddComposeConfigMountsAsync(app, plan.Model, log);

        // The file this deploy runs, kept with its release: a rollback brings it up again as it is.
        var own = (JsonObject)plan.Model[ComposeTransform.OwnKey]!;
        own["file"] = Path.GetRelativePath(source.ReleaseDir, source.ComposePath).Replace('\\', '/');
        own["dir"] = Path.GetRelativePath(source.ReleaseDir, source.ProjectDir).Replace('\\', '/');
        var file = Path.Combine(source.ReleaseDir, ComposeReleases.ResolvedName(dep.Id));
        await WriteOwnFileAsync(file, plan.Model.ToJsonString(Indented));
        dep.ImageTag = plan.Images.GetValueOrDefault(target.WebService);
        dep.Images = new ComposeImages(plan.Images, new Dictionary<string, string>()).ToJson();

        await SetStatusAsync(db, dep, DeployStatus.Starting);
        // The new release's release command (B2), in a one-off container of its service, before it's brought up.
        if (RunsComposeRelease(app, request, dep)) await Commands.RunForComposeAsync(app, Composer, file, source.ProjectDir, log, ct);
        var shareKey = ComposeNames.EveryServiceSharedKey(app.ParentAppId ?? app.Id);
        var everyShared = await db.Settings.AsNoTracking().AnyAsync(s => s.Key == shareKey && s.Value == "true", CancellationToken.None);
        var outcome = await ComposeUpAsync(app, dep, plan, target.WebService, file, source.ProjectDir, history, releases, unchanged, everyShared, log, ct);
        // What each service ran, by its image's ID, held by the app's own tag: a rollback runs it again even once its tag moved.
        if (outcome.Ok) dep.Images = (await RecordImagesAsync(app.Slug, plan, log)).ToJson();
        var networks = await db.AppNetworks.AsNoTracking().Where(n => n.AppId == app.Id).OrderBy(n => n.Id).ToListAsync(CancellationToken.None);
        return (new Prepared(dep.ImageTag ?? "", null, null, source.Traits), outcome with { Networks = networks });
    }

    // ---- The release's files ----

    // A deploy with a build: the repo at its commit, or the Compose file Gitberth keeps, as a release of its own.
    private async Task<ComposeSource> ComposeSourceAsync(GitberthDb db, AppDefinition app, Deployment dep, BuildSource fetch, string workDir,
        ComposeReleases releases, DeployLog log, CancellationToken ct)
    {
        releases.EnsureRoot();
        // An inline Compose app's file is Gitberth's own; a repo's and an upload's are in their files.
        if (app.Source == Apps.AppSource.Compose)
        {
            var yaml = app.ComposeYamlProtected is { Length: > 0 } stored ? protector.TryUnprotect(stored) : "";
            if (yaml.Length == 0)
                throw new DeployException("The app's Compose file can't be read (Gitberth's key ring may have been lost). Paste it again in the app's settings.");
            var id = ComposeReleases.IdOf(yaml);
            var dir = releases.Dir(id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "compose.yaml");
            // A pasted file often has its passwords in it: kept encrypted in Gitberth's database, and readable by root alone here.
            await WriteOwnFileAsync(path, yaml);
            log.Step($"Deploying the Compose file Gitberth keeps for {app.Name} (version {id})");
            return new ComposeSource(dir, path, dir, "the Compose file", id, null, AppTraits.None, null);
        }

        var (commit, srcDir) = await FetchSourceAsync(db, app, dep, fetch, workDir, log, ct);
        var appDir = app.Subdirectory?.Trim().Trim('/') ?? "";
        var folder = Inside(srcDir, appDir) ?? throw new DeployException($"The subfolder '{app.Subdirectory}' doesn't exist {Where(app)}.");
        var name = app.ComposeFile is { Length: > 0 } f ? f.Trim().Trim('/') : ComposeFiles.Find(folder).FirstOrDefault()
            ?? throw new DeployException($"There's no Compose file in {(appDir.Length > 0 ? appDir + "/" : RootWords(app))} {Where(app)}.");
        if (Inside(folder, name, mustBeFolder: false) is null)
            throw new DeployException($"There's no {name} in {(appDir.Length > 0 ? appDir + "/" : RootWords(app))} {Where(app)} any more. Pick the Compose file again in the app's settings.");
        // Gitberth reads the Compose file and the .env itself: never through a link that leads out of the repo (a pull
        // request's .env -> /proc/1/environ would be Gitberth's own environment).
        if (ComposeTransform.LinksOut(srcDir, Path.Combine(folder, name)))
            throw new DeployException($"{(appDir.Length > 0 ? appDir + "/" : "")}{name} is a link out of the repo, which Gitberth doesn't follow.");
        if (ComposeTransform.LinksOut(srcDir, Path.Combine(folder, ".env")))
            throw new DeployException($"The repo's {(appDir.Length > 0 ? appDir + "/" : "")}.env is a link out of the repo, which Gitberth doesn't follow.");
        var traits = VercelEnv.Detect(folder, srcDir);
        var tag = commit.Sha[..7];
        var release = releases.Dir(tag);
        var shown = appDir.Length > 0 ? $"{appDir}/{name}" : name;
        var projectDir = Path.GetDirectoryName(Path.Combine(release, appDir, name))!;
        if (Directory.Exists(release))
        {
            // A commit's files never change: the release kept from an earlier deploy of it is used as it is, and its bind
            // mounts' containers keep reading the same folder.
            log.Line($"The files of {tag} are kept from an earlier deploy: they're used as they are.");
        }
        else
        {
            // The clone becomes the release (both are in Gitberth's data folder, so it's a rename), without its history.
            DeployPaths.TryDeleteDirectory(Path.Combine(srcDir, ".git"));
            Directory.Move(srcDir, release);
            // The repo's .env (or a link of the repo's in its place, which can't lead out: checked above) goes aside.
            var repoEnv = Path.Combine(projectDir, ".env");
            if (Present(repoEnv)) File.Move(repoEnv, Path.Combine(projectDir, RepoEnvName), overwrite: true);
        }
        return new ComposeSource(release, Path.Combine(release, appDir, name), projectDir, shown, tag, null, traits,
            await RepoEnvAsync(release, projectDir));
    }

    // The repo's own .env, kept aside: the .env Gitberth writes starts with it.
    private const string RepoEnvName = ".gitberth-repo.env";

    /// <summary>
    /// The repo's own .env as the release keeps it (null: it has none), read by Gitberth's own process: never through a
    /// link that leads out of the release (refused), whatever put it there.
    /// </summary>
    internal static async Task<string?> RepoEnvAsync(string release, string projectDir)
    {
        var path = Path.Combine(projectDir, RepoEnvName);
        if (!Present(path)) return null;
        if (ComposeTransform.LinksOut(release, path) || Directory.Exists(path))
            throw new DeployException("The repo's .env is a link out of the repo (or not a file), which Gitberth doesn't follow.");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, CancellationToken.None) : null;
    }

    // Something is there, a link (even one that leads nowhere) included.
    private static bool Present(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// Writes a file of Gitberth's own into a release (its .env, the file a deploy runs), readable by root alone: whatever the
    /// repo has at that path (a link to Gitberth's database, say, or one that leads nowhere) is removed first, and the file
    /// is made new, so nothing is ever written through a link.
    /// </summary>
    internal static async Task WriteOwnFileAsync(string path, string text)
    {
        if (Directory.Exists(path) && new FileInfo(path).LinkTarget is null) Directory.Delete(path, recursive: true);
        else if (Present(path)) File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(text);
    }

    // A deploy without a build: the release another deploy ran (a rollback's, or the one that runs now), with its images.
    private async Task<ComposeSource> ComposeAgainAsync(GitberthDb db, AppDefinition app, Deployment dep, DeployRequest request, ComposeReleases releases, DeployLog log)
    {
        var fromId = request.FromDeployment
            ?? await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id)
                .Select(d => (int?)d.Id).FirstOrDefaultAsync(CancellationToken.None)
            ?? throw new DeployException("Nothing of the app has run yet, so there's no release to run again. Deploy it instead.");
        var file = releases.ResolvedOf(fromId)
            ?? throw new DeployException($"The files of deploy #{fromId} aren't kept any more (Gitberth keeps the last {ComposeReleases.Kept}), so it can't run again. Deploy the app instead.");
        var dir = Path.GetDirectoryName(file)!;
        var model = JsonNode.Parse(await File.ReadAllTextAsync(file, CancellationToken.None)) as JsonObject;
        var own = model?[ComposeTransform.OwnKey];
        var composeFile = own?["file"]?.GetValue<string>() ?? "compose.yaml";
        var projectDir = Path.GetFullPath(Path.Combine(dir, own?["dir"]?.GetValue<string>() ?? "."));
        var ran = ComposeImages.Parse(await db.Deployments.AsNoTracking().Where(d => d.Id == fromId).Select(d => d.Images).FirstOrDefaultAsync(CancellationToken.None));
        var tag = Path.GetFileName(dir);
        log.Step(request.FromDeployment is int back && dep.Trigger == DeployTrigger.Rollback
            ? $"Running the release of deploy #{back} ({tag}) again, without a build"
            : $"Running the release {tag} again with the app's settings, without a build");
        // A pulled image whose tag moved since (a later pull, a deploy without the cache): the image that release ran.
        var pins = await PinsAsync(app.Slug, ran);
        // The composed file is the release's own: never read through a link out of it.
        if (ComposeTransform.LinksOut(dir, Path.Combine(dir, composeFile)))
            throw new DeployException($"{composeFile} is a link out of the release, which Gitberth doesn't follow.");
        return new ComposeSource(dir, Path.Combine(dir, composeFile), projectDir, composeFile, tag, ran.Refs.Count > 0 ? ran.Refs : null, AppTraits.None,
            await RepoEnvAsync(dir, projectDir), pins);
    }

    /// <summary>A deploy's images (Deployment.Images), by service; empty when they can't be read.</summary>
    internal static IReadOnlyDictionary<string, string> ParseImages(string? json) => ComposeImages.Parse(json).Refs;

    /// <summary>
    /// The pins of a release's pulled services whose tag now names another image than the one it ran (and whose pin is
    /// still here): they run by their pin.
    /// </summary>
    private async Task<Dictionary<string, string>> PinsAsync(string slug, ComposeImages ran)
    {
        var now = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var image in ran.Refs.Values.Distinct()) now[image] = await docker.ImageIdAsync(image);
        var pins = ran.Moved(slug, image => now.GetValueOrDefault(image));
        foreach (var (service, pin) in pins.ToList())
            if (await docker.ImageIdAsync(pin) is null) pins.Remove(service);
        return pins;
    }

    /// <summary>
    /// What each service ran, once they're up: its container's image ID, and for a pulled image a tag of the app's own for
    /// it (gitberth/&lt;slug&gt;/&lt;service&gt;:pin-…), which keeps it here for a rollback while a kept release needs it.
    /// </summary>
    private async Task<ComposeImages> RecordImagesAsync(string slug, ComposePlan plan, DeployLog log)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (service, member) in plan.Members)
        {
            var r = await docker.RunAsync(["container", "inspect", "--format", "{{.Image}}", member]);
            if (!r.Ok || r.StdOut.Trim() is not { Length: > 0 } id) continue;
            ids[service] = id;
            if (plan.Images.GetValueOrDefault(service) is { } image && !ComposeImages.BuiltFor(slug, image))
            {
                var tagged = await docker.RunAsync(["tag", id, ComposeImages.PinTag(slug, service, id)]);
                if (!tagged.Ok) log.Line($"warning: the image {service} ran couldn't be kept for a rollback: {tagged.ErrorText.Trim()}");
            }
        }
        return new ComposeImages(plan.Images, ids);
    }

    // What the transform needs: the app, the deploy, and the release's folders as Gitberth and the Docker host see them.
    private async Task<ComposeTarget> TargetAsync(AppDefinition app, Deployment dep, ComposeSource source, IReadOnlyList<(string Key, string Value)> values, bool preview)
    {
        var releases = ComposeReleases.For(options, app.Slug);
        var releaseHost = await Composer.HostPathAsync(source.ReleaseDir);
        var dataHost = await Composer.HostPathAsync(releases.DataDir);
        if (releaseHost is null || dataHost is null)
            throw new DeployException("Gitberth couldn't find where its data folder is on the Docker host, so the Compose file's bind mounts can't be given their host paths.");
        var raw = ComposeFiles.Read(await File.ReadAllTextAsync(source.ComposePath, CancellationToken.None));
        var (_, lanIpv4Only) = await HostTraitsAsync();
        return new ComposeTarget
        {
            Slug = app.Slug,
            WebService = app.WebService is { Length: > 0 } web ? web : ComposeFiles.PickWeb(raw.Services)?.Service ?? raw.Services.FirstOrDefault()?.Name ?? "",
            Ports = app.Ports.OrderBy(p => p.Id).ToList(),
            DeploymentId = dep.Id,
            Trigger = dep.Trigger,
            ReleaseDir = source.ReleaseDir,
            ReleaseHostDir = releaseHost,
            DataDir = releases.DataDir,
            DataHostDir = dataHost,
            Preview = preview,
            Tag = source.Tag,
            Images = source.Images,
            Pins = source.Pins,
            LanIpv4Only = lanIpv4Only,
            RelativeBinds = RelativeBinds(raw, values),
            Variables = values.Select(v => v.Key).ToHashSet(StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// The bind mounts a Compose file writes with a relative source (./conf, ../data, ${DATA:-./data}), by service and target,
    /// once the app's variables fill them in: Compose makes every path absolute, so this is how a relative one that leaves the
    /// release is told from a host folder written in full.
    /// </summary>
    internal static HashSet<(string Service, string Target)> RelativeBinds(ComposeFileInfo file, IReadOnlyList<(string Key, string Value)> values)
    {
        var known = values.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        var relative = new HashSet<(string, string)>();
        foreach (var service in file.Services)
            foreach (var bind in service.Binds)
            {
                var source = ComposeVariables.Fill(bind.Source, known);
                if (source.StartsWith('/') || source.StartsWith('~') || source.Length > 1 && source[1] == ':') continue;
                relative.Add((service.Name, ComposeVariables.Fill(bind.Target, known)));
            }
        return relative;
    }

    // ---- Kept folders ----

    /// <summary>
    /// The app's kept folders for its writable bind mounts, as the release has them: a file the app has no copy of yet is
    /// copied in; one it has is updated only when it's still as the release that ran before left it (the app didn't change
    /// it), the way git leaves a changed file alone. Links aren't followed: not the repo's, nor ones the app's containers
    /// made in its kept folder (which they write), so Gitberth never writes or reads through one.
    /// </summary>
    private static void Keep(IReadOnlyList<ComposeMount> kept, string dataDir, string? running, DeployLog log)
    {
        foreach (var mount in kept)
        {
            try
            {
                if (ComposeTransform.LinksOut(dataDir, mount.Path) || new FileInfo(mount.Path).LinkTarget is not null)
                {
                    log.Line($"warning: ./{mount.Relative} in the kept folder is a link, so Gitberth leaves it as it is.");
                    continue;
                }
                if (mount.SeedFrom is null)
                {
                    Directory.CreateDirectory(mount.Path);
                    continue;
                }
                var previous = running is null ? null : Path.Combine(running, mount.Relative);
                if (File.Exists(mount.SeedFrom)) SyncFile(mount.SeedFrom, mount.Path, previous);
                else if (Directory.Exists(mount.SeedFrom)) SyncFolder(mount.SeedFrom, mount.Path, previous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Line($"warning: ./{mount.Relative} couldn't be brought up to date in the kept folder: {ex.Message}");
            }
        }
    }

    private static void SyncFolder(string from, string to, string? previous)
    {
        if (new FileInfo(to).LinkTarget is not null) return;
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) continue;
            var there = Path.Combine(to, entry.Name);
            var before = previous is null ? null : Path.Combine(previous, entry.Name);
            if (entry is DirectoryInfo) SyncFolder(entry.FullName, there, before);
            else SyncFile(entry.FullName, there, before);
        }
    }

    // Copied when the kept folder doesn't have it, or has it as the release before left it; otherwise the app's copy stays.
    // A link on either side is left alone: the kept one may be a container's, pointing anywhere in Gitberth's container.
    internal static void SyncFile(string from, string to, string? previous)
    {
        if (new FileInfo(from).LinkTarget is not null || new FileInfo(to).LinkTarget is not null) return;
        if (previous is not null && new FileInfo(previous).LinkTarget is not null) previous = null;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (!File.Exists(to))
        {
            File.Copy(from, to);
            return;
        }
        if (previous is null || !File.Exists(previous) || Same(from, to) || !Same(previous, to)) return;
        File.Copy(from, to, overwrite: true);
    }

    private static bool Same(string a, string b)
    {
        var (x, y) = (new FileInfo(a), new FileInfo(b));
        if (x.Length != y.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    // Files with the app's values in them: only Gitberth (root) reads them.
    private static void OwnerOnly(string path) => ComposeReleases.OwnerOnly(path);

    // ---- Builds ----

    /// <summary>
    /// Builds the services that have a build section, each under the host-wide build gate: with Gitberth's own build runner
    /// when the section asks only for what it does (the app's build-time values and secrets, without the cache when asked,
    /// in the deploy's log), else with docker compose build. Their images are tagged gitberth/&lt;slug&gt;/&lt;service&gt;:&lt;tag&gt;,
    /// and the file that runs no longer builds them.
    /// </summary>
    private async Task BuildServicesAsync(AppDefinition app, Deployment dep, ComposePlan plan, ComposeSource source, List<EnvValue> buildEnv,
        IReadOnlySet<string> gitHubKeys, string workDir, DeployLog log, CancellationToken ct)
    {
        var services = (JsonObject)plan.Model["services"]!;
        var byCompose = new List<ComposeBuild>();
        foreach (var build in plan.Builds)
        {
            var args = await RunnerArgsAsync(app, dep, build, buildEnv, gitHubKeys, workDir, log);
            if (args is null)
            {
                byCompose.Add(build);
                continue;
            }
            await GatedAsync(log, async () =>
            {
                log.Step(dep.NoCache ? $"Building {build.Service} ({build.Tag}) without the build cache" : $"Building {build.Service} ({build.Tag})");
                var (list, env) = args.Value;
                var hosts = await docker.NetworkHostsAsync(CancellationToken.None);
                var reach = list.Contains("--network") ? [] : (List<string>)["--network", "host", .. hosts.SelectMany(h => new[] { "--add-host", $"{h.Host}:{h.Ip}" })];
                var context = list[^1];
                List<string> Command(bool withReach) => [.. list.Take(list.Count - 1), .. withReach ? reach : [], context];
                var result = await docker.RunAsync(Command(true), new ProcessOptions { Environment = env, OnLine = log.Line, MaxCapture = 16 * 1024 }, ct);
                if (!result.Ok && (result.StdOut + result.StdErr).Contains("entitlement network.host", StringComparison.Ordinal))
                    result = await docker.RunAsync(Command(false), new ProcessOptions { Environment = env, OnLine = log.Line, MaxCapture = 16 * 1024 }, ct);
                if (!result.Ok)
                    throw new DeployException(result.ExitCode == -1 ? result.ErrorText : $"The image of {build.Service} didn't build; the log above shows why. The running services weren't touched.");
            }, ct);
            (services[build.Service] as JsonObject)?.Remove("build");
        }
        if (byCompose.Count > 0)
        {
            // Compose builds what Gitberth's runner can't (SSH, build secrets, more platforms), from a copy of the file that still builds them.
            var buildFile = Path.Combine(source.ReleaseDir, $"gitberth-{dep.Id}.build.json");
            await WriteOwnFileAsync(buildFile, plan.Model.ToJsonString(Indented));
            try
            {
                await GatedAsync(log, async () =>
                {
                    log.Step($"Building {string.Join(", ", byCompose.Select(b => b.Service))} with docker compose build");
                    List<string> command = ["build", .. dep.NoCache ? new[] { "--no-cache", "--pull" } : [], .. byCompose.Select(b => b.Service)];
                    var result = await Composer.RunAsync(app.ComposeProject, buildFile, source.ProjectDir, command, log.Line, ct);
                    if (!result.Ok) throw new DeployException("docker compose build failed; the log above shows why. The running services weren't touched.");
                }, ct);
            }
            finally
            {
                DeployPaths.TryDeleteFile(buildFile);
            }
            foreach (var build in byCompose) (services[build.Service] as JsonObject)?.Remove("build");
        }
    }

    private async Task GatedAsync(DeployLog log, Func<Task> build, CancellationToken ct)
    {
        if (_buildGate.CurrentCount == 0) log.Step(WaitLine);
        await _buildGate.WaitAsync(ct);
        try
        {
            await build();
        }
        finally
        {
            _buildGate.Release();
        }
    }

    // What Gitberth's runner can build of a Compose build section; anything else goes to docker compose build.
    private static readonly HashSet<string> RunnerKeys = new(StringComparer.Ordinal)
    {
        "context", "dockerfile", "dockerfile_inline", "args", "target", "labels", "shm_size", "extra_hosts", "network", "additional_contexts",
        "no_cache", "pull", "platforms", "cache_from",
    };

    /// <summary>
    /// docker build's arguments for a service (its context last) and the environment its values travel in; null when the
    /// section asks for what only docker compose build does.
    /// </summary>
    private async Task<(List<string> Args, Dictionary<string, string> Env)?> RunnerArgsAsync(AppDefinition app, Deployment dep, ComposeBuild build,
        List<EnvValue> buildEnv, IReadOnlySet<string> gitHubKeys, string workDir, DeployLog log)
    {
        var b = build.Build;
        if (b.Any(e => !RunnerKeys.Contains(e.Key))) return null;
        if (b["platforms"] is JsonArray { Count: > 1 }) return null;
        var context = Text(b["context"]) ?? ".";
        string dockerfile;
        if (Text(b["dockerfile_inline"]) is { Length: > 0 } inline)
        {
            Directory.CreateDirectory(workDir);
            dockerfile = Path.Combine(workDir, $"{ComposeNames.ImagePart(build.Service)}.Dockerfile");
            await File.WriteAllTextAsync(dockerfile, inline, CancellationToken.None);
        }
        else
        {
            var named = Text(b["dockerfile"]) ?? "Dockerfile";
            dockerfile = Path.IsPathRooted(named) || context.Contains("://", StringComparison.Ordinal) ? named : Path.Combine(context, named);
        }
        var args = new List<string>
        {
            "build", "--progress=plain", "-f", dockerfile, "-t", build.Tag,
            "--label", $"gitberth.app={app.Slug}", "--label", $"{ComposeNames.ServiceLabel}={build.Service}",
        };
        if (dep.CommitSha is { Length: > 0 } sha) args.AddRange(["--label", $"gitberth.commit={sha}"]);
        if (dep.NoCache || b["no_cache"]?.GetValueKind() == JsonValueKind.True) args.Add("--no-cache");
        if (dep.NoCache || b["pull"]?.GetValueKind() == JsonValueKind.True) args.Add("--pull");
        if (Text(b["target"]) is { Length: > 0 } stage) args.AddRange(["--target", stage]);
        if (Text(b["shm_size"]) is { Length: > 0 } shm) args.AddRange(["--shm-size", shm]);
        if (b["platforms"] is JsonArray { Count: 1 } one && Text(one[0]) is { } platform) args.AddRange(["--platform", platform]);
        if (Text(b["network"]) is "none") args.AddRange(["--network", "none"]);
        foreach (var from in b["cache_from"] as JsonArray ?? []) if (Text(from) is { Length: > 0 } cache) args.AddRange(["--cache-from", cache]);
        foreach (var (key, value) in b["labels"] as JsonObject ?? [])
            if (!key.StartsWith("gitberth.", StringComparison.Ordinal)) args.AddRange(["--label", $"{key}={Text(value)}"]);
        foreach (var host in Hosts(b["extra_hosts"])) args.AddRange(["--add-host", host]);
        foreach (var (key, value) in b["additional_contexts"] as JsonObject ?? []) if (Text(value) is { Length: > 0 } path) args.AddRange(["--build-context", $"{key}={path}"]);

        // The app's build-time values (and the repo's GitHub ones), as for any Git app; the file's own args come after, and win.
        // The Dockerfile is read only when it's a plain file (the transform refused one that's a link out of the repo).
        var source = File.Exists(dockerfile) && new FileInfo(dockerfile).LinkTarget is null ? await File.ReadAllTextAsync(dockerfile, CancellationToken.None) : "";
        var values = await BuildValuesAsync(source, filledIn: false, buildEnv, gitHubKeys, new HashSet<string>(), () => BuildSecrets.KeyAsync(dbFactory, protector, CancellationToken.None));
        if (values.SetBeforeFrom.Count > 0) log.Line($"Not passed to {build.Service}'s build, because its Dockerfile sets them before its first FROM: {string.Join(", ", values.SetBeforeFrom)}");
        args.AddRange(values.Args);
        var env = new Dictionary<string, string>(values.Environment, StringComparer.Ordinal);
        foreach (var (key, value) in b["args"] as JsonObject ?? [])
        {
            if (Text(value) is not { } text) continue;
            args.Add("--build-arg");
            if (EnvPassing.CanPassViaEnvironment(key))
            {
                args.Add(key);
                env[key] = text;
            }
            else args.Add($"{key}={text}");
        }
        args.Add(context);
        return (args, env);
    }

    // extra_hosts as Compose resolves them: ["host=ip"], ["host:ip"] or {"host": "ip"}, as docker build's host:ip.
    private static IEnumerable<string> Hosts(JsonNode? node)
    {
        if (node is JsonArray list)
            foreach (var item in list)
                if (Text(item) is { Length: > 0 } entry)
                {
                    var eq = entry.IndexOf('=');
                    yield return eq > 0 ? entry[..eq] + ":" + entry[(eq + 1)..] : entry;
                }
        if (node is JsonObject map)
            foreach (var (host, ip) in map)
                if (Text(ip) is { Length: > 0 } address) yield return $"{host}:{address}";
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n && n.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? n.ToJsonString() : null;

    // ---- Up, checks and rollback ----

    /// <summary>
    /// Brings the project up with the file this deploy runs, under the app's lock, with its domains on the down page
    /// meanwhile; joins the web service to the shared network under the app's name; checks every service. A failure brings
    /// up the release that ran before, or, with none, takes down what this deploy started (its volumes stay).
    /// </summary>
    private async Task<SwapOutcome> ComposeUpAsync(AppDefinition app, Deployment dep, ComposePlan plan, string webService, string file, string projectDir,
        IReadOnlyList<(int Id, DeployStatus Status)> history, ComposeReleases releases, IReadOnlyList<string> unchanged, bool everyShared, DeployLog log,
        CancellationToken ct)
    {
        // Not cancellable once started: a half-finished up is worse than a finished one.
        using var _ = await locks.AcquireAsync(app.Id, CancellationToken.None);
        await using (var fresh = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            if (await fresh.Apps.AsNoTracking().Where(a => a.Id == app.Id).Select(a => (DesiredState?)a.DesiredState).FirstOrDefaultAsync() == DesiredState.Stopped)
            {
                log.Step("The app was stopped while this deploy waited, so it isn't started again.");
                return new SwapOutcome(false, false, "Cancelled: the app was stopped.", Cancelled: true);
            }
        try
        {
            await docker.EnsureNetworkAsync();
        }
        catch (InvalidOperationException ex)
        {
            return new SwapOutcome(false, false, ex.Message);
        }
        var hadOld = await docker.ContainerExistsAsync(app.ContainerName);
        // A service that keeps running has restarted before, maybe: only a restart from now on counts against it.
        var restarts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var service in unchanged)
            if (plan.Members.GetValueOrDefault(service) is { } member && await docker.InspectStateAsync(member) is { } before) restarts[service] = before.RestartCount;
        var downPage = hadOld && webService is { Length: > 0 } && !unchanged.Contains(webService) && await ShowDownPageAsync(app, log);
        try
        {
            log.Step($"Starting {Count(plan.Services.Count, "service")} with docker compose up");
            var pull = dep.NoCache || !app.FromRepo && dep.Trigger is DeployTrigger.Manual or DeployTrigger.Hook ? "always" : "missing";
            var failure = await UpAsync(app.ComposeProject, file, projectDir, pull, app.Slug, plan.Model, everyShared, log)
                ?? await VerifyMembersAsync(app, plan, webService, restarts, log, ct);
            if (failure is null) return new SwapOutcome(true, false, null);

            await ShowMembersOutputAsync(plan, log);
            if (ComposeReleases.PickRollback(history, dep.Id, id => releases.ResolvedOf(id) is not null) is int back && releases.ResolvedOf(back) is { } previous)
            {
                log.Step($"Bringing the release of deploy #{back} back");
                var (backDir, backWeb, backModel) = await RunsAsync(previous);
                // With the images it ran: a pulled one whose tag moved since (this deploy pulled it anew) runs by its pin.
                var ranBack = await RanAsync(back);
                var pins = await PinsAsync(app.Slug, ranBack);
                var backFile = previous;
                if (pins.Count > 0)
                {
                    foreach (var (service, pin) in pins)
                        if (backModel["services"]?[service] is JsonObject s) s["image"] = pin;
                    backFile = Path.Combine(Path.GetDirectoryName(previous)!, $"gitberth-{back}.pinned.json");
                    await WriteOwnFileAsync(backFile, backModel.ToJsonString(Indented));
                    log.Line($"Their tags moved on since then, so these run the images that release ran: {string.Join(", ", pins.Select(p => $"{p.Key} ({p.Value})"))}.");
                }
                var restored = await UpAsync(app.ComposeProject, backFile, backDir, "missing", app.Slug, backModel, everyShared, log);
                if (backWeb != webService) log.Line($"That release's web service is {backWeb}.");
                return restored is null
                    ? new SwapOutcome(false, true, failure)
                    : new SwapOutcome(false, false, $"{failure} Bringing the previous release back failed too: {restored}");
            }
            log.Step("Taking down what this deploy started: there's no earlier release to go back to (its volumes stay)");
            await Composer.RunAsync(app.ComposeProject, file, projectDir, ["down", "--remove-orphans"], log.Line);
            return new SwapOutcome(false, false, failure);
        }
        finally
        {
            if (downPage) await HideDownPageAsync(app, log);
        }
    }

    /// <summary>
    /// Brings the project up in three steps: docker compose up --no-start makes (or remakes) its containers without starting
    /// them, the ones that belong on Gitberth's shared network join it (<see cref="JoinSharedAsync"/>), and only then
    /// docker compose up -d --wait starts them. So a service reaches the app's database server (postgres) and other apps by
    /// name from its very first start: an app that connects to its database as it starts (Miniflux, n8n, Nextcloud) would
    /// otherwise fail before it's joined. Null when every service runs, else why not.
    /// </summary>
    private async Task<string?> UpAsync(string project, string file, string projectDir, string pull, string slug, JsonObject model, bool everyShared,
        DeployLog log)
    {
        var made = await Composer.RunAsync(project, file, projectDir, ["up", "--no-start", "--remove-orphans", "--pull", pull, "--quiet-pull"], log.Line);
        if (!made.Ok) return $"docker compose up didn't make every service: {LastLine(made.ErrorText)}";
        if (await JoinSharedAsync(slug, model, everyShared, log) is { } joinFailed) return joinFailed;
        var wait = WaitFor(model, ComposeWait);
        if (wait > ComposeWait) log.Line($"Its file gives a service up to {Minutes(wait - StartGrace)} to start, so up waits up to {Minutes(wait)}.");
        var seconds = Math.Max(10, (int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var result = await Composer.RunAsync(project, file, projectDir,
            ["up", "-d", "--remove-orphans", "--wait", "--wait-timeout", seconds, "--pull", "missing", "--quiet-pull"], log.Line);
        return result.Ok ? null : $"docker compose up didn't bring every service up: {LastLine(result.ErrorText)}";
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "it failed";

    // A minute more than the longest start a service's health check allows, for its first check to pass after it.
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(1);

    // However long a file asks for, up never waits longer than this.
    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long docker compose up --wait waits for the services: <paramref name="least"/>, or longer when a service's health
    /// check gives it a longer start (healthcheck.start_period: 10m for an app that installs itself on its first start, say),
    /// then a minute for its check to pass; never more than 20 minutes.
    /// </summary>
    internal static TimeSpan WaitFor(JsonObject model, TimeSpan least)
    {
        var longest = TimeSpan.Zero;
        foreach (var (_, service) in model["services"] as JsonObject ?? [])
            if (GoDuration(service?["healthcheck"]?["start_period"]) is { } start && start > longest) longest = start;
        if (longest == TimeSpan.Zero) return least;
        var wait = longest + StartGrace;
        return wait <= least ? least : wait < LongestWait ? wait : LongestWait;
    }

    /// <summary>A duration as docker compose config writes it: "5m0s", "1h30m", "90s", or a number of nanoseconds; null for anything else.</summary>
    internal static TimeSpan? GoDuration(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var nanos)) return TimeSpan.FromTicks(nanos / 100);
        if (!value.TryGetValue<string>(out var text) || text.Length == 0) return null;
        var total = TimeSpan.Zero;
        var at = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(\d+(?:\.\d+)?)(ns|us|µs|ms|s|m|h)"))
        {
            if (m.Index != at) return null;
            at = m.Index + m.Length;
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(n),
                "m" => TimeSpan.FromMinutes(n),
                "s" => TimeSpan.FromSeconds(n),
                "ms" => TimeSpan.FromMilliseconds(n),
                _ => TimeSpan.Zero,
            };
        }
        return at == text.Length ? total : null;
    }

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.Seconds == 0 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";

    /// <summary>
    /// The web service joins the shared network under the app's name, as every app's container does, so the tunnel, health
    /// checks and other apps reach it by that name; with the app's "every service on Gitberth's network", the others join
    /// with no name of their own there, so they reach its database and the other apps; without, one that's there leaves.
    /// Compose isn't asked to: it would give every service's own name there too, and two apps with a service called web
    /// would share it. Every connect and disconnect goes through the docker CLI, announced.
    /// </summary>
    private async Task<string?> JoinSharedAsync(string slug, JsonObject model, bool everyService, DeployLog log)
    {
        var web = model[ComposeTransform.OwnKey]?["web"] is JsonValue w && w.TryGetValue<string>(out var webName) ? webName : "";
        if (model["services"]?[web]?["network_mode"] is JsonValue m && m.TryGetValue<string>(out var mode) && mode.Length > 0)
            log.Line($"The web service uses network mode {mode}, so it isn't on {options.Network}: other apps and the tunnel can't reach it by name.");
        var api = await docker.ServerApiVersionAsync();
        string[] priority = api is not null && api >= DockerCli.GwPriorityApi ? ["--gw-priority", "-1"] : [];
        var joined = new List<string>();
        foreach (var join in ComposeTransform.SharedJoins(model, slug, everyService))
        {
            var on = await OnSharedAsync(join.Member);
            if (join.Join && on != true)
            {
                List<string> args = ["network", "connect", .. join.Alias is { } alias ? new[] { "--alias", alias } : [], .. priority, options.Network, join.Member];
                var r = await docker.RunAsync(args);
                if (!r.Ok && !r.ErrorText.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    if (join.Alias is not null) return $"{join.Member} couldn't join {options.Network}: {r.ErrorText}";
                    log.Line($"warning: {join.Service} couldn't join {options.Network}: {r.ErrorText.Trim()}");
                    continue;
                }
                if (join.Alias is null) joined.Add(join.Service);
            }
            else if (!join.Join && on == true)
            {
                var r = await docker.RunAsync(["network", "disconnect", options.Network, join.Member]);
                if (!r.Ok) log.Line($"warning: {join.Service} couldn't leave {options.Network}: {r.ErrorText.Trim()}");
            }
        }
        if (joined.Count > 0) log.Line($"On {options.Network} too, as the app asks (they reach its database and the other apps by name): {string.Join(", ", joined)}.");
        return null;
    }

    // Whether a container is on Gitberth's shared network; null when it can't be told.
    private async Task<bool?> OnSharedAsync(string member)
    {
        var r = await docker.RunAsync(["container", "inspect", "--format", "{{json .NetworkSettings.Networks}}", member]);
        if (!r.Ok) return null;
        try
        {
            return JsonNode.Parse(r.StdOut) is JsonObject networks && networks.ContainsKey(options.Network);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What a kept file runs: its project folder, its web service, and the file itself.
    private static async Task<(string Dir, string Web, JsonObject Model)> RunsAsync(string file)
    {
        var model = JsonNode.Parse(await File.ReadAllTextAsync(file, CancellationToken.None)) as JsonObject ?? [];
        var own = model[ComposeTransform.OwnKey];
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, own?["dir"]?.GetValue<string>() ?? "."));
        var web = own?["web"]?.GetValue<string>() ?? "";
        return (dir, web, model);
    }

    // What a deploy ran, as it recorded it.
    private async Task<ComposeImages> RanAsync(int deploymentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return ComposeImages.Parse(await db.Deployments.AsNoTracking().Where(d => d.Id == deploymentId).Select(d => d.Images).FirstOrDefaultAsync(CancellationToken.None));
    }

    /// <summary>
    /// Every service has to stay up for <see cref="StableFor"/> (a one-off job that ends well, with a restart policy that
    /// doesn't bring it back, may end), be healthy where its image or file checks its health, and the web service answer
    /// the app's health URL.
    /// </summary>
    private async Task<string?> VerifyMembersAsync(AppDefinition app, ComposePlan plan, string webService, IReadOnlyDictionary<string, int> restartsBefore,
        DeployLog log, CancellationToken ct)
    {
        log.Step($"Checking that {(plan.Services.Count == 1 ? "it stays" : $"its {plan.Services.Count} services stay")} up for {StableFor.TotalSeconds:0} seconds…");
        var services = (JsonObject)plan.Model["services"]!;
        var until = DateTime.UtcNow + StableFor;
        var done = new HashSet<string>(StringComparer.Ordinal);
        bool starting;
        do
        {
            if (ct.IsCancellationRequested) return "Cancelled while checking the new services.";
            await Task.Delay(StatePoll, CancellationToken.None);
            starting = false;
            foreach (var (service, member) in plan.Members)
            {
                if (done.Contains(service)) continue;
                var state = await docker.InspectStateAsync(member);
                if (state is null) return $"The container of {service} disappeared.";
                if (state.OomKilled) return $"{service} ran out of memory. Raise its memory limit in the Compose file.";
                // A one-off job (a migration, a seed) that ended well, and that nothing restarts, has done its part.
                var restart = Text(services[service]?["restart"]) ?? "unless-stopped";
                if (!state.Running && state.ExitCode == 0 && (restart == "no" || restart.StartsWith("on-failure", StringComparison.Ordinal)))
                {
                    done.Add(service);
                    continue;
                }
                if (!state.Running || state.RestartCount > restartsBefore.GetValueOrDefault(service)) return $"{service} exited (code {state.ExitCode}).";
                if (state.Health == "unhealthy") return $"{service}'s health check reports unhealthy.";
                if (state.Health == "starting")
                {
                    if (DateTime.UtcNow > until + HealthTimeout) return $"{service}'s health check never reported healthy.";
                    starting = true;
                }
            }
        }
        while (DateTime.UtcNow < until || starting);
        if (plan.Members.GetValueOrDefault(webService) is not { } web) return null;
        return await HealthPathAsync(app, web, app.Ports.ToList(), log, ct, asLocalhost: true);
    }

    // The last lines of each service that isn't running as it should, for the log.
    private async Task ShowMembersOutputAsync(ComposePlan plan, DeployLog log)
    {
        foreach (var (service, member) in plan.Members)
        {
            var state = await docker.InspectStateAsync(member);
            if (state is { Running: true, RestartCount: 0 } && state.Health != "unhealthy") continue;
            var tail = await docker.LogsTailAsync(member, 30);
            var output = (tail.StdOut + tail.StdErr).TrimEnd();
            if (!tail.Ok || output.Length == 0) continue;
            log.Step($"Last output from {service}:");
            foreach (var line in output.Split('\n')) log.Line("  " + line);
        }
    }

    private async Task<List<(int Id, DeployStatus Status)>> HistoryAsync(int appId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return (await db.Deployments.AsNoTracking().Where(d => d.AppId == appId).Select(d => new { d.Id, d.Status }).ToListAsync(CancellationToken.None))
            .Select(d => (d.Id, d.Status)).ToList();
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    // ---- Pruning ----

    /// <summary>
    /// Keeps the releases (and the images Gitberth built for them) of the last <see cref="ComposeReleases.Kept"/> successful
    /// deploys and the one that runs, for rollback, and the files of a deploy still under way; the rest go. The kept data
    /// folder always stays. Returns the tags it removed.
    /// </summary>
    internal async Task<List<string>> PruneComposeAsync(GitberthDb db, AppDefinition app, int running)
    {
        var removed = new List<string>();
        try
        {
            var history = await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id).Select(d => new { d.Id, d.Status, d.Images }).ToListAsync();
            var keep = ComposeReleases.ToKeep(history.Select(d => (d.Id, d.Status)), running);
            // A deploy queued or running meanwhile keeps its release and its files: the disk cleanup prunes while others deploy.
            keep.UnionWith(history.Where(d => d.Status is DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting).Select(d => d.Id));
            ComposeReleases.For(options, app.Slug).Prune(keep);
            // The kept deploys' built images and the pins of the images they pulled stay; any other tag of the app's own goes.
            var kept = history.Where(d => keep.Contains(d.Id)).Select(d => ComposeImages.Parse(d.Images)).ToList();
            var images = kept.SelectMany(i => i.Refs.Values.Concat(i.PinTags(app.Slug))).ToHashSet(StringComparer.Ordinal);
            foreach (var tag in await docker.ListImageTagsUnderAsync($"gitberth/{app.Slug}/"))
                if (!images.Contains(tag) && (await docker.RemoveImageAsync(tag)).Ok) removed.Add(tag);
            await docker.PruneDanglingImagesAsync(app.Slug);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Release cleanup failed for {App}", app.Slug);
        }
        return removed;
    }
}
