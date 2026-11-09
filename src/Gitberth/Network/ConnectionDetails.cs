using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Network;

/// <summary>The databases, caches and queues Gitberth gives connection details for.</summary>
public enum DatabaseEngine { Postgres, MySql, MongoDb, Redis, RabbitMq }

/// <summary>How a connection is written out (5.2): a URL, a client's command line, a JDBC URL, a .NET connection string, or environment variables.</summary>
public enum ConnectionFormat { Url, Psql, Mysql, Mongosh, RedisCli, Jdbc, DotNet, Environment }

/// <summary>What connecting to a database takes, from its container's own settings.</summary>
/// <param name="User">Null for an engine without users (Redis), or a MongoDB without a root user.</param>
/// <param name="Password">Null when it has none.</param>
/// <param name="Database">Null when there's none to name.</param>
public sealed record ConnectionInfo(DatabaseEngine Engine, string Host, int Port, string? User, string? Password, string? Database);

/// <summary>
/// Connection details for a database Gitberth recognizes (the Postgres family, MySQL and MariaDB, MongoDB, the Redis family
/// and RabbitMQ), read from the container's own settings: its environment variables, and Redis's --requirepass on its
/// command line. Each format writes the values so they survive being pasted: URL-encoded in URLs and JDBC, quoted in a
/// .NET connection string when they need it, single-quoted in a shell. The password shows as •••• until it's revealed;
/// what's copied is the revealed one. Pure.
/// </summary>
public static partial class ConnectionDetails
{
    /// <summary>The engine a container's image is, or null for anything else.</summary>
    public static DatabaseEngine? EngineOf(string image) => ContainerService.Repository(image).ToLowerInvariant() switch
    {
        "postgres" or "postgis" or "timescaledb" or "timescaledb-ha" or "pgvector" => DatabaseEngine.Postgres,
        "mysql" or "mariadb" or "percona-server" => DatabaseEngine.MySql,
        "mongo" or "mongodb-community-server" => DatabaseEngine.MongoDb,
        "redis" or "redis-stack" or "redis-stack-server" or "valkey" or "keydb" or "dragonfly" => DatabaseEngine.Redis,
        "rabbitmq" => DatabaseEngine.RabbitMq,
        _ => null,
    };

    /// <summary>The port an engine listens on unless it's told otherwise: 5432 for Postgres.</summary>
    public static int DefaultPort(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres => 5432,
        DatabaseEngine.MySql => 3306,
        DatabaseEngine.MongoDb => 27017,
        DatabaseEngine.Redis => 6379,
        _ => 5672,
    };

    /// <summary>The port a database it recognizes is reached on (5432 for Postgres), or null.</summary>
    public static int? DatabasePort(ContainerDetails d) => EngineOf(d.Image) is { } engine ? DefaultPort(engine) : null;

    /// <summary>
    /// The details for reaching the container at <paramref name="host"/>, from its own settings; null for what isn't a
    /// database Gitberth recognizes.
    /// </summary>
    /// <param name="port">Where it's reached when that isn't its own port: a published one. Else its usual port when it listens there (or declares nothing), else the first it does listen on.</param>
    public static ConnectionInfo? For(ContainerDetails d, string host, int? port = null)
    {
        if (EngineOf(d.Image) is not { } engine) return null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in d.Env) env[key] = value;
        string? Env(params string[] keys) => keys.Select(k => env.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var listening = ContainerService.ListeningPorts(d);
        var usual = DefaultPort(engine);
        var at = port ?? (listening.Count == 0 || listening.Any(p => p.Port == usual) ? usual : listening[0].Port);

        switch (engine)
        {
            case DatabaseEngine.Postgres:
            {
                var user = Env("POSTGRES_USER") ?? "postgres";
                return new ConnectionInfo(engine, host, at, user, Env("POSTGRES_PASSWORD"), Env("POSTGRES_DB") ?? user);
            }
            case DatabaseEngine.MySql:
            {
                // An app user when one is set up, else root.
                var user = Env("MYSQL_USER", "MARIADB_USER");
                var password = user is null ? Env("MYSQL_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD") : Env("MYSQL_PASSWORD", "MARIADB_PASSWORD");
                return new ConnectionInfo(engine, host, at, user ?? "root", password, Env("MYSQL_DATABASE", "MARIADB_DATABASE"));
            }
            case DatabaseEngine.MongoDb:
            {
                var user = Env("MONGO_INITDB_ROOT_USERNAME", "MONGODB_INITDB_ROOT_USERNAME");
                return new ConnectionInfo(engine, host, at, user, user is null ? null : Env("MONGO_INITDB_ROOT_PASSWORD", "MONGODB_INITDB_ROOT_PASSWORD"), null);
            }
            case DatabaseEngine.Redis:
                return new ConnectionInfo(engine, host, at, null, Env("REDIS_PASSWORD", "VALKEY_PASSWORD") ?? RequirePass(d.Entrypoint.Concat(d.Cmd)), null);
            default:
                // The image's own defaults when it's given none.
                return new ConnectionInfo(engine, host, at, Env("RABBITMQ_DEFAULT_USER") ?? "guest", Env("RABBITMQ_DEFAULT_PASS") ?? "guest", null);
        }
    }

    /// <summary>The details for a container as Docker's inspect describes it (see <see cref="For(ContainerDetails, string, int?)"/>).</summary>
    public static ConnectionInfo? For(JsonObject inspect, string host, int? port = null) =>
        DockerCli.ParseContainerDetails($"[{inspect.ToJsonString()}]").FirstOrDefault() is { } d ? For(d, host, port) : null;

    /// <summary>The formats an engine's details come in, the URL first.</summary>
    public static IReadOnlyList<ConnectionFormat> Formats(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres => [ConnectionFormat.Url, ConnectionFormat.Psql, ConnectionFormat.Jdbc, ConnectionFormat.DotNet, ConnectionFormat.Environment],
        DatabaseEngine.MySql => [ConnectionFormat.Url, ConnectionFormat.Mysql, ConnectionFormat.Jdbc, ConnectionFormat.DotNet],
        DatabaseEngine.MongoDb => [ConnectionFormat.Url, ConnectionFormat.Mongosh],
        DatabaseEngine.Redis => [ConnectionFormat.Url, ConnectionFormat.RedisCli],
        _ => [ConnectionFormat.Url],
    };

    /// <summary>A format's name in the Format select: URL, psql, JDBC, .NET, Environment.</summary>
    public static string Name(ConnectionFormat format) => format switch
    {
        ConnectionFormat.Url => "URL",
        ConnectionFormat.Psql => "psql",
        ConnectionFormat.Mysql => "mysql",
        ConnectionFormat.Mongosh => "mongosh",
        ConnectionFormat.RedisCli => "redis-cli",
        ConnectionFormat.Jdbc => "JDBC",
        ConnectionFormat.DotNet => ".NET",
        _ => "Environment",
    };

    /// <summary>What a URL row is called on the Addresses card: Postgres URL, MySQL URL, MongoDB URL, Redis URL, AMQP URL.</summary>
    public static string UrlName(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres => "Postgres URL",
        DatabaseEngine.MySql => "MySQL URL",
        DatabaseEngine.MongoDb => "MongoDB URL",
        DatabaseEngine.Redis => "Redis URL",
        _ => "AMQP URL",
    };

    /// <summary>
    /// The details written out in a format the engine has (<see cref="Formats"/>). The password shows as •••• unless
    /// <paramref name="reveal"/>: what's shown can be masked, what's copied never is.
    /// </summary>
    public static string Render(ConnectionInfo info, ConnectionFormat format, bool reveal)
    {
        if (!Formats(info.Engine).Contains(format)) throw new ArgumentOutOfRangeException(nameof(format), $"{info.Engine} has no {Name(format)} format.");
        var port = info.Port.ToString(CultureInfo.InvariantCulture);
        var url = UrlHost(info.Host) + ":" + port;
        // URL-encoded, so nothing in it means anything to a URL or a shell.
        string Encoded(string? password) => password is null ? "" : reveal ? Uri.EscapeDataString(password) : Security.Masking.Mask;
        string Credentials() => Uri.EscapeDataString(info.User ?? "") + (info.Password is null ? "" : ":" + Encoded(info.Password));
        string Url() => info.Engine switch
        {
            DatabaseEngine.Postgres => $"postgres://{Credentials()}@{url}/{Uri.EscapeDataString(info.Database ?? "")}",
            DatabaseEngine.MySql => $"mysql://{Credentials()}@{url}/{(info.Database is null ? "" : Uri.EscapeDataString(info.Database))}",
            DatabaseEngine.MongoDb => $"mongodb://{(info.User is null ? "" : Credentials() + "@")}{url}",
            DatabaseEngine.Redis => $"redis://{(info.Password is null ? "" : ":" + Encoded(info.Password) + "@")}{url}",
            _ => $"amqp://{Credentials()}@{url}",
        };
        string ShellPassword(string password) => reveal ? Quoted(password) : Security.Masking.Mask;

        switch (format)
        {
            case ConnectionFormat.Url:
                return Url();
            case ConnectionFormat.Psql:
                return $"psql \"{Url()}\"";
            case ConnectionFormat.Mongosh:
                return $"mongosh \"{Url()}\"";
            case ConnectionFormat.Mysql:
                // -p with nothing after it asks for the password, so it never lands in the shell's history.
                return $"mysql -h {Quoted(info.Host)} -P {port} -u {Quoted(info.User ?? "root")} -p" + (info.Database is null ? "" : " " + Quoted(info.Database));
            case ConnectionFormat.RedisCli:
                return $"redis-cli -h {Quoted(info.Host)} -p {port}" + (info.Password is null ? "" : " -a " + ShellPassword(info.Password));
            case ConnectionFormat.Jdbc:
            {
                var scheme = info.Engine == DatabaseEngine.Postgres ? "postgresql" : "mysql";
                var query = "?user=" + Uri.EscapeDataString(info.User ?? "") + (info.Password is null ? "" : "&password=" + Encoded(info.Password));
                return $"jdbc:{scheme}://{url}/{(info.Database is null ? "" : Uri.EscapeDataString(info.Database))}{query}";
            }
            case ConnectionFormat.DotNet:
            {
                // Npgsql's names, or MySqlConnector's.
                var postgres = info.Engine == DatabaseEngine.Postgres;
                var parts = new List<string>
                {
                    $"{(postgres ? "Host" : "Server")}={Net(info.Host)}",
                    $"Port={port}",
                    $"{(postgres ? "Username" : "User ID")}={Net(info.User ?? "")}",
                };
                if (info.Password is { } password) parts.Add($"Password={(reveal ? Net(password) : Security.Masking.Mask)}");
                if (info.Database is { } database) parts.Add($"Database={Net(database)}");
                return string.Join(';', parts);
            }
            default:
            {
                var vars = new List<string> { $"PGHOST={Quoted(info.Host)}", $"PGPORT={port}", $"PGUSER={Quoted(info.User ?? "")}" };
                if (info.Password is { } password) vars.Add($"PGPASSWORD={ShellPassword(password)}");
                if (info.Database is { } database) vars.Add($"PGDATABASE={Quoted(database)}");
                return string.Join(' ', vars);
            }
        }
    }

    // An IPv6 address in a URL is bracketed.
    private static string UrlHost(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    /// <summary>A value for a shell: as it is when nothing in it means anything to one, else single-quoted ('it'\''s').</summary>
    internal static string Quoted(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,+%".Contains(c)) ? value : "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// A value for a .NET connection string, as DbConnectionStringBuilder writes one: as it is unless it holds ; = ' " or
    /// white space, else in double quotes with each " doubled, or in single quotes when it holds a " but no '.
    /// </summary>
    internal static string Net(string value) =>
        NetPlain().IsMatch(value) ? value
        : value.Contains('"') && !value.Contains('\'') ? $"'{value}'"
        : $"\"{value.Replace("\"", "\"\"")}\"";

    // redis-server --requirepass secret, given as separate arguments or one shell line.
    private static string? RequirePass(IEnumerable<string> args)
    {
        var words = args.SelectMany(a => a.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == "--requirepass" && i + 1 < words.Count) return words[i + 1];
            if (words[i].StartsWith("--requirepass=", StringComparison.Ordinal)) return words[i]["--requirepass=".Length..];
        }
        return null;
    }

    [GeneratedRegex(@"^[^""'=;\s\p{Cc}]*$")]
    private static partial Regex NetPlain();
}
