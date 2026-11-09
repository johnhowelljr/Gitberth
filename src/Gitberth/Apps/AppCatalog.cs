using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Apps;

/// <summary>
/// Every app, rows and live containers, one list (1.3): a container without a row is an app all the same, of source
/// Existing, and gets a row only the first time Gitberth has to store something about it (<see cref="AdoptAsync"/>).
/// Reads Docker only through NetworkService (its snapshot, inspects and image digests) and AddressBook, and never writes
/// to Docker: it writes rows only. Helpers are left out; a deploy's temporary containers and the two containers of a
/// change that isn't settled are folded into their app.
/// </summary>
/// <param name="updates">The update badges (UpdateService, which depends on this class): when it fails, there are no badges, never an error.</param>
public sealed class AppCatalog(IDbContextFactory<GitberthDb> dbFactory, NetworkService network, AddressBook book, AppService apps,
    ContainerStatsCollector stats, ContainerTitles titles, HealthSettings health, RestartSchedules restarts, IChangeJournal journal,
    DockerCli docker, DomainService domains, SecretProtector protector, GitberthOptions options, ActivityLog activity,
    Lazy<UpdateService> updates, ILogger<AppCatalog> logger)
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { Converters = { new JsonStringEnumConverter() } };

    // One adoption of a container at a time: a second one waits, then finds the row the first made.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _adopting = new(StringComparer.Ordinal);

    // The containers Gitberth is creating again from saved settings, and since when: their first start isn't a recreate
    // made outside Gitberth (AppSync).
    private readonly ConcurrentDictionary<string, DateTime> _creating = new(StringComparer.Ordinal);
    private static readonly TimeSpan CreateWindow = TimeSpan.FromMinutes(15);

    /// <summary>Raised after any write, so the list and the menu refresh.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads a container's settings from its inspect and its image's (ContainerSettingsJson.Read, the change engine's);
    /// stands in for it in tests.
    /// </summary>
    internal Func<JsonObject, JsonObject?, ContainerSettings> ReadSettings { get; init; } = ContainerSettingsJson.Read;

    /// <summary>For the app actions, which remove rows through the domains: the list and the menu refresh.</summary>
    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>Gitberth is about to create the container again from its saved settings (the change engine's create).</summary>
    internal void Creating(string container) => _creating[container] = DateTime.UtcNow;

    /// <summary>Whether Gitberth created the container a moment ago; it's told once.</summary>
    internal bool Created(string container) =>
        _creating.TryRemove(container, out var at) && DateTime.UtcNow - at < CreateWindow;

    /// <summary>Every app: pinned first, then by title. Helpers left out; deploy and change temporaries folded into their app.</summary>
    public async Task<AppList> ListAsync(string lanHost, CancellationToken ct = default)
    {
        var (entries, error) = await ComposeAsync(lanHost, only: null, ct);
        return new AppList(entries.Select(e => e.Summary).ToList(), error);
    }

    /// <summary>One app by key (container name), or by a legacy numeric id; null when neither a row nor a container has it.</summary>
    public async Task<AppView?> GetAsync(string key, string lanHost, CancellationToken ct = default)
    {
        // A number names a row first: the old /apps/{id} links.
        if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && await KeyForIdAsync(id, ct) is { } byId)
            key = byId;
        var (entries, _) = await ComposeAsync(lanHost, only: key, ct);
        if (entries.FirstOrDefault() is not { } entry) return null;
        var summary = entry.Summary;
        AppDefinition? row = null;
        if (summary.AppId is int appId)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            row = await db.Apps.AsNoTracking()
                .Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Domains).Include(a => a.Networks).AsSplitQuery()
                .FirstOrDefaultAsync(a => a.Id == appId, ct);
        }
        // By ID: while a change isn't settled, the container standing for the app may go by the change's other name.
        JsonObject? inspect = null;
        ContainerDetails? details = null;
        if (summary.ContainerId is { } containerId)
        {
            try
            {
                inspect = await network.InspectAsync(containerId, ct);
            }
            catch (Exception ex) when (Unanswered(ex))
            {
                logger.LogDebug(ex, "Couldn't inspect {Container}", key);
            }
            details = (await docker.InspectContainersAsync([containerId], ct)).FirstOrDefault();
        }
        if (details is not null) summary = summary with { StartedAt = details.StartedAt };
        // The one database detector, with what the image declares (its ports, its data folder) as well as its name.
        if (inspect is not null && summary.Role is not (ContainerRole.Self or ContainerRole.Tunnel or ContainerRole.Helper)
            && ContainerService.IsDatabase(ContainerPorts.Str(inspect["Config"], "Image"), inspect["Config"], row?.ImageRef))
            summary = summary with { Database = true };
        var latest = await journal.LatestAsync(key, ct);
        var git = row is { Deployed: true } ? await apps.GetDetailAsync(row.Id, ct) : null;
        var appAddresses = row is null ? null : await book.ForAppAsync(row, lanHost, ct);
        return new AppView(summary, row, details, inspect, latest, git, entry.Addresses, appAddresses);
    }

    public async Task<string?> KeyForIdAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.Container ?? "gb-" + a.Slug).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The container's row, made (Source Existing, Name = its title or name, ImageRef = Config.Image when it's a tag, ports
    /// mirror, snapshot) when it has none. Idempotent; never a Docker write; throws when there's no such container.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no such container, or it's a helper or a temporary one, which is never an app.</exception>
    public async Task<AppDefinition> AdoptAsync(string container, CancellationToken ct = default)
    {
        var gate = _adopting.GetOrAdd(container, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await RowOfAsync(container, ct) is { } known) return known;
            var inspect = await network.InspectAsync(container, ct) ?? throw new InvalidOperationException($"There's no container called {container}.");
            var name = ContainerPorts.Str(inspect, "Name").TrimStart('/');
            if (name != container) throw new InvalidOperationException($"{container} isn't a container's name: its name is {name}.");
            if (await WhyNotAnAppAsync(name, RecreateRules.Labels(inspect), ct) is { } why) throw new InvalidOperationException(why);

            var image = ContainerPorts.Str(inspect["Config"], "Image");
            var row = new AppDefinition
            {
                Name = titles.For(name) ?? name,
                Source = AppSource.Existing,
                Container = name,
                ContainerId = ContainerPorts.Str(inspect, "Id"),
                ImageRef = IsTag(image) ? image : null,
                UpdatePolicy = UpdatePolicy.Notify,
                AutoDeploy = false,
                DesiredState = ContainerPorts.Running(inspect) ? DesiredState.Running : DesiredState.Stopped,
                SpecProtected = await ProtectedSnapshotAsync(inspect, ct),
            };
            row.Ports.AddRange(Mirror(inspect, previous: null));
            // Another container whose name gives the same slug may be adopted at the same moment: the slug is chosen again.
            for (var attempt = 1; ; attempt++)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                row.Slug = await FreeSlugAsync(db, AppService.Slugify(name) is { Length: > 0 } slug ? slug : "app", ct);
                db.Apps.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException) when (attempt < 3)
                {
                    row.Id = 0;
                    foreach (var p in row.Ports) (p.Id, p.AppId) = (0, 0);
                }
            }
            await activity.AddAsync(name, row.Id, ActivityKind.Adopted, "Gitberth keeps its own data about it from now on.", ct: ct);
            Changed?.Invoke();
            return await RowOfAsync(name, ct) ?? row;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A new app's row, for AppCreator: container gb-&lt;slug&gt;, AutoDeploy off, ports mirror and AppNetworks from the snapshot.</summary>
    public async Task<AppDefinition> AddAsync(NewAppRow row, CancellationToken ct = default)
    {
        if (row.Source is AppSource.Git or AppSource.Existing)
            throw new ArgumentException($"A new {row.Source} app isn't added here: Git apps come from AppService.CreateAsync, and Existing ones are adopted.", nameof(row));
        var app = new AppDefinition
        {
            Name = row.Name,
            Slug = row.Slug,
            // gb-<slug> unless the creator asked for another name (the real-engine test's gbtest-* containers).
            Container = row.Container is { Length: > 0 } container && container != AppDefinition.ContainerNameFor(row.Slug) ? container : null,
            Source = row.Source,
            ImageRef = row.ImageRef,
            RegistryId = row.RegistryId,
            AutoDeploy = false,
            DesiredState = DesiredState.Running,
            UpdatePolicy = row.Options.Updates,
            HealthCheckPath = string.IsNullOrWhiteSpace(row.Options.HealthCheckPath) ? null : row.Options.HealthCheckPath.Trim(),
            Pinned = row.Options.Pinned,
            SecretKeys = row.Options.SecretKeys.Count > 0 ? JsonSerializer.Serialize(row.Options.SecretKeys) : null,
            SpecProtected = Protect(row.Snapshot),
        };
        var webMarked = false;
        foreach (var rule in row.Snapshot.Settings.Ports)
        {
            var web = !webMarked && rule.ContainerPort == row.Options.WebPort && rule.Protocol == "tcp";
            webMarked |= web;
            app.Ports.Add(MappingOf(rule, web));
        }
        foreach (var join in row.Snapshot.Networks)
        {
            var alias = join.Aliases.FirstOrDefault(a => a != row.Slug);
            // The shared network is every app's: a row for it only adds a name there.
            if (join.Name == network.Shared && alias is null) continue;
            app.Networks.Add(new AppNetwork { Network = join.Name, Alias = alias, IPv4Address = join.IPv4Address });
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Apps.Add(app);
            await db.SaveChangesAsync(ct);
        }
        // Also clears choices left over from an older container with the same name.
        await health.SetGatheredAsync(app.ContainerName, row.Options.GatherStats, ct);
        await restarts.SetAsync(app.ContainerName, row.Options.ScheduledRestart, ct);
        Changed?.Invoke();
        return app;
    }

    /// <summary>A non-Git row brought in line with its live container: ports mirror (IsWeb kept by container port), ContainerId, snapshot.</summary>
    public async Task SyncAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Apps.Include(a => a.Ports).FirstOrDefaultAsync(a => a.Id == appId, ct);
        // A Git app's row is the truth for its settings, and its deploys make its container.
        if (row is null || row.Deployed) return;
        if (await network.InspectAsync(row.ContainerName, ct) is not { } inspect) return;
        var previous = row.Ports.ToList();
        row.Ports.Clear();
        row.Ports.AddRange(Mirror(inspect, previous));
        row.ContainerId = ContainerPorts.Str(inspect, "Id");
        if (await ProtectedSnapshotAsync(inspect, ct) is { } snapshot) row.SpecProtected = snapshot;
        // The tag it follows is the one it runs: an image changed in Settings (or outside Gitberth) is followed from then on,
        // so an update never takes it back to the tag it was made from. A digest or an ID (a pin, a rollback) changes nothing.
        if (ContainerPorts.Str(inspect["Config"], "Image") is var image && IsTag(image) && image != row.ImageRef) row.ImageRef = image;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>The settings saved to create the app's container again; null when there are none, or they can't be read (a lost key ring).</summary>
    public async Task<AppSnapshot?> SnapshotAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.SpecProtected).FirstOrDefaultAsync(ct);
        return Unprotect(stored);
    }

    public async Task SaveSnapshotAsync(int appId, AppSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var protectedSnapshot = Protect(snapshot);
        await db.Apps.Where(a => a.Id == appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.SpecProtected, protectedSnapshot).SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);
        Changed?.Invoke();
    }

    /// <summary>Gitberth's own data about the app: its row's, or, before it has one, what's kept by its container's name.</summary>
    public async Task<AppOptions> OptionsAsync(string key, CancellationToken ct = default)
    {
        if (await RowOfAsync(key, ct) is { } row)
            return GitAppSettings.ToOptions(row, health, restarts);
        return new AppOptions
        {
            Name = titles.For(key) ?? key,
            GatherStats = health.IsGathered(key),
            ScheduledRestart = restarts.For(key),
            Updates = UpdatePolicy.Notify,
        };
    }

    /// <summary>Gitberth's own data; adopts first when something needs a row (2.2). An Existing app's name is its title too.</summary>
    /// <exception cref="ArgumentException">The name can't be used.</exception>
    public async Task<AppOptions> SaveOptionsAsync(string key, AppOptions options, CancellationToken ct = default)
    {
        var current = await OptionsAsync(key, ct);
        var row = await RowOfAsync(key, ct);
        var name = options.Name.Trim();
        if (name != current.Name && (AppService.NameProblem(name) ?? ContainerTitles.Problem(name)) is { } problem) throw new ArgumentException(problem, nameof(options));
        // A title, stats and a scheduled restart are kept by the container's name; the rest is the row's.
        var rowData = options.HealthCheckPath != current.HealthCheckPath || options.Updates != current.Updates || options.TrackedImage != current.TrackedImage
            || options.RegistryId != current.RegistryId || !options.SecretKeys.SequenceEqual(current.SecretKeys) || options.Pinned != current.Pinned
            || options.WebPort != current.WebPort;
        if (row is null && rowData) row = await AdoptAsync(key, ct);

        if (name != current.Name && (row is null || row.Source == AppSource.Existing)) await titles.SetAsync(key, name, ct);
        if (options.GatherStats != current.GatherStats) await health.SetGatheredAsync(key, options.GatherStats, ct);
        if (options.ScheduledRestart != current.ScheduledRestart) await restarts.SetAsync(key, options.ScheduledRestart, ct);
        if (row is not null && (rowData || name != current.Name))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var app = await db.Apps.Include(a => a.Ports).FirstAsync(a => a.Id == row.Id, ct);
            if (name != current.Name) app.Name = name;
            if (options.HealthCheckPath != current.HealthCheckPath) app.HealthCheckPath = string.IsNullOrWhiteSpace(options.HealthCheckPath) ? null : options.HealthCheckPath.Trim();
            if (options.Pinned != current.Pinned) app.Pinned = options.Pinned;
            // A Git app's updates are its deploys.
            if (!app.Deployed)
            {
                if (options.Updates != current.Updates) app.UpdatePolicy = options.Updates;
                if (options.TrackedImage != current.TrackedImage) app.ImageRef = string.IsNullOrWhiteSpace(options.TrackedImage) ? null : options.TrackedImage.Trim();
                if (options.RegistryId != current.RegistryId) app.RegistryId = options.RegistryId;
                if (!options.SecretKeys.SequenceEqual(current.SecretKeys))
                    app.SecretKeys = options.SecretKeys.Count > 0 ? JsonSerializer.Serialize(options.SecretKeys) : null;
            }
            else if (!options.SecretKeys.SequenceEqual(current.SecretKeys))
            {
                // A Git app keeps the mark on its variables: its deploys pass secret ones through their own environment,
                // never on docker's command line or in the deploy log. One that looks secret stays secret.
                await db.Entry(app).Collection(a => a.EnvVars).LoadAsync(ct);
                var secret = options.SecretKeys.ToHashSet(StringComparer.Ordinal);
                foreach (var v in app.EnvVars)
                    v.IsSecret = Masking.IsSecret(v.Key, protector.TryUnprotect(v.ProtectedValue), secret);
            }
            if (options.WebPort != current.WebPort)
            {
                var marked = false;
                foreach (var p in app.Ports.OrderBy(p => p.Id))
                {
                    p.IsWeb = !marked && p.ContainerPort == options.WebPort && p.Protocol == "tcp";
                    marked |= p.IsWeb;
                }
            }
            app.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        Changed?.Invoke();
        return await OptionsAsync(key, ct);
    }

    /// <summary>
    /// A new app whose create failed: its row goes (with its ports, so its host ports are free again for a retry under the
    /// same name), and the choices kept by its container's name with it. Docker is untouched.
    /// </summary>
    public async Task DiscardAsync(int appId, CancellationToken ct = default)
    {
        string? container;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            container = await db.Apps.AsNoTracking().Where(a => a.Id == appId && a.Source != AppSource.Git).Select(a => a.Container ?? "gb-" + a.Slug).FirstOrDefaultAsync(ct);
        if (container is null) return;
        await domains.DeleteAppAsync(appId, ct);
        await restarts.SetAsync(container, null, ct);
        await health.SetGatheredAsync(container, true, ct);
        Changed?.Invoke();
    }

    /// <summary>Removes the row and its domains (DomainService.DeleteAppAsync); the container is untouched.</summary>
    /// <exception cref="InvalidOperationException">It's a Git app, which is deleted with its container instead.</exception>
    public async Task<SyncReport> ForgetAsync(int appId, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
            if (row is null) return SyncReport.Nothing;
            if (row.Deployed) throw new InvalidOperationException($"{row.Name} is a Git app: delete it instead, which removes its container too.");
        }
        var report = await domains.DeleteAppAsync(appId, ct);
        Changed?.Invoke();
        return report;
    }

    // ---- Composing the list ----

    /// <summary>One app as the list has it, and its container's addresses.</summary>
    private sealed record Entry(AppSummary Summary, ContainerAddresses? Addresses);

    // Everything the list shows, or only the app with this key.
    private async Task<(List<Entry> Entries, string? Error)> ComposeAsync(string lanHost, string? only, CancellationToken ct)
    {
        List<AppDefinition> rows;
        Dictionary<int, Deployment> deploying;
        Dictionary<string, ImageCheck> checks;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().ToListAsync(ct);
            checks = await db.ImageChecks.AsNoTracking().ToDictionaryAsync(c => c.Reference, StringComparer.Ordinal, ct);
            deploying = (await db.Deployments.AsNoTracking()
                    .Where(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building || d.Status == DeployStatus.Starting)
                    .ToListAsync(ct))
                .GroupBy(d => d.AppId).ToDictionary(g => g.Key, g => g.MaxBy(d => d.Id)!);
        }
        string? error = null;
        var snapshot = NetworkSnapshot.Empty;
        try
        {
            snapshot = await network.SnapshotAsync(ct);
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            error = ex.Message;
        }
        var unsettled = await journal.UnfinishedAsync(ct);

        var byName = rows.GroupBy(r => r.ContainerName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nav = rows.Where(r => r.Container is null).ToDictionary(r => r.Slug, r => new NavApp(r.Id, r.Name, r.Slug), StringComparer.Ordinal);
        var labelled = rows.Where(r => r.Container is null).ToDictionary(r => r.Slug, StringComparer.Ordinal);
        // The two containers of a change that isn't settled are one app, under the change's name.
        var changeOf = new Dictionary<string, ContainerChange>(StringComparer.Ordinal);
        foreach (var change in unsettled)
        {
            changeOf[change.ContainerName] = change;
            changeOf.TryAdd(change.OldName, change);
            changeOf.TryAdd(change.TempName, change);
        }

        var picked = new Dictionary<string, (NetworkContainer? Container, AppDefinition? Row, ContainerRole Role, ContainerChange? Change)>(StringComparer.Ordinal);
        foreach (var c in snapshot.Containers)
        {
            var role = ContainerService.RoleOf(Summary(c), nav, network.Self);
            // A deploy's temporary or previous container is its app's, which shows Deploying meanwhile.
            if (role is ContainerRole.Helper or ContainerRole.AppInternal || c.Labels.ContainsKey(ContainerSpecFactory.BridgeLabel)) continue;
            var change = changeOf.GetValueOrDefault(c.Name);
            var key = change?.ContainerName ?? c.Name;
            // The container that holds the change's name stands for it; the other one only while that name has none.
            if (picked.ContainsKey(key) && c.Name != key) continue;
            picked[key] = (c, byName.GetValueOrDefault(key), role, change);
        }
        foreach (var row in rows.Where(r => !picked.ContainsKey(r.ContainerName)))
            picked[row.ContainerName] = (null, row, row.Container is null ? ContainerRole.App : ContainerService.IsDatabaseImage(row.ImageRef ?? "") ? ContainerRole.Database : ContainerRole.Other,
                changeOf.GetValueOrDefault(row.ContainerName));
        if (only is not null) picked = picked.Where(p => p.Key == only).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // What decides each origin and update badge: the reference it follows (the tag it runs, else its row's), and the image it runs.
        var tracked = picked.ToDictionary(p => p.Key, p => TrackedOf(p.Value.Row, p.Value.Container is { } c ? snapshot.ConfigImageOf(c) : null), StringComparer.Ordinal);
        var badges = await BadgesAsync(picked.Select(p => new UpdateSubject(p.Key, tracked[p.Key], p.Value.Container?.ImageId, p.Value.Row is { Deployed: true },
            p.Value.Row?.UpdatePolicy ?? UpdatePolicy.Notify)).ToList(), ct);
        var readings = stats.Current.Containers.GroupBy(h => h.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var entries = new List<Entry>();
        foreach (var (key, (c, row, picks, change)) in picked)
        {
            var role = picks;
            // Known as a database by the tag its row follows too: a container rolled back to an image ID is still postgres.
            if (role == ContainerRole.Other && ContainerService.IsDatabaseImage(tracked[key] ?? row?.ImageRef ?? "")) role = ContainerRole.Database;
            var gb = row ?? (c is not null && c.Labels.TryGetValue("gitberth.app", out var slug) && labelled.GetValueOrDefault(slug) is { } owner && owner.ContainerName == c.Name ? owner : null);
            var addresses = c is null ? null : AddressBook.ForContainer(c, snapshot.ConfigOf(c), role, lanHost, network.Shared, gb, snapshot.Containers);
            var (state, text) = StateOf(c, row, row is null ? null : deploying.GetValueOrDefault(row.Id), change, error is not null);
            // A row's container renamed outside Gitberth: the container with its ID, or one made for its slug, under another name.
            var renamedTo = c is null && row is { Deployed: false } && change is null ? RenamedTo(row, snapshot.Containers) : null;
            if (renamedTo is not null) (state, text) = (AppState.Renamed, $"Renamed outside Gitberth: it's {renamedTo} now");
            // A registry's only once it's known to have the tag: every image has RepoDigests in the containerd store.
            var origin = row is { Deployed: true } ? ImageOrigin.Built
                : ImageReference.Parse(tracked[key]) is not { Digest: null } reference ? ImageOrigin.Pinned
                : ImageOrigins.Of(reference, checks.GetValueOrDefault(reference.Canonical));
            var reading = readings.GetValueOrDefault(key);
            // The reference it was created with: Docker's list says the image's ID instead once the tag names another image.
            var image = c is null ? null : snapshot.ConfigImageOf(c);
            // Rolled back: the container is made from the older image's digest or ID, but the app still follows its tag, which
            // names a newer image now. It's shown by that tag, as the tag having moved on, not by a 70-character digest.
            var rolledBack = image is not null && !IsTag(image) && row is { Deployed: false } && IsTag(tracked[key]);
            if (rolledBack) image = tracked[key];
            entries.Add(new Entry(new AppSummary(
                key, row?.Id, row?.Name ?? titles.For(key) ?? key, key, row?.Source ?? AppSource.Existing, role, origin,
                image ?? row?.ImageRef ?? row?.CurrentImage ?? "",
                row is { Deployed: true } ? $"{row.Owner}/{row.Repo} · {row.Branch}{(row.CurrentCommit is { Length: >= 7 } sha ? " · " + sha[..7] : "")}" : null,
                state, text, c?.Id, addresses?.Chips ?? [], reading?.MemoryBytes, reading is { Recent.Count: > 0 } r ? r.Recent[^1].Cpu : null,
                badges.GetValueOrDefault(key), row?.Pinned ?? false, role is ContainerRole.Self or ContainerRole.Tunnel, null)
            {
                RenamedTo = renamedTo,
                Database = role == ContainerRole.Database,
                TagMoved = c is not null && (rolledBack || image != c.Image && ImageReference.IsImageId(c.Image) && IsTag(image)),
            }, addresses));
        }
        return (entries.OrderBy(e => e.Summary.Pinned ? 0 : 1).ThenBy(e => e.Summary.Title, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Summary.Key, StringComparer.Ordinal).ToList(),
            error);
    }

    // The update badges, by key; none when they can't be had.
    private async Task<IReadOnlyDictionary<string, UpdateBadge>> BadgesAsync(IReadOnlyList<UpdateSubject> subjects, CancellationToken ct)
    {
        if (subjects.Count == 0) return new Dictionary<string, UpdateBadge>();
        try
        {
            return await updates.Value.BadgesAsync(subjects, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "No update badges this time");
            return new Dictionary<string, UpdateBadge>();
        }
    }

    /// <summary>
    /// An app's state and its words: a deploy or a change under way, a change that waits for the owner, the container's own
    /// state (as its pill says it), or, without a container, why not.
    /// </summary>
    internal static (AppState State, string Text) StateOf(NetworkContainer? c, AppDefinition? row, Deployment? deploy, ContainerChange? change, bool dockerDown)
    {
        if (deploy is not null)
            return (AppState.Deploying, deploy.Status switch
            {
                DeployStatus.Cloning => "Cloning…",
                DeployStatus.Building => "Building…",
                DeployStatus.Starting => "Starting…",
                _ => "Queued…",
            });
        if (change is { Outcome: ChangeOutcome.NeedsDecision }) return (AppState.NeedsDecision, "Waits for you");
        if (change is { Outcome: ChangeOutcome.Running } && change.Step != ChangeStep.WaitingForStart) return (AppState.Changing, "Changing…");
        if (dockerDown) return (AppState.Unknown, "Unknown");
        if (c is null)
        {
            if (row is null) return (AppState.Unknown, "Unknown");
            if (row.Deployed)
                return row.CurrentImage is null ? (AppState.NotCreated, "Not deployed")
                    : row.DesiredState == DesiredState.Stopped ? (AppState.Stopped, "Stopped")
                    : (AppState.Missing, "Container missing");
            // Created by Gitberth, but never had a container: its first start failed or hasn't happened.
            return row.Source != AppSource.Existing && row.ContainerId is null ? (AppState.NotCreated, "Not created") : (AppState.Missing, "Container missing");
        }
        var (_, label) = ContainerService.Pill(Summary(c));
        return (c.State switch
        {
            "restarting" => AppState.Restarting,
            "paused" => AppState.Paused,
            "created" => AppState.Created,
            "dead" => AppState.Dead,
            "exited" when ContainerService.ExitCode(c.Status) is { } code and not (0 or 137 or 143) => AppState.Crashed,
            "exited" => AppState.Stopped,
            _ when c.Status.Contains("(unhealthy)", StringComparison.Ordinal) => AppState.Unhealthy,
            _ when c.Status.Contains("(health: starting)", StringComparison.Ordinal) => AppState.Starting,
            _ when c.Status.Contains("(healthy)", StringComparison.Ordinal) => AppState.Healthy,
            _ => AppState.Running,
        }, label);
    }

    private static ContainerSummary Summary(NetworkContainer c) => new(c.Name, c.State, c.Status, c.Image, "", c.Labels, c.Command, c.Id);

    /// <summary>
    /// The tag an app follows for updates: the one its container runs when it runs a tag (an image changed in Settings or
    /// outside Gitberth is followed from then on, never taken back), else its row's (a container pinned to a digest or an ID,
    /// a rollback's, still follows its tag). Null when neither is a tag.
    /// </summary>
    internal static string? TrackedOf(AppDefinition? row, string? configImage) =>
        IsTag(configImage) ? configImage : row?.ImageRef;

    // The name a row's container goes by now when it was renamed outside Gitberth: its ID under another name, or (for an app
    // Gitberth made) a container labelled with its slug under another name.
    private static string? RenamedTo(AppDefinition row, IReadOnlyList<NetworkContainer> containers) =>
        (row.ContainerId is { Length: > 0 } id ? containers.FirstOrDefault(c => c.Id == id && c.Name != row.ContainerName) : null)?.Name
        ?? (row.Container is null
            ? containers.FirstOrDefault(c => c.Name != row.ContainerName && c.Labels.TryGetValue("gitberth.app", out var slug) && slug == row.Slug
                && RecreateRules.MadeFromImage(c.Labels))?.Name
            : null);

    // ---- Rows ----

    /// <summary>The row whose container is <paramref name="container"/>: an Existing app's by its name, any other's as gb-&lt;slug&gt;.</summary>
    private async Task<AppDefinition?> RowOfAsync(string container, CancellationToken ct)
    {
        var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container["gb-".Length..] : null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug), ct);
    }

    // Why a container never gets a row of its own: a helper, or a deploy's or a change's temporary one, which are their app's.
    private async Task<string?> WhyNotAnAppAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (HostScanHelper.IsHelper(labels)) return $"{name} is a Gitberth helper: it only runs for a moment.";
        if (labels.ContainsKey(ContainerSpecFactory.BridgeLabel)) return $"{name} is a deploy's temporary container, which Gitberth removes itself.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // A Git app's container under another name is its deploy's; an app made from an image, renamed, is an app of its own.
        if (labels.TryGetValue("gitberth.app", out var slug) && name != AppDefinition.ContainerNameFor(slug) && !RecreateRules.MadeFromImage(labels)
            && await db.Apps.AsNoTracking().AnyAsync(a => a.Slug == slug && a.Container == null, ct))
            return $"{name} is a deploy's temporary or previous container, which Gitberth removes itself.";
        if (await db.ContainerChanges.AsNoTracking().AnyAsync(c => (c.OldName == name || c.TempName == name)
                && (c.Outcome == ChangeOutcome.Running || c.Outcome == ChangeOutcome.NeedsDecision), ct))
            return $"{name} is one of the two containers of a change that isn't settled.";
        return null;
    }

    // The slug, or with -2, -3… while another row has it.
    private static async Task<string> FreeSlugAsync(GitberthDb db, string slug, CancellationToken ct)
    {
        var taken = (await db.Apps.AsNoTracking().Select(a => a.Slug).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = slug;
        for (var i = 2; taken.Contains(unique); i++) unique = $"{slug}-{i}";
        return unique;
    }

    /// <summary>
    /// The container's ports as an app's rows: each binding, an exposed port with none as Internal, and a port Docker
    /// picks at every start without a host port. The web marker stays on the container port that had it; a container
    /// seen for the first time gets it on the first TCP port that opens in a browser, published ones first.
    /// </summary>
    /// <param name="previous">The rows before; null when there were none to keep the marker from.</param>
    internal static List<PortMapping> Mirror(JsonObject inspect, IReadOnlyList<PortMapping>? previous)
    {
        var database = ContainerService.IsDatabaseImage(ContainerPorts.Str(inspect["Config"], "Image"));
        var rows = new List<PortMapping>();
        foreach (var port in ContainerPorts.Read(inspect))
        {
            if (port.Bindings.Count == 0) rows.Add(new PortMapping { ContainerPort = port.ContainerPort, Protocol = port.Protocol, Access = PortAccess.Internal });
            foreach (var b in port.Bindings)
                rows.Add(new PortMapping
                {
                    ContainerPort = port.ContainerPort,
                    Protocol = port.Protocol,
                    Access = b.Access,
                    HostPort = b.Random ? null : b.HostPort,
                    HostIp = b.Access == PortAccess.Custom ? b.HostIp : null,
                });
        }
        int? web = previous is { Count: > 0 }
            ? PortRules.WebOf(previous)?.ContainerPort
            : rows.Where(r => r.Protocol == "tcp" && AddressBook.IsWeb(r.ContainerPort, "tcp", database, appWebPort: false))
                .OrderBy(r => r.Access == PortAccess.Internal ? 1 : 0).ThenBy(r => r.ContainerPort).Select(r => (int?)r.ContainerPort).FirstOrDefault();
        if (rows.FirstOrDefault(r => r.ContainerPort == web && r.Protocol == "tcp") is { } marked) marked.IsWeb = true;
        return rows;
    }

    private static PortMapping MappingOf(PortRule rule, bool web) => new()
    {
        ContainerPort = rule.ContainerPort,
        Protocol = rule.Protocol,
        Access = rule.Access,
        HostPort = rule.Access == PortAccess.Internal ? null : rule.HostPort,
        HostIp = rule.Access == PortAccess.Custom ? rule.HostIp : null,
        IsWeb = web,
    };

    // ---- Snapshots ----

    // The settings to create the container again, encrypted; null when they can't be read (they're kept as they were then).
    private async Task<string?> ProtectedSnapshotAsync(JsonObject inspect, CancellationToken ct)
    {
        try
        {
            JsonObject? image = null;
            try
            {
                image = await network.InspectImageAsync(ContainerPorts.Str(inspect, "Image"), ct);
            }
            catch (Exception ex) when (Unanswered(ex))
            {
                logger.LogDebug(ex, "Couldn't inspect the image of {Container}", ContainerPorts.Str(inspect, "Name"));
            }
            return Protect(new AppSnapshot(ReadSettings(inspect, image), JoinsOf(inspect, options.Network)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't keep the settings of {Container}", ContainerPorts.Str(inspect, "Name").TrimStart('/'));
            return null;
        }
    }

    private string Protect(AppSnapshot snapshot) => protector.Protect(JsonSerializer.Serialize(snapshot, SnapshotJson));

    private AppSnapshot? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || protector.TryUnprotect(stored) is not { Length: > 0 } json) return null;
        try
        {
            return JsonSerializer.Deserialize<AppSnapshot>(json, SnapshotJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "A saved snapshot couldn't be read");
            return null;
        }
    }

    /// <summary>
    /// The networks a container is on, as a create body would join it again: the shared one first, each with the names it
    /// answers to there besides its own name and short ID, its pinned address, and whether it's below the gateway.
    /// </summary>
    internal static List<NetworkJoin> JoinsOf(JsonObject inspect, string shared)
    {
        var name = ContainerPorts.Str(inspect, "Name").TrimStart('/');
        var id = ContainerPorts.Str(inspect, "Id");
        var joins = new List<NetworkJoin>();
        if (inspect["NetworkSettings"]?["Networks"] is not JsonObject endpoints) return joins;
        foreach (var (network, endpoint) in endpoints)
        {
            var aliases = (endpoint?["Aliases"] as JsonArray ?? [])
                .Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()
                .Where(a => a.Length > 0 && a != name && !(a.Length == 12 && id.StartsWith(a, StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToList();
            var ipv4 = ContainerPorts.Str(endpoint?["IPAMConfig"], "IPv4Address") is { Length: > 0 } ip ? ip : null;
            joins.Add(new NetworkJoin(network, aliases, ipv4, LowPriority: ContainerPorts.Number(endpoint, "GwPriority") < 0));
        }
        return joins.OrderBy(j => j.Name == shared ? 0 : 1).ThenBy(j => j.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a reference names a tag (postgres:18, nginx) rather than a digest or an image ID.</summary>
    internal static bool IsTag(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && !reference.Contains('@') && !reference.StartsWith("sha256:", StringComparison.Ordinal)
        && !(reference.Length == 64 && reference.All(char.IsAsciiHexDigitLower));

    private static bool Unanswered(Exception ex) => ex is DockerApiException or HttpRequestException or TimeoutException or JsonException;
}

/// <param name="Container">The container's name when it isn't gb-&lt;slug&gt;; null for gb-&lt;slug&gt;.</param>
public sealed record NewAppRow(string Name, string Slug, AppSource Source, string? ImageRef, int? RegistryId, AppSnapshot Snapshot, AppOptions Options,
    string? Container = null);
