namespace Gitberth.Railpack;

/// <summary>The Railpack release Gitberth ships, and what goes with it. The Dockerfile pins the same (RailpackReleaseTests).</summary>
public static class RailpackRelease
{
    /// <summary>The CLI in Gitberth's image (railpack prepare): the frontend's own binary, so both are this release.</summary>
    /// <remarks>
    /// Gitberth's sandbox leans on how 0.43.0 behaves (the security review of 2026-11-22). Before a newer release ships, check
    /// again in its source that: mise still gets an environment Railpack makes itself (core/mise/mise.go), so no value an app
    /// sets (CI, MISE_YES…) can make mise say yes to a plugin a repo's railpack.json names; each cache mount's ID is still
    /// &lt;cache-key&gt;-&lt;name&gt; (<see cref="CacheKey"/>); secrets still reach the plan by name only.
    /// </remarks>
    public const string Version = "0.43.0";

    /// <summary>Railpack's BuildKit frontend (BUILDKIT_SYNTAX), by digest. Gitberth's image copies its /railpack as the CLI.</summary>
    public const string Frontend = "ghcr.io/railwayapp/railpack-frontend:v0.43.0@sha256:355ef61fae6f2551103cd8b10c710cb07167dd353d561f15c69b8d7ac1c676cc";

    /// <summary>The mise release Railpack 0.43.0 runs to look up the tools' versions (its core/mise/version.txt).</summary>
    public const string MiseVersion = "2026.10.14";

    /// <summary>moby/buildkit by the digest of its multi-arch index (amd64 and arm64), the image gitberth-buildkit runs.</summary>
    public const string BuildKitImage = "moby/buildkit:v0.34.0@sha256:b059f8d7226d0b326bc871af489a5dddf65e36af4d20d14251b072974d9ee87c";

    /// <summary>The container, its volume and the buildx builder.</summary>
    public const string BuildKitContainer = "gitberth-buildkit";

    /// <summary>mise in Gitberth's image, checked against its sha256; prepare finds it through a link, so it never downloads it.</summary>
    public const string MisePath = "/usr/local/lib/railpack/mise";

    /// <summary>The container port a new Railpack app gets, and is told as PORT.</summary>
    public const int DefaultPort = 8080;

    /// <summary>The gitberth.helper label of the throwaway containers railpack prepare runs in.</summary>
    public const string HelperKind = "railpack";

    /// <summary>gitberth-buildkit's gitberth.helper label, which keeps it out of Gitberth's lists of apps.</summary>
    public const string BuildKitHelperKind = "buildkit";

    /// <summary>
    /// The prefix of every mount cache an app's Railpack builds use (cache-key): each app and preview has its own. Railpack
    /// names a cache &lt;key&gt;-&lt;the name railpack.json gives it&gt;, so the ID comes first, right after gb-: no app's key
    /// followed by a name is another's (gb-3-shop-9-… is never gb-9-shop-3-…), whatever names a repo gives its caches. The
    /// slug keeps a reused row ID from meeting an old app's cache.
    /// </summary>
    public static string CacheKey(int appId, string slug) => $"gb-{appId}-{slug}";
}

/// <summary>
/// Why a plan failed. NothingFound: no provider matched; NoStartCommand: nothing starts the app; BadConfig: the repo's
/// railpack.json; Offline: the tools' versions couldn't be looked up; TimedOut: 5 minutes passed; NotInstalled: no Railpack
/// in this image; NoSandbox: the container prepare runs in couldn't start; Other: anything else Railpack said.
/// </summary>
public enum RailpackFailure { NothingFound, NoStartCommand, BadConfig, Offline, TimedOut, NotInstalled, NoSandbox, Other }

/// <param name="RepoDir">The clone (or New app's analysis folder).</param>
/// <param name="AppDir">The app's folder in it: "" for the root.</param>
/// <param name="Env">The build's values; secret ones reach planning only as placeholders.</param>
/// <param name="HasStartCommand">The app has a Command: no --error-missing-start.</param>
/// <param name="Previous">name@version pairs to keep (--previous).</param>
public sealed record RailpackPlanRequest(string RepoDir, string AppDir, IReadOnlyList<Docker.EnvValue> Env, bool HasStartCommand,
    IReadOnlyList<string> Previous);

/// <summary>A tool Railpack installs with mise, and the version it resolved.</summary>
/// <param name="Name">mise's name for it: node, python, erlang…</param>
/// <param name="Requested">The version asked for (by the repo, a RAILPACK_ variable or Railpack's default), when there was one.</param>
/// <param name="Resolved">The version Railpack picked.</param>
/// <param name="Source">Where the request came from, in Railpack's words, when it said.</param>
public sealed record RailpackPackage(string Name, string? Requested, string Resolved, string? Source);

/// <summary>One of Railpack's log entries from planning.</summary>
/// <param name="Level">Lower case: info, warn, deprecation, suggestion or error.</param>
/// <param name="Message">What Railpack said.</param>
/// <param name="DocsPath">The railpack.com page it points to, when it gave one.</param>
public sealed record RailpackLog(string Level, string Message, string? DocsPath);

/// <summary>What railpack prepare found (its info file), with what the plan adds.</summary>
/// <param name="Version">The Railpack release that planned it.</param>
/// <param name="Provider">The first detected provider, lower case (php, golang, … shell).</param>
/// <param name="Providers">Every detected provider, lower case.</param>
/// <param name="Packages">The tools and their resolved versions.</param>
/// <param name="Metadata">What the providers noted (phpFramework, rubyRails, nodePackageManager…).</param>
/// <param name="Logs">Railpack's log entries, in order.</param>
/// <param name="StartCommand">The plan's deploy.startCommand.</param>
/// <param name="Secrets">The plan's secrets[]: the names the build must be given.</param>
public sealed record RailpackInfo(string Version, string Provider, IReadOnlyList<string> Providers, IReadOnlyList<RailpackPackage> Packages,
    IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<RailpackLog> Logs, string? StartCommand, IReadOnlyList<string> Secrets);

/// <summary>
/// What planning gave. Ok: <see cref="Info"/> and <see cref="PlanJson"/> are set. Otherwise <see cref="Failure"/> and
/// <see cref="Error"/> say why, and <see cref="Info"/> is set when Railpack wrote its info file.
/// </summary>
/// <param name="Error">
/// What to tell the person: one of <see cref="RailpackText"/>'s errors, as a deploy of an app set to Railpack, New app's
/// switch and create_app show it. For NothingFound it's CantBuild(FoundNothing); a fallback deploy says NeitherCanBuild.
/// </param>
/// <param name="PlanJson">The plan as Railpack wrote it (before Gitberth's edits).</param>
/// <param name="Output">prepare's own output, line by line (the human-readable plan).</param>
public sealed record RailpackPlanResult(bool Ok, RailpackFailure? Failure, string? Error, RailpackInfo? Info, string? PlanJson, IReadOnlyList<string> Output);

public interface IRailpackPlanner
{
    /// <summary>
    /// Plans the build (railpack prepare) in a throwaway container of Gitberth's own image. <paramref name="onLine"/> gets
    /// prepare's own output as it comes. Railpack's failures come back as a result that isn't Ok; only cancelling throws.
    /// </summary>
    Task<RailpackPlanResult> PlanAsync(RailpackPlanRequest request, Action<string>? onLine, CancellationToken ct);

    /// <summary>The CLI's version ("0.43.0"), or null when this image has none.</summary>
    Task<string?> VersionAsync(CancellationToken ct);
}

/// <summary>gitberth-buildkit, Gitberth's own BuildKit for Railpack builds, as it is now.</summary>
/// <param name="Exists">The container is there (it's made on the first Railpack build).</param>
/// <param name="Running">It's running; it stops when no Railpack build has needed it for a while.</param>
/// <param name="Image">The BuildKit image it runs, when it exists.</param>
/// <param name="CacheBytes">The size of its volume, Railpack's build cache; null when it isn't known.</param>
public sealed record RailpackBuildKitStatus(bool Exists, bool Running, string? Image, long? CacheBytes);

public interface IRailpackBuildKit
{
    /// <summary>gitberth-buildkit as it is now, for Settings › System and Settings › Disk.</summary>
    Task<RailpackBuildKitStatus> StatusAsync(CancellationToken ct);
}

/// <summary>Until package A lands: no Railpack (every plan is NotInstalled).</summary>
public sealed class NoRailpack : IRailpackPlanner, IRailpackBuildKit
{
    public Task<RailpackPlanResult> PlanAsync(RailpackPlanRequest request, Action<string>? onLine, CancellationToken ct) =>
        ct.IsCancellationRequested
            ? Task.FromCanceled<RailpackPlanResult>(ct)
            : Task.FromResult(new RailpackPlanResult(false, RailpackFailure.NotInstalled, RailpackText.NotInstalled, null, null, []));

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<RailpackBuildKitStatus> StatusAsync(CancellationToken ct) =>
        Task.FromResult(new RailpackBuildKitStatus(Exists: false, Running: false, Image: null, CacheBytes: null));
}
