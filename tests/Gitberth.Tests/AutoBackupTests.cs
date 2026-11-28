using System.Security.Cryptography;
using System.Text;
using Gitberth.Audit;
using Gitberth.Backup;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Automatic backups: format 2 (sealed to a key pair, opened by the passphrase alone), the schedule and its missed run,
/// what's saved (left-out items kept), keep-N (only this host's own files), the destinations' checks (SMB names, S3's
/// signature), host folders saved and restored, and a run end to end: written, pruned, recorded, told and shown.
/// </summary>
public sealed class AutoBackupTests
{
    private const string Passphrase = "correct horse battery staple";
    private const int Iterations = 1000;

    private static byte[] DumpLike(int size, string marker)
    {
        var text = new StringBuilder();
        var n = 0;
        while (text.Length < size) text.Append("INSERT INTO orders VALUES (").Append(n++).Append(", '").Append(marker).Append("', ").Append(n % 97).Append(");\n");
        return Encoding.UTF8.GetBytes(text.ToString(0, size));
    }

    private static async Task<byte[]> SealedFileAsync(SealedKeyPair pair, byte[] content, string name = "dump/db")
    {
        using var key = BackupKey.ForNewFile(pair, 4096);
        var output = new MemoryStream();
        using (var writer = new BackupWriter(output, key))
        {
            await writer.WritePartAsync(BackupPartKind.Dump, name, content.Length, new MemoryStream(content), default);
            await writer.FinishAsync(default);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> ReadAsync(byte[] file, string passphrase)
    {
        using var reader = await BackupReader.OpenAsync(new MemoryStream(file), passphrase);
        var part = await reader.NextPartAsync();
        var content = new MemoryStream();
        await part!.Content.CopyToAsync(content);
        Assert.Null(await reader.NextPartAsync());
        return content.ToArray();
    }

    // ---- format 2 ----

    [Fact]
    public async Task A_sealed_file_opens_with_its_passphrase_alone_and_holds_no_plaintext()
    {
        var pair = SealedKeys.Make(Passphrase, Iterations);
        var content = DumpLike(200_000, "s3cr3t-api-key");
        var file = await SealedFileAsync(pair, content);

        Assert.True(file.Length < content.Length / 3, $"{file.Length} bytes for {content.Length} of text: it isn't compressed");
        Assert.Equal(-1, file.AsSpan().IndexOf("s3cr3t-api-key"u8));
        Assert.Equal(-1, file.AsSpan().IndexOf("dump/db"u8));
        var (header, _) = await BackupReader.ReadHeaderAsync(new MemoryStream(file));
        Assert.Equal(BackupFormat.Sealed, header.Version);
        Assert.Equal(pair.ShortId, header.ShortKeyId);
        Assert.Equal(content, await ReadAsync(file, Passphrase));

        // The header is read to its end and no further: the reader goes on from there on a stream that can't seek.
        using var reader = await BackupReader.OpenAsync(new OneWay(file), Passphrase);
        Assert.NotNull(await reader.NextPartAsync());
    }

    [Fact]
    public async Task A_wrong_passphrase_and_a_changed_byte_are_refused()
    {
        var pair = SealedKeys.Make(Passphrase, Iterations);
        var file = await SealedFileAsync(pair, DumpLike(50_000, "row-7f3a"));

        var wrong = await Assert.ThrowsAsync<BackupFileException>(() => ReadAsync(file, "not the passphrase at all"));
        Assert.Equal(UploadProblem.WrongPassphrase, wrong.Problem);

        var (header, bytes) = await BackupReader.ReadHeaderAsync(new MemoryStream(file));
        // A byte of the body, of the file's wrapped key, and of the ephemeral key: each refused, nothing handed out.
        foreach (var at in new[] { bytes.Length + 200, bytes.Length - 10, bytes.Length - 2 - header.WrappedKey!.Length - 20 })
        {
            var changed = (byte[])file.Clone();
            changed[at] ^= 0x40;
            var refused = await Assert.ThrowsAsync<BackupFileException>(() => ReadAsync(changed, Passphrase));
            Assert.Equal(UploadProblem.Damaged, refused.Problem);
        }
    }

    [Fact]
    public async Task Gitberths_saved_settings_cant_open_a_file_without_the_passphrase()
    {
        var pair = SealedKeys.Make(Passphrase, Iterations);
        var file = await SealedFileAsync(pair, DumpLike(20_000, "row-7f3a"));
        var settings = new AutoBackupConfig { Key = pair }.ToJson();
        Assert.DoesNotContain(Passphrase, settings, StringComparison.Ordinal);

        // What's kept is a public key and a sealed blob: neither is a private key.
        foreach (var blob in new[] { pair.PublicKey, pair.SealedPrivateKey, pair.Salt })
        {
            using var ecdh = ECDiffieHellman.Create();
            Assert.ThrowsAny<CryptographicException>(() => ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(blob), out _));
        }
        // Anything but the passphrase, the settings' own values among them, is refused.
        foreach (var guess in new[] { "", pair.Id, pair.PublicKey, pair.SealedPrivateKey, pair.Salt, settings })
            Assert.Equal(UploadProblem.WrongPassphrase, (await Assert.ThrowsAsync<BackupFileException>(() => ReadAsync(file, guess))).Problem);
        Assert.True(SealedKeys.Opens(pair, Passphrase));
        Assert.False(SealedKeys.Opens(pair, Passphrase + "!"));
    }

    [Fact]
    public async Task A_new_passphrase_makes_a_new_key_and_older_files_keep_opening_with_theirs()
    {
        var first = SealedKeys.Make(Passphrase, Iterations);
        var second = SealedKeys.Make("another long passphrase", Iterations);
        Assert.NotEqual(first.Id, second.Id);
        var old = await SealedFileAsync(first, DumpLike(5000, "a"));
        var made = await SealedFileAsync(second, DumpLike(5000, "b"));
        Assert.Equal(DumpLike(5000, "a"), await ReadAsync(old, Passphrase));
        Assert.Equal(UploadProblem.WrongPassphrase, (await Assert.ThrowsAsync<BackupFileException>(() => ReadAsync(old, "another long passphrase"))).Problem);
        Assert.Equal(DumpLike(5000, "b"), await ReadAsync(made, "another long passphrase"));
        Assert.Equal(first.ShortId, AutoBackups.KeyIdOf(old[..64]));
        Assert.Equal(second.ShortId, AutoBackups.KeyIdOf(made[..64]));
        Assert.Throws<ArgumentException>(() => SealedKeys.Make("too short", Iterations));
    }

    // ---- the schedule ----

    private static readonly DateTime Day = new(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_next_backup_is_the_first_slot_after_the_last_one_or_after_the_schedule_was_set()
    {
        var utc = TimeZoneInfo.Utc;
        var daily = new AutoBackupConfig { Schedule = AutoSchedule.Daily, At = "03:00", SetAt = Day.AddHours(14) };
        Assert.Equal(Day.AddDays(1).AddHours(3), AutoBackupRules.Next(daily, null, utc));
        Assert.Equal(Day.AddDays(2).AddHours(3), AutoBackupRules.Next(daily, Day.AddDays(1).AddHours(3), utc));

        var weekly = daily with { Schedule = AutoSchedule.Weekly, Day = DayOfWeek.Sunday };
        Assert.Equal(new DateTime(2026, 11, 29, 3, 0, 0, DateTimeKind.Utc), AutoBackupRules.Next(weekly, null, utc));

        var hours = daily with { Schedule = AutoSchedule.Hours, EveryHours = 6 };
        Assert.Equal(Day.AddHours(20), AutoBackupRules.Next(hours, null, utc));
        Assert.Equal(Day.AddDays(1).AddHours(9), AutoBackupRules.Next(hours, Day.AddDays(1).AddHours(3), utc));
        Assert.Null(AutoBackupRules.Next(daily with { Schedule = AutoSchedule.Off }, null, utc));

        // In Gitberth's time zone: 03:00 two hours east of UTC is 01:00 UTC.
        var east = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        Assert.Equal(Day.AddDays(1).AddHours(1), AutoBackupRules.Next(daily, null, east));

        // No good backup for twice the interval, counted from the last good one (or from when it was set, if later).
        Assert.True(AutoBackupRules.Overdue(daily with { SetAt = Day }, Day.AddHours(3), Day.AddDays(2).AddHours(4)));
        Assert.False(AutoBackupRules.Overdue(daily with { SetAt = Day }, Day.AddDays(1).AddHours(3), Day.AddDays(2).AddHours(4)));
        Assert.False(AutoBackupRules.Overdue(daily, Day.AddHours(3), Day.AddDays(2).AddHours(4)));
        Assert.False(AutoBackupRules.Overdue(daily with { Schedule = AutoSchedule.Off }, null, Day.AddDays(30)));
    }

    [Fact]
    public async Task A_backup_missed_while_Gitberth_was_down_runs_once_five_minutes_after_it_starts()
    {
        using var rig = new AutoRig();
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        await rig.Auto.SaveAsync(new AutoBackupConfig { Schedule = AutoSchedule.Daily, At = "03:00", Folder = "/mnt/backups" }, null, null);
        // It was set two days ago, and Gitberth comes up at 10:00, long after 03:00.
        var config = await rig.Auto.ConfigAsync();
        await rig.Settings.SetAsync(AutoBackups.ConfigKey, (config with { SetAt = Day.AddDays(-2) }).ToJson());
        using var again = new AutoRig(rig);
        rig.Clock.Now = Day.AddHours(10);
        await again.Auto.StartedAsync(Day.AddHours(10), default);
        Assert.Equal(Day.AddHours(10).AddMinutes(5), (await again.Auto.StatusAsync()).Next);
        await again.Auto.LookAsync(default);
        Assert.Empty(again.Destination.Files);

        rig.Clock.Now = Day.AddHours(10).AddMinutes(5);
        await again.Auto.LookAsync(default);
        Assert.Single(again.Destination.Files);
        // Then the schedule as set: tomorrow at 03:00.
        Assert.Equal(Day.AddDays(1).AddHours(3), (await again.Auto.StatusAsync()).Next);
    }

    // ---- what's saved, and keep-N ----

    private static BackupItem Item(string id, string? parent = null, bool included = true, BackupItemKind kind = BackupItemKind.App) =>
        new(id, kind, id, null, null, id == "gitberth", included, parent);

    [Fact]
    public void Left_out_items_stay_out_and_a_new_app_is_saved_by_itself()
    {
        IReadOnlyList<BackupItem> items =
        [
            Item("gitberth", kind: BackupItemKind.GitberthData), Item("app:1"), Item("volume:shop-cache", "app:1", kind: BackupItemKind.Volume),
            Item("app:2"), Item("volume:media", "app:2", kind: BackupItemKind.Volume), Item("folder:/srv/photos", "app:2", false, BackupItemKind.HostFolder),
        ];
        var ticked = AutoBackupRules.Ticked(items, [], []);
        ticked.Remove("volume:shop-cache");
        ticked.Add("folder:/srv/photos");
        var (excluded, added) = AutoBackupRules.Kept(items, ticked);
        Assert.Equal(["volume:shop-cache"], excluded);
        Assert.Equal(["folder:/srv/photos"], added);

        // Later: a new app, and the left-out app's parent unticked takes its children with it.
        IReadOnlyList<BackupItem> later = [.. items, Item("app:3"), Item("volume:new", "app:3", kind: BackupItemKind.Volume)];
        var choice = AutoBackupRules.Choice(later, new AutoBackupConfig { Excluded = excluded, Added = added });
        Assert.Contains("app:3", choice.Included);
        Assert.Contains("volume:new", choice.Included);
        Assert.Contains("folder:/srv/photos", choice.Included);
        Assert.DoesNotContain("volume:shop-cache", choice.Included);
        var noApp = AutoBackupRules.Choice(later, new AutoBackupConfig { Excluded = ["app:2"], Added = [] });
        Assert.DoesNotContain("volume:media", noApp.Included);
        Assert.Contains("gitberth", AutoBackupRules.Choice(later, new AutoBackupConfig { Excluded = ["gitberth"] }).Included);
    }

    [Fact]
    public void Keep_n_counts_and_removes_only_this_hosts_own_automatic_files()
    {
        var name = AutoBackupRules.FileName("homelab", new DateTime(2026, 11, 27, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal("gitberth-auto-homelab-20261127-030000.gbak", name);
        Assert.Equal(("homelab", new DateTime(2026, 11, 27, 3, 0, 0, DateTimeKind.Utc)), AutoBackupRules.Parse(name));
        Assert.Null(AutoBackupRules.Parse("gitberth-backup-homelab-2026-11-27-0300.gbak"));
        Assert.Null(AutoBackupRules.Parse("gitberth-auto-homelab-20261127-030000.gbak.part"));
        Assert.Equal("gitberth", AutoBackupRules.HostWord("§§"));

        string[] names =
        [
            "gitberth-auto-homelab-20261125-030000.gbak", "gitberth-auto-homelab-20261127-030000.gbak", "gitberth-auto-homelab-20261126-030000.gbak",
            "gitberth-auto-other-20261101-030000.gbak", "my-notes.txt", "gitberth-backup-homelab-2026-11-01-0300.gbak",
        ];
        Assert.Equal(["gitberth-auto-homelab-20261125-030000.gbak"], AutoBackupRules.ToPrune(names, "homelab", 2));
        Assert.Empty(AutoBackupRules.ToPrune(names, "homelab", 3));
        Assert.Equal(2, AutoBackupRules.ToPrune(names, "homelab", 1).Count);
    }

    // ---- the destinations' checks ----

    [Fact]
    public void Smb_names_never_carry_quotes_semicolons_or_line_breaks_into_smbclient()
    {
        Assert.Equal(("//nas/backups", "gitberth/daily"), AutoBackupRules.SmbTarget("//nas/backups/gitberth/daily")!.Value);
        Assert.Equal(("//192.168.1.20/backups", (string?)null), AutoBackupRules.SmbTarget(@"\\192.168.1.20\backups")!.Value);
        Assert.Equal(("//nas/my share", "a b"), AutoBackupRules.SmbTarget("//nas/my share/a b")!.Value);
        foreach (var bad in new[] { "//nas", "nas/backups", "//nas/back\"ups", "//nas/b;ackups", "//nas/backups/x\ny", "//nas/backups/..", "//n as/backups",
                     "//nas/backups/'x'", "//nas/backups/a|b" })
            Assert.Null(AutoBackupRules.SmbTarget(bad));
        Assert.NotNull(AutoBackupRules.SmbProblem("//nas/backups", "admin\"; rm", null));
        Assert.NotNull(AutoBackupRules.SmbProblem("//nas/backups", "a;b", null));
        Assert.NotNull(AutoBackupRules.SmbPasswordProblem("line\nbreak"));
        Assert.Null(AutoBackupRules.SmbPasswordProblem("has \"quotes\"; and semicolons"));
        Assert.Null(AutoBackupRules.SmbProblem("//nas/backups/gitberth", "backup", "WORKGROUP"));
    }

    [Fact]
    public void A_destination_is_checked_before_it_is_saved()
    {
        Assert.Null(AutoBackupRules.FolderProblem("/mnt/backups"));
        foreach (var bad in new[] { "", "mnt/backups", "/", "/etc/backups", "/proc", "/var/lib/docker/volumes", "/mnt/a:/b", "/mnt/../etc" })
            Assert.NotNull(AutoBackupRules.FolderProblem(bad));

        var s3 = new AutoBackupConfig { Where = AutoDestinationKind.S3, S3Endpoint = "http://minio:9000", S3Region = "us-east-1", S3Bucket = "backups", S3AccessKey = "gitberth" };
        Assert.Null(AutoBackupRules.S3Problem(s3));
        Assert.Null(AutoBackupRules.S3Problem(s3 with { S3Endpoint = null }));
        Assert.NotNull(AutoBackupRules.S3Problem(s3 with { S3Bucket = "Bad_Bucket" }));
        Assert.NotNull(AutoBackupRules.S3Problem(s3 with { S3Endpoint = "ftp://x" }));
        Assert.NotNull(AutoBackupRules.S3Problem(s3 with { S3Prefix = "a/../b" }));
        Assert.NotNull(AutoBackupRules.DestinationProblem(s3, hasSecret: false));
        Assert.True(S3Target.Of(s3).PathStyle);
        Assert.False(S3Target.Of(s3 with { S3Endpoint = null }).PathStyle);
        Assert.Equal(new Uri("https://s3.eu-west-1.amazonaws.com"), S3Target.Of(s3 with { S3Endpoint = null, S3Region = "eu-west-1" }).Endpoint);
        Assert.Equal("gitberth/x.gbak", S3Target.Of(s3 with { S3Prefix = "/gitberth/" }).Key("x.gbak"));

        Assert.NotNull(AutoBackupRules.ScheduleProblem(new AutoBackupConfig { Schedule = AutoSchedule.Daily }));
        Assert.Null(AutoBackupRules.ScheduleProblem(new AutoBackupConfig { Schedule = AutoSchedule.Daily, Key = SealedKeys.Make(Passphrase, Iterations) }));
        Assert.NotNull(AutoBackupRules.ScheduleProblem(new AutoBackupConfig { Keep = 0 }));
        Assert.True(AutoDestinations.Ours("gitberth-auto-homelab-20261127-030000.gbak"));
        Assert.True(AutoDestinations.Ours("gitberth-test-0a1b2c.txt"));
        Assert.False(AutoDestinations.Ours("../gitberth-auto-homelab-20261127-030000.gbak"));
        Assert.False(AutoDestinations.Ours("notes.txt"));
    }

    [Fact]
    public void S3_requests_are_signed_as_AWS_signature_version_4_says()
    {
        // AWS's own example: GET /test.txt from examplebucket, with a range, on 24 May 2013.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = S3Client.EmptyHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var authorization = S3Client.Authorization("GET", "/test.txt", "", headers, S3Client.EmptyHash, "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, "
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", authorization);
        Assert.Equal("list-type=2&prefix=backups%2Fgitberth%2F", S3Client.CanonicalQuery([new("prefix", "backups/gitberth/"), new("list-type", "2")]));
        Assert.Equal("a%20b~c", S3Client.Encode("a b~c"));
    }

    [Fact]
    public void Listings_are_read_and_only_gitberths_own_files_kept()
    {
        var folder = FolderDestination.ParseList("gitberth-auto-homelab-20261127-030000.gbak\t1234\t1764212400\tR0JBSw==\nnotes.txt\t5\t1\t\ngitberth-test-1.txt\t3\t1\t\n");
        Assert.Equal(["gitberth-auto-homelab-20261127-030000.gbak", "gitberth-test-1.txt"], folder.Select(f => f.Name));
        Assert.Equal(1234, folder[0].Bytes);
        Assert.Equal("GBAK"u8.ToArray(), folder[0].Header);

        var smb = SmbDestination.ParseLs(
            "  .                                   D        0  Fri Nov 27 10:00:00 2026\n" +
            "  gitberth-auto-homelab-20261127-030000.gbak      A   123456  Fri Nov 27 10:00:05 2026\n" +
            "  family photos                       D        0  Fri Nov 27 10:00:00 2026\n" +
            "\t\t1234567 blocks of size 1024. 7654321 blocks available\n");
        Assert.Equal("gitberth-auto-homelab-20261127-030000.gbak", Assert.Single(smb).Name);
        Assert.Equal(123456, smb[0].Bytes);

        var ftp = FtpDestination.ParseList(
            "drwxr-xr-x    2 1000     1000         4096 Nov 28 10:00 old\r\n" +
            "-rw-------    1 1000     1000        16439 Nov 28 10:20 gitberth-auto-homelab-20261128-102000.gbak\r\n" +
            "-rw-r--r--    1 1000     1000            5 Jan 02  2025 notes.txt\r\n" +
            "11-28-26  10:21AM                17586 gitberth-auto-homelab-20261128-102100.gbak\r\n" +
            "11-28-26  10:21AM       <DIR>          gitberth-auto-folder\r\n");
        Assert.Equal([("gitberth-auto-homelab-20261128-102000.gbak", 16439L), ("gitberth-auto-homelab-20261128-102100.gbak", 17586L)],
            ftp.Select(f => (f.Name, f.Bytes)));
    }

    [Fact]
    public void An_ftp_address_is_a_plain_folder_and_its_password_stays_in_a_netrc_file()
    {
        var target = AutoBackupRules.Ftp("ftp://NAS.local:2121/backups/my%20box")!;
        Assert.Equal(("ftp", "nas.local", 2121, "backups/my box"), (target.Scheme, target.Host, target.Port, target.Folder));
        Assert.Equal("ftp://nas.local:2121/backups/my%20box/", target.Url);
        Assert.Equal("ftp://nas.local:2121/backups/my box", target.Display);
        Assert.Equal("ftps://nas/", AutoBackupRules.Ftp("ftps://nas")!.Url);
        foreach (var bad in new[] { "", "nas/backups", "http://nas/backups", "ftp://user:pw@nas/backups", "ftp://nas/back%22ups", "ftp://nas/a%3Bb",
                     "ftp://nas/x%0Ay", "ftp://nas/../etc", "ftp://nas/backups?x=1", "sftp://nas/backups" })
            Assert.Null(AutoBackupRules.Ftp(bad));
        Assert.NotNull(AutoBackupRules.FtpProblem("ftp://nas/backups", ""));
        Assert.Null(AutoBackupRules.FtpProblem("ftp://nas/backups", "backup"));
        Assert.NotNull(AutoBackupRules.FtpPasswordProblem("line\nbreak"));
        Assert.Null(AutoBackupRules.FtpPasswordProblem("with \"quotes\" and \\ spaces"));

        // The netrc line quotes its values, so a password with spaces, quotes or backslashes is read as typed.
        Assert.Equal("machine nas login \"backup\" password \"a \\\"b\\\" c\\\\d\"\n", FtpDestination.Netrc("nas", "backup", "a \"b\" c\\d"));
        Assert.Equal("the server refused the user name or password", FtpDestination.Explain(67, "curl: (67) Access denied: 530"));
        Assert.Equal("the server's TLS certificate isn't one this host trusts", FtpDestination.Explain(60, "curl: (60) SSL certificate problem"));
        Assert.Equal("Something else", FtpDestination.Explain(99, "curl: (99) Something else."));
        Assert.Equal("ftp://nas/backups", AutoDestinations.Describe(new AutoBackupConfig { Where = AutoDestinationKind.Ftp, FtpAddress = "ftp://nas/backups" }));
    }

    // ---- host folders ----

    [Fact]
    public void Only_a_containers_own_folders_are_offered_never_the_hosts_or_Gitberths()
    {
        Assert.True(HostFolders.Offered("/srv/photos", null));
        Assert.True(HostFolders.Offered("/home/me/app/data/", null));
        foreach (var never in new[] { "/var/run/docker.sock", "/run/docker.sock", "/proc", "/sys/fs/cgroup", "/dev/dri", "/etc", "/etc/localtime", "/var/lib/docker",
                     "/var/lib/docker/volumes/x/_data", "/", "relative/path", "/srv/../etc", "/opt/gitberth/data/compose/x" })
            Assert.False(HostFolders.Offered(never, "/opt/gitberth/data"), never);
        Assert.Equal("/srv/photos-website2", HostFolders.CopyPath("/srv/photos", "website2"));
        Assert.Equal("/srv/photos.before-202611271830", HostFolders.BeforePath("/srv/photos", new DateTime(2026, 11, 27, 18, 30, 0)));
    }

    [Fact]
    public async Task A_host_folder_is_listed_under_its_container_saved_and_filled_on_a_fresh_host()
    {
        using var old = new BackupHost();
        old.Engine.AddImage("sha256:web", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        old.Engine.Registry["nginx:1.29"] = "sha256:" + new string('b', 64);
        old.Engine.AddContainer("web", "nginx:1.29", "sha256:web", folders: ["/srv/photos:/usr/share/nginx/html", "/var/run/docker.sock:/var/run/docker.sock", "/etc/localtime:/etc/localtime"]);
        old.Engine.Files["/srv/photos"] = BackupTestEngine.FolderTar("photo-5be1");
        var service = old.Backup();
        var items = await service.ItemsAsync();
        var folder = Assert.Single(items, i => i.Kind == BackupItemKind.HostFolder);
        Assert.Equal(("folder:/srv/photos", "container:web", true), (folder.Id, folder.Parent, folder.Included));

        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), false), BackupHost.Passphrase);
        var file = new MemoryStream();
        await service.WriteAsync(service.Claim(ticket.Id)!, file, default);
        Assert.Contains(old.Engine.Calls, c => c.Contains("/h (/srv/photos)", StringComparison.Ordinal));
        Assert.DoesNotContain(old.Engine.Calls, c => c.Contains("docker.sock", StringComparison.Ordinal));

        using var fresh = new BackupHost();
        var restore = fresh.Restore();
        await restore.ReceiveAsync(new MemoryStream(file.ToArray()), default);
        await restore.StartAsync(BackupHost.Passphrase);
        await restore.PhaseOne;
        var step = restore.Status()!.Steps.Single(s => s.Id == "folder:/srv/photos");
        Assert.Equal(RestoreStepState.Done, step.State);
        Assert.Contains("photo-5be1", Encoding.UTF8.GetString(fresh.Engine.Files["/srv/photos"]));
    }

    [Fact]
    public async Task Restore_keeps_a_folder_with_files_and_replace_moves_them_aside_first()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var saved = new SavedHostFolder { Path = "/srv/photos", Part = "folder:/srv/photos" };
        host.Engine.Files["/srv/photos"] = BackupTestEngine.FolderTar("here-now");
        var kept = await restore.FillFolderAsync("/srv/photos", saved, new MemoryStream(BackupTestEngine.FolderTar("saved")), RestoreAction.Restore, "sha256:own", default);
        Assert.Contains("kept as it is", kept, StringComparison.Ordinal);
        Assert.Contains("here-now", Encoding.UTF8.GetString(host.Engine.Files["/srv/photos"]));

        await restore.FillFolderAsync("/srv/photos", saved, new MemoryStream(BackupTestEngine.FolderTar("saved")), RestoreAction.Replace, "sha256:own", default);
        Assert.Contains("saved", Encoding.UTF8.GetString(host.Engine.Files["/srv/photos"]));
        var before = Assert.Single(host.Engine.Files.Keys, k => k.StartsWith("/srv/photos.before-", StringComparison.Ordinal));
        Assert.Contains("here-now", Encoding.UTF8.GetString(host.Engine.Files[before]));

        // A copy's own folder must be new.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restore.FillFolderAsync("/srv/photos", saved, new MemoryStream(BackupTestEngine.FolderTar("saved")), RestoreAction.Copy, "sha256:own", default));
    }

    // ---- a run, end to end ----

    [Fact]
    public async Task A_run_writes_a_sealed_file_keeps_the_newest_and_records_it()
    {
        using var rig = new AutoRig();
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        await rig.Auto.SaveAsync(new AutoBackupConfig { Schedule = AutoSchedule.Daily, At = "03:00", Folder = "/mnt/backups", Keep = 2 }, null, null);
        foreach (var old in new[] { "gitberth-auto-homelab-20261101-030000.gbak", "gitberth-auto-homelab-20261102-030000.gbak", "gitberth-auto-other-20261101-030000.gbak" })
            rig.Destination.Files[old] = [1, 2, 3];

        var run = await rig.Auto.RunAsync(manual: true, default);
        Assert.True(run.Ok, run.Problem);
        Assert.Equal(["gitberth-auto-homelab-20261102-030000.gbak", run.File!, "gitberth-auto-other-20261101-030000.gbak"], rig.Destination.Files.Keys.Order(StringComparer.Ordinal));
        var file = rig.Destination.Files[run.File!];
        Assert.Equal(file.Length, run.Bytes);
        var (header, _) = await BackupReader.ReadHeaderAsync(new MemoryStream(file));
        Assert.Equal((await rig.Auto.ConfigAsync()).Key!.ShortId, header.ShortKeyId);
        Assert.NotEmpty(await BackupReader.VerifyAsync(new MemoryStream(file), Passphrase));

        var status = await rig.Auto.StatusAsync();
        Assert.True(status.State.Runs[0].Ok);
        Assert.Equal(run.At, status.State.LastOk);
        Assert.Contains(rig.Audit.Entries, e => e.Action == "backup.auto.saved");
        Assert.DoesNotContain(rig.Audit.Entries, e => e.Action == "backup.saved");
    }

    [Fact]
    public async Task A_failing_destination_is_told_once_shown_until_a_backup_works_and_leaves_nothing()
    {
        using var rig = new AutoRig();
        await rig.TellAsync();
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        await rig.Auto.SaveAsync(new AutoBackupConfig { Schedule = AutoSchedule.Daily, At = "03:00", Folder = "/mnt/backups" }, null, null);
        rig.Destination.Failure = "Writing to /mnt/backups failed: no space left on device.";

        var first = await rig.Auto.RunAsync(manual: false, default);
        Assert.False(first.Ok);
        Assert.Equal("Writing to /mnt/backups failed: no space left on device.", first.Problem);
        await rig.Auto.RunAsync(manual: false, default);
        var told = rig.Notices();
        Assert.Equal([NotifyEvent.BackupFailed], told.Select(n => n.Event));
        Assert.Contains("tries again", told[0].Text, StringComparison.Ordinal);
        Assert.Contains(rig.Board.Items, i => i.Key == AutoBackups.ItemKey);
        Assert.Empty(rig.Destination.Files);
        Assert.Equal(2, rig.Audit.Entries.Count(e => e.Action == "backup.auto.failed"));

        rig.Destination.Failure = null;
        Assert.True((await rig.Auto.RunAsync(manual: false, default)).Ok);
        Assert.Equal([NotifyEvent.BackupOk], rig.Notices().Select(n => n.Event));
        Assert.DoesNotContain(rig.Board.Items, i => i.Key == AutoBackups.ItemKey);
    }

    [Fact]
    public async Task A_save_that_fails_half_way_leaves_nothing_at_the_destination()
    {
        using var rig = new AutoRig();
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        await rig.Auto.SaveAsync(new AutoBackupConfig { Folder = "/mnt/backups" }, null, null);
        rig.Host.Engine.AddContainer("web", "nginx:1.29", "sha256:web", volumes: ["web-data:/usr/share/nginx/html"]);
        rig.Host.Engine.AddImage("sha256:web", "nginx:1.29");
        rig.Host.Engine.Failing["get "] = "the volume went away";
        var run = await rig.Auto.RunAsync(manual: true, default);
        Assert.False(run.Ok);
        Assert.Contains("the volume went away", run.Problem, StringComparison.Ordinal);
        Assert.Empty(rig.Destination.Files);
    }

    [Fact]
    public async Task Back_up_now_waits_for_nothing_but_refuses_while_a_restore_runs_or_nothing_is_set()
    {
        using var rig = new AutoRig();
        Assert.Contains("passphrase", await rig.Auto.BackUpNowAsync(), StringComparison.Ordinal);
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        Assert.NotNull(await rig.Auto.BackUpNowAsync());
        await rig.Auto.SaveAsync(new AutoBackupConfig { Folder = "/mnt/backups" }, null, null);
        rig.Host.Gate.Close();
        Assert.Contains("restore", await rig.Auto.BackUpNowAsync(), StringComparison.Ordinal);
        rig.Host.Gate.Open();
        Assert.Null(await rig.Auto.BackUpNowAsync());
        await Eventually.UntilAsync(() => rig.Destination.Files.Count == 1 && !rig.Auto.Running, "Back up now's backup to be written");
        var run = (await rig.Auto.StatusAsync()).State.Runs[0];
        Assert.True(run.Ok && run.Manual, run.Problem);
    }

    [Fact]
    public async Task Secrets_are_kept_encrypted_kept_when_left_empty_and_a_schedule_needs_a_passphrase()
    {
        using var rig = new AutoRig();
        var s3 = new AutoBackupConfig
        {
            Where = AutoDestinationKind.S3, S3Endpoint = "http://minio:9000", S3Region = "us-east-1", S3Bucket = "backups", S3AccessKey = "gitberth",
        };
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Auto.SaveAsync(s3, null, null));
        await rig.Auto.SaveAsync(s3, null, "s3cr3t-api-key");
        var json = await rig.Settings.GetAsync(AutoBackups.ConfigKey);
        Assert.DoesNotContain("s3cr3t-api-key", json, StringComparison.Ordinal);
        await rig.Auto.SaveAsync(s3 with { S3Prefix = "gitberth" }, null, null);
        Assert.Equal("s3cr3t-api-key", rig.Host.Protector.Unprotect((await rig.Auto.ConfigAsync()).S3Secret!));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Auto.SaveAsync(s3 with { Schedule = AutoSchedule.Daily }, null, null));
        Assert.DoesNotContain(rig.Audit.Entries, e => e.Summary.Contains("s3cr3t-api-key", StringComparison.Ordinal)
            || e.Changes.Any(c => c.To?.Contains("s3cr3t-api-key", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Test_the_destination_writes_lists_reads_back_and_removes_a_small_file()
    {
        using var rig = new AutoRig();
        var test = await rig.Auto.TestAsync(new AutoBackupConfig { Folder = "/mnt/backups" }, null, null);
        Assert.True(test.Ok, test.Problem);
        Assert.Equal(4, test.Steps.Count);
        Assert.Empty(rig.Destination.Files);
        rig.Destination.Failure = "Writing to /mnt/backups failed: permission denied.";
        var failed = await rig.Auto.TestAsync(new AutoBackupConfig { Folder = "/mnt/backups" }, null, null);
        Assert.False(failed.Ok);
        Assert.Equal("Writing to /mnt/backups failed: permission denied.", failed.Problem);
        Assert.False((await rig.Auto.TestAsync(new AutoBackupConfig { Folder = "relative" }, null, null)).Ok);
    }

    [Fact]
    public async Task An_automatic_backup_is_fetched_into_the_upload_and_opens_with_its_passphrase()
    {
        using var rig = new AutoRig();
        await rig.Auto.SetPassphraseAsync(Passphrase, Passphrase);
        await rig.Auto.SaveAsync(new AutoBackupConfig { Folder = "/mnt/backups" }, null, null);
        var run = await rig.Auto.RunAsync(manual: true, default);
        var listed = Assert.Single(await rig.Auto.ListAsync());
        Assert.Equal((run.File, "homelab", (await rig.Auto.ConfigAsync()).Key!.ShortId), (listed.Name, listed.Host, listed.KeyId));

        Assert.Equal(listed.Bytes, await rig.Auto.FetchAsync(listed.Name, listed.Bytes));
        var check = await rig.Restore.CheckAsync(Passphrase);
        Assert.Equal(UploadProblem.None, check.Problem);
        Assert.Equal(UploadProblem.WrongPassphrase, (await rig.Restore.CheckAsync("not the passphrase")).Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Auto.FetchAsync("notes.txt", 1));
    }

    /// <summary>A stream that can only be read forward, as a destination's download is.</summary>
    private sealed class OneWay(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A destination in memory: files by name, and a failure the test sets, refused before anything is kept.</summary>
internal sealed class MemoryDestination : IBackupDestination
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public string? Failure { get; set; }

    public string Where => "the folder /mnt/backups on this host";

    public async Task WriteAsync(string name, Stream content, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, ct);
        if (Failure is { } failure) throw new DestinationException(failure);
        Files[name] = bytes.ToArray();
    }

    public Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DestinationFile>>([.. Files.Where(f => AutoDestinations.Ours(f.Key))
            .Select(f => new DestinationFile(f.Key, f.Value.Length, null, headers ? f.Value.Take(64).ToArray() : null))]);

    public async Task ReadAsync(string name, Stream into, CancellationToken ct) => await into.WriteAsync(Files[name], ct);

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        Files.Remove(name);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Automatic backups over a BackupHost (its engine, database and backup service), with a destination in memory.</summary>
internal sealed class AutoRig : IDisposable
{
    private readonly bool _owns;

    public AutoRig()
    {
        _owns = true;
        Host = new BackupHost();
        Clock = new ImageKit.Clock(new DateTimeOffset(2026, 11, 27, 3, 0, 0, TimeSpan.Zero));
        Destination = new MemoryDestination();
        Settings = new SettingsService(Host.Db, Host.Protector);
        Backup = Host.Backup();
        Restore = Host.Restore();
        Auto = Make();
    }

    /// <summary>Gitberth started again on the same host, destination and clock.</summary>
    public AutoRig(AutoRig before)
    {
        (Host, Clock, Destination, Settings, Backup, Restore) = (before.Host, before.Clock, before.Destination, before.Settings, before.Host.Backup(), before.Host.Restore());
        Auto = Make();
    }

    private AutoBackups Make()
    {
        Notify = new NotificationSettings(Settings);
        Notifier = new Notifier(Notify, new NotificationSender(new ImageKit.NoHttp(), new SmtpMailer()), Settings, NullLogger<Notifier>.Instance);
        var destinations = new AutoDestinations(Host.Engine, new DockerCli(Host.Options), Host.Protector, new ImageKit.NoHttp(), Host.Options) { Open = _ => Destination };
        return new AutoBackups(Backup, Restore, Host.Gate, Settings, Host.Protector, destinations, Host.Engine, Notifier, Board,
            NullLogger<AutoBackups>.Instance, Audit, Clock) { Iterations = 1000 };
    }

    public BackupHost Host { get; }
    public ImageKit.Clock Clock { get; }
    public MemoryDestination Destination { get; }
    public SettingsService Settings { get; }
    public BackupService Backup { get; }
    public RestoreService Restore { get; }
    public AttentionBoard Board { get; } = new();
    public AuditLog Audit { get; } = new();
    public NotificationSettings Notify { get; private set; } = null!;
    public Notifier Notifier { get; private set; } = null!;
    public AutoBackups Auto { get; }

    /// <summary>A destination for notices, so they're queued rather than dropped as unwanted.</summary>
    public Task TellAsync() => Notify.SaveDestinationAsync(new Destination { Id = "d1", Kind = DestinationKind.Webhook, Name = "Ops", Url = "https://hooks.example.com/gitberth" });

    public List<Notice> Notices()
    {
        var list = new List<Notice>();
        while (Notifier.Queued.TryRead(out var n)) list.Add(n);
        return list;
    }

    public void Dispose()
    {
        Audit.Dispose();
        if (_owns) Host.Dispose();
    }
}
