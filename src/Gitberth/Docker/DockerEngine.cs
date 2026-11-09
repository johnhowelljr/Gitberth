using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Network;

namespace Gitberth.Docker;

/// <summary>
/// The calls a recreate makes (<see cref="IContainerEngine"/>), over the Engine API client: Docker's answers as raw JSON,
/// every path on the one API version asked for once, so an inspect and the create built from it speak the same version.
/// Docker's errors come as <see cref="EngineException"/> with its status and message, and an answer that never came
/// (a timeout, a broken socket) with status 0. Inspect is null only for Docker's 404. Changes are refused below API 1.44,
/// and nothing here removes a volume.
/// </summary>
public sealed class DockerEngine(DockerApi api) : IContainerEngine
{
    public Task<EngineVersion> VersionAsync(CancellationToken ct) => CallAsync(async () =>
    {
        var version = await api.ApiVersionAsync(ct);
        return new EngineVersion(await api.EngineVersionAsync(ct), version.ToString(2));
    });

    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => CallAsync(() => api.InspectContainerAsync(idOrName, ct));

    public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct) => CallAsync(() => api.InspectImageAsync(nameOrId, ct));

    public Task<JsonArray> ListContainersAsync(CancellationToken ct) =>
        CallAsync(async () => JsonNode.Parse(await api.SendAsync(HttpMethod.Get, "containers/json?all=true", ct: ct)) as JsonArray ?? []);

    public Task<JsonArray> ListNetworksAsync(CancellationToken ct) =>
        CallAsync(async () => JsonNode.Parse(await api.SendAsync(HttpMethod.Get, "networks", ct: ct)) as JsonArray ?? []);

    public Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct) => CallAsync(() => api.CreateContainerAsync(name, body, ct));

    public Task UpdateRestartPolicyAsync(string id, JsonObject policy, CancellationToken ct) => CallAsync(() => api.UpdateRestartPolicyAsync(id, policy, ct));

    public Task<JsonObject> UpdateAsync(string id, JsonObject resources, CancellationToken ct) => CallAsync(() => api.UpdateContainerAsync(id, resources, ct));

    // Only a running container has a reading; one Docker can't read for gives none.
    public async Task<ulong?> MemoryUsageAsync(string id, CancellationToken ct)
    {
        try
        {
            var reading = await api.GetStatsAsync(id, ct);
            return reading.MemoryBytes > 0 ? reading.MemoryBytes : null;
        }
        catch (Exception ex) when (ex is DockerApiException or TimeoutException or HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    public async Task<EngineHost?> HostAsync(CancellationToken ct)
    {
        try
        {
            var info = await api.GetHostInfoAsync(ct);
            return info.Cpus > 0 ? new EngineHost(info.Cpus, (long)Math.Min(info.MemoryBytes, long.MaxValue)) : null;
        }
        catch (Exception ex) when (ex is DockerApiException or TimeoutException or HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    public Task StopAsync(string id, int timeoutSeconds, CancellationToken ct) => CallAsync(() => api.StopContainerAsync(id, timeoutSeconds, ct));

    public Task RenameAsync(string id, string name, CancellationToken ct) => CallAsync(() => api.RenameContainerAsync(id, name, ct));

    public Task StartAsync(string id, CancellationToken ct) => CallAsync(() => api.StartContainerAsync(id, ct));

    public Task<int> WaitAsync(string id, CancellationToken ct) => CallAsync(() => api.WaitContainerAsync(id, ct));

    public Task<byte[]> LogsAsync(string id, int tail, CancellationToken ct) => CallAsync(() => api.ContainerLogsAsync(id, tail, ct));

    public Task<JsonArray> ChangesAsync(string id, CancellationToken ct) => CallAsync(() => api.ContainerChangesAsync(id, ct));

    public Task RemoveAsync(string id, bool force, CancellationToken ct) => CallAsync(() => api.RemoveContainerAsync(id, force, ct));

    public async Task<JsonObject?> InspectVolumeAsync(string name, CancellationToken ct)
    {
        try
        {
            return await CallAsync(async () => JsonNode.Parse(await api.SendAsync(HttpMethod.Get, $"volumes/{Uri.EscapeDataString(name)}", ct: ct)) as JsonObject);
        }
        catch (EngineException ex) when (ex.NotFound)
        {
            return null;
        }
    }

    public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct) => CallAsync(() => api.CreateNetworkAsync(body, ct));

    public Task RemoveNetworkAsync(string idOrName, CancellationToken ct) => CallAsync(() => api.RemoveNetworkAsync(idOrName, ct));

    private static async Task CallAsync(Func<Task> call) => await CallAsync(async () =>
    {
        await call();
        return true;
    });

    // Docker's answer in the engine's terms. A cancellation the caller asked for stays one.
    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (DockerApiException ex)
        {
            throw new EngineException(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or IOException or JsonException)
        {
            throw new EngineException(0, ex.Message);
        }
    }
}
