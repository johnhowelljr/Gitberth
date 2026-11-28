using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>One picked app or container, as the per-item restore runs it.</summary>
internal sealed class PickPlan
{
    public required RestorePick Pick { get; init; }
    public required BackupItem Item { get; init; }

    /// <summary>An app's saved rows (null for a container that isn't an app).</summary>
    public SavedAppRows? Saved { get; init; }

    /// <summary>Replace of an app: the app here, which keeps its ID.</summary>
    public AppDefinition? Current { get; init; }

    /// <summary>Its saved containers: the app's own, a Compose app's services, its Redis; or the one container.</summary>
    public required List<SavedContainer> Containers { get; init; }

    public required List<SavedVolume> Volumes { get; init; }

    /// <summary>What it's called here: the app's name and slug (a copy's new ones), or the container's name.</summary>
    public required string Name { get; init; }

    public required string Slug { get; init; }

    /// <summary>Saved names (containers, volumes, Compose project) to the names they get here; the same unless it's a copy.</summary>
    public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);

    /// <summary>The containers here it replaces (stopped first, then removed or redeployed).</summary>
    public List<string> Replaced { get; init; } = [];

    /// <summary>The app's databases as they're made here (filled in by the database step).</summary>
    public List<ManagedDatabase> Databases { get; } = [];

    /// <summary>The app's ID here, once its rows are written.</summary>
    public int? AppId { get; set; }

    /// <summary>The host folders its containers mount that the backup holds.</summary>
    public List<SavedHostFolder> Folders { get; } = [];

    /// <summary>A copy's own folders: each saved folder's path to the copy's, next to it (/srv/photos-website2).</summary>
    public Dictionary<string, string> FolderRenames { get; } = new(StringComparer.Ordinal);

    public string Rename(string saved) => Renames.TryGetValue(saved, out var here) ? here : saved;

    /// <summary>A bind's source here: a copy's own folder for a saved one, else the same path.</summary>
    public string Folder(string source) => HostFolders.Clean(source) is { } path && FolderRenames.TryGetValue(path, out var copy) ? copy : source;

    public bool IsApp => Saved is not null;
}

public sealed partial class RestoreService
{
    // ---- what each top-level item of the backup is here (P4) ----

    /// <summary>This Gitberth as a restore finds it: its apps, containers, ports, domains and volumes.</summary>
    private sealed class HereNow
    {
        public required List<AppDefinition> Apps { get; init; }
        public required Dictionary<string, JsonObject> Containers { get; init; }
        public required Dictionary<int, string> Ports { get; init; }
        public required Dictionary<string, int> Domains { get; init; }
        public required HashSet<string> Volumes { get; init; }
        public required HashSet<string> Redis { get; init; }

        /// <summary>The previews' slugs: names no copy may take either.</summary>
        public required HashSet<string> Previews { get; init; }

        /// <summary>Each volume here, with the containers here that mount it.</summary>
        public required Dictionary<string, List<string>> VolumeUsers { get; init; }

        /// <summary>The Postgres databases Gitberth keeps here, each with its app.</summary>
        public required List<ManagedDatabase> Databases { get; init; }

        public AppDefinition? AppLike(AppDefinition saved) =>
            Apps.FirstOrDefault(a => a.Slug == saved.Slug) ?? Apps.FirstOrDefault(a => string.Equals(a.Name, saved.Name, StringComparison.OrdinalIgnoreCase));

        // A name a new app could have here: no app or preview has it or its slug, and no container its container name.
        public bool AppNameTaken(string name)
        {
            var slug = AppService.Slugify(name);
            return slug.Length == 0 || Apps.Any(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) || a.Slug == slug
                       || a.Container == AppDefinition.ContainerNameFor(slug))
                   || Previews.Contains(slug) || Containers.ContainsKey(AppDefinition.ContainerNameFor(slug)) || Redis.Contains(slug) || slug == "gitberth";
        }
    }

    private async Task<HereNow> HereAsync(CancellationToken ct)
    {
        List<AppDefinition> apps;
        List<CustomDomain> domains;
        List<string> redis;
        List<string> previews;
        List<ManagedDatabase> databases;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            apps = await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null).Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().ToListAsync(ct);
            previews = await db.Apps.AsNoTracking().Where(a => a.ParentAppId != null).Select(a => a.Slug).ToListAsync(ct);
            domains = await db.Domains.AsNoTracking().ToListAsync(ct);
            redis = await db.ManagedDatabases.AsNoTracking().Where(d => d.Kind == DatabaseKind.Redis).Select(d => d.Name).ToListAsync(ct);
            databases = await db.ManagedDatabases.AsNoTracking().Where(d => d.Kind == DatabaseKind.Postgres).ToListAsync(ct);
        }
        var containers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var c in await _engine.ListAllAsync(ct))
            if (await _engine.InspectContainerAsync(c.Id, ct) is { } inspect) containers[BackupInventory.Name(inspect)] = inspect;
        var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, inspect) in containers)
            foreach (var m in (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => BackupInventory.Str(m, "Type") == "volume"))
            {
                if (!users.TryGetValue(BackupInventory.Str(m, "Name"), out var list)) users[BackupInventory.Str(m, "Name")] = list = [];
                list.Add(name);
            }
        var ports = new Dictionary<int, string>();
        foreach (var (name, inspect) in containers)
            foreach (var port in HostPorts(inspect))
                ports.TryAdd(port, name);
        foreach (var app in apps)
            foreach (var port in app.Ports.Where(p => p.HostPort is not null))
                ports.TryAdd(port.HostPort!.Value, app.ContainerName);
        var volumes = (await _engine.ListVolumesAsync(ct)).OfType<JsonObject>().Select(v => BackupInventory.Str(v, "Name")).ToHashSet(StringComparer.Ordinal);
        return new HereNow
        {
            Apps = apps, Containers = containers, Ports = ports, Volumes = volumes,
            Domains = domains.GroupBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AppId, StringComparer.OrdinalIgnoreCase),
            Redis = redis.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Previews = previews.ToHashSet(StringComparer.OrdinalIgnoreCase),
            VolumeUsers = users,
            Databases = databases,
        };
    }

    /// <summary>A copy's name: the name and 2, else 3…, whichever is free.</summary>
    internal static string CopyName(string name, Func<string, bool> taken)
    {
        for (var n = 2; ; n++)
        {
            var candidate = name.Trim() + n.ToString(CultureInfo.InvariantCulture);
            if (!taken(candidate)) return candidate;
        }
    }

    // The top-level items a restore can pick: apps and containers.
    private static IEnumerable<BackupItem> Pickable(BackupManifest manifest) =>
        manifest.Items.Where(i => i.Parent is null && i.Kind is BackupItemKind.App or BackupItemKind.Container);

    /// <summary>The containers of an item (the app's own, its services and Redis; or the container), and their volumes.</summary>
    internal static (List<SavedContainer> Containers, List<SavedVolume> Volumes) PartsOf(BackupManifest manifest, string itemId)
    {
        var children = manifest.Items.Where(i => i.Parent == itemId).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var containers = manifest.Containers.Where(c => c.Item == itemId || children.Contains(c.Item)).ToList();
        var names = containers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var volumes = manifest.Volumes.Where(v => v.Users.Any(names.Contains)).ToList();
        return (containers, volumes);
    }

    private static int? SavedAppId(string itemId) =>
        itemId.StartsWith("app:", StringComparison.Ordinal) && int.TryParse(itemId["app:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>How each top-level app and container of the backup stands here: here or not, a copy's name, and the first conflict.</summary>
    internal async Task<List<RestoreTarget>> TargetsAsync(BackupManifest manifest, IReadOnlyDictionary<int, SavedAppRows> saved, CancellationToken ct)
    {
        var here = await HereAsync(ct);
        var targets = new List<RestoreTarget>();
        foreach (var item in Pickable(manifest))
        {
            var (containers, volumes) = PartsOf(manifest, item.Id);
            if (SavedAppId(item.Id) is { } unread && !saved.ContainsKey(unread))
            {
                targets.Add(new RestoreTarget(item.Id, false, CopyName(item.Title, here.AppNameTaken), "Its settings couldn't be read from this backup, so it can't be restored."));
                continue;
            }
            if (SavedAppId(item.Id) is { } id && saved.TryGetValue(id, out var rows))
            {
                var app = rows.App;
                var match = here.AppLike(app);
                var orphan = match is null && here.Containers.ContainsKey(app.ContainerName);
                var copyName = CopyName(app.Name, here.AppNameTaken);
                // Not here, but it can't come back as it was: offered as here, so a copy can be picked.
                var blocked = match is null && !orphan ? OnlyAsCopy(manifest, containers, volumes, rows, here) : null;
                string? note = orphan ? $"A container called {app.ContainerName} is here, and no app has it: restore it as a copy, or remove that container first." : blocked;
                note ??= app.Ports.Where(p => p.HostPort is { } port && here.Ports.TryGetValue(port, out var holder) && holder != match?.ContainerName)
                    .Select(p => $"Port {p.HostPort!.Value.ToString(CultureInfo.InvariantCulture)} is taken here: it gets a free one.").FirstOrDefault();
                note ??= app.Domains.Where(d => here.Domains.TryGetValue(d.Hostname, out var owner) && owner != match?.Id)
                    .Select(d => $"{d.Hostname} is another app's here: it's left off.").FirstOrDefault();
                note ??= match is null ? volumes.Where(v => here.Volumes.Contains(v.Name)).Select(v => $"The volume {v.Name} is here already: it's kept as it is.").FirstOrDefault() : null;
                targets.Add(new RestoreTarget(item.Id, match is not null || orphan || blocked is not null, copyName, note));
            }
            else
            {
                var name = item.Title;
                var isHere = here.Containers.ContainsKey(name);
                var copyName = CopyName(name, c => here.Containers.ContainsKey(c) || here.AppNameTaken(c));
                var blocked = !isHere ? OnlyAsCopy(manifest, containers, volumes, null, here) : null;
                var note = blocked ?? containers.SelectMany(c => HostPorts(c.Inspect)).Where(p => here.Ports.TryGetValue(p, out var holder) && holder != name)
                    .Select(p => $"Port {p.ToString(CultureInfo.InvariantCulture)} is taken here: it gets a free one.").FirstOrDefault();
                note ??= !isHere ? volumes.Where(v => here.Volumes.Contains(v.Name)).Select(v => $"The volume {v.Name} is here already: it's kept as it is.").FirstOrDefault() : null;
                targets.Add(new RestoreTarget(item.Id, isHere || blocked is not null, copyName, note));
            }
        }
        return targets;
    }

    // Why an item that isn't here can't come back as it was, only as a copy (the picks refuse it so too); null when it can.
    private static string? OnlyAsCopy(BackupManifest manifest, List<SavedContainer> containers, List<SavedVolume> volumes, SavedAppRows? rows, HereNow here)
    {
        if (manifest.Databases.Where(d => containers.Any(c => c.Name == d.Container)).SelectMany(d => d.DataVolumes).FirstOrDefault(here.Volumes.Contains) is { } data)
            return $"Its data volume {data} is here: it can only come back as a copy.";
        foreach (var v in volumes.Where(v => here.Volumes.Contains(v.Name)))
            if (here.VolumeUsers.TryGetValue(v.Name, out var users) && users.Count > 0)
                return $"The volume {v.Name} is here and {users[0]} uses it: it can only come back as a copy.";
        foreach (var d in rows?.Databases.Where(d => d.Kind == DatabaseKind.Postgres) ?? [])
            if (here.Databases.FirstOrDefault(r => Overlaps(r, d)) is { } other)
                return $"Its database {d.Name} on {d.Server} (or its role) is {here.Apps.FirstOrDefault(a => a.Id == other.AppId)?.Name ?? "another app"}'s here: it can only come back as a copy.";
        return null;
    }

    // A database here on the same server with the saved one's name or role, either way round.
    private static bool Overlaps(ManagedDatabase here, ManagedDatabase saved)
    {
        var role = saved.Role ?? saved.Name;
        var theirs = here.Role ?? here.Name;
        return here.Server == saved.Server && (here.Name == saved.Name || here.Name == role || theirs == role || theirs == saved.Name);
    }

    // ---- the picks checked, each with what it becomes here ----

    /// <summary>The picks as they'll run; one that doesn't fit throws, saying why (nothing has run then).</summary>
    internal async Task<List<PickPlan>> PlanAsync(BackupManifest manifest, IReadOnlyDictionary<int, SavedAppRows> saved, IReadOnlyList<RestorePick> picks,
        CancellationToken ct)
    {
        if (picks.Count == 0) throw new InvalidOperationException("Pick at least one app or container to restore.");
        var here = await HereAsync(ct);
        var items = Pickable(manifest).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var plans = new List<PickPlan>();
        var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pick in picks)
        {
            if (!items.TryGetValue(pick.ItemId, out var item)) throw new InvalidOperationException($"{pick.ItemId} isn't an app or container of this backup.");
            if (plans.Any(p => p.Pick.ItemId == pick.ItemId)) throw new InvalidOperationException($"{item.Title} is picked twice.");
            var (containers, volumes) = PartsOf(manifest, item.Id);
            PickPlan plan;
            if (SavedAppId(item.Id) is { } id)
            {
                if (!saved.TryGetValue(id, out var rows)) throw new InvalidOperationException($"{item.Title}'s settings aren't in this backup.");
                plan = AppPlan(pick, item, rows, containers, volumes, here, copies);
            }
            else plan = ContainerPlan(pick, item, containers, volumes, here, copies);
            // Its host folders the backup holds; a copy gets its own of each, next to the original (it shares none).
            var names = plan.Containers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            plan.Folders.AddRange(manifest.HostFolders.Where(f => f.Part is not null && f.Users.Any(names.Contains)));
            if (pick.Action == RestoreAction.Copy)
                foreach (var folder in plan.Folders) plan.FolderRenames[folder.Path] = HostFolders.CopyPath(folder.Path, plan.Slug);
            await CheckPickAsync(plan, manifest, here, plans, ct);
            plans.Add(plan);
        }
        return plans;
    }

    /// <summary>A copy's slug as New app chooses one (tests give their own; else Gitberth's apps).</summary>
    internal Func<string, CancellationToken, Task<NetworkName>>? ChooseSlug { get; init; }

    /// <summary>A copy's database URLs by its ID (tests give their own; else Gitberth's databases).</summary>
    internal Func<int, CancellationToken, Task<IReadOnlyList<ProvidedVariable>>>? CopyValues { get; init; }

    private Task<IReadOnlyList<ProvidedVariable>> ValuesOfAsync(int appId, CancellationToken ct) =>
        CopyValues?.Invoke(appId, ct) ?? _databases?.ValuesAsync(appId, ct) ?? Task.FromResult<IReadOnlyList<ProvidedVariable>>([]);

    // What a pick may not do here, checked before anything runs: load a database server's whole dump over data that's here,
    // write into a volume or folder that's here, share the host's folders with the original, take another app's database
    // or role, or need a database server (or its dump) that isn't there.
    private async Task CheckPickAsync(PickPlan plan, BackupManifest manifest, HereNow here, List<PickPlan> before, CancellationToken ct)
    {
        var title = plan.Item.Title;
        var action = plan.Pick.Action;

        // A database server's dump is the whole server's: it only ever goes into a server with no data yet.
        var servers = manifest.Databases.Where(d => plan.Containers.Any(c => c.Name == d.Container)).ToList();
        if (servers.Count > 0 && action == RestoreAction.Replace)
            throw new InvalidOperationException($"{title} is a database server: replacing it would load its whole dump over the databases it has now. Restore it as a copy instead.");
        if (action == RestoreAction.Restore && servers.SelectMany(d => d.DataVolumes).FirstOrDefault(here.Volumes.Contains) is { } data)
            throw new InvalidOperationException($"{title}'s data volume {data} is here: restoring it would load its whole dump over that data. Restore it as a copy instead.");

        if (action == RestoreAction.Copy)
        {
            // A copy writes only into volumes and folders of its own, and shares no host folder with the original.
            if (plan.Volumes.Select(v => plan.Rename(v.Name)).FirstOrDefault(here.Volumes.Contains) is { } volume)
                throw new InvalidOperationException($"A volume called {volume} is here already: give the copy of {title} another name, or remove that volume first.");
            if (plan.Saved?.App.IsCompose == true && Directory.Exists(Path.Combine(_options.DataDir, "compose", plan.Slug)))
                throw new InvalidOperationException($"Compose files of {plan.Slug} are here already: give the copy of {title} another name.");
            if (SharedFolder(plan) is { } folder)
                throw new InvalidOperationException($"{title} writes to the host's folder {folder}: a copy would share it with the original, so it can't be copied.");
            // As New app names an app: a preview's name, a removed domain's route, a name on the shared network are taken too.
            Func<string, CancellationToken, Task<NetworkName>>? choose = ChooseSlug ?? (_apps is { } apps ? apps.ChooseSlugAsync : null);
            if (plan.IsApp && choose is not null
                && await choose(plan.Name, ct) is var chosen && (chosen.Taken is not null || chosen.Slug != AppService.Slugify(plan.Name)))
                throw new InvalidOperationException($"{plan.Name} is taken here ({chosen.Taken ?? $"{chosen.Slug} would be its name"}): give the copy of {title} another name.");
        }

        // Restore keeps a volume that's here as it is: never one another container here uses. Replace empties its own: never
        // one a container that isn't its uses too.
        if (action == RestoreAction.Restore)
            foreach (var v in plan.Volumes.Where(v => here.Volumes.Contains(v.Name)))
                if (here.VolumeUsers.TryGetValue(v.Name, out var users) && users.Count > 0)
                    throw new InvalidOperationException($"The volume {v.Name} is here and {users[0]} uses it: restore {title} as a copy instead.");
        if (action == RestoreAction.Replace)
            foreach (var v in plan.Volumes.Select(v => plan.Rename(v.Name)).Where(here.Volumes.Contains))
                if (here.VolumeUsers.TryGetValue(v, out var users) && users.FirstOrDefault(u => !plan.Replaced.Contains(u)) is { } other)
                    throw new InvalidOperationException($"The volume {v} is here and {other} uses it too: replacing {title} would empty it under {other}. Restore it as a copy instead.");

        foreach (var d in plan.Saved?.Databases.Where(d => d.Kind == DatabaseKind.Postgres) ?? [])
        {
            // Its server runs here (or a pick before it brings it back), and the backup has the server's dump.
            var comes = before.Any(p => p.Pick.Action == RestoreAction.Restore && p.Containers.Any(c => c.Name == d.Server));
            if (!comes && (!here.Containers.TryGetValue(d.Server, out var live) || !BackupInventory.Running(live)))
                throw new InvalidOperationException($"{title}'s database is on {d.Server}, which isn't running here: restore or start {d.Server} first.");
            if (!manifest.Databases.Any(s => s.Container == d.Server) || !manifest.Containers.Any(c => c.Name == d.Server))
                throw new InvalidOperationException($"The backup has no dump of {d.Server}, so {title}'s database can't come back.");
            if (action == RestoreAction.Copy) continue;

            // Its database and role keep their names: never another app's.
            if (here.Databases.FirstOrDefault(r => r.AppId != plan.Current?.Id && Overlaps(r, d)) is { } other)
                throw new InvalidOperationException(
                    $"The database {d.Name} on {d.Server} (or its role) is {here.Apps.FirstOrDefault(a => a.Id == other.AppId)?.Name ?? "another app"}'s here: restore {title} as a copy instead.");
        }
    }

    // A folder of the host one of its containers writes to (a socket aside, and a Compose app's own folder, which a copy
    // gets its own of), or a Git app's. A folder the backup holds gets the copy's own (not for a Compose app, whose file
    // names its folders itself).
    private static string? SharedFolder(PickPlan plan)
    {
        var own = plan.Saved is { } saved ? $"/compose/{saved.App.Slug}/" : null;
        var moved = plan.Saved?.App.IsCompose == true ? new HashSet<string>(StringComparer.Ordinal) : plan.FolderRenames.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var c in plan.Containers)
            foreach (var m in (c.Inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => BackupInventory.Str(m, "Type") == "bind"))
            {
                var source = BackupInventory.Str(m, "Source");
                var writes = m["RW"] is not JsonValue rw || !rw.TryGetValue<bool>(out var w) || w;
                if (writes && source.Length > 0 && !source.EndsWith(".sock", StringComparison.Ordinal) && (own is null || !source.Contains(own, StringComparison.Ordinal))
                    && !moved.Contains(HostFolders.Clean(source) ?? source))
                    return source;
            }
        return plan.Saved?.App.Volumes.FirstOrDefault(v => VolumeNames.IsBind(v.Source) && !v.ReadOnly && !moved.Contains(HostFolders.Clean(v.Source) ?? v.Source))?.Source;
    }

    private static PickPlan AppPlan(RestorePick pick, BackupItem item, SavedAppRows rows, List<SavedContainer> containers, List<SavedVolume> volumes, HereNow here,
        HashSet<string> copies)
    {
        var app = rows.App;
        var match = here.AppLike(app);
        switch (pick.Action)
        {
            case RestoreAction.Restore:
                if (match is not null) throw new InvalidOperationException($"{app.Name} is here: replace it, or restore it as a copy.");
                if (here.Containers.ContainsKey(app.ContainerName))
                    throw new InvalidOperationException($"A container called {app.ContainerName} is here: restore {app.Name} as a copy, or remove that container first.");
                return new PickPlan { Pick = pick, Item = item, Saved = rows, Containers = containers, Volumes = volumes, Name = app.Name, Slug = app.Slug };
            case RestoreAction.Replace:
                if (match is null) throw new InvalidOperationException($"{app.Name} isn't here to replace: restore it as it was.");
                var replaced = here.Containers.Where(c => c.Key == match.ContainerName || BackupInventory.Labels(c.Value).TryGetValue("gitberth.app", out var slug) && slug == match.Slug)
                    .Select(c => c.Key).ToList();
                var replace = new PickPlan
                {
                    Pick = pick, Item = item, Saved = rows, Current = match, Containers = containers, Volumes = volumes, Name = match.Name, Slug = match.Slug,
                    Replaced = replaced,
                };
                Map(replace, app, match.Slug, match.ContainerName);
                return replace;
            default:
                var name = pick.NewName?.Trim() ?? "";
                if (AppService.NameProblem(name) is { } problem) throw new InvalidOperationException($"The copy of {app.Name}: {problem}");
                if (here.AppNameTaken(name) || !copies.Add(AppService.Slugify(name)))
                    throw new InvalidOperationException($"{name} is taken here: give the copy of {app.Name} another name.");
                var slugCopy = AppService.Slugify(name);
                var copy = new PickPlan { Pick = pick, Item = item, Saved = rows, Containers = containers, Volumes = volumes, Name = name, Slug = slugCopy };
                Map(copy, app, slugCopy, app.Container is null ? AppDefinition.ContainerNameFor(slugCopy) : CopyContainer(app, slugCopy));
                return copy;
        }
    }

    // A Compose app's web member keeps its service's name; an adopted container's copy is named after the copy.
    private static string CopyContainer(AppDefinition app, string slug) =>
        app.IsCompose && app.WebService is { } web ? $"gb-{slug}_{web}" : AppDefinition.ContainerNameFor(slug);

    // Saved names to here's: the app's container and its Redis, its volumes (gb-<slug>-…, gb-<slug>_…), its Compose project.
    private static void Map(PickPlan plan, AppDefinition saved, string slug, string container)
    {
        if (saved.Slug == slug && saved.ContainerName == container) return;
        plan.Renames[saved.ContainerName] = container;
        plan.Renames[DatabaseNames.RedisContainer(saved.ContainerName)] = DatabaseNames.RedisContainer(container);
        plan.Renames[saved.ComposeProject] = Compose.ComposeNames.Project(slug);
        plan.Renames[saved.Slug] = slug;
        foreach (var c in plan.Containers.Where(c => !plan.Renames.ContainsKey(c.Name)))
            plan.Renames[c.Name] = c.Name.StartsWith($"gb-{saved.Slug}_", StringComparison.Ordinal) ? $"gb-{slug}_{c.Name[$"gb-{saved.Slug}_".Length..]}" : $"{container}-{c.Name}";
        foreach (var v in plan.Volumes) plan.Renames[v.Name] = RenameVolume(v.Name, saved.Slug, slug, saved.ContainerName, container);
    }

    /// <summary>A saved volume's name for a copy: gb-&lt;slug&gt;-… and gb-&lt;slug&gt;_… take the copy's slug; any other gets it in front.</summary>
    internal static string RenameVolume(string volume, string fromSlug, string toSlug, string fromContainer, string toContainer)
    {
        if (volume.StartsWith(fromContainer + "_", StringComparison.Ordinal)) return toContainer + volume[fromContainer.Length..];
        foreach (var sep in new[] { "-", "_" })
            if (volume.StartsWith($"gb-{fromSlug}{sep}", StringComparison.Ordinal)) return $"gb-{toSlug}{sep}{volume[$"gb-{fromSlug}{sep}".Length..]}";
        return $"gb-{toSlug}-{(volume.Length == 64 ? volume[..12] : volume)}";
    }

    private static PickPlan ContainerPlan(RestorePick pick, BackupItem item, List<SavedContainer> containers, List<SavedVolume> volumes, HereNow here,
        HashSet<string> copies)
    {
        var name = item.Title;
        var isHere = here.Containers.ContainsKey(name);
        switch (pick.Action)
        {
            case RestoreAction.Restore:
                if (isHere) throw new InvalidOperationException($"A container called {name} is here: replace it, or restore it as a copy.");
                return new PickPlan { Pick = pick, Item = item, Containers = containers, Volumes = volumes, Name = name, Slug = name };
            case RestoreAction.Replace:
                if (!isHere) throw new InvalidOperationException($"{name} isn't here to replace: restore it as it was.");
                return new PickPlan { Pick = pick, Item = item, Containers = containers, Volumes = volumes, Name = name, Slug = name, Replaced = [name] };
            default:
                var copyName = pick.NewName?.Trim() ?? "";
                if (ContainerNameProblem(copyName) is { } problem) throw new InvalidOperationException($"The copy of {name}: {problem}");
                if (here.Containers.ContainsKey(copyName) || here.AppNameTaken(copyName) || !copies.Add(copyName))
                    throw new InvalidOperationException($"{copyName} is taken here: give the copy of {name} another name.");
                var plan = new PickPlan { Pick = pick, Item = item, Containers = containers, Volumes = volumes, Name = copyName, Slug = copyName };
                plan.Renames[name] = copyName;
                foreach (var v in volumes) plan.Renames[v.Name] = v.Name.Contains(name, StringComparison.Ordinal) ? v.Name.Replace(name, copyName, StringComparison.Ordinal) : $"{copyName}-{v.Name}";
                return plan;
        }
    }

    // A container's name as Docker takes it.
    private static string? ContainerNameProblem(string name) =>
        name.Length == 0 ? "Give the copy a name."
        : name.Length > 63 || !char.IsAsciiLetterOrDigit(name[0]) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            ? "Use letters, digits, '-', '_' and '.' (starting with a letter or digit)."
            : null;

    /// <summary>Each pick's steps, as Status lists them: "&lt;item&gt;:&lt;stage&gt;".</summary>
    internal static List<RestoreStepRecord> PickSteps(IReadOnlyList<PickPlan> plans, BackupManifest manifest)
    {
        var steps = new List<RestoreStepRecord>();
        foreach (var plan in plans)
        {
            void Add(string stage, string title) => steps.Add(new RestoreStepRecord { Id = $"{plan.Item.Id}:{stage}", Title = $"{plan.Name}: {title}", Phase = 1 });
            var app = plan.Saved?.App;
            var verb = plan.Pick.Action switch { RestoreAction.Replace => "replace", RestoreAction.Copy => $"copy of {plan.Item.Title}", _ => "restore" };
            if (plan.Replaced.Count > 0) Add("stop", "stop it");
            if (plan.Containers.Any(c => c.How != ComesBack.Deploy) || plan.Saved?.Databases.Any(d => d.Kind == DatabaseKind.Postgres) == true) Add("images", "images");
            // Its database before its volumes: restored aside first, it's the step most likely to fail, and nothing here has
            // changed until it worked.
            if (plan.Saved?.Databases.Any(d => d.Kind == DatabaseKind.Postgres) == true) Add("database", "database");
            if (plan.Volumes.Count > 0 || plan.Folders.Count > 0 || app?.IsCompose == true) Add("volumes", "volumes and files");
            if (plan.IsApp) Add("app", $"settings ({verb})");
            if (plan.Containers.Any(c => c.How != ComesBack.Deploy)) Add("container", "container");
            if (plan.Containers.Any(c => manifest.Databases.Any(d => d.Container == c.Name))) Add("dump", "its dump loaded");
            if (app?.Deployed == true) Add("deploy", app.CurrentCommit is { Length: >= 7 } sha && !app.IsCompose ? $"deploy at {sha[..7]}" : "deploy");
            if (app?.Domains.Count > 0 && plan.Pick.Action != RestoreAction.Copy) Add("domains", "domains");
        }
        return steps;
    }

    // ---- running the picks ----

    /// <summary>
    /// Starts the per-item restore (see <see cref="IRestoreService.StartAsync(string, IReadOnlyList{RestorePick}, CancellationToken)"/>):
    /// the picks are checked first, then run one after the other, each in its own steps; one failing doesn't stop the others.
    /// </summary>
    public async Task StartAsync(string passphrase, IReadOnlyList<RestorePick> picks, CancellationToken ct = default)
    {
        if (CantRestore() is { } why) throw new InvalidOperationException(why);
        if (!File.Exists(_paths.Upload)) throw new InvalidOperationException("No file was uploaded.");
        var key = await KeyAsync(passphrase, ct);
        try
        {
            var manifest = await ReadManifestAsync(key, ct);
            if (await TooNewAsync(manifest, ct) is { } newer) throw new InvalidOperationException(newer);
            var saved = await SavedAppsAsync(manifest, key, ct);
            var plans = await PlanAsync(manifest, saved, picks, ct);
            lock (_planGate)
            {
                if (Running) throw new InvalidOperationException("A restore is running.");
                Log.Clear();
                _plan = new RestorePlanState { StartedAt = DateTime.UtcNow, Host = manifest.Host, SavedAt = manifest.SavedAt, Steps = PickSteps(plans, manifest) };
                Save();
                var running = key;
                key = null;
                _phaseOne = Task.Run(() => PicksAsync(running, manifest, plans));
            }
        }
        catch (BackupFileException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            key?.Dispose();
        }
    }

    // The file's key, made once from the passphrase (PBKDF2), for every pass over the file.
    private async Task<BackupKey> KeyAsync(string passphrase, CancellationToken ct)
    {
        await using var file = OpenUpload();
        var (header, _) = await BackupReader.ReadHeaderAsync(file, ct);
        return await Task.Run(() => BackupKey.ForFile(header, passphrase), ct);
    }

    // The backup's apps from its own data; none to read when it holds no app (or there's no protector to re-encrypt with).
    private async Task<Dictionary<int, SavedAppRows>> SavedAppsAsync(BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        if (_protector is null || !manifest.Items.Any(i => i.Kind == BackupItemKind.App)) return [];
        await using var file = OpenUpload();
        return await SavedApps.ReadAsync(file, key, Path.Combine(_paths.RestoreDir, "read"), _protector, ct);
    }

    // For the targets: a file whose own data can't be read still opens (its containers can be picked, the whole backup
    // restored); its apps then can't be picked, and the log says why.
    private async Task<Dictionary<int, SavedAppRows>> SavedAppsOrNoneAsync(BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        try
        {
            return await SavedAppsAsync(manifest, key, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or BackupFileException))
        {
            _logger.LogWarning(ex, "The backup's apps couldn't be read from its own data");
            return [];
        }
    }

    private async Task PicksAsync(BackupKey key, BackupManifest manifest, List<PickPlan> plans)
    {
        var ct = _lifetime?.ApplicationStopping ?? CancellationToken.None;
        Stopping = ct;
        foreach (var c in manifest.Containers)
            foreach (var value in Secrets(c.Inspect))
                Log.Secret(value);
        try
        {
            Log.Line($"Restoring {string.Join(", ", plans.Select(p => p.Name))} from the backup made on {manifest.Host} on "
                + $"{manifest.SavedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, into this Gitberth (no restart).");
            foreach (var plan in plans)
            {
                // Gitberth's own jobs leave this app alone while it comes back (the keep-running check, the repo watcher).
                _gate.Hold(plan.Slug);
                try
                {
                    await PickAsync(plan, manifest, key, ct);
                }
                finally
                {
                    _gate.Release(plan.Slug);
                }
            }
            lock (_planGate)
                if (_plan is not null) _plan.Finished = true;
            Save();
            var failed = Status()?.Failed ?? 0;
            Log.Line(failed == 0 ? "The restore finished: what you picked is back." : $"The restore finished, with {failed.ToString(CultureInfo.InvariantCulture)} step(s) that failed: see above.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The restore stopped");
            Log.Line("The restore stopped: " + OneLine(ex));
            lock (_planGate)
                if (_plan is not null) _plan.Finished = true;
            Save();
        }
        finally
        {
            key.Dispose();
        }
    }

    // One pick, stage by stage; a failed stage skips the rest of this pick only.
    private async Task PickAsync(PickPlan plan, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        var failed = false;
        async Task Stage(string stage, Func<Task<string>> run)
        {
            var id = $"{plan.Item.Id}:{stage}";
            if (failed)
            {
                await StepAsync(id, () => Task.FromResult(Skip("An earlier step of it failed.")));
                return;
            }
            if (!await StepAsync(id, run)) failed = true;
        }
        var stopped = new List<string>();
        await Stage("stop", () => StopReplacedAsync(plan, stopped, ct));
        await Stage("images", () => PickImagesAsync(plan, manifest, key, ct));
        await Stage("database", () => PickDatabasesAsync(plan, manifest, key, ct));
        if (failed)
        {
            // Nothing of it here has changed: what was stopped for it runs again.
            foreach (var name in stopped)
                try
                {
                    await _engine.StartContainerAsync(name, ct);
                    Log.Line($"{plan.Name}: {name} is started again, as it was.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Couldn't start {Container} again", name);
                }
        }
        await Stage("volumes", () => PickVolumesAsync(plan, key, ct));
        await Stage("app", () => PickRowsAsync(plan, ct));
        await Stage("container", () => PickContainersAsync(plan, manifest, ct));
        await Stage("dump", () => PickDumpAsync(plan, manifest, key, ct));
        await Stage("deploy", () => PickDeployAsync(plan, key, ct));
        await Stage("domains", async () =>
        {
            if (_domains is null || !await _domains.EnabledAsync(ct)) return Skip("Domains aren't turned on.");
            var report = await _domains.SyncAsync(ct);
            return report.Notes.FirstOrDefault(n => n.Problem) is { } problem ? "Synced, with a problem: " + problem.Text : "Synced.";
        });
    }

    private async Task<string> StopReplacedAsync(PickPlan plan, List<string> stopped, CancellationToken ct)
    {
        foreach (var name in plan.Replaced)
            if (await _engine.InspectContainerAsync(name, ct) is { } c && BackupInventory.Running(c))
            {
                await _engine.StopContainerAsync(name, ContainerService.IsDatabase(BackupInventory.Str(c["Config"], "Image"), c["Config"]) ? 60 : 10, ct);
                stopped.Add(name);
            }
        return stopped.Count == 0 ? "Nothing of it was running." : "Stopped " + string.Join(", ", stopped) + ".";
    }

    // Images: the ones its containers run (pulled, or loaded from the file), and its database server's for the dump's helper.
    private async Task<string> PickImagesAsync(PickPlan plan, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        var ids = plan.Containers.Where(c => c.How != ComesBack.Deploy).Select(c => BackupInventory.Str(c.Inspect, "Image")).ToHashSet(StringComparer.Ordinal);
        foreach (var server in plan.Saved?.Databases.Where(d => d.Kind == DatabaseKind.Postgres).Select(d => d.Server) ?? [])
            if (manifest.Containers.FirstOrDefault(c => c.Name == server) is { } saved) ids.Add(BackupInventory.Str(saved.Inspect, "Image"));
        var said = new List<string>();
        var load = new Dictionary<string, SavedImage>(StringComparer.Ordinal);
        foreach (var image in manifest.Images.Where(i => ids.Contains(i.Id)))
        {
            if (await _engine.InspectImageAsync(image.Id, ct) is not null) continue;
            if (image.Part is not null) load[image.Part] = image;
            else if (await PullAsync(image, ct) is { } pulled && !pulled.StartsWith(SkipMark, StringComparison.Ordinal)) said.Add(pulled);
        }
        if (load.Count > 0)
        {
            await using var file = OpenUpload();
            using var reader = await BackupReader.OpenAsync(file, key, ct);
            while (load.Count > 0 && await reader.NextPartAsync(ct) is { } part)
                if (part.Kind == BackupPartKind.Image && load.Remove(part.Name, out var image))
                    said.Add((await _engine.LoadImagesAsync(part.Content, ct)) is { Length: > 0 } loaded ? loaded.Replace('\n', ' ') : $"Loaded {image.Tags.FirstOrDefault() ?? image.Id}.");
        }
        return said.Count == 0 ? "Every image it needs is here." : string.Join(" ", said);
    }

    // Volumes made (or, to replace, emptied) and filled from the file; a Compose app's files (its releases, its data) too.
    private async Task<string> PickVolumesAsync(PickPlan plan, BackupKey key, CancellationToken ct)
    {
        var here = (await _engine.ListVolumesAsync(ct)).OfType<JsonObject>().Select(v => BackupInventory.Str(v, "Name")).ToHashSet(StringComparer.Ordinal);
        string? own = null;
        async Task<string> OwnImage() => own ??= await OwnImageAsync(ct);
        var said = new List<string>();
        var parts = plan.Volumes.Where(v => v.Part is not null).ToDictionary(v => v.Part!, StringComparer.Ordinal);
        var folders = plan.Folders.Where(f => f.Part is not null).ToDictionary(f => f.Part!, StringComparer.Ordinal);
        var app = plan.Saved?.App;
        var compose = app?.IsCompose == true;
        if (parts.Count > 0 || folders.Count > 0 || compose)
        {
            await using var file = OpenUpload();
            using var reader = await BackupReader.OpenAsync(file, key, ct);
            while ((parts.Count > 0 || folders.Count > 0 || compose) && await reader.NextPartAsync(ct) is { } part)
            {
                if (part.Kind == BackupPartKind.Volume && parts.Remove(part.Name, out var volume))
                    said.Add(await PickVolumeAsync(plan, volume, part.Content, here, OwnImage, ct));
                else if (part.Kind == BackupPartKind.HostFolder && folders.Remove(part.Name, out var folder))
                {
                    var note = await FillFolderAsync(plan.Folder(folder.Path), folder, part.Content, plan.Pick.Action, await OwnImage(), ct);
                    said.Add(note.StartsWith(SkipMark, StringComparison.Ordinal) ? note[SkipMark.Length..] : note);
                }
                else if (compose && part.Kind == BackupPartKind.DataFolder && part.Name == "data/compose")
                {
                    compose = false;
                    said.Add(await ComposeFilesAsync(app!, plan.Slug, plan.Pick.Action, part.Content, ct));
                }
            }
        }
        foreach (var volume in plan.Volumes.Where(v => v.Part is null))
        {
            var name = plan.Rename(volume.Name);
            if (here.Contains(name)) continue;
            await CreateVolumeAsync(volume, name, ct);
            said.Add($"{name}: made, empty.");
        }
        return said.Count == 0 ? "It has no volumes." : string.Join(" ", said);
    }

    private async Task<string> PickVolumeAsync(PickPlan plan, SavedVolume volume, Stream tar, HashSet<string> here, Func<Task<string>> ownImage, CancellationToken ct)
    {
        var name = plan.Rename(volume.Name);
        if (here.Contains(name))
        {
            if (plan.Pick.Action == RestoreAction.Restore) return $"{name}: here already, kept as it is.";
            // Only Replace refills a volume that's here: a copy's are new ones.
            if (plan.Pick.Action != RestoreAction.Replace) throw new InvalidOperationException($"A volume called {name} is here already, and it isn't this copy's.");
            await EmptyVolumeAsync(name, await ownImage(), ct);
        }
        else
        {
            await CreateVolumeAsync(volume, name, ct);
            here.Add(name);
        }
        await PutIntoVolumeAsync(name, tar, await ownImage(), ct);
        return $"{name}: filled{(volume.Bytes is { } bytes ? $" ({Size(bytes)})" : "")}.";
    }

    private async Task CreateVolumeAsync(SavedVolume volume, string name, CancellationToken ct)
    {
        var body = new JsonObject { ["Name"] = name, ["Driver"] = volume.Driver };
        if (volume.Options is { Count: > 0 } options) body["DriverOpts"] = Object(options);
        var labels = volume.Labels is { Count: > 0 } l ? new Dictionary<string, string>(l) : new Dictionary<string, string>();
        // A Compose volume of a copy belongs to the copy's project.
        if (labels.TryGetValue(NetworkService.ComposeProjectLabel, out var project) && name != volume.Name && name.IndexOf('_') > 0)
            labels[NetworkService.ComposeProjectLabel] = project.StartsWith("gb-", StringComparison.Ordinal) ? name[..name.IndexOf('_')] : project;
        if (labels.Count > 0) body["Labels"] = Object(labels);
        await _engine.CreateVolumeAsync(body, ct);
    }

    // A volume's files through a helper made with it at /v: the tar's entries are v/…, put in at /.
    private async Task PutIntoVolumeAsync(string volume, Stream tar, string ownImage, CancellationToken ct)
    {
        var helper = Helper("restore");
        var body = BackupService.HelperBody(ownImage, volume + ":/v");
        body["Labels"]![HostScanHelper.Label] = "restore";
        await _engine.CreateContainerAsync(helper, body, ct);
        try
        {
            await _engine.PutArchiveAsync(helper, "/", tar, ct);
        }
        finally
        {
            await RemoveQuietlyAsync(helper);
        }
    }

    // A volume emptied, to be filled again: a helper of Gitberth's own image deletes what's in it.
    private async Task EmptyVolumeAsync(string volume, string ownImage, CancellationToken ct)
    {
        var helper = Helper("restore");
        var body = BackupService.HelperBody(ownImage, volume + ":/v");
        body["Labels"]![HostScanHelper.Label] = "restore";
        body["Entrypoint"] = new JsonArray("find");
        body["Cmd"] = new JsonArray("/v", "-mindepth", "1", "-delete");
        await _engine.CreateContainerAsync(helper, body, ct);
        try
        {
            await _engine.StartContainerAsync(helper, ct);
            var exit = await _engine.WaitContainerAsync(helper, ct);
            if (exit != 0) throw new InvalidOperationException($"Emptying the volume {volume} failed (exit code {exit.ToString(CultureInfo.InvariantCulture)}).");
        }
        finally
        {
            await RemoveQuietlyAsync(helper);
        }
    }

    private static string Helper(string kind) => $"{HostScanHelper.NamePrefix}{kind}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";

    private async Task RemoveQuietlyAsync(string container)
    {
        try
        {
            await _engine.RemoveContainerAsync(container, force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Couldn't remove the helper {Helper}", container);
        }
    }

    /// <summary>
    /// A Compose app's folder (its releases and what its services wrote to relative binds) from the saved Gitberth's data.
    /// Replace: the folder becomes the saved one. Restore: a data/ folder that's here (its app was removed, its data kept) is
    /// kept as it is. A copy's folder is new (the picks were checked).
    /// </summary>
    internal async Task<string> ComposeFilesAsync(AppDefinition saved, string slug, RestoreAction action, Stream tar, CancellationToken ct)
    {
        var root = Path.Combine(_options.DataDir, "compose");
        var target = Path.Combine(root, slug);
        if (action == RestoreAction.Replace && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        var keepData = action == RestoreAction.Restore && Directory.Exists(Path.Combine(target, "data"));
        var prefix = $"compose/{saved.Slug}/";
        await ExtractAsync(tar, root, ct, name =>
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var rest = name[prefix.Length..];
            return keepData && (rest == "data" || rest.StartsWith("data/", StringComparison.Ordinal)) ? null : slug + "/" + rest;
        });
        return !Directory.Exists(target) ? "It had no Compose files saved."
            : keepData ? $"Its Compose files are in {target}; the data/ folder there is kept as it was, not the saved one."
            : $"Its Compose files are in {target}.";
    }

    // [builder B, B6] An uploaded app's kept uploads (data/uploads/<its saved ID>/) into its folder here, under its ID here.
    internal async Task<string> UploadFilesAsync(AppDefinition saved, int appId, Stream tar, CancellationToken ct)
    {
        var root = Deploy.AppDataFolders.UploadsRoot(_options);
        var prefix = $"uploads/{saved.Id.ToString(CultureInfo.InvariantCulture)}/";
        var target = appId.ToString(CultureInfo.InvariantCulture) + "/";
        await ExtractAsync(tar, root, ct, name => name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? target + name[prefix.Length..] : null);
        var kept = Directory.Exists(Path.Combine(root, appId.ToString(CultureInfo.InvariantCulture)))
            ? Directory.EnumerateFiles(Path.Combine(root, appId.ToString(CultureInfo.InvariantCulture))).Count() : 0;
        return kept == 0 ? "It had no uploads saved: upload it again on its page." : $"Its {kept} kept upload{(kept == 1 ? "" : "s")} came back.";
    }

    // ---- one database out of its server's dump (P5) ----

    private async Task<string> PickDatabasesAsync(PickPlan plan, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        var said = new List<string>();
        foreach (var saved in plan.Saved?.Databases ?? [])
        {
            if (saved.Kind != DatabaseKind.Postgres) continue;
            said.Add(await PickDatabaseAsync(plan, saved, manifest, key, ct));
        }
        return said.Count == 0 ? Skip("It has no database of its own.") : string.Join(" ", said);
    }

    private async Task<string> PickDatabaseAsync(PickPlan plan, ManagedDatabase saved, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        if (await _engine.InspectContainerAsync(saved.Server, ct) is not { } live || !BackupInventory.Running(live))
            throw new InvalidOperationException($"Its database server {saved.Server} isn't running here: restore or start it first.");
        var dump = manifest.Databases.FirstOrDefault(d => d.Container == saved.Server)
            ?? throw new InvalidOperationException($"The backup has no dump of {saved.Server}, so its database can't come back.");
        var server = manifest.Containers.First(c => c.Name == saved.Server);
        var login = BackupInventory.DatabasePlanOf(live, await _engine.InspectImageAsync(BackupInventory.Str(live, "Image"), ct), [])
            ?? throw new InvalidOperationException($"{saved.Server} isn't a Postgres server here.");
        var (databases, roles) = await PgNamesAsync(login, ct);

        // What it becomes here: as saved (Restore, Replace), or a database and role of the copy's own with a new password.
        string name, role, password;
        if (plan.Pick.Action == RestoreAction.Copy)
        {
            name = role = DatabaseNames.ForApp(plan.Slug, databases.Concat(roles).ToHashSet(StringComparer.OrdinalIgnoreCase));
            password = DatabaseService.NewPassword();
        }
        else
        {
            name = saved.Name;
            role = saved.Role ?? saved.Name;
            password = _protector!.TryUnprotect(saved.ProtectedPassword);
        }

        // Restored into a database of its own first (its role made when there's none): nothing here changes until that worked,
        // and it goes again when it didn't.
        var temp = DatabaseNames.Shorten($"{name}_restoring_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}");
        var newRole = !roles.Contains(role);
        var verifier = PostgresSql.Literal(Scram.Verifier(password));
        var make = new StringBuilder();
        if (newRole) make.Append("CREATE ROLE ").Append(PostgresSql.Ident(role)).Append(" LOGIN PASSWORD ").Append(verifier).Append(";\n");
        make.Append("CREATE DATABASE ").Append(PostgresSql.Ident(temp)).Append(" OWNER ").Append(PostgresSql.Ident(role)).Append(";\n");
        make.Append("REVOKE CONNECT, TEMPORARY ON DATABASE ").Append(PostgresSql.Ident(temp)).Append(" FROM PUBLIC;\n");

        // Then, in one transaction: what's here under the names it takes kept under _before_ ones (the current database of
        // the app it replaces first), the restored one under its name, and its role's password the saved one.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var aside = new List<string>();
        if (plan.Current is { } current)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            aside.AddRange(await db.ManagedDatabases.AsNoTracking().Where(d => d.AppId == current.Id && d.Kind == DatabaseKind.Postgres && d.Server == saved.Server)
                .Select(d => d.Name).ToListAsync(ct));
        }
        aside.Add(name);
        var kept = aside.Distinct(StringComparer.Ordinal).Where(databases.Contains).Select(old => (Old: old, Before: DatabaseNames.Shorten($"{old}_before_{stamp}"))).ToList();
        var swap = new StringBuilder();
        if (kept.Count > 0)
            swap.Append("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname IN (")
                .Append(string.Join(", ", kept.Select(k => PostgresSql.Literal(k.Old)))).Append(") AND pid <> pg_backend_pid();\n");
        swap.Append("BEGIN;\n");
        foreach (var (old, before) in kept) swap.Append("ALTER DATABASE ").Append(PostgresSql.Ident(old)).Append(" RENAME TO ").Append(PostgresSql.Ident(before)).Append(";\n");
        swap.Append("ALTER DATABASE ").Append(PostgresSql.Ident(temp)).Append(" RENAME TO ").Append(PostgresSql.Ident(name)).Append(";\n");
        if (!newRole) swap.Append("ALTER ROLE ").Append(PostgresSql.Ident(role)).Append(" LOGIN PASSWORD ").Append(verifier).Append(";\n");
        swap.Append("COMMIT;\n");

        try
        {
            await PgAsync(login, "postgres", make.ToString(), ct);
            await CopyOutOfDumpAsync(server, dump, saved.Name, login, temp, role, key, ct);
            await PgAsync(login, "postgres", swap.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await DropQuietlyAsync(login, temp, newRole ? role : null);
            throw;
        }
        foreach (var (old, before) in kept) Log.Line($"{plan.Name}: the database {old} here is kept as {before}.");

        plan.Databases.Add(new ManagedDatabase
        {
            Kind = DatabaseKind.Postgres, Server = saved.Server, Name = name, Role = role, Variable = saved.Variable, Created = true,
            ProtectedPassword = _protector!.Protect(password),
        });
        return $"{name}: restored from {saved.Server}'s dump" + (kept.Count > 0 ? $" ({string.Join("; ", kept.Select(k => $"{k.Old} is kept as {k.Before}"))})." : ".");
    }

    // A restore into a database of its own that didn't work: that database goes, and the role it made for it.
    private async Task DropQuietlyAsync(DatabasePlan login, string temp, string? role)
    {
        var sql = $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = {PostgresSql.Literal(temp)} AND pid <> pg_backend_pid();\n"
            + $"DROP DATABASE IF EXISTS {PostgresSql.Ident(temp)};\n"
            + (role is null ? "" : $"DROP ROLE IF EXISTS {PostgresSql.Ident(role)};\n");
        try
        {
            await PgAsync(login, "postgres", sql, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Couldn't drop the restore's database {Database}", temp);
            Log.Line($"The database {temp} a restore made couldn't be removed: remove it on {login.Container}.");
        }
    }

    // The saved server's dump loaded into a throwaway server of its image (its own volume, no network), and only the one
    // database copied out of it into the live server: pg_dump -Fc there, pg_restore here as the target's role.
    private async Task CopyOutOfDumpAsync(SavedContainer server, SavedDatabase dump, string savedName, DatabasePlan live, string target, string role,
        BackupKey key, CancellationToken ct)
    {
        var helper = Helper("restore-db");
        var volume = helper + "-data";
        var user = BackupInventory.DatabasePlanOf(server.Inspect, server.Image, [])?.User ?? "postgres";
        var password = DatabaseService.NewPassword();
        var image = BackupInventory.Str(server.Inspect, "Image");
        var paths = (server.Image?["Config"]?["Volumes"] as JsonObject ?? []).Select(v => v.Key).ToList();
        if (paths.Count == 0) paths.Add("/var/lib/postgresql");
        Log.Line($"Loading {server.Name}'s dump into a throwaway server, to take {savedName} out of it: this takes as long as the whole dump's load.");
        await _engine.CreateVolumeAsync(new JsonObject { ["Name"] = volume, ["Labels"] = new JsonObject { [HostScanHelper.Label] = "restore" } }, ct);
        try
        {
            var body = new JsonObject
            {
                ["Image"] = image,
                ["Env"] = new JsonArray($"POSTGRES_USER={user}", $"POSTGRES_PASSWORD={password}"),
                ["Labels"] = new JsonObject { [HostScanHelper.Label] = "restore" },
                ["HostConfig"] = new JsonObject
                {
                    ["NetworkMode"] = "none",
                    ["Mounts"] = new JsonArray([.. paths.Select((p, i) => (JsonNode?)new JsonObject { ["Type"] = "volume", ["Source"] = i == 0 ? volume : $"{volume}-{i}", ["Target"] = p })]),
                },
            };
            await _engine.CreateContainerAsync(helper, body, ct);
            try
            {
                await _engine.StartContainerAsync(helper, ct);
                var plan = new DatabasePlan(helper, "postgres", user, password, [], [], true, true);
                await WaitReadyAsync(plan, ct);
                await using (var file = OpenUpload())
                using (var reader = await BackupReader.OpenAsync(file, key, ct))
                {
                    var loaded = false;
                    while (!loaded && await reader.NextPartAsync(ct) is { } part)
                    {
                        if (part.Kind != BackupPartKind.Dump || part.Name != dump.Part) continue;
                        var r = await _engine.ExecAsync(helper, LoadCommand(plan), BackupInventory.DumpEnv(plan), part.Content, Stream.Null, ct);
                        if (!r.Ok) throw new InvalidOperationException($"Loading {server.Name}'s dump failed (exit code {r.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
                        loaded = true;
                    }
                    if (!loaded) throw new InvalidOperationException($"{server.Name}'s dump isn't in the file.");
                }

                // pg_dump's output straight into pg_restore's input.
                var pipe = new Pipe();
                var restoring = Task.Run(async () =>
                {
                    try
                    {
                        return await _engine.ExecAsync(live.Container,
                            ["pg_restore", "-U", live.User, "-d", target, "--no-owner", "--no-privileges", "--role=" + role],
                            BackupInventory.DumpEnv(live), pipe.Reader.AsStream(), Stream.Null, ct);
                    }
                    finally
                    {
                        await pipe.Reader.CompleteAsync();
                    }
                }, CancellationToken.None);
                ExecResult dumped;
                try
                {
                    await using var output = pipe.Writer.AsStream();
                    dumped = await _engine.ExecAsync(helper, ["pg_dump", "-Fc", "-U", user, "-d", savedName], BackupInventory.DumpEnv(plan), null, output, ct);
                }
                catch (InvalidOperationException) when (restoring.IsCompleted)
                {
                    dumped = new ExecResult(1, "");
                }
                var restored = await restoring;
                if (!dumped.Ok && restored.Ok) throw new InvalidOperationException($"Taking {savedName} out of the dump failed: {FirstLine(dumped.StdErr) ?? "it said nothing"}");
                if (!restored.Ok && Errors(restored.StdErr) is { Count: > 0 } errors)
                    throw new InvalidOperationException($"Restoring {target} failed: {errors[0]}");
            }
            finally
            {
                await RemoveQuietlyAsync(helper);
            }
        }
        finally
        {
            foreach (var i in Enumerable.Range(0, paths.Count))
                try
                {
                    await _engine.RemoveVolumeAsync(i == 0 ? volume : $"{volume}-{i}", CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Couldn't remove the throwaway volume {Volume}", volume);
                }
        }
    }

    private static string? FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    // Every database and role name on a Postgres server.
    private async Task<(HashSet<string> Databases, HashSet<string> Roles)> PgNamesAsync(DatabasePlan login, CancellationToken ct)
    {
        var output = await PgAsync(login, "postgres", PostgresSql.Names, ct);
        var (databases, roles) = PostgresSql.ParseNames(output);
        return (databases, roles);
    }

    // psql as the server's superuser, the script on its standard input, stopping at the first error.
    private async Task<string> PgAsync(DatabasePlan login, string database, string sql, CancellationToken ct)
    {
        var output = new MemoryStream();
        var r = await _engine.ExecAsync(login.Container, PostgresSql.Psql(login.User, database), BackupInventory.DumpEnv(login),
            new MemoryStream(Encoding.UTF8.GetBytes(sql)), output, ct);
        if (!r.Ok) throw new InvalidOperationException($"{login.Container} refused it: {FirstLine(r.StdErr) ?? $"exit code {r.ExitCode.ToString(CultureInfo.InvariantCulture)}"}");
        return Encoding.UTF8.GetString(output.ToArray());
    }

    // ---- the app's rows ----

    private static readonly JsonSerializerOptions SnapshotJson = new() { Converters = { new JsonStringEnumConverter() } };

    private async Task<string> PickRowsAsync(PickPlan plan, CancellationToken ct)
    {
        if (plan.Saved is not { } saved) return Skip("It isn't an app.");
        var app = saved.App;
        var copy = plan.Pick.Action == RestoreAction.Copy;
        var notes = new List<string>();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var taken = new HashSet<int>();
        foreach (var other in await db.Ports.AsNoTracking().Where(p => p.HostPort != null && p.AppId != (plan.Current == null ? 0 : plan.Current.Id)).Select(p => p.HostPort!.Value).ToListAsync(ct))
            taken.Add(other);
        foreach (var c in await _engine.ListAllAsync(ct))
            if (c.Name != plan.Rename(app.ContainerName) && !plan.Replaced.Contains(c.Name) && await _engine.InspectContainerAsync(c.Id, ct) is { } inspect)
                foreach (var port in HostPorts(inspect)) taken.Add(port);
        var otherDomains = (await db.Domains.AsNoTracking().Where(d => d.AppId != (plan.Current == null ? 0 : plan.Current.Id)).Select(d => d.Hostname).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AppDefinition row;
        if (plan.Current is { } current)
        {
            row = await db.Apps.Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Domains).Include(a => a.Networks)
                .AsSplitQuery().FirstAsync(a => a.Id == current.Id, ct);
            db.Ports.RemoveRange(row.Ports);
            db.EnvVars.RemoveRange(row.EnvVars);
            db.Volumes.RemoveRange(row.Volumes);
            db.Domains.RemoveRange(row.Domains);
            db.AppNetworks.RemoveRange(row.Networks);
            db.DeployHooks.RemoveRange(db.DeployHooks.Where(h => h.AppId == row.Id));
            db.ManagedDatabases.RemoveRange(db.ManagedDatabases.Where(d => d.AppId == row.Id));
            await db.SaveChangesAsync(ct);
            row.Ports.Clear();
            row.EnvVars.Clear();
            row.Volumes.Clear();
            row.Domains.Clear();
            row.Networks.Clear();
        }
        else
        {
            row = new AppDefinition();
            db.Apps.Add(row);
        }

        Settings(row, app);
        row.Name = plan.Name;
        row.Slug = plan.Slug;
        if (plan.Current is null)
        {
            row.Container = app.Container is null ? null : plan.Rename(app.ContainerName);
            row.ContainerId = null;
            row.Pinned = app.Pinned;
        }
        if (copy)
        {
            row.PreviewMode = PreviewMode.Off;
            row.SpecProtected = CopySnapshot(app.SpecProtected, plan, taken, notes);
        }

        foreach (var p in app.Ports)
        {
            var port = new PortMapping { ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = p.Access, HostPort = p.HostPort, HostIp = p.HostIp, IsWeb = p.IsWeb };
            if (port.HostPort is { } wanted && taken.Contains(wanted))
            {
                port.HostPort = await FreePortAsync(wanted, port.Protocol, taken, ct);
                notes.Add($"port {wanted.ToString(CultureInfo.InvariantCulture)} was taken, so it's {port.HostPort.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            if (port.HostPort is { } got) taken.Add(got);
            row.Ports.Add(port);
        }
        foreach (var v in app.EnvVars)
            row.EnvVars.Add(new EnvVar { Key = v.Key, ProtectedValue = v.ProtectedValue, IsSecret = v.IsSecret, BuildTime = v.BuildTime, Scope = v.Scope });
        // A named volume's short name is the app's own (gb-<slug>-<name> when it's deployed): a copy's are the copy's.
        // A host folder the backup holds is the copy's own (filled from the file).
        foreach (var v in app.Volumes) row.Volumes.Add(new VolumeMount { Source = copy ? plan.Folder(v.Source) : v.Source, Target = v.Target, ReadOnly = v.ReadOnly });
        foreach (var n in app.Networks)
            row.Networks.Add(new AppNetwork { Network = n.Network, Alias = copy ? null : n.Alias, IPv4Address = copy ? null : n.IPv4Address });
        if (!copy)
            foreach (var d in app.Domains)
            {
                if (otherDomains.Contains(d.Hostname))
                {
                    notes.Add($"{d.Hostname} is another app's here, so it's left off");
                    continue;
                }
                row.Domains.Add(new CustomDomain { Hostname = d.Hostname, KeepPaths = d.KeepPaths, Service = d.Service, Main = d.Main, Protected = d.Protected, AccessAppId = d.AccessAppId });
            }
        await db.SaveChangesAsync(ct);
        plan.AppId = row.Id;

        // Its hooks (not a copy's), its databases as made here, and its deploys: a saved commit is one this app ran.
        if (!copy)
        {
            var hashes = (await db.DeployHooks.AsNoTracking().Select(h => h.TokenHash).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            foreach (var h in saved.Hooks.Where(h => !hashes.Contains(h.TokenHash)))
                db.DeployHooks.Add(new DeployHook { AppId = row.Id, Name = h.Name, TokenHash = h.TokenHash, ProtectedToken = h.ProtectedToken, CreatedAt = h.CreatedAt });
        }
        foreach (var d in plan.Databases)
        {
            d.AppId = row.Id;
            db.ManagedDatabases.Add(d);
        }
        foreach (var redis in saved.Databases.Where(d => d.Kind == DatabaseKind.Redis))
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                AppId = row.Id, Kind = DatabaseKind.Redis, Server = plan.Rename(redis.Server), Variable = redis.Variable, Created = redis.Created,
                Name = copy ? $"{plan.Slug}-redis" : redis.Name, ProtectedPassword = redis.ProtectedPassword,
            });
        foreach (var d in saved.Deployments)
            db.Deployments.Add(new Deployment
            {
                AppId = row.Id, Trigger = d.Trigger, Status = d.Status is DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting ? DeployStatus.Failed : d.Status,
                CommitSha = d.CommitSha, CommitMessage = d.CommitMessage, CommitAuthor = d.CommitAuthor, ImageTag = d.ImageTag, Error = d.Error,
                QueuedAt = d.QueuedAt, StartedAt = d.StartedAt, FinishedAt = d.FinishedAt, GitberthBuild = d.GitberthBuild, SkipReason = d.SkipReason,
                HookName = d.HookName, NoCache = d.NoCache, RequestedBy = d.RequestedBy, Images = d.Images,
            });
        await db.SaveChangesAsync(ct);

        // [builder B, B3] Its config files, as files of its own here; an image app's saved settings mount those.
        var configs = await Workbench.ConfigFiles.RestoreAsync(db, _options, _protector, row.Id, saved.ConfigFiles, ct);
        if (configs.Count > 0 && row.SpecProtected is { } saving && _protector?.TryUnprotect(saving) is { Length: > 0 } savedJson
            && JsonSerializer.Deserialize<AppSnapshot>(savedJson, SnapshotJson) is { } withConfigs)
        {
            row.SpecProtected = _protector.Protect(JsonSerializer.Serialize(withConfigs with
            {
                Settings = withConfigs.Settings with { Mounts = Workbench.ConfigFiles.Remap(withConfigs.Settings.Mounts, row.Id, configs) },
            }, SnapshotJson));
            await db.SaveChangesAsync(ct);
        }
        // [builder B, B3] end.

        // An image app's copy runs from its saved settings: its own databases' URLs in them, and its own slug on its label.
        if (copy && row.SpecProtected is { } spec && _protector?.TryUnprotect(spec) is { Length: > 0 } json
            && JsonSerializer.Deserialize<AppSnapshot>(json, SnapshotJson) is { } snapshot)
        {
            var values = await ValuesOfAsync(row.Id, ct);
            row.SpecProtected = _protector.Protect(JsonSerializer.Serialize(snapshot with { Settings = ForCopy(snapshot.Settings, values, plan.Slug) }, SnapshotJson));
            await db.SaveChangesAsync(ct);
        }
        // Its ports are saved: the port map Gitberth keeps reads them again.
        _hostPorts?.ForgetSaved();
        var verb = plan.Pick.Action switch { RestoreAction.Replace => "replaced its settings", RestoreAction.Copy => $"made as a copy of {app.Name}", _ => "brought back" };
        return $"{plan.Name} {verb} (app {row.Id.ToString(CultureInfo.InvariantCulture)})" + (notes.Count > 0 ? ": " + string.Join("; ", notes) + "." : ".");
    }

    // The saved app's settings onto the row (never its ID, name, slug or container: the caller sets those).
    private static void Settings(AppDefinition row, AppDefinition app)
    {
        row.Owner = app.Owner;
        row.Repo = app.Repo;
        row.Branch = app.Branch;
        row.Subdirectory = app.Subdirectory;
        row.DockerfileMode = app.DockerfileMode;
        row.CustomDockerfile = app.CustomDockerfile;
        row.ProjectHint = app.ProjectHint;
        row.DetectedKind = app.DetectedKind;
        row.DetectedSummary = app.DetectedSummary;
        row.DetectedPort = app.DetectedPort;
        row.AutoDeploy = app.AutoDeploy;
        row.DesiredState = app.DesiredState;
        row.RestartPolicy = app.RestartPolicy;
        row.MemoryLimitMb = app.MemoryLimitMb;
        row.CpuLimit = app.CpuLimit;
        row.HealthCheckPath = app.HealthCheckPath;
        row.VercelVariables = app.VercelVariables;
        row.CurrentImage = null;
        row.CurrentCommit = app.CurrentCommit;
        row.LastAttemptedCommit = app.CurrentCommit;
        row.ConfigChangePending = false;
        row.RebuildPending = false;
        row.Source = app.Source;
        row.ImageRef = app.ImageRef;
        row.RegistryId = app.RegistryId;
        row.UpdatePolicy = app.UpdatePolicy;
        row.PreviousImage = null;
        row.SpecProtected = app.SpecProtected;
        row.RuntimeExtras = app.RuntimeExtras;
        row.SecretKeys = app.SecretKeys;
        row.PreviewMode = app.PreviewMode;
        row.PreviewLimit = app.PreviewLimit;
        row.PreviewExpiryDays = app.PreviewExpiryDays;
        row.PreviewForks = app.PreviewForks;
        row.ProtectPreviews = app.ProtectPreviews;
        row.WatchPaths = app.WatchPaths;
        row.IgnoredPaths = app.IgnoredPaths;
        row.PromotedOverCommit = null;
        row.UptimeCheck = app.UptimeCheck;
        row.UptimePath = app.UptimePath;
        row.DownPage = app.DownPage;
        row.DownPageMessage = app.DownPageMessage;
        row.ComposeFile = app.ComposeFile;
        row.ComposeYamlProtected = app.ComposeYamlProtected;
        row.WebService = app.WebService;
        row.PreviewDatabases = app.PreviewDatabases;
        row.TemplateId = app.TemplateId;
        // [builder B] An uploaded app's upload (B6) and any app's release command (B2).
        row.UploadName = app.UploadName;
        row.UploadSha = app.UploadSha;
        row.UploadSize = app.UploadSize;
        row.UploadedAt = app.UploadedAt;
        row.UploadedBy = app.UploadedBy;
        row.ReleaseCommand = app.ReleaseCommand;
        row.ReleaseTimeoutMinutes = app.ReleaseTimeoutMinutes;
        row.ReleaseService = app.ReleaseService;
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A copy's container settings: the variables its databases set (DATABASE_URL, REDIS_URL…) hold its own databases' URLs,
    /// never the original's, and gitberth.app says it's the copy's.
    /// </summary>
    internal static ContainerSettings ForCopy(ContainerSettings settings, IReadOnlyList<ProvidedVariable> values, string slug)
    {
        var env = settings.Env.ToList();
        foreach (var value in values)
        {
            var at = env.FindIndex(e => e.Key == value.Key && !e.FromImage);
            if (at >= 0) env[at] = env[at] with { Value = value.Value };
            else env.Add(new EnvSetting(value.Key, value.Value, Secret: true));
        }
        var labels = settings.Labels.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (labels.ContainsKey("gitberth.app")) labels["gitberth.app"] = slug;
        return settings with { Env = env, Labels = labels };
    }

    // An image app's saved settings for its copy: its volumes renamed, its host ports free, and no pinned address or alias.
    private string? CopySnapshot(string? spec, PickPlan plan, HashSet<int> taken, List<string> notes)
    {
        if (spec is null || _protector is null || _protector.TryUnprotect(spec) is not { Length: > 0 } json) return spec;
        var snapshot = JsonSerializer.Deserialize<AppSnapshot>(json, SnapshotJson);
        if (snapshot is null) return spec;
        var settings = snapshot.Settings with
        {
            Mounts = snapshot.Settings.Mounts.Select(m => m.Kind switch
            {
                MountKind.Volume => m with { Source = plan.Rename(m.Source) },
                MountKind.Bind => m with { Source = plan.Folder(m.Source) },
                _ => m,
            }).ToList(),
            Ports = snapshot.Settings.Ports.Select(p =>
            {
                if (p.HostPort is not { } wanted || !taken.Contains(wanted)) return p;
                var free = Enumerable.Range(wanted + 1, 65535 - wanted).First(n => !taken.Contains(n));
                taken.Add(free);
                notes.Add($"port {wanted.ToString(CultureInfo.InvariantCulture)} was taken, so it's {free.ToString(CultureInfo.InvariantCulture)}");
                return p with { HostPort = free };
            }).ToList(),
        };
        var networks = snapshot.Networks.Select(n => n with { Aliases = [], IPv4Address = null }).ToList();
        return _protector.Protect(JsonSerializer.Serialize(new AppSnapshot(settings, networks), SnapshotJson));
    }

    // A free host port: the one asked for, else the next free one above it (Gitberth's port map when it's there).
    private async Task<int> FreePortAsync(int wanted, string protocol, HashSet<int> taken, CancellationToken ct)
    {
        if (_hostPorts is not null)
        {
            var suggested = await _hostPorts.SuggestAsync(wanted, protocol, null, taken, ct);
            if (!taken.Contains(suggested)) return suggested;
        }
        for (var port = wanted + 1; port <= 65535; port++)
            if (!taken.Contains(port)) return port;
        throw new InvalidOperationException("No host port is free.");
    }

    // ---- its containers ----

    private async Task<string> PickContainersAsync(PickPlan plan, BackupManifest manifest, CancellationToken ct)
    {
        var created = plan.Containers.Where(c => c.How != ComesBack.Deploy).ToList();
        if (created.Count == 0) return Skip("Gitberth deploys it.");
        var said = new List<string>();
        var app = plan.Saved?.App;
        foreach (var saved in created)
        {
            var name = plan.Rename(saved.Name);
            if (plan.Replaced.Contains(name) && await _engine.InspectContainerAsync(name, ct) is not null) await _engine.RemoveContainerAsync(name, force: true, ct);
            if (await _engine.InspectContainerAsync(name, ct) is not null) throw new InvalidOperationException($"A container called {name} is here already.");
            // An app made from an image comes back through the change engine, from its saved settings.
            if (app is not null && saved.Name == app.ContainerName && plan.AppId is { } appId && _actions is not null && app.SpecProtected is not null
                && app.Source is AppSource.Registry or AppSource.Local or AppSource.Manual)
            {
                AppDefinition row;
                await using (var db = await _dbFactory.CreateDbContextAsync(ct))
                    row = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == appId, ct);
                if (await _actions.CreateAgainAsync(row, ct) is { } refused) throw new InvalidOperationException(refused);
                await RunsAsync(name, ct);
                said.Add($"{name}: made from its saved settings.");
                continue;
            }
            await CreateFromInspectAsync(saved, name, plan, ct);
            if (saved.Running) await _engine.StartContainerAsync(name, ct);
            said.Add(saved.Running ? $"{name}: made and started." : $"{name}: made (it wasn't running).");
        }
        if (plan.AppId is { } id && _catalog is not null) await _catalog.SyncAsync(id, ct);
        return string.Join(" ", said);
    }

    // Started within 2 minutes (the change engine makes and starts it on its own).
    private async Task RunsAsync(string name, CancellationToken ct)
    {
        for (var until = DateTime.UtcNow + TimeSpan.FromMinutes(2); ; await Task.Delay(TimeSpan.FromSeconds(1), ct))
        {
            if (await _engine.InspectContainerAsync(name, ct) is { } c && BackupInventory.Running(c)) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"{name} didn't start within 2 minutes.");
        }
    }

    // A container from its saved inspect, under its name here: its volumes renamed, its taken host ports free, on the
    // networks here; a copy without the aliases and addresses the saved one answers to.
    private async Task CreateFromInspectAsync(SavedContainer saved, string name, PickPlan plan, CancellationToken ct)
    {
        var settings = ContainerSettingsJson.Read(saved.Inspect, saved.Image);
        var copy = plan.Pick.Action == RestoreAction.Copy;
        var taken = new HashSet<int>();
        foreach (var c in await _engine.ListAllAsync(ct))
            if (c.Name != name && await _engine.InspectContainerAsync(c.Id, ct) is { } inspect)
                foreach (var port in HostPorts(inspect)) taken.Add(port);
        var ports = new List<PortRule>();
        foreach (var p in settings.Ports)
        {
            if (p.HostPort is { } wanted && taken.Contains(wanted))
            {
                var free = await FreePortAsync(wanted, p.Protocol, taken, ct);
                Log.Line($"{name}: port {wanted.ToString(CultureInfo.InvariantCulture)} is taken here, so it's {free.ToString(CultureInfo.InvariantCulture)}.");
                ports.Add(p with { HostPort = free });
                taken.Add(free);
            }
            else ports.Add(p);
        }
        var labels = settings.Labels.ToDictionary(kv => kv.Key, kv => plan.Rename(kv.Value), StringComparer.Ordinal);
        if (copy && labels.ContainsKey("gitberth.app")) labels["gitberth.app"] = plan.Slug;
        settings = settings with
        {
            Mounts = settings.Mounts.Select(m => m.Kind switch
            {
                MountKind.Volume => m with { Source = plan.Rename(m.Source) },
                MountKind.Bind => m with { Source = plan.Folder(m.Source) },
                _ => m,
            }).ToList(),
            Ports = ports,
            Labels = labels,
        };
        // A copied app's own container takes its own databases' URLs, never the original's.
        if (copy && plan.AppId is { } appId && plan.Saved?.App.ContainerName == saved.Name)
            settings = ForCopy(settings, await ValuesOfAsync(appId, ct), plan.Slug);
        // [builder B, B3] Its config files' mounts are its own files here, by their paths in the container.
        if (plan.AppId is { } configApp && plan.Saved?.App.ContainerName == saved.Name)
        {
            await using var configDb = await _dbFactory.CreateDbContextAsync(ct);
            var configs = await configDb.ConfigFiles.AsNoTracking().Where(f => f.AppId == configApp).ToListAsync(ct);
            if (configs.Count > 0) settings = settings with { Mounts = Workbench.ConfigFiles.Remap(settings.Mounts, configApp, configs) };
        }
        var here = (await _engine.ListNetworksJsonAsync(ct)).OfType<JsonObject>().Select(n => BackupInventory.Str(n, "Name")).ToHashSet(StringComparer.Ordinal);
        // A copy answers to none of the saved one's aliases or addresses (they're the original's); a copy's Redis to the copy's.
        var redis = copy && DatabaseNames.IsMember(labels);
        var joins = AppCatalog.JoinsOf(saved.Inspect, _options.Network)
            .Where(j => j.Name != LanAddresses.Network && here.Contains(j.Name))
            .Select(j => !copy ? j : j with { Aliases = redis && j.Name == _options.Network ? [$"{plan.Slug}-redis"] : [], IPv4Address = null })
            .ToList();
        if (ContainerSettingsJson.IsOwnMode(settings.NetworkMode)) joins = [];
        var api = await _engine.ApiVersionAsync(ct);
        await _engine.CreateContainerAsync(name, ContainerSettingsJson.NewBody(settings, joins, "", api >= new Version(1, 48)), ct);
    }

    // A database server picked: its whole dump loaded into it once it runs (its data volume came back empty, or new).
    private async Task<string> PickDumpAsync(PickPlan plan, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        var said = new List<string>();
        foreach (var saved in plan.Containers)
        {
            if (manifest.Databases.FirstOrDefault(d => d.Container == saved.Name) is not { } dump) continue;
            var name = plan.Rename(saved.Name);
            if (await _engine.InspectContainerAsync(name, ct) is not { } live) throw new InvalidOperationException($"{name} isn't here.");
            if (!BackupInventory.Running(live)) await _engine.StartContainerAsync(name, ct);
            var server = (BackupInventory.DatabasePlanOf(live, saved.Image, []) ?? throw new InvalidOperationException($"{name} isn't a database Gitberth can load.")) with { Container = name };
            await WaitReadyAsync(server, ct);
            await using var file = OpenUpload();
            using var reader = await BackupReader.OpenAsync(file, key, ct);
            while (await reader.NextPartAsync(ct) is { } part)
            {
                if (part.Kind != BackupPartKind.Dump || part.Name != dump.Part) continue;
                var r = await _engine.ExecAsync(name, LoadCommand(server), BackupInventory.DumpEnv(server), part.Content, Stream.Null, ct);
                if (!r.Ok) throw new InvalidOperationException($"Loading {name}'s dump failed (exit code {r.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
                said.Add($"{name}: its dump loaded.");
                break;
            }
        }
        return said.Count == 0 ? Skip("It isn't a database server.") : string.Join(" ", said);
    }

    // A Git app built at the commit it ran, a Compose app brought up at its release; waited for.
    private async Task<string> PickDeployAsync(PickPlan plan, BackupKey key, CancellationToken ct)
    {
        if (plan.Saved?.App is not { Deployed: true } app || plan.AppId is not { } appId) return Skip("Gitberth doesn't deploy it.");
        // [builder B, B6] An uploaded app's kept uploads come with it (under its ID here), stopped or not, so a deploy here
        // builds its saved upload.
        var uploads = "";
        if (app.FromUpload)
        {
            await using var file = OpenUpload();
            using var reader = await BackupReader.OpenAsync(file, key, ct);
            while (await reader.NextPartAsync(ct) is { } part)
                if (part.Kind == BackupPartKind.DataFolder && part.Name == "data/uploads")
                {
                    uploads = await UploadFilesAsync(app, appId, part.Content, ct) + " ";
                    break;
                }
        }
        if (app.DesiredState != DesiredState.Running) return Skip(uploads + "It was stopped: it's deployed when you start it.");
        if (_actions is null) return Skip("Nothing here deploys it.");
        AppDefinition row;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            row = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == appId, ct);
        var deployment = row.FromRepo
            ? await _actions.DeployAsync(row.Id, DeployTrigger.Start, ct, gitRef: row.CurrentCommit is null ? null : DeployPipeline.OwnBranchRef(row), commit: row.CurrentCommit)
            : await _actions.RecreateAsync(row.Id, DeployTrigger.Start, ct: ct);
        Log.Line($"{plan.Name}: deploy #{deployment.ToString(CultureInfo.InvariantCulture)} queued.");
        while (true)
        {
            await Task.Delay(DeployPoll, ct);
            Deployment? dep;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
                dep = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deployment, ct);
            if (dep is null) throw new InvalidOperationException("Its deploy is gone.");
            switch (dep.Status)
            {
                case DeployStatus.Succeeded:
                    return dep.CommitSha is { Length: >= 7 } sha ? $"Deployed at {sha[..7]}." : "Deployed.";
                case DeployStatus.Failed or DeployStatus.RolledBack or DeployStatus.Cancelled or DeployStatus.Skipped:
                    throw new InvalidOperationException($"Its deploy #{deployment.ToString(CultureInfo.InvariantCulture)} ended {dep.Status.ToString().ToLowerInvariant()}"
                        + (dep.Error is { Length: > 0 } error ? ": " + error : "."));
            }
        }
    }

    /// <summary>How often a deploy is looked at until it ends.</summary>
    internal TimeSpan DeployPoll { get; init; } = TimeSpan.FromSeconds(2);
}
