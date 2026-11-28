using System.Globalization;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>What saving a release command did: why it was refused, or what it says now.</summary>
public sealed record ReleaseSaved(bool Ok, string Message);

/// <summary>
/// An app's release command (B2), like <c>npm run migrate</c>: run once per new version before it takes over, in a one-off
/// container of the new image with the app's variables, volumes and networks (no ports, domains or restart policy). A
/// non-zero exit or a timeout (10 minutes unless the app says, 1 to 60) stops the new version: the running one stays. It
/// runs when the image changes (a build, an upload, a promote, a rollback, an image app's update or a new image through
/// the change engine), never on a restart or a recreate with the same image. A Compose app runs it in its web service, or
/// the one it chose, with docker compose run.
/// </summary>
public sealed class ReleaseCommands(IDbContextFactory<GitberthDb> dbFactory, OneOffRunner runner, ILogger<ReleaseCommands> logger,
    Lazy<AppCatalog>? catalog = null, Audit.AuditLog? audit = null)
{
    public const int DefaultMinutes = 10;
    public const int MaxLength = 2000;

    /// <summary>How long the app's release command may take.</summary>
    public static TimeSpan TimeoutOf(AppDefinition app) => TimeSpan.FromMinutes(Math.Clamp(app.ReleaseTimeoutMinutes ?? DefaultMinutes, 1, 60));

    /// <summary>Whether a deploy runs the release command: it has one, and the image changes (a build, or another image than the one running).</summary>
    public static bool Runs(AppDefinition app, bool build, string image) =>
        app.ReleaseCommand is { Length: > 0 } && (build || !string.Equals(image, app.CurrentImage, StringComparison.Ordinal));

    /// <summary>Why a release command can't be saved as it is; null when it can.</summary>
    public static string? Problem(string? command, int? minutes)
    {
        if (command is { } c && c.Length > MaxLength) return $"Keep the release command to {MaxLength.ToString(CultureInfo.InvariantCulture)} characters.";
        if (command is { } d && d.Any(ch => ch == '\0' || ch == '\n' || ch == '\r')) return "Write the release command on one line: join several with &&.";
        if (minutes is < 1 or > 60) return "The release command's time limit is from 1 to 60 minutes.";
        return null;
    }

    /// <summary>
    /// Saves the app's release command (empty: none), its time limit and, for a Compose app, its service. A container
    /// Gitberth keeps nothing about yet gets its row first, as any first edit does. Audited.
    /// </summary>
    public async Task<ReleaseSaved> SaveAsync(string key, string? command, int? minutes, string? service, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(command) ? null : command.Trim();
        if (Problem(text, minutes) is { } problem) return new ReleaseSaved(false, problem);
        if (service is { Length: > 0 } s && !Compose.ComposeNames.IsServiceName(s)) return new ReleaseSaved(false, $"{s} isn't a service's name.");
        int appId;
        try
        {
            appId = await AppIdAsync(key, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ReleaseSaved(false, ex.Message);
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return new ReleaseSaved(false, "That app no longer exists.");
        var before = (app.ReleaseCommand, app.ReleaseTimeoutMinutes, app.ReleaseService);
        app.ReleaseCommand = text;
        app.ReleaseTimeoutMinutes = text is null || minutes is null or DefaultMinutes ? null : minutes;
        app.ReleaseService = text is null || !app.IsCompose || string.IsNullOrWhiteSpace(service) || service == app.WebService ? null : service.Trim();
        if (before == (app.ReleaseCommand, app.ReleaseTimeoutMinutes, app.ReleaseService)) return new ReleaseSaved(true, "Nothing changed.");
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var changes = new List<Audit.AuditChange> { new("Release command", Shown(before.ReleaseCommand), Shown(app.ReleaseCommand)) };
        if (before.ReleaseTimeoutMinutes != app.ReleaseTimeoutMinutes)
            changes.Add(new("Time limit", $"{before.ReleaseTimeoutMinutes ?? DefaultMinutes} min", $"{app.ReleaseTimeoutMinutes ?? DefaultMinutes} min"));
        if (before.ReleaseService != app.ReleaseService) changes.Add(new("Service", before.ReleaseService, app.ReleaseService));
        audit?.Record(new Audit.AuditEntry("app.release.changed", app.ReleaseCommand is null
            ? $"Removed {app.Name}'s release command."
            : before.ReleaseCommand is null ? $"Set {app.Name}'s release command." : $"Changed {app.Name}'s release command.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Changes = changes,
        });
        return new ReleaseSaved(true, app.ReleaseCommand is null ? "Saved: no release command." : "Saved: it runs before each new version takes over.");
    }

    // A command as the audit log shows it: its secrets (a password option, a URL's password) hidden.
    private static string? Shown(string? command) => command is null ? null : Security.Masking.Text(command);

    private async Task<int> AppIdAsync(string key, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key[3..] : null;
            if (await db.Apps.AsNoTracking().Where(a => a.Container == key || (a.Container == null && slug != null && a.Slug == slug))
                    .Select(a => (int?)a.Id).FirstOrDefaultAsync(ct) is int id)
                return id;
        }
        if (catalog is null) throw new InvalidOperationException($"There's no app called {key}.");
        return (await catalog.Value.AdoptAsync(key, ct)).Id;
    }

    // ---- Running it ----

    /// <summary>
    /// A Git or uploaded app's deploy: the release command in a one-off container of the new version (<paramref name="spec"/>,
    /// as the deploy would start it). Its output goes to the deploy's log. Throws the deploy's failure when it fails.
    /// </summary>
    public async Task RunForDeployAsync(AppDefinition app, ContainerSpec spec, DeployLog log, CancellationToken ct)
    {
        var command = app.ReleaseCommand!;
        var limit = TimeoutOf(app);
        log.Step($"Running the release command in the new version, before it takes over: {Security.Masking.Text(command)}");
        var result = await runner.RunSpecAsync(spec, command, limit, log.Line, ct);
        Ended(app, result, limit, log);
    }

    /// <summary>A Compose app's deploy: docker compose run of its release service (its web one unless it chose), from the release's file.</summary>
    public async Task RunForComposeAsync(AppDefinition app, Compose.ComposeRunner composer, string file, string projectDir, DeployLog log, CancellationToken ct)
    {
        var command = app.ReleaseCommand!;
        var service = app.ReleaseService ?? app.WebService ?? throw new DeployException("The app has no web service to run its release command in.");
        var limit = TimeoutOf(app);
        log.Step($"Running the release command in a one-off {service} of the new release, before it takes over: {Security.Masking.Text(command)}");
        var name = HostScanHelper.NewName(OneOffRunner.HelperKind);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(limit);
        OneOffResult result;
        try
        {
            var r = await composer.RunAsync(app.ComposeProject, file, projectDir,
                ["run", "--rm", "-T", "--name", name, "--label", $"{HostScanHelper.Label}={OneOffRunner.HelperKind}", "--entrypoint", "/bin/sh", service, "-c", command],
                log.Line, timer.Token);
            result = r.ExitCode == 0 ? new OneOffResult(CommandRunStatus.Succeeded, 0, "", null)
                : r.ExitCode == -1 ? new OneOffResult(CommandRunStatus.Failed, null, "", r.ErrorText)
                : new OneOffResult(CommandRunStatus.Failed, r.ExitCode, "", OneOffRunner.NoShell(r.StdOut + r.StdErr));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new OneOffResult(CommandRunStatus.TimedOut, null, "", $"It didn't finish within {OneOffRunner.Minutes(limit)}, so it was stopped.");
        }
        finally
        {
            // docker compose run --rm leaves the container when it's cut short: that one container (by its name) goes, and
            // nothing else of the project.
            await runner.RemoveAsync(name);
        }
        Ended(app, result, limit, log);
    }

    // The deploy goes on when it worked; it stops, the running version kept, when it didn't.
    private static void Ended(AppDefinition app, OneOffResult result, TimeSpan limit, DeployLog log)
    {
        if (result.Ok)
        {
            log.Line("The release command finished (exit code 0).");
            return;
        }
        var keeps = app.CurrentImage is null ? "nothing was started" : $"{app.Name} keeps running its current version";
        throw new DeployException(result.Status switch
        {
            CommandRunStatus.TimedOut => $"The release command didn't finish within {OneOffRunner.Minutes(limit)}, so {keeps}.",
            _ when result.ExitCode is int code && result.Error is null => $"The release command failed (exit code {code.ToString(CultureInfo.InvariantCulture)}), so {keeps}. Its output is in the log above.",
            _ => $"The release command couldn't run: {result.Error?.TrimEnd('.') ?? "Docker refused it"}. So {keeps}.",
        });
    }

    /// <summary>
    /// An app the change engine recreates on another image (an update, a rollback, a new image in its settings): its release
    /// command, when it has one, in a one-off container of <paramref name="body"/> (the new container's create body) before
    /// anything of the running one changes. Null when it worked (or there's none), else why nothing changed.
    /// </summary>
    public async Task<string?> BeforeImageChangeAsync(string container, JsonObject body, CancellationToken ct)
    {
        AppDefinition? app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var slug = container.StartsWith("gb-", StringComparison.Ordinal) ? container[3..] : null;
            app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Container == container || (a.Container == null && slug != null && a.Slug == slug), ct);
        }
        if (app is not { ReleaseCommand: { Length: > 0 } command } || app.Deployed) return null;
        var limit = TimeoutOf(app);
        var secrets = SecretValues(body, app);
        OneOffResult result;
        try
        {
            result = await runner.RunBodyAsync(body, command, limit, new OutputMask(secrets), null, ct);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            logger.LogWarning(ex, "The release command of {Container} couldn't run", container);
            return $"Nothing changed: its release command couldn't run ({ex.Message.TrimEnd('.')}).";
        }
        if (result.Ok) return null;
        var tail = string.Join(" ", result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(5));
        var said = tail.Length > 0 ? $" Its last output: {(tail.Length > 600 ? tail[^600..] : tail)}" : "";
        return result.Status switch
        {
            CommandRunStatus.TimedOut => $"Nothing changed: its release command didn't finish within {OneOffRunner.Minutes(limit)}.{said}",
            _ when result.ExitCode is int code && result.Error is null => $"Nothing changed: its release command failed (exit code {code.ToString(CultureInfo.InvariantCulture)}).{said}",
            _ => $"Nothing changed: its release command couldn't run ({result.Error?.TrimEnd('.')}).",
        };
    }

    /// <summary>The values a create body's variables hold that are secret (marked so on the app, or looking it), to mask in what a command prints.</summary>
    internal static IReadOnlyList<string> SecretValues(JsonObject body, AppDefinition? app)
    {
        var marked = app?.SecretKeys is { Length: > 0 } json ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        var values = new List<string>();
        foreach (var node in body["Env"] as JsonArray ?? [])
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var pair) || pair.IndexOf('=') is not (> 0 and var eq)) continue;
            var (key, value) = (pair[..eq], pair[(eq + 1)..]);
            if (marked.Contains(key) || Security.Masking.IsSecret(key, value)) values.Add(value);
        }
        return values;
    }
}
