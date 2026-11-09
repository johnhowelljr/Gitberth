using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

public enum DomainFix { None, Sync, PointDnsHere, ServeAllPaths }

/// <param name="Warning">Something to know about a domain that works, such as a name the free certificate doesn't cover.</param>
/// <param name="Main">The app's main domain: the address Gitberth tells the app it has (see <see cref="DomainService.MainOf"/>).</param>
public sealed record DomainState(string Hostname, bool Working, string Summary, DomainFix Fix, string? Warning = null, bool Main = false);

/// <param name="Problem">Why none of the app's domains can be checked or routed right now.</param>
/// <param name="Enabled">Whether domains are turned on in Settings. Off, nothing was checked.</param>
public sealed record DomainsOverview(bool TokenSaved, TunnelInfo? Tunnel, string? Problem, IReadOnlyList<DomainState> Domains, bool Enabled = true);

public sealed record SyncReport(IReadOnlyList<DomainNote> Notes)
{
    public static SyncReport Nothing { get; } = new([]);

    /// <summary>The tunnel configuration's version after Gitberth changed it, which cloudflared logs once it runs it; null when nothing changed.</summary>
    public long? TunnelVersion { get; init; }

    /// <summary>Where Gitberth sends each running app's domains, by app, whether or not the tunnel could be changed.</summary>
    public IReadOnlyDictionary<int, string>? Targets { get; init; }

    /// <summary>The container each app's domains go to, by app: its own, or a deploy's new version next to it.</summary>
    public IReadOnlyDictionary<int, string>? Containers { get; init; }

    public static SyncReport Failed(string why) => new([new DomainNote(null, why, Problem: true)]);

    /// <summary>The notes about these domains, plus the ones about routing in general.</summary>
    public SyncReport For(IEnumerable<string> hostnames)
    {
        var set = hostnames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SyncReport(Notes.Where(n => n.Hostname is null || set.Contains(n.Hostname)).ToList()) { TunnelVersion = TunnelVersion, Targets = Targets, Containers = Containers };
    }
}

/// <summary>A tunnel configuration Gitberth wrote: its version, and when.</summary>
public sealed record TunnelChange(long Version, DateTime At);

/// <param name="Moved">Every one of the app's domains now goes to the container asked for.</param>
/// <param name="Version">The tunnel configuration that does it, for <see cref="DomainService.WaitForTunnelAsync"/>; null when nothing had to change.</param>
public sealed record RouteSwitch(bool Moved, long? Version, SyncReport Report);

/// <summary>What adding a domain did.</summary>
/// <param name="Error">Why it wasn't added; null when it was.</param>
/// <param name="NeedsHostBinding">
/// It wasn't added: cloudflared runs on the host's network, which reaches apps only through a host port, and the app's web
/// port is Internal. <see cref="DomainService.AddAndPublishAsync"/> publishes the web port on the host only and adds it.
/// </param>
/// <param name="HostPort">The host port the web port was published on (host only) to carry the domain; null when nothing was published.</param>
/// <param name="DeploymentId">The deploy without a build that publishes it now; null when none started (nothing was published, or the app is stopped and takes it when it starts).</param>
/// <param name="Plan">
/// An app that isn't deployed from a repo: the change to its container that publishes the web port on <paramref name="HostPort"/>,
/// for the change dialog. Nothing was added or changed yet: once that change is made, <see cref="DomainService.AddWhenPublishedAsync"/>
/// adds the domain.
/// </param>
public sealed record DomainAdd(string? Error, SyncReport Report, bool NeedsHostBinding = false, int? HostPort = null, int? DeploymentId = null, SettingsPlan? Plan = null)
{
    public void Deconstruct(out string? error, out SyncReport report) => (error, report) = (Error, Report);
}

/// <summary>
/// An app's domains, served through the Cloudflare Tunnel on this host the way Vercel serves a project's domains: Gitberth
/// keeps one tunnel route per domain, for every path, to the web port of the app's running container, and a DNS
/// record that sends the domain to the tunnel. Each domain remembers where Gitberth sent it, which tells its rules from
/// anyone else's. Every change goes through one lock, so two never race on the tunnel.
/// It's optional: until domains are turned on in Settings, Gitberth never contacts Cloudflare or reads the tunnel container's settings.
/// </summary>
/// <param name="hostPorts">Picks the host port when adding a domain publishes the web port. Lazy: the map reads the tunnel's routes from here.</param>
/// <param name="actions">Recreates an app to publish its web port. Lazy: deploys route domains through here.</param>
/// <param name="appSettings">Plans publishing the web port of an app that isn't deployed from a repo. Lazy: it depends on this class.</param>
public sealed partial class DomainService(IDbContextFactory<GitberthDb> dbFactory, SettingsService settings, CloudflareApi cloudflare, TunnelLocator tunnels, DockerCli docker,
    ILogger<DomainService> logger, Lazy<HostPortMap>? hostPorts = null, Lazy<AppActions>? actions = null, Lazy<AppSettingsService>? appSettings = null)
{
    public const string TurnedOff = "Domains are turned off in Settings, so Gitberth leaves Cloudflare alone.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Cancelled when domains are turned off: a check that runs without the lock stops talking to Cloudflare and cloudflared.
    private CancellationTokenSource _whileOn = new();
    // While a deploy runs an app's new version next to the old one: the container its domains go to instead of its own.
    private readonly ConcurrentDictionary<int, string> _routeTo = new();

    /// <summary>How often <see cref="WaitForTunnelAsync"/> reads cloudflared's log.</summary>
    internal TimeSpan TunnelPoll { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The newest tunnel configuration Gitberth wrote since it started: what cloudflared should be running now.</summary>
    public TunnelChange? LastTunnelChange { get; private set; }

    public async Task<bool> EnabledAsync(CancellationToken ct = default) => await settings.GetAsync(SettingKeys.DomainsEnabled, ct) == "true";

    /// <summary>
    /// Turns domains on or off. Turning them off changes nothing in Cloudflare: the tunnel, DNS, token and domains stay as
    /// they are, and a check still running stops. Turned back on with a token saved, Gitberth picks up where it left off: it
    /// syncs, which also finishes the removals saved while domains were off.
    /// </summary>
    public async Task<SyncReport> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        // Under the lock: once this returns, no sync is still running against the old setting.
        await _gate.WaitAsync(ct);
        try
        {
            await settings.SetAsync(SettingKeys.DomainsEnabled, enabled ? "true" : null, ct);
            if (!enabled)
            {
                // After the setting is saved, so a check that reads it after this sees it off, and one that read it before is stopped.
                Interlocked.Exchange(ref _whileOn, new CancellationTokenSource()).Cancel();
                return SyncReport.Nothing;
            }
            return await TokenSavedAsync(ct) ? await SyncCoreAsync(widen: null, ct) : SyncReport.Nothing;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The tunnel Gitberth manages, for display. Looking never changes anything.</summary>
    public async Task<(TunnelInfo? Tunnel, string? Problem)> FindTunnelAsync(CancellationToken ct = default)
    {
        // Taken before the setting is read: see SetEnabledAsync.
        using var whileOn = CancellationTokenSource.CreateLinkedTokenSource(ct, _whileOn.Token);
        try
        {
            return await EnabledAsync(ct) ? await ResolveTunnelAsync(connect: false, whileOn.Token) : (null, TurnedOff);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, TurnedOff);
        }
    }

    public async Task<bool> TokenSavedAsync(CancellationToken ct = default) => await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not null;

    public async Task<List<string>> HostnamesAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Domains.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.Id).Select(d => d.Hostname).ToListAsync(ct);
    }

    /// <summary>
    /// The app's main domain: the one chosen as main, else the shortest, the way Vercel picks a project's production URL
    /// (of two the same length, the one added first). Null when it has none.
    /// </summary>
    public static string? MainOf(IEnumerable<CustomDomain> domains)
    {
        // One being added has no ID yet, and comes last, as it will once it's saved.
        var list = domains.OrderBy(d => d.Id == 0 ? int.MaxValue : d.Id).ToList();
        return (list.FirstOrDefault(d => d.Main) ?? list.OrderBy(d => d.Hostname.Length).FirstOrDefault())?.Hostname;
    }

    /// <summary>
    /// The app's main domain (see <see cref="MainOf"/>), or null when it has none. Only reads Gitberth's own records, and
    /// counts while domains are turned off too: their routes stay in the tunnel, so the app is still reached there.
    /// </summary>
    public async Task<string?> MainHostnameAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return MainOf(await db.Domains.AsNoTracking().Where(d => d.AppId == appId).ToListAsync(ct));
    }

    /// <summary>
    /// Makes a domain the app's main one. The app is told its address at build time too (NEXT_PUBLIC_VERCEL_URL, say), so
    /// a running app is marked as needing a new build. Returns why it couldn't, or null.
    /// </summary>
    public async Task<string?> SetMainAsync(int appId, string hostname, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return TurnedOff;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == appId, ct);
            if (app?.Domains.FirstOrDefault(d => d.Hostname == hostname) is not { } chosen) return $"{hostname} isn't one of this app's domains.";
            var before = MainOf(app.Domains);
            foreach (var d in app.Domains) d.Main = d == chosen;
            MarkIfAddressChanged(app, before, app.Domains);
            await db.SaveChangesAsync(ct);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A running app built with its old address needs a new build to get the new one.
    private static void MarkIfAddressChanged(AppDefinition app, string? before, IEnumerable<CustomDomain> now)
    {
        if (app.CurrentImage is not null && MainOf(now) != before) app.RebuildPending = true;
    }

    /// <summary>Checks the token against the tunnel on this host, saves both, and routes every app's domains.</summary>
    public async Task<(string? Error, SyncReport Report, TunnelInfo? Tunnel)> SaveTokenAsync(string token, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return (TurnedOff, SyncReport.Nothing, null);
            var scan = await tunnels.FindAllAsync(ct);
            var savedId = await settings.GetAsync(SettingKeys.CloudflareTunnelId, ct);
            var ids = scan.Tunnels.Select(t => t.TunnelId).Distinct().ToList();
            var chosen = ids.Count == 1 ? ids[0] : ids.FirstOrDefault(id => id == savedId);
            if (chosen is null)
            {
                return (ids.Count > 1
                    ? $"More than one Cloudflare tunnel runs on this host ({string.Join(", ", scan.Tunnels.Select(t => t.ContainerName))}). Stop the ones Gitberth shouldn't manage, then save the token again."
                    : NoTunnel(scan), SyncReport.Nothing, null);
            }
            var found = scan.Tunnels.First(t => t.TunnelId == chosen);
            // The token is checked before anything about cloudflared changes.
            try
            {
                await cloudflare.VerifyTokenAsync(token, found.AccountId, ct);
                await cloudflare.GetTunnelConfigAsync(token, found.AccountId, found.TunnelId, ct);
            }
            catch (CloudflareApiException ex)
            {
                return (ex.Message, SyncReport.Nothing, found);
            }
            var (tunnel, problem) = await ReachableAsync(found, connect: true, ct);
            if (tunnel is null) return (problem, SyncReport.Nothing, found);
            await settings.SetSecretAsync(SettingKeys.CloudflareToken, token, ct);
            await settings.SetAsync(SettingKeys.CloudflareTunnelId, tunnel.TunnelId, ct);
            return (null, await SyncCoreAsync(widen: null, ct), tunnel);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await settings.SetSecretAsync(SettingKeys.CloudflareToken, null, ct);
            await settings.SetAsync(SettingKeys.CloudflareTunnelId, null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a domain to the app and routes it. When cloudflared runs on the host's network and the app's web port is
    /// Internal, the tunnel couldn't reach the app, so nothing is added and the result says NeedsHostBinding: that's
    /// <see cref="AddAndPublishAsync"/>'s to do.
    /// </summary>
    public async Task<DomainAdd> AddAsync(int appId, string input, CancellationToken ct = default)
    {
        var hostname = DomainNames.Normalize(input);
        if (DomainNames.Problem(hostname) is { } problem) return new(problem, SyncReport.Nothing);
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return new(TurnedOff, SyncReport.Nothing);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await TakenAsync(db, appId, hostname!, ct) is { } taken) return new(taken, SyncReport.Nothing);
                var app = await db.Apps.Include(a => a.Domains).Include(a => a.Ports).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == appId, ct);
                if (app is null) return new("This app doesn't exist any more.", SyncReport.Nothing);
                if (PortRules.WebOf(app.Ports) is { Access: PortAccess.Internal } && await TunnelOnHostAsync(ct))
                    return new(PortRules.NeedsHostPort(hostname!), SyncReport.Nothing, NeedsHostBinding: true);
                await AddDomainAsync(db, app, hostname!, ct);
                await db.SaveChangesAsync(ct);
            }
            return new(null, await SyncCoreAsync(widen: null, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a domain to an app whose web port is Internal while cloudflared runs on the host's network, which reaches apps
    /// only through a host port (6d): the web port is published on the host only (the LAN still can't reach it), on the
    /// port the host-port map suggests, the domain is saved, and the app is recreated once to take the port when it runs;
    /// a stopped app takes it when it starts. The deploy's domain sync then writes the route. When the tunnel can reach
    /// the web port as it is, the domain is just added (<see cref="AddAsync"/>).
    /// Any other app's ports are its container's, so nothing is saved here: the result carries the change engine's plan
    /// for publishing the web port (<see cref="DomainAdd.Plan"/>), the owner confirms it in the change dialog, and
    /// <see cref="AddWhenPublishedAsync"/> adds the domain once the port is published.
    /// </summary>
    public async Task<DomainAdd> AddAndPublishAsync(int appId, string input, CancellationToken ct = default)
    {
        var hostname = DomainNames.Normalize(input);
        if (DomainNames.Problem(hostname) is { } problem) return new(problem, SyncReport.Nothing);
        var map = hostPorts?.Value ?? throw new InvalidOperationException("Publishing a web port needs the host-port map.");
        int port;
        // An app that isn't deployed from a repo: its container, its ports as Gitberth has them, and its web port.
        (string Container, string Loaded, int WebPort)? other = null;
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return new(TurnedOff, SyncReport.Nothing);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await TakenAsync(db, appId, hostname!, ct) is { } taken) return new(taken, SyncReport.Nothing);
                var app = await db.Apps.Include(a => a.Domains).Include(a => a.Ports).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == appId, ct);
                if (app is null) return new("This app doesn't exist any more.", SyncReport.Nothing);
                if (PortRules.WebOf(app.Ports) is not { } web) return new("Add a web port in Ports first: domains go to the web port.", SyncReport.Nothing);
                if (web.Access != PortAccess.Internal || !await TunnelOnHostAsync(ct))
                {
                    await AddDomainAsync(db, app, hostname!, ct);
                    await db.SaveChangesAsync(ct);
                    return new(null, await SyncCoreAsync(widen: null, ct));
                }
                try
                {
                    // The app's other host ports are its own, so the map doesn't count them: they're left out here.
                    port = await map.SuggestAsync(web.ContainerPort, "tcp", new HostPortOwner(app.ContainerName, app.Id),
                        app.Ports.Where(p => p != web && p.HostPort is not null).Select(p => p.HostPort!.Value), ct);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
                {
                    return new($"Docker didn't say which host ports are in use ({ex.Message}). Try again in a moment.", SyncReport.Nothing);
                }
                if (!app.Deployed) other = (app.ContainerName, AppSettingsModel.PortsKey(app.Ports), web.ContainerPort);
                else
                {
                    (web.Access, web.HostPort, web.HostIp) = (PortAccess.HostOnly, port, null);
                    if (app.CurrentImage is not null) app.ConfigChangePending = true;
                    app.UpdatedAt = DateTime.UtcNow;
                    await AddDomainAsync(db, app, hostname!, ct);
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        // Planned without the lock: the plan reads the app as the catalog has it, domains included.
        if (other is { } container) return await PlanPublishAsync(container.Container, container.Loaded, container.WebPort, port, ct);
        map.ForgetSaved();
        // Its route can only be written once the container publishes the port: the deploy's domain sync does that.
        var deploymentId = actions is null ? null : await actions.Value.ApplyIfRunningAsync(appId, ct);
        return new(null, SyncReport.Nothing, HostPort: port, DeploymentId: deploymentId);
    }

    // Any other app's web port is published by a change to its container: the change engine's plan for it, from the
    // container's ports as they are, with the web port on the host only. Nothing is saved.
    private async Task<DomainAdd> PlanPublishAsync(string container, string loaded, int webPort, int port, CancellationToken ct)
    {
        var service = appSettings?.Value ?? throw new InvalidOperationException("Publishing an app's web port needs the settings service.");
        if (await service.LoadAsync(container, ct) is not { } view) return new($"There's no app called {container} any more.", SyncReport.Nothing);
        var rows = view.Container.Ports
            .Select(r => new PortRow { ContainerPort = r.ContainerPort, Protocol = r.Protocol, Access = r.Access, HostPort = r.HostPort, HostIp = r.HostIp })
            .ToList();
        if (rows.FirstOrDefault(r => r.ContainerPort == webPort && PortRules.ProtocolOf(r.Protocol) == "tcp") is not { } web) rows.Add(web = new PortRow { ContainerPort = webPort });
        (web.Access, web.HostPort, web.HostIp, web.IsWeb) = (PortAccess.HostOnly, port, null, true);
        var plan = await service.PlanPortsAsync(container, view.ContainerId, rows, loaded, ct);
        return new(null, SyncReport.Nothing, HostPort: port, Plan: plan);
    }

    /// <summary>
    /// Adds a domain once the change that publishes the app's web port (<see cref="DomainAdd.Plan"/>) is over. When it
    /// published the port, the domain is saved and routed (the tunnel goes to the port as the container publishes it);
    /// when it didn't (it failed, or was rolled back), nothing is added and the result says why.
    /// </summary>
    /// <param name="publishing">The change's completion (<see cref="PortChangeJob.Completion"/>).</param>
    public async Task<DomainAdd> AddWhenPublishedAsync(int appId, string input, Task<PortChangeResult> publishing, CancellationToken ct = default)
    {
        var hostname = DomainNames.Normalize(input);
        if (DomainNames.Problem(hostname) is { } problem) return new(problem, SyncReport.Nothing);
        var published = await publishing.WaitAsync(ct);
        if (!published.Ok) return new($"{hostname} wasn't added: its web port wasn't published. {published.Message}", SyncReport.Nothing);
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return new(TurnedOff, SyncReport.Nothing);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await TakenAsync(db, appId, hostname!, ct) is { } taken) return new(taken, SyncReport.Nothing);
                var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == appId, ct);
                if (app is null) return new("This app doesn't exist any more.", SyncReport.Nothing);
                await AddDomainAsync(db, app, hostname!, ct);
                await db.SaveChangesAsync(ct);
            }
            return new(null, await SyncCoreAsync(widen: null, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Why the domain can't be added to the app: it's already this app's, or another's.
    private static async Task<string?> TakenAsync(GitberthDb db, int appId, string hostname, CancellationToken ct)
    {
        if (await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Hostname == hostname, ct) is not { } owner) return null;
        var name = await db.Apps.AsNoTracking().Where(a => a.Id == owner.AppId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        return owner.AppId == appId ? $"{hostname} is already one of this app's domains." : $"{hostname} already belongs to {name ?? "another app"}.";
    }

    private static async Task AddDomainAsync(GitberthDb db, AppDefinition app, string hostname, CancellationToken ct)
    {
        var before = MainOf(app.Domains);
        var domain = new CustomDomain { AppId = app.Id, Hostname = hostname };
        // Still on its way out from another app: its old route is this domain's to replace.
        if (await db.DomainRemovals.FindAsync([hostname], ct) is { } removal)
        {
            domain.Service = removal.Service;
            db.DomainRemovals.Remove(removal);
        }
        app.Domains.Add(domain);
        MarkIfAddressChanged(app, before, app.Domains);
    }

    // cloudflared on the host's network reaches apps only through a host port; on the shared network, by name.
    private async Task<bool> TunnelOnHostAsync(CancellationToken ct) => (await ResolveTunnelAsync(connect: false, ct)).Tunnel?.HostNetwork == true;

    /// <summary>
    /// Takes a domain off the app. The removal is saved with it, so its route and the DNS record Gitberth added come out
    /// even when Cloudflare can't be reached right now: every sync tries again (and <see cref="RemovalRetry"/> every 15
    /// minutes while one is pending), and nothing takes the route over meanwhile.
    /// </summary>
    public async Task<SyncReport> RemoveAsync(int appId, string hostname, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return SyncReport.Failed(TurnedOff);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == appId, ct);
                if (app?.Domains.FirstOrDefault(d => d.Hostname == hostname) is not { } row) return SyncReport.Nothing;
                var before = MainOf(app.Domains);
                db.Domains.Remove(row);
                await AddRemovalAsync(db, row, ct);
                MarkIfAddressChanged(app, before, app.Domains.Where(d => d != row));
                await db.SaveChangesAsync(ct);
            }
            return await SyncCoreAsync(widen: null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the app's row, saving the removal of its domains in the same transaction, so their routes can't stay on a
    /// port the next new app may be given. Then takes the domains out of the tunnel, or, while domains are turned off,
    /// leaves that for when they're turned back on.
    /// </summary>
    public async Task<SyncReport> DeleteAppAsync(int appId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            List<string> hostnames;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == appId, ct);
                if (app is null) return SyncReport.Nothing;
                hostnames = app.Domains.Select(d => d.Hostname).ToList();
                foreach (var domain in app.Domains) await AddRemovalAsync(db, domain, ct);
                db.Apps.Remove(app);
                await db.SaveChangesAsync(ct);
            }
            return hostnames.Count == 0 || !await EnabledAsync(ct) ? SyncReport.Nothing : (await SyncCoreAsync(widen: null, ct)).For(hostnames);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task AddRemovalAsync(GitberthDb db, CustomDomain domain, CancellationToken ct)
    {
        if (await db.DomainRemovals.FindAsync([domain.Hostname], ct) is { } existing) existing.Service = domain.Service ?? existing.Service;
        else db.DomainRemovals.Add(new DomainRemoval { Hostname = domain.Hostname, Service = domain.Service });
    }

    /// <summary>Opens a domain kept on its path filters to every path of the app, when someone chooses to.</summary>
    public async Task<SyncReport> ServeAllPathsAsync(int appId, string hostname, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return SyncReport.Failed(TurnedOff);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var row = await db.Domains.FirstOrDefaultAsync(d => d.AppId == appId && d.Hostname == hostname, ct);
                if (row is null) return SyncReport.Nothing;
                row.KeepPaths = false;
                await db.SaveChangesAsync(ct);
            }
            return await SyncCoreAsync(widen: hostname, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tries the pending removals again, for <see cref="RemovalRetry"/>: a sync like any other, under the same lock, when
    /// domains are on, a token is saved and a domain taken off an app still has its route or DNS record to remove. Otherwise
    /// it asks nothing of Cloudflare or the tunnel's container. Null when there was nothing to try.
    /// </summary>
    public async Task<SyncReport?> RetryRemovalsAsync(CancellationToken ct = default)
    {
        // Gitberth's own records first, without the lock: nearly always there's nothing pending.
        if (!await RemovalsPendingAsync(ct)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            // Again under the lock: a sync may have finished them meanwhile, or domains been turned off.
            if (!await EnabledAsync(ct) || !await TokenSavedAsync(ct) || !await RemovalsPendingAsync(ct)) return null;
            return await SyncCoreAsync(widen: null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RemovalsPendingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DomainRemovals.AnyAsync(ct);
    }

    /// <summary>Makes the tunnel and DNS match Gitberth. See <see cref="SyncCoreAsync"/>.</summary>
    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await SyncCoreAsync(widen: null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// With the lock held:
    /// <list type="bullet">
    /// <item>takes over dashboard routes that already go exactly where Gitberth would send them;</item>
    /// <item>keeps a domain someone narrowed in the dashboard narrowed, except the one being widened on request (<paramref name="widen"/>);</item>
    /// <item>sends every domain of a running app to its web port on its container: one rule for every path, or its own
    /// path rules re-pointed when it keeps them;</item>
    /// <item>adds missing DNS records, and finishes pending removals.</item>
    /// </list>
    /// A stopped app's routes stay as they are. DNS records that point somewhere else are only reported;
    /// <see cref="PointDnsHereAsync"/> replaces them when asked.
    /// </summary>
    private async Task<SyncReport> SyncCoreAsync(string? widen, CancellationToken ct)
    {
        var notes = new List<DomainNote>();
        long? version = null;
        IReadOnlyDictionary<int, string>? targets = null;
        IReadOnlyDictionary<int, string>? containers = null;
        try
        {
            if (!await EnabledAsync(ct)) return SyncReport.Failed(TurnedOff);
            if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token)
                return SyncReport.Failed("Domains aren't routed: Gitberth has no Cloudflare token. Add one in Settings.");
            var (tunnel, problem) = await ResolveTunnelAsync(connect: true, ct);
            if (tunnel is null) return SyncReport.Failed("Domains aren't routed: " + problem);
            var config = await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, ct);
            var ingress = config["ingress"] as JsonArray ?? [];

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var apps = await db.Apps.Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().ToListAsync(ct);
            var removals = await db.DomainRemovals.ToListAsync(ct);
            var picked = await ServicesAsync(apps, tunnel, ct);
            var services = picked.ToDictionary(p => p.Key, p => p.Value.Service);
            targets = services;
            containers = picked.ToDictionary(p => p.Key, p => p.Value.Container);

            var known = apps.SelectMany(a => a.Domains).Select(d => d.Hostname).Concat(removals.Select(r => r.Hostname)).ToHashSet();
            // Never through a deploy's temporary container: whatever else the tunnel sends to its spare port isn't the app's.
            var adoptable = services.Where(s => !_routeTo.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value);
            var mainBefore = apps.ToDictionary(a => a.Id, a => MainOf(a.Domains));
            foreach (var (hostname, appId, keepPaths, service) in TunnelRoutes.FindAdoptable(ingress, apps, adoptable, known))
            {
                var app = apps.First(a => a.Id == appId);
                app.Domains.Add(new CustomDomain { AppId = appId, Hostname = hostname, KeepPaths = keepPaths, Service = service });
                MarkIfAddressChanged(app, mainBefore[app.Id], app.Domains);
                notes.Add(new DomainNote(hostname, keepPaths
                    ? $"Took over {hostname}. The tunnel sends only some of its addresses to {app.Name}, and it stays that way until you choose to serve every path on {app.Name}'s Network tab."
                    : $"Took over {hostname}: the tunnel already sent it to {app.Name}."));
            }

            var routes = new List<DomainRoute>();
            var drops = removals.Where(r => r.Service is not null).Select(r => new DomainDrop(r.Hostname, r.Service!)).ToList();
            foreach (var app in apps.Where(a => a.Domains.Count > 0))
            {
                if (PortRules.WebOf(app.Ports) is null)
                {
                    // Without a web port its routes go, so they can't follow a port to whichever app gets it next.
                    foreach (var d in app.Domains)
                    {
                        if (d.Service is not null) drops.Add(new DomainDrop(d.Hostname, d.Service));
                        notes.Add(new DomainNote(d.Hostname, $"{d.Hostname} isn't routed: {app.Name} has no web port.", Problem: true));
                    }
                    continue;
                }
                // Not running: its routes stay as they are until it is, and move with it once it runs.
                if (!services.TryGetValue(app.Id, out var service)) continue;
                foreach (var d in app.Domains)
                {
                    if (!d.KeepPaths && !string.Equals(d.Hostname, widen, StringComparison.OrdinalIgnoreCase)
                        && TunnelRoutes.Narrowed(ingress, d.Hostname, app, service, d.Service))
                    {
                        d.KeepPaths = true;
                        notes.Add(new DomainNote(d.Hostname, $"{d.Hostname} sends only some addresses to {app.Name} now, as it was changed in Cloudflare. Gitberth keeps it that way until you choose to serve every path."));
                    }
                    routes.Add(new DomainRoute(d.Hostname, app.Id, service, d.Service, d.KeepPaths));
                }
            }
            var plan = TunnelRoutes.Plan(ingress, apps, routes, drops);
            if (plan.Changed)
            {
                config["ingress"] = plan.Ingress;
                version = await cloudflare.PutTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, config, ct);
                if (version is long written) LastTunnelChange = new TunnelChange(written, DateTime.UtcNow);
                notes.AddRange(plan.Notes);
            }
            // Its rules now go there, so that's where Gitberth sent it: remembered as the tunnel writes it, so a later removal finds it.
            foreach (var route in routes)
            {
                var app = apps.First(a => a.Id == route.AppId);
                app.Domains.First(d => d.Hostname == route.Hostname).Service = TunnelRoutes.ServiceOf(plan.Ingress, route.Hostname, route.Service, app) ?? route.Service;
            }
            await db.SaveChangesAsync(ct);

            // Each domain's DNS on its own: one Cloudflare refuses doesn't stop the rest.
            var zones = new List<CloudflareZone>();
            var served = apps.Where(a => PortRules.WebOf(a.Ports) is not null).SelectMany(a => a.Domains).Select(d => d.Hostname).ToHashSet();
            foreach (var hostname in served.Order(StringComparer.Ordinal))
            {
                try
                {
                    var dns = await CheckDnsAsync(token, tunnel, hostname, zones, notes, ct);
                    if (!dns.Ok) notes.Add(new DomainNote(hostname, $"{hostname}: {dns.Summary}", Problem: true));
                }
                catch (CloudflareApiException ex)
                {
                    notes.Add(new DomainNote(hostname, $"{hostname}: {ex.Message}", Problem: true));
                }
            }

            // A removal is done once Gitberth's route is gone (the PUT above) and, when nothing in the tunnel uses the
            // name any more, the DNS record Gitberth added for it.
            foreach (var removal in removals)
            {
                if (served.Contains(removal.Hostname))
                {
                    db.DomainRemovals.Remove(removal);
                    continue;
                }
                try
                {
                    if (!TunnelRoutes.HasRules(plan.Ingress, removal.Hostname) && await ZoneAsync(token, tunnel, removal.Hostname, zones, ct) is { } zone)
                    {
                        var ours = (await cloudflare.GetDnsRecordsAsync(token, zone.Id, removal.Hostname, ct))
                            .Where(r => PointsAtTunnel(r, tunnel) && CloudflareApi.AddedByUs(r.Comment));
                        foreach (var record in ours)
                        {
                            await cloudflare.DeleteDnsRecordAsync(token, zone.Id, record.Id, removal.Hostname, ct);
                            notes.Add(new DomainNote(removal.Hostname, $"Removed the DNS record Gitberth added for {removal.Hostname}"));
                        }
                    }
                    db.DomainRemovals.Remove(removal);
                }
                catch (CloudflareApiException ex)
                {
                    notes.Add(new DomainNote(removal.Hostname, $"{removal.Hostname}: {ex.Message} Gitberth tries again every {RemovalRetry.Every.TotalMinutes:0} minutes until it's done.", Problem: true));
                }
            }
            await db.SaveChangesAsync(ct);
            return new SyncReport(notes) { TunnelVersion = version, Targets = targets, Containers = containers };
        }
        catch (CloudflareApiException ex)
        {
            // What was done before the failure still happened, so it's reported along with it.
            notes.Add(new DomainNote(null, ex.Message, Problem: true));
            return new SyncReport(notes) { TunnelVersion = version, Targets = targets, Containers = containers };
        }
        catch (DbUpdateException)
        {
            notes.Add(new DomainNote(null, "Another change to the domains was saved at the same moment. Check again in a few seconds.", Problem: true));
            return new SyncReport(notes) { TunnelVersion = version, Targets = targets, Containers = containers };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug here must not stop routing and removals for every app; it's reported and logged instead.
            logger.LogError(ex, "Routing domains failed");
            notes.Add(new DomainNote(null, $"Gitberth couldn't work out the tunnel's routes ({ex.Message}). Nothing was changed.", Problem: true));
            return new SyncReport(notes) { TunnelVersion = version, Targets = targets, Containers = containers };
        }
    }

    /// <summary>
    /// Sends the app's domains to another of its containers (a deploy's new version, running next to the old one on a spare
    /// port) and keeps them there through every other sync, until <see cref="RouteHomeAsync"/>.
    /// </summary>
    public Task<RouteSwitch> RouteToAsync(int appId, string container, CancellationToken ct = default) => SwitchAsync(appId, container, ct);

    /// <summary>Sends the app's domains back to its own container.</summary>
    public Task<RouteSwitch> RouteHomeAsync(int appId, CancellationToken ct = default) => SwitchAsync(appId, null, ct);

    private async Task<RouteSwitch> SwitchAsync(int appId, string? container, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (container is null) _routeTo.TryRemove(appId, out _);
            else _routeTo[appId] = container;
            var report = await SyncCoreAsync(widen: null, ct);
            // Only what the tunnel was changed to counts: each domain remembers where Gitberth sent it once Cloudflare took
            // it. And only to the container asked for: when it isn't running, the domains stay on the app's own.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var app = await db.Apps.AsNoTracking().Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == appId, ct);
            var moved = report.Targets?.GetValueOrDefault(appId) is { } target && app is { Domains.Count: > 0 }
                && report.Containers?.GetValueOrDefault(appId) == (container ?? app.ContainerName)
                && app.Domains.All(d => TunnelRoutes.SameService(d.Service, target, app));
            return new RouteSwitch(moved, report.TunnelVersion, report);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits until cloudflared runs the tunnel configuration with this version or a later one: it logs "Updated to new
    /// configuration … version=N" when it takes one. A deploy waits for that before it stops the container visitors were
    /// sent away from. False when cloudflared didn't say so in time, or its log can't be read.
    /// </summary>
    public async Task<bool> WaitForTunnelAsync(long version, DateTime since, TimeSpan timeout, CancellationToken ct = default)
    {
        var (tunnel, _) = await ResolveTunnelAsync(connect: false, ct);
        if (tunnel is null) return false;
        var until = DateTime.UtcNow + timeout;
        // A little early, in case the two clocks differ; an older version in the log never counts.
        var from = new DateTimeOffset(since.AddSeconds(-10), TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        while (true)
        {
            var r = await docker.RunAsync(["logs", "--since", from, tunnel.ContainerName], new ProcessOptions { MaxCapture = 512 * 1024 }, ct);
            if (r.Ok && RunsVersion(r.StdOut + "\n" + r.StdErr, version)) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TunnelPoll, ct);
        }
    }

    /// <summary>
    /// Whether cloudflared's log says it took the tunnel configuration with this version, or a later one. Its console
    /// log may be coloured (when it runs with a terminal) or JSON (--output json).
    /// </summary>
    internal static bool RunsVersion(string log, long version) =>
        AnsiEscape().Replace(log, "").Split('\n').Any(line =>
            line.Contains("Updated to new configuration", StringComparison.Ordinal)
            && LoggedVersion().Match(line) is { Success: true } m
            && long.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            && v >= version);

    // The console form ends with version=N (the config before it is quoted); the JSON form has "version":N anywhere.
    [GeneratedRegex(@"\bversion=(\d+)\s*$|""version"":\s*(\d+)")]
    private static partial Regex LoggedVersion();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    /// <summary>
    /// The host ports that any of the tunnel's rules goes to now, Gitberth's or not: a deploy's temporary container must
    /// not take one, or it would get those visitors. Empty when the tunnel can't be read.
    /// </summary>
    public async Task<HashSet<int>> TunnelHostPortsAsync(CancellationToken ct = default)
    {
        try
        {
            if (!await EnabledAsync(ct) || await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return [];
            var (tunnel, _) = await ResolveTunnelAsync(connect: false, ct);
            if (tunnel is null) return [];
            var config = await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, ct);
            return TunnelRoutes.HostPorts(config["ingress"] as JsonArray ?? []);
        }
        catch (CloudflareApiException)
        {
            return [];
        }
    }

    /// <summary>Whether any app has a domain, or a removed one is still on its way out: then deploys check the tunnel.</summary>
    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Domains.AnyAsync(ct) || await db.DomainRemovals.AnyAsync(ct);
    }

    /// <summary>
    /// Host ports that Gitberth's tunnel routes go to, with the domain each serves: they stay taken while a route might
    /// still point at them, so a new app is never given a port that sends someone else's domain to it. That holds while
    /// domains are turned off too, since the routes stay in the tunnel. It only reads Gitberth's own records.
    /// </summary>
    public async Task<List<(int Port, int? AppId, string Hostname)>> RoutedHostPortsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var domains = await db.Domains.AsNoTracking().Select(d => new { d.AppId, d.Hostname, d.Service }).ToListAsync(ct);
        var removals = await db.DomainRemovals.AsNoTracking().Select(r => new { r.Hostname, r.Service }).ToListAsync(ct);
        return
        [
            .. domains.Where(d => TunnelRoutes.HostPortOf(d.Service) is not null).Select(d => (TunnelRoutes.HostPortOf(d.Service)!.Value, (int?)d.AppId, d.Hostname)),
            .. removals.Where(r => TunnelRoutes.HostPortOf(r.Service) is not null).Select(r => (TunnelRoutes.HostPortOf(r.Service)!.Value, (int?)null, r.Hostname)),
        ];
    }

    /// <summary>
    /// The container names that routes of removed domains still go to (web in http://web:3000). A new app mustn't take
    /// one: it would get that domain's visitors. Routes to a port are covered by <see cref="RoutedHostPortsAsync"/>. That
    /// holds while domains are turned off too, and it only reads Gitberth's own records.
    /// </summary>
    public async Task<HashSet<string>> RoutedNamesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var services = await db.DomainRemovals.AsNoTracking().Select(r => r.Service).ToListAsync(ct);
        return services
            .Select(s => TunnelRoutes.HostPortOf(s) is null && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.HostNameType == UriHostNameType.Dns ? uri.Host : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces a domain's DNS records (say, ones still pointing at Vercel) with one that sends it to the tunnel. When the
    /// new record can't be added, the old ones are put back rather than leaving the name with no record at all.
    /// </summary>
    public async Task<SyncReport> PointDnsHereAsync(int appId, string hostname, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var notes = new List<DomainNote>();
        try
        {
            if (!await EnabledAsync(ct)) return SyncReport.Failed(TurnedOff);
            if (!(await HostnamesAsync(appId, ct)).Contains(hostname)) return SyncReport.Nothing;
            if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return SyncReport.Failed("Gitberth has no Cloudflare token. Add one in Settings.");
            var (tunnel, problem) = await ResolveTunnelAsync(connect: false, ct);
            if (tunnel is null) return SyncReport.Failed(problem!);
            if (await ZoneAsync(token, tunnel, hostname, [], ct) is not { } zone)
                return SyncReport.Failed($"Gitberth's token can't see a Cloudflare zone for {hostname} in the tunnel's account.");
            var removed = new List<DnsRecord>();
            try
            {
                foreach (var record in (await cloudflare.GetDnsRecordsAsync(token, zone.Id, hostname, ct)).Where(r => !PointsAtTunnel(r, tunnel)))
                {
                    await cloudflare.DeleteDnsRecordAsync(token, zone.Id, record.Id, hostname, ct);
                    removed.Add(record);
                    notes.Add(new DomainNote(hostname, $"Removed {hostname}'s {record.Type} record to {record.Content}"));
                }
                await CheckDnsAsync(token, tunnel, hostname, [zone], notes, ct);
            }
            catch (CloudflareApiException ex)
            {
                foreach (var record in removed)
                {
                    try
                    {
                        await cloudflare.CreateRecordAsync(token, zone.Id, record, ct);
                        notes.Add(new DomainNote(hostname, $"Put back {hostname}'s {record.Type} record to {record.Content}"));
                    }
                    catch (CloudflareApiException)
                    {
                        notes.Add(new DomainNote(hostname, $"Couldn't put back {hostname}'s {record.Type} record to {record.Content} (TTL {record.Ttl}). Add it again in Cloudflare.", Problem: true));
                    }
                }
                notes.Add(new DomainNote(hostname, ex.Message, Problem: true));
            }
            return new SyncReport(notes);
        }
        catch (CloudflareApiException ex)
        {
            notes.Add(new DomainNote(hostname, ex.Message, Problem: true));
            return new SyncReport(notes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether each of the app's domains reaches it, and what to do when one doesn't. Read-only.</summary>
    public async Task<DomainsOverview> GetOverviewAsync(int appId, CancellationToken ct = default)
    {
        // Taken before the setting is read: see SetEnabledAsync.
        using var whileOn = CancellationTokenSource.CreateLinkedTokenSource(ct, _whileOn.Token);
        try
        {
            return await OverviewAsync(appId, whileOn.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DomainsOverview(false, null, null, [], Enabled: false);
        }
    }

    private async Task<DomainsOverview> OverviewAsync(int appId, CancellationToken ct)
    {
        AppDefinition? app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return new DomainsOverview(false, null, "This app doesn't exist any more.", []);
        var domains = app.Domains.OrderBy(d => d.Id).ToList();
        var main = MainOf(domains);
        List<DomainState> Unchecked(string why, DomainFix fix = DomainFix.None) => domains.Select(d => new DomainState(d.Hostname, false, why, fix, Main: d.Hostname == main)).ToList();

        if (!await EnabledAsync(ct)) return new DomainsOverview(false, null, null, Unchecked("Not managed: domains are turned off."), Enabled: false);
        if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token)
            return new DomainsOverview(false, null, null, Unchecked("Not routed yet: Gitberth has no Cloudflare token."));
        var (tunnel, problem) = await ResolveTunnelAsync(connect: false, ct);
        if (tunnel is null) return new DomainsOverview(true, null, problem, Unchecked("Not routed: there's no tunnel Gitberth can use."));
        if (domains.Count == 0) return new DomainsOverview(true, tunnel, null, []);
        if (!tunnel.HostNetwork && !tunnel.OnAppNetwork)
            return new DomainsOverview(true, tunnel, $"cloudflared ({tunnel.ContainerName}) is on neither the host's network nor the shared network {docker.Network}, so it can't reach the app. Fix joins it to {docker.Network}.", Unchecked("Not routed.", DomainFix.Sync));
        if (PortRules.WebOf(app.Ports) is not { } web) return new DomainsOverview(true, tunnel, null, Unchecked($"Not routed: {app.Name} has no web port."));
        if (!(await ServicesAsync([app], tunnel, ct)).TryGetValue(app.Id, out var picked) || picked.Service is not { } service)
        {
            // cloudflared on the host's network reaches an app only through a host port, which an Internal web port hasn't.
            if (web.Access == PortAccess.Internal && tunnel.HostNetwork)
                return new DomainsOverview(true, tunnel, null,
                    domains.Select(d => new DomainState(d.Hostname, false, PortRules.NeedsHostPort(d.Hostname), DomainFix.None, Main: d.Hostname == main)).ToList());
            return new DomainsOverview(true, tunnel, null, Unchecked($"Not serving: {app.Name} isn't running, or its saved ports aren't live yet."));
        }
        try
        {
            var ingress = (await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, tunnel.TunnelId, ct))["ingress"] as JsonArray ?? [];
            var zones = new List<CloudflareZone>();
            var states = new List<DomainState>();
            foreach (var domain in domains)
            {
                var route = TunnelRoutes.RouteState(ingress, domain.Hostname, app, service, domain.Service, domain.KeepPaths, newVersion: picked.Container != app.ContainerName);
                var dns = await CheckDnsAsync(token, tunnel, domain.Hostname, zones, notes: null, ct);
                var warning = dns.Zone is { } zone ? DomainNames.CertificateWarning(domain.Hostname, zone.Name) : null;
                var isMain = domain.Hostname == main;
                states.Add(route.Ok && dns.Ok ? new DomainState(domain.Hostname, true, route.Summary, DomainFix.None, warning, isMain)
                    : !route.Ok ? new DomainState(domain.Hostname, false, route.Summary, route.Fix, warning, isMain)
                    : new DomainState(domain.Hostname, false, dns.Summary, dns.Fix, warning, isMain));
            }
            return new DomainsOverview(true, tunnel, null, states);
        }
        catch (CloudflareApiException ex)
        {
            return new DomainsOverview(true, tunnel, ex.Message, Unchecked("Couldn't be checked."));
        }
    }

    /// <summary>
    /// Where each running app's domains go now, by app: its web port on its container, from one `docker ps`. During a
    /// deploy that runs the new version next to the old one, that's the new version's container, as long as it runs.
    /// </summary>
    private async Task<Dictionary<int, (string Service, string Container)>> ServicesAsync(IEnumerable<AppDefinition> apps, TunnelInfo tunnel, CancellationToken ct)
    {
        var (containers, _) = await docker.ListContainersAsync(managedOnly: true, ct);
        var running = containers.Where(c => c.State == "running").ToDictionary(c => c.Name, c => c.Ports);
        var services = new Dictionary<int, (string, string)>();
        foreach (var app in apps)
        {
            var name = _routeTo.TryGetValue(app.Id, out var other) && running.ContainsKey(other) ? other : app.ContainerName;
            if (running.TryGetValue(name, out var ports) && TunnelRoutes.ServiceFor(app, ports, tunnel) is { } service)
                services[app.Id] = (service, name);
        }
        return services;
    }

    /// <summary>The tunnel Gitberth manages: the one its token was saved for, or the only one running here.</summary>
    private async Task<(TunnelInfo? Tunnel, string? Problem)> ResolveTunnelAsync(bool connect, CancellationToken ct)
    {
        var scan = await tunnels.FindAllAsync(ct);
        var savedId = await settings.GetAsync(SettingKeys.CloudflareTunnelId, ct);
        var candidates = savedId is null ? scan.Tunnels : scan.Tunnels.Where(t => t.TunnelId == savedId).ToList();
        if (candidates.Count == 0)
            return (null, savedId is not null && scan.Tunnels.Count > 0 ? "The tunnel Gitberth's Cloudflare token was saved for isn't running on this host." : NoTunnel(scan));
        if (candidates.Select(t => t.TunnelId).Distinct().Count() > 1)
            return (null, $"More than one Cloudflare tunnel runs on this host ({string.Join(", ", candidates.Select(t => t.ContainerName))}). Stop the ones Gitberth shouldn't manage, then save the token again.");
        return await ReachableAsync(candidates[0], connect, ct);
    }

    // On neither the host's network nor Gitberth's, cloudflared joins Gitberth's when Gitberth is about to route through it.
    private async Task<(TunnelInfo? Tunnel, string? Problem)> ReachableAsync(TunnelInfo tunnel, bool connect, CancellationToken ct)
    {
        if (tunnel.HostNetwork || tunnel.OnAppNetwork || !connect) return (tunnel, null);
        if (await docker.ConnectToNetworkAsync(tunnel.ContainerName, ct) is { } error)
            return (null, $"cloudflared ({tunnel.ContainerName}) can't reach Gitberth's apps: it's on neither the host's network nor the shared network {docker.Network}, and joining {docker.Network} failed ({error}).");
        return (tunnel with { OnAppNetwork = true }, null);
    }

    private static string NoTunnel(TunnelScan scan) =>
        scan.Unreadable.Count > 0
            ? $"The cloudflared container {scan.Unreadable[0]} doesn't run its tunnel from a token Gitberth can read (a credentials file, say), so Gitberth can't tell which tunnel it is."
            : "No cloudflared container is running on this host. Gitberth reads which tunnel to use from it, so start the tunnel first.";

    /// <summary>
    /// Whether the domain's DNS sends it to the tunnel, directly or through another of the owner's names (www.example.com →
    /// example.com → the tunnel). With <paramref name="notes"/>, also fixes what's safe to fix: a missing record is added and
    /// an unproxied record is proxied. Records pointing elsewhere are never touched here.
    /// </summary>
    private async Task<(bool Ok, string Summary, DomainFix Fix, CloudflareZone? Zone)> CheckDnsAsync(
        string token, TunnelInfo tunnel, string hostname, List<CloudflareZone> zones, List<DomainNote>? notes, CancellationToken ct)
    {
        if (await ZoneAsync(token, tunnel, hostname, zones, ct) is not { } zone)
            return (false, "Gitberth's token can't see a Cloudflare zone for it in the tunnel's account. Add the domain there, or give the token access to its zone.", DomainFix.None, null);
        var records = await cloudflare.GetDnsRecordsAsync(token, zone.Id, hostname, ct);
        if (records.Count == 0)
        {
            if (notes is null) return (false, "It has no DNS record yet.", DomainFix.Sync, zone);
            await cloudflare.CreateCnameAsync(token, zone.Id, hostname, tunnel.DnsTarget, ct);
            notes.Add(new DomainNote(hostname, $"Added a DNS record sending {hostname} to the tunnel"));
            return (true, "", DomainFix.None, zone);
        }
        if (records is [var only] && (PointsAtTunnel(only, tunnel) || await ReachesTunnelAsync(token, tunnel, only, zones, ct)))
        {
            if (only.Proxied) return (true, "", DomainFix.None, zone);
            if (notes is null) return (false, "Its DNS record leads to the tunnel, but Cloudflare's proxy is off for it.", DomainFix.Sync, zone);
            await cloudflare.SetProxiedAsync(token, zone.Id, only.Id, hostname, ct);
            notes.Add(new DomainNote(hostname, $"Turned on Cloudflare's proxy for {hostname}"));
            return (true, "", DomainFix.None, zone);
        }
        var where = string.Join(", ", records.Where(r => !PointsAtTunnel(r, tunnel)).Select(r => $"{r.Type} {r.Content}"));
        return (false, $"Its DNS points somewhere else ({where}), so visitors don't reach the tunnel on the host.", DomainFix.PointDnsHere, zone);
    }

    /// <summary>
    /// Whether a CNAME to another name ends at the tunnel, the way www.example.com → example.com → the tunnel does: Cloudflare
    /// follows the chain, so the name gets there as surely as one pointing straight at the tunnel. Only through names in
    /// zones the token can see, a few steps at most, and never round in a circle.
    /// </summary>
    private async Task<bool> ReachesTunnelAsync(string token, TunnelInfo tunnel, DnsRecord record, List<CloudflareZone> zones, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.Name };
        for (var step = 0; step < 4 && record.Type == "CNAME"; step++)
        {
            var target = record.Content.TrimEnd('.');
            if (!seen.Add(target) || await ZoneAsync(token, tunnel, target, zones, ct) is not { } zone) return false;
            if (await cloudflare.GetDnsRecordsAsync(token, zone.Id, target, ct) is not [var next]) return false;
            if (PointsAtTunnel(next, tunnel)) return true;
            record = next;
        }
        return false;
    }

    // The most specific zone already found that contains the name, else the one Cloudflare finds for it in the tunnel's account.
    private async Task<CloudflareZone?> ZoneAsync(string token, TunnelInfo tunnel, string hostname, List<CloudflareZone> zones, CancellationToken ct)
    {
        var known = zones.Where(z => hostname == z.Name || hostname.EndsWith("." + z.Name, StringComparison.Ordinal)).MaxBy(z => z.Name.Length);
        if (known is not null) return known;
        var zone = await cloudflare.FindZoneAsync(token, tunnel.AccountId, hostname, ct);
        if (zone is not null) zones.Add(zone);
        return zone;
    }

    private static bool PointsAtTunnel(DnsRecord record, TunnelInfo tunnel) =>
        record.Type == "CNAME" && record.Content.TrimEnd('.').Equals(tunnel.DnsTarget, StringComparison.OrdinalIgnoreCase);
}
