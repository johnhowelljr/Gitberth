using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gitberth.Sources;

/// <summary>
/// An upload received and unpacked, waiting to become an app (New app's Configure step) or a new version of one: its
/// archive (a folder's packed as a tar), the name it came with, its size and SHA-256, and where it unpacked.
/// </summary>
/// <param name="Id">Its staging folder's name: 32 random hex characters.</param>
/// <param name="Name">What was uploaded: site.zip, or a folder's name.</param>
/// <param name="Folder">It came as a folder the browser sent file by file (kept as a tar).</param>
public sealed record StagedUpload(string Id, string Name, UploadKind Kind, long Size, string Sha, int Files, long Unpacked, bool Folder, DateTime At)
{
    /// <summary>The app's name it suggests: site for site.zip.</summary>
    public string AppName => UploadArchives.NameOf(Name);
}

/// <summary>
/// Where uploads live. A new upload is received and unpacked into its own staging folder under Gitberth's work folder
/// (/data/work/uploads/&lt;id&gt;/: upload.&lt;zip|tar|tgz&gt;, src/, upload.json), never held in memory, and forgotten after an
/// hour. An app's uploads are kept as /data/uploads/&lt;app id&gt;/&lt;sha&gt;.&lt;ext&gt;, the newest <see cref="Kept"/> of them
/// (the one it runs and the one Deploy builds always among them), and go with the app. Backups carry /data/uploads.
/// </summary>
public sealed class UploadStore(GitberthOptions options, ILogger<UploadStore> logger)
{
    /// <summary>How many of an app's uploads are kept (Roll back's builds, and Deploy's).</summary>
    public const int Kept = 5;

    /// <summary>How long an upload waits in staging for New app's Deploy (or its new version's) before it's removed.</summary>
    public static readonly TimeSpan StagedFor = TimeSpan.FromHours(1);

    private const string MetaFile = "upload.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>/data/uploads: every app's kept uploads, by its ID.</summary>
    public string Root => Deploy.AppDataFolders.UploadsRoot(options);

    /// <summary>/data/work/uploads: uploads being received, or waiting for their app.</summary>
    public string StagingRoot => Path.Combine(options.WorkDir, "uploads");

    public string AppDir(int appId) => Deploy.AppDataFolders.Uploads(options, appId);

    private string StageDir(string id) => Path.Combine(StagingRoot, id);

    /// <summary>The staged upload's archive.</summary>
    public string ArchiveOf(StagedUpload staged) => Path.Combine(StageDir(staged.Id), "upload." + UploadArchives.Extension(staged.Kind));

    /// <summary>The staged upload's files, unpacked (its root: the one top-level folder it had, else the folder itself).</summary>
    public string SourceOf(StagedUpload staged) => UploadArchives.RootOf(Path.Combine(StageDir(staged.Id), "src"));

    /// <summary>The folder staged uploads unpack into, which analysis and the Railpack planner read (src/ itself).</summary>
    public string UnpackedDir(StagedUpload staged) => Path.Combine(StageDir(staged.Id), "src");

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// An archive from its sender, streamed to disk (its SHA-256 worked out on the way, its size held to
    /// <see cref="UploadLimits.MaxUpload"/>), then unpacked safely (<see cref="UploadArchives.UnpackAsync"/>). A refusal
    /// throws its sentence (<see cref="UploadRefusedException"/>) and leaves nothing behind.
    /// </summary>
    public async Task<StagedUpload> ReceiveArchiveAsync(string fileName, Stream body, CancellationToken ct)
    {
        var name = CleanName(fileName);
        var kind = UploadArchives.KindOf(name) ?? throw new UploadRefusedException(UploadArchives.NotAnArchive);
        var (id, dir) = NewStage();
        try
        {
            var archive = Path.Combine(dir, "upload." + UploadArchives.Extension(kind));
            long size;
            string sha;
            await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                size = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    size += n;
                    if (size > UploadLimits.MaxUpload) throw TooBig();
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (size == 0) throw new UploadRefusedException("The file is empty.");
            var unpacked = await UploadArchives.UnpackAsync(archive, kind, Path.Combine(dir, "src"), ct);
            var staged = new StagedUpload(id, name, kind, size, sha, unpacked.Files, unpacked.Bytes, Folder: false, DateTime.UtcNow);
            await WriteMetaAsync(dir, staged, ct);
            return staged;
        }
        catch
        {
            UploadArchives.TryDelete(dir);
            throw;
        }
    }

    private static UploadRefusedException TooBig() =>
        new($"The upload is larger than {UploadArchives.Size(UploadLimits.MaxUpload)}, the most Gitberth takes, so it isn't kept.");

    /// <summary>A folder the browser sends file by file: <see cref="FolderUpload.AddAsync"/> each, then <see cref="FolderUpload.FinishAsync"/>.</summary>
    public FolderUpload BeginFolder()
    {
        var (id, dir) = NewStage();
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        return new FolderUpload(id, dir);
    }

    /// <summary>
    /// A folder upload as it comes in: each file under its relative path (the folder's name first, as the browser sends it),
    /// what <see cref="UploadArchives.LeftOutOfFolder"/> names left out, the limits held. Then packed as a tar, which is
    /// what's kept. Disposed without finishing, it leaves nothing behind.
    /// </summary>
    public sealed class FolderUpload(string id, string dir) : IAsyncDisposable
    {
        private long _bytes;
        private int _files, _left;
        private bool _done;
        private string? _top;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <summary>How many files it left out (.git, node_modules…).</summary>
        public int LeftOut => _left;

        public async Task AddAsync(string relativePath, Stream data, CancellationToken ct)
        {
            var parts = UploadArchives.PartsOf(relativePath);
            if (parts.Count == 0) throw new UploadRefusedException("A file of the folder came without a name.");
            // Every file of one folder upload is under the same top folder; a lone file is put under one.
            _top ??= parts.Count > 1 ? parts[0] : "upload";
            if (parts.Count == 1) parts = [_top, .. parts];
            else if (parts[0] != _top) throw new UploadRefusedException("The files came from more than one folder: choose one folder.");
            if (UploadArchives.LeftOutOfFolder(parts))
            {
                _left++;
                await data.CopyToAsync(Stream.Null, ct);
                return;
            }
            if (++_files > UploadLimits.MaxEntries)
                throw new UploadRefusedException($"The folder has more than {UploadLimits.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} files, so it isn't kept.");
            var key = string.Join('/', parts);
            if (!_seen.Add(key)) throw new UploadRefusedException($"{key} came twice.");
            var root = Path.GetFullPath(Path.Combine(dir, "src"));
            var path = Path.GetFullPath(Path.Combine([root, .. parts]));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new UploadRefusedException($"{key} leads out of its folder, so the upload isn't kept.");
            if (Directory.Exists(path)) throw new UploadRefusedException($"{key} is both a folder and a file.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var buffer = new byte[128 * 1024];
            int n;
            while ((n = await data.ReadAsync(buffer, ct)) > 0)
            {
                _bytes += n;
                if (_bytes > UploadLimits.MaxUpload) throw TooBig();
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }

        /// <summary>Packs what came (refused when nothing did) and stages it.</summary>
        public async Task<StagedUpload> FinishAsync(CancellationToken ct)
        {
            if (_files == 0)
                throw new UploadRefusedException(_left > 0 ? "Every file of the folder is one Gitberth leaves out (.git, node_modules and the like): there's nothing to deploy." : "No files came with the upload.");
            var src = Path.Combine(dir, "src");
            var top = Path.Combine(src, _top!);
            var tar = Path.Combine(dir, "upload.tar");
            var (sha, size) = await UploadArchives.PackFolderAsync(top, tar, ct);
            var staged = new StagedUpload(id, _top!, UploadKind.Tar, size, sha, _files, _bytes, Folder: true, DateTime.UtcNow);
            await WriteMetaAsync(dir, staged, ct);
            _done = true;
            return staged;
        }

        public ValueTask DisposeAsync()
        {
            if (!_done) UploadArchives.TryDelete(dir);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A staged upload by its ID; null when it's gone (made into an app already, or forgotten after an hour).</summary>
    public StagedUpload? Find(string? id)
    {
        if (!IsId(id)) return null;
        var meta = Path.Combine(StageDir(id!), MetaFile);
        if (!File.Exists(meta)) return null;
        try
        {
            var staged = JsonSerializer.Deserialize<StagedUpload>(File.ReadAllText(meta), Json);
            return staged is not null && staged.Id == id && DateTime.UtcNow - staged.At < StagedFor ? staged : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A staged upload's folder goes (it was made into an app, or replaced).</summary>
    public void Discard(string? id)
    {
        if (IsId(id)) UploadArchives.TryDelete(StageDir(id!));
    }

    /// <summary>Uploads staged more than <see cref="StagedFor"/> ago go: an upload New app never deployed.</summary>
    public void Sweep()
    {
        if (!Directory.Exists(StagingRoot)) return;
        foreach (var dir in new DirectoryInfo(StagingRoot).EnumerateDirectories())
            if (DateTime.UtcNow - dir.CreationTimeUtc > StagedFor) UploadArchives.TryDelete(dir.FullName);
    }

    private (string Id, string Dir) NewStage()
    {
        Sweep();
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var dir = StageDir(id);
        Directory.CreateDirectory(dir);
        return (id, dir);
    }

    private static async Task WriteMetaAsync(string dir, StagedUpload staged, CancellationToken ct) =>
        await File.WriteAllTextAsync(Path.Combine(dir, MetaFile), JsonSerializer.Serialize(staged, Json), ct);

    /// <summary>A name as the page shows it: the file's own name, without a path, at most 200 characters.</summary>
    internal static string CleanName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').TrimEnd('/')).Trim();
        name = new string(name.Where(c => c >= ' ' && c != '\u007f').ToArray());
        return name.Length == 0 ? "upload" : name.Length > 200 ? name[..200] : name;
    }

    // ---- An app's kept uploads ----

    /// <summary>
    /// The staged upload becomes one of the app's: its archive moved to /data/uploads/&lt;id&gt;/&lt;sha&gt;.&lt;ext&gt; (the same
    /// file twice is kept once), the staging folder removed, and the app's oldest beyond <see cref="Kept"/> removed, never
    /// one named in <paramref name="keep"/> (the one it runs). Returns the kept archive's path.
    /// </summary>
    public string Keep(int appId, StagedUpload staged, IEnumerable<string?> keep)
    {
        var dir = AppDir(appId);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, staged.Sha + "." + UploadArchives.Extension(staged.Kind));
        var from = ArchiveOf(staged);
        if (!File.Exists(target)) File.Move(from, target);
        // Its time is when it was kept: the newest kept is the newest upload.
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        Discard(staged.Id);
        Prune(appId, [staged.Sha, .. keep]);
        return target;
    }

    /// <summary>The app's kept archive with this SHA-256, or null when it isn't kept (any more).</summary>
    public string? ArchiveOf(int appId, string? sha)
    {
        if (sha is not { Length: 64 } || !sha.All(char.IsAsciiHexDigitLower)) return null;
        var dir = AppDir(appId);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, sha + ".*").FirstOrDefault(f => UploadArchives.KindOfExtension(Path.GetExtension(f)) is not null);
    }

    /// <summary>The SHA-256s of the app's kept uploads, newest first.</summary>
    public IReadOnlyList<string> KeptOf(int appId)
    {
        var dir = AppDir(appId);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles().Where(f => UploadArchives.KindOfExtension(f.Extension) is not null)
            .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList();
    }

    private void Prune(int appId, IReadOnlyCollection<string?> keep)
    {
        var dir = AppDir(appId);
        var files = new DirectoryInfo(dir).EnumerateFiles().Where(f => UploadArchives.KindOfExtension(f.Extension) is not null)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(Kept))
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(old.Name))) continue;
            try
            {
                old.Delete();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Couldn't remove the old upload {File}", old.FullName);
            }
        }
    }

    /// <summary>An app's uploads go with it.</summary>
    public void RemoveApp(int appId) => UploadArchives.TryDelete(AppDir(appId));
}
