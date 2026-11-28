using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;

namespace Gitberth.Deploy;

/// <summary>How a one-off command ended: its status, exit code, the last of what it printed (masked), and why it didn't run.</summary>
public sealed record OneOffResult(CommandRunStatus Status, int? ExitCode, string Output, string? Error)
{
    public bool Ok => Status == CommandRunStatus.Succeeded;
}

/// <summary>
/// Hides secret values in what a command prints: each value (of four characters or more) becomes ••••, as the deploy log
/// does it.
/// </summary>
public sealed class OutputMask(IEnumerable<string> secrets)
{
    private readonly string[] _secrets = secrets.Where(s => s.Length >= 4).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();

    public static readonly OutputMask None = new([]);

    public string Apply(string text)
    {
        foreach (var s in _secrets) text = text.Replace(s, Security.Masking.Mask, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>
/// Runs a command once, for a release command (B2) or a scheduled one: in a new container of an app's image (from a
/// create body the change engine made, or from the running container's own settings, or from a deploy's container spec),
/// or in the running container (docker exec). Each runs through /bin/sh -c, with a timeout, and keeps the last 64 KB of
/// what it printed, masked. A new container is Gitberth's helper (gitberth.helper=command): no ports, no restart policy,
/// none of the app's names on its networks and no address of its own, so nothing reaches it, and it's removed when it
/// ends.
/// </summary>
/// <param name="engine">The Engine API, for a new container made from a create body; without it (a deploy's, in tests) only the docker CLI's runs work.</param>
public sealed class OneOffRunner(DockerCli docker, GitberthOptions options, ILogger<OneOffRunner> logger, DockerApi? engine = null)
{
    private DockerApi Api => engine ?? throw new InvalidOperationException("Gitberth can't make a container from here.");

    /// <summary>The helper label's value on a one-off container.</summary>
    public const string HelperKind = "command";

    /// <summary>How much of what a command printed is kept: its last 64 KB.</summary>
    public const int OutputMax = 64 * 1024;

    /// <summary>The words a command runs with: the shell, and the command as one word.</summary>
    private const string Shell = "/bin/sh";

    // ---- In a new container ----

    /// <summary>
    /// A one-off container's create body, from the app's own (a change's new container, or one made from the running
    /// container's settings): the command through /bin/sh -c in place of the image's entrypoint and command, no ports, no
    /// restart policy, no health check, no terminal, Gitberth's helper label in place of the app's labels (so no list counts
    /// it, and Compose doesn't take it for a service), and on the same networks without the app's names or addresses there
    /// (a network it has an address of its own on is left out, as a deploy's temporary container does).
    /// </summary>
    internal static JsonObject OneOffBody(JsonObject appBody, string command)
    {
        var body = (JsonObject)appBody.DeepClone();
        body["Entrypoint"] = new JsonArray(Shell, "-c");
        body["Cmd"] = new JsonArray(command);
        body["Tty"] = false;
        body["OpenStdin"] = false;
        body["AttachStdin"] = false;
        body["StdinOnce"] = false;
        body["Healthcheck"] = new JsonObject { ["Test"] = new JsonArray("NONE") };
        body.Remove("ExposedPorts");
        body.Remove("Hostname");
        body.Remove("MacAddress");
        body["Labels"] = new JsonObject { [HostScanHelper.Label] = HelperKind };
        if (body["HostConfig"] is JsonObject host)
        {
            host.Remove("PortBindings");
            host["PublishAllPorts"] = false;
            host["RestartPolicy"] = new JsonObject { ["Name"] = "no" };
            host["AutoRemove"] = false;
        }
        if (body["NetworkingConfig"]?["EndpointsConfig"] is JsonObject endpoints)
        {
            foreach (var (name, node) in endpoints.ToList())
            {
                if (node is not JsonObject endpoint) continue;
                if (endpoint["IPAMConfig"] is JsonObject ipam && (ipam["IPv4Address"] is JsonValue v && v.TryGetValue<string>(out var ip) && ip.Length > 0))
                {
                    endpoints.Remove(name);
                    continue;
                }
                endpoint.Remove("Aliases");
                endpoint.Remove("DNSNames");
                endpoint.Remove("IPAMConfig");
                endpoint.Remove("MacAddress");
                endpoint.Remove("Links");
            }
        }
        return body;
    }

    /// <summary>A one-off container from <paramref name="appBody"/> (see <see cref="OneOffBody"/>), waited for up to <paramref name="timeout"/>.</summary>
    public async Task<OneOffResult> RunBodyAsync(JsonObject appBody, string command, TimeSpan timeout, OutputMask mask, Action<string>? onLine, CancellationToken ct)
    {
        var name = HostScanHelper.NewName(HelperKind);
        var output = new OutputTail(mask, onLine);
        try
        {
            await Api.CreateContainerAsync(name, OneOffBody(appBody, command), ct);
        }
        catch (DockerApiException ex)
        {
            return new OneOffResult(CommandRunStatus.Failed, null, "", $"Docker refused the container to run it in: {ex.Message.TrimEnd('.')}.");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            try
            {
                await Api.StartContainerAsync(name, limit.Token);
            }
            catch (DockerApiException ex)
            {
                return new OneOffResult(CommandRunStatus.Failed, null, "", NoShell(ex.Message) ?? $"Its container didn't start: {ex.Message.TrimEnd('.')}.");
            }
            // What it prints, as it prints it, until it ends (docker logs --follow ends with it).
            var follow = docker.RunAsync(["logs", "--follow", name], new ProcessOptions { OnLine = output.Add, MaxCapture = 1024 }, limit.Token);
            int exit;
            try
            {
                exit = await Api.WaitContainerAsync(name, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await SafeAsync(follow);
                return new OneOffResult(CommandRunStatus.TimedOut, null, output.Text, $"It didn't finish within {Minutes(timeout)}, so it was stopped.");
            }
            await SafeAsync(follow);
            return Ended(exit, output.Text);
        }
        finally
        {
            await RemoveQuietlyAsync(name);
        }
    }

    /// <summary>
    /// A one-off container like the running <paramref name="container"/>: its settings (its variables, volumes, user,
    /// working folder, limits) and networks as Docker has them, on <paramref name="image"/> when given, else its own image.
    /// </summary>
    public async Task<OneOffResult> RunLikeAsync(string container, string? image, string command, TimeSpan timeout, OutputMask mask, Action<string>? onLine,
        CancellationToken ct)
    {
        var inspect = await Api.InspectContainerAsync(container, ct);
        if (inspect is null) return new OneOffResult(CommandRunStatus.Failed, null, "", $"{container} has no container now, so there's nothing to run it like.");
        var settings = Network.ContainerSettingsJson.Read(inspect, null);
        if (image is not null) settings = settings with { Image = image };
        else if (Str(inspect, "Image") is { Length: > 0 } id) settings = settings with { Image = id };
        var joins = Apps.AppCatalog.JoinsOf(inspect, options.Network);
        var priorities = await Api.ApiVersionAsync(ct) >= new Version(1, 48);
        return await RunBodyAsync(Network.ContainerSettingsJson.NewBody(settings, joins, "", priorities), command, timeout, mask, onLine, ct);
    }

    // ---- In the running container ----

    /// <summary>
    /// docker exec in the running container: /bin/sh -c, under coreutils' or busybox's timeout when the image has one (so
    /// a command that runs too long is killed in the container too); the command is an argument of its own, never put into
    /// the script.
    /// </summary>
    public async Task<OneOffResult> ExecAsync(string container, string command, TimeSpan timeout, OutputMask mask, Action<string>? onLine, CancellationToken ct)
    {
        var output = new OutputTail(mask, onLine);
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The CLI is given a little longer than the command, so timeout's own kill comes first.
        limit.CancelAfter(timeout + TimeSpan.FromSeconds(15));
        ProcessResult r;
        try
        {
            r = await docker.RunAsync(["exec", container, Shell, "-c", ExecScript, seconds, command],
                new ProcessOptions { OnLine = output.Add, MaxCapture = 4096 }, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new OneOffResult(CommandRunStatus.TimedOut, null, output.Text,
                $"It didn't finish within {Minutes(timeout)}. The image has no timeout command, so it may still be running in {container}.");
        }
        if (r.ExitCode == -1) return new OneOffResult(CommandRunStatus.Failed, null, output.Text, r.ErrorText);
        // docker exec's own refusals: the container isn't running (or is gone), or there's no shell in it.
        if (r.ExitCode != 0 && r.StdErr.Contains("Error response from daemon", StringComparison.Ordinal) && NoContainer(r.StdErr, container) is { } gone)
            return new OneOffResult(CommandRunStatus.Failed, null, output.Text, gone);
        if (r.ExitCode is 125 or 126 or 127 && NoShell(output.Text + "\n" + r.StdErr) is { } why)
            return new OneOffResult(CommandRunStatus.Failed, r.ExitCode, output.Text, why);
        // timeout's kill (128 + 9), or its own "timed out" (124).
        if (r.ExitCode is 137 or 124) return new OneOffResult(CommandRunStatus.TimedOut, r.ExitCode, output.Text, $"It didn't finish within {Minutes(timeout)}, so it was stopped.");
        return Ended(r.ExitCode, output.Text);
    }

    /// <summary>What docker exec runs: the command under timeout when there's one ($0 is the seconds, $1 the command).</summary>
    internal const string ExecScript =
        "if command -v timeout >/dev/null 2>&1; then exec timeout -s KILL \"$0\" /bin/sh -c \"$1\"; else exec /bin/sh -c \"$1\"; fi";

    // ---- In a new container of a deploy's new image ----

    /// <summary>
    /// docker run for a deploy's release command: the new version's container as the deploy would start it (its variables,
    /// volumes and networks, its settings beyond the classic ones), with no ports and no restart policy, its names left off
    /// its networks, and the command through /bin/sh -c. Secret values reach it through the docker CLI's environment.
    /// </summary>
    public async Task<OneOffResult> RunSpecAsync(ContainerSpec spec, string command, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
    {
        var name = HostScanHelper.NewName(HelperKind);
        var output = new OutputTail(OutputMask.None, onLine);
        var (args, env) = RunArgs(spec, name, command);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            ProcessResult r;
            try
            {
                r = await docker.RunAsync(args, new ProcessOptions { Environment = env, OnLine = output.Add, MaxCapture = 4096 }, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new OneOffResult(CommandRunStatus.TimedOut, null, output.Text, $"It didn't finish within {Minutes(timeout)}, so it was stopped.");
            }
            if (r.ExitCode == -1) return new OneOffResult(CommandRunStatus.Failed, null, output.Text, r.ErrorText);
            // docker run's own failures: the container couldn't be made or started (no shell in the image, say).
            if (r.ExitCode is 125 or 126 or 127 && (NoShell(output.Text + "\n" + r.StdErr) is { } why))
                return new OneOffResult(CommandRunStatus.Failed, r.ExitCode, output.Text, why);
            if (r.ExitCode == 125) return new OneOffResult(CommandRunStatus.Failed, r.ExitCode, output.Text, "Docker couldn't start the container to run it in; the log above says why.");
            return Ended(r.ExitCode, output.Text);
        }
        finally
        {
            // docker run without --rm: the container is removed here, also when the run was cut short.
            await docker.RunAsync(["rm", "-f", name], new ProcessOptions { MaxCapture = 1024 }, CancellationToken.None);
        }
    }

    /// <summary>The docker run of <see cref="RunSpecAsync"/>, and the docker CLI's environment its secret values go through.</summary>
    internal static (List<string> Args, Dictionary<string, string> Env) RunArgs(ContainerSpec spec, string name, string command)
    {
        var args = new List<string> { "run", "--name", name, "--label", $"{HostScanHelper.Label}={HelperKind}", "--restart", "no" };
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var network in spec.Networks) args.AddRange(["--network", ContainerSpec.NetworkArg(network with { Aliases = [], IPv4Address = null }, spec.GatewayPriority)]);
        foreach (var e in spec.Env)
        {
            args.Add("-e");
            if (e.IsSecret && EnvPassing.CanPassViaEnvironment(e.Key))
            {
                args.Add(e.Key);
                env[e.Key] = e.Value;
            }
            else args.Add($"{e.Key}={e.Value}");
        }
        foreach (var v in spec.Volumes) args.AddRange(["-v", $"{v.Source}:{v.Target}{(v.ReadOnly ? ":ro" : "")}"]);
        if (spec.MemoryMb is > 0) args.AddRange(["--memory", $"{spec.MemoryMb}m"]);
        if (spec.Cpus is > 0) args.AddRange(["--cpus", spec.Cpus.Value.ToString("0.##", CultureInfo.InvariantCulture)]);
        // The app's own settings beyond the classic ones, but not what makes it a service: its entrypoint and command (the
        // command replaces them), its health check, a terminal, and its own labels (the helper's label stands instead).
        if (spec.Extras is { } x) args.AddRange(ContainerSpec.ExtraArgs(x with { Entrypoint = null, Command = null, Health = null, Tty = null, OpenStdin = null, Labels = null }));
        args.AddRange(["--entrypoint", Shell, spec.Image, "-c", command]);
        return (args, env);
    }

    /// <summary>Removes a one-off container by its name (docker rm -f), when it's there: a compose run's that was cut short.</summary>
    public async Task RemoveAsync(string name)
    {
        var r = await docker.RunAsync(["rm", "-f", name], new ProcessOptions { MaxCapture = 1024 }, CancellationToken.None);
        if (r.ExitCode != 0 && !r.StdErr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            logger.LogDebug("Couldn't remove the helper {Helper}: {Error}", name, r.ErrorText);
    }

    // ---- How it ended ----

    private static OneOffResult Ended(int exit, string output) => exit == 0
        ? new OneOffResult(CommandRunStatus.Succeeded, 0, output, null)
        : new OneOffResult(CommandRunStatus.Failed, exit, output, null);

    /// <summary>The sentence for an image with no shell, when Docker's words say so; null otherwise.</summary>
    internal static string? NoShell(string text) =>
        text.Contains(Shell, StringComparison.Ordinal) && (text.Contains("no such file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("executable file", StringComparison.OrdinalIgnoreCase))
            ? "There's no shell (/bin/sh) in the image, so a command can't run in it."
            : null;

    private static string? NoContainer(string text, string container) =>
        text.Contains("No such container", StringComparison.OrdinalIgnoreCase) ? $"{container} has no container now."
        : text.Contains("is not running", StringComparison.OrdinalIgnoreCase) ? $"{container} isn't running, so nothing can run in it."
        : null;

    internal static string Minutes(TimeSpan t) => t.TotalMinutes >= 1
        ? (t.TotalMinutes == 1 ? "1 minute" : $"{t.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes")
        : $"{(int)t.TotalSeconds} seconds";

    private async Task RemoveQuietlyAsync(string name)
    {
        try
        {
            await Api.RemoveContainerAsync(name, force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Couldn't remove the helper {Helper}", name);
        }
    }

    private static async Task SafeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    private static string? Str(JsonObject? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The last <see cref="OutputMax"/> of what a command printed, masked line by line, and each line handed on.</summary>
    internal sealed class OutputTail(OutputMask mask, Action<string>? onLine)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            var masked = mask.Apply(line);
            onLine?.Invoke(masked);
            lock (_gate)
            {
                _text.Append(masked).Append('\n');
                if (_text.Length > OutputMax) _text.Remove(0, _text.Length - OutputMax);
            }
        }

        public string Text
        {
            get
            {
                lock (_gate) return _text.ToString();
            }
        }
    }
}
