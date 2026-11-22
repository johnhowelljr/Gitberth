using System.Net;
using System.Text;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Railpack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// gitberth-buildkit, where Railpack's builds run: made privileged on Gitberth's network with Docker's DNS for its build
/// steps and a bounded cache (design 2.4, spike D2 b), made again for a new pin or new settings with its cache kept,
/// started when it's stopped, its buildx builder made once a process and again when a build lost it, stopped when no build
/// needs it, and its size and cache for Settings and the cleanup. The docker CLI and the clock are stand-ins.
/// </summary>
public sealed class RailpackBuildKitTests : IDisposable
{
    private const string Image = RailpackRelease.BuildKitImage;

    private readonly string _logs = Path.Combine(Path.GetTempPath(), "gitberth-bk-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LiveLogHub _hub = new();

    public RailpackBuildKitTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>The docker CLI as gitberth-buildkit's owner uses it, with the container's state.</summary>
    private sealed class Kit
    {
        public bool Exists { get; set; }
        public bool Running { get; set; }
        public string Pin { get; set; } = Image;
        public string? Stamp { get; set; }
        public bool HasImage { get; set; }
        public bool Builder { get; set; }
        public bool Ready { get; set; } = true;
        public string? RunRefused { get; set; }
        public List<List<string>> Calls { get; } = [];
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Commands => Calls.Select(c => string.Join(' ', c.Take(c[0] is "buildx" ? 3 : 2))).ToList();

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> a, ProcessOptions? options, CancellationToken ct)
        {
            lock (Calls) Calls.Add([.. a]);
            return Task.FromResult(a switch
            {
                ["container", "inspect", "--format", _, RailpackRelease.BuildKitContainer] => Exists
                    ? Ok($"{(Running ? "true" : "false")} {Pin} {Stamp ?? "<no value>"}\n") : Fail("Error: No such container: gitberth-buildkit"),
                ["image", "inspect", "--format", "{{.Id}}", Image] => HasImage ? Ok("sha256:1") : Fail("Error: No such image"),
                ["pull", Image] => Pulled(),
                ["run", "-d", ..] => Made(a),
                ["rm", "-f", RailpackRelease.BuildKitContainer] => Gone(),
                ["start", RailpackRelease.BuildKitContainer] => Started(),
                ["stop", "-t", "5", RailpackRelease.BuildKitContainer] => Stop(),
                ["buildx", "inspect", RailpackRelease.BuildKitContainer] => Builder ? Ok("Driver: remote") : Fail("ERROR: no builder \"gitberth-buildkit\" found"),
                ["buildx", "create", "--name", RailpackRelease.BuildKitContainer, "--driver", "remote", "docker-container://gitberth-buildkit"] => MadeBuilder(),
                ["buildx", "inspect", "--bootstrap", RailpackRelease.BuildKitContainer] => Running && Ready ? Ok("Status: running") : Fail("ERROR: failed to connect"),
                ["buildx", "prune", ..] => Ok("Total: 1.2GB"),
                ["volume", "rm", RailpackRelease.BuildKitContainer] => Ok(RailpackRelease.BuildKitContainer),
                ["logs", "--since", _, "--tail", "40", RailpackRelease.BuildKitContainer] => new ProcessResult(0, "", " INFO starting frontend\n ERRO error: %+v\n failed to parse railpack plan\n"),
                _ => Fail("unknown command " + string.Join(' ', a)),
            });
        }

        private static ProcessResult Ok(string output = "") => new(0, output, "");
        private static ProcessResult Fail(string error) => new(1, "", error);

        private ProcessResult Pulled()
        {
            HasImage = true;
            return Ok("v0.34.0: Pulling from moby/buildkit\nDigest: sha256:b059f8d7\n");
        }

        private ProcessResult Made(IReadOnlyList<string> a)
        {
            if (RunRefused is { } why) return new ProcessResult(125, "", why);
            (Exists, Running) = (true, true);
            Pin = a[a.ToList().FindIndex(x => x.StartsWith("gitberth.buildkit-image=", StringComparison.Ordinal))]["gitberth.buildkit-image=".Length..];
            Stamp = a[a.ToList().FindIndex(x => x.StartsWith("gitberth.buildkit-args=", StringComparison.Ordinal))]["gitberth.buildkit-args=".Length..];
            return Ok("4f1c2b\n");
        }

        private ProcessResult Gone()
        {
            (Exists, Running) = (false, false);
            return Ok();
        }

        private ProcessResult Started()
        {
            Running = true;
            return Ok();
        }

        private ProcessResult Stop()
        {
            Running = false;
            Stopped.TrySetResult();
            return Ok();
        }

        private ProcessResult MadeBuilder()
        {
            Builder = true;
            return Ok("gitberth-buildkit");
        }
    }

    /// <summary>A clock whose timers fire when the test says so.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 1, 0, 0, TimeSpan.Zero);
        public List<(TimerCallback Callback, object? State, TimeSpan Due)> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers.Add((callback, state, dueTime));
            return new Held();
        }

        public void Fire(int index) => Timers[index].Callback(Timers[index].State);

        private sealed class Held : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>The engine's system/df for volumes, with gitberth-buildkit's size, and how often it was asked.</summary>
    private sealed class Df(long? size) : HttpMessageHandler
    {
        public int Asked { get; private set; }
        public long? Size { get; set; } = size;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/version") return Task.FromResult(Answer(DockerFixtures.Text("version.json")));
            Asked++;
            var volumes = Size is long bytes
                ? """[{"Name":"gitberth-buildkit","UsageData":{"RefCount":1,"Size":""" + bytes + """}},{"Name":"other","UsageData":{"Size":5}}]"""
                : "[]";
            return Task.FromResult(Answer("""{"VolumeUsage":0,"Volumes":""" + volumes + "}"));
        }

        private static HttpResponseMessage Answer(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private (RailpackBuildKit BuildKit, Kit Kit, ManualTime Time) Make(long disk = 100_000_000_000, Df? df = null, TimeSpan? readyWait = null)
    {
        var kit = new Kit();
        var options = new GitberthOptions { DataDir = _logs };
        var api = df is null ? null : new DockerApi("unix:///var/run/docker.sock", df);
        var time = new ManualTime();
        return (new RailpackBuildKit(new DockerCli(options) { Runner = kit.RunAsync }, options, NullLogger<RailpackBuildKit>.Instance, api)
        {
            DiskTotal = () => disk, Time = time, ReadyPause = TimeSpan.Zero, ReadyWait = readyWait ?? TimeSpan.FromSeconds(60),
        }, kit, time);
    }

    private (DeployLog Log, string Path) Log()
    {
        var path = Path.Combine(_logs, $"deploy-{Guid.NewGuid():N}.log");
        return (new DeployLog(path, _hub.Open(1), []), path);
    }

    // What it runs on a 100 GB disk: half the build cache's 10 GB cap, as buildkitd counts MB.
    private const string Script =
        """mkdir -p /etc/buildkit && printf "[dns]\n  nameservers = [\"127.0.0.11\"]\n" >/etc/buildkit/buildkitd.toml && exec buildkitd --oci-worker-net=host --oci-worker-gc --oci-worker-gc-keepstorage=2048,0,5000""";

    [Fact]
    public async Task The_first_build_pulls_buildkit_once_and_makes_it_privileged_on_gitberths_network()
    {
        var (buildKit, kit, _) = Make();
        var (log, path) = Log();

        await buildKit.EnsureAsync(log, CancellationToken.None);
        log.Dispose();

        Assert.Equal(
        [
            "container inspect", "image inspect", "pull " + Image, "run -d", "buildx inspect gitberth-buildkit", "buildx create --name", "buildx inspect --bootstrap",
        ], kit.Commands);
        Assert.Equal(
        [
            "run", "-d", "--name", "gitberth-buildkit", "--label", "gitberth.helper=buildkit",
            "--label", $"gitberth.buildkit-image={Image}", "--label", $"gitberth.buildkit-args={RailpackBuildKit.Stamp(Script)}",
            "--privileged", "--restart", "no", "--network", "gitberth", "-v", "gitberth-buildkit:/var/lib/buildkit",
            "--entrypoint", "sh", Image, "-c", Script,
        ], kit.Calls.Single(c => c[0] == "run"));
        Assert.Equal(["buildx", "create", "--name", "gitberth-buildkit", "--driver", "remote", "docker-container://gitberth-buildkit"], kit.Calls.Single(c => c is ["buildx", "create", ..]));
        Assert.Contains("==> Downloading BuildKit (moby/buildkit v0.34.0) for Railpack builds, once", await File.ReadAllTextAsync(path));
        Assert.Equal("moby/buildkit:v0.34.0@sha256:b059f8d7226d0b326bc871af489a5dddf65e36af4d20d14251b072974d9ee87c", Image);
    }

    [Fact]
    public void Its_cache_is_bounded_by_the_disk_and_never_under_2_gb()
    {
        Assert.Equal(2048, RailpackBuildKit.KeepStorageMb(8_000_000_000));
        Assert.Equal(5000, RailpackBuildKit.KeepStorageMb(100_000_000_000));
        Assert.Equal(10737, RailpackBuildKit.KeepStorageMb(4_000_000_000_000));
        // One number would be a floor only (spike, point 2): reserved, no free-space target, at most.
        Assert.EndsWith("--oci-worker-gc-keepstorage=2048,0,5000", RailpackBuildKit.Script(5000));
    }

    [Fact]
    public async Task A_new_pin_or_new_settings_make_it_again_and_its_cache_stays()
    {
        foreach (var (pin, stamp) in new[] { ("moby/buildkit:v0.33.0@sha256:old", RailpackBuildKit.Stamp(Script)), (Image, "0123456789ab") })
        {
            var (buildKit, kit, _) = Make();
            (kit.Exists, kit.Running, kit.Pin, kit.Stamp, kit.HasImage, kit.Builder) = (true, true, pin, stamp, true, true);
            var (log, path) = Log();

            await buildKit.EnsureAsync(log, CancellationToken.None);
            log.Dispose();

            Assert.Equal(["container inspect", "rm -f", "image inspect", "run -d", "buildx inspect gitberth-buildkit", "buildx inspect --bootstrap"], kit.Commands);
            Assert.DoesNotContain(kit.Calls, c => c[0] == "volume");
            Assert.Equal((Image, RailpackBuildKit.Stamp(Script)), (kit.Pin, kit.Stamp));
            Assert.Contains("gitberth-buildkit was made from another BuildKit or other settings, so it's made again; its build cache stays.", await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task A_stopped_one_is_started_and_its_builder_is_made_once_a_process()
    {
        var (buildKit, kit, _) = Make();
        (kit.Exists, kit.Running, kit.Stamp, kit.HasImage) = (true, false, RailpackBuildKit.Stamp(Script), true);
        var (log, path) = Log();

        await buildKit.EnsureAsync(log, CancellationToken.None);
        await buildKit.EnsureAsync(log, CancellationToken.None);
        log.Dispose();

        Assert.Equal(
        [
            "container inspect", "start gitberth-buildkit", "buildx inspect gitberth-buildkit", "buildx create --name", "buildx inspect --bootstrap",
            "container inspect", "buildx inspect --bootstrap",
        ], kit.Commands);
        Assert.Contains("==> Starting gitberth-buildkit, Gitberth's BuildKit for Railpack builds", await File.ReadAllTextAsync(path));

        // A build that said its builder is gone ("no builder … found") has it looked at again.
        buildKit.ForgetBuilder();
        kit.Calls.Clear();
        await buildKit.EnsureAsync(Log().Log, CancellationToken.None);
        Assert.Equal(["container inspect", "buildx inspect gitberth-buildkit", "buildx inspect --bootstrap"], kit.Commands);
    }

    [Fact]
    public async Task A_host_that_refuses_privileged_containers_or_one_that_never_answers_says_so()
    {
        var (refused, refusedKit, _) = Make();
        refusedKit.HasImage = true;
        refusedKit.RunRefused = "docker: Error response from daemon: privileged mode is incompatible with user namespaces.\n\nRun 'docker run --help' for more information\n";

        var no = await Assert.ThrowsAsync<DeployException>(() => refused.EnsureAsync(Log().Log, CancellationToken.None));

        Assert.Equal(RailpackText.NoPrivileged("docker: Error response from daemon: privileged mode is incompatible with user namespaces."), no.Message);
        Assert.Contains(refusedKit.Calls, c => c is ["rm", "-f", "gitberth-buildkit"]);

        var (silent, silentKit, _) = Make(readyWait: TimeSpan.Zero);
        (silentKit.HasImage, silentKit.Ready) = (true, false);
        var late = await Assert.ThrowsAsync<DeployException>(() => silent.EnsureAsync(Log().Log, CancellationToken.None));
        Assert.Equal("Gitberth couldn't start gitberth-buildkit, where Railpack builds run: ERROR: failed to connect", late.Message);
    }

    [Fact]
    public async Task It_stops_once_no_build_has_needed_it_for_ten_minutes()
    {
        var (buildKit, kit, time) = Make();
        kit.HasImage = true;
        await buildKit.EnsureAsync(Log().Log, CancellationToken.None);

        buildKit.BuildEnded();
        // Another build comes before the ten minutes are up: that timer stops nothing.
        await buildKit.EnsureAsync(Log().Log, CancellationToken.None);
        time.Fire(0);
        Assert.DoesNotContain(kit.Calls, c => c[0] == "stop");

        buildKit.BuildEnded();
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)], time.Timers.Select(t => t.Due));
        time.Fire(1);
        await kit.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(kit.Calls, c => c is ["stop", "-t", "5", "gitberth-buildkit"]);
        Assert.False(kit.Running);
    }

    [Fact]
    public async Task Its_status_and_its_cache_size_are_for_settings_and_disk()
    {
        var df = new Df(1_714_208_766);
        var (buildKit, kit, time) = Make(df: df);

        Assert.Equal(new RailpackBuildKitStatus(false, false, null, null), await buildKit.StatusAsync(CancellationToken.None));
        Assert.Equal(0, df.Asked);

        (kit.Exists, kit.Running) = (true, true);
        Assert.Equal(new RailpackBuildKitStatus(true, true, Image, 1_714_208_766), await buildKit.StatusAsync(CancellationToken.None));
        // Reading it walks every volume: a page asking again within a minute gets the same.
        df.Size = 5;
        Assert.Equal(1_714_208_766, (await buildKit.StatusAsync(CancellationToken.None)).CacheBytes);
        time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(5, (await buildKit.StatusAsync(CancellationToken.None)).CacheBytes);
        Assert.Equal(2, df.Asked);
    }

    [Fact]
    public async Task Buildkitds_own_words_since_a_build_started()
    {
        var (buildKit, kit, _) = Make();

        var lines = await buildKit.LogTailAsync(new DateTimeOffset(2026, 11, 22, 1, 2, 3, TimeSpan.Zero).AddTicks(4_567_890), CancellationToken.None);

        Assert.Equal(["logs", "--since", "2026-11-22T01:02:03.4567890Z", "--tail", "40", "gitberth-buildkit"], kit.Calls.Single());
        Assert.Equal([" INFO starting frontend", " ERRO error: %+v", " failed to parse railpack plan"], lines);
    }

    [Fact]
    public async Task The_cleanup_prunes_it_by_age_and_removes_it_once_nothing_builds_with_railpack()
    {
        var df = new Df(3_000_000_000);
        var (buildKit, kit, _) = Make(df: df);

        // Not there: nothing to do.
        Assert.Null(await buildKit.CleanAsync(TimeSpan.FromDays(7), anyAppUsesRailpack: true, CancellationToken.None));

        // Running: buildx prunes what no build used for the cleanup's days.
        (kit.Exists, kit.Running) = (true, true);
        var pruned = await buildKit.CleanAsync(TimeSpan.FromDays(7), anyAppUsesRailpack: true, CancellationToken.None);
        Assert.Contains(kit.Calls, c => c.SequenceEqual(["buildx", "prune", "--builder", "gitberth-buildkit", "--force", "--filter", "until=168h"]));
        Assert.Equal(["buildx", "prune", "--builder", "gitberth-buildkit", "--force"], RailpackBuildKit.PruneArgs(null));
        Assert.Equal((3_000_000_000L, 3_000_000_000L, false), (pruned!.Before!.Value, pruned.After!.Value, pruned.Removed));

        // Stopped, and an app still builds with it: left as it is.
        kit.Running = false;
        Assert.Null(await buildKit.CleanAsync(TimeSpan.FromDays(7), anyAppUsesRailpack: true, CancellationToken.None));
        Assert.DoesNotContain(kit.Calls, c => c[0] is "rm" or "volume");

        // Stopped, and none does: the container and its volume go.
        var removed = await buildKit.CleanAsync(TimeSpan.FromDays(7), anyAppUsesRailpack: false, CancellationToken.None);
        Assert.Equal((3_000_000_000L, 0L, true, 3_000_000_000L), (removed!.Before!.Value, removed.After!.Value, removed.Removed, removed.Freed));
        Assert.Contains(kit.Calls, c => c is ["rm", "-f", "gitberth-buildkit"]);
        Assert.Contains(kit.Calls, c => c is ["volume", "rm", "gitberth-buildkit"]);
    }

    [Fact]
    public async Task Clearing_the_build_cache_removes_it_and_its_volume_whatever_runs()
    {
        var (buildKit, kit, _) = Make(df: new Df(1_200_000_000));
        (kit.Exists, kit.Running) = (true, true);

        var cleared = await buildKit.ClearAsync(CancellationToken.None);

        Assert.Equal(1_200_000_000, cleared!.Freed);
        Assert.Equal(["container inspect", "rm -f", "volume rm"], kit.Commands);
        // Then the builder is made again with the next one.
        kit.Calls.Clear();
        kit.HasImage = true;
        await buildKit.EnsureAsync(Log().Log, CancellationToken.None);
        Assert.Contains("buildx inspect gitberth-buildkit", kit.Commands);
    }
}
