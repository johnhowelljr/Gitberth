using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Apps;

/// <summary>
/// Loads, plans and applies any app's settings, by the dispatch of section 2.2: Gitberth's own data at once (adopting
/// the container first when that needs a row); a Git app's settings saved, its limits applied live and the rest by a
/// deploy without a build; any other app's through the change engine (a docker update, or a recreate with its checks and
/// rollback); a container Compose manages as the YAML for its file; and an app without a container in its snapshot.
/// </summary>
public sealed class AppSettingsService
{
    private readonly IAppSettingsHost _host;
    private readonly PortChanger _changer;
    private readonly ContainerUpdater _updater;
    private readonly IContainerEngine _engine;
    private readonly GitberthOptions _options;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(AppCatalog catalog, AppService apps, Lazy<AppActions> actions, PortChanger changer, ContainerUpdater updater,
        IContainerEngine engine, AppLocks appLocks, DomainService domains, HealthSettings health, RestartSchedules restarts, SecretProtector protector,
        IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, ActivityLog activity, ILogger<AppSettingsService> logger)
        : this(new AppSettingsHost(catalog, apps, actions, appLocks, domains, health, restarts, protector, dbFactory, options, activity), changer, updater, engine, options,
            logger)
    {
    }

    /// <summary>The service over its seam: what it asks of the catalog, the Git apps' records, deploys, domains and Activity.</summary>
    internal AppSettingsService(IAppSettingsHost host, PortChanger changer, ContainerUpdater updater, IContainerEngine engine, GitberthOptions options,
        ILogger<AppSettingsService> logger)
    {
        _host = host;
        _changer = changer;
        _updater = updater;
        _engine = engine;
        _options = options;
        _logger = logger;
    }

    /// <summary>Null when there's no such app. Without a container, the settings are the snapshot's (ApplyKind.Snapshot).</summary>
    public async Task<SettingsView?> LoadAsync(string key, CancellationToken ct = default)
    {
        if (await _host.ViewAsync(key, ct) is not { } view) return null;
        var app = view.Summary;
        if (app.Source == AppSource.Git && view.Row is { } gitRow)
            return new SettingsView(app, app.ContainerId, _host.GitContainer(gitRow), _host.GitAppOptions(gitRow), _host.GitOptions(gitRow),
                new EditAccess(null, null, null, ViaDeploy: true, null, GitAppSettings.ReadOnly), null);
        var options = await _host.OptionsAsync(key, ct);
        if (view.Inspect is { } inspect)
        {
            var image = await ImageAsync(inspect, ct);
            return new SettingsView(app, ContainerPorts.Str(inspect, "Id"), ContainerSettingsJson.Read(inspect, image), options, null,
                await AccessAsync(app.Container, inspect, ct), image is null ? null : ContainerSettingsJson.Facts(image));
        }
        var snapshot = view.Row is { } row ? await _host.SnapshotAsync(row.Id, ct) : null;
        return new SettingsView(app, null, snapshot?.Settings ?? new ContainerSettings(), options, null,
            new EditAccess(null, null, $"There's no container yet: {app.Container} joins its networks when it's created.", false, null, new HashSet<SettingField>()), null);
    }

    /// <summary>What applying the edit would do, with every line, problem and consequence. It changes nothing.</summary>
    public async Task<SettingsPlan> PlanAsync(string key, SettingsEdit edit, CancellationToken ct = default)
    {
        if (await _host.ViewAsync(key, ct) is not { } view) return new SettingsPlan { Key = key, Edit = edit, Blocked = $"There's no app called {key}." };
        var app = view.Summary;
        // A swap limit set before, and not changed with it, follows a new memory limit, so Docker takes the change.
        edit = edit with { Wanted = ContainerUpdater.FollowSwap(edit.Loaded, edit.Wanted) };
        var fields = SettingsDiff.Changed(edit.Loaded, edit.Wanted);
        var secrets = edit.WantedOptions.SecretKeys;
        var own = OptionLines(edit.LoadedOptions, edit.WantedOptions).Concat(GitLines(edit.LoadedGit, edit.WantedGit)).ToList();
        var plan = new SettingsPlan { Key = key, Edit = edit, Lines = own, Consequence = own.Count > 0 ? "Saved at once: nothing restarts." : null };
        if (fields.Count == 0) return plan;
        var context = await ContextAsync(app.Source == AppSource.Git, ct);
        var problems = SettingsRules.Check(edit.Wanted, context).Where(p => p.Field is not { } f || fields.Contains(f)).Concat(LabelProblems(edit, fields)).ToList();

        // A Git app: saved, its limits applied live, the rest by a deploy without a build.
        if (app.Source == AppSource.Git)
        {
            problems.AddRange(fields.Where(GitAppSettings.ReadOnly.Contains).Select(f => new SettingProblem(f,
                f == SettingField.Image ? "A Git app's image is built from its repo." : "A Git app always runs on the shared network.")));
            var lines = SettingsDiff.Lines(edit.Loaded, edit.Wanted, fields, secrets, f => SettingFields.Live.Contains(f) ? ApplyKind.Live : ApplyKind.Deploy);
            var recreate = fields.Any(f => !SettingFields.Live.Contains(f));
            return plan with
            {
                Lines = [.. lines, .. own],
                Problems = problems,
                Consequence = !recreate ? "No restart."
                    : !app.Running ? "Saved. It applies with the next deploy."
                    : view.Row is { } busy && await _host.DeployActiveAsync(busy.Id, ct) ? UnderWay
                    : view.Row is { } row && await _host.UnnoticedAsync(row, app.Running, ct) ? "Applies with a deploy without a build: no downtime for its domains."
                    : "Applies with a deploy without a build: a few seconds offline.",
            };
        }

        // No container: the snapshot changes, and applies when it's created.
        if (view.Inspect is not { } inspect)
        {
            if (view.Row is null) return plan with { Blocked = $"There's no container called {app.Container}." };
            return plan with
            {
                Lines = [.. SettingsDiff.Lines(edit.Loaded, edit.Wanted, fields, secrets, _ => ApplyKind.Snapshot), .. own],
                Problems = problems,
                Consequence = $"Saved. It applies when {app.Title} is created.",
            };
        }

        var verdict = RecreateRules.Classify(app.Container, inspect, await RecreateContextAsync(app.Container, ct), ChangeScope.Settings);
        var compose = verdict.Compose ?? (verdict.Kind == RecreateKind.Compose ? ComposeService.Of(inspect) : null);
        var live = fields.All(SettingFields.Live.Contains)
            && ContainerUpdater.CanApplyLive(ContainerSettingsJson.Read(inspect, null), SettingsDiff.Merge(ContainerSettingsJson.Read(inspect, null), edit.Wanted, fields),
                fields, inspect["HostConfig"] as JsonObject);

        // Gitberth itself: its title and stats only; anything else is a change to its Compose file.
        if (verdict.Kind == RecreateKind.Self)
            return plan with
            {
                Lines = [.. SettingsDiff.Lines(edit.Loaded, edit.Wanted, fields, secrets, _ => ApplyKind.Compose), .. own],
                ComposeSnippet = compose is null ? null : ComposeSnippet.For(compose, edit.Wanted, fields, secrets),
                ComposeCommand = compose?.Command,
                Blocked = compose is null ? verdict.Reason : null,
                Consequence = $"Gitberth's own container: change it in {compose?.File ?? "its Compose file"}, then run the command.",
            };
        // Swarm, Kubernetes, Gitberth's helpers and a deploy's temporaries: nothing of theirs changes here.
        if (verdict.Kind is RecreateKind.Swarm or RecreateKind.Kubernetes or RecreateKind.Helper or RecreateKind.DeployTemporary)
            return plan with { Lines = [.. SettingsDiff.Lines(edit.Loaded, edit.Wanted, fields, secrets, _ => ApplyKind.Recreate), .. own], Blocked = verdict.Reason };
        // Compose: limits live (Compose sets them again when it recreates the container); anything else is YAML.
        if (verdict.Kind == RecreateKind.Compose && !live && compose is not null)
            return plan with
            {
                Lines = [.. SettingsDiff.Lines(edit.Loaded, edit.Wanted, fields, secrets, _ => ApplyKind.Compose), .. own],
                Problems = problems,
                ComposeSnippet = ComposeSnippet.For(compose, edit.Wanted, fields, secrets),
                ComposeCommand = compose.Command,
                Consequence = $"Change it in Compose: put this in {compose.File}, then run the command.",
            };

        // Any other app: the engine's plan, a docker update or a recreate.
        var change = await _changer.PlanAsync(app.Container, new ContainerEditRequest
        {
            ContainerId = edit.ContainerId ?? "",
            Loaded = edit.Loaded,
            Wanted = edit.Wanted,
            // The Network tab's port change is journaled as one.
            Kind = fields.Count == 1 && fields.Contains(SettingField.Ports) ? ChangeKind.Ports : ChangeKind.Settings,
            HealthPath = edit.WantedOptions.HealthCheckPath,
            WebPort = edit.WantedOptions.WebPort,
            SecretKeys = secrets,
            // The dialog's lines name it as the page does (Adminer), not by its container (gb-adminer).
            Title = app.Title,
        }, ct);
        var engineLines = change.Changes.Select(c => new SettingLine(SettingField.Ports, c.Port, c.Before, c.After, ApplyKind.Recreate)).Concat(change.Settings);
        return plan with
        {
            Lines = [.. engineLines, .. own],
            Change = change,
            Problems = problems,
            Blocked = problems.Count > 0 ? null : change.Blocked,
            Consequence = change.LiveOnly ? "No restart."
                : change.Running ? change.Impact.FirstOrDefault() ?? "It restarts once."
                : $"{app.Title} is stopped: it's recreated stopped, so nothing goes offline.",
        };
    }

    /// <summary>
    /// Does it: Gitberth fields saved; Git apps saved (and deployed with Now); a recreate or live change started through
    /// PortChanger.Start (the job is returned, and once it's Done or WaitsForStart the row is synced and the domains too).
    /// </summary>
    public async Task<SettingsOutcome> ApplyAsync(SettingsPlan plan, ApplyTiming timing = ApplyTiming.Now, CancellationToken ct = default)
    {
        if (plan.Problems.Count > 0) return new SettingsOutcome(false, plan.Problems[0].Message, Problems: plan.Problems);
        if (plan.Blocked is { } blocked) return new SettingsOutcome(false, blocked);
        if (plan.ComposeSnippet is not null) return new SettingsOutcome(false, "Compose manages this container: change it in its file, then run the command.");
        if (plan.Lines.Count == 0) return new SettingsOutcome(false, "There's nothing to apply.");
        if (await _host.ViewAsync(plan.Key, ct) is not { } view) return new SettingsOutcome(false, $"There's no app called {plan.Key}.");
        var app = view.Summary;
        var edit = plan.Edit;
        var fields = SettingsDiff.Changed(edit.Loaded, edit.Wanted);

        // Gitberth's own data first: it adopts the container when it needs a row.
        var optionsChanged = OptionLines(edit.LoadedOptions, edit.WantedOptions).Any();
        if (optionsChanged) await _host.SaveOptionsAsync(plan.Key, edit.WantedOptions, ct);

        if (app.Source == AppSource.Git && view.Row is { } gitRow) return await ApplyGitAsync(app, gitRow, edit, fields, timing, ct);
        if (fields.Count == 0) return new SettingsOutcome(true, "Saved.");

        if (view.Inspect is null)
        {
            if (view.Row is not { } row) return new SettingsOutcome(false, $"There's no container called {app.Container}.");
            var snapshot = await _host.SnapshotAsync(row.Id, ct);
            var settings = SettingsDiff.Merge(snapshot?.Settings ?? edit.Loaded, edit.Wanted, fields);
            await _host.SaveSnapshotAsync(row.Id, new AppSnapshot(settings, snapshot?.Networks ?? []), ct);
            await ActivityAsync(app.Container, row.Id, ActivityKind.Settings, $"{Capital(SettingsDiff.Summary(edit.Loaded, edit.Wanted, fields))}; it applies when it's created.",
                ActivityTone.Neutral);
            return new SettingsOutcome(true, $"Saved. It applies when {app.Title} is created.");
        }

        if (plan.Change is not { } change) return new SettingsOutcome(false, "There's no change to make.");
        // A settings change of any kind adopts the container first, so its row keeps the tag before a recreate pins a digest.
        var appId = view.Row?.Id ?? (await _host.AdoptAsync(app.Container, ct)).Id;
        var job = _changer.Start(change);
        _ = FollowAsync(job, app.Container, appId);
        return new SettingsOutcome(true, change.LiveOnly ? "Updating its limits." : $"Recreating {app.Title}.", job);
    }

    /// <summary>The Network tab's ports, for any app: PlanAsync with only Ports changed, the web marker kept as Gitberth data.</summary>
    public async Task<SettingsPlan> PlanPortsAsync(string key, string? containerId, IReadOnlyList<PortRow> rows, string? loaded, CancellationToken ct = default)
    {
        var view = await _host.ViewAsync(key, ct);
        var settings = view?.Inspect is { } inspect ? ContainerSettingsJson.Read(inspect, null)
            : view?.Row is { } row ? (await _host.SnapshotAsync(row.Id, ct))?.Settings ?? new ContainerSettings()
            : new ContainerSettings();
        var rules = rows.Where(r => r.ContainerPort is not null)
            .Select(r => new PortRule(r.ContainerPort!.Value, PortRules.ProtocolOf(r.Protocol), r.Access, r.Access == PortAccess.Internal ? null : r.HostPort, r.HostIp))
            .ToList();
        var web = rows.FirstOrDefault(r => r.IsWeb && r.ContainerPort is not null && PortRules.ProtocolOf(r.Protocol) == "tcp")?.ContainerPort;
        var options = view is null ? new AppOptions() : await _host.OptionsAsync(key, ct);
        var edit = new SettingsEdit(containerId, settings, settings with { Ports = rules }, options, options with { WebPort = web });
        if (loaded is not null && view?.Row is { } saved && AppSettingsModel.PortsKey(saved.Ports) != loaded)
            return new SettingsPlan { Key = key, Edit = edit, Blocked = $"{view.Summary.Container}'s ports changed since you started editing." };
        return await PlanAsync(key, edit, ct);
    }

    // A Git app's change that needs a new container, saved while one of its deploys is queued or running: that deploy read
    // the app before the save, so the change stays pending until the next one.
    private const string UnderWay = "Saved for the next deploy: a deploy is already under way.";

    // A Git app: the database first (its deploys use it), then its limits live when it runs and no deploy is under way.
    private async Task<SettingsOutcome> ApplyGitAsync(AppSummary app, AppDefinition row, SettingsEdit edit, IReadOnlySet<SettingField> fields, ApplyTiming timing,
        CancellationToken ct)
    {
        var saved = await _host.SaveGitAsync(row.Id, edit, ct);
        if (saved.Problems.Count > 0) return new SettingsOutcome(false, saved.Problems[0].Message, Problems: saved.Problems);
        var messages = new List<string>();
        LiveResult? live = null;
        if (saved.LiveChanged.Count > 0)
        {
            if (app.Running && !await _host.DeployActiveAsync(row.Id, ct))
                using (await _host.LockAppAsync(row.Id, ct))
                    live = await _updater.ApplyToAsync(app.Container, edit.Wanted, saved.LiveChanged, ct);
            if (live is { Ok: true }) messages.Add(live.Message);
            else
            {
                // A deploy recreates it with the same values in any case: until then the change waits, marked pending.
                await _host.MarkPendingAsync(row.Id, ct);
                messages.Add(live is null ? "Saved. It applies with the next deploy." : $"Saved. {live.Message} It applies with the next deploy.");
            }
        }
        int? deployment = null;
        if (saved.RecreateNeeded)
        {
            // A deploy already under way read the app before this save: it stays pending (the deploy says so when it ends),
            // and the next deploy applies it.
            if (timing == ApplyTiming.Now && await _host.DeployActiveAsync(row.Id, ct))
                messages.Add(UnderWay);
            else if (timing == ApplyTiming.Now && (deployment = await _host.ApplyIfRunningAsync(row.Id, ct)) is not null)
                messages.Add("Saved. A deploy without a build applies it.");
            else messages.Add("Saved. It applies with the next deploy.");
        }
        if (saved.RebuildNeeded) messages.Add("Saved. The next deploy builds it with the change.");
        if (messages.Count == 0) messages.Add("Saved.");
        // What a deploy will apply is this save's record; what was changed live is ContainerUpdater's, written once.
        var queued = fields.Where(f => live is not { Ok: true } || !saved.LiveChanged.Contains(f)).ToHashSet();
        if (queued.Count > 0)
            await ActivityAsync(app.Container, row.Id, ActivityKind.Settings,
                $"{Capital(SettingsDiff.Summary(edit.Loaded, edit.Wanted, queued))}: saved for {(deployment is not null ? "a deploy without a build" : "the next deploy")}.",
                ActivityTone.Neutral);
        return new SettingsOutcome(true, string.Join(' ', messages.Distinct()), DeploymentId: deployment);
    }

    // Once the change is over, however it ended: the row follows the container that holds the name (the new one, or the
    // original put back), and after one that worked, the domains its new ports. Its record in Activity is the engine's (the
    // journal row of a recreate, ContainerUpdater's entry for a live change), so none is written here.
    private async Task FollowAsync(PortChangeJob job, string container, int appId)
    {
        try
        {
            var result = await job.Completion;
            var plan = job.Plan;
            await _host.SyncAsync(appId, CancellationToken.None);
            if (result.Ok && !plan.LiveOnly) await _host.SyncDomainsAsync(appId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Bringing {Container}'s record in line after its change failed", container);
        }
    }

    // What the form may change, and why not: the change engine's rules, read without the rest of the host.
    private async Task<EditAccess> AccessAsync(string name, JsonObject inspect, CancellationToken ct)
    {
        var context = await RecreateContextAsync(name, ct);
        var verdict = RecreateRules.Classify(name, inspect, context, ChangeScope.Settings);
        var live = RecreateRules.LiveRefusal(name, inspect, context);
        var mode = ContainerPorts.Str(inspect["HostConfig"], "NetworkMode");
        var readOnly = new HashSet<SettingField>();
        if (ContainerSettingsJson.IsOwnMode(mode)) readOnly.Add(SettingField.Ports);
        if (verdict.Kind == RecreateKind.Self) readOnly.UnionWith(Enum.GetValues<SettingField>());
        var networks = verdict.Kind is RecreateKind.Swarm or RecreateKind.Kubernetes or RecreateKind.Helper or RecreateKind.DeployTemporary ? verdict.Reason
            : ContainerSettingsJson.IsOwnMode(mode) ? $"It uses network mode {mode}: it has no networks of its own." : null;
        return new EditAccess(verdict.Allowed || verdict.Kind == RecreateKind.Compose ? null : verdict.Reason, live, networks, false,
            verdict.Kind == RecreateKind.Compose ? verdict.Compose : null, readOnly);
    }

    private async Task<RecreateContext> RecreateContextAsync(string name, CancellationToken ct) =>
        new(await _engine.VersionAsync(ct), _options.OwnContainer, [], await _engine.ListNetworksAsync(ct), "",
            await _changer.WhyNotChangeAsync(name, ct));

    private async Task<SettingsContext> ContextAsync(bool gitApp, CancellationToken ct)
    {
        var host = await Try<EngineHost>(() => _engine.HostAsync(ct));
        var networks = await Try<JsonArray>(async () => await _engine.ListNetworksAsync(ct));
        return new SettingsContext(host?.Cpus ?? 0, host?.MemoryBytes ?? 0, networks is null ? new HashSet<string>() : CloneSpec.NetworkNames(networks), gitApp);
    }

    private async Task<T?> Try<T>(Func<Task<T?>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Couldn't read the host's facts for a settings plan");
            return null;
        }
    }

    private async Task<JsonObject?> ImageAsync(JsonObject inspect, CancellationToken ct)
    {
        var id = ContainerPorts.Str(inspect, "Image");
        return id.Length == 0 ? null : await Try<JsonObject>(() => _engine.InspectImageAsync(id, ct));
    }

    // gitberth.* and com.docker.compose.* are Gitberth's and Compose's: shown, never changed here.
    private static IEnumerable<SettingProblem> LabelProblems(SettingsEdit edit, IReadOnlySet<SettingField> fields)
    {
        if (!fields.Contains(SettingField.Labels)) yield break;
        static IEnumerable<KeyValuePair<string, string>> Reserved(IReadOnlyDictionary<string, string> labels) =>
            labels.Where(l => l.Key.StartsWith("gitberth.", StringComparison.Ordinal) || l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal))
                .OrderBy(l => l.Key, StringComparer.Ordinal);
        if (!Reserved(edit.Loaded.Labels).SequenceEqual(Reserved(edit.Wanted.Labels)))
            yield return new SettingProblem(SettingField.Labels, "The gitberth.* and com.docker.compose.* labels are Gitberth's and Compose's: they can't be changed here.");
    }

    // The CHANGE lines of Gitberth's own data, applied at once.
    internal static IEnumerable<SettingLine> OptionLines(AppOptions before, AppOptions after)
    {
        SettingLine Line(string label, string from, string to) => new(null, label, from, to, ApplyKind.Gitberth);
        if (before.Name != after.Name) yield return Line("Name", before.Name, after.Name);
        if (before.WebPort != after.WebPort) yield return Line("Web port", before.WebPort?.ToString() ?? "none", after.WebPort?.ToString() ?? "none");
        if (before.HealthCheckPath != after.HealthCheckPath) yield return Line("Health URL", before.HealthCheckPath ?? "none", after.HealthCheckPath ?? "none");
        if (before.GatherStats != after.GatherStats) yield return Line("Gather stats", before.GatherStats ? "on" : "off", after.GatherStats ? "on" : "off");
        if (before.ScheduledRestart != after.ScheduledRestart)
            yield return Line("Scheduled restart", before.ScheduledRestart?.ToString() ?? "never", after.ScheduledRestart?.ToString() ?? "never");
        if (before.Updates != after.Updates) yield return Line("Updates", PolicyWords(before.Updates), PolicyWords(after.Updates));
        if (before.TrackedImage != after.TrackedImage) yield return Line("Tracked image", before.TrackedImage ?? "its image", after.TrackedImage ?? "its image");
        if (before.RegistryId != after.RegistryId) yield return Line("Registry sign-in", before.RegistryId is null ? "none" : "set", after.RegistryId is null ? "none" : "set");
        if (!before.SecretKeys.Order(StringComparer.Ordinal).SequenceEqual(after.SecretKeys.Order(StringComparer.Ordinal)))
            yield return Line("Secret variables", Names(before.SecretKeys), Names(after.SecretKeys));
        if (before.Pinned != after.Pinned) yield return Line("Pinned", before.Pinned ? "yes" : "no", after.Pinned ? "yes" : "no");
    }

    internal static IEnumerable<SettingLine> GitLines(GitOptions? before, GitOptions? after)
    {
        if (before is null || after is null || before == after) yield break;
        SettingLine Line(string label, string from, string to) => new(null, label, from, to, ApplyKind.Rebuild);
        if (before.Branch != after.Branch) yield return Line("Branch", before.Branch, after.Branch);
        if (before.Subdirectory != after.Subdirectory) yield return Line("Subfolder", before.Subdirectory ?? "the repo's root", after.Subdirectory ?? "the repo's root");
        // Each named as the Settings tab's field is.
        if (before.DockerfileMode != after.DockerfileMode) yield return Line("Build", BuildWords(before.DockerfileMode), BuildWords(after.DockerfileMode));
        if (before.CustomDockerfile != after.CustomDockerfile) yield return Line("Dockerfile", before.CustomDockerfile is null ? "none" : "set", after.CustomDockerfile is null ? "none" : "changed");
        if (before.ProjectHint != after.ProjectHint) yield return Line("Project to run", before.ProjectHint ?? "detected", after.ProjectHint ?? "detected");
        if (before.AutoDeploy != after.AutoDeploy) yield return new SettingLine(null, "Deploy on push", before.AutoDeploy ? "on" : "off", after.AutoDeploy ? "on" : "off", ApplyKind.Gitberth);
        if (before.VercelVariables != after.VercelVariables) yield return Line("Vercel's variables", before.VercelVariables ? "on" : "off", after.VercelVariables ? "on" : "off");
    }

    private static string BuildWords(DockerfileMode mode) => mode == DockerfileMode.Custom ? "Custom Dockerfile" : "Detect automatically";

    // An update policy in the words the app's Settings tab gives it.
    private static string PolicyWords(UpdatePolicy policy) => policy switch
    {
        UpdatePolicy.Notify => "Tell me",
        UpdatePolicy.Auto => "Automatically",
        _ => "When I ask",
    };

    private static string Names(IReadOnlyList<string> keys) => keys.Count == 0 ? "Gitberth's guess" : RecreateRules.Join(keys.Order(StringComparer.Ordinal).ToList());

    private async Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone)
    {
        try
        {
            await _host.ActivityAsync(container, appId, kind, text, tone, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Couldn't write {Container}'s Activity entry", container);
        }
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>What <see cref="AppSettingsService"/> asks of the rest of Gitberth: one seam, so its dispatch can be tested on its own.</summary>
internal interface IAppSettingsHost
{
    Task<AppView?> ViewAsync(string key, CancellationToken ct);
    Task<AppDefinition> AdoptAsync(string container, CancellationToken ct);
    Task SyncAsync(int appId, CancellationToken ct);
    Task<AppSnapshot?> SnapshotAsync(int appId, CancellationToken ct);
    Task SaveSnapshotAsync(int appId, AppSnapshot snapshot, CancellationToken ct);
    Task<AppOptions> OptionsAsync(string key, CancellationToken ct);
    Task<AppOptions> SaveOptionsAsync(string key, AppOptions options, CancellationToken ct);
    ContainerSettings GitContainer(AppDefinition app);
    GitOptions GitOptions(AppDefinition app);
    AppOptions GitAppOptions(AppDefinition app);
    Task<GitSaved> SaveGitAsync(int appId, SettingsEdit edit, CancellationToken ct);
    Task MarkPendingAsync(int appId, CancellationToken ct);
    /// <summary>A deploy without a build when the app runs; the deploy's ID, or null when none started.</summary>
    Task<int?> ApplyIfRunningAsync(int appId, CancellationToken ct);
    /// <summary>A deploy of the app is queued or running.</summary>
    Task<bool> DeployActiveAsync(int appId, CancellationToken ct);
    Task<IDisposable> LockAppAsync(int appId, CancellationToken ct);
    /// <summary>A deploy can swap the app's container without its domains' visitors noticing (BridgeRules).</summary>
    Task<bool> UnnoticedAsync(AppDefinition app, bool running, CancellationToken ct);
    /// <summary>The tunnel brought in line, when the app has domains and they're on; null when there was nothing to do.</summary>
    Task<SyncReport?> SyncDomainsAsync(int appId, CancellationToken ct);
    Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct);
}

/// <summary>The seam over Gitberth's services: the catalog (A), the Git apps' records and deploys, domains and Activity.</summary>
internal sealed class AppSettingsHost(AppCatalog catalog, AppService apps, Lazy<AppActions> actions, AppLocks appLocks, DomainService domains, HealthSettings health,
    RestartSchedules restarts, SecretProtector protector, IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, ActivityLog activity) : IAppSettingsHost
{
    public Task<AppView?> ViewAsync(string key, CancellationToken ct) => catalog.GetAsync(key, options.PublicHost ?? "", ct);

    public Task<AppDefinition> AdoptAsync(string container, CancellationToken ct) => catalog.AdoptAsync(container, ct);

    public Task SyncAsync(int appId, CancellationToken ct) => catalog.SyncAsync(appId, ct);

    public Task<AppSnapshot?> SnapshotAsync(int appId, CancellationToken ct) => catalog.SnapshotAsync(appId, ct);

    public Task SaveSnapshotAsync(int appId, AppSnapshot snapshot, CancellationToken ct) => catalog.SaveSnapshotAsync(appId, snapshot, ct);

    public Task<AppOptions> OptionsAsync(string key, CancellationToken ct) => catalog.OptionsAsync(key, ct);

    public Task<AppOptions> SaveOptionsAsync(string key, AppOptions saved, CancellationToken ct) => catalog.SaveOptionsAsync(key, saved, ct);

    public ContainerSettings GitContainer(AppDefinition app) => GitAppSettings.ToContainer(app, protector);

    public GitOptions GitOptions(AppDefinition app) => GitAppSettings.ToGit(app);

    public AppOptions GitAppOptions(AppDefinition app) => GitAppSettings.ToOptions(app, health, restarts);

    public Task<GitSaved> SaveGitAsync(int appId, SettingsEdit edit, CancellationToken ct) => apps.SaveSettingsAsync(appId, edit, ct);

    public Task MarkPendingAsync(int appId, CancellationToken ct) => apps.MarkPendingAsync(appId, ct);

    public Task<int?> ApplyIfRunningAsync(int appId, CancellationToken ct) => actions.Value.ApplyIfRunningAsync(appId, ct);

    public async Task<bool> DeployActiveAsync(int appId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().AnyAsync(d => d.AppId == appId
            && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting), ct);
    }

    public Task<IDisposable> LockAppAsync(int appId, CancellationToken ct) => appLocks.AcquireAsync(appId, ct);

    public async Task<bool> UnnoticedAsync(AppDefinition app, bool running, CancellationToken ct)
    {
        var on = await domains.EnabledAsync(ct) && await domains.TokenSavedAsync(ct);
        var web = PortRules.WebOf(app.Ports);
        var tunnelOnHost = on && (await domains.FindTunnelAsync(ct)).Tunnel?.HostNetwork == true;
        return BridgeRules.WhyNot(new BridgeFacts(on, app.Domains.Count > 0, running, app.Volumes.Count > 0, web is not null, tunnelOnHost,
            web?.Access == PortAccess.Internal)) is null;
    }

    public async Task<SyncReport?> SyncDomainsAsync(int appId, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            if (!await db.Domains.AsNoTracking().AnyAsync(d => d.AppId == appId, ct)) return null;
        return await domains.EnabledAsync(ct) ? await domains.SyncAsync(ct) : null;
    }

    public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct) =>
        activity.AddAsync(container, appId, kind, text, tone, ct);
}
