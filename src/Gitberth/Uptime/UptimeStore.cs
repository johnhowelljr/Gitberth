using Microsoft.Data.Sqlite;

namespace Gitberth.Uptime;

/// <summary>
/// The uptime checks' history, in a small database of its own next to Gitberth's (uptime.db), like the Health page's
/// stats.db: each check for 7 days, an hourly roll-up of every app's checks and response times for 400 days, the times an
/// app was down (incidents) for 400 days, and what each host's certificate said. Keyed by app id; what an app had before
/// its row was made (an id used again) is never read as its own.
/// </summary>
public sealed class UptimeStore(GitberthOptions options)
{
    /// <summary>How long each check is kept.</summary>
    public static readonly TimeSpan KeepResults = TimeSpan.FromDays(7);

    /// <summary>How long the hourly roll-ups and the incidents are kept.</summary>
    public static readonly TimeSpan KeepHistory = TimeSpan.FromDays(400);

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataDir, "uptime.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _created;

    /// <summary>Keeps a check, and counts it in its hour.</summary>
    public async Task AddAsync(UptimeResult r, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
            await using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO results (app, t, up, status, ms, problem, address) VALUES ($app, $t, $up, $status, $ms, $problem, $address);
                    INSERT INTO hours (app, hour, checks, ups, ms_sum, ms_count) VALUES ($app, $hour, 1, $up, $msUp, $countUp)
                      ON CONFLICT (app, hour) DO UPDATE SET checks = checks + 1, ups = ups + excluded.ups,
                        ms_sum = ms_sum + excluded.ms_sum, ms_count = ms_count + excluded.ms_count;
                    """;
                cmd.Parameters.AddWithValue("$app", r.AppId);
                cmd.Parameters.AddWithValue("$t", r.At.Ticks);
                cmd.Parameters.AddWithValue("$up", r.Up ? 1 : 0);
                cmd.Parameters.AddWithValue("$status", (object?)r.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ms", (object?)r.Ms ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$problem", (object?)r.Problem ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$address", r.Address);
                cmd.Parameters.AddWithValue("$hour", HourOf(r.At).Ticks);
                // Response times count only for checks that found it up: a timeout's isn't one.
                cmd.Parameters.AddWithValue("$msUp", r.Up && r.Ms is int ms ? ms : 0);
                cmd.Parameters.AddWithValue("$countUp", r.Up && r.Ms is not null ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The app's checks from <paramref name="since"/> on, oldest first.</summary>
    public async Task<List<UptimeResult>> ResultsAsync(int appId, DateTime since, CancellationToken ct = default) =>
        await ReadAsync("SELECT app, t, up, status, ms, problem, address FROM results WHERE app = $app AND t >= $since ORDER BY t",
            cmd => { cmd.Parameters.AddWithValue("$app", appId); cmd.Parameters.AddWithValue("$since", since.Ticks); }, ReadResult, ct);

    /// <summary>Each app's last few checks, newest last: how each one stood when Gitberth stopped.</summary>
    public async Task<Dictionary<int, List<UptimeResult>>> LatestAsync(int perApp, CancellationToken ct = default)
    {
        var rows = await ReadAsync("""
            SELECT app, t, up, status, ms, problem, address FROM (
              SELECT *, ROW_NUMBER() OVER (PARTITION BY app ORDER BY t DESC) AS n FROM results) WHERE n <= $n ORDER BY app, t
            """, cmd => cmd.Parameters.AddWithValue("$n", perApp), ReadResult, ct);
        return rows.GroupBy(r => r.AppId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>The hourly roll-ups from <paramref name="since"/> on, of these apps (every app's when null), oldest first.</summary>
    public async Task<List<UptimeHour>> HoursAsync(IReadOnlyCollection<int>? appIds, DateTime since, CancellationToken ct = default)
    {
        var rows = await ReadAsync("SELECT app, hour, checks, ups, ms_sum, ms_count FROM hours WHERE hour >= $since ORDER BY app, hour",
            cmd => cmd.Parameters.AddWithValue("$since", HourOf(since).Ticks),
            r => new UptimeHour(r.GetInt32(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.GetInt32(5)), ct);
        return appIds is null ? rows : rows.Where(h => appIds.Contains(h.AppId)).ToList();
    }

    /// <summary>Opens an incident; returns its id.</summary>
    public async Task<long> OpenIncidentAsync(int appId, DateTime started, string cause, string? address, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO incidents (app, started, ended, cause, address) VALUES ($app, $started, NULL, $cause, $address); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$app", appId);
            cmd.Parameters.AddWithValue("$started", started.Ticks);
            cmd.Parameters.AddWithValue("$cause", cause);
            cmd.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseIncidentAsync(long id, DateTime ended, CancellationToken ct = default) =>
        await WriteAsync("UPDATE incidents SET ended = $ended WHERE id = $id AND ended IS NULL",
            cmd => { cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$ended", ended.Ticks); }, ct);

    /// <summary>The incidents still open (apps down now).</summary>
    public async Task<List<UptimeIncident>> OpenIncidentsAsync(CancellationToken ct = default) =>
        await ReadAsync("SELECT id, app, started, ended, cause, address FROM incidents WHERE ended IS NULL ORDER BY started", _ => { }, ReadIncident, ct);

    /// <summary>The incidents that were going on at any time from <paramref name="since"/> on, of one app or all, newest first.</summary>
    public async Task<List<UptimeIncident>> IncidentsAsync(int? appId, DateTime since, CancellationToken ct = default) =>
        await ReadAsync($"SELECT id, app, started, ended, cause, address FROM incidents WHERE (ended IS NULL OR ended >= $since){(appId is null ? "" : " AND app = $app")} ORDER BY started DESC",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$since", since.Ticks);
                if (appId is int id) cmd.Parameters.AddWithValue("$app", id);
            }, ReadIncident, ct);

    public async Task<CertificateFact?> CertificateAsync(string host, CancellationToken ct = default) =>
        (await ReadAsync("SELECT host, not_after, checked, warned_days, warned_at FROM certificates WHERE host = $host",
            cmd => cmd.Parameters.AddWithValue("$host", host.ToLowerInvariant()),
            r => new CertificateFact(r.GetString(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc), new DateTime(r.GetInt64(2), DateTimeKind.Utc),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : new DateTime(r.GetInt64(4), DateTimeKind.Utc)), ct)).FirstOrDefault();

    public async Task SaveCertificateAsync(CertificateFact fact, CancellationToken ct = default) =>
        await WriteAsync("INSERT OR REPLACE INTO certificates (host, not_after, checked, warned_days, warned_at) VALUES ($host, $notAfter, $checked, $warnedDays, $warnedAt)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$host", fact.Host.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$notAfter", fact.NotAfter.Ticks);
                cmd.Parameters.AddWithValue("$checked", fact.CheckedAt.Ticks);
                cmd.Parameters.AddWithValue("$warnedDays", (object?)fact.WarnedDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$warnedAt", (object?)fact.WarnedAt?.Ticks ?? DBNull.Value);
            }, ct);

    /// <summary>
    /// Forgets checks older than 7 days, roll-ups and incidents older than 400 days, certificates not seen for 30 days, and
    /// everything of apps that are gone (<paramref name="apps"/> are the ones there are).
    /// </summary>
    public async Task PruneAsync(DateTime now, IReadOnlyCollection<int> apps, CancellationToken ct = default) =>
        await WriteAsync($"""
            DELETE FROM results WHERE t < $results;
            DELETE FROM hours WHERE hour < $history;
            DELETE FROM incidents WHERE ended IS NOT NULL AND ended < $history;
            DELETE FROM certificates WHERE checked < $certificates;
            DELETE FROM results WHERE app NOT IN ({Ids(apps)});
            DELETE FROM hours WHERE app NOT IN ({Ids(apps)});
            DELETE FROM incidents WHERE app NOT IN ({Ids(apps)});
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$results", (now - KeepResults).Ticks);
                cmd.Parameters.AddWithValue("$history", (now - KeepHistory).Ticks);
                cmd.Parameters.AddWithValue("$certificates", (now - TimeSpan.FromDays(30)).Ticks);
            }, ct);

    // App ids are numbers Gitberth has, written as such (an empty list keeps nothing).
    private static string Ids(IReadOnlyCollection<int> apps) => apps.Count == 0 ? "-1" : string.Join(',', apps.Select(a => a.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static DateTime HourOf(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private static UptimeResult ReadResult(SqliteDataReader r) => new(r.GetInt32(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc), r.GetInt32(2) == 1,
        r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6));

    private static UptimeIncident ReadIncident(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt32(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc),
        r.IsDBNull(3) ? null : new DateTime(r.GetInt64(3), DateTimeKind.Utc), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));

    private async Task<List<T>> ReadAsync<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> read, CancellationToken ct)
    {
        var list = new List<T>();
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(read(reader));
        }
        finally
        {
            _gate.Release();
        }
        return list;
    }

    private async Task WriteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
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
        cmd.CommandText = _created ? "PRAGMA synchronous = NORMAL" : """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS results (
                app INTEGER NOT NULL, t INTEGER NOT NULL, up INTEGER NOT NULL, status INTEGER, ms INTEGER, problem TEXT, address TEXT NOT NULL,
                PRIMARY KEY (app, t)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS results_by_time ON results (t);
            CREATE TABLE IF NOT EXISTS hours (
                app INTEGER NOT NULL, hour INTEGER NOT NULL, checks INTEGER NOT NULL, ups INTEGER NOT NULL, ms_sum INTEGER NOT NULL, ms_count INTEGER NOT NULL,
                PRIMARY KEY (app, hour)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS incidents (
                id INTEGER PRIMARY KEY AUTOINCREMENT, app INTEGER NOT NULL, started INTEGER NOT NULL, ended INTEGER, cause TEXT NOT NULL, address TEXT);
            CREATE INDEX IF NOT EXISTS incidents_by_app ON incidents (app, started);
            CREATE TABLE IF NOT EXISTS certificates (
                host TEXT NOT NULL PRIMARY KEY, not_after INTEGER NOT NULL, checked INTEGER NOT NULL, warned_days INTEGER, warned_at INTEGER) WITHOUT ROWID;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _created = true;
        return db;
    }
}
