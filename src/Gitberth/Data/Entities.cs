using Gitberth.Apps;
using Gitberth.Network;

namespace Gitberth.Data;

public enum DockerfileMode { Auto, Custom }

public enum DesiredState { Running, Stopped }

// Stored as strings: values are only ever added, never renamed. Webhook: started by GitHub telling Gitberth about a push;
// Promote: a preview's commit made the production version.
public enum DeployTrigger { Created, Manual, Start, ConfigChange, Rollback, Poll, Retry, Restart, Webhook, Promote }

public enum DeployStatus { Queued, Cloning, Building, Starting, Succeeded, Failed, RolledBack, Cancelled }

/// <summary>Which branches and pull requests of a Git app get a preview of their own.</summary>
public enum PreviewMode { Off, PullRequests, PullRequestsAndBranches }

/// <summary>What a preview is a preview of.</summary>
public enum PreviewKind { Branch, PullRequest }

/// <summary>
/// Where an environment variable applies, as Vercel's environments: every deploy, only the production one (the app's own
/// container), or only previews. A Production or Preview value wins over an All one with the same name.
/// </summary>
public enum EnvScope { All, Production, Preview }

/// <summary>
/// One app: a container Gitberth runs or knows. A Git app is a repo + branch (+ optional subfolder) and how its container
/// should run; any other app's settings live in its container, and the row keeps Gitberth's own data about it.
/// </summary>
public class AppDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string? Subdirectory { get; set; }

    /// <summary>Auto = detect and generate the Dockerfile on every deploy; Custom = use <see cref="CustomDockerfile"/>.</summary>
    public DockerfileMode DockerfileMode { get; set; } = DockerfileMode.Auto;
    public string? CustomDockerfile { get; set; }
    /// <summary>Which project to run when a .NET solution has several (relative path of the .csproj).</summary>
    public string? ProjectHint { get; set; }
    public string? DetectedKind { get; set; }
    public string? DetectedSummary { get; set; }
    /// <summary>The container port detection gave last time. A port mapping still on it moves when the app's port changes.</summary>
    public int? DetectedPort { get; set; }

    public bool AutoDeploy { get; set; } = true;
    public DesiredState DesiredState { get; set; } = DesiredState.Running;
    public string RestartPolicy { get; set; } = "unless-stopped";
    public int? MemoryLimitMb { get; set; }
    public double? CpuLimit { get; set; }
    public string? HealthCheckPath { get; set; }
    /// <summary>Whether Gitberth tells the app where it runs with Vercel's variables (VERCEL_URL and friends). On unless turned off.</summary>
    public bool VercelVariables { get; set; } = true;

    public string? CurrentImage { get; set; }
    public string? CurrentCommit { get; set; }
    /// <summary>Commit of the most recent build attempt, successful or not. The watcher only retries it within RepoWatcher.ShouldRetry's limits.</summary>
    public string? LastAttemptedCommit { get; set; }
    /// <summary>Settings were saved but the running container still uses the old ones.</summary>
    public bool ConfigChangePending { get; set; }
    /// <summary>Saved changes (branch, Dockerfile, build-time variables, volumes) only take effect after a new build.</summary>
    public bool RebuildPending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How the app came to Gitberth: every row from before the unified apps is a Git app.</summary>
    public AppSource Source { get; set; } = AppSource.Git;
    /// <summary>The container's name when it isn't gb-&lt;slug&gt; (Existing apps). Null for every app Gitberth creates.</summary>
    public string? Container { get; set; }
    /// <summary>The container's ID when Gitberth last looked: a new one under the same name means it was recreated.</summary>
    public string? ContainerId { get; set; }
    /// <summary>The reference updates follow (postgres:18); null for Git apps.</summary>
    public string? ImageRef { get; set; }
    public int? RegistryId { get; set; }
    public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.Off;
    /// <summary>The image the app ran before its last update (an ID), for Roll back.</summary>
    public string? PreviousImage { get; set; }
    /// <summary>AppSnapshot as JSON, encrypted with SecretProtector: the settings to create the container again.</summary>
    public string? SpecProtected { get; set; }
    /// <summary>Git apps: the settings beyond the classic ones (ContainerSettings minus what has columns or rows), as JSON. Null: none.</summary>
    public string? RuntimeExtras { get; set; }
    /// <summary>Variable names marked secret, as a JSON array; null: Gitberth's own guess (EnvRules).</summary>
    public string? SecretKeys { get; set; }
    /// <summary>Shown in the menu's Pinned section.</summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Set on a preview's own row: the Git app it previews. A preview is a Git app of its own (its own slug, container,
    /// image repository, queue slot, lock, volumes and domain), kept out of the Apps list and shown on its parent's page.
    /// No foreign key: adding one would make EF Core rebuild Apps. <see cref="PreviewInfo"/> holds what it previews.
    /// </summary>
    public int? ParentAppId { get; set; }
    /// <summary>A Git app's previews: off, or for pull requests, or for pull requests and branches.</summary>
    public PreviewMode PreviewMode { get; set; } = PreviewMode.Off;
    /// <summary>How many of its previews may run at once (a home server's room); more wait until one goes.</summary>
    public int PreviewLimit { get; set; } = 3;
    /// <summary>A preview with no new commit for this many days is removed.</summary>
    public int PreviewExpiryDays { get; set; } = 7;
    /// <summary>Also preview pull requests from forks: their code is anyone's, and it's built on this server. Off unless chosen.</summary>
    public bool PreviewForks { get; set; }
    /// <summary>Put previews' domains behind Cloudflare Access, when it's set up. On unless turned off.</summary>
    public bool ProtectPreviews { get; set; } = true;
    /// <summary>
    /// The production branch's commit when a preview was promoted over it: production stays on the promoted commit, and the
    /// watcher doesn't deploy this one. The next new commit on the branch, or a manual deploy, clears it; so does a promote
    /// that never ran or failed (RepoWatcher.ForgetUnheldPromotionAsync).
    /// </summary>
    public string? PromotedOverCommit { get; set; }

    /// <summary>A preview's own row (see <see cref="ParentAppId"/>).</summary>
    public bool IsPreview => ParentAppId is not null;

    public List<PortMapping> Ports { get; set; } = [];
    public List<EnvVar> EnvVars { get; set; } = [];
    public List<VolumeMount> Volumes { get; set; } = [];
    public List<CustomDomain> Domains { get; set; } = [];
    /// <summary>Networks the app is on besides the shared one.</summary>
    public List<AppNetwork> Networks { get; set; } = [];

    /// <summary>Its container's name: gb-&lt;slug&gt;, or the name of the container an Existing app is.</summary>
    public string ContainerName => Container ?? ContainerNameFor(Slug);

    /// <summary>What containers on the shared network call it: an Existing app its container's name, any other app its slug.</summary>
    public string NetworkName => Source == AppSource.Existing ? ContainerName : Slug;

    /// <summary>Gitberth deploys it from a repo: its database row is the truth for its settings, and a deploy applies them.</summary>
    public bool Deployed => Source == AppSource.Git;

    public static string ContainerNameFor(string slug) => $"gb-{slug}";
    public string ImageRepository => $"gitberth/{Slug}";
    public string RepoFullName => $"{Owner}/{Repo}";
    public string RepoWebUrl => $"https://github.com/{Owner}/{Repo}";
}

/// <summary>A port of an app: the container port, who reaches it (its Access), and the host port that leads to it when it's published.</summary>
public class PortMapping
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public PortAccess Access { get; set; }
    /// <summary>Null when the port is Internal: it isn't published.</summary>
    public int? HostPort { get; set; }
    /// <summary>The one address it's published on, for Custom only; LAN and Host only have theirs (<see cref="ContainerPorts.BindAddress"/>).</summary>
    public string? HostIp { get; set; }
    /// <summary>The port Open, domains, the health URL and scheduled jobs use. At most one per app, and only a TCP one; none for a worker.</summary>
    public bool IsWeb { get; set; }
}

/// <summary>A network an app is on besides the shared one: joined live, and passed again by every deploy.</summary>
public class AppNetwork
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Network { get; set; } = "";
    /// <summary>A name it answers to there besides its network name (its slug), like db for apps that expect one.</summary>
    public string? Alias { get; set; }
    /// <summary>Its own address on the LAN (gitberth-lan). Only the app's own container takes it, never a deploy's temporary one.</summary>
    public string? IPv4Address { get; set; }
}

/// <summary>
/// A network Gitberth joined a container it didn't deploy to. Docker forgets it when something recreates the container
/// (docker run, compose up, an image update), so Gitberth joins it again when the container starts, until it's left.
/// </summary>
public class KeptNetwork
{
    public string ContainerName { get; set; } = "";
    public string Network { get; set; } = "";
    /// <summary>A name it answers to there besides its own.</summary>
    public string? Alias { get; set; }
    /// <summary>Its own address on the LAN (gitberth-lan), pinned so it stays the same across recreates.</summary>
    public string? IPv4Address { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A port a container listens on that its image doesn't declare, noted on its Ports card. Nothing restarts for it.</summary>
public class ContainerPortNote
{
    public string ContainerName { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "tcp";
}

/// <summary>
/// The journal's row for one port change (<see cref="ContainerChange"/>, field for field). It never holds the container's
/// config or environment, which carry secrets.
/// </summary>
public class ContainerChangeEntry
{
    public int Id { get; set; }
    public string ContainerName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string OldId { get; set; } = "";
    public string? NewId { get; set; }
    public string OldName { get; set; } = "";
    public string TempName { get; set; } = "";
    public bool WasRunning { get; set; }
    /// <summary>Docker's JSON for it: {"Name":"unless-stopped","MaximumRetryCount":0}.</summary>
    public string OldRestartPolicy { get; set; } = "";
    public ChangeStep Step { get; set; }
    public DateTime? StopAt { get; set; }
    public ChangeOutcome Outcome { get; set; }
    public int? OfflineMs { get; set; }
    public string? Error { get; set; }
    public string? Notice { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public ChangeKind Kind { get; set; } = ChangeKind.Ports;
    /// <summary>The settings it changed, by name (Env,Memory); never their values.</summary>
    public string? Fields { get; set; }
    /// <summary>The new container's restart policy as Docker's JSON, when the change sets one; null: the original's.</summary>
    public string? NewRestartPolicy { get; set; }
    /// <summary>The image before and after an update or rollback: references, or IDs when there's no tag.</summary>
    public string? ImageBefore { get; set; }
    public string? ImageAfter { get; set; }
}

/// <summary>Credentials for a registry, for pulls and update checks. The secret is encrypted with SecretProtector.</summary>
public class RegistryCredential
{
    public int Id { get; set; }
    /// <summary>docker.io, ghcr.io, registry.lan:5000: lower case, no scheme.</summary>
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>The last remote check of an image reference, shared by every app on it.</summary>
public class ImageCheck
{
    public string Reference { get; set; } = "";     // docker.io/library/postgres:18
    public string? RemoteDigest { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? Error { get; set; }
    public DateTime? RetryAfter { get; set; }
}

/// <summary>Something that happened to an app, for its Activity tab. Text never holds a secret value.</summary>
public class ActivityEntry
{
    public int Id { get; set; }
    public string ContainerName { get; set; } = "";
    public int? AppId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public ActivityKind Kind { get; set; }            // stored as string
    public ActivityTone Tone { get; set; }            // stored as string
    public string Text { get; set; } = "";
}

public class EnvVar
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>Value encrypted with ASP.NET Core Data Protection.</summary>
    public string ProtectedValue { get; set; } = "";
    public bool IsSecret { get; set; }
    /// <summary>Also passed to the image build as a build arg (needed for VITE_*, NEXT_PUBLIC_*, ...).</summary>
    public bool BuildTime { get; set; }
    /// <summary>Which deploys get it: all, the production one, or previews. One name can have a value per scope.</summary>
    public EnvScope Scope { get; set; } = EnvScope.All;
}

/// <summary>
/// What a preview previews, kept with the preview's own app row (<see cref="AppDefinition.ParentAppId"/>): a branch, or a
/// pull request and its head branch.
/// </summary>
public class PreviewInfo
{
    /// <summary>The preview's own app row.</summary>
    public int AppId { get; set; }
    /// <summary>The Git app it previews (also on the preview's row).</summary>
    public int ParentAppId { get; set; }
    public PreviewKind Kind { get; set; }
    /// <summary>The branch: the pull request's head branch for a pull request.</summary>
    public string Branch { get; set; } = "";
    public int? PullRequest { get; set; }
    /// <summary>The pull request's title, for the Previews list and the comment on it.</summary>
    public string? Title { get; set; }
    /// <summary>What a deploy fetches: refs/heads/&lt;branch&gt;, or refs/pull/&lt;n&gt;/head for a pull request.</summary>
    public string Ref { get; set; } = "";
    /// <summary>The newest commit seen on it.</summary>
    public string? HeadSha { get; set; }
    /// <summary>A pull request from a fork: someone else's code (previewed only when the app allows forks).</summary>
    public bool FromFork { get; set; }
    /// <summary>The comment on the pull request that Gitberth keeps up to date with the preview's address.</summary>
    public long? CommentId { get; set; }
    /// <summary>Its last new commit or update: it's removed after the parent's PreviewExpiryDays without one.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class VolumeMount
{
    public int Id { get; set; }
    public int AppId { get; set; }
    /// <summary>A short volume name (stored as gb-&lt;slug&gt;-&lt;name&gt;) or an absolute path on the Docker host.</summary>
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
}

/// <summary>A domain the app is served on through the Cloudflare Tunnel on this host, like a domain on a Vercel project.</summary>
public class CustomDomain
{
    public int Id { get; set; }
    public int AppId { get; set; }
    /// <summary>Lowercase, e.g. app.example.com. A domain belongs to one app.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>
    /// Taken over from a route in Cloudflare's dashboard that only sends some paths to the app. Gitberth leaves those rules
    /// as they are until someone chooses to serve every path: widening a route can expose more of an app than intended.
    /// </summary>
    public bool KeepPaths { get; set; }
    /// <summary>
    /// Where Gitberth last sent this domain in the tunnel: the service it wrote, or the one the route it took over had.
    /// It tells Gitberth's rules for the domain from anyone else's, even after the app's port changes.
    /// </summary>
    public string? Service { get; set; }
    /// <summary>
    /// Chosen on the Domains card as the app's own address: what Gitberth tells the app in VERCEL_URL and friends. With none
    /// chosen, the shortest domain is the main one, the way Vercel picks a project's production URL.
    /// </summary>
    public bool Main { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Behind Cloudflare Access: visitors sign in (an emailed code) before they reach the app.</summary>
    public bool Protected { get; set; }
    /// <summary>The Cloudflare Access application Gitberth made for it; only one Gitberth made is ever changed or removed.</summary>
    public string? AccessAppId { get; set; }
}

/// <summary>
/// A domain taken off an app, or deleted with it, whose tunnel route and DNS record Gitberth still has to remove. Kept
/// until that's done, so a failed attempt is retried and the route is never taken over again in the meantime.
/// </summary>
public class DomainRemoval
{
    public string Hostname { get; set; } = "";
    /// <summary>Where Gitberth routed it, so only that route goes and anything else on the host stays.</summary>
    public string? Service { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Deployment
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public DeployTrigger Trigger { get; set; }
    public DeployStatus Status { get; set; } = DeployStatus.Queued;
    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }
    public string? ImageTag { get; set; }
    public string? Error { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>The Gitberth build that ran it (<see cref="BuildStamp"/>), so a failure can be retried after an update.</summary>
    public string? GitberthBuild { get; set; } = BuildStamp.Current;

    public bool IsActive => Status is DeployStatus.Queued or DeployStatus.Cloning or DeployStatus.Building or DeployStatus.Starting;
    public string ShortSha => CommitSha is { Length: >= 7 } ? CommitSha[..7] : CommitSha ?? "";
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
