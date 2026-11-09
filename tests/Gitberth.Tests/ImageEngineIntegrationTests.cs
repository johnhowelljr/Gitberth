using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Health;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Gitberth.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Images, creating an app and updating it, against the real engine (opt-in: GITBERTH_ENGINE_IT=1, with Docker's socket
/// at /var/run/docker.sock). It pulls only alpine:3.20 and alpine:3.21, and removes the ones it pulled that the host
/// didn't have before. Every container and network it makes is named gbtest-uc-*, through a guard that refuses any other
/// name, any other network and any volume; they're removed at the end, whatever happened.
/// </summary>
public sealed class ImageEngineIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Prefix = "gbtest-uc-";
    private const string Alpine320 = "alpine:3.20";
    private const string Alpine321 = "alpine:3.21";
    private const string Busybox = "busybox:1.37";
    private static readonly string[] Pullable = [Alpine320, Alpine321, Busybox];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-image-it-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private readonly string _lan = Environment.GetEnvironmentVariable("GITBERTH_ENGINE_IT_LAN") ?? "10.1.1.80";
    private readonly HashSet<string> _hadBefore = new(StringComparer.Ordinal);
    private GitberthOptions _options = null!;
    private DockerApi _api = null!;
    private DockerEngine _real = null!;
    private IContainerEngine _guarded = null!;
    private Db _db = null!;
    private SecretProtector _protector = null!;

    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private string Network => $"{Prefix}{_run}-net";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GITBERTH_ENGINE_IT") != "1") return;
        Directory.CreateDirectory(_dir);
        _options = new GitberthOptions { DataDir = _dir, RunningInContainer = true, Network = Network };
        _api = new DockerApi(_options);
        _real = new DockerEngine(_api);
        _guarded = Guard.Over(_real, Prefix);
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite($"Data Source={Path.Combine(_dir, "gitberth.db")}").Options);
        await using (var context = _db.CreateDbContext()) await context.Database.MigrateAsync();
        _protector = new SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        foreach (var image in Pullable)
            if (await _api.InspectImageAsync(image) is not null) _hadBefore.Add(image);
        output.WriteLine($"on the host before: {string.Join(", ", _hadBefore.DefaultIfEmpty("none of them"))}");
    }

    public async Task DisposeAsync()
    {
        if (_real is null) return;
        foreach (var c in (await _real.ListContainersAsync(default)).OfType<JsonObject>())
        {
            var name = ImageService.Strings(c["Names"]).Select(n => n.TrimStart('/')).FirstOrDefault() ?? "";
            if (!name.StartsWith(Prefix + _run, StringComparison.Ordinal)) continue;
            output.WriteLine($"removing {name}");
            await _guarded.RemoveAsync(name, force: true, default);
        }
        if ((await _real.ListNetworksAsync(default)).OfType<JsonObject>().Any(n => (string?)n["Name"] == Network))
            await _guarded.RemoveNetworkAsync(Network, default);
        // The images this run pulled that the host didn't have before; never one a container uses.
        var images = Images();
        foreach (var image in Pullable.Reverse().Where(i => !_hadBefore.Contains(i)))
        {
            if (await _api.InspectImageAsync(image) is not { } pulled) continue;
            var left = await images.RemoveIfUnusedAsync((string)pulled["Id"]!);
            output.WriteLine($"removing {image}: {left ?? "done"}");
        }
        _api.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    // With the database, as Program.cs wires it: a pull records that the registry has the reference.
    private ImageService Images() =>
        new(_api, new RegistryService(_db, _protector, _api), _options, new NoHub(), NullLogger<ImageService>.Instance, _db);

    [EngineFact]
    public async Task Resolves_pulls_lists_and_removes_images_on_the_real_engine()
    {
        // An image the host hasn't got, so the pull is a real one, with layers to download; another builder's run may
        // have pulled alpine:3.20 already.
        var target = Pullable.FirstOrDefault(i => !_hadBefore.Contains(i)) ?? Alpine320;
        var stream = new List<string>();
        var images = new ImageService(_api, new RegistryService(_db, _protector, _api), _options, new NoHub(), NullLogger<ImageService>.Instance, _db)
        {
            PullLine = line => { lock (stream) stream.Add(line.ToJsonString()); },
        };

        // What Docker Hub says, without pulling.
        var remote = await images.ResolveAsync(target);
        Assert.True(remote.Found, remote.Error);
        Assert.StartsWith("sha256:", remote.Digest);
        Assert.Contains("linux/amd64", remote.Platforms);
        output.WriteLine($"{target}: {remote.Digest} ({string.Join(", ", remote.Platforms)})");
        var refused = await images.ResolveAsync("docker.io/gbtestuc/no-such-repo:1");
        Assert.Equal((false, true), (refused.Found, refused.Unauthorized));
        output.WriteLine(refused.Error);
        var missing = await images.ResolveAsync("alpine:no-such-tag-gbtest-uc");
        Assert.Equal((false, false), (missing.Found, missing.Unauthorized));
        output.WriteLine(missing.Error);

        // The pull, with its progress as Docker reports it.
        var seen = new List<PullProgress>();
        var job = images.StartPull(target);
        job.Changed += () => { lock (seen) seen.Add(job.Progress); };
        var pulled = await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(pulled.Ok, pulled.Message);
        output.WriteLine($"{pulled.Message} digest {pulled.Digest}; {seen.Count} progress reports, last {job.Progress}");
        output.WriteLine($"the stream, as Docker sent it:{Environment.NewLine}{string.Join(Environment.NewLine, stream)}");
        Assert.Equal(remote.Digest, pulled.Digest);
        if (!_hadBefore.Contains(target))
        {
            Assert.NotEmpty(seen);
            Assert.True(job.Progress.Layers >= 1);
            Assert.Equal(job.Progress.Layers, job.Progress.LayersDone);
            // Docker gives byte counts only for a layer big enough to take a while; either way the bar ends full.
            Assert.Equal(job.Progress.Total, job.Progress.Current);
            Assert.Equal(1, job.Progress.Fraction);
        }

        // On the host now: listed, and described.
        var local = Assert.Single(await images.ListLocalAsync(), i => i.Id == pulled.ImageId);
        Assert.Contains(target, local.Tags);
        var facts = await images.FactsAsync(target);
        Assert.Equal((pulled.ImageId, ImageOrigin.Registry), (facts!.Id, facts.Origin));

        // Pulled again, it's up to date.
        var again = await images.StartPull(target).Completion.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(again.Ok, again.Message);
        Assert.StartsWith($"{target} is up to date", again.Message);

        // A sign-in Docker Hub refuses saves nothing.
        var registries = new RegistryService(_db, _protector, _api);
        var signIn = await registries.SaveAsync(new RegistryInput(null, "docker.io", "gbtestuc-nobody-" + _run, "not-the-password"));
        output.WriteLine(signIn.Message);
        Assert.False(signIn.Ok);
        Assert.Empty(await registries.ListAsync());

        // DATA-5 (the backend fixer): an update's tidying never removes an image that still has a name, which someone gave
        // it; this run's own pull is removed by the test itself.
        if (!_hadBefore.Contains(target))
        {
            Assert.StartsWith($"{ImageService.Short(pulled.ImageId!)} has a name of its own ({target})", await images.RemoveIfUnusedAsync(pulled.ImageId!));
            await _api.RemoveImageAsync(target, default);
            Assert.Null(await _api.InspectImageAsync(target));
        }
    }

    /// <summary>
    /// Needs Builder B's change engine (PortChanger.PlanCreateAsync, and PlanAsync for an update and a rollback): with the
    /// stubs in Builder C's own tree it stops at the create and says so. Run it after the merge.
    /// </summary>
    [EngineFact]
    public async Task Creates_updates_rolls_back_and_removes_an_app_on_the_real_engine()
    {
        await _guarded.CreateNetworkAsync(new JsonObject { ["Name"] = Network, ["Labels"] = new JsonObject { ["gbtest"] = "image-it" } }, default);
        var images = Images();
        var changer = Changer();
        Func<string, string> containerFor = slug => $"{Prefix}{_run}-{slug}";
        var creator = new AppCreator(new CreateSteps(_real, changer, _db, _lan, containerFor, output), images, _options, NullLogger<AppCreator>.Instance)
        {
            ContainerFor = containerFor,
        };

        // Created from the registry image as any app is (pulled first when the host hasn't got it): Gitberth's labels, its
        // network with the alias, unless-stopped.
        var draft = await creator.DraftFromImageAsync(Alpine320, AppSource.Registry);
        var plan = await creator.PlanAsync(draft with { Settings = draft.Settings with { Command = ["sleep", "3600"] } });
        Assert.True(plan.CanCreate, string.Join("; ", plan.Problems.Select(p => p.Message)));
        Assert.Equal(!_hadBefore.Contains(Alpine320), plan.NeedsPull);
        output.WriteLine(plan.DockerRun);
        var job = creator.Start(plan);
        var created = await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        if (!created.Ok && created.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase))
            Assert.Fail($"The change engine here is a stub ({created.Message}): this test needs Builder B's PortChanger.PlanCreateAsync. Run it after the merge.");
        Assert.True(created.Ok, $"{created.Message} {created.LastOutput}");
        Assert.Equal(("Done", plan.NeedsPull), (job.Phase, job.Pull is not null));
        var id320 = (string)(await _api.InspectImageAsync(Alpine320))!["Id"]!;
        var container = plan.Container;
        var made = (await _real.InspectContainerAsync(container, default))!;
        Assert.Equal((true, id320), (Running(made), Str(made, "Image")));
        Assert.Equal(("true", "alpine", "registry"), (Label(made, "gitberth.managed"), Label(made, "gitberth.app"), Label(made, "gitberth.source")));
        Assert.Equal("unless-stopped", Str(made["HostConfig"]?["RestartPolicy"], "Name"));
        Assert.Contains("alpine", ImageService.Strings(made["NetworkSettings"]?["Networks"]?[Network]?["Aliases"]));

        // Updated to alpine:3.21, the tag it follows from now on: pulled, recreated on the new image, the old one kept.
        var updates = new UpdateService(new UpdateSteps(_real, changer, _db, output), images, _db, NullLogger<UpdateService>.Instance);
        await using (var context = _db.CreateDbContext())
            await context.Apps.Where(a => a.Container == container).ExecuteUpdateAsync(s => s.SetProperty(a => a.ImageRef, Alpine321));
        // Check for update, as the owner presses it: Docker Hub has alpine:3.21, so it's a registry's image with an update.
        var status = await updates.CheckAsync(container);
        Assert.True((status.Origin, status.Available) == (ImageOrigin.Registry, true), status.Text);
        var update = await updates.PrepareAsync(container);
        Assert.Null(update.Blocked);
        output.WriteLine(string.Join(" | ", update.Plan!.Settings.Select(l => $"{l.Label}: {l.Before} → {l.After}")));
        var updated = await updates.Start(update.Plan).Completion.WaitAsync(TimeSpan.FromMinutes(3));
        await updates.FollowUpOf(container);
        Assert.True(updated.Ok, updated.Message);
        Assert.True(update.Pull!.Ok, update.Pull.Message);
        var id321 = (string)(await _api.InspectImageAsync(Alpine321))!["Id"]!;
        var after = (await _real.InspectContainerAsync(container, default))!;
        Assert.Equal((true, id321, Alpine321), (Running(after), Str(after, "Image"), Str(after["Config"], "Image")));
        Assert.Equal(["sleep", "3600"], ImageService.Strings(after["Config"]?["Cmd"]));
        await using (var context = _db.CreateDbContext())
            Assert.Equal(id320, (await context.Apps.AsNoTracking().SingleAsync(a => a.Container == container)).PreviousImage);

        // Rolled back to the image it ran before, by its ID: nothing pulled.
        var rollback = await updates.PrepareRollbackAsync(container);
        Assert.Null(rollback.Blocked);
        var rolledBack = await updates.Start(rollback.Plan!).Completion.WaitAsync(TimeSpan.FromMinutes(3));
        await updates.FollowUpOf(container);
        Assert.True(rolledBack.Ok, rolledBack.Message);
        var back = (await _real.InspectContainerAsync(container, default))!;
        Assert.Equal((true, id320), (Running(back), Str(back, "Image")));

        // Removed; nothing of it is left but the images, which the end of the run takes care of.
        await _guarded.RemoveAsync(container, force: true, default);
        Assert.Null(await _real.InspectContainerAsync(container, default));
        Assert.DoesNotContain((await _real.ListContainersAsync(default)).OfType<JsonObject>(),
            c => ImageService.Strings(c["Names"]).Any(n => n.TrimStart('/').StartsWith(container, StringComparison.Ordinal)));
    }

    // The change engine as Program.cs makes it, from the real adapters, whatever its constructor asks for.
    private PortChanger Changer()
    {
        var helpers = new HostHelpers(_guarded, _options, NullLogger<HostHelpers>.Instance) { NamePrefix = $"{Prefix}helper-", InContainer = true };
        var host = new ItHost(_lan);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_options);
        services.AddSingleton(_guarded);
        services.AddSingleton<IChangeJournal>(new DbChangeJournal(_db));
        services.AddSingleton(new ContainerLocks());
        services.AddSingleton<IHostPortChecks>(host);
        services.AddSingleton<IContainerUsers>(host);
        services.AddSingleton<IHostPortProbe>(helpers);
        services.AddSingleton<ILanCheck>(helpers);
        services.AddSingleton<INameCheck>(new NameCheck(_real, _options));
        services.AddSingleton<IDbContextFactory<GitberthDb>>(_db);
        services.AddSingleton(new StatsStore(_options));
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<ContainerUpdater>();
        services.AddSingleton<PortChanger>();
        services.AddSingleton(sp => new Lazy<PortChanger>(() => sp.GetRequiredService<PortChanger>()));
        var changer = services.BuildServiceProvider().GetRequiredService<PortChanger>();
        changer.StableFor = TimeSpan.FromSeconds(5);
        return changer;
    }

    private static bool Running(JsonObject inspect) => inspect["State"]?["Running"]?.GetValueKind() == JsonValueKind.True;

    private static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    private static string Label(JsonObject inspect, string name) => Str(inspect["Config"]?["Labels"], name);

    private sealed class NoHub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>The LAN address given; nobody holds a host port (this test publishes none) and nobody uses its containers.</summary>
    private sealed class ItHost(string lan) : IHostPortChecks, IContainerUsers
    {
        public Task<string> LanAddressAsync(CancellationToken ct) => Task.FromResult(lan);

        public Task<string> LanHostIpAsync(CancellationToken ct) => Task.FromResult("");

        public Task<string?> HolderAsync(string container, int hostPort, string protocol, string hostIp, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> TunnelRoutesAsync(int hostPort, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ContainerUsers> UsersOfAsync(string container, IReadOnlyCollection<string> addresses, CancellationToken ct) => Task.FromResult(ContainerUsers.None);
    }

    /// <summary>What a create needs besides the engine: a slug as is, rows in the test's database, the networks Docker has.</summary>
    private sealed class CreateSteps(DockerEngine real, PortChanger changer, Db db, string lan, Func<string, string> containerFor, ITestOutputHelper output)
        : ICreateSteps
    {
        public Task<NetworkName> ChooseSlugAsync(string name, CancellationToken ct) => Task.FromResult(new NetworkName(AppService.Slugify(name), null));

        public Task<string> LanHostAsync(CancellationToken ct) => Task.FromResult(lan);

        public Task<IReadOnlyList<HostPortUse>> HostPortsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostPortUse>>([]);

        public Task<int> SuggestAsync(int containerPort, string protocol, IEnumerable<int> alsoTaken, CancellationToken ct) => Task.FromResult(containerPort);

        public async Task<IReadOnlySet<string>> NetworksAsync(CancellationToken ct) =>
            (await real.ListNetworksAsync(ct)).OfType<JsonObject>().Select(n => (string?)n["Name"] ?? "").ToHashSet(StringComparer.Ordinal);

        public async Task<AppDefinition> AddAsync(NewAppRow row, CancellationToken ct)
        {
            await using var context = db.CreateDbContext();
            // The container is named by the test's hook, not gb-<slug>: the row says so, as an Existing app's does.
            var app = new AppDefinition
            {
                Name = row.Name, Slug = row.Slug, Source = row.Source, ImageRef = row.ImageRef, UpdatePolicy = row.Options.Updates, AutoDeploy = false,
                Container = containerFor(row.Slug),
            };
            context.Apps.Add(app);
            await context.SaveChangesAsync(ct);
            return app;
        }

        public Task SyncAsync(int appId, CancellationToken ct) => Task.CompletedTask;

        public Task<PortChangePlan> PlanCreateAsync(NewContainerRequest request, CancellationToken ct) => changer.PlanCreateAsync(request, ct);

        public PortChangeJob Start(PortChangePlan plan)
        {
            var job = changer.Start(plan);
            job.Changed += () => { if (job.Current is { } step) output.WriteLine($"  {step.Number}/{step.Count} {step.Text}"); };
            return job;
        }

        public Task RememberAsync(string container, string network, string? alias, string? ipv4, CancellationToken ct) => Task.CompletedTask;

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
        {
            output.WriteLine($"activity {container}: {kind} {tone}: {text}");
            return Task.CompletedTask;
        }

        // A failed create's row goes, as AppCatalog.DiscardAsync does it.
        public async Task DiscardAsync(int appId, CancellationToken ct)
        {
            await using var context = db.CreateDbContext();
            await context.Apps.Where(a => a.Id == appId).ExecuteDeleteAsync(ct);
        }

        public async Task<bool> ExistsAsync(string container, CancellationToken ct) => await real.InspectContainerAsync(container, ct) is not null;

        public Task<UpdatePolicy> DefaultPolicyAsync(CancellationToken ct) => Task.FromResult(UpdatePolicy.Notify);
    }

    /// <summary>What an update needs besides the engine: the row the create made, and the container's settings as Docker has them.</summary>
    private sealed class UpdateSteps(DockerEngine real, PortChanger changer, Db db, ITestOutputHelper output) : IUpdateSteps
    {
        public Task<JsonObject?> InspectContainerAsync(string container, CancellationToken ct) => real.InspectContainerAsync(container, ct);

        public async Task<AppDefinition> AdoptAsync(string key, CancellationToken ct)
        {
            await using var context = db.CreateDbContext();
            return await context.Apps.SingleAsync(a => a.Container == key, ct);
        }

        public Task SyncAsync(int appId, CancellationToken ct) => Task.CompletedTask;

        public async Task<SettingsView?> LoadAsync(string key, CancellationToken ct)
        {
            if (await real.InspectContainerAsync(key, ct) is not { } inspect) return null;
            var image = await real.InspectImageAsync(Str(inspect, "Image"), ct);
            var summary = new AppSummary(key, null, key, key, AppSource.Registry, ContainerRole.Other, ImageOrigin.Registry, Str(inspect["Config"], "Image"), null,
                AppState.Running, "Running", Str(inspect, "Id"), [], null, null, null, false, false, null);
            return new SettingsView(summary, Str(inspect, "Id"), ContainerSettingsJson.Read(inspect, image), new AppOptions { Name = key }, null,
                new EditAccess(null, null, null, false, null, new HashSet<SettingField>()), null);
        }

        public Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct) => changer.PlanAsync(container, request, ct);

        public PortChangeJob Start(PortChangePlan plan)
        {
            var job = changer.Start(plan);
            job.Changed += () => { if (job.Current is { } step) output.WriteLine($"  {step.Number}/{step.Count} {step.Text}"); };
            return job;
        }

        public Task<string?> BusyWithAsync(string container, CancellationToken ct) => changer.BusyWithAsync(container, ct);

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct)
        {
            output.WriteLine($"activity {container}: {kind} {tone}: {text}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The real engine behind a guard: reads go through; anything that changes something is refused unless its container
    /// (as Docker names it, or the new name) and every network it's on start with the prefix, and a create mounts no
    /// volume. A proxy, so it guards whatever calls the engine interface gains.
    /// </summary>
    public class Guard : DispatchProxy
    {
        // Calls that only read: the engine's version and host, inspects, lists, waits, logs, changes, memory use.
        private static readonly string[] Reads = ["Version", "Host", "Inspect", "List", "Wait", "Logs", "Changes", "Memory", "Stats"];

        private IContainerEngine _real = null!;
        private string _prefix = "";

        internal static IContainerEngine Over(IContainerEngine real, string prefix)
        {
            var proxy = Create<IContainerEngine, Guard>();
            var guard = (Guard)(object)proxy;
            guard._real = real;
            guard._prefix = prefix;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var a = args ?? [];
            var name = method!.Name;
            if (Reads.Any(r => name.StartsWith(r, StringComparison.Ordinal))) return method.Invoke(_real, a);
            // Anything else changes something: it must name a container of ours, or it's refused.
            if (name is not ("CreateAsync" or "RenameAsync" or "CreateNetworkAsync" or "RemoveNetworkAsync") && (a.Length == 0 || a[0] is not string))
                Refuse($"{name}, which names no container");
            Func<Task> check = name switch
            {
                "CreateAsync" => () =>
                {
                    Create((string)a[0]!, (JsonObject)a[1]!);
                    return Task.CompletedTask;
                },
                "RenameAsync" => async () =>
                {
                    await TargetAsync((string)a[0]!);
                    Ours((string)a[1]!);
                },
                "CreateNetworkAsync" => () =>
                {
                    Ours(Str((JsonObject)a[0]!, "Name"));
                    return Task.CompletedTask;
                },
                "RemoveNetworkAsync" => () =>
                {
                    Ours((string)a[0]!);
                    return Task.CompletedTask;
                },
                _ => () => TargetAsync((string)a[0]!),
            };
            if (method.ReturnType == typeof(Task)) return RunAsync(check, () => (Task)method.Invoke(_real, a)!);
            var result = method.ReturnType.GetGenericArguments()[0];
            return typeof(Guard).GetMethod(nameof(RunOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(result)
                .Invoke(null, [check, (Func<object>)(() => method.Invoke(_real, a)!)]);
        }

        private static async Task RunAsync(Func<Task> check, Func<Task> call)
        {
            await check();
            await call();
        }

        private static async Task<T> RunOfAsync<T>(Func<Task> check, Func<object> call)
        {
            await check();
            return await (Task<T>)call();
        }

        private void Create(string name, JsonObject body)
        {
            Ours(name);
            var host = body["HostConfig"] as JsonObject;
            var mode = Str(host, "NetworkMode");
            if (mode is not ("" or "default" or "bridge") && !mode.StartsWith(_prefix, StringComparison.Ordinal)) Refuse($"{name} on network {mode}");
            foreach (var (network, _) in body["NetworkingConfig"]?["EndpointsConfig"] as JsonObject ?? [])
                if (network != "bridge" && !network.StartsWith(_prefix, StringComparison.Ordinal)) Refuse($"{name} on network {network}");
            if (host?["Binds"] is JsonArray { Count: > 0 } || host?["Mounts"] is JsonArray { Count: > 0 } || body["Volumes"] is JsonObject { Count: > 0 })
                Refuse($"{name} with a volume");
        }

        // The container's name as Docker has it must be ours; one that isn't there is Docker's 404 to give.
        private async Task TargetAsync(string idOrName)
        {
            if (await _real.InspectContainerAsync(idOrName, default) is { } inspect) Ours(Str(inspect, "Name").TrimStart('/'));
        }

        private void Ours(string name)
        {
            if (!name.StartsWith(_prefix, StringComparison.Ordinal)) Refuse(name);
        }

        private void Refuse(string what) =>
            throw new InvalidOperationException($"The image engine test refused to touch {what}: only {_prefix}* containers and networks, without volumes.");
    }
}
