using System.Globalization;
using System.Text;

namespace Gitberth.Audit;

/// <summary>The time ranges the audit log page offers.</summary>
public enum AuditRange { Day, Week, Month, All }

/// <summary>
/// The audit log page's filters as a person sets them, and as its address and the export's carry them:
/// /audit?range=week&amp;who=User:admin&amp;app=gb-web&amp;area=deploys&amp;outcome=failed&amp;q=shop.
/// </summary>
public sealed record AuditFilter
{
    public AuditRange Range { get; init; } = AuditRange.Week;
    /// <summary>"Kind:Name" (User:admin, Docker:outside Gitberth), or null for anyone.</summary>
    public string? Who { get; init; }
    /// <summary>An app by its container's name, or null for every app.</summary>
    public string? App { get; init; }
    public AuditArea? Area { get; init; }
    public AuditOutcome? Outcome { get; init; }
    public string? Text { get; init; }

    public static readonly AuditFilter Default = new();

    public bool IsDefault => this == Default;

    /// <summary>The query the filters stand for at <paramref name="now"/>.</summary>
    public AuditQuery ToQuery(DateTime now, int limit = 100, int? appId = null)
    {
        var (kind, name) = SplitWho(Who);
        return new AuditQuery
        {
            From = Range switch
            {
                AuditRange.Day => now.AddHours(-24),
                AuditRange.Week => now.AddDays(-7),
                AuditRange.Month => now.AddDays(-30),
                _ => null,
            },
            ActorKind = kind,
            Actor = name,
            Container = string.IsNullOrWhiteSpace(App) ? null : App,
            AppId = string.IsNullOrWhiteSpace(App) ? null : appId,
            Area = Area,
            Outcome = Outcome,
            Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            Limit = limit,
        };
    }

    /// <summary>The filters as a query string ("range=all&amp;app=gb-web"); the defaults are left out.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        if (Range != AuditRange.Week) parts.Add("range=" + RangeKey(Range));
        if (!string.IsNullOrWhiteSpace(Who)) parts.Add("who=" + Uri.EscapeDataString(Who));
        if (!string.IsNullOrWhiteSpace(App)) parts.Add("app=" + Uri.EscapeDataString(App));
        if (Area is { } area) parts.Add("area=" + AreaKey(area));
        if (Outcome is { } outcome) parts.Add("outcome=" + outcome.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(Text)) parts.Add("q=" + Uri.EscapeDataString(Text.Trim()));
        return string.Join('&', parts);
    }

    /// <summary>The filters an address carries; anything unknown is left at its default.</summary>
    public static AuditFilter Parse(string? range, string? who, string? app, string? area, string? outcome, string? text) => new()
    {
        Range = ParseRange(range),
        Who = string.IsNullOrWhiteSpace(who) || SplitWho(who).Kind is null ? null : who.Trim(),
        App = string.IsNullOrWhiteSpace(app) ? null : app.Trim(),
        Area = ParseArea(area),
        Outcome = Enum.TryParse<AuditOutcome>(outcome, ignoreCase: true, out var o) && Enum.IsDefined(o) ? o : null,
        Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
    };

    public static string RangeKey(AuditRange range) => range switch
    {
        AuditRange.Day => "day",
        AuditRange.Month => "month",
        AuditRange.All => "all",
        _ => "week",
    };

    public static AuditRange ParseRange(string? range) => range?.Trim().ToLowerInvariant() switch
    {
        "day" or "24h" => AuditRange.Day,
        "month" or "30d" => AuditRange.Month,
        "all" => AuditRange.All,
        _ => AuditRange.Week,
    };

    public static string AreaKey(AuditArea area) => area switch
    {
        AuditArea.SignIns => "signins",
        _ => area.ToString().ToLowerInvariant(),
    };

    public static AuditArea? ParseArea(string? area) => area?.Trim().ToLowerInvariant() switch
    {
        "apps" => AuditArea.Apps,
        "deploys" => AuditArea.Deploys,
        "domains" => AuditArea.Domains,
        "settings" => AuditArea.Settings,
        "signins" or "sign-ins" => AuditArea.SignIns,
        "outside" => AuditArea.Outside,
        _ => null,
    };

    /// <summary>"User:admin" as its kind and name; (null, null) for anything else.</summary>
    public static (AuditActorKind? Kind, string? Name) SplitWho(string? who)
    {
        if (string.IsNullOrWhiteSpace(who)) return (null, null);
        var colon = who.IndexOf(':');
        if (colon <= 0 || !Enum.TryParse<AuditActorKind>(who[..colon], out var kind) || !Enum.IsDefined(kind)) return (null, null);
        var name = who[(colon + 1)..].Trim();
        return (kind, name.Length == 0 ? null : name);
    }

    public static string WhoKey(AuditActorRef actor) => $"{actor.Kind}:{actor.Name}";
}

/// <summary>The audit log's own address besides its page: the export of a filtered view, for signed-in people only.</summary>
public static class AuditEndpoints
{
    public const string ExportPath = "/audit/export";

    /// <summary>/audit/export?format=csv|jsonl and the page's filters: every entry they match, as a download.</summary>
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder app) =>
        app.MapGet(ExportPath, async (HttpContext context, AuditLog audit) =>
        {
            var q = context.Request.Query;
            var format = string.Equals(q["format"], "jsonl", StringComparison.OrdinalIgnoreCase) ? AuditExportFormat.JsonLines : AuditExportFormat.Csv;
            var filter = AuditFilter.Parse(q["range"], q["who"], q["app"], q["area"], q["outcome"], q["q"]);
            var appId = int.TryParse(q["appId"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (int?)null;
            var query = filter.ToQuery(DateTime.UtcNow, appId: appId);
            context.Response.ContentType = AuditExport.ContentType(format);
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{AuditExport.FileName(format, DateTime.Now)}\"";
            context.Response.Headers.CacheControl = "no-store";
            await audit.ExportAsync(query, format, context.Response.Body, context.RequestAborted);
        }).RequireAuthorization();

    /// <summary>A browser signed out (POST /logout).</summary>
    public static void SignedOut(AuditLog audit, string user, HttpContext context)
    {
        var actor = AuditActor.User(user, AuditCircuitHandler.ClientIp(context));
        audit.Record(new AuditEntry("auth.signout", $"{user} signed out{From(actor)}.") { Actor = actor, Target = user });
    }

    /// <summary>The browser that changed the password picked up its new sign-in (GET /account/renew).</summary>
    public static void Renewed(AuditLog audit, string user, HttpContext context)
    {
        var actor = AuditActor.User(user, AuditCircuitHandler.ClientIp(context));
        audit.Record(new AuditEntry("auth.renewed", $"{user} was signed in again after changing the password{From(actor)}.") { Actor = actor, Target = user });
    }

    private static string From(AuditActor actor) => actor.Ip is { } ip ? $" from {ip}" : "";

    /// <summary>The export's address for the page's filters.</summary>
    public static string ExportUrl(AuditFilter filter, AuditExportFormat format, int? appId = null)
    {
        var sb = new StringBuilder(ExportPath.TrimStart('/'));
        sb.Append("?format=").Append(format == AuditExportFormat.Csv ? "csv" : "jsonl");
        if (filter.ToQueryString() is { Length: > 0 } rest) sb.Append('&').Append(rest);
        if (appId is int app && filter.App is not null) sb.Append("&appId=").Append(app.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
