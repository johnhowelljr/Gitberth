using System.Security.Cryptography;
using System.Text;
using Gitberth.Deploy;
using Gitberth.Disk;
using Gitberth.Docker;

namespace Gitberth.Railpack;

/// <summary>What the disk cleanup or Clear build cache did to gitberth-buildkit's cache: its volume's size before and after.</summary>
/// <param name="Removed">The container and its volume went (nothing builds with Railpack any more, or the cache was cleared).</param>
public sealed record RailpackCacheChange(long? Before, long? After, bool Removed)
{
    /// <summary>What it freed, as the volume's size says it.</summary>
    public long Freed => Before is long before ? Math.Max(0, before - (After ?? 0)) : 0;
}

/// <summary>
/// gitberth-buildkit: Gitberth's own BuildKit, where Railpack's builds run (a pinned moby/buildkit, privileged, on Gitberth's
/// network). This owns the container, its volume (the build cache) and the buildx builder, all called gitberth-buildkit.
/// It's made on the first Railpack build, started before each one, and stopped when no build has needed it for a while.
/// Its RUN steps share its network namespace, and its buildkitd.toml (written by its entrypoint) points them at Docker's
/// DNS there, so they reach the app containers by name as the apps do. buildx reaches it through docker exec (the remote
/// driver's docker-container://), so it opens no port.
/// </summary>
/// <param name="api">Reads its volume's size (system/df); without it (a deploy's own copy in tests) the size isn't known.</param>
public sealed class RailpackBuildKit(DockerCli docker, GitberthOptions options, ILogger<RailpackBuildKit> logger, DockerApi? api = null) : IRailpackBuildKit, IDisposable
{
    /// <summary>The pin it was made from: a different one (Gitberth was updated) makes it again, its cache kept.</summary>
    public const string ImageLabel = "gitberth.buildkit-image";

    /// <summary>A short hash of what it runs (its buildkitd settings): a change makes it again too, its cache kept.</summary>
    public const string ArgsLabel = "gitberth.buildkit-args";

    private const string Name = RailpackRelease.BuildKitContainer;

    /// <summary>How long after the last Railpack build it's stopped.</summary>
    internal TimeSpan IdleStop { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long it may take to answer once it's started.</summary>
    internal TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The pause between two looks while it starts.</summary>
    internal TimeSpan ReadyPause { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long its volume's size is kept: reading it walks every volume of the engine.</summary>
    internal TimeSpan SizeKeptFor { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The disk's size, which bounds the cache; in tests, a stand-in.</summary>
    internal Func<long>? DiskTotal { get; init; }

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _builderChecked;
    private volatile bool _busy;
    private int _idleGeneration;
    private ITimer? _idle;
    private (long? Bytes, DateTimeOffset At)? _size;

    /// <summary>
    /// Makes it ready for a build, inside the build gate: made (its image pulled first, once) when it's missing, made again
    /// when it runs another pin or other settings, started when it's stopped, its buildx builder made when this process
    /// doesn't have it yet, and then asked to answer. Throws a <see cref="DeployException"/> with what went wrong.
    /// </summary>
    public async Task EnsureAsync(DeployLog log, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _busy = true;
            Interlocked.Increment(ref _idleGeneration);
            var script = Script(KeepStorageMb(DiskTotal?.Invoke() ?? ReadDiskTotal()));
            var found = await InspectAsync(ct);
            if (found is not null && (found.Image != RailpackRelease.BuildKitImage || found.Args != Stamp(script)))
            {
                log.Line($"{Name} was made from another BuildKit or other settings, so it's made again; its build cache stays.");
                await docker.RemoveAsync(Name, CancellationToken.None);
                found = null;
            }
            if (found is null)
            {
                if (!await docker.ImageExistsAsync(RailpackRelease.BuildKitImage, ct))
                {
                    log.Step(RailpackText.DownloadingBuildKit);
                    var pull = await docker.RunAsync(["pull", RailpackRelease.BuildKitImage], new ProcessOptions { OnLine = log.Line, MaxCapture = 64 * 1024 }, ct);
                    if (!pull.Ok) throw new DeployException(RailpackText.BuildKitFailed(Words(pull)));
                }
                var run = await docker.RunAsync(RunArgs(script), new ProcessOptions { MaxCapture = 64 * 1024 }, ct);
                if (!run.Ok)
                {
                    await docker.RemoveAsync(Name, CancellationToken.None);
                    var why = Words(run);
                    throw new DeployException(why.Contains("privileged", StringComparison.OrdinalIgnoreCase) ? RailpackText.NoPrivileged(why) : RailpackText.BuildKitFailed(why));
                }
            }
            else if (!found.Running)
            {
                log.Step(RailpackText.StartingBuildKit);
                var start = await docker.StartAsync(Name, ct);
                if (!start.Ok) throw new DeployException(RailpackText.BuildKitFailed(Words(start)));
            }
            if (!_builderChecked)
            {
                // The builder lives in Gitberth's container's /root/.docker/buildx, which a restart of Gitberth loses.
                if (!(await docker.RunAsync(["buildx", "inspect", Name], null, ct)).Ok)
                {
                    var create = await docker.RunAsync(["buildx", "create", "--name", Name, "--driver", "remote", $"docker-container://{Name}"], null, ct);
                    if (!create.Ok && !create.ErrorText.Contains("existing instance", StringComparison.OrdinalIgnoreCase))
                        throw new DeployException(RailpackText.BuildKitFailed(Words(create)));
                }
                _builderChecked = true;
            }
            await ReadyAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A build said its builder is gone ("no builder"): the next <see cref="EnsureAsync"/> makes it again.</summary>
    public void ForgetBuilder() => _builderChecked = false;

    /// <summary>A Railpack build ended, however it went: the container stops once no build has needed it for <see cref="IdleStop"/>.</summary>
    public void BuildEnded()
    {
        _busy = false;
        var generation = Interlocked.Increment(ref _idleGeneration);
        var timer = Time.CreateTimer(_ => _ = StopIfIdleAsync(generation), null, IdleStop, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _idle, timer)?.Dispose();
    }

    /// <summary>What buildkitd logged since <paramref name="since"/> (the frontend's own words go there), its last 40 lines.</summary>
    public async Task<IReadOnlyList<string>> LogTailAsync(DateTimeOffset since, CancellationToken ct)
    {
        var r = await docker.RunAsync(["logs", "--since", since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            "--tail", "40", Name], new ProcessOptions { MaxCapture = 64 * 1024 }, ct);
        return (r.StdOut + "\n" + r.StdErr).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
    }

    public async Task<RailpackBuildKitStatus> StatusAsync(CancellationToken ct)
    {
        var found = await InspectAsync(ct);
        return new RailpackBuildKitStatus(found is not null, found?.Running == true, found?.Image, found is null ? null : await CacheBytesAsync(ct));
    }

    /// <summary>
    /// The disk cleanup's turn, under the build gate: while it runs, the cache no build used for <paramref name="unusedFor"/>
    /// (all of it no build uses now, when null) goes; while it's stopped and no app builds with Railpack any more, the
    /// container and its volume go. Null when there's nothing of it, or nothing to do.
    /// </summary>
    public async Task<RailpackCacheChange?> CleanAsync(TimeSpan? unusedFor, bool anyAppUsesRailpack, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await InspectAsync(ct) is not { } found) return null;
            if (found.Running)
            {
                var before = await SizeAsync(ct);
                var prune = await docker.RunAsync(PruneArgs(unusedFor), new ProcessOptions { MaxCapture = 64 * 1024 }, ct);
                if (!prune.Ok) logger.LogWarning("Pruning gitberth-buildkit's cache failed: {Why}", Words(prune));
                return new RailpackCacheChange(before, await SizeAsync(ct), Removed: false);
            }
            if (anyAppUsesRailpack) return null;
            return await RemoveCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clear build cache, under the build gate: the container and its volume go, whatever runs; null when there's neither.</summary>
    public async Task<RailpackCacheChange?> ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await RemoveCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _idle, null)?.Dispose();

    /// <summary>Its docker run (design 2.4, with the spike's DNS and GC settings).</summary>
    internal List<string> RunArgs(string script) =>
    [
        "run", "-d", "--name", Name, "--label", $"{HostScanHelper.Label}={RailpackRelease.BuildKitHelperKind}",
        "--label", $"{ImageLabel}={RailpackRelease.BuildKitImage}", "--label", $"{ArgsLabel}={Stamp(script)}",
        "--privileged", "--restart", "no", "--network", options.Network, "-v", $"{Name}:/var/lib/buildkit",
        "--entrypoint", "sh", RailpackRelease.BuildKitImage, "-c", script,
    ];

    /// <summary>
    /// What it runs: buildkitd.toml pointing RUN steps at Docker's DNS on Gitberth's network (BuildKit would put Google's in
    /// place of 127.0.0.11), then buildkitd with its RUN steps in the container's own network namespace and its cache kept
    /// to <paramref name="keepStorageMb"/> MB (reserved 2048 MB, no free-space target, that many at most).
    /// </summary>
    internal static string Script(long keepStorageMb) =>
        "mkdir -p /etc/buildkit && printf \"[dns]\\n  nameservers = [\\\"127.0.0.11\\\"]\\n\" >/etc/buildkit/buildkitd.toml"
        + $" && exec buildkitd --oci-worker-net=host --oci-worker-gc --oci-worker-gc-keepstorage=2048,0,{keepStorageMb}";

    /// <summary>Its prune through buildx: by the cleanup's age, in whole hours, or everything no build uses now.</summary>
    internal static List<string> PruneArgs(TimeSpan? unusedFor) => unusedFor is { } age
        ? ["buildx", "prune", "--builder", Name, "--force", "--filter", $"until={Math.Max(1, (long)Math.Round(age.TotalHours))}h"]
        : ["buildx", "prune", "--builder", Name, "--force"];

    /// <summary>The cache's bound, in buildkitd's MB (10^6 bytes): half the build cache's cap for this disk, 2048 at least.</summary>
    internal static long KeepStorageMb(long diskTotal) => Math.Max(2048, DiskRules.CacheCap(diskTotal) / 2 / 1_000_000);

    /// <summary>The settings' hash its label keeps: 12 hex digits of SHA-256.</summary>
    internal static string Stamp(string script) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..12];

    private sealed record Found(bool Running, string? Image, string? Args);

    private async Task<Found?> InspectAsync(CancellationToken ct)
    {
        var r = await docker.RunAsync(["container", "inspect", "--format",
            $"{{{{.State.Running}}}} {{{{index .Config.Labels \"{ImageLabel}\"}}}} {{{{index .Config.Labels \"{ArgsLabel}\"}}}}", Name], null, ct);
        if (!r.Ok) return null;
        var parts = r.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? Part(int i) => i < parts.Length && parts[i] != "<no value>" ? parts[i] : null;
        return new Found(Part(0) == "true", Part(1), Part(2));
    }

    // Asks it to answer (buildx inspect --bootstrap) until it does, for ReadyWait at most.
    private async Task ReadyAsync(CancellationToken ct)
    {
        var until = Time.GetUtcNow() + ReadyWait;
        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ReadyWait);
            ProcessResult r;
            try
            {
                r = await docker.RunAsync(["buildx", "inspect", "--bootstrap", Name], new ProcessOptions { MaxCapture = 64 * 1024 }, wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                r = new ProcessResult(1, "", $"it didn't answer within {ReadyWait.TotalSeconds:0} seconds.");
            }
            if (r.Ok) return;
            if (Time.GetUtcNow() >= until) throw new DeployException(RailpackText.BuildKitFailed(Words(r)));
            await Task.Delay(ReadyPause, ct);
        }
    }

    private async Task StopIfIdleAsync(int generation)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_busy || generation != Volatile.Read(ref _idleGeneration)) return;
                if (await InspectAsync(CancellationToken.None) is not { Running: true }) return;
                var stop = await docker.StopAsync(Name, timeoutSeconds: 5);
                if (stop.Ok) logger.LogInformation("Stopped {Container}: no Railpack build needed it for {Minutes} minutes", Name, IdleStop.TotalMinutes);
                else logger.LogWarning("Stopping {Container} failed: {Why}", Name, Words(stop));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Gitberth is stopping.
        }
    }

    private async Task<RailpackCacheChange?> RemoveCoreAsync(CancellationToken ct)
    {
        var exists = await InspectAsync(ct) is not null;
        var before = await SizeAsync(ct);
        if (!exists && before is null) return null;
        if (exists) await docker.RemoveAsync(Name, ct);
        var removed = await docker.RemoveVolumeAsync(Name, ct);
        _size = null;
        // The builder points at a container that's gone: made again with it.
        _builderChecked = false;
        if (!removed.Ok && before is not null) logger.LogWarning("Removing the {Volume} volume failed: {Why}", Name, Words(removed));
        return new RailpackCacheChange(before, removed.Ok ? 0 : before, Removed: true);
    }

    // The volume's size, read now (after a change, or for the cleanup).
    private async Task<long?> SizeAsync(CancellationToken ct)
    {
        if (api is null) return null;
        try
        {
            var bytes = await api.VolumeSizeAsync(Name, ct);
            _size = (bytes, Time.GetUtcNow());
            return bytes;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            logger.LogDebug(ex, "The size of {Volume} couldn't be read", Name);
            return null;
        }
    }

    // The volume's size for a page: kept a minute, since reading it walks every volume.
    private async Task<long?> CacheBytesAsync(CancellationToken ct) =>
        _size is { } kept && Time.GetUtcNow() - kept.At < SizeKeptFor ? kept.Bytes : await SizeAsync(ct);

    private long ReadDiskTotal()
    {
        try
        {
            var path = Path.GetFullPath(options.DataDir);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
            return new DriveInfo(path).TotalSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    // A failed command's own words, on one line: docker run's first, not its "Run 'docker run --help' for more information".
    internal static string Words(ProcessResult r) =>
        r.ErrorText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Run 'docker", StringComparison.Ordinal))
        ?? $"it ended with exit code {r.ExitCode}.";
}
