using Microsoft.Data.Sqlite;

namespace Gitberth.Health;

/// <summary>A roll-up's step, stamped at its end: CPU and memory averages and peaks, network averages (null when unknown).</summary>
public readonly record struct RolledPoint(DateTime Time, double Cpu, double CpuMax, double? Memory, double? MemoryMax, double? RxRate, double? TxRate)
{
    public StatsPoint Average => new(Time, Cpu, RxRate, TxRate, Memory);
}

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

    // ---- builder A: longer history, as roll-ups of the readings ----

    /// <summary>The 5-minute roll-ups, kept 14 days; the 1-hour ones, kept 90 days. The readings themselves stay 6 hours.</summary>
    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5), OneHour = TimeSpan.FromHours(1);

    public static readonly TimeSpan FiveMinutesKept = TimeSpan.FromDays(14), OneHourKept = TimeSpan.FromDays(90);

    /// <summary>
    /// Rolls up what's finished: the readings into 5-minute steps (CPU and memory averages and peaks, network averages), and
    /// those into 1-hour steps, each stamped at the end of its step. Steps are fixed to the clock, and the last few are
    /// worked out again each time (late readings land in them), so running it often, or after a long pause, gives the same.
    /// Then each kind is pruned to how long it's kept.
    /// </summary>
    public async Task RollUpAsync(DateTime now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            await RollAsync(db, transaction, "points", "points_5m", FiveMinutes, now, now - ContainerStatsCollector.Kept, fromReadings: true, ct);
            await RollAsync(db, transaction, "points_5m", "points_1h", OneHour, now, now - FiveMinutesKept, fromReadings: false, ct);
            await using (var prune = db.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM points_5m WHERE t < $five; DELETE FROM points_1h WHERE t < $hour";
                prune.Parameters.AddWithValue("$five", (now - FiveMinutesKept).Ticks);
                prune.Parameters.AddWithValue("$hour", (now - OneHourKept).Ticks);
                await prune.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // From the step before the last one rolled up (or as far back as the source goes) to the step still filling up.
    private static async Task RollAsync(SqliteConnection db, SqliteTransaction transaction, string from, string into, TimeSpan step, DateTime now, DateTime oldest,
        bool fromReadings, CancellationToken ct)
    {
        await using var last = db.CreateCommand();
        last.Transaction = transaction;
        last.CommandText = $"SELECT MAX(t) FROM {into}";
        var rolled = await last.ExecuteScalarAsync(ct) is long t ? t : (long?)null;
        var start = Math.Max(rolled is { } r ? r - 2 * step.Ticks : oldest.Ticks, oldest.Ticks) / step.Ticks * step.Ticks;
        var end = now.Ticks / step.Ticks * step.Ticks;
        if (end <= start) return;
        await using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        // A reading is stamped at the end of its interval, so one stamped on a step's boundary belongs to the step it ends.
        cmd.CommandText = fromReadings
            ? $"""
                INSERT OR REPLACE INTO {into} (container, t, cpu, cpu_max, mem, mem_max, rx, tx)
                SELECT container, ((t - 1) / $step + 1) * $step, AVG(cpu), MAX(cpu), AVG(mem), MAX(mem), AVG(rx), AVG(tx)
                FROM {from} WHERE t > $start AND t <= $end GROUP BY container, (t - 1) / $step
                """
            : $"""
                INSERT OR REPLACE INTO {into} (container, t, cpu, cpu_max, mem, mem_max, rx, tx)
                SELECT container, ((t - 1) / $step + 1) * $step, AVG(cpu), MAX(cpu_max), AVG(mem), MAX(mem_max), AVG(rx), AVG(tx)
                FROM {from} WHERE t > $start AND t <= $end GROUP BY container, (t - 1) / $step
                """;
        cmd.Parameters.AddWithValue("$step", step.Ticks);
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$end", end);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The 5-minute (<paramref name="hourly"/> false) or 1-hour roll-ups from <paramref name="since"/> on, by container (or
    /// one container's), oldest first, as chart points: averages, with the peaks beside them.
    /// </summary>
    public async Task<Dictionary<string, List<RolledPoint>>> LoadRolledAsync(bool hourly, DateTime since, string? container = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<RolledPoint>>(StringComparer.Ordinal);
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT container, t, cpu, cpu_max, mem, mem_max, rx, tx FROM {(hourly ? "points_1h" : "points_5m")} " +
                              $"WHERE t >= $since{(container is null ? "" : " AND container = $container")} ORDER BY container, t";
            cmd.Parameters.AddWithValue("$since", since.Ticks);
            if (container is not null) cmd.Parameters.AddWithValue("$container", container);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!result.TryGetValue(name, out var list)) result[name] = list = [];
                list.Add(new RolledPoint(new DateTime(reader.GetInt64(1), DateTimeKind.Utc), reader.GetDouble(2), reader.GetDouble(3), Real(reader, 4), Real(reader, 5),
                    Real(reader, 6), Real(reader, 7)));
            }
        }
        finally
        {
            _gate.Release();
        }
        return result;
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
            CREATE TABLE IF NOT EXISTS points_5m (
                container TEXT NOT NULL, t INTEGER NOT NULL, cpu REAL NOT NULL, cpu_max REAL NOT NULL, mem REAL, mem_max REAL, rx REAL, tx REAL,
                PRIMARY KEY (container, t)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS points_5m_by_time ON points_5m (t);
            CREATE TABLE IF NOT EXISTS points_1h (
                container TEXT NOT NULL, t INTEGER NOT NULL, cpu REAL NOT NULL, cpu_max REAL NOT NULL, mem REAL, mem_max REAL, rx REAL, tx REAL,
                PRIMARY KEY (container, t)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS points_1h_by_time ON points_1h (t);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _created = true;
        return db;
    }

    private static double? Real(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private static int? Int(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
}
