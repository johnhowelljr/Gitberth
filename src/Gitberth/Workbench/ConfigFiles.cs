using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Workbench;

/// <summary>What the Config files section asks for when it adds or changes one: Content null keeps a secret file's contents.</summary>
public sealed record ConfigFileInput(string Path, string? Content, bool Secret, string Mode, string? Service);

/// <summary>A config file as the section shows it: a secret one's contents never.</summary>
/// <param name="HostPath">Where it is on the Docker host, which the app's container mounts; null when that can't be known.</param>
public sealed record ConfigFileView(int Id, string Path, string? Content, bool Secret, string Mode, string? Service, DateTime UpdatedAt, int Size, string? HostPath);

/// <summary>What a save or a removal did: why it was refused, or what it says now.</summary>
/// <param name="Rewritten">Only its contents changed, rewritten where the container has it: the app sees them at once.</param>
public sealed record ConfigOutcome(bool Ok, string Message, int? Id = null, string? HostPath = null, bool Rewritten = false);

/// <summary>
/// The files Gitberth keeps for an app and mounts into its container (B3), each at a path of its own, read-only: a path,
/// its contents (encrypted in Gitberth's database, like a secret variable), whether it's secret (never shown again once
/// saved), and its mode. Each is written to /data/configs/&lt;app&gt;/&lt;file&gt; and bind-mounted from where that is on the
/// Docker host. Changing a file's contents rewrites it in place, so the container sees it at once; adding or removing one
/// changes the app's mounts: a Git, uploaded or Compose app's at its next deploy, any other app's through the change
/// dialog, as any mount. Backups carry them with Gitberth's data.
/// </summary>
public sealed partial class ConfigFiles(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector, SelfContainer self, GitberthOptions options,
    ILogger<ConfigFiles> logger, Lazy<AppCatalog>? catalog = null, Audit.AuditLog? audit = null)
{
    public const int MaxContent = 256 * 1024;
    public const int MaxPerApp = 50;

    /// <summary>The folder an app's config files are written to.</summary>
    public static string FolderOf(GitberthOptions options, int appId) =>
        Path.Combine(options.DataDir, "configs", appId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Where one config file is written.</summary>
    public static string FileOf(GitberthOptions options, int appId, int id) => Path.Combine(FolderOf(options, appId), id.ToString(CultureInfo.InvariantCulture));

    /// <summary>Where a config file is on the Docker host (what its mount's source is); null outside a container when /data can't be found.</summary>
    public Task<string?> HostPathAsync(int appId, int id, CancellationToken ct = default) => self.HostPathAsync(FileOf(options, appId, id), ct);

    // ---- Checks ----

    /// <summary>Why a path can't be a config file's in a container; null when it can.</summary>
    public static string? PathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Give the path in the container, like /etc/nginx/conf.d/site.conf.";
        if (!path.StartsWith('/')) return "The path starts with /: it's where the file is in the container.";
        if (path.Length > 512) return "Keep the path to 512 characters.";
        if (path.EndsWith('/')) return "The path is a file's, so it doesn't end with /.";
        if (path.Contains('\0') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) return "The path has a character a mount can't take (\\, :, or a control character).";
        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0 || names.Any(n => n is "." or "..")) return "Write the path without . or .. in it.";
        if (names[0] is "proc" or "sys" or "dev") return $"/{names[0]} is the kernel's in a container: a file can't be mounted there.";
        return null;
    }

    /// <summary>The path as it's kept: its names joined by single slashes.</summary>
    public static string CleanPath(string path) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A mode written in octal (644, 0640, 0755) as a number; null when it isn't one, or has more than rwx for owner, group and others.</summary>
    public static int? ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || !OctalMode().IsMatch(mode.Trim())) return null;
        var value = Convert.ToInt32(mode.Trim(), 8);
        return value is >= 0 and <= 511 ? value : null;
    }

    /// <summary>A mode as it's shown: four octal digits (0644).</summary>
    public static string ModeText(int mode) => "0" + Convert.ToString(mode & 511, 8).PadLeft(3, '0');

    [GeneratedRegex("^0?[0-7]{3}$")]
    private static partial Regex OctalMode();

    // ---- Reading ----

    public async Task<IReadOnlyList<ConfigFileView>> ListAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ConfigFiles.AsNoTracking().Where(f => f.AppId == appId).OrderBy(f => f.Path).ToListAsync(ct);
        var list = new List<ConfigFileView>(rows.Count);
        foreach (var f in rows)
        {
            var content = protector.TryUnprotect(f.ContentProtected);
            list.Add(new ConfigFileView(f.Id, f.Path, f.Secret ? null : content, f.Secret, ModeText(f.Mode), f.Service, f.UpdatedAt, Encoding.UTF8.GetByteCount(content),
                await HostPathAsync(appId, f.Id, ct)));
        }
        return list;
    }

    // ---- Changing ----

    /// <summary>
    /// Adds a config file to the app (its key), or changes one (<paramref name="id"/>): written to its file at once, in place
    /// when it's there, so a container that mounts it sees the new contents. A container Gitberth keeps nothing about yet
    /// gets its row first, as any first edit does. Audited, without the contents.
    /// </summary>
    public async Task<ConfigOutcome> SaveAsync(string appKey, int? id, ConfigFileInput input, CancellationToken ct = default)
    {
        if (PathProblem(input.Path) is { } pathProblem) return new ConfigOutcome(false, pathProblem);
        if (ParseMode(input.Mode) is not int mode) return new ConfigOutcome(false, "The mode is three octal digits, like 644 (or 0644).");
        if (input.Content is { } c && Encoding.UTF8.GetByteCount(c) > MaxContent) return new ConfigOutcome(false, "Keep a config file to 256 KiB.");
        if (input.Content is { } nul && nul.Contains('\0')) return new ConfigOutcome(false, "A config file is text: it has no NUL characters.");
        if (input.Service is { Length: > 0 } s && !Compose.ComposeNames.IsServiceName(s)) return new ConfigOutcome(false, $"{s} isn't a service's name.");
        var path = CleanPath(input.Path);

        AppDefinition app;
        try
        {
            app = await AppOfAsync(appKey, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ConfigOutcome(false, ex.Message);
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var service = app.IsCompose && input.Service is { Length: > 0 } svc && svc != app.WebService ? svc : null;
        if (await db.ConfigFiles.AnyAsync(f => f.AppId == app.Id && f.Path == path && f.Id != (id ?? 0), ct))
            return new ConfigOutcome(false, $"{app.Name} has a config file at {path} already: change that one.");
        ConfigFile row;
        string? before = null;
        bool added;
        if (id is int existing)
        {
            row = await db.ConfigFiles.FirstOrDefaultAsync(f => f.Id == existing && f.AppId == app.Id, ct) ?? throw new InvalidOperationException("That config file is gone.");
            before = row.Path;
            added = false;
        }
        else
        {
            if (await db.ConfigFiles.CountAsync(f => f.AppId == app.Id, ct) >= MaxPerApp) return new ConfigOutcome(false, $"An app has at most {MaxPerApp} config files.");
            if (input.Content is null) return new ConfigOutcome(false, "Write the file's contents.");
            row = new ConfigFile { AppId = app.Id };
            db.ConfigFiles.Add(row);
            added = true;
        }
        var oldContent = added ? null : protector.TryUnprotect(row.ContentProtected);
        var content = input.Content ?? oldContent ?? "";
        var moved = !added && (before != path || row.Service != service);
        var changes = new List<Audit.AuditChange>();
        if (added || before != path) changes.Add(new("Path", before, path));
        if (added || row.Mode != mode) changes.Add(new("Mode", added ? null : ModeText(row.Mode), ModeText(mode)));
        if (added || row.Secret != input.Secret) changes.Add(new("Secret", added ? null : row.Secret ? "yes" : "no", input.Secret ? "yes" : "no"));
        if (added || oldContent != content) changes.Add(new("Contents", added ? null : $"{Encoding.UTF8.GetByteCount(oldContent ?? "")} bytes", $"{Encoding.UTF8.GetByteCount(content)} bytes"));
        if (row.Service != service) changes.Add(new("Service", row.Service, service));
        if (changes.Count == 0) return new ConfigOutcome(true, "Nothing changed.", row.Id, await HostPathAsync(app.Id, row.Id, ct));

        row.Path = path;
        row.Mode = mode;
        row.Secret = input.Secret;
        row.Service = service;
        row.ContentProtected = protector.Protect(content);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        try
        {
            await WriteInPlaceAsync(FileOf(options, app.Id, row.Id), Encoding.UTF8.GetBytes(content), mode, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't write {App}'s config file {Path}", app.Slug, path);
            return new ConfigOutcome(false, $"It's saved, but Gitberth couldn't write it to its data folder: {ex.Message.TrimEnd('.')}.", row.Id);
        }
        audit?.Record(new Audit.AuditEntry(added ? "app.config.added" : "app.config.changed",
            added ? $"Added the config file {path} to {app.Name}." : $"Changed {app.Name}'s config file {path}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = path, Changes = changes,
        });
        var host = await HostPathAsync(app.Id, row.Id, ct);
        var rewritten = !added && !moved;
        var message = added
            ? app.Deployed ? $"Added {path}: the app's next deploy mounts it." : $"Added {path}: Review and apply mounts it."
            : moved ? app.Deployed ? $"Saved {path}: the app's next deploy mounts it there." : $"Saved {path}: Review and apply mounts it there."
            : $"Saved {path}: the app sees the new contents at once.";
        return new ConfigOutcome(true, message, row.Id, host, rewritten);
    }

    /// <summary>
    /// Removes a config file. A Git, uploaded or Compose app's goes from its container at its next deploy; its file goes now
    /// (the running container keeps what it had open). Any other app's file stays until no container mounts it.
    /// </summary>
    public async Task<ConfigOutcome> RemoveAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ConfigFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return new ConfigOutcome(true, "It's gone already.");
        var app = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == row.AppId, ct);
        var host = await HostPathAsync(app.Id, row.Id, ct);
        db.ConfigFiles.Remove(row);
        await db.SaveChangesAsync(ct);
        if (app.Deployed) TryDelete(FileOf(options, app.Id, row.Id));
        audit?.Record(new Audit.AuditEntry("app.config.removed", $"Removed the config file {row.Path} from {app.Name}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = row.Path, Changes = [new("Path", row.Path, null)],
        });
        return new ConfigOutcome(true, app.Deployed ? $"Removed {row.Path}: the app's next deploy leaves it out." : $"Removed {row.Path}: Review and apply takes it out of the container.",
            row.Id, host);
    }

    /// <summary>
    /// An image app's files that no row keeps and its container no longer mounts (removed, and the change applied): gone.
    /// <paramref name="mounted"/> are the host paths its container mounts now.
    /// </summary>
    public async Task SweepAsync(int appId, IReadOnlyCollection<string> mounted, CancellationToken ct = default)
    {
        var folder = FolderOf(options, appId);
        if (!Directory.Exists(folder)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var kept = (await db.ConfigFiles.AsNoTracking().Where(f => f.AppId == appId).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!int.TryParse(Path.GetFileName(file), NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) || kept.Contains(fileId)) continue;
            if (await self.HostPathAsync(file, ct) is { } host && mounted.Contains(host)) continue;
            TryDelete(file);
        }
    }

    // ---- For a restore or a copy ----

    /// <summary>
    /// A restored or copied app's config files, made its own here: rows for <paramref name="appId"/> (contents already
    /// encrypted for this Gitberth) and their files written, in place of any it had. Returns the rows.
    /// </summary>
    internal static async Task<List<ConfigFile>> RestoreAsync(GitberthDb db, GitberthOptions options, SecretProtector? protector, int appId,
        IEnumerable<ConfigFile> saved, CancellationToken ct)
    {
        db.ConfigFiles.RemoveRange(db.ConfigFiles.Where(f => f.AppId == appId));
        var rows = saved.Select(f => new ConfigFile
        {
            AppId = appId, Path = f.Path, ContentProtected = f.ContentProtected, Secret = f.Secret, Mode = f.Mode, Service = f.Service,
            CreatedAt = f.CreatedAt, UpdatedAt = f.UpdatedAt,
        }).ToList();
        db.ConfigFiles.AddRange(rows);
        await db.SaveChangesAsync(ct);
        foreach (var f in rows)
            await WriteInPlaceAsync(FileOf(options, appId, f.Id), Encoding.UTF8.GetBytes(protector?.TryUnprotect(f.ContentProtected) ?? ""), f.Mode, ct);
        return rows;
    }

    /// <summary>
    /// A container's mounts with each config file's (a bind mount of …/configs/&lt;app&gt;/&lt;file&gt;) made <paramref name="appId"/>'s own
    /// file at the same path in the container: a restored or copied image app mounts its own files, not the saved app's.
    /// </summary>
    internal static IReadOnlyList<MountSetting> Remap(IReadOnlyList<MountSetting> mounts, int appId, IReadOnlyCollection<ConfigFile> rows) =>
        mounts.Select(m => m.Kind == MountKind.Bind && ConfigSource().Match(m.Source) is { Success: true } hit
                && rows.FirstOrDefault(f => f.Path == m.Target) is { } row
            ? m with { Source = $"{hit.Groups["root"].Value}/configs/{appId.ToString(CultureInfo.InvariantCulture)}/{row.Id.ToString(CultureInfo.InvariantCulture)}" }
            : m).ToList();

    [GeneratedRegex(@"^(?<root>.*)/configs/\d+/\d+$")]
    private static partial Regex ConfigSource();

    // ---- For deploys ----

    /// <summary>
    /// The read-only mounts a deploy gives the app's container (a Compose app's <paramref name="service"/>; null for its web
    /// service or a single container): each config file at its path, from where it is on the Docker host. Each file is
    /// written again first, from Gitberth's database, so a restored or moved data folder has it.
    /// </summary>
    public async Task<IReadOnlyList<VolumeMount>> MountsAsync(AppDefinition app, string? service, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ConfigFiles.AsNoTracking().Where(f => f.AppId == app.Id).OrderBy(f => f.Path).ToListAsync(ct);
        var web = app.IsCompose ? app.WebService : null;
        var list = new List<VolumeMount>();
        foreach (var f in rows.Where(f => (f.Service ?? web) == (service ?? web)))
        {
            var file = FileOf(options, app.Id, f.Id);
            await WriteInPlaceAsync(file, Encoding.UTF8.GetBytes(protector.TryUnprotect(f.ContentProtected)), f.Mode, ct);
            if (await self.HostPathAsync(file, ct) is { } host) list.Add(new VolumeMount { Source = host, Target = f.Path, ReadOnly = true });
        }
        return list;
    }

    /// <summary>All of a Compose app's mounts by service (the web service's under its own name).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<VolumeMount>>> ComposeMountsAsync(AppDefinition app, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var services = await db.ConfigFiles.AsNoTracking().Where(f => f.AppId == app.Id).Select(f => f.Service).Distinct().ToListAsync(ct);
        var result = new Dictionary<string, IReadOnlyList<VolumeMount>>(StringComparer.Ordinal);
        foreach (var s in services)
        {
            var name = s ?? app.WebService;
            if (name is null) continue;
            result[name] = await MountsAsync(app, s, ct);
        }
        return result;
    }

    /// <summary>
    /// Writes the file where it is, in place (the same file, so a container that has it bind-mounted sees the new contents
    /// at once), with its mode.
    /// </summary>
    internal static async Task WriteInPlaceAsync(string file, byte[] bytes, int mode, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (Directory.Exists(file)) Directory.Delete(file, recursive: false);
        await using (var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(0);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, (UnixFileMode)(mode & 511));
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<AppDefinition> AppOfAsync(string key, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key[3..] : null;
            if (await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Container == key || (a.Container == null && slug != null && a.Slug == slug), ct) is { } row)
                return row;
        }
        if (catalog is null) throw new InvalidOperationException($"There's no app called {key}.");
        return await catalog.Value.AdoptAsync(key, ct);
    }
}
