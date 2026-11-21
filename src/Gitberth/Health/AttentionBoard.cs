namespace Gitberth.Health;

/// <summary>How much an item on the attention board matters: its banner's tone, its place (worst first), and whether agents hear of it.</summary>
public enum AttentionLevel { Info, Warn, Critical }

/// <summary>A button on an item's banner: a page of Gitberth's, or another site's.</summary>
/// <param name="Label">Its words: "Open Settings", "Make a new token".</param>
/// <param name="Href">
/// Where it goes: a page of Gitberth's by its relative address (settings#disk), or another site's https:// address, which
/// opens in a new tab.
/// </param>
/// <param name="Tip">Its tip, by its key in Help/Tips. Pass it by name (Tip: "…"), so the tips' check sees the key is used.</param>
public sealed record AttentionAction(string Label, string Href, string? Tip = null)
{
    /// <summary>Another site's page: it opens in a new tab.</summary>
    public bool External => Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || Href.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Something that needs someone's attention for as long as it's true, kept on the <see cref="AttentionBoard"/> by the job
/// that knows about it (Docker's disk getting full, an app Gitberth stopped bringing back, a token about to expire). The
/// dashboard shows it as a banner, the top bar as a pill when it asks for one (<see cref="Pill"/>), and get_health lists it
/// among its problems from <see cref="AttentionLevel.Warn"/> up.
/// </summary>
/// <param name="Key">Its name on the board, one no other job uses ("disk", "credential:github"): setting an item replaces the one with its key.</param>
/// <param name="Source">The job that keeps it ("disk", "keep-running", "credentials"), which <see cref="AttentionBoard.Replace"/> goes by.</param>
/// <param name="Level">How much it matters.</param>
/// <param name="Title">The banner's title, one line without a full stop: "Docker's disk is getting low".</param>
/// <param name="Text">A sentence or two under it: what it means, and what to do.</param>
/// <param name="Href">Where it's dealt with (settings#disk): the pill leads there.</param>
/// <param name="Tip">The banner's tip, by its key in Help/Tips. Pass it by name (Tip: "…"), so the tips' check sees the key is used.</param>
/// <param name="Actions">The banner's buttons, at most <see cref="AttentionBoard.MaxActions"/>.</param>
/// <param name="Dismiss">
/// Null when it can't be dismissed. Otherwise a stamp of what it says now (the threshold it's at, a token's stamp): once a
/// person dismissed it, its banner stays hidden while it's set with that stamp, and shows again as soon as it's set with
/// another one.
/// </param>
public sealed record AttentionItem(string Key, string Source, AttentionLevel Level, string Title, string Text, string? Href = null,
    string? Tip = null, IReadOnlyList<AttentionAction>? Actions = null, string? Dismiss = null)
{
    /// <summary>
    /// The top bar's pill in a few words ("GitHub token refused"), on every page while the item is set: only for what needs
    /// someone now. Null for none.
    /// </summary>
    public string? Pill { get; init; }

    /// <summary>The line get_health gives agents among its problems (from Warn up); its title and its text when null.</summary>
    public string? Problem { get; init; }

    // A job that looks every minute sets the same item again and again: that's no change, so the buttons count by what they
    // say rather than as one list, and nothing is raised for it.
    public bool Equals(AttentionItem? other) =>
        other is not null && Key == other.Key && Source == other.Source && Level == other.Level && Title == other.Title && Text == other.Text
        && Href == other.Href && Tip == other.Tip && Dismiss == other.Dismiss && Pill == other.Pill && Problem == other.Problem
        && (Actions ?? []).SequenceEqual(other.Actions ?? []);

    public override int GetHashCode() => HashCode.Combine(Key, Level, Title, Text, Dismiss, Pill);
}

/// <summary>
/// What needs someone's attention now, in memory: each job sets its items while they're true and clears them once they
/// aren't (<see cref="Set"/>, <see cref="Clear"/>, <see cref="Replace"/>), and open pages follow <see cref="Changed"/>.
/// Three places show them: the dashboard's banners (AttentionBanners), the top bar's pill (AttentionPill) and get_health's
/// problems (<see cref="Problems"/>). A person may dismiss an item that allows it; that holds while the item says the same
/// thing. A job that wants a dismissal to outlast a restart saves it on <see cref="Dismissed"/> and gives it back with
/// <see cref="KeepDismissed"/>. Safe from any thread.
/// </summary>
public sealed class AttentionBoard(ILogger<AttentionBoard>? logger = null)
{
    /// <summary>At most this many buttons on a banner.</summary>
    public const int MaxActions = 2;

    private readonly Lock _gate = new();
    // Each item by its key, with the order it was first set in: an item set again keeps its place among equal levels.
    private readonly Dictionary<string, (AttentionItem Item, long Order)> _items = new(StringComparer.Ordinal);
    // A dismissal: the item's key, and the stamp the item had when it was dismissed.
    private readonly Dictionary<string, string> _dismissed = new(StringComparer.Ordinal);
    private long _next;
    // What the pages read, made again under the lock after each change.
    private volatile Snapshot _now = Snapshot.Empty;

    private sealed record Snapshot(IReadOnlyList<AttentionItem> Items, IReadOnlyList<AttentionItem> Shown)
    {
        public static readonly Snapshot Empty = new([], []);
    }

    /// <summary>Raised after anything on the board changed (outside its lock), for open pages.</summary>
    public event Action? Changed;

    /// <summary>Raised when a person dismissed an item, with the item: a job that keeps dismissals across restarts saves its stamp.</summary>
    public event Action<AttentionItem>? Dismissed;

    /// <summary>Every item, worst first, then in the order they were first set; dismissed ones too.</summary>
    public IReadOnlyList<AttentionItem> Items => _now.Items;

    /// <summary>The items no one dismissed, worst first: the dashboard's banners.</summary>
    public IReadOnlyList<AttentionItem> Shown => _now.Shown;

    /// <summary>The items no one dismissed that ask for the top bar's pill, worst first.</summary>
    public IReadOnlyList<AttentionItem> Pills => [.. _now.Shown.Where(i => i.Pill is not null)];

    /// <summary>
    /// What get_health tells agents: every item from Warn up, dismissed or not (a dismissal hides a banner, it mends
    /// nothing), as its <see cref="AttentionItem.Problem"/> line, else its title and its text.
    /// </summary>
    public IReadOnlyList<string> Problems =>
        [.. _now.Items.Where(i => i.Level >= AttentionLevel.Warn).Select(i => i.Problem ?? $"{i.Title}: {i.Text}")];

    /// <summary>Whether a person dismissed the item with this key, as it is now.</summary>
    public bool IsDismissed(string key)
    {
        lock (_gate) return DismissedNow(key);
    }

    /// <summary>Adds the item, or replaces the one with its key. Setting it again unchanged changes nothing.</summary>
    /// <exception cref="ArgumentException">It has no key, source or title, or more than <see cref="MaxActions"/> buttons.</exception>
    public void Set(AttentionItem item)
    {
        Check(item);
        lock (_gate)
        {
            if (!Put(item)) return;
            Rebuild();
        }
        RaiseChanged();
    }

    /// <summary>Takes the item off the board, its dismissal with it: if it comes back, it shows. False when there was none.</summary>
    public bool Clear(string key)
    {
        lock (_gate)
        {
            _dismissed.Remove(key);
            if (!_items.Remove(key)) return false;
            Rebuild();
        }
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The source's items become exactly these, in one change: the others it had go (with their dismissals). For a job that
    /// works all its items out again each time, one per token or one per app.
    /// </summary>
    public void Replace(string source, IEnumerable<AttentionItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var list = items.ToList();
        foreach (var item in list)
        {
            Check(item);
            if (item.Source != source) throw new ArgumentException($"{item.Key} is kept by {item.Source}, not {source}.", nameof(items));
        }
        var keys = list.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var gone = _items.Where(e => e.Value.Item.Source == source && !keys.Contains(e.Key)).Select(e => e.Key).ToList();
            foreach (var key in gone)
            {
                _items.Remove(key);
                _dismissed.Remove(key);
            }
            var changed = gone.Count > 0;
            foreach (var item in list) changed |= Put(item);
            if (!changed) return;
            Rebuild();
        }
        RaiseChanged();
    }

    /// <summary>
    /// A person dismissed the item: its banner (and its pill) stay hidden while it's set with the stamp it has now. False
    /// when there's no such item or it can't be dismissed.
    /// </summary>
    public bool Dismiss(string key)
    {
        AttentionItem item;
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var entry) || entry.Item.Dismiss is not { } stamp) return false;
            if (DismissedNow(key)) return true;
            _dismissed[key] = stamp;
            item = entry.Item;
            Rebuild();
        }
        foreach (var handler in Dismissed?.GetInvocationList() ?? [])
        {
            try { ((Action<AttentionItem>)handler)(item); }
            catch (Exception ex) { logger?.LogWarning(ex, "Keeping the dismissal of {Key} failed", key); }
        }
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// A dismissal a job kept across a restart, given back: the item stays hidden while it's set with this stamp. Before the
    /// item is set is fine; a stamp that isn't what the item says now is let go.
    /// </summary>
    public void KeepDismissed(string key, string stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var entry) && entry.Item.Dismiss != stamp) return;
            if (_dismissed.TryGetValue(key, out var was) && was == stamp) return;
            _dismissed[key] = stamp;
            if (!_items.ContainsKey(key)) return;
            Rebuild();
        }
        RaiseChanged();
    }

    private static void Check(AttentionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Title))
            throw new ArgumentException("An attention item needs a key, a source and a title.", nameof(item));
        if (item.Actions is { Count: > MaxActions })
            throw new ArgumentException($"A banner has at most {MaxActions} buttons; {item.Key} has {item.Actions.Count}.", nameof(item));
    }

    // Under the lock: true when the board changed.
    private bool Put(AttentionItem item)
    {
        // A dismissal holds while the item says what it said then: set with another stamp (its next threshold, a new
        // token) it shows again, and stays shown.
        if (_dismissed.TryGetValue(item.Key, out var was) && was != item.Dismiss) _dismissed.Remove(item.Key);
        if (_items.TryGetValue(item.Key, out var entry))
        {
            if (entry.Item.Equals(item)) return false;
            _items[item.Key] = (item, entry.Order);
            return true;
        }
        _items[item.Key] = (item, _next++);
        return true;
    }

    // Under the lock.
    private bool DismissedNow(string key) =>
        _items.TryGetValue(key, out var entry) && entry.Item.Dismiss is { } stamp && _dismissed.TryGetValue(key, out var was) && was == stamp;

    // Under the lock.
    private void Rebuild()
    {
        var all = _items.Values.OrderByDescending(e => e.Item.Level).ThenBy(e => e.Order).Select(e => e.Item).ToList();
        _now = new Snapshot(all, [.. all.Where(i => !DismissedNow(i.Key))]);
    }

    private void RaiseChanged()
    {
        // A page whose connection just dropped mustn't stop the job that set the item.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { logger?.LogDebug(ex, "A page failed to follow the attention board"); }
        }
    }
}
