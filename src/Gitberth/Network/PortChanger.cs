using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Network;

/// <summary>
/// The change engine: changes a container by recreating it, for what Docker can't change in place (its ports, and
/// every setting but its limits and restart policy, sections 7.4 and 7.5, and 2.3 of the unified design); applies limits
/// live through <see cref="ContainerUpdater"/>; and creates new containers through the same checks.
/// <list type="number">
/// <item>Preflight: the rules, both temporary names free, the new host ports free (and bound for a moment by a probe),
/// and which ports answer now.</item>
/// <item>The new container is created next to the old one, so a setting Docker refuses fails while nothing has stopped.
/// It has no restart policy until it passes its checks: a crash shows at once, and nothing restarts it during a rollback.</item>
/// <item>The old one's restart policy goes to "no": neither Docker nor a policy may start it next to the new one.</item>
/// <item>It stops, with its own signal and timeout (60 s for a database without one).</item>
/// <item>The names swap: the old one becomes name_old_id, the new one takes the name.</item>
/// <item>The new one starts, and is checked: it stays up (or reports healthy), publishes every port, answers its health
/// URL when it has one, and answers on the LAN when it answered before.</item>
/// <item>It gets its restart policy (the new one when the change sets one, else the original's), and the old one is
/// removed, never with its volumes.</item>
/// </list>
/// Every step has its undo, and the journal says how far a change got. A stopped container is recreated stopped, and its
/// original is kept until the new one's first start passes the same checks (<see cref="ContainerStartedAsync"/>). The two
/// containers of a change mount the same volumes, so nothing here starts one while Docker can't say the other is down.
/// </summary>
public sealed partial class PortChanger(
    IContainerEngine engine,
    IChangeJournal journal,
    ContainerLocks locks,
    IHostPortChecks hostPorts,
    IHostPortProbe probe,
    ILanCheck lan,
    INameCheck names,
    IContainerUsers users,
    GitberthOptions options,
    ILogger<PortChanger> logger,
    TimeProvider? time = null,
    ContainerUpdater? updater = null,
    IHealthProbe? health = null)
{
    private const int LogLines = 60;
    private static readonly IReadOnlyDictionary<string, NavApp> NoApps = new Dictionary<string, NavApp>();

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, PortChangeJob> _jobs = new(StringComparer.Ordinal);
    private ContainerUpdater? _ownUpdater;

    // Without one given (a caller from before live changes), live changes are made without an Activity entry.
    private ContainerUpdater Updater => updater ?? (_ownUpdater ??= new ContainerUpdater(engine, locks, new Lazy<PortChanger>(() => this), null!,
        NullLogger<ContainerUpdater>.Instance));

    /// <summary>How long a new container without a health check has to stay up.</summary>
    internal TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>How often the new container's state is read while it's checked.</summary>
    internal TimeSpan StatePoll { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How often a port that answered before is tried again, to measure the time offline.</summary>
    internal TimeSpan OnlinePoll { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>How long a port that answered before may take to answer again after the start, as in a deploy; the health URL too.</summary>
    internal TimeSpan ListenWait { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How often the health URL is tried while it doesn't answer.</summary>
    internal TimeSpan HealthPoll { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The original's name while the new container takes its place: postgres_old_0ebf31bbcd07.</summary>
    public static string OldNameFor(string name, string oldId) => $"{name}_old_{Short(oldId)}";

    /// <summary>
    /// The new container's name until the swap: postgres_new_0ebf31bbcd07. Docker Compose names its own temporary
    /// containers &lt;id12&gt;_&lt;name&gt;; this can't be mistaken for one, nor one of those for this.
    /// </summary>
    public static string TempNameFor(string name, string oldId) => $"{name}_new_{Short(oldId)}";

    /// <summary>
    /// What changing the container's ports to <paramref name="ports"/> would do, with every line of the confirmation. It
    /// changes nothing. <paramref name="ports"/> is every port the container should have afterwards: start from
    /// <see cref="ContainerPorts.Rules"/> and change the rows that change. The same as a request with only its ports
    /// changed, of kind Ports.
    /// </summary>
    public async Task<PortChangePlan> PlanAsync(string container, IReadOnlyList<PortRule> ports, CancellationToken ct = default)
    {
        try
        {
            var look = await LookAsync(container, holdingLock: false, ChangeScope.Ports, ct);
            return await PlanAsync(look, PortsRequest(look, ports), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = Unreadable(container, ex);
            return new PortChangePlan { Container = container, Verdict = new RecreateVerdict(RecreateKind.Missing, why), Blocked = why };
        }
    }

    /// <summary>
    /// What a change of the container's settings would do (2.3): the fields that differ between the settings as the owner
    /// loaded them and as they want them, applied on top of the container as it is now, so a change made outside Gitberth
    /// since the page loaded (a docker update) survives. Only limits and the restart policy: a live update, no recreate.
    /// Anything else: a recreate. Update and Rollback always recreate on the image asked for, which must still be the one
    /// pulled. It changes nothing.
    /// </summary>
    public async Task<PortChangePlan> PlanAsync(string container, ContainerEditRequest request, CancellationToken ct = default)
    {
        try
        {
            var fields = FieldsOf(request);
            var look = await LookAsync(container, holdingLock: false, ScopeOf(fields, request.Kind), ct);
            return await PlanAsync(look, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = Unreadable(container, ex);
            return new PortChangePlan { Container = container, Kind = request.Kind, Verdict = new RecreateVerdict(RecreateKind.Missing, why), Blocked = why };
        }
    }

    // A port change as a request: the container's settings as they are, with these ports.
    private static ContainerEditRequest? PortsRequest(Look look, IReadOnlyList<PortRule> ports)
    {
        if (look.Inspect is not { } inspect) return null;
        var loaded = ContainerSettingsJson.Read(inspect, look.Runs);
        return new ContainerEditRequest { ContainerId = Str(inspect, "Id"), Loaded = loaded, Wanted = loaded with { Ports = ports }, Kind = ChangeKind.Ports };
    }

    /// <summary>The fields a request changes: what differs, and for an update or a rollback the image, always (it's compared by ID).</summary>
    internal static IReadOnlySet<SettingField> FieldsOf(ContainerEditRequest request)
    {
        var fields = SettingsDiff.Changed(request.Loaded, request.Wanted).ToHashSet();
        if (request.Kind is ChangeKind.Update or ChangeKind.Rollback) fields.Add(SettingField.Image);
        if (request.Kind == ChangeKind.Ports) fields.Add(SettingField.Ports);
        return fields;
    }

    private static ChangeScope ScopeOf(IReadOnlySet<SettingField> fields, ChangeKind kind) =>
        kind == ChangeKind.Ports || (fields.Count == 1 && fields.Contains(SettingField.Ports)) ? ChangeScope.Ports : ChangeScope.Settings;

    private static string Unreadable(string container, Exception ex) =>
        ex is EngineException or HttpRequestException or IOException or OperationCanceledException
            ? $"Couldn't read {container} from Docker ({ex.Message})."
            : $"Couldn't work out the change to {container} ({ex.Message}).";

    // Facts that only add a line to the confirmation: one that can't be read leaves its line out, not the whole plan.
    private async Task<T> OptionalAsync<T>(Func<Task<T>> read, T fallback, string what)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Couldn't read {What} for a change", what);
            return fallback;
        }
    }

    /// <summary>
    /// Runs the change in the background and returns at once, so closing the dialog doesn't stop it; the Ports and
    /// Addresses cards follow it through <see cref="JobFor"/>. The same plan started again gets its job back; another plan
    /// while a change of the container runs is refused (its job ends at once, NothingChanged, saying so), never handed the
    /// running change as if it were its own.
    /// </summary>
    public PortChangeJob Start(PortChangePlan plan)
    {
        if (_jobs.TryGetValue(plan.Container, out var running) && !running.Completion.IsCompleted)
            return ReferenceEquals(running.Plan, plan) ? running : new PortChangeJob(plan) { Completion = Task.FromResult(Nothing(BusyText(plan.Container))) };
        var job = new PortChangeJob(plan);
        job.Completion = Task.Run(async () =>
        {
            try
            {
                return await ApplyAsync(plan, job, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Change} failed", Doing(plan.Kind, plan.Container));
                return new PortChangeResult(PortChangeOutcome.NeedsDecision, $"{Doing(plan.Kind, plan.Container)} failed: {ex.Message}");
            }
            finally
            {
                job.Finished();
            }
        });
        _jobs[plan.Container] = job;
        return job;
    }

    /// <summary>The container's change running now, or the last one since Gitberth started; null for none.</summary>
    public PortChangeJob? JobFor(string container) => _jobs.TryGetValue(container, out var job) ? job : null;

    /// <summary>
    /// Raised (on the change's thread) whenever a recreate's change ends, whatever its end: done, put back, rolled back,
    /// nothing changed, or left for the owner to decide, after a restart of Gitberth or the owner's Keep or Put back too.
    /// With the journal row as it ended, so the app's row can follow the container that holds the name now (AppSync).
    /// </summary>
    public event Action<ContainerChange>? Ended;

    /// <summary>
    /// Takes the banner off the container's page, and nothing else: it takes no lock, so a change that moved on since the
    /// banner was read ([Keep the new one] finishing, say) keeps what it says by then.
    /// </summary>
    public async Task DismissAsync(string container, CancellationToken ct = default)
    {
        if (await journal.LatestAsync(container, ct) is { Notice: not null } row) await journal.DismissNoticeAsync(row.Id, ct);
    }

    /// <summary>
    /// Why the container mustn't be changed now (its ports, its settings or its limits); null when nothing stands in the
    /// way. A change to it is under way, waits for a start or for the owner, or it's a copy a finished change left behind:
    /// the parked original of one that was kept, or the unused new container of one that wasn't (it mounts the volumes of
    /// the container that stayed, as <see cref="WhyNotStartAsync"/> says). Locks aren't counted: the caller holds or tries one.
    /// </summary>
    public async Task<string?> WhyNotChangeAsync(string container, CancellationToken ct = default)
    {
        var inspect = await engine.InspectContainerAsync(container, ct);
        var name = inspect is null ? container : Str(inspect, "Name").TrimStart('/');
        return await RefusalAsync(name, inspect is null ? null : Str(inspect, "Id"), ct);
    }

    /// <summary>Everything a plan reads, read once.</summary>
    /// <param name="Image">The image its tag names now.</param>
    /// <param name="Runs">The image it runs, by its ID: the same as Image unless a pull has moved the tag on.</param>
    private sealed record Look(
        string Name,
        JsonObject? Inspect,
        JsonObject? Image,
        JsonObject? Runs,
        JsonArray Networks,
        IReadOnlyList<JsonObject> Others,
        string LanAddress,
        string LanHostIp,
        RecreateContext Context,
        RecreateVerdict Verdict);

    private async Task<Look> LookAsync(string container, bool holdingLock, ChangeScope scope, CancellationToken ct)
    {
        var inspect = await engine.InspectContainerAsync(container, ct);
        var name = inspect is null ? container : Str(inspect, "Name").TrimStart('/');
        var version = await engine.VersionAsync(ct);
        var networks = await engine.ListNetworksAsync(ct);
        var lanAddress = await hostPorts.LanAddressAsync(ct);
        var lanHostIp = await hostPorts.LanHostIpAsync(ct);
        var others = inspect is null ? [] : await OthersAsync(Str(inspect, "Id"), ct);
        var tag = Str(inspect?["Config"], "Image");
        var imageId = Str(inspect, "Image");
        var image = inspect is null ? null : await engine.InspectImageAsync(tag.Length > 0 ? tag : imageId, ct);
        // A tag that moved on: the image it runs has the digest that pins it. Without it, the ID does.
        var runs = image is not null && Str(image, "Id") == imageId ? image
            : inspect is null || imageId.Length == 0 ? null
            : await OptionalAsync(() => engine.InspectImageAsync(imageId, ct), null, "the image it runs");
        var busy = await BusyAsync(name, inspect is null ? null : Str(inspect, "Id"), holdingLock, ct);
        var context = new RecreateContext(version, options.OwnContainer, others, networks, lanAddress, busy);
        var verdict = RecreateRules.Classify(name, inspect, context, scope);
        return new Look(name, inspect, image, runs, networks, others, lanAddress, lanHostIp, context, verdict);
    }

    // Every other container, for who shares its namespaces or links to it. The list doesn't say, so each is inspected.
    private async Task<IReadOnlyList<JsonObject>> OthersAsync(string id, CancellationToken ct)
    {
        var ids = (await engine.ListContainersAsync(ct)).OfType<JsonObject>()
            .Where(c => Str(c, "Id") != id && !(c["Labels"] is JsonObject l && l.ContainsKey(RecreateRules.HelperLabel)))
            .Select(c => Str(c, "Id")).Where(i => i.Length > 0).ToList();
        using var gate = new SemaphoreSlim(8);
        var inspects = await Task.WhenAll(ids.Select(async other =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await engine.InspectContainerAsync(other, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return inspects.OfType<JsonObject>().ToList();
    }

    private async Task<string?> BusyAsync(string name, string? id, bool holdingLock, CancellationToken ct) =>
        await RefusalAsync(name, id, ct) ?? (!holdingLock && locks.IsHeld(name) ? BusyText(name) : null);

    // A change of its that isn't settled, by any of its names; or a finished change that left it behind, by its ID.
    private async Task<string?> RefusalAsync(string name, string? id, CancellationToken ct)
    {
        var row = (await journal.UnfinishedAsync(ct)).LastOrDefault(r => r.ContainerName == name || r.OldName == name || r.TempName == name);
        if (row is not null)
            return name != row.ContainerName ? $"This is {row.ContainerName}'s other container, kept until its {Noun(row.Kind)} is settled."
                : row.Outcome == ChangeOutcome.NeedsDecision ? $"{On(row.Kind, name)} waits for your decision: keep the new container or put the original back."
                : row.Step == ChangeStep.WaitingForStart
                    ? $"{name} hasn't started since its last {Noun(row.Kind)}. Start it, or put the original back, before changing {(row.Kind == ChangeKind.Ports ? "its ports" : "it")} again."
                : BusyText(name);
        if (id is { Length: > 0 } && await journal.WithContainerAsync(id, ct) is { Unfinished: false } done && LeftBehind(done, id, name) is { } left)
            return left;
        return null;
    }

    private static string BusyText(string name) => $"Gitberth is already changing {name}. Try again when that's done.";

    /// <summary>"A port change on postgres", "A settings change on postgres", "An update of postgres", "A rollback of postgres".</summary>
    internal static string On(ChangeKind kind, string name) => kind switch
    {
        ChangeKind.Settings => $"A settings change on {name}",
        ChangeKind.Update => $"An update of {name}",
        ChangeKind.Rollback => $"A rollback of {name}",
        ChangeKind.Create => $"Creating {name}",
        _ => $"A port change on {name}",
    };

    /// <summary>"port change", "settings change", "update", "rollback".</summary>
    internal static string Noun(ChangeKind kind) => kind switch
    {
        ChangeKind.Settings => "settings change",
        ChangeKind.Update => "update",
        ChangeKind.Rollback => "rollback",
        ChangeKind.Create => "creation",
        _ => "port change",
    };

    // "Changing postgres's ports", "Changing postgres's settings", "Updating postgres", "Creating gb-whoami".
    private static string Doing(ChangeKind kind, string name) => kind switch
    {
        ChangeKind.Settings => $"Changing {name}'s settings",
        ChangeKind.Update => $"Updating {name}",
        ChangeKind.Rollback => $"Rolling {name} back",
        ChangeKind.Create => $"Creating {name}",
        _ => $"Changing the ports of {name}",
    };

    // What a plan's words call the container: the app's name the request gives, else the container's own.
    private static string Shown(ContainerEditRequest? request, string name) => request?.Title is { Length: > 0 } title ? title : name;

    // "with its new ports", "with its new settings", "on its new image", "on its previous image".
    private static string WithWhat(ChangeKind kind) => kind switch
    {
        ChangeKind.Settings => "with its new settings",
        ChangeKind.Update => "on its new image",
        ChangeKind.Rollback => "on its previous image",
        _ => "with its new ports",
    };

    /// <summary>The plan for a request, on what was read.</summary>
    private async Task<PortChangePlan> PlanAsync(Look look, ContainerEditRequest? request, CancellationToken ct)
    {
        var name = look.Name;
        var kind = request?.Kind ?? ChangeKind.Ports;
        if (look.Inspect is not { } inspect || request is null)
            return new PortChangePlan { Container = name, Kind = kind, Verdict = look.Verdict, Blocked = look.Verdict.Reason };
        if (Str(inspect, "Id") != request.ContainerId)
            return new PortChangePlan { Container = name, Kind = kind, Verdict = look.Verdict, Blocked = $"{Shown(request, name)} changed since you started editing.", ContainerId = Str(inspect, "Id") };

        var fields = FieldsOf(request);
        // What the lines call it: the app's name when the request gives one. Addresses (postgres:5432) keep the container's.
        var shown = Shown(request, name);
        var live = ContainerSettingsJson.Read(inspect, look.Runs);
        var target = SettingsDiff.Merge(live, request.Wanted, fields);
        var running = ContainerPorts.Running(inspect);
        var secrets = request.SecretKeys;
        var host = await OptionalAsync(() => engine.HostAsync(ct), null, "the host's CPUs and memory");
        var problem = SettingsRules.Check(target, new SettingsContext(host?.Cpus ?? 0, host?.MemoryBytes ?? 0, CloneSpec.NetworkNames(look.Networks), GitApp: false))
            .Where(p => p.Field is not { } f || fields.Contains(f)).Select(p => p.Message).FirstOrDefault();

        // Limits and the restart policy alone, and Docker can make them live: a docker update, no recreate.
        if (kind is not (ChangeKind.Update or ChangeKind.Rollback or ChangeKind.Ports) && fields.Count > 0 && fields.All(SettingFields.Live.Contains)
            && ContainerUpdater.CanApplyLive(live, target, fields, inspect["HostConfig"] as JsonObject))
            return await LivePlanAsync(look, inspect, request, live, target, fields, problem, ct);

        // The ports: as asked when they change, else the ones it has now, kept as Docker has them (random ones stay random).
        var portsChanged = fields.Contains(SettingField.Ports);
        var rules = (portsChanged ? target.Ports : ContainerPorts.Rules(inspect)).Select(r => r with { Protocol = r.Protocol.ToLowerInvariant() }).Distinct().ToList();
        var current = ContainerPorts.Read(inspect);
        var ownMode = ContainerSettingsJson.IsOwnMode(target.NetworkMode);
        problem ??= portsChanged ? rules.Select(r => r.Problem()).FirstOrDefault(p => p is not null) : null;
        var bindings = ownMode ? [] : problem is null && portsChanged ? CloneSpec.Bindings(inspect, rules, look.LanHostIp) : problem is null ? Fixed(current) : [];
        if (portsChanged) problem ??= Twice(bindings);
        if (problem is not null) bindings = [];
        var changes = problem is null && portsChanged ? Lines(current, bindings, look.LanAddress) : [];
        if (portsChanged && changes.Count == 0 && kind != ChangeKind.Ports) fields = fields.Where(f => f != SettingField.Ports).ToHashSet();

        // The image it's recreated on: the one asked for, which must be on the host (and, for an update, still the one pulled).
        JsonObject? newImage = null;
        if (fields.Contains(SettingField.Image) && problem is null)
        {
            newImage = await engine.InspectImageAsync(target.Image, ct);
            if (newImage is null) problem = $"{target.Image} isn't on this host yet.";
            else if (request.ExpectedImageId is { } expected && Str(newImage, "Id") != expected)
                problem = $"{target.Image} moved again while it was pulled: check again.";
            else if (look.Runs is null)
                problem = $"Docker couldn't say what the image {name} runs supplies, so its settings can't be moved to another image.";
        }
        // An image asked for by its ID (a rollback's) goes by its repository's digest when it has one, so it keeps its name.
        if (newImage is not null) target = Named(target, newImage, live.Image);
        if (problem is null && newImage is not null && look.Runs is { } before)
            problem = DatabaseImageProblem(name, inspect, kind, live, target, fields, before, newImage, PreviewBody(inspect, look, target, fields, newImage));
        // New volumes: none a running database writes, and none anything running writes when it's a database.
        if (problem is null && fields.Contains(SettingField.Mounts))
            problem = SharedVolumes(name, "Recreating", ContainerService.IsDatabase(target.Image, (newImage ?? look.Runs)?["Config"] ?? inspect["Config"]),
                target.Mounts.Where(m => m.Kind == MountKind.Volume).Select(m => (m.Source, m.ReadOnly)), look.Others.Where(o => Str(o, "Id") != Str(inspect, "Id")));

        var settingFields = fields.Where(f => f != SettingField.Ports).ToHashSet();
        var lines = SettingsDiff.Lines(live, target, settingFields, secrets, _ => ApplyKind.Recreate).ToList();
        if (fields.Contains(SettingField.Image))
        {
            var at = lines.FindIndex(l => l.Field == SettingField.Image);
            var line = new SettingLine(SettingField.Image, "Image", $"{live.Image} ({Short(Str(inspect, "Image"))})",
                // A rollback's image was pulled long ago: on the containerd store its LastTagTime is when it lost its tag.
                $"{target.Image} ({(newImage is null ? "not on this host" : Short(Str(newImage, "Id")) + (kind == ChangeKind.Rollback ? "" : Pulled(newImage)))})", ApplyKind.Recreate);
            if (at >= 0) lines[at] = line;
            else lines.Insert(0, line);
        }

        var compose = look.Verdict.Compose;
        var plan = new PortChangePlan
        {
            Container = name,
            Kind = kind,
            Fields = fields,
            Verdict = look.Verdict,
            Ports = rules,
            Bindings = bindings,
            Changes = changes,
            Settings = lines,
            ContainerId = Str(inspect, "Id"),
            Running = running,
            Summary = SummaryOf(kind, live, target, settingFields, current, bindings, portsChanged, inspect, newImage),
            ImageAfter = fields.Contains(SettingField.Image) ? target.Image : null,
            Request = request,
            ComposeSnippet = compose is not null && problem is null
                ? kind == ChangeKind.Ports || settingFields.Count == 0 ? compose.Snippet(bindings) : ComposeSnippet.For(compose, target, fields, secrets)
                : null,
        };
        if (problem is not null) return plan with { Blocked = problem };
        if (!look.Verdict.Allowed) return plan with { Blocked = look.Verdict.Reason };
        if (changes.Count == 0 && lines.Count == 0) return plan;

        var unheld = Unheld(current, bindings);
        if (portsChanged)
            foreach (var b in unheld)
                if (await hostPorts.HolderAsync(name, b.HostPort, b.Protocol, b.HostIp, ct) is { } holder)
                    return plan with { Blocked = $"Port {b.HostPort.ToString(CultureInfo.InvariantCulture)} is taken by {holder}." };
        if (!portsChanged) unheld = [];

        var database = IsDatabase(inspect, name);
        var changedKeys = changes.Select(c => c.Port).ToHashSet(StringComparer.Ordinal);
        var impact = new List<string>();
        if (running) impact.Add($"{shown} restarts once: about {(database ? 5 : 3)} s offline.");
        else
        {
            var takes = bindings.Where(b => changedKeys.Contains(b.Key)).Select(b => b.HostPort).Distinct().ToList();
            impact.Add($"{shown} is stopped: it's recreated stopped, so nothing goes offline."
                + (takes.Count == 0 ? "" : $" It takes {PortWords(takes)} when it starts."));
        }
        if (running && RoleOf(inspect, name) == ContainerRole.Tunnel) impact.Add("Every domain it serves stops answering until it's back.");

        var who = running ? await OptionalAsync(() => users.UsersOfAsync(name, DynamicAddresses(inspect), ct), ContainerUsers.None, "who uses it") : ContainerUsers.None;
        if (who.Users.Count > 0)
            impact.Add($"{Join(who.Users)} {(who.Users.Count == 1 ? "uses" : "use")} {shown}: {(who.Users.Count == 1 ? "its" : "their")} connections drop and reconnect.");

        var shared = Endpoints(inspect).ContainsKey(options.Network);
        foreach (var port in current.Where(p => changedKeys.Contains(p.Key) && p.Bindings.Any(b => b.HostPort is not null) && bindings.All(b => b.Key != p.Key)))
        {
            var gone = port.Bindings.Where(b => b.HostPort is not null).Select(b => BindingAddress(b.HostIp, b.HostPort!.Value, look.LanAddress)).Distinct().ToList();
            impact.Add($"{Join(gone)} {(gone.Count == 1 ? "stops" : "stop")} answering"
                + (shared ? $"; apps keep {name}:{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}." : "."));
        }

        var stillBound = bindings.Select(b => (b.HostPort, b.Protocol)).ToHashSet();
        foreach (var (hostPort, protocol) in current.Where(p => changedKeys.Contains(p.Key) && p.Protocol == "tcp")
                     .SelectMany(p => p.Bindings.Where(b => b.HostPort is not null).Select(b => (b.HostPort!.Value, p.Protocol))).Distinct())
        {
            if (stillBound.Contains((hostPort, protocol))) continue;
            var routes = await OptionalAsync(() => hostPorts.TunnelRoutesAsync(hostPort, ct), [], "the tunnel's routes");
            if (routes.Count > 0)
                impact.Add($"{Join(routes)} {(routes.Count == 1 ? "reaches" : "reach")} host port {hostPort.ToString(CultureInfo.InvariantCulture)} through the tunnel and {(routes.Count == 1 ? "stops" : "stop")} working.");
        }

        var lost = LostFiles(await OptionalAsync(() => engine.ChangesAsync(Str(inspect, "Id"), ct), [], "its changed files"), inspect);
        if (lost.Count > 0)
            impact.Add($"{lost.Count.ToString(CultureInfo.InvariantCulture)} {(lost.Count == 1 ? "file" : "files")} changed outside its volumes {(lost.Count == 1 ? "is" : "are")} lost: "
                + (lost.Count <= 3 ? string.Join(", ", lost) : string.Join(", ", lost.Take(3)) + $" and {(lost.Count - 3).ToString(CultureInfo.InvariantCulture)} more") + ".");

        if (who.Building.Count > 0)
            impact.Add($"{Join(who.Building)} {(who.Building.Count == 1 ? "is" : "are")} building and found {shown} at its current address; {(who.Building.Count == 1 ? "that build" : "those builds")} may fail.");
        foreach (var pinned in who.NameItsAddress)
            impact.Add($"{pinned.Who} names {shown}'s address {pinned.Address} in its settings; that address can change.");

        foreach (var port in current.Where(p => changedKeys.Contains(p.Key)))
            foreach (var random in port.Bindings.Where(b => b.Random && b.HostPort is not null))
                if (bindings.Any(b => b.Key == port.Key && b.HostPort == random.HostPort))
                    impact.Add($"Its random host port {random.HostPort!.Value.ToString(CultureInfo.InvariantCulture)} becomes fixed at {random.HostPort.Value.ToString(CultureInfo.InvariantCulture)}.");

        if (running)
            foreach (var port in bindings.Where(b => b.Protocol == "tcp" && changedKeys.Contains(b.Key)).Select(b => b.ContainerPort).Distinct())
                if (await OptionalAsync(() => names.AnswersAsync(name, port, ct), null, "whether it answers") == false)
                    impact.Add($"Nothing answers on {name}:{port.ToString(CultureInfo.InvariantCulture)} right now, so publishing it may not make it reachable.");

        var mode = CloneSpec.NetworkModeFor(inspect, look.Networks);
        if (!fields.Contains(SettingField.NetworkMode) || !ownMode)
        {
            if (mode.Gone is { } goneNetwork && mode.Mode is { } instead)
                impact.Add($"Its network {goneNetwork} no longer exists, so the new container uses {instead} instead.");
            foreach (var missing in Endpoints(inspect).Select(e => e.Key).Where(n => CloneSpec.NetworkName(look.Networks, n) is null))
                impact.Add($"Its network {missing} no longer exists, so the new container leaves it out.");
        }
        impact.AddRange(await SettingsImpactAsync(shown, inspect, live, target, fields, kind, database, running, ct));
        if (fields.Contains(SettingField.Image) && newImage is not null && look.Runs is { } runs)
            impact.AddRange(RebaseNotes(inspect, look, target, fields, runs, newImage, secrets));

        var stop = StopOf(inspect, database);
        var body = PreviewBody(inspect, look, target, fields, newImage);
        var healthWindow = HealthWindowOf(body, newImage ?? look.Runs ?? look.Image);
        var lanChecks = LanTargets(bindings, look.LanAddress);
        var healthUrl = HealthUrlOf(name, request.HealthPath, request.WebPort, body);
        if (UncheckedHealthNote(name, request.HealthPath, request.WebPort, body) is { } healthNote) impact.Add(healthNote);
        return plan with
        {
            Keeps = Keeps(inspect, look.Image, look.Runs, look.Networks, name, fields),
            Impact = impact,
            Footer = running
                ? "If the new container fails its checks, the old one comes back as it was."
                : $"The original is kept until {shown}'s first start passes its checks.",
            OfflineEstimate = running ? TimeSpan.FromSeconds(database ? 5 : 3) : null,
            Steps = StepsFor(running, unheld, stop, OldNameFor(name, Str(inspect, "Id")), healthWindow is not null, lanChecks, healthUrl).Select(s => s.Done).ToList(),
        };
    }

    // A plan made only of live fields: docker update, with no KEEPS, no ALSO, no offline time. Its consequence is "No restart."
    private async Task<PortChangePlan> LivePlanAsync(Look look, JsonObject inspect, ContainerEditRequest request, ContainerSettings live, ContainerSettings target,
        IReadOnlySet<SettingField> fields, string? problem, CancellationToken ct)
    {
        var name = look.Name;
        var verdict = RecreateRules.LiveVerdict(name, inspect, look.Context);
        var impact = new List<string>();
        if (fields.Contains(SettingField.Memory) && target.Resources.MemoryBytes is { } limit
            && await OptionalAsync(() => engine.MemoryUsageAsync(Str(inspect, "Id"), ct), null, "the memory it uses") is { } used && (long)used > limit)
            impact.Add(MemoryLine((long)used, limit));
        if (ComposeService.Of(inspect) is not null) impact.Add(RecreateRules.ComposeLiveNote);
        if (fields.Contains(SettingField.RestartPolicy) && target.Restart.Name == "always" && !ContainerPorts.Running(inspect))
            impact.Add($"{Shown(request, name)} stays stopped for now, but with the policy always Docker starts it again when Docker itself starts (after a reboot, say).");
        var plan = new PortChangePlan
        {
            Container = name,
            Kind = request.Kind,
            Fields = fields,
            LiveOnly = true,
            Verdict = verdict,
            Settings = SettingsDiff.Lines(live, target, fields, request.SecretKeys, _ => ApplyKind.Live),
            ContainerId = Str(inspect, "Id"),
            Running = ContainerPorts.Running(inspect),
            Ports = ContainerPorts.Rules(inspect),
            Summary = SettingsDiff.Summary(live, target, fields),
            Request = request,
            Keeps = "",
            AlsoChanges = "",
            Impact = impact,
            Footer = "No restart.",
            Steps = ["Updated its limits"],
        };
        return problem is not null ? plan with { Blocked = problem } : !verdict.Allowed ? plan with { Blocked = verdict.Reason } : plan;
    }

    // The settings' own impact lines (2.7): memory below what it uses, a network mode that leaves its networks, and what an
    // update can't undo.
    private async Task<List<string>> SettingsImpactAsync(string name, JsonObject inspect, ContainerSettings live, ContainerSettings target,
        IReadOnlySet<SettingField> fields, ChangeKind kind, bool database, bool running, CancellationToken ct)
    {
        var lines = new List<string>();
        if (running && fields.Contains(SettingField.Memory) && target.Resources.MemoryBytes is { } limit
            && await OptionalAsync(() => engine.MemoryUsageAsync(Str(inspect, "Id"), ct), null, "the memory it uses") is { } used && (long)used > limit)
            lines.Add(MemoryLine((long)used, limit));
        if (fields.Contains(SettingField.NetworkMode) && ContainerSettingsJson.IsOwnMode(target.NetworkMode) && !ContainerSettingsJson.IsOwnMode(live.NetworkMode))
        {
            var named = Endpoints(inspect).Select(e => e.Key).Where(n => n is not ("bridge" or "host" or "none")).ToList();
            if (named.Count > 0) lines.Add($"It leaves {Join(named)}: containers there can't reach it by name any more.");
            if (ContainerPorts.Read(inspect).Any(p => p.Bindings.Count > 0)) lines.Add("Its published ports go: " + (target.NetworkMode == "host"
                ? "on the host's network it listens on the host's own ports." : "it has no network of its own to publish them on."));
        }
        if (kind is ChangeKind.Update or ChangeKind.Rollback || fields.Contains(SettingField.Image))
        {
            if (database)
                lines.Add($"{name} checks its data files when it starts: if it can't use them, it fails its checks and the old container comes back.");
            lines.Add(kind == ChangeKind.Rollback
                ? "If the newer version changed its data when it started, putting the older one back doesn't undo that."
                : "If the new version changes its data when it starts, putting the old one back doesn't undo that.");
        }
        return lines;
    }

    /// <summary>The settings with their image named by its repository's digest (postgres@sha256:…) when it was asked for by ID and has one.</summary>
    internal static ContainerSettings Named(ContainerSettings target, JsonObject newImage, string liveImage)
    {
        if (!(target.Image.StartsWith("sha256:", StringComparison.Ordinal) || Images.ImageReference.IsImageId(target.Image))) return target;
        var digests = (newImage["RepoDigests"] as JsonArray ?? []).OfType<JsonValue>().Select(d => d.TryGetValue<string>(out var s) ? s : "")
            .Where(d => d.Contains("@sha256:", StringComparison.Ordinal)).ToList();
        var repository = ContainerService.Repository(liveImage);
        var named = digests.FirstOrDefault(d => ContainerService.Repository(d) == repository) ?? digests.FirstOrDefault();
        return named is null ? target : target with { Image = named };
    }

    // The variables official database images name their major version by.
    private static readonly string[] MajorKeys = ["PG_MAJOR", "MYSQL_MAJOR", "MARIADB_MAJOR", "MONGO_MAJOR"];

    /// <summary>
    /// Why a database's image can't change like this (section 2.6), or null: another major version can't read the data
    /// (postgres 18 to 17 or 19, mysql 8 to 9, a tag such as latest that may be either), and a new image that keeps its data
    /// in a folder none of the container's volumes holds would start with an empty database while its own data stays behind.
    /// Within a major version, onto data its volumes hold, the change goes ahead.
    /// </summary>
    private static string? DatabaseImageProblem(string name, JsonObject inspect, ChangeKind kind, ContainerSettings live, ContainerSettings target,
        IReadOnlySet<SettingField> fields, JsonObject runs, JsonObject newImage, JsonObject body)
    {
        if (!ContainerService.IsDatabase(live.Image, inspect["Config"]) && !ContainerService.IsDatabase(target.Image, newImage["Config"])) return null;
        var product = Product(target.Image.Length > 0 && !target.Image.StartsWith("sha256:", StringComparison.Ordinal) ? target.Image : live.Image);
        // An update or a rollback stays on its tag; the owner's own change of the image may move to another.
        if (kind == ChangeKind.Settings)
        {
            var (before, after) = Majors(live.Image, runs, target.Image, newImage);
            if (before is null)
                return $"Gitberth can't tell which major version of {product} {name} runs, and a new major version can't read an older one's data: "
                    + "dump it and restore it into a new app instead.";
            if (after is null)
                return $"{target.Image} may be another major version of {product} than {before}, which can't read {before}'s data: pick a {before} tag, "
                    + "or dump it and restore it into a new app instead.";
            if (before != after) return $"A new major version of {product} can't read {before}'s data: dump it and restore it into a new app instead.";
        }

        // Where the new image keeps its data: its VOLUME folders and its PGDATA (the container's own when it sets one).
        var config = newImage["Config"];
        var own = (ContainerSettingsJson.StrList(body["Env"]) ?? []).FirstOrDefault(e => e.StartsWith("PGDATA=", StringComparison.Ordinal));
        var pgdata = own ?? (ContainerSettingsJson.StrList(config?["Env"]) ?? []).FirstOrDefault(e => e.StartsWith("PGDATA=", StringComparison.Ordinal));
        var paths = (config?["Volumes"] as JsonObject ?? []).Select(v => v.Key).Concat(pgdata is null ? [] : [pgdata["PGDATA=".Length..]])
            .Select(CloneSpec.Clean).Where(p => p.Length > 1).Distinct(StringComparer.Ordinal).ToList();
        var held = fields.Contains(SettingField.Mounts)
            ? target.Mounts.Where(m => m.Kind != MountKind.Tmpfs).Select(m => CloneSpec.Clean(m.Target)).ToList()
            : (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Str(m, "Type") is "volume" or "bind").Select(m => CloneSpec.Clean(Str(m, "Destination"))).ToList();
        if (paths.FirstOrDefault(p => !held.Any(h => p == h || p.StartsWith(h + "/", StringComparison.Ordinal))) is { } outside)
            return $"{target.Image} keeps its data in {outside}, which none of {name}'s volumes holds: it would start with an empty database, and its data "
                + "would stay behind. Mount its volume there first, or keep the image it has.";
        return null;
    }

    // The major versions of the image it runs and the one asked for: by the variable official images set (PG_MAJOR=18),
    // else by the tag's leading number (18 of 18.1-alpine); null when it can't be told (latest, a digest).
    private static (string? Before, string? After) Majors(string liveImage, JsonObject runs, string targetImage, JsonObject newImage)
    {
        foreach (var key in MajorKeys)
            if (EnvValue(runs, key) is { } a && EnvValue(newImage, key) is { } b) return (a, b);
        var before = TagMajor(liveImage) ?? (runs["RepoTags"] as JsonArray ?? []).OfType<JsonValue>()
            .Select(t => t.TryGetValue<string>(out var s) ? s : "").Where(t => ContainerService.Repository(t) == ContainerService.Repository(liveImage))
            .Select(TagMajor).FirstOrDefault(m => m is not null);
        return (before, TagMajor(targetImage));
    }

    private static string? EnvValue(JsonObject image, string key) =>
        (ContainerSettingsJson.StrList(image["Config"]?["Env"]) ?? []).Where(e => e.StartsWith(key + "=", StringComparison.Ordinal)).Select(e => e[(key.Length + 1)..])
            .FirstOrDefault(v => v.Length > 0);

    // "18" of postgres:18.1-alpine; null for latest, a digest or no tag.
    private static string? TagMajor(string reference)
    {
        if (reference.Contains('@') || reference.StartsWith("sha256:", StringComparison.Ordinal)) return null;
        var colon = reference.LastIndexOf(':');
        if (colon < 0 || colon < reference.LastIndexOf('/')) return null;
        var digits = new string(reference[(colon + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    // "PostgreSQL" for postgres:18, bitnami/postgresql or postgis/postgis; the repository's name for the rest.
    private static string Product(string image)
    {
        var repository = ContainerService.Repository(image).ToLowerInvariant();
        return repository.StartsWith("postg", StringComparison.Ordinal) || repository.StartsWith("timescale", StringComparison.Ordinal) || repository.StartsWith("pgvec", StringComparison.Ordinal)
            ? "PostgreSQL"
            : repository.StartsWith("mysql", StringComparison.Ordinal) ? "MySQL"
            : repository.StartsWith("mariadb", StringComparison.Ordinal) ? "MariaDB"
            : repository.StartsWith("mongo", StringComparison.Ordinal) ? "MongoDB"
            : repository.StartsWith("redis", StringComparison.Ordinal) ? "Redis"
            : ContainerService.Repository(image);
    }

    private static string MemoryLine(long used, long limit) =>
        $"It uses {SettingsDiff.Size(used)} now: with a {SettingsDiff.Size(limit)} limit Docker stops it when it needs more.";

    // "From the new image: PG_VERSION 18.0 → 18.1", from the body the change would make (built here and dropped: it holds secrets).
    private static IEnumerable<string> RebaseNotes(JsonObject inspect, Look look, ContainerSettings target, IReadOnlySet<SettingField> fields,
        JsonObject runs, JsonObject newImage, IReadOnlyCollection<string> secrets)
    {
        var body = PreviewBody(inspect, look, target, fields, null);
        return ImageRebase.Apply(body, runs, newImage).Select(note => Masked(note, newImage, secrets));
    }

    // A note's value hidden when its variable looks secret.
    private static string Masked(string note, JsonObject image, IReadOnlyCollection<string> secrets)
    {
        const string lead = "From the new image: ";
        if (!note.StartsWith(lead, StringComparison.Ordinal)) return note;
        var key = note[lead.Length..].Split(' ')[0];
        var values = (ContainerSettingsJson.StrList(image["Config"]?["Env"]) ?? []).Where(e => e.StartsWith(key + "=", StringComparison.Ordinal)).Select(e => e[(key.Length + 1)..]);
        return values.Any(v => Security.Masking.IsSecret(key, v, secrets)) ? lead + key + " (changed; its value is hidden)" : note;
    }

    // The body a change makes, for the plan's own reading (its health check, the rebase notes); never kept.
    private static JsonObject PreviewBody(JsonObject inspect, Look look, ContainerSettings target, IReadOnlySet<SettingField> fields, JsonObject? newImage)
    {
        var body = BodyFor(inspect, look.Image, look.Runs, look.Networks, look.LanHostIp, target, fields, "gitberth");
        if (newImage is not null && look.Runs is { } runs) ImageRebase.Apply(body, runs, newImage);
        return body;
    }

    /// <summary>
    /// The create body for the change (2.3): the clone of the container as it is, its ports as asked when they change (else
    /// exactly as Docker has them), then only the changed fields written on top. It holds secrets: it's built when it's
    /// used, never kept.
    /// </summary>
    private static JsonObject BodyFor(JsonObject inspect, JsonObject? image, JsonObject? runs, JsonArray networks, string lanHostIp, ContainerSettings target,
        IReadOnlySet<SettingField> fields, string sharedNetwork)
    {
        var portsChanged = fields.Contains(SettingField.Ports);
        var body = CloneSpec.Build(inspect, image, portsChanged ? target.Ports : ContainerPorts.Rules(inspect), networks, lanHostIp, runs);
        if (!portsChanged)
        {
            // Untouched, the ports stay as they're set: a random one stays random, and every address as it is.
            var hostConfig = (JsonObject)body["HostConfig"]!;
            hostConfig["PortBindings"] = inspect["HostConfig"]?["PortBindings"]?.DeepClone() ?? new JsonObject();
            hostConfig["PublishAllPorts"] = inspect["HostConfig"]?["PublishAllPorts"]?.DeepClone() ?? false;
        }
        ContainerSettingsJson.Write(body, target, fields.Where(f => f != SettingField.Ports).ToHashSet(), sharedNetwork);
        return body;
    }

    // The bindings a container keeps when its ports don't change: the fixed ones (a random one gets a new port at each start).
    private static List<NewBinding> Fixed(IReadOnlyList<CurrentPort> current) =>
        current.SelectMany(p => p.Bindings.Where(b => !b.Random && b.HostPort is not null).Select(b => new NewBinding(p.ContainerPort, p.Protocol, b.HostIp, b.HostPort!.Value)))
            .Distinct().ToList();

    private string SummaryOf(ChangeKind kind, ContainerSettings live, ContainerSettings target, IReadOnlySet<SettingField> settingFields,
        IReadOnlyList<CurrentPort> current, IReadOnlyList<NewBinding> bindings, bool portsChanged, JsonObject inspect, JsonObject? newImage)
    {
        var from = Short(Str(inspect, "Image"));
        var to = newImage is null ? "?" : Short(Str(newImage, "Id"));
        if (kind == ChangeKind.Update) return $"updated {target.Image} ({Abbrev(from)} → {Abbrev(to)})";
        if (kind == ChangeKind.Rollback) return $"rolled back to {target.Image} ({Abbrev(from)} → {Abbrev(to)})";
        var ports = portsChanged ? Summary(current, bindings) : "";
        if (kind == ChangeKind.Ports || settingFields.Count == 0) return ports;
        var settings = SettingsDiff.Summary(live, target, settingFields);
        return ports.Length == 0 ? settings : $"{settings}; {ports}";
    }

    // "3a42…": the start of an ID, as summaries name images.
    private static string Abbrev(string shortId) => shortId.Length > 4 ? shortId[..4] + "…" : shortId;

    // ", pulled 2 min ago", from the image's own record of when its tag last moved.
    private string Pulled(JsonObject image)
    {
        if (!DateTimeOffset.TryParse(Str(image["Metadata"], "LastTagTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at.Year < 2000)
            return "";
        var ago = _time.GetUtcNow() - at;
        return ago < TimeSpan.Zero ? ""
            : ago < TimeSpan.FromMinutes(1) ? ", pulled just now"
            : ago < TimeSpan.FromHours(1) ? $", pulled {((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min ago"
            : ago < TimeSpan.FromDays(2) ? $", pulled {((int)ago.TotalHours).ToString(CultureInfo.InvariantCulture)} h ago"
            : $", pulled {((int)ago.TotalDays).ToString(CultureInfo.InvariantCulture)} days ago";
    }

    // The health URL Gitberth checks after the start: by the container's name, which Gitberth reaches only on the shared
    // network. A container that isn't on it (Docker's bridge, the host's network) has its URL left unchecked, as its plan says.
    private string? HealthUrlOf(string name, string? path, int? port, JsonObject body) =>
        path is { Length: > 0 } && port is { } p && OnShared(body) ? HealthProbe.Url(name, p, path) : null;

    private bool OnShared(JsonObject body) =>
        !ContainerSettingsJson.IsOwnMode(Str(body["HostConfig"], "NetworkMode"))
        && body["NetworkingConfig"]?["EndpointsConfig"] is JsonObject endpoints && endpoints.ContainsKey(options.Network);

    // The plan's line for a health URL Gitberth can't reach.
    private string? UncheckedHealthNote(string name, string? path, int? port, JsonObject body) =>
        path is { Length: > 0 } && port is not null && !OnShared(body)
            ? $"Its health URL isn't checked after the start: Gitberth reaches {name} by name only on {options.Network}, which it isn't on."
            : null;

    // Two bindings can't take one host port and protocol on addresses that overlap: Docker would refuse the second at
    // start. 127.0.0.1 and ::1, or 0.0.0.0 and ::, are two bindings Docker allows.
    private static string? Twice(IReadOnlyList<NewBinding> bindings)
    {
        for (var i = 0; i < bindings.Count; i++)
            for (var j = i + 1; j < bindings.Count; j++)
                if (bindings[i].HostPort == bindings[j].HostPort && bindings[i].Protocol == bindings[j].Protocol
                    && ContainerPorts.Overlap(bindings[i].HostIp, bindings[j].HostIp))
                    return $"Host port {bindings[i].HostPort.ToString(CultureInfo.InvariantCulture)}/{bindings[i].Protocol} is listed twice.";
        return null;
    }

    /// <summary>The CHANGE lines: each port whose bindings differ, or that Docker publishes on a random port now.</summary>
    internal static List<PortChangeLine> Lines(IReadOnlyList<CurrentPort> current, IReadOnlyList<NewBinding> bindings, string lanAddress)
    {
        var keys = current.Where(p => p.Bindings.Count > 0).Select(p => (p.ContainerPort, p.Protocol))
            .Concat(bindings.Select(b => (b.ContainerPort, b.Protocol)))
            .Distinct().OrderBy(k => k.ContainerPort).ThenBy(k => k.Protocol, StringComparer.Ordinal);
        var lines = new List<PortChangeLine>();
        foreach (var (port, protocol) in keys)
        {
            var key = ContainerPorts.Key(port, protocol);
            var before = current.FirstOrDefault(p => p.Key == key)?.Bindings ?? [];
            var after = bindings.Where(b => b.Key == key).ToList();
            var same = before.Count == after.Count && before.All(b => !b.Random && after.Any(a => a.HostIp == b.HostIp && a.HostPort == b.HostPort));
            if (same) continue;
            lines.Add(new PortChangeLine(key,
                before.Count == 0 ? "Internal" : Join(before.Select(b => ContainerPorts.Describe(b.HostIp, b.HostPort, lanAddress, b.Random)).ToList()),
                after.Count == 0 ? "Internal" : Join(after.Select(a => ContainerPorts.Describe(a.HostIp, a.HostPort, lanAddress)).ToList())));
        }
        return lines;
    }

    /// <summary>What changed, for "Last change": published 5432/tcp on the LAN.</summary>
    internal static string Summary(IReadOnlyList<CurrentPort> current, IReadOnlyList<NewBinding> bindings)
    {
        var parts = new List<string>();
        foreach (var line in Lines(current, bindings, ""))
        {
            var before = current.FirstOrDefault(p => p.Key == line.Port)?.Bindings ?? [];
            var after = bindings.Where(b => b.Key == line.Port).ToList();
            parts.Add(before.Count == 0 && after.All(a => a.Access == PortAccess.HostOnly) ? $"published {line.Port} on the host only"
                : before.Count == 0 ? $"published {line.Port} on the LAN"
                : after.Count == 0 ? $"unpublished {line.Port}"
                : after.Count == 1 && before.All(b => b.Access == after[0].Access) ? $"moved {line.Port} to host port {after[0].HostPort.ToString(CultureInfo.InvariantCulture)}"
                : after.All(a => a.Access == PortAccess.HostOnly) ? $"made {line.Port} host only"
                : after.Any(a => a.Access == PortAccess.Lan) && before.All(b => b.Access != PortAccess.Lan) ? $"published {line.Port} on the LAN"
                : $"changed {line.Port}");
        }
        return parts.Count <= 2 ? Join(parts) : $"{parts.Count.ToString(CultureInfo.InvariantCulture)} port changes";
    }

    /// <summary>
    /// The bindings the old container doesn't hold already: none of its bindings of that host port covers the address
    /// (the same address, every address, or every address of its family). Someone else may hold any of these, so each is
    /// checked against the host-port map: widening 127.0.0.1:5432 to every address is checked, keeping it isn't.
    /// </summary>
    internal static List<NewBinding> Unheld(IReadOnlyList<CurrentPort> current, IReadOnlyList<NewBinding> bindings) =>
        bindings.Where(b => !Held(current, b).Any(h => ContainerPorts.Covers(h.HostIp, b.HostIp))).ToList();

    /// <summary>The bindings the probe can bind: no binding of the old container's overlaps them, so its own socket can't get in the way.</summary>
    internal static List<NewBinding> Probeable(IReadOnlyList<CurrentPort> current, IReadOnlyList<NewBinding> bindings) =>
        bindings.Where(b => !Held(current, b).Any(h => ContainerPorts.Overlap(h.HostIp, b.HostIp))).ToList();

    // The old container's bindings of the same host port and protocol.
    private static IEnumerable<HostBinding> Held(IReadOnlyList<CurrentPort> current, NewBinding b) =>
        current.Where(p => p.Protocol == b.Protocol).SelectMany(p => p.Bindings).Where(h => h.HostPort == b.HostPort);

    /// <summary>The KEEPS line.</summary>
    /// <param name="image">The image its tag names now.</param>
    /// <param name="runs">The image it runs, by its ID.</param>
    internal static string Keeps(JsonObject inspect, JsonObject? image, JsonObject? runs, JsonArray networks, string name) =>
        Keeps(inspect, image, runs, networks, name, new HashSet<SettingField>());

    /// <summary>The KEEPS line, less what the change changes (those are CHANGE lines).</summary>
    internal static string Keeps(JsonObject inspect, JsonObject? image, JsonObject? runs, JsonArray networks, string name, IReadOnlySet<SettingField> fields)
    {
        var parts = new List<string> { "Its name" };
        var tag = Str(inspect["Config"], "Image");
        var id = Short(Str(inspect, "Image"));
        if (!fields.Contains(SettingField.Image))
            parts.Add(CloneSpec.ImageFor(inspect, image, runs) == tag && tag.Length > 0
                ? $"image {tag} ({id}, not pulled again)"
                : $"image {id}, the one it runs{(tag.Length > 0 && !tag.StartsWith("sha256:", StringComparison.Ordinal) ? $" ({tag} has moved on; not pulled again)" : "")}");
        if (!fields.Contains(SettingField.Command) && !fields.Contains(SettingField.Entrypoint)) parts.Add("command");
        if (!fields.Contains(SettingField.Env)) parts.Add("environment");

        if (!fields.Contains(SettingField.Mounts))
        {
            var volumes = CloneSpec.Volumes(inspect);
            var anonymous = volumes.Count(v => v.Anonymous);
            var items = volumes.Where(v => !v.Anonymous).Select(v => v.Name)
                .Concat((inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Str(m, "Type") == "bind").Select(m => Str(m, "Source")))
                .Distinct(StringComparer.Ordinal).ToList();
            if (anonymous > 0) items.Add($"{anonymous.ToString(CultureInfo.InvariantCulture)} anonymous");
            if (items.Count == 1) parts.Add(anonymous == 0 ? $"volume {items[0]}" : anonymous == 1 ? "its anonymous volume" : $"{anonymous.ToString(CultureInfo.InvariantCulture)} anonymous volumes");
            else if (items.Count > 1) parts.Add($"volumes ({Join(items)})");
        }

        if (!fields.Contains(SettingField.NetworkMode))
        {
            var shortId = Short(Str(inspect, "Id"));
            var nets = new List<string>();
            foreach (var (network, node) in Endpoints(inspect))
            {
                if (CloneSpec.NetworkName(networks, network) is null) continue;
                // Docker's default bridge gives no names.
                if (network is "bridge" or "host" or "none")
                {
                    nets.Add(network);
                    continue;
                }
                var known = new List<string> { name };
                known.AddRange((node?["Aliases"] as JsonArray ?? []).OfType<JsonValue>().Select(a => a.TryGetValue<string>(out var s) ? s : "")
                    .Where(a => a.Length > 0 && a != shortId));
                var ip = Str(node?["IPAMConfig"], "IPv4Address");
                nets.Add($"{network} (names {string.Join(", ", known.Distinct(StringComparer.Ordinal))}{(ip.Length > 0 ? $", IP {ip}" : "")})");
            }
            if (nets.Count > 0) parts.Add((nets.Count == 1 ? "network " : "networks ") + Join(nets));
        }

        var hostConfig = inspect["HostConfig"];
        if (!fields.Contains(SettingField.RestartPolicy) && Str(hostConfig?["RestartPolicy"], "Name") is { Length: > 0 } policy && policy != "no") parts.Add("restart policy");
        if (!fields.Overlaps([SettingField.Memory, SettingField.Cpu, SettingField.PidsLimit, SettingField.Ulimits])
            && (new[] { "Memory", "MemoryReservation", "NanoCpus", "CpuShares", "CpuQuota", "PidsLimit", "BlkioWeight" }.Any(k => Long(hostConfig, k) > 0)
                || Str(hostConfig, "CpusetCpus").Length > 0 || hostConfig?["Ulimits"] is JsonArray { Count: > 0 }))
            parts.Add("limits");
        if (!fields.Contains(SettingField.Health) && HasHealthcheck(inspect)) parts.Add("health check");
        if (!fields.Contains(SettingField.Hostname)) parts.Add("hostname");
        if (parts.Count == 1) return parts[0] + ".";
        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1] + ".";
    }

    // Files written outside its volumes, which the new container won't have. Directories that only hold changed files,
    // mount points, Docker's own files and paths under /tmp, /run and /var/run don't count.
    internal static List<string> LostFiles(JsonArray changes, JsonObject inspect)
    {
        var mounts = (inspect["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Select(m => CloneSpec.Clean(Str(m, "Destination")))
            .Concat(["/tmp", "/run", "/var/run", "/dev", "/proc", "/sys", "/etc/hosts", "/etc/hostname", "/etc/resolv.conf", "/.dockerenv"])
            .ToList();
        var paths = changes.OfType<JsonObject>().Select(c => Str(c, "Path")).Where(p => p.Length > 1).Distinct(StringComparer.Ordinal).ToList();
        return paths
            .Where(p => !mounts.Any(m => p == m || p.StartsWith(m + "/", StringComparison.Ordinal)))
            .Where(p => !paths.Any(o => o.StartsWith(p + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // Addresses Docker hands out at start, which a recreate can change: not the static ones.
    private static List<string> DynamicAddresses(JsonObject inspect) =>
        Endpoints(inspect).Select(e => e.Value)
            .Where(e => Str(e?["IPAMConfig"], "IPv4Address").Length == 0 && Str(e, "IPAddress").Length > 0)
            .Select(e => Str(e, "IPAddress")).Distinct().ToList();

    internal static JsonObject Endpoints(JsonObject inspect) => inspect["NetworkSettings"]?["Networks"] as JsonObject ?? [];

    // By its name, or by what its image declares (an image ID after a rollback is still postgres): the one detector.
    private static bool IsDatabase(JsonObject inspect, string name) => RoleOf(inspect, name) switch
    {
        ContainerRole.Database => true,
        ContainerRole.Other => ContainerService.IsDatabase(Str(inspect["Config"], "Image"), inspect["Config"]),
        _ => false,
    };

    private static ContainerRole RoleOf(JsonObject inspect, string name) =>
        ContainerService.RoleOf(new ContainerSummary(name, "", "", Str(inspect["Config"], "Image"), "", RecreateRules.Labels(inspect),
            string.Join(' ', ContainerSettingsJson.StrList(inspect["Config"]?["Entrypoint"]) ?? []), Str(inspect, "Id")), NoApps, null);

    /// <param name="Signal">The container's own stop signal.</param>
    /// <param name="Seconds">
    /// Its own stop timeout, as Docker reads it: 0 kills it at once, -1 never kills it. Without one, 60 for a database (a
    /// clean shutdown spares it crash recovery), else Docker's 10.
    /// </param>
    private sealed record StopWith(string Signal, int Seconds)
    {
        /// <summary>"waits up to 60 s", in the step's words.</summary>
        public string Words => Seconds < 0 ? "waits until it stops" : Seconds == 0 ? "kills it at once"
            : $"waits up to {Seconds.ToString(CultureInfo.InvariantCulture)} s";

        /// <summary>How long the stop can take: its timeout and 15 s, or ten minutes when it never kills (or waits longer).</summary>
        public TimeSpan Window => Seconds is < 0 or > 585 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(Seconds + 15);
    }

    private static StopWith StopOf(JsonObject inspect, bool database)
    {
        var config = inspect["Config"];
        var signal = Str(config, "StopSignal") is { Length: > 0 } s ? s : "SIGTERM";
        // Docker leaves the key out (or null) unless --stop-timeout set it; 0 and -1 are the container's own choice.
        var own = config?["StopTimeout"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? (int)Math.Clamp(Long(config, "StopTimeout"), -1, int.MaxValue)
            : (int?)null;
        return new StopWith(signal, own ?? (database ? 60 : 10));
    }

    private static bool HasHealthcheck(JsonObject inspect) => IsCheck(inspect["Config"]?["Healthcheck"]);

    private static bool IsCheck(JsonNode? check) =>
        check?["Test"] is JsonArray { Count: > 0 } test && !(test[0] is JsonValue v && v.TryGetValue<string>(out var first) && first == "NONE");

    /// <summary>How long a health check may take to say healthy: StartPeriod + Interval × (Retries + 1), plus 10 s.</summary>
    private static TimeSpan? HealthWindow(JsonObject inspect) => WindowOf(inspect["Config"]?["Healthcheck"]);

    /// <summary>The health window of the new container: its body's check, else its image's (the body leaves the image's out).</summary>
    private static TimeSpan? HealthWindowOf(JsonObject body, JsonObject? image) =>
        body["Healthcheck"] is JsonObject check ? WindowOf(check) : WindowOf(image?["Config"]?["Healthcheck"]);

    private static TimeSpan? WindowOf(JsonNode? check)
    {
        if (!IsCheck(check)) return null;
        var interval = Long(check, "Interval") is > 0 and var i ? TimeSpan.FromTicks(i / 100) : TimeSpan.FromSeconds(30);
        var retries = Long(check, "Retries") is > 0 and var r ? r : 3;
        var start = TimeSpan.FromTicks(Math.Max(0, Long(check, "StartPeriod")) / 100);
        return start + interval * (retries + 1) + TimeSpan.FromSeconds(10);
    }

    // The bindings a device on the LAN connects to: TCP, not on loopback.
    private static List<(NewBinding Binding, string Address)> LanTargets(IReadOnlyList<NewBinding> bindings, string lanAddress) =>
        bindings.Where(b => b.Protocol == "tcp" && b.Access != PortAccess.HostOnly)
            .Select(b => (b, BindingAddress(b.HostIp, b.HostPort, lanAddress)))
            .DistinctBy(t => t.Item2)
            .ToList();

    private static string BindingAddress(string hostIp, int hostPort, string lanAddress) =>
        ContainerPorts.Address(ContainerPorts.AccessOf(hostIp) == PortAccess.Lan ? lanAddress : hostIp, hostPort);

    private static string PortWords(IReadOnlyList<int> ports) =>
        (ports.Count == 1 ? "port " : "ports ") + Join(ports.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList());

    internal static string Join(IReadOnlyList<string> items) => RecreateRules.Join(items);

    /// <summary>The first 12 hex digits of an ID, without "sha256:".</summary>
    internal static string Short(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    internal static string Str(JsonNode? node, string name) => ContainerPorts.Str(node, name);

    private static long Long(JsonNode? node, string name) => ContainerPorts.Number(node, name);

    /// <summary>Docker's words for a port someone else holds, or null for another error.</summary>
    internal static string? PortTaken(string message) =>
        message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ? "port is already allocated"
        : message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
          && (message.Contains("bind", StringComparison.OrdinalIgnoreCase) || message.Contains("host port", StringComparison.OrdinalIgnoreCase)
              || message.Contains("listen", StringComparison.OrdinalIgnoreCase))
            ? "address already in use"
            : null;

    /// <summary>The port a message of Docker's names, among the ones the change takes: "Bind for 0.0.0.0:5432 failed" gives 5432.</summary>
    internal static int? PortIn(string message, IEnumerable<int> ports)
    {
        var wanted = ports.ToHashSet();
        foreach (Match m in PortNumber().Matches(message))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && wanted.Contains(port))
                return port;
        return wanted.Count == 1 ? wanted.First() : null;
    }

    [GeneratedRegex(@"[:\s](\d{1,5})(?=\D|$)")]
    private static partial Regex PortNumber();
}
