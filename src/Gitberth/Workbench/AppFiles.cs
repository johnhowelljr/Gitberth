using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Workbench;

/// <summary>A volume or host folder of an app's container, as the Files tab offers it.</summary>
/// <param name="Volume">Its volume's name; null for a host folder.</param>
/// <param name="Source">Where it is on the Docker host (a volume's folder there, or the host folder).</param>
/// <param name="Target">Where the container has it.</param>
public sealed record FileMount(string? Volume, string Source, string Target, bool ReadOnly)
{
    /// <summary>"data (volume)", "/srv/media (host folder)".</summary>
    public string Title => Volume is { } v ? v : Source;
}

public enum FileType { File, Folder, Link, Other }

/// <summary>One entry of a folder: its name, type, size, when it changed, its mode (0644) and owner, and a link's target.</summary>
public sealed record FileEntry(string Name, FileType Type, long Size, DateTime? Modified, string Mode, int Uid, int Gid, string? LinkTarget);

/// <summary>A folder's entries (folders first, then by name), whether there were more than shown, or why it can't be read.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, bool Truncated, string? Problem);

/// <summary>What a change did: why it was refused, or what it says now.</summary>
public sealed record FileOutcome(bool Ok, string Message);

/// <summary>Who and what a change is about, for the audit log.</summary>
public sealed record FilesOf(string AppName, int? AppId, string Container, FileMount Mount);

/// <summary>
/// An app's files (B3): each of its container's volumes and host folders, browsed and changed through a helper container
/// of Gitberth's own image (nothing is pulled, and nothing runs in the app's container, whose image may have no tools),
/// with that one volume or folder mounted at /mnt: read-only unless a change needs it, no network, root with only the
/// capabilities it needs to read and give files their owners. Every path is one Gitberth made from names it checked, and
/// the helper's script resolves it (links followed) and refuses one that ends outside /mnt. Docker's socket and
/// Gitberth's own data are never offered. Uploads, new folders, renames, deletes, edits and downloads go in the audit log
/// (paths and sizes, never contents). Each helper is made for one listing or change and removed right after it, so none
/// keeps holding the app's volume.
/// </summary>
public sealed class AppFiles(DockerApi api, SelfContainer self, GitberthOptions options, ILogger<AppFiles> logger, Audit.AuditLog? audit = null) : IAsyncDisposable
{
    public const string HelperKind = "files";
    public const long MaxEdit = 1024 * 1024;
    public const int MaxEntries = 5000;

    // ---- What's offered ----

    /// <summary>
    /// The container's volumes and host folders as Docker lists its mounts, but Docker's socket, Gitberth's own data
    /// (<paramref name="dataHostDir"/>, where Gitberth's /data is on the host) and the host's system files.
    /// </summary>
    public static IReadOnlyList<FileMount> MountsOf(JsonObject? inspect, string? dataHostDir)
    {
        var list = new List<FileMount>();
        foreach (var m in inspect?["Mounts"] as JsonArray ?? [])
        {
            if (m is not JsonObject o) continue;
            var type = Str(o, "Type");
            var source = Str(o, "Source") ?? "";
            var target = Str(o, "Destination") ?? "";
            if (type is not ("volume" or "bind") || source.Length == 0 || !target.StartsWith('/')) continue;
            if (Hidden(source, dataHostDir) || Hidden(target, null)) continue;
            var rw = o["RW"] is JsonValue v && v.TryGetValue<bool>(out var w) ? w : true;
            list.Add(new FileMount(type == "volume" ? Str(o, "Name") : null, source, target, !rw));
        }
        return list.OrderBy(f => f.Target, StringComparer.Ordinal).ToList();
    }

    // Docker's socket, Gitberth's own data, and the host's system files: never offered.
    private static bool Hidden(string path, string? dataHostDir)
    {
        var p = path.TrimEnd('/');
        if (p.EndsWith("docker.sock", StringComparison.Ordinal)) return true;
        if (dataHostDir is { Length: > 0 } data && (p == data.TrimEnd('/') || p.StartsWith(data.TrimEnd('/') + "/", StringComparison.Ordinal))) return true;
        if (p.Length == 0) return true;
        foreach (var system in new[] { "/proc", "/sys", "/dev", "/etc/localtime", "/etc/timezone", "/var/lib/docker/containers", "/run/docker", "/var/run/docker" })
            if (p == system || p.StartsWith(system + "/", StringComparison.Ordinal)) return true;
        return p == "/var/lib/docker";
    }

    /// <summary>Where Gitberth's own data is on the Docker host, which is never offered.</summary>
    public Task<string?> DataHostDirAsync(CancellationToken ct = default) => self.HostPathAsync(options.DataDir, ct);

    // ---- Paths ----

    /// <summary>
    /// A path under a mount as Gitberth uses it: its names joined by /, no empty, . or .. name, no NUL; "" for the mount
    /// itself. Null when it can't be one.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Length > 4096 || path.Contains('\0')) return null;
        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var n in names)
            if (n is "." or ".." || Encoding.UTF8.GetByteCount(n) > 255) return null;
        return string.Join('/', names);
    }

    /// <summary>Why a name can't be a file's or folder's (new, renamed or uploaded); null when it can.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give it a name.";
        if (name.Contains('/') || name.Contains('\0')) return "A name has no / in it.";
        if (name is "." or "..") return $"{name} can't be a name.";
        if (Encoding.UTF8.GetByteCount(name) > 255) return "Keep the name to 255 bytes.";
        if (name.StartsWith(".gitberth-upload-", StringComparison.Ordinal)) return "That name is Gitberth's own, for an upload on its way.";
        return null;
    }

    /// <summary>The parent of a path ("" for a name in the mount's root) and its last name.</summary>
    public static (string Dir, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
    }

    // ---- The helper's scripts: $1, $2, $3 are a path under /mnt, and names; never put into the script ----

    // Refuses a path that resolves (links followed) outside /mnt; new files and folders get the usual modes (0644, 0755).
    private const string Guard = "umask 022; inside() { case \"$(realpath -m -- \"$1\")\" in /mnt|/mnt/*) return 0;; esac; echo 'outside the volume' >&2; exit 3; }; ";

    internal const string ListScript = Guard +
        "p=\"/mnt/$1\"; inside \"$p\"; [ -e \"$p\" ] || { echo missing >&2; exit 4; }; [ -d \"$p\" ] || { echo 'not a folder' >&2; exit 7; }; " +
        "cd \"$p\" && find . -mindepth 1 -maxdepth 1 -printf '%y\\t%s\\t%T@\\t%m\\t%U\\t%G\\t%l\\t%f\\0' | head -z -n " + "5001";

    internal const string ReadScript = Guard +
        "p=\"/mnt/$1\"; inside \"$p\"; [ -f \"$p\" ] || { echo 'not a file' >&2; exit 4; }; " +
        "if [ \"$2\" -ge 0 ] && [ \"$(stat -c %s -- \"$p\")\" -gt \"$2\" ]; then echo 'too large' >&2; exit 6; fi; cat -- \"$p\"";

    internal const string ArchiveScript = Guard +
        "p=\"/mnt/$1\"; inside \"$p\"; [ -d \"$p\" ] || { echo 'not a folder' >&2; exit 4; }; cd \"$p\" && tar -czf - .";

    internal const string WriteScript = Guard +
        "d=\"/mnt/$1\"; inside \"$d\"; [ -d \"$d\" ] || { echo 'no such folder' >&2; exit 4; }; t=\"$d/$2\"; inside \"$t\"; " +
        "[ ! -d \"$t\" ] || { echo 'a folder' >&2; exit 5; }; tmp=\"$d/.gitberth-upload-$$\"; " +
        "cat > \"$tmp\" || { rm -f -- \"$tmp\"; exit 8; }; " +
        "if [ -e \"$t\" ]; then chown --reference=\"$t\" -- \"$tmp\"; chmod --reference=\"$t\" -- \"$tmp\"; else chown --reference=\"$d\" -- \"$tmp\"; fi; " +
        "mv -f -- \"$tmp\" \"$t\"";

    internal const string FolderScript = Guard +
        "d=\"/mnt/$1\"; inside \"$d\"; [ -d \"$d\" ] || { echo 'no such folder' >&2; exit 4; }; t=\"$d/$2\"; " +
        "if [ -e \"$t\" ] || [ -L \"$t\" ]; then echo exists >&2; exit 5; fi; mkdir -- \"$t\" && chown --reference=\"$d\" -- \"$t\"";

    internal const string RenameScript = Guard +
        "d=\"/mnt/$1\"; inside \"$d\"; a=\"$d/$2\"; b=\"$d/$3\"; [ -e \"$a\" ] || [ -L \"$a\" ] || { echo missing >&2; exit 4; }; " +
        "if [ -e \"$b\" ] || [ -L \"$b\" ]; then echo exists >&2; exit 5; fi; mv -- \"$a\" \"$b\"";

    internal const string DeleteScript = Guard +
        "d=\"/mnt/$1\"; inside \"$d\"; t=\"$d/$2\"; [ -e \"$t\" ] || [ -L \"$t\" ] || { echo missing >&2; exit 4; }; rm -rf -- \"$t\"";

    // ---- Listing and reading ----

    public async Task<FileListing> ListAsync(string container, FileMount mount, string path, CancellationToken ct = default)
    {
        if (Normalize(path) is not { } p) return new FileListing([], false, "That path can't be in the volume.");
        using var stdout = new MemoryStream();
        var (exit, said) = await RunAsync(container, mount, write: false, ListScript, [p], null, stdout, ct);
        if (exit != 0) return new FileListing([], false, Sentence(exit, said, p, mount));
        var entries = Parse(stdout.ToArray());
        var truncated = entries.Count > MaxEntries;
        return new FileListing(entries.Take(MaxEntries).ToList(), truncated, null);
    }

    /// <summary>find's -printf records ("%y\t%s\t%T@\t%m\t%U\t%G\t%l\t%f\0"), folders first, then by name.</summary>
    internal static List<FileEntry> Parse(byte[] output)
    {
        var list = new List<FileEntry>();
        foreach (var record in Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Split('\t', 8);
            if (f.Length < 8) continue;
            var type = f[0] switch { "f" => FileType.File, "d" => FileType.Folder, "l" => FileType.Link, _ => FileType.Other };
            long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size);
            DateTime? modified = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch)
                ? DateTime.UnixEpoch.AddSeconds(epoch) : null;
            int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var uid);
            int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var gid);
            var mode = f[3].Length > 0 ? f[3].PadLeft(4, '0') : "";
            list.Add(new FileEntry(f[7], type, size, modified, mode, uid, gid, f[6].Length > 0 ? f[6] : null));
        }
        return list.OrderBy(e => e.Type == FileType.Folder ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>A small text file's contents (≤ 1 MiB, UTF-8 without NUL bytes) for the editor; or why it can't be edited here.</summary>
    public async Task<(string? Text, string? Problem)> ReadTextAsync(string container, FileMount mount, string path, CancellationToken ct = default)
    {
        if (Normalize(path) is not { Length: > 0 } p) return (null, "That path can't be in the volume.");
        using var stdout = new MemoryStream();
        var (exit, said) = await RunAsync(container, mount, write: false, ReadScript, [p, MaxEdit.ToString(CultureInfo.InvariantCulture)], null, stdout, ct);
        if (exit == 6) return (null, "It's larger than 1 MiB, the most the editor takes: download it instead.");
        if (exit != 0) return (null, Sentence(exit, said, p, mount));
        var bytes = stdout.ToArray();
        if (bytes.Contains((byte)0)) return (null, "It isn't a text file: download it instead.");
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), null);
        }
        catch (DecoderFallbackException)
        {
            return (null, "It isn't UTF-8 text: download it instead.");
        }
    }

    /// <summary>A file, or a folder as a .tar.gz of what's in it, written to <paramref name="into"/> as it's read.</summary>
    public async Task<string?> DownloadAsync(FilesOf of, string path, bool folder, Stream into, CancellationToken ct = default)
    {
        if (Normalize(path) is not { } p || !folder && p.Length == 0) return "That path can't be in the volume.";
        var counting = new CountingStream(into);
        var (exit, said) = await RunAsync(of.Container, of.Mount, write: false, folder ? ArchiveScript : ReadScript, folder ? [p] : [p, "-1"], null, counting, ct);
        if (exit != 0) return Sentence(exit, said, p, of.Mount);
        Record("app.files.downloaded", $"Downloaded {Shown(of, p)}{(folder ? " as a .tar.gz" : "")} from {of.AppName} ({Size(counting.Count)}).", of, p,
            [new("Size", null, Size(counting.Count))]);
        return null;
    }

    // ---- Changes ----

    /// <summary>Writes a file in <paramref name="dir"/> from <paramref name="content"/>: a new one gets the folder's owner, one that's there keeps its owner and mode.</summary>
    public async Task<FileOutcome> WriteAsync(FilesOf of, string dir, string name, Stream content, bool edit = false, CancellationToken ct = default)
    {
        if (Normalize(dir) is not { } d) return new FileOutcome(false, "That folder can't be in the volume.");
        if (NameProblem(name) is { } problem) return new FileOutcome(false, problem);
        if (of.Mount.ReadOnly) return new FileOutcome(false, ReadOnlyMount(of.Mount));
        var counting = new CountingReadStream(content);
        var (exit, said) = await RunAsync(of.Container, of.Mount, write: true, WriteScript, [d, name], counting, null, ct);
        var path = d.Length == 0 ? name : $"{d}/{name}";
        if (exit != 0) return new FileOutcome(false, exit == 5 ? $"{name} is a folder there: a file can't take its place." : Sentence(exit, said, path, of.Mount));
        Record(edit ? "app.files.edited" : "app.files.uploaded", $"{(edit ? "Edited" : "Uploaded")} {Shown(of, path)} in {of.AppName} ({Size(counting.Count)}).", of, path,
            [new("Size", null, Size(counting.Count))]);
        return new FileOutcome(true, edit ? $"Saved {name}." : $"Uploaded {name} ({Size(counting.Count)}).");
    }

    public async Task<FileOutcome> MakeFolderAsync(FilesOf of, string dir, string name, CancellationToken ct = default)
    {
        if (Normalize(dir) is not { } d) return new FileOutcome(false, "That folder can't be in the volume.");
        if (NameProblem(name) is { } problem) return new FileOutcome(false, problem);
        if (of.Mount.ReadOnly) return new FileOutcome(false, ReadOnlyMount(of.Mount));
        var (exit, said) = await RunAsync(of.Container, of.Mount, write: true, FolderScript, [d, name], null, null, ct);
        var path = d.Length == 0 ? name : $"{d}/{name}";
        if (exit != 0) return new FileOutcome(false, exit == 5 ? $"There's something called {name} there already." : Sentence(exit, said, path, of.Mount));
        Record("app.files.folder", $"Made the folder {Shown(of, path)} in {of.AppName}.", of, path, []);
        return new FileOutcome(true, $"Made the folder {name}.");
    }

    public async Task<FileOutcome> RenameAsync(FilesOf of, string dir, string name, string newName, CancellationToken ct = default)
    {
        if (Normalize(dir) is not { } d) return new FileOutcome(false, "That folder can't be in the volume.");
        if ((NameProblem(name) ?? NameProblem(newName)) is { } bad) return new FileOutcome(false, bad);
        if (name == newName) return new FileOutcome(true, "Nothing changed.");
        if (of.Mount.ReadOnly) return new FileOutcome(false, ReadOnlyMount(of.Mount));
        var (exit, said) = await RunAsync(of.Container, of.Mount, write: true, RenameScript, [d, name, newName], null, null, ct);
        var path = d.Length == 0 ? name : $"{d}/{name}";
        if (exit != 0) return new FileOutcome(false, exit == 5 ? $"There's something called {newName} there already." : Sentence(exit, said, path, of.Mount));
        var to = d.Length == 0 ? newName : $"{d}/{newName}";
        Record("app.files.renamed", $"Renamed {Shown(of, path)} to {newName} in {of.AppName}.", of, path, [new("Name", name, newName)]);
        return new FileOutcome(true, $"Renamed {name} to {newName}.");
    }

    public async Task<FileOutcome> DeleteAsync(FilesOf of, string dir, string name, CancellationToken ct = default)
    {
        if (Normalize(dir) is not { } d) return new FileOutcome(false, "That folder can't be in the volume.");
        if (NameProblem(name) is { } problem) return new FileOutcome(false, problem);
        if (of.Mount.ReadOnly) return new FileOutcome(false, ReadOnlyMount(of.Mount));
        var (exit, said) = await RunAsync(of.Container, of.Mount, write: true, DeleteScript, [d, name], null, null, ct);
        var path = d.Length == 0 ? name : $"{d}/{name}";
        if (exit != 0) return new FileOutcome(false, Sentence(exit, said, path, of.Mount));
        Record("app.files.deleted", $"Deleted {Shown(of, path)} from {of.AppName}.", of, path, []);
        return new FileOutcome(true, $"Deleted {name}.");
    }

    // ---- Words ----

    private static string ReadOnlyMount(FileMount mount) => $"{mount.Target} is mounted read-only in the app, so Gitberth doesn't change it here.";

    // What an exit of the helper's script means, in a sentence.
    internal static string Sentence(int exit, string said, string path, FileMount mount) => exit switch
    {
        3 => "That path leads outside the volume (through a link), so Gitberth doesn't follow it.",
        4 => path.Length == 0 ? $"{mount.Target} isn't there." : $"{path} isn't there any more: the folder was changed meanwhile.",
        6 => "It's larger than 1 MiB, the most the editor takes.",
        7 => $"{mount.Target} is a single file, not a folder: Gitberth browses folders.",
        -1 => said,
        _ => $"It didn't work: {(said.Trim().Length > 0 ? said.Trim().TrimEnd('.') : $"exit code {exit}")}.",
    };

    private static string Shown(FilesOf of, string path) => path.Length == 0 ? of.Mount.Target : $"{of.Mount.Target.TrimEnd('/')}/{path}";

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KiB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    private void Record(string action, string summary, FilesOf of, string path, IReadOnlyList<Audit.AuditChange> changes) =>
        audit?.Record(new Audit.AuditEntry(action, summary)
        {
            AppId = of.AppId, AppName = of.AppName, Container = of.Container, Target = Shown(of, path), Changes = changes,
        });

    private static string? Str(JsonObject? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- The helpers ----

    private readonly SemaphoreSlim _sweeping = new(1, 1);
    private bool _swept;
    private IReadOnlyList<string>? _imageVolumes;

    /// <summary>
    /// The helper's create body: Gitberth's image, the mount at /mnt, no network, root with a few capabilities, a read-only
    /// root. The image's own VOLUME paths (<paramref name="imageVolumes"/>: Gitberth's /data) get a tmpfs each, so no
    /// helper leaves an anonymous volume behind.
    /// </summary>
    internal static JsonObject HelperBody(string image, FileMount mount, bool write, IReadOnlyList<string>? imageVolumes = null) => new()
    {
        ["Image"] = image,
        ["Entrypoint"] = new JsonArray("/bin/sh", "-c"),
        // It only waits for the one script it's made for, and goes at once after it; a minute at most if Gitberth stops meanwhile.
        ["Cmd"] = new JsonArray("sleep 60"),
        ["User"] = "0",
        ["WorkingDir"] = "/",
        ["NetworkDisabled"] = true,
        ["Labels"] = new JsonObject { [HostScanHelper.Label] = HelperKind },
        ["HostConfig"] = new JsonObject
        {
            // nocopy: Docker never fills an empty volume from the helper's own /mnt, nor gives the volume's root /mnt's owner.
            ["Binds"] = new JsonArray(mount.Volume is { } volume
                ? $"{volume}:/mnt:{(write ? "rw" : "ro")},nocopy"
                : $"{mount.Source}:/mnt:{(write ? "rw" : "ro")}"),
            ["NetworkMode"] = "none",
            ["CapDrop"] = new JsonArray("ALL"),
            ["CapAdd"] = new JsonArray("CHOWN", "DAC_OVERRIDE", "DAC_READ_SEARCH", "FOWNER", "FSETID"),
            ["SecurityOpt"] = new JsonArray("no-new-privileges"),
            ["ReadonlyRootfs"] = true,
            ["Tmpfs"] = new JsonObject((imageVolumes ?? []).Where(v => v.TrimEnd('/') is not ("/mnt" or "/tmp" or ""))
                .Select(v => KeyValuePair.Create(v, (JsonNode?)JsonValue.Create("size=1m")))
                .Append(KeyValuePair.Create("/tmp", (JsonNode?)JsonValue.Create("size=16m")))
                .DistinctBy(kv => kv.Key, StringComparer.Ordinal)),
            ["Memory"] = 256L * 1024 * 1024,
            ["PidsLimit"] = 128,
            ["AutoRemove"] = true,
            ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
        },
    };

    // The script, in a helper made for it with the mount, removed as soon as it's done (nothing keeps holding the app's
    // volume): its exit code and what it said, or -1 and why it couldn't run.
    private async Task<(int Exit, string Said)> RunAsync(string container, FileMount mount, bool write, string script, IReadOnlyList<string> args,
        Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        string name;
        try
        {
            await SweepOnceAsync(ct);
            var image = await self.OwnImageAsync(ct) ?? throw new InvalidOperationException("Gitberth isn't running in a container here, so it can't open an app's files");
            _imageVolumes ??= (await api.InspectImageAsync(image, ct))?["Config"]?["Volumes"] is JsonObject declared ? declared.Select(v => v.Key).ToList() : [];
            name = HostScanHelper.NewName(HelperKind);
            await api.CreateContainerAsync(name, HelperBody(image, mount, write, _imageVolumes), ct);
            try
            {
                await api.StartContainerAsync(name, ct);
            }
            catch
            {
                await RemoveQuietlyAsync(name);
                throw;
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The files helper for {Container}'s {Target} couldn't be started", container, mount.Target);
            return (-1, $"Gitberth couldn't open {mount.Target}: {ex.Message.TrimEnd('.')}.");
        }
        try
        {
            var result = await api.ExecAsync(name, ["/bin/sh", "-c", script, "sh", .. args], null, stdin, stdout, ct);
            return (result.ExitCode, result.StdErr);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            return (-1, $"Docker couldn't read or change {mount.Target}: {ex.Message.TrimEnd('.')}.");
        }
        finally
        {
            await RemoveQuietlyAsync(name);
        }
    }

    // Helpers a Gitberth that stopped left behind: removed before the first new one is made.
    private async Task SweepOnceAsync(CancellationToken ct)
    {
        if (_swept) return;
        await _sweeping.WaitAsync(ct);
        try
        {
            if (_swept) return;
            foreach (var c in await api.ListAllAsync(ct))
                if (c.Labels.TryGetValue(HostScanHelper.Label, out var kind) && kind == HelperKind) await RemoveQuietlyAsync(c.Name);
            _swept = true;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            logger.LogDebug(ex, "Couldn't look for files helpers left behind");
        }
        finally
        {
            _sweeping.Release();
        }
    }

    private async Task RemoveQuietlyAsync(string name)
    {
        try
        {
            await api.RemoveContainerAsync(name, force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Couldn't remove the files helper {Helper}", name);
        }
    }

    public ValueTask DisposeAsync()
    {
        _sweeping.Dispose();
        return ValueTask.CompletedTask;
    }
    // ---- Counting what goes by ----

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count += count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Count += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Count += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            Count += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
