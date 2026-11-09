using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Images;

/// <summary>What an update needs from the catalog, the settings and the change engine; tests stand in for them.</summary>
internal interface IUpdateSteps
{
    /// <summary>The container as Docker describes it; null when there's none.</summary>
    Task<JsonObject?> InspectContainerAsync(string container, CancellationToken ct);
    Task<AppDefinition> AdoptAsync(string key, CancellationToken ct);
    Task SyncAsync(int appId, CancellationToken ct);
    Task<SettingsView?> LoadAsync(string key, CancellationToken ct);
    Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct);
    PortChangeJob Start(PortChangePlan plan);
    Task<string?> BusyWithAsync(string container, CancellationToken ct);
    Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct);
}

/// <summary>
/// Updates for every app that isn't built from a repo (section 4). What "update" means goes by where the image comes from:
/// <list type="bullet">
/// <item>Registry, once a registry is known to have the tag (a check found it, or Gitberth pulled it; see
/// <see cref="ImageOrigins"/>): it has a newer digest remotely (GET /distribution, compared with the RepoDigests of the
/// image the container runs). Checks are kept per reference in ImageChecks, so every app on a reference shows the same
/// badge.</item>
/// <item>Local, anything else (Gitberth's builds, the samples, what no registry has, what no check has found yet): the tag
/// names another image on the host now (a rebuild), checked for free whenever it's asked; for every tag, in fact.</item>
/// <item>Pinned: a digest or an image ID, so there's nothing to check.</item>
/// </list>
/// An update pulls (with progress), then recreates the container through the change engine with the image rebased, and
/// puts the old one back if the new one fails. The image it ran before is kept for Roll back; the one before that goes
/// once nothing uses it, and so does the one the tag named on the host before the pull moved it (after a rollback, the
/// image the app was rolled back from).
/// </summary>
public sealed partial class UpdateService
{
    /// <summary>How long a tag's image is kept between looks: the Apps list asks for every container at once.</summary>
    private static readonly TimeSpan TagKept = TimeSpan.FromSeconds(15);

    private readonly IUpdateSteps _steps;
    private readonly ImageService _images;
    private readonly IDbContextFactory<GitberthDb> _dbFactory;
    private readonly ILogger<UpdateService> _logger;
    // An image never changes under its ID; a tag can move at any time.
    private readonly ConcurrentDictionary<string, JsonObject?> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, JsonObject? Image)> _byTag = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<PortChangePlan, Prepared> _prepared = new();
    private readonly ConcurrentDictionary<string, Task> _followUps = new(StringComparer.Ordinal);

    /// <summary>An update or rollback as prepared: what the follow-up needs once the engine is done.</summary>
    private sealed record Prepared(string Key, int AppId, ChangeKind Kind, string OldImageId, string NewImageId, string Tracked, string? Digest)
    {
        /// <summary>The image Roll back went to before this update, which goes once nothing uses it.</summary>
        public string? Before { get; init; }

        /// <summary>
        /// The image the tag named on this host before the update's pull, when it's neither the one the app runs nor the new
        /// one (an update's image a rollback left, say): the pull took its name, so it goes once nothing uses it.
        /// </summary>
        public string? Superseded { get; init; }
    }

    /// <summary>A container whose image is followed by a tag, for the watcher.</summary>
    /// <param name="Image">The image it runs, as Docker describes it; null when Docker didn't say.</param>
    /// <param name="Policy">Its row's update policy; Tell me for a container without a row.</param>
    internal sealed record Followed(string Key, int? AppId, ImageReference Reference, int? RegistryId, string RunningId, IReadOnlyList<string> RunningDigests,
        JsonObject? Image, UpdatePolicy Policy);

    public UpdateService(AppCatalog catalog, AppSettingsService settings, ImageService images, PortChanger changer, IContainerEngine engine,
        IDbContextFactory<GitberthDb> dbFactory, ActivityLog activity, ILogger<UpdateService> logger)
        : this(new LiveSteps(catalog, settings, changer, engine, activity), images, dbFactory, logger)
    {
    }

    internal UpdateService(IUpdateSteps steps, ImageService images, IDbContextFactory<GitberthDb> dbFactory, ILogger<UpdateService> logger)
    {
        _steps = steps;
        _images = images;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Where the app's image comes from and whether there's a newer one, from the last check: no registry is asked. Said
    /// whatever its policy: an app set to When I ask shows what's known on its own page, only never as a badge.
    /// </summary>
    public async Task<UpdateStatus> StatusAsync(string key, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        var subject = SubjectOf(key, row, await _steps.InspectContainerAsync(key, ct));
        return await StatusOfAsync(subject, await ChecksAsync(ct), row?.PreviousImage, ct);
    }

    /// <summary>
    /// "Check now": asks the registry at once (one request), then says as <see cref="StatusAsync"/> does. The owner asked, so
    /// a local image is asked about too (a registry may have it after all), except Gitberth's own builds and the samples.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(string key, CancellationToken ct = default)
    {
        var row = await RowAsync(key, ct);
        var subject = SubjectOf(key, row, await _steps.InspectContainerAsync(key, ct));
        if (!subject.Git && ImageReference.Parse(subject.Tracked) is { Digest: null } reference
            && !ImageOrigins.NeverAsked(reference, subject.RunningImageId is { } id ? await ByIdAsync(id, ct) : null))
            await CheckReferenceAsync(reference, row?.RegistryId, ct);
        return await StatusOfAsync(subject, await ChecksAsync(ct), row?.PreviousImage, ct);
    }

    /// <summary>
    /// The update badge of each app, from the last checks and the images on the host: no registry is asked. Git apps get
    /// none (a new commit is their update), nor do apps set to When I ask: a check of the same reference for another app
    /// is no news for them. One that can't be worked out gets none rather than an error.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UpdateBadge>> BadgesAsync(IReadOnlyList<UpdateSubject> subjects, CancellationToken ct = default)
    {
        var checks = await ChecksAsync(ct);
        var badges = new ConcurrentDictionary<string, UpdateBadge>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(subjects.Where(s => !s.Git && s.Policy != UpdatePolicy.Off), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (s, token) =>
        {
            try
            {
                var status = await StatusOfAsync(s, checks, null, token);
                badges[s.Key] = new UpdateBadge(status.Available, status.Text, status.CheckedAt, status.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "No update badge for {Container}", s.Key);
            }
        });
        return badges;
    }

    /// <summary>
    /// Adopts the app, pulls its tag (progress reported; nothing for a local image), and plans the update with the change
    /// engine. The UI shows the plan and calls <see cref="Start"/>. Blocked when there's nothing to update, the pull
    /// failed, or the tag moved again while it was pulled.
    /// </summary>
    public async Task<UpdatePrepared> PrepareAsync(string key, IProgress<PullProgress>? progress = null, CancellationToken ct = default)
    {
        if ((await RowAsync(key, ct))?.Deployed == true) return Blocked("A GitHub app is updated by a new commit: use Deploy.");
        if (await _steps.InspectContainerAsync(key, ct) is not { } inspect) return Blocked($"{key} has no container to update: start it first.");
        var row = await _steps.AdoptAsync(key, ct);
        // The tag it runs, never another: an image changed in Settings is never taken back to the tag it was made from.
        var tracked = TrackedOf(row, inspect);
        if (ImageReference.Parse(tracked) is not { Digest: null } reference)
            return Blocked("It's pinned to a digest, so there's nothing to update. Give it a tag in Settings to follow one.");
        var runningId = Str(inspect, "Image");
        var running = await ByIdAsync(runningId, ct);

        PullResult? pull = null;
        string newId;
        string? tagged = null;
        // A registry known to have it is asked for it; anything else is this host's, and its tag says what's newest.
        if (ImageOrigins.Of(reference, (await ChecksAsync(ct)).GetValueOrDefault(reference.Canonical), running) == ImageOrigin.Registry)
        {
            // What the tag names here before the pull moves it: after a rollback, the image the app was rolled back from.
            tagged = await _images.InspectAsync(tracked!, ct) is { } had ? Str(had, "Id") : null;
            var job = _images.StartPull(tracked!, null, row.RegistryId);
            void Report() => progress?.Report(job.Progress);
            job.Changed += Report;
            try
            {
                pull = await job.Completion.WaitAsync(ct);
            }
            finally
            {
                job.Changed -= Report;
            }
            if (!pull.Ok || pull.ImageId is null) return new UpdatePrepared(pull, null, pull.Message);
            newId = pull.ImageId;
            Forget(tracked!);
            // Another pull of the tag between this one and the plan would update to an image nobody looked at.
            if (await _images.InspectAsync(tracked!, ct) is not { } now || Str(now, "Id") != newId)
                return new UpdatePrepared(pull, null, $"{reference.Familiar} moved again while it was pulled: check again.");
        }
        else
        {
            Forget(tracked!);
            if (await _images.InspectAsync(tracked!, ct) is not { } local) return Blocked($"{reference.Familiar} isn't on this host any more.");
            newId = Str(local, "Id");
            // Never back to an older image than it runs.
            if (newId != runningId && Older(local, running))
                return Blocked($"{reference.Familiar} names an older image ({Built(local)}) than the one {key} runs, so there's nothing to update.");
        }
        if (newId == runningId) return new UpdatePrepared(pull, null, $"It's up to date: {key} runs the image {reference.Familiar} names.");

        if (await _steps.LoadAsync(key, ct) is not { ContainerId: { } containerId } view) return new UpdatePrepared(pull, null, $"{key}'s settings couldn't be read.");
        var request = new ContainerEditRequest
        {
            ContainerId = containerId,
            Loaded = view.Container,
            Wanted = view.Container with { Image = tracked! },
            Kind = ChangeKind.Update,
            ExpectedImageId = newId,
            HealthPath = view.Options.HealthCheckPath,
            WebPort = view.Options.WebPort,
            SecretKeys = view.Options.SecretKeys,
        };
        var plan = await _steps.PlanAsync(key, request, ct);
        _prepared.AddOrUpdate(plan, new Prepared(key, row.Id, ChangeKind.Update, runningId, newId, tracked!, pull?.Digest)
        {
            Before = row.PreviousImage,
            Superseded = tagged is not null && tagged != runningId && tagged != newId ? tagged : null,
        });
        return new UpdatePrepared(pull, plan, plan.Blocked);
    }

    /// <summary>Plans going back to the image the app ran before its last update (an ID, so nothing is pulled).</summary>
    public async Task<UpdatePrepared> PrepareRollbackAsync(string key, CancellationToken ct = default)
    {
        if ((await RowAsync(key, ct))?.PreviousImage is not { } previous) return Blocked("There's no earlier image to go back to: Gitberth hasn't updated it.");
        if (await _steps.InspectContainerAsync(key, ct) is not { } inspect) return Blocked($"{key} has no container: start it first.");
        if (await _images.InspectAsync(previous, ct) is not { } image) return Blocked($"The image it ran before ({ImageService.Short(previous)}) isn't on this host any more.");
        var row = await _steps.AdoptAsync(key, ct);
        var runningId = Str(inspect, "Image");
        var previousId = Str(image, "Id");
        if (runningId == previousId) return Blocked($"It runs that image ({ImageService.Short(previousId)}) already.");
        if (await _steps.LoadAsync(key, ct) is not { ContainerId: { } containerId } view) return Blocked($"{key}'s settings couldn't be read.");
        // By its repository's digest (postgres@sha256:…) when it has one, as a recreate pins an image, so it keeps its name
        // (and stays a database to Gitberth); by its ID only without one.
        var tracked = TrackedOf(row, inspect) ?? Str(inspect["Config"], "Image");
        var digests = ImageService.Strings(image["RepoDigests"]).Where(d => d.Contains("@sha256:", StringComparison.Ordinal)).ToList();
        var named = digests.FirstOrDefault(d => ContainerService.Repository(d) == ContainerService.Repository(tracked)) ?? digests.FirstOrDefault() ?? previousId;
        var request = new ContainerEditRequest
        {
            ContainerId = containerId,
            Loaded = view.Container,
            Wanted = view.Container with { Image = named },
            Kind = ChangeKind.Rollback,
            ExpectedImageId = previousId,
            HealthPath = view.Options.HealthCheckPath,
            WebPort = view.Options.WebPort,
            SecretKeys = view.Options.SecretKeys,
        };
        var plan = await _steps.PlanAsync(key, request, ct);
        _prepared.AddOrUpdate(plan, new Prepared(key, row.Id, ChangeKind.Rollback, runningId, previousId, tracked, null));
        return new UpdatePrepared(null, plan, plan.Blocked);
    }

    /// <summary>
    /// Runs an update or rollback plan (PortChanger.Start) and, when the job ends Done or WaitsForStart: PreviousImage,
    /// removing the image before it when unused, ImageChecks and AppCatalog.SyncAsync. Its Activity record is the engine's
    /// journal row, so nothing is written there twice.
    /// </summary>
    public PortChangeJob Start(PortChangePlan plan)
    {
        var job = _steps.Start(plan);
        // A change already running for the container is returned as it is: it isn't this update, so nothing follows it.
        if (ReferenceEquals(job.Plan, plan) && _prepared.TryGetValue(plan, out var prepared))
            _followUps[plan.Container] = FollowUpAsync(job, prepared);
        return job;
    }

    /// <summary>The follow-up of the container's last update or rollback; done when there's none.</summary>
    internal Task FollowUpOf(string container) => _followUps.TryGetValue(container, out var task) ? task : Task.CompletedTask;

    /// <summary>
    /// Asks registries about the references due a check: each one once however many apps follow it, not before its
    /// RetryAfter, and not within <paramref name="every"/> of its last check. Asked every few hours, and only those: what a
    /// registry is known to have (a check found it, Gitberth pulled it), what the owner checked by hand, and what an app
    /// names a sign-in for. Never asked by itself: an image the host has with no such record (a local build such as
    /// family-photos:dev stays local until the owner presses Check for update), Gitberth's own builds and the samples
    /// (<see cref="ImageOrigins.NeverAsked"/>), and what only apps set to When I ask follow. A local image's check is free
    /// and made whenever it's shown. A newer digest is written to the Activity of each app that isn't set to When I ask, once.
    /// </summary>
    /// <returns>The references checked.</returns>
    internal async Task<IReadOnlyList<string>> CheckDueAsync(TimeSpan every, CancellationToken ct)
    {
        // An app set to When I ask is checked only when its owner presses Check for update.
        var followed = (await FollowedAsync(ct)).Where(f => f.Policy != UpdatePolicy.Off && !ImageOrigins.NeverAsked(f.Reference, f.Image)).ToList();
        var checks = await ChecksAsync(ct);
        var references = followed.Select(f => f.Reference.Canonical).Distinct(StringComparer.Ordinal).ToList();
        // Only what a registry is known to have, or what someone asked about: Gitberth pulled it or a check found it
        // (RemoteDigest), the owner checked it by hand (a check row that isn't "only on this host"), or the app names a
        // sign-in for it. An image the host has with no such record is local until the owner presses Check for update.
        var known = references.Where(r => checks.GetValueOrDefault(r) is { } check && !ImageOrigins.OnlyOnHost(check)
                || followed.Any(f => f.Reference.Canonical == r && f.RegistryId is not null))
            .ToList();
        var due = Due(known, checks, Now, every);
        foreach (var canonical in due)
        {
            var on = followed.Where(f => f.Reference.Canonical == canonical).ToList();
            var before = checks.GetValueOrDefault(canonical)?.RemoteDigest;
            var check = await CheckReferenceAsync(on[0].Reference, on.Select(f => f.RegistryId).FirstOrDefault(id => id is not null), ct);
            if (check.RemoteDigest is not { } latest || latest == before) continue;
            foreach (var f in on.Where(f => !f.RunningDigests.Contains(latest)))
                await _steps.ActivityAsync(f.Key, f.AppId, ActivityKind.UpdateFound, $"{f.Reference.Familiar} has a newer image ({Abbrev(latest)}).", ActivityTone.Neutral, ct);
        }
        return due;
    }

    /// <summary>
    /// The automatic updates (4.3): every app whose policy is Auto and that has an update, except what's never updated by
    /// itself (<see cref="AutoRefusal"/>) and a container whose change isn't settled. Each is prepared and run as the owner
    /// would; one that fails is rolled back by the engine, its policy goes back to Tell me (Notify) and its Activity says why.
    /// </summary>
    /// <param name="self">Gitberth's own container (its ID or name).</param>
    /// <returns>The containers updated.</returns>
    internal async Task<IReadOnlyList<string>> AutoUpdateAsync(string? self, CancellationToken ct)
    {
        List<AppDefinition> rows;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            rows = await db.Apps.AsNoTracking().Where(a => a.UpdatePolicy == UpdatePolicy.Auto && a.Source != AppSource.Git).ToListAsync(ct);
        var updated = new List<string>();
        foreach (var row in rows)
        {
            var key = row.ContainerName;
            if (await _steps.InspectContainerAsync(key, ct) is not { } inspect) continue;
            // What the image it runs declares counts too (a database's port or data folder): in doubt, it isn't updated by itself.
            var runs = Str(inspect, "Image") is { Length: > 0 } runningId ? await ByIdAsync(runningId, ct) : null;
            if ((AutoRefusal(row, inspect, self) ?? (ContainerService.IsDatabase(Str(inspect["Config"], "Image"), runs?["Config"], row.ImageRef)
                    ? "Databases are never updated by themselves." : null)) is { } why)
            {
                _logger.LogDebug("No automatic update for {Container}: {Why}", key, why);
                continue;
            }
            if (await _steps.BusyWithAsync(key, ct) is not null || !(await StatusAsync(key, ct)).Available) continue;
            var prepared = await PrepareAsync(key, null, ct);
            if (prepared.Plan is not { CanApply: true } plan || prepared.Blocked is not null)
            {
                _logger.LogInformation("The automatic update of {Container} waits: {Why}", key, prepared.Blocked ?? prepared.Plan?.Blocked ?? "it can't be applied");
                continue;
            }
            var job = Start(plan);
            var result = await job.Completion;
            await FollowUpOf(key);
            if (result.Ok)
            {
                updated.Add(key);
                continue;
            }
            // Nothing was changed (another change of it ran, or the tag moved again): the next window tries again.
            if (result.Outcome == PortChangeOutcome.NothingChanged)
            {
                _logger.LogInformation("The automatic update of {Container} waits: {Why}", key, result.Message);
                continue;
            }
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
                await db.Apps.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatePolicy, UpdatePolicy.Notify), ct);
            var tracked = row.ImageRef ?? Str(inspect["Config"], "Image");
            await _steps.ActivityAsync(key, row.Id, ActivityKind.Updated,
                $"Automatic update to {tracked} failed and was rolled back: {result.Message} Updates are back to Tell me until you choose Automatically again.",
                ActivityTone.Warning, ct);
        }
        return updated;
    }

    /// <summary>
    /// Why an app is never updated by itself, whatever its policy says; null when it may be. Databases, Gitberth itself,
    /// and containers Compose, Swarm or Kubernetes look after are never.
    /// </summary>
    internal static string? AutoRefusal(AppDefinition row, JsonObject inspect, string? self)
    {
        if (row.Deployed) return "A GitHub app is updated by a new commit.";
        if (ContainerService.IsDatabase(Str(inspect["Config"], "Image"), inspect["Config"], row.ImageRef)) return "Databases are never updated by themselves.";
        var labels = inspect["Config"]?["Labels"] as JsonObject;
        var id = Str(inspect, "Id");
        var name = Str(inspect, "Name").TrimStart('/');
        if (self is { Length: > 0 } && (id.StartsWith(self, StringComparison.Ordinal) || name == self)
            || Str(labels, RecreateRules.ComposeProjectLabel) == "gitberth" && Str(labels, "com.docker.compose.service") == "gitberth")
            return "Gitberth doesn't update itself.";
        if (labels?.ContainsKey(RecreateRules.ComposeProjectLabel) == true) return "Compose looks after it.";
        if (labels?.Any(l => l.Key.StartsWith("com.docker.swarm.", StringComparison.Ordinal)) == true) return "Swarm looks after it.";
        if (labels?.Any(l => l.Key.StartsWith("io.kubernetes.", StringComparison.Ordinal)) == true) return "Kubernetes looks after it.";
        return null;
    }

    /// <summary>The references to check now: each once, not before its RetryAfter, and not within <paramref name="every"/> of the last check.</summary>
    internal static List<string> Due(IEnumerable<string> references, IReadOnlyDictionary<string, ImageCheck> checks, DateTime now, TimeSpan every) =>
        references.Distinct(StringComparer.Ordinal)
            .Where(r => !checks.TryGetValue(r, out var c) || (c.RetryAfter is not { } retry || retry <= now) && c.CheckedAt + every <= now)
            .ToList();

    /// <summary>
    /// Asks the registry about one reference and keeps the answer. No such reference there, or refused when Gitberth has no
    /// sign-in to send (Docker Hub refuses what doesn't exist), means it's only on this host: not asked by the watcher for
    /// a week (<see cref="ImageOrigins.OnlyHereFor"/>). A refused sign-in waits 24 hours before it's asked again.
    /// </summary>
    internal async Task<ImageCheck> CheckReferenceAsync(ImageReference reference, int? registryId, CancellationToken ct)
    {
        var remote = await _images.ResolveAsync(reference.Canonical, registryId, ct);
        var onlyHere = !remote.Found && (remote.NotFound || remote.Unauthorized && !await _images.SignedInAsync(reference, registryId, ct));
        var now = Now;
        return await SaveCheckAsync(_dbFactory, reference.Canonical, check =>
        {
            check.CheckedAt = now;
            if (remote.Found)
            {
                check.RemoteDigest = remote.Digest;
                check.Error = null;
                check.RetryAfter = null;
            }
            else if (onlyHere)
            {
                check.RemoteDigest = null;
                check.Error = ImageOrigins.OnlyHere;
                check.RetryAfter = now + ImageOrigins.OnlyHereFor;
            }
            else
            {
                check.Error = remote.Unauthorized ? "Can't check: sign-in failed." : $"Can't check: {remote.Error}";
                check.RetryAfter = remote.Unauthorized ? now.AddHours(24) : null;
            }
        }, ct);
    }

    /// <summary>
    /// The reference's check, written whoever gets there first (an upsert): two first checks of the same reference (the
    /// watcher and an owner's Check, two apps on it, or a check and a pull) both find no row, and the second insert would
    /// collide with the first's; it writes over that one instead.
    /// </summary>
    internal static async Task<ImageCheck> SaveCheckAsync(IDbContextFactory<GitberthDb> dbFactory, string canonical, Action<ImageCheck> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var check = await db.ImageChecks.FirstOrDefaultAsync(c => c.Reference == canonical, ct);
            if (check is null) db.ImageChecks.Add(check = new ImageCheck { Reference = canonical });
            write(check);
            try
            {
                await db.SaveChangesAsync(ct);
                return check;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // Inserted meanwhile by the other check: read again, and update it.
            }
        }
    }

    /// <summary>The tracked reference and running image of a container, from its row (if any) and its inspect.</summary>
    internal static UpdateSubject SubjectOf(string key, AppDefinition? row, JsonObject? inspect)
    {
        var tracked = TrackedOf(row, inspect);
        var running = inspect is null ? null : Str(inspect, "Image") is { Length: > 0 } id ? id : null;
        return new UpdateSubject(key, tracked, running, row?.Deployed == true);
    }

    /// <param name="previousId">The image the app ran before its last update (its row's PreviousImage); null for none.</param>
    internal async Task<UpdateStatus> StatusOfAsync(UpdateSubject s, IReadOnlyDictionary<string, ImageCheck> checks, string? previousId, CancellationToken ct)
    {
        var previous = await PreviousAsync(s, previousId, ct);
        return await StatusCoreAsync(s, checks, previous is not null, ct) with { Previous = previous };
    }

    /// <summary>
    /// The image Roll back goes to, as its button names it ("postgres:18 (3a42…)"): only while it's still on this host and
    /// isn't the one the container runs. Looked up each time, never kept: anyone can remove it.
    /// </summary>
    private async Task<string?> PreviousAsync(UpdateSubject s, string? previousId, CancellationToken ct)
    {
        if (s.Git || previousId is null || await _images.InspectAsync(previousId, ct) is not { } image) return null;
        var id = Str(image, "Id");
        if (id == s.RunningImageId) return null;
        var reference = ImageReference.Parse(s.Tracked);
        // Its digest in the followed repository, as the Running and Latest lines show theirs; its ID without one.
        var digest = ImageService.Strings(image["RepoDigests"])
            .Where(d => reference is null || ImageReference.Parse(d) is { } named && named.FromImage == reference.FromImage)
            .Select(ImageService.DigestOf).OfType<string>().FirstOrDefault();
        return $"{reference?.Familiar ?? "its previous image"} ({Abbrev(digest ?? id)})";
    }

    private async Task<UpdateStatus> StatusCoreAsync(UpdateSubject s, IReadOnlyDictionary<string, ImageCheck> checks, bool canRollBack, CancellationToken ct)
    {
        if (s.Git) return new UpdateStatus(ImageOrigin.Built, null, false, null, null, null, "Built from its repo: a new commit is its update.", null, false);
        var running = s.RunningImageId is null ? null : await ByIdAsync(s.RunningImageId, ct);
        var runningDigests = Digests(running);
        if (ImageReference.Parse(s.Tracked) is not { Digest: null } reference)
            return new UpdateStatus(ImageOrigin.Pinned, s.Tracked, false, runningDigests.FirstOrDefault(), null, null, "Pinned to a digest: nothing to check.", null, canRollBack);

        var check = checks.GetValueOrDefault(reference.Canonical);
        // The free check, for every tag: it names another image on this host than the one the container runs (a rebuild, or
        // someone's pull).
        var tagged = await ByTagAsync(s.Tracked!, ct);
        var newerHere = running is not null && tagged is not null && Str(tagged, "Id") != Str(running, "Id") && !Older(tagged, running);
        if (ImageOrigins.Of(reference, check, running) == ImageOrigin.Local)
        {
            if (tagged is null)
                return new UpdateStatus(ImageOrigin.Local, s.Tracked, false, null, null, Now, $"{reference.Familiar} isn't on this host any more.", null, canRollBack);
            if (newerHere) return new UpdateStatus(ImageOrigin.Local, s.Tracked, true, null, null, Now, $"A newer local image ({Built(tagged)})", null, canRollBack);
            // Never asked about, or no registry has it: the tag is all there is. Otherwise no check has found it yet, or one failed.
            if (ImageOrigins.NeverAsked(reference, running) || ImageOrigins.OnlyOnHost(check))
                return new UpdateStatus(ImageOrigin.Local, s.Tracked, false, null, null, Now, $"Up to date: it runs the image {reference.Familiar} names.", null, canRollBack);
            return new UpdateStatus(ImageOrigin.Local, s.Tracked, false, null, null, check?.CheckedAt, check?.Error ?? "Not checked yet.", check?.Error, canRollBack);
        }

        var runningDigest = runningDigests.FirstOrDefault();
        var remoteNewer = check!.RemoteDigest is { } latest && running is not null && !runningDigests.Contains(latest);
        var text = check.Error ?? (remoteNewer ? $"Update available: {reference.Familiar} ({Abbrev(check.RemoteDigest!)})"
            : newerHere ? $"A newer image of {reference.Familiar} is on this host ({Built(tagged!)})"
            : $"Up to date ({reference.Familiar}).");
        return new UpdateStatus(ImageOrigin.Registry, s.Tracked, remoteNewer || newerHere, runningDigest, check.RemoteDigest, check.CheckedAt, text, check.Error, canRollBack);
    }

    // Every container that follows a tag, helpers and Gitberth's temporaries left out.
    private async Task<List<Followed>> FollowedAsync(CancellationToken ct)
    {
        List<AppDefinition> rows;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            rows = await db.Apps.AsNoTracking().ToListAsync(ct);
        var byName = rows.GroupBy(r => r.ContainerName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var followed = new List<Followed>();
        foreach (var c in (await _images.ContainersAsync(ct)).OfType<JsonObject>())
        {
            var name = NameOf(c);
            var labels = c["Labels"] as JsonObject;
            if (name.Length == 0 || labels?.ContainsKey(RecreateRules.HelperLabel) == true || Temporary().IsMatch(name)
                || labels?.ContainsKey(Gitberth.Docker.ContainerSpecFactory.BridgeLabel) == true) continue;
            var row = byName.GetValueOrDefault(name);
            if (row?.Deployed == true) continue;
            // The tag it runs, else its row's. Docker lists the image by its ID once the tag has moved on: the container's own
            // settings still name the tag.
            var tracked = TrackableOrNull(Str(c, "Image"));
            if (tracked is null && await _steps.InspectContainerAsync(name, ct) is { } inspect) tracked = TrackableOrNull(Str(inspect["Config"], "Image"));
            tracked ??= row?.ImageRef;
            if (ImageReference.Parse(tracked) is not { Digest: null } reference) continue;
            var runningId = Str(c, "ImageID");
            var image = await ByIdAsync(runningId, ct);
            followed.Add(new Followed(name, row?.Id, reference, row?.RegistryId, runningId, Digests(image), image, row?.UpdatePolicy ?? UpdatePolicy.Notify));
        }
        return followed;
    }

    private async Task FollowUpAsync(PortChangeJob job, Prepared p)
    {
        PortChangeResult result;
        try
        {
            result = await job.Completion;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The update of {Container} ended without a result", p.Key);
            return;
        }
        try
        {
            // The engine's journal row is the update's record in Activity, whatever its end: nothing is written here.
            if (!result.Ok) return;

            string? before;
            var paused = false;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                if (await db.Apps.FirstOrDefaultAsync(a => a.Id == p.AppId) is not { } row) return;
                before = p.Before ?? row.PreviousImage;
                // After a rollback there's nothing to go back to: the newer image is still the tag's, and Update offers it again.
                row.PreviousImage = p.Kind == ChangeKind.Update ? p.OldImageId : null;
                // The owner chose the older image: automatic updates don't take it forward again the next night.
                if (p.Kind == ChangeKind.Rollback && row.UpdatePolicy == UpdatePolicy.Auto)
                {
                    row.UpdatePolicy = UpdatePolicy.Notify;
                    paused = true;
                }
                await db.SaveChangesAsync();
            }
            if (paused)
                await _steps.ActivityAsync(p.Key, p.AppId, ActivityKind.Settings,
                    "Automatic updates paused after a roll back: updates are back to Tell me until you choose Automatically again.", ActivityTone.Neutral, CancellationToken.None);
            if (p.Kind == ChangeKind.Update && before is not null && before != p.OldImageId && before != p.NewImageId
                && await _images.RemoveIfUnusedAsync(before) is { } kept)
                _logger.LogInformation("The image {Image} {Container} ran two updates ago stays: {Why}", ImageService.Short(before), p.Key, kept);
            // The rollback that left it assumed the tag still named it; the pull moved the tag on, so nothing else would remove it.
            if (p.Kind == ChangeKind.Update && p.Superseded is { } superseded && superseded != before
                && await _images.RemoveIfUnusedAsync(superseded) is { } stays)
                _logger.LogInformation("The image {Image} the tag named before {Container}'s update stays: {Why}", ImageService.Short(superseded), p.Key, stays);
            if (p.Kind == ChangeKind.Update && p.Digest is not null && ImageReference.Parse(p.Tracked) is { Digest: null } reference)
                await RecordAsync(reference.Canonical, p.Digest);
            Forget(p.Tracked);
            await _steps.SyncAsync(p.AppId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tidying up after the update of {Container} failed", p.Key);
        }
    }

    // The digest just pulled is what the registry has: the badge needn't wait for the next check to say so.
    private async Task RecordAsync(string canonical, string digest)
    {
        var now = Now;
        await SaveCheckAsync(_dbFactory, canonical, check =>
        {
            check.RemoteDigest = digest;
            check.CheckedAt = now;
            check.Error = null;
            check.RetryAfter = null;
        }, CancellationToken.None);
    }

    private async Task<AppDefinition?> RowAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key["gb-".Length..] : null;
        return await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Container == key || (slug != null && a.Container == null && a.Slug == slug), ct);
    }

    private async Task<IReadOnlyDictionary<string, ImageCheck>> ChecksAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ImageChecks.AsNoTracking().ToDictionaryAsync(c => c.Reference, StringComparer.Ordinal, ct);
    }

    private async Task<JsonObject?> ByIdAsync(string id, CancellationToken ct)
    {
        if (_byId.TryGetValue(id, out var known)) return known;
        var image = await _images.InspectAsync(id, ct);
        // A gone image may come back under a new ID, never under this one; but don't keep a miss for good.
        if (image is not null) _byId[id] = image;
        return image;
    }

    private async Task<JsonObject?> ByTagAsync(string tag, CancellationToken ct)
    {
        if (_byTag.TryGetValue(tag, out var kept) && Now - kept.At < TagKept) return kept.Image;
        var image = await _images.InspectAsync(tag, ct);
        _byTag[tag] = (Now, image);
        return image;
    }

    private void Forget(string tag) => _byTag.TryRemove(tag, out _);

    private static UpdatePrepared Blocked(string why) => new(null, null, why);

    private static string? TrackableOrNull(string image) => ImageReference.Parse(image) is { Digest: null } ? image : null;

    /// <summary>The tag an update follows: the one the container runs when it runs a tag, else its row's (AppCatalog.TrackedOf).</summary>
    private static string? TrackedOf(AppDefinition? row, JsonObject? inspect) =>
        TrackableOrNull(Str(inspect?["Config"], "Image")) ?? row?.ImageRef;

    // The tag names an image built before the one the container runs: never offered as an update.
    private static bool Older(JsonObject? image, JsonObject? running) =>
        Created(image) is { } a && Created(running) is { } b && a < b;

    private static DateTime? Created(JsonObject? image) =>
        DateTime.TryParse(Str(image, "Created"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>The digests of an image's RepoDigests: sha256:… of postgres@sha256:….</summary>
    internal static List<string> Digests(JsonObject? image) =>
        ImageService.Strings(image?["RepoDigests"]).Select(ImageService.DigestOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>An ID or digest as Activity writes it: 3a42….</summary>
    internal static string Abbrev(string id) => ImageService.Short(id) is { Length: > 4 } s ? s[..4] + "…" : id;

    // "built 3 h ago", from the image's Created.
    private string Built(JsonObject image)
    {
        if (!DateTime.TryParse(Str(image, "Created"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
            return "built recently";
        var age = Now - created;
        return age < TimeSpan.FromMinutes(1) ? "built just now"
            : age < TimeSpan.FromHours(1) ? $"built {(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(2) ? $"built {(int)age.TotalHours} h ago"
            : $"built {(int)age.TotalDays} days ago";
    }

    private static string NameOf(JsonObject listed) =>
        ImageService.Strings(listed["Names"]).Select(n => n.TrimStart('/')).FirstOrDefault(n => !n.Contains('/')) ?? "";

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    // A change's copies (name_new_id12, name_old_id12) and a deploy's (gb-x_next, gb-x_prev) are folded into their app.
    [GeneratedRegex("(?:_(?:new|old)_[0-9a-f]{12}|_(?:next|prev))$")]
    private static partial Regex Temporary();

    private sealed class LiveSteps(AppCatalog catalog, AppSettingsService settings, PortChanger changer, IContainerEngine engine, ActivityLog activity) : IUpdateSteps
    {
        public Task<JsonObject?> InspectContainerAsync(string container, CancellationToken ct) => engine.InspectContainerAsync(container, ct);
        public Task<AppDefinition> AdoptAsync(string key, CancellationToken ct) => catalog.AdoptAsync(key, ct);
        public Task SyncAsync(int appId, CancellationToken ct) => catalog.SyncAsync(appId, ct);
        public Task<SettingsView?> LoadAsync(string key, CancellationToken ct) => settings.LoadAsync(key, ct);
        public Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct) => changer.PlanAsync(container, request, ct);
        public PortChangeJob Start(PortChangePlan plan) => changer.Start(plan);
        public Task<string?> BusyWithAsync(string container, CancellationToken ct) => changer.BusyWithAsync(container, ct);
        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct) =>
            activity.AddAsync(container, appId, kind, text, tone, ct);
    }
}
