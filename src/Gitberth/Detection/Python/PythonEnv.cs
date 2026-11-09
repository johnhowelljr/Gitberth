using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// The environment variables Python code reads: os.environ["X"], os.environ.get("X"), os.getenv("X"), django-environ's
/// env("X") and env.str("X"), python-decouple's config("X"), and the fields of pydantic-settings classes.
/// </summary>
internal static partial class PythonEnv
{
    private const int MaxFiles = 2000;

    // Set by Gitberth or the image, or read by Python and its tools rather than the app.
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "PORT", "HOST", "HOSTNAME", "PATH", "HOME", "USER", "PWD", "SHELL", "TERM", "LANG", "TMPDIR", "VIRTUAL_ENV",
        "DJANGO_SETTINGS_MODULE", "FORWARDED_ALLOW_IPS", "GRADIO_SERVER_NAME", "GRADIO_SERVER_PORT", "DYNO",
    };

    private static readonly string[] IgnoredPrefixes = ["PYTHON", "UV_", "PIP_", "POETRY_", "PDM_", "PIPENV_", "STREAMLIT_SERVER_", "RAILWAY_", "RENDER_", "FLY_", "HEROKU_"];

    private static readonly string[] EnvironUrls = ["db", "db_url", "cache", "cache_url", "email", "email_url", "search_url"];

    public static void Scan(RepoFiles files, Action<string, string, string> add)
    {
        foreach (var file in PythonFiles.Sources(files).Take(MaxFiles))
        {
            if (PythonFiles.Read(files, file) is not { Length: > 0 } text) continue;
            foreach (var key in Keys(text))
            {
                if (!Ignored.Contains(key) && !IgnoredPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) add(key, "", $"used in {file}");
            }
        }
    }

    /// <summary>The variables one file reads, in the order they appear.</summary>
    internal static IEnumerable<string> Keys(string text)
    {
        foreach (Match m in OsEnviron().Matches(text)) yield return m.Groups["key"].Value;

        if (DjangoEnvironImport().IsMatch(text))
        {
            var env = EnvironEnv().Match(text);
            var name = env.Success ? env.Groups["var"].Value : "env";
            // environ.Env(DEBUG=(bool, False)) declares variables with their types and defaults.
            if (env.Success)
            {
                foreach (Match declared in Keyword().Matches(Arguments(text, env.Index + env.Length))) yield return declared.Groups["key"].Value;
            }
            var call = new Regex($@"(?<![\w.]){Regex.Escape(name)}(?:\.(?<cast>\w+))?\(\s*(?:['""](?<key>[A-Za-z_][A-Za-z0-9_]*)['""])?");
            foreach (Match m in call.Matches(text))
            {
                if (m.Groups["key"].Success) yield return m.Groups["key"].Value;
                // env.db() reads DATABASE_URL, env.cache() CACHE_URL, and so on.
                else if (m.Groups["cast"].Success && EnvironUrls.Contains(m.Groups["cast"].Value))
                    yield return m.Groups["cast"].Value.Split('_')[0] switch { "db" => "DATABASE_URL", var kind => kind.ToUpperInvariant() + "_URL" };
            }
        }

        if (DecoupleImport().IsMatch(text))
        {
            foreach (Match m in DecoupleConfig().Matches(text)) yield return m.Groups["key"].Value;
        }

        if (text.Contains("BaseSettings", StringComparison.Ordinal))
        {
            foreach (var key in PydanticSettings(text)) yield return key;
        }
    }

    /// <summary>
    /// The variables a pydantic-settings class reads: its fields upper-cased (pydantic-settings matches them without
    /// regard to case), after its env_prefix, or a field's alias.
    /// </summary>
    internal static IEnumerable<string> PydanticSettings(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var header = SettingsClass().Match(lines[i]);
            if (!header.Success || !header.Groups["bases"].Value.Contains("BaseSettings", StringComparison.Ordinal)) continue;
            var classIndent = Indent(lines[i]);
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length > 0 && Indent(lines[j]) <= classIndent) break;
                body.Add(lines[j]);
            }
            var fieldIndent = body.Where(l => l.Trim().Length > 0).Select(Indent).DefaultIfEmpty(0).First();
            var block = string.Join('\n', body);
            var prefix = EnvPrefix().Match(block) is { Success: true } p ? p.Groups[1].Value : "";
            var caseSensitive = CaseSensitive().IsMatch(block);
            foreach (var line in body.Where(l => l.Trim().Length > 0 && Indent(l) == fieldIndent))
            {
                var field = Field().Match(line.Trim());
                if (!field.Success) continue;
                var name = field.Groups["name"].Value;
                if (name.StartsWith('_') || name == "model_config" || field.Groups["type"].Value.Contains("ClassVar", StringComparison.Ordinal)) continue;
                // In the default (Field(alias="X")) or the annotation (Annotated[str, Field(alias="X")]).
                var alias = Alias().Match(line);
                yield return alias.Success ? alias.Groups[1].Value : caseSensitive ? prefix + name : (prefix + name).ToUpperInvariant();
            }
        }
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    // The text between a call's parentheses, from just after the "(".
    private static string Arguments(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[start..i];
        }
        return "";
    }

    [GeneratedRegex(@"(?:\bos\.environ\[|(?<![\w.])environ\[)\s*['""](?<key>[A-Za-z_][A-Za-z0-9_]*)['""]\s*\]|(?:\bos\.environ\.get|(?<![\w.])environ\.get|\bos\.getenv|(?<![\w.])getenv)\(\s*['""](?<key>[A-Za-z_][A-Za-z0-9_]*)['""]")]
    private static partial Regex OsEnviron();

    [GeneratedRegex(@"^[ \t]*(?:import[ \t]+environ\b|from[ \t]+environ[ \t]+import\b)", RegexOptions.Multiline)]
    private static partial Regex DjangoEnvironImport();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)[ \t]*=[ \t]*(?:environ\.)?Env\(", RegexOptions.Multiline)]
    private static partial Regex EnvironEnv();

    [GeneratedRegex(@"\b(?<key>[A-Z][A-Z0-9_]*)\s*=")]
    private static partial Regex Keyword();

    [GeneratedRegex(@"^[ \t]*(?:from[ \t]+decouple[ \t]+import\b|import[ \t]+decouple\b)", RegexOptions.Multiline)]
    private static partial Regex DecoupleImport();

    [GeneratedRegex(@"(?<![\w.])(?:decouple\.)?config\(\s*['""](?<key>[A-Za-z_][A-Za-z0-9_]*)['""]")]
    private static partial Regex DecoupleConfig();

    [GeneratedRegex(@"^[ \t]*class[ \t]+\w+[ \t]*\((?<bases>[^)]*)\)[ \t]*:")]
    private static partial Regex SettingsClass();

    [GeneratedRegex(@"env_prefix\s*=\s*['""]([^'""]*)['""]")]
    private static partial Regex EnvPrefix();

    [GeneratedRegex(@"case_sensitive\s*=\s*True\b")]
    private static partial Regex CaseSensitive();

    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*)\s*:\s*(?<type>[^=]+?)\s*(?:=(?<rest>.*))?$")]
    private static partial Regex Field();

    [GeneratedRegex(@"\b(?:validation_)?alias\s*=\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]")]
    private static partial Regex Alias();
}
