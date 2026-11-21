using System.Globalization;

namespace Gitberth.Keeper;

/// <summary>How a try to bring an app back went about it, for its words.</summary>
public enum KeepHow
{
    /// <summary>Its existing container started (docker start, or a Compose app's services).</summary>
    Started,
    /// <summary>A Git app's container a deploy cut short had moved aside, put back and started.</summary>
    PutBack,
    /// <summary>A deploy without a build made its container again from its last image (a Compose app: its current release).</summary>
    MadeFromImage,
    /// <summary>Made again from its saved settings (an app from a registry, a local image or by hand).</summary>
    MadeFromSettings,
    /// <summary>Built again from the commit it ran: its image was gone too.</summary>
    Built,
}

/// <summary>Everything the keep-running check says: Activity, the audit log, notices, banners, Settings. Pure, so each is tested.</summary>
public static class KeepText
{
    /// <summary>"Try 2 of 4."</summary>
    public static string Try(int n) => $"Try {n} of {KeepRules.Tries}.";

    /// <summary>A try's Activity entry: what Gitberth did, and why.</summary>
    public static string Tried(KeepHow how, KeepReason why, int n, string? image = null, int? deployment = null, string? commit = null,
        IReadOnlyList<string>? services = null) => how switch
    {
        KeepHow.Started when services is { Count: > 0 } => $"Gitberth started {Join(services)} again: {why.Told}. {Try(n)}",
        KeepHow.Started => $"Gitberth started it again: {why.Told}. {Try(n)}",
        KeepHow.PutBack => $"Gitberth put its container back and started it: a deploy that was cut short had moved it aside. {Try(n)}",
        KeepHow.MadeFromImage when image is null => $"Gitberth brought its current release up again{Deploy(deployment)}: {why.Words}. {Try(n)}",
        KeepHow.MadeFromImage => $"Gitberth made its container again from {image}{Deploy(deployment)}: {why.Words}. {Try(n)}",
        KeepHow.MadeFromSettings => $"Gitberth made its container again from its saved settings: {why.Words}. {Try(n)}",
        _ => $"Gitberth built it again{(commit is { Length: >= 7 } sha ? $" from {sha[..7]}" : "")}{Deploy(deployment)}: its container and its image were gone.",
    };

    /// <summary>A try that didn't start anything.</summary>
    public static string Failed(string error, int n) => $"Gitberth couldn't start it: {Clause(error)}. {Try(n)}";

    /// <summary>A try after Gitberth gave up, every 6 hours.</summary>
    public static string QuietTry(KeepHow how, string? error, int? deployment = null) =>
        "Gitberth tried again, as it does every 6 hours since it stopped bringing it back, and " + (error is not null
            ? $"couldn't start it: {Clause(error)}."
            : how switch
            {
                KeepHow.MadeFromImage or KeepHow.MadeFromSettings => $"made its container again{Deploy(deployment)}.",
                KeepHow.Built => $"built it again{Deploy(deployment)}.",
                _ => "started it.",
            });

    /// <summary>Its Activity entry when Gitberth gives up on it.</summary>
    public static string GaveUp(KeepReason why, int tries, TimeSpan over) =>
        $"Gitberth stopped bringing it back. {Story(why, tries, over)} It tries again every 6 hours; " + (Built(why)
            ? "deploy it once it's fixed, and the deploy's log says why it didn't work."
            : "start it once it's fixed, and its Logs tab says why it stops.");

    /// <summary>A quiet try that worked: the app has stayed up.</summary>
    public const string CameBack = "It runs again: Gitberth's try every 6 hours worked, and it has stayed up for 10 minutes.";

    /// <summary>A stop Gitberth saw happen, not a crash, or a cancel of its own deploy: said once.</summary>
    public static string Left(KeepReason why) => why.Cause switch
    {
        KeepCause.Ended => $"{Capital(why.Words)}, so Gitberth leaves it stopped. Start it to bring it back.",
        KeepCause.Cancelled => $"{Capital(why.Words)}, so Gitberth leaves it as it is. Start or deploy it to bring it back.",
        _ => $"{Capital(why.Words)}, not by a crash, so Gitberth leaves it stopped. Start it to bring it back.",
    };

    public const string ExistingGone =
        "Its container is gone. Gitberth doesn't make an adopted container again by itself: start it to make it again from its saved settings, or forget it.";

    public const string Dead = "Docker can't start it: it's dead, so Gitberth leaves it as it is.";

    // ---- The audit log ----

    /// <summary>A try's entry (app.recovered): what it did, as a sentence.</summary>
    public static string Recovered(string app, KeepHow how, KeepReason why, string? image = null, int? deployment = null, string? commit = null,
        IReadOnlyList<string>? services = null) => how switch
    {
        KeepHow.Started when services is { Count: > 0 } => $"Brought {app} back: {why.Told}. Started {Join(services)} again.",
        KeepHow.Started or KeepHow.PutBack => $"Brought {app} back: {why.Told}. It runs again.",
        KeepHow.MadeFromImage when image is null => $"Bringing {app} back: {why.Words}, so{DeployAs(deployment)} brings its current release up again.",
        KeepHow.MadeFromImage => $"Bringing {app} back: {why.Words}, so{DeployAs(deployment)} makes it again from {image}.",
        KeepHow.MadeFromSettings => $"Bringing {app} back: {why.Words}, so Gitberth makes it again from its saved settings.",
        _ => $"Bringing {app} back: its container and its image were gone, so{DeployAs(deployment)} builds it again{(commit is { Length: >= 7 } sha ? $" from {sha[..7]}" : "")}.",
    };

    /// <summary>How, for the entry's changes: "started its container", "made its container again (deploy #12)".</summary>
    public static string HowWords(KeepHow how, int? deployment = null, IReadOnlyList<string>? services = null) => how switch
    {
        KeepHow.Started when services is { Count: > 0 } => $"started {Join(services)}",
        KeepHow.Started => "started its container",
        KeepHow.PutBack => "put its container back",
        KeepHow.MadeFromImage => $"made its container again{Deploy(deployment)}",
        KeepHow.MadeFromSettings => "made its container again from its saved settings",
        _ => $"built it again{Deploy(deployment)}",
    };

    public static string CouldntRecover(string app, string error) => $"Couldn't bring {app} back: {Clause(error)}.";

    /// <summary>The entry when Gitberth gives up (app.recovery.stopped).</summary>
    public static string StoppedTrying(string app, KeepReason why, int tries, TimeSpan over) =>
        $"Stopped bringing {app} back. {Story(why, tries, over)} Gitberth tries again every 6 hours.";

    // ---- Notices ----

    /// <summary>The first try of an incident, told once the app runs: its title and text.</summary>
    public static (string Title, string Text) BackNotice(string app, KeepHow how, KeepReason why, string? image = null, int? deployment = null,
        string? commit = null) => how switch
    {
        KeepHow.Started when why.Cause == KeepCause.HostRestarted =>
            ($"{app} is running again", "It stopped when Docker or the host restarted, and Docker didn't start it again, so Gitberth did."),
        KeepHow.Started or KeepHow.PutBack =>
            ($"{app} is running again", $"{Capital(why.Told)}, so Gitberth started it. Its log on Gitberth has the details."),
        KeepHow.MadeFromImage when image is null =>
            ($"{app}'s containers are back", $"One was gone, so Gitberth brought its current release up again{Deploy(deployment)}."),
        KeepHow.MadeFromImage => ($"{app}'s container is back", $"It was gone, so Gitberth made it again from its last image{Deploy(deployment)}."),
        KeepHow.MadeFromSettings => ($"{app}'s container is back", "It was gone, so Gitberth made it again from its saved settings."),
        _ => ($"{app}'s container is back",
            $"It and its image were gone, so Gitberth built it again{(commit is { Length: >= 7 } sha ? $" from {sha[..7]}" : "")}{Deploy(deployment)}."),
    };

    /// <summary>Several apps brought back after Docker or the host restarted: one notice for them all.</summary>
    public static (string Title, string Text) BatchNotice(IReadOnlyList<string> apps) =>
        ($"Gitberth started {apps.Count} apps again",
            $"They stopped when Docker or the host restarted, and Docker didn't start them again: {Join(apps)}.");

    public static (string Title, string Text) GaveUpNotice(string app, KeepReason why, int tries, TimeSpan over) =>
        ($"Gitberth stopped bringing {app} back",
            $"{Story(why, tries, over)} Gitberth tries again every 6 hours; it stays down until then, or until you start or deploy it. "
            + (Built(why) ? "The deploy's log on Gitberth has the details." : "Its log on Gitberth has the details."));

    public static (string Title, string Text) CameBackNotice(string app) =>
        ($"{app} is running again", "Gitberth had stopped bringing it back after it kept stopping; its try 6 hours later worked, and it has stayed up for 10 minutes.");

    // ---- Banners and Settings ----

    /// <summary>The app page's banner while a try is coming: when, and which.</summary>
    public static string SoonBanner(KeepReason why, int nextTry, DateTime? at, DateTime now) =>
        $"{Capital(why.Told)}. Gitberth starts it {When(at, now)} (try {nextTry} of {KeepRules.Tries}).";

    /// <summary>The app page's banner, and the dashboard's, once Gitberth gave up.</summary>
    public static string GaveUpBanner(KeepReason why, int tries, TimeSpan over, DateTime? next, DateTime now) =>
        $"{Story(why, tries, over)} {(Built(why) ? "The deploy's log says why. Deploy it" : "Its Logs tab says why. Start it")} once it's fixed; until then Gitberth tries again every 6 hours{(next is { } n ? $", next {When(n, now)}" : "")}.";

    /// <summary>What agents hear of an app Gitberth gave up on (get_health's problems).</summary>
    public static string GaveUpProblem(string app, string key, KeepReason why, int tries, TimeSpan over) =>
        $"{app} ({key}): Gitberth stopped bringing it back. {Story(why, tries, over)} It tries again every 6 hours.";

    /// <summary>Settings › Keep apps running's line for an app Gitberth gave up on.</summary>
    public static string GaveUpLine(KeepReason why, int tries, TimeSpan over, DateTime at, DateTime now) =>
        $"{Story(why, tries, over)} Gitberth stopped trying {Ago(at, now)}, and tries again every 6 hours.";

    /// <summary>
    /// Why Gitberth gave up, as a sentence: "It stopped 4 times in 21 minutes; the last time it crashed (exit code 3)." A
    /// single try (a build counts as every try) says only what happened.
    /// </summary>
    public static string Story(KeepReason why, int tries, TimeSpan over) => tries <= 1 || Built(why)
        ? $"{Capital(why.Words)}."
        : $"It stopped {Times(tries)} in {Span(over)}; the last time {why.Words}.";

    private static bool Built(KeepReason why) => why.Cause == KeepCause.BuildFailed;

    /// <summary>The line under the switch: what was brought back lately.</summary>
    public static string Recent(int times, IReadOnlyList<string> apps) => times == 0
        ? "Nothing brought back in the last 24 hours."
        : $"Brought back {Times(times)} in the last 24 hours: {Join(apps)}.";

    // ---- Words ----

    /// <summary>"once", "twice", "4 times".</summary>
    public static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };

    /// <summary>"under a minute", "21 minutes", "3 hours".</summary>
    public static string Span(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? "under a minute"
        : t < TimeSpan.FromMinutes(2) ? "a minute"
        : t < TimeSpan.FromHours(2) ? $"{(int)t.TotalMinutes} minutes"
        : $"{(int)t.TotalHours} hours";

    /// <summary>"within a minute", "in about 5 minutes", "at 14:05".</summary>
    public static string When(DateTime? at, DateTime now)
    {
        if (at is not { } t || t - now <= TimeSpan.FromMinutes(1)) return "within a minute";
        var left = t - now;
        return left < TimeSpan.FromHours(1)
            ? $"in about {(int)Math.Ceiling(left.TotalMinutes)} minutes"
            : $"at {t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static string Ago(DateTime at, DateTime now)
    {
        var t = now - at;
        return t < TimeSpan.FromMinutes(1) ? "just now"
            : t < TimeSpan.FromHours(1) ? $"{(int)t.TotalMinutes} min ago"
            : t < TimeSpan.FromDays(1) ? $"{(int)t.TotalHours} h ago"
            : $"{(int)t.TotalDays} days ago";
    }

    /// <summary>Docker's or a command's words, in the middle of a sentence: one line, without its full stop.</summary>
    public static string Clause(string? text)
    {
        var line = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).TrimEnd('.');
        if (line.Length > 300) line = line[..299] + "…";
        return line.Length > 1 && char.IsUpper(line[0]) && !char.IsUpper(line[1]) ? char.ToLowerInvariant(line[0]) + line[1..] : line;
    }

    public static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Deploy(int? deployment) => deployment is int id ? $" (deploy #{id.ToString(CultureInfo.InvariantCulture)})" : "";

    private static string DeployAs(int? deployment) => deployment is int id ? $" deploy #{id.ToString(CultureInfo.InvariantCulture)}" : " a deploy";

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
