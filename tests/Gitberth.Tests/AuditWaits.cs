using Gitberth.Audit;

namespace Gitberth.Tests;

/// <summary>Waiting for an audit entry that background work records (a deploy on the queue's worker, say).</summary>
internal static class AuditWaits
{
    /// <summary>
    /// The first entry stored that matches, whether it was stored before this was called or while it waits. It listens to
    /// <see cref="AuditLog.Recorded"/> before it looks at what's stored, so an entry stored either side of the look counts.
    /// A guard of a minute against a hang (<see cref="Eventually.Limit"/>), never what a passing test waits.
    /// </summary>
    public static async Task<AuditRecord> RecordedAsync(this AuditLog log, Func<AuditRecord, bool> match, TimeSpan? limit = null)
    {
        var found = new TaskCompletionSource<AuditRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Heard(AuditRecord record)
        {
            if (match(record)) found.TrySetResult(record);
        }
        log.Recorded += Heard;
        try
        {
            // An in-memory log stores each entry as it's recorded; the file's is read without waiting for its writer.
            var stored = log.InMemory ? log.Entries : (await log.QueryAsync(new AuditQuery { Limit = 1000 })).Entries.Reverse();
            if (stored.FirstOrDefault(match) is { } already) return already;
            var within = limit ?? Eventually.Limit;
            try
            {
                return await found.Task.WaitAsync(within);
            }
            catch (TimeoutException)
            {
                var actions = log.InMemory ? log.Entries.Select(e => e.Action) : Enumerable.Empty<string>();
                Assert.Fail($"No such audit entry was recorded in {within.TotalSeconds:0} s. Recorded: {string.Join(", ", actions.DefaultIfEmpty("(nothing)"))}.");
                throw;
            }
        }
        finally
        {
            log.Recorded -= Heard;
        }
    }
}
