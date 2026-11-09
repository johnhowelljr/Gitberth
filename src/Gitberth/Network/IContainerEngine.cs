using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Gitberth.Network;

/// <summary>
/// The calls to the Docker Engine API that recreating a container needs, and that Gitberth's helpers need, kept close to
/// the API: JSON as Docker writes it, and Docker's own error messages (<see cref="EngineException"/>). Inspect and create
/// must go through the same negotiated API version, so a clone carries every field the engine knows about. Nothing here
/// removes a volume.
/// </summary>
public interface IContainerEngine
{
    /// <summary>GET /version: the engine's version and the API version every other call uses.</summary>
    Task<EngineVersion> VersionAsync(CancellationToken ct);

    /// <summary>
    /// GET /containers/{id}/json, as Docker wrote it; null only for Docker's 404, when there's no such container. Every
    /// other failure (a timeout, a 500, a broken socket) is thrown: a container Docker didn't answer for isn't gone.
    /// </summary>
    Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct);

    /// <summary>GET /images/{name}/json; null when there's no such image or tag.</summary>
    Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct);

    /// <summary>GET /containers/json?all=true: every container, stopped ones too.</summary>
    Task<JsonArray> ListContainersAsync(CancellationToken ct);

    /// <summary>GET /networks: every network that exists, each with its Name and Driver.</summary>
    Task<JsonArray> ListNetworksAsync(CancellationToken ct);

    /// <summary>POST /containers/create?name=: creates it without starting it, and returns its ID.</summary>
    Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct);

    /// <summary>POST /containers/{id}/update with only {"RestartPolicy": policy}.</summary>
    Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct);

    /// <summary>
    /// POST /containers/{id}/update with <paramref name="resources"/> (docker update): its limits and restart policy change
    /// while it runs, without a restart. Docker reads a 0 as "unchanged". Returns Docker's answer, {"Warnings":[…]}.
    /// </summary>
    Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct);

    /// <summary>
    /// The memory a running container uses now, as `docker stats` counts it (page cache left out); null when it isn't
    /// running or the engine doesn't say. Only for a plan's line: a failure is the caller's to ignore.
    /// </summary>
    Task<ulong?> MemoryUsageAsync(string id, CancellationToken ct) => Task.FromResult<ulong?>(null);

    /// <summary>GET /info: the host's CPUs and memory, for the limits a setting may ask for; null when the engine doesn't say.</summary>
    Task<EngineHost?> HostAsync(CancellationToken ct) => Task.FromResult<EngineHost?>(null);

    /// <summary>
    /// POST /containers/{id}/stop?t=: its own stop signal, then SIGKILL once the timeout has passed; t=0 kills it at once,
    /// and a negative t never does (Docker waits for the program however long it takes, as --stop-timeout -1 asks). Waits
    /// up to the timeout plus 15 s for Docker's answer, and up to ten minutes for a negative one. A container that isn't
    /// running counts as stopped (Docker's 304). Docker finishes a stop even after its caller has gone.
    /// </summary>
    Task StopAsync(string id, int timeoutSeconds, CancellationToken ct);

    /// <summary>POST /containers/{id}/rename?name=. Another container with that name is a 409.</summary>
    Task RenameAsync(string id, string name, CancellationToken ct);

    /// <summary>POST /containers/{id}/start. A container that's running already counts as started (Docker's 304).</summary>
    Task StartAsync(string id, CancellationToken ct);

    /// <summary>POST /containers/{id}/wait?condition=not-running: its exit code, once it isn't running.</summary>
    Task<int> WaitAsync(string id, CancellationToken ct);

    /// <summary>
    /// GET /containers/{id}/logs?stdout=1&amp;stderr=1&amp;tail=: the last lines, as the bytes Docker sent. Docker frames
    /// them only for a container without a TTY; <see cref="LogStream.Text"/> reads either.
    /// </summary>
    Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct);

    /// <summary>GET /containers/{id}/changes: the files written outside its volumes, as [{"Path","Kind"}]; empty for none.</summary>
    Task<JsonArray> ChangesAsync(string id, CancellationToken ct);

    /// <summary>
    /// DELETE /containers/{id}, never with v: the container goes, its volumes (anonymous ones too) always stay.
    /// <paramref name="force"/> kills it first when it runs.
    /// </summary>
    Task RemoveAsync(string id, bool force, CancellationToken ct);

    /// <summary>GET /volumes/{name}: the volume (its CreatedAt, Labels…), or null when there's none. Only for a plan's line.</summary>
    Task<JsonObject?> InspectVolumeAsync(string name, CancellationToken ct) => Task.FromResult<JsonObject?>(null);

    /// <summary>POST /networks/create: a network as <paramref name="body"/> describes it, like the lan-check's own. Returns its ID.</summary>
    Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct);

    /// <summary>DELETE /networks/{id}: the one network, once no running container is on it. Never a prune.</summary>
    Task RemoveNetworkAsync(string idOrName, CancellationToken ct);
}

/// <param name="Version">The engine's own version: 29.9.2.</param>
/// <param name="ApiVersion">The API version calls use, the lower of the engine's and Gitberth's newest: 1.56.</param>
public sealed record EngineVersion(string Version, string ApiVersion)
{
    /// <summary>Whether the API version is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    public bool AtLeast(int major, int minor)
    {
        var parts = ApiVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return false;
        return a > major || (a == major && b >= minor);
    }
}

/// <param name="Cpus">The host's CPUs, as Docker counts them (NCPU).</param>
/// <param name="MemoryBytes">The host's memory (MemTotal).</param>
public sealed record EngineHost(int Cpus, long MemoryBytes);

/// <summary>Docker's answer to a call that failed: its HTTP status and its {"message"}. Status 0 when no answer came.</summary>
public sealed class EngineException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public bool NotFound => Status == 404;

    /// <summary>A name that's taken: Docker's 409 on create and rename.</summary>
    public bool Conflict => Status == 409;
}

/// <summary>The bytes of GET /containers/{id}/logs as text.</summary>
public static class LogStream
{
    /// <summary>
    /// The text of a log stream. Without a TTY Docker sends stdout and stderr in frames: one byte for the stream, three of
    /// padding, a big-endian length, then that many bytes. With a TTY it sends the bytes as the program wrote them, and
    /// reading those as frames would garble them.
    /// </summary>
    public static string Text(ReadOnlySpan<byte> raw, bool tty)
    {
        if (tty) return Encoding.UTF8.GetString(raw);
        var text = new StringBuilder();
        var rest = raw;
        while (rest.Length >= 8)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(rest[4..8]);
            // Not a frame after all (a stream Docker didn't frame): read it as it is.
            if (rest[0] > 2 || rest[1] != 0 || rest[2] != 0 || rest[3] != 0 || size > rest.Length - 8) return Encoding.UTF8.GetString(raw);
            text.Append(Encoding.UTF8.GetString(rest.Slice(8, (int)size)));
            rest = rest[(8 + (int)size)..];
        }
        return rest.Length == 0 ? text.ToString() : Encoding.UTF8.GetString(raw);
    }
}
