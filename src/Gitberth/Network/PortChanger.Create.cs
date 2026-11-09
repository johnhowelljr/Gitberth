using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Services;

namespace Gitberth.Network;

public sealed partial class PortChanger
{
    /// <summary>
    /// What creating a new container would do (2.3), checked as a change is: the name free, the image on the host, the
    /// networks there, the settings and ports sound, and the host ports free on the host-port map (the probe binds them at
    /// apply). It changes nothing. Creating writes no journal row: there's no original to put back. A container that fails
    /// its first start or its checks is removed, so nothing is left in Docker (its volumes always stay).
    /// </summary>
    public async Task<PortChangePlan> PlanCreateAsync(NewContainerRequest request, CancellationToken ct = default)
    {
        try
        {
            return (await CreateLookAsync(request, ct)).Plan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = Unreadable(request.Name, ex);
            return new PortChangePlan { Container = request.Name, Kind = ChangeKind.Create, Create = request, Verdict = new RecreateVerdict(RecreateKind.Missing, why), Blocked = why };
        }
    }

    /// <param name="GatewayPriority">The engine takes gateway priorities (API 1.48).</param>
    private sealed record CreateLook(PortChangePlan Plan, string LanAddress, string LanHostIp, bool GatewayPriority, JsonObject? Image);

    private async Task<CreateLook> CreateLookAsync(NewContainerRequest request, CancellationToken ct)
    {
        var name = request.Name;
        var s = request.Settings;
        var existing = await engine.InspectContainerAsync(name, ct);
        var version = await engine.VersionAsync(ct);
        var networks = await engine.ListNetworksAsync(ct);
        var lanAddress = await hostPorts.LanAddressAsync(ct);
        var lanHostIp = await hostPorts.LanHostIpAsync(ct);
        var image = s.Image.Length == 0 ? null : await engine.InspectImageAsync(s.Image, ct);
        var host = await OptionalAsync(() => engine.HostAsync(ct), null, "the host's CPUs and memory");

        var rules = s.Ports.Select(r => r with { Protocol = r.Protocol.ToLowerInvariant() }).Distinct().ToList();
        var ownMode = ContainerSettingsJson.IsOwnMode(s.NetworkMode);
        var bindings = ownMode ? [] : rules.Where(r => r.Published && r.HostPort is not null)
            .Select(r => new NewBinding(r.ContainerPort, r.Protocol, AddressFor(r, lanHostIp), r.HostPort!.Value)).Distinct().ToList();
        var names = CloneSpec.NetworkNames(networks);
        var problem = existing is not null ? $"There's already a container called {name}."
            : !version.AtLeast(1, 44) ? $"Creating containers needs Docker 25 or later (API 1.44). This host has Docker {version.Version} (API {version.ApiVersion})."
            : SettingsRules.Check(s, new SettingsContext(host?.Cpus ?? 0, host?.MemoryBytes ?? 0, names, GitApp: false)).Select(p => p.Message).FirstOrDefault()
              ?? rules.Select(r => r.Problem()).FirstOrDefault(p => p is not null)
              ?? Twice(bindings)
              ?? (ownMode ? null : request.Networks.Select(n => n.Name).FirstOrDefault(n => !names.Contains(n)) is { } missing ? $"There's no network called {missing}." : null)
              ?? (image is null ? $"{(s.Image.Length == 0 ? "The image" : s.Image)} isn't on this host yet." : null);
        if (problem is null)
            foreach (var b in bindings)
                if (await hostPorts.HolderAsync(name, b.HostPort, b.Protocol, b.HostIp, ct) is { } holder)
                {
                    problem = $"Port {b.HostPort.ToString(CultureInfo.InvariantCulture)} is taken by {holder}.";
                    break;
                }

        // Its volumes: none a running database writes (or, for a database, none anything running writes), and a volume
        // left by an app that was here before is named, since it holds that app's data.
        var database = ContainerService.IsDatabase(s.Image, image?["Config"]);
        var volumes = s.Mounts.Where(m => m.Kind == MountKind.Volume && m.Source.Length > 0).ToList();
        if (problem is null && volumes.Count > 0)
            problem = SharedVolumes(name, "Creating", database, volumes.Select(m => (m.Source, m.ReadOnly)), await OthersAsync("", ct));
        var notes = new List<string>();
        if (problem is null)
            foreach (var volume in volumes.Select(m => m.Source).Distinct(StringComparer.Ordinal).Where(v => v.StartsWith("gb-", StringComparison.Ordinal)))
            {
                if (await OptionalAsync(() => engine.InspectVolumeAsync(volume, ct), null, "its volumes") is not { } left) continue;
                var made = DateTimeOffset.TryParse(Str(left, "CreatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? $" (made {Ago(at)})" : "";
                if (database && !request.UseExistingData)
                {
                    problem = $"The volume {volume}{made} is left from an app that was here before, and holds its data: a database may not be able to read it "
                        + "(another major version, say). Choose to use the existing data, or give the app another name or volume.";
                    break;
                }
                notes.Add($"The volume {volume}{made} is left from an app that was here before: {name} uses it as it is, with its data.");
            }

        var body = ContainerSettingsJson.NewBody(s, request.Networks, lanHostIp, version.AtLeast(1, 48));
        var healthWindow = HealthWindowOf(body, image);
        var lanTargets = LanTargets(bindings, lanAddress);
        var healthUrl = HealthUrlOf(name, request.HealthPath, request.WebPort, body);
        if (request.Start && UncheckedHealthNote(name, request.HealthPath, request.WebPort, body) is { } healthNote) notes.Add(healthNote);
        var reached = lanTargets.Select(t => t.Address).ToList();
        var plan = new PortChangePlan
        {
            Container = name,
            Kind = ChangeKind.Create,
            Create = request,
            Verdict = RecreateVerdict.Yes,
            Blocked = problem,
            Ports = rules,
            Bindings = bindings,
            Running = false,
            Summary = $"created from {s.Image}",
            Keeps = "",
            AlsoChanges = "",
            Impact = [request.Start
                ? $"Creates {name} and starts it." + (reached.Count == 0 ? "" : $" Reached at {Join(reached)}.")
                : $"Creates {name}, not started.", .. notes],
            Footer = request.Start ? "If it fails its first start or its checks, it's removed: nothing is left in Docker but its volumes." : "",
            Steps = CreateSteps(name, request.Start, bindings, healthWindow is not null, lanTargets, healthUrl).Select(st => st.Done).ToList(),
        };
        return new CreateLook(plan, lanAddress, lanHostIp, version.AtLeast(1, 48), image);
    }

    /// <summary>
    /// Why a new container (a create, or a recreate whose volumes or image change) mustn't mount these volumes: a running
    /// database writes one of them, or the new container is a database and a running container writes one. Two servers
    /// writing the same data files at once can damage them. A volume it mounts read-only (a backup) doesn't count.
    /// </summary>
    /// <param name="doing">"Creating" or "Recreating": the sentence's verb.</param>
    /// <param name="others">Every other container's inspect; only running ones count.</param>
    internal static string? SharedVolumes(string name, string doing, bool database, IEnumerable<(string Volume, bool ReadOnly)> volumes, IEnumerable<JsonObject> others)
    {
        var mine = volumes.Where(v => !v.ReadOnly && v.Volume.Length > 0).Select(v => v.Volume).ToHashSet(StringComparer.Ordinal);
        if (mine.Count == 0) return null;
        foreach (var other in others.Where(ContainerPorts.Running))
        {
            var otherDatabase = ContainerService.IsDatabase(Str(other["Config"], "Image"), other["Config"]);
            if (!otherDatabase && !database) continue;
            var shared = (other["Mounts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(m => Str(m, "Type") == "volume" && m["RW"]?.GetValueKind() != JsonValueKind.False)
                .Select(m => Str(m, "Name")).FirstOrDefault(mine.Contains);
            if (shared is null) continue;
            var otherName = Str(other, "Name").TrimStart('/');
            return $"{otherName} runs and writes the volume {shared}. {doing} {name} on it as well would put "
                + (otherDatabase && database ? "two database servers" : otherDatabase ? "a second program next to its database server" : "a database server next to it")
                + $" on the same files, which can damage them. Stop {otherName} first, or give {name} a volume of its own.";
        }
        return null;
    }

    // "3 days ago", for a volume's age.
    private string Ago(DateTimeOffset at)
    {
        var ago = _time.GetUtcNow() - at;
        return ago < TimeSpan.FromHours(1) ? "less than an hour ago"
            : ago < TimeSpan.FromDays(2) ? $"{((int)ago.TotalHours).ToString(CultureInfo.InvariantCulture)} h ago"
            : $"{((int)ago.TotalDays).ToString(CultureInfo.InvariantCulture)} days ago";
    }

    // A rule's own address while it belongs to its access, else its access's: the host's LAN choice, or 127.0.0.1.
    private static string AddressFor(PortRule rule, string lanHostIp) =>
        rule.HostIp is { } own && (rule.Access == PortAccess.Custom || ContainerPorts.AccessOf(own) == rule.Access) ? own
        : rule.Access == PortAccess.Lan ? lanHostIp
        : ContainerPorts.BindAddress(rule.Access, rule.HostIp) ?? "";

    private List<StepText> CreateSteps(string name, bool start, IReadOnlyList<NewBinding> bindings, bool health,
        IReadOnlyList<(NewBinding Binding, string Address)> lanTargets, string? healthUrl)
    {
        var steps = new List<StepText>();
        if (start && bindings.Count > 0)
        {
            var ports = bindings.Select(b => b.HostPort.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
            var one = ports.Count == 1;
            steps.Add(new(StepKind.Ports, $"Checking host {(one ? "port" : "ports")} {Join(ports)}", $"Host {(one ? "port" : "ports")} {Join(ports)} {(one ? "is" : "are")} free"));
        }
        steps.Add(new(StepKind.Create, $"Creating {name}", $"Created {name}"));
        if (start)
        {
            steps.Add(new(StepKind.Start, $"Starting {name}", $"Started {name}"));
            AddChecks(steps, health, healthUrl, lanTargets);
        }
        steps.Add(new(StepKind.Policy, "Setting its restart policy", "Set its restart policy"));
        return steps;
    }

    // Create, start, check, then the restart policy; any failure removes what was made.
    private async Task<PortChangeResult> ApplyCreateAsync(PortChangePlan plan, IProgress<PortChangeStep>? progress, CancellationToken ct)
    {
        if (plan.Create is not { } request) return Nothing("Nothing was created: the plan doesn't say what to create.");
        var name = request.Name;
        using var held = await locks.TryAcquireAsync(name);
        if (held is null) return Nothing(BusyText(name));

        CreateLook look;
        try
        {
            look = await CreateLookAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Nothing($"Nothing was created. {Unreadable(name, ex)}");
        }
        if (look.Plan.Blocked is { } blocked) return Nothing($"Nothing was created: {char.ToLowerInvariant(blocked[0])}{blocked[1..]}");

        var bindings = look.Plan.Bindings;
        var lanTargets = LanTargets(bindings, look.LanAddress);
        var body = ContainerSettingsJson.NewBody(request.Settings, request.Networks, look.LanHostIp, look.GatewayPriority);
        // No restart policy until it has passed: a crash shows at once, and nothing restarts it while it's removed.
        ((JsonObject)body["HostConfig"]!)["RestartPolicy"] = NoRestart();
        var healthWindow = HealthWindowOf(body, look.Image);
        var healthUrl = HealthUrlOf(name, request.HealthPath, request.WebPort, body);
        var steps = new StepTracker(progress, _time, CreateSteps(name, request.Start, bindings, healthWindow is not null, lanTargets, healthUrl));
        var scrub = ScrubberFor(EnvOf(body), request.SecretKeys);
        var none = CancellationToken.None;

        // 1. The host ports are free: the probe binds them for a moment.
        if (request.Start && bindings.Count > 0)
        {
            steps.Begin(StepKind.Ports);
            string? taken;
            try
            {
                taken = await probe.ProbeAsync(bindings.Select(b => new ProbePort(b.HostIp, b.HostPort, b.Protocol)).ToList(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                taken = ex.Message;
            }
            if (taken is not null)
            {
                steps.Fail(StepKind.Ports);
                return Nothing(scrub($"{(PortIn(taken, bindings.Select(b => b.HostPort)) is { } port ? $"Port {port.ToString(CultureInfo.InvariantCulture)}" : "A host port")} is taken ({PortTaken(taken) ?? taken}). Nothing was created."));
            }
            steps.Done(StepKind.Ports);
        }
        ct.ThrowIfCancellationRequested();

        // 2. Created, not started. From here on nothing is cancelled: what's made is either kept or removed.
        var since = _time.GetUtcNow().UtcDateTime;
        steps.Begin(StepKind.Create);
        string id;
        try
        {
            id = await engine.CreateAsync(name, body, none);
        }
        catch (Exception ex)
        {
            steps.Fail(StepKind.Create);
            // Only a lost answer may have left a container behind: one by that name made since is this one.
            var note = ex is EngineException { Status: >= 400 and < 500 } ? null : await RemoveMadeAsync(name, since);
            return Nothing(scrub((ex is EngineException { Conflict: true } ? $"Nothing was created: another container is already called {name}."
                : $"Nothing was created: Docker refused it ({ex.Message}).") + (note is null ? "" : " " + note)));
        }
        steps.Done(StepKind.Create);
        var tty = body["Tty"]?.GetValueKind() == JsonValueKind.True;

        if (request.Start)
        {
            // 3. Its first start.
            steps.Begin(StepKind.Start);
            if (await TryAsync(() => engine.StartAsync(id, none)) is { } startError)
            {
                steps.Fail(StepKind.Start);
                return await RemoveFailedAsync(id, name, tty, scrub, PortTaken(startError) is { } portTaken
                    ? $"{name} didn't start: port {PortIn(startError, bindings.Select(b => b.HostPort))?.ToString(CultureInfo.InvariantCulture) ?? "?"} is taken ({portTaken})."
                    : $"{name} didn't start ({startError}).");
            }
            var startedAt = _time.GetUtcNow();
            steps.Done(StepKind.Start);

            // 4. The checks a change makes: up for 15 s or healthy, its health URL, its ports live. Nothing answered before,
            // so a LAN port that doesn't answer is only noted.
            var c = new Change
            {
                Name = name,
                Row = new ContainerChange
                {
                    ContainerName = name, Summary = plan.Summary, OldId = "", OldName = name, TempName = name, OldRestartPolicy = "{}", Kind = ChangeKind.Create,
                },
                NewId = id,
                Stop = new StopWith("SIGTERM", 10),
                Health = healthWindow,
                Tty = tty,
                Bindings = bindings,
                LanTargets = lanTargets,
                LanAddress = look.LanAddress,
                Before = Before.Nothing,
                Steps = steps,
                Scrub = scrub,
                HealthUrl = healthUrl,
                WebPort = request.WebPort,
                HealthPath = request.HealthPath,
            };
            var verified = await VerifyAsync(c, startedAt, measure: false);
            if (verified.Failure is { } failure) return await RemoveFailedAsync(id, name, tty, scrub, failure.Text);

            // 5. It passed: its restart policy.
            steps.Begin(StepKind.Policy);
            var policyError = await TryAsync(() => engine.UpdateRestartPolicyAsync(id, ContainerSettingsJson.RestartJson(request.Settings.Restart), none));
            if (policyError is null) steps.Done(StepKind.Policy);
            else steps.Fail(StepKind.Policy);
            var text = verified.Answering.Count > 0 ? $"{name} is created and answers on {Join(verified.Answering)} (checked from the host)."
                : verified.Silent.Count > 0 ? $"{name} is created and runs. Nothing answers on {Join(verified.Silent.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList())} yet: it may listen only inside its container, or on another port."
                : $"{name} is created and runs.";
            if (policyError is not null) text += $" Its restart policy couldn't be set to {request.Settings.Restart.Name} ({policyError}); set it when you like.";
            return new PortChangeResult(PortChangeOutcome.Done, scrub(text)) { NothingAnswers = verified.Answering.Count == 0 && verified.Silent.Count > 0 };
        }

        steps.Begin(StepKind.Policy);
        var notStartedError = await TryAsync(() => engine.UpdateRestartPolicyAsync(id, ContainerSettingsJson.RestartJson(request.Settings.Restart), none));
        if (notStartedError is null) steps.Done(StepKind.Policy);
        else steps.Fail(StepKind.Policy);
        return new PortChangeResult(PortChangeOutcome.Done, scrub($"{name} is created, not started."
            + (notStartedError is null ? "" : $" Its restart policy couldn't be set ({notStartedError}); set it when you like.")));
    }

    // A first start or its checks failed: the last output is kept for the owner, and the container goes (never its volumes).
    private async Task<PortChangeResult> RemoveFailedAsync(string id, string name, bool tty, Func<string, string> scrub, string why)
    {
        var output = await LastOutputAsync(id, tty);
        var removeError = await TryAsync(() => engine.RemoveAsync(id, true, CancellationToken.None), goneIsFine: true);
        var text = why + (removeError is null
            ? " It was removed: nothing is left in Docker but its volumes."
            : $" It couldn't be removed ({removeError}): remove {name} when you like.");
        return new PortChangeResult(PortChangeOutcome.RolledBack, scrub(text)) { LastOutput = output is null ? null : scrub(output) };
    }

    // After a create whose answer was lost: a container by that name, made since the create began, is the one it made.
    private async Task<string?> RemoveMadeAsync(string name, DateTime since)
    {
        try
        {
            if (await InspectAsync(name) is not { } made || !MadeSince(made, since)) return null;
            return await TryAsync(() => engine.RemoveAsync(Str(made, "Id"), true, CancellationToken.None), goneIsFine: true) is { } error
                ? $"The container Docker may have made couldn't be removed ({error}): remove {name} when you like."
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Docker didn't say whether it made {name} ({ex.Message}).";
        }
    }
}
