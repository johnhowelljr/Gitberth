using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Previews;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Uptime;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>
/// What a person sees of each app: the Apps list, its page (settings with secrets as names only), its logs and stats, its
/// deploys and their logs, its previews. Every tool here reads; none changes anything.
/// </summary>
[McpServerToolType]
public sealed class AppReadTools(AgentContext ctx, AppCatalog catalog, AppSettingsService settings, AppService apps, DockerCli docker,
    ContainerStatsCollector stats, PreviewService previews, DatabaseService databases, DeployHooks hooks, CronScheduler crons,
    UpdateService updates, UptimeMonitor uptime, UptimeSettings uptimeSettings, ComposeApps compose, AppActions actions)
{
    [McpServerTool(Name = "list_apps", Title = "List apps", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Every app on this Gitberth host (Git apps, registry and local images, Compose apps, containers made by hand), as the Apps page lists them: " +
        "its key (use it to name the app in other tools), name, source, state, image or commit, address, uptime and whether an update is waiting. " +
        "Previews aren't listed here: list_previews lists an app's.")]
    public async Task<object> ListApps(
        [Description("Only apps whose name, key, image or repo contains this text (case-insensitive). Leave out for every app.")] string? filter = null,
        CancellationToken ct = default)
    {
        var list = await catalog.ListAsync(await ctx.LanHostAsync(ct), ct);
        var shown = list.Apps.Where(a => string.IsNullOrWhiteSpace(filter)
            || a.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || a.Image.Contains(filter, StringComparison.OrdinalIgnoreCase) || (a.Repo?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        return new
        {
            apps = shown.Select(a => new
            {
                key = a.Key,
                id = a.AppId,
                name = a.Title,
                source = a.Source.ToString(),
                state = a.State.ToString(),
                state_text = a.StateText,
                running = a.Running,
                image = a.Source == AppSource.Git ? null : a.Image,
                repo = a.Repo,
                address = Address(a),
                cpu_percent = a.CpuPercent is double c ? Math.Round(c, 1) : (double?)null,
                memory_mb = a.MemoryBytes is ulong m ? (long)(m / 1024 / 1024) : (long?)null,
                uptime = a.AppId is int id ? UptimeText(uptime.For(id)) : null,
                update = a.Update is { Available: true } u ? u.Text : null,
                system = a.System ? true : (bool?)null,
            }).ToList(),
            docker_error = list.DockerError,
        };
    }

    [McpServerTool(Name = "get_app", Title = "Get an app", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("Everything the app's page shows: state, source (repo, branch and commit, or image), addresses and domains, ports, environment variables " +
        "(secret ones by name only: their values are never returned), volumes, resources, restart policy, health check, build filters, previews, databases, " +
        "deploy hooks (names only), Compose services, cron jobs, update status, uptime, the down page, and the latest deploys.")]
    public async Task<object> GetApp(
        [Description("The app: its key from list_apps (gb-web), its name, or its number.")] string app,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var s = view.Summary;
        var row = view.Row;
        SettingsView? loaded = null;
        try
        {
            loaded = await settings.LoadAsync(s.Key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rest of the page still shows.
        }
        var secretKeys = loaded?.Options.SecretKeys ?? (IReadOnlyCollection<string>)[];
        var c = loaded?.Container;
        var id = s.AppId;
        return new
        {
            key = s.Key,
            id,
            name = s.Title,
            source = s.Source.ToString(),
            state = s.State.ToString(),
            state_text = s.StateText,
            running = s.Running,
            started_at = s.StartedAt,
            image = s.Image,
            origin = s.Origin.ToString(),
            container = s.Container,
            system = s.System ? true : (bool?)null,
            pinned = s.Pinned ? true : (bool?)null,
            address = Address(s),
            addresses = s.Chips.Select(ch => ch.Value ?? ch.Label).Distinct().ToList(),
            git = row is { Source: AppSource.Git } g ? new
            {
                // owner/repo on GitHub, group/subgroup/project on Gitberth's GitLab.
                host = g.FromGitLab ? "gitlab" : "github",
                repo = g.RepoFullName,
                branch = g.Branch,
                subdirectory = g.Subdirectory,
                commit = g.CurrentCommit is { Length: >= 7 } sha ? sha[..7] : g.CurrentCommit,
                auto_deploy = g.AutoDeploy,
                compose_file = g.ComposeFile,
                watch_paths = g.WatchPaths,
                ignored_paths = g.IgnoredPaths,
                settings_pending = g.ConfigChangePending || g.RebuildPending ? true : (bool?)null,
                // How its image is built: what Gitberth detects (Railpack for a project it doesn't know), always Railpack, or its own Dockerfile.
                build = g.DockerfileMode switch { DockerfileMode.Railpack => "railpack", DockerfileMode.Custom => "custom", _ => "auto" },
                detected = g.DetectedSummary,
            } : null,
            preview_of = row?.ParentAppId,
            web_port = loaded?.Options.WebPort,
            health_path = loaded?.Options.HealthCheckPath,
            ports = c?.Ports.Select(p => new { container_port = p.ContainerPort, protocol = p.Protocol, access = p.Access.ToString(), host_port = p.HostPort, host_ip = p.HostIp }).ToList(),
            domains = row?.Domains.OrderBy(d => d.Id).Select(d => new { hostname = d.Hostname, main = d.Main ? true : (bool?)null, @protected = d.Protected ? true : (bool?)null }).ToList(),
            env = c?.Env.Where(e => !e.FromImage).Select(e => EnvOut(e, secretKeys)).ToList(),
            env_from_image = c?.Env.Where(e => e.FromImage).Select(e => e.Key).ToList(),
            command = c?.Command is { } cmd ? string.Join(' ', Masking.Args(cmd, secretKeys)) : null,
            volumes = c?.Mounts.Select(m => new { kind = m.Kind.ToString(), source = m.Source, target = m.Target, read_only = m.ReadOnly ? true : (bool?)null }).ToList(),
            restart_policy = c?.Restart.Name,
            memory_mb = c?.Resources.MemoryBytes is long mem ? mem / 1024 / 1024 : (long?)null,
            cpus = c?.Resources.Cpus,
            updates = loaded?.Options.Updates.ToString(),
            notify = loaded?.Options.Notify,
            gather_stats = loaded?.Options.GatherStats,
            scheduled_restart = loaded?.Options.ScheduledRestart?.ToString(),
            previews = row is { Source: AppSource.Git, ParentAppId: null } p ? new
            {
                mode = p.PreviewMode.ToString(),
                limit = p.PreviewLimit,
                expiry_days = p.PreviewExpiryDays,
                forks = p.PreviewForks,
                protect = p.ProtectPreviews,
                databases = p.PreviewDatabases.ToString(),
                count = id is int pid ? await previews.CountAsync(pid, ct) : 0,
            } : null,
            compose_services = row is { IsCompose: true } ? await Try(async () => (await compose.MembersAsync(row, ct)).Select(m => new
            {
                service = m.Service, container = m.Container, state = m.State, status = m.Status, image = m.Image, ports = m.Ports, web = m.Web ? true : (bool?)null,
            }).ToList()) : null,
            databases = id is int did ? await Try(async () => (await databases.ListAsync(did, sizes: false, ct)).Select(d => new
            {
                id = d.Id, kind = d.Kind.ToString(), server = d.Server, name = d.Name, variable = d.Variable, problem = d.Problem,
            }).ToList()) : null,
            deploy_hooks = id is int hid ? await Try(async () => (await hooks.ListAsync(hid, ct)).Select(h => new { id = h.Id, name = h.Name, last_used_at = h.LastUsedAt }).ToList()) : null,
            cron_jobs = id is int cid ? await Try(async () => (await crons.GetAsync(cid, ct)).Jobs.Select(j => new
            {
                path = j.Path, schedule = j.Schedule, description = j.Description, next_run = j.NextRun,
                last = j.Last is { } l ? new { at = l.StartedAt, outcome = l.Outcome.ToString(), status = l.Status, detail = l.Detail } : null,
            }).ToList()) : null,
            update = s.Source is AppSource.Git or AppSource.Compose ? null : await Try(async () => (await updates.StatusAsync(s.Key, ct)) is var u
                ? new { available = u.Available, text = u.Text, tracked = u.Tracked, checked_at = u.CheckedAt, error = u.Error, can_roll_back = u.CanRollBack } : null),
            uptime = id is int uid ? UptimeOut(uptime.For(uid)) : null,
            down_page = id is int dpid && await uptimeSettings.OfAsync(dpid, ct) is { } dp
                ? new { on = dp.DownPage, message = dp.Message, uptime_check = dp.Uptime, uptime_path = dp.Path } : null,
            recent_deployments = view.Git?.Deployments.OrderByDescending(d => d.Id).Take(5).Select(DeploymentLine).ToList(),
            page = $"{await ctx.RootAsync(ct)}{s.Href}",
        };
    }

    [McpServerTool(Name = "get_app_logs", Title = "Get an app's logs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("The app's container logs (stdout and stderr, with timestamps), newest last, as its Logs tab shows them. " +
        "Filter with grep, limit with tail and since. A Compose app's logs are its web service's unless you name another service. " +
        "A line longer than 8 KB is cut, and at most about 1 MB comes back (the oldest lines go first): truncated says when.")]
    public async Task<object> GetAppLogs(
        [Description("The app: its key, name or number.")] string app,
        [Description("How many of the last lines to read (1 to 5000). Default 200.")] int tail = 200,
        [Description("Only lines since then: a duration like 10m, 2h or 1d, or a UTC time like 2026-11-15T10:00:00Z.")] string? since = null,
        [Description("Only lines that contain this text (case-insensitive), or match it as a regular expression when it starts and ends with / " +
            "(at most 200 characters; no backreferences or lookarounds).")] string? grep = null,
        [Description("A Compose app's service whose logs to read; leave out for its web service.")] string? service = null,
        CancellationToken ct = default)
    {
        var match = Matcher(grep);
        var view = await ctx.AppAsync(app, ct);
        var container = view.Summary.Container;
        if (!string.IsNullOrWhiteSpace(service))
        {
            if (view.Row is not { IsCompose: true } row) throw new ToolError($"{view.Summary.Title} isn't a Compose app: leave service out.");
            var members = await compose.MembersAsync(row, ct);
            container = members.FirstOrDefault(m => string.Equals(m.Service, service.Trim(), StringComparison.OrdinalIgnoreCase))?.Container
                ?? throw ToolError.NotFound($"{view.Summary.Title} has no service called {service}: it has {string.Join(", ", members.Select(m => m.Service))}.");
        }
        var args = new List<string> { "logs", "--timestamps", "--tail", Math.Clamp(tail, 1, 5000).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(since)) args.AddRange(["--since", Since(since)]);
        args.Add(container);
        // Matched and bounded as they come: what's kept never grows past the limits, whatever the container writes.
        var kept = new LogLines();
        var seen = 0;
        var r = await docker.RunAsync(args, new ProcessOptions
        {
            OnLine = line =>
            {
                Interlocked.Increment(ref seen);
                if (!ct.IsCancellationRequested && match(line)) kept.Add(line);
            },
            MaxCapture = 64 * 1024,
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Ok && seen == 0) throw ToolError.Conflict($"Docker couldn't read {container}'s logs: {r.ErrorText}");
        var shown = kept.Lines;
        return new { app = view.Summary.Title, container, lines = shown, count = shown.Count, truncated = kept.Truncated ? true : (bool?)null };
    }

    [McpServerTool(Name = "get_app_stats", Title = "Get an app's stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("The app's CPU, memory and network now, and its last hour in 5-minute averages, as its Overview's charts show them. " +
        "CPU is a percentage of one core (200 = two cores busy).")]
    public async Task<object> GetAppStats(
        [Description("The app: its key, name or number.")] string app,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var snapshot = stats.Current;
        var health = snapshot.Containers.FirstOrDefault(h => h.Name == view.Summary.Container);
        if (health is null)
            return new { app = view.Summary.Title, container = view.Summary.Container, note = snapshot.Error ?? "No readings: the app isn't running, or its stats are switched off." };
        var last = health.Recent.Count > 0 ? health.Recent[^1] : (StatsPoint?)null;
        var buckets = health.Recent.GroupBy(p => new DateTime(p.Time.Ticks - p.Time.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc))
            .Select(g => new
            {
                at = g.Key,
                cpu_percent = Math.Round(g.Average(p => p.Cpu), 1),
                memory_mb = g.Where(p => p.Memory is not null).Select(p => p.Memory!.Value).DefaultIfEmpty().Average() / 1024 / 1024 is var mb && mb > 0 ? Math.Round(mb, 1) : (double?)null,
            }).ToList();
        return new
        {
            app = view.Summary.Title,
            container = health.Name,
            state = health.State,
            sampled_at = snapshot.SampledAt,
            cpu_percent = last is { } l ? Math.Round(l.Cpu, 1) : (double?)null,
            memory_mb = health.MemoryBytes is ulong m ? Math.Round(m / 1024.0 / 1024.0, 1) : (double?)null,
            memory_limit_mb = health.MemoryLimit is ulong lim and > 0 ? Math.Round(lim / 1024.0 / 1024.0, 1) : (double?)null,
            network_in_kbps = last?.RxRate is double rx ? Math.Round(rx / 1024, 1) : (double?)null,
            network_out_kbps = last?.TxRate is double tx ? Math.Round(tx / 1024, 1) : (double?)null,
            gathered = health.Gathered,
            last_hour = buckets,
            host = snapshot.Host is { } h ? new { name = h.Name, cpus = h.Cpus, memory_mb = (long)(h.MemoryBytes / 1024 / 1024) } : null,
        };
    }

    [McpServerTool(Name = "list_deployments", Title = "List an app's deploys", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("A Git or Compose app's deploys, newest first: number, status, trigger, commit, who asked, and when. " +
        "get_deployment reads one with its log; rollback_app goes back to one that succeeded.")]
    public async Task<object> ListDeployments(
        [Description("The app: its key, name or number.")] string app,
        [Description("How many (1 to 100). Default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = AgentContext.DeployedRow(view, "have deploys");
        var detail = await apps.GetDetailAsync(row.Id, ct);
        // The images a rollback can go to, as the Activity tab offers them.
        var kept = await Try(async () => await actions.RollbackImagesAsync(row.Id, ct)) ?? new HashSet<string>();
        return new
        {
            app = view.Summary.Title,
            deployments = (detail?.Deployments ?? []).OrderByDescending(d => d.Id).Take(Math.Clamp(limit, 1, 100)).Select(d => new
            {
                id = d.Id,
                status = d.Status.ToString(),
                trigger = d.Trigger.ToString(),
                commit = d.CommitSha is { Length: > 0 } ? d.ShortSha : null,
                message = d.CommitMessage is { } msg ? FirstLine(msg) : null,
                image = d.ImageTag,
                requested_by = Audit.AuditActors.Parse(d.RequestedBy) is { } who ? Audit.AuditActors.Describe(who) : null,
                hook = d.HookName,
                error = d.Error is { } e ? FirstLine(e) : null,
                skipped = d.SkipReason,
                queued_at = d.QueuedAt,
                finished_at = d.FinishedAt,
                can_roll_back = Components.Apps.AppActivityTab.CanRollBackTo(d, row.CurrentImage, kept) ? true : (bool?)null,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "get_deployment", Title = "Get a deploy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("One deploy: its status, what it built, how it ended, and its log (secrets are masked in it). " +
        "The log is its last lines; grep picks lines from its last 5000. A deploy still running shows its log so far. " +
        "A line longer than 8 KB is cut, and at most about 1 MB comes back: truncated says when.")]
    public async Task<object> GetDeployment(
        [Description("The deploy's number (deployment_id from deploy_app or list_deployments).")] int deployment_id,
        [Description("How many of the log's last lines (1 to 5000). Default 100.")] int tail = 100,
        [Description("Only log lines that contain this text (case-insensitive), or match it as a regular expression when it starts and ends with / " +
            "(at most 200 characters; no backreferences or lookarounds).")] string? grep = null,
        CancellationToken ct = default)
    {
        var match = Matcher(grep);
        var dep = await ctx.DeploymentAsync(deployment_id, ct) ?? throw ToolError.NotFound($"There's no deploy #{deployment_id}.");
        var app = await apps.GetAsync(dep.AppId, ct);
        var lines = grep is null ? ctx.LogTail(dep.Id, tail) : Filter(ctx.LogTail(dep.Id, AgentContext.MaxLogLines), match, ct).TakeLast(Math.Clamp(tail, 1, AgentContext.MaxLogLines)).ToList();
        var result = AgentContext.DeployResult(dep, lines);
        return new { app = app?.Name, app_key = app?.ContainerName, deployment = result, page = $"{await ctx.RootAsync(ct)}apps/{dep.AppId}/deployments/{dep.Id}" };
    }

    [McpServerTool(Name = "list_previews", Title = "List an app's previews", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Read)]
    [Description("A Git app's preview deployments (one per branch or pull request, as its Previews tab lists them): key, branch or pull request, state, " +
        "commit, address, when it expires, and whether it can be promoted to production.")]
    public async Task<object> ListPreviews(
        [Description("The parent app: its key, name or number.")] string app,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = view.Row is { Source: AppSource.Git, ParentAppId: null } r ? r : throw ToolError.Conflict($"{view.Summary.Title} isn't a Git app with previews.");
        var rows = await previews.ListAsync(row.Id, await ctx.LanHostAsync(ct), ct);
        return new
        {
            app = view.Summary.Title,
            mode = row.PreviewMode.ToString(),
            previews = rows.Select(p => new
            {
                key = p.Key, id = p.AppId, name = p.Name, kind = p.Kind.ToString(), branch = p.Branch, pull_request = p.PullRequest, title = p.Title,
                state = p.State.ToString(), state_text = p.StateText, commit = p.Commit, message = p.CommitMessage is { } m ? FirstLine(m) : null,
                url = p.Url, domain = p.Domain, lan_url = p.LanUrl, last_activity_at = p.LastActivityAt, expires_at = p.ExpiresAt,
                deployment_id = p.DeploymentId, can_promote = p.CanPromote, from_fork = p.FromFork ? true : (bool?)null,
            }).ToList(),
        };
    }

    // ---- helpers ----

    internal static string? Address(AppSummary a) =>
        a.Chips.FirstOrDefault(ch => ch.Kind == Network.AddressChipKind.Open && ch.Value is not null)?.Value
        ?? a.Chips.FirstOrDefault(ch => ch.Value is not null)?.Value;

    internal static object EnvOut(EnvSetting e, IReadOnlyCollection<string> secretKeys)
    {
        var secret = e.Secret || Masking.IsSecret(e.Key, e.Value, secretKeys);
        return new
        {
            name = e.Key,
            value = secret ? null : e.Value,
            secret = secret ? true : (bool?)null,
            scope = e.Scope == EnvScope.All ? null : e.Scope.ToString(),
            build_time = e.BuildTime ? true : (bool?)null,
        };
    }

    internal static string? UptimeText(AppUptime u) => u.Status switch
    {
        UptimeStatus.Up => "up",
        UptimeStatus.Down => "down",
        UptimeStatus.Paused => "paused",
        UptimeStatus.Unknown => null,
        _ => "not checked",
    };

    internal static object UptimeOut(AppUptime u) => new
    {
        status = u.Status.ToString(),
        address = u.Address,
        why = u.Why,
        last = u.Last is { } l ? new { at = l.At, up = l.Up, http_status = l.Status, ms = l.Ms, problem = l.Problem } : null,
        down_since = u.Open?.Started,
        down_cause = u.Open?.Cause,
        certificate_expires = u.CertificateExpires,
    };

    internal static object DeploymentLine(Deployment d) => new
    {
        id = d.Id,
        status = d.Status.ToString(),
        trigger = d.Trigger.ToString(),
        commit = d.CommitSha is { Length: > 0 } ? d.ShortSha : null,
        queued_at = d.QueuedAt,
        finished_at = d.FinishedAt,
        error = d.Error is { } e ? FirstLine(e) : null,
    };

    internal static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    /// <summary>The longest grep a tool takes, in characters.</summary>
    internal const int MaxGrep = 200;

    /// <summary>
    /// A grep: text (case-insensitive), or a regular expression between slashes. The expression runs without backtracking,
    /// so its time grows with the line's length, never exponentially (a pattern like (.+)+! is as quick as any), and it still
    /// gives up on a line after 200 ms.
    /// </summary>
    internal static Func<string, bool> Matcher(string? grep)
    {
        if (string.IsNullOrEmpty(grep)) return _ => true;
        if (grep.Length > MaxGrep) throw new ToolError($"grep is at most {MaxGrep} characters.");
        if (grep.Length > 2 && grep[0] == '/' && grep[^1] == '/')
        {
            Regex regex;
            try
            {
                regex = GrepRegex(grep[1..^1]);
            }
            catch (NotSupportedException)
            {
                throw new ToolError("grep's regular expression can't use backreferences, lookarounds, atomic groups or conditionals here.");
            }
            catch (ArgumentException ex)
            {
                throw new ToolError($"grep isn't a regular expression .NET can read: {ex.Message}");
            }
            return line =>
            {
                try { return regex.IsMatch(line); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }
        return line => line.Contains(grep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A grep's regular expression (what's between its slashes): case-insensitive, without backtracking, 200 ms a line at most.</summary>
    internal static Regex GrepRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));

    /// <summary>The lines a grep matches, stopping as soon as the call is cancelled (its caller hung up).</summary>
    internal static List<string> Filter(IEnumerable<string> lines, Func<string, bool> match, CancellationToken ct)
    {
        var kept = new List<string>();
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (match(line)) kept.Add(line);
        }
        return kept;
    }

    /// <summary>docker logs' --since: a duration (10m, 2h, 1d) or a time, checked first.</summary>
    internal static string Since(string since)
    {
        since = since.Trim();
        var m = Regex.Match(since, @"^(\d{1,6})\s*([smhd])$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value.ToLowerInvariant() == "d" ? $"{n * 24}h" : $"{n}{m.Groups[2].Value.ToLowerInvariant()}";
        }
        if (DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return at.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        throw new ToolError("since is a duration like 10m, 2h or 1d, or a UTC time like 2026-11-15T10:00:00Z.");
    }

    private static async Task<T?> Try<T>(Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
