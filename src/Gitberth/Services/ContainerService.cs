using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Services;

/// <summary>What a container is to Gitberth, which decides what it may do with it and what to warn about first.</summary>
public enum ContainerRole
{
    /// <summary>A Gitberth app's own container: its actions go through the app, so the app's state follows.</summary>
    App,
    /// <summary>A deploy's temporary container, or the previous one kept aside: Gitberth tidies these itself.</summary>
    AppInternal,
    /// <summary>Gitberth's own container: it can restart itself, but not stop.</summary>
    Self,
    /// <summary>A helper Gitberth runs for a moment, such as a host-port scan: no list, count or chart shows it.</summary>
    Helper,
    /// <summary>cloudflared, which carries the domains.</summary>
    Tunnel,
    /// <summary>
    /// A port forwarder an older Gitberth started. It's listed like any other container, and the page of the container
    /// it publishes names it, so it can be removed there.
    /// </summary>
    LegacyForwarder,
    /// <summary>A database, cache or queue that others depend on.</summary>
    Database,
    Other,
}

public enum ContainerAction { Start, Stop, Restart, Remove }

/// <param name="Title">The app's name, the title given to the container on the Health page, or its own name.</param>
/// <param name="MemoryBytes">The latest reading, while the Health page gathers stats for it.</param>
public sealed partial record ContainerRow(ContainerSummary Container, string Title, ContainerRole Role, NavApp? App, ulong? MemoryBytes)
{
    public bool Running => Container.State == "running";

    public bool NeedsAttention => ContainerService.NeedsAttention(Container);
}

public sealed record ContainerList(IReadOnlyList<ContainerRow> Rows, string? Error)
{
    public static readonly ContainerList Empty = new([], null);
}

/// <summary>
/// Every container on the host, whoever started it, and what Gitberth can do with each: start, stop, restart and remove.
/// An app's own container goes through the app's own actions (<see cref="AppActions"/>, which come back here for any app
/// that isn't a Git app); Gitberth's own container and a deploy's temporary ones are left to Gitberth. Any other
/// container's actions hold its lock (<see cref="ContainerLocks"/>), so none runs in the middle of a change, and neither
/// container of a change that isn't settled starts next to the other. Pages list apps through AppCatalog; this list is
/// for who uses a container and what to warn about.
/// </summary>
/// <param name="changer">The port changes, which say when a container mustn't start; it asks this service who uses a container, so it comes when it's first needed.</param>
/// <param name="network">Forgets the networks Gitberth kept a container on when Gitberth removes it.</param>
public sealed partial class ContainerService(
    DockerCli docker,
    AppService apps,
    AppActions actions,
    ContainerTitles titles,
    ContainerStatsCollector stats,
    GitberthOptions options,
    IDbContextFactory<GitberthDb>? dbFactory = null,
    ContainerLocks? locks = null,
    Lazy<PortChanger>? changer = null,
    NetworkService? network = null) : IContainerUsers
{
    private readonly ContainerLocks _locks = locks ?? new ContainerLocks();

    /// <summary>
    /// On a port forwarder an older Gitberth started: the container and port it publishes, as "postgres:5432". Gitberth
    /// starts none any more; the label tells the ones left behind (<see cref="ContainerRole.LegacyForwarder"/>).
    /// </summary>
    public const string PublishesLabel = "gitberth.publishes";

    // The images whose containers others rely on for data: stopping one takes it away from them. By the repository's last
    // segment, with the names other publishers give the same servers (bitnami/postgresql, mysql/mysql-server,
    // tensorchord/pgvecto-rs, ghcr.io/cloudnative-pg/postgresql, bitnami/mongodb…).
    private static readonly HashSet<string> Databases = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres", "postgresql", "postgresql-repmgr", "postgresql-ha", "postgis", "timescaledb", "timescaledb-ha", "pgvector", "pgvecto-rs",
        "mysql", "mysql-server", "mariadb", "mariadb-galera", "percona", "percona-server", "percona-server-mongodb", "percona-xtradb-cluster",
        "mongo", "mongodb", "mongodb-community-server", "mongodb-enterprise-server", "redis", "redis-stack", "redis-stack-server", "redis-cluster",
        "valkey", "valkey-cluster", "keydb", "dragonfly", "memcached", "couchdb", "couchbase", "cassandra", "scylla", "clickhouse", "clickhouse-server",
        "influxdb", "questdb", "neo4j", "arangodb", "elasticsearch", "opensearch", "rabbitmq", "nats", "mssql-server", "surrealdb", "minio",
        "cockroach", "yugabyte", "etcd",
    };

    // The ports a database, cache or queue says it listens on: PostgreSQL, MySQL (and its X protocol), MongoDB, Redis,
    // RabbitMQ, Elasticsearch, Cassandra, InfluxDB and SQL Server.
    private static readonly HashSet<int> DatabasePorts = [5432, 3306, 33060, 27017, 6379, 5672, 9200, 9042, 8086, 1433];

    // Where they keep their data, as an image's VOLUME or PGDATA names it.
    private static readonly string[] DataPaths = ["/var/lib/postgresql", "/var/lib/mysql", "/var/lib/mariadb", "/var/lib/mongodb", "/data/db", "/bitnami"];

    public async Task<ContainerList> ListAsync(CancellationToken ct = default)
    {
        var (containers, error) = await docker.ListContainersAsync(managedOnly: false, ct);
        var appsBySlug = (await apps.ListLabelledAsync(ct)).ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var memory = stats.Current.Containers
            .Where(c => c.MemoryBytes is not null)
            .ToDictionary(c => c.Name, c => c.MemoryBytes, StringComparer.Ordinal);
        var self = options.OwnContainer;
        var rows = containers.Select(c =>
            {
                var role = RoleOf(c, appsBySlug, self);
                var app = c.Labels.TryGetValue("gitberth.app", out var slug) && appsBySlug.TryGetValue(slug, out var a) ? a : null;
                var title = role == ContainerRole.App && app is not null ? app.Name : titles.For(c.Name) ?? c.Name;
                return new ContainerRow(c, title, role, app, memory.GetValueOrDefault(c.Name));
            })
            // Gitberth's own short-lived helpers (host-port scans, port probes) aren't anyone's containers.
            .Where(r => r.Role != ContainerRole.Helper)
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ContainerList(rows, error);
    }

    /// <summary>The container by name, with what Gitberth knows about it; null when there's no such container.</summary>
    public async Task<ContainerRow?> FindAsync(string name, CancellationToken ct = default) =>
        (await ListAsync(ct)).Rows.FirstOrDefault(r => r.Container.Name == name);

    public async Task<ContainerDetails?> InspectAsync(string name, CancellationToken ct = default) =>
        (await docker.InspectContainersAsync([name], ct)).FirstOrDefault();

    /// <summary>
    /// Does it; null when it worked, else what went wrong. An app's own container goes through the app. Any other
    /// container's action waits for nothing: while Gitberth is changing it, it's refused, and so is starting either
    /// container of a port change that isn't settled. A stop gets as long as the container asks for (60 s for a database
    /// that doesn't say).
    /// </summary>
    public async Task<string?> RunAsync(ContainerRow row, ContainerAction action, CancellationToken ct = default)
    {
        if (Refusal(row, action) is { } refusal) return refusal;
        // An app's own container, whatever its source: the app's actions keep its wanted state, and come back here for any
        // app that isn't a Git app.
        if (row is { Role: ContainerRole.App, App: { } app })
            return action switch
            {
                ContainerAction.Start => await actions.StartAsync(app.Id),
                ContainerAction.Stop => await actions.StopAsync(app.Id),
                _ => await actions.RestartAsync(app.Id),
            };
        return await RunContainerAsync(row.Container.Name, row.Role == ContainerRole.Database, action, ct);
    }

    /// <summary>
    /// Starts, stops, restarts or removes a container Gitberth doesn't deploy, under its lock: any container that isn't a
    /// Git app's, an app's from a registry, a local image or by hand included. While Gitberth is changing it, it's refused,
    /// and so is starting either container of a change that isn't settled. A stop gets as long as the container asks for
    /// (60 s for a database that doesn't say). Null when it worked, else what went wrong.
    /// </summary>
    /// <param name="database">It's a database Gitberth recognizes, whose old copy mustn't start next to it.</param>
    internal async Task<string?> RunContainerAsync(string name, bool database, ContainerAction action, CancellationToken ct = default)
    {
        using var held = await _locks.TryAcquireAsync(name);
        if (held is null) return $"Gitberth is already changing {name}. Try again when that's done.";
        if (changer is not null && await (action is ContainerAction.Start or ContainerAction.Restart
                ? changer.Value.WhyNotStartAsync(name, ct)
                : changer.Value.BusyWithAsync(name, ct)) is { } why)
            return why;
        // An old copy of a database next to the running one would write the same files.
        if (action is ContainerAction.Start or ContainerAction.Restart && database && await SharedDataAsync(name, ct) is { } shared)
            return shared;
        bool Database(string image) => database || IsDatabaseImage(image);
        var r = action switch
        {
            ContainerAction.Start => await docker.StartAsync(name, ct),
            ContainerAction.Stop => await docker.StopStandaloneAsync(name, Database, ct),
            ContainerAction.Restart => await docker.RestartStandaloneAsync(name, Database, ct),
            // The container goes, its volumes stay.
            _ => await docker.RemoveAsync(name, ct),
        };
        // What Gitberth kept for a removed container goes with it: its networks and port notes. Not while a change of its
        // name isn't settled: those are then the original's, which may get the name back (NetworkService.ForgetAsync).
        if (r.Ok && action == ContainerAction.Remove && network is not null) await network.ForgetAsync(name, ct);
        return r.Ok ? null : r.ErrorText;
    }

    /// <summary>Why Gitberth won't do this to the container, or null when it will.</summary>
    public static string? Refusal(ContainerRow row, ContainerAction action) => (row.Role, action) switch
    {
        (ContainerRole.Self, ContainerAction.Stop or ContainerAction.Remove) =>
            "Gitberth can't stop or remove its own container. Use docker compose on the host for that.",
        (ContainerRole.AppInternal, _) => "This is a deploy's temporary or previous container, which Gitberth removes itself.",
        (ContainerRole.App, ContainerAction.Remove) => "Delete the app on its page instead: that removes its container too.",
        _ => null,
    };

    /// <summary>What stopping, restarting or removing it would do to everything else, for the confirmation.</summary>
    public async Task<List<string>> WarningsAsync(ContainerRow row, ContainerAction action, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (action == ContainerAction.Start) return warnings;
        var back = action == ContainerAction.Restart ? "until it's back, a few seconds" : "until it runs again";
        if (row.Role == ContainerRole.Self)
            warnings.Add("Gitberth restarts: this page reconnects in a few seconds, and a deploy that's running is tried again.");
        if (row.Role == ContainerRole.Tunnel)
            warnings.Add($"Every domain it serves stops answering {back}.");

        // Who else relies on it: containers whose settings name it as a host, like a DATABASE_URL.
        var (users, _, target) = await ReadUsersAsync(row.Container.Name, [], ct);
        if (users.Count > 0)
            warnings.Add($"{Join(users)} {(users.Count == 1 ? "uses" : "use")} it, and {(users.Count == 1 ? "loses" : "lose")} it {back}.");
        else if (row.Role == ContainerRole.Database)
            warnings.Add($"Anything that uses this database loses it {back}.");

        if (action == ContainerAction.Remove && target is not null)
        {
            var volumes = target.Mounts.Where(m => m.Name is not null).Select(m => m.Name!).ToList();
            warnings.Add(volumes.Count > 0
                ? $"Its {(volumes.Count == 1 ? "volume stays" : "volumes stay")}: {Join(volumes)}. Only the container goes, so the data is still there for a new one."
                : "Only the container goes. Anything it kept inside itself rather than in a volume goes with it.");
        }
        return warnings;
    }

    /// <summary>
    /// For a port change's confirmation: the running containers whose settings name the container as a host, the ones
    /// whose settings name one of its <paramref name="addresses"/> (which a recreate can change), and the apps building
    /// right now. A build reaches the containers on the shared network by the addresses they had when it began, so one
    /// building while the container is recreated can lose it.
    /// </summary>
    public async Task<ContainerUsers> UsersOfAsync(string container, IReadOnlyCollection<string> addresses, CancellationToken ct)
    {
        var (users, pinned, target) = await ReadUsersAsync(container, addresses, ct);
        if (target is null) return ContainerUsers.None;
        // A build is given the address it has on the shared network; only one that can change matters.
        var building = target.Networks.Any(n => n.Name == options.Network && n.IpAddress is { } ip && addresses.Contains(ip))
            ? await BuildingAsync(ct)
            : [];
        return new ContainerUsers(users, building, pinned);
    }

    // The running containers whose environment names this one (or one of its network aliases) as a host, and the ones
    // that name one of the addresses given.
    private async Task<(List<string> Users, List<AddressUser> Pinned, ContainerDetails? Target)> ReadUsersAsync(string name, IReadOnlyCollection<string> addresses,
        CancellationToken ct)
    {
        var list = await ListAsync(ct);
        var names = list.Rows.Where(r => r.Running).Select(r => r.Container.Name).Append(name).Distinct(StringComparer.Ordinal).ToList();
        var details = await docker.InspectContainersAsync(names, ct);
        var target = details.FirstOrDefault(d => d.Name == name);
        if (target is null) return ([], [], null);
        var hosts = HostNamesOf(target);
        var titlesByName = list.Rows.ToDictionary(r => r.Container.Name, r => r.Title, StringComparer.Ordinal);
        string Title(ContainerDetails d) => titlesByName.GetValueOrDefault(d.Name) ?? d.Name;
        var others = details.Where(d => d.Name != name).ToList();
        var users = others
            .Where(d => d.Env.Any(e => hosts.Any(h => NamesHost(e.Value, h))))
            .Select(Title)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pinned = others
            .SelectMany(d => addresses.Where(a => d.Env.Any(e => NamesHost(e.Value, a))).Select(a => new AddressUser(Title(d), a)))
            .Distinct()
            .OrderBy(p => p.Who, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (users, pinned, target);
    }

    // The apps whose deploy is building now, by name.
    private async Task<List<string>> BuildingAsync(CancellationToken ct)
    {
        if (dbFactory is null) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Deployments.AsNoTracking().Where(d => d.Status == DeployStatus.Building).Select(d => d.AppId).Distinct().ToListAsync(ct);
        return await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Name).OrderBy(n => n).ToListAsync(ct);
    }

    // Inspects the running containers and the one to start, for SharedData. A running one starts nothing new.
    private async Task<string?> SharedDataAsync(string name, CancellationToken ct)
    {
        var names = (await ListAsync(ct)).Rows.Where(r => r.Running).Select(r => r.Container.Name).Append(name).Distinct(StringComparer.Ordinal).ToList();
        var details = await docker.InspectContainersAsync(names, ct);
        return details.FirstOrDefault(d => d.Name == name) is { Running: false } target
            ? SharedData(name, target.Mounts, details.Where(d => d.Running && d.Name != name).Select(d => (d.Name, d.Image, d.Mounts)))
            : null;
    }

    /// <summary>
    /// Why a stopped database mustn't start: another running database mounts one of its volumes writable, and two servers
    /// writing the same data files at once can damage them. The usual case is an old copy kept after the container was
    /// made again. A container that mounts the volume read-only (a backup), or isn't a database, doesn't count.
    /// </summary>
    /// <param name="others">The other running containers.</param>
    internal static string? SharedData(string name, IEnumerable<ContainerMount> mounts, IEnumerable<(string Name, string Image, IReadOnlyList<ContainerMount> Mounts)> others)
    {
        var volumes = mounts.Where(m => m.Type == "volume" && m.Name is { Length: > 0 }).Select(m => m.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var other in others.Where(o => o.Name != name && IsDatabaseImage(o.Image)))
            if (other.Mounts.FirstOrDefault(m => m.Type == "volume" && !m.ReadOnly && m.Name is { } v && volumes.Contains(v)) is { Name: { } volume })
                return $"{other.Name} is running with the same volume, {volume}. Starting {name} as well would put two database servers on the same files, which can damage them. Stop {other.Name} first, or remove {name} if it's an old copy.";
        return null;
    }

    /// <summary>The names other containers reach it by: its own, and its aliases on each network (not the short ID Docker adds).</summary>
    internal static List<string> HostNamesOf(ContainerDetails d) =>
        d.Networks.SelectMany(n => n.Aliases).Append(d.Name)
            .Where(a => a.Length > 0 && !(a.Length == 12 && a.All(char.IsAsciiHexDigitLower)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether a setting's value names the host, as in postgres://u:p@postgres:5432/db or REDIS_HOST=redis.</summary>
    internal static bool NamesHost(string value, string host) =>
        value.Contains(host, StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(value, $@"(?:^|[@/=,;\s]){Regex.Escape(host)}(?=$|[:/;,?\s])", RegexOptions.IgnoreCase);

    /// <param name="self">Gitberth's own container (<see cref="GitberthOptions.OwnContainer"/>): its ID, or its hostname, which is its short ID unless set otherwise.</param>
    internal static ContainerRole RoleOf(ContainerSummary c, IReadOnlyDictionary<string, NavApp> appsBySlug, string? self)
    {
        if (self is not null && (c.Name == self || (c.Id.Length > 0 && c.Id.StartsWith(self, StringComparison.Ordinal)))) return ContainerRole.Self;
        if (HostScanHelper.IsHelper(c.Labels)) return ContainerRole.Helper;
        if (c.Labels.TryGetValue("gitberth.app", out var slug) && appsBySlug.ContainsKey(slug))
        {
            if (c.Name == AppDefinition.ContainerNameFor(slug)) return ContainerRole.App;
            // Under another name: a Git app's is its deploy's (as anything with the bridge label is); an app made from an
            // image, renamed outside Gitberth, is a container like any other.
            if (!RecreateRules.MadeFromImage(c.Labels) || c.Labels.ContainsKey(ContainerSpecFactory.BridgeLabel)) return ContainerRole.AppInternal;
        }
        if (c.Labels.ContainsKey(PublishesLabel)) return ContainerRole.LegacyForwarder;
        if (Repository(c.Image).Equals("cloudflared", StringComparison.OrdinalIgnoreCase) || c.Command.EndsWith("cloudflared", StringComparison.Ordinal))
            return ContainerRole.Tunnel;
        return IsDatabaseImage(c.Image) ? ContainerRole.Database : ContainerRole.Other;
    }

    /// <summary>
    /// Whether the image is a database, cache or queue Gitberth recognizes by its name: postgres:18, redis:7-alpine,
    /// bitnami/postgresql:16, postgres@sha256:…. An image named by its ID alone isn't known by name: <see cref="IsDatabase"/>
    /// reads what it declares too.
    /// </summary>
    public static bool IsDatabaseImage(string image) => Databases.Contains(Repository(image));

    /// <summary>
    /// The one answer to "is it a database?", for its role, its stop timeout, the shared-data guards, the plans and
    /// automatic updates: by its name (<see cref="IsDatabaseImage"/>, the tag Gitberth tracks for it included), or by what its
    /// image declares: a database's port (5432, 3306, 27017, 6379…) or data folder (/var/lib/postgresql, /var/lib/mysql,
    /// /data/db, /bitnami/…, or a PGDATA). In doubt, it's a database: that only makes Gitberth more careful with it.
    /// </summary>
    /// <param name="image">Config.Image, or any name it goes by.</param>
    /// <param name="config">The image's Config, or the container's (which holds the image's ports, volumes and variables too); null when unknown.</param>
    /// <param name="tracked">The tag the app's row follows, when it has one: a container rolled back to an image ID is still postgres.</param>
    public static bool IsDatabase(string image, System.Text.Json.Nodes.JsonNode? config, string? tracked = null)
    {
        if (IsDatabaseImage(image) || tracked is { Length: > 0 } && IsDatabaseImage(tracked)) return true;
        if (config is not System.Text.Json.Nodes.JsonObject c) return false;
        if (c["ExposedPorts"] is System.Text.Json.Nodes.JsonObject exposed
            && exposed.Any(p => int.TryParse(p.Key.Split('/')[0], out var port) && DatabasePorts.Contains(port)))
            return true;
        var paths = (c["Volumes"] as System.Text.Json.Nodes.JsonObject ?? []).Select(v => v.Key)
            .Concat((ContainerSettingsJson.StrList(c["Env"]) ?? []).Where(e => e.StartsWith("PGDATA=", StringComparison.Ordinal)).Select(e => e["PGDATA=".Length..]));
        return paths.Select(CloneSpec.Clean).Any(p => DataPaths.Any(d => p == d || p.StartsWith(d + "/", StringComparison.Ordinal)));
    }

    /// <summary>"postgres" from "postgres:18", "ghcr.io/x/postgres:18" or "postgres@sha256:…".</summary>
    internal static string Repository(string image)
    {
        var name = image.Split('@')[0];
        name = name[(name.LastIndexOf('/') + 1)..];
        var colon = name.IndexOf(':');
        return colon < 0 ? name : name[..colon];
    }

    /// <summary>Restarting, unhealthy, or stopped by an error: worth a look, whoever started it.</summary>
    public static bool NeedsAttention(ContainerSummary c) =>
        c.State is "restarting" or "dead"
        || c.Status.Contains("(unhealthy)", StringComparison.Ordinal)
        || (c.State == "exited" && ExitCode(c.Status) is { } code && code is not (0 or 137 or 143));

    /// <summary>"Exited (1) 3 minutes ago" gives 1.</summary>
    public static int? ExitCode(string status) =>
        ExitCodePattern().Match(status) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var code) ? code : null;

    /// <summary>The pill for a container's state: (tone, label).</summary>
    public static (string Tone, string Label) Pill(ContainerSummary c) => c.State switch
    {
        "restarting" => ("warning", "Restarting"),
        "paused" => ("secondary", "Paused"),
        "created" => ("secondary", "Not started"),
        "dead" => ("danger", "Dead"),
        "exited" when ExitCode(c.Status) is { } code and not (0 or 137 or 143) => ("danger", $"Exited ({code})"),
        "exited" => ("secondary", "Stopped"),
        _ when c.Status.Contains("(unhealthy)", StringComparison.Ordinal) => ("danger", "Unhealthy"),
        _ when c.Status.Contains("(health: starting)", StringComparison.Ordinal) => ("info", "Starting"),
        _ when c.Status.Contains("(healthy)", StringComparison.Ordinal) => ("success", "Healthy"),
        _ => ("success", "Running"),
    };

    /// <summary>Everything it listens on: what its image exposes and what it publishes, each port once.</summary>
    public static List<ExposedPort> ListeningPorts(ContainerDetails d) =>
        d.Exposed.Concat(d.Ports.Select(p => new ExposedPort(p.ContainerPort, p.Protocol)))
            .Where(p => p.Port > 0)
            .Distinct()
            .OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal)
            .ToList();

    /// <summary>The networks others reach it on by name. Docker's default bridge network, host and none have no names.</summary>
    public static List<ContainerNetwork> NamedNetworks(ContainerDetails d) =>
        d.Networks.Where(n => n.Name is not ("bridge" or "host" or "none")).ToList();

    /// <summary>The one rule's mask (<see cref="Masking.Mask"/>), by its older name.</summary>
    public const string Masked = Masking.Mask;

    /// <summary>A setting's value as the page shows it: the one rule's (<see cref="Masking.Value"/>), by its older name.</summary>
    public static string MaskValue(string key, string value) => Masking.Value(key, value);

    /// <summary>A command line with its secrets hidden: the one rule's (<see cref="Masking.Args"/>), by its older name.</summary>
    public static IReadOnlyList<string> MaskArgs(IReadOnlyList<string> args) => Masking.Args(args);

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    [GeneratedRegex(@"^Exited \((\d+)\)")]
    private static partial Regex ExitCodePattern();
}
