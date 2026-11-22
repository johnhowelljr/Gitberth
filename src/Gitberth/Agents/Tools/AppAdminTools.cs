using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Components.Shared.Network;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;
using Gitberth.Sources;
using Gitberth.Templates;
using Gitberth.Uptime;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>A variable for set_env_vars and create_app.</summary>
public sealed record EnvVarInput(
    [property: Description("The variable's name, like DATABASE_URL.")] string Name,
    [property: Description("Its value. A secret's value is written and never read back.")] string Value,
    [property: Description("Mask it everywhere and keep it out of logs. A password-like name or value is secret anyway. A Git app's RAILPACK_ variable " +
        "can't be secret (Railpack writes it into its plan and the image): name the secret as $NAME in it, from a variable of its own.")] bool? Secret = null,
    [property: Description("Git apps: which deploys get it: all (default), production or preview.")] string? Scope = null,
    [property: Description("Git apps: pass it to the build too.")] bool? BuildTime = null);

/// <summary>A port for update_app_settings: the whole list replaces the app's ports.</summary>
public sealed record PortInput(
    [property: Description("The port inside the container.")] int ContainerPort,
    [property: Description("lan (every device on the network, the default), host_only (127.0.0.1 on the host) or internal (other containers only).")] string? Access = null,
    [property: Description("The port on the host; leave out for a free one (lan and host_only).")] int? HostPort = null,
    [property: Description("tcp (default) or udp.")] string? Protocol = null,
    [property: Description("The web port: the one Open, domains and the health check use. At most one, TCP.")] bool? Web = null);

/// <summary>
/// Creating, changing and deleting apps, as New app and an app's Settings, Environment and Network tabs do: each through
/// the same service, with the same checks. Admin access.
/// </summary>
[McpServerToolType]
public sealed class AppAdminTools(AgentContext ctx, AppCatalog catalog, AppService apps, AppActions actions, AppSettingsService settings,
    Lazy<AppCreator> creator, ComposeApps compose, GitClient git, ProjectAnalyzer analyzer, RepoVariables repoVariables, PreviewService previews,
    DatabaseService databases, DeployHooks hooks, UptimeSettings uptimeSettings, DomainService domains, GitberthOptions options,
    TemplateCatalog templates, TemplateInstaller installer, GitLabApi? gitLab = null, Railpack.IRailpackPlanner? railpack = null)
{
    private static readonly Railpack.NoRailpack NoPlanner = new();

    [McpServerTool(Name = "create_app", Title = "Create an app", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Creates an app as New app does, checked the same way, and deploys or starts it. Sources: " +
        "git (a GitHub repo, or a project on Gitberth's GitLab (Settings › GitLab: gitlab.com unless another is set), groups and subgroups too, " +
        "built with what Gitberth detects (Railpack builds a project Gitberth doesn't know), or with its Compose file when it has one: give repo, " +
        "and maybe branch, subdirectory and build); " +
        "image (a registry's image like nginx:1.27 or ghcr.io/owner/app:latest: give image); " +
        "compose (a whole Compose file deployed as one app: give compose_yaml, and maybe web_service); " +
        "template (one of Gitberth's app templates, as list_templates lists them: give template, its inputs, and maybe a domain; its databases and secrets " +
        "are made as its page in New app makes them, and a template that needs Postgres needs a Postgres server first: create_postgres_server). " +
        "Returns the app's key, and the deployment_id of a Git, Compose or template app's first deploy (wait follows it).")]
    public async Task<object> CreateApp(
        [Description("git, image, compose or template.")] string source,
        [Description("The app's name; leave out to take the repo's, the image's or the Compose file's.")] string? name = null,
        [Description("git: the repository, like https://github.com/owner/repo or https://gitlab.com/group/subgroup/project (a link to a branch or folder works too).")] string? repo = null,
        [Description("git: the branch to deploy; the repo's default when left out.")] string? branch = null,
        [Description("git: the folder the app is in, when it isn't the repo's root.")] string? subdirectory = null,
        [Description("git: true to deploy with the repo's Compose file, false to build one container; leave out to choose as New app does.")] bool? use_compose = null,
        [Description("git: auto (the default: what Gitberth detects, and Railpack, Railway's builder, for a project Gitberth doesn't know; Railpack when " +
            "the repo has a railpack.json and no Dockerfile) or railpack (always Railpack).")] string? build = null,
        [Description("image: the image reference, like nginx:1.27.")] string? image = null,
        [Description("compose: the whole Compose file (YAML).")] string? compose_yaml = null,
        [Description("git with Compose, or compose: the service that's the app's web service (domains, LAN port, health check); leave out to pick as New app does.")] string? web_service = null,
        [Description("Variables to set (Git and Compose apps: the Compose file's variables too).")] IReadOnlyList<EnvVarInput>? env = null,
        [Description("template: the template's id, as list_templates gives it (like uptime-kuma).")] string? template = null,
        [Description("template: its inputs by name, as list_templates lists them, like {\"ADMIN_USER\": \"admin\"}. An input left out takes its default.")]
        IReadOnlyDictionary<string, JsonElement>? inputs = null,
        [Description("template: a domain for it, like wiki.example.com, when domains are on (Settings › Domains). Its address variables (APP_URL) follow " +
            "its main domain, else its LAN address. A template whose first visitor sets it up (open_until_setup in list_templates) takes a domain only " +
            "behind Cloudflare Access (Settings › Protection), until a person turns that off.")] string? domain = null,
        [Description("Wait for the first deploy or start to end (up to 10 minutes).")] bool wait = false,
        CancellationToken ct = default)
    {
        // Each source is New app's, through the same services and checks.
        var kind = source?.Trim().ToLowerInvariant();
        if (kind != "template" && (template is not null || inputs is not null || domain is not null))
            throw new ToolError("template, inputs and domain go with source template. Any other app gets a domain once it's made, with add_domain.");
        var fromGit = kind is "git" or "github" or "gitlab" or "repo";
        if (!fromGit && build is not null) throw new ToolError("build goes with source git: it's how a repo's image is built.");
        var alwaysRailpack = build?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => false,
            "railpack" => true,
            _ => throw new ToolError("build is auto or railpack."),
        };
        return kind switch
        {
            "git" or "github" or "gitlab" or "repo" => await CreateFromGitAsync(name, repo, branch, subdirectory, use_compose, alwaysRailpack, web_service, env, wait, ct),
            "image" or "registry" => await CreateFromImageAsync(name, image, env, wait, ct),
            "compose" => await CreateFromComposeAsync(name, compose_yaml, web_service, env, wait, ct),
            "template" => await CreateFromTemplateAsync(name, template, inputs, domain, env, wait, ct),
            _ => throw new ToolError("source is git, image, compose or template."),
        };
    }

    [McpServerTool(Name = "delete_app", Title = "Delete an app", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Deletes an app as its page's Delete does: its container goes, its domains come out of the tunnel, and (when asked) its images, volumes and databases' data. " +
        "A preview goes with everything of its own. Pass confirm with the app's name. This can't be undone.")]
    public async Task<object> DeleteApp(
        [Description("The app: its key, name or number.")] string app,
        [Description("The app's name, repeated, to show the delete is meant.")] string? confirm = null,
        [Description("Also delete its images.")] bool remove_images = false,
        [Description("Also delete its volumes (their data is gone for good).")] bool remove_volumes = false,
        [Description("Also delete its databases' data (Settings › Databases). A preview's always go.")] bool remove_databases = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var s = view.Summary;
        AgentContext.Confirm(confirm, s, "delete");
        if (AppCommands.Refusal(s, ContainerAction.Remove) is { } refusal) throw ToolError.Conflict(refusal);
        if (view.Row is { IsPreview: true } preview)
        {
            await previews.RemoveAsync(preview.Id, ct);
            return new { deleted = s.Title, message = $"Removed the preview {s.Title}: a new commit on its branch or pull request makes it again." };
        }
        if (view.Row is { Deployed: true } row)
        {
            var report = await actions.DeleteAsync(row.Id, remove_images, remove_volumes, remove_databases);
            var trouble = report.Notes.Where(n => n.Problem).Select(n => n.Text).ToList();
            return new
            {
                deleted = s.Title,
                message = trouble.Count > 0 ? $"Deleted {s.Title}. Its domains couldn't all be taken out of the tunnel yet: Gitberth tries again." : $"Deleted {s.Title}.",
                notes = trouble.Count > 0 ? trouble : null,
            };
        }
        var result = await actions.RemoveAsync(s.Key, new RemoveOptions(remove_volumes, remove_images, remove_databases), ct);
        if (!result.Ok) throw ToolError.Conflict(result.Message);
        return new { deleted = s.Title, message = result.Message };
    }

    [McpServerTool(Name = "set_env_vars", Title = "Set environment variables", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Sets and removes an app's environment variables, as its Environment tab does, and applies them: a Git app is redeployed without a build " +
        "(or rebuilt when a build-time variable changed), any other app's container is recreated with them. Secret values are written and never returned. " +
        "Variables Gitberth sets from the app's databases aren't changed here.")]
    public async Task<object> SetEnvVars(
        [Description("The app: its key, name or number.")] string app,
        [Description("Variables to add or change.")] IReadOnlyList<EnvVarInput>? set = null,
        [Description("Names of variables to remove (every scope of the name, unless remove_scope says one).")] IReadOnlyList<string>? remove = null,
        [Description("Git apps: only remove the names' values for this scope: all, production or preview.")] string? remove_scope = null,
        [Description("Git apps: save them for the next deploy instead of deploying now.")] bool next_deploy = false,
        [Description("Wait for the change (or its deploy) to end, up to 10 minutes.")] bool wait = false,
        CancellationToken ct = default)
    {
        if ((set?.Count ?? 0) == 0 && (remove?.Count ?? 0) == 0) throw new ToolError("Give variables to set, or names to remove.");
        var view = await ctx.AppAsync(app, ct);
        var loaded = await LoadAsync(view, ct);
        if (loaded.Access.ReadOnly.Contains(SettingField.Env)) throw ToolError.Conflict(loaded.Access.Recreate ?? $"Gitberth doesn't change {view.Summary.Title}'s environment.");
        var isGit = view.Summary.Source == AppSource.Git;
        var managed = view.Summary.AppId is int appId ? (await databases.RowsAsync(appId, ct)).Select(d => d.Variable).ToHashSet(StringComparer.Ordinal) : [];
        var draft = ContainerSettingsDraft.From(loaded.Container, loaded.Options.SecretKeys);
        foreach (var v in set ?? [])
        {
            var key = v.Name?.Trim() ?? "";
            if (key.Length == 0) throw new ToolError("A variable needs a name.");
            if (!isGit && managed.Contains(key)) throw ToolError.Conflict($"Gitberth sets {key} from {view.Summary.Title}'s database: remove the database to change it.");
            var scope = Scope(v.Scope, isGit);
            if (draft.Env.FirstOrDefault(r => !r.Removed && r.Key == key && r.Scope == scope) is { } row)
            {
                row.Value = v.Value ?? "";
                row.FromImage = false;
            }
            else
            {
                row = draft.AddEnv(key, v.Value ?? "");
                row.Scope = scope;
            }
            if (v.Secret is bool secret) row.Secret = secret;
            if (v.BuildTime is bool build)
            {
                if (!isGit && build) throw new ToolError($"Only Git apps have a build: {key} can't be build-time on {view.Summary.Title}.");
                row.BuildTime = build;
            }
        }
        var removeScope = remove_scope is null ? (EnvScope?)null : Scope(remove_scope, isGit);
        foreach (var name in remove ?? [])
        {
            var key = name.Trim();
            if (!isGit && managed.Contains(key)) throw ToolError.Conflict($"Gitberth sets {key} from {view.Summary.Title}'s database: remove the database instead.");
            var rows = draft.Env.Where(r => !r.Removed && r.Key == key && (removeScope is null || r.Scope == removeScope)).ToList();
            if (rows.Count == 0) throw ToolError.NotFound($"{view.Summary.Title} has no variable {key}{(removeScope is { } sc ? $" for {EnvScopes.Label(sc)}" : "")}.");
            foreach (var r in rows) draft.RemoveEnv(r);
        }
        if (draft.Problems() is { Count: > 0 } problems) throw new ToolError(string.Join(" ", problems));
        var optionsDraft = AppOptionsDraft.From(loaded.Options);
        var edit = new SettingsEdit(loaded.ContainerId, loaded.Container, draft.ToSettings(), loaded.Options,
            optionsDraft.ToOptions() with { SecretKeys = draft.SecretKeys(loaded.Options.SecretKeys) }, loaded.Git, loaded.Git);
        return await ApplyEditAsync(view, edit, next_deploy ? ApplyTiming.NextDeploy : ApplyTiming.Now, wait, ct);
    }

    [McpServerTool(Name = "update_app_settings", Title = "Change an app's settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Changes an app's settings, as its Settings and Network tabs do, and applies them the way the page would (live where Docker can, else a redeploy or a recreate): " +
        "name, health check path, memory and CPU limits, restart policy, updates policy, a Git app's auto-deploy and build filters, the down page and its message, the uptime check, " +
        "or the whole list of ports (on its own: not with other container settings in the same call). Only what's given changes. " +
        "Previews: set_previews and set_preview_settings.")]
    public async Task<object> UpdateAppSettings(
        [Description("The app: its key, name or number.")] string app,
        [Description("The app's name as Gitberth shows it.")] string? name = null,
        [Description("The path the health check asks for, like /health; empty for none.")] string? health_path = null,
        [Description("Memory limit in MiB; 0 for none.")] int? memory_mb = null,
        [Description("CPU limit in cores, like 0.5 or 2; 0 for none.")] double? cpus = null,
        [Description("no, always, unless-stopped or on-failure.")] string? restart_policy = null,
        [Description("With on-failure only: how many times Docker restarts it before giving up (0: no limit).")] int? restart_retries = null,
        [Description("Image apps: off (when I ask), notify (tell me) or auto (update by itself).")] string? updates = null,
        [Description("Git apps: deploy new commits by themselves.")] bool? auto_deploy = null,
        [Description("Git apps: build filters, one pattern per line: a commit deploys by itself only when it changes a file these match. Empty for any file.")] string? watch_paths = null,
        [Description("Git apps: a commit whose changed files all match these patterns doesn't deploy by itself. Empty for none.")] string? ignored_paths = null,
        [Description("Git apps: auto (what Gitberth detects, else Railpack) or railpack (always Railpack). A custom Dockerfile is written in the app's Settings tab.")]
        string? build = null,
        [Description("Show the down page on the app's domains while it can't be served.")] bool? down_page = null,
        [Description("The down page's message; empty for none.")] string? down_page_message = null,
        [Description("Check the app from outside (its main domain, else its LAN address).")] bool? uptime_check = null,
        [Description("The path the uptime check asks for; empty for /.")] string? uptime_path = null,
        [Description("The app's ports, as a whole new list.")] IReadOnlyList<PortInput>? ports = null,
        [Description("Git apps: save container changes for the next deploy instead of deploying now.")] bool next_deploy = false,
        [Description("Wait for the change (or its deploy) to end, up to 10 minutes.")] bool wait = false,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var title = view.Summary.Title;
        var done = new List<object>();
        var containerChange = name is not null || health_path is not null || memory_mb is not null || cpus is not null || restart_policy is not null
            || restart_retries is not null || updates is not null || auto_deploy is not null || watch_paths is not null || ignored_paths is not null
            || build is not null;
        if (ports is not null && containerChange) throw new ToolError("Change the ports in a call of their own: both may recreate the app.");
        if (!containerChange && ports is null && down_page is null && down_page_message is null && uptime_check is null && uptime_path is null)
            throw new ToolError("Say what to change: get_app shows the settings as they are.");
        if (containerChange)
        {
            var loaded = await LoadAsync(view, ct);
            var draft = ContainerSettingsDraft.From(loaded.Container, loaded.Options.SecretKeys);
            var optionsDraft = AppOptionsDraft.From(loaded.Options);
            if (name is not null) optionsDraft.Name = name;
            if (health_path is not null) optionsDraft.HealthCheckPath = health_path;
            if (updates is not null) optionsDraft.Updates = UpdatesOf(updates);
            if (optionsDraft.NameProblem is { } nameProblem) throw new ToolError(nameProblem);
            var gitDraft = loaded.Git is { } g ? GitOptionsDraft.From(g) : null;
            if ((auto_deploy is not null || watch_paths is not null || ignored_paths is not null) && gitDraft is null)
                throw new ToolError($"{title} isn't deployed from a repo: auto_deploy and build filters are a Git app's.");
            if (build is not null && gitDraft is null) throw new ToolError($"{title} isn't deployed from a repo: build is a Git app's.");
            if (gitDraft is not null)
            {
                if (auto_deploy is bool on) gitDraft.AutoDeploy = on;
                if (watch_paths is not null) gitDraft.WatchPaths = watch_paths;
                if (ignored_paths is not null) gitDraft.IgnoredPaths = ignored_paths;
                // A custom Dockerfile's text stays with the app, for when its Build is set back to it in the Settings tab.
                if (build is not null) gitDraft.DockerfileMode = BuildOf(build);
                if ((gitDraft.WatchProblem ?? gitDraft.IgnoredProblem) is { } filterProblem) throw new ToolError(filterProblem);
            }
            var wanted = draft.ToSettings();
            if (memory_mb is not null || cpus is not null)
                wanted = wanted with
                {
                    Resources = wanted.Resources with
                    {
                        MemoryBytes = memory_mb is int m ? (m <= 0 ? null : (long)m * 1024 * 1024) : wanted.Resources.MemoryBytes,
                        Cpus = cpus is double c ? (c <= 0 ? null : c) : wanted.Resources.Cpus,
                    },
                };
            if (restart_policy is not null || restart_retries is not null)
            {
                var policy = restart_policy is not null ? RestartOf(restart_policy) : string.IsNullOrEmpty(wanted.Restart.Name) ? "no" : wanted.Restart.Name;
                if (restart_retries is int tries && (tries < 0 || policy != "on-failure"))
                    throw new ToolError(tries < 0 ? "restart_retries is 0 or more." : "restart_retries goes with the on-failure restart policy only.");
                // As the Settings tab has it: only on-failure counts tries, so any other policy has none.
                wanted = wanted with { Restart = new RestartSetting(policy, policy == "on-failure" ? restart_retries ?? Math.Max(0, wanted.Restart.MaximumRetryCount) : 0) };
            }
            var edit = new SettingsEdit(loaded.ContainerId, loaded.Container, wanted, loaded.Options,
                optionsDraft.ToOptions() with { SecretKeys = draft.SecretKeys(loaded.Options.SecretKeys) }, loaded.Git, gitDraft?.ToOptions() ?? loaded.Git);
            done.Add(await ApplyEditAsync(view, edit, next_deploy ? ApplyTiming.NextDeploy : ApplyTiming.Now, wait, ct));
        }
        if (ports is not null) done.Add(await SetPortsAsync(view, ports, wait, ct));
        if (down_page is not null || down_page_message is not null || uptime_check is not null || uptime_path is not null)
        {
            var id = view.Summary.AppId ?? (await catalog.AdoptAsync(view.Summary.Key, ct)).Id;
            var current = await uptimeSettings.OfAsync(id, ct) ?? throw ToolError.NotFound($"There's no app called {app}.");
            if (down_page is not null || down_page_message is not null)
            {
                var on = down_page ?? current.DownPage;
                var message = down_page_message is null ? current.Message : down_page_message.Trim().Length == 0 ? null : down_page_message;
                if (await uptimeSettings.SetDownPageAsync(id, on, message, ct) is { } error) throw ToolError.Conflict(error);
                done.Add(new { down_page = on, down_page_message = message });
            }
            if (uptime_check is not null || uptime_path is not null)
            {
                var on = uptime_check ?? current.Uptime;
                var path = uptime_path is null ? current.Path : uptime_path.Trim().Length == 0 || uptime_path.Trim() == "/" ? null : uptime_path.Trim();
                if (await uptimeSettings.SetUptimeAsync(id, on, path, ct) is { } error) throw ToolError.Conflict(error);
                done.Add(new { uptime_check = on, uptime_path = UptimeMonitor.PathOf(path) });
            }
        }
        return new { app = title, done };
    }

    [McpServerTool(Name = "set_preview_settings", Title = "Change preview settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Changes a Git app's preview settings, as its Settings tab's Previews section does: how many previews run at most, the days without a new commit " +
        "before one is removed, whether pull requests from forks get previews, whether previews' domains are behind Cloudflare Access, and each preview's database " +
        "(none of its own, an empty one, or a copy of production's). Only what's given changes; previews on or off is set_previews. " +
        "Forks: " + ForkWarning + " A copy of production's database: " + CopyWarning)]
    public async Task<object> SetPreviewSettings(
        [Description("The app: its key, name or number.")] string app,
        [Description("How many previews run at most (1 to 20); more wait for one to be removed.")] int? limit = null,
        [Description("Days without a new commit before a preview is removed (1 to 365).")] int? expiry_days = null,
        [Description("Whether pull requests from forks get previews: anyone's code, built and run on this server.")] bool? forks = null,
        [Description("Whether previews' domains are behind Cloudflare Access (visitors sign in first).")] bool? @protect = null,
        [Description("Each preview's database: none (none of its own), empty (an empty one, the default) or copy_of_production.")] string? databases = null,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var row = view.Row is { Source: AppSource.Git, ParentAppId: null } r ? r : throw ToolError.Conflict($"{view.Summary.Title} isn't a Git app, so it has no previews.");
        if (limit is null && expiry_days is null && forks is null && @protect is null && databases is null)
            throw new ToolError("Say what to change: get_app shows the previews' settings as they are.");
        if (limit is < 1 or > 20) throw new ToolError("limit is between 1 and 20.");
        if (expiry_days is < 1 or > 365) throw new ToolError("expiry_days is between 1 and 365.");
        var current = PreviewSettings.Of(row);
        var next = current with
        {
            Limit = limit ?? current.Limit,
            ExpiryDays = expiry_days ?? current.ExpiryDays,
            Forks = forks ?? current.Forks,
            Protect = @protect ?? current.Protect,
            Databases = databases is null ? current.Databases : DatabasesOf(databases),
        };
        var saved = await previews.SaveSettingsAsync(row.Id, next, ct);
        if (saved.Problem is { } problem) throw new ToolError(problem);
        // What the Previews section's banners say while these are on.
        List<string> warnings = [];
        if (next.Forks) warnings.Add(ForkWarning);
        if (next.Databases == PreviewDatabaseMode.CopyOfProduction) warnings.Add(CopyWarning);
        if (saved.Warning is { } warning) warnings.Add(warning);
        return new
        {
            app = view.Summary.Title,
            mode = next.Mode.ToString(),
            limit = next.Limit,
            expiry_days = next.ExpiryDays,
            forks = next.Forks,
            protect = next.Protect,
            databases = next.Databases.ToString(),
            warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>The Previews section's banner while pull requests from forks get previews.</summary>
    internal const string ForkWarning = "Whoever opens a pull request from a fork decides what its preview builds and runs here, on your network. " +
        "Turn this on only if you trust everyone who can open one.";

    /// <summary>The Previews section's banner while each preview gets a copy of production's database.</summary>
    internal const string CopyWarning = "A copy holds whatever production's database holds, people's personal data too, and a preview runs a pull request's code with it. " +
        "Choose it only when everyone who can open a pull request may see that data.";

    [McpServerTool(Name = "add_database", Title = "Add a database", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Gives the app a database of its own, as its Settings tab › Databases does: a Postgres database and role on a Postgres server, or a Redis of its own. " +
        "Its connection string goes in the variable (DATABASE_URL or REDIS_URL by default), and the app is redeployed or recreated with it. The connection string is never returned.")]
    public async Task<object> AddDatabase(
        [Description("The app: its key, name or number.")] string app,
        [Description("postgres or redis.")] string kind,
        [Description("The variable the connection string goes in; DATABASE_URL for Postgres, REDIS_URL for Redis when left out.")] string? variable = null,
        [Description("Postgres: the server's container (list_databases lists them); the default server when left out.")] string? server = null,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var k = kind?.Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pg" => DatabaseKind.Postgres,
            "redis" => DatabaseKind.Redis,
            _ => throw new ToolError("kind is postgres or redis."),
        };
        var done = await databases.AddAsync(view.Summary.Key, new DatabaseRequest(k, string.IsNullOrWhiteSpace(variable) ? DatabaseService.DefaultVariable(k) : variable.Trim(),
            k == DatabaseKind.Postgres && !string.IsNullOrWhiteSpace(server) ? server.Trim() : null), ct);
        if (!done.Ok) throw ToolError.Conflict(done.Message);
        return new { app = view.Summary.Title, database_id = done.DatabaseId, message = done.Message, deployment_id = done.DeploymentId };
    }

    [McpServerTool(Name = "remove_database", Title = "Remove a database", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Takes a database off the app, as Remove on its Settings tab › Databases does: its variable goes, and the app is redeployed or recreated without it. " +
        "With delete_data its data is deleted for good (only a database Gitberth made): then pass confirm with the app's name.")]
    public async Task<object> RemoveDatabase(
        [Description("The app: its key, name or number.")] string app,
        [Description("The database: its id (list_databases) or its variable (DATABASE_URL).")] string database,
        [Description("Delete its data too. Without it the data stays on the server.")] bool delete_data = false,
        [Description("With delete_data: the app's name, repeated, to show it's meant.")] string? confirm = null,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var appId = view.Summary.AppId ?? throw ToolError.NotFound($"{view.Summary.Title} has no databases.");
        var rows = await databases.RowsAsync(appId, ct);
        var wanted = database?.Trim() ?? "";
        var row = rows.FirstOrDefault(d => d.Id.ToString(CultureInfo.InvariantCulture) == wanted || string.Equals(d.Variable, wanted, StringComparison.Ordinal))
            ?? throw ToolError.NotFound($"{view.Summary.Title} has no database {database}: list_databases lists its databases.");
        if (delete_data) AgentContext.Confirm(confirm, view.Summary, "delete the data of a database of");
        var done = await databases.RemoveAsync(appId, row.Id, delete_data, ct);
        if (!done.Ok) throw ToolError.Conflict(done.Message);
        return new { app = view.Summary.Title, message = done.Message, deployment_id = done.DeploymentId };
    }

    [McpServerTool(Name = "create_deploy_hook", Title = "Create a deploy hook", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Makes a deploy hook for the app, as its Settings tab › Deploy hooks does: a secret URL that deploys it (a Git or Compose app) or updates it (a registry image app) " +
        "when called with POST. The URL is returned this once: keep it as you would a password.")]
    public async Task<object> CreateDeployHook(
        [Description("The app: its key, name or number.")] string app,
        [Description("What will call it, like Contentful or Nightly build.")] string name,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var made = await hooks.CreateAsync(view.Summary.Key, name, ct);
        if (made.Problem is { } problem) throw ToolError.Conflict(problem);
        var hook = made.Hook!;
        var token = hook.Token ?? throw ToolError.Conflict("The hook was made, but its URL can't be read back.");
        var root = await ctx.RootAsync(ct);
        var publicHost = await domains.EnabledAsync(ct) ? await domains.WebhookHostAsync(ct) : null;
        return new
        {
            app = view.Summary.Title,
            hook_id = hook.Id,
            name = hook.Name,
            url = DeployHooks.LanUrl(root, token),
            public_url = publicHost is null ? null : DeployHooks.PublicUrl(publicHost, token),
            note = "Shown this once. A POST to the URL deploys the app; anyone with it can.",
        };
    }

    [McpServerTool(Name = "delete_deploy_hook", Title = "Delete a deploy hook", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Deletes one of the app's deploy hooks: its URL answers 404 from then on.")]
    public async Task<object> DeleteDeployHook(
        [Description("The app: its key, name or number.")] string app,
        [Description("The hook: its name or id (get_app lists them).")] string hook,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var appId = view.Summary.AppId ?? throw ToolError.NotFound($"{view.Summary.Title} has no deploy hooks.");
        var list = await hooks.ListAsync(appId, ct);
        var wanted = hook?.Trim() ?? "";
        var found = list.FirstOrDefault(h => h.Id.ToString(CultureInfo.InvariantCulture) == wanted || string.Equals(h.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolError.NotFound($"{view.Summary.Title} has no deploy hook {hook}.");
        await hooks.DeleteAsync(appId, found.Id, ct);
        return new { app = view.Summary.Title, deleted = found.Name, message = $"Deleted the deploy hook {found.Name}: its URL answers 404 from now on." };
    }

    // ---- create ----

    private async Task<object> CreateFromImageAsync(string? name, string? image, IReadOnlyList<EnvVarInput>? env, bool wait, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(image)) throw new ToolError("Give the image, like nginx:1.27.");
        var draft = await creator.Value.DraftFromImageAsync(image.Trim(), AppSource.Registry, ct: ct);
        if (!string.IsNullOrWhiteSpace(name)) draft = draft with { Name = name.Trim() };
        if (env is { Count: > 0 })
        {
            var vars = draft.Settings.Env.ToList();
            var secret = draft.Options.SecretKeys.ToList();
            foreach (var v in env)
            {
                var key = v.Name?.Trim() ?? "";
                if (key.Length == 0) throw new ToolError("A variable needs a name.");
                vars.RemoveAll(e => e.Key == key);
                vars.Add(new EnvSetting(key, v.Value ?? "", Secret: v.Secret == true));
                if (v.Secret == true && !secret.Contains(key)) secret.Add(key);
            }
            draft = draft with { Settings = draft.Settings with { Env = vars }, Options = draft.Options with { SecretKeys = secret } };
        }
        var plan = await creator.Value.PlanAsync(draft, ct);
        if (!plan.CanCreate)
            throw new ToolError(plan.Problems.Count > 0 ? string.Join(" ", plan.Problems.Select(p => p.Message)) : "There isn't enough free disk space to pull it.");
        var job = creator.Value.Start(plan);
        if (!wait)
            return new { app = draft.Name, key = plan.Container, started = true, message = $"Creating {plan.Container}. {plan.Consequence} get_app shows it once it's made, or call again with wait." };
        if (await AgentContext.WaitAsync(job.Completion, ct) is not { } result)
            return new { app = draft.Name, key = plan.Container, still_running = true, message = $"Still creating {plan.Container} after 10 minutes: get_app shows it once it's made." };
        if (!result.Ok) throw ToolError.Conflict(result.Message);
        return new { app = draft.Name, key = result.Key, id = result.AppId, created = true, message = result.Message };
    }

    private async Task<object> CreateFromComposeAsync(string? name, string? yaml, string? webService, IReadOnlyList<EnvVarInput>? env, bool wait, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(yaml)) throw new ToolError("Give the whole Compose file in compose_yaml.");
        var info = ComposeFiles.Read(yaml);
        if (info.Error is { } error) throw new ToolError(error);
        var web = !string.IsNullOrWhiteSpace(webService) ? webService.Trim() : ComposeFiles.PickWeb(info.Services)?.Service ?? info.Services.FirstOrDefault()?.Name
            ?? throw new ToolError("The Compose file has no services.");
        if (ComposeApps.Problems(yaml, web) is { Count: > 0 } problems) throw new ToolError(string.Join(" ", problems));
        var appName = string.IsNullOrWhiteSpace(name) ? web : name.Trim();
        if (AppService.NameProblem(appName) is { } nameProblem) throw new ToolError(nameProblem);
        var port = info.Service(web) is { } s ? ComposeFiles.WebPortOf(s) : null;
        var values = (env ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name)).Select(v => new ComposeValue(v.Name.Trim(), v.Value ?? "", v.Secret == true)).ToList();
        var created = await compose.CreateInlineAsync(new InlineComposeApp(appName, yaml, web, port, values), deploy: true, ct);
        if (created.DeploymentId is int deployment && wait) return await ctx.WaitForDeployAsync(deployment, ct);
        return new
        {
            app = appName, key = created.Key, id = created.AppId, deployment_id = created.DeploymentId,
            message = created.DeploymentId is null ? $"Created {appName}." : $"Created {appName}: deploy #{created.DeploymentId} is queued.",
        };
    }

    // A template is installed as its page in New app installs it (TemplateInstaller): the app, its databases, its secrets
    // (made here, never returned), its domain, then its first deploy.
    private async Task<object> CreateFromTemplateAsync(string? name, string? id, IReadOnlyDictionary<string, JsonElement>? inputs, string? domain,
        IReadOnlyList<EnvVarInput>? env, bool wait, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ToolError("Say which template: its id, as list_templates gives it (like uptime-kuma).");
        var t = templates.Find(id.Trim().ToLowerInvariant()) ?? throw ToolError.NotFound($"There's no template {id.Trim()}. list_templates lists them.");
        var known = t.Inputs.Count == 0 ? "it has none" : "its inputs are " + string.Join(", ", t.Inputs.Select(i => i.Name));
        if (env is { Count: > 0 })
            throw new ToolError($"A template takes inputs, not env ({known}). Its other variables can be changed once it's installed, with set_env_vars.");
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in inputs ?? new Dictionary<string, JsonElement>())
        {
            var input = t.Inputs.FirstOrDefault(i => string.Equals(i.Name, key.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolError($"{t.Name} has no input {key}: {known}.");
            given[input.Name] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => throw new ToolError($"The input {input.Name} takes text, a number, or true or false."),
            };
        }
        var request = new TemplateRequest(t.Id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), given, string.IsNullOrWhiteSpace(domain) ? null : domain.Trim());
        var installed = await installer.InstallAsync(request, await ctx.LanHostAsync(ct), ct);
        if (!installed.Ok)
            throw new ToolError(installed.Message.Contains(TemplateInstaller.NoServer, StringComparison.Ordinal)
                ? $"{installed.Message} create_postgres_server sets one up."
                : installed.Message);
        if (installed.DeploymentId is int deployment && wait)
            return new { key = installed.Key, id = installed.AppId, template = t.Id, message = installed.Message, notes = t.Notes, deploy = await ctx.WaitForDeployAsync(deployment, ct) };
        return new { key = installed.Key, id = installed.AppId, template = t.Id, deployment_id = installed.DeploymentId, message = installed.Message, notes = t.Notes };
    }

    /// <param name="alwaysRailpack">build railpack: Railpack builds it, whatever Gitberth detects.</param>
    private async Task<object> CreateFromGitAsync(string? name, string? url, string? branch, string? subdirectory, bool? useCompose, bool alwaysRailpack,
        string? webService, IReadOnlyList<EnvVarInput>? env, bool wait, CancellationToken ct)
    {
        if (alwaysRailpack && useCompose == true) throw new ToolError("build railpack builds one container: leave use_compose out, or set it to false.");
        // A github.com repository, or a project on Gitberth's GitLab.
        var instance = gitLab is null ? GitLabRepoRef.DefaultInstance : await gitLab.InstanceAsync(ct);
        if (!RepoUrls.TryParse(url, instance, out var repo, out var parseError) || repo is null)
            throw new ToolError(parseError ?? "Give the repo, like https://github.com/owner/repo or https://gitlab.com/group/project.");
        var remote = await git.LsRemoteAsync(repo, ct);
        if (remote.Branches.Count == 0) throw ToolError.Conflict("This repository has no branches yet. Push a commit first.");
        var chosenBranch = branch?.Trim() is { Length: > 0 } b ? b : remote.DefaultBranch ?? remote.Branches[0].Name;
        var sub = subdirectory;
        if (repo.TreeOf is { } tree && GitHubRepoRef.ResolveTreePath(tree, remote.Branches.Select(x => x.Name)) is { } resolved)
        {
            if (branch is null) chosenBranch = resolved.Branch;
            sub ??= resolved.Subdirectory;
        }
        if (remote.Branches.All(x => x.Name != chosenBranch)) throw ToolError.NotFound($"The repository has no branch {chosenBranch}.");
        var dir = Path.Combine(options.WorkDir, "analyze-" + Guid.NewGuid().ToString("N"));
        try
        {
            await git.CloneAsync(repo, chosenBranch, dir, null, ct);
            var candidates = await Task.Run(() => analyzer.FindApps(dir), ct);
            string? hint = null;
            if (string.IsNullOrEmpty(sub) && candidates.Count > 0)
                (sub, hint) = (candidates[0].Dir.Length == 0 ? null : candidates[0].Dir, candidates[0].Hint);
            if (ProjectAnalyzer.NormalizeDir(sub) is not { } folderName) throw new ToolError($"The subfolder '{sub}' isn't inside the repo.");
            if (folderName.Length > 0 && !Directory.Exists(Path.Combine(dir, folderName.Replace('/', Path.DirectorySeparatorChar))))
                throw ToolError.NotFound($"The subfolder '{folderName}' doesn't exist on {chosenBranch}.");
            var detection = await Task.Run(() => analyzer.Analyze(dir, new AnalyzeOptions(hint, AppDir: folderName)), ct);
            hint = detection.SelectedProject ?? hint;
            var folder = folderName.Length == 0 ? dir : Path.Combine(dir, folderName.Replace('/', Path.DirectorySeparatorChar));
            var composeFiles = ComposeFiles.Find(folder).Where(f => !ComposeTransform.LinksOut(dir, Path.Combine(folder, f))).ToList();
            var composeFile = composeFiles.FirstOrDefault();
            var composeText = composeFile is null ? null : await File.ReadAllTextAsync(Path.Combine(folder, composeFile), ct);
            var composeInfo = composeText is null ? null : ComposeFiles.Read(composeText);
            var useIt = !alwaysRailpack && (useCompose ?? (composeInfo is { } found && GitSource.PreferCompose(found, detection.Kind != ProjectKind.Unknown)));
            if (useIt && composeInfo is null) throw new ToolError("The repo's folder has no Compose file: leave use_compose out to build it.");
            // Railpack, as New app has it: always when asked (or the repo's railpack.json says how Railway's builder makes it),
            // and for a project Gitberth doesn't know.
            var (chosen, mode, triedRailpack) = useIt ? (detection, DockerfileMode.Auto, false)
                : await ChooseBuilderAsync(railpack ?? NoPlanner, alwaysRailpack, detection, dir, folderName, folder, PlanValues(env), ct);
            detection = chosen;
            var custom = !useIt && detection.Kind == ProjectKind.Unknown;
            var model = new AppSettingsModel
            {
                Name = string.IsNullOrWhiteSpace(name) ? folderName.Length == 0 ? repo.Name : $"{repo.Name}-{folderName.Split('/')[^1]}" : name.Trim(),
                Branch = chosenBranch,
                Subdirectory = folderName.Length == 0 ? null : folderName,
                ProjectHint = hint,
                DockerfileMode = custom ? DockerfileMode.Custom : mode,
                CustomDockerfile = custom ? detection.Dockerfile ?? DockerfileTemplates.Starter : null,
            };
            var inGitHub = (await repoVariables.GetAsync(repo.AppOwner, repo.AppRepo, ct)).Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (useIt && composeInfo is { } info)
            {
                var web = !string.IsNullOrWhiteSpace(webService) ? webService.Trim() : ComposeFiles.PickWeb(info.Services)?.Service ?? info.Services[0].Name;
                (model.ComposeFile, model.WebService) = (composeFile, web);
                model.Ports = (await ComposePortsAsync(info, web, ct)).ToList();
                foreach (var v in info.Variables)
                    model.Env.Add(new EnvRow { Key = v.Name, Value = "", IsSecret = Security.Masking.IsSecret(v.Name, "") });
                if (ComposeApps.Problems(composeText!, web) is { Count: > 0 } composeProblems) throw new ToolError(string.Join(" ", composeProblems));
            }
            else
            {
                var rules = new List<PortRule>();
                if (detection.ContainerPort is int port) rules.Add(new PortRule(port, "tcp", PortAccess.Lan, await SuggestAsync(port, ct)));
                model.Ports = ContainerPortsDraft.ForNew(rules, detection.ContainerPort).ToPortRows().ToList();
                foreach (var s in detection.EnvSuggestions)
                    model.Env.Add(new EnvRow { Key = s.Key, Value = inGitHub.Contains(s.Key) ? "" : s.Value, IsSecret = s.IsSecret, BuildTime = s.BuildTime });
            }
            foreach (var v in env ?? [])
            {
                var key = v.Name?.Trim() ?? "";
                if (key.Length == 0) throw new ToolError("A variable needs a name.");
                model.Env.RemoveAll(r => r.Key == key);
                model.Env.Add(new EnvRow { Key = key, Value = v.Value ?? "", IsSecret = SecretOf(v, key), BuildTime = v.BuildTime == true });
            }
            var portProblems = await apps.ValidatePortsAsync(null, model.Ports, ct: ct);
            var all = await apps.ValidateAsync(null, model, ct);
            if (all.Count > 0 || portProblems.Count > 0)
                throw new ToolError(string.Join(" ", all.Concat(portProblems.Select(p => p.Message)).Distinct()));
            var appId = await apps.CreateAsync(repo, model, detection, ct);
            var deployment = await actions.DeployAsync(appId, DeployTrigger.Created, ct);
            var key2 = await catalog.KeyForIdAsync(appId, ct);
            if (wait) return await ctx.WaitForDeployAsync(deployment, ct);
            var created = $"Created {model.Name}: deploy #{deployment} is queued (get_deployment follows it).";
            return new
            {
                app = model.Name, key = key2, id = appId, deployment_id = deployment, compose = useIt ? true : (bool?)null,
                message = custom && triedRailpack ? $"{created} {Railpack.RailpackText.StarterNote}" : created,
            };
        }
        finally
        {
            DeployPaths.TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// How create_app builds a repo's app, as New app chooses it. Always Railpack when asked, or when the app's folder has a
    /// railpack.json (and no Dockerfile of the repo's builds it): Railpack must plan it, or the app isn't made. For a project
    /// Gitberth doesn't know, Railpack is asked too: its plan gives the app's card and port (8080), and if it can't plan it
    /// either the app gets the Starter Dockerfile, as before. The analyzer's variables to suggest are kept either way.
    /// </summary>
    /// <returns>The detection to create the app with, its Build, and whether Railpack was asked.</returns>
    internal static async Task<(DetectionResult Detection, DockerfileMode Mode, bool Tried)> ChooseBuilderAsync(Railpack.IRailpackPlanner planner, bool alwaysRailpack,
        DetectionResult detection, string dir, string appDir, string folder, IReadOnlyList<Docker.EnvValue> env, CancellationToken ct)
    {
        var chosen = alwaysRailpack || Railpack.RailpackProviders.Suggested(folder, detection);
        if (!chosen && !detection.Unrecognized) return (detection, DockerfileMode.Auto, false);
        var plan = await planner.PlanAsync(new Railpack.RailpackPlanRequest(dir, appDir, env, HasStartCommand: false, Previous: []), null, ct);
        if (!plan.Ok)
        {
            if (chosen) throw new ToolError(plan.Error ?? Railpack.RailpackText.NotInstalled);
            return (detection, DockerfileMode.Auto, true);
        }
        // Gitberth's detected port stays when Railpack is chosen; a project Gitberth doesn't know gets Railpack's 8080.
        var card = Railpack.RailpackDetection.ToDetection(plan, appDir, folder, chosen ? detection.ContainerPort : null, fallback: !chosen);
        card.EnvSuggestions.AddRange(detection.EnvSuggestions);
        return (card, chosen ? DockerfileMode.Railpack : DockerfileMode.Auto, true);
    }

    // The values create_app gives Railpack to plan with: the build-time ones it was given, RAILPACK_ ones always.
    private static List<Docker.EnvValue> PlanValues(IReadOnlyList<EnvVarInput>? env) =>
        (env ?? []).Where(v => v.Name?.Trim() is { Length: > 0 } key && (v.BuildTime == true || EnvRules.IsRailpackKey(key)) && (v.Value ?? "").Length > 0)
            .Select(v => new Docker.EnvValue(v.Name!.Trim(), v.Value!, SecretOf(v, v.Name.Trim())))
            .ToList();

    // A variable create_app was given is secret as it says, else when it looks it; a RAILPACK_ one by its value without its
    // $NAME references (RailpackSettings), so one that names a secret isn't refused.
    private static bool SecretOf(EnvVarInput v, string key) =>
        v.Secret ?? (EnvRules.IsRailpackKey(key) ? Railpack.RailpackSettings.LooksSecret(key, v.Value ?? "") : Security.Masking.IsSecret(key, v.Value ?? ""));

    // The web service's ports as New app's Compose path proposes them.
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

    // ---- settings ----

    private async Task<SettingsView> LoadAsync(AppView view, CancellationToken ct) =>
        await settings.LoadAsync(view.Summary.Key, ct) ?? throw ToolError.NotFound($"There's no app called {view.Summary.Key}.");

    /// <summary>The page's Apply: the plan, its refusals, then the change (and, asked, its end).</summary>
    private async Task<object> ApplyEditAsync(AppView view, SettingsEdit edit, ApplyTiming timing, bool wait, CancellationToken ct)
    {
        var title = view.Summary.Title;
        var plan = await settings.PlanAsync(view.Summary.Key, edit, ct);
        if (plan.Lines.Count == 0) return new { app = title, changed = false, message = "Nothing to change: it's set so already." };
        if (plan.ComposeSnippet is { } yaml)
            throw ToolError.Conflict($"Compose created {title}, so the change goes in its Compose file: put this in the service's section, then run {plan.ComposeCommand ?? "docker compose up -d"} on the host.\n{yaml}");
        if (plan.Blocked is { } blocked) throw ToolError.Conflict(blocked);
        if (plan.Problems.Count > 0) throw new ToolError(string.Join(" ", plan.Problems.Select(p => p.Message)));
        var outcome = await settings.ApplyAsync(plan, timing, ct);
        if (outcome.Job is { } other && ChangeText.NotThisChange(other, plan.Change, title) is { } refused) throw ToolError.Conflict(refused);
        if (!outcome.Ok)
            throw ToolError.Conflict(outcome.Problems is { Count: > 0 } problems ? string.Join(" ", problems.Select(p => p.Message)) : outcome.Message);
        // The plan's lines are masked: a secret's value is never in them.
        var changes = plan.Lines.Select(l => new { setting = l.Label, before = l.Before, after = l.After, how = l.How.ToString() }).ToList();
        if (wait && outcome.DeploymentId is int deployment)
            return new { app = title, changed = true, changes, deploy = await ctx.WaitForDeployAsync(deployment, ct) };
        if (wait && outcome.Job is { } job)
        {
            if (await AgentContext.WaitAsync(job.Completion, ct) is not { } result)
                return new { app = title, changed = true, changes, still_running = true, container = job.Container, message = $"It's still going after 10 minutes: get_app shows how {title} ends up." };
            return new { app = title, changed = result.Ok, changes, outcome = result.Outcome.ToString(), message = result.Message };
        }
        return new { app = title, changed = true, changes, message = outcome.Message, deployment_id = outcome.DeploymentId, consequence = plan.Consequence };
    }

    private async Task<object> SetPortsAsync(AppView view, IReadOnlyList<PortInput> ports, bool wait, CancellationToken ct)
    {
        var rows = ports.Select(p => new PortRow
        {
            ContainerPort = p.ContainerPort,
            HostPort = p.HostPort,
            Protocol = string.IsNullOrWhiteSpace(p.Protocol) ? "tcp" : p.Protocol.Trim().ToLowerInvariant(),
            Access = AccessOf(p.Access),
            IsWeb = p.Web == true,
        }).ToList();
        foreach (var row in rows.Where(r => r.HostPort is null && r.Access != PortAccess.Internal && r.ContainerPort is not null))
            row.HostPort = await apps.SuggestHostPortAsync(row.ContainerPort!.Value, row.Protocol, view.Summary.AppId,
                rows.Where(o => o != row && o.HostPort is not null).Select(o => o.HostPort!.Value), ct);
        var appId = view.Summary.AppId ?? (await catalog.AdoptAsync(view.Summary.Key, ct)).Id;
        var stored = await apps.GetAsync(appId, ct);
        var saved = await actions.ApplyPortsAsync(appId, rows, stored is null ? null : AppSettingsModel.PortsKey(stored.Ports), applyNow: true, ct);
        if (!saved.Ok) throw new ToolError(string.Join(" ", saved.Problems.Select(p => p.Message)));
        if (wait && saved.DeploymentId is int deployment) return new { ports = "saved", deploy = await ctx.WaitForDeployAsync(deployment, ct) };
        return new { ports = "saved", restarts = saved.NeedsRestart, deployment_id = saved.DeploymentId };
    }

    private static PortAccess AccessOf(string? access) => access?.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        null or "" or "lan" => PortAccess.Lan,
        "host_only" or "hostonly" or "host" => PortAccess.HostOnly,
        "internal" or "none" => PortAccess.Internal,
        _ => throw new ToolError("A port's access is lan, host_only or internal."),
    };

    private static EnvScope Scope(string? scope, bool git)
    {
        var parsed = scope?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => EnvScope.All,
            "production" or "prod" => EnvScope.Production,
            "preview" or "previews" => EnvScope.Preview,
            _ => throw new ToolError("A variable's scope is all, production or preview."),
        };
        if (!git && parsed != EnvScope.All) throw new ToolError("Only Git apps have production and preview scopes.");
        return parsed;
    }

    private static PreviewDatabaseMode DatabasesOf(string databases) => databases.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "none" or "off" => PreviewDatabaseMode.Off,
        "empty" => PreviewDatabaseMode.Empty,
        "copy_of_production" or "copy" or "copyofproduction" => PreviewDatabaseMode.CopyOfProduction,
        _ => throw new ToolError("databases is none, empty or copy_of_production."),
    };

    private static UpdatePolicy UpdatesOf(string updates) => updates.Trim().ToLowerInvariant() switch
    {
        "off" or "when_i_ask" or "manual" => UpdatePolicy.Off,
        "notify" or "tell_me" => UpdatePolicy.Notify,
        "auto" or "automatic" or "automatically" => UpdatePolicy.Auto,
        _ => throw new ToolError("updates is off, notify or auto."),
    };

    private static string RestartOf(string policy) => policy.Trim().ToLowerInvariant() switch
    {
        "no" or "never" => "no",
        "always" => "always",
        "unless-stopped" or "unless_stopped" => "unless-stopped",
        "on-failure" or "on_failure" => "on-failure",
        _ => throw new ToolError("restart_policy is no, always, unless-stopped or on-failure."),
    };

    // A Git app's Build: what Gitberth detects (else Railpack), or always Railpack. A custom Dockerfile is the Settings tab's to write.
    private static DockerfileMode BuildOf(string build) => build.Trim().ToLowerInvariant() switch
    {
        "auto" => DockerfileMode.Auto,
        "railpack" => DockerfileMode.Railpack,
        "custom" => throw new ToolError("build is auto or railpack: a custom Dockerfile is written in the app's Settings tab."),
        _ => throw new ToolError("build is auto or railpack."),
    };
}
