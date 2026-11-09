using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Detection;
using Gitberth.Services;

namespace Gitberth.Network;

public sealed partial class PortChanger
{
    /// <summary>
    /// Makes the plan's change: preflight again on a fresh inspect, then create, turn off the restart policy, stop, swap
    /// the names, start, check, remove the old one. Every step that fails is undone. The token only counts during the
    /// preflight: once the new container exists, the change runs to its end. A plan of live fields is a docker update
    /// (<see cref="ContainerUpdater"/>), and a plan to create a container creates it (<see cref="PlanCreateAsync"/>).
    /// </summary>
    public async Task<PortChangeResult> ApplyAsync(PortChangePlan plan, IProgress<PortChangeStep>? progress = null, CancellationToken ct = default)
    {
        if (plan.Kind == ChangeKind.Create) return await ApplyCreateAsync(plan, progress, ct);
        if (plan.LiveOnly) return await ApplyLiveAsync(plan, progress, ct);

        var name = plan.Container;
        using var held = await locks.TryAcquireAsync(name);
        if (held is null) return Nothing(BusyText(name));

        Look look;
        PortChangePlan now;
        try
        {
            var request = plan.Request;
            look = await LookAsync(name, holdingLock: true, request is null ? ChangeScope.Ports : ScopeOf(FieldsOf(request), request.Kind), ct);
            now = await PlanAsync(look, request ?? PortsRequest(look, plan.Ports), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Nothing($"Nothing changed. {Unreadable(name, ex)}");
        }
        // The dialog may have been open a while: a container recreated since, or started since, isn't the one confirmed.
        if (look.Inspect is not { } inspect || now.ContainerId != plan.ContainerId || (now.Running && !plan.Running) || now.LiveOnly
            || now.Request is not { } wanted)
            return Nothing($"{name} changed since you started editing.");
        if (!now.CanApply)
            return Nothing(now.Blocked is { } blocked
                ? blocked.StartsWith("Port ", StringComparison.Ordinal) ? blocked + " Nothing changed." : blocked
                : plan.Kind == ChangeKind.Ports ? "Nothing changed: the ports are already like that." : "Nothing changed: it's already like that.");

        var kind = now.Kind;
        var fields = now.Fields;
        var target = SettingsDiff.Merge(ContainerSettingsJson.Read(inspect, look.Runs), wanted.Wanted, fields);
        JsonObject? newImage = null;
        try
        {
            if (fields.Contains(SettingField.Image)) newImage = await engine.InspectImageAsync(target.Image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Nothing($"Nothing changed. {Unreadable(name, ex)}");
        }
        if (fields.Contains(SettingField.Image) && (newImage is null || look.Runs is null
            || (wanted.ExpectedImageId is { } expected && Str(newImage, "Id") != expected)))
            return Nothing(newImage is null ? $"Nothing changed: {target.Image} isn't on this host any more."
                : look.Runs is null ? $"Nothing changed: Docker couldn't say what the image {name} runs supplies."
                : $"Nothing changed: {target.Image} moved again while it was pulled: check again.");
        // As the plan named it: an image asked for by its ID goes by its repository's digest.
        if (newImage is not null) target = Named(target, newImage, Str(inspect["Config"], "Image"));

        var oldId = Str(inspect, "Id");
        var temp = TempNameFor(name, oldId);
        var parked = OldNameFor(name, oldId);
        var stop = StopOf(inspect, IsDatabase(inspect, name));
        var lanTargets = LanTargets(now.Bindings, look.LanAddress);
        var current = ContainerPorts.Read(inspect);
        var portsChanged = fields.Contains(SettingField.Ports);
        var unheld = portsChanged ? Unheld(current, now.Bindings) : [];
        var probed = portsChanged ? Probeable(current, now.Bindings) : [];

        // The body is built here, from the fresh inspect, and never kept: its environment holds secrets. Only the changed
        // fields are written on the clone; a new image takes what the old one supplied out of it. The new container has no
        // restart policy until it passes its checks, so a crash shows at once (not as a restart loop), with its own exit
        // code, and nothing restarts it during a rollback. It gets its policy once it has passed.
        var body = BodyFor(inspect, look.Image, look.Runs, look.Networks, look.LanHostIp, target, fields, options.Network);
        if (newImage is not null && look.Runs is { } runs) ImageRebase.Apply(body, runs, newImage);
        ((JsonObject)body["HostConfig"]!)["RestartPolicy"] = NoRestart();
        var healthWindow = HealthWindowOf(body, newImage ?? look.Runs ?? look.Image);
        var healthUrl = HealthUrlOf(name, wanted.HealthPath, wanted.WebPort, body);
        var steps = new StepTracker(progress, _time, StepsFor(now.Running, unheld, stop, parked, healthWindow is not null, lanTargets, healthUrl));
        var scrub = ScrubberFor(EnvOf(inspect["Config"]).Concat(EnvOf(body)), wanted.SecretKeys);

        // 1. Preflight: both temporary names are free, and the probe binds the new host ports for a moment (only those
        // the old container's own sockets can't be in the way of).
        if (unheld.Count > 0) steps.Begin(StepKind.Ports);
        string? refused = null;
        var before = Before.Nothing;
        try
        {
            foreach (var taken in new[] { temp, parked })
                if (refused is null && await engine.InspectContainerAsync(taken, ct) is not null)
                    refused = $"Nothing changed: another container is already called {taken}.";
            if (refused is null && now.Running && probed.Count > 0
                && await probe.ProbeAsync(probed.Select(b => new ProbePort(b.HostIp, b.HostPort, b.Protocol)).ToList(), ct) is { } probeError)
                refused = $"{(PortIn(probeError, probed.Select(b => b.HostPort)) is { } port ? $"Port {port.ToString(CultureInfo.InvariantCulture)}" : "A new host port")} is taken ({PortTaken(probeError) ?? probeError}). Nothing changed.";
            if (refused is null && now.Running) before = await AnsweringAsync(name, inspect, now.Ports, look.LanAddress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            refused = $"Nothing changed. {Unreadable(name, ex)}";
        }
        if (refused is not null)
        {
            if (unheld.Count > 0) steps.Fail(StepKind.Ports);
            return Nothing(scrub(refused));
        }
        if (unheld.Count > 0) steps.Done(StepKind.Ports);
        ct.ThrowIfCancellationRequested();

        // From here on nothing is cancelled: a half-finished swap is worse than a finished one. The journal row comes
        // first, so a restart of Gitberth at any point after finds it. It holds names, IDs and policies, never a value.
        var policy = inspect["HostConfig"]?["RestartPolicy"] as JsonObject ?? new JsonObject { ["Name"] = "no" };
        ContainerChange row;
        try
        {
            row = await journal.AddAsync(new ContainerChange
            {
                ContainerName = name,
                Summary = now.Summary,
                OldId = oldId,
                OldName = parked,
                TempName = temp,
                WasRunning = now.Running,
                OldRestartPolicy = policy.ToJsonString(),
                Step = ChangeStep.Creating,
                StartedAt = _time.GetUtcNow().UtcDateTime,
                Kind = kind,
                Fields = string.Join(',', fields.Order()),
                NewRestartPolicy = fields.Contains(SettingField.RestartPolicy) ? ContainerSettingsJson.RestartJson(target.Restart).ToJsonString() : null,
                // An update keeps its tag, so what it ran before is told by the image's ID: Roll back needs it, even after a restart.
                ImageBefore = kind == ChangeKind.Update && Str(inspect, "Image") is { Length: > 0 } ran ? ran : ImageOf(inspect),
                ImageAfter = fields.Contains(SettingField.Image) ? target.Image : null,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without its row a restart of Gitberth couldn't finish or undo the change, so it doesn't begin.
            logger.LogError(ex, "Writing down the change on {Container} failed", name);
            return Nothing($"Nothing changed: Gitberth couldn't write the change down ({ex.Message}).");
        }
        var change = new Change
        {
            Name = name,
            Row = row,
            Stop = stop,
            Health = healthWindow,
            Tty = body["Tty"]?.GetValueKind() == JsonValueKind.True,
            Bindings = now.Bindings,
            LanTargets = lanTargets,
            LanAddress = look.LanAddress,
            Before = before,
            Steps = steps,
            Scrub = scrub,
            HealthUrl = healthUrl,
            WebPort = wanted.WebPort,
            HealthPath = wanted.HealthPath,
        };
        try
        {
            return await SwapAsync(change, body);
        }
        catch (Exception ex)
        {
            // Something unforeseen, or Docker stopped answering: what's done stays as the journal says, for the owner to choose.
            logger.LogError(ex, "{Change} stopped halfway", Doing(kind, name));
            return await DecideAsync(change.Row, scrub($"{Doing(kind, name)} stopped halfway: {ex.Message}"), $"{On(kind, name)} didn't finish.");
        }
    }

    // A docker update: the change of live fields, on the container as it is now, under its lock.
    private async Task<PortChangeResult> ApplyLiveAsync(PortChangePlan plan, IProgress<PortChangeStep>? progress, CancellationToken ct)
    {
        var name = plan.Container;
        using var held = await locks.TryAcquireAsync(name);
        if (held is null) return Nothing(BusyText(name));
        if (plan.Request is not { } request) return Nothing("Nothing changed: the plan doesn't say what to change.");
        Look look;
        PortChangePlan now;
        try
        {
            look = await LookAsync(name, holdingLock: true, ChangeScope.Settings, ct);
            now = await PlanAsync(look, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Nothing($"Nothing changed. {Unreadable(name, ex)}");
        }
        if (look.Inspect is not { } inspect || now.ContainerId != plan.ContainerId || !now.LiveOnly) return Nothing($"{name} changed since you started editing.");
        if (!now.CanApply) return Nothing(now.Blocked ?? "Nothing changed: it's already like that.");
        var target = SettingsDiff.Merge(ContainerSettingsJson.Read(inspect, look.Runs), request.Wanted, now.Fields);
        var steps = new StepTracker(progress, _time, [new StepText(StepKind.Live, "Updating its limits", "Updated its limits")]);
        steps.Begin(StepKind.Live);
        var result = await Updater.ApplyToAsync(name, target, now.Fields, CancellationToken.None);
        if (!result.Ok)
        {
            steps.Fail(StepKind.Live);
            return Nothing(result.Message.StartsWith("Nothing changed", StringComparison.Ordinal) ? result.Message : "Nothing changed. " + result.Message);
        }
        steps.Done(StepKind.Live);
        return new PortChangeResult(PortChangeOutcome.Done, result.Message);
    }

    // Steps 2 to 8. Each failure undoes what came before it.
    private async Task<PortChangeResult> SwapAsync(Change c, JsonObject body)
    {
        var none = CancellationToken.None;

        // 2. The new container next to the old one: a mount, network or address Docker refuses fails here, while nothing has stopped.
        c.Steps.Begin(StepKind.Create);
        try
        {
            c.NewId = await engine.CreateAsync(c.TempName, body, none);
        }
        catch (Exception ex)
        {
            c.Steps.Fail(StepKind.Create);
            // Docker's 4xx says no container was made; a 409 says another container took the temporary name since the
            // preflight, which isn't Gitberth's. Only a lost answer leaves a container of Gitberth's to look up by that name.
            var refusedIt = ex is EngineException { Status: >= 400 and < 500 };
            return await UndoBeforeStopAsync(c, ex is EngineException { Conflict: true }
                ? $"Nothing changed: another container is already called {c.TempName}."
                : $"Nothing changed: Docker refused the new container ({ex.Message}).", byTempName: !refusedIt);
        }
        await SaveAsync(c, r => r with { NewId = c.NewId, Step = ChangeStep.Disarming });
        c.Steps.Done(StepKind.Create);

        // 3. Neither Docker nor an "always" policy may start the old one next to the new one, on the same volumes.
        c.Steps.Begin(StepKind.Disarm);
        if (await TryAsync(() => engine.UpdateRestartPolicyAsync(c.OldId, NoRestart(), none)) is { } disarm)
        {
            c.Steps.Fail(StepKind.Disarm);
            return await UndoBeforeStopAsync(c, $"Nothing changed: Docker didn't turn off {c.Name}'s restart policy ({disarm}).", byTempName: false);
        }
        c.Steps.Done(StepKind.Disarm);

        // 4. It stops with its own signal; a database without its own timeout gets 60 s. The time is written down first:
        // Docker finishes a stop after its caller has gone, so a restart of Gitberth may find it still on its way down.
        if (c.WasRunning)
        {
            await SaveAsync(c, r => r with { Step = ChangeStep.Stopping, StopAt = _time.GetUtcNow().UtcDateTime });
            c.StopAt = _time.GetUtcNow();
            c.Steps.Begin(StepKind.Stop);
            if (await TryAsync(() => engine.StopAsync(c.OldId, c.Stop.Seconds, none)) is { } stopError)
            {
                c.Steps.Fail(StepKind.Stop);
                return await UndoStopAsync(c, stopError);
            }
            c.Steps.Done(StepKind.Stop);
        }

        // 5. The names swap before the start, so the new container comes up under its own name.
        await SaveAsync(c, r => r with { Step = ChangeStep.Renaming });
        c.Steps.Begin(StepKind.Swap);
        if (await TryAsync(() => engine.RenameAsync(c.OldId, c.OldName, none)) is { } aside)
        {
            c.Steps.Fail(StepKind.Swap);
            return await UndoAfterStopAsync(c, $"Couldn't rename {c.Name} to {c.OldName} ({Conflict(aside, c.OldName)}).");
        }
        if (await TryAsync(() => engine.RenameAsync(c.NewId!, c.Name, none)) is { } take)
        {
            c.Steps.Fail(StepKind.Swap);
            return await UndoAfterStopAsync(c, $"Couldn't give the new container the name {c.Name} ({Conflict(take, c.Name)}).");
        }
        c.Steps.Done(StepKind.Swap);

        // A stopped container stays stopped. Its original is kept until the new one's first start passes its checks.
        if (!c.WasRunning)
        {
            var takes = c.Row.Kind == ChangeKind.Ports ? c.Bindings.Select(b => b.HostPort).Distinct().ToList() : [];
            var waits = $"{c.Name} is recreated, still stopped{(takes.Count == 0 ? "" : $": it takes {PortWords(takes)} when it starts")}. "
                + $"The original stays, as {c.OldName}, until that start passes its checks.";
            await SaveAsync(c, r => r with { Step = ChangeStep.WaitingForStart, Error = waits });
            return new PortChangeResult(PortChangeOutcome.WaitsForStart, waits);
        }

        // 6. The new container starts. A port someone took since the probe shows up here.
        await SaveAsync(c, r => r with { Step = ChangeStep.Starting });
        c.Steps.Begin(StepKind.Start, NextAfterStart(c));
        if (await TryAsync(() => engine.StartAsync(c.NewId!, none)) is { } startError)
        {
            c.Steps.Fail(StepKind.Start);
            return await RollbackAsync(c, PortTaken(startError) is { } taken
                ? new Failure($"{c.Name} didn't start with port {Port(startError, c)} published ({taken}).", NameTheOld: true)
                : new Failure($"The new {c.Name} didn't start ({startError})."));
        }
        var startedAt = _time.GetUtcNow();
        c.Steps.Done(StepKind.Start);
        await SaveAsync(c, r => r with { Step = ChangeStep.Verifying });

        // 7. Checks.
        var verified = await VerifyAsync(c, startedAt, measure: true);
        if (verified.Failure is { } failure) return await RollbackAsync(c, failure);

        // 8. The old container goes, never with its volumes: the new one mounts them.
        return await FinishAsync(c, verified);
    }

    private sealed record Failure(string Text, bool NameTheOld = false, bool ShowOffline = true);

    /// <param name="Answering">The LAN addresses that answered after the change.</param>
    /// <param name="Silent">The container ports nothing answered on, before or after.</param>
    private sealed record Verified(Failure? Failure, DateTimeOffset? BackAt, IReadOnlyList<string> Answering, IReadOnlyList<int> Silent);

    /// <summary>
    /// The new container's checks: back online (timed, when <paramref name="measure"/>), up for 15 s or healthy, its health
    /// URL answering when it has one, every binding live, and each LAN port answering from the host. A LAN port that
    /// doesn't answer only fails the change when it answered before. Docker's answers are tried again before they count:
    /// one failed read doesn't fail a change.
    /// </summary>
    private async Task<Verified> VerifyAsync(Change c, DateTimeOffset startedAt, bool measure)
    {
        var none = CancellationToken.None;
        DateTimeOffset? back = null;
        if (measure)
        {
            // Offline time runs from the stop to the first connect that works, by name or on the LAN.
            var targets = c.Before.ByName ? c.Before.Answered.ToList()
                : c.Before.Answered.Where(p => c.Bindings.Any(b => b.ContainerPort == p && b.Protocol == "tcp")).ToList();
            if (targets.Count == 0) back = startedAt;
            // Staying up isn't listening: a slow starter (a JVM, a database's recovery) gets as long as a deploy gives it.
            var until = startedAt + (c.Health is { } window && window > ListenWait ? window : ListenWait);
            while (back is null && _time.GetUtcNow() < until)
            {
                if (await AnswersAgainAsync(c, targets)) back = _time.GetUtcNow();
                // Stopped, restarted or killed (the checks below say how), or past its health check either way: those
                // checks have the last word, so nothing waits out the whole window for a container that has failed.
                else if (await TryStateAsync(c.NewId!) is { Running: false } or { Restarting: true } or { RestartCount: > 0 } or { OomKilled: true }
                         or { Health: "healthy" or "unhealthy" }) break;
                else await Task.Delay(OnlinePoll, _time, none);
            }
        }

        c.Steps.Begin(StepKind.Settle);
        if (await SettleAsync(c, startedAt) is { } unsettled)
        {
            c.Steps.Fail(StepKind.Settle);
            return new Verified(unsettled, back, [], []);
        }
        c.Steps.Done(StepKind.Settle);

        // Its health URL answers, as a deploy checks it.
        if (c.HealthUrl is not null && health is { } probeHealth && c.WebPort is { } webPort && c.HealthPath is { } path)
        {
            c.Steps.Begin(StepKind.HealthUrl);
            if (await HealthUrlAsync(c, probeHealth, webPort, path, startedAt) is { } unanswered)
            {
                c.Steps.Fail(StepKind.HealthUrl);
                return new Verified(unanswered, back, [], []);
            }
            c.Steps.Done(StepKind.HealthUrl);
        }

        // Every binding is live, as Docker says: a read that fails is tried again, and only then fails the change.
        State? live;
        try
        {
            live = await StateAsync(c.NewId!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Verified(new Failure($"Docker didn't answer while the new {c.Name} was checked ({ex.Message})."), back, [], []);
        }
        if (live is null) return new Verified(new Failure($"The new {c.Name} disappeared."), back, [], []);
        foreach (var b in c.Bindings)
            if (live.Ports?[b.Key] is not JsonArray bound
                || !bound.OfType<JsonObject>().Any(x => Str(x, "HostPort") == b.HostPort.ToString(CultureInfo.InvariantCulture)))
                return new Verified(new Failure($"{c.Name} didn't start with port {b.HostPort.ToString(CultureInfo.InvariantCulture)} published.", NameTheOld: true), back, [], []);

        // Each LAN port answers from the host's own network, as it would from a PC.
        var answering = new List<string>();
        var silent = new List<int>();
        var unknown = new List<string>();
        if (c.LanTargets.Count > 0) c.Steps.Begin(StepKind.Lan);
        foreach (var (b, address) in c.LanTargets)
        {
            var host = ConnectHost(b.HostIp, c.LanAddress);
            var result = await lan.ConnectAsync(host, b.HostPort, none);
            // One that answered before gets a couple more tries before it counts as broken.
            for (var i = 0; i < 2 && !result.Answered && !result.Unknown && c.Before.Answered.Contains(b.ContainerPort); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, none);
                result = await lan.ConnectAsync(host, b.HostPort, none);
            }
            if (result.Unknown)
            {
                logger.LogInformation("Couldn't check {Address} from the host ({Reason}).", address, result.Error);
                unknown.Add(address);
            }
            else if (result.Answered)
            {
                answering.Add(address);
                back ??= _time.GetUtcNow();
            }
            else if (c.Before.Answered.Contains(b.ContainerPort))
            {
                c.Steps.Fail(StepKind.Lan, $"{address} doesn't answer");
                return new Verified(new Failure($"The new {c.Name} doesn't answer on {address} (checked from the host), though it answered before the change."), back, [], []);
            }
            else silent.Add(b.ContainerPort);
        }
        if (c.LanTargets.Count > 0)
            c.Steps.Done(StepKind.Lan,
                silent.Count > 0 ? $"Nothing answers on {Join(c.LanTargets.Where(t => silent.Contains(t.Binding.ContainerPort)).Select(t => t.Address).ToList())} yet"
                : unknown.Count > 0 ? $"Couldn't check {Join(unknown)} from the host"
                : null);
        return new Verified(null, back ?? (measure ? _time.GetUtcNow() : null), answering, silent.Distinct().ToList());
    }

    // It stays up for StableFor, or until its health check says healthy: no exit, no restart, no OOM kill. Each read of
    // Docker is tried again before it counts, so a passing hiccup doesn't fail a change that's working.
    private async Task<Failure?> SettleAsync(Change c, DateTimeOffset startedAt)
    {
        var until = startedAt + (c.Health ?? StableFor);
        while (true)
        {
            State? state;
            try
            {
                state = await StateAsync(c.NewId!);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new Failure($"Docker didn't answer while the new {c.Name} was checked ({ex.Message}).");
            }
            if (state is null) return new Failure($"The new {c.Name} disappeared.");
            if (state.OomKilled) return new Failure($"The new {c.Name} ran out of memory.", ShowOffline: false);
            if (!state.Running || state.Restarting || state.RestartCount > 0)
                return new Failure($"The new {c.Name} stopped after {RanFor(state, startedAt)} s (exit code {state.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
            if (c.Health is not null && state.Health == "healthy") return null;
            if (c.Health is not null && state.Health == "unhealthy")
                return new Failure($"The new {c.Name} reported unhealthy ({state.HealthOutput ?? "no output"}).", ShowOffline: false);
            if (_time.GetUtcNow() >= until)
                return c.Health is { } window
                    ? new Failure($"The new {c.Name} didn't report healthy within {window.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.")
                    : null;
            await Task.Delay(StatePoll, _time, CancellationToken.None);
        }
    }

    // The app's health URL, from inside the shared network, until it answers 2xx or 3xx or the window is over (as long as a
    // deploy gives it); a container that stops meanwhile fails at once.
    private async Task<Failure?> HealthUrlAsync(Change c, IHealthProbe probeHealth, int webPort, string path, DateTimeOffset startedAt)
    {
        var until = (_time.GetUtcNow() > startedAt ? _time.GetUtcNow() : startedAt) + ListenWait;
        string? last = null;
        while (true)
        {
            last = await probeHealth.CheckAsync(c.Name, webPort, path, CancellationToken.None);
            if (last is null) return null;
            if (await TryStateAsync(c.NewId!) is { Running: false } state)
                return new Failure($"The new {c.Name} stopped while its health URL was checked (exit code {state.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
            if (_time.GetUtcNow() >= until) return new Failure($"The new {c.Name} didn't answer its health URL: {last}.");
            await Task.Delay(HealthPoll, _time, CancellationToken.None);
        }
    }

    private async Task<PortChangeResult> FinishAsync(Change c, Verified v)
    {
        var none = CancellationToken.None;
        await SaveAsync(c, r => r with { Step = ChangeStep.RemovingOld });
        // Someone may have started the original meanwhile: the two mount the same volumes, so the owner chooses.
        if (await BothRunAsync(c.Row) is { } both) return await DecideAsync(c.Row, c.Scrub(both.Message), both.Lead);
        c.Steps.Begin(StepKind.RemoveOld);
        // It passed: now it may have its restart policy, the new one when the change sets one, else the original's.
        var policyError = await TryAsync(() => engine.UpdateRestartPolicyAsync(c.NewId!, NewPolicyOf(c.Row), none));
        var removeError = await TryAsync(() => engine.RemoveAsync(c.OldId, false, none), goneIsFine: true);
        if (removeError is null) c.Steps.Done(StepKind.RemoveOld);
        else c.Steps.Fail(StepKind.RemoveOld);

        var offline = c.StopAt is { } from ? (v.BackAt ?? _time.GetUtcNow()) - from : (TimeSpan?)null;
        var message = v.Answering.Count > 0 ? $"{c.Name} answers on {Join(v.Answering)} (checked from the host). It was offline for {Seconds(offline)}."
            : v.Silent.Count > 0 && c.Row.Kind == ChangeKind.Ports ? $"Published. Nothing answers on {Join(v.Silent.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList())} yet: it may listen only inside its container, or on another port."
            : $"{c.Name} is back {WithWhat(c.Row.Kind)}. It was offline for {Seconds(offline)}.";
        if (policyError is not null) message += " " + PolicyNote(c.Row, policyError);
        if (removeError is not null)
            message += $" The old container, {c.OldName}, couldn't be removed ({removeError}); remove it when you like: its volumes stay either way.";
        await EndAsync(c.Row, ChangeOutcome.Done, c.Scrub(message), offline);
        return new PortChangeResult(PortChangeOutcome.Done, c.Scrub(message)) { Offline = offline, NothingAnswers = v.Answering.Count == 0 && v.Silent.Count > 0 };
    }

    // "Its restart policy couldn't be set to unless-stopped (…); set it when you like."
    private static string PolicyNote(ContainerChange row, string error) =>
        $"Its restart policy couldn't be set to {Str(NewPolicyOf(row), "Name")} ({error}); set it when you like.";

    // After a failure in steps 6 and 7: the original comes back as it was, and the new container goes.
    private async Task<PortChangeResult> RollbackAsync(Change c, Failure failure)
    {
        c.Steps.Add(new StepText(StepKind.PutBack, "Putting the old container back", "Put the old container back"));
        c.Steps.Begin(StepKind.PutBack);
        // Written down first: a restart of Gitberth from here on goes on putting the original back, and never finishes a
        // change that failed its checks, however well the new container runs.
        await SaveAsync(c, r => r with { Step = ChangeStep.RollingBack, Error = c.Scrub(failure.Text) });
        var output = await LastOutputAsync(c.NewId!, c.Tty);
        var restored = await RestoreAsync(c.Row, start: true);
        if (restored.Error is { } error)
        {
            c.Steps.Fail(StepKind.PutBack);
            var both = $"The new {c.Name} failed, and the old one ({await NameOrAsync(c.OldId, c.OldName)}) {error}. Nothing was deleted.";
            return await DecideAsync(c.Row, c.Scrub(both), $"{On(c.Row.Kind, c.Name)} didn't finish.", output is null ? null : c.Scrub(output));
        }
        var offline = await BackOnlineAsync(c);
        c.Steps.Done(StepKind.PutBack);
        var text = failure.Text + (failure.NameTheOld ? $" The old {c.Name} is back as it was" : " The old one is back as it was")
            + (failure.ShowOffline && offline is { } o ? $"; offline {Seconds(o)} in all." : ".")
            + (restored.Note is { } note ? " " + note : "");
        await EndAsync(c.Row, ChangeOutcome.RolledBack, c.Scrub(text), offline);
        return new PortChangeResult(PortChangeOutcome.RolledBack, c.Scrub(text)) { Offline = offline, LastOutput = output is null ? null : c.Scrub(output) };
    }

    // After a failure in steps 2 and 3: nothing has stopped. The new container goes and the policy comes back.
    private async Task<PortChangeResult> UndoBeforeStopAsync(Change c, string message, bool byTempName)
    {
        var note = await TidyAsync(c.Row, byTempName);
        var text = c.Scrub(message + (note is null ? "" : " " + note));
        await EndAsync(c.Row, ChangeOutcome.NothingChanged, text, null);
        return Nothing(text);
    }

    // The stop failed or didn't answer in time: the original may still run, may still be stopping, or may have stopped
    // after all. It gets its own timeout once more to finish, so a container halfway down isn't taken for a running one.
    private async Task<PortChangeResult> UndoStopAsync(Change c, string stopError)
    {
        if (await StateAsync(c.OldId) is { Running: true }) await WaitStoppedAsync(c.OldId, c.Stop.Window);
        if (await StateAsync(c.OldId) is { Running: true })
        {
            var note = await TidyAsync(c.Row, byTempName: false);
            var text = c.Scrub($"Couldn't stop {c.Name} ({stopError}), so nothing changed: it runs as before." + (note is null ? "" : " " + note));
            await EndAsync(c.Row, ChangeOutcome.NothingChanged, text, null);
            return Nothing(text);
        }
        return await UndoAfterStopAsync(c, $"Couldn't stop {c.Name} cleanly ({stopError}).");
    }

    // After a failure in steps 4 and 5: the new container never started. The original takes its name back and starts again.
    private async Task<PortChangeResult> UndoAfterStopAsync(Change c, string message)
    {
        await SaveAsync(c, r => r with { Step = ChangeStep.RollingBack, Error = c.Scrub(message) });
        var restored = await RestoreAsync(c.Row, start: c.WasRunning);
        if (restored.Error is { } error)
            return await DecideAsync(c.Row, c.Scrub($"{message} The old {c.Name} ({await NameOrAsync(c.OldId, c.OldName)}) {error}. Nothing was deleted."),
                $"{On(c.Row.Kind, c.Name)} didn't finish.");
        var offline = c.WasRunning ? await BackOnlineAsync(c) : null;
        var text = c.Scrub(message + $" {c.Name} is back as it was" + (offline is { } o ? $"; offline {Seconds(o)} in all." : ".")
            + (restored.Note is { } note ? " " + note : ""));
        await EndAsync(c.Row, ChangeOutcome.RolledBack, text, offline);
        return new PortChangeResult(PortChangeOutcome.RolledBack, text) { Offline = offline };
    }

    // Until the container isn't running, or the time is up; whichever comes first.
    private async Task WaitStoppedAsync(string id, TimeSpan max)
    {
        using var stop = new CancellationTokenSource();
        var waited = engine.WaitAsync(id, stop.Token);
        await Task.WhenAny(waited, Task.Delay(max, _time, stop.Token));
        await stop.CancelAsync();
        _ = waited.ContinueWith(t => t.Exception, TaskScheduler.Default);
    }

    /// <summary>Which ports answered before the change, and whether that was by name (else from the host).</summary>
    private sealed record Before(bool ByName, IReadOnlySet<int> Answered)
    {
        public static readonly Before Nothing = new(false, new HashSet<int>());
    }

    private async Task<Before> AnsweringAsync(string name, JsonObject inspect, IReadOnlyList<PortRule> rules, string lanAddress, CancellationToken ct)
    {
        var current = ContainerPorts.Read(inspect);
        var tcp = current.Where(p => p.Protocol == "tcp").Select(p => p.ContainerPort)
            .Concat(rules.Where(r => r.Protocol == "tcp").Select(r => r.ContainerPort))
            .Distinct().Take(8).ToList();
        var answered = new HashSet<int>();
        var byName = tcp.Count > 0;
        foreach (var port in tcp)
        {
            var answers = await names.AnswersAsync(name, port, ct);
            if (answers is null)
            {
                byName = false;
                break;
            }
            if (answers == true) answered.Add(port);
        }
        if (byName) return new Before(true, answered);

        // Gitberth can't reach it by name: what it publishes now is tried from the host instead.
        answered.Clear();
        foreach (var port in current.Where(p => p.Protocol == "tcp"))
            foreach (var b in port.Bindings.Where(b => b.HostPort is not null))
                if (!answered.Contains(port.ContainerPort) && (await lan.ConnectAsync(ConnectHost(b.HostIp, lanAddress), b.HostPort!.Value, ct)).Answered)
                    answered.Add(port.ContainerPort);
        return new Before(false, answered);
    }

    private async Task<bool> AnswersAgainAsync(Change c, IReadOnlyList<int> ports)
    {
        foreach (var port in ports)
        {
            if (c.Before.ByName)
            {
                if (await names.AnswersAsync(c.Name, port, CancellationToken.None) == true) return true;
                continue;
            }
            foreach (var b in c.Bindings.Where(b => b.ContainerPort == port && b.Protocol == "tcp"))
                if ((await lan.ConnectAsync(ConnectHost(b.HostIp, c.LanAddress), b.HostPort, CancellationToken.None)).Answered) return true;
        }
        return false;
    }

    // After a rollback: until the original answers by name again, when it did before, for the time offline in all.
    private async Task<TimeSpan?> BackOnlineAsync(Change c)
    {
        if (c.StopAt is not { } from) return null;
        if (c.Before.ByName && c.Before.Answered.Count > 0)
        {
            var until = _time.GetUtcNow() + StableFor;
            while (_time.GetUtcNow() < until)
            {
                if (await AnswersAgainAsync(c, c.Before.Answered.ToList())) break;
                await Task.Delay(OnlinePoll, _time, CancellationToken.None);
            }
        }
        return _time.GetUtcNow() - from;
    }

    private string NextAfterStart(Change c)
    {
        var settle = c.Health is null ? $"it stays up for {StableFor.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s" : "it must report healthy";
        var then = c.LanTargets.Select(t => t.Address).Prepend(c.HealthUrl).OfType<string>().ToList();
        return then.Count == 0 ? $"Next: {settle}." : $"Next: {settle}, then {Join(then)} must answer.";
    }

    private enum StepKind { Ports, Create, Disarm, Stop, Swap, Start, Settle, HealthUrl, Lan, RemoveOld, PutBack, Live, Policy }

    private sealed record StepText(StepKind Kind, string Running, string Done);

    /// <summary>
    /// The steps of a change, in the progress's words: what it says while a step runs, and once it's done. They speak of
    /// the old and the new container, never by the container's name, which an app's dialog doesn't call it by.
    /// </summary>
    private List<StepText> StepsFor(bool running, IReadOnlyList<NewBinding> unheld, StopWith stop, string oldName, bool health,
        IReadOnlyList<(NewBinding Binding, string Address)> lanTargets, string? healthUrl = null)
    {
        var steps = new List<StepText>();
        if (unheld.Count > 0)
        {
            var ports = unheld.Select(b => b.HostPort.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
            var one = ports.Count == 1;
            steps.Add(new(StepKind.Ports, $"Checking host {(one ? "port" : "ports")} {Join(ports)}", $"Host {(one ? "port" : "ports")} {Join(ports)} {(one ? "is" : "are")} free"));
        }
        steps.Add(new(StepKind.Create, "Creating the new container", "Created the new container"));
        steps.Add(new(StepKind.Disarm, "Turning off the old container's restart policy", "Turned off the old container's restart policy"));
        var waits = $"({stop.Signal}, {stop.Words})";
        if (running) steps.Add(new(StepKind.Stop, $"Stopping the old container {waits}", $"Stopped the old container {waits}"));
        steps.Add(new(StepKind.Swap, "Swapping the names", $"Swapped the names: the old one is {oldName}"));
        if (!running) return steps;
        steps.Add(new(StepKind.Start, "Starting the new container", "Started the new container"));
        AddChecks(steps, health, healthUrl, lanTargets);
        steps.Add(new(StepKind.RemoveOld, "Removing the old container", "Removed the old container (its volumes stay)"));
        return steps;
    }

    // Settle, the health URL and the LAN: the checks a new container passes.
    private void AddChecks(List<StepText> steps, bool health, string? healthUrl, IReadOnlyList<(NewBinding Binding, string Address)> lanTargets)
    {
        var stable = StableFor.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        steps.Add(health
            ? new(StepKind.Settle, "Waiting for it to report healthy", "It reports healthy")
            : new(StepKind.Settle, $"Checking that it stays up for {stable} s", $"It stayed up for {stable} s"));
        if (healthUrl is not null) steps.Add(new(StepKind.HealthUrl, $"Checking {healthUrl}", $"{healthUrl} answers"));
        if (lanTargets.Count > 0)
        {
            var addresses = Join(lanTargets.Select(t => t.Address).ToList());
            steps.Add(new(StepKind.Lan, $"Checking {addresses}", $"{addresses} {(lanTargets.Count == 1 ? "answers" : "answer")}"));
        }
    }

    /// <summary>Reports each step as it starts and ends, with how long it took.</summary>
    private sealed class StepTracker(IProgress<PortChangeStep>? progress, TimeProvider time, List<StepText> steps)
    {
        private DateTimeOffset _since;

        public void Begin(StepKind kind, string? next = null)
        {
            _since = time.GetUtcNow();
            Report(kind, StepState.Running, null, null, next);
        }

        public void Done(StepKind kind, string? text = null) => Report(kind, StepState.Done, time.GetUtcNow() - _since, text, null);

        public void Fail(StepKind kind, string? text = null) => Report(kind, StepState.Failed, time.GetUtcNow() - _since, text, null);

        public void Add(StepText step)
        {
            if (steps.All(s => s.Kind != step.Kind)) steps.Add(step);
        }

        private void Report(StepKind kind, StepState state, TimeSpan? took, string? text, string? next)
        {
            var at = steps.FindIndex(s => s.Kind == kind);
            if (at < 0) return;
            progress?.Report(new PortChangeStep(at + 1, steps.Count, text ?? (state == StepState.Done ? steps[at].Done : steps[at].Running), state, took, next));
        }
    }

    /// <summary>One change as it runs.</summary>
    private sealed class Change
    {
        public required string Name { get; init; }
        public required ContainerChange Row { get; set; }
        public required StopWith Stop { get; init; }
        public required TimeSpan? Health { get; init; }
        public required bool Tty { get; init; }
        public required IReadOnlyList<NewBinding> Bindings { get; init; }
        public required IReadOnlyList<(NewBinding Binding, string Address)> LanTargets { get; init; }
        public required string LanAddress { get; init; }
        public required Before Before { get; init; }
        public required StepTracker Steps { get; init; }
        public required Func<string, string> Scrub { get; init; }
        /// <summary>The app's health URL as the steps name it, checked after the start; null for none.</summary>
        public string? HealthUrl { get; init; }
        public int? WebPort { get; init; }
        public string? HealthPath { get; init; }
        public string? NewId { get; set; }
        public DateTimeOffset? StopAt { get; set; }

        public string OldId => Row.OldId;
        public string OldName => Row.OldName;
        public string TempName => Row.TempName;
        public bool WasRunning => Row.WasRunning;
    }

    private async Task SaveAsync(Change c, Func<ContainerChange, ContainerChange> edit) => c.Row = await SaveAsync(edit(c.Row));

    private async Task<ContainerChange> SaveAsync(ContainerChange row)
    {
        await journal.UpdateAsync(row, CancellationToken.None);
        return row;
    }

    private async Task EndAsync(ContainerChange row, ChangeOutcome outcome, string message, TimeSpan? offline, string? notice = null)
    {
        var ended = row with
        {
            Outcome = outcome,
            Step = outcome == ChangeOutcome.Running ? row.Step : ChangeStep.Finished,
            Error = outcome == ChangeOutcome.Done ? null : message,
            OfflineMs = offline is { } o ? (int)Math.Round(o.TotalMilliseconds) : row.OfflineMs,
            FinishedAt = _time.GetUtcNow().UtcDateTime,
            Notice = notice,
        };
        await journal.UpdateAsync(ended, CancellationToken.None);
        RaiseEnded(ended);
    }

    // The app's row follows whatever holds the name now (AppSync): only a record, so a failure changes nothing here.
    private void RaiseEnded(ContainerChange ended)
    {
        try
        {
            Ended?.Invoke(ended);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Following the end of the change on {Container} failed", ended.ContainerName);
        }
    }

    private static PortChangeResult Nothing(string message) => new(PortChangeOutcome.NothingChanged, message);

    // A rename's 409 in the owner's words.
    private static string Conflict(string error, string name) =>
        error.Contains("already in use", StringComparison.OrdinalIgnoreCase) || error.Contains("Conflict", StringComparison.Ordinal)
            ? $"another container is already called {name}"
            : error;

    private static string Port(string message, Change c) =>
        (PortIn(message, c.Bindings.Select(b => b.HostPort)) ?? c.Bindings.Select(b => b.HostPort).FirstOrDefault()).ToString(CultureInfo.InvariantCulture);

    private static string ConnectHost(string hostIp, string lanAddress) =>
        ContainerPorts.AccessOf(hostIp) == PortAccess.Lan ? lanAddress : hostIp;

    private static string Seconds(TimeSpan? span) => (span ?? TimeSpan.Zero).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private string RanFor(State state, DateTimeOffset startedAt)
    {
        var ran = state.StartedAt is { } s && state.FinishedAt is { } f && f > s ? f - s : _time.GetUtcNow() - startedAt;
        return Math.Max(1, (int)Math.Round(ran.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    private static JsonObject NoRestart() => new() { ["Name"] = "no" };

    // The image the container ran, for the journal: its reference, or its ID when it has no tag.
    private static string ImageOf(JsonObject inspect) => Str(inspect["Config"], "Image") is { Length: > 0 } reference ? reference : Str(inspect, "Image");

    // The KEY=value words of an Env list: an inspect's Config, or a create body.
    private static IEnumerable<string> EnvOf(JsonNode? config) => ContainerSettingsJson.StrList(config?["Env"]) ?? [];

    /// <summary>Hides the container's secret settings in a message that's kept or logged.</summary>
    private static Func<string, string> ScrubberFor(JsonObject inspect) => ScrubberFor(EnvOf(inspect["Config"]), []);

    /// <summary>
    /// Hides secrets in a message that's kept or logged, by the one rule (<see cref="Security.Masking.Scrubber"/>): the
    /// values of the variables given (the old container's and the new one's) that are secret, and any secret in the text.
    /// </summary>
    private static Func<string, string> ScrubberFor(IEnumerable<string> env, IReadOnlyCollection<string> secretKeys) =>
        Security.Masking.Scrubber(env.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).Select(kv => KeyValuePair.Create(kv[0], kv[1])), secretKeys);

    /// <summary>
    /// Null when the call worked; else Docker's message, or why no answer came. With <paramref name="goneIsFine"/>, Docker's
    /// 404 counts as done: a container that's gone needs no stopping or removing.
    /// </summary>
    private static async Task<string?> TryAsync(Func<Task> call, bool goneIsFine = false)
    {
        try
        {
            await call();
            return null;
        }
        catch (EngineException ex) when (goneIsFine && ex.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.Message;
        }
    }

    private async Task<string?> LastOutputAsync(string id, bool tty)
    {
        try
        {
            var text = LogStream.Text(await engine.LogsAsync(id, LogLines, CancellationToken.None), tty).TrimEnd();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
