using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A CPython release line, like 3.12: what picks the python:&lt;version&gt; image.</summary>
internal readonly record struct PyVersion(int Major, int Minor) : IComparable<PyVersion>
{
    public int CompareTo(PyVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Which Python a project asks for, and whether that release is still supported.</summary>
internal static partial class PythonVersions
{
    /// <summary>
    /// Used when the repo doesn't say. 3.14 came out in October 2025, and a year on the common packages (numpy, pandas,
    /// pydantic-core, psycopg, uvloop, msgspec, Pillow…) ship wheels for it. 3.15 came out in October 2026 and many don't
    /// yet, so their installs would fall back to compiling. Move this to 3.15 once they've caught up.
    /// </summary>
    public static readonly PyVersion Default = new(3, 14);

    public const string DefaultSource = "Gitberth's default";

    // The 3.x releases Gitberth knows, and the month each one's support ends (https://devguide.python.org/versions/).
    private static readonly Dictionary<int, DateOnly> EndOfLife = new()
    {
        [6] = new(2021, 12, 1), [7] = new(2023, 6, 1), [8] = new(2024, 10, 1), [9] = new(2025, 10, 1), [10] = new(2026, 10, 1),
        [11] = new(2027, 10, 1), [12] = new(2028, 10, 1), [13] = new(2029, 10, 1), [14] = new(2030, 10, 1), [15] = new(2031, 10, 1),
    };

    /// <summary>The first "3.12" in a version file or runtime.txt ("3.12.4", "python-3.12.4", "cpython-3.13"); null when there's none.</summary>
    public static PyVersion? FromExact(string text)
    {
        var m = ExactVersion().Match(text);
        return m.Success ? new PyVersion(Int(m.Groups[1].Value), Int(m.Groups[2].Value)) : null;
    }

    /// <summary>
    /// The release to use for a version range (PEP 440 like "&gt;=3.10,&lt;3.13", or Poetry's "^3.11"): the default when it
    /// fits, else the newest older release that does, else a newer one.
    /// </summary>
    public static PyVersion? FromSpec(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return null;
        if (spec is "*") return Default;
        foreach (var candidate in Candidates())
        {
            if (Satisfies(candidate, spec)) return candidate;
        }
        // Nothing Gitberth knows fits (">=3.16", say): go with the version it names.
        return FromExact(spec);
    }

    private static IEnumerable<PyVersion> Candidates() =>
        new[] { Default }
            .Concat(EndOfLife.Keys.Where(m => m < Default.Minor).OrderByDescending(m => m).Select(m => new PyVersion(3, m)))
            .Concat(EndOfLife.Keys.Where(m => m > Default.Minor).Order().Select(m => new PyVersion(3, m)));

    /// <summary>
    /// Whether some patch release of <paramref name="version"/> satisfies a range. PEP 440 operators (==, !=, &lt;, &lt;=,
    /// &gt;, &gt;=, ~=, ===, wildcards) and Poetry's (^, ~, bare versions, || between alternatives) are understood;
    /// clauses are separated by commas or spaces.
    /// </summary>
    internal static bool Satisfies(PyVersion version, string spec)
    {
        foreach (var alternative in spec.Split("||"))
        {
            var clauses = Clause().Matches(alternative);
            if (clauses.Count > 0 && clauses.All(c => Holds(version, c))) return true;
        }
        return false;
    }

    private static bool Holds(PyVersion v, Match clause)
    {
        var op = clause.Groups["op"].Value;
        var major = Int(clause.Groups["major"].Value);
        var minorText = clause.Groups["minor"].Success ? clause.Groups["minor"].Value : null;
        var patchText = clause.Groups["patch"].Success ? clause.Groups["patch"].Value : null;
        var minorWild = minorText is "*" or "x" or "X";
        var patchWild = patchText is "*" or "x" or "X";
        var minor = minorText is null || minorWild ? 0 : Int(minorText);
        var patch = patchText is null || patchWild ? 0 : Int(patchText);
        var target = new PyVersion(major, minor);
        var sameMinor = v.Major == major && v.Minor == minor;
        return op switch
        {
            "" or "=" or "==" or "===" => minorText is null || minorWild ? v.Major == major : sameMinor,
            // "!=3.12.*" rules out the whole release; "!=3.12.4" only one patch of it.
            "!=" => minorText is null || minorWild ? v.Major != major : !(patchWild && sameMinor),
            ">=" or ">" => v.CompareTo(target) >= 0,
            "<" => patch > 0 ? v.CompareTo(target) <= 0 : v.CompareTo(target) < 0,
            "<=" => v.CompareTo(target) <= 0,
            "~=" => patchText is not null && !patchWild ? sameMinor : v.Major == major && v.Minor >= minor,
            "^" => v.Major == major && v.Minor >= minor,
            "~" => minorText is null ? v.Major == major : sameMinor,
            _ => false,
        };
    }

    /// <summary>End-of-life and too-new warnings for the release a project runs on.</summary>
    public static IEnumerable<string> Warnings(PyVersion version, DateOnly today)
    {
        if (version.Major < 3)
        {
            yield return $"Python {version} is long past end-of-life, and Gitberth's Dockerfile needs Python 3. Customize the Dockerfile to build it.";
            yield break;
        }
        if (!EndOfLife.TryGetValue(version.Minor, out var endOfLife))
        {
            yield return version.Minor > EndOfLife.Keys.Max()
                ? $"Python {version} is newer than Gitberth knows about, so the build fails if there's no python:{version}-slim image yet."
                : $"Python {version} is past end-of-life and gets no security fixes. Consider moving to Python {Default}.";
            yield break;
        }
        if (today >= endOfLife)
            yield return $"Python {version} is past end-of-life ({endOfLife.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}) and gets no security fixes. Consider moving to Python {Default}.";
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![\d.])(\d)\.(\d{1,2})(?!\d)")]
    private static partial Regex ExactVersion();

    [GeneratedRegex(@"(?<op>===|==|!=|~=|<=|>=|<|>|\^|~|=)?\s*v?(?<major>\d+)(?:\.(?<minor>\d+|\*|[xX]))?(?:\.(?<patch>\d+|\*|[xX]))?(?:\.\d+)*")]
    private static partial Regex Clause();
}
