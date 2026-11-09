using System.Net;
using Gitberth.Data;
using Gitberth.Network;

namespace Gitberth.Services;

/// <summary>Which field of the Ports table a problem is about, so it shows under that field; Table for the table as a whole.</summary>
public enum PortField { ContainerPort, Protocol, Access, HostPort, Web, Table }

/// <summary>A problem with an app's ports, in the words shown under the field it concerns.</summary>
/// <param name="Row">The row's index in the table; null for one about the table as a whole.</param>
public sealed record PortProblem(int? Row, PortField Field, string Message);

/// <summary>What an app's ports are checked against, besides each other.</summary>
/// <param name="Domains">The app's domains, its main one first. They go to the web port.</param>
/// <param name="TunnelOnHost">cloudflared runs on the host's network, so a domain reaches the app only through a host port.</param>
/// <param name="Uses">Every use of a host port (<see cref="HostPortMap"/>), to name whoever holds one a row asks for.</param>
/// <param name="Self">The app's own container and saved ports, which never count against it; null for a new app.</param>
public sealed record PortContext(IReadOnlyList<string> Domains, bool TunnelOnHost, IReadOnlyList<HostPortUse> Uses, HostPortOwner? Self)
{
    /// <summary>No domains, and no host port known to be taken.</summary>
    public static readonly PortContext None = new([], false, [], null);
}

/// <summary>
/// The rules for an app's ports (5.9), the same on the Network tab and in New app. Pure: the host ports in use and the
/// app's domains come in with the <see cref="PortContext"/>.
/// <list type="bullet">
/// <item>each row has a container port and is TCP or UDP, and no container port and protocol is listed twice;</item>
/// <item>a published row (LAN, Host only, Custom) has a host port that nobody else holds and that isn't listed twice; an
/// Internal row needs none;</item>
/// <item>at most one row is the web port, and it's a TCP one;</item>
/// <item>an app with a domain has a web port, on the host when cloudflared can only reach the app that way.</item>
/// </list>
/// </summary>
public static class PortRules
{
    /// <summary>Every problem with the rows, row by row; empty when they can be saved.</summary>
    public static List<PortProblem> Check(IReadOnlyList<PortRow> rows, PortContext context)
    {
        var problems = new List<PortProblem>();
        var listed = new HashSet<(int, string)>();
        var bound = new List<(int Port, string Protocol, string Address)>();
        int? web = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.ContainerPort is not int port || port is < 1 or > 65535)
            {
                problems.Add(new(i, PortField.ContainerPort, "Each port needs a container port between 1 and 65535."));
                continue;
            }
            var protocol = ProtocolOf(row.Protocol);
            if (protocol is not ("tcp" or "udp"))
            {
                problems.Add(new(i, PortField.Protocol, $"{port} needs a protocol: TCP or UDP."));
                continue;
            }
            if (!listed.Add((port, protocol))) problems.Add(new(i, PortField.ContainerPort, $"{port}/{protocol} is listed twice."));

            if (row.IsWeb)
            {
                if (protocol != "tcp") problems.Add(new(i, PortField.Web, "The web port must be TCP."));
                else if (web is not null) problems.Add(new(i, PortField.Web, "Only one port can be the web port."));
                else web = i;
            }

            if (row.Access == PortAccess.Internal) continue;
            if (row.Access == PortAccess.Custom && !IPAddress.TryParse(row.HostIp?.Trim().Trim('[', ']'), out _))
                problems.Add(new(i, PortField.Access, $"{port}/{protocol} needs the address to publish on."));
            if (row.HostPort is not int hostPort || hostPort is < 1 or > 65535)
            {
                problems.Add(new(i, PortField.HostPort, "Pick a host port between 1 and 65535."));
                continue;
            }
            var address = ContainerPorts.BindAddress(row.Access, row.HostIp) ?? "";
            if (bound.Any(b => b.Port == hostPort && b.Protocol == protocol && ContainerPorts.Overlap(b.Address, address)))
                problems.Add(new(i, PortField.HostPort, $"Host port {hostPort} is listed twice."));
            else if (HostPortMap.Check(context.Uses, default, hostPort, protocol, address, context.Self).Problem is { } taken)
                problems.Add(new(i, PortField.HostPort, taken));
            bound.Add((hostPort, protocol, address));
        }

        if (context.Domains.Count > 0)
        {
            var domain = context.Domains[0];
            if (web is not int at)
                problems.Add(new(null, PortField.Web, $"{domain} needs a web port: domains go to the web port."));
            else if (rows[at].Access == PortAccess.Internal && context.TunnelOnHost)
                problems.Add(new(at, PortField.Access, NeedsHostPort(domain)));
        }
        return problems;
    }

    /// <summary>Why a domain can't go to an Internal web port while cloudflared runs on the host's network.</summary>
    public static string NeedsHostPort(string domain) => $"{domain} needs the web port on the host. Set it to LAN or Host only.";

    /// <summary>
    /// Whether the app's container has to be recreated for the rows to be live: a port's access, host port, address,
    /// container port or protocol changed, or a port was added or removed. Moving the web marker isn't one: domains, the
    /// health URL and scheduled jobs follow it at once.
    /// </summary>
    public static bool NeedsRestart(IEnumerable<PortMapping> saved, IEnumerable<PortRow> rows) =>
        RuntimeKey(saved.Select(PortRow.From)) != RuntimeKey(rows);

    /// <summary>What the container is created with for these ports, in any order. The web marker isn't part of it.</summary>
    public static string RuntimeKey(IEnumerable<PortRow> rows) =>
        string.Join('|', rows.Where(r => r.ContainerPort is not null).Select(r => r.Binding).Order(StringComparer.Ordinal));

    /// <summary>
    /// The rows as they're saved, in their order: a row without a container port is left out, and each keeps only what its
    /// access uses (no host port when it's Internal, an address only when it's Custom, the web marker only on TCP).
    /// </summary>
    public static List<PortMapping> ToMappings(IEnumerable<PortRow> rows) =>
        rows.Where(r => r.ContainerPort is not null).Select(r =>
        {
            var protocol = ProtocolOf(r.Protocol);
            return new PortMapping
            {
                ContainerPort = r.ContainerPort!.Value,
                Protocol = protocol,
                Access = r.Access,
                HostPort = r.Access == PortAccess.Internal ? null : r.HostPort,
                HostIp = r.Access == PortAccess.Custom ? r.HostIp?.Trim() : null,
                IsWeb = r.IsWeb && protocol == "tcp",
            };
        }).ToList();

    /// <summary>The app's web port: the row marked as it; null for an app without one, such as a worker.</summary>
    public static PortMapping? WebOf(IEnumerable<PortMapping> ports) => ports.Where(p => p.IsWeb && p.Protocol == "tcp").MinBy(p => p.Id);

    /// <summary>"tcp" or "udp" as a row has it, lower case; an empty one is TCP.</summary>
    internal static string ProtocolOf(string? protocol) => string.IsNullOrWhiteSpace(protocol) ? "tcp" : protocol.Trim().ToLowerInvariant();
}
