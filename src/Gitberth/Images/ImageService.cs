using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Images;

/// <summary>
/// The images on the host and in registries, for New app and for updates: the local list, Docker Hub's search and tags,
/// what a registry says about a reference (without pulling), pulls in the background with their progress, the free space
/// on Docker's disk, and removing an image nothing uses. Gitberth talks to registries through the Engine API, with the
/// credentials <see cref="RegistryService"/> keeps; only New app's look at an image the host doesn't have yet asks the
/// registry itself, for the image's config (<see cref="RemoteFactsAsync"/>).
/// </summary>
public sealed class ImageService
{
    /// <summary>The named HttpClient for Docker Hub's public tags API (Program.cs gives it a 10 s timeout).</summary>
    public const string HubClient = "dockerhub";

    private readonly IImageEngine _engine;
    private readonly RegistryService _registries;
    private readonly GitberthOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageService> _logger;
    private readonly IDbContextFactory<GitberthDb>? _dbFactory;
    private readonly ConcurrentDictionary<string, PullJob> _pulls = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hostGate = new(1, 1);
    private HostFacts? _host;

    /// <param name="dbFactory">Where a pull records that the registry has the reference (ImageChecks), and facts read it back.</param>
    public ImageService(DockerApi api, RegistryService registries, GitberthOptions options, IHttpClientFactory http, ILogger<ImageService> logger,
        IDbContextFactory<GitberthDb>? dbFactory = null)
        : this((IImageEngine)api, registries, options, http, logger, dbFactory)
    {
    }

    /// <summary>Talks to the engine through <paramref name="engine"/>: a stand-in, in tests.</summary>
    internal ImageService(IImageEngine engine, RegistryService registries, GitberthOptions options, IHttpClientFactory http, ILogger<ImageService> logger,
        IDbContextFactory<GitberthDb>? dbFactory = null)
    {
        _engine = engine;
        _registries = registries;
        _options = options;
        _http = http;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>Reads Docker's disk; tests give their own.</summary>
    internal Func<DiskSpace>? ReadDisk { get; init; }

    /// <summary>Sees each line of a pull's stream as Docker sent it: the real-engine test records them.</summary>
    internal Action<JsonObject>? PullLine { get; init; }

    /// <summary>
    /// The images on the host with a name, newest first: their tags, size, age and how many containers (running or not)
    /// were made from them. Dangling images (no tag) are left out. Gitberth's own builds are marked, for "Built by Gitberth".
    /// </summary>
    public async Task<IReadOnlyList<LocalImage>> ListLocalAsync(CancellationToken ct = default)
    {
        var images = _engine.ListImagesAsync(ct);
        var containers = _engine.ListContainersAsync(ct);
        return ParseLocal(await images, await containers);
    }

    /// <summary>
    /// What the image on the host says about itself (ports, volumes, variables…); null when it isn't on the host. Its origin
    /// is Registry once a registry is known to have the reference (<see cref="ImageOrigins"/>), else Local.
    /// </summary>
    public async Task<ImageFacts?> FactsAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var image = await _engine.InspectImageAsync(reference.Trim(), ct);
        if (image is null) return null;
        var facts = ContainerSettingsJson.Facts(image);
        if (ImageReference.Parse(reference) is not { Digest: null } parsed || _dbFactory is null) return facts;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var check = await db.ImageChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Reference == parsed.Canonical, ct);
        return facts with { Origin = ImageOrigins.Of(parsed, check, image) };
    }

    /// <summary>How long reading an image from its registry may take before New app goes on without it.</summary>
    internal static readonly TimeSpan RemoteFactsTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// What an image the host doesn't have says about itself, read from its registry without pulling it (its config, for
    /// this host's platform), with the sign-in update checks use: for New app's Configure. Null when it can't be read
    /// within a few seconds, whatever the reason; nothing is ever pulled. Its facts say they aren't the host's (OnHost).
    /// </summary>
    /// <remarks>Docker Hub counts the manifest it reads as a pull against its limit, as Docker's own pull of it does.</remarks>
    public async Task<ImageFacts?> RemoteFactsAsync(string reference, int? registryId = null, CancellationToken ct = default)
    {
        if (ImageReference.Parse(reference) is not { } parsed) return null;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(RemoteFactsTimeout);
        try
        {
            var platform = await PlatformAsync(timer.Token);
            var signIn = await _registries.SignInAsync(parsed, registryId, timer.Token);
            using var client = _http.CreateClient(RegistryClient.ClientName);
            var image = await new RegistryClient(client).ImageAsync(parsed, signIn, platform, timer.Token);
            return image is null ? null : ContainerSettingsJson.Facts(image) with { Origin = ImageOrigin.Registry, OnHost = false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "{Reference} couldn't be read from its registry", parsed.Familiar);
            return null;
        }
    }

    // The host's platform, for an image index; linux/amd64 when Docker didn't say.
    private async Task<HostPlatform> PlatformAsync(CancellationToken ct)
    {
        try
        {
            return (await HostAsync(ct)).Platform;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "The host's platform couldn't be read");
            return HostPlatform.Default;
        }
    }

    /// <summary>
    /// Docker Hub's search, through the daemon: official images first, then by stars. Throws with Docker's words when Docker
    /// Hub can't be searched.
    /// </summary>
    /// <exception cref="InvalidOperationException">The search failed; its message says why.</exception>
    public async Task<IReadOnlyList<HubResult>> SearchAsync(string term, CancellationToken ct = default)
    {
        var text = term?.Trim() ?? "";
        if (text.Length == 0) return [];
        try
        {
            return ParseSearch(await _engine.SearchImagesAsync(text, 25, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Docker Hub couldn't be searched: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A Docker Hub repository's tags, most recently pushed first, from Docker Hub's public API; empty for another
    /// registry, or when Docker Hub didn't answer (the tag is then typed in).
    /// </summary>
    public async Task<IReadOnlyList<string>> TagsAsync(ImageReference reference, CancellationToken ct = default)
    {
        if (!reference.IsDockerHub) return [];
        var slash = reference.Repository.IndexOf('/');
        var url = $"https://hub.docker.com/v2/namespaces/{Uri.EscapeDataString(reference.Repository[..slash])}/repositories/" +
                  $"{Uri.EscapeDataString(reference.Repository[(slash + 1)..])}/tags?page_size=100&ordering=last_updated";
        try
        {
            using var client = _http.CreateClient(HubClient);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return [];
            return ParseTags(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Docker Hub's tags for {Repository} couldn't be read", reference.Name);
            return [];
        }
    }

    /// <summary>
    /// What the registry says about the reference, without pulling it: found, its digest and platforms; or why not
    /// (signed out, no such tag, a registry Docker won't talk to), in words the owner can act on.
    /// </summary>
    public async Task<RemoteImage> ResolveAsync(string reference, int? registryId = null, CancellationToken ct = default)
    {
        if (ImageReference.Parse(reference) is not { } parsed)
            return new RemoteImage(false, null, [], NotAReference(reference));
        try
        {
            var auth = await _registries.AuthHeaderAsync(parsed, registryId, ct);
            return ParseDistribution(await _engine.DistributionAsync(parsed.Canonical, auth, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var status = (ex as DockerApiException)?.Status ?? 0;
            return new RemoteImage(false, null, [], Explain(ex.Message, parsed, status), Unauthorized(ex.Message, status), !Unauthorized(ex.Message, status) && Missing(ex.Message, status));
        }
    }

    /// <summary>Whether Gitberth has a sign-in to send for the reference (the app's credential, or one for its registry).</summary>
    internal async Task<bool> SignedInAsync(ImageReference reference, int? registryId, CancellationToken ct) =>
        await _registries.AuthHeaderAsync(reference, registryId, ct) is not null;

    /// <summary>
    /// Starts pulling the reference in the background, or returns the pull that's running for it already. It's refused
    /// before it starts when Docker's disk is too full (<see cref="DiskSpace.TooLow"/>).
    /// </summary>
    public PullJob StartPull(string reference, string? platform = null, int? registryId = null)
    {
        var parsed = ImageReference.Parse(reference);
        var key = PullKey(parsed?.Canonical ?? reference.Trim(), platform);
        if (_pulls.TryGetValue(key, out var running) && !running.Completion.IsCompleted) return running;
        var job = new PullJob(parsed?.Familiar ?? reference.Trim());
        job.Completion = Task.Run(async () =>
        {
            try
            {
                return parsed is null ? new PullResult(false, null, null, NotAReference(reference)) : await PullCoreAsync(job, parsed, platform, registryId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pulling {Reference} failed", reference);
                return new PullResult(false, null, null, $"Pulling {reference} failed: {ex.Message}");
            }
            finally
            {
                job.Finished();
            }
        });
        _pulls[key] = job;
        return job;
    }

    /// <summary>The pull of the reference running now, or the last one since Gitberth started; null for none.</summary>
    public PullJob? PullFor(string reference)
    {
        var canonical = ImageReference.Parse(reference)?.Canonical ?? reference.Trim();
        return _pulls.Where(p => p.Key == canonical || p.Key.StartsWith(canonical + "|", StringComparison.Ordinal))
            .Select(p => p.Value).OrderBy(p => p.Completion.IsCompleted).FirstOrDefault();
    }

    /// <summary>
    /// The free space on Docker's disk, read on Gitberth's data folder (/data), which lives in Docker's volume store on the
    /// same filesystem. When it can't be read, it reads as plenty: a pull is never refused on a guess.
    /// </summary>
    public DiskSpace Disk()
    {
        if (ReadDisk is { } read) return read();
        try
        {
            var path = Path.GetFullPath(_options.DataDir);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
            var drive = new DriveInfo(path);
            return new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Docker's free disk space couldn't be read");
            return new DiskSpace(long.MaxValue / 4, long.MaxValue / 4);
        }
    }

    /// <summary>
    /// Removes the image when no container uses it; null when removed or still used, else Docker's message. Only an
    /// untagged image goes: by the time an update tidies it up its followed tag has moved on, so a name it still has was
    /// given by someone else (postgres:latest, a "-before-upgrade" kept by hand), and it stays (its message says so). Nor
    /// does an image any app keeps for Roll back. Never forced.
    /// </summary>
    public async Task<string?> RemoveIfUnusedAsync(string imageId, CancellationToken ct = default)
    {
        try
        {
            if (await _engine.InspectImageAsync(imageId, ct) is not { } image) return null;
            var id = Str(image, "Id");
            if ((await _engine.ListContainersAsync(ct)).OfType<JsonObject>().Any(c => Str(c, "ImageID") == id)) return null;
            var tags = Strings(image["RepoTags"]).Where(t => t != "<none>:<none>").ToList();
            if (tags.Count > 0) return $"{Short(id)} has a name of its own ({string.Join(", ", tags)}), so Gitberth left it.";
            if (_dbFactory is not null)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                if (await db.Apps.AsNoTracking().AnyAsync(a => a.PreviousImage == id, ct)) return $"{Short(id)} is kept for an app's Roll back, so Gitberth left it.";
            }
            await _engine.RemoveImageAsync(id, ct);
            return null;
        }
        catch (DockerApiException ex) when (ex.NotFound || ex.Status == 409)
        {
            // Gone already, or a container was made from it meanwhile: either way there's nothing to remove.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    /// <summary>What the host has for new containers: its CPUs and memory, and the daemon's log driver. Read once.</summary>
    internal async Task<HostFacts> HostAsync(CancellationToken ct = default)
    {
        if (_host is { } known) return known;
        await _hostGate.WaitAsync(ct);
        try
        {
            if (_host is { } again) return again;
            var info = await _engine.InfoAsync(ct);
            return _host = new HostFacts((int)ContainerPorts.Number(info, "NCPU"), ContainerPorts.Number(info, "MemTotal"), Str(info, "LoggingDriver"))
            {
                Platform = HostPlatform.Of(Str(info, "OSType"), Str(info, "Architecture")),
            };
        }
        finally
        {
            _hostGate.Release();
        }
    }

    /// <summary>The image an ID or reference names on the host, as Docker describes it; null when there's none.</summary>
    internal Task<JsonObject?> InspectAsync(string nameOrId, CancellationToken ct) => _engine.InspectImageAsync(nameOrId, ct);

    /// <summary>Every container, stopped ones too, as Docker lists them: what each runs, for the update checks.</summary>
    internal Task<JsonArray> ContainersAsync(CancellationToken ct) => _engine.ListContainersAsync(ct);

    private async Task<PullResult> PullCoreAsync(PullJob job, ImageReference reference, string? platform, int? registryId)
    {
        var disk = Disk();
        if (disk.TooLow) return new PullResult(false, null, null, disk.Problem!);
        var auth = await _registries.AuthHeaderAsync(reference, registryId);
        var tracker = new PullTracker();
        job.Report(new PullProgress($"Asking {reference.RegistryName} for {reference.Familiar}", 0, 0, 0, 0));
        try
        {
            await foreach (var line in _engine.PullAsync(reference.FromImage, reference.TagOrDigest, platform, auth, CancellationToken.None))
            {
                PullLine?.Invoke(line);
                if (tracker.Feed(line) is { } error) return new PullResult(false, null, tracker.Digest, Explain(error, reference));
                job.Report(tracker.Progress);
            }
        }
        catch (Exception ex) when (ex is DockerApiException or TimeoutException or HttpRequestException or IOException)
        {
            return new PullResult(false, null, tracker.Digest, Explain(ex.Message, reference, (ex as DockerApiException)?.Status ?? 0));
        }
        var image = await _engine.InspectImageAsync(reference.Canonical, CancellationToken.None);
        if (image is null) return new PullResult(false, null, tracker.Digest, $"Docker finished pulling {reference.Familiar}, but doesn't have it.");
        var digest = tracker.Digest ?? Strings(image["RepoDigests"]).Select(DigestOf).FirstOrDefault(d => d is not null);
        var id = Str(image, "Id");
        await RecordPulledAsync(reference, digest);
        return new PullResult(true, id, digest, tracker.UpToDate ? $"{reference.Familiar} is up to date ({Short(id)})." : $"Pulled {reference.Familiar} ({Short(id)}).");
    }

    // A pull is the registry saying it has the reference: from now on its image is the registry's, and the watcher checks it.
    private async Task RecordPulledAsync(ImageReference reference, string? digest)
    {
        if (_dbFactory is null || digest is null || reference.Digest is not null) return;
        try
        {
            await UpdateService.SaveCheckAsync(_dbFactory, reference.Canonical,
                check => (check.RemoteDigest, check.CheckedAt, check.Error, check.RetryAfter) = (digest, DateTime.UtcNow, null, null), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The pull is done all the same; the next check records what the registry has.
            _logger.LogInformation(ex, "Couldn't record that {Reference} was pulled", reference.Familiar);
        }
    }

    private static string PullKey(string canonical, string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? canonical : canonical + "|" + platform.Trim();

    internal static string NotAReference(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Give an image, such as postgres:18 or ghcr.io/owner/app:1.2."
        : ImageReference.IsImageId(text) ? $"{Short(text.Trim())} is an image ID: it can't be pulled, only used from this host."
        : $"{text.Trim()} isn't an image name Docker takes: lower-case letters, digits, . _ - and /, then :tag (postgres:18, ghcr.io/owner/app:1.2).";

    /// <summary>Docker's error about a registry in words the owner can act on, with Docker's own at the end.</summary>
    internal static string Explain(string message, ImageReference reference, int status = 0)
    {
        var registry = reference.RegistryName;
        if (message.Contains("server gave HTTP response to HTTPS client", StringComparison.OrdinalIgnoreCase))
            return $"{reference.Registry} answers over plain HTTP. Docker pulls from it only once the daemon lists it under insecure-registries " +
                   $"(/etc/docker/daemon.json on the host); Gitberth doesn't change Docker's settings. Docker said: {message}";
        if (message.Contains("x509:", StringComparison.OrdinalIgnoreCase) || message.Contains("certificate signed by unknown authority", StringComparison.OrdinalIgnoreCase))
            return $"Docker doesn't trust {reference.Registry}'s certificate. Its CA goes in /etc/docker/certs.d/{reference.Registry}/ca.crt on the host; " +
                   $"Gitberth doesn't change Docker's settings. Docker said: {message}";
        if (status == 429 || message.Contains("toomanyrequests", StringComparison.OrdinalIgnoreCase) || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return $"{registry}'s limit on pulls is reached for now: try again in a few hours, or sign in to it (Settings › Registries) for a higher limit. Docker said: {message}";
        if (Unauthorized(message, status))
            return reference.IsDockerHub
                ? $"Docker Hub refused {reference.Familiar}: it doesn't exist, or it's private and needs a sign-in (Settings › Registries). Docker said: {message}"
                : $"{registry} refused {reference.Familiar}: it needs a sign-in (Settings › Registries), or it doesn't exist. Docker said: {message}";
        if (Missing(message, status))
            return $"{registry} has no {reference.Familiar}. Docker said: {message}";
        if (message.Contains("no space left on device", StringComparison.OrdinalIgnoreCase))
            return $"Docker's disk is full: free some space, then try again. Docker said: {message}";
        if (message.Contains("no such host", StringComparison.OrdinalIgnoreCase) || message.Contains("dial tcp", StringComparison.OrdinalIgnoreCase)
            || message.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase) || message.Contains("TLS handshake timeout", StringComparison.OrdinalIgnoreCase))
            return $"Docker couldn't reach {registry}. Docker said: {message}";
        return message;
    }

    /// <summary>The registry has no such repository or tag.</summary>
    internal static bool Missing(string message, int status) =>
        status == 404 || message.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    internal static bool Unauthorized(string message, int status) =>
        status is 401 or 403
        || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
        || message.Contains("requested access to the resource is denied", StringComparison.OrdinalIgnoreCase)
        || message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>GET /distribution's answer: {"Descriptor":{"digest"},"Platforms":[{"architecture","os","variant"}]}.</summary>
    internal static RemoteImage ParseDistribution(JsonObject answer)
    {
        var digest = Str(answer["Descriptor"], "digest");
        var platforms = (answer["Platforms"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => string.Join('/', new[] { Str(p, "os"), Str(p, "architecture"), Str(p, "variant") }.Where(x => x.Length > 0)))
            .Where(p => p.Length > 0 && p != "unknown/unknown")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return digest.Length > 0
            ? new RemoteImage(true, digest, platforms, null)
            : new RemoteImage(false, null, platforms, "The registry didn't say which image the reference names.");
    }

    /// <summary>GET /images/json and GET /containers/json as the local list: tagged images only, newest first.</summary>
    internal static List<LocalImage> ParseLocal(JsonArray images, JsonArray containers)
    {
        var users = containers.OfType<JsonObject>().GroupBy(c => Str(c, "ImageID")).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var list = new List<LocalImage>();
        foreach (var image in images.OfType<JsonObject>())
        {
            var tags = Strings(image["RepoTags"]).Where(t => t != "<none>:<none>").ToList();
            if (tags.Count == 0) continue;
            var id = Str(image, "Id");
            var created = DateTime.UnixEpoch.AddSeconds(ContainerPorts.Number(image, "Created"));
            list.Add(new LocalImage(id, tags, created, ContainerPorts.Number(image, "Size"), users.GetValueOrDefault(id),
                tags.Any(t => t.StartsWith("gitberth/", StringComparison.Ordinal) || t.StartsWith("gitberth:", StringComparison.Ordinal))));
        }
        return list.OrderByDescending(i => i.Created).ThenBy(i => i.Tags[0], StringComparer.Ordinal).ToList();
    }

    internal static List<HubResult> ParseSearch(JsonArray results) =>
        results.OfType<JsonObject>()
            .Select(r => new HubResult(Str(r, "name"), Str(r, "description"), (int)ContainerPorts.Number(r, "star_count"),
                r["is_official"]?.GetValueKind() == JsonValueKind.True))
            .Where(r => r.Name.Length > 0)
            .OrderByDescending(r => r.Official).ThenByDescending(r => r.Stars).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Docker Hub's tags answer: {"results":[{"name":"18"},…]}.</summary>
    internal static List<string> ParseTags(string json)
    {
        try
        {
            return (JsonNode.Parse(json)?["results"] as JsonArray ?? []).OfType<JsonObject>().Select(t => Str(t, "name")).Where(n => n.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The digest of a RepoDigests entry: sha256:… of postgres@sha256:….</summary>
    internal static string? DigestOf(string repoDigest)
    {
        var at = repoDigest.IndexOf('@');
        return at >= 0 && at < repoDigest.Length - 1 ? repoDigest[(at + 1)..] : null;
    }

    /// <summary>An ID or digest as the pages show it: its first 12 hex digits.</summary>
    internal static string Short(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    internal static IEnumerable<string> Strings(JsonNode? array) =>
        (array as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>();

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);
}

/// <param name="LogDriver">The daemon's log driver: json-file needs rotation options on a new container, the others rotate themselves.</param>
internal sealed record HostFacts(int Cpus, long MemoryBytes, string LogDriver)
{
    /// <summary>The platform images are pulled for: an image's registry is asked for this one.</summary>
    public HostPlatform Platform { get; init; } = HostPlatform.Default;
}
