using System.Globalization;
using Gitberth.Apps;
using Gitberth.Services;

namespace Gitberth.Images;

/// <summary>
/// When automatic updates may run, in Gitberth's own local time (its time zone, set with TZ; UTC when it's unset, as in
/// the compose file), not the host's: 03:00 to 05:00 unless the owner chose otherwise.
/// </summary>
/// <param name="From">The window opens at this time.</param>
/// <param name="To">It closes at this time; before From means it runs past midnight.</param>
public sealed record AutoWindow(TimeOnly From, TimeOnly To)
{
    public static readonly AutoWindow Default = new(new TimeOnly(3, 0), new TimeOnly(5, 0));

    public bool Contains(TimeOnly time) => From <= To ? time >= From && time < To : time >= From || time < To;

    /// <summary>"03:00-05:00" as a window; null for anything else.</summary>
    public static AutoWindow? Parse(string? text)
    {
        var parts = text?.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts is { Length: 2 }
               && TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
               && TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
               && from != to
            ? new AutoWindow(from, to)
            : null;
    }

    public override string ToString() => $"{From:HH\\:mm}-{To:HH\\:mm}";
}

/// <summary>
/// Checks followed image references for a newer digest, every 6 hours by default (updates.checkHours), looking every 15
/// minutes from 2 minutes after Gitberth starts (<see cref="UpdateService.CheckDueAsync"/>): one registry request per
/// distinct reference, however many apps follow it, and only for what a registry is known to have (a check found it, or
/// Gitberth pulled it), what the owner checked by hand, or what an app names a sign-in for. A reference no registry is
/// known to have isn't asked about until the owner presses Check for update; nor are Gitberth's own builds and the samples,
/// nor what only apps set to When I ask follow. What a check found no registry has is left alone (asked again after a week
/// only when an app names a sign-in for it), and a refused sign-in waits 24 hours. There's no other cap: every reference
/// that's due is asked on the same look. So it stays far below Docker Hub's anonymous limit. Inside the automatic-update
/// window (03:00 to 05:00 by default, updates.autoWindow, in Gitberth's own time zone: its TZ, UTC when unset) it updates
/// the apps whose policy is Auto, never a database, Gitberth itself or what Compose, Swarm or Kubernetes look after. It
/// changes nothing in Docker otherwise.
/// </summary>
public sealed class UpdateWatcher(UpdateService updates, SettingsService settings, GitberthOptions options, ILogger<UpdateWatcher> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>Hours between checks of a reference (1 to 168); 6 when unset.</summary>
    public const string CheckHoursKey = "updates.checkHours";

    /// <summary>The automatic-update window, "03:00-05:00" (Gitberth's local time); the default when unset.</summary>
    public const string AutoWindowKey = "updates.autoWindow";

    /// <summary>
    /// The update policy a new app from an image gets (AppCreator): Off, Notify or Auto; Notify when unset. A database gets
    /// Notify for Auto, and a digest-pinned image Off.
    /// </summary>
    public const string DefaultPolicyKey = "updates.defaultPolicy";

    /// <summary>The policy a new app gets, as saved: Tell me when it's unset or can't be read.</summary>
    public static async Task<UpdatePolicy> DefaultPolicyAsync(SettingsService settings, CancellationToken ct = default) =>
        Enum.TryParse<UpdatePolicy>(await settings.GetAsync(DefaultPolicyKey, ct), out var policy) && Enum.IsDefined(policy) ? policy : UpdatePolicy.Notify;

    /// <summary>The time zone the window is in, as Settings names it: Gitberth's own (TZ), "UTC" when it's unset.</summary>
    public static string ZoneName(TimeProvider? time = null)
    {
        var id = (time ?? TimeProvider.System).LocalTimeZone.Id;
        return id is "Etc/UTC" or "Etc/UCT" or "UCT" or "Etc/Universal" or "Universal" or "Etc/Zulu" or "Zulu" || id == TimeZoneInfo.Utc.Id ? "UTC" : id;
    }

    public const int DefaultCheckHours = 6;

    /// <summary>How long after Gitberth starts the first check waits, so a start isn't slowed by registries.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often it looks whether a check is due or the window is open; each reference is still asked only every few hours.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Checking for image updates failed; trying again in {Minutes} minutes", Tick.TotalMinutes);
                }
                await Task.Delay(Tick, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One look: the checks that are due, then the automatic updates when the window is open.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var hours = Math.Clamp(await settings.GetIntAsync(CheckHoursKey, DefaultCheckHours, ct), 1, 168);
        var checkedNow = await updates.CheckDueAsync(TimeSpan.FromHours(hours), ct);
        if (checkedNow.Count > 0) logger.LogInformation("Checked {Count} image references for updates", checkedNow.Count);

        var window = AutoWindow.Parse(await settings.GetAsync(AutoWindowKey, ct)) ?? AutoWindow.Default;
        if (!window.Contains(TimeOnly.FromDateTime(_time.GetLocalNow().DateTime))) return;
        var updated = await updates.AutoUpdateAsync(options.OwnContainer, ct);
        if (updated.Count > 0) logger.LogInformation("Updated {Containers} automatically", string.Join(", ", updated));
    }
}
