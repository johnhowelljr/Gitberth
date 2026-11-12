using System.Collections.Concurrent;
using System.Threading.Channels;
using Gitberth.Services;

namespace Gitberth.Notifications;

/// <summary>
/// Sends notices to every destination in Settings › Notifications, in the background: whatever raised one (a deploy, a
/// container event) never waits for Discord or a mail server. A destination that's busy or down for a moment gets one more
/// try 30 seconds later; how each delivery went is kept in memory for Settings to show. Nothing is sent while there's no
/// destination, for a kind that's off, or about an app that's left out.
/// </summary>
public sealed class Notifier(NotificationSettings settings, NotificationSender sender, SettingsService store, ILogger<Notifier> logger) : BackgroundService
{
    internal TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one delivery may take, all tries counted.</summary>
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    // Notices waiting to go; at most this many, the oldest dropped first when a destination is down for long.
    private readonly Channel<Notice> _queue = Channel.CreateBounded<Notice>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    /// <summary>The notices waiting, for tests to read what was published.</summary>
    internal ChannelReader<Notice> Queued => _queue.Reader;

    private readonly ConcurrentDictionary<string, Delivery> _last = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sending = new(4, 4);
    private int _pending;

    /// <summary>Raised after each delivery, for open Settings pages.</summary>
    public event Action? Delivered;

    /// <summary>The last delivery to the destination, or null when nothing was sent there since Gitberth started.</summary>
    public Delivery? LastFor(string destinationId) => _last.TryGetValue(destinationId, out var d) ? d : null;

    /// <summary>Notices queued or being sent.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Queues the notice for every destination, unless Settings says it isn't wanted. True when it was queued.</summary>
    public bool Publish(Notice notice)
    {
        if (!Wanted(notice)) return false;
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(notice)) return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>Whether Settings wants this notice sent at all: a destination, its kind on, its app not left out, previews if they're on.</summary>
    public bool Wanted(Notice notice) =>
        settings.Destinations.Count > 0 && settings.IsOn(notice.Event)
        && (notice.Event == NotifyEvent.Test || !settings.IsMuted(notice.Container))
        && (!notice.Preview || settings.Previews);

    /// <summary>Sends a test to one destination now, without the retry. Null when it went, else why not.</summary>
    public async Task<string?> SendTestAsync(Destination destination, CancellationToken ct = default)
    {
        var settingsUrl = await LinkAsync("settings#notifications", ct);
        var notice = new Notice(NotifyEvent.Test, NoticeLevel.Info, $"Gitberth can reach {destination.Title}",
            "This is a test from Settings › Notifications. Gitberth sends here what you chose there: failed deploys, crashes and failing health checks, unless you turned them off.",
            settingsUrl, "", null, DateTime.UtcNow);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeliveryTimeout);
        try
        {
            await sender.SendAsync(destination, notice, timeout.Token);
            Record(destination, new Delivery(DateTime.UtcNow, true, notice.Title, null));
            return null;
        }
        catch (Exception ex) when (ex is NotifyException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var why = ex is NotifyException ? ex.Message : $"{destination.Title} didn't answer in time.";
            Record(destination, new Delivery(DateTime.UtcNow, false, notice.Title, why));
            return why;
        }
    }

    /// <summary>A page of Gitberth's, from the address its pages were last opened at; null when that isn't known.</summary>
    public async Task<string?> LinkAsync(string path, CancellationToken ct = default) =>
        Uri.TryCreate(await store.GetAsync(SettingKeys.UiAddress, ct), UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? new Uri(root, path).ToString()
            : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notice in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var destinations = settings.Destinations;
                var sends = destinations.Select(d => DeliverAsync(d, notice, stoppingToken)).ToList();
                // Each delivery goes on by itself: a slow one never holds the next notice up past the limit on sends.
                _ = Task.WhenAll(sends).ContinueWith(_ => Interlocked.Decrement(ref _pending), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One notice to one destination: a second try after a moment when the first failed for a passing reason.</summary>
    internal async Task DeliverAsync(Destination destination, Notice notice, CancellationToken stoppingToken)
    {
        await _sending.WaitAsync(stoppingToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(DeliveryTimeout + RetryAfter);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await sender.SendAsync(destination, notice, timeout.Token);
                    Record(destination, new Delivery(DateTime.UtcNow, true, notice.Title, null));
                    return;
                }
                catch (NotifyException ex) when (ex.Transient && attempt == 1)
                {
                    await Task.Delay(RetryAfter, timeout.Token);
                }
                catch (NotifyException ex)
                {
                    logger.LogWarning("Couldn't send \"{Title}\" to {Destination}: {Problem}", notice.Title, destination.Title, ex.Message);
                    Record(destination, new Delivery(DateTime.UtcNow, false, notice.Title, ex.Message));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            Record(destination, new Delivery(DateTime.UtcNow, false, notice.Title, $"{destination.Title} didn't answer in time."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sending \"{Title}\" to {Destination} failed", notice.Title, destination.Title);
            Record(destination, new Delivery(DateTime.UtcNow, false, notice.Title, ex.Message));
        }
        finally
        {
            _sending.Release();
        }
    }

    private void Record(Destination destination, Delivery delivery)
    {
        _last[destination.Id] = delivery;
        Delivered?.Invoke();
    }
}
