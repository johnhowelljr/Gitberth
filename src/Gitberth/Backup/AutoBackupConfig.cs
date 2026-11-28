using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Gitberth.Backup;

/// <summary>When automatic backups run.</summary>
public enum AutoSchedule { Off, Daily, Weekly, Hours }

/// <summary>Where automatic backups go. Kept by name: only ever appended.</summary>
public enum AutoDestinationKind { Folder, Smb, S3, Ftp }

/// <summary>
/// Automatic backups as Settings › Save and restore keeps them (the setting backup.auto, JSON): when, what (the items left
/// out, so a new app is saved by itself), where, how many to keep, and the key pair the files are sealed to. The SMB and
/// FTP passwords and the S3 secret key are kept encrypted (SecretProtector) in it; nothing in it opens a backup.
/// </summary>
public sealed record AutoBackupConfig
{
    public AutoSchedule Schedule { get; init; } = AutoSchedule.Off;

    /// <summary>The time of day (HH:mm, Gitberth's time zone) a daily or weekly backup runs.</summary>
    public string At { get; init; } = "03:00";

    public DayOfWeek Day { get; init; } = DayOfWeek.Sunday;

    /// <summary>Every N hours (1 to 168), counted from the last run, or from when the schedule was set.</summary>
    public int EveryHours { get; init; } = 24;

    /// <summary>When the schedule was last changed: the first run is the first one due after it.</summary>
    public DateTime SetAt { get; init; }

    /// <summary>The items left out (an app unticked, a cache's volume): everything else the list ticks is saved.</summary>
    public List<string> Excluded { get; init; } = [];

    /// <summary>Items the list leaves unticked (a host folder over 2 GiB) that the owner ticked.</summary>
    public List<string> Added { get; init; } = [];

    public bool StopEverything { get; init; }

    public AutoDestinationKind Where { get; init; } = AutoDestinationKind.Folder;

    /// <summary>A folder on the Docker host, by its absolute path (/mnt/backups).</summary>
    public string? Folder { get; init; }

    /// <summary>//server/share, or //server/share/folder.</summary>
    public string? SmbShare { get; init; }

    public string? SmbUser { get; init; }
    public string? SmbDomain { get; init; }

    /// <summary>Encrypted with SecretProtector.</summary>
    public string? SmbPassword { get; init; }

    /// <summary>https://s3.us-west-004.backblazeb2.com, http://minio:9000; empty for AWS S3 (from the region).</summary>
    public string? S3Endpoint { get; init; }

    public string? S3Region { get; init; }
    public string? S3Bucket { get; init; }

    /// <summary>A folder in the bucket (backups/gitberth); empty for the bucket's top.</summary>
    public string? S3Prefix { get; init; }

    public string? S3AccessKey { get; init; }

    /// <summary>Encrypted with SecretProtector.</summary>
    public string? S3Secret { get; init; }

    /// <summary>Path-style addresses (endpoint/bucket/key): null picks (on everywhere but AWS S3).</summary>
    public bool? S3PathStyle { get; init; }

    /// <summary>ftp://server[:port]/folder (TLS when the server offers it), or ftps://… (TLS from the first byte).</summary>
    public string? FtpAddress { get; init; }

    public string? FtpUser { get; init; }

    /// <summary>Encrypted with SecretProtector.</summary>
    public string? FtpPassword { get; init; }

    /// <summary>How many of this Gitberth's automatic files stay at the destination (1 to 365).</summary>
    public int Keep { get; init; } = 7;

    /// <summary>The key pair files are sealed to; null until the owner sets a passphrase.</summary>
    public SealedKeyPair? Key { get; init; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The saved setting; the defaults (off) when it's unset or can't be read.</summary>
    public static AutoBackupConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutoBackupConfig();
        try
        {
            return JsonSerializer.Deserialize<AutoBackupConfig>(json, Json) ?? new AutoBackupConfig();
        }
        catch (JsonException)
        {
            return new AutoBackupConfig();
        }
    }
}

/// <summary>One automatic backup as it ended, for the card's list (the last 20 are kept).</summary>
/// <param name="Manual">Started with Back up now (by whom: <paramref name="By"/>), not by the schedule.</param>
/// <param name="Problem">Why it failed, in a sentence; null when it worked.</param>
public sealed record AutoBackupRun(DateTime At, bool Manual, bool Ok, long Bytes, double Seconds, string? File, string? Problem, string? KeyId = null,
    string? By = null);

/// <summary>Automatic backups' own record (the setting backup.auto.state): the runs, the last good one, and what was told.</summary>
public sealed class AutoBackupState
{
    public List<AutoBackupRun> Runs { get; set; } = [];

    /// <summary>The last run that worked.</summary>
    public DateTime? LastOk { get; set; }

    /// <summary>The last run, worked or not: the schedule counts from it.</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>The failure last told (its reason and when), so a run failing the same way isn't told every time.</summary>
    public string? ToldProblem { get; set; }

    public DateTime? ToldAt { get; set; }

    public const int KeptRuns = 20;

    public string ToJson() => JsonSerializer.Serialize(this, AutoBackupConfig.Json);

    public static AutoBackupState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutoBackupState();
        try
        {
            return JsonSerializer.Deserialize<AutoBackupState>(json, AutoBackupConfig.Json) ?? new AutoBackupState();
        }
        catch (JsonException)
        {
            return new AutoBackupState();
        }
    }

    /// <summary>A run added at the top; the oldest go past <see cref="KeptRuns"/>.</summary>
    public void Add(AutoBackupRun run)
    {
        Runs.Insert(0, run);
        if (Runs.Count > KeptRuns) Runs.RemoveRange(KeptRuns, Runs.Count - KeptRuns);
        LastRun = run.At;
        if (run.Ok) LastOk = run.At;
    }
}

/// <summary>A file of an automatic backup at its destination.</summary>
/// <param name="KeyId">The short ID of the key pair it's sealed to ("3f9c2ab1"); null when its header couldn't be read.</param>
public sealed record RemoteBackup(string Name, long Bytes, DateTime? Modified, string? KeyId, string Host, DateTime SavedAt);

/// <summary>
/// Automatic backups' rules, pure: when the next one runs, what it saves, its file's name, which files keep-N removes, and
/// what's wrong with the settings as they'd be saved.
/// </summary>
public static partial class AutoBackupRules
{
    public const int MinHours = 1;
    public const int MaxHours = 168;
    public const int MinKeep = 1;
    public const int MaxKeep = 365;

    /// <summary>A run missed while Gitberth was down runs once, this long after it starts.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(5);

    // gitberth-auto-<host>-<yyyyMMdd-HHmmss>.gbak: nothing else at a destination is ever counted or removed.
    [GeneratedRegex(@"^gitberth-auto-(?<host>[A-Za-z0-9_-]+)-(?<stamp>\d{8}-\d{6})\.gbak$")]
    private static partial Regex OwnName();

    /// <summary>The file's name: gitberth-auto-homelab-20261127-030000.gbak (UTC).</summary>
    public static string FileName(string host, DateTime utc) =>
        $"gitberth-auto-{HostWord(host)}-{utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{BackupFormat.Extension}";

    /// <summary>The host's name as a file name takes it (letters, digits, - and _); "gitberth" when it has none.</summary>
    public static string HostWord(string? host)
    {
        var word = new string((host ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray()).Trim('-');
        return word.Length > 0 ? word : "gitberth";
    }

    /// <summary>An automatic backup's file, read from its name: its host and when it was saved; null for any other file.</summary>
    public static (string Host, DateTime SavedAt)? Parse(string name)
    {
        var m = OwnName().Match(name);
        if (!m.Success || !DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return null;
        return (m.Groups["host"].Value, at);
    }

    /// <summary>
    /// The files keep-N removes: only this host's automatic files, the newest <paramref name="keep"/> of them stay. Every
    /// other file (another host's, one of the owner's own) is never among them.
    /// </summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> names, string host, int keep)
    {
        var word = HostWord(host);
        return names.Select(n => (Name: n, Parsed: Parse(n))).Where(x => x.Parsed is { } p && p.Host == word)
            .OrderByDescending(x => x.Parsed!.Value.SavedAt).Skip(Math.Max(MinKeep, keep)).Select(x => x.Name).ToList();
    }

    /// <summary>
    /// When the next scheduled backup is due (UTC), counting from the last run, or from when the schedule was set; null
    /// when it's off. A daily or weekly time is Gitberth's own time zone's.
    /// </summary>
    public static DateTime? Next(AutoBackupConfig c, DateTime? lastRun, TimeZoneInfo zone)
    {
        var from = lastRun is { } last && last > c.SetAt ? last : c.SetAt;
        switch (c.Schedule)
        {
            case AutoSchedule.Hours:
                return from + TimeSpan.FromHours(Math.Clamp(c.EveryHours, MinHours, MaxHours));
            case AutoSchedule.Daily or AutoSchedule.Weekly:
                if (!TryTime(c.At, out var time)) return null;
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), zone);
                for (var day = 0; day <= 8; day++)
                {
                    var date = local.Date.AddDays(day);
                    if (c.Schedule == AutoSchedule.Weekly && date.DayOfWeek != c.Day) continue;
                    var slot = Utc(date + time, zone);
                    if (slot > from) return slot;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>How long one schedule's turn lasts: a day, a week, or N hours; null when it's off.</summary>
    public static TimeSpan? Interval(AutoBackupConfig c) => c.Schedule switch
    {
        AutoSchedule.Daily => TimeSpan.FromDays(1),
        AutoSchedule.Weekly => TimeSpan.FromDays(7),
        AutoSchedule.Hours => TimeSpan.FromHours(Math.Clamp(c.EveryHours, MinHours, MaxHours)),
        _ => null,
    };

    /// <summary>
    /// Whether the schedule is on and no backup worked for twice its interval (counted from when it was set, when none ever
    /// did): the dashboard says automatic backups are failing.
    /// </summary>
    public static bool Overdue(AutoBackupConfig c, DateTime? lastOk, DateTime now) =>
        Interval(c) is { } every && now - (lastOk is { } ok && ok > c.SetAt ? ok : c.SetAt) > every * 2;

    // A local time in the zone as UTC: one the clocks skip (spring forward) is taken an hour later.
    private static DateTime Utc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>"03:00" as a time of day; false for anything that isn't HH:mm.</summary>
    public static bool TryTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);

    /// <summary>
    /// What an automatic backup saves, from the list as it is now: what the list ticks (a new app too), less what was left
    /// out and what's under it, plus what the owner ticked that the list doesn't (with its parent). A child whose parent
    /// isn't saved isn't either.
    /// </summary>
    public static BackupChoice Choice(IReadOnlyList<BackupItem> items, AutoBackupConfig c)
    {
        var ticked = Ticked(items, c.Excluded, c.Added);
        return new BackupChoice(ticked, c.StopEverything);
    }

    /// <summary>The ticks the save list shows for these choices (the card's list, and Choice).</summary>
    public static HashSet<string> Ticked(IReadOnlyList<BackupItem> items, IEnumerable<string> excluded, IEnumerable<string> added)
    {
        var ticked = items.Where(i => i.Included || i.Required).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in excluded)
        {
            if (items.FirstOrDefault(i => i.Id == id) is not { Required: false }) continue;
            ticked.Remove(id);
            foreach (var child in items.Where(i => i.Parent == id && !i.Required)) ticked.Remove(child.Id);
        }
        foreach (var id in added)
            if (items.FirstOrDefault(i => i.Id == id) is { } item)
            {
                ticked.Add(id);
                if (item.Parent is { } parent) ticked.Add(parent);
            }
        ticked.RemoveWhere(id => items.FirstOrDefault(i => i.Id == id) is { Parent: { } parent } && !ticked.Contains(parent));
        return ticked;
    }

    /// <summary>The ticks as they're kept: what the list ticks that isn't ticked (left out), and what's ticked that it doesn't (added).</summary>
    public static (List<string> Excluded, List<string> Added) Kept(IReadOnlyList<BackupItem> items, IReadOnlySet<string> ticked)
    {
        var defaults = items.Where(i => i.Included || i.Required).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return ([.. defaults.Where(id => !ticked.Contains(id)).Order(StringComparer.Ordinal)],
            [.. ticked.Where(id => !defaults.Contains(id) && items.Any(i => i.Id == id)).Order(StringComparer.Ordinal)]);
    }

    // ---- What's wrong with the settings, in a sentence each ----

    /// <summary>What's wrong with the schedule and keep-N as they'd be saved; null when they're fine.</summary>
    public static string? ScheduleProblem(AutoBackupConfig c)
    {
        if (c.Schedule is AutoSchedule.Daily or AutoSchedule.Weekly && !TryTime(c.At, out _)) return "Give the time as HH:MM, like 03:00.";
        if (c.Schedule == AutoSchedule.Hours && c.EveryHours is < MinHours or > MaxHours) return $"Every {MinHours} to {MaxHours} hours.";
        if (c.Keep is < MinKeep or > MaxKeep) return $"Keep {MinKeep} to {MaxKeep} backups.";
        if (c.Schedule != AutoSchedule.Off && c.Key is null) return "Set a passphrase for automatic backups first: the files are encrypted with it.";
        return null;
    }

    /// <summary>What's wrong with the destination as it'd be saved; null when it can be used.</summary>
    /// <param name="hasSecret">Whether its password or secret key is set (given now, or kept from before).</param>
    public static string? DestinationProblem(AutoBackupConfig c, bool hasSecret) => c.Where switch
    {
        AutoDestinationKind.Folder => FolderProblem(c.Folder),
        AutoDestinationKind.Smb => SmbProblem(c.SmbShare, c.SmbUser, c.SmbDomain) ?? (hasSecret || string.IsNullOrEmpty(c.SmbUser) ? null : "Enter the share's password."),
        AutoDestinationKind.Ftp => FtpProblem(c.FtpAddress, c.FtpUser) ?? (hasSecret ? null : "Enter the FTP password."),
        _ => S3Problem(c) ?? (hasSecret ? null : "Enter the secret key."),
    };

    /// <summary>
    /// ftp://server[:port]/folder (or ftps://) as curl takes it: no user or password in it (they have fields of their own),
    /// every part of its folder a plain name; null when it isn't one.
    /// </summary>
    public static FtpTarget? Ftp(string? address)
    {
        // A . or .. part is refused as typed (Uri would quietly take it out).
        if (string.IsNullOrWhiteSpace(address) || address.Split('/').Any(p => p is "." or "..") || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("ftp" or "ftps") || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || !ServerName().IsMatch(u.Host)) return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (parts.Any(p => !SmbSafe(p) || p.Contains('/'))) return null;
        return new FtpTarget(u.Scheme, u.Host.ToLowerInvariant(), u.IsDefaultPort ? null : u.Port, string.Join('/', parts));
    }

    public static string? FtpProblem(string? address, string? user)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Enter the FTP server's address, like ftp://nas/backups.";
        if (Ftp(address) is null)
            return "Give the address as ftp://server/folder (or ftps:// for TLS from the start): no user name or password in it, no quotes, semicolons or line breaks.";
        if (string.IsNullOrWhiteSpace(user)) return "Enter the FTP user name.";
        if (user.Trim().Length > 128 || user.IndexOfAny(['\n', '\r', '\0']) >= 0) return "The user name is one line of at most 128 characters.";
        return null;
    }

    /// <summary>An FTP password as curl's netrc file takes it: one line.</summary>
    public static string? FtpPasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.IndexOfAny(['\n', '\r', '\0']) < 0 ? null : "The password can't have line breaks.";

    /// <summary>A folder on the host as a destination: an absolute path, not the root, not one of the host's own folders.</summary>
    public static string? FolderProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Enter the folder on this host, like /mnt/backups.";
        var path = HostFolders.Clean(folder.Trim());
        if (path is null || folder.Contains(':')) return "Give the folder's whole path on this host, starting with /, like /mnt/backups.";
        if (path == "/" || !HostFolders.Offered(path, null)) return "Pick a folder of your own, not the host's root or one of its system folders (/etc, /proc, /var/lib/docker…).";
        return null;
    }

    // What smbclient's commands are given: no quotes, semicolons or line breaks (a must), nor anything a path can't hold.
    private static readonly char[] SmbRefused = ['"', '\'', ';', '\n', '\r', '\0', '\\', '*', '?', '<', '>', '|', ':'];

    /// <summary>Whether a name may go into smbclient's command line and commands: nothing it could read as more than a name.</summary>
    public static bool SmbSafe(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 255 && name.IndexOfAny(SmbRefused) < 0 && name is not ("." or "..") && name.Trim() == name;

    /// <summary>//server/share[/folder…] as smbclient takes it: the service (//server/share) and the folder in it (a/b), or why not.</summary>
    public static (string Service, string? Folder)? SmbTarget(string? share)
    {
        if (string.IsNullOrWhiteSpace(share)) return null;
        var text = share.Trim().Replace('\\', '/');
        if (!text.StartsWith("//", StringComparison.Ordinal)) return null;
        var parts = text[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ServerName().IsMatch(parts[0]) || parts.Skip(1).Any(p => !SmbSafe(p))) return null;
        return ($"//{parts[0]}/{parts[1]}", parts.Length > 2 ? string.Join('/', parts.Skip(2)) : null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,252}[A-Za-z0-9])?$")]
    private static partial Regex ServerName();

    public static string? SmbProblem(string? share, string? user, string? domain)
    {
        if (string.IsNullOrWhiteSpace(share)) return "Enter the share, like //nas/backups or //192.168.1.20/backups/gitberth.";
        if (SmbTarget(share) is null)
            return "Give the share as //server/share or //server/share/folder: the server's name or address, then the share; no quotes, semicolons or line breaks.";
        if (!string.IsNullOrEmpty(user) && (!SmbSafe(user) || user.Contains('/')))
            return "The user name can't have quotes, semicolons, slashes or line breaks.";
        if (!string.IsNullOrEmpty(domain) && !DomainName().IsMatch(domain)) return "The domain (workgroup) is letters, digits, dots and dashes.";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex DomainName();

    /// <summary>A password as smbclient's auth file takes it: one line, not starting with a space.</summary>
    public static string? SmbPasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) ? null
        : password.IndexOfAny(['\n', '\r', '\0']) >= 0 || char.IsWhiteSpace(password[0]) ? "The password can't have line breaks or start with a space." : null;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketName();

    [GeneratedRegex(@"^[a-z0-9-]{1,40}$")]
    private static partial Regex RegionName();

    public static string? S3Problem(AutoBackupConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.S3Endpoint)
            && (!Uri.TryCreate(c.S3Endpoint.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0
                || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.AbsolutePath.Trim('/').Length > 0))
            return "Give the endpoint as its address, like https://s3.us-west-004.backblazeb2.com; leave it empty for AWS S3.";
        if (string.IsNullOrWhiteSpace(c.S3Region) ? string.IsNullOrWhiteSpace(c.S3Endpoint) : !RegionName().IsMatch(c.S3Region.Trim()))
            return "Enter the region, like us-east-1 (auto for Cloudflare R2).";
        if (string.IsNullOrWhiteSpace(c.S3Bucket) || !BucketName().IsMatch(c.S3Bucket.Trim()) || c.S3Bucket.Contains(".."))
            return "Enter the bucket's name: 3 to 63 lower-case letters, digits, dots and dashes.";
        if (c.S3Prefix is { Length: > 0 } prefix && (prefix.Length > 200 || prefix.Any(ch => char.IsControl(ch) || ch is '\\' or '?' or '#')
                || prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(p => p is "." or "..")))
            return "The folder in the bucket is a path like backups/gitberth.";
        if (string.IsNullOrWhiteSpace(c.S3AccessKey) || c.S3AccessKey.Trim().Any(char.IsWhiteSpace)) return "Enter the access key.";
        return null;
    }

    /// <summary>The passphrase for automatic backups as typed twice: what's wrong with it, or null.</summary>
    public static string? PassphraseProblem(string? passphrase, string? again) =>
        string.IsNullOrEmpty(passphrase) || passphrase.Length < BackupFormat.MinPassphrase ? $"At least {BackupFormat.MinPassphrase} characters."
        : again != passphrase ? "The two passphrases aren't the same." : null;
}
