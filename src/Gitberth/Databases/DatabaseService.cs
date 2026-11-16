using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Databases;

/// <summary>One of an app's databases, as its Databases section lists it.</summary>
/// <param name="Server">Postgres: the server's container; Redis: the app's Redis container.</param>
/// <param name="Name">Postgres: the database; Redis: the name apps reach it by.</param>
/// <param name="Host">Where the app reaches it: the server's name on the shared network, or the Redis's.</param>
/// <param name="SizeBytes">Postgres's pg_database_size, Redis's used_memory; null when it couldn't be read.</param>
/// <param name="Problem">Why it can't be reached now (its server stopped, say); null when it can.</param>
public sealed record DatabaseView(int Id, DatabaseKind Kind, string Server, string Name, string? Role, string Variable, bool Created, DateTime CreatedAt,
    string Host, long? SizeBytes, string? Problem)
{
    /// <summary>Postgres or Redis.</summary>
    public string KindText => DatabaseService.KindText(Kind);
}

/// <summary>What Add a database asks for.</summary>
/// <param name="Server">The Postgres server's container; null for the default one.</param>
public sealed record DatabaseRequest(DatabaseKind Kind, string Variable, string? Server = null);

/// <summary>What adding or removing a database did.</summary>
/// <param name="AppId">The app's row (made just now for a container that had none).</param>
/// <param name="DeploymentId">The deploy that makes the change live, for a Git app.</param>
/// <param name="Job">The change engine's recreate that makes it live, for any other app.</param>
public sealed record DatabaseDone(bool Ok, string Message, int? AppId = null, int? DatabaseId = null, int? DeploymentId = null, PortChangeJob? Job = null);

/// <summary>A variable Gitberth sets for an app: one of its databases' connection strings.</summary>
/// <param name="From">What it comes from, as the Environment tab and the deploy log say it ("the Postgres database shop on postgres").</param>
/// <param name="Password">The password in it, masked on its own too wherever the value could be echoed (a log, a build's output).</param>
public sealed record ProvidedVariable(string Key, string Value, string From, string? Password = null)
{
    /// <summary>What must never show in a log: the value, and the password in it on its own.</summary>
    public IEnumerable<string> Secrets => Password is { Length: > 0 } password ? [Value, password] : [Value];
}

/// <summary>Where an app's variable of the same name was set before Gitberth set it from a database.</summary>
public sealed record VariableReplaces(bool Environment, bool GitHub)
{
    public static readonly VariableReplaces Nothing = new(false, false);

    public bool Any => Environment || GitHub;

    /// <summary>"It replaces the value set on the Environment tab and the repo's GitHub variable." or "".</summary>
    public string Text(string variable) => (Environment, GitHub) switch
    {
        (true, true) => $"It replaces the {variable} set on the Environment tab and the repo's GitHub variable.",
        (true, false) => $"It replaces the {variable} set on the Environment tab.",
        (false, true) => $"It replaces the repo's GitHub variable {variable}.",
        _ => "",
    };
}

/// <summary>What a deploy writes to its log as it makes a preview's databases.</summary>
public interface IDatabaseLog
{
    void Step(string text);
    void Line(string text);
    /// <summary>Values that must never show in the log from here on.</summary>
    void Mask(IEnumerable<string> secrets);
}

/// <summary>
/// An app's databases, from its Settings tab: a Postgres database and a role of its own on a Postgres server
/// (<see cref="DatabaseServers"/>), or a Redis container of its own; the connection string goes in a variable of the
/// app's (DATABASE_URL, REDIS_URL), which Gitberth sets. For a Git app the deploy sets it (<see cref="ValuesAsync"/>: it wins
/// over GitHub's variables, is masked in the log, and reaches the build as a secret), for any other app the change engine
/// puts it in the container's environment; either way the app takes it the way any settings change does. Each preview of
/// a Git app gets its own (<see cref="ForPreviewAsync"/>), empty or a copy of production's, dropped with the preview.
/// Gitberth never touches a database or role it didn't make: its names never clash with what a server has, and only a
/// database it made is ever dropped.
/// </summary>
/// <param name="actions">A Git app's deploy that makes a change live; null in tests that don't deploy.</param>
/// <param name="settings">Any other app's settings flow (the change engine); null in tests that don't change containers.</param>
/// <param name="catalog">Adopts a container that has no row yet, so its databases have an app to belong to.</param>
/// <param name="repoVariables">The repo's GitHub variables, to say when Gitberth's value replaces one.</param>
/// <param name="changer">The change engine's jobs: a change of any other app's environment waits for the one under way to end.</param>
/// <param name="locks">Whether something is changing a container right now: waited for the same way.</param>
public sealed class DatabaseService(
    IDbContextFactory<GitberthDb> dbFactory,
    DockerCli docker,
    SecretProtector protector,
    DatabaseServers servers,
    GitberthOptions options,
    ILogger<DatabaseService> logger,
    Lazy<AppActions>? actions = null,
    Lazy<AppSettingsService>? settings = null,
    AppCatalog? catalog = null,
    RepoVariables? repoVariables = null,
    NetworkService? network = null,
    Audit.AuditLog? audit = null,
    Lazy<PortChanger>? changer = null,
    ContainerLocks? locks = null)
{
    /// <summary>How long a change of an app's environment waits for the change engine's change of it under way.</summary>
    internal TimeSpan ChangeWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The image an app's Redis runs.</summary>
    public const string RedisImage = "redis:8-alpine";

    /// <summary>Where an app's Redis listens.</summary>
    public const int RedisPort = 6379;

    private readonly PostgresAdmin _admin = new(docker);
    // One change to an app's databases at a time; one create on the servers at a time, so two never pick the same name.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _appGates = new();
    private readonly SemaphoreSlim _createGate = new(1, 1);

    /// <summary>"Postgres" or "Redis".</summary>
    public static string KindText(DatabaseKind kind) => kind == DatabaseKind.Redis ? "Redis" : "Postgres";

    /// <summary>The variable a kind's connection string goes in unless chosen otherwise.</summary>
    public static string DefaultVariable(DatabaseKind kind) => kind == DatabaseKind.Redis ? DatabaseNames.RedisVariable : DatabaseNames.PostgresVariable;

    /// <summary>A new password: 32 letters and digits, safe in a URL and a shell, and as SCRAM's input as it is.</summary>
    internal static string NewPassword() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 32);

    // ---- Reading ----

    /// <summary>The app's databases, oldest first, with their sizes when <paramref name="sizes"/> (each a docker exec).</summary>
    public async Task<IReadOnlyList<DatabaseView>> ListAsync(int appId, bool sizes = true, CancellationToken ct = default)
    {
        var rows = await RowsAsync(appId, ct);
        var views = new List<DatabaseView>();
        foreach (var group in rows.GroupBy(r => (r.Kind, r.Server)))
        {
            if (group.Key.Kind == DatabaseKind.Postgres)
            {
                var (server, login) = await servers.LoginAsync(group.Key.Server, ct);
                var size = sizes && login is not null ? await _admin.SizesAsync(login, group.Select(r => r.Name).ToList(), ct) : null;
                var problem = server is null ? $"There's no Postgres server called {group.Key.Server} any more." : server.Problem;
                foreach (var r in group)
                    views.Add(View(r, login?.Alias ?? server?.Alias ?? r.Server, size is not null && size.TryGetValue(r.Name, out var bytes) ? bytes : null,
                        problem ?? (size is not null && !size.ContainsKey(r.Name) ? $"{group.Key.Server} has no database {r.Name} any more." : null)));
            }
            else
            {
                foreach (var r in group)
                {
                    var (running, used) = sizes ? await RedisMemoryAsync(r, ct) : (true, null);
                    views.Add(View(r, r.Name, used, running ? null : $"Its container {r.Server} isn't running."));
                }
            }
        }
        return views.OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToList();
    }

    private static DatabaseView View(ManagedDatabase r, string host, long? size, string? problem) =>
        new(r.Id, r.Kind, r.Server, r.Name, r.Role, r.Variable, r.Created, r.CreatedAt, host, size, problem);

    /// <summary>The app's own databases' rows (a preview's are its own too), oldest first.</summary>
    public async Task<List<ManagedDatabase>> RowsAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ManagedDatabases.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.Id).ToListAsync(ct);
    }

    /// <summary>
    /// What a deploy of the app sets from its databases (its own, not a preview's: <see cref="ForPreviewAsync"/>): each
    /// variable with its connection string.
    /// </summary>
    public async Task<IReadOnlyList<ProvidedVariable>> ValuesAsync(int appId, CancellationToken ct = default)
    {
        var list = new List<ProvidedVariable>();
        foreach (var row in await RowsAsync(appId, ct))
            list.Add(new ProvidedVariable(row.Variable, await UrlAsync(row, null, ct), From(row), protector.TryUnprotect(row.ProtectedPassword)));
        return list;
    }

    /// <summary>
    /// A stamp of the app's databases (which, in which variables), so a deploy can tell a database added or removed while it
    /// ran: the change is then still to be made live.
    /// </summary>
    public async Task<string> StampAsync(int appId, CancellationToken ct = default) =>
        string.Join(',', (await RowsAsync(appId, ct)).Select(r => $"{r.Id.ToString(CultureInfo.InvariantCulture)}:{r.Variable}"));

    /// <summary>A database's connection string, for Copy connection string; null when there's no such database of the app's.</summary>
    public async Task<string?> ConnectionStringAsync(int appId, int databaseId, CancellationToken ct = default)
    {
        var row = (await RowsAsync(appId, ct)).FirstOrDefault(r => r.Id == databaseId);
        return row is null ? null : await UrlAsync(row, null, ct);
    }

    /// <summary>"the Postgres database shop on postgres", "its Redis, shop-redis".</summary>
    public static string From(ManagedDatabase row) =>
        row.Kind == DatabaseKind.Redis ? $"its Redis, {row.Name}" : $"the Postgres database {row.Name} on {row.Server}";

    // The connection string: the server's name on the shared network, or (for a preview's made from production's URL) that
    // URL with the preview's own role, password and database.
    private async Task<string> UrlAsync(ManagedDatabase row, PostgresUrl? production, CancellationToken ct)
    {
        var password = protector.TryUnprotect(row.ProtectedPassword);
        if (row.Kind == DatabaseKind.Redis) return DatabaseUrls.Redis(password, row.Name, RedisPort);
        var user = row.Role ?? row.Name;
        if (production is not null) return DatabaseUrls.ForPreview(production, user, password, row.Name);
        var (host, port) = await ServerAddressAsync(row.Server, ct);
        return DatabaseUrls.Postgres(user, password, host, port, row.Name);
    }

    // Where apps reach a Postgres server: its alias on the shared network (its name when it has none there), and its port.
    private async Task<(string Host, int Port)> ServerAddressAsync(string container, CancellationToken ct)
    {
        var details = (await docker.InspectContainersAsync([container], ct)).FirstOrDefault();
        if (details is null) return (container, 5432);
        var shared = details.Networks.FirstOrDefault(n => n.Name == options.Network);
        var alias = shared?.Aliases.FirstOrDefault(a => a.Length > 0 && a != details.Name && !(a.Length == 12 && a.All(char.IsAsciiHexDigitLower))) ?? details.Name;
        return (alias, ConnectionDetails.For(details, alias)?.Port ?? 5432);
    }

    /// <summary>
    /// Whether the app has a value of its own by that name that Gitberth's would replace: on its Environment tab (for every
    /// deploy or for production; any other app's container's own), or in its repo's GitHub variables.
    /// </summary>
    public async Task<VariableReplaces> ReplacesAsync(int appId, string variable, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().Include(a => a.EnvVars).FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null || variable.Length == 0) return VariableReplaces.Nothing;
        var own = app.Deployed
            ? EnvScopes.ForDeploy(app.EnvVars, preview: false).Any(v => v.Key == variable && protector.TryUnprotect(v.ProtectedValue).Length > 0)
            : await ContainerHasAsync(app, variable, ct);
        // An inline Compose app is deployed too, but from no repo: it has no GitHub variables.
        var github = app.FromRepo && (await GitHubNamesAsync(app, ct)).Contains(variable);
        return new VariableReplaces(own, github);
    }

    // The repo's GitHub variables' names, read at most once a minute: the Databases section asks as it's typed in.
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlySet<string> Names)> _github = new(StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlySet<string>> GitHubNamesAsync(AppDefinition app, CancellationToken ct)
    {
        if (repoVariables is null) return new HashSet<string>();
        // By Owner and Repo, as stored: a GitLab project's (gitlab:group/project) is never a GitHub repo's (group/project).
        var key = $"{app.Owner}/{app.Repo}";
        if (_github.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(1)) return known.Names;
        try
        {
            var names = (await repoVariables.ForDeployAsync(app.Owner, app.Repo, ct)).Variables.Where(v => v.Value.Length > 0).Select(v => v.Name)
                .ToHashSet(StringComparer.Ordinal);
            _github[key] = (DateTime.UtcNow, names);
            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't read {Repo}'s GitHub variables", app.RepoFullName);
            return new HashSet<string>();
        }
    }

    // Whether a container's own environment (not its image's) has the variable, other than as Gitberth's own value.
    private async Task<bool> ContainerHasAsync(AppDefinition app, string variable, CancellationToken ct)
    {
        if (settings is null) return false;
        try
        {
            var view = await settings.Value.LoadAsync(app.ContainerName, ct);
            var mine = (await RowsAsync(app.Id, ct)).Any(r => r.Variable == variable);
            return !mine && view?.Container.Env.Any(e => e.Key == variable && !e.FromImage && e.Value.Length > 0) == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't read {Container}'s environment", app.ContainerName);
            return false;
        }
    }

    // ---- Adding ----

    /// <summary>
    /// Adds a database to the app (adopting a container that has no row yet): makes it, keeps it, and has the app take its
    /// connection string as any settings change does (a Git app by a deploy, any other app through the change engine).
    /// </summary>
    public async Task<DatabaseDone> AddAsync(string key, DatabaseRequest request, CancellationToken ct = default)
    {
        var variable = request.Variable.Trim();
        if (variable.Length == 0) return new DatabaseDone(false, "Give the variable its connection string goes in, like " + DefaultVariable(request.Kind) + ".");
        if (!EnvRules.IsValidKey(variable)) return new DatabaseDone(false, $"'{variable}' isn't a valid variable name (letters, digits and _; it can't start with a digit).");
        var app = await AppAsync(key, ct);
        if (app is null) return new DatabaseDone(false, $"There's no app called {key}.");
        if (app.IsPreview) return new DatabaseDone(false, "A preview's databases follow its parent's: they're chosen in the parent's Settings › Previews.");
        var gate = _appGates.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var rows = await RowsAsync(app.Id, ct);
            if (rows.FirstOrDefault(r => r.Variable == variable) is { } same)
                return new DatabaseDone(false, $"{variable} is set from {From(same)} already. Give this one another variable.", app.Id);
            if (request.Kind == DatabaseKind.Redis && rows.FirstOrDefault(r => r.Kind == DatabaseKind.Redis) is { } redis)
                return new DatabaseDone(false, $"It has a Redis already, in {redis.Variable}.", app.Id);
            // Any other app takes it through the change engine: what the engine won't change is said before anything's made.
            if (!app.Deployed && await EnvRefusalAsync(app, ct) is { } refused) return new DatabaseDone(false, refused, app.Id);

            var made = request.Kind == DatabaseKind.Redis ? await MakeRedisAsync(app, variable, ct) : await MakePostgresAsync(app, variable, request.Server, ct);
            if (made.Error is not null || made.Row is null)
            {
                Audited("app.database.added", app, $"Couldn't add a {KindText(request.Kind)} database to {app.Name}: {made.Error?.TrimEnd('.')}.", made.Row, Audit.AuditOutcome.Failed,
                    [new("Variable", null, variable)]);
                return new DatabaseDone(false, made.Error ?? "It couldn't be made.", app.Id);
            }
            // Kept at once, whatever happens to the request from here (nothing waits on it any more): what's made is always
            // someone's. What it replaces is read first, while the app's own value is still the only one by that name.
            var row = made.Row;
            VariableReplaces replaces;
            try
            {
                replaces = await ReplacesAsync(app.Id, variable, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't tell what {Variable} replaces for {App}", variable, app.Slug);
                replaces = VariableReplaces.Nothing;
            }
            await KeepAsync(row);
            Audited("app.database.added",
                app, $"Added {(row.Kind == DatabaseKind.Redis ? $"a Redis ({row.Name})" : $"the Postgres database {row.Name} on {row.Server}")} to {app.Name}: its connection string is in {variable}.",
                row, Audit.AuditOutcome.Succeeded,
                [
                    new("Kind", null, KindText(row.Kind)), new(row.Kind == DatabaseKind.Redis ? "Container" : "Server", null, row.Server),
                    new(row.Kind == DatabaseKind.Redis ? "Name" : "Database", null, row.Name), new("Variable", null, variable),
                    .. replaces.Any ? new[] { new Audit.AuditChange("Replaces", null, replaces.Environment && replaces.GitHub ? "the Environment tab's and GitHub's value" : replaces.Environment ? "the Environment tab's value" : "GitHub's value") } : [],
                ]);

            // The app takes it.
            var what = row.Kind == DatabaseKind.Redis ? $"Added a Redis, {row.Name}" : $"Added the database {row.Name} on {row.Server}";
            if (app.Deployed)
            {
                var deployment = await ApplyGitAsync(app, ct);
                return new DatabaseDone(true, $"{what}. {Taking(app, deployment)}", app.Id, row.Id, deployment);
            }
            // It reaches the server or its Redis by name on the shared network: joined to it first when it isn't there.
            var joined = await JoinSharedAsync(app, ct);
            var (ok, message, job) = await ApplyEnvAsync(app, variable, await UrlAsync(row, null, ct), ct);
            return ok
                ? new DatabaseDone(true, $"{what}. {(job is null ? message : $"{app.Name} is recreated with {variable}.")}{joined}", app.Id, row.Id, Job: job)
                : new DatabaseDone(true, $"{what}, but {app.Name} didn't take {variable}: {message.TrimEnd('.')}.{joined}", app.Id, row.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Joins any other app's container to the shared network, where it reaches its database by name, when it isn't on it
    /// (a container started by hand on Docker's own bridge, say); nothing restarts, and the network is kept for every later
    /// recreate. What came of it, as a sentence that follows the add's (" It joined gitberth…"), or "" when it was there.
    /// </summary>
    private async Task<string> JoinSharedAsync(AppDefinition app, CancellationToken ct)
    {
        if (network is null) return "";
        try
        {
            var details = (await docker.InspectContainersAsync([app.ContainerName], ct)).FirstOrDefault();
            if (details is null || details.Networks.Any(n => n.Name == options.Network)) return "";
            await WaitForChangeAsync(app.ContainerName, ct);
            var change = await network.JoinAsync(app.ContainerName, options.Network, ct: ct);
            return change.Ok
                ? $" It joined {options.Network}, where it reaches the database by name."
                : $" It isn't on {options.Network}, where it would reach the database by name: {change.Error!.TrimEnd('.')}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't join {Container} to {Network} for its database", app.ContainerName, options.Network);
            return $" It isn't on {options.Network}, where it would reach the database by name.";
        }
    }

    // What a Git app's change means for it now.
    private static string Taking(AppDefinition app, int? deployment) =>
        deployment is not null ? $"A deploy without a build makes {app.Name} take it; its next build gets it too."
        : app.CurrentImage is null ? "Its first deploy gets it."
        : $"{app.Name} takes it when it starts again.";

    private sealed record Made(ManagedDatabase? Row, string? Error);

    private async Task<Made> MakePostgresAsync(AppDefinition app, string variable, string? serverName, CancellationToken ct)
    {
        var server = serverName is { Length: > 0 } ? serverName : (await servers.DefaultAsync(null, ct))?.Container;
        if (server is null) return new Made(null, NoServer);
        var (found, login) = await servers.LoginAsync(server, ct);
        if (login is null) return new Made(null, found is null ? $"There's no Postgres server called {server}." : $"Gitberth can't use {server}: {found.Problem}");
        await _createGate.WaitAsync(ct);
        try
        {
            var taken = await TakenOnAsync(login, ct);
            var name = DatabaseNames.ForApp(app.Slug, taken);
            var password = NewPassword();
            // Never cut off halfway (it takes a moment): a cancelled create would leave a role and a database no one keeps.
            if (await _admin.CreateAsync(login, name, name, password, CancellationToken.None) is { } error) return new Made(null, error);
            return new Made(new ManagedDatabase
            {
                AppId = app.Id, Kind = DatabaseKind.Postgres, Server = server, Name = name, Role = name,
                ProtectedPassword = protector.Protect(password), Variable = variable, Created = true,
            }, null);
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>What Add a database says when there's no server to put a Postgres database on.</summary>
    public const string NoServer = "There's no Postgres server for it yet. Create one in Settings › Databases, or choose one there.";

    // Every name the server has, and every name Gitberth keeps for it (a database dropped outside Gitberth still has its row).
    private async Task<IReadOnlySet<string>> TakenOnAsync(ServerLogin login, CancellationToken ct)
    {
        var taken = await _admin.NamesAsync(login, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var r in await db.ManagedDatabases.AsNoTracking().Where(d => d.Kind == DatabaseKind.Postgres && d.Server == login.Container).ToListAsync(ct))
        {
            taken.Add(r.Name);
            if (r.Role is { } role) taken.Add(role);
        }
        return taken;
    }

    private async Task<Made> MakeRedisAsync(AppDefinition app, string variable, CancellationToken ct)
    {
        var alias = await RedisAliasAsync(app.Slug, ct);
        var container = DatabaseNames.RedisContainer(app.ContainerName);
        var password = NewPassword();
        if (await StartRedisAsync(container, DatabaseNames.RedisVolume(app.ContainerName), alias, app.Slug, password, ct) is { } error) return new Made(null, error);
        return new Made(new ManagedDatabase
        {
            AppId = app.Id, Kind = DatabaseKind.Redis, Server = container, Name = alias, Role = null,
            ProtectedPassword = protector.Protect(password), Variable = variable, Created = true,
        }, null);
    }

    // <slug>-redis, or the first free variant: never an app's slug, a name on the shared network, or another Redis's.
    private async Task<string> RedisAliasAsync(string slug, CancellationToken ct)
    {
        HashSet<string> taken;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.Apps.AsNoTracking().Select(a => new { a.Slug, a.Container }).ToListAsync(ct);
            taken = rows.Select(a => a.Slug).Concat(rows.Where(a => a.Container is not null).Select(a => a.Container!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            taken.UnionWith(await db.ManagedDatabases.AsNoTracking().Where(d => d.Kind == DatabaseKind.Redis).Select(d => d.Name).ToListAsync(ct));
        }
        if (network is not null) taken.UnionWith(await network.TakenNamesAsync(options.Network, ct));
        return DatabaseNames.RedisAlias(slug, name => taken.Contains(name) || taken.Contains(AppDefinition.ContainerNameFor(name)));
    }

    /// <summary>
    /// The Redis container: redis:8-alpine with its password (passed through the docker CLI's environment, never its
    /// command line) and appendonly on its own named volume, on the shared network by its alias, restarting unless stopped,
    /// labelled as the app's (hidden among its app's, as a deploy's temporaries are). Null once it answers, else why not.
    /// Once Docker has made it, anything but its answer (none in time, an error, the deploy or request cancelled) removes it
    /// again, so nothing is left half made that no one keeps.
    /// </summary>
    /// <param name="started">Called once it's made, before it's waited for: a preview keeps its row from then on.</param>
    private async Task<string?> StartRedisAsync(string container, string volume, string alias, string slug, string password, CancellationToken ct,
        Func<Task>? started = null)
    {
        if (await docker.ContainerExistsAsync(container, ct))
        {
            // One this app's (or preview's) Redis left behind with no row keeping it is taken away; anything else is never touched.
            if (!await LeftOverAsync(container, slug, ct)) return $"There's a container called {container} already.";
            await docker.RemoveAsync(container, CancellationToken.None);
        }
        await docker.EnsureNetworkAsync(ct);
        var args = RedisRunArgs(container, volume, alias, slug, options.Network, await docker.GetLogOptionsAsync(ct));
        // docker run -d returns at once; never cut off, so a container it made is always known to be there.
        var r = await docker.RunAsync(args, new ProcessOptions { Environment = new Dictionary<string, string> { ["REDIS_PASSWORD"] = password }, MaxCapture = 64 * 1024 },
            CancellationToken.None);
        if (!r.Ok)
        {
            // A run that failed after its create (its start refused) leaves the container behind.
            if (await docker.ContainerExistsAsync(container, CancellationToken.None)) await docker.RemoveAsync(container, CancellationToken.None);
            return $"Docker didn't start {container}: {PostgresAdmin.FirstLine(r.ErrorText) ?? "docker run failed"}.";
        }
        var answered = false;
        try
        {
            if (started is not null) await started();
            // It answers within seconds.
            for (var i = 0; i < 40 && !answered; i++)
            {
                var ping = await docker.ExecAsync(container, ["redis-cli", "ping"], null, new Dictionary<string, string> { ["REDISCLI_AUTH"] = password }, ct);
                answered = ping.Ok && ping.StdOut.Contains("PONG", StringComparison.Ordinal);
                if (!answered) await Task.Delay(500, ct);
            }
            if (answered) return null;
            var logs = await docker.LogsTailAsync(container, 5, CancellationToken.None);
            return $"{container} didn't answer: {PostgresAdmin.FirstLine(logs.StdOut + logs.StdErr) ?? "it didn't start"}.";
        }
        finally
        {
            if (!answered) await docker.RemoveAsync(container, CancellationToken.None);
        }
    }

    // Whether the container is a Redis Gitberth made for this app (its labels say so) that no row keeps any more.
    private async Task<bool> LeftOverAsync(string container, string slug, CancellationToken ct)
    {
        var details = (await docker.InspectContainersAsync([container], ct)).FirstOrDefault();
        if (details is null || !DatabaseNames.IsMember(details.Labels) || details.Labels.GetValueOrDefault("gitberth.app") != slug) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return !await db.ManagedDatabases.AsNoTracking().AnyAsync(d => d.Kind == DatabaseKind.Redis && d.Server == container, ct);
    }

    /// <summary>`docker run` for an app's Redis: its password is named (-e REDIS_PASSWORD) but its value isn't in it.</summary>
    internal static List<string> RedisRunArgs(string container, string volume, string alias, string slug, string network, IReadOnlyList<string> logOptions)
    {
        var args = new List<string>
        {
            "run", "-d", "--name", container,
            "--label", "gitberth.managed=true", "--label", $"gitberth.app={slug}", "--label", $"{DatabaseNames.MemberLabel}=redis",
            "--network", network, "--network-alias", alias,
            "--restart", "unless-stopped",
            "-v", $"{volume}:/data",
            "-e", "REDIS_PASSWORD",
        };
        foreach (var option in logOptions) args.AddRange(["--log-opt", option]);
        // The image's own entrypoint still runs (it gives /data to the redis user and drops root); the password is read
        // from the environment, so it's on no command line Docker keeps.
        args.AddRange(["--entrypoint", "sh", RedisImage, "-c", "exec docker-entrypoint.sh redis-server --appendonly yes --requirepass \"$REDIS_PASSWORD\""]);
        return args;
    }

    // Whether the Redis runs, and how much memory it holds (used_memory).
    private async Task<(bool Running, long? Used)> RedisMemoryAsync(ManagedDatabase row, CancellationToken ct)
    {
        var r = await docker.ExecAsync(row.Server, ["redis-cli", "info", "memory"], null,
            new Dictionary<string, string> { ["REDISCLI_AUTH"] = protector.TryUnprotect(row.ProtectedPassword) }, ct, maxCapture: 16 * 1024);
        if (!r.Ok) return (await docker.InspectStateAsync(row.Server, ct) is { Running: true }, null);
        var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("used_memory:", StringComparison.Ordinal));
        return (true, line is not null && long.TryParse(line["used_memory:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var used) ? used : null);
    }

    // ---- Removing ----

    /// <summary>
    /// Takes a database off the app: its variable goes (the app takes that as any settings change), and with
    /// <paramref name="deleteData"/> its data too (a Postgres database and its role dropped; a Redis's volume removed).
    /// Without it, a Postgres database stays on its server as it is, and a Redis's container goes while its volume stays.
    /// A database Gitberth didn't make is never dropped.
    /// </summary>
    public async Task<DatabaseDone> RemoveAsync(int appId, int databaseId, bool deleteData, CancellationToken ct = default)
    {
        var gate = _appGates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            AppDefinition? app;
            ManagedDatabase? row;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
                row = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId && d.AppId == appId, ct);
            }
            if (app is null || row is null) return new DatabaseDone(false, "That database isn't the app's any more.");
            if (deleteData && !row.Created)
                return new DatabaseDone(false, $"Gitberth didn't make {row.Name}, so it never deletes its data. Remove it and keep the data.", appId);

            // The data first: a database that can't be dropped stays the app's, so nothing is left that no one keeps.
            if (await LetGoAsync(row, deleteData, ct) is { } error)
            {
                Audited("app.database.removed", app, $"Couldn't delete {Describe(row)} of {app.Name}: {error.TrimEnd('.')}.", row, Audit.AuditOutcome.Failed,
                    [new("Variable", row.Variable, row.Variable), new("Data", null, "kept")]);
                return new DatabaseDone(false, error, appId);
            }
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
                await db.ManagedDatabases.Where(d => d.Id == row.Id).ExecuteDeleteAsync(CancellationToken.None);
            Audited("app.database.removed", app, deleteData
                    ? $"Deleted {Describe(row)} of {app.Name} with its data; {row.Variable} is no longer set."
                    : $"Took {Describe(row)} off {app.Name}: {KeptText(row)}, and {row.Variable} is no longer set.",
                row, Audit.AuditOutcome.Succeeded, [new("Variable", row.Variable, null), new("Data", null, deleteData ? "deleted" : "kept")]);

            var what = deleteData ? $"Deleted {Describe(row)} and its data" : $"Removed {Describe(row)}: {KeptText(row)}";
            if (app.Deployed)
            {
                var deployment = await ApplyGitAsync(app, ct);
                return new DatabaseDone(true, $"{what}. {Untaking(app, row.Variable, deployment)}", appId, row.Id, deployment);
            }
            var (ok, message, job) = await ApplyEnvAsync(app, row.Variable, null, ct);
            return ok
                ? new DatabaseDone(true, $"{what}. {(job is null ? message : $"{app.Name} is recreated without {row.Variable}.")}", appId, row.Id, Job: job)
                : new DatabaseDone(true, $"{what}, but {row.Variable} is still in {app.Name}'s container: {message.TrimEnd('.')}.", appId, row.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Untaking(AppDefinition app, string variable, int? deployment) =>
        deployment is not null ? $"A deploy without a build takes {variable} out of {app.Name}."
        : app.CurrentImage is null ? $"Its first deploy doesn't get {variable}."
        : $"{app.Name} goes without {variable} when it starts again.";

    private static string Describe(ManagedDatabase row) =>
        row.Kind == DatabaseKind.Redis ? $"its Redis ({row.Name})" : $"the Postgres database {row.Name}";

    private static string KeptText(ManagedDatabase row) =>
        row.Kind == DatabaseKind.Redis ? $"its data stays in the volume {RedisVolumeOf(row)}" : $"its data stays on {row.Server}";

    private static string RedisVolumeOf(ManagedDatabase row) =>
        row.Server.EndsWith("_redis", StringComparison.Ordinal) ? row.Server + "-data" : row.Server + "_redis-data";

    // What letting go of a database does: a Postgres database dropped (with deleteData) or left as it is; a Redis's container
    // removed, and its volume with deleteData. Null when done, else why not.
    private async Task<string?> LetGoAsync(ManagedDatabase row, bool deleteData, CancellationToken ct)
    {
        if (row.Kind == DatabaseKind.Redis)
        {
            var rm = await docker.RemoveAsync(row.Server, ct);
            if (!rm.Ok && await docker.ContainerExistsAsync(row.Server, ct)) return $"Docker didn't remove {row.Server}: {PostgresAdmin.FirstLine(rm.ErrorText)}.";
            if (deleteData)
            {
                var volume = RedisVolumeOf(row);
                var v = await docker.RemoveVolumeAsync(volume, ct);
                if (!v.Ok && !v.ErrorText.Contains("no such volume", StringComparison.OrdinalIgnoreCase)) return $"Docker didn't remove the volume {volume}: {PostgresAdmin.FirstLine(v.ErrorText)}.";
            }
            return null;
        }
        if (!deleteData || !row.Created) return null;
        var (server, login) = await servers.LoginAsync(row.Server, ct);
        if (login is null)
            return server is null ? $"There's no Postgres server called {row.Server}, so {row.Name} can't be dropped." : $"Gitberth can't use {row.Server} now: {server.Problem}";
        return await _admin.DropAsync(login, row.Name, row.Role, ct);
    }

    /// <summary>
    /// An app that's being deleted: each of its databases is let go of before its row goes (the rows go with it). With
    /// <paramref name="deleteData"/> (always for a preview) the data goes too; a Redis's container goes either way. What
    /// can't be dropped is logged and recorded, never a reason to keep the app.
    /// </summary>
    public async Task DeleteWithAppAsync(int appId, bool deleteData, CancellationToken ct = default)
    {
        var rows = await RowsAsync(appId, ct);
        if (rows.Count == 0) return;
        AppDefinition? app, parent = null;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
            if (app?.ParentAppId is int parentId) parent = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == parentId, ct);
        }
        if (app is null) return;
        var drop = deleteData || app.IsPreview;
        foreach (var row in rows)
        {
            string? error;
            try
            {
                error = await LetGoAsync(row, drop, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }
            if (error is not null) logger.LogWarning("Letting go of {Database} of {App}: {Error}", row.Name, app.Slug, error);
            var owner = parent ?? app;
            var whose = app.IsPreview ? $"the preview {app.Name}" : app.Name;
            Audited(app.IsPreview ? "preview.database.removed" : "app.database.removed", owner,
                error is not null ? $"Couldn't delete {Describe(row)} of {whose}: {error.TrimEnd('.')}."
                : drop && row.Created ? $"Deleted {Describe(row)} of {whose} with it."
                : $"Let go of {Describe(row)} of {whose} with it: {KeptText(row)}.",
                row, error is null ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
                [new("Data", null, error is null && drop && row.Created ? "deleted" : "kept")], target: app.IsPreview ? app.Name : null);
        }
        // The rows go with the app's; the marks of copies that never finished go now.
        var marks = rows.Select(r => CopyingKey(r.Id)).ToList();
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            await db.Settings.Where(s => marks.Contains(s.Key)).ExecuteDeleteAsync(CancellationToken.None);
    }

    // ---- How the app takes a change ----

    /// <summary>
    /// A Git app takes a change of its databases as a change of its variables: a deploy without a build when it runs (one
    /// under way is followed instead), else when it starts again. Builds get it from the next one on. The deploy, when one
    /// started.
    /// </summary>
    private async Task<int?> ApplyGitAsync(AppDefinition app, CancellationToken ct)
    {
        if (app.CurrentImage is null) return null;
        int? deployment = null;
        if (actions is not null)
        {
            try
            {
                deployment = await actions.Value.ApplyIfRunningAsync(app.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't deploy {App} after its databases changed", app.Slug);
            }
        }
        if (deployment is not null) return deployment;
        // Not running now: its next start makes its container again, with the change, rather than starting the old one.
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await db.Apps.Where(a => a.Id == app.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.ConfigChangePending, true)
            .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
        return null;
    }

    // Why the change engine won't change the app's environment, said before anything's made; null when it will.
    private async Task<string?> EnvRefusalAsync(AppDefinition app, CancellationToken ct)
    {
        if (settings is null) return $"Gitberth can't change {app.Name}'s environment from here.";
        var view = await settings.Value.LoadAsync(app.ContainerName, ct);
        if (view is null) return $"There's no app called {app.ContainerName}.";
        if (view.Access.Compose is { } compose)
            return $"Compose made {app.ContainerName} (project {compose.Project}): Compose would undo a change Gitberth made to it, so it can't get a database from here.";
        if (view.Access.ReadOnly.Contains(SettingField.Env)) return view.Access.Recreate ?? $"Gitberth doesn't change {app.Name}'s environment.";
        return view.Access.Recreate is { } why ? $"Gitberth won't recreate {app.Name} now: {why.TrimEnd('.')}." : null;
    }

    /// <summary>
    /// Any other app takes a change of its databases through its settings: the variable set (or taken out) of its
    /// container's environment by the change engine, which recreates it with its checks, as an edit on its Environment tab
    /// would. (ok, what it said, the engine's job when one runs).
    /// </summary>
    private async Task<(bool Ok, string Message, PortChangeJob? Job)> ApplyEnvAsync(AppDefinition app, string variable, string? value, CancellationToken ct)
    {
        if (settings is null) return (false, $"Gitberth can't change {app.Name}'s environment from here.", null);
        try
        {
            await WaitForChangeAsync(app.ContainerName, ct);
            var view = await settings.Value.LoadAsync(app.ContainerName, ct);
            if (view is null) return (false, $"There's no app called {app.ContainerName}.", null);
            var env = view.Container.Env.Where(e => e.Key != variable).ToList();
            if (value is not null) env.Add(new EnvSetting(variable, value, FromImage: false, Secret: true));
            else
            {
                // The image's own value of that name, when it has one, is the container's again.
                if (view.Container.Env.FirstOrDefault(e => e.Key == variable && e.FromImage) is { } image) env.Add(image);
            }
            var edit = new SettingsEdit(view.ContainerId, view.Container, view.Container with { Env = env }, view.Options, view.Options);
            var plan = await settings.Value.PlanAsync(app.ContainerName, edit, ct);
            if (plan.Lines.Count == 0) return (true, value is null ? $"{app.Name} didn't have {variable}." : $"{app.Name} has it already.", null);
            if (plan.Blocked is { } blocked) return (false, blocked, null);
            if (plan.Problems.Count > 0) return (false, plan.Problems[0].Message, null);
            if (plan.ComposeSnippet is not null) return (false, "Compose manages its container, so the change would be its file's", null);
            var outcome = await settings.Value.ApplyAsync(plan, ApplyTiming.Now, ct);
            return (outcome.Ok, outcome.Message, outcome.Job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't change {Container}'s environment for its database", app.ContainerName);
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// A change the engine is making to the container now (the recreate a database's add just started, say) ends first, for
    /// <see cref="ChangeWait"/> at most: this one is made on what that leaves, rather than refused for it.
    /// </summary>
    private async Task WaitForChangeAsync(string container, CancellationToken ct)
    {
        var until = DateTime.UtcNow + ChangeWait;
        while (DateTime.UtcNow < until)
        {
            if (changer?.Value.JobFor(container) is { Completion.IsCompleted: false } job)
            {
                try
                {
                    await job.Completion.WaitAsync(until - DateTime.UtcNow, ct);
                }
                catch (TimeoutException)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // However it ended, it's over: the plan is made on what it left.
                }
                continue;
            }
            if (locks?.IsHeld(container) != true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    // ---- Previews ----

    // Where a preview's database comes from: one of the parent's own databases, or production's variable pointing at a
    // Postgres server on this host that Gitberth can use.
    private sealed record Source(string Variable, DatabaseKind Kind, string Server, string Production, PostgresUrl? Url);

    /// <summary>
    /// The databases a preview gets before its build (each preview its own: <c>&lt;production's database&gt;_&lt;preview&gt;</c>
    /// and its role on the same server, empty or a copy of production's, and a Redis of its own for a parent with one), with
    /// their variables: the same names as production's, pointing at the preview's own. A database is the preview's own when
    /// its parent has one of its own on a server (<see cref="RowsAsync"/>), or when production's variable is a postgres://
    /// URL whose host is a Postgres container on this host Gitberth can use. Made once, at its first deploy; dropped with it.
    /// Nothing when the parent's previews get no database of their own (<see cref="PreviewDatabaseMode.Off"/>).
    /// </summary>
    /// <param name="production">What production's deploy gets, by name: the parent's own values for every deploy and for production, and the repo's GitHub variables it doesn't set itself.</param>
    /// <param name="forPreviews">Names the parent has a value for previews of its own: the owner's choice, never replaced.</param>
    /// <exception cref="InvalidOperationException">A database the preview should get couldn't be made: its deploy stops, and the next one tries again.</exception>
    public async Task<IReadOnlyList<ProvidedVariable>> ForPreviewAsync(AppDefinition preview, AppDefinition parent, IReadOnlyDictionary<string, string> production,
        IReadOnlySet<string> forPreviews, IDatabaseLog log, CancellationToken ct = default)
    {
        var mine = await RowsAsync(preview.Id, ct);
        if (parent.PreviewDatabases == PreviewDatabaseMode.Off)
        {
            if (mine.Count > 0) log.Line($"{parent.Name}'s previews get no database of their own (Settings › Previews), so the ones made for this preview before aren't used.");
            return [];
        }
        // A copy of production's data that Gitberth stopped before it finished (it was restarted, or killed) is no copy: it
        // goes, and is made again below.
        mine = await TakeBackUnfinishedAsync(mine, log);
        var sources = await SourcesAsync(parent, production, forPreviews, ct);
        var provided = new List<ProvidedVariable>();
        // One database of the preview's own for each of production's: variables that name the same one (Prisma's
        // DATABASE_URL and DIRECT_URL) point at the same preview database, each with its own URL's query.
        foreach (var group in sources.GroupBy(s => s.Kind == DatabaseKind.Redis ? (s.Kind, Server: "", Production: s.Variable) : (s.Kind, s.Server, s.Production)))
        {
            var members = group.ToList();
            var row = members[0].Kind == DatabaseKind.Redis
                ? mine.FirstOrDefault(r => r.Kind == DatabaseKind.Redis && r.Variable == members[0].Variable)
                : mine.FirstOrDefault(r => r.Kind == DatabaseKind.Postgres && r.Server == members[0].Server && members.Any(m => m.Variable == r.Variable));
            if (row is null)
            {
                row = await MakeForPreviewAsync(preview, parent, members, log, ct);
                mine.Add(row);
            }
            var password = protector.TryUnprotect(row.ProtectedPassword);
            foreach (var source in members)
            {
                var value = await UrlAsync(row, row.Kind == DatabaseKind.Postgres && row.Server == source.Server ? source.Url : null, ct);
                log.Mask([value, password]);
                provided.Add(new ProvidedVariable(source.Variable, value,
                    row.Kind == DatabaseKind.Redis ? $"its own Redis, {row.Name}" : $"its own Postgres database {row.Name} on {row.Server}", password));
            }
        }
        return provided;
    }

    // A preview's databases, less those whose copy of production's data never finished (its mark is still there): each is
    // dropped and forgotten, so its deploy makes it again rather than run on half a copy.
    private async Task<List<ManagedDatabase>> TakeBackUnfinishedAsync(List<ManagedDatabase> rows, IDatabaseLog log)
    {
        if (rows.Count == 0) return rows;
        var marks = rows.Select(r => CopyingKey(r.Id)).ToList();
        HashSet<string> unfinished;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            unfinished = (await db.Settings.AsNoTracking().Where(s => marks.Contains(s.Key)).Select(s => s.Key).ToListAsync(CancellationToken.None)).ToHashSet(StringComparer.Ordinal);
        if (unfinished.Count == 0) return rows;
        var kept = new List<ManagedDatabase>();
        foreach (var row in rows)
        {
            if (!unfinished.Contains(CopyingKey(row.Id)))
            {
                kept.Add(row);
                continue;
            }
            log.Line($"The copy of production's data into {row.Name} didn't finish (Gitberth stopped meanwhile): it's made again.");
            var (_, login) = await servers.LoginAsync(row.Server, CancellationToken.None);
            if (login is null || await _admin.DropAsync(login, row.Name, row.Role, CancellationToken.None) is not null)
                throw new InvalidOperationException($"The preview's database {row.Name}, whose copy of production's data didn't finish, couldn't be dropped to be made again.");
            await UnkeepAsync(row);
        }
        return kept;
    }

    /// <summary>
    /// The variables each preview of the parent gets a database of its own for with <paramref name="mode"/>, as they'd be
    /// found now (for its Previews section), each with where its database goes ("DATABASE_URL", "postgres").
    /// </summary>
    public async Task<IReadOnlyList<(string Variable, DatabaseKind Kind, string Server)>> PreviewVariablesAsync(AppDefinition parent, PreviewDatabaseMode mode,
        IReadOnlyDictionary<string, string> production, IReadOnlySet<string> forPreviews, CancellationToken ct = default) =>
        mode == PreviewDatabaseMode.Off ? [] : (await SourcesAsync(parent, production, forPreviews, ct)).Select(s => (s.Variable, s.Kind, s.Server)).ToList();

    private async Task<List<Source>> SourcesAsync(AppDefinition parent, IReadOnlyDictionary<string, string> production, IReadOnlySet<string> forPreviews, CancellationToken ct)
    {
        var sources = new List<Source>();
        foreach (var row in await RowsAsync(parent.Id, ct))
            if (!forPreviews.Contains(row.Variable)) sources.Add(new Source(row.Variable, row.Kind, row.Server, row.Name, null));
        IReadOnlyList<PostgresServer>? list = null;
        foreach (var (name, value) in production.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (forPreviews.Contains(name) || sources.Any(s => s.Variable == name)) continue;
            if (DatabaseUrls.ParsePostgres(value) is not { Database: { } database } url) continue;
            try
            {
                list ??= await servers.ListAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogInformation(ex, "Couldn't list the Postgres servers for {App}'s previews", parent.Slug);
                return sources;
            }
            if (list.FirstOrDefault(s => s.Names.Contains(url.Host, StringComparer.OrdinalIgnoreCase)) is { Usable: true } server)
                sources.Add(new Source(name, DatabaseKind.Postgres, server.Container, database, url));
        }
        return sources;
    }

    /// <summary>
    /// A preview's own database for one of production's (<paramref name="members"/>: the variables that name it), or its own
    /// Redis. It's kept (its row saved) as soon as it exists, so nothing the deploy stops for from then on (a cancel when its
    /// pull request closes or it's removed, Gitberth stopping, an error) leaves anything no one keeps: what didn't come to be
    /// is taken back at once, and whatever is kept goes with the preview.
    /// </summary>
    private async Task<ManagedDatabase> MakeForPreviewAsync(AppDefinition preview, AppDefinition parent, IReadOnlyList<Source> members, IDatabaseLog log,
        CancellationToken ct)
    {
        var source = members[0];
        var password = NewPassword();
        log.Mask([password]);
        if (source.Kind == DatabaseKind.Redis) return await MakePreviewRedisAsync(preview, parent, source, password, log, ct);

        var (server, login) = await servers.LoginAsync(source.Server, ct);
        if (login is null)
            throw new InvalidOperationException($"The preview's own database couldn't be made: Gitberth can't use {source.Server}{(server?.Problem is { } p ? $" ({p.TrimEnd('.')})" : "")}.");
        var variables = Audit.AuditText.Join(members.Select(m => m.Variable).ToList());
        // Production's data is copied as the server's superuser: only from a database Gitberth keeps for the app itself, or
        // one the URL's own role and password may read. Anyone who can set the app's variables (a repo's GitHub variables)
        // could otherwise have another app's database copied into a preview that runs their code.
        var copy = parent.PreviewDatabases == PreviewDatabaseMode.CopyOfProduction;
        string? notCopied = null;
        if (copy && members.All(m => m.Url is not null))
        {
            foreach (var m in members)
                if ((notCopied = await _admin.CopyRefusalAsync(login, m.Url!, source.Production, ct)) is null) break;
            copy = notCopied is null;
        }
        var row = new ManagedDatabase
        {
            AppId = preview.Id, Kind = DatabaseKind.Postgres, Server = source.Server, ProtectedPassword = protector.Protect(password), Variable = source.Variable,
        };
        await _createGate.WaitAsync(ct);
        try
        {
            row.Name = row.Role = DatabaseNames.ForPreview(source.Production, preview.Slug, parent.Slug, await TakenOnAsync(login, ct));
            log.Step(copy
                ? $"Making the preview's own database {row.Name} on {source.Server}, a copy of production's {source.Production}, for {variables}"
                : $"Making the preview's own database {row.Name} on {source.Server}, empty, for {variables}");
            if (notCopied is not null) log.Line($"Not a copy of production's {source.Production}: {notCopied}. The preview's database starts empty.");
            // Made in one go (never cut off halfway), then kept at once, marked while its copy isn't finished.
            if (await _admin.CreateAsync(login, row.Name, row.Role, password, CancellationToken.None) is { } error)
                throw new InvalidOperationException($"The preview's own database couldn't be made: {error}");
            await KeepAsync(row, copying: copy);
        }
        finally
        {
            _createGate.Release();
        }
        IReadOnlyList<string> warnings = [];
        if (copy)
        {
            CopyResult result;
            try
            {
                result = await _admin.CopyAsync(login, source.Production, row.Name, row.Role, ct);
            }
            catch (Exception)
            {
                // Cancelled (the preview is going, Gitberth is stopping) or broken: half a copy is no copy. docker exec's own
                // end doesn't stop the copy inside the container; dropping the database (WITH FORCE) does.
                await TakeBackAsync(login, row);
                throw;
            }
            if (!result.Ok)
            {
                await TakeBackAsync(login, row);
                throw new InvalidOperationException($"Production's data couldn't be copied into the preview's database: {result.Error}");
            }
            await CopiedAsync(row);
            warnings = result.Warnings;
            foreach (var warning in warnings) log.Line("warning: " + warning);
            log.Line(warnings.Count == 0 ? $"Copied production's data into {row.Name}." : $"Copied production's data into {row.Name}, with the errors above left out.");
        }
        Audited("preview.database.created", parent,
            $"Made the preview {preview.Name}'s own database {row.Name} on {row.Server} for {variables}, {(copy ? $"a copy of production's {source.Production}" : "empty")}.",
            row, Audit.AuditOutcome.Succeeded,
            [
                new("Variable", null, variables),
                new("Data", null, copy ? (warnings.Count == 0 ? "copied from production" : "copied from production, some objects left out") : notCopied is null ? "empty" : "empty: production's may not be copied"),
            ],
            target: preview.Name);
        return row;
    }

    // A preview's own Redis: kept as soon as Docker has made it; one that doesn't answer, or whose deploy is cancelled
    // meanwhile, is removed again with its volume and forgotten.
    private async Task<ManagedDatabase> MakePreviewRedisAsync(AppDefinition preview, AppDefinition parent, Source source, string password, IDatabaseLog log, CancellationToken ct)
    {
        var alias = await RedisAliasAsync(preview.Slug, ct);
        var container = DatabaseNames.RedisContainer(preview.ContainerName);
        var volume = DatabaseNames.RedisVolume(preview.ContainerName);
        var row = new ManagedDatabase
        {
            AppId = preview.Id, Kind = DatabaseKind.Redis, Server = container, Name = alias, ProtectedPassword = protector.Protect(password), Variable = source.Variable,
        };
        log.Step($"Starting the preview's own Redis, {alias}, for {source.Variable}");
        string? error;
        try
        {
            error = await StartRedisAsync(container, volume, alias, preview.Slug, password, ct, started: () => KeepAsync(row));
        }
        catch (Exception)
        {
            await ForgetPreviewRedisAsync(row, volume);
            throw;
        }
        if (error is not null)
        {
            await ForgetPreviewRedisAsync(row, volume);
            throw new InvalidOperationException($"The preview's own Redis couldn't be started: {error}");
        }
        Audited("preview.database.created", parent, $"Started the preview {preview.Name}'s own Redis, {row.Name}, for {row.Variable}.", row, Audit.AuditOutcome.Succeeded,
            [new("Variable", null, row.Variable), new("Data", null, "empty")], target: preview.Name);
        return row;
    }

    // A preview's Redis that didn't come to be: once Docker had made it (its row is kept then), its row and its new volume go
    // too; the container StartRedisAsync removed itself.
    private async Task ForgetPreviewRedisAsync(ManagedDatabase row, string volume)
    {
        if (row.Id == 0) return;
        await UnkeepAsync(row);
        await docker.RemoveVolumeAsync(volume, CancellationToken.None);
    }

    // A preview's database whose copy didn't finish: dropped, then forgotten. One that can't be dropped now stays kept and
    // marked, so its next deploy tries again and the preview's removal drops it.
    private async Task TakeBackAsync(ServerLogin login, ManagedDatabase row)
    {
        if (await _admin.DropAsync(login, row.Name, row.Role, CancellationToken.None) is { } error)
        {
            logger.LogWarning("Couldn't take back the preview's database {Database}: {Error}", row.Name, error);
            return;
        }
        await UnkeepAsync(row);
    }

    // Saved at once, whatever happens to the request or deploy that made it from here; with copying, marked until its copy
    // of production's data is finished (CopiedAsync), in the same transaction.
    private async Task KeepAsync(ManagedDatabase row, bool copying = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        db.ManagedDatabases.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);
        if (copying)
        {
            db.Settings.Add(new Setting { Key = CopyingKey(row.Id), Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await tx.CommitAsync(CancellationToken.None);
    }

    private async Task CopiedAsync(ManagedDatabase row)
    {
        var mark = CopyingKey(row.Id);
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await db.Settings.Where(s => s.Key == mark).ExecuteDeleteAsync(CancellationToken.None);
    }

    // A row that didn't come to be, and its mark.
    private async Task UnkeepAsync(ManagedDatabase row)
    {
        if (row.Id == 0) return;
        var mark = CopyingKey(row.Id);
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await db.ManagedDatabases.Where(d => d.Id == row.Id).ExecuteDeleteAsync(CancellationToken.None);
        await db.Settings.Where(s => s.Key == mark).ExecuteDeleteAsync(CancellationToken.None);
    }

    /// <summary>The setting that marks a preview's database whose copy of production's data hasn't finished.</summary>
    internal static string CopyingKey(int databaseId) => "databases.copying." + databaseId.ToString(CultureInfo.InvariantCulture);

    // ---- Shared ----

    private async Task<AppDefinition?> AppAsync(string key, CancellationToken ct)
    {
        var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key["gb-".Length..] : null;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Container == key || (a.Container == null && slug != null && a.Slug == slug), ct);
            if (row is not null) return row;
        }
        if (catalog is null) return null;
        try
        {
            return await catalog.AdoptAsync(key, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Audited(string action, AppDefinition app, string summary, ManagedDatabase? row, Audit.AuditOutcome outcome, IReadOnlyList<Audit.AuditChange> changes,
        string? target = null) =>
        audit?.Record(new Audit.AuditEntry(action, summary)
        {
            Outcome = outcome, AppId = app.Id, AppName = app.Name, Container = app.ContainerName,
            Target = target ?? (row is null ? "Databases" : row.Kind == DatabaseKind.Redis ? row.Name : $"{row.Name} on {row.Server}"),
            Changes = changes,
        });
}
