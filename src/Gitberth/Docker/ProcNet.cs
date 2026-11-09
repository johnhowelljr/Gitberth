using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace Gitberth.Docker;

/// <summary>
/// A socket that holds a port on the host, from /proc/net/tcp, tcp6, udp or udp6: a TCP one that listens, or a UDP one
/// bound to a port (UDP has no listening state, so any bound socket holds its port).
/// </summary>
/// <param name="Protocol">tcp or udp, for both address families.</param>
/// <param name="Address">Where it's bound: 0.0.0.0 and :: are every address. An IPv4 address seen through tcp6 is written as IPv4.</param>
public sealed record ProcNetSocket(string Protocol, string Address, int Port)
{
    /// <summary>Bound to every address. A [::] socket takes the IPv4 port as well, unless it's IPv6-only, which /proc doesn't say.</summary>
    public bool AnyAddress => ProcNet.IsAnyAddress(Address);

    public bool Loopback => ProcNet.IsLoopback(Address);

    /// <summary>Whether a binding of the same port and protocol on <paramref name="hostIp"/> (empty for every address) would clash with it.</summary>
    public bool Blocks(string? hostIp) => ProcNet.SocketBlocks(Address, hostIp);
}

/// <summary>A line of /proc/net/route.</summary>
/// <param name="Flags">RTF_UP is 1, RTF_GATEWAY 2.</param>
public sealed record ProcNetRoute(string Interface, string Destination, string Gateway, string Mask, int Flags, int Metric)
{
    /// <summary>Where traffic for anywhere else goes.</summary>
    public bool IsDefault => Destination == "0.0.0.0" && Mask == "0.0.0.0";

    /// <summary>Through a router, rather than straight onto the interface's own network.</summary>
    public bool ViaGateway => (Flags & 2) != 0;

    /// <summary>Whether an IPv4 address is inside the route's range.</summary>
    public bool Contains(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
        && (ProcNet.ToUInt(ip) & ProcNet.ToUInt(IPAddress.Parse(Mask))) == ProcNet.ToUInt(IPAddress.Parse(Destination));

    /// <summary>The range as people write it: 10.1.1.0/24.</summary>
    public string Cidr => $"{Destination}/{BitOperations.PopCount(ProcNet.ToUInt(IPAddress.Parse(Mask))).ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A line of /proc/net/if_inet6: one IPv6 address of one interface.</summary>
/// <param name="Scope">0 for global, 0x10 for the loopback, 0x20 for link-local.</param>
public sealed record ProcNetInet6(string Address, string Interface, int Scope)
{
    /// <summary>
    /// An address the internet can route to: global scope, and not a unique local address (fc00::/7), which Linux also
    /// reports as global.
    /// </summary>
    public bool Global => Scope == 0 && IPAddress.TryParse(Address, out var ip) && !IPAddress.IsLoopback(ip) && (ip.GetAddressBytes()[0] & 0xFE) != 0xFC;
}

/// <summary>The host's default route: the interface its traffic leaves by, its own address there, and the router's.</summary>
/// <param name="Subnet">The network that address is on, as it's routed straight onto the interface: 10.1.1.0/24.</param>
public sealed record ProcNetDefault(string Interface, string? Address, string? Gateway, string? Subnet = null);

/// <summary>
/// Reads the host's network from the files Linux keeps under /proc: sockets, routes, local addresses and the range of
/// ports it hands out for outgoing connections. Pure: the files come in as text.
/// </summary>
public static class ProcNet
{
    /// <summary>
    /// The sockets holding a port, from /proc/net/tcp or tcp6 (<paramref name="protocol"/> tcp: listening ones, state 0A)
    /// or udp or udp6 (every bound one). Addresses are hex in the host's byte order, ports plain hex.
    /// </summary>
    public static List<ProcNetSocket> ParseSockets(string text, string protocol)
    {
        var sockets = new List<ProcNetSocket>();
        foreach (var line in Lines(text))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "0: 00000000:1F90 00000000:0000 0A ..."; the header starts with "sl".
            if (parts.Length < 4 || !parts[0].EndsWith(':')) continue;
            if (protocol == "tcp" && parts[3] != "0A") continue;
            var colon = parts[1].IndexOf(':');
            if (colon < 0 || DecodeAddress(parts[1][..colon]) is not { } address
                || !int.TryParse(parts[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port) || port == 0) continue;
            var socket = new ProcNetSocket(protocol, address, port);
            if (!sockets.Contains(socket)) sockets.Add(socket);
        }
        return sockets;
    }

    /// <summary>/proc/sys/net/ipv4/ip_local_port_range, "32768 60999": the ports Linux hands out at random.</summary>
    public static (int From, int To)? ParsePortRange(string text)
    {
        var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var from)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var to) && from <= to
            ? (from, to)
            : null;
    }

    /// <summary>/proc/net/route: destinations, gateways and masks as little-endian hex.</summary>
    public static List<ProcNetRoute> ParseRoutes(string text)
    {
        var routes = new List<ProcNetRoute>();
        foreach (var line in Lines(text))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[0] == "Iface") continue;
            if (DecodeAddress(parts[1]) is not { } destination || DecodeAddress(parts[2]) is not { } gateway || DecodeAddress(parts[7]) is not { } mask
                || !int.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags)
                || !int.TryParse(parts[6], CultureInfo.InvariantCulture, out var metric)) continue;
            routes.Add(new ProcNetRoute(parts[0], destination, gateway, mask, flags, metric));
        }
        return routes;
    }

    /// <summary>The host's own IPv4 addresses, from /proc/net/fib_trie: the leaves marked "/32 host LOCAL".</summary>
    public static List<string> ParseLocalAddresses(string fibTrie)
    {
        var addresses = new List<string>();
        string? leaf = null;
        foreach (var raw in Lines(fibTrie))
        {
            var line = raw.Trim();
            if (line.StartsWith("|-- ", StringComparison.Ordinal)) leaf = line[4..].Trim();
            else if (line.StartsWith("+-- ", StringComparison.Ordinal) || line.EndsWith(':')) leaf = null;
            else if (leaf is not null && line.StartsWith("/32 host LOCAL", StringComparison.Ordinal) && !addresses.Contains(leaf)) addresses.Add(leaf);
        }
        return addresses;
    }

    /// <summary>/proc/net/if_inet6: each IPv6 address (32 hex digits, network order) with its interface and scope.</summary>
    public static List<ProcNetInet6> ParseIfInet6(string text)
    {
        var addresses = new List<ProcNetInet6>();
        foreach (var line in Lines(text))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || parts[0].Length != 32
                || !int.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scope)) continue;
            try
            {
                addresses.Add(new ProcNetInet6(new IPAddress(Convert.FromHexString(parts[0])).ToString(), parts[5], scope));
            }
            catch (FormatException)
            {
            }
        }
        return addresses;
    }

    /// <summary>
    /// The default route's interface and router, and the host's own address on that interface: the one of its local
    /// addresses inside a range routed straight onto the interface, which is the network it's on. The route with the
    /// lowest metric wins.
    /// </summary>
    public static ProcNetDefault? DefaultRoute(IReadOnlyList<ProcNetRoute> routes, IReadOnlyList<string> localAddresses)
    {
        var route = routes.Where(r => r.IsDefault && (r.Flags & 1) != 0).OrderBy(r => r.Metric).FirstOrDefault();
        if (route is null) return null;
        var ranges = routes.Where(r => r.Interface == route.Interface && !r.IsDefault && !r.ViaGateway).ToList();
        var address = localAddresses.FirstOrDefault(a => ranges.Any(r => r.Contains(a)));
        var subnet = address is null ? null : ranges.Where(r => r.Contains(address)).OrderBy(r => r.Metric).FirstOrDefault()?.Cidr;
        return new ProcNetDefault(route.Interface, address, route.Gateway == "0.0.0.0" ? null : route.Gateway, subnet);
    }

    /// <summary>Every address: empty, 0.0.0.0 or ::.</summary>
    public static bool IsAnyAddress(string? ip) => string.IsNullOrEmpty(ip) || ip is "0.0.0.0" or "::" or "[::]";

    public static bool IsLoopback(string? ip) => ip is not null && IPAddress.TryParse(ip.Trim('[', ']'), out var address) && IPAddress.IsLoopback(Unmapped(address));

    /// <summary>
    /// Whether two bindings of one port and protocol clash, as Docker decides it between its own bindings: one is on every
    /// address (empty), one is on every address of the other's family (0.0.0.0 for IPv4, :: for IPv6), or both are on the
    /// same address. 127.0.0.1 and 0.0.0.0 clash; 127.0.0.1 and 10.1.1.80 don't, nor 127.0.0.1 and ::1, nor 0.0.0.0 and ::
    /// (Docker publishes every address as exactly those two). An address that isn't known (null) counts as every address.
    /// </summary>
    public static bool Overlap(string? a, string? b) => Covers(a, b) || Covers(b, a);

    /// <summary>Whether a binding on <paramref name="outer"/> takes every address a binding on <paramref name="inner"/> would.</summary>
    public static bool Covers(string? outer, string? inner)
    {
        if (string.IsNullOrEmpty(outer)) return true;
        if (string.IsNullOrEmpty(inner)) return false;
        if (!IPAddress.TryParse(outer.Trim('[', ']'), out var o) || !IPAddress.TryParse(inner.Trim('[', ']'), out var i))
            return string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase);
        o = Unmapped(o);
        i = Unmapped(i);
        if (o.Equals(IPAddress.Any)) return i.AddressFamily == AddressFamily.InterNetwork;
        if (o.Equals(IPAddress.IPv6Any)) return i.AddressFamily == AddressFamily.InterNetworkV6;
        return o.Equals(i);
    }

    /// <summary>
    /// Whether a socket a program on the host holds (read from /proc) clashes with a binding of its port on
    /// <paramref name="hostIp"/>. As <see cref="Overlap"/>, except that a socket on [::] may be dual-stack, which /proc
    /// doesn't say: it may hold the IPv4 port too, so it counts as clashing with every binding.
    /// </summary>
    public static bool SocketBlocks(string? socket, string? hostIp) => IsIpv6Any(socket) || Overlap(socket, hostIp);

    /// <summary>Every IPv6 address: :: or [::].</summary>
    public static bool IsIpv6Any(string? ip) => ip is "::" or "[::]";

    internal static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>
    /// An address as /proc writes it: 8 or 32 hex digits, each 32-bit word in the host's byte order (little-endian on x86
    /// and ARM), so each word's bytes come out reversed. IPv4 seen through an IPv6 socket comes back as IPv4.
    /// </summary>
    internal static string? DecodeAddress(string hex)
    {
        if (hex.Length is not (8 or 32)) return null;
        var bytes = new byte[hex.Length / 2];
        for (var w = 0; w < hex.Length / 8; w++)
        {
            if (!uint.TryParse(hex.AsSpan(w * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word)) return null;
            bytes[w * 4] = (byte)word;
            bytes[w * 4 + 1] = (byte)(word >> 8);
            bytes[w * 4 + 2] = (byte)(word >> 16);
            bytes[w * 4 + 3] = (byte)(word >> 24);
        }
        return Unmapped(new IPAddress(bytes)).ToString();
    }

    private static IPAddress Unmapped(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0);
}
