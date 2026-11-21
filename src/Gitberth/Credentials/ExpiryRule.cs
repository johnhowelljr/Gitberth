namespace Gitberth.Credentials;

/// <summary>
/// When a token about to expire is told about (like UptimeMonitor.CertificateCheck for certificates): at each of its
/// thresholds, the lowest one crossed and only once per token, never two warnings within a day, and the expiry itself the
/// moment it passes. Pure: the watch keeps what was told (<see cref="CredentialFact"/>) and asks this what to tell now.
/// </summary>
internal static class ExpiryRule
{
    /// <summary>
    /// The days before a GitHub, GitLab or Cloudflare token expires at which it's told about: 3 weeks leaves time to make a
    /// new token even with a holiday in between (GitHub and GitLab mail at a week themselves), a week and a day catch
    /// someone who let the first go by.
    /// </summary>
    public static readonly IReadOnlyList<int> External = [21, 7, 1];

    /// <summary>Gitberth's own API tokens that are in use: a week and a day (Gitberth made them; nobody waits on a service).</summary>
    public static readonly IReadOnlyList<int> ApiToken = [7, 1];

    /// <summary>The expiry itself, as a threshold: no days left.</summary>
    public const int Expired = 0;

    /// <summary>Two warnings about one token never come within this of each other; the expiry isn't held back by it.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(1);

    /// <summary>
    /// The threshold the expiry is at now: <see cref="Expired"/> once it has passed, else the smallest of
    /// <paramref name="thresholds"/> it's within; null while it's further off than all of them.
    /// </summary>
    public static int? At(DateTimeOffset expires, DateTimeOffset now, IReadOnlyList<int> thresholds)
    {
        var left = expires - now;
        if (left <= TimeSpan.Zero) return Expired;
        int? at = null;
        foreach (var days in thresholds)
            if (left <= TimeSpan.FromDays(days) && (at is null || days < at)) at = days;
        return at;
    }

    /// <summary>
    /// What to tell now (the threshold, or null for nothing) and what's then told: a threshold is told when it's lower than
    /// the last one told (or none was), so a token found 5 days from its expiry gets the week's warning only; a warning
    /// waits until a day after the last one; the expiry is told the moment it passes.
    /// </summary>
    public static (int? Tell, int? WarnedDays, DateTimeOffset? WarnedAt) Next(DateTimeOffset? expires, DateTimeOffset now, IReadOnlyList<int> thresholds,
        int? warnedDays, DateTimeOffset? warnedAt)
    {
        if (expires is not { } at || At(at, now, thresholds) is not { } threshold) return (null, warnedDays, warnedAt);
        if (warnedDays is { } told && threshold >= told) return (null, warnedDays, warnedAt);
        if (threshold != Expired && warnedAt is { } last && now - last < Quiet) return (null, warnedDays, warnedAt);
        return (threshold, threshold, now);
    }

    /// <summary>
    /// A token someone just saved: the thresholds it's past already count as told (its date is on the page that saved it,
    /// so a notice now would only repeat it), and the next one is told when it comes.
    /// </summary>
    public static int? Saved(DateTimeOffset? expires, DateTimeOffset now, IReadOnlyList<int> thresholds) =>
        expires is { } at ? At(at, now, thresholds) : null;

    /// <summary>The next moment after now at which the expiry crosses one of its thresholds, or passes; null when none is ahead.</summary>
    public static DateTimeOffset? NextCrossing(DateTimeOffset? expires, DateTimeOffset now, IReadOnlyList<int> thresholds)
    {
        if (expires is not { } at) return null;
        DateTimeOffset? next = null;
        foreach (var instant in thresholds.Select(days => at - TimeSpan.FromDays(days)).Append(at))
            if (instant > now && (next is null || instant < next)) next = instant;
        return next;
    }

    /// <summary>Whole days left, rounded up (5 days and an hour is 6): 0 once it has passed.</summary>
    public static int DaysLeft(DateTimeOffset expires, DateTimeOffset now) =>
        expires <= now ? 0 : (int)Math.Ceiling((expires - now).TotalDays);
}
