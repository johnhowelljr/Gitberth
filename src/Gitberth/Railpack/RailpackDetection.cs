using Gitberth.Detection;

namespace Gitberth.Railpack;

/// <summary>
/// Railpack's plan as New app and create_app show it: a detection result of its own (the card "How it will run", its ticks
/// and warnings, the port), and, when Railpack couldn't plan it either, its reason on the Unknown one. Pure.
/// </summary>
public static class RailpackDetection
{
    /// <summary>
    /// The card for a plan that worked. Its ticks: the marker files found (and railpack.json), the Railpack release that
    /// plans it, one per tool, the start command and PORT; Railpack's warn and deprecation logs are its warnings. The
    /// variables to suggest are left to the caller: the analyzer's (detection.EnvSuggestions) know the app's code.
    /// </summary>
    /// <param name="appDir">The app's folder in the repo, "" for the root: the build context.</param>
    /// <param name="folder">That folder on disk, where the marker files are looked for.</param>
    /// <param name="port">
    /// The container port to keep: Gitberth's detected one, when Build with Railpack is chosen. Null gives
    /// <see cref="RailpackRelease.DefaultPort"/>.
    /// </param>
    /// <param name="fallback">
    /// Railpack plans it because Gitberth didn't know it (Detect automatically): the result stays
    /// <see cref="DetectionResult.Unrecognized"/>, which tells the card it's a fallback.
    /// </param>
    public static DetectionResult ToDetection(RailpackPlanResult plan, string appDir, string folder, int? port, bool fallback)
    {
        if (!plan.Ok || plan.Info is not { } info)
            throw new ArgumentException("Only a plan that worked makes a card; CouldntEither takes the others.", nameof(plan));
        var webPort = port ?? RailpackRelease.DefaultPort;
        var r = new DetectionResult
        {
            Kind = ProjectKind.Railpack,
            Title = RailpackProviders.Summary(info),
            ContainerPort = webPort,
            BuildContext = appDir,
            Unrecognized = fallback,
            RailpackPlan = plan,
        };
        r.Evidence.AddRange(RailpackProviders.Markers(info.Provider, folder));
        if (File.Exists(Path.Combine(folder, RailpackProviders.ConfigFile))) r.Evidence.Add(RailpackProviders.ConfigFile);
        r.Evidence.Add(RailpackText.PlansTheBuild);
        r.Evidence.AddRange(info.Packages.Where(p => p.Name.Trim().Length > 0).Select(RailpackText.PackageTick));
        if (info.StartCommand is { } start && start.Trim().Length > 0) r.Evidence.Add(RailpackText.StartsWith(start));
        r.Evidence.Add(RailpackText.PortTick(webPort));
        r.Warnings.AddRange(info.Logs.Where(l => IsLevel(l, "warn") || IsLevel(l, "deprecation"))
            .Select(l => l.Message.Trim()).Where(m => m.Length > 0));
        return r;
    }

    /// <summary>
    /// Adds the banner "Railpack couldn't work out how to build it either: …" to the Unknown result, with Railpack's reason,
    /// and gives it back. A plan that worked adds nothing.
    /// </summary>
    public static DetectionResult CouldntEither(DetectionResult unknown, RailpackPlanResult plan)
    {
        if (!plan.Ok) unknown.Warnings.Add(RailpackText.CouldntEither(Why(plan)));
        return unknown;
    }

    // Railpack's reason: the files it looks for when it found none, its own words when it said something else, and the
    // failure's own sentence when it never got to say (not installed, offline, too slow, no container to plan in).
    private static string Why(RailpackPlanResult plan) => plan.Failure switch
    {
        RailpackFailure.NothingFound => RailpackText.FoundNothing,
        RailpackFailure.Other => Said(plan) ?? plan.Error ?? RailpackText.FoundNothing,
        _ => plan.Error ?? RailpackText.FoundNothing,
    };

    // What Railpack said: its first error, else the last line of its output.
    private static string? Said(RailpackPlanResult plan) =>
        plan.Info?.Logs.FirstOrDefault(l => IsLevel(l, "error") && l.Message.Trim().Length > 0)?.Message
        ?? plan.Output.LastOrDefault(l => l.Trim().Length > 0);

    private static bool IsLevel(RailpackLog log, string level) => log.Level.Trim().Equals(level, StringComparison.OrdinalIgnoreCase);
}
