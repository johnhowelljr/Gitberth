using System.Globalization;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>A database server that can be dumped: its engine, who dumps it, and the volumes its dump stands in for.</summary>
/// <param name="Password">Its superuser's password from its own settings; null when it has none there.</param>
/// <param name="Excluded">Previews' databases, left out of the dump (previews are made again, never restored).</param>
internal sealed record DatabasePlan(string Container, string Engine, string User, string? Password, IReadOnlyList<string> Excluded,
    IReadOnlyList<string> DataVolumes, bool Running, bool CanDump);

/// <summary>
/// Everything on this host that can be saved, as the save list shows it and the save reads it: Gitberth's data, every app
/// (any kind), the containers that aren't apps, volumes, database servers and the images only this host has. Never
/// Gitberth itself, its helpers (gitberth-buildkit among them), previews, a deploy's temporary containers, nor Swarm or
/// Kubernetes containers.
/// </summary>
internal sealed class BackupInventory
{
    public const string GitberthItem = "gitberth";

    public List<BackupItem> Items { get; } = [];

    /// <summary>Every container saved with its item, by name (a Git or Compose app's own are deployed again, not created).</summary>
    public Dictionary<string, SavedContainer> Containers { get; } = new(StringComparer.Ordinal);

    /// <summary>Every volume that can be saved, by name, with its item ("volume:x").</summary>
    public Dictionary<string, (SavedVolume Volume, string Item)> Volumes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, DatabasePlan> Databases { get; } = new(StringComparer.Ordinal);

    /// <summary>The images the created containers run, by ID; the ones only this host has are items ("image:&lt;id&gt;").</summary>
    public Dictionary<string, (SavedImage Image, string? Item)> Images { get; } = new(StringComparer.Ordinal);

    public List<SavedApp> Apps { get; } = [];

    /// <summary>The networks the saved containers are on, as Docker lists them.</summary>
    public List<SavedNetwork> Networks { get; } = [];

    /// <summary>Every container's state now, by name (running, paused…), the ones not saved too.</summary>
    public Dictionary<string, JsonObject> Inspects { get; } = new(StringComparer.Ordinal);

    public string? SelfName { get; private set; }

    /// <summary>Reads the host and Gitberth's rows. <paramref name="sizes"/> is each volume's size when known.</summary>
    /// <param name="registryDigest">Whether a registry has a reference, and its digest (null: none has it); asked only of images nothing else places.</param>
    public static async Task<BackupInventory> ReadAsync(IBackupEngine engine, IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options,
        IReadOnlyDictionary<string, long> sizes, long? dataBytes, CancellationToken ct, Func<string, CancellationToken, Task<string?>>? registryDigest = null)
    {
        registryDigest ??= engine.RegistryDigestAsync;
        var inv = new BackupInventory();
        List<AppDefinition> rows;
        List<(string Server, string Name)> previewDatabases;
        Dictionary<string, ImageCheck> checks;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.Apps.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            var previewIds = rows.Where(a => a.IsPreview).Select(a => a.Id).ToList();
            previewDatabases = (await db.ManagedDatabases.AsNoTracking()
                    .Where(m => m.Kind == DatabaseKind.Postgres && previewIds.Contains(m.AppId)).Select(m => new { m.Server, m.Name }).ToListAsync(ct))
                .Select(m => (m.Server, m.Name)).ToList();
            checks = (await db.ImageChecks.AsNoTracking().ToListAsync(ct)).GroupBy(c => c.Reference, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        var apps = rows.Where(a => !a.IsPreview).ToList();
        var previewSlugs = rows.Where(a => a.IsPreview).Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
        var bySlug = apps.GroupBy(a => a.Slug, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byContainer = apps.GroupBy(a => a.ContainerName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byProject = apps.Where(a => a.IsCompose).GroupBy(a => a.ComposeProject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var listed in await engine.ListAllAsync(ct))
            if (await engine.InspectContainerAsync(listed.Id, ct) is { } inspect)
                inv.Inspects[Name(inspect)] = inspect;

        var self = options.OwnContainer;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var appItems = new Dictionary<int, string>();
        var children = new Dictionary<string, List<BackupItem>>(StringComparer.Ordinal);
        void Child(string parent, BackupItem item)
        {
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(item);
        }

        // ---- Apps: their rows come with Gitberth's data; Git and Compose apps are deployed again after the restart.
        foreach (var app in apps)
        {
            var id = "app:" + app.Id.ToString(CultureInfo.InvariantCulture);
            appItems[app.Id] = id;
            if (app.Deployed)
                inv.Apps.Add(new SavedApp(app.Id, app.Name, app.Slug, app.Source.ToString(), app.IsCompose, app.FromRepo, app.Branch, app.CurrentCommit,
                    app.DesiredState == DesiredState.Running));
        }

        // ---- Containers, each with the item it belongs to.
        var topContainers = new List<BackupItem>();
        foreach (var (name, inspect) in inv.Inspects)
        {
            var labels = Labels(inspect);
            if (self is not null && (name == self || Str(inspect, "Id").StartsWith(self, StringComparison.Ordinal)))
            {
                inv.SelfName = name;
                excluded.Add(name);
                continue;
            }
            if (labels.ContainsKey(HostScanHelper.Label) || labels.ContainsKey(Services.ContainerService.PublishesLabel)
                || labels.Keys.Any(k => k.StartsWith("com.docker.swarm.", StringComparison.Ordinal) || k.StartsWith("io.kubernetes.", StringComparison.Ordinal))
                || labels.TryGetValue("gitberth.app", out var labelled) && previewSlugs.Contains(labelled))
            {
                excluded.Add(name);
                continue;
            }

            AppDefinition? owner = null;
            var how = ComesBack.Inspect;
            if (DatabaseNames.IsMember(labels) && bySlug.TryGetValue(labels["gitberth.app"], out var keeper))
                owner = keeper;
            else if (byContainer.TryGetValue(name, out var row))
            {
                owner = row;
                if (row.Deployed) how = ComesBack.Deploy;
            }
            else if (labels.TryGetValue(NetworkService.ComposeProjectLabel, out var project) && byProject.TryGetValue(project, out var compose))
            {
                owner = compose;
                how = ComesBack.Deploy;
            }
            else if (labels.TryGetValue("gitberth.app", out var slug) && bySlug.TryGetValue(slug, out var gitApp) && gitApp.Deployed)
            {
                // A deploy's own (the previous container kept aside, a bridge): Gitberth tidies these itself.
                excluded.Add(name);
                continue;
            }

            var image = await engine.InspectImageAsync(Str(inspect, "Image"), ct);
            string item;
            if (owner is not null)
            {
                item = appItems[owner.Id];
                // An app's Redis is listed under its app; the app's own container is the app's row.
                if (owner.ContainerName != name && how == ComesBack.Inspect)
                    Child(item, new BackupItem("container:" + name, BackupItemKind.Container, name, $"{ImageName(inspect, image)} · {owner.Name}'s", null, false, true, item));
            }
            else
            {
                item = "container:" + name;
                topContainers.Add(new BackupItem(item, BackupItemKind.Container, name, ImageName(inspect, image) + (Running(inspect) ? "" : " · stopped"), null, false, true, null));
            }
            inv.Containers[name] = new SavedContainer { Name = name, How = how, Inspect = inspect, Image = image, Running = Running(inspect), Item = item };

            // A database server: dumped while it serves, its data volumes then not copied.
            if (how != ComesBack.Deploy && DatabasePlanOf(inspect, image, previewDatabases) is { } plan)
                inv.Databases[name] = plan;
        }

        // ---- Volumes: under the item of the first saved container using them; a volume nothing uses is a row of its own.
        var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, inspect) in inv.Inspects)
            foreach (var m in (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Str(m, "Type") == "volume" && Str(m, "Name").Length > 0))
            {
                if (!users.TryGetValue(Str(m, "Name"), out var list)) users[Str(m, "Name")] = list = [];
                if (!list.Contains(name)) list.Add(name);
            }
        var orphans = new List<BackupItem>();
        foreach (var volume in (await engine.ListVolumesAsync(ct)).OfType<JsonObject>().OrderBy(v => Str(v, "Name"), StringComparer.Ordinal))
        {
            var name = Str(volume, "Name");
            if (name.Length == 0) continue;
            var usedBy = users.GetValueOrDefault(name) ?? [];
            // Gitberth's own data volume (and anything else its container has) is Gitberth's data, saved as its files.
            if (inv.SelfName is { } own && usedBy.Contains(own)) continue;
            var saved = usedBy.Where(inv.Containers.ContainsKey).ToList();
            if (usedBy.Count > 0 && saved.Count == 0) continue;
            if (saved.Count == 0 && (name.StartsWith(RailpackBuildKitPrefix, StringComparison.Ordinal)
                || previewSlugs.Any(p => name.StartsWith($"gb-{p}-", StringComparison.Ordinal) || name.StartsWith($"gb-{p}_", StringComparison.Ordinal))))
                continue;
            var bytes = sizes.TryGetValue(name, out var size) ? size : (long?)null;
            var record = new SavedVolume
            {
                Name = name, Driver = Str(volume, "Driver") is { Length: > 0 } driver ? driver : "local",
                Options = Map(volume["Options"]), Labels = Map(volume["Labels"]), Users = saved, Bytes = bytes,
            };
            var dumpedBy = inv.Databases.Values.FirstOrDefault(d => d.CanDump && d.DataVolumes.Contains(name));
            if (dumpedBy is not null)
            {
                // Listed as the database's: its dump stands in for its files.
                inv.Volumes[name] = (record, "database:" + dumpedBy.Container);
                continue;
            }
            var item = "volume:" + name;
            inv.Volumes[name] = (record, item);
            var detail = saved.Count > 0 ? "used by " + string.Join(", ", saved) : "used by no container";
            if (saved.Count > 0) Child(inv.Containers[saved[0]].Item, new BackupItem(item, BackupItemKind.Volume, name, detail, bytes, false, true, inv.Containers[saved[0]].Item));
            else orphans.Add(new BackupItem(item, BackupItemKind.Volume, name, detail, bytes, false, true, null));
        }

        // ---- Database servers: one row each, under their app or container.
        foreach (var plan in inv.Databases.Values)
        {
            var owner = inv.Containers[plan.Container].Item;
            long? bytes = plan.DataVolumes.Select(v => sizes.TryGetValue(v, out var s) ? s : (long?)null).Aggregate((long?)0, (a, b) => a is null || b is null ? null : a + b);
            var engineName = plan.Engine == "postgres" ? "Postgres" : "MySQL";
            var detail = plan.CanDump ? $"{engineName} · dumped while it serves" : plan.Running ? $"{engineName} · its files, paused for the copy" : $"{engineName} · stopped: its files are saved";
            Child(owner, new BackupItem("database:" + plan.Container, BackupItemKind.Database, plan.Container, detail, plan.DataVolumes.Count == 0 ? null : bytes, false, true, owner));
        }

        // ---- Images only this host has, for the containers created from their inspect.
        var selfImage = inv.SelfName is { } me ? Str(inv.Inspects[me], "Image") : null;
        foreach (var c in inv.Containers.Values.Where(c => c.How != ComesBack.Deploy))
        {
            var id = Str(c.Inspect, "Image");
            if (id.Length == 0 || id == selfImage || inv.Images.ContainsKey(id) || c.Image is null) continue;
            var tags = StrList(c.Image["RepoTags"]).Where(t => !t.EndsWith(":<none>", StringComparison.Ordinal)).ToList();
            var digests = StrList(c.Image["RepoDigests"]);
            var reference = ContainerReference(c.Inspect, tags);
            var row = byContainer.GetValueOrDefault(c.Name);
            var origin = Origin(c.Image, reference, row, checks);
            string? remote = null;
            // Nothing says where it comes from: a registry that has its reference pulls it again; else it's saved whole.
            if (origin is null) origin = ImageReference.Parse(reference) is not null && (remote = await registryDigest(reference, ct)) is not null;
            var onlyHere = origin == false;
            var saved = new SavedImage
            {
                Id = id, Tags = tags, Bytes = c.Image["Size"] is JsonValue s && s.TryGetValue<long>(out var size) ? size : null,
                Digest = onlyHere ? null : DigestFor(reference, digests, remote),
            };
            string? item = null;
            if (onlyHere)
            {
                item = "image:" + id;
                Child(c.Item, new BackupItem(item, BackupItemKind.Image, tags.FirstOrDefault() ?? Short(id), "only on this host", saved.Bytes, false, true, c.Item));
            }
            inv.Images[id] = (saved, item);
        }

        // ---- The networks they're on (not Docker's own, not gitberth: made at start; gitberth-lan is the old machine's LAN).
        var wanted = inv.Containers.Values
            .SelectMany(c => (c.Inspect["NetworkSettings"]?["Networks"] as JsonObject ?? []).Select(n => n.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var network in (await engine.ListNetworksJsonAsync(ct)).OfType<JsonObject>())
        {
            var name = Str(network, "Name");
            if (!wanted.Contains(name) || name is "bridge" or "host" or "none" || name == options.Network) continue;
            inv.Networks.Add(new SavedNetwork { Name = name, Inspect = network });
        }

        // ---- The list: Gitberth's data, then apps, containers and volumes nothing uses, each followed by its children.
        inv.Items.Add(new BackupItem(GitberthItem, BackupItemKind.GitberthData, "Gitberth's data",
            "Settings, apps, secrets, keys, Health, uptime and audit history, Compose releases", dataBytes, true, true, null));
        foreach (var app in apps)
        {
            var id = appItems[app.Id];
            inv.Items.Add(new BackupItem(id, BackupItemKind.App, app.Name, AppDetail(app), null, false, true, null));
            inv.Items.AddRange(children.GetValueOrDefault(id) ?? []);
        }
        foreach (var container in topContainers.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
        {
            inv.Items.Add(container);
            inv.Items.AddRange(children.GetValueOrDefault(container.Id) ?? []);
        }
        inv.Items.AddRange(orphans);
        return inv;
    }

    private const string RailpackBuildKitPrefix = Railpack.RailpackRelease.BuildKitContainer;

    private static string AppDetail(AppDefinition app) => app.Source switch
    {
        AppSource.Git when app.IsCompose => "Compose app · " + app.RepoFullName,
        AppSource.Git => "Git app · " + app.RepoFullName,
        AppSource.Compose => "Compose app",
        AppSource.Registry => "Registry app · " + app.ImageRef,
        AppSource.Local => "Local image app · " + app.ImageRef,
        AppSource.Manual => "App · " + app.ImageRef,
        _ => "Container app · " + app.ContainerName,
    };

    /// <summary>A Postgres or MySQL/MariaDB server, with how to dump it; null for anything else.</summary>
    internal static DatabasePlan? DatabasePlanOf(JsonObject inspect, JsonObject? image, IReadOnlyList<(string Server, string Name)> previewDatabases)
    {
        var name = Name(inspect);
        var reference = ContainerReference(inspect, StrList(image?["RepoTags"]));
        if (ConnectionDetails.EngineOf(reference) is not ({ } engine and (DatabaseEngine.Postgres or DatabaseEngine.MySql))) return null;
        var env = (inspect["Config"]?["Env"] as JsonArray ?? []).Select(e => (string?)e ?? "").Select(e => e.Split('=', 2))
            .GroupBy(kv => kv[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Length > 1 ? g.Last()[1] : "", StringComparer.Ordinal);
        string? Env(params string[] keys) => keys.Select(k => env.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var dataPaths = engine == DatabaseEngine.Postgres
            ? new[] { Env("PGDATA") ?? "", "/var/lib/postgresql/data", "/var/lib/postgresql" }
            : ["/var/lib/mysql", "/var/lib/mariadb"];
        var data = (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(m => Str(m, "Type") == "volume" && Str(m, "Name").Length > 0)
            .Where(m => dataPaths.Any(p => p.Length > 0 && (CloneSpec.Clean(Str(m, "Destination")) == CloneSpec.Clean(p) || CloneSpec.Clean(p).StartsWith(CloneSpec.Clean(Str(m, "Destination")) + "/", StringComparison.Ordinal))))
            .Select(m => Str(m, "Name")).Distinct(StringComparer.Ordinal).ToList();
        var running = Running(inspect);
        if (engine == DatabaseEngine.Postgres)
        {
            var excluded = previewDatabases.Where(d => d.Server == name).Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList();
            return new DatabasePlan(name, "postgres", Env("POSTGRES_USER") ?? "postgres", Env("POSTGRES_PASSWORD"), excluded, data, running, running);
        }
        // MySQL: as root, whose password its settings give (a random one can't be known: its files are saved instead).
        var root = Env("MYSQL_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD");
        var empty = Env("MYSQL_ALLOW_EMPTY_PASSWORD", "MARIADB_ALLOW_EMPTY_ROOT_PASSWORD") is { } yes && yes is "yes" or "1" or "true";
        return new DatabasePlan(name, "mysql", "root", root, [], data, running, running && (root is not null || empty));
    }

    /// <summary>pg_dumpall's or mysqldump's command for a server (its password goes in its environment, never here).</summary>
    internal static IReadOnlyList<string> DumpCommand(DatabasePlan plan) => plan.Engine == "postgres"
        ? ["pg_dumpall", "-U", plan.User, .. plan.Excluded.Select(d => "--exclude-database=" + d)]
        : ["sh", "-c", "if command -v mariadb-dump >/dev/null 2>&1; then exec mariadb-dump \"$@\"; else exec mysqldump \"$@\"; fi", "sh",
            "-u", plan.User, "--all-databases", "--single-transaction", "--routines", "--events"];

    /// <summary>The variables a dump or a load gets: the password, by the client's own variable.</summary>
    internal static Dictionary<string, string>? DumpEnv(DatabasePlan plan) => plan.Password is not { Length: > 0 } password ? null
        : plan.Engine == "postgres" ? new() { ["PGPASSWORD"] = password } : new() { ["MYSQL_PWD"] = password };

    // Where it comes from, as far as Gitberth knows (ImageOrigins: never by its RepoDigests, which the containerd store gives
    // local builds too): true when a registry has it (a check confirmed it), false when only this host has it (a Local
    // app's, Gitberth's builds', the samples', or a check found no registry has it), null when nothing says.
    private static bool? Origin(JsonObject image, string reference, AppDefinition? row, IReadOnlyDictionary<string, ImageCheck> checks)
    {
        if (row?.Source == AppSource.Local) return false;
        if (ImageReference.Parse(reference) is not { } parsed) return false;
        if (ImageOrigins.NeverAsked(parsed, image)) return false;
        var check = checks.GetValueOrDefault(parsed.Canonical);
        if (ImageOrigins.OnlyOnHost(check)) return false;
        return ImageOrigins.Confirmed(check) ? true : null;
    }

    // The digest a pull gets it by: the one this host has for the repository the container names (the very image it ran),
    // else the registry's for its reference now.
    private static string? DigestFor(string reference, IReadOnlyList<string> digests, string? remote)
    {
        if (ImageReference.Parse(reference) is not { } parsed) return null;
        return digests.FirstOrDefault(d => ImageReference.Parse(d) is { } p && p.FromImage == parsed.FromImage)
            ?? (remote is not null ? $"{parsed.Name}@{remote}" : null);
    }

    // The name the container's image goes by: its Config.Image, or (when that's an ID) the image's first tag.
    internal static string ContainerReference(JsonObject inspect, IReadOnlyList<string> tags)
    {
        var configured = Str(inspect["Config"], "Image");
        return ImageReference.IsImageId(configured) || configured.Length == 0 ? tags.FirstOrDefault() ?? configured : configured;
    }

    private static string ImageName(JsonObject inspect, JsonObject? image) => ContainerReference(inspect, StrList(image?["RepoTags"])) is { Length: > 0 } r ? r : "an image";

    private static string Short(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) && id.Length > 19 ? id[..19] : id;

    internal static string Name(JsonObject inspect) => Str(inspect, "Name").TrimStart('/');

    internal static bool Running(JsonObject inspect) => inspect["State"]?["Running"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;

    internal static bool Paused(JsonObject inspect) => inspect["State"]?["Paused"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;

    internal static IReadOnlyDictionary<string, string> Labels(JsonObject inspect) => Map(inspect["Config"]?["Labels"]) ?? new Dictionary<string, string>();

    internal static Dictionary<string, string>? Map(JsonNode? node) =>
        node is JsonObject o && o.Count > 0
            ? o.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal)
            : null;

    internal static List<string> StrList(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();

    internal static string Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
