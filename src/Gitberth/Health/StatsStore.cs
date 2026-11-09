using Microsoft.Data.Sqlite;

namespace Gitberth.Health;

/// <summary>
/// The Health page's readings and container events on disk, in a small database of their own next to Gitberth's
/// (stats.db), so a restart of Gitberth doesn't wipe the charts. Only the last hours are kept. Nothing in it is precious:
/// callers log a failure and carry on without it.
/// </summary>
public sealed class StatsStore(GitberthOptions options)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataDir, "stats.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    // One writer at a time: the sampler every 5 seconds, the event watcher now and then.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _created;

    public async Task AddPointsAsync(IReadOnlyList<(string Container, StatsPoint Point)> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR IGNORE INTO points (container, t, cpu, rx, tx, mem) VALUES ($container, $t, $cpu, $rx, $tx, $mem)";
            var container = cmd.Parameters.Add("$container", SqliteType.Text);
            var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            var cpu = cmd.Parameters.Add("$cpu", SqliteType.Real);
            var rx = cmd.Parameters.Add("$rx", SqliteType.Real);
            var tx = cmd.Parameters.Add("$tx", SqliteType.Real);
            var mem = cmd.Parameters.Add("$mem", SqliteType.Real);
            foreach (var (name, p) in points)
            {
                container.Value = name;
                t.Value = p.Time.Ticks;
                cpu.Value = p.Cpu;
                rx.Value = (object?)p.RxRate ?? DBNull.Value;
                tx.Value = (object?)p.TxRate ?? DBNull.Value;
                mem.Value = (object?)p.Memory ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every container's readings from <paramref name="since"/> on, oldest first.</summary>
    public async Task<Dictionary<string, List<StatsPoint>>> LoadPointsAsync(DateTime since, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<StatsPoint>>(StringComparer.Ordinal);
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT container, t, cpu, rx, tx, mem FROM points WHERE t >= $since ORDER BY container, t";
            cmd.Parameters.AddWithValue("$since", since.Ticks);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!result.TryGetValue(name, out var list)) result[name] = list = [];
                list.Add(new StatsPoint(new DateTime(reader.GetInt64(1), DateTimeKind.Utc), reader.GetDouble(2),
                    Real(reader, 3), Real(reader, 4), Real(reader, 5)));
            }
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }

    public async Task AddEventsAsync(IReadOnlyList<ContainerEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO events (t, id, action, name, exit_code, signal, deployment, deploy_trigger)
                VALUES ($t, $id, $action, $name, $exit, $signal, $deployment, $trigger)
                """;
            foreach (var e in events)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$t", e.Time.Ticks);
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.Parameters.AddWithValue("$action", e.Action);
                cmd.Parameters.AddWithValue("$name", e.Name);
                cmd.Parameters.AddWithValue("$exit", (object?)e.ExitCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$signal", (object?)e.Signal ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$deployment", (object?)e.Deployment ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$trigger", (object?)e.Trigger ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The events from <paramref name="since"/> on, oldest first.</summary>
    public async Task<List<ContainerEvent>> LoadEventsAsync(DateTime since, CancellationToken ct = default)
    {
        var result = new List<ContainerEvent>();
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT t, id, action, name, exit_code, signal, deployment, deploy_trigger FROM events WHERE t >= $since ORDER BY t";
            cmd.Parameters.AddWithValue("$since", since.Ticks);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new ContainerEvent(new DateTime(reader.GetInt64(0), DateTimeKind.Utc), reader.GetString(1), reader.GetString(3), reader.GetString(2),
                    Int(reader, 4), Int(reader, 5), Int(reader, 6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }

    /// <summary>Forgets readings older than <paramref name="pointsBefore"/> and events older than <paramref name="eventsBefore"/>.</summary>
    public async Task PruneAsync(DateTime pointsBefore, DateTime eventsBefore, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM points WHERE t < $points; DELETE FROM events WHERE t < $events";
            cmd.Parameters.AddWithValue("$points", pointsBefore.Ticks);
            cmd.Parameters.AddWithValue("$events", eventsBefore.Ticks);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        // NORMAL is safe with a write-ahead log and skips a disk flush on every write; a crash loses at most the last readings.
        cmd.CommandText = _created ? "PRAGMA synchronous = NORMAL" : """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS points (
                container TEXT NOT NULL, t INTEGER NOT NULL, cpu REAL NOT NULL, rx REAL, tx REAL, mem REAL,
                PRIMARY KEY (container, t)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS points_by_time ON points (t);
            CREATE TABLE IF NOT EXISTS events (
                t INTEGER NOT NULL, id TEXT NOT NULL, action TEXT NOT NULL, name TEXT NOT NULL,
                exit_code INTEGER, signal INTEGER, deployment INTEGER, deploy_trigger TEXT,
                PRIMARY KEY (t, id, action)) WITHOUT ROWID;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _created = true;
        return db;
    }

    private static double? Real(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private static int? Int(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
}
