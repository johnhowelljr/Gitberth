using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Previews;

/// <summary>
/// A preview's settings, copied from its parent when it's made and again before each of its deploys: the source, the
/// build, the limits, the health URL, Vercel's switch, the settings beyond the classic ones, the ports, the volumes and
/// the networks. Some are never copied, so a preview can't take anything production holds: bind mounts (host folders
/// production writes into), fixed addresses on networks and own LAN addresses (two containers can't hold one), the names
/// production answers to on its networks (Docker would split their traffic), and the parent's domains. Its web port gets
/// a host port of its own; the parent's other ports stay inside Docker. Its named volumes are its own already: they're
/// named after its slug.
/// </summary>
public static class PreviewCopy
{
    public static void Sync(AppDefinition preview, AppDefinition parent, PreviewInfo info)
    {
        preview.Source = AppSource.Git;
        preview.Container = null;
        preview.AutoDeploy = false;
        preview.PreviewMode = PreviewMode.Off;
        preview.Owner = parent.Owner;
        preview.Repo = parent.Repo;
        preview.Branch = info.Branch;
        preview.Subdirectory = parent.Subdirectory;
        preview.DockerfileMode = parent.DockerfileMode;
        preview.CustomDockerfile = parent.CustomDockerfile;
        preview.ProjectHint = parent.ProjectHint;
        preview.RestartPolicy = parent.RestartPolicy;
        preview.MemoryLimitMb = parent.MemoryLimitMb;
        preview.CpuLimit = parent.CpuLimit;
        preview.HealthCheckPath = parent.HealthCheckPath;
        preview.VercelVariables = parent.VercelVariables;
        preview.RuntimeExtras = parent.RuntimeExtras;

        var ports = Ports(parent.Ports, preview.Ports, preview.DetectedPort);
        if (PortsKey(ports) != PortsKey(preview.Ports.OrderBy(p => p.Id)))
        {
            preview.Ports.Clear();
            preview.Ports.AddRange(ports);
        }
        var volumes = Volumes(parent.Volumes);
        if (VolumesKey(volumes) != VolumesKey(preview.Volumes.OrderBy(v => v.Id)))
        {
            preview.Volumes.Clear();
            preview.Volumes.AddRange(volumes);
        }
        var networks = Networks(parent.Networks);
        if (!networks.Select(n => n.Network).SequenceEqual(preview.Networks.OrderBy(n => n.Id).Select(n => n.Network), StringComparer.Ordinal)
            || preview.Networks.Any(n => n.Alias is not null || n.IPv4Address is not null))
        {
            preview.Networks.Clear();
            preview.Networks.AddRange(networks);
        }
    }

    /// <summary>
    /// The preview's ports: the parent's, each by its container port, all inside Docker but the web port, which is on the
    /// LAN on the preview's own host port (<paramref name="current"/>'s, or none yet: its maker picks one). The web port's
    /// container port is the parent's, unless the preview's own code moved it (a build found the app listens elsewhere).
    /// </summary>
    /// <param name="detectedPort">The port the preview's last build found its app listens on.</param>
    public static List<PortMapping> Ports(IEnumerable<PortMapping> parentPorts, IReadOnlyCollection<PortMapping> current, int? detectedPort)
    {
        var parentList = parentPorts.OrderBy(p => p.Id).ToList();
        var parentWeb = PortRules.WebOf(parentList);
        var ownWeb = PortRules.WebOf(current);
        var list = new List<PortMapping>();
        if (parentWeb is not null)
        {
            var port = ownWeb is not null && detectedPort == ownWeb.ContainerPort ? ownWeb.ContainerPort : parentWeb.ContainerPort;
            list.Add(new PortMapping { ContainerPort = port, Protocol = "tcp", Access = PortAccess.Lan, HostPort = ownWeb?.HostPort, IsWeb = true });
        }
        foreach (var p in parentList.Where(p => p != parentWeb))
        {
            var protocol = PortRules.ProtocolOf(p.Protocol);
            if (list.Any(x => x.ContainerPort == p.ContainerPort && PortRules.ProtocolOf(x.Protocol) == protocol)) continue;
            list.Add(new PortMapping { ContainerPort = p.ContainerPort, Protocol = protocol, Access = PortAccess.Internal });
        }
        return list;
    }

    /// <summary>The parent's named volumes (the preview's own once named after its slug); never a host folder.</summary>
    public static List<VolumeMount> Volumes(IEnumerable<VolumeMount> parentVolumes) =>
        parentVolumes.Where(v => !VolumeNames.IsBind(v.Source)).OrderBy(v => v.Id)
            .Select(v => new VolumeMount { Source = v.Source, Target = v.Target, ReadOnly = v.ReadOnly }).ToList();

    /// <summary>The parent's host folders, which a preview leaves out.</summary>
    public static List<VolumeMount> LeftOut(IEnumerable<VolumeMount> parentVolumes) => parentVolumes.Where(v => VolumeNames.IsBind(v.Source)).OrderBy(v => v.Id).ToList();

    /// <summary>The parent's networks, without the names it answers to there, and without the one it has its own address on.</summary>
    public static List<AppNetwork> Networks(IEnumerable<AppNetwork> parentNetworks) =>
        parentNetworks.Where(n => string.IsNullOrWhiteSpace(n.IPv4Address)).OrderBy(n => n.Id)
            .DistinctBy(n => n.Network, StringComparer.Ordinal)
            .Select(n => new AppNetwork { Network = n.Network }).ToList();

    private static string PortsKey(IEnumerable<PortMapping> ports) =>
        string.Join('|', ports.Select(p => $"{p.ContainerPort}/{PortRules.ProtocolOf(p.Protocol)}:{p.Access}:{p.HostPort}:{p.HostIp}:{p.IsWeb}"));

    private static string VolumesKey(IEnumerable<VolumeMount> volumes) => string.Join('|', volumes.Select(v => $"{v.Source}:{v.Target}:{v.ReadOnly}"));
}
