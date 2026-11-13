using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Previews;

/// <summary>
/// A preview's settings, copied from its parent when it's made and again before each of its deploys: the source, the
/// build, the build filters, the limits, the health URL, Vercel's switch, the settings beyond the classic ones, the ports, the volumes and
/// the networks. Some are never copied, so a preview can't take anything production holds: bind mounts (host folders
/// production writes into), host devices and privileged mode, which gives a container every device (a serial stick is
/// one container's at a time), fixed addresses on networks and own LAN addresses (two containers can't hold one), the
/// names production answers to on its networks (Docker would split their traffic), and the parent's domains. A pull
/// request from a fork doesn't get what reaches past the container into the host either: added capabilities, security
/// options (but no-new-privileges) and GPUs. Its web port gets a host port of its own; the parent's other ports stay
/// inside Docker. Its named volumes are its own already: they're named after its slug, and it keeps each one it has
/// (<see cref="SyncVolumes"/>), even where the parent has a tmpfs now (<see cref="TmpfsUnderVolumes"/>).
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
        // A preview's new commits go through its parent's build filters: copied before each deploy, which reads them.
        preview.WatchPaths = parent.WatchPaths;
        preview.IgnoredPaths = parent.IgnoredPaths;

        var ports = Ports(parent.Ports, preview.Ports, preview.DetectedPort);
        if (PortsKey(ports) != PortsKey(preview.Ports.OrderBy(p => p.Id)))
        {
            preview.Ports.Clear();
            preview.Ports.AddRange(ports);
        }
        SyncVolumes(preview.Volumes, parent.Volumes);
        // After the volumes: a tmpfs the parent has where the preview keeps a volume isn't copied (Docker mounts one thing at a path).
        preview.RuntimeExtras = GitExtras.Write(WithoutTmpfsUnder(Extras(GitExtras.Parse(parent.RuntimeExtras), info.FromFork), preview.Volumes));
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

    /// <summary>
    /// Brings a preview's volumes in step with its parent's, in place: each of the parent's named volumes at a path the
    /// preview has no volume at is added, and the preview's volume at a path the parent has one at follows its read-only.
    /// No named volume the preview has goes, even when the parent's has gone, moved or been renamed, or its image no longer
    /// declares it: its container may still mount it (a deploy that fails leaves the old one running), and only its row
    /// keeps another app from taking its name (VolumeOwners) and has it deleted with the preview. A host folder never stays.
    /// </summary>
    public static void SyncVolumes(List<VolumeMount> volumes, IEnumerable<VolumeMount> parentVolumes)
    {
        volumes.RemoveAll(v => VolumeNames.IsBind(v.Source));
        foreach (var copy in Volumes(parentVolumes))
        {
            // Docker mounts one thing at a path: the preview's own that's there already stays, as the parent mounts that path.
            if (volumes.FirstOrDefault(v => SamePath(v.Target, copy.Target)) is not { } there) volumes.Add(copy);
            else if (there.ReadOnly != copy.ReadOnly) there.ReadOnly = copy.ReadOnly;
        }
    }

    /// <summary>The preview's volumes as <see cref="SyncVolumes"/> would leave them; the ones given aren't touched.</summary>
    public static List<VolumeMount> VolumesAfterSync(IEnumerable<VolumeMount> volumes, IEnumerable<VolumeMount> parentVolumes)
    {
        var list = volumes.OrderBy(v => v.Id).Select(v => new VolumeMount { Source = v.Source, Target = v.Target, ReadOnly = v.ReadOnly }).ToList();
        SyncVolumes(list, parentVolumes);
        return list;
    }

    // "/data/" and "/data" are one place in the container.
    private static bool SamePath(string a, string b) => a.Trim().TrimEnd('/') == b.Trim().TrimEnd('/');

    /// <summary>
    /// The parent's tmpfs mounts at a path the preview keeps a volume of its own at (the parent swapped its volume there for
    /// a tmpfs, say). Docker mounts one thing at a path and refuses a container with both, so the preview's volume stays and
    /// the tmpfs isn't copied: the volume's row is what keeps its name the preview's (<see cref="SyncVolumes"/>).
    /// </summary>
    public static List<(GitTmpfs Tmpfs, VolumeMount Volume)> TmpfsUnderVolumes(GitExtras? parent, IEnumerable<VolumeMount> volumes)
    {
        var list = volumes.ToList();
        return (parent?.Tmpfs ?? []).Select(t => (Tmpfs: t, Volume: list.FirstOrDefault(v => SamePath(v.Target, t.Target))))
            .Where(x => x.Volume is not null).Select(x => (x.Tmpfs, x.Volume!)).ToList();
    }

    private static GitExtras? WithoutTmpfsUnder(GitExtras? extras, IEnumerable<VolumeMount> volumes)
    {
        var under = TmpfsUnderVolumes(extras, volumes).Select(x => x.Tmpfs).ToList();
        return under.Count == 0 ? extras : extras! with { Tmpfs = extras.Tmpfs!.Where(t => !under.Contains(t)).ToList() };
    }

    /// <summary>The parent's host folders, which a preview leaves out.</summary>
    public static List<VolumeMount> LeftOut(IEnumerable<VolumeMount> parentVolumes) => parentVolumes.Where(v => VolumeNames.IsBind(v.Source)).OrderBy(v => v.Id).ToList();

    /// <summary>
    /// The parent's settings beyond the classic ones as a preview gets them: never its host devices, nor privileged mode,
    /// which gives a container every device of the host; for a pull request from a fork, also no added capabilities,
    /// GPUs, or security options but no-new-privileges (the one that only takes rights away). The rest (dropped
    /// capabilities, a read-only root, limits and the like) stays.
    /// </summary>
    public static GitExtras? Extras(GitExtras? parent, bool fromFork) => parent is null ? null : parent with
    {
        Devices = null,
        Privileged = null,
        CapAdd = fromFork ? null : parent.CapAdd,
        Gpus = fromFork ? null : parent.Gpus,
        SecurityOpt = fromFork ? parent.SecurityOpt?.Where(NoNewPrivileges).ToList() : parent.SecurityOpt,
    };

    /// <summary>What <see cref="Extras"/> leaves out of the parent's settings, as the preview's deploy log says it; empty when nothing.</summary>
    public static List<string> ExtrasLeftOut(GitExtras? parent, bool fromFork)
    {
        var lines = new List<string>();
        if (parent is null) return lines;
        if (parent.Devices is { Count: > 0 } devices)
            lines.Add($"Not given the host's {(devices.Count == 1 ? "device" : "devices")}, because a preview never takes production's: {string.Join(", ", devices.Select(d => d.HostPath))}.");
        if (parent.Privileged == true)
            lines.Add("Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's.");
        if (!fromFork) return lines;
        var parts = new List<string>();
        if (parent.CapAdd is { Count: > 0 } caps) parts.Add($"added capabilities ({string.Join(", ", caps)})");
        if (parent.SecurityOpt?.Where(o => !NoNewPrivileges(o)).ToList() is { Count: > 0 } options) parts.Add($"security options ({string.Join(", ", options)})");
        if (!string.IsNullOrWhiteSpace(parent.Gpus)) parts.Add($"GPUs ({parent.Gpus.Trim()})");
        if (parts.Count > 0)
            lines.Add($"Runs without {(parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " or " + parts[^1])}, because the pull request comes from a fork and its code mustn't reach the host.");
        return lines;
    }

    // no-new-privileges, as Docker takes it (alone, or :true / =true): it only takes rights away, so a fork keeps it.
    private static bool NoNewPrivileges(string option) =>
        option.Trim().ToLowerInvariant() is "no-new-privileges" or "no-new-privileges:true" or "no-new-privileges=true";

    /// <summary>The parent's networks, without the names it answers to there, and without the one it has its own address on.</summary>
    public static List<AppNetwork> Networks(IEnumerable<AppNetwork> parentNetworks) =>
        parentNetworks.Where(n => string.IsNullOrWhiteSpace(n.IPv4Address)).OrderBy(n => n.Id)
            .DistinctBy(n => n.Network, StringComparer.Ordinal)
            .Select(n => new AppNetwork { Network = n.Network }).ToList();

    private static string PortsKey(IEnumerable<PortMapping> ports) =>
        string.Join('|', ports.Select(p => $"{p.ContainerPort}/{PortRules.ProtocolOf(p.Protocol)}:{p.Access}:{p.HostPort}:{p.HostIp}:{p.IsWeb}"));
}
