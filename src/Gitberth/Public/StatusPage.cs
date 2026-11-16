using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Uptime;
using Microsoft.EntityFrameworkCore;
using static System.Net.WebUtility;

namespace Gitberth.Public;

/// <summary>How the status page's banner reads.</summary>
public enum StatusOverall { Operational, Partial, Major, Unknown }

/// <summary>One day of an app's 90-day bar: its uptime that day (0 to 100), or null when it wasn't checked.</summary>
public sealed record StatusDay(DateOnly Date, double? Uptime);

/// <summary>An app on the status page: only its display name, how it stands, and its uptime.</summary>
public sealed record StatusApp(string Name, UptimeStatus State, IReadOnlyList<StatusDay> Days, double? Uptime90, int? ResponseMs);

/// <summary>A time an app was down, as the status page tells it.</summary>
public sealed record StatusIncident(string App, DateTime Started, DateTime? Ended, string Cause)
{
    /// <summary>"web was down for 4 minutes on Nov 12, 14:05–14:09 UTC: Server error 502", or "web is down since …" while it is.</summary>
    public string Summary => Ended is { } ended
        ? $"{App} was down for {UptimeMonitor.Duration(ended - Started)} on {Started.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}–{(ended.Date == Started.Date ? ended.ToString("HH:mm", CultureInfo.InvariantCulture) : ended.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture))} UTC: {Cause}"
        : $"{App} is down since {Started.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)} UTC: {Cause}";
}

public sealed record StatusPageModel(string Title, string? Description, StatusOverall Overall, IReadOnlyList<StatusApp> Apps, IReadOnlyList<StatusIncident> Incidents,
    bool ResponseTimes, DateTime At);

/// <summary>
/// The public status page: server-rendered HTML with inline styles (nothing loaded from anywhere; light and dark; good on a
/// phone; refreshes itself every minute), and the same as JSON. It shows only what Settings › Uptime and status page lists:
/// each app's display name, how it stands, 90 days of uptime and, when chosen, its response time, and the last 14 days'
/// incidents. Never an address, a container's name or an error's text. Everything in it is escaped.
/// </summary>
public sealed class StatusPage(IDbContextFactory<GitberthDb> dbFactory, UptimeStore store, UptimeMonitor monitor, UptimeSettings uptimeSettings, TimeProvider? time = null)
{
    public const int Days = 90, IncidentDays = 14;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The page as it is now; null while it's off.</summary>
    public async Task<StatusPageModel?> BuildAsync(CancellationToken ct = default)
    {
        var settings = await uptimeSettings.StatusAsync(ct);
        if (!settings.Enabled) return null;
        var now = _time.GetUtcNow().UtcDateTime;
        List<AppDefinition> apps;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            apps = (await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null).ToListAsync(ct)).Where(settings.Lists).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var ids = apps.Select(a => a.Id).ToHashSet();
        var today = DateOnly.FromDateTime(now);
        var from = today.AddDays(-(Days - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var hours = (await store.HoursAsync(ids, from, ct)).GroupBy(h => h.AppId).ToDictionary(g => g.Key, g => g.ToList());
        var incidents = await store.IncidentsAsync(null, now.AddDays(-IncidentDays), ct);
        var listed = apps.Select(app => App(app, hours.GetValueOrDefault(app.Id) ?? [], monitor.For(app.Id).Status, today, now, settings.ResponseTimes)).ToList();
        var told = incidents
            .Where(i => ids.Contains(i.AppId))
            .Select(i => (Incident: i, App: apps.First(a => a.Id == i.AppId)))
            // What happened before its row was made is another app's (an id used again).
            .Where(x => x.Incident.Started >= x.App.CreatedAt.AddMinutes(-1))
            .Select(x => new StatusIncident(x.App.Name, x.Incident.Started, x.Incident.Ended, x.Incident.Cause))
            .OrderByDescending(i => i.Started)
            .ToList();
        return new StatusPageModel(settings.Title, settings.Description, Overall(listed), listed, told, settings.ResponseTimes, now);
    }

    internal static StatusApp App(AppDefinition app, List<UptimeHour> hours, UptimeStatus state, DateOnly today, DateTime now, bool responseTimes)
    {
        // Hours from before the app's row was made aren't its own.
        var own = hours.Where(h => h.Hour >= UptimeStore.HourOf(app.CreatedAt)).ToList();
        var byDay = own.GroupBy(h => DateOnly.FromDateTime(h.Hour)).ToDictionary(g => g.Key, g => (Checks: g.Sum(h => h.Checks), Ups: g.Sum(h => h.Ups)));
        var days = Enumerable.Range(0, Days).Select(i => today.AddDays(i - (Days - 1)))
            .Select(d => new StatusDay(d, byDay.TryGetValue(d, out var x) && x.Checks > 0 ? Math.Round(100.0 * x.Ups / x.Checks, 2) : null))
            .ToList();
        var checks = own.Sum(h => h.Checks);
        double? uptime = checks > 0 ? Math.Round(100.0 * own.Sum(h => h.Ups) / checks, 2) : null;
        var lastDay = own.Where(h => h.Hour >= now.AddHours(-24)).ToList();
        var count = lastDay.Sum(h => h.MsCount);
        int? response = responseTimes && count > 0 ? (int)(lastDay.Sum(h => h.MsSum) / count) : null;
        return new StatusApp(app.Name, state, days, uptime, response);
    }

    internal static StatusOverall Overall(IReadOnlyList<StatusApp> apps)
    {
        var known = apps.Where(a => a.State is UptimeStatus.Up or UptimeStatus.Down).ToList();
        if (known.Count == 0) return StatusOverall.Unknown;
        var down = known.Count(a => a.State == UptimeStatus.Down);
        return down == 0 ? StatusOverall.Operational : down == known.Count ? StatusOverall.Major : StatusOverall.Partial;
    }

    public static string Banner(StatusOverall overall) => overall switch
    {
        StatusOverall.Operational => "All systems operational",
        StatusOverall.Partial => "Partial outage",
        StatusOverall.Major => "Major outage",
        _ => "Status not known yet",
    };

    private static (string Label, string Css) Pill(UptimeStatus state) => state switch
    {
        UptimeStatus.Up => ("Operational", "up"),
        UptimeStatus.Down => ("Down", "down"),
        UptimeStatus.Paused => ("Paused", "none"),
        UptimeStatus.NotChecked => ("Not checked", "none"),
        _ => ("Checking", "none"),
    };

    private static string Bar(double? uptime) => uptime switch
    {
        null => "none",
        >= 99.5 => "up",
        >= 95 => "part",
        _ => "down",
    };

    private static string Percent(double value) => value.ToString(value >= 99.995 ? "0" : value >= 99 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "%";

    public static string Html(StatusPageModel m)
    {
        var h = new StringBuilder();
        h.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="60">
            <meta name="color-scheme" content="light dark">
            """);
        h.Append("<title>").Append(HtmlEncode(m.Title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n<header>\n");
        h.Append("<h1>").Append(HtmlEncode(m.Title)).Append("</h1>\n");
        if (m.Description is { Length: > 0 } description) h.Append("<p class=\"lead\">").Append(HtmlEncode(description)).Append("</p>\n");
        h.Append("</header>\n");
        var tone = m.Overall switch { StatusOverall.Operational => "up", StatusOverall.Partial => "part", StatusOverall.Major => "down", _ => "none" };
        h.Append("<section class=\"banner ").Append(tone).Append("\" role=\"status\"><span class=\"dot\"></span><strong>").Append(Banner(m.Overall)).Append("</strong></section>\n");
        h.Append("<section class=\"apps\" aria-label=\"Apps\">\n");
        if (m.Apps.Count == 0) h.Append("<p class=\"empty\">No apps are listed yet.</p>\n");
        foreach (var app in m.Apps)
        {
            var (label, css) = Pill(app.State);
            h.Append("<article class=\"app\">\n<div class=\"row\"><h2>").Append(HtmlEncode(app.Name)).Append("</h2><span class=\"pill ").Append(css).Append("\">").Append(label).Append("</span></div>\n");
            h.Append("<div class=\"bars\" role=\"img\" aria-label=\"").Append(HtmlEncode($"Uptime of {app.Name} over the last {Days} days")).Append("\">");
            foreach (var day in app.Days)
            {
                var title = $"{day.Date.ToString("MMM d", CultureInfo.InvariantCulture)}: {(day.Uptime is double u ? Percent(u) : "not checked")}";
                h.Append("<i class=\"").Append(Bar(day.Uptime)).Append("\" title=\"").Append(HtmlEncode(title)).Append("\"></i>");
            }
            h.Append("</div>\n<div class=\"meta\"><span>").Append(Days).Append(" days ago</span><span>")
                .Append(app.Uptime90 is double up ? $"{Percent(up)} uptime" : "No checks yet");
            if (m.ResponseTimes && app.ResponseMs is int ms) h.Append(" · ").Append(ms.ToString(CultureInfo.InvariantCulture)).Append(" ms");
            h.Append("</span><span>Today</span></div>\n</article>\n");
        }
        h.Append("</section>\n<section class=\"incidents\" aria-labelledby=\"incidents-title\">\n<h2 id=\"incidents-title\">Past ").Append(IncidentDays).Append(" days</h2>\n");
        if (m.Incidents.Count == 0) h.Append("<p class=\"empty\">No incidents in the last ").Append(IncidentDays).Append(" days.</p>\n");
        else
        {
            h.Append("<ul>\n");
            foreach (var incident in m.Incidents)
                h.Append("<li class=\"").Append(incident.Ended is null ? "open" : "closed").Append("\">").Append(HtmlEncode(incident.Summary)).Append("</li>\n");
            h.Append("</ul>\n");
        }
        h.Append("</section>\n<footer>Updated ").Append(m.At.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)).Append(" UTC · refreshes every minute</footer>\n");
        h.Append("</main>\n</body>\n</html>\n");
        return h.ToString();
    }

    public static string Json(StatusPageModel m)
    {
        static string Time(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var apps = new JsonArray();
        foreach (var app in m.Apps)
        {
            var o = new JsonObject
            {
                ["name"] = app.Name,
                ["status"] = app.State switch { UptimeStatus.Up => "up", UptimeStatus.Down => "down", UptimeStatus.Paused => "paused", UptimeStatus.NotChecked => "not_checked", _ => "unknown" },
                ["uptime90"] = app.Uptime90,
                ["days"] = new JsonArray([.. app.Days.Select(d => (JsonNode)new JsonObject
                {
                    ["date"] = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["uptime"] = d.Uptime,
                })]),
            };
            if (m.ResponseTimes) o["responseMs"] = app.ResponseMs;
            apps.Add(o);
        }
        var body = new JsonObject
        {
            ["title"] = m.Title,
            ["description"] = m.Description,
            ["status"] = m.Overall switch { StatusOverall.Operational => "operational", StatusOverall.Partial => "partial_outage", StatusOverall.Major => "major_outage", _ => "unknown" },
            ["summary"] = Banner(m.Overall),
            ["updated"] = Time(m.At),
            ["apps"] = apps,
            ["incidents"] = new JsonArray([.. m.Incidents.Select(i => (JsonNode)new JsonObject
            {
                ["app"] = i.App,
                ["started"] = Time(i.Started),
                ["ended"] = i.Ended is { } e ? Time(e) : null,
                ["cause"] = i.Cause,
                ["summary"] = i.Summary,
            })]),
        };
        return body.ToJsonString();
    }

    private const string Css = """
        :root{--bg:#f5f6f8;--card:#ffffff;--text:#15171c;--muted:#5d6472;--line:#e2e5ea;--up:#1f9d55;--part:#d18a00;--down:#d43c3c;--none:#d5d9e0;--up-soft:#e7f6ee;--part-soft:#fcf2df;--down-soft:#fbe8e8;--none-soft:#eef0f3}
        @media (prefers-color-scheme:dark){:root{--bg:#0c0e12;--card:#151920;--text:#e7e9ee;--muted:#9ba3b1;--line:#262b35;--up:#3fbf75;--part:#e6a23c;--down:#ef6666;--none:#323946;--up-soft:#13281d;--part-soft:#2b2214;--down-soft:#2e1717;--none-soft:#1d222b}}
        *{box-sizing:border-box}
        html,body{margin:0}
        body{background:var(--bg);color:var(--text);font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;-webkit-text-size-adjust:100%}
        main{max-width:760px;margin:0 auto;padding:40px 16px 48px}
        header h1{font-size:1.75rem;line-height:1.2;margin:0 0 6px;letter-spacing:-.015em;font-weight:680}
        .lead{color:var(--muted);margin:0 0 4px}
        .banner{display:flex;align-items:center;gap:10px;margin:22px 0 20px;padding:14px 18px;border-radius:12px;border:1px solid var(--line);background:var(--card);font-size:1.02rem}
        .banner .dot{width:10px;height:10px;border-radius:50%;flex:none;background:var(--none)}
        .banner.up{background:var(--up-soft);border-color:transparent}.banner.up .dot{background:var(--up)}
        .banner.part{background:var(--part-soft);border-color:transparent}.banner.part .dot{background:var(--part)}
        .banner.down{background:var(--down-soft);border-color:transparent}.banner.down .dot{background:var(--down)}
        .apps{background:var(--card);border:1px solid var(--line);border-radius:14px;overflow:hidden}
        .app{padding:16px 18px}.app+.app{border-top:1px solid var(--line)}
        .row{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:10px}
        .row h2{font-size:1rem;margin:0;font-weight:620;overflow-wrap:anywhere}
        .pill{font-size:.78rem;font-weight:600;padding:3px 10px;border-radius:999px;white-space:nowrap;background:var(--none-soft);color:var(--muted)}
        .pill.up{background:var(--up-soft);color:var(--up)}.pill.down{background:var(--down-soft);color:var(--down)}
        .bars{display:flex;gap:2px;height:30px;align-items:stretch}
        .bars i{flex:1 1 0;min-width:1px;border-radius:2px;background:var(--none)}
        .bars i.up{background:var(--up)}.bars i.part{background:var(--part)}.bars i.down{background:var(--down)}
        .meta{display:flex;justify-content:space-between;gap:8px;color:var(--muted);font-size:.8rem;margin-top:6px}
        .meta span:nth-child(2){text-align:center;color:var(--text)}
        .incidents{margin-top:28px}
        .incidents h2{font-size:1.05rem;margin:0 0 10px}
        .incidents ul{list-style:none;margin:0;padding:0;background:var(--card);border:1px solid var(--line);border-radius:14px}
        .incidents li{padding:12px 16px 12px 34px;position:relative;overflow-wrap:anywhere}
        .incidents li+li{border-top:1px solid var(--line)}
        .incidents li::before{content:"";position:absolute;left:16px;top:20px;width:8px;height:8px;border-radius:50%;background:var(--up)}
        .incidents li.open::before{background:var(--down)}
        .empty{color:var(--muted);margin:0;padding:14px 18px}
        footer{color:var(--muted);font-size:.8rem;margin-top:28px;text-align:center}
        @media (max-width:520px){main{padding:24px 16px 36px}header h1{font-size:1.45rem}.bars{gap:1px;height:26px}.app{padding:14px}}
        """;
}
