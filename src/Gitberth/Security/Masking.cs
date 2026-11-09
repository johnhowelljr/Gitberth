using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Security;

/// <summary>
/// The one rule for what's secret and how it's hidden, wherever a setting is shown, planned, logged or written out: the
/// environment, the command and entrypoint, labels, log options, the health command, plans, docker run lines, Compose
/// snippets, a change's messages and a repo's GitHub variables. Secret is a value whose name is marked secret or looks it
/// (DATABASE_PASSWORD, TUNNEL_TOKEN, splunk-token), a URL's password anywhere in a value (postgres://app:••••@db/app, one
/// with / or + in it too), the value after a secret-looking option on a command line (--token ••••, --password=••••,
/// --requirepass ••••), also inside a shell string (sh -c "cloudflared tunnel run --token ••••"), and the value of a
/// secret-looking KEY=value word. Pure.
/// </summary>
public static partial class Masking
{
    /// <summary>What a hidden value shows as.</summary>
    public const string Mask = "••••";

    /// <summary>
    /// A name whose value is secret, before anyone marks it: DATABASE_PASSWORD, SMTP_PASS, TUNNEL_TOKEN, ConnectionStrings__Main,
    /// and a label's or log option's name alike (splunk-token, com.example.api-key: - and . count as _).
    /// </summary>
    public static bool IsSecretName(string key) =>
        key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || SecretName().IsMatch(key.Replace('-', '_').Replace('.', '_'));

    /// <summary>A URL with a password in it, anywhere in the value: postgres://user:password@host/db, redis://:password@cache.</summary>
    public static bool HasUrlPassword(string value) => value.Length > 0 && UrlPassword().IsMatch(value);

    /// <summary>A value to keep hidden: its name is marked secret or looks it, or there's a secret in it (<see cref="Text"/>).</summary>
    public static bool IsSecret(string key, string value, IReadOnlyCollection<string>? secretKeys = null) =>
        Marked(key, secretKeys) || IsSecretName(key) || Text(value, secretKeys) != value;

    /// <summary>
    /// A named value (a variable, a label, a log option) as it's shown: hidden whole when it's marked secret (by
    /// <paramref name="secret"/> or <paramref name="secretKeys"/>) or its name looks it, else with the secrets in it hidden.
    /// </summary>
    public static string Value(string key, string value, IReadOnlyCollection<string>? secretKeys = null, bool secret = false) =>
        value.Length == 0 ? value
        : secret || Marked(key, secretKeys) || IsSecretName(key) ? Mask
        : Text(value, secretKeys);

    /// <summary>
    /// Free text (a shell string, a URL, one word of a command line, a message) with the secrets in it hidden: a URL's
    /// password, the value after a secret-looking option, and the value of a KEY=value word whose key is secret.
    /// </summary>
    public static string Text(string text, IReadOnlyCollection<string>? secretKeys = null)
    {
        if (text.Length == 0) return text;
        var masked = UrlPassword().Replace(text, m => m.Groups["head"].Value + Mask + "@");
        // Only the option or key is matched, its value looked at: a value that isn't hidden is read on, so a secret quoted
        // inside it (--header "… --token x") is found too.
        var hidden = new List<Group>();
        foreach (Match m in OptionValue().Matches(masked))
            if (SecretOption(m.Groups["option"].Value)) hidden.Add(m.Groups["value"]);
        foreach (Match m in Assignment().Matches(masked))
            if (Marked(m.Groups["key"].Value, secretKeys) || IsSecretName(m.Groups["key"].Value)) hidden.Add(m.Groups["value"]);
        if (hidden.Count == 0) return masked;
        var result = new StringBuilder();
        var at = 0;
        foreach (var value in hidden.OrderBy(v => v.Index))
        {
            // One inside a value already hidden goes with it.
            if (value.Index < at) continue;
            result.Append(masked, at, value.Index - at).Append(Mask);
            at = value.Index + value.Length;
        }
        return result.Append(masked, at, masked.Length - at).ToString();
    }

    /// <summary>
    /// A command line (an entrypoint, a command, a health check's words) with its secrets hidden, word for word: the word
    /// after a secret-looking option, and in each word what <see cref="Text"/> hides.
    /// </summary>
    public static IReadOnlyList<string> Args(IReadOnlyList<string> args, IReadOnlyCollection<string>? secretKeys = null)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (i > 0 && args[i - 1] is { } option && option.StartsWith('-') && !option.Contains('=') && !option.Any(char.IsWhiteSpace) && SecretOption(option)
                && a.Length > 0 && !a.StartsWith('-'))
                result.Add(Mask);
            else result.Add(Text(a, secretKeys));
        }
        return result;
    }

    /// <summary>
    /// An option whose value is a secret: --token, --password, --api-key, --requirepass, --masterauth and the like. A
    /// one-letter option never is: -p and -a mean too many things.
    /// </summary>
    public static bool SecretOption(string option)
    {
        var name = option.TrimStart('-');
        if (name.Length < 2 || name.Length == option.Length) return false;
        return IsSecretName(name) || PassOption().IsMatch(name);
    }

    /// <summary>
    /// Hides the secrets in a message that's kept or logged: every value given whose name is marked secret or looks it, or
    /// that holds a secret, wherever it appears in the message, then whatever <see cref="Text"/> hides.
    /// </summary>
    public static Func<string, string> Scrubber(IEnumerable<KeyValuePair<string, string>> values, IReadOnlyCollection<string>? secretKeys = null)
    {
        var secrets = values.Where(kv => kv.Value.Length >= 4 && IsSecret(kv.Key, kv.Value, secretKeys))
            .Select(kv => kv.Value).Distinct().OrderByDescending(v => v.Length).ToList();
        return text => Text(secrets.Aggregate(text, (t, secret) => t.Replace(secret, Mask, StringComparison.Ordinal)), secretKeys);
    }

    private static bool Marked(string key, IReadOnlyCollection<string>? secretKeys) => secretKeys is not null && secretKeys.Contains(key);

    [GeneratedRegex(@"(SECRET|PASSWORD|PASSWD|PWD|PASSPHRASE|(?:^|_)PASS(?:_|$)|TOKEN|API_?KEY|PRIVATE|CREDENTIAL|CONNECTION_?STRING|CONN_?STR|DSN|SALT|SIGNING|ACCESS_?KEY|_KEY$|^KEY$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    // scheme://user:password@, the user may be empty. The password may hold / + = (a generated one often does), but
    // host:port/path@ (http://localhost:5173/@vite/client) isn't one.
    [GeneratedRegex(@"(?<head>[a-z][a-z0-9+.\-]*://[^:/@\s]*:)(?!\d{1,5}(?:[/?#\s]|$))[^@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPassword();

    // An option at a word's start, and the value after it: --option=value, or --option value (one that isn't an option).
    [GeneratedRegex(@"(?<![^\s'""])(?<option>--?[A-Za-z][A-Za-z0-9_.\-]*)(?==(?<value>""[^""]*""|'[^']*'|[^\s'""]+)|\s+(?<value>""[^""]*""|'[^']*'|[^\s'""\-][^\s'""]*))")]
    private static partial Regex OptionValue();

    // KEY=value at a word's start.
    [GeneratedRegex(@"(?<![^\s'""])(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?=(?<value>""[^""]*""|'[^']*'|[^\s'""]+))")]
    private static partial Regex Assignment();

    // requirepass, masterpass, dbpass; masterauth (Redis's replica password).
    [GeneratedRegex(@"(?:pass|masterauth)$", RegexOptions.IgnoreCase)]
    private static partial Regex PassOption();
}
