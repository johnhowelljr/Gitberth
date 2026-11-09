using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Network;

/// <summary>Who reaches a container port.</summary>
public enum PortAccess
{
    /// <summary>Published on every address of the host, so every device on the LAN reaches it.</summary>
    Lan = 0,
    /// <summary>Published on 127.0.0.1: programs on the host (cloudflared) reach it, the LAN doesn't.</summary>
    HostOnly = 1,
    /// <summary>Not published: containers on the same network reach it by name.</summary>
    Internal = 2,
    /// <summary>Published on one address of the host that isn't loopback: kept as Docker has it, never offered for new ports.</summary>
    Custom = 3,
}

/// <summary>
/// One port of a container as it should be: the container port, its protocol, who reaches it, and for a published one the
/// host port. A container port can have several rules, one per binding.
/// </summary>
/// <param name="HostIp">
/// The exact address to publish on. Null takes the access's own: every address for LAN, 127.0.0.1 for Host only. Rules
/// read from a container carry Docker's value (such as 0.0.0.0), so a port nobody touched is published exactly as before.
/// An address of another access counts for nothing (the row's access was changed), except for Custom, which needs one.
/// </param>
public sealed record PortRule(int ContainerPort, string Protocol, PortAccess Access, int? HostPort = null, string? HostIp = null)
{
    /// <summary>Docker's key for the port: 5432/tcp.</summary>
    public string Key => ContainerPorts.Key(ContainerPort, Protocol);

    public bool Published => Access != PortAccess.Internal;

    /// <summary>What's wrong with the rule, in the words the Ports table uses; null when it's fine.</summary>
    public string? Problem() =>
        ContainerPort is < 1 or > 65535 ? "Each port needs a container port between 1 and 65535."
        : Protocol is not ("tcp" or "udp" or "sctp") ? $"{ContainerPort} needs a protocol: TCP or UDP."
        : Published && HostPort is not (>= 1 and <= 65535) ? "Pick a host port between 1 and 65535."
        : Access == PortAccess.Custom && (HostIp is null || !IPAddress.TryParse(HostIp, out _)) ? $"{Key} needs the address to publish on."
        : null;
}

/// <summary>A container port's binding on the host as Docker has it.</summary>
/// <param name="HostIp">As configured: "" for every address, or 0.0.0.0, 127.0.0.1, another address.</param>
/// <param name="HostPort">The host port; for a random one, the port Docker picked at this start (null while it's stopped).</param>
/// <param name="Random">Docker picks the port at every start: -P, or a port published without its host side.</param>
public sealed record HostBinding(string HostIp, int? HostPort, bool Random = false)
{
    public PortAccess Access => ContainerPorts.AccessOf(HostIp);
}

/// <summary>A port of the container as it stands: exposed, configured to be published, or both.</summary>
public sealed record CurrentPort(int ContainerPort, string Protocol, IReadOnlyList<HostBinding> Bindings)
{
    public string Key => ContainerPorts.Key(ContainerPort, Protocol);
}

/// <summary>Reads a container's ports from its inspect, and writes addresses the way every page shows them.</summary>
public static class ContainerPorts
{
    public static string Key(int port, string protocol) => $"{port.ToString(CultureInfo.InvariantCulture)}/{protocol}";

    /// <summary>"5432/tcp" as (5432, "tcp"); a key without a protocol is TCP.</summary>
    public static (int Port, string Protocol)? ParseKey(string key)
    {
        var slash = key.IndexOf('/');
        var number = slash < 0 ? key : key[..slash];
        var protocol = slash < 0 ? "tcp" : key[(slash + 1)..].ToLowerInvariant();
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? (port, protocol) : null;
    }

    /// <summary>Every address, 0.0.0.0 and :: are the LAN; 127.0.0.1 and ::1 the host only; any other address is Custom.</summary>
    public static PortAccess AccessOf(string? hostIp) => hostIp switch
    {
        null or "" or "0.0.0.0" or "::" => PortAccess.Lan,
        "127.0.0.1" or "::1" => PortAccess.HostOnly,
        _ => PortAccess.Custom,
    };

    /// <summary>
    /// Where a port with this access is published, as Docker writes it, the other way round from <see cref="AccessOf"/>:
    /// "" (every address) for the LAN, 127.0.0.1 for the host only, its own address for Custom; null for an Internal port,
    /// which isn't published.
    /// </summary>
    public static string? BindAddress(PortAccess access, string? hostIp) => access switch
    {
        PortAccess.Lan => "",
        PortAccess.HostOnly => "127.0.0.1",
        PortAccess.Custom => hostIp?.Trim() ?? "",
        _ => null,
    };

    /// <summary>
    /// The container's ports: what its image and settings expose, and what it's configured to publish. For a running
    /// container a random host port reads as the one Docker picked; a stopped one shows its configured bindings.
    /// </summary>
    public static List<CurrentPort> Read(JsonObject inspect)
    {
        var bindings = new SortedDictionary<(int, string), List<HostBinding>>();
        List<HostBinding> For((int, string) key) => bindings.TryGetValue(key, out var list) ? list : bindings[key] = [];

        if (inspect["Config"]?["ExposedPorts"] is JsonObject exposed)
            foreach (var (key, _) in exposed)
                if (ParseKey(key) is { } p) For(p);

        var hostConfig = inspect["HostConfig"] as JsonObject;
        if (hostConfig?["PortBindings"] is JsonObject configured)
            foreach (var (key, value) in configured)
            {
                if (ParseKey(key) is not { } p) continue;
                var list = For(p);
                foreach (var b in (value as JsonArray ?? []).OfType<JsonObject>())
                {
                    var ip = Str(b, "HostIp");
                    // "", "0" or a range: Docker picks the port when the container starts.
                    var fixedPort = int.TryParse(Str(b, "HostPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var hp) && hp > 0 ? hp : (int?)null;
                    list.Add(new HostBinding(ip, fixedPort, fixedPort is null));
                }
            }
        if (hostConfig?["PublishAllPorts"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
            foreach (var (key, list) in bindings)
                if (list.Count == 0) list.Add(new HostBinding("", null, Random: true));

        // A random port as Docker picked it this time, from the live bindings.
        if (Running(inspect) && inspect["NetworkSettings"]?["Ports"] is JsonObject live)
            foreach (var (key, list) in bindings)
            {
                if (list.All(b => !b.Random) || live[Key(key.Item1, key.Item2)] is not JsonArray now) continue;
                var picked = now.OfType<JsonObject>().Select(b => (Ip: Str(b, "HostIp"), Port: int.TryParse(Str(b, "HostPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0))
                    .Where(b => b.Port > 0).ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    if (!list[i].Random) continue;
                    var wanted = AccessOf(list[i].HostIp);
                    var match = picked.FirstOrDefault(b => AccessOf(b.Ip) == wanted && list.All(o => o.HostPort != b.Port));
                    if (match.Port > 0) list[i] = list[i] with { HostPort = match.Port };
                }
            }

        return bindings.Select(b => new CurrentPort(b.Key.Item1, b.Key.Item2, b.Value)).ToList();
    }

    /// <summary>The container's ports as rules: where the Ports table's draft starts, so an untouched row changes nothing.</summary>
    public static List<PortRule> Rules(JsonObject inspect) =>
        Read(inspect).SelectMany(p => p.Bindings.Count == 0
                ? [new PortRule(p.ContainerPort, p.Protocol, PortAccess.Internal)]
                : p.Bindings.Select(b => new PortRule(p.ContainerPort, p.Protocol, b.Access, b.HostPort, b.HostIp)))
            .ToList();

    /// <summary>
    /// Whether two bindings of one host port and protocol clash: one is on every address (""), one is on every address of
    /// the other's family (0.0.0.0 for IPv4, :: for IPv6), or both are on the same address. 127.0.0.1 and ::1 don't clash,
    /// nor do 10.1.1.80 and 127.0.0.1, nor 0.0.0.0 and :: (Docker publishes "" as exactly those two). The host-port map
    /// uses the same rule (<see cref="ProcNet.Overlap"/>).
    /// </summary>
    public static bool Overlap(string a, string b) => ProcNet.Overlap(a, b);

    /// <summary>Whether a binding on <paramref name="outer"/> takes every address a binding on <paramref name="inner"/> would.</summary>
    public static bool Covers(string outer, string inner) => ProcNet.Covers(outer, inner);

    /// <summary>An address and port as people type them: 10.1.1.80:5432, [fd00::5]:5432.</summary>
    public static string Address(string host, int port) =>
        (host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A binding in the dialog's words: "LAN at 10.1.1.80:5432", "Host only at 127.0.0.1:3002", or for a binding on one
    /// address, "LAN (10.1.1.80 only) at 10.1.1.80:5432".
    /// </summary>
    public static string Describe(string hostIp, int? hostPort, string lanAddress, bool random = false)
    {
        var access = AccessOf(hostIp);
        if (hostPort is not { } port) return access == PortAccess.HostOnly ? "Host only, on a random port" : "LAN, on a random port";
        var text = access switch
        {
            PortAccess.Lan => $"LAN at {Address(lanAddress, port)}",
            PortAccess.HostOnly => $"Host only at {Address(hostIp, port)}",
            _ => $"LAN ({hostIp} only) at {Address(hostIp, port)}",
        };
        return random ? text + " (random)" : text;
    }

    internal static bool Running(JsonObject inspect) => inspect["State"]?["Running"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;

    internal static string Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>A whole number, whether it was parsed from Docker's answer or built in code; 0 when there's none.</summary>
    internal static long Number(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? v.TryGetValue<long>(out var n) ? n : long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            : 0;
}
