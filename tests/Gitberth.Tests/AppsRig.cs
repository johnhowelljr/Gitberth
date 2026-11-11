using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// CT 101 for the unified apps' tests: its eight containers from the fixtures, plus any a test adds, answered alike by the
/// Engine API (DockerApi's handler) and the docker CLI (DockerCli.Runner), on a database with orb and kanban as the
/// migration leaves them (Git apps, pinned). Every call that would change something is recorded; the Engine API refuses
/// them all, and the CLI carries out the few a test allows (start, stop, restart, rm, volume rm, image rm).
/// </summary>
/// <remarks>
/// The database is a file in the rig's data folder, in WAL mode, and each context opens its own connection, as Gitberth's
/// does. One in-memory connection shared by every context broke whenever a background job (a change's follow-up, an
/// Activity write) used the database while the test did: EF Core registers its SQL functions on the connection each new
/// context gets, and SQLite refuses that while another statement on it is active ("unable to delete/modify user-function
/// due to active statements").
/// </remarks>
internal sealed class AppsRig : IDisposable
{
    public const string Self = "455b8ea31b6d";

    private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to GitHub or Cloudflare.");
    }

    private sealed class Stopping : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => new(canceled: true);
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    public AppsRig()
    {
        Options = new GitberthOptions { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-apps-" + Guid.NewGuid().ToString("N")[..8]), RunningInContainer = true, OwnContainer = Self };
        Directory.CreateDirectory(Options.DataDir);
        Db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Options.DbPath}").Options);
        using (var db = Db.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            var orb = new AppDefinition { Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", CurrentImage = "gitberth/orb:1d2948c", CurrentCommit = "1d2948cc693a", Pinned = true, AutoDeploy = true };
            orb.Ports.Add(new PortMapping { ContainerPort = 80, HostPort = 3000, IsWeb = true });
            var kanban = new AppDefinition { Name = "Kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:425df8a", CurrentCommit = "425df8a3bf54", Pinned = true, AutoDeploy = true };
            kanban.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true });
            db.Apps.AddRange(orb, kanban);
            db.SaveChanges();
            (ObsId, KanbanId) = (orb.Id, kanban.Id);
        }
        Protector = new SecretProtector(new EphemeralDataProtectionProvider());
        Settings = new SettingsService(Db, Protector);
        Engine = new HostEngine();
        Api = new DockerApi("unix:///var/run/docker.sock", Engine);
        Cli = new DockerCli(Options) { Runner = Engine.RunAsync };
        var http = new NoHttp();
        Domains = new DomainService(Db, Settings, new CloudflareApi(http), new TunnelLocator(Cli), Cli, NullLogger<DomainService>.Instance);
        Health = new HealthSettings(Settings);
        Titles = new ContainerTitles(Settings);
        Restarts = new RestartSchedules(Settings);
        Network = new NetworkService(Api, Db, Options, ContainerLocks, AppLocks);
        Apps = new AppService(Db, Protector, Cli, Settings, Health, Domains, Options, Restarts, new WatchState(), network: Network);
        Book = new AddressBook(Network, Apps, Domains, Db);
        Store = new StatsStore(Options);
        Events = new ContainerEventWatcher(Api, Store, NullLogger<ContainerEventWatcher>.Instance);
        Stats = new ContainerStatsCollector(Api, Health, Store, Events, NullLogger<ContainerStatsCollector>.Instance);
        Journal = new DbChangeJournal(Db);
        Activity = new ActivityLog(Db, Store);
        Catalog = new AppCatalog(Db, Network, Book, Apps, Stats, Titles, Health, Restarts, Journal, Cli, Domains, Protector, Options, Activity,
            new Lazy<UpdateService>(() => Updates ?? throw new InvalidOperationException("No update checks here.")), NullLogger<AppCatalog>.Instance)
        {
            ReadSettings = ReadSettings,
        };
        var hub = new LiveLogHub();
        var repoVariables = new RepoVariables(new GitHubApi(http), Settings);
        var pipeline = new DeployPipeline(Db, new GitClient(Options, Settings), Cli, new ProjectAnalyzer(), AppLocks, hub, Protector, Options, http, repoVariables,
            Domains, new WatchState(), new Stopping(), NullLogger<DeployPipeline>.Instance);
        Pipeline = pipeline;
        var queue = new DeployQueue(pipeline, hub, Db, new Stopping(), NullLogger<DeployQueue>.Instance);
        ContainerService? containers = null;
        Actions = new AppActions(Db, queue, Cli, AppLocks, Options, repoVariables, Domains, Restarts, Apps, NullLogger<AppActions>.Instance,
            Catalog, new Lazy<ContainerService>(() => containers!), changer: null, settings: null, Network, Titles, Health, PreviewGate)
        {
            Create = (request, _) =>
            {
                lock (Created) Created.Add(request);
                return Task.FromResult(CreateAnswer);
            },
        };
        Containers = containers = new ContainerService(Cli, Apps, Actions, Titles, Stats, Options, Db, ContainerLocks, changer: null, Network);
    }

    public int ObsId { get; }
    public int KanbanId { get; }
    /// <summary>The previews' gate AppActions holds while deleting an app's previews; a PreviewService here shares it.</summary>
    public Gitberth.Previews.PreviewGate PreviewGate { get; } = new();
    public IDbContextFactory<GitberthDb> Db { get; }
    public GitberthOptions Options { get; }
    public SecretProtector Protector { get; }
    public SettingsService Settings { get; }
    public HostEngine Engine { get; }
    public DockerApi Api { get; }
    public DockerCli Cli { get; }
    public DomainService Domains { get; }
    public HealthSettings Health { get; }
    public ContainerTitles Titles { get; }
    public RestartSchedules Restarts { get; }
    public ContainerLocks ContainerLocks { get; } = new();
    public AppLocks AppLocks { get; } = new();
    public NetworkService Network { get; }
    public AppService Apps { get; }
    public AddressBook Book { get; }
    public StatsStore Store { get; }
    public ContainerEventWatcher Events { get; }
    public ContainerStatsCollector Stats { get; }
    public DbChangeJournal Journal { get; }
    public ActivityLog Activity { get; }
    public AppCatalog Catalog { get; }
    public DeployPipeline Pipeline { get; }
    public AppActions Actions { get; }
    public ContainerService Containers { get; }

    /// <summary>The update badges, when a test has some; without, asking for them fails, as a broken UpdateService would.</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>What the change engine was asked to create (AppActions' create stands in for it).</summary>
    public List<NewContainerRequest> Created { get; } = [];

    /// <summary>What a create says: null when it started.</summary>
    public string? CreateAnswer { get; set; }

    /// <summary>ContainerSettingsJson.Read's stand-in: the image, environment, ports, mounts and restart policy as the inspect has them.</summary>
    public static ContainerSettings ReadSettings(JsonObject inspect, JsonObject? image) => new()
    {
        Image = ContainerPorts.Str(inspect["Config"], "Image"),
        Env = (inspect["Config"]?["Env"] as JsonArray ?? []).Select(e => (string)e!).Select(e => e.Split('=', 2)).Select(kv => new EnvSetting(kv[0], kv.Length > 1 ? kv[1] : "")).ToList(),
        Ports = ContainerPorts.Rules(inspect),
        Mounts = (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new MountSetting(
            ContainerPorts.Str(m, "Type") == "bind" ? MountKind.Bind : MountKind.Volume,
            ContainerPorts.Str(m, ContainerPorts.Str(m, "Type") == "bind" ? "Source" : "Name"), ContainerPorts.Str(m, "Destination"))).ToList(),
        Restart = new RestartSetting(ContainerPorts.Str(inspect["HostConfig"]?["RestartPolicy"], "Name")),
        Hostname = ContainerPorts.Str(inspect["Config"], "Hostname"),
    };

    public async Task<AppDefinition?> RowAsync(Func<AppDefinition, bool> which)
    {
        await using var db = Db.CreateDbContext();
        return (await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Networks).Include(a => a.Domains).AsSplitQuery().ToListAsync()).FirstOrDefault(which);
    }

    public async Task<int> CountAppsAsync()
    {
        await using var db = Db.CreateDbContext();
        return await db.Apps.CountAsync();
    }

    public async Task<int> AddAsync(AppDefinition app)
    {
        await using var db = Db.CreateDbContext();
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    public async Task AddAsync(object row)
    {
        await using var db = Db.CreateDbContext();
        db.Add(row);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        Engine.Dispose();
        // The pooled connections let go of the database file before its folder goes.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Options.DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The engine of <see cref="AppsRig"/>: CT 101's containers and any added, by the Engine API and the docker CLI.</summary>
internal sealed class HostEngine : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<JsonObject> _listed = [];
    private readonly Dictionary<string, JsonObject> _inspects = new(StringComparer.Ordinal);

    public HostEngine()
    {
        foreach (var listed in DockerFixtures.Json("containers-json.json").AsArray().OfType<JsonObject>())
        {
            var name = ((string)listed["Names"]![0]!).TrimStart('/');
            Know((JsonObject)listed.DeepClone(), DockerFixtures.Inspect(name));
        }
        // postgres:18 came from Docker Hub; the samples' images were built here.
        Images[ContainerPorts.Str(Inspect("postgres"), "Image")] = new JsonObject
        {
            ["Id"] = ContainerPorts.Str(Inspect("postgres"), "Image"),
            ["RepoTags"] = new JsonArray("postgres:18"),
            ["RepoDigests"] = new JsonArray("postgres@sha256:" + new string('d', 64)),
        };
    }

    /// <summary>Images by ID, as GET /images/{id}/json answers them; any other is missing.</summary>
    public Dictionary<string, JsonObject> Images { get; } = new(StringComparer.Ordinal);

    /// <summary>Every call to the Engine API, as "GET /v1.56/containers/json".</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every docker command run.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>The calls that would change something, by either way in: the Engine API's are refused.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>docker commands that fail, by their first words ("volume rm", "image rm"), with what they say.</summary>
    public Dictionary<string, string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>The volumes `docker volume ls` lists.</summary>
    public HashSet<string> Volumes { get; } = new(StringComparer.Ordinal);

    public JsonObject Inspect(string name)
    {
        lock (_gate) return _inspects[name];
    }

    public bool Has(string name)
    {
        lock (_gate) return _inspects.ContainsKey(name);
    }

    /// <summary>
    /// A container besides CT 101's, made from one of them (its image, ports, mounts and networks), under a new name and ID,
    /// with labels added or replaced, and in the state given.
    /// </summary>
    public string AddLike(string template, string name, IDictionary<string, string>? labels = null, string state = "running", string? configImage = null,
        string? listedImage = null)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        JsonObject listed, inspect;
        lock (_gate)
        {
            listed = (JsonObject)_listed.Single(l => ((string)l["Names"]![0]!).TrimStart('/') == template).DeepClone();
            inspect = (JsonObject)_inspects[template].DeepClone();
        }
        listed["Id"] = id;
        listed["Names"] = new JsonArray("/" + name);
        inspect["Id"] = id;
        inspect["Name"] = "/" + name;
        foreach (var (key, value) in labels ?? new Dictionary<string, string>())
        {
            ((JsonObject)(listed["Labels"] ??= new JsonObject()))[key] = value;
            ((JsonObject)(inspect["Config"]!["Labels"] ??= new JsonObject()))[key] = value;
        }
        if (configImage is not null) inspect["Config"]!["Image"] = configImage;
        if (listedImage is not null || configImage is not null) listed["Image"] = listedImage ?? configImage;
        SetState(listed, inspect, state);
        Know(listed, inspect);
        return id;
    }

    /// <summary>A container gone, as docker rm leaves it.</summary>
    public void Remove(string name)
    {
        lock (_gate)
        {
            if (!_inspects.TryGetValue(name, out var inspect)) return;
            var id = (string)inspect["Id"]!;
            _inspects.Remove(name);
            _inspects.Remove(id);
            _listed.RemoveAll(l => (string)l["Id"]! == id);
        }
    }

    /// <summary>The same name, a new container: docker run again, or compose up. Returns its ID.</summary>
    public string Recreate(string name)
    {
        JsonObject listed, inspect;
        lock (_gate)
        {
            inspect = (JsonObject)_inspects[name].DeepClone();
            listed = (JsonObject)_listed.Single(l => (string)l["Id"]! == (string)inspect["Id"]!).DeepClone();
        }
        Remove(name);
        var id = new string('f', 52) + Guid.NewGuid().ToString("N")[..12];
        inspect["Id"] = id;
        listed["Id"] = id;
        Know(listed, inspect);
        return id;
    }

    private void Know(JsonObject listed, JsonObject inspect)
    {
        lock (_gate)
        {
            _listed.Add(listed);
            _inspects[(string)inspect["Id"]!] = _inspects[((string)inspect["Name"]!).TrimStart('/')] = inspect;
        }
    }

    private static void SetState(JsonObject listed, JsonObject inspect, string state)
    {
        listed["State"] = state;
        listed["Status"] = state switch { "running" => "Up 2 hours", "exited" => "Exited (0) 3 hours ago", "created" => "Created", _ => state };
        inspect["State"] = new JsonObject
        {
            ["Status"] = state, ["Running"] = state == "running", ["ExitCode"] = 0, ["StartedAt"] = "2026-11-07T01:00:00.123456789Z", ["OOMKilled"] = false,
            ["FinishedAt"] = "0001-01-01T00:00:00Z",
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate) Requests.Add($"{request.Method} {path}");
        if (request.Method != HttpMethod.Get)
        {
            lock (_gate) Writes.Add($"engine {request.Method} {path}");
            return Task.FromResult(Answer("""{"message":"refused: the tests never change a container"}""", HttpStatusCode.InternalServerError));
        }
        var rest = path.StartsWith("/v1.56/", StringComparison.Ordinal) ? path["/v1.56/".Length..] : path.TrimStart('/');
        lock (_gate)
        {
            return Task.FromResult(rest switch
            {
                "version" => Answer(DockerFixtures.Text("version.json")),
                "containers/json" => Answer(new JsonArray([.. _listed.Select(l => (JsonNode?)l.DeepClone())]).ToJsonString()),
                "networks" => Answer(DockerFixtures.Text("networks-json.json")),
                _ when rest.StartsWith("containers/", StringComparison.Ordinal) && rest.EndsWith("/json", StringComparison.Ordinal)
                    => _inspects.TryGetValue(Uri.UnescapeDataString(rest["containers/".Length..^"/json".Length]), out var inspect)
                        ? Answer(inspect.ToJsonString()) : Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound),
                _ when rest.StartsWith("images/", StringComparison.Ordinal) && rest.EndsWith("/json", StringComparison.Ordinal)
                    => Images.TryGetValue(Uri.UnescapeDataString(rest["images/".Length..^"/json".Length]), out var image)
                        ? Answer(image.ToJsonString()) : Answer("""{"message":"No such image"}""", HttpStatusCode.NotFound),
                _ when rest.StartsWith("networks/", StringComparison.Ordinal)
                    => DockerFixtures.Json("networks-json.json").AsArray().OfType<JsonObject>().FirstOrDefault(n => (string)n["Name"]! == Uri.UnescapeDataString(rest["networks/".Length..])) is { } network
                        ? Answer(network.ToJsonString()) : Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound),
                _ => Answer("""{"message":"not here"}""", HttpStatusCode.NotFound),
            });
        }
    }

    /// <summary>Awaited before each docker command runs, with its line: a test holds one while something else happens.</summary>
    public Func<string, Task>? BeforeCommand { get; set; }

    /// <summary>The docker CLI, answered from the same containers.</summary>
    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
    {
        var line = string.Join(' ', args);
        if (BeforeCommand is { } before) await before(line);
        lock (_gate)
        {
            Commands.Add(line);
            return (args switch
            {
                ["ps", ..] => Ok(string.Concat(_listed.Where(l => !args.Contains("label=gitberth.managed=true") || ContainerPorts.Str(l["Labels"], "gitberth.managed") == "true")
                    .Select(PsLine))),
                ["container", "inspect", "--format", "{{.Id}}", var name] => _inspects.TryGetValue(name, out var i) ? Ok((string)i["Id"]! + "\n") : Missing(name),
                ["container", "inspect", "--format", "{{json .Config.StopTimeout}} {{json .Config.Image}}", var name] =>
                    _inspects.TryGetValue(name, out var i) ? Ok($"null {JsonSerializer.Serialize(ContainerPorts.Str(i["Config"], "Image"))}\n") : Missing(name),
                ["container", "inspect", "--format", "{{.State.Running}} {{.State.StartedAt}}", var name] =>
                    _inspects.TryGetValue(name, out var i) ? Ok($"{(ContainerPorts.Running(i) ? "true" : "false")} {ContainerPorts.Str(i["State"], "StartedAt")}\n") : Missing(name),
                ["container", "inspect", ..] when !args.Contains("--format") => InspectAll(args.Skip(2).ToList()),
                ["volume", "ls", ..] => Ok(string.Concat(Volumes.Order(StringComparer.Ordinal).Select(v => v + "\n"))),
                ["start" or "stop" or "restart" or "rm" or "volume" or "image" or "update" or "network" or "rename" or "run" or "create", ..] => Write(args, line),
                _ => Ok(""),
            });
        }
    }

    private ProcessResult InspectAll(IReadOnlyList<string> names)
    {
        var found = names.Where(_inspects.ContainsKey).Select(n => (JsonNode?)_inspects[n].DeepClone()).ToArray();
        var json = new JsonArray(found).ToJsonString();
        return found.Length == names.Count ? Ok(json) : new ProcessResult(1, json, "Error: No such container: " + string.Join(' ', names.Where(n => !_inspects.ContainsKey(n))));
    }

    private ProcessResult Write(IReadOnlyList<string> args, string line)
    {
        Writes.Add("docker " + line);
        if (Failing.FirstOrDefault(f => line.StartsWith(f.Key, StringComparison.Ordinal)) is { Key: not null } failing) return new ProcessResult(1, "", failing.Value);
        var name = args[^1];
        switch (args[0])
        {
            case "rm":
                if (!_inspects.TryGetValue(name, out var gone)) return Missing(name);
                var id = (string)gone["Id"]!;
                _inspects.Remove(name);
                _inspects.Remove(id);
                _listed.RemoveAll(l => (string)l["Id"]! == id);
                return Ok(name + "\n");
            case "start" or "restart":
                if (!_inspects.TryGetValue(name, out var started)) return Missing(name);
                SetState(_listed.Single(l => (string)l["Id"]! == (string)started["Id"]!), started, "running");
                return Ok(name + "\n");
            case "stop":
                if (!_inspects.TryGetValue(name, out var stopped)) return Missing(name);
                SetState(_listed.Single(l => (string)l["Id"]! == (string)stopped["Id"]!), stopped, "exited");
                return Ok(name + "\n");
            default:
                return Ok("");
        }
    }

    // A docker ps line: {"ID","Names","Image","State","Status","Ports","Labels","Command"}.
    private static string PsLine(JsonObject l)
    {
        var ports = string.Join(", ", (l["Ports"] as JsonArray ?? []).OfType<JsonObject>().Select(p =>
            p["PublicPort"] is JsonValue pub
                ? $"{(ContainerPorts.Str(p, "IP") is { Length: > 0 } ip ? AddressBook.HostPart(ip) + ":" : "")}{pub.GetValue<int>().ToString(CultureInfo.InvariantCulture)}->{p["PrivatePort"]!.GetValue<int>()}/{ContainerPorts.Str(p, "Type")}"
                : $"{p["PrivatePort"]!.GetValue<int>()}/{ContainerPorts.Str(p, "Type")}"));
        var labels = string.Join(',', (l["Labels"] as JsonObject ?? []).Select(kv => $"{kv.Key}={(string?)kv.Value}"));
        return new JsonObject
        {
            ["ID"] = (string)l["Id"]!,
            ["Names"] = ((string)l["Names"]![0]!).TrimStart('/'),
            ["Image"] = ContainerPorts.Str(l, "Image"),
            ["State"] = ContainerPorts.Str(l, "State"),
            ["Status"] = ContainerPorts.Str(l, "Status"),
            ["Ports"] = ports,
            ["Labels"] = labels,
            ["Command"] = "\"run\"",
        }.ToJsonString() + "\n";
    }

    private static ProcessResult Ok(string output) => new(0, output, "");

    private static ProcessResult Missing(string name) => new(1, "", "Error: No such container: " + name);

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
