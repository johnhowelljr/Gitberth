using System.Data.Common;
using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Gitberth.Tests;

/// <summary>
/// The NetworkModel migration, run for real on a database as Gitberth kept it until then: each port's access comes from
/// the address it was published on, and each app's first TCP port becomes its web port.
/// </summary>
public sealed class PortMigrationTests : IDisposable
{
    // The last migration before NetworkModel.
    private const string Before = "20261104215155_RenameGitDockBuildColumn";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PortMigrationTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private GitberthDb Db() => new(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);

    /// <summary>A database at <see cref="Before"/>, with three apps and these ports as the old table held them.</summary>
    private void OldDatabase(params (int Id, int AppId, string Protocol, string? HostIp, int HostPort, int ContainerPort)[] ports)
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        // Both tables are written by hand, in their old shapes: the ports' has no Access, the apps' none of today's new columns.
        OldSchema.InsertApp(_connection, 1, "orb");
        OldSchema.InsertApp(_connection, 2, "kanban");
        OldSchema.InsertApp(_connection, 3, "dns");
        foreach (var p in ports)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = """INSERT INTO "Ports" ("Id", "AppId", "Protocol", "HostIp", "HostPort", "ContainerPort") VALUES ($id, $app, $protocol, $ip, $host, $container)""";
            insert.Parameters.AddWithValue("$id", p.Id);
            insert.Parameters.AddWithValue("$app", p.AppId);
            insert.Parameters.AddWithValue("$protocol", p.Protocol);
            insert.Parameters.AddWithValue("$ip", (object?)p.HostIp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$host", p.HostPort);
            insert.Parameters.AddWithValue("$container", p.ContainerPort);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Every port as the table holds it now, by ID.</summary>
    private List<(int Id, int AppId, string Protocol, string Access, string? HostIp, int? HostPort, int ContainerPort, bool IsWeb)> Ports()
    {
        using var select = _connection.CreateCommand();
        select.CommandText = """SELECT "Id", "AppId", "Protocol", "Access", "HostIp", "HostPort", "ContainerPort", "IsWeb" FROM "Ports" ORDER BY "Id" """;
        using var reader = select.ExecuteReader();
        var rows = new List<(int, int, string, string, string?, int?, int, bool)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7) == 1));
        return rows;
    }

    [Fact]
    public void Old_ports_become_lan_host_only_or_custom_and_the_first_tcp_one_is_web()
    {
        OldDatabase(
            // orb: a UDP port before its first TCP one, which is its web port whatever its address.
            (1, 1, "udp", null, 5353, 53),
            (2, 1, "tcp", "0.0.0.0", 3000, 80),
            (3, 1, "tcp", "", 3010, 8080),
            (4, 1, "tcp", "127.0.0.1", 3011, 9090),
            // kanban: written out of order, so the lowest ID is what makes the web port, not the first row written.
            (8, 2, "tcp", null, 3001, 3001),
            (5, 2, "udp", "::", 3003, 3003),
            (6, 2, "tcp", "10.1.1.80", 3004, 3004),
            (7, 2, "tcp", "::1", 3002, 3000),
            // dns: UDP only, so no web port; addresses as the old form may have kept them, spaces and brackets included.
            (9, 3, "udp", "0.0.0.0", 53, 53),
            (10, 3, "udp", " 127.0.0.1 ", 54, 54),
            (11, 3, "udp", "[::1]", 55, 55),
            (12, 3, "udp", "fd00::5", 56, 56));

        using (var db = Db()) db.Database.Migrate();

        Assert.Equal(
            [
                (1, 1, "udp", "Lan", null, 5353, 53, false),
                (2, 1, "tcp", "Lan", null, 3000, 80, true),
                (3, 1, "tcp", "Lan", null, 3010, 8080, false),
                (4, 1, "tcp", "HostOnly", null, 3011, 9090, false),
                (5, 2, "udp", "Lan", null, 3003, 3003, false),
                (6, 2, "tcp", "Custom", "10.1.1.80", 3004, 3004, true),
                (7, 2, "tcp", "HostOnly", null, 3002, 3000, false),
                (8, 2, "tcp", "Lan", null, 3001, 3001, false),
                (9, 3, "udp", "Lan", null, 53, 53, false),
                (10, 3, "udp", "HostOnly", null, 54, 54, false),
                (11, 3, "udp", "HostOnly", null, 55, 55, false),
                (12, 3, "udp", "Custom", "fd00::5", 56, 56, false),
            ],
            Ports());

        // Gitberth reads them back the same way, and can save what the old table couldn't hold.
        using (var db = Db())
        {
            Assert.Equal([PortAccess.Lan, PortAccess.Custom, PortAccess.HostOnly, PortAccess.Lan],
                db.Ports.Where(p => p.AppId == 2).OrderBy(p => p.Id).Select(p => p.Access).ToList());
            Assert.Equal(6, PortRules.WebOf(db.Ports.Where(p => p.AppId == 2).ToList())!.Id);
            db.Ports.Add(new PortMapping { AppId = 2, ContainerPort = 9229, Access = PortAccess.Internal });
            db.AppNetworks.Add(new AppNetwork { AppId = 2, Network = "media", Alias = "db" });
            db.KeptNetworks.Add(new KeptNetwork { ContainerName = "postgres", Network = "gitberth" });
            db.ContainerPortNotes.Add(new ContainerPortNote { ContainerName = "postgres", Port = 5432 });
            db.SaveChanges();
        }
        Assert.Equal((null, null), Ports().Where(p => p.ContainerPort == 9229).Select(p => (p.HostPort, p.HostIp)).Single());
    }

    /// <summary>Stops the migration at its very end, as a start stopped then would: before NetworkModel is written down as applied.</summary>
    private sealed class StopBeforeItsDone : DbCommandInterceptor
    {
        private bool _stopped;

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (!_stopped && command.CommandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal) && command.CommandText.Contains("_NetworkModel", StringComparison.Ordinal))
            {
                _stopped = true;
                throw new InvalidOperationException("Gitberth stopped.");
            }
            return result;
        }
    }

    [Fact]
    public void A_start_stopped_during_the_migration_leaves_the_database_as_it_was()
    {
        OldDatabase((1, 1, "tcp", null, 3000, 80));

        using (var db = new GitberthDb(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).AddInterceptors(new StopBeforeItsDone()).Options))
            Assert.ThrowsAny<Exception>(() => db.Database.Migrate());

        // All or nothing: the ports' table is as it was, so the next start migrates it as if it were the first.
        using (var db = Db()) Assert.Equal(Before, db.Database.GetAppliedMigrations().Last());
        using (var columns = _connection.CreateCommand())
        {
            columns.CommandText = """SELECT COUNT(*) FROM pragma_table_info('Ports') WHERE "name" IN ('Access', 'IsWeb')""";
            Assert.Equal(0L, columns.ExecuteScalar());
        }
        using (var db = Db()) db.Database.Migrate();
        Assert.Equal([(1, 1, "tcp", "Lan", null, 3000, 80, true)], Ports());
    }

    [Fact]
    public void Going_back_keeps_every_published_port()
    {
        OldDatabase((1, 1, "tcp", "127.0.0.1", 3002, 3000), (2, 1, "tcp", "0.0.0.0", 3001, 9000));
        using (var db = Db())
        {
            db.Database.Migrate();
            db.Ports.Add(new PortMapping { AppId = 1, ContainerPort = 9229, Access = PortAccess.Internal });
            db.SaveChanges();
            db.GetService<IMigrator>().Migrate(Before);
        }

        // The host only is an address again; a port that was only Internal has no place in the old table.
        using var select = _connection.CreateCommand();
        select.CommandText = """SELECT "Id", "HostIp", "HostPort" FROM "Ports" ORDER BY "Id" """;
        using var reader = select.ExecuteReader();
        var rows = new List<(int, string?, int)>();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2)));
        Assert.Equal([(1, "127.0.0.1", 3002), (2, null, 3001)], rows);
    }
}
