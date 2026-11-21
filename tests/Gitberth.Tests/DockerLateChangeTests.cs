using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Tests;

/// <summary>
/// A busy engine can take far longer than a change's timeout to answer, and still do what it was asked. A change whose
/// answer is late is checked on: made, started, renamed, updated, removed or joined all the same counts as done, and only
/// one that still hasn't happened once the settle wait is over is a timeout. Docker's own refusals are never waited on.
/// </summary>
public sealed class DockerLateChangeTests
{
    private const string Id = "4f3c2b1a0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b";

    /// <summary>A stand-in engine whose changes happen at once but whose answers to them never come (until cancelled).</summary>
    private sealed class SlowEngine : HttpMessageHandler
    {
        private int _changes;
        private int _checkedAfter = -1;

        public JsonObject? Container { get; set; }
        public List<string> Calls { get; } = [];
        public bool AnswerChanges { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? Refuse { get; set; }

        /// <summary>How many changes were asked for so far.</summary>
        public int Changes => Volatile.Read(ref _changes);

        /// <summary>Held while the container is read for an answer, and while a test changes it, so a read never meets half a change.</summary>
        public object Gate { get; } = new();

        /// <summary>
        /// Whether the API has started checking on a change asked for after the first <paramref name="changes"/>: it gave up
        /// waiting for that change's answer and reads the container (or network) to see whether Docker did it.
        /// </summary>
        public bool CheckingOn(int changes) => Volatile.Read(ref _checkedAfter) > changes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri.PathAndQuery}");
            if (path == "/version") return Answer(DockerFixtures.Text("version.json"));
            if (request.Method == HttpMethod.Get)
            {
                Volatile.Write(ref _checkedAfter, Changes);
                lock (Gate)
                {
                    if (path.StartsWith("/v1.56/networks/", StringComparison.Ordinal))
                        return Container?["NetworkSettings"]?["Networks"]?["web"] is not null && path.EndsWith("/web", StringComparison.Ordinal)
                            ? Answer("""{"Id":"6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e","Name":"web"}""")
                            : Answer("""{"message":"network not found"}""", HttpStatusCode.NotFound);
                    return Container is null ? Answer("""{"message":"No such container"}""", HttpStatusCode.NotFound) : Answer(Container.ToJsonString());
                }
            }
            Interlocked.Increment(ref _changes);
            if (Refuse?.Invoke(request) is { } refused) return refused;
            // The change happens now; its answer is late.
            if (AnswerChanges) return Answer("{}");
            await Task.Delay(Timeout.Infinite, ct);
            return Answer("{}");
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Docker does it (what `done` changes) only once the API has given up on its answer and is checking on it: the change
    // counts all the same, however long the machine took to get there.
    private static async Task DoneWhileCheckedOnAsync(SlowEngine engine, int changesBefore, Action done)
    {
        await Eventually.UntilAsync(() => engine.CheckingOn(changesBefore), "the API to check on the change whose answer is late");
        lock (engine.Gate) done();
    }

    // The settle wait of the tests where Docker does it in the end: long, since a busy machine may take a while to get
    // there, and a passing test never waits it out. The tests where Docker never does it keep a short one.
    private static DockerApi Api(SlowEngine engine, double settleSeconds = 30) => new("unix:///var/run/docker.sock", engine)
    {
        ChangeTimeout = TimeSpan.FromMilliseconds(150),
        SettleWait = TimeSpan.FromSeconds(settleSeconds),
        SettlePoll = TimeSpan.FromMilliseconds(20),
        StopGrace = TimeSpan.FromMilliseconds(100),
    };

    private static string Now(double secondsAgo = 0) =>
        DateTime.UtcNow.AddSeconds(-secondsAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Container(string created, bool running = false, string startedAt = "0001-01-01T00:00:00Z") => new()
    {
        ["Id"] = Id,
        ["Name"] = "/web-next",
        ["Created"] = created,
        ["State"] = new JsonObject { ["Running"] = running, ["StartedAt"] = startedAt },
        ["HostConfig"] = new JsonObject { ["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 }, ["Memory"] = 0L },
        ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject() },
    };

    [Fact]
    public async Task A_create_whose_answer_is_late_counts_once_docker_has_made_the_container()
    {
        var engine = new SlowEngine();
        using var api = Api(engine);
        var create = api.CreateContainerAsync("web-next", new JsonObject { ["Image"] = "nginx:1.27" });
        // The engine made it while its answer was still on the way.
        await DoneWhileCheckedOnAsync(engine, 0, () => engine.Container = Container(Now()));
        Assert.Equal(Id, await create);
        Assert.Contains(engine.Calls, c => c == "GET /v1.56/containers/web-next/json");
    }

    [Fact]
    public async Task A_create_docker_never_made_is_a_timeout_that_says_so()
    {
        var engine = new SlowEngine();
        using var api = Api(engine, settleSeconds: 0.5);
        var late = await Assert.ThrowsAsync<TimeoutException>(() => api.CreateContainerAsync("web-next", new JsonObject { ["Image"] = "nginx:1.27" }));
        Assert.StartsWith("Docker didn't answer within ", late.Message, StringComparison.Ordinal);
        Assert.EndsWith(", and it still hadn't happened 0.5 seconds later.", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_container_of_that_name_made_long_before_isnt_taken_for_the_late_create()
    {
        var engine = new SlowEngine { Container = Container(Now(secondsAgo: 600)) };
        using var api = Api(engine, settleSeconds: 0.4);
        await Assert.ThrowsAsync<TimeoutException>(() => api.CreateContainerAsync("web-next", new JsonObject { ["Image"] = "nginx:1.27" }));
    }

    [Fact]
    public async Task A_late_start_counts_once_the_container_runs_or_has_started_since()
    {
        var engine = new SlowEngine { Container = Container(Now(60)) };
        using var api = Api(engine);
        var start = api.StartContainerAsync(Id);
        await DoneWhileCheckedOnAsync(engine, 0, () => engine.Container = Container(Now(60), running: true, startedAt: Now()));
        await start;

        // Started and already stopped again (it crashed at once): it did start.
        engine.Container = Container(Now(60), running: false, startedAt: Now(120));
        var changes = engine.Changes;
        var again = api.StartContainerAsync(Id);
        await DoneWhileCheckedOnAsync(engine, changes, () => engine.Container = Container(Now(60), running: false, startedAt: Now()));
        await again;
    }

    [Fact]
    public async Task A_late_start_of_a_container_that_never_started_is_a_timeout()
    {
        var engine = new SlowEngine { Container = Container(Now(60), running: false, startedAt: Now(300)) };
        using var api = Api(engine, settleSeconds: 0.4);
        await Assert.ThrowsAsync<TimeoutException>(() => api.StartContainerAsync(Id));
    }

    [Fact]
    public async Task Late_stops_renames_removals_updates_and_joins_count_once_docker_did_them()
    {
        var engine = new SlowEngine { Container = Container(Now(60), running: false) };
        using var api = Api(engine);

        // Stopped: it isn't running.
        await api.StopContainerAsync(Id, 0);

        // Renamed: the new name is this container.
        await api.RenameContainerAsync(Id, "web-next");

        // Updated: its HostConfig says what was asked (Docker reads 0 as unchanged).
        var policy = new JsonObject { ["Name"] = "always", ["MaximumRetryCount"] = 0 };
        var changes = engine.Changes;
        var update = api.UpdateRestartPolicyAsync(Id, policy);
        await DoneWhileCheckedOnAsync(engine, changes, () => engine.Container!["HostConfig"]!["RestartPolicy"] = policy.DeepClone());
        await update;
        var resources = await api.UpdateContainerAsync(Id, new JsonObject { ["RestartPolicy"] = policy.DeepClone(), ["Memory"] = 0L });
        Assert.Empty((JsonArray)resources["Warnings"]!);

        // Joined: the container is on the network.
        changes = engine.Changes;
        var join = api.ConnectNetworkAsync("web", Id, new JsonObject());
        await DoneWhileCheckedOnAsync(engine, changes,
            () => engine.Container!["NetworkSettings"]!["Networks"]!["web"] = new JsonObject { ["NetworkID"] = "6e6e6e6e6e6e6e6e6e6e" });
        await join;

        // Left: it isn't on it any more.
        changes = engine.Changes;
        var leave = api.DisconnectNetworkAsync("web", Id);
        await DoneWhileCheckedOnAsync(engine, changes, () => ((JsonObject)engine.Container!["NetworkSettings"]!["Networks"]!).Remove("web"));
        await leave;

        // Removed: it's gone.
        changes = engine.Changes;
        var remove = api.RemoveContainerAsync(Id, force: true);
        await DoneWhileCheckedOnAsync(engine, changes, () => engine.Container = null);
        await remove;
    }

    [Fact]
    public async Task An_update_docker_hasnt_made_is_a_timeout()
    {
        var engine = new SlowEngine { Container = Container(Now(60)) };
        using var api = Api(engine, settleSeconds: 0.4);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            api.UpdateRestartPolicyAsync(Id, new JsonObject { ["Name"] = "always", ["MaximumRetryCount"] = 0 }));
    }

    [Fact]
    public async Task Dockers_own_refusal_is_never_waited_on()
    {
        var engine = new SlowEngine
        {
            Refuse = r => Answer("""{"message":"Conflict. The container name \"/web-next\" is already in use"}""", HttpStatusCode.Conflict),
        };
        using var api = Api(engine, settleSeconds: 30);
        var started = DateTime.UtcNow;
        var refused = await Assert.ThrowsAsync<DockerApiException>(() => api.CreateContainerAsync("web-next", new JsonObject { ["Image"] = "nginx:1.27" }));
        Assert.Equal(409, refused.Status);
        // Never checked on, and never waited the 30 s out: what proves it isn't how quick a busy machine was.
        Assert.DoesNotContain(engine.Calls, c => c.StartsWith("GET /v1.56/containers/", StringComparison.Ordinal));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_change_answered_in_time_is_never_checked_on()
    {
        var engine = new SlowEngine { AnswerChanges = true, Container = Container(Now(60)) };
        using var api = Api(engine);
        await api.StartContainerAsync(Id);
        await api.RemoveContainerAsync(Id, force: false);
        Assert.DoesNotContain(engine.Calls, c => c.StartsWith("GET /v1.56/containers/", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_and_changes_wait_longer_than_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DockerApi.DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), DockerApi.DefaultChangeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), DockerApi.DefaultSettleWait);
    }
}
