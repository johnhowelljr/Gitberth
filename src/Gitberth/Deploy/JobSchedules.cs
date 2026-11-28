using System.Globalization;
using Gitberth.Detection;

namespace Gitberth.Deploy;

/// <summary>How a scheduled command's schedule is chosen on its card.</summary>
public enum JobEvery { Minutes, Hourly, Daily, Weekly, Cron }

/// <summary>A schedule as the card's fields hold it: its kind, and the numbers it needs (or a cron expression).</summary>
public sealed record JobScheduleForm(JobEvery Every, int Minutes = 5, int Minute = 0, int Hour = 3, DayOfWeek Day = DayOfWeek.Monday, string Cron = "");

/// <summary>
/// Scheduled commands' schedules (B2): what the card's choices write (every N minutes, hourly at :MM, daily at HH:MM, weekly
/// on a day at HH:MM, or a cron expression), saved as one cron expression, read back into the choices, said in words, and
/// when each runs next, all in Gitberth's time zone (the TZ it's given; UTC when it has none).
/// </summary>
public static class JobSchedules
{
    /// <summary>Gitberth's time zone, as the card names it next to the schedule.</summary>
    public static string ZoneName(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return zone == TimeZoneInfo.Utc || zone.Id is "UTC" or "Etc/UTC" or "Universal" ? "UTC" : zone.Id;
    }

    /// <summary>The cron expression the card's choices make; refused (with why) when a number is out of range.</summary>
    public static (string? Cron, string? Problem) ToCron(JobScheduleForm form) => form.Every switch
    {
        JobEvery.Minutes when form.Minutes is < 1 or > 59 => (null, "Every 1 to 59 minutes: for longer, run it hourly or daily."),
        JobEvery.Minutes => (form.Minutes == 1 ? "* * * * *" : $"*/{form.Minutes.ToString(CultureInfo.InvariantCulture)} * * * *", null),
        _ when form.Minute is < 0 or > 59 => (null, "The minute is from 0 to 59."),
        JobEvery.Hourly => ($"{form.Minute.ToString(CultureInfo.InvariantCulture)} * * * *", null),
        _ when form.Hour is < 0 or > 23 && form.Every is JobEvery.Daily or JobEvery.Weekly => (null, "The hour is from 0 to 23."),
        JobEvery.Daily => ($"{form.Minute.ToString(CultureInfo.InvariantCulture)} {form.Hour.ToString(CultureInfo.InvariantCulture)} * * *", null),
        JobEvery.Weekly => ($"{form.Minute.ToString(CultureInfo.InvariantCulture)} {form.Hour.ToString(CultureInfo.InvariantCulture)} * * {((int)form.Day).ToString(CultureInfo.InvariantCulture)}", null),
        _ => CronSchedule.TryParse(form.Cron, out var parsed, out var error) ? (parsed.Text, null) : (null, error.Replace("which Vercel doesn't allow", "which isn't allowed", StringComparison.Ordinal)),
    };

    /// <summary>The card's choices for a saved expression: the one it was made from, or a cron expression of its own.</summary>
    public static JobScheduleForm FormOf(string cron)
    {
        var f = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 5 && f[2] == "*" && f[3] == "*")
        {
            if (f[0] == "*" && f[1] == "*" && f[4] == "*") return new JobScheduleForm(JobEvery.Minutes, Minutes: 1);
            if (f[0].StartsWith("*/", StringComparison.Ordinal) && Num(f[0][2..], 1, 59) is int every && f[1] == "*" && f[4] == "*")
                return new JobScheduleForm(JobEvery.Minutes, Minutes: every);
            if (Num(f[0], 0, 59) is int minute)
            {
                if (f[1] == "*" && f[4] == "*") return new JobScheduleForm(JobEvery.Hourly, Minute: minute);
                if (Num(f[1], 0, 23) is int hour)
                {
                    if (f[4] == "*") return new JobScheduleForm(JobEvery.Daily, Minute: minute, Hour: hour);
                    if (Num(f[4], 0, 7) is int day) return new JobScheduleForm(JobEvery.Weekly, Minute: minute, Hour: hour, Day: (DayOfWeek)(day % 7));
                }
            }
        }
        return new JobScheduleForm(JobEvery.Cron, Cron: cron);
    }

    /// <summary>The schedule in words: "every 5 minutes", "daily at 03:00", "every Monday at 03:00", or the expression.</summary>
    public static string Describe(string cron)
    {
        var form = FormOf(cron);
        return form.Every switch
        {
            JobEvery.Minutes => form.Minutes == 1 ? "every minute" : $"every {form.Minutes} minutes",
            JobEvery.Hourly => $"hourly at :{form.Minute:00}",
            JobEvery.Daily => $"daily at {form.Hour:00}:{form.Minute:00}",
            JobEvery.Weekly => $"every {form.Day} at {form.Hour:00}:{form.Minute:00}",
            _ => $"cron {cron}",
        };
    }

    /// <summary>When it runs next after <paramref name="afterUtc"/>, in UTC (the expression read in <paramref name="zone"/>); null when never.</summary>
    public static DateTime? NextUtc(string cron, DateTime afterUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (!CronSchedule.TryParse(cron, out var schedule, out _)) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
        // The schedule matches wall-clock times: a time the clock skips (when it springs forward) doesn't come that day, so
        // the next match after it is the one; a time that comes twice (when it falls back) runs once, the second time.
        for (var at = schedule.Next(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)); at is { } next; at = schedule.Next(next))
        {
            var wall = DateTime.SpecifyKind(next, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall)) continue;
            return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
        }
        return null;
    }

    /// <summary>Whether a run is due: its next time after the last look has come.</summary>
    public static bool Due(string cron, DateTime lastLookUtc, DateTime nowUtc, TimeZoneInfo? zone = null) =>
        NextUtc(cron, lastLookUtc, zone) is { } next && next <= nowUtc;

    private static int? Num(string text, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : null;
}
