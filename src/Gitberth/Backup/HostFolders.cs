using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Backup;

/// <summary>What a bind mount's source is on the host, as a helper found it: a folder (and its size), or not one.</summary>
/// <param name="Folder">It's a folder (a file, or a path that isn't there, is never offered).</param>
/// <param name="Bytes">Its files' size, when it was measured in time.</param>
public sealed record HostPathInfo(bool Folder, long? Bytes);

/// <summary>
/// Host folders in backups: which bind-mounted folders are offered (never Docker's socket, the system's own folders, the
/// host's root or Gitberth's own data), and what a helper of Gitberth's own image does with one: measure it, look whether it
/// holds files, fill it from a tar, or move its files aside. Gitberth can't see the host's paths; a helper mounts them.
/// </summary>
public static class HostFolders
{
    /// <summary>A folder bigger than this is listed unticked (with its size): ticking it is the owner's choice.</summary>
    public const long BigBytes = 2L << 30;

    /// <summary>How long measuring the folders may take: a huge folder is left unmeasured, and ticked.</summary>
    public static readonly TimeSpan MeasureFor = TimeSpan.FromMinutes(2);

    /// <summary>The host's own folders: never offered, nor anything under them.</summary>
    private static readonly string[] Never = ["/proc", "/sys", "/dev", "/run", "/var/run", "/etc", "/var/lib/docker"];

    /// <summary>A bind source as a clean absolute path (no trailing slash, no . or .. parts); null when it isn't one.</summary>
    public static string? Clean(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\0') || path.Contains('\n') || path.Contains('\r')) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) return null;
        return "/" + string.Join('/', parts);
    }

    /// <summary>
    /// Whether a container's bind source may be saved as a host folder: an absolute path that isn't the root, a socket,
    /// one of the host's own folders (or under one), nor Gitberth's own data (<paramref name="ownData"/>, its host path).
    /// </summary>
    public static bool Offered(string? source, string? ownData)
    {
        if (Clean(source) is not { } path || path == "/") return false;
        if (path.EndsWith(".sock", StringComparison.Ordinal) || path.EndsWith("/docker.sock", StringComparison.Ordinal)) return false;
        if (Never.Any(n => Under(path, n))) return false;
        return Clean(ownData) is not { } data || !Under(path, data) && !Under(data, path);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool Under(string path, string folder) =>
        path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>The save list's item of a folder ("folder:/srv/photos"), and its part in the file.</summary>
    public static string ItemId(string path) => "folder:" + path;

    public static string PartName(string path) => "folder:" + path;

    /// <summary>Where Replace keeps the folder's files as they were: /srv/photos.before-202611271830.</summary>
    public static string BeforePath(string path, DateTime at) => $"{path}.before-{at.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";

    /// <summary>A copy's own folder, next to the original's: /srv/photos-website2.</summary>
    public static string CopyPath(string path, string copyName) => $"{path}-{copyName}";

    /// <summary>A helper's create body: Gitberth's own image, its binds, no network; a command, or asleep for execs.</summary>
    internal static JsonObject Body(string image, IEnumerable<string> binds, string kind, IReadOnlyList<string>? command = null)
    {
        var body = new JsonObject
        {
            ["Image"] = image,
            ["User"] = "0:0",
            ["Labels"] = new JsonObject { [HostScanHelper.Label] = kind },
            ["NetworkDisabled"] = true,
            ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray([.. binds.Select(b => (JsonNode?)b)]), ["NetworkMode"] = "none" },
        };
        if (command is not null)
        {
            body["Entrypoint"] = new JsonArray([.. command.Take(1).Select(c => (JsonNode?)c)]);
            body["Cmd"] = new JsonArray([.. command.Skip(1).Select(c => (JsonNode?)c)]);
        }
        return body;
    }

    internal static string HelperName(string kind) => $"{HostScanHelper.NamePrefix}{kind}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";

    private const string MeasureScript =
        "for p in \"$@\"; do if [ -d \"$p\" ]; then printf 'd\\t%s\\t%s\\n' \"$p\" \"$(du -sxb \"$p\" 2>/dev/null | cut -f1)\"; " +
        "elif [ -e \"$p\" ]; then printf 'f\\t%s\\t\\n' \"$p\"; else printf 'm\\t%s\\t\\n' \"$p\"; fi; done";

    /// <summary>
    /// Each path as a helper finds it (mounted read-only): a folder and its size, or not a folder. Paths it couldn't look
    /// at in <paramref name="timeout"/> are left out.
    /// </summary>
    public static async Task<Dictionary<string, HostPathInfo>> MeasureAsync(IBackupEngine engine, string image, IReadOnlyList<string> paths, TimeSpan timeout,
        CancellationToken ct)
    {
        var found = new Dictionary<string, HostPathInfo>(StringComparer.Ordinal);
        if (paths.Count == 0) return found;
        var helper = HelperName("measure");
        var binds = paths.Select((p, i) => $"{p}:/h/{i.ToString(CultureInfo.InvariantCulture)}:ro").ToList();
        await engine.CreateContainerAsync(helper, Body(image, binds, "backup", ["sleep", "600"]), ct);
        try
        {
            await engine.StartContainerAsync(helper, ct);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            var output = new MemoryStream();
            try
            {
                await engine.ExecAsync(helper, ["sh", "-c", MeasureScript, "sh", .. paths.Select((_, i) => $"/h/{i.ToString(CultureInfo.InvariantCulture)}")], null, null, output, timer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // What came before the time ran out still counts.
            }
            foreach (var line in Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = line.Split('\t');
                if (cols.Length < 2 || !cols[1].StartsWith("/h/", StringComparison.Ordinal)
                    || !int.TryParse(cols[1][3..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= paths.Count) continue;
                long? bytes = cols.Length > 2 && long.TryParse(cols[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
                found[paths[index]] = new HostPathInfo(cols[0] == "d", cols[0] == "d" ? bytes : null);
            }
        }
        finally
        {
            await RemoveQuietlyAsync(engine, helper);
        }
        return found;
    }

    /// <summary>Whether the host folder holds anything (a helper's archive of it has more than the folder itself). A missing one is made, empty.</summary>
    public static async Task<bool> HasFilesAsync(IBackupEngine engine, string image, string path, CancellationToken ct)
    {
        var helper = HelperName("restore");
        await engine.CreateContainerAsync(helper, Body(image, [$"{path}:/h:ro"], "restore"), ct);
        try
        {
            await using var archive = await engine.GetArchiveAsync(helper, "/h", ct);
            var reader = new TarReader(archive, leaveOpen: true);
            var entries = 0;
            while (await reader.GetNextEntryAsync(copyData: false, ct) is not null)
                if (++entries > 1) return true;
            return false;
        }
        finally
        {
            await RemoveQuietlyAsync(engine, helper);
        }
    }

    /// <summary>The host folder filled from a tar whose entries are h/… (made when it isn't there); ownership and modes as the tar has them.</summary>
    public static async Task FillAsync(IBackupEngine engine, string image, string path, Stream tar, CancellationToken ct)
    {
        var helper = HelperName("restore");
        await engine.CreateContainerAsync(helper, Body(image, [$"{path}:/h"], "restore"), ct);
        try
        {
            await engine.PutArchiveAsync(helper, "/", tar, ct);
        }
        finally
        {
            await RemoveQuietlyAsync(engine, helper);
        }
    }

    /// <summary>
    /// The host folder's files moved into <paramref name="to"/> (made next to it, with its owner and mode), leaving it empty:
    /// Replace keeps what a folder held this way. Only the two folders are mounted, never their parent.
    /// </summary>
    public static async Task MoveAsideAsync(IBackupEngine engine, string image, string path, string to, CancellationToken ct)
    {
        var helper = HelperName("restore");
        const string script = "set -e; cp -a /h/. /b/; chown --reference=/h /b; chmod --reference=/h /b; find /h -mindepth 1 -delete";
        await engine.CreateContainerAsync(helper, Body(image, [$"{path}:/h", $"{to}:/b"], "restore", ["sh", "-c", script]), ct);
        try
        {
            await engine.StartContainerAsync(helper, ct);
            var exit = await engine.WaitContainerAsync(helper, ct);
            if (exit != 0) throw new InvalidOperationException($"Moving the files of {path} to {to} failed (exit code {exit.ToString(CultureInfo.InvariantCulture)}).");
        }
        finally
        {
            await RemoveQuietlyAsync(engine, helper);
        }
    }

    private static async Task RemoveQuietlyAsync(IBackupEngine engine, string helper)
    {
        try
        {
            await engine.RemoveContainerAsync(helper, force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A helper left behind is removed at the next start (BackupService.RecoverAsync).
        }
    }
}
