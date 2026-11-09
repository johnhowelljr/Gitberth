using System.Globalization;
using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.Shared.Network;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Gitberth.Sources;
using MudBlazor;

namespace Gitberth.Components.NewApp;

/// <summary>Where a new app comes from, as the four source cards offer it.</summary>
public enum NewAppSource { Git, Registry, Local, Hand }

/// <summary>The wizard's three steps: Source, Configure, Create.</summary>
public enum NewAppStep { Source, Configure, Create }

/// <summary>By hand: a pasted docker run, a pasted Compose service, or a blank form.</summary>
public enum HandMode { DockerRun, Compose, Blank }

/// <summary>A pasted command's or service's issues, grouped as the page shows them: Errors block, Dropped are listed, Notes are shown.</summary>
public sealed record ParseIssueView(IReadOnlyList<ParseIssue> Errors, IReadOnlyList<ParseIssue> Dropped, IReadOnlyList<ParseIssue> Notes)
{
    public bool Blocks => Errors.Count > 0;

    public static ParseIssueView Of(IEnumerable<ParseIssue> issues)
    {
        var list = issues.ToList();
        return new(list.Where(i => i.Severity == ParseSeverity.Error).ToList(), list.Where(i => i.Severity == ParseSeverity.Dropped).ToList(),
            list.Where(i => i.Severity == ParseSeverity.Note).ToList());
    }

    /// <summary>The heading of a group: "2 things to fix", "Not carried over (1)".</summary>
    public static string Heading(ParseSeverity severity, int count) => severity switch
    {
        ParseSeverity.Error => count == 1 ? "1 thing to fix before it can be created" : $"{count} things to fix before it can be created",
        ParseSeverity.Dropped => $"Not carried over ({count})",
        _ => count == 1 ? "1 note on how Gitberth read it" : $"{count} notes on how Gitberth read it",
    };

    private const string DroppedPrefix = "Not carried over: ";

    /// <summary>
    /// An issue's words as its group lists them: a dropped one without "Not carried over:" (the group's heading says it
    /// already), and the source cards called by the names they have.
    /// </summary>
    public static string TextOf(ParseIssue issue)
    {
        var text = issue.Text;
        if (issue.Severity == ParseSeverity.Dropped && text.StartsWith(DroppedPrefix, StringComparison.OrdinalIgnoreCase) && text.Length > DroppedPrefix.Length)
            text = char.ToUpperInvariant(text[DroppedPrefix.Length]) + text[(DroppedPrefix.Length + 1)..];
        // Configure offers to create a missing network itself, so the reader isn't sent to the Network page for it.
        return text.Replace("Use From GitHub", $"Use the {NewAppSources.GitTitle} source", StringComparison.Ordinal)
            .Replace("use From GitHub", $"use the {NewAppSources.GitTitle} source", StringComparison.Ordinal)
            .Replace("which must exist (Network page).", "which must exist: Configure offers to create it.", StringComparison.Ordinal);
    }
}

/// <summary>An environment variable in the New app form.</summary>
public sealed class NewEnvRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Secret { get; set; }
    /// <summary>The image sets it: shown under "From the image", and left to the image unless it's edited.</summary>
    public bool FromImage { get; set; }
    /// <summary>The value the image gave it, to tell an edit from the image's own.</summary>
    public string? ImageValue { get; init; }

    public bool Edited => !FromImage || Value != ImageValue;
}

/// <summary>A volume, folder or tmpfs in the New app form.</summary>
public sealed class NewMountRow
{
    public MountKind Kind { get; set; } = MountKind.Volume;
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
    /// <summary>The image declares the path (VOLUME): its row says so.</summary>
    public bool FromImage { get; init; }
}

/// <summary>
/// A new app's settings as the Configure step edits them, made from the source's draft (<see cref="NewAppDraft"/>) and
/// turned back into one for the plan. The App page's own sections (Labels, Logging, Advanced and the resource limits) edit
/// <see cref="More"/>, a settings draft of the source's settings, so everything the source gave is carried and editable;
/// the few fields that still have no field are summed up under "Everything else".
/// </summary>
public sealed class NewAppForm
{
    public AppSource Source { get; init; }
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public int? RegistryId { get; set; }
    public string? Platform { get; init; }
    public bool PullFirst { get; init; }
    /// <summary>Read from a pasted docker run or Compose service: its ports are the paste's, not the image's.</summary>
    public bool Pasted { get; init; }
    public ContainerPortsDraft Ports { get; set; } = ContainerPortsDraft.ForNew([], null);
    public List<NewEnvRow> Env { get; } = [];
    public List<NewMountRow> Mounts { get; } = [];
    /// <summary>Networks besides the shared one, which every app is on; one that doesn't exist yet is listed too, to be made or taken out.</summary>
    public List<string> ExtraNetworks { get; } = [];
    /// <summary>Aliases and addresses the source gave, by network.</summary>
    public Dictionary<string, NetworkJoin> Joins { get; } = new(StringComparer.Ordinal);
    public string? LanAddress { get; set; }
    public string Restart { get; set; } = "unless-stopped";
    public int Retries { get; set; }

    /// <summary>
    /// The source's settings as the App page's sections edit them in create mode: labels, logging, the Advanced fields
    /// (network mode, hostname, DNS, devices, capabilities…) and the resource limits.
    /// </summary>
    public ContainerSettingsDraft More { get; init; } = ContainerSettingsDraft.From(new ContainerSettings());

    /// <summary>"" for its networks (the shared one and any others), host, none or container:&lt;name&gt;.</summary>
    public string NetworkMode
    {
        get => More.NetworkMode;
        set => More.NetworkMode = value ?? "";
    }

    /// <summary>It's on Docker networks of its own (not the host's network, none, or another container's).</summary>
    public bool OnNetworks => NetworkMode.Trim().Length == 0;

    /// <summary>The memory limit in MiB; null for none.</summary>
    public long? MemoryMb
    {
        get => More.MemoryBytes is long bytes ? bytes / 1024 / 1024 : null;
        set => (More.Memory, More.MemoryUnit) = (value, MemoryUnit.MiB);
    }

    public double? Cpus
    {
        get => More.Cpus;
        set => More.Cpus = value;
    }

    public HealthMode Health { get; set; } = HealthMode.Image;
    public string HealthCommand { get; set; } = "";
    public int? HealthIntervalSeconds { get; set; }
    public string? Entrypoint { get; set; }
    public string? Command { get; set; }
    public string? WorkingDir { get; set; }
    public string? User { get; set; }
    public ContainerSettings Base { get; init; } = new();
    public AppOptions Options { get; set; } = new();
    public IReadOnlyList<ParseIssue> Issues { get; init; } = [];
    /// <summary>
    /// What the image says about itself: the host's image, or (<see cref="ImageFacts.OnHost"/> false) its registry's config
    /// before it's pulled. Null when neither could be read: it's filled in once it's pulled.
    /// </summary>
    public ImageFacts? Facts { get; init; }

    /// <summary>The host has the image already: nothing is pulled before it starts.</summary>
    public bool OnHost => Facts is { OnHost: true };

    /// <param name="pasted">Read from a pasted docker run or Compose service.</param>
    public static NewAppForm From(NewAppDraft draft, ImageFacts? facts, bool pasted = false)
    {
        var s = draft.Settings;
        var form = new NewAppForm
        {
            Source = draft.Source, Name = draft.Name, Image = s.Image, RegistryId = draft.RegistryId, Platform = draft.Platform, PullFirst = draft.PullFirst, Pasted = pasted,
            Ports = ContainerPortsDraft.ForNew(s.Ports, draft.Options.WebPort, facts?.ExposedPorts.Select(p => ContainerPorts.Key(p.Port, p.Protocol))),
            LanAddress = draft.LanAddress, Restart = s.Restart.Name, Retries = s.Restart.MaximumRetryCount, More = ContainerSettingsDraft.From(s),
            Health = s.Health.Mode, HealthCommand = s.Health.Test is { Count: > 1 } t ? string.Join(' ', t.Skip(1)) : "",
            HealthIntervalSeconds = s.Health.Interval is { } every ? (int)every.TotalSeconds : null,
            Entrypoint = s.Entrypoint is null ? null : string.Join(' ', s.Entrypoint), Command = s.Command is null ? null : string.Join(' ', s.Command),
            WorkingDir = s.WorkingDir, User = s.User, Base = s, Options = draft.Options with { Name = draft.Name }, Issues = draft.Issues, Facts = facts,
        };
        var imageEnv = (facts?.Env ?? []).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        foreach (var e in s.Env)
            form.Env.Add(new NewEnvRow
            {
                Key = e.Key, Value = e.Value, Secret = e.Secret || Security.Masking.IsSecret(e.Key, e.Value), FromImage = e.FromImage || imageEnv.TryGetValue(e.Key, out var v) && v == e.Value,
                ImageValue = imageEnv.GetValueOrDefault(e.Key),
            });
        var declared = (facts?.Volumes ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var m in s.Mounts)
            form.Mounts.Add(new NewMountRow { Kind = m.Kind, Source = m.Source, Target = m.Target, ReadOnly = m.ReadOnly, FromImage = declared.Contains(m.Target) });
        foreach (var n in draft.ExtraNetworks)
        {
            // Aliases on the shared network (a --network-alias with no network named) are carried as they came.
            if (n.Name.Length == 0)
            {
                form.sharedAliases.Add(n);
                continue;
            }
            if (form.ExtraNetworks.Contains(n.Name)) continue;
            form.ExtraNetworks.Add(n.Name);
            form.Joins[n.Name] = n;
        }
        return form;
    }

    private readonly List<NetworkJoin> sharedAliases = [];

    /// <summary>
    /// The networks it's asked to join that aren't on this host (a pasted --network mynet, or a Compose service's networks):
    /// each is shown with Create and Remove, since the app can't be created on a network that doesn't exist.
    /// </summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> existing, string shared)
    {
        var have = existing.ToHashSet(StringComparer.Ordinal);
        return ExtraNetworks.Where(n => n != shared && n != LanAddresses.Network && !have.Contains(n)).ToList();
    }

    /// <summary>
    /// What the source set that no section has a field for (Configure's "Everything else"): each can still change after
    /// it's created. Empty for nearly every app.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> Unfielded()
    {
        var s = More.ToSettings();
        var list = new List<(string, string)>();
        if (s.Domainname is { Length: > 0 } domain) list.Add(("Domain name", domain));
        if (s.DnsSearch.Count > 0) list.Add(("DNS search", string.Join(", ", s.DnsSearch)));
        if (s.DnsOptions.Count > 0) list.Add(("DNS options", string.Join(", ", s.DnsOptions)));
        if (s.Resources.MemoryReservationBytes is long soft) list.Add(("Memory reservation", $"{soft / 1024 / 1024} MiB"));
        if (s.Resources.MemorySwapBytes is long swap) list.Add(("Memory and swap", swap < 0 ? "no limit" : $"{swap / 1024 / 1024} MiB"));
        if (s.Resources.CpusetCpus is { Length: > 0 } cpuset) list.Add(("CPUs it runs on", cpuset));
        if (s.OpenStdin) list.Add(("Input", "stdin kept open"));
        return list;
    }

    /// <summary>Takes the port rows out: on the host's network, or none, it has no ports of its own.</summary>
    public void TakePortsOut() => Ports = ContainerPortsDraft.ForNew([], null, Ports.ImageExposed);

    /// <summary>The variables the container gets as its own (the image's untouched ones are left to it).</summary>
    public IEnumerable<NewEnvRow> OwnEnv => Env.Where(e => e.Key.Trim().Length > 0 && e.Edited);

    /// <summary>The draft for the plan, everything the form doesn't show carried from the source's.</summary>
    public NewAppDraft ToDraft()
    {
        var health = Health switch
        {
            HealthMode.Custom when HealthCommand.Trim().Length > 0 => new HealthSetting(HealthMode.Custom, ["CMD-SHELL", HealthCommand.Trim()],
                HealthIntervalSeconds is int s && s > 0 ? TimeSpan.FromSeconds(s) : Base.Health.Interval, Base.Health.Timeout, Base.Health.Retries, Base.Health.StartPeriod),
            HealthMode.Custom => Base.Health,
            HealthMode.Disabled => new HealthSetting(HealthMode.Disabled),
            _ => new HealthSetting(),
        };
        // The sections Configure shares with the App page (labels, logging, Advanced, resources) edit More; its own fields go over them.
        var settings = More.ToSettings() with
        {
            Image = Image.Trim(),
            Ports = Ports.Rules(),
            Env = Env.Where(e => e.Key.Trim().Length > 0).Select(e => new EnvSetting(e.Key.Trim(), e.Value, e.FromImage && !e.Edited, e.Secret)).ToList(),
            Mounts = Mounts.Where(m => m.Target.Trim().Length > 0 && (m.Kind == MountKind.Tmpfs || m.Source.Trim().Length > 0))
                .Select(m => new MountSetting(m.Kind, m.Kind == MountKind.Tmpfs ? "" : m.Source.Trim(), m.Target.Trim(), m.ReadOnly)).ToList(),
            Restart = new RestartSetting(Restart, Restart == "on-failure" ? Retries : 0),
            Health = health,
            Entrypoint = Words(Entrypoint, Base.Entrypoint),
            Command = Words(Command, Base.Command),
            WorkingDir = string.IsNullOrWhiteSpace(WorkingDir) ? null : WorkingDir.Trim(),
            User = string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
        };
        return new NewAppDraft
        {
            Source = Source, Name = Name.Trim(), Settings = settings, RegistryId = RegistryId, Platform = Platform, PullFirst = PullFirst, Issues = Issues,
            // The ports and volumes shown came from the image (here or its registry): they're the owner's choice, not refilled after the pull.
            ImageRead = Facts is not null, Facts = Facts,
            ExtraNetworks = [.. sharedAliases, .. ExtraNetworks.Select(n => Joins.TryGetValue(n, out var j) ? j : new NetworkJoin(n, []))],
            LanAddress = string.IsNullOrWhiteSpace(LanAddress) ? null : LanAddress.Trim(),
            Options = Options with
            {
                Name = Name.Trim(), WebPort = Ports.WebPort,
                SecretKeys = Env.Where(e => e.Secret && e.Key.Trim().Length > 0).Select(e => e.Key.Trim()).ToList(),
            },
        };
    }

    // A command line typed in: split as a shell would, null for the image's own.
    private static IReadOnlyList<string>? Words(string? text, IReadOnlyList<string>? loaded)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (loaded is not null && string.Join(' ', loaded) == text) return loaded;
        return ShellWords.Split(text).Words;
    }
}

/// <summary>New app's words, worked out from the plan and the form. Pure, so they're tested.</summary>
public static class NewAppText
{
    /// <summary>The line under Create: "Creates gb-uptime-kuma and starts it. Reached at 10.1.1.80:3001."</summary>
    /// <param name="address">Where it's reached first: its web port's LAN address, else its first published port's, else by name.</param>
    public static string Consequence(string container, string? address, bool needsPull, string? image)
    {
        var pull = needsPull && image is not null ? $"Pulls {image}, then creates {container} and starts it." : $"Creates {container} and starts it.";
        return address is null ? pull : $"{pull} Reached at {address}.";
    }

    /// <summary>Where a new app is reached first, as the consequence line names it.</summary>
    public static string? FirstAddress(IReadOnlyList<PortRule> ports, int? webPort, string lanHost, string slug)
    {
        var published = ports.Where(p => p.Published && p.HostPort is not null).OrderByDescending(p => p.ContainerPort == webPort)
            .ThenBy(p => p.Access == PortAccess.HostOnly).ThenBy(p => p.ContainerPort).FirstOrDefault();
        if (published is not null)
            return published.Access == PortAccess.HostOnly ? ContainerPorts.Address("127.0.0.1", published.HostPort!.Value) : ContainerPorts.Address(lanHost, published.HostPort!.Value);
        var any = ports.OrderByDescending(p => p.ContainerPort == webPort).FirstOrDefault();
        return any is null ? null : ContainerPorts.Address(slug, any.ContainerPort);
    }

    /// <summary>The Create step's lead: one last look before Create, what's happening while it runs, and the truth after a failure.</summary>
    /// <param name="started">Create was pressed.</param>
    /// <param name="ok">How it ended; null while it runs.</param>
    /// <param name="kept">A failed create left something behind (its record or its container).</param>
    public static string CreateLead(bool started, bool? ok, bool kept) => (started, ok) switch
    {
        (false, _) => "One last look. Nothing is created until you press Create.",
        (true, null) => "Creating it now. It carries on if you leave this page.",
        (true, true) => "Created.",
        _ when kept => "It wasn't created as asked. Gitberth kept what it made: open it to see why, or change the settings and try again.",
        _ => "It wasn't created, and nothing was left behind. Try again as it is, or change its settings first.",
    };

    /// <summary>What a failed create left behind, said under its reason: nothing (the record and its host ports went with it), or what was kept.</summary>
    public static string LeftBehind(string container, string? key) => key is null
        ? $"Nothing was left behind: no {container} container and no app record, and its host ports are free again."
        : $"Gitberth kept it as {key}: open it to see why, or remove it there.";

    /// <summary>
    /// The plan's problems as the review lists them. A network the source names that isn't on this host is said the way it
    /// can be fixed here (Configure has Create and Remove for it), not with the Network page's words.
    /// </summary>
    public static IReadOnlyList<string> ReviewProblems(IEnumerable<SettingProblem> problems, IReadOnlyCollection<string> missing)
    {
        var list = missing.Select(m => $"There's no network called {m} on this host: create it or take it out under Network, in Configure.").ToList();
        foreach (var p in problems)
            if (!(p.Field == SettingField.NetworkMode && missing.Any(m => p.Message.Contains(m, StringComparison.Ordinal))))
                list.Add(p.Message);
        return list.Distinct().ToList();
    }

    /// <summary>A port as "Gitberth read" lists it, Docker's way round: "8088:80 (LAN)", "127.0.0.1:5353:53/udp (Host only)", "5432/tcp (Internal)".</summary>
    public static string PortText(PortRule p)
    {
        var udp = PortRules.ProtocolOf(p.Protocol) == "udp" ? "/udp" : "";
        return p.Access switch
        {
            PortAccess.Internal => $"{ContainerPorts.Key(p.ContainerPort, PortRules.ProtocolOf(p.Protocol))} (Internal)",
            PortAccess.HostOnly => p.HostPort is int local ? $"127.0.0.1:{local}:{p.ContainerPort}{udp} (Host only)" : $"127.0.0.1::{p.ContainerPort}{udp} (Host only, host port picked)",
            PortAccess.Custom when p.HostIp is { Length: > 0 } ip => $"{ip}:{p.HostPort}:{p.ContainerPort}{udp} ({ip} only)",
            _ => p.HostPort is int h ? $"{h}:{p.ContainerPort}{udp} (LAN)" : $"{p.ContainerPort}{udp} (LAN, host port picked)",
        };
    }

    /// <summary>How a network mode reads: null for its own networks, "The host's own" for host, and so on.</summary>
    public static string? NetworkModeText(string mode) => mode.Trim() switch
    {
        "" => null,
        "host" => "The host's own",
        "none" => "None",
        var m when m.StartsWith("container:", StringComparison.Ordinal) => $"{m["container:".Length..]}'s (it shares that container's)",
        var m => m,
    };

    /// <summary>"57.8 MB", "1.2 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1e3).ToString("0", CultureInfo.InvariantCulture) + " kB",
    };

    /// <summary>"2 days ago", "4 months ago".</summary>
    public static string Ago(DateTime utc, DateTime now)
    {
        var d = now - utc;
        return d.TotalMinutes < 60 ? "just now"
            : d.TotalHours < 24 ? Plural((int)d.TotalHours, "hour") + " ago"
            : d.TotalDays < 14 ? Plural((int)d.TotalDays, "day") + " ago"
            : d.TotalDays < 60 ? Plural((int)(d.TotalDays / 7), "week") + " ago"
            : d.TotalDays < 730 ? Plural((int)(d.TotalDays / 30), "month") + " ago"
            : Plural((int)(d.TotalDays / 365), "year") + " ago";
    }

    /// <summary>The name a reference suggests: postgres for docker.io/library/postgres:18, uptime-kuma for louislam/uptime-kuma:1.</summary>
    public static string NameFor(string reference)
    {
        var last = reference.Split('@')[0].Split('/')[^1];
        var colon = last.IndexOf(':');
        return colon > 0 ? last[..colon] : last;
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n.ToString(CultureInfo.InvariantCulture)} {unit}s";
}

/// <summary>A popular image, with what it's for: the Registry source's tiles.</summary>
public sealed record PopularImage(string Reference, string What);

public static class PopularImages
{
    /// <summary>Images people start a home server with; their ports, volumes and checks come from the image itself.</summary>
    public static readonly IReadOnlyList<PopularImage> All =
    [
        new("postgres:18", "Database"),
        new("redis:7-alpine", "Cache"),
        new("nginx:stable-alpine", "Web server"),
        new("mariadb:11", "Database"),
        new("mongo:8", "Database"),
        new("rabbitmq:4-management", "Queue"),
        new("grafana/grafana:latest", "Dashboards"),
        new("minio/minio:latest", "S3 storage"),
        new("caddy:2", "Web server"),
        new("traefik:v3", "Proxy"),
        new("prom/prometheus:latest", "Metrics"),
        new("louislam/uptime-kuma:1", "Monitoring"),
    ];
}

/// <summary>The four source cards' words and icons.</summary>
public static class NewAppSources
{
    /// <summary>The Git source's card: only github.com repositories work for now, so it says so.</summary>
    public const string GitTitle = "GitHub repository";

    public static (string Title, string Text, string Icon, string[] Tags) Of(NewAppSource source) => source switch
    {
        NewAppSource.Git => (GitTitle, "Gitberth builds it from GitHub, then again on every push. It detects Next.js, Python, Go, .NET, Dockerfiles and more.",
            Icons.Custom.Brands.GitHub, ["GitHub", "Deploy on push"]),
        NewAppSource.Registry => ("Registry image", "Pull any image from Docker Hub, GitHub or your own registry, and keep it up to date.",
            Icons.Material.Outlined.CloudDownload, ["Docker Hub", "ghcr.io"]),
        NewAppSource.Local => ("Local image", "Run an image that's already on this host, with no pull.", Icons.Material.Outlined.Layers, ["No pull"]),
        _ => ("By hand", "Paste a docker run command or a Compose service, or fill in a blank form.", Icons.Material.Outlined.EditNote, ["docker run", "Compose"]),
    };
}
