using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Git;
using Gitberth.Railpack;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Railpack through the deploy pipeline, with git, Railpack's planner and the docker CLI as stand-ins: which builder each
/// app gets (design 1.2), the exact build (gitberth-buildkit, the frontend by digest, the app's own cache-key, each value a
/// BuildKit secret from the CLI's environment, nothing on its command line), the finishing build (Gitberth's labels and
/// PORT), PORT for the container, a Command through Railpack's bash -c, the build cache and the tools' versions, each way
/// it can fail with the running container untouched, and the web port that follows the one other port an app opens (D4).
/// </summary>
public sealed class RailpackPipelineTests : IDisposable
{
    private const string Sha = "3333333333333333333333333333333333333333";
    private const string Tag = "gitberth/shop:3333333";

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>git: a clone is the test's files, at <see cref="Sha"/>.</summary>
    private sealed class FakeGit(RailpackPipelineTests test)
    {
        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken ct) => Task.FromResult(args switch
        {
            ["clone", .., var dir] => Checkout(dir),
            ["log", ..] => new ProcessResult(0, $"{Sha}\u001fAda\u001f2026-11-21T10:00:00+00:00\u001fAdd start.sh\u001fAdd start.sh\n", ""),
            _ => new ProcessResult(0, "", ""),
        });

        private ProcessResult Checkout(string dir)
        {
            foreach (var (path, text) in test.Files)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            return new ProcessResult(0, "", "");
        }
    }

    /// <summary>Railpack's planner: what it was asked, and the plan it gives (the shell sample's, naming every value it got).</summary>
    private sealed class FakePlanner : IRailpackPlanner
    {
        public List<RailpackPlanRequest> Requests { get; } = [];
        public Func<RailpackPlanRequest, RailpackPlanResult>? Answer { get; set; }

        public Task<RailpackPlanResult> PlanAsync(RailpackPlanRequest request, Action<string>? onLine, CancellationToken ct)
        {
            Requests.Add(request);
            onLine?.Invoke("↳ Detected Shell");
            return Task.FromResult(Answer?.Invoke(request) ?? Planned("plan-shell.json", "info-shell.json", request.Env.Select(e => e.Key)));
        }

        public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>(RailpackRelease.Version);
    }

    // A plan Railpack wrote, its secrets[] the names given (Railpack adds every --env name, and the repo's railpack.json's).
    private static RailpackPlanResult Planned(string plan, string info, IEnumerable<string> secrets)
    {
        var json = RailpackFixtures.Plan(plan);
        var names = secrets.ToList();
        if (names.Count > 0) json["secrets"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray());
        return new RailpackPlanResult(true, null, null, RailpackInfoParser.Parse(RailpackFixtures.Info(info), json), RailpackPlanFile.Write(json), ["Railpack 0.43.0"]);
    }

    private static RailpackPlanResult Failed(RailpackFailure failure, string error) => new(false, failure, error, null, null, []);

    /// <summary>A container the world runs.</summary>
    private sealed class Box
    {
        public required string Name { get; set; }
        public required string Image { get; init; }
        public required List<string> Args { get; init; }
        public Dictionary<string, string> Env { get; init; } = [];
        public bool Running { get; set; } = true;
        /// <summary>The container port its app listens on.</summary>
        public int? ListensOn { get; set; }
        public int Index { get; init; }
    }

    /// <summary>
    /// The docker CLI for a Railpack deploy: gitberth-buildkit and its buildx builder, Railpack's build (buildx build) and the
    /// finishing build, images and their labels, the app's containers (run, stop, rename, rm, inspect, exec of /proc/net/tcp),
    /// and the containers on Gitberth's network.
    /// </summary>
    private sealed class World
    {
        public List<Box> Containers { get; } = [];
        public Dictionary<string, Dictionary<string, string>> Images { get; } = new(StringComparer.Ordinal);
        public List<(List<string> Args, ProcessOptions? Options, string Plan)> Builds { get; } = [];
        public List<(List<string> Args, string Dockerfile)> Finishes { get; } = [];
        public List<List<string>> Calls { get; } = [];
        public Queue<ProcessResult> BuildResults { get; } = new();
        public ProcessResult? FinishResult { get; set; }
        public string? BuildKitRefused { get; set; }
        public bool BuildKit { get; set; }
        public bool BuilderKnown { get; set; } = true;
        /// <summary>Where each container's app listens: by default the PORT it was given, as an app written for Railway does.</summary>
        public Func<Box, int?> Listens { get; set; } = box => box.Env.TryGetValue("PORT", out var port) ? int.Parse(port) : null;
        /// <summary>The ports /proc/net/tcp shows in a container: by default the one its app listens on.</summary>
        public Func<Box, IEnumerable<int>> Sockets { get; set; } = box => box.ListensOn is int port ? [port] : [];
        /// <summary>The app listens on 127.0.0.1 only, where nothing outside its container reaches it.</summary>
        public bool InsideOnly { get; set; }
        private string? _buildKitStamp;
        private int _runs;

        /// <summary>The app containers each name was run as, in order.</summary>
        public Dictionary<string, List<Box>> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
        {
            lock (this) return Task.FromResult(Run([.. a], options));
        }

        private static ProcessResult Ok(string output = "") => new(0, output, "");
        private static ProcessResult Fail(string error, int exit = 1) => new(exit, "", error);
        private Box? Find(string name) => Containers.FirstOrDefault(c => c.Name == name);

        private ProcessResult Run(List<string> a, ProcessOptions? options)
        {
            Calls.Add(a);
            switch (a)
            {
                case ["version", ..]:
                    return Ok("1.52\n");
                case ["info", ..]:
                    return Ok("json-file\n");
                case ["network", "inspect", ..]:
                    return Ok("gitberth\n");
                case ["container", "inspect", "--format", var format, RailpackRelease.BuildKitContainer] when format.Contains("buildkit-image", StringComparison.Ordinal):
                    return BuildKit ? Ok($"true {RailpackRelease.BuildKitImage} {_buildKitStamp}\n") : Fail("Error: No such container: gitberth-buildkit");
                case ["container", "inspect", "--format", var format, ..] when format.StartsWith("{\"Id\"", StringComparison.Ordinal):
                    // gitberth-buildkit is on the network too, as it is once it runs.
                    return Ok(string.Join('\n', a.Skip(4).Select(id => id[3..]).Select(n =>
                        "{\"Id\":\"id-" + n + "\",\"Name\":\"/" + n + "\",\"NetworkSettings\":{\"Networks\":{\"gitberth\":{\"IPAddress\":\""
                        + (n == RailpackRelease.BuildKitContainer ? "172.19.0.9" : "172.19.0.3") + "\",\"Aliases\":[\"" + n + "\"]}}}}")));
                case ["container", "inspect", "--format", "{{.Id}}", var name]:
                    return Find(name) is not null ? Ok("id\n") : Fail("Error: No such container: " + name);
                case ["container", "inspect", var name]:
                    if (Find(name) is not { } box) return Fail("Error: No such container: " + name);
                    var running = box.Running ? "true" : "false";
                    return Ok($$"""[{"State":{"Running":{{running}},"Status":"{{(box.Running ? "running" : "exited")}}","ExitCode":0,"OOMKilled":false},"RestartCount":0}]""");
                case ["ps", "-q", "--no-trunc", "--filter", "network=gitberth"]:
                    return Ok(string.Join('\n', Containers.Where(c => c.Running).Select(c => "id-" + c.Name).Concat(BuildKit ? ["id-gitberth-buildkit"] : [])));
                case ["image", "inspect", "--format", var format, var image]:
                    if (image == RailpackRelease.BuildKitImage) return Ok("sha256:bk\n");
                    if (!Images.TryGetValue(image, out var labels)) return Fail("Error: No such image: " + image);
                    if (format == "{{.Id}}") return Ok("sha256:" + image.GetHashCode().ToString("x") + "\n");
                    if (format == "{{json .Config.Volumes}}") return Ok("null\n");
                    if (format == "{{json .Config.Labels}}") return Ok(JsonSerializer.Serialize(labels) + "\n");
                    var label = format["{{index .Config.Labels \"".Length..^"\"}}".Length];
                    return Ok((labels.TryGetValue(label, out var value) ? value : "<no value>") + "\n");
                case ["image", "rm", var tag]:
                    return Images.Remove(tag) ? Ok() : Fail("Error: No such image: " + tag);
                case ["image", "ls", var repository, ..]:
                    return Ok(string.Join('\n', Images.Keys.Where(k => k.StartsWith(repository + ":", StringComparison.Ordinal))));
                case ["image", "prune", ..] or ["pull", ..] or ["update", ..] or ["logs", "--tail", ..]:
                    return Ok();
                case ["run", "-d", "--name", RailpackRelease.BuildKitContainer, ..]:
                    if (BuildKitRefused is { } refused) return Fail(refused, 125);
                    BuildKit = true;
                    _buildKitStamp = a.First(x => x.StartsWith("gitberth.buildkit-args=", StringComparison.Ordinal))["gitberth.buildkit-args=".Length..];
                    return Ok("bk\n");
                case ["run", "-d", "--name", var name, ..]:
                    return RunApp(name, a, options);
                case ["buildx", "inspect", "--bootstrap", _]:
                    return Ok("Status: running\n");
                case ["buildx", "inspect", _]:
                    return BuilderKnown ? Ok("Driver: remote\n") : Fail("ERROR: no builder \"gitberth-buildkit\" found");
                case ["buildx", "create", ..]:
                    BuilderKnown = true;
                    return Ok("gitberth-buildkit\n");
                case ["buildx", "build", ..]:
                {
                    var plan = File.ReadAllText(a[a.IndexOf("-f") + 1]);
                    Builds.Add((a, options, plan));
                    var result = BuildResults.Count > 0 ? BuildResults.Dequeue() : Ok("#7 DONE 0.1s\n");
                    if (result.Ok) Images[a[a.IndexOf("-t") + 1]] = [];
                    // buildx's config went with a restart of Gitberth: the builder is gone until it's made again.
                    if (result.StdErr.Contains("no builder", StringComparison.Ordinal)) BuilderKnown = false;
                    return result;
                }
                case ["logs", "--since", _, "--tail", "40", RailpackRelease.BuildKitContainer]:
                    return new ProcessResult(0, "", " INFO starting frontend\n ERRO error: %+v\n failed to parse railpack plan: unexpected end of JSON input\n");
                case ["build", ..] when a.Any(x => x.EndsWith(Path.Combine("railpack", "finish", "Dockerfile"), StringComparison.Ordinal)):
                {
                    Finishes.Add((a, File.ReadAllText(a[a.IndexOf("-f") + 1])));
                    if (FinishResult is { } failed) return failed;
                    Images[a[a.IndexOf("-t") + 1]] = a.Select((x, i) => (x, i)).Where(p => p.x == "--label").Select(p => a[p.i + 1].Split('=', 2)).ToDictionary(l => l[0], l => l[1]);
                    return Ok();
                }
                case ["build", ..]:
                    return Fail("the test's docker builds nothing but Railpack's");
                case ["stop", .., var name]:
                    if (Find(name) is { } stopped) stopped.Running = false;
                    return Ok();
                case ["start", var name]:
                    if (Find(name) is not { } started) return Fail("Error: No such container: " + name);
                    started.Running = true;
                    return Ok();
                case ["rename", var from, var to]:
                    if (Find(from) is not { } renamed || Find(to) is not null) return Fail("Error: rename failed");
                    renamed.Name = to;
                    return Ok();
                case ["rm", "-f", var name]:
                    return Containers.RemoveAll(c => c.Name == name) > 0 ? Ok() : Fail("Error: No such container: " + name);
                case ["exec", var name, "cat", "/proc/net/tcp", "/proc/net/tcp6"]:
                    return Find(name) is { } inside ? Ok(Tcp(Sockets(inside), InsideOnly)) : Fail("Error: No such container: " + name);
                default:
                    return Ok();
            }
        }

        private ProcessResult RunApp(string name, List<string> a, ProcessOptions? options)
        {
            if (Find(name) is not null) return Fail($"Conflict. The container name \"/{name}\" is already in use.");
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in a.Select((x, i) => (x, i)).Where(p => p.x == "-e").Select(p => a[p.i + 1]))
                env[e.Split('=')[0]] = e.Contains('=') ? e[(e.IndexOf('=') + 1)..] : options?.Environment?[e] ?? "";
            var box = new Box { Name = name, Image = a.First(x => x.StartsWith("gitberth/", StringComparison.Ordinal)), Args = a, Env = env, Index = _runs++ };
            box.ListensOn = Listens(box);
            Containers.Add(box);
            if (!Ran.TryGetValue(name.Replace(DeployPipeline.BridgeSuffix, ""), out var list)) Ran[name.Replace(DeployPipeline.BridgeSuffix, "")] = list = [];
            list.Add(box);
            return Ok("id\n");
        }

        /// <summary>Whether something answers at a host port: the app's container publishes it from the container port its app listens on.</summary>
        public bool Accepts(int hostPort) => Containers.Any(c => c.Running && c.Name == "gb-shop"
            && c.Args.Select((x, i) => (x, i)).Where(p => p.x == "-p").Select(p => c.Args[p.i + 1].Split('/')[0].Split(':'))
                .Any(p => int.Parse(p[^2]) == hostPort && int.Parse(p[^1]) == c.ListensOn));

        /// <summary>
        /// /proc/net/tcp and tcp6 as cat prints them: Docker's DNS on 127.0.0.11 (in every container on a network of its
        /// own), then each listening port on every address, or on 127.0.0.1 only.
        /// </summary>
        public static string Tcp(IEnumerable<int> ports, bool inside = false) =>
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n"
            + "   0: 0B00007F:A805 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 11 1 0000000000000000 100 0 0 10 0\n"
            + string.Concat(ports.Select((p, i) =>
                $"   {i + 1}: {(inside ? "0100007F" : "00000000")}:{p:X4} 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 {i + 12} 1 0000000000000000 100 0 0 10 0\n"))
            + "  sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";
    }

    private readonly ImageKit.Db _db = new();
    private readonly GitberthOptions _options = new() { DataDir = Path.Combine(Path.GetTempPath(), "gitberth-rpp-" + Guid.NewGuid().ToString("N")[..8]) };
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly World _docker = new();
    private readonly FakePlanner _planner = new();
    private readonly AuditLog _audit = new();
    private readonly DeployPipeline _pipeline;

    /// <summary>The repo's files: the shell sample (design 7.3) unless a test says otherwise.</summary>
    public Dictionary<string, string> Files { get; } = new() { ["start.sh"] = "#!/bin/sh\necho hi\n", ["marker.txt"] = "railpack v1\n" };

    public RailpackPipelineTests()
    {
        Directory.CreateDirectory(_options.LogsDir);
        Directory.CreateDirectory(_options.WorkDir);
        var settings = new SettingsService(_db, _protector);
        var cli = new DockerCli(_options) { Runner = _docker.RunAsync };
        var http = new ImageKit.NoHttp();
        var domains = new DomainService(_db, settings, new CloudflareApi(http), new TunnelLocator(cli), cli, NullLogger<DomainService>.Instance);
        _pipeline = new DeployPipeline(_db, new GitClient(_options, settings) { Runner = new FakeGit(this).RunAsync }, cli, new Detection.ProjectAnalyzer(), new AppLocks(),
            new LiveLogHub(), _protector, _options, http, new RepoVariables(new GitHubApi(http), settings), domains, new WatchState(), new Lifetime(),
            NullLogger<DeployPipeline>.Instance, audit: _audit, railpack: _planner)
        {
            StableFor = TimeSpan.Zero,
            ListenWait = TimeSpan.Zero,
        };
        // A database the app reaches by name, on Gitberth's network.
        _docker.Containers.Add(new Box { Name = "postgres", Image = "gitberth/postgres:16", Args = [] });
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_options.DataDir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<AppDefinition> AppAsync(DockerfileMode mode = DockerfileMode.Auto, Action<AppDefinition>? change = null)
    {
        var app = new AppDefinition { Name = "Shop", Slug = "shop", Owner = "acme", Repo = "shop", Branch = "main", DockerfileMode = mode, VercelVariables = false };
        app.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 3000, IsWeb = true });
        app.EnvVars.Add(new EnvVar { Key = "FOO", ProtectedValue = _protector.Protect("bar"), BuildTime = true });
        app.EnvVars.Add(new EnvVar { Key = "API_TOKEN", ProtectedValue = _protector.Protect("s3cr3t-value-9876"), IsSecret = true, BuildTime = true });
        // Saved before Railpack's rule made them build-time: it still reaches the build.
        app.EnvVars.Add(new EnvVar { Key = "RAILPACK_NODE_VERSION", ProtectedValue = _protector.Protect("22") });
        app.EnvVars.Add(new EnvVar { Key = "RUNTIME_ONLY", ProtectedValue = _protector.Protect("x") });
        change?.Invoke(app);
        return await _db.AddAsync(app);
    }

    private async Task<(Deployment Dep, AppDefinition App, string Log)> DeployAsync(AppDefinition app, bool noCache = false, bool build = true)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var dep = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Manual, NoCache = noCache };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        await _pipeline.RunAsync(new DeployRequest(app.Id, id, Build: build, NoCache: noCache), CancellationToken.None);
        await using var after = _db.CreateDbContext();
        return (await after.Deployments.AsNoTracking().SingleAsync(d => d.Id == id),
            await after.Apps.AsNoTracking().Include(a => a.Ports).SingleAsync(a => a.Id == app.Id),
            await File.ReadAllTextAsync(DeployPaths.LogFile(_options, id)));
    }

    private string Work(int deployment, params string[] parts) => Path.Combine([_options.WorkDir, $"dep-{deployment}", .. parts]);

    // An old version runs: a deploy that fails must leave it as it is.
    private void Running()
    {
        _docker.Images["gitberth/shop:1111111"] = new() { ["gitberth.app"] = "shop", ["gitberth.traits"] = "1" };
        _docker.Containers.Add(new Box { Name = "gb-shop", Image = "gitberth/shop:1111111", Args = [], ListensOn = 8080 });
    }

    [Fact]
    public async Task A_project_gitberth_doesnt_know_is_planned_and_built_by_railpack_and_runs_with_PORT()
    {
        var app = await AppAsync();

        var (dep, after, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal((Tag, "Railpack", "Shell script with Railpack", DockerfileMode.Auto), (after.CurrentImage, after.DetectedKind, after.DetectedSummary, after.DockerfileMode));
        // Planned from the clone, with nothing to keep and no Command: Railpack must find what starts it.
        var request = _planner.Requests.Single();
        Assert.Equal((Work(dep.Id, "src"), "", false), (request.RepoDir, request.AppDir, request.HasStartCommand));
        Assert.Empty(request.Previous);
        // The build-time values, and the RAILPACK_ one saved as runtime-only; never a runtime-only one.
        Assert.Equal(["API_TOKEN", "FOO", "RAILPACK_NODE_VERSION"], request.Env.Select(e => e.Key).Order(StringComparer.Ordinal));
        // The log, in the order a Railpack build says it; the analyzer's advice to customize the Dockerfile isn't in it.
        string[] said =
        [
            "==> Gitberth doesn't know this kind of project, so Railpack builds it",
            "==> Planning the build with Railpack 0.43.0",
            "  | ↳ Detected Shell",
            "==> Railpack found Shell script with Railpack",
            "Reachable by name while building: postgres",
            "==> Building gitberth/shop:3333333 with Railpack",
            "==> Adding Gitberth's labels to the image, and PORT=8080",
            "The app gets PORT=8080, the port Gitberth sends visitors to.",
            "==> Deploy succeeded",
        ];
        Assert.Equal(said, said.OrderBy(s => log.IndexOf(s, StringComparison.Ordinal)).Where(s => log.Contains(s, StringComparison.Ordinal)));
        Assert.DoesNotContain("Customize the Dockerfile", log);
        // gitberth-buildkit is Gitberth's own: no build reaches it by name.
        Assert.DoesNotContain("gitberth-buildkit, postgres", log);
        // The app's container gets PORT, and the image Gitberth's labels.
        Assert.Equal("8080", _docker.Ran["gb-shop"].Single().Env["PORT"]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["gitberth.app"] = "shop", ["gitberth.commit"] = Sha, ["gitberth.port"] = "8080", ["gitberth.traits"] = "1",
            ["gitberth.builder"] = "railpack", ["gitberth.railpack"] = "0.43.0",
        }, _docker.Images[Tag]);
        // Railpack's own tag is gone; the plan it used is kept with the deploy's log.
        Assert.False(_docker.Images.ContainsKey(Tag + "-railpack"));
        Assert.Contains("\".git\"", await File.ReadAllTextAsync(DeployPaths.RailpackPlanCopy(_options, dep.Id)));
        Assert.Contains(_audit.Entries.Last().Changes, c => c == new AuditChange("Built with", null, "Railpack 0.43.0"));
    }

    [Fact]
    public async Task The_build_runs_in_gitberth_buildkit_with_every_value_a_secret_from_the_clis_environment()
    {
        var app = await AppAsync();
        _planner.Answer = r => Planned("plan-shell.json", "info-shell.json", [.. r.Env.Select(e => e.Key), "NPM_TOKEN"]);

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        var (args, options, plan) = _docker.Builds.Single();
        var names = _planner.Requests.Single().Env.Select(e => e.Key).ToList();
        var hash = BuildSecrets.Stamp(await BuildSecrets.KeyAsync(_db, _protector), _planner.Requests.Single().Env);
        Assert.Equal(
        [
            "buildx", "build", "--builder", "gitberth-buildkit", "--load", "--progress=plain", "--provenance=false", "--sbom=false",
            "-f", Work(dep.Id, "railpack", "railpack-plan.json"), "-t", "gitberth/shop:3333333-railpack",
            "--build-arg", "BUILDKIT_SYNTAX=" + RailpackRelease.Frontend,
            "--build-arg", $"cache-key=gb-{app.Id}-shop",
            "--build-arg", "secrets-hash=" + hash,
            .. names.SelectMany((n, i) => new[] { "--secret", $"id={n},env=GITBERTH_SECRET_{i}" }),
            // The repo's railpack.json asked for one the app doesn't set: an empty file.
            "--secret", $"id=NPM_TOKEN,src={Work(dep.Id, "railpack", "empty")}",
            Work(dep.Id, "src"),
        ], args);
        Assert.Contains("warning: the repo's railpack.json asks for NPM_TOKEN, which this app doesn't set: the build gets it empty.", log);
        // The values only in the CLI's environment, under neutral names; a runtime-only one not at all.
        Assert.Equal(names.Select((n, i) => ($"GITBERTH_SECRET_{i}", n switch { "FOO" => "bar", "API_TOKEN" => "s3cr3t-value-9876", _ => "22" })).Append(("BUILDX_GIT_INFO", "0")).Order(),
            options!.Environment!.Select(e => (e.Key, e.Value)).Order());
        Assert.DoesNotContain(args, a => a.Contains("s3cr3t", StringComparison.Ordinal) || a.Contains("RUNTIME_ONLY", StringComparison.Ordinal));
        // The frontend ignores these, and they'd only ask for an entitlement.
        Assert.DoesNotContain(args, a => a is "--network" or "--add-host" or "--label" or "--no-cache" or "--pull");
        // The plan as Gitberth edited it: .git left out.
        Assert.Contains(".git", RailpackPlanFile.Parse(plan)!["exclude"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public async Task The_finishing_build_adds_gitberths_labels_and_PORT_and_a_failed_one_leaves_no_tag()
    {
        var app = await AppAsync(change: a => a.RuntimeExtras = GitExtras.Write(new GitExtras { Command = ["npm", "run", "serve"] }));
        _planner.Answer = r => Planned("plan-node-subsets.json", "info-node-subsets.json", r.Env.Select(e => e.Key));

        var (dep, _, _) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        var (args, dockerfile) = _docker.Finishes.Single();
        Assert.Equal("FROM gitberth/shop:3333333-railpack\nENV PORT=8080\nEXPOSE 8080\n", dockerfile);
        Assert.Equal(
        [
            "build", "--progress=plain", "--provenance=false", "--sbom=false", "-f", Work(dep.Id, "railpack", "finish", "Dockerfile"), "-t", Tag,
            "--label", "gitberth.app=shop", "--label", $"gitberth.commit={Sha}", "--label", "gitberth.port=8080", "--label", "gitberth.traits=1",
            "--label", "gitberth.builder=railpack", "--label", "gitberth.railpack=0.43.0", "--label", "gitberth.railpack.packages=node@24.22.2",
            Work(dep.Id, "railpack", "finish"),
        ], args);
        // A Command of its own starts it (Railpack needn't find one), as one word through the image's bash -c.
        Assert.True(_planner.Requests.Single().HasStartCommand);
        Assert.Equal([Tag, "npm run serve"], _docker.Ran["gb-shop"].Single().Args.TakeLast(2));
        Assert.Equal("Node.js with Railpack", (await _db.AppAsync(app.Id)).DetectedSummary);

        _docker.FinishResult = new ProcessResult(1, "", "ERROR: failed to solve: gitberth/shop:3333333-railpack: not found\n");
        var (failed, _, _) = await DeployAsync(app);

        Assert.Equal(RailpackText.FinishFailed("ERROR: failed to solve: gitberth/shop:3333333-railpack: not found"), failed.Error);
        Assert.False(_docker.Images.ContainsKey(Tag + "-railpack"));
    }

    [Fact]
    public async Task Without_the_cache_every_step_runs_again_and_a_plain_deploy_keeps_the_tool_versions()
    {
        var app = await AppAsync(change: a => (a.CurrentImage, a.DetectedKind) = ("gitberth/shop:1111111", "Railpack"));
        _docker.Images["gitberth/shop:1111111"] = new() { ["gitberth.builder"] = "railpack", ["gitberth.railpack.packages"] = "node@22.11.0 python@3.13.2" };

        var (plain, _, plainLog) = await DeployAsync(app);
        var (fresh, _, freshLog) = await DeployAsync(app, noCache: true);

        Assert.Equal((DeployStatus.Succeeded, DeployStatus.Succeeded), (plain.Status, fresh.Status));
        Assert.Equal(["node@22.11.0", "python@3.13.2"], _planner.Requests[0].Previous);
        Assert.Contains("Keeping the tool versions the running image was built with: node@22.11.0, python@3.13.2", plainLog);
        // Without the cache: no versions kept, a hash of its own, and every command step keyed to it.
        Assert.Empty(_planner.Requests[1].Previous);
        Assert.Contains("==> Building gitberth/shop:3333333 with Railpack without the build cache: every step runs again, and the tools' versions are looked up again", freshLog);
        var hash = _docker.Builds[1].Args.Single(a => a.StartsWith("secrets-hash=", StringComparison.Ordinal));
        Assert.EndsWith($"-{fresh.Id}", hash);
        Assert.Equal(_docker.Builds[0].Args.Single(a => a.StartsWith("secrets-hash=", StringComparison.Ordinal)) + $"-{fresh.Id}", hash);
        var steps = RailpackPlanFile.Parse(_docker.Builds[1].Plan)!["steps"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s["name"]!.GetValue<string>());
        Assert.Equal("*", steps["build"]["secrets"]![0]!.GetValue<string>());

        // The image that ran is gone: its versions went with it.
        _docker.Images.Clear();
        var (_, _, goneLog) = await DeployAsync(app);
        Assert.Contains("Tool versions are looked up again, since the image that ran is gone.", goneLog);
    }

    public static readonly TheoryData<string, DockerfileMode, string, bool, string> Builders = new()
    {
        { "custom Dockerfile", DockerfileMode.Custom, "start.sh", false, "==> Using your custom Dockerfile" },
        { "always Railpack, the repo's Dockerfile unused", DockerfileMode.Railpack, "Dockerfile", true, "The repo's Dockerfile isn't used: Railpack builds from the source." },
        { "the repo's Dockerfile", DockerfileMode.Auto, "Dockerfile", false, "==> Using the repo's Dockerfile" },
        { "a kind Gitberth knows", DockerfileMode.Auto, "index.html", false, "==> Detected Static website" },
        { "nothing Gitberth knows", DockerfileMode.Auto, "start.sh", true, "==> Gitberth doesn't know this kind of project, so Railpack builds it" },
        { "an Unknown Gitberth explains", DockerfileMode.Auto, "package.json", false, "Failed: Gitberth couldn't work out how to build this project." },
    };

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task Each_app_gets_the_builder_its_settings_and_its_repo_say(string what, DockerfileMode mode, string file, bool railpack, string said)
    {
        Files.Clear();
        Files[file] = file switch { "Dockerfile" => "FROM alpine:3.22\nCMD [\"true\"]\n", "package.json" => "{ not json", "index.html" => "<h1>hi</h1>", _ => "echo hi\n" };
        var app = await AppAsync(mode, a => a.CustomDockerfile = mode == DockerfileMode.Custom ? "FROM alpine:3.22\nCMD [\"true\"]\n" : null);

        var (_, _, log) = await DeployAsync(app);

        Assert.True(railpack == (_planner.Requests.Count == 1), what);
        Assert.Contains(said, log);
        Assert.Equal(railpack, _docker.Builds.Count == 1);
    }

    public static readonly TheoryData<string, DockerfileMode, RailpackFailure, string, string> PlanFailures = new()
    {
        { "nothing to build, as a fallback", DockerfileMode.Auto, RailpackFailure.NothingFound, RailpackText.CantBuild(RailpackText.FoundNothing), RailpackText.NeitherCanBuild },
        { "nothing to build, Railpack chosen", DockerfileMode.Railpack, RailpackFailure.NothingFound, RailpackText.CantBuild(RailpackText.FoundNothing),
            RailpackText.CantBuild(RailpackText.FoundNothing) },
        { "no start command", DockerfileMode.Railpack, RailpackFailure.NoStartCommand, RailpackText.NoStartCommand("node"), RailpackText.NoStartCommand("node") },
        { "a broken railpack.json", DockerfileMode.Auto, RailpackFailure.BadConfig, RailpackText.BadConfig("line 4"), RailpackText.BadConfig("line 4") },
        { "offline", DockerfileMode.Auto, RailpackFailure.Offline, RailpackText.Offline("dns error"), RailpackText.Offline("dns error") },
        { "too slow", DockerfileMode.Railpack, RailpackFailure.TimedOut, RailpackText.TimedOut, RailpackText.TimedOut },
        { "no Railpack here", DockerfileMode.Railpack, RailpackFailure.NotInstalled, RailpackText.NotInstalled, RailpackText.NotInstalled },
        { "no sandbox", DockerfileMode.Auto, RailpackFailure.NoSandbox, RailpackText.NoSandbox("no image"), RailpackText.NoSandbox("no image") },
    };

    [Theory]
    [MemberData(nameof(PlanFailures))]
    public async Task A_plan_that_fails_ends_the_deploy_with_its_words_and_the_running_container_untouched(string what, DockerfileMode mode, RailpackFailure failure,
        string error, string ends)
    {
        Running();
        var app = await AppAsync(mode, a => (a.CurrentImage, a.CurrentCommit) = ("gitberth/shop:1111111", "1111111111111111111111111111111111111111"));
        _planner.Answer = _ => Failed(failure, error);

        var (dep, after, log) = await DeployAsync(app);

        Assert.Equal((DeployStatus.Failed, ends), (dep.Status, dep.Error!));
        Assert.Contains("Failed: " + ends, log);
        Assert.Empty(_docker.Builds);
        Assert.DoesNotContain(_docker.Calls, c => c[0] is "stop" or "rename" or "run" && c.Contains("gb-shop"));
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-shop").Running, what);
        Assert.Equal("gitberth/shop:1111111", after.CurrentImage);
        // Neither could build it: it stays Unknown, as before.
        if (mode == DockerfileMode.Auto && failure == RailpackFailure.NothingFound) Assert.Equal(("Unknown", "Unknown project type"), (after.DetectedKind, after.DetectedSummary));
    }

    [Fact]
    public async Task A_build_that_fails_says_buildkitds_own_words_and_touches_nothing()
    {
        Running();
        var app = await AppAsync(change: a => a.CurrentImage = "gitberth/shop:1111111");
        _docker.BuildResults.Enqueue(new ProcessResult(1, "", "ERROR: failed to build: failed to solve: failed to parse railpack plan: unexpected end of JSON input\n"));

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal((DeployStatus.Failed, RailpackText.BuildFailed), (dep.Status, dep.Error!));
        Assert.Contains("  buildkit |  ERRO error: %+v", log);
        Assert.Contains("  buildkit |  failed to parse railpack plan: unexpected end of JSON input", log);
        // Its logs are read from when this build started, to the fraction of a second.
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$", _docker.Calls.Single(c => c is ["logs", "--since", ..])[2]);
        Assert.Empty(_docker.Finishes);
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-shop").Running);
    }

    [Fact]
    public async Task A_host_that_refuses_privileged_containers_says_so_before_anything_is_built()
    {
        Running();
        var app = await AppAsync(change: a => a.CurrentImage = "gitberth/shop:1111111");
        _docker.BuildKitRefused = "docker: Error response from daemon: privileged mode is incompatible with user namespaces.\n";

        var (dep, _, _) = await DeployAsync(app);

        Assert.Equal(RailpackText.NoPrivileged("docker: Error response from daemon: privileged mode is incompatible with user namespaces."), dep.Error);
        Assert.Empty(_docker.Builds);
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-shop").Running);
    }

    [Fact]
    public async Task A_builder_gitberth_lost_is_made_again_and_the_build_runs_once_more()
    {
        var app = await AppAsync();
        // It was there when Gitberth looked, and gone when the build ran (Gitberth restarted with its buildx config lost).
        _docker.BuildResults.Enqueue(new ProcessResult(1, "", "ERROR: no builder \"gitberth-buildkit\" found\n"));

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(2, _docker.Builds.Count);
        Assert.Contains("gitberth-buildkit wasn't there for the build, so it's made ready again.", log);
        // Looked for again, and made again once.
        Assert.Equal(2, _docker.Calls.Count(c => c is ["buildx", "inspect", RailpackRelease.BuildKitContainer]));
        Assert.Single(_docker.Calls, c => c is ["buildx", "create", "--name", RailpackRelease.BuildKitContainer, "--driver", "remote", "docker-container://gitberth-buildkit"]);
    }

    [Fact]
    public async Task A_recreate_of_a_railpack_image_gets_PORT_and_its_label_never_pulls_the_web_port_back()
    {
        // A deploy moved its web port to 4000, where its app listens; the image was built for 8080.
        _docker.Images["gitberth/shop:3333333"] = new() { ["gitberth.app"] = "shop", ["gitberth.traits"] = "1", ["gitberth.port"] = "8080", ["gitberth.builder"] = "railpack" };
        var app = await AppAsync(DockerfileMode.Auto, a =>
        {
            (a.CurrentImage, a.DetectedKind, a.DetectedPort) = (Tag, "Railpack", 4000);
            a.Ports.Single().ContainerPort = 4000;
            a.RuntimeExtras = GitExtras.Write(new GitExtras { Command = ["npm", "run", "serve"] });
        });

        var (dep, after, log) = await DeployAsync(app, build: false);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(4000, after.Ports.Single().ContainerPort);
        Assert.DoesNotContain("so the web port follows it", log);
        var box = _docker.Ran["gb-shop"].Single();
        Assert.Equal("4000", box.Env["PORT"]);
        Assert.Equal([Tag, "npm run serve"], box.Args.TakeLast(2));
        Assert.Empty(_planner.Requests);
    }

    [Fact]
    public async Task An_app_that_listens_on_one_other_port_gets_its_web_port_moved_there_in_the_same_deploy()
    {
        var app = await AppAsync();
        // It ignores PORT and listens on 4000.
        _docker.Listens = _ => 4000;
        _pipeline.ListenWait = TimeSpan.FromTicks(1);
        _pipeline.Accepts = (_, port) => Task.FromResult(_docker.Accepts(port));

        var (dep, after, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("==> It listens on port 4000, not 8080, so its web port moves to 4000.", log);
        Assert.Contains("The app gets PORT=4000, the port Gitberth sends visitors to.", log);
        Assert.Equal((4000, 4000), (after.Ports.Single().ContainerPort, after.DetectedPort!.Value));
        var runs = _docker.Ran["gb-shop"];
        Assert.Equal(["3000:8080", "3000:4000"], runs.Select(r => r.Args[r.Args.IndexOf("-p") + 1]));
        Assert.Equal(["8080", "4000"], runs.Select(r => r.Env["PORT"]));
        // Read from inside the container, only once the wait had run out.
        Assert.Single(_docker.Calls, c => c is ["exec", "gb-shop", "cat", "/proc/net/tcp", "/proc/net/tcp6"]);
    }

    [Fact]
    public async Task An_app_on_several_other_ports_or_one_that_moves_again_fails_naming_them()
    {
        var app = await AppAsync();
        _docker.Listens = _ => 4000;
        _docker.Sockets = _ => [4000, 9000];
        _pipeline.ListenWait = TimeSpan.FromTicks(1);
        _pipeline.Accepts = (_, port) => Task.FromResult(_docker.Accepts(port));

        var (several, after, _) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Failed, several.Status);
        Assert.Equal("gb-shop didn't open port 8080 within 0 seconds. It listens on 4000 and 9000, not 8080: set its web port on the Network tab, or have it listen on $PORT.",
            several.Error);
        Assert.Equal(8080, after.Ports.Single().ContainerPort);
        Assert.Single(_docker.Ran["gb-shop"]);

        // At most once a deploy: an app that's somewhere else again after the move fails, and the web port stays.
        _docker.Ran.Clear();
        _docker.Listens = _ => _docker.Ran.TryGetValue("gb-shop", out var ran) && ran.Count > 0 ? 9000 : 4000;
        _docker.Sockets = box => box.ListensOn is int port ? [port] : [];
        var (again, still, _) = await DeployAsync(app);

        Assert.Equal("gb-shop didn't open port 4000 within 0 seconds. It listens on 9000, not 4000: set its web port on the Network tab, or have it listen on $PORT.",
            again.Error);
        Assert.Equal(8080, still.Ports.Single().ContainerPort);
        Assert.Equal(2, _docker.Ran["gb-shop"].Count);
    }

    [Fact]
    public async Task An_app_that_listens_only_inside_its_container_or_nowhere_fails_saying_so_and_its_port_stays()
    {
        var app = await AppAsync();
        _docker.Listens = _ => 4000;
        _docker.InsideOnly = true;
        _pipeline.ListenWait = TimeSpan.FromTicks(1);
        _pipeline.Accepts = (_, port) => Task.FromResult(_docker.Accepts(port));

        var (inside, after, _) = await DeployAsync(app);

        Assert.Equal("gb-shop didn't open port 8080 within 0 seconds. It listens on 4000 only inside its container (127.0.0.1): have it listen on 0.0.0.0, on $PORT.",
            inside.Error);
        Assert.Equal(8080, after.Ports.Single().ContainerPort);
        Assert.Single(_docker.Ran["gb-shop"]);

        // Docker's own DNS listens on 127.0.0.11 in there too: it's never taken for the app's.
        _docker.Listens = _ => null;
        var (nowhere, _, _) = await DeployAsync(app);
        Assert.Equal("gb-shop didn't open port 8080 within 0 seconds. It doesn't listen on any other port either: have it listen on $PORT.", nowhere.Error);
    }

    [Fact]
    public async Task A_running_app_whose_new_version_listens_elsewhere_keeps_its_old_one_until_the_moved_one_works()
    {
        Running();
        var app = await AppAsync(change: a => (a.CurrentImage, a.CurrentCommit) = ("gitberth/shop:1111111", "1111111111111111111111111111111111111111"));
        // The new version ignores PORT and listens on 4000.
        _docker.Listens = box => box.Image == Tag ? 4000 : 8080;
        _pipeline.ListenWait = TimeSpan.FromTicks(1);
        _pipeline.Accepts = (_, port) => Task.FromResult(_docker.Accepts(port));

        var (dep, after, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Contains("==> It listens on port 4000, not 8080, so its web port moves to 4000.", log);
        Assert.Equal((4000, Tag), (after.Ports.Single().ContainerPort, after.CurrentImage));
        // The old version was started again between the two tries, and went once the moved one worked.
        Assert.Single(_docker.Calls, c => c is ["rename", "gb-shop_prev", "gb-shop"]);
        var box = Assert.Single(_docker.Containers, c => c.Name.StartsWith("gb-shop", StringComparison.Ordinal));
        Assert.Equal((Tag, "3000:4000", "4000", true), (box.Image, box.Args[box.Args.IndexOf("-p") + 1], box.Env["PORT"], box.Running));
    }

    // ---- The security review's fixes ----

    [Fact]
    public async Task A_railpack_setting_that_is_secret_anyway_reaches_neither_the_plan_nor_the_build_and_the_log_says_why()
    {
        // Saved secret before the saves refused one; and one that only names a secret, which goes to Railpack as it is.
        var app = await AppAsync(change: a =>
        {
            a.EnvVars.Add(new EnvVar { Key = "RAILPACK_START_CMD", ProtectedValue = _protector.Protect("./server --api-key s3cr3t-api-key"), IsSecret = true, BuildTime = true });
            a.EnvVars.Add(new EnvVar { Key = "RAILPACK_BUILD_CMD", ProtectedValue = _protector.Protect("npm run build -- --token $API_TOKEN"), BuildTime = true });
        });

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal(DeployStatus.Succeeded, dep.Status);
        Assert.Equal(["API_TOKEN", "FOO", "RAILPACK_BUILD_CMD", "RAILPACK_NODE_VERSION"], _planner.Requests.Single().Env.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(_docker.Builds.Single().Args, a => a.Contains("RAILPACK_START_CMD", StringComparison.Ordinal));
        Assert.DoesNotContain(_docker.Builds.Single().Options!.Environment!.Values, v => v.Contains("s3cr3t-api-key", StringComparison.Ordinal));
        Assert.Contains(RailpackText.SecretSettingsLeftOut(["RAILPACK_START_CMD"]), log);
        // The running container still gets it: it's the app's.
        Assert.True(_docker.Ran["gb-shop"].Single().Env.ContainsKey("RAILPACK_START_CMD"));
    }

    [Fact]
    public async Task A_secret_name_that_isnt_a_variable_name_never_reaches_a_secret_flag_and_nothing_is_built()
    {
        Running();
        var app = await AppAsync(change: a => a.CurrentImage = "gitberth/shop:1111111");
        // A railpack.json asking for "X,src=/data/gitberth.db": in a --secret flag it would name a file of Gitberth's.
        _planner.Answer = r => Planned("plan-shell.json", "info-shell.json", [.. r.Env.Select(e => e.Key), "X,src=/data/gitberth.db"]);

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal((DeployStatus.Failed, RailpackText.BadSecretName("X,src=/data/gitberth.db")), (dep.Status, dep.Error!));
        Assert.Contains("Failed: " + RailpackText.BadSecretName("X,src=/data/gitberth.db"), log);
        Assert.Empty(_docker.Builds);
        Assert.True(_docker.Containers.Single(c => c.Name == "gb-shop").Running);

        // A name the app sets itself was checked when it was saved (no comma, no =): it goes as it is, my.flag included.
        _planner.Answer = null;
        var (built, _, _) = await DeployAsync(await AppAsync(change: a =>
            (a.Name, a.Slug, a.EnvVars) = ("Shop 2", "shop-2", [new EnvVar { Key = "my.flag", ProtectedValue = _protector.Protect("on"), BuildTime = true }])));
        Assert.Equal(DeployStatus.Succeeded, built.Status);
        Assert.Contains($"id=my.flag,env=GITBERTH_SECRET_0", _docker.Builds.Single().Args);
        Assert.Equal((true, false, false), (RailpackSettings.IsVariableName("NPM_TOKEN"), RailpackSettings.IsVariableName("my.flag"), RailpackSettings.IsVariableName("9LIVES")));
    }

    [Fact]
    public async Task A_deploys_error_is_masked_with_its_secret_values_as_its_log_is()
    {
        var app = await AppAsync();
        // Railpack quotes what it was given in its own words, and they reach the deploy's error.
        _planner.Answer = _ => Failed(RailpackFailure.Other, RailpackText.CantBuild("token s3cr3t-value-9876 isn't valid for npm"));

        var (dep, _, log) = await DeployAsync(app);

        Assert.Equal(RailpackText.CantBuild("token •••• isn't valid for npm"), dep.Error);
        Assert.DoesNotContain("s3cr3t-value-9876", log);
    }
}
