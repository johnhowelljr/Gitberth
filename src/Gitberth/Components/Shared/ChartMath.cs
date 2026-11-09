namespace Gitberth.Components.Shared;

/// <summary>One line on a <see cref="TimeChart"/>. Slot picks the series color; values line up with the chart's times.</summary>
public sealed record ChartSeries(string Name, int Slot, IReadOnlyList<double?> Values);

public static class ChartMath
{
    /// <summary>The top of the y-axis: the next 1, 2 or 5 step at or above the largest value, and never below the floor.</summary>
    public static double NiceMax(double max, double floor)
    {
        var target = Math.Max(max, floor);
        if (target <= 0 || double.IsNaN(target)) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var step = target / magnitude;
        // The tolerance stops floating-point noise like 5.000000001 from doubling the axis.
        var nice = step <= 1.0001 ? 1 : step <= 2.0001 ? 2 : step <= 5.0001 ? 5 : 10;
        return nice * magnitude;
    }

    private static readonly double[] ByteSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500];

    /// <summary>
    /// The top of a bytes axis: 1, 2 or 5 times a power of ten of a binary unit (200 MiB, 5 GiB), or the next unit up
    /// (1 GiB, not 1000 MiB), so its labels read as round numbers. Never below the floor.
    /// </summary>
    public static double NiceMaxBytes(double max, double floor)
    {
        var target = Math.Max(max, floor);
        if (target <= 0 || double.IsNaN(target)) return 1024;
        var unit = Math.Pow(1024, Math.Max(0, Math.Floor(Math.Log(target, 1024))));
        foreach (var step in ByteSteps)
            if (step * unit >= target * 0.9999) return step * unit;
        return unit * 1024;
    }
}
