using System.Globalization;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>
/// The host's own network, where own LAN addresses go: its default route's interface, the network on it, the router and
/// the host's own address there.
/// </summary>
public sealed record LanSubnet(string Interface, uint Network, int Prefix, uint Router, uint Host)
{
    public uint Mask => Prefix == 0 ? 0u : uint.MaxValue << (32 - Prefix);

    public uint Broadcast => Network | ~Mask;

    /// <summary>10.1.1.0/24.</summary>
    public string Cidr => $"{LanAddresses.Text(Network)}/{Prefix.ToString(CultureInfo.InvariantCulture)}";

    public string RouterText => LanAddresses.Text(Router);

    public string HostText => LanAddresses.Text(Host);

    public bool Contains(uint ip) => (ip & Mask) == Network;

    /// <summary>The host's network as its scan found it; null when the scan didn't find its interface, address, network or router.</summary>
    public static LanSubnet? From(HostScan? scan) =>
        scan is { Interface: { } nic, Address: { } host, Gateway: { } router, Subnet: { } subnet } ? Make(nic, subnet, router, host) : null;

    /// <summary>
    /// The network an own-addresses network (gitberth-lan) was made on, from Docker's answer: its interface, subnet and
    /// gateway, with the host's address as the scan has it. Null when Docker doesn't say all three.
    /// </summary>
    public static LanSubnet? From(NetworkInfo network, string? host) =>
        network.Options.GetValueOrDefault("parent") is { Length: > 0 } nic && network.Subnets.FirstOrDefault(s => s.Contains('.')) is { } subnet
        && network.Gateways.FirstOrDefault(g => g.Contains('.')) is { } router
            ? Make(nic, subnet, router, host ?? "0.0.0.0")
            : null;

    private static LanSubnet? Make(string nic, string subnet, string router, string host) =>
        LanAddresses.TryParseCidr(subnet, out var network, out var prefix) && LanAddresses.TryParse(router, out var r) && LanAddresses.TryParse(host, out var h)
            ? new LanSubnet(nic, network, prefix, r, h)
            : null;
}

/// <summary>
/// The block of LAN addresses kept for Gitberth, which it picks containers' own addresses from: a CIDR (10.1.1.224/28) or
/// a range (10.1.1.224-10.1.1.239), kept out of the router's DHCP range by the owner.
/// </summary>
/// <param name="Prefix">The CIDR's prefix length; null for a range.</param>
public sealed record LanBlock(uint First, uint Last, int? Prefix)
{
    public int Count => (int)(Last - First) + 1;

    /// <summary>As Settings writes it: 10.1.1.224/28, or 10.1.1.224-10.1.1.239.</summary>
    public string Text => Prefix is int prefix ? $"{LanAddresses.Text(First)}/{prefix.ToString(CultureInfo.InvariantCulture)}" : Range;

    /// <summary>Its first address to its last: 10.1.1.224-10.1.1.239.</summary>
    public string Range => $"{LanAddresses.Text(First)}-{LanAddresses.Text(Last)}";

    /// <summary>"16 addresses, 10.1.1.224-10.1.1.239".</summary>
    public string Summary => $"{Count.ToString(CultureInfo.InvariantCulture)} addresses, {Range}";

    /// <summary>Its last address, where the lan-check helper sits while it tests the block.</summary>
    public string LastText => LanAddresses.Text(Last);

    public bool Contains(uint ip) => ip >= First && ip <= Last;

    public IEnumerable<uint> Addresses
    {
        get
        {
            for (var ip = (long)First; ip <= Last; ip++) yield return (uint)ip;
        }
    }

    /// <summary>
    /// Docker's ip-range for it: the block itself, or for a range that isn't one CIDR, the largest CIDR inside it, so a
    /// container joined without an address (by hand, say) still gets one of the block's.
    /// </summary>
    public string IpRange
    {
        get
        {
            if (Prefix is int prefix) return Text;
            var (start, length) = (First, 32);
            for (var p = 31; p >= 24; p--)
            {
                var size = 1u << (32 - p);
                // The first start at or above First that's a multiple of the size.
                var aligned = (First + size - 1) & ~(size - 1);
                if (aligned < First || aligned + size - 1 > Last || aligned + size - 1 < aligned) break;
                (start, length) = (aligned, p);
            }
            return $"{LanAddresses.Text(start)}/{length.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}

/// <summary>One run of the lan-check helper (<see cref="HostScanHelper.LanCheckBody"/>).</summary>
/// <param name="Network">An own-addresses network to run on, at <paramref name="Address"/>; null for the host's own network, or for <paramref name="Temporary"/>.</param>
/// <param name="Address">Its own address on that network.</param>
/// <param name="Temporary">A network to make for the run and remove after it, as POST networks/create takes it but for its name and label.</param>
/// <param name="Router">The router to try as well; null for none.</param>
/// <param name="Ports">The ports each target is tried on.</param>
/// <param name="Targets">The addresses to try.</param>
public sealed record LanCheck(string? Network, string? Address, JsonObject? Temporary, string? Router, IReadOnlyList<int> Ports, IReadOnlyList<string> Targets);

/// <summary>What the lan-check helper found.</summary>
/// <param name="Answering">The addresses something answers on, the router's left out: a complete ARP entry, or a TCP port that opened.</param>
/// <param name="Open">The TCP ports that opened, by address.</param>
/// <param name="RouterAnswers">The router answered, on 53, 80 or 443 or by its ARP entry; null when it wasn't tried.</param>
/// <param name="Error">Why the check didn't run; nothing else is known then.</param>
public sealed record LanScan(IReadOnlySet<string> Answering, IReadOnlyDictionary<string, IReadOnlyList<int>> Open, bool? RouterAnswers, string? Error = null)
{
    public static LanScan Failed(string why) => new(new HashSet<string>(), new Dictionary<string, IReadOnlyList<int>>(), null, why);

    /// <summary>
    /// The helper's output: an "open &lt;address&gt; &lt;port&gt;" line for each connect that went through, then /proc/net/arp.
    /// An ARP entry is complete when its flags have ATF_COM (0x2) and its MAC is known: a device answered for the address,
    /// whatever its ports said. Output without the ARP table was cut short, and says nothing.
    /// </summary>
    public static LanScan Read(string output, string? router)
    {
        var open = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var arp = new HashSet<string>(StringComparer.Ordinal);
        var table = false;
        var sawTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("==> ", StringComparison.Ordinal))
            {
                table = line == "==> /proc/net/arp <==";
                sawTable |= table;
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!table && parts is ["open", var ip, var port] && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            {
                if (!open.TryGetValue(ip, out var ports)) open[ip] = ports = [];
                if (!ports.Contains(p)) ports.Add(p);
            }
            // "IP address       HW type     Flags       HW address            Mask     Device", then one entry a line.
            else if (table && parts.Length >= 4 && parts[0] != "IP" && Complete(parts[2], parts[3])) arp.Add(parts[0]);
        }
        if (!sawTable) return Failed("the check helper's output was cut short");
        var answering = open.Keys.Concat(arp).Where(a => a != router).ToHashSet(StringComparer.Ordinal);
        return new LanScan(answering, open.ToDictionary(o => o.Key, o => (IReadOnlyList<int>)o.Value.Order().ToList(), StringComparer.Ordinal),
            router is null ? null : open.ContainsKey(router) || arp.Contains(router));
    }

    private static bool Complete(string flags, string mac) =>
        int.TryParse(flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? flags[2..] : flags, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var f)
        && (f & 0x2) != 0 && mac != "00:00:00:00:00:00";
}

/// <summary>One line of a test of the block: "No device answers on 10.1.1.224-10.1.1.239", with whether it passed.</summary>
public sealed record LanTestLine(bool Ok, string Text);

/// <summary>A test of the block, or turning own LAN addresses on, which tests it on gitberth-lan itself.</summary>
/// <param name="Problem">Why it wasn't tested at all (the block is wrong, say), in the field's words; null when it was.</param>
/// <param name="Note">What Gitberth did about it: "Gitberth removed gitberth-lan again."</param>
public sealed record LanTest(LanBlock? Block, IReadOnlyList<LanTestLine> Lines, string? Problem = null, string? Note = null)
{
    public bool Passed => Problem is null && Lines.Count > 0 && Lines.All(l => l.Ok);

    public static LanTest Refused(string why, LanBlock? block = null) => new(block, [], why);
}

/// <summary>A container that has an own LAN address, or that Gitberth keeps one for.</summary>
/// <param name="Container">Its name; for an app, its container's (gb-&lt;slug&gt;).</param>
/// <param name="Title">What lines name it by: the app's name, or the container's.</param>
/// <param name="Link">Its page.</param>
public sealed record LanHolder(string Container, string Title, string Address, string Link);

/// <summary>Own LAN addresses as Settings › Own LAN addresses shows them.</summary>
/// <param name="On">gitberth-lan is there.</param>
/// <param name="Block">The block: the one it's on with, else the one saved.</param>
/// <param name="Subnet">The host's network: gitberth-lan's while it's on, else as the host scan found it.</param>
/// <param name="Problem">Why own LAN addresses can't be turned on here; null when they can.</param>
/// <param name="Note">Something to know first: the saved block's network is gone, say.</param>
public sealed record LanState(bool On, string? Block, LanSubnet? Subnet, IReadOnlyList<LanHolder> Holders, string? Problem, string? Note = null)
{
    public static LanState Unknown(string why) => new(false, null, null, [], why);
}

/// <summary>A container's own LAN address as its LAN address card shows it.</summary>
/// <param name="On">Own LAN addresses are turned on.</param>
/// <param name="Refusal">Why it can't have one (Gitberth itself, the host's network…), in the card's words; null when it can.</param>
/// <param name="Address">Its own address: there now, or kept for it; null for none.</param>
/// <param name="Automatic">The address is one of the block's, so it was picked from it; else it was typed (Static).</param>
/// <param name="Attached">It's on gitberth-lan now. A kept address it isn't on is joined again when it starts (an app's at its next deploy).</param>
/// <param name="Block">The block automatic addresses come from.</param>
/// <param name="Subnet">The host's network, where a static address can be.</param>
public sealed record LanAddressView(bool On, string? Refusal, string? Address, bool Automatic, bool Attached, LanBlock? Block, LanSubnet? Subnet);

/// <summary>What giving a container its own address, or taking it away, did.</summary>
/// <param name="Error">Why it didn't happen; null when it did.</param>
/// <param name="Done">"postgres has its own address 10.1.1.230 now."</param>
/// <param name="Check">What the check after it found: "10.1.1.230 answers on 5432." It's said, and never undoes the change.</param>
/// <param name="CheckOk">Something answered there.</param>
public sealed record LanChange(string? Error, string? Done = null, string? Check = null, bool CheckOk = true)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Own LAN addresses (own-lan-addresses.md): a container, or an app, can have its own address on the LAN as well as its
/// ports on the host's. They're on gitberth-lan, an ipvlan L2 network on the host's interface: every address uses the
/// interface's own MAC, so Proxmox's MAC filter never sees a new one, and Docker can't ask the router's DHCP for one, so
/// Gitberth picks them from a block the owner keeps out of it. Off until it's turned on in Settings › Own LAN addresses.
/// <list type="bullet">
/// <item>Test runs the lan-check helper on a temporary network at the block's last address: no device may answer in the
/// block, and the router must answer from it. Turn on makes gitberth-lan and keeps it only when the same test passes on it.</item>
/// <item>A container's address is joined live, pinned, at gateway priority -1 (<see cref="NetworkService.SetLanAddressAsync"/>),
/// and kept like any joined network, so it's the same after a restart or a recreate.</item>
/// <item>The host and cloudflared can't reach these addresses (ipvlan keeps its parent apart), so checks run from helpers on
/// gitberth-lan, and domains keep using host ports.</item>
/// </list>
/// One test, address change or turn on or off at a time: two temporary networks can't share the interface.
/// </summary>
public sealed class LanAddresses(SettingsService settings, HostScanner scanner, DockerApi api, IContainerEngine engine, HostHelpers helpers,
    NetworkService networks, IDbContextFactory<GitberthDb> dbFactory, ILogger<LanAddresses> logger)
{
    /// <summary>The network own LAN addresses are on.</summary>
    public const string Network = "gitberth-lan";

    /// <summary>What a LAN address card says, and a change is refused with, while own LAN addresses are off.</summary>
    public const string OffText = "Own LAN addresses are off. Turn them on in Settings › Own LAN addresses.";

    /// <summary>What a device is tried on, besides its ARP: the ports routers, NAS boxes, PCs and printers listen on.</summary>
    public static readonly IReadOnlyList<int> DevicePorts = [80, 443, 22];

    /// <summary>How many of the host network's highest addresses a suggestion tries.</summary>
    public const int SuggestTries = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Name => networks.LanNetwork;

    /// <summary>Own LAN addresses as Settings shows them. Throws when Docker can't be read.</summary>
    public async Task<LanState> StateAsync(CancellationToken ct = default)
    {
        var scan = await scanner.LatestAsync(ct);
        var saved = await settings.GetAsync(SettingKeys.OwnLanBlock, ct);
        var problem = helpers.InContainer ? null : "Gitberth tests addresses with a helper container, which needs Gitberth to run in one.";
        if (await api.InspectNetworkAsync(Name, ct) is { } on)
            return new LanState(true, saved ?? IpRangeOf(on), LanSubnet.From(on, scan.Address), await HoldersAsync(ct), null);
        var subnet = LanSubnet.From(scan);
        problem ??= subnet is null ? $"Gitberth couldn't read this host's network{(scan.Error is { } why ? $" ({why.TrimEnd('.')})" : "")}." : null;
        return new LanState(false, saved, subnet, [], problem, saved is null ? null : $"{Name} isn't there any more, so own LAN addresses are off. Turn them on again to use {saved}.");
    }

    /// <summary>
    /// Tests the block and keeps nothing: on a temporary network, at the block's last address, the lan-check helper tries
    /// every other address of the block and the router. While own LAN addresses are on, it runs on gitberth-lan instead (two
    /// such networks can't share the interface), and the addresses containers hold there don't count as devices.
    /// </summary>
    public async Task<LanTest> TestAsync(string? text, CancellationToken ct = default)
    {
        var (subnet, all, why) = await FactsAsync(ct);
        if (why is not null) return LanTest.Refused(why);
        var (block, problem) = CheckBlock(text, subnet!, all, Name);
        if (block is null) return LanTest.Refused(problem!);
        await _gate.WaitAsync(ct);
        try
        {
            if (all.Any(n => n.Name == Name))
            {
                var held = await HeldAsync(except: null, ct);
                if (HelperAddress(block, held.Keys.ToHashSet(StringComparer.Ordinal)) is not { } at) return LanTest.Refused($"Every address of {block.Text} is taken.", block);
                var targets = block.Addresses.Select(Text).Where(a => a != at && !held.ContainsKey(a)).ToList();
                return Lines(block, subnet!, await helpers.LanCheckAsync(new LanCheck(Name, at, null, subnet!.RouterText, DevicePorts, targets), ct));
            }
            var temporary = NetworkBody(null, subnet!, block.LastText + "/32", new Dictionary<string, string>());
            var others = block.Addresses.Where(a => a != block.Last).Select(Text).ToList();
            return Lines(block, subnet!, await helpers.LanCheckAsync(new LanCheck(null, block.LastText, temporary, subnet!.RouterText, DevicePorts, others), ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns own LAN addresses on: makes gitberth-lan (ipvlan L2 on the host's interface, in its network, the router as
    /// gateway, the block as Docker's ip-range, labelled gitberth.managed), then tests the block from it as
    /// <see cref="TestAsync"/> does. gitberth-lan stays only when both checks pass; the block is saved then.
    /// </summary>
    public async Task<LanTest> TurnOnAsync(string? text, CancellationToken ct = default)
    {
        var (subnet, all, why) = await FactsAsync(ct);
        if (why is not null) return LanTest.Refused(why);
        var (block, problem) = CheckBlock(text, subnet!, all, Name);
        if (block is null) return LanTest.Refused(problem!);
        if (all.Any(n => n.Name == Name)) return LanTest.Refused($"There's a network called {Name} already, so own LAN addresses are on.", block);
        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                await engine.CreateNetworkAsync(NetworkBody(Name, subnet!, block.IpRange, new Dictionary<string, string> { ["gitberth.managed"] = "true" }), ct);
            }
            catch (EngineException ex)
            {
                return LanTest.Refused($"Docker didn't make {Name}: {ex.Message}", block);
            }
            var others = block.Addresses.Where(a => a != block.Last).Select(Text).ToList();
            var tested = Lines(block, subnet!, await helpers.LanCheckAsync(new LanCheck(Name, block.LastText, null, subnet!.RouterText, DevicePorts, others), ct));
            if (!tested.Passed)
            {
                await RemoveNetworkAsync();
                return tested with { Note = $"Gitberth removed {Name} again." };
            }
            await settings.SetAsync(SettingKeys.OwnLanBlock, block.Text, ct);
            logger.LogInformation("Own LAN addresses are on: {Network} on {Interface}, block {Block}", Name, subnet!.Interface, block.Text);
            return tested;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns own LAN addresses off: every container on gitberth-lan, and every one Gitberth keeps there, loses its own
    /// address (its devices lose it), then gitberth-lan goes and the block is forgotten. One joined there by hand without
    /// an address leaves too, or its next start would look for a network that's gone. Returns why it couldn't, or null.
    /// </summary>
    public async Task<string?> TurnOffAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var errors = new List<string>();
            var on = (await api.ListContainersAsync(ct)).Where(c => c.Networks.Any(e => e.Network == Name)).Select(c => c.Name);
            foreach (var container in (await HoldersAsync(ct)).Select(h => h.Container).Union(on, StringComparer.Ordinal))
                if ((await networks.SetLanAddressAsync(container, null, ct)).Error is { } error && !error.StartsWith("There's no container called", StringComparison.Ordinal))
                    errors.Add(error);
            if (errors.Count > 0) return string.Join(" ", errors);
            // What's kept for a container that's gone has nobody to take it from.
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                await db.KeptNetworks.Where(k => k.Network == Name).ExecuteDeleteAsync(ct);
                await db.AppNetworks.Where(n => n.Network == Name).ExecuteDeleteAsync(ct);
            }
            if (await api.InspectNetworkAsync(Name, ct) is not null)
            {
                try
                {
                    await engine.RemoveNetworkAsync(Name, ct);
                }
                catch (EngineException ex) when (!ex.NotFound)
                {
                    return $"Docker didn't remove {Name}: {ex.Message}";
                }
            }
            await settings.SetAsync(SettingKeys.OwnLanBlock, null, ct);
            logger.LogInformation("Own LAN addresses are off: {Network} is removed", Name);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The highest /28 of the host's network where no device answers, from a lan-check on the host's own network over
    /// its highest <see cref="SuggestTries"/> addresses (it needs no address of its own there); null when there's none, with
    /// why. Only while own LAN addresses are off.
    /// </summary>
    public async Task<(LanBlock? Block, string? Problem)> SuggestAsync(CancellationToken ct = default)
    {
        var (subnet, all, why) = await FactsAsync(ct);
        if (why is not null) return (null, why);
        var lowest = Math.Max((long)subnet!.Network + 1, (long)subnet.Broadcast - SuggestTries);
        var targets = new List<string>();
        for (var ip = (long)subnet.Broadcast - 1; ip >= lowest; ip--)
            if (ip != subnet.Router && ip != subnet.Host) targets.Add(Text((uint)ip));
        await _gate.WaitAsync(ct);
        LanScan scan;
        try
        {
            scan = await helpers.LanCheckAsync(new LanCheck(null, null, null, null, [80], targets), ct);
        }
        finally
        {
            _gate.Release();
        }
        if (scan.Error is { } error) return (null, $"Couldn't look for a free block: {error.TrimEnd('.')}.");
        return Suggest(subnet, all, scan.Answering, (uint)lowest) is { } block
            ? (block, null)
            : (null, $"Devices answer in every /28 of {Text((uint)lowest)}-{Text(subnet.Broadcast)}. Type a block you keep free.");
    }

    /// <summary>Who has an own LAN address: each container on gitberth-lan, and each one Gitberth keeps there (an app's waits for its next deploy).</summary>
    public async Task<IReadOnlyList<LanHolder>> HoldersAsync(CancellationToken ct = default)
    {
        var holders = new Dictionary<string, LanHolder>(StringComparer.Ordinal);
        List<AppDefinition> apps;
        List<KeptNetwork> kept;
        List<AppNetwork> saved;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            apps = await db.Apps.AsNoTracking().ToListAsync(ct);
            kept = await db.KeptNetworks.AsNoTracking().Where(k => k.Network == Name && k.IPv4Address != null).ToListAsync(ct);
            saved = await db.AppNetworks.AsNoTracking().Where(n => n.Network == Name && n.IPv4Address != null).ToListAsync(ct);
        }
        var byContainer = apps.ToDictionary(a => a.ContainerName, StringComparer.Ordinal);
        LanHolder Holder(string container, string address) => byContainer.TryGetValue(container, out var app)
            ? new LanHolder(container, app.Name, address, $"apps/{app.Id}")
            : new LanHolder(container, container, address, $"containers/{Uri.EscapeDataString(container)}");
        // Not the lan-check, which only sits on gitberth-lan while it checks.
        foreach (var c in (await api.ListContainersAsync(ct)).Where(c => !HostScanHelper.IsHelper(c.Labels)))
            if (c.Networks.FirstOrDefault(e => e.Network == Name) is { } e && (e.FixedAddress ?? e.IpAddress) is { } address)
                holders[c.Name] = Holder(c.Name, address);
        foreach (var k in kept) holders.TryAdd(k.ContainerName, Holder(k.ContainerName, k.IPv4Address!));
        foreach (var n in saved)
            if (apps.FirstOrDefault(a => a.Id == n.AppId) is { } app) holders.TryAdd(app.ContainerName, Holder(app.ContainerName, n.IPv4Address!));
        return holders.Values.OrderBy(h => Parse(h.Address)).ToList();
    }

    /// <summary>The container's own LAN address, and whether it may have one, as its LAN address card shows it. Throws when Docker can't be read.</summary>
    public async Task<LanAddressView> ForAsync(string container, CancellationToken ct = default)
    {
        var own = await networks.OwnAddressOfAsync(container, ct);
        var info = await api.InspectNetworkAsync(Name, ct);
        var block = info is null ? null : await BlockAsync(info, ct);
        var subnet = info is null ? null : LanSubnet.From(info, (await scanner.LatestAsync(ct)).Address);
        var automatic = own.Address is { } address && block is not null && TryParse(address, out var ip) && block.Contains(ip);
        return new LanAddressView(info is not null, own.Refusal, own.Address, automatic, own.Attached, block, subnet);
    }

    /// <summary>
    /// Gives the container its own address at once, nothing restarting: the next free one of the block (Automatic, when
    /// <paramref name="address"/> is null), or the one typed (Static). Free means no container holds it on gitberth-lan,
    /// Gitberth keeps it for none, and nothing answers there when the lan-check helper tries it. Then the helper tries the
    /// address on the ports the container is known to listen on; what it finds is said, and never undoes the change.
    /// </summary>
    public async Task<LanChange> GiveAsync(string container, string? address, CancellationToken ct = default)
    {
        try
        {
            if (await api.InspectNetworkAsync(Name, ct) is not { } info) return new LanChange(OffText);
            var own = await networks.OwnAddressOfAsync(container, ct);
            if (own.Refusal is { } refusal) return new LanChange(refusal);
            if (await BlockAsync(info, ct) is not { } block || LanSubnet.From(info, (await scanner.LatestAsync(ct)).Address) is not { } subnet)
                return new LanChange($"Docker doesn't say which block {Name} has. Turn own LAN addresses off and on again.");
            if (!helpers.InContainer) return new LanChange("Gitberth checks addresses with a helper container, which needs Gitberth to run in one.");
            await _gate.WaitAsync(ct);
            try
            {
                var held = await HeldAsync(except: container, ct);
                var taken = held.Keys.ToHashSet(StringComparer.Ordinal);
                if (own.Address is { } current) taken.Add(current);
                string pick;
                if (address is null)
                {
                    if (own.Address is { } mine && TryParse(mine, out var ip) && block.Contains(ip)) return new LanChange(null, $"{own.Title} has {mine} from the block already.");
                    if (HelperAddress(block, taken) is not { } at) return new LanChange($"Every address of {block.Text} is taken.");
                    var candidates = block.Addresses.Select(Text).Where(a => a != at && !taken.Contains(a)).Take(LanCheckFew).ToList();
                    var scan = await helpers.LanCheckAsync(new LanCheck(Name, at, null, null, DevicePorts, candidates), ct);
                    if (scan.Error is { } error) return new LanChange($"Couldn't check which addresses are free: {error.TrimEnd('.')}.");
                    if (NextFree(block, taken, scan.Answering, at) is not { } free)
                        return new LanChange($"Every address of {block.Text} is taken, or a device answers there.");
                    pick = free;
                }
                else
                {
                    address = address.Trim();
                    if (address == own.Address) return new LanChange(null, $"{own.Title} has {address} already.");
                    if (WhyNotStatic(address, subnet, held) is { } wrong) return new LanChange(wrong);
                    taken.Add(address);
                    if (HelperAddress(block, taken) is not { } at) return new LanChange($"Every address of {block.Text} is taken, so Gitberth can't check {address}.");
                    var scan = await helpers.LanCheckAsync(new LanCheck(Name, at, null, null, DevicePorts, [address]), ct);
                    if (scan.Error is { } error) return new LanChange($"Couldn't check {address}: {error.TrimEnd('.')}.");
                    if (scan.Answering.Contains(address)) return new LanChange($"A device answers on {address}. Pick another address.");
                    pick = address;
                }
                var change = await networks.SetLanAddressAsync(container, pick, ct);
                if (!change.Ok) return new LanChange(change.Error);
                var (check, ok) = await CheckAsync(container, pick, block, taken, ct);
                return new LanChange(null, change.Done, check, ok);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            return new LanChange($"Couldn't give {container} its own address: {ex.Message}");
        }
    }

    /// <summary>Takes the container's own address away at once: devices using it lose it.</summary>
    public async Task<LanChange> RemoveAsync(string container, CancellationToken ct = default)
    {
        var change = await networks.SetLanAddressAsync(container, null, ct);
        return new LanChange(change.Error, change.Done);
    }

    /// <summary>How many candidates an automatic pick tries at once: a batch's worth, on each device port.</summary>
    private const int LanCheckFew = HostScanHelper.LanCheckBatch / 3;

    // After a change: the helper, on gitberth-lan, tries the address on the ports the container is known to listen on.
    private async Task<(string? Line, bool Ok)> CheckAsync(string container, string address, LanBlock block, HashSet<string> taken, CancellationToken ct)
    {
        var now = await networks.OwnAddressOfAsync(container, ct);
        if (!now.Running || now.Ports.Count == 0) return (null, true);
        taken.Add(address);
        if (HelperAddress(block, taken) is not { } at) return (null, true);
        var scan = await helpers.LanCheckAsync(new LanCheck(Name, at, null, null, now.Ports, [address]), ct);
        if (scan.Error is { } error) return ($"Couldn't check {address} ({error.TrimEnd('.')}).", false);
        var open = scan.Open.GetValueOrDefault(address) ?? [];
        var ports = string.Join(", ", now.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        return open.Count > 0
            ? ($"{address} answers on {string.Join(", ", open.Select(p => p.ToString(CultureInfo.InvariantCulture)))}.", true)
            : ($"Nothing answers on {address} on {ports} yet: it may listen on another port.", false);
    }

    // The host's network and Docker's networks, and why own LAN addresses can't be set up here. Docker failing throws.
    private async Task<(LanSubnet? Subnet, IReadOnlyList<NetworkInfo> Networks, string? Problem)> FactsAsync(CancellationToken ct)
    {
        var scan = await scanner.LatestAsync(ct);
        var all = await api.ListNetworksAsync(ct);
        var subnet = all.FirstOrDefault(n => n.Name == Name) is { } on ? LanSubnet.From(on, scan.Address) : LanSubnet.From(scan);
        var problem = !helpers.InContainer ? "Gitberth tests addresses with a helper container, which needs Gitberth to run in one."
            : subnet is null ? $"Gitberth couldn't read this host's network{(scan.Error is { } why ? $" ({why.TrimEnd('.')})" : "")}."
            : null;
        return (subnet, all, problem);
    }

    // The block gitberth-lan is on: the one saved, else its ip-range.
    private async Task<LanBlock?> BlockAsync(NetworkInfo info, CancellationToken ct)
    {
        var text = await settings.GetAsync(SettingKeys.OwnLanBlock, ct) ?? IpRangeOf(info);
        return text is not null && ParseBlock(text) is { } block ? block : null;
    }

    // gitberth-lan's ip-range, from its IPAM config: the block, when it was turned on with a CIDR.
    private static string? IpRangeOf(NetworkInfo info) => info.IpRanges?.FirstOrDefault(r => r.Contains('.', StringComparison.Ordinal));

    /// <summary>
    /// The addresses other containers hold on gitberth-lan (running, or kept while stopped), and the ones Gitberth keeps for
    /// containers and apps there, but <paramref name="except"/>'s; by address, who has it.
    /// </summary>
    private async Task<Dictionary<string, string>> HeldAsync(string? except, CancellationToken ct)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in await HoldersAsync(ct))
            if (h.Container != except) held.TryAdd(h.Address, h.Title);
        return held;
    }

    private async Task RemoveNetworkAsync()
    {
        try
        {
            await engine.RemoveNetworkAsync(Name, CancellationToken.None);
        }
        catch (EngineException ex) when (!ex.NotFound)
        {
            logger.LogWarning("Couldn't remove {Network} after its test failed: {Error}", Name, ex.Message);
        }
    }

    // The lines of a test, as Settings shows them: whether devices answer in the block, and whether the router does from it.
    private static LanTest Lines(LanBlock block, LanSubnet subnet, LanScan scan)
    {
        if (scan.Error is { } error) return new LanTest(block, [new LanTestLine(false, $"Couldn't test the block: {error.TrimEnd('.')}.")]);
        var answering = scan.Answering.Where(a => TryParse(a, out var ip) && block.Contains(ip)).OrderBy(Parse).ToList();
        return new LanTest(block,
        [
            answering.Count == 0
                ? new LanTestLine(true, $"No device answers on {block.Range}")
                : new LanTestLine(false, $"{(answering.Count == 1 ? "A device answers" : "Devices answer")} on {List(answering)}: pick a block no device uses."),
            scan.RouterAnswers == true
                ? new LanTestLine(true, "The router answers from the block")
                : new LanTestLine(false, $"The router {subnet.RouterText} doesn't answer from the block. A firewall between this host and the router may drop "
                    + $"addresses other than {subnet.HostText}: on Proxmox, the container's IP filter."),
        ]);
    }

    // 10.1.1.230, 10.1.1.231 and 3 more.
    private static string List(IReadOnlyList<string> addresses) =>
        addresses.Count <= 3 ? string.Join(", ", addresses) : $"{string.Join(", ", addresses.Take(3))} and {(addresses.Count - 3).ToString(CultureInfo.InvariantCulture)} more";

    /// <summary>
    /// Whether <paramref name="text"/> is a block Gitberth can have: a CIDR from /24 to /30 or a range of 4 to 256
    /// addresses, inside the host's network, without the network's own or broadcast address, the router, the host, or any
    /// of a Docker network's addresses. Returns the block, or why not.
    /// </summary>
    /// <param name="own">gitberth-lan, whose subnet is the host's own and doesn't count against it.</param>
    public static (LanBlock? Block, string? Problem) CheckBlock(string? text, LanSubnet subnet, IEnumerable<NetworkInfo> networks, string own = Network)
    {
        var example = Example(subnet);
        if (string.IsNullOrWhiteSpace(text)) return (null, $"Type a block, like {example}.");
        if (ParseBlock(text) is not { } block)
            return (null, text.Contains('/', StringComparison.Ordinal) && TryParseCidr(text.Trim(), out _, out var prefix) && prefix is < 24 or > 30
                ? $"A block is a /24 to a /30, like {example}."
                : Aligned(text.Trim()) is { } aligned ? $"{text.Trim()} doesn't start a block of its size: {aligned} does."
                : RangeSize(text) is long size && size is > 0 and (< 4 or > 256) ? "A range holds 4 to 256 addresses."
                : $"Write the block like {example}, or as a range like {Range(subnet, example)}.");
        if (!subnet.Contains(block.First) || !subnet.Contains(block.Last)) return (null, $"{block.Text} isn't in this host's network, {subnet.Cidr}.");
        if (block.Contains(subnet.Network)) return (null, $"{block.Text} holds {Text(subnet.Network)}, the network's own address.");
        if (block.Contains(subnet.Broadcast)) return (null, $"{block.Text} holds {Text(subnet.Broadcast)}, the network's broadcast address.");
        if (block.Contains(subnet.Router)) return (null, $"{block.Text} holds the router's address, {subnet.RouterText}.");
        if (block.Contains(subnet.Host)) return (null, $"{block.Text} holds this host's own address, {subnet.HostText}.");
        foreach (var n in networks.Where(n => n.Name != own && !n.Labels.ContainsKey(HostScanHelper.Label)))
            foreach (var s in n.Subnets)
                if (TryParseCidr(s, out var start, out var length) && Overlap(block, start, length))
                    return (null, $"{block.Text} overlaps Docker's network {n.Name} ({s}).");
        return (block, null);
    }

    /// <summary>A block as Settings writes it, a CIDR (/24 to /30, starting where one of its size starts) or a range of 4 to 256 addresses; null for anything else.</summary>
    public static LanBlock? ParseBlock(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Contains('/', StringComparison.Ordinal))
        {
            if (!TryParseCidr(text, out var start, out var prefix) || prefix is < 24 or > 30 || Aligned(text) is not null) return null;
            return new LanBlock(start, start | ~(uint.MaxValue << (32 - prefix)), prefix);
        }
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !TryParse(text[..dash].Trim(), out var first) || !TryParse(text[(dash + 1)..].Trim(), out var last) || last < first) return null;
        return (long)last - first + 1 is >= 4 and <= 256 ? new LanBlock(first, last, null) : null;
    }

    /// <summary>
    /// The highest /28 of the host's network, from <paramref name="lowest"/> up, that's a block Gitberth can have and where
    /// nothing answered; null when there's none.
    /// </summary>
    public static LanBlock? Suggest(LanSubnet subnet, IEnumerable<NetworkInfo> networks, IReadOnlySet<string> answering, uint lowest)
    {
        var all = networks.ToList();
        for (var start = (long)(subnet.Broadcast & ~15u); start >= Math.Max(subnet.Network, lowest); start -= 16)
            if (CheckBlock($"{Text((uint)start)}/28", subnet, all).Block is { } block && !block.Addresses.Any(a => answering.Contains(Text(a))))
                return block;
        return null;
    }

    /// <summary>
    /// The lowest address of the block that no container holds, nothing answered on, and the helper doesn't sit at; null
    /// when there's none.
    /// </summary>
    public static string? NextFree(LanBlock block, IReadOnlySet<string> held, IReadOnlySet<string> answering, string? helper) =>
        block.Addresses.Select(Text).FirstOrDefault(a => a != helper && !held.Contains(a) && !answering.Contains(a));

    /// <summary>
    /// Where the lan-check helper sits on gitberth-lan: the block's last address, else the highest one nobody has; null when
    /// every one is taken. Automatic addresses never take its place.
    /// </summary>
    public static string? HelperAddress(LanBlock block, IReadOnlySet<string> taken)
    {
        for (var ip = (long)block.Last; ip >= block.First; ip--)
            if (!taken.Contains(Text((uint)ip))) return Text((uint)ip);
        return null;
    }

    /// <summary>
    /// Why <paramref name="text"/> can't be a static own address, as far as Gitberth knows without trying it on the LAN:
    /// it has to be an address of the host's network (the block's or not), and not the network's own or broadcast address,
    /// the router's, the host's, or another container's. Null when it can.
    /// </summary>
    /// <param name="held">The addresses other containers have there, and who has each.</param>
    public static string? WhyNotStatic(string? text, LanSubnet subnet, IReadOnlyDictionary<string, string> held)
    {
        if (!TryParse(text?.Trim(), out var ip)) return $"Type an address of this host's network, {subnet.Cidr}.";
        var address = Text(ip);
        return !subnet.Contains(ip) ? $"{address} isn't in this host's network, {subnet.Cidr}."
            : ip == subnet.Network ? $"{address} is the network's own address."
            : ip == subnet.Broadcast ? $"{address} is the network's broadcast address."
            : ip == subnet.Router ? $"{address} is the router's."
            : ip == subnet.Host ? $"{address} is this host's own address."
            : held.TryGetValue(address, out var who) ? $"{address} is {who}'s own address."
            : null;
    }

    /// <summary>
    /// POST networks/create for an own-addresses network: ipvlan in L2 mode on the host's interface, in its network, with
    /// the router as gateway and <paramref name="ipRange"/> for Docker to give out. A null name is the lan-check's, which
    /// names it itself.
    /// </summary>
    internal static JsonObject NetworkBody(string? name, LanSubnet subnet, string ipRange, IReadOnlyDictionary<string, string> labels)
    {
        var body = new JsonObject
        {
            ["Driver"] = "ipvlan",
            ["Options"] = new JsonObject { ["parent"] = subnet.Interface, ["ipvlan_mode"] = "l2" },
            ["IPAM"] = new JsonObject
            {
                ["Driver"] = "default",
                ["Config"] = new JsonArray(new JsonObject { ["Subnet"] = subnet.Cidr, ["Gateway"] = subnet.RouterText, ["IPRange"] = ipRange }),
            },
            ["Labels"] = new JsonObject(labels.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)JsonValue.Create(l.Value)))),
        };
        if (name is not null) body["Name"] = name;
        return body;
    }

    /// <summary>
    /// The highest /28 of the host's network that holds neither its own, broadcast, router's or host's address, to show the
    /// format with; nothing is tried on the LAN for it. A network smaller than a /28 shows itself.
    /// </summary>
    public static string Example(LanSubnet subnet)
    {
        for (var start = (long)(subnet.Broadcast & ~15u); start >= subnet.Network; start -= 16)
            if (start > subnet.Network && start + 15 < subnet.Broadcast && !(subnet.Router >= start && subnet.Router <= start + 15)
                && !(subnet.Host >= start && subnet.Host <= start + 15))
                return $"{Text((uint)start)}/28";
        return subnet.Cidr;
    }

    // The example as a range: 10.1.1.224-10.1.1.239.
    private static string Range(LanSubnet subnet, string example) => ParseBlock(example)?.Range ?? subnet.Cidr;

    // Where a misaligned CIDR's block starts: 10.1.1.224/28 for 10.1.1.230/28; null when it's aligned, or isn't one.
    private static string? Aligned(string text)
    {
        if (!TryParseCidr(text, out var start, out var prefix) || prefix is < 24 or > 30) return null;
        var mask = uint.MaxValue << (32 - prefix);
        return (start & mask) == start ? null : $"{Text(start & mask)}/{prefix.ToString(CultureInfo.InvariantCulture)}";
    }

    // How many addresses a range "a-b" has; null when it isn't one.
    private static long? RangeSize(string text)
    {
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && TryParse(text[..dash].Trim(), out var first) && TryParse(text[(dash + 1)..].Trim(), out var last) && last >= first ? (long)last - first + 1 : null;
    }

    private static bool Overlap(LanBlock block, uint start, int prefix)
    {
        var end = prefix == 0 ? uint.MaxValue : start | ~(uint.MaxValue << (32 - prefix));
        return block.First <= end && start <= block.Last;
    }

    /// <summary>An IPv4 address as four numbers from 0 to 255, and nothing else.</summary>
    public static bool TryParse(string? text, out uint ip)
    {
        ip = 0;
        var parts = text?.Split('.') ?? [];
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return false;
            ip = ip << 8 | b;
        }
        return true;
    }

    /// <summary>10.1.1.0/24 as its first address and prefix length; the address needn't start the range.</summary>
    public static bool TryParseCidr(string? text, out uint start, out int prefix)
    {
        (start, prefix) = (0, 0);
        var slash = text?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash > 0 && TryParse(text![..slash], out start)
            && int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) && prefix is >= 0 and <= 32;
    }

    public static string Text(uint ip) =>
        string.Join('.', new[] { ip >> 24, (ip >> 16) & 255, (ip >> 8) & 255, ip & 255 }.Select(b => b.ToString(CultureInfo.InvariantCulture)));

    // For sorting addresses as numbers; one that isn't an address sorts first.
    private static uint Parse(string address) => TryParse(address, out var ip) ? ip : 0;
}
