using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Workbench;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// The terminal's way in (B3): one-time tickets (once, 30 seconds, only for whom they were given), the WebSocket's Origin
/// check, and what the page says back.
/// </summary>
public sealed class TerminalTests
{
    [Fact]
    public void A_ticket_is_good_once_for_whom_it_was_given_and_for_thirty_seconds()
    {
        var clock = new FakeClock();
        var tickets = new WorkbenchTickets(clock);
        var details = new TerminalDetails("orb", 1, null, false, 120, 32);

        var one = tickets.Issue(TicketKind.Terminal, "admin", "gb-orb", "gb-orb", details);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", one);
        Assert.Null(tickets.Take(one, TicketKind.Terminal, "someone-else"));
        // Taken by the wrong one, it's gone for the right one too: a ticket is never tried twice.
        Assert.Null(tickets.Take(one, TicketKind.Terminal, "admin"));

        var two = tickets.Issue(TicketKind.Terminal, "admin", "gb-orb", "gb-orb", details);
        Assert.Null(tickets.Take(two, TicketKind.Download, "admin"));
        var three = tickets.Issue(TicketKind.Terminal, "admin", "gb-orb", "gb-orb", details);
        var taken = tickets.Take(three, TicketKind.Terminal, "admin");
        Assert.Equal(("gb-orb", details), (taken!.Container, (TerminalDetails)taken.Details));
        Assert.Null(tickets.Take(three, TicketKind.Terminal, "admin"));

        var late = tickets.Issue(TicketKind.Upload, "admin", "gb-orb", "gb-orb", details);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(tickets.Take(late, TicketKind.Upload, "admin"));
        Assert.Null(tickets.Take(null, TicketKind.Upload, "admin"));
        Assert.Null(tickets.Take(new string('x', 200), TicketKind.Upload, "admin"));
    }

    private static HttpRequest Request(string host, string? origin, string? forwardedHost = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        return context.Request;
    }

    [Fact]
    public void Only_gitberths_own_pages_open_a_terminal()
    {
        Assert.True(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "http://10.1.1.80:8080")));
        Assert.True(TerminalEndpoint.SameOrigin(Request("gitberth.example.com", "https://gitberth.example.com")));
        Assert.True(TerminalEndpoint.SameOrigin(Request("GITBERTH.example.com", "https://gitberth.example.com")));
        Assert.True(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "https://gitberth.example.com", forwardedHost: "gitberth.example.com")));
        Assert.False(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "http://evil.example")));
        Assert.False(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "http://10.1.1.80:9999")));
        Assert.False(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", null)));
        Assert.False(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "null")));
        Assert.False(TerminalEndpoint.SameOrigin(Request("10.1.1.80:8080", "file://10.1.1.80:8080")));
    }

    [Fact]
    public void A_resize_is_read_and_a_refusal_is_said_in_a_sentence()
    {
        Assert.Equal((100, 30), TerminalSessions.Resize("""{"type":"resize","cols":100,"rows":30}"""));
        Assert.Equal((1000, 500), TerminalSessions.Resize("""{"type":"resize","cols":99999,"rows":99999}"""));
        Assert.Null(TerminalSessions.Resize("""{"type":"resize","cols":0,"rows":30}"""));
        Assert.Null(TerminalSessions.Resize("""{"type":"input","data":"ls"}"""));
        Assert.Null(TerminalSessions.Resize("not json"));

        Assert.Equal("gb-orb isn't running, so it has no terminal now. Start it, then open the terminal again.",
            TerminalSessions.Refusal("gb-orb", "Container 4f2a is not running"));
        Assert.Equal("There's no shell in gb-orb's image (no /bin/sh or bash), so it has no terminal.",
            TerminalSessions.Refusal("gb-orb", "OCI runtime exec failed: exec: \"/bin/sh\": stat /bin/sh: no such file or directory: unknown"));
        Assert.Equal("Docker couldn't open a shell in gb-orb: something else.", TerminalSessions.Refusal("gb-orb", "something else."));
    }

    [Fact]
    public void Gitberths_own_containers_have_no_terminal_and_no_files()
    {
        AppSummary Summary(Services.ContainerRole role, bool system) => new("k", 1, "k", "k", AppSource.Registry, role, ImageOrigin.Registry, "nginx", null,
            AppState.Running, "Running", "id", [], null, null, null, false, system, null);
        Assert.True(TerminalSessions.Offered(Summary(Services.ContainerRole.App, false)));
        Assert.False(TerminalSessions.Offered(Summary(Services.ContainerRole.Self, true)));
        Assert.False(TerminalSessions.Offered(Summary(Services.ContainerRole.Helper, false)));
        Assert.False(TerminalSessions.Offered(Summary(Services.ContainerRole.App, true)));
    }
}

/// <summary>
/// An app's files (B3): paths and names as Gitberth makes them, what's offered (never Docker's socket or Gitberth's own
/// data), find's listing read, the helper container's body, and what each refusal says.
/// </summary>
public sealed class AppFilesTests
{
    [Fact]
    public void A_path_is_names_joined_and_never_leaves_the_mount()
    {
        Assert.Equal("", AppFiles.Normalize(null));
        Assert.Equal("", AppFiles.Normalize("/"));
        Assert.Equal("a/b/c", AppFiles.Normalize("/a//b/c/"));
        Assert.Null(AppFiles.Normalize("a/../../etc"));
        Assert.Null(AppFiles.Normalize(".."));
        Assert.Null(AppFiles.Normalize("a/./b"));
        Assert.Null(AppFiles.Normalize("a\0b"));
        Assert.Null(AppFiles.Normalize(new string('x', 300)));
        Assert.Equal(("a/b", "c.txt"), AppFiles.Split("a/b/c.txt"));
        Assert.Equal(("", "c.txt"), AppFiles.Split("c.txt"));

        Assert.Null(AppFiles.NameProblem("site.conf"));
        Assert.Equal("A name has no / in it.", AppFiles.NameProblem("a/b"));
        Assert.Equal(".. can't be a name.", AppFiles.NameProblem(".."));
        Assert.NotNull(AppFiles.NameProblem(" "));
        Assert.NotNull(AppFiles.NameProblem(".gitberth-upload-12"));
    }

    [Fact]
    public void A_containers_volumes_and_folders_are_offered_but_dockers_socket_gitberths_data_and_the_hosts_system_files()
    {
        var inspect = JsonNode.Parse("""
            {"Mounts": [
              {"Type": "volume", "Name": "gb-orb-data", "Source": "/var/lib/docker/volumes/gb-orb-data/_data", "Destination": "/data", "RW": true},
              {"Type": "bind", "Source": "/srv/media", "Destination": "/media", "RW": false},
              {"Type": "bind", "Source": "/var/run/docker.sock", "Destination": "/var/run/docker.sock", "RW": true},
              {"Type": "bind", "Source": "/var/lib/docker/volumes/gitberth-data/_data/configs/3/7", "Destination": "/etc/app.yml", "RW": false},
              {"Type": "bind", "Source": "/etc/localtime", "Destination": "/etc/localtime", "RW": false},
              {"Type": "bind", "Source": "/proc", "Destination": "/host/proc", "RW": false},
              {"Type": "tmpfs", "Source": "", "Destination": "/tmp", "RW": true}
            ]}
            """)!.AsObject();

        var mounts = AppFiles.MountsOf(inspect, "/var/lib/docker/volumes/gitberth-data/_data");

        Assert.Equal([
            new FileMount("gb-orb-data", "/var/lib/docker/volumes/gb-orb-data/_data", "/data", false),
            new FileMount(null, "/srv/media", "/media", true),
        ], mounts);
        Assert.Empty(AppFiles.MountsOf(null, null));
    }

    [Fact]
    public void Finds_listing_is_read_folders_first()
    {
        var output = Encoding.UTF8.GetBytes(
            "f\t12\t1764331200.5\t644\t101\t101\t\tnotes.txt\0" +
            "d\t4096\t1764331200\t755\t0\t0\t\tconf\0" +
            "l\t4\t1764331200\t777\t0\t0\t/etc\tescape\0" +
            "f\t3\t1764331200\t600\t0\t0\t\twith\ttab\0");

        var entries = AppFiles.Parse(output);

        Assert.Equal(["conf", "escape", "notes.txt", "with\ttab"], entries.Select(e => e.Name));
        Assert.Equal(new FileEntry("notes.txt", FileType.File, 12, DateTime.UnixEpoch.AddSeconds(1764331200.5), "0644", 101, 101, null), entries[2]);
        Assert.Equal((FileType.Link, "/etc"), (entries[1].Type, entries[1].LinkTarget));
        Assert.Equal(FileType.Folder, entries[0].Type);
    }

    [Fact]
    public void The_helper_has_only_the_one_mount_no_network_and_few_powers()
    {
        var body = AppFiles.HelperBody("sha256:abc", new FileMount("gb-orb-data", "/var/lib/docker/volumes/gb-orb-data/_data", "/data", false), write: false, ["/data"]);
        // Gitberth's own image's VOLUME gets a tmpfs: a helper never leaves an anonymous volume behind.
        Assert.Equal(["/data", "/tmp"], body["HostConfig"]!["Tmpfs"]!.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.True((bool)body["HostConfig"]!["AutoRemove"]!);
        // Never filled from the helper's own /mnt, nor given its owner: nocopy.
        Assert.Equal("gb-orb-data:/mnt:ro,nocopy", (string)body["HostConfig"]!["Binds"]![0]!);
        Assert.Equal("none", (string)body["HostConfig"]!["NetworkMode"]!);
        Assert.True((bool)body["NetworkDisabled"]!);
        Assert.Equal(["ALL"], body["HostConfig"]!["CapDrop"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain("SYS_ADMIN", body["HostConfig"]!["CapAdd"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)body["HostConfig"]!["ReadonlyRootfs"]!);
        Assert.Equal("files", (string)body["Labels"]![HostScanHelper.Label]!);
        var write = AppFiles.HelperBody("sha256:abc", new FileMount(null, "/srv/media", "/media", false), write: true);
        Assert.Equal("/srv/media:/mnt:rw", (string)write["HostConfig"]!["Binds"]![0]!);
        // The paths reach the scripts as arguments, never in them, and every script checks where they lead.
        Assert.All(new[] { AppFiles.ListScript, AppFiles.ReadScript, AppFiles.ArchiveScript, AppFiles.WriteScript, AppFiles.FolderScript, AppFiles.RenameScript, AppFiles.DeleteScript },
            s => Assert.Contains("realpath -m", s));
    }

    [Fact]
    public void Each_refusal_says_what_happened()
    {
        var mount = new FileMount("gb-orb-data", "/x", "/data", false);
        Assert.Equal("That path leads outside the volume (through a link), so Gitberth doesn't follow it.", AppFiles.Sentence(3, "outside the volume", "conf", mount));
        Assert.Equal("conf isn't there any more: the folder was changed meanwhile.", AppFiles.Sentence(4, "missing", "conf", mount));
        Assert.Equal("/data is a single file, not a folder: Gitberth browses folders.", AppFiles.Sentence(7, "", "", mount));
        Assert.Equal("It didn't work: mv: cannot move.", AppFiles.Sentence(1, "mv: cannot move.\n", "a", mount));
        Assert.Equal(("12 B", "2 KiB", "1.5 MiB"), (AppFiles.Size(12), AppFiles.Size(2048), AppFiles.Size(1024 * 1536)));
    }

    [Fact]
    public void A_downloads_name_is_the_files_or_the_folders_with_tar_gz()
    {
        var of = new FilesOf("orb", 1, "gb-orb", new FileMount("gb-orb-data", "/x", "/data", false));
        Assert.Equal("notes.txt", FileEndpoints.DownloadName(new FileTicket(of, "conf/notes.txt", false)));
        Assert.Equal("conf.tar.gz", FileEndpoints.DownloadName(new FileTicket(of, "conf", true)));
        Assert.Equal("gb-orb-data.tar.gz", FileEndpoints.DownloadName(new FileTicket(of, "", true)));
        Assert.Equal("media.tar.gz", FileEndpoints.DownloadName(new FileTicket(of with { Mount = new FileMount(null, "/srv/media", "/media", false) }, "", true)));
    }
}

/// <summary>
/// Config files (B3): what a path and a mode may be, a save audited without its contents and written in place (a reader
/// that has the file open sees the new contents), the mounts a deploy gives, and a restored app's mounts made its own.
/// </summary>
public sealed class ConfigFileTests : IDisposable
{
    private readonly AppsRig _rig = new();
    private int? _orbId;

    // The rig's orb app (a Git app the rig's database starts with).
    private int OrbId => _orbId ??= RigApps.OrbOf(_rig);
    private readonly AuditLog _audit = new();
    private readonly ConfigFiles _configs;

    public ConfigFileTests()
    {
        var self = new SelfContainer(_rig.Cli, _rig.Options, NullLogger<SelfContainer>.Instance) { DataHostDir = "/var/lib/docker/volumes/gitberth-data/_data" };
        _configs = new ConfigFiles(_rig.Db, _rig.Protector, self, _rig.Options, NullLogger<ConfigFiles>.Instance, new Lazy<AppCatalog>(() => _rig.Catalog), _audit);
    }

    public void Dispose()
    {
        _audit.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public void A_path_and_a_mode_are_checked_and_kept_clean()
    {
        Assert.Null(ConfigFiles.PathProblem("/etc/nginx/conf.d/site.conf"));
        Assert.NotNull(ConfigFiles.PathProblem("etc/app.yml"));
        Assert.NotNull(ConfigFiles.PathProblem("/etc/app/"));
        Assert.NotNull(ConfigFiles.PathProblem("/etc/../root/.ssh/authorized_keys"));
        Assert.NotNull(ConfigFiles.PathProblem("/proc/sys/kernel/x"));
        Assert.NotNull(ConfigFiles.PathProblem("/etc/a:b"));
        Assert.Equal("/etc/app.yml", ConfigFiles.CleanPath("//etc//app.yml"));
        Assert.Equal((420, 384, 493), (ConfigFiles.ParseMode("644"), ConfigFiles.ParseMode("0600"), ConfigFiles.ParseMode("0755")));
        Assert.Null(ConfigFiles.ParseMode("888"));
        Assert.Null(ConfigFiles.ParseMode("4755"));
        Assert.Null(ConfigFiles.ParseMode("rw-r--r--"));
        Assert.Equal("0644", ConfigFiles.ModeText(420));
    }

    [Fact]
    public async Task A_change_is_written_in_place_audited_without_its_contents_and_mounted_by_the_next_deploy()
    {
        var added = await _configs.SaveAsync("gb-orb", null, new ConfigFileInput("/etc/app.yml", "token: s3cr3t-api-key\n", Secret: true, "0640", null));
        Assert.True(added.Ok, added.Message);
        Assert.Equal("Added /etc/app.yml: the app's next deploy mounts it.", added.Message);
        var file = ConfigFiles.FileOf(_rig.Options, OrbId, added.Id!.Value);
        Assert.Equal("token: s3cr3t-api-key\n", await File.ReadAllTextAsync(file));
        Assert.Equal($"/var/lib/docker/volumes/gitberth-data/_data/configs/{OrbId}/{added.Id}", added.HostPath);
        var entry = Assert.Single(_audit.Entries, e => e.Action == "app.config.added");
        Assert.DoesNotContain(entry.Changes!, c => (c.To ?? "").Contains("s3cr3t", StringComparison.Ordinal));
        // A secret file's contents are never shown again.
        var listed = Assert.Single(await _configs.ListAsync(OrbId));
        Assert.Equal((null, true, "0640"), (listed.Content, listed.Secret, listed.Mode));

        // New contents are written over the file a reader has open: the container that mounts it sees them at once.
        await using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var changed = await _configs.SaveAsync("gb-orb", added.Id, new ConfigFileInput("/etc/app.yml", "token: tok-9f8e7d6c5b4a\n", true, "0640", null));
            Assert.True(changed.Rewritten, changed.Message);
            Assert.Equal("Saved /etc/app.yml: the app sees the new contents at once.", changed.Message);
            using var text = new StreamReader(reader);
            Assert.Equal("token: tok-9f8e7d6c5b4a\n", await text.ReadToEndAsync());
        }
        // Left empty, a secret file keeps its contents; a path another file has is refused.
        Assert.Equal("Nothing changed.", (await _configs.SaveAsync("gb-orb", added.Id, new ConfigFileInput("/etc/app.yml", null, true, "640", null))).Message);
        var other = await _configs.SaveAsync("gb-orb", null, new ConfigFileInput("/etc/other.yml", "a: 1\n", false, "644", null));
        Assert.False((await _configs.SaveAsync("gb-orb", other.Id, new ConfigFileInput("/etc/app.yml", "a: 1\n", false, "644", null))).Ok);

        // A deploy mounts each, read-only, from where it is on the Docker host.
        var orb = (await _rig.RowAsync(a => a.Id == OrbId))!;
        var mounts = await _configs.MountsAsync(orb, null);
        Assert.Equal(["/etc/app.yml", "/etc/other.yml"], mounts.Select(m => m.Target));
        Assert.All(mounts, m => Assert.True(m.ReadOnly));
        var spec = ContainerSpecFactory.For(orb, "gitberth/orb:1d2948c", [], "gitberth", configMounts: mounts);
        var (args, _) = spec.BuildRunCommand();
        Assert.Contains($"{added.HostPath}:/etc/app.yml:ro", args);

        // Removed: its file goes with it (a Git app's container drops it at its next deploy).
        Assert.True((await _configs.RemoveAsync(added.Id.Value)).Ok);
        Assert.False(File.Exists(file));
        Assert.Single(_audit.Entries, e => e.Action == "app.config.removed");
    }

    [Fact]
    public void A_restored_apps_mounts_are_its_own_files_by_their_paths()
    {
        var rows = new[] { new ConfigFile { Id = 41, Path = "/etc/app.yml" } };
        var mounts = new List<MountSetting>
        {
            new(MountKind.Bind, "/var/lib/docker/volumes/gitberth-data/_data/configs/3/7", "/etc/app.yml", true),
            new(MountKind.Bind, "/srv/media", "/media", false),
            new(MountKind.Volume, "data", "/data", false),
        };

        var remapped = ConfigFiles.Remap(mounts, 12, rows);

        Assert.Equal("/var/lib/docker/volumes/gitberth-data/_data/configs/12/41", remapped[0].Source);
        Assert.Equal(mounts.Skip(1), remapped.Skip(1));
    }
}
