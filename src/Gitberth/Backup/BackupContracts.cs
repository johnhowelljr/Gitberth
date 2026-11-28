namespace Gitberth.Backup;

// Save and restore (Settings › Save and restore): one compressed, encrypted file with everything this host runs, brought
// back onto a fresh install. This file is the contract the backend (BackupService, RestoreService, the file format, the
// endpoints) and the UI share. The UI reaches the backend only through IBackupService and IRestoreService.

/// <summary>What one row of the save list is.</summary>
public enum BackupItemKind
{
    /// <summary>Gitberth's own data: its database, keys, Health, uptime and audit history, Compose releases. Always saved.</summary>
    GitberthData,

    /// <summary>An app (any kind): its settings come with Gitberth's data; its children are its volumes and databases.</summary>
    App,

    /// <summary>A container that isn't an app (cloudflared, a database run by hand, an app's Redis): saved as Docker describes it.</summary>
    Container,

    /// <summary>A named or anonymous volume: its files, copied while what uses it is paused for that moment.</summary>
    Volume,

    /// <summary>A Postgres or MySQL/MariaDB server: dumped with its own tools while it keeps serving, instead of its volume's files.</summary>
    Database,

    /// <summary>An image that's only on this host (no registry has it): saved whole so its apps can come back.</summary>
    Image,

    /// <summary>
    /// A folder of the host a container bind-mounts (never Docker's socket, the system's folders or Gitberth's own data):
    /// its files, copied through a helper that mounts it read-only while what uses it is paused for that moment.
    /// </summary>
    HostFolder,
}

/// <summary>
/// One row of the save list (Settings › Save and restore), or of what a backup holds.
/// </summary>
/// <param name="Id">Stable: "gitberth", "app:&lt;id&gt;", "container:&lt;name&gt;", "volume:&lt;name&gt;", "database:&lt;container&gt;", "image:&lt;id&gt;".</param>
/// <param name="Title">As the owner knows it: the app's name, the container's or volume's name, the image reference.</param>
/// <param name="Detail">One short line: "Git app · github.com/you/site", "Postgres 18 · 3 databases", "used by website".</param>
/// <param name="Bytes">Its size on this host when known (volumes are measured by Docker, slowly on big ones); null while unknown.</param>
/// <param name="Required">Can't be left out (Gitberth's own data).</param>
/// <param name="Included">Ticked by default (previews, caches and Gitberth's helpers never are, and aren't listed).</param>
/// <param name="Parent">The item it belongs to ("app:7" for the app's volume), or null for a top-level row.</param>
public sealed record BackupItem(string Id, BackupItemKind Kind, string Title, string? Detail, long? Bytes, bool Required, bool Included, string? Parent);

/// <summary>What the owner chose to save.</summary>
/// <param name="Included">The ids of the items to save; required items are saved whatever it says.</param>
/// <param name="StopEverything">Stop every running container (Gitberth's own aside) for the whole save, then start exactly those again.</param>
public sealed record BackupChoice(IReadOnlySet<string> Included, bool StopEverything);

/// <summary>A save ready to stream: the browser downloads it once, from GET /backup/download/{Id}, before it expires.</summary>
public sealed record SaveTicket(string Id, string FileName, DateTime ExpiresAt);

/// <summary>Where a save stands, for the page's progress line.</summary>
/// <param name="Current">The item being saved now ("volume gb-website-data"), null between items.</param>
/// <param name="Problem">One sentence when it failed (never a secret); the download then ends early and the file is unusable.</param>
public sealed record SaveProgress(string Phase, string? Current, long BytesWritten, int ItemsDone, int ItemsTotal, bool Done, string? Problem);

/// <summary>Something the owner should know before a restore runs.</summary>
public enum RestoreWarningKind
{
    /// <summary>The backup brings back cloudflared: if the old machine still runs it, both share the tunnel's visitors.</summary>
    TunnelElsewhere,

    /// <summary>A host port the backup's apps use is taken on this host.</summary>
    PortTaken,

    /// <summary>A container with a saved container's name already exists here: it's left as it is, and the saved one isn't restored.</summary>
    NameTaken,

    /// <summary>A volume with a saved volume's name already holds data here: it's kept, not overwritten.</summary>
    VolumeHasData,

    /// <summary>The backup was made on another CPU architecture: images that only the old host had may not run.</summary>
    ArchitectureDiffers,

    /// <summary>This host's Docker is older than the one the backup was made on, or below what Gitberth needs.</summary>
    DockerTooOld,

    /// <summary>A bind-mounted host folder the backup's containers use doesn't exist here: it's created empty.</summary>
    BindPathMissing,

    /// <summary>There isn't enough free disk space for what the backup holds.</summary>
    NeedsSpace,
}

/// <summary>One warning before a restore. Blocks: the restore can't start while it stands.</summary>
public sealed record RestoreWarning(RestoreWarningKind Kind, string Message, bool Blocks);

/// <summary>What a backup file is: read from its encrypted manifest, before anything is restored.</summary>
/// <param name="Items">What it holds, as the save list showed it (with sizes as saved).</param>
public sealed record BackupSummary(string GitberthVersion, DateTime SavedAt, string Host, string Architecture, string DockerVersion,
    IReadOnlyList<BackupItem> Items, IReadOnlyList<RestoreWarning> Warnings);

/// <summary>Why an uploaded file can't be restored.</summary>
public enum UploadProblem
{
    None,

    /// <summary>Not a Gitberth backup (or no file was uploaded).</summary>
    NotABackup,

    /// <summary>The passphrase doesn't open it.</summary>
    WrongPassphrase,

    /// <summary>It opens, but a byte was changed, removed or moved: nothing of it is trusted.</summary>
    Damaged,

    /// <summary>Made by a newer Gitberth than this one: update Gitberth first.</summary>
    TooNew,

    /// <summary>A restore can't start here now (one is running): the message says why.</summary>
    NotEmpty,
}

/// <summary>
/// The uploaded file checked with a passphrase: its summary, or why not; and how each of its apps and containers stands
/// against this Gitberth, for picking what comes back.
/// </summary>
/// <param name="Targets">One per top-level app or container of the backup, in the summary's order.</param>
/// <param name="Everything">The whole backup can be restored here (a Gitberth with no apps): its settings and data too.</param>
public sealed record UploadCheck(UploadProblem Problem, string? Message, BackupSummary? Summary,
    IReadOnlyList<RestoreTarget>? Targets = null, bool Everything = false);

/// <summary>What happens to one picked app or container of a backup in this Gitberth.</summary>
public enum RestoreAction
{
    /// <summary>It isn't here (deleted, or saved on another machine): it comes back as it was, under its own name.</summary>
    Restore,

    /// <summary>It's here: its saved settings and data replace what it has now. Its current database is kept under another name.</summary>
    Replace,

    /// <summary>It comes back next to what's here, under a new name: its own container and volumes, free ports, a copy of its database, no domains.</summary>
    Copy,
}

/// <summary>One app or container the owner picked to restore into this Gitberth, and how.</summary>
/// <param name="ItemId">A top-level item of the backup's summary: "app:&lt;id&gt;" or "container:&lt;name&gt;".</param>
/// <param name="NewName">For Copy, the copy's name ("website2"); null otherwise.</param>
public sealed record RestorePick(string ItemId, RestoreAction Action, string? NewName = null);

/// <summary>How one top-level app or container of a backup stands against this Gitberth, before the owner picks.</summary>
/// <param name="Here">An app or container with its name is here now: it can be replaced or copied, not restored as itself.</param>
/// <param name="CopyName">A name free here for a copy ("website2").</param>
/// <param name="Note">One sentence about it here ("Port 3000 is taken here: it gets a free one."), or null.</param>
public sealed record RestoreTarget(string ItemId, bool Here, string CopyName, string? Note);

/// <summary>Where one step of a restore stands.</summary>
public enum RestoreStepState { Waiting, Running, Done, Skipped, Failed }

/// <summary>One step of a restore, as the Restoring page lists it ("Volume gb-website-data", "Deploy website at 3f9c2ab").</summary>
/// <param name="Note">One sentence: why it was skipped or failed, or what it did.</param>
public sealed record RestoreStepView(string Id, string Title, RestoreStepState State, string? Note);

/// <summary>
/// A restore as it runs and after: its steps, and its log (a file under /data that survives Gitberth's restart in the middle).
/// </summary>
/// <param name="Phase">1 while the file is read (volumes, databases, images, containers), 2 after the restart (Git and Compose apps, domains).</param>
public sealed record RestoreStatus(bool Active, bool Finished, int Phase, IReadOnlyList<RestoreStepView> Steps, int Failed, DateTime StartedAt);

/// <summary>Saving: what can be saved, and a save streamed to the browser.</summary>
public interface IBackupService
{
    /// <summary>Every item that can be saved, top-level rows first, each followed by its children. Sizes may be null at first.</summary>
    Task<IReadOnlyList<BackupItem>> ItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Prepares a save of the chosen items with the passphrase (12 characters at least; kept in memory until the download ends
    /// or the ticket expires, never written anywhere). The bytes are made while the browser downloads them.
    /// </summary>
    /// <exception cref="ArgumentException">The passphrase is too short, or nothing is chosen.</exception>
    Task<SaveTicket> StartSaveAsync(BackupChoice choice, string passphrase, CancellationToken ct = default);

    /// <summary>The save's progress, while its ticket lives; null for an unknown or expired one.</summary>
    SaveProgress? Progress(string ticketId);
}

/// <summary>
/// Restoring: an uploaded file checked, then what the owner picks restored into this Gitberth (any Gitberth, apps or not);
/// or, on a Gitberth with no apps yet, the whole backup.
/// </summary>
public interface IRestoreService
{
    /// <summary>Null when a restore can start here (picked items, into any Gitberth); else why not (a restore is running).</summary>
    string? CantRestore();

    /// <summary>Null when the whole backup can be restored here (a Gitberth with no apps yet); else why not.</summary>
    string? CantRestoreEverything();

    /// <summary>Whether a file was uploaded (POST /backup/upload) and not yet restored or discarded, and its size.</summary>
    long? UploadedBytes();

    /// <summary>Opens the uploaded file with the passphrase and reads its manifest, with this host's warnings. Restores nothing.</summary>
    Task<UploadCheck> CheckAsync(string passphrase, CancellationToken ct = default);

    /// <summary>
    /// Starts restoring the whole backup (a Gitberth with no apps only): phase 1 runs now (the passphrase stays in memory only
    /// for it), then Gitberth restarts on the saved data and phase 2 runs by itself. Returns once phase 1 has started.
    /// </summary>
    /// <exception cref="InvalidOperationException">CantRestoreEverything says why, or the check didn't pass.</exception>
    Task StartAsync(string passphrase, CancellationToken ct = default);

    /// <summary>
    /// Starts restoring the picked apps and containers into this Gitberth, each as its pick says, without restarting Gitberth
    /// and without touching anything else here (other apps, settings, tokens, history). Status and LogLines follow it. Returns
    /// once it has started.
    /// </summary>
    /// <exception cref="InvalidOperationException">CantRestore says why, the check didn't pass, or a pick doesn't fit (unknown
    /// item, Restore of one that's here, Copy without a free name).</exception>
    Task StartAsync(string passphrase, IReadOnlyList<RestorePick> picks, CancellationToken ct = default);

    /// <summary>The restore running or last finished; null when none was ever started.</summary>
    RestoreStatus? Status();

    /// <summary>The restore log's lines from <paramref name="from"/> on (secrets masked), for the live view.</summary>
    IReadOnlyList<string> LogLines(int from);

    /// <summary>Removes the uploaded file without restoring it.</summary>
    Task DiscardUploadAsync(CancellationToken ct = default);
}
