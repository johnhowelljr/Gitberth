using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitberth.Docker;

/// <summary>
/// A port in Docker's list of containers: published on <paramref name="Ip"/>:<paramref name="PublicPort"/> when it has a
/// public port, else one the running container only exposes.
/// </summary>
public sealed record NetworkPort(string? Ip, int PrivatePort, int? PublicPort, string Type);

/// <summary>A container's place on one network, as Docker's list of containers gives it.</summary>
/// <param name="IpAddress">Empty while the container is stopped.</param>
/// <param name="Aliases">Only what was asked for; Docker's list leaves them out (null), so they come from an inspect.</param>
/// <param name="DnsNames">Every name the network's DNS answers for it (name, aliases, short ID); also only in an inspect.</param>
/// <param name="GwPriority">Which network carries its published ports and its way out: the highest (Docker 28 on).</param>
/// <param name="FixedAddress">
/// The IPv4 address it keeps there (IPAMConfig.IPv4Address), stopped or not: Docker asks for it again at every start. Null
/// for one Docker picks.
/// </param>
public sealed record NetworkEndpoint(string Network, string? IpAddress, string? MacAddress, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> DnsNames, int GwPriority, string? FixedAddress = null);

/// <summary>
/// A container as Docker's list describes it (GET containers/json?all=true): enough for its addresses, host ports and
/// networks without an inspect each. A stopped or restarting container has no ports in the list; what it was created
/// with is in <see cref="NetworkPortConfig"/>.
/// </summary>
/// <param name="Command">Only the program: the arguments can hold secrets, such as a tunnel token.</param>
public sealed record NetworkContainer(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    string Command,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<NetworkPort> Ports,
    string? NetworkMode,
    IReadOnlyList<NetworkEndpoint> Networks)
{
    /// <summary>
    /// Running or paused: its ports are bound, and the list shows them. Not restarting: between one exit and the next
    /// start Docker lets go of a restarting container's ports and lists none, so it's read like a stopped one.
    /// </summary>
    public bool Live => State is "running" or "paused";

    public bool Running => State == "running";

    internal static List<NetworkContainer> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<NetworkContainer>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            // Legacy links add aliases like "/web/db"; the container's own name is the one without a second slash.
            var names = Strings(Get(c, "Names")).Select(x => x.TrimStart('/')).Where(x => x.Length > 0).ToList();
            var id = Str(c, "Id");
            var name = names.FirstOrDefault(x => !x.Contains('/')) ?? names.FirstOrDefault() ?? id[..Math.Min(12, id.Length)];

            var ports = new List<NetworkPort>();
            if (Get(c, "Ports") is { ValueKind: JsonValueKind.Array } list)
                foreach (var p in list.EnumerateArray())
                {
                    if (Int(p, "PrivatePort") is not int privatePort || privatePort <= 0) continue;
                    var published = Int(p, "PublicPort") is int pub && pub > 0 ? pub : (int?)null;
                    ports.Add(new NetworkPort(Str(p, "IP") is { Length: > 0 } ip ? ip : null, privatePort, published,
                        Str(p, "Type") is { Length: > 0 } type ? type : "tcp"));
                }

            var networks = new List<NetworkEndpoint>();
            if (Get(Get(c, "NetworkSettings"), "Networks") is { ValueKind: JsonValueKind.Object } nets)
                foreach (var net in nets.EnumerateObject())
                    networks.Add(Endpoint(net.Name, net.Value));

            var networkMode = Str(Get(c, "HostConfig"), "NetworkMode");
            result.Add(new NetworkContainer(id, name, Str(c, "Image"), Str(c, "ImageID"), Str(c, "State"), Str(c, "Status"),
                Str(c, "Command").Trim('"', ' ').Split(' ', 2)[0], StringMap(Get(c, "Labels")), ports,
                networkMode.Length > 0 ? networkMode : null, networks));
        }
        return result;
    }

    internal static NetworkEndpoint Endpoint(string network, JsonElement e) => new(
        network,
        Str(e, "IPAddress") is { Length: > 0 } ip ? ip : null,
        Str(e, "MacAddress") is { Length: > 0 } mac ? mac : null,
        Strings(Get(e, "Aliases")).ToList(),
        Strings(Get(e, "DNSNames")).ToList(),
        Int(e, "GwPriority") ?? 0,
        Str(Get(e, "IPAMConfig"), "IPv4Address") is { Length: > 0 } fixedIp ? fixedIp : null);

    internal static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;

    internal static string Str(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    internal static int? Int(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    internal static IEnumerable<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "") : [];

    internal static Dictionary<string, string> StringMap(JsonElement e)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var label in e.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.String))
                labels[label.Name] = label.Value.GetString()!;
        return labels;
    }
}

/// <summary>A Docker network as GET networks describes it.</summary>
/// <param name="Subnets">Its IPv4 and IPv6 ranges, like 172.21.0.0/16; none for host and none.</param>
/// <param name="Ingress">Docker Swarm's routing-mesh network.</param>
/// <param name="IpRanges">The part of each subnet Docker gives out (--ip-range), where one was set: 10.1.1.224/28.</param>
public sealed record NetworkInfo(string Id, string Name, string Driver, string Scope, bool Internal, bool Ingress,
    IReadOnlyList<string> Subnets, IReadOnlyList<string> Gateways, IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Options, IReadOnlyList<string>? IpRanges = null)
{
    internal static List<NetworkInfo> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(Parse).ToList() : [];
    }

    internal static NetworkInfo Parse(JsonObject network)
    {
        using var doc = JsonDocument.Parse(network.ToJsonString());
        return Parse(doc.RootElement);
    }

    private static NetworkInfo Parse(JsonElement n)
    {
        var config = NetworkContainer.Get(NetworkContainer.Get(n, "IPAM"), "Config");
        var ranges = config.ValueKind == JsonValueKind.Array ? config.EnumerateArray().ToList() : [];
        return new NetworkInfo(
            NetworkContainer.Str(n, "Id"),
            NetworkContainer.Str(n, "Name"),
            NetworkContainer.Str(n, "Driver"),
            NetworkContainer.Str(n, "Scope"),
            NetworkContainer.Get(n, "Internal").ValueKind == JsonValueKind.True,
            NetworkContainer.Get(n, "Ingress").ValueKind == JsonValueKind.True,
            ranges.Select(r => NetworkContainer.Str(r, "Subnet")).Where(s => s.Length > 0).ToList(),
            ranges.Select(r => NetworkContainer.Str(r, "Gateway")).Where(s => s.Length > 0).ToList(),
            NetworkContainer.StringMap(NetworkContainer.Get(n, "Labels")),
            NetworkContainer.StringMap(NetworkContainer.Get(n, "Options")),
            ranges.Select(r => NetworkContainer.Str(r, "IPRange")).Where(s => s.Length > 0).ToList());
    }
}

/// <summary>
/// The ports a container was created with: its bindings (a stopped container's only record of them) and the ports it
/// exposes. They never change for a container, so they're read from its inspect once.
/// </summary>
/// <param name="Bindings">HostConfig.PortBindings. A null HostPort is one Docker picks at every start.</param>
/// <param name="PublishAll">Started with -P: Docker picks a host port for every exposed port, at every start.</param>
public sealed record NetworkPortConfig(IReadOnlyList<PortBinding> Bindings, IReadOnlyList<ExposedPort> Exposed, bool PublishAll)
{
    public static readonly NetworkPortConfig None = new([], [], false);

    public static NetworkPortConfig From(JsonObject inspect)
    {
        var bindings = new List<PortBinding>();
        if (inspect["HostConfig"]?["PortBindings"] is JsonObject configured)
            foreach (var (key, value) in configured)
            {
                var (port, protocol) = SplitPort(key);
                if (port <= 0 || value is not JsonArray list) continue;
                foreach (var b in list.OfType<JsonObject>())
                {
                    var hostIp = b["HostIp"]?.GetValue<string>();
                    var hostPort = int.TryParse(b["HostPort"]?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp) && hp > 0 ? hp : (int?)null;
                    bindings.Add(new PortBinding(string.IsNullOrEmpty(hostIp) ? null : hostIp, hostPort, port, protocol));
                }
            }

        var exposed = new List<ExposedPort>();
        if (inspect["Config"]?["ExposedPorts"] is JsonObject ports)
            foreach (var (key, _) in ports)
            {
                var (port, protocol) = SplitPort(key);
                if (port > 0) exposed.Add(new ExposedPort(port, protocol));
            }

        var publishAll = inspect["HostConfig"]?["PublishAllPorts"] is JsonValue all && all.TryGetValue<bool>(out var p) && p;
        return new NetworkPortConfig(bindings, exposed, publishAll);
    }

    /// <summary>"5432/tcp" gives (5432, "tcp"); a key without a protocol is TCP.</summary>
    internal static (int Port, string Protocol) SplitPort(string key)
    {
        var parts = key.Split('/', 2);
        return (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0, parts.Length > 1 ? parts[1] : "tcp");
    }
}
