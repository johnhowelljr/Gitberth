using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Sources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Uploaded folders (B6): an archive is unpacked only when it's safe (no path out of its folder, no link out of it followed
/// as the system would, nothing written through a link, no devices, every entry its declared size, the limits), a folder
/// upload leaves out what the build makes again and packs to the same archive for the same files, the store keeps the
/// newest five, and a deploy of an uploaded app unpacks its upload and builds it, with the upload as its commit.
/// </summary>
public sealed class UploadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-upload-" + Guid.NewGuid().ToString("N")[..8]);

    public UploadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- archives made for the tests ----

    private string Zip(string name, params (string Path, string Text)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, text) in entries)
        {
            var entry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);
            if (entryPath.EndsWith('/')) continue;
            using var w = new StreamWriter(entry.Open());
            w.Write(text);
        }
        return path;
    }

    private sealed record T(TarEntryType Type, string Name, string? Text = null, string? Link = null);

    private string Tar(string name, bool gzip, params T[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        using Stream sink = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file;
        using var tar = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var e in entries)
        {
            var entry = new PaxTarEntry(e.Type, e.Name) { Mode = (UnixFileMode)0x1A4 };
            if (e.Link is not null) entry.LinkName = e.Link;
            if (e.Text is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(e.Text));
            tar.WriteEntry(entry);
        }
        return path;
    }

    private string Out(string name) => Path.Combine(_dir, name);

    private async Task<UploadRefusedException> RefusedAsync(string archive, UploadKind kind, UnpackLimits? limits = null)
    {
        var target = Out("refused-" + Guid.NewGuid().ToString("N")[..6]);
        var refused = await Assert.ThrowsAsync<UploadRefusedException>(() => UploadArchives.UnpackAsync(archive, kind, target, CancellationToken.None, limits));
        // Nothing of a refused upload stays.
        Assert.False(Directory.Exists(target));
        return refused;
    }

    // ---- what's taken ----

    [Fact]
    public void Its_kind_and_name_come_from_the_file_name()
    {
        Assert.Equal(UploadKind.Zip, UploadArchives.KindOf("site.ZIP"));
        Assert.Equal(UploadKind.TarGz, UploadArchives.KindOf("app.tar.gz"));
        Assert.Equal(UploadKind.TarGz, UploadArchives.KindOf("app.tgz"));
        Assert.Equal(UploadKind.Tar, UploadArchives.KindOf("app.tar"));
        Assert.Null(UploadArchives.KindOf("app.rar"));
        Assert.Null(UploadArchives.KindOf(null));
        Assert.Equal("site", UploadArchives.NameOf("site.zip"));
        Assert.Equal("my-app", UploadArchives.NameOf("my-app.tar.gz"));
        Assert.Equal("todo-api", UploadArchives.NameOf("todo-api"));
        Assert.Equal("upload", UploadStore.CleanName(""));
        Assert.Equal("site.zip", UploadStore.CleanName("C:\\Users\\me\\site.zip"));
    }

    [Fact]
    public async Task A_zip_with_one_top_folder_has_it_as_its_root()
    {
        var zip = Zip("site.zip", ("site/", ""), ("site/index.html", "<h1>hi</h1>"), ("site/css/app.css", "body{}"), ("__MACOSX/site/._index.html", "junk"));
        var target = Out("site");

        var unpacked = await UploadArchives.UnpackAsync(zip, UploadKind.Zip, target, CancellationToken.None);

        Assert.Equal(Path.Combine(target, "site"), unpacked.Root);
        Assert.Equal(2, unpacked.Files);
        Assert.Equal("<h1>hi</h1>", File.ReadAllText(Path.Combine(unpacked.Root, "index.html")));
        Assert.True(File.Exists(Path.Combine(unpacked.Root, "css", "app.css")));
        // macOS's leftovers are never unpacked.
        Assert.False(Directory.Exists(Path.Combine(target, "__MACOSX")));
    }

    [Fact]
    public void Unpacked_files_get_a_checkouts_modes()
    {
        // 0600 (as Python's zipfile writes them) is readable by anyone, as a git checkout is; an executable stays one.
        Assert.Equal(0x1A4, UploadArchives.CheckoutMode(0x180));
        Assert.Equal(0x1A4, UploadArchives.CheckoutMode(0x1B6));
        Assert.Equal(0x1ED, UploadArchives.CheckoutMode(0x1C0));
        Assert.Equal(0x1ED, UploadArchives.CheckoutMode(0x9ED));
        Assert.Equal(0x1A4, UploadArchives.CheckoutMode(0xDA4));
        Assert.Equal(0x1ED, UploadArchives.CheckoutFolderMode);
    }

    [Fact]
    public async Task A_zip_of_loose_files_has_its_folder_as_its_root()
    {
        var zip = Zip("loose.zip", ("index.html", "<h1>hi</h1>"), ("about.html", "about"));
        var target = Out("loose");

        var unpacked = await UploadArchives.UnpackAsync(zip, UploadKind.Zip, target, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(unpacked.Root));
        Assert.Equal(2, unpacked.Files);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("site/../../evil.txt")]
    [InlineData("/etc/cron.d/evil")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("..\\evil.txt")]
    public async Task A_zip_entry_that_leaves_its_folder_refuses_the_whole_upload(string name)
    {
        var zip = Zip("slip.zip", ("index.html", "ok"), (name, "evil"));

        var refused = await RefusedAsync(zip, UploadKind.Zip);

        Assert.Contains("isn't unpacked", refused.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
    }

    [Fact]
    public async Task A_tar_link_out_of_its_folder_is_refused()
    {
        var tar = Tar("link.tar", gzip: false, new T(TarEntryType.RegularFile, "a.txt", "a"), new T(TarEntryType.SymbolicLink, "out", Link: "../../etc/passwd"));

        var refused = await RefusedAsync(tar, UploadKind.Tar);

        Assert.Contains("link", refused.Message);
    }

    [Fact]
    public async Task A_tar_link_that_only_leaves_through_another_link_is_refused_too()
    {
        // Read as text, here/.. is the folder itself; followed as the system follows it, here is the folder, so here/.. is
        // its parent: outside.
        var tar = Tar("chain.tar", gzip: false, new T(TarEntryType.SymbolicLink, "here", Link: "."), new T(TarEntryType.SymbolicLink, "esc", Link: "here/.."),
            new T(TarEntryType.RegularFile, "index.html", "ok"));

        var refused = await RefusedAsync(tar, UploadKind.Tar);

        Assert.Contains("esc", refused.Message);
    }

    [Fact]
    public async Task A_link_inside_its_folder_is_kept()
    {
        var tar = Tar("inside.tgz", gzip: true, new T(TarEntryType.Directory, "app/"), new T(TarEntryType.RegularFile, "app/real.txt", "real"),
            new T(TarEntryType.SymbolicLink, "app/alias.txt", Link: "real.txt"), new T(TarEntryType.HardLink, "app/copy.txt", Link: "app/real.txt"));
        var target = Out("inside");

        var unpacked = await UploadArchives.UnpackAsync(tar, UploadKind.TarGz, target, CancellationToken.None);

        Assert.Equal("real", File.ReadAllText(Path.Combine(unpacked.Root, "alias.txt")));
        // A hard link is a copy of the file it names, never a link to anything.
        var copy = new FileInfo(Path.Combine(unpacked.Root, "copy.txt"));
        Assert.Null(copy.LinkTarget);
        Assert.Equal("real", File.ReadAllText(copy.FullName));
    }

    [Fact]
    public async Task Nothing_is_written_through_a_link()
    {
        var tar = Tar("through.tar", gzip: false, new T(TarEntryType.Directory, "sub/"), new T(TarEntryType.SymbolicLink, "d", Link: "sub"),
            new T(TarEntryType.RegularFile, "d/x.txt", "x"));

        var refused = await RefusedAsync(tar, UploadKind.Tar);

        Assert.Contains("through the link", refused.Message);
    }

    [Theory]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    public async Task Devices_and_fifos_are_refused(TarEntryType type)
    {
        var tar = Tar("dev.tar", gzip: false, new T(type, "dev"));

        var refused = await RefusedAsync(tar, UploadKind.Tar);

        Assert.Contains("can't have", refused.Message);
    }

    [Fact]
    public async Task An_entry_that_holds_more_than_it_says_is_refused()
    {
        var zip = Zip("lie.zip", ("a.txt", "hello world, and then some"));
        // The central directory's uncompressed size says 3 bytes; the entry holds 26.
        var bytes = File.ReadAllBytes(zip);
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b || bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02) continue;
            BitConverter.GetBytes(3u).CopyTo(bytes, i + 24);
            break;
        }
        File.WriteAllBytes(zip, bytes);

        var refused = await RefusedAsync(zip, UploadKind.Zip);

        Assert.Contains("a.txt", refused.Message);
    }

    [Fact]
    public async Task The_limits_hold_entries_and_a_bombs_ratio()
    {
        var many = Zip("many.zip", ("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));
        var refused = await RefusedAsync(many, UploadKind.Zip, UnpackLimits.Default with { MaxEntries = 2 });
        Assert.Contains("more than 2 entries", refused.Message);

        // Highly compressible: a few KB of archive for a lot unpacked.
        var bomb = Path.Combine(_dir, "bomb.zip");
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("zeros.txt", CompressionLevel.SmallestSize).Open()))
            w.Write(new string('0', 2_000_000));
        var ratio = await RefusedAsync(bomb, UploadKind.Zip, UnpackLimits.Default with { RatioFloor = 1000, MaxRatio = 20 });
        Assert.Contains("zip bomb", ratio.Message);

        var big = await RefusedAsync(bomb, UploadKind.Zip, UnpackLimits.Default with { MaxUnpacked = 1000 });
        Assert.Contains("unpacks to more than", big.Message);
    }

    [Fact]
    public async Task A_damaged_archive_says_so()
    {
        var path = Path.Combine(_dir, "broken.zip");
        File.WriteAllText(path, "this isn't a zip");

        var refused = await RefusedAsync(path, UploadKind.Zip);

        Assert.Contains("damaged", refused.Message);
    }

    [Fact]
    public void Links_are_followed_as_the_system_follows_them()
    {
        var root = Out("resolve");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Assert.Equal(["a", "x"], UploadArchives.Resolve(root, ["a", "b"], "../x"));
        Assert.Null(UploadArchives.Resolve(root, ["a"], "../../x"));
        Assert.Null(UploadArchives.Resolve(root, [], "/etc"));
        Assert.Equal(["x"], UploadArchives.Resolve(root, [], "./x"));
    }

    // ---- a folder upload ----

    [Fact]
    public void A_folder_upload_leaves_out_what_the_build_makes_again()
    {
        Assert.True(UploadArchives.LeftOutOfFolder(["app", "node_modules", "x", "index.js"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", ".git", "HEAD"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", ".venv", "bin", "python"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", "venv", "x"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", "src", "__pycache__", "x.pyc"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", ".next", "cache", "x"]));
        Assert.True(UploadArchives.LeftOutOfFolder(["app", ".DS_Store"]));
        Assert.False(UploadArchives.LeftOutOfFolder(["app", ".next", "server", "x.js"]));
        Assert.False(UploadArchives.LeftOutOfFolder(["app", "src", "index.js"]));
    }

    private async Task<StagedUpload> FolderAsync(UploadStore store, params (string Path, string Text)[] files)
    {
        await using var folder = store.BeginFolder();
        foreach (var (path, text) in files) await folder.AddAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);
        return await folder.FinishAsync(CancellationToken.None);
    }

    private UploadStore Store() => new(new GitberthOptions { DataDir = Path.Combine(_dir, "data") }, NullLogger<UploadStore>.Instance);

    [Fact]
    public async Task A_folder_upload_is_packed_the_same_for_the_same_files()
    {
        var store = Store();
        var first = await FolderAsync(store, ("todo-api/package.json", "{}"), ("todo-api/server.js", "x"), ("todo-api/node_modules/a/index.js", "dep"));
        var second = await FolderAsync(store, ("todo-api/server.js", "x"), ("todo-api/package.json", "{}"));

        Assert.Equal(first.Sha, second.Sha);
        Assert.Equal("todo-api", first.Name);
        Assert.True(first.Folder);
        Assert.Equal(2, first.Files);
        // What it holds, unpacked: the folder's files, node_modules left out.
        var root = store.SourceOf(first);
        Assert.True(File.Exists(Path.Combine(root, "server.js")));
        Assert.False(Directory.Exists(Path.Combine(root, "node_modules")));
        Assert.Equal(first, store.Find(first.Id));
    }

    [Fact]
    public async Task A_folder_upload_with_a_path_out_of_it_is_refused()
    {
        var store = Store();
        var refused = await Assert.ThrowsAsync<UploadRefusedException>(() => FolderAsync(store, ("app/ok.txt", "ok"), ("app/../../evil.txt", "evil")));
        Assert.Contains("leads out of its folder", refused.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        // A refused folder leaves nothing staged.
        Assert.False(Directory.Exists(store.StagingRoot) && Directory.EnumerateDirectories(store.StagingRoot).Any());
    }

    [Fact]
    public async Task A_folder_upload_from_two_folders_is_refused()
    {
        var store = Store();
        var refused = await Assert.ThrowsAsync<UploadRefusedException>(() => FolderAsync(store, ("one/a.txt", "a"), ("two/b.txt", "b")));
        Assert.Contains("more than one folder", refused.Message);
    }

    [Fact]
    public async Task An_archive_is_received_hashed_and_unpacked()
    {
        var store = Store();
        var zip = Zip("site.zip", ("site/index.html", "<h1>hi</h1>"));
        await using var body = File.OpenRead(zip);

        var staged = await store.ReceiveArchiveAsync("site.zip", body, CancellationToken.None);

        Assert.Equal(await UploadArchives.ShaAsync(zip, CancellationToken.None), staged.Sha);
        Assert.Equal(new FileInfo(zip).Length, staged.Size);
        Assert.Equal("site", staged.AppName);
        Assert.True(File.Exists(Path.Combine(store.SourceOf(staged), "index.html")));
        await Assert.ThrowsAsync<UploadRefusedException>(() => store.ReceiveArchiveAsync("site.rar", new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    [Fact]
    public async Task An_app_keeps_its_newest_five_uploads_and_never_the_one_it_runs()
    {
        var store = Store();
        var kept = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var staged = await FolderAsync(store, ("app/v.txt", $"version {i}"));
            var path = store.Keep(5, staged, [kept.FirstOrDefault()]);
            // The newest is the newest by its time: each a minute apart, the next one kept now.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i));
            kept.Add(staged.Sha);
            Assert.Null(store.Find(staged.Id));
        }

        var left = store.KeptOf(5);
        // The newest five, and the first one, which the app "runs".
        Assert.Equal(6, left.Count);
        Assert.Contains(kept[0], left);
        Assert.DoesNotContain(kept[1], left);
        Assert.NotNull(store.ArchiveOf(5, kept[6]));
        Assert.Null(store.ArchiveOf(5, kept[1]));
        Assert.Null(store.ArchiveOf(5, "not-a-sha"));

        store.RemoveApp(5);
        Assert.Empty(store.KeptOf(5));
    }

    // ---- a deploy of an uploaded app ----

    private sealed class FakeDocker
    {
        public List<List<string>> Builds { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            lock (Builds)
            {
                if (args is ["build", ..])
                {
                    // What the build was given: its context (the last argument) still holds the upload's files now.
                    var context = args[^1];
                    Builds.Add([.. args, "files:" + string.Join(",", Directory.EnumerateFiles(context, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(context, f).Replace('\\', '/')).Order(StringComparer.Ordinal))]);
                    return Task.FromResult(new ProcessResult(1, "", "the test's docker builds nothing"));
                }
                return Task.FromResult(args is ["image", "inspect", ..] ? new ProcessResult(1, "", "Error: No such image") : new ProcessResult(0, "", ""));
            }
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Nothing here talks to GitHub or Cloudflare.");
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task An_uploaded_apps_deploy_unpacks_its_upload_and_builds_it_with_the_upload_as_its_commit()
    {
        using var db = new ImageKit.Db();
        var options = new GitberthOptions { DataDir = Path.Combine(_dir, "pipeline") };
        Directory.CreateDirectory(options.LogsDir);
        Directory.CreateDirectory(options.WorkDir);
        var store = new UploadStore(options, NullLogger<UploadStore>.Instance);
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(db, protector);
        var docker = new FakeDocker();
        var cli = new DockerCli(options) { Runner = docker.RunAsync };
        var http = new NoHttp();
        var domains = new DomainService(db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var pipeline = new DeployPipeline(db, new GitClient(options, settings), cli, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(), protector, options, http,
            new RepoVariables(new GitHubApi(http), settings), domains, new WatchState(), new Lifetime(), NullLogger<DeployPipeline>.Instance, uploads: store);

        // A static site, zipped with its folder: the folder is its root.
        var zip = Zip("site.zip", ("site/index.html", "<h1>hi</h1>"), ("site/Dockerfile", "FROM nginx:alpine\nCOPY . /usr/share/nginx/html\n"));
        await using var body = File.OpenRead(zip);
        var staged = await store.ReceiveArchiveAsync("site.zip", body, CancellationToken.None);
        var app = await db.AddAsync(new AppDefinition
        {
            Name = "site", Slug = "site", Source = AppSource.Upload, UploadName = "site.zip", UploadSha = staged.Sha, UploadSize = staged.Size,
            UploadedAt = DateTime.UtcNow, UploadedBy = "admin", AutoDeploy = false, VercelVariables = false,
        });
        store.Keep(app.Id, staged, []);
        int id;
        await using (var c = db.CreateDbContext())
        {
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Upload, CommitSha = staged.Sha };
            c.Deployments.Add(dep);
            await c.SaveChangesAsync();
            id = dep.Id;
        }

        await pipeline.RunAsync(new DeployRequest(app.Id, id, Build: true), CancellationToken.None);

        await using var after = db.CreateDbContext();
        var done = await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id);
        var log = await File.ReadAllTextAsync(DeployPaths.LogFile(options, id));
        Assert.Equal(staged.Sha, done.CommitSha);
        Assert.Equal("Upload of site.zip", done.CommitMessage);
        Assert.Equal("admin", done.CommitAuthor);
        Assert.Contains("Unpacking site.zip", log);
        Assert.Contains("from its folder site/", log);
        var build = Assert.Single(docker.Builds);
        // Its image is the app's, tagged with the upload's short SHA-256, built from the upload's files.
        Assert.Contains($"gitberth/site:{staged.Sha[..7]}", build);
        Assert.Contains("files:Dockerfile,index.html", build);
        // Nothing of a repo: no clone was asked for.
        Assert.DoesNotContain("Cloning", log);
    }

    [Fact]
    public async Task An_uploaded_apps_deploy_without_its_upload_says_so()
    {
        using var db = new ImageKit.Db();
        var options = new GitberthOptions { DataDir = Path.Combine(_dir, "pipeline-gone") };
        Directory.CreateDirectory(options.LogsDir);
        Directory.CreateDirectory(options.WorkDir);
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var settings = new SettingsService(db, protector);
        var cli = new DockerCli(options) { Runner = new FakeDocker().RunAsync };
        var http = new NoHttp();
        var domains = new DomainService(db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        var pipeline = new DeployPipeline(db, new GitClient(options, settings), cli, new ProjectAnalyzer(), new AppLocks(), new LiveLogHub(), protector, options, http,
            new RepoVariables(new GitHubApi(http), settings), domains, new WatchState(), new Lifetime(), NullLogger<DeployPipeline>.Instance);
        var app = await db.AddAsync(new AppDefinition { Name = "site", Slug = "site", Source = AppSource.Upload, UploadName = "site.zip", UploadSha = new string('a', 64) });
        int id;
        await using (var c = db.CreateDbContext())
        {
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Manual };
            c.Deployments.Add(dep);
            await c.SaveChangesAsync();
            id = dep.Id;
        }

        await pipeline.RunAsync(new DeployRequest(app.Id, id, Build: true), CancellationToken.None);

        await using var after = db.CreateDbContext();
        var done = await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.Equal(DeployStatus.Failed, done.Status);
        Assert.Contains("isn't kept here any more. Upload it again on its page.", done.Error);
    }

    [Fact]
    public void An_uploaded_app_is_deployed_and_built_but_not_from_a_repo()
    {
        var app = new AppDefinition { Source = AppSource.Upload };
        Assert.True(app.Deployed);
        Assert.True(app.Built);
        Assert.True(app.FromUpload);
        Assert.False(app.FromRepo);
        Assert.Equal("New upload", Format.Trigger(DeployTrigger.Upload));
        Assert.Contains("a new upload (abc1234)", Audit.AuditText.Queued("site", new Deployment { Trigger = DeployTrigger.Upload, CommitSha = "abc1234" + new string('0', 57) }));
        Assert.Equal(HookKind.Git, DeployHooks.KindOf(Summary(AppSource.Upload), app));
        Assert.Contains("curl -X POST -H \"Authorization: Bearer $GITBERTH_TOKEN\" -F file=@site.zip http://h:8080/api/v1/apps/gb-site/upload",
            UploadEndpoints.CurlLine("http://h:8080/", "gb-site", "site.zip"));
        // A name that isn't a plain archive's is never put on the line as it is.
        Assert.Contains("-F file=@site.zip ", UploadEndpoints.CurlLine("http://h:8080/", "gb-site", "a b;rm -rf.zip"));
    }

    private static AppSummary Summary(AppSource source) => new("gb-site", 1, "site", "gb-site", source, ContainerRole.App, ImageOrigin.Built, "gitberth/site:abc1234",
        null, AppState.Running, "Running", null, [], null, null, null, false, false, null);
}
