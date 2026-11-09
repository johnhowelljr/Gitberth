using Gitberth;
using Gitberth.Apps;
using Gitberth.Components;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
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
builder.Services.AddSingleton(options);

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
builder.Services.AddHttpClient("cloudflare", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("health", c => c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("cron", c => c.Timeout = CronScheduler.RunTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// Docker Hub's public tags API, for the tag picker of New app.
builder.Services.AddHttpClient(ImageService.HubClient, c => c.Timeout = TimeSpan.FromSeconds(10));
// A registry's own API, for New app to read an image's config before it's pulled.
builder.Services.AddHttpClient(RegistryClient.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

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
builder.Services.AddSingleton<ProjectAnalyzer>();
builder.Services.AddSingleton<AppLocks>();
builder.Services.AddSingleton<LiveLogHub>();
// ---- GitHub integration (1.1): what deploys tell GitHub (commit statuses, pull request comments), and instant deploys
// through GitHub's webhook. Each does nothing until a token (and, for the webhook, a host name) is set up.
builder.Services.AddSingleton<GitHubWrites>();
builder.Services.AddSingleton<GitHubIntegrationState>();
builder.Services.AddSingleton<IDeployReporter, GitHubReporter>();
builder.Services.AddSingleton<WebhookThrottle>();
builder.Services.AddSingleton<GitHubWebhookEndpoint>();
builder.Services.AddSingleton<WebhookKeeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookKeeper>());
builder.Services.AddSingleton<DeployPipeline>();
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
// Images, registries and updates (through the Engine API: pulls, /distribution and /auth; New app also reads an image's
// config from its registry before it's pulled), and new apps from any source.
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<AppCreator>();
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
builder.Services.AddSingleton<Gitberth.Previews.PreviewService>();
builder.Services.AddHostedService<StartupTasks>();
// Before the event watcher, so they hear its first starts.
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkKeeper>());
builder.Services.AddHostedService<AppSync>();
builder.Services.AddHostedService<UpdateWatcher>();
builder.Services.AddHostedService<PortChangeWatcher>();
builder.Services.AddHostedService<RepoWatcher>();
// Domains taken off an app whose route or DNS record couldn't be removed yet: tried again every 15 minutes.
builder.Services.AddHostedService<RemovalRetry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerEventWatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerStatsCollector>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CronScheduler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RestartScheduler>());

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<GitberthDb>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
await app.Services.GetRequiredService<AdminAuthService>().EnsureAdminPasswordAsync();
await app.Services.GetRequiredService<HealthSettings>().LoadAsync();
await app.Services.GetRequiredService<ContainerTitles>().LoadAsync();
await app.Services.GetRequiredService<RestartSchedules>().LoadAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explicit (instead of the implicit ones at the top of the pipeline) so re-executed
// status-code pages like /not-found also pass through authorization.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// GitHub's webhook (instant deploys): anonymous, signed by GitHub with Gitberth's secret instead of a sign-in or a form token.
app.MapPost(GitHubWebhookEndpoint.Path, (HttpContext context) => context.RequestServices.GetRequiredService<GitHubWebhookEndpoint>().HandleAsync(context))
    .AllowAnonymous()
    .DisableAntiforgery();

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Right after the admin password changed: the browser that changed it swaps its sign-in for one under the new password,
// with the one-time renewal it was given. Every other browser's sign-in no longer counts.
app.MapGet("/account/renew", async (HttpContext context, AdminAuthService auth, string? renewal) =>
{
    if (auth.RedeemRenewal(renewal) is not { } user) return Results.Redirect("/login");
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user, new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect("/settings?password=changed#password");
});

app.Run();
