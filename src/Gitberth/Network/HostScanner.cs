using System.Text.RegularExpressions;
using Gitberth.Docker;

namespace Gitberth.Network;

/// <summary>
/// Reads the host's network (<see cref="HostScan"/>) and keeps it for 30 seconds. Gitberth runs in a container, whose
/// /proc shows its own network, so it reads the host's through the "ports" helper on the host's network (see
/// <see cref="HostScanHelper"/>). Outside a container, in development, it reads /proc itself on Linux, and nothing on
/// Windows. A failed read is kept for as long, so a broken engine isn't asked again on every page load.
/// </summary>
public sealed partial class HostScanner(DockerCli docker, GitberthOptions options, ILogger<HostScanner> logger)
{
    public static readonly TimeSpan Kept = TimeSpan.FromSeconds(30);

    // Where a container's own mounts are listed: Docker mounts its hostname, hosts and resolv.conf from its own folder.
    private const string MountInfo = "/proc/self/mountinfo";

    // Gitberth's own container's ID, read once: it doesn't change while Gitberth runs.
    private static readonly Lazy<string?> OwnId = new(() =>
    {
        try { return ContainerIdIn(File.ReadAllText(MountInfo)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostScan? _last;
    // A scan read before this is out of date (see Forget).
    private DateTime _notBefore = DateTime.MinValue;
    private string? _image;
    private bool _failing;
    // Look for helpers left behind before the first scan (Gitberth may have stopped in the middle of one), and after a
    // scan that didn't finish.
    private bool _sweep = true;
    private int _refreshing;

    /// <summary>Reads a file of the host's /proc outside a container; tests read fixtures instead.</summary>
    internal Func<string, string?> ReadFile { get; init; } = path =>
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    };

    /// <summary>Whether to read the host through the helper; outside a container, /proc is the host's own.</summary>
    internal bool InContainer { get; init; } = options.RunningInContainer;

    /// <summary>Whether Gitberth runs on Linux, with a /proc to read.</summary>
    internal bool OnLinux { get; init; } = OperatingSystem.IsLinux();

    /// <summary>How long the helper may take. It reads a few files and exits in about a second.</summary>
    internal TimeSpan HelperTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a scan is kept.</summary>
    internal TimeSpan KeptFor { get; init; } = Kept;

    /// <summary>The last scan when it's under 30 seconds old, else a new one. Only one runs at a time.</summary>
    public Task<HostScan> GetAsync(CancellationToken ct = default) => GetAsync(null, ct);

    /// <summary>
    /// The last scan when it's under 30 seconds old and was read after <paramref name="changedAt"/>, else a new one. A
    /// container starting or stopping opens or closes docker-proxy's sockets, so callers pass when that last happened.
    /// </summary>
    public async Task<HostScan> GetAsync(DateTime? changedAt, CancellationToken ct = default)
    {
        if (Fresh(_last, changedAt) is { } fresh) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh(_last, changedAt) is { } again) return again;
            var scan = await ScanAsync(ct);
            if (scan.Error is not null && !_failing) logger.LogWarning("Couldn't read the host's sockets: {Error}", scan.Error);
            else if (scan.Error is null && _failing) logger.LogInformation("Reading the host's sockets works again");
            _failing = scan.Error is not null;
            return _last = scan;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The last scan, however old, without reading again; null before the first.</summary>
    public HostScan? Last => _last;

    /// <summary>
    /// The last scan whatever its age, read again in the background when it's out of date: for what rarely changes, like
    /// the host's LAN address, so no page waits on the helper. Only the very first look waits for one.
    /// </summary>
    public async Task<HostScan> LatestAsync(CancellationToken ct = default)
    {
        if (_last is not { } last) return await GetAsync(ct);
        if (Fresh(last, null) is null) RefreshInBackground();
        return last;
    }

    /// <summary>The host's LAN address as last detected (see <see cref="LatestAsync"/>); null when it couldn't be.</summary>
    public async Task<string?> DetectedAddressAsync(CancellationToken ct = default) => (await LatestAsync(ct)).Address;

    /// <summary>Drops the kept scan, after Gitberth changed what listens on the host. <see cref="Last"/> stays until the next.</summary>
    public void Forget() => _notBefore = DateTime.UtcNow;

    private HostScan? Fresh(HostScan? scan, DateTime? changedAt) =>
        scan is not null && DateTime.UtcNow - scan.ReadAt < KeptFor && scan.ReadAt >= _notBefore && !(changedAt > scan.ReadAt) ? scan : null;

    // One at a time; a failure is kept like any scan's, and logged by it.
    private void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't read the host's network again");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private async Task<HostScan> ScanAsync(CancellationToken ct)
    {
        // Stamped before reading: a container that starts or stops while the files are read makes this scan out of date.
        var at = DateTime.UtcNow;
        if (!InContainer)
        {
            if (!OnLinux) return HostScan.Failed("Gitberth isn't running on Linux here", at);
            var files = HostScanHelper.Files.Select(f => (f, ReadFile(f))).Where(f => f.Item2 is not null).ToDictionary(f => f.f, f => f.Item2!);
            return HostScanHelper.Read(files, at);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(HelperTimeout);
        var name = HostScanHelper.NewName();
        try
        {
            if (await OwnImageAsync(timer.Token) is not { } image) return HostScan.Failed("Gitberth couldn't find its own container's image", at);
            if (_sweep) await RemoveLeftoversAsync(timer.Token);
            at = DateTime.UtcNow;
            var r = await docker.RunAsync(HostScanHelper.RunArgs(image, name), new ProcessOptions { MaxCapture = HostScanHelper.MaxOutput }, timer.Token);
            if (r.Ok) return HostScanHelper.ReadOutput(r.StdOut, at);
            // Docker removes a helper that fails to start; the next scan still looks, in case the engine finished one late.
            _sweep = true;
            return HostScan.Failed(FirstLine(r.ErrorText), at);
        }
        catch (OperationCanceledException)
        {
            // `docker run --rm` only removes a container that started: one the CLI was stopped before starting would stay
            // behind, Created, with an anonymous volume for the image's /data.
            _sweep = true;
            await RemoveAsync(name);
            if (ct.IsCancellationRequested) throw;
            return HostScan.Failed($"the helper container didn't finish within {HelperTimeout.TotalSeconds:0} seconds", at);
        }
    }

    // Helpers a scan that didn't finish left behind, or Gitberth stopping in the middle of one.
    private async Task RemoveLeftoversAsync(CancellationToken ct)
    {
        var listed = await docker.RunAsync(HostScanHelper.LeftoverArgs, null, ct);
        if (!listed.Ok) return;
        var ids = listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length > 0)
        {
            logger.LogInformation("Removing {Count} helper container(s) left behind", ids.Length);
            if (!(await docker.RunAsync(["rm", "-f", "-v", .. ids], null, ct)).Ok) return;
        }
        _sweep = false;
    }

    // After a timeout the scan's own time is up: the removal gets a few seconds of its own.
    private async Task RemoveAsync(string name)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await docker.RunAsync(["rm", "-f", "-v", name], null, timer.Token);
        }
        catch (OperationCanceledException)
        {
            // The next scan looks for it again.
        }
    }

    /// <summary>
    /// The image ID of Gitberth's own container, which the helper runs. Gitberth's hostname is its container's short ID
    /// unless it was set otherwise; then its ID comes from the container's own files in /proc/self/mountinfo.
    /// </summary>
    private async Task<string?> OwnImageAsync(CancellationToken ct)
    {
        if (_image is not null) return _image;
        foreach (var self in new[] { Environment.MachineName, MountedId() }.OfType<string>().Distinct())
        {
            var r = await docker.RunAsync(["container", "inspect", "--format", "{{.Image}}", self], null, ct);
            if (r.Ok && r.StdOut.Trim() is { Length: > 0 } image) return _image = image;
        }
        return null;
    }

    private string? MountedId() => ContainerIdIn(ReadFile(MountInfo));

    /// <summary>
    /// The ID of the container Gitberth runs in, from its /proc/self/mountinfo; null outside a container, or where there's
    /// no such file. Its hostname is that ID's first 12 digits only unless it was started with another, so this is what
    /// tells Gitberth's own container apart (see <see cref="GitberthOptions.OwnContainer"/>).
    /// </summary>
    public static string? OwnIdFromMounts() => OwnId.Value;

    /// <summary>The ID of the container whose /proc/self/mountinfo this is: Docker mounts its hostname and hosts files from its own folder.</summary>
    internal static string? ContainerIdIn(string? mountinfo) =>
        mountinfo is not null && ContainerId().Match(mountinfo) is { Success: true } m ? m.Groups[1].Value : null;

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "the helper container failed";

    // Docker mounts each container's hostname, hosts and resolv.conf from /var/lib/docker/containers/<id>/.
    [GeneratedRegex(@"/containers/([0-9a-f]{64})/")]
    private static partial Regex ContainerId();
}
