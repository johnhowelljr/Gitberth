using System.Globalization;
using Gitberth.Apps;

namespace Gitberth.Components.Shared;

/// <summary>How a state looks: the CSS modifier that gives its shape and colour, its word, and an icon when it's a shape of its own.</summary>
/// <param name="Modifier">gb-st--running, gb-st--healthy, gb-st--stopped, gb-st--error…</param>
/// <param name="Tone">good, warn, serious, critical, neutral, accent: for a waffle, a banner or a count.</param>
public sealed record StatusLook(string Modifier, string Word, string? Icon, string Tone)
{
    /// <summary>It's in trouble: the Apps list's error banner and Needs attention count it.</summary>
    public bool Trouble => Tone is "serious" or "critical";
}

/// <summary>
/// The status vocabulary (design-system.md section 8): a shape, a word, and colour as the third cue. Running is a small lit
/// dot, healthy adds a ring, stopping on purpose is a calm hollow ring, and an error is an orange triangle with the exit
/// code, the loudest thing in a row.
/// </summary>
public static class AppStatus
{
    public static StatusLook Of(AppState state, string? text = null) => state == AppState.Renamed
        // Its container runs under another name: the record needs a decision (the hero's banner), nothing is broken.
        ? new("gb-st--renamed", "Renamed", MudBlazor.Icons.Material.Outlined.DriveFileRenameOutline, "warn")
        : state switch
    {
        AppState.Running => new("gb-st--running", "Running", null, "good"),
        AppState.Healthy => new("gb-st--healthy", "Healthy", null, "good"),
        AppState.Starting => new("gb-st--starting", "Starting", null, "warn"),
        AppState.Restarting => new("gb-st--starting", "Restarting", null, "warn"),
        AppState.Unhealthy => new("gb-st--unhealthy", "Unhealthy", MudBlazor.Icons.Material.Outlined.HealthAndSafety, "serious"),
        AppState.Paused => new("gb-st--paused", "Paused", null, "warn"),
        AppState.Stopped => new("gb-st--stopped", "Stopped", null, "neutral"),
        AppState.Created => new("gb-st--stopped", "Not started", null, "neutral"),
        AppState.Crashed => new("gb-st--error", Word(text, "Exited with an error"), MudBlazor.Icons.Material.Outlined.WarningAmber, "serious"),
        AppState.Dead => new("gb-st--critical", "Dead", MudBlazor.Icons.Material.Outlined.ErrorOutline, "critical"),
        AppState.Deploying => new("gb-st--busy", "Deploying", null, "accent"),
        AppState.Changing => new("gb-st--busy", Word(text, "Changing"), null, "accent"),
        AppState.NeedsDecision => new("gb-st--error", "Needs a decision", MudBlazor.Icons.Material.Outlined.WarningAmber, "serious"),
        AppState.Missing => new("gb-st--critical", Word(text, "Missing"), MudBlazor.Icons.Material.Outlined.ErrorOutline, "critical"),
        AppState.NotCreated => new("gb-st--stopped", Word(text, "Not created"), null, "neutral"),
        _ => new("gb-st--stopped", Word(text, "Unknown"), null, "neutral"),
    };

    /// <summary>The waffle's square for a state: running, stopped, or an error.</summary>
    public static string WaffleClass(AppState state) => Of(state) switch
    {
        { Trouble: true } => "gb-w-err",
        { Tone: "good" or "warn" or "accent" } => "",
        _ => "gb-w-stop",
    };

    /// <summary>How long, as a row shows it: 12s, 48m, 28h, 2d.</summary>
    public static string Short(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{Math.Max(0, (int)span.TotalSeconds)}s",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m",
        { TotalHours: < 48 } => $"{(int)span.TotalHours}h",
        _ => $"{(int)span.TotalDays}d",
    };

    /// <summary>How long, as the hero says it: for 48 min, for 28 h, for 3 days.</summary>
    public static string Long(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"for {Math.Max(1, (int)span.TotalSeconds)} s",
        { TotalMinutes: < 60 } => $"for {(int)span.TotalMinutes} min",
        { TotalHours: < 48 } => $"for {(int)span.TotalHours} h",
        _ => $"for {((int)span.TotalDays).ToString(CultureInfo.InvariantCulture)} days",
    };

    private static string Word(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;
}
