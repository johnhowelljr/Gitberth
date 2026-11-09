namespace Gitberth.Components.Shared;

/// <summary>Splits a log line into its timestamp and text and decides how it should look.</summary>
public readonly record struct LogLine(string? Time, string Text, string Kind)
{
    public static LogLine Parse(string line)
    {
        string? time = null;
        var text = line;
        if (line.Length > 9 && line[2] == ':' && line[5] == ':' && line[8] == ' ' && char.IsDigit(line[0]) && char.IsDigit(line[7]))
        {
            time = line[..8];
            text = line[9..];
        }

        if (text.StartsWith("==> ", StringComparison.Ordinal))
        {
            var step = text[4..];
            var kind = step.StartsWith("Deploy succeeded", StringComparison.Ordinal) ? "ok"
                : step.StartsWith("Failed", StringComparison.Ordinal) || step.StartsWith("Unexpected error", StringComparison.Ordinal) ? "error"
                : step.StartsWith("Rolled back", StringComparison.Ordinal) || step.StartsWith("Cancelled", StringComparison.Ordinal) ? "warn"
                : "step";
            return new LogLine(time, step, kind);
        }
        if (text.StartsWith("  | ", StringComparison.Ordinal)) return new LogLine(time, text[4..], "dim");
        if (text.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("warn:", StringComparison.Ordinal)
            || text.Contains("npm warn", StringComparison.OrdinalIgnoreCase)) return new LogLine(time, text, "warn");
        if (text.Contains("ERROR", StringComparison.Ordinal) || text.StartsWith("fail:", StringComparison.Ordinal)
            || text.StartsWith("error", StringComparison.OrdinalIgnoreCase) || text.Contains(" error:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Exception", StringComparison.Ordinal)) return new LogLine(time, text, "error");
        if (text.Length > 2 && text[0] == '#' && char.IsDigit(text[1]) && text.Contains(" [", StringComparison.Ordinal)) return new LogLine(time, text, "stage");
        return new LogLine(time, text, "plain");
    }
}
