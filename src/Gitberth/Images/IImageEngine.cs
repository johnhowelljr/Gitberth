using System.Text.Json.Nodes;

namespace Gitberth.Images;

/// <summary>
/// The image calls of the Docker Engine API (<see cref="Gitberth.Docker.DockerApi"/>'s), as Docker answers them: images,
/// Docker Hub's search, the registry's view of a reference, pulls, sign-ins and removals. Docker's errors come as
/// <see cref="Gitberth.Docker.DockerApiException"/>; one that didn't answer as a <see cref="TimeoutException"/>. Nothing
/// here removes a container or a volume.
/// </summary>
public interface IImageEngine
{
    /// <summary>GET /images/json: every image, dangling ones too.</summary>
    Task<JsonArray> ListImagesAsync(CancellationToken ct);

    /// <summary>GET /containers/json?all=true: to see which images containers use.</summary>
    Task<JsonArray> ListContainersAsync(CancellationToken ct);

    /// <summary>GET /images/{name}/json; null when there's no such image or tag.</summary>
    Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct);

    /// <summary>GET /images/search: Docker Hub's search, through the daemon.</summary>
    Task<JsonArray> SearchImagesAsync(string term, int limit, CancellationToken ct);

    /// <summary>GET /distribution/{reference}/json: the registry's descriptor and platforms for it, signed in with <paramref name="registryAuth"/>.</summary>
    Task<JsonObject> DistributionAsync(string reference, string? registryAuth, CancellationToken ct);

    /// <summary>POST /images/create, one JSON line at a time as Docker sends them; no overall timeout, but 120 s at most between lines.</summary>
    IAsyncEnumerable<JsonObject> PullAsync(string fromImage, string tagOrDigest, string? platform, string? registryAuth, CancellationToken ct);

    /// <summary>POST /auth: whether the registry takes the credentials.</summary>
    Task<JsonObject> AuthAsync(JsonObject credentials, CancellationToken ct);

    /// <summary>DELETE /images/{id}, never forced: Docker refuses one a container uses.</summary>
    Task<JsonArray> RemoveImageAsync(string idOrName, CancellationToken ct);

    /// <summary>GET /info, as Docker wrote it.</summary>
    Task<JsonObject> InfoAsync(CancellationToken ct);
}
