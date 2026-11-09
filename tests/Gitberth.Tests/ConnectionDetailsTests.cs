using Gitberth.Docker;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>Connection details for the databases Gitberth recognizes, from their containers' own settings, in every format the Connect dialog offers.</summary>
public class ConnectionDetailsTests
{
    private static ContainerDetails Details(string image, IReadOnlyList<KeyValuePair<string, string>>? env = null, IReadOnlyList<string>? cmd = null,
        IReadOnlyList<ExposedPort>? exposed = null) =>
        new("id", "x", image, "sha256:1", null, "running", true, 0, null, null, null, false, 0, [], cmd ?? [], null, null, env ?? [],
            new Dictionary<string, string>(), "no", [], [], [], "bridge", 0, 0, exposed ?? []);

    private static List<KeyValuePair<string, string>> Env(params string[] pairs) =>
        pairs.Select(p => p.Split('=', 2)).Select(kv => new KeyValuePair<string, string>(kv[0], kv[1])).ToList();

    private static ConnectionInfo Info(ContainerDetails d, string host = "postgres", int? port = null) => ConnectionDetails.For(d, host, port)!;

    private static Dictionary<ConnectionFormat, string> Rendered(ConnectionInfo info, bool reveal = true) =>
        ConnectionDetails.Formats(info.Engine).ToDictionary(f => f, f => ConnectionDetails.Render(info, f, reveal));

    [Fact]
    public void Builds_postgres_fields_and_every_format()
    {
        var d = Details("postgres:18", Env("POSTGRES_USER=kanban", "POSTGRES_PASSWORD=s3cret", "POSTGRES_DB=app"), exposed: [new ExposedPort(5432, "tcp")]);

        var lan = Info(d, "10.1.1.80");
        Assert.Equal(new ConnectionInfo(DatabaseEngine.Postgres, "10.1.1.80", 5432, "kanban", "s3cret", "app"), lan);
        Assert.Equal(new Dictionary<ConnectionFormat, string>
        {
            [ConnectionFormat.Url] = "postgres://kanban:s3cret@10.1.1.80:5432/app",
            [ConnectionFormat.Psql] = "psql \"postgres://kanban:s3cret@10.1.1.80:5432/app\"",
            [ConnectionFormat.Jdbc] = "jdbc:postgresql://10.1.1.80:5432/app?user=kanban&password=s3cret",
            [ConnectionFormat.DotNet] = "Host=10.1.1.80;Port=5432;Username=kanban;Password=s3cret;Database=app",
            [ConnectionFormat.Environment] = "PGHOST=10.1.1.80 PGPORT=5432 PGUSER=kanban PGPASSWORD=s3cret PGDATABASE=app",
        }, Rendered(lan));
        Assert.Equal(["URL", "psql", "JDBC", ".NET", "Environment"], ConnectionDetails.Formats(DatabaseEngine.Postgres).Select(ConnectionDetails.Name));
        Assert.Equal("Postgres URL", ConnectionDetails.UrlName(DatabaseEngine.Postgres));

        // Reached through a published port that isn't its own.
        Assert.Equal("postgres://kanban:s3cret@10.1.1.80:15432/app", ConnectionDetails.Render(Info(d, "10.1.1.80", 15432), ConnectionFormat.Url, reveal: true));
        // The image's defaults when it's told nothing else; an IPv6 host is bracketed in a URL.
        var plain = Info(Details("postgres:18"), "fd00::5");
        Assert.Equal(new ConnectionInfo(DatabaseEngine.Postgres, "fd00::5", 5432, "postgres", null, "postgres"), plain);
        Assert.Equal("postgres://postgres@[fd00::5]:5432/postgres", ConnectionDetails.Render(plain, ConnectionFormat.Url, reveal: true));
        Assert.Equal("Host=fd00::5;Port=5432;Username=postgres;Database=postgres", ConnectionDetails.Render(plain, ConnectionFormat.DotNet, reveal: true));
        // It listens elsewhere: the first port it declares.
        Assert.Equal(6432, Info(Details("postgres:18", exposed: [new ExposedPort(6432, "tcp")])).Port);
        Assert.Equal(5432, ConnectionDetails.DatabasePort(d));
    }

    [Theory]
    [InlineData("mysql:8", "MYSQL_ROOT_PASSWORD=r00t", null, "mysql://root:r00t@db:3306/")]
    [InlineData("mariadb:11", "MARIADB_USER=app", "MARIADB_PASSWORD=secret", "mysql://app:secret@db:3306/")]
    [InlineData("mongo:8", "MONGO_INITDB_ROOT_USERNAME=admin", "MONGO_INITDB_ROOT_PASSWORD=pw", "mongodb://admin:pw@db:27017")]
    [InlineData("mongo:8", null, null, "mongodb://db:27017")]
    [InlineData("redis:7", null, null, "redis://db:6379")]
    [InlineData("rabbitmq:4-management", null, null, "amqp://guest:guest@db:5672")]
    public void Builds_mysql_mongo_redis_and_rabbitmq(string image, string? first, string? second, string url)
    {
        var info = Info(Details(image, Env(new[] { first, second }.OfType<string>().ToArray())), "db");

        Assert.Equal(url, ConnectionDetails.Render(info, ConnectionFormat.Url, reveal: true));
        Assert.All(ConnectionDetails.Formats(info.Engine), f => Assert.NotEmpty(ConnectionDetails.Render(info, f, reveal: true)));
    }

    [Fact]
    public void Writes_the_other_engines_formats()
    {
        var mysql = Info(Details("mariadb:11", Env("MARIADB_USER=app", "MARIADB_PASSWORD=secret", "MARIADB_DATABASE=shop")), "10.1.1.80");
        Assert.Equal(new Dictionary<ConnectionFormat, string>
        {
            [ConnectionFormat.Url] = "mysql://app:secret@10.1.1.80:3306/shop",
            // -p alone: mysql asks for the password.
            [ConnectionFormat.Mysql] = "mysql -h 10.1.1.80 -P 3306 -u app -p shop",
            [ConnectionFormat.Jdbc] = "jdbc:mysql://10.1.1.80:3306/shop?user=app&password=secret",
            [ConnectionFormat.DotNet] = "Server=10.1.1.80;Port=3306;User ID=app;Password=secret;Database=shop",
        }, Rendered(mysql));

        var mongo = Info(Details("mongo:8", Env("MONGO_INITDB_ROOT_USERNAME=admin", "MONGO_INITDB_ROOT_PASSWORD=pw")), "10.1.1.80");
        Assert.Equal("mongosh \"mongodb://admin:pw@10.1.1.80:27017\"", ConnectionDetails.Render(mongo, ConnectionFormat.Mongosh, reveal: true));

        var redis = Info(Details("redis:7", Env("REDIS_PASSWORD=pw")), "10.1.1.80");
        Assert.Equal("redis-cli -h 10.1.1.80 -p 6379 -a pw", ConnectionDetails.Render(redis, ConnectionFormat.RedisCli, reveal: true));
        Assert.Equal("redis-cli -h 10.1.1.80 -p 6379", ConnectionDetails.Render(Info(Details("redis:7"), "10.1.1.80"), ConnectionFormat.RedisCli, reveal: true));

        Assert.Equal([ConnectionFormat.Url], ConnectionDetails.Formats(DatabaseEngine.RabbitMq));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionDetails.Render(redis, ConnectionFormat.Jdbc, reveal: true));
        Assert.Equal(["MySQL URL", "MongoDB URL", "Redis URL", "AMQP URL"],
            new[] { DatabaseEngine.MySql, DatabaseEngine.MongoDb, DatabaseEngine.Redis, DatabaseEngine.RabbitMq }.Select(ConnectionDetails.UrlName));
    }

    [Fact]
    public void Finds_redis_password_on_its_command_line()
    {
        var d = Details("redis:7-alpine", cmd: ["sh", "-c", "redis-server --appendonly yes --requirepass s3cret"]);
        Assert.Equal("s3cret", Info(d, "cache").Password);
        Assert.Equal("redis://:s3cret@cache:6379", ConnectionDetails.Render(Info(d, "cache"), ConnectionFormat.Url, reveal: true));
        Assert.Equal("s3cret", Info(Details("valkey/valkey:8", cmd: ["valkey-server", "--requirepass=s3cret"]), "cache").Password);
    }

    [Fact]
    public void Masks_the_password_but_copies_the_real_value()
    {
        var info = Info(Details("postgres:18", Env("POSTGRES_USER=kanban", "POSTGRES_PASSWORD=p@ss word", "POSTGRES_DB=app")));

        var shown = Rendered(info, reveal: false);
        Assert.Equal("postgres://kanban:••••@postgres:5432/app", shown[ConnectionFormat.Url]);
        Assert.Equal("jdbc:postgresql://postgres:5432/app?user=kanban&password=••••", shown[ConnectionFormat.Jdbc]);
        Assert.Equal("Host=postgres;Port=5432;Username=kanban;Password=••••;Database=app", shown[ConnectionFormat.DotNet]);
        Assert.Equal("PGHOST=postgres PGPORT=5432 PGUSER=kanban PGPASSWORD=•••• PGDATABASE=app", shown[ConnectionFormat.Environment]);
        Assert.All(shown.Values, s => Assert.DoesNotContain("p@ss", s));
        // What's copied has it, written so it survives the paste.
        Assert.Equal("postgres://kanban:p%40ss%20word@postgres:5432/app", ConnectionDetails.Render(info, ConnectionFormat.Url, reveal: true));
        Assert.Equal("redis-cli -h cache -p 6379 -a ••••", ConnectionDetails.Render(Info(Details("redis:7", Env("REDIS_PASSWORD=pw")), "cache"), ConnectionFormat.RedisCli, reveal: false));
    }

    [Fact]
    public void Escapes_special_characters_in_every_format()
    {
        // A user and password that would break each format if written raw.
        var info = Info(Details("postgres:18", Env("POSTGRES_USER=app user", "POSTGRES_PASSWORD=p@ss;w'rd$", "POSTGRES_DB=app")), "10.1.1.80");

        Assert.Equal(new Dictionary<ConnectionFormat, string>
        {
            [ConnectionFormat.Url] = "postgres://app%20user:p%40ss%3Bw%27rd%24@10.1.1.80:5432/app",
            [ConnectionFormat.Psql] = "psql \"postgres://app%20user:p%40ss%3Bw%27rd%24@10.1.1.80:5432/app\"",
            [ConnectionFormat.Jdbc] = "jdbc:postgresql://10.1.1.80:5432/app?user=app%20user&password=p%40ss%3Bw%27rd%24",
            [ConnectionFormat.DotNet] = "Host=10.1.1.80;Port=5432;Username=\"app user\";Password=\"p@ss;w'rd$\";Database=app",
            [ConnectionFormat.Environment] = "PGHOST=10.1.1.80 PGPORT=5432 PGUSER='app user' PGPASSWORD='p@ss;w'\\''rd$' PGDATABASE=app",
        }, Rendered(info));

        var redis = Info(Details("redis:7", Env("REDIS_PASSWORD=p@ss;w'rd$")), "cache");
        Assert.Equal("redis-cli -h cache -p 6379 -a 'p@ss;w'\\''rd$'", ConnectionDetails.Render(redis, ConnectionFormat.RedisCli, reveal: true));
        var mysql = Info(Details("mysql:8", Env("MYSQL_USER=o'neil", "MYSQL_PASSWORD=a\"b", "MYSQL_DATABASE=my db")), "db");
        Assert.Equal("mysql -h db -P 3306 -u 'o'\\''neil' -p 'my db'", ConnectionDetails.Render(mysql, ConnectionFormat.Mysql, reveal: true));
        // A " with no ' is single-quoted in .NET, as DbConnectionStringBuilder writes it; both are double-quoted, " doubled.
        Assert.Equal("Server=db;Port=3306;User ID=\"o'neil\";Password='a\"b';Database=\"my db\"", ConnectionDetails.Render(mysql, ConnectionFormat.DotNet, reveal: true));
        Assert.Equal("\"it's \"\"x\"\"\"", ConnectionDetails.Net("it's \"x\""));
    }

    [Fact]
    public void Has_no_details_for_what_isnt_a_database()
    {
        Assert.Null(ConnectionDetails.For(Details("nginx:stable"), "web"));
        Assert.Null(ConnectionDetails.DatabasePort(Details("nginx:stable")));
    }

    [Fact]
    public void Reads_the_details_from_an_inspect()
    {
        // The shared postgres as Docker describes it: no password in the fixture's environment.
        var info = ConnectionDetails.For(DockerFixtures.Inspect("postgres"), "postgres");

        Assert.NotNull(info);
        Assert.Equal((DatabaseEngine.Postgres, "postgres", 5432), (info.Engine, info.Host, info.Port));
    }
}
