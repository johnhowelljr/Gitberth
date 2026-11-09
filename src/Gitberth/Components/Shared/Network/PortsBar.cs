using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Components.Shared.Network;

/// <summary>The line on an app's Ports card that says what saving the changes does (5.9).</summary>
public static class PortsBar
{
    /// <param name="restart">The container has to be recreated for the change (<see cref="ContainerPortsDraft.NeedsRecreate"/>).</param>
    /// <param name="deployed">The app has an image to recreate its container from.</param>
    /// <param name="unnoticed">The deploy keeps its domains' visitors on the old version meanwhile (<see cref="Gitberth.Deploy.BridgeRules"/>).</param>
    /// <param name="webPort">The web row's container port after the change; null when no row is marked.</param>
    public static string Line(int changes, bool restart, string app, bool deployed, bool running, bool unnoticed, int? webPort, bool hasDomains)
    {
        var count = changes == 1 ? "1 change." : $"{changes} changes.";
        if (!restart)
            return $"{count} Nothing restarts: " + (webPort is not int port ? $"{app} has no web port now."
                : hasDomains ? $"domains move to port {port} at once." : $"port {port} is the web port from now on.");
        if (!deployed) return $"{count} {app} isn't deployed yet: its first deploy uses the new ports.";
        if (!running) return $"{count} The app is stopped: it takes the new ports when it starts.";
        return $"{count} {app} restarts: a few seconds offline{(unnoticed ? " on the LAN; domain visitors aren't affected" : "")}.";
    }

    /// <summary>
    /// Whether applying a change restarts the app, by its status: it runs, or a deploy is under way (the old version runs
    /// meanwhile, and the deploy leaves the app running), or Docker can't be read, when nothing promises it won't.
    /// </summary>
    public static bool Runs(AppRunState state) => state is AppRunState.Running or AppRunState.Restarting or AppRunState.Deploying or AppRunState.Unknown;

    /// <summary>What saving the ports did, when no deploy was started to make them live.</summary>
    /// <param name="applyNow">Saved with [Apply], which starts a deploy unless the app isn't running.</param>
    /// <param name="running">The app ran when the bar was shown (<see cref="Runs"/>): a stopped one takes them when it starts.</param>
    public static string Saved(AppDefinition app, PortsSaved saved, bool applyNow, bool running) =>
        !saved.NeedsRestart ? app.Domains.Count > 0 ? "Saved. The domains go to the new web port now." : "Saved."
        : app.CurrentImage is null ? "Saved. Its first deploy uses the new ports."
        : applyNow || !running ? $"Saved. {app.Name} isn't running: it takes the new ports when it starts."
        : "Saved. The new ports are live after the next deploy.";

    /// <summary>
    /// Whether the deploy an Apply followed left settings pending that were saved while it ran. An Apply clicked while a
    /// deploy was already under way follows that deploy (AppActions.ApplyIfRunningAsync) rather than queue a restart with
    /// the image from before it, and the deploy had read the app before the save. A save from before it started, or from
    /// after it finished, isn't counted: a page that read the app just before the deploy's last write still shows that one.
    /// </summary>
    public static bool LeftPending(AppDefinition app, Deployment dep) =>
        app.ConfigChangePending && app.UpdatedAt >= dep.StartedAt && app.UpdatedAt <= dep.FinishedAt;

    /// <summary>What the deploy an Apply followed did, once it worked (see <see cref="LeftPending"/>).</summary>
    /// <param name="liveAddress">Where the app answers on the LAN now, if anywhere.</param>
    public static string Applied(AppDefinition app, Deployment dep, string? liveAddress) =>
        LeftPending(app, dep) ? $"Deploy #{dep.Id} finished, but settings saved while it ran aren't live yet."
        : liveAddress is { } address ? $"Applied. {app.Name} answers at {address}." : "Applied.";
}

/// <summary>Where a row of the Ports table is reached, as its Address column shows it.</summary>
public static class PortAddress
{
    /// <summary>
    /// The row's address: LAN at the LAN address of this host, Host only at 127.0.0.1, a Custom row at its own address, and
    /// Internal by its network name (the web port as http://kanban:3000). A web port on the LAN is a URL to open, https for
    /// 443 and 8443; a host-only one never is, since the browser's machine can't reach it. Null while the row has no
    /// container port, or a published one no host port.
    /// </summary>
    /// <param name="name">What containers on the shared network call it: an app's network name.</param>
    public static (string Text, string? Href)? Of(PortRow row, string lanHost, string name)
    {
        if (row.ContainerPort is not int port) return null;
        var protocol = PortRules.ProtocolOf(row.Protocol);
        var web = row.IsWeb && protocol == "tcp";
        if (row.Access == PortAccess.Internal) return (web ? $"http://{ContainerPorts.Address(name, port)}" : ContainerPorts.Address(name, port), null);
        if (row.HostPort is not int hostPort) return null;
        if (row.Access == PortAccess.HostOnly) return (ContainerPorts.Address("127.0.0.1", hostPort), null);
        var address = ContainerPorts.Address(row.Access == PortAccess.Custom ? row.HostIp?.Trim().Trim('[', ']') ?? "" : lanHost, hostPort);
        if (!AddressBook.IsWeb(port, protocol, database: false, appWebPort: web)) return (address, null);
        var url = $"{(port is 443 or 8443 ? "https" : "http")}://{address}";
        return (url, url);
    }
}

/// <summary>The line under a host-port field (7.7), from the host-port map's checks.</summary>
public static class HostPortText
{
    /// <summary>
    /// What's wrong with a host port whoever holds it, in the words the save uses: out of range, or another row has it;
    /// null when nothing is.
    /// </summary>
    /// <param name="alsoTaken">Host ports the table's other rows have for the same protocol.</param>
    public static string? Problem(int port, IReadOnlyCollection<int> alsoTaken) =>
        port is < 1 or > 65535 ? "Pick a host port between 1 and 65535."
        : alsoTaken.Contains(port) ? $"Host port {port} is listed twice."
        : null;

    /// <summary>
    /// Why a free host port isn't the container's own one, for a field that was given the next free port: "5432 is used by
    /// postgres; 5433 is free.", "3000 and 3001 are used; 3002 is free."; null unless every port in between is taken.
    /// </summary>
    /// <param name="check">Who holds a port, for the binding the field is for.</param>
    public static string? WhyNotOwn(int containerPort, int hostPort, Func<int, HostPortCheck> check)
    {
        var skipped = hostPort - containerPort;
        // Further off, a list of who holds what would say more than the line can.
        if (skipped is < 1 or > 100) return null;
        var held = Enumerable.Range(containerPort, skipped).Select(check).ToList();
        if (held.Any(c => c.Free)) return null;
        return skipped switch
        {
            1 => $"{Held(held[0])}; {hostPort} is free.",
            2 => $"{containerPort} and {containerPort + 1} are used; {hostPort} is free.",
            _ => $"{containerPort}-{hostPort - 1} are used; {hostPort} is free.",
        };
    }

    // "5432 is used by postgres", "8080 is Gitberth's own page".
    private static string Held(HostPortCheck c) => c.Owner!.Kind switch
    {
        HostPortUser.App or HostPortUser.Container => $"{c.Port} is used by {c.Owner.Who}",
        HostPortUser.Gitberth => $"{c.Port} is Gitberth's own page",
        HostPortUser.Reserved when c.Owner.Who == HostPortMap.Ssh => $"{c.Port} is SSH on the host",
        HostPortUser.Reserved => $"{c.Port} is Docker's API on the host",
        HostPortUser.HostProgram => $"{c.Port} is used by a program on the host",
        _ => $"{c.Port} is kept for the domain {c.Owner.Who}",
    };
}
