using Gitberth.Health;

namespace Gitberth.Network;

/// <summary>
/// Follows port changes up once they've left their dialog (7.4, 7.5). A stopped container is recreated stopped and its
/// original kept until the new one's first start passes the checks a running one's change makes: that start is heard
/// from Docker's events and checked at once (<see cref="PortChanger.ContainerStartedAsync"/>), and every five minutes the
/// changes still waiting are looked at, in case a start went by unheard (<see cref="PortChanger.CheckWaitingAsync"/>).
/// Either container of a change that isn't settled starting next to the other asks the owner to choose, at once.
/// </summary>
public sealed class PortChangeWatcher(PortChanger changer, ContainerEventWatcher events, ILogger<PortChangeWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        events.Started += OnStarted;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Every, stoppingToken);
                try
                {
                    await changer.CheckWaitingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Looking at the port changes that wait for a start failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            events.Started -= OnStarted;
        }
    }

    // Off the event loop: the checks take as long as a change's own (15 s, or until it reports healthy).
    private void OnStarted(string container) => _ = Task.Run(async () =>
    {
        try
        {
            if (await changer.ContainerStartedAsync(container, _stopping) is { } result)
                logger.LogInformation("{Container} started after its port change: {Result}", container, result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Checking the start of {Container} after its port change failed", container);
        }
    }, CancellationToken.None);
}
