using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Backup;
using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Saving: the save list on CT 101 as the fixtures have it (Gitberth itself, its helpers, previews and a deploy's own
/// containers left out), and a save made into a stream: every part compressed and encrypted, each volume's users paused
/// for exactly its copy, a database dumped instead of copied, an image only this host has saved whole.
/// </summary>
public sealed class BackupServiceTests
{
    // CT 101's engine (AppsRig's), with what the Engine API stand-in doesn't answer: the volumes, their sizes, a registry.
    private sealed class RigEngine(DockerApi api, IReadOnlyList<string> volumes) : IBackupEngine
    {
        public Task<Version> ApiVersionAsync(CancellationToken ct = default) => api.ApiVersionAsync(ct);
        public Task<string> EngineVersionAsync(CancellationToken ct = default) => api.EngineVersionAsync(ct);
        public Task<JsonObject> InfoAsync(CancellationToken ct = default) => Task.FromResult(new JsonObject { ["Name"] = "ct101", ["Architecture"] = "x86_64" });
        public Task<List<RunningContainer>> ListAllAsync(CancellationToken ct = default) => api.ListAllAsync(ct);
        public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct = default) => api.InspectContainerAsync(idOrName, ct);
        public Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct = default) => api.InspectImageAsync(idOrName, ct);
        public Task<JsonArray> ListVolumesAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonArray([.. volumes.Select(v => (JsonNode?)new JsonObject { ["Name"] = v, ["Driver"] = "local" })]));
        public Task<IReadOnlyDictionary<string, long>> VolumeSizesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(volumes.ToDictionary(v => v, _ => 4096L));
        public Task<JsonArray> ListNetworksJsonAsync(CancellationToken ct = default) => api.ListNetworksJsonAsync(ct);
        public Task<string?> RegistryDigestAsync(string reference, CancellationToken ct = default) =>
            Task.FromResult(reference.StartsWith("postgres", StringComparison.Ordinal) ? "sha256:" + new string('d', 64) : null);
        public Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct = default) => throw Refused();
        public Task StartContainerAsync(string idOrName, CancellationToken ct = default) => throw Refused();
        public Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct = default) => throw Refused();
        public Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct = default) => throw Refused();
        public Task PauseContainerAsync(string idOrName, CancellationToken ct = default) => throw Refused();
        public Task UnpauseContainerAsync(string idOrName, CancellationToken ct = default) => throw Refused();
        public Task CreateVolumeAsync(JsonObject body, CancellationToken ct = default) => throw Refused();
        public Task RemoveVolumeAsync(string name, CancellationToken ct = default) => throw Refused();
        public Task<int> WaitContainerAsync(string idOrName, CancellationToken ct = default) => throw Refused();
        public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct = default) => throw Refused();
        public Task<Stream> GetArchiveAsync(string container, string path, CancellationToken ct = default) => throw Refused();
        public Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct = default) => throw Refused();
        public Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? env, Stream? stdin, Stream? stdout,
            CancellationToken ct = default) => throw Refused();
        public Task<Stream> SaveImagesAsync(IReadOnlyList<string> names, CancellationToken ct = default) => throw Refused();
        public Task<string> LoadImagesAsync(Stream tar, CancellationToken ct = default) => throw Refused();
        public Task PullImageAsync(string fromImage, string tagOrDigest, string? registryAuth, CancellationToken ct = default) => throw Refused();
        public Task TagImageAsync(string image, string repository, string tag, CancellationToken ct = default) => throw Refused();
        private static InvalidOperationException Refused() => new("The save list changes nothing.");
    }

    [Fact]
    public async Task The_save_list_has_every_app_and_container_but_Gitberth_its_helpers_previews_and_a_deploys_own()
    {
        using var rig = new AppsRig();
        // A preview of orb with its container and volume, gitberth-buildkit, and the container a deploy kept aside.
        var preview = await rig.AddAsync(new AppDefinition { Name = "orb · PR #3", Slug = "orb-pr-3", Owner = "acme", Repo = "orb", ParentAppId = rig.ObsId });
        rig.Engine.AddLike("gb-orb", "gb-orb-pr-3", new Dictionary<string, string> { ["gitberth.app"] = "orb-pr-3" });
        rig.Engine.AddLike("sample-index", "gitberth-buildkit", new Dictionary<string, string> { ["gitberth.helper"] = "buildkit" });
        rig.Engine.AddLike("gb-orb", "gb-orb-previous");
        var engine = new RigEngine(rig.Api, ["postgres-data", "gitberth_gitberth-data", "gitberth-buildkit", "gb-orb-pr-3-data", "leftover-data"]);
        var service = new BackupService(engine, rig.Db, rig.Options, NullLogger<BackupService>.Instance);

        var items = await service.ItemsAsync();
        var ids = items.Select(i => i.Id).ToList();
        Assert.Equal("gitberth", ids[0]);
        Assert.True(items[0].Required && items[0].Included && items[0].Kind == BackupItemKind.GitberthData);
        Assert.Contains($"app:{rig.ObsId}", ids);
        Assert.Contains($"app:{rig.KanbanId}", ids);
        foreach (var container in new[] { "cloudflare-tunnel", "postgres", "sample-index", "sample-bun-hono", "sample-bun-elysia" })
            Assert.Contains("container:" + container, ids);
        foreach (var left in new[] { "container:gitberth", "container:gitberth-buildkit", "container:gb-orb-pr-3", "container:gb-orb-previous", $"app:{preview}",
                     "container:gb-orb", "container:gb-kanban", "volume:gitberth_gitberth-data", "volume:gitberth-buildkit", "volume:gb-orb-pr-3-data" })
            Assert.DoesNotContain(left, ids);

        // Postgres is stopped in the fixtures: it can't be dumped, so its volume's files are saved, both under it.
        var database = items.Single(i => i.Id == "database:postgres");
        Assert.Equal("container:postgres", database.Parent);
        Assert.Contains("stopped", database.Detail);
        Assert.Equal("container:postgres", items.Single(i => i.Id == "volume:postgres-data").Parent);
        Assert.Null(items.Single(i => i.Id == "volume:leftover-data").Parent);
        // postgres:18 is pulled again (a registry has it): no image row for it.
        Assert.DoesNotContain(ids, i => i.StartsWith("image:", StringComparison.Ordinal));

        // Children follow their parent; every row is ticked, and only Gitberth's data is required.
        for (var i = 0; i < items.Count; i++)
            if (items[i].Parent is { } parent)
                Assert.True(ids.IndexOf(parent) is var p && p >= 0 && p < i && items.Skip(p + 1).Take(i - p).All(x => x.Parent == parent), $"{items[i].Id} isn't under {parent}");
        Assert.All(items, i => Assert.True(i.Included));
        Assert.Single(items, i => i.Required);
    }

    [Fact]
    public async Task A_save_is_compressed_and_encrypted_and_pauses_each_volumes_users_for_its_copy_alone()
    {
        using var host = new BackupHost();
        host.Files("old-machine");
        var engine = host.Engine;
        engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        engine.AddImage("sha256:web", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        engine.AddImage("sha256:tool", "backup-agent:1.0");
        engine.Registry["postgres:18"] = "sha256:" + new string('a', 64);
        engine.Registry["nginx:1.29"] = "sha256:" + new string('b', 64);
        engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=s3cr3t-api-key"], volumes: ["db-data:/var/lib/postgresql"], networks: ["appnet"]);
        engine.AddContainer("web", "nginx:1.29", "sha256:web", volumes: ["web-data:/usr/share/nginx/html"], networks: ["appnet"], hostPort: 8081);
        engine.AddContainer("tool", "backup-agent:1.0", "sha256:tool", running: false);
        engine.AddVolume("web-data", "marker-5be1 web page");
        engine.AddVolume("db-data", "the database's own files");

        var service = host.Backup();
        var items = await service.ItemsAsync();
        Assert.Equal(["gitberth", "container:db", "database:db", "container:tool", "image:sha256:tool", "container:web", "volume:web-data"], items.Select(i => i.Id));
        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), StopEverything: false), BackupHost.Passphrase);
        Assert.EndsWith(".gbak", ticket.FileName);
        Assert.Equal("Waiting for the download", service.Progress(ticket.Id)!.Phase);
        var job = service.Claim(ticket.Id)!;
        Assert.Null(service.Claim(ticket.Id));
        var file = new MemoryStream();
        await service.WriteAsync(job, file, default);
        var bytes = file.ToArray();

        // The page's progress line says it ended, and keeps saying so.
        var done = service.Progress(ticket.Id)!;
        Assert.True(done.Done);
        Assert.Null(done.Problem);
        Assert.Equal("Saved", done.Phase);
        Assert.Equal(done.ItemsTotal, done.ItemsDone);
        Assert.Equal(bytes.Length, done.BytesWritten);

        // web was paused for its volume's copy alone, through a helper made (never started) and removed; db was dumped, not
        // paused or copied; the stopped tool's image only this host has is saved whole.
        var calls = engine.Calls;
        Assert.Contains("exec db pg_dumpall", calls);
        Assert.DoesNotContain(calls, c => c.Contains("(db-data)", StringComparison.Ordinal) || c == "pause db");
        var pause = calls.IndexOf("pause web");
        var get = calls.FindIndex(c => c.StartsWith("get gitberth-helper-backup-", StringComparison.Ordinal) && c.EndsWith("(web-data)", StringComparison.Ordinal));
        var unpause = calls.IndexOf("unpause web");
        Assert.True(pause >= 0 && pause < get && get < unpause, string.Join(" | ", calls));
        Assert.Contains(calls, c => c.StartsWith("rm gitberth-helper-backup-", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("start ", StringComparison.Ordinal));
        Assert.Contains("save backup-agent:1.0", calls);
        Assert.DoesNotContain(engine.Containers.Keys, n => n.StartsWith("gitberth-helper", StringComparison.Ordinal));
        Assert.False(File.Exists(host.Paths.PausedList));

        // Nothing of it can be read in the file: not the marker, the password, a dump's row, a name.
        foreach (var plain in new[] { "marker-5be1", "s3cr3t-api-key", "row-7f3a", "web-data", "backup-agent", "old-machine", "homelab", "gitberth.db" })
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plain)) < 0, $"\"{plain}\" can be read in the file");

        // Opened with the passphrase: the manifest first, then Gitberth's data, the dump, the volume, the image.
        using var reader = await BackupReader.OpenAsync(new MemoryStream(bytes), BackupHost.Passphrase);
        var parts = new List<(BackupPartKind Kind, string Name, BackupCodec Codec)>();
        BackupManifest? manifest = null;
        while (await reader.NextPartAsync() is { } part)
        {
            parts.Add((part.Kind, part.Name, part.Codec));
            if (part.Kind == BackupPartKind.Manifest)
            {
                var json = new MemoryStream();
                await part.Content.CopyToAsync(json);
                manifest = BackupManifest.Parse(json.ToArray());
            }
        }
        Assert.Equal(["manifest.json", "data/gitberth.db", "data/keys", "data/compose", "data/logs", "dump/db", "volume/web-data", "image/sha256:tool"],
            parts.Select(p => p.Name));
        // Every part that shrinks is compressed: the manifest, the database, the dump, the volume's tar.
        Assert.All(parts.Where(p => p.Name is "manifest.json" or "data/gitberth.db" or "dump/db" or "volume/web-data"), p => Assert.Equal(BackupCodec.Brotli, p.Codec));

        Assert.NotNull(manifest);
        Assert.Equal("homelab", manifest.Host);
        Assert.Equal("x86_64", manifest.Architecture);
        Assert.Contains("brotli", manifest.Compression);
        Assert.Equal(ComesBack.Database, manifest.Containers.Single(c => c.Name == "db").How);
        Assert.Equal(["appnet"], manifest.Networks.Select(n => n.Name));
        var db = Assert.Single(manifest.Databases);
        Assert.Equal(["db-data"], db.DataVolumes);
        Assert.DoesNotContain("s3cr3t-api-key", db.Command);
        Assert.Null(manifest.Volumes.Single(v => v.Name == "db-data").Part);
        Assert.Equal("volume/web-data", manifest.Volumes.Single(v => v.Name == "web-data").Part);
        Assert.Equal("postgres@sha256:" + new string('a', 64), manifest.Images.Single(i => i.Id == "sha256:pg").Digest);
        Assert.Equal("image/sha256:tool", manifest.Images.Single(i => i.Id == "sha256:tool").Part);
        Assert.Contains(manifest.Plan, s => s.Id == "database:db");
    }

    [Fact]
    public async Task Stop_everything_writes_the_list_first_copies_the_databases_files_and_starts_exactly_those_again()
    {
        using var host = new BackupHost();
        var engine = host.Engine;
        engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        engine.Registry["postgres:18"] = "sha256:" + new string('a', 64);
        engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=s3cr3t-api-key"], volumes: ["db-data:/var/lib/postgresql"]);
        engine.AddContainer("idle", "postgres:18", "sha256:pg", running: false);
        string? listed = null;
        engine.OnCall = call =>
        {
            if (call == "stop db") listed = File.ReadAllText(host.Paths.StoppedList);
        };
        var service = host.Backup();
        var items = await service.ItemsAsync();
        var job = service.Claim((await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), StopEverything: true), BackupHost.Passphrase)).Id)!;
        await service.WriteAsync(job, new MemoryStream(), default);

        Assert.Equal("[\"db\"]", listed);
        Assert.Equal(["stop db"], engine.Calls.Where(c => c.StartsWith("stop ", StringComparison.Ordinal)));
        // Stopped, it's not dumped: its files are copied, nothing paused, and it's started again at the end.
        Assert.DoesNotContain(engine.Calls, c => c.StartsWith("exec ", StringComparison.Ordinal) || c.StartsWith("pause ", StringComparison.Ordinal));
        Assert.Contains(engine.Calls, c => c.EndsWith("(db-data)", StringComparison.Ordinal));
        Assert.Equal("start db", engine.Calls[^1]);
        Assert.False(File.Exists(host.Paths.StoppedList));
    }

    [Fact]
    public async Task A_save_that_fails_says_why_ends_the_file_unfinished_and_unpauses_what_it_paused()
    {
        using var host = new BackupHost();
        var engine = host.Engine;
        engine.AddImage("sha256:web", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        engine.Registry["nginx:1.29"] = "sha256:" + new string('b', 64);
        engine.AddContainer("web", "nginx:1.29", "sha256:web", volumes: ["web-data:/data"]);
        engine.Failing["get gitberth-helper-backup-"] = "Docker can't read the volume";
        var service = host.Backup();
        var items = await service.ItemsAsync();
        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), false), BackupHost.Passphrase);
        var file = new MemoryStream();
        await Assert.ThrowsAsync<DockerApiException>(() => service.WriteAsync(service.Claim(ticket.Id)!, file, default));

        var progress = service.Progress(ticket.Id)!;
        Assert.True(progress.Done);
        Assert.Equal("Failed", progress.Phase);
        Assert.Contains("Docker can't read the volume", progress.Problem);
        Assert.Equal("unpause web", engine.Calls.Last(c => c.Contains("pause", StringComparison.Ordinal)));
        Assert.Contains(engine.Calls, c => c.StartsWith("rm gitberth-helper-backup-", StringComparison.Ordinal));
        // What was written is no backup anyone can restore.
        await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.VerifyAsync(new MemoryStream(file.ToArray()), BackupHost.Passphrase));
    }

    [Fact]
    public async Task A_short_passphrase_is_refused_and_a_ticket_is_downloaded_once()
    {
        using var host = new BackupHost();
        var service = host.Backup();
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartSaveAsync(new BackupChoice(new HashSet<string>(), false), "short"));
        Assert.Null(service.Progress("no-such-ticket"));
        var ticket = await service.StartSaveAsync(new BackupChoice(new HashSet<string>(), false), BackupHost.Passphrase);
        Assert.Equal(32, ticket.Id.Length);
        Assert.True(ticket.ExpiresAt > DateTime.UtcNow.AddMinutes(9));
        Assert.NotNull(service.Claim(ticket.Id));
        Assert.Null(service.Claim(ticket.Id));
    }

    // A host with a database and a web container, each on a volume of its own.
    private static BackupHost TwoContainers()
    {
        var host = new BackupHost();
        var engine = host.Engine;
        engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        engine.AddImage("sha256:web", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        engine.Registry["postgres:18"] = "sha256:" + new string('a', 64);
        engine.Registry["nginx:1.29"] = "sha256:" + new string('b', 64);
        engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=s3cr3t-api-key"], volumes: ["db-data:/var/lib/postgresql"]);
        engine.AddContainer("web", "nginx:1.29", "sha256:web", volumes: ["web-data:/data"]);
        return host;
    }

    private static async Task<SaveJob> ClaimAsync(BackupService service, bool stopEverything)
    {
        var items = await service.ItemsAsync();
        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), stopEverything), BackupHost.Passphrase);
        return service.Claim(ticket.Id)!;
    }

    private static bool Running(BackupTestEngine engine, string name) => (bool)engine.Containers[name]["State"]!["Running"]!;
    private static bool Paused(BackupTestEngine engine, string name) => (bool)engine.Containers[name]["State"]!["Paused"]!;

    [Fact]
    public async Task A_stop_or_a_pause_cut_short_is_undone_and_its_list_stays_until_everything_is_back()
    {
        // The download stops while Docker stops db: db is stopped all the same, so it's started again.
        using (var host = TwoContainers())
        {
            var service = host.Backup();
            var job = await ClaimAsync(service, stopEverything: true);
            using var stop = new CancellationTokenSource();
            host.Engine.OnCall = call =>
            {
                if (call == "stop db") stop.Cancel();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WriteAsync(job, new MemoryStream(), stop.Token));
            Assert.True(host.Engine.Calls.IndexOf("start db") > host.Engine.Calls.IndexOf("stop db"), string.Join(" | ", host.Engine.Calls));
            Assert.True(Running(host.Engine, "db"));
            Assert.False(File.Exists(host.Paths.StoppedList));
        }

        // The download stops while Docker pauses web: web is frozen all the same, so it's unpaused again.
        using (var host = TwoContainers())
        {
            var service = host.Backup();
            var job = await ClaimAsync(service, stopEverything: false);
            using var stop = new CancellationTokenSource();
            host.Engine.OnCall = call =>
            {
                if (call == "pause web") stop.Cancel();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WriteAsync(job, new MemoryStream(), stop.Token));
            Assert.True(host.Engine.Calls.IndexOf("unpause web") > host.Engine.Calls.IndexOf("pause web"), string.Join(" | ", host.Engine.Calls));
            Assert.False(Paused(host.Engine, "web"));
            Assert.False(File.Exists(host.Paths.PausedList));
        }

        // db won't start again after the save: its list stays, and Gitberth's next start brings it back.
        using (var host = TwoContainers())
        {
            var service = host.Backup();
            var job = await ClaimAsync(service, stopEverything: true);
            host.Engine.Failing["start db"] = "the port is taken";
            await service.WriteAsync(job, new MemoryStream(), default);
            Assert.False(Running(host.Engine, "db"));
            Assert.True(Running(host.Engine, "web"));
            Assert.True(File.Exists(host.Paths.StoppedList));
            await host.Backup().RecoverAsync(default);
            Assert.True(Running(host.Engine, "db"));
            Assert.False(File.Exists(host.Paths.StoppedList));
        }
    }

    // A download the browser takes its time with: the first write waits until it's let go.
    private sealed class HeldStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            await base.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task A_save_that_loses_to_another_wipes_its_key_and_none_holds_the_next_saves_up()
    {
        using var host = TwoContainers();
        var service = host.Backup();
        var first = await ClaimAsync(service, stopEverything: false);
        var second = await ClaimAsync(service, stopEverything: false);
        var held = new HeldStream();
        var running = Task.Run(() => service.WriteAsync(first, held, default));
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Another download meanwhile: refused, its key wiped, its progress saying why.
        var lost = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(second, new MemoryStream(), default));
        Assert.Contains("Another save", lost.Message);
        Assert.Null(second.Key);
        var said = service.Progress(second.Id)!;
        Assert.True(said.Done);
        Assert.Contains("Another save", said.Problem);
        held.Release.SetResult();
        await running;
        Assert.Equal("Saved", service.Progress(first.Id)!.Phase);

        // A ticket whose key went: refused, and the next save isn't held up by it.
        var expired = await ClaimAsync(service, stopEverything: false);
        expired.Key!.Dispose();
        expired.Key = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(expired, new MemoryStream(), default));
        Assert.False(service.Saving);
        await service.WriteAsync(await ClaimAsync(service, stopEverything: false), new MemoryStream(), default);
    }
}
