namespace Gitberth.Agents;

/// <summary>
/// Lines of a log as a tool returns them, bounded: each line cut at <see cref="MaxLine"/> characters (ending with "…"), and
/// at most <see cref="MaxTotal"/> characters in all, the oldest lines dropped first. <see cref="Truncated"/> says whether
/// anything was cut. Lines can be added from several threads at once (a process's output and its errors).
/// </summary>
public sealed class LogLines(int maxLine = LogLines.MaxLine, int maxTotal = LogLines.MaxTotal)
{
    /// <summary>The longest line kept whole, in characters.</summary>
    public const int MaxLine = 8 * 1024;

    /// <summary>The most kept in all, in characters (about 1 MB).</summary>
    public const int MaxTotal = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private long _total;
    private bool _truncated;

    /// <summary>Whether a line was cut, or old lines dropped, to keep within the limits.</summary>
    public bool Truncated
    {
        get
        {
            lock (_gate) return _truncated;
        }
    }

    /// <summary>The lines kept, oldest first.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate) return _lines.ToList();
        }
    }

    public void Add(string line)
    {
        if (line.Length > maxLine)
        {
            // Never between the two halves of a surrogate pair.
            var cut = char.IsHighSurrogate(line[maxLine - 1]) ? maxLine - 1 : maxLine;
            line = line[..cut] + "…";
            lock (_gate) _truncated = true;
        }
        lock (_gate)
        {
            _lines.Enqueue(line);
            _total += line.Length;
            while (_total > maxTotal && _lines.Count > 0)
            {
                _total -= _lines.Dequeue().Length;
                _truncated = true;
            }
        }
    }

    /// <summary>The lines given, bounded, and whether anything was cut.</summary>
    public static (IReadOnlyList<string> Lines, bool Truncated) Of(IEnumerable<string> lines, int maxLine = MaxLine, int maxTotal = MaxTotal)
    {
        var kept = new LogLines(maxLine, maxTotal);
        foreach (var line in lines) kept.Add(line);
        return (kept.Lines, kept.Truncated);
    }
}
