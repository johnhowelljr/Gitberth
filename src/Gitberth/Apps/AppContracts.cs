using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Apps;

/// <summary>How an app came to Gitberth. What "update" means goes by <see cref="ImageOrigin"/>, not by this.</summary>
public enum AppSource { Git, Registry, Local, Manual, Existing }

/// <summary>
/// Where the image an app runs comes from: a registry known to have it (Gitberth pulled it, or a check found it there), this
/// host (anything else; see Images.ImageOrigins), built from a Git app's repo, or pinned to a digest or an ID.
/// </summary>
public enum ImageOrigin { Registry, Local, Built, Pinned }

public enum UpdatePolicy { Off, Notify, Auto }

/// <summary>A setting the owner can change; each is applied one way (<see cref="SettingFields.KindOf"/>).</summary>
public enum SettingField
{
    Image, Entrypoint, Command, WorkingDir, User, Env, Ports, Mounts, NetworkMode, Hostname, ExtraHosts, Dns,
    RestartPolicy, Memory, Cpu, PidsLimit, ShmSize, Ulimits, Health, Labels, Devices, Gpus, Capabilities, Privileged,
    SecurityOpt, ReadOnlyRootfs, Init, Sysctls, Logging, Stop, Terminal,
}

/// <summary>How a change takes effect.</summary>
public enum ApplyKind
{
    /// <summary>Only Gitberth's own data: instant.</summary>
    Gitberth,
    /// <summary>docker update: no restart.</summary>
    Live,
    /// <summary>A network connect or disconnect: no restart.</summary>
    Network,
    /// <summary>The container is recreated (the change engine).</summary>
    Recreate,
    /// <summary>Git apps: saved, then a deploy without a build.</summary>
    Deploy,
    /// <summary>Git apps: saved, then a deploy with a build.</summary>
    Rebuild,
    /// <summary>Compose owns the container: Gitberth gives the YAML and the command.</summary>
    Compose,
    /// <summary>There's no container: the saved settings change, and apply when it's created.</summary>
    Snapshot,
}

public enum ChangeKind { Ports, Settings, Update, Rollback, Create }

public static class SettingFields
{
    public static readonly IReadOnlySet<SettingField> Live = new HashSet<SettingField>
        { SettingField.RestartPolicy, SettingField.Memory, SettingField.Cpu, SettingField.PidsLimit };

    public static ApplyKind KindOf(SettingField field) => Live.Contains(field) ? ApplyKind.Live : ApplyKind.Recreate;
}

/// <param name="FromImage">The image sets it to this value: it isn't the container's own.</param>
/// <param name="Secret">Masked wherever it's shown (the owner's marking, else Gitberth's guess).</param>
/// <param name="BuildTime">Git apps: passed to the build too.</param>
/// <param name="Scope">Git apps: which deploys get it (every one, production's, or previews'). Any other container's are All.</param>
public sealed record EnvSetting(string Key, string Value, bool FromImage = false, bool Secret = false, bool BuildTime = false,
    Data.EnvScope Scope = Data.EnvScope.All);

public enum MountKind { Volume, Bind, Tmpfs }

/// <param name="Source">A volume's full Docker name (postgres-data, an anonymous one's 64-hex name), a bind's host path, "" for tmpfs.</param>
public sealed record MountSetting(MountKind Kind, string Source, string Target, bool ReadOnly = false, long? TmpfsBytes = null)
{
    public bool Anonymous => Kind == MountKind.Volume && Source.Length == 64 && Source.All(char.IsAsciiHexDigitLower);
}

/// <param name="Name">no, always, unless-stopped or on-failure.</param>
public sealed record RestartSetting(string Name = "unless-stopped", int MaximumRetryCount = 0);

public sealed record ResourceSetting(long? MemoryBytes = null, long? MemoryReservationBytes = null, long? MemorySwapBytes = null,
    double? Cpus = null, long? CpuShares = null, string? CpusetCpus = null, long? PidsLimit = null);

public enum HealthMode { Image, Custom, Disabled }

/// <param name="Test">For Custom: ["CMD", …] or ["CMD-SHELL", "…"].</param>
public sealed record HealthSetting(HealthMode Mode = HealthMode.Image, IReadOnlyList<string>? Test = null, TimeSpan? Interval = null,
    TimeSpan? Timeout = null, int? Retries = null, TimeSpan? StartPeriod = null, TimeSpan? StartInterval = null);

public sealed record DeviceSetting(string HostPath, string ContainerPath, string Permissions = "rwm");

public sealed record UlimitSetting(string Name, long Soft, long Hard);

/// <param name="Driver">"" for the daemon's default.</param>
public sealed record LogSetting(string Driver = "", IReadOnlyDictionary<string, string>? Options = null);

/// <summary>
/// Everything about a container the owner can set, as one value: read from an inspect, a snapshot or a form, and
/// written into a create body (<see cref="Gitberth.Network.ContainerSettingsJson"/>). Lists and maps compare by
/// content only through <see cref="Gitberth.Network.SettingsDiff"/>; record equality is by reference for them.
/// </summary>
public sealed record ContainerSettings
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>As Config.Image has it: postgres:18, ghcr.io/x/y@sha256:…, or an image ID.</summary>
    public string Image { get; init; } = "";
    /// <summary>Null: the image's own. Empty: none.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }
    /// <summary>Null: the image's own.</summary>
    public IReadOnlyList<string>? Command { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    /// <summary>Every variable in Docker's order, the image's ones marked FromImage.</summary>
    public IReadOnlyList<EnvSetting> Env { get; init; } = [];
    public IReadOnlyList<PortRule> Ports { get; init; } = [];
    public IReadOnlyList<MountSetting> Mounts { get; init; } = [];
    /// <summary>"" for a bridge network (default, gitberth…), host, none, container:&lt;name&gt;.</summary>
    public string NetworkMode { get; init; } = "";
    /// <summary>As Docker has it, the default short ID included: a recreate keeps it.</summary>
    public string? Hostname { get; init; }
    public string? Domainname { get; init; }
    /// <summary>host:ip.</summary>
    public IReadOnlyList<string> ExtraHosts { get; init; } = [];
    public IReadOnlyList<string> Dns { get; init; } = [];
    public IReadOnlyList<string> DnsSearch { get; init; } = [];
    public IReadOnlyList<string> DnsOptions { get; init; } = [];
    public RestartSetting Restart { get; init; } = new();
    public ResourceSetting Resources { get; init; } = new();
    /// <summary>Null: Docker's 64 MiB.</summary>
    public long? ShmSizeBytes { get; init; }
    public IReadOnlyList<UlimitSetting> Ulimits { get; init; } = [];
    public HealthSetting Health { get; init; } = new();
    /// <summary>Labels the container has beyond its image's (Gitberth's and Compose's included; the UI keeps those read-only).</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = None;
    /// <summary>The image's own labels, shown read-only.</summary>
    public IReadOnlyDictionary<string, string> ImageLabels { get; init; } = None;
    public IReadOnlyList<DeviceSetting> Devices { get; init; } = [];
    /// <summary>--gpus: null, "all", a count ("2") or "device=0,1".</summary>
    public string? Gpus { get; init; }
    public IReadOnlyList<string> CapAdd { get; init; } = [];
    public IReadOnlyList<string> CapDrop { get; init; } = [];
    public bool Privileged { get; init; }
    public IReadOnlyList<string> SecurityOpt { get; init; } = [];
    public bool ReadOnlyRootfs { get; init; }
    public bool Init { get; init; }
    public IReadOnlyDictionary<string, string> Sysctls { get; init; } = None;
    public LogSetting Logging { get; init; } = new();
    public string? StopSignal { get; init; }
    public int? StopTimeoutSeconds { get; init; }
    public bool Tty { get; init; }
    public bool OpenStdin { get; init; }
}

/// <summary>What Gitberth itself keeps about an app; none of it touches the container.</summary>
public sealed record AppOptions
{
    /// <summary>What Gitberth calls it (the app's name, or the container's title).</summary>
    public string Name { get; init; } = "";
    /// <summary>The container port Open, domains and the health URL use (the web marker).</summary>
    public int? WebPort { get; init; }
    public string? HealthCheckPath { get; init; }
    public bool GatherStats { get; init; } = true;
    /// <summary>Whether Settings › Notifications tells about this app (kept by its container's name, like GatherStats).</summary>
    public bool Notify { get; init; } = true;
    public RestartInterval? ScheduledRestart { get; init; }
    public UpdatePolicy Updates { get; init; } = UpdatePolicy.Notify;
    /// <summary>The reference updates follow; null: Config.Image.</summary>
    public string? TrackedImage { get; init; }
    public int? RegistryId { get; init; }
    /// <summary>Variable names the owner marked secret; empty: Gitberth's guess.</summary>
    public IReadOnlyList<string> SecretKeys { get; init; } = [];
    public bool Pinned { get; init; }
}

/// <summary>Git apps only: where the code comes from and how it's built (AppSettingsModel's Git fields).</summary>
public sealed record GitOptions(string Branch, string? Subdirectory, DockerfileMode DockerfileMode, string? CustomDockerfile, string? ProjectHint,
    bool AutoDeploy, bool VercelVariables);

/// <summary>What an encrypted snapshot holds: enough to create the container again.</summary>
public sealed record AppSnapshot(ContainerSettings Settings, IReadOnlyList<NetworkJoin> Networks);

/// <summary>An image as Docker describes it, for "From the image" hints and prefills.</summary>
public sealed record ImageFacts(
    string Id, IReadOnlyList<string> Tags, IReadOnlyList<string> RepoDigests, DateTime? Created, long SizeBytes, string Os, string Architecture,
    IReadOnlyList<EnvSetting> Env, IReadOnlyList<string>? Entrypoint, IReadOnlyList<string>? Command, string? WorkingDir, string? User,
    IReadOnlyList<ExposedPort> ExposedPorts, IReadOnlyList<string> Volumes, IReadOnlyDictionary<string, string> Labels, HealthSetting Health,
    string? StopSignal)
{
    /// <summary>
    /// Registry once a registry is known to have the image (Gitberth pulled it, or a check found its reference there), else
    /// Local. RepoDigests don't tell: Docker's containerd image store gives every image one, local builds included. Set by
    /// whoever knows (ImageService.FactsAsync); a plain read of an image leaves it Local.
    /// </summary>
    public ImageOrigin Origin { get; init; } = ImageOrigin.Local;

    /// <summary>
    /// Read from the image on this host; false when it was read from its registry before it's pulled
    /// (ImageService.RemoteFactsAsync), so it's still pulled first.
    /// </summary>
    public bool OnHost { get; init; } = true;
}

// ---- The catalog ----

public enum AppState
{
    Running, Starting, Healthy, Unhealthy, Restarting, Paused, Stopped, Crashed, Created, Dead,
    /// <summary>A deploy is queued or running (Git).</summary>
    Deploying,
    /// <summary>A change, update or create is running.</summary>
    Changing,
    /// <summary>A change waits for the owner: keep the new container or put the original back.</summary>
    NeedsDecision,
    /// <summary>It has a row but no container.</summary>
    Missing,
    /// <summary>Created by Gitberth, but its first start failed or hasn't happened: nothing in Docker yet.</summary>
    NotCreated,
    Unknown,
    /// <summary>
    /// Its container was renamed outside Gitberth: it runs (or is kept) under another name (<see cref="AppSummary.RenamedTo"/>),
    /// so nothing is created again by its old name. Rename it back, or forget this app and use the other one.
    /// </summary>
    Renamed,
}

public sealed record UpdateBadge(bool Available, string Text, DateTime? CheckedAt, string? Error);

/// <param name="Key">The container's name: the page's URL segment.</param>
/// <param name="AppId">The row's ID; null until it's adopted.</param>
/// <param name="Image">
/// The reference its container was created with (Config.Image), even once the tag names another image and docker ps shows
/// the image's ID instead; without a container, its row's. For Git apps, the image it runs.
/// </param>
/// <param name="Repo">Git apps: owner/repo · branch · short commit.</param>
/// <param name="System">Gitberth itself or its tunnel.</param>
public sealed record AppSummary(
    string Key, int? AppId, string Title, string Container, AppSource Source, ContainerRole Role, ImageOrigin Origin,
    string Image, string? Repo, AppState State, string StateText, string? ContainerId, IReadOnlyList<AddressChip> Chips,
    ulong? MemoryBytes, double? CpuPercent, UpdateBadge? Update, bool Pinned, bool System, DateTime? StartedAt)
{
    public bool Running => State is AppState.Running or AppState.Starting or AppState.Healthy or AppState.Unhealthy;

    public string Href => "apps/" + Uri.EscapeDataString(Key);

    /// <summary>
    /// <see cref="AppState.Renamed"/>: the name its container goes by now (its own key on the Apps list, for a link); null
    /// otherwise.
    /// </summary>
    public string? RenamedTo { get; init; }

    /// <summary>
    /// It's a database, cache or queue (<see cref="ContainerService.IsDatabase"/>, the one answer the backend uses for its
    /// stop timeout, the shared-data guards and automatic updates): the Settings tab greys out automatic updates by this.
    /// </summary>
    public bool Database { get; init; }

    /// <summary>
    /// The tag it was created with (<see cref="Image"/>) names another image on this host now (pulled again, or rebuilt):
    /// the container still runs the one it was created from.
    /// </summary>
    public bool TagMoved { get; init; }
}

public sealed record AppList(IReadOnlyList<AppSummary> Apps, string? DockerError)
{
    public static readonly AppList Empty = new([], null);
}

/// <summary>One app as its page needs it.</summary>
/// <param name="Row">With Ports, EnvVars, Volumes, Domains and Networks loaded; null until adopted.</param>
/// <param name="Details">DockerCli's read of the container; null when there's none.</param>
/// <param name="Git">Deployments and status, for Git apps.</param>
public sealed record AppView(AppSummary Summary, AppDefinition? Row, ContainerDetails? Details, JsonObject? Inspect, ContainerChange? LatestChange,
    AppDetail? Git, ContainerAddresses? Addresses, AppAddresses? AppAddresses);

// ---- Settings ----

public sealed record SettingProblem(SettingField? Field, string Message);

/// <summary>What may be changed, and why not.</summary>
/// <param name="Recreate">Why recreate-class fields can't be applied; null when they can.</param>
/// <param name="Live">Why live fields can't be applied; null when they can.</param>
/// <param name="Networks">Why networks can't be joined or left; null when they can.</param>
/// <param name="ViaDeploy">Git app: recreate-class fields apply with a deploy.</param>
/// <param name="Compose">Compose-managed: recreate-class fields become YAML.</param>
/// <param name="ReadOnly">Fields this app never edits (Image for Git apps, Ports on the host's network…).</param>
public sealed record EditAccess(string? Recreate, string? Live, string? Networks, bool ViaDeploy, ComposeService? Compose, IReadOnlySet<SettingField> ReadOnly);

/// <param name="ContainerId">The container as loaded; null when there's none (the settings are the snapshot's).</param>
public sealed record SettingsView(AppSummary App, string? ContainerId, ContainerSettings Container, AppOptions Options, GitOptions? Git,
    EditAccess Access, ImageFacts? Image);

/// <summary>A change as the form holds it: as loaded, and as wanted. Only what differs is applied.</summary>
public sealed record SettingsEdit(string? ContainerId, ContainerSettings Loaded, ContainerSettings Wanted, AppOptions LoadedOptions, AppOptions WantedOptions,
    GitOptions? LoadedGit = null, GitOptions? WantedGit = null);

/// <summary>One CHANGE line, values masked.</summary>
public sealed record SettingLine(SettingField? Field, string Label, string Before, string After, ApplyKind How);

public sealed record SettingsPlan
{
    public required string Key { get; init; }
    public required SettingsEdit Edit { get; init; }
    public IReadOnlyList<SettingLine> Lines { get; init; } = [];
    /// <summary>The engine's plan when the container is recreated or updated live; null otherwise.</summary>
    public PortChangePlan? Change { get; init; }
    /// <summary>The one line next to Apply.</summary>
    public string? Consequence { get; init; }
    public string? Blocked { get; init; }
    public string? ComposeSnippet { get; init; }
    public string? ComposeCommand { get; init; }
    public IReadOnlyList<SettingProblem> Problems { get; init; } = [];
    public bool NeedsConfirm => Change is { LiveOnly: false };
    public bool CanApply => Blocked is null && Problems.Count == 0 && Lines.Count > 0 && ComposeSnippet is null;
}

public enum ApplyTiming { Now, NextDeploy }

public sealed record SettingsOutcome(bool Ok, string Message, PortChangeJob? Job = null, int? DeploymentId = null,
    IReadOnlyList<SettingProblem>? Problems = null, SyncReport? Domains = null);

// ---- Engine requests (used by B's PortChanger, C's creator and updates, A's actions) ----

public sealed record ContainerEditRequest
{
    /// <summary>The container as the owner loaded it: one recreated since makes the plan refuse.</summary>
    public required string ContainerId { get; init; }
    public required ContainerSettings Loaded { get; init; }
    public required ContainerSettings Wanted { get; init; }
    public ChangeKind Kind { get; init; } = ChangeKind.Settings;
    /// <summary>Update and Rollback: the image ID Wanted.Image must name when the plan is made and applied.</summary>
    public string? ExpectedImageId { get; init; }
    /// <summary>The app's health URL path, checked after the start; null for none.</summary>
    public string? HealthPath { get; init; }
    public int? WebPort { get; init; }
    public IReadOnlyCollection<string> SecretKeys { get; init; } = [];
    /// <summary>What the plan's words call it: the app's name as every page shows it (Adminer for gb-adminer). Null: its container's name.</summary>
    public string? Title { get; init; }
}

public sealed record NewContainerRequest
{
    public required string Name { get; init; }
    public required ContainerSettings Settings { get; init; }
    /// <summary>Every network, the shared one (with the app's alias) first.</summary>
    public required IReadOnlyList<NetworkJoin> Networks { get; init; }
    public bool Start { get; init; } = true;
    public string? HealthPath { get; init; }
    public int? WebPort { get; init; }
    public IReadOnlyCollection<string> SecretKeys { get; init; } = [];

    /// <summary>
    /// The owner chose to use the data a volume left by an earlier app holds (gb-&lt;slug&gt;-data, say). Without it, a database
    /// is refused such a volume: it may hold another major version's data.
    /// </summary>
    public bool UseExistingData { get; init; }
}

// ---- Activity ----

public enum ActivityKind { Created, Adopted, Settings, Live, Recreated, Updated, RolledBack, Started, Stopped, Restarted, Removed,
    UpdateFound, RecreatedOutside, Deploy, Change, Event }

public enum ActivityTone { Neutral, Good, Warning, Danger }

/// <param name="Href">Where it leads: a deploy's log, say.</param>
public sealed record ActivityItem(DateTime At, ActivityKind Kind, string Text, ActivityTone Tone, string? Href = null, int? DeploymentId = null,
    int? ChangeId = null);

/// <summary>One line of the dashboard's Recent deploys: which app, and what was deployed.</summary>
/// <param name="Key">The app's container name, its key in Apps; empty when its row is gone.</param>
/// <param name="App">The app's name, as Apps shows it.</param>
public sealed record RecentDeploy(string Key, string App, ActivityItem Item);

/// <summary>What the update badge of one app is worked out from.</summary>
/// <param name="Tracked">The reference updates follow: App.ImageRef, else Config.Image when it's a tag; null: pinned.</param>
/// <param name="RunningImageId">The image the container runs, by ID; null when there's no container.</param>
/// <param name="Policy">Its update policy: When I ask (Off) gets no badge. Tell me for a container without a row.</param>
public sealed record UpdateSubject(string Key, string? Tracked, string? RunningImageId, bool Git, UpdatePolicy Policy = UpdatePolicy.Notify);
