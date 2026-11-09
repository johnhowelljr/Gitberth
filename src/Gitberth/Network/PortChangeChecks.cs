namespace Gitberth.Network;

/// <summary>What the host knows about its ports: the HostPortMap's answers, the LAN address and the tunnel's routes.</summary>
public interface IHostPortChecks
{
    /// <summary>The LAN address of this host, which every address Gitberth shows uses: 10.1.1.80.</summary>
    Task<string> LanAddressAsync(CancellationToken ct);

    /// <summary>
    /// The address a LAN port is published on: "" for every address of the host. A host with a global IPv6 address the LAN
    /// choice shouldn't reach gives 0.0.0.0, which is IPv4 only.
    /// </summary>
    Task<string> LanHostIpAsync(CancellationToken ct);

    /// <summary>
    /// Who else holds the host port, as the owner reads it: "sample-pg (container)", "orb (app)", "SSH on the host", "a
    /// program on the host". Null when <paramref name="container"/> may take it: its own bindings don't count against it.
    /// </summary>
    Task<string?> HolderAsync(string container, int hostPort, string protocol, string hostIp, CancellationToken ct);

    /// <summary>The domains whose tunnel route goes to this host port: they stop working when it moves.</summary>
    Task<IReadOnlyList<string>> TunnelRoutesAsync(int hostPort, CancellationToken ct);
}

/// <summary>A host port to try binding.</summary>
public sealed record ProbePort(string HostIp, int HostPort, string Protocol);

/// <summary>The last check before anything stops: a throwaway container (the "probe" helper) binds the new host ports.</summary>
public interface IHostPortProbe
{
    /// <summary>Null when Docker could bind them all; else Docker's message, which names the port.</summary>
    Task<string?> ProbeAsync(IReadOnlyList<ProbePort> ports, CancellationToken ct);
}

/// <summary>What a connect found.</summary>
/// <param name="Error">Why the check itself couldn't run (it says nothing about the port); null when it ran.</param>
public sealed record ConnectResult(bool Answered, string? Error = null)
{
    public static readonly ConnectResult Yes = new(true);
    public static readonly ConnectResult No = new(false);

    public bool Unknown => Error is not null;
}

/// <summary>A TCP connect from the host's own network (the "check" helper), as a device on the LAN would connect.</summary>
public interface ILanCheck
{
    Task<ConnectResult> ConnectAsync(string address, int port, CancellationToken ct);
}

/// <summary>A TCP connect to a container by name, from Gitberth's own container.</summary>
public interface INameCheck
{
    /// <summary>Whether container:port answers now; null when Gitberth can't tell (they share no network).</summary>
    Task<bool?> AnswersAsync(string container, int port, CancellationToken ct);
}

/// <param name="Users">Running containers whose settings name it as a host, by their titles: kanban.</param>
/// <param name="Building">Apps building right now that were given its current address.</param>
/// <param name="NameItsAddress">Containers whose settings name one of the addresses passed in.</param>
public sealed record ContainerUsers(IReadOnlyList<string> Users, IReadOnlyList<string> Building, IReadOnlyList<AddressUser> NameItsAddress)
{
    public static readonly ContainerUsers None = new([], [], []);
}

/// <param name="Who">The container's title.</param>
/// <param name="Address">The address its settings name: 172.21.0.4.</param>
public sealed record AddressUser(string Who, string Address);

/// <summary>Who depends on a container (ContainerService.UsersOfAsync, and builds in progress).</summary>
public interface IContainerUsers
{
    /// <param name="addresses">Its addresses that can change when it's recreated (not static ones).</param>
    Task<ContainerUsers> UsersOfAsync(string container, IReadOnlyCollection<string> addresses, CancellationToken ct);
}
