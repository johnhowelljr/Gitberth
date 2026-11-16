using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Agents;

/// <summary>
/// What every tool needs around the services it calls: finding the app it names (by its name, its key or its number, as
/// a person would say it), Gitberth's own address for links, the <c>confirm</c> check of destructive tools, and waiting
/// for a deploy to end.
/// </summary>
public sealed class AgentContext(AppCatalog catalog, AppService apps, AddressBook addresses, SettingsService settings,
    IDbContextFactory<GitberthDb> dbFactory, GitberthOptions options, IHttpContextAccessor http)
{
    /// <summary>How long a <c>wait</c> follows a deploy at most.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(10);

    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long <see cref="WaitForDeployAsync"/> follows a deploy (<see cref="WaitLimit"/>; shorter in tests).</summary>
    internal TimeSpan DeployWaitLimit { get; set; } = WaitLimit;

    /// <summary>
    /// Gitberth's address as people open it (http://192.168.1.10:8080/): the one Settings saved last, else the request's.
    /// </summary>
    public async Task<string> RootAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKeys.UiAddress, ct) is { Length: > 0 } saved && Uri.TryCreate(saved, UriKind.Absolute, out _))
            return saved.EndsWith('/') ? saved : saved + "/";
        if (http.HttpContext?.Request is { } r) return $"{r.Scheme}://{r.Host}{r.PathBase}/";
        return "http://localhost:8080/";
    }

    /// <summary>This host's LAN address, as the pages use it for the apps' addresses.</summary>
    public async Task<string> LanHostAsync(CancellationToken ct = default) => await addresses.LanHostAsync(await RootAsync(ct));

    /// <summary>
    /// The app <paramref name="app"/> names: its key (container name), its number, its name as Apps shows it, or its short
    /// name (slug). A name two apps share is refused with both keys, so the agent can say which.
    /// </summary>
    public async Task<AppView> AppAsync(string? app, CancellationToken ct = default)
    {
        var key = await KeyAsync(app, ct);
        return await catalog.GetAsync(key, await LanHostAsync(ct), ct)
            ?? throw ToolError.NotFound($"There's no app called {app}. list_apps lists them.");
    }

    /// <summary>The key of the app <paramref name="app"/> names (see <see cref="AppAsync"/>).</summary>
    public async Task<string> KeyAsync(string? app, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ToolError("Say which app: its name, its key (as list_apps gives it) or its number.");
        app = app.Trim();
        if (app.All(char.IsAsciiDigit) && int.TryParse(app, out var id) && await catalog.KeyForIdAsync(id, ct) is { } byId) return byId;
        var list = await catalog.ListAsync(await LanHostAsync(ct), ct);
        if (list.Apps.FirstOrDefault(a => string.Equals(a.Key, app, StringComparison.Ordinal)) is { } exact) return exact.Key;
        var named = list.Apps.Where(a => string.Equals(a.Title, app, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Key, app, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Key, "gb-" + app, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1) return named[0].Key;
        if (named.Count > 1)
            throw new ToolError($"More than one app is called {app}: {string.Join(", ", named.Select(a => a.Key))}. Name it by its key.");
        // A preview isn't on the list: it's found by its key or number, or here by its row.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Apps.AsNoTracking().Where(a => a.ParentAppId != null).ToListAsync(ct);
        if (rows.FirstOrDefault(a => string.Equals(a.ContainerName, app, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Slug, app, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase)) is { } preview)
            return preview.ContainerName;
        throw ToolError.NotFound($"There's no app called {app}. list_apps lists them.");
    }

    /// <summary>The app's row; an app that has none yet (a container Gitberth hasn't stored anything about) is refused with why.</summary>
    public static AppDefinition RowOf(AppView view, string doing) =>
        view.Row ?? throw ToolError.Conflict($"{view.Summary.Title} has nothing stored in Gitberth yet, so it can't {doing}.");

    /// <summary>A Git or Compose app's row (deployed by Gitberth); anything else is refused with what to do instead.</summary>
    public static AppDefinition DeployedRow(AppView view, string doing) =>
        view.Row is { Deployed: true } row ? row
        : throw ToolError.Conflict($"{view.Summary.Title} isn't deployed from a repo or a Compose file, so it can't {doing}."
            + (view.Summary.Origin == ImageOrigin.Registry ? " It runs a registry's image: check_updates and apply_update update it." : ""));

    /// <summary>
    /// A destructive tool's check: <paramref name="confirm"/> must repeat the app's name (or key), the way the UI's dialogs
    /// ask for it to be typed.
    /// </summary>
    public static void Confirm(string? confirm, AppSummary app, string doing)
    {
        if (confirm is { } c && (string.Equals(c.Trim(), app.Title, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Trim(), app.Key, StringComparison.Ordinal)))
            return;
        throw new ToolError($"To {doing} {app.Title}, pass confirm: \"{app.Title}\" (the app's name), to show it's meant.");
    }

    /// <summary>A destructive domain tool's check: <paramref name="confirm"/> must repeat the domain.</summary>
    public static void ConfirmDomain(string? confirm, string hostname, string doing)
    {
        if (confirm is { } c && string.Equals(c.Trim().TrimEnd('.'), hostname, StringComparison.OrdinalIgnoreCase)) return;
        throw new ToolError($"To {doing} {hostname}, pass confirm: \"{hostname}\" (the domain), to show it's meant.");
    }

    public async Task<Deployment?> DeploymentAsync(int id, CancellationToken ct = default) => await apps.GetDeploymentAsync(id, ct);

    /// <summary>
    /// The deploy's outcome once it ends, polled every couple of seconds for at most <see cref="WaitLimit"/>, with the last
    /// lines of its log; still running when the limit came first.
    /// </summary>
    public async Task<object> WaitForDeployAsync(int deploymentId, CancellationToken ct)
    {
        var until = DateTime.UtcNow + DeployWaitLimit;
        Deployment? dep;
        while (true)
        {
            dep = await apps.GetDeploymentAsync(deploymentId, ct);
            if (dep is null || !dep.IsActive || DateTime.UtcNow >= until) break;
            await Task.Delay(PollEvery, ct);
        }
        if (dep is null) throw ToolError.NotFound($"There's no deploy #{deploymentId}.");
        return DeployResult(dep, LogTail(dep.Id, 30), waited: true);
    }

    /// <summary>A deploy as the tools return it: its number, status, what it built, and how it ended, with its log's lines bounded (<see cref="LogLines"/>).</summary>
    public static object DeployResult(Deployment dep, IReadOnlyList<string>? log = null, bool waited = false)
    {
        IReadOnlyList<string>? lines = null;
        var truncated = false;
        if (log is not null) (lines, truncated) = LogLines.Of(log);
        return new
        {
            deployment_id = dep.Id,
            app_id = dep.AppId,
            status = dep.Status.ToString(),
            finished = !dep.IsActive,
            still_running = waited && dep.IsActive ? true : (bool?)null,
            trigger = dep.Trigger.ToString(),
            commit = dep.CommitSha is { Length: > 0 } ? dep.ShortSha : null,
            commit_message = dep.CommitMessage,
            image = dep.ImageTag,
            no_cache = dep.NoCache ? true : (bool?)null,
            error = dep.Error,
            skipped = dep.SkipReason,
            requested_by = Audit.AuditActors.Parse(dep.RequestedBy) is { } who ? Audit.AuditActors.Describe(who) : null,
            queued_at = dep.QueuedAt,
            started_at = dep.StartedAt,
            finished_at = dep.FinishedAt,
            log_tail = lines,
            truncated = truncated ? true : (bool?)null,
            hint = waited && dep.IsActive ? "It's still running: call get_deployment later, or deploy with wait again." : null,
        };
    }

    /// <summary>The most of a deploy's log a tool reads: its last lines, which a grep picks from too.</summary>
    public const int MaxLogLines = 5000;

    /// <summary>The last <paramref name="lines"/> lines of a deploy's log (it's masked as it's written: secrets never reach it).</summary>
    public IReadOnlyList<string> LogTail(int deploymentId, int lines) => DeployPaths.ReadTail(DeployPaths.LogFile(options, deploymentId), Math.Clamp(lines, 1, MaxLogLines));

    /// <summary>
    /// What a <c>wait</c> on a change in the background (an update, a settings change, a new app) gives: its result when it
    /// ends within <see cref="WaitLimit"/> (or <paramref name="limit"/>), else null, so the tool says it's still running
    /// rather than failing.
    /// </summary>
    public static async Task<T?> WaitAsync<T>(Task<T> completion, CancellationToken ct, TimeSpan? limit = null) where T : class
    {
        try
        {
            return await completion.WaitAsync(limit ?? WaitLimit, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
