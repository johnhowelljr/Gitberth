using Gitberth;
using Gitberth.Agents;
using Gitberth.Apps;
using Gitberth.Components;
using Gitberth.Credentials;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Disk;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Keeper;
using Gitberth.Network;
using Gitberth.Notifications;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Sources;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

var options = GitberthOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
foreach (var dir in new[] { options.DataDir, options.KeysDir, options.LogsDir, options.WorkDir })
    Directory.CreateDirectory(dir);
// A restore's first phase staged the backup's data and restarted Gitberth: it goes in place now, before anything opens the
// database, the keys or the histories (Settings › Save and restore).
var restored = Gitberth.Backup.RestoreBoot.SwapStaged(options);
builder.Services.AddSingleton(options);
// The audit log: every change to the system, who made it and how it ended (/data/audit.db). Its writer is flushed when
// Gitberth stops: registered first among the hosted services, it's the last to stop.
builder.Services.AddSingleton<Gitberth.Audit.AuditLog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Gitberth.Audit.AuditLog>());
// Save and restore's start-up work, before every other job: what a save stopped or paused is brought back, and a
// restore's second phase runs (its gate, closed from the start, holds the other jobs off until every app is back).
builder.Services.AddHostedService<Gitberth.Backup.RestoreRunner>();
builder.Services.AddSingleton<Gitberth.Audit.AuditSettings>();
// What Gitberth is about to change in Docker (DockerCli and DockerApi register it), so its own events aren't recorded as
// changes made outside Gitberth.
builder.Services.AddSingleton<Gitberth.Audit.ExpectedChanges>();
// Who acts through a page: the signed-in user and the browser's address, around everything a circuit does.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, Gitberth.Audit.AuditCircuitHandler>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 4000;
    config.SnackbarConfiguration.PreventDuplicates = false;
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "gitberth.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        // A sign-in made before the admin password last changed is refused: changing it signs every other browser out.
        o.Events.OnValidatePrincipal = AdminAuthService.ValidateCookieAsync;
    });
builder.Services.AddAuthorization();
// An open page checks the same every minute, so a browser left open is signed out too, not only on its next load.
builder.Services.AddScoped<AuthenticationStateProvider, StampRevalidator>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDir))
    .SetApplicationName("Gitberth");
builder.Services.AddDbContextFactory<GitberthDb>(o => o.UseSqlite($"Data Source={options.DbPath}"));
builder.Services.AddHttpClient("github", c => c.Timeout = TimeSpan.FromSeconds(15));
// Gitberth's GitLab (Settings › GitLab): gitlab.com, or a self-hosted one.
builder.Services.AddHttpClient(GitLabApi.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("cloudflare", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("health", c => c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("cron", c => c.Timeout = CronScheduler.RunTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// Docker Hub's search and tags API, for New app's search and the tag picker: anonymous, as Gitberth/<version>, answers
// decompressed, never redirected; a host that doesn't answer fails in 3 seconds.
builder.Services.AddHttpClient(ImageService.HubClient, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.Add(HostGates.UserAgent);
        // Answers are read through HostGates.ReadAsync's capped copy; should one ever be buffered, never past 4 MiB.
        c.MaxResponseContentBufferSize = 4 * 1024 * 1024;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = false,
    });
// A registry's own API, for New app to read an image's config before it's pulled and to list an image's tags. Its blobs
// may be served from a CDN: up to 5 redirects, which drop the Authorization header.
builder.Services.AddHttpClient(RegistryClient.ClientName, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.Add(HostGates.UserAgent);
        // Answers are read through HostGates.ReadAsync's capped copy; should one ever be buffered, never past 4 MiB.
        c.MaxResponseContentBufferSize = 4 * 1024 * 1024;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        MaxAutomaticRedirections = 5,
    });

builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<GitHubApi>();
builder.Services.AddSingleton<RepoVariables>();
builder.Services.AddSingleton<CloudflareApi>();
builder.Services.AddSingleton<TunnelLocator>();
builder.Services.AddSingleton<DomainService>();
builder.Services.AddSingleton<GitClient>();
builder.Services.AddSingleton<DockerCli>();
// Gitberth's own container, as its helpers need it: its image, and where its data folder is on the Docker host.
builder.Services.AddSingleton<SelfContainer>();
// ---- Compose apps (1.3): a repo's Compose file, or a pasted one, deployed as one app with all its services.
builder.Services.AddSingleton<Gitberth.Compose.ComposeRunner>();
builder.Services.AddSingleton<Gitberth.Compose.ComposeApps>();
builder.Services.AddSingleton<ProjectAnalyzer>();
// ---- Railpack: Railway's builder, for the projects Gitberth doesn't build itself and the apps set to it. It plans in a
// throwaway container of Gitberth's own image, and builds in gitberth-buildkit, which it starts when a build needs it.
builder.Services.AddSingleton<Gitberth.Railpack.RailpackPlanner>();
builder.Services.AddSingleton<Gitberth.Railpack.IRailpackPlanner>(sp => sp.GetRequiredService<Gitberth.Railpack.RailpackPlanner>());
builder.Services.AddSingleton<Gitberth.Railpack.RailpackBuildKit>();
builder.Services.AddSingleton<Gitberth.Railpack.IRailpackBuildKit>(sp => sp.GetRequiredService<Gitberth.Railpack.RailpackBuildKit>());
builder.Services.AddSingleton<AppLocks>();
builder.Services.AddSingleton<LiveLogHub>();
// ---- GitHub integration (1.1): what deploys tell GitHub (commit statuses, pull request comments), and instant deploys
// through GitHub's webhook. Each does nothing until a token (and, for the webhook, a host name) is set up.
builder.Services.AddSingleton<GitHubWrites>();
builder.Services.AddSingleton<GitHubIntegrationState>();
// ---- GitLab: the same for GitLab projects (statuses, a merge request's comment, the project hook, CI/CD variables),
// with GitLab's token and state of its own. GitHubReporter and WebhookKeeper report and keep hooks for both hosts.
builder.Services.AddSingleton<GitLabApi>();
builder.Services.AddSingleton<GitLabIntegrationState>();
builder.Services.AddSingleton<GitLabSettings>();
builder.Services.AddSingleton<GitHubReporter>();
// ---- Notifications (1.2): failed deploys, crashes and failing health checks, by mail, Discord, Slack, ntfy or a webhook.
// Nothing is sent until Settings › Notifications has somewhere to send it.
builder.Services.AddHttpClient(NotificationSender.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<NotificationSettings>();
builder.Services.AddSingleton<IMailer, SmtpMailer>();
builder.Services.AddSingleton<NotificationSender>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<DeployNotifier>();
// Every deploy is told to GitHub and to notifications, at once.
builder.Services.AddSingleton<IDeployReporter>(sp => new DeployReporters(
    [sp.GetRequiredService<GitHubReporter>(), sp.GetRequiredService<DeployNotifier>()], sp.GetRequiredService<ILogger<DeployReporters>>()));
builder.Services.AddSingleton<ContainerAlerts>();
builder.Services.AddSingleton<WebhookThrottle>();
builder.Services.AddSingleton<GitHubWebhookEndpoint>();
builder.Services.AddSingleton<GitLabWebhookEndpoint>();
builder.Services.AddSingleton<WebhookKeeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookKeeper>());
builder.Services.AddSingleton<DeployPipeline>();
builder.Services.AddSingleton<BuildCache>();
builder.Services.AddSingleton<DeployQueue>();
builder.Services.AddSingleton<AppService>();
builder.Services.AddSingleton<AppActions>();
builder.Services.AddSingleton<WatchState>();
builder.Services.AddSingleton<DockerApi>();
builder.Services.AddSingleton<HealthSettings>();
builder.Services.AddSingleton<ContainerTitles>();
builder.Services.AddSingleton<StatsStore>();
builder.Services.AddSingleton<ContainerEventWatcher>();
builder.Services.AddSingleton<ContainerStatsCollector>();
builder.Services.AddSingleton<ContainerService>();
builder.Services.AddSingleton<HostScanner>();
builder.Services.AddSingleton<ContainerLocks>();
builder.Services.AddSingleton<NetworkService>();
builder.Services.AddSingleton<HostPortMap>();
builder.Services.AddSingleton<AddressBook>();
builder.Services.AddSingleton<IChangeJournal, DbChangeJournal>();
// Adding a domain can publish an app's web port: DomainService picks the port and recreates the app through these, which
// themselves depend on it, so it gets them when it first needs them.
builder.Services.AddSingleton(sp => new Lazy<HostPortMap>(sp.GetRequiredService<HostPortMap>));
builder.Services.AddSingleton(sp => new Lazy<AppActions>(sp.GetRequiredService<AppActions>));
// Changing a standalone container's ports: the recreate engine, and what it asks of Docker and of the host.
builder.Services.AddSingleton<IContainerEngine, DockerEngine>();
builder.Services.AddSingleton<HostHelpers>();
builder.Services.AddSingleton<IHostPortProbe>(sp => sp.GetRequiredService<HostHelpers>());
builder.Services.AddSingleton<ILanCheck>(sp => sp.GetRequiredService<HostHelpers>());
builder.Services.AddSingleton<IHostPortChecks, HostPortChecks>();
builder.Services.AddSingleton<INameCheck, NameCheck>();
builder.Services.AddSingleton<IContainerUsers>(sp => sp.GetRequiredService<ContainerService>());
builder.Services.AddSingleton<PortChanger>();
// ContainerService asks PortChanger before a start, and PortChanger asks ContainerService who uses a container, so
// ContainerService gets it when it first needs it.
builder.Services.AddSingleton(sp => new Lazy<PortChanger>(sp.GetRequiredService<PortChanger>));
// Settings for every app: live changes (docker update) and the health URL a recreate checks reach the engine through
// PortChanger's optional parameters.
builder.Services.AddSingleton<ContainerUpdater>();
builder.Services.AddSingleton<IHealthProbe, HealthProbe>();
// Every app, rows and live containers, as one list; and what happened to each.
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<AppCatalog>();
builder.Services.AddSingleton<AppSettingsService>();
// Save and restore (Settings › Save and restore): one compressed, encrypted file with everything this host runs, restored
// onto a fresh install in two phases around a restart. While a restore runs, the background jobs hold off (RestoreGate).
builder.Services.AddSingleton(_ => new Gitberth.Backup.RestoreGate(options));
builder.Services.AddSingleton<Gitberth.Backup.IBackupEngine>(sp => sp.GetRequiredService<DockerApi>());
builder.Services.AddSingleton<Gitberth.Backup.BackupService>();
builder.Services.AddSingleton<Gitberth.Backup.IBackupService>(sp => sp.GetRequiredService<Gitberth.Backup.BackupService>());
builder.Services.AddSingleton<Gitberth.Backup.RestoreService>();
builder.Services.AddSingleton<Gitberth.Backup.IRestoreService>(sp => sp.GetRequiredService<Gitberth.Backup.RestoreService>());
// Images, registries and updates (through the Engine API: pulls, /distribution and /auth; New app also reads an image's
// config from its registry before it's pulled), and new apps from any source.
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<IHookUpdates>(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddSingleton<AppCreator>();
// Deploy hooks (1.2): secret URLs that build a Git app again or update an image app, for a CMS, a scheduled job or CI.
builder.Services.AddSingleton<DeployHooks>();
builder.Services.AddSingleton<DeployHookEndpoint>();
// AppActions starts, stops and removes any app through ContainerService and applies a non-Git app's ports through
// AppSettingsService, which both depend on it; the catalog's update badges come from UpdateService, which depends on the
// catalog. Each gets the other when it first needs it.
builder.Services.AddSingleton(sp => new Lazy<ContainerService>(sp.GetRequiredService<ContainerService>));
builder.Services.AddSingleton(sp => new Lazy<AppSettingsService>(sp.GetRequiredService<AppSettingsService>));
builder.Services.AddSingleton(sp => new Lazy<UpdateService>(sp.GetRequiredService<UpdateService>));
builder.Services.AddSingleton<NetworkKeeper>();
builder.Services.AddSingleton<ContainerPortNotes>();
// Own LAN addresses: the block in Settings, gitberth-lan, and each container's address on it.
builder.Services.AddSingleton<LanAddresses>();
builder.Services.AddSingleton<CronScheduler>();
builder.Services.AddSingleton<RestartSchedules>();
builder.Services.AddSingleton<RestartScheduler>();
// Preview deployments: a Git app's branches and pull requests as apps of their own (the repo watcher drives them).
builder.Services.AddSingleton<GitHubPulls>();
// Shared by PreviewService and AppActions: deleting an app never races a watcher pass that would make its previews again.
builder.Services.AddSingleton<Gitberth.Previews.PreviewGate>();
builder.Services.AddSingleton<Gitberth.Previews.PreviewService>();
// ---- Databases (1.3): an app's own Postgres database or Redis, made from its page, and each preview's own ----
// The Postgres servers (Settings › Databases), which can set one up as an app; an app's databases, which its deploys set and
// AppActions lets go of with it (each comes to the other when first needed).
builder.Services.AddSingleton(sp => new Lazy<ImageService>(sp.GetRequiredService<ImageService>));
builder.Services.AddSingleton(sp => new Lazy<AppCreator>(sp.GetRequiredService<AppCreator>));
builder.Services.AddSingleton<Gitberth.Databases.DatabaseServers>();
builder.Services.AddSingleton<Gitberth.Databases.DatabaseService>();
builder.Services.AddSingleton(sp => new Lazy<Gitberth.Databases.DatabaseService>(sp.GetRequiredService<Gitberth.Databases.DatabaseService>));
// ---- App templates (1.3): a gallery of self-hosted apps, each installed as a Compose app with its databases, secrets and domain.
builder.Services.AddSingleton(_ => Gitberth.Templates.TemplateCatalog.Bundled);
builder.Services.AddSingleton<Gitberth.Templates.TemplateInstaller>();
// Where a template's app is reached (APP_URL…), as each of its deploys tells it.
builder.Services.AddSingleton<Gitberth.Templates.TemplateBuiltIns>();
// ---- Uptime checks, the status page and the down page (1.3) ----
// Which apps' domains show the down page (DownWatcher decides, a deploy's swap too), read by every domain sync.
builder.Services.AddSingleton<DownStates>();
builder.Services.AddSingleton<DownWatcher>();
// Each app checked from outside, the way a visitor reaches it; the history in /data/uptime.db.
builder.Services.AddSingleton<Gitberth.Uptime.UptimeCertificates>();
builder.Services.AddHttpClient(Gitberth.Uptime.UptimeChecker.ClientName, c => c.Timeout = Gitberth.Uptime.UptimeChecker.Timeout)
    .ConfigurePrimaryHttpMessageHandler(sp => Gitberth.Uptime.UptimeChecker.Handler(sp.GetRequiredService<Gitberth.Uptime.UptimeCertificates>(),
        sp.GetRequiredService<GitberthOptions>().UptimeConnectTo));
builder.Services.AddSingleton<Gitberth.Uptime.UptimeChecker>();
builder.Services.AddSingleton<Gitberth.Uptime.UptimeStore>();
builder.Services.AddSingleton<Gitberth.Uptime.UptimeMonitor>();
builder.Services.AddSingleton<Gitberth.Uptime.UptimeSettings>();
// The public pages (the down page and the status page): the only thing a marker host is ever answered with.
builder.Services.AddSingleton<Gitberth.Public.StatusPage>();
builder.Services.AddSingleton<Gitberth.Public.PublicPages>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Gitberth.Uptime.UptimeMonitor>());
// One instance, as a service too: the keep-running check (AddAppKeeper) waits for StartupTasks' own recovery to be done.
builder.Services.AddSingleton<StartupTasks>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupTasks>());
// Before the event watcher, so they hear its first starts.
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownWatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkKeeper>());
builder.Services.AddHostedService<AppSync>();
builder.Services.AddHostedService<UpdateWatcher>();
builder.Services.AddHostedService<PortChangeWatcher>();
builder.Services.AddHostedService<RepoWatcher>();
// What a sync couldn't finish (the tunnel, a removed domain's route or DNS record, a protected domain not behind
// Cloudflare Access yet, an Access application Cloudflare couldn't remove for the moment): tried again every 15 minutes.
builder.Services.AddHostedService<RemovalRetry>();
// Before the event watcher, so it hears the watcher's first events.
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerAlerts>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerEventWatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerStatsCollector>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CronScheduler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RestartScheduler>());
// The audit log's daily prune, and its own follow of Docker's events for changes made outside Gitberth.
builder.Services.AddHostedService<Gitberth.Audit.AuditMaintenance>();
builder.Services.AddHostedService<Gitberth.Audit.OutsideChanges>();
// ---- AI agents and the HTTP API (1.3): API tokens (Settings › AI agents and API), the MCP server at /mcp and the same tools
// at /api/v1, each tool calling the service the UI calls.
builder.Services.AddGitberthAgents();
// ---- Help (1.4): the docs, built in (/help), and the tips on the UI's labels, both read from the assembly once.
builder.Services.AddSingleton(_ => Gitberth.Help.HelpDocs.Bundled);
builder.Services.AddSingleton(_ => Gitberth.Help.TipCatalog.Bundled);
// ---- Stability: what needs someone's attention (the dashboard's banners, the top bar's pill, get_health), then each job
// that keeps items there, registered by its own folder: apps that should run brought back (Keeper), Docker's disk kept
// from filling up (Disk), and the tokens Gitberth uses watched before they expire (Credentials).
builder.Services.AddSingleton<AttentionBoard>();
builder.Services.AddAppKeeper();
builder.Services.AddDiskCare();
builder.Services.AddCredentialWatch();

var app = builder.Build();
if (restored is not null) app.Logger.LogWarning("Restore: {Restored}", restored);

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<GitberthDb>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
await app.Services.GetRequiredService<AdminAuthService>().EnsureAdminPasswordAsync();
await app.Services.GetRequiredService<HealthSettings>().LoadAsync();
await app.Services.GetRequiredService<ContainerTitles>().LoadAsync();
await app.Services.GetRequiredService<RestartSchedules>().LoadAsync();
await app.Services.GetRequiredService<NotificationSettings>().LoadAsync();
await app.Services.GetRequiredService<Gitberth.Audit.AuditSettings>().LoadAsync();

// The docs and the tips are part of the build: what's wrong with them stops a developer's Gitberth at once (the unit tests
// hold them to the same), and a release says it in the log and shows what it can.
{
    var help = app.Services.GetRequiredService<Gitberth.Help.HelpDocs>();
    var problems = help.Problems.Concat(app.Services.GetRequiredService<Gitberth.Help.TipCatalog>().Check(help)).ToList();
    if (problems.Count > 0 && app.Environment.IsDevelopment())
        throw new InvalidOperationException("The help or the tips have problems:\n" + string.Join("\n", problems));
    foreach (var problem in problems)
        app.Logger.LogWarning("Help: {Problem}", problem);
}

// The end-to-end rig's shorter waits, before any background job starts. An install never sets them: see TestTimings.
if (options.TestTimings is { } testTimings)
{
    testTimings.Apply(app.Services);
    app.Logger.LogWarning("{Variable} is set ({Timings}): Gitberth runs with shorter waits, for its end-to-end rig only.",
        TestTimings.Variable, testTimings);
}

// The very first: a request for one of Gitberth's marker hosts (a tunnel rule's httpHostHeader under gitberth.invalid) is
// answered with a public page (a domain's down page, the status page) or a bare 404, and goes no further. Nothing else of
// Gitberth's (sign-in, Blazor, the hooks, static files, the "not found" page) ever answers one.
app.Use(Gitberth.Public.PublicPages.Middleware);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// /mcp and /api answer their own status codes and JSON (never the "not found" page), and refuse browser pages (an Origin header).
app.Use(Gitberth.Agents.AgentPaths.Middleware);

// Explicit (instead of the implicit ones at the top of the pipeline) so re-executed
// status-code pages like /not-found also pass through authorization.
app.UseAuthentication();
app.UseAuthorization();
// What an API token changes is the token's in the audit log.
app.Use(Gitberth.Agents.AgentPaths.ActorMiddleware);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// GitHub's webhook (instant deploys): anonymous, signed by GitHub with Gitberth's secret instead of a sign-in or a form token.
// Every method is mapped, so its host answers a bare status code and never shows Gitberth's pages.
GitHubWebhookEndpoint.Map(app);
// GitLab's project hooks (instant deploys of GitLab projects): anonymous, with the secret token Gitberth set on the hook.
GitLabWebhookEndpoint.Map(app);
// Deploy hooks: anonymous too, the token in the path being the secret; every method and path under /hooks/deploy is mapped
// for the same reason.
DeployHookEndpoint.Map(app);

// The status page on the LAN, without signing in, while it's on (a bare 404 while it's off).
app.MapMethods("/status", ["GET", "HEAD"], (HttpContext context, Gitberth.Public.PublicPages pages) => pages.StatusAsync(context, json: false)).AllowAnonymous();
app.MapMethods("/status.json", ["GET", "HEAD"], (HttpContext context, Gitberth.Public.PublicPages pages) => pages.StatusAsync(context, json: true)).AllowAnonymous();
// A domain's down page as its visitors would see it, for the Domains card (signed in only).
app.MapGet("/down-page/preview", (HttpContext context, Gitberth.Public.PublicPages pages, string? host) => pages.PreviewAsync(context, host)).RequireAuthorization();

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery, Gitberth.Audit.AuditLog audit) =>
{
    if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
    var who = context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    if (who is not null) Gitberth.Audit.AuditEndpoints.SignedOut(audit, who, context);
    return Results.Redirect("/login");
});

// Right after the admin password changed: the browser that changed it swaps its sign-in for one under the new password,
// with the one-time renewal it was given. Every other browser's sign-in no longer counts.
app.MapGet("/account/renew", async (HttpContext context, AdminAuthService auth, Gitberth.Audit.AuditLog audit, string? renewal) =>
{
    if (auth.RedeemRenewal(renewal) is not { } user) return Results.Redirect("/login");
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user, new AuthenticationProperties { IsPersistent = true });
    Gitberth.Audit.AuditEndpoints.Renewed(audit, user.Identity?.Name ?? AdminAuthService.UserName, context);
    return Results.Redirect("/settings?password=changed#password");
});

// The audit log's export: the page's filtered view as CSV or JSON Lines, for signed-in people.
Gitberth.Audit.AuditEndpoints.Map(app);
// Save and restore's file: the save's download, made while it's sent, and the restore's upload (signed in only).
Gitberth.Backup.BackupEndpoints.Map(app);
// The app templates' icons, for New app's gallery (signed in only).
Gitberth.Templates.TemplateEndpoints.Map(app);
// The help's pictures and the tips' catalog (signed in only).
Gitberth.Help.HelpEndpoints.Map(app);

// AI agents (MCP, /mcp) and scripts (/api/v1), with an API token only: never the sign-in cookie.
app.MapGitberthAgents();

app.Run();
