using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Gitberth.Tests;

/// <summary>A rehearsal test: skipped unless GITBERTH_REHEARSAL names a folder holding the live server's snapshot.</summary>
public sealed class RehearsalFactAttribute : FactAttribute
{
    public RehearsalFactAttribute()
    {
        if (RehearsalSnapshot.Folder is null) Skip = RehearsalSnapshot.SkipReason;
    }
}

/// <summary>A rehearsal theory: skipped unless GITBERTH_REHEARSAL names a folder holding the live server's snapshot.</summary>
public sealed class RehearsalTheoryAttribute : TheoryAttribute
{
    public RehearsalTheoryAttribute()
    {
        if (RehearsalSnapshot.Folder is null) Skip = RehearsalSnapshot.SkipReason;
    }
}

/// <summary>A port row after the NetworkModel migration, as the table holds it.</summary>
public sealed record RehearsalPort(int Id, int AppId, string Protocol, string Access, string? HostIp, int? HostPort, int ContainerPort, bool IsWeb);

/// <summary>What an app's row says about pending changes, and when it was last updated.</summary>
public sealed record RehearsalApp(int Id, string Slug, bool ConfigChangePending, bool RebuildPending, string? CurrentImage, string? UpdatedAt);

/// <summary>
/// The snapshot of the live server that scratchpad/rehearsal/capture.sh takes, read-only, into /root/gb-rehearsal: the
/// live database (gitberth.db, through SQLite's backup API), the inspects of gb-orb, gb-kanban, gitberth, postgres and
/// cloudflare-tunnel with every environment value and command line removed, docker ps without commands, the networks,
/// the engine's version and the host's /proc/net files. It's read once, and the database is copied into a folder of the
/// test's own, where the pending migrations run once, the way Program.cs runs them. The snapshot is never written to.
/// </summary>
public sealed class RehearsalSnapshot : IAsyncLifetime
{
    public const string Variable = "GITBERTH_REHEARSAL";

    /// <summary>A folder the rows before and after the migration are written to as well, when set.</summary>
    public const string OutVariable = "GITBERTH_REHEARSAL_OUT";

    public const string SkipReason =
        "Set GITBERTH_REHEARSAL to a folder holding the live server's snapshot (gitberth.db, inspect-*.json, ps.jsonl, networks.json, engine.txt, proc/) to rehearse the upgrade.";

    /// <summary>The migration step 2 brings.</summary>
    public const string NetworkModel = "20261106012106_NetworkModel";

    /// <summary>The live database's last migration before the upgrade: NetworkModel, live since step 3 was deployed.</summary>
    public const string Before = NetworkModel;

    /// <summary>The unified apps' one migration.</summary>
    public const string UnifiedApps = "20261107052157_UnifiedApps";

    /// <summary>The tree's latest migration, which the upgrade migrates the database to.</summary>
    public string Latest { get; private set; } = "";

    public static string? Folder =>
        Environment.GetEnvironmentVariable(Variable)?.Trim() is { Length: > 0 } dir && File.Exists(Path.Combine(dir, "gitberth.db")) ? dir : null;

    public string TempDir { get; private set; } = "";
    public string PristineDb { get; private set; } = "";
    public string MigratedDb { get; private set; } = "";

    /// <summary>Why the snapshot couldn't be read or copied; nothing else is known then.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>What the migration threw, if it did.</summary>
    public Exception? MigrationError { get; private set; }

    /// <summary>Each container's inspect, by name, as captured.</summary>
    public Dictionary<string, JsonObject> Inspects { get; } = new(StringComparer.Ordinal);

    /// <summary>docker ps -a --no-trunc --format '{{json .}}', a row per container, and each row's line as captured.</summary>
    public List<JsonObject> Ps { get; } = [];
    public List<string> PsLines { get; } = [];

    public JsonArray Networks { get; private set; } = [];
    public Version Api { get; private set; } = new(0, 0);
    public string EngineVersion { get; private set; } = "";

    /// <summary>The host's /proc files, by their path on the host.</summary>
    public Dictionary<string, string> Proc { get; } = new(StringComparer.Ordinal);

    /// <summary>What GET containers/json?all=true answers, built from docker ps and the inspects.</summary>
    public string ContainerList { get; private set; } = "[]";

    public List<Dictionary<string, object?>> PortsBefore { get; private set; } = [];
    public List<RehearsalPort> PortsAfter { get; private set; } = [];
    public List<RehearsalApp> AppsBefore { get; private set; } = [];
    public List<RehearsalApp> AppsAfter { get; private set; } = [];
    public List<string> AppliedBefore { get; private set; } = [];
    public List<string> AppliedAfter { get; private set; } = [];
    public List<string> PendingBefore { get; private set; } = [];
    public List<string> PendingAfter { get; private set; } = [];

    /// <summary>Rows in EF Core's migration lock table before the upgrade (a left one would hold the start up); null without the table.</summary>
    public int? LockRowsBefore { get; private set; }

    /// <summary>The Ports table's columns after the migration, and whether each is NOT NULL.</summary>
    public Dictionary<string, bool> PortsColumnsAfter { get; private set; } = [];

    /// <summary>The domain rows, which hold no secrets: hostname, where Gitberth sent it, whether it keeps its paths, and main.</summary>
    public List<(int AppId, string Hostname, string? Service, bool KeepPaths, bool Main)> Domains { get; private set; } = [];

    /// <summary>What EF Core logged while migrating: the migrations it applied, and any warning.</summary>
    public List<string> EfLog { get; } = [];

    /// <summary>The SQL of the migrations the upgrade applies, as EF Core scripts them.</summary>
    public string MigrationScript { get; private set; } = "";

    /// <summary>The SQL of UnifiedApps alone, from NetworkModel on.</summary>
    public string UnifiedScript { get; private set; } = "";

    /// <summary>Every column of every app's row, before the upgrade and after it.</summary>
    public List<Dictionary<string, object?>> AppRowsBefore { get; private set; } = [];
    public List<Dictionary<string, object?>> AppRowsAfter { get; private set; } = [];

    /// <summary>The rows in the tables UnifiedApps adds, by table, after the migration.</summary>
    public Dictionary<string, int> NewTableRows { get; private set; } = [];

    /// <summary>The rows before and after, and what else the migration was seen to do; test A prints them.</summary>
    public List<string> Report { get; } = [];

    public async Task InitializeAsync()
    {
        if (Folder is not { } folder) return;
        try
        {
            Read(folder);
            TempDir = Path.Combine(Path.GetTempPath(), "gitberth-rehearsal-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(TempDir);
            PristineDb = Path.Combine(TempDir, "before.db");
            MigratedDb = Path.Combine(TempDir, "migrated.db");
            CopyDatabase(Path.Combine(folder, "gitberth.db"), PristineDb);
            CopyDatabase(PristineDb, MigratedDb);
            ReadBefore();
        }
        catch (Exception ex)
        {
            Failure = ex;
            return;
        }

        try
        {
            await MigrateAsync();
        }
        catch (Exception ex)
        {
            MigrationError = ex;
        }
        finally
        {
            Checkpoint(MigratedDb);
        }

        try
        {
            if (MigrationError is null) ReadAfter();
            WriteReport(folder);
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (TempDir.Length > 0 && Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    /// <summary>A database file and its write-ahead log, if it has a non-empty one; the shared-memory index is rebuilt by SQLite.</summary>
    public static void CopyDatabase(string from, string to)
    {
        File.Copy(from, to, overwrite: true);
        if (new FileInfo(from + "-wal") is { Exists: true, Length: > 0 }) File.Copy(from + "-wal", to + "-wal", overwrite: true);
    }

    /// <summary>The inspect of a container by its name, full ID or short ID.</summary>
    public JsonObject? InspectOf(string idOrName)
    {
        var name = idOrName.TrimStart('/');
        if (Inspects.TryGetValue(name, out var byName)) return byName;
        return name.Length >= 12 ? Inspects.Values.FirstOrDefault(i => Text(i, "Id").StartsWith(name, StringComparison.Ordinal)) : null;
    }

    /// <summary>A container's docker ps row, by its name or ID.</summary>
    public JsonObject? PsRow(string idOrName) =>
        Ps.FirstOrDefault(r => Text(r, "Names").Split(',').Contains(idOrName) || (idOrName.Length >= 12 && Text(r, "ID").StartsWith(idOrName, StringComparison.Ordinal)));

    internal static string Text(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private void Read(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, "inspect-*.json"))
        {
            var inspect = (JsonNode.Parse(File.ReadAllText(file)) as JsonArray)?.FirstOrDefault() as JsonObject
                ?? throw new InvalidDataException($"{Path.GetFileName(file)} holds no inspect.");
            Inspects[Text(inspect, "Name").TrimStart('/')] = (JsonObject)inspect.DeepClone();
        }
        foreach (var line in File.ReadAllLines(Path.Combine(folder, "ps.jsonl")).Where(l => l.Trim().Length > 0))
        {
            Ps.Add(JsonNode.Parse(line)!.AsObject());
            PsLines.Add(line.Trim());
        }
        Networks = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "networks.json")))!.AsArray();
        var engine = File.ReadAllText(Path.Combine(folder, "engine.txt")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Api = Version.Parse(engine[0]);
        EngineVersion = engine.Length > 1 ? engine[1] : "";
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "proc")))
            Proc["/proc/net/" + Path.GetFileName(file)] = File.ReadAllText(file);
        ContainerList = BuildContainerList();
    }

    // The engine's list of containers, as Docker 29 answers GET containers/json?all=true: from each container's inspect
    // when the snapshot has one, else from its docker ps row. Like the engine, the list leaves aliases and DNS names out.
    private string BuildContainerList()
    {
        var list = new JsonArray();
        foreach (var row in Ps)
        {
            var names = Text(row, "Names").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = names.FirstOrDefault(n => !n.Contains('/')) ?? names.FirstOrDefault() ?? "";
            Inspects.TryGetValue(name, out var inspect);
            var state = Text(row, "State");
            var live = state is "running" or "paused";
            var networks = new JsonObject();
            if (inspect?["NetworkSettings"]?["Networks"] is JsonObject endpoints)
            {
                foreach (var (network, endpoint) in endpoints)
                {
                    var copy = endpoint?.DeepClone() as JsonObject ?? new JsonObject();
                    copy["Aliases"] = null;
                    copy["DNSNames"] = null;
                    networks[network] = copy;
                }
            }
            else
            {
                foreach (var network in Text(row, "Networks").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    networks[network] = new JsonObject { ["IPAMConfig"] = null, ["Aliases"] = null, ["GwPriority"] = 0, ["IPAddress"] = "", ["MacAddress"] = "", ["DNSNames"] = null };
            }
            var mode = Text(inspect?["HostConfig"], "NetworkMode") is { Length: > 0 } m ? m
                : Text(row, "Networks").Split(',')[0] is { Length: > 0 } first ? first : "bridge";
            list.Add(new JsonObject
            {
                ["Id"] = Text(row, "ID"),
                ["Names"] = new JsonArray([.. names.Select(n => (JsonNode?)JsonValue.Create("/" + n))]),
                ["Image"] = Text(row, "Image"),
                ["ImageID"] = Text(inspect, "Image"),
                ["Command"] = "",
                ["Labels"] = inspect?["Config"]?["Labels"] is JsonObject labels ? labels.DeepClone() : LabelsOf(Text(row, "Labels")),
                ["State"] = state,
                ["Status"] = Text(row, "Status"),
                ["Ports"] = !live ? new JsonArray() : inspect is not null ? PortsOf(inspect) : PortsOf(Text(row, "Ports")),
                ["HostConfig"] = new JsonObject { ["NetworkMode"] = mode },
                ["NetworkSettings"] = new JsonObject { ["Networks"] = networks },
            });
        }
        return list.ToJsonString();
    }

    private static JsonObject LabelsOf(string labels)
    {
        var o = new JsonObject();
        foreach (var (key, value) in DockerCli.ParseLabels(labels)) o[key] = value;
        return o;
    }

    // A running container's ports from its live bindings: one entry per binding, and one without a host side for a port it only exposes.
    private static JsonArray PortsOf(JsonObject inspect)
    {
        var ports = new JsonArray();
        if (inspect["NetworkSettings"]?["Ports"] is not JsonObject bound) return ports;
        foreach (var (key, value) in bound)
        {
            var (port, protocol) = NetworkPortConfig.SplitPort(key);
            var bindings = (value as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (bindings.Count == 0) ports.Add(new JsonObject { ["PrivatePort"] = port, ["Type"] = protocol });
            foreach (var b in bindings)
                ports.Add(new JsonObject
                {
                    ["IP"] = Text(b, "HostIp"),
                    ["PrivatePort"] = port,
                    ["PublicPort"] = int.Parse(Text(b, "HostPort"), CultureInfo.InvariantCulture),
                    ["Type"] = protocol,
                });
        }
        return ports;
    }

    // The same from a docker ps Ports column: "0.0.0.0:9175->80/tcp, [::]:9175->80/tcp", "5432/tcp", ranges spread out.
    private static JsonArray PortsOf(string column)
    {
        var ports = new JsonArray();
        foreach (var part in column.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            var target = arrow < 0 ? part : part[(arrow + 2)..];
            var slash = target.IndexOf('/');
            if (slash < 0) continue;
            var (from, to) = Range(target[..slash]);
            var protocol = target[(slash + 1)..];
            if (arrow < 0)
            {
                for (var p = from; p <= to; p++) ports.Add(new JsonObject { ["PrivatePort"] = p, ["Type"] = protocol });
                continue;
            }
            var host = part[..arrow];
            var colon = host.LastIndexOf(':');
            var (hostFrom, _) = Range(host[(colon + 1)..]);
            var ip = host[..colon].Trim('[', ']');
            for (var i = 0; i <= to - from; i++)
                ports.Add(new JsonObject { ["IP"] = ip, ["PrivatePort"] = from + i, ["PublicPort"] = hostFrom + i, ["Type"] = protocol });
        }
        return ports;

        static (int From, int To) Range(string text)
        {
            var dash = text.IndexOf('-');
            return dash < 0
                ? (int.Parse(text, CultureInfo.InvariantCulture), int.Parse(text, CultureInfo.InvariantCulture))
                : (int.Parse(text[..dash], CultureInfo.InvariantCulture), int.Parse(text[(dash + 1)..], CultureInfo.InvariantCulture));
        }
    }

    private void ReadBefore()
    {
        PortsBefore = Query(PristineDb, """SELECT * FROM "Ports" ORDER BY "Id" """);
        AppsBefore = Apps(PristineDb);
        AppRowsBefore = Query(PristineDb, """SELECT * FROM "Apps" ORDER BY "Id" """);
        AppliedBefore = Query(PristineDb, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" """).Select(r => (string)r["MigrationId"]!).ToList();
        // A row left in EF Core's migration lock would make the start wait for it.
        LockRowsBefore = Int(Query(PristineDb, """SELECT COUNT(*) AS "n" FROM "sqlite_master" WHERE "name" = '__EFMigrationsLock'""")[0]["n"]) == 0 ? null
            : Int(Query(PristineDb, """SELECT COUNT(*) AS "n" FROM "__EFMigrationsLock" """)[0]["n"]);
        Domains = Query(PristineDb, """SELECT "AppId", "Hostname", "Service", "KeepPaths", "Main" FROM "Domains" ORDER BY "AppId", "Id" """)
            .Select(r => (Int(r["AppId"]), (string)r["Hostname"]!, r["Service"] as string, Int(r["KeepPaths"]) == 1, Int(r["Main"]) == 1)).ToList();
    }

    // As Program.cs does it at every start: migrate, then write-ahead logging.
    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<GitberthDb>()
            .UseSqlite($"Data Source={MigratedDb}")
            .LogTo(line => { lock (EfLog) EfLog.Add(line.Trim()); }, (id, level) => level >= LogLevel.Warning || id == RelationalEventId.MigrationApplying)
            .Options;
        await using var db = new GitberthDb(options);
        Latest = db.Database.GetMigrations().Last();
        PendingBefore = [.. await db.Database.GetPendingMigrationsAsync()];
        // What the upgrade runs, statement by statement, with where its transactions begin and end.
        MigrationScript = db.GetService<IMigrator>().GenerateScript(AppliedBefore.LastOrDefault(), Latest);
        UnifiedScript = db.GetService<IMigrator>().GenerateScript(NetworkModel, Latest);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        PendingAfter = [.. await db.Database.GetPendingMigrationsAsync()];
    }

    // Every connection closed and the log folded into the file, so the migrated database copies as one file.
    private static void Checkpoint(string db)
    {
        SqliteConnection.ClearAllPools();
        if (!File.Exists(db)) return;
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private void ReadAfter()
    {
        PortsAfter = Query(MigratedDb, """SELECT "Id", "AppId", "Protocol", "Access", "HostIp", "HostPort", "ContainerPort", "IsWeb" FROM "Ports" ORDER BY "Id" """)
            .Select(r => new RehearsalPort(Int(r["Id"]), Int(r["AppId"]), (string)r["Protocol"]!, (string)r["Access"]!, r["HostIp"] as string,
                r["HostPort"] is null ? null : Int(r["HostPort"]), Int(r["ContainerPort"]), Int(r["IsWeb"]) == 1))
            .ToList();
        AppsAfter = Apps(MigratedDb);
        AppRowsAfter = Query(MigratedDb, """SELECT * FROM "Apps" ORDER BY "Id" """);
        NewTableRows = new[] { "ActivityEntries", "Registries", "ImageChecks" }
            .ToDictionary(t => t, t => Int(Query(MigratedDb, $"""SELECT COUNT(*) AS "n" FROM "{t}" """)[0]["n"]));
        AppliedAfter = Query(MigratedDb, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" """).Select(r => (string)r["MigrationId"]!).ToList();
        PortsColumnsAfter = Query(MigratedDb, """PRAGMA table_info("Ports")""").ToDictionary(r => (string)r["name"]!, r => Int(r["notnull"]) == 1);
        Checkpoint(MigratedDb);
    }

    private static List<RehearsalApp> Apps(string db) =>
        Query(db, """SELECT "Id", "Slug", "ConfigChangePending", "RebuildPending", "CurrentImage", "UpdatedAt" FROM "Apps" ORDER BY "Id" """)
            .Select(r => new RehearsalApp(Int(r["Id"]), (string)r["Slug"]!, Int(r["ConfigChangePending"]) == 1, Int(r["RebuildPending"]) == 1, r["CurrentImage"] as string, r["UpdatedAt"] as string))
            .ToList();

    internal static List<Dictionary<string, object?>> Query(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    internal static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static string Show(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private void WriteReport(string folder)
    {
        Report.Add($"Snapshot {folder}: Docker {EngineVersion}, API {Api}; {Ps.Count} containers in docker ps; inspects of {string.Join(", ", Inspects.Keys.Order(StringComparer.Ordinal))}.");
        Report.Add($"Migrations before: {AppliedBefore.Count}, the last {AppliedBefore.LastOrDefault()}; pending for this tree: {string.Join(", ", PendingBefore)} (its latest: {Latest}); migration lock rows: {(LockRowsBefore is int n ? n.ToString(CultureInfo.InvariantCulture) : "no lock table")}.");
        Report.Add("Ports before (the old table):");
        foreach (var row in PortsBefore) Report.Add("  " + string.Join(' ', row.Select(c => $"{c.Key}={Show(c.Value)}")));
        if (MigrationError is { } error)
        {
            Report.Add($"The migration failed: {error.GetType().Name}: {error.Message}");
        }
        else
        {
            Report.Add($"Migrations after: {AppliedAfter.Count}, the last {AppliedAfter.LastOrDefault()}; still pending: {(PendingAfter.Count == 0 ? "none" : string.Join(", ", PendingAfter))}.");
            Report.Add("Ports after (NetworkModel):");
            foreach (var p in PortsAfter)
                Report.Add($"  Id={p.Id} AppId={p.AppId} Protocol={p.Protocol} Access={p.Access} HostIp={Show(p.HostIp)} HostPort={Show(p.HostPort)} ContainerPort={p.ContainerPort} IsWeb={(p.IsWeb ? 1 : 0)}");
            Report.Add("  Columns: " + string.Join(", ", PortsColumnsAfter.Select(c => $"{c.Key}{(c.Value ? " NOT NULL" : "")}")));
        }
        Report.Add("Apps before -> after (pending flags, image, last update):");
        foreach (var a in AppsBefore)
        {
            var after = AppsAfter.FirstOrDefault(x => x.Id == a.Id);
            Report.Add($"  {a.Slug} (Id {a.Id}): ConfigChangePending {a.ConfigChangePending} -> {after?.ConfigChangePending}, RebuildPending {a.RebuildPending} -> {after?.RebuildPending}, {a.CurrentImage}, UpdatedAt {a.UpdatedAt} -> {after?.UpdatedAt}");
        }
        Report.Add("Domains (app, hostname, where Gitberth sent it, keeps paths, main):");
        foreach (var d in Domains) Report.Add($"  {d.AppId} {d.Hostname} {d.Service} {d.KeepPaths} {d.Main}");
        Report.Add("EF Core during the migration:" + (EfLog.Count == 0 ? " nothing at warning level or above" : ""));
        foreach (var line in EfLog) Report.Add("  " + line);
        if (Environment.GetEnvironmentVariable(OutVariable)?.Trim() is { Length: > 0 } outDir && Directory.Exists(outDir))
            File.WriteAllLines(Path.Combine(outDir, "rows.txt"), Report);
    }
}

/// <summary>
/// The dress rehearsal of the upgrade to this tree, against a snapshot of the live server (see <see cref="RehearsalSnapshot"/>):
/// after "docker compose up -d --build" migrates the database, orb (gb-orb, 3000 -> 80) and kanban (gb-kanban, 3001 ->
/// 3000) must keep running untouched, with nothing pending, their tunnel routes as they are and nothing else on the
/// host changed; the start's settling of port changes and kept networks touches nothing, and the container page shows
/// postgres, Gitberth and the tunnel as they are. Skipped unless GITBERTH_REHEARSAL names the snapshot's folder. Everything runs on copies of the
/// snapshot's database; Docker and Cloudflare are stand-ins fed from the snapshot, through the seams the other tests use
/// (DockerCli.Runner, DockerApi's handler, the Cloudflare HttpClient), never a socket or the network. Anything that would
/// change a container, network, volume, image or the tunnel is refused, recorded and fails the test.
/// </summary>
public sealed class UpgradeRehearsalTests(RehearsalSnapshot snapshot, ITestOutputHelper output) : IClassFixture<RehearsalSnapshot>
{
    /// <summary>The two live apps as the owner's brief describes them, and where the tunnel sends their domains today.</summary>
    private sealed record LiveApp(string Slug, int Id, int HostPort, int ContainerPort, string Image, string Service, string[] Domains, string? Main);

    private static readonly LiveApp[] Live =
    [
        new("orb", 1, 3000, 80, "gitberth/orb:1d2948c", "http://localhost:3000", ["orchardlane.com", "www.orchardlane.com"], null),
        new("kanban", 5, 3001, 3000, "gitberth/kanban:425df8a", "http://localhost:3001", ["harborlab.com", "www.harborlab.com", "app.harborlab.com"], "app.harborlab.com"),
    ];

    private static LiveApp LiveOf(string slug) => Live.Single(a => a.Slug == slug);

    /// <summary>
    /// (a) The migration from the snapshot's state runs without an error, keeps every port row, and gives each the access,
    /// address, host port and web marker that design 8.5 says: LAN for no address, "", 0.0.0.0 or ::; Host only for 127.0.0.1
    /// or ::1; Custom otherwise, which alone keeps its address; the web port on each app's lowest-Id TCP row.
    /// </summary>
    [RehearsalFact]
    public void A_the_migration_keeps_every_port_as_design_8_5_says()
    {
        var s = Ready(needMigration: false);
        foreach (var line in s.Report) output.WriteLine(line);
        output.WriteLine("The migration's SQL, as EF Core scripts it:");
        foreach (var line in s.MigrationScript.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)) output.WriteLine("  " + line);
        Assert.True(s.MigrationError is null, $"The migration failed: {s.MigrationError}");

        // From the snapshot's state (NetworkModel) to the tree's latest migration, UnifiedApps the one it applies; no lock left behind.
        Assert.Equal(RehearsalSnapshot.Before, s.AppliedBefore[^1]);
        Assert.True(s.LockRowsBefore is null or 0, $"EF Core's migration lock holds {s.LockRowsBefore} row(s): the start would wait for it.");
        Assert.Equal([RehearsalSnapshot.UnifiedApps], s.PendingBefore);
        Assert.Empty(s.PendingAfter);
        Assert.Equal(s.Latest, s.AppliedAfter[^1]);
        Assert.Equal(s.AppliedBefore.Concat(s.PendingBefore).ToList(), s.AppliedAfter);

        // Every row is kept, and becomes what 8.5 says.
        Assert.Equal(s.PortsBefore.Select(r => RehearsalSnapshot.Int(r["Id"])).Order(), s.PortsAfter.Select(p => p.Id).Order());
        Assert.Equal(Expected(s.PortsBefore), s.PortsAfter);
        // The live rows, as the brief has them.
        Assert.Contains(new RehearsalPort(1, 1, "tcp", "Lan", null, 3000, 80, true), s.PortsAfter);
        Assert.Contains(new RehearsalPort(6, 5, "tcp", "Lan", null, 3001, 3000, true), s.PortsAfter);
        // HostPort may be null now (an Internal port has none); Access and IsWeb are always there.
        Assert.False(s.PortsColumnsAfter["HostPort"]);
        Assert.True(s.PortsColumnsAfter["Access"]);
        Assert.True(s.PortsColumnsAfter["IsWeb"]);
        // Nothing else about the apps changes.
        Assert.Equal(s.AppsBefore, s.AppsAfter);
        Assert.DoesNotContain(s.EfLog, l => l.StartsWith("fail", StringComparison.OrdinalIgnoreCase) || l.StartsWith("crit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// (b) The container each app runs now is up to date with what the upgraded Gitberth would create it with, and the -p
    /// arguments it builds are exactly the live container's HostConfig.PortBindings. The spec is the deploy's: the app with
    /// its ports, volumes, domains and networks, its current image, no environment (values aren't compared), the shared
    /// network, gateway priority as API 1.56 gives, and IPv4-only LAN ports as the snapshot's host scan gives.
    /// </summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task B_its_container_is_up_to_date_and_published_exactly_as_it_is_now(string slug)
    {
        var live = LiveOf(slug);
        await using var rig = await Rig.CreateAsync(Ready(), output);
        AppDefinition app;
        await using (var db = await rig.Db.CreateDbContextAsync())
            app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Volumes).Include(a => a.Domains).Include(a => a.Networks).AsSplitQuery()
                .SingleAsync(a => a.Slug == slug);
        var inspect = rig.Snapshot.InspectOf(app.ContainerName) ?? throw new InvalidDataException($"The snapshot has no inspect of {app.ContainerName}.");

        // As DeployPipeline.HostTraitsAsync decides them.
        var api = await rig.Cli.ServerApiVersionAsync();
        var scan = await rig.Scanner.LatestAsync();
        var gatewayPriority = api is not null && api >= DockerCli.GwPriorityApi;
        output.WriteLine($"Engine API {api}: gateway priority {gatewayPriority}. Host scan: interface {scan.Interface}, address {scan.Address}, gateway {scan.Gateway}, global IPv6 {scan.GlobalIpv6}, error {scan.Error ?? "none"}.");
        Assert.Null(scan.Error);

        var spec = ContainerSpecFactory.For(app, app.CurrentImage!, [], "gitberth", gatewayPriority: gatewayPriority, lanIpv4Only: scan.LanIpv4Only);
        var args = spec.BuildRunCommand().Args;
        output.WriteLine($"{slug}: image {app.CurrentImage}; ports {string.Join(", ", app.Ports.Select(p => $"#{p.Id} {p.Access} {p.HostPort}->{p.ContainerPort}/{p.Protocol}{(p.IsWeb ? " web" : "")}"))}; networks {app.Networks.Count}; volumes {app.Volumes.Count}.");
        output.WriteLine("Spec: " + string.Join(' ', Pairs(args, "-p", "--expose", "--network").Select(p => $"{p.Flag} {p.Value}")));
        output.WriteLine("Live HostConfig.PortBindings: " + inspect["HostConfig"]?["PortBindings"]?.ToJsonString());
        output.WriteLine("Live endpoints: " + string.Join("; ", (inspect["NetworkSettings"]?["Networks"] as JsonObject ?? new JsonObject()).Select(n => $"{n.Key} aliases {n.Value?["Aliases"]?.ToJsonString()} names {n.Value?["DNSNames"]?.ToJsonString()}")));

        Assert.Equal(live.Id, app.Id);
        // The image Gitberth would run is the one the container runs (gitberth/orb:1d2948c, gitberth/kanban:425df8a when captured).
        Assert.Equal(RehearsalSnapshot.Text(inspect["Config"], "Image"), app.CurrentImage);
        Assert.True(spec.UpToDate(inspect), $"{app.ContainerName} isn't up to date with its migrated settings: the next look at it would want it recreated.");

        // -p H:C becomes {"C/tcp": [{"HostIp": "", "HostPort": "H"}]}, as Docker's own parser reads it.
        var built = Pairs(args, "-p").Select(p => DockerPortSpec(p.Value)).Order().ToList();
        var bound = LiveBindings(inspect).Order().ToList();
        Assert.Equal(bound, built);
        // Nothing is only exposed that the container doesn't expose already.
        var exposed = (inspect["Config"]?["ExposedPorts"] as JsonObject ?? new JsonObject()).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(Pairs(args, "--expose"), e => Assert.Contains(e.Value.Contains('/') ? e.Value : e.Value + "/tcp", exposed));
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (c) ConfigChangePending and RebuildPending stay as they are through the migration, loading the app the way its page
    /// does, saving its Network tab unchanged and saving its Settings tab unchanged, and nothing restarts.
    /// </summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task C_nothing_is_pending_after_the_migration_the_page_loads_or_unchanged_saves(string slug)
    {
        var s = Ready();
        var before = s.AppsBefore.Single(a => a.Slug == slug);
        var migrated = s.AppsAfter.Single(a => a.Slug == slug);
        output.WriteLine($"{slug}: before the upgrade pending {Pending(before)}, after the migration {Pending(migrated)}.");
        Assert.Equal((false, false), (before.ConfigChangePending, before.RebuildPending));
        Assert.Equal((before.ConfigChangePending, before.RebuildPending), (migrated.ConfigChangePending, migrated.RebuildPending));

        await using var rig = await Rig.CreateAsync(s, output);
        var id = migrated.Id;

        // The app's page, as AppPage.OnParametersSetAsync and LoadAsync load it.
        var publicHost = await rig.Apps.GetPublicHostAsync("http://10.1.1.80:8080/");
        var detail = await rig.Apps.GetDetailAsync(id) ?? throw new InvalidOperationException($"{slug} isn't in the database.");
        var domainsEnabled = await rig.Domains.EnabledAsync();
        var vercel = await rig.Apps.VercelVariablesAsync(detail.App);
        var dockerRun = await rig.Apps.DockerRunPreviewAsync(detail.App, vercel);
        var reached = await rig.Book.ForAppAsync(detail.App, publicHost);
        // The Network tab's draft of a Git app's saved ports (D2: one draft for every app), and the rows it plans and saves.
        var draft = ContainerPortsDraft.FromSaved(detail.App.Ports);
        var rows = draft.ToPortRows().ToList();
        var model = rig.Apps.ToModel(detail.App);
        var tokenSaved = domainsEnabled && await rig.Domains.TokenSavedAsync();
        var tunnelOnHost = domainsEnabled && (await rig.Domains.FindTunnelAsync()).Tunnel?.HostNetwork == true;
        var overview = await rig.Domains.GetOverviewAsync(id);
        output.WriteLine($"Page: status {detail.Status.State} ({detail.Status.Detail}), docker error {detail.DockerError ?? "none"}, domains on {domainsEnabled}, token {tokenSaved}, tunnel on the host {tunnelOnHost}, Open goes to {AddressBook.Primary(reached)}.");
        output.WriteLine("Equivalent docker run: " + string.Join(' ', dockerRun.Split('\n').Select(l => l.Trim().TrimEnd('\\').Trim()).Where(l => l.StartsWith("--network", StringComparison.Ordinal) || l.StartsWith("-p ", StringComparison.Ordinal) || l.StartsWith("--expose", StringComparison.Ordinal))));
        Assert.Equal(AppRunState.Running, detail.Status.State);
        Assert.Equal((false, false), await rig.FlagsAsync(id));

        // The Network tab: nothing to save, and saving it as it loaded restarts nothing. Its Save for next deploy and its
        // Apply both go through AppActions.ApplyPortsAsync (StorePortsAsync), which saves with SavePortsAsync and starts a
        // deploy only for a change that needs the container recreated: a deploy row would show it.
        Assert.False(draft.Dirty);
        Assert.Equal(0, draft.Changes);
        Assert.False(draft.NeedsRecreate);
        var hostnames = detail.App.Domains.OrderBy(d => d.Hostname == DomainService.MainOf(detail.App.Domains) ? 0 : 1).ThenBy(d => d.Id).Select(d => d.Hostname).ToList();
        Assert.Empty(PortRules.Check(rows, new PortContext(hostnames, tunnelOnHost, [], new HostPortOwner(detail.App.ContainerName, id))));
        var problems = await rig.Apps.ValidatePortsAsync(id, rows, draft.Loaded);
        Assert.True(problems.Count == 0, "The Network tab's check says: " + string.Join(" ", problems.Select(p => p.Message)));
        var deploys = Deploys(rig);
        foreach (var applyNow in new[] { false, true })
        {
            var saved = await rig.Actions.ApplyPortsAsync(id, rows, draft.Loaded, applyNow);
            output.WriteLine($"Network tab {(applyNow ? "Apply" : "Save for next deploy")}: ok {saved.Ok}, needs restart {saved.NeedsRestart}, deploy {saved.DeploymentId?.ToString(CultureInfo.InvariantCulture) ?? "none"}, problems [{string.Join(" ", saved.Problems.Select(p => p.Message))}].");
            Assert.True(saved.Ok);
            Assert.False(saved.NeedsRestart);
            Assert.Null(saved.DeploymentId);
        }
        // The same with the rows and key the Settings model carries, as the other tests save them.
        var again = await rig.Apps.SavePortsAsync(id, model.Ports, model.PortsLoaded);
        Assert.True(again.Ok, string.Join(" ", again.Problems.Select(p => p.Message)));
        Assert.False(again.NeedsRestart);
        Assert.Equal(deploys, Deploys(rig));
        Assert.Equal((false, false), await rig.FlagsAsync(id));

        // The Settings tab: its Save checks, then saves what it loaded.
        var errors = await rig.Apps.ValidateAsync(id, model);
        Assert.True(errors.Count == 0, "The Settings tab's check says: " + string.Join(" ", errors));
        var outcome = await rig.Apps.SaveAsync(id, model);
        output.WriteLine($"Settings tab Save: needs restart {outcome.NeedsRestart}, needs rebuild {outcome.NeedsRebuild}.");
        Assert.Equal(new SaveOutcome(false, false), outcome);
        Assert.Equal((false, false), await rig.FlagsAsync(id));
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (d) With the live docker ps Ports column and cloudflared on the host's network, the upgraded Gitberth sends each
    /// app's domains exactly where the tunnel sends them today, so its first domain sync changes no route; and the Domains
    /// card says each domain is routed and served.
    /// </summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task D_its_domains_go_exactly_where_the_tunnel_sends_them_now(string slug)
    {
        var live = LiveOf(slug);
        await using var rig = await Rig.CreateAsync(Ready(), output);
        var app = await rig.Apps.GetAsync(live.Id) ?? throw new InvalidOperationException($"{slug} isn't in the database.");
        var ps = rig.Snapshot.PsRow(app.ContainerName) ?? throw new InvalidDataException($"docker ps has no {app.ContainerName}.");
        var psPorts = RehearsalSnapshot.Text(ps, "Ports");
        var found = TunnelLocator.Parse(rig.CloudflaredInspect().ToJsonString(), "gitberth") ?? throw new InvalidDataException("The cloudflared inspect couldn't be read.");
        var tunnel = new TunnelInfo(found.Name, rig.AccountId, rig.TunnelId, found.HostNetwork, found.OnAppNetwork);
        var service = TunnelRoutes.ServiceFor(app, psPorts, tunnel);
        output.WriteLine($"{slug}: docker ps Ports \"{psPorts}\"; cloudflared {found.Name} on the host's network {found.HostNetwork}, on gitberth {found.OnAppNetwork}; ServiceFor gives {service}.");

        Assert.Equal(slug, app.Slug);
        Assert.True(tunnel.HostNetwork);
        Assert.Equal(live.Service, service);
        Assert.Equal(live.Domains.Order(StringComparer.Ordinal), app.Domains.Select(d => d.Hostname).Order(StringComparer.Ordinal));
        Assert.Equal(live.Main, app.Domains.SingleOrDefault(d => d.Main)?.Hostname);
        // Where Gitberth remembers sending each domain is that same place, and so is the tunnel's route for it.
        Assert.All(app.Domains, d => Assert.Equal(live.Service, d.Service));
        Assert.All(app.Domains, d => Assert.Equal(live.Service, RehearsalTunnel.ServiceOf(rig.Ingress, d.Hostname)));

        // The Domains card, as the Network tab reads it.
        var overview = await rig.Domains.GetOverviewAsync(app.Id);
        foreach (var d in overview.Domains) output.WriteLine($"  {d.Hostname}: working {d.Working}, \"{d.Summary}\"{(d.Main ? ", main" : "")}{(d.Warning is { } w ? ", " + w : "")}");
        Assert.Null(overview.Problem);
        Assert.True(overview.TokenSaved);
        Assert.True(overview.Tunnel?.HostNetwork);
        Assert.Equal(live.Domains.Length, overview.Domains.Count);
        Assert.All(overview.Domains, d => Assert.True(d.Working, $"{d.Hostname}: {d.Summary}"));
        Assert.All(overview.Domains, d => Assert.Equal($"Port {live.ContainerPort}, through host port {live.HostPort}", d.Summary));
        rig.AssertNothingChanged();
    }

    /// <summary>(d) The first domain sync after the upgrade, against the tunnel's routes as they are, writes nothing to Cloudflare.</summary>
    [RehearsalFact]
    public async Task D_the_first_domain_sync_after_the_upgrade_changes_no_route()
    {
        var s = Ready();
        await using var rig = await Rig.CreateAsync(s, output);
        var report = await rig.Domains.SyncAsync();
        foreach (var note in report.Notes) output.WriteLine($"  note: {note.Hostname}: {note.Text}{(note.Problem ? " (problem)" : "")}");
        output.WriteLine("Targets: " + string.Join(", ", (report.Targets ?? new Dictionary<int, string>()).Select(t => $"{t.Key} -> {t.Value}")));

        Assert.Null(report.TunnelVersion);
        Assert.DoesNotContain(report.Notes, n => n.Problem);
        Assert.Equal(Live.Select(a => (a.Id, a.Service)).OrderBy(t => t.Id).ToList(),
            (report.Targets ?? new Dictionary<int, string>()).Select(t => (t.Key, t.Value)).OrderBy(t => t.Key).ToList());
        // What each domain remembers is what it remembered.
        var after = RehearsalSnapshot.Query(rig.DbPath, """SELECT "AppId", "Hostname", "Service", "KeepPaths", "Main" FROM "Domains" ORDER BY "AppId", "Id" """)
            .Select(r => (RehearsalSnapshot.Int(r["AppId"]), (string)r["Hostname"]!, r["Service"] as string, RehearsalSnapshot.Int(r["KeepPaths"]) == 1, RehearsalSnapshot.Int(r["Main"]) == 1))
            .ToList();
        Assert.Equal(s.Domains, after);
        rig.AssertNothingChanged();
    }

    /// <summary>(e) Each app's scheduled jobs are called where base1's CronScheduler called them, inside a container and outside one.</summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task E_scheduled_jobs_are_called_where_base1_called_them(string slug)
    {
        var s = Ready();
        var live = LiveOf(slug);
        await using var rig = await Rig.CreateAsync(s, output);
        var app = await rig.Apps.GetAsync(live.Id) ?? throw new InvalidOperationException($"{slug} isn't in the database.");
        // What CronScheduler.TickAsync and RunNowAsync read: one docker ps of Gitberth's containers.
        var (containers, error) = await rig.Cli.ListContainersAsync(managedOnly: true);
        Assert.Null(error);
        var container = containers.FirstOrDefault(c => c.Name == app.ContainerName);
        var oldPorts = s.PortsBefore.Where(r => RehearsalSnapshot.Int(r["AppId"]) == app.Id)
            .Select(r => new Base1.Port(RehearsalSnapshot.Int(r["Id"]), RehearsalSnapshot.Int(r["ContainerPort"]), (string)r["Protocol"]!)).ToList();
        var crons = RehearsalSnapshot.Text(rig.Snapshot.InspectOf(app.ContainerName)?["Config"]?["Labels"], "gitberth.crons");
        output.WriteLine($"{slug}: jobs {(crons.Length > 0 ? crons : "none")}; container {container?.State} \"{container?.Ports}\".");
        foreach (var inContainer in new[] { true, false })
        {
            var now = CronScheduler.BaseUrl(app, container, inContainer);
            var then = Base1.BaseUrl(oldPorts, app.ContainerName, container, inContainer);
            output.WriteLine($"  Gitberth {(inContainer ? "in a container" : "outside a container")}: base1 {then.Url ?? then.Problem}, now {now.Url ?? now.Problem}");
            Assert.Equal(then, now);
            Assert.NotNull(now.Url);
        }
        // The live Gitberth runs in a container, so that's by name on gitberth.
        Assert.Equal($"http://{app.ContainerName}:{live.ContainerPort}", CronScheduler.BaseUrl(app, container, inContainer: true).Url);
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (f) The Addresses card shows the LAN address (the host scan's, from the snapshot's /proc) and the domains as live,
    /// and no "not live yet" note; the host-port map built from the snapshot's /proc, docker ps and inspects agrees.
    /// </summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task F_the_addresses_card_shows_the_lan_address_and_the_domains_as_live(string slug)
    {
        var live = LiveOf(slug);
        await using var rig = await Rig.CreateAsync(Ready(), output);
        // The live Gitberth has no saved LAN address (no ui.publicHost setting); its GITBERTH_PUBLIC_HOST isn't in the
        // snapshot, so here it's the address detected from the host's default route.
        var saved = await rig.Settings.GetAsync(SettingKeys.PublicHost);
        var lanHost = await rig.Apps.GetPublicHostAsync("http://10.1.1.80:8080/");
        output.WriteLine($"LAN address: {(saved is null ? lanHost : "(a saved setting, not printed)")}.");
        Assert.Null(saved);
        Assert.Equal("10.1.1.80", lanHost);

        var detail = await rig.Apps.GetDetailAsync(live.Id) ?? throw new InvalidOperationException($"{slug} isn't in the database.");
        var reached = await rig.Book.ForAppAsync(detail.App, lanHost);
        foreach (var a in reached.Addresses) output.WriteLine($"  {a.Kind} {a.Text} live {a.Live}{(a.Note is { } n ? $" ({n})" : "")}");
        output.WriteLine($"Open goes to {AddressBook.Primary(reached)}.");

        var lan = Assert.Single(reached.Lan);
        Assert.Equal(($"http://10.1.1.80:{live.HostPort}", (int?)live.HostPort, (int?)live.ContainerPort, true, (string?)null), (lan.Text, lan.HostPort, lan.ContainerPort, lan.Live, lan.Note));
        Assert.Equal(live.Domains.Select(d => "https://" + d).Order(StringComparer.Ordinal), reached.Domains.Select(d => d.Text).Order(StringComparer.Ordinal));
        Assert.All(reached.Domains, d => Assert.True(d.Live, $"{d.Text} isn't live"));
        Assert.DoesNotContain(reached.Addresses, a => a.Note?.Contains("not live yet", StringComparison.Ordinal) == true);
        Assert.Empty(reached.HostOnly);
        Assert.True(reached.Running);
        Assert.Equal(live.ContainerPort, reached.WebPort);
        Assert.Equal($"https://{live.Main ?? live.Domains.OrderBy(d => d.Length).First()}/", AddressBook.Primary(reached));
        // The dashboard and the lists read the same.
        var list = await rig.Book.LoadAsync(lanHost);
        Assert.Equal(reached.Addresses, list.For(live.Id)?.Addresses);

        // The host-port map, from the snapshot's /proc, docker ps and inspects.
        var view = await rig.HostPorts.GetViewAsync();
        foreach (var u in view.Uses.Where(u => u.Port is 22 or 2375 or 3000 or 3001 or 8080))
            output.WriteLine($"  host port {u.Port}/{u.Protocol}: {u.Owner}, {u.State}, {HostPortMap.Access(u.HostIp)}{(u.Note is { } note ? $", {note}" : "")}");
        Assert.Null(view.Scan.Error);
        var use = Assert.Single(view.Uses, u => u.Port == live.HostPort && u.Protocol == "tcp");
        Assert.Equal((HostPortUser.App, detail.App.Name, HostPortState.Published, (int?)live.ContainerPort, "LAN"), (use.Kind, use.Who, use.State, use.ContainerPort, HostPortMap.Access(use.HostIp)));
        Assert.True(HostPortMap.Check(view.Uses, view.Scan.Random, live.HostPort, "tcp", "", new HostPortOwner(detail.App.ContainerName, live.Id)).Free);
        Assert.DoesNotContain(view.Uses, u => u.AppId == live.Id && u.State == HostPortState.Saved);
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (c, "nothing restarts") The upgraded Gitberth's housekeeping at its start (StartupTasks), with Docker reachable as
    /// it is on the host: it finds orb's and kanban's containers and leaves them alone, asks for no deploy, and changes
    /// nothing in Docker but joining its own new container to gitberth (by its ID), as every start does. The deploy queue
    /// is stopped here, so a deploy it asked for would only show as a new deploy row. Once Docker answers it also removes
    /// the helpers a restart left behind, settles the port changes a restart cut short and checks the networks it keeps
    /// containers on: the live database has no port change and keeps no network, so none of those touches a container.
    /// </summary>
    [RehearsalFact]
    public async Task G_the_start_with_docker_reachable_leaves_every_container_alone()
    {
        var s = Ready();
        await using var rig = await Rig.CreateAsync(s, output);
        var deploysBefore = Deploys(rig);
        var keptBefore = Kept(rig);
        var startup = rig.Startup();
        await startup.StartAsync(CancellationToken.None);
        if (startup.ExecuteTask is { } pass) await pass.WaitAsync(TimeSpan.FromSeconds(60));
        await startup.Settled.WaitAsync(TimeSpan.FromSeconds(60));
        await startup.StopAsync(CancellationToken.None);
        var deploysAfter = Deploys(rig);
        output.WriteLine($"Deploys: {deploysBefore.Count} before, {deploysAfter.Count} after; Gitberth's own joins: {string.Join(" | ", rig.OwnJoins)}.");
        output.WriteLine($"Port changes, kept networks, port notes and apps' networks: {keptBefore} before, {Kept(rig)} after.");

        Assert.Equal(deploysBefore, deploysAfter);
        Assert.Equal([$"docker network connect --gw-priority -1 gitberth {rig.Options.OwnContainer}"], rig.OwnJoins);
        Assert.DoesNotContain(rig.Logs, l => l.Contains("Restoring", StringComparison.Ordinal) || l.Contains("is missing", StringComparison.Ordinal));
        Assert.Contains(rig.Logs, l => l.Contains($"Connected to Docker Engine {s.EngineVersion}", StringComparison.Ordinal));
        Assert.Equal((false, false), await rig.FlagsAsync(LiveOf("orb").Id));
        Assert.Equal((false, false), await rig.FlagsAsync(LiveOf("kanban").Id));
        // The settling looked at Docker, and found nothing to do.
        Assert.Contains(rig.EngineCalls, c => c.StartsWith("GET ", StringComparison.Ordinal) && c.Contains("/networks", StringComparison.Ordinal));
        Assert.Equal((0, 0, 0, 0), keptBefore);
        Assert.Equal(keptBefore, Kept(rig));
        Assert.DoesNotContain(rig.Logs, l => l.Contains("left behind", StringComparison.Ordinal) || l.Contains("no longer keeps", StringComparison.Ordinal)
            || (l.Contains("NetworkKeeper:", StringComparison.Ordinal) && l.Contains(" again", StringComparison.Ordinal))
            || new[] { "Warning StartupTasks", "Warning PortChanger", "Warning HostHelpers", "Warning NetworkKeeper" }.Any(w => l.StartsWith(w, StringComparison.Ordinal)));
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (Step 3) The container page of postgres, Gitberth and the tunnel, read the way ContainerPage reads it, from their
    /// inspects in the snapshot. postgres: reached by name on gitberth with its URL (the password masked), not published,
    /// one Internal port, and a recreate may publish it. Gitberth: its page on the LAN and inside Docker, a URL both, with
    /// no publish actions. The tunnel: on the host's network, so no table. Nothing is changed or asked to change.
    /// </summary>
    [RehearsalFact]
    public async Task H_the_container_page_shows_postgres_gitberth_and_the_tunnel_as_they_are()
    {
        await using var rig = await Rig.CreateAsync(Ready(), output);
        var lanHost = await rig.Apps.GetPublicHostAsync("http://10.1.1.80:8080/");
        Assert.Equal("10.1.1.80", lanHost);

        var postgres = await PageAsync(rig, "postgres", lanHost);
        Assert.Equal(ContainerRole.Database, postgres.Row.Role);
        Assert.Equal(["Internal postgres:5432"], postgres.Shown.Select(a => $"{a.Kind} {a.Text}"));
        Assert.Equal("gitberth", postgres.Network);
        // The snapshot has no environment values: a stand-in password shows it's masked, and copied as it is.
        var details = postgres.Details with { Env = [.. postgres.Details.Env.Where(e => e.Key != "POSTGRES_PASSWORD"), new("POSTGRES_PASSWORD", "stand-in")] };
        Assert.Equal([new UrlRow("Postgres URL (internal)", "postgres://postgres:••••@postgres:5432/postgres", "postgres://postgres:stand-in@postgres:5432/postgres")],
            ConnectionRows.Urls(details, postgres.Shown));
        Assert.Empty(ConnectionRows.Lan(postgres.Shown, 5432));
        Assert.Equal([((int?)5432, "tcp", PortAccess.Internal, (int?)null, PortOrigin.Exposed)], postgres.Ports);
        Assert.Equal(["5432/tcp"], postgres.Draft.ImageExposed);
        Assert.Equal((RecreateKind.Allowed, (string?)null), (postgres.Verdict.Kind, postgres.Busy));
        Assert.Null(postgres.WhyNotStart);

        var gitberth = await PageAsync(rig, "gitberth", lanHost);
        Assert.Equal(ContainerRole.Self, gitberth.Row.Role);
        Assert.Equal(["Lan http://10.1.1.80:8080", "Internal http://gitberth:8080"], gitberth.Shown.Select(a => $"{a.Kind} {a.Text}"));
        Assert.Equal([((int?)8080, "tcp", PortAccess.Lan, (int?)8080, PortOrigin.Bound)], gitberth.Ports);
        Assert.Equal(RecreateKind.Self, gitberth.Verdict.Kind);

        var tunnel = await PageAsync(rig, "cloudflare-tunnel", lanHost);
        Assert.Equal(ContainerRole.Tunnel, tunnel.Row.Role);
        Assert.True(tunnel.HostNetwork);
        Assert.Equal(["Lan 10.1.1.80 (On any port it listens on (host network))"], tunnel.Shown.Select(a => $"{a.Kind} {a.Text} ({a.Note})"));
        Assert.Empty(tunnel.Ports);
        Assert.Equal((RecreateKind.HostNetwork, "Uses the host's network, so Docker maps no ports: it listens on 10.1.1.80 directly."),
            (tunnel.Verdict.Kind, tunnel.Verdict.Reason));

        // None is in the middle of a port change, nor has an own LAN address or noted ports.
        Assert.All(new[] { postgres, gitberth, tunnel }, p => Assert.Null(p.Latest));
        Assert.DoesNotContain(new[] { postgres, gitberth, tunnel }.SelectMany(p => p.Shown), a => a.Kind == AddressKind.OwnLan);
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (Unified apps, 5.2 and 5.3) UnifiedApps on the live database only adds: every column orb and kanban had keeps its
    /// value (their pending flags and last update included), they're Git apps keeping their container names, pinned so the
    /// menu looks as it did, with nothing else set; the new tables are empty, and Apps is never rebuilt (no row is copied).
    /// </summary>
    [RehearsalFact]
    public void I_unified_apps_keeps_every_value_obs_and_kanban_had_and_only_adds()
    {
        var s = Ready();
        output.WriteLine("UnifiedApps, as EF Core scripts it:");
        foreach (var line in s.UnifiedScript.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)) output.WriteLine("  " + line);

        Assert.Equal(RehearsalSnapshot.UnifiedApps, s.Latest);
        Assert.Equal(RehearsalSnapshot.UnifiedApps, s.AppliedAfter[^1]);
        Assert.Equal(Live.Select(a => a.Id), s.AppRowsAfter.Select(r => RehearsalSnapshot.Int(r["Id"])));
        foreach (var before in s.AppRowsBefore)
        {
            var after = s.AppRowsAfter.Single(r => Equals(r["Id"], before["Id"]));
            foreach (var (column, value) in before)
                Assert.True(Equals(value, after[column]), $"Apps {before["Slug"]}.{column} was {value ?? "NULL"} and is {after[column] ?? "NULL"}");
            output.WriteLine($"{after["Slug"]}: Source {after["Source"]}, Pinned {after["Pinned"]}, UpdatePolicy {after["UpdatePolicy"]}, Container {after["Container"] ?? "NULL"}.");
            Assert.Equal(("Git", 1L, "Off"), ((string)after["Source"]!, (long)after["Pinned"]!, (string)after["UpdatePolicy"]!));
            foreach (var column in new[] { "Container", "ContainerId", "ImageRef", "RegistryId", "PreviousImage", "SpecProtected", "RuntimeExtras", "SecretKeys" })
                Assert.Null(after[column]);
        }
        Assert.Equal(s.AppsBefore, s.AppsAfter);
        Assert.All(s.AppsAfter, a => Assert.Equal((false, false), (a.ConfigChangePending, a.RebuildPending)));
        Assert.All(s.NewTableRows, t => Assert.Equal(0, t.Value));
        Assert.DoesNotContain("ef_temp", s.UnifiedScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RENAME", s.UnifiedScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", s.UnifiedScript, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (Unified apps, 5.3) orb's and kanban's runtime and build fingerprints, which decide ConfigChangePending and
    /// RebuildPending, are what step 3 worked out from the same rows; their runtime key is that one split in two; and the
    /// docker run their deploys use is step 3's, word for word: no settings beyond the classic ones, no new flag or label.
    /// </summary>
    [RehearsalTheory]
    [InlineData("orb")]
    [InlineData("kanban")]
    public async Task J_their_fingerprints_and_docker_run_are_step_3s(string slug)
    {
        await using var rig = await Rig.CreateAsync(Ready(), output);
        AppDefinition app;
        await using (var db = await rig.Db.CreateDbContextAsync())
            app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
                .SingleAsync(a => a.Slug == slug);
        Assert.Equal((Gitberth.Apps.AppSource.Git, $"gb-{slug}", slug, true, (string?)null), (app.Source, app.ContainerName, app.NetworkName, app.Deployed, app.RuntimeExtras));

        var fingerprint = AppService.Fingerprint(app, rig.Protector, "gitberth");
        Assert.Equal(Step3.Fingerprint(app, rig.Protector, "gitberth"), fingerprint);
        var (recreate, live) = AppService.RuntimeKeys(app, rig.Protector, "gitberth");
        Assert.Equal(fingerprint.Runtime, recreate + "#" + live);
        Assert.DoesNotContain("#extras:", fingerprint.Runtime, StringComparison.Ordinal);

        // As a deploy builds it: the host's engine and scan, the daemon's log options, and its variables (stand-in values:
        // the snapshot has no key ring to read them with).
        var api = await rig.Cli.ServerApiVersionAsync();
        var gatewayPriority = api is not null && api >= DockerCli.GwPriorityApi;
        var lanIpv4Only = (await rig.Scanner.LatestAsync()).LanIpv4Only;
        var logOptions = await rig.Cli.GetLogOptionsAsync();
        var env = app.EnvVars.OrderBy(v => v.Id).Select(v => new EnvValue(v.Key, "stand-in", v.IsSecret)).ToList();
        var spec = ContainerSpecFactory.For(app, app.CurrentImage!, env, "gitberth", logOptions, gatewayPriority: gatewayPriority, lanIpv4Only: lanIpv4Only);
        var then = Step3.Args(app, app.CurrentImage!, env, "gitberth", logOptions, gatewayPriority, lanIpv4Only);
        output.WriteLine($"{slug}: {fingerprint.Runtime.Length}-character runtime key; docker run {string.Join(' ', then.Select(a => a.Contains("stand-in", StringComparison.Ordinal) ? "…" : a))}");

        Assert.Null(spec.Extras);
        Assert.Equal(then, spec.BuildRunCommand().Args);
        Assert.Equal(Step3.Display(then, env), spec.ToDisplayString());
        var preview = await rig.Apps.DockerRunPreviewAsync(app);
        Assert.DoesNotContain(ContainerSpecFactory.SourceLabel, preview, StringComparison.Ordinal);
        rig.AssertNothingChanged();
    }

    /// <summary>
    /// (Unified apps, 1.3) The one list of apps on the live host: every container docker ps lists is an app, orb and kanban
    /// first as the pinned Git apps they are, every other one of source Existing without a row. Listing it, opening an
    /// app's page (by name, or by an old numeric link), and AppSync hearing every container start write nothing: no row
    /// besides the migration's two, no Activity entry, and nothing to Docker.
    /// </summary>
    [RehearsalFact]
    public async Task K_the_catalog_lists_every_container_as_an_app_and_writes_nothing()
    {
        var s = Ready();
        await using var rig = await Rig.CreateAsync(s, output);
        var catalog = await rig.CatalogAsync();

        var list = await catalog.ListAsync("10.1.1.80");
        foreach (var a in list.Apps)
            output.WriteLine($"  {a.Key}: {a.Title}, {a.Source}, {a.Role}, {a.Origin}, {a.State} ({a.StateText}){(a.Pinned ? ", pinned" : "")}{(a.AppId is int id ? $", row {id}" : "")}");
        Assert.Null(list.DockerError);
        var names = s.Ps.Where(r => !DockerCli.ParseLabels(RehearsalSnapshot.Text(r, "Labels")).ContainsKey(HostScanHelper.Label))
            .Select(r => RehearsalSnapshot.Text(r, "Names").Split(',').First(n => !n.Contains('/')))
            .Where(n => !n.EndsWith(DeployPipeline.BridgeSuffix, StringComparison.Ordinal) && !n.EndsWith(DeployPipeline.PreviousSuffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(names, list.Apps.Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["gb-kanban", "gb-orb"], list.Apps.Take(2).Select(a => a.Key).Order(StringComparer.Ordinal));
        foreach (var live in Live)
        {
            var app = list.Apps.Single(a => a.Key == $"gb-{live.Slug}");
            Assert.Equal((live.Id, Gitberth.Apps.AppSource.Git, true, Gitberth.Apps.ImageOrigin.Built, Gitberth.Apps.AppState.Running),
                (app.AppId, app.Source, app.Pinned, app.Origin, app.State));
        }
        Assert.All(list.Apps.Where(a => !a.Key.StartsWith("gb-", StringComparison.Ordinal)), a => Assert.Equal((Gitberth.Apps.AppSource.Existing, (int?)null), (a.Source, a.AppId)));

        Assert.Equal("gb-orb", (await catalog.GetAsync(LiveOf("orb").Id.ToString(CultureInfo.InvariantCulture), "10.1.1.80"))?.Summary.Key);
        Assert.Null((await catalog.GetAsync("postgres", "10.1.1.80"))?.Row);
        var sync = rig.Sync(catalog);
        foreach (var name in names) Assert.False(await sync.SeenAsync(name, CancellationToken.None));

        Assert.Equal([1, 5], RehearsalSnapshot.Query(rig.DbPath, """SELECT "Id" FROM "Apps" ORDER BY "Id" """).Select(r => RehearsalSnapshot.Int(r["Id"])));
        Assert.Equal(0, RehearsalSnapshot.Int(RehearsalSnapshot.Query(rig.DbPath, """SELECT COUNT(*) AS "n" FROM "ActivityEntries" """)[0]["n"]));
        Assert.Equal((false, false), await rig.FlagsAsync(LiveOf("orb").Id));
        Assert.Equal((false, false), await rig.FlagsAsync(LiveOf("kanban").Id));
        Assert.All(rig.EngineCalls, c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
        rig.AssertNothingChanged();
    }

    /// <summary>What a standalone container's page shows, read as ContainerPage.LoadAsync reads it.</summary>
    private sealed record PageView(ContainerRow Row, ContainerDetails Details, IReadOnlyList<Address> Shown, string? Network, bool HostNetwork, ContainerPortsDraft Draft,
        IReadOnlyList<(int?, string, PortAccess, int?, PortOrigin)> Ports, RecreateVerdict Verdict, ContainerChange? Latest, string? Busy, string? WhyNotStart);

    private async Task<PageView> PageAsync(Rig rig, string name, string lanHost)
    {
        var changes = rig.Changes();
        var row = (await changes.Containers.ListAsync()).Rows.Single(r => r.Container.Name == name);
        var raw = await changes.Engine.InspectContainerAsync(name, default) ?? throw new InvalidDataException($"The snapshot has no inspect of {name}.");
        var details = DockerCli.ParseContainerDetails($"[{raw.ToJsonString()}]").Single();
        var notes = await new ContainerPortNotes(rig.Db).ListAsync(name);
        var image = (await changes.Engine.InspectImageAsync(details.ImageId, default))?["Config"]?["ExposedPorts"] is JsonObject declared
            ? declared.Select(p => ContainerPorts.ParseKey(p.Key)).OfType<(int Port, string Protocol)>().Select(p => ContainerPorts.Key(p.Port, p.Protocol)).ToList()
            : [];
        var addresses = (await rig.Book.LoadAsync(lanHost)).For(name) ?? throw new InvalidDataException($"The address book has no {name}.");
        var hostNetwork = addresses.HostNetwork;
        var shown = ContainerAddressing.Shown(addresses.Addresses, row.Role, notes, lanHost, hostNetwork, addresses.Network is null ? null : details.Name, details.Running);
        var draft = ContainerPortsDraft.From(raw, image, notes);
        var plan = await changes.Changer.PlanAsync(name, ContainerPorts.Rules(raw));
        var view = new PageView(row, details, shown, addresses.Network, hostNetwork, draft,
            draft.Rows.Select(r => (r.ContainerPort, r.Protocol, r.Access, r.HostPort, r.Origin)).ToList(), plan.Verdict,
            await new DbChangeJournal(rig.Db).LatestAsync(name, default), await changes.Changer.BusyWithAsync(name), await changes.Changer.WhyNotStartAsync(name));
        output.WriteLine($"{name} ({row.Role}, {row.Title}): addresses {string.Join(" | ", shown.Select(a => $"{a.Kind} {a.Text}{(a.Note is { } n ? $" ({n})" : "")}"))}; " +
            $"network {addresses.Network ?? "none"}; ports {string.Join(" | ", view.Ports)}; image exposes [{string.Join(", ", image)}]; verdict {plan.Verdict.Kind}: {plan.Verdict.Reason ?? "may be recreated."}");
        return view;
    }

    // What Gitberth keeps for containers that the start looks at: port changes, kept networks, port notes, apps' networks.
    private static (int Changes, int Kept, int Notes, int AppNetworks) Kept(Rig rig)
    {
        int Count(string table) => RehearsalSnapshot.Int(RehearsalSnapshot.Query(rig.DbPath, $"""SELECT COUNT(*) AS "n" FROM "{table}" """)[0]["n"]);
        return (Count("ContainerChanges"), Count("KeptNetworks"), Count("ContainerPortNotes"), Count("AppNetworks"));
    }

    // Every deploy row: its ID and status.
    private static List<(int Id, string Status)> Deploys(Rig rig) =>
        RehearsalSnapshot.Query(rig.DbPath, """SELECT "Id", "Status" FROM "Deployments" ORDER BY "Id" """)
            .Select(r => (RehearsalSnapshot.Int(r["Id"]), (string)r["Status"]!)).ToList();

    // The fixture, once the snapshot was read; the migrated copy too, unless only the snapshot is asked for.
    private RehearsalSnapshot Ready(bool needMigration = true)
    {
        if (snapshot.Failure is { } failure) throw new InvalidOperationException($"The snapshot couldn't be read or copied: {failure.Message}", failure);
        if (needMigration && snapshot.MigrationError is { } error) throw new InvalidOperationException($"The migration failed, so nothing after it can be rehearsed: {error.Message}", error);
        return snapshot;
    }

    private static string Pending(RehearsalApp a) => $"ConfigChangePending {a.ConfigChangePending}, RebuildPending {a.RebuildPending}";

    // What 8.5 says each old row becomes, written out from the rows themselves.
    private static List<RehearsalPort> Expected(IEnumerable<Dictionary<string, object?>> before)
    {
        var rows = before.Select(r => (Id: RehearsalSnapshot.Int(r["Id"]), AppId: RehearsalSnapshot.Int(r["AppId"]), Protocol: (string)r["Protocol"]!, HostIp: r["HostIp"] as string,
            HostPort: RehearsalSnapshot.Int(r["HostPort"]), ContainerPort: RehearsalSnapshot.Int(r["ContainerPort"]))).ToList();
        var web = rows.Where(r => r.Protocol == "tcp").GroupBy(r => r.AppId).Select(g => g.Min(r => r.Id)).ToHashSet();
        return rows.OrderBy(r => r.Id).Select(r =>
        {
            var access = r.HostIp is null or "" or "0.0.0.0" or "::" ? "Lan" : r.HostIp is "127.0.0.1" or "::1" ? "HostOnly" : "Custom";
            return new RehearsalPort(r.Id, r.AppId, r.Protocol, access, access == "Custom" ? r.HostIp : null, r.HostPort, r.ContainerPort, web.Contains(r.Id));
        }).ToList();
    }

    // Each value that follows one of the flags in a docker run argv.
    private static List<(string Flag, string Value)> Pairs(IReadOnlyList<string> args, params string[] flags) =>
        args.Select((a, i) => (a, i)).Where(x => flags.Contains(x.a) && x.i + 1 < args.Count).Select(x => (x.a, args[x.i + 1])).ToList();

    // A -p value as the docker CLI reads it (nat.ParsePortSpec): [ip:]host:container[/protocol], an IPv6 ip with or without brackets.
    private static (string Key, string HostIp, string HostPort) DockerPortSpec(string spec)
    {
        var protocol = "tcp";
        var slash = spec.LastIndexOf('/');
        if (slash >= 0) (spec, protocol) = (spec[..slash], spec[(slash + 1)..]);
        var parts = spec.Split(':');
        var (ip, host, container) = parts.Length switch
        {
            1 => ("", "", parts[0]),
            2 => ("", parts[0], parts[1]),
            3 => (parts[0], parts[1], parts[2]),
            _ => (string.Join(':', parts[..^2]), parts[^2], parts[^1]),
        };
        return ($"{container}/{protocol}", ip.Trim('[', ']'), host);
    }

    private static List<(string Key, string HostIp, string HostPort)> LiveBindings(JsonObject inspect) =>
        (inspect["HostConfig"]?["PortBindings"] as JsonObject ?? new JsonObject())
            .SelectMany(p => (p.Value as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(b => (p.Key, RehearsalSnapshot.Text(b, "HostIp"), RehearsalSnapshot.Text(b, "HostPort"))))
            .ToList();

    /// <summary>
    /// base1's CronScheduler.BaseUrl (src/Gitberth/Deploy/CronScheduler.cs:175-182, its MainPort at :290) and the docker ps
    /// parser it used (Docker/DockerCli.cs:478-504), as they were, on the ports as base1's table held them.
    /// </summary>
    private static class Base1
    {
        public sealed record Port(int Id, int ContainerPort, string Protocol);

        public static (string? Url, string? Problem) BaseUrl(IReadOnlyList<Port> ports, string containerName, ContainerSummary? container, bool inContainer)
        {
            if (MainPort(ports) is not { } port) return (null, "The app has no TCP port, so Gitberth can't call its jobs. Add one on the Settings tab.");
            if (container is not { State: "running" }) return (null, "The app's container isn't running.");
            var binding = ParsePortBindings(container.Ports).FirstOrDefault(b => b.Protocol == "tcp" && b.ContainerPort == port.ContainerPort);
            if (binding is null) return (null, $"The running container doesn't publish port {port.ContainerPort} yet. Restart it with the new settings.");
            return (inContainer ? $"http://{containerName}:{port.ContainerPort}" : $"http://localhost:{binding.HostPort}", null);
        }

        private static Port? MainPort(IReadOnlyList<Port> ports) => ports.Where(p => p.Protocol != "udp").OrderBy(p => p.Id).FirstOrDefault();

        private static List<PublishedPort> ParsePortBindings(string ports)
        {
            var list = new List<PublishedPort>();
            foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var arrow = part.IndexOf("->", StringComparison.Ordinal);
                if (arrow < 0) continue;
                var (host, target) = (part[..arrow], part[(arrow + 2)..]);
                var slash = target.IndexOf('/');
                var colon = host.LastIndexOf(':');
                if (slash < 0 || colon < 0 || Range(target[..slash]) is not (var containerFrom, var containerTo) || Range(host[(colon + 1)..]) is not (var hostFrom, var hostTo)
                    || containerTo - containerFrom != hostTo - hostFrom) continue;
                var ip = host[..colon].Trim('[', ']');
                for (var i = 0; i <= hostTo - hostFrom; i++)
                    list.Add(new PublishedPort(ip.Length == 0 ? null : ip, hostFrom + i, containerFrom + i, target[(slash + 1)..]));
            }
            return list.OrderBy(p => p.HostIp?.Contains(':') == true ? 1 : 0).ToList();

            static (int From, int To)? Range(string text)
            {
                var dash = text.IndexOf('-');
                var (from, to) = dash < 0 ? (text, text) : (text[..dash], text[(dash + 1)..]);
                return int.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && int.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= a
                    ? (a, b)
                    : null;
            }
        }
    }

    /// <summary>
    /// Step 3's AppService.Fingerprint and ContainerSpec.BuildRunCommand and ToDisplayString (s3-final, AppService.cs:499-513
    /// and ContainerSpec.cs:55-105), as they were: what orb and kanban must still give with the unified apps.
    /// </summary>
    private static class Step3
    {
        public static (string Runtime, string Build) Fingerprint(AppDefinition a, SecretProtector protector, string shared)
        {
            var env = a.EnvVars.OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => (v.Key, Value: protector.TryUnprotect(v.ProtectedValue), v.IsSecret, v.BuildTime)).ToList();
            var runtime = ContainerSpecFactory.RuntimeKey(a, shared)
                + "#" + string.Join('|', env.Select(e => $"{e.Key}={e.Value}"))
                + "#" + string.Join('|', a.Volumes.Select(v => $"{v.Source}:{v.Target}:{v.ReadOnly}").Order())
                + $"#{a.RestartPolicy}#{a.MemoryLimitMb}#{a.CpuLimit}";
            var build = $"{a.Branch}#{a.Subdirectory}#{a.DockerfileMode}#{(a.DockerfileMode == DockerfileMode.Custom ? a.CustomDockerfile : "")}#{a.ProjectHint}#{a.VercelVariables}"
                + "#" + string.Join('|', env.Where(e => e.BuildTime).Select(e => $"{e.Key}={e.Value}"))
                + "#" + string.Join('|', a.Volumes.Select(v => v.Target).Order());
            return (runtime, build);
        }

        public static List<string> Args(AppDefinition app, string image, IReadOnlyList<EnvValue> env, string network, IReadOnlyList<string> logOptions, bool gatewayPriority,
            bool lanIpv4Only)
        {
            var args = new List<string> { "run", "-d", "--name", $"gb-{app.Slug}", "--restart", app.RestartPolicy };
            foreach (var join in ContainerSpecFactory.Joins(app, network, app.Networks)) args.AddRange(["--network", ContainerSpec.NetworkArg(join, gatewayPriority)]);
            foreach (var option in logOptions) args.AddRange(["--log-opt", option]);
            foreach (var (key, value) in new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = app.Slug }) args.AddRange(["--label", $"{key}={value}"]);
            foreach (var port in app.Ports.OrderBy(p => p.Id)) args.AddRange(ContainerSpec.PortArg(port, lanIpv4Only));
            foreach (var e in env)
            {
                args.Add("-e");
                args.Add(e.IsSecret && EnvPassing.CanPassViaEnvironment(e.Key) ? e.Key : $"{e.Key}={e.Value}");
            }
            foreach (var v in app.Volumes.OrderBy(v => v.Id)) args.AddRange(["-v", $"{VolumeNames.Resolve(app.Slug, v.Source)}:{v.Target}{(v.ReadOnly ? ":ro" : "")}"]);
            if (app.MemoryLimitMb is > 0) args.AddRange(["--memory", $"{app.MemoryLimitMb}m"]);
            if (app.CpuLimit is > 0) args.AddRange(["--cpus", app.CpuLimit.Value.ToString("0.##", CultureInfo.InvariantCulture)]);
            args.Add(image);
            return args;
        }

        public static string Display(List<string> args, IReadOnlyList<EnvValue> env)
        {
            var secrets = env.Where(e => e.IsSecret).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var sb = new StringBuilder("docker");
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (i > 0 && args[i - 1] == "-e")
                {
                    var key = arg.Split('=')[0];
                    if (secrets.Contains(key)) arg = key + "=••••";
                }
                var breakLine = (arg.StartsWith('-') && arg.Length > 1 && i > 2) || i == args.Count - 1;
                sb.Append(breakLine ? " \\\n  " : " ").Append(Quote(arg));
            }
            return sb.ToString();
        }

        private static string Quote(string arg) =>
            arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@,+%•".Contains(c)) ? arg : "'" + arg.Replace("'", "'\\''") + "'";
    }

    /// <summary>The containers Gitberth didn't deploy and their port changes, as the rig wires them (<see cref="Rig.Changes"/>).</summary>
    private sealed record RigChanges(IContainerEngine Engine, ContainerService Containers, PortChanger Changer, HostHelpers Helpers, NetworkKeeper Keeper);

    /// <summary>
    /// Gitberth's services as the upgraded app wires them (Program.cs), on a copy of the migrated database of their own,
    /// with Docker and Cloudflare answered from the snapshot. Gitberth here can read its Cloudflare token, as the live one
    /// can with its key ring: the copy's token is replaced by a stand-in that only this rig's key ring opens. The live
    /// key ring isn't in the snapshot, so every other secret (environment values included) reads as empty.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly ITestOutputHelper _output;
        private int _helperRuns;

        private Rig(RehearsalSnapshot snapshot, ITestOutputHelper output)
        {
            Snapshot = snapshot;
            _output = output;
            Dir = Path.Combine(snapshot.TempDir, "rig-" + Guid.NewGuid().ToString("N")[..8]);
            DbPath = Path.Combine(Dir, "gitberth.db");
            // Gitberth's own container is the live one, by its ID as the live Gitberth reads it from its mounts; this test
            // runs in a container of its own, whose mounts name that one.
            Options = new GitberthOptions
            {
                DataDir = Dir,
                RunningInContainer = true,
                OwnContainer = snapshot.InspectOf("gitberth") is { } self ? RehearsalSnapshot.Text(self, "Id") : null,
            };
            Db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={DbPath}").Options);
            Protector = new SecretProtector(new EphemeralDataProtectionProvider());
            Settings = new SettingsService(Db, Protector);
            Cli = new DockerCli(Options) { Runner = RunDockerAsync };
            Api = new DockerApi("unix:///var/run/docker.sock", new Engine(this));
            HostPortMap? map = null;
            Domains = new DomainService(Db, Settings, new CloudflareApi(new HttpFactory(new Cloudflare(this))), new TunnelLocator(Cli), Cli, Logger<DomainService>(),
                new Lazy<HostPortMap>(() => map!),
                new Lazy<AppActions>(() => throw new InvalidOperationException("Something asked for AppActions to recreate an app.")));
            // As Program.cs wires it: joins and leaves wait for nothing while a port change is under way.
            Net = new NetworkService(Api, Db, Options, Locks, new AppLocks(), new Lazy<PortChanger>(() => Changes().Changer));
            Scanner = new HostScanner(Cli, Options, Logger<HostScanner>());
            map = HostPorts = new HostPortMap(Net, Db, Domains, Scanner, Settings, new ContainerEventWatcher(Api, new StatsStore(Options), Logger<ContainerEventWatcher>()),
                Logger<HostPortMap>());
            Health = new HealthSettings(Settings);
            Restarts = new RestartSchedules(Settings);
            Apps = new AppService(Db, Protector, Cli, Settings, Health, Domains, Options, Restarts, Watch, Scanner, HostPorts, Net);
            Book = new AddressBook(Net, Apps, Domains, Db);
            Ingress = RehearsalTunnel.LiveIngress();
        }

        public RehearsalSnapshot Snapshot { get; }
        public string Dir { get; }
        public string DbPath { get; }
        public GitberthOptions Options { get; }
        public IDbContextFactory<GitberthDb> Db { get; }
        public SecretProtector Protector { get; }
        public SettingsService Settings { get; }
        public DockerCli Cli { get; }
        public DockerApi Api { get; }
        public DomainService Domains { get; }
        public NetworkService Net { get; }
        public HostScanner Scanner { get; }
        public HostPortMap HostPorts { get; }
        public HealthSettings Health { get; }
        public RestartSchedules Restarts { get; }
        public WatchState Watch { get; } = new();
        public AppService Apps { get; }
        public AddressBook Book { get; }
        public ContainerLocks Locks { get; } = new();

        /// <summary>The tunnel's routes, as they are in Cloudflare today.</summary>
        public JsonArray Ingress { get; }

        /// <summary>A stand-in account; the tunnel is the one the live token was saved for (a setting's value, never printed).</summary>
        public string AccountId { get; } = "0123456789abcdef0123456789abcdef";
        public string TunnelId { get; private set; } = "";

        public List<string> DockerCalls { get; } = [];
        public List<string> EngineCalls { get; } = [];
        public List<string> CloudflareCalls { get; } = [];
        public List<string> Mutations { get; } = [];
        public List<string> CloudflareWrites { get; } = [];
        public List<string> Unanswered { get; } = [];
        public List<string> Logs { get; } = [];

        /// <summary>Gitberth joining its own container to the shared network, as every start does: the one change it makes in Docker.</summary>
        public List<string> OwnJoins { get; } = [];

        private (AppLocks Locks, DeployPipeline Pipeline, AppActions Actions)? _deploys;

        /// <summary>
        /// The deploy pipeline, its queue and the app actions, wired as Program.cs wires them, with the queue stopped: a
        /// deploy anything asks for is only recorded as a deploy row, never run.
        /// </summary>
        private (AppLocks Locks, DeployPipeline Pipeline, AppActions Actions) Deploys()
        {
            if (_deploys is { } built) return built;
            var http = new HttpFactory(new Cloudflare(this));
            var locks = new AppLocks();
            var hub = new LiveLogHub();
            var repoVariables = new RepoVariables(new GitHubApi(http), Settings);
            var pipeline = new DeployPipeline(Db, new GitClient(Options, Settings), Cli, new ProjectAnalyzer(), locks, hub, Protector, Options, http, repoVariables,
                Domains, Watch, new Stopped(), Logger<DeployPipeline>(), HostPorts, Scanner);
            var queue = new DeployQueue(pipeline, hub, Db, new Stopped(), Logger<DeployQueue>());
            var actions = new AppActions(Db, queue, Cli, locks, Options, repoVariables, Domains, Restarts, Apps, Logger<AppActions>());
            _deploys = (locks, pipeline, actions);
            return _deploys.Value;
        }

        /// <summary>Deploy, start, stop and the Network tab's Save and Apply, with the deploy queue stopped.</summary>
        public AppActions Actions => Deploys().Actions;

        private RigChanges? _changes;

        /// <summary>
        /// The containers Gitberth didn't deploy and their port changes, wired as Program.cs wires them: the containers'
        /// list and actions, the recreate engine with its journal in the rig's database, the helpers, and the keeper of
        /// the networks Gitberth keeps containers on.
        /// </summary>
        public RigChanges Changes()
        {
            if (_changes is { } built) return built;
            var engine = new DockerEngine(Api);
            var helpers = new HostHelpers(engine, Options, Logger<HostHelpers>());
            PortChanger? changer = null;
            var events = new ContainerEventWatcher(Api, new StatsStore(Options), Logger<ContainerEventWatcher>());
            var containers = new ContainerService(Cli, Apps, Actions, new ContainerTitles(Settings),
                new ContainerStatsCollector(Api, Health, new StatsStore(Options), events, Logger<ContainerStatsCollector>()), Options, Db, Locks,
                new Lazy<PortChanger>(() => changer!), Net);
            changer = new PortChanger(engine, new DbChangeJournal(Db), Locks,
                new HostPortChecks(HostPorts, Scanner, Settings, Domains, new CloudflareApi(new HttpFactory(new Cloudflare(this))), Options, Logger<HostPortChecks>()),
                helpers, helpers, new NameCheck(engine, Options), containers, Options, Logger<PortChanger>());
            return (_changes = new RigChanges(engine, containers, changer, helpers, new NetworkKeeper(Net, Api, events, Locks, Db, Logger<NetworkKeeper>())));
        }

        /// <summary>
        /// The one list of apps, wired as the integrator wires it (8.5): its titles loaded as at a start, and no update badges
        /// (UpdateService is another builder's; without it the list has none, never an error).
        /// </summary>
        public async Task<Gitberth.Apps.AppCatalog> CatalogAsync()
        {
            var store = new StatsStore(Options);
            var events = new ContainerEventWatcher(Api, store, Logger<ContainerEventWatcher>());
            var titles = new ContainerTitles(Settings);
            await titles.LoadAsync();
            _activity = new Gitberth.Apps.ActivityLog(Db, store);
            return new Gitberth.Apps.AppCatalog(Db, Net, Book, Apps, new ContainerStatsCollector(Api, Health, store, events, Logger<ContainerStatsCollector>()), titles, Health,
                Restarts, new DbChangeJournal(Db), Cli, Domains, Protector, Options, _activity,
                new Lazy<Gitberth.Images.UpdateService>(() => throw new InvalidOperationException("No update checks in the rehearsal.")), Logger<Gitberth.Apps.AppCatalog>());
        }

        private Gitberth.Apps.ActivityLog? _activity;

        /// <summary>AppSync, as it hears each container's start from the event watcher.</summary>
        public Gitberth.Apps.AppSync Sync(Gitberth.Apps.AppCatalog catalog) =>
            new(new ContainerEventWatcher(Api, new StatsStore(Options), Logger<ContainerEventWatcher>()), catalog, Db, new DbChangeJournal(Db), Net,
                _activity ?? new Gitberth.Apps.ActivityLog(Db, new StatsStore(Options)), Logger<Gitberth.Apps.AppSync>());

        /// <summary>The start's housekeeping, wired as Program.cs wires it, with the deploy queue stopped.</summary>
        public StartupTasks Startup()
        {
            Directory.CreateDirectory(Options.WorkDir);
            var (locks, pipeline, actions) = Deploys();
            var changes = Changes();
            return new StartupTasks(Db, Cli, locks, actions, pipeline, Options, Logger<StartupTasks>(), changes.Engine, changes.Changer, changes.Helpers, changes.Keeper);
        }

        /// <summary>Gitberth is stopping, so nothing takes a deploy off the queue and runs it.</summary>
        private sealed class Stopped : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => new(canceled: true);
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        public static async Task<Rig> CreateAsync(RehearsalSnapshot snapshot, ITestOutputHelper output)
        {
            var rig = new Rig(snapshot, output);
            Directory.CreateDirectory(rig.Dir);
            RehearsalSnapshot.CopyDatabase(snapshot.MigratedDb, rig.DbPath);
            // Never printed. Its format is checked, as Gitberth's own tunnel lookup checks the token's.
            var saved = await rig.Settings.GetAsync(SettingKeys.CloudflareTunnelId);
            rig.TunnelId = Guid.TryParse(saved, out var id) ? id.ToString("D") : "c0ffee00-0000-4000-8000-000000000001";
            await rig.Settings.SetSecretAsync(SettingKeys.CloudflareToken, "rehearsal-stand-in-token");
            // As Program.cs does before it serves a page.
            await rig.Health.LoadAsync();
            await rig.Restarts.LoadAsync();
            return rig;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
            }
            return ValueTask.CompletedTask;
        }

        public async Task<(bool ConfigChangePending, bool RebuildPending)> FlagsAsync(int appId)
        {
            await using var db = await Db.CreateDbContextAsync();
            var app = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == appId);
            return (app.ConfigChangePending, app.RebuildPending);
        }

        /// <summary>Nothing was changed in Docker or Cloudflare, and nothing was logged as an error. Prints what was asked.</summary>
        public void AssertNothingChanged()
        {
            lock (_gate)
            {
                _output.WriteLine($"Asked: {DockerCalls.Count} docker commands (the host-scan helper {_helperRuns} times), {EngineCalls.Count} engine calls, {CloudflareCalls.Count} Cloudflare calls.");
                _output.WriteLine("  docker: " + string.Join(" | ", DockerCalls.Distinct().Take(40)));
                _output.WriteLine("  Cloudflare: " + string.Join(" | ", CloudflareCalls.Distinct().Take(40)));
                foreach (var line in Unanswered.Distinct()) _output.WriteLine("  not in the snapshot, answered as missing: " + line);
                foreach (var line in Logs) _output.WriteLine("  log: " + line);
                Assert.True(Mutations.Count == 0, "Refused changes: " + string.Join(" | ", Mutations));
                Assert.True(CloudflareWrites.Count == 0, "Cloudflare writes: " + string.Join(" | ", CloudflareWrites));
                Assert.DoesNotContain(Logs, l => l.StartsWith("Error", StringComparison.Ordinal) || l.StartsWith("Critical", StringComparison.Ordinal));
            }
        }

        /// <summary>cloudflare-tunnel's inspect from the snapshot, with a stand-in token for the tunnel the live one runs (the snapshot leaves its arguments out).</summary>
        public JsonObject CloudflaredInspect()
        {
            var inspect = (JsonObject)(Snapshot.InspectOf("cloudflare-tunnel") ?? throw new InvalidDataException("The snapshot has no inspect of cloudflare-tunnel.")).DeepClone();
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c3RhbmQtaW4="}"""));
            inspect["Args"] = new JsonArray("tunnel", "--no-autoupdate", "run", "--token", token);
            return inspect;
        }

        /// <summary>
        /// An image's inspect (GET images/{name}/json), from the inspect of the container that runs it, as the CLI's image
        /// inspect here has it: its ID and tag, and the ports, volumes and labels it declares; null for an image no container
        /// in the snapshot runs.
        /// </summary>
        public JsonObject? ImageOf(string nameOrId)
        {
            var owner = Snapshot.Inspects.Values.FirstOrDefault(i => RehearsalSnapshot.Text(i["Config"], "Image") == nameOrId || RehearsalSnapshot.Text(i, "Image") == nameOrId);
            if (owner?["Config"] is not JsonObject config) return null;
            return new JsonObject
            {
                ["Id"] = RehearsalSnapshot.Text(owner, "Image"),
                ["RepoTags"] = new JsonArray(RehearsalSnapshot.Text(config, "Image")),
                ["Config"] = new JsonObject
                {
                    ["ExposedPorts"] = config["ExposedPorts"]?.DeepClone(),
                    ["Volumes"] = config["Volumes"]?.DeepClone(),
                    ["Labels"] = config["Labels"]?.DeepClone(),
                },
            };
        }

        private void Note(List<string> list, string line)
        {
            lock (_gate) list.Add(line);
        }

        private ILogger<T> Logger<T>() => new Collector<T>(this);

        private sealed class Collector<T>(Rig rig) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                rig.Note(rig.Logs, $"{logLevel} {typeof(T).Name}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
            }
        }

        private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
        {
            public GitberthDb CreateDbContext() => new(options);
        }

        private sealed class HttpFactory(HttpMessageHandler cloudflare) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => name == "cloudflare"
                ? new HttpClient(cloudflare, disposeHandler: false)
                : throw new InvalidOperationException($"The {name} HTTP client was asked for: the rehearsal reaches no network.");
        }

        // The docker CLI, answered from the snapshot. Anything that would change something is refused and recorded.
        private Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            string[] a = [.. args];
            if (a is ["run", ..] && a.Contains($"{HostScanHelper.Label}=ports"))
            {
                Note(DockerCalls, "run (the host-scan helper)");
                Interlocked.Increment(ref _helperRuns);
                return Task.FromResult(Ok(HelperOutput()));
            }
            var line = string.Join(' ', a);
            Note(DockerCalls, line);
            // Gitberth's own new container joins the shared network at every start (StartupTasks), by its ID; nothing else may be joined.
            if (a is ["network", "connect", .., var network, var self] && network == Options.Network && self == Options.OwnContainer)
            {
                Note(OwnJoins, "docker " + line);
                return Task.FromResult(Ok(""));
            }
            return Task.FromResult(Answer(a, line));
        }

        private static ProcessResult Ok(string output) => new(0, output, "");

        private static ProcessResult Fail(string error) => new(1, "", error);

        private ProcessResult Answer(string[] a, string line)
        {
            switch (a)
            {
                case ["version", "--format", "{{.Server.APIVersion}}"]:
                    return Ok($"{Snapshot.Api}\n");
                case ["version", "--format", "{{json .}}"]:
                    return Ok(new JsonObject
                    {
                        ["Client"] = new JsonObject { ["Version"] = Snapshot.EngineVersion },
                        ["Server"] = new JsonObject { ["Version"] = Snapshot.EngineVersion, ["ApiVersion"] = Snapshot.Api.ToString() },
                    }.ToJsonString() + "\n");
                case ["info", "--format", "{{.LoggingDriver}}"]:
                    return Ok("json-file\n");
                case ["ps", .. var rest]:
                    return Ps(rest, line);
                case ["container", "inspect", "--format", var format, .. var names]:
                    return InspectFormatted(format, names, line);
                case ["container", "inspect", .. var names] when names.Length > 0:
                    return InspectAll(names);
                case ["image", "inspect", "--format", var format, var image]:
                    return ImageInspect(format, image, line);
                case ["network", "inspect", "--format", "{{.Name}}", var network]:
                    return Snapshot.Networks.OfType<JsonObject>().Any(n => RehearsalSnapshot.Text(n, "Name") == network) ? Ok(network + "\n") : Fail($"Error response from daemon: network {network} not found");
                case ["logs", ..]:
                    return Ok("");
            }
            if (ReadOnly(a))
            {
                Note(Unanswered, "docker " + line);
                return Fail("Error: not in the rehearsal's snapshot");
            }
            Note(Mutations, "docker " + line);
            return Fail("Error: refused, the rehearsal never changes a container, network, volume or image");
        }

        private static bool ReadOnly(string[] a) => a switch
        {
            ["ps" or "inspect" or "version" or "info" or "logs" or "images", ..] => true,
            ["container" or "image" or "network" or "volume", "inspect" or "ls", ..] => true,
            ["cp", var from, "-"] => from.Contains(':'),
            _ => false,
        };

        private ProcessResult Ps(string[] rest, string line)
        {
            if (rest.Contains("-q") || rest.Contains("-aq"))
            {
                // No host-scan helper was left behind.
                if (rest.Contains($"label={HostScanHelper.Label}=ports")) return Ok("");
                if (Filter(rest, "network") is { } network)
                    return Ok(string.Concat(Snapshot.Ps.Where(r => RehearsalSnapshot.Text(r, "State") == "running" && RehearsalSnapshot.Text(r, "Networks").Split(',').Contains(network))
                        .Select(r => RehearsalSnapshot.Text(r, "ID") + "\n")));
                Note(Unanswered, "docker " + line);
                return Ok("");
            }
            var managed = rest.Contains("label=gitberth.managed=true");
            return Ok(string.Concat(Snapshot.Ps.Zip(Snapshot.PsLines)
                .Where(x => !managed || DockerCli.ParseLabels(RehearsalSnapshot.Text(x.First, "Labels")).GetValueOrDefault("gitberth.managed") == "true")
                .Select(x => x.Second + "\n")));
        }

        private static string? Filter(string[] args, string key)
        {
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--filter" && args[i + 1].StartsWith(key + "=", StringComparison.Ordinal)) return args[i + 1][(key.Length + 1)..];
            return null;
        }

        private ProcessResult InspectFormatted(string format, string[] names, string line)
        {
            switch (format)
            {
                case "{{.Id}}":
                    return names is [var name] && (Snapshot.InspectOf(name) is { } i ? RehearsalSnapshot.Text(i, "Id") : Snapshot.PsRow(name) is { } r ? RehearsalSnapshot.Text(r, "ID") : null) is { } id
                        ? Ok(id + "\n")
                        : Fail($"Error: No such container: {string.Join(' ', names)}");
                case "{{.Image}}":
                    // Gitberth's own container, which the host-scan helper runs the image of.
                    return Ok(RehearsalSnapshot.Text(Snapshot.InspectOf("gitberth"), "Image") + "\n");
                case "{{.State.Running}} {{.State.StartedAt}}":
                    return names is [var started] && Snapshot.InspectOf(started) is { } s
                        ? Ok($"{(s["State"]?["Running"]?.GetValue<bool>() == true ? "true" : "false")} {RehearsalSnapshot.Text(s["State"], "StartedAt")}\n")
                        : Fail($"Error: No such container: {string.Join(' ', names)}");
                case CronScheduler.SecretFormat:
                    // The snapshot has no environment values.
                    return names is [var app] && Snapshot.InspectOf(app) is not null ? Ok("") : Fail($"Error: No such container: {string.Join(' ', names)}");
                case var _ when format.StartsWith("""{"Id":""", StringComparison.Ordinal):
                    return Ok(string.Concat(names.Select(Snapshot.InspectOf).OfType<JsonObject>().Select(i => new JsonObject
                    {
                        ["Id"] = RehearsalSnapshot.Text(i, "Id"),
                        ["Name"] = RehearsalSnapshot.Text(i, "Name"),
                        ["NetworkSettings"] = new JsonObject { ["Networks"] = i["NetworkSettings"]?["Networks"]?.DeepClone() },
                    }.ToJsonString() + "\n")));
                default:
                    Note(Unanswered, "docker " + line);
                    return Fail("Error: not in the rehearsal's snapshot");
            }
        }

        private ProcessResult InspectAll(string[] names)
        {
            var found = names.Select(n => n is "cloudflare-tunnel" ? CloudflaredInspect() : Snapshot.InspectOf(n)?.DeepClone() as JsonObject).OfType<JsonObject>().ToList();
            var json = new JsonArray([.. found.Select(f => (JsonNode?)f)]).ToJsonString();
            return found.Count == names.Length ? Ok(json) : new ProcessResult(1, json, "Error: No such container: " + string.Join(' ', names.Where(n => Snapshot.InspectOf(n) is null)));
        }

        // An image's inspect, from the inspect of the container that runs it: its labels are the image's plus Gitberth's own.
        private ProcessResult ImageInspect(string format, string image, string line)
        {
            var owner = Snapshot.Inspects.Values.FirstOrDefault(i => RehearsalSnapshot.Text(i["Config"], "Image") == image || RehearsalSnapshot.Text(i, "Image") == image);
            if (owner is null) return Fail($"Error response from daemon: No such image: {image}");
            var labels = owner["Config"]?["Labels"] as JsonObject ?? new JsonObject();
            if (format == "{{json .Config.Labels}}") return Ok(labels.ToJsonString() + "\n");
            if (format == "{{.Id}}") return Ok(RehearsalSnapshot.Text(owner, "Image") + "\n");
            if (Regex.Match(format, """^\{\{index \.Config\.Labels "(.+)"\}\}$""") is { Success: true } m)
                return Ok((RehearsalSnapshot.Text(labels, m.Groups[1].Value) is { Length: > 0 } value ? value : "<no value>") + "\n");
            Note(Unanswered, "docker " + line);
            return Fail("Error: not in the rehearsal's snapshot");
        }

        // What the host-scan helper prints from the host's /proc (HostScanHelper's script): each file after its name line,
        // only listening sockets of the TCP tables, and only the name line of a file the snapshot doesn't have.
        private string HelperOutput()
        {
            var sb = new StringBuilder();
            foreach (var file in HostScanHelper.Files)
            {
                sb.Append("==> ").Append(file).Append(" <==\n");
                if (!Snapshot.Proc.TryGetValue(file, out var text)) continue;
                var tcp = file.EndsWith("/tcp", StringComparison.Ordinal) || file.EndsWith("/tcp6", StringComparison.Ordinal);
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (tcp)
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4 || parts[3] != "0A") continue;
                        line = string.Join(' ', parts);
                    }
                    sb.Append(line).Append('\n');
                }
            }
            return sb.ToString();
        }

        // The Engine API, answered from the snapshot: GETs only.
        private sealed class Engine(Rig rig) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                var line = $"{request.Method} {uri.PathAndQuery}";
                rig.Note(rig.EngineCalls, line);
                if (request.Method != HttpMethod.Get)
                {
                    rig.Note(rig.Mutations, "engine " + line);
                    return Task.FromResult(Json(HttpStatusCode.InternalServerError, """{"message":"refused: the rehearsal never changes anything"}"""));
                }
                var path = uri.AbsolutePath;
                if (path == "/version")
                    return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject { ["Version"] = rig.Snapshot.EngineVersion, ["ApiVersion"] = rig.Snapshot.Api.ToString() }.ToJsonString()));
                var rest = Regex.Match(path, @"^/v\d+\.\d+/(.+)$") is { Success: true } m ? m.Groups[1].Value : path.TrimStart('/');
                string? body = rest switch
                {
                    "containers/json" => rig.Snapshot.ContainerList,
                    "networks" => rig.Snapshot.Networks.ToJsonString(),
                    _ when rest.StartsWith("containers/", StringComparison.Ordinal) && rest.EndsWith("/json", StringComparison.Ordinal)
                        => rig.Snapshot.InspectOf(Uri.UnescapeDataString(rest["containers/".Length..^"/json".Length]))?.ToJsonString(),
                    _ when rest.StartsWith("images/", StringComparison.Ordinal) && rest.EndsWith("/json", StringComparison.Ordinal)
                        => rig.ImageOf(Uri.UnescapeDataString(rest["images/".Length..^"/json".Length]))?.ToJsonString(),
                    _ when rest.StartsWith("networks/", StringComparison.Ordinal)
                        => rig.Snapshot.Networks.OfType<JsonObject>().FirstOrDefault(n => RehearsalSnapshot.Text(n, "Name") == Uri.UnescapeDataString(rest["networks/".Length..])
                            || RehearsalSnapshot.Text(n, "Id").StartsWith(Uri.UnescapeDataString(rest["networks/".Length..]), StringComparison.Ordinal))?.ToJsonString(),
                    _ => null,
                };
                if (body is null && !(rest.StartsWith("containers/", StringComparison.Ordinal) && rest.EndsWith("/json", StringComparison.Ordinal)))
                    rig.Note(rig.Unanswered, "engine " + line);
                return Task.FromResult(body is null
                    ? Json(HttpStatusCode.NotFound, """{"message":"No such object (not in the rehearsal's snapshot)"}""")
                    : Json(HttpStatusCode.OK, body));
            }
        }

        // Cloudflare's API, answered with the tunnel's routes as they are today and DNS records that already send each
        // domain to the tunnel. Every write is recorded (and answered, so it's recorded once), and fails the test.
        private sealed class Cloudflare(Rig rig) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                var path = uri.AbsolutePath.StartsWith("/client/v4/", StringComparison.Ordinal) ? uri.AbsolutePath["/client/v4/".Length..] : uri.AbsolutePath;
                var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.Ordinal);
                // The tunnel's ID is a setting's value: it's never printed.
                var line = $"{request.Method} {path}{uri.Query}".Replace(rig.TunnelId, "<tunnel>", StringComparison.Ordinal);
                rig.Note(rig.CloudflareCalls, line);
                if (request.Method != HttpMethod.Get)
                {
                    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                    rig.Note(rig.CloudflareWrites, $"{line} {body.Replace(rig.TunnelId, "<tunnel>", StringComparison.Ordinal)}");
                    return Success(new JsonObject { ["version"] = 1000 });
                }
                if (path == $"accounts/{rig.AccountId}/cfd_tunnel/{rig.TunnelId}/configurations")
                    return Success(new JsonObject { ["tunnel_id"] = rig.TunnelId, ["version"] = 41, ["config"] = new JsonObject { ["ingress"] = rig.Ingress.DeepClone() } });
                if (path == "zones" && query.TryGetValue("name", out var name))
                    return Success(RehearsalTunnel.Zones.Contains(name) ? new JsonArray(new JsonObject { ["id"] = "zone-" + name, ["name"] = name }) : new JsonArray());
                if (path.StartsWith("zones/zone-", StringComparison.Ordinal) && path.EndsWith("/dns_records", StringComparison.Ordinal) && query.TryGetValue("name", out var host))
                    return Success(new JsonArray(new JsonObject
                    {
                        ["id"] = "record-" + host, ["type"] = "CNAME", ["name"] = host, ["content"] = $"{rig.TunnelId}.cfargotunnel.com", ["proxied"] = true, ["ttl"] = 1,
                    }));
                if (path == "user/tokens/verify") return Success(new JsonObject { ["status"] = "active" });
                rig.Note(rig.Unanswered, "cloudflare " + line);
                return Json(HttpStatusCode.NotFound, """{"success":false,"errors":[{"code":7003,"message":"Not in the rehearsal"}]}""");
            }

            private static HttpResponseMessage Success(JsonNode result) =>
                Json(HttpStatusCode.OK, new JsonObject { ["success"] = true, ["errors"] = new JsonArray(), ["result"] = result }.ToJsonString());
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// The live tunnel's routes, as the owner's brief gives them, in the order cloudflared last logged them (configuration
/// version 24, 2026-11-06 03:43:51 UTC). Cloudflare itself is never asked.
/// </summary>
internal static class RehearsalTunnel
{
    /// <summary>The Cloudflare zones the domains live in.</summary>
    public static readonly IReadOnlySet<string> Zones = new HashSet<string>(StringComparer.Ordinal) { "orchardlane.com", "harborlab.com" };

    /// <summary>app.harborlab.com, www and harborlab.com to http://localhost:3001, orchardlane.com and www to http://localhost:3000, then the catch-all.</summary>
    public static JsonArray LiveIngress() => new(
        Rule("app.harborlab.com", "http://localhost:3001"),
        Rule("www.harborlab.com", "http://localhost:3001"),
        Rule("harborlab.com", "http://localhost:3001"),
        Rule("orchardlane.com", "http://localhost:3000"),
        Rule("www.orchardlane.com", "http://localhost:3000"),
        new JsonObject { ["service"] = "http_status:404" });

    /// <summary>Where the route for a host name goes.</summary>
    public static string? ServiceOf(JsonArray ingress, string hostname) =>
        ingress.OfType<JsonObject>().FirstOrDefault(r => RehearsalSnapshot.Text(r, "hostname") == hostname) is { } rule ? RehearsalSnapshot.Text(rule, "service") : null;

    private static JsonObject Rule(string hostname, string service) => new() { ["hostname"] = hostname, ["service"] = service };
}
