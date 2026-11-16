using System.Globalization;
using System.Text;

namespace Gitberth.Databases;

/// <summary>
/// The SQL Gitberth sends a Postgres server (on psql's standard input, never its command line). Names are always quoted as
/// identifiers and values as literals, so nothing in them is ever read as SQL; a password is only ever a SCRAM verifier
/// (<see cref="Scram"/>). Pure.
/// </summary>
public static partial class PostgresSql
{
    /// <summary>The first version with DROP DATABASE … WITH (FORCE): 13.</summary>
    public const int ForceVersion = 130000;

    /// <summary>A name as a quoted identifier: "shop", "a""b".</summary>
    /// <exception cref="ArgumentException">It's empty, too long, or holds a NUL, which no Postgres name can.</exception>
    public static string Ident(string name)
    {
        if (name.Length == 0 || name.Length > DatabaseNames.MaxLength || name.Contains('\0'))
            throw new ArgumentException($"'{name}' can't be a Postgres name.", nameof(name));
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>A value as a quoted literal: 'it''s'. Backslashes are plain characters (standard_conforming_strings).</summary>
    /// <exception cref="ArgumentException">It holds a NUL.</exception>
    public static string Literal(string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("A Postgres value can't hold a NUL.", nameof(text));
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>What Gitberth asks first: the server's version number, and whether it signed in as a superuser ("180001|t").</summary>
    public const string Probe = "SELECT current_setting('server_version_num') || '|' || (SELECT CASE WHEN rolsuper THEN 't' ELSE 'f' END FROM pg_roles WHERE rolname = current_user);";

    /// <summary>Every database and role name on the server, one a line: d:shop, r:shop.</summary>
    public const string Names = "SELECT 'd:' || datname FROM pg_database UNION ALL SELECT 'r:' || rolname FROM pg_roles;";

    /// <summary><see cref="Create"/>'s statements, one a line, by their line: the role, the database, then the REVOKE.</summary>
    public const int CreateRoleStatement = 1, CreateDatabaseStatement = 2, CreateRevokeStatement = 3;

    /// <summary>
    /// The line of the script whose statement psql stopped at (ON_ERROR_STOP): "psql:&lt;stdin&gt;:2: ERROR: …" is 2. Null when
    /// it doesn't say (it couldn't sign in, say): nothing of the script ran then.
    /// </summary>
    public static int? FailedStatement(string? error) =>
        error is not null && FailedLine().Match(error) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) ? line : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"psql:<stdin>:(\d+):")]
    private static partial System.Text.RegularExpressions.Regex FailedLine();

    /// <summary>
    /// A database and its own role: the role signs in with the password (sent as its verifier), owns the database, and is the
    /// only one but superusers who may connect to it. One statement a line (<see cref="FailedStatement"/>).
    /// </summary>
    public static string Create(string database, string role, string verifier)
    {
        if (!verifier.StartsWith("SCRAM-SHA-256$", StringComparison.Ordinal)) throw new ArgumentException("A password goes to Postgres as a SCRAM verifier only.", nameof(verifier));
        var sb = new StringBuilder();
        sb.Append("CREATE ROLE ").Append(Ident(role)).Append(" LOGIN PASSWORD ").Append(Literal(verifier)).Append(";\n");
        sb.Append("CREATE DATABASE ").Append(Ident(database)).Append(" OWNER ").Append(Ident(role)).Append(";\n");
        sb.Append("REVOKE CONNECT, TEMPORARY ON DATABASE ").Append(Ident(database)).Append(" FROM PUBLIC;\n");
        return sb.ToString();
    }

    /// <summary>
    /// The database (with whoever is connected to it) and its role. Before Postgres 13, which can't force it, its sessions are
    /// ended first.
    /// </summary>
    public static string Drop(string database, string? role, int serverVersion)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = ").Append(Literal(database)).Append(" AND pid <> pg_backend_pid();\n");
        sb.Append("DROP DATABASE IF EXISTS ").Append(Ident(database)).Append(serverVersion >= ForceVersion ? " WITH (FORCE);\n" : ";\n");
        if (role is not null) sb.Append("DROP ROLE IF EXISTS ").Append(Ident(role)).Append(";\n");
        return sb.ToString();
    }

    /// <summary>Only the role, for a create that made it but not its database.</summary>
    public static string DropRole(string role) => "DROP ROLE IF EXISTS " + Ident(role) + ";\n";

    /// <summary>Each of the databases' size in bytes, one a line: shop:7861015.</summary>
    public static string Sizes(IEnumerable<string> databases) =>
        "SELECT datname || ':' || pg_database_size(datname) FROM pg_database WHERE datname IN (" + string.Join(", ", databases.Select(Literal)) + ");";

    /// <summary>Lines of <see cref="Names"/>: (databases, roles).</summary>
    public static (HashSet<string> Databases, HashSet<string> Roles) ParseNames(string output)
    {
        var databases = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("d:", StringComparison.Ordinal)) databases.Add(text[2..]);
            else if (text.StartsWith("r:", StringComparison.Ordinal)) roles.Add(text[2..]);
        }
        return (databases, roles);
    }

    /// <summary>Lines of <see cref="Sizes"/>, by database.</summary>
    public static Dictionary<string, long> ParseSizes(string output)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            var colon = text.LastIndexOf(':');
            if (colon > 0 && long.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) sizes[text[..colon]] = bytes;
        }
        return sizes;
    }

    /// <summary>The probe's answer: (180001, true); null when it isn't one.</summary>
    public static (int Version, bool Superuser)? ParseProbe(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Contains('|'));
        if (line is null) return null;
        var parts = line.Split('|');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? (version, parts[1] == "t") : null;
    }

    /// <summary>A version number as people say it: 180001 is 18.1, 90624 is 9.6.24.</summary>
    public static string VersionText(int number) => number >= 100000
        ? $"{number / 10000}.{number % 10000}"
        : $"{number / 10000}.{number / 100 % 100}.{number % 100}";

    /// <summary>
    /// psql's arguments: no ~/.psqlrc, quiet, stop at the first error, rows as plain lines, and the script read from stdin as
    /// a file (<c>-f -</c>), so an error says the line it stopped at ("psql:&lt;stdin&gt;:2: ERROR: …", <see cref="FailedStatement"/>);
    /// a script piped in without it gets a bare "ERROR: …".
    /// </summary>
    public static IReadOnlyList<string> Psql(string user, string database) => ["psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-t", "-A", "-U", user, "-d", database, "-f", "-"];

    /// <summary>
    /// Whether a production URL's role may have its database copied into a preview: its password verifier when it may sign
    /// in (empty when it can't, or has none), and whether it owns the database (or is a member of its owner), was granted
    /// CONNECT on it (itself or through a role it's a member of, never only as PUBLIC), or is a superuser. One line,
    /// "SCRAM-SHA-256$…|t"; none when there's no such role or database. Only a superuser reads pg_authid, as Gitberth signs in.
    /// </summary>
    public static string CopyAccess(string role, string database) =>
        "SELECT CASE WHEN r.rolcanlogin AND (r.rolvaliduntil IS NULL OR r.rolvaliduntil > now()) THEN coalesce(r.rolpassword, '') ELSE '' END || '|' || "
        + "CASE WHEN r.rolsuper OR pg_has_role(r.oid, d.datdba, 'MEMBER') OR EXISTS (SELECT 1 FROM aclexplode(d.datacl) a "
        + "WHERE a.privilege_type = 'CONNECT' AND a.grantee <> 0 AND pg_has_role(r.oid, a.grantee, 'MEMBER')) THEN 't' ELSE 'f' END "
        + "FROM pg_authid r CROSS JOIN pg_database d WHERE r.rolname = " + Literal(role) + " AND d.datname = " + Literal(database) + ";";

    /// <summary><see cref="CopyAccess"/>'s answer: (the verifier, "" for none; whether it may read the database); null for no row.</summary>
    public static (string Verifier, bool Allowed)? ParseCopyAccess(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Contains('|'));
        if (line is null) return null;
        var bar = line.LastIndexOf('|');
        return (line[..bar], line[(bar + 1)..] == "t");
    }

    /// <summary>
    /// A copy of one database into another, inside the server's container: pg_dump's custom format piped into pg_restore,
    /// whose objects the role owns (it restores as that role, without the source's owners or privileges). The names go as
    /// the shell's arguments, never into its script.
    /// </summary>
    public static IReadOnlyList<string> Copy(string user, string from, string to, string role) =>
    [
        "sh", "-c", "pg_dump -U \"$1\" -d \"$2\" -Fc --no-owner --no-privileges | pg_restore -U \"$1\" -d \"$3\" --no-owner --no-privileges --role=\"$4\"",
        "sh", user, from, to, role,
    ];
}
