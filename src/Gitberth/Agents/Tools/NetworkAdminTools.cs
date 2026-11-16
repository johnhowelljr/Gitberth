using System.ComponentModel;
using Gitberth.Apps;
using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Network;
using ModelContextProtocol.Server;

namespace Gitberth.Agents.Tools;

/// <summary>
/// An app's domains, as its Domains card changes them: add (publishing the web port first when the tunnel needs a host
/// port, as the card's button does), remove, main, and Cloudflare Access protection. Admin access.
/// </summary>
[McpServerToolType]
public sealed class NetworkAdminTools(AgentContext ctx, AppCatalog catalog, DomainService domains, AppSettingsService settings)
{
    [McpServerTool(Name = "add_domain", Title = "Add a domain", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Serves the app on a domain through the Cloudflare Tunnel, as Add domain on its Domains card does: the tunnel's route and the DNS record are written in Cloudflare. " +
        "Needs domains on in Settings, and a web port on the app. When the tunnel reaches apps through a host port, the app's web port is published on the host first (it may restart once).")]
    public async Task<object> AddDomain(
        [Description("The app: its key, name or number.")] string app,
        [Description("The domain, like shop.example.com.")] string hostname,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        if (!await domains.EnabledAsync(ct)) throw ToolError.Conflict("Domains are off: a person turns them on in Settings › Domains.");
        var appId = view.Summary.AppId ?? (await catalog.AdoptAsync(view.Summary.Key, ct)).Id;
        var added = await domains.AddAsync(appId, hostname, ct);
        if (added.NeedsHostBinding)
        {
            var published = await domains.AddAndPublishAsync(appId, hostname, ct);
            if (published.Error is { } error) throw ToolError.Conflict(error);
            if (published.Plan is { } plan)
            {
                // An app that isn't deployed from a repo: its container is changed to publish the port, then the domain is added.
                var outcome = await settings.ApplyAsync(plan, ApplyTiming.Now, ct);
                if (outcome.Job is not { } job) throw ToolError.Conflict($"{hostname} wasn't added: {outcome.Message}");
                if (ChangeText.NotThisChange(job, plan.Change, view.Summary.Title) is { } refused) throw ToolError.Conflict(refused);
                var after = await domains.AddWhenPublishedAsync(appId, hostname, job.Completion, ct);
                if (after.Error is { } later) throw ToolError.Conflict(later);
                return Result(view.Summary.Title, hostname, after.Report, published.HostPort, published.DeploymentId);
            }
            return Result(view.Summary.Title, hostname, published.Report, published.HostPort, published.DeploymentId);
        }
        if (added.Error is { } problem) throw ToolError.Conflict(problem);
        return Result(view.Summary.Title, hostname, added.Report, added.HostPort, added.DeploymentId);
    }

    [McpServerTool(Name = "remove_domain", Title = "Remove a domain", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Takes a domain off the app, as Remove on its Domains card does: its route leaves the tunnel and the DNS record Gitberth added goes. The app keeps running. " +
        "Pass confirm with the domain.")]
    public async Task<object> RemoveDomain(
        [Description("The app: its key, name or number.")] string app,
        [Description("The domain to remove.")] string hostname,
        [Description("The domain, repeated, to show it's meant.")] string? confirm = null,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var host = Own(view, hostname);
        AgentContext.ConfirmDomain(confirm, host, "remove");
        var report = await domains.RemoveAsync(view.Summary.AppId!.Value, host, ct);
        return Result(view.Summary.Title, host, report, null, null, removed: true);
    }

    [McpServerTool(Name = "set_main_domain", Title = "Make a domain the main one", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Makes one of the app's domains its main one: the address Gitberth gives the app (VERCEL_URL and friends) with its next build, and the one uptime checks ask.")]
    public async Task<object> SetMainDomain(
        [Description("The app: its key, name or number.")] string app,
        [Description("One of the app's domains.")] string hostname,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var host = Own(view, hostname);
        if (await domains.SetMainAsync(view.Summary.AppId!.Value, host, ct) is { } error) throw ToolError.Conflict(error);
        return new { app = view.Summary.Title, main = host, message = $"{host} is the main domain now. The app gets it as its address with its next build." };
    }

    [McpServerTool(Name = "protect_domain", Title = "Protect a domain", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [ToolAccess(ApiAccess.Admin)]
    [Description("Puts one of the app's domains behind Cloudflare Access (visitors sign in with an emailed code; only Settings › Domains › Protection's addresses get in), " +
        "or takes it out again. Anything that calls a protected domain without a browser can't any more.")]
    public async Task<object> ProtectDomain(
        [Description("The app: its key, name or number.")] string app,
        [Description("One of the app's domains.")] string hostname,
        [Description("true to protect it, false to open it to everyone again.")] bool on,
        CancellationToken ct = default)
    {
        var view = await ctx.AppAsync(app, ct);
        var host = Own(view, hostname);
        var (error, report) = await domains.SetProtectedAsync(view.Summary.AppId!.Value, host, on, ct);
        if (error is not null) throw ToolError.Conflict(error);
        return new
        {
            app = view.Summary.Title,
            hostname = host,
            @protected = on,
            message = on ? $"{host} is protected: visitors sign in first." : $"{host} is open to everyone again.",
            notes = Notes(report),
        };
    }

    // One of the app's own domains, by its name as given.
    private static string Own(AppView view, string hostname)
    {
        var host = DomainNames.Normalize(hostname) ?? hostname?.Trim().ToLowerInvariant() ?? "";
        if (view.Row?.Domains.FirstOrDefault(d => string.Equals(d.Hostname, host, StringComparison.OrdinalIgnoreCase)) is { } d) return d.Hostname;
        throw ToolError.NotFound($"{view.Summary.Title} has no domain {hostname}: list_domains lists its domains.");
    }

    private static List<object>? Notes(SyncReport report) =>
        report.Notes.Count == 0 ? null : report.Notes.Select(n => (object)new { hostname = n.Hostname, text = n.Text, problem = n.Problem ? true : (bool?)null }).ToList();

    private static object Result(string app, string hostname, SyncReport report, int? hostPort, int? deploymentId, bool removed = false) => new
    {
        app,
        hostname = DomainNames.Normalize(hostname) ?? hostname,
        added = removed ? (bool?)null : true,
        removed = removed ? true : (bool?)null,
        published_host_port = hostPort,
        deployment_id = deploymentId,
        message = removed ? $"Removed {hostname}: its route left the tunnel." : $"Added {hostname}: the tunnel sends it to {app}.",
        notes = Notes(report),
    };
}
