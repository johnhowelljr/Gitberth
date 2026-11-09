using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// A clock for PortChanger's waits: each wait moves it on at once by as long as was asked, so checks that take 15 s run in
/// no time and every duration it measures is exact. The fake engine moves it on too, for the time a stop or start takes.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Wait(this, callback, state, dueTime);

    private sealed class Wait : ITimer
    {
        private int _gone;

        public Wait(FakeClock clock, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) return;
            clock.Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _gone) == 0) callback(state);
            });
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Interlocked.Exchange(ref _gone, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The Docker engine kept in memory, behaving like Docker 29 where PortChanger can tell: names are unique (409), host ports
/// and static addresses are taken at start and given back at stop, a stopped container holds none, anonymous volumes are
/// made at create for every VOLUME path nothing else mounts, and a removed container's volumes stay. A container that exits
/// on its own is restarted by its restart policy, as Docker's monitor does it (checked on Docker 29.9.2): it counts as
/// running and restarting while Docker waits, 100 ms after the first exit and twice as long each time, its exit code and
/// OOM kill readable meanwhile, then it runs again with both cleared and its restart count one higher. Any call can be made
/// to fail before or after it runs, held until the test lets it go, or hung for good, as if Gitberth stopped right there.
/// </summary>
internal sealed class FakeEngine(FakeClock clock) : IContainerEngine
{
    private readonly object _gate = new();
    private int _ids;
    private int _hangSkips;

    /// <summary>A container.</summary>
    public sealed class Box
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required JsonObject Config { get; init; }
        public required JsonObject HostConfig { get; init; }
        /// <summary>Its networks, as configured.</summary>
        public required JsonObject Endpoints { get; init; }
        public required JsonArray Mounts { get; init; }
        /// <summary>The ID of the image it runs.</summary>
        public required string Image { get; init; }
        public DateTimeOffset Created { get; init; }
        public bool Running { get; set; }
        /// <summary>Docker waits to restart it, by its policy, until <see cref="RestartAt"/>; it counts as running meanwhile.</summary>
        public bool Restarting { get; set; }
        public DateTimeOffset RestartAt { get; set; }
        /// <summary>The restart policy's wait after the last exit: 100 ms, then twice as long each time.</summary>
        public TimeSpan Backoff { get; set; }
        /// <summary>A stop Docker is still carrying out after its caller has gone: it's down at this time.</summary>
        public DateTimeOffset? StoppingUntil { get; set; }
        public int ExitCode { get; set; }
        public int RestartCount { get; set; }
        public bool OomKilled { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        /// <summary>What it publishes while it runs: Docker's NetworkSettings.Ports.</summary>
        public Dictionary<string, List<(string Ip, int Port)>> Live { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Addresses { get; } = new(StringComparer.Ordinal);
        /// <summary>The container ports its program answers on, once it has been up <see cref="ListenAfter"/>.</summary>
        public HashSet<int> Listens { get; set; } = [];
        public TimeSpan ListenAfter { get; set; }
        /// <summary>It exits this long after each start, with <see cref="ExitWith"/>.</summary>
        public TimeSpan? ExitsAfter { get; set; }
        public int ExitWith { get; set; } = 1;
        public bool OutOfMemory { get; set; }
        /// <summary>When its health check says healthy after a start.</summary>
        public TimeSpan HealthyAfter { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Its health check's output when it fails instead.</summary>
        public string? Unhealthy { get; set; }
        public string Output { get; set; } = "";
        public JsonArray Changes { get; set; } = [];

        public bool Tty => Config["Tty"]?.GetValueKind() == JsonValueKind.True;
    }

    public List<Box> Boxes { get; } = [];
    public HashSet<string> Volumes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonObject> Images { get; } = new(StringComparer.Ordinal);
    public JsonArray Networks { get; set; } = DockerFixtures.Json("networks-json.json").AsArray();
    /// <summary>Host ports programs outside Docker listen on.</summary>
    public HashSet<int> HostPrograms { get; } = [];
    public EngineVersion Version { get; set; } = new("29.9.2", "1.56");
    /// <summary>Every call that changes something, in order: "stop postgres t=60".</summary>
    public List<string> Calls { get; } = [];
    public TimeSpan StopTakes { get; set; } = TimeSpan.FromSeconds(1.4);
    public TimeSpan StartTakes { get; set; } = TimeSpan.FromSeconds(0.45);
    /// <summary>
    /// When set, a stop only begins: the container goes on running this long on the clock before it's down. With a Hang
    /// after the stop, that's Docker finishing a stop whose caller has gone.
    /// </summary>
    public TimeSpan? StopLingers { get; set; }
    /// <summary>Called with each container it creates, to give it a behavior.</summary>
    public Action<Box>? OnCreate { get; set; }
    /// <summary>Docker's error for a call (by its name and its container's name, or the new name, or the ID or name inspected), before it runs.</summary>
    public Func<string, string, EngineException?>? Fail { get; set; }
    /// <summary>Docker's error for a call after it ran anyway: a lost answer.</summary>
    public Func<string, string, EngineException?>? FailAfter { get; set; }
    /// <summary>A call that never returns, before or after it runs: Gitberth stopped right there.</summary>
    public (string Call, string Target, bool After)? Hang { get; set; }
    /// <summary>How many calls matching <see cref="Hang"/> go through before the one that hangs.</summary>
    public int HangSkips
    {
        get => Volatile.Read(ref _hangSkips);
        set => Volatile.Write(ref _hangSkips, value);
    }
    /// <summary>Calls held until the test completes their gate: "start postgres".</summary>
    public Dictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.Ordinal);
    /// <summary>Done once a call has hung.</summary>
    public TaskCompletionSource Hung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Done once a call waits at its gate.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A container as a real inspect describes it.</summary>
    public Box Add(JsonObject inspect)
    {
        var state = inspect["State"];
        var endpoints = new JsonObject();
        foreach (var (net, node) in inspect["NetworkSettings"]?["Networks"] as JsonObject ?? [])
        {
            var e = (JsonObject)(node?.DeepClone() ?? new JsonObject());
            foreach (var live in new[] { "NetworkID", "EndpointID", "Gateway", "IPAddress", "IPPrefixLen", "IPv6Gateway", "GlobalIPv6Address", "GlobalIPv6PrefixLen", "DNSNames", "MacAddress" })
                e.Remove(live);
            endpoints[net] = e;
        }
        var box = new Box
        {
            Id = Str(inspect, "Id"),
            Name = Str(inspect, "Name").TrimStart('/'),
            Config = (JsonObject)inspect["Config"]!.DeepClone(),
            HostConfig = (JsonObject)inspect["HostConfig"]!.DeepClone(),
            Endpoints = endpoints,
            Mounts = (JsonArray)(inspect["Mounts"]?.DeepClone() ?? new JsonArray()),
            Image = Str(inspect, "Image"),
            Created = Time(Str(inspect, "Created")) ?? clock.GetUtcNow() - TimeSpan.FromDays(3),
            ExitCode = state?["ExitCode"]?.GetValue<int>() ?? 0,
            StartedAt = Time(Str(state, "StartedAt")),
            FinishedAt = Time(Str(state, "FinishedAt")),
        };
        foreach (var m in box.Mounts.OfType<JsonObject>().Where(m => Str(m, "Type") == "volume")) Volumes.Add(Str(m, "Name"));
        lock (_gate)
        {
            Boxes.Add(box);
            if (state?["Running"]?.GetValueKind() == JsonValueKind.True)
            {
                Bind(box);
                box.Running = true;
            }
        }
        return box;
    }

    public Box? Find(string idOrName)
    {
        lock (_gate)
            return Boxes.FirstOrDefault(b => b.Name == idOrName) ?? Boxes.FirstOrDefault(b => b.Id == idOrName)
                ?? (Boxes.Where(b => b.Id.StartsWith(idOrName, StringComparison.Ordinal)).ToList() is [var only] ? only : null);
    }

    public Box Get(string idOrName) => Find(idOrName) ?? throw new EngineException(404, $"No such container: {idOrName}");

    public Task<EngineVersion> VersionAsync(CancellationToken ct) => Task.FromResult(Version);

    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) =>
        RunAsync("inspect", idOrName, () => Find(idOrName) is { } b ? InspectOf(b) : null);

    public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) =>
        Task.FromResult(ImageOf(nameOrId)?.DeepClone() as JsonObject);

    public Task<JsonArray> ListContainersAsync(CancellationToken ct) => RunAsync("list", "", () =>
    {
        var list = new JsonArray();
        foreach (var b in Boxes)
        {
            Settle(b);
            list.Add(new JsonObject
            {
                ["Id"] = b.Id,
                ["Names"] = new JsonArray("/" + b.Name),
                ["Image"] = Str(b.Config, "Image"),
                ["ImageID"] = b.Image,
                ["State"] = StatusOf(b),
                ["Labels"] = b.Config["Labels"]?.DeepClone(),
                ["HostConfig"] = new JsonObject { ["NetworkMode"] = Str(b.HostConfig, "NetworkMode") },
            });
        }
        return list;
    });

    public Task<JsonArray> ListNetworksAsync(CancellationToken ct) => Task.FromResult((JsonArray)Networks.DeepClone());

    public Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct) => RunAsync("create", name, () =>
    {
        if (Boxes.FirstOrDefault(b => b.Name == name) is { } existing) throw Conflict(name, existing.Id);
        if (body.ContainsKey("MacAddress")) throw new EngineException(400, "invalid MacAddress: it's set per network endpoint since API 1.44");
        var config = (JsonObject)body.DeepClone();
        config.Remove("HostConfig");
        config.Remove("NetworkingConfig");
        var host = (JsonObject)(body["HostConfig"]?.DeepClone() ?? new JsonObject());
        var endpoints = (JsonObject)(body["NetworkingConfig"]?["EndpointsConfig"]?.DeepClone() ?? new JsonObject());
        var image = ImageOf(Str(config, "Image")) ?? throw new EngineException(404, $"No such image: {Str(config, "Image")}");
        var mode = Str(host, "NetworkMode");
        if (IsNetworkName(mode) && NetworkId(mode) is null) throw new EngineException(404, $"network {mode} not found");
        foreach (var (net, _) in endpoints)
            if (NetworkId(net) is null) throw new EngineException(404, $"network {net} not found");
        if (IsNetworkName(mode) && !endpoints.ContainsKey(mode)) endpoints[mode] = new JsonObject();
        if (mode is "" or "default" && endpoints.Count == 0) endpoints["bridge"] = new JsonObject();
        var id = NewId();
        if (Str(config, "Hostname").Length == 0) config["Hostname"] = id[..12];
        var box = new Box
        {
            Id = id, Name = name, Config = config, HostConfig = host, Endpoints = endpoints, Mounts = MountsFor(host, config), Image = Str(image, "Id"),
            Created = clock.GetUtcNow(),
        };
        OnCreate?.Invoke(box);
        Boxes.Add(box);
        Calls.Add($"create {name}");
        return id;
    });

    public Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct) => RunAsync("update", NameOf(id), () =>
    {
        var b = Get(id);
        b.HostConfig["RestartPolicy"] = policy.DeepClone();
        Calls.Add($"update {b.Name} restart={Str(policy, "Name")}");
        return true;
    });

    /// <summary>
    /// docker update: only the keys given change, live. Docker reads a 0 (and an empty cpuset) as "unchanged", refuses a
    /// memory limit above the swap limit it would be left with, and refuses NanoCpus next to a CPU quota.
    /// </summary>
    public Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct) => RunAsync("update", NameOf(id), () =>
    {
        var b = Get(id);
        long Number(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        var memory = Number(resources, "Memory") is > 0 and var m ? m : Number(b.HostConfig, "Memory");
        var swap = resources.ContainsKey("MemorySwap") && Number(resources, "MemorySwap") != 0 ? Number(resources, "MemorySwap") : Number(b.HostConfig, "MemorySwap");
        if (resources.ContainsKey("Memory") && swap > 0 && memory > swap)
            throw new EngineException(409, "Cannot update container: Memory limit should be smaller than already set memoryswap limit, update the memoryswap at the same time");
        if (Number(resources, "NanoCpus") > 0 && Number(b.HostConfig, "CpuQuota") > 0)
            throw new EngineException(409, "Cannot update container: Conflicting options: CPU Quota cannot be updated as NanoCPUs has already been set");
        foreach (var (key, value) in resources)
        {
            if (value is JsonValue v && ((v.GetValueKind() == JsonValueKind.Number && v.GetValue<long>() == 0 && key != "PidsLimit")
                                         || (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length == 0))) continue;
            b.HostConfig[key] = key == "PidsLimit" && Number(resources, key) <= 0 ? null : value?.DeepClone();
        }
        Calls.Add($"update {b.Name} {string.Join(' ', resources.Select(kv => kv.Key + "=" + kv.Value?.ToJsonString()))}");
        return new JsonObject { ["Warnings"] = new JsonArray() };
    });

    /// <summary>What a running container uses now, by name, for the plan's memory line.</summary>
    public Dictionary<string, ulong> MemoryInUse { get; } = new(StringComparer.Ordinal);

    public EngineHost? Host { get; set; } = new(8, 16L * 1024 * 1024 * 1024);

    public Task<ulong?> MemoryUsageAsync(string id, CancellationToken ct) =>
        Task.FromResult(Find(id) is { Running: true } b && MemoryInUse.TryGetValue(b.Name, out var used) ? used : (ulong?)null);

    public Task<EngineHost?> HostAsync(CancellationToken ct) => Task.FromResult(Host);

    public Task StopAsync(string id, int timeoutSeconds, CancellationToken ct) => RunAsync("stop", NameOf(id), () =>
    {
        var b = Get(id);
        Calls.Add($"stop {b.Name} t={timeoutSeconds.ToString(CultureInfo.InvariantCulture)}");
        Settle(b);
        if (!b.Running) return true;
        // A stop during the policy's wait just calls the restart off.
        if (b.Restarting)
        {
            b.Restarting = false;
            b.Running = false;
            return true;
        }
        if (StopLingers is { } linger)
        {
            b.StoppingUntil ??= clock.GetUtcNow() + linger;
            return true;
        }
        clock.Advance(StopTakes);
        Down(b, clock.GetUtcNow(), 0);
        return true;
    });

    public Task RenameAsync(string id, string name, CancellationToken ct) => RunAsync("rename", name, () =>
    {
        var b = Get(id);
        if (Boxes.FirstOrDefault(o => o.Name == name) is { } other && other != b) throw Conflict(name, other.Id);
        Calls.Add($"rename {b.Name} {name}");
        b.Name = name;
        return true;
    });

    public Task StartAsync(string id, CancellationToken ct) => RunAsync("start", NameOf(id), () =>
    {
        var b = Get(id);
        Calls.Add($"start {b.Name}");
        Settle(b);
        // Running, or still on its way down from a stop: Docker's 304.
        if (b.Running) return true;
        var mode = Str(b.HostConfig, "NetworkMode");
        if (IsNetworkName(mode) && NetworkId(mode) is null)
            throw new EngineException(500, $"failed to set up container networking: could not find a network matching network mode {mode}: network {mode} not found");
        Bind(b);
        b.Running = true;
        b.OomKilled = false;
        b.StartedAt = clock.GetUtcNow();
        b.FinishedAt = null;
        b.ExitCode = 0;
        // A start through the API begins the restart policy's count afresh.
        b.RestartCount = 0;
        b.Backoff = TimeSpan.Zero;
        clock.Advance(StartTakes);
        return true;
    });

    public async Task<int> WaitAsync(string id, CancellationToken ct)
    {
        while (true)
        {
            var b = Get(id);
            lock (_gate) Settle(b);
            if (!b.Running) return b.ExitCode;
            await Task.Delay(5, ct);
        }
    }

    public Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct) => RunAsync("logs", NameOf(id), () =>
    {
        var b = Get(id);
        var text = string.Join('\n', b.Output.Split('\n').TakeLast(tail));
        return b.Tty ? Encoding.UTF8.GetBytes(text) : Framed(text);
    });

    public Task<JsonArray> ChangesAsync(string id, CancellationToken ct) => RunAsync("changes", NameOf(id), () => (JsonArray)Get(id).Changes.DeepClone());

    public Task RemoveAsync(string id, bool force, CancellationToken ct) => RunAsync("remove", NameOf(id), () =>
    {
        var b = Get(id);
        Settle(b);
        if (b.Running && !force)
            throw new EngineException(409, $"cannot remove container \"/{b.Name}\": container is running: stop the container before removing or force remove");
        Calls.Add($"remove {b.Name}{(force ? " force" : "")}");
        Unbind(b);
        Boxes.Remove(b);
        return true;
    });

    public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct) => RunAsync("create-network", Str(body, "Name"), () =>
    {
        var name = Str(body, "Name");
        if (NetworkId(name) is not null) throw new EngineException(409, $"network with name {name} already exists");
        var network = (JsonObject)body.DeepClone();
        network["Id"] = NewId();
        network["Created"] = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        Networks.Add(network);
        Calls.Add($"create network {name}");
        return Str(network, "Id");
    });

    // Docker refuses while a running container is on it.
    public Task RemoveNetworkAsync(string idOrName, CancellationToken ct) => RunAsync("remove-network", idOrName, () =>
    {
        var network = Networks.OfType<JsonObject>().FirstOrDefault(n => Str(n, "Name") == idOrName || Str(n, "Id") == idOrName)
            ?? throw new EngineException(404, $"network {idOrName} not found");
        var name = Str(network, "Name");
        if (Boxes.Any(b => { Settle(b); return b.Running && b.Endpoints.ContainsKey(name); }))
            throw new EngineException(403, $"error while removing network: network {name} has active endpoints");
        Networks.Remove(network);
        Calls.Add($"remove network {name}");
        return true;
    });

    /// <summary>Docker's answer to binding these host ports now: null when they're free.</summary>
    public string? TryBind(IEnumerable<ProbePort> ports)
    {
        lock (_gate)
            foreach (var p in ports)
                if (HostPrograms.Contains(p.HostPort))
                    return $"failed to bind host port for 0.0.0.0:{p.HostPort}:172.17.0.9:{p.HostPort}/tcp: address already in use";
                else if (Holder(p.HostPort, p.Protocol, p.HostIp, null) is not null)
                    return $"Bind for 0.0.0.0:{p.HostPort} failed: port is already allocated";
        return null;
    }

    /// <summary>Whether something answers on the host port, from the host's own network.</summary>
    public bool AnswersOnHost(string address, int port)
    {
        lock (_gate)
            return Boxes.Any(b =>
            {
                Settle(b);
                return b.Running && clock.GetUtcNow() >= b.StartedAt + b.ListenAfter && b.Live.Any(l =>
                    l.Value.Any(x => x.Port == port && (x.Ip is "0.0.0.0" or "::" || x.Ip == address))
                    && int.TryParse(l.Key.Split('/')[0], out var cp) && b.Listens.Contains(cp) && l.Key.EndsWith("/tcp", StringComparison.Ordinal));
            });
    }

    /// <summary>Whether the container answers on its port, by name from a container next to it.</summary>
    public bool AnswersByName(string name, int port)
    {
        lock (_gate)
            return Boxes.Any(b =>
            {
                Settle(b);
                return b.Name == name && b.Running && b.Listens.Contains(port) && clock.GetUtcNow() >= b.StartedAt + b.ListenAfter;
            });
    }

    private async Task<T> RunAsync<T>(string call, string target, Func<T> action)
    {
        if (Gates.TryGetValue($"{call} {target}", out var gate))
        {
            Held.TrySetResult();
            await gate.Task;
        }
        var hangs = Hang is { } hang && hang.Call == call && hang.Target == target && Interlocked.Decrement(ref _hangSkips) < 0;
        if (hangs && Hang is { After: false }) await ForeverAsync();
        T result;
        lock (_gate)
        {
            if (Fail?.Invoke(call, target) is { } error) throw error;
            result = action();
        }
        if (FailAfter?.Invoke(call, target) is { } late) throw late;
        if (hangs && Hang is { After: true }) await ForeverAsync();
        return result;
    }

    private Task ForeverAsync()
    {
        Hung.TrySetResult();
        return new TaskCompletionSource().Task;
    }

    private string NameOf(string id) => Find(id)?.Name ?? id;

    private JsonObject InspectOf(Box b)
    {
        Settle(b);
        var ports = new JsonObject();
        if (b.Running)
        {
            foreach (var (key, _) in b.Config["ExposedPorts"] as JsonObject ?? []) ports[key] = null;
            foreach (var (key, list) in b.Live)
                ports[key] = new JsonArray(list.Select(x => (JsonNode)new JsonObject { ["HostIp"] = x.Ip, ["HostPort"] = x.Port.ToString(CultureInfo.InvariantCulture) }).ToArray());
        }
        var networks = new JsonObject();
        var i = 0;
        foreach (var (net, node) in b.Endpoints)
        {
            i++;
            var e = (JsonObject)(node?.DeepClone() ?? new JsonObject());
            e["NetworkID"] = NetworkId(net) ?? "";
            e["EndpointID"] = b.Running ? "e" + b.Id[..10] : "";
            e["IPAddress"] = b.Running ? b.Addresses.GetValueOrDefault(net, "") : "";
            e["IPPrefixLen"] = b.Running ? 16 : 0;
            var mac = Str(e, "MacAddress");
            e["MacAddress"] = b.Running ? (mac.Length > 0 ? mac : $"02:42:ac:11:00:{i:x2}") : "";
            e["DNSNames"] = net == "bridge" ? null : new JsonArray(b.Name, b.Id[..12]);
            networks[net] = e;
        }
        var state = new JsonObject
        {
            ["Status"] = StatusOf(b),
            ["Running"] = b.Running,
            ["Paused"] = false,
            ["Restarting"] = b.Restarting,
            ["OOMKilled"] = b.OomKilled,
            ["Dead"] = false,
            ["ExitCode"] = b.ExitCode,
            ["StartedAt"] = (b.StartedAt ?? DateTimeOffset.MinValue).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["FinishedAt"] = (b.FinishedAt ?? DateTimeOffset.MinValue).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        if (b.Config["Healthcheck"]?["Test"] is JsonArray { Count: > 0 } && b.StartedAt is { } started)
        {
            var since = clock.GetUtcNow() - started;
            var status = b.Unhealthy is not null ? (since >= TimeSpan.FromSeconds(10) ? "unhealthy" : "starting") : since >= b.HealthyAfter ? "healthy" : "starting";
            state["Health"] = new JsonObject
            {
                ["Status"] = status,
                ["FailingStreak"] = 0,
                ["Log"] = new JsonArray(new JsonObject { ["ExitCode"] = status == "unhealthy" ? 1 : 0, ["Output"] = b.Unhealthy ?? "accepting connections\n" }),
            };
        }
        return new JsonObject
        {
            ["Id"] = b.Id,
            ["Created"] = b.Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["Name"] = "/" + b.Name,
            ["Image"] = b.Image,
            ["RestartCount"] = b.RestartCount,
            ["State"] = state,
            ["HostConfig"] = b.HostConfig.DeepClone(),
            ["Mounts"] = b.Mounts.DeepClone(),
            ["Config"] = b.Config.DeepClone(),
            ["NetworkSettings"] = new JsonObject { ["Ports"] = ports, ["Networks"] = networks },
        };
    }

    // What happened by now: a stop Docker carried on with comes to its end, and a container that exits on its own does so
    // once its time has come, then is restarted or not by its restart policy, as often as the time allows.
    private void Settle(Box b)
    {
        var now = clock.GetUtcNow();
        if (b.StoppingUntil is { } down && now >= down)
        {
            b.StoppingUntil = null;
            if (b.Running) Down(b, down, 0);
        }
        while (true)
        {
            if (b.Restarting)
            {
                if (now < b.RestartAt) return;
                b.Restarting = false;
                try
                {
                    Bind(b);
                }
                catch (EngineException)
                {
                    // Its port went meanwhile: Docker gives up on it.
                    b.Running = false;
                    b.ExitCode = 128;
                    return;
                }
                b.StartedAt = b.RestartAt;
                b.ExitCode = 0;
                b.OomKilled = false;
                continue;
            }
            if (!b.Running || b.StoppingUntil is not null || b.ExitsAfter is not { } after || b.StartedAt is not { } started || now < started + after) return;
            var exitAt = started + after;
            var code = b.OutOfMemory ? 137 : b.ExitWith;
            Down(b, exitAt, code);
            b.OomKilled = b.OutOfMemory;
            if (!RestartsAfter(b, code)) return;
            b.Running = true;
            b.Restarting = true;
            b.RestartCount++;
            b.Backoff = b.Backoff == TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : b.Backoff * 2;
            b.RestartAt = exitAt + b.Backoff;
        }
    }

    // Docker's monitor: always and unless-stopped restart a container that exited on its own (a stop never comes here),
    // on-failure only after a non-zero exit, and only MaximumRetryCount times when that's set.
    private static bool RestartsAfter(Box b, int exitCode)
    {
        var policy = b.HostConfig["RestartPolicy"];
        return Str(policy, "Name") switch
        {
            "always" or "unless-stopped" => true,
            "on-failure" => exitCode != 0 && (policy?["MaximumRetryCount"]?.GetValue<int>() is not (> 0 and var most) || b.RestartCount < most),
            _ => false,
        };
    }

    private void Down(Box b, DateTimeOffset at, int exitCode)
    {
        Unbind(b);
        b.Running = false;
        b.ExitCode = exitCode;
        b.FinishedAt = at;
    }

    private static string StatusOf(Box b) => b.Restarting ? "restarting" : b.Running ? "running" : b.StartedAt is null ? "created" : "exited";

    // Ports and static addresses are taken at start, as Docker does, with Docker's errors when they aren't free.
    private void Bind(Box b)
    {
        var live = new Dictionary<string, List<(string Ip, int Port)>>(StringComparer.Ordinal);
        foreach (var (key, node) in b.HostConfig["PortBindings"] as JsonObject ?? [])
        {
            var protocol = key.Contains('/') ? key[(key.IndexOf('/') + 1)..] : "tcp";
            foreach (var binding in (node as JsonArray ?? []).OfType<JsonObject>())
            {
                var ip = Str(binding, "HostIp");
                var port = int.TryParse(Str(binding, "HostPort"), out var p) && p > 0 ? p : NextRandomPort(protocol);
                if (HostPrograms.Contains(port))
                    throw new EngineException(500, $"failed to set up container networking: driver failed programming external connectivity on endpoint {b.Name} ({b.Id}): failed to bind host port for {(ip.Length == 0 ? "0.0.0.0" : ip)}:{port}:172.17.0.2:{key}: address already in use");
                if (Holder(port, protocol, ip, b) is not null)
                    throw new EngineException(500, $"failed to set up container networking: driver failed programming external connectivity on endpoint {b.Name} ({b.Id}): Bind for {(ip.Length == 0 ? "0.0.0.0" : ip)}:{port} failed: port is already allocated");
                var list = live.TryGetValue(key, out var l) ? l : live[key] = [];
                if (ip.Length == 0)
                {
                    list.Add(("0.0.0.0", port));
                    list.Add(("::", port));
                }
                else list.Add((ip, port));
            }
        }
        // -P: every exposed port without a binding gets a random one, a new one at each start.
        if (b.HostConfig["PublishAllPorts"]?.GetValueKind() == JsonValueKind.True)
            foreach (var (key, _) in b.Config["ExposedPorts"] as JsonObject ?? [])
                if (!live.ContainsKey(key))
                {
                    var port = NextRandomPort(key.Contains('/') ? key[(key.IndexOf('/') + 1)..] : "tcp");
                    while (live.Values.Any(l => l.Any(x => x.Port == port))) port++;
                    live[key] = [("0.0.0.0", port), ("::", port)];
                }
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (net, node) in b.Endpoints)
        {
            var fixedIp = Str(node?["IPAMConfig"], "IPv4Address");
            if (fixedIp.Length > 0 && Boxes.Any(o => o != b && o.Running && o.Addresses.GetValueOrDefault(net) == fixedIp))
                throw new EngineException(500, "failed to set up container networking: Address already in use");
            addresses[net] = fixedIp.Length > 0 ? fixedIp : $"172.21.0.{Interlocked.Increment(ref _ids) % 200 + 10}";
        }
        b.Live.Clear();
        foreach (var (key, list) in live) b.Live[key] = list;
        b.Addresses.Clear();
        foreach (var (net, ip) in addresses) b.Addresses[net] = ip;
    }

    private static void Unbind(Box b)
    {
        b.Live.Clear();
        b.Addresses.Clear();
    }

    // Who holds the host port: one whose binding of that port and protocol overlaps the address (every address clashes
    // with any other; 0.0.0.0 with IPv4 addresses, :: with IPv6 ones).
    private Box? Holder(int port, string protocol, string ip, Box? self) =>
        Boxes.FirstOrDefault(o => o != self && o.Running && o.Live.Any(l => l.Key.EndsWith("/" + protocol, StringComparison.Ordinal)
            && l.Value.Any(x => x.Port == port && ContainerPorts.Overlap(x.Ip, ip))));

    private int NextRandomPort(string protocol)
    {
        var port = 32768;
        while (Holder(port, protocol, "", null) is not null) port++;
        return port;
    }

    // Top-level Mounts as Docker makes them at create: binds, named volumes, and a new anonymous volume for every VOLUME
    // path, or -v /path, that nothing else mounts.
    private JsonArray MountsFor(JsonObject host, JsonObject config)
    {
        var mounts = new JsonArray();
        var at = new HashSet<string>(StringComparer.Ordinal);
        void Mount(string type, string? name, string source, string destination, bool rw)
        {
            if (!at.Add(destination)) throw new EngineException(400, $"Duplicate mount point: {destination}");
            var m = new JsonObject { ["Type"] = type };
            if (name is not null) m["Name"] = name;
            m["Source"] = source;
            m["Destination"] = destination;
            m["RW"] = rw;
            mounts.Add(m);
            if (type == "volume") Volumes.Add(name!);
        }
        foreach (var spec in (host["Binds"] as JsonArray ?? []).OfType<JsonValue>().Select(v => v.GetValue<string>()))
        {
            var parts = spec.Split(':');
            var rw = parts.Length < 3 || !parts[2].Split(',').Contains("ro");
            if (parts.Length == 1) Mount("volume", Anonymous(), "", parts[0], true);
            else if (parts[0].StartsWith('/')) Mount("bind", null, parts[0], parts[1], rw);
            else Mount("volume", parts[0], $"/var/lib/docker/volumes/{parts[0]}/_data", parts[1], rw);
        }
        foreach (var m in (host["Mounts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = Str(m, "Type");
            var source = Str(m, "Source");
            var target = Str(m, "Target");
            var rw = m["ReadOnly"]?.GetValueKind() != JsonValueKind.True;
            if (type == "volume") Mount("volume", source.Length > 0 ? source : Anonymous(), "", target, rw);
            else Mount(type, null, source, target, rw);
        }
        foreach (var (path, _) in config["Volumes"] as JsonObject ?? [])
            if (!at.Contains(path)) Mount("volume", Anonymous(), "", path, true);
        return mounts;
    }

    private string Anonymous() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    private string NewId() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    // By its tag, its ID, or one of its digests (postgres@sha256:…).
    private JsonObject? ImageOf(string nameOrId) =>
        Images.TryGetValue(nameOrId, out var image) ? image
        : Images.Values.FirstOrDefault(i => Str(i, "Id") == nameOrId)
          ?? Images.Values.FirstOrDefault(i => (i["RepoDigests"] as JsonArray ?? []).Any(d => d?.GetValue<string>() == nameOrId));

    private static bool IsNetworkName(string mode) => mode is not ("" or "default" or "host" or "none") && !mode.StartsWith("container:", StringComparison.Ordinal);

    private string? NetworkId(string name) =>
        Networks.OfType<JsonObject>().FirstOrDefault(n => Str(n, "Name") == name) is { } n ? Str(n, "Id") : null;

    private static EngineException Conflict(string name, string id) =>
        new(409, $"Conflict. The container name \"/{name}\" is already in use by container \"{id}\". You have to remove (or rename) that container to be able to reuse that name.");

    /// <summary>Docker's frames for a container without a TTY: stdout, padding, big-endian length, the bytes.</summary>
    public static byte[] Framed(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at.Year > 1 ? at : null;

    private static string Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>The host's side, faked: the host-port map, the probe, the LAN check from the host, the check by name and who uses what.</summary>
internal sealed class FakeHost(FakeEngine engine) : IHostPortChecks, IHostPortProbe, ILanCheck, INameCheck, IContainerUsers
{
    public string Lan { get; set; } = "10.1.1.80";
    public string LanHostIp { get; set; } = "";
    /// <summary>Who the host-port map says holds a host port.</summary>
    public Dictionary<int, string> Holders { get; } = new();
    public Dictionary<int, IReadOnlyList<string>> Routes { get; } = new();
    /// <summary>The probe's answer; by default, whatever the engine would say to binding them now.</summary>
    public Func<IReadOnlyList<ProbePort>, string?>? Probe { get; set; }
    /// <summary>Gitberth shares a network with the containers, so it can try them by name.</summary>
    public bool ByName { get; set; } = true;
    /// <summary>Why the LAN check can't run; null when it can.</summary>
    public string? LanCheckFails { get; set; }
    public ContainerUsers Users { get; set; } = ContainerUsers.None;
    /// <summary>Reading who uses a container fails.</summary>
    public bool UsersFail { get; set; }
    /// <summary>Each LAN check: "10.1.1.80:5432".</summary>
    public List<string> LanChecks { get; } = [];
    /// <summary>Called with each LAN check before it's answered, for a test that changes things at that moment.</summary>
    public Action<string, int>? Connecting { get; set; }
    public int Probes { get; private set; }
    /// <summary>The host-port map's questions: "5432/tcp on 127.0.0.1".</summary>
    public List<string> HolderChecks { get; } = [];

    public Task<string> LanAddressAsync(CancellationToken ct) => Task.FromResult(Lan);

    public Task<string> LanHostIpAsync(CancellationToken ct) => Task.FromResult(LanHostIp);

    public Task<string?> HolderAsync(string container, int hostPort, string protocol, string hostIp, CancellationToken ct)
    {
        lock (HolderChecks) HolderChecks.Add($"{hostPort}/{protocol} on {(hostIp.Length == 0 ? "every address" : hostIp)}");
        return Task.FromResult(Holders.GetValueOrDefault(hostPort));
    }

    public Task<IReadOnlyList<string>> TunnelRoutesAsync(int hostPort, CancellationToken ct) =>
        Task.FromResult(Routes.GetValueOrDefault(hostPort) ?? []);

    public Task<string?> ProbeAsync(IReadOnlyList<ProbePort> ports, CancellationToken ct)
    {
        Probes++;
        return Task.FromResult(Probe is { } probe ? probe(ports) : engine.TryBind(ports));
    }

    public Task<ConnectResult> ConnectAsync(string address, int port, CancellationToken ct)
    {
        Connecting?.Invoke(address, port);
        lock (LanChecks) LanChecks.Add($"{address}:{port}");
        return Task.FromResult(LanCheckFails is { } why ? new ConnectResult(false, why) : engine.AnswersOnHost(address, port) ? ConnectResult.Yes : ConnectResult.No);
    }

    public Task<bool?> AnswersAsync(string container, int port, CancellationToken ct) =>
        Task.FromResult(ByName ? engine.AnswersByName(container, port) : (bool?)null);

    public Task<ContainerUsers> UsersOfAsync(string container, IReadOnlyCollection<string> addresses, CancellationToken ct) =>
        UsersFail ? Task.FromException<ContainerUsers>(new InvalidOperationException("docker inspect failed")) : Task.FromResult(Users);
}

/// <summary>The journal kept in memory, as the database would keep it.</summary>
internal sealed class MemoryJournal : IChangeJournal
{
    private readonly List<ContainerChange> _rows = [];

    public IReadOnlyList<ContainerChange> Rows
    {
        get
        {
            lock (_rows) return _rows.ToList();
        }
    }

    public Task<ContainerChange> AddAsync(ContainerChange change, CancellationToken ct)
    {
        lock (_rows)
        {
            var row = change with { Id = _rows.Count + 1 };
            _rows.Add(row);
            return Task.FromResult(row);
        }
    }

    public Task UpdateAsync(ContainerChange change, CancellationToken ct)
    {
        lock (_rows) _rows[_rows.FindIndex(r => r.Id == change.Id)] = change;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContainerChange>> UnfinishedAsync(CancellationToken ct)
    {
        lock (_rows) return Task.FromResult<IReadOnlyList<ContainerChange>>(_rows.Where(r => r.Unfinished).ToList());
    }

    public Task<ContainerChange?> LatestAsync(string container, CancellationToken ct)
    {
        lock (_rows) return Task.FromResult(_rows.LastOrDefault(r => r.ContainerName == container));
    }

    public Task DismissNoticeAsync(int id, CancellationToken ct)
    {
        lock (_rows)
        {
            var at = _rows.FindIndex(r => r.Id == id);
            if (at >= 0) _rows[at] = _rows[at] with { Notice = null };
        }
        return Task.CompletedTask;
    }

    public Task<ContainerChange?> WithContainerAsync(string containerId, CancellationToken ct)
    {
        lock (_rows) return Task.FromResult(_rows.LastOrDefault(r => r.OldId == containerId || r.NewId == containerId));
    }
}
