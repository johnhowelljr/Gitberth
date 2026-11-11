namespace Gitberth.Domains;

/// <summary>
/// Finishes what a sync couldn't (Cloudflare couldn't be reached, say) without waiting for something else to sync: every
/// 15 minutes, while domains are on and the last sync didn't get as far as the tunnel (or none has since Gitberth started,
/// as a change it couldn't finish before a restart isn't known), a removal is pending (a route or a DNS record), or
/// Cloudflare Access isn't settled (a protected domain may still be served without it, or be out of the tunnel because
/// Access failed for a moment, or one no longer protected, or gone, may still have the Access application Gitberth made),
/// it syncs as any change does (<see cref="DomainService.RetryRemovalsAsync"/>). Otherwise (nothing pending, or only a
/// lasting problem with Access), or with domains off, nothing is asked of Cloudflare; once Check again in Settings ›
/// Domains › Protection finds Access working while an application Gitberth made is still to be removed, that's tried
/// once more.
/// </summary>
public sealed class RemovalRetry(DomainService domains, ILogger<RemovalRetry> logger) : BackgroundService
{
    /// <summary>How often pending removals are tried again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    /// <summary>The wait between tries; shorter in tests.</summary>
    internal TimeSpan Interval { get; init; } = Every;

    /// <summary>Raised after each try, with what the sync said (null when there was nothing to try). For tests.</summary>
    internal event Action<SyncReport?>? Tried;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SyncReport? report = null;
                try
                {
                    report = await domains.RetryRemovalsAsync(stoppingToken);
                    foreach (var problem in report?.Notes.Where(n => n.Problem) ?? [])
                        logger.LogInformation("A domain change is still waiting: {Problem}", problem.Text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Trying the pending domain changes again failed");
                }
                Tried?.Invoke(report);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
