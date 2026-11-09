using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The Docker networks on the host and who's on them, read from Docker's answers captured on CT 101, and the one look at
/// every container the addresses and the host-port map share.
/// </summary>
public sealed class NetworkServiceTests : IDisposable
{
    // Gitberth's hostname on CT 101: its container's short ID.
    private const string Self = "455b8ea31b6d";

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>
    /// The engine on CT 101: its list, networks and each container's inspect, by ID or name, plus <see cref="Extra"/>
    /// containers in the list. Joining and leaving a network change the container's inspect and its entry in the list, the
    /// way Docker's do.
    /// </summary>
    private sealed class Engine : HttpMessageHandler
    {
        private readonly List<string> _calls = [];
        private readonly List<string> _requests = [];
        private readonly Dictionary<string, JsonObject> _inspects = new(StringComparer.Ordinal);
        private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        private readonly JsonArray _networks = DockerFixtures.Json("networks-json.json").AsArray();

        public Engine()
        {
            foreach (var name in new[] { "postgres", "gitberth", "cloudflare-tunnel", "gb-orb", "gb-kanban", "sample-bun-elysia", "sample-bun-hono", "sample-index" })
                Know(DockerFixtures.Inspect(name));
        }

        public List<JsonObject> Extra { get; } = [];

        /// <summary>Containers whose inspect the engine fails, as a busy engine does.</summary>
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        /// <summary>The API version it speaks: 1.47 is Docker 27's, from before gateway priorities.</summary>
        public string ApiVersion { get; set; } = "1.56";

        /// <summary>
        /// Connects or disconnects ("connect", "disconnect") whose answer is lost, as one from a busy engine that answers after
        /// Gitberth gave up: carried out first when <c>Done</c>, else not at all.
        /// </summary>
        public (string Action, bool Done)? Unanswered { get; set; }

        /// <summary>Each call that changes something, with what it sent.</summary>
        public List<(string Path, string Body)> Writes { get; } = [];

        /// <summary>Every call, as "POST /v1.56/networks/media/connect".</summary>
        public List<string> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        /// <summary>The containers inspected, by the ID or name asked for.</summary>
        public List<string> Inspected
        {
            get { lock (_calls) return _calls.Where(c => c.EndsWith("/json", StringComparison.Ordinal) && !c.StartsWith("/v1.56/containers/json", StringComparison.Ordinal))
                .Select(c => c["/v1.56/containers/".Length..^"/json".Length]).ToList(); }
        }

        /// <summary>A container Docker has besides CT 101's: its inspect, and its entry in the list.</summary>
        public void Add(JsonObject inspect, JsonObject listed)
        {
            Know(inspect);
            Extra.Add(listed);
        }

        public void AddNetwork(JsonObject network) => _networks.Add(network);

        /// <summary>Takes the container off a network behind Gitberth's back, as a recreate elsewhere does.</summary>
        public void Drop(string container, string network)
        {
            ((JsonObject)_inspects[container]["NetworkSettings"]!["Networks"]!).Remove(network);
            _changed.Add((string)_inspects[container]["Id"]!);
        }

        /// <summary>
        /// Replaces one of <see cref="Extra"/> with a new container of the same name, on Docker's default bridge alone, as
        /// docker run, compose up or an image update does. Returns its ID.
        /// </summary>
        public string Recreate(string container)
        {
            var old = _inspects[container];
            var oldId = (string)old["Id"]!;
            var id = new string('e', 64);
            var inspect = (JsonObject)old.DeepClone();
            inspect["Id"] = id;
            inspect["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["bridge"] = new JsonObject { ["IPAddress"] = "172.17.0.9" } } };
            _inspects.Remove(oldId);
            Know(inspect);
            Extra.Single(e => (string)e["Id"]! == oldId)["Id"] = id;
            _changed.Add(id);
            return id;
        }

        /// <summary>Takes one of <see cref="Extra"/> away, as docker rm does.</summary>
        public void Remove(string container)
        {
            var id = (string)_inspects[container]["Id"]!;
            _inspects.Remove(container);
            _inspects.Remove(id);
            Extra.RemoveAll(e => (string)e["Id"]! == id);
        }

        private void Know(JsonObject inspect) => _inspects[(string)inspect["Id"]!] = _inspects[((string)inspect["Name"]!).TrimStart('/')] = inspect;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_calls) _calls.Add(path);
            lock (_requests) _requests.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Writes) Writes.Add((path, body));
                if (Unanswered is { } lost && path.EndsWith("/" + lost.Action, StringComparison.Ordinal))
                {
                    if (lost.Done) Change(path, JsonNode.Parse(body)!.AsObject());
                    throw new HttpRequestException("The response ended prematurely.");
                }
                return Change(path, JsonNode.Parse(body)!.AsObject());
            }
            var list = DockerFixtures.Json("containers-json.json").AsArray();
            foreach (var extra in Extra) list.Add(extra.DeepClone());
            // A container whose networks changed is listed on the networks it's on now.
            foreach (var entry in list.OfType<JsonObject>().Where(e => _changed.Contains((string)e["Id"]!)))
                entry["NetworkSettings"] = new JsonObject { ["Networks"] = _inspects[(string)entry["Id"]!]["NetworkSettings"]!["Networks"]!.DeepClone() };
            return path switch
            {
                "/version" => Answer(Version()),
                "/v1.56/containers/json" or "/v1.47/containers/json" => Answer(list.ToJsonString()),
                "/v1.56/networks" or "/v1.47/networks" => Answer(_networks.ToJsonString()),
                _ when Network(path) is { } name => FindNetwork(name) is { } network ? Answer(network.ToJsonString()) : Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound),
                _ when Container(path) is { } id && Failing.Contains(id) => Answer("""{"message":"context deadline exceeded"}""", HttpStatusCode.InternalServerError),
                _ when Container(path) is { } id && _inspects.TryGetValue(id, out var inspect) => Answer(inspect.ToJsonString()),
                _ => Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound),
            };
        }

        // POST /v1.56/networks/{network}/connect and /disconnect.
        private HttpResponseMessage Change(string path, JsonObject body)
        {
            var parts = path.Split('/');
            if (parts.Length != 5 || parts[2] != "networks" || FindNetwork(Uri.UnescapeDataString(parts[3])) is not { } network)
                return Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound);
            if (!_inspects.TryGetValue((string)body["Container"]!, out var inspect)) return Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound);
            var name = (string)network["Name"]!;
            var endpoints = (JsonObject)inspect["NetworkSettings"]!["Networks"]!;
            _changed.Add((string)inspect["Id"]!);
            if (parts[4] == "disconnect") return endpoints.Remove(name) ? Answer("") : Answer($$"""{"message":"container is not connected to network {{name}}"}""", HttpStatusCode.Forbidden);
            if (endpoints.ContainsKey(name)) return Answer($$"""{"message":"endpoint with name x already exists in network {{name}}"}""", HttpStatusCode.Forbidden);
            var config = body["EndpointConfig"] as JsonObject;
            var aliases = (config?["Aliases"] as JsonArray ?? []).Select(a => (string)a!).ToList();
            endpoints[name] = new JsonObject
            {
                ["NetworkID"] = (string)network["Id"]!,
                ["Aliases"] = config?["Aliases"]?.DeepClone(),
                ["DNSNames"] = new JsonArray([((string)inspect["Name"]!).TrimStart('/'), .. aliases.Select(a => (JsonNode)a)]),
                ["GwPriority"] = config?["GwPriority"]?.DeepClone() ?? 0,
                ["IPAddress"] = "",
            };
            return Answer("");
        }

        private string Version()
        {
            var version = DockerFixtures.Json("version.json").AsObject();
            version["ApiVersion"] = ApiVersion;
            return version.ToJsonString();
        }

        private JsonObject? FindNetwork(string nameOrId) =>
            _networks.OfType<JsonObject>().FirstOrDefault(n => (string)n["Name"]! == nameOrId || (string)n["Id"]! == nameOrId);

        private static string? Network(string path) => path.Split('/') is [_, _, "networks", var name] ? Uri.UnescapeDataString(name) : null;

        private static string? Container(string path) => path.Split('/') is [_, _, "containers", var id, "json"] ? Uri.UnescapeDataString(id) : null;

        private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>A user-defined network Docker has besides CT 101's, made by Compose for <paramref name="project"/> when one is given.</summary>
    private static JsonObject Net(string name, string? project = null) => new()
    {
        ["Name"] = name,
        ["Id"] = (name + new string('0', 64))[..64].Replace('_', '0').Replace('-', '0'),
        ["Driver"] = "bridge",
        ["Scope"] = "local",
        ["Internal"] = false,
        ["Ingress"] = false,
        ["IPAM"] = new JsonObject { ["Config"] = new JsonArray() },
        ["Labels"] = project is null ? new JsonObject() : new JsonObject { [NetworkService.ComposeProjectLabel] = project },
        ["Options"] = new JsonObject(),
    };

    /// <summary>A running container besides CT 101's, as its inspect and its entry in Docker's list.</summary>
    private static (JsonObject Inspect, JsonObject Listed) Made(string name, string networkMode, Dictionary<string, string>? labels = null, params string[] networks)
    {
        var id = (name.Replace('-', '0') + new string('0', 64))[..64];
        JsonObject Endpoints() => new(networks.Select(n => KeyValuePair.Create(n, (JsonNode?)new JsonObject { ["Aliases"] = null, ["DNSNames"] = new JsonArray(name), ["GwPriority"] = 0, ["IPAddress"] = "" })));
        JsonObject Labels() => new((labels ?? []).Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value)));
        var inspect = new JsonObject
        {
            ["Id"] = id,
            ["Name"] = "/" + name,
            ["Config"] = new JsonObject { ["Hostname"] = id[..12], ["Labels"] = Labels() },
            ["HostConfig"] = new JsonObject { ["NetworkMode"] = networkMode },
            ["State"] = new JsonObject { ["Status"] = "running", ["Running"] = true },
            ["NetworkSettings"] = new JsonObject { ["Networks"] = Endpoints() },
        };
        var listed = new JsonObject
        {
            ["Id"] = id,
            ["Names"] = new JsonArray("/" + name),
            ["Image"] = "img",
            ["ImageID"] = "sha256:1",
            ["Command"] = "run",
            ["State"] = "running",
            ["Status"] = "Up 1 hour",
            ["Labels"] = Labels(),
            ["Ports"] = new JsonArray(),
            ["HostConfig"] = new JsonObject { ["NetworkMode"] = networkMode },
            ["NetworkSettings"] = new JsonObject { ["Networks"] = Endpoints() },
        };
        return (inspect, listed);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;
    private readonly Engine _engine = new();

    public NetworkServiceTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using var db = _db.CreateDbContext();
        db.Database.EnsureCreated();
        db.Apps.AddRange(new AppDefinition { Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb" }, new AppDefinition { Name = "Kanban", Slug = "kanban", Owner = "acme", Repo = "kanban" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _engine.Dispose();
        _connection.Dispose();
    }

    private NetworkService Service(bool inContainer = false) =>
        new(new DockerApi("unix:///var/run/docker.sock", _engine), _db, new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = inContainer });

    private static NetworkOverview Overview() => NetworkService.Build(
        NetworkInfo.ParseList(DockerFixtures.Text("networks-json.json")),
        NetworkContainer.ParseList(DockerFixtures.Text("containers-json.json")),
        [new NavApp(1, "orb", "orb"), new NavApp(2, "Kanban", "kanban")], "gitberth", Self);

    [Fact]
    public void Lists_networks_with_members_running_or_stopped()
    {
        var overview = Overview();

        Assert.Equal(["gitberth", "gitberth_default", "bridge", "docker_gwbridge", "host", "none"], overview.Networks.Select(n => n.Network.Name));
        var gitberth = overview.Networks[0];
        Assert.Equal(["gitberth (Gitberth)", "Kanban", "orb", "postgres"], gitberth.Members.Select(m => m.Title));
        // Every container's page is an app's, by its name.
        Assert.Equal(("apps/gb-kanban", "172.21.0.2", true), (gitberth.Members[1].Link, gitberth.Members[1].IpAddress, gitberth.Members[1].Running));
        // postgres is stopped: still a member, with no address until it starts.
        Assert.Equal(("apps/postgres", false), (gitberth.Members[3].Link, gitberth.Members[3].Running));
        Assert.Null(gitberth.Members[3].IpAddress);

        var counts = overview.Networks.ToDictionary(n => n.Network.Name, n => n.Members.Count);
        Assert.Equal(1, counts["gitberth_default"]);
        Assert.Equal(3, counts["bridge"]);
        Assert.Equal(0, counts["docker_gwbridge"]);
        Assert.Equal(1, counts["host"]);
        Assert.Equal(0, counts["none"]);
        // The host's own network has no addresses or names to list.
        Assert.False(overview.Networks.Single(n => n.Network.Name == "host").Expandable);
        Assert.True(gitberth.Expandable);
    }

    [Fact]
    public void Names_docker_swarm_compose_and_gitberth_networks()
    {
        var views = Overview().Networks.ToDictionary(n => n.Network.Name);

        Assert.Equal("Gitberth's shared network", views["gitberth"].Note);
        Assert.Equal("Gitberth's compose project", views["gitberth_default"].Note);
        Assert.Equal("Docker's default: no names", views["bridge"].Note);
        Assert.Equal("Docker Swarm's; not in use", views["docker_gwbridge"].Note);
        Assert.Equal("The host's own network", views["host"].Note);
        Assert.Equal("No network", views["none"].Note);

        static NetworkInfo Net(string name, bool ingress = false, Dictionary<string, string>? labels = null) =>
            new("id", name, ingress ? "overlay" : "bridge", "local", false, ingress, [], [], labels ?? [], new Dictionary<string, string>());
        Assert.Equal("Docker Compose (db)", NetworkService.NoteOf(Net("db_default", labels: new() { [NetworkService.ComposeProjectLabel] = "db" }), "gitberth", "gitberth", 2));
        Assert.Equal("Docker Swarm's", NetworkService.NoteOf(Net("ingress", ingress: true), "gitberth", "gitberth", 3));
        Assert.Null(NetworkService.NoteOf(Net("backend"), "gitberth", "gitberth", 1));

        // Docker's own networks are never offered for joining; the shared one is marked.
        Assert.All(new[] { "bridge", "docker_gwbridge", "host", "none" }, n => Assert.True(views[n].System));
        Assert.False(views["gitberth"].System || views["gitberth_default"].System);
        Assert.True(views["gitberth"].Shared);
        Assert.False(views["gitberth_default"].Shared);
    }

    [Fact]
    public void Names_leave_out_the_short_id_and_an_id_like_hostname()
    {
        Assert.Equal(["kanban", "gb-kanban"], NetworkService.NamesOn(DockerFixtures.Inspect("gb-kanban"), "gitberth"));
        Assert.Equal(["postgres"], NetworkService.NamesOn(DockerFixtures.Inspect("postgres"), "gitberth"));
        Assert.Equal(["gitberth"], NetworkService.NamesOn(DockerFixtures.Inspect("gitberth"), "gitberth_default"));
        Assert.Empty(NetworkService.NamesOn(DockerFixtures.Inspect("sample-index"), "bridge"));
        Assert.Empty(NetworkService.NamesOn(DockerFixtures.Inspect("postgres"), "elsewhere"));
    }

    [Fact]
    public async Task Reads_each_containers_ports_once_and_leaves_out_helpers()
    {
        var helper = DockerFixtures.Json("containers-json.json").AsArray()[0]!.AsObject().DeepClone().AsObject();
        helper["Id"] = "4e1b1c0ffee000000000000000000000000000000000000000000000000000000";
        helper["Names"] = new JsonArray("/eager_turing");
        helper["Labels"] = new JsonObject { [HostScanHelper.Label] = "ports" };
        _engine.Extra.Add(helper);
        var service = Service();

        var first = await service.SnapshotAsync();
        Assert.Equal(8, first.Containers.Count);
        Assert.DoesNotContain(first.Containers, c => c.Name == "eager_turing");
        Assert.Equal([new PortBinding("0.0.0.0", 9100, 80, "tcp")], first.ConfigOf(first.Containers.Single(c => c.Name == "sample-index")).Bindings);
        Assert.Equal(8, _engine.Inspected.Count);

        // A container's configuration never changes, so it's read once.
        var second = await service.SnapshotAsync();
        Assert.Equal(8, _engine.Inspected.Count);
        Assert.Equal([new PortBinding(null, 3001, 3000, "tcp")], second.ConfigOf(second.Containers.Single(c => c.Name == "gb-kanban")).Bindings);
    }

    [Fact]
    public async Task Lists_the_networks_from_docker_with_gitberths_apps()
    {
        var overview = await Service(inContainer: false).ListAsync();

        Assert.Null(overview.Error);
        var gitberth = overview.Networks.Single(n => n.Network.Name == "gitberth");
        // Outside a container, Gitberth isn't any of them.
        Assert.Equal(["gitberth", "Kanban", "orb", "postgres"], gitberth.Members.Select(m => m.Title));
        Assert.Equal("apps/gb-kanban", gitberth.Members[1].Link);
    }

    [Fact]
    public async Task A_networks_members_get_their_names_when_it_opens()
    {
        var service = Service();

        var members = await service.MembersAsync("gitberth");
        Assert.Equal(["kanban", "gb-kanban"], members.Single(m => m.Container == "gb-kanban").Names);
        Assert.Equal(["postgres"], members.Single(m => m.Container == "postgres").Names);

        // Docker's default bridge has no names, so its members aren't read.
        var before = _engine.Inspected.Count;
        Assert.All(await service.MembersAsync("bridge"), m => Assert.Empty(m.Names));
        Assert.Equal(before, _engine.Inspected.Count);
    }

    [Fact]
    public async Task A_member_docker_doesnt_answer_for_is_listed_without_names()
    {
        _engine.Failing.Add("gb-kanban");

        var members = await Service().MembersAsync("gitberth");

        // Every member is still there; the names of the one that failed, and the ones after it, wait for the next look.
        Assert.Equal(["gitberth", "gb-kanban", "gb-orb", "postgres"], members.Select(m => m.Container));
        Assert.NotEmpty(members[0].Names);
        Assert.All(members.Skip(1), m => Assert.Empty(m.Names));
        Assert.Equal(["gitberth", "gb-kanban"], _engine.Inspected);
    }

    [Fact]
    public async Task Says_why_when_docker_cant_be_read()
    {
        using var broken = new DockerApi("unix:///var/run/docker.sock", new FailingEngine());
        var overview = await new NetworkService(broken, _db, new GitberthOptions { DataDir = Path.GetTempPath() }).ListAsync();

        Assert.Empty(overview.Networks);
        Assert.Equal("Cannot connect to the Docker daemon", overview.Error);
    }

    [Fact]
    public async Task Joins_with_gateway_priority_minus_one()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();

        var joined = await service.JoinAsync("sample-bun-hono", "media", alias: "hono");

        // -1: media never becomes its gateway, so its published ports and its way out stay on bridge.
        Assert.Equal((null, "sample-bun-hono joined media. Containers there reach it as sample-bun-hono or hono.", null), (joined.Error, joined.Done, joined.Warning));
        Assert.Equal([("/v1.56/networks/media/connect", """{"Container":"sample-bun-hono","EndpointConfig":{"GwPriority":-1,"Aliases":["hono"]}}""")], _engine.Writes);
        // Kept, so it's joined again when something recreates it.
        using (var db = _db.CreateDbContext())
            Assert.Equal(("sample-bun-hono", "media", "hono"), db.KeptNetworks.AsEnumerable().Select(k => (k.ContainerName, k.Network, k.Alias)).Single());

        // An app's container answers to the app's network name there as well, and its deploys join it again: it's saved for the app.
        var app = await service.JoinAsync("gb-kanban", "media");
        Assert.Equal("Kanban joined media. Containers there reach it as kanban.", app.Done);
        Assert.Equal("""{"Container":"gb-kanban","EndpointConfig":{"GwPriority":-1,"Aliases":["kanban"]}}""", _engine.Writes[^1].Body);
        using (var db = _db.CreateDbContext())
            Assert.Equal((2, "media", (string?)null), db.AppNetworks.AsEnumerable().Select(n => (n.AppId, n.Network, n.Alias)).Single());
    }

    [Fact]
    public async Task Joins_without_a_priority_before_docker_28()
    {
        _engine.ApiVersion = "1.47";
        _engine.AddNetwork(Net("media"));

        var joined = await Service().JoinAsync("sample-bun-hono", "media");

        // Docker 27 has no gateway priorities, and refuses none; it says what that may mean.
        Assert.Equal("sample-bun-hono joined media. Containers there reach it as sample-bun-hono. Docker before 28 may move its published ports for a moment.", joined.Done);
        Assert.Equal([("/v1.47/networks/media/connect", """{"Container":"sample-bun-hono","EndpointConfig":{}}""")], _engine.Writes);
    }

    [Fact]
    public async Task Leaving_stops_keeping_it()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();
        await service.JoinAsync("sample-bun-hono", "media");

        // While it's kept, a container recreated without it (docker run, compose up) is joined to it again.
        _engine.Drop("sample-bun-hono", "media");
        Assert.Equal(["sample-bun-hono joined media again."], (await service.RejoinAsync("sample-bun-hono")).Select(r => r.Done));

        var left = await service.LeaveAsync("sample-bun-hono", "media");
        Assert.Equal("sample-bun-hono left media.", left.Done);
        Assert.Equal(("/v1.56/networks/media/disconnect", """{"Container":"sample-bun-hono","Force":false}"""), _engine.Writes[^1]);
        using (var db = _db.CreateDbContext()) Assert.Empty(db.KeptNetworks);
        // Once left, nothing joins it again.
        var writes = _engine.Writes.Count;
        Assert.Empty(await service.RejoinAsync("sample-bun-hono"));
        Assert.Equal(writes, _engine.Writes.Count);

        // A network it's kept on but was taken off elsewhere just stops being kept: there's nothing to disconnect.
        await service.JoinAsync("sample-bun-elysia", "media");
        _engine.Drop("sample-bun-elysia", "media");
        Assert.True((await service.LeaveAsync("sample-bun-elysia", "media")).Ok);
        Assert.DoesNotContain(_engine.Writes, w => w.Path.EndsWith("/disconnect", StringComparison.Ordinal) && w.Body.Contains("elysia", StringComparison.Ordinal));
        using (var db = _db.CreateDbContext()) Assert.Empty(db.KeptNetworks);

        // An app's next deploys leave it off.
        await service.JoinAsync("gb-kanban", "media");
        Assert.Equal("Kanban left media.", (await service.LeaveAsync("gb-kanban", "media")).Done);
        using (var db = _db.CreateDbContext()) Assert.Empty(db.AppNetworks);
    }

    [Fact]
    public async Task Never_lets_an_app_or_gitberth_leave_gitberth()
    {
        var service = Service();

        // L6 (round 2): in the words of every app, not of a kind of app.
        Assert.Equal("Kanban always stays on gitberth: Gitberth made it there, and others reach it there as kanban.", (await service.LeaveAsync("gb-kanban", "gitberth")).Error);
        Assert.Equal("orb always stays on gitberth: Gitberth made it there, and others reach it there as orb.", (await service.PlanLeaveAsync("gb-orb", "gitberth")).Refusal);
        Assert.Equal("Gitberth needs gitberth to reach your apps.", (await service.LeaveAsync("gitberth", "gitberth")).Error);
        // Nor its compose project's network: its own page is published there.
        Assert.Equal("Gitberth stays on its networks: leaving one could cut off its own page.", (await service.LeaveAsync("gitberth", "gitberth_default")).Error);
        // Any other container may.
        Assert.Null((await service.PlanLeaveAsync("postgres", "gitberth")).Refusal);
        Assert.Empty(_engine.Writes);
    }

    [Fact]
    public async Task Refuses_host_none_and_container_modes()
    {
        _engine.AddNetwork(Net("media"));
        var (isolated, listedIsolated) = Made("batch", "none");
        _engine.Add(isolated, listedIsolated);
        var hono = (string)DockerFixtures.Inspect("sample-bun-hono")["Id"]!;
        // Compose writes network_mode: service:x as container:<x's full ID>.
        var (sidecar, listedSidecar) = Made("sidecar", "container:" + hono);
        _engine.Add(sidecar, listedSidecar);
        var service = Service();

        Assert.Equal("cloudflare-tunnel uses the host's network, so it can't join other networks.", (await service.JoinAsync("cloudflare-tunnel", "media")).Error);
        Assert.Equal("batch has no network (network mode none).", (await service.JoinAsync("batch", "media")).Error);
        Assert.Equal("sidecar shares the network of sample-bun-hono, so its networks are sample-bun-hono's.", (await service.JoinAsync("sidecar", "media")).Error);
        Assert.Equal("cloudflare-tunnel uses the host's network: there are no Docker networks to leave.", (await service.LeaveAsync("cloudflare-tunnel", "host")).Error);
        // Docker's own networks are never joined either.
        foreach (var network in new[] { "bridge", "host", "none", "docker_gwbridge" })
            Assert.Equal($"{network} is Docker's own network: Gitberth doesn't join containers to it.", (await service.PlanJoinAsync("postgres", network)).Refusal);
        Assert.Empty(_engine.Writes);
    }

    [Fact]
    public async Task Warns_when_a_name_is_taken_on_the_network()
    {
        var service = Service();

        // Under the name apps reach the shared database by: both would answer to it.
        var plan = await service.PlanJoinAsync("sample-bun-hono", "gitberth", alias: "postgres");
        Assert.Equal((null, "Another container on gitberth is already called postgres. Both would answer to postgres."), (plan.Refusal, plan.Taken));
        Assert.Equal(["sample-bun-hono", "postgres"], plan.Names);
        // A warning, not a refusal: it joins, and says so again.
        var joined = await service.JoinAsync("sample-bun-hono", "gitberth", alias: "postgres");
        Assert.Equal((true, plan.Taken), (joined.Ok, joined.Warning));

        Assert.Equal("Another container on gitberth is already called gitberth. Both would answer to gitberth.",
            (await service.PlanJoinAsync("sample-bun-elysia", "gitberth", alias: "gitberth")).Taken);
        Assert.Equal("Another container on gitberth is already called kanban. Both would answer to kanban.",
            (await service.PlanJoinAsync("sample-bun-elysia", "gitberth", alias: "kanban")).Taken);
        Assert.Null((await service.PlanJoinAsync("sample-bun-elysia", "gitberth", alias: "elysia")).Taken);
    }

    [Fact]
    public async Task Never_calls_network_prune()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();

        // media is left empty twice over, and stays.
        await service.JoinAsync("sample-bun-hono", "media");
        await service.LeaveAsync("sample-bun-hono", "media");
        await service.JoinAsync("gb-kanban", "media");
        await service.LeaveAsync("gb-kanban", "media");

        Assert.Equal(4, _engine.Writes.Count);
        Assert.All(_engine.Writes, w => Assert.Matches("^/v1\\.56/networks/media/(connect|disconnect)$", w.Path));
        Assert.DoesNotContain(_engine.Requests, r => r.Contains("prune", StringComparison.Ordinal) || r.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaves_a_network_compose_put_a_container_on_to_compose()
    {
        _engine.AddNetwork(Net("db_default", project: "db"));
        var (pg, listed) = Made("db-postgres-1", "db_default", new()
        {
            [NetworkService.ComposeProjectLabel] = "db",
            ["com.docker.compose.service"] = "postgres",
            ["com.docker.compose.project.config_files"] = "/opt/db/docker-compose.yml",
        }, "db_default");
        _engine.Add(pg, listed);
        var service = Service();

        // Compose would put it back on db_default the next time it recreates it.
        Assert.Equal("Docker Compose put db-postgres-1 on db_default (project db) and puts it back whenever it recreates it. Change /opt/db/docker-compose.yml instead.",
            (await service.LeaveAsync("db-postgres-1", "db_default")).Error);
        // A network Gitberth joined it to is Gitberth's to take it off again.
        Assert.True((await service.JoinAsync("db-postgres-1", "gitberth")).Ok);
        Assert.Equal((null, false, null), Lines(await service.PlanLeaveAsync("db-postgres-1", "gitberth")));
        Assert.True((await service.LeaveAsync("db-postgres-1", "gitberth")).Ok);
    }

    [Fact]
    public async Task Lists_a_containers_networks_as_its_card_shows_them()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();
        static List<(string, string, string?, string?, bool, bool, string?)> Rows(ContainerNetworks card) =>
            card.Networks.Select(n => (n.Network, string.Join(",", n.Names), n.IpAddress, n.Note, n.Kept, n.Attached, n.LeaveRefusal)).ToList();

        // postgres: stopped on the shared network, so without an address.
        var postgres = await service.NetworksOfAsync("postgres");
        Assert.Equal([("gitberth", "postgres", null, "Shared network", false, true, null)], Rows(postgres));
        Assert.Equal(["gitberth_default", "media"], postgres.Joinable);
        Assert.Null(postgres.Note);
        // Joined to media under another name, it's kept there.
        await service.JoinAsync("postgres", "media", alias: "db");
        var media = (await service.NetworksOfAsync("postgres")).Networks.Single(x => x.Network == "media");
        Assert.Equal(("postgres,db", "Joined by Gitberth · kept", true), (string.Join(",", media.Names), media.Note, media.Kept));

        // An app is always on the shared network, and the networks saved for it come with every deploy.
        await service.JoinAsync("gb-kanban", "media");
        var kanban = await service.NetworksOfAsync("gb-kanban");
        Assert.Equal(
            [
                ("gitberth", "kanban,gb-kanban", "172.21.0.2", "Shared network · always on", false, true,
                    "Kanban always stays on gitberth: Gitberth made it there, and others reach it there as kanban."),
                ("media", "kanban,gb-kanban", null, "Joined by Gitberth · every deploy", true, true, null),
            ],
            Rows(kanban));
        Assert.Equal(["gitberth_default"], kanban.Joinable);

        // Gitberth stays on both of its own.
        Assert.Equal(
            [
                ("gitberth", "Shared network", "Gitberth needs gitberth to reach your apps."),
                ("gitberth_default", "Gitberth's compose project", "Gitberth stays on its networks: leaving one could cut off its own page."),
            ],
            (await service.NetworksOfAsync("gitberth")).Networks.Select(x => (x.Network, x.Note, x.LeaveRefusal)));
        // On the host's network there's nothing to list or join.
        var tunnel = await service.NetworksOfAsync("cloudflare-tunnel");
        Assert.Equal("Uses the host's network: there are no Docker networks to join.", tunnel.Note);
        Assert.Empty(tunnel.Networks);
        Assert.Empty(tunnel.Joinable);
    }

    [Fact]
    public async Task Says_where_its_published_ports_go_when_it_leaves()
    {
        _engine.AddNetwork(Net("media"));
        // Published as -p 9101:80, on Docker's default bridge only.
        var (web, listed) = Made("web", "bridge", null, "bridge");
        web["HostConfig"]!["PortBindings"] = new JsonObject { ["80/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "", ["HostPort"] = "9101" }) };
        _engine.Add(web, listed);
        var service = Service();
        Assert.Equal((null, true, null), Lines(await service.PlanLeaveAsync("web", "bridge")));

        await service.JoinAsync("web", "media");

        // bridge carries its ports; media, joined at -1, only gets them when bridge goes.
        Assert.Equal((null, false, "media"), Lines(await service.PlanLeaveAsync("web", "bridge")));
        Assert.Equal((null, false, null), Lines(await service.PlanLeaveAsync("web", "media")));
    }

    // A leave's refusal and where its published ports go; who loses its name is Leaving_says_who_loses_the_name's.
    private static (string?, bool, string?) Lines(LeavePlan plan) => (plan.Refusal, plan.OnlyNetwork, plan.PortsMoveTo);

    /// <summary>A running container besides CT 101's on these networks, whose settings are <paramref name="env"/>.</summary>
    private (JsonObject Inspect, JsonObject Listed) AddWithEnv(string name, string[] env, params string[] networks)
    {
        var (inspect, listed) = Made(name, networks[0], null, networks);
        inspect["Config"]!["Env"] = new JsonArray([.. env.Select(e => (JsonNode)e)]);
        _engine.Add(inspect, listed);
        return (inspect, listed);
    }

    [Fact]
    public async Task Leaving_says_who_loses_the_name()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();
        await service.JoinAsync("gb-kanban", "media", alias: "db");
        // On media: jellyfin's settings name kanban by its extra name there; sonarr names kanban too, but reaches it on
        // gitberth as well; bazarr names neither; radarr names it, but isn't running.
        AddWithEnv("jellyfin", ["TZ=UTC", "DATABASE_URL=postgres://media:REDACTED@db:5432/media"], "media");
        AddWithEnv("sonarr", ["KANBAN_URL=http://kanban:3000"], "media", "gitberth");
        AddWithEnv("bazarr", ["TZ=UTC"], "media");
        var (_, radarr) = AddWithEnv("radarr", ["DB_HOST=db"], "media");
        radarr["State"] = "exited";

        var plan = await service.PlanLeaveAsync("gb-kanban", "media");

        Assert.Equal((null, false, null), Lines(plan));
        Assert.Equal(["jellyfin"], plan.Users);
        // Nobody there names bazarr. Nobody is named for a network it can't leave, or isn't on.
        Assert.Empty((await service.PlanLeaveAsync("bazarr", "media")).Users!);
        Assert.Null((await service.PlanLeaveAsync("gb-kanban", "gitberth")).Users);
        _engine.Drop("gb-kanban", "media");
        Assert.Null((await service.PlanLeaveAsync("gb-kanban", "media")).Users);
    }

    [Fact]
    public async Task An_extra_name_is_kept_in_lower_case()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();

        // docker run lower-cases the names an app's deploys give it there, and Docker's DNS ignores case: the join gives it
        // what every deploy will.
        Assert.Equal(["kanban", "db"], (await service.PlanJoinAsync("gb-kanban", "media", alias: "DB")).Names);
        Assert.Equal("Kanban joined media. Containers there reach it as kanban or db.", (await service.JoinAsync("gb-kanban", "media", alias: " DB ")).Done);
        Assert.Equal("""{"Container":"gb-kanban","EndpointConfig":{"GwPriority":-1,"Aliases":["kanban","db"]}}""", _engine.Writes[^1].Body);
        using var db = _db.CreateDbContext();
        Assert.Equal("db", db.AppNetworks.Single().Alias);
    }

    [Fact]
    public async Task An_app_never_joins_a_network_under_a_name_another_container_has_there()
    {
        _engine.AddNetwork(Net("media"));
        var (database, listedDatabase) = Made("db", "media", null, "media");
        _engine.Add(database, listedDatabase);
        var service = Service();

        // Docker's DNS would answer db with both: the media stack's connections to its database would reach kanban too.
        Assert.Equal("Another container on media is already called db. Pick another name.", (await service.PlanJoinAsync("gb-kanban", "media", alias: "db")).Refusal);
        Assert.Equal("Another container on media is already called db. Pick another name.", (await service.JoinAsync("gb-kanban", "media", alias: "db")).Error);
        // Nor under the app's own name.
        var (other, listedOther) = Made("kanban", "media", null, "media");
        _engine.Add(other, listedOther);
        Assert.Equal("Another container on media is already called kanban, so Kanban can't join it: both would answer to kanban.",
            (await service.PlanJoinAsync("gb-kanban", "media")).Refusal);
        Assert.Empty(_engine.Writes);
        using (var db = _db.CreateDbContext()) Assert.Empty(db.AppNetworks);
        // Any other container is only warned (Warns_when_a_name_is_taken_on_the_network).
        var warned = await service.PlanJoinAsync("sample-bun-hono", "media", alias: "db");
        Assert.Equal((null, "Another container on media is already called db. Both would answer to db."), (warned.Refusal, warned.Taken));
    }

    [Fact]
    public async Task A_join_or_leave_whose_answer_is_lost_goes_by_what_docker_shows()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();

        // Docker joins kanban, but its answer is lost: kanban is on media, so it's saved, or its next deploy would leave it off.
        _engine.Unanswered = ("connect", Done: true);
        var joined = await service.JoinAsync("gb-kanban", "media");
        Assert.Equal((null, "Kanban joined media. Containers there reach it as kanban."), (joined.Error, joined.Done));
        using (var db = _db.CreateDbContext()) Assert.Equal("media", db.AppNetworks.Single().Network);

        // A leave Docker carried out is forgotten, or the next deploy would join it again.
        _engine.Unanswered = ("disconnect", Done: true);
        var left = await service.LeaveAsync("gb-kanban", "media");
        Assert.Equal((null, "Kanban left media."), (left.Error, left.Done));
        using (var db = _db.CreateDbContext()) Assert.Empty(db.AppNetworks);

        // One Docker didn't carry out is said, and nothing is saved.
        _engine.Unanswered = ("connect", Done: false);
        Assert.Equal("Couldn't join media: The response ended prematurely.", (await service.JoinAsync("gb-kanban", "media")).Error);
        using (var db = _db.CreateDbContext()) Assert.Empty(db.AppNetworks);
    }

    /// <summary>Fails every write to the table, as a locked or full database does; reads work, so Docker has been told to change by then.</summary>
    private sealed class FailWrites(string table) : DbCommandInterceptor
    {
        private bool Writes(DbCommand command) =>
            command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal) && command.CommandText.TrimStart() is var sql
            && (sql.StartsWith("INSERT", StringComparison.Ordinal) || sql.StartsWith("UPDATE", StringComparison.Ordinal) || sql.StartsWith("DELETE", StringComparison.Ordinal));

        private static SqliteException Locked() => new("SQLite Error 5: 'database is locked'.", 5);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => Writes(command) ? throw Locked() : ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => Writes(command) ? throw Locked() : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task A_join_or_leave_gitberth_cant_write_down_is_undone_in_docker()
    {
        _engine.AddNetwork(Net("media"));
        var locked = new NetworkService(new DockerApi("unix:///var/run/docker.sock", _engine),
            new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).AddInterceptors(new FailWrites("AppNetworks")).Options),
            new GitberthOptions { DataDir = Path.GetTempPath() });
        async Task<List<(string, bool)>> MediaAsync() =>
            (await Service().NetworksOfAsync("gb-kanban")).Networks.Where(n => n.Network == "media").Select(n => (n.Network, n.Attached)).ToList();

        // Docker joins kanban, then the row can't be saved: it leaves again, or the card would show it on media while its
        // next deploy left it off.
        Assert.Equal("Couldn't join media: SQLite Error 5: 'database is locked'.", (await locked.JoinAsync("gb-kanban", "media", alias: "db")).Error);
        Assert.Equal(["/v1.56/networks/media/connect", "/v1.56/networks/media/disconnect"], _engine.Writes.Select(w => w.Path));
        Assert.Empty(await MediaAsync());
        using (var db = _db.CreateDbContext()) Assert.Empty(db.AppNetworks);

        // Docker takes it off, then the row can't be removed: it joins again as it was, or its next deploy would join it again.
        Assert.True((await Service().JoinAsync("gb-kanban", "media", alias: "db")).Ok);
        _engine.Writes.Clear();
        Assert.Equal("Couldn't leave media: SQLite Error 5: 'database is locked'.", (await locked.LeaveAsync("gb-kanban", "media")).Error);
        Assert.Equal([
            ("/v1.56/networks/media/disconnect", """{"Container":"gb-kanban","Force":false}"""),
            ("/v1.56/networks/media/connect", """{"Container":"gb-kanban","EndpointConfig":{"GwPriority":-1,"Aliases":["kanban","db"]}}"""),
        ], _engine.Writes);
        Assert.Equal([("media", true)], await MediaAsync());
        using (var db = _db.CreateDbContext()) Assert.Equal(("media", "db"), db.AppNetworks.AsNoTracking().AsEnumerable().Select(n => (n.Network, n.Alias)).Single());
    }

    private NetworkKeeper Keeper(NetworkService service, ContainerEventWatcher? events = null, ContainerLocks? locks = null)
    {
        var api = new DockerApi("unix:///var/run/docker.sock", _engine);
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        return new NetworkKeeper(service, api, events ?? new ContainerEventWatcher(api, new StatsStore(options), NullLogger<ContainerEventWatcher>.Instance),
            locks ?? new ContainerLocks(), _db, NullLogger<NetworkKeeper>.Instance) { BusyPoll = TimeSpan.FromMilliseconds(10) };
    }

    [Fact]
    public async Task Joins_again_a_kept_network_after_a_recreate()
    {
        _engine.AddNetwork(Net("media"));
        _engine.AddNetwork(Net("gitberth-lan"));
        var (web, listed) = Made("web", "bridge", null, "bridge");
        _engine.Add(web, listed);
        var service = Service();
        Assert.True((await service.JoinAsync("web", "media", alias: "site")).Ok);
        // An own LAN address is kept the same way, pinned, so it's the same after a recreate.
        await using (var db = _db.CreateDbContext())
        {
            db.KeptNetworks.Add(new KeptNetwork { ContainerName = "web", Network = "gitberth-lan", IPv4Address = "10.1.1.230" });
            await db.SaveChangesAsync();
        }
        var joins = _engine.Writes.Count;

        // Recreated elsewhere: a new container under the name, on Docker's default bridge alone.
        _engine.Recreate("web");
        var keeper = Keeper(service);
        var again = await keeper.KeepAsync("web", default);

        // Joined again with the same alias and the same address, at -1: its published ports and its way out stay on bridge.
        Assert.Equal(["web joined gitberth-lan again.", "web joined media again."], again.Select(r => r.Done).Order(StringComparer.Ordinal));
        var writes = _engine.Writes.Skip(joins).ToList();
        Assert.Contains(("/v1.56/networks/media/connect", """{"Container":"web","EndpointConfig":{"GwPriority":-1,"Aliases":["site"]}}"""), writes);
        Assert.Contains(("/v1.56/networks/gitberth-lan/connect", """{"Container":"web","EndpointConfig":{"GwPriority":-1,"IPAMConfig":{"IPv4Address":"10.1.1.230"}}}"""), writes);
        // Nothing's missing any more.
        Assert.Empty(await keeper.KeepAsync("web", default));
        Assert.Equal(joins + 2, _engine.Writes.Count);
    }

    [Fact]
    public async Task Joins_again_when_it_hears_the_container_start()
    {
        _engine.AddNetwork(Net("media"));
        var service = Service();
        await service.JoinAsync("sample-bun-hono", "media");
        _engine.Drop("sample-bun-hono", "media");
        var api = new DockerApi("unix:///var/run/docker.sock", _engine);
        var events = new ContainerEventWatcher(api, new StatsStore(new GitberthOptions { DataDir = Path.GetTempPath() }), NullLogger<ContainerEventWatcher>.Instance);
        var locks = new ContainerLocks();
        var keeper = Keeper(service, events, locks);
        await keeper.StartAsync(default);
        try
        {
            // While Gitberth starts it, it's looked at once that's done.
            var start = await locks.AcquireAsync("sample-bun-hono");
            var now = DateTime.UtcNow;
            Assert.True(events.Heard(new ContainerEvent(now, "abc", "sample-bun-hono", "start"), now));
            await Task.Delay(100);
            Assert.DoesNotContain(_engine.Writes.Skip(1), w => w.Path.EndsWith("/connect", StringComparison.Ordinal));
            start.Dispose();

            for (var i = 0; i < 200 && _engine.Writes.Count < 2; i++) await Task.Delay(25);
            Assert.Equal(("/v1.56/networks/media/connect", """{"Container":"sample-bun-hono","EndpointConfig":{"GwPriority":-1}}"""), _engine.Writes[^1]);
        }
        finally
        {
            await keeper.StopAsync(default);
        }
    }

    [Fact]
    public async Task Forgets_what_it_kept_for_a_container_gone_at_two_looks_in_a_row()
    {
        _engine.AddNetwork(Net("media"));
        var (pg, listed) = Made("pg-old", "bridge", null, "bridge");
        _engine.Add(pg, listed);
        var service = Service();
        await service.JoinAsync("pg-old", "media");
        await service.JoinAsync("sample-bun-elysia", "media");
        await using (var db = _db.CreateDbContext())
        {
            db.ContainerPortNotes.Add(new ContainerPortNote { ContainerName = "pg-old", Port = 5432 });
            await db.SaveChangesAsync();
        }
        _engine.Remove("pg-old");
        // Taken off media elsewhere: joined again at the next look.
        _engine.Drop("sample-bun-elysia", "media");
        var keeper = Keeper(service);
        string Kept()
        {
            using var db = _db.CreateDbContext();
            return string.Join(",", db.KeptNetworks.AsEnumerable().Select(k => k.ContainerName).Concat(db.ContainerPortNotes.AsEnumerable().Select(n => $"{n.ContainerName}:{n.Port}")).Order(StringComparer.Ordinal));
        }

        await keeper.ReconcileAsync();
        // Gone at one look: it may be on its way back (docker rm, then docker run), so it's still kept.
        Assert.Equal("pg-old,pg-old:5432,sample-bun-elysia", Kept());
        Assert.Equal(("/v1.56/networks/media/connect", """{"Container":"sample-bun-elysia","EndpointConfig":{"GwPriority":-1}}"""), _engine.Writes[^1]);

        await keeper.ReconcileAsync();
        // Gone at the next as well: its networks and port notes are forgotten.
        Assert.Equal("sample-bun-elysia", Kept());
    }

    [Fact]
    public async Task On_its_timer_it_joins_again_only_after_a_start_it_missed()
    {
        _engine.AddNetwork(Net("media"));
        var (web, listed) = Made("web", "bridge", null, "bridge");
        web["State"]!["StartedAt"] = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        _engine.Add(web, listed);
        var service = Service();
        Assert.True((await service.JoinAsync("web", "media")).Ok);
        var keeper = Keeper(service);
        // At startup every container is looked at.
        await keeper.ReconcileAsync();
        var writes = _engine.Writes.Count;

        // Taken off media on purpose (docker network disconnect), with no start since: it stays off until its next start,
        // as its Networks card says.
        _engine.Drop("web", "media");
        await keeper.ReconcileAsync(startsOnly: true, default);
        Assert.Equal(writes, _engine.Writes.Count);

        // Started again while Docker's events weren't heard: the next look joins it again.
        web["State"]!["StartedAt"] = DateTime.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture);
        await keeper.ReconcileAsync(startsOnly: true, default);
        Assert.Equal(("/v1.56/networks/media/connect", """{"Container":"web","EndpointConfig":{"GwPriority":-1}}"""), _engine.Writes[^1]);

        // A start that was heard was looked at then: taken off after it, it stays off at the next look as well.
        web["State"]!["StartedAt"] = DateTime.UtcNow.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture);
        Assert.Empty(await keeper.KeepAsync("web", default));
        _engine.Drop("web", "media");
        writes = _engine.Writes.Count;
        await keeper.ReconcileAsync(startsOnly: true, default);
        Assert.Equal(writes, _engine.Writes.Count);
    }

    [Fact]
    public async Task Gitberth_knows_its_own_container_by_its_id_whatever_its_hostname()
    {
        // Gitberth started with docker run --hostname and without Compose's labels: only its ID, read from its own mounts,
        // says which container is Gitberth's.
        var (self, listed) = Made("gitberth-plain", "gitberth", null, "gitberth");
        _engine.Add(self, listed);
        var service = new NetworkService(new DockerApi("unix:///var/run/docker.sock", _engine), _db,
            new GitberthOptions { DataDir = Path.GetTempPath(), RunningInContainer = true, OwnContainer = (string)self["Id"]! });

        Assert.Equal("Gitberth needs gitberth to reach your apps.", (await service.LeaveAsync("gitberth-plain", "gitberth")).Error);
        Assert.Contains("gitberth-plain (Gitberth)", (await service.ListAsync()).Networks.Single(n => n.Network.Name == "gitberth").Members.Select(m => m.Title));
        Assert.Empty(_engine.Writes);
    }

    [Fact]
    public async Task Joins_and_leaves_wait_while_a_port_change_isnt_settled()
    {
        _engine.AddNetwork(Net("media"));
        var journal = new MemoryJournal();
        var locks = new ContainerLocks();
        var engine = new FakeEngine(new FakeClock());
        var host = new FakeHost(engine);
        var changer = new PortChanger(engine, journal, locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance);
        var service = new NetworkService(new DockerApi("unix:///var/run/docker.sock", _engine), _db, new GitberthOptions { DataDir = Path.GetTempPath() }, locks,
            changer: new Lazy<PortChanger>(() => changer));
        await using (var db = _db.CreateDbContext())
        {
            db.KeptNetworks.Add(new KeptNetwork { ContainerName = "postgres", Network = "media" });
            await db.SaveChangesAsync();
        }
        // A change of postgres a restart of Gitberth cut short, which recovery hasn't settled yet: it may yet put the
        // original back, on the networks it had.
        var row = await journal.AddAsync(new ContainerChange
        {
            ContainerName = "postgres",
            Summary = "published 5432/tcp on the LAN",
            OldId = "0ebf31bbcd07" + new string('a', 52),
            OldName = "postgres_old_0ebf31bbcd07",
            TempName = "postgres_new_0ebf31bbcd07",
            WasRunning = true,
            OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""",
            Step = ChangeStep.Verifying,
        }, default);
        const string busy = "Gitberth is already changing postgres. Try again when that's done.";

        Assert.Equal(busy, (await service.PlanJoinAsync("postgres", "gitberth_default")).Refusal);
        Assert.Equal(busy, (await service.JoinAsync("postgres", "gitberth_default")).Error);
        Assert.Equal(busy, (await service.PlanLeaveAsync("postgres", "gitberth")).Refusal);
        Assert.Equal(busy, (await service.LeaveAsync("postgres", "gitberth")).Error);
        Assert.Empty(await service.RejoinAsync("postgres"));
        Assert.Empty(_engine.Writes);
        using (var db = _db.CreateDbContext()) Assert.Equal("media", db.KeptNetworks.Single().Network);

        // Settled: they go ahead.
        await journal.UpdateAsync(row with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done }, default);
        Assert.Equal(["postgres joined media again."], (await service.RejoinAsync("postgres")).Select(r => r.Done));
        Assert.True((await service.LeaveAsync("postgres", "media")).Ok);
    }

    [Fact]
    public async Task A_non_git_apps_join_waits_for_its_change_and_holds_the_container_lock()
    {
        // whoami came from a registry: its container is gb-whoami, which the change engine recreates, not a deploy.
        _engine.AddNetwork(Net("media"));
        var (inspect, listed) = Made("gb-whoami", "gitberth",
            new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "whoami", [ContainerSpecFactory.SourceLabel] = "registry" }, "gitberth");
        _engine.Add(inspect, listed);
        int whoami, kanban;
        await using (var db = _db.CreateDbContext())
        {
            var app = new AppDefinition { Name = "whoami", Slug = "whoami", Source = Gitberth.Apps.AppSource.Registry };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            whoami = app.Id;
            kanban = db.Apps.Single(a => a.Slug == "kanban").Id;
        }
        var journal = new MemoryJournal();
        var locks = new ContainerLocks();
        var appLocks = new AppLocks();
        var engine = new FakeEngine(new FakeClock());
        var host = new FakeHost(engine);
        var changer = new PortChanger(engine, journal, locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance);
        var service = new NetworkService(new DockerApi("unix:///var/run/docker.sock", _engine), _db, new GitberthOptions { DataDir = Path.GetTempPath() }, locks, appLocks,
            new Lazy<PortChanger>(() => changer));

        // Its change a restart of Gitberth cut short: joining now could be undone by the recovery.
        var row = await journal.AddAsync(new ContainerChange
        {
            ContainerName = "gb-whoami", Summary = "changed WHOAMI_NAME", OldId = (string)inspect["Id"]!, OldName = "gb-whoami_old_x", TempName = "gb-whoami_new_x",
            WasRunning = true, OldRestartPolicy = """{"Name":"unless-stopped","MaximumRetryCount":0}""", Step = ChangeStep.Verifying,
        }, default);
        Assert.Contains("already changing", (await service.JoinAsync("gb-whoami", "media")).Error);
        Assert.Contains("already changing", (await service.PlanLeaveAsync("gb-whoami", "gitberth")).Refusal);
        await journal.UpdateAsync(row with { Step = ChangeStep.Finished, Outcome = ChangeOutcome.Done }, default);

        // It's under the container's lock, as the engine holds it, not the app lock a deploy holds.
        using (await locks.TryAcquireAsync("gb-whoami"))
            Assert.Equal("Gitberth is already changing whoami. Try again when that's done.", (await service.JoinAsync("gb-whoami", "media")).Error);
        using (await appLocks.AcquireAsync(whoami))
            Assert.True((await service.JoinAsync("gb-whoami", "media", "who")).Ok);
        // Kept as the app's network row, which its recreates pass again; under its slug, as every app's.
        await using (var db = _db.CreateDbContext())
            Assert.Equal(("media", "who"), db.AppNetworks.Where(n => n.AppId == whoami).Select(n => new { n.Network, n.Alias }).AsEnumerable().Select(n => (n.Network, n.Alias)).Single());
        Assert.Contains(_engine.Writes, w => w.Path.EndsWith("/networks/media/connect", StringComparison.Ordinal) && w.Body.Contains("\"whoami\"", StringComparison.Ordinal));

        // A Git app's join still waits for its deploy.
        using (await appLocks.AcquireAsync(kanban))
            Assert.Equal("Gitberth is already changing Kanban. Try again when that's done.", (await service.JoinAsync("gb-kanban", "media")).Error);
    }

    [Fact]
    public async Task Remembering_a_network_writes_it_down_without_asking_docker()
    {
        var (inspect, listed) = Made("gb-whoami", "gitberth",
            new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "whoami", [ContainerSpecFactory.SourceLabel] = "registry" }, "gitberth", "media");
        _engine.Add(inspect, listed);
        int whoami;
        await using (var db = _db.CreateDbContext())
        {
            var app = new AppDefinition { Name = "whoami", Slug = "whoami", Source = Gitberth.Apps.AppSource.Registry };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            whoami = app.Id;
        }
        var service = Service();

        await service.RememberAsync("gb-whoami", "gitberth", "whoami", null);
        await service.RememberAsync("gb-whoami", "media", "Who", null);
        await service.RememberAsync("gb-whoami", "gitberth-lan", null, "10.1.1.231");
        await service.RememberAsync("postgres", "media", "db", null);

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal([("media", "who", (string?)null), ("gitberth-lan", null, "10.1.1.231")],
                db.AppNetworks.Where(n => n.AppId == whoami).OrderBy(n => n.Id).AsEnumerable().Select(n => (n.Network, n.Alias, n.IPv4Address)));
            Assert.Equal(("postgres", "media", "db"), db.KeptNetworks.AsEnumerable().Select(k => (k.ContainerName, k.Network, k.Alias)).Single());
        }
        Assert.Empty(_engine.Writes);
    }

    [Fact]
    public async Task A_new_app_never_takes_a_name_already_on_gitberth()
    {
        var settings = new SettingsService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var options = new GitberthOptions { DataDir = Path.GetTempPath() };
        var cli = new DockerCli(options);
        var domains = new DomainService(_db, settings, new CloudflareApi(new NoHttp()), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var apps = new AppService(_db, new SecretProtector(new EphemeralDataProtectionProvider()), cli, settings, new HealthSettings(settings), domains, options,
            new RestartSchedules(settings), new WatchState(), network: Service());

        // An app called Postgres would get half of what kanban sends the shared postgres.
        Assert.Equal(new NetworkName("postgres-2", "postgres is taken on gitberth"), await apps.ChooseSlugAsync("Postgres"));
        // Gitberth's own name is never an app's.
        Assert.Equal(new NetworkName("gitberth-2", "gitberth is taken on gitberth"), await apps.ChooseSlugAsync("Gitberth"));
        Assert.Equal(new NetworkName("kanban-2", "kanban is another app's"), await apps.ChooseSlugAsync("Kanban"));
        // A container called that anywhere could join gitberth later.
        Assert.Equal("sample-index-2", (await apps.ChooseSlugAsync("Sample index")).Slug);
        Assert.Equal(new NetworkName("blog", null), await apps.ChooseSlugAsync("Blog"));
    }

    /// <summary>Nothing here may go out to the internet.</summary>
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to Cloudflare.");
    }

    private sealed class FailingEngine : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Cannot connect to the Docker daemon"));
    }
}
