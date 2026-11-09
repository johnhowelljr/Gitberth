using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Network;

namespace Gitberth.Images;

/// <summary>
/// An image reference as Docker reads it (its distribution/reference rules): the registry, the repository, a tag and a
/// digest. Docker Hub's names are written in full: postgres:18 is docker.io/library/postgres:18.
/// </summary>
public sealed partial record ImageReference(string Registry, string Repository, string? Tag, string? Digest)
{
    /// <summary>postgres:18 → docker.io/library/postgres:18; null for something that isn't a reference.</summary>
    /// <remarks>
    /// An image ID (64 hex digits, with or without sha256:) isn't one, nor is a name with capitals, spaces or a tag Docker
    /// refuses. The first part is a registry when it has a dot or a colon, is localhost, or has a capital; index.docker.io
    /// and registry-1.docker.io are Docker Hub. With both a tag and a digest, the digest is what's pulled.
    /// </remarks>
    public static ImageReference? Parse(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 512 || ImageId().IsMatch(s)) return null;

        string? digest = null;
        var at = s.IndexOf('@');
        if (at >= 0)
        {
            digest = s[(at + 1)..];
            s = s[..at];
            if (!ValidDigest(digest)) return null;
        }

        string? tag = null;
        var colon = s.LastIndexOf(':');
        if (colon > s.LastIndexOf('/'))
        {
            tag = s[(colon + 1)..];
            s = s[..colon];
            if (!TagPattern().IsMatch(tag)) return null;
        }

        string registry, path;
        var slash = s.IndexOf('/');
        var first = slash < 0 ? "" : s[..slash];
        if (slash < 0 || (!first.Contains('.') && !first.Contains(':') && first != "localhost" && first == first.ToLowerInvariant()))
        {
            registry = DockerHub;
            path = s;
        }
        else
        {
            registry = first.ToLowerInvariant();
            path = s[(slash + 1)..];
            if (!DomainPattern().IsMatch(registry)) return null;
        }
        if (registry is "index.docker.io" or "registry-1.docker.io") registry = DockerHub;
        if (!PathPattern().IsMatch(path)) return null;
        if (registry == DockerHub && !path.Contains('/')) path = "library/" + path;
        if (registry.Length + 1 + path.Length > 255) return null;
        return new ImageReference(registry, path, tag, digest);
    }

    /// <summary>Whether <paramref name="text"/> names an image by its ID: 64 hex digits, with or without sha256:.</summary>
    public static bool IsImageId(string? text) => text is not null && ImageId().IsMatch(text.Trim());

    /// <summary>What POST /images/create?fromImage= takes: docker.io/library/postgres.</summary>
    public string FromImage => $"{Registry}/{Repository}";

    /// <summary>The digest when there is one, else the tag, else latest.</summary>
    public string TagOrDigest => Digest ?? Tag ?? "latest";

    /// <summary>As people write it: postgres:18, ghcr.io/x/y:1.2.</summary>
    public string Familiar =>
        Name + (Tag is null ? "" : ":" + Tag) + (Digest is null ? "" : "@" + Digest);

    /// <summary>The key of ImageChecks: docker.io/library/postgres:18.</summary>
    public string Canonical => FromImage + (Digest is not null ? "@" + Digest : ":" + (Tag ?? "latest"));

    public bool IsDockerHub => Registry == "docker.io";

    /// <summary>The repository as people write it: postgres, louislam/uptime-kuma, ghcr.io/o/r.</summary>
    public string Name => IsDockerHub ? (Repository.StartsWith("library/", StringComparison.Ordinal) ? Repository["library/".Length..] : Repository) : FromImage;

    /// <summary>The repository's last part: postgres for docker.io/library/postgres, r for ghcr.io/o/r.</summary>
    public string ShortName => Repository[(Repository.LastIndexOf('/') + 1)..];

    /// <summary>The registry as the owner knows it: Docker Hub, or its host.</summary>
    public string RegistryName => IsDockerHub ? "Docker Hub" : Registry;

    /// <summary>The same reference with no digest: what updates follow.</summary>
    public ImageReference WithoutDigest => this with { Digest = null };

    public override string ToString() => Familiar;

    internal const string DockerHub = "docker.io";

    private static bool ValidDigest(string digest)
    {
        if (!DigestPattern().IsMatch(digest)) return false;
        var colon = digest.IndexOf(':');
        return digest[..colon] != "sha256" || Sha256Hex().IsMatch(digest[(colon + 1)..]);
    }

    [GeneratedRegex("^(?:sha256:)?[a-f0-9]{64}$")]
    private static partial Regex ImageId();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[a-z0-9]+(?:[.+_-][a-z0-9]+)*:[a-zA-Z0-9=_-]{32,}$")]
    private static partial Regex DigestPattern();

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^(?:(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)(?:\.(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?))*|\[[0-9a-fA-F:.]+\])(?::[0-9]{1,5})?$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$")]
    private static partial Regex PathPattern();
}

/// <param name="Containers">How many containers, running or stopped, were created from it.</param>
/// <param name="BuiltByGitberth">One of Gitberth's own builds (gitberth/&lt;slug&gt;:&lt;commit&gt;) or Gitberth's image.</param>
public sealed record LocalImage(string Id, IReadOnlyList<string> Tags, DateTime Created, long SizeBytes, int Containers, bool BuiltByGitberth);
public sealed record HubResult(string Name, string Description, int Stars, bool Official);
/// <param name="Digest">The digest a pull by tag gets: the index's, for an image with several platforms.</param>
/// <param name="Platforms">linux/amd64, linux/arm64/v8 and the like; empty when the registry didn't say.</param>
/// <param name="Error">Why it can't be pulled, in words the owner can act on; null when it was found.</param>
/// <param name="Unauthorized">The registry refused it: it needs a sign-in, or (on Docker Hub) it doesn't exist.</param>
/// <param name="NotFound">The registry said it has no such repository or tag.</param>
public sealed record RemoteImage(bool Found, string? Digest, IReadOnlyList<string> Platforms, string? Error, bool Unauthorized = false, bool NotFound = false);
/// <param name="Status">What Docker is doing: "Downloading", "Extracting", "Pull complete"…</param>
/// <param name="Current">Bytes downloaded so far, of the layers whose size is known.</param>
/// <param name="Total">The size of the layers whose size is known.</param>
public sealed record PullProgress(string Status, long Current, long Total, int Layers, int LayersDone)
{
    /// <summary>0 to 1, for the bar; null until a size is known.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : Layers > 0 ? (double)LayersDone / Layers : null;
}
/// <param name="ImageId">The image the reference names after the pull.</param>
/// <param name="Digest">The digest Docker pulled it by.</param>
public sealed record PullResult(bool Ok, string? ImageId, string? Digest, string Message);

/// <summary>
/// A pull running in the background (closing the page doesn't stop it): its progress as Docker reports it, layer by
/// layer, summed into one bar, and its result.
/// </summary>
public sealed class PullJob
{
    private PullProgress _progress;

    internal PullJob(string reference)
    {
        Reference = reference;
        _progress = new PullProgress("Waiting to start", 0, 0, 0, 0);
    }

    public string Reference { get; }

    public PullProgress Progress => Volatile.Read(ref _progress);

    public Task<PullResult> Completion { get; internal set; } = Task.FromResult(new PullResult(false, null, null, ""));

    /// <summary>Raised on any thread as progress comes in, and when it ends.</summary>
    public event Action? Changed;

    internal void Report(PullProgress progress)
    {
        Volatile.Write(ref _progress, progress);
        Changed?.Invoke();
    }

    internal void Finished() => Changed?.Invoke();
}

public sealed record DiskSpace(long FreeBytes, long TotalBytes)
{
    public bool TooLow => FreeBytes < 5L * 1024 * 1024 * 1024 || FreeBytes < TotalBytes / 20;
    public bool Low => FreeBytes < 15L * 1024 * 1024 * 1024;

    /// <summary>"Only 3.1 GB free on Docker's disk." when it's low, else "41.2 GB free on Docker's disk."</summary>
    public string Text => $"{(Low ? "Only " : "")}{Gb(FreeBytes)} free on Docker's disk.";

    /// <summary>Why a pull is refused, or a warning; null when there's room.</summary>
    public string? Problem => TooLow ? Text + " Free some space first." : Low ? Text : null;

    internal static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}

/// <param name="CanRollBack">The image it ran before its last update is still on this host, and it doesn't run it: Roll back is offered.</param>
public sealed record UpdateStatus(ImageOrigin Origin, string? Tracked, bool Available, string? RunningDigest, string? LatestDigest, DateTime? CheckedAt,
    string Text, string? Error, bool CanRollBack)
{
    /// <summary>The image Roll back goes to, as its button names it: "postgres:18 (3a42…)"; null when there's none.</summary>
    public string? Previous { get; init; }
}

/// <param name="Plan">The engine's plan for the update or rollback, ready for the dialog; null with Blocked.</param>
public sealed record UpdatePrepared(PullResult? Pull, PortChangePlan? Plan, string? Blocked);

public sealed record RegistryInfo(int Id, string Host, string Username, DateTime CreatedAt, DateTime? VerifiedAt)
{
    /// <summary>Its secret can't be read any more (Gitberth's keys were lost): it needs its password again.</summary>
    public bool NeedsSecret { get; init; }
}

public sealed record RegistryInput(int? Id, string Host, string Username, string Secret);
public sealed record RegistrySaved(bool Ok, string Message, int? Id);

/// <summary>
/// Sums the lines of a pull's stream (POST /images/create) into one progress: each layer's bytes as Docker reports
/// them, layers already on the host or finished counted as done, and the digest and the final status. Pure.
/// <para>
/// Docker 29 with the containerd image store also reports the index, manifest and config blobs, as "Download complete"
/// alone: those aren't layers. Its "Extracting" counts seconds, not bytes, and a small layer has no byte counts at all
/// (hidecounts), so the bar falls back to layers done. The final status means every layer is in.
/// </para>
/// </summary>
internal sealed class PullTracker
{
    private sealed class Layer
    {
        public long Current;
        public long Total;
        public bool Downloaded;
        public bool Done;
        /// <summary>Seen as more than a blob that was fetched: pulled, waited for, downloaded, extracted or already there.</summary>
        public bool Real;
    }

    private readonly Dictionary<string, Layer> _layers = new(StringComparer.Ordinal);
    private string _status = "Starting";

    public string? Digest { get; private set; }

    /// <summary>Docker said the image was there already: nothing new was pulled.</summary>
    public bool UpToDate { get; private set; }

    public PullProgress Progress
    {
        get
        {
            long current = 0, total = 0;
            var layers = _layers.Values.Where(l => l.Real).ToList();
            foreach (var layer in layers)
            {
                if (layer.Total <= 0) continue;
                total += layer.Total;
                current += layer.Downloaded || layer.Done ? layer.Total : Math.Min(layer.Current, layer.Total);
            }
            return new PullProgress(_status, current, total, layers.Count, layers.Count(l => l.Done));
        }
    }

    /// <summary>Takes one line; returns Docker's error when the line is one, else null.</summary>
    public string? Feed(JsonObject line)
    {
        if (line["errorDetail"] is JsonObject detail && Str(detail, "message") is { Length: > 0 } message) return message;
        if (Str(line, "error") is { Length: > 0 } error) return error;

        var status = Str(line, "status");
        var id = Str(line, "id");
        if (status.Length == 0) return null;
        if (status.StartsWith("Digest: ", StringComparison.Ordinal))
        {
            Digest = status["Digest: ".Length..].Trim();
            return null;
        }
        if (status.StartsWith("Status: ", StringComparison.Ordinal))
        {
            UpToDate = status.Contains("Image is up to date", StringComparison.Ordinal);
            _status = status["Status: ".Length..].Trim();
            // Docker says this once everything is in.
            foreach (var done in _layers.Values.Where(l => l.Real))
            {
                done.Downloaded = true;
                done.Done = true;
            }
            return null;
        }
        // "Pulling from library/postgres" carries the tag as its id, not a layer.
        if (id.Length == 0 || status.StartsWith("Pulling from", StringComparison.Ordinal) || status.StartsWith("Pulling ", StringComparison.Ordinal) && !status.StartsWith("Pulling fs layer", StringComparison.Ordinal))
        {
            _status = status;
            return null;
        }

        if (!_layers.TryGetValue(id, out var layer)) _layers[id] = layer = new Layer();
        var progress = line["progressDetail"] as JsonObject;
        // Counts in another unit (the containerd store's extraction counts seconds) aren't bytes.
        var bytes = Str(progress, "units") is "" or "bytes" or "B";
        var current = bytes ? ContainerPorts.Number(progress, "current") : 0;
        var size = bytes ? ContainerPorts.Number(progress, "total") : 0;
        switch (status)
        {
            case "Pulling fs layer":
            case "Waiting":
                layer.Real = true;
                break;
            case "Downloading":
                layer.Real = true;
                if (size > 0) layer.Total = size;
                layer.Current = current;
                break;
            case "Verifying Checksum":
            case "Download complete":
                layer.Downloaded = true;
                break;
            case "Extracting":
                layer.Real = true;
                layer.Downloaded = true;
                if (size > 0 && layer.Total <= 0) layer.Total = size;
                break;
            case "Pull complete":
            case "Already exists":
                layer.Real = true;
                layer.Downloaded = true;
                layer.Done = true;
                break;
        }
        // A blob that's only fetched isn't news worth showing; a layer's step is.
        if (layer.Real) _status = status;
        return null;
    }

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);
}
