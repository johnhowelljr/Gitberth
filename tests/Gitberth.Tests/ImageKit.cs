using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Sources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// What the image, update and create tests share: an image engine in memory, a database in memory, and stand-ins for the
/// catalog, the settings and the change engine (the other builders' services), so each test sees exactly what was asked.
/// </summary>
internal static class ImageKit
{
    public static readonly DiskSpace Plenty = new(200L << 30, 250L << 30);

    public static string Fixture(string folder, string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file));

    public static SecretProtector Protector() => new(new EphemeralDataProtectionProvider());

    public static ImageService Images(Engine engine, Db db, SecretProtector? protector = null, DiskSpace? disk = null, IHttpClientFactory? http = null) =>
        new(engine, new RegistryService(db, protector ?? Protector(), engine), new GitberthOptions { DataDir = Path.GetTempPath() }, http ?? new NoHttp(),
            NullLogger<ImageService>.Instance, db) { ReadDisk = () => disk ?? Plenty };

    /// <summary>An image as GET /images/{id}/json describes it.</summary>
    public static JsonObject Image(string id, string[] tags, string[]? digests = null, string[]? exposed = null, string[]? volumes = null, string[]? env = null,
        string created = "2026-11-01T00:00:00Z") => new()
    {
        ["Id"] = id,
        ["RepoTags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
        ["RepoDigests"] = new JsonArray((digests ?? []).Select(d => (JsonNode)d).ToArray()),
        ["Created"] = created,
        ["Size"] = 123_456_789,
        ["Os"] = "linux",
        ["Architecture"] = "amd64",
        ["Config"] = new JsonObject
        {
            ["Env"] = new JsonArray((env ?? ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"]).Select(e => (JsonNode)e).ToArray()),
            ["ExposedPorts"] = new JsonObject((exposed ?? []).Select(p => new KeyValuePair<string, JsonNode?>(p, new JsonObject()))),
            ["Volumes"] = new JsonObject((volumes ?? []).Select(v => new KeyValuePair<string, JsonNode?>(v, new JsonObject()))),
            ["Labels"] = new JsonObject(),
        },
    };

    /// <summary>A container as GET /containers/{id}/json describes it, as far as updates read it.</summary>
    public static JsonObject Container(string name, string id, string configImage, string imageId, Dictionary<string, string>? labels = null) => new()
    {
        ["Id"] = id,
        ["Name"] = "/" + name,
        ["Image"] = imageId,
        ["Config"] = new JsonObject
        {
            ["Image"] = configImage,
            ["Labels"] = new JsonObject((labels ?? []).Select(l => new KeyValuePair<string, JsonNode?>(l.Key, l.Value))),
        },
        ["State"] = new JsonObject { ["Running"] = true, ["Status"] = "running" },
    };

    public static string Id(char c) => "sha256:" + new string(c, 64);

    public static string Digest(char c) => "sha256:" + new string(c, 64);

    /// <summary>A clock the test sets; local time is UTC.</summary>
    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Progress as it's reported, on the reporting thread.</summary>
    public sealed class Seen<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            lock (Values) Values.Add(value);
        }
    }

    /// <summary>
    /// The database, made from the model, in a file of its own that's gone on Dispose. Each context opens its own connection,
    /// as Gitberth's do: an update's follow-up writes in the background while a test reads, and contexts sharing one
    /// connection break then (see <see cref="AppsRig"/>).
    /// </summary>
    public sealed class Db : IDbContextFactory<GitberthDb>, IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "gitberth-images-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly DbContextOptions<GitberthDb> _options;

        public Db()
        {
            Directory.CreateDirectory(_folder);
            _options = new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_folder, "gitberth.db")}").Options;
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        public GitberthDb CreateDbContext() => new(_options);

        public async Task<AppDefinition> AddAsync(AppDefinition app)
        {
            await using var db = CreateDbContext();
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            return app;
        }

        public async Task<AppDefinition> AppAsync(int id)
        {
            await using var db = CreateDbContext();
            return await db.Apps.AsNoTracking().FirstAsync(a => a.Id == id);
        }

        public void Dispose()
        {
            // The pooled connections let go of the file before its folder goes.
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>No Docker Hub: every call fails as if it were unreachable.</summary>
    public sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());

        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("No network in tests.");
        }
    }

    /// <summary>
    /// Docker's image calls, in memory: images by ID with their tags and digests, containers by what they run, each
    /// registry's answer per reference, and pulls that send their lines then change the images as a real pull would.
    /// </summary>
    public sealed class Engine : IImageEngine
    {
        public List<JsonObject> Images { get; } = [];
        public List<JsonObject> Containers { get; } = [];
        public Dictionary<string, Func<JsonObject>> Distribution { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (IReadOnlyList<string> Lines, Action? Then)> Pulls { get; } = new(StringComparer.Ordinal);
        public Func<JsonObject, Exception?> Auth { get; set; } = _ => null;
        public List<string> Calls { get; } = [];
        public List<string>? Timeline { get; set; }
        public JsonObject Info { get; set; } = new() { ["NCPU"] = 4, ["MemTotal"] = 8L << 30, ["LoggingDriver"] = "journald" };
        public List<string?> AuthHeaders { get; } = [];
        /// <summary>When set, a pull sends nothing until the test completes it.</summary>
        public TaskCompletionSource? PullGate { get; set; }
        /// <summary>Called with each image looked up, before the answer: a tag can move in between.</summary>
        public Action<string>? OnInspect { get; set; }

        public JsonObject Add(JsonObject image)
        {
            Images.Add(image);
            return image;
        }

        /// <summary>Moves a tag onto another image, as a pull or a build does: the old image keeps its digests, not the tag.</summary>
        public void Tag(string tag, string id)
        {
            foreach (var image in Images)
            {
                var tags = image["RepoTags"]!.AsArray();
                foreach (var node in tags.Where(t => Same((string)t!, tag)).ToList()) tags.Remove(node);
            }
            Images.First(i => (string)i["Id"]! == id)["RepoTags"]!.AsArray().Add(tag);
        }

        public void Uses(string name, string imageId, string? image = null) =>
            Containers.Add(new JsonObject { ["Names"] = new JsonArray("/" + name), ["Image"] = image ?? imageId, ["ImageID"] = imageId, ["Labels"] = new JsonObject() });

        public JsonObject? Find(string nameOrId)
        {
            var id = nameOrId.StartsWith("sha256:", StringComparison.Ordinal) ? nameOrId : "sha256:" + nameOrId;
            return Images.FirstOrDefault(i => (string)i["Id"]! == nameOrId || (string)i["Id"]! == id)
                ?? Images.FirstOrDefault(i => i["RepoTags"]!.AsArray().Any(t => Same((string)t!, nameOrId)))
                ?? Images.FirstOrDefault(i => i["RepoDigests"]!.AsArray().Any(d => Same((string)d!, nameOrId)));
        }

        private static bool Same(string a, string b) =>
            a == b || ImageReference.Parse(a) is { } x && ImageReference.Parse(b) is { } y && x.Canonical == y.Canonical;

        private void Log(string call)
        {
            lock (Calls) Calls.Add(call);
            if (Timeline is { } timeline) lock (timeline) timeline.Add(call);
        }

        public Task<JsonArray> ListImagesAsync(CancellationToken ct) => Task.FromResult(new JsonArray(Images.Select(i => (JsonNode)new JsonObject
        {
            ["Id"] = i["Id"]!.DeepClone(),
            ["RepoTags"] = i["RepoTags"]!.DeepClone(),
            ["RepoDigests"] = i["RepoDigests"]!.DeepClone(),
            ["Created"] = DateTimeOffset.Parse((string)i["Created"]!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            ["Size"] = i["Size"]!.DeepClone(),
            ["Containers"] = -1,
        }).ToArray()));

        public Task<JsonArray> ListContainersAsync(CancellationToken ct) => Task.FromResult(new JsonArray(Containers.Select(c => c.DeepClone()).ToArray()));

        public Task<JsonObject?> InspectImageAsync(string nameOrId, CancellationToken ct)
        {
            OnInspect?.Invoke(nameOrId);
            return Task.FromResult(Find(nameOrId)?.DeepClone() as JsonObject);
        }

        public Task<JsonArray> SearchImagesAsync(string term, int limit, CancellationToken ct)
        {
            Log($"search {term}");
            return Task.FromResult(JsonNode.Parse(Fixture("Images", "search-postgres.json"))!.AsArray());
        }

        public Task<JsonObject> DistributionAsync(string reference, string? registryAuth, CancellationToken ct)
        {
            Log($"distribution {reference}");
            AuthHeaders.Add(registryAuth);
            return Distribution.TryGetValue(reference, out var answer)
                ? Task.FromResult(answer())
                : throw new DockerApiException(404, $"manifest unknown: manifest unknown for {reference}");
        }

        public async IAsyncEnumerable<JsonObject> PullAsync(string fromImage, string tagOrDigest, string? platform, string? registryAuth,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var key = $"{fromImage}:{tagOrDigest}";
            Log($"pull {key}");
            AuthHeaders.Add(registryAuth);
            if (!Pulls.TryGetValue(key, out var pull)) throw new DockerApiException(404, $"pull access denied for {fromImage}, repository does not exist or may require 'docker login'");
            if (PullGate is { } gate) await gate.Task;
            foreach (var line in pull.Lines)
            {
                await Task.Yield();
                if (DockerApi.ParsePullLine(line) is { } parsed) yield return parsed;
            }
            pull.Then?.Invoke();
        }

        public Task<JsonObject> AuthAsync(JsonObject credentials, CancellationToken ct)
        {
            Log($"auth {credentials["serveraddress"]}");
            return Auth(credentials) is { } refused ? Task.FromException<JsonObject>(refused) : Task.FromResult(new JsonObject { ["Status"] = "Login Succeeded" });
        }

        public Task<JsonArray> RemoveImageAsync(string idOrName, CancellationToken ct)
        {
            Log($"remove {idOrName}");
            var image = Find(idOrName) ?? throw new DockerApiException(404, $"No such image: {idOrName}");
            if (Containers.Any(c => (string)c["ImageID"]! == (string)image["Id"]!)) throw new DockerApiException(409, "conflict: image is being used by a container");
            Images.Remove(image);
            return Task.FromResult(new JsonArray(new JsonObject { ["Deleted"] = image["Id"]!.DeepClone() }));
        }

        public Task<JsonObject> InfoAsync(CancellationToken ct) => Task.FromResult((JsonObject)Info.DeepClone());
    }

    /// <summary>The catalog, the settings and the change engine for updates: a row per adopted container, plans as asked, a result as set.</summary>
    public sealed class UpdateSteps(Db db) : IUpdateSteps
    {
        public Dictionary<string, JsonObject> Containers { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public List<ContainerEditRequest> Requests { get; } = [];
        public List<(string Container, ActivityKind Kind, string Text, ActivityTone Tone)> Activity { get; } = [];
        public PortChangeResult Result { get; set; } = new(PortChangeOutcome.Done, "It runs and passed its checks.");
        public string? Blocked { get; set; }
        public string? Busy { get; set; }
        public Action? OnPlan { get; set; }

        public Task<JsonObject?> InspectContainerAsync(string container, CancellationToken ct) =>
            Task.FromResult(Containers.TryGetValue(container, out var c) ? c.DeepClone() as JsonObject : null);

        public async Task<AppDefinition> AdoptAsync(string key, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"adopt {key}");
            await using var context = db.CreateDbContext();
            var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key[3..] : null;
            if (await context.Apps.FirstOrDefaultAsync(a => a.Container == key || (slug != null && a.Container == null && a.Slug == slug), ct) is { } row) return row;
            var image = (string)Containers[key]["Config"]!["Image"]!;
            row = new AppDefinition
            {
                Name = key, Slug = AppService.Slugify(key), Container = key, Source = AppSource.Existing, UpdatePolicy = UpdatePolicy.Notify,
                ImageRef = ImageReference.Parse(image) is { Digest: null } ? image : null,
            };
            context.Apps.Add(row);
            await context.SaveChangesAsync(ct);
            return row;
        }

        public Task SyncAsync(int appId, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"sync {appId}");
            return Task.CompletedTask;
        }

        public Task<SettingsView?> LoadAsync(string key, CancellationToken ct)
        {
            if (!Containers.TryGetValue(key, out var c)) return Task.FromResult<SettingsView?>(null);
            var summary = new AppSummary(key, null, key, key, AppSource.Existing, ContainerRole.Other, ImageOrigin.Registry, (string)c["Config"]!["Image"]!, null,
                AppState.Running, "Running", (string)c["Id"]!, [], null, null, null, false, false, null);
            var settings = new ContainerSettings { Image = (string)c["Config"]!["Image"]!, Env = [new EnvSetting("POSTGRES_PASSWORD", "hunter22", Secret: true)] };
            return Task.FromResult<SettingsView?>(new SettingsView(summary, (string)c["Id"]!, settings, new AppOptions { Name = key, WebPort = 80, SecretKeys = ["POSTGRES_PASSWORD"] },
                null, new EditAccess(null, null, null, false, null, new HashSet<SettingField>()), null));
        }

        public Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"plan {request.Kind} {container} {request.Wanted.Image}");
            Requests.Add(request);
            OnPlan?.Invoke();
            return Task.FromResult(new PortChangePlan
            {
                Container = container,
                Verdict = RecreateVerdict.Yes,
                Blocked = Blocked,
                Kind = request.Kind,
                Fields = new HashSet<SettingField> { SettingField.Image },
                Settings = [new SettingLine(SettingField.Image, "Image", request.Loaded.Image, request.Wanted.Image, ApplyKind.Recreate)],
                ImageAfter = request.Wanted.Image,
                Request = request,
                ContainerId = request.ContainerId,
            });
        }

        public PortChangeJob Start(PortChangePlan plan)
        {
            lock (Calls) Calls.Add($"start {plan.Kind} {plan.Container}");
            if (Result.Ok && Containers.TryGetValue(plan.Container, out var c) && plan.Request?.ExpectedImageId is { } id) c["Image"] = id;
            return new PortChangeJob(plan) { Completion = Task.FromResult(Result) };
        }

        public Task<string?> BusyWithAsync(string container, CancellationToken ct) => Task.FromResult(Busy);

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
        {
            lock (Activity) Activity.Add((container, kind, text, tone));
            return Task.CompletedTask;
        }
    }

    /// <summary>The services a create goes through: slugs as AppService chooses them, the host's ports, rows, and the engine's result as set.</summary>
    public sealed class CreateSteps : ICreateSteps
    {
        public HashSet<string> Taken { get; } = new(StringComparer.OrdinalIgnoreCase) { "gitberth" };
        public List<HostPortUse> Uses { get; } = [];
        public HashSet<string> Networks { get; } = new(StringComparer.Ordinal) { "bridge", "host", "none", "gitberth", "gitberth-lan", "media" };
        public List<string> Timeline { get; set; } = [];
        public List<NewAppRow> Rows { get; } = [];
        public List<NewContainerRequest> Requests { get; } = [];
        public List<(string Container, ActivityKind Kind, string Text, ActivityTone Tone)> Activity { get; } = [];
        public PortChangeResult Result { get; set; } = new(PortChangeOutcome.Done, "It answers at 10.1.1.80:5432.");
        public string? EngineBlocks { get; set; }

        public Task<NetworkName> ChooseSlugAsync(string name, CancellationToken ct)
        {
            var slug = AppService.Slugify(name);
            var unique = slug;
            for (var i = 2; Taken.Contains(unique); i++) unique = $"{slug}-{i}";
            return Task.FromResult(new NetworkName(unique, Taken.Contains(slug) ? $"{slug} is taken on gitberth" : null));
        }

        public Task<string> LanHostAsync(CancellationToken ct) => Task.FromResult("10.1.1.80");

        public Task<IReadOnlyList<HostPortUse>> HostPortsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostPortUse>>(Uses);

        public Task<int> SuggestAsync(int containerPort, string protocol, IEnumerable<int> alsoTaken, CancellationToken ct) =>
            Task.FromResult(HostPortMap.Suggest(Uses, (32768, 60999), containerPort, protocol, null, alsoTaken));

        public Task<IReadOnlySet<string>> NetworksAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(Networks);

        public Task<AppDefinition> AddAsync(NewAppRow row, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"row {row.Slug}");
            Rows.Add(row);
            return Task.FromResult(new AppDefinition { Id = Rows.Count, Name = row.Name, Slug = row.Slug, Source = row.Source, ImageRef = row.ImageRef });
        }

        public Task SyncAsync(int appId, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"sync {appId}");
            return Task.CompletedTask;
        }

        public Task<PortChangePlan> PlanCreateAsync(NewContainerRequest request, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"plan-create {request.Name}");
            Requests.Add(request);
            return Task.FromResult(new PortChangePlan
            {
                Container = request.Name, Verdict = RecreateVerdict.Yes, Kind = ChangeKind.Create, Create = request, Blocked = EngineBlocks,
            });
        }

        public PortChangeJob Start(PortChangePlan plan)
        {
            lock (Timeline) Timeline.Add($"engine {plan.Container}");
            return new PortChangeJob(plan) { Completion = Task.FromResult(Result) };
        }

        public Task RememberAsync(string container, string network, string? alias, string? ipv4, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"remember {network}{(ipv4 is null ? "" : " " + ipv4)}");
            return Task.CompletedTask;
        }

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"activity {kind}");
            Activity.Add((container, kind, text, tone));
            return Task.CompletedTask;
        }

        /// <summary>The rows a failed create removed.</summary>
        public List<int> Discarded { get; } = [];

        /// <summary>A failed create left its container in Docker (the engine couldn't remove it).</summary>
        public bool LeftBehind { get; set; }

        public Task DiscardAsync(int appId, CancellationToken ct)
        {
            lock (Timeline) Timeline.Add($"discard {appId}");
            Discarded.Add(appId);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string container, CancellationToken ct) => Task.FromResult(LeftBehind);

        /// <summary>Settings › Updates › New apps from an image, as the test sets it.</summary>
        public UpdatePolicy DefaultPolicy { get; set; } = UpdatePolicy.Notify;

        public Task<UpdatePolicy> DefaultPolicyAsync(CancellationToken ct) => Task.FromResult(DefaultPolicy);
    }
}
