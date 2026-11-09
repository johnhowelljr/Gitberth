using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Components.Shared.Network;

/// <summary>Where something reaches a database: a host, and the port there.</summary>
/// <param name="Via">How a device on the LAN gets there, for the choice between them: "the host" (a published port) or "its own address".</param>
public sealed record ConnectTarget(string Host, int Port, string? Via = null);

/// <summary>A URL row of the Addresses card: "Postgres URL (LAN)", shown with the password masked, copied with it.</summary>
public sealed record UrlRow(string Label, string Shown, string Copy);

/// <summary>
/// Where Connect and the Addresses card's URL rows point (5.1, 5.2), from the container's addresses as AddressBook gives
/// them: on the LAN, its published port on the host's LAN address, then its own LAN address; inside Docker, its name on its
/// network. The details themselves come from the container's settings (<see cref="ConnectionDetails"/>).
/// </summary>
public static class ConnectionRows
{
    /// <summary>The container port a database Gitberth recognizes is reached on (5432 for Postgres); null for anything else.</summary>
    public static int? DatabasePort(ContainerDetails d) => ConnectionDetails.For(d, "")?.Port;

    /// <summary>Where a device on the LAN reaches the port: each LAN address of its, the host's published ports first.</summary>
    public static List<ConnectTarget> Lan(IReadOnlyList<Address> addresses, int port) =>
        addresses.Where(a => a.Kind == AddressKind.Lan && a.ContainerPort == port && a.HostPort is not null)
            .Select(a => new ConnectTarget(HostOf(a.Copy), a.HostPort!.Value, "the host"))
            .Concat(addresses.Where(a => a.Kind == AddressKind.OwnLan && (a.ContainerPort == port || a.ContainerPort is null))
                .Select(a => new ConnectTarget(HostOf(a.Copy), port, "its own address")))
            .DistinctBy(t => (t.Host, t.Port))
            .ToList();

    /// <summary>Its address on the host only (127.0.0.1:5432), which the LAN can't reach; null when it isn't published that way.</summary>
    public static string? HostOnly(IReadOnlyList<Address> addresses, int port) =>
        addresses.FirstOrDefault(a => a.Kind == AddressKind.HostOnly && a.ContainerPort == port)?.Copy;

    /// <summary>What containers on its network call it; null when it's on no named network.</summary>
    public static ConnectTarget? Internal(IReadOnlyList<Address> addresses, int port) =>
        addresses.FirstOrDefault(a => a.Kind == AddressKind.Internal && a.ContainerPort == port) is { } a ? new ConnectTarget(HostOf(a.Copy), port) : null;

    /// <summary>The Addresses card's URL rows, one per scope, the LAN first; none for what isn't a database Gitberth recognizes.</summary>
    public static List<UrlRow> Urls(ContainerDetails d, IReadOnlyList<Address> addresses)
    {
        if (ConnectionDetails.EngineOf(d.Image) is not { } engine || DatabasePort(d) is not int port) return [];
        var name = ConnectionDetails.UrlName(engine);
        var rows = new List<UrlRow>();
        foreach (var (scope, at) in new[] { ("LAN", Lan(addresses, port).FirstOrDefault()), ("internal", Internal(addresses, port)) })
            if (at is not null && ConnectionDetails.For(d, at.Host, at.Port) is { } info)
                rows.Add(new UrlRow($"{name} ({scope})", ConnectionDetails.Render(info, ConnectionFormat.Url, reveal: false),
                    ConnectionDetails.Render(info, ConnectionFormat.Url, reveal: true)));
        return rows;
    }

    /// <summary>The host of an address as AddressBook writes it: 10.1.1.80 from http://10.1.1.80:8080, fd00::5 from [fd00::5]:53, postgres from postgres:5432.</summary>
    public static string HostOf(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        var rest = (scheme < 0 ? address : address[(scheme + 3)..]).Split('/')[0];
        if (rest.StartsWith('[')) return rest[1..Math.Max(1, rest.IndexOf(']'))];
        return rest.Count(c => c == ':') == 1 ? rest[..rest.IndexOf(':')] : rest;
    }
}

/// <summary>What a standalone container's Addresses card lists, from the addresses AddressBook gives it (5.1).</summary>
public static class ContainerAddressing
{
    /// <summary>
    /// The addresses as the card lists them. Gitberth's own web port is a URL inside Docker too (http://gitberth:8080). A
    /// port Gitberth noted is reached by name like the container's own (postgres:8081) while it's on a named network, and
    /// on its own LAN address when it has one (10.1.1.230:8081), which then stands for itself only while no port is known.
    /// A container on the host's network listens straight on the host's addresses: Docker can't say on which ports, so the
    /// ones noted for it follow the line that says so (10.1.1.80:53, http://10.1.1.80:8080).
    /// </summary>
    /// <param name="hostNetwork">It uses the host's network (<see cref="ContainerAddresses.HostNetwork"/>).</param>
    /// <param name="named">What containers on its named network call it; null when it's on none.</param>
    /// <param name="running">It runs: a noted port answers by name only then.</param>
    public static IReadOnlyList<Address> Shown(IReadOnlyList<Address> addresses, ContainerRole role, IEnumerable<ContainerPortNote> notes, string lanHost,
        bool hostNetwork = false, string? named = null, bool running = true)
    {
        var shown = addresses.ToList();
        var noted = notes.OrderBy(n => n.Port).ThenBy(n => n.Protocol, StringComparer.Ordinal).ToList();
        if (role == ContainerRole.Self)
            for (var i = 0; i < shown.Count; i++)
                if (shown[i] is { Kind: AddressKind.Internal, ContainerPort: int port } a
                    && !a.Text.Contains("://", StringComparison.Ordinal)
                    && shown.Any(l => l.Kind == AddressKind.Lan && l.ContainerPort == port && l.Href is not null))
                    shown[i] = a with { Text = $"http://{a.Text}", Copy = $"http://{a.Copy}" };
        if (!hostNetwork)
        {
            // After the container's own: one it has already isn't noted twice.
            if (named is not null)
                foreach (var n in noted.Where(n => !shown.Any(a => a.Kind == AddressKind.Internal && a.ContainerPort == n.Port && a.Protocol == n.Protocol)))
                {
                    var text = ContainerPorts.Address(named, n.Port);
                    shown.Add(new Address(AddressKind.Internal, text, null, text, n.Port, n.Protocol, running, running ? null : "Stopped"));
                }
            if (shown.FirstOrDefault(a => a.Kind == AddressKind.OwnLan) is not { } own) return shown;
            var ip = ConnectionRows.HostOf(own.Copy);
            var ownNoted = noted.Where(n => !shown.Any(a => a.Kind == AddressKind.OwnLan && a.ContainerPort == n.Port && a.Protocol == n.Protocol)).ToList();
            foreach (var n in ownNoted)
            {
                var at = ContainerPorts.Address(ip, n.Port);
                var url = AddressBook.IsWeb(n.Port, n.Protocol, role, appWebPort: false) ? $"{(n.Port is 443 or 8443 ? "https" : "http")}://{at}" : null;
                shown.Add(new Address(AddressKind.OwnLan, url ?? at, url, url ?? at, n.Port, n.Protocol, own.Live, own.Note));
            }
            if (ownNoted.Count > 0) shown.RemoveAll(a => a.Kind == AddressKind.OwnLan && a.ContainerPort is null);
            return shown;
        }
        if (shown.FirstOrDefault(a => a.Kind == AddressKind.Lan) is not { } host) return shown;
        foreach (var n in noted)
        {
            var at = ContainerPorts.Address(AddressBook.HostPart(lanHost), n.Port);
            var web = AddressBook.IsWeb(n.Port, n.Protocol, role, appWebPort: false);
            var text = web ? $"{(n.Port is 443 or 8443 ? "https" : "http")}://{at}" : at;
            shown.Add(new Address(AddressKind.Lan, text, web && host.Live ? text : null, text, n.Port, n.Protocol, host.Live, host.Live ? null : "Stopped"));
        }
        return shown;
    }
}
