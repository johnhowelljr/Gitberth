using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Gitberth.Detection;

/// <summary>
/// A cron expression as Vercel reads one: five fields (minute, hour, day of the month, month, day of the week), always
/// in UTC. A field takes *, numbers, ranges (1-5), lists (1,3) and steps (*/5, 1-10/2). Names like MON aren't supported,
/// and when one of the day fields is set the other must be *. 7 is Sunday, like 0.
/// </summary>
internal sealed class CronSchedule
{
    /// <param name="Last">The highest number accepted, which is above <paramref name="Max"/> only for Sunday's 7.</param>
    /// <param name="OutOfRange">The sentence for a number outside the field, with {0} for the number.</param>
    private sealed record Field(string Name, int Min, int Max, int Last, string OutOfRange, string? Names = null);

    private static readonly Field[] Fields =
    [
        new("minute", 0, 59, 59, "There's no minute {0}: minutes go from 0 to 59."),
        new("hour", 0, 23, 23, "There's no hour {0}: hours go from 0 to 23."),
        new("day of the month", 1, 31, 31, "There's no day {0} of the month: days go from 1 to 31."),
        new("month", 1, 12, 12, "There's no month {0}: months go from 1 to 12.", "write months as numbers, from 1 (January) to 12"),
        new("day of the week", 0, 6, 7, "There's no day {0} of the week: days of the week go from 0 (Sunday) to 6 (Saturday).",
            "write days of the week as numbers, from 0 (Sunday) to 6 (Saturday)"),
    ];

    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static readonly string[] MonthNames =
        ["", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private readonly string[] _fields;
    // One bit per value each field allows, in field order.
    private readonly ulong[] _sets;

    private CronSchedule(string text, string[] fields, ulong[] sets) => (Text, _fields, _sets) = (text, fields, sets);

    public string Text { get; }

    /// <param name="error">Why the expression can't be used, as a sentence, when it can't.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CronSchedule? schedule, out string error)
    {
        schedule = null;
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "The schedule is empty.";
            return false;
        }
        var fields = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"\"{text}\" needs five fields: minute, hour, day of the month, month and day of the week.";
            return false;
        }
        var sets = new ulong[5];
        for (var i = 0; i < 5; i++)
        {
            if (ParseField(fields[i], Fields[i], out sets[i]) is not { } problem) continue;
            error = problem;
            return false;
        }
        if (fields[2] != "*" && fields[4] != "*")
        {
            error = $"\"{text}\" sets both the day of the month and the day of the week, which Vercel doesn't allow: set one of them to *.";
            return false;
        }
        schedule = new CronSchedule(text, fields, sets);
        error = "";
        return true;
    }

    /// <summary>Whether the job runs in this minute. Only one day field is ever set, so both always have to match.</summary>
    public bool Matches(DateTime utc) =>
        Has(0, utc.Minute) && Has(1, utc.Hour) && Has(2, utc.Day) && Has(3, utc.Month) && Has(4, (int)utc.DayOfWeek);

    /// <summary>The first minute after <paramref name="after"/> that the job runs in; null when it never does, like on February 30th.</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Ticks - after.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        // February 29th can be eight years away (2096 to 2104).
        for (var end = t.AddYears(9); t < end;)
        {
            if (!Has(3, t.Month)) t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            else if (!Has(2, t.Day) || !Has(4, (int)t.DayOfWeek)) t = t.Date.AddDays(1);
            else if (!Has(1, t.Hour)) t = t.Date.AddHours(t.Hour + 1);
            else if (!Has(0, t.Minute)) t = t.AddMinutes(1);
            else return t;
        }
        return null;
    }

    /// <summary>A short description like "every day at 06:00 UTC", or null when the expression doesn't make an easy one.</summary>
    public string? Describe()
    {
        var (minute, hour, day, month, weekday) = (_fields[0], _fields[1], _fields[2], _fields[3], _fields[4]);
        if (hour == "*" || Step(hour) is not null)
        {
            // Several times a day: only described for jobs that run every day.
            if (day != "*" || month != "*" || weekday != "*") return null;
            if (hour == "*" && minute == "*") return "every minute";
            if (hour == "*" && Step(minute) is int every) return every == 1 ? "every minute" : $"every {every} minutes";
            if (Number(minute) is not int past) return null;
            var hours = Step(hour) is int n and > 1 ? $"every {n} hours" : "every hour";
            return past == 0 ? hours : $"{hours} at :{past:00}";
        }
        if (Number(minute) is not int at || Numbers(hour) is not { } times || Days(day, month, weekday) is not { } days) return null;
        return $"{days} at {And(times.Select(h => $"{h:00}:{at:00}"))} UTC";
    }

    private bool Has(int field, int value) => (_sets[field] >> value & 1) != 0;

    private static string? ParseField(string text, Field field, out ulong set)
    {
        set = 0;
        if (text.Any(char.IsLetter) && field.Names is not null) return $"\"{text}\" isn't supported: {field.Names}.";
        foreach (var part in text.Split(','))
        {
            var slash = part.IndexOf('/');
            var range = slash < 0 ? part : part[..slash];
            var step = 1;
            if (slash >= 0 && !TryNumber(part[(slash + 1)..], out step)) return $"\"{text}\" isn't a valid {field.Name}.";
            if (step == 0) return $"The step in \"{part}\" must be at least 1.";
            int from, to;
            var dash = range.IndexOf('-');
            if (range == "*")
            {
                (from, to) = (field.Min, field.Max);
            }
            else if (dash < 0)
            {
                if (!TryNumber(range, out from)) return $"\"{text}\" isn't a valid {field.Name}.";
                // "5/15" goes from 5 to the end of the field, as crontab.guru reads it.
                to = slash < 0 ? from : Math.Max(from, field.Max);
            }
            else if (!TryNumber(range[..dash], out from) || !TryNumber(range[(dash + 1)..], out to))
            {
                return $"\"{text}\" isn't a valid {field.Name}.";
            }
            if (from < field.Min || from > field.Last) return string.Format(CultureInfo.InvariantCulture, field.OutOfRange, from);
            if (to < field.Min || to > field.Last) return string.Format(CultureInfo.InvariantCulture, field.OutOfRange, to);
            if (from > to) return $"\"{range}\" goes backwards: put the smaller number first.";
            // Only a day of the week can be above its field (7, for Sunday), and it wraps round to 0. A long, so a huge
            // step can't overflow back into the field.
            for (long v = from; v <= to; v += step) set |= 1UL << (int)(v % (field.Max + 1));
        }
        return null;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int? Number(string text) => TryNumber(text, out var n) ? n : null;

    private static int? Step(string text) => text.StartsWith("*/", StringComparison.Ordinal) ? Number(text[2..]) : null;

    private static List<int>? Numbers(string text)
    {
        var numbers = text.Split(',').Select(Number).ToList();
        return numbers.All(n => n is not null) ? numbers.Select(n => n!.Value).ToList() : null;
    }

    private static string? Days(string day, string month, string weekday)
    {
        if (weekday != "*")
        {
            if (month != "*") return null;
            if (Number(weekday) is int d) return $"every {DayNames[d]}";
            var dash = weekday.IndexOf('-');
            if (dash > 0 && Number(weekday[..dash]) is int first && Number(weekday[(dash + 1)..]) is int last) return $"{DayNames[first]} to {DayNames[last]}";
            return Numbers(weekday) is { } list ? $"every {And(list.Select(n => DayNames[n]))}" : null;
        }
        if (day == "*") return month == "*" ? "every day" : Number(month) is int only ? $"every day in {MonthNames[only]}" : null;
        if (Number(day) is not int date) return null;
        if (month == "*") return $"on the {Ordinal(date)} of every month";
        return Number(month) is int m ? $"every year on {date} {MonthNames[m]}" : null;
    }

    private static string Ordinal(int n) =>
        n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string And(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }
}
