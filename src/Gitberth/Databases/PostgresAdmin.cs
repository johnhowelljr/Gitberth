using Gitberth.Docker;

namespace Gitberth.Databases;

/// <summary>What a copy of production's data into a preview's database came to.</summary>
/// <param name="Ok">The data was copied, maybe with some objects left out (<paramref name="Warnings"/>).</param>
/// <param name="Warnings">pg_restore's errors it went on past (an extension only a superuser makes, say), a few lines at most.</param>
/// <param name="Error">Why nothing was copied.</param>
public sealed record CopyResult(bool Ok, IReadOnlyList<string> Warnings, string? Error);

/// <summary>
/// Gitberth's work on a Postgres server, inside the server's own container (psql, pg_dump and pg_restore through docker
/// exec): the SQL goes on psql's standard input, and the superuser's password, when psql needs one, through its
/// environment. Nothing of it is ever on a command line, and a password only ever as its SCRAM verifier.
/// </summary>
internal sealed class PostgresAdmin(DockerCli docker)
{
    /// <summary>psql as <paramref name="user"/> on <paramref name="database"/>, the script on its standard input.</summary>
    internal static Task<ProcessResult> RunAsync(DockerCli docker, string container, string user, string? password, string database, string sql, CancellationToken ct) =>
        docker.ExecAsync(container, PostgresSql.Psql(user, database), sql, Env(password), ct, maxCapture: 256 * 1024);

    private static Dictionary<string, string>? Env(string? password) =>
        string.IsNullOrEmpty(password) ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["PGPASSWORD"] = password };

    private Task<ProcessResult> PsqlAsync(ServerLogin login, string sql, CancellationToken ct) =>
        RunAsync(docker, login.Container, login.User, login.Password, "postgres", sql, ct);

    /// <summary>Every database and role name on the server.</summary>
    /// <exception cref="InvalidOperationException">psql failed: its first line says why.</exception>
    public async Task<HashSet<string>> NamesAsync(ServerLogin login, CancellationToken ct)
    {
        var r = await PsqlAsync(login, PostgresSql.Names, ct);
        if (!r.Ok) throw new InvalidOperationException($"Gitberth couldn't read the names on {login.Container}: {FirstLine(r.ErrorText) ?? "psql failed"}.");
        var (databases, roles) = PostgresSql.ParseNames(r.StdOut);
        databases.UnionWith(roles);
        return databases;
    }

    /// <summary>The database and its role, with the password as its verifier. Null when it's made, else why not (and nothing is left of it).</summary>
    public async Task<string?> CreateAsync(ServerLogin login, string database, string role, string password, CancellationToken ct)
    {
        var r = await PsqlAsync(login, PostgresSql.Create(database, role, Scram.Verifier(password)), ct);
        if (r.Ok) return null;
        // What the script made before the statement that failed goes again, so nothing is left behind; the statement that
        // failed made nothing, so a role or database someone else made meanwhile by that name is never touched.
        var failed = PostgresSql.FailedStatement(r.ErrorText);
        if (failed >= PostgresSql.CreateRevokeStatement) await PsqlAsync(login, PostgresSql.Drop(database, role, login.Version), CancellationToken.None);
        else if (failed == PostgresSql.CreateDatabaseStatement) await PsqlAsync(login, PostgresSql.DropRole(role), CancellationToken.None);
        return $"{login.Container} refused the database {database}: {FirstLine(r.ErrorText) ?? "psql failed"}.";
    }

    /// <summary>The database (and whoever is connected to it) and its role. Null when they're gone, else why not.</summary>
    public async Task<string?> DropAsync(ServerLogin login, string database, string? role, CancellationToken ct)
    {
        var r = await PsqlAsync(login, PostgresSql.Drop(database, role, login.Version), ct);
        return r.Ok ? null : $"{login.Container} didn't drop {database}: {FirstLine(r.ErrorText) ?? "psql failed"}.";
    }

    /// <summary>Each database's size in bytes; the ones the server doesn't have are left out.</summary>
    public async Task<Dictionary<string, long>> SizesAsync(ServerLogin login, IReadOnlyCollection<string> databases, CancellationToken ct)
    {
        if (databases.Count == 0) return new Dictionary<string, long>(StringComparer.Ordinal);
        var r = await PsqlAsync(login, PostgresSql.Sizes(databases), ct);
        return r.Ok ? PostgresSql.ParseSizes(r.StdOut) : new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Why production's database named by a URL (one Gitberth doesn't keep for the app) may not be copied into a preview, or
    /// null when it may: the URL's own user and password must sign in to the server (checked against the verifier Postgres
    /// keeps, <see cref="Scram.Checks"/>), and that role must own the database, be granted CONNECT on it, or be a superuser.
    /// Gitberth copies as the server's superuser, so without this anyone who can set the app's variables (a repo's GitHub
    /// variables) could have another app's database copied into a preview whose code they write.
    /// </summary>
    public async Task<string?> CopyRefusalAsync(ServerLogin login, PostgresUrl url, string database, CancellationToken ct)
    {
        if (url.User is not { Length: > 0 } user || url.Password is not { Length: > 0 } password)
            return "its URL has no user and password to check that they may read it";
        if (user.Length > DatabaseNames.MaxLength || database.Length > DatabaseNames.MaxLength) return $"{user} isn't a role on {login.Container}";
        var r = await PsqlAsync(login, PostgresSql.CopyAccess(user, database), ct);
        if (!r.Ok) return $"Gitberth couldn't check who may read it ({FirstLine(r.ErrorText) ?? "psql failed"})";
        if (PostgresSql.ParseCopyAccess(r.StdOut) is not { } access) return $"{user} isn't a role on {login.Container}, or it has no database {database}";
        if (!Scram.Checks(password, user, access.Verifier)) return $"the URL's password doesn't sign in to {login.Container} as {user}";
        return access.Allowed ? null : $"{user} doesn't own {database} and wasn't granted CONNECT on it";
    }

    /// <summary>
    /// Production's data into a preview's database, owned by the preview's role (pg_dump into pg_restore, inside the server's
    /// container). What pg_restore went on past is a warning; nothing copied at all is an error.
    /// </summary>
    public async Task<CopyResult> CopyAsync(ServerLogin login, string from, string to, string role, CancellationToken ct)
    {
        var r = await docker.ExecAsync(login.Container, PostgresSql.Copy(login.User, from, to, role), null, Env(login.Password), ct, maxCapture: 64 * 1024);
        if (r.Ok) return new CopyResult(true, [], null);
        var lines = (r.StdErr + "\n" + r.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // pg_restore went on past some statements: the rest is there.
        if (lines.Any(l => l.Contains("errors ignored on restore", StringComparison.OrdinalIgnoreCase)))
            return new CopyResult(true, lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5).ToList(), null);
        return new CopyResult(false, [], FirstLine(r.ErrorText) ?? $"pg_dump or pg_restore failed (exit code {r.ExitCode}).");
    }

    /// <summary>The first line of a message, without psql's "psql: error:" or "ERROR:" in front.</summary>
    internal static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return null;
        foreach (var prefix in new[] { "psql:<stdin>:", "psql: error:", "psql:" })
            if (line.StartsWith(prefix, StringComparison.Ordinal)) line = line[prefix.Length..].Trim();
        // psql:<stdin>:3: ERROR:  database "x" already exists
        var error = line.IndexOf("ERROR:", StringComparison.Ordinal);
        if (error >= 0) line = line[(error + "ERROR:".Length)..].Trim();
        return line.TrimEnd('.');
    }
}
