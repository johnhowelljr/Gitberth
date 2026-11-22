using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Railpack;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Deploy;

/// <summary>
/// Railpack, the second way a Git app's image is built: for a project Gitberth's analyzer doesn't know (under Detect
/// automatically), or always for an app whose Build is Railpack. Railpack plans it in a throwaway container
/// (<see cref="IRailpackPlanner"/>), gitberth-buildkit builds the plan (<see cref="RailpackBuildKit"/>), and the engine's
/// builder gives the image Gitberth's labels and PORT (Railpack's frontend ignores --label). Everything before the build
/// (variables, clone, filters) and after it (swap, checks, bookkeeping) is the same as for a Dockerfile.
/// </summary>
public sealed partial class DeployPipeline
{
    private static readonly NoRailpack NoPlanner = new();
    private RailpackBuildKit? _ownBuildKit;

    private IRailpackPlanner Planner => railpack ?? NoPlanner;

    // Program.cs gives the one gitberth-buildkit; a pipeline made in a test has one of its own over its docker.
    private RailpackBuildKit BuildKit => buildKit ?? (_ownBuildKit ??= new RailpackBuildKit(docker, options, NullLogger<RailpackBuildKit>.Instance));

    /// <summary>How long the finishing build (Gitberth's labels and PORT on Railpack's image) may take.</summary>
    internal TimeSpan FinishTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The image Railpack builds, before Gitberth's labels are added: in the app's own repository, gone once finished.</summary>
    internal static string RailpackTag(string tag) => tag + "-railpack";

    /// <param name="buildEnv">The build's values, Vercel's included.</param>
    /// <param name="railpackValues">The app's RAILPACK_ values, which reach the build whatever their mark.</param>
    /// <param name="unrecognized">The analyzer's result when it found nothing it knows (a fallback); null when the app's Build is Railpack.</param>
    private async Task<Prepared> BuildWithRailpackAsync(GitberthDb db, AppDefinition app, Deployment dep, CommitInfo commit, string srcDir, string appDir,
        string folder, AppTraits traits, IReadOnlyList<EnvValue> buildEnv, IReadOnlyList<EnvValue> railpackValues, string? cronLabel,
        DetectionResult? unrecognized, string buildDir, DeployLog log, CancellationToken ct)
    {
        var railpackDir = Path.Combine(Path.GetDirectoryName(buildDir)!, "railpack");
        Directory.CreateDirectory(railpackDir);
        var values = RailpackValues(buildEnv, railpackValues);
        var leftOut = values.Select(v => v.Key).Where(RailpackPlanner.NotPassed.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (leftOut.Count > 0) log.Line(RailpackText.NotPassed(leftOut));
        values = values.Where(v => !RailpackPlanner.NotPassed.Contains(v.Key)).ToList();
        // A RAILPACK_ value that's secret anyway (a GitLab masked variable, a row saved before the saves refused one) reaches
        // neither the plan nor the build: Railpack would write it into both (RailpackSettings).
        var secretSettings = values.Where(RailpackPlanner.LeftOut).Select(v => v.Key).Distinct(StringComparer.Ordinal).ToList();
        if (secretSettings.Count > 0) log.Line(RailpackText.SecretSettingsLeftOut(secretSettings));
        values = values.Where(v => !RailpackPlanner.LeftOut(v)).ToList();

        var previous = dep.NoCache ? [] : await PreviousVersionsAsync(app, log);
        if (previous.Count > 0) log.Line(RailpackText.Keeping(previous));
        log.Step(RailpackText.Planning);
        // With a Command of the app's own, that's what starts it: Railpack doesn't have to find one.
        var command = GitExtras.Parse(app.RuntimeExtras)?.Command is { Count: > 0 };
        var plan = await Planner.PlanAsync(new RailpackPlanRequest(srcDir, appDir, values, command, previous), line => log.Line(RailpackText.Output(line)), ct);
        if (!plan.Ok)
        {
            if (unrecognized is not null)
            {
                (app.DetectedKind, app.DetectedSummary) = (unrecognized.Kind.ToString(), unrecognized.Title);
                if (plan.Failure == RailpackFailure.NothingFound) throw new DeployException(RailpackText.NeitherCanBuild);
            }
            // Railpack's warnings for a project it couldn't plan ("script start.sh not found") would read as advice: not logged.
            throw new DeployException(plan.Error ?? RailpackText.CantBuild(""));
        }
        var info = plan.Info!;
        (app.DetectedKind, app.DetectedSummary) = (nameof(ProjectKind.Railpack), RailpackProviders.Summary(info));
        log.Step(RailpackText.Found(info));
        foreach (var warning in info.Logs.Where(l => l.Level is "warn" or "deprecation")) log.Line(RailpackText.Warning(warning.Message));

        // Gitberth's edits: .git left out, secret subsets made "*", and without the cache every command step "*".
        var edited = RailpackPlanFile.Parse(plan.PlanJson) ?? throw new DeployException(RailpackText.CantBuild("its plan isn't JSON."));
        RailpackPlanFile.Adjust(edited, dep.NoCache, RailpackPlanner.NotPassed.ToList());
        var planPath = Path.Combine(railpackDir, RailpackPlanner.PlanFile);
        var planText = RailpackPlanFile.Write(edited);
        await File.WriteAllTextAsync(planPath, planText, CancellationToken.None);
        await File.WriteAllTextAsync(DeployPaths.RailpackPlanCopy(options, dep.Id), planText, CancellationToken.None);

        // Every value the plan names reaches every build step as a BuildKit secret, from the docker CLI's environment under
        // a neutral name: never on its command line, never in the image. One the repo's railpack.json asks for and the app
        // doesn't set gets an empty file, or the build would stop. Its name is the repo's, so it has to be a variable's
        // before it goes into a --secret flag, where a comma would start an option of its own. (The app's own names are
        // checked when they're saved, and hold no comma or =.)
        var byName = values.ToDictionary(v => v.Key, StringComparer.Ordinal);
        if (RailpackPlanFile.Secrets(edited).FirstOrDefault(n => !byName.ContainsKey(n) && !RailpackSettings.IsVariableName(n)) is { } badName)
            throw new DeployException(RailpackText.BadSecretName(badName));
        var secrets = new List<string>();
        var processEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["BUILDX_GIT_INFO"] = "0" };
        var given = new List<EnvValue>();
        string? empty = null;
        foreach (var name in RailpackPlanFile.Secrets(edited))
        {
            if (byName.TryGetValue(name, out var value))
            {
                var env = $"GITBERTH_SECRET_{given.Count}";
                secrets.AddRange(["--secret", $"id={name},env={env}"]);
                processEnv[env] = value.Value;
                given.Add(value);
                continue;
            }
            log.Line(RailpackText.MissingSecret(name));
            empty ??= EmptyFile(railpackDir);
            secrets.AddRange(["--secret", $"id={name},src={empty}"]);
        }
        // BuildKit keeps secrets out of its cache: the steps that see them are keyed to their values by this, which gives
        // nothing away. Without the cache it's salted with the deploy, so every step runs again.
        var hash = BuildSecrets.Stamp(await BuildSecrets.KeyAsync(dbFactory, protector, ct), given) + (dep.NoCache ? $"-{dep.Id}" : "");

        var tag = $"{app.ImageRepository}:{commit.Sha[..7]}";
        var temporary = RailpackTag(tag);
        dep.ImageTag = tag;
        List<string> build =
        [
            "buildx", "build", "--builder", RailpackRelease.BuildKitContainer, "--load", "--progress=plain", "--provenance=false", "--sbom=false",
            "-f", planPath, "-t", temporary,
            "--build-arg", $"BUILDKIT_SYNTAX={RailpackRelease.Frontend}",
            "--build-arg", $"cache-key={RailpackRelease.CacheKey(app.Id, app.Slug)}",
            "--build-arg", $"secrets-hash={hash}",
            .. secrets,
            // The app's folder is both what Railpack planned and the build's context: files outside it aren't there.
            folder,
        ];
        var web = PortRules.WebOf(app.Ports)?.ContainerPort;
        var finish = FinishArgs(app, commit, traits, cronLabel, info, tag, web, Path.Combine(railpackDir, "finish"));

        if (_buildGate.CurrentCount == 0) log.Step(WaitLine);
        await _buildGate.WaitAsync(ct);
        var ran = false;
        try
        {
            await SetStatusAsync(db, dep, DeployStatus.Building);
            await BuildKit.EnsureAsync(log, ct);
            // Its build steps share gitberth-buildkit's place on Gitberth's network, with Docker's DNS there.
            var hosts = await docker.NetworkHostsAsync(CancellationToken.None);
            if (hosts.Count > 0) log.Line(RailpackText.Reachable(hosts.Select(h => h.Host)));
            log.Step(dep.NoCache ? RailpackText.BuildingWithoutCache(tag) : RailpackText.Building(tag));
            ran = true;
            var started = DateTimeOffset.UtcNow;
            var result = await docker.RunAsync(build, new ProcessOptions { Environment = processEnv, OnLine = log.Line, MaxCapture = 16 * 1024 }, ct);
            if (!result.Ok && BuilderLost(result))
            {
                // buildx's builder went with a restart of Gitberth, or gitberth-buildkit stopped meanwhile: ready again, built once more.
                log.Line($"{RailpackRelease.BuildKitContainer} wasn't there for the build, so it's made ready again.");
                BuildKit.ForgetBuilder();
                await BuildKit.EnsureAsync(log, ct);
                started = DateTimeOffset.UtcNow;
                result = await docker.RunAsync(build, new ProcessOptions { Environment = processEnv, OnLine = log.Line, MaxCapture = 16 * 1024 }, ct);
            }
            if (!result.Ok)
            {
                // The frontend's own words go to buildkitd's log, not to the build's output.
                foreach (var line in await BuildKit.LogTailAsync(started, CancellationToken.None)) log.Line(RailpackText.BuildKitLog(line));
                throw new DeployException(result.ExitCode == -1 ? result.ErrorText : RailpackText.BuildFailed);
            }

            log.Step(RailpackText.Finishing(web));
            Directory.CreateDirectory(Path.Combine(railpackDir, "finish"));
            await File.WriteAllTextAsync(Path.Combine(railpackDir, "finish", "Dockerfile"), FinishDockerfile(temporary, web), CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(FinishTimeout);
            ProcessResult finished;
            try
            {
                finished = await docker.RunAsync(finish, new ProcessOptions { OnLine = log.Line, MaxCapture = 16 * 1024 }, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DeployException(RailpackText.FinishFailed($"it took longer than {FinishTimeout.TotalMinutes:0} minutes."));
            }
            if (!finished.Ok) throw new DeployException(RailpackText.FinishFailed(RailpackBuildKit.Words(finished)));
        }
        finally
        {
            // The final image keeps the layers; the tag Railpack built goes, however it went.
            if (ran) await docker.RemoveImageAsync(temporary, CancellationToken.None);
            BuildKit.BuildEnded();
            _buildGate.Release();
        }
        return new Prepared(tag, null, null, traits, Railpack: true);
    }

    /// <summary>
    /// The values a Railpack build gets: the build-time ones (Vercel's included), and every RAILPACK_ one, which configures
    /// the build whatever its mark; the last of a name set twice.
    /// </summary>
    internal static List<EnvValue> RailpackValues(IReadOnlyList<EnvValue> buildEnv, IReadOnlyList<EnvValue> railpackValues)
    {
        var values = new List<EnvValue>();
        foreach (var e in buildEnv.Concat(railpackValues.Where(r => EnvRules.IsRailpackKey(r.Key) && buildEnv.All(b => b.Key != r.Key))))
        {
            values.RemoveAll(v => v.Key == e.Key);
            values.Add(e);
        }
        return values;
    }

    /// <summary>The finishing build's docker build: Gitberth's labels on Railpack's image, the builder's own among them.</summary>
    internal static List<string> FinishArgs(AppDefinition app, CommitInfo commit, AppTraits traits, string? cronLabel, RailpackInfo info, string tag, int? web,
        string finishDir)
    {
        var args = new List<string>
        {
            "build", "--progress=plain", "--provenance=false", "--sbom=false", "-f", Path.Combine(finishDir, "Dockerfile"), "-t", tag,
            "--label", $"gitberth.app={app.Slug}", "--label", $"gitberth.commit={commit.Sha}",
        };
        if (web is int port) args.AddRange(["--label", $"{ImageLabels.Port}={port}"]);
        args.AddRange(traits.LabelArgs());
        if (cronLabel is not null) args.AddRange(["--label", $"{ImageLabels.Crons}={cronLabel}"]);
        args.AddRange(["--label", $"{ImageLabels.Builder}=railpack", "--label", $"{ImageLabels.RailpackVersion}={RailpackRelease.Version}"]);
        var packages = string.Join(' ', info.Packages.Where(p => p.Name.Length > 0 && p.Resolved.Length > 0).Select(p => $"{p.Name}@{p.Resolved}"));
        if (packages.Length > 0) args.AddRange(["--label", $"{ImageLabels.RailpackPackages}={packages}"]);
        args.Add(finishDir);
        return args;
    }

    // PORT for a Railpack image's container (RailpackPort), said in the log when it's added.
    private static List<EnvValue> WithRailpackPort(List<EnvValue> env, IReadOnlyList<PortMapping> ports, DeployLog log)
    {
        if (RailpackPort.Added(env, ports) is int port) log.Line(RailpackText.GetsPort(port));
        return RailpackPort.With(env, ports);
    }

    /// <summary>FROM Railpack's image, with PORT and EXPOSE for the app's web port when it has one: metadata only, no layer.</summary>
    internal static string FinishDockerfile(string temporary, int? web) =>
        web is int port ? $"FROM {temporary}\nENV PORT={port}\nEXPOSE {port}\n" : $"FROM {temporary}\n";

    // The tool versions the running image was built with (its label), which a plain deploy keeps (--previous).
    private async Task<IReadOnlyList<string>> PreviousVersionsAsync(AppDefinition app, DeployLog log)
    {
        if (app.CurrentImage is not { } current) return [];
        if (!await docker.ImageExistsAsync(current, CancellationToken.None))
        {
            if (app.DetectedKind == nameof(ProjectKind.Railpack)) log.Line(RailpackText.VersionsLookedUpAgain);
            return [];
        }
        var label = await docker.ImageLabelAsync(current, ImageLabels.RailpackPackages, CancellationToken.None);
        return label?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Contains('@')).ToList() ?? [];
    }

    // buildx's builder is gone ("no builder … found"), or gitberth-buildkit stopped (the build waits, then "context deadline exceeded").
    private static bool BuilderLost(ProcessResult result)
    {
        var output = result.StdOut + result.StdErr;
        return output.Contains($"no builder \"{RailpackRelease.BuildKitContainer}\" found", StringComparison.Ordinal)
            || output.Contains("context deadline exceeded", StringComparison.Ordinal);
    }

    private static string EmptyFile(string dir)
    {
        var path = Path.Combine(dir, "empty");
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>Whether the app's folder has a Dockerfile of its own (Dockerfile, Containerfile, Dockerfile.prod…).</summary>
    private static bool HasDockerfile(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(f => ProjectAnalyzer.IsDockerfileName(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The web port a Railpack image's new container opens (plan D4). Railpack's start commands read PORT, but an app can
    /// listen where it likes. Only once the usual wait has run out, the ports it listens on are read from inside it: exactly
    /// one other, which none of the app's other ports has, moves the web port there, and the swap runs once more (at most
    /// once a deploy); otherwise the swap fails, naming what it found. Null when it opened its port, or there's no web port.
    /// </summary>
    /// <param name="listen">The ports the container publishes (a temporary container next to the old one has its web port only).</param>
    /// <param name="ports">The app's ports, whose other container ports the web port never moves onto.</param>
    private async Task<(string? Failure, int? MoveTo)> RailpackListensAsync(string container, IReadOnlyList<PortMapping> listen, IReadOnlyList<PortMapping> ports,
        bool mayMove, DeployLog log)
    {
        if (await ListeningAsync(container, listen, log) is not { } timedOut) return (null, null);
        if (PortRules.WebOf(listen) is not { } web || await docker.InspectStateAsync(container) is not { Running: true }) return (timedOut, null);
        var read = await docker.ExecAsync(container, ["cat", "/proc/net/tcp", "/proc/net/tcp6"], maxCapture: 256 * 1024);
        // A missing tcp6 (no IPv6) makes cat fail after it printed tcp.
        if (read.StdOut.Trim().Length == 0) return (timedOut, null);
        var sockets = ProcNet.ParseSockets(read.StdOut, "tcp");
        // It does listen on the web port, for others to reach: what kept Gitberth from connecting isn't the app's port.
        if (sockets.Any(s => !s.Loopback && s.Port == web.ContainerPort)) return (timedOut, null);
        var own = PortRules.WebOf(ports);
        var mapped = ports.Where(p => p != own).Select(p => p.ContainerPort).ToHashSet();
        var others = sockets.Where(s => !s.Loopback && !mapped.Contains(s.Port)).Select(s => s.Port).Distinct().Order().ToList();
        if (others.Count == 1 && mayMove) return (timedOut, others[0]);
        // Docker's own DNS listens on 127.0.0.11 in every container on a network of its own: never the app's.
        var inside = sockets.Where(s => s.Loopback && s.Address != "127.0.0.11").Select(s => s.Port).Distinct().Order().ToList();
        return ($"{timedOut} {RailpackText.ListensElsewhere(others, inside, web.ContainerPort)}", null);
    }
}
