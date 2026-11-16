using System.Collections.Concurrent;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Gitberth.Sources;

namespace Gitberth.Databases;

/// <summary>
/// A Postgres server Gitberth found: a container of a Postgres image. Gitberth makes apps' databases on one it can use: it
/// runs, it's on the shared network (apps reach it by <see cref="Alias"/>), and Gitberth signs in to it inside its container
/// (psql through docker exec, as its POSTGRES_USER) as a superuser.
/// </summary>
/// <param name="Alias">The name apps on the shared network reach it by; null when it isn't on that network.</param>
/// <param name="User">Who Gitberth signs in as: its POSTGRES_USER, else postgres.</param>
/// <param name="Version">Its version number (180001), once Gitberth signed in to it.</param>
/// <param name="Problem">Why Gitberth can't use it; null when it can.</param>
/// <param name="Names">Every name it's reached by (its own and its aliases on each network): a production URL's host is matched against them.</param>
public sealed record PostgresServer(string Container, string Image, string? Alias, bool Running, string User, int? Version, string? Problem,
    IReadOnlyList<string> Names)
{
    public bool Usable => Problem is null && Version is not null;

    /// <summary>"18.1", or the image's tag before Gitberth signed in.</summary>
    public string VersionText => Version is int v ? PostgresSql.VersionText(v) : Image.Contains(':') ? Image[(Image.LastIndexOf(':') + 1)..] : Image;
}

/// <summary>How Gitberth signs in to a server it uses, for <see cref="PostgresAdmin"/>. The password only ever goes into psql's environment.</summary>
internal sealed record ServerLogin(string Container, string User, string? Password, int Version, string Alias);

/// <summary>Settings › Databases' "Create a Postgres server" under way: its pull, then the app's create (AppCreator).</summary>
public sealed class ServerCreation
{
    private string _phase = "Pulling";

    /// <summary>"Pulling", "Creating", "Starting", "Checking", "Done", "Failed".</summary>
    public string Phase => Volatile.Read(ref _phase);

    public Task<CreateResult> Completion { get; internal set; } = Task.FromResult(new CreateResult(false, "", null, null, null));

    internal void Enter(string phase) => Volatile.Write(ref _phase, phase);
}

/// <summary>
/// The Postgres servers on this host (Settings › Databases): every container of a Postgres image, whether Gitberth can use
/// it and why not, and the one new databases go on. One that Gitberth can use is the default by itself when it's the only
/// one. "Create a Postgres server" makes one as a normal app from postgres:18 (AppCreator, like New app from an image):
/// container postgres (or the first free variant), a named volume at the image's data path, a generated superuser
/// password kept as a secret variable, the shared network with that name as its alias, no published port, and updates
/// that only tell (a database is never updated by itself).
/// </summary>
/// <param name="images">Pulls the server's image first, so its create reads the image's ports and volumes.</param>
/// <param name="creator">Creates the server as an app; null in tests that never create one.</param>
public sealed class DatabaseServers(DockerCli docker, SettingsService settings, GitberthOptions options, ILogger<DatabaseServers> logger,
    Lazy<ImageService>? images = null, Lazy<AppCreator>? creator = null, Audit.AuditLog? audit = null)
{
    /// <summary>The image a server Gitberth sets up runs.</summary>
    public const string ServerImage = "postgres:18";

    // How long a sign-in check of a server is good for: pages ask often, and each check is a docker exec.
    private static readonly TimeSpan ProbeFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (DateTime At, int? Version, string? Problem)> _probes = new(StringComparer.Ordinal);
    private ServerCreation? _creation;

    /// <summary>Stands in for the clock in tests.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>The server being set up now, or the last one since Gitberth started; null for none.</summary>
    public ServerCreation? Creation => _creation;

    /// <summary>Every Postgres server on the host, those Gitberth can use first, then by name.</summary>
    public async Task<IReadOnlyList<PostgresServer>> ListAsync(CancellationToken ct = default)
    {
        var (all, error) = await docker.ListContainersAsync(managedOnly: false, ct);
        if (error is not null) throw new InvalidOperationException($"Docker didn't list its containers ({error}).");
        var names = all.Where(IsServer).Select(c => c.Name).ToList();
        if (names.Count == 0) return [];
        var details = await docker.InspectContainersAsync(names, ct);
        var servers = new List<PostgresServer>();
        foreach (var d in details)
        {
            var (server, _) = await ReadAsync(d, ct);
            servers.Add(server);
        }
        return servers.OrderBy(s => s.Usable ? 0 : 1).ThenBy(s => s.Container, StringComparer.Ordinal).ToList();
    }

    // A container of a Postgres image, never one of Gitberth's helpers or a deploy's temporary one.
    private static bool IsServer(ContainerSummary c) =>
        ConnectionDetails.EngineOf(c.Image) == DatabaseEngine.Postgres && !HostScanHelper.IsHelper(c.Labels) && !c.Labels.ContainsKey(ContainerSpecFactory.BridgeLabel);

    /// <summary>
    /// The server new databases go on: the one Settings › Databases chose while Gitberth can use it, else the only one it
    /// can use; null when there's none, or several and none chosen.
    /// </summary>
    public async Task<PostgresServer?> DefaultAsync(IReadOnlyList<PostgresServer>? servers = null, CancellationToken ct = default)
    {
        servers ??= await ListAsync(ct);
        var chosen = await settings.GetAsync(SettingKeys.DatabasesDefaultServer, ct);
        if (chosen is not null && servers.FirstOrDefault(s => s.Container == chosen && s.Usable) is { } saved) return saved;
        var usable = servers.Where(s => s.Usable).ToList();
        return usable.Count == 1 ? usable[0] : null;
    }

    /// <summary>The server Settings › Databases chose, as saved (its container's name); null when none was.</summary>
    public Task<string?> ChosenAsync(CancellationToken ct = default) => settings.GetAsync(SettingKeys.DatabasesDefaultServer, ct);

    /// <summary>Makes a server the default for new databases. Null when it's saved, else why not.</summary>
    public async Task<string?> SetDefaultAsync(string container, CancellationToken ct = default)
    {
        var servers = await ListAsync(ct);
        if (servers.FirstOrDefault(s => s.Container == container) is not { } server) return $"There's no Postgres server called {container}.";
        if (!server.Usable) return $"Gitberth can't use {container}: {server.Problem}";
        var before = await settings.GetAsync(SettingKeys.DatabasesDefaultServer, ct);
        if (before == container) return null;
        await settings.SetAsync(SettingKeys.DatabasesDefaultServer, container, ct);
        audit?.Record(new Audit.AuditEntry("settings.databases.changed", $"Made {container} the Postgres server new databases go on.")
        {
            Target = "Databases",
            Changes = [new("Default server", before ?? "none chosen", container)],
        });
        return null;
    }

    /// <summary>The server a URL's host names, by any name it's reached by; null when no Postgres container on this host answers to it.</summary>
    public async Task<PostgresServer?> FindByHostAsync(string host, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(s => s.Names.Contains(host, StringComparer.OrdinalIgnoreCase));

    /// <summary>How Gitberth signs in to the server; null when there's no such server or Gitberth can't use it.</summary>
    internal async Task<(PostgresServer? Server, ServerLogin? Login)> LoginAsync(string container, CancellationToken ct = default)
    {
        var details = (await docker.InspectContainersAsync([container], ct)).FirstOrDefault();
        if (details is null || ConnectionDetails.EngineOf(details.Image) != DatabaseEngine.Postgres) return (null, null);
        return await ReadAsync(details, ct);
    }

    // The server as it is now, and how to sign in to it when Gitberth can: its sign-in is checked at most every ProbeFor.
    private async Task<(PostgresServer Server, ServerLogin? Login)> ReadAsync(ContainerDetails d, CancellationToken ct)
    {
        var info = ConnectionDetails.For(d, d.Name);
        var user = info?.User ?? "postgres";
        var names = ContainerService.HostNamesOf(d);
        var shared = d.Networks.FirstOrDefault(n => n.Name == options.Network);
        var alias = shared is null ? null
            : shared.Aliases.FirstOrDefault(a => a.Length > 0 && a != d.Name && !(a.Length == 12 && a.All(char.IsAsciiHexDigitLower))) ?? d.Name;
        string? problem = !d.Running ? "It isn't running."
            : shared is null ? $"It isn't on Gitberth's network ({options.Network}), so apps can't reach it by name."
            : null;
        int? version = null;
        if (problem is null)
        {
            var key = d.Id + "|" + user;
            if (!_probes.TryGetValue(key, out var probe) || Now() - probe.At > ProbeFor)
            {
                probe = await ProbeAsync(d.Name, user, info?.Password, ct);
                _probes[key] = probe;
            }
            (version, problem) = (probe.Version, probe.Problem);
        }
        var server = new PostgresServer(d.Name, d.Image, alias, d.Running, user, version, problem, names);
        return (server, server.Usable ? new ServerLogin(d.Name, user, info?.Password, version!.Value, alias!) : null);
    }

    private async Task<(DateTime At, int? Version, string? Problem)> ProbeAsync(string container, string user, string? password, CancellationToken ct)
    {
        var r = await PostgresAdmin.RunAsync(docker, container, user, password, "postgres", PostgresSql.Probe, ct);
        if (!r.Ok) return (Now(), null, $"Gitberth couldn't sign in to it as {user} ({PostgresAdmin.FirstLine(r.ErrorText) ?? "psql failed"}).");
        if (PostgresSql.ParseProbe(r.StdOut) is not { } answer) return (Now(), null, $"Gitberth couldn't read its version as {user}.");
        return answer.Superuser
            ? (Now(), answer.Version, null)
            : (Now(), answer.Version, $"{user} isn't a superuser there, so Gitberth can't make databases and roles on it.");
    }

    /// <summary>Forgets what Gitberth knows of a server's sign-in, so the next look checks it again.</summary>
    public void Forget() => _probes.Clear();

    /// <summary>
    /// Sets up a Postgres server as a normal app, in the background; one under way is returned as it is. Once it's made, it
    /// becomes the default when none was chosen.
    /// </summary>
    public ServerCreation Create()
    {
        if (_creation is { Completion.IsCompleted: false } running) return running;
        if (creator is null || images is null) throw new InvalidOperationException("Gitberth can't set up a server from here.");
        var creation = new ServerCreation();
        // As whoever asked: the task carries their audit actor with it.
        creation.Completion = Task.Run(async () =>
        {
            CreateResult result;
            try
            {
                result = await CreateCoreAsync(creation);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Setting up a Postgres server failed");
                result = new CreateResult(false, $"The Postgres server couldn't be set up: {ex.Message}", null, null, null);
            }
            creation.Enter(result.Ok ? "Done" : "Failed");
            audit?.Record(new Audit.AuditEntry("settings.databases.server.created", result.Ok
                ? $"Set up the Postgres server {result.Key} ({ServerImage}) for apps' databases."
                : $"Couldn't set up a Postgres server: {Audit.AuditLog.Clean(result.Message, 300)?.TrimEnd('.')}.")
            {
                Outcome = result.Ok ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
                AppId = result.AppId, AppName = result.Ok ? "Postgres" : null, Container = result.Key, Target = "Databases",
                Changes = [new("Image", null, ServerImage)],
            });
            return result;
        });
        _creation = creation;
        return creation;
    }

    private async Task<CreateResult> CreateCoreAsync(ServerCreation creation)
    {
        // Pulled first, so the create reads the image's own ports and volumes, and none of them is published.
        var pull = images!.Value.StartPull(ServerImage);
        var pulled = await pull.Completion;
        if (!pulled.Ok) return new CreateResult(false, pulled.Message, null, null, null);
        creation.Enter("Creating");
        var draft = await creator!.Value.DraftFromImageAsync(ServerImage, AppSource.Registry, null, CancellationToken.None);
        draft = ServerDraft(draft);
        var plan = await creator.Value.PlanAsync(draft, CancellationToken.None);
        if (!plan.CanCreate) return new CreateResult(false, plan.Problems.FirstOrDefault()?.Message ?? plan.Disk.Problem ?? "It can't be created as it is.", null, null, null);
        var job = creator.Value.Start(plan);
        job.Changed += () => creation.Enter(job.Phase);
        var result = await job.Completion;
        if (result.Ok)
        {
            Forget();
            // The first server Gitberth sets up is where new databases go, unless one was chosen.
            if (await settings.GetAsync(SettingKeys.DatabasesDefaultServer) is null)
                await settings.SetAsync(SettingKeys.DatabasesDefaultServer, plan.Container);
            result = result with { Key = plan.Container };
        }
        return result;
    }

    /// <summary>
    /// A server's draft from the image's (its generated POSTGRES_PASSWORD, marked secret; a named volume at its data path):
    /// called Postgres, its container named like its slug (postgres), every port inside only, updates that only tell.
    /// </summary>
    internal static NewAppDraft ServerDraft(NewAppDraft draft) => draft with
    {
        Name = "Postgres",
        ContainerIsSlug = true,
        Settings = draft.Settings with
        {
            Ports = draft.Settings.Ports.Select(p => p with { Access = PortAccess.Internal, HostPort = null, HostIp = null }).ToList(),
        },
        Options = draft.Options with { Name = "Postgres", WebPort = null, Updates = UpdatePolicy.Notify, Pinned = true },
    };
}
