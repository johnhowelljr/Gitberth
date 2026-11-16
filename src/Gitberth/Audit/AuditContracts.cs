namespace Gitberth.Audit;

/// <summary>Who made a change: a person, a token, a hook, GitHub, Gitberth itself, or something outside Gitberth.</summary>
public enum AuditActorKind { Gitberth, User, ApiToken, DeployHook, GitHub, Docker, Cloudflare }

/// <summary>Where the change came in: the UI, an AI agent or script, a webhook, or one of Gitberth's own jobs.</summary>
public enum AuditVia { Gitberth, UI, Mcp, Api, Webhook, Hook, Watcher, Schedule, Startup, Updates, Health, Docker, Sync }

/// <summary>How the change ended.</summary>
public enum AuditOutcome { Succeeded, Failed, Denied }

/// <summary>Who made a change, as the audit log shows them.</summary>
/// <param name="Kind">What sort of actor it is.</param>
/// <param name="Name">"admin", a token's name, a deploy hook's name, "GitHub", "Gitberth", "outside Gitberth".</param>
/// <param name="Via">Where it came in.</param>
/// <param name="Ip">The client's address, when the change came over HTTP or from a browser.</param>
public sealed record AuditActor(AuditActorKind Kind, string Name, AuditVia Via, string? Ip = null)
{
    /// <summary>Gitberth acting on its own, with nothing more specific known.</summary>
    public static readonly AuditActor Gitberth = new(AuditActorKind.Gitberth, "Gitberth", AuditVia.Gitberth);

    /// <summary>A change made to Docker by something other than Gitberth (docker on the host, Compose, another tool).</summary>
    public static readonly AuditActor Outside = new(AuditActorKind.Docker, "outside Gitberth", AuditVia.Docker);

    /// <summary>Gitberth acting on its own, through one of its jobs (the watcher, a schedule, startup, updates…).</summary>
    public static AuditActor System(AuditVia via) => new(AuditActorKind.Gitberth, "Gitberth", via);

    /// <summary>The signed-in person, from the UI.</summary>
    public static AuditActor User(string name, string? ip) => new(AuditActorKind.User, name, AuditVia.UI, ip);
}

/// <summary>One field a change touched. Secret values never go in: pass null, or a masked placeholder.</summary>
public sealed record AuditChange(string Field, string? From, string? To);

/// <summary>
/// One change, as recorded in the audit log. <see cref="Action"/> is a dotted name such as <c>app.deploy.started</c> or
/// <c>settings.uptime.changed</c>; <see cref="Summary"/> is the one sentence a person reads.
/// </summary>
public sealed record AuditEntry(string Action, string Summary)
{
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;
    public int? AppId { get; init; }
    public string? AppName { get; init; }
    public string? Container { get; init; }
    /// <summary>What was changed when it isn't the app itself: a domain, a setting, a variable's name, a token's name…</summary>
    public string? Target { get; init; }
    public IReadOnlyList<AuditChange>? Changes { get; init; }
    /// <summary>Something to follow up with, such as the deployment's id.</summary>
    public string? Correlation { get; init; }
    /// <summary>Who made it; null means the ambient actor (<see cref="AuditScope.Current"/>).</summary>
    public AuditActor? Actor { get; init; }
    /// <summary>When it happened; null means now.</summary>
    public DateTime? At { get; init; }
}

/// <summary>
/// The actor of the code that's running: set by the UI's circuit, the API and MCP token check, the webhook and deploy-hook
/// endpoints and each background job, and read by <see cref="AuditLog.Record"/> when an entry names no actor. Long-lived
/// work (a deploy queue's consumer) must not inherit it: such work records its actor explicitly.
/// </summary>
public static class AuditScope
{
    private static readonly AsyncLocal<AuditActor?> current = new();

    /// <summary>The actor of the running code; Gitberth itself when none was set.</summary>
    public static AuditActor Current => current.Value ?? AuditActor.Gitberth;

    /// <summary>Whether an actor was set for the running code.</summary>
    public static bool IsSet => current.Value is not null;

    /// <summary>Makes <paramref name="actor"/> the actor until the returned scope is disposed.</summary>
    public static IDisposable Begin(AuditActor actor)
    {
        var previous = current.Value;
        current.Value = actor;
        return new Restore(previous);
    }

    private sealed class Restore(AuditActor? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current.Value = previous;
        }
    }
}

/// <summary>One entry as the audit log keeps it: numbered, timed, with its actor resolved and its place in the hash chain.</summary>
/// <param name="Id">Its number: entries are numbered in the order they were recorded.</param>
/// <param name="At">When it happened, in UTC, to the millisecond.</param>
/// <param name="PrevHash">The hash of the entry before it (or the chain's anchor), which its own hash covers.</param>
/// <param name="Hash">SHA-256 of <paramref name="PrevHash"/> and the entry's fields (<see cref="AuditHash"/>).</param>
public sealed record AuditRecord(long Id, DateTime At, AuditActor Actor, string Action, AuditOutcome Outcome, string Summary,
    int? AppId, string? AppName, string? Container, string? Target, IReadOnlyList<AuditChange> Changes, string? Correlation,
    string PrevHash, string Hash)
{
    /// <summary>A Docker change made outside Gitberth.</summary>
    public bool Outside => Actor.Kind == AuditActorKind.Docker;

    /// <summary>The deployment this entry is about, when its correlation names one ("deployment:12").</summary>
    public int? DeploymentId => Correlation is { } c && c.StartsWith(AuditCorrelation.Deployment, StringComparison.Ordinal)
        && int.TryParse(c.AsSpan(AuditCorrelation.Deployment.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
        ? id : null;
}

/// <summary>How entries name what they can be followed up with.</summary>
public static class AuditCorrelation
{
    public const string Deployment = "deployment:";

    /// <summary>"deployment:12": the deploy an entry is about, which the audit log page links to.</summary>
    public static string ForDeployment(int id) => Deployment + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The parts of the system the audit log page filters by.</summary>
public enum AuditArea { Apps, Deploys, Domains, Settings, SignIns, Outside }

/// <summary>Which actions make up each <see cref="AuditArea"/>: by their names' beginnings, and for Outside by the actor.</summary>
public static class AuditAreas
{
    /// <summary>The beginnings of the action names in an area, and the ones left out of it.</summary>
    public static (string[] Include, string[] Exclude) Of(AuditArea area) => area switch
    {
        AuditArea.Apps => (["app.", "preview.", "image."], ["app.deploy.", "app.update.", "app.domain."]),
        AuditArea.Deploys => (["app.deploy.", "app.update."], []),
        AuditArea.Domains => (["app.domain.", "domains.", "cloudflare."], []),
        AuditArea.Settings => (["settings.", "audit.", "github."], []),
        AuditArea.SignIns => (["auth."], []),
        _ => (["docker."], []),
    };

    /// <summary>Whether <paramref name="action"/> belongs to <paramref name="area"/> (Outside goes by its actor instead).</summary>
    public static bool Contains(AuditArea area, string action)
    {
        var (include, exclude) = Of(area);
        return include.Any(p => action.StartsWith(p, StringComparison.Ordinal)) && !exclude.Any(p => action.StartsWith(p, StringComparison.Ordinal));
    }

    public static string Label(AuditArea area) => area switch
    {
        AuditArea.Apps => "Apps",
        AuditArea.Deploys => "Deploys",
        AuditArea.Domains => "Domains",
        AuditArea.Settings => "Settings",
        AuditArea.SignIns => "Sign-ins",
        _ => "Outside Gitberth",
    };
}

/// <summary>Which entries a query wants. Every filter left null matches everything.</summary>
public sealed record AuditQuery
{
    /// <summary>From this time on (inclusive).</summary>
    public DateTime? From { get; init; }
    /// <summary>Before this time.</summary>
    public DateTime? To { get; init; }
    public AuditActorKind? ActorKind { get; init; }
    /// <summary>The actor's name, exactly ("admin", "GitHub", "cms").</summary>
    public string? Actor { get; init; }
    /// <summary>An app by its id; with <see cref="Container"/> too, an entry that names either matches.</summary>
    public int? AppId { get; init; }
    /// <summary>An app by its container's name, which outlives its id (history stays after an app is deleted).</summary>
    public string? Container { get; init; }
    /// <summary>Actions whose name starts with this ("app.deploy.") or is it.</summary>
    public string? ActionPrefix { get; init; }
    public AuditArea? Area { get; init; }
    public AuditOutcome? Outcome { get; init; }
    /// <summary>Only Docker changes made outside Gitberth.</summary>
    public bool OutsideOnly { get; init; }
    /// <summary>Words in the summary, the target, the actor, the app or the action.</summary>
    public string? Text { get; init; }
    /// <summary>For paging: only entries numbered below this one.</summary>
    public long? BeforeId { get; init; }
    /// <summary>How many to return at most (1 to 1000).</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>A page of entries, newest first, and whether there are older ones.</summary>
public sealed record AuditPage(IReadOnlyList<AuditRecord> Entries, bool More);

/// <summary>What the log holds: how many entries, from when to when, and the size of its file.</summary>
public sealed record AuditSummary(long Count, DateTime? Oldest, DateTime? Newest, long Bytes);

/// <summary>An actor that appears in the log, for the page's "who" filter.</summary>
public sealed record AuditActorRef(AuditActorKind Kind, string Name);

/// <summary>An app that appears in the log, for the page's app filter: by its container's name, with its last known title.</summary>
public sealed record AuditAppRef(string Container, string? Name, int? AppId);

/// <summary>The result of walking the hash chain.</summary>
/// <param name="Ok">Every entry is as it was recorded, and none is missing.</param>
/// <param name="Checked">How many entries were checked.</param>
/// <param name="BrokenId">The first entry that doesn't match, when one doesn't.</param>
/// <param name="Message">One sentence that says what was found.</param>
public sealed record AuditVerification(bool Ok, long Checked, long? BrokenId, string Message);

/// <summary>The two formats an export comes in.</summary>
public enum AuditExportFormat { Csv, JsonLines }

/// <summary>
/// How a deploy remembers who asked for it (Deployment.RequestedBy): a compact line, "Kind|Via|Ip|Name", that's read back
/// into the same actor when the deploy ends. Gitberth's own deploys keep nothing (null), and are told against the job that
/// started them, from the deploy's trigger.
/// </summary>
public static class AuditActors
{
    /// <summary>The line kept for <paramref name="actor"/>, or null for Gitberth itself.</summary>
    public static string? Format(AuditActor actor)
    {
        if (actor.Kind == AuditActorKind.Gitberth) return null;
        var ip = actor.Ip?.Replace("|", "", StringComparison.Ordinal) ?? "";
        return $"{actor.Kind}|{actor.Via}|{ip}|{actor.Name}";
    }

    /// <summary>The actor a kept line stands for; null for nothing kept or a line that can't be read.</summary>
    public static AuditActor? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split('|', 4);
        if (parts.Length != 4 || !Enum.TryParse<AuditActorKind>(parts[0], out var kind) || !Enum.IsDefined(kind)
            || !Enum.TryParse<AuditVia>(parts[1], out var via) || !Enum.IsDefined(via) || parts[3].Length == 0) return null;
        return new AuditActor(kind, parts[3], via, parts[2].Length == 0 ? null : parts[2]);
    }

    /// <summary>
    /// Who a deploy was for: the actor it kept, else Gitberth through the job its trigger names (the watcher for a new
    /// commit, the scheduler for a restart, startup for a start after Gitberth came up).
    /// </summary>
    public static AuditActor ForDeploy(string? requestedBy, Data.DeployTrigger trigger) =>
        Parse(requestedBy) ?? AuditActor.System(trigger switch
        {
            Data.DeployTrigger.Poll or Data.DeployTrigger.Retry or Data.DeployTrigger.Webhook => AuditVia.Watcher,
            Data.DeployTrigger.Restart => AuditVia.Schedule,
            Data.DeployTrigger.Start => AuditVia.Startup,
            _ => AuditVia.Gitberth,
        });

    /// <summary>Who to name when the actor says something a deploy's trigger doesn't: a person, a token, a hook.</summary>
    public static bool AddsToTrigger(AuditActor actor) =>
        actor.Kind is AuditActorKind.User or AuditActorKind.ApiToken or AuditActorKind.DeployHook;

    /// <summary>"admin", "admin from 10.1.1.5", "the token ci", "the deploy hook cms": who, as a sentence names them.</summary>
    public static string Describe(AuditActor actor) => actor.Kind switch
    {
        AuditActorKind.ApiToken => $"the token {actor.Name}",
        AuditActorKind.DeployHook => $"the deploy hook {actor.Name}",
        _ => actor.Name,
    };
}
