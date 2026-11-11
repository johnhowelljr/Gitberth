using System.Collections.Concurrent;
using System.Threading.Channels;
using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>
/// One worker per app runs deploys in order. Each app has a single pending slot, so commits that land
/// while a build is running collapse into one follow-up deploy of the newest request.
/// </summary>
public sealed class DeployQueue(
    DeployPipeline pipeline,
    LiveLogHub hub,
    IDbContextFactory<GitberthDb> dbFactory,
    IHostApplicationLifetime lifetime,
    ILogger<DeployQueue> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Channel<DeployRequest>> _channels = [];
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public void Enqueue(DeployRequest request)
    {
        // Open the live log now so a page opened right after queueing sees every line.
        hub.Open(request.DeploymentId);
        Channel<DeployRequest> channel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(request.AppId, out channel!))
            {
                channel = Channel.CreateBounded<DeployRequest>(
                    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                    dropped => _ = MarkSupersededAsync(dropped));
                _channels[request.AppId] = channel;
                var reader = channel.Reader;
                var appId = request.AppId;
                _ = Task.Run(() => ConsumeAsync(appId, reader));
            }
        }
        channel.Writer.TryWrite(request);
    }

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public bool Cancel(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return false;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    private async Task ConsumeAsync(int appId, ChannelReader<DeployRequest> reader)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            while (await reader.WaitToReadAsync(stopping))
            {
                while (reader.TryRead(out var request))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    _running[appId] = cts;
                    try
                    {
                        await pipeline.RunAsync(request, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Deploy {DeploymentId} for app {AppId} crashed", request.DeploymentId, appId);
                    }
                    finally
                    {
                        _running.TryRemove(appId, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MarkSupersededAsync(DeployRequest dropped)
    {
        hub.Close(dropped.DeploymentId);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var dep = await db.Deployments.FirstOrDefaultAsync(d => d.Id == dropped.DeploymentId);
            if (dep is null || dep.Status != DeployStatus.Queued) return;
            dep.Status = DeployStatus.Cancelled;
            dep.Error = "Superseded by a newer deploy";
            dep.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            // A promote that never ran lets production follow its branch again, unless a newer promote took its place.
            if (dep.Trigger == DeployTrigger.Promote) await RepoWatcher.ForgetUnheldPromotionAsync(db, dep.AppId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't mark deploy {DeploymentId} as superseded", dropped.DeploymentId);
        }
    }
}
