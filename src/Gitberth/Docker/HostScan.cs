using System.Globalization;
using System.Text.Json.Nodes;
using Gitberth.Network;

namespace Gitberth.Docker;

/// <summary>
/// The host's own network, read from its /proc: the sockets holding ports, the range Linux hands out for outgoing
/// connections, and the interface, address and router of its default route.
/// </summary>
/// <param name="RandomRange">ip_local_port_range; null when it couldn't be read.</param>
/// <param name="Interface">The default route's interface, like eth0.</param>
/// <param name="Address">The host's own IPv4 address on that interface: its LAN address, as detected.</param>
/// <param name="GlobalIpv6">That interface has an IPv6 address the internet can route to.</param>
/// <param name="Error">Why the host couldn't be read. Nothing else is known then.</param>
/// <param name="LocalAddresses">Every IPv4 address of the host, Docker's bridges and the loopback included; null when unknown.</param>
/// <param name="Subnet">The network the host's address is on, as its route has it (10.1.1.0/24); null when unknown.</param>
public sealed record HostScan(
    DateTime ReadAt,
    IReadOnlyList<ProcNetSocket> Sockets,
    (int From, int To)? RandomRange,
    string? Interface,
    string? Address,
    string? Gateway,
    bool GlobalIpv6,
    string? Error = null,
    IReadOnlyList<string>? LocalAddresses = null,
    string? Subnet = null)
{
    /// <summary>Linux's own default, for when the host's range couldn't be read.</summary>
    public static readonly (int From, int To) DefaultRandomRange = (32768, 60999);

    /// <summary>The range Linux hands out at random: the host's, or Linux's default.</summary>
    public (int From, int To) Random => RandomRange ?? DefaultRandomRange;

    /// <summary>
    /// Whether the LAN's ports go on IPv4 only (0.0.0.0): the host has an IPv6 address the internet can route to, or its
    /// network couldn't be read, so it may have one. A port is never published where the internet may reach it on a guess.
    /// </summary>
    public bool LanIpv4Only => GlobalIpv6 || Error is not null;

    public static HostScan Failed(string why, DateTime? at = null) => new(at ?? DateTime.UtcNow, [], null, null, null, null, false, why);
}

/// <summary>
/// The "ports" helper: a container of Gitberth's own image (nothing to pull) on the host's network, which prints the
/// host's socket tables, routes and addresses and is removed when it exits. It runs read-only, with no capabilities and
/// 32 MiB of memory; /proc's network files are readable by anyone.
/// </summary>
public static class HostScanHelper
{
    /// <summary>
    /// Marks every short-lived container Gitberth runs for itself, with what it's for as the value (ports, probe, check,
    /// lan-check), and the lan-check's own temporary network. Gitberth's lists, counts, health charts and events leave them out.
    /// </summary>
    public const string Label = "gitberth.helper";

    /// <summary>The files the helper prints, in this order.</summary>
    public static readonly IReadOnlyList<string> Files =
    [
        "/proc/net/tcp", "/proc/net/tcp6", "/proc/net/udp", "/proc/net/udp6", "/proc/net/route", "/proc/net/fib_trie",
        "/proc/net/if_inet6", "/proc/sys/net/ipv4/ip_local_port_range",
    ];

    /// <summary>
    /// The most of the helper's output that's kept. Only listening TCP sockets are printed, so a host with thousands of
    /// connections stays far below it; output that's cut short anyway is a failed scan, never a partial one.
    /// </summary>
    public const int MaxOutput = 1024 * 1024;

    /// <summary>`docker ps` for "ports" helpers left behind: created but never started, or stopped and not removed.</summary>
    public static readonly IReadOnlyList<string> LeftoverArgs =
    [
        "ps", "-aq", "--filter", $"label={Label}=ports", "--filter", "status=created", "--filter", "status=exited", "--filter", "status=dead",
    ];

    // Each file after a line naming it, so the output splits back into files; a file that's missing (no IPv6) only
    // leaves its name line. Of the TCP tables only listening sockets (state 0A) are printed: connections never hold a
    // port, and a busy host has tens of thousands. Plain sh, so it needs nothing from the image but a shell.
    private const string Script =
        "for f in \"$@\"; do echo \"==> $f <==\"; case \"$f\" in "
        + "*/tcp|*/tcp6) while read -r sl addr peer state rest; do [ \"$state\" = 0A ] && printf '%s %s %s %s %s\\n' \"$sl\" \"$addr\" \"$peer\" \"$state\" \"$rest\"; done < \"$f\";; "
        + "*) cat \"$f\";; esac; done";

    /// <summary>What every helper's name starts with: gitberth-helper-ports-1a2b3c4d5e6f.</summary>
    public const string NamePrefix = "gitberth-helper-";

    /// <summary>The probe: binds a port change's new host ports for a moment, before anything stops (see <see cref="ProbeBody"/>).</summary>
    public const string ProbeKind = "probe";

    /// <summary>The check: connects to an address and port from the host's own network (see <see cref="CheckBody"/>).</summary>
    public const string CheckKind = "check";

    /// <summary>
    /// The lan-check: finds which LAN addresses something answers on, and whether the router answers, for own LAN
    /// addresses (see <see cref="LanCheckBody"/>).
    /// </summary>
    public const string LanCheckKind = "lan-check";

    /// <summary>
    /// How long, in seconds, the lan-check gives each batch of connects before it cuts them off: a device answers its ARP
    /// within milliseconds, a dozing phone by Linux's third try, a second apart.
    /// </summary>
    public const int LanCheckWait = 3;

    /// <summary>
    /// How many connects the lan-check runs at once. Each is a forked shell of about 200 KB: 51 at once peaked at 13 MB on
    /// CT 101, and 762 at once ran out of the helper's 32 MiB.
    /// </summary>
    public const int LanCheckBatch = 60;

    public static bool IsHelper(IReadOnlyDictionary<string, string> labels) => labels.ContainsKey(Label);

    /// <summary>A name for one run of the helper, so one left behind can be removed by it.</summary>
    public static string NewName() => NewName("ports");

    /// <summary>A name for one run of a helper of this kind (ports, probe or check).</summary>
    public static string NewName(string kind, string prefix = NamePrefix) => $"{prefix}{kind}-{Guid.NewGuid().ToString("N")[..12]}";

    /// <summary>
    /// POST containers/create for the "probe" helper: `sleep 5` on Docker's default bridge, with only the host ports to try
    /// bound, each on its exact address. When Docker starts it they're free; it's removed at once either way.
    /// </summary>
    /// <param name="volumes">The image's VOLUME paths, which get a tmpfs each (see <see cref="HelperBody"/>).</param>
    public static JsonObject ProbeBody(string image, IEnumerable<string> volumes, IReadOnlyList<ProbePort> ports)
    {
        var exposed = new JsonObject();
        var bindings = new JsonObject();
        // The container port doesn't matter: nothing listens. The host port's own number keeps each one apart.
        foreach (var group in ports.GroupBy(p => $"{p.HostPort.ToString(CultureInfo.InvariantCulture)}/{p.Protocol}"))
        {
            exposed[group.Key] = new JsonObject();
            bindings[group.Key] = new JsonArray(group.Select(p => (JsonNode)new JsonObject
            {
                ["HostIp"] = p.HostIp,
                ["HostPort"] = p.HostPort.ToString(CultureInfo.InvariantCulture),
            }).ToArray());
        }
        var body = HelperBody(image, volumes, ProbeKind, "bridge", ["sleep", "5"]);
        body["ExposedPorts"] = exposed;
        ((JsonObject)body["HostConfig"]!)["PortBindings"] = bindings;
        return body;
    }

    /// <summary>
    /// POST containers/create for the "check" helper: on the host's own network, a TCP connect to address:port given 3 s,
    /// the way a device on the LAN connects. It exits 0 when something answered, 1 when nothing did, 124 when it timed out.
    /// </summary>
    public static JsonObject CheckBody(string image, IEnumerable<string> volumes, string address, int port) =>
        HelperBody(image, volumes, CheckKind, "host",
            ["timeout", "3", "bash", "-c", "exec 3<>/dev/tcp/$0/$1", address, port.ToString(CultureInfo.InvariantCulture)]);

    // Tries each address on each port, then prints the ARP table. Pinging would need NET_RAW, which the helper hasn't got,
    // but any connect makes Linux ask for the address's MAC: a device that's there answers that even when every port is
    // closed, and its ARP entry is then complete. Each connect runs in a forked shell, a batch at a time, and is cut off
    // after LanCheckWait seconds. The router is tried on DNS and the web ports first, when there's one to try.
    private static readonly string LanCheckScript =
        "router=$1 ports=${2//,/ }; shift 2; n=0\n"
        + "try() { { exec 3<>\"/dev/tcp/$1/$2\" && echo \"open $1 $2\"; } 2>/dev/null & n=$((n + 1)); }\n"
        + $"settle() {{ sleep {LanCheckWait}; kill $(jobs -p) 2>/dev/null; wait 2>/dev/null; n=0; }}\n"
        + "if [ \"$router\" != - ]; then for p in 53 80 443; do try \"$router\" $p; done; fi\n"
        + $"for ip in \"$@\"; do for p in $ports; do try \"$ip\" $p; done; [ $n -lt {LanCheckBatch} ] || settle; done\n"
        + "settle\n"
        + "echo '==> /proc/net/arp <=='\n"
        + "cat /proc/net/arp\n";

    /// <summary>
    /// POST containers/create for the "lan-check" helper: TCP connects to each of <paramref name="targets"/> on each of
    /// <paramref name="ports"/>, and to the router on 53, 80 and 443, then the ARP table it learnt, for
    /// <see cref="Gitberth.Network.LanScan.Read"/>. On an own-addresses network it sits at <paramref name="address"/>, and
    /// what answers it there answers from the block; on the host's network ("host") it needs no address of its own.
    /// Unlike the other helpers it keeps a log (json-file), which is how its answer is read back.
    /// </summary>
    /// <param name="network">The network it runs on: "host", or an own-addresses network it joins at <paramref name="address"/>.</param>
    /// <param name="router">The router to try as well; null for none.</param>
    public static JsonObject LanCheckBody(string image, IEnumerable<string> volumes, string network, string? address, string? router,
        IEnumerable<int> ports, IEnumerable<string> targets)
    {
        var body = HelperBody(image, volumes, LanCheckKind, network,
            ["bash", "-c", LanCheckScript, "bash", router ?? "-", string.Join(',', ports.Select(p => p.ToString(CultureInfo.InvariantCulture))), .. targets]);
        ((JsonObject)body["HostConfig"]!)["LogConfig"] = new JsonObject { ["Type"] = "json-file" };
        if (address is not null)
            body["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = new JsonObject { [network] = new JsonObject { ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = address } } },
            };
        return body;
    }

    /// <summary>
    /// The probe, check and lan-check helpers in Docker's list (GET containers/json?all=true) made before
    /// <paramref name="before"/>: ones a restart of Gitberth left behind. Ports helpers are the host scan's own to remove.
    /// </summary>
    public static List<string> Leftovers(JsonArray containers, DateTimeOffset before) =>
        containers.OfType<JsonObject>()
            .Where(c => c["Labels"] is JsonObject labels && labels[Label] is JsonValue kind && kind.TryGetValue<string>(out var k) && k is ProbeKind or CheckKind or LanCheckKind)
            .Where(c => c["Created"] is JsonValue created && created.TryGetValue<long>(out var at) && DateTimeOffset.FromUnixTimeSeconds(at) < before)
            .Select(c => c["Id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : "")
            .Where(id => id.Length > 0)
            .ToList();

    /// <summary>
    /// The lan-check's temporary networks in Docker's list (GET networks) made before <paramref name="before"/>: ones a
    /// restart of Gitberth left behind. Docker writes their creation time with nanoseconds.
    /// </summary>
    public static List<string> LeftoverNetworks(JsonArray networks, DateTimeOffset before) =>
        networks.OfType<JsonObject>()
            .Where(n => n["Labels"] is JsonObject labels && labels[Label] is JsonValue kind && kind.TryGetValue<string>(out var k) && k == LanCheckKind)
            .Where(n => n["Created"] is JsonValue created && created.TryGetValue<string>(out var at)
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var made) && made < before)
            .Select(n => n["Id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : "")
            .Where(id => id.Length > 0)
            .ToList();

    // Read-only, without capabilities, 32 MiB of memory, no log and no restart policy, like the ports helper. Each VOLUME
    // of the image gets a tmpfs, so Docker makes no anonymous volume for it, and removing a helper never removes a volume.
    private static JsonObject HelperBody(string image, IEnumerable<string> volumes, string kind, string networkMode, string[] command) => new()
    {
        ["Image"] = image,
        ["Entrypoint"] = new JsonArray(command[..1].Select(c => (JsonNode)c).ToArray()),
        ["Cmd"] = new JsonArray(command[1..].Select(c => (JsonNode)c).ToArray()),
        ["Labels"] = new JsonObject { [Label] = kind },
        ["HostConfig"] = new JsonObject
        {
            ["NetworkMode"] = networkMode,
            ["ReadonlyRootfs"] = true,
            ["CapDrop"] = new JsonArray("ALL"),
            ["Memory"] = 32L * 1024 * 1024,
            ["LogConfig"] = new JsonObject { ["Type"] = "none" },
            ["Tmpfs"] = new JsonObject(volumes.Distinct(StringComparer.Ordinal).Select(v => KeyValuePair.Create(v, (JsonNode?)JsonValue.Create(""))).ToList()),
        },
    };

    /// <summary>
    /// `docker run` for the helper. <paramref name="image"/> is Gitberth's own image ID, so it's the image already on the
    /// host. Its log goes nowhere (the output comes back over the attached stream), so the socket tables don't fill the
    /// daemon's log.
    /// </summary>
    public static List<string> RunArgs(string image, string name) =>
    [
        "run", "--rm", "--network", "host", "--read-only", "--cap-drop", "ALL", "--memory", "32m",
        "--label", $"{Label}=ports", "--name", name, "--log-driver", "none", "--pull", "never",
        "--entrypoint", "sh", image, "-c", Script, "sh", .. Files,
    ];

    /// <summary>
    /// What the helper printed, read at <paramref name="at"/>. Gitberth keeps only the end of a long output, so output that
    /// doesn't start with the first file's line lost its beginning (the listening IPv4 sockets) and isn't read at all.
    /// </summary>
    public static HostScan ReadOutput(string output, DateTime at) =>
        output.StartsWith($"==> {Files[0]} <==", StringComparison.Ordinal)
            ? Read(SplitFiles(output), at)
            : HostScan.Failed("the helper's output was cut short", at);

    /// <summary>The helper's output split back into its files, by the "==> path &lt;==" line before each.</summary>
    public static Dictionary<string, string> SplitFiles(string output)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var text = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("==> ", StringComparison.Ordinal) && trimmed.EndsWith(" <==", StringComparison.Ordinal))
            {
                if (current is not null) files[current] = string.Join('\n', text);
                current = trimmed[4..^4];
                text.Clear();
            }
            else if (current is not null)
            {
                text.Add(trimmed);
            }
        }
        if (current is not null) files[current] = string.Join('\n', text);
        return files;
    }

    /// <summary>What the files say, read at <paramref name="at"/>. A missing file leaves its part unknown.</summary>
    public static HostScan Read(IReadOnlyDictionary<string, string> files, DateTime at)
    {
        string File(string path) => files.TryGetValue(path, out var text) ? text : "";
        if (!files.Keys.Any(k => File(k).Length > 0)) return HostScan.Failed("the host's network files came back empty", at);

        List<ProcNetSocket> sockets =
        [
            .. ProcNet.ParseSockets(File("/proc/net/tcp"), "tcp"),
            .. ProcNet.ParseSockets(File("/proc/net/tcp6"), "tcp"),
            .. ProcNet.ParseSockets(File("/proc/net/udp"), "udp"),
            .. ProcNet.ParseSockets(File("/proc/net/udp6"), "udp"),
        ];
        var local = ProcNet.ParseLocalAddresses(File("/proc/net/fib_trie"));
        var route = ProcNet.DefaultRoute(ProcNet.ParseRoutes(File("/proc/net/route")), local);
        var globalIpv6 = route is not null && ProcNet.ParseIfInet6(File("/proc/net/if_inet6")).Any(a => a.Interface == route.Interface && a.Global);
        return new HostScan(at, sockets.Distinct().ToList(), ProcNet.ParsePortRange(File("/proc/sys/net/ipv4/ip_local_port_range")),
            route?.Interface, route?.Address, route?.Gateway, globalIpv6, LocalAddresses: local.Count > 0 ? local : null, Subnet: route?.Subnet);
    }
}
