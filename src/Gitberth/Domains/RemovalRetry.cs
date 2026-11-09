namespace Gitberth.Domains;

/// <summary>
/// Finishes the domain removals that failed (Cloudflare couldn't be reached, say) without waiting for something else to
/// sync: every 15 minutes, while domains are on and a removal is pending, it syncs as any change does
/// (<see cref="DomainService.RetryRemovalsAsync"/>). With nothing pending, or domains off, nothing is asked of Cloudflare.
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
                        logger.LogInformation("Removing a domain is still waiting: {Problem}", problem.Text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Trying the pending domain removals again failed");
                }
                Tried?.Invoke(report);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
