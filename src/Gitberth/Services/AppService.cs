using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>Reads and writes app definitions and validates what the user entered.</summary>
/// <param name="hostScanner">Detects the host's LAN address; without it (in tests) the browser's host name stands in.</param>
/// <param name="hostPorts">Who holds each host port; without it (in tests) no host port counts as taken.</param>
/// <param name="network">The names already taken on the shared network; without it (in tests) only apps' and routes' names are.</param>
public sealed partial class AppService(
    IDbContextFactory<GitberthDb> dbFactory,
    SecretProtector protector,
    DockerCli docker,
    SettingsService settings,
    HealthSettings health,
    DomainService domains,
    GitberthOptions options,
    RestartSchedules restarts,
    WatchState watch,
    HostScanner? hostScanner = null,
    HostPortMap? hostPorts = null,
    NetworkService? network = null,
    Notifications.NotificationSettings? notify = null)
{
    public static readonly string[] RestartPolicies = ["unless-stopped", "always", "on-failure", "no"];

    /// <summary>Gitberth's own container's name, which no app may answer to on the shared network.</summary>
    private const string GitberthName = "gitberth";

    public async Task<AppDefinition?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking()
            .Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Domains).Include(a => a.Networks).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<List<NavApp>> ListNavAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Apps.AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Id, a.Name, a.Slug, a.Source }).ToListAsync(ct);
        return rows.Select(a => new NavApp(a.Id, a.Name, a.Slug, a.Source == AppSource.Git)).ToList();
    }

    /// <summary>
    /// The apps whose containers carry their slug in the gitberth.app label (gb-&lt;slug&gt;, from any source), for telling
    /// containers apart by it. An Existing app's container goes by its own name instead.
    /// </summary>
    internal async Task<List<NavApp>> ListLabelledAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Where(a => a.Container == null).OrderBy(a => a.Name).Select(a => new NavApp(a.Id, a.Name, a.Slug)).ToListAsync(ct);
    }

    /// <summary>Why a name can't be an app's, or null when it can.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the app a name."
        : Slugify(name).Length == 0 ? "The name needs at least one letter or digit."
        : null;

    /// <summary>
    /// Gives an app another name, as its Settings do. Only what Gitberth shows changes: its container, network alias and
    /// volumes keep the names they got when it was created, so nothing is redeployed.
    /// </summary>
    public async Task RenameAsync(int appId, string name, CancellationToken ct = default)
    {
        if (NameProblem(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct) ?? throw new InvalidOperationException("That app no longer exists.");
        app.Name = name.Trim();
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AppDetail?> GetDetailAsync(int id, CancellationToken ct = default)
    {
        var app = await GetAsync(id, ct);
        if (app is null) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployments = await db.Deployments.AsNoTracking().Where(d => d.AppId == id).OrderByDescending(d => d.Id).Take(50).ToListAsync(ct);
        // An Existing app's container isn't labelled as Gitberth's.
        var (containers, dockerError) = await docker.ListContainersAsync(managedOnly: app.Source != AppSource.Existing, ct);
        var container = containers.FirstOrDefault(c => c.Name == app.ContainerName);
        var status = AppStatusResolver.Resolve(app, container, deployments.FirstOrDefault(d => d.IsActive), dockerError is null);
        return new AppDetail(app, status, deployments, container, dockerError);
    }

    public async Task<Deployment?> GetDeploymentAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public AppSettingsModel ToModel(AppDefinition app)
    {
        var restart = restarts.For(app.ContainerName);
        var (every, amount) = RestartChoices.From(restart);
        var model = Model(app);
        model.RestartEvery = every;
        model.RestartAmount = amount;
        model.RestartLoaded = restart?.TotalMinutes;
        return model;
    }

    private AppSettingsModel Model(AppDefinition app) => new()
    {
        Name = app.Name,
        Branch = app.Branch,
        Subdirectory = app.Subdirectory,
        DockerfileMode = app.DockerfileMode,
        CustomDockerfile = app.CustomDockerfile,
        ProjectHint = app.ProjectHint,
        RestartPolicy = app.RestartPolicy,
        MemoryLimitMb = app.MemoryLimitMb,
        CpuLimit = app.CpuLimit,
        HealthCheckPath = app.HealthCheckPath,
        AutoDeploy = app.AutoDeploy,
        VercelVariables = app.VercelVariables,
        GatherStats = health.IsGathered(app.ContainerName),
        GatherStatsLoaded = health.IsGathered(app.ContainerName),
        Ports = app.Ports.OrderBy(p => p.Id).Select(PortRow.From).ToList(),
        PortsLoaded = AppSettingsModel.PortsKey(app.Ports),
        // This form has no scopes: it edits the variables every deploy gets (Apply leaves the scoped ones as they are).
        Env = app.EnvVars.Where(v => v.Scope == EnvScope.All).OrderBy(v => v.Id)
            .Select(v => new EnvRow { Key = v.Key, Value = protector.TryUnprotect(v.ProtectedValue), IsSecret = v.IsSecret, BuildTime = v.BuildTime }).ToList(),
        Volumes = app.Volumes.OrderBy(v => v.Id)
            .Select(v => new VolumeRow { Source = v.Source, Target = v.Target, ReadOnly = v.ReadOnly }).ToList(),
    };

    /// <summary>
    /// What's wrong with the settings, in the words the form shows. A new app's ports are checked too (its ports come with
    /// its settings); an existing app's are saved on their own, and checked there (<see cref="SavePortsAsync"/>).
    /// </summary>
    /// <param name="appId">The app; null for a new one.</param>
    public async Task<List<string>> ValidateAsync(int? appId, AppSettingsModel m, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (NameProblem(m.Name) is { } nameProblem) errors.Add(nameProblem);
        if (string.IsNullOrWhiteSpace(m.Branch)) errors.Add("Pick a branch.");
        if (m.Subdirectory is { Length: > 0 } sub && (sub.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(sub) || sub.StartsWith('/')))
            errors.Add("The subfolder must be a relative path inside the repo, like src/api.");
        if (!RestartPolicies.Contains(m.RestartPolicy)) errors.Add("Pick a restart policy.");
        if (m.HealthCheckPath is { Length: > 0 } health && !health.StartsWith('/')) errors.Add("The health URL must start with /, like /health.");
        if (m.MemoryLimitMb is < 6) errors.Add("The memory limit must be at least 6 MiB.");
        if (m.CpuLimit is <= 0) errors.Add("The CPU limit must be more than 0.");
        if (m.DockerfileMode == DockerfileMode.Custom && string.IsNullOrWhiteSpace(m.CustomDockerfile)) errors.Add("The custom Dockerfile is empty.");
        if (m.RestartInterval is { } restart && RestartInterval.Problem(restart.Amount, restart.Unit) is { } restartProblem)
            errors.Add("Scheduled restart: " + restartProblem);
        if (appId is null)
            foreach (var problem in await ValidatePortsAsync(null, m.Ports, ct: ct))
                if (!errors.Contains(problem.Message)) errors.Add(problem.Message);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in m.Env.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
        {
            var key = e.Key.Trim();
            if (!EnvRules.IsValidKey(key)) errors.Add($"'{key}' isn't a valid variable name (letters, digits and _; it can't start with a digit).");
            else if (!keys.Add(key)) errors.Add($"The variable {key} is listed twice.");
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in m.Volumes.Where(v => !string.IsNullOrWhiteSpace(v.Source) || !string.IsNullOrWhiteSpace(v.Target)))
        {
            var source = v.Source.Trim();
            var target = v.Target.Trim();
            if (source.Contains(':') || target.Contains(':')) errors.Add("Volume paths can't contain ':'.");
            if (!target.StartsWith('/')) errors.Add($"The volume target '{target}' must be an absolute path in the container, like /app/data.");
            else if (!targets.Add(target)) errors.Add($"Two volumes are mounted at {target}.");
            if (!VolumeNames.IsBind(source) && !VolumeName().IsMatch(source))
                errors.Add($"'{source}' isn't a valid volume. Use a name made of letters, digits, - and _, or an absolute host path like /srv/myapp.");
        }
        // An app's named volumes, by the names they'd get, here and in its previews: none may be another app's (VolumeOwners).
        if (appId is int id)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } app)
                errors.AddRange((await VolumeOwners.LoadAsync(db, ct)).Problems(app, m.Volumes
                    .Where(v => !string.IsNullOrWhiteSpace(v.Source) && !string.IsNullOrWhiteSpace(v.Target)).Select(v => new VolumeMount { Source = v.Source })));
        }
        return errors;
    }

    /// <summary>
    /// What's wrong with an app's ports, each under the field it concerns (5.9); empty when they can be saved. A host port
    /// is checked against everything that holds one (<see cref="HostPortMap"/>), the app's own ports left out, and a web
    /// port that's Internal against how cloudflared reaches the app's domains.
    /// </summary>
    /// <param name="appId">The app; null for a new one, which has no domains or ports of its own yet.</param>
    /// <param name="loaded">
    /// The ports as the page loaded them (<see cref="AppSettingsModel.PortsKey(IEnumerable{PortMapping})"/>). When the stored
    /// ones differ, because a deploy moved the container port, that's the only problem given.
    /// </param>
    public async Task<List<PortProblem>> ValidatePortsAsync(int? appId, IReadOnlyList<PortRow> rows, string? loaded = null, CancellationToken ct = default)
    {
        AppDefinition? app = null;
        if (appId is int id)
        {
            app = await GetAsync(id, ct) ?? throw new InvalidOperationException("That app no longer exists.");
            if (Stale(app, loaded) is { } stale) return [stale];
        }
        var main = app is null ? null : DomainService.MainOf(app.Domains);
        var hostnames = app is null ? [] : app.Domains.OrderBy(d => d.Hostname == main ? 0 : 1).ThenBy(d => d.Id).Select(d => d.Hostname).ToList();
        // Finding the tunnel reads Docker, so it's only asked when it matters.
        var tunnelOnHost = hostnames.Count > 0 && rows.Any(r => r.IsWeb && r.Access == PortAccess.Internal) && await TunnelOnHostAsync(ct);
        IReadOnlyList<HostPortUse> uses = [];
        if (hostPorts is not null && rows.Any(r => r.Access != PortAccess.Internal))
        {
            try
            {
                uses = await hostPorts.GetAsync(ct);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            {
                return [new PortProblem(null, PortField.Table, $"Docker didn't say which host ports are in use ({ex.Message}). Try again in a moment.")];
            }
        }
        return PortRules.Check(rows, new PortContext(hostnames, tunnelOnHost, uses, app is null ? null : new HostPortOwner(app.ContainerName, app.Id)));
    }

    /// <summary>
    /// The host port to offer for a container port (<see cref="HostPortMap.Suggest"/>): its own number when that's free, else
    /// the next free one above it, never 22, 2375, 2376, 8080 or one in the random range. The app's own ports don't count.
    /// Throws when Docker can't be read, as <see cref="CheckHostPortAsync"/> does.
    /// </summary>
    /// <param name="appId">The app the port is for; null for a new one.</param>
    /// <param name="alsoTaken">Host ports the table's other rows already have.</param>
    public async Task<int> SuggestHostPortAsync(int containerPort, string protocol, int? appId, IEnumerable<int> alsoTaken, CancellationToken ct = default) =>
        hostPorts is null ? containerPort
        : await hostPorts.SuggestAsync(containerPort, PortRules.ProtocolOf(protocol), appId is int id ? new HostPortOwner(AppId: id) : null, alsoTaken, ct);

    /// <summary>
    /// Who holds a host port a row asks for, for the line under its field as it's typed in ("Free", "Used by orb (app).");
    /// the app's own ports don't count. Every port is free while Internal is chosen, which takes none.
    /// </summary>
    /// <param name="appId">The app the port is for; null for a new one.</param>
    public async Task<HostPortCheck> CheckHostPortAsync(int? appId, int hostPort, string protocol, PortAccess access, string? hostIp = null, CancellationToken ct = default) =>
        hostPorts is null || access == PortAccess.Internal ? new HostPortCheck(hostPort, PortRules.ProtocolOf(protocol), null, default)
        : await hostPorts.CheckAsync(hostPort, PortRules.ProtocolOf(protocol), ContainerPorts.BindAddress(access, hostIp), appId is int id ? new HostPortOwner(AppId: id) : null, ct);

    /// <summary>
    /// Vercel's variables as the app's next deploy sets them (see <see cref="VercelEnv"/>): its main domain now, and what
    /// its running image and commit were built with, less the ones the app sets itself. The repo's GitHub variables
    /// aren't read here, so one of those with the same name also wins.
    /// </summary>
    public async Task<VercelView> VercelVariablesAsync(AppDefinition app, CancellationToken ct = default)
    {
        if (!app.VercelVariables) return new VercelView([], [], Off: true);
        var traits = app.CurrentImage is { } image ? AppTraits.FromLabels(await ImageLabelsAsync(app, image, ct)) ?? AppTraits.None : AppTraits.None;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployed = app.CurrentCommit is null ? null : await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.CommitSha == app.CurrentCommit && d.Status == DeployStatus.Succeeded)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        var production = watch.DefaultBranch(app.Owner, app.Repo) is not { } main || main == app.Branch;
        var all = VercelEnv.For(new DeployFacts(await domains.MainHostnameAsync(app.Id, ct), app.Owner, app.Repo, app.Branch,
            app.CurrentCommit, deployed?.CommitMessage, deployed?.CommitAuthor, traits, production));
        var own = EnvScopes.ForDeploy(app.EnvVars, preview: false).Select(v => new EnvValue(v.Key, protector.TryUnprotect(v.ProtectedValue), v.IsSecret))
            .Where(e => e.Value.Length > 0).ToList();
        var added = VercelEnv.AddTo(own, all).Skip(own.Count).ToList();
        return new VercelView(added, all.Select(v => v.Key).Except(added.Select(a => a.Key)).ToList(), Off: false);
    }

    // An image's labels don't change once it's built, and the app page asks every few seconds. A rebuild of the same
    // commit reuses the tag, but every deploy updates the app, so the app's last update is part of the key.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<string, string>> ImageLabelsAsync(AppDefinition app, string image, CancellationToken ct)
    {
        var key = $"{image}|{app.UpdatedAt.Ticks}";
        if (_labels.TryGetValue(key, out var known)) return known;
        var labels = await docker.ImageLabelsAsync(image, ct);
        if (labels.Count > 0) _labels[key] = labels;
        return labels;
    }

    /// <summary>Adds the app with its settings and ports, under the network name <see cref="ChooseSlugAsync"/> gives it.</summary>
    public async Task<int> CreateAsync(GitHubRepoRef repo, AppSettingsModel m, DetectionResult? detection, CancellationToken ct = default)
    {
        var slug = (await ChooseSlugAsync(m.Name, ct)).Slug;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = new AppDefinition
        {
            Owner = repo.Owner,
            Repo = repo.Repo,
            Slug = slug,
            // A new Git app goes in the menu, as every one did before the menu listed only the pinned ones.
            Source = AppSource.Git,
            Pinned = true,
            DetectedKind = detection?.Kind.ToString(),
            DetectedSummary = detection?.Title,
            DetectedPort = detection?.ContainerPort,
        };
        Apply(app, m);
        // The limits beyond memory and CPUs (their priority, processes, shared memory) go where the Settings tab keeps them.
        if (m.Limits is { } limits) GitAppSettings.Apply(app, limits, GitAppSettings.Resources, protector);
        app.Ports.AddRange(PortRules.ToMappings(m.Ports));
        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);
        hostPorts?.ForgetSaved();
        // Also clears choices left over from an older container with the same name.
        await health.SetGatheredAsync(app.ContainerName, m.GatherStats, ct);
        await restarts.SetAsync(app.ContainerName, m.RestartInterval, ct);
        if (notify is not null) await notify.SetMutedAsync(app.ContainerName, false, ct);
        return app.Id;
    }

    /// <summary>
    /// The network name a new app gets: its name as a slug, with -2, -3… added while that's taken. A name is taken when
    /// another app has it, when a removed domain's route still goes to it, or when a container already answers to it or to
    /// gb-&lt;slug&gt; on the shared network, or is called that anywhere. Docker's DNS would split the traffic between the
    /// two: a new app called Postgres would get half of what was meant for the shared postgres. gitberth is Gitberth's own.
    /// </summary>
    /// <exception cref="InvalidOperationException">Docker couldn't say which names are taken.</exception>
    public async Task<NetworkName> ChooseSlugAsync(string name, CancellationToken ct = default)
    {
        var slug = Slugify(name);
        HashSet<string> apps;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // An Existing app's container keeps its name while it's missing: a new app mustn't be called gb-<that>.
            var rows = await db.Apps.AsNoTracking().Select(a => new { a.Slug, a.Container }).ToListAsync(ct);
            apps = rows.Select(a => a.Slug)
                .Concat(rows.Where(a => a.Container is { } c && c.StartsWith("gb-", StringComparison.Ordinal)).Select(a => a.Container!["gb-".Length..]))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        // A route of a removed domain that still goes to a name keeps it taken, as a route to a port keeps the port: the
        // app's alias and container name would get that domain's visitors.
        var routed = await domains.RoutedNamesAsync(ct);
        var answered = network is null ? new HashSet<string>() : await TakenOnNetworkAsync(network, ct);
        answered.Add(GitberthName);
        string? Taken(string candidate) =>
            apps.Contains(candidate) ? $"{candidate} is another app's"
            : routed.Contains(candidate) || routed.Contains(AppDefinition.ContainerNameFor(candidate)) ? $"a removed domain still goes to {candidate}"
            : answered.Contains(candidate) || answered.Contains(AppDefinition.ContainerNameFor(candidate)) ? $"{candidate} is taken on {options.Network}"
            : null;
        var unique = slug;
        for (var i = 2; Taken(unique) is not null; i++) unique = $"{slug}-{i}";
        return new NetworkName(unique, Taken(slug));
    }

    private async Task<HashSet<string>> TakenOnNetworkAsync(NetworkService names, CancellationToken ct)
    {
        try
        {
            return new HashSet<string>(await names.TakenNamesAsync(options.Network, ct), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            // The name is the app's for good, so it's never chosen blind.
            throw new InvalidOperationException($"Docker didn't say which names are taken on {options.Network} ({ex.Message}). Try again in a moment.", ex);
        }
    }

    /// <summary>
    /// Saves what the Settings tab edits. The app's ports aren't among them: they're saved on their own
    /// (<see cref="SavePortsAsync"/>), so whatever the form holds for them, they stay as they are.
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(int appId, AppSettingsModel m, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
            .FirstAsync(a => a.Id == appId, ct);
        var before = Fingerprint(app, protector, options.Network);
        Apply(app, m);
        var after = Fingerprint(app, protector, options.Network);
        var outcome = new SaveOutcome(before.Runtime != after.Runtime, before.Build != after.Build);
        if (app.CurrentImage is not null)
        {
            if (outcome.NeedsRestart) app.ConfigChangePending = true;
            if (outcome.NeedsRebuild) app.RebuildPending = true;
        }
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Not part of the fingerprint: gathering stats or a restart schedule never needs a restart to apply.
        if (m.GatherStats != m.GatherStatsLoaded) await health.SetGatheredAsync(app.ContainerName, m.GatherStats, ct);
        if (m.RestartInterval?.TotalMinutes != m.RestartLoaded) await restarts.SetAsync(app.ContainerName, m.RestartInterval, ct);
        return outcome;
    }

    /// <summary>
    /// Saves an app's ports, as its Network tab has them, once they pass <see cref="ValidatePortsAsync"/>; nothing is saved
    /// otherwise. A change to a port's access, host port, address, container port or protocol is live only once the
    /// container is recreated, so the app is marked as having saved settings that aren't live yet. Moving the web marker
    /// alone is live at once: the domains are synced afterwards and follow it.
    /// </summary>
    /// <param name="loaded">The ports as the page loaded them (<see cref="AppSettingsModel.PortsKey(IEnumerable{PortMapping})"/>); null to save over whatever is stored.</param>
    public async Task<PortsSaved> SavePortsAsync(int appId, IReadOnlyList<PortRow> rows, string? loaded, CancellationToken ct = default)
    {
        var problems = await ValidatePortsAsync(appId, rows, loaded, ct);
        if (problems.Count > 0) return new PortsSaved(problems, NeedsRestart: false);
        bool restart, routed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var app = await db.Apps.Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == appId, ct)
                ?? throw new InvalidOperationException("That app no longer exists.");
            // Again, in the same read as the save: a deploy may have moved the container port since.
            if (Stale(app, loaded) is { } stale) return new PortsSaved([stale], NeedsRestart: false);
            if (AppSettingsModel.PortsKey(app.Ports) == AppSettingsModel.PortsKey(rows)) return new PortsSaved([], NeedsRestart: false);
            restart = PortRules.NeedsRestart(app.Ports, rows);
            app.Ports.Clear();
            app.Ports.AddRange(PortRules.ToMappings(rows));
            if (restart && app.CurrentImage is not null) app.ConfigChangePending = true;
            app.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            routed = app.Domains.Count > 0;
        }
        hostPorts?.ForgetSaved();
        // Routes go to the web port's live binding: a moved marker re-routes the domains now, and a port that isn't live
        // yet leaves them where they are until the container is recreated.
        var report = routed && await domains.EnabledAsync(ct) ? await domains.SyncAsync(ct) : null;
        return new PortsSaved([], restart, report);
    }

    // The ports stored now aren't the ones the page loaded: a deploy can move the container port.
    private static PortProblem? Stale(AppDefinition app, string? loaded) =>
        loaded is not null && AppSettingsModel.PortsKey(app.Ports) != loaded
            ? new PortProblem(null, PortField.Table, $"{app.Name}'s ports changed while this page was open (a deploy can move the container port). Reload, then change them again.")
            : null;

    // cloudflared on the host's network reaches apps only through a host port; on the shared network, it reaches them by name.
    private async Task<bool> TunnelOnHostAsync(CancellationToken ct) => (await domains.FindTunnelAsync(ct)).Tunnel?.HostNetwork == true;

    /// <summary>The equivalent `docker run` command (secrets masked), for transparency. The repo's GitHub variables aren't in it.</summary>
    /// <param name="vercel">Vercel's variables for the app, when the caller already has them (<see cref="VercelVariablesAsync"/>).</param>
    public async Task<string> DockerRunPreviewAsync(AppDefinition app, VercelView? vercel = null)
    {
        var env = EnvScopes.ForDeploy(app.EnvVars, preview: false)
            .Select(v => new EnvValue(v.Key, protector.TryUnprotect(v.ProtectedValue), v.IsSecret))
            .Where(e => e.Value.Length > 0)
            .ToList();
        env = VercelEnv.AddTo(env, (vercel ?? await VercelVariablesAsync(app)).Added);
        var image = app.CurrentImage ?? $"{app.ImageRepository}:<commit>";
        // As a deploy decides them: networks at a lower gateway priority where the engine has them, and the LAN's ports on
        // IPv4 only when the host has an IPv6 address the internet can reach, or couldn't be read. That's from the host's
        // last scan, whatever its age: the app's page asks for this every few seconds, and mustn't start the scan's helper
        // container each time.
        var api = await docker.ServerApiVersionAsync();
        var lanIpv4Only = hostScanner?.Last?.LanIpv4Only == true;
        return ContainerSpecFactory.For(app, image, env, options.Network, await docker.GetLogOptionsAsync(),
            gatewayPriority: api is not null && api >= DockerCli.GwPriorityApi, lanIpv4Only: lanIpv4Only).ToDisplayString();
    }

    /// <summary>
    /// The LAN address of this host, which every LAN address and connection string uses: the one saved in Settings, else
    /// GITBERTH_PUBLIC_HOST, else the address detected on the host's default route, else the browser's host. The detected
    /// one is the last scan's, so a page never waits on the helper container (only the first look after a start does).
    /// </summary>
    public async Task<string> GetPublicHostAsync(string baseUri) =>
        await AddressBook.LanHostFromAsync(await settings.GetAsync(SettingKeys.PublicHost), options.PublicHost,
            async () => hostScanner is null ? null : await hostScanner.DetectedAddressAsync(), new Uri(baseUri).Host);

    public static string Slugify(string? name)
    {
        var slug = NonSlugChars().Replace((name ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    private void Apply(AppDefinition app, AppSettingsModel m)
    {
        app.Name = m.Name.Trim();
        app.Branch = m.Branch.Trim();
        app.Subdirectory = NullIfBlank(m.Subdirectory?.Trim().Trim('/'));
        app.DockerfileMode = m.DockerfileMode;
        if (m.CustomDockerfile is not null) app.CustomDockerfile = m.CustomDockerfile;
        app.ProjectHint = NullIfBlank(m.ProjectHint);
        app.RestartPolicy = m.RestartPolicy;
        app.MemoryLimitMb = m.MemoryLimitMb;
        app.CpuLimit = m.CpuLimit;
        app.HealthCheckPath = NullIfBlank(m.HealthCheckPath?.Trim());
        app.AutoDeploy = m.AutoDeploy;
        app.VercelVariables = m.VercelVariables;

        // Update env vars in place (the (AppId, Key, Scope) index is unique). This form edits the ones every deploy gets;
        // a production or preview value stays as it is.
        var rows = m.Env.Where(e => !string.IsNullOrWhiteSpace(e.Key)).ToList();
        var keep = rows.Select(r => r.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        app.EnvVars.RemoveAll(v => v.Scope == EnvScope.All && !keep.Contains(v.Key));
        foreach (var row in rows)
        {
            var key = row.Key.Trim();
            var existing = app.EnvVars.FirstOrDefault(v => v.Key == key && v.Scope == EnvScope.All);
            if (existing is null)
            {
                existing = new EnvVar { Key = key };
                app.EnvVars.Add(existing);
            }
            existing.ProtectedValue = protector.Protect(row.Value ?? "");
            existing.IsSecret = row.IsSecret;
            existing.BuildTime = row.BuildTime;
        }

        app.Volumes.Clear();
        foreach (var v in m.Volumes.Where(v => !string.IsNullOrWhiteSpace(v.Source) && !string.IsNullOrWhiteSpace(v.Target)))
            app.Volumes.Add(new VolumeMount { Source = v.Source.Trim(), Target = v.Target.Trim(), ReadOnly = v.ReadOnly });
    }

    /// <summary>
    /// What the app's saved settings give its container short of a build (Runtime), and its image (Build). A save that
    /// leaves one as it was needs no restart, or no build, for it; a deploy compares the settings it ran with to the ones
    /// saved once it's done, to know what's still pending.
    /// </summary>
    /// <param name="shared">The shared network every app is on.</param>
    /// <param name="networks">The app's own networks, when they're read apart from the app (a deploy reads them under the app's lock).</param>
    internal static (string Runtime, string Build) Fingerprint(AppDefinition a, SecretProtector protector, string shared, IEnumerable<AppNetwork>? networks = null)
    {
        // Production's variables only: a value for previews never leaves the app's own container pending.
        var env = EnvScopes.ForDeploy(a.EnvVars, preview: false).OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => (v.Key, Value: protector.TryUnprotect(v.ProtectedValue), v.IsSecret, v.BuildTime)).ToList();
        // The -p, --expose and --network arguments the container is created with; not the web marker, which needs no restart.
        // The settings beyond the classic ones only count once there are some: an app without any keeps the key it had.
        var runtime = ClassicRecreateKey(a, env, shared, networks) + $"#{a.RestartPolicy}#{a.MemoryLimitMb}#{a.CpuLimit}"
            + (a.RuntimeExtras is { } extras ? "#extras:" + extras : "");
        // Vercel's variables go into the build too (front ends build them in).
        var build = $"{a.Branch}#{a.Subdirectory}#{a.DockerfileMode}#{(a.DockerfileMode == DockerfileMode.Custom ? a.CustomDockerfile : "")}#{a.ProjectHint}#{a.VercelVariables}"
            + "#" + string.Join('|', env.Where(e => e.BuildTime).Select(e => $"{e.Key}={e.Value}"))
            + "#" + string.Join('|', a.Volumes.Select(v => v.Target).Order());
        return (runtime, build);
    }

    /// <summary>
    /// The runtime fingerprint in two (2.2): what only a new container takes (Recreate: ports, networks, environment,
    /// volumes and the settings beyond the classic ones that a recreate applies) and what <c>docker update</c> changes on
    /// the running container (Live: restart policy, memory, CPUs and PIDs).
    /// </summary>
    internal static (string Recreate, string Live) RuntimeKeys(AppDefinition a, SecretProtector protector, string shared, IEnumerable<AppNetwork>? networks = null)
    {
        var env = EnvScopes.ForDeploy(a.EnvVars, preview: false).OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => (v.Key, Value: protector.TryUnprotect(v.ProtectedValue), v.IsSecret, v.BuildTime)).ToList();
        var extras = GitExtras.Parse(a.RuntimeExtras);
        var live = extras is null ? null : GitExtras.Write(new GitExtras
        {
            RestartRetries = extras.RestartRetries, MemoryReservation = extras.MemoryReservation, MemorySwap = extras.MemorySwap,
            CpuShares = extras.CpuShares, CpusetCpus = extras.CpusetCpus, PidsLimit = extras.PidsLimit,
        });
        var recreate = extras is null ? null : GitExtras.Write(extras with
        {
            RestartRetries = null, MemoryReservation = null, MemorySwap = null, CpuShares = null, CpusetCpus = null, PidsLimit = null,
        });
        return (ClassicRecreateKey(a, env, shared, networks) + (recreate is null ? "" : "#extras:" + recreate),
            $"{a.RestartPolicy}#{a.MemoryLimitMb}#{a.CpuLimit}" + (live is null ? "" : "#extras:" + live));
    }

    private static string ClassicRecreateKey(AppDefinition a, List<(string Key, string Value, bool IsSecret, bool BuildTime)> env, string shared, IEnumerable<AppNetwork>? networks) =>
        ContainerSpecFactory.RuntimeKey(a, shared, networks)
        + "#" + string.Join('|', env.Select(e => $"{e.Key}={e.Value}"))
        + "#" + string.Join('|', a.Volumes.Select(v => $"{v.Source}:{v.Target}:{v.ReadOnly}").Order());

    /// <summary>
    /// Saves a Git app's settings from the unified form; never touches Docker. Only what differs between the loaded and the
    /// wanted settings is written, onto what's saved now, so a change saved meanwhile elsewhere stays. A change only a new
    /// container takes sets ConfigChangePending (RecreateNeeded); one to the build sets RebuildPending (RebuildNeeded); the
    /// live fields that changed are left for the caller to apply with docker update (LiveChanged), and marked pending only
    /// when that fails (<see cref="MarkPendingAsync"/>). Nothing is saved while there are problems.
    /// </summary>
    public async Task<GitSaved> SaveSettingsAsync(int appId, SettingsEdit edit, CancellationToken ct = default)
    {
        var fields = GitAppSettings.Changed(edit.Loaded, edit.Wanted);
        var problems = new List<SettingProblem>();
        if (edit.WantedOptions.Name != edit.LoadedOptions.Name && NameProblem(edit.WantedOptions.Name) is { } nameProblem) problems.Add(new(null, nameProblem));
        if (edit.WantedOptions.HealthCheckPath is { Length: > 0 } path && !path.StartsWith('/')) problems.Add(new(null, "The health URL must start with /, like /health."));
        if (edit.WantedOptions.ScheduledRestart is { } every && every != edit.LoadedOptions.ScheduledRestart && RestartInterval.Problem(every.Amount, every.Unit) is { } restartProblem)
            problems.Add(new(null, "Scheduled restart: " + restartProblem));
        if (edit.WantedGit is { } git && edit.WantedGit != edit.LoadedGit)
        {
            if (string.IsNullOrWhiteSpace(git.Branch)) problems.Add(new(null, "Pick a branch."));
            if (git.DockerfileMode == DockerfileMode.Custom && string.IsNullOrWhiteSpace(git.CustomDockerfile)) problems.Add(new(null, "The custom Dockerfile is empty."));
            problems.AddRange(AppSettingsService.GitProblems(edit.LoadedGit, git));
        }
        if (fields.Contains(SettingField.RestartPolicy) && !RestartPolicies.Contains(edit.Wanted.Restart.Name))
            problems.Add(new(SettingField.RestartPolicy, "Pick a restart policy."));
        if (fields.Contains(SettingField.Env))
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in edit.Wanted.Env.Where(e => !e.FromImage))
            {
                if (!EnvRules.IsValidKey(e.Key.Trim())) problems.Add(new(SettingField.Env, $"'{e.Key}' isn't a valid variable name (letters, digits and _; it can't start with a digit)."));
                else if (!keys.Add(EnvScopes.Named(e.Key.Trim(), e.Scope))) problems.Add(new(SettingField.Env, $"The variable {EnvScopes.Named(e.Key.Trim(), e.Scope)} is listed twice."));
            }
        }
        if (problems.Count > 0) return new GitSaved(false, false, new HashSet<SettingField>(), problems);

        AppDefinition app;
        bool recreate, rebuild;
        var liveChanged = new HashSet<SettingField>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            app = await db.Apps.Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
                .FirstOrDefaultAsync(a => a.Id == appId, ct) ?? throw new InvalidOperationException("That app no longer exists.");
            if (!app.Deployed) throw new InvalidOperationException($"{app.Name} isn't a Git app: its settings live in its container.");
            var keysBefore = RuntimeKeys(app, protector, options.Network);
            var buildBefore = Fingerprint(app, protector, options.Network).Build;
            GitAppSettings.Apply(app, edit.Wanted, fields, protector);
            // A named volume whose name another app has, here or in a preview's copy, isn't saved (VolumeOwners). The
            // query reads what's saved, before this change.
            if (fields.Contains(SettingField.Mounts) && (await VolumeOwners.LoadAsync(db, ct)).Problems(app, app.Volumes) is { Count: > 0 } clashes)
                return new GitSaved(false, false, new HashSet<SettingField>(), clashes.Select(c => new SettingProblem(SettingField.Mounts, c)).ToList());
            if (edit.WantedGit is { } wantedGit && wantedGit != edit.LoadedGit) GitAppSettings.Apply(app, wantedGit);
            ApplyOptions(app, edit.LoadedOptions, edit.WantedOptions);
            var keysAfter = RuntimeKeys(app, protector, options.Network);
            recreate = keysBefore.Recreate != keysAfter.Recreate;
            rebuild = buildBefore != Fingerprint(app, protector, options.Network).Build;
            if (keysBefore.Live != keysAfter.Live) liveChanged.UnionWith(fields.Where(SettingFields.Live.Contains));
            if (app.CurrentImage is not null)
            {
                if (recreate) app.ConfigChangePending = true;
                if (rebuild) app.RebuildPending = true;
            }
            // Saved as it loaded, the row stays exactly as it was, its last update included.
            if (db.ChangeTracker.HasChanges())
            {
                app.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        hostPorts?.ForgetSaved();
        // Not part of the fingerprint: gathering stats or a restart schedule never needs a restart to apply.
        if (edit.WantedOptions.GatherStats != edit.LoadedOptions.GatherStats) await health.SetGatheredAsync(app.ContainerName, edit.WantedOptions.GatherStats, ct);
        if (edit.WantedOptions.ScheduledRestart != edit.LoadedOptions.ScheduledRestart) await restarts.SetAsync(app.ContainerName, edit.WantedOptions.ScheduledRestart, ct);
        if (edit.WantedOptions.Notify != edit.LoadedOptions.Notify && notify is not null) await notify.SetMutedAsync(app.ContainerName, !edit.WantedOptions.Notify, ct);
        return new GitSaved(recreate, rebuild, liveChanged, []);
    }

    // Gitberth's own data on the row: only what the form changed.
    private static void ApplyOptions(AppDefinition app, AppOptions loaded, AppOptions wanted)
    {
        if (wanted.Name != loaded.Name) app.Name = wanted.Name.Trim();
        if (wanted.HealthCheckPath != loaded.HealthCheckPath) app.HealthCheckPath = NullIfBlank(wanted.HealthCheckPath?.Trim());
        if (wanted.Pinned != loaded.Pinned) app.Pinned = wanted.Pinned;
        if (wanted.WebPort != loaded.WebPort)
        {
            var marked = false;
            foreach (var p in app.Ports.OrderBy(p => p.Id))
            {
                p.IsWeb = !marked && wanted.WebPort == p.ContainerPort && p.Protocol == "tcp";
                marked |= p.IsWeb;
            }
        }
    }

    /// <summary>Marks a Git app's saved settings as not live yet: a live update of them failed, and the next deploy applies them.</summary>
    public async Task MarkPendingAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Apps.Where(a => a.Id == appId && a.CurrentImage != null).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.ConfigChangePending, true)
            .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex VolumeName();
}

/// <summary>What saving a Git app's settings did (<see cref="AppService.SaveSettingsAsync"/>).</summary>
/// <param name="RecreateNeeded">Only a new container takes it: ConfigChangePending is set.</param>
/// <param name="RebuildNeeded">The build changes: RebuildPending is set.</param>
/// <param name="LiveChanged">Live fields that changed, for the caller to apply with docker update.</param>
/// <param name="Problems">Why nothing was saved; empty once it is.</param>
public sealed record GitSaved(bool RecreateNeeded, bool RebuildNeeded, IReadOnlySet<SettingField> LiveChanged, IReadOnlyList<SettingProblem> Problems);
