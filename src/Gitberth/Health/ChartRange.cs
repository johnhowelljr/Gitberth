namespace Gitberth.Health;

/// <summary>How much of the charts to show: the Health page's and an app's.</summary>
public enum ChartRange { Hour, SixHours, Day, Week, Month, Quarter }

/// <summary>
/// Each range's window, the step its points are apart, and where they come from: every reading (1 hour), 30-second
/// averages of the readings (6 hours), the 5-minute roll-ups (24 hours), the 1-hour ones (7 and 30 days), and those
/// averaged over 3 hours (90 days), so no chart draws more than about 720 points.
/// </summary>
public static class ChartRanges
{
    public static readonly IReadOnlyList<ChartRange> All = Enum.GetValues<ChartRange>();

    public static TimeSpan Window(ChartRange r) => r switch
    {
        ChartRange.Hour => ContainerStatsCollector.RecentWindow,
        ChartRange.SixHours => ContainerStatsCollector.Kept,
        ChartRange.Day => TimeSpan.FromHours(24),
        ChartRange.Week => TimeSpan.FromDays(7),
        ChartRange.Month => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    public static TimeSpan Step(ChartRange r) => r switch
    {
        ChartRange.Hour => ContainerStatsCollector.Interval,
        ChartRange.SixHours => ContainerStatsCollector.HistoryStep,
        ChartRange.Day => StatsStore.FiveMinutes,
        ChartRange.Week or ChartRange.Month => StatsStore.OneHour,
        _ => TimeSpan.FromHours(3),
    };

    /// <summary>Where a chart's line breaks: a whole step (and a half) missing; the readings' own gap for the hour.</summary>
    public static TimeSpan Gap(ChartRange r) => r == ChartRange.Hour ? ContainerStatsCollector.MaxGap : Step(r) * 1.5;

    /// <summary>Read from stats.db's roll-ups, not from what the sampler holds.</summary>
    public static bool Stored(ChartRange r) => r >= ChartRange.Day;

    /// <summary>"1 hour", "7 days": the range's button.</summary>
    public static string Label(ChartRange r) => r switch
    {
        ChartRange.Hour => "1 hour",
        ChartRange.SixHours => "6 hours",
        ChartRange.Day => "24 hours",
        ChartRange.Week => "7 days",
        ChartRange.Month => "30 days",
        _ => "90 days",
    };

    /// <summary>"the last 7 days": the range in a sentence.</summary>
    public static string InWords(ChartRange r) => r == ChartRange.Hour ? "the last hour" : "the last " + Label(r);

    /// <summary>"Last 7 days · every hour": what a chart's card says under its title.</summary>
    public static string Sub(ChartRange r) => r switch
    {
        ChartRange.Hour => "Last hour · every 5 s",
        ChartRange.SixHours => "Last 6 hours · every 30 s",
        ChartRange.Day => "Last 24 hours · every 5 min",
        ChartRange.Week => "Last 7 days · every hour",
        ChartRange.Month => "Last 30 days · every hour",
        _ => "Last 90 days · every 3 hours",
    };

    /// <summary>", averaged over 5 minutes": how a table of the points says what each is; nothing for every reading.</summary>
    public static string Averaged(ChartRange r) => r switch
    {
        ChartRange.Hour => "",
        ChartRange.SixHours => ", averaged over 30 seconds",
        ChartRange.Day => ", averaged over 5 minutes",
        ChartRange.Week or ChartRange.Month => ", averaged over an hour",
        _ => ", averaged over 3 hours",
    };
}
