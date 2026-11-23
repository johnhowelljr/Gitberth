using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace Gitberth.Images;

/// <summary>
/// What Gitberth remembers about the hosts it asks for images and tags (Docker Hub's and registries'), so it asks no more
/// than it should: a host that couldn't be reached is left alone for a minute, one that asked Gitberth to wait (a 429, or
/// few requests left) until then, at most four requests go to one host at once, and at most twelve registry listings run
/// at once whatever their hosts. Per process, in memory.
/// </summary>
internal sealed class HostGates(TimeProvider time)
{
    /// <summary>How long a host that couldn't be reached is answered for without asking it again.</summary>
    public static readonly TimeSpan DownFor = TimeSpan.FromSeconds(60);

    /// <summary>At most this many requests to one host at once.</summary>
    public const int PerHost = 4;

    /// <summary>
    /// Hosts kept in mind before the ones that need nothing kept are forgotten (no longer down or held, no request under
    /// way): registries named by hand, by the owner or an agent's list_image_tags, come and go.
    /// </summary>
    internal const int MaxHosts = 256;

    /// <summary>
    /// At most this many registry listings at once, whatever their hosts: lists asked of many registries at once (an agent's
    /// list_image_tags) wait their turn, so the answers being read stay few. Docker Hub's two hosts have their own four
    /// places each and never wait for these.
    /// </summary>
    public const int Listings = 12;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Places> _places = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _placesGate = new();
    private readonly SemaphoreSlim _listings = new(Listings, Listings);

    // A host's four places, and how many requests hold or wait for one: guarded by _placesGate, so a host is forgotten only
    // when nobody holds or waits for its places, and never between a request finding them and taking one.
    private sealed class Places
    {
        public SemaphoreSlim Slots { get; } = new(PerHost, PerHost);

        public int Users;
    }

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// The most a host is left alone or held: the end-to-end rig's shorter waits (test timings' hubWait), so one scenario's
    /// unreachable or rate-limited Docker Hub doesn't hold up the next; null as installed.
    /// </summary>
    public TimeSpan? Cap { get; init; }

    /// <summary>The host couldn't be reached less than a minute ago.</summary>
    public bool IsDown(string host) => _down.TryGetValue(host, out var until) && until > Now;

    /// <summary>When a host that asked Gitberth to wait may be asked again; null when it may be now.</summary>
    public DateTimeOffset? HeldUntil(string host) => _held.TryGetValue(host, out var until) && until > Now ? until : null;

    public void Down(string host)
    {
        Forget();
        _down[host] = Now + (Cap is { } cap && cap < DownFor ? cap : DownFor);
    }

    public void Up(string host) => _down.TryRemove(host, out _);

    /// <summary>Holds the host until then (a later hold it has stands); returns when it may be asked again.</summary>
    public DateTimeOffset Hold(string host, DateTimeOffset until)
    {
        Forget();
        if (Cap is { } cap && until > Now + cap) until = Now + cap;
        return _held.AddOrUpdate(host, until, (_, known) => until > known ? until : known);
    }

    /// <summary>The hosts kept in mind now: down, held, or with places (for the tests).</summary>
    internal int Known
    {
        get
        {
            int places;
            lock (_placesGate) places = _places.Count;
            return _down.Count + _held.Count + places;
        }
    }

    // Past MaxHosts, forgets what needs no keeping: waits that are over, and places nobody holds or waits for.
    private void Forget()
    {
        if (Known <= MaxHosts) return;
        var now = Now;
        foreach (var entry in _down)
            if (entry.Value <= now) _down.TryRemove(entry);
        foreach (var entry in _held)
            if (entry.Value <= now) _held.TryRemove(entry);
        lock (_placesGate)
            foreach (var host in _places.Where(p => p.Value.Users == 0).Select(p => p.Key).ToList())
                _places.Remove(host);
    }

    /// <summary>
    /// One of the host's four places; for a registry's listing (<paramref name="listing"/>), then one of the twelve all
    /// listings share. Disposing it gives them back.
    /// </summary>
    public async Task<IDisposable> SlotAsync(string host, CancellationToken ct, bool listing = false)
    {
        Forget();
        Places places;
        lock (_placesGate)
        {
            if (!_places.TryGetValue(host, out var known)) _places[host] = known = new Places();
            places = known;
            places.Users++;
        }
        var placed = false;
        try
        {
            await places.Slots.WaitAsync(ct);
            placed = true;
            if (listing) await _listings.WaitAsync(ct);
            return new Slot(this, places, listing ? _listings : null);
        }
        catch
        {
            if (placed) places.Slots.Release();
            Leave(places);
            throw;
        }
    }

    private void Leave(Places places)
    {
        lock (_placesGate) places.Users--;
    }

    private sealed class Slot(HostGates gates, Places places, SemaphoreSlim? listings) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            listings?.Release();
            places.Slots.Release();
            gates.Leave(places);
        }
    }

    /// <summary>
    /// Until when a 429 holds the host: its Retry-After (seconds or a date), else x-ratelimit-reset (a Unix time, or
    /// seconds from now when it's small), else a minute; never less than 10 seconds nor more than 10 minutes.
    /// </summary>
    public static DateTimeOffset RetryAt(HttpResponseHeaders headers, DateTimeOffset now)
    {
        DateTimeOffset? until = headers.RetryAfter switch
        {
            { Delta: { } delta } => now + delta,
            { Date: { } date } => date,
            _ => Reset(headers, now),
        };
        var wait = (until ?? now + TimeSpan.FromSeconds(60)) - now;
        return now + TimeSpan.FromTicks(Math.Clamp(wait.Ticks, TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromMinutes(10).Ticks));
    }

    /// <summary>
    /// After an answer that says only a few requests are left (x-ratelimit-remaining of 3 or less): until x-ratelimit-reset,
    /// at most 2 minutes (a minute when it doesn't say). Null when there are more left, or it doesn't say.
    /// </summary>
    public static DateTimeOffset? LowUntil(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (First(headers, "x-ratelimit-remaining") is not { } remaining || remaining > 3) return null;
        var until = Reset(headers, now) ?? now + TimeSpan.FromMinutes(1);
        if (until <= now) return null;
        return until - now > TimeSpan.FromMinutes(2) ? now + TimeSpan.FromMinutes(2) : until;
    }

    // hub.docker.com says when as a Unix time; index.docker.io as seconds from now ("41").
    private static DateTimeOffset? Reset(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (First(headers, "x-ratelimit-reset") is not { } value) return null;
        return value > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(value, 253_402_300_799)) : now + TimeSpan.FromSeconds(value);
    }

    // The first number of a header ("176", or "3000, 3000;w=60").
    private static long? First(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var text = values.FirstOrDefault()?.Trim() ?? "";
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end > 0 && long.TryParse(text.AsSpan(0, Math.Min(end, 18)), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// The body, decompressed (when the handler hasn't already), through a copy that stops past <paramref name="max"/> bytes:
    /// null when it's bigger, whatever Content-Length said, or in an encoding Gitberth doesn't read.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, int max, CancellationToken ct)
    {
        var encodings = content.Headers.ContentEncoding.Select(e => e.Trim().ToLowerInvariant()).Where(e => e is not ("" or "identity")).ToList();
        // One coding at most (Gitberth asks for one; no server stacks them): a header listing thousands would build a
        // decoder for each before a byte is read.
        if (encodings.Count > 1) return null;
        var streams = new List<Stream> { await content.ReadAsStreamAsync(ct) };
        try
        {
            for (var i = encodings.Count - 1; i >= 0; i--)
            {
                Stream? decoded = encodings[i] switch
                {
                    "gzip" or "x-gzip" => new GZipStream(streams[^1], CompressionMode.Decompress),
                    "deflate" => new ZLibStream(streams[^1], CompressionMode.Decompress),
                    "br" => new BrotliStream(streams[^1], CompressionMode.Decompress),
                    _ => null,
                };
                if (decoded is null) return null;
                streams.Add(decoded);
            }
            var body = streams[^1];
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (copy.Length + read > max) return null;
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        }
        catch (InvalidDataException)
        {
            // Not what its encoding says it is.
            return null;
        }
        finally
        {
            for (var i = streams.Count - 1; i >= 0; i--) await streams[i].DisposeAsync();
        }
    }

    /// <summary>Gitberth and its version (Gitberth/1.8.0): the User-Agent every request for images and tags carries.</summary>
    public static ProductInfoHeaderValue UserAgent { get; } = new("Gitberth", AppVersion.Number);
}

/// <summary>
/// Answers kept for a while, by key: each until its own time, the oldest going first past <paramref name="capacity"/>, or
/// past <paramref name="maxWeight"/> in all when <paramref name="weigh"/> says what each weighs (a tag list: its tags).
/// The newest answer always stays.
/// </summary>
internal sealed class TimedCache<TValue>(TimeProvider time, int capacity, Func<TValue, int>? weigh = null, long maxWeight = long.MaxValue)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (TValue Value, DateTimeOffset Until, long Added, int Weight)> _items = new(StringComparer.Ordinal);
    private long _added;
    private long _weight;

    /// <summary>What the answers kept weigh in all (for the tests).</summary>
    internal long Weight
    {
        get
        {
            lock (_gate) return _weight;
        }
    }

    public bool TryGet(string key, out TValue value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var item) && item.Until > time.GetUtcNow())
            {
                value = item.Value;
                return true;
            }
            Remove(key);
        }
        value = default!;
        return false;
    }

    public void Set(string key, TValue value, TimeSpan keep)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Remove(key);
            var weight = weigh?.Invoke(value) ?? 0;
            _items[key] = (value, now + keep, ++_added, weight);
            _weight += weight;
            if (_items.Count <= capacity && _weight <= maxWeight) return;
            foreach (var gone in _items.Where(i => i.Value.Until <= now).Select(i => i.Key).ToList()) Remove(gone);
            foreach (var gone in _items.OrderBy(i => i.Value.Added).Select(i => i.Key).ToList())
            {
                if ((_items.Count <= capacity && _weight <= maxWeight) || gone == key) break;
                Remove(gone);
            }
        }
    }

    private void Remove(string key)
    {
        if (_items.Remove(key, out var item)) _weight -= item.Weight;
    }
}
