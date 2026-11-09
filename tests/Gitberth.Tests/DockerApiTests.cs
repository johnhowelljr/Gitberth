using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Docker;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// The Engine API client, against a stand-in engine that answers with what the real one (Docker 29.9.2, API 1.56) said,
/// from the fixtures.
/// </summary>
public class DockerApiTests
{
    /// <summary>An engine that answers by path, remembering each call.</summary>
    private sealed class Engine(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private readonly List<string> _calls = [];

        public List<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_calls) _calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return answer(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An engine at API 1.56 (or <paramref name="version"/>'s) that answers every other path from <paramref name="byPath"/>.</summary>
    private static (DockerApi Api, Engine Engine) Connect(Func<string, HttpResponseMessage> byPath, string? version = null)
    {
        var engine = new Engine((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath == "/version"
            ? Answer(version ?? DockerFixtures.Text("version.json"))
            : byPath(r.RequestUri!.PathAndQuery)));
        return (new DockerApi("unix:///var/run/docker.sock", engine), engine);
    }

    private static HttpResponseMessage Fixtures(string path) => path switch
    {
        "/v1.56/containers/json?all=true" => Answer(DockerFixtures.Text("containers-json.json")),
        "/v1.56/networks" => Answer(DockerFixtures.Text("networks-json.json")),
        "/v1.56/containers/postgres/json" => Answer(DockerFixtures.Text("inspect-postgres.json")),
        "/v1.56/images/postgres%3A18/json" => Answer(DockerFixtures.Text("image-postgres.json")),
        "/v1.56/networks/gitberth" => Answer(DockerFixtures.Json("networks-json.json").AsArray().First(n => (string)n!["Name"]! == "gitberth")!.ToJsonString()),
        _ => Answer($$"""{"message":"page not found: {{path}}"}""", HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Negotiates_the_api_version_and_prefixes_paths()
    {
        var (api, engine) = Connect(Fixtures);
        using var _ = api;

        Assert.Equal(new Version(1, 56), await api.ApiVersionAsync());
        await api.ListContainersAsync();
        await api.ListNetworksAsync();

        // GET /version is asked once, without a version; every other path carries the one both sides speak.
        Assert.Equal(["GET /version", "GET /v1.56/containers/json?all=true", "GET /v1.56/networks"], engine.Calls);
    }

    [Theory]
    [InlineData("1.56", "1.40", "1.56")]
    [InlineData("1.47", "1.24", "1.47")]
    // An engine newer than Gitberth is spoken to in the newest version Gitberth knows.
    [InlineData("1.60", "1.40", "1.56")]
    // One that no longer accepts that version is spoken to in its oldest.
    [InlineData("1.70", "1.58", "1.58")]
    public void Speaks_the_newest_version_both_sides_know(string server, string oldest, string spoken) =>
        Assert.Equal(Version.Parse(spoken), DockerApi.Negotiate($$"""{"Version":"x","ApiVersion":"{{server}}","MinAPIVersion":"{{oldest}}"}"""));

    [Fact]
    public async Task An_older_engine_gets_its_own_version_in_every_path()
    {
        var (api, engine) = Connect(_ => Answer("[]"), version: """{"Version":"27.5.1","ApiVersion":"1.47","MinAPIVersion":"1.24"}""");
        using var _ = api;

        await api.ListContainersAsync();

        Assert.Equal("GET /v1.47/containers/json?all=true", engine.Calls[^1]);
    }

    [Fact]
    public async Task Reads_dockers_error_message()
    {
        var (api, _) = Connect(path => path.StartsWith("/v1.56/containers/", StringComparison.Ordinal)
            ? Answer("""{"message":"No such container: nope"}""", HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("driver failed programming external connectivity") });
        using var __ = api;

        var missing = await Assert.ThrowsAsync<DockerApiException>(() => api.SendAsync(HttpMethod.Get, "containers/nope/json"));
        Assert.Equal((404, "No such container: nope"), (missing.Status, missing.Message));
        Assert.True(missing.NotFound);
        // Asking for one that isn't there isn't an error to the caller.
        Assert.Null(await api.InspectContainerAsync("nope"));

        var broken = await Assert.ThrowsAsync<DockerApiException>(() => api.SendAsync(HttpMethod.Post, "networks/create"));
        Assert.Equal((500, "driver failed programming external connectivity"), (broken.Status, broken.Message));
    }

    [Fact]
    public async Task Each_call_has_its_own_timeout()
    {
        var engine = new Engine(async (r, ct) =>
        {
            if (r.RequestUri!.AbsolutePath == "/version") return Answer(DockerFixtures.Text("version.json"));
            await Task.Delay(Timeout.Infinite, ct);
            return Answer("[]");
        });
        using var api = new DockerApi("unix:///var/run/docker.sock", engine);

        var slow = await Assert.ThrowsAsync<TimeoutException>(() => api.SendAsync(HttpMethod.Get, "containers/json", timeout: TimeSpan.FromMilliseconds(50)));
        Assert.Contains("didn't answer", slow.Message);

        // Cancelled by the caller rather than timed out: that stays a cancellation.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SendAsync(HttpMethod.Get, "containers/json", timeout: TimeSpan.FromMinutes(1), ct: cancel.Token));
    }

    [Fact]
    public async Task Reads_structured_ports_and_networks_from_the_list()
    {
        var (api, _) = Connect(Fixtures);
        using var __ = api;
        var list = (await api.ListContainersAsync()).ToDictionary(c => c.Name);

        var kanban = list["gb-kanban"];
        Assert.Equal([new NetworkPort("0.0.0.0", 3000, 3001, "tcp"), new NetworkPort("::", 3000, 3001, "tcp")], kanban.Ports);
        Assert.Equal(("running", "gitberth", "kanban"), (kanban.State, kanban.NetworkMode, kanban.Labels["gitberth.app"]));
        var endpoint = Assert.Single(kanban.Networks);
        Assert.Equal(("gitberth", "172.21.0.2", "f6:33:ad:f5:b7:4f", 0), (endpoint.Network, endpoint.IpAddress, endpoint.MacAddress, endpoint.GwPriority));

        // Stopped: no ports in the list and no address, but still on its network, joined at priority -1.
        var postgres = list["postgres"];
        Assert.Equal(("exited", 0), (postgres.State, postgres.Ports.Count));
        Assert.Equal(("gitberth", -1), (postgres.Networks[0].Network, postgres.Networks[0].GwPriority));
        Assert.Null(postgres.Networks[0].IpAddress);

        // Gitberth itself: on its compose project's network and on the shared one.
        Assert.Equal(["gitberth", "gitberth_default"], list["gitberth"].Networks.Select(n => n.Network));
        Assert.Equal("sha256:0c1a38670237d7bc11ee185585ec1309b54599d259ccd7930e81c2114472b125", list["gitberth"].ImageId);

        // cloudflared runs on the host's network; its command keeps the program only, never the token after it.
        Assert.Equal(("host", "cloudflared"), (list["cloudflare-tunnel"].NetworkMode, list["cloudflare-tunnel"].Command));
        Assert.DoesNotContain(list.Values, c => c.Command.Contains("REDACTED", StringComparison.Ordinal));
        Assert.Equal(8, list.Count);
    }

    [Fact]
    public async Task Keeps_inspect_answers_as_docker_gave_them()
    {
        var (api, _) = Connect(Fixtures);
        using var __ = api;

        var postgres = await api.InspectContainerAsync("postgres");
        Assert.Equal("postgres-data:/var/lib/postgresql", (string)postgres!["HostConfig"]!["Binds"]![0]!);
        Assert.Equal("SIGINT", (string)postgres["Config"]!["StopSignal"]!);

        var image = await api.InspectImageAsync("postgres:18");
        Assert.Equal("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", (string)image!["Id"]!);
    }

    [Fact]
    public async Task Lists_the_networks_with_their_ranges_and_labels()
    {
        var (api, _) = Connect(Fixtures);
        using var __ = api;
        var networks = (await api.ListNetworksAsync()).ToDictionary(n => n.Name);

        Assert.Equal(["bridge", "docker_gwbridge", "gitberth", "gitberth_default", "host", "none"], networks.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["172.21.0.0/16"], networks["gitberth"].Subnets);
        Assert.Equal(["172.21.0.1"], networks["gitberth"].Gateways);
        Assert.Equal("bridge", networks["gitberth"].Driver);
        Assert.Equal("true", networks["gitberth"].Labels["gitberth.managed"]);
        Assert.Equal("gitberth", networks["gitberth_default"].Labels["com.docker.compose.project"]);
        Assert.Empty(networks["host"].Subnets);

        Assert.Equal("5999c46a0b4723b1944bc6037798fe423018dce0dd5e653a15b7f66f979694a9", (await api.InspectNetworkAsync("gitberth"))!.Id);
        Assert.Null(await api.InspectNetworkAsync("nope"));
    }

    [Fact]
    public void Gitberths_helpers_are_left_out_of_the_events()
    {
        const string helper = """{"Type":"container","Action":"start","Actor":{"ID":"abc","Attributes":{"gitberth.helper":"ports","image":"sha256:0c1a","name":"eager_turing"}},"timeNano":1762102800000000000}""";
        Assert.Null(DockerApi.ParseEvent(helper));
        Assert.Equal("start", DockerApi.ParseEvent(helper.Replace("gitberth.helper", "other.label", StringComparison.Ordinal))?.Action);
    }

    [Fact]
    public void Reads_a_ports_configuration_from_inspect()
    {
        // Created with -p 0.0.0.0:9100:80, stopped: the binding it takes again when it starts.
        var sample = NetworkPortConfig.From(DockerFixtures.Inspect("sample-index"));
        Assert.Equal([new PortBinding("0.0.0.0", 9100, 80, "tcp")], sample.Bindings);
        Assert.Equal([new ExposedPort(80, "tcp")], sample.Exposed);
        Assert.False(sample.PublishAll);

        // HostIp "" is every address.
        Assert.Equal([new PortBinding(null, 8080, 8080, "tcp")], NetworkPortConfig.From(DockerFixtures.Inspect("gitberth")).Bindings);
        Assert.Empty(NetworkPortConfig.From(DockerFixtures.Inspect("postgres")).Bindings);
    }

    /// <summary>An engine that answers /version with <paramref name="version"/> and everything else from <paramref name="answer"/>, by method and path.</summary>
    private static (DockerEngine Engine, Engine Handler) Changing(Func<HttpMethod, string, HttpResponseMessage> answer, string? version = null)
    {
        var handler = new Engine((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath == "/version"
            ? Answer(version ?? DockerFixtures.Text("version.json"))
            : answer(r.Method, r.RequestUri!.PathAndQuery)));
        return (new DockerEngine(new DockerApi("unix:///var/run/docker.sock", handler)), handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    [Fact]
    public async Task Refuses_changes_below_api_1_44()
    {
        // Docker 24 speaks API 1.43: it inspects, but a copy of a container made through it would leave settings out.
        var (engine, handler) = Changing((_, _) => Answer("[]"), """{"Version":"24.0.9","ApiVersion":"1.43","MinAPIVersion":"1.12"}""");

        var refused = await Assert.ThrowsAsync<EngineException>(() => engine.CreateAsync("postgres_new_0ebf31bbcd07", new JsonObject { ["Image"] = "postgres:18" }, default));
        Assert.Equal("Changing containers needs Docker 25 or later (API 1.44). This host has Docker 24.0.9 (API 1.43).", refused.Message);
        await Assert.ThrowsAsync<EngineException>(() => engine.UpdateRestartPolicyAsync("postgres", new JsonObject { ["Name"] = "no" }, default));
        await Assert.ThrowsAsync<EngineException>(() => engine.StopAsync("postgres", 60, default));
        await Assert.ThrowsAsync<EngineException>(() => engine.RenameAsync("postgres", "postgres_old_0ebf31bbcd07", default));
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync("postgres", default));
        await Assert.ThrowsAsync<EngineException>(() => engine.RemoveAsync("postgres", force: false, default));

        // Reading goes on, in the version it speaks; nothing that changes anything was sent.
        Assert.Equal(new EngineVersion("24.0.9", "1.43"), await engine.VersionAsync(default));
        Assert.Empty(await engine.ListContainersAsync(default));
        Assert.All(handler.Calls, c => Assert.StartsWith("GET ", c));
        Assert.Contains("GET /v1.43/containers/json?all=true", handler.Calls);

        // Docker 25 changes them.
        var (current, sent) = Changing((_, _) => Answer("""{"Id":"f00d","Warnings":[]}""", HttpStatusCode.Created),
            """{"Version":"25.0.5","ApiVersion":"1.44","MinAPIVersion":"1.24"}""");
        Assert.Equal("f00d", await current.CreateAsync("postgres_new_0ebf31bbcd07", new JsonObject { ["Image"] = "postgres:18" }, default));
        Assert.Equal("POST /v1.44/containers/create?name=postgres_new_0ebf31bbcd07", sent.Calls[^1]);
    }

    [Fact]
    public async Task Updates_a_containers_limits_live_with_only_what_it_was_given()
    {
        // docker update: the body is sent as it is, and Docker's warnings come back.
        string? sent = null;
        var handler = new Engine(async (r, ct) =>
        {
            if (r.RequestUri!.AbsolutePath == "/version") return Answer(DockerFixtures.Text("version.json"));
            sent = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return Answer("""{"Warnings":["Your kernel does not support swap limit capabilities"]}""");
        });
        var engine = new DockerEngine(new DockerApi("unix:///var/run/docker.sock", handler));

        var answer = await engine.UpdateAsync("postgres", new JsonObject { ["Memory"] = 536870912L, ["MemorySwap"] = 1073741824L, ["PidsLimit"] = -1 }, default);

        Assert.Equal("POST /v1.56/containers/postgres/update", handler.Calls[^1]);
        Assert.Equal("""{"Memory":536870912,"MemorySwap":1073741824,"PidsLimit":-1}""", sent);
        Assert.Equal("""{"Warnings":["Your kernel does not support swap limit capabilities"]}""", answer.ToJsonString());

        // An answer without warnings is an empty list; and Docker 24 changes nothing.
        var (quiet, _) = Changing((_, _) => Answer("{}"));
        Assert.Equal("""{"Warnings":[]}""", (await quiet.UpdateAsync("postgres", new JsonObject { ["NanoCpus"] = 1500000000L }, default)).ToJsonString());
        var (old, oldCalls) = Changing((_, _) => Answer("{}"), """{"Version":"24.0.9","ApiVersion":"1.43","MinAPIVersion":"1.12"}""");
        await Assert.ThrowsAsync<EngineException>(() => old.UpdateAsync("postgres", new JsonObject { ["Memory"] = 536870912L }, default));
        Assert.All(oldCalls.Calls, c => Assert.StartsWith("GET ", c));
    }

    [Fact]
    public async Task Demultiplexes_log_frames()
    {
        // Without a TTY Docker frames each write: its stream (1 stdout, 2 stderr), three bytes of padding, a big-endian
        // length, then the bytes. With one it sends the bytes as the program wrote them.
        static byte[] Frame(byte stream, string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var frame = new byte[8 + payload.Length];
            frame[0] = stream;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
            payload.CopyTo(frame, 8);
            return frame;
        }
        byte[] framed = [.. Frame(1, "LOG:  database system is ready to accept connections\n"), .. Frame(2, "FATAL:  role \"kanban\" does not exist\n")];
        var tty = Encoding.UTF8.GetBytes("\u001b[32mready\u001b[0m\r\nlistening on :3000\r\n");
        var (engine, handler) = Changing((_, path) => path.StartsWith("/v1.56/containers/postgres/logs", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(framed) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tty) });

        // The bytes come back as Docker sent them; only the reader, which knows whether there's a TTY, takes frames apart.
        var raw = await engine.LogsAsync("postgres", 60, default);
        Assert.Equal(framed, raw);
        Assert.Equal("LOG:  database system is ready to accept connections\nFATAL:  role \"kanban\" does not exist\n", LogStream.Text(raw, tty: false));
        Assert.Equal("GET /v1.56/containers/postgres/logs?stdout=1&stderr=1&tail=60", handler.Calls[^1]);

        var plain = await engine.LogsAsync("web", 60, default);
        Assert.Equal(tty, plain);
        Assert.Equal("\u001b[32mready\u001b[0m\r\nlistening on :3000\r\n", LogStream.Text(plain, tty: true));
        // Bytes that aren't frames are never garbled into some: they read as they are.
        Assert.Equal(LogStream.Text(plain, tty: true), LogStream.Text(plain, tty: false));
    }

    [Fact]
    public async Task Never_sends_v_when_removing()
    {
        var (engine, handler) = Changing((_, _) => Status(HttpStatusCode.NoContent));

        // The original, once the new one passed; a new one that failed, killed first. Their volumes stay either way.
        await engine.RemoveAsync("0ebf31bbcd07", force: false, default);
        await engine.RemoveAsync("postgres_new_0ebf31bbcd07", force: true, default);

        var removes = handler.Calls.Where(c => c.StartsWith("DELETE ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["DELETE /v1.56/containers/0ebf31bbcd07?force=false", "DELETE /v1.56/containers/postgres_new_0ebf31bbcd07?force=true"], removes);
        Assert.All(removes, r => Assert.DoesNotContain("v", new Uri("http://docker" + r["DELETE ".Length..]).Query.TrimStart('?').Split('&').Select(p => p.Split('=')[0])));
    }

    [Fact]
    public async Task A_start_or_stop_docker_has_done_already_is_done()
    {
        // 304: it runs already, or isn't running.
        var (engine, handler) = Changing((_, _) => Status(HttpStatusCode.NotModified));

        await engine.StartAsync("postgres", default);
        await engine.StopAsync("postgres", 60, default);

        Assert.Equal(["POST /v1.56/containers/postgres/start", "POST /v1.56/containers/postgres/stop?t=60"], handler.Calls.Where(c => c.StartsWith("POST ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Waits_for_a_stop_as_long_as_it_takes_and_15_s_more()
    {
        Assert.Equal(TimeSpan.FromSeconds(75), DockerApi.StopWait(60));
        Assert.Equal(TimeSpan.FromSeconds(15), DockerApi.StopWait(0));
        // Never killed (--stop-timeout -1), or a wait longer still: ten minutes, and Docker goes on stopping it after.
        Assert.Equal(TimeSpan.FromMinutes(10), DockerApi.StopWait(-1));
        Assert.Equal(TimeSpan.FromMinutes(10), DockerApi.StopWait(3600));

        var (engine, handler) = Changing((_, _) => Status(HttpStatusCode.NoContent));
        await engine.StopAsync("postgres", -5, default);
        Assert.Equal("POST /v1.56/containers/postgres/stop?t=-1", handler.Calls[^1]);
    }

    [Fact]
    public async Task Only_a_404_is_a_container_that_isnt_there()
    {
        var (engine, _) = Changing((_, path) => path switch
        {
            "/v1.56/containers/gone/json" => Answer("""{"message":"No such container: gone"}""", HttpStatusCode.NotFound),
            "/v1.56/containers/postgres_old_0ebf31bbcd07/rename?name=postgres" =>
                Answer("""{"message":"Conflict. The container name \"/postgres\" is already in use by container \"f00d\"."}""", HttpStatusCode.Conflict),
            _ => Answer("""{"message":"context deadline exceeded"}""", HttpStatusCode.InternalServerError),
        });

        Assert.Null(await engine.InspectContainerAsync("gone", default));
        // A container Docker didn't answer for isn't gone.
        var busy = await Assert.ThrowsAsync<EngineException>(() => engine.InspectContainerAsync("postgres", default));
        Assert.Equal((500, "context deadline exceeded"), (busy.Status, busy.Message));
        var taken = await Assert.ThrowsAsync<EngineException>(() => engine.RenameAsync("postgres_old_0ebf31bbcd07", "postgres", default));
        Assert.True(taken.Conflict);
        Assert.StartsWith("Conflict. The container name", taken.Message);

        // No answer at all: status 0.
        var broken = new DockerEngine(new DockerApi("unix:///var/run/docker.sock",
            new Engine((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Cannot connect to the Docker daemon")))));
        var unanswered = await Assert.ThrowsAsync<EngineException>(() => broken.InspectContainerAsync("postgres", default));
        Assert.Equal((0, "Cannot connect to the Docker daemon"), (unanswered.Status, unanswered.Message));
    }

    [Fact]
    public async Task Creates_from_the_body_and_answers_with_the_new_containers_id()
    {
        string? body = null;
        var handler = new Engine(async (r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/version") return Answer(DockerFixtures.Text("version.json"));
            body = await r.Content!.ReadAsStringAsync();
            return Answer("""{"Id":"9c1b5e0f2a7d","Warnings":[]}""", HttpStatusCode.Created);
        });
        var engine = new DockerEngine(new DockerApi("unix:///var/run/docker.sock", handler));

        var id = await engine.CreateAsync("postgres_new_0ebf31bbcd07", new JsonObject { ["Image"] = "postgres:18", ["HostConfig"] = new JsonObject { ["RestartPolicy"] = new JsonObject { ["Name"] = "no" } } }, default);

        Assert.Equal("9c1b5e0f2a7d", id);
        Assert.Equal("""{"Image":"postgres:18","HostConfig":{"RestartPolicy":{"Name":"no"}}}""", body);
        Assert.Equal("POST /v1.56/containers/create?name=postgres_new_0ebf31bbcd07", handler.Calls[^1]);
        // The restart policy alone, when it's turned off before the stop and given back after the checks.
        await engine.UpdateRestartPolicyAsync("9c1b5e0f2a7d", new JsonObject { ["Name"] = "unless-stopped", ["MaximumRetryCount"] = 0 }, default);
        Assert.Equal("""{"RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0}}""", body);
    }
}
