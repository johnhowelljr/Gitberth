using Gitberth.Network;

namespace Gitberth.Components.Shared.Network;

/// <summary>How the Ports card treats an app's ports.</summary>
public enum PortsMode
{
    /// <summary>Gitberth recreates it for a change, through the change dialog.</summary>
    Editable,
    /// <summary>A Git app: a change is saved, then applied by a deploy without a build.</summary>
    Git,
    /// <summary>Docker Compose owns it: a change is the edit to make in its file.</summary>
    Compose,
    /// <summary>Paused: the table is there, but nothing changes until it's unpaused.</summary>
    Paused,
    /// <summary>Gitberth won't change its ports (Gitberth itself, --rm, Swarm, a deploy's temporary container…).</summary>
    ReadOnly,
    /// <summary>The host's network: Docker maps no ports, and Gitberth notes the ones it listens on.</summary>
    HostNetwork,
    /// <summary>Another container's network, or none: there are no ports of its own.</summary>
    NoTable,
}

/// <summary>Which Ports mode an app is in, from what it is and what the recreate rules say (5.6). Pure.</summary>
public static class PortsModes
{
    /// <param name="git">A Git app: its ports are saved settings, applied by its deploys.</param>
    /// <param name="self">Gitberth's own container.</param>
    /// <param name="temporary">A deploy's or a change's temporary container.</param>
    /// <param name="networkMode">"" for a bridge network, host, none, container:&lt;x&gt;.</param>
    /// <param name="verdict">Whether Gitberth may recreate it; null until that's been worked out.</param>
    public static PortsMode Of(bool git, bool self, bool temporary, string networkMode, RecreateVerdict? verdict)
    {
        if (networkMode == "host" || verdict?.Kind == RecreateKind.HostNetwork) return PortsMode.HostNetwork;
        if (networkMode == "none" || networkMode.StartsWith("container:", StringComparison.Ordinal)
            || verdict?.Kind is RecreateKind.SharesNetwork or RecreateKind.NoNetwork) return PortsMode.NoTable;
        if (self || temporary) return PortsMode.ReadOnly;
        if (git) return PortsMode.Git;
        if (verdict is null) return PortsMode.ReadOnly;
        return verdict.Kind switch
        {
            // A Registry, Local or Manual app Gitberth created: its own settings change it (the engine has the last word).
            RecreateKind.Allowed or RecreateKind.EngineTooOld or RecreateKind.Busy or RecreateKind.App => PortsMode.Editable,
            RecreateKind.Compose => PortsMode.Compose,
            RecreateKind.Paused => PortsMode.Paused,
            _ => PortsMode.ReadOnly,
        };
    }
}
