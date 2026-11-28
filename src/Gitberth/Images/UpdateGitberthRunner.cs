using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Docker;

namespace Gitberth.Images;

/// <summary>What the updater asks of Docker (DockerApi's calls; a stand-in in tests).</summary>
internal interface IUpdaterDocker
{
    Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct);
    Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct);
    Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct);
    Task RenameContainerAsync(string idOrName, string name, CancellationToken ct);
    Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct);
    Task StartContainerAsync(string idOrName, CancellationToken ct);
    Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct);
    Task<byte[]> ContainerLogsAsync(string idOrName, int tail, CancellationToken ct);
}

/// <summary>Docker's own answers, through the Engine API.</summary>
internal sealed class DockerUpdater(DockerApi api) : IUpdaterDocker
{
    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) => api.InspectContainerAsync(idOrName, ct);
    public Task<JsonObject?> InspectImageAsync(string idOrName, CancellationToken ct) => api.InspectImageAsync(idOrName, ct);
    public Task StopContainerAsync(string idOrName, int timeoutSeconds, CancellationToken ct) => api.StopContainerAsync(idOrName, timeoutSeconds, ct);
    public Task RenameContainerAsync(string idOrName, string name, CancellationToken ct) => api.RenameContainerAsync(idOrName, name, ct);
    public Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct) => api.CreateContainerAsync(name, body, ct);
    public Task StartContainerAsync(string idOrName, CancellationToken ct) => api.StartContainerAsync(idOrName, ct);
    public Task RemoveContainerAsync(string idOrName, bool force, CancellationToken ct) => api.RemoveContainerAsync(idOrName, force, ct);
    public Task<byte[]> ContainerLogsAsync(string idOrName, int tail, CancellationToken ct) => api.ContainerLogsAsync(idOrName, tail, ct);
}

/// <summary>
/// The updater: Gitberth's new version, run in a container of its own (dotnet Gitberth.dll --self-update plan.json) with
/// Gitberth's Docker access, its data at /data and the copy of its data at /copy. It stops the old Gitberth, keeps it as
/// gitberth-before-&lt;version&gt;, makes the new one under the old name with the old one's whole config, and waits up to 3
/// minutes for its /healthz to answer the new version. Then the old one goes. If the new one doesn't answer (or stops), it
/// goes instead: the data is put back from the copy (the new version may have changed it), the old one gets its name back
/// and starts. Either way the result is written to /data/update/result.json, which Gitberth reads when it starts.
/// </summary>
internal sealed class GitberthUpdater(IUpdaterDocker docker, Func<string, CancellationToken, Task<string?>> healthz, Func<CancellationToken, Task<string?>> restoreData,
    Func<string, string, CancellationToken, Task> composeEnv, TextWriter log, TimeProvider? time = null)
{
    /// <summary>How long the new version has to answer /healthz.</summary>
    internal TimeSpan AnswerWithin { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often it's asked.</summary>
    internal TimeSpan AskEvery { get; init; } = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private void Say(string line) => log.WriteLine($"{_time.GetUtcNow():HH:mm:ss} {line}");

    public async Task<GitberthUpdateResult> RunAsync(GitberthUpdatePlan plan, CancellationToken ct)
    {
        Say($"Updating Gitberth from {plan.From} to {plan.To} ({plan.Image}).");
        if (await docker.InspectContainerAsync(plan.Container, ct) is not { } old)
            return Result(plan, false, "Gitberth's container wasn't there any more, so nothing was changed.");
        var oldId = Str(old, "Id");
        var oldImage = await docker.InspectImageAsync(Str(old, "Image"), ct);
        var body = UpdateGitberthRules.CreateBody(old, oldImage, plan.Image);
        var before = UpdateGitberthRules.BeforeName(plan.From);

        // A container left with that name by an earlier try (never the one running now) goes first.
        if (await docker.InspectContainerAsync(before, ct) is { } leftover && Str(leftover, "Id") != oldId)
            await docker.RemoveContainerAsync(before, force: true, ct);

        Say("Stopping the old Gitberth.");
        await docker.StopContainerAsync(oldId, StopSeconds(old), ct);
        try
        {
            await docker.RenameContainerAsync(oldId, before, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await docker.StartContainerAsync(oldId, CancellationToken.None);
            return Result(plan, false, "The old Gitberth couldn't be put aside (" + Sentence(ex.Message) + "), so it's running again as it was.");
        }

        string? newId = null;
        try
        {
            Say($"Starting {plan.To} as {plan.Name}.");
            newId = await docker.CreateContainerAsync(plan.Name, body, ct);
            await docker.StartContainerAsync(newId, ct);
            await WaitAnswerAsync(newId, plan, plan.To, ct);
            Say($"{plan.To} answers. The old Gitberth goes.");
            try
            {
                await docker.RemoveContainerAsync(oldId, force: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Say($"The old Gitberth ({before}) couldn't be removed: {ex.Message}. Remove it by hand when you like.");
            }
            if (plan.ComposeImage is { } image && plan.ComposeOld is { } was)
            {
                try
                {
                    await composeEnv(was, image, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Say($"Compose's .env couldn't be updated: {ex.Message}. Set GITBERTH_IMAGE={image} in it, or docker compose up goes back.");
                }
            }
            return Result(plan, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = ex is UpdateRefusedException ? ex.Message : Sentence(ex.Message);
            Say($"{plan.To} didn't take over: {why} Putting {plan.From} back.");
            if (newId is not null)
            {
                try
                {
                    await docker.StopContainerAsync(newId, 10, CancellationToken.None);
                }
                catch (Exception stop) when (stop is not OutOfMemoryException)
                {
                    Say($"Stopping {plan.To}: {stop.Message}");
                }
                try
                {
                    await docker.RemoveContainerAsync(newId, force: true, CancellationToken.None);
                }
                catch (Exception remove) when (remove is not OutOfMemoryException)
                {
                    Say($"Removing {plan.To}: {remove.Message}");
                }
            }
            // The new version may have changed the data (a migration): it's put back as it was.
            if (await restoreData(CancellationToken.None) is { } notBack) Say("The data couldn't be put back from its copy: " + notBack);
            await docker.RenameContainerAsync(oldId, plan.Name, CancellationToken.None);
            await docker.StartContainerAsync(oldId, CancellationToken.None);
            try
            {
                await WaitAnswerAsync(oldId, plan, plan.From, CancellationToken.None);
                Say($"{plan.From} answers again.");
            }
            catch (Exception again) when (again is not OutOfMemoryException)
            {
                Say($"{plan.From} doesn't answer yet: {again.Message}");
            }
            return Result(plan, false, why);
        }
    }

    // Up to AnswerWithin for /healthz to say the version; it stopping, or answering another version, ends the wait.
    private async Task WaitAnswerAsync(string id, GitberthUpdatePlan plan, string version, CancellationToken ct)
    {
        var until = _time.GetUtcNow() + AnswerWithin;
        while (true)
        {
            var c = await docker.InspectContainerAsync(id, ct) ?? throw new UpdateRefusedException("Its container was removed while it started.");
            // Stopped, or stopped and started again by its restart policy: Docker then says it's restarting, and counts it.
            var state = c["State"];
            if (state?["Running"]?.GetValue<bool>() != true || state["Restarting"]?.GetValue<bool>() == true
                || c["RestartCount"] is JsonValue restarts && restarts.TryGetValue<int>(out var times) && times > 0)
            {
                var code = c["State"]?["ExitCode"]?.GetValue<int>() ?? 0;
                var said = LastLine(await docker.ContainerLogsAsync(id, 20, CancellationToken.None));
                throw new UpdateRefusedException($"It stopped right after it started (exit code {code.ToString(CultureInfo.InvariantCulture)})" + (said is null ? "." : $": {said}"));
            }
            if (HealthUrl(c, plan) is { } url && await healthz(url, ct) is { } answered)
            {
                if (answered == version) return;
                throw new UpdateRefusedException($"It answers as version {answered}, not {version}.");
            }
            if (_time.GetUtcNow() > until) throw new UpdateRefusedException($"It didn't answer within {AnswerWithin.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes.");
            await Task.Delay(AskEvery, _time, ct);
        }
    }

    /// <summary>Where a container answers /healthz: its address on the plan's network, or the host's own on the host's network.</summary>
    internal static string? HealthUrl(JsonObject c, GitberthUpdatePlan plan)
    {
        var port = plan.Port.ToString(CultureInfo.InvariantCulture);
        if (plan.Network == "host") return $"http://127.0.0.1:{port}/healthz";
        var networks = c["NetworkSettings"]?["Networks"] as JsonObject;
        var ip = (networks?[plan.Network] ?? networks?.FirstOrDefault().Value)?["IPAddress"]?.GetValue<string>();
        return string.IsNullOrEmpty(ip) ? null : $"http://{ip}:{port}/healthz";
    }

    private static int StopSeconds(JsonObject c) => c["Config"]?["StopTimeout"] is JsonValue v && v.TryGetValue<int>(out var s) && s > 0 ? s : 30;

    private GitberthUpdateResult Result(GitberthUpdatePlan plan, bool ok, string? why)
    {
        Say(ok ? $"Updated to {plan.To}." : $"The update to {plan.To} failed: {why}");
        return new GitberthUpdateResult(plan.From, plan.To, ok, why, _time.GetUtcNow().UtcDateTime, plan.By);
    }

    private static string Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Sentence(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "it said nothing";
        return line.EndsWith('.') ? line : line + ".";
    }

    // The last line a container printed (Docker's frames, or plain text from a TTY), cut to one line.
    internal static string? LastLine(byte[] logs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < logs.Length;)
        {
            if (i + 8 <= logs.Length && logs[i] is 1 or 2 && logs[i + 1] == 0 && logs[i + 2] == 0 && logs[i + 3] == 0)
            {
                var length = (logs[i + 4] << 24) | (logs[i + 5] << 16) | (logs[i + 6] << 8) | logs[i + 7];
                var take = Math.Min(length, logs.Length - i - 8);
                text.Append(Encoding.UTF8.GetString(logs, i + 8, take));
                i += 8 + take;
            }
            else
            {
                text.Append(Encoding.UTF8.GetString(logs, i, logs.Length - i));
                break;
            }
        }
        var line = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return line is null ? null : line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>Why the new version didn't take over, in a sentence the owner reads.</summary>
internal sealed class UpdateRefusedException(string message) : Exception(message);

/// <summary>
/// The updater's entry (dotnet Gitberth.dll --self-update /data/update/plan.json): Docker through the socket or DOCKER_HOST
/// Gitberth had, the data at /data, its copy at /copy, Compose's folder at /compose when its .env pins the image.
/// </summary>
public static class UpdateGitberthRunner
{
    public const string Flag = "--self-update";

    /// <summary>Runs the plan and writes its result next to it; the process's exit code (0 when it worked).</summary>
    public static async Task<int> RunAsync(string planPath)
    {
        var log = new StringWriter();
        var tee = new TeeWriter(Console.Out, log);
        var dir = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        GitberthUpdateResult result;
        try
        {
            var plan = GitberthUpdatePlan.Parse(await File.ReadAllTextAsync(planPath))
                ?? throw new InvalidOperationException("The update's plan couldn't be read.");
            var host = Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } h ? h : "unix:///var/run/docker.sock";
            using var api = new DockerApi(new GitberthOptions { DataDir = "/data", DockerHost = host });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var updater = new GitberthUpdater(new DockerUpdater(api), (url, ct) => VersionAtAsync(http, url, ct), RestoreDataAsync, ComposeEnvAsync, tee);
            result = await updater.RunAsync(plan, CancellationToken.None);
        }
        catch (Exception ex)
        {
            tee.WriteLine($"The updater failed: {ex}");
            result = new GitberthUpdateResult("", "", false, "The updater failed: " + ex.Message, DateTime.UtcNow);
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "updater.log"), log.ToString());
        var json = JsonSerializer.Serialize(result, GitberthUpdateState.Json);
        await File.WriteAllTextAsync(Path.Combine(dir, "result.json.new"), json);
        File.Move(Path.Combine(dir, "result.json.new"), Path.Combine(dir, "result.json"), overwrite: true);
        return result.Ok ? 0 : 1;
    }

    // /healthz's version, or null while it doesn't answer.
    private static async Task<string?> VersionAtAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Gitberth's data as it was: everything but the update's own folder emptied, then the copy put back (owners and modes kept).
    private static async Task<string?> RestoreDataAsync(CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("sh", ["-c", "set -e; find /data -mindepth 1 -maxdepth 1 ! -name update -exec rm -rf {} +; cp -a /copy/. /data/"], null, ct);
        return r.Ok ? null : r.ErrorText;
    }

    // Compose's .env moved to the new image, when it pinned the old one.
    private static async Task ComposeEnvAsync(string old, string image, CancellationToken ct)
    {
        const string path = "/compose/.env";
        if (!File.Exists(path)) return;
        if (UpdateGitberthRules.EnvWith(await File.ReadAllTextAsync(path, ct), old, image) is { } next) await File.WriteAllTextAsync(path, next, ct);
    }

    /// <summary>The updater's lines to its log and the result's.</summary>
    private sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            a.WriteLine(value);
            b.WriteLine(value);
        }
    }
}
