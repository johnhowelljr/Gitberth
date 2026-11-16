using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Health;

namespace Gitberth.Docker;

/// <summary>A call the engine answered with an error: its HTTP status and the message from Docker's {"message"} body.</summary>
public sealed class DockerApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public bool NotFound => Status == 404;
}

public sealed record RunningContainer(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    string? NetworkMode,
    IReadOnlyDictionary<string, string> Labels);

public readonly record struct NetCounters(ulong RxBytes, ulong TxBytes);

/// <summary>
/// Cumulative counters from one stats call. Network is null when the container has no network of its own;
/// ReadAt is when the engine took the reading (UTC), if it said.
/// </summary>
public sealed record StatsReading(ulong CpuNanos, ulong MemoryBytes, ulong MemoryLimit, NetCounters? Network, DateTime? ReadAt = null);

public sealed record DockerHostInfo(string Name, int Cpus, ulong MemoryBytes, string ServerVersion);

/// <summary>
/// A client for the Docker Engine API, over the socket. Builds and app containers go through the docker CLI, but the
/// API returns what the CLI only prints as text: raw stats counters, structured ports and networks, and inspect JSON.
/// Every path carries the API version both sides speak (asked once with GET /version), and every call has a timeout
/// of its own. It also makes the changes a recreate takes (create, update, stop, rename, start, remove), which need
/// API 1.44 (Docker 25), and it never removes a volume. Images, pulls and registries are in its other part
/// (DockerApi.Images.cs).
/// </summary>
public sealed partial class DockerApi : IDisposable
{
    /// <summary>The newest Engine API version Gitberth knows: Docker 29.9's.</summary>
    public static readonly Version NewestApi = new(1, 56);

    /// <summary>
    /// The oldest Engine API version Gitberth changes containers with: Docker 25's. Inspect and create speak the same
    /// version, and older ones leave out settings a copy of a container needs, such as per-network MAC addresses.
    /// </summary>
    public static readonly Version OldestChangingApi = new(1, 44);

    /// <summary>
    /// How long a call that reads may take when it doesn't say: a busy engine (builds, pulls, many containers changing at
    /// once) can take well over ten seconds to answer.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a change (making, starting, renaming, updating or removing a container, a network's change) is waited for
    /// before Gitberth asks Docker what became of it: see <see cref="DefaultSettleWait"/>.
    /// </summary>
    public static readonly TimeSpan DefaultChangeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long, once a change's answer is late, Gitberth keeps asking Docker whether it did it all the same: an engine
    /// finishes what it was asked even when it answers late, and a change it made isn't a failure.
    /// </summary>
    public static readonly TimeSpan DefaultSettleWait = TimeSpan.FromSeconds(60);

    /// <summary>How long a change is waited for (<see cref="DefaultChangeTimeout"/>; shorter in tests).</summary>
    internal TimeSpan ChangeTimeout { get; init; } = DefaultChangeTimeout;

    /// <summary>How long a late change is checked on (<see cref="DefaultSettleWait"/>; shorter in tests).</summary>
    internal TimeSpan SettleWait { get; init; } = DefaultSettleWait;

    /// <summary>How often a late change is checked on.</summary>
    internal TimeSpan SettlePoll { get; init; } = TimeSpan.FromSeconds(2);

    // A container made, or started, more than this before Gitberth asked can't be its own doing. The engine's clock and
    // Gitberth's may differ a little (the same host's clock, over the socket); a larger difference only means a late
    // change isn't recognized, and is a timeout as before, never the other way round.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(10);

    /// <summary>The longest Gitberth waits for Docker to say a container stopped, for one that's never killed (--stop-timeout -1).</summary>
    public static readonly TimeSpan LongestStop = TimeSpan.FromMinutes(10);

    // No timeout of its own: each call sets one, and the event stream stays open for as long as Gitberth runs.
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private Version? _api;
    private string? _engine;

    /// <param name="expected">Where every change it makes is registered first, so the audit log knows the events as Gitberth's own.</param>
    public DockerApi(GitberthOptions options, Audit.ExpectedChanges? expected = null) : this(options.DockerHost, CreateHandler(options.DockerHost))
    {
        Expected = expected;
    }

    /// <summary>Where the changes it makes are registered before they're made (the audit log's); none in tests.</summary>
    internal Audit.ExpectedChanges? Expected { get; init; }

    /// <summary>Talks to the engine through <paramref name="handler"/>: a stand-in engine, in tests.</summary>
    internal DockerApi(string endpoint, HttpMessageHandler handler)
    {
        Endpoint = endpoint;
        var tcp = endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
        // Over a socket the host name is never used; it only has to make a valid URL.
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(tcp ? $"http://{endpoint["tcp://".Length..].TrimEnd('/')}/" : "http://docker/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The engine's address, in DOCKER_HOST form.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// The API version every path uses: the engine's own, up to <see cref="NewestApi"/>. Asked once; a failed ask is asked
    /// again on the next call.
    /// </summary>
    public async Task<Version> ApiVersionAsync(CancellationToken ct = default)
    {
        if (_api is { } known) return known;
        await _versionGate.WaitAsync(ct);
        try
        {
            if (_api is { } again) return again;
            // /version is the one path every engine answers without a version in it.
            var answer = await SendCoreAsync(HttpMethod.Get, "version", null, DefaultTimeout, ct);
            var api = Negotiate(answer);
            _engine = EngineVersionOf(answer);
            return _api = api;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    /// <summary>The engine's own version, such as 29.9.2, asked with the API version; empty when it didn't say.</summary>
    public async Task<string> EngineVersionAsync(CancellationToken ct = default)
    {
        await ApiVersionAsync(ct);
        return _engine ?? "";
    }

    /// <summary>
    /// One call to the engine, on the negotiated API version: <paramref name="path"/> is everything after it, such as
    /// "containers/json?all=true". Returns the answer's body. Docker's {"message"} on a failure becomes a
    /// <see cref="DockerApiException"/>, and a call that outlasts its timeout a <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // A change is Gitberth's own from just before it's asked for until a little after Docker answered.
        using var expected = Expected?.ForApi(method, path, body);
        return await SendCoreAsync(method, await VersionedAsync(path, ct), body, timeout ?? DefaultTimeout, ct);
    }

    public async Task<List<RunningContainer>> ListRunningAsync(CancellationToken ct = default) =>
        ParseContainers(await SendAsync(HttpMethod.Get, "containers/json", ct: ct));

    /// <summary>Every container, stopped ones too.</summary>
    public async Task<List<RunningContainer>> ListAllAsync(CancellationToken ct = default) =>
        ParseContainers(await SendAsync(HttpMethod.Get, "containers/json?all=true", ct: ct));

    /// <summary>Every container, stopped ones too, with its ports and networks as Docker structures them.</summary>
    public async Task<List<NetworkContainer>> ListContainersAsync(CancellationToken ct = default) =>
        NetworkContainer.ParseList(await SendAsync(HttpMethod.Get, "containers/json?all=true", ct: ct));

    /// <summary>A container as `docker container inspect` gives it, untouched; null when there's no such container.</summary>
    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct = default) =>
        GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", ct);

    /// <summary>An image as `docker image inspect` gives it, untouched; null when there's no such image.</summary>
    public Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct = default) =>
        GetObjectAsync($"images/{Uri.EscapeDataString(idOrName)}/json", ct);

    /// <summary>Every network on the host. Docker's list doesn't say which containers are on each: their own endpoints do.</summary>
    public async Task<List<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default) =>
        NetworkInfo.ParseList(await SendAsync(HttpMethod.Get, "networks", ct: ct));

    /// <summary>One network by name or ID; null when there's none.</summary>
    public async Task<NetworkInfo?> InspectNetworkAsync(string idOrName, CancellationToken ct = default) =>
        await GetObjectAsync($"networks/{Uri.EscapeDataString(idOrName)}", ct) is { } network ? NetworkInfo.Parse(network) : null;

    /// <summary>POST networks/create: a network as <paramref name="body"/> describes it. Returns its ID.</summary>
    public async Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct = default)
    {
        var name = body["Name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : "";
        var answer = JsonNode.Parse(await SendChangeAsync(HttpMethod.Post, "networks/create", body,
            // Made all the same: the network of that name is there now.
            async token => name.Length > 0 && await GetObjectAsync($"networks/{Uri.EscapeDataString(name)}", token) is { } made
                ? new JsonObject { ["Id"] = made["Id"]?.DeepClone() }.ToJsonString()
                : null,
            ct));
        return answer?["Id"] is JsonValue id && id.TryGetValue<string>(out var madeId) && madeId.Length > 0
            ? madeId
            : throw new DockerApiException(0, $"Docker didn't say which network it made for {body["Name"]}.");
    }

    /// <summary>
    /// DELETE networks/{id}: the one network, which Docker refuses while a running container is on it. Gitberth never
    /// prunes networks.
    /// </summary>
    public async Task RemoveNetworkAsync(string idOrName, CancellationToken ct = default) =>
        await SendChangeAsync(HttpMethod.Delete, $"networks/{Uri.EscapeDataString(idOrName)}", null,
            async token => await GetObjectAsync($"networks/{Uri.EscapeDataString(idOrName)}", token) is null ? "" : null, ct);

    /// <summary>
    /// POST networks/{network}/connect: <paramref name="container"/> joins it with <paramref name="endpoint"/> (its aliases,
    /// address and gateway priority). Joined all the same when its answer was late, once the container is on it.
    /// </summary>
    public async Task ConnectNetworkAsync(string network, string container, JsonObject endpoint, CancellationToken ct = default) =>
        await SendChangeAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/connect",
            new JsonObject { ["Container"] = container, ["EndpointConfig"] = endpoint.DeepClone() },
            async token => await OnNetworkAsync(container, network, token) == true ? "" : null, ct);

    /// <summary>
    /// POST networks/{network}/disconnect, not forced: Docker says why it can't rather than tearing the endpoint down
    /// regardless. Left all the same when its answer was late, once the container isn't on it (or is gone).
    /// </summary>
    public async Task DisconnectNetworkAsync(string network, string container, CancellationToken ct = default) =>
        await SendChangeAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/disconnect",
            new JsonObject { ["Container"] = container, ["Force"] = false },
            async token => await OnNetworkAsync(container, network, token) != true ? "" : null, ct);

    // Whether the container is on the network (by its name, or its ID's start); null when there's no such container.
    private async Task<bool?> OnNetworkAsync(string container, string network, CancellationToken ct)
    {
        if (await GetObjectAsync($"containers/{Uri.EscapeDataString(container)}/json", ct) is not { } inspect) return null;
        if (inspect["NetworkSettings"]?["Networks"] is not JsonObject networks) return false;
        return networks.Any(n => n.Key == network
            || (n.Value?["NetworkID"] is JsonValue id && id.TryGetValue<string>(out var s) && network.Length >= 12 && s.StartsWith(network, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Containers' events from <paramref name="since"/> on (the engine remembers recent ones), then as they happen. Only
    /// the ones the Health page marks; it ends when the connection does.
    /// </summary>
    public async IAsyncEnumerable<ContainerEvent> StreamEventsAsync(DateTime since, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ticks = (since - DateTime.UnixEpoch).Ticks;
        var from = $"{ticks / TimeSpan.TicksPerSecond}.{ticks % TimeSpan.TicksPerSecond * 100:D9}";
        var filters = Uri.EscapeDataString("""{"type":["container"]}""");
        using var response = await _http.GetAsync(await VersionedAsync($"events?since={from}&filters={filters}", ct), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        // One JSON object per line.
        while (await reader.ReadLineAsync(ct) is { } line)
            if (ParseEvent(line) is { } e) yield return e;
    }

    /// <summary>
    /// Every event that <paramref name="filters"/> (Docker's filters JSON) lets through, from <paramref name="since"/> on, as the
    /// raw lines the engine sends (one JSON object each): the audit log reads them itself. Ends when the connection does.
    /// </summary>
    public async IAsyncEnumerable<string> StreamEventLinesAsync(DateTime since, string filters, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ticks = (since - DateTime.UnixEpoch).Ticks;
        var from = $"{ticks / TimeSpan.TicksPerSecond}.{ticks % TimeSpan.TicksPerSecond * 100:D9}";
        using var response = await _http.GetAsync(await VersionedAsync($"events?since={from}&filters={Uri.EscapeDataString(filters)}", ct), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Length > 0) yield return line;
    }

    /// <summary>One reading, without the second sample `docker stats` waits for: Gitberth diffs its own readings.</summary>
    public async Task<StatsReading> GetStatsAsync(string containerId, CancellationToken ct = default) =>
        ParseStats(await SendAsync(HttpMethod.Get, $"containers/{Uri.EscapeDataString(containerId)}/stats?stream=false&one-shot=true", ct: ct));

    public async Task<DockerHostInfo> GetHostInfoAsync(CancellationToken ct = default) =>
        ParseHostInfo(await SendAsync(HttpMethod.Get, "info", ct: ct));

    /// <summary>POST containers/create?name=: the container is made, not started. Returns its ID.</summary>
    public async Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var asked = DateTime.UtcNow;
        var answer = JsonNode.Parse(await SendChangeAsync(HttpMethod.Post, $"containers/create?name={Uri.EscapeDataString(name)}", body,
            // Made all the same: a container of that name, made since Gitberth asked (a name another one had is Docker's 409).
            async token => await GetObjectAsync($"containers/{Uri.EscapeDataString(name)}/json", token) is { } made && Since(made["Created"], asked)
                ? new JsonObject { ["Id"] = made["Id"]?.DeepClone() }.ToJsonString()
                : null,
            ct));
        return answer?["Id"] is JsonValue id && id.TryGetValue<string>(out var madeId) && madeId.Length > 0
            ? madeId
            : throw new DockerApiException(0, $"Docker didn't say which container it made for {name}.");
    }

    /// <summary>POST containers/{id}/update with the restart policy alone: nothing else about the container changes.</summary>
    public async Task UpdateRestartPolicyAsync(string idOrName, JsonObject policy, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var update = new JsonObject { ["RestartPolicy"] = policy.DeepClone() };
        await SendChangeAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/update", update,
            async token => await HasAsync(idOrName, update, token) ? "" : null, ct);
    }

    /// <summary>
    /// POST containers/{id}/update (docker update): only what <paramref name="update"/> names changes (its memory, CPUs, PID
    /// limit, restart policy), live, with no restart. Docker reads 0 as "unchanged". Returns Docker's answer:
    /// {"Warnings":[…]}, an empty list when it had nothing to say.
    /// </summary>
    public async Task<JsonObject> UpdateContainerAsync(string idOrName, JsonObject update, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var answer = await SendChangeAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/update", update.DeepClone(),
            async token => await HasAsync(idOrName, update, token) ? "" : null, ct);
        var parsed = answer.Trim().Length == 0 ? null : JsonNode.Parse(answer) as JsonObject;
        var warnings = parsed?["Warnings"] as JsonArray ?? [];
        return new JsonObject { ["Warnings"] = warnings.DeepClone() };
    }

    // Whether the container's HostConfig already says what an update asked for: each value it names, but 0, which Docker
    // reads as "unchanged".
    private async Task<bool> HasAsync(string idOrName, JsonObject update, CancellationToken ct)
    {
        if (await GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", ct) is not { } inspect
            || inspect["HostConfig"] is not JsonObject host) return false;
        foreach (var (key, wanted) in update)
        {
            if (wanted is JsonValue v && v.TryGetValue<long>(out var number) && number == 0) continue;
            if (wanted is JsonObject asked)
            {
                // A part of a setting, such as the restart policy's name: the parts asked for must match.
                if (host[key] is not JsonObject has || asked.Any(p => !JsonNode.DeepEquals(p.Value, has[p.Key]))) return false;
                continue;
            }
            if (!JsonNode.DeepEquals(wanted, host[key])) return false;
        }
        return true;
    }

    /// <summary>
    /// POST containers/{id}/stop?t=: its own stop signal, then a kill once <paramref name="timeoutSeconds"/> have passed;
    /// 0 kills it at once, and a negative one (sent as -1) never does. Docker answers once it's down, so the call waits
    /// that long and 15 s more, or up to <see cref="LongestStop"/>. One that isn't running counts as stopped (Docker's 304).
    /// </summary>
    public async Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var t = Math.Max(timeoutSeconds, -1);
        try
        {
            await SendChangeAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/stop?t={t.ToString(CultureInfo.InvariantCulture)}", null,
                // Stopped all the same: it isn't running (or is gone).
                async token => await GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", token) is not { } c || !Running(c) ? "" : null,
                ct, StopWaitFor(t));
        }
        catch (DockerApiException ex) when (ex.Status == 304)
        {
        }
    }

    /// <summary>How long a stop is waited for: its timeout and 15 s; for one that's never killed, or would wait longer, <see cref="LongestStop"/>.</summary>
    internal static TimeSpan StopWait(int timeoutSeconds) =>
        timeoutSeconds < 0 || TimeSpan.FromSeconds(timeoutSeconds + 15) > LongestStop ? LongestStop : TimeSpan.FromSeconds(timeoutSeconds + 15);

    /// <summary>How long past a stop's own timeout Docker's answer is waited for (15 s, as <see cref="StopWait"/>; shorter in tests).</summary>
    internal TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(15);

    // A stop over the API is waited for its own timeout and the grace (or LongestStop), and never less than any other
    // change: a busy engine is as slow to answer a stop as anything else.
    private TimeSpan StopWaitFor(int timeoutSeconds)
    {
        var wait = timeoutSeconds < 0 || TimeSpan.FromSeconds(timeoutSeconds) + StopGrace > LongestStop
            ? LongestStop
            : TimeSpan.FromSeconds(timeoutSeconds) + StopGrace;
        return wait > ChangeTimeout ? wait : ChangeTimeout;
    }

    /// <summary>POST containers/{id}/rename?name=. A name another container has is Docker's 409.</summary>
    public async Task RenameContainerAsync(string idOrName, string name, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        await SendChangeAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/rename?name={Uri.EscapeDataString(name)}", null,
            async token =>
            {
                // Renamed all the same: the new name is there, and it's this container (by its ID), or its old name is gone.
                if (await GetObjectAsync($"containers/{Uri.EscapeDataString(name)}/json", token) is not { } renamed) return null;
                if (renamed["Id"] is JsonValue id && id.TryGetValue<string>(out var full) && idOrName.Length >= 12 && full.StartsWith(idOrName, StringComparison.Ordinal))
                    return "";
                return await GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", token) is null ? "" : null;
            },
            ct);
    }

    /// <summary>POST containers/{id}/start. One that runs already counts as started (Docker's 304).</summary>
    public async Task StartContainerAsync(string idOrName, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var asked = DateTime.UtcNow;
        try
        {
            await SendChangeAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/start", null,
                // Started all the same: it runs, or it started since Gitberth asked (and has stopped again: its own business).
                async token => await GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", token) is { } c
                    && (Running(c) || Since(c["State"]?["StartedAt"], asked)) ? "" : null,
                ct);
        }
        catch (DockerApiException ex) when (ex.Status == 304)
        {
        }
    }

    /// <summary>POST containers/{id}/wait?condition=not-running: its exit code, once it isn't running. Waits as long as that takes, until cancelled.</summary>
    public async Task<int> WaitContainerAsync(string idOrName, CancellationToken ct = default)
    {
        var answer = JsonNode.Parse(await SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/wait?condition=not-running",
            timeout: Timeout.InfiniteTimeSpan, ct: ct));
        return answer?["StatusCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : 0;
    }

    /// <summary>
    /// GET containers/{id}/logs: the last <paramref name="tail"/> lines of stdout and stderr, as the bytes Docker sent.
    /// Docker frames them only for a container without a TTY, so they're left for the caller to read
    /// (<see cref="Gitberth.Network.LogStream.Text"/>).
    /// </summary>
    public async Task<byte[]> ContainerLogsAsync(string idOrName, int tail, CancellationToken ct = default) =>
        await SendCoreAsync(HttpMethod.Get, await VersionedAsync($"containers/{Uri.EscapeDataString(idOrName)}/logs?stdout=1&stderr=1&tail={tail.ToString(CultureInfo.InvariantCulture)}", ct),
            null, DefaultTimeout, (content, token) => content.ReadAsByteArrayAsync(token), ct);

    /// <summary>GET containers/{id}/changes: the files written outside its volumes, as [{"Path","Kind"}]; empty for none.</summary>
    public async Task<JsonArray> ContainerChangesAsync(string idOrName, CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, $"containers/{Uri.EscapeDataString(idOrName)}/changes", ct: ct)) as JsonArray ?? [];

    /// <summary>
    /// DELETE containers/{id}: the container goes, and its volumes, anonymous ones too, always stay: v is never sent.
    /// <paramref name="force"/> kills it first when it runs.
    /// </summary>
    public async Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        await SendChangeAsync(HttpMethod.Delete, $"containers/{Uri.EscapeDataString(idOrName)}?force={(force ? "true" : "false")}", null,
            // Removed all the same: it's gone.
            async token => await GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", token) is null ? "" : null, ct);
    }

    /// <summary>
    /// A change, as <see cref="SendAsync"/>, waited for up to <see cref="ChangeTimeout"/> (or <paramref name="timeout"/>).
    /// When its answer still hasn't come, Docker may have done it all the same: a busy engine finishes what it was asked
    /// even when it answers late. So <paramref name="happened"/> asks what became of it, every <see cref="SettlePoll"/> for
    /// up to <see cref="SettleWait"/>, and the answer it gives stands for Docker's. Only a change that still hasn't happened
    /// is a <see cref="TimeoutException"/>. The audit log knows the change as Gitberth's own all that time.
    /// </summary>
    private async Task<string> SendChangeAsync(HttpMethod method, string path, JsonNode? body, Func<CancellationToken, Task<string?>> happened,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        using var expected = Expected?.ForApi(method, path, body);
        var versioned = await VersionedAsync(path, ct);
        try
        {
            return await SendCoreAsync(method, versioned, body, timeout ?? ChangeTimeout, ct);
        }
        catch (TimeoutException late)
        {
            var until = DateTime.UtcNow + SettleWait;
            while (true)
            {
                try
                {
                    if (await happened(ct) is { } answer) return answer;
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException or IOException or JsonException)
                {
                    // Still too busy to say: ask again.
                }
                if (DateTime.UtcNow >= until)
                    throw new TimeoutException(
                        $"{late.Message.TrimEnd('.')}, and it still hadn't happened {SettleWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds later.");
                await Task.Delay(SettlePoll, ct);
            }
        }
    }

    // Whether the container runs now.
    private static bool Running(JsonObject inspect) =>
        inspect["State"]?["Running"] is JsonValue running && running.TryGetValue<bool>(out var yes) && yes;

    // Whether a time Docker gave (RFC 3339, such as "2026-11-16T20:28:43.123456789Z") is since Gitberth asked, give or take
    // the clocks' difference. Docker's zero time ("0001-01-01T00:00:00Z", never started) never is.
    private static bool Since(JsonNode? time, DateTime asked) =>
        time is JsonValue v && v.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
        && at.Year > 1 && at.UtcDateTime >= asked - ClockSkew;

    // Changing a container needs API 1.44: below it Gitberth only reads, and says why.
    private async Task ChangingAsync(CancellationToken ct)
    {
        var api = await ApiVersionAsync(ct);
        if (api < OldestChangingApi)
            throw new DockerApiException(0,
                $"Changing containers needs Docker 25 or later (API 1.44). This host has Docker {_engine} (API {api.ToString(2)}).");
    }

    public void Dispose()
    {
        _http.Dispose();
        _versionGate.Dispose();
    }

    /// <summary>
    /// The version to speak, from GET /version: the engine's own API version, but no newer than Gitberth knows, and no older
    /// than the oldest the engine still accepts.
    /// </summary>
    internal static Version Negotiate(string versionJson)
    {
        using var doc = JsonDocument.Parse(versionJson);
        if (!Version.TryParse(Str(doc.RootElement, "ApiVersion"), out var server))
            throw new DockerApiException(0, "Docker didn't say which API version it speaks.");
        var use = server < NewestApi ? server : NewestApi;
        return Version.TryParse(Str(doc.RootElement, "MinAPIVersion"), out var oldest) && use < oldest ? oldest : use;
    }

    /// <summary>The engine's own version from GET /version, such as 29.9.2.</summary>
    internal static string EngineVersionOf(string versionJson)
    {
        using var doc = JsonDocument.Parse(versionJson);
        return Str(doc.RootElement, "Version");
    }

    private async Task<string> VersionedAsync(string path, CancellationToken ct)
    {
        var api = await ApiVersionAsync(ct);
        return $"v{api.Major}.{api.Minor}/{path}";
    }

    // Null for a 404, so "there's no such thing" isn't an error to the caller.
    private async Task<JsonObject?> GetObjectAsync(string path, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await SendAsync(HttpMethod.Get, path, ct: ct)) as JsonObject;
        }
        catch (DockerApiException ex) when (ex.NotFound)
        {
            return null;
        }
    }

    private Task<string> SendCoreAsync(HttpMethod method, string path, JsonNode? body, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(method, path, body, timeout, (content, token) => content.ReadAsStringAsync(token), ct);

    private async Task<T> SendCoreAsync<T>(HttpMethod method, string path, JsonNode? body, TimeSpan timeout,
        Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, timer.Token);
            if (!response.IsSuccessStatusCode)
                throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(timer.Token), response));
            return await read(response.Content, timer.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker didn't answer within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.");
        }
    }

    /// <summary>Docker's own words for a failure: the "message" of its JSON answer, else the answer itself.</summary>
    internal static string ErrorMessage(string body, HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Str(doc.RootElement, "message") is { Length: > 0 } message) return message;
        }
        catch (JsonException)
        {
        }
        return body.Trim() is { Length: > 0 } text ? text : $"Docker answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
    }

    private static HttpMessageHandler CreateHandler(string dockerHost)
    {
        if (dockerHost.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) return new SocketsHttpHandler();

        var socketPath = dockerHost.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) ? dockerHost["unix://".Length..]
            : dockerHost.StartsWith('/') ? dockerHost
            : null;
        return new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                if (socketPath is null) throw new NotSupportedException($"Gitberth talks to Docker over unix:// or tcp://, not {dockerHost}.");
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    internal static List<RunningContainer> ParseContainers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<RunningContainer>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            // Legacy links add aliases like "/web/db"; the container's own name is the one without a second slash.
            var names = Get(c, "Names") is { ValueKind: JsonValueKind.Array } n
                ? n.EnumerateArray().Select(x => (x.GetString() ?? "").TrimStart('/')).Where(x => x.Length > 0).ToList()
                : [];
            var id = Str(c, "Id");
            var name = names.FirstOrDefault(x => !x.Contains('/')) ?? names.FirstOrDefault() ?? id[..Math.Min(12, id.Length)];

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Get(c, "Labels") is { ValueKind: JsonValueKind.Object } l)
                foreach (var label in l.EnumerateObject())
                    labels[label.Name] = label.Value.GetString() ?? "";

            var networkMode = Str(Get(c, "HostConfig"), "NetworkMode");
            result.Add(new RunningContainer(id, name, Str(c, "Image"), Str(c, "State"), Str(c, "Status"),
                networkMode.Length > 0 ? networkMode : null, labels));
        }
        return result;
    }

    internal static StatsReading ParseStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cpu = U64(Get(Get(root, "cpu_stats"), "cpu_usage"), "total_usage");

        // Like `docker stats`, leave out page cache the kernel can reclaim (cgroup v1: total_inactive_file, v2: inactive_file).
        var memory = Get(root, "memory_stats");
        var usage = U64(memory, "usage");
        var stats = Get(memory, "stats");
        var cache = Get(stats, "total_inactive_file").ValueKind == JsonValueKind.Number ? U64(stats, "total_inactive_file") : U64(stats, "inactive_file");
        var used = cache < usage ? usage - cache : usage;

        // Containers on the host's network (or none) have no "networks" section.
        NetCounters? network = null;
        if (Get(root, "networks") is { ValueKind: JsonValueKind.Object } nets)
        {
            ulong rx = 0, tx = 0;
            foreach (var nic in nets.EnumerateObject())
            {
                rx += U64(nic.Value, "rx_bytes");
                tx += U64(nic.Value, "tx_bytes");
            }
            network = new NetCounters(rx, tx);
        }

        // RFC 3339 with nanoseconds; Docker writes the zero time ("0001-01-01T00:00:00Z") when it has none.
        DateTime? readAt = DateTimeOffset.TryParse(Str(root, "read"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var read)
            && read.Year > 1 ? read.UtcDateTime : null;
        return new StatsReading(cpu, used, U64(memory, "limit"), network, readAt);
    }

    /// <summary>One line of the event stream, or null for one the Health page doesn't mark (or can't read).</summary>
    internal static ContainerEvent? ParseEvent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Str(root, "Type") is { Length: > 0 } type && type != "container") return null;
            var action = Str(root, "Action") is { Length: > 0 } a ? a : Str(root, "status");
            var actor = Get(root, "Actor");
            var attributes = Get(actor, "Attributes");
            var id = Str(actor, "ID") is { Length: > 0 } i ? i : Str(root, "id");
            var name = Str(attributes, "name");
            // A container's labels come as attributes: Gitberth's own short-lived helpers are nobody's history.
            if (Get(attributes, HostScanHelper.Label).ValueKind == JsonValueKind.String) return null;
            // Usually "health_status: unhealthy"; the status can also come as an attribute of its own.
            if (action == "health_status" && Str(attributes, "health_status") is { Length: > 0 } status) action += ": " + status;
            if (id.Length == 0 || name.Length == 0 || !ContainerEvent.Relevant(action)) return null;

            var nanos = Get(root, "timeNano") is { ValueKind: JsonValueKind.Number } tn && tn.TryGetInt64(out var ns) ? ns
                : Get(root, "time") is { ValueKind: JsonValueKind.Number } ts && ts.TryGetInt64(out var s) ? s * 1_000_000_000 : 0;
            if (nanos <= 0) return null;
            var service = Str(attributes, Compose.ComposeNames.ServiceLabel) is { Length: > 0 } named ? named : null;
            return new ContainerEvent(DateTime.UnixEpoch.AddTicks(nanos / 100), id, name, action,
                Int(attributes, "exitCode"), Int(attributes, "signal"), Int(attributes, ContainerSpecFactory.DeploymentLabel),
                Str(attributes, ContainerSpecFactory.TriggerLabel) is { Length: > 0 } trigger ? trigger : null,
                service is null ? null : Str(attributes, "gitberth.app") is { Length: > 0 } app ? app : null, service);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    internal static DockerHostInfo ParseHostInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cpus = Get(root, "NCPU") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var count) ? count : 0;
        return new DockerHostInfo(Str(root, "Name"), cpus, U64(root, "MemTotal"), Str(root, "ServerVersion"));
    }

    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;

    private static ulong U64(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetUInt64(out var n) ? n : 0;

    private static string Str(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
}
