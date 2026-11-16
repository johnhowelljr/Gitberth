using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Gitberth.Audit;

/// <summary>
/// The audit log: every change to the system, who made it, from where, and how it ended. Kept in its own SQLite file
/// (/data/audit.db), never a database server. <see cref="Record"/> never throws and never waits: entries go to a queue in
/// memory and one writer stores them in order, in batches, each hashed onto the one before (<see cref="AuditHash"/>), so a
/// changed or removed entry shows when the log is checked (<see cref="VerifyAsync"/>). Nothing is lost because the file
/// fails: while it can't be written (a full disk, an I/O error, a read-only file), entries wait in memory, in order, and
/// are written once it works again; the log says so meanwhile. Only an entry the file refuses while it takes the others
/// is ever left out, and loudly.
/// <para>
/// <c>new AuditLog()</c> is an in-memory log for unit tests: it stores each entry at once, so <see cref="Entries"/> and the
/// queries see it as soon as <see cref="Record"/> returns.
/// </para>
/// </summary>
public class AuditLog : IHostedService, IDisposable
{
    /// <summary>How many entries may wait to be written before the log warns about it (once a minute while it stays so).</summary>
    internal const int QueueWarning = 10_000;

    private const int Batch = 500;

    private readonly AuditStore _store;
    private readonly ILogger? _logger;
    private readonly Channel<AuditDraft>? _queue;
    private readonly Task? _writer;
    // Ends the writer's waits when the log closes with entries still waiting for a file that doesn't work.
    private readonly CancellationTokenSource _closing = new();
    // Cuts the writer's wait before its next try short when Gitberth stops: what waits is tried again at once, then often.
    private readonly CancellationTokenSource _hurry = new();
    private volatile bool _stopping;
    private long _pending;
    private DateTime _warnedAt = DateTime.MinValue;
    private string? _lastError;
    private int _disposed;

    /// <summary>An in-memory log that keeps its entries and answers the same queries: for unit tests.</summary>
    public AuditLog()
    {
        _store = AuditStore.InMemory();
    }

    /// <summary>The log in <c>audit.db</c> in Gitberth's data folder, with its writer running.</summary>
    public AuditLog(GitberthOptions options, ILogger<AuditLog> logger)
    {
        _logger = logger;
        _store = AuditStore.AtFile(Path.Combine(options.DataDir, "audit.db"));
        _queue = Channel.CreateUnbounded<AuditDraft>(new UnboundedChannelOptions { SingleReader = true });
        // The writer runs for as long as Gitberth does: it never takes on the actor of whatever made the log.
        using (ExecutionContext.SuppressFlow())
            _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Raised once an entry is stored, on the writer (or, in memory, on the caller). Handlers return at once.</summary>
    public event Action<AuditRecord>? Recorded;

    /// <summary>Whether this is the in-memory log of a unit test.</summary>
    public bool InMemory => _queue is null;

    /// <summary>The database file; null in memory.</summary>
    public string? FilePath => _store.FilePath;

    /// <summary>How many entries wait to be written.</summary>
    public long Pending => Interlocked.Read(ref _pending);

    /// <summary>
    /// Whether Docker changes made outside Gitberth are recorded (Settings › Audit log, <see cref="AuditSettings"/>): off,
    /// an entry whose actor is Docker is left out.
    /// </summary>
    public bool RecordOutside { get; set; } = true;

    /// <summary>Records a change. Never throws and never blocks the caller.</summary>
    public void Record(AuditEntry entry)
    {
        try
        {
            if (!RecordOutside && (entry.Actor ?? AuditScope.Current).Kind == AuditActorKind.Docker) return;
            var draft = Draft(entry, AuditScope.Current, DateTime.UtcNow);
            if (_queue is null)
            {
                Stored(_store.Append([draft]));
                return;
            }
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(draft))
            {
                Interlocked.Decrement(ref _pending);
                _logger?.LogWarning("The audit log is closed; this change wasn't recorded: {Action}: {Summary}", draft.Action, draft.Summary);
                return;
            }
            WarnIfBehind();
        }
        catch (Exception ex)
        {
            // Recording a change must never break the change.
            try
            {
                _logger?.LogWarning(ex, "Couldn't record a change in the audit log: {Action}", entry?.Action);
            }
            catch
            {
            }
        }
    }

    /// <summary>Every entry stored, oldest first (waiting ones are written first). For tests and small logs.</summary>
    public IReadOnlyList<AuditRecord> Entries
    {
        get
        {
            if (_queue is not null) FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return _store.All();
        }
    }

    /// <summary>Waits until every entry recorded so far is stored, or <paramref name="timeout"/> passes. True when it's all written.</summary>
    public async Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_queue is null) return true;
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (Pending > 0)
        {
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested) return false;
            try
            {
                await Task.Delay(20, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The entries a query matches, newest first, a page at a time (<see cref="AuditQuery.BeforeId"/>).</summary>
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        await FlushAsync(TimeSpan.FromSeconds(2), ct);
        return await _store.QueryAsync(query, ct);
    }

    /// <summary>How many entries a query matches, whatever its page.</summary>
    public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => _store.CountAsync(query, ct);

    public Task<AuditSummary> SummaryAsync(CancellationToken ct = default) => _store.SummaryAsync(ct);

    public async Task<IReadOnlyList<AuditActorRef>> ActorsAsync(CancellationToken ct = default) => await _store.ActorsAsync(ct);

    public async Task<IReadOnlyList<AuditAppRef>> AppsAsync(CancellationToken ct = default) => await _store.AppsAsync(ct);

    /// <summary>Checks every entry against its hash and the one before it (what's waiting is written first).</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken ct = default)
    {
        await FlushAsync(TimeSpan.FromSeconds(10), ct);
        return await _store.VerifyAsync(ct);
    }

    /// <summary>
    /// Removes the entries older than <paramref name="keepDays"/> days (the oldest run of them, so the rest still verify),
    /// and records that it did. Returns how many went.
    /// </summary>
    public async Task<int> PruneAsync(int keepDays, CancellationToken ct = default)
    {
        await FlushAsync(TimeSpan.FromSeconds(10), ct);
        var removed = await Task.Run(() => _store.Prune(DateTime.UtcNow.AddDays(-keepDays)), ct);
        if (removed > 0)
            Record(new AuditEntry("audit.pruned",
                $"Removed {Count(removed, "entry", "entries")} older than {Count(keepDays, "day", "days")} from the audit log, as its retention says.")
            {
                Target = "audit log",
                Changes = [new AuditChange("Entries removed", null, removed.ToString(CultureInfo.InvariantCulture))],
            });
        return removed;
    }

    /// <summary>Writes the entries a query matches (all of them, newest first, whatever its limit) as CSV or JSON Lines.</summary>
    public async Task ExportAsync(AuditQuery query, AuditExportFormat format, Stream output, CancellationToken ct = default)
    {
        await FlushAsync(TimeSpan.FromSeconds(5), ct);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        if (format == AuditExportFormat.Csv) await writer.WriteLineAsync(AuditExport.CsvHeader);
        var page = query with { Limit = 1000, BeforeId = null };
        while (true)
        {
            var result = await _store.QueryAsync(page, ct);
            foreach (var record in result.Entries)
                await writer.WriteLineAsync(format == AuditExportFormat.Csv ? AuditExport.CsvLine(record) : AuditExport.JsonLine(record));
            if (!result.More || result.Entries.Count == 0) break;
            page = page with { BeforeId = result.Entries[^1].Id };
            await writer.FlushAsync(ct);
        }
        await writer.FlushAsync(ct);
    }

    /// <summary>The size of the log's file, its write-ahead log included.</summary>
    public long Bytes => _store.Bytes();

    // ---- Hosting: the writer is flushed when Gitberth stops (the log is the last service to stop) ----

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Hurry();
        if (!await FlushAsync(TimeSpan.FromSeconds(20), cancellationToken) && Pending > 0)
            _logger?.LogWarning("Gitberth stopped with {Count} audit entries not written yet ({Error})", Pending, _lastError ?? "still writing");
    }

    // Gitberth stops: a write that failed a while ago is tried again now, not after the rest of its wait.
    private void Hurry()
    {
        _stopping = true;
        try
        {
            _hurry.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_queue is not null)
        {
            Hurry();
            _queue.Writer.TryComplete();
            try
            {
                // What waits goes in first, while the file takes it; a file that doesn't isn't waited for long.
                if (_writer?.Wait(TimeSpan.FromSeconds(10)) == false)
                {
                    _closing.Cancel();
                    _writer.Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception)
            {
            }
        }
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- The writer ----

    /// <summary>The longest the writer waits before it tries a failed write again: 2 s after the first failure, 4 s, … up to this.</summary>
    internal TimeSpan RetryCap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Holds the writer before it takes its next batch, in tests: what's recorded meanwhile goes in that batch.</summary>
    internal Func<Task>? BeforeBatch { get; init; }

    /// <summary>Makes the file refuse an entry (an exception) or take it (null), in tests.</summary>
    internal Func<AuditDraft, Exception?>? StoreFault
    {
        get => _store.Fault;
        init => _store.Fault = value;
    }

    // How many times what waits is tried as a whole before each entry is tried on its own, when what failed isn't the file.
    private const int WholeAttempts = 3;

    // How many entries in a row may be refused on their own, with none taken, before it's taken to be the file after all.
    private const int MostSuspects = 3;

    // The entries waiting are written in one go, in order, and tried again until they're in. What's recorded meanwhile
    // joins them, after them, so a file that's down for a while takes everything at once when it's back. Nothing is given
    // up on because the file fails (IsStoreFailure): it waits, however long. When what failed isn't plainly the file, each
    // entry is tried on its own (IsolateAsync), and only one the file refuses while it takes another after it is left out.
    private async Task WriteLoopAsync()
    {
        var reader = _queue!.Reader;
        var waiting = new List<AuditDraft>(Batch);
        var attempt = 0;
        try
        {
            while (waiting.Count > 0 || await reader.WaitToReadAsync())
            {
                if (waiting.Count == 0 && BeforeBatch is { } hold) await hold();
                while (waiting.Count < Batch && reader.TryRead(out var draft)) waiting.Add(draft);
                if (waiting.Count == 0) continue;
                var failure = TryAppend(waiting);
                if (failure is null)
                {
                    Done(waiting.Count, recovered: attempt > 0);
                    waiting.Clear();
                    attempt = 0;
                    continue;
                }
                attempt++;
                Failing(failure, waiting.Count, attempt);
                if (!IsStoreFailure(failure) && attempt % WholeAttempts == 0)
                {
                    var count = waiting.Count;
                    waiting = await IsolateAsync(waiting);
                    Done(count - waiting.Count, recovered: waiting.Count == 0);
                    if (waiting.Count == 0)
                    {
                        attempt = 0;
                        continue;
                    }
                }
                await PauseAsync(attempt);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The log is closing and the file still doesn't take what waits: StopAsync said how many entries weren't written.
        }
    }

    // The wait before the next try: 2 s after the first failure, 4 s after the second, and so on, up to RetryCap; half a
    // second while Gitberth stops. Throws once the log is closing.
    private async Task PauseAsync(int attempt)
    {
        if (_stopping)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), _closing.Token);
            return;
        }
        var delay = TimeSpan.FromSeconds(2 * Math.Clamp(attempt, 1, 1000));
        using var either = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, _hurry.Token);
        try
        {
            await Task.Delay(delay < RetryCap ? delay : RetryCap, either.Token);
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            // Hurried: Gitberth stops, so it's tried again now.
        }
    }

    // Stores the drafts in one transaction and tells the listeners; the failure when the file didn't take them.
    private Exception? TryAppend(IReadOnlyList<AuditDraft> drafts)
    {
        List<AuditRecord> records;
        try
        {
            records = _store.Append(drafts);
        }
        catch (Exception ex)
        {
            return ex;
        }
        Stored(records);
        return null;
    }

    /// <summary>
    /// The entries one at a time, in order, after writes of them all failed for something that isn't plainly the file.
    /// One the file refuses on its own (twice, a moment apart) is a suspect; once the file takes a later entry, the
    /// suspects before it are what it won't take, and they're left out, loudly. A failure of the file itself, or several
    /// refused in a row with none taken (the entries without the first one fail too), means it's the file after all:
    /// nothing is left out, and what's left waits, in order. Returns what's left to write.
    /// </summary>
    private async Task<List<AuditDraft>> IsolateAsync(List<AuditDraft> entries)
    {
        var suspects = new List<(AuditDraft Draft, Exception Error)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var failure = TryAppend([entries[i]]);
            // A failure that passes is no reason to leave anything out: once more, a moment later.
            if (failure is not null && !IsStoreFailure(failure) && !await DelayAsync(TimeSpan.FromSeconds(1))) failure = TryAppend([entries[i]]);
            if (failure is null)
            {
                // The file took this one after refusing them: it's them it won't take, not the file failing.
                foreach (var (draft, error) in suspects)
                    _logger?.LogError(error, "The audit log's file takes other entries but refuses this one, so it's left out: {Action}: {Summary}", draft.Action, draft.Summary);
                suspects.Clear();
                continue;
            }
            if (IsStoreFailure(failure) || _closing.IsCancellationRequested || suspects.Count + 1 >= MostSuspects)
                return [.. suspects.Select(s => s.Draft), .. entries.Skip(i)];
            suspects.Add((entries[i], failure));
        }
        // Refused, with nothing after them that the file took: they wait (the next entry recorded shows whether it's them).
        return [.. suspects.Select(s => s.Draft)];
    }

    // Waits, unless the log is closing: true when it is.
    private async Task<bool> DelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _closing.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether a write failed because of the file rather than what was written: the disk is full or failing, the file is
    /// read-only, can't be opened, isn't a database or is damaged, or another program holds it. What such a write held
    /// waits, however long, and none of it is ever left out.
    /// </summary>
    internal static bool IsStoreFailure(Exception ex) => ex switch
    {
        Microsoft.Data.Sqlite.SqliteException sqlite => (sqlite.SqliteErrorCode & 0xFF) is SQLitePCL.raw.SQLITE_FULL or SQLitePCL.raw.SQLITE_IOERR
            or SQLitePCL.raw.SQLITE_READONLY or SQLitePCL.raw.SQLITE_CANTOPEN or SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED
            or SQLitePCL.raw.SQLITE_NOTADB or SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_PERM or SQLitePCL.raw.SQLITE_NOMEM
            or SQLitePCL.raw.SQLITE_PROTOCOL or SQLitePCL.raw.SQLITE_NOLFS,
        IOException or UnauthorizedAccessException or ObjectDisposedException => true,
        _ => false,
    };

    private void Failing(Exception ex, int count, int attempt)
    {
        // Said at the first failure, then every 20th, so a file that's down for long doesn't fill Gitberth's log.
        if (attempt == 1 || attempt % 20 == 0)
            _logger?.LogError(ex, "Couldn't write {Count} audit entries to {Path}; they wait in memory, in order, and are tried again", count, _store.FilePath);
        _lastError = ex.Message;
    }

    // Entries stored, or left out: they're not waiting any more.
    private void Done(int count, bool recovered)
    {
        if (count > 0) Interlocked.Add(ref _pending, -count);
        if (!recovered) return;
        _logger?.LogInformation("The audit log is written again");
        _lastError = null;
    }

    private void Stored(List<AuditRecord> records)
    {
        if (Recorded is not { } recorded) return;
        foreach (var record in records)
            foreach (var handler in recorded.GetInvocationList().Cast<Action<AuditRecord>>())
            {
                try
                {
                    handler(record);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "An audit log listener failed");
                }
            }
    }

    private void WarnIfBehind()
    {
        var pending = Pending;
        if (pending < QueueWarning || _logger is null) return;
        var now = DateTime.UtcNow;
        if (now - _warnedAt < TimeSpan.FromMinutes(1)) return;
        _warnedAt = now;
        _logger.LogWarning("The audit log is {Count} entries behind: they're kept in memory until they're written ({Error})", pending, _lastError ?? "the file is slow");
    }

    // ---- Shaping an entry ----

    internal const int SummaryMax = 1000;
    internal const int TextMax = 300;
    internal const int ValueMax = 500;
    internal const int ChangesMax = 100;

    internal static AuditDraft Draft(AuditEntry entry, AuditActor ambient, DateTime now)
    {
        var actor = entry.Actor ?? ambient;
        actor = actor with
        {
            Name = Clean(actor.Name, 200) is { Length: > 0 } name ? name : actor.Kind.ToString(),
            Ip = Clean(actor.Ip, 64),
        };
        var at = entry.At is { } given ? (given.Kind == DateTimeKind.Local ? given.ToUniversalTime() : DateTime.SpecifyKind(given, DateTimeKind.Utc)) : now;
        var action = Clean(entry.Action, 100) is { Length: > 0 } a ? a.ToLowerInvariant() : "unknown";
        var summary = Clean(entry.Summary, SummaryMax) is { Length: > 0 } s ? s : action;
        var changes = entry.Changes?.Take(ChangesMax)
            .Select(c => new AuditChange(Clean(c.Field, TextMax) ?? "", Clean(c.From, ValueMax), Clean(c.To, ValueMax)))
            .ToList();
        return new AuditDraft(AuditStore.ToMs(at), actor, action, entry.Outcome, summary, entry.AppId, Clean(entry.AppName, 200), Clean(entry.Container, 200),
            Clean(entry.Target, TextMax), AuditStore.ChangesText(changes), Clean(entry.Correlation, 200));
    }

    /// <summary>
    /// One line, no control characters, at most <paramref name="max"/> characters (an ellipsis says it was cut), and
    /// never half a character: a cut falls between an emoji's two halves never, and half of one that came in alone is
    /// U+FFFD, as SQLite would store it. So what's hashed is exactly what's stored, and the entry verifies.
    /// </summary>
    internal static string? Clean(string? text, int max)
    {
        if (text is null) return null;
        var sb = new StringBuilder(Math.Min(text.Length, max + 2));
        var space = false;
        var trimmed = text.AsSpan().Trim();
        for (var i = 0; i < trimmed.Length && sb.Length <= max; i++)
        {
            var ch = trimmed[i];
            if (char.IsControl(ch) || ch is (char)0x2028 or (char)0x2029)
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            if (char.IsHighSurrogate(ch) && i + 1 < trimmed.Length && char.IsLowSurrogate(trimmed[i + 1])) sb.Append(ch).Append(trimmed[++i]);
            else sb.Append(char.IsSurrogate(ch) ? '�' : ch);
        }
        var clean = sb.ToString().TrimEnd();
        if (clean.Length <= max) return clean;
        // Cut before the character that doesn't fit whole: never between the two halves of one.
        var cut = max - 1;
        if (char.IsLowSurrogate(clean[cut])) cut--;
        return clean[..cut].TrimEnd() + "…";
    }

    private static string Count(int n, string one, string many) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";
}
