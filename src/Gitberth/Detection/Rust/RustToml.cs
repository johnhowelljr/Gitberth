using System.Globalization;
using System.Text;

namespace Gitberth.Detection;

/// <summary>
/// Enough of TOML for Cargo.toml, rust-toolchain.toml and Rocket.toml: tables, arrays of tables, dotted and quoted keys,
/// strings, arrays and inline tables. Numbers, booleans and dates stay strings, as written. A line it can't read is skipped,
/// so a file it only half understands still gives what it could read.
/// </summary>
internal sealed class RustToml
{
    private readonly string _s;
    private int _i;

    private RustToml(string text) => _s = text.Replace("\r\n", "\n");

    /// <summary>Tables are dictionaries, arrays are lists, everything else is a string.</summary>
    public static Dictionary<string, object> Parse(string text) => new RustToml(text).Document();

    public static Dictionary<string, object>? Table(object? value) => value as Dictionary<string, object>;

    public static object? Get(Dictionary<string, object>? table, params string[] path)
    {
        object? current = table;
        foreach (var key in path)
        {
            if (current is not Dictionary<string, object> t || !t.TryGetValue(key, out current)) return null;
        }
        return current;
    }

    public static string? Str(Dictionary<string, object>? table, params string[] path) => Get(table, path) as string;

    /// <summary>The strings of an array, or none.</summary>
    public static IEnumerable<string> Strings(object? value) => value is List<object> list ? list.OfType<string>() : [];

    /// <summary>The tables of an array of tables, or the one table.</summary>
    public static IEnumerable<Dictionary<string, object>> Tables(object? value) =>
        value switch
        {
            List<object> list => list.OfType<Dictionary<string, object>>(),
            Dictionary<string, object> one => [one],
            _ => [],
        };

    private Dictionary<string, object> Document()
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var table = root;
        while (true)
        {
            SkipBlank(newlines: true);
            if (_i >= _s.Length) break;
            var line = _i;
            try
            {
                if (_s[_i] == '[')
                {
                    var array = Peek(1) == '[';
                    _i += array ? 2 : 1;
                    var path = Key();
                    Expect(']');
                    if (array) Expect(']');
                    table = array ? NewArrayTable(root, path) : Navigate(root, path);
                }
                else
                {
                    var key = Key();
                    Expect('=');
                    Assign(table, key, Value());
                }
                SkipBlank(newlines: false);
                if (_i < _s.Length && _s[_i] != '\n') throw new FormatException();
            }
            catch (FormatException)
            {
                // Skip what can't be read, from the start of its line.
                _i = line;
                while (_i < _s.Length && _s[_i] != '\n') _i++;
            }
        }
        return root;
    }

    private List<string> Key()
    {
        var parts = new List<string>();
        while (true)
        {
            SkipSpaces();
            if (_i >= _s.Length) throw new FormatException();
            var c = _s[_i];
            if (c == '"') parts.Add(BasicString());
            else if (c == '\'') parts.Add(LiteralString());
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

    private object Value()
    {
        SkipSpaces();
        if (_i >= _s.Length) throw new FormatException();
        switch (_s[_i])
        {
            case '"':
                return Peek(1) == '"' && Peek(2) == '"' ? MultiLine("\"\"\"", escapes: true) : BasicString();
            case '\'':
                return Peek(1) == '\'' && Peek(2) == '\'' ? MultiLine("'''", escapes: false) : LiteralString();
            case '[':
                _i++;
                var list = new List<object>();
                while (true)
                {
                    SkipBlank(newlines: true);
                    if (_i >= _s.Length) throw new FormatException();
                    if (_s[_i] == ']')
                    {
                        _i++;
                        return list;
                    }
                    list.Add(Value());
                    SkipBlank(newlines: true);
                    if (_i < _s.Length && _s[_i] == ',') _i++;
                    else if (_i >= _s.Length || _s[_i] != ']') throw new FormatException();
                }
            case '{':
                _i++;
                var inline = new Dictionary<string, object>(StringComparer.Ordinal);
                while (true)
                {
                    SkipBlank(newlines: true);
                    if (_i >= _s.Length) throw new FormatException();
                    if (_s[_i] == '}')
                    {
                        _i++;
                        return inline;
                    }
                    var key = Key();
                    Expect('=');
                    Assign(inline, key, Value());
                    SkipBlank(newlines: true);
                    if (_i < _s.Length && _s[_i] == ',') _i++;
                    else if (_i >= _s.Length || _s[_i] != '}') throw new FormatException();
                }
            default:
                var start = _i;
                while (_i < _s.Length && _s[_i] is not (',' or ']' or '}' or '\n' or '#')) _i++;
                var bare = _s[start.._i].Trim();
                return bare.Length == 0 ? throw new FormatException() : bare;
        }
    }

    private string BasicString()
    {
        _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length && _s[_i] is not ('"' or '\n'))
        {
            if (_s[_i] == '\\') sb.Append(Escape());
            else sb.Append(_s[_i++]);
        }
        Expect('"');
        return sb.ToString();
    }

    private string LiteralString()
    {
        _i++;
        var end = _s.IndexOfAny(['\'', '\n'], _i);
        if (end < 0 || _s[end] != '\'') throw new FormatException();
        var text = _s[_i..end];
        _i = end + 1;
        return text;
    }

    private string MultiLine(string quote, bool escapes)
    {
        _i += 3;
        if (_i < _s.Length && _s[_i] == '\n') _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length && string.CompareOrdinal(_s, _i, quote, 0, 3) != 0)
        {
            if (escapes && _s[_i] == '\\') sb.Append(Escape());
            else sb.Append(_s[_i++]);
        }
        if (_i >= _s.Length) throw new FormatException();
        _i += 3;
        return sb.ToString();
    }

    private string Escape()
    {
        _i++;
        if (_i >= _s.Length) throw new FormatException();
        var c = _s[_i++];
        switch (c)
        {
            case 'n': return "\n";
            case 't': return "\t";
            case 'r': return "\r";
            case 'b': return "\b";
            case 'f': return "\f";
            case 'u' or 'U':
                var length = c == 'u' ? 4 : 8;
                if (_i + length > _s.Length || !int.TryParse(_s.AsSpan(_i, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                _i += length;
                return char.ConvertFromUtf32(code);
            case '\n':
                // A line-ending backslash in a multi-line string joins the lines.
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                return "";
            default:
                return c.ToString();
        }
    }

    private static void Assign(Dictionary<string, object> table, List<string> key, object value)
    {
        var parent = Navigate(table, key.Take(key.Count - 1));
        parent[key[^1]] = value;
    }

    /// <summary>The table at a dotted path, creating any that are missing. An array of tables gives its last table.</summary>
    private static Dictionary<string, object> Navigate(Dictionary<string, object> table, IEnumerable<string> path)
    {
        foreach (var part in path)
        {
            table = table.TryGetValue(part, out var existing) switch
            {
                true when existing is Dictionary<string, object> t => t,
                true when existing is List<object> { Count: > 0 } l && l[^1] is Dictionary<string, object> last => last,
                _ => (Dictionary<string, object>)(table[part] = new Dictionary<string, object>(StringComparer.Ordinal)),
            };
        }
        return table;
    }

    private static Dictionary<string, object> NewArrayTable(Dictionary<string, object> root, List<string> path)
    {
        var parent = Navigate(root, path.Take(path.Count - 1));
        if (!parent.TryGetValue(path[^1], out var existing) || existing is not List<object> list) parent[path[^1]] = list = [];
        var table = new Dictionary<string, object>(StringComparer.Ordinal);
        list.Add(table);
        return table;
    }

    private void Expect(char c)
    {
        SkipSpaces();
        if (_i >= _s.Length || _s[_i] != c) throw new FormatException();
        _i++;
    }

    private char Peek(int ahead) => _i + ahead < _s.Length ? _s[_i + ahead] : '\0';

    private void SkipSpaces()
    {
        while (_i < _s.Length && _s[_i] is ' ' or '\t') _i++;
    }

    /// <summary>Spaces and a comment, and with <paramref name="newlines"/> blank lines and comments too.</summary>
    private void SkipBlank(bool newlines)
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c is ' ' or '\t' or '\r' || (newlines && c == '\n')) _i++;
            else if (c == '#')
            {
                while (_i < _s.Length && _s[_i] != '\n') _i++;
            }
            else break;
        }
    }
}
