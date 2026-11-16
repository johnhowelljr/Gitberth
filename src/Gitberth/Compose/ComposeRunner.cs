using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Compose;

/// <summary>
/// Runs `docker compose` for Compose apps, through the docker CLI (so every change it makes is announced to the audit log
/// first): with none of Gitberth's own environment, so a Compose file's ${…} never reads Gitberth's settings, and with the
/// app's variables only through the .env it writes. Also knows where Gitberth's data folder is on the Docker host: bind
/// mounts are the host's paths, and Gitberth runs in a container.
/// </summary>
public sealed class ComposeRunner(DockerCli docker, GitberthOptions options, ILogger<ComposeRunner> logger)
{
    /// <summary>The label value of the helper a preview's Compose file is read in.</summary>
    public const string HelperKind = "compose";

    private string? _dataHostDir;
    private string? _ownImage;

    /// <summary>Stands in for the host's data folder in tests (and outside a container, where it's the folder itself).</summary>
    internal string? DataHostDir { get; init; }

    /// <summary>
    /// docker compose -p &lt;project&gt; [-f &lt;file&gt;] [--project-directory &lt;dir&gt;] &lt;command&gt;, always from Gitberth's own
    /// empty folder (<see cref="WorkDir"/>), never from a release: nothing Compose or the docker CLI looks up by a relative
    /// path (a provider's program, a plugin, a file) can come from a repo's files. The release is the project's folder.
    /// </summary>
    public async Task<ProcessResult> RunAsync(string project, string? file, string? projectDir, IReadOnlyList<string> command, Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        // The project's own containers, networks and volumes are announced by its name (DockerCli); the ones the file names
        // itself aren't called after it, so they're announced here.
        var named = file is not null && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? NamedIn(ReadModel(file)) : NamedThings.None;
        using var networks = named.Networks.Count > 0 ? docker.Expect("network", Audit.ExpectedChanges.AnyNetwork, [.. named.Networks]) : null;
        using var joined = named.External.Count > 0 ? docker.Expect("network", ["connect", "disconnect"], [.. named.External]) : null;
        using var volumes = named.Volumes.Count > 0 ? docker.Expect("volume", Audit.ExpectedChanges.AnyVolume, [.. named.Volumes]) : null;
        return await docker.RunAsync(Args(project, file, projectDir, command), Options(onLine, 256 * 1024), ct);
    }

    /// <summary>How every docker compose of Gitberth's runs: none of Gitberth's environment, from <see cref="WorkDir"/>.</summary>
    public ProcessOptions Options(Action<string>? onLine = null, int maxCapture = 256 * 1024) => new()
    {
        CleanEnvironment = true,
        WorkingDirectory = WorkDir(),
        OnLine = onLine,
        MaxCapture = maxCapture,
    };

    /// <summary>The networks and volumes a resolved Compose file names itself: its own (made and removed with it), and the external networks its services join.</summary>
    internal sealed record NamedThings(IReadOnlyList<string> Networks, IReadOnlyList<string> External, IReadOnlyList<string> Volumes)
    {
        public static readonly NamedThings None = new([], [], []);
    }

    /// <summary>
    /// What a resolved file calls its networks and volumes in Docker: Compose resolves every one's name (the project's
    /// &lt;project&gt;_&lt;key&gt;, or the one the file gives), so the ones that aren't the project's are told by it.
    /// </summary>
    internal static NamedThings NamedIn(JsonObject? model)
    {
        if (model is null) return NamedThings.None;
        var project = model["name"] is JsonValue p && p.TryGetValue<string>(out var n) ? n : "";
        List<string> networks = [], external = [], volumes = [];
        foreach (var (section, list) in new[] { ("networks", networks), ("volumes", volumes) })
        {
            if (model[section] is not JsonObject entries) continue;
            foreach (var (key, node) in entries)
            {
                var name = node?["name"] is JsonValue v && v.TryGetValue<string>(out var given) && given.Length > 0 ? given : null;
                var isExternal = node?["external"] is JsonValue e && e.GetValueKind() == JsonValueKind.True;
                if (isExternal)
                {
                    if (section == "networks") external.Add(name ?? key);
                    continue;
                }
                if (name is not null && (project.Length == 0 || !name.StartsWith(project + "_", StringComparison.Ordinal))) list.Add(name);
            }
        }
        return new NamedThings(networks, external, volumes);
    }

    private static JsonObject? ReadModel(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The docker CLI's arguments for a compose command: global flags, the project, its file and folder, then the command.</summary>
    internal static List<string> Args(string project, string? file, string? projectDir, IReadOnlyList<string> command)
    {
        var args = new List<string> { "compose", "--ansi", "never", "--progress", "plain", "-p", project };
        if (file is not null) args.AddRange(["-f", file]);
        if (projectDir is not null) args.AddRange(["--project-directory", projectDir]);
        args.AddRange(command);
        return args;
    }

    /// <summary>
    /// Compose's resolved file (docker compose config --format json) for <paramref name="file"/>, read from
    /// <paramref name="projectDir"/> with its .env. With <paramref name="sandbox"/> (a preview's file, which a pull request
    /// wrote), it's read in a helper container of Gitberth's own image that only has the release's folder, read-only, and no
    /// network: a file that names env_file: /proc/1/environ or includes a file of Gitberth's gets nothing of Gitberth's.
    /// </summary>
    public async Task<(JsonObject? Model, string? Error)> ConfigAsync(string project, string file, string projectDir, string releaseDir, bool sandbox, CancellationToken ct)
    {
        ProcessResult result;
        if (sandbox && options.RunningInContainer)
        {
            if (await OwnImageAsync(ct) is not { } image) return (null, "Gitberth couldn't find its own image to read the preview's Compose file in.");
            var host = await HostPathAsync(releaseDir, ct);
            if (host is null) return (null, "Gitberth couldn't find where its data folder is on the Docker host.");
            List<string> args =
            [
                "run", "--rm", "--name", Docker.HostScanHelper.NewName(HelperKind), "--label", $"{Docker.HostScanHelper.Label}={HelperKind}",
                "--network", "none", "--read-only", "--cap-drop", "ALL", "--memory", "256m", "--tmpfs", "/tmp", "--tmpfs", options.DataDir,
                "-e", "HOME=/tmp", "-v", $"{host}:{releaseDir}:ro", "-w", "/tmp", "--entrypoint", "docker", image,
                .. Args(project, file, projectDir, ["config", "--format", "json"]),
            ];
            result = await docker.RunAsync(args, new ProcessOptions { CleanEnvironment = true, MaxCapture = 8 * 1024 * 1024 }, ct);
        }
        else
        {
            result = await docker.RunAsync(Args(project, file, projectDir, ["config", "--format", "json"]), Options(maxCapture: 8 * 1024 * 1024), ct);
        }
        if (!result.Ok) return (null, Clean(result.ErrorText));
        try
        {
            return JsonNode.Parse(result.StdOut) is JsonObject model ? (model, null) : (null, "docker compose config gave no file.");
        }
        catch (JsonException ex)
        {
            return (null, "docker compose config gave something that isn't JSON: " + ex.Message);
        }
    }

    // Compose's own words, without the "validating …:" path prefix where it adds one.
    private static string Clean(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "docker compose config failed." : string.Join(' ', lines.TakeLast(3));
    }

    /// <summary>
    /// Where a path under Gitberth's data folder is on the Docker host: the same path outside a container, else the source
    /// of the mount Gitberth's /data comes from (its volume's folder, or a host folder). Null when it can't be found.
    /// </summary>
    public async Task<string?> HostPathAsync(string path, CancellationToken ct = default)
    {
        var rel = Path.GetRelativePath(options.DataDir, path).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
        var root = DataHostDir ?? (options.RunningInContainer ? await DataHostDirAsync(ct) : options.DataDir);
        if (root is null) return null;
        return rel == "." ? root : root.TrimEnd('/') + "/" + rel;
    }

    private async Task<string?> DataHostDirAsync(CancellationToken ct)
    {
        if (_dataHostDir is not null) return _dataHostDir;
        if (options.OwnContainer is not { } self) return null;
        var r = await docker.RunAsync(["container", "inspect", "--format", "{{json .Mounts}}", self], null, ct);
        if (!r.Ok) return null;
        _dataHostDir = MountSource(r.StdOut, options.DataDir);
        if (_dataHostDir is null) logger.LogWarning("Gitberth's container has no mount at {Data}, so Compose apps' bind mounts can't be given their host paths", options.DataDir);
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

    private async Task<string?> OwnImageAsync(CancellationToken ct)
    {
        if (_ownImage is not null) return _ownImage;
        if (options.OwnContainer is not { } self) return null;
        var r = await docker.RunAsync(["container", "inspect", "--format", "{{.Image}}", self], null, ct);
        return r.Ok && r.StdOut.Trim() is { Length: > 0 } id ? _ownImage = id : null;
    }

    /// <summary>
    /// Gitberth's own empty folder every docker compose runs from: /data/compose/.run (a slug never starts with '.', so it's
    /// never an app's), which nothing of a repo's is ever in and only Gitberth opens.
    /// </summary>
    public string WorkDir()
    {
        var dir = Path.Combine(options.DataDir, "compose", ".run");
        try
        {
            Directory.CreateDirectory(dir);
            ComposeReleases.OwnerOnly(dir, folder: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return options.DataDir;
        }
        return dir;
    }
}
