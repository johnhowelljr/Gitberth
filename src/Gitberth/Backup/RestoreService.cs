using System.Formats.Tar;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>
/// Restoring (IRestoreService), onto a fresh install. The file is uploaded first (POST /backup/upload, to
/// /data/restore/upload.gbak); <see cref="CheckAsync"/> opens it with the passphrase and reads its manifest. The restore
/// runs in two phases, so the passphrase is never kept: phase 1 (here, with the passphrase in memory) checks the whole file,
/// then brings back networks, volumes, images, database servers with their dumps and every other container, stages
/// Gitberth's data and exits; at the next start the staged data is put in place before the database is opened
/// (<see cref="RestoreBoot"/>), and phase 2 (<see cref="RestoreRunner"/>) deploys the apps again. Each step is written to
/// plan.json as it ends.
/// </summary>
public sealed partial class RestoreService : IRestoreService
{
    /// <summary>How long a database server is waited for to take connections after it starts.</summary>
    internal TimeSpan DatabaseReady { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long before exiting the restore lets the page show its last lines.</summary>
    internal TimeSpan ExitDelay { get; init; } = TimeSpan.FromSeconds(2);

    private readonly IBackupEngine _engine;
    private readonly IDbContextFactory<GitberthDb> _dbFactory;
    private readonly GitberthOptions _options;
    private readonly RestoreGate _gate;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<RestoreService> _logger;
    private readonly Security.SecretProtector? _protector;
    private readonly Services.AppActions? _actions;
    private readonly AppCatalog? _catalog;
    private readonly Domains.DomainService? _domains;
    private readonly HostPortMap? _hostPorts;
    private readonly Databases.DatabaseService? _databases;
    private readonly Services.AppService? _apps;
    private readonly BackupPaths _paths;
    private readonly object _planGate = new();
    private RestorePlanState? _plan;
    private Task? _phaseOne;

    /// <param name="protector">This Gitberth's, which a picked app's saved secrets are encrypted with again.</param>
    /// <param name="actions">Deploys a picked Git or Compose app, and makes a picked image app's container again.</param>
    /// <param name="catalog">Brings a picked app's row in line with its container.</param>
    /// <param name="domains">Syncs a picked app's domains.</param>
    /// <param name="hostPorts">Finds a picked app a free host port when its own is taken.</param>
    /// <param name="databases">A copy's own database URLs, for its container's environment.</param>
    /// <param name="apps">Checks a copy's name as New app checks one.</param>
    public RestoreService(IBackupEngine engine, IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, RestoreGate gate,
        ILogger<RestoreService> logger, IHostApplicationLifetime? lifetime = null, Security.SecretProtector? protector = null,
        Services.AppActions? actions = null, AppCatalog? catalog = null, Domains.DomainService? domains = null, HostPortMap? hostPorts = null,
        Databases.DatabaseService? databases = null, Services.AppService? apps = null)
    {
        _databases = databases;
        _apps = apps;
        _engine = engine;
        _dbFactory = dbFactory;
        _options = options;
        _gate = gate;
        _logger = logger;
        _lifetime = lifetime;
        _protector = protector;
        _actions = actions;
        _catalog = catalog;
        _domains = domains;
        _hostPorts = hostPorts;
        _paths = new BackupPaths(options);
        Log = new RestoreLog(_paths);
        _plan = RestorePlanState.Load(_paths);
    }

    /// <summary>The log the Restoring page shows, across the restart.</summary>
    public RestoreLog Log { get; }

    internal BackupPaths Paths => _paths;

    /// <summary>Phase 1 while it runs (tests wait for it).</summary>
    internal Task PhaseOne => _phaseOne ?? Task.CompletedTask;

    /// <summary>What happens when phase 1 ends: Gitberth exits, and Docker starts it again (tests count it instead).</summary>
    internal Action? Exit { get; init; }

    public string? CantRestore() => Running ? "A restore is running." : null;

    public string? CantRestoreEverything()
    {
        if (Running) return "A restore is running.";
        using var db = _dbFactory.CreateDbContext();
        return db.Apps.Any()
            ? "This Gitberth already has apps: pick the apps to bring back. The whole backup is restored onto a Gitberth with no apps yet."
            : null;
    }

    // Phase 1 running here, or phase 2 still to finish after the restart.
    private bool Running => _phaseOne is { IsCompleted: false } || Plan is { PhaseOneDone: true, Finished: false };

    public long? UploadedBytes() => File.Exists(_paths.Upload) ? new FileInfo(_paths.Upload).Length : null;

    /// <summary>
    /// The upload (POST /backup/upload): the request's body streamed to /data/restore/upload.gbak, through a file of its
    /// own until it's whole. Refused while a restore can't start. Returns its size.
    /// </summary>
    internal async Task<long> ReceiveAsync(Stream body, CancellationToken ct)
    {
        if (CantRestore() is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(_paths.RestoreDir);
        try
        {
            await using (var file = new FileStream(_paths.UploadPart, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await body.CopyToAsync(file, 1 << 20, ct);
            File.Move(_paths.UploadPart, _paths.Upload, overwrite: true);
        }
        finally
        {
            TryDelete(_paths.UploadPart);
        }
        return new FileInfo(_paths.Upload).Length;
    }

    public async Task<UploadCheck> CheckAsync(string passphrase, CancellationToken ct = default)
    {
        if (!File.Exists(_paths.Upload)) return new UploadCheck(UploadProblem.NotABackup, "No file was uploaded.", null);
        if (CantRestore() is { } why) return new UploadCheck(UploadProblem.NotEmpty, why, null);
        BackupManifest manifest;
        Dictionary<int, SavedAppRows> saved;
        try
        {
            // The key is made once, for the manifest and the backup's own data (its apps, read to compare with what's here).
            await using (var file = OpenUpload())
            {
                var (header, _) = await BackupReader.ReadHeaderAsync(file, ct);
                using var key = await Task.Run(() => BackupKey.ForFile(header, passphrase), ct);
                manifest = await ReadManifestAsync(key, ct);
                if (await TooNewAsync(manifest, ct) is { } newer) return new UploadCheck(UploadProblem.TooNew, newer, null);
                saved = await SavedAppsOrNoneAsync(manifest, key, ct);
            }
        }
        catch (BackupFileException ex)
        {
            return new UploadCheck(ex.Problem, ex.Message, null);
        }
        var warnings = await WarningsAsync(manifest, ct);
        return new UploadCheck(UploadProblem.None, null,
            new BackupSummary(manifest.GitberthVersion, manifest.SavedAt, manifest.Host, manifest.Architecture, manifest.DockerVersion, manifest.Items, warnings),
            await TargetsAsync(manifest, saved, ct), CantRestoreEverything() is null);
    }

    public async Task StartAsync(string passphrase, CancellationToken ct = default)
    {
        if (CantRestoreEverything() is { } why) throw new InvalidOperationException(why);
        var check = await CheckAsync(passphrase, ct);
        if (check.Problem != UploadProblem.None || check.Summary is null) throw new InvalidOperationException(check.Message ?? "This file can't be restored.");
        if (check.Summary.Warnings.FirstOrDefault(w => w.Blocks) is { } blocking) throw new InvalidOperationException(blocking.Message);

        // The key is made once for the file and used for every pass over it; the passphrase isn't kept.
        BackupKey key;
        await using (var file = OpenUpload())
        {
            var (header, _) = await BackupReader.ReadHeaderAsync(file, ct);
            key = await Task.Run(() => BackupKey.ForFile(header, passphrase), ct);
        }
        var manifest = await ReadManifestAsync(key, ct);
        lock (_planGate)
        {
            if (Running)
            {
                key.Dispose();
                throw new InvalidOperationException("A restore is running.");
            }
            _gate.Close();
            // The same backup's whole restore that stopped in its first phase goes on from where it was; anything else (a
            // restore of picked items from the same file among them) starts anew.
            var old = Plan;
            var resume = old is not null && old.Of(manifest) && !old.PhaseOneDone && old.Step("gitberth") is not null;
            var plan = resume ? old! : RestorePlanState.New(manifest, DateTime.UtcNow);
            if (!resume) Log.Clear();
            plan.Finished = false;
            foreach (var step in plan.Steps.Where(s => s.State is RestoreStepState.Running or RestoreStepState.Failed))
            {
                step.State = RestoreStepState.Waiting;
                step.Note = null;
            }
            _plan = plan;
            Save();
            _phaseOne = Task.Run(() => PhaseOneAsync(key, manifest));
        }
    }

    public RestoreStatus? Status()
    {
        lock (_planGate) return Plan?.View(Running);
    }

    public IReadOnlyList<string> LogLines(int from) => Log.From(from);

    public Task DiscardUploadAsync(CancellationToken ct = default)
    {
        if (Running) throw new InvalidOperationException("A restore is running.");
        TryDelete(_paths.Upload);
        TryDelete(_paths.UploadPart);
        // What a restore that didn't end staged from it (the old machine's key ring and database, unencrypted) goes too.
        DeleteStaged();
        return Task.CompletedTask;
    }

    /// <summary>The restore's plan as plan.json has it; null when none was ever started.</summary>
    internal RestorePlanState? PlanState => Plan;

    // Gitberth's data staged from a backup holds its key ring and secrets in plain files: it never stays behind a restore
    // that won't use it.
    private void DeleteStaged()
    {
        try
        {
            if (Directory.Exists(_paths.Staged)) Directory.Delete(_paths.Staged, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't remove the restore's staged data");
        }
    }

    private RestorePlanState? Plan
    {
        get
        {
            lock (_planGate) return _plan;
        }
    }

    internal void Reload()
    {
        lock (_planGate) _plan = RestorePlanState.Load(_paths);
    }

    private void Save()
    {
        lock (_planGate) _plan?.Save(_paths);
    }

    // ---- reading the file ----

    private FileStream OpenUpload() => new(_paths.Upload, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

    private async Task<BackupManifest> ReadManifestAsync(string passphrase, CancellationToken ct)
    {
        await using var file = OpenUpload();
        using var reader = await BackupReader.OpenAsync(file, passphrase, ct);
        return await ManifestOfAsync(reader, ct);
    }

    private async Task<BackupManifest> ReadManifestAsync(BackupKey key, CancellationToken ct)
    {
        await using var file = OpenUpload();
        using var reader = await BackupReader.OpenAsync(file, key, ct);
        return await ManifestOfAsync(reader, ct);
    }

    private static async Task<BackupManifest> ManifestOfAsync(BackupReader reader, CancellationToken ct)
    {
        if (await reader.NextPartAsync(ct) is not { Kind: BackupPartKind.Manifest } part) throw BackupReader.Damaged();
        var json = new MemoryStream();
        await part.Content.CopyToAsync(json, ct);
        try
        {
            return BackupManifest.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            throw BackupReader.Damaged();
        }
    }

    // Made by a newer Gitberth: a format, or a database migration, this one doesn't know.
    private async Task<string?> TooNewAsync(BackupManifest manifest, CancellationToken ct)
    {
        const string update = "This backup was made by a newer Gitberth ({0}). Update Gitberth, then restore it.";
        if (manifest.Format > BackupFormat.Newest) return string.Format(CultureInfo.InvariantCulture, update, manifest.GitberthVersion);
        if (manifest.LastMigration is not { Length: > 0 } last) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return db.Database.GetMigrations().Contains(last, StringComparer.Ordinal) ? null : string.Format(CultureInfo.InvariantCulture, update, manifest.GitberthVersion);
    }

    /// <summary>What the owner should know about this host before the restore runs.</summary>
    internal async Task<List<RestoreWarning>> WarningsAsync(BackupManifest manifest, CancellationToken ct)
    {
        var warnings = new List<RestoreWarning>();
        var created = manifest.Containers.Where(c => c.How != ComesBack.Deploy).ToList();
        foreach (var tunnel in created.Where(c => IsTunnel(c.Inspect)))
            warnings.Add(new(RestoreWarningKind.TunnelElsewhere,
                $"This backup brings back {tunnel.Name}. If the old machine still runs it, stop it there once this one is up: two machines on one tunnel share its visitors.", false));

        var here = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var c in await _engine.ListAllAsync(ct))
            if (await _engine.InspectContainerAsync(c.Id, ct) is { } inspect) here[BackupInventory.Name(inspect)] = inspect;
        foreach (var c in created.Where(c => here.ContainsKey(c.Name)))
            warnings.Add(new(RestoreWarningKind.NameTaken, $"A container called {c.Name} is here already: it's left as it is, and the saved one isn't restored.", false));

        var taken = new Dictionary<int, string>();
        foreach (var (name, inspect) in here)
            foreach (var port in HostPorts(inspect))
                taken.TryAdd(port, name);
        foreach (var c in manifest.Containers.Where(c => !here.ContainsKey(c.Name)))
            foreach (var port in HostPorts(c.Inspect).Where(taken.ContainsKey).Distinct())
                warnings.Add(new(RestoreWarningKind.PortTaken,
                    $"Port {port.ToString(CultureInfo.InvariantCulture)} is taken here by {taken[port]}: {c.Name} can't use it until it's free.", false));

        var volumes = (await _engine.ListVolumesAsync(ct)).OfType<JsonObject>().Select(v => BackupInventory.Str(v, "Name")).ToHashSet(StringComparer.Ordinal);
        foreach (var v in manifest.Volumes.Where(v => v.Part is not null && volumes.Contains(v.Name)))
            warnings.Add(new(RestoreWarningKind.VolumeHasData, $"The volume {v.Name} is here already: it's kept as it is, not overwritten.", false));

        var info = await _engine.InfoAsync(ct);
        var arch = BackupInventory.Str(info, "Architecture");
        if (arch.Length > 0 && manifest.Architecture.Length > 0 && arch != manifest.Architecture)
            warnings.Add(new(RestoreWarningKind.ArchitectureDiffers,
                $"The backup was made on {manifest.Architecture} and this host is {arch}: images only the old host had may not run here.", false));

        var api = await _engine.ApiVersionAsync(ct);
        var version = await _engine.EngineVersionAsync(ct);
        if (api < DockerApi.OldestChangingApi)
            warnings.Add(new(RestoreWarningKind.DockerTooOld, $"Restoring needs Docker 25 or later; this host has Docker {version}.", true));
        else if (Version.TryParse(version, out var mine) && Version.TryParse(manifest.DockerVersion, out var theirs) && mine < theirs)
            warnings.Add(new(RestoreWarningKind.DockerTooOld, $"This host's Docker ({version}) is older than the old host's ({manifest.DockerVersion}).", false));

        var needed = manifest.Volumes.Sum(v => v.Part is null ? 0 : v.Bytes ?? 0) + manifest.Images.Sum(i => i.Part is null ? 0 : i.Bytes ?? 0);
        try
        {
            var free = new DriveInfo(Path.GetFullPath(_options.DataDir)).AvailableFreeSpace;
            if (needed > 0 && free < needed)
                warnings.Add(new(RestoreWarningKind.NeedsSpace,
                    $"The backup's volumes and images take about {Size(needed)}, and this disk has {Size(free)} free.", false));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        return warnings;
    }

    private static bool IsTunnel(JsonObject inspect) =>
        Services.ContainerService.Repository(BackupInventory.Str(inspect["Config"], "Image")).Equals("cloudflared", StringComparison.OrdinalIgnoreCase)
        || BackupInventory.StrList(inspect["Config"]?["Entrypoint"]).Concat(BackupInventory.StrList(inspect["Config"]?["Cmd"])).Any(a => a.EndsWith("cloudflared", StringComparison.Ordinal));

    private static IEnumerable<int> HostPorts(JsonObject inspect) =>
        (inspect["HostConfig"]?["PortBindings"] as JsonObject ?? []).SelectMany(p => p.Value as JsonArray ?? [])
            .Select(b => int.TryParse(BackupInventory.Str(b, "HostPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0)
            .Where(p => p > 0);

    private static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    // ---- phase 1 ----

    private async Task PhaseOneAsync(BackupKey key, BackupManifest manifest)
    {
        // What it changes is the change of whoever started it (their scope flows here).
        var ct = _lifetime?.ApplicationStopping ?? CancellationToken.None;
        Stopping = ct;
        foreach (var c in manifest.Containers)
            foreach (var value in Secrets(c.Inspect))
                Log.Secret(value);
        try
        {
            Log.Line($"Restoring the backup made on {manifest.Host} on {manifest.SavedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC (Gitberth {manifest.GitberthVersion}).");

            // Nothing is restored from a file that isn't whole: every chunk is checked first.
            if (!await StepAsync("check", async () =>
                {
                    await using var file = OpenUpload();
                    var parts = await BackupReader.VerifyAsync(file, key, ct);
                    return $"Every part is whole ({parts.Count.ToString(CultureInfo.InvariantCulture)} parts).";
                }))
            {
                Abort("The file can't be used, so nothing was restored.");
                return;
            }

            var ownImage = await OwnImageAsync(ct);

            // Networks the saved containers are on (never gitberth-lan: its addresses are the old machine's LAN).
            var networks = (await _engine.ListNetworksJsonAsync(ct)).OfType<JsonObject>().Select(n => BackupInventory.Str(n, "Name")).ToHashSet(StringComparer.Ordinal);
            foreach (var network in manifest.Networks)
                await StepAsync("network:" + network.Name, async () =>
                {
                    if (network.Name == LanAddresses.Network)
                        return Skip("Not made again: its LAN addresses are the old machine's. Set own LAN addresses up again in Settings › Network.");
                    if (networks.Contains(network.Name)) return Skip("It's here already.");
                    await _engine.CreateNetworkAsync(NetworkBody(network), ct);
                    return "Made.";
                });

            // Pass 1 over the file: Gitberth's data staged, volumes filled, images loaded. Dumps wait for their servers.
            var volumes = (await _engine.ListVolumesAsync(ct)).OfType<JsonObject>().Select(v => BackupInventory.Str(v, "Name")).ToHashSet(StringComparer.Ordinal);
            var byPart = manifest.Volumes.Where(v => v.Part is not null).ToDictionary(v => v.Part!, StringComparer.Ordinal);
            var images = manifest.Images.Where(i => i.Part is not null).ToDictionary(i => i.Part!, StringComparer.Ordinal);
            var folders = manifest.HostFolders.Where(f => f.Part is not null).ToDictionary(f => f.Part!, StringComparer.Ordinal);
            if (Directory.Exists(_paths.Staged)) Directory.Delete(_paths.Staged, recursive: true);
            Directory.CreateDirectory(_paths.Staged);
            await using (var file = OpenUpload())
            using (var reader = await BackupReader.OpenAsync(file, key, ct))
            {
                while (await reader.NextPartAsync(ct) is { } part)
                {
                    switch (part.Kind)
                    {
                        case BackupPartKind.DataFile:
                            await StageFileAsync(part, ct);
                            break;
                        case BackupPartKind.DataFolder:
                            await StageFolderAsync(part, ct);
                            break;
                        case BackupPartKind.Volume when byPart.TryGetValue(part.Name, out var volume):
                            await StepAsync("volume:" + volume.Name, () => FillVolumeAsync(volume, part.Content, volumes, ownImage, ct));
                            break;
                        case BackupPartKind.HostFolder when folders.TryGetValue(part.Name, out var folder):
                            await StepAsync(HostFolders.ItemId(folder.Path), () => FillFolderAsync(folder.Path, folder, part.Content, RestoreAction.Restore, ownImage, ct));
                            break;
                        case BackupPartKind.Image when images.TryGetValue(part.Name, out var image):
                            await StepAsync("image:" + image.Id, async () =>
                            {
                                if (await _engine.InspectImageAsync(image.Id, ct) is not null) return Skip("It's here already.");
                                var said = await _engine.LoadImagesAsync(part.Content, ct);
                                return said.Length > 0 ? said.Replace('\n', ' ') : "Loaded.";
                            });
                            break;
                    }
                }
            }

            // Volumes without files of their own: a database's (its dump fills it), or one left out of the save.
            foreach (var volume in manifest.Volumes.Where(v => v.Part is null))
                await StepAsync("volume:" + volume.Name, async () =>
                {
                    if (volumes.Contains(volume.Name)) return Skip("It's here already: kept as it is.");
                    await MakeVolumeAsync(volume, volumes, ct);
                    return manifest.Databases.Any(d => d.DataVolumes.Contains(volume.Name)) ? "Made, empty: its database's dump fills it." : "Made, empty (its files weren't saved).";
                });

            // Images registries have: pulled by the digest the old host ran, then given its names.
            foreach (var image in manifest.Images.Where(i => i.Part is null))
                await StepAsync("pull:" + image.Id, () => PullAsync(image, ct));

            // Database servers, then their dumps.
            foreach (var database in manifest.Databases)
                await StepAsync("database:" + database.Container, () => RestoreDatabaseAsync(database, manifest, key, ct));

            // Every other container, from its inspect: databases first, then the rest; those that ran are started.
            foreach (var container in RestorePlanner.Created(manifest))
                await StepAsync("container:" + container.Name, () => CreateAsync(container, manifest, ct));

            // Gitberth's data is staged: it's put in place when Gitberth starts again.
            await StepAsync("gitberth", () =>
            {
                File.WriteAllText(_paths.StagedReady, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                return Task.FromResult("Staged: it's put in place when Gitberth starts again.");
            });

            lock (_planGate) _plan!.PhaseOneDone = true;
            Save();
            Log.Line("Gitberth restarts now to finish on the saved data. You're signed out: sign in with the saved machine's admin password.");
            await Task.Delay(ExitDelay, CancellationToken.None);
            if (Exit is { } exit) exit();
            else _lifetime?.StopApplication();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Gitberth is stopping: the plan stays as it is, and the same restore started again goes on from it (staging
            // Gitberth's data again, so none of it is left in plain files meanwhile).
            DeleteStaged();
            Log.Line("Gitberth stopped in the middle of the restore. Start it again with the same file: it goes on from where it was.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The restore stopped");
            Log.Line("The restore stopped: " + OneLine(ex));
            Abort(null);
        }
        finally
        {
            key.Dispose();
        }
    }

    /// <summary>The restore is over (phase 2 ended): its plan says so, and Gitberth's jobs carry on.</summary>
    internal void Finish() => Abort(null);

    // A restore that can't go on: its plan says so, what it staged goes, and Gitberth's jobs carry on.
    private void Abort(string? why)
    {
        if (why is not null) Log.Line(why);
        lock (_planGate)
        {
            if (_plan is not null) _plan.Finished = true;
        }
        Save();
        DeleteStaged();
        _gate.Open();
    }

    /// <summary>
    /// One step: skipped when plan.json says it's done, else run and written down as it ends (Done, Skipped or Failed, with
    /// its note). A failed step is logged and the restore goes on; false when it failed.
    /// </summary>
    internal async Task<bool> StepAsync(string id, Func<Task<string>> run)
    {
        RestoreStepRecord? step;
        lock (_planGate) step = _plan?.Step(id);
        if (step is null) return true;
        if (step.State is RestoreStepState.Done or RestoreStepState.Skipped) return true;
        step.State = RestoreStepState.Running;
        Save();
        try
        {
            var note = await run();
            var skipped = note.StartsWith(SkipMark, StringComparison.Ordinal);
            step.Note = skipped ? note[SkipMark.Length..] : note;
            step.State = skipped ? RestoreStepState.Skipped : RestoreStepState.Done;
            Log.Line($"{step.Title}: {step.Note}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !Stopping.IsCancellationRequested)
        {
            step.State = RestoreStepState.Failed;
            step.Note = OneLine(ex);
            Log.Line($"{step.Title}: failed. {step.Note}");
            _logger.LogWarning(ex, "Restore step {Step} failed", id);
            return false;
        }
        finally
        {
            Save();
        }
    }

    /// <summary>Gitberth stopping: a step it cuts short stays to run again, rather than failing.</summary>
    internal CancellationToken Stopping { get; set; }

    private const string SkipMark = "\u0001skip:";

    internal static string Skip(string why) => SkipMark + why;

    private static string OneLine(Exception ex) => (ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? ex.GetType().Name).TrimEnd('.') + ".";

    // ---- the steps ----

    private async Task<string> OwnImageAsync(CancellationToken ct) =>
        _options.OwnContainer is { } self && await _engine.InspectContainerAsync(self, ct) is { } me && BackupInventory.Str(me, "Image") is { Length: > 0 } image
            ? image
            : throw new InvalidOperationException("Volumes are filled by a helper of Gitberth's own image, and Gitberth isn't running in a container here.");

    private static JsonObject NetworkBody(SavedNetwork network)
    {
        var n = network.Inspect;
        var body = new JsonObject { ["Name"] = network.Name, ["CheckDuplicate"] = true };
        foreach (var key in new[] { "Driver", "Internal", "Attachable", "EnableIPv6", "Ingress", "Scope" })
            if (n[key] is { } value) body[key] = value.DeepClone();
        foreach (var key in new[] { "Options", "Labels" })
            if (n[key] is JsonObject o) body[key] = o.DeepClone();
        if (n["IPAM"] is JsonObject ipam)
        {
            var copy = new JsonObject { ["Driver"] = ipam["Driver"]?.DeepClone() ?? "default" };
            if (ipam["Config"] is JsonArray config && config.Count > 0) copy["Config"] = config.DeepClone();
            if (ipam["Options"] is JsonObject options) copy["Options"] = options.DeepClone();
            body["IPAM"] = copy;
        }
        return body;
    }

    private async Task MakeVolumeAsync(SavedVolume volume, HashSet<string> volumes, CancellationToken ct)
    {
        var body = new JsonObject { ["Name"] = volume.Name, ["Driver"] = volume.Driver };
        if (volume.Options is { Count: > 0 } options) body["DriverOpts"] = Object(options);
        if (volume.Labels is { Count: > 0 } labels) body["Labels"] = Object(labels);
        await _engine.CreateVolumeAsync(body, ct);
        volumes.Add(volume.Name);
        lock (_planGate)
            if (_plan is not null && !_plan.MadeVolumes.Contains(volume.Name)) _plan.MadeVolumes.Add(volume.Name);
        Save();
    }

    // A volume made (unless this restore made it before: a volume that was here already is kept as it is), then filled
    // through a helper made with it at /v: the tar's entries are v/…, put in at /.
    private async Task<string> FillVolumeAsync(SavedVolume volume, Stream tar, HashSet<string> volumes, string ownImage, CancellationToken ct)
    {
        bool ours;
        lock (_planGate) ours = _plan?.MadeVolumes.Contains(volume.Name) == true;
        if (volumes.Contains(volume.Name) && !ours) return Skip("It's here already: kept as it is, not overwritten.");
        if (!volumes.Contains(volume.Name)) await MakeVolumeAsync(volume, volumes, ct);
        var helper = $"{HostScanHelper.NamePrefix}restore-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
        var body = BackupService.HelperBody(ownImage, volume.Name + ":/v");
        body["Labels"]![HostScanHelper.Label] = "restore";
        await _engine.CreateContainerAsync(helper, body, ct);
        try
        {
            await _engine.PutArchiveAsync(helper, "/", tar, ct);
        }
        finally
        {
            try
            {
                await _engine.RemoveContainerAsync(helper, force: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Couldn't remove the helper {Helper}", helper);
            }
        }
        return volume.Bytes is { } bytes ? $"Filled ({Size(bytes)})." : "Filled.";
    }

    /// <summary>
    /// A host folder from the file, into <paramref name="target"/> (its own path, or a copy's): a missing or empty one is
    /// filled. Restore keeps one that holds files as it is (and says so); Replace moves its files aside first
    /// (&lt;path&gt;.before-&lt;time&gt;); a copy's own folder must be new.
    /// </summary>
    internal async Task<string> FillFolderAsync(string target, SavedHostFolder folder, Stream tar, RestoreAction action, string ownImage, CancellationToken ct)
    {
        if (await HostFolders.HasFilesAsync(_engine, ownImage, target, ct))
        {
            switch (action)
            {
                case RestoreAction.Restore:
                    return Skip($"{target} holds files here already: it's kept as it is, and the saved files aren't copied over them.");
                case RestoreAction.Copy:
                    throw new InvalidOperationException($"The folder {target} holds files here already, and it isn't this copy's.");
                default:
                    var before = HostFolders.BeforePath(target, DateTime.UtcNow);
                    await HostFolders.MoveAsideAsync(_engine, ownImage, target, before, ct);
                    Log.Line($"{target}: its files as they were are kept in {before}.");
                    break;
            }
        }
        await HostFolders.FillAsync(_engine, ownImage, target, tar, ct);
        return folder.Bytes is { } bytes ? $"{target}: filled ({Size(bytes)})." : $"{target}: filled.";
    }

    private async Task<string> PullAsync(SavedImage image, CancellationToken ct)
    {
        if (await _engine.InspectImageAsync(image.Id, ct) is not null)
        {
            await TagAsync(image, image.Id, ct);
            return Skip("It's here already.");
        }
        string? pulled = null;
        if (image.Digest is { } digest && Images.ImageReference.Parse(digest) is { Digest: { } d } byDigest)
        {
            try
            {
                await _engine.PullImageAsync(byDigest.FromImage, d, null, ct);
                pulled = digest;
            }
            catch (DockerApiException ex)
            {
                Log.Line($"Pulling {digest} failed ({ex.Message}); trying its tag.");
            }
        }
        if (pulled is null && image.Tags.FirstOrDefault() is { } tag && Images.ImageReference.Parse(tag) is { } byTag)
        {
            await _engine.PullImageAsync(byTag.FromImage, byTag.TagOrDigest, null, ct);
            pulled = tag;
        }
        if (pulled is null) throw new InvalidOperationException("Nothing names it in a registry, so it can't be pulled.");
        await TagAsync(image, pulled, ct);
        return "Pulled " + pulled + ".";
    }

    // The names the old host gave it, so containers find it by them.
    private async Task TagAsync(SavedImage image, string pulled, CancellationToken ct)
    {
        foreach (var tag in image.Tags)
        {
            if (tag == pulled || Images.ImageReference.Parse(tag) is not { Tag: { } t } reference) continue;
            if (await _engine.InspectImageAsync(tag, ct) is not null) continue;
            await _engine.TagImageAsync(pulled, reference.Name, t, ct);
        }
    }

    // A database server: made from its inspect (its data volume empty), started, waited for, then its dump loaded on the
    // standard input of its own client.
    private async Task<string> RestoreDatabaseAsync(SavedDatabase database, BackupManifest manifest, BackupKey key, CancellationToken ct)
    {
        var container = manifest.Containers.First(c => c.Name == database.Container);
        if (await _engine.InspectContainerAsync(container.Name, ct) is null)
        {
            await CreateContainerAsync(container, manifest, ct);
            await _engine.StartContainerAsync(container.Name, ct);
        }
        else if (await _engine.InspectContainerAsync(container.Name, ct) is { } running && !BackupInventory.Running(running))
            await _engine.StartContainerAsync(container.Name, ct);

        var plan = BackupInventory.DatabasePlanOf(container.Inspect, container.Image, []) ?? throw new InvalidOperationException("It isn't a database Gitberth can load.");
        await WaitReadyAsync(plan, ct);

        await using var file = OpenUpload();
        using var reader = await BackupReader.OpenAsync(file, key, ct);
        while (await reader.NextPartAsync(ct) is { } part)
        {
            if (part.Kind != BackupPartKind.Dump || part.Name != database.Part) continue;
            var r = await _engine.ExecAsync(plan.Container, LoadCommand(plan), BackupInventory.DumpEnv(plan), part.Content, Stream.Null, ct);
            var errors = Errors(r.StdErr);
            if (!r.Ok) throw new InvalidOperationException($"Loading its dump failed (exit code {r.ExitCode.ToString(CultureInfo.InvariantCulture)}): {errors.FirstOrDefault() ?? "it said nothing"}");
            return errors.Count == 0 ? "Made, and its dump loaded." : $"Made, and its dump loaded; it said: {errors[0]}";
        }
        throw new InvalidOperationException("Its dump isn't in the file.");
    }

    // What a load's client complained about, without what's expected on a fresh server (its own role, made at its start).
    private static List<string> Errors(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) && !l.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();

    /// <summary>The client that loads a dump: psql going on past errors (a fresh server has some of it already), or mysql --force.</summary>
    internal static IReadOnlyList<string> LoadCommand(DatabasePlan plan) => plan.Engine == "postgres"
        ? ["psql", "-X", "-q", "-v", "ON_ERROR_STOP=0", "-U", plan.User, "-d", "postgres", "-f", "-"]
        : ["sh", "-c", "if command -v mariadb >/dev/null 2>&1; then exec mariadb \"$@\"; else exec mysql \"$@\"; fi", "sh", "-u", plan.User, "--force"];

    // Ready once it takes connections over TCP: the official images' first start runs a server of their own on its socket
    // alone while it sets itself up, and stops it again.
    private async Task WaitReadyAsync(DatabasePlan plan, CancellationToken ct)
    {
        IReadOnlyList<string> probe = plan.Engine == "postgres"
            ? ["pg_isready", "-h", "127.0.0.1", "-U", plan.User, "-q"]
            : ["sh", "-c", "if command -v mariadb-admin >/dev/null 2>&1; then exec mariadb-admin \"$@\"; else exec mysqladmin \"$@\"; fi", "sh",
                "ping", "-h", "127.0.0.1", "--protocol=tcp", "-u", plan.User, "--silent"];
        var until = DateTime.UtcNow + DatabaseReady;
        while (true)
        {
            if (await _engine.InspectContainerAsync(plan.Container, ct) is { } c && !BackupInventory.Running(c))
                throw new InvalidOperationException("It stopped right after it started: see its logs.");
            try
            {
                if ((await _engine.ExecAsync(plan.Container, probe, BackupInventory.DumpEnv(plan), null, null, ct)).Ok) return;
            }
            catch (DockerApiException)
            {
                // Not running yet.
            }
            if (DateTime.UtcNow > until) throw new TimeoutException($"It didn't take connections within {DatabaseReady.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes.");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<string> CreateAsync(SavedContainer container, BackupManifest manifest, CancellationToken ct)
    {
        if (await _engine.InspectContainerAsync(container.Name, ct) is not null)
            return Skip($"A container called {container.Name} is here already: it's left as it is.");
        await CreateContainerAsync(container, manifest, ct);
        if (!container.Running) return "Made (it wasn't running, so it isn't started).";
        await _engine.StartContainerAsync(container.Name, ct);
        return "Made and started.";
    }

    // Its settings read from its inspect and written into a new create body (ContainerSettingsJson), on the networks it was
    // on that are here (never gitberth-lan, whose addresses are the old LAN's).
    private async Task CreateContainerAsync(SavedContainer container, BackupManifest manifest, CancellationToken ct)
    {
        var settings = ContainerSettingsJson.Read(container.Inspect, container.Image);
        if (settings.NetworkMode.StartsWith("container:", StringComparison.Ordinal))
        {
            // Another container's network, by its ID on the old host: by its name here.
            var other = settings.NetworkMode["container:".Length..];
            if (manifest.Containers.FirstOrDefault(c => BackupInventory.Str(c.Inspect, "Id").StartsWith(other, StringComparison.Ordinal) || c.Name == other) is { } target)
                settings = settings with { NetworkMode = "container:" + target.Name };
        }
        var here = (await _engine.ListNetworksJsonAsync(ct)).OfType<JsonObject>().Select(n => BackupInventory.Str(n, "Name")).ToHashSet(StringComparer.Ordinal);
        var joins = AppCatalog.JoinsOf(container.Inspect, _options.Network)
            .Where(j => j.Name != LanAddresses.Network && here.Contains(j.Name)).ToList();
        if (ContainerSettingsJson.IsOwnMode(settings.NetworkMode)) joins = [];
        var api = await _engine.ApiVersionAsync(ct);
        var body = ContainerSettingsJson.NewBody(settings, joins, "", api >= new Version(1, 48));
        await _engine.CreateContainerAsync(container.Name, body, ct);
    }

    // ---- Gitberth's data, staged ----

    private async Task StageFileAsync(BackupPart part, CancellationToken ct)
    {
        var name = Path.GetFileName(part.Name);
        if (!BackupPaths.DataFiles.Contains(name, StringComparer.Ordinal)) return;
        await using var file = new FileStream(Path.Combine(_paths.Staged, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await part.Content.CopyToAsync(file, ct);
    }

    private Task StageFolderAsync(BackupPart part, CancellationToken ct) => ExtractAsync(part.Content, _paths.Staged, ct);

    /// <summary>
    /// A folder's tar extracted under <paramref name="root"/>: each entry must stay inside it, and an entry whose path goes
    /// through a link (one the extraction itself made: the folder is new) is skipped, so nothing is written through a link.
    /// </summary>
    /// <param name="rename">An entry's name to the one it's written under (null: left out); each entry as named when none.</param>
    internal static async Task ExtractAsync(Stream tar, string root, CancellationToken ct, Func<string, string?>? rename = null)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var named = rename is null ? entry.Name : rename(entry.Name);
            if (named is null) continue;
            var target = Path.GetFullPath(Path.Combine(root, named.TrimStart('/')));
            if (!target.StartsWith(root, StringComparison.Ordinal) || ThroughLink(root, target)) continue;
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    SetMode(target, entry.Mode);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await entry.ExtractToFileAsync(target, overwrite: true, ct);
                    break;
                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Directory.Exists(target) || File.Exists(target)) continue;
                    File.CreateSymbolicLink(target, entry.LinkName);
                    break;
            }
        }
    }

    // Whether the path, or a folder on its way down from the root, is a link: writing there would go wherever it points.
    private static bool ThroughLink(string root, string target)
    {
        var path = target.TrimEnd(Path.DirectorySeparatorChar);
        while (path.Length >= root.Length)
        {
            if (new FileInfo(path).LinkTarget is not null) return true;
            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent.Length >= path.Length) break;
            path = parent;
        }
        return false;
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    // The values in a container's settings that look like secrets: never written to the log.
    private static IEnumerable<string> Secrets(JsonObject inspect) =>
        BackupInventory.StrList(inspect["Config"]?["Env"]).Select(e => e.Split('=', 2)).Where(kv => kv.Length == 2 && kv[1].Length >= 4)
            .Where(kv => kv[0].Contains("PASS", StringComparison.OrdinalIgnoreCase) || kv[0].Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || kv[0].Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || kv[0].Contains("KEY", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1]);

    private static JsonObject Object(IReadOnlyDictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map) o[k] = v;
        return o;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
