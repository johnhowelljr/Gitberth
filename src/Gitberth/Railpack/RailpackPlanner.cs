using System.ComponentModel;
using System.Text.RegularExpressions;
using Gitberth.Docker;

namespace Gitberth.Railpack;

/// <summary>
/// Plans a build with <c>railpack prepare</c>. When Gitberth runs in a container, prepare runs in a throwaway container of
/// Gitberth's own image: no Docker socket, none of /data, a read-only root and no capabilities, and Gitberth's own network
/// view (its /etc/hosts too). The repo is mounted read-only and only the output folder is written, so the files a repo
/// controls (railpack.json, mise.toml, .tool-versions) are read by Railpack there, never in Gitberth's own container. No
/// secret value reaches it: a secret is given as a placeholder (the plan records names only), and a RAILPACK_ value that's
/// secret isn't given at all, since Railpack would write it into the plan (<see cref="RailpackSettings"/>). Gitberth sets
/// neither CI nor MISE_YES for it: either turns on mise's "yes", which installs a plugin that mise's paranoid mode would
/// refuse, should a Railpack ever pass its environment on to mise (0.43.0 gives mise one of its own). Outside a container
/// (a developer's machine) the CLI runs as it is.
/// </summary>
public sealed partial class RailpackPlanner(DockerCli docker, SelfContainer self, GitberthOptions options, ILogger<RailpackPlanner> logger) : IRailpackPlanner
{
    /// <summary>
    /// Values left out of prepare and the build: every Railpack build step gets every value as a variable, and these would
    /// take the place of the step's own.
    /// </summary>
    public static readonly IReadOnlySet<string> NotPassed = new HashSet<string>(StringComparer.Ordinal)
    {
        "PATH", "HOME", "PWD", "HOSTNAME", "SHLVL", "TERM", "TMPDIR", "NO_COLOR",
    };

    /// <summary>What a secret value is given to prepare as: the plan only names the values, so nothing of a secret is needed.</summary>
    public const string Placeholder = "gitberth-secret";

    /// <summary>How the files prepare writes are called, in its output folder.</summary>
    public const string PlanFile = "railpack-plan.json";
    public const string InfoFile = "railpack-info.json";

    /// <summary>Stands in for running a CLI itself (outside a container, and for the version) in tests.</summary>
    internal Func<string, IReadOnlyList<string>, ProcessOptions, CancellationToken, Task<ProcessResult>>? InProcessRunner { get; init; }

    /// <summary>How long one plan may take, its retry included.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The pause before the one retry of a failure that may pass: Railpack's "try again" (exit 75), or the network.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    private Task<string?>? _version;

    public async Task<RailpackPlanResult> PlanAsync(RailpackPlanRequest request, Action<string>? onLine, CancellationToken ct)
    {
        // Next to the folder planned (a deploy's work folder, New app's analysis folder): under /data, so the helper can mount it.
        var outDir = $"{request.RepoDir.TrimEnd('/', '\\')}-railpack-{Guid.NewGuid().ToString("N")[..8]}";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        IReadOnlyList<string> lines = [];
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var run = await AttemptAsync(request, outDir, onLine, limit.Token);
                if (run.Refused is { } refused) return refused;
                lines = run.Lines;
                var (result, retry) = Read(run, outDir);
                if (retry && attempt == 1)
                {
                    logger.LogInformation("Railpack's plan failed in a way that may pass ({Why}): trying once more", result.Error);
                    await Task.Delay(RetryDelay, limit.Token);
                    continue;
                }
                return result;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(RailpackFailure.TimedOut, RailpackText.TimedOut, null, lines);
        }
        finally
        {
            Deploy.DeployPaths.TryDeleteDirectory(outDir);
        }
    }

    public Task<string?> VersionAsync(CancellationToken ct) => _version ??= ReadVersionAsync();

    // `railpack --version` ("railpack version 0.43.0"), run in Gitberth's own container: it reads nothing of a repo's.
    private async Task<string?> ReadVersionAsync()
    {
        try
        {
            var r = await RunCliAsync(options.RailpackPath, ["--version"], new ProcessOptions { MaxCapture = 4096 }, CancellationToken.None);
            return r.Ok && VersionNumber().Match(r.StdOut) is { Success: true } m ? m.Value : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>One run of prepare: what it printed and how it ended, or the result when it couldn't run at all.</summary>
    private sealed record Attempt(int ExitCode, string StdOut, string StdErr, IReadOnlyList<string> Lines, RailpackPlanResult? Refused = null);

    private async Task<Attempt> AttemptAsync(RailpackPlanRequest request, string outDir, Action<string>? onLine, CancellationToken ct)
    {
        Deploy.DeployPaths.TryDeleteDirectory(outDir);
        Directory.CreateDirectory(outDir);
        var lines = new List<string>();
        void Line(string line)
        {
            lock (lines) lines.Add(line);
            onLine?.Invoke(line);
        }
        var values = Values(request.Env);
        var environment = values.Where(v => v.ByName).ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var appDir = request.AppDir.Trim().Trim('/');

        if (!options.RunningInContainer)
        {
            SeedMise();
            environment.TryAdd("NO_COLOR", "1");
            foreach (var v in values.Where(v => !v.ByName)) environment[v.Name] = v.Value;
            var folder = appDir.Length == 0 ? request.RepoDir : Path.Combine(request.RepoDir, appDir.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var r = await RunCliAsync(options.RailpackPath,
                    ["prepare", folder, .. PrepareFlags(Path.Combine(outDir, PlanFile), Path.Combine(outDir, InfoFile), request, values)],
                    new ProcessOptions { CleanEnvironment = true, Environment = environment, OnLine = Line, MaxCapture = 1024 * 1024, WorkingDirectory = outDir }, ct);
                return new Attempt(r.ExitCode, r.StdOut, r.StdErr, Tidy(lines));
            }
            catch (Win32Exception)
            {
                return new Attempt(127, "", "", [], Failed(RailpackFailure.NotInstalled, RailpackText.NotInstalled, null, []));
            }
        }

        if (await self.OwnImageAsync(ct) is not { } image)
            return new Attempt(125, "", "", [], Failed(RailpackFailure.NoSandbox, RailpackText.NoSandbox("Gitberth couldn't find its own image."), null, []));
        if (await self.HostPathAsync(request.RepoDir, ct) is not { } repoHost || await self.HostPathAsync(outDir, ct) is not { } outHost)
            return new Attempt(125, "", "", [], Failed(RailpackFailure.NoSandbox,
                RailpackText.NoSandbox("Gitberth couldn't find where its data folder is on the Docker host."), null, []));
        var name = HostScanHelper.NewName(RailpackRelease.HelperKind);
        var args = HelperArgs(name, image, options.OwnContainer!, repoHost, outHost, options.DataDir, appDir, request, values,
            ProxyNames().Where(p => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p))));
        try
        {
            var r = await docker.RunAsync(args, new ProcessOptions { CleanEnvironment = true, Environment = environment, OnLine = Line, MaxCapture = 1024 * 1024 }, ct);
            return new Attempt(r.ExitCode, r.StdOut, r.StdErr, Tidy(lines));
        }
        catch (OperationCanceledException)
        {
            // Killing the docker CLI leaves the container running: it goes too.
            await docker.RemoveAsync(name, CancellationToken.None);
            throw;
        }
    }

    // What one run left: the plan and its info, or why there's none, and whether that's worth one more try.
    private static (RailpackPlanResult Result, bool Retry) Read(Attempt run, string outDir)
    {
        var all = run.StdOut + "\n" + run.StdErr;
        switch (run.ExitCode)
        {
            case 0:
            {
                var planJson = ReadFile(outDir, PlanFile);
                if (RailpackInfoParser.ParseObject(ReadFile(outDir, InfoFile)) is not { } info || RailpackPlanFile.Parse(planJson) is not { } plan)
                    return (Failed(RailpackFailure.Other, RailpackText.CantBuild("it ended without writing its plan."), null, run.Lines), false);
                return (new RailpackPlanResult(true, null, null, RailpackInfoParser.Parse(info, plan), planJson, run.Lines), false);
            }
            case 1:
            {
                var info = RailpackInfoParser.ParseObject(ReadFile(outDir, InfoFile)) is { } found ? RailpackInfoParser.Parse(found, null) : null;
                var (failure, why) = RailpackInfoParser.Classify(info?.Logs ?? [], all);
                var error = failure switch
                {
                    RailpackFailure.NothingFound => RailpackText.CantBuild(RailpackText.FoundNothing),
                    RailpackFailure.NoStartCommand => RailpackText.NoStartCommand(info?.Provider),
                    RailpackFailure.BadConfig => RailpackText.BadConfig(why),
                    RailpackFailure.Offline => RailpackText.Offline(why),
                    _ => RailpackText.CantBuild(why),
                };
                return (Failed(failure, error, info, run.Lines), failure == RailpackFailure.Offline);
            }
            case 75:
                // Railpack's "try again": a download that failed (mise's, which the image's copy should spare it). No info file.
                return (Failed(RailpackFailure.Offline, RailpackText.Offline(RailpackInfoParser.NetworkWords(run.StdErr.Trim().Length > 0 ? run.StdErr : run.StdOut)),
                    null, run.Lines), true);
            case 127:
                return (Failed(RailpackFailure.NotInstalled, RailpackText.NotInstalled, null, run.Lines), false);
            case 125 or -1:
                // Docker refused the helper (or there's no docker CLI): its words, not Railpack's.
                return (Failed(RailpackFailure.NoSandbox, RailpackText.NoSandbox(FirstLine(run.StdErr) ?? FirstLine(run.StdOut) ?? $"docker run ended with {run.ExitCode}."),
                    null, run.Lines), false);
            default:
                return (Failed(RailpackFailure.Other, RailpackText.CantBuild(LastLine(all) ?? $"prepare ended with exit code {run.ExitCode}."), null, run.Lines), false);
        }
    }

    /// <summary>
    /// The build's values as prepare gets them: plain ones as they are, a secret (marked so, or looking it) as
    /// <see cref="Placeholder"/>, none of <see cref="NotPassed"/> and no secret RAILPACK_ one (<see cref="LeftOut"/>), the
    /// last of a name that's set twice. A RAILPACK_ one that's left goes as it is, since it configures the plan: one that
    /// names a secret (<c>--api-key $API_KEY</c>) holds none, though Masking's own rule would hide the word after the option.
    /// One reaches the helper by name, its value in the docker CLI's environment, when it can go there; else (a plain value,
    /// or a placeholder) on the command line. So planning never sees a secret value, whoever asks for it (a deploy, create_app).
    /// </summary>
    internal static List<(string Name, string Value, bool ByName)> Values(IReadOnlyList<EnvValue> env)
    {
        var values = new List<(string Name, string Value, bool ByName)>();
        foreach (var e in env)
        {
            var name = e.Key.Trim();
            if (name.Length == 0 || NotPassed.Contains(name)) continue;
            values.RemoveAll(v => v.Name == name);
            if (LeftOut(e)) continue;
            var secret = !Detection.EnvRules.IsRailpackKey(name) && (e.IsSecret || Security.Masking.IsSecret(name, e.Value));
            values.Add((name, secret ? Placeholder : e.Value, EnvPassing.CanPassViaEnvironment(name)));
        }
        return values;
    }

    /// <summary>
    /// A value Railpack never gets, neither to plan nor to build: a RAILPACK_ one that's secret, marked so or holding one (a
    /// GitLab masked variable, a row saved before the saves refused one). Railpack would write it into its plan and the image.
    /// </summary>
    public static bool LeftOut(EnvValue e) => RailpackSettings.IsSecret(e.Key, e.Value, e.IsSecret);

    /// <summary>The helper's docker run, as design 2.2 has it.</summary>
    internal static List<string> HelperArgs(string name, string image, string ownContainer, string repoHost, string outHost, string dataDir, string appDir,
        RailpackPlanRequest request, IReadOnlyList<(string Name, string Value, bool ByName)> values, IEnumerable<string> proxies)
    {
        var args = new List<string>
        {
            "run", "--rm", "--name", name, "--label", $"{HostScanHelper.Label}={RailpackRelease.HelperKind}",
            "--network", $"container:{ownContainer}",
            "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "1g", "--pids-limit", "512",
            "--tmpfs", "/tmp:rw,exec,nosuid,size=512m", "--tmpfs", dataDir,
            "-v", $"{repoHost}:/workspace:ro", "-v", $"{outHost}:/out",
            // No CI and no MISE_YES (see the class's summary). Railpack gives mise an environment of its own anyway.
            "-w", "/tmp", "-e", "HOME=/tmp", "-e", "NO_COLOR=1",
        };
        foreach (var proxy in proxies) args.AddRange(["-e", proxy]);
        foreach (var v in values) args.AddRange(["-e", v.ByName ? v.Name : $"{v.Name}={v.Value}"]);
        args.AddRange(["--entrypoint", "/bin/sh", image, "-c", SeedScript, "railpack", "prepare", appDir.Length == 0 ? "/workspace" : $"/workspace/{appDir}"]);
        args.AddRange(PrepareFlags($"/out/{PlanFile}", $"/out/{InfoFile}", request, values));
        return args;
    }

    /// <summary>
    /// What the helper runs: mise linked where Railpack looks for it (its install folder, mise-&lt;version&gt;), so prepare never
    /// downloads it, then prepare with the helper's arguments.
    /// </summary>
    internal const string SeedScript =
        "mkdir -p /tmp/railpack/mise && ln -sf " + RailpackRelease.MisePath + " /tmp/railpack/mise/mise-" + RailpackRelease.MiseVersion + " && exec railpack \"$@\"";

    // prepare's flags after its folder: the files it writes, a start command it must find, the versions to keep, the values.
    private static List<string> PrepareFlags(string planOut, string infoOut, RailpackPlanRequest request, IReadOnlyList<(string Name, string Value, bool ByName)> values)
    {
        var flags = new List<string> { "--plan-out", planOut, "--info-out", infoOut };
        // With a Command of the app's own, that's what starts it (it replaces the image's command).
        if (!request.HasStartCommand) flags.Add("--error-missing-start");
        foreach (var previous in request.Previous.Where(p => p.Contains('@'))) flags.AddRange(["--previous", previous]);
        foreach (var v in values) flags.AddRange(["--env", v.Name]);
        return flags;
    }

    private static IEnumerable<string> ProxyNames() => ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"];

    // Outside a container: mise where Railpack looks for it, when this machine has Gitberth's copy (as the image does).
    private void SeedMise()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(RailpackRelease.MisePath)) return;
        try
        {
            var link = $"/tmp/railpack/mise/mise-{RailpackRelease.MiseVersion}";
            Directory.CreateDirectory("/tmp/railpack/mise");
            if (!File.Exists(link)) File.CreateSymbolicLink(link, RailpackRelease.MisePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "mise couldn't be linked where Railpack looks for it");
        }
    }

    private Task<ProcessResult> RunCliAsync(string file, IReadOnlyList<string> args, ProcessOptions processOptions, CancellationToken ct) =>
        InProcessRunner is { } runner ? runner(file, args, processOptions, ct) : ProcessRunner.RunAsync(file, args, processOptions, ct);

    private static RailpackPlanResult Failed(RailpackFailure failure, string error, RailpackInfo? info, IReadOnlyList<string> lines) =>
        new(false, failure, error, info, null, lines);

    private static string? ReadFile(string dir, string name)
    {
        try
        {
            var path = Path.Combine(dir, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // prepare's output as it's kept: each line without its trailing spaces (the box it draws pads them), blank ends dropped.
    private static List<string> Tidy(List<string> lines)
    {
        List<string> copy;
        lock (lines) copy = lines.Select(l => l.TrimEnd()).ToList();
        while (copy.Count > 0 && copy[0].Length == 0) copy.RemoveAt(0);
        while (copy.Count > 0 && copy[^1].Length == 0) copy.RemoveAt(copy.Count - 1);
        return copy;
    }

    private static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string? LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
