using Gitberth.Apps;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;

namespace Gitberth.Sources;

/// <summary>
/// How much a parse issue matters: an Error blocks creating until it's fixed, Dropped is something the paste had that
/// the app won't (with why), and a Note says how Gitberth read something.
/// </summary>
public enum ParseSeverity { Note, Dropped, Error }

/// <param name="Fragment">The part of the paste it's about, as written: "--rm", "-v ./data:/data", "depends_on".</param>
public sealed record ParseIssue(ParseSeverity Severity, string Text, string? Fragment = null);

/// <param name="Networks">
/// Extra networks named (gitberth is always added by the creator). One named "" (or the shared network's own name) carries
/// aliases on the shared network, from a --network-alias with no network named.
/// </param>
/// <param name="LanAddress">Its own LAN address: an address on gitberth-lan.</param>
public sealed record ParsedApp(string? Name, ContainerSettings Settings, IReadOnlyList<NetworkJoin> Networks, string? LanAddress,
    IReadOnlyList<ParseIssue> Issues, string? Platform, bool PullFirst)
{
    public bool Ok => Issues.All(i => i.Severity != ParseSeverity.Error);

    /// <summary>docker run -P: every port the image exposes is published on the LAN, with suggested host ports.</summary>
    public bool PublishAll { get; init; }
}

public sealed record ComposeServices(IReadOnlyList<string> Services, IReadOnlyList<ParseIssue> Issues);

public sealed record NewAppDraft
{
    public required AppSource Source { get; init; }
    public required string Name { get; init; }
    public required ContainerSettings Settings { get; init; }
    public IReadOnlyList<NetworkJoin> ExtraNetworks { get; init; } = [];
    public string? LanAddress { get; init; }
    public AppOptions Options { get; init; } = new();
    public int? RegistryId { get; init; }
    public string? Platform { get; init; }
    public bool PullFirst { get; init; }
    public IReadOnlyList<ParseIssue> Issues { get; init; } = [];

    /// <summary>
    /// The slug the draft's volume names were made with (gb-&lt;slug&gt;-data): when the app ends up with another (it was
    /// renamed, or the name was taken meanwhile), those volumes follow it. Null when none were made.
    /// </summary>
    public string? Slug { get; init; }

    /// <summary>
    /// The image was read when the draft was made (on the host, or from its registry before it's pulled), so its ports and
    /// volumes are the owner's choice as Configure shows them. Without it, the create fills them in from the image once
    /// it's pulled (a registry or local image's exposed ports on the LAN, and a named volume for every VOLUME nothing mounts).
    /// </summary>
    public bool ImageRead { get; init; }

    /// <summary>
    /// What the image said when the draft was made: the host's image, or its registry's config before it's pulled
    /// (<see cref="ImageFacts.OnHost"/> false). Null when neither could be read.
    /// </summary>
    public ImageFacts? Facts { get; init; }

    /// <summary>
    /// The owner chose to use the data a volume left by an earlier app holds (gb-&lt;slug&gt;-data from an app removed
    /// before): a database is refused such a volume without it (NewContainerRequest.UseExistingData).
    /// </summary>
    public bool UseExistingData { get; init; }
}

public sealed record CreatePlan(NewAppDraft Draft, string Slug, string Container, string? NameTaken, string DockerRun, bool NeedsPull, DiskSpace Disk,
    IReadOnlyList<SettingProblem> Problems, IReadOnlyList<string> Notes)
{
    public bool CanCreate => Problems.Count == 0 && !(NeedsPull && Disk.TooLow);

    /// <summary>The line under Create: "Creates gb-uptime-kuma and starts it. Reached at 10.1.1.80:3001."</summary>
    public string Consequence { get; init; } = "";

    /// <summary>The container exactly as it's asked of the engine: Gitberth's labels, restart policy and logging included.</summary>
    public ContainerSettings Settings { get; init; } = Draft.Settings;

    /// <summary>Every network it joins, the shared one (with its alias) first.</summary>
    public IReadOnlyList<NetworkJoin> Networks { get; init; } = [];
}

public sealed record CreateResult(bool Ok, string Message, string? Key, int? AppId, string? LastOutput);

/// <summary>A new app being made in the background: its pull, its row, then the engine's create and checks.</summary>
public sealed class CreateJob
{
    private string _phase = "Waiting";

    internal CreateJob(string slug) => Slug = slug;

    public string Slug { get; }

    /// <summary>"Pulling", "Creating", "Starting", "Checking", "Done", "Failed".</summary>
    public string Phase => Volatile.Read(ref _phase);

    public PullJob? Pull { get; private set; }

    public PortChangeJob? Engine { get; private set; }

    public Task<CreateResult> Completion { get; internal set; } = Task.FromResult(new CreateResult(false, "", null, null, null));

    public event Action? Changed;

    internal void Enter(string phase)
    {
        Volatile.Write(ref _phase, phase);
        Changed?.Invoke();
    }

    internal void Pulling(PullJob pull)
    {
        Pull = pull;
        pull.Changed += Raise;
        Enter("Pulling");
    }

    internal void Running(PortChangeJob engine)
    {
        Engine = engine;
        engine.Changed += Raise;
        Enter("Starting");
    }

    internal void Raise() => Changed?.Invoke();
}
