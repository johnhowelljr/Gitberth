namespace Gitberth.Uptime;

/// <summary>Where an app stands, as its uptime checks see it.</summary>
public enum UptimeStatus
{
    /// <summary>Not checked yet since Gitberth started (or since its checks were turned on).</summary>
    Unknown,
    Up,
    /// <summary>Two checks in a row failed: an incident is open.</summary>
    Down,
    /// <summary>Stopped on purpose: it isn't checked while it's stopped.</summary>
    Paused,
    /// <summary>Not checked at all: its checks are off, or it has no address to check (<see cref="AppUptime.Why"/>).</summary>
    NotChecked,
}

/// <summary>One check of an app: what it asked, and how it went.</summary>
/// <param name="Status">The HTTP status it answered with; null when there was no answer.</param>
/// <param name="Ms">How long the answer took, in milliseconds; null when there was none.</param>
/// <param name="Problem">Why it counts as down, in words people understand ("Server error 502", "Timed out"); null when it's up.</param>
/// <param name="Address">What was asked: https://web.example.com/ or http://192.168.1.10:3000/.</param>
public sealed record UptimeResult(int AppId, DateTime At, bool Up, int? Status, int? Ms, string? Problem, string Address);

/// <summary>A time an app was down: from the first of the failed checks that opened it to the check that found it up again.</summary>
/// <param name="Ended">Null while it's still down.</param>
/// <param name="Cause">What the first failed check found ("Server error 502").</param>
public sealed record UptimeIncident(long Id, int AppId, DateTime Started, DateTime? Ended, string Cause, string? Address)
{
    public TimeSpan Duration(DateTime now) => (Ended ?? now) - Started;
}

/// <summary>An hour of an app's checks, kept for 400 days: how many there were, how many found it up, and its response times.</summary>
public sealed record UptimeHour(int AppId, DateTime Hour, int Checks, int Ups, long MsSum, int MsCount)
{
    public int? AverageMs => MsCount > 0 ? (int)(MsSum / MsCount) : null;
}

/// <summary>What Gitberth knows of a host's TLS certificate, from its uptime checks.</summary>
/// <param name="WarnedDays">The last threshold told about (14 or 3 days left); null since it was renewed.</param>
public sealed record CertificateFact(string Host, DateTime NotAfter, DateTime CheckedAt, int? WarnedDays, DateTime? WarnedAt);

/// <summary>An app's uptime as its pages show it now.</summary>
/// <param name="Why">Why it isn't checked, for <see cref="UptimeStatus.NotChecked"/> and <see cref="UptimeStatus.Paused"/>.</param>
/// <param name="Address">What its checks ask; null when there's nothing to check.</param>
/// <param name="Open">The incident it's in, while it's down.</param>
/// <param name="CertificateExpires">When the certificate of its domain expires, when a check over HTTPS saw it.</param>
public sealed record AppUptime(int AppId, UptimeStatus Status, string? Address, UptimeResult? Last, UptimeIncident? Open, DateTime? CertificateExpires, string? Why = null)
{
    public static AppUptime Unknown(int appId) => new(appId, UptimeStatus.Unknown, null, null, null, null);
}
