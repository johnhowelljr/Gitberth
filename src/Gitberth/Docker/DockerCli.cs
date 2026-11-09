using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitberth.Docker;

public sealed record ContainerSummary(string Name, string State, string Status, string Image, string Ports, IReadOnlyDictionary<string, string> Labels, string Command = "", string Id = "");

/// <summary>A port a container publishes, as it was created with; HostPort is null for one Docker picks.</summary>
public sealed record PortBinding(string? HostIp, int? HostPort, int ContainerPort, string Protocol);

/// <param name="Name">The volume's name, for a named volume.</param>
/// <param name="Source">The volume's name, or the host folder of a bind mount.</param>
public sealed record ContainerMount(string Type, string? Name, string Source, string Destination, bool ReadOnly);

public sealed record ContainerNetwork(string Name, IReadOnlyList<string> Aliases, string? IpAddress);

/// <summary>A port the container listens on, from its image's EXPOSE or its own settings; published or not.</summary>
public sealed record ExposedPort(int Port, string Protocol);

/// <summary>A container as `docker container inspect` describes it: enough to show it, and to create it again.</summary>
public sealed record ContainerDetails(
    string Id,
    string Name,
    string Image,
    string ImageId,
    DateTime? Created,
    string Status,
    bool Running,
    int ExitCode,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Health,
    bool OomKilled,
    int RestartCount,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    string? WorkingDir,
    string? User,
    IReadOnlyList<KeyValuePair<string, string>> Env,
    IReadOnlyDictionary<string, string> Labels,
    string RestartPolicy,
    IReadOnlyList<PortBinding> Ports,
    IReadOnlyList<ContainerMount> Mounts,
    IReadOnlyList<ContainerNetwork> Networks,
    string NetworkMode,
    long MemoryLimit,
    double Cpus,
    IReadOnlyList<ExposedPort> Exposed);

/// <summary>A port a running container publishes on the host.</summary>
public sealed record PublishedPort(string? HostIp, int HostPort, int ContainerPort, string Protocol);

public sealed record ContainerState(bool Running, string Status, int ExitCode, int RestartCount, string? Health, bool OomKilled);

public sealed record DockerInfo(string? ServerVersion, string? ClientVersion, string? Error);

/// <summary>
/// Thin wrapper over the docker CLI. The CLI negotiates the Engine API version itself,
/// so Gitberth keeps working across Docker upgrades (a pinned old API version is what broke Watchtower on Docker 29).
/// </summary>
public sealed partial class DockerCli(GitberthOptions options)
{
    private IReadOnlyList<string>? _logOptions;

    public string Network => options.Network;

    /// <summary>Stands in for the docker CLI in tests.</summary>
    internal Func<IReadOnlyList<string>, ProcessOptions?, CancellationToken, Task<ProcessResult>>? Runner { get; init; }

    /// <summary>
    /// Log rotation flags for new containers. Only the json-file driver needs them (it never rotates on its own);
    /// other drivers (local, journald, ...) rotate by themselves and reject these options.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLogOptionsAsync(CancellationToken ct = default)
    {
        if (_logOptions is not null) return _logOptions;
        var r = await RunAsync(["info", "--format", "{{.LoggingDriver}}"], null, ct);
        if (!r.Ok) return [];
        _logOptions = r.StdOut.Trim() == "json-file" ? ["max-size=10m", "max-file=3"] : [];
        return _logOptions;
    }

    public async Task<ProcessResult> RunAsync(IEnumerable<string> args, ProcessOptions? processOptions = null, CancellationToken ct = default)
    {
        if (Runner is not null) return await Runner([.. args], processOptions, ct);
        try
        {
            return await ProcessRunner.RunAsync(options.DockerPath, args, processOptions, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", $"The docker CLI wasn't found ('{options.DockerPath}').");
        }
    }

    public async Task<DockerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(["version", "--format", "{{json .}}"], null, ct);
        string? client = null, server = null;
        var json = r.StdOut.Trim();
        if (json.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Client", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("Version", out var cv)) client = cv.GetString();
                if (doc.RootElement.TryGetProperty("Server", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("Version", out var sv)) server = sv.GetString();
            }
            catch (JsonException) { }
        }
        var error = server is null ? FirstLine(r.ErrorText) ?? "Docker daemon not reachable" : null;
        return new DockerInfo(server, client, error);
    }

    public async Task EnsureNetworkAsync(CancellationToken ct = default)
    {
        if ((await RunAsync(["network", "inspect", "--format", "{{.Name}}", Network], null, ct)).Ok) return;
        var create = await RunAsync(["network", "create", "--label", "gitberth.managed=true", Network], null, ct);
        if (!create.Ok && !create.ErrorText.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Couldn't create the Docker network: " + create.ErrorText);
    }

    /// <summary>Attaches a container (normally Gitberth itself) to the shared network so it can reach apps by name.</summary>
    public async Task<string?> ConnectToNetworkAsync(string container, CancellationToken ct = default)
    {
        var r = await RunAsync(ConnectArgs(Network, container, await ServerApiVersionAsync(ct)), null, ct);
        return r.Ok || r.ErrorText.Contains("already exists", StringComparison.OrdinalIgnoreCase) ? null : r.ErrorText;
    }

    /// <summary>The Engine API version that brought gateway priorities (Docker 28).</summary>
    internal static readonly Version GwPriorityApi = new(1, 48);

    /// <summary>
    /// `docker network connect` for joining a network only to be reached by name. At gateway priority -1 the network never
    /// becomes the container's gateway, so its published ports and its way out stay where they are. Docker before 28 has
    /// no priorities, and its CLI refuses the flag, so joins there go ahead without one.
    /// </summary>
    internal static List<string> ConnectArgs(string network, string container, Version? api) =>
        api is not null && api >= GwPriorityApi
            ? ["network", "connect", "--gw-priority", "-1", network, container]
            : ["network", "connect", network, container];

    private Version? _serverApi;

    /// <summary>The Engine API version the daemon speaks, read once; null while it can't be read.</summary>
    public async Task<Version?> ServerApiVersionAsync(CancellationToken ct = default)
    {
        if (_serverApi is not null) return _serverApi;
        var r = await RunAsync(["version", "--format", "{{.Server.APIVersion}}"], null, ct);
        return r.Ok && Version.TryParse(r.StdOut.Trim(), out var api) ? _serverApi = api : null;
    }

    /// <summary>
    /// The running containers on Gitberth's network, by name and alias, with their addresses: the names an app reaches
    /// other containers by. Docker's own short-ID aliases are left out, since they'd change with every redeploy.
    /// </summary>
    public async Task<List<(string Host, string Ip)>> NetworkHostsAsync(CancellationToken ct = default)
    {
        var ps = await RunAsync(["ps", "-q", "--no-trunc", "--filter", $"network={Network}"], null, ct);
        var ids = ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!ps.Ok || ids.Length == 0) return [];
        // A container that stops in between makes inspect fail, but it still prints the others.
        var r = await RunAsync(["container", "inspect", "--format", NetworkFormat, .. ids], null, ct);
        return ParseNetworkHostLines(r.StdOut, Network);
    }

    /// <summary>
    /// Only a container's ID, name and networks, on one line: never its settings, which can hold secrets such as a tunnel
    /// token or a database password.
    /// </summary>
    private const string NetworkFormat = """{"Id":{{json .Id}},"Name":{{json .Name}},"NetworkSettings":{"Networks":{{json .NetworkSettings.Networks}}}}""";

    /// <summary>The output of `docker container inspect --format` with <see cref="NetworkFormat"/>: a JSON object per line.</summary>
    internal static List<(string Host, string Ip)> ParseNetworkHostLines(string output, string network) =>
        ParseNetworkHosts("[" + string.Join(",", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith('{'))) + "]", network);

    internal static List<(string Host, string Ip)> ParseNetworkHosts(string inspectJson, string network)
    {
        var hosts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (!c.TryGetProperty("NetworkSettings", out var settings) || !settings.TryGetProperty("Networks", out var networks)
                    || networks.ValueKind != JsonValueKind.Object || !networks.TryGetProperty(network, out var endpoint)) continue;
                var ip = Str(endpoint, "IPAddress");
                if (ip.Length == 0) continue;
                var id = Str(c, "Id");
                var names = new List<string> { Str(c, "Name").TrimStart('/') };
                if (endpoint.TryGetProperty("Aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                    names.AddRange(aliases.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));
                foreach (var name in names)
                    if (HostName().IsMatch(name) && name != "localhost" && !(name.Length >= 12 && id.StartsWith(name, StringComparison.Ordinal)))
                        hosts.TryAdd(name, ip);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return hosts.Select(h => (h.Key, h.Value)).ToList();
    }

    public async Task<(List<ContainerSummary> Containers, string? Error)> ListContainersAsync(bool managedOnly, CancellationToken ct = default)
    {
        var args = new List<string> { "ps", "-a", "--no-trunc", "--format", "{{json .}}" };
        if (managedOnly) args.InsertRange(2, ["--filter", "label=gitberth.managed=true"]);
        var r = await RunAsync(args, null, ct);
        var list = new List<ContainerSummary>();
        if (!r.Ok) return (list, FirstLine(r.ErrorText) ?? "docker ps failed");
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                list.Add(new ContainerSummary(
                    Str(e, "Names").Split(',')[0],
                    Str(e, "State"),
                    Str(e, "Status"),
                    Str(e, "Image"),
                    Str(e, "Ports"),
                    ParseLabels(Str(e, "Labels")),
                    // Only the program: the arguments can hold secrets, such as a tunnel token.
                    Str(e, "Command").Trim('"', ' ').Split(' ', 2)[0],
                    Str(e, "ID")));
            }
            catch (JsonException) { }
        }
        return (list, null);
    }

    /// <summary>The named containers in full, in one call; ones that don't exist are left out.</summary>
    public async Task<List<ContainerDetails>> InspectContainersAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return [];
        // A missing name fails the call, but the others are still printed.
        var r = await RunAsync(["container", "inspect", .. names], null, ct);
        return r.StdOut.TrimStart().StartsWith('[') ? ParseContainerDetails(r.StdOut) : [];
    }

    internal static List<ContainerDetails> ParseContainerDetails(string json)
    {
        var result = new List<ContainerDetails>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var state = Get(c, "State");
                var config = Get(c, "Config");
                var host = Get(c, "HostConfig");
                var env = Strings(Get(config, "Env"))
                    .Select(e => e.Split('=', 2))
                    .Select(kv => new KeyValuePair<string, string>(kv[0], kv.Length > 1 ? kv[1] : ""))
                    .ToList();

                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Get(config, "Labels") is { ValueKind: JsonValueKind.Object } l)
                    foreach (var label in l.EnumerateObject()) labels[label.Name] = label.Value.GetString() ?? "";

                // The ports it was created with, so a stopped container still shows them.
                var ports = new List<PortBinding>();
                if (Get(host, "PortBindings") is { ValueKind: JsonValueKind.Object } bindings)
                    foreach (var binding in bindings.EnumerateObject())
                    {
                        var (containerPort, protocol) = SplitPort(binding.Name);
                        if (binding.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (var b in binding.Value.EnumerateArray())
                            ports.Add(new PortBinding(Str(b, "HostIp") is { Length: > 0 } ip ? ip : null,
                                int.TryParse(Str(b, "HostPort"), out var hp) ? hp : null, containerPort, protocol));
                    }

                var mounts = new List<ContainerMount>();
                if (Get(c, "Mounts") is { ValueKind: JsonValueKind.Array } m)
                    foreach (var mount in m.EnumerateArray())
                    {
                        var type = Str(mount, "Type");
                        var name = Str(mount, "Name");
                        mounts.Add(new ContainerMount(type, type == "volume" && name.Length > 0 ? name : null,
                            type == "volume" && name.Length > 0 ? name : Str(mount, "Source"), Str(mount, "Destination"),
                            Get(mount, "RW") is { ValueKind: JsonValueKind.False }));
                    }

                var networks = new List<ContainerNetwork>();
                if (Get(Get(c, "NetworkSettings"), "Networks") is { ValueKind: JsonValueKind.Object } nets)
                    foreach (var net in nets.EnumerateObject())
                        networks.Add(new ContainerNetwork(net.Name, Strings(Get(net.Value, "Aliases")).ToList(),
                            Str(net.Value, "IPAddress") is { Length: > 0 } ip ? ip : null));

                var exposed = new List<ExposedPort>();
                if (Get(config, "ExposedPorts") is { ValueKind: JsonValueKind.Object } ex)
                    foreach (var port in ex.EnumerateObject())
                    {
                        var (number, protocol) = SplitPort(port.Name);
                        if (number > 0) exposed.Add(new ExposedPort(number, protocol));
                    }

                var restart = Get(host, "RestartPolicy");
                var restartName = Str(restart, "Name");
                var maxRetries = Get(restart, "MaximumRetryCount") is { ValueKind: JsonValueKind.Number } mr ? mr.GetInt32() : 0;
                result.Add(new ContainerDetails(
                    Str(c, "Id"),
                    Str(c, "Name").TrimStart('/'),
                    Str(config, "Image"),
                    Str(c, "Image"),
                    Time(Str(c, "Created")),
                    Str(state, "Status"),
                    Get(state, "Running") is { ValueKind: JsonValueKind.True },
                    Get(state, "ExitCode") is { ValueKind: JsonValueKind.Number } ec ? ec.GetInt32() : 0,
                    Time(Str(state, "StartedAt")),
                    Time(Str(state, "FinishedAt")),
                    Str(Get(state, "Health"), "Status") is { Length: > 0 } health ? health : null,
                    Get(state, "OOMKilled") is { ValueKind: JsonValueKind.True },
                    Get(c, "RestartCount") is { ValueKind: JsonValueKind.Number } rc ? rc.GetInt32() : 0,
                    Strings(Get(config, "Entrypoint")).ToList(),
                    Strings(Get(config, "Cmd")).ToList(),
                    Str(config, "WorkingDir") is { Length: > 0 } wd ? wd : null,
                    Str(config, "User") is { Length: > 0 } user ? user : null,
                    env,
                    labels,
                    restartName is "" or "no" ? "no" : restartName == "on-failure" && maxRetries > 0 ? $"on-failure:{maxRetries}" : restartName,
                    ports,
                    mounts,
                    networks,
                    Str(host, "NetworkMode"),
                    Get(host, "Memory") is { ValueKind: JsonValueKind.Number } mem ? mem.GetInt64() : 0,
                    Get(host, "NanoCpus") is { ValueKind: JsonValueKind.Number } nano ? nano.GetInt64() / 1e9 : 0,
                    exposed));
            }
        }
        catch (JsonException)
        {
        }
        return result;

        static JsonElement Get(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

        // Safe on a missing section, unlike the class's Str.
        static string Str(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

        static IEnumerable<string> Strings(JsonElement e) =>
            e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "") : [];

        // Docker's zero time ("0001-01-01T00:00:00Z") is "never".
        static DateTime? Time(string s) =>
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year > 1 ? t.UtcDateTime : null;

        static (int Port, string Protocol) SplitPort(string key)
        {
            var parts = key.Split('/', 2);
            return (int.TryParse(parts[0], out var p) ? p : 0, parts.Length > 1 ? parts[1] : "tcp");
        }
    }

    public async Task<ContainerState?> InspectStateAsync(string name, CancellationToken ct = default)
    {
        var r = await RunAsync(["container", "inspect", name], null, ct);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var c = doc.RootElement[0];
            var s = c.GetProperty("State");
            string? health = s.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.Object && h.TryGetProperty("Status", out var hs) ? hs.GetString() : null;
            return new ContainerState(
                s.GetProperty("Running").GetBoolean(),
                s.GetProperty("Status").GetString() ?? "",
                s.GetProperty("ExitCode").GetInt32(),
                c.TryGetProperty("RestartCount", out var rc) ? rc.GetInt32() : 0,
                health,
                s.TryGetProperty("OOMKilled", out var oom) && oom.GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>When a running container last started, in UTC; null when it isn't running or doesn't exist.</summary>
    public async Task<DateTime?> StartedAtAsync(string name, CancellationToken ct = default)
    {
        var r = await RunAsync(["container", "inspect", "--format", "{{.State.Running}} {{.State.StartedAt}}", name], null, ct);
        return r.Ok ? ParseStartedAt(r.StdOut) : null;
    }

    /// <summary>"true 2026-11-04T10:00:00.123456789Z" gives that time, to the second; a container that isn't running gives null.</summary>
    internal static DateTime? ParseStartedAt(string output)
    {
        var parts = output.Trim().Split(' ', 2);
        if (parts is not ["true", var at] || at.Length < 19) return null;
        return DateTime.TryParseExact(at[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started) ? started : null;
    }

    public async Task<bool> ContainerExistsAsync(string name, CancellationToken ct = default) =>
        (await RunAsync(["container", "inspect", "--format", "{{.Id}}", name], null, ct)).Ok;

    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct = default) =>
        (await RunAsync(["image", "inspect", "--format", "{{.Id}}", image], null, ct)).Ok;

    /// <summary>A label on an image, or null when it's missing (or the image is).</summary>
    public async Task<string?> ImageLabelAsync(string image, string label, CancellationToken ct = default)
    {
        var r = await RunAsync(["image", "inspect", "--format", $"{{{{index .Config.Labels \"{label}\"}}}}", image], null, ct);
        var value = r.StdOut.Trim();
        return r.Ok && value.Length > 0 && value != "<no value>" ? value : null;
    }

    public Task<ProcessResult> UpdateRestartPolicyAsync(string name, string policy, CancellationToken ct = default) =>
        RunAsync(["update", "--restart", policy, name], null, ct);

    /// <summary>Gives a container that's on Gitberth's network an alias there: it's taken off the network and put back with it.</summary>
    public async Task<string?> AddNetworkAliasAsync(string container, string alias, CancellationToken ct = default)
    {
        await RunAsync(["network", "disconnect", Network, container], null, ct);
        var r = await RunAsync(["network", "connect", "--alias", alias, Network, container], null, ct);
        return r.Ok ? null : r.ErrorText;
    }

    /// <summary>All of an image's labels; empty when it has none or the image is gone.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ImageLabelsAsync(string image, CancellationToken ct = default)
    {
        var r = await RunAsync(["image", "inspect", "--format", "{{json .Config.Labels}}", image], null, ct);
        return r.Ok ? ParseLabelsJson(r.StdOut) : new Dictionary<string, string>();
    }

    internal static IReadOnlyDictionary<string, string> ParseLabelsJson(string json)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var label in doc.RootElement.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.String))
                    labels[label.Name] = label.Value.GetString()!;
        }
        catch (JsonException) { }
        return labels;
    }

    public Task<ProcessResult> StartAsync(string name, CancellationToken ct = default) => RunAsync(["start", name], null, ct);

    public Task<ProcessResult> StopAsync(string name, int timeoutSeconds = 20, CancellationToken ct = default) =>
        RunAsync(["stop", "-t", timeoutSeconds.ToString(CultureInfo.InvariantCulture), name], null, ct);

    public Task<ProcessResult> RestartAsync(string name, CancellationToken ct = default) => RunAsync(["restart", "-t", "20", name], null, ct);

    /// <summary>
    /// `docker stop` for a container Gitberth didn't deploy, which waits as long as the container asks (see
    /// <see cref="StandaloneStopSeconds"/>). The call itself waits that long and 15 s more, or ten minutes for one that's
    /// never killed; Docker goes on stopping it after that.
    /// </summary>
    /// <param name="isDatabase">Whether its image (Config.Image) is a database, cache or queue, which gets 60 s when it doesn't say.</param>
    public async Task<ProcessResult> StopStandaloneAsync(string name, Func<string, bool> isDatabase, CancellationToken ct = default) =>
        await TimedAsync("stop", name, isDatabase, ct);

    /// <summary>`docker restart` for a container Gitberth didn't deploy, with the stop <see cref="StopStandaloneAsync"/> gives it.</summary>
    public async Task<ProcessResult> RestartStandaloneAsync(string name, Func<string, bool> isDatabase, CancellationToken ct = default) =>
        await TimedAsync("restart", name, isDatabase, ct);

    /// <summary>
    /// How long `docker stop` waits before it kills a container Gitberth didn't deploy: its own stop timeout when it has
    /// one (docker run --stop-timeout: 0 kills it at once, -1 never does), else 60 s for a database, which a clean
    /// shutdown spares crash recovery, else 20.
    /// </summary>
    internal static int StandaloneStopSeconds(int? own, bool database) => own is { } t ? Math.Max(t, -1) : database ? 60 : 20;

    /// <summary>A container's own stop timeout and the image it was created from; (null, "") when it can't be read.</summary>
    public async Task<(int? StopTimeout, string Image)> StopSettingsAsync(string name, CancellationToken ct = default)
    {
        var r = await RunAsync(["container", "inspect", "--format", "{{json .Config.StopTimeout}} {{json .Config.Image}}", name], null, ct);
        return r.Ok ? ParseStopSettings(r.StdOut) : (null, "");
    }

    /// <summary>"60 \"postgres:18\"" gives (60, postgres:18); "null …" is a container without a stop timeout of its own.</summary>
    internal static (int? StopTimeout, string Image) ParseStopSettings(string output)
    {
        var parts = output.Trim().Split(' ', 2);
        int? timeout = int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t) ? t : null;
        var image = "";
        if (parts.Length == 2)
        {
            try
            {
                image = JsonSerializer.Deserialize<string>(parts[1]) ?? "";
            }
            catch (JsonException)
            {
            }
        }
        return (timeout, image);
    }

    private async Task<ProcessResult> TimedAsync(string command, string name, Func<string, bool> isDatabase, CancellationToken ct)
    {
        var (own, image) = await StopSettingsAsync(name, ct);
        var seconds = StandaloneStopSeconds(own, isDatabase(image));
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(DockerApi.StopWait(seconds));
        try
        {
            return await RunAsync([command, "-t", seconds.ToString(CultureInfo.InvariantCulture), name], null, cap.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProcessResult(-1, "", $"Docker is still stopping {name}: it waits for the program to end, as its stop timeout asks.");
        }
    }

    public Task<ProcessResult> RemoveAsync(string name, CancellationToken ct = default) => RunAsync(["rm", "-f", name], null, ct);

    public Task<ProcessResult> RenameAsync(string from, string to, CancellationToken ct = default) => RunAsync(["rename", from, to], null, ct);

    public Task<ProcessResult> LogsTailAsync(string name, int lines, CancellationToken ct = default) =>
        RunAsync(["logs", "--tail", lines.ToString(CultureInfo.InvariantCulture), name], null, ct);

    public async Task<List<string>> ListImageTagsAsync(string repository, CancellationToken ct = default)
    {
        var r = await RunAsync(["image", "ls", repository, "--format", "{{.Repository}}:{{.Tag}}"], null, ct);
        if (!r.Ok) return [];
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.EndsWith(":<none>", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    public Task<ProcessResult> RemoveImageAsync(string image, CancellationToken ct = default) => RunAsync(["image", "rm", image], null, ct);

    public Task<ProcessResult> PruneDanglingImagesAsync(string slug, CancellationToken ct = default) =>
        RunAsync(["image", "prune", "-f", "--filter", $"label=gitberth.app={slug}"], null, ct);

    public Task<ProcessResult> RemoveVolumeAsync(string name, CancellationToken ct = default) => RunAsync(["volume", "rm", name], null, ct);

    /// <summary>
    /// The bindings in a `docker ps` Ports column ("0.0.0.0:3001->3000/tcp, [::]:3001->3000/tcp, 9000/udp"), IPv4
    /// ones first. Consecutive ports show as ranges ("0.0.0.0:3000-3001->3000-3001/tcp"), which are spread out again.
    /// Ports a container exposes without publishing them aren't bindings.
    /// </summary>
    public static List<PublishedPort> ParsePortBindings(string ports)
    {
        var list = new List<PublishedPort>();
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var (host, target) = (part[..arrow], part[(arrow + 2)..]);
            var slash = target.IndexOf('/');
            var colon = host.LastIndexOf(':');
            if (slash < 0 || colon < 0 || PortRange(target[..slash]) is not (var containerFrom, var containerTo) || PortRange(host[(colon + 1)..]) is not (var hostFrom, var hostTo)
                || containerTo - containerFrom != hostTo - hostFrom) continue;
            var ip = host[..colon].Trim('[', ']');
            for (var i = 0; i <= hostTo - hostFrom; i++)
                list.Add(new PublishedPort(ip.Length == 0 ? null : ip, hostFrom + i, containerFrom + i, target[(slash + 1)..]));
        }
        return list.OrderBy(p => p.HostIp?.Contains(':') == true ? 1 : 0).ToList();
    }

    /// <summary>
    /// Every container port a `docker ps` Ports column names, published or only exposed: "0.0.0.0:3001->3000/tcp, 9229/tcp"
    /// gives 3000/tcp and 9229/tcp. Containers on its networks reach it on each of them by name.
    /// </summary>
    public static List<ExposedPort> ParseContainerPorts(string ports)
    {
        var list = new List<ExposedPort>();
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            var target = arrow < 0 ? part : part[(arrow + 2)..];
            var slash = target.IndexOf('/');
            if (slash < 0 || PortRange(target[..slash]) is not (var from, var to)) continue;
            for (var port = from; port <= to; port++) list.Add(new ExposedPort(port, target[(slash + 1)..]));
        }
        return list.Distinct().ToList();
    }

    // "3000", or a run of ports like "3000-3001", as docker ps writes consecutive ones.
    private static (int From, int To)? PortRange(string text)
    {
        var dash = text.IndexOf('-');
        var (from, to) = dash < 0 ? (text, text) : (text[..dash], text[(dash + 1)..]);
        return int.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && int.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= a
            ? (a, b)
            : null;
    }

    /// <summary>
    /// A small text file from inside a container, or null. Copied out as a tar stream, so it works for images without a
    /// shell (cloudflared's has none).
    /// </summary>
    public async Task<string?> ReadContainerFileAsync(string container, string path, CancellationToken ct = default)
    {
        var r = await RunAsync(["cp", $"{container}:{path}", "-"], new ProcessOptions { MaxCapture = 64 * 1024 }, ct);
        return r.Ok ? TarFileText(r.StdOut) : null;
    }

    /// <summary>
    /// The first file in a tar stream: a 512-byte header, its size in octal at offset 124, then the contents. The stream
    /// arrives as text, which can drop a carriage return or two, so a slightly short body is still read.
    /// </summary>
    internal static string? TarFileText(string tar)
    {
        if (tar.Length < 512) return null;
        var size = tar.Substring(124, 12).Trim('\0', ' ');
        try
        {
            var length = Math.Min(Convert.ToInt32(size, 8), tar.Length - 512);
            var text = tar.Substring(512, length).Trim('\0', ' ', '\r', '\n', '\t');
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Host ports from a `docker ps` Ports column, e.g. "0.0.0.0:8101->8080/tcp, [::]:8101->8080/tcp".</summary>
    public static IEnumerable<int> ParsePublishedPorts(string ports) =>
        PublishedPort().Matches(ports).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct();

    internal static IReadOnlyDictionary<string, string> ParseLabels(string labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) result[pair[..eq]] = pair[(eq + 1)..];
        }
        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Split('\n')[0].Trim();

    [GeneratedRegex(@":(\d+)->")]
    private static partial Regex PublishedPort();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex HostName();
}
