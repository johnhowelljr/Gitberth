using Gitberth.Data;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Apps;

/// <summary>
/// What happened to an app, for its Activity tab: Gitberth's own entries (created, adopted, settings, live changes,
/// updates…), its deploys, its changes (the journal, by container name) and Docker's events from the Health page's store
/// (starts, stops, crashes). Every text is written from names and outcomes only, never from a setting's value.
/// </summary>
public sealed class ActivityLog(IDbContextFactory<GitberthDb> dbFactory, StatsStore events)
{
    /// <summary>How far back Docker's events go: the Health store keeps a few hours of them.</summary>
    private static readonly TimeSpan EventsKept = ContainerEventWatcher.Kept;

    /// <param name="appId">The app's row; when null (a live change, say), the row of the container's app, if it has one.</param>
    public async Task AddAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone = ActivityTone.Neutral, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (appId is null)
        {
            var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
            appId = await db.Apps.AsNoTracking().Where(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug))
                .Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
        }
        db.Activity.Add(new ActivityEntry { ContainerName = container, AppId = appId, At = DateTime.UtcNow, Kind = kind, Tone = tone, Text = text });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Newest first: its entries, its deploys (by app), its changes (ContainerChanges by name) and Docker's events from the Health store.</summary>
    public async Task<IReadOnlyList<ActivityItem>> ForAsync(string container, int? appId, int take = 100, CancellationToken ct = default)
    {
        var items = new List<ActivityItem>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var entries = await db.Activity.AsNoTracking()
                .Where(a => a.ContainerName == container || (appId != null && a.AppId == appId))
                .OrderByDescending(a => a.At).Take(take).ToListAsync(ct);
            items.AddRange(entries.Select(e => new ActivityItem(e.At, e.Kind, e.Text, e.Tone)));
            if (appId is int id)
            {
                var deploys = await db.Deployments.AsNoTracking().Where(d => d.AppId == id).OrderByDescending(d => d.Id).Take(take).ToListAsync(ct);
                items.AddRange(deploys.Select(Deploy));
            }
            var changes = await db.ContainerChanges.AsNoTracking().Where(c => c.ContainerName == container).OrderByDescending(c => c.Id).Take(take).ToListAsync(ct);
            items.AddRange(changes.Select(Change));
        }
        items.AddRange(await EventsAsync(container, ct));
        return items.OrderByDescending(i => i.At).Take(take).ToList();
    }

    /// <summary>
    /// What was deployed lately on every app, whatever its source, newest first, for the dashboard: deploys from a
    /// repository (scheduled restarts aren't deploys), apps created, and updates, rollbacks and recreating changes from the
    /// journal. Each entry names its app and links to the app's page (a deploy to its log).
    /// </summary>
    public async Task<IReadOnlyList<RecentDeploy>> RecentDeploysAsync(DateTime since, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apps = await db.Apps.AsNoTracking().ToListAsync(ct);
        var byId = apps.ToDictionary(a => a.Id);
        var byContainer = apps.GroupBy(a => a.ContainerName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string Name(string container, int? appId) =>
            appId is int id && byId.TryGetValue(id, out var row) ? row.Name : byContainer.TryGetValue(container, out var named) ? named.Name : container;

        var all = new List<RecentDeploy>();
        var deploys = await db.Deployments.AsNoTracking().Where(d => d.QueuedAt >= since && d.Trigger != DeployTrigger.Restart).ToListAsync(ct);
        foreach (var d in deploys)
        {
            var row = byId.GetValueOrDefault(d.AppId);
            var item = Deploy(d);
            all.Add(new RecentDeploy(row?.ContainerName ?? "", row?.Name ?? $"App {d.AppId}",
                item with { Text = DeployWords(d), Href = $"apps/{d.AppId}/deployments/{d.Id}" }));
        }
        var created = await db.Activity.AsNoTracking().Where(a => a.At >= since && a.Kind == ActivityKind.Created).ToListAsync(ct);
        foreach (var e in created)
            all.Add(new RecentDeploy(e.ContainerName, Name(e.ContainerName, e.AppId),
                new ActivityItem(e.At, e.Kind, LowerFirst(e.Text), e.Tone, $"apps/{Uri.EscapeDataString(e.ContainerName)}")));
        // A create's own journal entry is left out: its Created entry above says it.
        var changes = await db.ContainerChanges.AsNoTracking()
            .Where(c => c.StartedAt >= since && c.Kind != ChangeKind.Create && c.Outcome != ChangeOutcome.NothingChanged).ToListAsync(ct);
        foreach (var c in changes)
        {
            var item = Change(c);
            all.Add(new RecentDeploy(c.ContainerName, Name(c.ContainerName, null), item with
            {
                Kind = c.Kind switch { ChangeKind.Update => ActivityKind.Updated, ChangeKind.Rollback => ActivityKind.RolledBack, _ => ActivityKind.Change },
                Text = LowerFirst(item.Text),
                Href = $"apps/{Uri.EscapeDataString(c.ContainerName)}/activity",
            }));
        }
        return all.OrderByDescending(r => r.Item.At).Take(take).ToList();
    }

    // A deploy in a line that sits after its app's name.
    private static string DeployWords(Deployment d) => d.Status switch
    {
        DeployStatus.Succeeded => $"deployed {d.ShortSha} · {Format.Trigger(d.Trigger).ToLowerInvariant()}",
        DeployStatus.Failed => $"deploy #{d.Id} failed",
        DeployStatus.RolledBack => $"deploy #{d.Id} failed, and the running version stayed",
        DeployStatus.Cancelled => $"deploy #{d.Id} cancelled",
        _ => $"deploy #{d.Id} is running",
    };

    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    // A deploy: what started it, how it ended, and the commit; the log it links to is masked already. Its error isn't
    // repeated here: Docker's own messages can quote a setting.
    private static ActivityItem Deploy(Deployment d)
    {
        var (outcome, tone) = d.Status switch
        {
            DeployStatus.Succeeded => ("succeeded", ActivityTone.Good),
            DeployStatus.Failed => ("failed", ActivityTone.Danger),
            DeployStatus.RolledBack => ("failed, and the running version stayed", ActivityTone.Warning),
            DeployStatus.Cancelled => ("cancelled", ActivityTone.Neutral),
            _ => ("running", ActivityTone.Neutral),
        };
        var commit = d.ShortSha is { Length: > 0 } sha ? $" · {sha}" + (d.CommitMessage is { Length: > 0 } m ? " " + FirstLine(m) : "") : "";
        return new ActivityItem(d.FinishedAt ?? d.StartedAt ?? d.QueuedAt, ActivityKind.Deploy, $"Deploy #{d.Id} ({Format.Trigger(d.Trigger)}) {outcome}{commit}", tone,
            $"deployments/{d.Id}", DeploymentId: d.Id);
    }

    // A change from the journal: its summary (names only) and how it ended.
    private static ActivityItem Change(ContainerChangeEntry c)
    {
        var (outcome, tone) = c.Outcome switch
        {
            ChangeOutcome.Done => ("done", ActivityTone.Good),
            ChangeOutcome.RolledBack => ("undone: the original is back", ActivityTone.Warning),
            ChangeOutcome.NothingChanged => ("nothing changed", ActivityTone.Neutral),
            ChangeOutcome.NeedsDecision => ("waits for you: keep the new one or put the original back", ActivityTone.Warning),
            _ => c.Step == ChangeStep.WaitingForStart ? ("waits for its first start", ActivityTone.Neutral) : ("under way", ActivityTone.Neutral),
        };
        var what = c.Kind switch
        {
            ChangeKind.Update => "Update",
            ChangeKind.Rollback => "Rollback",
            ChangeKind.Settings => "Settings change",
            ChangeKind.Create => "Created",
            _ => "Port change",
        };
        var offline = c.OfflineMs is int ms && c.Outcome == ChangeOutcome.Done ? $", offline {Math.Max(0.1, ms / 1000.0):0.#} s" : "";
        // An update's and a rollback's summary says what happened already ("updated …", "rolled back to …").
        var text = c.Kind is ChangeKind.Update or ChangeKind.Rollback && c.Summary.Length > 0
            ? char.ToUpperInvariant(c.Summary[0]) + c.Summary[1..]
            : $"{what}: {c.Summary}";
        return new ActivityItem(c.FinishedAt ?? c.StartedAt, ActivityKind.Change, $"{text} ({outcome}{offline})", tone, ChangeId: c.Id);
    }

    // Starts, stops, restarts, crashes and health changes, as the Health page marks them. Deploys and recreates are left
    // to the deploys and changes above, which say more.
    private async Task<List<ActivityItem>> EventsAsync(string container, CancellationToken ct)
    {
        List<ContainerEvent> all;
        try
        {
            all = await events.LoadEventsAsync(DateTime.UtcNow - EventsKept, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing in the store is precious: without it, the tab shows the rest.
            return [];
        }
        var mine = all.Where(e => e.Name == container).OrderBy(e => e.Time).ToList();
        if (mine.Count == 0) return [];
        var (marks, _) = EventMarks.For(mine, mine[0].Time, DateTime.UtcNow, running: true);
        return marks
            .Where(m => m.Kind is not (MarkKind.Deploy or MarkKind.Recreate))
            .Select(m => new ActivityItem(m.Time, m.Kind switch
            {
                MarkKind.Start => ActivityKind.Started,
                MarkKind.Stop => ActivityKind.Stopped,
                MarkKind.Restart => ActivityKind.Restarted,
                _ => ActivityKind.Event,
            }, m.Detail, m.Tone switch
            {
                MarkTone.Good => ActivityTone.Good,
                MarkTone.Warning => ActivityTone.Warning,
                MarkTone.Danger => ActivityTone.Danger,
                _ => ActivityTone.Neutral,
            }))
            .ToList();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..77] + "…" : line;
    }
}
