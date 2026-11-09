using System.Globalization;
using System.Text;

namespace Gitberth.Detection;

/// <summary>
/// Reads the TOML that pyproject.toml and Pipfile use: tables, arrays of tables, dotted and quoted keys, strings,
/// numbers, booleans, arrays and inline tables. A line it can't read is skipped instead of failing the whole file:
/// detection that reads most of a file beats detection that gives up on it.
/// </summary>
/// <remarks>Tables are <c>Dictionary&lt;string, object?&gt;</c>, arrays <c>List&lt;object?&gt;</c>, values string, long, double or bool. Dates stay strings.</remarks>
internal sealed class Toml
{
    private readonly string _s;
    private int _i;

    private Toml(string text) => _s = text.Replace("\r\n", "\n");

    public static Dictionary<string, object?> Parse(string text) => new Toml(text).Document();

    /// <summary>The table at a dotted path, like Table(doc, "tool", "poetry"); null when it's missing or not a table.</summary>
    public static Dictionary<string, object?>? Table(Dictionary<string, object?>? table, params string[] path) =>
        Get(table, path) as Dictionary<string, object?>;

    public static object? Get(Dictionary<string, object?>? table, params string[] path)
    {
        object? current = table;
        foreach (var key in path)
        {
            if (current is not Dictionary<string, object?> t || !t.TryGetValue(key, out current)) return null;
        }
        return current;
    }

    public static string? String(Dictionary<string, object?>? table, params string[] path) => Get(table, path) as string;

    public static bool? Bool(Dictionary<string, object?>? table, params string[] path) => Get(table, path) as bool?;

    /// <summary>The strings in an array; anything else in it is left out.</summary>
    public static IEnumerable<string> Strings(Dictionary<string, object?>? table, params string[] path) =>
        Get(table, path) is List<object?> list ? list.OfType<string>() : [];

    private Dictionary<string, object?> Document()
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = root;
        while (true)
        {
            SkipBlank();
            if (_i >= _s.Length) return root;
            var start = _i;
            try
            {
                if (_s[_i] == '[')
                {
                    // Keys under a header that can't be read go nowhere, rather than into the table before it.
                    current = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var arrayOfTables = Starts("[[");
                    _i += arrayOfTables ? 2 : 1;
                    var path = Key();
                    Expect(']');
                    if (arrayOfTables) Expect(']');
                    EndOfLine();
                    current = arrayOfTables ? AppendTable(root, path) : OpenTable(root, path);
                }
                else
                {
                    var path = Key();
                    Expect('=');
                    SkipSpaces();
                    var value = Value();
                    EndOfLine();
                    Assign(current, path, value);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _i = start;
                SkipLine();
            }
        }
    }

    private List<string> Key()
    {
        var parts = new List<string>();
        while (true)
        {
            SkipSpaces();
            if (_i >= _s.Length) throw new FormatException();
            if (_s[_i] == '"') parts.Add(BasicString());
            else if (_s[_i] == '\'') parts.Add(LiteralString());
            else
            {
                var start = _i;
                while (_i < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] is '_' or '-')) _i++;
                if (_i == start) throw new FormatException();
                parts.Add(_s[start.._i]);
            }
            SkipSpaces();
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                continue;
            }
            return parts;
        }
    }

    private object? Value()
    {
        if (_i >= _s.Length) throw new FormatException();
        switch (_s[_i])
        {
            case '"': return Starts("\"\"\"") ? MultilineString(literal: false) : BasicString();
            case '\'': return Starts("'''") ? MultilineString(literal: true) : LiteralString();
            case '[': return Array();
            case '{': return InlineTable();
        }
        if (Starts("true") && !IsBareChar(4)) { _i += 4; return true; }
        if (Starts("false") && !IsBareChar(5)) { _i += 5; return false; }
        // Numbers, dates and times: the bare token up to the next delimiter.
        var start = _i;
        while (_i < _s.Length && _s[_i] is not (',' or ']' or '}' or '\n' or '#')) _i++;
        var token = _s[start.._i].Trim();
        if (token.Length == 0) throw new FormatException();
        var number = token.Replace("_", "");
        if (long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return token;
    }

    private bool IsBareChar(int offset) =>
        _i + offset < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i + offset]) || _s[_i + offset] is '_' or '-');

    private List<object?> Array()
    {
        _i++;
        var list = new List<object?>();
        while (true)
        {
            SkipBlank();
            if (_i >= _s.Length) throw new FormatException();
            if (_s[_i] == ']')
            {
                _i++;
                return list;
            }
            list.Add(Value());
            SkipBlank();
            if (_i < _s.Length && _s[_i] == ',') _i++;
            else if (_i >= _s.Length || _s[_i] != ']') throw new FormatException();
        }
    }

    // Lenient: newlines are allowed inside, as TOML 1.1 allows.
    private Dictionary<string, object?> InlineTable()
    {
        _i++;
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            SkipBlank();
            if (_i >= _s.Length) throw new FormatException();
            if (_s[_i] == '}')
            {
                _i++;
                return table;
            }
            var path = Key();
            Expect('=');
            SkipSpaces();
            Assign(table, path, Value());
            SkipBlank();
            if (_i < _s.Length && _s[_i] == ',') _i++;
            else if (_i >= _s.Length || _s[_i] != '}') throw new FormatException();
        }
    }

    private string BasicString()
    {
        _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length)
        {
            var c = _s[_i++];
            if (c == '"') return sb.ToString();
            if (c == '\n') throw new FormatException();
            if (c == '\\') sb.Append(Escape());
            else sb.Append(c);
        }
        throw new FormatException();
    }

    private string LiteralString()
    {
        _i++;
        var end = _s.IndexOf('\'', _i);
        var newline = _s.IndexOf('\n', _i);
        if (end < 0 || (newline >= 0 && newline < end)) throw new FormatException();
        var value = _s[_i..end];
        _i = end + 1;
        return value;
    }

    private string MultilineString(bool literal)
    {
        var quote = literal ? "'''" : "\"\"\"";
        _i += 3;
        // A newline right after the opening quotes isn't part of the string.
        if (_i < _s.Length && _s[_i] == '\n') _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length)
        {
            if (_s[_i] == quote[0] && CountAhead(quote[0]) is var run and >= 3)
            {
                // Up to two quote characters right before the closing three belong to the string.
                var extra = Math.Min(run - 3, 2);
                sb.Append(quote[0], extra);
                _i += extra + 3;
                return sb.ToString();
            }
            var c = _s[_i++];
            if (!literal && c == '\\')
            {
                if (_i < _s.Length && _s[_i] is ' ' or '\t' or '\n')
                {
                    // A backslash at the end of a line joins it to the next, dropping the whitespace between.
                    while (_i < _s.Length && _s[_i] is ' ' or '\t' or '\n') _i++;
                    continue;
                }
                sb.Append(Escape());
                continue;
            }
            sb.Append(c);
        }
        throw new FormatException();
    }

    private int CountAhead(char c)
    {
        var n = 0;
        while (_i + n < _s.Length && _s[_i + n] == c) n++;
        return n;
    }

    private string Escape()
    {
        if (_i >= _s.Length) throw new FormatException();
        var c = _s[_i++];
        return c switch
        {
            'n' => "\n",
            't' => "\t",
            'r' => "\r",
            'b' => "\b",
            'f' => "\f",
            'e' => "\u001b",
            '"' => "\"",
            '\\' => "\\",
            'u' => Unicode(4),
            'U' => Unicode(8),
            _ => throw new FormatException(),
        };
    }

    private string Unicode(int digits)
    {
        if (_i + digits > _s.Length) throw new FormatException();
        var hex = _s.Substring(_i, digits);
        _i += digits;
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
        return char.ConvertFromUtf32(code);
    }

    private static Dictionary<string, object?> OpenTable(Dictionary<string, object?> root, List<string> path)
    {
        var table = root;
        foreach (var key in path) table = Child(table, key);
        return table;
    }

    private static Dictionary<string, object?> AppendTable(Dictionary<string, object?> root, List<string> path)
    {
        var parent = OpenTable(root, path[..^1]);
        if (!parent.TryGetValue(path[^1], out var existing) || existing is not List<object?> list)
        {
            if (existing is not null) throw new FormatException();
            parent[path[^1]] = list = [];
        }
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        list.Add(table);
        return table;
    }

    // The table under a key, created when missing; for an array of tables, its last one.
    private static Dictionary<string, object?> Child(Dictionary<string, object?> table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is null)
        {
            var created = new Dictionary<string, object?>(StringComparer.Ordinal);
            table[key] = created;
            return created;
        }
        return value switch
        {
            Dictionary<string, object?> t => t,
            List<object?> { Count: > 0 } list when list[^1] is Dictionary<string, object?> last => last,
            _ => throw new FormatException(),
        };
    }

    private static void Assign(Dictionary<string, object?> table, List<string> path, object? value)
    {
        foreach (var key in path[..^1]) table = Child(table, key);
        table[path[^1]] = value;
    }

    private bool Starts(string s) => string.CompareOrdinal(_s, _i, s, 0, s.Length) == 0;

    private void Expect(char c)
    {
        SkipSpaces();
        if (_i >= _s.Length || _s[_i] != c) throw new FormatException();
        _i++;
    }

    private void SkipSpaces()
    {
        while (_i < _s.Length && _s[_i] is ' ' or '\t' or '\r') _i++;
    }

    // Whitespace, newlines and comments.
    private void SkipBlank()
    {
        while (_i < _s.Length)
        {
            if (_s[_i] is ' ' or '\t' or '\r' or '\n') _i++;
            else if (_s[_i] == '#') SkipLine();
            else return;
        }
    }

    private void SkipLine()
    {
        var newline = _s.IndexOf('\n', _i);
        _i = newline < 0 ? _s.Length : newline + 1;
    }

    private void EndOfLine()
    {
        SkipSpaces();
        if (_i < _s.Length && _s[_i] == '#') SkipLine();
        else if (_i < _s.Length && _s[_i] != '\n') throw new FormatException();
    }
}
