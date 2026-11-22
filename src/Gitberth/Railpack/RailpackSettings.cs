using System.Text;
using System.Text.RegularExpressions;
using Gitberth.Detection;
using Gitberth.Security;

namespace Gitberth.Railpack;

/// <summary>
/// Railpack's settings, a Git app's RAILPACK_ variables, can't be secret: Railpack writes them into its plan and the image
/// (a build command becomes a step's command and name, a start command the image's command). A secret goes in a variable
/// of its own, and the setting names it as <c>$NAME</c>: every build step gets the build's values as variables, and the
/// start command runs through bash -c, where the container's are. The saves refuse a secret one
/// (<see cref="RailpackText.SecretSetting"/>), and a deploy leaves one that's secret anyway out of Railpack
/// (<see cref="RailpackText.SecretSettingsLeftOut"/>). Pure.
/// </summary>
public static partial class RailpackSettings
{
    /// <summary>
    /// Whether a value is a RAILPACK_ one that would be secret: marked so (<paramref name="marked"/>), or it looks it
    /// (<see cref="LooksSecret"/>). Never for any other name.
    /// </summary>
    public static bool IsSecret(string key, string value, bool marked) => EnvRules.IsRailpackKey(key.Trim()) && (marked || LooksSecret(key, value));

    /// <summary>
    /// A value that holds a secret by Masking's rule (a secret-looking name, a URL's password, the value after an option like
    /// --token), its <c>$NAME</c> and <c>${NAME}</c> references left out (<see cref="WithoutReferences"/>): a reference names
    /// a variable, it holds nothing. So <c>./server --api-key "$API_KEY"</c> doesn't, and <c>./server --api-key=sk_live_…</c>
    /// does, and so does <c>--password '$ecret'</c>, where the shell never expands it.
    /// </summary>
    public static bool LooksSecret(string key, string value) => Masking.IsSecret(key.Trim(), WithoutReferences(value));

    /// <summary>
    /// The value without the <c>$NAME</c> and <c>${NAME}</c> references a shell expands: unquoted or inside "…". Inside '…',
    /// or after a backslash, a $ is only a character, so the text stays and is checked as written. A reference that was all
    /// of a "…" leaves "", which goes too.
    /// </summary>
    internal static string WithoutReferences(string value)
    {
        var text = new StringBuilder(value.Length);
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                text.Append(c);
            }
            else if (c == '\\' && i + 1 < value.Length)
            {
                text.Append(c).Append(value[++i]);
            }
            else if (c == '$' && Reference().Match(value, i) is { Success: true } reference && reference.Index == i)
            {
                i += reference.Length - 1;
            }
            else
            {
                if (c == '\'' && quote == '\0') quote = '\'';
                else if (c == '"') quote = quote == '"' ? '\0' : '"';
                text.Append(c);
            }
        }
        return text.ToString().Replace("\"\"", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A name a shell takes as a variable's: letters, digits and _, not starting with a digit, and nothing after them (not
    /// even a newline). A secret the repo's railpack.json asks for has to have one (DeployPipeline: it goes into a --secret flag).
    /// </summary>
    public static bool IsVariableName(string name) => VariableName().IsMatch(name);

    [GeneratedRegex(@"\$(?:\{[A-Za-z_][A-Za-z0-9_]*\}|[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Reference();

    // \z, not $: $ would also match before a final newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex VariableName();
}
