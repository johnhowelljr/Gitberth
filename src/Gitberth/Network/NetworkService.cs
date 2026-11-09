using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Network;

/// <summary>
/// One look at every container (helpers left out): Docker's list, plus what each was created with for its ports, which a
/// stopped container's list entry leaves out.
/// </summary>
public sealed record NetworkSnapshot(DateTime ReadAt, IReadOnlyList<NetworkContainer> Containers, IReadOnlyDictionary<string, NetworkPortConfig> Configs)
{
    public static readonly NetworkSnapshot Empty = new(DateTime.MinValue, [], new Dictionary<string, NetworkPortConfig>());

    /// <summary>
    /// Each container's Config.Image by its ID: the reference it was created with (postgres:18), which Docker's list
    /// replaces with the image's ID once the tag names another image.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigImages { get; init; } = new Dictionary<string, string>();

    public NetworkPortConfig ConfigOf(NetworkContainer c) => Configs.GetValueOrDefault(c.Id) ?? NetworkPortConfig.None;

    /// <summary>The image reference the container was created with; the list's image when it wasn't read.</summary>
    public string ConfigImageOf(NetworkContainer c) => ConfigImages.GetValueOrDefault(c.Id) is { Length: > 0 } image ? image : c.Image;
}

/// <summary>A container on a network, as the Network page lists it.</summary>
/// <param name="Title">The app's name ("kanban"), "gitberth (Gitberth)", or the container's name.</param>
/// <param name="Link">Its page: an app's container links to the app.</param>
/// <param name="IpAddress">Null while it's stopped: Docker gives it an address when it starts.</param>
/// <param name="Names">What containers on the network call it: its name and aliases, not its short ID. Docker's default bridge has none.</param>
public sealed record NetworkMember(string Container, string Title, string? Link, bool Running, string? IpAddress, IReadOnlyList<string> Names);

/// <summary>A Docker network, what Gitberth knows it as, and its members, running or stopped.</summary>
/// <param name="Note">"Gitberth's shared network", "Docker Compose (db)" and the like; null for a network nothing more is known of.</param>
/// <param name="System">Docker's own (bridge, host, none, and Swarm's docker_gwbridge and ingress): never offered for joining.</param>
/// <param name="Shared">The shared network every app is on (gitberth).</param>
public sealed record NetworkView(NetworkInfo Network, string? Note, bool System, bool Shared, IReadOnlyList<NetworkMember> Members)
{
    /// <summary>Whether its members can be listed: host and none have no addresses or names to show.</summary>
    public bool Expandable => Members.Count > 0 && Network.Name is not ("host" or "none");
}

/// <param name="Error">Why Docker couldn't be read; the list is empty then.</param>
public sealed record NetworkOverview(IReadOnlyList<NetworkView> Networks, string? Error)
{
    public static readonly NetworkOverview Empty = new([], null);
}

/// <summary>A network a container is on, or that Gitberth keeps it on, as its Networks card lists it (5.7).</summary>
/// <param name="Names">What containers there call it: its name and aliases, not its short ID. None on Docker's default bridge, or while it isn't on it.</param>
/// <param name="IpAddress">Its address there; null while it's stopped, or isn't on it.</param>
/// <param name="Note">"Shared network · kept", "Shared network · always on", "Joined by Gitberth · every deploy", "Docker Compose (db)" and the like.</param>
/// <param name="Kept">Gitberth joins it there again: when something recreates it, or with every deploy for an app.</param>
/// <param name="Attached">It's on the network now. A kept one it isn't on is joined again when it next starts.</param>
/// <param name="LeaveRefusal">Why it can't leave; null when Leave… is offered.</param>
public sealed record NetworkAttachment(string Network, IReadOnlyList<string> Names, string? IpAddress, string? Note, bool Kept, bool Attached, string? LeaveRefusal);

/// <summary>A container's Networks card (5.7): its networks, and the ones it could join.</summary>
/// <param name="Joinable">The user-defined networks it isn't on, the shared one first. Never Docker's own.</param>
/// <param name="Note">
/// What the card says instead of a table, for a container without networks of its own (the host's, none, another
/// container's); or why Gitberth leaves its networks alone (a helper, a deploy's temporary container, Swarm's or
/// Kubernetes'). Null otherwise.
/// </param>
public sealed record ContainerNetworks(IReadOnlyList<NetworkAttachment> Networks, IReadOnlyList<string> Joinable, string? Note);

/// <summary>Whether a container can join a network, and what to know before it does (5.7). Changes nothing.</summary>
/// <param name="Refusal">Why it can't, in the words the Networks card shows; null when it can.</param>
/// <param name="Taken">"Another container on gitberth is already called db. Both would answer to db." when it would share a name there.</param>
/// <param name="Names">What containers on the network will call it: an app its network name, any other container its own name; plus the alias.</param>
public sealed record JoinPlan(string? Refusal, string? Taken, IReadOnlyList<string> Names);

/// <summary>Whether a container can leave a network, and what the confirmation says (5.7). Changes nothing.</summary>
/// <param name="Refusal">Why it can't; null when it can.</param>
/// <param name="OnlyNetwork">It's the container's only network: its published ports stop answering until it joins another.</param>
/// <param name="PortsMoveTo">The network its published ports move to, when this one carries them now: a moment of failed connections.</param>
/// <param name="Users">
/// Who loses it now: the running containers there whose settings name it as a host, by a name it answers to there (as a
/// DATABASE_URL does), and that don't reach it by that name on another network. An app's container goes by the app's
/// name. Empty when nothing there names it; null when it isn't on the network, or Docker couldn't say.
/// </param>
/// <param name="OwnAddress">
/// Its own LAN address, when the network is gitberth-lan: leaving takes it away, and the confirmation is the LAN address
/// card's, "Devices using 10.1.1.230 lose it."
/// </param>
public sealed record LeavePlan(string? Refusal, bool OnlyNetwork, string? PortsMoveTo, IReadOnlyList<string>? Users = null, string? OwnAddress = null);

/// <summary>A container's own LAN address (gitberth-lan), as its LAN address card shows it.</summary>
/// <param name="Title">What lines name it by: the app's name, or the container's.</param>
/// <param name="Refusal">Why it can't have one, in the card's words; null when it can.</param>
/// <param name="Address">Its own address: there now, or kept for it there; null for none.</param>
/// <param name="Attached">It's on gitberth-lan now.</param>
/// <param name="Running">It runs, so something can answer on its address.</param>
/// <param name="Ports">The TCP ports it's known to listen on: what its image and settings expose or publish.</param>
public sealed record OwnAddress(string Title, string? Refusal, string? Address, bool Attached, bool Running, IReadOnlyList<int> Ports);

/// <summary>What joining or leaving a network did.</summary>
/// <param name="Error">Why it didn't happen; null when it did.</param>
/// <param name="Done">The line to show: "postgres joined gitberth. Containers there reach it as postgres."</param>
/// <param name="Warning">A name it now shares with another container on the network.</param>
public sealed record NetworkChange(string? Error, string? Done = null, string? Warning = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The Docker networks on the host and who's on them, for the Network page, and the one look at Docker's containers that
/// the addresses and the host-port map are built from. Membership comes from each container's own endpoints, so stopped
/// containers count too. A container's port configuration is read from its inspect the first time it's seen (it never
/// changes for that container) and forgotten when the container is gone.
/// <para>
/// It also joins containers to networks and takes them off, the same way for apps and other containers (5.7, 7.3): at
/// gateway priority -1, so a container's published ports and its way out stay where they are, and remembered so the join
/// lasts. An app's networks are saved for its deploys to pass again; any other container's are kept, and joined again
/// when something recreates it (<see cref="RejoinAsync"/>). An own LAN address is a join of gitberth-lan the same way,
/// with the address pinned, and only through <see cref="SetLanAddressAsync"/>. It never prunes or removes a network.
/// </para>
/// </summary>
/// <param name="containerLocks">The locks a port change and a container's actions hold; a join or leave doesn't wait for one.</param>
/// <param name="appLocks">The locks a deploy's swap and an app's actions hold.</param>
/// <param name="changer">
/// The port changes: a container one of them hasn't settled is neither joined nor taken off a network meanwhile. It
/// depends on ContainerService, which depends on this service, so it comes when it's first needed.
/// </param>
public sealed partial class NetworkService(DockerApi api, IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options,
    ContainerLocks? containerLocks = null, AppLocks? appLocks = null, Lazy<PortChanger>? changer = null)
{
    public const string ComposeProjectLabel = "com.docker.compose.project";

    private readonly ConcurrentDictionary<string, NetworkPortConfig> _configs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _configImages = new(StringComparer.Ordinal);
    private readonly ContainerLocks _containerLocks = containerLocks ?? new ContainerLocks();
    private readonly AppLocks _appLocks = appLocks ?? new AppLocks();

    /// <summary>The shared network every app is on.</summary>
    public string Shared => options.Network;

    /// <summary>
    /// The network own LAN addresses are on (<see cref="LanAddresses.Network"/>), which a container only joins through its
    /// LAN address card, with its address pinned. The real-engine test names its own.
    /// </summary>
    internal string LanNetwork { get; init; } = LanAddresses.Network;

    /// <summary>Gitberth's own container when it runs in one: its ID, else its hostname (<see cref="GitberthOptions.OwnContainer"/>).</summary>
    public string? Self => options.OwnContainer;

    /// <summary>
    /// Makes a plain bridge network called <paramref name="name"/>, labelled as Gitberth's, for a new app whose source names
    /// one this host doesn't have. Nothing happens when it exists already. Docker's own networks and own LAN addresses'
    /// network can't be made here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name can't be a network's.</exception>
    public async Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 63 || !AliasName().IsMatch(name))
            throw new InvalidOperationException("A network's name has letters, digits, -, _ and ., and starts with a letter or digit.");
        if (name is "bridge" or "host" or "none" || name == LanNetwork)
            throw new InvalidOperationException($"{name} is one of Docker's or Gitberth's own networks.");
        if (await api.InspectNetworkAsync(name, ct) is not null) return;
        await api.CreateNetworkAsync(new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = "bridge",
            ["CheckDuplicate"] = true,
            ["Labels"] = new JsonObject { ["gitberth.managed"] = "true" },
        }, ct);
    }

    /// <summary>Every container, helpers left out, with what each was created with for its ports.</summary>
    public async Task<NetworkSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var listed = (await api.ListContainersAsync(ct)).Where(c => !HostScanHelper.IsHelper(c.Labels)).ToList();
        var unknown = listed.Where(c => !_configs.ContainsKey(c.Id)).ToList();
        await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (c, token) =>
        {
            // One that's gone in between has nothing to read; it drops out of the next list.
            if (await api.InspectContainerAsync(c.Id, token) is not { } inspect) return;
            _configImages[c.Id] = ContainerPorts.Str(inspect["Config"], "Image");
            _configs[c.Id] = NetworkPortConfig.From(inspect);
        });
        var ids = listed.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _configs.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _configs.TryRemove(gone, out _);
            _configImages.TryRemove(gone, out _);
        }
        return new NetworkSnapshot(DateTime.UtcNow, listed, new Dictionary<string, NetworkPortConfig>(_configs, StringComparer.Ordinal))
        {
            ConfigImages = new Dictionary<string, string>(_configImages, StringComparer.Ordinal),
        };
    }

    /// <summary>A container's inspect as Docker gives it, untouched; null when there's no such container. Throws when Docker can't be read.</summary>
    public Task<JsonObject?> InspectAsync(string container, CancellationToken ct = default) => api.InspectContainerAsync(container, ct);

    /// <summary>An image's inspect by ID or reference; null when there's no such image. Throws when Docker can't be read.</summary>
    public Task<JsonObject?> InspectImageAsync(string image, CancellationToken ct = default) => api.InspectImageAsync(image, ct);

    /// <summary>Every network with its members. Their names aren't read here (see <see cref="MembersAsync"/>).</summary>
    public async Task<NetworkOverview> ListAsync(CancellationToken ct = default)
    {
        try
        {
            // The lan-check's network is a helper's too, there for a few seconds.
            var networks = (await api.ListNetworksAsync(ct)).Where(n => !HostScanHelper.IsHelper(n.Labels)).ToList();
            var containers = (await api.ListContainersAsync(ct)).Where(c => !HostScanHelper.IsHelper(c.Labels)).ToList();
            List<NavApp> apps;
            // Apps are known by their gb-<slug> containers' label; an Existing app's container is listed by its own name.
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                apps = await db.Apps.AsNoTracking().Where(a => a.Container == null).Select(a => new NavApp(a.Id, a.Name, a.Slug)).ToListAsync(ct);
            return Build(networks, containers, apps, Shared, Self);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            return new NetworkOverview([], ex.Message);
        }
    }

    /// <summary>
    /// A network's members with the names others reach them by, read from each one's inspect. When Docker doesn't answer
    /// an inspect, that member and the ones after it are listed without names rather than not at all.
    /// </summary>
    public async Task<IReadOnlyList<NetworkMember>> MembersAsync(string network, CancellationToken ct = default)
    {
        var overview = await ListAsync(ct);
        if (overview.Networks.FirstOrDefault(n => n.Network.Name == network) is not { } view) return [];
        // Docker's default bridge has no names, however many containers are on it.
        if (network == "bridge" || !view.Expandable) return view.Members;
        var named = new List<NetworkMember>();
        try
        {
            foreach (var member in view.Members)
                named.Add(await api.InspectContainerAsync(member.Container, ct) is { } inspect ? member with { Names = NamesOn(inspect, network) } : member);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            // A busy engine: one slow inspect is waited for, not one per member. The names come with the next look.
            return [.. named, .. view.Members.Skip(named.Count)];
        }
        return named;
    }

    /// <summary>
    /// Every name a new one mustn't be on <paramref name="network"/>: whatever its DNS answers for (its members' names and
    /// aliases, running or stopped), and every container's name anywhere, since a container can join it later. Throws
    /// when Docker can't say.
    /// </summary>
    public async Task<IReadOnlySet<string>> TakenNamesAsync(string network, CancellationToken ct = default)
    {
        var containers = await ContainersAsync(ct);
        var names = await NamesOnAsync(containers, network, except: null, ct);
        names.UnionWith(containers.Select(c => c.Name));
        return names;
    }

    /// <summary>
    /// The container's Networks card (5.7): on each network, the names others reach it by, its address, what Gitberth knows
    /// the network as, whether Gitberth keeps it there and whether it may leave; and the networks it could join. For an app,
    /// pass its container's name (gb-&lt;slug&gt;): the networks saved for it are listed even before it has a container.
    /// Throws when Docker can't be read.
    /// </summary>
    public async Task<ContainerNetworks> NetworksOfAsync(string container, CancellationToken ct = default)
    {
        var t = await TargetAsync(container, ct);
        if (t.Inspect is null && t.App is null) return new ContainerNetworks([], [], $"There's no container called {t.Name}.");
        if (await ModeNoteAsync(t, ct) is { } mode) return new ContainerNetworks([], [], mode);
        var kept = await KeptAsync(t, ct);
        var networks = (await api.ListNetworksAsync(ct)).Where(n => !HostScanHelper.IsHelper(n.Labels)).ToList();
        var selfProject = (await ContainersAsync(ct)).FirstOrDefault(c => IsSelf(c, Self) || IsGitberth(c.Labels))?.Labels.GetValueOrDefault(ComposeProjectLabel);
        var endpoints = t.Inspect?["NetworkSettings"]?["Networks"] as JsonObject;
        // What Gitberth calls it comes first: postgres, db; kanban, gb-kanban.
        var called = t.App?.Slug ?? t.Name;
        var rows = new List<NetworkAttachment>();
        foreach (var name in t.Networks.Concat(kept.Select(k => k.Network)).Distinct(StringComparer.Ordinal))
        {
            var attached = t.Networks.Contains(name);
            var isKept = kept.Any(k => k.Network == name);
            rows.Add(new NetworkAttachment(name, attached ? NamesOn(t.Inspect!, name).OrderBy(n => n == called ? 0 : 1).ToList() : [],
                // An own LAN address is its own while it's stopped too.
                !attached ? null : name == LanNetwork ? AddressOn(t.Inspect, name) : ContainerPorts.Str(endpoints?[name], "IPAddress") is { Length: > 0 } ip ? ip : null,
                AttachmentNote(t, name, networks.FirstOrDefault(n => n.Name == name), isKept, selfProject), isKept, attached,
                await LeaveRefusalAsync(t, name, kept, networks, ct)));
        }
        var untouchable = Untouchable(t);
        // Never gitberth-lan: an own address comes from the LAN address card, with its address.
        var joinable = untouchable is not null ? [] : networks
            .Where(n => !IsSystem(n) && n.Name != LanNetwork && rows.All(r => r.Network != n.Name) && !(t.App is not null && n.Name == Shared))
            .OrderBy(n => n.Name == Shared ? 0 : 1).ThenBy(n => n.Name, StringComparer.Ordinal).Select(n => n.Name).ToList();
        return new ContainerNetworks(rows.OrderBy(r => r.Network == Shared ? 0 : 1).ThenBy(r => r.Network, StringComparer.Ordinal).ToList(), joinable, untouchable);
    }

    /// <summary>
    /// Whether the container can join the network, and a name it would share there. For an app, pass its container's name
    /// (gb-&lt;slug&gt;), whether or not the container exists.
    /// </summary>
    /// <param name="alias">A name it answers to there besides its own, like db; in lower case, as docker run gives it.</param>
    public async Task<JoinPlan> PlanJoinAsync(string container, string network, string? alias = null, CancellationToken ct = default)
    {
        try
        {
            return (await JoinPlanAsync(await TargetAsync(container, ct), network, AliasOf(alias), ct)).Plan;
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return new JoinPlan(Unreadable(container, ex), null, []);
        }
    }

    /// <summary>
    /// Joins the container to the network at once; nothing restarts. For an app the network is saved, so every deploy joins
    /// its container again, and the container is joined now if it exists; any other container's network is kept, so it's
    /// joined again when something recreates the container. For an app, pass its container's name (gb-&lt;slug&gt;).
    /// </summary>
    /// <param name="alias">A name it answers to there besides its own, like db; in lower case, as docker run gives it.</param>
    /// <returns>Why it wasn't joined, or the line to show and a name it now shares with another container there.</returns>
    public async Task<NetworkChange> JoinAsync(string container, string network, string? alias = null, CancellationToken ct = default)
    {
        alias = AliasOf(alias);
        try
        {
            var target = await TargetAsync(container, ct);
            var (plan, name) = await JoinPlanAsync(target, network, alias, ct);
            if (plan.Refusal is not null) return new NetworkChange(plan.Refusal);
            using var held = await HoldAsync(target);
            if (held is null) return new NetworkChange(Busy(target));
            var lowPriority = await GatewayPriorityAsync(ct);
            if (target.Inspect is not null)
            {
                try
                {
                    await ConnectAsync(target.Name, name, AliasesFor(target, alias), ipv4: null, lowPriority, ct);
                }
                catch (DockerApiException ex)
                {
                    return new NetworkChange($"Couldn't join {name}: {ex.Message}");
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
                {
                    // No answer: a busy engine may have joined it all the same, after Gitberth gave up. What it shows now decides,
                    // or the card would show it on the network while its deploys left it off.
                    if (!await OnNetworkAsync(target.Name, name, ct)) return new NetworkChange($"Couldn't join {name}: {ex.Message}");
                }
            }
            try
            {
                await KeepAsync(target, name, alias, null, ct);
            }
            catch (Exception ex) when (Unwritten(ex))
            {
                // Docker has it on the network, but Gitberth couldn't write that down: the card would show it there while its
                // next deploy (or recreate) left it off. It leaves again, so the two agree.
                if (target.Inspect is not null) await UndoAsync(() => DisconnectAsync(target.Name, name, CancellationToken.None));
                return new NetworkChange($"Couldn't join {name}: {ex.GetBaseException().Message}");
            }
            var done = $"{target.Title} joined {name}. Containers there reach it as {string.Join(" or ", plan.Names)}.";
            return new NetworkChange(null, lowPriority ? done : done + " Docker before 28 may move its published ports for a moment.", plan.Taken);
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return new NetworkChange($"Couldn't join {network}: {ex.Message}");
        }
    }

    /// <summary>Whether the container can leave the network, where its published ports go if it does, and who loses its name.</summary>
    public async Task<LeavePlan> PlanLeaveAsync(string container, string network, CancellationToken ct = default)
    {
        try
        {
            var target = await TargetAsync(container, ct);
            var plan = await LeavePlanAsync(target, network, ct);
            if (plan.Refusal is not null || !target.Networks.Contains(network)) return plan;
            return network == LanNetwork
                ? plan with { OwnAddress = AddressOn(target.Inspect, network) }
                : plan with { Users = await UsersAsync(target, network, ct) };
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return new LeavePlan(Unreadable(container, ex), false, null);
        }
    }

    /// <summary>
    /// Takes the container off the network at once, and stops keeping it there: an app's next deploys leave it off, and no
    /// other container is joined to it again. Never on an app's or Gitberth's shared network, never Gitberth's own, and
    /// never a network Docker Compose put the container on.
    /// </summary>
    public async Task<NetworkChange> LeaveAsync(string container, string network, CancellationToken ct = default)
    {
        try
        {
            var target = await TargetAsync(container, ct);
            var plan = await LeavePlanAsync(target, network, ct);
            if (plan.Refusal is not null) return new NetworkChange(plan.Refusal);
            using var held = await HoldAsync(target);
            if (held is null) return new NetworkChange(Busy(target));
            if (target.Networks.Contains(network))
            {
                try
                {
                    await DisconnectAsync(target.Name, network, ct);
                }
                catch (DockerApiException ex)
                {
                    return new NetworkChange($"Couldn't leave {network}: {ex.Message}");
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
                {
                    // As for a join: kept while Docker shows it still on the network, or its next deploy would join it again.
                    if (await OnNetworkAsync(target.Name, network, ct)) return new NetworkChange($"Couldn't leave {network}: {ex.Message}");
                }
            }
            try
            {
                await ForgetAsync(target, network, ct);
            }
            catch (Exception ex) when (Unwritten(ex))
            {
                // Docker took it off, but Gitberth still keeps it there: its next deploy (or recreate) would join it again. It
                // joins again now, as it was, so the two agree.
                if (target.Networks.Contains(network)) await UndoAsync(() => RejoinAsItWasAsync(target, network));
                return new NetworkChange($"Couldn't leave {network}: {ex.GetBaseException().Message}");
            }
            return new NetworkChange(null, $"{target.Title} left {network}.");
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return new NetworkChange($"Couldn't leave {network}: {ex.Message}");
        }
    }

    /// <summary>
    /// Joins the container again to each network Gitberth keeps it on that it's missing, with the same alias and address:
    /// for NetworkKeeper when a container starts (docker run, compose up or an image update recreated it), and for a check
    /// at startup. An app's container gets its app's networks, any other container its kept ones. A container that's being
    /// changed is left alone, since the change carries its networks over itself. Returns one result per network joined;
    /// throws when Docker can't be read.
    /// </summary>
    public async Task<IReadOnlyList<NetworkChange>> RejoinAsync(string container, CancellationToken ct = default)
    {
        var target = await TargetAsync(container, ct);
        if (target.Inspect is null || Untouchable(target) is not null || target.Mode is "host" or "none" || target.Mode.StartsWith("container:", StringComparison.Ordinal))
            return [];
        var missing = (await KeptAsync(target, ct)).Where(k => !target.Networks.Contains(k.Network)).ToList();
        if (missing.Count == 0) return [];
        using var held = await HoldAsync(target);
        if (held is null || await UnsettledAsync(target, ct) is not null) return [];
        var lowPriority = await GatewayPriorityAsync(ct);
        var results = new List<NetworkChange>();
        foreach (var (network, alias, ipv4) in missing)
        {
            try
            {
                await ConnectAsync(target.Name, network, AliasesFor(target, alias), ipv4, lowPriority, ct);
                results.Add(new NetworkChange(null, $"{target.Title} joined {network} again."));
            }
            catch (DockerApiException ex)
            {
                results.Add(new NetworkChange($"Couldn't join {target.Title} to {network} again: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>
    /// Forgets what Gitberth kept for a container it removed: the networks it kept it on (its own LAN address too), and its
    /// port notes. What's kept goes by name, so during a change of that name that isn't settled it belongs to whichever
    /// container ends up with the name: the original, which recovery or [Put the original back] gives the name back, or the
    /// new one if it's kept. Removing the container that holds the name meanwhile forgets none of it then. Returns whether
    /// anything was forgotten.
    /// </summary>
    public async Task<bool> ForgetAsync(string container, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await UnsettledChangeOfAsync(db, container, ct)) return false;
        await db.KeptNetworks.Where(k => k.ContainerName == container).ExecuteDeleteAsync(ct);
        await db.ContainerPortNotes.Where(n => n.ContainerName == container).ExecuteDeleteAsync(ct);
        return true;
    }

    // A change of the container's name that isn't over: under way, waiting for the new container's first start, or
    // waiting for the owner.
    private static Task<bool> UnsettledChangeOfAsync(GitberthDb db, string container, CancellationToken ct) =>
        db.ContainerChanges.AsNoTracking().AnyAsync(c => c.ContainerName == container
            && (c.Outcome == ChangeOutcome.Running || c.Outcome == ChangeOutcome.NeedsDecision), ct);

    /// <summary>
    /// Writes down a network a container is on already, as joining it would (for AppCreator, whose create body put the new
    /// app's container on its networks): an app's network row, which its deploys or recreates pass again, or any other
    /// container's kept network. Nothing in Docker changes.
    /// </summary>
    /// <param name="ipv4">Its own address there, pinned; only on gitberth-lan.</param>
    public async Task RememberAsync(string container, string network, string? alias, string? ipv4, CancellationToken ct = default)
    {
        var target = await TargetAsync(container, ct);
        if (target.App is not null && network == Shared && (AliasOf(alias) is null || AliasOf(alias) == target.App.Slug)) return;
        await KeepAsync(target, network, AliasOf(alias), NullIfBlank(ipv4), ct);
    }

    /// <summary>
    /// The container's own LAN address on gitberth-lan, and whether it may have one, for its LAN address card. For an app,
    /// pass its container's name (gb-&lt;slug&gt;): one saved for it is there before it has a container. Throws when Docker
    /// can't be read.
    /// </summary>
    public async Task<OwnAddress> OwnAddressOfAsync(string container, CancellationToken ct = default)
    {
        var t = await TargetAsync(container, ct);
        var kept = (await KeptAsync(t, ct)).FirstOrDefault(k => k.Network == LanNetwork);
        var ports = t.Inspect is null ? [] : ContainerPorts.Read(t.Inspect).Where(p => p.Protocol == "tcp").Select(p => p.ContainerPort).Distinct().ToList();
        return new OwnAddress(t.Title, await LanRefusalAsync(t, ct), AddressOn(t.Inspect, LanNetwork) ?? kept.IPv4Address, t.Networks.Contains(LanNetwork),
            t.Inspect is not null && ContainerPorts.Running(t.Inspect), ports);
    }

    /// <summary>
    /// Gives the container its own LAN address at once, moves it to another, or takes it away (null); nothing restarts. It's
    /// a connect to gitberth-lan at gateway priority -1 with the address pinned (IPAMConfig.IPv4Address), so its published
    /// ports and its way out stay where they are, and the address is the same after a restart or a recreate. It's kept like
    /// a joined network: an app's is saved for its deploys, which give it to the app's own container; any other
    /// container's is kept, and joined again when something recreates the container. Never for Gitberth itself, a
    /// container on the host's network, another's or none, a helper, a deploy's temporary container, or what Swarm or
    /// Kubernetes run; Compose's may have one, since it needs no recreate. <see cref="LanAddresses"/> checks the address
    /// first. For an app, pass its container's name (gb-&lt;slug&gt;), whether or not the container exists.
    /// </summary>
    public async Task<NetworkChange> SetLanAddressAsync(string container, string? address, CancellationToken ct = default)
    {
        try
        {
            var t = await TargetAsync(container, ct);
            if (await LanRefusalAsync(t, ct) is { } refusal) return new NetworkChange(refusal);
            // As for a join or leave: a port change that isn't settled carries the address over, or puts the original back without it.
            if (await UnsettledAsync(t, ct) is { } busy) return new NetworkChange(busy);
            if (await api.InspectNetworkAsync(LanNetwork, ct) is null) return new NetworkChange(LanAddresses.OffText);
            using var held = await HoldAsync(t);
            if (held is null) return new NetworkChange(Busy(t));
            var kept = (await KeptAsync(t, ct)).FirstOrDefault(k => k.Network == LanNetwork);
            var attached = t.Networks.Contains(LanNetwork);
            var current = AddressOn(t.Inspect, LanNetwork) ?? kept.IPv4Address;
            if (address == current && kept.Network is not null && (attached || t.Inspect is null))
                return new NetworkChange(null, address is null ? $"{t.Title} has no own address." : $"{t.Title} has {address} already.");
            if (attached)
            {
                try
                {
                    await DisconnectAsync(t.Name, LanNetwork, ct);
                }
                catch (DockerApiException ex)
                {
                    return new NetworkChange($"Couldn't change {t.Title}'s own address: {ex.Message}");
                }
            }
            if (address is null)
            {
                try
                {
                    await ForgetAsync(t, LanNetwork, ct);
                }
                catch (Exception ex) when (Unwritten(ex))
                {
                    // As for a leave: it gets its address back, so the card and its next recreate agree.
                    if (attached) await UndoAsync(() => RejoinAsItWasAsync(t, LanNetwork));
                    return new NetworkChange($"Couldn't take {t.Title}'s own address away: {ex.GetBaseException().Message}");
                }
                return new NetworkChange(null, current is null ? $"{t.Title} has no own address." : $"{t.Title} no longer has its own address {current}.");
            }
            var lowPriority = await GatewayPriorityAsync(ct);
            if (t.Inspect is not null)
            {
                try
                {
                    await ConnectAsync(t.Name, LanNetwork, AliasesFor(t, null), address, lowPriority, ct);
                }
                catch (DockerApiException ex)
                {
                    // Docker took the old address away but won't give the new one: the old one comes back.
                    if (attached) await UndoAsync(() => RejoinAsItWasAsync(t, LanNetwork));
                    return new NetworkChange($"Couldn't give {t.Title} {address}: {ex.Message}");
                }
            }
            try
            {
                await KeepAsync(t, LanNetwork, kept.Alias, address, ct);
            }
            catch (Exception ex) when (Unwritten(ex))
            {
                if (t.Inspect is not null) await UndoAsync(() => DisconnectAsync(t.Name, LanNetwork, CancellationToken.None));
                if (attached) await UndoAsync(() => RejoinAsItWasAsync(t, LanNetwork));
                return new NetworkChange($"Couldn't give {t.Title} {address}: {ex.GetBaseException().Message}");
            }
            var done = t.Inspect is null ? $"{t.Title} gets its own address {address} with its next deploy." : $"{t.Title} has its own address {address} now.";
            return new NetworkChange(null, lowPriority ? done : done + " Docker before 28 may move its published ports for a moment.");
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return new NetworkChange($"Couldn't change {container}'s own address: {ex.Message}");
        }
    }

    /// <summary>A container as joining and leaving see it.</summary>
    /// <param name="Inspect">Its inspect; null when there's no such container (an app that hasn't one yet).</param>
    /// <param name="App">The app whose container it is: gb-&lt;slug&gt; of an app Gitberth has.</param>
    private sealed record Target(string Name, JsonObject? Inspect, AppDefinition? App, IReadOnlyDictionary<string, string> Labels)
    {
        /// <summary>What lines name it by: the app's name, else the container's.</summary>
        public string Title => App?.Name ?? Name;

        public IReadOnlyCollection<string> Networks =>
            (Inspect?["NetworkSettings"]?["Networks"] as JsonObject)?.Select(n => n.Key).ToList() ?? [];

        public string Mode => ContainerPorts.Str(Inspect?["HostConfig"], "NetworkMode");

        public string Id => ContainerPorts.Str(Inspect, "Id");
    }

    private async Task<Target> TargetAsync(string container, CancellationToken ct)
    {
        var inspect = await api.InspectContainerAsync(container, ct);
        var name = inspect is null ? container : ContainerPorts.Str(inspect, "Name").TrimStart('/');
        AppDefinition? app = null;
        if (name.StartsWith("gb-", StringComparison.Ordinal))
        {
            // An app whose container is gb-<slug>, from any source; an Existing app's networks are kept by its container's name.
            var slug = name["gb-".Length..];
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug && a.Container == null, ct);
        }
        return new Target(name, inspect, app, inspect is null ? new Dictionary<string, string>() : RecreateRules.Labels(inspect));
    }

    // The plan, and the network's own name (it may have been given by ID).
    private async Task<(JoinPlan Plan, string Network)> JoinPlanAsync(Target t, string network, string? alias, CancellationToken ct)
    {
        (JoinPlan, string) Refused(string why) => (new JoinPlan(why, null, []), network);
        if (t.Inspect is null && t.App is null) return Refused($"There's no container called {t.Name}.");
        if (await UnsettledAsync(t, ct) is { } busy) return Refused(busy);
        if (Untouchable(t) is { } untouchable) return Refused(untouchable);
        if (await ModeRefusalAsync(t, joining: true, ct) is { } mode) return Refused(mode);
        if (alias is not null && !AliasName().IsMatch(alias)) return Refused("A name has letters, digits, -, _ and ., and starts with a letter or digit.");
        if (await api.InspectNetworkAsync(network, ct) is not { } info) return Refused($"There's no network called {network}.");
        network = info.Name;
        if (IsSystem(info)) return Refused($"{network} is Docker's own network: Gitberth doesn't join containers to it.");
        if (network == LanNetwork) return Refused($"{network} is for own LAN addresses: give {t.Title} one in its LAN address card.");
        if (t.App is not null && network == Shared) return Refused($"{t.Title} is always on {Shared}: Gitberth made it there.");
        if (t.Networks.Contains(network) || (t.Inspect is null && (await KeptAsync(t, ct)).Any(k => k.Network == network)))
            return Refused($"{t.Title} is already on {network}.");

        var taken = await NamesOnAsync(await ContainersAsync(ct), network, except: t.Name, ct);
        var clash = AnswersTo(t, alias).FirstOrDefault(taken.Contains);
        // An app's names are its own on every network it's on, as its network name is on the shared one: Docker's DNS would
        // split what's meant for the other container between the two. Any other container is warned.
        if (clash is not null && t.App is { } app)
            return Refused(clash == alias && alias != app.Slug
                ? $"Another container on {network} is already called {clash}. Pick another name."
                : $"Another container on {network} is already called {clash}, so {t.Title} can't join it: both would answer to {clash}.");
        return (new JoinPlan(null, clash is null ? null : $"Another container on {network} is already called {clash}. Both would answer to {clash}.", Reached(t, alias)), network);
    }

    private async Task<LeavePlan> LeavePlanAsync(Target t, string network, CancellationToken ct)
    {
        if (t.Inspect is null && t.App is null) return new LeavePlan($"There's no container called {t.Name}.", false, null);
        if (await UnsettledAsync(t, ct) is { } busy) return new LeavePlan(busy, false, null);
        var networks = await api.ListNetworksAsync(ct);
        if (await LeaveRefusalAsync(t, network, await KeptAsync(t, ct), networks, ct) is { } refusal) return new LeavePlan(refusal, false, null);
        // Only kept there: it just stops being kept.
        return t.Networks.Contains(network) ? new LeavePlan(null, t.Networks.Count == 1, PortsMoveTo(t, network, networks)) : new LeavePlan(null, false, null);
    }

    // Why the container can't leave the network (7.3 and the critique): never an app's or Gitberth's shared network, never
    // one of Gitberth's own, never one Docker Compose put it on, and never what isn't its to leave.
    private async Task<string?> LeaveRefusalAsync(Target t, string network, IReadOnlyList<(string Network, string? Alias, string? IPv4Address)> kept,
        IReadOnlyList<NetworkInfo> networks, CancellationToken ct)
    {
        if (Untouchable(t) is { } untouchable) return untouchable;
        if (IsSelf(t)) return network == Shared ? $"Gitberth needs {Shared} to reach your apps." : "Gitberth stays on its networks: leaving one could cut off its own page.";
        if (t.App is not null && network == Shared) return $"{t.Title} always stays on {Shared}: Gitberth made it there, and others reach it there as {t.App.Slug}.";
        if (await ModeRefusalAsync(t, joining: false, ct) is { } mode) return mode;
        var on = t.Networks.Contains(network);
        if (!on && !kept.Any(k => k.Network == network)) return $"{t.Title} isn't on {network}.";
        // Compose would put it back the next time it recreates the container, so the change belongs in its file.
        if (on && t.Labels.GetValueOrDefault(ComposeProjectLabel) is { Length: > 0 } project
            && networks.FirstOrDefault(n => n.Name == network)?.Labels.GetValueOrDefault(ComposeProjectLabel) == project)
            return $"Docker Compose put {t.Title} on {network} (project {project}) and puts it back whenever it recreates it. Change {ComposeService.Of(t.Inspect!)!.File} instead.";
        return null;
    }

    /// <summary>
    /// Where a container's published ports go when it leaves <paramref name="network"/>: they're on the network Docker
    /// picks as its gateway, the highest gateway priority first, then not internal, then by name; null when that isn't
    /// this one, or the container publishes nothing.
    /// </summary>
    private static string? PortsMoveTo(Target t, string network, IReadOnlyList<NetworkInfo> networks)
    {
        var hostConfig = t.Inspect?["HostConfig"];
        var publishes = hostConfig?["PortBindings"] is JsonObject { Count: > 0 } || hostConfig?["PublishAllPorts"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        if (!publishes || t.Inspect?["NetworkSettings"]?["Networks"] is not JsonObject endpoints) return null;
        var order = endpoints
            .Select(e => (Name: e.Key, Priority: ContainerPorts.Number(e.Value, "GwPriority"), Internal: networks.FirstOrDefault(n => n.Name == e.Key)?.Internal == true))
            .OrderByDescending(e => e.Priority).ThenBy(e => e.Internal).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Name).ToList();
        return order.Count > 1 && order[0] == network ? order[1] : null;
    }

    // What joining and leaving never touch (7.3): helpers, a deploy's temporary containers, and what Swarm or Kubernetes run.
    // An app made from an image is never a deploy's, under any name (RecreateRules.MadeFromImage).
    private static string? Untouchable(Target t) =>
        t.Labels.ContainsKey(HostScanHelper.Label) ? "This is a Gitberth helper: it only runs for a moment."
        : t.Labels.ContainsKey(ContainerSpecFactory.BridgeLabel)
          || (t.Labels.TryGetValue("gitberth.app", out var slug) && t.Name != AppDefinition.ContainerNameFor(slug) && !RecreateRules.MadeFromImage(t.Labels))
            ? "This is a deploy's temporary or previous container, which Gitberth removes itself."
        : t.Labels.ContainsKey("com.docker.swarm.task.id") || t.Labels.ContainsKey("com.docker.swarm.service.id")
            ? "Part of a Swarm service: change its networks with docker service update."
        : t.Labels.Keys.Any(k => k.StartsWith("io.kubernetes.", StringComparison.Ordinal) || k.StartsWith("io.x-k8s.", StringComparison.Ordinal))
            ? "Managed by Kubernetes: change its networks there."
        : null;

    // On the host's network, another container's, or none, a container has no networks of its own to join or leave.
    private async Task<string?> ModeRefusalAsync(Target t, bool joining, CancellationToken ct)
    {
        if (t.Mode == "host")
            return joining ? $"{t.Title} uses the host's network, so it can't join other networks." : $"{t.Title} uses the host's network: there are no Docker networks to leave.";
        if (t.Mode == "none") return $"{t.Title} has no network (network mode none).";
        if (!t.Mode.StartsWith("container:", StringComparison.Ordinal)) return null;
        var other = await NetworkOwnerAsync(t.Mode, ct);
        return $"{t.Title} shares the network of {other}, so its networks are {other}'s.";
    }

    // Why the container can't have an own LAN address (own-lan-addresses.md): never Gitberth itself, what the Networks card
    // never touches, or a container without networks of its own. Compose's may: an own address needs no recreate.
    private async Task<string?> LanRefusalAsync(Target t, CancellationToken ct)
    {
        if (t.Inspect is null && t.App is null) return $"There's no container called {t.Name}.";
        if (Untouchable(t) is { } untouchable) return untouchable;
        if (IsSelf(t)) return "Gitberth's own container keeps to the host's address: its page is there.";
        if (t.Mode == "host") return $"{t.Title} uses the host's network, so it's on the host's own address.";
        if (t.Mode == "none") return $"{t.Title} has no network (network mode none).";
        return t.Mode.StartsWith("container:", StringComparison.Ordinal) ? $"{t.Title} shares the network of {await NetworkOwnerAsync(t.Mode, ct)}: its addresses are its." : null;
    }

    // The container's address on the network: the one it keeps there (IPAMConfig), else the one it has while it runs.
    private static string? AddressOn(JsonObject? inspect, string network)
    {
        var endpoint = inspect?["NetworkSettings"]?["Networks"]?[network];
        return ContainerPorts.Str(endpoint?["IPAMConfig"], "IPv4Address") is { Length: > 0 } pinned ? pinned
            : ContainerPorts.Str(endpoint, "IPAddress") is { Length: > 0 } ip ? ip
            : null;
    }

    // What the Networks card says instead of a table, for a container with no networks of its own (5.7).
    private async Task<string?> ModeNoteAsync(Target t, CancellationToken ct) =>
        t.Mode == "host" ? "Uses the host's network: there are no Docker networks to join."
        : t.Mode == "none" ? "No network (network mode none)."
        : t.Mode.StartsWith("container:", StringComparison.Ordinal) ? $"Shares the network of {await NetworkOwnerAsync(t.Mode, ct)}."
        : null;

    // The x of network mode container:x, by name: Compose writes x's full ID there. One that's gone shows as docker ps shows an ID.
    private async Task<string> NetworkOwnerAsync(string mode, CancellationToken ct)
    {
        var other = mode["container:".Length..];
        return await api.InspectContainerAsync(other, ct) is { } owner ? ContainerPorts.Str(owner, "Name").TrimStart('/') : other.Length > 12 ? other[..12] : other;
    }

    // What the card says about one of the container's networks.
    private string? AttachmentNote(Target t, string network, NetworkInfo? info, bool kept, string? selfProject) =>
        network == Shared ? t.App is not null ? "Shared network · always on" : kept ? "Shared network · kept" : "Shared network"
        : network == LanNetwork ? !kept ? "Own LAN address" : t.App is not null ? "Own LAN address · every deploy" : "Own LAN address · kept"
        : kept ? t.App is not null ? "Joined by Gitberth · every deploy" : "Joined by Gitberth · kept"
        : info is null ? null
        : NoteOf(info, Shared, selfProject, members: 1);

    /// <summary>Gitberth's own container: by its ID or hostname (<see cref="Self"/>), else as the gitberth service of the gitberth Compose project.</summary>
    private bool IsSelf(Target t) =>
        t.Inspect is not null
        && ((Self is { Length: > 0 } self && (t.Name == self || (self.Length >= 12 && t.Id.StartsWith(self, StringComparison.Ordinal)))) || IsGitberth(t.Labels));

    // Compose's labels on Gitberth's own container: the gitberth service of the gitberth project.
    private static bool IsGitberth(IReadOnlyDictionary<string, string> labels) =>
        labels.GetValueOrDefault(ComposeProjectLabel) == "gitberth" && labels.GetValueOrDefault("com.docker.compose.service") == "gitberth";

    // What the network's other members will call it: an app by its network name, any other container by its own name.
    private static List<string> Reached(Target t, string? alias) => With([t.App?.Slug ?? t.Name], alias);

    // Every name Docker's DNS answers to for it there: an app's container name too.
    private static List<string> AnswersTo(Target t, string? alias) => t.App is null ? Reached(t, alias) : [.. Reached(t, alias), t.Name];

    // The aliases to join with: an app's network name on every network it's on, as on the shared one.
    private static List<string> AliasesFor(Target t, string? alias) => With(t.App is { } app ? [app.Slug] : [], alias);

    private static List<string> With(List<string> names, string? alias)
    {
        if (alias is not null && !names.Contains(alias)) names.Add(alias);
        return names;
    }

    private static string Busy(Target t) => $"Gitberth is already changing {t.Title}. Try again when that's done.";

    // Neither waits: a port change, a start or stop, or a deploy's swap holding the container is said instead. A Git app's
    // container is its deploys' (AppLocks); any other app's, like any container, is the change engine's (ContainerLocks).
    private async Task<IDisposable?> HoldAsync(Target t) =>
        t.App is { Deployed: true } app ? await _appLocks.TryAcquireAsync(app.Id) : await _containerLocks.TryAcquireAsync(t.Name);

    // A change of the container that isn't settled (by any of its names: its lock is the name's alone), such as one a
    // restart of Gitberth cut short: it carries the container's networks over itself, or puts the original back on the
    // ones it had, so a join or leave meanwhile could be lost or undone. Null for a Git app, which only deploys recreate.
    private async Task<string?> UnsettledAsync(Target t, CancellationToken ct) =>
        changer is null || t.App is { Deployed: true } ? null : await changer.Value.BusyWithAsync(t.Name, ct);

    // Docker 28 (API 1.48) brought gateway priorities; joins on an older one go ahead without.
    private async Task<bool> GatewayPriorityAsync(CancellationToken ct) => await api.ApiVersionAsync(ct) >= DockerCli.GwPriorityApi;

    /// <summary>The networks Gitberth keeps the container on: an app's saved ones, or any other container's kept ones.</summary>
    private async Task<List<(string Network, string? Alias, string? IPv4Address)>> KeptAsync(Target t, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return t.App is { } app
            ? (await db.AppNetworks.AsNoTracking().Where(n => n.AppId == app.Id).ToListAsync(ct)).Select(n => (n.Network, n.Alias, n.IPv4Address)).ToList()
            : (await db.KeptNetworks.AsNoTracking().Where(k => k.ContainerName == t.Name).ToListAsync(ct)).Select(k => (k.Network, k.Alias, k.IPv4Address)).ToList();
    }

    /// <param name="ipv4">Its own address there, pinned; only on gitberth-lan.</param>
    private async Task KeepAsync(Target t, string network, string? alias, string? ipv4, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (t.App is { } app)
        {
            var row = await db.AppNetworks.FirstOrDefaultAsync(n => n.AppId == app.Id && n.Network == network, ct);
            if (row is null) db.AppNetworks.Add(row = new AppNetwork { AppId = app.Id, Network = network });
            row.Alias = alias;
            row.IPv4Address = ipv4;
        }
        else
        {
            var row = await db.KeptNetworks.FindAsync([t.Name, network], ct);
            if (row is null) db.KeptNetworks.Add(row = new KeptNetwork { ContainerName = t.Name, Network = network });
            row.Alias = alias;
            row.IPv4Address = ipv4;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ForgetAsync(Target t, string network, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (t.App is { } app) await db.AppNetworks.Where(n => n.AppId == app.Id && n.Network == network).ExecuteDeleteAsync(ct);
        else await db.KeptNetworks.Where(k => k.ContainerName == t.Name && k.Network == network).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// POST networks/{id}/connect. At gateway priority -1 the network never becomes the container's gateway, so its
    /// published ports and its way out stay where they are.
    /// </summary>
    private Task ConnectAsync(string container, string network, IReadOnlyList<string> aliases, string? ipv4, bool lowPriority, CancellationToken ct)
    {
        var endpoint = new JsonObject();
        if (lowPriority) endpoint["GwPriority"] = -1;
        if (aliases.Count > 0) endpoint["Aliases"] = new JsonArray(aliases.Select(a => (JsonNode?)a).ToArray());
        if (ipv4 is not null) endpoint["IPAMConfig"] = new JsonObject { ["IPv4Address"] = ipv4 };
        return api.SendAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/connect",
            new JsonObject { ["Container"] = container, ["EndpointConfig"] = endpoint }, ct: ct);
    }

    // Not forced: Docker says why it can't rather than tearing the endpoint down regardless.
    private Task DisconnectAsync(string container, string network, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/disconnect",
            new JsonObject { ["Container"] = container, ["Force"] = false }, ct: ct);

    // Joins the container to the network again with what its endpoint there had before: its aliases, fixed address and
    // gateway priority.
    private Task RejoinAsItWasAsync(Target t, string network)
    {
        var endpoint = t.Inspect?["NetworkSettings"]?["Networks"]?[network] as JsonObject;
        var aliases = endpoint?["Aliases"] is JsonArray list ? list.Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
        var ipv4 = ContainerPorts.Str(endpoint?["IPAMConfig"], "IPv4Address") is { Length: > 0 } ip ? ip : null;
        var lowPriority = endpoint?["GwPriority"] is JsonValue p && p.TryGetValue<int>(out var priority) && priority < 0;
        return ConnectAsync(t.Name, network, aliases, ipv4, lowPriority, CancellationToken.None);
    }

    // Puts back a change Docker made that Gitberth couldn't write down. If Docker doesn't take that either, the card shows
    // what Docker has, and the error already says what went wrong.
    private static async Task UndoAsync(Func<Task> undo)
    {
        try
        {
            await undo();
        }
        catch (Exception ex) when (Unanswered(ex))
        {
        }
    }

    // The database didn't take a write: locked or full, say, or the app was deleted meanwhile.
    private static bool Unwritten(Exception ex) => ex is DbException or DbUpdateException;

    private async Task<List<NetworkContainer>> ContainersAsync(CancellationToken ct) =>
        (await api.ListContainersAsync(ct)).Where(c => !HostScanHelper.IsHelper(c.Labels)).ToList();

    // Every name the network's DNS answers for, from each member's inspect (Docker's list leaves aliases out).
    private async Task<HashSet<string>> NamesOnAsync(IEnumerable<NetworkContainer> containers, string network, string? except, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in containers.Where(c => c.Name != except && c.Networks.Any(e => e.Network == network)))
        {
            names.Add(member.Name);
            if (await api.InspectContainerAsync(member.Id, ct) is { } inspect) names.UnionWith(NamesOn(inspect, network));
        }
        return names;
    }

    // Whether the container is on the network, as its inspect shows it now.
    private async Task<bool> OnNetworkAsync(string container, string network, CancellationToken ct) =>
        (await api.InspectContainerAsync(container, ct))?["NetworkSettings"]?["Networks"]?[network] is not null;

    /// <summary>
    /// Who loses the container's names on the network when it leaves (5.7): the other running containers there whose
    /// settings name one of them as a host (<see cref="ContainerService.NamesHost"/>), unless they reach it by that name on
    /// another network they share with it. Only who, never what their settings say; an app's container goes by the app's
    /// name. Null when Docker doesn't say.
    /// </summary>
    private async Task<IReadOnlyList<string>?> UsersAsync(Target t, string network, CancellationToken ct)
    {
        try
        {
            var names = NamesOn(t.Inspect!, network);
            var elsewhere = t.Networks.Where(n => n != network).ToDictionary(n => n, n => NamesOn(t.Inspect!, n), StringComparer.Ordinal);
            Dictionary<string, string> apps;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                apps = (await db.Apps.AsNoTracking().Select(a => new { a.Slug, a.Container, a.Name }).ToListAsync(ct))
                    .ToDictionary(a => a.Container ?? AppDefinition.ContainerNameFor(a.Slug), a => a.Name, StringComparer.Ordinal);
            var users = new List<string>();
            foreach (var member in (await ContainersAsync(ct)).Where(c => c.Name != t.Name && c.Running && c.Networks.Any(e => e.Network == network)))
            {
                if (await api.InspectContainerAsync(member.Id, ct) is not { } inspect) continue;
                // Each setting's value (KEY=value).
                List<string> values = inspect["Config"]?["Env"] is JsonArray env
                    ? env.Select(e => e is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()
                        .Select(e => e.Split('=', 2) is [_, var value] ? value : "").ToList()
                    : [];
                // The names it still reaches the container by, on the other networks they're both on.
                var still = member.Networks.Select(e => e.Network).Where(elsewhere.ContainsKey).SelectMany(n => elsewhere[n]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (names.Any(n => !still.Contains(n) && values.Any(value => ContainerService.NamesHost(value, n))))
                    users.Add(apps.GetValueOrDefault(member.Name) ?? member.Name);
            }
            return users.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (Unanswered(ex))
        {
            return null;
        }
    }

    private static bool Unanswered(Exception ex) => ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException;

    private static string Unreadable(string container, Exception ex) => $"Couldn't read {container} from Docker ({ex.Message}).";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // An extra name in lower case: docker run lower-cases it (Docker 29's --network does) and Docker's DNS ignores case, so
    // a join gives a container the name its app's next deploys give it.
    private static string? AliasOf(string? alias) => NullIfBlank(alias)?.ToLowerInvariant();

    /// <summary>The networks, Gitberth's first, then other user-defined ones, then Docker's own; each with its members.</summary>
    internal static NetworkOverview Build(IReadOnlyList<NetworkInfo> networks, IReadOnlyList<NetworkContainer> containers, IReadOnlyList<NavApp> apps,
        string shared, string? self)
    {
        var bySlug = apps.ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var selfProject = containers.FirstOrDefault(c => IsSelf(c, self))?.Labels.GetValueOrDefault(ComposeProjectLabel);
        var views = networks.Select(n =>
        {
            var members = containers
                .Select(c => (Container: c, Endpoint: c.Networks.FirstOrDefault(e => e.Network == n.Name)))
                .Where(m => m.Endpoint is not null)
                .Select(m => Member(m.Container, m.Endpoint!, bySlug, self))
                .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new NetworkView(n, NoteOf(n, shared, selfProject, members.Count), IsSystem(n), n.Name == shared, members);
        });
        return new NetworkOverview(views.OrderBy(v => Rank(v, selfProject)).ThenBy(v => v.Network.Name, StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>What Gitberth knows a network as, in the Network page's Note column.</summary>
    internal static string? NoteOf(NetworkInfo n, string shared, string? selfProject, int members)
    {
        if (n.Name == shared) return "Gitberth's shared network";
        if (n.Name == LanAddresses.Network) return "Own LAN addresses";
        if (n.Name is "docker_gwbridge" || n.Ingress) return members == 0 ? "Docker Swarm's; not in use" : "Docker Swarm's";
        return n.Name switch
        {
            "bridge" => "Docker's default: no names",
            "host" => "The host's own network",
            "none" => "No network",
            _ when n.Labels.TryGetValue(ComposeProjectLabel, out var project) =>
                project == selfProject ? "Gitberth's compose project" : $"Docker Compose ({project})",
            _ => null,
        };
    }

    internal static bool IsSystem(NetworkInfo n) => n.Name is "bridge" or "host" or "none" or "docker_gwbridge" || n.Ingress;

    /// <summary>
    /// Whether the container is Gitberth's own (<paramref name="self"/> is <see cref="Self"/>): its ID, or its hostname,
    /// which is the container's name or its short ID unless the hostname was set otherwise.
    /// </summary>
    internal static bool IsSelf(NetworkContainer c, string? self) =>
        self is not null && (c.Name == self || (self.Length >= 12 && c.Id.StartsWith(self, StringComparison.Ordinal)));

    /// <summary>
    /// The names containers on the network reach it by: its aliases, then the rest of Docker's DNS names, without its
    /// short ID or a hostname that's only an ID.
    /// </summary>
    internal static List<string> NamesOn(JsonObject inspect, string network)
    {
        var endpoint = inspect["NetworkSettings"]?["Networks"]?[network] as JsonObject;
        var id = inspect["Id"]?.GetValue<string>() ?? "";
        var hostname = inspect["Config"]?["Hostname"]?.GetValue<string>();
        return Strings(endpoint?["Aliases"]).Concat(Strings(endpoint?["DNSNames"]))
            .Where(n => n.Length > 0 && !(ShortId().IsMatch(n) && (id.StartsWith(n, StringComparison.Ordinal) || n == hostname)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        static IEnumerable<string> Strings(JsonNode? node) =>
            node is JsonArray list ? list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>() : [];
    }

    private static NetworkMember Member(NetworkContainer c, NetworkEndpoint endpoint, IReadOnlyDictionary<string, NavApp> bySlug, string? self)
    {
        // Every container is an app with a page of its own, by its name.
        var page = PageOf(c.Name);
        // An own LAN address is the container's while it's stopped too: it's where devices reach it once it starts.
        var ip = endpoint.IpAddress ?? (endpoint.Network == LanAddresses.Network ? endpoint.FixedAddress : null);
        if (IsSelf(c, self)) return new NetworkMember(c.Name, $"{c.Name} (Gitberth)", page, c.Running, ip, []);
        if (c.Labels.TryGetValue("gitberth.app", out var slug) && bySlug.TryGetValue(slug, out var app))
            return c.Name == AppDefinition.ContainerNameFor(slug)
                ? new NetworkMember(c.Name, app.Name, page, c.Running, ip, [])
                : new NetworkMember(c.Name, $"{c.Name} (a deploy of {app.Name})", PageOf(AppDefinition.ContainerNameFor(slug)), c.Running, ip, []);
        return new NetworkMember(c.Name, c.Name, page, c.Running, ip, []);
    }

    /// <summary>A container's page: apps/postgres.</summary>
    public static string PageOf(string container) => "apps/" + Uri.EscapeDataString(container);

    // Gitberth's shared network, Gitberth's own compose network, anyone else's, Docker's default bridge, then Docker's plumbing.
    private static int Rank(NetworkView v, string? selfProject) =>
        v.Shared ? 0
        : selfProject is not null && v.Network.Labels.GetValueOrDefault(ComposeProjectLabel) == selfProject ? 1
        : !v.System ? 2
        : v.Network.Name == "bridge" ? 3
        : v.Network.Name is "host" ? 5
        : v.Network.Name is "none" ? 6
        : 4;

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex ShortId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex AliasName();
}
