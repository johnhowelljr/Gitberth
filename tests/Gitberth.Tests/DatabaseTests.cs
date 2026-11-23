using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Docker;
using Gitberth.Previews;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The pieces of an app's databases that need no server: the SCRAM verifier Postgres keeps instead of the password, the SQL
/// and its quoting, the names Gitberth chooses, and the connection URLs, a preview's included.
/// </summary>
public sealed class DatabasePartsTests
{
    // RFC 7677's exchange, for the user "user" with the password "pencil".
    private const string Salt = "W22ZaJ0SNY7soEsUEjb6gQ==";
    private const string AuthMessage = "n=user,r=rOprNGfwEbeRWgbNEkqO,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
        + "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";
    private const string ClientProof = "dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=";
    private const string ServerSignature = "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=";

    [Fact]
    public void The_verifier_is_rfc_7677s_for_its_password_and_salt()
    {
        var verifier = Scram.Verifier("pencil", Convert.FromBase64String(Salt));

        Assert.Equal("SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=", verifier);
        var (iterations, salt, storedKey, serverKey) = Scram.Parse(verifier)!.Value;
        Assert.Equal((4096, Salt), (iterations, Convert.ToBase64String(salt)));
        // The server signs the exchange with ServerKey, as the RFC's server did…
        Assert.Equal(ServerSignature, Convert.ToBase64String(HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(AuthMessage))));
        // …and the RFC's client proof checks out against StoredKey, as a server checks a sign-in.
        var signature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(AuthMessage));
        var clientKey = Convert.FromBase64String(ClientProof).Zip(signature, (a, b) => (byte)(a ^ b)).ToArray();
        Assert.Equal(storedKey, SHA256.HashData(clientKey));
    }

    [Fact]
    public void Each_verifier_has_a_salt_of_its_own_and_never_holds_the_password()
    {
        var one = Scram.Verifier("correct horse battery");
        var two = Scram.Verifier("correct horse battery");
        Assert.NotEqual(one, two);
        Assert.DoesNotContain("correct horse battery", one, StringComparison.Ordinal);
        Assert.Equal(16, Scram.Parse(one)!.Value.Salt.Length);
        Assert.Null(Scram.Parse("md5abc"));
    }

    [Fact]
    public void A_password_is_checked_against_the_verifier_or_md5_hash_postgres_keeps()
    {
        var verifier = Scram.Verifier("pencil", Convert.FromBase64String(Salt));
        Assert.True(Scram.Checks("pencil", "user", verifier));
        Assert.False(Scram.Checks("pencil2", "user", verifier));
        Assert.False(Scram.Checks("", "user", verifier));
        // An md5 one is "md5" and md5(password + user).
        var md5 = "md5" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("pencil" + "kanban")));
        Assert.True(Scram.Checks("pencil", "kanban", md5));
        Assert.False(Scram.Checks("pencil", "other", md5));
        // No password, or one it can't read, never lets anyone through.
        Assert.False(Scram.Checks("pencil", "user", ""));
        Assert.False(Scram.Checks("pencil", "user", "pencil"));
        Assert.Equal(("SCRAM-SHA-256$4096:abc$def:ghi", true), PostgresSql.ParseCopyAccess("SCRAM-SHA-256$4096:abc$def:ghi|t\n"));
        Assert.Equal(("", false), PostgresSql.ParseCopyAccess("|f"));
        Assert.Null(PostgresSql.ParseCopyAccess(""));
        Assert.Contains("r.rolname = 'o''brien' AND d.datname = 'shop'", PostgresSql.CopyAccess("o'brien", "shop"));
    }

    [Fact]
    public void Names_and_values_are_quoted_so_nothing_in_them_is_read_as_sql()
    {
        Assert.Equal("\"shop\"", PostgresSql.Ident("shop"));
        Assert.Equal("\"a\"\"; DROP ROLE x; --\"", PostgresSql.Ident("a\"; DROP ROLE x; --"));
        Assert.Equal("'it''s'", PostgresSql.Literal("it's"));
        Assert.Equal("'back\\slash'", PostgresSql.Literal("back\\slash"));
        Assert.Throws<ArgumentException>(() => PostgresSql.Ident(""));
        Assert.Throws<ArgumentException>(() => PostgresSql.Ident(new string('a', 64)));
        Assert.Throws<ArgumentException>(() => PostgresSql.Ident("a\0b"));
        Assert.Throws<ArgumentException>(() => PostgresSql.Literal("a\0b"));
    }

    [Fact]
    public void A_database_is_made_with_a_role_of_its_own_that_alone_may_connect()
    {
        var verifier = Scram.Verifier("s3cret-Password", Convert.FromBase64String(Salt));
        var sql = PostgresSql.Create("shop", "shop", verifier);

        Assert.Equal($"CREATE ROLE \"shop\" LOGIN PASSWORD '{verifier}';\nCREATE DATABASE \"shop\" OWNER \"shop\";\nREVOKE CONNECT, TEMPORARY ON DATABASE \"shop\" FROM PUBLIC;\n", sql);
        Assert.DoesNotContain("s3cret-Password", sql, StringComparison.Ordinal);
        // A password goes as its verifier only.
        Assert.Throws<ArgumentException>(() => PostgresSql.Create("shop", "shop", "plain"));
    }

    [Fact]
    public void A_drop_forces_its_sessions_out_and_takes_the_role_with_it()
    {
        Assert.Equal("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'shop_pr_1' AND pid <> pg_backend_pid();\n"
            + "DROP DATABASE IF EXISTS \"shop_pr_1\" WITH (FORCE);\nDROP ROLE IF EXISTS \"shop_pr_1\";\n", PostgresSql.Drop("shop_pr_1", "shop_pr_1", 180001));
        // Before 13 there's no FORCE: its sessions are ended first, as above.
        Assert.EndsWith("DROP DATABASE IF EXISTS \"old\";\n", PostgresSql.Drop("old", null, 120015));
        Assert.Equal("SELECT datname || ':' || pg_database_size(datname) FROM pg_database WHERE datname IN ('a', 'b''c');", PostgresSql.Sizes(["a", "b'c"]));
    }

    [Fact]
    public void What_psql_answers_is_read_back()
    {
        var (databases, roles) = PostgresSql.ParseNames("d:postgres\nd:shop\r\nr:postgres\nr:shop\n");
        Assert.Equal(["postgres", "shop"], databases.Order());
        Assert.Equal(["postgres", "shop"], roles.Order());
        Assert.Equal(new Dictionary<string, long> { ["shop"] = 7861015, ["my:db"] = 12 }, PostgresSql.ParseSizes("shop:7861015\nmy:db:12\nnonsense\n"));
        Assert.Equal((180001, true), PostgresSql.ParseProbe("180001|t\n"));
        Assert.Equal((160004, false), PostgresSql.ParseProbe("160004|f"));
        Assert.Null(PostgresSql.ParseProbe("psql: error"));
        Assert.Equal(("18.1", "9.6.24"), (PostgresSql.VersionText(180001), PostgresSql.VersionText(90624)));
    }

    [Fact]
    public void An_apps_database_is_named_after_its_slug_and_never_after_one_the_server_has()
    {
        Assert.Equal("shop", DatabaseNames.ForApp("shop", new HashSet<string>()));
        Assert.Equal("my_shop", DatabaseNames.ForApp("my-shop", new HashSet<string>()));
        // A database or a role by the name, in any case, is someone else's: the next number is taken.
        Assert.Equal("shop_3", DatabaseNames.ForApp("shop", new HashSet<string> { "Shop", "shop_2" }));
        Assert.Equal("app", DatabaseNames.ForApp("---", new HashSet<string>()));
        // Never past Postgres's 63 characters, the number included.
        var slug = new string('a', 40) + "-" + new string('b', 30);
        var name = DatabaseNames.ForApp(slug, new HashSet<string> { DatabaseNames.Shorten(DatabaseNames.Identifier(slug)) });
        Assert.True(name.Length <= DatabaseNames.MaxLength, name);
        Assert.EndsWith("_2", name);
    }

    [Fact]
    public void A_previews_database_is_productions_and_what_the_preview_is_of()
    {
        Assert.Equal("shop_pr_12", DatabaseNames.ForPreview("shop", "shop-pr-12", "shop", new HashSet<string>()));
        Assert.Equal("kanban_feature_login", DatabaseNames.ForPreview("kanban", "kanban-feature-login", "kanban", new HashSet<string>()));
        Assert.Equal("shop_pr_12_2", DatabaseNames.ForPreview("shop", "shop-pr-12", "shop", new HashSet<string> { "shop_pr_12" }));
        // A salted slug that doesn't start with its parent's is used whole.
        Assert.Equal("shop_x_3fa9", DatabaseNames.ForPreview("shop", "x-3fa9", "shop", new HashSet<string>()));
        var longest = DatabaseNames.ForPreview(new string('p', 63), "shop-pr-1", "shop", new HashSet<string>());
        Assert.Equal(DatabaseNames.MaxLength, longest.Length);
        Assert.Matches("^p+_[0-9a-f]{6}$", longest);
    }

    [Fact]
    public void A_redis_answers_to_a_name_no_app_has()
    {
        Assert.Equal("shop-redis", DatabaseNames.RedisAlias("shop", _ => false));
        Assert.Equal("shop-redis-3", DatabaseNames.RedisAlias("shop", n => n is "shop-redis" or "shop-redis-2"));
        Assert.Equal(("gb-shop_redis", "gb-shop_redis-data"), (DatabaseNames.RedisContainer("gb-shop"), DatabaseNames.RedisVolume("gb-shop")));
        Assert.True(DatabaseNames.IsMember(new Dictionary<string, string> { ["gitberth.app"] = "shop", ["gitberth.database"] = "redis" }));
        Assert.False(DatabaseNames.IsMember(new Dictionary<string, string> { ["gitberth.app"] = "shop" }));
    }

    [Fact]
    public void A_connection_url_escapes_each_part()
    {
        Assert.Equal("postgres://shop:p%40ss%2Fw0rd@postgres:5432/shop", DatabaseUrls.Postgres("shop", "p@ss/w0rd", "postgres", 5432, "shop"));
        Assert.Equal("redis://:abc@shop-redis:6379", DatabaseUrls.Redis("abc", "shop-redis", 6379));
        Assert.Equal("postgres://u:••••@db:5432/x?sslmode=disable", DatabaseUrls.Masked("postgres://u:secret@db:5432/x?sslmode=disable"));
    }

    [Fact]
    public void A_postgres_url_is_read_into_its_parts()
    {
        var url = DatabaseUrls.ParsePostgres("postgresql://kanban:s%40cret@postgres:5433/kanban?schema=public&sslmode=disable")!;
        Assert.Equal(new PostgresUrl("postgresql", "kanban", "s@cret", "postgres", 5433, "kanban", "?schema=public&sslmode=disable"), url);
        Assert.Equal(new PostgresUrl("postgres", null, null, "db", null, "app", ""), DatabaseUrls.ParsePostgres("postgres://db/app"));
        Assert.Null(DatabaseUrls.ParsePostgres("Host=db;Database=app"));
        Assert.Null(DatabaseUrls.ParsePostgres("mysql://u:p@db/app"));
        Assert.Null(DatabaseUrls.ParsePostgres(null));
    }

    [Fact]
    public void A_previews_url_is_productions_with_its_own_role_password_and_database()
    {
        var production = DatabaseUrls.ParsePostgres("postgresql://kanban:prod@postgres:5432/kanban?schema=public&connection_limit=5#x")!;
        Assert.Equal("postgresql://kanban_pr_3:n3w@postgres:5432/kanban_pr_3?schema=public&connection_limit=5#x",
            DatabaseUrls.ForPreview(production, "kanban_pr_3", "n3w", "kanban_pr_3"));
        // No port stays no port.
        Assert.Equal("postgres://a:b@db/a", DatabaseUrls.ForPreview(DatabaseUrls.ParsePostgres("postgres://x:y@db/x")!, "a", "b", "a"));
    }

    [Fact]
    public void A_database_gitberth_makes_for_previews_takes_productions_place()
    {
        (string, string, EnvScope)[] own = [("DATABASE_URL", "postgres://prod@postgres/shop", EnvScope.All), ("REDIS_URL", "redis://prod", EnvScope.All)];
        (string, string)[] github = [("SHADOW_DATABASE_URL", "postgres://prod@postgres/shadow")];

        Assert.Equal(["DATABASE_URL", "REDIS_URL", "SHADOW_DATABASE_URL"], PreviewDatabases.Find(own, github).Select(s => s.Name));
        // What Gitberth provides for the preview itself is no longer production's.
        Assert.Equal(["REDIS_URL"], PreviewDatabases.Find(own, github, ["DATABASE_URL", "shadow_database_url"]).Select(s => s.Name));
    }

    [Fact]
    public void A_redis_is_started_with_its_password_named_but_never_on_the_command_line()
    {
        var args = DatabaseService.RedisRunArgs("gb-shop_redis", "gb-shop_redis-data", "shop-redis", "shop", "gitberth", ["max-size=10m"]);

        Assert.Equal(["run", "-d", "--name", "gb-shop_redis"], args.Take(4));
        Assert.Contains("REDIS_PASSWORD", args);
        Assert.DoesNotContain(args, a => a.StartsWith("REDIS_PASSWORD=", StringComparison.Ordinal));
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal(("gitberth", "shop-redis", "gb-shop_redis-data:/data", "unless-stopped"), (After("--network"), After("--network-alias"), After("-v"), After("--restart")));
        Assert.Equal(["gitberth.managed=true", "gitberth.app=shop", "gitberth.database=redis"], args.Select((a, i) => (a, i)).Where(x => x.a == "--label").Select(x => args[x.i + 1]));
        Assert.Equal(DatabaseService.RedisImage, args[^3]);
        Assert.Contains("--appendonly yes --requirepass \"$REDIS_PASSWORD\"", args[^1], StringComparison.Ordinal);
        // An app's Redis is its app's: hidden among its containers, never a deploy's or an app of its own.
        var labels = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "shop", ["gitberth.database"] = "redis" };
        var redis = new ContainerSummary("gb-shop_redis", "running", "Up", DatabaseService.RedisImage, "", labels);
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(redis, new Dictionary<string, NavApp>(), null));
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(redis, new Dictionary<string, NavApp>(), null, new HashSet<string> { "gb-shop_redis" }));
        // Once no app keeps it (its app forgotten, its row lost), it's a database container like any other, never hidden.
        Assert.Equal(ContainerRole.Database, ContainerService.RoleOf(redis, new Dictionary<string, NavApp> { ["shop"] = new NavApp(1, "Shop", "shop") }, null, new HashSet<string>()));
    }

    [Fact]
    public async Task Exec_pipes_its_input_and_passes_secrets_through_the_clis_environment()
    {
        (IReadOnlyList<string> Args, ProcessOptions? Options)? seen = null;
        var docker = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, options, _) =>
            {
                seen = (args, options);
                return Task.FromResult(new ProcessResult(0, "1\n", ""));
            },
        };

        var r = await docker.ExecAsync("postgres", ["psql", "-U", "postgres"], "SELECT 1;", new Dictionary<string, string> { ["PGPASSWORD"] = "hunter22" });

        Assert.True(r.Ok);
        Assert.Equal(["exec", "-i", "-e", "PGPASSWORD", "postgres", "psql", "-U", "postgres"], seen!.Value.Args);
        Assert.Equal("SELECT 1;", seen.Value.Options!.Stdin);
        Assert.Equal("hunter22", seen.Value.Options.Environment!["PGPASSWORD"]);
        Assert.DoesNotContain(seen.Value.Args, a => a.Contains("hunter22", StringComparison.Ordinal));
        // Without input it's not interactive; an exec changes nothing Docker reports, so it expects no events.
        Assert.Equal(["exec", "redis", "redis-cli", "ping"], DockerCli.ExecArgs("redis", ["redis-cli", "ping"], input: false, null));
        Assert.Empty(Audit.ExpectedChanges.CliExpectations(seen.Value.Args));
    }

    [Fact]
    public async Task A_process_reads_what_its_standard_input_is_given()
    {
        if (!OperatingSystem.IsLinux()) return;
        var r = await ProcessRunner.RunAsync("cat", [], new ProcessOptions { Stdin = "SELECT 'é';\nSELECT 2;" });
        Assert.Equal("SELECT 'é';\nSELECT 2;\n", r.StdOut);
        // A command that ends without reading its input isn't a failure of the write.
        var early = await ProcessRunner.RunAsync("true", [], new ProcessOptions { Stdin = new string('x', 1 << 20) });
        Assert.True(early.Ok);
    }
}

/// <summary>
/// An app's databases on a Postgres server of this host, against a stand-in for the server's container (its psql answers
/// the SQL it's given on standard input): what's made, how it's named, what the app gets, a preview's own, and what goes
/// when it's removed.
/// </summary>
public sealed class DatabaseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-db-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Factory _db;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly PostgresHost _host = new();
    private readonly GitberthOptions _options;
    private readonly DatabaseServers _servers;
    private readonly DatabaseService _service;
    private readonly Audit.AuditLog _audit = new();
    private readonly int _shopId;

    private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    public DatabaseServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new GitberthOptions { DataDir = _dir };
        _db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options);
        using (var db = _db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var shop = new AppDefinition { Name = "Shop", Slug = "shop", Owner = "acme", Repo = "shop" };
            db.Apps.Add(shop);
            db.SaveChanges();
            _shopId = shop.Id;
        }
        var docker = new DockerCli(_options) { Runner = _host.RunAsync };
        var settings = new SettingsService(_db, _protector);
        _servers = new DatabaseServers(docker, settings, _options, NullLogger<DatabaseServers>.Instance, audit: _audit);
        _service = new DatabaseService(_db, docker, _protector, _servers, _options, NullLogger<DatabaseService>.Instance, audit: _audit);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<int> AddAppAsync(AppDefinition app)
    {
        await using var db = _db.CreateDbContext();
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private async Task<List<ManagedDatabase>> RowsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.ManagedDatabases.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    private sealed class Log : IDatabaseLog
    {
        public List<string> Lines { get; } = [];
        public List<string> Masked { get; } = [];
        public void Step(string text) => Lines.Add("==> " + text);
        public void Line(string text) => Lines.Add(text);
        public void Mask(IEnumerable<string> secrets) => Masked.AddRange(secrets);
    }

    [Fact]
    public async Task The_server_gitberth_can_use_is_the_default_by_itself()
    {
        var servers = await _servers.ListAsync();

        var postgres = Assert.Single(servers);
        Assert.Equal(("postgres", (string?)"postgres", "postgres", "18.1", (string?)null), (postgres.Container, postgres.Alias, postgres.User, postgres.VersionText, postgres.Problem));
        Assert.True(postgres.Usable);
        Assert.Equal("postgres", (await _servers.DefaultAsync(servers))?.Container);
        // Its sign-in was checked through psql, with its password in psql's environment only.
        var probe = _host.Execs.First();
        Assert.Equal(PostgresSql.Probe, probe.Stdin);
        Assert.Equal("serverpass", probe.Environment!["PGPASSWORD"]);
        Assert.DoesNotContain(_host.Commands, c => c.Any(a => a.Contains("serverpass", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_server_still_starting_is_checked_again_within_seconds_and_a_good_check_is_kept()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var servers = new DatabaseServers(new DockerCli(_options) { Runner = _host.RunAsync }, new SettingsService(_db, _protector), _options,
            NullLogger<DatabaseServers>.Instance) { Now = () => now };
        _host.Starting = true;
        Assert.False(Assert.Single(await servers.ListAsync()).Usable);

        // Ready seconds later: the refused check isn't kept for half a minute, so the server isn't "No server yet" meanwhile.
        _host.Starting = false;
        now = now.AddSeconds(6);
        Assert.True(Assert.Single(await servers.ListAsync()).Usable);

        // A check that passed is kept: no psql for the next 30 seconds.
        var probes = _host.Execs.Count(e => e.Stdin == PostgresSql.Probe);
        now = now.AddSeconds(20);
        Assert.True(Assert.Single(await servers.ListAsync()).Usable);
        Assert.Equal(probes, _host.Execs.Count(e => e.Stdin == PostgresSql.Probe));
    }

    [Fact]
    public async Task A_server_gitberth_cant_use_says_why()
    {
        _host.Containers.Add(PostgresHost.Server("analytics", network: "bridge"));
        _host.Containers.Add(PostgresHost.Server("stopped", running: false));
        _host.Containers.Add(PostgresHost.Server("readonly", user: "app"));

        var servers = (await _servers.ListAsync()).ToDictionary(s => s.Container);

        Assert.Equal("It isn't on Gitberth's network (gitberth), so apps can't reach it by name.", servers["analytics"].Problem);
        Assert.Equal("It isn't running.", servers["stopped"].Problem);
        Assert.Equal("app isn't a superuser there, so Gitberth can't make databases and roles on it.", servers["readonly"].Problem);
        // Only one is usable, so it's the default without being chosen.
        Assert.Equal("postgres", (await _servers.DefaultAsync(servers.Values.ToList()))?.Container);
    }

    [Fact]
    public async Task An_app_gets_a_database_and_a_role_of_its_own_and_its_password_reaches_the_server_only_as_a_verifier()
    {
        var done = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));

        Assert.True(done.Ok, done.Message);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal((_shopId, DatabaseKind.Postgres, "postgres", "shop", (string?)"shop", "DATABASE_URL", true), (row.AppId, row.Kind, row.Server, row.Name, row.Role, row.Variable, row.Created));
        var password = _protector.Unprotect(row.ProtectedPassword);
        var create = Assert.Single(_host.Execs, e => e.Stdin?.StartsWith("CREATE ROLE", StringComparison.Ordinal) == true).Stdin!;
        var verifier = Regex.Match(create, "PASSWORD '([^']+)'").Groups[1].Value;
        Assert.Equal(PostgresSql.Create("shop", "shop", verifier), create);
        // The verifier is the password's, and the password itself went nowhere: not in SQL, not on a command line.
        Assert.Equal(verifier, Scram.Verifier(password, Scram.Parse(verifier)!.Value.Salt));
        Assert.DoesNotContain(_host.Execs, e => e.Stdin?.Contains(password, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(_host.Commands, c => c.Any(a => a.Contains(password, StringComparison.Ordinal)));

        // What the app's deploys set: the URL on the server's name on the shared network.
        var value = Assert.Single(await _service.ValuesAsync(_shopId));
        Assert.Equal(("DATABASE_URL", $"postgres://shop:{password}@postgres:5432/shop", "the Postgres database shop on postgres"), (value.Key, value.Value, value.From));
        // A deploy masks the URL and the password on its own.
        Assert.Equal([value.Value, password], value.Secrets);
        Assert.Equal(value.Value, await _service.ConnectionStringAsync(_shopId, row.Id));
        var listed = Assert.Single(await _service.ListAsync(_shopId));
        Assert.Equal(("postgres", (long?)8192L, (string?)null), (listed.Host, listed.SizeBytes, listed.Problem));
        var entry = Assert.Single(_audit.Entries, e => e.Action == "app.database.added");
        Assert.Equal("Added the Postgres database shop on postgres to Shop: its connection string is in DATABASE_URL.", entry.Summary);
        Assert.DoesNotContain(password, JsonSerializer.Serialize(_audit.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Its_names_never_clash_with_what_the_server_has_or_gitberth_keeps()
    {
        _host.Databases.Add("shop");
        _host.Roles.Add("shop_2");

        var first = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        var second = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "ANALYTICS_DATABASE_URL"));

        Assert.True(first.Ok && second.Ok, first.Message + second.Message);
        Assert.Equal(["shop_3", "shop_4"], (await RowsAsync()).Select(r => r.Name));
        // Its own variables stay apart.
        var again = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        Assert.False(again.Ok);
        Assert.Equal("DATABASE_URL is set from the Postgres database shop_3 on postgres already. Give this one another variable.", again.Message);
        Assert.False((await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "1BAD"))).Ok);
    }

    [Fact]
    public async Task A_create_that_fails_takes_back_only_what_it_made()
    {
        // psql says which statement failed only for a script it reads as a file: a plain pipe gives a bare "ERROR:" (the
        // stand-in answers as the real one does), and nothing could be taken back.
        Assert.Equal(["-f", "-"], PostgresSql.Psql("postgres", "postgres").TakeLast(2));
        // Someone makes a role called shop between Gitberth's look at the names and its create: the create stops at its
        // first statement, and their role stays.
        _host.MadeMeanwhile = "shop";
        var role = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        Assert.False(role.Ok);
        Assert.Equal("postgres refused the database shop: role \"shop\" already exists.", role.Message);
        Assert.Contains("shop", _host.Roles);
        Assert.DoesNotContain(_host.Execs, e => e.Stdin?.StartsWith("DROP", StringComparison.Ordinal) == true);

        // A database by the name instead: the role Gitberth made just before goes again, their database stays.
        _host.MadeMeanwhile = "shop_2";
        _host.MadeMeanwhileIsDatabase = true;
        var database = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        Assert.False(database.Ok);
        Assert.Contains("shop_2", _host.Databases);
        Assert.DoesNotContain("shop_2", _host.Roles);
        Assert.Equal(PostgresSql.DropRole("shop_2"), _host.Execs[^1].Stdin);
        Assert.Empty(await RowsAsync());
        Assert.Equal((1, 2, 3, (int?)null), (PostgresSql.FailedStatement("psql:<stdin>:1: ERROR:  x"), PostgresSql.FailedStatement("psql:<stdin>:2: ERROR:  x"),
            PostgresSql.FailedStatement("psql:<stdin>:3: ERROR:  x"), PostgresSql.FailedStatement("psql: error: connection to server failed")));
    }

    [Fact]
    public async Task Without_a_server_it_says_where_to_get_one()
    {
        _host.Containers.Clear();
        var done = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        Assert.Equal((false, DatabaseService.NoServer), (done.Ok, done.Message));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Keeping_the_data_forgets_it_deleting_it_drops_it_and_one_gitberth_didnt_make_is_never_dropped()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "OTHER_DATABASE_URL"));
        var (kept, dropped) = ((await RowsAsync())[0], (await RowsAsync())[1]);

        var keep = await _service.RemoveAsync(_shopId, kept.Id, deleteData: false);
        Assert.True(keep.Ok, keep.Message);
        Assert.Contains("shop", _host.Databases);
        Assert.DoesNotContain(_host.Execs, e => e.Stdin?.Contains("DROP DATABASE IF EXISTS \"shop\"", StringComparison.Ordinal) == true);

        var delete = await _service.RemoveAsync(_shopId, dropped.Id, deleteData: true);
        Assert.True(delete.Ok, delete.Message);
        Assert.DoesNotContain(dropped.Name, _host.Databases);
        Assert.DoesNotContain(dropped.Name, _host.Roles);
        Assert.Empty(await RowsAsync());

        await using (var db = _db.CreateDbContext())
        {
            db.ManagedDatabases.Add(new ManagedDatabase { AppId = _shopId, Kind = DatabaseKind.Postgres, Server = "postgres", Name = "legacy", Role = "legacy", Variable = "LEGACY_URL", Created = false });
            await db.SaveChangesAsync();
        }
        _host.Databases.Add("legacy");
        var legacy = (await RowsAsync()).Single();
        var refused = await _service.RemoveAsync(_shopId, legacy.Id, deleteData: true);
        Assert.False(refused.Ok);
        Assert.Contains("legacy", _host.Databases);
        Assert.Equal(["app.database.added", "app.database.added", "app.database.removed", "app.database.removed"], _audit.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task A_preview_gets_its_own_database_once_and_it_goes_with_the_preview()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        var parent = await AppAsync(_shopId);
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #12", Slug = "shop-pr-12", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var preview = await AppAsync(previewId);
        var log = new Log();

        var provided = await _service.ForPreviewAsync(preview, parent, new Dictionary<string, string>(), new HashSet<string>(), log);

        var value = Assert.Single(provided);
        var row = (await RowsAsync()).Single(r => r.AppId == previewId);
        var password = _protector.Unprotect(row.ProtectedPassword);
        Assert.Equal(("DATABASE_URL", $"postgres://shop_pr_12:{password}@postgres:5432/shop_pr_12"), (value.Key, value.Value));
        Assert.Equal(("shop_pr_12", (string?)"shop_pr_12", "postgres"), (row.Name, row.Role, row.Server));
        Assert.Contains("shop_pr_12", _host.Databases);
        Assert.Contains(password, log.Masked);
        Assert.Contains("==> Making the preview's own database shop_pr_12 on postgres, empty, for DATABASE_URL", log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(password, StringComparison.Ordinal));

        // Its next deploy gets the same one: nothing is made again.
        var creates = _host.Execs.Count(e => e.Stdin?.StartsWith("CREATE ROLE", StringComparison.Ordinal) == true);
        Assert.Equal(value, Assert.Single(await _service.ForPreviewAsync(preview, parent, new Dictionary<string, string>(), new HashSet<string>(), new Log())));
        Assert.Equal(creates, _host.Execs.Count(e => e.Stdin?.StartsWith("CREATE ROLE", StringComparison.Ordinal) == true));

        // Production's stays; the preview's goes with it, data and all.
        await _service.DeleteWithAppAsync(previewId, deleteData: false);
        Assert.DoesNotContain("shop_pr_12", _host.Databases);
        Assert.DoesNotContain("shop_pr_12", _host.Roles);
        Assert.Contains("shop", _host.Databases);
        Assert.Contains(_audit.Entries, e => e.Action == "preview.database.created" && e.Target == "Shop · PR #12");
        Assert.Contains(_audit.Entries, e => e.Action == "preview.database.removed" && e.Summary == "Deleted the Postgres database shop_pr_12 of the preview Shop · PR #12 with it.");
    }

    [Fact]
    public async Task A_preview_of_an_app_whose_own_url_names_a_server_here_gets_its_own_and_keeps_the_urls_query()
    {
        var parent = await AppAsync(_shopId);
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #3", Slug = "shop-pr-3", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var production = new Dictionary<string, string>
        {
            ["DATABASE_URL"] = "postgresql://kanban:prodpass@postgres:5432/kanban?schema=public",
            // Not a server here: refused as before (PreviewDatabases), never made.
            ["ANALYTICS_URL"] = "postgres://a:b@db.example.com/analytics",
            ["SECRET_KEY"] = "x",
        };

        var provided = await _service.ForPreviewAsync(await AppAsync(previewId), parent, production, new HashSet<string>(), new Log());

        var value = Assert.Single(provided);
        var password = _protector.Unprotect((await RowsAsync()).Single().ProtectedPassword);
        Assert.Equal($"postgresql://kanban_pr_3:{password}@postgres:5432/kanban_pr_3?schema=public", value.Value);
        // A name the owner gave a value for previews is theirs.
        var mine = await _service.ForPreviewAsync(await AppAsync(previewId), parent, production, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database_url" }, new Log());
        Assert.Empty(mine);
    }

    [Fact]
    public async Task A_copy_of_production_is_restored_into_the_previews_database_as_its_role()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _shopId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewDatabases, PreviewDatabaseMode.CopyOfProduction));
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #4", Slug = "shop-pr-4", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var log = new Log();

        await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), new Dictionary<string, string>(), new HashSet<string>(), log);

        var copy = Assert.Single(_host.Execs, e => e.Command[0] == "sh");
        Assert.Equal(PostgresSql.Copy("postgres", "shop", "shop_pr_4", "shop_pr_4"), copy.Command);
        Assert.Null(copy.Stdin);
        Assert.Contains("Copied production's data into shop_pr_4.", log.Lines);
        Assert.Contains(_audit.Entries, e => e.Action == "preview.database.created" && e.Summary.EndsWith("a copy of production's shop.", StringComparison.Ordinal));

        // A copy that fails leaves nothing behind, and the preview's deploy says why.
        _host.CopyFails = true;
        var otherId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #5", Slug = "shop-pr-5", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ForPreviewAsync(AppAsync(otherId).Result, AppAsync(_shopId).Result, new Dictionary<string, string>(), new HashSet<string>(), new Log()));
        Assert.StartsWith("Production's data couldn't be copied into the preview's database:", failed.Message);
        Assert.DoesNotContain("shop_pr_5", _host.Databases);
        Assert.DoesNotContain(await RowsAsync(), r => r.AppId == otherId);
    }

    private async Task CopyOfProductionAsync()
    {
        await using var db = _db.CreateDbContext();
        await db.Apps.Where(a => a.Id == _shopId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewDatabases, PreviewDatabaseMode.CopyOfProduction));
    }

    private async Task<List<string>> MarksAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("databases.copying.")).Select(s => s.Key).ToListAsync();
    }

    [Fact]
    public async Task A_deploy_cancelled_while_it_copies_production_leaves_no_database_role_or_row_behind()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        await CopyOfProductionAsync();
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #4", Slug = "shop-pr-4", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        using var cts = new CancellationTokenSource();
        // Its pull request closes while production's data is being copied: the preview's removal cancels its deploy.
        _host.OnCopy = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ForPreviewAsync(AppAsync(previewId).Result, AppAsync(_shopId).Result, new Dictionary<string, string>(), new HashSet<string>(), new Log(), cts.Token));

        Assert.DoesNotContain("shop_pr_4", _host.Databases);
        Assert.DoesNotContain("shop_pr_4", _host.Roles);
        Assert.DoesNotContain(await RowsAsync(), r => r.AppId == previewId);
        Assert.Empty(await MarksAsync());
        // Production's is untouched.
        Assert.Contains("shop", _host.Databases);
    }

    [Fact]
    public async Task A_copy_gitberth_stopped_before_it_finished_is_made_again_at_the_next_deploy()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        await CopyOfProductionAsync();
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #8", Slug = "shop-pr-8", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var first = Assert.Single(await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), new Dictionary<string, string>(), new HashSet<string>(), new Log()));
        var row = (await RowsAsync()).Single(r => r.AppId == previewId);
        Assert.Empty(await MarksAsync());
        // Gitberth was killed mid-copy: the row was kept, and so was the mark that its copy hadn't finished.
        await using (var db = _db.CreateDbContext())
        {
            db.Settings.Add(new Setting { Key = DatabaseService.CopyingKey(row.Id), Value = "x" });
            await db.SaveChangesAsync();
        }
        var log = new Log();

        var again = Assert.Single(await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), new Dictionary<string, string>(), new HashSet<string>(), log));

        Assert.Contains("The copy of production's data into shop_pr_8 didn't finish (Gitberth stopped meanwhile): it's made again.", log.Lines);
        Assert.Contains(_host.Execs, e => e.Stdin?.Contains("DROP DATABASE IF EXISTS \"shop_pr_8\"", StringComparison.Ordinal) == true);
        var remade = (await RowsAsync()).Single(r => r.AppId == previewId);
        Assert.NotEqual(row.Id, remade.Id);
        Assert.Equal(2, _host.Execs.Count(e => e.Command[0] == "sh"));
        Assert.NotEqual(first.Value, again.Value);
        Assert.Empty(await MarksAsync());
    }

    [Fact]
    public async Task A_preview_redis_that_doesnt_come_to_be_is_removed_with_its_volume_and_forgotten()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Redis, "REDIS_URL"));
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #7", Slug = "shop-pr-7", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        using var cts = new CancellationTokenSource();
        // It doesn't answer yet when the deploy is cancelled.
        _host.PingAnswers = false;
        _host.OnPing = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ForPreviewAsync(AppAsync(previewId).Result, AppAsync(_shopId).Result, new Dictionary<string, string>(), new HashSet<string>(), new Log(), cts.Token));

        var commands = _host.Commands.Select(c => string.Join(' ', c)).ToList();
        Assert.Contains(commands, c => c.StartsWith("run -d --name gb-shop-pr-7_redis", StringComparison.Ordinal));
        Assert.Contains("rm -f gb-shop-pr-7_redis", commands);
        Assert.Contains("volume rm gb-shop-pr-7_redis-data", commands);
        Assert.DoesNotContain(await RowsAsync(), r => r.AppId == previewId);
        // The app's own Redis stays.
        Assert.DoesNotContain("rm -f gb-shop_redis", commands);
    }

    [Fact]
    public async Task Variables_that_name_the_same_production_database_share_one_preview_database()
    {
        _host.Databases.Add("kanban");
        _host.Roles.Add("kanban");
        _host.Logins["kanban"] = ("prodpass", ["kanban"]);
        await CopyOfProductionAsync();
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #3", Slug = "shop-pr-3", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        // Prisma's: the pooled URL and the direct one, to the same database.
        var production = new Dictionary<string, string>
        {
            ["DATABASE_URL"] = "postgresql://kanban:prodpass@postgres:5432/kanban?schema=public&pgbouncer=true",
            ["DIRECT_URL"] = "postgresql://kanban:prodpass@postgres:5432/kanban?schema=public",
        };

        var provided = await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), production, new HashSet<string>(), new Log());

        var row = Assert.Single(await RowsAsync(), r => r.AppId == previewId);
        Assert.Equal("kanban_pr_3", row.Name);
        var password = _protector.Unprotect(row.ProtectedPassword);
        Assert.Equal([
                ("DATABASE_URL", $"postgresql://kanban_pr_3:{password}@postgres:5432/kanban_pr_3?schema=public&pgbouncer=true"),
                ("DIRECT_URL", $"postgresql://kanban_pr_3:{password}@postgres:5432/kanban_pr_3?schema=public"),
            ],
            provided.Select(p => (p.Key, p.Value)));
        Assert.All(provided, p => Assert.Equal(password, p.Password));
        // Made once and copied once.
        Assert.Single(_host.Execs, e => e.Stdin?.StartsWith("CREATE ROLE", StringComparison.Ordinal) == true);
        Assert.Single(_host.Execs, e => e.Command[0] == "sh");
        // Its next deploy finds it for both.
        Assert.Equal(provided, await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), production, new HashSet<string>(), new Log()));
        Assert.Single(_host.Execs, e => e.Stdin?.StartsWith("CREATE ROLE", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("prodpass", "other", true, null)]
    [InlineData("guessed", "other", false, "the URL's password doesn't sign in to postgres as kanban")]
    [InlineData("prodpass", "billing", false, "kanban doesn't own billing and wasn't granted CONNECT on it")]
    [InlineData("", "other", false, "its URL has no user and password to check that they may read it")]
    public async Task A_copy_of_production_named_by_a_url_needs_its_own_role_and_password_to_read_it(string password, string database, bool copied, string? why)
    {
        _host.Databases.UnionWith(["other", "billing"]);
        _host.Roles.Add("kanban");
        _host.Logins["kanban"] = ("prodpass", ["other"]);
        await CopyOfProductionAsync();
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #9", Slug = "shop-pr-9", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });
        var url = password.Length > 0 ? $"postgres://kanban:{password}@postgres/{database}" : $"postgres://postgres/{database}";
        var log = new Log();

        var value = Assert.Single(await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), new Dictionary<string, string> { ["DATABASE_URL"] = url },
            new HashSet<string>(), log));

        Assert.Equal(copied, _host.Execs.Any(e => e.Command[0] == "sh"));
        Assert.Contains($"{database}_pr_9", value.Value);
        var entry = Assert.Single(_audit.Entries, e => e.Action == "preview.database.created");
        if (copied)
        {
            Assert.EndsWith($"a copy of production's {database}.", entry.Summary);
        }
        else
        {
            Assert.Contains($"Not a copy of production's {database}: {why}. The preview's database starts empty.", log.Lines);
            Assert.EndsWith("empty.", entry.Summary);
            Assert.Contains(entry.Changes!, c => c.To == "empty: production's may not be copied");
        }
        // The check never puts a password in SQL.
        Assert.DoesNotContain(_host.Execs, e => e.Stdin?.Contains("prodpass", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Previews_that_get_no_database_of_their_own_get_none()
    {
        await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Postgres, "DATABASE_URL"));
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _shopId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviewDatabases, PreviewDatabaseMode.Off));
        var previewId = await AddAppAsync(new AppDefinition { Name = "Shop · PR #6", Slug = "shop-pr-6", ParentAppId = _shopId, Owner = "acme", Repo = "shop" });

        Assert.Empty(await _service.ForPreviewAsync(await AppAsync(previewId), await AppAsync(_shopId), new Dictionary<string, string>(), new HashSet<string>(), new Log()));
        Assert.Empty(await _service.PreviewVariablesAsync(await AppAsync(_shopId), PreviewDatabaseMode.Off, new Dictionary<string, string>(), new HashSet<string>()));
        Assert.Equal(["DATABASE_URL"], (await _service.PreviewVariablesAsync(await AppAsync(_shopId), PreviewDatabaseMode.Empty, new Dictionary<string, string>(), new HashSet<string>()))
            .Select(v => v.Variable));
    }

    [Fact]
    public async Task An_apps_redis_runs_in_a_container_of_its_own_and_goes_with_its_data_when_asked()
    {
        var done = await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Redis, "REDIS_URL"));

        Assert.True(done.Ok, done.Message);
        var row = Assert.Single(await RowsAsync());
        var password = _protector.Unprotect(row.ProtectedPassword);
        Assert.Equal((DatabaseKind.Redis, "gb-shop_redis", "shop-redis", (string?)null), (row.Kind, row.Server, row.Name, row.Role));
        var run = Assert.Single(_host.Runs);
        Assert.Equal(password, run.Environment!["REDIS_PASSWORD"]);
        Assert.DoesNotContain(run.Args, a => a.Contains(password, StringComparison.Ordinal));
        Assert.Equal($"redis://:{password}@shop-redis:6379", Assert.Single(await _service.ValuesAsync(_shopId)).Value);
        // One Redis an app.
        Assert.False((await _service.AddAsync("gb-shop", new DatabaseRequest(DatabaseKind.Redis, "CACHE_URL"))).Ok);

        var removed = await _service.RemoveAsync(_shopId, row.Id, deleteData: true);
        Assert.True(removed.Ok, removed.Message);
        Assert.Contains(["rm", "-f", "gb-shop_redis"], _host.Commands.Select(c => c.ToList()), new ListComparer());
        Assert.Contains(["volume", "rm", "gb-shop_redis-data"], _host.Commands.Select(c => c.ToList()), new ListComparer());
    }

    private sealed class ListComparer : IEqualityComparer<List<string>>
    {
        public bool Equals(List<string>? x, List<string>? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(List<string> obj) => obj.Count;
    }

    private async Task<AppDefinition> AppAsync(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Apps.AsNoTracking().Include(a => a.EnvVars).FirstAsync(a => a.Id == id);
    }
}

/// <summary>
/// A Docker host with Postgres servers for <see cref="DatabaseServiceTests"/>: docker ps and inspect for them, psql in them
/// (it answers the probe, lists names, makes, drops and sizes databases from the SQL on its input), the copy's shell, and a
/// Redis container's run, ping and removal.
/// </summary>
internal sealed class PostgresHost
{
    public sealed record Exec(string Container, IReadOnlyList<string> Command, string? Stdin, IReadOnlyDictionary<string, string>? Environment);

    public sealed record Run(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Environment);

    public sealed record ServerContainer(string Name, string Image, bool Running, string Network, string User, string Password, Dictionary<string, string> Labels);

    public static ServerContainer Server(string name, string network = "gitberth", bool running = true, string user = "postgres") =>
        new(name, "postgres:18", running, network, user, "serverpass", []);

    public List<ServerContainer> Containers { get; } = [Server("postgres")];
    public HashSet<string> Databases { get; } = ["postgres", "template0", "template1"];
    public HashSet<string> Roles { get; } = ["postgres"];
    public List<IReadOnlyList<string>> Commands { get; } = [];
    public List<Exec> Execs { get; } = [];
    public List<Run> Runs { get; } = [];
    public bool CopyFails { get; set; }
    /// <summary>A role (or with <see cref="MadeMeanwhileIsDatabase"/>, a database) someone else makes just before Gitberth's create of it.</summary>
    public string? MadeMeanwhile { get; set; }
    public bool MadeMeanwhileIsDatabase { get; set; }
    /// <summary>Called as a copy of production's data starts (a test cancels its deploy there).</summary>
    public Action? OnCopy { get; set; }
    /// <summary>Called on each redis-cli ping; with <see cref="PingAnswers"/> off, the Redis never answers.</summary>
    public Action? OnPing { get; set; }
    public bool PingAnswers { get; set; } = true;
    /// <summary>The servers still start: psql's sign-in is refused, as in a server's first seconds.</summary>
    public bool Starting { get; set; }
    /// <summary>Roles that sign in with a password, and the databases each owns (pg_authid and pg_database, for a copy's check).</summary>
    public Dictionary<string, (string Password, HashSet<string> Owns)> Logins { get; } = new(StringComparer.Ordinal);

    private readonly HashSet<string> _redis = new(StringComparer.Ordinal);

    public Task<ProcessResult> RunAsync(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
    {
        lock (this)
        {
            Commands.Add(a);
            return Task.FromResult(Answer(a, options, ct));
        }
    }

    // psql says the line it stopped at only for a script it reads as a file (-f -); from a plain pipe, a bare "ERROR:".
    private static string At(IReadOnlyList<string> command, int line) =>
        command.SkipWhile(x => x != "-f").Skip(1).FirstOrDefault() == "-" ? $"psql:<stdin>:{line}: " : "";

    private static ProcessResult Ok(string output = "") => new(0, output, "");

    private static ProcessResult Fail(string error, int code = 1) => new(code, "", error);

    private ProcessResult Answer(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
    {
        switch (a[0])
        {
            case "ps":
                return Ok(string.Join('\n', Containers.Select(c => JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["Names"] = c.Name, ["State"] = c.Running ? "running" : "exited", ["Status"] = c.Running ? "Up 1 hour" : "Exited (0)", ["Image"] = c.Image,
                    ["Ports"] = "5432/tcp", ["Labels"] = "", ["Command"] = "docker-entrypoint.sh postgres", ["ID"] = "id-" + c.Name,
                }))));
            case "container" when a[1] == "inspect" && a.Contains("--format"):
                return Containers.Any(c => c.Name == a[^1]) || _redis.Contains(a[^1]) ? Ok("id") : Fail("Error: No such container: " + a[^1]);
            case "container" when a[1] == "inspect":
                var found = Containers.Where(c => a.Skip(2).Contains(c.Name)).Select(Inspect).ToList();
                return found.Count == 0 ? Fail("Error: No such container") : Ok("[" + string.Join(",", found) + "]");
            case "exec":
                return ExecIn(a, options, ct);
            case "network":
                return Ok("gitberth");
            case "info":
                return Ok("json-file\n");
            case "run":
                Runs.Add(new Run(a, options?.Environment));
                _redis.Add(a[a.ToList().IndexOf("--name") + 1]);
                return Ok("id");
            case "rm":
                _redis.Remove(a[^1]);
                return Ok();
            case "volume":
                return Ok();
            case "logs":
                return Ok();
            default:
                return Fail("unknown command " + string.Join(' ', a));
        }
    }

    private static string Inspect(ServerContainer c) => new System.Text.Json.Nodes.JsonObject
    {
        ["Id"] = "id-" + c.Name,
        ["Name"] = "/" + c.Name,
        ["Image"] = "sha256:1",
        ["Config"] = new System.Text.Json.Nodes.JsonObject
        {
            ["Image"] = c.Image,
            ["Env"] = new System.Text.Json.Nodes.JsonArray($"POSTGRES_USER={c.User}", $"POSTGRES_PASSWORD={c.Password}", "PGDATA=/var/lib/postgresql/18/docker"),
            ["Labels"] = new System.Text.Json.Nodes.JsonObject(),
            ["ExposedPorts"] = new System.Text.Json.Nodes.JsonObject { ["5432/tcp"] = new System.Text.Json.Nodes.JsonObject() },
        },
        ["State"] = new System.Text.Json.Nodes.JsonObject { ["Status"] = c.Running ? "running" : "exited", ["Running"] = c.Running, ["ExitCode"] = 0 },
        ["HostConfig"] = new System.Text.Json.Nodes.JsonObject { ["NetworkMode"] = c.Network, ["RestartPolicy"] = new System.Text.Json.Nodes.JsonObject { ["Name"] = "unless-stopped" } },
        // Its name, and a short ID as Docker adds one, on its network.
        ["NetworkSettings"] = new System.Text.Json.Nodes.JsonObject
        {
            ["Networks"] = new System.Text.Json.Nodes.JsonObject
            {
                [c.Network] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Aliases"] = new System.Text.Json.Nodes.JsonArray(c.Name, c.Name.Length.ToString("x12", System.Globalization.CultureInfo.InvariantCulture)),
                    ["IPAddress"] = "172.18.0.5",
                },
            },
        },
    }.ToJsonString();

    private ProcessResult ExecIn(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
    {
        var i = 1;
        while (a[i].StartsWith('-')) i += a[i] == "-e" ? 2 : 1;
        var container = a[i];
        var command = a.Skip(i + 1).ToList();
        Execs.Add(new Exec(container, command, options?.Stdin, options?.Environment));
        if (command[0] == "redis-cli")
        {
            if (!command.Contains("ping")) return Ok("# Memory\nused_memory:1048576\nused_memory_human:1.00M\n");
            OnPing?.Invoke();
            return PingAnswers ? Ok("PONG\n") : Fail("Could not connect to Redis at 127.0.0.1:6379: Connection refused");
        }
        if (command[0] == "sh")
        {
            OnCopy?.Invoke();
            // The docker CLI is killed when its deploy is cancelled (the copy inside the container isn't).
            ct.ThrowIfCancellationRequested();
            return CopyFails ? Fail("pg_dump: error: connection to server failed") : Ok();
        }
        var server = Containers.First(c => c.Name == container);
        var sql = options?.Stdin ?? "";
        if (sql == PostgresSql.Probe && Starting)
            return Fail("psql: error: connection to server on socket \"/var/run/postgresql/.s.PGSQL.5432\" failed: FATAL:  the database system is starting up", 2);
        if (sql == PostgresSql.Probe) return Ok($"180001|{(server.User == "postgres" ? "t" : "f")}\n");
        if (sql == PostgresSql.Names) return Ok(string.Concat(Databases.Select(d => $"d:{d}\n")) + string.Concat(Roles.Select(r => $"r:{r}\n")));
        if (sql.StartsWith("CREATE ROLE", StringComparison.Ordinal))
        {
            var role = Regex.Match(sql, "CREATE ROLE \"([^\"]+)\"").Groups[1].Value;
            var database = Regex.Match(sql, "CREATE DATABASE \"([^\"]+)\"").Groups[1].Value;
            // Someone else made one by that name after Gitberth looked.
            if (MadeMeanwhile is { } other && (other == role || other == database)) (MadeMeanwhileIsDatabase ? Databases : Roles).Add(other);
            if (Roles.Contains(role)) return Fail($"{At(command, 1)}ERROR:  role \"{role}\" already exists", 3);
            Roles.Add(role);
            if (Databases.Contains(database)) return Fail($"{At(command, 2)}ERROR:  database \"{database}\" already exists", 3);
            Databases.Add(database);
            return Ok();
        }
        if (sql.StartsWith("SELECT CASE WHEN r.rolcanlogin", StringComparison.Ordinal))
        {
            var asked = Regex.Match(sql, "r.rolname = '([^']*)' AND d.datname = '([^']*)'");
            var (role, database) = (asked.Groups[1].Value, asked.Groups[2].Value);
            if (!Logins.TryGetValue(role, out var login) || !Databases.Contains(database)) return Ok();
            return Ok($"{Scram.Verifier(login.Password)}|{(login.Owns.Contains(database) ? "t" : "f")}\n");
        }
        if (sql.Contains("DROP DATABASE", StringComparison.Ordinal) || sql.StartsWith("DROP ROLE", StringComparison.Ordinal))
        {
            foreach (Match m in Regex.Matches(sql, "DROP DATABASE IF EXISTS \"([^\"]+)\"")) Databases.Remove(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(sql, "DROP ROLE IF EXISTS \"([^\"]+)\"")) Roles.Remove(m.Groups[1].Value);
            return Ok();
        }
        if (sql.StartsWith("SELECT datname || ':'", StringComparison.Ordinal))
            return Ok(string.Concat(Regex.Matches(sql, "'([^']+)'").Select(m => m.Groups[1].Value).Where(Databases.Contains).Select(d => $"{d}:8192\n")));
        return Fail("psql: unexpected script");
    }
}
