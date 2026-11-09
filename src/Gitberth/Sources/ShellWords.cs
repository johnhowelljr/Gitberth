using System.Text;

namespace Gitberth.Sources;

/// <summary>
/// Splits a pasted command into its words as a POSIX shell would, without running anything: single and double quotes,
/// backslash escapes and line continuations (\ at the end of a line; PowerShell's ` and cmd's ^ too, since the owner's PC
/// runs Windows), CRLF or LF. What a shell would have to run or look up is never guessed at: command substitution
/// ($(…) and backticks) is an Error, $VAR is kept as written with a Note, and a second command (&amp;&amp;, ;, |, or a new
/// line) is an Error, as is a redirection.
/// </summary>
public static class ShellWords
{
    public static (IReadOnlyList<string> Words, IReadOnlyList<ParseIssue> Issues) Split(string text) => Split(text, commandLine: true);

    /// <param name="commandLine">
    /// A pasted command line: a new line ends the command and $VAR gets a Note. False for a Compose command or entrypoint
    /// string, whose new lines are spaces and whose $VAR is the container's own shell's (Compose's own variables are
    /// replaced before).
    /// </param>
    internal static (IReadOnlyList<string> Words, IReadOnlyList<ParseIssue> Issues) Split(string text, bool commandLine)
    {
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var words = new List<string>();
        var issues = new List<ParseIssue>();
        var noted = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        var inWord = false;
        var continuationNoted = false;

        void Flush()
        {
            if (!inWord) return;
            words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        void Continued(string how, string fragment)
        {
            if (continuationNoted) return;
            continuationNoted = true;
            issues.Add(new ParseIssue(ParseSeverity.Note, $"Lines ending in {how} are read as one command.", fragment));
        }

        void Variable(string fragment, string name)
        {
            if (!commandLine || !noted.Add(name)) return;
            issues.Add(new ParseIssue(ParseSeverity.Note,
                $"{fragment} is kept as written: Gitberth doesn't have your shell's variables. Put its value in instead if it's meant to be one.", fragment));
        }

        // $(…), $((…)) or ${…}: the index after it.
        static int Closing(string s, int from, char open, char close)
        {
            var depth = 0;
            for (var j = from; j < s.Length; j++)
            {
                if (s[j] == open) depth++;
                else if (s[j] == close && --depth == 0) return j + 1;
            }
            return s.Length;
        }

        // At s[i] == '$': handles it, appending what stays, and returns the index after it.
        int Dollar(int i)
        {
            if (i + 1 < s.Length && s[i + 1] == '(')
            {
                var end = Closing(s, i + 1, '(', ')');
                issues.Add(new ParseIssue(ParseSeverity.Error, "Command substitution isn't run: paste the command with its values.", Clip(s[i..end])));
                return end;
            }
            if (i + 1 < s.Length && s[i + 1] == '{')
            {
                var end = Closing(s, i + 1, '{', '}');
                var fragment = s[i..end];
                word.Append(fragment);
                inWord = true;
                Variable(fragment, fragment.Trim('$', '{', '}'));
                return end;
            }
            var j = i + 1;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_') && !(j == i + 1 && char.IsAsciiDigit(s[j]))) j++;
            if (j > i + 1)
            {
                var fragment = s[i..j];
                word.Append(fragment);
                inWord = true;
                Variable(fragment, fragment[1..]);
                return j;
            }
            word.Append('$');
            inWord = true;
            return i + 1;
        }

        // Whether only blanks, comments and new lines are left from j on.
        static bool NothingFrom(string s, int j)
        {
            while (j < s.Length)
            {
                if (char.IsWhiteSpace(s[j])) j++;
                else if (s[j] == '#') while (j < s.Length && s[j] != '\n') j++;
                else return false;
            }
            return true;
        }

        // Spaces then a new line from j on: the index of that new line, else -1.
        static int LineEndAfterBlanks(string s, int j)
        {
            while (j < s.Length && s[j] is ' ' or '\t') j++;
            return j >= s.Length ? s.Length : s[j] == '\n' ? j : -1;
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                {
                    var end = LineEndAfterBlanks(s, i + 1);
                    if (end >= 0)
                    {
                        if (end > i + 1) Continued("\\ (with spaces after it)", "\\");
                        i = end + 1;
                    }
                    else
                    {
                        word.Append(s[i + 1]);
                        inWord = true;
                        i += 2;
                    }
                    continue;
                }
                case '`':
                {
                    var end = LineEndAfterBlanks(s, i + 1);
                    if (end >= 0)
                    {
                        Continued("` (PowerShell's)", "`");
                        i = end + 1;
                        continue;
                    }
                    var close = s.IndexOf('`', i + 1);
                    var stop = close < 0 ? s.Length : close + 1;
                    issues.Add(new ParseIssue(ParseSeverity.Error, "Command substitution isn't run: paste the command with its values.", Clip(s[i..stop])));
                    i = stop;
                    continue;
                }
                case '^' when commandLine && LineEndAfterBlanks(s, i + 1) >= 0:
                {
                    Continued("^ (Windows' cmd)", "^");
                    i = LineEndAfterBlanks(s, i + 1) + 1;
                    continue;
                }
                case ' ' or '\t':
                    Flush();
                    i++;
                    continue;
                case '\n':
                    Flush();
                    if (commandLine && words.Count > 0 && !NothingFrom(s, i))
                    {
                        issues.Add(new ParseIssue(ParseSeverity.Error,
                            "Paste one command: the next line starts another one. End a line with \\ to carry on to the next.", Clip(s[(i + 1)..].TrimStart())));
                        return (words, issues);
                    }
                    i++;
                    continue;
                case '#' when !inWord:
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                case '\'':
                {
                    var close = s.IndexOf('\'', i + 1);
                    if (close < 0)
                    {
                        issues.Add(new ParseIssue(ParseSeverity.Error, "A quote isn't closed: add the ' that ends it.", Clip(s[i..])));
                        word.Append(s, i + 1, s.Length - i - 1);
                        inWord = true;
                        i = s.Length;
                        continue;
                    }
                    word.Append(s, i + 1, close - i - 1);
                    inWord = true;
                    i = close + 1;
                    continue;
                }
                case '"':
                {
                    inWord = true;
                    var j = i + 1;
                    var closed = false;
                    while (j < s.Length)
                    {
                        var d = s[j];
                        if (d == '"')
                        {
                            closed = true;
                            j++;
                            break;
                        }
                        if (d == '\\' && j + 1 < s.Length && s[j + 1] is '$' or '`' or '"' or '\\')
                        {
                            word.Append(s[j + 1]);
                            j += 2;
                        }
                        else if (d == '\\' && j + 1 < s.Length && s[j + 1] == '\n')
                        {
                            j += 2;
                        }
                        else if (d == '$')
                        {
                            j = Dollar(j);
                        }
                        else if (d == '`')
                        {
                            var close = s.IndexOf('`', j + 1);
                            var stop = close < 0 ? s.Length : close + 1;
                            issues.Add(new ParseIssue(ParseSeverity.Error, "Command substitution isn't run: paste the command with its values.", Clip(s[j..stop])));
                            j = stop;
                        }
                        else
                        {
                            word.Append(d);
                            j++;
                        }
                    }
                    if (!closed) issues.Add(new ParseIssue(ParseSeverity.Error, "A quote isn't closed: add the \" that ends it.", Clip(s[i..])));
                    i = j;
                    continue;
                }
                case '$':
                    i = Dollar(i);
                    continue;
                case ';' or '&' or '|':
                {
                    var op = i + 1 < s.Length && s[i + 1] == c ? s.Substring(i, 2) : c.ToString();
                    Flush();
                    var rest = i + op.Length;
                    // A ; or & at the very end ends the one command; anything after it is another.
                    if (op is ";" or "&" && NothingFrom(s, rest))
                    {
                        i = s.Length;
                        continue;
                    }
                    issues.Add(new ParseIssue(ParseSeverity.Error,
                        op == "|" ? "Paste one command: Gitberth doesn't pipe a container's output anywhere." : "Paste one command: Gitberth runs one container per app.",
                        Clip(s[i..].Trim())));
                    return (words, issues);
                }
                case '<' or '>':
                {
                    // 2>&1, > file, < file: the digits before it belong to the redirection, and so does the word after it.
                    if (inWord && word.ToString().All(char.IsAsciiDigit))
                    {
                        word.Clear();
                        inWord = false;
                    }
                    Flush();
                    var j = i + 1;
                    while (j < s.Length && s[j] is '>' or '&' or '<') j++;
                    while (j < s.Length && s[j] is ' ' or '\t') j++;
                    while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
                    issues.Add(new ParseIssue(ParseSeverity.Error, "A redirection isn't part of a container: remove it. Its output is on the Logs tab.", Clip(s[i..j].Trim())));
                    i = j;
                    continue;
                }
                default:
                    word.Append(c);
                    inWord = true;
                    i++;
                    continue;
            }
        }
        Flush();
        return (words, issues);
    }

    private static string Clip(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 60 ? line[..57] + "…" : line;
    }
}
