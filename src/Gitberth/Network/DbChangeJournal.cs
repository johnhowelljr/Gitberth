using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>The journal of changes, kept in Gitberth's database (table ContainerChanges), so a restart of Gitberth finds them.</summary>
public sealed class DbChangeJournal(IDbContextFactory<GitberthDb> dbFactory) : IChangeJournal
{
    public async Task<ContainerChange> AddAsync(ContainerChange change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = new ContainerChangeEntry();
        Copy(change, entry);
        db.ContainerChanges.Add(entry);
        await db.SaveChangesAsync(ct);
        return change with { Id = entry.Id };
    }

    public async Task UpdateAsync(ContainerChange change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ContainerChanges.FirstOrDefaultAsync(c => c.Id == change.Id, ct)
            ?? throw new InvalidOperationException($"The journal has no port change {change.Id}.");
        Copy(change, entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ContainerChange>> UnfinishedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ContainerChanges.AsNoTracking()
            .Where(c => c.Outcome == ChangeOutcome.Running || c.Outcome == ChangeOutcome.NeedsDecision)
            .OrderBy(c => c.Id).ToListAsync(ct);
        return rows.Select(ToChange).ToList();
    }

    public async Task<ContainerChange?> LatestAsync(string container, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ContainerChanges.AsNoTracking().Where(c => c.ContainerName == container).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        return row is null ? null : ToChange(row);
    }

    // The Notice column alone, in one statement: a change that moved on since its banner was read isn't written back.
    public async Task DismissNoticeAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ContainerChanges.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Notice, (string?)null), ct);
    }

    public async Task<ContainerChange?> WithContainerAsync(string containerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ContainerChanges.AsNoTracking().Where(c => c.OldId == containerId || c.NewId == containerId).OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToChange(row);
    }

    private static void Copy(ContainerChange from, ContainerChangeEntry to)
    {
        to.ContainerName = from.ContainerName;
        to.Summary = from.Summary;
        to.OldId = from.OldId;
        to.NewId = from.NewId;
        to.OldName = from.OldName;
        to.TempName = from.TempName;
        to.WasRunning = from.WasRunning;
        to.OldRestartPolicy = from.OldRestartPolicy;
        to.Step = from.Step;
        to.StopAt = from.StopAt;
        to.Outcome = from.Outcome;
        to.OfflineMs = from.OfflineMs;
        to.Error = from.Error;
        to.Notice = from.Notice;
        to.StartedAt = from.StartedAt;
        to.FinishedAt = from.FinishedAt;
        to.Kind = from.Kind;
        to.Fields = from.Fields;
        to.NewRestartPolicy = from.NewRestartPolicy;
        to.ImageBefore = from.ImageBefore;
        to.ImageAfter = from.ImageAfter;
    }

    private static ContainerChange ToChange(ContainerChangeEntry e) => new()
    {
        Id = e.Id,
        ContainerName = e.ContainerName,
        Summary = e.Summary,
        OldId = e.OldId,
        NewId = e.NewId,
        OldName = e.OldName,
        TempName = e.TempName,
        WasRunning = e.WasRunning,
        OldRestartPolicy = e.OldRestartPolicy,
        Step = e.Step,
        StopAt = e.StopAt,
        Outcome = e.Outcome,
        OfflineMs = e.OfflineMs,
        Error = e.Error,
        Notice = e.Notice,
        StartedAt = e.StartedAt,
        FinishedAt = e.FinishedAt,
        Kind = e.Kind,
        Fields = e.Fields,
        NewRestartPolicy = e.NewRestartPolicy,
        ImageBefore = e.ImageBefore,
        ImageAfter = e.ImageAfter,
    };
}
