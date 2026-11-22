using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Network;

namespace Gitberth.Images;

/// <summary>A registry sign-in as it's sent to the registry itself: never logged, never shown.</summary>
internal sealed record RegistrySignIn(string Username, string Secret);

/// <summary>A registry's tag list (tags/list): its tags as it listed them, or why there are none.</summary>
/// <param name="RetryAt">When a registry that asked Gitberth to wait (429) may be asked again.</param>
internal sealed record RegistryTags(IReadOnlyList<string> Tags, LookupProblem Problem = LookupProblem.None, int Status = 0, DateTimeOffset? RetryAt = null);

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
/// or manifest list is resolved to this host's platform), then its config blob. And a repository's tag list (tags/list),
/// page by page, for the tag picker. Signed in as Docker is: anonymously, or with the registry's saved sign-in, through the
/// token service the registry's 401 names (Bearer), or with the sign-in itself (Basic). Credentials go only over HTTPS; the
/// token lives in this client alone (one read, one listing) and is never kept, logged or shown.
/// </summary>
internal sealed partial class RegistryClient(HttpClient http, TimeProvider? time = null)
{
    /// <summary>The named HttpClient it uses (Program.cs gives it a 10 s timeout).</summary>
    public const string ClientName = "registry";

    // Manifests and configs are small; anything much bigger isn't one. A token's answer is smaller still.
    private const int MaxBody = 4 * 1024 * 1024;
    private const int MaxToken = 64 * 1024;

    /// <summary>A page of tags/list at most, decompressed.</summary>
    internal const int MaxTagsPage = 2 * 1024 * 1024;

    /// <summary>Tags asked for a page (100 when a registry refuses that many), and the most pages and tags one listing reads.</summary>
    internal const int TagsPerPage = 1000;
    internal const int SmallerPage = 100;
    internal const int MaxTagPages = 10;
    internal const int MaxTags = 10_000;

    /// <summary>A request of a listing, and the whole listing (the token's request included).</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);

    // What a manifest can be: an OCI index or a Docker manifest list (several platforms), or one platform's manifest.
    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private static readonly string[] ConfigTypes = ["application/vnd.oci.image.config.v1+json", "application/vnd.docker.container.image.v1+json", "application/json"];

    private static readonly string[] JsonTypes = ["application/json"];

    // How the registry wants to be asked, once its first 401 has said: a bearer token, or the sign-in itself.
    private AuthenticationHeaderValue? _authorization;

    // Where the listing's 401 that gave it came from: it's sent to that origin only.
    private Uri? _authorizedAt;

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
    /// The repository's tags as its registry lists them (GET /v2/&lt;repo&gt;/tags/list): 1000 a page (100 when it refuses
    /// that), then each page its Link header names on the same origin, at most 10 pages and 10,000 tags, in 20 seconds. Names
    /// Docker wouldn't take are left out. A first request redirected to another host (a redirect service) lists from there,
    /// without the saved sign-in. Never throws but for cancellation: a problem comes back in the answer; a later page that
    /// fails ends the list where it is.
    /// </summary>
    public async Task<RegistryTags> TagsAsync(ImageReference reference, RegistrySignIn? signIn, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ListTimeout);
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var origin = new Uri(BaseOf(reference));
        var url = new Uri(origin, $"/v2/{reference.Repository}/tags/list?n={TagsPerPage}");
        var asked = new HashSet<string>(StringComparer.Ordinal) { url.AbsoluteUri };
        try
        {
            for (var page = 1; page <= MaxTagPages && tags.Count < MaxTags; page++)
            {
                var response = await ListPageAsync(url, reference, signIn, budget.Token);
                if (page == 1 && response.StatusCode == HttpStatusCode.BadRequest)
                {
                    response.Dispose();
                    url = new Uri(origin, $"/v2/{reference.Repository}/tags/list?n={SmallerPage}");
                    response = await ListPageAsync(url, reference, signIn, budget.Token);
                }
                using (response)
                {
                    var at = response.RequestMessage?.RequestUri ?? url;
                    if (page == 1 && !SameOrigin(at, origin))
                    {
                        // Redirected to another host: the list goes on there without the sign-in, and without a token or
                        // sign-in given for another origin (one the host it ended on gave for itself, anonymously, stays).
                        origin = new Uri(at.GetLeftPart(UriPartial.Authority));
                        signIn = null;
                        if (_authorizedAt is not null && !SameOrigin(_authorizedAt, at)) _authorization = null;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        if (page > 1) break;
                        var status = (int)response.StatusCode;
                        return status switch
                        {
                            401 or 403 => new RegistryTags([], LookupProblem.NeedsSignIn, status),
                            404 => new RegistryTags([], LookupProblem.NotFound, status),
                            429 => new RegistryTags([], LookupProblem.RateLimited, status, HostGates.RetryAt(response.Headers, Now)),
                            _ => new RegistryTags([], LookupProblem.Failed, status),
                        };
                    }
                    if (!await ListIntoAsync(response, tags, seen, budget.Token))
                    {
                        if (page > 1) break;
                        return new RegistryTags([], LookupProblem.Failed, (int)response.StatusCode);
                    }
                    // The next page only where the registry says, and only on the origin the listing is on.
                    if (NextLink(response.Headers, at) is not { } next || !SameOrigin(next, origin) || !asked.Add(next.AbsoluteUri)) break;
                    url = next;
                }
            }
            return new RegistryTags(tags);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException)
        {
            if (tags.Count > 0) return new RegistryTags(tags);
            return ex switch
            {
                // The token service's own answer.
                HttpRequestException { StatusCode: { } refused } when refused is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new RegistryTags([], LookupProblem.NeedsSignIn, (int)refused),
                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => new RegistryTags([], LookupProblem.RateLimited, 429, Now + TimeSpan.FromSeconds(60)),
                HttpRequestException { StatusCode: { } other } => new RegistryTags([], LookupProblem.Failed, (int)other),
                InvalidDataException or JsonException => new RegistryTags([], LookupProblem.Failed),
                // DNS, connect, TLS, a timeout.
                _ => new RegistryTags([], LookupProblem.Unreachable),
            };
        }
    }

    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();

    // A page of the list, anonymously first; on a 401 (once a listing) the way the challenge says, then again. A first
    // request that ended on another host is answered there, without the sign-in.
    private async Task<HttpResponseMessage> ListPageAsync(Uri url, ImageReference reference, RegistrySignIn? signIn, CancellationToken budget)
    {
        var response = await SendTimedAsync(url, budget);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _authorization is not null) return response;
        var at = response.RequestMessage?.RequestUri ?? url;
        AuthenticationHeaderValue? authorization;
        try
        {
            authorization = await AuthorizationAsync(response.Headers.WwwAuthenticate, reference, at, SameOrigin(at, url) ? signIn : null, budget);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        if (authorization is null) return response;
        response.Dispose();
        _authorization = authorization;
        _authorizedAt = at;
        return await SendTimedAsync(at, budget);
    }

    private async Task<HttpResponseMessage> SendTimedAsync(Uri url, CancellationToken budget)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(budget);
        timer.CancelAfter(RequestTimeout);
        return await SendAsync(url, JsonTypes, timer.Token);
    }

    // {"name":"library/nginx","tags":["1","1.27",…]} (tags null for a repository without any): its tags Docker would take,
    // each once, added to the listing's until it holds MaxTags; false when it isn't such an answer. Read as a document, so a
    // page of 2 MiB costs about its size, never an object for each of its entries.
    private static async Task<bool> ListIntoAsync(HttpResponseMessage response, List<string> tags, HashSet<string> seen, CancellationToken ct)
    {
        if (await HostGates.ReadAsync(response.Content, MaxTagsPage, ct) is not { } body) return false;
        try
        {
            using var answer = JsonDocument.Parse(body);
            if (answer.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!answer.RootElement.TryGetProperty("tags", out var listed) || listed.ValueKind == JsonValueKind.Null) return true;
            if (listed.ValueKind != JsonValueKind.Array) return false;
            foreach (var tag in listed.EnumerateArray())
            {
                if (tags.Count == MaxTags) break;
                if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { } name && TagRanking.IsTagName(name) && seen.Add(name)) tags.Add(name);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Link: </v2/library/nginx/tags/list?last=1-alpine-slim&n=1000>; rel="next", resolved against the page's address.
    private static Uri? NextLink(HttpResponseHeaders headers, Uri at)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;
        foreach (var value in values)
            foreach (Match link in NextRel().Matches(value))
                if (Uri.TryCreate(at, link.Groups[1].Value.Trim(), out var next) && (next.Scheme == Uri.UriSchemeHttps || next.Scheme == Uri.UriSchemeHttp))
                    return next;
        return null;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

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
        // A 401 from a host a redirect led to is answered without the sign-in: it was saved for the registry's own host.
        var at = response.RequestMessage?.RequestUri ?? url;
        if (response.StatusCode == HttpStatusCode.Unauthorized && _authorization is null
            && await AuthorizationAsync(response.Headers.WwwAuthenticate, reference, at, SameOrigin(at, url) ? signIn : null, ct) is { } authorization)
        {
            _authorization = authorization;
            using var again = await SendAsync(url, accept, ct);
            return await ReadAsync(again, MaxBody, ct);
        }
        return await ReadAsync(response, MaxBody, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string[] accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var type in accept) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        request.Headers.UserAgent.Add(HostGates.UserAgent);
        // A redirect (a blob served from a CDN) doesn't carry it on: HttpClient drops Authorization when it follows one.
        request.Headers.Authorization = _authorization;
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // The answer as JSON, read through a copy that stops past max bytes (whatever Content-Length says).
    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The registry answered {(int)response.StatusCode}.", null, response.StatusCode);
        var body = await HostGates.ReadAsync(response.Content, max, ct) ?? throw new InvalidDataException("The registry's answer is too big, or not readable.");
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("The registry's answer wasn't readable.");
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
            request.Headers.UserAgent.Add(HostGates.UserAgent);
            if (signIn is not null && service.Scheme == Uri.UriSchemeHttps) request.Headers.Authorization = Basic(signIn);
            // Headers first: the answer is read through the capped copy, never buffered whole (a token service can send anything).
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var answer = await ReadAsync(response, MaxToken, ct);
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

    // One link of a Link header that's rel="next": its target.
    [GeneratedRegex("""<([^>]*)>[^<]*?\brel\s*=\s*"?next\b""", RegexOptions.IgnoreCase)]
    private static partial Regex NextRel();
}
