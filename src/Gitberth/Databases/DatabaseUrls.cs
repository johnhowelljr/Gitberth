using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Databases;

/// <summary>A postgres:// (or postgresql://) URL's parts, unescaped, with what follows its path (?sslmode=…) as it was written.</summary>
/// <param name="Port">Null when it names none (5432 then).</param>
/// <param name="Rest">Its query and fragment as written, "" for none: a preview's URL keeps them.</param>
public sealed record PostgresUrl(string Scheme, string? User, string? Password, string Host, int? Port, string? Database, string Rest);

/// <summary>Connection URLs: written for a database Gitberth made, read from one an app has, and rewritten for a preview. Pure.</summary>
public static partial class DatabaseUrls
{
    /// <summary>postgres://role:password@host:5432/database, each part escaped.</summary>
    public static string Postgres(string user, string password, string host, int port, string database) =>
        $"postgres://{Esc(user)}:{Esc(password)}@{UrlHost(host)}:{port.ToString(CultureInfo.InvariantCulture)}/{Esc(database)}";

    /// <summary>redis://:password@host:6379 (Redis has no user of its own here: the default one, with the password).</summary>
    public static string Redis(string password, string host, int port) =>
        $"redis://:{Esc(password)}@{UrlHost(host)}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A postgres:// or postgresql:// URL's parts; null for anything else (a libpq key=value string, a .NET connection string).</summary>
    public static PostgresUrl? ParsePostgres(string? value)
    {
        if (value is null || PostgresPattern().Match(value.Trim()) is not { Success: true } m) return null;
        var host = m.Groups["host"].Value;
        if (host.Length == 0) return null;
        int? port = m.Groups["port"].Success && int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        string? Part(string name) => m.Groups[name].Success ? Unesc(m.Groups[name].Value) : null;
        var database = Part("db");
        return new PostgresUrl(m.Groups["scheme"].Value, Part("user") is { Length: > 0 } user ? user : null, Part("pass"), host.Trim('[', ']'), port,
            database is { Length: > 0 } ? database : null, m.Groups["rest"].Value);
    }

    /// <summary>
    /// A preview's URL from production's: the same scheme, host, port, query and fragment (?sslmode=disable&amp;schema=public
    /// stay), with the preview's own role, password and database in place of production's.
    /// </summary>
    public static string ForPreview(PostgresUrl production, string user, string password, string database) =>
        $"{production.Scheme}://{Esc(user)}:{Esc(password)}@{UrlHost(production.Host)}"
        + (production.Port is int port ? ":" + port.ToString(CultureInfo.InvariantCulture) : "")
        + "/" + Esc(database) + production.Rest;

    /// <summary>The URL with its password masked, for anything that's shown or logged.</summary>
    public static string Masked(string url) => PasswordPart().Replace(url, m => m.Groups[1].Value + ":" + Security.Masking.Mask + "@");

    /// <summary>The suffixes of a database variable's parts: V_HOST, V_PORT, V_NAME, V_USER, V_PASSWORD.</summary>
    public static readonly IReadOnlyList<string> PartSuffixes = ["_HOST", "_PORT", "_NAME", "_USER", "_PASSWORD"];

    /// <summary>
    /// The names a database variable's parts go by in a Compose app's .env: V_HOST, V_PORT, V_NAME, V_USER and V_PASSWORD for
    /// Postgres; V_HOST, V_PORT and V_PASSWORD for Redis, which has no database name or user of its own.
    /// </summary>
    public static IReadOnlyList<string> PartNames(string variable, Data.DatabaseKind kind) =>
        PartSuffixes.Where(s => kind == Data.DatabaseKind.Postgres || s is "_HOST" or "_PORT" or "_PASSWORD").Select(s => variable + s).ToList();

    /// <summary>
    /// A database variable's connection string in parts, for an app that wants them one by one (DB_HOST, DB_PORT…) rather
    /// than as a URL: <paramref name="variable"/>_HOST, _PORT, _NAME and _USER (Postgres) and _PASSWORD, each unescaped. A
    /// postgres:// URL gives all five (its port 5432 when it names none); a redis:// one its host, port (6379) and
    /// password. Empty for anything else; a part the URL doesn't have isn't given.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Parts(string variable, string url)
    {
        var list = new List<(string, string)>();
        if (ParsePostgres(url) is { } pg)
        {
            list.Add((variable + "_HOST", pg.Host));
            list.Add((variable + "_PORT", (pg.Port ?? 5432).ToString(CultureInfo.InvariantCulture)));
            if (pg.Database is { } name) list.Add((variable + "_NAME", name));
            if (pg.User is { } user) list.Add((variable + "_USER", user));
            if (pg.Password is { Length: > 0 } password) list.Add((variable + "_PASSWORD", password));
            return list;
        }
        if (RedisPattern().Match(url.Trim()) is { Success: true } m && m.Groups["host"].Value.Length > 0)
        {
            list.Add((variable + "_HOST", m.Groups["host"].Value.Trim('[', ']')));
            list.Add((variable + "_PORT", m.Groups["port"].Success ? m.Groups["port"].Value : "6379"));
            if (m.Groups["pass"].Success && m.Groups["pass"].Value.Length > 0) list.Add((variable + "_PASSWORD", Unesc(m.Groups["pass"].Value)));
        }
        return list;
    }

    private static string Esc(string part) => Uri.EscapeDataString(part);

    private static string Unesc(string part)
    {
        try
        {
            return Uri.UnescapeDataString(part);
        }
        catch (UriFormatException)
        {
            return part;
        }
    }

    // An IPv6 address in a URL is bracketed.
    private static string UrlHost(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    [GeneratedRegex(@"^(?<scheme>postgres(?:ql)?)://(?:(?<user>[^:@/?#]*)(?::(?<pass>[^@/?#]*))?@)?(?<host>\[[^\]]+\]|[^:/?#,]*)(?::(?<port>\d{1,5}))?(?:/(?<db>[^?#]*))?(?<rest>[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PostgresPattern();

    [GeneratedRegex(@"^([a-z][a-z0-9+.-]*://[^:@/?#]*):[^@/?#]*@", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPart();

    [GeneratedRegex(@"^rediss?://(?:(?<user>[^:@/?#]*)(?::(?<pass>[^@/?#]*))?@)?(?<host>\[[^\]]+\]|[^:/?#,]*)(?::(?<port>\d{1,5}))?(?:/[^?#]*)?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RedisPattern();
}
