using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Gitberth.Sources;

/// <summary>What an upload came as: an archive, or a folder the browser sent file by file (kept as a tar).</summary>
public enum UploadKind { Zip, Tar, TarGz }

/// <summary>An upload Gitberth won't unpack or keep, and why, in one sentence the page or the API shows as it is.</summary>
public sealed class UploadRefusedException(string message) : Exception(message);

/// <summary>The limits every upload is held to (a must: an upload is anyone's files, unpacked on this host).</summary>
public static class UploadLimits
{
    /// <summary>The most an upload may be: the archive, or a folder's files together.</summary>
    public const long MaxUpload = 2L * 1024 * 1024 * 1024;
    /// <summary>The most an archive may unpack to.</summary>
    public const long MaxUnpacked = 10L * 1024 * 1024 * 1024;
    /// <summary>The most entries (files, folders, links) an upload may have.</summary>
    public const int MaxEntries = 200_000;
    /// <summary>Past this much unpacked, an archive may unpack to at most <see cref="MaxRatio"/> times its own size.</summary>
    public const long RatioFloor = 1L * 1024 * 1024 * 1024;
    public const int MaxRatio = 200;
    /// <summary>How many links deep a link may lead before it counts as a loop.</summary>
    public const int MaxHops = 40;
}

/// <summary>What an archive unpacked to: its root (the one top-level folder it had, else the folder itself), files and bytes.</summary>
public sealed record Unpacked(string Root, int Files, long Bytes);

/// <summary>The limits an unpack holds (<see cref="UploadLimits"/>; smaller in tests).</summary>
public sealed record UnpackLimits(long MaxUnpacked, int MaxEntries, long RatioFloor, int MaxRatio)
{
    public static readonly UnpackLimits Default = new(UploadLimits.MaxUnpacked, UploadLimits.MaxEntries, UploadLimits.RatioFloor, UploadLimits.MaxRatio);
}

/// <summary>
/// Safe unpacking of an uploaded archive (.zip, .tar, .tar.gz/.tgz), and the tar a folder upload is kept as. Nothing of an
/// archive is ever written outside its folder: no absolute paths, no "..", nothing written through a link, no link that
/// leads outside (followed as the system would, link by link), no devices or FIFOs, every entry exactly the size it says,
/// and the limits of <see cref="UploadLimits"/>, a ratio guard against bombs included. Anything else refuses the whole
/// upload with a sentence (<see cref="UploadRefusedException"/>), and what was unpacked of it is removed.
/// </summary>
public static class UploadArchives
{
    /// <summary>The kind a file's name says, or null for one Gitberth doesn't take.</summary>
    public static UploadKind? KindOf(string? fileName)
    {
        var name = (fileName ?? "").Trim().ToLowerInvariant();
        return name.EndsWith(".zip", StringComparison.Ordinal) ? UploadKind.Zip
            : name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal) ? UploadKind.TarGz
            : name.EndsWith(".tar", StringComparison.Ordinal) ? UploadKind.Tar
            : null;
    }

    /// <summary>The extension a kept archive of the kind gets.</summary>
    public static string Extension(UploadKind kind) => kind switch { UploadKind.Zip => "zip", UploadKind.Tar => "tar", _ => "tgz" };

    /// <summary>The kind of a kept archive, by its extension.</summary>
    public static UploadKind? KindOfExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "zip" => UploadKind.Zip,
        "tar" => UploadKind.Tar,
        "tgz" => UploadKind.TarGz,
        _ => null,
    };

    /// <summary>The sentence for a file of a kind Gitberth doesn't take.</summary>
    public const string NotAnArchive = "Upload a .zip, .tar, .tar.gz or .tgz file, or choose a folder.";

    /// <summary>A name for the app, from what was uploaded: site.zip is site, my-app.tar.gz is my-app.</summary>
    public static string NameOf(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/').TrimEnd('/'));
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }

    /// <summary>The short form of an upload's SHA-256, as its deploys show it (its "commit").</summary>
    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    // ---- Paths ----

    /// <summary>
    /// An entry's name as its parts, inside the upload; empty for the root itself. Refused (with why): an absolute path, a
    /// drive letter, a ".." part, a control character. Backslashes count as separators, as Windows' zips have them.
    /// </summary>
    public static IReadOnlyList<string> PartsOf(string name)
    {
        if (name.Any(c => c < ' ' || c == '\u007f')) throw new UploadRefusedException($"An entry's name has a control character in it ({Shown(name)}), so the upload isn't unpacked.");
        var path = name.Replace('\\', '/');
        if (path.StartsWith('/') || path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            throw new UploadRefusedException($"{Shown(name)} is an absolute path, so the upload isn't unpacked: every entry has to stay inside its folder.");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Contains(".."))
            throw new UploadRefusedException($"{Shown(name)} leads out of its folder (..), so the upload isn't unpacked: every entry has to stay inside it.");
        return parts;
    }

    private static string Shown(string name) => name.Length > 120 ? name[..120] + "…" : name;

    /// <summary>
    /// What a folder upload leaves out, as New app says: Git's own folder, installed packages and caches that are made again
    /// by the build (.git, node_modules, .venv, venv, __pycache__, .next/cache), and macOS's .DS_Store.
    /// </summary>
    public static bool LeftOutOfFolder(IReadOnlyList<string> parts)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p is ".git" or "node_modules" or ".venv" or "venv" or "__pycache__") return true;
            if (p == ".next" && i + 1 < parts.Count && parts[i + 1] == "cache") return true;
        }
        return parts.Count > 0 && parts[^1] == ".DS_Store";
    }

    /// <summary>macOS's leftovers in an archive (__MACOSX/, .DS_Store): never unpacked.</summary>
    public static bool Junk(IReadOnlyList<string> parts) => parts.Count > 0 && (parts[0] == "__MACOSX" || parts[^1] == ".DS_Store");

    /// <summary>The upload's root: the one top-level folder it has (its leftovers aside), else the folder itself.</summary>
    public static string RootOf(string dir)
    {
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().Where(e => e.Name is not ("__MACOSX" or ".DS_Store")).ToList();
        return entries is [DirectoryInfo only] && only.LinkTarget is null ? only.FullName : dir;
    }

    // ---- Unpacking ----

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="target"/> (made empty first), safely: see the class. Returns
    /// its root (<see cref="RootOf"/>), files and bytes. On a refusal the folder is removed and the sentence thrown.
    /// </summary>
    public static async Task<Unpacked> UnpackAsync(string archive, UploadKind kind, string target, CancellationToken ct, UnpackLimits? limits = null)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var size = new FileInfo(archive).Length;
        var state = new UnpackState(Path.GetFullPath(target), size, limits ?? UnpackLimits.Default);
        try
        {
            if (kind == UploadKind.Zip) await UnzipAsync(archive, state, ct);
            else await UntarAsync(archive, kind == UploadKind.TarGz, state, ct);
            CheckLinks(state);
        }
        catch (Exception ex) when (ex is not UploadRefusedException and not OperationCanceledException)
        {
            TryDelete(target);
            throw new UploadRefusedException(ex switch
            {
                InvalidDataException or EndOfStreamException or FormatException => $"The archive is damaged or isn't a {Words(kind)} ({ex.Message.TrimEnd('.')}), so it isn't unpacked.",
                IOException io => $"The upload couldn't be unpacked here: {io.Message.TrimEnd('.')}.",
                UnauthorizedAccessException => "The upload couldn't be unpacked here: Gitberth wasn't allowed to write one of its files.",
                _ => $"The upload couldn't be unpacked: {ex.Message.TrimEnd('.')}.",
            });
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        return new Unpacked(RootOf(target), state.Files, state.Bytes);
    }

    private static string Words(UploadKind kind) => kind switch { UploadKind.Zip => "zip file", UploadKind.Tar => "tar file", _ => "gzipped tar file" };

    /// <summary>What an unpack has written so far, and the links it made (checked once everything is there).</summary>
    private sealed class UnpackState(string root, long archiveBytes, UnpackLimits limits)
    {
        public string Root { get; } = root;
        public long ArchiveBytes { get; } = archiveBytes;
        public int Entries { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        public List<string> Links { get; } = [];

        public void Count()
        {
            if (++Entries > limits.MaxEntries)
                throw new UploadRefusedException($"The archive has more than {limits.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} entries, so it isn't unpacked.");
        }

        public void Grow(long bytes)
        {
            Bytes += bytes;
            if (Bytes > limits.MaxUnpacked)
                throw new UploadRefusedException($"The archive unpacks to more than {Size(limits.MaxUnpacked)}, so it isn't unpacked.");
            if (Bytes > limits.RatioFloor && Bytes > ArchiveBytes * limits.MaxRatio)
                throw new UploadRefusedException($"The archive unpacks to more than {limits.MaxRatio.ToString(CultureInfo.InvariantCulture)} times its own size, as a zip bomb does, so it isn't unpacked.");
        }
    }

    private static async Task UnzipAsync(string archive, UnpackState state, CancellationToken ct)
    {
        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            state.Count();
            var parts = PartsOf(entry.FullName);
            if (parts.Count == 0 || Junk(parts)) continue;
            // A zip made on Unix keeps the entry's mode in the high half of its external attributes (Info-ZIP's way).
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var type = mode & 0xF000;
            var isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || type == 0x4000;
            if (type is 0x2000 or 0x6000 or 0x1000 or 0xC000)
                throw new UploadRefusedException($"{Shown(entry.FullName)} is a device, a FIFO or a socket, which an upload can't have, so it isn't unpacked.");
            var path = Place(state, parts);
            if (isDir)
            {
                MakeDirectory(state, path, parts, mode & 0x1FF);
                continue;
            }
            if (type == 0xA000)
            {
                // A link: its target is the entry's contents.
                if (entry.Length > 4096) throw new UploadRefusedException($"{Shown(entry.FullName)} is a link with a target too long to be one, so the upload isn't unpacked.");
                await using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                MakeLink(state, path, parts, await reader.ReadToEndAsync(ct));
                continue;
            }
            state.Grow(entry.Length);
            await using (var data = entry.Open())
                await WriteFileAsync(state, path, parts, data, entry.Length, mode == 0 ? 0x1A4 : mode & 0x1FF, entry.LastWriteTime.UtcDateTime, ct);
        }
    }

    private static async Task UntarAsync(string archive, bool gzipped, UnpackState state, CancellationToken ct)
    {
        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        await using Stream source = gzipped ? new GZipStream(file, CompressionMode.Decompress) : file;
        await using var tar = new TarReader(source, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            state.Count();
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;
            var parts = PartsOf(entry.Name);
            if (parts.Count == 0 || Junk(parts)) continue;
            var path = Place(state, parts);
            var mode = (int)entry.Mode & 0x1FF;
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    MakeDirectory(state, path, parts, mode);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    state.Grow(entry.Length);
                    await WriteFileAsync(state, path, parts, entry.DataStream ?? Stream.Null, entry.Length, mode, entry.ModificationTime.UtcDateTime, ct);
                    break;
                case TarEntryType.SymbolicLink:
                    MakeLink(state, path, parts, entry.LinkName);
                    break;
                case TarEntryType.HardLink:
                {
                    // A copy of an earlier file of the archive's, never a link to anything else.
                    var to = Place(state, PartsOf(entry.LinkName));
                    if (!File.Exists(to) || new FileInfo(to).LinkTarget is not null)
                        throw new UploadRefusedException($"{Shown(entry.Name)} is a hard link to {Shown(entry.LinkName)}, which isn't a file earlier in the archive, so it isn't unpacked.");
                    var length = new FileInfo(to).Length;
                    state.Grow(length);
                    await using var copy = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                    await WriteFileAsync(state, path, parts, copy, length, mode, entry.ModificationTime.UtcDateTime, ct);
                    break;
                }
                default:
                    throw new UploadRefusedException($"{Shown(entry.Name)} is a {Kind(entry.EntryType)}, which an upload can't have, so it isn't unpacked.");
            }
        }
    }

    private static string Kind(TarEntryType type) => type switch
    {
        TarEntryType.CharacterDevice or TarEntryType.BlockDevice => "device",
        TarEntryType.Fifo => "FIFO",
        _ => $"{type} entry",
    };

    /// <summary>Where an entry goes: under the root, and never through a link (each folder on the way is checked on disk).</summary>
    private static string Place(UnpackState state, IReadOnlyList<string> parts)
    {
        var path = state.Root;
        for (var i = 0; i < parts.Count; i++)
        {
            path = Path.Combine(path, parts[i]);
            if (i == parts.Count - 1) break;
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
                throw new UploadRefusedException($"{string.Join('/', parts)} would be written through the link {string.Join('/', parts.Take(i + 1))}, so the upload isn't unpacked.");
            if (File.Exists(path))
                throw new UploadRefusedException($"{string.Join('/', parts)} goes inside {string.Join('/', parts.Take(i + 1))}, which is a file, so the upload isn't unpacked.");
        }
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(state.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UploadRefusedException($"{string.Join('/', parts)} leads out of its folder, so the upload isn't unpacked.");
        return full;
    }

    private static void MakeDirectory(UnpackState state, string path, IReadOnlyList<string> parts, int mode)
    {
        if (new FileInfo(path).LinkTarget is not null || File.Exists(path))
            throw new UploadRefusedException($"{string.Join('/', parts)} is both a folder and a file or link in the archive, so it isn't unpacked.");
        Directory.CreateDirectory(path);
        SetMode(path, CheckoutFolderMode);
    }

    /// <summary>
    /// The modes an upload's files get, as a repository's checkout has them (git keeps no more than that): 0755 for a file
    /// that was executable for anyone, 0644 for any other, 0755 for a folder. So an archive made with 0600 files (as some
    /// tools make them) is still readable by the app's own user (nginx's, say), and nothing is ever setuid or setgid.
    /// </summary>
    internal static int CheckoutMode(int mode) => (mode & 0x49) != 0 ? 0x1ED : 0x1A4;

    internal const int CheckoutFolderMode = 0x1ED;

    private static async Task WriteFileAsync(UnpackState state, string path, IReadOnlyList<string> parts, Stream data, long length, int mode, DateTime modified,
        CancellationToken ct)
    {
        if (Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new UploadRefusedException($"{string.Join('/', parts)} is in the archive twice, as a file and as a folder or link, so it isn't unpacked.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            // Exactly the size it says: more is a lie (a bomb's), less a damaged archive.
            var buffer = new byte[128 * 1024];
            long written = 0;
            int n;
            while ((n = await data.ReadAsync(buffer, ct)) > 0)
            {
                written += n;
                if (written > length)
                    throw new UploadRefusedException($"{string.Join('/', parts)} holds more than the {Size(length)} the archive says it does, so the upload isn't unpacked.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            if (written != length)
                throw new UploadRefusedException($"{string.Join('/', parts)} is shorter than the archive says, so the archive is damaged and isn't unpacked.");
        }
        SetMode(path, CheckoutMode(mode));
        if (modified.Year > 1980) File.SetLastWriteTimeUtc(path, modified);
        state.Files++;
    }

    private static void MakeLink(UnpackState state, string path, IReadOnlyList<string> parts, string target)
    {
        if (string.IsNullOrEmpty(target) || target.Any(c => c < ' '))
            throw new UploadRefusedException($"{string.Join('/', parts)} is a link without a usable target, so the upload isn't unpacked.");
        var normalized = target.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Length >= 2 && normalized[1] == ':')
            throw new UploadRefusedException($"{string.Join('/', parts)} is a link to {Shown(target)}, outside its folder, so the upload isn't unpacked.");
        if (Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new UploadRefusedException($"{string.Join('/', parts)} is in the archive twice, so it isn't unpacked.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, normalized);
        state.Links.Add(path);
    }

    /// <summary>
    /// Once everything is there: every link is followed as the system would, link by link from where it is, and must end
    /// inside the upload (a link to a missing file inside it is fine). One that leaves it refuses the whole upload.
    /// </summary>
    private static void CheckLinks(UnpackState state)
    {
        foreach (var link in state.Links)
        {
            var at = Path.GetRelativePath(state.Root, link).Replace('\\', '/').Split('/').ToList();
            var target = new FileInfo(link).LinkTarget ?? "";
            if (Resolve(state.Root, at.Take(at.Count - 1).ToList(), target) is null)
                throw new UploadRefusedException($"{string.Join('/', at)} is a link that leads outside its folder (to {Shown(target)}), so the upload isn't unpacked.");
        }
    }

    /// <summary>
    /// Where <paramref name="target"/> leads from the folder <paramref name="from"/> (parts under <paramref name="root"/>), each
    /// link on the way followed from where it is, as the system does; null when it leaves <paramref name="root"/>, loops, or
    /// a link on the way is absolute. The parts it ends at otherwise.
    /// </summary>
    internal static List<string>? Resolve(string root, IReadOnlyList<string> from, string target)
    {
        var first = target.Replace('\\', '/');
        if (first.StartsWith('/') || first.Length >= 2 && first[1] == ':') return null;
        var at = from.ToList();
        var pending = new Queue<string>(first.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var hops = 0;
        while (pending.Count > 0)
        {
            var part = pending.Dequeue();
            if (part == ".") continue;
            if (part == "..")
            {
                if (at.Count == 0) return null;
                at.RemoveAt(at.Count - 1);
                continue;
            }
            at.Add(part);
            var here = Path.Combine([root, .. at]);
            if (new FileInfo(here).LinkTarget is not { } next) continue;
            if (++hops > UploadLimits.MaxHops) return null;
            var normalized = next.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Length >= 2 && normalized[1] == ':') return null;
            // The link's own target, from the folder the link is in, then what was left of the path.
            at.RemoveAt(at.Count - 1);
            var rest = pending.ToList();
            pending.Clear();
            foreach (var p in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)) pending.Enqueue(p);
            foreach (var p in rest) pending.Enqueue(p);
        }
        return at;
    }

    private static void SetMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF));
    }

    internal static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB"
        : bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB"
        : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";

    internal static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- A folder upload, kept as a tar ----

    /// <summary>
    /// A folder as a tar, its entries in name order under the folder's own name, every file's time the same: the same files
    /// make the same archive, so the same SHA-256. Links aren't followed (a folder upload has none: the browser sends files).
    /// Returns the tar's SHA-256 (hex) and size.
    /// </summary>
    public static async Task<(string Sha, long Size)> PackFolderAsync(string folder, string tarPath, CancellationToken ct)
    {
        var top = new DirectoryInfo(folder);
        await using (var file = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        await using (var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true))
        {
            var fixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            async Task AddAsync(DirectoryInfo dir, string name)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = (UnixFileMode)0x1ED, ModificationTime = fixedTime }, ct);
                foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    if (entry.LinkTarget is not null) continue;
                    var child = name + "/" + entry.Name;
                    if (entry is DirectoryInfo sub) await AddAsync(sub, child);
                    else if (entry is FileInfo f)
                    {
                        await using var data = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, child) { Mode = (UnixFileMode)0x1A4, ModificationTime = fixedTime, DataStream = data }, ct);
                    }
                }
            }
            await AddAsync(top, top.Name);
        }
        return (await ShaAsync(tarPath, ct), new FileInfo(tarPath).Length);
    }

    /// <summary>A file's SHA-256, as lowercase hex.</summary>
    public static async Task<string> ShaAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
}
