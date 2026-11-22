using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Disk;

/// <summary>A deleted app's images still on the host, for Settings › Disk.</summary>
/// <param name="Size">What they take, about: each image's own size, once.</param>
/// <param name="Waiting">Its deletion asked for them to go and a container still uses one: the cleanup removes them once nothing does.</param>
public sealed record DeletedAppImages(string Slug, IReadOnlyList<string> Tags, long Size, bool Waiting);

/// <summary>What Settings › Disk shows.</summary>
/// <param name="ImagesSize">What every image on the host takes; null when the engine can't say cheaply.</param>
/// <param name="Running">A cleanup runs now.</param>
public sealed record DiskReport(DiskSpace Disk, long? ImagesSize, bool Auto, int KeepDays, DiskResult? Last, IReadOnlyList<DeletedAppImages> Deleted, bool Running);

/// <summary>What Clean up now and Remove these images answer.</summary>
public sealed record DiskAnswer(bool Ok, string Message, DiskResult? Result = null);

/// <summary>What one look did, for the tests.</summary>
internal sealed record DiskLook(DiskTier? Tier, DiskResult? Ran, DiskNotice Told, DiskLevel Shown);

/// <summary>
/// The disk cleanup: it looks at Docker's disk every minute, and keeps it from filling up on its own. Once a day it removes
/// the build cache no build used for N days (7 unless Settings says otherwise) and keeps the cache under its cap, the
/// untagged images of Gitberth's old builds, the leftover images of deleted apps, and each app's images beyond its keep set.
/// When the disk gets low it removes more of the cache (no build used for a day), and when it's almost full all the cache
/// no build uses; it says so on the dashboard and in notifications. It never removes an image an app runs, names, rolls
/// back to or a container uses, anything a build uses (it holds builds off while it runs), nor an image that isn't
/// Gitberth's; the images of a deleted app kept on purpose are only listed. What it removes is in the audit log, as
/// Gitberth's through the disk cleanup, or as the person's who clicked Clean up now. A run Docker fails or refuses is
/// tried again soon, never taken for one that found nothing to free.
/// </summary>
/// <param name="imageService">Reads Docker's disk; none in tests, which give <see cref="ReadDisk"/>.</param>
/// <param name="actions">Says which apps are being deleted, whose images the keep-set step leaves alone; none in older tests.</param>
/// <param name="buildKit">gitberth-buildkit, whose own cache (Railpack's builds) is cleaned up too; none in older tests.</param>
public sealed class DiskCleanup(
    DockerApi api,
    ImageService? imageService,
    DeployPipeline pipeline,
    IDbContextFactory<GitberthDb> dbFactory,
    SettingsService settings,
    DiskSettings diskSettings,
    AppLocks locks,
    Notifier notifier,
    AttentionBoard board,
    ILogger<DiskCleanup> logger,
    AuditLog? audit = null,
    ContainerStatsCollector? stats = null,
    TimeProvider? time = null,
    IImageEngine? engine = null,
    AppActions? actions = null,
    Railpack.RailpackBuildKit? buildKit = null) : BackgroundService
{
    /// <summary>Its item on the attention board, and the source that keeps it.</summary>
    public const string ItemKey = "disk";
    public const string Source = "disk";

    private static readonly TimeSpan NoBuildCacheSaidEvery = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly DateTime _startedAt = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private readonly IImageEngine _images = engine ?? api;
    // One run at a time, one look at a time, and one save of disk.state at a time.
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _look = new(1, 1);
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly Dictionary<DiskTier, DateTime> _notBefore = [];
    private DiskState? _state;
    private string? _saved;
    private DiskLevel _level;
    private DateTime? _lowSince;
    private DiskTier? _pending;
    private DateTime _noBuildCacheSaid = DateTime.MinValue;

    /// <summary>Raised after a run, or after a choice changed what it says, for open pages.</summary>
    public event Action? Changed;

    /// <summary>Reads Docker's disk; tests give their own.</summary>
    internal Func<DiskSpace>? ReadDisk { get; init; }

    // Unread, it's the stand-in for a disk that couldn't be read: plenty, and nothing done on a guess.
    private DiskSpace Disk() => ReadDisk is { } read ? read() : imageService?.Disk() ?? new DiskSpace(long.MaxValue / 4, long.MaxValue / 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        board.Dismissed += OnDismissed;
        try
        {
            await Task.Delay(DiskRules.FirstLook, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await LookAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The disk cleanup's look at Docker's disk failed");
                }
                await Task.Delay(DiskRules.LookEvery, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            board.Dismissed -= OnDismissed;
        }
    }

    // A person dismissed the banner: it stays hidden while it says the same, across a restart too (saved at the next look).
    private void OnDismissed(AttentionItem item)
    {
        if (item.Key != ItemKey) return;
        lock (_stateGate)
            if (_state is { } state) state.Dismissed = item.Dismiss;
    }

    /// <summary>One look at the disk, as at <paramref name="now"/>: the run that's due, then what to tell and what the board shows.</summary>
    internal async Task<DiskLook> LookAsync(DateTime now, CancellationToken ct = default)
    {
        await _look.WaitAsync(ct);
        try
        {
            var state = await StateAsync(ct);
            var disk = Disk();
            var level = disk.Known ? disk.Level : DiskLevel.Ok;
            if (level > _level) logger.LogWarning("{Title}: {Free}", DiskText.Title(level), DiskText.FreeOf(disk));
            _level = level;
            if (level == DiskLevel.Ok) (_lowSince, _pending) = (null, null);
            else _lowSince ??= now;

            var auto = await diskSettings.OnAsync(ct);
            DiskTier? tier;
            lock (_stateGate) tier = DiskRules.Due(now, _startedAt, disk, state, auto, _notBefore);
            // The daily run waits for deploys: one that's building would wait for it, and one that's queued may build any moment.
            if (tier == DiskTier.Routine && await DeployingAsync(ct))
            {
                WaitUntil(DiskTier.Routine, now);
                tier = null;
            }
            DiskResult? ran = null;
            if (tier is { } t)
            {
                using var actor = AuditScope.Begin(AuditActor.System(AuditVia.Cleanup));
                DiskResult? result = null;
                string? busy = null;
                try
                {
                    (result, busy) = await RunAsync(t, "Gitberth", now, ct);
                }
                catch (Exception ex) when (Failed(ex, ct))
                {
                    // Whatever failed (Gitberth's database, say), the run is tried again soon, not at every look.
                    logger.LogWarning(ex, "The disk cleanup ({Tier}) failed", t);
                    WaitUntil(t, now);
                }
                if (result is not null)
                {
                    ran = result;
                    disk = Disk();
                    _pending = null;
                    // Docker failed or refused a part of it: tried again soon, rather than at the tier's own pace.
                    if (result.Problem is not null) WaitUntil(t, now);
                }
                else if (busy is not null)
                {
                    // Tried again soon; the notice waits for it a little, so it says what the run freed.
                    WaitUntil(t, now);
                    if (t != DiskTier.Routine) _pending = t;
                }
            }

            var waits = _pending is not null && _lowSince is { } since && now - since < DiskRules.NoticeWaitsForRun;
            var told = waits ? DiskNotice.None : await TellAsync(state, disk, now, ran is { Tier: not DiskTier.Routine } urgent ? urgent.CacheFreed : 0, ct);
            var shown = Show(state, disk);
            await SaveAsync(ct);
            if (ran is not null) Changed?.Invoke();
            return new DiskLook(tier, ran, told, shown);
        }
        finally
        {
            _look.Release();
        }
    }

    // A run that waited or failed: tried again in 10 minutes (the daily one) or 2 (a low or almost full disk's).
    private void WaitUntil(DiskTier tier, DateTime now)
    {
        lock (_stateGate) _notBefore[tier] = now + (tier == DiskTier.Routine ? DiskRules.RoutineRetry : DiskRules.UrgentRetry);
    }

    /// <summary>
    /// Clean up now: the daily run, as the person who asked (and recorded so), whatever the switch says. Refused while a build
    /// (or Clear build cache) holds builds off, or while another run is on.
    /// </summary>
    public async Task<DiskAnswer> CleanNowAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var state = await StateAsync(ct);
        var by = AuditScope.Current is { Kind: AuditActorKind.User or AuditActorKind.ApiToken } who ? AuditActors.Describe(who) : "Gitberth";
        DiskResult? result;
        string? busy;
        try
        {
            (result, busy) = await RunAsync(DiskTier.Routine, by, now, ct);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            logger.LogWarning(ex, "Clean up now failed");
            return new DiskAnswer(false, DiskText.Couldnt(ex.Message));
        }
        if (result is null) return new DiskAnswer(false, busy ?? DiskText.AlreadyRunning);
        if (result.Problem is not null) WaitUntil(DiskTier.Routine, now);
        // What the dashboard says follows what the run freed; kept for a restart as well as it goes (the next look saves it).
        Show(state, Disk());
        try
        {
            await SaveAsync(ct);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            logger.LogWarning(ex, "The disk cleanup couldn't keep what it did for a restart");
        }
        Changed?.Invoke();
        return new DiskAnswer(result.Problem is null || result.Removed, DiskText.Cleaned(result), result);
    }

    /// <summary>What Settings › Disk shows: the disk, what the images take, the choices, the last run and the images of deleted apps.</summary>
    public async Task<DiskReport> ReportAsync(CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        long? imagesSize = null;
        IReadOnlyList<DeletedAppImages> deleted = [];
        try
        {
            imagesSize = await api.ImagesSizeAsync(ct);
            deleted = await DeletedAppsAsync(ct);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            logger.LogDebug(ex, "Settings › Disk couldn't read Docker's images");
        }
        DiskResult? last;
        lock (_stateGate) last = state.LastResult;
        return new DiskReport(Disk(), imagesSize, await diskSettings.OnAsync(ct), await diskSettings.KeepDaysAsync(ct), last, deleted, _run.CurrentCount == 0);
    }

    /// <summary>
    /// Every tag of Gitberth's (gitberth/&lt;slug&gt;…) whose app is gone, by app: kept on purpose when it was deleted, or
    /// waiting for a container to let go of it. Never one a live app names or keeps (its image, the one it follows or rolls
    /// back to, a Compose release's): that's the live app's.
    /// </summary>
    public async Task<IReadOnlyList<DeletedAppImages>> DeletedAppsAsync(CancellationToken ct = default)
    {
        var list = await _images.ListImagesAsync(ct);
        var (apps, kept) = await NamedAsync(list, ct);
        var slugs = apps.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
        var leftovers = DiskLeftovers.Read(await settings.GetAsync(SettingKeys.DiskLeftovers, ct));
        return [.. list.OfType<JsonObject>()
            .SelectMany(image => Tags(image).Select(tag => (Tag: tag, Image: image, Slug: DiskLeftovers.SlugOf(tag))))
            .Where(t => t.Slug is not null && !slugs.Contains(t.Slug) && !Named(kept, t.Tag, t.Image))
            .GroupBy(t => t.Slug!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DeletedAppImages(g.Key, [.. g.Select(t => t.Tag).Order(StringComparer.Ordinal)],
                g.Select(t => t.Image).DistinctBy(i => Str(i, "Id")).Sum(i => Number(i, "Size")),
                g.Any(t => leftovers.Any(l => l.Takes(t.Tag, Created(t.Image))))))];
    }

    /// <summary>
    /// Remove these images: the images of deleted apps the person saw and confirmed (<paramref name="confirmed"/>), as long as
    /// they're still a deleted app's: one listed since (another app deleted meanwhile) isn't touched. One a container uses
    /// stays, and the cleanup removes it once nothing does. Recorded in the audit log.
    /// </summary>
    public async Task<DiskAnswer> RemoveDeletedAppsImagesAsync(IReadOnlyCollection<string> confirmed, CancellationToken ct = default)
    {
        if (!await _run.WaitAsync(0, ct)) return new DiskAnswer(false, DiskText.AlreadyRunning);
        try
        {
            var listed = (await DeletedAppsAsync(ct)).SelectMany(g => g.Tags).ToHashSet(StringComparer.Ordinal);
            var removed = new List<string>();
            var stayed = new List<string>();
            var failed = new List<string>();
            string? failure = null;
            foreach (var tag in confirmed.Where(listed.Contains).Distinct(StringComparer.Ordinal))
            {
                switch (await RemoveAsync(tag, ex => failure ??= ex.Message, ct))
                {
                    case Removal.Removed:
                        removed.Add(tag);
                        break;
                    case Removal.Stays:
                        stayed.Add(tag);
                        break;
                    case Removal.Failed:
                        failed.Add(tag);
                        break;
                }
            }
            // What stays (a container uses it) or Docker failed to remove is the cleanup's to take later, as asked.
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var repo in stayed.Concat(failed).Select(LeftoverOf).Distinct())
                await DiskLeftovers.RecordAsync(dbFactory, repo.Repo, repo.Prefix, now, ct);
            if (removed.Count > 0)
                audit?.Record(new AuditEntry("settings.disk.images.removed", DiskText.RemovedDeleted(removed.Count, stayed.Count))
                {
                    Target = "Docker's disk", Changes = [new("Images removed", null, DiskText.ImagesLine(removed))],
                });
            Changed?.Invoke();
            return failure is null
                ? new DiskAnswer(stayed.Count == 0, DiskText.RemovedDeleted(removed.Count, stayed.Count))
                : new DiskAnswer(false, DiskText.RemovedDeletedFailed(removed.Count, failure));
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            return new DiskAnswer(false, $"Docker didn't remove them: {ex.Message.TrimEnd('.')}.");
        }
        finally
        {
            _run.Release();
        }
    }

    // A tag's leftover entry: gitberth/shop for gitberth/shop:1a2b3c4, the prefix gitberth/shop/ for gitberth/shop/web:….
    private static (string Repo, bool Prefix) LeftoverOf(string tag)
    {
        var slug = DiskLeftovers.SlugOf(tag)!;
        return DiskLeftovers.Repository(tag) == $"gitberth/{slug}" ? ($"gitberth/{slug}", false) : ($"gitberth/{slug}/", true);
    }

    // ---- A run ----

    /// <summary>A run under the build gate: its result, or why it didn't run (a build or Clear build cache holds the gate, or a run is on).</summary>
    private async Task<(DiskResult? Result, string? Busy)> RunAsync(DiskTier tier, string by, DateTime now, CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct)) return (null, DiskText.AlreadyRunning);
        try
        {
            using var hold = pipeline.TryHoldBuilds("the disk cleanup to finish", DiskText.Holding);
            if (hold is null)
            {
                logger.LogDebug("The disk cleanup ({Tier}) waits: {Holder}", tier, pipeline.Holder ?? "a build is running");
                return (null, DiskText.HeldOff(pipeline.Holder));
            }
            var keepDays = await diskSettings.KeepDaysAsync(ct);
            var result = await RunCoreAsync(tier, by, keepDays, now, ct);
            lock (_stateGate)
            {
                var state = _state ??= new DiskState();
                state.Last[tier] = new DiskRun(now, result.Freed, result.FreeAfter, by, Failed: result.Problem is not null);
                state.LastResult = result;
                if (tier != DiskTier.Routine && result.CacheFreed > 0) (state.ClearedBytes, state.ClearedAt) = (result.CacheFreed, now);
            }
            if (result.Removed)
                audit?.Record(new AuditEntry("settings.disk.cleaned", DiskText.Audit(result, keepDays))
                {
                    Target = "Docker's disk",
                    Changes = [.. Changes(result)],
                });
            logger.LogInformation("Disk cleanup ({Tier}): {Freed} freed, {Free} free now", tier, DiskText.Size(result.Freed), DiskText.Size(result.FreeAfter));
            return (result, null);
        }
        finally
        {
            _run.Release();
        }
    }

    private static IEnumerable<AuditChange> Changes(DiskResult r)
    {
        if (r.CacheBefore is { } before && r.CacheAfter is { } after) yield return new("Build cache", DiskText.Size(before), DiskText.Size(after));
        if (r.RailpackBefore is { } railpack && (r.RailpackAfter ?? 0) != railpack)
            yield return new("Railpack's build cache", DiskText.Size(railpack), DiskText.Size(r.RailpackAfter ?? 0));
        if (r.Images.Count > 0) yield return new("Images removed", null, DiskText.ImagesLine(r.Images));
        yield return new("Free", DiskText.Size(r.FreeBefore), DiskText.Size(r.FreeAfter));
    }

    private async Task<DiskResult> RunCoreAsync(DiskTier tier, string by, int keepDays, DateTime now, CancellationToken ct)
    {
        var before = Disk();
        // What Docker (or the database) said of the first part that failed: the run is tried again soon, and never rests.
        string? problem = null;
        var warned = false;
        void Warn(Exception ex, string what)
        {
            if (!warned) logger.LogWarning(ex, "The disk cleanup couldn't {What}: {Reason}", what, ex.Message);
            warned = true;
        }

        // 1 and 2: the build cache, by age, then down to its cap.
        long cacheFreed = 0;
        long? cacheBefore = null, cacheAfter = null;
        try
        {
            var byType = await api.ApiVersionAsync(ct) >= DockerApi.DiskUsageByType;
            if (byType) cacheBefore = (await api.BuildCacheAsync(ct)).Bytes;
            cacheFreed += await api.PruneUnusedBuildCacheAsync(DiskRules.Until(tier, keepDays), ct);
            if (byType)
            {
                var size = (await api.BuildCacheAsync(ct)).Bytes;
                var cap = DiskRules.CacheCap(before.TotalBytes);
                foreach (var step in DiskRules.CapSteps(tier, keepDays))
                {
                    if (size <= cap) break;
                    cacheFreed += await api.PruneUnusedBuildCacheAsync(step, ct);
                    size = (await api.BuildCacheAsync(ct)).Bytes;
                }
                cacheAfter = size;
            }
        }
        catch (DockerApiException ex) when (ex.Status is 404 or 501)
        {
            if (now - _noBuildCacheSaid >= NoBuildCacheSaidEvery)
                logger.LogInformation("This engine keeps no build cache Gitberth can clear ({Reason})", ex.Message);
            _noBuildCacheSaid = now;
        }
        catch (DockerApiException ex) when (ex.Status == 409)
        {
            // Docker's own prune (or BuildKit's) is on: the run comes back to it soon.
            logger.LogInformation("Docker is pruning its build cache already, so the disk cleanup left it: {Reason}", ex.Message);
            problem ??= ex.Message;
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            Warn(ex, "clear old build cache");
            problem ??= ex.Message;
        }

        // 2b: gitberth-buildkit's own cache, where Railpack's builds run, by the same age while it runs; once it's stopped and
        // no app builds with Railpack any more, the container and its volume go.
        Railpack.RailpackCacheChange? railpack = null;
        if (buildKit is not null)
        {
            try
            {
                railpack = await buildKit.CleanAsync(DiskRules.Until(tier, keepDays), await AnyRailpackAppAsync(ct), ct);
                cacheFreed += railpack?.Freed ?? 0;
            }
            catch (Exception ex) when (Failed(ex, ct))
            {
                Warn(ex, "clear Railpack's build cache");
                problem ??= ex.Message;
            }
        }

        // 3, 4 and 5: the images no app needs. One image Docker fails to remove is said once, and the rest go on.
        var removed = new List<string>();
        void Unremoved(Exception ex) => Warn(ex, "remove an image");
        try
        {
            var list = await _images.ListImagesAsync(ct);
            var (apps, kept) = await NamedAsync(list, ct);
            foreach (var id in Untagged(list, kept))
                if (await RemoveAsync(id, Unremoved, ct) == Removal.Removed) removed.Add(ShortId(id));
            removed.AddRange(await LeftoversAsync(list, apps, kept, Unremoved, ct));
            if (tier == DiskTier.Routine) removed.AddRange(await KeepSetsAsync(apps.Where(a => a.Deployed && a.CurrentImage is not null).Select(a => a.Id), ct));
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            Warn(ex, "remove the images no app needs");
            problem ??= ex.Message;
        }

        var after = Disk();
        return new DiskResult(now, tier, by, Math.Max(cacheFreed, after.FreeBytes - before.FreeBytes), cacheFreed, cacheBefore, cacheAfter, removed,
            before.FreeBytes, after.FreeBytes, after.TotalBytes, problem, railpack?.Before, railpack is null ? null : railpack.After ?? 0);
    }

    // Whether an app still builds with Railpack: its Build says so, or Railpack built it last (a project Gitberth doesn't know).
    private async Task<bool> AnyRailpackAppAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AnyAsync(a => a.DockerfileMode == DockerfileMode.Railpack || a.DetectedKind == nameof(Detection.ProjectKind.Railpack), ct);
    }

    // The apps as they are now, and every image (by tag or ID) one of them names or keeps (KeptIds).
    private async Task<(List<AppDefinition> Apps, HashSet<string> Kept)> NamedAsync(JsonArray list, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apps = await db.Apps.AsNoTracking().ToListAsync(ct);
        var deploys = await db.Deployments.AsNoTracking().Where(d => d.Images != null || d.ImageTag != null).Select(d => new { d.Images, d.ImageTag }).ToListAsync(ct);
        return (apps, KeptIds(list, apps, deploys.Select(d => (d.Images, d.ImageTag))));
    }

    /// <summary>
    /// The images (by ID, and by the reference that names them) nothing of Gitberth's may lose: the image every app runs (by
    /// its tag or ID), the one it ran before its last update, the one it follows (an app made from a local or registry
    /// image), and every image a deploy names (a Compose release's refs and IDs, kept or not, and every build's tag): the
    /// careful side.
    /// </summary>
    internal static HashSet<string> KeptIds(JsonArray list, IEnumerable<AppDefinition> apps, IEnumerable<(string? Images, string? ImageTag)> deploys)
    {
        var byTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in list.OfType<JsonObject>())
            if (Str(image, "Id") is { } id)
                foreach (var tag in Tags(image))
                    byTag[tag] = id;
        var kept = new HashSet<string>(StringComparer.Ordinal);
        void Keep(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return;
            if (byTag.TryGetValue(reference, out var id)) kept.Add(id);
            kept.Add(FullId(reference));
        }
        foreach (var app in apps)
        {
            Keep(app.CurrentImage);
            Keep(app.PreviousImage);
            Keep(app.ImageRef);
        }
        foreach (var (images, tag) in deploys)
        {
            Keep(tag);
            var parsed = ComposeImages.Parse(images);
            foreach (var reference in parsed.Refs.Values.Concat(parsed.Ids.Values)) Keep(reference);
        }
        return kept;
    }

    // A tag a live app names, or whose image one keeps by its ID: never a deleted app's to remove.
    private static bool Named(IReadOnlySet<string> kept, string tag, JsonObject image) => kept.Contains(tag) || Str(image, "Id") is { } id && kept.Contains(id);

    /// <summary>The untagged images of Gitberth's builds (labelled gitberth.app) that nothing keeps.</summary>
    internal static IEnumerable<string> Untagged(JsonArray list, IReadOnlySet<string> kept) => list.OfType<JsonObject>()
        .Where(image => Tags(image).Count == 0 && (image["Labels"] as JsonObject)?["gitberth.app"] is JsonValue)
        .Select(image => Str(image, "Id"))
        .OfType<string>()
        .Where(id => !kept.Contains(id));

    // 4: the images deleted apps' deletions couldn't remove, now that nothing may use them: only the ones made before the
    // deletion (a later app's with the slug are that app's), and never one a live app names.
    private async Task<List<string>> LeftoversAsync(JsonArray list, List<AppDefinition> apps, IReadOnlySet<string> kept, Action<Exception> failed, CancellationToken ct)
    {
        var removed = new List<string>();
        var entries = DiskLeftovers.Read(await settings.GetAsync(SettingKeys.DiskLeftovers, ct));
        if (entries.Count == 0) return removed;
        var tags = list.OfType<JsonObject>().SelectMany(image => Tags(image).Select(tag => (Tag: tag, Image: image)))
            .DistinctBy(t => t.Tag, StringComparer.Ordinal).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (apps.FirstOrDefault(a => a.Slug == entry.Slug) is { } app)
            {
                // The slug is an app's again: those tags are that app's now, and its own deploys prune them. A row older than
                // the entry is the app being deleted (its row goes a moment after its images): left for the next run.
                if (app.CreatedAt > entry.Since) done.Add(entry.Repo);
                continue;
            }
            var stays = false;
            foreach (var (tag, image) in tags.Where(t => entry.Takes(t.Tag, Created(t.Image))))
            {
                // One a live app names stays while it does: the next run takes it once none does.
                if (Named(kept, tag, image))
                {
                    stays = true;
                    continue;
                }
                switch (await RemoveAsync(tag, failed, ct))
                {
                    case Removal.Removed:
                        removed.Add(tag);
                        break;
                    case Removal.Stays or Removal.Failed:
                        stays = true;
                        break;
                }
            }
            if (!stays) done.Add(entry.Repo);
        }
        if (done.Count > 0) await DiskLeftovers.ChangeAsync(dbFactory, l => l.RemoveAll(e => done.Contains(e.Repo)) > 0, ct);
        return removed;
    }

    // 5: each Git or Compose app's images beyond its keep set, as after each of its deploys: for an earlier tidy-up that
    // failed. Each app is read again under its lock, right before its prune: never one deleted (or being deleted) since the
    // run began, nor one with a deploy queued or running now, nor one whose lock is held. What it says it removed is only
    // what these prunes removed.
    private async Task<List<string>> KeepSetsAsync(IEnumerable<int> appIds, CancellationToken ct)
    {
        var removed = new List<string>();
        foreach (var id in appIds)
        {
            using var held = await locks.TryAcquireAsync(id);
            if (held is null || actions?.IsDeleting(id) == true) continue;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is not { Deployed: true, CurrentImage: not null } app) continue;
            if (await db.Deployments.AnyAsync(d => d.AppId == id && (d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning
                    || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting), ct))
                continue;
            if (!app.IsCompose)
            {
                removed.AddRange(await pipeline.PruneImagesAsync(db, app));
                continue;
            }
            var running = await db.Deployments.AsNoTracking().Where(d => d.AppId == id && d.Status == DeployStatus.Succeeded)
                .OrderByDescending(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
            if (running is int current) removed.AddRange(await pipeline.PruneComposeAsync(db, app, current));
        }
        return [.. removed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private enum Removal { Removed, Gone, Stays, Failed }

    // Never forced: Docker refuses (409) an image a container uses, and it stays. Any other failure (a 500, a timeout) is
    // that image's alone: said (<paramref name="failed"/>), and the rest go on.
    private async Task<Removal> RemoveAsync(string idOrTag, Action<Exception> failed, CancellationToken ct)
    {
        try
        {
            await _images.RemoveImageAsync(idOrTag, ct);
            return Removal.Removed;
        }
        catch (DockerApiException ex) when (ex.NotFound)
        {
            return Removal.Gone;
        }
        catch (DockerApiException ex) when (ex.Status == 409)
        {
            return Removal.Stays;
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            failed(ex);
            return Removal.Failed;
        }
    }

    private async Task<bool> DeployingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AnyAsync(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building
            || d.Status == DeployStatus.Starting, ct);
    }

    // ---- What it says ----

    private async Task<DiskNotice> TellAsync(DiskState state, DiskSpace disk, DateTime now, long cleared, CancellationToken ct)
    {
        DiskNotice notice;
        lock (_stateGate)
        {
            notice = DiskRules.Tell(state.Told, state.LowToldAt, disk, now);
            if (notice == DiskNotice.None) return notice;
            state.Told = DiskRules.Told(state.Told, notice);
            if (notice == DiskNotice.Low) state.LowToldAt = now;
        }
        var (title, text) = DiskText.Notice(notice, disk, stats?.Current.Host?.Name, cleared);
        var (kind, level) = notice switch
        {
            DiskNotice.TooLow => (NotifyEvent.DiskLow, NoticeLevel.Critical),
            DiskNotice.Low => (NotifyEvent.DiskLow, NoticeLevel.Warning),
            _ => (NotifyEvent.DiskOk, NoticeLevel.Good),
        };
        notifier.Publish(new Notice(kind, level, title, text, await notifier.LinkAsync("settings#disk", ct), "", null, now));
        return notice;
    }

    // The dashboard's banner (and the top bar's pill when it's almost full), while the disk is low and until there's room again.
    private DiskLevel Show(DiskState state, DiskSpace disk)
    {
        AttentionItem? item = null;
        DiskLevel shown;
        lock (_stateGate)
        {
            shown = DiskRules.Shown(state.Shown, disk);
            state.Shown = shown;
            if (shown == DiskLevel.Ok) state.Dismissed = null;
            else
            {
                var cleared = state.ClearedBytes is long bytes && state.ClearedAt is { } at ? (bytes, at) : ((long, DateTime)?)null;
                item = new AttentionItem(ItemKey, Source, shown == DiskLevel.TooLow ? AttentionLevel.Critical : AttentionLevel.Warn, DiskText.Title(shown),
                    DiskText.Banner(shown, disk, cleared), "settings#disk", Tip: "dashboard.disk-low",
                    Actions: [new AttentionAction("See what takes the room", "settings#disk")],
                    // Getting low can be put aside; it shows again when it's almost full.
                    Dismiss: shown == DiskLevel.Low ? "low" : null)
                {
                    Pill = shown == DiskLevel.TooLow ? "Disk almost full" : null,
                    Problem = DiskText.Problem(shown, disk),
                };
            }
        }
        board.Replace(Source, item is null ? [] : [item]);
        return shown;
    }

    // ---- Kept across restarts ----

    private async Task<DiskState> StateAsync(CancellationToken ct)
    {
        lock (_stateGate)
            if (_state is { } known) return known;
        var json = await settings.GetAsync(SettingKeys.DiskState, ct);
        var state = DiskState.Parse(json);
        lock (_stateGate)
        {
            if (_state is { } raced) return raced;
            (_state, _saved) = (state, json);
        }
        if (state.Dismissed is { } stamp) board.KeepDismissed(ItemKey, stamp);
        return state;
    }

    // One save at a time (a look and Clean up now can both save), and counted as saved only once it's written: one that
    // failed is written by the next.
    private async Task SaveAsync(CancellationToken ct)
    {
        await _save.WaitAsync(ct);
        try
        {
            string? json;
            lock (_stateGate)
            {
                if (_state is null) return;
                json = _state.ToJson();
                if (json == _saved) return;
            }
            await settings.SetAsync(SettingKeys.DiskState, json, ct);
            lock (_stateGate) _saved = json;
        }
        finally
        {
            _save.Release();
        }
    }

    // ---- Docker's answers ----

    // Anything but the caller's own cancellation: Docker failing or refusing, a timeout, the database, an answer that can't be read.
    private static bool Failed(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private static List<string> Tags(JsonObject image) =>
        [.. (image["RepoTags"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .OfType<string>().Where(t => t != "<none>:<none>")];

    // When the image was made (Docker's Created, in seconds); null when it doesn't say.
    private static DateTime? Created(JsonObject image) =>
        image["Created"] is JsonValue v && v.TryGetValue<long>(out var seconds) && seconds > 0 ? DateTime.UnixEpoch.AddSeconds(seconds) : null;

    private static string? Str(JsonObject node, string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Number(JsonObject node, string key) => node[key] is JsonValue v && v.TryGetValue<long>(out var n) ? Math.Max(0, n) : 0;

    // An ID with its sha256: prefix; a reference stays as it is.
    private static string FullId(string reference) =>
        reference.Length == 64 && reference.All(Uri.IsHexDigit) ? "sha256:" + reference : reference;

    private static string ShortId(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return hex.Length > 12 ? hex[..12] : hex;
    }
}
