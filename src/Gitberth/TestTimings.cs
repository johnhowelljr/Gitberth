using System.Globalization;
using Gitberth.Deploy;
using Gitberth.Network;
using Gitberth.Previews;
using Gitberth.Services;

namespace Gitberth;

/// <summary>
/// Shorter waits for Gitberth's own end-to-end rig: GITBERTH_TEST_TIMINGS="stableFor=3,pullsEvery=20", in seconds. Only
/// tests/e2e/rig/compose.e2e.yml sets it; docker-compose.yml never passes it, and without it nothing here runs. Each
/// setting can only shorten its wait, anything it doesn't understand stops Gitberth from starting, and while it's set
/// Gitberth says so at start-up and in Settings › System. What the UI says about an interval doesn't change.
/// </summary>
public sealed class TestTimings
{
    public const string Variable = "GITBERTH_TEST_TIMINGS";

    // The shortest and the longest wait a setting takes, in seconds: a wait of nothing would make a loop spin.
    private const double Shortest = 0.1;
    private const double Longest = 3600;

    private sealed record Knob(string What, Func<IServiceProvider, TimeSpan> Get, Action<IServiceProvider, TimeSpan> Set);

    private static readonly IReadOnlyDictionary<string, Knob> Knobs = new Dictionary<string, Knob>(StringComparer.Ordinal)
    {
        ["stableFor"] = new("how long a new container has to stay up, in a deploy and in a container change",
            sp => Min(sp.GetRequiredService<DeployPipeline>().StableFor, sp.GetRequiredService<PortChanger>().StableFor),
            (sp, v) =>
            {
                sp.GetRequiredService<DeployPipeline>().StableFor = v;
                sp.GetRequiredService<PortChanger>().StableFor = v;
            }),
        ["pullsEvery"] = new("how often a repo's pull requests are listed again",
            sp => sp.GetRequiredService<PreviewService>().PullsEvery,
            (sp, v) => sp.GetRequiredService<PreviewService>().PullsEvery = v),
        ["restartMinute"] = new("how long a minute of a restart schedule lasts",
            sp => sp.GetRequiredService<RestartScheduler>().MinuteLength,
            (sp, v) => sp.GetRequiredService<RestartScheduler>().MinuteLength = v),
        ["restartTick"] = new("how often restart schedules are looked at",
            sp => sp.GetRequiredService<RestartScheduler>().TickEvery,
            (sp, v) => sp.GetRequiredService<RestartScheduler>().TickEvery = v),
    };

    private TestTimings(IReadOnlyDictionary<string, TimeSpan> values) => Values = values;

    /// <summary>Each setting and its wait, by name.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Values { get; }

    /// <summary>
    /// The settings in <paramref name="text"/>; null when it's blank (unset), which leaves every wait as it is. Throws on
    /// anything it doesn't understand: an empty or unknown setting, one without seconds, seconds out of range, one set twice.
    /// </summary>
    public static TestTimings? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new SortedDictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                throw new InvalidOperationException($"{Variable}: '{text.Trim()}' has an empty setting; it takes setting=seconds pairs, separated by commas.");
            var eq = part.IndexOf('=');
            var key = (eq < 0 ? part : part[..eq]).Trim();
            var number = eq < 0 ? "" : part[(eq + 1)..].Trim();
            if (!Knobs.ContainsKey(key))
                throw new InvalidOperationException($"{Variable}: '{key}' isn't one of its settings ({string.Join(", ", Knobs.Keys)}).");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is not (>= Shortest and <= Longest))
                throw new InvalidOperationException($"{Variable}: {key} takes seconds, from {Seconds(TimeSpan.FromSeconds(Shortest))} to {Seconds(TimeSpan.FromSeconds(Longest))}: '{part}'.");
            if (!values.TryAdd(key, TimeSpan.FromSeconds(seconds)))
                throw new InvalidOperationException($"{Variable}: {key} is set twice.");
        }
        return new TestTimings(values);
    }

    /// <summary>
    /// Sets each wait on the running services. Throws, and changes nothing, when one wouldn't be shorter than it is: test
    /// timings only ever shorten a wait.
    /// </summary>
    public void Apply(IServiceProvider services)
    {
        foreach (var (key, value) in Values)
        {
            var knob = Knobs[key];
            var now = knob.Get(services);
            if (value >= now)
                throw new InvalidOperationException(
                    $"{Variable}: {key}={Seconds(value)} isn't shorter than {Seconds(now)} s ({knob.What}); test timings only shorten waits.");
        }
        foreach (var (key, value) in Values) Knobs[key].Set(services, value);
    }

    /// <summary>"pullsEvery=20,restartMinute=20,restartTick=2,stableFor=3": the settings by name, in seconds.</summary>
    public override string ToString() => string.Join(",", Values.Select(v => $"{v.Key}={Seconds(v.Value)}"));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
