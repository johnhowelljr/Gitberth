using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>
/// Saving (IBackupService): the save list with sizes, and a save made while the browser downloads it, straight into the
/// response, nothing on disk but SQLite snapshots for a moment. Order: the manifest, Gitberth's data, the database dumps,
/// the volumes (each one's users paused for exactly its copy), the images only this host has. Every part is compressed
/// and the whole file encrypted (<see cref="BackupWriter"/>). "Stop everything" writes the running containers down first,
/// stops them, saves, and starts exactly those again.
/// </summary>
/// <param name="audit">Where each save's end is recorded (backup.saved, backup.save.failed); none in tests.</param>
public sealed class BackupService(IBackupEngine engine, IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, ILogger<BackupService> logger,
    Audit.AuditLog? audit = null)
    : IBackupService
{
    /// <summary>How long a save's ticket waits for its download, and its progress is kept after it.</summary>
    public static readonly TimeSpan TicketLife = TimeSpan.FromMinutes(10);

    // How long volumes' sizes are kept before Docker is asked again (it walks every volume to size them).
    private static readonly TimeSpan SizesFor = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, SaveJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly BackupPaths _paths = new(options);
    private readonly object _sizesGate = new();
    private IReadOnlyDictionary<string, long> _sizes = new Dictionary<string, long>();
    private IReadOnlyDictionary<string, HostPathInfo> _folders = new Dictionary<string, HostPathInfo>();
    private DateTime _sizesAt = DateTime.MinValue;
    private Task? _measuring;

    /// <summary>The automatic backup being made now, for its card's progress line; null between them.</summary>
    internal SaveJob? AutoJob { get; private set; }

    /// <summary>PBKDF2's iterations and the chunk size of new files (fewer and smaller in tests).</summary>
    internal int Iterations { get; init; } = BackupFormat.DefaultIterations;

    internal int ChunkSize { get; init; } = BackupFormat.ChunkBytes;

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public async Task<IReadOnlyList<BackupItem>> ItemsAsync(CancellationToken ct = default)
    {
        // Sizes come from Docker, which walks every volume: the first list waits a moment for them, later ones use what's
        // known (asked again every two minutes at most). The rest is read each time: an app made a moment ago is listed.
        var measuring = MeasureSizes();
        if (measuring is not null) await Task.WhenAny(measuring, Task.Delay(TimeSpan.FromSeconds(2), ct));
        var inventory = await BackupInventory.ReadAsync(engine, dbFactory, options, _sizes, DataBytes(), ct, RegistryDigestAsync, _folders);
        return inventory.Items;
    }

    /// <summary>
    /// An automatic backup: the items <paramref name="choice"/> says (as the save list would tick them), written to
    /// <paramref name="output"/> with <paramref name="key"/> (format 2) as they're made. It waits up to
    /// <paramref name="wait"/> for a save being made to end; one at a time, never two. Returns how it ended (Problem set
    /// when it failed); the key is disposed when it's done.
    /// </summary>
    /// <param name="choose">The items to save, from the list as it is now (a new app is in it).</param>
    internal async Task<SaveProgress> SaveToAsync(Func<IReadOnlyList<BackupItem>, BackupChoice> choose, BackupKey key, string fileName, Stream output,
        TimeSpan wait, CancellationToken ct)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var job = new SaveJob(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), new BackupChoice(new HashSet<string>(), false), key, fileName,
            now + TicketLife) { Auto = true, Choose = choose, Wait = wait };
        job.TryClaim();
        AutoJob = job;
        try
        {
            await WriteAsync(job, output, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // How it ended is in its progress.
        }
        finally
        {
            AutoJob = null;
        }
        return job.Snapshot();
    }

    // How long what registries said about a reference is kept.
    private static readonly TimeSpan RegistryFor = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (string? Digest, DateTime At)> _registry = new(StringComparer.Ordinal);

    // Whether a registry has the reference (its digest), asked at most every half hour and for 15 s at most: a registry
    // that doesn't answer leaves the image saved whole, which always restores.
    private async Task<string?> RegistryDigestAsync(string reference, CancellationToken ct)
    {
        if (_registry.TryGetValue(reference, out var known) && DateTime.UtcNow - known.At < RegistryFor) return known.Digest;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TimeSpan.FromSeconds(15));
        string? digest;
        try
        {
            digest = await engine.RegistryDigestAsync(reference, timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            digest = null;
        }
        _registry[reference] = (digest, DateTime.UtcNow);
        return digest;
    }

    public async Task<SaveTicket> StartSaveAsync(BackupChoice choice, string passphrase, CancellationToken ct = default)
    {
        if (choice?.Included is null) throw new ArgumentException("Choose what to save.", nameof(choice));
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < BackupFormat.MinPassphrase)
            throw new ArgumentException($"The passphrase needs {BackupFormat.MinPassphrase} characters at least.", nameof(passphrase));
        Prune();
        // The key is made now (PBKDF2 takes a moment on purpose) and only it is kept, until the download ends or the ticket expires.
        var key = await Task.Run(() => BackupKey.ForNewFile(passphrase, Iterations, ChunkSize), ct);
        var now = Time.GetUtcNow().UtcDateTime;
        string host;
        try
        {
            host = BackupInventory.Str(await engine.InfoAsync(ct), "Name");
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            host = "";
        }
        var file = $"gitberth-backup-{FileWord(host)}{now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}{BackupFormat.Extension}";
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var job = new SaveJob(id, choice, key, file, now + TicketLife);
        _jobs[id] = job;
        return new SaveTicket(id, file, job.ExpiresAt);
    }

    public SaveProgress? Progress(string ticketId)
    {
        Prune();
        return _jobs.TryGetValue(ticketId, out var job) ? job.Snapshot() : null;
    }

    /// <summary>The ticket, once: a second download of it, or one after it expired, gets nothing.</summary>
    internal SaveJob? Claim(string ticketId)
    {
        Prune();
        return _jobs.TryGetValue(ticketId, out var job) && job.Key is not null && job.TryClaim() ? job : null;
    }

    /// <summary>Whether a save is being written now.</summary>
    internal bool Saving => _saving.CurrentCount == 0;

    /// <summary>
    /// The save itself, written to <paramref name="output"/> as it's made. A failure ends it (the file is then unusable,
    /// and refused when read) and says why in the job's progress. What it stopped or paused is started or unpaused again
    /// whatever happens.
    /// </summary>
    internal async Task WriteAsync(SaveJob job, Stream output, CancellationToken ct)
    {
        if (!await _saving.WaitAsync(job.Wait, ct))
        {
            // Lost to another save: this one's key goes now, and its progress says why.
            const string busy = "Another save is being made. Wait for it to end, then save again.";
            job.Key?.Dispose();
            job.Key = null;
            job.Finish(busy, Time.GetUtcNow().UtcDateTime + TicketLife);
            throw new InvalidOperationException(busy);
        }
        var stopped = new List<string>();
        var stoppedListed = false;
        var stopAll = job.Choice.StopEverything;
        try
        {
            var key = job.Key ?? throw new InvalidOperationException("This save's ticket has expired. Save again.");
            job.Report("Reading what's on this host", null);
            // An automatic backup has nobody to wait for sizes: the host folders are measured now (a big one stays out
            // unless the owner ticked it).
            if (job.Auto && MeasureSizes() is { } measuring) await measuring.WaitAsync(HostFolders.MeasureFor + TimeSpan.FromSeconds(30), ct);
            var inventory = await BackupInventory.ReadAsync(engine, dbFactory, options, _sizes, DataBytes(), ct, RegistryDigestAsync, _folders);
            if (job.Choose is { } choose)
            {
                job.Choice = choose(inventory.Items);
                stopAll = job.Choice.StopEverything;
            }
            var chosen = inventory.Items.Where(i => i.Required || job.Choice.Included.Contains(i.Id)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

            var containers = inventory.Containers.Values.Where(c => chosen.Contains(c.Item)).ToList();
            var saved = containers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var dumps = inventory.Databases.Values
                .Where(d => chosen.Contains("database:" + d.Container) && saved.Contains(d.Container) && d.CanDump && !stopAll).ToList();
            var dumped = dumps.SelectMany(d => d.DataVolumes).ToHashSet(StringComparer.Ordinal);
            var volumes = inventory.Volumes.Values
                .Where(v => chosen.Contains(v.Item) && !dumped.Contains(v.Volume.Name))
                .Select(v => v.Volume).OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
            var images = inventory.Images.Values.Where(i => i.Item is { } item && chosen.Contains(item)).Select(i => i.Image).ToList();
            var folders = inventory.Folders.Values.Where(f => chosen.Contains(f.Item)).Select(f => f.Folder).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            foreach (var folder in folders) folder.Part = HostFolders.PartName(folder.Path);

            // ---- The manifest: everything the restore needs to know, first.
            var manifest = await ManifestAsync(inventory, chosen, containers, dumps, volumes, images, stopAll, ct);
            manifest.Format = key.Header.Version;
            manifest.HostFolders = folders;
            var dataParts = DataParts();
            manifest.DataParts = dataParts.Select(p => p.Part).ToList();
            manifest.Plan = RestorePlanner.Steps(manifest);
            job.Total = 1 + dataParts.Count + dumps.Count + volumes.Count + folders.Count + images.Count;

            // ---- Stop everything: the list first, so a restart in the middle still starts them again.
            if (stopAll)
            {
                job.Report("Stopping every container", null);
                var running = inventory.Inspects.Where(c => BackupInventory.Running(c.Value) && c.Key != inventory.SelfName
                        && !BackupInventory.Labels(c.Value).ContainsKey(HostScanHelper.Label))
                    .Select(c => c.Key).ToList();
                await WriteListAsync(_paths.StoppedList, running, ct);
                stoppedListed = true;
                foreach (var name in running)
                {
                    // On the list before Docker is asked, and asked to the end: a stop cut short is started again too.
                    stopped.Add(name);
                    await engine.StopContainerAsync(name, StopSeconds(inventory.Inspects[name]), CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
            }

            using var writer = new BackupWriter(output, key);
            job.Writer = writer;
            job.Report("Saving", "the manifest");
            await writer.WritePartAsync(BackupPartKind.Manifest, "manifest.json", 0, new MemoryStream(manifest.ToBytes()), ct);
            job.Done++;

            // ---- Gitberth's data: SQLite snapshots (consistent while it runs), its keys, Compose releases and deploy logs.
            job.Report("Gitberth's data", null);
            foreach (var (part, path, folder) in dataParts)
            {
                job.Report("Gitberth's data", part);
                if (folder) await writer.WritePartAsync(BackupPartKind.DataFolder, part, 0, (sink, token) => TarFolderAsync(path, sink, token), ct);
                else await WriteSnapshotAsync(writer, part, path, ct);
                job.Done++;
            }

            // ---- Database servers, dumped with their own tools while they keep serving.
            foreach (var dump in dumps)
            {
                job.Report("Databases", "database " + dump.Container);
                await writer.WritePartAsync(BackupPartKind.Dump, "dump/" + dump.Container, 0, async (sink, token) =>
                {
                    var r = await engine.ExecAsync(dump.Container, BackupInventory.DumpCommand(dump), BackupInventory.DumpEnv(dump), null, sink, token);
                    if (!r.Ok) throw new BackupSaveException($"The dump of {dump.Container} failed (exit code {r.ExitCode}): {FirstLine(r.StdErr) ?? "it said nothing"}");
                }, ct);
                job.Done++;
            }

            // ---- Volumes, through a helper of Gitberth's own image; what uses each is paused for exactly its copy.
            if (volumes.Count > 0 || folders.Count > 0)
            {
                var image = inventory.SelfName is { } self ? BackupInventory.Str(inventory.Inspects[self], "Image") : null;
                if (string.IsNullOrEmpty(image))
                    throw new BackupSaveException("Volumes are copied by a helper of Gitberth's own image, and Gitberth isn't running in a container here.");
                foreach (var volume in volumes)
                {
                    job.Report("Volumes", "volume " + volume.Name);
                    await CopyVolumeAsync(writer, volume.Part!, volume.Name + ":/v:ro", "/v", c => UsesVolume(c, volume.Name), image, inventory, ct);
                    job.Done++;
                }

                // ---- Host folders, the same way: mounted read-only in the helper, their users paused for the copy.
                foreach (var folder in folders)
                {
                    job.Report("Host folders", "folder " + folder.Path);
                    await CopyVolumeAsync(writer, folder.Part!, folder.Path + ":/h:ro", "/h", c => UsesFolder(c, folder.Path), image, inventory, ct,
                        BackupPartKind.HostFolder);
                    job.Done++;
                }
            }

            // ---- Images only this host has, as docker save writes them.
            foreach (var image in images)
            {
                job.Report("Images", "image " + (image.Tags.FirstOrDefault() ?? image.Id));
                await using (var tar = await engine.SaveImagesAsync(image.Tags.Count > 0 ? image.Tags : [image.Id], ct))
                    await writer.WritePartAsync(BackupPartKind.Image, image.Part!, image.Bytes ?? 0, tar, ct);
                job.Done++;
            }

            await writer.FinishAsync(ct);
            job.Finish(null, Time.GetUtcNow().UtcDateTime + TicketLife);
            logger.LogInformation("Saved a backup: {Parts} parts, {Bytes} bytes", job.Total, writer.BytesWritten);
            if (!job.Auto) Audited(job, stopAll, null);
        }
        catch (Exception ex)
        {
            var why = ex switch
            {
                OperationCanceledException when job.Auto => "Gitberth stopped in the middle of it.",
                OperationCanceledException => "The download was stopped, so the save ended.",
                BackupSaveException or InvalidOperationException => ex.Message,
                _ => "The save failed: " + ex.Message,
            };
            job.Finish(why, Time.GetUtcNow().UtcDateTime + TicketLife);
            if (ex is not OperationCanceledException) logger.LogWarning(ex, "A save failed");
            if (!job.Auto) Audited(job, stopAll, why);
            throw;
        }
        finally
        {
            job.Key?.Dispose();
            job.Key = null;
            // The list goes once every container on it runs again; one that won't start stays on it for the next start.
            if (stoppedListed && await BringBackAsync(stopped, start: true)) TryDelete(_paths.StoppedList);
            _saving.Release();
        }
    }

    // The save's end in the audit log: what was saved, whether everything was stopped for it, and its size.
    private void Audited(SaveJob job, bool stopAll, string? problem)
    {
        var progress = job.Snapshot();
        audit?.Record(new Audit.AuditEntry(problem is null ? "backup.saved" : "backup.save.failed",
            problem is null ? $"Saved a backup: {job.FileName}." : $"A save didn't end: {Audit.AuditLog.Clean(problem, 300)?.TrimEnd('.')}.")
        {
            Outcome = problem is null ? Audit.AuditOutcome.Succeeded : Audit.AuditOutcome.Failed,
            Target = job.FileName,
            Changes =
            [
                new("Items", null, job.Choice.Included.Count.ToString(CultureInfo.InvariantCulture)),
                new("Stop everything", null, stopAll ? "yes" : "no"),
                new("Bytes", null, progress.BytesWritten.ToString(CultureInfo.InvariantCulture)),
            ],
        });
    }

    private async Task<BackupManifest> ManifestAsync(BackupInventory inventory, HashSet<string> chosen, List<SavedContainer> containers,
        List<DatabasePlan> dumps, List<SavedVolume> volumes, List<SavedImage> images, bool stopAll, CancellationToken ct)
    {
        var info = await engine.InfoAsync(ct);
        string? migration;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            migration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        var dumpedBy = dumps.ToDictionary(d => d.Container, StringComparer.Ordinal);
        foreach (var image in images) image.Part = "image/" + image.Id;
        foreach (var volume in volumes) volume.Part = "volume/" + volume.Name;
        var saved = containers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var networks = containers.SelectMany(c => (c.Inspect["NetworkSettings"]?["Networks"] as JsonObject ?? []).Select(n => n.Key)).ToHashSet(StringComparer.Ordinal);
        var created = containers.Where(c => c.How != ComesBack.Deploy).Select(c => BackupInventory.Str(c.Inspect, "Image")).ToHashSet(StringComparer.Ordinal);
        return new BackupManifest
        {
            GitberthVersion = AppVersion.Number,
            LastMigration = migration,
            SavedAt = Time.GetUtcNow().UtcDateTime,
            Host = BackupInventory.Str(info, "Name"),
            Architecture = BackupInventory.Str(info, "Architecture"),
            DockerVersion = await engine.EngineVersionAsync(ct),
            DockerApi = (await engine.ApiVersionAsync(ct)).ToString(2),
            StoppedEverything = stopAll,
            Items = inventory.Items.Where(i => chosen.Contains(i.Id)).ToList(),
            Apps = inventory.Apps.Where(a => chosen.Contains("app:" + a.Id.ToString(CultureInfo.InvariantCulture))).ToList(),
            Containers = containers.Select(c => new SavedContainer
            {
                Name = c.Name, Inspect = c.Inspect, Image = c.Image, Item = c.Item,
                How = dumpedBy.ContainsKey(c.Name) ? ComesBack.Database : c.How,
                // Running when saved, or before "stop everything" stopped it.
                Running = c.Running,
            }).ToList(),
            Volumes = inventory.Volumes.Values.Select(v => v.Volume)
                .Where(v => volumes.Contains(v) || dumps.Any(d => d.DataVolumes.Contains(v.Name)) || v.Users.Any(saved.Contains))
                .OrderBy(v => v.Name, StringComparer.Ordinal).ToList(),
            Databases = dumps.Select(d => new SavedDatabase(d.Container, d.Engine, d.User, string.Join(' ', BackupInventory.DumpCommand(d)), "dump/" + d.Container, d.DataVolumes)).ToList(),
            Images = inventory.Images.Values.Select(i => i.Image).Where(i => created.Contains(i.Id)).ToList(),
            Networks = inventory.Networks.Where(n => networks.Contains(n.Name)).ToList(),
        };
    }

    // Gitberth's own data, by part: its databases (snapshots), then its keys, Compose releases and deploy logs (tars).
    private List<(string Part, string Path, bool Folder)> DataParts()
    {
        var parts = new List<(string, string, bool)>();
        foreach (var file in BackupPaths.DataFiles)
            if (File.Exists(Path.Combine(options.DataDir, file))) parts.Add(("data/" + file, Path.Combine(options.DataDir, file), false));
        foreach (var folder in BackupPaths.DataFolders)
            if (Directory.Exists(Path.Combine(options.DataDir, folder))) parts.Add(("data/" + folder, Path.Combine(options.DataDir, folder), true));
        return parts;
    }

    // SQLite's online backup into a file of its own (consistent while Gitberth writes), streamed, then removed.
    private async Task WriteSnapshotAsync(BackupWriter writer, string part, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.BackupDir);
        var snapshot = Path.Combine(_paths.BackupDir, "snapshot-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".db");
        try
        {
            await Task.Run(() => Snapshot(path, snapshot), ct);
            await using var file = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            await writer.WritePartAsync(BackupPartKind.DataFile, part, file.Length, file, ct);
        }
        finally
        {
            TryDelete(snapshot);
        }
    }

    /// <summary>A consistent copy of a SQLite database, by SQLite's backup API.</summary>
    internal static void Snapshot(string source, string target)
    {
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
    }

    // A folder as a tar, its entries named under the data folder (keys/…, compose/…), modes kept. Each file is read for the
    // length it had when its entry began (a deploy log still being written can't spoil the tar). Compose's empty working
    // folder (.run) isn't data.
    private async Task TarFolderAsync(string folder, Stream sink, CancellationToken ct)
    {
        await using var tar = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true);
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var name = Path.GetRelativePath(options.DataDir, dir.FullName).Replace('\\', '/');
            if (name == "compose/.run") continue;
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = ModeOf(dir, "755"), ModificationTime = dir.LastWriteTimeUtc }, ct);
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                var entryName = Path.GetRelativePath(options.DataDir, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is { } target)
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, entryName) { LinkName = target, Mode = ModeOf(entry, "777") }, ct);
                else if (entry is DirectoryInfo sub)
                    stack.Push(sub);
                else if (entry is FileInfo file)
                {
                    await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, useAsync: true);
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entryName)
                    {
                        Mode = ModeOf(file, "644"), ModificationTime = file.LastWriteTimeUtc, DataStream = new FixedLengthStream(stream, stream.Length),
                    }, ct);
                }
            }
        }
    }

    private static UnixFileMode ModeOf(FileSystemInfo entry, string fallback) =>
        OperatingSystem.IsWindows() ? (UnixFileMode)Convert.ToInt32(fallback, 8) : File.GetUnixFileMode(entry.FullName);

    // A volume's (or a host folder's) files through a helper made (never started) with it at /v (or /h), read-only; its
    // running users paused for exactly that copy, and unpaused whatever happens.
    private async Task CopyVolumeAsync(BackupWriter writer, string part, string bind, string at, Func<JsonObject, bool> uses, string image,
        BackupInventory inventory, CancellationToken ct, BackupPartKind kind = BackupPartKind.Volume)
    {
        var helper = $"{HostScanHelper.NamePrefix}backup-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
        await engine.CreateContainerAsync(helper, HelperBody(image, bind), ct);
        var paused = new List<string>();
        var pausedListed = false;
        try
        {
            // What runs now (a "stop everything" save stopped them all; something may have started or stopped since the list).
            var users = new List<string>();
            foreach (var name in inventory.Inspects.Where(c => c.Key != inventory.SelfName && uses(c.Value)).Select(c => c.Key))
                if (await engine.InspectContainerAsync(name, ct) is { } now && BackupInventory.Running(now) && !BackupInventory.Paused(now))
                    users.Add(name);
            if (users.Count > 0)
            {
                await WriteListAsync(_paths.PausedList, users, ct);
                pausedListed = true;
                foreach (var user in users)
                {
                    // On the list before Docker is asked, and asked to the end: a pause cut short is undone too.
                    paused.Add(user);
                    await engine.PauseContainerAsync(user, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
            }
            await using var archive = await engine.GetArchiveAsync(helper, at, ct);
            await writer.WritePartAsync(kind, part, 0, archive, ct);
        }
        finally
        {
            // The list goes once every container on it runs again; one that won't unpause stays on it for the next start.
            if (pausedListed && await BringBackAsync(paused, start: false)) TryDelete(_paths.PausedList);
            try
            {
                await engine.RemoveContainerAsync(helper, force: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Couldn't remove the helper {Helper}", helper);
            }
        }
    }

    /// <summary>A helper container's create body: Gitberth's own image, never started, no network, the volume bound.</summary>
    internal static JsonObject HelperBody(string image, string bind) => new()
    {
        ["Image"] = image,
        ["User"] = "0:0",
        ["Labels"] = new JsonObject { [HostScanHelper.Label] = "backup" },
        ["NetworkDisabled"] = true,
        ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray(bind), ["NetworkMode"] = "none" },
    };

    private static bool UsesVolume(JsonObject inspect, string volume) =>
        (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Any(m => BackupInventory.Str(m, "Type") == "volume" && BackupInventory.Str(m, "Name") == volume);

    // A container that bind-mounts the folder, or something in it, or a folder it's in.
    private static bool UsesFolder(JsonObject inspect, string path) =>
        (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Any(m => BackupInventory.Str(m, "Type") == "bind"
            && HostFolders.Clean(BackupInventory.Str(m, "Source")) is { } source && (HostFolders.Under(source, path) || HostFolders.Under(path, source)));

    // Its own stop timeout, else Docker's 10 s (a database 60 s: it flushes first).
    private static int StopSeconds(JsonObject inspect) =>
        inspect["Config"]?["StopTimeout"] is JsonValue v && v.TryGetValue<int>(out var seconds) ? seconds
        : Services.ContainerService.IsDatabase(BackupInventory.Str(inspect["Config"], "Image"), inspect["Config"]) ? 60 : 10;

    /// <summary>
    /// Starts (or unpauses) each again, whatever the save's own token says. True once every one is back: one that's gone,
    /// or (Docker's 409) isn't paused, counts as back; its list may then go.
    /// </summary>
    private async Task<bool> BringBackAsync(IReadOnlyList<string> names, bool start)
    {
        var all = true;
        foreach (var name in names)
            try
            {
                if (start) await engine.StartContainerAsync(name, CancellationToken.None);
                else await engine.UnpauseContainerAsync(name, CancellationToken.None);
            }
            catch (DockerApiException ex) when (ex.NotFound || !start && ex.Status == 409)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                all = false;
                logger.LogWarning(ex, "Couldn't {Action} {Container} again after the save; it's tried again when Gitberth starts", start ? "start" : "unpause", name);
            }
        return all;
    }

    /// <summary>
    /// At start: the containers a save stopped or paused and couldn't start or unpause again (Gitberth stopped in the
    /// middle) are started or unpaused now; each list goes once all its containers are back.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        // The helpers a copy left (made, never started): they'd keep their volume in use.
        foreach (var c in await engine.ListAllAsync(ct))
            if (c.Labels.TryGetValue(HostScanHelper.Label, out var kind) && kind is "backup" or "restore" or AutoDestinations.HelperKind)
                try
                {
                    await engine.RemoveContainerAsync(c.Id, force: true, ct);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
                {
                    logger.LogWarning(ex, "Couldn't remove the helper {Helper} a save or restore left", c.Name);
                }
        foreach (var (list, start) in new[] { (_paths.PausedList, false), (_paths.StoppedList, true) })
        {
            if (!File.Exists(list)) continue;
            List<string> names;
            try
            {
                names = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(list, ct)) ?? [];
            }
            catch (JsonException)
            {
                names = [];
            }
            var back = new List<string>();
            var all = true;
            foreach (var name in names)
                try
                {
                    if (await engine.InspectContainerAsync(name, ct) is not { } c) continue;
                    if (start ? BackupInventory.Running(c) : !BackupInventory.Paused(c)) continue;
                    back.Add(name);
                    logger.LogWarning("{Container} was {What} by a save that didn't end; it's {Again} again", name, start ? "stopped" : "paused", start ? "started" : "unpaused");
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
                {
                    all = false;
                    logger.LogWarning(ex, "Couldn't look at {Container} after a save", name);
                }
            if (back.Count > 0 && !await BringBackAsync(back, start)) all = false;
            if (all) TryDelete(list);
        }
    }

    private static async Task WriteListAsync(string path, IReadOnlyList<string> names, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(names), ct);
    }

    // Volumes' sizes, asked of Docker in the background at most every two minutes, and the host folders containers mount,
    // measured by a helper; the task while one is asked.
    private Task? MeasureSizes()
    {
        lock (_sizesGate)
        {
            if (_measuring is { IsCompleted: false }) return _measuring;
            if (DateTime.UtcNow - _sizesAt < SizesFor) return null;
            _sizesAt = DateTime.UtcNow;
            return _measuring = Task.Run(async () =>
            {
                try
                {
                    _sizes = await engine.VolumeSizesAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException or JsonException)
                {
                    logger.LogDebug(ex, "Docker didn't size the volumes");
                }
                try
                {
                    _folders = await MeasureFoldersAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException or JsonException)
                {
                    logger.LogDebug(ex, "The host folders couldn't be measured");
                }
            });
        }
    }

    // Every bind source a container (Gitberth aside) mounts that may be saved, looked at by one helper of Gitberth's own
    // image: a folder and its size, or not a folder. None outside a container (there's no own image to run).
    private async Task<IReadOnlyDictionary<string, HostPathInfo>> MeasureFoldersAsync(CancellationToken ct)
    {
        if (options.OwnContainer is not { } self || await engine.InspectContainerAsync(self, ct) is not { } me
            || BackupInventory.Str(me, "Image") is not { Length: > 0 } image) return new Dictionary<string, HostPathInfo>();
        var ownData = BackupInventory.DataSource(me, options.DataDir);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in await engine.ListAllAsync(ct))
            if (c.Id != BackupInventory.Str(me, "Id") && !c.Labels.ContainsKey(HostScanHelper.Label) && await engine.InspectContainerAsync(c.Id, ct) is { } inspect)
                foreach (var (path, _) in BackupInventory.BindSources(inspect, ownData))
                    paths.Add(path);
        return await HostFolders.MeasureAsync(engine, image, [.. paths], HostFolders.MeasureFor, ct);
    }

    // What Gitberth's own data takes on disk, roughly.
    private long? DataBytes()
    {
        try
        {
            long total = 0;
            foreach (var file in BackupPaths.DataFiles)
                foreach (var suffix in new[] { "", "-wal" })
                    if (new FileInfo(Path.Combine(options.DataDir, file + suffix)) is { Exists: true } f) total += f.Length;
            foreach (var folder in BackupPaths.DataFolders)
                if (Directory.Exists(Path.Combine(options.DataDir, folder)))
                    total += new DirectoryInfo(Path.Combine(options.DataDir, folder)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Tickets nobody downloaded, and finished saves' progress, go when they expire; a key with them.
    private void Prune()
    {
        var now = Time.GetUtcNow().UtcDateTime;
        foreach (var (id, job) in _jobs)
            if (job.ExpiresAt < now && !job.Running && _jobs.TryRemove(id, out _))
            {
                job.Key?.Dispose();
                job.Key = null;
            }
    }

    private static string FileWord(string host)
    {
        var word = new string(host.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray());
        return word.Length > 0 ? word + "-" : "";
    }

    private static string? FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Why a save stopped, in one sentence the owner reads (never a secret).</summary>
public sealed class BackupSaveException(string message) : Exception(message);

/// <summary>
/// Exactly <paramref name="length"/> bytes of a file being written meanwhile: what it grew by after is left out, and what
/// it lost is read as zeros, so a tar entry's size always matches its data.
/// </summary>
internal sealed class FixedLengthStream(Stream inner, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var want = (int)Math.Min(buffer.Length, length - _position);
        if (want <= 0) return 0;
        var n = await inner.ReadAsync(buffer[..want], ct);
        if (n == 0)
        {
            buffer.Span[..want].Clear();
            n = want;
        }
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var want = (int)Math.Min(count, length - _position);
        if (want <= 0) return 0;
        var n = inner.Read(buffer, offset, want);
        if (n == 0)
        {
            Array.Clear(buffer, offset, want);
            n = want;
        }
        _position += n;
        return n;
    }

    // Only where it is already (a writer asking): it's read once, from its start.
    public override long Seek(long offset, SeekOrigin origin)
    {
        var to = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };
        if (to != _position) throw new NotSupportedException("This stream is read once, from its start.");
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>One save: its ticket, its key until it's written, and its progress.</summary>
internal sealed class SaveJob(string id, BackupChoice choice, BackupKey key, string fileName, DateTime expiresAt)
{
    private int _claimed;
    private string _phase = "Waiting for the download";
    private string? _current;
    private string? _problem;
    private bool _done;

    public string Id { get; } = id;
    public BackupChoice Choice { get; set; } = choice;
    public BackupKey? Key { get; set; } = key;

    /// <summary>An automatic backup: no download, no ticket; it waits its turn (<see cref="Wait"/>) and records its own end.</summary>
    public bool Auto { get; init; }

    /// <summary>An automatic backup's items, chosen from the list as it is when it starts.</summary>
    public Func<IReadOnlyList<BackupItem>, BackupChoice>? Choose { get; init; }

    /// <summary>How long it waits for a save being made to end (none for a download).</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.Zero;
    public string FileName { get; } = fileName;
    public DateTime ExpiresAt { get; private set; } = expiresAt;
    public BackupWriter? Writer { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public bool Running => _claimed == 1 && !_done;

    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    public void Report(string phase, string? current)
    {
        _phase = phase;
        _current = current;
    }

    public void Finish(string? problem, DateTime keepUntil)
    {
        if (Writer is { } w) _bytes = w.BytesWritten;
        _problem = problem;
        _phase = problem is null ? "Saved" : "Failed";
        _current = null;
        _done = true;
        Writer = null;
        ExpiresAt = keepUntil;
    }

    private long _bytes;

    public SaveProgress Snapshot()
    {
        if (Writer is { } w) _bytes = w.BytesWritten;
        return new SaveProgress(_phase, _current, _bytes, Done, Total, _done, _problem);
    }
}
