using System.Globalization;
using System.Text;

namespace Gitberth.Components.Shared;

/// <summary>The SVG paths of a sparkline: a line through the readings and the area under it, in a box of the given size.</summary>
public static class SparkPath
{
    /// <summary>How many readings a sparkline draws: the last 30.</summary>
    public const int Points = 30;

    public static string Line(IReadOnlyList<double> values, double? max, double width, double height) =>
        Build(values, max, width, height, area: false);

    public static string Area(IReadOnlyList<double> values, double? max, double width, double height) =>
        Build(values, max, width, height, area: true);

    private static string Build(IReadOnlyList<double> values, double? max, double width, double height, bool area)
    {
        var shown = values.Count > Points ? values.Skip(values.Count - Points).ToList() : values.ToList();
        if (shown.Count < 2) return "";
        var top = max is > 0 ? max.Value : Math.Max(shown.Max(), 1e-9);
        var step = width / (shown.Count - 1);
        var sb = new StringBuilder();
        for (var i = 0; i < shown.Count; i++)
        {
            var x = i * step;
            // A 1 px margin top and bottom, so the line's stroke isn't cut.
            var y = height - 1 - Math.Clamp(shown[i] / top, 0, 1) * (height - 2);
            sb.Append(i == 0 ? 'M' : 'L').Append(N(x)).Append(',').Append(N(y)).Append(' ');
        }
        if (area) sb.Append('L').Append(N(width)).Append(',').Append(N(height)).Append(" L0,").Append(N(height)).Append(" Z");
        return sb.ToString().TrimEnd();
    }

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
