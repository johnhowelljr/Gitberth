using System.ComponentModel;
using Gitberth.Docker;
using Gitberth.Railpack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// railpack prepare in its throwaway container (design 2.2): the helper's exact arguments (every sandbox flag, Gitberth's
/// own network view, the repo read-only, no secret's value anywhere), the start command and versions it's given, and how
/// each way it ends comes back: a plan, a retry of what may pass, a failure in Railpack's words, a timeout, a cancel. The
/// docker CLI is a stand-in that writes Railpack's own files (Fixtures/Railpack) where the helper's /out is mounted.
/// </summary>
public sealed class RailpackPlannerTests : IDisposable
{
    private const string HostData = "/var/lib/docker/volumes/gitberth-data/_data";
    private const string Own = "0c1a38670237";
    private const string OwnImage = "sha256:6f7e1b2c3d4e";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "gitberth-rp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo;

    public RailpackPlannerTests()
    {
        _repo = Path.Combine(_data, "work", "dep-7", "src");
        Directory.CreateDirectory(Path.Combine(_repo, "web"));
        File.WriteAllText(Path.Combine(_repo, "web", "start.sh"), "echo hi\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>One run of prepare: what the stand-in writes and prints, and how it ends.</summary>
    private sealed record Run(int Exit, string? Plan = null, string? Info = null, string StdOut = "", string StdErr = "");

    /// <summary>The docker CLI as the planner uses it: Gitberth's own container's image, the helper, and rm -f.</summary>
    private sealed class FakeDocker(RailpackPlannerTests test)
    {
        public List<(List<string> Args, ProcessOptions? Options)> Calls { get; } = [];
        public Queue<Run> Runs { get; } = new();
        /// <summary>Stands in for prepare that never ends: it waits for the token.</summary>
        public bool Hang { get; set; }
        public Action? OnPrepare { get; set; }
        public int OwnImageExit { get; set; }

        public List<List<string>> Helpers => Calls.Where(c => c.Args is ["run", ..]).Select(c => c.Args).ToList();

        public async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, ProcessOptions? options, CancellationToken ct)
        {
            lock (Calls) Calls.Add(([.. args], options));
            switch (args)
            {
                case ["container", "inspect", "--format", "{{.Image}}", Own]:
                    return OwnImageExit == 0 ? new ProcessResult(0, OwnImage + "\n", "") : new ProcessResult(OwnImageExit, "", "Error: No such container");
                case ["rm", "-f", _]:
                    return new ProcessResult(0, "", "");
                case ["run", ..]:
                    OnPrepare?.Invoke();
                    if (Hang) await Task.Delay(Timeout.Infinite, ct);
                    var run = Runs.Dequeue();
                    var outDir = test.Local(args.First(a => a.EndsWith(":/out", StringComparison.Ordinal))[..^":/out".Length]);
                    if (run.Plan is not null) File.WriteAllText(Path.Combine(outDir, RailpackPlanner.PlanFile), RailpackFixtures.Text(run.Plan));
                    if (run.Info is not null) File.WriteAllText(Path.Combine(outDir, RailpackPlanner.InfoFile), RailpackFixtures.Text(run.Info));
                    foreach (var line in (run.StdOut + run.StdErr).Split('\n')) options?.OnLine?.Invoke(line.TrimEnd('\r'));
                    return new ProcessResult(run.Exit, run.StdOut, run.StdErr);
                default:
                    return new ProcessResult(1, "", "unknown command " + string.Join(' ', args));
            }
        }
    }

    // A host path under Gitberth's data folder, as the helper mounts it, back to the folder it is here.
    private string Local(string hostPath) => Path.Combine(_data, hostPath[(HostData.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));

    private (RailpackPlanner Planner, FakeDocker Docker) Planner(bool container = true, Func<string, IReadOnlyList<string>, ProcessOptions, CancellationToken, Task<ProcessResult>>? inProcess = null,
        TimeSpan? timeout = null)
    {
        var options = new GitberthOptions { DataDir = _data, RunningInContainer = container, OwnContainer = container ? Own : null };
        var docker = new FakeDocker(this);
        var cli = new DockerCli(options) { Runner = docker.RunAsync };
        var self = new SelfContainer(cli, options, NullLogger<SelfContainer>.Instance) { DataHostDir = HostData };
        return (new RailpackPlanner(cli, self, options, NullLogger<RailpackPlanner>.Instance)
        {
            RetryDelay = TimeSpan.Zero, Timeout = timeout ?? TimeSpan.FromMinutes(5), InProcessRunner = inProcess,
        }, docker);
    }

    private RailpackPlanRequest Request(bool command = false, IReadOnlyList<string>? previous = null) => new(_repo, "web",
    [
        new EnvValue("RAILPACK_NODE_VERSION", "22", IsSecret: false),
        new EnvValue("API_KEY", "s3cret-value-1234", IsSecret: true),
        new EnvValue("CGO_ENABLED", "0", IsSecret: false),
        new EnvValue("PATH", "/opt/bin", IsSecret: false),
        new EnvValue("my.flag", "on", IsSecret: false),
        new EnvValue("RAILPACK_PACKAGES_TOKEN", "railpack-secret-5678", IsSecret: true),
    ], command, previous ?? []);

    private static IEnumerable<string> Proxies() =>
        new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" }.Where(p => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p))).SelectMany(p => new[] { "-e", p });

    [Fact]
    public async Task Prepare_runs_sandboxed_with_gitberths_own_network_and_never_a_secret()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json", RailpackFixtures.Text("prepare-shell.txt")));

        var plan = await planner.PlanAsync(Request(previous: ["node@22.11.0", "junk"]), null, CancellationToken.None);

        Assert.True(plan.Ok);
        var (args, options) = docker.Calls.Single(c => c.Args is ["run", ..]);
        var name = args[args.IndexOf("--name") + 1];
        Assert.Matches("^gitberth-helper-railpack-[0-9a-f]{12}$", name);
        var outHost = args.First(a => a.EndsWith(":/out", StringComparison.Ordinal))[..^":/out".Length];
        Assert.StartsWith($"{HostData}/work/dep-7/src-railpack-", outHost);
        Assert.Equal(
        [
            "run", "--rm", "--name", name, "--label", "gitberth.helper=railpack",
            "--network", $"container:{Own}",
            "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "1g", "--pids-limit", "512",
            "--tmpfs", "/tmp:rw,exec,nosuid,size=512m", "--tmpfs", _data,
            "-v", $"{HostData}/work/dep-7/src:/workspace:ro", "-v", $"{outHost}:/out",
            "-w", "/tmp", "-e", "HOME=/tmp", "-e", "NO_COLOR=1", .. Proxies(),
            // By name, its value in the docker CLI's environment; a name that can't go there, on the line: a plain value only.
            "-e", "RAILPACK_NODE_VERSION", "-e", "API_KEY", "-e", "CGO_ENABLED", "-e", "my.flag=on",
            "--entrypoint", "/bin/sh", OwnImage,
            "-c", "mkdir -p /tmp/railpack/mise && ln -sf /usr/local/lib/railpack/mise /tmp/railpack/mise/mise-2026.10.14 && exec railpack \"$@\"", "railpack",
            "prepare", "/workspace/web", "--plan-out", "/out/railpack-plan.json", "--info-out", "/out/railpack-info.json",
            "--error-missing-start", "--previous", "node@22.11.0",
            "--env", "RAILPACK_NODE_VERSION", "--env", "API_KEY", "--env", "CGO_ENABLED", "--env", "my.flag",
        ], args);
        // A secret reaches planning as a placeholder: the plan names values only. A secret RAILPACK_ one doesn't reach it at
        // all, since Railpack would write it into the plan.
        Assert.True(options!.CleanEnvironment);
        Assert.Equal(1024 * 1024, options.MaxCapture);
        Assert.Equal(new Dictionary<string, string>
        {
            ["RAILPACK_NODE_VERSION"] = "22", ["API_KEY"] = RailpackPlanner.Placeholder, ["CGO_ENABLED"] = "0",
        }, options.Environment);
        Assert.DoesNotContain(docker.Calls.SelectMany(c => c.Args), a => a.Contains("s3cret-value-1234", StringComparison.Ordinal) || a.Contains("railpack-secret-5678", StringComparison.Ordinal));
        // PATH would take the place of every build step's own: never passed.
        Assert.DoesNotContain(args, a => a.StartsWith("PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_that_worked_comes_back_with_railpacks_files_and_output_and_its_folder_goes()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json", RailpackFixtures.Text("prepare-shell.txt")));
        var lines = new List<string>();

        var plan = await planner.PlanAsync(Request(), lines.Add, CancellationToken.None);

        Assert.Equal((true, (RailpackFailure?)null, (string?)null), (plan.Ok, plan.Failure, plan.Error));
        Assert.Equal(RailpackFixtures.Text("plan-shell.json"), plan.PlanJson);
        Assert.Equal(("shell", "sh start.sh"), (plan.Info!.Provider, plan.Info.StartCommand));
        // prepare's own words, as it printed them, without the box's trailing spaces or blank ends.
        Assert.Equal("╭─────────────────╮", plan.Output[0]);
        Assert.Equal("    $ sh start.sh", plan.Output[^1]);
        Assert.Contains("  ↳ Detected Shell", lines);
        var outHost = docker.Helpers[0].First(a => a.EndsWith(":/out", StringComparison.Ordinal))[..^":/out".Length];
        Assert.False(Directory.Exists(Local(outHost)));
    }

    [Fact]
    public async Task A_command_of_the_apps_own_means_prepare_needs_no_start_command()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json"));
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json"));

        await planner.PlanAsync(Request(command: true), null, CancellationToken.None);
        await planner.PlanAsync(Request(command: false), null, CancellationToken.None);

        Assert.DoesNotContain("--error-missing-start", docker.Helpers[0]);
        Assert.Contains("--error-missing-start", docker.Helpers[1]);
        Assert.DoesNotContain("--previous", docker.Helpers[1]);
    }

    [Fact]
    public async Task Railpacks_try_again_is_tried_once_more()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(75, StdErr: RailpackFixtures.Text("prepare-shell-noseed.err")));
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json"));

        var plan = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.True(plan.Ok);
        Assert.Equal(2, docker.Helpers.Count);

        // Twice: the tools couldn't be looked up, in the network's own words.
        docker.Runs.Enqueue(new Run(75, StdErr: RailpackFixtures.Text("prepare-shell-noseed.err")));
        docker.Runs.Enqueue(new Run(75, StdErr: RailpackFixtures.Text("prepare-shell-noseed.err")));
        var offline = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.Offline, "Railpack couldn't look up the tools' versions online (dial tcp 127.0.0.1:443: connect: connection refused). The next deploy tries again."),
            (offline.Failure!.Value, offline.Error!));
        Assert.Equal(4, docker.Helpers.Count);
    }

    [Fact]
    public async Task A_version_lookup_the_network_failed_is_tried_once_more_and_nothing_to_build_isnt()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(1, Info: "info-offline.json", StdOut: RailpackFixtures.Text("prepare-offline.txt")));
        docker.Runs.Enqueue(new Run(1, Info: "info-offline.json", StdOut: RailpackFixtures.Text("prepare-offline.txt")));

        var offline = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.Offline, "Railpack couldn't look up the tools' versions online (failed to lookup address information: Try again). The next deploy tries again."),
            (offline.Failure!.Value, offline.Error!));
        Assert.Equal(2, docker.Helpers.Count);

        docker.Runs.Enqueue(new Run(1, Info: "info-nothing.json"));
        var nothing = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.NothingFound, RailpackText.CantBuild(RailpackText.FoundNothing)), (nothing.Failure!.Value, nothing.Error!));
        // Its logs come back too, for New app's banner.
        Assert.Contains(nothing.Info!.Logs, l => l.Level == "error");
        Assert.Equal(3, docker.Helpers.Count);
    }

    [Fact]
    public async Task No_start_command_and_a_broken_railpack_json_say_so()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(1, Info: "info-nostart.json"));
        docker.Runs.Enqueue(new Run(1, Info: "info-badconfig.json"));

        var nostart = await planner.PlanAsync(Request(), null, CancellationToken.None);
        var badconfig = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.NoStartCommand,
            "Railpack found a Node.js project but no command that starts it. Set one in the app's Settings › Command, or as deploy.startCommand in the repo's railpack.json."),
            (nostart.Failure!.Value, nostart.Error!));
        Assert.Equal((RailpackFailure.BadConfig, "Railpack couldn't read the repo's railpack.json: line 4, column 1: parsing object after value: unexpected EOF"),
            (badconfig.Failure!.Value, badconfig.Error!));
        Assert.Equal(2, docker.Helpers.Count);
    }

    [Fact]
    public async Task Five_minutes_gone_stops_it_and_its_container_goes()
    {
        var (planner, docker) = Planner(timeout: TimeSpan.FromTicks(1));
        docker.Hang = true;

        var plan = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.TimedOut, RailpackText.TimedOut), (plan.Failure!.Value, plan.Error!));
        var name = docker.Helpers.Single()[3];
        Assert.Contains(docker.Calls, c => c.Args.SequenceEqual(["rm", "-f", name]));
    }

    [Fact]
    public async Task Cancelling_it_removes_its_container_too()
    {
        var (planner, docker) = Planner();
        using var cts = new CancellationTokenSource();
        docker.Hang = true;
        docker.OnPrepare = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.PlanAsync(Request(), null, cts.Token));

        Assert.Contains(docker.Calls, c => c.Args.SequenceEqual(["rm", "-f", docker.Helpers.Single()[3]]));
    }

    [Fact]
    public async Task Docker_refusing_the_helper_or_no_railpack_in_the_image_say_so()
    {
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(125, StdErr: "docker: Error response from daemon: invalid mount config for type \"bind\": bind source path does not exist\n\nRun 'docker run --help' for more information\n"));
        docker.Runs.Enqueue(new Run(127, StdErr: "/bin/sh: 1: exec: railpack: not found\n"));

        var refused = await planner.PlanAsync(Request(), null, CancellationToken.None);
        var missing = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.Equal((RailpackFailure.NoSandbox,
            "Gitberth couldn't start the container Railpack plans the build in: docker: Error response from daemon: invalid mount config for type \"bind\": bind source path does not exist"),
            (refused.Failure!.Value, refused.Error!));
        Assert.Equal((RailpackFailure.NotInstalled, RailpackText.NotInstalled), (missing.Failure!.Value, missing.Error!));

        // Without its own image there's no helper at all.
        var (lost, lostDocker) = Planner();
        lostDocker.OwnImageExit = 1;
        Assert.Equal(RailpackText.NoSandbox("Gitberth couldn't find its own image."), (await lost.PlanAsync(Request(), null, CancellationToken.None)).Error);
        Assert.Empty(lostDocker.Helpers);
    }

    [Fact]
    public async Task Outside_a_container_the_cli_runs_as_it_is()
    {
        (string File, List<string> Args, ProcessOptions Options)? ran = null;
        var (planner, _) = Planner(container: false, inProcess: (file, args, options, _) =>
        {
            ran = (file, [.. args], options);
            File.WriteAllText(args[args.ToList().IndexOf("--plan-out") + 1], RailpackFixtures.Text("plan-shell.json"));
            File.WriteAllText(args[args.ToList().IndexOf("--info-out") + 1], RailpackFixtures.Text("info-shell.json"));
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        var plan = await planner.PlanAsync(Request(), null, CancellationToken.None);

        Assert.True(plan.Ok);
        Assert.Equal("railpack", ran!.Value.File);
        Assert.Equal(["prepare", Path.Combine(_repo, "web")], ran.Value.Args.Take(2));
        Assert.StartsWith(_repo + "-railpack-", ran.Value.Args[ran.Value.Args.IndexOf("--plan-out") + 1]);
        Assert.True(ran.Value.Options.CleanEnvironment);
        Assert.Equal(("1", RailpackPlanner.Placeholder, "on"),
            (ran.Value.Options.Environment!["NO_COLOR"], ran.Value.Options.Environment["API_KEY"], ran.Value.Options.Environment["my.flag"]));
        Assert.DoesNotContain("RAILPACK_PACKAGES_TOKEN", ran.Value.Options.Environment.Keys);

        // Not installed here: Railpack can't build it.
        var (none, _) = Planner(container: false, inProcess: (_, _, _, _) => throw new Win32Exception(2, "No such file or directory"));
        var missing = await none.PlanAsync(Request(), null, CancellationToken.None);
        Assert.Equal((RailpackFailure.NotInstalled, RailpackText.NotInstalled), (missing.Failure!.Value, missing.Error!));
    }

    [Fact]
    public async Task Mise_never_gets_CI_or_MISE_YES_which_would_let_it_install_a_plugin_paranoid_mode_refuses()
    {
        // Part of every Railpack bump: mise reads CI as "yes" (settings.ci sets yes), and yes skips paranoid mode's refusal
        // of a community plugin. Today Railpack gives mise an environment of its own; this keeps it so if it ever doesn't.
        var (planner, docker) = Planner();
        docker.Runs.Enqueue(new Run(0, "plan-shell.json", "info-shell.json"));
        Assert.True((await planner.PlanAsync(Request(), null, CancellationToken.None)).Ok);
        var (args, options) = docker.Calls.Single(c => c.Args is ["run", ..]);
        var given = args.Select((a, i) => (a, i)).Where(p => p.a == "-e").Select(p => args[p.i + 1].Split('=')[0]).ToList();
        Assert.DoesNotContain(given, n => n is "CI" or "MISE_YES");
        Assert.DoesNotContain(options!.Environment!.Keys, n => n is "CI" or "MISE_YES");

        // Nor outside a container.
        ProcessOptions? ran = null;
        var (local, _) = Planner(container: false, inProcess: (_, a, o, _) =>
        {
            ran = o;
            File.WriteAllText(a[a.ToList().IndexOf("--plan-out") + 1], RailpackFixtures.Text("plan-shell.json"));
            File.WriteAllText(a[a.ToList().IndexOf("--info-out") + 1], RailpackFixtures.Text("info-shell.json"));
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        Assert.True((await local.PlanAsync(Request(), null, CancellationToken.None)).Ok);
        Assert.DoesNotContain(ran!.Environment!.Keys, n => n is "CI" or "MISE_YES");
    }

    [Fact]
    public void A_secret_reaches_planning_as_a_placeholder_and_a_secret_railpack_setting_not_at_all()
    {
        var values = RailpackPlanner.Values(
        [
            new EnvValue("RAILPACK_START_CMD", "./server --api-key s3cr3t-api-key", IsSecret: true),
            new EnvValue("RAILPACK_BUILD_CMD", "make", IsSecret: false),
            new EnvValue("API_KEY", "s3cret-value-1234", IsSecret: true),
            // Set twice: the last one counts, and it's secret.
            new EnvValue("RAILPACK_PACKAGES", "node@22", IsSecret: false),
            new EnvValue("RAILPACK_PACKAGES", "x@1 --token abc123secret", IsSecret: true),
        ]);

        Assert.Equal([("RAILPACK_BUILD_CMD", "make"), ("API_KEY", RailpackPlanner.Placeholder)], values.Select(v => (v.Name, v.Value)));
        Assert.True(RailpackPlanner.LeftOut(new EnvValue("RAILPACK_START_CMD", "x", IsSecret: true)));
        Assert.False(RailpackPlanner.LeftOut(new EnvValue("RAILPACK_START_CMD", "x", IsSecret: false)));
        Assert.False(RailpackPlanner.LeftOut(new EnvValue("API_KEY", "x", IsSecret: true)));
    }

    [Fact]
    public void A_railpack_setting_that_names_a_secret_reaches_planning_as_it_is()
    {
        // The way the saves' refusal says to write it: the secret in a variable of its own, named in the setting.
        var values = RailpackPlanner.Values(
        [
            new EnvValue("RAILPACK_START_CMD", "./server --api-key $API_KEY", IsSecret: false),
            new EnvValue("RAILPACK_BUILD_CMD", "DATABASE_PASSWORD=$DB_PASSWORD npm run migrate", IsSecret: false),
            new EnvValue("API_KEY", "s3cret-value-1234", IsSecret: true),
            // Any other variable that names a secret that way is still hidden by Masking's own rule, as before.
            new EnvValue("SERVER_ARGS", "--token $TOKEN", IsSecret: false),
        ]);

        Assert.Equal(
            [
                ("RAILPACK_START_CMD", "./server --api-key $API_KEY"), ("RAILPACK_BUILD_CMD", "DATABASE_PASSWORD=$DB_PASSWORD npm run migrate"),
                ("API_KEY", RailpackPlanner.Placeholder), ("SERVER_ARGS", RailpackPlanner.Placeholder),
            ],
            values.Select(v => (v.Name, v.Value)));
    }

    [Fact]
    public async Task The_version_is_the_clis_own_read_once_and_none_without_it()
    {
        var runs = 0;
        var (planner, _) = Planner(inProcess: (file, args, _, _) =>
        {
            Interlocked.Increment(ref runs);
            Assert.Equal(("railpack", "--version"), (file, args.Single()));
            return Task.FromResult(new ProcessResult(0, "railpack version 0.43.0\n", ""));
        });

        Assert.Equal("0.43.0", await planner.VersionAsync(CancellationToken.None));
        Assert.Equal("0.43.0", await planner.VersionAsync(CancellationToken.None));
        Assert.Equal(1, runs);

        var (none, _) = Planner(inProcess: (_, _, _, _) => throw new Win32Exception(2, "No such file or directory"));
        Assert.Null(await none.VersionAsync(CancellationToken.None));
    }
}
