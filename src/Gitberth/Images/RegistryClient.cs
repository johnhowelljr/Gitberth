using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Network;

namespace Gitberth.Images;

/// <summary>A registry sign-in as it's sent to the registry itself: never logged, never shown.</summary>
internal sealed record RegistrySignIn(string Username, string Secret);

/// <summary>The platform images are pulled for on this host, as an image index names platforms: linux, amd64, no variant.</summary>
internal sealed record HostPlatform(string Os, string Architecture, string? Variant)
{
    public static readonly HostPlatform Default = new("linux", "amd64", null);

    /// <summary>GET /info's OSType and Architecture (uname's words: x86_64, aarch64, armv7l) in an index's words.</summary>
    public static HostPlatform Of(string? osType, string? architecture)
    {
        var os = string.IsNullOrWhiteSpace(osType) ? "linux" : osType.Trim().ToLowerInvariant();
        return architecture?.Trim().ToLowerInvariant() switch
        {
            null or "" or "x86_64" or "amd64" => new(os, "amd64", null),
            "aarch64" or "arm64" => new(os, "arm64", null),
            "armv7l" or "armhf" => new(os, "arm", "v7"),
            "armv6l" or "armel" => new(os, "arm", "v6"),
            "i386" or "i686" or "386" => new(os, "386", null),
            var other => new(os, other, null),
        };
    }
}

/// <summary>
/// Reads what an image holds straight from its registry (the Registry HTTP API, v2), without pulling it: for New app, so
/// an image the host doesn't have yet shows its ports, volumes and check before it's created. The manifest first (an index
/// or manifest list is resolved to this host's platform), then its config blob. Signed in as Docker is: anonymously, or
/// with the registry's saved sign-in, through the token service the registry's 401 names (Bearer), or with the sign-in
/// itself (Basic). Credentials go only over HTTPS. It throws on anything it can't read: the caller goes on without it.
/// </summary>
internal sealed partial class RegistryClient(HttpClient http)
{
    /// <summary>The named HttpClient it uses (Program.cs gives it a 10 s timeout).</summary>
    public const string ClientName = "registry";

    // Manifests and configs are small; anything much bigger isn't one.
    private const long MaxBody = 4 * 1024 * 1024;

    // What a manifest can be: an OCI index or a Docker manifest list (several platforms), or one platform's manifest.
    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private static readonly string[] ConfigTypes = ["application/vnd.oci.image.config.v1+json", "application/vnd.docker.container.image.v1+json", "application/json"];

    // How the registry wants to be asked, once its first 401 has said: a bearer token, or the sign-in itself.
    private AuthenticationHeaderValue? _authorization;

    /// <summary>
    /// The image as `docker image inspect` would describe it: Id (its config's digest), Created, Os, Architecture, and
    /// Config with its ExposedPorts, Volumes, Env, Cmd, Entrypoint, WorkingDir, User, Labels and Healthcheck. Null when the
    /// registry has no image for this platform.
    /// </summary>
    /// <exception cref="HttpRequestException">The registry refused, or doesn't have it.</exception>
    public async Task<JsonObject?> ImageAsync(ImageReference reference, RegistrySignIn? signIn, HostPlatform platform, CancellationToken ct)
    {
        var manifest = await GetAsync(reference, $"manifests/{reference.TagOrDigest}", ManifestTypes, signIn, ct);
        if (manifest["manifests"] is JsonArray index)
        {
            if (Pick(index, platform) is not { } digest) return null;
            manifest = await GetAsync(reference, $"manifests/{digest}", ManifestTypes, signIn, ct);
        }
        if (Str(manifest["config"], "digest") is not { Length: > 0 } configDigest) return null;
        var config = await GetAsync(reference, $"blobs/{configDigest}", ConfigTypes, signIn, ct);
        return new JsonObject
        {
            ["Id"] = configDigest,
            ["RepoTags"] = new JsonArray(),
            ["RepoDigests"] = new JsonArray(),
            ["Created"] = config["created"]?.DeepClone(),
            ["Os"] = config["os"]?.DeepClone(),
            ["Architecture"] = config["architecture"]?.DeepClone(),
            ["Config"] = config["config"]?.DeepClone() ?? new JsonObject(),
        };
    }

    /// <summary>
    /// The digest of an index's manifest for the platform: its OS and architecture, the host's variant first, then one
    /// that names none (arm64 images often don't), then any. Attestations (unknown/unknown) never match.
    /// </summary>
    internal static string? Pick(JsonArray manifests, HostPlatform platform)
    {
        var fits = manifests.OfType<JsonObject>()
            .Select(m => (Digest: Str(m, "digest"), Os: Str(m["platform"], "os"), Arch: Str(m["platform"], "architecture"), Variant: Str(m["platform"], "variant")))
            .Where(m => m.Digest.Length > 0 && m.Os == platform.Os && m.Arch == platform.Architecture)
            .ToList();
        var exact = platform.Variant is { } variant ? fits.FirstOrDefault(m => m.Variant == variant).Digest : null;
        return exact ?? fits.FirstOrDefault(m => m.Variant.Length == 0).Digest ?? fits.FirstOrDefault().Digest;
    }

    /// <summary>Where the registry answers: Docker Hub's at registry-1.docker.io; one on this machine over plain HTTP, as Docker allows.</summary>
    internal static string BaseOf(ImageReference reference) =>
        reference.IsDockerHub ? "https://registry-1.docker.io"
        : reference.Registry.StartsWith("localhost", StringComparison.Ordinal) || reference.Registry.StartsWith("127.", StringComparison.Ordinal)
            ? "http://" + reference.Registry
            : "https://" + reference.Registry;

    private async Task<JsonObject> GetAsync(ImageReference reference, string path, string[] accept, RegistrySignIn? signIn, CancellationToken ct)
    {
        var url = new Uri($"{BaseOf(reference)}/v2/{reference.Repository}/{path}");
        using var response = await SendAsync(url, accept, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && _authorization is null
            && await AuthorizationAsync(response.Headers.WwwAuthenticate, reference, url, signIn, ct) is { } authorization)
        {
            _authorization = authorization;
            using var again = await SendAsync(url, accept, ct);
            return await ReadAsync(again, ct);
        }
        return await ReadAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string[] accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var type in accept) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        // A redirect (a blob served from a CDN) doesn't carry it on: HttpClient drops Authorization when it follows one.
        request.Headers.Authorization = _authorization;
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The registry answered {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxBody) throw new HttpRequestException("The registry's answer is too big to be a manifest.");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new HttpRequestException("The registry's answer wasn't readable.");
    }

    // The registry's 401 says how to sign in: a token from the service it names (Bearer), or the sign-in itself (Basic).
    private async Task<AuthenticationHeaderValue?> AuthorizationAsync(HttpHeaderValueCollection<AuthenticationHeaderValue> challenges, ImageReference reference, Uri url,
        RegistrySignIn? signIn, CancellationToken ct)
    {
        foreach (var challenge in challenges)
        {
            if (challenge.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                return signIn is not null && url.Scheme == Uri.UriSchemeHttps ? Basic(signIn) : null;
            if (!challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = ChallengeField().Matches(challenge.Parameter ?? "").ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
            if (!fields.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var service)
                || service.Scheme != Uri.UriSchemeHttps && service.Scheme != Uri.UriSchemeHttp) return null;
            var query = new List<string>();
            if (fields.TryGetValue("service", out var name)) query.Add("service=" + Uri.EscapeDataString(name));
            query.Add("scope=" + Uri.EscapeDataString(fields.GetValueOrDefault("scope") ?? $"repository:{reference.Repository}:pull"));
            using var request = new HttpRequestMessage(HttpMethod.Get, realm + (realm.Contains('?') ? "&" : "?") + string.Join('&', query));
            if (signIn is not null && service.Scheme == Uri.UriSchemeHttps) request.Headers.Authorization = Basic(signIn);
            using var response = await http.SendAsync(request, ct);
            var answer = await ReadAsync(response, ct);
            var token = Str(answer, "token") is { Length: > 0 } t ? t : Str(answer, "access_token");
            return token.Length > 0 ? new AuthenticationHeaderValue("Bearer", token) : null;
        }
        return null;
    }

    private static AuthenticationHeaderValue Basic(RegistrySignIn signIn) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{signIn.Username}:{signIn.Secret}")));

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    // realm="https://auth.docker.io/token",service="registry.docker.io",scope="repository:library/postgres:pull"
    [GeneratedRegex("""(\w+)="([^"]*)"\s*,?""")]
    private static partial Regex ChallengeField();
}
