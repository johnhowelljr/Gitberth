using System.Globalization;
using Gitberth.Audit;
using MudBlazor;

namespace Gitberth.Components.Shared;

/// <summary>How the audit log page shows an entry: its area's icon, its actor's icon and words, and its outcome.</summary>
public static class AuditLook
{
    /// <summary>The area an action belongs to, for its mark; an outside change is Outside whatever its action.</summary>
    public static AuditArea AreaOf(AuditRecord r) =>
        r.Outside ? AuditArea.Outside
        : Enum.GetValues<AuditArea>().Where(a => a != AuditArea.Outside).FirstOrDefault(a => AuditAreas.Contains(a, r.Action), AuditArea.Settings);

    public static string AreaIcon(AuditArea area) => area switch
    {
        AuditArea.Apps => Icons.Material.Outlined.GridView,
        AuditArea.Deploys => Icons.Material.Outlined.RocketLaunch,
        AuditArea.Domains => Icons.Material.Outlined.Public,
        AuditArea.SignIns => Icons.Material.Outlined.Login,
        AuditArea.Outside => Icons.Material.Outlined.ViewInAr,
        _ => Icons.Material.Outlined.Tune,
    };

    /// <summary>The mark's tone: failures and refusals stand out, outside changes are amber, the rest by area.</summary>
    public static string Tone(AuditRecord r) => r.Outcome switch
    {
        AuditOutcome.Failed => "critical",
        AuditOutcome.Denied => "warn",
        _ when r.Outside => "serious",
        _ => AreaOf(r) switch
        {
            AuditArea.Deploys => "accent",
            AuditArea.SignIns => "info",
            AuditArea.Domains => "info",
            _ => "neutral",
        },
    };

    public static string ActorIcon(AuditActorKind kind) => kind switch
    {
        AuditActorKind.User => Icons.Material.Outlined.Person,
        AuditActorKind.ApiToken => Icons.Material.Outlined.Key,
        AuditActorKind.DeployHook => Icons.Material.Outlined.Webhook,
        AuditActorKind.GitHub => Icons.Custom.Brands.GitHub,
        AuditActorKind.Docker => Icons.Material.Outlined.ViewInAr,
        AuditActorKind.Cloudflare => Icons.Material.Outlined.Cloud,
        _ => Icons.Material.Outlined.SmartToy,
    };

    public static string KindWords(AuditActorKind kind) => kind switch
    {
        AuditActorKind.User => "Person",
        AuditActorKind.ApiToken => "API token",
        AuditActorKind.DeployHook => "Deploy hook",
        AuditActorKind.GitHub => "GitHub",
        AuditActorKind.Docker => "Docker",
        AuditActorKind.Cloudflare => "Cloudflare",
        _ => "Gitberth",
    };

    /// <summary>"the UI", "an AI agent", "the watcher": where a change came in.</summary>
    public static string ViaWords(AuditVia via) => via switch
    {
        AuditVia.UI => "the UI",
        AuditVia.Mcp => "an AI agent (MCP)",
        AuditVia.Api => "the API",
        AuditVia.Webhook => "a webhook",
        AuditVia.Hook => "a deploy hook",
        AuditVia.Watcher => "the repo watcher",
        AuditVia.Schedule => "a schedule",
        AuditVia.Startup => "startup",
        AuditVia.Updates => "the update checks",
        AuditVia.Health => "the health checks",
        AuditVia.Docker => "Docker's events",
        AuditVia.Sync => "a sync",
        _ => "Gitberth",
    };

    /// <summary>The short line under an actor's name: "UI · 10.1.1.5", "watcher", "outside Gitberth".</summary>
    public static string ActorSub(AuditActor actor) => actor.Kind == AuditActorKind.Docker
        ? "outside Gitberth"
        : string.Join(" · ", new[]
        {
            actor.Via switch
            {
                AuditVia.UI => "UI",
                AuditVia.Mcp => "MCP",
                AuditVia.Api => "API",
                AuditVia.Gitberth => null,
                _ => ViaWords(actor.Via).Replace("the ", "", StringComparison.Ordinal).Replace("a ", "", StringComparison.Ordinal),
            },
            actor.Ip,
        }.Where(s => !string.IsNullOrEmpty(s)));

    public static string ActorName(AuditActor actor) => actor.Kind == AuditActorKind.Docker ? "Docker" : actor.Name;

    public static string? OutcomeWords(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Failed => "Failed",
        AuditOutcome.Denied => "Refused",
        _ => null,
    };

    /// <summary>"Today", "Yesterday", "Thu 12 Nov", "Thu 12 Nov 2025".</summary>
    public static string Day(DateTime localDate, DateTime today) =>
        localDate == today ? "Today"
        : localDate == today.AddDays(-1) ? "Yesterday"
        : localDate.Year == today.Year ? localDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)
        : localDate.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The page's words for a range: "in the last 24 hours", "in the last 7 days", "since the log began".</summary>
    public static string RangeWords(AuditRange range) => range switch
    {
        AuditRange.Day => "in the last 24 hours",
        AuditRange.Month => "in the last 30 days",
        AuditRange.All => "in the whole log",
        _ => "in the last 7 days",
    };

    public static string Count(long n, string one, string many) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";
}
