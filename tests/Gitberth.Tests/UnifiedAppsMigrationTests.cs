using System.Globalization;
using Gitberth.Apps;
using Gitberth.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Gitberth.Tests;

/// <summary>
/// Rows written the way an older Gitberth wrote them, by hand: EF Core's model is today's, whose Apps has columns an older
/// database doesn't. The Apps table had the same columns from RenameGitDockBuildColumn to NetworkModel.
/// </summary>
internal static class OldSchema
{
    public const string RenameGitDockBuildColumn = "20261104215155_RenameGitDockBuildColumn";
    public const string NetworkModel = "20261106012106_NetworkModel";
    public const string UnifiedApps = "20261107052157_UnifiedApps";

    /// <summary>
    /// An app's row as Gitberth wrote it before UnifiedApps: as a new AppDefinition has it, or with <paramref name="full"/>
    /// every column given a value of its own.
    /// </summary>
    public static void InsertApp(SqliteConnection connection, int id, string slug, string? currentImage = null, bool configPending = false, bool rebuildPending = false,
        string updatedAt = "2026-11-05 21:14:07.1234567", string? name = null, bool full = false)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = $"""
            INSERT INTO "Apps" ("Id", "Name", "Slug", "Owner", "Repo", "Branch", "Subdirectory", "DockerfileMode", "CustomDockerfile", "ProjectHint",
                "DetectedKind", "DetectedSummary", "DetectedPort", "AutoDeploy", "DesiredState", "RestartPolicy", "MemoryLimitMb", "CpuLimit",
                "HealthCheckPath", "VercelVariables", "CurrentImage", "CurrentCommit", "LastAttemptedCommit", "ConfigChangePending", "RebuildPending",
                "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $slug, 'acme', $slug, 'main', {(full ? "'web'" : "NULL")}, 'Auto', {(full ? "'FROM node'" : "NULL")}, {(full ? "'web.csproj'" : "NULL")},
                {(full ? "'Node', 'Next.js', 3000" : "NULL, NULL, NULL")}, 1, 'Running', 'unless-stopped', {(full ? "512, 1.5, '/health'" : "NULL, NULL, NULL")}, 1,
                $image, $commit, $commit, $config, $rebuild, '2026-11-02 17:08:52.7654321', $updated)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", name ?? slug);
        insert.Parameters.AddWithValue("$slug", slug);
        insert.Parameters.AddWithValue("$image", (object?)currentImage ?? DBNull.Value);
        insert.Parameters.AddWithValue("$commit", currentImage is null ? DBNull.Value : currentImage[(currentImage.IndexOf(':') + 1)..]);
        insert.Parameters.AddWithValue("$config", configPending ? 1 : 0);
        insert.Parameters.AddWithValue("$rebuild", rebuildPending ? 1 : 0);
        insert.Parameters.AddWithValue("$updated", updatedAt);
        insert.ExecuteNonQuery();
    }

    /// <summary>A domain's row as Gitberth wrote it before UnifiedApps.</summary>
    public static void InsertDomain(SqliteConnection connection, int appId, string hostname, string? service, bool main = false)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """INSERT INTO "Domains" ("AppId", "Hostname", "KeepPaths", "Service", "Main", "CreatedAt") VALUES ($app, $host, 0, $service, $main, '2026-11-03 10:00:00')""";
        insert.Parameters.AddWithValue("$app", appId);
        insert.Parameters.AddWithValue("$host", hostname);
        insert.Parameters.AddWithValue("$service", (object?)service ?? DBNull.Value);
        insert.Parameters.AddWithValue("$main", main ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    /// <summary>Runs a statement, for rows of other tables.</summary>
    public static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every row of a table, each column by name.</summary>
    public static List<Dictionary<string, object?>> Rows(SqliteConnection connection, string table)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"""SELECT * FROM "{table}" ORDER BY rowid""";
        using var reader = select.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A table's columns.</summary>
    public static List<string> Columns(SqliteConnection connection, string table)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"""SELECT "name" FROM pragma_table_info('{table}')""";
        using var reader = select.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    public static string Show(Dictionary<string, object?> row) =>
        string.Join(' ', row.Select(c => $"{c.Key}={(c.Value is null ? "NULL" : Convert.ToString(c.Value, CultureInfo.InvariantCulture))}"));
}

/// <summary>
/// The one migration of the unified apps (5.2) and what it guarantees for orb and kanban (5.3): it only adds columns,
/// tables and indexes, every value the apps had stays exactly as it was (their pending flags and last update included),
/// they're Git apps that keep their container names and are pinned, and going back works.
/// </summary>
public sealed class UnifiedAppsMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public UnifiedAppsMigrationTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private GitberthDb Db() => new(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);

    // Tables as NetworkModel left them, and the rows an orb-like and a kanban-like app have there, with every column
    // filled, so a value the migration touched would show.
    private static readonly string[] OldTables = ["Apps", "Ports", "EnvVars", "Volumes", "Domains", "Deployments", "AppNetworks", "KeptNetworks", "ContainerPortNotes", "ContainerChanges", "Settings"];

    private void NetworkModelDatabase()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(OldSchema.NetworkModel);
        OldSchema.InsertApp(_connection, 1, "orb", "gitberth/orb:1d2948c", updatedAt: "2026-11-05 03:12:44.5550001", full: true);
        // kanban has a change saved for its next deploy, and one for its next build: they must stay that way.
        OldSchema.InsertApp(_connection, 5, "kanban", "gitberth/kanban:425df8a", configPending: true, rebuildPending: true, updatedAt: "2026-11-06 09:41:00.0000002", full: true);
        OldSchema.Exec(_connection, """
            INSERT INTO "Ports" ("Id", "AppId", "ContainerPort", "Protocol", "Access", "HostPort", "HostIp", "IsWeb") VALUES (1, 1, 80, 'tcp', 'Lan', 3000, NULL, 1);
            INSERT INTO "Ports" ("Id", "AppId", "ContainerPort", "Protocol", "Access", "HostPort", "HostIp", "IsWeb") VALUES (6, 5, 3000, 'tcp', 'Lan', 3001, NULL, 1);
            INSERT INTO "Ports" ("Id", "AppId", "ContainerPort", "Protocol", "Access", "HostPort", "HostIp", "IsWeb") VALUES (7, 5, 9229, 'tcp', 'Internal', NULL, NULL, 0);
            INSERT INTO "EnvVars" ("AppId", "Key", "ProtectedValue", "IsSecret", "BuildTime") VALUES (5, 'DATABASE_URL', 'CfDJ8-protected-1', 1, 0);
            INSERT INTO "EnvVars" ("AppId", "Key", "ProtectedValue", "IsSecret", "BuildTime") VALUES (5, 'NEXT_PUBLIC_SITE', 'CfDJ8-protected-2', 0, 1);
            INSERT INTO "Volumes" ("AppId", "Source", "Target", "ReadOnly") VALUES (5, 'uploads', '/app/uploads', 0);
            INSERT INTO "Deployments" ("AppId", "Trigger", "Status", "CommitSha", "ImageTag", "QueuedAt", "GitberthBuild") VALUES (5, 'Poll', 'Succeeded', '425df8a', 'gitberth/kanban:425df8a', '2026-11-06 09:40:00', 'b1');
            INSERT INTO "AppNetworks" ("AppId", "Network", "Alias", "IPv4Address") VALUES (5, 'media', 'kanban-db', NULL);
            INSERT INTO "KeptNetworks" ("ContainerName", "Network", "Alias", "IPv4Address", "CreatedAt") VALUES ('postgres', 'gitberth-lan', NULL, '10.1.1.230', '2026-11-06 10:00:00');
            INSERT INTO "ContainerPortNotes" ("ContainerName", "Port", "Protocol") VALUES ('postgres', 9187, 'tcp');
            INSERT INTO "ContainerChanges" ("ContainerName", "Summary", "OldId", "NewId", "OldName", "TempName", "WasRunning", "OldRestartPolicy", "Step", "Outcome", "StartedAt")
                VALUES ('postgres', 'published 5432/tcp on the LAN', 'aaa', 'bbb', 'postgres_old_aaa', 'postgres_new_aaa', 1, '{"Name":"unless-stopped","MaximumRetryCount":0}', 'Finished', 'Done', '2026-11-06 10:05:00');
            INSERT INTO "Settings" ("Key", "Value") VALUES ('restart.schedules', '{"gb-kanban":360}');
            """);
        OldSchema.InsertDomain(_connection, 1, "orchardlane.com", "http://localhost:3000");
        OldSchema.InsertDomain(_connection, 5, "app.harborlab.com", "http://localhost:3001", main: true);
    }

    private Dictionary<string, List<Dictionary<string, object?>>> Snapshot() => OldTables.ToDictionary(t => t, t => OldSchema.Rows(_connection, t));

    // The rows of each table, with only the columns they had before.
    private static void AssertKept(Dictionary<string, List<Dictionary<string, object?>>> before, Dictionary<string, List<Dictionary<string, object?>>> after)
    {
        foreach (var (table, rows) in before)
        {
            Assert.Equal(rows.Count, after[table].Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var old = rows[i];
                var now = after[table][i].Where(c => old.ContainsKey(c.Key)).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
                Assert.True(old.Count == now.Count && old.All(c => Equals(c.Value, now[c.Key])), $"{table}: {OldSchema.Show(old)} became {OldSchema.Show(now)}");
            }
        }
    }

    [Fact]
    public void It_only_adds_and_every_value_orb_and_kanban_had_stays()
    {
        NetworkModelDatabase();
        var before = Snapshot();

        using (var db = Db())
        {
            // Every migration since NetworkModel: UnifiedApps first, then any later one, all of which only add.
            var pending = db.Database.GetPendingMigrations().ToList();
            Assert.Equal(OldSchema.UnifiedApps, pending.First());
            Assert.Equal(db.Database.GetMigrations().SkipWhile(m => m != OldSchema.NetworkModel).Skip(1), pending);
            db.Database.Migrate();
        }

        AssertKept(before, Snapshot());
        // The new columns: Git apps from before, pinned so the menu looks as it did, nothing else set.
        foreach (var app in OldSchema.Rows(_connection, "Apps"))
            Assert.Equal(("Git", 1L, "Off", null, null, null, null, null, null, null, null),
                ((string)app["Source"]!, (long)app["Pinned"]!, (string)app["UpdatePolicy"]!, app["Container"], app["ContainerId"], app["ImageRef"], app["RegistryId"],
                    app["PreviousImage"], app["SpecProtected"], app["RuntimeExtras"], app["SecretKeys"]));
        Assert.Equal(("Ports", null, null, null, null), Single(OldSchema.Rows(_connection, "ContainerChanges"), c =>
            ((string)c["Kind"]!, c["Fields"], c["NewRestartPolicy"], c["ImageBefore"], c["ImageAfter"])));
        Assert.Empty(OldSchema.Rows(_connection, "ActivityEntries"));
        Assert.Empty(OldSchema.Rows(_connection, "Registries"));
        Assert.Empty(OldSchema.Rows(_connection, "ImageChecks"));

        // Read back as today's Gitberth reads them.
        using (var db = Db())
        {
            var apps = db.Apps.AsNoTracking().OrderBy(a => a.Id).ToList();
            Assert.Equal([("gb-orb", AppSource.Git, true, "orb", true, false, false), ("gb-kanban", AppSource.Git, true, "kanban", true, true, true)],
                apps.Select(a => (a.ContainerName, a.Source, a.Deployed, a.NetworkName, a.Pinned, a.ConfigChangePending, a.RebuildPending)));
            Assert.All(apps, a => Assert.Equal(DateTimeKind.Utc, a.UpdatedAt.Kind));
            Assert.Equal(new DateTime(2026, 11, 6, 9, 41, 0, DateTimeKind.Utc).AddTicks(2), apps[1].UpdatedAt);
            Assert.Equal(ChangeKind.Ports, db.ContainerChanges.AsNoTracking().Single().Kind);
        }
    }

    [Fact]
    public void It_never_rebuilds_the_apps_table()
    {
        NetworkModelDatabase();
        using var db = Db();
        var script = db.GetService<IMigrator>().GenerateScript(OldSchema.NetworkModel, OldSchema.UnifiedApps);

        // Every statement on Apps adds a column or an index (5.2): orb's and kanban's rows are never copied.
        Assert.DoesNotContain("ef_temp", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("""ALTER TABLE "Apps" ADD "Source" TEXT NOT NULL DEFAULT 'Git';""", script, StringComparison.Ordinal);
        Assert.Contains("""ALTER TABLE "Apps" ADD "Pinned" INTEGER NOT NULL DEFAULT 0;""", script, StringComparison.Ordinal);
        Assert.Contains("""ALTER TABLE "ContainerChanges" ADD "Kind" TEXT NOT NULL DEFAULT 'Ports';""", script, StringComparison.Ordinal);
        Assert.Contains("""CREATE UNIQUE INDEX "IX_Apps_Container" ON "Apps" ("Container");""", script, StringComparison.Ordinal);
        Assert.Contains("""UPDATE "Apps" SET "Pinned" = 1;""", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Going_back_leaves_the_rows_as_they_were()
    {
        NetworkModelDatabase();
        var before = Snapshot();
        using (var db = Db())
        {
            db.Database.Migrate();
            db.GetService<IMigrator>().Migrate(OldSchema.NetworkModel);
            Assert.Equal(OldSchema.NetworkModel, db.Database.GetAppliedMigrations().Last());
        }

        AssertKept(before, Snapshot());
        Assert.DoesNotContain("Source", OldSchema.Columns(_connection, "Apps"));
        Assert.DoesNotContain("Kind", OldSchema.Columns(_connection, "ContainerChanges"));
        // And forward again, as the next start would.
        using (var db = Db()) db.Database.Migrate();
        AssertKept(before, Snapshot());
    }

    [Fact]
    public void Two_rows_can_have_no_container_but_never_the_same_one()
    {
        using (var db = Db()) db.Database.Migrate();
        using (var db = Db())
        {
            db.Apps.AddRange(
                new AppDefinition { Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb" },
                new AppDefinition { Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban" },
                new AppDefinition { Name = "postgres", Slug = "postgres", Source = AppSource.Existing, Container = "postgres" });
            db.SaveChanges();
            // A Git app saved by EF Core is a Git app in the database too (the column's default), and an Existing one says so.
            Assert.Equal(["Git", "Git", "Existing"], OldSchema.Rows(_connection, "Apps").Select(r => (string)r["Source"]!));
        }
        using (var db = Db())
        {
            db.Apps.Add(new AppDefinition { Name = "postgres again", Slug = "postgres-2", Source = AppSource.Existing, Container = "postgres" });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void An_entry_outlives_its_apps_row()
    {
        using (var db = Db()) db.Database.Migrate();
        using (var db = Db())
        {
            var app = new AppDefinition { Name = "postgres", Slug = "postgres", Source = AppSource.Existing, Container = "postgres" };
            db.Apps.Add(app);
            db.SaveChanges();
            db.Activity.Add(new ActivityEntry { ContainerName = "postgres", AppId = app.Id, Kind = ActivityKind.Adopted, Text = "Adopted" });
            db.SaveChanges();
        }
        using (var db = Db())
        {
            db.Apps.Remove(db.Apps.Single());
            db.SaveChanges();
        }
        using (var db = Db())
        {
            var entry = Assert.Single(db.Activity.AsNoTracking().ToList());
            Assert.Equal(("postgres", (int?)null, ActivityKind.Adopted), (entry.ContainerName, entry.AppId, entry.Kind));
        }
    }

    private static T Single<T>(List<Dictionary<string, object?>> rows, Func<Dictionary<string, object?>, T> pick) => pick(Assert.Single(rows));
}
