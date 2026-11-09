using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Gitberth.Network;

/// <summary>
/// Whether a container answers on a port by name (INameCheck): a TCP connect to name:port from Gitberth's own container,
/// over a network the two share. Docker's default bridge gives no names, so for a container that's only there, or for
/// Gitberth outside a container, it can't tell (null); nor can it while Docker's DNS doesn't know the name, as in the
/// middle of a swap. Docker not answering is "can't tell" too: this never fails a change.
/// </summary>
public sealed class NameCheck(IContainerEngine engine, GitberthOptions options) : INameCheck
{
    // Whether each container shares a network with Gitberth, for a while: a change asks again every quarter second.
    private static readonly TimeSpan Kept = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (DateTime At, bool Shared)> _shared = new(StringComparer.Ordinal);

    /// <summary>How long a connect is given.</summary>
    internal TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<bool?> AnswersAsync(string container, int port, CancellationToken ct)
    {
        if (!options.RunningInContainer) return null;
        try
        {
            if (!await SharesAsync(container, ct)) return null;
        }
        catch (EngineException)
        {
            return null;
        }
        using var client = new TcpClient();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(ConnectWait);
        try
        {
            await client.ConnectAsync(container, port, timer.Token);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
        {
            return null;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> SharesAsync(string container, CancellationToken ct)
    {
        if (_shared.TryGetValue(container, out var known) && DateTime.UtcNow - known.At < Kept) return known.Shared;
        // By its ID: its hostname names its container only unless it was started with another.
        var self = await engine.InspectContainerAsync(options.OwnContainer ?? Environment.MachineName, ct);
        var target = await engine.InspectContainerAsync(container, ct);
        var shared = self is not null && target is not null && Named(self).Overlaps(Named(target));
        _shared[container] = (DateTime.UtcNow, shared);
        return shared;
    }

    /// <summary>The networks a container is on where Docker's DNS gives names: not the default bridge, host or none.</summary>
    internal static HashSet<string> Named(JsonObject inspect) =>
        (inspect["NetworkSettings"]?["Networks"] as JsonObject ?? []).Select(n => n.Key)
            .Where(n => n is not ("bridge" or "host" or "none"))
            .ToHashSet(StringComparer.Ordinal);
}
