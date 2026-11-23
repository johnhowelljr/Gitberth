using System.Formats.Tar;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Backup;
using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// A Gitberth host for save and restore's tests: a data folder with its own database (no apps), its own engine
/// (<see cref="BackupTestEngine"/>) with Gitberth's own container in it, and the services made over them.
/// </summary>
internal sealed class BackupHost : IDisposable
{
    public const string Passphrase = "correct horse battery staple";
    public const string Self = "gitberth";

    private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    /// <summary>An application lifetime whose stopping a test sets off.</summary>
    public sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public int Stops { get; private set; }
        public void StopApplication() => Stops++;
    }

    public BackupHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitberth-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var self = Engine.AddContainer(Self, "gitberth:local", "sha256:own", volumes: ["gitberth_gitberth-data:/data"]);
        Options = new GitberthOptions { DataDir = dir, RunningInContainer = true, OwnContainer = ((string)self["Id"]!)[..12] };
        Db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Options.DbPath}").Options);
        using var db = Db.CreateDbContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public BackupTestEngine Engine { get; } = new();
    public GitberthOptions Options { get; }
    public IDbContextFactory<GitberthDb> Db { get; }
    public RestoreGate Gate { get; } = new();
    public Lifetime App { get; } = new();
    public BackupPaths Paths => new(Options);

    public BackupService Backup() => new(Engine, Db, Options, NullLogger<BackupService>.Instance) { Iterations = 1000, ChunkSize = 4096 };

    public RestoreService Restore() =>
        new(Engine, Db, Options, Gate, NullLogger<RestoreService>.Instance, App) { ExitDelay = TimeSpan.Zero, DatabaseReady = TimeSpan.FromSeconds(5) };

    /// <summary>Gitberth's own files, as a running Gitberth has them (its database is the host's own).</summary>
    public void Files(string key)
    {
        Directory.CreateDirectory(Options.KeysDir);
        File.WriteAllText(Path.Combine(Options.KeysDir, "key-1.xml"), $"<key id=\"{key}\"/>");
        Directory.CreateDirectory(Options.LogsDir);
        File.WriteAllText(Path.Combine(Options.LogsDir, "deploy-1.log"), "Building site…\n");
        Directory.CreateDirectory(Path.Combine(Options.DataDir, "compose", "site", "data"));
        File.WriteAllText(Path.Combine(Options.DataDir, "compose", "site", "data", "notes.txt"), "compose data");
        Directory.CreateDirectory(Path.Combine(Options.DataDir, "compose", ".run"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Options.DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A Docker engine for save and restore's tests, in memory: containers, volumes (their files as a tar), images and
/// networks, every change written to <see cref="Calls"/> in order. A helper container's archive is its volume's; a
/// pg_dumpall prints a dump, and psql takes one on its standard input.
/// </summary>
internal sealed class BackupTestEngine : IBackupEngine
{
    private readonly object _gate = new();
    private int _ids;

    public Dictionary<string, JsonObject> Containers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonObject> Images { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonObject> Volumes { get; } = new(StringComparer.Ordinal);

    /// <summary>Each volume's files, as the tar Docker's archive gives (entries v/…).</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<JsonObject> Networks { get; } = [new JsonObject { ["Name"] = "bridge", ["Driver"] = "bridge" }];
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

    /// <summary>What registries have, by reference: its digest.</summary>
    public Dictionary<string, string> Registry { get; } = new(StringComparer.Ordinal);

    /// <summary>The dump pg_dumpall prints, and every dump psql was given.</summary>
    public string Dump { get; set; } = "CREATE ROLE shop;\nCREATE DATABASE shop OWNER shop;\n\\connect shop\nINSERT INTO orders VALUES (1, 'row-7f3a');\n"
        + string.Concat(Enumerable.Range(2, 300).Select(i => $"INSERT INTO orders VALUES ({i}, 'note {i % 7}');\n"));

    public List<string> Loaded { get; } = [];

    /// <summary>Every change and stream, in order: "create web", "start web", "pause web", "volume web-data", "put helper v", "exec db pg_dumpall"…</summary>
    public List<string> Calls { get; } = [];

    /// <summary>A call that fails, by its line's start, with Docker's message.</summary>
    public Dictionary<string, string> Failing { get; } = new(StringComparer.Ordinal);

    public string Architecture { get; set; } = "x86_64";

    /// <summary>Told each call as it's made (a test stops Gitberth in the middle of one).</summary>
    public Action<string>? OnCall { get; set; }

    private void Call(string line)
    {
        lock (_gate)
        {
            Calls.Add(line);
            if (Failing.FirstOrDefault(f => line.StartsWith(f.Key, StringComparison.Ordinal)) is { Key: not null } failing)
            {
                Failing.Remove(failing.Key);
                throw new DockerApiException(500, failing.Value);
            }
        }
        OnCall?.Invoke(line);
    }

    // ---- making the host ----

    public JsonObject AddImage(string id, string? tag, params string[] digests)
    {
        var image = new JsonObject
        {
            ["Id"] = id,
            ["RepoTags"] = new JsonArray(tag is null ? [] : [(JsonNode)tag]),
            ["RepoDigests"] = new JsonArray([.. digests.Select(d => (JsonNode?)d)]),
            ["Size"] = 1_000_000,
            ["Config"] = new JsonObject { ["Env"] = new JsonArray("PATH=/usr/bin") },
        };
        Images[id] = image;
        if (tag is not null) Images[tag] = image;
        return image;
    }

    /// <summary>A container as an inspect has it, its volumes made, on its networks.</summary>
    public JsonObject AddContainer(string name, string image, string imageId, bool running = true, string[]? env = null, string[]? volumes = null,
        IDictionary<string, string>? labels = null, string[]? networks = null, int? hostPort = null)
    {
        var id = (name.GetHashCode().ToString("x8") + new string('0', 64))[..64];
        var mounts = new JsonArray();
        var binds = new JsonArray();
        foreach (var v in volumes ?? [])
        {
            var (volume, target) = (v.Split(':')[0], v.Split(':')[1]);
            mounts.Add(new JsonObject { ["Type"] = "volume", ["Name"] = volume, ["Source"] = "/var/lib/docker/volumes/" + volume, ["Destination"] = target, ["RW"] = true });
            binds.Add(v);
            AddVolume(volume);
        }
        var endpoints = new JsonObject();
        foreach (var n in networks ?? ["bridge"])
        {
            endpoints[n] = new JsonObject { ["Aliases"] = new JsonArray(name) };
            if (!Networks.Any(x => (string?)x["Name"] == n)) Networks.Add(new JsonObject { ["Name"] = n, ["Driver"] = "bridge", ["Labels"] = new JsonObject { ["made-by"] = "test" } });
        }
        var ports = new JsonObject();
        if (hostPort is { } port) ports["80/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "", ["HostPort"] = port.ToString() });
        var inspect = new JsonObject
        {
            ["Id"] = id,
            ["Name"] = "/" + name,
            ["Image"] = imageId,
            ["Config"] = new JsonObject
            {
                ["Image"] = image, ["Hostname"] = id[..12],
                ["Env"] = new JsonArray([.. (env ?? []).Select(e => (JsonNode?)e)]),
                ["Labels"] = new JsonObject([.. (labels ?? new Dictionary<string, string>()).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]),
                ["Cmd"] = new JsonArray("run"),
            },
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = binds, ["NetworkMode"] = (networks ?? ["bridge"])[0], ["PortBindings"] = ports,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped", ["MaximumRetryCount"] = 0 },
            },
            ["Mounts"] = mounts,
            ["State"] = State(running),
            ["NetworkSettings"] = new JsonObject { ["Networks"] = endpoints },
        };
        lock (_gate) Containers[name] = inspect;
        return inspect;
    }

    public void AddVolume(string name, string? marker = null)
    {
        lock (_gate)
        {
            if (!Volumes.ContainsKey(name)) Volumes[name] = new JsonObject { ["Name"] = name, ["Driver"] = "local", ["Labels"] = new JsonObject { ["kept"] = "yes" } };
            if (marker is not null) Files[name] = Tar(marker);
        }
    }

    /// <summary>A volume's files as Docker's archive of /v gives them: the folder, a marker file, and a sub-folder.</summary>
    public static byte[] Tar(string marker)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "v/") { Uid = 999, Gid = 999 });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "v/marker.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(marker)) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "v/sub/data.bin") { DataStream = new MemoryStream(new byte[50_000]) });
        }
        return tar.ToArray();
    }

    private static JsonObject State(bool running) => new() { ["Running"] = running, ["Paused"] = false, ["Status"] = running ? "running" : "exited" };

    private JsonObject Get(string idOrName) =>
        Containers.TryGetValue(idOrName, out var c) ? c
        : Containers.Values.FirstOrDefault(x => ((string)x["Id"]!).StartsWith(idOrName, StringComparison.Ordinal)) ?? throw new DockerApiException(404, "No such container: " + idOrName);

    private static string NameOf(JsonObject c) => ((string)c["Name"]!).TrimStart('/');

    // ---- IBackupEngine ----

    public Task<Version> ApiVersionAsync(CancellationToken ct = default) => Task.FromResult(new Version(1, 51));
    public Task<string> EngineVersionAsync(CancellationToken ct = default) => Task.FromResult("29.0.1");

    public Task<JsonObject> InfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new JsonObject { ["Name"] = "homelab", ["Architecture"] = Architecture });

    public Task<List<RunningContainer>> ListAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Containers.Values.Select(c => new RunningContainer((string)c["Id"]!, NameOf(c), (string)c["Config"]!["Image"]!,
                (bool)c["State"]!["Running"]! ? "running" : "exited", "", null,
                (c["Config"]!["Labels"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => (string)kv.Value!))).ToList());
    }

    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            try
            {
                return Task.FromResult<JsonObject?>((JsonObject)Get(idOrName).DeepClone());
            }
            catch (DockerApiException)
            {
                return Task.FromResult<JsonObject?>(null);
            }
        }
    }

    public Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Images.TryGetValue(idOrName, out var image) ? (JsonObject?)image.DeepClone() : null);
    }

    public Task<JsonArray> ListVolumesAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new JsonArray([.. Volumes.Values.Select(v => (JsonNode?)v.DeepClone())]));
    }

    public Task<IReadOnlyDictionary<string, long>> VolumeSizesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>(Sizes));

    public Task<JsonArray> ListNetworksJsonAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new JsonArray([.. Networks.Select(n => (JsonNode?)n.DeepClone())]));
    }

    public Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        Call("create " + name);
        lock (_gate)
        {
            if (Containers.ContainsKey(name)) throw new DockerApiException(409, "Conflict: the name is in use.");
            var id = (++_ids).ToString("x8") + new string('a', 56);
            var mounts = new JsonArray();
            foreach (var bind in (body["HostConfig"]?["Binds"] as JsonArray ?? []).Select(b => (string)b!))
            {
                var parts = bind.Split(':');
                if (!Volumes.ContainsKey(parts[0])) Volumes[parts[0]] = new JsonObject { ["Name"] = parts[0], ["Driver"] = "local" };
                mounts.Add(new JsonObject { ["Type"] = "volume", ["Name"] = parts[0], ["Destination"] = parts[1], ["RW"] = parts.Length < 3 });
            }
            foreach (var m in (body["HostConfig"]?["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => (string?)m["Type"] == "volume"))
                mounts.Add(new JsonObject { ["Type"] = "volume", ["Name"] = m["Source"]?.DeepClone(), ["Destination"] = m["Target"]?.DeepClone(), ["RW"] = true });
            var config = (JsonObject)body.DeepClone();
            config.Remove("HostConfig");
            config.Remove("NetworkingConfig");
            var endpoints = new JsonObject();
            foreach (var (n, e) in body["NetworkingConfig"]?["EndpointsConfig"] as JsonObject ?? [])
                endpoints[n] = e?.DeepClone();
            Containers[name] = new JsonObject
            {
                ["Id"] = id, ["Name"] = "/" + name, ["Image"] = (string?)body["Image"] ?? "",
                ["Config"] = config, ["HostConfig"] = body["HostConfig"]?.DeepClone(), ["Mounts"] = mounts, ["State"] = State(false),
                ["NetworkSettings"] = new JsonObject { ["Networks"] = endpoints },
                ["Body"] = body.DeepClone(),
            };
            return Task.FromResult(id);
        }
    }

    public Task StartContainerAsync(string idOrName, CancellationToken ct = default)
    {
        Call("start " + idOrName);
        lock (_gate) Get(idOrName)["State"] = State(true);
        return Task.CompletedTask;
    }

    // Like Docker's, a stop or pause asked for is done even when the call's token is cancelled meanwhile: the call then
    // says it was cancelled, though the container is stopped or frozen.
    public Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct = default)
    {
        Call("stop " + idOrName);
        lock (_gate) Get(idOrName)["State"] = State(false);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct = default)
    {
        Call("rm " + idOrName);
        lock (_gate)
        {
            var c = Get(idOrName);
            Containers.Remove(NameOf(c));
        }
        return Task.CompletedTask;
    }

    public Task PauseContainerAsync(string idOrName, CancellationToken ct = default)
    {
        Call("pause " + idOrName);
        lock (_gate) Get(idOrName)["State"]!["Paused"] = true;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task UnpauseContainerAsync(string idOrName, CancellationToken ct = default)
    {
        Call("unpause " + idOrName);
        lock (_gate) Get(idOrName)["State"]!["Paused"] = false;
        return Task.CompletedTask;
    }

    public Task CreateVolumeAsync(JsonObject body, CancellationToken ct = default)
    {
        Call("volume " + body["Name"]);
        lock (_gate) Volumes[(string)body["Name"]!] = (JsonObject)body.DeepClone();
        return Task.CompletedTask;
    }

    public Task<string> CreateNetworkAsync(JsonObject body, CancellationToken ct = default)
    {
        Call("network " + body["Name"]);
        lock (_gate) Networks.Add((JsonObject)body.DeepClone());
        return Task.FromResult("net-" + body["Name"]);
    }

    // A helper's archive of /v is its volume's files.
    private string VolumeAt(string container, string path)
    {
        lock (_gate)
        {
            var c = Get(container);
            var mount = (c["Mounts"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(m => (string?)m["Destination"] == "/v")
                ?? throw new DockerApiException(404, $"Could not find the file {path} in container {container}");
            return (string)mount["Name"]!;
        }
    }

    public Task<Stream> GetArchiveAsync(string container, string path, CancellationToken ct = default)
    {
        var volume = VolumeAt(container, path);
        Call($"get {container} {path} ({volume})");
        lock (_gate) return Task.FromResult<Stream>(new MemoryStream(Files.GetValueOrDefault(volume) ?? Tar("")));
    }

    public async Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct = default)
    {
        var volume = VolumeAt(container, "/v");
        Call($"put {container} {path} ({volume})");
        var bytes = new MemoryStream();
        await tar.CopyToAsync(bytes, ct);
        lock (_gate) Files[volume] = bytes.ToArray();
    }

    public async Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? env, Stream? stdin, Stream? stdout,
        CancellationToken ct = default)
    {
        Call($"exec {container} {command[0]}" + (command[0] == "sh" ? " " + command[2][..Math.Min(40, command[2].Length)] : ""));
        if (env is not null && env.Values.Any(v => command.Any(c => c.Contains(v, StringComparison.Ordinal))))
            throw new InvalidOperationException("A secret went on a command line.");
        switch (command[0])
        {
            case "pg_dumpall":
                if (stdout is not null) await stdout.WriteAsync(Encoding.UTF8.GetBytes(Dump), ct);
                return new ExecResult(0, "");
            case "psql":
                var dump = new MemoryStream();
                if (stdin is not null) await stdin.CopyToAsync(dump, ct);
                lock (_gate) Loaded.Add(Encoding.UTF8.GetString(dump.ToArray()));
                return new ExecResult(0, "psql:<stdin>:1: ERROR:  role \"postgres\" already exists");
            default:
                return new ExecResult(0, "");
        }
    }

    public Task<Stream> SaveImagesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        Call("save " + string.Join(',', names));
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("image tar of " + string.Join(',', names) + new string('x', 20_000))));
    }

    public async Task<string> LoadImagesAsync(Stream tar, CancellationToken ct = default)
    {
        var bytes = new MemoryStream();
        await tar.CopyToAsync(bytes, ct);
        var text = Encoding.UTF8.GetString(bytes.ToArray());
        Call("load " + text[..Math.Min(40, text.Length)].Replace("x", ""));
        return "Loaded image: " + text["image tar of ".Length..].TrimEnd('x');
    }

    public Task PullImageAsync(string fromImage, string tagOrDigest, string? registryAuth, CancellationToken ct = default)
    {
        Call($"pull {fromImage} {tagOrDigest}");
        return Task.CompletedTask;
    }

    public Task TagImageAsync(string image, string repository, string tag, CancellationToken ct = default)
    {
        Call($"tag {image} {repository}:{tag}");
        return Task.CompletedTask;
    }

    public Task<string?> RegistryDigestAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(Registry.TryGetValue(reference, out var digest) ? digest : null);
}
