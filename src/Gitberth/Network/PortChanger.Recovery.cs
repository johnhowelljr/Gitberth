using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;

namespace Gitberth.Network;

public sealed partial class PortChanger
{
    // How often an inspect is tried before Docker counts as not answering.
    private const int InspectTries = 3;

    /// <summary>
    /// At startup: finishes or undoes each change a restart of Gitberth cut short, acting only on the containers its
    /// journal row names (never on names that merely look like Gitberth's). Before the stop, the new container goes and
    /// nothing changed; between the stop and the start, the original comes back; after the start, the change is finished
    /// when the new container runs and passes the quick checks, counted from its own start (so it has stayed up 15 s, or
    /// reports healthy), else the original comes back; a rollback under way goes on, however well the new container runs.
    /// A stop asked for before the restart first gets until its timeout to finish, since Docker finishes it anyway. A
    /// change waiting for a stopped container's first start keeps waiting, unless it started meanwhile: that start gets
    /// the checks its start event would have. Anything unclear, a Docker that doesn't answer included, keeps both
    /// containers for the owner to choose. The new container is never started here. It can wait as long as a stop timeout
    /// or a health check, so it shouldn't hold up the web UI.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        foreach (var seen in await journal.UnfinishedAsync(ct))
        {
            if (seen.Outcome != ChangeOutcome.Running) continue;
            using var _ = await locks.AcquireAsync(seen.ContainerName, ct);
            // Read again under the lock: a change in this process may have moved it on meanwhile.
            if (await ReadAsync(seen.Id, ct) is not { Outcome: ChangeOutcome.Running } row) continue;
            try
            {
                await RecoverAsync(row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Finishing the interrupted change on {Container} failed", row.ContainerName);
                await DecideAsync(await ReadAsync(row.Id, CancellationToken.None) ?? row,
                    $"Gitberth couldn't finish or undo the {Noun(row.Kind)} on {row.ContainerName}: {ex.Message}",
                    $"{On(row.Kind, row.ContainerName)} was interrupted.");
            }
        }
    }

    private async Task RecoverAsync(ContainerChange row)
    {
        var name = row.ContainerName;
        await StopSettledAsync(row);
        switch (row.Step)
        {
            case ChangeStep.Creating or ChangeStep.Disarming:
            {
                // The create's answer may have been lost, so a container with the temporary name made since is looked for.
                var note = await TidyAsync(row, byTempName: true);
                var text = $"Gitberth restarted before it {Changed(row.Kind, name)}, so nothing changed." + (note is null ? "" : " " + note);
                await EndAsync(row, ChangeOutcome.NothingChanged, text, null, text);
                logger.LogWarning("{Text}", text);
                return;
            }
            case ChangeStep.Stopping or ChangeStep.Renaming:
                await PutBackAfterRestartAsync(row, start: row.WasRunning);
                return;
            case ChangeStep.RollingBack:
                // The change had failed, or the owner chose the original: never finished now, however well the new one runs.
                await PutBackAfterRestartAsync(row, start: row.WasRunning || await HasStartedAsync(row));
                return;
            case ChangeStep.Starting or ChangeStep.Verifying:
                if (await QuickCheckAsync(row) is null) await FinishAfterRestartAsync(row, FinishedNotice(name));
                else await PutBackAfterRestartAsync(row, start: row.WasRunning);
                return;
            case ChangeStep.WaitingForStart:
                await FirstStartQuietlyAsync(row);
                return;
            case ChangeStep.RemovingOld:
                await FinishAfterRestartAsync(row, FinishedNotice(name));
                return;
        }
    }

    private static string FinishedNotice(string name) =>
        $"Gitberth restarted while it recreated {name}. It finished the change: the new container runs and passed its checks.";

    // A stop asked for before Gitberth went may still be under way: Docker finishes a stop after its caller has gone, and
    // a start meanwhile only says it runs (304), until the stop takes it down after all. So the original gets until its
    // stop timeout has passed to finish stopping, before anything relies on its state. One started since (a rollback put
    // it back) isn't stopping.
    private async Task StopSettledAsync(ContainerChange row)
    {
        if (row.StopAt is not { } at) return;
        if (await InspectAsync(row.OldId) is not { } original || !ContainerPorts.Running(original)) return;
        var asked = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        if (StateOf(original).StartedAt > asked) return;
        var left = asked + StopOf(original, IsDatabase(original, row.ContainerName)).Window - _time.GetUtcNow();
        if (left > TimeSpan.Zero) await WaitStoppedAsync(row.OldId, left);
    }

    private async Task<bool> HasStartedAsync(ContainerChange row) =>
        row.NewId is { } id && await InspectAsync(id) is { } made && StartedSince(made);

    /// <summary>
    /// For a slow timer (every few minutes): each recreated stopped container that waits for its first start is looked at,
    /// in case the event watcher missed that start; until it's checked, it has no restart policy. One that started is
    /// checked, and its change finished or left for the owner; the others keep waiting. One busy right now waits its turn.
    /// </summary>
    public async Task CheckWaitingAsync(CancellationToken ct = default)
    {
        foreach (var seen in await journal.UnfinishedAsync(ct))
        {
            if (seen is not { Outcome: ChangeOutcome.Running, Step: ChangeStep.WaitingForStart }) continue;
            using var held = await locks.TryAcquireAsync(seen.ContainerName);
            if (held is null) continue;
            if (await ReadAsync(seen.Id, ct) is { Outcome: ChangeOutcome.Running, Step: ChangeStep.WaitingForStart } row) await FirstStartQuietlyAsync(row);
        }
    }

    // Docker not answering decides nothing: the change keeps waiting, and the next look tries again.
    private async Task FirstStartQuietlyAsync(ContainerChange row)
    {
        try
        {
            await FirstStartAsync(row);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation(ex, "Couldn't look at {Container}'s first start since its port change", row.ContainerName);
        }
    }

    // A recreated stopped container waiting for its first start, looked at without its start event (by the timer, or at
    // startup): one that started meanwhile gets the checks its start event would have given it, counted from its own
    // start, so the original only goes once it has stayed up 15 s (or reported healthy) and published every port. One
    // that never started, or was started and stopped cleanly by hand, keeps waiting: its next start is the one checked.
    private async Task FirstStartAsync(ContainerChange row)
    {
        var name = row.ContainerName;
        if (row.NewId is not { } newId) return;
        if (await InspectAsync(newId) is not { } made)
        {
            await DecideAsync(row, $"The new {name} is gone; the original is kept as {row.OldName}.", $"{On(row.Kind, name)} can't finish.");
            return;
        }
        if (KeepsWaiting(made)) return;
        await FirstStartCheckedAsync(row, newId, CancellationToken.None);
    }

    // Not started since its port change, or started and stopped cleanly by hand since (exit code 0, or 143 for SIGTERM):
    // its next start is the one checked.
    private static bool KeepsWaiting(JsonObject made) =>
        !StartedSince(made) || StateOf(made) is { Running: false, OomKilled: false, ExitCode: 0 or 143 };

    /// <summary>
    /// For the event watcher, on every container start (not awaited in its loop). A recreated stopped container's first
    /// start is checked like a recreate's new container: when it passes, it gets the original's restart policy, the
    /// original goes and the change is done, else both stay for the owner. When both containers of a change that isn't
    /// over run, on the same volumes, the owner is asked to choose at once. Null at once for any other container, and for
    /// a change under way, which checks its own starts. A start heard late (Docker replays the ones Gitberth missed while
    /// it was away) of a container stopped cleanly by hand since decides nothing either, as for the timer: its next start
    /// is the one checked. Takes as long as the checks (15 s, or until healthy).
    /// </summary>
    public async Task<PortChangeResult?> ContainerStartedAsync(string container, CancellationToken ct = default)
    {
        if (await RowOfAsync(container, ct) is not { } seen || UnderWay(seen)) return null;
        using var _ = await locks.AcquireAsync(seen.ContainerName, ct);
        if (await ReadAsync(seen.Id, ct) is not { } row || UnderWay(row)) return null;
        try
        {
            if (await BothRunAsync(row) is { } both) return await DecideAsync(row, both.Message, both.Lead);
            if (row.Outcome != ChangeOutcome.Running || container != row.ContainerName || row.NewId is not { } newId) return null;
            if (await InspectAsync(newId) is { } made && KeepsWaiting(made)) return null;
            return await FirstStartCheckedAsync(row, newId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Docker didn't answer: nothing was decided. The change keeps waiting, and the timer's next look checks it.
            logger.LogWarning(ex, "Couldn't check {Container}'s start after its port change", container);
            return null;
        }
    }

    // Not over and not waiting for a start: its ApplyAsync, or recovery, has it.
    private static bool UnderWay(ContainerChange row) => row.Outcome == ChangeOutcome.Running && row.Step != ChangeStep.WaitingForStart;

    private async Task<PortChangeResult> FirstStartCheckedAsync(ContainerChange row, string newId, CancellationToken ct)
    {
        var none = CancellationToken.None;
        var container = row.ContainerName;
        var made = await InspectAsync(newId);
        if (made is null || NameOf(made) != container)
            return await DecideAsync(row, $"The container now called {container} isn't the one its {Noun(row.Kind)} made.",
                $"{On(row.Kind, container)} can't finish.");

        var lanAddress = await hostPorts.LanAddressAsync(ct);
        var bindings = CloneSpec.Bindings(made, ContainerPorts.Rules(made), "");
        var c = new Change
        {
            Name = container,
            Row = row,
            NewId = newId,
            Stop = StopOf(made, IsDatabase(made, container)),
            Health = HealthWindow(made),
            Tty = made["Config"]?["Tty"]?.GetValueKind() == JsonValueKind.True,
            Bindings = bindings,
            LanTargets = LanTargets(bindings, lanAddress),
            LanAddress = lanAddress,
            Before = Before.Nothing,
            Steps = new StepTracker(null, _time, []),
            Scrub = ScrubberFor(made),
        };
        var verified = await VerifyAsync(c, StateOf(made).StartedAt ?? _time.GetUtcNow(), measure: false);
        if (verified.Failure is { } failure)
        {
            var text = c.Scrub($"{container}'s first start since its {Noun(row.Kind)} failed its checks: {char.ToLowerInvariant(failure.Text[0])}{failure.Text[1..]}");
            return await DecideAsync(row, text, $"{text} The original is kept as {row.OldName}.", await LastOutputAsync(newId, c.Tty));
        }
        // The original may have been started while this one was checked.
        if (await BothRunAsync(row) is { } both) return await DecideAsync(row, c.Scrub(both.Message), both.Lead);
        // It passed: it gets the original's restart policy, and the original goes.
        var policyError = await TryAsync(() => engine.UpdateRestartPolicyAsync(newId, NewPolicyOf(row), none));
        var note = await TryAsync(() => engine.RemoveAsync(row.OldId, false, none), goneIsFine: true) is { } removeError
            ? $" The original, {row.OldName}, couldn't be removed ({removeError})."
            : " The original is removed (its volumes stay).";
        if (policyError is not null) note += " " + PolicyNote(row, policyError);
        var done = c.Scrub($"{container} started {WithWhat(row.Kind)} and passed its checks.{note}");
        await EndAsync(row, ChangeOutcome.Done, done, null);
        return new PortChangeResult(PortChangeOutcome.Done, done) { NothingAnswers = verified.Answering.Count == 0 && verified.Silent.Count > 0 };
    }

    /// <summary>
    /// Why the container mustn't be started or restarted now, for ContainerService's start and restart and for
    /// RestartScheduler (which then waits for its next tick); null when nothing stands in the way. The two containers of a
    /// port change that isn't over mount the same volumes: neither starts while the other runs, and the kept original
    /// comes back only through [Put the original back]. One that Docker can't answer for isn't started either. Nor is a
    /// copy a finished change left behind because Docker didn't remove it (the original of a change that was kept, the
    /// new container of one that wasn't): it mounts the volumes of the container that stayed. Each is known by its ID as
    /// well as its names, whatever it's called now.
    /// </summary>
    public async Task<string?> WhyNotStartAsync(string container, CancellationToken ct = default)
    {
        var row = await RowOfAsync(container, ct);
        if (row is not null && UnderWay(row)) return BusyText(row.ContainerName);
        try
        {
            if (await InspectAsync(container) is not { } target) return null;
            var id = Str(target, "Id");
            row ??= await journal.WithContainerAsync(id, ct);
            if (row is null) return null;
            if (UnderWay(row)) return BusyText(row.ContainerName);
            if (!row.Unfinished) return LeftBehind(row, id, NameOf(target));
            if (id == row.OldId)
                return $"{NameOf(target)} is {row.ContainerName}'s original, kept until its {Noun(row.Kind)} is settled: put the original back to use it.";
            if (id == row.NewId && await InspectAsync(row.OldId) is { } original && ContainerPorts.Running(original))
                return $"{row.ContainerName}'s original, {NameOf(original)}, runs on the same volumes: keep the new one or put the original back first.";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only a change that isn't over holds a start up when Docker can't say: anything else starts as ever.
            return row is null ? null : $"Couldn't check {container} with Docker ({ex.Message}).";
        }
    }

    // A copy a finished change left behind because Docker didn't remove it: the original of a change that was kept, or the
    // new container of one that wasn't. It mounts the same volumes as the container that stayed.
    private static string? LeftBehind(ContainerChange row, string id, string name) =>
        row.Outcome == ChangeOutcome.Done
            ? id == row.OldId ? $"{name} is the old copy of {row.ContainerName} from its {Noun(row.Kind)} and mounts the same volumes; remove it instead." : null
            : id == row.NewId ? $"{name} is the unused copy of {row.ContainerName} from its {Noun(row.Kind)} and mounts the same volumes; remove it instead." : null;

    /// <summary>
    /// Why the container mustn't be stopped or removed now, for ContainerService: it's one of the containers of a port
    /// change under way (by any of its names: the change's lock is the container's name alone), or one a restart of
    /// Gitberth cut short and recovery hasn't settled yet. Null otherwise: one that waits for a start or for the owner may
    /// be stopped, and removed, as the owner likes.
    /// </summary>
    public async Task<string?> BusyWithAsync(string container, CancellationToken ct = default) =>
        await RowOfAsync(container, ct) is { } row && UnderWay(row) ? BusyText(row.ContainerName) : null;

    /// <summary>
    /// The owner's [Put the original back] (and [Start the original] after a failed rollback): the new container stops
    /// for good and goes, and the original takes back its name, its restart policy and, when it ran (or the new one has
    /// run since), its place. A change whose containers were removed or replaced outside Gitberth just ends.
    /// </summary>
    public async Task<PortChangeResult> PutOriginalBackAsync(string container, CancellationToken ct = default)
    {
        using var _ = await locks.AcquireAsync(container, ct);
        if (await PendingAsync(container, ct) is not { } row) return Nothing($"There's no change on {container} to undo.");
        try
        {
            var newId = row.NewId ?? await TempIdAsync(row);
            var made = newId is null ? null : await InspectAsync(newId);
            var original = await InspectAsync(row.OldId);
            if (await OverAsync(row, made, original) is { } over) return over;
            if (original is null) return Nothing($"The original {container} is gone, so the new container is the one to keep.");
            var start = row.WasRunning || (made is not null && StartedSince(made));
            // Written down first: if Gitberth restarts now, it goes on putting the original back.
            row = await SaveAsync(row with { Step = ChangeStep.RollingBack });
            var restored = await RestoreAsync(row, start);
            if (restored.Error is { } error)
                return await DecideAsync(row, $"The original ({await NameOrAsync(row.OldId, row.OldName)}) {error}. Both containers are kept.",
                    $"{On(row.Kind, container)} didn't finish.");
            var text = $"{container} is back as it was." + (restored.Note is { } note ? " " + note : "");
            await EndAsync(row, ChangeOutcome.RolledBack, text, null);
            return new PortChangeResult(PortChangeOutcome.RolledBack, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await DecideAsync(row, $"Couldn't put the original {container} back: Docker didn't answer ({ex.Message}). Both containers are kept.",
                $"{On(row.Kind, container)} didn't finish.");
        }
    }

    /// <summary>
    /// The owner's [Keep the new one]: the original stops for good and goes, never with its volumes, and the new container
    /// takes its place with the original's restart policy. The new one only starts once Docker says the original is down.
    /// A change whose containers were removed or replaced outside Gitberth just ends.
    /// </summary>
    public async Task<PortChangeResult> KeepNewAsync(string container, CancellationToken ct = default)
    {
        var none = CancellationToken.None;
        using var _ = await locks.AcquireAsync(container, ct);
        if (await PendingAsync(container, ct) is not { } row) return Nothing($"There's no change on {container} to finish.");
        try
        {
            var newId = row.NewId ?? await TempIdAsync(row);
            var made = newId is null ? null : await InspectAsync(newId);
            var original = await InspectAsync(row.OldId);
            if (await OverAsync(row, made, original) is { } over) return over;
            if (newId is null || made is null) return Nothing("The new container is gone, so the original is the one to keep.");

            string? error = null;
            if (original is not null)
            {
                // Down for good before the new one may run: the two mount the same volumes.
                error = await TryAsync(() => engine.StopAsync(row.OldId, StopOf(original, IsDatabase(original, container)).Seconds, none), goneIsFine: true);
                if (error is null && await InspectAsync(row.OldId) is { } now)
                {
                    if (ContainerPorts.Running(now)) error = "the original still runs";
                    else if (NameOf(now) == container) error = await TryAsync(() => engine.RenameAsync(row.OldId, row.OldName, none));
                }
            }
            if (error is null && NameOf(made) != container) error = await TryAsync(() => engine.RenameAsync(newId, container, none));
            // A new container only gets the original's restart policy once it's kept (and a failed rollback turned it off).
            if (error is null) error = await TryAsync(() => engine.UpdateRestartPolicyAsync(newId, NewPolicyOf(row), none));
            if (error is null && row.WasRunning && !ContainerPorts.Running(made)) error = await TryAsync(() => engine.StartAsync(newId, none));
            if (error is not null)
                return await DecideAsync(row, $"Couldn't keep the new {container}: {error}.", $"{On(row.Kind, container)} didn't finish.");

            var note = original is null ? ""
                : await TryAsync(() => engine.RemoveAsync(row.OldId, false, none), goneIsFine: true) is { } removeError
                    ? $" The original, {row.OldName}, couldn't be removed ({removeError})."
                    : " The original is removed (its volumes stay).";
            var text = $"{container} keeps the new container.{note}";
            await EndAsync(row, ChangeOutcome.Done, text, null);
            return new PortChangeResult(PortChangeOutcome.Done, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await DecideAsync(row, $"Couldn't keep the new {container}: Docker didn't answer ({ex.Message}).", $"{On(row.Kind, container)} didn't finish.");
        }
    }

    // A change whose containers were removed, or whose name went to someone else's container, outside Gitberth: there's
    // nothing left to keep or put back, so it ends, rather than wait for a choice that can't be made.
    private async Task<PortChangeResult?> OverAsync(ContainerChange row, JsonObject? made, JsonObject? original)
    {
        var name = row.ContainerName;
        string? text = null;
        if (made is null && original is null)
            text = $"Neither container of the {Noun(row.Kind)} on {name} is left, so there's nothing to keep or put back.";
        else if (await InspectAsync(name) is { } holder && Str(holder, "Id") is var id && id != row.OldId && id != Str(made, "Id"))
        {
            var left = new[] { made, original }.OfType<JsonObject>().Select(NameOf).ToList();
            text = $"{name} is another container now, so its {Noun(row.Kind)} is over."
                + (left.Count == 0 ? "" : $" {Join(left)} {(left.Count == 1 ? "is" : "are")} left from it; remove {(left.Count == 1 ? "it" : "them")} when you like.");
        }
        if (text is null) return null;
        await EndAsync(row, ChangeOutcome.NothingChanged, text, null);
        return Nothing(text);
    }

    private sealed record PutBack(string? Error, string? Note);

    /// <summary>
    /// The original back as it was: the new container loses its restart policy and stops (its ports and addresses free
    /// up), and only once Docker says it's down do the names go back and the original start again, under its own name (so
    /// its start, which AppSync and NetworkKeeper go by, is the app's); then its restart policy comes back, and the new
    /// container goes. When the original won't start, both stay; when Docker doesn't answer, nothing more is done. Works
    /// from any step, by ID.
    /// </summary>
    private async Task<PutBack> RestoreAsync(ContainerChange row, bool start)
    {
        try
        {
            return await RestoreCoreAsync(row, start);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PutBack($"wasn't put back, because Docker didn't answer ({ex.Message})", null);
        }
    }

    private async Task<PutBack> RestoreCoreAsync(ContainerChange row, bool start)
    {
        var none = CancellationToken.None;
        var newId = row.NewId ?? await TempIdAsync(row);
        var made = newId is null ? null : await InspectAsync(newId);
        if (newId is not null && made is not null)
        {
            // Nothing may start it again, and it must be down: they mount the same volumes. Docker's 404 means it's gone.
            if (await TryAsync(() => engine.UpdateRestartPolicyAsync(newId, NoRestart(), none), goneIsFine: true) is { } policyError)
                return new PutBack($"wasn't put back, because the new one's restart policy couldn't be turned off: {policyError}", null);
            if (await TryAsync(() => engine.StopAsync(newId, StopOf(made, IsDatabase(made, row.ContainerName)).Seconds, none), goneIsFine: true) is { } stopError)
                return new PutBack($"wasn't put back, because the new one didn't stop: {stopError}", null);
            made = await InspectAsync(newId);
            if (made is not null && ContainerPorts.Running(made)) return new PutBack("wasn't put back, because the new one still runs", null);
        }
        if (made is not null && NameOf(made) == row.ContainerName
            && await TryAsync(() => engine.RenameAsync(newId!, row.TempName, none)) is { } aside)
            return new PutBack($"couldn't take its name back: {Conflict(aside, row.TempName)}", null);
        if (await InspectAsync(row.OldId) is not { } original) return new PutBack("is gone", null);
        if (NameOf(original) != row.ContainerName && await TryAsync(() => engine.RenameAsync(row.OldId, row.ContainerName, none)) is { } back)
            return new PutBack($"couldn't take its name back: {Conflict(back, row.ContainerName)}", null);
        if (start)
        {
            var startError = await TryAsync(() => engine.StartAsync(row.OldId, none));
            if (startError is null && await StateAsync(row.OldId) is not { Running: true }) startError = "it stopped again";
            if (startError is not null) return new PutBack($"didn't start again: {startError}", null);
        }

        string? note = null;
        if (await TryAsync(() => engine.UpdateRestartPolicyAsync(row.OldId, PolicyOf(row), none)) is { } restoreError)
            note = $"Its restart policy couldn't be put back ({restoreError}).";
        if (made is not null && await TryAsync(() => engine.RemoveAsync(newId!, true, none), goneIsFine: true) is { } removeError)
            note = (note is null ? "" : note + " ") + $"The new container, {row.TempName}, couldn't be removed ({removeError}).";
        return new PutBack(null, note);
    }

    // Before anything stopped: the new container (never started) goes, and the original gets its policy back if it may
    // have lost it. The temporary name is looked up only when the create's answer may have been lost, never after Docker
    // refused the create: after a 409 that name is someone else's container.
    private async Task<string?> TidyAsync(ContainerChange row, bool byTempName)
    {
        var none = CancellationToken.None;
        string? note = null;
        var newId = row.NewId ?? (byTempName ? await TempIdAsync(row) : null);
        if (newId is not null && await TryAsync(() => engine.RemoveAsync(newId, true, none), goneIsFine: true) is { } removeError)
            note = $"The new container, {row.TempName}, couldn't be removed ({removeError}).";
        if (row.Step >= ChangeStep.Disarming
            && await TryAsync(() => engine.UpdateRestartPolicyAsync(row.OldId, PolicyOf(row), none), goneIsFine: true) is { } policyError)
            note = (note is null ? "" : note + " ") + $"Its restart policy couldn't be put back ({policyError}).";
        return note;
    }

    private async Task PutBackAfterRestartAsync(ContainerChange row, bool start)
    {
        // Written down first: if Gitberth restarts again, it goes on putting the original back.
        if (row.Step != ChangeStep.RollingBack) row = await SaveAsync(row with { Step = ChangeStep.RollingBack });
        var restored = await RestoreAsync(row, start);
        if (restored.Error is { } error)
        {
            await DecideAsync(row, $"Gitberth restarted while it recreated {row.ContainerName}, and the original ({await NameOrAsync(row.OldId, row.OldName)}) {error}.",
                $"{On(row.Kind, row.ContainerName)} was interrupted.");
            return;
        }
        var text = $"Gitberth restarted while it recreated {row.ContainerName}. It put the original back." + (restored.Note is { } note ? " " + note : "");
        await EndAsync(row, ChangeOutcome.RolledBack, text, null, text);
        logger.LogWarning("{Text}", text);
    }

    private async Task FinishAfterRestartAsync(ContainerChange row, string? notice)
    {
        var none = CancellationToken.None;
        // Someone may have started the original meanwhile: the two mount the same volumes, so the owner chooses.
        if (await BothRunAsync(row) is { } both)
        {
            await DecideAsync(row, both.Message, both.Lead);
            return;
        }
        var notes = new List<string>();
        // It passed: it gets the original's restart policy, which a new container only gets then.
        if (row.NewId is { } newId && await TryAsync(() => engine.UpdateRestartPolicyAsync(newId, NewPolicyOf(row), none)) is { } policyError)
            notes.Add(PolicyNote(row, policyError));
        if (await TryAsync(() => engine.RemoveAsync(row.OldId, false, none), goneIsFine: true) is { } removeError)
            notes.Add($"The original, {row.OldName}, couldn't be removed ({removeError}).");
        var text = (notice ?? $"{row.ContainerName} started {WithWhat(row.Kind)} and passed its checks.") + string.Concat(notes.Select(n => " " + n));
        await EndAsync(row, ChangeOutcome.Done, text, null, notice is null ? null : text);
        logger.LogInformation("{Text}", text);
    }

    // After a restart: running, no restart, no OOM kill, and up for 15 s or healthy, both counted from its own start as
    // the change's own checks count them: Gitberth may be back within seconds of the start, before a fault shows. Only
    // Docker's 404 means the new container is gone; when Docker doesn't answer, this throws, and the owner chooses.
    private async Task<string?> QuickCheckAsync(ContainerChange row)
    {
        if (row.NewId is null || await InspectAsync(row.NewId) is not { } made) return "the new container is gone";
        if (NameOf(made) != row.ContainerName) return $"the new container isn't called {row.ContainerName}";
        var window = HealthWindow(made);
        var until = (StateOf(made).StartedAt ?? _time.GetUtcNow()) + (window ?? StableFor);
        while (true)
        {
            var state = StateOf(made);
            if (state.OomKilled) return "it ran out of memory";
            if (!state.Running || state.Restarting || state.RestartCount > 0) return $"it stopped (exit code {state.ExitCode.ToString(CultureInfo.InvariantCulture)})";
            if (window is not null && state.Health == "healthy") return null;
            if (window is not null && state.Health == "unhealthy") return $"it reported unhealthy ({state.HealthOutput ?? "no output"})";
            if (_time.GetUtcNow() >= until) return window is null ? null : "it didn't report healthy";
            await Task.Delay(StatePoll, _time, CancellationToken.None);
            if (await InspectAsync(row.NewId) is not { } again) return "the new container is gone";
            made = again;
        }
    }

    /// <param name="Lead">The banner's first sentence.</param>
    private sealed record Clash(string Message, string Lead);

    // Both containers of the change run: they mount the same volumes, so it can neither go on nor wait quietly.
    private async Task<Clash?> BothRunAsync(ContainerChange row)
    {
        if (row.NewId is not { } newId) return null;
        if (await InspectAsync(row.OldId) is not { } original || !ContainerPorts.Running(original)) return null;
        if (await InspectAsync(newId) is not { } made || !ContainerPorts.Running(made)) return null;
        return new Clash(
            $"{NameOf(made)} and its original, {NameOf(original)}, both run, on the same volumes. Keep the new one or put the original back: the other one stops.",
            $"{row.ContainerName} and its original both run, on the same volumes.");
    }

    private async Task<PortChangeResult> DecideAsync(ContainerChange row, string message, string lead, string? output = null)
    {
        var notice = $"{lead} {await BothKeptAsync(row)}";
        var waiting = row with
        {
            Outcome = ChangeOutcome.NeedsDecision,
            Error = message,
            Notice = notice,
            FinishedAt = _time.GetUtcNow().UtcDateTime,
        };
        await journal.UpdateAsync(waiting, CancellationToken.None);
        // The job is over until the owner chooses: the row follows whichever container holds the name meanwhile.
        RaiseEnded(waiting);
        logger.LogWarning("{Notice}", notice);
        return new PortChangeResult(PortChangeOutcome.NeedsDecision, message) { LastOutput = output };
    }

    // "Both containers are kept: postgres (new, stopped) and postgres_old_0ebf31bbcd07 (the original)."
    private async Task<string> BothKeptAsync(ContainerChange row)
    {
        try
        {
            var newId = row.NewId ?? await TempIdAsync(row);
            var made = newId is null ? null : await InspectAsync(newId);
            var original = await InspectAsync(row.OldId);
            var kept = new List<string>();
            if (made is not null) kept.Add($"{NameOf(made)} (new, {(ContainerPorts.Running(made) ? "running" : "stopped")})");
            if (original is not null) kept.Add($"{NameOf(original)} (the original)");
            return kept.Count switch
            {
                2 => $"Both containers are kept: {kept[0]} and {kept[1]}.",
                1 => $"Only {kept[0]} is left.",
                _ => "Neither container is left.",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Docker didn't say: Gitberth removed neither, so both are there as far as it knows.
            return "Both containers are kept.";
        }
    }

    // The latest change of the container that isn't over: under way (only after a restart, since it holds the lock), waiting for a start, or for a decision.
    private async Task<ContainerChange?> PendingAsync(string container, CancellationToken ct) =>
        (await journal.UnfinishedAsync(ct)).LastOrDefault(r => r.ContainerName == container);

    // The change that isn't over which a container belongs to, by any of its names: postgres, postgres_old_…, postgres_new_….
    private async Task<ContainerChange?> RowOfAsync(string container, CancellationToken ct) =>
        (await journal.UnfinishedAsync(ct)).LastOrDefault(r => r.ContainerName == container || r.OldName == container || r.TempName == container);

    private async Task<ContainerChange?> ReadAsync(int id, CancellationToken ct) =>
        (await journal.UnfinishedAsync(ct)).FirstOrDefault(r => r.Id == id);

    // The new container when the journal didn't get its ID, because the create's answer was lost: the temporary name was
    // free when the change began, so a container with that name, made since, is the one it made.
    private async Task<string?> TempIdAsync(ContainerChange row) =>
        await InspectAsync(row.TempName) is { } temp && Str(temp, "Id") != row.OldId && MadeSince(temp, row.StartedAt) ? Str(temp, "Id") : null;

    // Created after the change began, with a minute's leeway for the clocks: an older one is someone else's.
    private static bool MadeSince(JsonObject inspect, DateTime startedAt) =>
        Time(inspect, "Created") is not { } created || created >= new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)) - TimeSpan.FromMinutes(1);

    /// <summary>
    /// The container's inspect, or null when Docker says there's no such container (404). Any other failure is tried again,
    /// then thrown: a container Docker didn't answer for isn't gone, and nothing may start next to one that may run.
    /// </summary>
    private async Task<JsonObject?> InspectAsync(string idOrName)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await engine.InspectContainerAsync(idOrName, CancellationToken.None);
            }
            catch (EngineException ex) when (ex.NotFound)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && attempt < InspectTries)
            {
                logger.LogInformation(ex, "Docker didn't answer an inspect of {Container}; trying again", idOrName);
                await Task.Delay(StatePoll, _time, CancellationToken.None);
            }
        }
    }

    // A container's name for a message, or the fallback when Docker doesn't say.
    private async Task<string> NameOrAsync(string id, string fallback)
    {
        try
        {
            return await engine.InspectContainerAsync(id, CancellationToken.None) is { } inspect ? NameOf(inspect) : fallback;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return fallback;
        }
    }

    private static string NameOf(JsonObject inspect) => Str(inspect, "Name").TrimStart('/');

    // The original's restart policy: a rollback and Put back give it back to the original.
    private static JsonObject PolicyOf(ContainerChange row) => Policy(row.OldRestartPolicy);

    // The new container's restart policy once it has passed (or is kept): the one the change sets, else the original's.
    private static JsonObject NewPolicyOf(ContainerChange row) => Policy(row.NewRestartPolicy ?? row.OldRestartPolicy);

    private static JsonObject Policy(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? NoRestart();
        }
        catch (JsonException)
        {
            return NoRestart();
        }
    }

    // "changed postgres's ports", "changed postgres's settings", "updated postgres", "rolled postgres back".
    private static string Changed(ChangeKind kind, string name) => kind switch
    {
        ChangeKind.Settings => $"changed {name}'s settings",
        ChangeKind.Update => $"updated {name}",
        ChangeKind.Rollback => $"rolled {name} back",
        _ => $"changed {name}'s ports",
    };

    // Docker writes 0001-01-01 for a container that never started.
    private static bool StartedSince(JsonObject inspect) => StateOf(inspect).StartedAt is not null;

    /// <param name="Restarting">Docker waits to restart it after an exit, by its restart policy: it counts as running meanwhile.</param>
    /// <param name="Ports">NetworkSettings.Ports: what it publishes right now.</param>
    private sealed record State(bool Running, bool Restarting, int ExitCode, int RestartCount, bool OomKilled, string? Health, string? HealthOutput,
        DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, JsonObject? Ports);

    /// <summary>The container's state; null when there's no such container. Docker's other errors are tried again, then thrown.</summary>
    private async Task<State?> StateAsync(string id) => await InspectAsync(id) is { } inspect ? StateOf(inspect) : null;

    // For a loop that looks again anyway: a read that fails is as good as no news.
    private async Task<State?> TryStateAsync(string id)
    {
        try
        {
            return await engine.InspectContainerAsync(id, CancellationToken.None) is { } inspect ? StateOf(inspect) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static State StateOf(JsonObject inspect)
    {
        var state = inspect["State"];
        var health = state?["Health"];
        // The last check's output, on one line.
        var output = (health?["Log"] as JsonArray)?.OfType<JsonObject>().LastOrDefault() is { } last
            ? Str(last, "Output").Trim().Split('\n').LastOrDefault()?.Trim()
            : null;
        return new State(
            state?["Running"]?.GetValueKind() == JsonValueKind.True,
            state?["Restarting"]?.GetValueKind() == JsonValueKind.True,
            (int)Long(state, "ExitCode"),
            (int)Long(inspect, "RestartCount"),
            state?["OOMKilled"]?.GetValueKind() == JsonValueKind.True,
            Str(health, "Status") is { Length: > 0 } status ? status : null,
            output is { Length: > 0 } ? (output.Length > 200 ? output[..200] + "…" : output) : null,
            Time(state, "StartedAt"),
            Time(state, "FinishedAt"),
            inspect["NetworkSettings"]?["Ports"] as JsonObject);
    }

    private static DateTimeOffset? Time(JsonNode? node, string name) =>
        DateTimeOffset.TryParse(Str(node, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at.Year > 1 ? at : null;
}
