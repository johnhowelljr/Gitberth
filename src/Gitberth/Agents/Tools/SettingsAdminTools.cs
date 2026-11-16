using System.ComponentModel;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Services;
using Gitberth.Uptime;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>
/// Gitberth's own settings a person may change from Settings, through the same services: which notifications are sent,
/// updates, uptime checks, the status page, and a Postgres server for apps' databases. Never tokens, the admin password, or
/// the audit log's recording of changes made outside Gitberth: those stay with a signed-in person. Admin access.
/// </summary>
[McpServerToolType]
public sealed class SettingsAdminTools(NotificationSettings notifications, SettingsService settings, AuditLog audit, UptimeSettings uptimeSettings, AgentContext ctx,
    DatabaseServers servers)
{
    [McpServerTool(Name = "set_notifications", Title = "Choose notifications", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Chooses what Settings › Notifications sends to its destinations (where they go is set by a person, with their addresses and tokens). " +
        "Only what's given changes.")]
    public async Task<object> SetNotifications(
        [Description("When a deploy fails.")] bool? deploy_failed = null,
        [Description("When a deploy succeeds.")] bool? deploy_succeeded = null,
        [Description("When an app crashes.")] bool? crashed = null,
        [Description("When a health check fails (and passes again).")] bool? unhealthy = null,
        [Description("When an uptime check finds an app down (and back up).")] bool? down = null,
        [Description("When a domain's certificate is about to expire.")] bool? certificate_expiring = null,
        [Description("About previews' deploys too.")] bool? previews = null,
        CancellationToken ct = default)
    {
        var wanted = new (NotifyEvent Event, bool? On)[]
        {
            (NotifyEvent.DeployFailed, deploy_failed), (NotifyEvent.DeploySucceeded, deploy_succeeded), (NotifyEvent.Crashed, crashed),
            (NotifyEvent.Unhealthy, unhealthy), (NotifyEvent.Down, down), (NotifyEvent.CertificateExpiring, certificate_expiring),
        };
        if (wanted.All(w => w.On is null) && previews is null) throw new ToolError("Say what to turn on or off: get_settings shows what's sent now.");
        foreach (var (e, on) in wanted)
            if (on is bool value) await notifications.SetEventAsync(e, value, ct);
        if (previews is bool p) await notifications.SetPreviewsAsync(p, ct);
        return new
        {
            events = NotificationSettings.Choosable.ToDictionary(e => e.ToString(), notifications.IsOn),
            previews = notifications.Previews,
            destinations = notifications.Destinations.Count,
        };
    }

    [McpServerTool(Name = "set_update_settings", Title = "Change update settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Changes Settings › Updates: how often apps' images are checked for updates, when automatic updates run (in Gitberth's time zone), " +
        "and the policy new image apps start with. Only what's given changes.")]
    public async Task<object> SetUpdateSettings(
        [Description("Check every this many hours (1 to 168).")] int? check_every_hours = null,
        [Description("Automatic updates run between these times, like 03:00-05:00.")] string? automatic_window = null,
        [Description("New apps from an image: off (when I ask), notify (tell me) or auto.")] string? default_policy = null,
        CancellationToken ct = default)
    {
        var hours = check_every_hours ?? await settings.GetIntAsync(UpdateWatcher.CheckHoursKey, 6, ct);
        var window = automatic_window ?? await settings.GetAsync(UpdateWatcher.AutoWindowKey, ct) ?? UpdateSettingsSave.DefaultWindow;
        if (window.Split('-') is not [var from, var to]) throw new ToolError("automatic_window is two times, like 03:00-05:00.");
        var policy = default_policy is null ? await UpdateWatcher.DefaultPolicyAsync(settings, ct) : default_policy.Trim().ToLowerInvariant() switch
        {
            "off" or "when_i_ask" => UpdatePolicy.Off,
            "notify" or "tell_me" => UpdatePolicy.Notify,
            "auto" or "automatic" or "automatically" => UpdatePolicy.Auto,
            _ => throw new ToolError("default_policy is off, notify or auto."),
        };
        if (await UpdateSettingsSave.SaveAsync(settings, audit, hours, from, to, policy, ct) is { } problem) throw new ToolError(problem);
        return new { check_every_hours = hours, automatic_window = $"{from.Trim()}-{to.Trim()}", time_zone = UpdateWatcher.ZoneName(), default_policy = UpdateSettingsSave.PolicyName(policy) };
    }

    [McpServerTool(Name = "set_uptime_settings", Title = "Change uptime checks", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Changes how often every app is checked from outside (Settings › Uptime and status page). An app's own check is turned on or off with update_app_settings.")]
    public async Task<object> SetUptimeSettings(
        [Description("Check every this many seconds (30 to 900).")] int interval_seconds,
        CancellationToken ct = default)
    {
        if (await uptimeSettings.SetIntervalAsync(interval_seconds, ct) is { } problem) throw new ToolError(problem);
        return new { interval_seconds = await uptimeSettings.IntervalAsync(ct) };
    }

    [McpServerTool(Name = "set_status_page", Title = "Change the status page", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Changes the public status page (Settings › Uptime and status page): on or off, its title and description, which apps it lists, " +
        "whether it shows response times, and the host name it's served on through the tunnel. Only what's given changes.")]
    public async Task<object> SetStatusPage(
        [Description("Serve the status page (on the LAN at /status, and on its host name).")] bool? enabled = null,
        [Description("Its title.")] string? title = null,
        [Description("A short description under the title; empty for none.")] string? description = null,
        [Description("The apps it lists (keys, names or numbers); an empty list for every app with uptime checks.")] IReadOnlyList<string>? apps = null,
        [Description("Show each app's response time.")] bool? response_times = null,
        [Description("The host name it's served on through the tunnel, like status.example.com; empty for none.")] string? host = null,
        CancellationToken ct = default)
    {
        var current = await uptimeSettings.StatusAsync(ct);
        IReadOnlyList<int>? ids = current.Apps;
        if (apps is not null)
        {
            if (apps.Count == 0) ids = null;
            else
            {
                var list = new List<int>();
                foreach (var a in apps)
                {
                    var view = await ctx.AppAsync(a, ct);
                    list.Add(view.Summary.AppId ?? throw ToolError.Conflict($"{view.Summary.Title} isn't checked, so the status page can't list it."));
                }
                ids = list;
            }
        }
        var next = current with
        {
            Enabled = enabled ?? current.Enabled,
            Title = title ?? current.Title,
            Description = description is null ? current.Description : description.Trim().Length == 0 ? null : description.Trim(),
            Apps = ids,
            ResponseTimes = response_times ?? current.ResponseTimes,
            Host = host is null ? current.Host : host.Trim().Length == 0 ? null : host.Trim(),
        };
        if (await uptimeSettings.SaveStatusAsync(next, ct) is { } problem) throw new ToolError(problem);
        var saved = await uptimeSettings.StatusAsync(ct);
        return new { enabled = saved.Enabled, title = saved.Title, description = saved.Description, apps = saved.Apps, response_times = saved.ResponseTimes, host = saved.Host };
    }

    [McpServerTool(Name = "create_postgres_server", Title = "Set up a Postgres server", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Sets up a Postgres server for apps' databases, as Settings › Databases' Create a Postgres server does: an app called postgres (or a free " +
        "variant) running " + DatabaseServers.ServerImage + ", on Gitberth's network with no published port, its password a secret variable. New databases go on it " +
        "when no other server was chosen. Templates that need Postgres need one, and so does add_database's Postgres; list_databases says whether there is one.")]
    public async Task<object> CreatePostgresServer(
        [Description("Wait for it to be set up (pulled, created and checked), up to 10 minutes.")] bool wait = false,
        CancellationToken ct = default)
    {
        var list = await servers.ListAsync(ct);
        if (await servers.DefaultAsync(list, ct) is { } current)
            return new { server = current.Container, created = false, message = $"New databases already go on {current.Container}: there's no need for another server." };
        if (list.Count(s => s.Usable) > 1)
            throw ToolError.Conflict($"Gitberth can use {string.Join(", ", list.Where(s => s.Usable).Select(s => s.Container))}, and none is chosen for new databases. " +
                "A person chooses one in Settings › Databases.");
        var creation = servers.Create();
        if (!wait)
            return new { started = true, phase = creation.Phase, message = "Setting up a Postgres server: list_databases shows it once it's made, or call again with wait." };
        if (await AgentContext.WaitAsync(creation.Completion, ct) is not { } result)
            return new { started = true, phase = creation.Phase, still_running = true, message = "It's still being set up: list_databases shows it once it's made." };
        if (!result.Ok) throw ToolError.Conflict(result.Message);
        return new { server = result.Key, id = result.AppId, created = true, message = result.Message };
    }
}
