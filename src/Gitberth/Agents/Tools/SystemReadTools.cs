using System.ComponentModel;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Public;
using Gitberth.Services;
using Gitberth.Templates;
using Gitberth.Uptime;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>
/// What a person sees across the host: its health, every domain, uptime and the status page, the audit log, databases,
/// updates waiting, and Gitberth's own settings (never a secret). Every tool here reads; none changes anything.
/// </summary>
[McpServerToolType]
public sealed class SystemReadTools(AgentContext ctx, AppCatalog catalog, DockerCli docker, ContainerStatsCollector stats, ImageService images,
    DomainService domains, UptimeMonitor uptime, UptimeStore uptimeStore, UptimeSettings uptimeSettings, StatusPage statusPage, AuditLog audit,
    AuditSettings auditSettings, DatabaseService databases, DatabaseServers servers, NotificationSettings notifications, SettingsService settings,
    IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, TemplateCatalog templates)
{
    [McpServerTool(Name = "get_health", Title = "Get the host's health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("How this host is doing: the Docker engine (version, CPUs, memory), the disk Docker uses, Gitberth's version, " +
        "and the problems worth knowing about now: apps crashed, unhealthy, missing or down, deploys failing, a full disk.")]
    public async Task<object> GetHealth(CancellationToken ct = default)
    {
        var info = await docker.GetInfoAsync(ct);
        var snapshot = stats.Current;
        var disk = images.Disk();
        var list = await catalog.ListAsync(await ctx.LanHostAsync(ct), ct);
        var problems = new List<string>();
        if (info.ServerVersion is null) problems.Add($"Docker isn't reachable: {info.Error}");
        if (list.DockerError is { } dockerError) problems.Add($"Apps couldn't all be read from Docker: {dockerError}");
        foreach (var a in list.Apps)
        {
            if (a.State is AppState.Crashed or AppState.Unhealthy or AppState.Missing or AppState.Dead or AppState.NeedsDecision or AppState.Renamed or AppState.Degraded)
                problems.Add($"{a.Title} ({a.Key}): {a.StateText}");
            if (a.AppId is int id && uptime.For(id) is { Status: UptimeStatus.Down } down)
                problems.Add($"{a.Title} is down from outside since {down.Open?.Started:u}: {down.Open?.Cause ?? down.Last?.Problem}");
        }
        if (disk.TotalBytes > 0 && disk.TotalBytes < long.MaxValue / 8 && disk.FreeBytes < disk.TotalBytes / 10)
            problems.Add($"Docker's disk is {100 - disk.FreeBytes * 100 / disk.TotalBytes}% full: {disk.FreeBytes / 1024 / 1024 / 1024} GB free.");
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var since = DateTime.UtcNow.AddHours(-24);
            var failed = await db.Deployments.AsNoTracking().Where(d => d.Status == DeployStatus.Failed && d.FinishedAt >= since).CountAsync(ct);
            if (failed > 0) problems.Add($"{failed} deploy{(failed == 1 ? "" : "s")} failed in the last 24 hours (list_deployments has them).");
        }
        return new
        {
            gitberth = new { version = AppVersion.Display, data_folder = options.DataDir },
            docker = new { server_version = info.ServerVersion, client_version = info.ClientVersion, error = info.Error },
            host = snapshot.Host is { } h ? new { name = h.Name, cpus = h.Cpus, memory_mb = (long)(h.MemoryBytes / 1024 / 1024) } : null,
            disk = disk.TotalBytes > 0 && disk.TotalBytes < long.MaxValue / 8
                ? new { free_gb = Math.Round(disk.FreeBytes / 1024.0 / 1024 / 1024, 1), total_gb = Math.Round(disk.TotalBytes / 1024.0 / 1024 / 1024, 1) } : null,
            apps = new
            {
                total = list.Apps.Count,
                running = list.Apps.Count(a => a.Running),
                stopped = list.Apps.Count(a => a.State == AppState.Stopped),
            },
            containers_sampled_at = snapshot.SampledAt,
            problems,
        };
    }

    [McpServerTool(Name = "list_domains", Title = "List domains", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("The domains apps are served on through the Cloudflare Tunnel: which app, main or not, protected by Cloudflare Access or not. " +
        "Name an app to have each of its domains checked as its Domains card does (working or not, and why).")]
    public async Task<object> ListDomains(
        [Description("Only this app's domains, checked live: its key, name or number. Leave out for every domain.")] string? app = null,
        CancellationToken ct = default)
    {
        var enabled = await domains.EnabledAsync(ct);
        if (app is not null)
        {
            var view = await ctx.AppAsync(app, ct);
            if (view.Summary.AppId is not int id) return new { enabled, app = view.Summary.Title, domains = Array.Empty<object>() };
            var overview = await domains.GetOverviewAsync(id, ct);
            return new
            {
                enabled = overview.Enabled,
                app = view.Summary.Title,
                problem = overview.Problem,
                protection_problem = overview.ProtectionProblem,
                domains = overview.Domains.Select(d => new
                {
                    hostname = d.Hostname, working = d.Working, summary = d.Summary, warning = d.Warning,
                    main = d.Main ? true : (bool?)null, @protected = d.Protected ? true : (bool?)null, down_page = d.DownPage ? true : (bool?)null,
                }).ToList(),
            };
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Domains.AsNoTracking().OrderBy(d => d.Hostname).ToListAsync(ct);
        var ids = rows.Select(d => d.AppId).ToHashSet();
        var apps = await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return new
        {
            enabled,
            webhook_host = await domains.WebhookHostAsync(ct),
            domains = rows.Select(d => new
            {
                hostname = d.Hostname,
                app = apps.GetValueOrDefault(d.AppId)?.Name,
                app_key = apps.GetValueOrDefault(d.AppId)?.ContainerName,
                main = d.Main ? true : (bool?)null,
                @protected = d.Protected ? true : (bool?)null,
                preview = apps.GetValueOrDefault(d.AppId)?.IsPreview == true ? true : (bool?)null,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "get_uptime", Title = "Get uptime", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Uptime checks, as the app's Uptime card shows them: up or down now, the last check, uptime over 24 hours, 7 and 30 days, and incidents. " +
        "Without an app: every app's state now and the public status page's data (when it's on).")]
    public async Task<object> GetUptime(
        [Description("The app: its key, name or number. Leave out for every app and the status page.")] string? app = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (app is not null)
        {
            var view = await ctx.AppAsync(app, ct);
            var id = view.Summary.AppId ?? throw ToolError.Conflict($"{view.Summary.Title} isn't checked: it has nothing stored in Gitberth yet.");
            var hours = await uptimeStore.HoursAsync([id], now.AddDays(-30), ct);
            double? Over(TimeSpan window)
            {
                var from = UptimeStore.HourOf(now - window);
                var inWindow = hours.Where(h => h.Hour >= from).ToList();
                var checks = inWindow.Sum(h => h.Checks);
                return checks == 0 ? null : Math.Round(100.0 * inWindow.Sum(h => h.Ups) / checks, 3);
            }
            var incidents = await uptimeStore.IncidentsAsync(id, now.AddDays(-30), ct);
            return new
            {
                app = view.Summary.Title,
                now = AppReadTools.UptimeOut(uptime.For(id)),
                interval_seconds = await uptime.IntervalAsync(ct),
                uptime_24h = Over(TimeSpan.FromHours(24)),
                uptime_7d = Over(TimeSpan.FromDays(7)),
                uptime_30d = Over(TimeSpan.FromDays(30)),
                incidents = incidents.OrderByDescending(i => i.Started).Take(20)
                    .Select(i => new { started = i.Started, ended = i.Ended, minutes = Math.Round(i.Duration(now).TotalMinutes, 1), cause = i.Cause }).ToList(),
            };
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null).OrderBy(a => a.Name).ToListAsync(ct);
        var page = await statusPage.BuildAsync(ct);
        return new
        {
            apps = rows.Select(a => (a, u: uptime.For(a.Id))).Select(x => new
            {
                app = x.a.Name, key = x.a.ContainerName, status = x.u.Status.ToString(), address = x.u.Address, why = x.u.Why, down_since = x.u.Open?.Started,
            }).ToList(),
            status_page = page is null ? null : new
            {
                title = page.Title,
                overall = page.Overall.ToString(),
                apps = page.Apps.Select(a => new { name = a.Name, state = a.State.ToString(), uptime_90d = a.Uptime90 is double u ? Math.Round(u, 3) : (double?)null, response_ms = a.ResponseMs }).ToList(),
                incidents = page.Incidents.Select(i => i.Summary).ToList(),
            },
        };
    }

    [McpServerTool(Name = "query_audit_log", Title = "Query the audit log", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("The audit log: every change to this Gitberth, who made it (a person, a token, a hook, GitHub, Gitberth itself, or something outside Gitberth), " +
        "how and with what outcome, newest first, with the same filters as its page. Page with before_id (the last id you got).")]
    public async Task<object> QueryAuditLog(
        [Description("How far back: day, week (default), month or all.")] string? range = null,
        [Description("Who made the changes, as kind:name, like User:admin, ApiToken:my-agent, DeployHook:cms, GitHub:GitHub, Docker:outside Gitberth.")] string? who = null,
        [Description("Only this app's entries: its key, name or number.")] string? app = null,
        [Description("Only one part: apps, deploys, domains, settings, signins or outside (Docker changes made outside Gitberth).")] string? area = null,
        [Description("Only entries that ended so: succeeded, failed or denied.")] string? outcome = null,
        [Description("Words in the summary, the target, the actor, the app or the action.")] string? text = null,
        [Description("How many entries (1 to 500). Default 50.")] int limit = 50,
        [Description("For the next page: only entries numbered below this one.")] long? before_id = null,
        CancellationToken ct = default)
    {
        string? container = null;
        int? appId = null;
        if (app is not null)
        {
            var view = await ctx.AppAsync(app, ct);
            (container, appId) = (view.Summary.Container, view.Summary.AppId);
        }
        var filter = AuditFilter.Parse(range, who, container, area, outcome, text);
        var query = filter.ToQuery(DateTime.UtcNow, Math.Clamp(limit, 1, 500), appId) with { BeforeId = before_id };
        var page = await audit.QueryAsync(query, ct);
        return new
        {
            entries = page.Entries.Select(e => new
            {
                id = e.Id,
                at = e.At,
                who = AuditActors.Describe(e.Actor),
                who_kind = e.Actor.Kind.ToString(),
                via = e.Actor.Via.ToString(),
                ip = e.Actor.Ip,
                action = e.Action,
                outcome = e.Outcome.ToString(),
                summary = e.Summary,
                app = e.AppName,
                app_key = e.Container,
                target = e.Target,
                changes = e.Changes.Count == 0 ? null : e.Changes.Select(c => new { field = c.Field, from = c.From, to = c.To }).ToList(),
                deployment_id = e.DeploymentId,
            }).ToList(),
            more = page.More,
            next_before_id = page.More && page.Entries.Count > 0 ? page.Entries[^1].Id : (long?)null,
        };
    }

    [McpServerTool(Name = "list_databases", Title = "List databases", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Apps' databases (Postgres databases and Redis that Gitberth made or took over): kind, server, name, the variable their connection string is set in, and size. " +
        "Never a connection string or a password. Without an app: the Postgres servers on this host and every app's databases.")]
    public async Task<object> ListDatabases(
        [Description("Only this app's databases: its key, name or number.")] string? app = null,
        CancellationToken ct = default)
    {
        if (app is not null)
        {
            var view = await ctx.AppAsync(app, ct);
            if (view.Summary.AppId is not int id) return new { app = view.Summary.Title, databases = Array.Empty<object>() };
            var rows = await databases.ListAsync(id, sizes: true, ct);
            return new
            {
                app = view.Summary.Title,
                databases = rows.Select(d => new
                {
                    id = d.Id, kind = d.Kind.ToString(), server = d.Server, name = d.Name, variable = d.Variable,
                    size_mb = d.SizeBytes is long b ? Math.Round(b / 1024.0 / 1024, 1) : (double?)null,
                    made_by_gitberth = d.Created, problem = d.Problem,
                }).ToList(),
            };
        }
        var list = await servers.ListAsync(ct);
        var chosen = (await servers.DefaultAsync(list, ct))?.Container;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.ManagedDatabases.AsNoTracking().OrderBy(d => d.AppId).ThenBy(d => d.Id).ToListAsync(ct);
        var ids = all.Select(d => d.AppId).ToHashSet();
        var names = await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return new
        {
            servers = list.Select(s => new
            {
                container = s.Container, image = s.Image, alias = s.Alias, running = s.Running, version = s.Version, usable = s.Usable,
                @default = s.Container == chosen ? true : (bool?)null, problem = s.Problem,
            }).ToList(),
            databases = all.Select(d => new
            {
                id = d.Id, app = names.GetValueOrDefault(d.AppId), app_id = d.AppId, kind = d.Kind.ToString(), server = d.Server, name = d.Name, variable = d.Variable,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "list_updates", Title = "List updates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Apps that run a registry's image, and whether a newer image is waiting for each, as Gitberth last checked (check_updates asks the registry again; apply_update updates one).")]
    public async Task<object> ListUpdates(CancellationToken ct = default)
    {
        var list = await catalog.ListAsync(await ctx.LanHostAsync(ct), ct);
        return new
        {
            apps = list.Apps.Where(a => a.Update is not null).Select(a => new
            {
                key = a.Key, name = a.Title, image = a.Image, available = a.Update!.Available, text = a.Update.Text, checked_at = a.Update.CheckedAt, error = a.Update.Error,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "list_images", Title = "List images", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("The images on this host with a name, newest first, as New app's local images list them: tags, size, age, how many containers use each, " +
        "and whether Gitberth built it (a Git app's build).")]
    public async Task<object> ListImages(
        [Description("Only images whose tag contains this text (case-insensitive).")] string? filter = null,
        CancellationToken ct = default)
    {
        var list = await images.ListLocalAsync(ct);
        return new
        {
            images = list.Where(i => string.IsNullOrWhiteSpace(filter) || i.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase))).Select(i => new
            {
                id = i.Id.StartsWith("sha256:", StringComparison.Ordinal) && i.Id.Length > 19 ? i.Id[7..19] : i.Id,
                tags = i.Tags,
                created = i.Created,
                size_mb = Math.Round(i.SizeBytes / 1024.0 / 1024, 1),
                containers = i.Containers,
                built_by_gitberth = i.BuiltByGitberth ? true : (bool?)null,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "list_templates", Title = "List app templates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Gitberth's app templates, as New app's Templates gallery shows them: what each one is, what it needs (a Postgres server, Redis, memory), " +
        "and the inputs create_app takes for it (source template). The secrets it gets are made when it's installed, and never shown.")]
    public Task<object> ListTemplates(
        [Description("Only templates with every word of this in their name, description, category or id (any case).")] string? query = null,
        [Description("Only this category, like Monitoring (categories lists them).")] string? category = null)
    {
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(category))
            chosen = templates.Categories.FirstOrDefault(c => string.Equals(c, category.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolError($"There's no category {category.Trim()}: they're {string.Join(", ", templates.Categories)}.");
        return Task.FromResult<object>(new
        {
            categories = templates.Categories,
            templates = templates.Search(query, chosen).Select(t => new
            {
                id = t.Id, name = t.Name, tagline = t.Tagline, description = t.Description, category = t.Category, website = t.Website, license = t.License,
                needs = new { databases = t.DatabasesText, postgres_server = t.NeedsPostgres ? true : (bool?)null, memory = t.MemoryText },
                web = new { service = t.Web.Service, port = t.Web.Port },
                services = t.Services,
                images = t.Images,
                latest_only = t.LatestOnly is not null ? true : (bool?)null,
                // Whoever opens it first sets it up: a domain at install goes behind Cloudflare Access, or waits until a person has.
                open_until_setup = t.OpenUntilSetup ? true : (bool?)null,
                inputs = t.Inputs.Select(i => new
                {
                    name = i.Name, label = i.Label, help = i.Help, kind = i.Kind.ToString().ToLowerInvariant(), required = i.Required, @default = i.Default,
                }).ToList(),
                generated_secrets = t.Secrets.Select(s => s.Name).ToList(),
            }).ToList(),
        });
    }

    [McpServerTool(Name = "get_settings", Title = "Get Gitberth's settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Gitberth's own settings, as its Settings page shows them, without a secret: notifications (where to by name and kind only, and about what), " +
        "updates, domains, uptime checks and the status page, deploys, the audit log, and the version.")]
    public async Task<object> GetSettings(CancellationToken ct = default)
    {
        var status = await uptimeSettings.StatusAsync(ct);
        var window = await settings.GetAsync(UpdateWatcher.AutoWindowKey, ct) ?? "03:00-05:00";
        return new
        {
            version = AppVersion.Display,
            address = await ctx.RootAsync(ct),
            notifications = new
            {
                destinations = notifications.Destinations.Select(d => new { name = d.Name, kind = d.Kind.ToString() }).ToList(),
                events = NotificationSettings.Choosable.ToDictionary(e => e.ToString(), notifications.IsOn),
                previews = notifications.Previews,
            },
            updates = new
            {
                check_every_hours = await settings.GetIntAsync(UpdateWatcher.CheckHoursKey, 6, ct),
                automatic_window = window,
                time_zone = UpdateWatcher.ZoneName(),
                default_policy = await settings.GetAsync(UpdateWatcher.DefaultPolicyKey, ct) ?? UpdatePolicy.Notify.ToString(),
            },
            domains = new
            {
                enabled = await domains.EnabledAsync(ct),
                cloudflare_token_saved = await domains.TokenSavedAsync(ct),
                webhook_host = await domains.WebhookHostAsync(ct),
            },
            uptime = new { interval_seconds = await uptimeSettings.IntervalAsync(ct) },
            status_page = new { enabled = status.Enabled, title = status.Title, description = status.Description, apps = status.Apps, response_times = status.ResponseTimes, host = status.Host },
            deploys = new { check_for_commits_every_seconds = await settings.GetIntAsync(SettingKeys.WatchIntervalSeconds, RepoWatcher.DefaultIntervalSeconds, ct) },
            github = new
            {
                token_saved = await settings.GetAsync(SettingKeys.GitHubToken, ct) is not null,
                login = await settings.GetAsync(SettingKeys.GitHubLogin, ct),
                commit_statuses = await settings.GetAsync(SettingKeys.GitHubStatuses, ct) != "false",
                pull_request_comments = await settings.GetAsync(SettingKeys.GitHubComments, ct) != "false",
            },
            audit_log = new { retention_days = auditSettings.RetentionDays, record_outside_changes = auditSettings.RecordOutside },
            lan_address = await settings.GetAsync(SettingKeys.PublicHost, ct),
            network = options.Network,
        };
    }
}
