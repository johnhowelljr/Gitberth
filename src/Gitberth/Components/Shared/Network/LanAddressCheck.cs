using System.Net;
using System.Net.Sockets;
using Gitberth.Docker;

namespace Gitberth.Components.Shared.Network;

/// <summary>Whether the LAN address of this host that Gitberth is set to use is still one of the host's (5.12).</summary>
public static class LanAddressCheck
{
    /// <summary>
    /// The address detected on the host's default route, when the one Gitberth is set to use is an IPv4 address the host
    /// doesn't have any more; null otherwise. A name isn't checked (the PC resolves it, not Gitberth), and nothing is while
    /// the host's network couldn't be read.
    /// </summary>
    /// <param name="configured">The address saved in Settings, else GITBERTH_PUBLIC_HOST.</param>
    public static string? Drifted(string? configured, HostScan? scan)
    {
        if (string.IsNullOrWhiteSpace(configured) || scan is not { Error: null, Address: { } detected }) return null;
        var value = configured.Trim();
        // IPAddress also reads "10.1" as an address; a name made of digits is still a name.
        if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        return (scan.LocalAddresses ?? [detected]).Contains(ip.ToString()) ? null : detected;
    }
}
