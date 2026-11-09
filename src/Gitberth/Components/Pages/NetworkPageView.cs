using System.Globalization;
using Gitberth.Docker;
using Gitberth.Network;

namespace Gitberth.Components.Pages;

/// <summary>Which host ports the Network page lists: all, those a container holds (every app alike), or the host's own.</summary>
public enum HostPortGroup { All, Containers, Host }

/// <summary>A small tag after a host port's holder: "3 domains", "Stopped", "Random".</summary>
/// <param name="Tone">good, warn, neutral, accent or info; the tag's colour.</param>
public sealed record PortNote(string Text, string Tone, string? Title = null);

/// <summary>Who holds a host port, as a row of the Network page shows it.</summary>
/// <param name="Name">The app's or container's name, a domain, or what holds it on the host. Never "(app)".</param>
/// <param name="Sub">The line under it: the container's name when the app goes by another, "On the host", a route's domain.</param>
/// <param name="Link">The holder's page, when it's a container: every container is an app with a page.</param>
/// <param name="Container">The container behind it, for its logo; null for the host's own and the tunnel's routes.</param>
/// <param name="Self">Gitberth's own container.</param>
/// <param name="Icon">An icon instead of a logo, for what isn't a container.</param>
/// <param name="Down">It holds the port but isn't running: stopped, restarting, or saved and not created yet.</param>
public sealed record PortHolder(string Name, string? Sub, string? Link, string? Container, bool Self, string? Icon, bool Down);

/// <summary>How the Network page shows a Docker network's member: its name, and a tag for Gitberth's own or a deploy's.</summary>
public sealed record MemberLook(string Name, string? Tag);

/// <summary>
/// The Network page's words and groupings, worked out from the host-port map and the network list. Pure, so they're
/// tested. One citizen level: a port is held by a container (whoever made it) or by the host, never "an app" or "a
/// container" apart.
/// </summary>
public static class NetworkPageView
{
    /// <summary>Whether a line of the Host ports card belongs to the group.</summary>
    public static bool InGroup(HostPortRow row, HostPortGroup group) => group switch
    {
        HostPortGroup.Containers => !OnHost(row.First),
        HostPortGroup.Host => OnHost(row.First),
        _ => true,
    };

    /// <summary>How many lines each group has, for the filter's counts.</summary>
    public static (int All, int Containers, int Host) Counts(IReadOnlyCollection<HostPortRow> rows)
    {
        var host = rows.Count(r => OnHost(r.First));
        return (rows.Count, rows.Count - host, host);
    }

    /// <summary>The ports and protocols in use, as listed: what "83 in use" counts.</summary>
    public static int InUse(IEnumerable<HostPortRow> rows) => rows.SelectMany(r => r.Uses).Select(u => (u.Port, u.Protocol)).Distinct().Count();

    /// <summary>How many of the containers' ports every device on the LAN reaches, and how many only the host does.</summary>
    public static (int Lan, int HostOnly) Reach(IEnumerable<HostPortRow> rows)
    {
        var bound = rows.SelectMany(r => r.Uses).Where(u => !OnHost(u) && u.HostIp is not null).ToList();
        int Count(string word) => bound.Where(u => HostPortMap.Access(u.HostIp) == word).Select(u => (u.Port, u.Protocol)).Distinct().Count();
        return (Count("LAN"), Count("Host only"));
    }

    /// <summary>"3001", or a folded run's "9100–9175".</summary>
    public static string PortText(HostPortRow row) =>
        row.Folded && row.LastPort != row.Port ? $"{N(row.Port)}–{N(row.LastPort)}" : N(row.Port);

    /// <summary>
    /// Who reaches the port, as its Access tag says it: LAN (every address), Host only (127.0.0.1), the address itself for
    /// one address, "Mixed" for a folded run with several; null where Docker doesn't bind it (the host's own, a route).
    /// </summary>
    public static (AddressKind Kind, string Word)? Access(IEnumerable<HostPortUse> uses)
    {
        var words = uses.Where(u => u.HostIp is not null && u.State != HostPortState.Listening).Select(u => HostPortMap.Access(u.HostIp)).Distinct().ToList();
        if (words.Count == 0)
        {
            // A program on the host: say where it listens when that's all one kind.
            var listening = uses.Where(u => u.HostIp is not null).Select(u => HostPortMap.Access(u.HostIp)).Distinct().ToList();
            return listening.Count == 1 ? Kind(listening[0]) : null;
        }
        return words.Count == 1 ? Kind(words[0]) : (AddressKind.Lan, "Mixed");

        static (AddressKind, string) Kind(string word) => word switch
        {
            "LAN" => (AddressKind.Lan, "LAN"),
            "Host only" => (AddressKind.HostOnly, "Host only"),
            _ => (AddressKind.Lan, word),
        };
    }

    /// <summary>Where the port goes in the container: "3000", "Several ports", or null for none (the host's own, a route).</summary>
    public static string? GoesTo(IReadOnlyList<HostPortUse> uses)
    {
        var ports = uses.Select(u => u.ContainerPort).Distinct().ToList();
        return ports switch
        {
            [null] => null,
            [{ } one] => N(one),
            _ => "Several ports",
        };
    }

    /// <summary>
    /// Who holds it: the app's or container's own name and its page, or what holds it on the host. Never "(app)".
    /// <paramref name="title"/> is the name the Apps list gives the container, when it has another (a renamed app).
    /// </summary>
    public static PortHolder Holder(HostPortUse u, string? title = null)
    {
        var down = u.State is HostPortState.Stopped or HostPortState.Saved;
        var name = u.Note != HostPortUse.TemporaryNote && title is { Length: > 0 } ? title : u.Who;
        return u.Kind switch
        {
            HostPortUser.Gitberth => new PortHolder(u.Container ?? u.Who, "Gitberth itself", u.Link, u.Container, true, null, down),
            HostPortUser.App or HostPortUser.Container => new PortHolder(name,
                u.Note == HostPortUse.TemporaryNote ? $"A deploy's container: {u.Container}"
                : u.Container is { } c && c != name ? c : null,
                u.Link, u.Container, false, null, down),
            HostPortUser.Route => new PortHolder(u.Who, u.AppId is null ? "The tunnel sends a domain here" : "The tunnel sends this domain here", u.Link, null, false,
                MudBlazor.Icons.Material.Outlined.Public, false),
            HostPortUser.Reserved => new PortHolder(u.Who == HostPortMap.Ssh ? "SSH" : "Docker's API", "On the host", null, null, false,
                u.Who == HostPortMap.Ssh ? MudBlazor.Icons.Material.Outlined.Terminal : MudBlazor.Icons.Material.Outlined.ViewInAr, false),
            _ => new PortHolder("A program on the host", "Outside Docker", null, null, false, MudBlazor.Icons.Material.Outlined.Dns, false),
        };
    }

    /// <summary>The tags after a holder: its domains, stopped, restarting, random, saved, a deploy's.</summary>
    public static IReadOnlyList<PortNote> Notes(HostPortUse u)
    {
        var notes = new List<PortNote>();
        switch (u.Note)
        {
            case null:
                break;
            case HostPortUse.StoppedNote:
                notes.Add(new PortNote("Stopped", "neutral", "It takes the port again when it starts."));
                break;
            case HostPortUse.RestartingNote:
                notes.Add(new PortNote("Restarting", "warn", "Docker lets go of the port until it's up again."));
                break;
            case HostPortUse.TemporaryNote:
                notes.Add(new PortNote("Deploying", "info", "A deploy's temporary container holds it for a moment."));
                break;
            case "Saved, not live":
                notes.Add(new PortNote("Saved, not live", "accent", "Saved for it; it takes the port when the change is applied."));
                break;
            case "Random: changes at every start":
                notes.Add(new PortNote("Random", "warn", "Docker picked it: it changes at every start."));
                break;
            case var domains when domains.EndsWith(" domain", StringComparison.Ordinal) || domains.EndsWith(" domains", StringComparison.Ordinal):
                notes.Add(new PortNote(domains, "good", "Its domains reach it through the Cloudflare tunnel."));
                break;
            case var other when u.Kind == HostPortUser.Route:
                // "Tunnel route to app.example.com": the holder's line says it already.
                if (!other.StartsWith("Tunnel route", StringComparison.Ordinal)) notes.Add(new PortNote(other, "neutral"));
                break;
            case var other:
                notes.Add(new PortNote(other, "neutral"));
                break;
        }
        return notes;
    }

    /// <summary>A folded run's holder: "76 containers", the names' common start ("sample-*"), and how many are stopped.</summary>
    public static (string Count, string Pattern, int Stopped) Fold(HostPortRow row)
    {
        var containers = row.Uses.Select(u => u.Container ?? u.Who).Distinct(StringComparer.Ordinal).Count();
        return ($"{N(containers)} {(containers == 1 ? "container" : "containers")}", $"{row.Prefix}*", row.Uses.Count(u => u.State == HostPortState.Stopped));
    }

    /// <summary>A folded run's key, to open and close it across refreshes.</summary>
    public static string FoldKey(HostPortRow row) => $"{row.Port}-{row.LastPort}/{row.Protocol}";

    /// <summary>"5433", "53/udp" or "8080/tcp"; null for anything else.</summary>
    public static (int Port, string Protocol)? ParseCheck(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().ToLowerInvariant().Split('/', 2);
        var protocol = parts.Length > 1 ? parts[1].Trim() : "tcp";
        return int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 && protocol is "tcp" or "udp"
            ? (port, protocol)
            : null;
    }

    /// <summary>The answer when what's typed isn't a port.</summary>
    public const string NotAPort = "Type a port between 1 and 65535, like 5433 or 53/udp.";

    /// <summary>Where the card's lines were read from, and what couldn't be: the card's foot.</summary>
    public static string Footer(HostPortView view, string clock)
    {
        var read = $"Read at {clock} from Docker, Gitberth's apps and the tunnel's routes";
        if (view.Scan.Error is { } why)
            return $"{read}. The host's own sockets couldn't be read ({why.TrimEnd('.')}), so ports held by programs outside Docker aren't listed.";
        var hostNetwork = view.HostNetwork.Count == 0 ? ""
            : $" Containers on the host's network, like {view.HostNetwork[0]}, show up as a program on the host.";
        return $"{read}, and the host's sockets.{hostNetwork}";
    }

    /// <summary>A member's name without "(app)": Gitberth's own and a deploy's container get a tag instead.</summary>
    public static MemberLook Member(NetworkMember m)
    {
        var title = m.Title;
        if (title.EndsWith(" (app)", StringComparison.Ordinal)) return new MemberLook(title[..^6], null);
        if (title.EndsWith(" (Gitberth)", StringComparison.Ordinal)) return new MemberLook(title[..^11], "Gitberth");
        var deploy = title.IndexOf(" (a deploy of ", StringComparison.Ordinal);
        if (deploy > 0 && title.EndsWith(')')) return new MemberLook(title[..deploy], "Deploy of " + title[(deploy + 14)..^1]);
        return new MemberLook(title, null);
    }

    /// <summary>The tags after a network's name: Shared, Internal, Docker's own.</summary>
    public static IReadOnlyList<string> Tags(NetworkView n)
    {
        var tags = new List<string>();
        if (n.Shared) tags.Add("Shared network");
        if (n.Network.Name == LanAddresses.Network) tags.Add("Own LAN addresses");
        if (n.Network.Internal) tags.Add("Internal: no way out");
        if (n.System) tags.Add("Docker's own");
        return tags;
    }

    /// <summary>The line under a network's name: what Gitberth knows it as, without repeating its tags.</summary>
    public static string? NoteOf(NetworkView n) => n.Note switch
    {
        "Gitberth's shared network" => "Every app is on it: containers here reach each other by name.",
        "Own LAN addresses" => "Containers with their own address on your LAN.",
        "Docker's default: no names" => "Docker's default: containers here can't reach each other by name.",
        var note => note,
    };

    /// <summary>What an address on the network means, for its tooltip.</summary>
    public static string AddressHint(string network) => network == LanAddresses.Network
        ? "Its own LAN address: devices on your network reach it there, and it stays when the container is recreated."
        : "Changes when the container is recreated. Use the name.";

    /// <summary>"5 containers", "1 container", "None".</summary>
    public static string MemberCount(int n) => n == 0 ? "None" : n == 1 ? "1 container" : $"{N(n)} containers";

    // The host's own: SSH, Docker's API, programs outside Docker.
    private static bool OnHost(HostPortUse u) => u.Kind is HostPortUser.HostProgram or HostPortUser.Reserved;

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
