using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Health;
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
/// Deploying an app that has a domain: the new version starts next to the old one, the tunnel moves to it, and only once
/// cloudflared runs that does the old one stop. A docker kept in memory and a stand-in Cloudflare write every step that
/// changes something into one timeline.
/// </summary>
public sealed class ZeroDowntimeTests : IAsyncLifetime
{
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string TunnelId = "11111111-2222-4333-8444-555555555555";
    private const string Hostname = "app.example.com";
    private const string NewCommit = "2222222222222222222222222222222222222222";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class Container
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public required string Name { get; set; }
        public required string Image { get; init; }
        public bool Running { get; set; } = true;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow.AddHours(-1);
        /// <summary>What it publishes: on every address when Ip is empty, as -p 3001:3000 does.</summary>
        public List<(string Ip, int HostPort, int ContainerPort)> Ports { get; init; } = [];
        /// <summary>Ports it only exposes (--expose).</summary>
        public List<int> Exposed { get; init; } = [];
        /// <summary>The networks it's on, with what it answers to there besides its name.</summary>
        public Dictionary<string, List<string>> Networks { get; init; } = new() { ["gitberth"] = [] };
        public List<string> Env { get; init; } = [];
        public Dictionary<string, string> Labels { get; init; } = new() { ["gitberth.managed"] = "true" };
    }

    /// <summary>
    /// Enough of the docker CLI for a swap and a scheduled restart: run (with --network name=…, -p [ip:]host:container and
    /// --expose), stop, start, restart, rename, rm, ps, inspect, logs, images, networks and the engine's API version. Like
    /// Docker 29's CLI, run lower-cases each --network name=…,alias=… field, values and all; like the engine, it finds a
    /// network by its exact name or its ID.
    /// </summary>
    private sealed class MemoryDocker(List<string> timeline)
    {
        public List<Container> Containers { get; } = [];
        /// <summary>Each image's labels, as `docker image inspect` prints them.</summary>
        public Dictionary<string, string> Images { get; } = new(StringComparer.Ordinal);
        /// <summary>The VOLUMEs an image declares, as `docker image inspect` prints them; none when it isn't here.</summary>
        public Dictionary<string, string> ImageVolumes { get; } = new(StringComparer.Ordinal);
        /// <summary>The volumes `docker volume ls` lists.</summary>
        public HashSet<string> Volumes { get; } = new(StringComparer.Ordinal);
        /// <summary>The networks there are, by name; each one's ID is <see cref="IdOf"/>.</summary>
        public HashSet<string> Networks { get; } = ["gitberth"];

        /// <summary>A network's ID: 64 hex digits, as Docker's are.</summary>
        public static string IdOf(string network) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(network)));

        private string? NetworkNamed(string nameOrId) => Networks.FirstOrDefault(n => n == nameOrId || IdOf(n) == nameOrId);
        /// <summary>Every `docker run`, as it was given.</summary>
        public List<List<string>> Runs { get; } = [];
        public List<string> TunnelLog { get; } = [];
        public string TunnelInspect { get; set; } = "";
        /// <summary>Containers that exit as soon as they start.</summary>
        public Func<Container, bool> Crashes { get; set; } = _ => false;
        /// <summary>Called with each command that changes a container (as the timeline writes it), before it runs.</summary>
        public Action<string>? OnChange { get; set; }

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
        {
            lock (this) return Task.FromResult(Run(a, options));
        }

        private static ProcessResult Ok(string output = "") => new(0, output, "");
        private static ProcessResult Fail(string error) => new(1, "", error);
        private Container? Find(string name) => Containers.FirstOrDefault(c => c.Name == name);

        private ProcessResult Run(IReadOnlyList<string> a, ProcessOptions? options)
        {
            switch (a[0])
            {
                case "version":
                    return Ok("1.56\n");
                case "network" when a[1] == "inspect":
                    return NetworkNamed(a[^1]) is { } network ? Ok(a.Contains("{{.Id}}") ? IdOf(network) : network) : Fail($"Error response from daemon: network {a[^1]} not found");
                case "network" when a[1] == "connect" && a.Contains("--alias"):
                    // How a new version that passed its checks gets the app's name: off the network, and on again with it.
                    if (Find(a[^1]) is { } aliased && aliased.Networks.TryGetValue(a[^2], out var names)) names.AddRange(Values(a, "--alias"));
                    return Ok();
                case "network":
                    return Ok();
                case "info":
                    return Ok("json-file\n");
                case "image" when a[1] == "inspect":
                    if (!Images.TryGetValue(a[^1], out var labels)) return Fail("Error: No such image");
                    if (a[3] == "{{json .Config.Volumes}}") return Ok(ImageVolumes.TryGetValue(a[^1], out var volumes) ? volumes : "null");
                    return Ok(a[3] == "{{json .Config.Labels}}" ? labels : "sha256:1");
                case "image" when a[1] == "ls":
                    return Ok(string.Join('\n', Images.Keys.Where(k => k.StartsWith(a[2] + ":", StringComparison.Ordinal))));
                case "image" when a[1] == "rm":
                    return Images.Remove(a[^1]) ? Ok() : Fail("Error: No such image");
                case "image":
                case "update":
                    return Ok();
                case "container" when a.Contains("--format"):
                    if (Find(a[^1]) is not { } found) return Fail("Error: No such container");
                    return Ok(a[3].Contains("StartedAt", StringComparison.Ordinal)
                        ? $"{(found.Running ? "true" : "false")} {found.StartedAt:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}"
                        : "id");
                case "container":
                    if (a[^1] == "tunnel") return Ok(TunnelInspect);
                    if (Find(a[^1]) is not { } inspected) return Fail("Error: No such container");
                    var running = inspected.Running ? "true" : "false";
                    return Ok($$"""[{"State":{"Running":{{running}},"Status":"{{(inspected.Running ? "running" : "exited")}}","ExitCode":{{(inspected.Running ? 0 : 1)}},"OOMKilled":false},"RestartCount":0}]""");
                case "ps":
                    return Ok(Ps(a.Contains("label=gitberth.managed=true")));
                case "logs" when a[1] == "--since":
                    return Ok(string.Join("\n", TunnelLog));
                case "logs":
                    return Ok();
                case "run":
                    return RunContainer([.. a], options);
                case "stop":
                    OnChange?.Invoke($"stop {a[^1]}");
                    timeline.Add($"stop {a[^1]}");
                    if (Find(a[^1]) is not { } stopped) return Fail("Error: No such container");
                    stopped.Running = false;
                    return Ok();
                case "start":
                case "restart":
                    timeline.Add($"{a[0]} {a[^1]}");
                    if (Find(a[^1]) is not { } started) return Fail("Error: No such container");
                    started.Running = !Crashes(started);
                    started.StartedAt = DateTime.UtcNow;
                    return Ok();
                case "rename":
                    timeline.Add($"rename {a[1]} {a[2]}");
                    if (Find(a[1]) is not { } renamed || Find(a[2]) is not null) return Fail("Error: rename failed");
                    renamed.Name = a[2];
                    return Ok();
                case "rm":
                    if (Containers.RemoveAll(c => c.Name == a[^1]) == 0) return Fail("Error: No such container");
                    timeline.Add($"rm {a[^1]}");
                    return Ok();
                case "volume" when a[1] == "ls":
                    return Ok(string.Concat(Volumes.Order(StringComparer.Ordinal).Select(v => v + "\n")));
                default:
                    return Fail("unknown command " + string.Join(' ', a));
            }
        }

        private ProcessResult RunContainer(List<string> a, ProcessOptions? options)
        {
            Runs.Add(a);
            var name = a[a.IndexOf("--name") + 1];
            var ports = Values(a, "-p").Select(p => p.Split('/')[0].Split(':'))
                .Select(p => (Ip: string.Join(':', p[..^2]), HostPort: int.Parse(p[^2]), ContainerPort: int.Parse(p[^1]))).ToList();
            timeline.Add($"run {name} {string.Join(',', ports.Select(p => p.Ip.Length > 0 ? $"{p.Ip}:{p.HostPort}" : $"{p.HostPort}"))}");
            OnChange?.Invoke($"run {name}");
            if (Find(name) is not null) return Fail($"Conflict. The container name \"/{name}\" is already in use.");
            // A binding on every address takes the port on all of them, 127.0.0.1 included.
            var taken = Containers.Where(c => c.Running).SelectMany(c => c.Ports).ToList();
            if (ports.Any(p => taken.Any(t => t.HostPort == p.HostPort && ContainerPorts.Overlap(t.Ip, p.Ip)))) return Fail("Bind for 0.0.0.0 failed: port is already allocated");
            var networks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var fields in Values(a, "--network").Select(n => (n.Contains('=') ? n.ToLowerInvariant() : n).Split(',')))
            {
                var asked = fields[0].StartsWith("name=", StringComparison.Ordinal) ? fields[0]["name=".Length..] : fields[0];
                // Filed under the network's own name, whichever way it was asked for.
                if (NetworkNamed(asked) is not { } network) return Fail($"Error response from daemon: network {asked} not found");
                networks[network] = fields.Where(o => o.StartsWith("alias=", StringComparison.Ordinal)).Select(o => o["alias=".Length..]).ToList();
            }
            var env = Values(a, "-e").Select(e => e.Contains('=') ? e : $"{e}={options?.Environment?[e]}").ToList();
            var labels = Values(a, "--label").Select(l => l.Split('=', 2)).ToDictionary(l => l[0], l => l[1]);
            var created = new Container
            {
                Name = name, Image = a[^1], Ports = ports, Exposed = Values(a, "--expose").Select(e => int.Parse(e.Split('/')[0])).ToList(),
                Networks = networks, Env = env, StartedAt = DateTime.UtcNow, Labels = labels,
            };
            created.Running = !Crashes(created);
            Containers.Add(created);
            return Ok("id");
        }

        private string Ps(bool managedOnly)
        {
            var lines = new List<string>();
            if (!managedOnly)
                lines.Add("""{"Names":"tunnel","State":"running","Status":"Up 1 hour","Image":"cloudflare/cloudflared:latest","Ports":"","Labels":"","Command":"cloudflared tunnel run"}""");
            foreach (var c in Containers)
            {
                lines.Add(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["Names"] = c.Name,
                    ["State"] = c.Running ? "running" : "exited",
                    ["Status"] = c.Running ? "Up 1 minute" : "Exited (1)",
                    ["Image"] = c.Image,
                    // As docker ps writes them: one on every address on both IPv4 and IPv6, then the ports it only exposes.
                    ["Ports"] = c.Running
                        ? string.Join(", ", c.Ports.SelectMany(p => (p.Ip.Length == 0 ? ["0.0.0.0", "[::]"] : new[] { p.Ip }).Select(ip => $"{ip}:{p.HostPort}->{p.ContainerPort}/tcp"))
                            .Concat(c.Exposed.Where(e => c.Ports.All(p => p.ContainerPort != e)).Select(e => $"{e}/tcp")))
                        : "",
                    ["Labels"] = string.Join(',', c.Labels.Select(l => $"{l.Key}={l.Value}")),
                    ["Command"] = "node server.js",
                }));
            }
            return string.Join('\n', lines);
        }
    }

    /// <summary>Every value given to a flag, in order: Values(args, "--network").</summary>
    private static List<string> Values(IReadOnlyList<string> args, string flag) =>
        args.Select((arg, i) => (arg, i)).Where(x => x.arg == flag && x.i + 1 < args.Count).Select(x => args[x.i + 1]).ToList();

    /// <summary>
    /// The same containers as the Engine API shows them, for the host-port map: Docker's list, with what each publishes,
    /// and each one's inspect, with the bindings it was created with.
    /// </summary>
    private sealed class MemoryEngine(MemoryDocker docker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? body;
            lock (docker)
            {
                body = path == "/version" ? DockerFixtures.Text("version.json")
                    : path.EndsWith("/containers/json", StringComparison.Ordinal) ? new JsonArray([.. docker.Containers.Select(Listed)]).ToJsonString()
                    : docker.Containers.FirstOrDefault(c => path.EndsWith($"/containers/{c.Id}/json", StringComparison.Ordinal)) is { } c ? Inspect(c).ToJsonString()
                    : null;
            }
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such container"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static JsonNode Listed(Container c) => new JsonObject
        {
            ["Id"] = c.Id,
            ["Names"] = new JsonArray("/" + c.Name),
            ["Image"] = c.Image,
            ["ImageID"] = "sha256:1",
            ["State"] = c.Running ? "running" : "exited",
            ["Status"] = c.Running ? "Up 1 minute" : "Exited (1)",
            ["Command"] = "node server.js",
            ["Labels"] = Labels(c),
            ["Ports"] = c.Running
                ? new JsonArray([.. c.Ports.SelectMany(p => (p.Ip.Length == 0 ? ["0.0.0.0", "::"] : new[] { p.Ip })
                    .Select(ip => (JsonNode)new JsonObject { ["IP"] = ip, ["PrivatePort"] = p.ContainerPort, ["PublicPort"] = p.HostPort, ["Type"] = "tcp" }))])
                : new JsonArray(),
            ["HostConfig"] = new JsonObject { ["NetworkMode"] = c.Networks.Keys.FirstOrDefault() ?? "none" },
            ["NetworkSettings"] = new JsonObject { ["Networks"] = Endpoints(c) },
        };

        private static JsonNode Inspect(Container c)
        {
            var bindings = new JsonObject();
            foreach (var port in c.Ports.GroupBy(p => p.ContainerPort))
                bindings[$"{port.Key}/tcp"] = new JsonArray([.. port.Select(p => (JsonNode)new JsonObject { ["HostIp"] = p.Ip, ["HostPort"] = p.HostPort.ToString(CultureInfo.InvariantCulture) })]);
            var exposed = new JsonObject();
            foreach (var port in c.Ports.Select(p => p.ContainerPort).Concat(c.Exposed).Distinct()) exposed[$"{port}/tcp"] = new JsonObject();
            return new JsonObject
            {
                ["Id"] = c.Id,
                ["Name"] = "/" + c.Name,
                ["Config"] = new JsonObject { ["Image"] = c.Image, ["ExposedPorts"] = exposed, ["Labels"] = Labels(c) },
                ["HostConfig"] = new JsonObject { ["PortBindings"] = bindings, ["PublishAllPorts"] = false, ["NetworkMode"] = c.Networks.Keys.FirstOrDefault() ?? "none" },
                ["State"] = new JsonObject { ["Running"] = c.Running },
                ["NetworkSettings"] = new JsonObject { ["Networks"] = Endpoints(c) },
            };
        }

        private static JsonObject Labels(Container c) => new([.. c.Labels.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))]);

        private static JsonObject Endpoints(Container c) =>
            new([.. c.Networks.Select(n => KeyValuePair.Create(n.Key, (JsonNode?)new JsonObject { ["Aliases"] = new JsonArray([.. n.Value.Select(a => (JsonNode?)a)]) }))]);
    }

    /// <summary>The tunnel's configuration as Cloudflare keeps it. cloudflared logs each version it takes, after <see cref="ConfirmAfter"/>.</summary>
    private sealed class Cloudflare(List<string> timeline, MemoryDocker docker) : HttpMessageHandler
    {
        public JsonArray Ingress { get; set; } = [];
        public int Version { get; private set; } = 10;
        public bool FailPuts { get; set; }
        public TimeSpan ConfirmAfter { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/configurations", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Ok(new JsonObject { ["config"] = new JsonObject { ["ingress"] = Ingress.DeepClone() } }.ToJsonString());
            if (path.EndsWith("/configurations", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                int version;
                lock (docker)
                {
                    if (FailPuts)
                    {
                        timeline.Add("PUT failed");
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }
                    Ingress = (JsonArray)body["config"]!["ingress"]!.DeepClone();
                    version = ++Version;
                    timeline.Add("PUT " + string.Join(' ', Ingress.OfType<JsonObject>().Where(r => r["hostname"] is not null).Select(r => (string)r["service"]!)));
                }
                _ = Task.Delay(ConfirmAfter, CancellationToken.None).ContinueWith(_ =>
                {
                    lock (docker)
                    {
                        docker.TunnelLog.Add($"2026-11-04T10:00:00Z INF Updated to new configuration config=\"{{}}\" version={version}");
                        timeline.Add($"cloudflared runs {version}");
                    }
                }, TaskScheduler.Default);
                return Ok($$"""{"tunnel_id":"{{TunnelId}}","version":{{version}}}""");
            }
            if (path.EndsWith("/zones", StringComparison.Ordinal)) return Ok("[]");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string resultJson) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true,\"result\":" + resultJson + "}", Encoding.UTF8, "application/json") };
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _timeline = [];
    private readonly Db _db;
    private readonly GitberthOptions _options;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly SettingsService _settings;
    private readonly MemoryDocker _docker;
    private readonly Cloudflare _cloudflare;
    private readonly DomainService _domains;
    private readonly DockerApi _api;
    private readonly DeployPipeline _pipeline;
    private readonly RestartSchedules _restarts;
    private readonly RestartScheduler _restarter;
    private readonly AppService _apps;
    private readonly AppActions _actions;
    private int _appId;

    public ZeroDowntimeTests()
    {
        _options = new GitberthOptions { DataDir = _dir };
        Directory.CreateDirectory(_options.LogsDir);
        // A database file, as Gitberth has: each context gets its own connection, so a deploy running on the queue's
        // thread and the test reading its outcome don't share one.
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options);
        _settings = new SettingsService(_db, _protector);
        _docker = new MemoryDocker(_timeline);
        _cloudflare = new Cloudflare(_timeline, _docker);
        var docker = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new Factory(_cloudflare);
        _domains = new DomainService(_db, _settings, new CloudflareApi(http), new TunnelLocator(docker), docker, NullLogger<DomainService>.Instance)
        {
            TunnelPoll = TimeSpan.FromMilliseconds(5),
        };
        _restarts = new RestartSchedules(_settings);
        var watch = new WatchState();
        _apps = new AppService(_db, _protector, docker, _settings, new HealthSettings(_settings), _domains, _options, _restarts, watch);
        var locks = new AppLocks();
        var hub = new LiveLogHub();
        var repoVariables = new RepoVariables(new GitHubApi(http), _settings);
        // The spare port comes from the host-port map, which reads the same containers through the Engine API. The host's
        // sockets aren't read here.
        _api = new DockerApi("unix:///var/run/docker.sock", new MemoryEngine(_docker));
        var hostPorts = new HostPortMap(new NetworkService(_api, _db, _options), _db, _domains,
            new HostScanner(docker, _options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false }, _settings,
            new ContainerEventWatcher(_api, new StatsStore(_options), NullLogger<ContainerEventWatcher>.Instance), NullLogger<HostPortMap>.Instance);
        _pipeline = new DeployPipeline(_db, new GitClient(_options, _settings), docker, new ProjectAnalyzer(), locks, hub, _protector,
            _options, http, repoVariables, _domains, watch, new Lifetime(), NullLogger<DeployPipeline>.Instance, hostPorts)
        {
            StableFor = TimeSpan.FromMilliseconds(40),
            StatePoll = TimeSpan.FromMilliseconds(5),
            TunnelWait = TimeSpan.FromSeconds(5),
            // Nothing listens in the in-memory docker.
            ListenWait = TimeSpan.Zero,
        };
        var queue = new DeployQueue(_pipeline, hub, _db, new Lifetime(), NullLogger<DeployQueue>.Instance);
        _actions = new AppActions(_db, queue, docker, locks, _options, repoVariables, _domains, _restarts, _apps, NullLogger<AppActions>.Instance);
        _restarter = new RestartScheduler(_db, docker, _restarts, _actions, locks, NullLogger<RestartScheduler>.Instance);
    }

    public async Task InitializeAsync()
    {
        await using (var db = _db.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var app = new AppDefinition { Name = "Web", Slug = "web", Owner = "acme", Repo = "web", CurrentImage = "gitberth/web:1", CurrentCommit = "1111111" };
            app.Ports.Add(new PortMapping { HostPort = 3001, ContainerPort = 3000, IsWeb = true });
            app.Domains.Add(new CustomDomain { Hostname = Hostname, Service = "http://localhost:3001" });
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            _appId = app.Id;
        }
        await _settings.SetSecretAsync(SettingKeys.CloudflareToken, "token");
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "true");

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"{{AccountId}}","t":"{{TunnelId}}","s":"c2VjcmV0"}"""));
        _docker.TunnelInspect = """[{"Id":"t1","Name":"/tunnel","Args":["tunnel","run","--token","TOKEN"],"Config":{"Cmd":[],"Env":[]},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{"host":{}}}}]"""
            .Replace("TOKEN", token, StringComparison.Ordinal);
        _docker.Containers.Add(new Container { Name = "gb-web", Image = "gitberth/web:1", Ports = [("", 3001, 3000)], Networks = new() { ["gitberth"] = ["web"] } });
        _docker.Images["gitberth/web:1"] = "{}";
        _docker.Images["gitberth/web:2"] = """{"gitberth.traits":"1","gitberth.env-prefix":"NEXT_PUBLIC_","gitberth.authjs":"true"}""";
        _cloudflare.Ingress = [new JsonObject { ["hostname"] = Hostname, ["service"] = "http://localhost:3001" }, new JsonObject { ["service"] = "http_status:404" }];
    }

    public Task DisposeAsync()
    {
        _api.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> BridgeLabels => new() { ["gitberth.managed"] = "true", ["gitberth.app"] = "web", ["gitberth.bridge"] = "true" };

    private List<string> Timeline()
    {
        lock (_docker) return [.. _timeline];
    }

    private string? TunnelService(string hostname)
    {
        lock (_docker) return _cloudflare.Ingress.OfType<JsonObject>().Where(r => (string?)r["hostname"] == hostname).Select(r => (string?)r["service"]).SingleOrDefault();
    }

    private async Task<(Deployment Deployment, string Log)> RedeployAsync(string image = "gitberth/web:2", CancellationToken ct = default)
    {
        int id;
        using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = _appId, Trigger = DeployTrigger.ConfigChange, ImageTag = image, CommitSha = NewCommit, CommitMessage = "New footer", CommitAuthor = "Ada" };
            db.Deployments.Add(dep);
            db.SaveChanges();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(_appId, id, Build: false, Image: image), ct);
        using var after = _db.CreateDbContext();
        return (after.Deployments.Single(d => d.Id == id), File.ReadAllText(DeployPaths.LogFile(_options, id)));
    }

    [Fact]
    public async Task The_old_version_serves_until_cloudflared_runs_the_route_to_the_new_one()
    {
        // cloudflared takes a moment to pick up a change: nothing stops before it has.
        _cloudflare.ConfirmAfter = TimeSpan.FromMilliseconds(300);

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(
        [
            "run gb-web_next 127.0.0.1:8100",
            "PUT http://127.0.0.1:8100",
            "cloudflared runs 11",
            "stop gb-web",
            "rename gb-web gb-web_prev",
            "run gb-web 3001",
            "PUT http://localhost:3001",
            "cloudflared runs 12",
            "rm gb-web_next",
            "rm gb-web_prev",
        ], Timeline());
        var web = Assert.Single(_docker.Containers);
        Assert.Equal(("gb-web", "gitberth/web:2", true), (web.Name, web.Image, web.Running));
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.Contains("so visitors are served throughout", log);
    }

    [Fact]
    public async Task A_VOLUME_the_image_declares_becomes_a_named_volume_of_the_app_kept_from_one_deploy_to_the_next()
    {
        // VOLUME /data and /var/cache/app/ in its Dockerfile: without mounts of their own, each container would get empty
        // anonymous volumes, and the old ones would be left behind.
        _docker.ImageVolumes["gitberth/web:2"] = """{"/data":{},"/var/cache/app/":{}}""";

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        var run = _docker.Runs.Last();
        Assert.Contains("gb-web-data:/data", run);
        Assert.Contains("gb-web-app:/var/cache/app", run);
        // Two copies never write one volume at once: the old version stops first, as for any app with a volume.
        Assert.DoesNotContain("run gb-web_next 127.0.0.1:8100", Timeline());
        Assert.Contains("The image declares VOLUME /data: it's kept in the volume gb-web-data", log);
        await using (var db = _db.CreateDbContext())
        {
            var app = await db.Apps.Include(a => a.Volumes).SingleAsync(a => a.Id == _appId);
            Assert.Equal([("app", "/var/cache/app"), ("data", "/data")], app.Volumes.OrderBy(v => v.Source).Select(v => (v.Source, v.Target)));
            // What runs is what's saved: nothing is left waiting to be applied.
            Assert.False(app.ConfigChangePending);
        }

        // The next deploy finds them there: none is added again.
        var (again, log2) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, again.Status);
        Assert.Contains("gb-web-data:/data", _docker.Runs.Last());
        Assert.DoesNotContain("The image declares VOLUME", log2);
        await using (var db = _db.CreateDbContext())
            Assert.Equal(2, await db.Volumes.CountAsync(v => v.AppId == _appId));
    }

    [Fact]
    public async Task A_VOLUME_the_app_keeps_in_a_tmpfs_gets_no_volume_as_docker_takes_one_mount_at_a_path()
    {
        // VOLUME /data and /cache; the app's settings keep /cache in memory.
        _docker.ImageVolumes["gitberth/web:2"] = """{"/data":{},"/cache":{}}""";
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.RuntimeExtras,
                Gitberth.Apps.GitExtras.Write(new Gitberth.Apps.GitExtras { Tmpfs = [new Gitberth.Apps.GitTmpfs("/cache/")] })));

        var (dep, log) = await RedeployAsync();

        Assert.True(dep.Status == DeployStatus.Succeeded, dep.Error);
        var run = _docker.Runs.Last();
        Assert.Contains("gb-web-data:/data", run);
        Assert.Equal("/cache/", run[run.IndexOf("--tmpfs") + 1]);
        Assert.DoesNotContain(run, a => a.Contains(":/cache", StringComparison.Ordinal));
        Assert.DoesNotContain("VOLUME /cache", log);
        await using (var db = _db.CreateDbContext())
            Assert.Equal(["data"], await db.Volumes.Where(v => v.AppId == _appId).Select(v => v.Source).ToListAsync());
    }

    [Fact]
    public async Task A_new_version_that_fails_its_checks_never_interrupts_the_running_one()
    {
        _docker.Crashes = c => c.Name == "gb-web_next";

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.RolledBack, dep.Status);
        Assert.Equal(["run gb-web_next 127.0.0.1:8100", "rm gb-web_next"], Timeline());
        var web = Assert.Single(_docker.Containers);
        Assert.Equal(("gb-web", "gitberth/web:1", true), (web.Name, web.Image, web.Running));
        Assert.Contains("the current one kept running throughout", log);
    }

    [Fact]
    public async Task When_the_tunnel_cannot_be_changed_the_usual_swap_runs()
    {
        _cloudflare.FailPuts = true;

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["run gb-web_next 127.0.0.1:8100", "PUT failed", "rm gb-web_next", "stop gb-web", "rename gb-web gb-web_prev", "run gb-web 3001", "rm gb-web_prev"], Timeline());
        Assert.Equal("gitberth/web:2", Assert.Single(_docker.Containers).Image);
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
    }

    [Fact]
    public async Task When_the_new_version_fails_on_the_apps_own_port_the_old_one_comes_back_and_so_do_visitors()
    {
        _docker.Crashes = c => c.Name == "gb-web" && c.Image == "gitberth/web:2";

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.RolledBack, dep.Status);
        Assert.Equal(
        [
            "run gb-web_next 127.0.0.1:8100", "PUT http://127.0.0.1:8100", "cloudflared runs 11",
            "stop gb-web", "rename gb-web gb-web_prev", "run gb-web 3001",
            "rm gb-web", "rename gb-web_prev gb-web", "start gb-web",
            "PUT http://localhost:3001", "cloudflared runs 12", "rm gb-web_next",
        ], Timeline());
        var web = Assert.Single(_docker.Containers);
        Assert.Equal(("gb-web", "gitberth/web:1", true), (web.Name, web.Image, web.Running));
    }

    [Fact]
    public async Task An_app_with_a_volume_swaps_the_usual_way()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Volumes.Add(new VolumeMount { AppId = _appId, Source = "data", Target = "/app/data" });
            db.SaveChanges();
        }

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["stop gb-web", "rename gb-web gb-web_prev", "run gb-web 3001", "rm gb-web_prev"], Timeline());
        Assert.Contains("the app has a volume", log);
    }

    [Fact]
    public async Task The_app_is_told_its_address_and_commit_and_its_own_values_win()
    {
        using (var db = _db.CreateDbContext())
        {
            db.EnvVars.Add(new EnvVar { AppId = _appId, Key = "VERCEL_ENV", ProtectedValue = _protector.Protect("staging") });
            db.SaveChanges();
        }

        var (_, log) = await RedeployAsync();

        var env = Assert.Single(_docker.Containers).Env;
        Assert.Contains("VERCEL_URL=app.example.com", env);
        Assert.Contains("VERCEL_PROJECT_PRODUCTION_URL=app.example.com", env);
        Assert.Contains("NEXT_PUBLIC_VERCEL_URL=app.example.com", env);
        Assert.Contains($"VERCEL_GIT_COMMIT_SHA={NewCommit}", env);
        Assert.Contains("VERCEL_GIT_COMMIT_MESSAGE=New footer", env);
        Assert.Contains("AUTH_URL=https://app.example.com", env);
        Assert.Contains("NEXTAUTH_URL=https://app.example.com", env);
        Assert.Contains("VERCEL_ENV=staging", env);
        Assert.DoesNotContain("VERCEL_ENV=production", env);
        Assert.DoesNotContain(env, e => e.StartsWith("VERCEL=", StringComparison.Ordinal));
        Assert.Contains("AUTH_URL and NEXTAUTH_URL are https://app.example.com", log);
    }

    [Fact]
    public async Task A_temporary_container_left_behind_goes_once_visitors_are_back_on_the_app()
    {
        _docker.Containers.Add(new Container { Name = "gb-web_next", Image = "gitberth/web:2", Ports = [("", 8100, 3000)], Labels = BridgeLabels });
        _cloudflare.Ingress[0]!["service"] = "http://localhost:8100";
        using (var db = _db.CreateDbContext())
        {
            db.Domains.Single().Service = "http://localhost:8100";
            db.SaveChanges();
        }

        await _pipeline.TidyBridgesAsync();

        Assert.Equal(["PUT http://localhost:3001", "cloudflared runs 11", "rm gb-web_next"], Timeline());
        Assert.Equal("gb-web", Assert.Single(_docker.Containers).Name);
    }

    [Fact]
    public async Task Other_syncs_keep_the_domains_on_the_new_version_until_it_sends_them_home()
    {
        _docker.Containers.Add(new Container { Name = "gb-web_next", Image = "gitberth/web:2", Ports = [("", 8100, 3000)] });

        var there = await _domains.RouteToAsync(_appId, "gb-web_next");
        Assert.True(there.Moved);
        Assert.Equal(11, there.Version);
        // Another app's deploy syncs the tunnel in between.
        await _domains.SyncAsync();
        Assert.Equal("http://localhost:8100", TunnelService(Hostname));

        var home = await _domains.RouteHomeAsync(_appId);
        Assert.True(home.Moved);
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        Assert.True(await _domains.WaitForTunnelAsync(home.Version!.Value, DateTime.UtcNow, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Choosing_another_main_domain_gives_the_app_a_new_address_with_its_next_build()
    {
        Assert.Null((await _domains.AddAsync(_appId, "example.com")).Error);
        using (var db = _db.CreateDbContext())
        {
            // The shorter name became the main one, which changes the app's address.
            Assert.True(db.Apps.Single().RebuildPending);
            db.Apps.Single().RebuildPending = false;
            db.SaveChanges();
        }
        Assert.Equal("example.com", await _domains.MainHostnameAsync(_appId));

        Assert.Null(await _domains.SetMainAsync(_appId, Hostname));

        Assert.Equal(Hostname, await _domains.MainHostnameAsync(_appId));
        using var after = _db.CreateDbContext();
        Assert.True(after.Apps.Single().RebuildPending);
        Assert.NotNull(await _domains.SetMainAsync(_appId, "elsewhere.example.com"));
    }

    private async Task<Deployment> FinishedAsync(int deploymentId)
    {
        for (var i = 0; i < 400; i++)
        {
            await using var db = _db.CreateDbContext();
            var dep = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == deploymentId);
            if (!dep.IsActive) return dep;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Deploy {deploymentId} didn't finish.");
    }

    [Fact]
    public async Task A_scheduled_restart_recreates_the_app_without_downtime_once_it_has_been_up_long_enough()
    {
        await _restarts.SetAsync("gb-web", new RestartInterval(6, RestartUnit.Hours));
        // Up for an hour: not due yet.
        Assert.Empty(await _restarter.TickAsync(DateTime.UtcNow));
        _docker.Containers.Single().StartedAt = DateTime.UtcNow.AddHours(-7);

        Assert.Equal(["gb-web"], await _restarter.TickAsync(DateTime.UtcNow));

        int id;
        await using (var db = _db.CreateDbContext())
            id = (await db.Deployments.SingleAsync(d => d.Trigger == DeployTrigger.Restart)).Id;
        var dep = await FinishedAsync(id);
        if (dep.Status != DeployStatus.Succeeded)
        {
            // The deploy may still have its log open.
            using var log = new StreamReader(new FileStream(DeployPaths.LogFile(_options, id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert.Fail($"{dep.Status}: {dep.Error}\n{await log.ReadToEndAsync()}");
        }
        Assert.Equal("gitberth/web:1", dep.ImageTag);
        Assert.Equal("run gb-web_next 127.0.0.1:8100", Timeline()[0]);
        Assert.Contains("stop gb-web", Timeline());
        var web = Assert.Single(_docker.Containers);
        Assert.Equal(("gb-web", "gitberth/web:1", true), (web.Name, web.Image, web.Running));
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        // Just started again, so it isn't due.
        Assert.Empty(await _restarter.TickAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task An_app_whose_saved_settings_arent_live_yet_is_restarted_as_it_is()
    {
        await using (var db = _db.CreateDbContext())
        {
            (await db.Apps.SingleAsync()).ConfigChangePending = true;
            await db.SaveChangesAsync();
        }
        await _restarts.SetAsync("gb-web", new RestartInterval(30, RestartUnit.Minutes));

        Assert.Equal(["gb-web"], await _restarter.TickAsync(DateTime.UtcNow));

        Assert.Equal(["restart gb-web"], Timeline());
        await using var after = _db.CreateDbContext();
        Assert.Empty(after.Deployments);
    }

    [Fact]
    public async Task Containers_gitberth_didnt_start_get_a_docker_restart_and_stopped_ones_are_left_alone()
    {
        _docker.Containers.Add(new Container { Name = "postgres", Image = "postgres:18", StartedAt = DateTime.UtcNow.AddDays(-2) });
        _docker.Containers.Add(new Container { Name = "worker", Image = "busybox", StartedAt = DateTime.UtcNow.AddDays(-2), Running = false });
        await _restarts.SetAsync("postgres", new RestartInterval(1, RestartUnit.Days));
        await _restarts.SetAsync("worker", new RestartInterval(1, RestartUnit.Days));

        Assert.Equal(["postgres"], await _restarter.TickAsync(DateTime.UtcNow));

        Assert.Equal(["restart postgres"], Timeline());
        Assert.Empty(await _restarter.TickAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task An_old_forwarder_is_listed_like_any_container_and_only_goes_when_it_is_removed()
    {
        // A port forwarder an older Gitberth started for postgres, and one of Gitberth's helpers, which runs for a moment.
        _docker.Containers.Add(new Container { Name = "postgres", Image = "postgres:18", Running = false, Labels = new() });
        _docker.Containers.Add(new Container { Name = "gb-publish-postgres-5432", Image = "alpine/socat:1.8.1.1", Ports = [("", 5432, 5432)],
            Labels = new() { [ContainerService.PublishesLabel] = "postgres:5432" } });
        _docker.Containers.Add(new Container { Name = "gitberth-helper-probe-1a2b3c4d5e6f", Image = "sha256:0c1a",
            Labels = new() { [HostScanHelper.Label] = HostScanHelper.ProbeKind } });
        var containers = Containers(new DockerCli(_options) { Runner = _docker.RunAsync }, new ContainerLocks(), null);

        var list = await containers.ListAsync();
        var forwarder = list.Rows.Single(r => r.Container.Name == "gb-publish-postgres-5432");

        // Removing postgres takes nothing else along; the forwarder goes when it's removed itself, from postgres's page.
        Assert.Null(await containers.RunAsync(list.Rows.Single(r => r.Container.Name == "postgres"), ContainerAction.Remove));
        Assert.Null(await containers.RunAsync(forwarder, ContainerAction.Remove));
        Assert.Equal(["rm postgres", "rm gb-publish-postgres-5432"], Timeline());
        // It's listed under its own name, as a container of its own; the helper isn't anyone's container.
        Assert.Equal((ContainerRole.LegacyForwarder, "gb-publish-postgres-5432"), (forwarder.Role, forwarder.Title));
        Assert.DoesNotContain(list.Rows, r => r.Container.Name.StartsWith(HostScanHelper.NamePrefix, StringComparison.Ordinal));
    }

    // A port changer whose journal the test writes, and the locks it shares with the container actions.
    private (PortChanger Changer, MemoryJournal Journal, ContainerLocks Locks) Changer()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock);
        var host = new FakeHost(engine);
        var journal = new MemoryJournal();
        var locks = new ContainerLocks();
        return (new PortChanger(engine, journal, locks, host, host, host, host, host, _options, NullLogger<PortChanger>.Instance, clock), journal, locks);
    }

    // A change of postgres a restart of Gitberth cut short, which recovery hasn't settled yet.
    private static ContainerChange Interrupted => new()
    {
        ContainerName = "postgres",
        Summary = "published 5432/tcp on the LAN",
        OldId = "0ebf31bbcd07" + new string('a', 52),
        OldName = "postgres_old_0ebf31bbcd07",
        TempName = "postgres_new_0ebf31bbcd07",
        WasRunning = true,
        OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""",
        Step = ChangeStep.Verifying,
    };

    [Fact]
    public async Task A_scheduled_restart_waits_while_gitberth_is_changing_the_container()
    {
        _docker.Containers.Add(new Container { Name = "postgres", Image = "postgres:18", StartedAt = DateTime.UtcNow.AddDays(-2) });
        await _restarts.SetAsync("postgres", new RestartInterval(1, RestartUnit.Days));
        var (changer, journal, locks) = Changer();
        var restarter = new RestartScheduler(_db, new DockerCli(_options) { Runner = _docker.RunAsync }, _restarts, _actions, new AppLocks(),
            NullLogger<RestartScheduler>.Instance, locks, changer);

        // A port change holds it: the restart waits for its next tick, and counts as not done.
        using (await locks.AcquireAsync("postgres"))
            Assert.Empty(await restarter.TickAsync(DateTime.UtcNow));
        // A change a restart of Gitberth cut short: neither of its containers starts until recovery has settled it.
        var row = await journal.AddAsync(Interrupted, default);
        Assert.Empty(await restarter.TickAsync(DateTime.UtcNow));
        Assert.Empty(Timeline());

        await journal.UpdateAsync(row with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done }, default);
        Assert.Equal(["postgres"], await restarter.TickAsync(DateTime.UtcNow));
        Assert.Equal(["restart postgres"], Timeline());
    }

    [Fact]
    public async Task Says_who_a_port_change_of_a_container_affects()
    {
        // postgres has a dynamic address on gitberth: api names it by name, worker by that address, and web is building.
        static string Inspect(string name, string image, string[] env, string ip) => new JsonObject
        {
            ["Id"] = name + "0000",
            ["Name"] = "/" + name,
            ["Config"] = new JsonObject { ["Image"] = image, ["Env"] = new JsonArray(env.Select(e => (JsonNode)e).ToArray()) },
            ["State"] = new JsonObject { ["Status"] = "running", ["Running"] = true },
            ["HostConfig"] = new JsonObject(),
            ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["gitberth"] = new JsonObject { ["IPAddress"] = ip } } },
        }.ToJsonString();
        var inspects = new Dictionary<string, string>
        {
            ["postgres"] = Inspect("postgres", "postgres:18", [], "172.21.0.4"),
            ["api"] = Inspect("api", "node:22", ["DATABASE_URL=postgres://api:pw@postgres:5432/api"], "172.21.0.5"),
            ["worker"] = Inspect("worker", "node:22", ["DB_HOST=172.21.0.4"], "172.21.0.6"),
        };
        var docker = new DockerCli(_options)
        {
            Runner = (args, _, _) => Task.FromResult(args[0] switch
            {
                "ps" => new ProcessResult(0, string.Join('\n', inspects.Keys.Select(n =>
                    $$"""{"Names":"{{n}}","State":"running","Status":"Up 2 days","Image":"img","Ports":"","Labels":"","Command":"run","ID":"{{n}}0000"}""")), ""),
                "container" => new ProcessResult(0, "[" + string.Join(',', args.Skip(2).Where(inspects.ContainsKey).Select(n => inspects[n])) + "]", ""),
                _ => new ProcessResult(1, "", "unexpected"),
            }),
        };
        await using (var db = _db.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { AppId = _appId, Status = DeployStatus.Building });
            await db.SaveChangesAsync();
        }
        var containers = Containers(docker, new ContainerLocks(), null);

        var users = await containers.UsersOfAsync("postgres", ["172.21.0.4"], default);

        Assert.Equal(["api"], users.Users);
        Assert.Equal([new AddressUser("worker", "172.21.0.4")], users.NameItsAddress);
        Assert.Equal(["Web"], users.Building);
        // A static address doesn't change with a recreate: nothing names one that can, and no build can lose it.
        var fixedIp = await containers.UsersOfAsync("postgres", [], default);
        Assert.Equal(["api"], fixedIp.Users);
        Assert.Empty(fixedIp.NameItsAddress);
        Assert.Empty(fixedIp.Building);
    }

    private ContainerService Containers(DockerCli docker, ContainerLocks locks, PortChanger? changer, GitberthOptions? options = null)
    {
        var api = new DockerApi(_options);
        var events = new ContainerEventWatcher(api, new StatsStore(_options), NullLogger<ContainerEventWatcher>.Instance);
        var stats = new ContainerStatsCollector(api, new HealthSettings(_settings), new StatsStore(_options), events, NullLogger<ContainerStatsCollector>.Instance);
        var apps = new AppService(_db, _protector, docker, _settings, new HealthSettings(_settings), _domains, _options, _restarts, new WatchState());
        return new ContainerService(docker, apps, _actions, new ContainerTitles(_settings), stats, options ?? _options, _db, locks,
            changer is null ? null : new Lazy<PortChanger>(() => changer), new NetworkService(api, _db, _options));
    }

    [Fact]
    public async Task A_standalone_container_waits_for_nothing_while_gitberth_is_changing_it()
    {
        _docker.Containers.Add(new Container { Name = "postgres", Image = "postgres:18" });
        _docker.Containers.Add(new Container { Name = "postgres_old_0ebf31bbcd07", Image = "postgres:18", Running = false });
        var (changer, journal, locks) = Changer();
        var containers = Containers(new DockerCli(_options) { Runner = _docker.RunAsync }, locks, changer);
        static ContainerRow Row(string name, bool running = true) =>
            new(new ContainerSummary(name, running ? "running" : "exited", running ? "Up 2 days" : "Exited (0) 1 minute ago", "postgres:18", "", new Dictionary<string, string>()),
                name, ContainerRole.Database, null, null);
        const string busy = "Gitberth is already changing postgres. Try again when that's done.";

        // While a port change holds it, every action is refused at once, as the design's one line says.
        using (await locks.AcquireAsync("postgres"))
        {
            Assert.Equal(busy, await containers.RunAsync(Row("postgres"), ContainerAction.Stop));
            Assert.Equal(busy, await containers.RunAsync(Row("postgres"), ContainerAction.Restart));
        }
        // A change a restart cut short covers its other containers too, though its lock is the name's alone.
        var row = await journal.AddAsync(Interrupted, default);
        Assert.Equal(busy, await containers.RunAsync(Row("postgres_old_0ebf31bbcd07", running: false), ContainerAction.Start));
        Assert.Equal(busy, await containers.RunAsync(Row("postgres_old_0ebf31bbcd07", running: false), ContainerAction.Remove));
        Assert.Equal(busy, await containers.RunAsync(Row("postgres"), ContainerAction.Stop));
        Assert.Empty(Timeline());

        // Settled: the actions go ahead, and a removed container takes what Gitberth kept for it along.
        await journal.UpdateAsync(row with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done }, default);
        await using (var db = _db.CreateDbContext())
        {
            db.KeptNetworks.Add(new KeptNetwork { ContainerName = "postgres", Network = "media" });
            db.ContainerPortNotes.Add(new ContainerPortNote { ContainerName = "postgres", Port = 5432 });
            await db.SaveChangesAsync();
        }
        Assert.Null(await containers.RunAsync(Row("postgres"), ContainerAction.Stop));
        Assert.Null(await containers.RunAsync(Row("postgres", running: false), ContainerAction.Remove));
        Assert.Equal(["stop postgres", "rm postgres"], Timeline());
        await using (var after = _db.CreateDbContext())
        {
            Assert.Empty(after.KeptNetworks);
            Assert.Empty(after.ContainerPortNotes);
        }
    }

    [Fact]
    public async Task Gitberth_knows_its_own_container_by_its_id_whatever_its_hostname()
    {
        // Started with docker run --hostname and without Compose's labels: its ID, read from its own mounts, says it's
        // Gitberth's own container, which it never stops or removes.
        const string id = "9d5c2a7e41b8f03c6e5d4a3b2c1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a39";
        var docker = new DockerCli(_options)
        {
            Runner = (args, _, _) => Task.FromResult(args[0] == "ps"
                ? new ProcessResult(0, $$"""{"Names":"gitberth-1","State":"running","Status":"Up 2 days","Image":"gitberth:local","Ports":"0.0.0.0:8080->8080/tcp","Labels":"","Command":"dotnet","ID":"{{id}}"}""", "")
                : new ProcessResult(1, "", "unexpected")),
        };
        var containers = Containers(docker, new ContainerLocks(), null, new GitberthOptions { DataDir = _dir, RunningInContainer = true, OwnContainer = id });

        var row = Assert.Single((await containers.ListAsync()).Rows);

        Assert.Equal(ContainerRole.Self, row.Role);
        Assert.Equal("Gitberth can't stop or remove its own container. Use docker compose on the host for that.", ContainerService.Refusal(row, ContainerAction.Stop));
    }

    [Fact]
    public async Task At_its_start_gitberth_joins_its_own_container_to_gitberth_by_its_id_whatever_its_hostname()
    {
        // Started with docker run --hostname: its hostname names no container, its ID (from its own mounts) does.
        const string id = "9d5c2a7e41b8f03c6e5d4a3b2c1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a39";
        var options = new GitberthOptions { DataDir = _dir, RunningInContainer = true, OwnContainer = id };
        Directory.CreateDirectory(options.WorkDir);
        var calls = new List<string>();
        var docker = new DockerCli(options)
        {
            Runner = (args, _, _) =>
            {
                lock (calls) calls.Add(string.Join(' ', args));
                return Task.FromResult(args switch
                {
                    ["version", "--format", "{{json .}}"] => new ProcessResult(0, """{"Client":{"Version":"29.0.0"},"Server":{"Version":"29.0.0"}}""", ""),
                    ["version", ..] => new ProcessResult(0, "1.56\n", ""),
                    _ => new ProcessResult(0, "", ""),
                });
            },
        };
        var startup = new StartupTasks(_db, docker, new AppLocks(), _actions, _pipeline, options, NullLogger<StartupTasks>.Instance);

        await startup.StartAsync(CancellationToken.None);
        if (startup.ExecuteTask is { } pass) await pass.WaitAsync(TimeSpan.FromSeconds(30));
        await startup.StopAsync(CancellationToken.None);

        Assert.Equal([$"network connect --gw-priority -1 gitberth {id}"], calls.Where(c => c.StartsWith("network connect", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Restarts_that_reuse_the_current_image_dont_push_older_images_out()
    {
        foreach (var tag in new[] { "a", "b", "c", "d" }) _docker.Images["gitberth/web:" + tag] = "{}";
        await using var db = _db.CreateDbContext();
        foreach (var tag in new[] { "a", "b", "c" })
            db.Deployments.Add(new Deployment { AppId = _appId, Status = DeployStatus.Succeeded, ImageTag = "gitberth/web:" + tag });
        for (var i = 0; i < 12; i++)
            db.Deployments.Add(new Deployment { AppId = _appId, Trigger = DeployTrigger.Restart, Status = DeployStatus.Succeeded, ImageTag = "gitberth/web:d" });
        await db.SaveChangesAsync();
        var app = await db.Apps.SingleAsync();
        app.CurrentImage = "gitberth/web:d";

        await _pipeline.PruneImagesAsync(db, app);

        // The last three different images stay for rollback; the ones before them, and ones no deploy used, go.
        Assert.Equal(["gitberth/web:b", "gitberth/web:c", "gitberth/web:d"], _docker.Images.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_app_named_like_another_apps_temporary_container_is_never_touched()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "Web next", Slug = "web-next", Owner = "acme", Repo = "web-next", CurrentImage = "gitberth/web-next:1" });
            await db.SaveChangesAsync();
        }
        _docker.Containers.Add(new Container { Name = "gb-web-next", Image = "gitberth/web-next:1", Ports = [("", 3002, 3000)],
            Labels = new() { ["gitberth.managed"] = "true", ["gitberth.app"] = "web-next" } });

        await _pipeline.TidyBridgesAsync();
        var (dep, _) = await RedeployAsync();
        await _actions.StopAsync(_appId);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("run gb-web_next 127.0.0.1:8100", Timeline());
        Assert.DoesNotContain(Timeline(), line => line.EndsWith(" gb-web-next", StringComparison.Ordinal));
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-web-next").Running);
    }

    [Fact]
    public async Task A_temporary_container_that_isnt_running_doesnt_count_as_where_the_domains_went()
    {
        _docker.Containers.Add(new Container { Name = "gb-web_next", Image = "gitberth/web:2", Ports = [("", 8100, 3000)], Running = false, Labels = BridgeLabels });

        var there = await _domains.RouteToAsync(_appId, "gb-web_next");

        Assert.False(there.Moved);
        Assert.Equal("http://localhost:3001", TunnelService(Hostname));
        await _domains.RouteHomeAsync(_appId);
    }

    [Fact]
    public async Task A_domain_removed_during_the_deploy_leaves_no_temporary_container_behind()
    {
        // Once visitors are on the new version, the app's only domain is removed (its rule dropped, its row gone).
        _docker.OnChange = change =>
        {
            if (change != "stop gb-web") return;
            using var db = _db.CreateDbContext();
            db.Domains.RemoveRange(db.Domains);
            db.SaveChanges();
            _cloudflare.Ingress = [new JsonObject { ["service"] = "http_status:404" }];
        };

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        var web = Assert.Single(_docker.Containers);
        Assert.Equal(("gb-web", "gitberth/web:2"), (web.Name, web.Image));
        Assert.Contains("Removed gb-web_next: the app has no domain any more.", log);
    }

    [Fact]
    public async Task Cancelling_while_the_new_version_is_checked_leaves_the_running_one_untouched()
    {
        using var cancel = new CancellationTokenSource();
        _docker.OnChange = change =>
        {
            if (change == "run gb-web_next") cancel.Cancel();
        };

        var (dep, log) = await RedeployAsync(ct: cancel.Token);

        Assert.Equal(DeployStatus.Cancelled, dep.Status);
        Assert.Equal(["run gb-web_next 127.0.0.1:8100", "rm gb-web_next"], Timeline());
        Assert.Equal(("gb-web", "gitberth/web:1", true), (_docker.Containers.Single().Name, _docker.Containers.Single().Image, _docker.Containers.Single().Running));
        Assert.Contains("The running container wasn't touched", log);
    }

    [Fact]
    public async Task A_domains_own_path_rules_and_their_settings_come_through_a_deploy_unchanged()
    {
        var admin = new JsonObject
        {
            ["hostname"] = Hostname, ["path"] = "^/admin", ["service"] = "http://localhost:3001",
            ["originRequest"] = new JsonObject { ["access"] = new JsonObject { ["required"] = true, ["teamName"] = "acme" } },
        };
        _cloudflare.Ingress = [admin, new JsonObject { ["hostname"] = Hostname, ["service"] = "http://localhost:3001" }, new JsonObject { ["service"] = "http_status:404" }];
        var before = _cloudflare.Ingress.ToJsonString();

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("PUT http://127.0.0.1:8100 http://127.0.0.1:8100", Timeline());
        Assert.Equal(before, _cloudflare.Ingress.ToJsonString());
    }

    private List<string> RunOf(string container) => _docker.Runs.Single(r => r[r.IndexOf("--name") + 1] == container);

    [Fact]
    public async Task The_new_version_starts_on_a_spare_port_on_127_0_0_1()
    {
        // Another container has the first spare port on every address, which a binding on 127.0.0.1 can't share.
        _docker.Containers.Add(new Container { Name = "sample-web", Image = "nginx", Ports = [("", 8100, 80)], Networks = new() { ["bridge"] = [] }, Labels = new() });

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(
        [
            "run gb-web_next 127.0.0.1:8101", "PUT http://127.0.0.1:8101", "cloudflared runs 11",
            "stop gb-web", "rename gb-web gb-web_prev", "run gb-web 3001",
            "PUT http://localhost:3001", "cloudflared runs 12", "rm gb-web_next", "rm gb-web_prev",
        ], Timeline());
        Assert.Contains("Starting the new version next to the current one, on 127.0.0.1:8101, so visitors are served throughout", log);
        // Only its web port, on the host only, and no name on the shared network until it had passed its checks.
        Assert.Equal(["127.0.0.1:8101:3000"], Values(RunOf("gb-web_next"), "-p"));
        Assert.Equal(["name=gitberth"], Values(RunOf("gb-web_next"), "--network"));
        Assert.Equal(["3001:3000"], Values(RunOf("gb-web"), "-p"));
        Assert.Equal(["name=gitberth,alias=web"], Values(RunOf("gb-web"), "--network"));
    }

    [Fact]
    public async Task The_apps_networks_are_joined_at_every_deploy_and_the_new_version_joins_them_without_its_names()
    {
        _docker.Networks.Add("media_default");
        using (var db = _db.CreateDbContext())
        {
            db.AppNetworks.Add(new AppNetwork { AppId = _appId, Network = "media_default", Alias = "db" });
            db.SaveChanges();
        }

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        // At gateway priority -1, so the shared network keeps the published ports and the way out.
        Assert.Equal(["name=gitberth", "name=media_default,gw-priority=-1"], Values(RunOf("gb-web_next"), "--network"));
        Assert.Equal(["name=gitberth,alias=web", "name=media_default,gw-priority=-1,alias=web,alias=db"], Values(RunOf("gb-web"), "--network"));
        Assert.Equal(["web", "db"], Assert.Single(_docker.Containers).Networks["media_default"]);
    }

    [Fact]
    public async Task A_network_that_is_gone_is_left_out_rather_than_failing_the_deploy()
    {
        using (var db = _db.CreateDbContext())
        {
            db.AppNetworks.Add(new AppNetwork { AppId = _appId, Network = "old_default" });
            db.SaveChanges();
        }

        var (dep, log) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("warning: the network old_default isn't there any more, so the new container isn't on it.", log);
        Assert.Equal(["name=gitberth,alias=web"], Values(RunOf("gb-web"), "--network"));
    }

    [Fact]
    public async Task Ports_saved_while_a_deploy_runs_stay_pending()
    {
        // Once the old version has stopped, a new host port is saved for the next deploy.
        _docker.OnChange = change =>
        {
            if (change != "stop gb-web") return;
            using var db = _db.CreateDbContext();
            var app = db.Apps.Include(a => a.Ports).Single();
            app.Ports.Single().HostPort = 3005;
            app.ConfigChangePending = true;
            app.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
        };

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["3001:3000"], Values(RunOf("gb-web"), "-p"));
        using var after = _db.CreateDbContext();
        // The new container has 3001 still: 3005 is saved, and not live yet.
        Assert.Equal((true, (int?)3005), (after.Apps.Single().ConfigChangePending, after.Ports.Single().HostPort));
    }

    [Fact]
    public async Task The_web_port_follows_the_image_whatever_the_number_of_ports()
    {
        _docker.Images["gitberth/web:3"] = $$"""{"{{ImageLabels.Port}}":"8080"}""";
        using (var db = _db.CreateDbContext())
        {
            db.Apps.Single().DetectedPort = 3000;
            db.Ports.Add(new PortMapping { AppId = _appId, ContainerPort = 9229, Access = PortAccess.Internal });
            db.SaveChanges();
        }

        var (dep, log) = await RedeployAsync("gitberth/web:3");

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("gitberth/web:3 listens on port 8080, so the web port follows it", log);
        // The tunnel follows the saved web port, which only moves once this deploy has worked: so no new version on the side.
        Assert.Contains("The swap takes a few seconds of downtime: the web port moves to port 8080 with this deploy.", log);
        Assert.Equal(["3001:8080"], Values(RunOf("gb-web"), "-p"));
        Assert.Equal(["9229"], Values(RunOf("gb-web"), "--expose"));
        using var after = _db.CreateDbContext();
        Assert.Equal([(8080, true), (9229, false)], after.Ports.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => (p.ContainerPort, p.IsWeb)));
        Assert.Equal(8080, after.Apps.Single().DetectedPort);
    }

    [Fact]
    public async Task A_network_whose_name_has_capitals_is_joined_by_its_id()
    {
        // docker run lower-cases --network name=… (Docker 29's CLI does), and Docker's names are exact: MediaNet would be
        // asked for as medianet, which isn't there, and every deploy would fail. Its ID has no capitals.
        _docker.Networks.Add("MediaNet");
        using (var db = _db.CreateDbContext())
        {
            db.AppNetworks.Add(new AppNetwork { AppId = _appId, Network = "MediaNet", Alias = "db" });
            db.SaveChanges();
        }

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        var id = MemoryDocker.IdOf("MediaNet");
        Assert.Equal(["name=gitberth", $"name={id},gw-priority=-1"], Values(RunOf("gb-web_next"), "--network"));
        Assert.Equal(["name=gitberth,alias=web", $"name={id},gw-priority=-1,alias=web,alias=db"], Values(RunOf("gb-web"), "--network"));
        Assert.Equal(["web", "db"], Assert.Single(_docker.Containers).Networks["MediaNet"]);
    }

    /// <summary>The image gitberth/web:3 listens on 8080, where the app's last build listened on 3000; domains are off.</summary>
    private async Task MovesToPort8080Async(PortMapping second)
    {
        await _settings.SetAsync(SettingKeys.DomainsEnabled, "false");
        _docker.Images["gitberth/web:3"] = $$"""{"{{ImageLabels.Port}}":"8080"}""";
        using var db = _db.CreateDbContext();
        db.Apps.Single().DetectedPort = 3000;
        second.AppId = _appId;
        db.Ports.Add(second);
        db.SaveChanges();
    }

    /// <summary>Saves the app's ports as the Network tab does, changed by <paramref name="edit"/>: every row is replaced, with a new ID.</summary>
    private PortsSaved SavePorts(Action<List<PortRow>> edit)
    {
        var loaded = _apps.GetAsync(_appId).GetAwaiter().GetResult()!;
        var rows = loaded.Ports.OrderBy(p => p.Id).Select(PortRow.From).ToList();
        edit(rows);
        return _apps.SavePortsAsync(_appId, rows, AppSettingsModel.PortsKey(loaded.Ports)).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Ports_saved_while_a_deploy_moves_the_web_port_move_with_it()
    {
        await MovesToPort8080Async(new PortMapping { ContainerPort = 9229, Access = PortAccess.Internal });
        // While it runs, 9229 is published on the LAN for the next deploy.
        PortsSaved? saved = null;
        _docker.OnChange = change =>
        {
            if (change == "stop gb-web") saved ??= SavePorts(rows => (rows[1].Access, rows[1].HostPort) = (PortAccess.Lan, 9229));
        };

        var (dep, _) = await RedeployAsync("gitberth/web:3");

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.True(saved!.Ok);
        Assert.Equal(["3001:8080"], Values(RunOf("gb-web"), "-p"));
        using (var after = _db.CreateDbContext())
        {
            // The saved web port is the one the new container serves on, and 9229 on the LAN waits for the next deploy.
            Assert.Equal([(8080, true, PortAccess.Lan), (9229, false, PortAccess.Lan)],
                after.Ports.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => (p.ContainerPort, p.IsWeb, p.Access)));
            Assert.Equal((8080, true), (after.Apps.Single().DetectedPort, after.Apps.Single().ConfigChangePending));
        }

        // That deploy publishes the port the image listens on.
        _docker.OnChange = null;
        _docker.Runs.Clear();
        var (next, _) = await RedeployAsync("gitberth/web:3");
        Assert.Equal(DeployStatus.Succeeded, next.Status);
        Assert.Equal(["3001:8080", "9229:9229"], Values(RunOf("gb-web"), "-p"));
    }

    [Fact]
    public async Task The_port_the_image_moved_away_from_follows_it_when_the_web_marker_moved_meanwhile()
    {
        await MovesToPort8080Async(new PortMapping { ContainerPort = 9229, HostPort = 9229 });
        // While it runs, 9229 becomes the web port, which restarts nothing.
        _docker.OnChange = change =>
        {
            if (change != "stop gb-web") return;
            _docker.OnChange = null;
            SavePorts(rows => (rows[0].IsWeb, rows[1].IsWeb) = (false, true));
        };

        var (dep, _) = await RedeployAsync("gitberth/web:3");

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["3001:8080", "9229:9229"], Values(RunOf("gb-web"), "-p"));
        using var after = _db.CreateDbContext();
        // The container serves 3001 from 8080: so does the row, though it's no longer the web port.
        Assert.Equal([(8080, false), (9229, true)], after.Ports.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => (p.ContainerPort, p.IsWeb)));
        Assert.False(after.Apps.Single().ConfigChangePending);
    }

    [Fact]
    public async Task Saves_during_a_deploy_leave_pending_only_what_it_didnt_make_live()
    {
        // "Restart with the new settings": a saved variable, which this recreate makes live.
        await using (var db = _db.CreateDbContext())
        {
            var app = await db.Apps.SingleAsync();
            app.EnvVars.Add(new EnvVar { Key = "GREETING", ProtectedValue = _protector.Protect("hello") });
            app.ConfigChangePending = true;
            await db.SaveChangesAsync();
        }
        // While it runs, the Settings tab is saved with auto-deploy switched and another branch, and the app is renamed.
        SaveOutcome? saved = null;
        _docker.OnChange = change =>
        {
            if (change != "stop gb-web" || saved is not null) return;
            var model = _apps.ToModel(_apps.GetAsync(_appId).GetAwaiter().GetResult()!);
            (model.AutoDeploy, model.Branch) = (!model.AutoDeploy, "next");
            saved = _apps.SaveAsync(_appId, model).GetAwaiter().GetResult();
            _apps.RenameAsync(_appId, "Web site").GetAwaiter().GetResult();
        };

        var (dep, _) = await RedeployAsync();

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(new SaveOutcome(NeedsRestart: false, NeedsRebuild: true), saved);
        Assert.Contains("GREETING=hello", Assert.Single(_docker.Containers).Env);
        using var after = _db.CreateDbContext();
        // The variable is live, so nothing waits for a restart (a start only starts the app, and a scheduled restart is the
        // one domain visitors don't notice). The branch waits for the next build.
        Assert.Equal((false, true, "Web site"), (after.Apps.Single().ConfigChangePending, after.Apps.Single().RebuildPending, after.Apps.Single().Name));
    }

    [Fact]
    public async Task An_image_an_older_gitberth_labelled_with_a_udp_port_leaves_the_web_port_alone()
    {
        // Its first port by ID is UDP: Gitberth before the web marker labelled images with that one, whatever its protocol.
        using (var db = _db.CreateDbContext())
        {
            var app = db.Apps.Include(a => a.Ports).Single();
            app.Ports.Clear();
            db.SaveChanges();
            app.Ports.Add(new PortMapping { ContainerPort = 53, Protocol = "udp", HostPort = 5353 });
            db.SaveChanges();
            // As the migration marks it: the first TCP port is the web port.
            app.Ports.Add(new PortMapping { ContainerPort = 3000, HostPort = 3001, IsWeb = true });
            app.DetectedPort = 3000;
            db.SaveChanges();
        }
        _docker.Images["gitberth/web:old"] = $$"""{"{{ImageLabels.Port}}":"53"}""";

        var (dep, log) = await RedeployAsync("gitberth/web:old");

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["5353:53/udp", "3001:3000"], Values(RunOf("gb-web"), "-p"));
        Assert.DoesNotContain("so the web port follows it", log);
        using var after = _db.CreateDbContext();
        Assert.Equal([(53, "udp", false), (3000, "tcp", true)], after.Ports.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => (p.ContainerPort, p.Protocol, p.IsWeb)));
    }

    [Fact]
    public async Task A_domain_taken_over_with_its_paths_says_where_to_serve_every_path()
    {
        // Made in Cloudflare's dashboard: only admin.example.com's /admin paths go to the app.
        _cloudflare.Ingress.Insert(0, new JsonObject { ["hostname"] = "admin.example.com", ["path"] = "^/admin", ["service"] = "http://localhost:3001" });

        var report = await _domains.SyncAsync();

        // Serve every path is on the Network tab, with the app's domains.
        Assert.Contains(report.Notes, n => n.Text == "Took over admin.example.com. The tunnel sends only some of its addresses to Web, and it stays that way until you choose to serve every path on Web's Network tab.");
    }

    private bool HasContainer(string name)
    {
        lock (_docker) return _docker.Containers.Any(c => c.Name == name);
    }

    private bool AnyDeployActive()
    {
        using var db = _db.CreateDbContext();
        return db.Deployments.AsNoTracking().ToList().Any(d => d.IsActive);
    }

    /// <summary>Waits until the deploy's new version runs next to the old one, then until no deploy is queued or running.</summary>
    private async Task WhileTheNewVersionRunsAsync(Func<Task> click)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!HasContainer("gb-web_next") && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(HasContainer("gb-web_next"));
        await click();
        until = DateTime.UtcNow.AddSeconds(30);
        while (AnyDeployActive() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.False(AnyDeployActive());
    }

    // S1: a new version goes out through the queue (a rollback to gitberth/web:2 stands in for a build's swap), and while it
    // runs [Apply] is clicked. A recreate queued then would be pinned to the image that ran then, and put it back afterwards.
    [Fact]
    public async Task Apply_clicked_during_a_deploy_doesnt_put_the_old_image_back()
    {
        _cloudflare.ConfirmAfter = TimeSpan.FromMilliseconds(300);
        int build;
        using (var db = _db.CreateDbContext())
        {
            // web:1 came from an earlier deploy, so it's kept for rollback as on the real host.
            db.Deployments.Add(new Deployment { AppId = _appId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = "gitberth/web:1", CommitSha = "1111111" });
            var next = new Deployment { AppId = _appId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, ImageTag = "gitberth/web:2", CommitSha = NewCommit, CommitMessage = "New footer", CommitAuthor = "Ada" };
            db.Deployments.Add(next);
            db.SaveChanges();
            build = next.Id;
        }

        var deploy = await _actions.RollbackAsync(_appId, build);
        await WhileTheNewVersionRunsAsync(async () => Assert.Equal(deploy, await _actions.ApplyIfRunningAsync(_appId)));

        using var after = _db.CreateDbContext();
        var app = after.Apps.Single();
        var image = _docker.Containers.Single(c => c.Name == "gb-web").Image;
        Assert.Equal(("gitberth/web:2", NewCommit, "gitberth/web:2"), (app.CurrentImage, app.CurrentCommit, image));
        Assert.Equal(1, Timeline().Count(step => step == "stop gb-web"));
    }

    // S1: [Apply] clicked, then clicked again while the deploy the first click started runs: the app restarts once.
    [Fact]
    public async Task A_second_apply_click_during_the_first_ones_deploy_doesnt_restart_the_app_again()
    {
        _cloudflare.ConfirmAfter = TimeSpan.FromMilliseconds(300);

        var first = await _actions.ApplyIfRunningAsync(_appId);
        Assert.NotNull(first);
        await WhileTheNewVersionRunsAsync(async () => Assert.Equal(first, await _actions.ApplyIfRunningAsync(_appId)));

        Assert.Equal(1, Timeline().Count(step => step == "stop gb-web"));
        using var after = _db.CreateDbContext();
        Assert.Equal([first], after.Deployments.AsNoTracking().Select(d => (int?)d.Id).ToList());
    }

    [Fact]
    public async Task A_forks_preview_runs_without_the_hosts_devices_or_what_reaches_the_host_and_its_log_says_so()
    {
        const string image = "gitberth/web-pr-7:1";
        int previewId, deploymentId;
        await using (var db = _db.CreateDbContext())
        {
            // Production has a serial stick, privileged mode and an added capability; forks are previewed.
            var parent = await db.Apps.SingleAsync(a => a.Id == _appId);
            parent.PreviewMode = PreviewMode.PullRequests;
            parent.PreviewForks = true;
            parent.RuntimeExtras = Gitberth.Apps.GitExtras.Write(new Gitberth.Apps.GitExtras
            {
                Devices = [new Gitberth.Apps.DeviceSetting("/dev/ttyUSB0", "/dev/ttyUSB0")], Privileged = true, CapAdd = ["SYS_ADMIN"], Init = true,
            });
            var preview = new AppDefinition { Name = "Web · PR #7", Slug = "web-pr-7", Owner = "acme", Repo = "web", ParentAppId = _appId, AutoDeploy = false, CurrentImage = image, CurrentCommit = "7777777" };
            preview.Ports.Add(new PortMapping { HostPort = 3002, ContainerPort = 3000, IsWeb = true });
            db.Apps.Add(preview);
            await db.SaveChangesAsync();
            db.Previews.Add(new PreviewInfo
            {
                AppId = preview.Id, ParentAppId = _appId, Kind = PreviewKind.PullRequest, Branch = "patch-1", PullRequest = 7, Ref = "refs/pull/7/head", HeadSha = "7777777", FromFork = true,
            });
            var deployment = new Deployment { AppId = preview.Id, Trigger = DeployTrigger.ConfigChange, ImageTag = image, CommitSha = "7777777" };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
            (previewId, deploymentId) = (preview.Id, deployment.Id);
        }
        _docker.Images[image] = "{}";

        await _pipeline.RunAsync(new DeployRequest(previewId, deploymentId, Build: false, Image: image), CancellationToken.None);

        await using (var db = _db.CreateDbContext())
        {
            var done = await db.Deployments.SingleAsync(d => d.Id == deploymentId);
            Assert.True(done.Status == DeployStatus.Succeeded, done.Error);
        }
        var log = File.ReadAllText(DeployPaths.LogFile(_options, deploymentId));
        Assert.Contains("Not given the host's device, because a preview never takes production's: /dev/ttyUSB0.", log);
        Assert.Contains("Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's.", log);
        Assert.Contains("Runs without added capabilities (SYS_ADMIN), because the pull request comes from a fork and its code mustn't reach the host.", log);
        var run = _docker.Runs.Last();
        Assert.Equal("gb-web-pr-7", run[run.IndexOf("--name") + 1]);
        foreach (var flag in new[] { "--device", "--privileged", "--cap-add" }) Assert.DoesNotContain(flag, run);
        // What doesn't reach the host is the parent's as usual.
        Assert.Contains("--init", run);
        // Production's own container is untouched.
        Assert.Equal(["gb-web", "gb-web-pr-7"], _docker.Containers.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_preview_never_gets_productions_database_unless_it_has_a_value_for_previews()
    {
        const string image = "gitberth/web-pr-8:1";
        int previewId;
        await using (var db = _db.CreateDbContext())
        {
            var parent = await db.Apps.SingleAsync(a => a.Id == _appId);
            parent.PreviewMode = PreviewMode.PullRequests;
            parent.EnvVars.Add(new EnvVar { Key = "DATABASE_URL", ProtectedValue = _protector.Protect("postgres://app:pw@postgres:5432/web") });
            var preview = new AppDefinition { Name = "Web · PR #8", Slug = "web-pr-8", Owner = "acme", Repo = "web", ParentAppId = _appId, AutoDeploy = false, CurrentImage = image, CurrentCommit = "8888888" };
            preview.Ports.Add(new PortMapping { HostPort = 3002, ContainerPort = 3000, IsWeb = true });
            db.Apps.Add(preview);
            await db.SaveChangesAsync();
            db.Previews.Add(new PreviewInfo { AppId = preview.Id, ParentAppId = _appId, Kind = PreviewKind.PullRequest, Branch = "feature", PullRequest = 8, Ref = "refs/pull/8/head", HeadSha = "8888888" });
            await db.SaveChangesAsync();
            previewId = preview.Id;
        }
        _docker.Images[image] = "{}";

        async Task<Deployment> DeployAsync()
        {
            int id;
            await using (var db = _db.CreateDbContext())
            {
                var d = new Deployment { AppId = previewId, Trigger = DeployTrigger.ConfigChange, ImageTag = image, CommitSha = "8888888" };
                db.Deployments.Add(d);
                await db.SaveChangesAsync();
                id = d.Id;
            }
            await _pipeline.RunAsync(new DeployRequest(previewId, id, Build: false, Image: image), CancellationToken.None);
            await using var after = _db.CreateDbContext();
            return await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id);
        }

        // Production's value for all deploys: refused before anything runs, and the log says what to do.
        var refused = await DeployAsync();
        Assert.Equal(DeployStatus.Failed, refused.Status);
        Assert.Equal("DATABASE_URL would reach this preview with production's value (set in Gitberth for all deploys), so the preview's build and code would use "
            + "production's database. On Web's Environment tab, give it a value for Previews: a database of the previews' own, or the same value if previews may share production's.", refused.Error);
        Assert.DoesNotContain(_docker.Containers, c => c.Name == "gb-web-pr-8");

        // A value for previews (even production's own, on purpose) lets it run with that.
        await using (var db = _db.CreateDbContext())
        {
            db.EnvVars.Add(new EnvVar { AppId = _appId, Key = "DATABASE_URL", Scope = EnvScope.Preview, ProtectedValue = _protector.Protect("postgres://app:pw@postgres:5432/web_preview") });
            await db.SaveChangesAsync();
        }
        var ran = await DeployAsync();
        Assert.True(ran.Status == DeployStatus.Succeeded, ran.Error);
        Assert.Contains(_docker.Containers, c => c.Name == "gb-web-pr-8");
    }

    // web's preview of the branch test, as PreviewService makes one (web-test), with copies of web's named volumes. Vercel's
    // variables are off, so no deploy here asks GitHub which branch is the default one.
    private async Task<int> AddTestPreviewAsync(string image, params string[] volumes)
    {
        await using var db = _db.CreateDbContext();
        var parent = await db.Apps.SingleAsync(a => a.Id == _appId);
        parent.PreviewMode = PreviewMode.PullRequestsAndBranches;
        parent.VercelVariables = false;
        var preview = new AppDefinition { Name = "Web · test", Slug = "web-test", Owner = "acme", Repo = "web", Branch = "test", ParentAppId = _appId, AutoDeploy = false, CurrentImage = image, CurrentCommit = "8888888", VercelVariables = false };
        preview.Ports.Add(new PortMapping { HostPort = 3002, ContainerPort = 3000, IsWeb = true });
        preview.Volumes.AddRange(volumes.Select(v => new VolumeMount { Source = v, Target = "/srv/" + v }));
        db.Apps.Add(preview);
        await db.SaveChangesAsync();
        db.Previews.Add(new PreviewInfo { AppId = preview.Id, ParentAppId = _appId, Kind = PreviewKind.Branch, Branch = "test", Ref = "refs/heads/test", HeadSha = "8888888" });
        await db.SaveChangesAsync();
        _docker.Images[image] = "{}";
        return preview.Id;
    }

    private async Task AddVolumeAsync(int appId, string source)
    {
        await using var db = _db.CreateDbContext();
        db.Volumes.Add(new VolumeMount { AppId = appId, Source = source, Target = "/srv/" + source });
        await db.SaveChangesAsync();
    }

    // A deploy of the app without a build, from the image given.
    private async Task<Deployment> RecreateAsync(int appId, string image)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = appId, Trigger = DeployTrigger.ConfigChange, ImageTag = image, CommitSha = "8888888" };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(appId, id, Build: false, Image: image), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task A_VOLUME_a_previews_image_declares_gets_a_name_no_other_app_has()
    {
        // web keeps gb-web-test-data at /srv/test-data; the preview's branch declares VOLUME /data, which as data would be
        // gb-web-test-data too.
        const string image = "gitberth/web-test:1";
        await AddVolumeAsync(_appId, "test-data");
        var previewId = await AddTestPreviewAsync(image, "test-data");
        _docker.ImageVolumes[image] = """{"/data":{}}""";

        var deploy = await RecreateAsync(previewId, image);

        Assert.True(deploy.Status == DeployStatus.Succeeded, deploy.Error);
        var run = _docker.Runs.Last();
        Assert.Equal("gb-web-test", run[run.IndexOf("--name") + 1]);
        Assert.Contains("gb-web-test-data-2:/data", run);
        Assert.Contains("gb-web-test-test-data:/srv/test-data", run);
        Assert.DoesNotContain(run, a => a.StartsWith("gb-web-test-data:", StringComparison.Ordinal));
        Assert.Contains("The image declares VOLUME /data: it's kept in the volume gb-web-test-data-2", File.ReadAllText(DeployPaths.LogFile(_options, deploy.Id)));
    }

    [Fact]
    public async Task A_deploy_never_starts_a_preview_and_another_app_on_one_volume_and_production_isnt_held_up_by_one_that_wasnt_started()
    {
        const string image = "gitberth/web-test:1";
        await AddVolumeAsync(_appId, "test-cache");
        var previewId = await AddTestPreviewAsync(image, "test-cache");
        // web gets a volume cache past the Settings tab's check: the preview's copy would be gb-web-test-cache, web's test-cache.
        await AddVolumeAsync(_appId, "cache");

        var refused = await RecreateAsync(previewId, image);

        Assert.Equal((DeployStatus.Failed, "The volume cache would be gb-web-test-cache, which is also Web's, so the preview isn't started: a preview never shares a volume with another app. Remove the preview: it comes back under another name with its next commit."),
            (refused.Status, refused.Error));
        Assert.DoesNotContain(_docker.Runs, r => r.Contains("gb-web-test"));
        // Its copies weren't saved, so it holds no name of web's.
        await using (var db = _db.CreateDbContext())
            Assert.Equal(["test-cache"], await db.Volumes.Where(v => v.AppId == previewId).Select(v => v.Source).ToListAsync());

        // So production deploys as usual.
        var production = await RecreateAsync(_appId, "gitberth/web:2");
        Assert.True(production.Status == DeployStatus.Succeeded, production.Error);
        Assert.Equal("gitberth/web:2", _docker.Containers.Single(c => c.Name == "gb-web").Image);

        // A volume of web's whose name the preview has: web's container isn't touched.
        await AddVolumeAsync(previewId, "data");
        await AddVolumeAsync(_appId, "test-data");
        var steps = Timeline().Count;

        var held = await RecreateAsync(_appId, "gitberth/web:2");

        Assert.Equal((DeployStatus.Failed, "The volume test-data would be gb-web-test-data, which is also the preview Web · test's, so Web isn't started with it: a preview never shares a volume with another app. Remove that preview: it comes back under another name with its next commit."),
            (held.Status, held.Error));
        Assert.Equal(steps, Timeline().Count);
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-web").Running);
    }

    // An edit of the app's settings as the Settings tab makes one: as loaded, and as wanted.
    private async Task<Gitberth.Apps.SettingsEdit> EditAsync(int appId, Func<Gitberth.Apps.ContainerSettings, Gitberth.Apps.ContainerSettings> change)
    {
        await using var db = _db.CreateDbContext();
        var app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
            .SingleAsync(a => a.Id == appId);
        var loaded = Gitberth.Apps.GitAppSettings.ToContainer(app, _protector);
        var options = Gitberth.Apps.GitAppSettings.ToOptions(app, new HealthSettings(_settings), _restarts);
        var git = Gitberth.Apps.GitAppSettings.ToGit(app);
        return new Gitberth.Apps.SettingsEdit(null, loaded, change(loaded), options, options, git, git);
    }

    [Fact]
    public async Task A_previews_own_volume_stays_through_a_failed_deploy_so_production_cant_take_its_name()
    {
        // web keeps data; its preview of the branch test has its copy, and the volume its image declared, cache. Its
        // container runs with gb-web-test-cache.
        const string image = "gitberth/web-test:1";
        await AddVolumeAsync(_appId, "data");
        var previewId = await AddTestPreviewAsync(image, "data", "cache");
        _docker.Containers.Add(new Container { Name = "gb-web-test", Image = image, Ports = [("", 3002, 3000)] });

        // Its next deploy fails before its container is touched (its image is gone here; a failed build is the same).
        var failed = await RecreateAsync(previewId, "gitberth/web-test:gone");

        Assert.Equal(DeployStatus.Failed, failed.Status);
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-web-test").Running);
        // The old container still writes to gb-web-test-cache, and the preview's row still says so.
        await using (var db = _db.CreateDbContext())
            Assert.Equal(["data", "cache"], await db.Volumes.Where(v => v.AppId == previewId).OrderBy(v => v.Id).Select(v => v.Source).ToListAsync());

        // So web can't take the name: test-cache is refused when it's saved…
        var saved = await _apps.SaveSettingsAsync(_appId, await EditAsync(_appId, s => s with
        {
            Mounts = [.. s.Mounts, new Gitberth.Apps.MountSetting(Gitberth.Apps.MountKind.Volume, "test-cache", "/var/cache")],
        }));
        Assert.Equal("The volume test-cache would be gb-web-test-cache, which is also the preview Web · test's. Give it another name, or remove that preview first.",
            Assert.Single(saved.Problems).Message);

        // …and saved past that check, web isn't started on it.
        await AddVolumeAsync(_appId, "test-cache");
        var held = await RecreateAsync(_appId, "gitberth/web:2");

        Assert.Equal((DeployStatus.Failed, "The volume test-cache would be gb-web-test-cache, which is also the preview Web · test's, so Web isn't started with it: a preview never shares a volume with another app. Remove that preview: it comes back under another name with its next commit."),
            (held.Status, held.Error));
        Assert.Equal("gitberth/web:1", _docker.Containers.Single(c => c.Name == "gb-web").Image);
    }

    [Fact]
    public async Task A_preview_never_gets_a_new_volume_docker_has_already()
    {
        // web took test-cache out of its settings; Docker keeps gb-web-test-cache, with web's data in it.
        const string image = "gitberth/web-test:1";
        var previewId = await AddTestPreviewAsync(image);
        _docker.Volumes.Add("gb-web-test-cache");

        // web gets a volume cache: the preview's copy would be gb-web-test-cache.
        await AddVolumeAsync(_appId, "cache");
        var refused = await RecreateAsync(previewId, image);

        Assert.Equal((DeployStatus.Failed, "The volume cache would be gb-web-test-cache, which Docker has already, so the preview isn't started: that volume holds another app's data, or a removed app's. Remove the preview: it comes back under another name with its next commit."),
            (refused.Status, refused.Error));
        Assert.DoesNotContain(_docker.Runs, r => r.Contains("gb-web-test"));
        await using (var db = _db.CreateDbContext())
            Assert.Empty(await db.Volumes.Where(v => v.AppId == previewId).ToListAsync());

        // Nor does a VOLUME its image declares get that name: /cache gets a number.
        await using (var db = _db.CreateDbContext())
            await db.Volumes.Where(v => v.AppId == _appId).ExecuteDeleteAsync();
        _docker.ImageVolumes[image] = """{"/cache":{}}""";
        var deploy = await RecreateAsync(previewId, image);

        Assert.True(deploy.Status == DeployStatus.Succeeded, deploy.Error);
        var run = _docker.Runs.Last();
        Assert.Contains("gb-web-test-cache-2:/cache", run);
        Assert.DoesNotContain(run, a => a.StartsWith("gb-web-test-cache:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_keeps_its_volume_where_web_now_has_a_tmpfs_and_runs_with_one_mount_there()
    {
        // web-test has its copy of web's volume cache at /srv/cache; web then keeps /srv/cache in memory instead.
        const string image = "gitberth/web-test:1";
        var previewId = await AddTestPreviewAsync(image, "cache");
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.RuntimeExtras, Gitberth.Apps.GitExtras.Write(new Gitberth.Apps.GitExtras
            {
                Tmpfs = [new Gitberth.Apps.GitTmpfs("/srv/cache"), new Gitberth.Apps.GitTmpfs("/tmp/scratch")],
            })));

        var deploy = await RecreateAsync(previewId, image);

        Assert.True(deploy.Status == DeployStatus.Succeeded, deploy.Error);
        var run = _docker.Runs.Last();
        // Docker refuses a volume and a tmpfs at one path: the preview's volume stays, and web's other tmpfs comes along.
        Assert.Contains("gb-web-test-cache:/srv/cache", run);
        Assert.Equal(["/tmp/scratch"], run.Select((a, i) => (a, i)).Where(x => x.a == "--tmpfs").Select(x => run[x.i + 1]));
        Assert.Contains("No tmpfs at /srv/cache: the preview keeps its volume gb-web-test-cache there", File.ReadAllText(DeployPaths.LogFile(_options, deploy.Id)));
    }

    [Fact]
    public async Task A_promote_that_fails_lets_production_follow_its_branch_unless_a_newer_promote_waits()
    {
        // web was promoted over its branch's commit twice: the first promote runs while the second waits its turn.
        const string head = "3333333333333333333333333333333333333333";
        async Task<int> AddPromoteAsync(string sha)
        {
            await using var db = _db.CreateDbContext();
            var dep = new Deployment { AppId = _appId, Trigger = DeployTrigger.Promote, CommitSha = sha };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            return dep.Id;
        }
        async Task<string?> PromotedOverAsync()
        {
            await using var db = _db.CreateDbContext();
            return await db.Apps.Where(a => a.Id == _appId).Select(a => a.PromotedOverCommit).SingleAsync();
        }
        await using (var db = _db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PromotedOverCommit, head).SetProperty(a => a.VercelVariables, false));
        var first = await AddPromoteAsync("4444444");
        var second = await AddPromoteAsync("5555555");

        // The first fails (its image is gone here; a failed build is the same): the second still holds production.
        await _pipeline.RunAsync(new DeployRequest(_appId, first, Build: false, Image: "gitberth/web:gone"), CancellationToken.None);

        await using (var db = _db.CreateDbContext())
            Assert.Equal(DeployStatus.Failed, (await db.Deployments.SingleAsync(d => d.Id == first)).Status);
        Assert.Equal(head, await PromotedOverAsync());

        // The second fails too: nothing holds production off its branch any more.
        await _pipeline.RunAsync(new DeployRequest(_appId, second, Build: false, Image: "gitberth/web:gone"), CancellationToken.None);

        Assert.Null(await PromotedOverAsync());
    }
}
