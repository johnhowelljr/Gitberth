using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gitberth.Audit;

/// <summary>An entry ready to be stored: cleaned, its actor and time settled, its changes as JSON. The store numbers and hashes it.</summary>
internal sealed record AuditDraft(long AtMs, AuditActor Actor, string Action, AuditOutcome Outcome, string Summary, int? AppId, string? AppName,
    string? Container, string? Target, string? Changes, string? Correlation);

/// <summary>
/// The audit log's own small database (/data/audit.db), next to Gitberth's and like stats.db: raw SQLite, a write-ahead log,
/// its tables made here and versioned with PRAGMA user_version, never an EF migration. One thing at a time goes through it
/// (a gate), so the hash chain is only ever extended by one writer. An in-memory store (for unit tests) keeps one connection
/// open for its whole life, since an in-memory database ends with its last connection.
/// </summary>
internal sealed class AuditStore : IDisposable
{
    /// <summary>The schema this code writes; an older file is brought up to it when it's first opened.</summary>
    internal const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SqliteConnection? _memory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;
    // The chain's head (the last entry's number and hash), once read; only changed under the gate, after a commit.
    private long _lastId = -1;
    private string _lastHash = AuditHash.Genesis;

    private static readonly JsonSerializerOptions ChangesJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private AuditStore(string connectionString, SqliteConnection? memory, string? path)
    {
        _connectionString = connectionString;
        _memory = memory;
        FilePath = path;
    }

    /// <summary>The database file; null for an in-memory store.</summary>
    public string? FilePath { get; }

    /// <summary>Makes the store refuse a draft (the exception is thrown, and nothing of the write is kept), in tests.</summary>
    internal Func<AuditDraft, Exception?>? Fault { get; set; }

    public static AuditStore InMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new AuditStore("", connection, null);
    }

    public static AuditStore AtFile(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // A connection per call, closed with it: the file is never held open between calls.
        Pooling = false,
    }.ToString(), null, path);

    // ---- Writing ----

    /// <summary>Numbers, hashes and stores the drafts in one transaction, in order. Returns them as stored.</summary>
    public List<AuditRecord> Append(IReadOnlyList<AuditDraft> drafts)
    {
        if (drafts.Count == 0) return [];
        _gate.Wait();
        try
        {
            using var lease = Open();
            var db = lease.Connection;
            var (lastId, lastHash) = Head(db);
            var records = new List<AuditRecord>(drafts.Count);
            using (var tx = db.BeginTransaction())
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO entries (id, at, actor_kind, actor, via, ip, action, outcome, app_id, app_name, container, target, summary, changes, correlation, prev_hash, hash)
                    VALUES ($id, $at, $kind, $actor, $via, $ip, $action, $outcome, $app, $name, $container, $target, $summary, $changes, $correlation, $prev, $hash)
                    """;
                var p = new Dictionary<string, SqliteParameter>();
                foreach (var name in new[] { "$id", "$at", "$kind", "$actor", "$via", "$ip", "$action", "$outcome", "$app", "$name", "$container", "$target", "$summary", "$changes", "$correlation", "$prev", "$hash" })
                    p[name] = cmd.Parameters.Add(name, SqliteType.Text);
                foreach (var d in drafts)
                {
                    if (Fault?.Invoke(d) is { } fault) throw fault;
                    var row = new AuditHash.Row(lastId + 1, d.AtMs, d.Actor.Kind.ToString(), d.Actor.Name, d.Actor.Via.ToString(), d.Actor.Ip, d.Action,
                        d.Outcome.ToString(), d.AppId, d.AppName, d.Container, d.Target, d.Summary, d.Changes, d.Correlation);
                    var hash = AuditHash.Of(lastHash, row);
                    p["$id"].Value = row.Id;
                    p["$at"].Value = row.AtMs;
                    p["$kind"].Value = row.ActorKind;
                    p["$actor"].Value = row.Actor;
                    p["$via"].Value = row.Via;
                    p["$ip"].Value = (object?)row.Ip ?? DBNull.Value;
                    p["$action"].Value = row.Action;
                    p["$outcome"].Value = row.Outcome;
                    p["$app"].Value = (object?)row.AppId ?? DBNull.Value;
                    p["$name"].Value = (object?)row.AppName ?? DBNull.Value;
                    p["$container"].Value = (object?)row.Container ?? DBNull.Value;
                    p["$target"].Value = (object?)row.Target ?? DBNull.Value;
                    p["$summary"].Value = row.Summary;
                    p["$changes"].Value = (object?)row.Changes ?? DBNull.Value;
                    p["$correlation"].Value = (object?)row.Correlation ?? DBNull.Value;
                    p["$prev"].Value = lastHash;
                    p["$hash"].Value = hash;
                    cmd.ExecuteNonQuery();
                    records.Add(new AuditRecord(row.Id, FromMs(row.AtMs), d.Actor, d.Action, d.Outcome, d.Summary, d.AppId, d.AppName, d.Container, d.Target,
                        ParseChanges(d.Changes), d.Correlation, lastHash, hash));
                    lastId = row.Id;
                    lastHash = hash;
                }
                SetMeta(db, tx, "last_id", lastId.ToString(CultureInfo.InvariantCulture));
                SetMeta(db, tx, "last_hash", lastHash);
                tx.Commit();
            }
            (_lastId, _lastHash) = (lastId, lastHash);
            return records;
        }
        catch
        {
            // Nothing of it was kept. The file is read afresh next time (its schema and the chain's head): it may have been
            // restored, replaced or made again meanwhile.
            (_lastId, _ready) = (-1, false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the oldest entries, up to the first one recorded at or after <paramref name="cutoff"/>: always a run from the
    /// start, so the rest still verify. The chain's anchor becomes the hash before the first one kept. Returns how many went.
    /// </summary>
    public int Prune(DateTime cutoff)
    {
        _gate.Wait();
        try
        {
            using var lease = Open();
            var db = lease.Connection;
            var (lastId, lastHash) = Head(db);
            long upTo;
            string anchor;
            using (var first = db.CreateCommand())
            {
                first.CommandText = "SELECT id, prev_hash FROM entries WHERE at >= $cutoff ORDER BY id LIMIT 1";
                first.Parameters.AddWithValue("$cutoff", ToMs(cutoff));
                using var r = first.ExecuteReader();
                if (r.Read()) (upTo, anchor) = (r.GetInt64(0) - 1, r.GetString(1));
                else (upTo, anchor) = (lastId, lastHash);
            }
            int removed;
            using (var tx = db.BeginTransaction())
            {
                using var delete = db.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM entries WHERE id <= $upTo";
                delete.Parameters.AddWithValue("$upTo", upTo);
                removed = delete.ExecuteNonQuery();
                if (removed > 0) SetMeta(db, tx, "anchor", anchor);
                tx.Commit();
            }
            if (removed > 0)
            {
                using var vacuum = db.CreateCommand();
                vacuum.CommandText = "PRAGMA incremental_vacuum";
                vacuum.ExecuteNonQuery();
            }
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Reading ----

    /// <summary>Every entry, oldest first.</summary>
    public List<AuditRecord> All()
    {
        _gate.Wait();
        try
        {
            using var lease = Open();
            using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM entries ORDER BY id";
            return ReadRecords(cmd);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The entries a query matches, newest first, a page at a time.</summary>
    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct) => ReadAsync(db =>
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        using var cmd = db.CreateCommand();
        var where = Filter(cmd, query);
        cmd.CommandText = $"SELECT {Columns} FROM entries{where} ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        var records = ReadRecords(cmd);
        var more = records.Count > limit;
        if (more) records.RemoveAt(records.Count - 1);
        return new AuditPage(records, more);
    }, ct);

    public Task<long> CountAsync(AuditQuery query, CancellationToken ct) => ReadAsync(db =>
    {
        using var cmd = db.CreateCommand();
        var where = Filter(cmd, query with { BeforeId = null });
        cmd.CommandText = $"SELECT COUNT(*) FROM entries{where}";
        return (long)cmd.ExecuteScalar()!;
    }, ct);

    public Task<AuditSummary> SummaryAsync(CancellationToken ct) => ReadAsync(db =>
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(at), MAX(at) FROM entries";
        using var r = cmd.ExecuteReader();
        r.Read();
        return new AuditSummary(r.GetInt64(0), r.IsDBNull(1) ? null : FromMs(r.GetInt64(1)), r.IsDBNull(2) ? null : FromMs(r.GetInt64(2)), Bytes());
    }, ct);

    /// <summary>The actors in the log, the most recent first.</summary>
    public Task<List<AuditActorRef>> ActorsAsync(CancellationToken ct) => ReadAsync(db =>
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT actor_kind, actor, MAX(id) AS last FROM entries GROUP BY actor_kind, actor ORDER BY last DESC LIMIT 100";
        var list = new List<AuditActorRef>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (Enum.TryParse<AuditActorKind>(r.GetString(0), out var kind)) list.Add(new AuditActorRef(kind, r.GetString(1)));
        return list;
    }, ct);

    /// <summary>The apps the log names, the most recent first, each with the title its newest entry gave it.</summary>
    public Task<List<AuditAppRef>> AppsAsync(CancellationToken ct) => ReadAsync(db =>
    {
        using var cmd = db.CreateCommand();
        // SQLite fills the other columns from the row MAX() picks: the newest entry's title and id.
        cmd.CommandText = "SELECT container, app_name, app_id, MAX(id) AS last FROM entries WHERE container IS NOT NULL GROUP BY container ORDER BY last DESC LIMIT 300";
        var list = new List<AuditAppRef>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new AuditAppRef(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
        return list;
    }, ct);

    /// <summary>
    /// Walks the chain from its anchor: each entry must follow the hash before it and hash to what it says, and the last
    /// one must be the head the store wrote last. Says where the first break is.
    /// </summary>
    public Task<AuditVerification> VerifyAsync(CancellationToken ct) => ReadAsync(db =>
    {
        var anchor = GetMeta(db, "anchor") ?? AuditHash.Genesis;
        var head = GetMeta(db, "last_hash");
        var headId = long.TryParse(GetMeta(db, "last_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hid) ? hid : (long?)null;
        var expected = anchor;
        long count = 0;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, at, actor_kind, actor, via, ip, action, outcome, app_id, app_name, container, target, summary, changes, correlation, prev_hash, hash FROM entries ORDER BY id";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                count++;
                var row = new AuditHash.Row(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), Text(r, 5), r.GetString(6), r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt32(8), Text(r, 9), Text(r, 10), Text(r, 11), r.GetString(12), Text(r, 13), Text(r, 14));
                var prev = r.GetString(15);
                var hash = r.GetString(16);
                if (prev != expected)
                    return new AuditVerification(false, count, row.Id, $"Entry {row.Id} doesn't follow the entry before it: an entry was removed or changed just before it.");
                if (AuditHash.Of(prev, row) != hash)
                    return new AuditVerification(false, count, row.Id, $"Entry {row.Id} was changed after it was recorded.");
                expected = hash;
            }
        }
        if (head is not null && head != expected)
            return new AuditVerification(false, count, headId, headId is { } last
                ? $"The newest entries are missing: the log should end at entry {last}."
                : "The newest entries are missing.");
        return new AuditVerification(true, count, null, count switch
        {
            0 => "The log is empty: there's nothing to check yet.",
            1 => "The one entry is as it was recorded.",
            _ => $"All {count.ToString("N0", CultureInfo.InvariantCulture)} entries are as they were recorded, and none is missing.",
        });
    }, ct);

    /// <summary>The database's size on disk, its write-ahead log included; 0 in memory.</summary>
    public long Bytes()
    {
        if (FilePath is null) return 0;
        long size = 0;
        foreach (var path in new[] { FilePath, FilePath + "-wal" })
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) size += info.Length;
            }
            catch (IOException)
            {
            }
        }
        return size;
    }

    public void Dispose()
    {
        _memory?.Dispose();
        _gate.Dispose();
    }

    // ---- The parts ----

    private const string Columns = "id, at, actor_kind, actor, via, ip, action, outcome, app_id, app_name, container, target, summary, changes, correlation, prev_hash, hash";

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var lease = Open();
            return read(lease.Connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The query's filters as a WHERE clause, its values added to the command.
    internal static string Filter(SqliteCommand cmd, AuditQuery q)
    {
        var where = new List<string>();
        if (q.From is { } from)
        {
            where.Add("at >= $from");
            cmd.Parameters.AddWithValue("$from", ToMs(from));
        }
        if (q.To is { } to)
        {
            where.Add("at < $to");
            cmd.Parameters.AddWithValue("$to", ToMs(to));
        }
        if (q.ActorKind is { } kind)
        {
            where.Add("actor_kind = $kind");
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
        }
        if (!string.IsNullOrEmpty(q.Actor))
        {
            where.Add("actor = $actor");
            cmd.Parameters.AddWithValue("$actor", q.Actor);
        }
        if (q.AppId is { } appId && !string.IsNullOrEmpty(q.Container))
        {
            where.Add("(app_id = $app OR container = $container)");
            cmd.Parameters.AddWithValue("$app", appId);
            cmd.Parameters.AddWithValue("$container", q.Container);
        }
        else if (q.AppId is { } onlyId)
        {
            where.Add("app_id = $app");
            cmd.Parameters.AddWithValue("$app", onlyId);
        }
        else if (!string.IsNullOrEmpty(q.Container))
        {
            where.Add("container = $container");
            cmd.Parameters.AddWithValue("$container", q.Container);
        }
        if (!string.IsNullOrEmpty(q.ActionPrefix))
        {
            where.Add("(action = $action OR action LIKE $actionLike ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$action", q.ActionPrefix);
            cmd.Parameters.AddWithValue("$actionLike", Like(q.ActionPrefix) + "%");
        }
        if (q.Area is { } area)
        {
            if (area == AuditArea.Outside)
            {
                where.Add("actor_kind = 'Docker'");
            }
            else
            {
                var (include, exclude) = AuditAreas.Of(area);
                var ors = new List<string>();
                for (var i = 0; i < include.Length; i++)
                {
                    ors.Add($"action LIKE $areaIn{i} ESCAPE '\\'");
                    cmd.Parameters.AddWithValue($"$areaIn{i}", Like(include[i]) + "%");
                }
                where.Add("(" + string.Join(" OR ", ors) + ")");
                for (var i = 0; i < exclude.Length; i++)
                {
                    where.Add($"action NOT LIKE $areaOut{i} ESCAPE '\\'");
                    cmd.Parameters.AddWithValue($"$areaOut{i}", Like(exclude[i]) + "%");
                }
            }
        }
        if (q.Outcome is { } outcome)
        {
            where.Add("outcome = $outcome");
            cmd.Parameters.AddWithValue("$outcome", outcome.ToString());
        }
        if (q.OutsideOnly) where.Add("actor_kind = 'Docker'");
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(summary LIKE $text ESCAPE '\\' OR target LIKE $text ESCAPE '\\' OR actor LIKE $text ESCAPE '\\' OR app_name LIKE $text ESCAPE '\\' "
                + "OR container LIKE $text ESCAPE '\\' OR action LIKE $text ESCAPE '\\' OR ip LIKE $text ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$text", "%" + Like(q.Text.Trim()) + "%");
        }
        if (q.BeforeId is { } before)
        {
            where.Add("id < $before");
            cmd.Parameters.AddWithValue("$before", before);
        }
        return where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
    }

    private static string Like(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static List<AuditRecord> ReadRecords(SqliteCommand cmd)
    {
        var list = new List<AuditRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var kind = Enum.TryParse<AuditActorKind>(r.GetString(2), out var k) ? k : AuditActorKind.Gitberth;
            var via = Enum.TryParse<AuditVia>(r.GetString(4), out var v) ? v : AuditVia.Gitberth;
            var outcome = Enum.TryParse<AuditOutcome>(r.GetString(7), out var o) ? o : AuditOutcome.Succeeded;
            list.Add(new AuditRecord(r.GetInt64(0), FromMs(r.GetInt64(1)), new AuditActor(kind, r.GetString(3), via, Text(r, 5)), r.GetString(6), outcome,
                r.GetString(12), r.IsDBNull(8) ? null : r.GetInt32(8), Text(r, 9), Text(r, 10), Text(r, 11), ParseChanges(Text(r, 13)), Text(r, 14),
                r.GetString(15), r.GetString(16)));
        }
        return list;
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    internal static string? ChangesText(IReadOnlyList<AuditChange>? changes) =>
        changes is null || changes.Count == 0 ? null
        : JsonSerializer.Serialize(changes.Select(c => new ChangeJson(c.Field, c.From, c.To)), ChangesJson);

    internal static IReadOnlyList<AuditChange> ParseChanges(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChangeJson>>(json, ChangesJson)?.Select(c => new AuditChange(c.Field ?? "", c.From, c.To)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ChangeJson(
        [property: System.Text.Json.Serialization.JsonPropertyName("field")] string? Field,
        [property: System.Text.Json.Serialization.JsonPropertyName("from")] string? From,
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string? To);

    internal static long ToMs(DateTime at) => new DateTimeOffset(at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime()).ToUnixTimeMilliseconds();

    internal static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    // The head of the chain: kept in memory once read; the meta table has it, or the newest row, or the anchor of an empty log.
    private (long Id, string Hash) Head(SqliteConnection db)
    {
        if (_lastId >= 0) return (_lastId, _lastHash);
        long id;
        string hash;
        if (long.TryParse(GetMeta(db, "last_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var metaId) && GetMeta(db, "last_hash") is { } metaHash)
        {
            (id, hash) = (metaId, metaHash);
        }
        else
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, hash FROM entries ORDER BY id DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            (id, hash) = r.Read() ? (r.GetInt64(0), r.GetString(1)) : (0L, GetMeta(db, "anchor") ?? AuditHash.Genesis);
        }
        (_lastId, _lastHash) = (id, hash);
        return (id, hash);
    }

    private static string? GetMeta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection db, SqliteTransaction tx, string key, string value)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private Lease Open()
    {
        if (_memory is not null)
        {
            EnsureSchema(_memory);
            return new Lease(_memory, owned: false);
        }
        var db = new SqliteConnection(_connectionString);
        db.Open();
        try
        {
            using (var cmd = db.CreateCommand())
            {
                // NORMAL is safe with a write-ahead log and skips a disk flush on every write.
                cmd.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
                cmd.ExecuteNonQuery();
            }
            EnsureSchema(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return new Lease(db, owned: true);
    }

    private void EnsureSchema(SqliteConnection db)
    {
        if (_ready) return;
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current < 1)
        {
            using var cmd = db.CreateCommand();
            // auto_vacuum only takes before the first table: a prune hands its pages back with incremental_vacuum.
            cmd.CommandText = """
                PRAGMA auto_vacuum = INCREMENTAL;
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS entries (
                    id INTEGER PRIMARY KEY,
                    at INTEGER NOT NULL,
                    actor_kind TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    via TEXT NOT NULL,
                    ip TEXT,
                    action TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    app_id INTEGER,
                    app_name TEXT,
                    container TEXT,
                    target TEXT,
                    summary TEXT NOT NULL,
                    changes TEXT,
                    correlation TEXT,
                    prev_hash TEXT NOT NULL,
                    hash TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS entries_by_time ON entries (at);
                CREATE INDEX IF NOT EXISTS entries_by_app ON entries (app_id);
                CREATE INDEX IF NOT EXISTS entries_by_container ON entries (container);
                CREATE INDEX IF NOT EXISTS entries_by_action ON entries (action);
                CREATE INDEX IF NOT EXISTS entries_by_actor ON entries (actor_kind, actor);
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        else if (current > SchemaVersion)
        {
            throw new InvalidOperationException($"The audit log's file is from a newer Gitberth (schema {current}; this one knows {SchemaVersion}).");
        }
        _ready = true;
    }

    private readonly struct Lease(SqliteConnection connection, bool owned) : IDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public void Dispose()
        {
            if (owned) Connection.Dispose();
        }
    }
}
