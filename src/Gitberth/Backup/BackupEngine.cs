using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Backup;

/// <summary>What a command run in a container (docker exec) ended with: its exit code, and the end of what it printed on stderr.</summary>
public sealed record ExecResult(int ExitCode, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// What saving and restoring ask of Docker: <see cref="DockerApi"/>'s calls, through the Engine API only (the CLI can't
/// carry binary streams). Archives, exec output and image tars are streamed, never held whole. A stand-in in tests.
/// </summary>
public interface IBackupEngine
{
    Task<Version> ApiVersionAsync(CancellationToken ct = default);

    /// <summary>The engine's own version, such as 29.0.1.</summary>
    Task<string> EngineVersionAsync(CancellationToken ct = default);

    /// <summary>GET info, as Docker wrote it (Architecture, Name, ServerVersion…).</summary>
    Task<JsonObject> InfoAsync(CancellationToken ct = default);

    /// <summary>Every container, stopped ones too.</summary>
    Task<List<RunningContainer>> ListAllAsync(CancellationToken ct = default);

    /// <summary>A container's inspect; null when there's none.</summary>
    Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct = default);

    /// <summary>An image's inspect; null when there's none.</summary>
    Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct = default);

    /// <summary>GET volumes: every volume, as Docker lists them (Name, Driver, Labels, Options…).</summary>
    Task<JsonArray> ListVolumesAsync(CancellationToken ct = default);

    /// <summary>Each volume's size (system/df), by name; empty when the engine doesn't say. Slow on big volumes.</summary>
    Task<IReadOnlyDictionary<string, long>> VolumeSizesAsync(CancellationToken ct = default);

    /// <summary>GET networks: every network as Docker describes it (Driver, Options, IPAM, Labels…).</summary>
    Task<JsonArray> ListNetworksJsonAsync(CancellationToken ct = default);

    Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct = default);
    Task StartContainerAsync(string idOrName, CancellationToken ct = default);
    Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct = default);
    Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct = default);
    Task PauseContainerAsync(string idOrName, CancellationToken ct = default);
    Task UnpauseContainerAsync(string idOrName, CancellationToken ct = default);

    /// <summary>POST volumes/create: {Name, Driver, DriverOpts, Labels}.</summary>
    Task CreateVolumeAsync(JsonObject body, CancellationToken ct = default);

    /// <summary>POST networks/create. Returns its ID.</summary>
    Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct = default);

    /// <summary>GET containers/{id}/archive?path=: a tar of the path, streamed; disposing it lets go of the connection.</summary>
    Task<Stream> GetArchiveAsync(string container, string path, CancellationToken ct = default);

    /// <summary>PUT containers/{id}/archive?path=: the tar extracted at the path (ownership as the tar has it).</summary>
    Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct = default);

    /// <summary>
    /// docker exec: <paramref name="command"/> in the container, its environment from <paramref name="env"/> (never on its
    /// command line), <paramref name="stdin"/> fed to it and then closed, its stdout written to <paramref name="stdout"/> as it
    /// comes. Returns its exit code and the end of its stderr.
    /// </summary>
    Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? env, Stream? stdin, Stream? stdout,
        CancellationToken ct = default);

    /// <summary>GET images/get?names=: the images as docker save writes them, streamed.</summary>
    Task<Stream> SaveImagesAsync(IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>POST images/load: a docker save tar loaded. Returns what Docker said it loaded.</summary>
    Task<string> LoadImagesAsync(Stream tar, CancellationToken ct = default);

    /// <summary>POST images/create: pulls the image; a failure is a <see cref="DockerApiException"/>.</summary>
    Task PullImageAsync(string fromImage, string tagOrDigest, string? registryAuth, CancellationToken ct = default);

    /// <summary>POST images/{name}/tag: another name for an image.</summary>
    Task TagImageAsync(string image, string repository, string tag, CancellationToken ct = default);

    /// <summary>
    /// The digest a registry gives the reference (GET distribution/{ref}/json, anonymously); null when no registry has it,
    /// it refuses, or it can't be reached: such an image is saved whole.
    /// </summary>
    Task<string?> RegistryDigestAsync(string reference, CancellationToken ct = default);
}
