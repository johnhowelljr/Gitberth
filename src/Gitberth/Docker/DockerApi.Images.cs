using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Images;

namespace Gitberth.Docker;

/// <summary>
/// The image calls: the images on the host, Docker Hub's search, what a registry says about a reference, pulls that stream
/// their progress, sign-ins and removals. Credentials go in X-Registry-Auth, per call, and are never logged. Nothing here
/// removes a container or a volume, and an image is never removed by force.
/// </summary>
public sealed partial class DockerApi : IImageEngine
{
    /// <summary>The header Docker reads a registry's credentials from: base64url JSON with username, password and serveraddress.</summary>
    public const string RegistryAuthHeader = "X-Registry-Auth";

    /// <summary>How long a pull may go without a line from Docker before Gitberth gives up on it. A pull as a whole has no limit.</summary>
    public static readonly TimeSpan PullIdle = TimeSpan.FromSeconds(120);

    /// <summary>How long a call that goes on to a registry may take: Docker asks the registry before it answers.</summary>
    public static readonly TimeSpan RegistryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>GET images/json: every image on the host, dangling ones too, as Docker wrote them.</summary>
    public async Task<JsonArray> ListImagesAsync(CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, "images/json", ct: ct)) as JsonArray ?? [];

    /// <summary>GET images/search: Docker Hub's search, which the daemon makes; name, description, star_count and is_official per result.</summary>
    public async Task<JsonArray> SearchImagesAsync(string term, int limit = 25, CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, $"images/search?term={Uri.EscapeDataString(term)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            timeout: RegistryTimeout, ct: ct)) as JsonArray ?? [];

    /// <summary>
    /// GET distribution/{reference}/json: the registry's descriptor for the reference (the digest a pull gets) and its
    /// platforms, without pulling it. A private one needs <paramref name="registryAuth"/>; Docker's 401 or 403 says it was
    /// refused, its 404 that there's no such tag.
    /// </summary>
    public async Task<JsonObject> DistributionAsync(string reference, string? registryAuth = null, CancellationToken ct = default) =>
        JsonNode.Parse(await SendWithAuthAsync(HttpMethod.Get, $"distribution/{reference}/json", registryAuth, null, RegistryTimeout, ct)) as JsonObject
            ?? throw new DockerApiException(0, $"Docker's answer about {reference} wasn't readable.");

    /// <summary>
    /// POST images/create?fromImage=&amp;tag=: pulls the image, with each line of Docker's progress as it comes. A pull has
    /// no overall limit, but one that goes <see cref="PullIdle"/> without a line throws a <see cref="TimeoutException"/>.
    /// A failure before the stream starts is Docker's error; one during it comes as a line with errorDetail.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> PullAsync(string fromImage, string tagOrDigest, string? platform = null, string? registryAuth = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"images/create?fromImage={Uri.EscapeDataString(fromImage)}&tag={Uri.EscapeDataString(tagOrDigest)}";
        if (!string.IsNullOrWhiteSpace(platform)) path += $"&platform={Uri.EscapeDataString(platform.Trim())}";
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(PullIdle);
        using var request = new HttpRequestMessage(HttpMethod.Post, await VersionedAsync(path, ct));
        if (registryAuth is not null) request.Headers.TryAddWithoutValidation(RegistryAuthHeader, registryAuth);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw PullStalled();
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body);
            while (true)
            {
                idle.CancelAfter(PullIdle);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw PullStalled();
                }
                if (line is null) yield break;
                if (ParsePullLine(line) is { } parsed) yield return parsed;
            }
        }
    }

    /// <summary>
    /// POST auth: whether the registry takes the credentials ({"username","password","serveraddress"}). Docker's 401 or
    /// 500 with its message when it doesn't.
    /// </summary>
    public async Task<JsonObject> AuthAsync(JsonObject credentials, CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Post, "auth", credentials, RegistryTimeout, ct)) as JsonObject ?? [];

    /// <summary>
    /// DELETE images/{id}: the image goes, with the layers nothing else uses. Never forced: Docker refuses (409) one a
    /// container uses, or one with several names.
    /// </summary>
    public async Task<JsonArray> RemoveImageAsync(string idOrName, CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Delete, $"images/{Uri.EscapeDataString(idOrName)}", timeout: TimeSpan.FromSeconds(60), ct: ct)) as JsonArray ?? [];

    /// <summary>GET info, as Docker wrote it: the log driver, CPUs and memory among the rest.</summary>
    public async Task<JsonObject> InfoAsync(CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, "info", ct: ct)) as JsonObject ?? [];

    Task<JsonArray> IImageEngine.ListImagesAsync(CancellationToken ct) => ListImagesAsync(ct);

    async Task<JsonArray> IImageEngine.ListContainersAsync(CancellationToken ct) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, "containers/json?all=true", ct: ct)) as JsonArray ?? [];

    Task<JsonObject?> IImageEngine.InspectImageAsync(string nameOrId, CancellationToken ct) => InspectImageAsync(nameOrId, ct);

    Task<JsonArray> IImageEngine.SearchImagesAsync(string term, int limit, CancellationToken ct) => SearchImagesAsync(term, limit, ct);

    Task<JsonObject> IImageEngine.DistributionAsync(string reference, string? registryAuth, CancellationToken ct) => DistributionAsync(reference, registryAuth, ct);

    IAsyncEnumerable<JsonObject> IImageEngine.PullAsync(string fromImage, string tagOrDigest, string? platform, string? registryAuth, CancellationToken ct) =>
        PullAsync(fromImage, tagOrDigest, platform, registryAuth, ct);

    Task<JsonObject> IImageEngine.AuthAsync(JsonObject credentials, CancellationToken ct) => AuthAsync(credentials, ct);

    Task<JsonArray> IImageEngine.RemoveImageAsync(string idOrName, CancellationToken ct) => RemoveImageAsync(idOrName, ct);

    Task<JsonObject> IImageEngine.InfoAsync(CancellationToken ct) => InfoAsync(ct);

    /// <summary>One line of a pull's stream; null for a blank or unreadable one.</summary>
    internal static JsonObject? ParsePullLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeoutException PullStalled() =>
        new($"Docker sent nothing about the pull for {PullIdle.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds, so Gitberth stopped waiting.");

    // As SendCoreAsync, with the registry's credentials in X-Registry-Auth.
    private async Task<string> SendWithAuthAsync(HttpMethod method, string path, string? registryAuth, JsonNode? body, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, await VersionedAsync(path, ct));
        if (registryAuth is not null) request.Headers.TryAddWithoutValidation(RegistryAuthHeader, registryAuth);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, timer.Token);
            var text = await response.Content.ReadAsStringAsync(timer.Token);
            if (!response.IsSuccessStatusCode) throw new DockerApiException((int)response.StatusCode, ErrorMessage(text, response));
            return text;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker didn't answer within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.");
        }
    }
}
