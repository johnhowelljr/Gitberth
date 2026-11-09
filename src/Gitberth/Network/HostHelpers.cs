using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Network;

/// <summary>
/// The "probe" and "check" helpers (8.2), a port change's last checks, and the "lan-check" own LAN addresses use:
/// throwaway containers of Gitberth's own image (so nothing is pulled), each run under a name of its own with the label
/// gitberth.helper, read-only, without capabilities, with 32 MiB of memory and no volume, and removed as soon as it has
/// answered. The probe binds the new host ports for a moment before anything stops; the check connects to a LAN address
/// from the host's own network, as a PC would; the lan-check finds which LAN addresses answer, from an own-addresses
/// network or a temporary one it makes and removes. Ones a restart of Gitberth left behind are removed at startup, the
/// lan-check's networks with them. Outside a container (development) Gitberth has no image of its own: the probe and the
/// lan-check are left out, and the check connects from Gitberth itself. None throws when Docker fails: the probe then says
/// nothing (the new container's start still finds a taken port), and the others say they couldn't tell.
/// </summary>
public sealed class HostHelpers(IContainerEngine engine, GitberthOptions options, ILogger<HostHelpers> logger) : IHostPortProbe, ILanCheck
{
    // Helpers made before this are a restart's leftovers; the ones this run makes come after it.
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow;
    private OwnImage? _image;

    /// <summary>Gitberth's own image, by its ID, and the paths it declares as VOLUME.</summary>
    private sealed record OwnImage(string Id, IReadOnlyList<string> Volumes);

    /// <summary>What the helpers' names start with; the real-engine test gives its own, which its guard lets through.</summary>
    internal string NamePrefix { get; init; } = HostScanHelper.NamePrefix;

    /// <summary>How long one helper may take, from its create to its exit. Each reads or binds a few ports and is gone in about a second.</summary>
    internal TimeSpan HelperTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Whether Gitberth runs in a container, whose image the helpers run.</summary>
    internal bool InContainer { get; init; } = options.RunningInContainer;

    /// <summary>How long the check gives a connect: the helper's own timeout.</summary>
    internal static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(3);

    /// <summary>Looks a name up from Gitberth's own container; tests give their own answers.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;

    public async Task<string?> ProbeAsync(IReadOnlyList<ProbePort> ports, CancellationToken ct)
    {
        if (ports.Count == 0 || !InContainer) return null;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(HelperTimeout);
        var name = HostScanHelper.NewName(HostScanHelper.ProbeKind, NamePrefix);
        var made = false;
        try
        {
            if (await ImageAsync(timer.Token) is not { } image)
            {
                logger.LogInformation("Couldn't probe host ports: Gitberth couldn't find its own container's image");
                return null;
            }
            // Made from here on, as far as removing goes: a create whose answer was lost made it too.
            made = true;
            var id = await engine.CreateAsync(name, HostScanHelper.ProbeBody(image.Id, image.Volumes, ports), timer.Token);
            try
            {
                await engine.StartAsync(id, timer.Token);
                return null;
            }
            catch (EngineException ex) when (PortChanger.PortTaken(ex.Message) is not null)
            {
                return ex.Message;
            }
        }
        catch (Exception ex) when (ex is EngineException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Couldn't probe host ports {Ports} ({Reason})", string.Join(", ", ports.Select(p => ContainerPorts.Address(p.HostIp.Length == 0 ? "*" : p.HostIp, p.HostPort))),
                ex is OperationCanceledException ? $"the probe didn't finish within {HelperTimeout.TotalSeconds:0} s" : ex.Message);
            return null;
        }
        finally
        {
            // By its name, which is this run's alone.
            if (made) await RemoveAsync(name);
        }
    }

    /// <summary>
    /// Connects to <paramref name="address"/>:<paramref name="port"/> from the host's own network. A name is looked up
    /// first, from Gitberth: bash exits 1 for a name it can't resolve just as for a refused connect, so one that doesn't
    /// resolve would read as nothing answering, and undo a change that works. It's a check that couldn't run instead.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(string address, int port, CancellationToken ct)
    {
        if (!InContainer) return await ConnectHereAsync(address, port, ct);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(HelperTimeout);
        var name = HostScanHelper.NewName(HostScanHelper.CheckKind, NamePrefix);
        var made = false;
        try
        {
            if (await AddressOfAsync(address, timer.Token) is not { } ip) return new ConnectResult(false, $"Gitberth can't resolve {address}");
            if (await ImageAsync(timer.Token) is not { } image) return new ConnectResult(false, "Gitberth couldn't find its own container's image");
            made = true;
            var id = await engine.CreateAsync(name, HostScanHelper.CheckBody(image.Id, image.Volumes, ip, port), timer.Token);
            await engine.StartAsync(id, timer.Token);
            return Answer(await engine.WaitAsync(id, timer.Token));
        }
        catch (Exception ex) when (ex is EngineException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new ConnectResult(false, ex is OperationCanceledException ? $"the check didn't finish within {HelperTimeout.TotalSeconds:0} s" : ex.Message);
        }
        finally
        {
            if (made) await RemoveAsync(name);
        }
    }

    // An address as it is; a name as the address it resolves to, IPv4 first (the LAN's ports may be published on IPv4 only).
    // Null when it doesn't resolve.
    private async Task<string?> AddressOfAsync(string address, CancellationToken ct)
    {
        if (IPAddress.TryParse(address.Trim('[', ']'), out var literal)) return literal.ToString();
        try
        {
            var found = await Resolve(address, ct);
            return (found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault())?.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            logger.LogDebug(ex, "Couldn't resolve {Address}", address);
            return null;
        }
    }

    /// <summary>What the check's exit code says: 0 something answered; 1 (refused, unreachable) or 124 (timed out) nothing did; anything else, that it couldn't run.</summary>
    internal static ConnectResult Answer(int exitCode) => exitCode switch
    {
        0 => ConnectResult.Yes,
        1 or 124 => ConnectResult.No,
        _ => new ConnectResult(false, $"the check helper exited with code {exitCode}"),
    };

    /// <summary>
    /// Runs the lan-check helper (<see cref="HostScanHelper.LanCheckBody"/>) and reads what it found. With
    /// <see cref="LanCheck.Temporary"/> it first makes that network under a name of its own, labelled as the helper is, and
    /// removes it after; it takes as long as its batches of connects, and a few seconds more.
    /// </summary>
    public async Task<LanScan> LanCheckAsync(LanCheck check, CancellationToken ct)
    {
        if (!InContainer) return LanScan.Failed("the check needs a helper container, and Gitberth isn't running in one here");
        var batches = (check.Targets.Count * check.Ports.Count + (check.Router is null ? 0 : 3) + HostScanHelper.LanCheckBatch - 1) / HostScanHelper.LanCheckBatch;
        var allowed = LanCheckTimeout + TimeSpan.FromSeconds(HostScanHelper.LanCheckWait * Math.Max(1, batches));
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(allowed);
        var name = HostScanHelper.NewName(HostScanHelper.LanCheckKind, NamePrefix);
        var network = check.Network ?? (check.Temporary is null ? "host" : name);
        var (made, madeNetwork) = (false, false);
        try
        {
            if (await ImageAsync(timer.Token) is not { } image) return LanScan.Failed("Gitberth couldn't find its own container's image");
            if (check.Temporary is { } temporary)
            {
                var body = (JsonObject)temporary.DeepClone();
                body["Name"] = network;
                if (body["Labels"] is not JsonObject labels) body["Labels"] = labels = new JsonObject();
                labels[HostScanHelper.Label] = HostScanHelper.LanCheckKind;
                madeNetwork = true;
                await engine.CreateNetworkAsync(body, timer.Token);
            }
            made = true;
            var id = await engine.CreateAsync(name, HostScanHelper.LanCheckBody(image.Id, image.Volumes, network, check.Address, check.Router, check.Ports, check.Targets), timer.Token);
            await engine.StartAsync(id, timer.Token);
            var exit = await engine.WaitAsync(id, timer.Token);
            if (exit != 0) return LanScan.Failed(exit == 137 ? "the check helper ran out of memory" : $"the check helper exited with code {exit}");
            return LanScan.Read(LogStream.Text(await engine.LogsAsync(id, LanCheckLines, timer.Token), tty: false), check.Router);
        }
        catch (Exception ex) when (ex is EngineException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return LanScan.Failed(ex is OperationCanceledException ? $"the check didn't finish within {allowed.TotalSeconds:0} s" : ex.Message);
        }
        finally
        {
            // The container first: a network with a running container on it can't go.
            if (made) await RemoveAsync(name);
            if (madeNetwork) await RemoveNetworkAsync(network);
        }
    }

    /// <summary>How long a lan-check may take besides its batches of connects: its network, its container, and their removal.</summary>
    internal TimeSpan LanCheckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // The most of a lan-check's log read back: an open port a line, then the ARP table, an address a line.
    private const int LanCheckLines = 20_000;

    /// <summary>
    /// Removes the probe, check and lan-check helpers a restart of Gitberth left behind, then the lan-check's networks it
    /// left; never one of this run's.
    /// </summary>
    public async Task RemoveLeftoversAsync(CancellationToken ct)
    {
        var leftovers = HostScanHelper.Leftovers(await engine.ListContainersAsync(ct), _since);
        if (leftovers.Count > 0)
        {
            logger.LogInformation("Removing {Count} helper container(s) left behind", leftovers.Count);
            foreach (var id in leftovers) await RemoveAsync(id);
        }
        var networks = HostScanHelper.LeftoverNetworks(await engine.ListNetworksAsync(ct), _since);
        if (networks.Count == 0) return;
        logger.LogInformation("Removing {Count} helper network(s) left behind", networks.Count);
        foreach (var id in networks) await RemoveNetworkAsync(id);
    }

    // Outside a container Gitberth is a program on some machine, usually one on the LAN: it connects itself.
    private static async Task<ConnectResult> ConnectHereAsync(string address, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(ConnectWait);
        try
        {
            await client.ConnectAsync(address, port, timer.Token);
            return ConnectResult.Yes;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
        {
            return new ConnectResult(false, ex.Message);
        }
        catch (SocketException)
        {
            return ConnectResult.No;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectResult.No;
        }
    }

    // With its own time: the change's may be up. One that can't be removed now goes at the next startup.
    private async Task RemoveAsync(string idOrName)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await engine.RemoveAsync(idOrName, force: true, timer.Token);
        }
        catch (EngineException ex) when (ex.NotFound)
        {
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't remove helper container {Container}", idOrName);
        }
    }

    // As for a container: the next startup tries again.
    private async Task RemoveNetworkAsync(string idOrName)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await engine.RemoveNetworkAsync(idOrName, timer.Token);
        }
        catch (EngineException ex) when (ex.NotFound)
        {
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException)
        {
            logger.LogWarning("Couldn't remove the helper network {Network} ({Reason}); it goes at the next start", idOrName, ex.Message);
        }
    }

    /// <summary>
    /// Gitberth's own image, which the helpers run; the real-engine test gives one, since it runs in another image.
    /// </summary>
    internal string? Image { get; init; }

    /// <summary>
    /// The image of Gitberth's own container, by its ID, and the paths it declares as VOLUME, which each helper gets as a
    /// tmpfs. Gitberth's hostname is its container's short ID unless it was set otherwise; then its ID comes from the
    /// container's own files in /proc/self/mountinfo.
    /// </summary>
    private async Task<OwnImage?> ImageAsync(CancellationToken ct)
    {
        if (_image is { } known) return known;
        if (Image is { } given && await engine.InspectImageAsync(given, ct) is { } image)
            return _image = new OwnImage(ContainerPorts.Str(image, "Id"), image["Config"]?["Volumes"] is JsonObject paths ? paths.Select(v => v.Key).ToList() : []);
        foreach (var self in new[] { Environment.MachineName, HostScanner.OwnIdFromMounts() }.OfType<string>().Distinct())
        {
            if (await engine.InspectContainerAsync(self, ct) is not { } inspect || ContainerPorts.Str(inspect, "Image") is not { Length: > 0 } id) continue;
            var volumes = (await engine.InspectImageAsync(id, ct))?["Config"]?["Volumes"] is JsonObject declared ? declared.Select(v => v.Key).ToList() : [];
            return _image = new OwnImage(id, volumes);
        }
        return null;
    }
}
