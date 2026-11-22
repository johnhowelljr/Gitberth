using System.Text.Json;

namespace Gitberth.Docker;

/// <summary>
/// Gitberth's own container, as the helpers it starts need it: the image it runs (a helper is a container of the same
/// image, so nothing is pulled) and where its data folder is on the Docker host (a helper's bind mounts are the host's
/// paths). Both are read once.
/// </summary>
public sealed class SelfContainer(DockerCli docker, GitberthOptions options, ILogger<SelfContainer> logger)
{
    private string? _dataHostDir;
    private string? _ownImage;

    /// <summary>Stands in for the host's data folder in tests (and outside a container, where it's the folder itself).</summary>
    internal string? DataHostDir { get; init; }

    /// <summary>
    /// Where a path under Gitberth's data folder is on the Docker host: the same path outside a container, else the source
    /// of the mount Gitberth's /data comes from (its volume's folder, or a host folder). Null when it can't be found.
    /// </summary>
    public async Task<string?> HostPathAsync(string path, CancellationToken ct = default)
    {
        var root = DataHostDir ?? (options.RunningInContainer ? await DataHostDirAsync(ct) : options.DataDir);
        return root is null ? null : Under(root, options.DataDir, path);
    }

    /// <summary>A path under <paramref name="dataDir"/> as it is under <paramref name="root"/>, its host folder; null outside it.</summary>
    internal static string? Under(string root, string dataDir, string path)
    {
        var rel = Path.GetRelativePath(dataDir, path).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
        return rel == "." ? root : root.TrimEnd('/') + "/" + rel;
    }

    /// <summary>The ID of the image Gitberth's own container runs; null outside a container, or when it can't be read.</summary>
    public async Task<string?> OwnImageAsync(CancellationToken ct = default)
    {
        if (_ownImage is not null) return _ownImage;
        if (options.OwnContainer is not { } self) return null;
        var r = await docker.RunAsync(["container", "inspect", "--format", "{{.Image}}", self], null, ct);
        return r.Ok && r.StdOut.Trim() is { Length: > 0 } id ? _ownImage = id : null;
    }

    private async Task<string?> DataHostDirAsync(CancellationToken ct)
    {
        if (_dataHostDir is not null) return _dataHostDir;
        if (options.OwnContainer is not { } self) return null;
        var r = await docker.RunAsync(["container", "inspect", "--format", "{{json .Mounts}}", self], null, ct);
        if (!r.Ok) return null;
        _dataHostDir = MountSource(r.StdOut, options.DataDir);
        if (_dataHostDir is null) logger.LogWarning("Gitberth's container has no mount at {Data}, so its helpers can't be given their host paths", options.DataDir);
        return _dataHostDir;
    }

    /// <summary>The host path of the mount at <paramref name="destination"/>, from `docker inspect`'s Mounts as JSON.</summary>
    internal static string? MountSource(string mountsJson, string destination)
    {
        try
        {
            using var doc = JsonDocument.Parse(mountsJson.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var m in doc.RootElement.EnumerateArray())
                if (m.TryGetProperty("Destination", out var d) && d.GetString()?.TrimEnd('/') == destination.TrimEnd('/')
                    && m.TryGetProperty("Source", out var s) && s.GetString() is { Length: > 0 } source)
                    return source;
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
