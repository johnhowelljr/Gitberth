using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Gitberth.Audit;
using Gitberth.Health;
using Gitberth.Notifications;
using Gitberth.Security;
using Gitberth.Services;

namespace Gitberth.Backup;

/// <summary>Automatic backups as their card shows them: the settings, the runs, the next one, and the one running now.</summary>
/// <param name="Running">The backup being made now (its progress), null between them.</param>
/// <param name="Waiting">Why a backup that's due waits (a restore runs), null when none does.</param>
public sealed record AutoBackupStatus(AutoBackupConfig Config, AutoBackupState State, DateTime? Next, SaveProgress? Running, string? Waiting);

/// <summary>Test the destination's answer: each step as it went, and the first that failed.</summary>
public sealed record DestinationTest(bool Ok, IReadOnlyList<string> Steps, string? Problem);

/// <summary>
/// Automatic backups (Settings › Save and restore): on the schedule (or Back up now), the items chosen once are saved like
/// Save a backup, sealed to the owner's key pair (format 2: no passphrase needed to make one), and streamed to the
/// destination as they're made; then only the newest N of this host's own files stay there. A run missed while Gitberth was
/// down runs once, 5 minutes after it starts. One at a time, never beside a manual save, never while a restore runs. A
/// failed run is told (BackupFailed, then BackupOk when one works), recorded in the audit log, and kept on the dashboard
/// until one works; so is a schedule with no good backup for twice its interval.
/// </summary>
public sealed class AutoBackups(BackupService backup, RestoreService restore, RestoreGate gate, SettingsService settings, SecretProtector protector,
    AutoDestinations destinations, IBackupEngine engine, Notifier notifier, AttentionBoard board, ILogger<AutoBackups> logger,
    AuditLog? audit = null, TimeProvider? time = null) : BackgroundService
{
    /// <summary>The settings, and the runs' record.</summary>
    public const string ConfigKey = "backup.auto";
    public const string StateKey = "backup.auto.state";

    /// <summary>Its item on the attention board, and the source that keeps it.</summary>
    public const string ItemKey = "backup-auto";
    public const string Source = "backup";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private AutoBackupConfig? _config;
    private AutoBackupState? _state;
    private DateTime? _missedAt;
    private string? _waiting;
    private volatile bool _running;

    /// <summary>How often the schedule is looked at.</summary>
    internal TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a scheduled backup waits for a save being made (a download) to end.</summary>
    internal TimeSpan WaitForSave { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The schedule's first look after Gitberth starts.</summary>
    internal TimeSpan FirstLook { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Raised when the settings, a run or what's being fetched changed, for open pages.</summary>
    public event Action? Changed;

    /// <summary>Whether an automatic backup is being made now.</summary>
    public bool Running => _running;

    /// <summary>A backup being fetched from the destination into the restore's upload: its name, the bytes so far and its size.</summary>
    public (string Name, long Bytes, long Total)? Fetching { get; private set; }

    private TimeZoneInfo Zone => _time.LocalTimeZone;

    // ---- the settings ----

    public async Task<AutoBackupStatus> StatusAsync(CancellationToken ct = default)
    {
        var config = await ConfigAsync(ct);
        var state = await StateAsync(ct);
        return new AutoBackupStatus(config, state, NextRun(config, state), backup.AutoJob?.Snapshot(), _waiting);
    }

    public async Task<AutoBackupConfig> ConfigAsync(CancellationToken ct = default) =>
        _config ??= AutoBackupConfig.Parse(await settings.GetAsync(ConfigKey, ct));

    private async Task<AutoBackupState> StateAsync(CancellationToken ct) =>
        _state ??= AutoBackupState.Parse(await settings.GetAsync(StateKey, ct));

    private DateTime? NextRun(AutoBackupConfig config, AutoBackupState state) =>
        config.Schedule == AutoSchedule.Off ? null : _missedAt ?? AutoBackupRules.Next(config, state.LastRun, Zone);

    /// <summary>
    /// The settings saved: the schedule, what's saved, where and how many to keep. A password or secret key given replaces
    /// the saved one; null keeps it. The key pair is <see cref="SetPassphraseAsync"/>'s. Throws with the reason when they
    /// can't be used.
    /// </summary>
    public async Task SaveAsync(AutoBackupConfig next, string? smbPassword, string? s3Secret, CancellationToken ct = default, string? ftpPassword = null)
    {
        var current = await ConfigAsync(ct);
        var merged = Merge(current, next, smbPassword, s3Secret, ftpPassword);
        if (AutoBackupRules.ScheduleProblem(merged) is { } schedule) throw new ArgumentException(schedule);
        if (next.Schedule != AutoSchedule.Off || HasDestination(next))
        {
            if (AutoBackupRules.DestinationProblem(merged, HasSecret(merged)) is { } where) throw new ArgumentException(where);
            if (merged.Where == AutoDestinationKind.Smb && AutoBackupRules.SmbPasswordProblem(smbPassword) is { } password) throw new ArgumentException(password);
            if (merged.Where == AutoDestinationKind.Ftp && AutoBackupRules.FtpPasswordProblem(ftpPassword) is { } ftp) throw new ArgumentException(ftp);
        }
        // A schedule changed counts from now: its first run is the first one due after this.
        var timing = current.Schedule != merged.Schedule || current.At != merged.At || current.Day != merged.Day || current.EveryHours != merged.EveryHours;
        merged = merged with { SetAt = timing || current.SetAt == default ? Now : current.SetAt };
        await WriteConfigAsync(merged, ct);
        if (timing) _missedAt = null;
        audit?.Record(new AuditEntry("settings.backup.auto.changed", Describe(merged))
        {
            Target = "automatic backups",
            Changes = Changes(current, merged),
        });
        Changed?.Invoke();
    }

    /// <summary>
    /// A new key pair for the passphrase (typed twice): new files are sealed to it; older ones still open with the
    /// passphrase they were made with. Gitberth keeps the public key and the private key sealed with the passphrase, never
    /// the passphrase.
    /// </summary>
    public async Task SetPassphraseAsync(string passphrase, string again, CancellationToken ct = default)
    {
        if (AutoBackupRules.PassphraseProblem(passphrase, again) is { } problem) throw new ArgumentException(problem);
        var pair = await Task.Run(() => SealedKeys.Make(passphrase, Iterations, Now), ct);
        var current = await ConfigAsync(ct);
        await WriteConfigAsync(current with { Key = pair }, ct);
        audit?.Record(new AuditEntry("settings.backup.auto.passphrase",
            current.Key is null ? $"Set the passphrase for automatic backups (key {pair.ShortId})." : $"Changed the passphrase for automatic backups (key {pair.ShortId}; older files keep theirs).")
        {
            Target = "automatic backups",
            Changes = [new("Key", current.Key?.ShortId, pair.ShortId)],
        });
        Changed?.Invoke();
    }

    /// <summary>PBKDF2's iterations for a new key pair (fewer in tests).</summary>
    internal int Iterations { get; init; } = BackupFormat.DefaultIterations;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // The form's settings over what's saved: the key pair stays; a secret given replaces the saved one, else it's kept.
    private AutoBackupConfig Merge(AutoBackupConfig current, AutoBackupConfig next, string? smbPassword, string? s3Secret, string? ftpPassword = null) => next with
    {
        FtpAddress = string.IsNullOrWhiteSpace(next.FtpAddress) ? null : next.FtpAddress.Trim(),
        FtpUser = next.FtpUser?.Trim(),
        FtpPassword = ftpPassword is { Length: > 0 } f ? protector.Protect(f) : current.FtpPassword,
        Key = current.Key,
        SetAt = current.SetAt,
        Folder = next.Folder?.Trim(),
        SmbShare = next.SmbShare?.Trim(),
        SmbUser = next.SmbUser?.Trim(),
        SmbDomain = string.IsNullOrWhiteSpace(next.SmbDomain) ? null : next.SmbDomain.Trim(),
        SmbPassword = smbPassword is { Length: > 0 } p ? protector.Protect(p) : current.SmbPassword,
        S3Endpoint = string.IsNullOrWhiteSpace(next.S3Endpoint) ? null : next.S3Endpoint.Trim(),
        S3Region = next.S3Region?.Trim(),
        S3Bucket = next.S3Bucket?.Trim(),
        S3Prefix = next.S3Prefix?.Trim().Trim('/'),
        S3AccessKey = next.S3AccessKey?.Trim(),
        S3Secret = s3Secret is { Length: > 0 } s ? protector.Protect(s) : current.S3Secret,
        Excluded = [.. next.Excluded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        Added = [.. next.Added.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
    };

    private static bool HasDestination(AutoBackupConfig c) => c.Where switch
    {
        AutoDestinationKind.Folder => !string.IsNullOrWhiteSpace(c.Folder),
        AutoDestinationKind.Smb => !string.IsNullOrWhiteSpace(c.SmbShare),
        AutoDestinationKind.Ftp => !string.IsNullOrWhiteSpace(c.FtpAddress),
        _ => !string.IsNullOrWhiteSpace(c.S3Bucket),
    };

    private static bool HasSecret(AutoBackupConfig c) => c.Where switch
    {
        AutoDestinationKind.Smb => !string.IsNullOrEmpty(c.SmbPassword),
        AutoDestinationKind.S3 => !string.IsNullOrEmpty(c.S3Secret),
        AutoDestinationKind.Ftp => !string.IsNullOrEmpty(c.FtpPassword),
        _ => true,
    };

    private async Task WriteConfigAsync(AutoBackupConfig config, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await settings.SetAsync(ConfigKey, config.ToJson(), ct);
            _config = config;
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task WriteStateAsync(AutoBackupState state, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await settings.SetAsync(StateKey, state.ToJson(), ct);
            _state = state;
        }
        finally
        {
            _write.Release();
        }
    }

    // ---- the schedule ----

    // Gitberth stopping: a Back up now in the background stops with it.
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var started = Now;
        try
        {
            await Task.Delay(FirstLook, _time, stoppingToken);
            await StartedAsync(started, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await LookAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The automatic backups' look at the schedule failed");
                }
                await Task.Delay(Tick, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>At start: a backup that was due while Gitberth was down runs once, <see cref="AutoBackupRules.MissedAfter"/> after <paramref name="started"/>.</summary>
    internal async Task StartedAsync(DateTime started, CancellationToken ct)
    {
        var config = await ConfigAsync(ct);
        var state = await StateAsync(ct);
        if (config.Schedule == AutoSchedule.Off || AutoBackupRules.Next(config, state.LastRun, Zone) is not { } due || due >= started) return;
        _missedAt = started + AutoBackupRules.MissedAfter;
        logger.LogInformation("An automatic backup was due at {Due} while Gitberth was down: it runs at {At}", due, _missedAt);
    }

    /// <summary>One look: the dashboard's item, then the backup that's due, when nothing holds it.</summary>
    internal async Task LookAsync(CancellationToken ct)
    {
        var config = await ConfigAsync(ct);
        var state = await StateAsync(ct);
        Show(config, state);
        if (NextRun(config, state) is not { } due || Now < due || _running) return;
        string? waiting = gate.Holding || restore.CantRestore() is not null ? "A restore is running: the backup that's due waits for it to end." : null;
        if (waiting != _waiting)
        {
            _waiting = waiting;
            Changed?.Invoke();
        }
        if (waiting is not null) return;
        _missedAt = null;
        using var actor = AuditScope.Begin(AuditActor.System(AuditVia.Schedule));
        await RunAsync(manual: false, ct);
    }

    /// <summary>
    /// Back up now, in the background: null once it has started, else why it can't (one is being made, a save is being
    /// downloaded, a restore runs, or the settings aren't complete).
    /// </summary>
    public async Task<string?> BackUpNowAsync(CancellationToken ct = default)
    {
        if (_running) return "An automatic backup is being made now.";
        if (backup.Saving) return "A backup is being saved to your browser: wait for it to end.";
        if (gate.Holding || restore.CantRestore() is not null) return "A restore is running: back up once it has ended.";
        var config = await ConfigAsync(ct);
        if (config.Key is null) return "Set a passphrase for automatic backups first.";
        if (AutoBackupRules.DestinationProblem(config, HasSecret(config)) is { } where) return where;
        var actor = AuditScope.Current;
        _ = Task.Run(async () =>
        {
            using var scope = AuditScope.Begin(actor);
            try
            {
                await RunAsync(manual: true, _stopping);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Back up now failed");
            }
        }, CancellationToken.None);
        return null;
    }

    // ---- one backup ----

    /// <summary>One automatic backup to the destination, then keep-N; how it ended is recorded, told and shown.</summary>
    internal async Task<AutoBackupRun> RunAsync(bool manual, CancellationToken ct)
    {
        await _run.WaitAsync(ct);
        try
        {
            _running = true;
            Changed?.Invoke();
            var run = await MakeAsync(manual, ct);
            return run;
        }
        finally
        {
            _running = false;
            _run.Release();
            Changed?.Invoke();
        }
    }

    private async Task<AutoBackupRun> MakeAsync(bool manual, CancellationToken ct)
    {
        var started = Now;
        var config = await ConfigAsync(ct);
        string? file = null, problem = null, pruneProblem = null;
        long bytes = 0;
        var where = AutoDestinations.Describe(config);
        try
        {
            if (config.Key is not { } pair) throw new DestinationException("No passphrase is set for automatic backups, so there's no key to encrypt them with.");
            if (AutoBackupRules.DestinationProblem(config, HasSecret(config)) is { } bad) throw new DestinationException(bad);
            var host = AutoBackupRules.HostWord(BackupInventory.Str(await engine.InfoAsync(ct), "Name"));
            file = AutoBackupRules.FileName(host, started);
            await using var destination = await destinations.OpenAsync(config, ct);
            where = destination.Where;
            var key = BackupKey.ForNewFile(pair);
            var made = await StreamAsync(destination, file, key, config, manual, ct);
            bytes = made;
            try
            {
                var files = await destination.ListAsync(headers: false, ct);
                foreach (var old in AutoBackupRules.ToPrune(files.Select(f => f.Name), host, config.Keep).Where(n => n != file))
                    await destination.DeleteAsync(old, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                pruneProblem = Reason(ex);
                logger.LogWarning(ex, "The oldest automatic backups couldn't be removed from {Where}", where);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem = Reason(ex);
            if (ex is not (DestinationException or BackupSaveException or IOException)) logger.LogWarning(ex, "An automatic backup failed");
        }
        catch (OperationCanceledException)
        {
            problem = "Gitberth stopped in the middle of it.";
        }

        var run = new AutoBackupRun(started, manual, problem is null, bytes, Math.Round((Now - started).TotalSeconds, 1), file, problem, config.Key?.ShortId,
            manual ? AuditActors.Describe(AuditScope.Current) : null);
        await EndedAsync(config, run, where, pruneProblem);
        return run;
    }

    // The save written into a pipe as it's made, and the destination reading it at the other end: nothing is staged. A
    // failure at either end stops the other; the destination keeps nothing of a backup that didn't end whole.
    private async Task<long> StreamAsync(IBackupDestination destination, string file, BackupKey key, AutoBackupConfig config, bool manual, CancellationToken ct)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8 << 20, resumeWriterThreshold: 4 << 20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            var output = pipe.Writer.AsStream(leaveOpen: true);
            var progress = await backup.SaveToAsync(items => AutoBackupRules.Choice(items, config), key, file, output, manual ? TimeSpan.Zero : WaitForSave, stop.Token);
            await pipe.Writer.CompleteAsync(progress.Problem is { } why ? new IOException(why) : null);
            return progress;
        }, CancellationToken.None);
        Exception? failed = null;
        try
        {
            await destination.WriteAsync(file, pipe.Reader.AsStream(leaveOpen: true), stop.Token);
        }
        catch (Exception ex)
        {
            failed = ex;
            await stop.CancelAsync();
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
        }
        var made = await producer;
        if (failed is not null) throw failed is OperationCanceledException && made.Problem is { } why ? new BackupSaveException(why) : failed;
        if (made.Problem is { } problem) throw new BackupSaveException(problem);
        return made.BytesWritten;
    }

    // What the owner reads about a failure: the sentence a destination or the save gave, else the exception's first line.
    private static string Reason(Exception ex) => ex switch
    {
        DestinationException or BackupSaveException or IOException or InvalidOperationException or ArgumentException => Sentence(ex.Message),
        TimeoutException => "It took too long: " + Sentence(ex.Message),
        HttpRequestException => "The destination couldn't be reached: " + Sentence(ex.Message),
        _ => "It failed: " + Sentence(ex.Message),
    };

    private static string Sentence(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "it said nothing";
        if (line.Length > 400) line = line[..400] + "…";
        return line.EndsWith('.') ? line : line + ".";
    }

    // A run's end: its record, what's told (a failure once per streak or reason, a day at most; working again once), the
    // audit log, and the dashboard.
    private async Task EndedAsync(AutoBackupConfig config, AutoBackupRun run, string where, string? pruneProblem)
    {
        var state = await StateAsync(CancellationToken.None);
        state.Add(run);
        var next = NextRun(config, state);
        var nextWords = next is { } at ? "at " + Local(at) : "when you click Back up now";
        if (!run.Ok)
        {
            var tell = state.ToldProblem != run.Problem || state.ToldAt is not { } told || Now - told > TimeSpan.FromHours(24);
            if (tell)
            {
                state.ToldProblem = run.Problem;
                state.ToldAt = Now;
                notifier.Publish(new Notice(NotifyEvent.BackupFailed, NoticeLevel.Critical, "An automatic backup failed",
                    $"{run.Problem} Gitberth tries again {nextWords}.", await notifier.LinkAsync("settings#auto-backup"), "", null, Now));
            }
        }
        else if (state.ToldProblem is not null)
        {
            state.ToldProblem = null;
            state.ToldAt = null;
            notifier.Publish(new Notice(NotifyEvent.BackupOk, NoticeLevel.Good, "Automatic backups work again",
                $"{run.File} was saved to {where} ({Disk.DiskText.Size(run.Bytes)}).", await notifier.LinkAsync("settings#auto-backup"), "", null, Now));
        }
        try
        {
            await WriteStateAsync(state, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The automatic backups' record couldn't be saved");
        }
        audit?.Record(new AuditEntry(run.Ok ? "backup.auto.saved" : "backup.auto.failed",
            run.Ok ? $"Saved the automatic backup {run.File} to {where}." : $"An automatic backup to {where} failed: {AuditLog.Clean(run.Problem, 300)?.TrimEnd('.')}.")
        {
            Outcome = run.Ok ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            Target = run.File ?? "automatic backups",
            Changes =
            [
                new("Started", null, run.Manual ? "Back up now" : "on the schedule"),
                new("Bytes", null, run.Bytes.ToString(CultureInfo.InvariantCulture)),
                new("Seconds", null, run.Seconds.ToString("0.#", CultureInfo.InvariantCulture)),
                .. pruneProblem is null ? Array.Empty<AuditChange>() : [new AuditChange("Old files", null, "not removed: " + pruneProblem)],
            ],
        });
        Show(config, state);
        Changed?.Invoke();
    }

    private string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    // "Automatic backups are failing" while the last one failed, or none worked for twice the schedule's interval.
    private void Show(AutoBackupConfig config, AutoBackupState state)
    {
        AttentionItem? item = null;
        var last = state.Runs.FirstOrDefault();
        var overdue = AutoBackupRules.Overdue(config, state.LastOk, Now);
        if (config.Schedule != AutoSchedule.Off && (last is { Ok: false } || overdue))
        {
            var text = last is { Ok: false, Problem: { } why }
                ? $"The last one, at {Local(last.At)}, failed: {why} Gitberth tries again at the next scheduled time."
                : state.LastOk is { } ok
                    ? $"None has worked since {Local(ok)}, twice the schedule's interval ago. Look at the last runs in Settings › Save and restore."
                    : "None has worked since the schedule was set, twice its interval ago. Look at the last runs in Settings › Save and restore.";
            item = new AttentionItem(ItemKey, Source, AttentionLevel.Warn, "Automatic backups are failing", text, "settings#auto-backup",
                Tip: "dashboard.backups-failing", Actions: [new AttentionAction("Open automatic backups", "settings#auto-backup")])
            {
                Pill = "Backups failing",
                Problem = "Automatic backups are failing: " + text,
            };
        }
        board.Replace(Source, item is null ? [] : [item]);
    }

    // ---- the destination ----

    /// <summary>
    /// Test the destination, as the form has it (not saved): a small file written, listed, read back and removed. Each step
    /// says how it went; the first that fails says why.
    /// </summary>
    public async Task<DestinationTest> TestAsync(AutoBackupConfig draft, string? smbPassword, string? s3Secret, CancellationToken ct = default,
        string? ftpPassword = null)
    {
        var config = Merge(await ConfigAsync(ct), draft, smbPassword, s3Secret, ftpPassword);
        var steps = new List<string>();
        string? problem = null;
        if (AutoBackupRules.DestinationProblem(config, HasSecret(config)) is { } bad) return new DestinationTest(false, steps, bad);
        var name = $"gitberth-test-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.txt";
        var content = Encoding.UTF8.GetBytes($"Gitberth can write here ({Now:O}).\n{Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))}\n");
        try
        {
            await using var destination = await destinations.OpenAsync(config, ct);
            await destination.WriteAsync(name, new MemoryStream(content), ct);
            steps.Add($"Wrote {name}.");
            var listed = await destination.ListAsync(headers: false, ct);
            if (listed.All(f => f.Name != name)) throw new DestinationException($"{name} was written, but the listing of {destination.Where} doesn't show it.");
            steps.Add("Listed it.");
            var back = new MemoryStream();
            await destination.ReadAsync(name, back, ct);
            if (!back.ToArray().AsSpan().SequenceEqual(content)) throw new DestinationException($"{name} read back isn't what was written.");
            steps.Add("Read it back.");
            await destination.DeleteAsync(name, ct);
            steps.Add("Removed it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem = Reason(ex);
        }
        audit?.Record(new AuditEntry("settings.backup.auto.tested", problem is null
            ? $"Tested the destination of automatic backups, {AutoDestinations.Describe(config)}: it works."
            : $"Tested the destination of automatic backups, {AutoDestinations.Describe(config)}: {AuditLog.Clean(problem, 300)?.TrimEnd('.')}.")
        {
            Target = "automatic backups",
            Outcome = problem is null ? AuditOutcome.Succeeded : AuditOutcome.Failed,
        });
        return new DestinationTest(problem is null, steps, problem);
    }

    /// <summary>The automatic backups at the destination (every host's), newest first, each with its key's short ID.</summary>
    public async Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken ct = default)
    {
        var config = await ConfigAsync(ct);
        await using var destination = await destinations.OpenAsync(config, ct);
        var files = await destination.ListAsync(headers: true, ct);
        return [.. files.Select(f => (File: f, Parsed: AutoBackupRules.Parse(f.Name))).Where(x => x.Parsed is not null)
            .Select(x => new RemoteBackup(x.File.Name, x.File.Bytes, x.File.Modified, KeyIdOf(x.File.Header), x.Parsed!.Value.Host, x.Parsed.Value.SavedAt))
            .OrderByDescending(r => r.SavedAt)];
    }

    // The key pair's short ID from a format 2 header's first bytes.
    internal static string? KeyIdOf(byte[]? header)
    {
        if (header is null || header.Length < BackupFormat.SealedFixedBytes || !header.AsSpan(0, 8).SequenceEqual(BackupFormat.Magic)) return null;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)) != BackupFormat.Sealed) return null;
        return SealedKeys.ShortId(header.AsSpan(44, BackupFormat.KeyIdBytes));
    }

    /// <summary>
    /// Use this one: the backup fetched from the destination into the restore's upload, as a file uploaded from the
    /// browser would be; then the usual Open the file, pick and restore. Returns its size.
    /// </summary>
    public async Task<long> FetchAsync(string name, long size, CancellationToken ct = default)
    {
        if (restore.CantRestore() is { } why) throw new InvalidOperationException(why);
        if (AutoBackupRules.Parse(name) is null) throw new InvalidOperationException($"{name} isn't an automatic backup.");
        var config = await ConfigAsync(ct);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8 << 20, resumeWriterThreshold: 4 << 20));
        Fetching = (name, 0, size);
        Changed?.Invoke();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var destination = await destinations.OpenAsync(config, ct);
            var reading = Task.Run(async () =>
            {
                try
                {
                    await destination.ReadAsync(name, pipe.Writer.AsStream(leaveOpen: true), stop.Token);
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                }
            }, CancellationToken.None);
            var counted = new EndStream(pipe.Reader.AsStream(leaveOpen: true));
            using var progress = new Timer(_ =>
            {
                Fetching = (name, counted.BytesRead, size);
                Changed?.Invoke();
            }, null, 1000, 1000);
            long bytes;
            try
            {
                bytes = await restore.ReceiveAsync(counted, stop.Token);
            }
            catch
            {
                await stop.CancelAsync();
                throw;
            }
            finally
            {
                await pipe.Reader.CompleteAsync();
                await reading;
            }
            audit?.Record(new AuditEntry("backup.auto.fetched", $"Fetched the automatic backup {name} from {destination.Where} to restore from it.")
            {
                Target = name,
                Changes = [new("Bytes", null, bytes.ToString(CultureInfo.InvariantCulture))],
            });
            return bytes;
        }
        catch (Exception ex) when (ex is DestinationException or IOException or S3Exception or HttpRequestException)
        {
            throw new InvalidOperationException(Reason(ex), ex);
        }
        finally
        {
            Fetching = null;
            Changed?.Invoke();
        }
    }

    // ---- the audit log's words ----

    private static string Describe(AutoBackupConfig c) => c.Schedule == AutoSchedule.Off
        ? "Automatic backups are off."
        : $"Automatic backups run {AutoBackupText.Schedule(c)}, to {AutoDestinations.Describe(c)}, keeping the last {c.Keep.ToString(CultureInfo.InvariantCulture)}.";

    private static List<AuditChange> Changes(AutoBackupConfig a, AutoBackupConfig b)
    {
        var changes = new List<AuditChange>();
        void Add(string field, string? from, string? to)
        {
            if (from != to) changes.Add(new AuditChange(field, from, to));
        }
        Add("Schedule", AutoBackupText.Schedule(a), AutoBackupText.Schedule(b));
        Add("Where", AutoDestinations.Describe(a), AutoDestinations.Describe(b));
        Add("Keep", a.Keep.ToString(CultureInfo.InvariantCulture), b.Keep.ToString(CultureInfo.InvariantCulture));
        Add("Left out", a.Excluded.Count.ToString(CultureInfo.InvariantCulture), b.Excluded.Count.ToString(CultureInfo.InvariantCulture));
        Add("Stop everything", a.StopEverything ? "yes" : "no", b.StopEverything ? "yes" : "no");
        if (a.SmbPassword != b.SmbPassword) changes.Add(new AuditChange("SMB password", null, "••••"));
        if (a.S3Secret != b.S3Secret) changes.Add(new AuditChange("S3 secret key", null, "••••"));
        if (a.FtpPassword != b.FtpPassword) changes.Add(new AuditChange("FTP password", null, "••••"));
        return changes;
    }
}

/// <summary>Automatic backups' words, for the card and the audit log. Pure, so each is tested.</summary>
public static class AutoBackupText
{
    /// <summary>"every day at 03:00", "every Sunday at 03:00", "every 6 hours", "off".</summary>
    public static string Schedule(AutoBackupConfig c) => c.Schedule switch
    {
        AutoSchedule.Daily => $"every day at {c.At}",
        AutoSchedule.Weekly => $"every {c.Day} at {c.At}",
        AutoSchedule.Hours => c.EveryHours == 1 ? "every hour" : $"every {c.EveryHours.ToString(CultureInfo.InvariantCulture)} hours",
        _ => "off",
    };
}
