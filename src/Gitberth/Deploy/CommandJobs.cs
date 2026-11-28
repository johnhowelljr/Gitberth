using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>A scheduled command as the Jobs card shows it, with its last runs (newest first).</summary>
public sealed record JobView(ScheduledCommand Command, IReadOnlyList<CommandRun> Runs, DateTime? NextUtc)
{
    public CommandRun? Last => Runs.FirstOrDefault();
    public bool Running => Runs.FirstOrDefault()?.Status == CommandRunStatus.Running;
}

/// <summary>What the card asks for when it adds or changes a command.</summary>
public sealed record JobInput(string Name, string Schedule, string Command, CommandPlace Place, string? Service, int TimeoutMinutes);

/// <summary>What a save or a Run now did: why it was refused, or what it says now.</summary>
public sealed record JobOutcome(bool Ok, string Message, int? CommandId = null);

/// <summary>
/// An app's scheduled commands (B2), for every app: each has a name, a schedule in Gitberth's time zone, a command, where it
/// runs (in the running container with docker exec, or in a new container of the app's image) and a time limit (30 minutes
/// unless it says). A run due while the one before it still runs is skipped. Run now runs one at once. Each run's when, how
/// long, exit code and last 64 KB of output (masked) are kept, the newest 20. A failed run tells Notifications (JobFailed;
/// JobOk once one passes again) and the app's Activity; adding, changing, removing and Run now go in the audit log.
/// </summary>
public sealed class CommandJobs(IDbContextFactory<GitberthDb> dbFactory, OneOffRunner runner, DockerApi api, ActivityLog activity, Notifier notifier,
    ILogger<CommandJobs> logger, Lazy<AppCatalog>? catalog = null, Audit.AuditLog? audit = null)
{
    public const int KeptRuns = 20;
    public const int DefaultMinutes = 30;
    public const int MaxCommand = 2000;

    // The commands running now, by ID: a run due while one runs is skipped.
    private readonly ConcurrentDictionary<int, Task> _running = new();

    /// <summary>Raised after a run starts or ends, and after a change, for open pages.</summary>
    public event Action<int>? Changed;

    /// <summary>The app's commands, each with its newest runs (without their output: <see cref="OutputAsync"/> reads it), and when it runs next.</summary>
    public async Task<IReadOnlyList<JobView>> ListAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var commands = await db.ScheduledCommands.AsNoTracking().Where(c => c.AppId == appId).OrderBy(c => c.Id).ToListAsync(ct);
        var ids = commands.Select(c => c.Id).ToList();
        var runs = (await db.CommandRuns.AsNoTracking().Where(r => ids.Contains(r.CommandId)).OrderByDescending(r => r.Id)
            .Select(r => new CommandRun
            {
                Id = r.Id, CommandId = r.CommandId, AppId = r.AppId, Trigger = r.Trigger, RequestedBy = r.RequestedBy, Status = r.Status,
                ExitCode = r.ExitCode, Error = r.Error, StartedAt = r.StartedAt, FinishedAt = r.FinishedAt,
            }).ToListAsync(ct)).ToLookup(r => r.CommandId);
        var now = DateTime.UtcNow;
        return commands.Select(c => new JobView(c, runs[c.Id].Take(KeptRuns).ToList(), JobSchedules.NextUtc(c.Schedule, now))).ToList();
    }

    /// <summary>What a run printed (its last 64 KB, masked); empty when nothing, null when the run is gone.</summary>
    public async Task<string?> OutputAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.CommandRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.Output }).FirstOrDefaultAsync(ct);
        return run is null ? null : run.Output ?? "";
    }

    /// <summary>
    /// At start-up: a run Gitberth was running when it stopped never ended here, so it's written down as failed (how it ended
    /// isn't known), and its command runs again on time.
    /// </summary>
    internal async Task<int> SettleAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var running = _running.Keys.ToList();
        return await db.CommandRuns.Where(r => r.Status == CommandRunStatus.Running && !running.Contains(r.CommandId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CommandRunStatus.Failed)
                .SetProperty(r => r.Error, "Gitberth restarted while it ran, so how it ended isn't known.")
                .SetProperty(r => r.FinishedAt, now), ct);
    }

    /// <summary>Why the input can't be saved, or null.</summary>
    public static string? Problem(JobInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Give the command a name, like Nightly cleanup.";
        if (input.Name.Trim().Length > 60) return "Keep the name to 60 characters.";
        if (string.IsNullOrWhiteSpace(input.Command)) return "Write the command to run, like npm run cleanup.";
        if (input.Command.Length > MaxCommand) return $"Keep the command to {MaxCommand.ToString(CultureInfo.InvariantCulture)} characters.";
        if (input.Command.Any(c => c is '\0' or '\n' or '\r')) return "Write the command on one line: join several with &&.";
        if (input.TimeoutMinutes is < 1 or > 1440) return "The time limit is from 1 minute to 24 hours (1440 minutes).";
        if (!Detection.CronSchedule.TryParse(input.Schedule, out _, out var error)) return error;
        if (input.Service is { Length: > 0 } s && !Compose.ComposeNames.IsServiceName(s)) return $"{s} isn't a service's name.";
        return null;
    }

    /// <summary>Adds a command to the app (its key); a container Gitberth keeps nothing about yet gets its row first, as any first edit does.</summary>
    public async Task<JobOutcome> AddAsync(string key, JobInput input, CancellationToken ct = default)
    {
        if (Problem(input) is { } problem) return new JobOutcome(false, problem);
        AppDefinition app;
        try
        {
            app = await AppOfAsync(key, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new JobOutcome(false, ex.Message);
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var command = new ScheduledCommand
        {
            AppId = app.Id, Name = input.Name.Trim(), Schedule = input.Schedule.Trim(), Command = input.Command.Trim(), Place = input.Place,
            Service = app.IsCompose && input.Service is { Length: > 0 } s && s != app.WebService ? s : null, TimeoutMinutes = input.TimeoutMinutes,
        };
        db.ScheduledCommands.Add(command);
        await db.SaveChangesAsync(ct);
        audit?.Record(new Audit.AuditEntry("app.job.added", $"Added the scheduled command {command.Name} to {app.Name}: {JobSchedules.Describe(command.Schedule)}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = command.Name, Changes = Lines(null, command),
        });
        Changed?.Invoke(app.Id);
        return new JobOutcome(true, $"Added {command.Name}: it runs {JobSchedules.Describe(command.Schedule)} ({JobSchedules.ZoneName()}).", command.Id);
    }

    public async Task<JobOutcome> ChangeAsync(int commandId, JobInput input, CancellationToken ct = default)
    {
        if (Problem(input) is { } problem) return new JobOutcome(false, problem);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var command = await db.ScheduledCommands.FirstOrDefaultAsync(c => c.Id == commandId, ct);
        if (command is null) return new JobOutcome(false, "That command is gone.");
        var app = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == command.AppId, ct);
        var before = Copy(command);
        command.Name = input.Name.Trim();
        command.Schedule = input.Schedule.Trim();
        command.Command = input.Command.Trim();
        command.Place = input.Place;
        command.Service = app.IsCompose && input.Service is { Length: > 0 } s && s != app.WebService ? s : null;
        command.TimeoutMinutes = input.TimeoutMinutes;
        var changes = Lines(before, command);
        if (changes.Count == 0) return new JobOutcome(true, "Nothing changed.", command.Id);
        command.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        audit?.Record(new Audit.AuditEntry("app.job.changed", $"Changed the scheduled command {command.Name} of {app.Name}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = command.Name, Changes = changes,
        });
        Changed?.Invoke(app.Id);
        return new JobOutcome(true, $"Saved {command.Name}: it runs {JobSchedules.Describe(command.Schedule)} ({JobSchedules.ZoneName()}).", command.Id);
    }

    public async Task<JobOutcome> RemoveAsync(int commandId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var command = await db.ScheduledCommands.FirstOrDefaultAsync(c => c.Id == commandId, ct);
        if (command is null) return new JobOutcome(true, "It's gone already.");
        var app = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == command.AppId, ct);
        db.ScheduledCommands.Remove(command);
        await db.SaveChangesAsync(ct);
        audit?.Record(new Audit.AuditEntry("app.job.removed", $"Removed the scheduled command {command.Name} from {app.Name}.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = command.Name, Changes = Lines(command, null),
        });
        Changed?.Invoke(app.Id);
        return new JobOutcome(true, $"Removed {command.Name}.");
    }

    /// <summary>Run now: the command starts at once, in the background; refused while it runs.</summary>
    public async Task<JobOutcome> RunNowAsync(int commandId, CancellationToken ct = default)
    {
        ScheduledCommand? command;
        AppDefinition? app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            command = await db.ScheduledCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commandId, ct);
            app = command is null ? null : await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == command.AppId, ct);
        }
        if (command is null || app is null) return new JobOutcome(false, "That command is gone.");
        if (_running.ContainsKey(command.Id)) return new JobOutcome(false, $"{command.Name} is running now: wait for it to end.");
        var by = Audit.AuditActors.Format(Audit.AuditScope.Current);
        audit?.Record(new Audit.AuditEntry("app.job.run", $"Ran the scheduled command {command.Name} of {app.Name} now.")
        {
            AppId = app.Id, AppName = app.Name, Container = app.ContainerName, Target = command.Name,
        });
        return await StartAsync(command, app, CommandTrigger.RunNow, by)
            ? new JobOutcome(true, $"{command.Name} is running: its card shows how it ends.", command.Id)
            : new JobOutcome(false, $"{command.Name} is running now: wait for it to end.");
    }

    /// <summary>The scheduler's look: every command due since <paramref name="lastUtc"/> runs (or, still running, is skipped).</summary>
    internal async Task<int> RunDueAsync(DateTime lastUtc, DateTime nowUtc, CancellationToken ct)
    {
        List<ScheduledCommand> commands;
        Dictionary<int, AppDefinition> apps;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            commands = await db.ScheduledCommands.AsNoTracking().ToListAsync(ct);
            var ids = commands.Select(c => c.AppId).Distinct().ToList();
            apps = await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        }
        var started = 0;
        foreach (var command in commands)
        {
            if (!apps.TryGetValue(command.AppId, out var app) || app.IsPreview) continue;
            if (!JobSchedules.Due(command.Schedule, lastUtc, nowUtc)) continue;
            // A stopped app's commands wait for it: nothing runs in (or like) a container that's meant to be down.
            if (app.DesiredState == DesiredState.Stopped) continue;
            if (await StartAsync(command, app, CommandTrigger.Schedule, null)) started++;
            else await SkippedAsync(command, app, ct);
        }
        return started;
    }

    // A run in the background, written down (as Running) before this returns; false when the command runs already.
    private async Task<bool> StartAsync(ScheduledCommand command, AppDefinition app, CommandTrigger trigger, string? by)
    {
        var gate = new TaskCompletionSource();
        if (!_running.TryAdd(command.Id, gate.Task)) return false;
        var run = new CommandRun { CommandId = command.Id, AppId = app.Id, Trigger = trigger, RequestedBy = by, StartedAt = DateTime.UtcNow };
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.CommandRuns.Add(run);
            await db.SaveChangesAsync();
        }
        catch
        {
            _running.TryRemove(command.Id, out _);
            gate.TrySetResult();
            throw;
        }
        Changed?.Invoke(app.Id);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(command, app, run);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scheduled command {Command} of {App} failed to run", command.Name, app.Slug);
            }
            finally
            {
                _running.TryRemove(command.Id, out _);
                gate.TrySetResult();
                Changed?.Invoke(app.Id);
            }
        });
        return true;
    }

    /// <summary>The runs going on now (for a test, or Gitberth stopping).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    private async Task RunAsync(ScheduledCommand command, AppDefinition app, CommandRun run)
    {
        var container = command.Service is { } service && app.IsCompose ? Compose.ComposeNames.Member(app.Slug, service) : app.ContainerName;
        var limit = TimeSpan.FromMinutes(Math.Clamp(command.TimeoutMinutes, 1, 1440));
        OneOffResult result;
        try
        {
            var mask = new OutputMask(await SecretsAsync(container, app));
            result = command.Place == CommandPlace.NewContainer
                ? await runner.RunLikeAsync(container, null, command.Command, limit, mask, null, CancellationToken.None)
                : await runner.ExecAsync(container, command.Command, limit, mask, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException or InvalidOperationException)
        {
            result = new OneOffResult(CommandRunStatus.Failed, null, "", $"Docker couldn't run it: {ex.Message.TrimEnd('.')}.");
        }
        run.Status = result.Status;
        run.ExitCode = result.ExitCode;
        run.Output = result.Output.Length > 0 ? result.Output : null;
        run.Error = result.Error;
        run.FinishedAt = DateTime.UtcNow;
        CommandRun? before;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CommandRuns.Update(run);
            await db.SaveChangesAsync();
            before = await db.CommandRuns.AsNoTracking()
                .Where(r => r.CommandId == command.Id && r.Id < run.Id && r.Status != CommandRunStatus.Skipped && r.Status != CommandRunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            await PruneAsync(db, command.Id);
        }
        await TellAsync(command, app, container, run, before);
    }

    // A run due while the one before it still runs: written down as skipped, and nothing else.
    private async Task SkippedAsync(ScheduledCommand command, AppDefinition app, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        db.CommandRuns.Add(new CommandRun
        {
            CommandId = command.Id, AppId = app.Id, Trigger = CommandTrigger.Schedule, Status = CommandRunStatus.Skipped, StartedAt = now, FinishedAt = now,
            Error = "Skipped: the run before it was still going.",
        });
        await db.SaveChangesAsync(ct);
        await PruneAsync(db, command.Id);
        Changed?.Invoke(app.Id);
    }

    private static async Task PruneAsync(GitberthDb db, int commandId)
    {
        var old = await db.CommandRuns.Where(r => r.CommandId == commandId).OrderByDescending(r => r.Id).Skip(KeptRuns).Select(r => r.Id).ToListAsync();
        if (old.Count > 0) await db.CommandRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync();
    }

    // A failed run (and the first that passes after failures) is told: Notifications and the app's Activity.
    private async Task TellAsync(ScheduledCommand command, AppDefinition app, string container, CommandRun run, CommandRun? before)
    {
        var failed = run.Status is CommandRunStatus.Failed or CommandRunStatus.TimedOut;
        var wasFailing = before?.Status is CommandRunStatus.Failed or CommandRunStatus.TimedOut;
        if (!failed && !wasFailing) return;
        var why = run.Status == CommandRunStatus.TimedOut ? run.Error ?? "It ran too long."
            : run.Error ?? $"It ended with exit code {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}.";
        var title = failed ? $"Scheduled command {command.Name} of {app.Name} failed" : $"Scheduled command {command.Name} of {app.Name} works again";
        var text = failed ? $"{why} Its card on {app.Name}'s page shows its output." : $"It passed again (exit code 0), after {(before?.Status == CommandRunStatus.TimedOut ? "running too long" : "failing")} last time.";
        try
        {
            await activity.AddAsync(app.ContainerName, app.Id, ActivityKind.Job, failed ? $"{title}: {why}" : $"{title}.", failed ? ActivityTone.Danger : ActivityTone.Good);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Couldn't write down the run of {Command}", command.Name);
        }
        var link = await notifier.LinkAsync($"apps/{Uri.EscapeDataString(app.ContainerName)}", CancellationToken.None);
        notifier.Publish(new Notice(failed ? NotifyEvent.JobFailed : NotifyEvent.JobOk, failed ? NoticeLevel.Critical : NoticeLevel.Good, title, text, link, app.Name, app.ContainerName,
            DateTime.UtcNow) { AppId = app.Id, Preview = app.IsPreview });
    }

    // The values to mask in what a command prints: the container's variables that are secret (marked on the app, or looking it).
    private async Task<IReadOnlyList<string>> SecretsAsync(string container, AppDefinition app)
    {
        try
        {
            if (await api.InspectContainerAsync(container, CancellationToken.None) is not { } inspect) return [];
            var body = new System.Text.Json.Nodes.JsonObject { ["Env"] = inspect["Config"]?["Env"]?.DeepClone() };
            return ReleaseCommands.SecretValues(body, app);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            return [];
        }
    }

    private async Task<AppDefinition> AppOfAsync(string key, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var slug = key.StartsWith("gb-", StringComparison.Ordinal) ? key[3..] : null;
            if (await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Container == key || (a.Container == null && slug != null && a.Slug == slug), ct) is { } row)
                return row;
        }
        if (catalog is null) throw new InvalidOperationException($"There's no app called {key}.");
        return await catalog.Value.AdoptAsync(key, ct);
    }

    private static ScheduledCommand Copy(ScheduledCommand c) => new()
    {
        Id = c.Id, AppId = c.AppId, Name = c.Name, Schedule = c.Schedule, Command = c.Command, Place = c.Place, Service = c.Service, TimeoutMinutes = c.TimeoutMinutes,
    };

    // What changed, as the audit log lists it: the command's secrets hidden.
    private static List<Audit.AuditChange> Lines(ScheduledCommand? before, ScheduledCommand? after)
    {
        var lines = new List<Audit.AuditChange>();
        void Add(string what, string? a, string? b)
        {
            if (a != b) lines.Add(new(what, a, b));
        }
        Add("Name", before?.Name, after?.Name);
        Add("Schedule", before is null ? null : JobSchedules.Describe(before.Schedule), after is null ? null : JobSchedules.Describe(after.Schedule));
        Add("Command", before is null ? null : Security.Masking.Text(before.Command), after is null ? null : Security.Masking.Text(after.Command));
        Add("Runs in", before is null ? null : Where(before), after is null ? null : Where(after));
        Add("Time limit", before is null ? null : $"{before.TimeoutMinutes} min", after is null ? null : $"{after.TimeoutMinutes} min");
        return lines;
    }

    /// <summary>Where a command runs, in words.</summary>
    public static string Where(ScheduledCommand c) =>
        (c.Place == CommandPlace.NewContainer ? "a new container" : "the running container") + (c.Service is { } s ? $" of {s}" : "");
}

/// <summary>
/// Runs the scheduled commands (<see cref="CommandJobs"/>) on time: every 15 seconds it looks at what came due since its
/// last look. A run missed while Gitberth was down doesn't run late; the next one is on time. Holds off while a restore
/// runs.
/// </summary>
public sealed class CommandScheduler(CommandJobs jobs, ILogger<CommandScheduler> logger, Backup.RestoreGate? restoreGate = null) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await jobs.SettleAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Settling the scheduled commands' runs cut short by a restart failed");
        }
        var last = DateTime.UtcNow;
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            if (restoreGate?.Holding == true)
            {
                last = now;
                continue;
            }
            try
            {
                await jobs.RunDueAsync(last, now, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Looking for scheduled commands to run failed");
            }
            last = now;
        }
    }
}
