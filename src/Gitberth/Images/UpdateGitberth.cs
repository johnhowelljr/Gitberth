using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Notifications;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Images;

/// <summary>One step of Update now, as the card shows it while it runs.</summary>
public sealed record GitberthUpdateStep(string Title, Backup.RestoreStepState State, string? Note = null);

/// <summary>What Settings › Updates › Gitberth shows.</summary>
/// <param name="CantUpdate">Why Gitberth can't replace itself here (a source build, not in a container…); null when it can.</param>
/// <param name="Busy">Why it can't now (a deploy, a backup or a restore runs); null when nothing holds it.</param>
/// <param name="Steps">Update now's steps while it runs (and after it failed before the updater took over).</param>
public sealed record GitberthUpdateStatus(string Current, string Commit, GitberthUpdateState State, bool Newer, string? CantUpdate, string? Busy,
    IReadOnlyList<GitberthUpdateStep> Steps, bool Updating);

/// <summary>
/// Gitberth's own updates (Settings › Updates › Gitberth): it asks GitHub for its newest release every 6 hours (and on Check
/// now), tells about a new one once (UpdateAvailable), and on Update now (or by itself between 03:00 and 05:00 when set so)
/// pulls the new version's image, checks its version, copies its data into gitberth-data-before-&lt;version&gt; (SQLite
/// snapshots, consistent while it runs; the newest 2 copies kept), and starts the updater, a container of the new version
/// that swaps the containers and puts the old one back when the new one doesn't answer (<see cref="GitberthUpdater"/>).
/// When Gitberth starts it reads how the last one ended. Never while a deploy, a backup or a restore runs.
/// </summary>
public sealed class UpdateGitberth(SettingsService settings, IHttpClientFactory http, DockerApi docker, GitberthOptions options, Notifier notifier,
    IDbContextFactory<GitberthDb> dbFactory, Backup.BackupService backups, Backup.AutoBackups autoBackups, Backup.RestoreGate gate, Backup.IRestoreService restore,
    ILogger<UpdateGitberth> logger, AuditLog? audit = null, TimeProvider? time = null) : BackgroundService
{
    public const string StateKey = "gitberth.update";

    /// <summary>The first check after Gitberth starts, and how often the window and the checks are looked at.</summary>
    internal TimeSpan FirstLook { get; set; } = TimeSpan.FromMinutes(3);
    internal TimeSpan Tick { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>GitHub's newest release; tests give their own (never GitHub's real API in tests).</summary>
    internal Func<CancellationToken, Task<GitberthRelease?>>? Latest { get; init; }

    /// <summary>The version this Gitberth runs (tests give their own).</summary>
    internal string Version { get; init; } = AppVersion.Number;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _updating = new(1, 1);
    private GitberthUpdateState? _state;
    private List<GitberthUpdateStep> _steps = [];
    private volatile bool _running;

    /// <summary>Raised when a check, a step or the policy changed, for open pages.</summary>
    public event Action? Changed;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private string UpdateDir => Path.Combine(options.DataDir, "update");

    public async Task<GitberthUpdateStatus> StatusAsync(CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        var cant = await CantUpdateAsync(ct);
        return new GitberthUpdateStatus(Version, AppVersion.Commit, state, UpdateGitberthRules.Newer(state.Latest?.Version, Version), cant,
            await BusyAsync(ct), _steps, _running);
    }

    private async Task<GitberthUpdateState> StateAsync(CancellationToken ct) =>
        _state ??= GitberthUpdateState.Parse(await settings.GetAsync(StateKey, ct));

    private async Task SaveStateAsync(GitberthUpdateState state, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await settings.SetAsync(StateKey, state.ToJson(), ct);
            _state = state;
        }
        finally
        {
            _write.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>When a new version comes out: tell (the default), update by itself in the window, or do nothing.</summary>
    public async Task SetPolicyAsync(GitberthUpdatePolicy policy, CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        if (state.Policy == policy) return;
        var was = state.Policy;
        state.Policy = policy;
        await SaveStateAsync(state, ct);
        audit?.Record(new AuditEntry("settings.updates.gitberth.changed", $"When a new version of Gitberth comes out: {Words(policy)}.")
        {
            Target = "Gitberth", Changes = [new("When a new version comes out", Words(was), Words(policy))],
        });
    }

    internal static string Words(GitberthUpdatePolicy p) => p switch
    {
        GitberthUpdatePolicy.Auto => "update by itself",
        GitberthUpdatePolicy.Nothing => "do nothing",
        _ => "tell me",
    };

    // ---- the check ----

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var actor = AuditScope.Begin(AuditActor.System(AuditVia.Updates));
        try
        {
            await AwaitResultAsync(stoppingToken);
            await Task.Delay(FirstLook, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await LookAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Looking for a new version of Gitberth failed");
                }
                await Task.Delay(Tick, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One look: a check when one is due, then an update by itself when it's set so, the window is open and nothing holds it.</summary>
    internal async Task LookAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct);
        if (state.CheckedAt is not { } at || Now - at >= UpdateGitberthRules.CheckEvery) state = await CheckAsync(ct);
        if (state.Policy != GitberthUpdatePolicy.Auto || !UpdateGitberthRules.Newer(state.Latest?.Version, Version)) return;
        if (!Images.AutoWindow.Default.Contains(TimeOnly.FromDateTime(_time.GetLocalNow().DateTime))) return;
        if (await CantUpdateAsync(ct) is not null || await BusyAsync(ct) is not null) return;
        using var actor = AuditScope.Begin(AuditActor.System(AuditVia.Updates));
        if (await UpdateNowAsync(ct) is { } why) logger.LogInformation("Gitberth doesn't update itself now: {Why}", why);
    }

    /// <summary>Check now: GitHub's newest release, kept; a newer one is told once (unless Do nothing is set).</summary>
    public async Task<GitberthUpdateState> CheckAsync(CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        try
        {
            state.Latest = await (Latest ?? LatestReleaseAsync)(ct);
            state.CheckProblem = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            state.CheckProblem = ex is TaskCanceledException ? "GitHub didn't answer in time." : ex.Message.TrimEnd('.') + ".";
            logger.LogInformation("Checking for a new version of Gitberth failed: {Why}", state.CheckProblem);
        }
        state.CheckedAt = Now;
        if (state.Latest is { } latest && UpdateGitberthRules.Newer(latest.Version, Version) && state.ToldVersion != latest.Version
            && state.Policy != GitberthUpdatePolicy.Nothing)
        {
            state.ToldVersion = latest.Version;
            notifier.Publish(new Notice(NotifyEvent.UpdateAvailable, NoticeLevel.Info, $"Gitberth {latest.Version} is out",
                $"This Gitberth runs {Version}. " + (state.Policy == GitberthUpdatePolicy.Auto
                    ? "It updates itself between 03:00 and 05:00 (Gitberth's time zone), once nothing else runs."
                    : "Settings › Updates has its release notes and Update now."),
                await notifier.LinkAsync("settings#gitberth-update", ct), "", null, Now));
        }
        await SaveStateAsync(state, ct);
        return state;
    }

    // GitHub's latest release (never a pre-release or a draft), anonymously: null when there's none.
    private async Task<GitberthRelease?> LatestReleaseAsync(CancellationToken ct)
    {
        using var client = http.CreateClient("github");
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateGitberthRules.LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd("Gitberth/" + Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new JsonException("GitHub's answer wasn't a release.");
        return ReleaseOf(json);
    }

    /// <summary>A release as GitHub's API describes it; null for a draft, a pre-release, or a tag that isn't a version.</summary>
    internal static GitberthRelease? ReleaseOf(JsonObject json)
    {
        if (Bool(json, "draft") || Bool(json, "prerelease")) return null;
        var tag = Str(json, "tag_name");
        if (UpdateGitberthRules.VersionOf(tag) is not { } version) return null;
        DateTime? published = DateTime.TryParse(Str(json, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var at) ? at : null;
        var notes = Str(json, "body");
        return new GitberthRelease(version, tag, NullIfEmpty(Str(json, "name")), notes.Length > 20_000 ? notes[..20_000] : NullIfEmpty(notes),
            NullIfEmpty(Str(json, "html_url")), published);
    }

    private static bool Bool(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ---- whether it can ----

    /// <summary>Why Gitberth can't replace itself here; null when it can.</summary>
    internal async Task<string?> CantUpdateAsync(CancellationToken ct)
    {
        if (!options.RunningInContainer || options.OwnContainer is not { } self)
            return UpdateGitberthRules.CantUpdate(false, null, PublishedImage);
        try
        {
            var me = await docker.InspectContainerAsync(self, ct);
            return UpdateGitberthRules.CantUpdate(me is not null, Str(me?["Config"], "Image"), PublishedImage);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            return "Docker doesn't answer, so Gitberth can't replace itself now.";
        }
    }

    /// <summary>Gitberth's published image, or the one GITBERTH_UPDATE_IMAGE names (the end-to-end rig's).</summary>
    internal static string PublishedImage =>
        Environment.GetEnvironmentVariable(UpdateGitberthRules.ImageVariable) is { Length: > 0 } image ? image.Trim() : UpdateGitberthRules.PublishedImage;

    /// <summary>What holds an update off now: a deploy, a backup or a restore; null when nothing does.</summary>
    internal async Task<string?> BusyAsync(CancellationToken ct)
    {
        if (gate.Holding || restore.CantRestore() is not null) return "A restore is running: update once it has ended.";
        if (backups.Saving || autoBackups.Running) return "A backup is being made: update once it has ended.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AnyAsync(d => d.Status == DeployStatus.Queued || d.Status == DeployStatus.Cloning || d.Status == DeployStatus.Building
            || d.Status == DeployStatus.Starting, ct)
            ? "A deploy is running: update once it has ended."
            : null;
    }

    // ---- Update now ----

    /// <summary>
    /// Update now, to the newest version: null once the updater has started (Gitberth is then replaced, and the page comes
    /// back by itself), else why not, in a sentence.
    /// </summary>
    public async Task<string?> UpdateNowAsync(CancellationToken ct = default)
    {
        if (!await _updating.WaitAsync(0, ct)) return "An update of Gitberth is under way.";
        try
        {
            var state = await StateAsync(ct);
            if (state.Latest is not { } latest || !UpdateGitberthRules.Newer(latest.Version, Version)) return $"Gitberth is up to date: it runs {Version}.";
            if (await CantUpdateAsync(ct) is { } cant) return cant;
            if (await BusyAsync(ct) is { } busy) return busy;
            _running = true;
            _steps = [];
            Changed?.Invoke();
            var by = AuditActors.Describe(AuditScope.Current);
            audit?.Record(new AuditEntry("settings.updates.gitberth.started", $"Started updating Gitberth from {Version} to {latest.Version}.")
            {
                Target = "Gitberth", Changes = [new("Version", Version, latest.Version)],
            });
            try
            {
                await PrepareAndStartAsync(latest.Version, by, ct);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _running = false;
                var why = ex is DockerApiException or UpdateRefusedException or InvalidOperationException ? ex.Message.TrimEnd('.') + "." : "It failed: " + ex.Message;
                Step(null, Backup.RestoreStepState.Failed, why);
                logger.LogWarning(ex, "Updating Gitberth didn't start");
                audit?.Record(new AuditEntry("settings.updates.gitberth.failed", $"The update of Gitberth to {latest.Version} didn't start: {AuditLog.Clean(why, 300)?.TrimEnd('.')}.")
                {
                    Target = "Gitberth", Outcome = AuditOutcome.Failed,
                });
                return "The update didn't start: " + why;
            }
        }
        finally
        {
            _updating.Release();
        }
    }

    // The steps up to the updater: pull, the version, the data's copy, the plan, the updater.
    private async Task PrepareAndStartAsync(string version, string by, CancellationToken ct)
    {
        var me = await docker.InspectContainerAsync(options.OwnContainer!, ct) ?? throw new InvalidOperationException("Gitberth's own container isn't there");
        var reference = Str(me["Config"], "Image");
        var (pull, run, retag) = UpdateGitberthRules.Target(reference, version);
        var target = ImageReference.Parse(pull)!;

        Step($"Pull {pull}", Backup.RestoreStepState.Running);
        string pulled;
        try
        {
            await docker.PullImageAsync(target.FromImage, target.TagOrDigest, null, ct);
            pulled = "Pulled.";
        }
        catch (DockerApiException ex)
        {
            // A host that has the image already (one loaded by hand, or the end-to-end rig's) uses it.
            if (await docker.InspectImageAsync(pull, ct) is null) throw;
            pulled = $"The pull failed ({ex.Message.TrimEnd('.')}); this host has the image, so it's used.";
        }
        Step(null, Backup.RestoreStepState.Done, pulled);

        Step("Check its version", Backup.RestoreStepState.Running);
        var image = await docker.InspectImageAsync(pull, ct) ?? throw new InvalidOperationException($"{pull} isn't on this host after its pull");
        var says = Str(image["Config"]?["Labels"], "org.opencontainers.image.version") is { Length: > 0 } label ? label : await VersionOfImageAsync(pull, ct);
        if (UpdateGitberthRules.VersionOf(says) != version)
            throw new UpdateRefusedException($"{pull} says it's version {(string.IsNullOrEmpty(says) ? "nothing" : says)}, not {version}, so it isn't used.");
        Step(null, Backup.RestoreStepState.Done, $"It's {version}.");
        if (retag)
        {
            var named = ImageReference.Parse(run)!;
            await docker.TagImageAsync(pull, named.Name, named.Tag ?? "latest", ct);
        }

        Step("Copy Gitberth's data", Backup.RestoreStepState.Running);
        var data = MountOf(me, options.DataDir) ?? throw new UpdateRefusedException("Gitberth's data isn't on a volume or a host folder, so it can't be kept safe across the update.");
        var copy = UpdateGitberthRules.CopyName(Version);
        await CopyDataAsync(Str(me, "Image"), data, copy, ct);
        Step(null, Backup.RestoreStepState.Done, $"In the volume {copy}.");

        Step("Start the updater", Backup.RestoreStepState.Running);
        var labels = me["Config"]?["Labels"] as JsonObject;
        var composeDir = Str(labels, "com.docker.compose.project.working_dir");
        var pinned = !retag && composeDir.StartsWith('/');
        var network = NetworkOf(me);
        var plan = new GitberthUpdatePlan
        {
            Container = Str(me, "Id"), Name = Str(me, "Name").TrimStart('/'), Image = run, From = Version, To = version, CopyVolume = copy,
            Network = network, Port = PortOf(me), ComposeImage = pinned ? run : null, ComposeOld = pinned ? reference : null, By = by, At = Now,
        };
        Directory.CreateDirectory(UpdateDir);
        TryDelete(Path.Combine(UpdateDir, "result.json"));
        await File.WriteAllTextAsync(Path.Combine(UpdateDir, "plan.json"), plan.ToJson(), ct);

        var binds = new JsonArray { $"{data}:/data", $"{copy}:/copy" };
        if (MountOf(me, "/var/run/docker.sock") is { } socket) binds.Add($"{socket}:/var/run/docker.sock");
        if (pinned) binds.Add($"{composeDir}:/compose");
        var host = new JsonObject { ["Binds"] = binds, ["NetworkMode"] = network, ["AutoRemove"] = true };
        if (me["HostConfig"]?["SecurityOpt"] is JsonArray security) host["SecurityOpt"] = security.DeepClone();
        var body = new JsonObject
        {
            ["Image"] = pull,
            ["Cmd"] = new JsonArray(UpdateGitberthRunner.Flag, "/data/update/plan.json"),
            ["User"] = "0:0",
            ["Labels"] = new JsonObject { [HostScanHelper.Label] = UpdateGitberthRules.HelperKind },
            ["HostConfig"] = host,
        };
        if (options.DockerHost is { } dockerHost && dockerHost != "unix:///var/run/docker.sock") body["Env"] = new JsonArray($"DOCKER_HOST={dockerHost}");
        var name = "gitberth-updater-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        await docker.CreateContainerAsync(name, body, ct);
        await docker.StartContainerAsync(name, ct);
        Step(null, Backup.RestoreStepState.Done, "Gitberth is updating; this page comes back by itself.");
        _ = WatchUpdaterAsync(name);
    }

    // While this Gitberth still runs, the updater hasn't stopped it yet: an updater gone without doing so failed before it
    // changed anything, and its result (or its absence) says so.
    private async Task WatchUpdaterAsync(string name)
    {
        for (var i = 0; i < 150; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                if (await docker.InspectContainerAsync(name) is not null) continue;
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!await ReadResultAsync(CancellationToken.None))
            {
                var state = await StateAsync(CancellationToken.None);
                state.LastResult = new GitberthUpdateResult(Version, state.Latest?.Version ?? "", false, "The updater stopped before it changed anything.", Now);
                await SaveStateAsync(state, CancellationToken.None);
            }
            _running = false;
            Changed?.Invoke();
            return;
        }
    }

    // Gitberth's data, copied into a volume of its own: its databases as SQLite snapshots (consistent while it runs), the
    // rest as it is (owners and modes kept), never its work, restore and update folders. The newest copies are kept.
    private async Task CopyDataAsync(string ownImage, string data, string copy, CancellationToken ct)
    {
        var snap = Path.Combine(UpdateDir, "snap");
        if (Directory.Exists(snap)) Directory.Delete(snap, recursive: true);
        Directory.CreateDirectory(snap);
        try
        {
            foreach (var file in Backup.BackupPaths.DataFiles)
                if (File.Exists(Path.Combine(options.DataDir, file)))
                    await Task.Run(() => Backup.BackupService.Snapshot(Path.Combine(options.DataDir, file), Path.Combine(snap, file)), ct);
            try
            {
                await docker.RemoveVolumeAsync(copy, ct);
            }
            catch (DockerApiException)
            {
            }
            await docker.CreateVolumeAsync(new JsonObject
            {
                ["Name"] = copy,
                ["Labels"] = new JsonObject
                {
                    [HostScanHelper.Label] = UpdateGitberthRules.HelperKind, ["gitberth.version"] = Version,
                    ["gitberth.made"] = Now.ToString("O", CultureInfo.InvariantCulture),
                },
            }, ct);
            var databases = string.Join('|', Backup.BackupPaths.DataFiles.Select(f => $"{f}|{f}-wal|{f}-shm|{f}-journal"));
            var script = "set -e; cd /data; for e in * .[!.]*; do [ -e \"$e\" ] || continue; case \"$e\" in work|restore|update) ;; " + databases + ") ;; " +
                         "*) cp -a \"$e\" /copy/ ;; esac; done; cp -a /data/update/snap/. /copy/";
            var helper = "gitberth-helper-update-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            await docker.CreateContainerAsync(helper, Backup.HostFolders.Body(ownImage, [$"{data}:/data:ro", $"{copy}:/copy"], UpdateGitberthRules.HelperKind, ["sh", "-c", script]), ct);
            try
            {
                await docker.StartContainerAsync(helper, ct);
                var exit = await docker.WaitContainerAsync(helper, ct);
                if (exit != 0) throw new UpdateRefusedException($"Copying Gitberth's data failed (exit code {exit.ToString(CultureInfo.InvariantCulture)}), so nothing was changed.");
            }
            finally
            {
                try
                {
                    await docker.RemoveContainerAsync(helper, force: true, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogDebug(ex, "Couldn't remove {Helper}", helper);
                }
            }
            await PruneCopiesAsync(copy, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(snap, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // The newest copies stay (this one with them); older ones go.
    private async Task PruneCopiesAsync(string keep, CancellationToken ct)
    {
        var copies = (await docker.ListVolumesAsync(ct)).OfType<JsonObject>()
            .Where(v => Str(v, "Name").StartsWith("gitberth-data-before-", StringComparison.Ordinal) && Str(v["Labels"], HostScanHelper.Label) == UpdateGitberthRules.HelperKind)
            .Select(v => (Name: Str(v, "Name"), Made: DateTime.TryParse(Str(v["Labels"], "gitberth.made"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : DateTime.MinValue));
        foreach (var old in UpdateGitberthRules.CopiesToRemove(copies, keep))
            try
            {
                await docker.RemoveVolumeAsync(old, ct);
            }
            catch (DockerApiException ex)
            {
                logger.LogInformation("The old copy {Copy} of Gitberth's data stays: {Why}", old, ex.Message);
            }
    }

    // A version the image says it is: dotnet Gitberth.dll --version in a throwaway container.
    private async Task<string> VersionOfImageAsync(string image, CancellationToken ct)
    {
        var name = "gitberth-helper-version-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        await docker.CreateContainerAsync(name, new JsonObject
        {
            ["Image"] = image, ["Cmd"] = new JsonArray("--version"), ["NetworkDisabled"] = true,
            ["Labels"] = new JsonObject { [HostScanHelper.Label] = UpdateGitberthRules.HelperKind },
            ["HostConfig"] = new JsonObject { ["NetworkMode"] = "none" },
        }, ct);
        try
        {
            await docker.StartContainerAsync(name, ct);
            await docker.WaitContainerAsync(name, ct);
            return GitberthUpdater.LastLine(await docker.ContainerLogsAsync(name, 5, ct)) ?? "";
        }
        finally
        {
            try
            {
                await docker.RemoveContainerAsync(name, force: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Couldn't remove {Helper}", name);
            }
        }
    }

    /// <summary>Where a container's <paramref name="destination"/> comes from: a volume's name, or a host folder; null when nothing is mounted there.</summary>
    internal static string? MountOf(JsonObject c, string destination) =>
        (c["Mounts"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(m => Str(m, "Destination").TrimEnd('/') == destination.TrimEnd('/')) is { } m
            ? Str(m, "Type") == "volume" ? Str(m, "Name") : Str(m, "Source")
            : null;

    /// <summary>The network the updater shares with Gitberth: "host" on the host's, else its first network.</summary>
    internal static string NetworkOf(JsonObject c)
    {
        var mode = Str(c["HostConfig"], "NetworkMode");
        if (mode == "host") return "host";
        var networks = c["NetworkSettings"]?["Networks"] as JsonObject;
        return networks?.Select(n => n.Key).FirstOrDefault(n => n == mode) ?? networks?.Select(n => n.Key).FirstOrDefault() ?? "bridge";
    }

    /// <summary>The port Gitberth listens on in its container: ASPNETCORE_HTTP_PORTS's first, 8080 by default.</summary>
    internal static int PortOf(JsonObject c)
    {
        var value = (c["Config"]?["Env"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? "")
            .FirstOrDefault(e => e.StartsWith("ASPNETCORE_HTTP_PORTS=", StringComparison.Ordinal))?["ASPNETCORE_HTTP_PORTS=".Length..];
        return int.TryParse(value?.Split([';', ','])[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 8080;
    }

    private void Step(string? title, Backup.RestoreStepState state, string? note = null)
    {
        var steps = _steps.ToList();
        if (title is not null) steps.Add(new GitberthUpdateStep(title, state, note));
        else if (steps.Count > 0) steps[^1] = steps[^1] with { State = state, Note = note };
        else steps.Add(new GitberthUpdateStep("Update", state, note));
        _steps = steps;
        Changed?.Invoke();
    }

    // ---- how the last one ended ----

    /// <summary>How long a Gitberth started by the updater waits for it to write how the update ended.</summary>
    internal TimeSpan ResultWithin { get; set; } = TimeSpan.FromMinutes(6);

    // A Gitberth the updater started (the new version, or the old one put back) answers before the updater writes how it
    // ended: while its plan is there and its result isn't yet, the result is waited for. A plan left without one (the
    // updater itself stopped) goes once the wait is over.
    private async Task AwaitResultAsync(CancellationToken ct)
    {
        var plan = Path.Combine(UpdateDir, "plan.json");
        var result = Path.Combine(UpdateDir, "result.json");
        var until = Now + ResultWithin;
        while (File.Exists(plan) && !File.Exists(result) && Now < until)
            await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
        if (!await ReadResultAsync(ct) && File.Exists(plan))
        {
            logger.LogWarning("An update of Gitberth left its plan without saying how it ended; see {Log}", Path.Combine(UpdateDir, "updater.log"));
            TryDelete(plan);
        }
    }

    /// <summary>
    /// The result the updater wrote, read once (at start, or when the updater ended without replacing this Gitberth): its
    /// record, the audit log and a notification. True when there was one.
    /// </summary>
    internal async Task<bool> ReadResultAsync(CancellationToken ct)
    {
        var path = Path.Combine(UpdateDir, "result.json");
        if (!File.Exists(path)) return false;
        GitberthUpdateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<GitberthUpdateResult>(await File.ReadAllTextAsync(path, ct), GitberthUpdateState.Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            result = null;
        }
        TryDelete(path);
        TryDelete(Path.Combine(UpdateDir, "plan.json"));
        if (result is null) return false;
        var state = await StateAsync(ct);
        state.LastResult = result;
        await SaveStateAsync(state, ct);
        audit?.Record(new AuditEntry(result.Ok ? "settings.updates.gitberth.updated" : "settings.updates.gitberth.failed", UpdateGitberthRules.Said(result))
        {
            Target = "Gitberth", Outcome = result.Ok ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            Changes = [new("Version", result.From, result.Ok ? result.To : result.From)],
            Actor = AuditActor.System(AuditVia.Updates),
        });
        notifier.Publish(result.Ok
            ? new Notice(NotifyEvent.GitberthUpdated, NoticeLevel.Good, $"Gitberth updated to {result.To}",
                $"It runs {result.To} now, and the apps kept running. The copy of its data from before is kept as {UpdateGitberthRules.CopyName(result.From)}.",
                await notifier.LinkAsync("settings#gitberth-update", ct), "", null, Now)
            : new Notice(NotifyEvent.GitberthUpdateFailed, NoticeLevel.Critical, $"The update of Gitberth to {result.To} failed",
                $"Gitberth went back to {result.From}, with its data as it was before: {(result.Why ?? "it didn't say why").TrimEnd('.')}.",
                await notifier.LinkAsync("settings#gitberth-update", ct), "", null, Now));
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
