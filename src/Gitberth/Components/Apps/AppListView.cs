using System.Globalization;
using Gitberth.Apps;
using Gitberth.Components.Shared;
using Gitberth.Network;

namespace Gitberth.Components.Apps;

/// <summary>The Apps list's status filter: everything, what runs, what doesn't, what's in trouble, what has an update.</summary>
public enum StatusFilter { All, Running, Stopped, Attention, Updates }

/// <summary>How the Apps list is grouped: by a shared name prefix (sample-*), by source, or not at all.</summary>
public enum GroupBy { Prefix, Source, None }

/// <summary>What the Apps list is sorted by inside each group: the columns of its header.</summary>
public enum SortBy { Name, Status, Address, Source, Cpu, Memory }

/// <summary>Search across the apps: names, images, repositories, ports (8080, 5432/tcp, 9101:80) and addresses.</summary>
public static class AppSearch
{
    public static IEnumerable<AppSummary> Filter(IEnumerable<AppSummary> apps, string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 ? apps : apps.Where(a => terms.All(t => Matches(a, t)));
    }

    /// <summary>One word of a search: a port number finds host and container ports; anything else is a substring of a name, image or address.</summary>
    public static bool Matches(AppSummary app, string term)
    {
        if (int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return app.Chips.Any(c => c.HostPort == port || c.ContainerPort == port) || Contains(app.Title, term) || Contains(app.Key, term);
        return Contains(app.Title, term) || Contains(app.Key, term) || Contains(app.Image, term) || Contains(app.Repo, term)
            || app.Chips.Any(c => Contains(c.Label, term) || Contains(c.Value, term) || Contains(c.Tooltip, term));
    }

    private static bool Contains(string? text, string term) => text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// The Apps list's order inside each group and stack, by a column of its header. Words sort the way people read numbers
/// in them (sample-go-2 before sample-go-10, 10.1.1.80:9101 before 10.1.1.80:10000); apps with no figure or no address go
/// last whichever way it runs; ties go by name.
/// </summary>
public static class AppSorting
{
    /// <summary>Which way a column runs when it's first chosen: CPU and memory with the most first, the rest from A.</summary>
    public static bool StartsDescending(SortBy by) => by is SortBy.Cpu or SortBy.Memory;

    public static string WordOf(SortBy by) => by switch
    {
        SortBy.Status => "Status",
        SortBy.Address => "Address",
        SortBy.Source => "Source",
        SortBy.Cpu => "CPU",
        SortBy.Memory => "Memory",
        _ => "Name",
    };

    public static List<AppSummary> Sort(IEnumerable<AppSummary> apps, SortBy by, bool descending)
    {
        var list = apps.ToList();
        list.Sort((a, b) => Compare(a, b, by, descending));
        return list;
    }

    private static int Compare(AppSummary a, AppSummary b, SortBy by, bool descending)
    {
        var (x, y) = (Key(a, by), Key(b, by));
        var result = (x, y) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string s, string t) => (descending ? -1 : 1) * Natural(s, t),
            _ => (descending ? -1 : 1) * System.Collections.Comparer.Default.Compare(x, y),
        };
        if (result != 0) return result;
        var name = Natural(a.Title, b.Title);
        return name != 0 ? name : string.CompareOrdinal(a.Key, b.Key);
    }

    // What a column sorts on: what the row shows in it. A stopped app has no CPU or memory figure.
    private static object? Key(AppSummary app, SortBy by) => by switch
    {
        SortBy.Status => StatusRank(app),
        SortBy.Address => AppFacts.Primary(app) is { } chip ? chip.Value is null ? chip.Label : AppFacts.Shown(chip.Value) : null,
        SortBy.Source => SourceBadge.WordOf(app.Source),
        SortBy.Cpu => app.Running ? app.CpuPercent : null,
        SortBy.Memory => app.Running ? app.MemoryBytes : null,
        _ => app.Title,
    };

    // Trouble first, then what runs, then what's paused, then what's stopped.
    private static int StatusRank(AppSummary app) =>
        AppStatus.Of(app.State).Trouble ? 0 : app.Running ? 1 : app.State == AppState.Paused ? 2 : 3;

    /// <summary>Text with its runs of digits compared as numbers, letters without regard to case.</summary>
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (si, sj) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var (x, y) = (a[si..i].TrimStart('0'), b[sj..j].TrimStart('0'));
                var byNumber = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (byNumber != 0) return byNumber;
                continue;
            }
            var byChar = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>One group of the Apps list, with its stacks when it's big enough to need them.</summary>
/// <param name="Id">Stable across refreshes: none, prefix:sample, source:Git.</param>
/// <param name="Tag">What made the group: "Name prefix"; null for the rest.</param>
public sealed record AppGroup(string Id, string Title, string? Tag, string Icon, IReadOnlyList<AppSummary> Apps, IReadOnlyList<AppStack> Stacks)
{
    public int RunningCount => Apps.Count(a => a.Running);

    public int TroubleCount => Apps.Count(a => AppStatus.Of(a.State).Trouble);

    /// <summary>Not running, for whatever reason: the apps the Stopped filter shows, so the two counts agree (those in trouble are among them).</summary>
    public int StoppedCount => Apps.Count(a => AppFacts.Passes(a, StatusFilter.Stopped));

    /// <summary>The apps that are stopped on purpose, which "Start N stopped" starts (crashed ones are left to look at first).</summary>
    public IEnumerable<AppSummary> Startable => Apps.Where(a => a.State is AppState.Stopped or AppState.Created);
}

/// <summary>A stack inside a big group: Node.js, Python, Go…, with the logo of its runtime.</summary>
public sealed record AppStack(string Id, string Title, string? Slug, IReadOnlyList<AppSummary> Apps);

/// <summary>
/// The Apps list's groups. Gitberth stores no groups of its own: they're worked out from the names, so 78 sample-*
/// containers fold into one group with a stack per runtime, while the few that stand alone stay together on top.
/// </summary>
public static class AppGrouping
{
    /// <summary>A prefix needs this many apps to make a group of its own.</summary>
    public const int MinPrefixGroup = 3;

    /// <summary>A group is split into stacks once it has this many apps.</summary>
    public const int MinForStacks = 12;

    private static readonly (string Word, string Title, string? Slug)[] StackWords =
    [
        ("node", "Node.js", "nodedotjs"), ("monorepo", "Node.js", "nodedotjs"), ("python", "Python", "python"), ("go", "Go", "go"),
        ("java", "Java and Kotlin", "openjdk"), ("kotlin", "Java and Kotlin", "openjdk"), ("php", "PHP", "php"), ("deno", "Deno", "deno"),
        ("dotnet", ".NET", "dotnet"), ("ruby", "Ruby", "ruby"), ("rust", "Rust", "rust"), ("bun", "Bun", "bun"),
        ("dockerfile", "Dockerfile", "docker"), ("static", "Static", "html5"), ("index", "Static", "html5"),
    ];

    public static IReadOnlyList<AppGroup> Group(IReadOnlyList<AppSummary> apps, GroupBy by) => by switch
    {
        GroupBy.None => [new AppGroup("all", "All apps", null, MudBlazor.Icons.Material.Outlined.GridView, apps, [])],
        GroupBy.Source => apps.GroupBy(a => a.Source).OrderBy(g => g.Key)
            .Select(g => new AppGroup($"source:{g.Key}", SourceBadge.WordOf(g.Key), null, SourceBadge.IconOf(g.Key), g.ToList(), []))
            .ToList(),
        _ => ByPrefix(apps),
    };

    /// <summary>The first word of a container's name (sample of sample-node-express); null when it's one word.</summary>
    public static string? PrefixOf(string name)
    {
        var dash = name.IndexOf('-');
        return dash > 0 ? name[..dash] : null;
    }

    /// <summary>The stack a name's second word says: sample-node-express is Node.js.</summary>
    public static (string Title, string? Slug) StackOf(string name)
    {
        var words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words.Skip(1))
            foreach (var (word, title, slug) in StackWords)
                if (w == word) return (title, slug);
        return ("Other", null);
    }

    private static List<AppGroup> ByPrefix(IReadOnlyList<AppSummary> apps)
    {
        // Gitberth's own apps (gb-…) never make a prefix group: they're apps, not a family.
        var prefixes = apps.Select(a => PrefixOf(a.Container)).Where(p => p is not null and not "gb")
            .GroupBy(p => p!).Where(g => g.Count() >= MinPrefixGroup).Select(g => g.Key).ToHashSet();
        var loose = apps.Where(a => PrefixOf(a.Container) is not { } p || !prefixes.Contains(p)).ToList();
        var groups = new List<AppGroup>();
        if (loose.Count > 0)
            groups.Add(new AppGroup("none", prefixes.Count == 0 ? "All apps" : "No group", null, MudBlazor.Icons.Material.Outlined.Inbox, loose, []));
        foreach (var prefix in prefixes.OrderBy(p => p, StringComparer.Ordinal))
        {
            var members = apps.Where(a => PrefixOf(a.Container) == prefix).ToList();
            groups.Add(new AppGroup($"prefix:{prefix}", $"{prefix}-*", "Name prefix", MudBlazor.Icons.Material.Outlined.Category, members, Stacks(members)));
        }
        return groups;
    }

    private static List<AppStack> Stacks(List<AppSummary> members)
    {
        if (members.Count < MinForStacks) return [];
        var stacks = members.GroupBy(a => StackOf(a.Container))
            .Select(g => new AppStack($"stack:{g.Key.Title}", g.Key.Title, g.Key.Slug, g.ToList()))
            .OrderBy(s => s.Title == "Other" ? 1 : 0).ThenByDescending(s => s.Apps.Count).ThenBy(s => s.Title, StringComparer.Ordinal)
            .ToList();
        return stacks.Count > 1 ? stacks : [];
    }
}

/// <summary>The words and numbers the list shows about a set of apps.</summary>
public static class AppFacts
{
    public static bool Passes(AppSummary app, StatusFilter filter) => filter switch
    {
        StatusFilter.Running => app.Running,
        StatusFilter.Stopped => !app.Running,
        StatusFilter.Attention => AppStatus.Of(app.State).Trouble,
        StatusFilter.Updates => app.Update is { Available: true },
        _ => true,
    };

    /// <summary>The host ports a set of apps publishes, as a range: 9100-9175; null when none.</summary>
    public static string? HostPorts(IEnumerable<AppSummary> apps)
    {
        var ports = apps.SelectMany(a => a.Chips).Select(c => c.HostPort).OfType<int>().Distinct().Order().ToList();
        return ports.Count switch
        {
            0 => null,
            1 => ports[0].ToString(CultureInfo.InvariantCulture),
            _ => $"{ports[0].ToString(CultureInfo.InvariantCulture)}-{ports[^1].ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>The address a row shows first: the web address it opens, else the first one it can be copied from.</summary>
    public static AddressChip? Primary(AppSummary app) =>
        app.Chips.FirstOrDefault(c => c.Kind == AddressChipKind.Open) ?? app.Chips.FirstOrDefault(c => c.Kind is AddressChipKind.Copy or AddressChipKind.HostOnly);

    /// <summary>"10.1.1.80:9101" from http://10.1.1.80:9101/: what a dense list shows of a URL.</summary>
    public static string Shown(string address)
    {
        var at = address.IndexOf("://", StringComparison.Ordinal);
        return (at < 0 ? address : address[(at + 3)..]).TrimEnd('/');
    }

    /// <summary>Bytes as the list says them: 27.3 MiB, 1.4 GiB.</summary>
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (1L << 20)).ToString(bytes >= 100L << 20 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MiB",
        >= 1L << 10 => (bytes / (1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KiB",
        _ => bytes.ToString("0", CultureInfo.InvariantCulture) + " B",
    };

    public static string Percent(double value) => value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A moment as a fact line says it: 19:28 today, 5 Nov 19:28 before.</summary>
    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.ToString(local.Date == DateTime.Now.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The restart policies as every form offers them (Settings, Configure, New app's GitHub form), in this order.</summary>
    public static readonly IReadOnlyList<SegOption<string>> RestartOptions =
        [new("no", "Never"), new("on-failure", "On failure"), new("unless-stopped", "Unless stopped"), new("always", "Always")];

    /// <summary>The health check's choices as every form offers them: the image's own, a command of its own, or none.</summary>
    public static readonly IReadOnlyList<SegOption<HealthMode>> HealthOptions =
        [new(HealthMode.Image, "Image default"), new(HealthMode.Custom, "Command"), new(HealthMode.Disabled, "None")];

    /// <summary>Docker's restart policy in words: Always, Unless stopped, On failure, Never.</summary>
    public static string RestartWords(string? policy) => policy switch
    {
        "always" => "Always",
        "unless-stopped" => "Unless stopped",
        "on-failure" => "On failure",
        null or "" => "–",
        _ => "Never",
    };

    /// <summary>"sample-go-chi, sample-go-fiber and 2 more".</summary>
    public static string Names(IReadOnlyList<string> names, int shown = 3) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ when names.Count <= shown => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(shown)) + $" and {names.Count - shown} more",
    };
}
