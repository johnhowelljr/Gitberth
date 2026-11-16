using System.Text;

namespace Gitberth.Compose;

/// <summary>A variable a Compose file uses: ${NAME}, ${NAME:-default}, ${NAME:?message} or $NAME.</summary>
/// <param name="Default">The value it takes when it isn't set, when the file gives one.</param>
/// <param name="Required">The file stops with a message when it isn't set (${NAME:?message}).</param>
public sealed record ComposeVariable(string Name, string? Default, bool Required);

/// <summary>Compose's variables (its interpolation): which a file uses, and a text with them filled in as Compose fills them.</summary>
public static class ComposeVariables
{
    /// <summary>
    /// The text with its variables filled in from <paramref name="values"/>, as Compose does: ${VAR:-default} and ${VAR-default}
    /// take the default when VAR is unset (or, with ':', empty), ${VAR:+other} and ${VAR+other} the other when it's set,
    /// ${VAR:?message} is empty when unset, $VAR and ${VAR} are the value or empty, and $$ is $. Defaults can hold variables.
    /// </summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) => Walk(text, values, null);

    /// <summary>Every variable the text uses, each once, in order.</summary>
    public static List<ComposeVariable> Used(string text)
    {
        var found = new List<ComposeVariable>();
        Walk(text, new Dictionary<string, string>(), found);
        return found;
    }

    private static string Walk(string text, IReadOnlyDictionary<string, string> values, List<ComposeVariable>? found)
    {
        if (!text.Contains('$')) return text;
        var output = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '$' || i + 1 >= text.Length)
            {
                output.Append(c);
                i++;
                continue;
            }
            var next = text[i + 1];
            if (next == '$')
            {
                output.Append('$');
                i += 2;
                continue;
            }
            if (next == '{')
            {
                var end = Closing(text, i + 2);
                if (end < 0)
                {
                    output.Append(text[i..]);
                    break;
                }
                output.Append(Braced(text[(i + 2)..end], values, found));
                i = end + 1;
                continue;
            }
            if (char.IsAsciiLetter(next) || next == '_')
            {
                var j = i + 1;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_')) j++;
                var name = text[(i + 1)..j];
                Note(found, new ComposeVariable(name, null, false));
                output.Append(values.GetValueOrDefault(name) ?? "");
                i = j;
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    // The matching '}' of a ${, counting nested ${…} in its default.
    private static int Closing(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string Braced(string inner, IReadOnlyDictionary<string, string> values, List<ComposeVariable>? found)
    {
        var n = 0;
        while (n < inner.Length && (char.IsAsciiLetterOrDigit(inner[n]) || inner[n] == '_')) n++;
        var name = inner[..n];
        var rest = inner[n..];
        var set = values.TryGetValue(name, out var value);
        var nonEmpty = set && value!.Length > 0;
        string Default(string d)
        {
            Note(found, new ComposeVariable(name, d, false));
            return Walk(d, values, found);
        }
        switch (rest)
        {
            case "":
                Note(found, new ComposeVariable(name, null, false));
                return value ?? "";
            case var r when r.StartsWith(":-", StringComparison.Ordinal):
                return nonEmpty ? value! : Default(r[2..]);
            case var r when r.StartsWith('-'):
                return set ? value! : Default(r[1..]);
            case var r when r.StartsWith(":?", StringComparison.Ordinal) || r.StartsWith('?'):
                Note(found, new ComposeVariable(name, null, true));
                return value ?? "";
            case var r when r.StartsWith(":+", StringComparison.Ordinal):
                Note(found, new ComposeVariable(name, null, false));
                return nonEmpty ? Walk(r[2..], values, found) : "";
            case var r when r.StartsWith('+'):
                Note(found, new ComposeVariable(name, null, false));
                return set ? Walk(r[1..], values, found) : "";
            default:
                Note(found, new ComposeVariable(name, null, false));
                return value ?? "";
        }
    }

    // The first mention of a variable says what's known of it; a later one with a default or a requirement adds that.
    private static void Note(List<ComposeVariable>? found, ComposeVariable variable)
    {
        if (found is null || variable.Name.Length == 0) return;
        var at = found.FindIndex(v => v.Name == variable.Name);
        if (at < 0) found.Add(variable);
        else if (found[at].Default is null && variable.Default is not null) found[at] = found[at] with { Default = variable.Default };
        else if (variable.Required && !found[at].Required) found[at] = found[at] with { Required = true };
    }
}
