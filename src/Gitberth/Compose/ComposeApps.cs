using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Compose;

/// <summary>A variable a Compose app's file is filled with: its value, and whether it's a secret (masked, never logged).</summary>
public sealed record ComposeValue(string Key, string Value, bool Secret = false);

/// <summary>
/// A new inline Compose app: a whole Compose file (pasted, or a template's) deployed as one app, its web service the app's
/// container.
/// </summary>
/// <param name="WebPort">The container port the web service serves on; null for an app without one (a worker).</param>
/// <param name="Variables">What fills the file's ${…}: the app's variables, changed later on its Environment tab.</param>
public sealed record InlineComposeApp(string Name, string Yaml, string WebService, int? WebPort, IReadOnlyList<ComposeValue> Variables)
{
    /// <summary>The web port's host port; null: the port's own number when it's free, else the next free one above it.</summary>
    public int? HostPort { get; init; }
    /// <summary>Who reaches the web port: the LAN by default.</summary>
    public PortAccess Access { get; init; } = PortAccess.Lan;
    /// <summary>The template it's installed from (its id in the catalog); null for a pasted file.</summary>
    public string? TemplateId { get; init; }
    public string? HealthCheckPath { get; init; }
    /// <summary>
    /// Every service on Gitberth's shared network, not only the web one (with no name of its own there): a worker or a cron
    /// container reaches the app's managed database (postgres, &lt;slug&gt;-redis) and other apps by name. Off by default.
    /// </summary>
    public bool EveryServiceOnSharedNetwork { get; init; }
}

/// <param name="Key">The app's key: its page is apps/&lt;key&gt;.</param>
/// <param name="DeploymentId">Its first deploy; null when it wasn't asked for.</param>
public sealed record ComposeCreated(int AppId, string Key, int? DeploymentId);

/// <summary>One service of a Compose app, as its Services card shows it.</summary>
/// <param name="Container">Its container (gb-&lt;slug&gt;_&lt;service&gt;); null when it has none.</param>
/// <param name="Web">It's the app's web service: the app's own container.</param>
public sealed record ComposeMember(string Service, string? Container, string State, string Status, string Image, string Ports, bool Web,
    ulong? MemoryBytes = null, double? CpuPercent = null)
{
    public bool Running => State == "running";
}

/// <summary>
/// Compose apps beyond their deploys: making an inline one from a whole Compose file (what templates call), saving a
/// Compose app's file and web service, and its services as its page shows them.
/// </summary>
public sealed class ComposeApps(IDbContextFactory<GitberthDb> dbFactory, AppService apps, AppActions actions, SecretProtector protector, DockerCli docker,
    HealthSettings health, ContainerStatsCollector stats, GitberthOptions options, ILogger<ComposeApps> logger,
    Notifications.NotificationSettings? notify = null, Audit.AuditLog? audit = null, ComposeRunner? composer = null)
{
    private readonly ComposeRunner _composer = composer ?? new ComposeRunner(docker, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposeRunner>.Instance);

    /// <summary>Whether the app puts every service on Gitberth's shared network (<see cref="InlineComposeApp.EveryServiceOnSharedNetwork"/>).</summary>
    public async Task<bool> EveryServiceSharedAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = ComposeNames.EveryServiceSharedKey(appId);
        return await db.Settings.AsNoTracking().AnyAsync(s => s.Key == key && s.Value == "true", ct);
    }

    // On: the setting; off: none.
    private static async Task SetEveryServiceSharedAsync(GitberthDb db, int appId, bool on, CancellationToken ct)
    {
        var key = ComposeNames.EveryServiceSharedKey(appId);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (on && row is null) db.Settings.Add(new Setting { Key = key, Value = "true" });
        else if (on && row is not null) row.Value = "true";
        else if (!on && row is not null) db.Settings.Remove(row);
    }
    /// <summary>Why a Compose file can't be deployed as one app with this web service; empty when it can.</summary>
    public static List<string> Problems(string yaml, string? webService)
    {
        var info = ComposeFiles.Read(yaml);
        if (info.Error is { } error) return [error];
        var problems = new List<string>();
        foreach (var s in info.Services.Where(s => s.Copies > 1))
            problems.Add($"The service {s.Name} asks for {s.Copies} containers (deploy.replicas or scale). Gitberth runs one container a service: set it to 1.");
        foreach (var s in info.Services.Where(s => !ComposeNames.IsServiceName(s.Name)))
            problems.Add($"The service name {s.Name} has characters Compose doesn't take: use letters, digits, '.', '_' and '-'.");
        if (webService is not null && info.Service(webService) is null)
            problems.Add($"There's no service {webService} in the file (it has {string.Join(", ", info.Services.Select(s => s.Name))}).");
        return problems;
    }

    /// <summary>
    /// Makes an inline Compose app from a whole Compose file: its record (the file kept encrypted, its variables as its own,
    /// its web port on the LAN at a free host port), then, unless <paramref name="deploy"/> is false, its first deploy.
    /// Templates call this with their file, their inputs and generated secrets as <see cref="InlineComposeApp.Variables"/>,
    /// and their web service and port.
    /// </summary>
    /// <exception cref="ArgumentException">The name, the file, its web service or its port can't be used: the message says why.</exception>
    public async Task<ComposeCreated> CreateInlineAsync(InlineComposeApp request, bool deploy = true, CancellationToken ct = default)
    {
        if (AppService.NameProblem(request.Name) is { } nameProblem) throw new ArgumentException(nameProblem, nameof(request));
        if (Problems(request.Yaml, request.WebService) is { Count: > 0 } problems) throw new ArgumentException(string.Join(" ", problems), nameof(request));
        foreach (var v in request.Variables.Where(v => v.Key.Trim().Length > 0))
            if (!Detection.EnvRules.IsValidKey(v.Key.Trim())) throw new ArgumentException($"'{v.Key}' isn't a valid variable name (letters, digits and _; it can't start with a digit).", nameof(request));
        var info = ComposeFiles.Read(request.Yaml);
        var web = info.Service(request.WebService)!;
        var slug = (await apps.ChooseSlugAsync(request.Name, ct)).Slug;
        var rows = await PortRowsAsync(web, request, ct);
        if (rows.Count > 0 && await apps.ValidatePortsAsync(null, rows, ct: ct) is { Count: > 0 } portProblems)
            throw new ArgumentException(string.Join(" ", portProblems.Select(p => p.Message).Distinct()), nameof(request));

        var app = new AppDefinition
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Source = AppSource.Compose,
            ComposeYamlProtected = protector.Protect(request.Yaml),
            WebService = request.WebService,
            Container = ComposeNames.Member(slug, request.WebService),
            TemplateId = request.TemplateId,
            Pinned = true,
            AutoDeploy = false,
            VercelVariables = false,
            HealthCheckPath = string.IsNullOrWhiteSpace(request.HealthCheckPath) ? null : request.HealthCheckPath.Trim(),
        };
        app.Ports.AddRange(PortRules.ToMappings(rows));
        foreach (var v in request.Variables.Where(v => v.Key.Trim().Length > 0).GroupBy(v => v.Key.Trim(), StringComparer.Ordinal).Select(g => g.Last()))
            app.EnvVars.Add(new EnvVar
            {
                Key = v.Key.Trim(), ProtectedValue = protector.Protect(v.Value), IsSecret = v.Secret || Masking.IsSecret(v.Key.Trim(), v.Value),
            });
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Apps.Add(app);
            await db.SaveChangesAsync(ct);
            if (request.EveryServiceOnSharedNetwork)
            {
                await SetEveryServiceSharedAsync(db, app.Id, true, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        // Its host ports are taken from now on: the next app's suggestion (a second install a moment later) gets another one.
        apps.ForgetSavedPorts();
        await health.SetGatheredAsync(app.ContainerName, true, ct);
        if (notify is not null) await notify.SetMutedAsync(app.ContainerName, false, ct);
        audit?.Record(new Audit.AuditEntry("app.created",
            $"Created {app.Name} from {(request.TemplateId is { } template ? $"the template {template}" : "a Compose file")} with {Count(info.Services.Count, "service")}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
            Changes =
            [
                new("Source", null, request.TemplateId is { } t ? $"template {t}" : "Compose file"),
                new("Services", null, string.Join(", ", info.Services.Select(s => s.Name))),
                new("Web service", null, request.WebService),
                new("Ports", null, SettingsAudit.Ports(app.Ports)),
                .. app.EnvVars.Count > 0 ? new[] { new Audit.AuditChange("Variables", null, string.Join(", ", app.EnvVars.Select(v => v.Key).Order(StringComparer.Ordinal))) } : [],
                .. request.EveryServiceOnSharedNetwork ? new[] { new Audit.AuditChange("Every service on Gitberth's network", null, "on") } : [],
            ],
        });
        var deployment = deploy ? await actions.DeployAsync(app.Id, DeployTrigger.Created, ct) : (int?)null;
        return new ComposeCreated(app.Id, app.ContainerName, deployment);
    }

    // The web service's ports as the app's: the web port on the LAN (its host port the file's, or the asked one, when free,
    // else the next free one), the file's other published ones too, and the ones it only exposes inside Docker.
    private async Task<List<PortRow>> PortRowsAsync(ComposeServiceInfo web, InlineComposeApp request, CancellationToken ct)
    {
        var rows = new List<PortRow>();
        var taken = new List<int>();
        async Task<int> HostAsync(int wanted)
        {
            int port;
            try
            {
                port = await apps.SuggestHostPortAsync(wanted, "tcp", null, taken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                port = wanted;
            }
            taken.Add(port);
            return port;
        }
        if (request.WebPort is int webPort)
        {
            var filed = web.Ports.FirstOrDefault(p => p.Target == webPort && p.Protocol == "tcp");
            var access = request.Access;
            var host = access == PortAccess.Internal ? (int?)null : request.HostPort ?? await HostAsync(filed?.Published ?? webPort);
            rows.Add(new PortRow { ContainerPort = webPort, Protocol = "tcp", Access = access, HostPort = host, IsWeb = true });
        }
        foreach (var p in web.Ports.Where(p => !(p.Target == request.WebPort && p.Protocol == "tcp")).DistinctBy(p => (p.Target, p.Protocol)))
            rows.Add(p.Published is int published
                ? new PortRow { ContainerPort = p.Target, Protocol = p.Protocol, Access = PortAccess.Lan, HostPort = await HostAsync(published) }
                : new PortRow { ContainerPort = p.Target, Protocol = p.Protocol, Access = PortAccess.Internal });
        return rows;
    }

    /// <summary>
    /// Saves an inline Compose app's file and web service, then deploys it with them (a save that changes nothing deploys
    /// nothing). Null when it's saved, else why not.
    /// </summary>
    public async Task<(string? Problem, int? DeploymentId)> SaveInlineAsync(int appId, string yaml, string webService, CancellationToken ct = default) =>
        await SaveInlineAsync(appId, yaml, webService, null, ct);

    /// <param name="everyServiceShared">Every service on Gitberth's shared network; null leaves it as it is.</param>
    public async Task<(string? Problem, int? DeploymentId)> SaveInlineAsync(int appId, string yaml, string webService, bool? everyServiceShared, CancellationToken ct = default)
    {
        if (Problems(yaml, webService) is { Count: > 0 } problems) return (string.Join(" ", problems), null);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is not { Source: AppSource.Compose }) return ("That isn't an inline Compose app.", null);
        var before = app.ComposeYamlProtected is { } stored ? protector.TryUnprotect(stored) : "";
        var changes = new List<Audit.AuditChange>();
        if (before != yaml)
        {
            var (added, removed) = (ComposeFiles.Read(yaml).Services.Select(s => s.Name).ToList(), ComposeFiles.Read(before).Services.Select(s => s.Name).ToList());
            changes.Add(new("Compose file", ComposeReleases.IdOf(before), ComposeReleases.IdOf(yaml)));
            if (!added.SequenceEqual(removed)) changes.Add(new("Services", string.Join(", ", removed), string.Join(", ", added)));
            app.ComposeYamlProtected = protector.Protect(yaml);
        }
        if (app.WebService != webService)
        {
            if (await ContainerTakenAsync(db, app, webService, ct) is { } taken) return (taken, null);
            changes.Add(new("Web service", app.WebService, webService));
            app.WebService = webService;
            app.Container = ComposeNames.Member(app.Slug, webService);
        }
        await SharedChangeAsync(db, app.Id, everyServiceShared, changes, ct);
        if (changes.Count == 0) return (null, null);
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        audit?.Record(new Audit.AuditEntry("app.settings.changed", $"Changed {app.Name}'s Compose file: it's deployed with it now.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
        return (null, await ApplyAsync(app.Id, changes, ct));
    }

    private const string SharedField = "Every service on Gitberth's network";

    // The shared-network switch as asked, with its change for the audit log.
    private async Task SharedChangeAsync(GitberthDb db, int appId, bool? wanted, List<Audit.AuditChange> changes, CancellationToken ct)
    {
        if (wanted is not bool on || await EveryServiceSharedAsync(appId, ct) == on) return;
        await SetEveryServiceSharedAsync(db, appId, on, ct);
        changes.Add(new(SharedField, on ? "off" : "on", on ? "on" : "off"));
    }

    // A changed file or web service is deployed; the shared network alone is made live without a build.
    private async Task<int> ApplyAsync(int appId, List<Audit.AuditChange> changes, CancellationToken ct) =>
        changes.All(c => c.Field == SharedField)
            ? await actions.RecreateAsync(appId, DeployTrigger.ConfigChange, ct: ct)
            : await actions.DeployAsync(appId, DeployTrigger.ConfigChange, ct);

    /// <summary>Saves a Git Compose app's Compose file and web service, then deploys it with them. Null when it's saved, else why not.</summary>
    public async Task<(string? Problem, int? DeploymentId)> SaveGitAsync(int appId, string file, string webService, CancellationToken ct = default) =>
        await SaveGitAsync(appId, file, webService, null, ct);

    /// <param name="everyServiceShared">Every service on Gitberth's shared network; null leaves it as it is.</param>
    public async Task<(string? Problem, int? DeploymentId)> SaveGitAsync(int appId, string file, string webService, bool? everyServiceShared, CancellationToken ct = default)
    {
        file = file.Trim().Trim('/');
        if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file))
            return ("The Compose file is a path inside the app's folder, like compose.yaml.", null);
        if (webService.Trim().Length == 0 || !ComposeNames.IsServiceName(webService)) return ("Pick the web service.", null);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is not { Source: AppSource.Git, ComposeFile: not null }) return ("That isn't a Git app deployed with Compose.", null);
        var changes = new List<Audit.AuditChange>();
        if (app.ComposeFile != file)
        {
            changes.Add(new("Compose file", app.ComposeFile, file));
            app.ComposeFile = file;
        }
        if (app.WebService != webService)
        {
            if (await ContainerTakenAsync(db, app, webService, ct) is { } taken) return (taken, null);
            changes.Add(new("Web service", app.WebService, webService));
            app.WebService = webService;
            app.Container = ComposeNames.Member(app.Slug, webService);
        }
        await SharedChangeAsync(db, app.Id, everyServiceShared, changes, ct);
        if (changes.Count == 0) return (null, null);
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        audit?.Record(new Audit.AuditEntry("app.settings.changed", $"Changed how {app.Name} is deployed with Compose.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
        return (null, await ApplyAsync(app.Id, changes, ct));
    }

    // Another app's container can't be the one a new web service's would be (two Compose apps never share one).
    private static async Task<string?> ContainerTakenAsync(GitberthDb db, AppDefinition app, string webService, CancellationToken ct)
    {
        var name = ComposeNames.Member(app.Slug, webService);
        return await db.Apps.AsNoTracking().AnyAsync(a => a.Id != app.Id && a.Container == name, ct) ? $"{name} is another app's container." : null;
    }

    /// <summary>
    /// The app's services as its page shows them: each service of its current release (and any other with a container of
    /// the app), its container's state, image, ports and latest CPU and memory; a service without a container says so.
    /// </summary>
    public async Task<IReadOnlyList<ComposeMember>> MembersAsync(AppDefinition app, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> images;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            images = Deploy.DeployPipeline.ParseImages(await db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.Status == DeployStatus.Succeeded)
                .OrderByDescending(d => d.Id).Select(d => d.Images).FirstOrDefaultAsync(ct));
        var (containers, _) = await docker.ListContainersAsync(managedOnly: true, ct);
        var mine = containers.Where(c => c.Labels.GetValueOrDefault("gitberth.app") == app.Slug && c.Labels.ContainsKey(ComposeNames.ServiceLabel))
            .GroupBy(c => c.Labels[ComposeNames.ServiceLabel], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var readings = stats.Current.Containers.GroupBy(h => h.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var services = images.Keys.Concat(mine.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s == app.WebService ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal).ToList();
        return services.Select(s =>
        {
            if (!mine.TryGetValue(s, out var c)) return new ComposeMember(s, null, "missing", "No container", images.GetValueOrDefault(s) ?? "", "", s == app.WebService);
            var reading = readings.GetValueOrDefault(c.Name);
            return new ComposeMember(s, c.Name, c.State, c.Status, c.Image, c.Ports, s == app.WebService, reading?.MemoryBytes,
                reading is { Recent.Count: > 0 } r ? r.Recent[^1].Cpu : null);
        }).ToList();
    }

    /// <summary>Restarts one service of the app (docker compose restart &lt;service&gt;). Null when it worked, else why not.</summary>
    public async Task<string?> RestartServiceAsync(AppDefinition app, string service, CancellationToken ct = default)
    {
        if (!ComposeNames.IsServiceName(service)) return $"There's no service {service}.";
        var result = await docker.RunAsync(ComposeRunner.Args(app.ComposeProject, null, null, ["restart", "-t", "20", service]), _composer.Options(), ct);
        var ok = result.Ok;
        audit?.Record(new Audit.AuditEntry("app.restarted", ok ? $"Restarted {app.Name}'s {service} service." : $"Couldn't restart {app.Name}'s {service} service: {result.ErrorText.Trim()}")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = service,
            Outcome = ok ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
        });
        if (!ok) logger.LogInformation("Restarting {Service} of {App} failed: {Error}", service, app.Slug, result.ErrorText);
        return ok ? null : result.ErrorText.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "docker compose restart failed.";
    }

    /// <summary>An inline Compose app's file, as it's kept; "" when it can't be read (a lost key ring).</summary>
    public string YamlOf(AppDefinition app) => app.ComposeYamlProtected is { } stored ? protector.TryUnprotect(stored) : "";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
